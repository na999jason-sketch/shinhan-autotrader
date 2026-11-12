using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.RegularExpressions;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace ShinhanNativeTrader {
    [ComImport, Guid("B196B283-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IProvideClassInfo { void GetClassInfo(out ComTypes.ITypeInfo typeInfo); }

    // AxHost provides the ActiveX site. It must live on the UI STA with a running message pump.
    public sealed class IndiHost : AxHost {
        readonly Action<object> attachInterfaces, createSink;
        readonly Action detachSink;
        public IndiHost(string progId,Action<object> attachInterfaces,Action<object> createSink,Action detachSink)
            : base(Type.GetTypeFromProgID(progId, true).GUID.ToString("B")) {
            this.attachInterfaces=attachInterfaces;this.createSink=createSink;this.detachSink=detachSink;
            Size=new System.Drawing.Size(8,8);
        }
        public object ComObject { get { return GetOcx(); } }
        // Use the same activation stages as the generated AxInterop control. Attaching
        // only after CreateControl has completed misses the control's CreateSink stage.
        protected override void AttachInterfaces() {
            base.AttachInterfaces();attachInterfaces(GetOcx());
        }
        protected override void CreateSink() {
            base.CreateSink();createSink(GetOcx());
        }
        protected override void DetachSink() {
            try{detachSink();}finally{base.DetachSink();}
        }
    }

    internal sealed class ComMethodInfo {
        public int Parameters;
        public VarEnum ReturnType;
    }
    internal sealed class ComEventInfo {
        public Guid SourceInterface;
        public readonly Dictionary<string,int> DispIds=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string,VarEnum> ArgumentTypes=new Dictionary<string,VarEnum>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string,ComMethodInfo> Methods=new Dictionary<string,ComMethodInfo>(StringComparer.OrdinalIgnoreCase);
    }
    internal static class ComTypeDiscovery {
        public static ComEventInfo Discover(object com) {
            var provide=com as IProvideClassInfo;
            if(provide==null)throw new InvalidOperationException("OCX has no IProvideClassInfo; inspect installed Indi typelib.");
            ComTypes.ITypeInfo coclass;provide.GetClassInfo(out coclass);
            if(coclass==null)throw new InvalidOperationException("ActiveX class type information unavailable.");
            try {
                IntPtr ptr;coclass.GetTypeAttr(out ptr);
                ComTypes.TYPEATTR attr;
                try {attr=(ComTypes.TYPEATTR)Marshal.PtrToStructure(ptr,typeof(ComTypes.TYPEATTR));}
                finally {coclass.ReleaseTypeAttr(ptr);}
                ComEventInfo result=null;
                var methods=new Dictionary<string,ComMethodInfo>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i<attr.cImplTypes;i++) {
                    ComTypes.IMPLTYPEFLAGS flags;coclass.GetImplTypeFlags(i,out flags);
                    bool events=(flags & ComTypes.IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE)!=0;
                    int href;coclass.GetRefTypeOfImplType(i,out href);
                    ComTypes.ITypeInfo source;coclass.GetRefTypeInfo(href,out source);
                    try {
                        source.GetTypeAttr(out ptr);ComTypes.TYPEATTR a;
                        try {a=(ComTypes.TYPEATTR)Marshal.PtrToStructure(ptr,typeof(ComTypes.TYPEATTR));}
                        finally {source.ReleaseTypeAttr(ptr);}
                        var e=new ComEventInfo{SourceInterface=a.guid};
                        for(int j=0;j<a.cFuncs;j++) {
                            source.GetFuncDesc(j,out ptr);
                            try {
                                var f=(ComTypes.FUNCDESC)Marshal.PtrToStructure(ptr,typeof(ComTypes.FUNCDESC));
                                string name,desc,helpfile;int context;
                                source.GetDocumentation(f.memid,out name,out desc,out context,out helpfile);
                                if(string.IsNullOrWhiteSpace(name))continue;
                                if(!events) {
                                    methods[name]=new ComMethodInfo{Parameters=f.cParams,ReturnType=(VarEnum)f.elemdescFunc.tdesc.vt};
                                    continue;
                                }
                                e.DispIds[name]=f.memid;
                                if(f.cParams==1) {
                                    var arg=(ComTypes.ELEMDESC)Marshal.PtrToStructure(f.lprgelemdescParam,typeof(ComTypes.ELEMDESC));
                                    e.ArgumentTypes[name]=(VarEnum)arg.tdesc.vt;
                                }
                            } finally {source.ReleaseFuncDesc(ptr);}
                        }
                        if(e.DispIds.ContainsKey("ReceiveData"))result=e;
                    } finally {Marshal.ReleaseComObject(source);}
                }
                if(result!=null){foreach(var method in methods)result.Methods[method.Key]=method.Value;return result;}
            } finally {Marshal.ReleaseComObject(coclass);}
            throw new InvalidOperationException("No outgoing Indi interface exposing ReceiveData in installed typelib.");
        }
    }

    // This boundary can be replaced with a fake without loading a Windows ActiveX control.
    public interface IIndiTransport : IDisposable {
        event Action<int> ReceiveData;
        event Action<int> ReceiveSysMsg;
        event Action<string> Error;
        object Invoke(string method,params object[] args);
    }

    internal sealed class IndiConnectionException : Exception {
        public IndiConnectionException(string safeMessage) : base(safeMessage) {}
    }

    internal sealed class ComIndiTransport : IIndiTransport,IIndiDiagnostics {
        readonly Control surface;
        readonly int ownerThread;
        readonly Action<string> log;
        ComEventSubscriptions subscriptions;
        IndiHost host;object com;ComEventInfo source;IndiReadOnlyDispatch dispatch;bool disposed;
        public event Action<int> ReceiveData;
        public event Action<int> ReceiveSysMsg;
        public event Action<string> Error;
        public ComIndiTransport(Control surface,Action<string> log) {
            this.surface=surface;this.log=log;ownerThread=Thread.CurrentThread.ManagedThreadId;
            if(IntPtr.Size!=4)throw new InvalidOperationException("Indi requires an x86 (32 bit) process.");
            if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Indi requires the UI STA.");
            if(surface.InvokeRequired)throw new InvalidOperationException("Connect Indi on the control's UI thread.");
            try {
                host=new IndiHost("GIEXPERTCONTROL.GiExpertControlCtrl.1",AttachInterfaces,CreateSink,DetachSink);
                ((ISupportInitialize)host).BeginInit();surface.Controls.Add(host);((ISupportInitialize)host).EndInit();
                host.CreateControl();
                if(!host.IsHandleCreated)throw new InvalidOperationException("ActiveX native handle creation failed.");
                if(com==null)throw new InvalidOperationException("Indi OCX was not loaded.");
                if(subscriptions==null||subscriptions.Count!=2)throw new InvalidOperationException("AxHost did not complete its event CreateSink stage.");
                log("ActiveX site ready; handleCreated="+host.IsHandleCreated+" visible="+host.Visible+
                    "; server response NOT YET VERIFIED.");
                ComMethodInfo initialization;
                if(!source.Methods.TryGetValue("StartIndi",out initialization)||initialization.Parameters!=4||initialization.ReturnType!=VarEnum.VT_BOOL)
                    throw new IndiConnectionException("설치된 Indi의 StartIndi 초기화 서명이 지원되지 않습니다.");
                string starter=IndiInstallation.FindStarter(Type.GetTypeFromProgID("GIEXPERTCONTROL.GiExpertControlCtrl.1",true).GUID);
                if(starter==null)throw new IndiConnectionException("등록된 신한 Indi 설치 폴더에서 giexpertstarter.exe를 찾지 못했습니다.");
                // A running Helper establishes the shared service, not this newly-created
                // OCX instance. Follow the C# sample's per-control initialization call.
                bool started=dispatch.Initialize(starter);
                log("OCX StartIndi initialization invoked; result="+started+"; credentials supplied by this app: none.");
                log(IndiProcessDiagnostics.Describe());
                log(IndiDiagnosticFormatter.Format(ReadDiagnostics(),new string[0]));
            }catch{Dispose();throw;}
        }
        void AttachInterfaces(object control){com=control;source=ComTypeDiscovery.Discover(control);dispatch=new IndiReadOnlyDispatch(control);}
        void CreateSink(object control) {
            DetachSink();
            if(source==null)AttachInterfaces(control);
            subscriptions=new ComEventSubscriptions(control,source.SourceInterface);
            try {
                Bind("ReceiveData",OnData);Bind("ReceiveSysMsg",OnSys);
                log("AxHost.CreateSink outgoing IID="+source.SourceInterface+" ReceiveData DISPID="+source.DispIds["ReceiveData"]+
                    " arg="+source.ArgumentTypes["ReceiveData"]+" ReceiveSysMsg DISPID="+source.DispIds["ReceiveSysMsg"]+
                    "; native subscriptions="+subscriptions.Count+"; receipt not yet verified.");
            }catch{DetachSink();throw;}
        }
        void DetachSink() {
            if(subscriptions==null)return;
            try{subscriptions.Dispose();}catch(Exception ex){log("COM detach failed ("+ex.GetType().Name+"); ActiveX disposal follows.");}
            subscriptions=null;
        }
        void Bind(string name,Action<int> callback) {
            int dispid;VarEnum type;
            if(!source.DispIds.TryGetValue(name,out dispid)||!source.ArgumentTypes.TryGetValue(name,out type))
                throw new InvalidOperationException(name+" must have one parameter in installed typelib.");
            subscriptions.AddInt32Callback(dispid,type,callback);
        }
        void Callback(Action action) {
            if(disposed)return;
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread) {
                // The receive buffer belongs to this callback; marshalling a read to the UI later loses it.
                surface.BeginInvoke(new Action(()=>{if(Error!=null)Error("Indi callback arrived outside its owning STA; data discarded and requests halted.");}));
                return;
            }
            try {action();}catch(Exception ex){if(Error!=null)Error("Indi callback failed ("+ex.GetBaseException().GetType().Name+"); requests halted.");}
        }
        void OnData(int id){Callback(()=>{log("NATIVE ReceiveData rid="+id);if(ReceiveData!=null)ReceiveData(id);});}
        void OnSys(int id){Callback(()=>{if(ReceiveSysMsg!=null)ReceiveSysMsg(id);});}
        public object Invoke(string method,params object[] args) {
            if(disposed)throw new ObjectDisposedException("ComIndiTransport");
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Indi call must use its owning STA.");
            // This control is reserved for read-only requests, even if another caller gets this boundary.
            if(method!="SetQueryName"&&method!="GetQueryName"&&method!="SetSingleData"&&method!="RequestData"&&method!="GetSingleData"&&
                method!="GetMultiData"&&method!="GetMultiRowCount"&&!DiagnosticMethod(method))throw new InvalidOperationException("Read-only Indi method not allowed: "+method);
            if(DiagnosticMethod(method)&&(!SupportsDiagnostic(method)||args.Length!=0))throw new InvalidOperationException("Installed Indi diagnostic signature is unavailable.");
            if(method=="SetQueryName"&&(args.Length!=1||!new[]{"stock_mst","TR_SCHART","SC","SABA200QB"}.Contains(Convert.ToString(args[0]))))
                throw new InvalidOperationException("Read-only Indi query not allowed.");
            return dispatch.Invoke(method,args);
        }
        static bool DiagnosticMethod(string name) {
            return name=="GetCommState"||name=="GetErrorState"||name=="GetErrorCode"||name=="GetErrorMessage";
        }
        bool SupportsDiagnostic(string name) {
            ComMethodInfo method;
            if(source==null||!source.Methods.TryGetValue(name,out method)||method.Parameters!=0)return false;
            if(name=="GetCommState"||name=="GetErrorState")return method.ReturnType==VarEnum.VT_I2||method.ReturnType==VarEnum.VT_I4||method.ReturnType==VarEnum.VT_INT;
            return method.ReturnType==VarEnum.VT_VARIANT||method.ReturnType==VarEnum.VT_BSTR||method.ReturnType==VarEnum.VT_I2||method.ReturnType==VarEnum.VT_I4;
        }
        public IndiDiagnosticSnapshot ReadDiagnostics() {
            var result=new IndiDiagnosticSnapshot{NativeError=dispatch.LastRequestNativeError};var unavailable=new List<string>();
            // Read error state before other calls can alter it. Only installed zero-argument
            // diagnostic getters are callable; no guessed APIs or session-changing calls.
            foreach(var name in new[]{"GetErrorState","GetErrorCode","GetErrorMessage","GetCommState"}) {
                if(!SupportsDiagnostic(name)){unavailable.Add(name+":not-exported");continue;}
                try {
                    object value=Invoke(name);
                    if(name=="GetCommState"||name=="GetErrorState") {
                        if(!(value is short)&&!(value is int))throw new InvalidOperationException("Unexpected diagnostic state type.");
                        int number=Convert.ToInt32(value,CultureInfo.InvariantCulture);
                        if(name=="GetCommState")result.CommState=number;else result.ErrorState=number;
                    } else if(name=="GetErrorCode")result.ErrorCode=Convert.ToString(value,CultureInfo.InvariantCulture);
                    else result.Message=Convert.ToString(value,CultureInfo.InvariantCulture);
                }catch(Exception ex){unavailable.Add(name+":"+ex.GetBaseException().GetType().Name);}
            }
            result.Unavailable=string.Join(",",unavailable);return result;
        }
        public void Dispose() {
            if(disposed)return;disposed=true;
            DetachSink();
            if(host!=null){host.Dispose();host=null;}
            // AxHost owns its RCW; do not FinalReleaseComObject behind the ActiveX site.
            com=null;dispatch=null;
        }
    }

    public sealed class IndiReply {
        public string Kind,Code;
        public int Count;
        public List<Stock> Stocks=new List<Stock>();
        public List<Bar> Bars=new List<Bar>();
        public List<Holding> Holdings=new List<Holding>();
        public decimal Price;
        public string Clock,Message,OrderNo;
    }

    public static class IndiResponseParser {
        static readonly Regex Number=new Regex(@"^[+-]?(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?$",RegexOptions.CultureInvariant);
        public static decimal RequiredNumber(object raw,string label,bool signed=false,bool optional=false) {
            var text=(Convert.ToString(raw,CultureInfo.InvariantCulture)??"").Trim();
            if(optional&&text=="")return 0;
            decimal value;
            if(!Number.IsMatch(text)||!decimal.TryParse(text.Replace(",",""),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,out value)||(!signed&&value<0))throw new InvalidOperationException("Invalid Indi numeric field: "+label);
            return value;
        }
        static int Rows(IIndiTransport io,int limit) {
            var count=RequiredNumber(io.Invoke("GetMultiRowCount"),"row count");
            if(count!=decimal.Truncate(count)||count>limit)throw new InvalidOperationException("Indi row count outside supported range.");
            return (int)count;
        }
        public static IndiReply Read(IIndiTransport io,string kind,string code) {
            Func<int,string> single=col=>Convert.ToString(io.Invoke("GetSingleData",col),CultureInfo.InvariantCulture)??"";
            Func<int,int,string> multi=(row,col)=>Convert.ToString(io.Invoke("GetMultiData",row,col),CultureInfo.InvariantCulture)??"";
            var reply=new IndiReply{Kind=kind,Code=code};
            if(kind=="STOCKS") {
                reply.Count=Rows(io,12000);var seen=new HashSet<string>();
                for(int row=0;row<reply.Count;row++) {
                    var symbol=Rules.Code(multi(row,1));var market=multi(row,2).Trim();var name=multi(row,3).Trim();
                    if(symbol!=""&&(market=="0"||market=="1")&&multi(row,14).Trim()=="ST"&&
                        multi(row,6).Trim()=="0"&&multi(row,7).Trim()=="0"&&multi(row,8).Trim()=="0") {
                        if(!seen.Add(symbol))throw new InvalidOperationException("Duplicate stock_mst symbol.");
                        reply.Stocks.Add(new Stock{Code=symbol,Name=name,Market=market});
                    }
                }
                if(reply.Stocks.Count==0)throw new InvalidOperationException("No ordinary KOSPI/KOSDAQ stocks; verify installed stock_mst layout.");
            }else if(kind=="BARS") {
                reply.Count=Rows(io,300);var seen=new HashSet<string>();
                for(int row=0;row<reply.Count;row++) {
                    var date=multi(row,0).Trim();DateTime parsed;
                    if(!DateTime.TryParseExact(date,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)||!seen.Add(date))
                        throw new InvalidOperationException("Invalid or duplicate TR_SCHART date.");
                    var bar=new Bar{Date=date,Open=RequiredNumber(multi(row,2),"open"),High=RequiredNumber(multi(row,3),"high"),
                        Low=RequiredNumber(multi(row,4),"low"),Close=RequiredNumber(multi(row,5),"close"),Volume=RequiredNumber(multi(row,9),"volume")};
                    if(bar.Open<=0||bar.Close<=0||bar.Low<=0||bar.High<Math.Max(bar.Open,bar.Close)||bar.Low>Math.Min(bar.Open,bar.Close))
                        throw new InvalidOperationException("Inconsistent TR_SCHART OHLC values.");
                    reply.Bars.Add(bar);
                }
            }else if(kind=="QUOTE") {
                reply.Code=Rules.Code(single(1));reply.Price=Math.Abs(RequiredNumber(single(3),"quote price",true));reply.Clock=single(2).Trim();
                DateTime clock;
                if(reply.Code==""||reply.Code!=code||reply.Price<=0||
                    !DateTime.TryParseExact(reply.Clock,"HHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out clock))
                    throw new InvalidOperationException("SC quote symbol, price or clock does not match request.");
            }else if(kind=="HOLDINGS") {
                reply.Count=Rows(io,2000);var seen=new HashSet<string>();
                for(int row=0;row<reply.Count;row++) {
                    var symbol=Rules.Code(multi(row,0));
                    if(symbol==""||!seen.Add(symbol))throw new InvalidOperationException("Invalid or duplicate SABA200QB holding symbol.");
                    var holding=new Holding{Code=symbol,Name=multi(row,1).Trim(),Quantity=RequiredNumber(multi(row,2),"holding quantity"),
                        SellPending=RequiredNumber(multi(row,3),"pending sells"),BuyPending=RequiredNumber(multi(row,4),"pending buys"),
                        Price=RequiredNumber(multi(row,5),"holding price"),AvgPrice=RequiredNumber(multi(row,6),"average price"),
                        CreditQty=RequiredNumber(multi(row,7),"credit quantity")};
                    if(holding.Quantity!=decimal.Truncate(holding.Quantity)||holding.SellPending!=decimal.Truncate(holding.SellPending)||
                        holding.BuyPending!=decimal.Truncate(holding.BuyPending)||holding.CreditQty!=decimal.Truncate(holding.CreditQty))
                        throw new InvalidOperationException("Fractional domestic stock holding quantity.");
                    reply.Holdings.Add(holding);
                }
            }else throw new InvalidOperationException("Unknown read-only Indi response kind.");
            return reply;
        }
    }

    // Serial request state is independent of COM and WinForms and can be replayed in unit tests.
    public sealed class IndiRequestCoordinator : IDisposable {
        sealed class Snapshot { public int Id;public int? ProviderErrorState;public IndiReply Reply;public Exception Error; }
        sealed class Pending {
            public string Kind,Code;public int? Id;
            public readonly List<Snapshot> Early=new List<Snapshot>();
            public string[] Sensitive=new string[0];
        }
        readonly IIndiTransport io;Pending pending;bool failed,disposed;
        readonly HashSet<string> sessionSensitive=new HashSet<string>(StringComparer.Ordinal);
        public event Action<string> Trace;
        public event Action<string> Fatal;
        public event Action<IndiReply> Done;
        public bool Busy {get{return pending!=null;}}
        public bool Failed {get{return failed;}}
        public IndiRequestCoordinator(IIndiTransport transport) {
            io=transport;io.ReceiveData+=OnData;io.Error+=Halt;
        }
        void Log(string message){if(Trace!=null)Trace(message);}
        IndiDiagnosticSnapshot Diagnose(Pending current,string stage) {
            var diagnostic=io as IIndiDiagnostics;
            if(diagnostic==null){Log("INDI "+stage+": diagnostic getters unavailable.");return null;}
            try {
                var snapshot=diagnostic.ReadDiagnostics();
                // Indi may retain the preceding request's error text. Redact credentials
                // from this session even when the current query has no account inputs.
                Log("INDI "+stage+": "+IndiDiagnosticFormatter.Format(snapshot,sessionSensitive));
                return snapshot;
            } catch(Exception ex) {Log("INDI "+stage+": diagnostics unavailable ("+ex.GetBaseException().GetType().Name+").");return null;}
        }
        static int RequestId(object raw) {
            // Null and bool both become 0 via Convert.ToInt32. They are not request IDs.
            if(raw is short)return (short)raw;
            if(raw is int)return (int)raw;
            throw new InvalidOperationException("RequestData must return an integer request identifier.");
        }
        static void Accepted(object result,string method) {
            if(result is bool&&!(bool)result)throw new InvalidOperationException(method+" returned false.");
            if(result!=null&&!(result is bool)&&result is IConvertible&&Convert.ToString(result,CultureInfo.InvariantCulture)=="0")
                throw new InvalidOperationException(method+" returned zero.");
        }
        public bool Begin(string kind,string query,IDictionary<int,string> input,string code) {
            if(disposed)throw new ObjectDisposedException("IndiRequestCoordinator");
            if(failed)throw new InvalidOperationException("Indi requests halted after an earlier error; reconnect required.");
            if(Busy){Log("Rejected request while another TR is pending.");return false;}
            var current=new Pending{Kind=kind,Code=code};pending=current;
            if(kind=="HOLDINGS"&&input!=null) {
                current.Sensitive=input.Where(x=>x.Key==0||x.Key==2).Select(x=>x.Value).ToArray();
                foreach(var value in current.Sensitive)if(!string.IsNullOrWhiteSpace(value))sessionSensitive.Add(value);
            }
            var stage="SetQueryName";
            try {
                Diagnose(current,"before "+query);
                Accepted(io.Invoke("SetQueryName",query),"SetQueryName");
                stage="GetQueryName";
                var actualQuery=io.Invoke("GetQueryName") as string;
                if(actualQuery==null||!string.Equals(actualQuery.Trim(),query,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Installed Indi did not retain the requested query name.");
                Log("Query name readback verified: "+query);
                stage="SetSingleData";
                if(input!=null)foreach(var field in input.OrderBy(x=>x.Key))Accepted(io.Invoke("SetSingleData",field.Key,field.Value),"SetSingleData");
                stage="RequestData";
                object raw=io.Invoke("RequestData");
                Log("RequestData return type="+(raw==null?"null":raw.GetType().Name));
                var id=RequestId(raw);
                Diagnose(current,"after RequestData rid="+id);
                // The official OCX's RequestData returns a positive ID on acceptance;
                // connection/query/WM_COPYDATA failures return zero (GI004 included).
                if(id<=0)throw new InvalidOperationException("RequestData did not return a positive accepted request ID.");
                if(failed)return false;
                // RequestData clears the OCX's response status, so GetErrorState can
                // be 2 with an empty code/message until ReceiveData sets N or E.
                // Acceptance is the positive ID; response errors are checked from
                // the callback-time snapshot, including reentrant callbacks.
                current.Id=id;Log("REQUEST "+query+" rid="+id+"; awaiting actual ReceiveData.");
                // ReceiveData can be raised before RequestData returns. Its buffer was copied in OnData.
                var match=current.Early.FirstOrDefault(x=>x.Id==id);
                current.Early.Clear();
                if(match!=null)Complete(current,match);
                return !failed;
            }catch(Exception ex){Diagnose(current,"failed "+stage);Halt("Indi "+stage+" failed ("+ex.GetBaseException().GetType().Name+"); request values and exception text omitted.");return false;}
        }
        void OnData(int id) {
            if(disposed||failed)return;
            var current=pending;
            if(current==null){Log("Ignored unsolicited/duplicate ReceiveData rid="+id);return;}
            if(current.Id.HasValue&&current.Id.Value!=id){Log("Ignored mismatched ReceiveData rid="+id);return;}
            var snapshot=new Snapshot{Id=id};
            // The OCX has now set this response's status. Capture it while the
            // response buffer is valid; a later request can clear it again.
            var diagnostic=Diagnose(current,"ReceiveData rid="+id);
            if(diagnostic!=null&&diagnostic.ErrorState.HasValue&&diagnostic.ErrorState.Value>0)
                snapshot.ProviderErrorState=diagnostic.ErrorState.Value;
            else {
                try{snapshot.Reply=IndiResponseParser.Read(io,current.Kind,current.Code);}
                catch(Exception ex){snapshot.Error=ex;}
            }
            if(!current.Id.HasValue) {
                if(current.Early.Count>=16){Halt("Too many callbacks before RequestData returned.");return;}
                current.Early.Add(snapshot);return;
            }
            Complete(current,snapshot);
        }
        void Complete(Pending current,Snapshot snapshot) {
            if(pending!=current||failed)return;
            if(snapshot.ProviderErrorState.HasValue) {
                Halt("Indi returned an error in matching ReceiveData rid="+snapshot.Id+"; GetErrorState="+snapshot.ProviderErrorState.Value+". See sanitized INDI diagnostics.");return;
            }
            if(snapshot.Error!=null){Diagnose(current,"response error rid="+snapshot.Id);Halt("Indi response parse failed ("+snapshot.Error.GetBaseException().GetType().Name+"); verify installed field layout. Exception text omitted.");return;}
            pending=null;Log("RECEIVED "+current.Kind+" rid="+snapshot.Id+" rows="+snapshot.Reply.Count);
            if(Done!=null)Done(snapshot.Reply);
        }
        public void Timeout(){if(Busy){var current=pending;Diagnose(current,"timeout rid="+(current.Id.HasValue?current.Id.Value.ToString():"unknown"));Halt("Indi TR timed out after 20 seconds without matching ReceiveData; rid="+(current.Id.HasValue?current.Id.Value.ToString():"unknown")+". Requests and automated trading halted.");}}
        public void Halt(string reason) {
            if(disposed||failed)return;
            pending=null;failed=true;Log("HALT: "+reason);if(Fatal!=null)Fatal(reason);
        }
        public void Dispose(){if(disposed)return;disposed=true;pending=null;sessionSensitive.Clear();io.ReceiveData-=OnData;io.Error-=Halt;}
    }

    public sealed class IndiApi : IDisposable {
        readonly Control surface;readonly System.Windows.Forms.Timer timeout;
        ComIndiTransport transport;IndiRequestCoordinator requests;bool disposed,hasReceivedData;
        public event Action<string> Trace;
        public event Action<IndiReply> Done;
        public event Action<string> Fatal;
        public bool TransportReady{get{return !disposed&&requests!=null&&!requests.Failed;}}
        public bool Connected{get{return TransportReady&&hasReceivedData;}}
        public bool Busy{get{return requests!=null&&requests.Busy;}}
        public IndiApi(Control surface) {
            this.surface=surface;timeout=new System.Windows.Forms.Timer{Interval=20000};
            timeout.Tick+=(s,e)=>{timeout.Stop();if(requests!=null)requests.Timeout();};
        }
        void Log(string message){if(Trace!=null)Trace(message);}
        public void Connect() {
            if(disposed)throw new ObjectDisposedException("IndiApi");
            if(TransportReady)return;
            timeout.Stop();
            hasReceivedData=false;
            if(requests!=null){requests.Dispose();requests=null;}
            if(transport!=null){transport.Dispose();transport=null;}
            transport=new ComIndiTransport(surface,Log);
            requests=new IndiRequestCoordinator(transport);requests.Trace+=Log;
            requests.Done+=reply=>{timeout.Stop();hasReceivedData=true;if(Done!=null)Done(reply);};
            requests.Fatal+=reason=>{timeout.Stop();if(Fatal!=null)Fatal(reason);};
            transport.ReceiveSysMsg+=id=>Log("Indi ReceiveSysMsg="+id+"; no assumption of account/login readiness.");
        }
        public bool Stocks(){return Request("STOCKS","stock_mst",null,"");}
        public bool Bars(string code,int count=45) {
            code=ValidateCode(code);
            if(count<2||count>250)throw new ArgumentOutOfRangeException("count","Daily bar count must be between 2 and 250.");
            return Request("BARS","TR_SCHART",new Dictionary<int,string>{{0,code},{1,"D"},{2,"1"},{3,"00000000"},{4,"99999999"},{5,count.ToString(CultureInfo.InvariantCulture)}},code);
        }
        public bool Quote(string code){code=ValidateCode(code);return Request("QUOTE","SC",new Dictionary<int,string>{{0,code}},code);}
        static string ValidateCode(string code){var normalized=Rules.Code(code);if(normalized=="")throw new ArgumentException("Six digit stock symbol required.");return normalized;}
        public bool Holdings(string account,string password) {
            if(string.IsNullOrWhiteSpace(account)||string.IsNullOrWhiteSpace(password))throw new InvalidOperationException("계좌번호/비밀번호를 PC 화면에서 입력해야 합니다.");
            return Request("HOLDINGS","SABA200QB",new Dictionary<int,string>{{0,account.Trim()},{1,"01"},{2,password}},"");
        }
        bool Request(string kind,string query,IDictionary<int,string> input,string code) {
            if(!TransportReady)throw new InvalidOperationException("Indi OCX is not attached or requests are halted.");
            if(Busy)return false;
            var accepted=requests.Begin(kind,query,input,code);
            if(requests.Busy)timeout.Start();
            return accepted;
        }
        public void Halt(string reason){if(requests!=null)requests.Halt(reason);}
        public void Dispose() {
            if(disposed)return;disposed=true;timeout.Stop();timeout.Dispose();
            if(requests!=null){requests.Dispose();requests=null;}
            if(transport!=null){transport.Dispose();transport=null;}
        }
    }
}
