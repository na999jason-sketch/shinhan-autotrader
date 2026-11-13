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
        // Exposed only to the separate owned order connection below. Ordinary
        // query callers cannot obtain the native RCW or widen the read allowlist.
        internal object NativeControl {get{if(disposed)throw new ObjectDisposedException("ComIndiTransport");return com;}}
        internal string InstalledStarterPath {get;private set;}
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
                InstalledStarterPath=starter;
                log(IndiInstallation.DescribeBalanceSchema(starter));
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
            if(method=="SetQueryName"&&(args.Length!=1||!new[]{"stock_mst","TR_SCHART","SC","TR_1110_11","SABA200QB","SABA609Q1","SABA602Q1","SABA415Q1","SABA231Q1"}.Contains(Convert.ToString(args[0]))))
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
        public List<CashInstrument> Instruments=new List<CashInstrument>();
        public List<Bar> Bars=new List<Bar>();
        public List<Holding> Holdings=new List<Holding>();
        public decimal Price;
        public bool HasTradeClock;
        public AccountCashSnapshot Cash;
        public BrokerOrdersSnapshot BrokerOrders;
        public AccountOrderCapacity Capacity;
        public BrokerHoldingsSnapshot TradeHoldings;
        public DateTime RequestStartedUtc,ReceivedUtc;
        public string Clock,Message,OrderNo;
    }

    public static class IndiResponseParser {
        // Only parser-owned context is safe to print. Provider exception messages
        // can contain credentials and must never be forwarded with InnerException.
        sealed class FieldFailure : InvalidOperationException {
            public readonly string Context;
            public FieldFailure(string context,Exception inner=null):base(context,inner){Context=context;}
        }
        internal static string FailureContext(Exception error) {
            var failure=error as FieldFailure;return failure==null?(AccountQueries.FailureContext(error)??"unclassified provider/validation error"):failure.Context;
        }
        static readonly Regex Number=new Regex(@"^[+-]?(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?$",RegexOptions.CultureInvariant);
        public static decimal RequiredNumber(object raw,string label,bool signed=false,bool optional=false) {
            var text=(Convert.ToString(raw,CultureInfo.InvariantCulture)??"").Trim();
            if(optional&&text=="")return 0;
            decimal value;
            if(!Number.IsMatch(text)||!decimal.TryParse(text.Replace(",",""),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,out value)||(!signed&&value<0))throw new FieldFailure("Invalid numeric field: "+label);
            return value;
        }
        static int Rows(IIndiTransport io,int limit) {
            var count=RequiredNumber(io.Invoke("GetMultiRowCount"),"row count");
            if(count!=decimal.Truncate(count)||count>limit)throw new InvalidOperationException("Indi row count outside supported range.");
            return (int)count;
        }
        public static IndiReply Read(IIndiTransport io,string kind,string code,Action<string,string,string> quoteTrace=null,decimal? requestedPrice=null,bool tradeBasisRequested=false,bool ordinaryOnlyRequested=false,
            BrokerOrderQueryProfile orderQueryProfile=BrokerOrderQueryProfile.Unverified,string orderSchemaSha256=null) {
            Func<int,string> single=col=>{
                try{return Convert.ToString(io.Invoke("GetSingleData",col),CultureInfo.InvariantCulture)??"";}
                catch(Exception ex){throw new FieldFailure("GetSingleData index="+col+" failed",ex);}
            };
            Func<int,int,string> multi=(row,col)=>{
                try{return Convert.ToString(io.Invoke("GetMultiData",row,col),CultureInfo.InvariantCulture)??"";}
                catch(Exception ex){throw new FieldFailure("GetMultiData row="+row+" index="+col+" failed",ex);}
            };
            var reply=new IndiReply{Kind=kind,Code=code};
            if(kind=="STOCKS") {
                reply.Count=Rows(io,12000);var seen=new HashSet<string>();
                for(int row=0;row<reply.Count;row++) {
                    var symbol=Rules.Code(multi(row,1));var market=multi(row,2).Trim();var name=multi(row,3).Trim();
                    var product=multi(row,14).Trim();var suspended=multi(row,6).Trim()!="0";
                    if(symbol!=""&&(market=="0"||market=="1"))
                        reply.Instruments.Add(new CashInstrument{Code=symbol,Market=market,Product=product,Suspended=suspended});
                    if(symbol!=""&&(market=="0"||market=="1")&&product=="ST"&&
                        !suspended&&multi(row,7).Trim()=="0"&&multi(row,8).Trim()=="0") {
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
                var rawCode=single(1);var rawClock=single(2);var rawPrice=single(3);
                if(quoteTrace!=null)quoteTrace(rawCode,rawClock,rawPrice);
                reply.Code=Rules.Code(rawCode);
                if(reply.Code=="")throw new FieldFailure("SC symbol single[1] is blank or invalid");
                if(reply.Code!=code)throw new FieldFailure("SC symbol single[1] differs from requested symbol");
                reply.Price=Math.Abs(RequiredNumber(rawPrice,"SC price single[3]",true));
                if(reply.Price<=0)throw new FieldFailure("SC price single[3] must be positive");
                reply.Clock=rawClock.Trim();
                DateTime clock;
                reply.HasTradeClock=DateTime.TryParseExact(reply.Clock,"HHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out clock);
                if(!reply.HasTradeClock) {
                    reply.Clock="";reply.Message="거래시각 미확인: 가격 표시만 허용, 자동매매 사용 제외";
                }
            }else if(kind=="SNAPSHOT") {
                // Official TR_1110_11 input0 identifies the stock; single24 is
                // current price. There is no symbol echo or trade timestamp.
                // Correlate the request ID and keep this display-only response
                // outside all paper buy/sell paths, regardless of arrival time.
                reply.Kind="QUOTE";
                if(Rules.Code(code)==""||Rules.Code(code)!=code)throw new FieldFailure("Snapshot request symbol is invalid");
                var rawPrice=single(24);
                if(quoteTrace!=null)quoteTrace(code,"",rawPrice);
                reply.Price=Math.Abs(RequiredNumber(rawPrice,"TR_1110_11 price single[24]",true));
                if(reply.Price<=0)throw new FieldFailure("TR_1110_11 price single[24] must be positive");
                reply.Clock="";reply.HasTradeClock=false;
                reply.Message="조회 가격: 거래시각 미제공, 자동매매 사용 제외";
            }else if(kind=="TRADEHOLDINGS"||kind=="ORDINARYHOLDINGS") {
                reply.TradeHoldings=AccountQueries.ReadHoldings(io,DateTime.UtcNow,tradeBasisRequested,ordinaryOnlyRequested);reply.Holdings=reply.TradeHoldings.Holdings;
                reply.Count=reply.Holdings.Count;
            }else if(kind=="CASH") {
                reply.Cash=AccountQueries.ReadCash(io,DateTime.UtcNow);reply.Count=1;
            }else if(kind=="CAPACITY") {
                if(!requestedPrice.HasValue||requestedPrice.Value<=0)throw new FieldFailure("Capacity request price is missing");
                reply.Capacity=AccountQueries.ReadCapacity(io,code,requestedPrice.Value,DateTime.UtcNow);reply.Count=1;
            }else if(kind=="ORDERS") {
                reply.BrokerOrders=AccountQueries.ReadOrders(io,code,DateTime.UtcNow,orderQueryProfile,orderSchemaSha256);reply.Count=reply.BrokerOrders.Orders.Count;
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
        sealed class Snapshot { public int Id;public int? ProviderErrorState;public bool NormalResponse;public IndiReply Reply;public Exception Error;public DateTime ReceivedUtc; }
        sealed class Pending {
            public string Kind,Code;public int? Id;
            public string Query;
            public IDictionary<int,string> Input;
            public bool Retried,RequestEntered;
            public DateTime? RetryDueUtc;
            public bool TradeBasisRequested,OrdinaryOnlyRequested,OrdersScopeRequested;
            public BrokerOrderQueryProfile OrdersProfile;
            public string OrderSchemaSha256;
            public decimal? RequestedPrice;
            public DateTime RequestStartedUtc;
            public readonly List<Snapshot> Early=new List<Snapshot>();
            public string[] Sensitive=new string[0];
        }
        readonly IIndiTransport io;Pending pending;bool failed,disposed;
        readonly Func<IndiAccountOrderSchemaInfo> orderSchemaResolver;
        readonly HashSet<string> sessionSensitive=new HashSet<string>(StringComparer.Ordinal);
        public event Action<string> Trace;
        public event Action<string> Fatal;
        public event Action<IndiReply> Done;
        public bool Busy {get{return pending!=null;}}
        public bool Failed {get{return failed;}}
        public IndiRequestCoordinator(IIndiTransport transport,Func<IndiAccountOrderSchemaInfo> schemaResolver=null) {
            io=transport;orderSchemaResolver=schemaResolver;io.ReceiveData+=OnData;io.Error+=Halt;
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
        static bool VerifiedHoldingsRequest(string kind,string expectedKind,string query,IDictionary<int,string> input,bool settersConfirmed,string expectedBasis) {
            string accountNumber,secret,product,basis,unit,selection,output,quoteKind;
            return kind==expectedKind&&string.Equals(query,"SABA609Q1",StringComparison.OrdinalIgnoreCase)
                &&settersConfirmed&&input!=null&&input.Count==8
                &&input.TryGetValue(0,out accountNumber)&&accountNumber!=null&&accountNumber.Length==11&&accountNumber.All(c=>c>='0'&&c<='9')
                &&input.TryGetValue(2,out secret)&&!string.IsNullOrWhiteSpace(secret)&&secret.Length<=9&&!secret.Any(char.IsControl)
                &&input.TryGetValue(1,out product)&&product=="01"
                &&input.TryGetValue(3,out basis)&&basis==expectedBasis&&input.TryGetValue(4,out unit)&&unit=="1"
                &&input.TryGetValue(5,out selection)&&selection=="1"&&input.TryGetValue(6,out output)&&output=="0"
                &&input.TryGetValue(7,out quoteKind)&&quoteKind=="1";
        }
        static bool VerifiedOrdersRequest(string kind,string query,IDictionary<int,string> input,bool settersConfirmed,string code) {
            string day,number,secret,market,filled,aggregation,symbol,product,work;DateTime parsed;
            return kind=="ORDERS"&&string.Equals(query,"SABA231Q1",StringComparison.OrdinalIgnoreCase)
                &&settersConfirmed&&input!=null&&input.Count==9
                &&input.TryGetValue(0,out day)&&day!=null&&day.Length==8&&day==code
                &&DateTime.TryParseExact(day,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)
                &&input.TryGetValue(1,out number)&&number!=null&&number.Length==11&&number.All(c=>c>='0'&&c<='9')
                &&input.TryGetValue(2,out secret)&&!string.IsNullOrWhiteSpace(secret)&&secret.Length<=9&&!secret.Any(char.IsControl)
                &&input.TryGetValue(3,out market)&&market=="00"&&input.TryGetValue(4,out filled)&&filled=="0"
                &&input.TryGetValue(5,out aggregation)&&aggregation=="0"&&input.TryGetValue(6,out symbol)&&symbol=="*"
                &&input.TryGetValue(7,out product)&&product=="01"&&input.TryGetValue(8,out work)&&work=="Y";
        }
        public bool Begin(string kind,string query,IDictionary<int,string> input,string code,decimal? requestedPrice=null) {
            return BeginCore(kind,query,input,code,requestedPrice,false);
        }
        static bool RetryableRead(string kind,string query) {
            if(query=="SABA609Q1")return kind=="TRADEHOLDINGS"||kind=="ORDINARYHOLDINGS";
            return kind=="CASH"&&query=="SABA602Q1"||kind=="CAPACITY"&&query=="SABA415Q1"
                ||kind=="ORDERS"&&query=="SABA231Q1"||kind=="HOLDINGS"&&query=="SABA200QB";
        }
        bool BeginCore(string kind,string query,IDictionary<int,string> input,string code,decimal? requestedPrice,bool retried) {
            if(disposed)throw new ObjectDisposedException("IndiRequestCoordinator");
            if(failed)throw new InvalidOperationException("Indi requests halted after an earlier error; reconnect required.");
            if(Busy){Log("Rejected request while another TR is pending.");return false;}
            // COM can pump callbacks. Bind provenance to a private snapshot of
            // the fields actually sent, never a caller-mutated dictionary.
            if(input!=null)input=new SortedDictionary<int,string>(input);
            var current=new Pending{Kind=kind,Code=code,RequestedPrice=requestedPrice,Query=query,Input=input,Retried=retried};pending=current;
            if((kind=="HOLDINGS"||kind=="TRADEHOLDINGS"||kind=="ORDINARYHOLDINGS"||kind=="CASH"||kind=="CAPACITY"||kind=="ORDERS")&&input!=null) {
                var accountIndex=kind=="ORDERS"?1:0;
                current.Sensitive=input.Where(x=>x.Key==accountIndex||x.Key==2).Select(x=>x.Value).ToArray();
                foreach(var value in current.Sensitive)if(!string.IsNullOrWhiteSpace(value))sessionSensitive.Add(value);
            }
            var stage="SetQueryName";
            try {
                Diagnose(current,"before "+query);
                object selected=io.Invoke("SetQueryName",query);Accepted(selected,"SetQueryName");
                bool settersConfirmed=selected is bool&&(bool)selected;
                stage="GetQueryName";
                var actualQuery=io.Invoke("GetQueryName") as string;
                if(actualQuery==null||!string.Equals(actualQuery.Trim(),query,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Installed Indi did not retain the requested query name.");
                Log("Query name readback verified: "+query);
                stage="SetSingleData";
                if(input!=null)foreach(var field in input.OrderBy(x=>x.Key)) {
                    object accepted=io.Invoke("SetSingleData",field.Key,field.Value);Accepted(accepted,"SetSingleData");
                    settersConfirmed=settersConfirmed&&accepted is bool&&(bool)accepted;
                }
                // The documented trade-basis request scopes its successful
                // response. Optional echo metadata is not the authority; an
                // explicitly contradictory returned basis still blocks orders.
                current.TradeBasisRequested=VerifiedHoldingsRequest(kind,"TRADEHOLDINGS",query,input,settersConfirmed,"1");
                current.OrdinaryOnlyRequested=VerifiedHoldingsRequest(kind,"ORDINARYHOLDINGS",query,input,settersConfirmed,"5");
                current.OrdersScopeRequested=VerifiedOrdersRequest(kind,query,input,settersConfirmed,code);
                if(current.OrdersScopeRequested&&orderSchemaResolver!=null) {
                    // Bind immutable static-layout evidence to this request.
                    // Missing definitions keep legacy interpretation and cannot
                    // authorize a modern status exception by numeric code alone.
                    try {
                        var schema=orderSchemaResolver();
                        if(schema!=null) {
                            Log(schema.Diagnostic);
                            if(schema.Recognized){current.OrdersProfile=BrokerOrderQueryProfile.ModernOrderState54;current.OrderSchemaSha256=schema.Sha256;}
                        }
                    }catch {Log("SABA231Q1 installed schema unavailable; modern status corroboration disabled.");}
                }
                // Input COM calls may pump a stop/dispose/account-change event.
                // An obsolete pending instance must not start its deferred read.
                if(disposed||failed||pending!=current)return false;
                stage="RequestData";
                current.RequestStartedUtc=DateTime.UtcNow;
                // Input setters may pump old ReceiveData callbacks. A timestamp
                // cannot distinguish those callbacks on a coarse Windows clock;
                // only callbacks after entering this invocation can be ours.
                current.RequestEntered=true;
                object raw=io.Invoke("RequestData");
                Log("RequestData return type="+(raw==null?"null":raw.GetType().Name));
                var id=RequestId(raw);
                var acceptance=Diagnose(current,"after RequestData rid="+id);
                // The official OCX's RequestData returns a positive ID on acceptance;
                // connection/query/WM_COPYDATA failures return zero (GI004 included).
                if(id==0&&!failed&&!disposed&&pending==current&&!current.Retried&&current.Early.Count==0
                    &&RetryableRead(kind,query)&&acceptance!=null&&acceptance.ErrorState>0&&acceptance.ErrorCode=="GI004") {
                    current.RetryDueUtc=DateTime.UtcNow.AddSeconds(1);
                    Log("READ-ONLY GI004: "+query+" was not accepted (rid=0); one fresh read will be attempted after one second. No order retry.");
                    return true;
                }
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
        public bool PollReadOnlyRetry(DateTime utc) {
            if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC retry polling time required.");
            if(disposed||failed)return false;
            var current=pending;
            if(current==null||!current.RetryDueUtc.HasValue||utc<current.RetryDueUtc.Value)return false;
            // This is still a read-only request. Repeat the full setter/query
            // binding and use a new actual request-start time and accepted ID.
            // CancelPending/Halt/Dispose removes this pending instance entirely.
            pending=null;
            return BeginCore(current.Kind,current.Query,current.Input,current.Code,current.RequestedPrice,true);
        }
        void OnData(int id) {
            if(disposed||failed)return;
            var current=pending;
            if(current==null){Log("Ignored unsolicited/duplicate ReceiveData rid="+id);return;}
            if(!current.RequestEntered){Log("Ignored ReceiveData before RequestData entry; response scope not established.");return;}
            if(current.RetryDueUtc.HasValue){Log("Ignored ReceiveData while an unaccepted read is awaiting its bounded retry.");return;}
            if(current.Id.HasValue&&current.Id.Value!=id){Log("Ignored mismatched ReceiveData rid="+id);return;}
            var snapshot=new Snapshot{Id=id,ReceivedUtc=DateTime.UtcNow};
            // The OCX has now set this response's status. Capture it while the
            // response buffer is valid; a later request can clear it again.
            var diagnostic=Diagnose(current,"ReceiveData rid="+id);
            snapshot.NormalResponse=diagnostic!=null&&diagnostic.ErrorState==0;
            if(diagnostic!=null&&diagnostic.ErrorState.HasValue&&diagnostic.ErrorState.Value>0)
                snapshot.ProviderErrorState=diagnostic.ErrorState.Value;
            else {
                try{snapshot.Reply=IndiResponseParser.Read(io,current.Kind,current.Code,
                    (symbol,clock,price)=>Log(IndiDiagnosticFormatter.FormatQuote(current.Kind,symbol,clock,price,sessionSensitive)),current.RequestedPrice,
                    current.TradeBasisRequested&&diagnostic!=null&&diagnostic.ErrorState==0,
                    current.OrdinaryOnlyRequested&&diagnostic!=null&&diagnostic.ErrorState==0,
                    current.OrdersScopeRequested&&snapshot.NormalResponse?current.OrdersProfile:BrokerOrderQueryProfile.Unverified,
                    current.OrdersScopeRequested&&snapshot.NormalResponse?current.OrderSchemaSha256:null);}
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
            if(snapshot.Error!=null){Diagnose(current,"response error rid="+snapshot.Id);Halt("Indi response parse failed ("+snapshot.Error.GetBaseException().GetType().Name+"); "+IndiResponseParser.FailureContext(snapshot.Error)+". Provider exception text omitted.");return;}
            snapshot.Reply.RequestStartedUtc=current.RequestStartedUtc;snapshot.Reply.ReceivedUtc=snapshot.ReceivedUtc;
            if(snapshot.Reply.TradeHoldings!=null){snapshot.Reply.TradeHoldings.RequestStartedUtc=current.RequestStartedUtc;snapshot.Reply.TradeHoldings.ReceivedUtc=snapshot.ReceivedUtc;}
            if(snapshot.Reply.Cash!=null)snapshot.Reply.Cash.ReceivedUtc=snapshot.ReceivedUtc;
            if(snapshot.Reply.Capacity!=null)snapshot.Reply.Capacity.ReceivedUtc=snapshot.ReceivedUtc;
            if(snapshot.Reply.BrokerOrders!=null) {
                string number=null;if(current.Input!=null)current.Input.TryGetValue(1,out number);
                bool verified=current.RequestEntered&&current.OrdersScopeRequested&&snapshot.NormalResponse
                    &&current.Id.HasValue&&current.Id.Value>0&&current.Id.Value==snapshot.Id
                    &&current.RequestStartedUtc.Kind==DateTimeKind.Utc&&current.RequestStartedUtc<=snapshot.ReceivedUtc;
                snapshot.Reply.BrokerOrders.BindRequestScope(current.RequestStartedUtc,snapshot.ReceivedUtc,verified,number);
            }
            pending=null;Log("RECEIVED "+current.Kind+" rid="+snapshot.Id+" rows="+snapshot.Reply.Count);
            if(Done!=null)Done(snapshot.Reply);
        }
        public void Timeout(){if(Busy){var current=pending;Diagnose(current,"timeout rid="+(current.Id.HasValue?current.Id.Value.ToString():"unknown"));Halt("Indi TR timed out after 20 seconds without matching ReceiveData; rid="+(current.Id.HasValue?current.Id.Value.ToString():"unknown")+". Requests and automated trading halted.");}}
        // Cancels only a local read-only request. It never cancels a broker order.
        public void CancelPending(){if(disposed||pending==null)return;pending=null;Log("Read-only request discarded locally; late callbacks will be ignored.");}
        public void Halt(string reason) {
            if(disposed||failed)return;
            pending=null;failed=true;Log("HALT: "+reason);if(Fatal!=null)Fatal(reason);
        }
        public void Dispose(){if(disposed)return;disposed=true;pending=null;sessionSensitive.Clear();io.ReceiveData-=OnData;io.Error-=Halt;}
    }

    // A dedicated order OCX is initialized independently of short-lived proofs.
    // Sending still requires the account authorization and current preflight;
    // it never shares response buffers with the read-only OCX.
    internal sealed class IndiLiveOrderConnection : ILiveOrderWire,ILiveOrderChannelPreparation {
        readonly Control surface;readonly Action<string> log;
        ComIndiTransport owner;ComLiveOrderWire wire;bool disposed,preparing;
        IndiOrderSchemaInfo activeSchema;string activeQuery,lastSchemaDiagnostic;
        public event Action<LiveOrderAcknowledgement> Acknowledged;
        public IndiLiveOrderConnection(Control surface,Action<string> log){this.surface=surface;this.log=log;}
        public void PrepareChannel() {
            if(disposed)throw new ObjectDisposedException("IndiLiveOrderConnection");if(wire!=null)return;
            if(preparing)throw new InvalidOperationException("Order channel initialization is already in progress.");
            preparing=true;
            // Site/initialize the dedicated control before collecting a short-lived
            // order preflight. Initialization itself does not submit any order.
            try {
                owner=new ComIndiTransport(surface,log);
                if(disposed)throw new ObjectDisposedException("IndiLiveOrderConnection");
                wire=new ComLiveOrderWire(owner.NativeControl,SelectLayout,owner.ReadDiagnostics,null,ConfigureFields);
                if(disposed)throw new ObjectDisposedException("IndiLiveOrderConnection");
                wire.Acknowledged+=Forward;
            }catch{if(wire!=null){wire.Dispose();wire=null;}if(owner!=null){owner.Dispose();owner=null;}throw;}
            finally{preparing=false;}
        }
        IndiOrderSchemaInfo InspectSchema(string query) {
            var info=IndiOrderSchema.ReadInstalled(owner.InstalledStarterPath,query);
            if(info.Diagnostic!=lastSchemaDiagnostic){lastSchemaDiagnostic=info.Diagnostic;if(log!=null)log(info.Diagnostic);}
            return info;
        }
        OrderResponseLayout SelectLayout(LiveOrderIntent intent) {
            activeQuery=LiveOrderProtocol.QueryName(intent);activeSchema=InspectSchema(activeQuery);
            return activeSchema.Kind==IndiOrderSchemaKind.Legacy?OrderResponseLayout.Legacy:
                activeSchema.Kind==IndiOrderSchemaKind.Modern?OrderResponseLayout.Modern:OrderResponseLayout.Unknown;
        }
        void ConfigureFields(LiveOrderIntent intent,SortedDictionary<int,string> fields) {
            if(activeSchema==null||activeQuery!=LiveOrderProtocol.QueryName(intent)||activeSchema.Kind==IndiOrderSchemaKind.Unknown)
                throw new InvalidOperationException("설치된 주문 정의를 확인할 수 없습니다.");
            if(activeSchema.Kind==IndiOrderSchemaKind.Legacy)return;
            if(!activeSchema.KrxRouteInputIndex.HasValue||string.IsNullOrEmpty(activeSchema.KrxRouteCode)) {
                if(log!=null)log("주문 전 중단: 설치된 신형 주문 정의의 KRX 전송 코드가 확인되지 않았습니다. 주문 정의 진단을 확인하십시오.");
                throw new InvalidOperationException("설치된 주문 방식 코드가 확인되지 않았습니다.");
            }
            LiveOrderProtocol.ApplyInstalledSchema(intent,fields,activeSchema);
        }
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            try {
                if(disposed)throw new ObjectDisposedException("IndiLiveOrderConnection");
                LiveOrderSafety.EnsureAllowed(checks,credentials,utc);
                if(wire==null)PrepareChannel();
            }catch{throw new LiveOrderNotSentException();}
            return wire.Send(intent,credentials,checks,utc);
        }
        void Forward(LiveOrderAcknowledgement reply){
            if(activeSchema!=null&&activeSchema.Kind==IndiOrderSchemaKind.Modern&&reply.ResponseEvidence!=null) {
                LiveOrderResponseParser.RequireKrxRoute(reply.ResponseEvidence);
                reply.BrokerOrderNumber=reply.ResponseEvidence.BrokerOrderNumber;
            }
            if(Acknowledged!=null)Acknowledged(reply);
        }
        public void Dispose() {
            if(disposed)return;disposed=true;
            try {if(wire!=null){wire.Acknowledged-=Forward;wire.Dispose();wire=null;}}
            finally {if(owner!=null){owner.Dispose();owner=null;}}
        }
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
            requests=new IndiRequestCoordinator(transport,()=>IndiAccountOrderSchema.ReadInstalled(transport.InstalledStarterPath));requests.Trace+=Log;
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
        public bool SnapshotQuote(string code){code=ValidateCode(code);return Request("SNAPSHOT","TR_1110_11",new Dictionary<int,string>{{0,code}},code);}
        public ILiveOrderWire CreateLiveOrderWire(){if(disposed)throw new ObjectDisposedException("IndiApi");return new IndiLiveOrderConnection(surface,Log);}
        static string ValidateCode(string code){var normalized=Rules.Code(code);if(normalized=="")throw new ArgumentException("Six digit stock symbol required.");return normalized;}
        public bool Holdings(string account,string password) {
            if(string.IsNullOrWhiteSpace(account)||string.IsNullOrWhiteSpace(password))throw new InvalidOperationException("계좌번호/비밀번호를 PC 화면에서 입력해야 합니다.");
            return Request("HOLDINGS","SABA200QB",new Dictionary<int,string>{{0,account.Trim()},{1,"01"},{2,password}},"");
        }
        public bool Cash(string account,string password){return Request("CASH","SABA602Q1",AccountQueries.CashInputs(account,password),"");}
        public bool TradeHoldings(string account,string password){return Request("TRADEHOLDINGS","SABA609Q1",AccountQueries.HoldingsInputs(account,password),"");}
        public bool OrdinaryHoldings(string account,string password){return Request("ORDINARYHOLDINGS","SABA609Q1",AccountQueries.OrdinaryHoldingsInputs(account,password),"");}
        public bool Orders(string account,string password,string date){return Request("ORDERS","SABA231Q1",AccountQueries.OrdersInputs(account,password,date),date);}
        public bool Capacity(string account,string password,string code,decimal limitPrice) {
            code=ValidateCode(code);
            return Request("CAPACITY","SABA415Q1",AccountQueries.CapacityInputs(account,password,code,limitPrice),code,limitPrice);
        }
        public void PollReadOnlyRetry(DateTime utc) {
            if(disposed||requests==null)return;
            if(requests.PollReadOnlyRetry(utc)) {
                timeout.Stop();if(requests.Busy)timeout.Start();
            }
        }
        bool Request(string kind,string query,IDictionary<int,string> input,string code,decimal? requestedPrice=null) {
            if(!TransportReady)throw new InvalidOperationException("Indi OCX is not attached or requests are halted.");
            if(Busy)return false;
            var accepted=requests.Begin(kind,query,input,code,requestedPrice);
            if(requests.Busy)timeout.Start();
            return accepted;
        }
        public void Halt(string reason){if(requests!=null)requests.Halt(reason);}
        public void CancelPending(){timeout.Stop();if(requests!=null)requests.CancelPending();}
        public void Dispose() {
            if(disposed)return;disposed=true;timeout.Stop();timeout.Dispose();
            if(requests!=null){requests.Dispose();requests=null;}
            if(transport!=null){transport.Dispose();transport=null;}
        }
    }
}
