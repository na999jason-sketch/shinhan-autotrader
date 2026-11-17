using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using ShinhanNativeTrader;

// The actual form receives isolated SC callbacks, evaluates entry conditions,
// and prepares only a synthetic order. No native control or broker is used.
static class StrategyConfirmationUiCases {
    const string Account="12345678901",Symbol="000660";
    static readonly DateTime Start=new DateTime(2026,11,17,1,0,0,DateTimeKind.Utc);
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static T Get<T>(object owner,string name){return (T)owner.GetType().GetField(name,Fields).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Fields).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] values) {
        try{return owner.GetType().GetMethod(name,Fields).Invoke(owner,values);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    sealed class ReadTransport : IIndiTransport {
        string query;
        public int Requests;
        public event Action<int> ReceiveData {add{} remove{}}
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public object Invoke(string method,params object[] values) {
            if(method=="SetQueryName"){query=(string)values[0];return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData")return true;
            if(method=="RequestData")return (short)(100+(++Requests));
            throw new InvalidOperationException("Unexpected isolated entry read: "+method);
        }
        public void Dispose(){}
    }
    sealed class NoOrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Sends,Preparations;
        public event Action<LiveOrderAcknowledgement> Acknowledged {add{} remove{}}
        public void PrepareChannel(){Preparations++;}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;throw new InvalidOperationException("Entry strategy tests must never transmit an order.");
        }
        public void Dispose(){}
    }
    sealed class Fixture : IDisposable {
        public readonly MainForm Form;
        public readonly LiveOrderJournal Journal;
        public readonly ReadTransport Reads=new ReadTransport();
        public readonly NoOrderWire Orders=new NoOrderWire();
        public readonly PremarketReference Reference;
        public readonly RealtimeEventProcessor Processor;
        public readonly string Key;
        readonly Dictionary<int,object> fields=new Dictionary<int,object>();
        decimal volume=2000;
        public RealtimeQuote Latest;
        public Fixture(string data,string name) {
            Form=new MainForm();Get<Timer>(Form,"scheduler").Stop();Form.ShowInTaskbar=false;Form.Show();Application.DoEvents();Get<Timer>(Form,"scheduler").Stop();
            Get<NumericUpDown>(Form,"budget").Value=1000000;Call(Form,"SaveSettings");
            var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            var credentials=Activator.CreateInstance(type,new object[]{Account,"1111"});
            Key=(string)type.GetProperty("Key").GetValue(credentials,null);
            Set(Form,"credentials",credentials);Set(Form,"boundKey",Key);Set(Form,"lastReportKey",Key);Set(Form,"monitoring",true);
            Set(Form,"researchRequested",false);Set(Form,"researchComplete",true);Set(Form,"researchDateKorea",MarketTime.ToKorea(Start).Date);
            Set(Form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Set(Form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Journal=new LiveOrderJournal(Path.Combine(data,"entry-confirmation-ui-"+name+".xml"));Journal.BindAccount(Account);
            Set(Form,"journal",Journal);Set(Form,"gateway",new LiveOrderGateway(Journal,Orders));
            var readiness=new BrokerReadiness(Key,Journal.Snapshot.AccountFingerprint);readiness.SetAuthorization(true);readiness.SetAccountStream(true,Start.AddMinutes(-2));Set(Form,"readiness",readiness);
            var api=new IndiApi(Get<Panel>(Form,"nativeArea"));Set(api,"requests",new IndiRequestCoordinator(Reads));Set(api,"hasReceivedData",true);Set(Form,"api",api);
            var session=Get<UnattendedSession>(Form,"session");session.Arm(Start);session.Tick(Start,false);session.ScanCompleted(Start);
            Get<LiveTradingSession>(Form,"authorization").Authorize(Account,Start);
            var stock=new Stock{Code=Symbol,Name="ISOLATED STRATEGY",Market="0"};Get<List<Stock>>(Form,"symbols").Add(stock);
            Get<Dictionary<string,CashInstrument>>(Form,"instruments")[Symbol]=new CashInstrument{Code=Symbol,Market="0",Product="ST"};
            var dates=new List<DateTime>();var date=MarketTime.ToKorea(Start).Date.AddDays(-1);
            while(dates.Count<45){if(date.DayOfWeek!=DayOfWeek.Saturday&&date.DayOfWeek!=DayOfWeek.Sunday)dates.Add(date);date=date.AddDays(-1);}
            dates.Reverse();var bars=dates.Select((day,index)=>new Bar{Date=day.ToString("yyyyMMdd",CultureInfo.InvariantCulture),
                Open=10000+index*10,High=10010+index*10,Low=9990+index*10,Close=10000+index*10,Volume=1000}).ToArray();
            Reference=PremarketResearch.Create(stock,bars,Get<AccountAppSettings>(Form,"configuration").Strategy,MarketTime.ToKorea(Start).Date);
            if(Reference==null)throw new InvalidOperationException("Synthetic entry reference was not created.");
            Get<Dictionary<string,PremarketReference>>(Form,"preparedReferences")[Symbol]=Reference;
            Get<List<PremarketReference>>(Form,"watchedReferences").Add(Reference);
            Get<DataGridView>(Form,"candidatesGrid").Rows.Add(Symbol,stock.Name,Reference.PriorHigh,Reference.RankScore,"completed bars","waiting");
            Processor=new RealtimeEventProcessor(index=>fields[index]);Processor.BindAccount(Account);Processor.Track(Symbol);
            Processor.QuoteReceived+=(Action<RealtimeQuote>)Delegate.CreateDelegate(typeof(Action<RealtimeQuote>),Form,typeof(MainForm).GetMethod("OnRealtimeQuote",Fields));
            Processor.QuoteReceived+=quote=>Latest=quote;
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",Processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){Symbol});Set(realtime,"native",new object());
            Set(realtime,"ownerThread",System.Threading.Thread.CurrentThread.ManagedThreadId);
            Set(realtime,"<AccountSubscribed>k__BackingField",true);Set(Form,"realtime",realtime);Get<HashSet<string>>(Form,"trackedQuotes").Add(Symbol);
            RefreshAccount(Start);
            Quote(Start.AddSeconds(-1),Reference.PriorHigh);
        }
        public object Pending {get{return Get<object>(Form,"entry");}}
        public LiveOrderIntent Intent {get{return Pending==null?null:Get<LiveOrderIntent>(Pending,"Intent");}}
        public string Status {get{return Convert.ToString(Get<DataGridView>(Form,"candidatesGrid").Rows[0].Cells["주문상태"].Value,CultureInfo.InvariantCulture);}}
        public decimal Price {get{return Reference.PriorHigh+10;}}
        public void RefreshAccount(DateTime at) {
            var balance=new BrokerHoldingsSnapshot{BasisVerified=true,RequestBasisVerified=true,RequestStartedUtc=at.AddMilliseconds(-10),ReceivedUtc=at};
            Set(Form,"tradeHoldings",balance);Set(Form,"holdingsReceivedUtc",(DateTime?)at);
            var snapshot=new BrokerOrdersSnapshot{TradingDate=MarketTime.ToKorea(at).ToString("yyyyMMdd",CultureInfo.InvariantCulture)};
            Call(snapshot,"BindRequestScope",at.AddMilliseconds(-10),at,true,Account);Set(Form,"brokerOrders",snapshot);
            Set(Form,"cash",new AccountCashSnapshot{DepositCash=1000000,Cash100Orderable=1000000,ReceivedUtc=at});
        }
        public void Quote(DateTime at,decimal price) {
            fields[1]=Symbol;fields[2]=MarketTime.ToKorea(at).ToString("HHmmss",CultureInfo.InvariantCulture);
            fields[3]=price;fields[7]=volume++;fields[17]="1";Processor.Process("SC",at);
        }
        public DateTime Hold(DateTime start,decimal? price=null) {
            int seconds=Get<AccountAppSettings>(Form,"configuration").Strategy.BreakoutConfirmationSeconds;
            for(int offset=0;offset<=seconds;offset+=10)Quote(start.AddSeconds(offset),price??Price);
            if(seconds%10!=0)Quote(start.AddSeconds(seconds),price??Price);
            return start.AddSeconds(seconds);
        }
        public void Evaluate(DateTime at){Call(Form,"EvaluatePreparedSignals",at);}
        public void Tick(DateTime at){Call(Form,"ScheduleAt",at);}
        public Candidate Confirmed(DateTime at) {
            var arguments=new object[]{Reference,Latest,at,null};
            return (Candidate)typeof(MainForm).GetMethod("EvaluateEntrySignal",Fields).Invoke(Form,arguments);
        }
        public void SizingInputs(DateTime at,decimal heldCost,decimal cashAmount,decimal capacityAmount,int capacityQuantity=100) {
            var positions=Get<List<Holding>>(Form,"holdings");positions.Clear();
            if(heldCost>0)positions.Add(new Holding{Code="005930",Name="ISOLATED SIZING HOLDING",Quantity=heldCost/10000m,
                AvgPrice=10000m,Price=10000m});
            Set(Form,"cash",new AccountCashSnapshot{DepositCash=cashAmount,Cash100Orderable=cashAmount,ReceivedUtc=at});
            if(Pending==null)throw new InvalidOperationException("Sizing fixture requires a confirmed unsent buy.");
            Set(Pending,"Capacity",new AccountOrderCapacity{Code=Symbol,LimitPrice=Price,Ratio=100,
                Quantity=capacityQuantity,MaximumQuantity=capacityQuantity,Amount=capacityAmount,MaximumAmount=capacityAmount,ReceivedUtc=at});
        }
        public LiveOrderRecord Accepted(LiveOrderIntent intent,int requestId,string brokerNumber,DateTime at) {
            var order=Journal.Prepare(intent,at);Journal.MarkDispatching(order.Id,at);Journal.MarkRequestAccepted(order.Id,requestId,at);
            var response=new[]{"0",brokerNumber,"0","","1","isolated accepted","",""};
            var evidence=LiveOrderResponseParser.Read(requestId,index=>response[index],new IndiDiagnosticSnapshot{ErrorState=0},
                OrderResponseLayout.Modern,new[]{Account,"1111"});
            Journal.RecordAcknowledgement(order.Id,requestId,brokerNumber,false,at,evidence);
            return Journal.Snapshot.Orders.Single(value=>value.Id==order.Id);
        }
        public void Dispose(){Form.Close();Form.Dispose();Journal.Dispose();}
    }
    static void RejectChaseAndTransient(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"overheat")) {
            var at=fixture.Hold(Start,11000);fixture.Evaluate(at);fixture.Tick(at);
            check(fixture.Status.Contains("추격")||fixture.Status.Contains("과열"),"the actual candidate row explains an overextended breakout exclusion");
            check(fixture.Pending==null&&Get<Queue<Candidate>>(fixture.Form,"buyQuotes").Count==0&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                "a sustained but overpriced breakout cannot reach order preparation");
        }
        using(var fixture=new Fixture(data,"transient")) {
            fixture.Quote(Start,fixture.Price);fixture.Evaluate(Start);
            check(fixture.Status.Contains("돌파")&&fixture.Pending==null&&Get<Queue<Candidate>>(fixture.Form,"buyQuotes").Count==0,
                "one fresh sequence-confirmed breakout displays a confirmation wait rather than an actionable buy");
            for(int offset=10;offset<=60;offset+=10)fixture.Tick(Start.AddSeconds(offset));
            check(fixture.Pending==null&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                "scheduler passage without new observed trades cannot age a cached breakout into a buy");
        }
    }
    static void SustainedEntryAndDip(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"steady")) {
            var ready=fixture.Hold(Start);fixture.RefreshAccount(ready);
            fixture.Processor.Track("005930");
            Get<HashSet<string>>(Get<RealtimeIndi>(fixture.Form,"realtime"),"watch").Add("005930");
            Get<HashSet<string>>(fixture.Form,"trackedQuotes").Add("005930");
            var source=new BrokerHoldingsSnapshot{BasisVerified=true,RequestBasisVerified=true,RequestStartedUtc=ready.AddMilliseconds(-10),ReceivedUtc=ready};
            source.Holdings.Add(new Holding{Code="005930",Name="ISOLATED OTHER",Quantity=1,AvgPrice=10000,Price=10000});
            source.BasisValidated["005930"]=true;source.CashSourceValidated["005930"]=true;source.SellableQuantities["005930"]=1;source.UnfilledQuantities["005930"]=0;
            Call(fixture.Form,"ApplyReply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=source,RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});
            check(fixture.Confirmed(ready)!=null,"fresh other-symbol holdings preserve a fully observed breakout confirmation");
            // This other holding is only a read-refresh regression, not a
            // tracked quote or a simulated additional entry.
            Get<List<Holding>>(fixture.Form,"holdings").Clear();fixture.Tick(ready);
            check(fixture.Intent!=null&&fixture.Intent.Kind==LiveOrderKind.Buy&&fixture.Intent.Code==Symbol&&fixture.Intent.LimitPrice==fixture.Price,
                "a continuously observed breakout reaches the actual scheduler with the current quote price");
            var reads=Get<Queue<string>>(fixture.Pending,"Reads");
            check(new[]{"BARS","CASH","TRADEHOLDINGS","ORDERS","CAPACITY"}.All(reads.Contains)&&fixture.Orders.Preparations==1&&fixture.Orders.Sends==0,
                "confirmed entry still requires every existing financial preflight and sends no order from confirmation alone");
        }
        using(var fixture=new Fixture(data,"dip-reset")) {
            for(int offset=0;offset<=40;offset+=10)fixture.Quote(Start.AddSeconds(offset),fixture.Price);
            fixture.Quote(Start.AddSeconds(45),fixture.Reference.PriorHigh);
            for(int offset=50;offset<=100;offset+=10)fixture.Quote(Start.AddSeconds(offset),fixture.Price);
            fixture.Evaluate(Start.AddSeconds(100));
            check(fixture.Confirmed(Start.AddSeconds(100))==null&&Get<Queue<Candidate>>(fixture.Form,"buyQuotes").Count==0,
                "a drop back to the breakout line resets the earlier confirmation time");
            fixture.Quote(Start.AddSeconds(110),fixture.Price);fixture.Evaluate(Start.AddSeconds(110));
            check(fixture.Confirmed(Start.AddSeconds(110))!=null&&Get<Queue<Candidate>>(fixture.Form,"buyQuotes").Count==1,
                "entry becomes eligible only after a complete new observed hold following the failed breakout");
        }
    }
    static void FinalRecheckAndSettings(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"final-latest-quote")) {
            var ready=fixture.Hold(Start);fixture.RefreshAccount(ready);fixture.Tick(ready);
            var changed=ready.AddSeconds(1);fixture.Quote(changed,fixture.Price);
            Set(fixture.Pending,"Capacity",new AccountOrderCapacity{Code=Symbol,LimitPrice=fixture.Price,Ratio=100,
                Quantity=100,MaximumQuantity=100,Amount=1000000,MaximumAmount=1000000,ReceivedUtc=ready});
            Call(fixture.Form,"SubmitPending",changed);
            check(fixture.Pending!=null&&fixture.Intent.Kind==LiveOrderKind.Buy&&fixture.Intent.LimitPrice==fixture.Price&&fixture.Orders.Sends==0,
                "a newer same-price SC preserves confirmed entry while unchanged financial checks still await their own fresh proof");
        }
        foreach(bool overheated in new[]{false,true})using(var fixture=new Fixture(data,"final-"+overheated)) {
            var ready=fixture.Hold(Start);fixture.RefreshAccount(ready);fixture.Tick(ready);
            check(fixture.Pending!=null&&fixture.Orders.Preparations==1,"a confirmed signal starts a fresh unsent buy preflight");
            var changed=ready.AddSeconds(1);fixture.Quote(changed,overheated?11000:fixture.Reference.PriorHigh);
            Call(fixture.Form,"SubmitPending",changed);
            check(fixture.Pending==null&&fixture.Orders.Sends==0&&fixture.Journal.Snapshot.Orders.Count==0,
                (overheated?"overextension":"a failed breakout")+" appearing during account preflight drops the unsent buy before the gateway");
        }
        using(var fixture=new Fixture(data,"settings-reset")) {
            var ready=fixture.Hold(Start);check(fixture.Confirmed(ready)!=null,"the settings-reset fixture has real observed entry evidence before saving");
            var settings=Get<AccountAppSettings>(fixture.Form,"configuration").Strategy;
            decimal loss=settings.StopLossPercent;int timeout=settings.BuyOrderTimeoutSeconds;
            Call(fixture.Form,"SaveSettings");
            check(fixture.Confirmed(ready)==null&&!Get<LiveTradingSession>(fixture.Form,"authorization").Active,
                "saving strategy settings clears observed confirmation and preserves explicit live reauthorization");
            var saved=Get<AccountAppSettings>(fixture.Form,"configuration").Strategy;
            check(saved.StopLossPercent==loss&&saved.BuyOrderTimeoutSeconds==timeout,
                "entry strategy settings save preserves existing exit thresholds and pending-buy timeout");
        }
    }
    static void FixedAllocationAmounts(string data,Action<bool,string> check) {
        // The same full-budget target must survive sequential consumption of
        // available funds. None of these cases bypasses live SC confirmation.
        var cases=new[]{
            new {Name="initial",Held=0m,Cash=1000000m,Capacity=1000000m,Quantity=100,Expected=19},
            new {Name="after-first",Held=200000m,Cash=800000m,Capacity=800000m,Quantity=100,Expected=19},
            new {Name="last-target",Held=800000m,Cash=200000m,Capacity=200000m,Quantity=100,Expected=19},
            new {Name="cash-cap",Held=0m,Cash=50000m,Capacity=1000000m,Quantity=100,Expected=4},
            new {Name="budget-cap",Held=950000m,Cash=1000000m,Capacity=1000000m,Quantity=100,Expected=4},
            new {Name="symbol-amount-cap",Held=0m,Cash=1000000m,Capacity=50000m,Quantity=100,Expected=4},
            new {Name="symbol-share-cap",Held=0m,Cash=1000000m,Capacity=1000000m,Quantity=2,Expected=2}
        };
        foreach(var sample in cases)using(var fixture=new Fixture(data,"fixed-allocation-"+sample.Name)) {
            var ready=fixture.Hold(Start);fixture.RefreshAccount(ready);fixture.Tick(ready);
            fixture.SizingInputs(ready,sample.Held,sample.Cash,sample.Capacity,sample.Quantity);
            var intent=fixture.Intent;Call(fixture.Form,"SubmitPending",ready);
            check(fixture.Pending!=null&&intent.Quantity==sample.Expected,
                "fixed allocation "+sample.Name+": actual form sizes from the one-million operating budget before applying availability caps");
            decimal spend=intent.Quantity*fixture.Price*1.003m;
            check(spend<=200000m&&spend<=sample.Cash&&spend<=1000000m-sample.Held&&spend<=sample.Capacity
                &&intent.Quantity<=sample.Quantity&&fixture.Orders.Sends==0&&fixture.Journal.Snapshot.Orders.Count==0,
                "fixed allocation "+sample.Name+": fees, all amount/share limits and the independent financial preflight remain enforced");
        }
        foreach(string empty in new[]{"cash","budget","capacity"})using(var fixture=new Fixture(data,"fixed-allocation-empty-"+empty)) {
            var ready=fixture.Hold(Start);fixture.RefreshAccount(ready);fixture.Tick(ready);
            fixture.SizingInputs(ready,empty=="budget"?1000000m:0m,empty=="cash"?0m:1000000m,empty=="capacity"?0m:1000000m);
            Call(fixture.Form,"SubmitPending",ready);
            check(fixture.Pending==null&&fixture.Status.Contains("부족")&&fixture.Orders.Sends==0&&fixture.Journal.Snapshot.Orders.Count==0,
                "fixed allocation empty "+empty+": a fixed target never authorizes spending when an actual remaining limit is zero");
        }
    }
    static void FixedAllocationReservations(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"fixed-allocation-reservation")) {
            var ready=fixture.Hold(Start);fixture.RefreshAccount(ready);fixture.Tick(ready);
            var original=fixture.Accepted(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code="005490",Quantity=15,LimitPrice=10000,
                ClientKey="entry-20261117-005490"},901,"123",ready);
            check(fixture.Journal.ReservedBuyAmount==150000m,"fixed allocation retains the original accepted buy's unfilled amount as a separate reservation");
            fixture.SizingInputs(ready,800000m,1000000m,1000000m);Call(fixture.Form,"SubmitPending",ready);
            check(fixture.Pending!=null&&fixture.Intent.Quantity==4&&fixture.Journal.ReservedBuyAmount==150000m&&fixture.Orders.Sends==0,
                "fixed allocation subtracts existing unfilled reservations from the remaining operating limit before clipping the full target");
            var cancelAt=ready.AddSeconds(1);fixture.Quote(cancelAt,fixture.Price);
            var cancellation=fixture.Accepted(new LiveOrderIntent{Kind=LiveOrderKind.Cancel,Code=original.Code,Quantity=15,LimitPrice=10000,
                OriginalBrokerOrderNumber=original.BrokerOrderNumber,ClientKey="isolated-sizing-cancel"},902,"124",cancelAt);
            fixture.SizingInputs(cancelAt,800000m,1000000m,1000000m);Call(fixture.Form,"SubmitPending",cancelAt);
            check(fixture.Pending!=null&&fixture.Intent.Quantity==4&&fixture.Journal.ReservedBuyAmount==150000m
                &&fixture.Journal.Snapshot.Orders.Single(value=>value.Id==cancellation.Id).Status==LiveOrderStatus.AwaitingConfirmation
                &&fixture.Orders.Sends==0,
                "a cancellation ACK cannot enlarge a new fixed-target buy by prematurely releasing its original reservation");
            var confirmedAt=ready.AddSeconds(2);fixture.Quote(confirmedAt,fixture.Price);
            fixture.Journal.ConfirmCancellation(original.BrokerOrderNumber,cancellation.BrokerOrderNumber,"AA05",15,confirmedAt);
            fixture.SizingInputs(confirmedAt,800000m,1000000m,1000000m);Call(fixture.Form,"SubmitPending",confirmedAt);
            check(fixture.Pending!=null&&fixture.Intent.Quantity==19&&fixture.Journal.ReservedBuyAmount==0&&fixture.Orders.Sends==0
                &&fixture.Journal.Snapshot.Orders.Count==2,
                "only confirmed cancellation restores enough remaining budget for the original full allocation target, without transmitting a replacement");
        }
    }
    public static void Run(string data,Action<bool,string> check) {
        RejectChaseAndTransient(data,check);SustainedEntryAndDip(data,check);FinalRecheckAndSettings(data,check);
        FixedAllocationAmounts(data,check);FixedAllocationReservations(data,check);
    }
}
