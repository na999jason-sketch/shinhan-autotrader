// Synthetic, registration-free COM event source. This is not an Indi implementation.
#include <windows.h>
#include <ocidl.h>
#include <olectl.h>
#include <oleauto.h>
#include <cwchar>
#include <new>
#include <string>

namespace {
const IID FixtureEvents = { 0x802fe718, 0xf37a, 0x4a88, { 0x90, 0x4f, 0x38, 0x9b, 0x5b, 0x59, 0x71, 0x31 } };
const IID TypedDispatchInterface = { 0xb4a0d92f, 0x6683, 0x4824, { 0x9c, 0x63, 0x53, 0xf7, 0xda, 0xd7, 0xf5, 0x7c } };
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

// A separate, strict automation target checks the production typed command adapter.
// It deliberately requires the VARIANT/Int16 widths declared by the matching typelib.
// It does not parse market data or emulate an Indi server.
class TypedDispatchFixture final : public IDispatch {
    LONG references_ = 1;
    const DWORD ownerThread_ = GetCurrentThreadId();
    LCID lastLcid_ = 0;
    DISPID lastId_ = 0;
    VARTYPE firstType_ = VT_EMPTY, secondType_ = VT_EMPTY;
    SHORT index_ = 0, row_ = 0;
    LONG calls_ = 0, initializations_ = 0, blankCredentials_ = 0;
    std::wstring query_, starter_;
    static HRESULT Number(VARIANT* result, LONG value, VARTYPE type = VT_I4) {
        if(result != nullptr) {
            result->vt = type;
            if(type == VT_I2) result->iVal = static_cast<SHORT>(value);
            else if(type == VT_BOOL) result->boolVal = static_cast<VARIANT_BOOL>(value == 0 ? VARIANT_FALSE : VARIANT_TRUE);
            else result->lVal = value;
        }
        return S_OK;
    }
    static HRESULT Text(VARIANT* result, const wchar_t* value) {
        if(result != nullptr) {
            result->vt = VT_BSTR; result->bstrVal = SysAllocString(value);
            if(result->bstrVal == nullptr) return E_OUTOFMEMORY;
        }
        return S_OK;
    }
public:
    TypedDispatchFixture() { InterlockedIncrement(&liveObjects); }
    ~TypedDispatchFixture() { InterlockedDecrement(&liveObjects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if(value == nullptr) return E_POINTER;
        *value = nullptr;
        if(iid != IID_IUnknown && iid != IID_IDispatch && iid != TypedDispatchInterface) return E_NOINTERFACE;
        *value = static_cast<IDispatch*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return static_cast<ULONG>(InterlockedIncrement(&references_)); }
    ULONG STDMETHODCALLTYPE Release() override {
        const LONG remaining = InterlockedDecrement(&references_);
        if(remaining == 0) delete this;
        return static_cast<ULONG>(remaining);
    }
    HRESULT STDMETHODCALLTYPE GetTypeInfoCount(UINT* count) override {
        if(count == nullptr) return E_POINTER;
        *count = 0; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetTypeInfo(UINT index, LCID lcid, ITypeInfo** value) override {
        (void)index; (void)lcid;
        if(value != nullptr) *value = nullptr;
        return DISP_E_BADINDEX;
    }
    HRESULT STDMETHODCALLTYPE GetIDsOfNames(REFIID iid, LPOLESTR* names, UINT count, LCID lcid, DISPID* ids) override {
        (void)lcid;
        if(iid != IID_NULL) return DISP_E_UNKNOWNINTERFACE;
        if(names == nullptr || ids == nullptr) return E_POINTER;
        if(count != 1) return DISP_E_UNKNOWNNAME;
        const wchar_t* const methods[] = {
            L"GetLastLcid", L"GetFirstArgType", L"GetSecondArgType", L"GetLastIndex", L"GetLastRow",
            L"GetLastDispId", L"GetInitializeCount", L"GetBlankCredentialCount", L"GetLastPath", L"GetCallCount"
        };
        for(UINT i = 0; i < ARRAYSIZE(methods); ++i) {
            if(_wcsicmp(names[0], methods[i]) == 0) { ids[0] = static_cast<DISPID>(100 + i); return S_OK; }
        }
        if(_wcsicmp(names[0], L"SetQueryName") == 0) { ids[0] = 3; return S_OK; }
        if(_wcsicmp(names[0], L"GetQueryName") == 0) { ids[0] = 4; return S_OK; }
        if(_wcsicmp(names[0], L"GetSingleData") == 0) { ids[0] = 8; return S_OK; }
        ids[0] = DISPID_UNKNOWN; return DISP_E_UNKNOWNNAME;
    }
    HRESULT STDMETHODCALLTYPE Invoke(DISPID id, REFIID iid, LCID lcid, WORD flags, DISPPARAMS* args,
        VARIANT* result, EXCEPINFO* error, UINT* argumentError) override {
        (void)flags; (void)error; (void)argumentError;
        if(iid != IID_NULL) return DISP_E_UNKNOWNINTERFACE;
        if(GetCurrentThreadId() != ownerThread_) return RPC_E_WRONG_THREAD;
        if(result != nullptr) VariantInit(result);
        if(args == nullptr) return E_POINTER;
        if(id >= 100) {
            if(args->cArgs != 0) return DISP_E_BADPARAMCOUNT;
            switch(id) {
                case 100:return Number(result,static_cast<LONG>(lastLcid_));
                case 101:return Number(result,firstType_);
                case 102:return Number(result,secondType_);
                case 103:return Number(result,index_);
                case 104:return Number(result,row_);
                case 105:return Number(result,lastId_);
                case 106:return Number(result,initializations_);
                case 107:return Number(result,blankCredentials_);
                case 108:return Text(result,starter_.c_str());
                case 109:return Number(result,calls_);
                default:return DISP_E_MEMBERNOTFOUND;
            }
        }
        ++calls_; lastLcid_ = lcid; lastId_ = id;
        firstType_ = static_cast<VARTYPE>(args->cArgs > 0 ? args->rgvarg[args->cArgs - 1].vt : VT_EMPTY);
        secondType_ = static_cast<VARTYPE>(args->cArgs > 1 ? args->rgvarg[args->cArgs - 2].vt : VT_EMPTY);
        if(id == 25) {
            if(args->cArgs != 4) return DISP_E_BADPARAMCOUNT;
            for(UINT i = 0; i < 4; ++i) if(args->rgvarg[i].vt != VT_BSTR) return DISP_E_TYPEMISMATCH;
            if(SysStringLen(args->rgvarg[3].bstrVal) != 0 || SysStringLen(args->rgvarg[2].bstrVal) != 0 || SysStringLen(args->rgvarg[1].bstrVal) != 0)
                return E_INVALIDARG;
            starter_ = args->rgvarg[0].bstrVal;
            ++initializations_; blankCredentials_ += 3;
            return Number(result,1,VT_BOOL);
        }
        if(id == 3) {
            if(args->cArgs != 1) return DISP_E_BADPARAMCOUNT;
            if(firstType_ != VT_BSTR) return DISP_E_TYPEMISMATCH;
            query_ = args->rgvarg[0].bstrVal;
            return Number(result,1,VT_BOOL);
        }
        if(id == 4) {
            if(args->cArgs != 0) return DISP_E_BADPARAMCOUNT;
            return Text(result,query_.c_str());
        }
        if(id == 1) {
            if(args->cArgs != 2) return DISP_E_BADPARAMCOUNT;
            if(firstType_ != VT_I2 || secondType_ != VT_BSTR) return DISP_E_TYPEMISMATCH;
            index_ = args->rgvarg[1].iVal;
            return Number(result,1,VT_BOOL);
        }
        if(id == 8) {
            if(args->cArgs != 1) return DISP_E_BADPARAMCOUNT;
            if(firstType_ != VT_I2) return DISP_E_TYPEMISMATCH;
            index_ = args->rgvarg[0].iVal;
            return Text(result,L"005930");
        }
        if(id == 9) {
            if(args->cArgs != 2) return DISP_E_BADPARAMCOUNT;
            if(firstType_ != VT_I2 || secondType_ != VT_I2) return DISP_E_TYPEMISMATCH;
            row_ = args->rgvarg[1].iVal; index_ = args->rgvarg[0].iVal;
            return Text(result,L"70000");
        }
        if(args->cArgs != 0) return DISP_E_BADPARAMCOUNT;
        if(id == 5) return initializations_ > 0 ? Number(result,73,VT_I2) : E_UNEXPECTED;
        if(id == 13) return Number(result,1,VT_I2);
        if(id == 14 || id == 17) return Number(result,0,VT_I2);
        if(id == 15 || id == 16) return Text(result,L"");
        return DISP_E_MEMBERNOTFOUND;
    }
};
} // namespace

extern "C" HRESULT __cdecl CreateFixture(IUnknown** value) {
    if (value == nullptr) return E_POINTER;
    *value = nullptr;
    Fixture* fixture = new (std::nothrow) Fixture();
    if (fixture == nullptr) return E_OUTOFMEMORY;
    *value = static_cast<IDispatch*>(fixture); return S_OK;
}
extern "C" LONG __cdecl GetLiveFixtureCount() { return liveObjects; }
extern "C" HRESULT __cdecl CreateTypedFixture(IUnknown** value) {
    if(value == nullptr) return E_POINTER;
    *value = nullptr;
    TypedDispatchFixture* fixture = new (std::nothrow) TypedDispatchFixture();
    if(fixture == nullptr) return E_OUTOFMEMORY;
    *value = static_cast<IDispatch*>(fixture); return S_OK;
}
