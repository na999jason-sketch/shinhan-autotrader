using System;
using System.Collections.Generic;
using System.Globalization;

namespace ShinhanNativeTrader {
    public static class IndiTests {
        sealed class FakeIndi : IIndiTransport,IIndiDiagnostics {
            public event Action<int> ReceiveData;
            public event Action<int> ReceiveSysMsg;
            public event Action<string> Error;
            public int Id=7,Rows,RequestCount,ResponseErrorState;
            public bool OverrideRequestReturn,DiagnosticThrows,ErrorAfterRequest;
            public object RequestReturn;
            public int DiagnosticReads;
            public readonly IndiDiagnosticSnapshot Diagnostic=new IndiDiagnosticSnapshot{CommState=0,ErrorState=0,ErrorCode="0",Message=""};
            public bool SetQueryAccepted=true,SetFieldAccepted=true,OverrideSetterReturns;
            public object QuerySetterReturn,FieldSetterReturn;
            public string QueryName,QueryReadbackOverride;
            public string ThrowMethod;
            public Action DuringRequest,DuringSetField;
            public readonly Dictionary<string,object> Fields=new Dictionary<string,object>();
            public object Invoke(string method,params object[] args) {
                if(method==ThrowMethod)throw new InvalidOperationException("account-123-password-SECRET");
                switch(method) {
                    case "SetQueryName":QueryName=Convert.ToString(args[0],CultureInfo.InvariantCulture);return OverrideSetterReturns?QuerySetterReturn:(object)SetQueryAccepted;
                    case "GetQueryName":return QueryReadbackOverride??QueryName;
                    case "SetSingleData":if(DuringSetField!=null)DuringSetField();return OverrideSetterReturns?FieldSetterReturn:(object)SetFieldAccepted;
                    case "RequestData":RequestCount++;if(DuringRequest!=null)DuringRequest();if(ErrorAfterRequest)Diagnostic.ErrorState=2;return OverrideRequestReturn?RequestReturn:Id;
                    case "GetMultiRowCount":return Rows;
                    case "GetSingleData":return Field("s:"+args[0]);
                    case "GetMultiData":return Field("m:"+args[0]+":"+args[1]);
                    default:throw new InvalidOperationException("Unexpected fake call "+method);
                }
            }
            object Field(string key){object result;return Fields.TryGetValue(key,out result)?result:"";}
            // The official OCX writes response status N/E before firing ReceiveData.
            public void Data(int id){Diagnostic.ErrorState=ResponseErrorState;if(ReceiveData!=null)ReceiveData(id);}
            public void DeliverWithoutSettingStatus(int id){if(ReceiveData!=null)ReceiveData(id);}
            public void Fail(string reason){if(Error!=null)Error(reason);}
            public void Sys(int id){if(ReceiveSysMsg!=null)ReceiveSysMsg(id);}
            public void Dispose(){}
            public IndiDiagnosticSnapshot ReadDiagnostics(){DiagnosticReads++;if(DiagnosticThrows)throw new InvalidOperationException("account-123-password-SECRET");return Diagnostic;}
            public void Quote(string code="005930",string price="70,000",string clock="091530") {
                Fields["s:1"]=code;Fields["s:3"]=price;Fields["s:2"]=clock;
            }
            public void Stock() {
                Rows=1;Fields["m:0:1"]="005930";Fields["m:0:2"]="0";Fields["m:0:3"]="Samsung";
                Fields["m:0:14"]="ST";Fields["m:0:6"]="0";Fields["m:0:7"]="0";Fields["m:0:8"]="0";
            }
            public void Bar() {
                Rows=1;Fields["m:0:0"]="20261111";Fields["m:0:2"]="69000";Fields["m:0:3"]="71000";
                Fields["m:0:4"]="68000";Fields["m:0:5"]="70000";Fields["m:0:9"]="123456";
            }
            public void Holding() {
                Rows=1;Fields["m:0:0"]="A005930";Fields["m:0:1"]="Samsung";
                for(int i=2;i<=7;i++)Fields["m:0:"+i]="0";
                Fields["m:0:2"]="10";Fields["m:0:5"]="70000";Fields["m:0:6"]="65000";
            }
            public void TradeHolding(string basis) {
                Rows=1;Fields["m:0:0"]="A005930";Fields["m:0:1"]="TEST";Fields["m:0:2"]=basis;
                Fields["m:0:3"]="보통";Fields["m:0:4"]="2";Fields["m:0:10"]="2";Fields["m:0:11"]="0";
                Fields["m:0:12"]="70000";Fields["m:0:13"]="-71000";Fields["m:0:22"]="0";
            }
        }
        static int checks;
        static void Check(bool condition,string name){checks++;if(!condition)throw new Exception("Indi test failed: "+name);}
        static void Reject(Action action,string name){bool rejected=false;try{action();}catch(InvalidOperationException){rejected=true;}Check(rejected,name);}
        static bool BeginQuote(IndiRequestCoordinator requests){return requests.Begin("QUOTE","SC",new Dictionary<int,string>{{0,"005930"}},"005930");}
        static bool BeginSnapshot(IndiRequestCoordinator requests){return requests.Begin("SNAPSHOT","TR_1110_11",new Dictionary<int,string>{{0,"005930"}},"005930");}
        static void OrdinaryHoldingsProvenance() {
            const string number="12345678901",secret="5678";
            var fake=new FakeIndi();fake.TradeHolding("5");fake.Fields["m:0:3"]="";fake.Fields["m:0:22"]="";
            fake.Diagnostic.Message="계좌"+number+" 암호"+secret;
            var requests=new IndiRequestCoordinator(fake);IndiReply reply=null;int fatal=0;string trace="";
            requests.Done+=value=>reply=value;requests.Fatal+=value=>fatal++;requests.Trace+=value=>trace+=value;
            Check(requests.Begin("ORDINARYHOLDINGS","SABA609Q1",AccountQueries.OrdinaryHoldingsInputs(number,secret),""),"ordinary-only query accepts its documented full SI3=5 profile");
            fake.Data(99);Check(reply==null&&requests.Busy,"ordinary-only evidence waits for its matching accepted request ID");
            fake.Data(7);
            Check(reply!=null&&reply.Kind=="ORDINARYHOLDINGS"&&reply.Holdings.Count==1&&reply.TradeHoldings.RequestOrdinaryOnlyVerified,"normal correlated SI3=5 response proves filtered ordinary membership without guessing blank metadata");
            Check(!reply.TradeHoldings.BasisVerified&&!reply.TradeHoldings.RequestBasisVerified,"ordinary-only membership never substitutes for SI3=1 quantity-basis authority");
            Check(reply.TradeHoldings.RequestStartedUtc.Kind==DateTimeKind.Utc&&reply.TradeHoldings.ReceivedUtc.Kind==DateTimeKind.Utc
                &&reply.TradeHoldings.RequestStartedUtc<=reply.TradeHoldings.ReceivedUtc,"ordinary-only evidence retains actual request and callback UTC timestamps");
            Check(!trace.Contains(number)&&!trace.Contains(secret),"ordinary-only request diagnostics redact credentials at every stage");requests.Dispose();

            fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
            fake.DuringRequest=()=>fake.Data(7);
            Check(requests.Begin("ORDINARYHOLDINGS","SABA609Q1",AccountQueries.OrdinaryHoldingsInputs(number,secret),"")
                &&reply!=null&&reply.TradeHoldings.RequestOrdinaryOnlyVerified&&reply.Holdings.Count==0,"matching reentrant ordinary-only response binds proof before the native request returns");requests.Dispose();
            for(int field=0;field<7;field++) {
                fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
                var input=AccountQueries.OrdinaryHoldingsInputs(number,secret);
                input[field]=field==0?"bad":field==1?"02":field==2?"":field==3?"1":field==4?"2":field==5?"0":"1";
                requests.Begin("ORDINARYHOLDINGS","SABA609Q1",input,"");fake.Data(7);
                Check(reply!=null&&!reply.TradeHoldings.RequestOrdinaryOnlyVerified,"changed ordinary-only request field cannot mint SI3=5 provenance: "+field);requests.Dispose();
            }
            for(int partial=0;partial<2;partial++) {
                fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
                var input=AccountQueries.OrdinaryHoldingsInputs(number,secret);if(partial==0)input.Remove(6);else input[7]="unsupported";
                requests.Begin("ORDINARYHOLDINGS","SABA609Q1",input,"");fake.Data(7);
                Check(reply!=null&&!reply.TradeHoldings.RequestOrdinaryOnlyVerified,"missing or additional request fields cannot prove ordinary-only scope: "+partial);requests.Dispose();
            }
            fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
            requests.Begin("ORDINARYHOLDINGS","SABA200QB",AccountQueries.OrdinaryHoldingsInputs(number,secret),"");fake.Data(7);
            Check(reply!=null&&!reply.TradeHoldings.RequestOrdinaryOnlyVerified,"different query cannot inherit ordinary-only provenance");requests.Dispose();
            fake=new FakeIndi{QueryReadbackOverride="SABA200QB"};requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
            Check(!requests.Begin("ORDINARYHOLDINGS","SABA609Q1",AccountQueries.OrdinaryHoldingsInputs(number,secret),"")&&requests.Failed&&reply==null,"ordinary-only query-name readback mismatch fails before native request acceptance");requests.Dispose();
            for(int ambiguous=0;ambiguous<2;ambiguous++) {
                fake=new FakeIndi{OverrideSetterReturns=true,QuerySetterReturn=ambiguous==0?null:(object)true,FieldSetterReturn=ambiguous==0?(object)true:(object)1};
                requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
                requests.Begin("ORDINARYHOLDINGS","SABA609Q1",AccountQueries.OrdinaryHoldingsInputs(number,secret),"");fake.Data(7);
                Check(reply!=null&&!reply.TradeHoldings.RequestOrdinaryOnlyVerified,"unknown setter return can display but cannot prove ordinary-only scope: "+ambiguous);requests.Dispose();
            }
            for(int unknown=0;unknown<2;unknown++) {
                fake=new FakeIndi{DiagnosticThrows=unknown==0};if(unknown==1)fake.Diagnostic.ErrorState=null;
                requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
                requests.Begin("ORDINARYHOLDINGS","SABA609Q1",AccountQueries.OrdinaryHoldingsInputs(number,secret),"");
                if(unknown==0)fake.Data(7);else fake.DeliverWithoutSettingStatus(7);
                Check(reply!=null&&!reply.TradeHoldings.RequestOrdinaryOnlyVerified,"unknown response error state cannot prove ordinary-only membership: "+unknown);requests.Dispose();
            }
            fake=new FakeIndi{ResponseErrorState=2};requests=new IndiRequestCoordinator(fake);reply=null;fatal=0;requests.Done+=value=>reply=value;requests.Fatal+=value=>fatal++;
            requests.Begin("ORDINARYHOLDINGS","SABA609Q1",AccountQueries.OrdinaryHoldingsInputs(number,secret),"");fake.Data(7);
            Check(reply==null&&fatal==1&&requests.Failed,"ordinary-only provider error never becomes membership evidence");requests.Dispose();
            fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
            var mutable=AccountQueries.OrdinaryHoldingsInputs(number,secret);fake.DuringSetField=()=>mutable[3]="3";
            requests.Begin("ORDINARYHOLDINGS","SABA609Q1",mutable,"");fake.Data(7);
            Check(reply!=null&&reply.TradeHoldings.RequestOrdinaryOnlyVerified,"ordinary-only provenance uses the accepted private snapshot when caller fields mutate during COM setters");requests.Dispose();
        }
        public static int Run() {
            checks=0;
            OrdinaryHoldingsProvenance();
            var fake=new FakeIndi();fake.Quote();var requests=new IndiRequestCoordinator(fake);
            int received=0,fatal=0;IndiReply reply=null;
            requests.Done+=value=>{received++;reply=value;};requests.Fatal+=reason=>fatal++;
            Check(BeginQuote(requests)&&requests.Busy,"asynchronous request stays pending");
            Check(!BeginQuote(requests)&&fake.RequestCount==1,"one request at a time");
            fake.Data(99);Check(received==0&&requests.Busy,"mismatched response ignored");
            fake.Data(7);Check(received==1&&!requests.Busy&&reply.Price==70000,"matching quote copied and completed");
            Check(reply.RequestStartedUtc.Kind==DateTimeKind.Utc&&reply.ReceivedUtc.Kind==DateTimeKind.Utc&&reply.RequestStartedUtc<=reply.ReceivedUtc,"copied response preserves the request-start and callback UTC timestamps for evidence barriers");
            fake.Data(7);Check(received==1,"duplicate response ignored");
            requests.Timeout();Check(fatal==0,"idle timeout has no effect");
            requests.Dispose();fake.Data(7);Check(received==1,"disposed coordinator unhooks receive handler");

            fake=new FakeIndi();fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;fatal=0;
            requests.Done+=value=>received++;requests.Fatal+=reason=>fatal++;
            Check(BeginQuote(requests)&&requests.Busy,"read-only request can be pending at unattended session close");
            requests.CancelPending();requests.Timeout();fake.Data(7);
            Check(!requests.Busy&&!requests.Failed&&received==0&&fatal==0,"local read-only cancellation rejects a late response without inventing broker cancellation");
            fake.Id=8;Check(BeginQuote(requests)&&requests.Busy,"local cancellation permits a later deliberate read-only request");
            fake.Data(7);Check(received==0&&requests.Busy,"old cancelled ID cannot complete the later request");
            fake.Data(8);Check(received==1&&!requests.Failed,"fresh request completes after cancelled session work");requests.Dispose();

            fake=new FakeIndi();fake.Fields["s:1"]="1000000";fake.Fields["s:20"]="800000";fake.Fields["s:30"]="500000";
            fake.Diagnostic.Message="계좌12345678901 암호5678";requests=new IndiRequestCoordinator(fake);received=0;
            string accountTrace="";requests.Trace+=value=>accountTrace+=value;requests.Done+=value=>{received++;reply=value;};
            Check(requests.Begin("CASH","SABA602Q1",AccountQueries.CashInputs("12345678901","5678"),""),"cash account query uses its read-only contract");
            fake.Data(7);Check(received==1&&reply.Cash.Cash100Orderable==500000,"cash100 amount is copied from official field30");
            Check(!accountTrace.Contains("12345678901")&&!accountTrace.Contains("5678"),"cash diagnostics redact account and password in every stage");requests.Dispose();

            // Replay the 2.1 regression: ReceiveData succeeds, but the basis
            // field differs from the documented echo. Positions stay visible,
            // and only the complete correlated successful request scopes them.
            fake=new FakeIndi{OverrideRequestReturn=true,RequestReturn=(short)3799};fake.TradeHolding("");
            requests=new IndiRequestCoordinator(fake);received=0;fatal=0;
            requests.Done+=value=>{received++;reply=value;};requests.Fatal+=reason=>fatal++;
            Check(requests.Begin("TRADEHOLDINGS","SABA609Q1",AccountQueries.HoldingsInputs("12345678901","5678"),""),"609 trade-basis request accepts its actual callback independently of row certainty");
            fake.Data(3798);Check(received==0&&requests.Busy,"609 unknown-basis regression preserves request ID matching");
            fake.Data(3799);
            Check(received==1&&fatal==0&&!requests.Failed&&!requests.Busy&&reply.Holdings.Count==1,"a successful unknown-basis response no longer halts or discards readable holdings");
            Check(reply.Holdings[0].Quantity==2&&reply.Holdings[0].Price==71000&&reply.TradeHoldings.BasisVerified
                &&reply.TradeHoldings.RequestBasisVerified&&reply.TradeHoldings.BasisValidated["005930"],"a successful complete trade-basis request binds readable positions without requiring an optional echo code");
            Check(reply.TradeHoldings.RequestStartedUtc.Kind==DateTimeKind.Utc&&reply.TradeHoldings.RequestStartedUtc<=reply.TradeHoldings.ReceivedUtc,"unknown-basis display retains real coordinator evidence timestamps");
            fake.OverrideRequestReturn=false;fake.Id=3800;fake.Fields["s:1"]="1000000";fake.Fields["s:20"]="800000";fake.Fields["s:30"]="500000";
            Check(requests.Begin("CASH","SABA602Q1",AccountQueries.CashInputs("12345678901","5678"),""),"cash can be requested after a readable basis-unconfirmed balance");
            fake.Data(3800);Check(received==2&&reply.Cash.Cash100Orderable==500000&&fatal==0&&!requests.Failed,"a basis warning does not poison subsequent account queries");requests.Dispose();

            fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);received=0;reply=null;
            requests.Done+=value=>{received++;reply=value;};
            fake.DuringRequest=()=>fake.Data(7);
            Check(requests.Begin("TRADEHOLDINGS","SABA609Q1",AccountQueries.HoldingsInputs("12345678901","5678"),"")
                &&received==1&&reply.TradeHoldings.BasisVerified&&reply.Holdings.Count==0,"a matched reentrant empty response is scoped by the complete accepted trade-basis request");requests.Dispose();
            foreach(var field in new[]{3,6}) {
                fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);reply=null;
                requests.Done+=value=>reply=value;
                var partial=AccountQueries.HoldingsInputs("12345678901","5678");partial[field]=field==3?"2":"1";
                requests.Begin("TRADEHOLDINGS","SABA609Q1",partial,"");fake.Data(7);
                Check(reply!=null&&!reply.TradeHoldings.BasisVerified,"empty response cannot certify a different basis or suppressed multi output: input "+field);requests.Dispose();
            }
            fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);reply=null;
            requests.Done+=value=>reply=value;
            requests.Begin("TRADEHOLDINGS","SABA200QB",AccountQueries.HoldingsInputs("12345678901","5678"),"");fake.Data(7);
            Check(reply!=null&&!reply.TradeHoldings.BasisVerified,"a different query cannot inherit empty 609 trade-basis evidence");requests.Dispose();
            fake=new FakeIndi{ResponseErrorState=2};requests=new IndiRequestCoordinator(fake);received=0;fatal=0;
            requests.Done+=value=>received++;requests.Fatal+=reason=>fatal++;
            requests.Begin("TRADEHOLDINGS","SABA609Q1",AccountQueries.HoldingsInputs("12345678901","5678"),"");fake.Data(7);
            Check(received==0&&fatal==1&&requests.Failed,"a provider error is never reclassified as a verified empty holdings response");requests.Dispose();
            for(int unavailable=0;unavailable<2;unavailable++) {
                fake=new FakeIndi{DiagnosticThrows=unavailable==0};requests=new IndiRequestCoordinator(fake);reply=null;
                requests.Done+=value=>reply=value;
                if(unavailable==1)fake.Diagnostic.ErrorState=null;
                requests.Begin("TRADEHOLDINGS","SABA609Q1",AccountQueries.HoldingsInputs("12345678901","5678"),"");
                // Preserve an unavailable callback state rather than FakeIndi.Data's
                // normal simulation of the provider writing a known response state.
                if(unavailable==0)fake.Data(7);else fake.DeliverWithoutSettingStatus(7);
                Check(reply!=null&&reply.Holdings.Count==0&&!reply.TradeHoldings.BasisVerified,"unavailable callback error status cannot certify a request-bound empty balance: "+unavailable);requests.Dispose();
            }
            for(int malformed=0;malformed<4;malformed++) {
                fake=new FakeIndi();fake.TradeHolding("1");requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
                var fields=AccountQueries.HoldingsInputs("12345678901","5678");
                if(malformed==0)fields.Remove(0);else if(malformed==1)fields[0]="bad";else if(malformed==2)fields[2]="";else fields[7]="undocumented";
                requests.Begin("TRADEHOLDINGS","SABA609Q1",fields,"");fake.Data(7);
                Check(reply!=null&&reply.Holdings.Count==1&&!reply.TradeHoldings.RequestBasisVerified&&!reply.TradeHoldings.BasisVerified,"even code1 cannot certify an incomplete or unsupported request profile: "+malformed);requests.Dispose();
            }
            for(int ambiguous=0;ambiguous<2;ambiguous++) {
                fake=new FakeIndi{OverrideSetterReturns=true,QuerySetterReturn=ambiguous==0?null:(object)true,FieldSetterReturn=ambiguous==0?(object)true:(object)1};
                fake.TradeHolding("1");requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
                requests.Begin("TRADEHOLDINGS","SABA609Q1",AccountQueries.HoldingsInputs("12345678901","5678"),"");fake.Data(7);
                Check(reply!=null&&!reply.TradeHoldings.BasisVerified,"ambiguous setter return cannot mint account evidence despite readable data: "+ambiguous);requests.Dispose();
            }
            fake=new FakeIndi();fake.TradeHolding("");requests=new IndiRequestCoordinator(fake);reply=null;requests.Done+=value=>reply=value;
            var originalInputs=AccountQueries.HoldingsInputs("12345678901","5678");fake.DuringSetField=()=>originalInputs[3]="2";
            requests.Begin("TRADEHOLDINGS","SABA609Q1",originalInputs,"");fake.Data(7);
            Check(reply!=null&&reply.TradeHoldings.RequestBasisVerified&&reply.TradeHoldings.BasisVerified,"provenance uses the private accepted field snapshot rather than caller changes during COM setters");requests.Dispose();

            fake=new FakeIndi();fake.Diagnostic.Message="계좌12345678901 암호5678";requests=new IndiRequestCoordinator(fake);received=0;accountTrace="";
            requests.Trace+=value=>accountTrace+=value;requests.Done+=value=>{received++;reply=value;};
            Check(requests.Begin("ORDERS","SABA231Q1",AccountQueries.OrdersInputs("12345678901","5678","20261112"),"20261112"),"order snapshot keeps account input at1 rather than treating date0 as credentials");
            fake.Data(7);
            Check(received==1&&reply.BrokerOrders.TradingDate=="20261112"&&!reply.BrokerOrders.IsComplete&&!reply.BrokerOrders.HasExecutionIds,"empty aggregate order response is displayed without claiming complete reconciliation");
            fake.Quote();BeginQuote(requests);
            Check(!accountTrace.Contains("12345678901")&&!accountTrace.Contains("5678"),"order-query credentials stay redacted in a subsequent public query");requests.Dispose();

            fake=new FakeIndi{Rows=1};fake.Fields["m:0:0"]="100";fake.Fields["m:0:1"]="10";fake.Fields["m:0:2"]="12";
            fake.Fields["m:0:4"]="700000";fake.Fields["m:0:5"]="840000";requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>{received++;reply=value;};
            Check(requests.Begin("CAPACITY","SABA415Q1",AccountQueries.CapacityInputs("12345678901","5678","005930",70000),"005930",70000m),"capacity request retains requested whole-won limit price as context");
            fake.Data(7);
            Check(received==1&&reply.Capacity.Code=="005930"&&reply.Capacity.LimitPrice==70000&&reply.Capacity.Ratio==100&&reply.Capacity.Quantity==10,"capacity callback binds cash100 row to the original requested symbol and price");requests.Dispose();

            // Replay the user's 1.4.0 log: Int16 request 3 is accepted while the
            // pre-response error state is 2 with no code/message; ReceiveData 3
            // follows on the next message-pump turn. It must reach the parser.
            fake=new FakeIndi{OverrideRequestReturn=true,RequestReturn=(short)3};fake.Stock();
            fake.Diagnostic.ErrorState=2;fake.Diagnostic.ErrorCode="";fake.Diagnostic.Message="";
            requests=new IndiRequestCoordinator(fake);received=0;fatal=0;
            requests.Done+=value=>{received++;reply=value;};requests.Fatal+=reason=>fatal++;
            Check(requests.Begin("STOCKS","stock_mst",null,"")&&requests.Busy&&!requests.Failed&&fatal==0,"accepted Int16 rid3 with empty state2 diagnostics waits for ReceiveData");
            fake.Data(99);Check(received==0&&requests.Busy,"stale state2 does not weaken callback ID matching");
            fake.Data(3);
            Check(received==1&&reply.Stocks.Count==1&&reply.Stocks[0].Code=="005930"&&!requests.Busy&&!requests.Failed&&fatal==0,"actual matching rid3 is parsed despite pre-response state2");
            fake.Data(3);Check(received==1,"rid3 completion remains exactly once");
            fake.Id=4;fake.OverrideRequestReturn=false;fake.Quote();
            Check(BeginQuote(requests)&&requests.Busy,"stale diagnostic status does not poison the next accepted quote");
            fake.Data(4);Check(received==2&&reply.Price==70000&&!requests.Failed,"next quote completes after stock master with stale status");requests.Dispose();

            fake=new FakeIndi{ErrorAfterRequest=true};fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>{received++;reply=value;};
            fake.DuringRequest=()=>{fake.Data(7);fake.Fields["s:3"]="broken after callback";};
            Check(BeginQuote(requests)&&received==1&&reply.Price==70000&&!requests.Failed,"stale diagnostic after submission cannot discard a reentrant matching response");requests.Dispose();

            fake=new FakeIndi{ErrorAfterRequest=true};fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>received++;
            fake.DuringRequest=()=>{fake.ResponseErrorState=2;fake.Data(99);fake.ResponseErrorState=0;fake.Data(7);};
            Check(BeginQuote(requests)&&received==1&&!requests.Failed,"early mismatched provider error cannot poison a matching successful snapshot");requests.Dispose();

            fake=new FakeIndi{ErrorAfterRequest=true};fake.Quote(price:"invalid");requests=new IndiRequestCoordinator(fake);received=0;fatal=0;
            requests.Done+=value=>received++;requests.Fatal+=reason=>fatal++;
            Check(BeginQuote(requests)&&requests.Busy,"accepted request awaits response validation even with diagnostic state2");
            fake.Data(7);Check(requests.Failed&&received==0&&fatal==1,"malformed matching response still halts when submission diagnostics are stale");requests.Dispose();

            fake=new FakeIndi{ErrorAfterRequest=true};fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;fatal=0;
            requests.Done+=value=>received++;requests.Fatal+=reason=>fatal++;
            Check(BeginQuote(requests)&&requests.Busy,"accepted state2 request without callback remains pending");
            requests.Timeout();fake.Data(7);
            Check(requests.Failed&&!requests.Busy&&received==0&&fatal==1,"missing callback still times out and rejects a late response with state2");requests.Dispose();

            fake=new FakeIndi();fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>{received++;reply=value;};
            fake.DuringRequest=()=>{fake.Data(7);fake.Fields["s:3"]="broken after callback";};
            Check(BeginQuote(requests)&&received==1&&!requests.Busy&&reply.Price==70000,"reentrant receive snapshots buffer before RequestData returns");
            requests.Dispose();

            fake=new FakeIndi();fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>received++;
            fake.DuringRequest=()=>{fake.Fields["s:3"]="invalid unrelated response";fake.Data(99);fake.Quote();fake.Data(7);};
            Check(BeginQuote(requests)&&received==1&&!requests.Failed,"early mismatched parse failure cannot poison matching response");
            requests.Dispose();

            fake=new FakeIndi();fake.Quote();requests=new IndiRequestCoordinator(fake);fatal=0;
            requests.Fatal+=reason=>fatal++;BeginQuote(requests);requests.Timeout();fake.Data(7);
            Check(requests.Failed&&!requests.Busy&&fatal==1,"timeout halts and ignores late callback");
            Reject(()=>BeginQuote(requests),"cannot request after timeout");requests.Dispose();

            fake=new FakeIndi{Id=-1};requests=new IndiRequestCoordinator(fake);
            Check(!BeginQuote(requests)&&requests.Failed,"negative request id halts");requests.Dispose();
            fake=new FakeIndi{Id=0};fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>received++;
            Check(!BeginQuote(requests)&&!requests.Busy&&requests.Failed,"official OCX zero return is a failed submission, not a pending request");
            fake.Data(0);Check(received==0&&!requests.Busy,"zero request rejection ignores a later unsolicited callback");requests.Dispose();
            fake=new FakeIndi{OverrideRequestReturn=true,RequestReturn=(short)0};fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>received++;fake.DuringRequest=()=>fake.Data(0);
            Check(!BeginQuote(requests)&&received==0,"native Int16 zero return cannot complete a reentrant request");requests.Dispose();
            foreach(object invalid in new object[]{null,DBNull.Value,false,true,7.0,7.0m,"7"}) {
                fake=new FakeIndi{OverrideRequestReturn=true,RequestReturn=invalid};requests=new IndiRequestCoordinator(fake);
                Check(!BeginQuote(requests)&&requests.Failed&&!requests.Busy,"non-integer and null returns never become a request id");requests.Dispose();
            }
            fake=new FakeIndi{Id=0,ErrorAfterRequest=true};requests=new IndiRequestCoordinator(fake);fatal=0;
            requests.Fatal+=reason=>fatal++;
            Check(!BeginQuote(requests)&&requests.Failed&&!requests.Busy&&fatal==1,"explicit provider error after rid0 stops immediately without a timeout");
            requests.Timeout();Check(fatal==1,"failed submit cannot produce a second timeout failure");requests.Dispose();
            fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);BeginQuote(requests);
            int diagnosticReads=fake.DiagnosticReads;requests.Timeout();
            Check(fake.DiagnosticReads>diagnosticReads,"timeout captures actual Indi diagnostic getters");requests.Dispose();
            fake=new FakeIndi{DiagnosticThrows=true};fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;
            requests.Done+=value=>received++;
            Check(BeginQuote(requests),"diagnostic getter failure does not replace a valid request");fake.Data(7);
            Check(received==1,"diagnostics failure does not discard a matching receive");requests.Dispose();
            fake=new FakeIndi{ErrorAfterRequest=true,ResponseErrorState=2};fake.Diagnostic.ErrorCode="1234567890";
            fake.Diagnostic.Message="계좌1234567890 암호4321 요청 오류";requests=new IndiRequestCoordinator(fake);
            var privateLog="";requests.Trace+=value=>privateLog+=value;requests.Fatal+=value=>privateLog+=value;
            received=0;fatal=0;requests.Done+=value=>received++;requests.Fatal+=value=>fatal++;
            Check(requests.Begin("HOLDINGS","SABA200QB",new Dictionary<int,string>{{0,"1234567890"},{1,"01"},{2,"4321"}},"")&&requests.Busy&&!requests.Failed,"accepted holding request awaits its own response rather than stale diagnostics");
            fake.Data(7);
            Check(requests.Failed&&!requests.Busy&&fatal==1&&received==0,"matching holding error callback cannot be mistaken for an empty balance");
            Check(!privateLog.Contains("1234567890")&&!privateLog.Contains("4321")&&privateLog.Contains("오류"),"actual provider diagnostics retain reason while redacting credentials");requests.Dispose();

            fake=new FakeIndi{ErrorAfterRequest=true,ResponseErrorState=2};fake.Quote();requests=new IndiRequestCoordinator(fake);received=0;fatal=0;
            requests.Done+=value=>received++;requests.Fatal+=reason=>fatal++;
            fake.DuringRequest=()=>fake.Data(7);
            Check(!BeginQuote(requests)&&requests.Failed&&received==0&&fatal==1,"matching reentrant provider error is rejected after correlating the returned request id");requests.Dispose();
            fake=new FakeIndi();requests=new IndiRequestCoordinator(fake);privateLog="";requests.Trace+=value=>privateLog+=value;
            Check(requests.Begin("HOLDINGS","SABA200QB",new Dictionary<int,string>{{0,"1234567890"},{1,"01"},{2,"4321"}},""),"holding request can complete before a later unrelated query");
            fake.Data(7);fake.Diagnostic.Message="이전 요청 암호4321";fake.Quote();
            Check(BeginQuote(requests)&&!privateLog.Contains("4321"),"retained provider messages cannot leak a prior request password");requests.Dispose();
            fake=new FakeIndi{SetQueryAccepted=false};requests=new IndiRequestCoordinator(fake);
            Check(!BeginQuote(requests)&&requests.Failed&&fake.RequestCount==0,"SetQueryName failure prevents request");requests.Dispose();
            fake=new FakeIndi{QueryReadbackOverride="wrong_query"};requests=new IndiRequestCoordinator(fake);
            Check(!BeginQuote(requests)&&requests.Failed&&fake.RequestCount==0,"query readback mismatch prevents a malformed request");requests.Dispose();
            fake=new FakeIndi{QueryReadbackOverride="sc  "};fake.Quote();requests=new IndiRequestCoordinator(fake);
            Check(BeginQuote(requests),"canonical query readback permits case normalization and padding");requests.Dispose();
            fake=new FakeIndi{SetFieldAccepted=false};requests=new IndiRequestCoordinator(fake);
            Check(!BeginQuote(requests)&&requests.Failed&&fake.RequestCount==0,"SetSingleData failure prevents request");requests.Dispose();

            fake=new FakeIndi{ThrowMethod="SetSingleData"};requests=new IndiRequestCoordinator(fake);
            var diagnostics="";requests.Trace+=value=>diagnostics+=value;requests.Fatal+=value=>diagnostics+=value;
            Check(!BeginQuote(requests)&&requests.Failed,"provider exception during field setter halts");
            Check(diagnostics.Contains("SetSingleData")&&!diagnostics.Contains("SECRET")&&!diagnostics.Contains("account-123"),"credential values inside provider exceptions never reach diagnostics");
            requests.Dispose();
            fake=new FakeIndi{ThrowMethod="GetSingleData"};requests=new IndiRequestCoordinator(fake);diagnostics="";
            requests.Trace+=value=>diagnostics+=value;requests.Fatal+=value=>diagnostics+=value;
            BeginQuote(requests);fake.Data(7);
            Check(requests.Failed&&!diagnostics.Contains("SECRET"),"provider parse exception text omitted");requests.Dispose();

            fake=new FakeIndi();fake.Quote(price:"N/A");requests=new IndiRequestCoordinator(fake);
            BeginQuote(requests);fake.Data(7);Check(requests.Failed,"malformed quote faults closed");requests.Dispose();
            fake=new FakeIndi();fake.Quote(code:"000660");
            Reject(()=>IndiResponseParser.Read(fake,"QUOTE","005930"),"response symbol must match requested symbol");
            fake.Quote(clock:"256100");reply=IndiResponseParser.Read(fake,"QUOTE","005930");
            Check(reply.Price==70000&&!reply.HasTradeClock&&reply.Clock==""&&reply.Message.Contains("자동매매 사용 제외"),"invalid trade clock permits display only and is never fabricated");
            Check(!MarketTime.CanUseQuote(reply,new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc)),"invalid trade clock blocks paper buy and sell use");
            fake.Quote(clock:"");reply=IndiResponseParser.Read(fake,"QUOTE","005930");
            Check(reply.Price==70000&&!reply.HasTradeClock&&!MarketTime.CanUseQuote(reply,new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc)),"missing trade clock cannot halt a valid display price or feed paper trading");
            fake.Quote();reply=IndiResponseParser.Read(fake,"QUOTE","005930");
            Check(reply.HasTradeClock&&MarketTime.CanUseQuote(reply,new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc)),"validated SC time is usable only during a fresh regular session");
            Check(!MarketTime.CanUseQuote(reply,new DateTime(2026,11,12,0,17,0,DateTimeKind.Utc)),"stale valid SC clock remains blocked");
            Check(!MarketTime.CanUseQuote(new IndiReply{Code="",Price=70000,Clock="091530",HasTradeClock=true},new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc)),"empty symbol cannot pass the common trading quote gate");
            fake.Quote(price:"-70,000");Check(IndiResponseParser.Read(fake,"QUOTE","005930").Price==70000,"signed broker quote price normalized");

            // The official snapshot has 31 outputs, price at24, and no symbol/time
            // echoes. Distinct old SC fields catch accidental reuse of its layout.
            fake=new FakeIndi();fake.Fields["s:1"]="not a symbol echo";fake.Fields["s:2"]="not a clock";
            fake.Fields["s:3"]="not a price";fake.Fields["s:24"]="70,000";
            requests=new IndiRequestCoordinator(fake);received=0;fatal=0;diagnostics="";
            requests.Done+=value=>{received++;reply=value;};requests.Fatal+=reason=>fatal++;requests.Trace+=value=>diagnostics+=value;
            Check(BeginSnapshot(requests)&&fake.QueryName=="TR_1110_11"&&requests.Busy,"manual current-price request uses the dedicated snapshot TR");
            fake.Data(99);Check(received==0&&requests.Busy,"snapshot response correlation rejects unrelated IDs");
            fake.Data(7);
            Check(received==1&&fatal==0&&!requests.Failed&&reply.Kind=="QUOTE"&&reply.Code=="005930"&&reply.Price==70000,"snapshot price24 is delivered to the quote UI with requested symbol");
            Check(!reply.HasTradeClock&&reply.Clock==""&&!MarketTime.CanUseQuote(reply,new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc)),"snapshot receipt time never substitutes for trade time");
            reply.Clock="091530";Check(!MarketTime.CanUseQuote(reply,new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc)),"display-only snapshot flag blocks trading even if a clock is supplied later");
            Check(diagnostics.Contains("price[24]")&&!diagnostics.Contains("not a symbol echo"),"snapshot diagnostics only inspect documented public price output");requests.Dispose();

            fake=new FakeIndi();fake.Fields["s:24"]="";requests=new IndiRequestCoordinator(fake);diagnostics="";
            requests.Trace+=value=>diagnostics+=value;BeginSnapshot(requests);fake.Data(7);
            Check(requests.Failed&&diagnostics.Contains("TR_1110_11 price single[24]"),"empty snapshot price fails with the exact field context");requests.Dispose();
            fake.Fields["s:24"]="0";Reject(()=>IndiResponseParser.Read(fake,"SNAPSHOT","005930"),"zero snapshot price is not a usable display quote");

            fake=new FakeIndi();fake.Quote(code:"");requests=new IndiRequestCoordinator(fake);diagnostics="";
            requests.Trace+=value=>diagnostics+=value;BeginQuote(requests);fake.Data(7);
            Check(requests.Failed&&diagnostics.Contains("SC symbol single[1]")&&diagnostics.Contains("symbol[1]=\"\""),"SC symbol failure identifies the exact missing field");requests.Dispose();
            fake=new FakeIndi();fake.Quote(price:"N/A");requests=new IndiRequestCoordinator(fake);diagnostics="";
            requests.Trace+=value=>diagnostics+=value;BeginQuote(requests);fake.Data(7);
            Check(requests.Failed&&diagnostics.Contains("SC price single[3]")&&diagnostics.Contains("price[3]=\"N/A\""),"SC price failure preserves only public quote fields and parser-owned context");requests.Dispose();
            Reject(()=>IndiResponseParser.RequiredNumber("", "cash"),"blank numeric field not zero");
            Reject(()=>IndiResponseParser.RequiredNumber("1e6", "cash"),"scientific notation rejected");
            Reject(()=>IndiResponseParser.RequiredNumber("1,,000", "cash"),"bad comma grouping rejected");
            Reject(()=>IndiResponseParser.RequiredNumber("-1", "cash"),"negative cash rejected");
            Check(IndiResponseParser.RequiredNumber("1,234.50","cash")==1234.5m,"invariant number parsed");
            var priorCulture=CultureInfo.CurrentCulture;
            try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");Check(IndiResponseParser.RequiredNumber("1234.5","cash")==1234.5m,"decimal parser independent of UI culture");}
            finally{CultureInfo.CurrentCulture=priorCulture;}

            fake=new FakeIndi();fake.Stock();reply=IndiResponseParser.Read(fake,"STOCKS","");
            Check(reply.Count==1&&reply.Stocks.Count==1&&reply.Stocks[0].Code=="005930","stock master replay");
            fake.Fields["m:0:7"]="1";Reject(()=>IndiResponseParser.Read(fake,"STOCKS",""),"master with no eligible common stock fails verification");
            fake=new FakeIndi();fake.Bar();reply=IndiResponseParser.Read(fake,"BARS","005930");
            Check(reply.Bars.Count==1&&reply.Bars[0].Close==70000&&reply.Bars[0].Volume==123456,"daily bar replay");
            fake.Fields["m:0:0"]="20260230";Reject(()=>IndiResponseParser.Read(fake,"BARS","005930"),"impossible bar date rejected");
            fake.Bar();fake.Fields["m:0:3"]="60000";Reject(()=>IndiResponseParser.Read(fake,"BARS","005930"),"inconsistent OHLC rejected");
            fake=new FakeIndi();fake.Holding();reply=IndiResponseParser.Read(fake,"HOLDINGS","");
            Check(reply.Holdings.Count==1&&reply.Holdings[0].Quantity==10&&reply.Holdings[0].Code=="005930","holding replay");
            fake.Fields["m:0:2"]="bad";Reject(()=>IndiResponseParser.Read(fake,"HOLDINGS",""),"malformed holding quantity cannot disappear as zero");
            fake.Holding();fake.Fields["m:0:2"]="1.5";Reject(()=>IndiResponseParser.Read(fake,"HOLDINGS",""),"fractional domestic shares rejected");
            fake.Rows=2001;Reject(()=>IndiResponseParser.Read(fake,"HOLDINGS",""),"row limit fails rather than truncating holdings");
            fake=new FakeIndi();Check(IndiResponseParser.Read(fake,"HOLDINGS","").Holdings.Count==0,"empty balance supported");
            requests=new IndiRequestCoordinator(fake);BeginQuote(requests);fake.Fail("connection lost");
            Check(requests.Failed&&!requests.Busy,"transport error halts pending request");requests.Dispose();
            return checks;
        }
    }
}
