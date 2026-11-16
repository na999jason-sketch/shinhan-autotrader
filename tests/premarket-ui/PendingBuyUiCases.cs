using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Exercises the real form scheduler and final preparation path. All account,
// quote and order responses are isolated values; the order wire refuses sends.
static class PendingBuyUiCases {
    const string Account="12345678901",BuyCode="084695",RiskCode="241520";
    static readonly DateTime Now=new DateTime(2026,11,16,3,0,0,DateTimeKind.Utc);
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static T Get<T>(object owner,string name){return (T)owner.GetType().GetField(name,Fields).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Fields).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] values) {
        try{return owner.GetType().GetMethod(name,Fields).Invoke(owner,values);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    sealed class ReadTransport : IIndiTransport {
        public event Action<int> ReceiveData;
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public int Requests,Completions;string query;int pending;
        public object Invoke(string method,params object[] values) {
            if(method=="SetQueryName"){query=(string)values[0];return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData")return true;
            if(method=="RequestData"){pending=100+(++Requests);return (short)pending;}
            if(method=="GetMultiRowCount")return 0;
            throw new InvalidOperationException("Unexpected isolated pending-order read: "+method);
        }
        public void Complete() {
            if(pending==0)throw new InvalidOperationException("A synthetic read must be in progress.");
            int id=pending;pending=0;Completions++;if(ReceiveData!=null)ReceiveData(id);
        }
        public void Dispose(){}
    }
    sealed class NoOrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Preparations,Sends;
        public bool AllowSyntheticCancel;
        public event Action<LiveOrderAcknowledgement> Acknowledged;
        public void PrepareChannel(){Preparations++;}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;
            if(AllowSyntheticCancel&&intent.Kind==LiveOrderKind.Cancel)return 901;
            throw new InvalidOperationException("Pending-order UI tests must never transmit an order.");
        }
        public void AcceptCancellation() {
            var fields=new[]{"0","000124","0","","1","synthetic cancel accepted","",""};
            var evidence=LiveOrderResponseParser.Read(901,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{Account,"1111"});
            if(Acknowledged!=null)Acknowledged(new LiveOrderAcknowledgement{RequestId=901,BrokerOrderNumber="000124",ResponseEvidence=evidence});
        }
        public void Dispose(){}
    }
    sealed class Fixture : IDisposable {
        public readonly MainForm Form;
        public readonly LiveOrderJournal Journal;
        public readonly ReadTransport Reads=new ReadTransport();
        public readonly NoOrderWire Orders=new NoOrderWire();
        public readonly IndiRequestCoordinator Requests;
        public readonly IndiApi Api;
        public readonly string Key,RootId;
        public readonly PremarketReference Reference;
        public readonly Holding Risk=new Holding{Code=RiskCode,Name="ISOLATED RISK",Quantity=18,AvgPrice=8160,Price=7660};
        readonly RealtimeEventProcessor processor;
        readonly Dictionary<int,object> quote=new Dictionary<int,object>();
        int volume=2000;
        public Fixture(string data,string name,bool risk=false,int age=180,bool candidate=true) {
            Form=new MainForm();Get<Timer>(Form,"scheduler").Stop();Form.ShowInTaskbar=false;Form.Show();Application.DoEvents();Get<Timer>(Form,"scheduler").Stop();
            Get<NumericUpDown>(Form,"budget").Value=1000000;Call(Form,"SaveSettings");
            var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            var credential=Activator.CreateInstance(type,new object[]{Account,"1111"});
            Key=(string)type.GetProperty("Key").GetValue(credential,null);
            Set(Form,"credentials",credential);Set(Form,"boundKey",Key);Set(Form,"lastReportKey",Key);Set(Form,"monitoring",true);
            Set(Form,"researchRequested",false);Set(Form,"researchComplete",true);Set(Form,"researchDateKorea",MarketTime.ToKorea(Now).Date);
            Set(Form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Set(Form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Journal=new LiveOrderJournal(Path.Combine(data,"pending-buy-ui-"+name+".xml"));Journal.BindAccount(Account);
            var at=Now.AddSeconds(-age);
            var order=Journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code=BuyCode,Quantity=3,LimitPrice=10460,
                ClientKey="entry-20261116-"+BuyCode},at);
            RootId=order.Id;Accept(order,501,"123",at.AddMilliseconds(1));
            Set(Form,"journal",Journal);Set(Form,"gateway",new LiveOrderGateway(Journal,Orders));
            var readiness=new BrokerReadiness(Key,Journal.Snapshot.AccountFingerprint);readiness.SetAuthorization(true);readiness.SetAccountStream(true,Now.AddMinutes(-1));Set(Form,"readiness",readiness);
            Api=new IndiApi(Get<Panel>(Form,"nativeArea"));Requests=new IndiRequestCoordinator(Reads);
            Requests.Fatal+=message=>{throw new InvalidOperationException("Isolated pending-order read failed: "+message);};
            Set(Api,"requests",Requests);Set(Api,"hasReceivedData",true);Set(Form,"api",Api);
            var session=Get<UnattendedSession>(Form,"session");session.Arm(Now);session.Tick(Now,false);session.ScanCompleted(Now);
            Get<LiveTradingSession>(Form,"authorization").Authorize(Account,Now);
            if(risk)Get<List<Holding>>(Form,"holdings").Add(Risk);
            foreach(string symbol in new[]{BuyCode,RiskCode})Get<Dictionary<string,CashInstrument>>(Form,"instruments")[symbol]=new CashInstrument{Code=symbol,Market="0",Product="ST"};
            processor=new RealtimeEventProcessor(index=>quote[index]);processor.BindAccount(Account);processor.Track(BuyCode);processor.Track(RiskCode);
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){BuyCode,RiskCode});
            Set(realtime,"native",new object());Set(realtime,"<AccountSubscribed>k__BackingField",true);Set(Form,"realtime",realtime);
            Get<HashSet<string>>(Form,"trackedQuotes").Add(BuyCode);Get<HashSet<string>>(Form,"trackedQuotes").Add(RiskCode);
            if(candidate) {
                var days=new List<DateTime>();var day=MarketTime.ToKorea(Now).Date.AddDays(-1);
                while(days.Count<45){if(day.DayOfWeek!=DayOfWeek.Saturday&&day.DayOfWeek!=DayOfWeek.Sunday)days.Add(day);day=day.AddDays(-1);}
                days.Reverse();var bars=days.Select((date,index)=>new Bar{Date=date.ToString("yyyyMMdd",CultureInfo.InvariantCulture),
                    Open=10000+index*10,High=10010+index*10,Low=9990+index*10,Close=10000+index*10,Volume=1000}).ToArray();
                Reference=PremarketResearch.Create(new Stock{Code=BuyCode,Name="ISOLATED BUY",Market="0"},bars,
                    Get<AccountAppSettings>(Form,"configuration").Strategy,MarketTime.ToKorea(Now).Date);
                if(Reference==null)throw new InvalidOperationException("Synthetic pending-buy reference was not created.");
                Get<Dictionary<string,PremarketReference>>(Form,"preparedReferences")[BuyCode]=Reference;
                Get<List<PremarketReference>>(Form,"watchedReferences").Add(Reference);
                Get<DataGridView>(Form,"candidatesGrid").Rows.Add(BuyCode,"ISOLATED BUY",Reference.PriorHigh,Reference.RankScore,"synthetic","");
            }
            Refresh(Now);
        }
        public LiveOrderRecord Root {get{return Journal.Snapshot.Orders.Single(value=>value.Id==RootId);}}
        public object Pending {get{return Get<object>(Form,"entry");}}
        public LiveOrderIntent Intent {get{return Pending==null?null:Get<LiveOrderIntent>(Pending,"Intent");}}
        public void Accept(LiveOrderRecord order,int request,string broker,DateTime at) {
            Journal.MarkDispatching(order.Id,at);Journal.MarkRequestAccepted(order.Id,request,at);
            var fields=new[]{"0","000"+broker,"0","","1","synthetic accepted","",""};
            var evidence=LiveOrderResponseParser.Read(request,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{Account,"1111"});
            Journal.RecordAcknowledgement(order.Id,request,broker,false,at,evidence);
        }
        BrokerOrderRow Row(LiveOrderRecord order) {
            return new BrokerOrderRow{OrderNumber=order.BrokerOrderNumber,OriginalOrderNumber=order.OriginalBrokerOrderNumber,
                Code=order.Code,SideCode="2",AmendCancelCode=order.Kind==LiveOrderKind.Cancel?"2":"0",ProductCode="01",CreditCode="00",
                Quantity=order.Quantity,FilledQuantity=order.FilledQuantity,OpenQuantity=order.Kind==LiveOrderKind.Cancel?0:order.Quantity-order.FilledQuantity-order.CancelledQuantity,
                ConfirmedQuantity=order.CancelledQuantity,LimitPrice=order.LimitPrice,ConfirmationCode="0",AverageFillPrice=order.FilledQuantity>0?order.LimitPrice:0};
        }
        public void Refresh(DateTime at,decimal buyPrice=10460) {
            var snapshot=new BrokerOrdersSnapshot{TradingDate="20261116"};Call(snapshot,"BindRequestScope",at.AddMilliseconds(-10),at,true,Account);
            foreach(var order in Journal.Snapshot.Orders.Where(value=>!string.IsNullOrEmpty(value.BrokerOrderNumber)))snapshot.Orders.Add(Row(order));
            Set(Form,"brokerOrders",snapshot);
            var balance=new BrokerHoldingsSnapshot{BasisVerified=true,RequestBasisVerified=true,RequestStartedUtc=at.AddMilliseconds(-10),ReceivedUtc=at};
            foreach(var held in Get<List<Holding>>(Form,"holdings")) {
                balance.Holdings.Add(held);balance.BasisValidated[held.Code]=true;balance.CashSourceValidated[held.Code]=true;
                balance.SellableQuantities[held.Code]=checked((int)held.Quantity);balance.UnfilledQuantities[held.Code]=0;
            }
            Set(Form,"tradeHoldings",balance);Set(Form,"holdingsReceivedUtc",(DateTime?)at);
            Set(Form,"cash",new AccountCashSnapshot{DepositCash=1000000,Cash100Orderable=1000000,ReceivedUtc=at});
            Quote(BuyCode,at,buyPrice);Quote(RiskCode,at,Risk.Price);
        }
        void Quote(string symbol,DateTime at,decimal price) {
            quote[1]=symbol;quote[3]=price.ToString(CultureInfo.InvariantCulture);quote[17]="1";
            quote[2]=MarketTime.ToKorea(at.AddSeconds(-1)).ToString("HHmmss",CultureInfo.InvariantCulture);quote[7]=volume++;processor.Process("SC",at.AddSeconds(-1));
            quote[2]=MarketTime.ToKorea(at).ToString("HHmmss",CultureInfo.InvariantCulture);quote[7]=volume++;processor.Process("SC",at);
        }
        public object QueueUnsentBuy() {
            var type=typeof(MainForm).GetNestedType("EntryRequest",BindingFlags.NonPublic);var pending=Activator.CreateInstance(type,true);
            Set(pending,"Intent",new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code="000660",Quantity=1,LimitPrice=10460,ClientKey="unsent-other-buy"});
            Set(pending,"WaitingFor",null);Set(pending,"NotBeforeUtc",Now.AddMinutes(1));Set(Form,"entry",pending);return pending;
        }
        public void BeginRead() {
            if(!Requests.Begin("BARS","TR_SCHART",new Dictionary<int,string>{{0,BuyCode},{1,"D"},{2,"1"},{3,"00000000"},{4,"99999999"},{5,"2"}},BuyCode))
                throw new InvalidOperationException("Synthetic readonly request did not begin.");
        }
        public void Tick(DateTime at){Call(Form,"ScheduleAt",at);}
        public void Dispose(){Form.Close();Form.Dispose();Journal.Dispose();}
    }
    static void SellPreemptsUnsentBuy(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"risk-priority",true)) {
            var before=fixture.QueueUnsentBuy();fixture.Tick(Now);
            check(fixture.Intent!=null&&fixture.Intent.Kind==LiveOrderKind.Sell&&fixture.Intent.Code==RiskCode
                &&fixture.Intent.Quantity==18&&fixture.Intent.LimitPrice==7660&&!ReferenceEquals(before,fixture.Pending),
                "a current stop-loss preempts an unsent different-symbol buy preflight even with a working unheld buy");
            check(fixture.Orders.Preparations==1&&fixture.Orders.Sends==0&&fixture.Journal.Snapshot.Orders.Count==1
                &&fixture.Root.Status==LiveOrderStatus.Working&&fixture.Root.CancelledQuantity==0,
                "sell prioritization preserves the already accepted buy and starts only the normal sell preflight");
            check(Get<Queue<string>>(fixture.Pending,"Reads").Contains("ORDINARYHOLDINGS"),
                "protective sell priority retains fresh cash classification and account verification");
        }
    }
    static void BusyReadFinishesFirst(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"busy-risk",true)) {
            var before=fixture.QueueUnsentBuy();fixture.BeginRead();fixture.Tick(Now);
            check(fixture.Api.Busy&&ReferenceEquals(before,fixture.Pending)&&fixture.Reads.Requests==1
                &&fixture.Reads.Completions==0&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                "risk priority never discards or replaces an in-flight readonly response buffer");
            fixture.Reads.Complete();fixture.Tick(Now);
            check(!fixture.Api.Busy&&fixture.Reads.Completions==1&&fixture.Intent!=null&&fixture.Intent.Kind==LiveOrderKind.Sell
                &&fixture.Orders.Preparations==1&&fixture.Orders.Sends==0,
                "the stop-loss takes priority immediately after the serialized read completes");
        }
    }
    static void TimeoutQueuesRemainder(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"timeout")) {
            fixture.Journal.RecordExecution("123","synthetic-partial",1,10460,Now.AddSeconds(-1));fixture.Refresh(Now);
            fixture.Tick(Now);
            check(fixture.Intent!=null&&fixture.Intent.Kind==LiveOrderKind.Cancel&&fixture.Intent.Code==BuyCode&&fixture.Intent.Quantity==2
                &&LiveOrderProtocol.BrokerNumbersEqual(fixture.Intent.OriginalBrokerOrderNumber,"123")&&fixture.Intent.ClientKey==PendingBuyPolicy.CancelKeyPrefix+fixture.RootId+"-1",
                "the actual scheduler cancels only two remaining shares after a timed-out three-share buy partially fills");
            check(fixture.Root.Status==LiveOrderStatus.PartiallyFilled&&fixture.Root.FilledQuantity==1&&fixture.Root.CancelledQuantity==0
                &&fixture.Journal.ReservedBuyAmount==20920&&fixture.Orders.Preparations==1&&fixture.Orders.Sends==0,
                "a cancellation plan preserves individual fills and keeps the unconfirmed remainder reserved");
            var before=fixture.Pending;fixture.Tick(Now);
            check(ReferenceEquals(before,fixture.Pending)&&fixture.Orders.Preparations==1&&fixture.Journal.Snapshot.Orders.Count==1,
                "repeated ticks cannot create duplicate cancellation intents or journal orders");
            var reads=Get<Queue<string>>(fixture.Pending,"Reads");
            check(reads.Contains("ORDINARYHOLDINGS")&&reads.Contains("CASH")&&reads.Contains("TRADEHOLDINGS")&&reads.Contains("ORDERS"),
                "timeout cancellation follows the full fresh account and original-order preflight");
        }
    }
    static void FillDuringCancelPreflight(string data,bool full,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"fill-race-"+full)) {
            fixture.Tick(Now);var planned=fixture.Intent;
            check(planned!=null&&planned.Kind==LiveOrderKind.Cancel&&planned.Quantity==3,"fill-race fixture starts with a three-share cancellation plan");
            var at=Now.AddSeconds(1);fixture.Journal.RecordExecution("123","race-fill",full?3:1,10460,at);fixture.Refresh(at.AddMilliseconds(100));
            Call(fixture.Form,"SubmitPending",at.AddMilliseconds(100));
            check(full?fixture.Pending==null:planned.Quantity==2,
                full?"a full fill arriving during cancel preflight drops the obsolete cancellation without transmission"
                    :"a partial fill arriving during cancel preflight reduces cancellation to the latest two-share remainder");
            check(fixture.Root.FilledQuantity==(full?3:1)&&fixture.Root.CancelledQuantity==0&&fixture.Orders.Sends==0,
                "cancel/fill races preserve purchased shares and never fabricate cancellation confirmation");
        }
    }
    static void AwaitCancellationConfirmation(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"await-confirmation")) {
            fixture.Tick(Now);var intent=fixture.Intent;Set(fixture.Form,"entry",null);
            var at=Now.AddSeconds(1);var cancellation=fixture.Journal.Prepare(intent,at);fixture.Accept(cancellation,502,"124",at.AddMilliseconds(1));
            fixture.Refresh(at.AddMilliseconds(100));fixture.Tick(at.AddMilliseconds(100));
            check(fixture.Journal.Snapshot.Orders.Single(value=>value.Kind==LiveOrderKind.Cancel).Status==LiveOrderStatus.AwaitingConfirmation
                &&fixture.Root.Status==LiveOrderStatus.Working&&fixture.Root.CancelledQuantity==0&&fixture.Journal.ReservedBuyAmount==31380,
                "a positive cancellation acknowledgement keeps the original exposure reserved until broker confirmation");
            check(fixture.Pending==null&&fixture.Orders.Preparations==1&&fixture.Orders.Sends==0&&fixture.Journal.Snapshot.Orders.Count==2,
                "a still-valid breakout cannot cause automatic reentry or another cancel while confirmation is outstanding");
        }
    }
    static void DisabledAndNotDue(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"not-due",false,119)) {
            fixture.Tick(Now);
            check(fixture.Pending==null&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                "an otherwise valid pending buy is left working before its configured timeout");
        }
        using(var fixture=new Fixture(data,"revoked")) {
            Get<LiveTradingSession>(fixture.Form,"authorization").Revoke();fixture.Tick(Now);
            check(fixture.Pending==null&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0&&fixture.Root.Status==LiveOrderStatus.Working,
                "account monitoring without live authorization never sends a timeout cancellation");
        }
        using(var fixture=new Fixture(data,"stale-scope")) {
            fixture.Refresh(Now.AddSeconds(-6));fixture.Tick(Now);
            check((fixture.Intent==null||fixture.Intent.Kind!=LiveOrderKind.Cancel)&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                "expired buy management refreshes stale broker scope instead of cancelling from old remaining quantity");
        }
    }
    static void CancellationReachesFakeGateway(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"ready-gateway")) {
            fixture.Tick(Now);var intent=fixture.Intent;
            var at=Now.AddSeconds(2);fixture.Refresh(at);
            var readiness=Get<BrokerReadiness>(fixture.Form,"readiness");
            readiness.ObserveTradingDate(BuyCode,"20261116",at.AddMilliseconds(-800),at.AddMilliseconds(-700));
            readiness.ObserveTradeBalance(Get<BrokerHoldingsSnapshot>(fixture.Form,"tradeHoldings"));
            var orders=Get<BrokerOrdersSnapshot>(fixture.Form,"brokerOrders");readiness.ObserveOrders(orders,orders.RequestStartedUtc);
            readiness.ObserveCash(Get<AccountCashSnapshot>(fixture.Form,"cash"),at.AddMilliseconds(-10));
            var result=readiness.Evaluate(intent,null,at,false,true,fixture.Journal.Snapshot,1000000,at);
            check(result.Ready&&result.Checks.OriginalOrderVerified&&result.Checks.OriginalTradeKind==LiveOrderKind.Buy,
                "a scheduler timeout cancellation of an unheld accepted buy reaches real readiness with fresh scoped original-order evidence");
            result.Checks.Authorization=Get<LiveTradingSession>(fixture.Form,"authorization");
            fixture.Orders.AllowSyntheticCancel=true;
            using(var gateway=new LiveOrderGateway(fixture.Journal,fixture.Orders,()=>at)) {
                var sent=gateway.Submit(intent,new LiveAccountCredentials(Account,"1111"),result.Checks,at);
                check(fixture.Orders.Sends==1&&gateway.Busy&&sent.Kind==LiveOrderKind.Cancel&&sent.Quantity==3
                    &&sent.Status==LiveOrderStatus.AwaitingAcknowledgement&&fixture.Journal.ReservedBuyAmount==31380,
                    "normal financial preflight sends exactly the planned remainder through an isolated gateway and retains exposure");
                fixture.Orders.AcceptCancellation();
                check(!gateway.Busy&&fixture.Journal.Snapshot.Orders.Single(value=>value.Kind==LiveOrderKind.Cancel).Status==LiveOrderStatus.AwaitingConfirmation
                    &&fixture.Root.CancelledQuantity==0&&fixture.Journal.ReservedBuyAmount==31380,
                    "the real gateway processes positive cancel ACK without releasing exposure or inventing cancellation confirmation");
            }
        }
    }
    public static void Run(string data,Action<bool,string> check) {
        SellPreemptsUnsentBuy(data,check);BusyReadFinishesFirst(data,check);TimeoutQueuesRemainder(data,check);
        FillDuringCancelPreflight(data,false,check);FillDuringCancelPreflight(data,true,check);
        AwaitCancellationConfirmation(data,check);DisabledAndNotDue(data,check);
        CancellationReachesFakeGateway(data,check);
    }
}
