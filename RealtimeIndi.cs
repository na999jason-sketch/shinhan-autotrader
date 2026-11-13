using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ShinhanNativeTrader {
    [ComImport,Guid("B4A0D92F-6683-4824-9C63-53F7DAD7F57C"),InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    internal interface IIndiRealtimeDispatch {
        [DispId(6),PreserveSig]bool RequestRTReg([MarshalAs(UnmanagedType.Struct)]object type,[MarshalAs(UnmanagedType.Struct)]object code);
        [DispId(7),PreserveSig]bool UnRequestRTReg([MarshalAs(UnmanagedType.Struct)]object type,[MarshalAs(UnmanagedType.Struct)]object code);
        [DispId(18),PreserveSig]bool UnRequestRTRegAll();
    }

    public sealed class RealtimeQuote {
        public string Code,Clock,MarketCode,ClockShape,ClockRawDigits;
        public decimal Price,CumulativeVolume;
        public DateTime ReceivedUtc;
        public bool SequenceConfirmed;
        public IndiReply ToReply(){return new IndiReply{Kind="QUOTE",Code=Code,Price=Price,Clock=Clock,
            HasTradeClock=SequenceConfirmed&&MarketCode=="1",Message=SequenceConfirmed?null:"실시간 시세 연속 수신 확인 대기"};}
        internal RealtimeQuote Copy(){return (RealtimeQuote)MemberwiseClone();}
    }
    public sealed class CashOrderEvent {
        public string ProcessingCode,Code,BrokerOrderNumber,OriginalBrokerOrderNumber,ExecutionId,Clock,OriginalClock,ProductCode,SideCode,TradeTypeCode,OrderStatusCode,OriginalResultCode;
        public int OrderQuantity,CumulativeFilledQuantity,IndividualFillQuantity,ConfirmedQuantity,OpenQuantity;
        public decimal OrderPrice,IndividualFillPrice;
        public DateTime ReceivedUtc;
        public bool IsExecution {get{return ProcessingCode=="03"&&OriginalResultCode=="0"&&(OrderStatusCode=="01"||OrderStatusCode=="02");}}
        public bool IsAmendmentConfirmation {get{return ProcessingCode=="04"&&OriginalResultCode=="0"&&OrderStatusCode=="02";}}
        public bool IsCancellationConfirmation {get{return ProcessingCode=="05"&&OriginalResultCode=="0"&&OrderStatusCode=="02";}}
        public bool IsRejection {get{return OriginalResultCode=="0"&&OrderStatusCode=="03"&&(ProcessingCode=="09"||ProcessingCode=="19"||ProcessingCode=="98"||ProcessingCode=="99");}}
    }
    public sealed class CashBalanceEvent {
        public string Code,StockKindCode;
        public int Quantity,OrderableQuantity,SellPendingQuantity;
        public decimal AveragePrice,Price;
        public DateTime ReceivedUtc;
    }

    public enum RealtimeRecordKind { Event,SC,AA,AD }
    public enum RealtimeValueShape { None,Unavailable,Blank,Digits6,Digits8,Colon8,Digits,Numeric,Other }
    public enum RealtimeFailureCode { FieldUnavailable,InvalidNumber,InvalidQuantity,InvalidSymbol,InvalidBrokerNumber,
        UnsupportedCode,ProfileMismatch,InconsistentQuantities,InvalidClock,InvalidExecution,ReadFailure,SubscriberFailure,EventType }
    // Only parser-owned enums and bounded field positions enter this evidence.
    // Provider values, account identities and exception messages never do.
    public sealed class RealtimeFailureEvidence {
        public RealtimeRecordKind Kind {get;private set;}
        public int FieldIndex {get;private set;}
        public RealtimeFailureCode Code {get;private set;}
        public RealtimeValueShape ValueShape {get;private set;}
        internal RealtimeFailureEvidence(RealtimeRecordKind kind,int index,RealtimeFailureCode code,RealtimeValueShape shape=RealtimeValueShape.None) {
            Kind=kind;FieldIndex=index;Code=code;ValueShape=shape;
        }
        public string SafeDiagnostic {get{return "RealtimeFailure kind="+Kind+"; field="+(FieldIndex<0?"none":FieldIndex.ToString(CultureInfo.InvariantCulture))+"; code="+Code+"; shape="+ValueShape+". Provider values omitted.";}}
    }
    internal sealed class RealtimeRecordException : InvalidOperationException {
        public RealtimeFailureEvidence Evidence {get;private set;}
        internal RealtimeRecordException(string kind,int index,RealtimeFailureCode code,string value=null) {
            var record=kind=="SC"?RealtimeRecordKind.SC:kind=="AA"?RealtimeRecordKind.AA:kind=="AD"?RealtimeRecordKind.AD:RealtimeRecordKind.Event;
            Evidence=new RealtimeFailureEvidence(record,index,code,code==RealtimeFailureCode.FieldUnavailable?RealtimeValueShape.Unavailable:Shape(value));
        }
        static RealtimeValueShape Shape(string value) {
            if(value==null)return RealtimeValueShape.None;
            if(value.Length==0)return RealtimeValueShape.Blank;
            bool digits=value.All(character=>character>='0'&&character<='9');
            if(digits)return value.Length==6?RealtimeValueShape.Digits6:value.Length==8?RealtimeValueShape.Digits8:RealtimeValueShape.Digits;
            if(value.Length==8&&value[2]==':'&&value[5]==':'
                &&(value.Substring(0,2)+value.Substring(3,2)+value.Substring(6,2)).All(character=>character>='0'&&character<='9'))return RealtimeValueShape.Colon8;
            decimal number;
            return decimal.TryParse(value,NumberStyles.Number,CultureInfo.InvariantCulture,out number)?RealtimeValueShape.Numeric:RealtimeValueShape.Other;
        }
    }

    // Only documented fields are read, inside the ReceiveRTData callback. Names,
    // account numbers and arbitrary provider descriptions never leave this parser.
    public static class RealtimeRecordParser {
        static string Field(Func<int,object> read,int index,string kind){
            try{return (Convert.ToString(read(index),CultureInfo.InvariantCulture)??"").Trim();}
            catch{throw new RealtimeRecordException(kind,index,RealtimeFailureCode.FieldUnavailable);}
        }
        static decimal Number(string raw,string kind,int index,bool signed=false){
            try{return IndiResponseParser.RequiredNumber(raw,"realtime number",signed);}
            catch{throw new RealtimeRecordException(kind,index,RealtimeFailureCode.InvalidNumber,raw);}
        }
        static int Quantity(string raw,string kind,int index){
            var value=Number(raw,kind,index);
            if(value!=decimal.Truncate(value)||value>int.MaxValue)throw new RealtimeRecordException(kind,index,RealtimeFailureCode.InvalidQuantity,raw);
            return (int)value;
        }
        static string Symbol(string raw,string kind,int index){
            var result=Rules.Code(raw);if(result=="")throw new RealtimeRecordException(kind,index,RealtimeFailureCode.InvalidSymbol,raw);return result;
        }
        static string BrokerNumber(string raw,bool optional,string kind,int index){
            if(optional&&(raw==""||raw.All(c=>c=='0')))return "";
            if(!LiveOrderProtocol.ValidBrokerNumber(raw))throw new RealtimeRecordException(kind,index,RealtimeFailureCode.InvalidBrokerNumber,raw);return raw;
        }
        static void Utc(DateTime value){if(value.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC realtime receipt time required.");}
        static bool AccountMatches(string incoming,string expected){return expected!=null&&incoming.Replace("-","")==expected;}
        public static string NormalizeExecutionClock(string raw) {
            var value=(raw??"").Trim();
            // Preserve the broker's clock; receipt time never chooses a slice
            // or supplies a missing date. The two numeric eight-digit forms
            // have disjoint regular-session interpretations.
            var shape=RealtimeDiagnostics.ClockShape(value);
            return shape=="digits6"||shape=="digits8"?RealtimeDiagnostics.NormalizeClock(value):"";
        }
        public static RealtimeQuote ReadQuote(Func<int,object> read,DateTime utc){
            Utc(utc);var code=Symbol(Field(read,1,"SC"),"SC",1);var clock=Field(read,2,"SC");
            var price=Math.Abs(Number(Field(read,3,"SC"),"SC",3,true));
            if(price<=0)throw new RealtimeRecordException("SC",3,RealtimeFailureCode.InvalidNumber);
            var volume=Number(Field(read,7,"SC"),"SC",7);
            if(volume!=decimal.Truncate(volume))throw new RealtimeRecordException("SC",7,RealtimeFailureCode.InvalidQuantity);
            return new RealtimeQuote{Code=code,Clock=RealtimeDiagnostics.NormalizeClock(clock),ClockShape=RealtimeDiagnostics.ClockShape(clock),ClockRawDigits=RealtimeDiagnostics.RawClockDigits(clock),Price=price,
                CumulativeVolume=volume,MarketCode=Field(read,17,"SC"),ReceivedUtc=utc};
        }
        public static CashOrderEvent ReadOrder(Func<int,object> read,string account,DateTime utc){
            Utc(utc);if(!AccountMatches(Field(read,2,"AA"),account)||Field(read,4,"AA")!="01")return null;
            var processing=Field(read,0,"AA");
            if(!new[]{"00","03","04","05","09","19","98","99"}.Contains(processing))throw new RealtimeRecordException("AA",0,RealtimeFailureCode.UnsupportedCode,processing);
            var rejected=processing=="09"||processing=="19"||processing=="98"||processing=="99";
            var result=new CashOrderEvent{ProcessingCode=processing,Code=Symbol(Field(read,11,"AA"),"AA",11),ProductCode="01",
                BrokerOrderNumber=BrokerNumber(Field(read,5,"AA"),rejected,"AA",5),OriginalBrokerOrderNumber=BrokerNumber(Field(read,6,"AA"),true,"AA",6),
                SideCode=Field(read,8,"AA"),TradeTypeCode=Field(read,9,"AA"),OrderStatusCode=Field(read,10,"AA"),OriginalResultCode=Field(read,32,"AA"),
                OrderQuantity=Quantity(Field(read,14,"AA"),"AA",14),
                OrderPrice=Math.Abs(Number(Field(read,15,"AA"),"AA",15,true)),
                CumulativeFilledQuantity=Quantity(Field(read,16,"AA"),"AA",16),
                ConfirmedQuantity=Quantity(Field(read,19,"AA"),"AA",19),
                OpenQuantity=Quantity(Field(read,20,"AA"),"AA",20),ReceivedUtc=utc};
            if(result.SideCode!="01"&&result.SideCode!="02")throw new RealtimeRecordException("AA",8,RealtimeFailureCode.UnsupportedCode,result.SideCode);
            if(result.TradeTypeCode!=(result.SideCode=="01"?"01":"02"))throw new RealtimeRecordException("AA",9,RealtimeFailureCode.ProfileMismatch,result.TradeTypeCode);
            if(result.OrderStatusCode!="01"&&result.OrderStatusCode!="02"&&result.OrderStatusCode!="03")throw new RealtimeRecordException("AA",10,RealtimeFailureCode.UnsupportedCode,result.OrderStatusCode);
            if(result.OriginalResultCode!="0"&&result.OriginalResultCode!="1")throw new RealtimeRecordException("AA",32,RealtimeFailureCode.UnsupportedCode,result.OriginalResultCode);
            if((processing=="04"||processing=="05")&&result.OrderStatusCode!="02"||rejected&&result.OrderStatusCode!="03"||processing=="03"&&result.OrderStatusCode=="03")
                throw new RealtimeRecordException("AA",10,RealtimeFailureCode.ProfileMismatch,result.OrderStatusCode);
            if(result.CumulativeFilledQuantity>result.OrderQuantity)throw new RealtimeRecordException("AA",16,RealtimeFailureCode.InconsistentQuantities);
            if(result.OpenQuantity>result.OrderQuantity)throw new RealtimeRecordException("AA",20,RealtimeFailureCode.InconsistentQuantities);
            if(result.IsExecution){
                result.ExecutionId=Field(read,22,"AA");
                if(result.ExecutionId.Length==0||result.ExecutionId.Length>64||result.ExecutionId.All(c=>c=='0')||result.ExecutionId.Any(c=>!(char.IsLetterOrDigit(c)||c=='-'||c=='_')))
                    throw new RealtimeRecordException("AA",22,RealtimeFailureCode.InvalidExecution,result.ExecutionId);
                result.IndividualFillQuantity=Quantity(Field(read,23,"AA"),"AA",23);
                result.IndividualFillPrice=Math.Abs(Number(Field(read,24,"AA"),"AA",24,true));
                var rawClock=Field(read,25,"AA");
                result.Clock=NormalizeExecutionClock(rawClock);
                if(result.IndividualFillQuantity<=0||result.IndividualFillQuantity>result.OrderQuantity)
                    throw new RealtimeRecordException("AA",23,RealtimeFailureCode.InvalidQuantity);
                if(result.IndividualFillPrice<=0)throw new RealtimeRecordException("AA",24,RealtimeFailureCode.InvalidNumber);
                if(result.Clock=="")throw new RealtimeRecordException("AA",25,RealtimeFailureCode.InvalidClock,rawClock);
                result.OriginalClock=rawClock;
            }else{result.ExecutionId="";result.Clock=Field(read,21,"AA");}
            return result;
        }
        public static CashBalanceEvent ReadBalance(Func<int,object> read,string account,DateTime utc){
            Utc(utc);if(!AccountMatches(Field(read,0,"AD"),account)||Field(read,1,"AD")!="01")return null;
            return new CashBalanceEvent{Code=Symbol(Field(read,2,"AD"),"AD",2),StockKindCode=Field(read,3,"AD"),
                Quantity=Quantity(Field(read,6,"AD"),"AD",6),AveragePrice=Number(Field(read,7,"AD"),"AD",7),
                OrderableQuantity=Quantity(Field(read,8,"AD"),"AD",8),SellPendingQuantity=Quantity(Field(read,9,"AD"),"AD",9),
                Price=Math.Abs(Number(Field(read,20,"AD"),"AD",20,true)),ReceivedUtc=utc};
        }
    }

    // Pure callback processing and quote-sequence state are testable without COM.
    public sealed class RealtimeEventProcessor : IDisposable {
        readonly Func<int,object> read;
        readonly Dictionary<string,RealtimeQuote> quotes=new Dictionary<string,RealtimeQuote>(StringComparer.Ordinal);
        string account;bool disposed;
        public bool Failed {get;private set;}
        public RealtimeFailureEvidence LastFailure {get;private set;}
        public bool AccountStreamObserved {get;private set;}
        public RealtimeDiagnostics Diagnostics {get;}=new RealtimeDiagnostics();
        public event Action<RealtimeQuote> QuoteReceived;
        public event Action<CashOrderEvent> OrderReceived;
        public event Action<CashBalanceEvent> BalanceReceived;
        public event Action<string> Faulted;
        public RealtimeEventProcessor(Func<int,object> read){this.read=read??throw new ArgumentNullException("read");}
        void Usable(){if(disposed)throw new ObjectDisposedException("RealtimeEventProcessor");if(Failed)throw new InvalidOperationException("Realtime processing failed; create a new session.");}
        public void BindAccount(string number){Usable();if(number==null||number.Length!=11||number.Any(c=>c<'0'||c>'9'))throw new ArgumentException("An eleven digit account is required.");if(account!=null&&account!=number)throw new InvalidOperationException("Create a new realtime session to change accounts.");account=number;}
        public void Track(string code){Usable();if(Rules.Code(code)!=code||string.IsNullOrEmpty(code))throw new ArgumentException("A normalized stock symbol is required.");if(!quotes.ContainsKey(code))quotes[code]=null;}
        public void Untrack(string code){quotes.Remove(code);if(Diagnostics.LastCode==code)Diagnostics.Invalidate();}
        public void Invalidate(){foreach(var code in quotes.Keys.ToList())quotes[code]=null;AccountStreamObserved=false;Diagnostics.Invalidate();}
        public void Fail(string reason){if(Failed||disposed)return;Failed=true;Invalidate();try{if(Faulted!=null)Faulted(reason);}catch{}}
        public void Process(object kind,DateTime utc){
            if(disposed||Failed)return;
            Diagnostics.RecordCallback(kind,utc);
            var type=kind as string;
            if(type==null){LastFailure=new RealtimeFailureEvidence(RealtimeRecordKind.Event,-1,RealtimeFailureCode.EventType);Fail(LastFailure.SafeDiagnostic);return;}
            bool publishing=false;
            try {
                if(type=="SC") {
                    var quote=RealtimeRecordParser.ReadQuote(read,utc);RealtimeQuote previous;
                    if(!quotes.TryGetValue(quote.Code,out previous)){Diagnostics.ObserveQuote(quote,RealtimeQuoteState.Untracked);return;}
                    bool validClock=!string.IsNullOrEmpty(quote.Clock)&&quote.MarketCode=="1";
                    bool advancing=previous!=null&&previous.MarketCode=="1"&&!string.IsNullOrEmpty(previous.Clock)&&validClock
                        &&((string.CompareOrdinal(quote.Clock,previous.Clock)>0&&quote.CumulativeVolume>=previous.CumulativeVolume)
                            ||quote.Clock==previous.Clock&&quote.CumulativeVolume>previous.CumulativeVolume);
                    bool regressed=previous!=null&&(string.CompareOrdinal(quote.Clock,previous.Clock)<0||quote.CumulativeVolume<previous.CumulativeVolume);
                    quote.SequenceConfirmed=validClock&&!regressed&&(advancing||previous!=null&&previous.SequenceConfirmed);
                    var state=string.IsNullOrEmpty(quote.Clock)?RealtimeQuoteState.InvalidClock:
                        quote.MarketCode!="1"?RealtimeQuoteState.NonRegularMarket:regressed?RealtimeQuoteState.Regressed:
                        quote.SequenceConfirmed?RealtimeQuoteState.Confirmed:previous==null?RealtimeQuoteState.FirstTick:RealtimeQuoteState.Replay;
                    Diagnostics.ObserveQuote(quote,state);
                    quotes[quote.Code]=quote.Copy();publishing=true;if(QuoteReceived!=null)QuoteReceived(quote.Copy());
                }else if(type=="AA") {
                    var value=RealtimeRecordParser.ReadOrder(read,account,utc);if(value==null)return;AccountStreamObserved=true;publishing=true;if(OrderReceived!=null)OrderReceived(value);
                }else if(type=="AD") {
                    var value=RealtimeRecordParser.ReadBalance(read,account,utc);if(value==null)return;AccountStreamObserved=true;publishing=true;if(BalanceReceived!=null)BalanceReceived(value);
                }
            }catch(Exception error){
                var record=error as RealtimeRecordException;
                var recordKind=type=="SC"?RealtimeRecordKind.SC:type=="AA"?RealtimeRecordKind.AA:type=="AD"?RealtimeRecordKind.AD:RealtimeRecordKind.Event;
                LastFailure=!publishing&&record!=null?record.Evidence:new RealtimeFailureEvidence(recordKind,-1,
                    publishing?RealtimeFailureCode.SubscriberFailure:RealtimeFailureCode.ReadFailure);
                Fail(LastFailure.SafeDiagnostic);
            }
        }
        public bool TryGetQuote(string code,DateTime utc,out RealtimeQuote quote){
            if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC quote evaluation time required.");
            quote=null;RealtimeQuote value;if(disposed||Failed||!quotes.TryGetValue(code,out value)||value==null||value.ReceivedUtc>utc||utc-value.ReceivedUtc>TimeSpan.FromSeconds(45)||!MarketTime.CanUseQuote(value.ToReply(),utc))return false;
            quote=value.Copy();return true;
        }
        public void Dispose(){if(disposed)return;disposed=true;quotes.Clear();account=null;QuoteReceived=null;OrderReceived=null;BalanceReceived=null;Faulted=null;}
    }

    public sealed class RealtimeIndi : IDisposable {
        readonly Control surface;readonly Action<string> trace;readonly int ownerThread;
        readonly HashSet<string> watch=new HashSet<string>(StringComparer.Ordinal);
        readonly RealtimeEventProcessor processor;
        IndiHost host;object native;IIndiReadOnlyDispatch read;IIndiRealtimeDispatch registration;ComEventInfo metadata;ComEventSubscriptions subscriptions;
        string account;bool disposed;volatile bool failed;
        public event Action<RealtimeQuote> QuoteReceived;
        public event Action<CashOrderEvent> OrderReceived;
        public event Action<CashBalanceEvent> BalanceReceived;
        public event Action<string> Faulted;
        public event Action<string> Invalidated;
        public bool Connected {get{return !disposed&&!Failed&&native!=null;}}
        public bool Failed {get{return failed||processor.Failed;}}
        public bool AccountSubscribed {get;private set;}
        public RealtimeFailureEvidence LastFailure {get{return processor==null?null:processor.LastFailure;}}
        public bool AccountStreamObserved {get{return processor.AccountStreamObserved;}}
        public int QuoteSubscriptionCount {get{return watch.Count;}}
        public string DiagnosticsSummary(DateTime utc){return processor.Diagnostics.Describe(utc);}
        public RealtimeIndi(Control surface,Action<string> trace){
            this.surface=surface??throw new ArgumentNullException("surface");this.trace=trace??(message=>{});ownerThread=Thread.CurrentThread.ManagedThreadId;
            processor=new RealtimeEventProcessor(index=>read.GetSingleData(checked((short)index)));
            processor.QuoteReceived+=value=>{if(QuoteReceived!=null)QuoteReceived(value);};processor.OrderReceived+=value=>{if(OrderReceived!=null)OrderReceived(value);};processor.BalanceReceived+=value=>{if(BalanceReceived!=null)BalanceReceived(value);};processor.Faulted+=Fail;
            if(IntPtr.Size!=4||Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA||surface.InvokeRequired)throw new InvalidOperationException("Indi realtime requires the owning Windows x86 UI STA.");
            try {
                host=new IndiHost("GIEXPERTCONTROL.GiExpertControlCtrl.1",Attach,CreateSink,Detach);
                ((ISupportInitialize)host).BeginInit();surface.Controls.Add(host);((ISupportInitialize)host).EndInit();host.CreateControl();
                if(native==null||subscriptions==null||subscriptions.Count!=2||!host.IsHandleCreated)throw new InvalidOperationException("Realtime ActiveX site initialization failed.");
                string starter=IndiInstallation.FindStarter(Type.GetTypeFromProgID("GIEXPERTCONTROL.GiExpertControlCtrl.1",true).GUID);
                if(starter==null||!new IndiReadOnlyDispatch(native).Initialize(starter))throw new InvalidOperationException("Realtime Indi initialization failed.");
                this.trace("Dedicated realtime OCX initialized; SC/AA/AD callbacks not yet observed.");
            }catch{Dispose();throw;}
        }
        void Attach(object control){native=control;metadata=ComTypeDiscovery.Discover(control);read=(IIndiReadOnlyDispatch)control;registration=(IIndiRealtimeDispatch)control;}
        void CreateSink(object control){
            Detach();if(metadata==null)Attach(control);
            ComMethodInfo method;
            foreach(var name in new[]{"RequestRTReg","UnRequestRTReg"})if(!metadata.Methods.TryGetValue(name,out method)||method.Parameters!=2||method.ReturnType!=VarEnum.VT_BOOL)throw new InvalidOperationException("Installed realtime registration signature is unsupported.");
            if(!metadata.Methods.TryGetValue("UnRequestRTRegAll",out method)||method.Parameters!=0||method.ReturnType!=VarEnum.VT_BOOL)throw new InvalidOperationException("Installed realtime unregister signature is unsupported.");
            int id;VarEnum argument;
            if(!metadata.DispIds.TryGetValue("ReceiveRTData",out id)||!metadata.ArgumentTypes.TryGetValue("ReceiveRTData",out argument)||argument!=VarEnum.VT_VARIANT)throw new InvalidOperationException("Installed ReceiveRTData must use its verified VARIANT event contract.");
            subscriptions=new ComEventSubscriptions(control,metadata.SourceInterface);
            try {
                subscriptions.AddVariantCallback(id,argument,OnRealtime);
                if(!metadata.DispIds.TryGetValue("ReceiveSysMsg",out id)||!metadata.ArgumentTypes.TryGetValue("ReceiveSysMsg",out argument))throw new InvalidOperationException("Installed realtime system event is unavailable.");
                subscriptions.AddInt32Callback(id,argument,OnSystem);
            }catch{Detach();throw;}
        }
        void Owner(){if(disposed)throw new ObjectDisposedException("RealtimeIndi");if(Thread.CurrentThread.ManagedThreadId!=ownerThread)throw new InvalidOperationException("Use realtime on its owning STA.");if(Failed)throw new InvalidOperationException("Realtime channel failed; reconnect explicitly.");}
        public void AttachAccount(string number){
            Owner();processor.BindAccount(number);if(account!=null&&account!=number)throw new InvalidOperationException("Create a new realtime channel to change accounts.");if(AccountSubscribed)return;
            account=number;
            try {
                if(!registration.RequestRTReg("AA",account)||!registration.RequestRTReg("AD",account))throw new InvalidOperationException("Account realtime registration was rejected.");
                AccountSubscribed=true;trace("AA/AD account subscriptions accepted; event delivery not yet verified.");
            }catch{try{registration.UnRequestRTReg("AA",account);registration.UnRequestRTReg("AD",account);}catch{}Fail("실계좌 실시간 등록 실패. 공급자 원문 생략.");throw new InvalidOperationException("Account realtime registration failed.");}
        }
        public bool EnsureQuote(string code){
            Owner();code=Rules.Code(code);if(code=="")throw new ArgumentException("A six digit stock symbol is required.");if(watch.Contains(code))return true;if(watch.Count>=200)return false;
            processor.Track(code);
            try{if(!registration.RequestRTReg("SC",code)){processor.Untrack(code);return false;}watch.Add(code);return true;}
            catch{processor.Untrack(code);Fail("실시간 종목 등록 실패. 공급자 원문 생략.");throw new InvalidOperationException("Realtime quote registration failed.");}
        }
        public void RemoveQuote(string code){Owner();code=Rules.Code(code);if(!watch.Contains(code))return;if(!registration.UnRequestRTReg("SC",code))throw new InvalidOperationException("Realtime quote unregister failed.");watch.Remove(code);processor.Untrack(code);}
        public bool TryGetQuote(string code,DateTime utc,out RealtimeQuote quote){quote=null;return !Failed&&!disposed&&processor.TryGetQuote(Rules.Code(code),utc,out quote);}
        void OnRealtime(object kind){
            if(disposed||Failed)return;
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread){failed=true;try{surface.BeginInvoke(new Action(()=>Fail("실시간 수신이 소유 STA 외부에서 도착했습니다.")));}catch{}return;}
            processor.Process(kind,DateTime.UtcNow);
        }
        void OnSystem(int code){
            if(disposed)return;
            if(Thread.CurrentThread.ManagedThreadId!=ownerThread){failed=true;try{surface.BeginInvoke(new Action(()=>Fail("실시간 시스템 수신이 소유 STA 외부에서 도착했습니다.")));}catch{}return;}
            try{processor.Invalidate();trace("Realtime system notification="+code+"; previous quote sequence invalidated.");if(Invalidated!=null)Invalidated("Indi realtime system notification.");}
            catch{Fail("실시간 시스템 알림 처리 실패. 공급자 원문 생략.");}
        }
        void Fail(string reason){if(disposed)return;failed=true;AccountSubscribed=false;processor.Invalidate();try{if(Faulted!=null)Faulted(reason);}catch{}}
        void Detach(){if(subscriptions==null)return;var previous=subscriptions;subscriptions=null;previous.Dispose();}
        public void Dispose(){
            if(disposed)return;disposed=true;AccountSubscribed=false;
            try{if(registration!=null)try{registration.UnRequestRTRegAll();}catch{}Detach();}
            finally{processor.Dispose();watch.Clear();account=null;try{if(host!=null){surface.Controls.Remove(host);host.Dispose();host=null;}}finally{native=null;read=null;registration=null;}}
        }
    }
}
