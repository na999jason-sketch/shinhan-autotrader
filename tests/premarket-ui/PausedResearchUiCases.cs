using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Runs the form's read-only research paths with copied synthetic bars/SC data.
// Neither a native control nor a financial query/order implementation exists.
static class PausedResearchUiCases {
    const string Account="12345678901";
    static readonly BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static T Get<T>(object owner,string field){return (T)owner.GetType().GetField(field,Fields).GetValue(owner);}
    static void Set(object owner,string field,object value){owner.GetType().GetField(field,Fields).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] arguments) {
        try{return owner.GetType().GetMethod(name,Fields).Invoke(owner,arguments);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    sealed class ResearchTransport : IIndiTransport {
        public event Action<int> ReceiveData;
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public readonly List<Bar> Bars=new List<Bar>();
        public int Requests;string query;int pending;
        public void History(DateTime day) {
            Bars.Clear();var dates=new List<DateTime>();var date=day.AddDays(-1);
            while(dates.Count<45) {
                if(date.DayOfWeek!=DayOfWeek.Saturday&&date.DayOfWeek!=DayOfWeek.Sunday)dates.Add(date);
                date=date.AddDays(-1);
            }
            dates.Reverse();
            for(int i=0;i<dates.Count;i++) {
                decimal price=7000+i*10;
                Bars.Add(new Bar{Date=dates[i].ToString("yyyyMMdd",CultureInfo.InvariantCulture),
                    Open=price,High=price+10,Low=price-10,Close=price,Volume=1000});
            }
        }
        public object Invoke(string method,params object[] arguments) {
            if(method=="SetQueryName"){query=(string)arguments[0];return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData")return true;
            if(method=="RequestData") {
                if(query!="TR_SCHART")throw new InvalidOperationException("Unexpected financial/native request: "+query);
                pending=++Requests;return (short)pending;
            }
            if(method=="GetMultiRowCount")return Bars.Count;
            if(method=="GetMultiData") {
                var bar=Bars[(int)arguments[0]];int column=(int)arguments[1];
                if(column==0)return bar.Date;
                decimal value=column==2?bar.Open:column==3?bar.High:column==4?bar.Low:column==5?bar.Close:column==9?bar.Volume:0;
                return value.ToString(CultureInfo.InvariantCulture);
            }
            throw new InvalidOperationException("Unexpected research provider method: "+method);
        }
        public void Complete() {
            if(pending==0)throw new InvalidOperationException("No synthetic bars are pending.");
            int id=pending;pending=0;if(ReceiveData!=null)ReceiveData(id);
        }
        public void Dispose(){}
    }
    sealed class NoOrderWire : ILiveOrderWire,ILiveOrderChannelPreparation {
        public int Sends,Preparations;
        public event Action<LiveOrderAcknowledgement> Acknowledged {add{} remove{}}
        public void PrepareChannel(){Preparations++;}
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            Sends++;throw new InvalidOperationException("Read-only research must not transmit an order.");
        }
        public void Dispose(){}
    }
    sealed class Fixture : IDisposable {
        public readonly MainForm Form;
        public readonly ResearchTransport Reads=new ResearchTransport();
        public readonly NoOrderWire Orders=new NoOrderWire();
        public readonly LiveOrderJournal Journal;
        public readonly IndiApi Api;
        public Fixture(string data,string name,bool unresolved) {
            Form=new MainForm();Get<Timer>(Form,"scheduler").Stop();
            Form.ShowInTaskbar=false;Form.Show();Application.DoEvents();Get<Timer>(Form,"scheduler").Stop();
            Get<NumericUpDown>(Form,"budget").Value=1000000;Call(Form,"SaveSettings");
            var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
            var credentials=Activator.CreateInstance(type,new object[]{Account,"1111"});
            var key=(string)type.GetProperty("Key").GetValue(credentials,null);
            Set(Form,"credentials",credentials);Set(Form,"boundKey",key);Set(Form,"lastReportKey",key);
            Set(Form,"monitoring",true);
            Set(Form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Journal=new LiveOrderJournal(Path.Combine(data,"paused-research-"+name+".xml"));Journal.BindAccount(Account);
            if(unresolved) {
                var now=DateTime.UtcNow;
                var order=Journal.Prepare(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code="005930",Quantity=2,
                    LimitPrice=70000,ClientKey="paused-research-"+name},now);
                Journal.MarkDispatching(order.Id,now);Journal.MarkRequestAccepted(order.Id,401,now);
                Journal.MarkUnknown(order.Id,now);
            }
            Set(Form,"journal",Journal);Set(Form,"gateway",new LiveOrderGateway(Journal,Orders));
            Set(Form,"readiness",new BrokerReadiness(key,Journal.Snapshot.AccountFingerprint));
            Get<List<Stock>>(Form,"symbols").AddRange(new[]{new Stock{Code="000660",Name="SYNTHETIC A",Market="0"},
                new Stock{Code="035420",Name="SYNTHETIC B",Market="0"}});
            Api=new IndiApi(Get<Panel>(Form,"nativeArea"));var requests=new IndiRequestCoordinator(Reads);
            requests.Fatal+=message=>{throw new InvalidOperationException("Synthetic research failed: "+message);};
            requests.Done+=reply=>{Get<Timer>(Api,"timeout").Stop();Call(Form,"Reply",reply);};
            Set(Api,"requests",requests);Set(Api,"hasReceivedData",true);Set(Form,"api",Api);
        }
        public Dictionary<string,PremarketReference> Prepared {get{return Get<Dictionary<string,PremarketReference>>(Form,"preparedReferences");}}
        public void PrepareOne(DateTime now) {
            Call(Form,"BeginResearch",now,false);Reads.History(Get<DateTime?>(Form,"researchDateKorea").Value);
            // Account refresh has separate coverage. Isolate the queued daily
            // bars without fabricating a balance or clearing the recovery gate.
            Get<Queue<string>>(Form,"accountReads").Clear();
            Set(Form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
            Call(Form,"ScheduleAt",now);if(!Api.Busy)throw new InvalidOperationException("Readonly bars did not start.");
            Reads.Complete();
        }
        public void Quote(DateTime now,decimal price,int volume) {
            RealtimeIndi realtime=Get<RealtimeIndi>(Form,"realtime");
            RealtimeEventProcessor processor;
            if(realtime==null) {
                processor=new RealtimeEventProcessor(index=>quoteFields[index]);processor.Track("000660");
                realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
                Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){"000660"});
                Set(Form,"realtime",realtime);Get<HashSet<string>>(Form,"trackedQuotes").Add("000660");
            }else processor=Get<RealtimeEventProcessor>(realtime,"processor");
            quoteFields[1]="000660";quoteFields[3]=price.ToString(CultureInfo.InvariantCulture);quoteFields[17]="1";
            quoteFields[2]=MarketTime.ToKorea(now.AddSeconds(-1)).ToString("HHmmss",CultureInfo.InvariantCulture);
            quoteFields[7]=volume;processor.Process("SC",now.AddSeconds(-1));
            quoteFields[2]=MarketTime.ToKorea(now).ToString("HHmmss",CultureInfo.InvariantCulture);
            quoteFields[7]=volume+1;processor.Process("SC",now);
        }
        readonly Dictionary<int,object> quoteFields=new Dictionary<int,object>();
        public string CandidateStatus {get{return Convert.ToString(Get<DataGridView>(Form,"candidatesGrid").Rows[0].Cells["주문상태"].Value,CultureInfo.InvariantCulture);}}
        public void Dispose(){Form.Close();Form.Dispose();Journal.Dispose();}
    }
    static void NoOrderSideEffects(Fixture fixture,Action<bool,string> check,string scenario) {
        check(!Get<LiveTradingSession>(fixture.Form,"authorization").Active&&!Get<UnattendedSession>(fixture.Form,"session").Armed
            &&Get<Queue<Candidate>>(fixture.Form,"buyQuotes").Count==0&&Get<object>(fixture.Form,"entry")==null
            &&fixture.Orders.Preparations==0&&fixture.Orders.Sends==0,
            scenario+": read-only analysis neither authorizes, queues nor transmits a live order");
    }
    static void RecoveryBlockedResearch(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"recovery",true)) {
            var form=fixture.Form;var credentials=Get<object>(form,"credentials");
            Call(form,"Arm");
            check(Get<bool>(form,"researchRequested")&&Get<DateTime?>(form,"researchDateKorea").HasValue
                &&Get<bool>(form,"monitoring")&&fixture.Journal.RecoveryRequired&&ReferenceEquals(Get<object>(form,"credentials"),credentials),
                "recovery-blocked start requests public research while retaining the read-only account session");
            check(fixture.Reads.Requests==0,"recovery-blocked start schedules research without invoking a provider synchronously");
            NoOrderSideEffects(fixture,check,"blocked start");
            // Arm uses the wall clock. Advance only the synthetic scheduler to
            // its chosen target date, including an after-close/weekend run.
            var day=Get<DateTime?>(form,"researchDateKorea").Value;
            var now=DateTime.SpecifyKind(day.AddHours(9).AddMinutes(15),DateTimeKind.Utc).AddHours(-9);
            fixture.PrepareOne(now);
            var prepared=fixture.Prepared["000660"];
            check(fixture.Prepared.Count==1&&Get<int>(form,"scanned")==1&&Get<Queue<Stock>>(form,"scans").Count==1
                &&Get<DataGridView>(form,"candidatesGrid").Rows.Count==1&&Get<bool>(form,"scanning"),
                "partial readonly daily-bar responses become visible candidates before the remaining scan completes");
            Call(form,"Arm");
            check(ReferenceEquals(fixture.Prepared["000660"],prepared)&&Get<int>(form,"scanned")==1
                &&Get<Queue<Stock>>(form,"scans").Count==1&&Get<DataGridView>(form,"candidatesGrid").Rows.Count==1,
                "another recovery-blocked start preserves prepared references, rows and remaining analysis");
            int records=fixture.Journal.Snapshot.Orders.Count,requests=fixture.Reads.Requests;
            fixture.Quote(now.AddSeconds(10),prepared.PriorHigh+10,100000);
            Call(form,"EvaluatePreparedSignals",now.AddSeconds(10));
            check(fixture.CandidateStatus=="매수 조건 충족 / 미확정 주문 대사 대기",
                "a qualified readonly candidate explains the recovery gate instead of hiding its completed live analysis");
            fixture.Quote(now.AddSeconds(12),prepared.PriorHigh,100010);
            Call(form,"EvaluatePreparedSignals",now.AddSeconds(12));
            check(fixture.CandidateStatus.Contains("미돌파"),"readonly candidates continue showing current nonqualifying breakout conditions");
            check(fixture.Journal.RecoveryRequired&&fixture.Journal.Snapshot.Orders.Count==records&&fixture.Reads.Requests==requests,
                "readonly candidate evaluation neither changes prior order evidence nor requests an account/order operation");
            NoOrderSideEffects(fixture,check,"paused candidate evaluation");
        }
    }
    static void UnarmedResearch(string data,Action<bool,string> check) {
        using(var fixture=new Fixture(data,"unarmed",false)) {
            var now=new DateTime(2026,11,16,0,15,0,DateTimeKind.Utc);
            fixture.PrepareOne(now);fixture.Quote(now.AddSeconds(10),fixture.Prepared["000660"].PriorHigh+10,100000);
            Call(fixture.Form,"EvaluatePreparedSignals",now.AddSeconds(10));
            check(fixture.CandidateStatus=="매수 조건 충족 / 실주문 미시작",
                "unarmed research displays a qualified live candidate and the explicit-start requirement");
            check(fixture.Journal.Snapshot.Orders.Count==0&&!fixture.Journal.RecoveryRequired,
                "unarmed public research leaves the empty order journal unchanged");
            NoOrderSideEffects(fixture,check,"unarmed candidate evaluation");
        }
    }
    public static void Run(string data,Action<bool,string> check) {
        RecoveryBlockedResearch(data,check);UnarmedResearch(data,check);
    }
}
