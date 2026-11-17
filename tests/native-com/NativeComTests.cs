using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ShinhanNativeTrader;

// Standalone runner links the production subscription helper; no production EXE or Indi broker.
internal static class NativeComTests {
    static readonly Guid EventInterface = new Guid("802FE718-F37A-4A88-904F-389B5B597131");
    const int DataId = 11, SystemId = 17, ShortId = 29;
    static readonly List<string> Report = new List<string>();
    static int checks;
    [DllImport("NativeComFixture.dll", CallingConvention=CallingConvention.Cdecl, ExactSpelling=true)]
    static extern int CreateFixture(out IntPtr value);
    [DllImport("NativeComFixture.dll", CallingConvention=CallingConvention.Cdecl, ExactSpelling=true)]
    static extern int GetLiveFixtureCount();
    [DllImport("NativeComFixture.dll", CallingConvention=CallingConvention.Cdecl, ExactSpelling=true)]
    static extern int CreateTypedFixture(out IntPtr value);
    static void Check(bool pass, string message) {
        checks++;
        if(!pass) throw new Exception(message);
        Report.Add("PASS " + message);
    }
    static object Invoke(object source, string method, params object[] args) {
        return source.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, source, args, CultureInfo.InvariantCulture);
    }
    static int Number(object source, string method) { return Convert.ToInt32(Invoke(source,method),CultureInfo.InvariantCulture); }
    [STAThread]
    static int Main(string[] args) {
        int exit = 0;
        try { Run(); Report.Add("PASS: " + checks + " native COM assertions; pointerBytes=" + IntPtr.Size); }
        catch(Exception ex) { exit = 1; Report.Add("FAIL: " + ex); }
        Report.Add("Synthetic native COM fixture only. Indi OCX, server readiness and live orders: NOT TESTED.");
        var output = string.Join(Environment.NewLine,Report) + Environment.NewLine;
        Console.Write(output);
        if(args.Length > 0) {
            try { var path = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path,output,Encoding.UTF8); }
            catch(Exception ex) { Console.Error.WriteLine(ex); return 2; }
        }
        return exit;
    }
    static void Run() {
        Check(IntPtr.Size == 4,"test process is x86");
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,"test process owns an STA");
        Check(GetLiveFixtureCount() == 0,"native fixture starts without live objects");
        IntPtr pointer; Marshal.ThrowExceptionForHR(CreateFixture(out pointer));
        object source;
        try { source = Marshal.GetObjectForIUnknown(pointer); } finally { Marshal.Release(pointer); }
        Check(Marshal.IsComObject(source),"event source is a native COM RCW, not a managed fake");
        var subscriptions = new ComEventSubscriptions(source,EventInterface);
        try {
            Check(Number(source,"GetSinkCount") == 0,"no connection point sink before bind");
            var received = new List<int>();
            int systemValue=0, shortValue=0, callbackThread=0;
            bool insideRequest=false, reentered=false;
            string buffer=null;
            Action<int> data = value => {
                received.Add(value); callbackThread=Thread.CurrentThread.ManagedThreadId;
                if(insideRequest) { reentered=true; buffer=Convert.ToString(Invoke(source,"GetBufferText"),CultureInfo.InvariantCulture); }
            };
            subscriptions.AddInt32Callback(DataId,VarEnum.VT_I4,data);
            subscriptions.AddInt32Callback(SystemId,VarEnum.VT_INT,value => { systemValue=value; });
            subscriptions.AddInt32Callback(ShortId,VarEnum.VT_I2,value => { shortValue=value; });
            Check(subscriptions.Count == 3,"production helper roots all three exact adapted delegates");
            Check(Number(source,"GetSinkCount") == 1,"three DISPIDs share one successfully advised native sink");
            Invoke(source,"EmitData",41);
            Check(received.Count == 1 && received[0] == 41,"native IDispatch callback reaches the rooted ReceiveData delegate");
            Check(callbackThread == Thread.CurrentThread.ManagedThreadId,"native callback executes on the owning STA");
            Invoke(source,"EmitSystem",91);
            Check(systemValue == 91,"separate native system DISPID reaches its delegate");
            Invoke(source,"EmitShort",1234);
            Check(shortValue == 1234,"VT_I2 event argument reaches a short delegate");
            insideRequest=true;
            int requestId=Number(source,"RequestData");
            insideRequest=false;
            Check(requestId == 73 && reentered && received[received.Count-1] == requestId,"ReceiveData can reenter before RequestData returns a positive ID");
            Check(buffer == "copied-during-native-callback","native receive buffer can be copied during reentrant callback");
            Check(Convert.ToString(Invoke(source,"GetBufferText"),CultureInfo.InvariantCulture) == "changed-after-native-callback","buffer changes after callback, demonstrating why delayed reads lose data");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Invoke(source,"EmitData",52);
            Check(received[received.Count-1] == 52,"event subscription survives forced GC while delegates are rooted");
            subscriptions.Dispose(); subscriptions.Dispose();
            Check(subscriptions.Count == 0,"production helper clears roots and permits repeated disposal");
            int before=received.Count;
            Invoke(source,"EmitData",66); Invoke(source,"EmitSystem",92); Invoke(source,"EmitShort",42);
            Check(received.Count == before && systemValue == 91 && shortValue == 1234,"disposed production helper receives no further callbacks");
            Check(Number(source,"GetSinkCount") == 0,"production helper disposal unadvises the final native sink");
            bool disposedRejected=false;
            try { subscriptions.AddInt32Callback(DataId,VarEnum.VT_I4,data); }
            catch(ObjectDisposedException) { disposedRejected=true; }
            Check(disposedRejected,"disposed production helper rejects a new subscription");
            using(var fresh = new ComEventSubscriptions(source,EventInterface)) {
                fresh.AddInt32Callback(DataId,VarEnum.VT_INT,data);
                Invoke(source,"EmitData",74);
                Check(fresh.Count == 1 && received[received.Count-1] == 74,"new production helper can reconnect after exact detach");
            }
            Check(Number(source,"GetSinkCount") == 0,"reconnected production helper also releases its native sink");
            bool refused=false;
            using(var wrong = new ComEventSubscriptions(source,Guid.NewGuid())) {
                try { wrong.AddInt32Callback(DataId,VarEnum.VT_I4,data); }
                catch(COMException) { refused=true; }
                Check(refused && wrong.Count == 0 && Number(source,"GetSinkCount") == 0,"wrong outgoing IID is refused without rooting a failed subscription");
            }
            bool unsupported=false;
            using(var invalid = new ComEventSubscriptions(source,EventInterface)) {
                try { invalid.AddInt32Callback(DataId,VarEnum.VT_BSTR,data); }
                catch(ArgumentException) { unsupported=true; }
                Check(unsupported && invalid.Count == 0 && Number(source,"GetSinkCount") == 0,"unsupported argument type is rejected before native Advise");
            }
            object variantValue=null;
            using(var variant=new ComEventSubscriptions(source,EventInterface)) {
                variant.AddVariantCallback(31,VarEnum.VT_VARIANT,value=>variantValue=value);
                Invoke(source,"EmitVariant","SC");
                Check(variantValue is string&&(string)variantValue=="SC","native VARIANT containing BSTR reaches the exact object event delegate");
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                Invoke(source,"EmitVariant","AA");
                Check(variantValue is string&&(string)variantValue=="AA","VARIANT event subscription survives forced collection");
                Invoke(source,"EmitVariant",42);
                Check(variantValue is int&&(int)variantValue==42,"VARIANT delegate preserves scalar type for processor validation");
                variant.Dispose();Invoke(source,"EmitVariant","AD");
                Check(variantValue is int&&(int)variantValue==42&&Number(source,"GetSinkCount")==0,"exact VARIANT callback detach prevents later events");
            }
            bool wrongVariant=false;
            using(var invalidVariant=new ComEventSubscriptions(source,EventInterface)) {
                try{invalidVariant.AddVariantCallback(31,VarEnum.VT_BSTR,value=>{});}catch(ArgumentException){wrongVariant=true;}
                Check(wrongVariant&&invalidVariant.Count==0&&Number(source,"GetSinkCount")==0,"BSTR-only declaration is refused for formal VARIANT events");
            }
            var quoteAdapter=new IndiReadOnlyDispatch(source);
            int realtimeCallbacks=0, realtimeThread=0;
            object realtimeKind=null;
            var quoteFields=new Dictionary<int,string>();
            using(var realtime=new ComEventSubscriptions(source,EventInterface)) {
                realtime.AddVariantCallback(2,VarEnum.VT_VARIANT,value=>{
                    realtimeCallbacks++;realtimeKind=value;realtimeThread=Thread.CurrentThread.ManagedThreadId;
                    foreach(var index in new[]{1,2,3,7,17})
                        quoteFields[index]=(string)quoteAdapter.Invoke("GetSingleData",index);
                });
                Invoke(source,"EmitRealtime");
                Check(realtimeCallbacks==1&&realtimeKind is string&&(string)realtimeKind=="SC","actual RT DISPID2 delivers a BSTR inside its formal VARIANT callback");
                Check(realtimeThread==Thread.CurrentThread.ManagedThreadId,"RT callback and typed field getters execute synchronously on the owning STA");
                Check(quoteFields[1]=="005930"&&quoteFields[2]=="10045512"&&quoteFields[3]=="70000"&&quoteFields[7]=="12345"&&quoteFields[17]=="1","RT callback copies documented SC fields through production typed GetSingleData preserving all eight numeric exchange-clock characters");
                Check((string)quoteAdapter.Invoke("GetSingleData",2)=="00000000"&&quoteFields[2]=="10045512","later native buffer changes cannot replace the eight-digit clock already copied inside RT callback");
                GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
                Invoke(source,"EmitRealtime");
                Check(realtimeCallbacks==2&&quoteFields[3]=="70000","DISPID2 subscription and same-source quote reads survive forced collection");
                realtime.Dispose();Invoke(source,"EmitRealtime");
                Check(realtimeCallbacks==2&&Number(source,"GetSinkCount")==0,"exact RT callback detach prevents later field processing and releases its connection");
            }
        } finally {
            subscriptions.Dispose();
            Marshal.FinalReleaseComObject(source);
        }
        Check(GetLiveFixtureCount() == 0,"native fixture releases after exact delegate detach and RCW disposal");
        RunTypedDispatch();
    }
    static void RunTypedDispatch() {
        IntPtr pointer; Marshal.ThrowExceptionForHR(CreateTypedFixture(out pointer));
        object source;
        try { source=Marshal.GetObjectForIUnknown(pointer); } finally { Marshal.Release(pointer); }
        var directory=Path.Combine(Path.GetTempPath(),"IndiDispatchFixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var starter=Path.Combine(directory,"giexpertstarter.exe");
        // An empty marker exercises marshaling only; no executable is launched by the fixture.
        File.WriteAllText(starter,"");
        var priorCulture=CultureInfo.CurrentCulture;
        try {
            var adapter=new IndiReadOnlyDispatch(source);
            Check(Marshal.IsComObject(source),"typed command adapter obtains its exact dispatch IID from a native RCW");
            bool invalidPath=false;
            try { adapter.Initialize("giexpertstarter.exe"); } catch(ArgumentException) { invalidPath=true; }
            Check(invalidPath&&Number(source,"GetInitializeCount")==0,"relative starter paths are rejected before invoking COM");
            Check(adapter.Initialize(starter),"explicit initialization uses the typed StartIndi DISPID");
            Check(Number(source,"GetInitializeCount")==1&&Number(source,"GetBlankCredentialCount")==3,"StartIndi receives three empty VARIANT BSTR credentials");
            Check(Convert.ToString(Invoke(source,"GetLastPath"),CultureInfo.InvariantCulture)==starter,"initialization forwards the full selected starter path");
            Check(Number(source,"GetLastDispId")==25&&Number(source,"GetFirstArgType")==8&&Number(source,"GetSecondArgType")==8,"initialization preserves its DISPID and VARIANT string argument types");
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ko-KR");
            Check((bool)adapter.Invoke("SetQueryName","stock_mst"),"typed SetQueryName returns VARIANT_BOOL correctly");
            Check(Number(source,"GetFirstArgType")==8,"typed query name is a VARIANT BSTR");
            Check((string)adapter.Invoke("GetQueryName")=="stock_mst","typed query readback preserves the selected query");
            int typedLcid=Number(source,"GetLastLcid");
            Check(typedLcid!=CultureInfo.InvariantCulture.LCID,"typed dispatch does not force the reflection invariant-culture LCID");
            Invoke(source,"GetQueryName");
            Check(Number(source,"GetLastLcid")==CultureInfo.InvariantCulture.LCID,"reflection path demonstrably supplied invariant LCID127");
            Check((bool)adapter.Invoke("SetSingleData",0,"005930"),"typed single input accepts an integer field index and string value");
            Check(Number(source,"GetFirstArgType")==2&&Number(source,"GetSecondArgType")==8,"single input marshals index as VT_I2 and data as VARIANT BSTR");
            Check((string)adapter.Invoke("GetSingleData",1)=="005930"&&Number(source,"GetLastIndex")==1,"typed single data uses its Int16 index contract");
            Check((bool)adapter.Invoke("SetQueryName","TR_1110_11")&&Number(source,"GetFirstArgType")==8,"manual snapshot query is admitted through the production typed read-only adapter");
            Check((string)adapter.Invoke("GetQueryName")=="TR_1110_11","manual snapshot query retains its distinct native query name");
            Check((bool)adapter.Invoke("SetSingleData",0,"005930")&&Number(source,"GetLastIndex")==0,"manual snapshot forwards its sole short-code input at index zero");
            Check((string)adapter.Invoke("GetSingleData",24)=="70000"&&Number(source,"GetLastIndex")==24&&Number(source,"GetFirstArgType")==2,"manual snapshot price reads native field24 with a VT_I2 index");
            Check((string)adapter.Invoke("GetSingleData",1)=="","manual snapshot fixture does not invent the SC symbol echo");
            Check((bool)adapter.Invoke("SetQueryName","SC")&&(string)adapter.Invoke("GetQueryName")=="SC","SC quote polling remains admitted separately from manual snapshots");
            foreach(var accountQuery in new[]{"SABA602Q1","SABA415Q1","SABA231Q1","SABA609Q1"})
                Check((bool)adapter.Invoke("SetQueryName",accountQuery)&&(string)adapter.Invoke("GetQueryName")==accountQuery,
                    "actual account cash, capacity and order snapshots pass only the explicit read-only adapter: "+accountQuery);
            Check((string)adapter.Invoke("GetMultiData",2,5)=="70000","typed multi data returns a VARIANT value");
            Check(Number(source,"GetFirstArgType")==2&&Number(source,"GetSecondArgType")==2&&Number(source,"GetLastRow")==2&&Number(source,"GetLastIndex")==5,"multi data preserves reversed COM arguments and both VT_I2 widths");
            object id=adapter.Invoke("RequestData");
            Check(id is short&&(short)id==73,"RequestData remains boxed Int16 instead of converting null or bool");
            Check(adapter.Invoke("GetMultiRowCount") is short&&(short)adapter.Invoke("GetMultiRowCount")==1,"row count follows the Int16 return contract");
            Check((short)adapter.Invoke("GetCommState")==0&&(short)adapter.Invoke("GetErrorState")==0,"typed diagnostic state getters preserve zero values");
            Check((string)adapter.Invoke("GetErrorCode")==""&&(string)adapter.Invoke("GetErrorMessage")=="","typed diagnostic variant getters preserve empty values");
            int before=Number(source,"GetCallCount");
            bool refused=false;
            try { adapter.Invoke("SetQueryName","SABA101U1"); } catch(InvalidOperationException) { refused=true; }
            Check(refused&&Number(source,"GetCallCount")==before,"order query is rejected before native dispatch");
            foreach(var orderQuery in new[]{"SABA102U1","SCDA601U2"}) {
                refused=false;
                try { adapter.Invoke("SetQueryName",orderQuery); } catch(InvalidOperationException) { refused=true; }
                Check(refused&&Number(source,"GetCallCount")==before,"additional order or cancellation query is rejected before native dispatch: "+orderQuery);
            }
            refused=false;
            try { adapter.Invoke("StartIndi","id","password","certificate",starter); } catch(InvalidOperationException) { refused=true; }
            Check(refused&&Number(source,"GetCallCount")==before,"generic invocation cannot supply login credentials");
            refused=false;
            try { adapter.Invoke("GetSingleData",40000); } catch(ArgumentException) { refused=true; }
            Check(refused&&Number(source,"GetCallCount")==before,"overflowing indices are rejected before COM");
            refused=false;
            try { adapter.Invoke("GetSingleData","1"); } catch(ArgumentException) { refused=true; }
            Check(refused&&Number(source,"GetCallCount")==before,"string indices are rejected instead of silently converting to Int16");
            refused=false;
            try { adapter.Invoke("GetSingleData",false); } catch(ArgumentException) { refused=true; }
            Check(refused&&Number(source,"GetCallCount")==before,"bool indices are rejected instead of silently becoming zero");
        } finally {
            CultureInfo.CurrentCulture=priorCulture;
            Marshal.FinalReleaseComObject(source);
            Directory.Delete(directory,true);
        }
        Check(GetLiveFixtureCount()==0,"typed native command fixture is released without invoking a broker");
    }
}
