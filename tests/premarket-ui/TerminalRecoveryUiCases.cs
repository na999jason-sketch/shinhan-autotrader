using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using System.Xml.Serialization;
using ShinhanNativeTrader;

// Actual form callbacks and journal persistence, isolated fake account data.
// No native control, account API or broker order is created by this fixture.
static class TerminalRecoveryUiCases {
    const string Account="12345678901", OtherAccount="12345678902";
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static T Get<T>(object owner,string field){return (T)owner.GetType().GetField(field,Fields).GetValue(owner);}
    static void Set(object owner,string field,object value){owner.GetType().GetField(field,Fields).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] arguments) {
        try{return owner.GetType().GetMethod(name,Fields).Invoke(owner,arguments);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    sealed class NoReadTransport : IIndiTransport {
        public int Calls;
        public event Action<int> ReceiveData {add{} remove{}}
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public object Invoke(string method,params object[] arguments) {
            Calls++;throw new InvalidOperationException("Unexpected synthetic provider call: "+method);
        }
        public void Dispose(){}
    }
    sealed class NoOrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Sends,Preparations;
        public event Action<LiveOrderAcknowledgement> Acknowledged {add{} remove{}}
        public void PrepareChannel(){Preparations++;}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;throw new InvalidOperationException("Recovery must never transmit an order.");
        }
        public void Dispose(){}
    }
    sealed class Fixture : IDisposable {
        public readonly MainForm Form;
        public readonly LiveOrderJournal Journal;
        public readonly NoOrderWire Orders=new NoOrderWire();
        public readonly NoReadTransport Reads=new NoReadTransport();
        public readonly string Path;
        public readonly string OrderId;
        public Fixture(string data,string scenario) {
            Form=new MainForm();Get<Timer>(Form,"scheduler").Stop();
            Form.ShowInTaskbar=false;Form.Show();Application.DoEvents();Get<Timer>(Form,"scheduler").Stop();
            Get<NumericUpDown>(Form,"budget").Value=1000000;Call(Form,"SaveSettings");
            var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            var credentials=Activator.CreateInstance(type,new object[]{Account,"1111"});
            var key=(string)type.GetProperty("Key").GetValue(credentials,null);
            Set(Form,"credentials",credentials);Set(Form,"boundKey",key);Set(Form,"lastReportKey",key);
            Set(Form,"monitoring",true);
            Set(Form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Set(Form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Path=System.IO.Path.Combine(data,"terminal-recovery-"+scenario+".xml");
            Journal=new LiveOrderJournal(Path);Journal.BindAccount(Account);
            var observed=DateTime.UtcNow;var utc=observed.AddSeconds(-1);
            if(MarketTime.ToKorea(utc).Date!=MarketTime.ToKorea(observed).Date)utc=observed;
            // A prior same-day acknowledged order is the only candidate. Its
            // positive identity is never reconstructed from symbol or amount.
            var order=Journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="005930",Quantity=3,
                LimitPrice=70000,ClientKey="terminal-recovery-"+scenario},utc);
            Journal.MarkDispatching(order.Id,utc);Journal.MarkRequestAccepted(order.Id,401,utc);
            var fields=new[]{"0","000345678","0","0","0","synthetic acceptance","0","0"};
            var response=LiveOrderResponseParser.Read(401,index=>fields[index],new IndiDiagnosticSnapshot{ErrorState=0},
                OrderResponseLayout.Modern,new[]{Account,"1111"});
            Journal.RecordAcknowledgement(order.Id,401,response.BrokerOrderNumber,false,utc,response);
            Journal.MarkUnknown(order.Id,utc);OrderId=order.Id;
            Set(Form,"journal",Journal);Set(Form,"gateway",new LiveOrderGateway(Journal,Orders));
            Set(Form,"readiness",new BrokerReadiness(key,Journal.Snapshot.AccountFingerprint));
            Get<List<Stock>>(Form,"symbols").Add(new Stock{Code="005930",Name="SYNTHETIC",Market="0"});
            var api=new IndiApi(Get<Panel>(Form,"nativeArea"));
            Set(api,"requests",new IndiRequestCoordinator(Reads));Set(api,"hasReceivedData",true);Set(Form,"api",api);
            var processor=new RealtimeEventProcessor(index=>{throw new InvalidOperationException("No realtime fields are expected.");});
            processor.BindAccount(Account);
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal));
            Set(realtime,"native",new object());Set(realtime,"<AccountSubscribed>k__BackingField",true);Set(Form,"realtime",realtime);
        }
        public BrokerOrdersSnapshot Snapshot() {
            var now=DateTime.UtcNow;
            var value=new BrokerOrdersSnapshot{TradingDate=Journal.Snapshot.Orders.Single().TradingDate};
            value.Orders.Add(new BrokerOrderRow{OrderNumber="345678",Code="005930",SideCode="1",Quantity=3,
                FilledQuantity=3,OpenQuantity=0,LimitPrice=70000,AverageFillPrice=70000,ProductCode="01",CreditCode="00",
                ConfirmationCode="0",ConfirmedQuantity=0,AutoCancelledQuantity=0,OriginalOrderNumber="",AmendCancelCode="0"});
            Bind(value,now,now,true,Account);return value;
        }
        public void Apply(BrokerOrdersSnapshot value) {
            Call(Form,"ApplyReply",new IndiReply{Kind="ORDERS",BrokerOrders=value,
                RequestStartedUtc=value.RequestStartedUtc,ReceivedUtc=value.ReceivedUtc});
        }
        public LiveOrderRecord Current {get{return Journal.Snapshot.Orders.Single();}}
        public void Dispose(){Form.Close();Form.Dispose();Journal.Dispose();}
    }
    static void Bind(BrokerOrdersSnapshot value,DateTime started,DateTime received,bool verified,string account) {
        Call(value,"BindRequestScope",started,received,verified,account);
    }
    static void CheckNoBrokerSideEffect(Fixture fixture,Action<bool,string> check,string scenario) {
        check(fixture.Reads.Calls==0&&fixture.Orders.Sends==0&&fixture.Orders.Preparations==0,
            scenario+": applying a received recovery snapshot never initializes a channel or invokes an API");
    }
    static void TerminalMatch(string data,Action<bool,string> check) {
        string path;
        using(var fixture=new Fixture(data,"terminal")) {
            path=fixture.Path;var credentials=Get<object>(fixture.Form,"credentials");
            fixture.Apply(fixture.Snapshot());var recovered=fixture.Current;
            check(recovered.Status==LiveOrderStatus.Filled&&recovered.FilledQuantity==3&&recovered.FillCheckpoint!=null
                &&!fixture.Journal.RecoveryRequired,"exact current-account terminal broker row resolves a previously accepted unknown order");
            check(fixture.Journal.Snapshot.Executions.Count==0,
                "terminal recovery stores a cumulative broker checkpoint without inventing execution IDs");
            check(ReferenceEquals(Get<object>(fixture.Form,"credentials"),credentials)&&Get<bool>(fixture.Form,"monitoring")
                &&!Get<LiveTradingSession>(fixture.Form,"authorization").Active,
                "read-only terminal reconciliation keeps monitoring and credentials but never arms live trading");
            LiveOrderJournalState saved;
            using(var file=File.OpenRead(path))saved=(LiveOrderJournalState)new XmlSerializer(typeof(LiveOrderJournalState)).Deserialize(file);
            check(saved.Orders.Single().Status==LiveOrderStatus.Filled&&saved.Orders.Single().FillCheckpoint!=null&&saved.Executions.Count==0,
                "terminal fill checkpoint is durably saved before the recovery gate clears");
            var grid=Get<DataGridView>(fixture.Form,"ordersGrid");
            check(grid.Rows.Count==1&&Convert.ToDecimal(grid.Rows[0].Cells["누적체결"].Value,CultureInfo.InvariantCulture)==3,
                "recovery display uses the broker's terminal cumulative quantity rather than a stale local remainder");
            CheckNoBrokerSideEffect(fixture,check,"terminal");
            Call(fixture.Form,"Arm");
            check(Get<LiveTradingSession>(fixture.Form,"authorization").Active&&Get<bool>(fixture.Form,"monitoring")
                &&ReferenceEquals(Get<object>(fixture.Form,"credentials"),credentials),
                "after read-only reconciliation the user's explicit start can arm the retained account session");
            CheckNoBrokerSideEffect(fixture,check,"explicit start after recovery");
        }
        using(var restarted=new LiveOrderJournal(path))
            check(restarted.Snapshot.Orders.Single().Status==LiveOrderStatus.Filled&&!restarted.RecoveryRequired,
                "a saved terminal checkpoint survives restart without becoming an unresolved order again");
    }
    static void Refused(string data,string scenario,Action<BrokerOrdersSnapshot,BrokerOrderRow> mutate,Action<bool,string> check) {
        using(var fixture=new Fixture(data,scenario)) {
            var credentials=Get<object>(fixture.Form,"credentials");var value=fixture.Snapshot();
            mutate(value,value.Orders.Single());fixture.Apply(value);
            check(fixture.Current.Status==LiveOrderStatus.Unknown&&fixture.Current.FillCheckpoint==null
                &&fixture.Journal.RecoveryRequired&&fixture.Journal.Snapshot.Executions.Count==0,
                scenario+": incomplete or conflicting broker evidence cannot clear the recovery gate or invent fills");
            check(ReferenceEquals(Get<object>(fixture.Form,"credentials"),credentials)&&Get<bool>(fixture.Form,"monitoring")
                &&!Get<LiveTradingSession>(fixture.Form,"authorization").Active,
                scenario+": unresolved recovery retains the read-only account session without arming it");
            Call(fixture.Form,"Arm");
            check(ReferenceEquals(Get<object>(fixture.Form,"credentials"),credentials)&&Get<bool>(fixture.Form,"monitoring")
                &&!Get<LiveTradingSession>(fixture.Form,"authorization").Active&&fixture.Journal.RecoveryRequired,
                scenario+": start while recovery is pending preserves account monitoring and refuses new live orders");
            CheckNoBrokerSideEffect(fixture,check,scenario);
        }
    }
    static void ConfirmationEvidence(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"blank-confirmation")) {
            var value=fixture.Snapshot();value.Orders.Single().ConfirmationCode="";fixture.Apply(value);
            check(fixture.Current.Status==LiveOrderStatus.Filled&&fixture.Current.FillCheckpoint!=null
                &&fixture.Current.FillCheckpoint.ConfirmationCode==""
                &&fixture.Current.FillCheckpoint.ConfirmationBasis==BrokerFillConfirmationBasis.IndependentFullFillWithoutConfirmation,
                "missing confirmation metadata is preserved separately from independently verified known-order full fill evidence");
            CheckNoBrokerSideEffect(fixture,check,"blank terminal confirmation");
        }
        using(var fixture=new Fixture(data,"full-fill-confirmation-conflict")) {
            var value=fixture.Snapshot();value.Orders.Single().ConfirmationCode="1";fixture.Apply(value);
            var grid=Get<DataGridView>(fixture.Form,"ordersGrid");
            check(fixture.Current.Status==LiveOrderStatus.Unknown&&fixture.Journal.RecoveryRequired
                &&fixture.Journal.Snapshot.Executions.Count==0&&fixture.Current.FillCheckpoint==null,
                "a full-fill row with confirmation code 1 cannot silently become normal or clear the unresolved order");
            check(grid.Rows.Count==1&&Convert.ToDecimal(grid.Rows[0].Cells[4].Value)==3
                &&Convert.ToDecimal(grid.Rows[0].Cells[5].Value)==0
                &&Convert.ToString(grid.Rows[0].Cells[8].Value).Contains("전량체결 조회 · 확인구분 충돌")
                &&Convert.ToString(grid.Rows[0].Cells[9].Value).Contains("확인구분 1"),
                "the UI retains reported filled and open quantities and explains the exact finite confirmation conflict");
            Call(fixture.Form,"Arm");
            check(!Get<LiveTradingSession>(fixture.Form,"authorization").Active&&fixture.Journal.RecoveryRequired,
                "a confirmation conflict cannot rearm trading through the start button");
            CheckNoBrokerSideEffect(fixture,check,"full-fill confirmation conflict");
        }
    }
    public static void Run(string data,Action<bool,string> check) {
        TerminalMatch(data,check);
        ConfirmationEvidence(data,check);
        Refused(data,"empty",(value,row)=>value.Orders.Clear(),check);
        Refused(data,"unverified",(value,row)=>Bind(value,value.RequestStartedUtc,value.ReceivedUtc,false,Account),check);
        Refused(data,"wrong-account",(value,row)=>Bind(value,value.RequestStartedUtc,value.ReceivedUtc,true,OtherAccount),check);
        Refused(data,"partial",(value,row)=>{row.FilledQuantity=1;row.OpenQuantity=2;},check);
        Refused(data,"wrong-identity",(value,row)=>row.OrderNumber="987654",check);
        Refused(data,"wrong-date",(value,row)=>{
            value.TradingDate=MarketTime.ToKorea(DateTime.UtcNow).AddDays(-1).ToString("yyyyMMdd",CultureInfo.InvariantCulture);
            Bind(value,value.RequestStartedUtc,value.ReceivedUtc,true,Account);
        },check);
        Refused(data,"wrong-product",(value,row)=>row.ProductCode="02",check);
        Refused(data,"wrong-price",(value,row)=>row.LimitPrice=70100,check);
        Refused(data,"stale",(value,row)=>{var time=DateTime.UtcNow.AddSeconds(-20);Bind(value,time,time,true,Account);},check);
    }
}
