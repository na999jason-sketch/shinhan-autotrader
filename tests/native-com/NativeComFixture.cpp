// Synthetic, registration-free COM event source. This is not an Indi implementation.
#include <windows.h>
#include <ocidl.h>
#include <oleauto.h>
#include <cwchar>
#include <new>

namespace {
const IID FixtureEvents = { 0x802fe718, 0xf37a, 0x4a88, { 0x90, 0x4f, 0x38, 0x9b, 0x5b, 0x59, 0x71, 0x31 } };
const DISPID DataEvent = 11;
const DISPID SystemEvent = 17;
const DISPID ShortEvent = 29;
LONG liveObjects = 0;
class Fixture;

class ConnectionPoint final : public IConnectionPoint {
    Fixture* owner_;
    IDispatch* sink_ = nullptr;
public:
    explicit ConnectionPoint(Fixture* owner) : owner_(owner) {}
    ~ConnectionPoint() { if (sink_ != nullptr) sink_->Release(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override;
    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;
    HRESULT STDMETHODCALLTYPE GetConnectionInterface(IID* value) override;
    HRESULT STDMETHODCALLTYPE GetConnectionPointContainer(IConnectionPointContainer** value) override;
    HRESULT STDMETHODCALLTYPE Advise(IUnknown* sink, DWORD* cookie) override;
    HRESULT STDMETHODCALLTYPE Unadvise(DWORD cookie) override;
    HRESULT STDMETHODCALLTYPE EnumConnections(IEnumConnections** value) override;
    HRESULT Emit(DISPID eventId, VARTYPE type, LONG value);
    LONG SinkCount() const { return sink_ == nullptr ? 0 : 1; }
};

class Fixture final : public IDispatch, public IConnectionPointContainer {
    LONG references_ = 1;
    ConnectionPoint point_;
    const DWORD ownerThread_;
    const wchar_t* buffer_ = L"initial-buffer";
public:
    Fixture() : point_(this), ownerThread_(GetCurrentThreadId()) { InterlockedIncrement(&liveObjects); }
    ~Fixture() { InterlockedDecrement(&liveObjects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (value == nullptr) return E_POINTER;
        *value = nullptr;
        if (iid == IID_IUnknown || iid == IID_IDispatch) *value = static_cast<IDispatch*>(this);
        else if (iid == IID_IConnectionPointContainer) *value = static_cast<IConnectionPointContainer*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references_)); }
    ULONG STDMETHODCALLTYPE Release() override {
        const LONG remaining = InterlockedDecrement(&references_);
        if (remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }
    HRESULT STDMETHODCALLTYPE GetTypeInfoCount(UINT* count) override {
        if (count == nullptr) return E_POINTER;
        *count = 0; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetTypeInfo(UINT index, LCID lcid, ITypeInfo** value) override {
        (void)index; (void)lcid;
        if (value != nullptr) *value = nullptr;
        return DISP_E_BADINDEX;
    }
    HRESULT STDMETHODCALLTYPE GetIDsOfNames(REFIID iid, LPOLESTR* names, UINT count, LCID lcid, DISPID* ids) override {
        (void)lcid;
        if (iid != IID_NULL) return DISP_E_UNKNOWNINTERFACE;
        if (names == nullptr || ids == nullptr) return E_POINTER;
        if (count != 1) return DISP_E_UNKNOWNNAME;
        const wchar_t* const methods[] = { L"RequestData", L"EmitData", L"EmitSystem", L"EmitShort", L"GetSinkCount", L"GetBufferText" };
        for (UINT i = 0; i < ARRAYSIZE(methods); ++i) {
            if (_wcsicmp(names[0], methods[i]) == 0) { ids[0] = static_cast<DISPID>(i + 1); return S_OK; }
        }
        ids[0] = DISPID_UNKNOWN; return DISP_E_UNKNOWNNAME;
    }
    HRESULT STDMETHODCALLTYPE Invoke(DISPID id, REFIID iid, LCID lcid, WORD flags, DISPPARAMS* args,
        VARIANT* result, EXCEPINFO* error, UINT* argumentError) override {
        (void)lcid; (void)flags; (void)error; (void)argumentError;
        if (iid != IID_NULL) return DISP_E_UNKNOWNINTERFACE;
        if (GetCurrentThreadId() != ownerThread_) return RPC_E_WRONG_THREAD;
        if (result != nullptr) VariantInit(result);
        if (args == nullptr) return E_POINTER;
        if (id == 1) {
            if (args->cArgs != 0) return DISP_E_BADPARAMCOUNT;
            buffer_ = L"copied-during-native-callback";
            const HRESULT emitted = point_.Emit(DataEvent, VT_I4, 73);
            if (FAILED(emitted)) return emitted;
            buffer_ = L"changed-after-native-callback";
            if (result != nullptr) { result->vt = VT_I4; result->lVal = 73; }
            return S_OK;
        }
        if (id >= 2 && id <= 4) {
            if (args->cArgs != 1) return DISP_E_BADPARAMCOUNT;
            VARIANT number; VariantInit(&number);
            const HRESULT converted = VariantChangeType(&number, &args->rgvarg[0], 0, VT_I4);
            if (FAILED(converted)) return DISP_E_TYPEMISMATCH;
            const LONG value = number.lVal; VariantClear(&number);
            return point_.Emit(id == 2 ? DataEvent : (id == 3 ? SystemEvent : ShortEvent), static_cast<VARTYPE>(id == 4 ? VT_I2 : (id == 3 ? VT_INT : VT_I4)), value);
        }
        if (id == 5) {
            if (args->cArgs != 0) return DISP_E_BADPARAMCOUNT;
            if (result != nullptr) { result->vt = VT_I4; result->lVal = point_.SinkCount(); }
            return S_OK;
        }
        if (id == 6) {
            if (args->cArgs != 0) return DISP_E_BADPARAMCOUNT;
            if (result != nullptr) {
                result->vt = VT_BSTR; result->bstrVal = SysAllocString(buffer_);
                if (result->bstrVal == nullptr) return E_OUTOFMEMORY;
            }
            return S_OK;
        }
        return DISP_E_MEMBERNOTFOUND;
    }
    HRESULT STDMETHODCALLTYPE EnumConnectionPoints(IEnumConnectionPoints** value) override {
        if (value != nullptr) *value = nullptr;
        return E_NOTIMPL;
    }
    HRESULT STDMETHODCALLTYPE FindConnectionPoint(REFIID iid, IConnectionPoint** value) override {
        if (value == nullptr) return E_POINTER;
        *value = nullptr;
        if (iid != FixtureEvents) return CONNECT_E_NOCONNECTION;
        *value = &point_; point_.AddRef(); return S_OK;
    }
};

HRESULT STDMETHODCALLTYPE ConnectionPoint::QueryInterface(REFIID iid, void** value) {
    if (value == nullptr) return E_POINTER;
    *value = nullptr;
    if (iid != IID_IUnknown && iid != IID_IConnectionPoint) return E_NOINTERFACE;
    *value = static_cast<IConnectionPoint*>(this); AddRef(); return S_OK;
}
ULONG STDMETHODCALLTYPE ConnectionPoint::AddRef() { return owner_->AddRef(); }
ULONG STDMETHODCALLTYPE ConnectionPoint::Release() { return owner_->Release(); }
HRESULT STDMETHODCALLTYPE ConnectionPoint::GetConnectionInterface(IID* value) {
    if (value == nullptr) return E_POINTER;
    *value = FixtureEvents; return S_OK;
}
HRESULT STDMETHODCALLTYPE ConnectionPoint::GetConnectionPointContainer(IConnectionPointContainer** value) {
    return owner_->QueryInterface(IID_IConnectionPointContainer, reinterpret_cast<void**>(value));
}
HRESULT STDMETHODCALLTYPE ConnectionPoint::Advise(IUnknown* sink, DWORD* cookie) {
    if (sink == nullptr || cookie == nullptr) return E_POINTER;
    *cookie = 0;
    if (sink_ != nullptr) return CONNECT_E_ADVISELIMIT;
    const HRESULT query = sink->QueryInterface(FixtureEvents, reinterpret_cast<void**>(&sink_));
    if (FAILED(query)) { sink_ = nullptr; return CONNECT_E_CANNOTCONNECT; }
    *cookie = 1; return S_OK;
}
HRESULT STDMETHODCALLTYPE ConnectionPoint::Unadvise(DWORD cookie) {
    if (cookie != 1 || sink_ == nullptr) return CONNECT_E_NOCONNECTION;
    IDispatch* removed = sink_; sink_ = nullptr; removed->Release(); return S_OK;
}
HRESULT STDMETHODCALLTYPE ConnectionPoint::EnumConnections(IEnumConnections** value) {
    if (value != nullptr) *value = nullptr;
    return E_NOTIMPL;
}
HRESULT ConnectionPoint::Emit(DISPID eventId, VARTYPE type, LONG value) {
    if (sink_ == nullptr) return S_OK;
    // Keep the callback alive if it removes its own connection while Invoke is running.
    IDispatch* callback = sink_; callback->AddRef();
    VARIANT argument; VariantInit(&argument); argument.vt = type;
    if (type == VT_I2) argument.iVal = static_cast<SHORT>(value); else argument.lVal = value;
    DISPPARAMS parameters = { &argument, nullptr, 1, 0 };
    const HRESULT result = callback->Invoke(eventId, IID_NULL, LOCALE_INVARIANT, DISPATCH_METHOD,
        &parameters, nullptr, nullptr, nullptr);
    callback->Release(); return result;
}
} // namespace

extern "C" HRESULT __cdecl CreateFixture(IUnknown** value) {
    if (value == nullptr) return E_POINTER;
    *value = nullptr;
    Fixture* fixture = new (std::nothrow) Fixture();
    if (fixture == nullptr) return E_OUTOFMEMORY;
    *value = static_cast<IDispatch*>(fixture); return S_OK;
}
extern "C" LONG __cdecl GetLiveFixtureCount() { return liveObjects; }
