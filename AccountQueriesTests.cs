using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class AccountQueriesTests {
        sealed class Response : IIndiTransport {
            public event Action<int> ReceiveData {add{} remove{}}
            public event Action<int> ReceiveSysMsg {add{} remove{}}
            public event Action<string> Error {add{} remove{}}
            public object Count=0;
            public bool ThrowGetter;
            public string ThrowMethod,ThrowField;
            public readonly Dictionary<string,object> Fields=new Dictionary<string,object>();
            public object Invoke(string name,params object[] args) {
                if(ThrowGetter||name==ThrowMethod)throw new InvalidOperationException("account=12345678901 password=4321 SECRET");
                if(name=="GetMultiRowCount")return Count;
                string key;
                if(name=="GetSingleData")key="s:"+args[0];
                else if(name=="GetMultiData")key="m:"+args[0]+":"+args[1];
                else throw new InvalidOperationException("Unexpected test query method.");
                if(key==ThrowField)throw new InvalidOperationException("account=12345678901 password=4321 SECRET");
                object value;return Fields.TryGetValue(key,out value)?value:"";
            }
            public void Dispose(){}
            public void SetRow(int row,int field,object value){Fields["m:"+row+":"+field]=value;}
            public void Order(int row,string id="00123") {
                Count=Math.Max(Convert.ToInt32(Count,CultureInfo.InvariantCulture),row+1);
                foreach(var i in new[]{15,16,24,25,26,28,35})SetRow(row,i,"0");
                SetRow(row,0,id);SetRow(row,1,"0");SetRow(row,4,"2");SetRow(row,13,"A005930");
                SetRow(row,14,"Samsung");SetRow(row,15,"10");SetRow(row,16,"70000");SetRow(row,17,"01");
                SetRow(row,18,"091500");SetRow(row,21,"0");SetRow(row,26,"10");SetRow(row,29,"00");SetRow(row,31,"접수");
            }
            public void Capacity(int row,string ratio,string qty,string amount) {
                Count=Math.Max(Convert.ToInt32(Count,CultureInfo.InvariantCulture),row+1);
                SetRow(row,0,ratio);SetRow(row,1,qty);SetRow(row,2,qty);SetRow(row,4,amount);SetRow(row,5,amount);
            }
            public void Holding(int row,string code="A005930",string kind="보통") {
                Count=Math.Max(Convert.ToInt32(Count,CultureInfo.InvariantCulture),row+1);
                SetRow(row,0,code);SetRow(row,1,"Samsung");SetRow(row,2,"1");SetRow(row,3,kind);
                foreach(var field in new[]{5,6,7,8,9,11,22})SetRow(row,field,"0");
                SetRow(row,4,"7");SetRow(row,10,"7");SetRow(row,12,"70000");SetRow(row,13,"71000");
                Fields["s:0"]="20261112";
            }
        }
        static int checks;
        static void Check(bool ok,string name){checks++;if(!ok)throw new Exception("AccountQueriesTests: "+name);}
        static void Reject(Action action,string name) {
            bool failed=false;try{action();}catch(ArgumentException){failed=true;}catch(InvalidOperationException){failed=true;}
            Check(failed,name);
        }
        static Exception Capture(Action action) {try{action();}catch(Exception ex){return ex;}return null;}
        public static int Run() {
            checks=0;
            const string account="12345678901",password="4321";
            var utc=new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc);
            var inputs=AccountQueries.CashInputs(account,password);
            Check(inputs.Count==4&&inputs[0]==account&&inputs[1]=="01"&&inputs[2]==password&&inputs[3]=="1","cash query sends cash account fields only");
            inputs=AccountQueries.HoldingsInputs(account,password);
            Check(inputs.Count==7&&inputs[0]==account&&inputs[1]=="01"&&inputs[2]==password&&inputs[3]=="1"&&inputs[4]=="1"&&inputs[5]=="1"&&inputs[6]=="0","holdings query explicitly requests average-cost trade-basis cash product with complete output excluding ELW");
            inputs=AccountQueries.CapacityInputs(account,password,"005930",70000m);
            Check(inputs.Count==9&&inputs[3]=="A005930"&&inputs[4]=="1"&&inputs[5]=="2"&&inputs[6]=="0"&&inputs[7]=="00"&&inputs[8]=="70000","capacity query uses regular cash limit order at requested price");
            var prior=CultureInfo.CurrentCulture;
            try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");Check(AccountQueries.CapacityInputs(account,password,"A005930",70000m)[8]=="70000","price serialization remains invariant");}
            finally {CultureInfo.CurrentCulture=prior;}
            inputs=AccountQueries.OrdersInputs(account,password,"20261112");
            Check(inputs.Count==9&&inputs[0]=="20261112"&&inputs[1]==account&&inputs[4]=="0"&&inputs[5]=="0"&&inputs[6]=="*"&&inputs[7]=="01"&&inputs[8]=="Y","order query explicitly requests all aggregate cash-account orders");
            Reject(()=>AccountQueries.CashInputs("bad",password),"invalid account rejects before provider calls");
            Reject(()=>AccountQueries.HoldingsInputs("bad",password),"trade-basis holdings validate credentials before request construction");
            Reject(()=>AccountQueries.CashInputs(account,"secret\nvalue"),"password control characters reject without echoing values");
            Reject(()=>AccountQueries.CapacityInputs(account,password,"005930",0),"capacity limit price must be positive");
            Reject(()=>AccountQueries.CapacityInputs(account,password,"005930",1.5m),"capacity does not silently round domestic stock price");
            Reject(()=>AccountQueries.OrdersInputs(account,password,"20260230"),"order date must be a real calendar date");

            var io=new Response();io.Fields["s:1"]="1,200,000";io.Fields["s:20"]="900000";io.Fields["s:30"]="800000";
            var cash=AccountQueries.ReadCash(io,utc);
            Check(cash.DepositCash==1200000&&cash.CashOrderable==900000&&cash.Cash100Orderable==800000&&cash.ReceivedUtc==utc,"cash uses separate documented fields rather than paper capital");
            Check(!cash.OrderSemanticsVerified,"read-only cash alone does not certify live-order safety");
            io.Fields["s:30"]="-100";cash=AccountQueries.ReadCash(io,utc);
            Check(cash.Cash100Orderable==-100&&cash.NonNegativeCash100==0,"negative broker cash remains visible and cannot become buying power");
            io.Fields["s:30"]="";Reject(()=>AccountQueries.ReadCash(io,utc),"missing cash100 field is not assumed zero");
            Reject(()=>AccountQueries.ReadCash(io,DateTime.SpecifyKind(utc,DateTimeKind.Local)),"cash response requires explicit UTC observation time");
            io=new Response{ThrowGetter=true};
            var failure=Capture(()=>AccountQueries.ReadCash(io,utc));string error=failure==null?"":failure.ToString();
            Check(!error.Contains(account)&&!error.Contains(password)&&error.Contains("single[1]"),"provider exceptions cannot leak account credentials through account query errors");
            var context=AccountQueries.FailureContext(failure);
            Check(context!=null&&context.Contains("SABA602Q1 single[1]")&&!context.Contains("SECRET")&&failure.InnerException==null,"safe owned single-field failure context preserves query/index without provider text");
            Check(AccountQueries.FailureContext(new InvalidOperationException("SECRET"))==null,"untrusted provider errors do not acquire displayable context");
            io=new Response{ThrowMethod="GetMultiData"};io.Capacity(0,"100","11","800000");
            failure=Capture(()=>AccountQueries.ReadCapacity(io,"005930",70000,utc));context=AccountQueries.FailureContext(failure);
            Check(context!=null&&context.Contains("SABA415Q1 multi[0,0]")&&!failure.ToString().Contains("SECRET"),"safe owned repeat-field context identifies row and index without provider text");
            io=new Response{ThrowMethod="GetMultiRowCount"};
            failure=Capture(()=>AccountQueries.ReadOrders(io,"20261112",utc));context=AccountQueries.FailureContext(failure);
            Check(context!=null&&context.Contains("SABA231Q1 row count")&&!failure.ToString().Contains("SECRET"),"safe owned row-count getter context survives diagnosis without vendor details");

            io=new Response();io.Holding(0);io.SetRow(0,8,"3");io.SetRow(0,9,"4");
            io.Fields["s:16"]="-1,250";io.Fields["s:19"]="7000";
            var balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings.Count==1&&balance.Holdings[0].Code=="005930"&&balance.Holdings[0].Quantity==7,"today's three bought shares are included by trade quantity rather than four settled shares");
            Check(balance.Holdings[0].AvgPrice==70000&&balance.Holdings[0].Price==71000&&balance.Holdings[0].CreditQty==0,"trade holdings preserve actual broker average and current prices");
            Check(balance.SellableQuantities["005930"]==7&&balance.CashSourceValidated["005930"]&&balance.UnfilledQuantities["005930"]==0,"documented ordinary no-loan trade position supplies typed sellable quantity");
            Check(balance.RequestBasisVerified&&balance.BasisVerified&&balance.BasisValidated["005930"]&&balance.BasisDiagnostics["005930"]=="TradeBasisCode1","successful bound trade request supplies row and frame quantity basis while preserving the actual enum");
            Check(balance.Holdings[0].BuyPending==0&&balance.Holdings[0].SellPending==0,"unsettled bought shares are not mislabeled as open buy orders");
            Check(balance.RealizedProfit==-1250&&balance.UnrealizedProfit==7000,"optional broker profit fields preserve signed actual values");
            Check(balance.BrokerDate=="20261112"&&balance.BrokerDateValidated&&balance.ReceivedUtc==utc&&balance.RequestStartedUtc==default(DateTime),"broker date comes from header while request start must come from the coordinator");
            io.Fields["s:16"]="";io.Fields["s:19"]="";balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(!balance.RealizedProfit.HasValue&&!balance.UnrealizedProfit.HasValue&&balance.Holdings.Count==1,"omitted broker profit aggregates remain unknown without hiding readable holdings");
            io.Fields["s:16"]="0";balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.RealizedProfit.HasValue&&balance.RealizedProfit.Value==0,"an explicitly returned zero profit differs from missing output");
            io.Fields["s:16"]="unavailable";io.Fields["s:19"]="--";balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(!balance.RealizedProfit.HasValue&&!balance.UnrealizedProfit.HasValue&&balance.Holdings[0].Quantity==7,"malformed optional profit aggregates do not discard independently readable positions");
            io.ThrowMethod="GetSingleData";balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings.Count==1&&!balance.RealizedProfit.HasValue&&!balance.UnrealizedProfit.HasValue&&!balance.BrokerDateValidated,"missing server aggregate getters do not fabricate dates or profit");
            io.ThrowMethod=null;io.Fields["s:0"]="20260230";balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.BrokerDate==""&&!balance.BrokerDateValidated,"impossible broker date never falls back to the request or PC date");
            io.Fields["s:0"]="";balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(!balance.BrokerDateValidated&&balance.BrokerDate=="","missing broker session date stays unknown");
            io=new Response();io.Holding(0,"A005930","유통융자");io.SetRow(0,22,"100000");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings[0].Quantity==7&&!balance.CashSourceValidated["005930"]&&balance.Holdings[0].CreditQty==7,"credit positions stay visible and cannot become ordinary cash order sources");
            io.SetRow(0,3,"00");io.SetRow(0,22,"0");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(!balance.CashSourceValidated["005930"]&&balance.Holdings[0].Quantity==7&&balance.Holdings[0].CreditQty==0,"undocumented cash kind is neither guessed as cash nor falsely labeled as credit from a zero loan");
            io.SetRow(0,3,"보통");io.SetRow(0,22,"");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(!balance.CashSourceValidated["005930"],"missing loan field cannot certify a cash position source");
            io.SetRow(0,22,"unavailable");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings.Count==1&&balance.Holdings[0].Quantity==7&&!balance.CashSourceValidated["005930"]&&balance.Holdings[0].CreditQty==0,"malformed optional loan leaves real positions readable without certifying cash or inventing credit shares");
            io.SetRow(0,22,"1");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(!balance.CashSourceValidated["005930"],"ordinary label with nonzero loan remains ineligible");
            foreach(var field in new[]{2,3,22}) {
                io=new Response();io.Holding(0);io.ThrowField="m:0:"+field;balance=AccountQueries.ReadHoldings(io,utc,true);
                Check(balance.Holdings[0].Quantity==7&&balance.CashSourceValidated["005930"]==(field==2)&&balance.Holdings[0].CreditQty==0&&balance.BasisVerified,
                    "unavailable basis echo retains verified request context while missing ordinary-kind or loan proof still prevents cash-source validation");
            }
            io=new Response();io.Holding(0);io.SetRow(0,11,"2");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.UnfilledQuantities["005930"]==2&&balance.Holdings[0].SellPending==0&&balance.Holdings[0].BuyPending==0,"directionless unfilled shares remain explicit for reconciliation instead of invented side totals");
            io.SetRow(0,4,"0");io.SetRow(0,10,"0");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings[0].Quantity==0&&balance.UnfilledQuantities["005930"]==2,"pending buys may exceed the current trade position without becoming owned shares");
            io=new Response();io.Holding(0);io.SetRow(0,10,"8");Reject(()=>AccountQueries.ReadHoldings(io,utc,true),"orderable quantity above trade holding quantity fails closed");
            io.SetRow(0,10,"-1");Reject(()=>AccountQueries.ReadHoldings(io,utc,true),"negative sellable quantity rejects");
            io.SetRow(0,10,"1.5");Reject(()=>AccountQueries.ReadHoldings(io,utc,true),"fractional sellable domestic shares reject");
            io=new Response();io.Holding(0);io.SetRow(0,4,"2147483648");Reject(()=>AccountQueries.ReadHoldings(io,utc,true),"trade holdings outside supported share bounds reject");
            io=new Response();io.Holding(0);io.SetRow(0,11,"-1");Reject(()=>AccountQueries.ReadHoldings(io,utc,true),"negative unfilled shares reject");
            io=new Response();io.Holding(0);foreach(var field in new[]{5,6,7,8,9})io.SetRow(0,field,"");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings[0].Quantity==7&&balance.BasisVerified,"unused blank settlement metadata does not prevent displaying trade-basis positions");
            io=new Response();io.Holding(0);io.SetRow(0,2,"2");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings[0].Quantity==7&&!balance.BasisVerified&&!balance.BasisValidated["005930"]&&!balance.CashSourceValidated["005930"]&&balance.BasisDiagnostics["005930"]=="SettlementBasisCode2","settlement-basis positions remain readable but cannot certify trade-basis orders");
            foreach(var basis in new[]{"","0","9","매매기준",account}) {
                io=new Response();io.Holding(0);io.SetRow(0,2,basis);balance=AccountQueries.ReadHoldings(io,utc,true);
                Check(balance.Holdings.Count==1&&balance.Holdings[0].Quantity==7&&balance.RequestBasisVerified&&balance.BasisVerified&&balance.BasisValidated["005930"]&&balance.CashSourceValidated["005930"],
                    "missing or unrecognized echo does not discard a successful bound trade-basis request or independently proven cash position");
                Check(balance.BasisDiagnostics["005930"]==(basis==""?"Blank":"Unknown"),"basis diagnosis uses a fixed classification and never raw provider values");
            }
            foreach(var unboundBasis in new[]{"1","","0","9","매매기준"}) {
                io=new Response();io.Holding(0);io.SetRow(0,2,unboundBasis);balance=AccountQueries.ReadHoldings(io,utc);
                Check(balance.Holdings[0].Quantity==7&&!balance.RequestBasisVerified&&!balance.BasisVerified
                    &&!balance.BasisValidated["005930"]&&!balance.CashSourceValidated["005930"],
                    "an echo or unknown metadata alone cannot certify quantity basis without a successful bound request");
            }
            foreach(var basis in new[]{"3","4","5"}) {
                io=new Response();io.Holding(0);io.SetRow(0,2,basis);balance=AccountQueries.ReadHoldings(io,utc,true);
                Check(balance.Holdings.Count==1&&!balance.BasisVerified&&!balance.CashSourceValidated["005930"]&&balance.BasisDiagnostics["005930"]!="Unknown","documented nontrade basis labels stay recognized but cannot approve a cash trade");
            }
            for(int digit=0;digit<=9;digit++) {
                io=new Response();io.Holding(0);io.SetRow(0,2,digit.ToString(CultureInfo.InvariantCulture));balance=AccountQueries.ReadHoldings(io,utc,true);
                var expectedBasis=digit<2||digit>5;
                Check(balance.BasisValueDiagnostics["005930"]=="Code"+digit.ToString(CultureInfo.InvariantCulture)
                    &&balance.BasisVerified==expectedBasis&&balance.BasisValidated["005930"]==expectedBasis,
                    "each enum remains diagnostic while only explicit nontrade codes contradict bound trade-request evidence");
            }
            foreach(var basisValue in new[]{"", "01", "x", "매매기준",account,password}) {
                io=new Response();io.Holding(0);io.SetRow(0,2,basisValue);balance=AccountQueries.ReadHoldings(io,utc,true);
                var expected=basisValue.Length==0?"Blank":basisValue.Length==1?"UnsupportedCharacter":"UnsupportedLength";
                var summary=AccountQueries.BasisDiagnosticsSummary(balance);
                Check(balance.BasisValueDiagnostics["005930"]==expected&&summary.Contains(expected+"=1")
                    &&!summary.Contains(account)&&!summary.Contains(password)&&!summary.Contains("매매기준")&&balance.BasisVerified,
                    "basis shape diagnostics remain bounded classifications and never reveal raw credential-shaped response values");
            }
            io=new Response();io.Holding(0);io.ThrowField="m:0:2";balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.BasisValueDiagnostics["005930"]=="Unavailable"&&AccountQueries.BasisDiagnosticsSummary(balance).Contains("Unavailable=1")&&balance.BasisVerified,
                "getter unavailability remains diagnostic without replacing or discarding bound request evidence");
            io=new Response();io.Holding(0);io.Holding(1,"A000660");io.SetRow(1,2,"2");balance=AccountQueries.ReadHoldings(io,utc,true);
            var enumSummary=AccountQueries.BasisDiagnosticsSummary(balance);
            Check(enumSummary.Contains("Code1=1")&&enumSummary.Contains("Code2=1")&&enumSummary.Length<512&&!enumSummary.Contains("005930"),
                "basis diagnostic summary counts exact safe enums without disclosing holdings or unbounded data");
            Check(!balance.BasisVerified&&balance.RequestBasisVerified&&balance.BasisValidated["005930"]&&!balance.BasisValidated["000660"],
                "one explicit contradictory basis rejects the whole frame while preserving individual bound row evidence");
            balance.BasisValueDiagnostics["005930"]=account;
            Check(!AccountQueries.BasisDiagnosticsSummary(balance).Contains(account)&&AccountQueries.BasisDiagnosticsSummary(balance).Contains("UnsupportedCharacter=1"),
                "tampered diagnostic labels cannot escape the fixed summary allowlist");
            Check(AccountQueries.BasisDiagnosticsSummary(null)=="SABA609Q1 MO2 enum: SnapshotUnavailable",
                "missing snapshot has an explicit noncertifying diagnosis");
            io=new Response();io.Holding(0);io.Holding(1,"A000660");io.SetRow(1,2,"");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings.Count==2&&balance.BasisVerified&&balance.BasisValidated["005930"]&&balance.BasisValidated["000660"],
                "an omitted echo in one row does not reject other valid quantities from the same bound trade request");
            io=new Response();io.Holding(0);io.SetRow(0,13,"-71000");balance=AccountQueries.ReadHoldings(io,utc,true);
            Check(balance.Holdings[0].Price==71000&&balance.CashSourceValidated["005930"],"signed TRDPRC_1 current-price encoding normalizes to an absolute share price");
            io.SetRow(0,13,"broken");Reject(()=>AccountQueries.ReadHoldings(io,utc,true),"an actually malformed core price still rejects without inventing a value");
            io=new Response();io.Holding(0);io.Holding(1,"A005930","유통융자");failure=Capture(()=>AccountQueries.ReadHoldings(io,utc,true));
            Check(AccountQueries.FailureContext(failure)!=null&&AccountQueries.FailureContext(failure).Contains("duplicate stock symbol"),"cash and credit rows for one symbol fail closed instead of being dropped or silently merged");
            io=new Response();io.Holding(0);io.ThrowMethod="GetMultiData";failure=Capture(()=>AccountQueries.ReadHoldings(io,utc,true));context=AccountQueries.FailureContext(failure);
            Check(context!=null&&context.Contains("SABA609Q1 multi[0,0]")&&!failure.ToString().Contains(account)&&!failure.ToString().Contains(password)&&failure.InnerException==null,"trade holdings getter failure retains only safe query and field context");
            Reject(()=>AccountQueries.ReadHoldings(new Response(),DateTime.SpecifyKind(utc,DateTimeKind.Local)),"trade balance requires an explicit UTC response time");
            balance=AccountQueries.ReadHoldings(new Response(),utc);
            Check(balance.Holdings.Count==0&&!balance.BrokerDateValidated&&!balance.RealizedProfit.HasValue&&!balance.BasisVerified&&!balance.RequestBasisVerified,"empty positions do not invent broker-session, profit or request-basis proof");
            balance=AccountQueries.ReadHoldings(new Response(),utc,true);
            Check(balance.Holdings.Count==0&&balance.BasisVerified&&balance.RequestBasisVerified&&!balance.BrokerDateValidated&&!balance.RealizedProfit.HasValue,"explicit validated trade request context scopes an empty balance without inventing broker dates or rows");

            io=new Response();io.Capacity(0,"40","999","99999999");io.Capacity(1,"100","11","800000");
            var capacity=AccountQueries.ReadCapacity(io,"005930",70000,utc);
            Check(capacity.Ratio==100&&capacity.Quantity==11&&capacity.Amount==800000&&capacity.Code=="005930","ratio100 is selected explicitly after a leveraged first row");
            Check(!capacity.OrderSemanticsVerified,"ratio100 snapshot remains unverified for fees and pending reservations");
            io=new Response();io.Capacity(0,"40","999","99999999");Reject(()=>AccountQueries.ReadCapacity(io,"005930",70000,utc),"missing ratio100 cannot fall back to first row");
            io.Capacity(1,"100","11","800000");io.Capacity(2,"100","12","900000");Reject(()=>AccountQueries.ReadCapacity(io,"005930",70000,utc),"duplicate cash100 rows reject ambiguity");
            io=new Response();io.Capacity(0,"100","1.5","800000");Reject(()=>AccountQueries.ReadCapacity(io,"005930",70000,utc),"fractional orderable domestic shares reject");
            io=new Response();io.Capacity(0,"100","11","800000");io.SetRow(0,2,"10");Reject(()=>AccountQueries.ReadCapacity(io,"005930",70000,utc),"capacity cannot exceed reported maximum");
            io=new Response{Count=10000};Reject(()=>AccountQueries.ReadCapacity(io,"005930",70000,utc),"row cap fails instead of trusting possible truncation");

            io=new Response();var orders=AccountQueries.ReadOrders(io,"20261112",utc);
            Check(orders.Orders.Count==0&&!orders.IsComplete&&!orders.HasExecutionIds,"empty account orders are displayed but cannot certify reconciliation");
            io.Order(0);io.SetRow(0,24,"4");io.SetRow(0,25,"70100");io.SetRow(0,26,"6");
            orders=AccountQueries.ReadOrders(io,"20261112",utc);var order=orders.Orders[0];
            Check(order.OrderNumber=="00123"&&order.OriginalOrderNumber==""&&order.Code=="005930"&&order.SideCode=="2","aggregate order identifiers preserve leading zero and normalize symbol");
            Check(order.Quantity==10&&order.FilledQuantity==4&&order.OpenQuantity==6&&order.AverageFillPrice==70100&&!order.HasExecutionId,"aggregate partial fill is never an incremental execution event");
            io.SetRow(0,21,"3");io.SetRow(0,31,"거부");orders=AccountQueries.ReadOrders(io,"20261112",utc);
            Check(orders.Orders[0].HasRejection&&orders.Orders[0].StatusName=="거부","exchange rejection remains explicit rather than inferred as accepted");
            io.SetRow(0,14,"Name\r\nInjected\u202E");orders=AccountQueries.ReadOrders(io,"20261112",utc);
            Check(orders.Orders[0].Name=="NameInjected","display text cannot insert multiline or bidi records");
            io.Order(1,"00123");Reject(()=>AccountQueries.ReadOrders(io,"20261112",utc),"duplicate aggregate IDs cannot be misread as two executions");
            io=new Response();io.Order(0,"0");Reject(()=>AccountQueries.ReadOrders(io,"20261112",utc),"zero broker order ID is invalid even without provider error state");
            io=new Response();io.Order(0);io.SetRow(0,24,"4");io.SetRow(0,26,"10");Reject(()=>AccountQueries.ReadOrders(io,"20261112",utc),"filled plus open shares cannot exceed order quantity");
            io.SetRow(0,26,"6");io.SetRow(0,25,"");Reject(()=>AccountQueries.ReadOrders(io,"20261112",utc),"filled aggregate requires an actual broker average fill price");
            io=new Response{Count=0.5m};failure=Capture(()=>AccountQueries.ReadOrders(io,"20261112",utc));
            Check(failure is InvalidOperationException&&AccountQueries.FailureContext(failure).Contains("SABA231Q1 row count outside verified bounds"),"fractional row-count validation keeps its owned query context");

            var holding=new Holding {Code="005930",Name="Samsung",Quantity=10,AvgPrice=70000,Price=71000};
            var value=HoldingValuation.From(holding);
            Check(value.CostBasis==700000&&value.MarketValue==710000&&value.UnrealizedProfit==10000,"actual holding valuations use actual quantity and average price");
            Check(value.ReturnPercent==10000m/700000m*100m&&value.ExcludesFeesAndTax,"unrealized return explicitly excludes fee/tax estimates");
            holding.Price=69000;Check(HoldingValuation.From(holding).UnrealizedProfit==-10000,"losses retain sign");
            holding.Price=0;value=HoldingValuation.From(holding);Check(!value.IsComplete&&!value.MarketValue.HasValue&&!value.UnrealizedProfit.HasValue,"missing current price is not reported as total loss");
            holding.Price=71000;holding.AvgPrice=0;value=HoldingValuation.From(holding);Check(!value.CostBasis.HasValue&&!value.ReturnPercent.HasValue,"missing cost basis is not fabricated as zero");
            holding.Quantity=0;value=HoldingValuation.From(holding);Check(value.CostBasis==0&&value.MarketValue==0&&value.UnrealizedProfit==0&&!value.ReturnPercent.HasValue,"pending-only empty holdings have no invented return percentage");
            holding.Quantity=1.5m;Reject(()=>HoldingValuation.From(holding),"fractional holding quantity rejects");
            var totals=AccountValuation.From(new[]{new Holding{Code="005930",Quantity=10,AvgPrice=70000,Price=71000},new Holding{Code="000660",Quantity=2,AvgPrice=200000,Price=190000}});
            Check(totals.HoldingCount==2&&totals.CostBasis==1100000&&totals.MarketValue==1090000&&totals.UnrealizedProfit==-10000,"account total sums signed actual holding valuations");
            totals=AccountValuation.From(new[]{new Holding{Code="005930",Quantity=10,AvgPrice=70000,Price=0}});
            Check(!totals.IsComplete&&!totals.UnrealizedProfit.HasValue,"incomplete price prevents misleading account total profit");
            totals=AccountValuation.From(new Holding[0]);Check(totals.IsComplete&&totals.MarketValue==0&&totals.UnrealizedProfit==0&&!totals.ReturnPercent.HasValue,"empty account portfolio has zero values without division by zero");
            foreach(var type in new[]{typeof(AccountCashSnapshot),typeof(AccountOrderCapacity),typeof(BrokerOrderRow),typeof(BrokerOrdersSnapshot),typeof(BrokerHoldingsSnapshot)})
                Check(!type.GetFields().Any(f=>f.Name.IndexOf("account",StringComparison.OrdinalIgnoreCase)>=0||f.Name.IndexOf("password",StringComparison.OrdinalIgnoreCase)>=0),"broker DTO has no credential fields: "+type.Name);
            return checks;
        }
    }
}
