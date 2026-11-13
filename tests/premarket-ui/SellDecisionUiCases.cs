using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Actual form risk/display/scheduler paths with isolated account and quote data.
// No native Indi control or real account/order API is created.
static class SellDecisionUiCases {
    const string Account="12345678901",Symbol="005930";
    static readonly DateTime Opening=new DateTime(2026,11,16,0,15,0,DateTimeKind.Utc);
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static T Get<T>(object owner,string name){return (T)owner.GetType().GetField(name,Fields).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Fields).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] values) {
        try{return owner.GetType().GetMethod(name,Fields).Invoke(owner,values);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    sealed class ReadTransport : IIndiTransport {
        public int Requests,Calls;string query;
        public event Action<int> ReceiveData {add{} remove{}}
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public object Invoke(string method,params object[] values) {
            Calls++;
            if(method=="SetQueryName"){query=(string)values[0];return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData")return true;
            if(method=="RequestData"){Requests++;return (short)(100+Requests);}
            throw new InvalidOperationException("Unexpected synthetic read: "+method);
        }
        public void Dispose(){}
    }
    sealed class OrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Sends,Preparations;
        public event Action<LiveOrderAcknowledgement> Acknowledged {add{} remove{}}
        public void PrepareChannel(){Preparations++;}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;throw new InvalidOperationException("Risk display must not submit even a synthetic order.");
        }
        public void Dispose(){}
    }
    sealed class Fixture : IDisposable {
        public readonly MainForm Form;
        public readonly LiveOrderJournal Journal;
        public readonly ReadTransport Reads=new ReadTransport();
        public readonly OrderWire Orders=new OrderWire();
        public readonly IndiRequestCoordinator Requests;
        public readonly RealtimeEventProcessor Processor;
        public readonly Holding Held=new Holding{Code=Symbol,Name="SYNTHETIC RISK",Quantity=2,AvgPrice=10000,Price=9300};
        public readonly string Key;
        readonly Dictionary<int,object> quote=new Dictionary<int,object>();
        int volume=1000;
        public Fixture(string data,string name,bool armed=true) {
            Form=new MainForm();Get<Timer>(Form,"scheduler").Stop();Form.ShowInTaskbar=false;Form.Show();Application.DoEvents();Get<Timer>(Form,"scheduler").Stop();
            Get<NumericUpDown>(Form,"budget").Value=1000000;Call(Form,"SaveSettings");
            var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            var credential=Activator.CreateInstance(type,new object[]{Account,"1111"});
            Key=(string)type.GetProperty("Key").GetValue(credential,null);
            Set(Form,"credentials",credential);Set(Form,"boundKey",Key);Set(Form,"lastReportKey",Key);Set(Form,"monitoring",true);
            Set(Form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Set(Form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Get<AccountAppSettingsStore>(Form,"settingsStore").RefreshPositions(Key,new Holding[0]);
            Journal=new LiveOrderJournal(Path.Combine(data,"sell-decision-"+name+".xml"));Journal.BindAccount(Account);
            Set(Form,"journal",Journal);Set(Form,"gateway",new LiveOrderGateway(Journal,Orders));
            Set(Form,"readiness",new BrokerReadiness(Key,Journal.Snapshot.AccountFingerprint));
            if(armed) {
                var session=Get<UnattendedSession>(Form,"session");session.Arm(Opening);session.Tick(Opening,false);session.ScanCompleted(Opening);
                Get<LiveTradingSession>(Form,"authorization").Authorize(Account,Opening);
            }
            Get<List<Holding>>(Form,"holdings").Add(Held);Set(Form,"tradeHoldings",Balance(Opening));Set(Form,"holdingsReceivedUtc",(DateTime?)Opening);
            Get<Dictionary<string,CashInstrument>>(Form,"instruments")[Symbol]=new CashInstrument{Code=Symbol,Market="0",Product="ST"};
            var api=new IndiApi(Get<Panel>(Form,"nativeArea"));Requests=new IndiRequestCoordinator(Reads);
            Set(api,"requests",Requests);Set(api,"hasReceivedData",true);Set(Form,"api",api);
            Processor=new RealtimeEventProcessor(index=>quote[index]);Processor.BindAccount(Account);Processor.Track(Symbol);
            Processor.QuoteReceived+=(Action<RealtimeQuote>)Delegate.CreateDelegate(typeof(Action<RealtimeQuote>),Form,typeof(MainForm).GetMethod("OnRealtimeQuote",Fields));
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",Processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){Symbol});
            Set(realtime,"native",new object());Set(realtime,"<AccountSubscribed>k__BackingField",true);Set(Form,"realtime",realtime);
            Get<HashSet<string>>(Form,"trackedQuotes").Add(Symbol);
        }
        public BrokerHoldingsSnapshot Balance(DateTime at) {
            var value=new BrokerHoldingsSnapshot{BasisVerified=true,RequestBasisVerified=true,RequestStartedUtc=at.AddMilliseconds(-10),ReceivedUtc=at};
            value.Holdings.Add(Held);value.BasisValidated[Symbol]=true;value.CashSourceValidated[Symbol]=true;
            value.SellableQuantities[Symbol]=2;value.UnfilledQuantities[Symbol]=0;return value;
        }
        public void Quote(DateTime at,decimal price,bool confirmed=true) {
            quote[1]=Symbol;quote[3]=price.ToString(CultureInfo.InvariantCulture);quote[17]="1";
            if(confirmed){quote[2]=MarketTime.ToKorea(at.AddSeconds(-1)).ToString("HHmmss",CultureInfo.InvariantCulture);quote[7]=volume++;Processor.Process("SC",at.AddSeconds(-1));}
            quote[2]=MarketTime.ToKorea(at).ToString("HHmmss",CultureInfo.InvariantCulture);quote[7]=volume++;Processor.Process("SC",at);
            Held.Price=price;
        }
        public void Busy() {
            if(!Requests.Begin("BARS","TR_SCHART",new Dictionary<int,string>{{0,Symbol},{1,"D"},{2,"1"},{3,"00000000"},{4,"99999999"},{5,"2"}},Symbol))
                throw new InvalidOperationException("Synthetic pending read did not begin.");
        }
        public void Tick(DateTime at){Call(Form,"ScheduleAt",at);}
        public string Cell(string name){return Convert.ToString(Get<DataGridView>(Form,"holdingsGrid").Rows[0].Cells[name].Value,CultureInfo.InvariantCulture);}
        public void Dispose(){Form.Close();Form.Dispose();Journal.Dispose();}
    }
    static void SeparateRiskAndOrder(string data,Action<bool,string> check) {
        foreach(var item in new[]{new{Price=9300m,Risk="손절",Status="거절 자동 재검증 대기 / 1/3"},
            new{Price=11300m,Risk="익절",Status="당일 자동·선택 재시도 한도 도달"},
            new{Price=10200m,Risk="추적손절",Status="동일 종목 접수·체결 확인 중 / 자동 재전송 없음"}}) {
            using(var fixture=new Fixture(data,"separate-"+item.Risk)) {
                if(item.Risk=="추적손절") {
                    fixture.Quote(Opening,11000);Call(fixture.Form,"EvaluateHoldingSell",fixture.Held,
                        new IndiReply{Code=Symbol,Price=11000,Clock="091500",HasTradeClock=true},Opening);
                }
                var at=Opening.AddSeconds(2);fixture.Quote(at,item.Price);
                var reply=new IndiReply{Code=Symbol,Price=item.Price,Clock="091502",HasTradeClock=true};
                Call(fixture.Form,"EvaluateHoldingSell",fixture.Held,reply,at);
                Get<Dictionary<string,string>>(fixture.Form,"automaticSellStatus")[Symbol]=item.Status;Call(fixture.Form,"RefreshAccountAt",at);
                check(fixture.Cell("매도판정").Contains(item.Risk),item.Risk+": retry/order status cannot replace the current strategy decision");
                check(fixture.Cell("매도 주문상태")==item.Status,item.Risk+": the distinct order-status column retains its wait or block explanation");
                check(fixture.Orders.Sends==0&&fixture.Orders.Preparations==0&&fixture.Reads.Calls==0,
                    item.Risk+": risk and order presentation does not prepare or transmit an order");
            }
        }
    }
    static void SchedulerDoesNotStarveRisk(string data,string mode,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"scheduler-"+mode)) {
            fixture.Quote(Opening,9300);
            if(mode=="busy")fixture.Busy();
            if(mode=="queued")Get<Queue<string>>(fixture.Form,"accountReads").Enqueue("CASH");
            if(mode=="preflight") {
                var type=typeof(MainForm).GetNestedType("EntryRequest",BindingFlags.NonPublic);
                var entry=Activator.CreateInstance(type,true);
                Set(entry,"Intent",new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code=Symbol,Quantity=2,LimitPrice=9300,ClientKey="synthetic-preflight"});
                Set(entry,"WaitingFor","BARS");Set(entry,"NotBeforeUtc",Opening);Set(fixture.Form,"entry",entry);
            }
            fixture.Tick(Opening);
            check(fixture.Cell("매도판정").Contains("손절"),mode+": a fresh confirmed quote updates sell risk before scheduler early returns");
            check(fixture.Orders.Sends==0&&fixture.Orders.Preparations==0&&fixture.Reads.Requests==(mode=="preflight"?0:1),
                mode+": risk refresh does not create another order or bypass the serialized read queue");
        }
    }
    static void PausedRecoveryPreview(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"paused-recovery",false)) {
            var order=fixture.Journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code=Symbol,Quantity=2,LimitPrice=9300,ClientKey="known-paused-order"},Opening.AddMinutes(-1));
            fixture.Journal.MarkDispatching(order.Id,Opening.AddMinutes(-1));fixture.Journal.MarkRequestAccepted(order.Id,801,Opening.AddMinutes(-1));
            var values=new[]{"0","000654321","0","0","0","synthetic accepted","0","0"};
            var proof=LiveOrderResponseParser.Read(801,index=>values[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{Account,"1111"});
            fixture.Journal.RecordAcknowledgement(order.Id,801,proof.BrokerOrderNumber,false,Opening.AddMinutes(-1),proof);fixture.Journal.MarkUnknown(order.Id,Opening.AddSeconds(-30));
            fixture.Busy();fixture.Quote(Opening,9300);
            Call(fixture.Form,"PauseOrdersForReconciliation","미확정 주문 자동 대사 중 / 계좌 조회·감시 유지",Opening);
            fixture.Tick(Opening);
            check(fixture.Cell("매도판정").Contains("손절"),"paused recovery still displays current sell risk from verified holdings and fresh confirmed SC");
            check(fixture.Cell("매도 주문상태").Contains("대사"),"paused recovery explains its order restriction separately from the sell strategy decision");
            check(fixture.Journal.RecoveryRequired&&!Get<LiveTradingSession>(fixture.Form,"authorization").Active
                &&Get<object>(fixture.Form,"entry")==null&&fixture.Orders.Sends==0&&fixture.Orders.Preparations==0,
                "read-only sell preview cannot rearm trading clear Unknown or prepare a replacement order");
            check(fixture.Requests.Busy&&fixture.Reads.Requests==1,
                "pausing for Unknown reconciliation retains the existing read request until its callback completes");
        }
    }
    static void DirectCashSnapshotWaits(string data,string mode,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"direct-cash-"+mode,false)) {
            fixture.Quote(Opening,9300);
            var balance=Get<BrokerHoldingsSnapshot>(fixture.Form,"tradeHoldings");
            if(mode=="old") {
                balance.RequestStartedUtc=Opening.AddSeconds(-122);balance.ReceivedUtc=Opening.AddSeconds(-121);
            } else if(mode=="non-utc") {
                balance.RequestStartedUtc=DateTime.SpecifyKind(Opening.AddMilliseconds(-10),DateTimeKind.Unspecified);
                balance.ReceivedUtc=DateTime.SpecifyKind(Opening,DateTimeKind.Unspecified);
            } else {
                balance.RequestStartedUtc=Opening.AddDays(-1).AddMilliseconds(-10);balance.ReceivedUtc=Opening.AddDays(-1);
            }
            fixture.Busy();fixture.Tick(Opening);
            check(fixture.Cell("매도판정")=="최신 계좌 잔고 확인 대기",
                mode+": direct cash/source validation cannot override invalid balance time evidence even with fresh confirmed SC");
            check(fixture.Orders.Sends==0&&fixture.Orders.Preparations==0&&fixture.Reads.Requests==1,
                mode+": invalid direct-cash balance evidence preserves the read queue and cannot prepare or send orders");
        }
    }
    static void InvalidEvidenceWaits(string data,string mode,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"invalid-"+mode,false)) {
            fixture.Quote(Opening,9300,mode!="first-tick");
            if(mode=="basis")Get<BrokerHoldingsSnapshot>(fixture.Form,"tradeHoldings").BasisVerified=false;
            var at=mode=="stale"?Opening.AddSeconds(46):Opening;
            // Hold the read channel busy so this fixture observes risk only.
            fixture.Busy();fixture.Tick(at);
            check(!fixture.Cell("매도판정").Contains("손절"),mode+": missing confirmation or stale/unverified evidence cannot fabricate a sell decision");
            check(fixture.Orders.Sends==0&&fixture.Orders.Preparations==0&&fixture.Reads.Requests==1,
                mode+": waiting risk display does not execute a new account or order request");
        }
    }
    static void RepeatedBalanceCallbacks(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"balance-callbacks")) {
            fixture.Busy();
            for(int index=0;index<3;index++) {
                var at=Opening.AddSeconds(index);fixture.Quote(at,9300);
                Call(fixture.Form,"ApplyReply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=fixture.Balance(at),
                    RequestStartedUtc=at.AddMilliseconds(-10),ReceivedUtc=at});fixture.Tick(at);
                check(fixture.Cell("매도판정").Contains("손절"),"balance refresh "+index+": callback cache clearing cannot hide fresh risk behind an ongoing read");
            }
            check(fixture.Reads.Requests==1&&fixture.Orders.Sends==0&&fixture.Orders.Preparations==0,
                "repeated balance risk refresh preserves the single pending read and performs no order preparation");
        }
    }
    public static void Run(string data,Action<bool,string> check) {
        SeparateRiskAndOrder(data,check);
        SchedulerDoesNotStarveRisk(data,"busy",check);SchedulerDoesNotStarveRisk(data,"queued",check);SchedulerDoesNotStarveRisk(data,"preflight",check);
        PausedRecoveryPreview(data,check);
        InvalidEvidenceWaits(data,"first-tick",check);InvalidEvidenceWaits(data,"stale",check);InvalidEvidenceWaits(data,"basis",check);
        DirectCashSnapshotWaits(data,"old",check);DirectCashSnapshotWaits(data,"non-utc",check);DirectCashSnapshotWaits(data,"wrong-day",check);
        RepeatedBalanceCallbacks(data,check);
    }
}
