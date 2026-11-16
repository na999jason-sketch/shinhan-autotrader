using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Actual read coordinator and form callbacks, with isolated account responses.
// No native control, login, registration, or real order API is invoked.
static class QuantityRefreshUiCases {
    const string Account="12345678901",Symbol="005930";
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly DateTime Opening=new DateTime(2026,11,16,0,3,0,DateTimeKind.Utc);
    static T Get<T>(object owner,string name){return (T)owner.GetType().GetField(name,Fields).GetValue(owner);}
    static void Set(object owner,string name,object value){owner.GetType().GetField(name,Fields).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] values) {
        try{return owner.GetType().GetMethod(name,Fields).Invoke(owner,values);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    sealed class HoldingsWire : IIndiTransport,IIndiDiagnostics {
        public int Quantity,Orderable=2,Requests,Setters;
        public readonly List<string> Queries=new List<string>();
        public event Action<int> ReceiveData;
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        string query;int pending;
        public IndiDiagnosticSnapshot ReadDiagnostics(){return new IndiDiagnosticSnapshot{ErrorState=0,CommState=0};}
        public object Invoke(string method,params object[] values) {
            if(method=="SetQueryName"){query=(string)values[0];return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData"){Setters++;return true;}
            if(method=="RequestData") {
                if(query!="SABA609Q1")throw new InvalidOperationException("Unexpected synthetic non-holdings read.");
                Queries.Add(query);pending=++Requests;return (short)pending;
            }
            if(method=="GetMultiRowCount")return 1;
            if(method=="GetSingleData") {
                int field=(int)values[0];
                return field==0?MarketTime.ToKorea(DateTime.UtcNow).ToString("yyyyMMdd",CultureInfo.InvariantCulture):field==16||field==19?"0":"";
            }
            if(method=="GetMultiData") {
                if((int)values[0]!=0)throw new InvalidOperationException("Unexpected synthetic holdings row.");
                switch((int)values[1]) {
                    case 0:return "A"+Symbol;case 1:return "SYNTHETIC QUANTITY UPDATE";case 2:return "1";case 3:return "보통";
                    case 4:return Quantity.ToString(CultureInfo.InvariantCulture);case 10:return Orderable.ToString(CultureInfo.InvariantCulture);
                    case 11:case 22:return "0";case 12:case 13:return "10000";
                    default:return "0";
                }
            }
            throw new InvalidOperationException("Unexpected synthetic provider method: "+method);
        }
        public void Complete(){int id=pending;if(id<=0)throw new InvalidOperationException("No synthetic read is pending.");pending=0;ReceiveData(id);}
        public void Dispose(){}
    }
    sealed class NoOrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Sends,Preparations;
        public event Action<LiveOrderAcknowledgement> Acknowledged {add{} remove{}}
        public void PrepareChannel(){Preparations++;}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;throw new InvalidOperationException("A holdings callback must never submit an order.");
        }
        public void Dispose(){}
    }
    sealed class Fixture : IDisposable {
        public readonly MainForm Form;
        public readonly LiveOrderJournal Journal;
        public readonly NoOrderWire Orders=new NoOrderWire();
        public readonly HoldingsWire Reads=new HoldingsWire();
        public readonly IndiRequestCoordinator Requests;
        public readonly RealtimeEventProcessor Processor;
        public readonly string Key,OrderId;
        public int Fatals;
        readonly object credentials;
        public Fixture(string data) {
            Form=new MainForm();Get<Timer>(Form,"scheduler").Stop();Form.ShowInTaskbar=false;Form.Show();Application.DoEvents();Get<Timer>(Form,"scheduler").Stop();
            Get<NumericUpDown>(Form,"budget").Value=1000000;Call(Form,"SaveSettings");
            var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            credentials=Activator.CreateInstance(type,new object[]{Account,"1111"});Key=(string)type.GetProperty("Key").GetValue(credentials,null);
            Set(Form,"credentials",credentials);Set(Form,"boundKey",Key);Set(Form,"lastReportKey",Key);Set(Form,"monitoring",true);
            Set(Form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Set(Form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Get<LiveTradingSession>(Form,"authorization").Authorize(Account,Opening);Get<UnattendedSession>(Form,"session").Arm(Opening);
            Journal=new LiveOrderJournal(Path.Combine(data,"quantity-refresh.xml"));Journal.BindAccount(Account);
            var now=DateTime.UtcNow.AddSeconds(-1);
            var order=Journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code=Symbol,Quantity=2,LimitPrice=10000,ClientKey="synthetic-accepted-buy"},now);
            Journal.MarkDispatching(order.Id,now);Journal.MarkRequestAccepted(order.Id,77,now);
            var values=new[]{"0","009021","0","","0","synthetic acceptance","0","0"};
            var proof=LiveOrderResponseParser.Read(77,index=>values[index],new IndiDiagnosticSnapshot{ErrorState=0},OrderResponseLayout.Modern,new[]{Account,"1111"});
            Journal.RecordAcknowledgement(order.Id,77,proof.BrokerOrderNumber,false,now,proof);OrderId=order.Id;
            Set(Form,"journal",Journal);Set(Form,"gateway",new LiveOrderGateway(Journal,Orders));Set(Form,"readiness",new BrokerReadiness(Key,Journal.Snapshot.AccountFingerprint));
            var highHolding=new Holding{Code=Symbol,Quantity=2,AvgPrice=10000,Price=11000};
            if(!Get<AccountAppSettingsStore>(Form,"settingsStore").ObserveHigh(Key,highHolding,
                new IndiReply{Code=Symbol,Price=11000,Clock="090300",HasTradeClock=true},Opening))throw new InvalidOperationException("Synthetic risk high was not seeded.");
            Get<List<Stock>>(Form,"symbols").Add(new Stock{Code=Symbol,Name="SYNTHETIC",Market="0"});
            var api=new IndiApi(Get<Panel>(Form,"nativeArea"));Requests=new IndiRequestCoordinator(Reads);
            Requests.Fatal+=message=>{Fatals++;Call(Form,"Halt");};
            Requests.Done+=reply=>{Get<Timer>(api,"timeout").Stop();Call(Form,"Safe",(Action)(()=>Call(Form,"Reply",reply)));};
            Set(api,"requests",Requests);Set(api,"hasReceivedData",true);Set(Form,"api",api);
            Processor=new RealtimeEventProcessor(index=>{throw new InvalidOperationException("No realtime getter is expected.");});Processor.BindAccount(Account);Processor.Track(Symbol);
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",Processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){Symbol});
            Set(realtime,"ownerThread",System.Threading.Thread.CurrentThread.ManagedThreadId);Set(realtime,"native",new object());
            Set(realtime,"<AccountSubscribed>k__BackingField",true);Set(Form,"realtime",realtime);
        }
        public void Read(int quantity,int orderable) {
            Reads.Quantity=quantity;Reads.Orderable=orderable;
            if(!Requests.Begin("TRADEHOLDINGS","SABA609Q1",AccountQueries.HoldingsInputs(Account,"1111"),null))throw new InvalidOperationException("Synthetic holdings read did not start.");
            Reads.Complete();
        }
        public bool SessionRetained {get{return ReferenceEquals(Get<object>(Form,"credentials"),credentials)&&Get<bool>(Form,"monitoring")
            &&Get<LiveTradingSession>(Form,"authorization").Active&&Get<UnattendedSession>(Form,"session").Armed;}}
        public LiveOrderRecord Order {get{return Journal.Snapshot.Orders.Single(value=>value.Id==OrderId);}}
        public DataGridViewRow Row {get{return Get<DataGridView>(Form,"holdingsGrid").Rows[0];}}
        public void Dispose(){Form.Close();Form.Dispose();Journal.Dispose();}
    }
    public static void Run(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data)) {
            fixture.Read(0,2);
            check(fixture.Fatals==0&&fixture.SessionRetained,"normal holdings callback with orderable above balance keeps armed account monitoring and credentials alive");
            var balance=Get<BrokerHoldingsSnapshot>(fixture.Form,"tradeHoldings");
            check(balance.Holdings.Single().Quantity==0&&balance.SellableQuantities[Symbol]==2&&!balance.QuantityExposureVerified,
                "mismatched raw balance0 and broker orderable2 remain separate without manufacturing a two-share position or valid exposure proof");
            check(fixture.Row.Cells.Count>=14&&Convert.ToDecimal(fixture.Row.Cells[2].Value,CultureInfo.InvariantCulture)==0
                &&Convert.ToDecimal(fixture.Row.Cells[12].Value,CultureInfo.InvariantCulture)==2,
                "actual holdings grid retains the raw zero position and separately displays positive broker orderable quantity");
            string pending=Convert.ToString(fixture.Row.Cells[13].Value,CultureInfo.InvariantCulture);
            check(pending.Contains("잔고수량 0")&&pending.Contains("주문가능수량 2")&&pending.Contains("대기"),
                "quantity wait explains the numeric discrepancy instead of reporting a transport failure");
            check(Get<Label>(fixture.Form,"portfolio").Text.Contains("수량 대사 중")&&Get<Label>(fixture.Form,"portfolio").Text.Contains("잠정값"),
                "portfolio amounts are visibly provisional while quantity exposure is unverified");
            check(Get<HashSet<string>>(fixture.Form,"trackedQuotes").Contains(Symbol),
                "a positive-orderable row remains tracked for quotes even before its raw balance quantity catches up");
            check(Get<AccountAppSettingsStore>(fixture.Form,"settingsStore").High(fixture.Key,Symbol,10000)==11000,
                "a transient zero-balance response does not delete the established trailing-stop high");
            check(fixture.Order.Status==LiveOrderStatus.Working&&fixture.Order.BrokerOrderNumber=="009021"&&fixture.Journal.Snapshot.Orders.Count==1
                &&fixture.Journal.Snapshot.Executions.Count==0&&fixture.Orders.Sends==0&&fixture.Orders.Preparations==0,
                "normal read preserves accepted buy identity and acknowledgement without inferring a fill or replaying it");
            fixture.Read(2,2);balance=Get<BrokerHoldingsSnapshot>(fixture.Form,"tradeHoldings");
            check(fixture.Fatals==0&&fixture.SessionRetained&&balance.QuantityExposureVerified
                &&balance.Holdings.Single().Quantity==2&&balance.SellableQuantities[Symbol]==2,
                "a later consistent normal callback clears the quantity-exposure barrier without requiring reconnection");
            check(!Convert.ToString(fixture.Row.Cells[13].Value,CultureInfo.InvariantCulture).Contains(" 대기")
                &&!Get<Label>(fixture.Form,"portfolio").Text.Contains("수량 대사 중"),
                "confirmed quantity refresh removes provisional quantity presentation through the real callback path");
            check(Get<AccountAppSettingsStore>(fixture.Form,"settingsStore").High(fixture.Key,Symbol,10000)==11000
                &&fixture.Order.Status==LiveOrderStatus.Working&&fixture.Order.BrokerOrderNumber=="009021"
                &&fixture.Reads.Requests==2&&fixture.Reads.Setters==16&&fixture.Orders.Sends==0&&fixture.Orders.Preparations==0,
                "consistent refresh preserves the risk high and original accepted order while using only two bound read requests");
        }
    }
}
