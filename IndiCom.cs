using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace ShinhanNativeTrader {
    [ComImport, Guid("B196B283-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IProvideClassInfo { void GetClassInfo(out ComTypes.ITypeInfo typeInfo); }

    // Unlike PyQt/QAxWidget, AxHost supplies a native Windows ActiveX site and STA message pump.
    public sealed class IndiHost : AxHost {
        public IndiHost(string progId) : base(Type.GetTypeFromProgID(progId, true).GUID.ToString("B")) {
            Size=new System.Drawing.Size(8,8);
        }
        public object ComObject { get { return GetOcx(); } }
    }

    internal sealed class ComEventInfo {
        public Guid SourceInterface;
        public Dictionary<string,int> DispIds = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    }
    internal static class ComTypeDiscovery {
        public static ComEventInfo Discover(object com) {
            var provide=com as IProvideClassInfo;
            if(provide==null)throw new InvalidOperationException("OCX does not expose IProvideClassInfo; inspect installed typelib before binding events.");
            ComTypes.ITypeInfo coclass;
            provide.GetClassInfo(out coclass);
            if(coclass==null)throw new InvalidOperationException("ActiveX class type information unavailable");
            try {
                IntPtr attrptr=IntPtr.Zero;
                coclass.GetTypeAttr(out attrptr);
                ComTypes.TYPEATTR attr;
                try {attr=(ComTypes.TYPEATTR)Marshal.PtrToStructure(attrptr, typeof(ComTypes.TYPEATTR));}
                finally {coclass.ReleaseTypeAttr(attrptr);}
                for(int i=0;i<attr.cImplTypes;i++) {
                    ComTypes.IMPLTYPEFLAGS flags;
                    coclass.GetImplTypeFlags(i,out flags);
                    if((flags & ComTypes.IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE)==0)continue;
                    int href;coclass.GetRefTypeOfImplType(i,out href);
                    ComTypes.ITypeInfo source;coclass.GetRefTypeInfo(href,out source);
                    try {
                        IntPtr ap=IntPtr.Zero;source.GetTypeAttr(out ap);
                        ComTypes.TYPEATTR a;
                        try {a=(ComTypes.TYPEATTR)Marshal.PtrToStructure(ap,typeof(ComTypes.TYPEATTR));}
                        finally {source.ReleaseTypeAttr(ap);}
                        var e=new ComEventInfo{SourceInterface=a.guid};
                        for(int j=0;j<a.cFuncs;j++) {
                            IntPtr fp=IntPtr.Zero;source.GetFuncDesc(j,out fp);
                            ComTypes.FUNCDESC f;
                            try {f=(ComTypes.FUNCDESC)Marshal.PtrToStructure(fp,typeof(ComTypes.FUNCDESC));}
                            finally {source.ReleaseFuncDesc(fp);}
                            string name,desc,helpfile;int helpContext;
                            source.GetDocumentation(f.memid,out name,out desc,out helpContext,out helpfile);
                            if(!string.IsNullOrWhiteSpace(name))e.DispIds[name]=f.memid;
                        }
                        if(e.DispIds.ContainsKey("ReceiveData"))return e;
                    } finally {Marshal.ReleaseComObject(source);}
                }
            } finally {Marshal.ReleaseComObject(coclass);}
            throw new InvalidOperationException("No event source interface exposing ReceiveData. Native OCX registration/type library should be checked.");
        }
    }

    public sealed class IndiReply {
        public string Kind,Code;
        public int Count;
        public List<Stock> Stocks=new List<Stock>();
        public List<Bar> Bars=new List<Bar>();
        public List<Holding> Holdings=new List<Holding>();
        public decimal Price;
        public string Clock, Message, OrderNo;
    }
    public sealed class IndiApi : IDisposable {
        const string PROGID="GIEXPERTCONTROL.GiExpertControlCtrl.1";
        readonly Control _surface;
        IndiHost _host;
        object _com;
        ComEventInfo _source;
        bool _connected, _failed;
        string _pendingKind, _pendingCode;
        int? _requestId;
        readonly Timer _timeout;
        readonly List<Tuple<int,Delegate>> _sinks=new List<Tuple<int,Delegate>>();
        public event Action<string> Trace;
        public event Action<IndiReply> Done;
        public event Action<string> Fatal;
        public bool Connected{get{return _connected;}}
        public bool Busy{get{return _pendingKind!=null;}}
        public IndiApi(Control surface) {
            _surface=surface;
            _timeout=new Timer{Interval=20000};
            _timeout.Tick += (s,e)=> { _timeout.Stop(); if(Busy)StopWithError("20초 동안 신한 응답이 없습니다. 추가 요청과 모든 자동주문을 중단합니다.");};
        }
        void Log(string line){if(Trace!=null)Trace(line);}
        static object CallCom(object com, string name, params object[] args) {
            return com.GetType().InvokeMember(name,BindingFlags.InvokeMethod,null,com,args);
        }
        object Call(string name,params object[] args){return CallCom(_com,name,args);}
        int Int(string method,params object[] args){return Convert.ToInt32(Call(method,args),CultureInfo.InvariantCulture);}
        string Single(int col) {return Convert.ToString(Call("GetSingleData",col),CultureInfo.InvariantCulture)??"";}
        string Multi(int row,int col) {return Convert.ToString(Call("GetMultiData",row,col),CultureInfo.InvariantCulture)??"";}

        public void Connect() {
            if(_connected)return;
            if(IntPtr.Size!=4)throw new InvalidOperationException("이 프로그램은 반드시 x86(32비트)로 실행해야 합니다.");
            _host=new IndiHost(PROGID);
            _surface.Controls.Add(_host);
            _host.CreateControl();
            if(!_host.IsHandleCreated)throw new InvalidOperationException("ActiveX native window handle creation failed");
            _com=_host.ComObject;
            if(_com==null)throw new InvalidOperationException("OCX is not loaded");
            Log("ActiveX site handle="+_host.Handle+"; event typelib resolving...");
            _source=ComTypeDiscovery.Discover(_com);
            Log("COM events source IID="+_source.SourceInterface+" names="+string.Join(",",_source.DispIds.Keys));
            Bind("ReceiveData",new Action<object>(x=>OnReceiveData(x)));
            Bind("ReceiveSysMsg",new Action<object>(x=>OnSystemMessage(x)));
            _connected=true;
            Log("C# native AxHost COM event sink connected; waiting for first actual ReceiveData");
        }
        void Bind(string name,Delegate handler) {
            int dispid;
            if(!_source.DispIds.TryGetValue(name,out dispid)) {
                if(name=="ReceiveData")throw new InvalidOperationException("ReceiveData event not listed in the installed typelib");
                Log("Optional event not present: "+name);return;
            }
            ComEventsHelper.Combine(_com,_source.SourceInterface,dispid,handler);
            _sinks.Add(Tuple.Create(dispid,handler));
            Log("COM event bound "+name+" dispid="+dispid);
        }
        public bool Stocks(){return Request("STOCKS","stock_mst",null,"");}
        public bool Bars(string code){return Request("BARS","TR_SCHART",new Dictionary<int,string>{{0,code},{1,"D"},{2,"1"},{3,"00000000"},{4,"99999999"},{5,"45"}},code);}
        public bool Quote(string code){return Request("QUOTE","SC",new Dictionary<int,string>{{0,code}},code);}
        public bool Holdings(string account,string password){
            if(string.IsNullOrWhiteSpace(account)||string.IsNullOrWhiteSpace(password))throw new InvalidOperationException("계좌번호/비밀번호를 PC 화면에서 입력해야 합니다.");
            return Request("HOLDINGS","SABA200QB",new Dictionary<int,string>{{0,account.Trim()},{1,"01"},{2,password}},"");
        }
        private bool Request(string kind,string tr,Dictionary<int,string> input,string code) {
            if(!_connected||_failed)throw new InvalidOperationException("신한 연결이 성공하지 않았거나 오류로 차단됐습니다.");
            if(Busy){Log("Request rejected: previous TR pending");return false;}
            _pendingKind=kind;_pendingCode=code;_requestId=null;
            try {
                var set=Call("SetQueryName",tr);
                if(input!=null)foreach(var field in input.OrderBy(x=>x.Key))Call("SetSingleData",field.Key,field.Value);
                var id=Int("RequestData");
                if(id<0)throw new InvalidOperationException(tr+" returned negative request ID: "+id);
                _requestId=id;
                Log("REQUEST "+tr+" rid="+id+" SetQueryName="+Convert.ToString(set));
                _timeout.Start();return true;
            } catch(Exception ex){StopWithError("Request "+tr+" failed: "+ex.GetBaseException().Message);return false;}
        }
        void OnReceiveData(object raw) {
            var rid=Convert.ToInt32(raw,CultureInfo.InvariantCulture);
            Log("COM ReceiveData rid="+rid+" expected="+(_requestId.HasValue?_requestId.Value.ToString():"pending"));
            if(_pendingKind==null)return;
            if(_requestId.HasValue && rid!=_requestId.Value){Log("Ignored response with different request ID");return;}
            _timeout.Stop();
            var kind=_pendingKind;var code=_pendingCode;
            _pendingKind=null;_pendingCode="";_requestId=null;
            try {
                var rep=new IndiReply{Kind=kind,Code=code};
                if(kind=="STOCKS") {
                    rep.Count=Int("GetMultiRowCount");
                    for(int i=0;i<Math.Min(rep.Count,12000);i++) {
                        var c=Rules.Code(Multi(i,1));
                        var market=Multi(i,2).Trim();var name=Multi(i,3).Trim();var part=Multi(i,14).Trim();
                        if(c!="" && (market=="0"||market=="1") && part=="ST"
                             && Multi(i,6).Trim()=="0" && Multi(i,7).Trim()=="0" && Multi(i,8).Trim()=="0")
                            rep.Stocks.Add(new Stock{Code=c,Name=name,Market=market});
                    }
                    if(rep.Stocks.Count==0)throw new InvalidOperationException("Parsed no ordinary KOSPI/KOSDAQ stocks: check latest stock_mst field layout");
                } else if(kind=="BARS") {
                    rep.Count=Int("GetMultiRowCount");
                    for(int i=0;i<Math.Min(rep.Count,300);i++) {
                        var date=Multi(i,0).Trim();
                        if(date.Length!=8 || !date.All(char.IsDigit))continue;
                        rep.Bars.Add(new Bar{Date=date,Open=Rules.Num(Multi(i,2)),High=Rules.Num(Multi(i,3)),
                            Low=Rules.Num(Multi(i,4)),Close=Rules.Num(Multi(i,5)),Volume=Rules.Num(Multi(i,9))});
                    }
                } else if(kind=="QUOTE") {
                    rep.Code=Rules.Code(Single(1));rep.Price=Math.Abs(Rules.Num(Single(3)));rep.Clock=Single(2).Trim();
                    if(rep.Code==""||rep.Price<=0)throw new InvalidOperationException("Current quote missing symbol or price");
                } else if(kind=="HOLDINGS") {
                    rep.Count=Int("GetMultiRowCount");
                    for(int i=0;i<Math.Min(rep.Count,2000);i++) {
                        var c=Rules.Code(Multi(i,0));if(c=="")continue;
                        rep.Holdings.Add(new Holding{Code=c,Name=Multi(i,1).Trim(),Quantity=Rules.Num(Multi(i,2)),
                           SellPending=Rules.Num(Multi(i,3)),BuyPending=Rules.Num(Multi(i,4)),Price=Rules.Num(Multi(i,5)),
                           AvgPrice=Rules.Num(Multi(i,6)),CreditQty=Rules.Num(Multi(i,7))});
                    }
                }
                Log("RECEIVED "+kind+" rows="+rep.Count+" parsed="+(kind=="STOCKS"?rep.Stocks.Count:kind=="BARS"?rep.Bars.Count:rep.Holdings.Count));
                if(Done!=null)Done(rep);
            } catch(Exception ex){StopWithError("Response parse failed: "+ex.GetBaseException().Message);}
        }
        void OnSystemMessage(object raw){StopWithError("신한 시스템 이벤트: "+Convert.ToString(raw));}
        public void Halt(string reason){StopWithError(reason);}
        void StopWithError(string reason){_timeout.Stop();_pendingKind=null;_failed=true;Log("HALT: "+reason);if(Fatal!=null)Fatal(reason);}
        public void Dispose(){
            _timeout.Stop();_timeout.Dispose();
            if(_com!=null&&_source!=null)foreach(var s in _sinks) {
                try{ComEventsHelper.Remove(_com,_source.SourceInterface,s.Item1,s.Item2);}catch{}
            }
            if(_host!=null){_host.Dispose();_host=null;}
            _com=null;
        }
    }
}
