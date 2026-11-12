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
            public string ThrowMethod;
            public readonly Dictionary<string,object> Fields=new Dictionary<string,object>();
            public object Invoke(string name,params object[] args) {
                if(ThrowGetter||name==ThrowMethod)throw new InvalidOperationException("account=12345678901 password=4321 SECRET");
                if(name=="GetMultiRowCount")return Count;
                string key;
                if(name=="GetSingleData")key="s:"+args[0];
                else if(name=="GetMultiData")key="m:"+args[0]+":"+args[1];
                else throw new InvalidOperationException("Unexpected test query method.");
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
            inputs=AccountQueries.CapacityInputs(account,password,"005930",70000m);
            Check(inputs.Count==9&&inputs[3]=="A005930"&&inputs[4]=="1"&&inputs[5]=="2"&&inputs[6]=="0"&&inputs[7]=="00"&&inputs[8]=="70000","capacity query uses regular cash limit order at requested price");
            var prior=CultureInfo.CurrentCulture;
            try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");Check(AccountQueries.CapacityInputs(account,password,"A005930",70000m)[8]=="70000","price serialization remains invariant");}
            finally {CultureInfo.CurrentCulture=prior;}
            inputs=AccountQueries.OrdersInputs(account,password,"20261112");
            Check(inputs.Count==9&&inputs[0]=="20261112"&&inputs[1]==account&&inputs[4]=="0"&&inputs[5]=="0"&&inputs[6]=="*"&&inputs[7]=="01"&&inputs[8]=="Y","order query explicitly requests all aggregate cash-account orders");
            Reject(()=>AccountQueries.CashInputs("bad",password),"invalid account rejects before provider calls");
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
            foreach(var type in new[]{typeof(AccountCashSnapshot),typeof(AccountOrderCapacity),typeof(BrokerOrderRow),typeof(BrokerOrdersSnapshot)})
                Check(!type.GetFields().Any(f=>f.Name.IndexOf("account",StringComparison.OrdinalIgnoreCase)>=0||f.Name.IndexOf("password",StringComparison.OrdinalIgnoreCase)>=0),"broker DTO has no credential fields: "+type.Name);
            return checks;
        }
    }
}
