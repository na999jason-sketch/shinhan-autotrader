using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Drives the real scheduler with synthetic rejected orders and live quote data.
// Native Indi controls and actual broker calls are deliberately absent.
static class AutomaticRetryUiCases {
    const string Account="12345678901", Symbol="000660";
    static readonly DateTime Opening=new DateTime(2026,11,16,0,15,0,DateTimeKind.Utc);
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static T Get<T>(object owner,string name){return (T)owner.GetType().GetField(name,Fields).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Fields).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] values) {
        try{return owner.GetType().GetMethod(name,Fields).Invoke(owner,values);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    sealed class NoReadTransport : IIndiTransport {
        public int Calls,Requests;string query;
        public event Action<int> ReceiveData {add{} remove{}}
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public object Invoke(string method,params object[] values) {
            Calls++;
            if(method=="SetQueryName"){query=(string)values[0];return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData")return true;
            if(method=="RequestData")return (short)(501+Requests++);
            throw new InvalidOperationException("Unexpected synthetic read getter: "+method);
        }
        public void Dispose(){}
    }
    sealed class NoOrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Preparations,Sends;
        public event Action<LiveOrderAcknowledgement> Acknowledged {add{} remove{}}
        public void PrepareChannel(){Preparations++;}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;throw new InvalidOperationException("Automatic retry UI fixture must never transmit an order.");
        }
        public void Dispose(){}
    }
    sealed class Fixture : IDisposable {
        public readonly MainForm Form;
        public readonly LiveOrderJournal Journal;
        public readonly NoReadTransport Reads=new NoReadTransport();
        public readonly NoOrderWire Orders=new NoOrderWire();
        public readonly LiveOrderKind Side;
        public readonly string Symbol;
        public readonly DateTime Opening;
        public readonly string Path,RootId,StrategyKey,AccountKey;
        public readonly PremarketReference Reference;
        readonly Dictionary<int,object> quoteFields=new Dictionary<int,object>();
        readonly RealtimeEventProcessor processor;
        int volume=2000;
        public Fixture(string data,string name,LiveOrderKind side,DateTime rejectedAt,bool evidence=true,string existingPath=null,string code="000660",DateTime? opening=null) {
            Side=side;Symbol=code;Opening=opening??AutomaticRetryUiCases.Opening;Form=new MainForm();Get<Timer>(Form,"scheduler").Stop();
            Form.ShowInTaskbar=false;Form.Show();Application.DoEvents();Get<Timer>(Form,"scheduler").Stop();
            Get<NumericUpDown>(Form,"budget").Value=1000000;Call(Form,"SaveSettings");
            var credentialType=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            var credentials=Activator.CreateInstance(credentialType,new object[]{Account,"1111"});
            AccountKey=(string)credentialType.GetProperty("Key").GetValue(credentials,null);
            Set(Form,"credentials",credentials);Set(Form,"boundKey",AccountKey);Set(Form,"lastReportKey",AccountKey);
            Set(Form,"monitoring",true);Set(Form,"researchRequested",false);Set(Form,"researchComplete",true);
            Set(Form,"researchDateKorea",MarketTime.ToKorea(Opening).Date);
            Set(Form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Set(Form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Path=existingPath??System.IO.Path.Combine(data,"automatic-retry-"+name+".xml");
            Journal=new LiveOrderJournal(Path);Journal.BindAccount(Account);
            StrategyKey=(side==LiveOrderKind.Buy?"entry-":"exit-")+MarketTime.ToKorea(Opening).ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-"+Symbol;
            if(existingPath==null)RootId=Reject(StrategyKey,rejectedAt,evidence).Id;
            else RootId=Journal.Snapshot.Orders.OrderBy(value=>value.CreatedUtc).First().Id;
            Set(Form,"journal",Journal);Set(Form,"gateway",new LiveOrderGateway(Journal,Orders));
            var readiness=new BrokerReadiness(AccountKey,Journal.Snapshot.AccountFingerprint);
            readiness.SetAuthorization(true);readiness.SetAccountStream(true,Opening.AddMinutes(-1));Set(Form,"readiness",readiness);
            var api=new IndiApi(Get<Panel>(Form,"nativeArea"));
            Set(api,"requests",new IndiRequestCoordinator(Reads));Set(api,"hasReceivedData",true);Set(Form,"api",api);
            var session=Get<UnattendedSession>(Form,"session");session.Arm(Opening);session.Tick(Opening,false);session.ScanCompleted(Opening);
            Get<LiveTradingSession>(Form,"authorization").Authorize(Account,Opening);
            var settings=Get<AccountAppSettings>(Form,"configuration").Strategy;
            var stock=new Stock{Code=Symbol,Name="SYNTHETIC RETRY",Market="0"};
            Get<List<Stock>>(Form,"symbols").Add(stock);
            Get<Dictionary<string,CashInstrument>>(Form,"instruments")[Symbol]=new CashInstrument{Code=Symbol,Market="0",Product=Symbol=="305720"?"EF":"ST"};
            if(side==LiveOrderKind.Buy) {
                var dates=new List<DateTime>();var date=MarketTime.ToKorea(Opening).Date.AddDays(-1);
                while(dates.Count<45){if(date.DayOfWeek!=DayOfWeek.Saturday&&date.DayOfWeek!=DayOfWeek.Sunday)dates.Add(date);date=date.AddDays(-1);}
                dates.Reverse();var bars=dates.Select((day,index)=>new Bar{Date=day.ToString("yyyyMMdd",CultureInfo.InvariantCulture),
                    Open=10000+index*10,High=10010+index*10,Low=9990+index*10,Close=10000+index*10,Volume=1000}).ToArray();
                Reference=PremarketResearch.Create(stock,bars,settings,MarketTime.ToKorea(Opening).Date);
                if(Reference==null)throw new InvalidOperationException("Synthetic retry reference was not created.");
                Get<Dictionary<string,PremarketReference>>(Form,"preparedReferences")[Symbol]=Reference;
                Get<List<PremarketReference>>(Form,"watchedReferences").Add(Reference);
                Get<DataGridView>(Form,"candidatesGrid").Rows.Add(Symbol,stock.Name,Reference.PriorHigh,Reference.RankScore,"synthetic","");
            }
            processor=new RealtimeEventProcessor(index=>quoteFields[index]);processor.BindAccount(Account);processor.Track(Symbol);
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){Symbol});
            Set(realtime,"native",new object());Set(realtime,"<AccountSubscribed>k__BackingField",true);Set(Form,"realtime",realtime);
            Get<HashSet<string>>(Form,"trackedQuotes").Add(Symbol);
            Refresh(Opening);
        }
        public LiveOrderRecord Reject(string key,DateTime at,bool evidence=true) {
            var record=Journal.Prepare(new LiveOrderIntent{Kind=Side,Code=Symbol,Quantity=3,LimitPrice=10440,ClientKey=key},at);
            int id=100+Journal.Snapshot.Orders.Count;Journal.MarkDispatching(record.Id,at);Journal.MarkRequestAccepted(record.Id,id,at);
            OrderResponseEvidence response=null;
            if(evidence) {
                var fields=new[]{"0","0","0","0","0","synthetic normal rejection","0","0"};
                response=LiveOrderResponseParser.Read(id,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{Account,"1111"});
            }
            Journal.RecordAcknowledgement(record.Id,id,"0",false,at,response);
            return Journal.Snapshot.Orders.Single(value=>value.Id==record.Id);
        }
        public BrokerOrdersSnapshot OrdersAt(DateTime at,string account=Account,bool verified=true) {
            var value=new BrokerOrdersSnapshot{TradingDate=MarketTime.ToKorea(at).ToString("yyyyMMdd",CultureInfo.InvariantCulture)};
            Call(value,"BindRequestScope",at.AddMilliseconds(-10),at,verified,account);return value;
        }
        public void Refresh(DateTime at,decimal price=10460,bool verified=true) {
            var source=new BrokerHoldingsSnapshot{BasisVerified=true,RequestBasisVerified=true,RequestStartedUtc=at.AddMilliseconds(-10),ReceivedUtc=at};
            if(Side==LiveOrderKind.Sell) {
                var held=new Holding{Code=Symbol,Name="SYNTHETIC RETRY",Quantity=2,AvgPrice=20000,Price=price};source.Holdings.Add(held);
                source.BasisValidated[Symbol]=true;source.CashSourceValidated[Symbol]=true;source.SellableQuantities[Symbol]=2;source.UnfilledQuantities[Symbol]=0;
            }
            Set(Form,"tradeHoldings",source);var heldValues=Get<List<Holding>>(Form,"holdings");heldValues.Clear();heldValues.AddRange(source.Holdings);
            Set(Form,"brokerOrders",OrdersAt(at,Account,verified));
            Set(Form,"cash",new AccountCashSnapshot{DepositCash=1000000,Cash100Orderable=1000000,ReceivedUtc=at});
            quoteFields[1]=Symbol;quoteFields[2]=MarketTime.ToKorea(at.AddSeconds(-1)).ToString("HHmmss",CultureInfo.InvariantCulture);
            quoteFields[3]=price.ToString(CultureInfo.InvariantCulture);quoteFields[7]=volume++;quoteFields[17]="1";processor.Process("SC",at.AddSeconds(-1));
            quoteFields[2]=MarketTime.ToKorea(at).ToString("HHmmss",CultureInfo.InvariantCulture);quoteFields[7]=volume++;processor.Process("SC",at);
        }
        public void Tick(DateTime at){Call(Form,"ScheduleAt",at);}
        public object Pending {get{return Get<object>(Form,"entry");}}
        public LiveOrderIntent Intent {get{return Pending==null?null:Get<LiveOrderIntent>(Pending,"Intent");}}
        public void Dispose(){Form.Close();Form.Dispose();Journal.Dispose();}
    }
    static void WaitThenQueue(string data,LiveOrderKind side,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"cooldown-"+side,side,Opening)) {
            var waiting=Opening.AddSeconds(1);fixture.Refresh(waiting);fixture.Tick(waiting);
            check(fixture.Pending==null&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                side+": automatic rejection recovery waits through its cooldown without manual selection or dispatch");
            var ready=Opening.AddSeconds(31);fixture.Refresh(ready);fixture.Tick(ready);
            var intent=fixture.Intent;
            check(intent!=null&&intent.Kind==side&&intent.Code==Symbol&&intent.ClientKey!=fixture.StrategyKey
                &&intent.ClientKey.StartsWith("auto-retry-",StringComparison.Ordinal),
                side+": actual scheduler automatically creates a distinct retry intent after the cooldown");
            check(intent.LimitPrice==10460&&intent.Quantity==(side==LiveOrderKind.Sell?2:1),
                side+": automatic retry uses the current quote and current position or fresh sizing path rather than rejected inputs");
            var reads=Get<Queue<string>>(fixture.Pending,"Reads");
            check(new[]{"BARS","CASH","TRADEHOLDINGS","ORDERS"}.All(reads.Contains)
                &&reads.Contains(side==LiveOrderKind.Sell?"ORDINARYHOLDINGS":"CAPACITY")
                &&Get<bool>(fixture.Pending,"AutoSize")== (side==LiveOrderKind.Buy),
                side+": automated retry queues the complete new account and strategy preflight");
            fixture.Tick(ready);
            check(ReferenceEquals(fixture.Intent,intent)&&fixture.Orders.Preparations==1&&fixture.Journal.Snapshot.Orders.Count==1
                &&fixture.Orders.Sends==0&&fixture.Reads.Calls==0,
                side+": duplicate scheduler ticks preserve one pending retry and never replay a journal record");
            Get<LiveTradingSession>(fixture.Form,"authorization").Revoke();fixture.Tick(ready);
            check(fixture.Pending==null&&fixture.Orders.Sends==0,
                side+": stopping live authorization removes an unsent automatic retry");
        }
    }
    static void Refused(string data,string name,Action<Fixture> configure,Action<bool,string> check) {
        using(var fixture=new Fixture(data,name,LiveOrderKind.Sell,Opening.AddMinutes(-1),name!="legacy")) {
            configure(fixture);fixture.Tick(Opening);
            check(fixture.Pending==null&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                name+": scheduler does not automatically replay an unproven or already accepted order");
        }
    }
    static void BuyConditionRevoked(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"buy-signal-revoked",LiveOrderKind.Buy,Opening.AddMinutes(-1))) {
            fixture.Refresh(Opening,fixture.Reference.PriorHigh);fixture.Tick(Opening);
            check(fixture.Pending==null&&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
                "buy retry waits when the current breakout signal no longer exists");
        }
    }
    static void DurableCap(string data,LiveOrderKind side,Action<bool,string> check) {
        string path;
        using(var fixture=new Fixture(data,"durable-cap-"+side,side,Opening.AddMinutes(-10))) {
            path=fixture.Path;
            for(int attempt=1;attempt<=3;attempt++) {
                var at=Opening.AddSeconds(-420+(attempt-1)*180);
                var decision=AutoOrderRetry.Evaluate(fixture.Journal.Snapshot,fixture.StrategyKey,Symbol,side,fixture.AccountKey,fixture.OrdersAt(at),at);
                check(decision.State==AutoRetryState.Eligible&&decision.Attempt==attempt,
                    side+": durable retry fixture obtains each audited key from the real policy");
                fixture.Reject(decision.ClientKey,at);
            }
            check(fixture.Journal.Snapshot.Orders.Count==4&&fixture.Orders.Sends==0,
                side+": retry history is persisted as distinct rejected attempts without an order side effect");
        }
        using(var restarted=new Fixture(data,"durable-cap-restart-"+side,side,Opening.AddMinutes(-10),true,path)) {
            var decision=AutoOrderRetry.Evaluate(restarted.Journal.Snapshot,restarted.StrategyKey,Symbol,side,restarted.AccountKey,restarted.OrdersAt(Opening),Opening);
            check(decision.State==AutoRetryState.Blocked&&decision.Status.Contains("한도"),
                side+": restart cannot reset the persisted automatic retry limit");
            restarted.Tick(Opening);
            check(restarted.Pending==null&&restarted.Orders.Preparations==0&&restarted.Orders.Sends==0&&restarted.Journal.Snapshot.Orders.Count==4,
                side+": actual scheduler respects the same durable limit after restarting the form");
        }
    }
    static void QueuedSignalRevoked(string data,LiveOrderKind side,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"queued-signal-revoked-"+side,side,Opening.AddMinutes(-1))) {
            fixture.Tick(Opening);
            check(fixture.Pending!=null&&fixture.Orders.Preparations==1&&fixture.Orders.Sends==0,
                side+": valid automatic retry reaches a fresh preflight before the signal changes");
            var later=Opening.AddSeconds(1);
            fixture.Refresh(later,side==LiveOrderKind.Buy?fixture.Reference.PriorHigh:19000);
            Call(fixture.Form,"SubmitPending",later);
            check(fixture.Pending==null&&fixture.Orders.Sends==0&&fixture.Journal.Snapshot.Orders.Count==1,
                side+": the final submission path drops the unsent retry when the current strategy condition is no longer met");
        }
    }
    static void GrantedLegacySell(string data,Action<bool,string> check) {
        var at=new DateTime(2026,11,13,0,15,0,DateTimeKind.Utc);
        using(var fixture=new Fixture(data,"granted-legacy-sell",LiveOrderKind.Sell,at.AddMinutes(-1),false,null,"305720",at)) {
            Call(fixture.Form,"AuthorizeConfirmedLegacySells",at);
            check(fixture.Journal.Snapshot.LegacySellRechecks.Count==0,
                "a different account cannot inherit the operator's historical no-order attestation");
            Set(fixture.Form,"legacySellAttestation",(Func<string,bool>)(key=>key==fixture.AccountKey));
            Call(fixture.Form,"AuthorizeConfirmedLegacySells",at);
            Call(fixture.Form,"AuthorizeConfirmedLegacySells",at);
            var ready=at.AddSeconds(1);fixture.Refresh(ready);fixture.Tick(ready);
            var intent=fixture.Intent;
            check(intent!=null&&intent.Kind==LiveOrderKind.Sell&&intent.Code=="305720"
                &&intent.ClientKey=="auto-legacy-"+fixture.RootId+"-1"&&intent.Quantity==2&&intent.LimitPrice==10460,
                "an explicitly authorized legacy sell is automatically reevaluated by the scheduler without selecting a row");
            var root=fixture.Journal.Snapshot.Orders.Single();
            check(root.Status==LiveOrderStatus.Rejected&&root.ResponseEvidence==null
                &&fixture.Journal.Snapshot.LegacySellRechecks.Single().Source==LiveOrderJournal.LegacySellRecheckSource,
                "legacy reevaluation preserves the old missing response and distinguishes operator evidence from broker rejection proof");
            fixture.Tick(ready);
            check(ReferenceEquals(fixture.Intent,intent)&&fixture.Orders.Preparations==1&&fixture.Orders.Sends==0&&fixture.Reads.Calls==0,
                "repeated ticks cannot duplicate a granted legacy reevaluation or transmit its old rejected order");
        }
    }
    static void FinancialEvidenceChanged(string data,LiveOrderKind side,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"financial-change-"+side,side,Opening.AddMinutes(-1))) {
            fixture.Tick(Opening);
            check(fixture.Pending!=null&&fixture.Orders.Sends==0,
                side+": automatic retry remains unsent while its new financial evidence is pending");
            if(side==LiveOrderKind.Buy) {
                Set(fixture.Form,"cash",new AccountCashSnapshot{DepositCash=0,Cash100Orderable=0,ReceivedUtc=Opening});
                Set(fixture.Pending,"Capacity",new AccountOrderCapacity{Code=Symbol,LimitPrice=10460,Ratio=100,Quantity=0,MaximumQuantity=0,Amount=0,MaximumAmount=0,ReceivedUtc=Opening});
            }else Get<List<Holding>>(fixture.Form,"holdings").Clear();
            Call(fixture.Form,"SubmitPending",Opening);
            check(fixture.Pending==null&&fixture.Orders.Sends==0&&fixture.Journal.Snapshot.Orders.Count==1,
                side+": current zero capacity or absent holding prevents retransmitting the rejected quantity");
        }
    }
    static void ExpiredLegacyAttestation(string data,Action<bool,string> check) {
        var original=new DateTime(2026,11,13,0,15,0,DateTimeKind.Utc);
        var next=new DateTime(2026,11,16,0,15,0,DateTimeKind.Utc);
        using(var fixture=new Fixture(data,"expired-legacy-sell",LiveOrderKind.Sell,original.AddMinutes(-1),false,null,"305720",original)) {
            Set(fixture.Form,"legacySellAttestation",(Func<string,bool>)(key=>key==fixture.AccountKey));
            var authorization=Get<LiveTradingSession>(fixture.Form,"authorization");authorization.Authorize(Account,next);
            var session=Get<UnattendedSession>(fixture.Form,"session");session.Disarm();session.Arm(next);session.Tick(next,false);session.ScanCompleted(next);
            fixture.Refresh(next);Call(fixture.Form,"AuthorizeConfirmedLegacySells",next);
            check(authorization.Active&&fixture.Journal.Snapshot.LegacySellRechecks.Count==0&&fixture.Orders.Sends==0&&fixture.Orders.Preparations==0,
                "an expired legacy attestation cannot throw or revoke a later trading day's live authorization");
            fixture.Tick(next);
            check(fixture.Intent!=null&&fixture.Intent.ClientKey=="exit-20261116-305720"&&fixture.Orders.Preparations==1&&fixture.Orders.Sends==0,
                "the next trading day's fresh strategy decision is not blocked or replayed as yesterday's legacy order");
        }
    }
    public static void Run(string data,Action<bool,string> check) {
        WaitThenQueue(data,LiveOrderKind.Sell,check);WaitThenQueue(data,LiveOrderKind.Buy,check);
        BuyConditionRevoked(data,check);
        Refused(data,"legacy",fixture=>{},check);
        Refused(data,"unverified-scope",fixture=>fixture.Refresh(Opening,10460,false),check);
        Refused(data,"wrong-account",fixture=>Set(fixture.Form,"brokerOrders",fixture.OrdersAt(Opening,"12345678902")),check);
        Refused(data,"unknown",fixture=>{
            var value=fixture.Journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="005930",Quantity=1,LimitPrice=70000,ClientKey="unresolved-other"},Opening.AddSeconds(-30));
            fixture.Journal.MarkUnknown(value.Id,Opening.AddSeconds(-30));
        },check);
        Refused(data,"accepted-working",fixture=>{
            var at=Opening.AddSeconds(-30);
            var value=fixture.Journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code=Symbol,Quantity=2,LimitPrice=10460,ClientKey="accepted-existing"},at);
            fixture.Journal.MarkDispatching(value.Id,at);fixture.Journal.MarkRequestAccepted(value.Id,901,at);
            var fields=new[]{"0","000456789","0","0","0","synthetic accepted","0","0"};
            var response=LiveOrderResponseParser.Read(901,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{Account,"1111"});
            fixture.Journal.RecordAcknowledgement(value.Id,901,response.BrokerOrderNumber,false,at,response);
        },check);
        DurableCap(data,LiveOrderKind.Sell,check);DurableCap(data,LiveOrderKind.Buy,check);
        QueuedSignalRevoked(data,LiveOrderKind.Sell,check);QueuedSignalRevoked(data,LiveOrderKind.Buy,check);
        FinancialEvidenceChanged(data,LiveOrderKind.Sell,check);FinancialEvidenceChanged(data,LiveOrderKind.Buy,check);
        GrantedLegacySell(data,check);
        ExpiredLegacyAttestation(data,check);
    }
}
