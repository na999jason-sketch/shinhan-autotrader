using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    // These DTOs contain broker data only. Account numbers and passwords exist
    // solely in transient request dictionaries and are never copied into them.
    public sealed class AccountCashSnapshot {
        public decimal DepositCash, CashOrderable, Cash100Orderable;
        public DateTime ReceivedUtc;
        public decimal NonNegativeCash100 { get { return Math.Max(0m,Cash100Orderable); } }
        // Fees, outstanding reservations and current server-version semantics
        // still require independent verification before an order can be enabled.
        public bool OrderSemanticsVerified { get { return false; } }
    }

    public sealed class AccountOrderCapacity {
        public string Code;
        public decimal LimitPrice, Ratio, Quantity, MaximumQuantity, Amount, MaximumAmount;
        public DateTime ReceivedUtc;
        public bool OrderSemanticsVerified { get { return false; } }
    }

    public sealed class BrokerOrderRow {
        public string OrderNumber,OriginalOrderNumber,Code,Name,SideCode,AmendCancelCode;
        public string ProductCode,CreditCode,OrderClock,FillClock,RejectCode,RejectName,ConfirmationCode,StatusName;
        public decimal Quantity,LimitPrice,FilledQuantity,AverageFillPrice,OpenQuantity,ConfirmedQuantity,AutoCancelledQuantity;
        public bool HasRejection { get { return ConfirmationCode=="1"||ConfirmationCode=="2"||ConfirmationCode=="3"; } }
        // SABA231Q1 aggregate rows are a snapshot, never individual executions.
        public bool HasExecutionId { get { return false; } }
    }

    public sealed class BrokerOrdersSnapshot {
        public string TradingDate;
        public DateTime ReceivedUtc;
        public readonly List<BrokerOrderRow> Orders=new List<BrokerOrderRow>();
        // The recovered resources do not establish continuation/end-of-stream
        // guarantees. An empty response must not unlock an unknown live order.
        public bool IsComplete { get { return false; } }
        public bool HasExecutionIds { get { return false; } }
    }

    public sealed class BrokerHoldingsSnapshot {
        // SABA609Q1 SI3=1/MO2=1 is the trade basis, including today's fills.
        // Its settlement-day quantity MO9 must never replace Holding.Quantity.
        public readonly List<Holding> Holdings=new List<Holding>();
        public readonly Dictionary<string,int> SellableQuantities=new Dictionary<string,int>(StringComparer.Ordinal);
        public readonly Dictionary<string,bool> CashSourceValidated=new Dictionary<string,bool>(StringComparer.Ordinal);
        // MO11 has no buy/sell direction. It is not safe to copy it to either
        // Holding.BuyPending or Holding.SellPending, or to ignore it at preflight.
        public readonly Dictionary<string,int> UnfilledQuantities=new Dictionary<string,int>(StringComparer.Ordinal);
        public DateTime ReceivedUtc,RequestStartedUtc;
        public string BrokerDate;
        public bool BrokerDateValidated;
        public decimal? RealizedProfit,UnrealizedProfit;
    }

    public sealed class HoldingValuation {
        public string Code,Name;
        public decimal Quantity,AveragePrice,CurrentPrice;
        public decimal? CostBasis,MarketValue,UnrealizedProfit,ReturnPercent;
        public bool ExcludesFeesAndTax { get { return true; } }
        public bool IsComplete { get { return CostBasis.HasValue&&MarketValue.HasValue; } }

        public static HoldingValuation From(Holding holding) {
            if(holding==null)throw new ArgumentNullException("holding");
            if(Rules.Code(holding.Code)==""||holding.Quantity<0||holding.Quantity!=decimal.Truncate(holding.Quantity)
                ||holding.AvgPrice<0||holding.Price<0)throw new InvalidOperationException("Invalid holding valuation input.");
            var result=new HoldingValuation {Code=Rules.Code(holding.Code),Name=holding.Name,Quantity=holding.Quantity,
                AveragePrice=holding.AvgPrice,CurrentPrice=holding.Price};
            result.CostBasis=holding.Quantity==0?0m:(holding.AvgPrice>0?(decimal?)(holding.Quantity*holding.AvgPrice):null);
            result.MarketValue=holding.Quantity==0?0m:(holding.Price>0?(decimal?)(holding.Quantity*holding.Price):null);
            if(result.IsComplete) {
                result.UnrealizedProfit=result.MarketValue.Value-result.CostBasis.Value;
                if(result.CostBasis.Value>0)result.ReturnPercent=result.UnrealizedProfit.Value/result.CostBasis.Value*100m;
            }
            return result;
        }
    }

    public sealed class AccountValuation {
        public decimal? CostBasis,MarketValue,UnrealizedProfit,ReturnPercent;
        public int HoldingCount;
        public bool IsComplete { get { return CostBasis.HasValue&&MarketValue.HasValue; } }
        public bool ExcludesFeesAndTax { get { return true; } }
        public static AccountValuation From(IEnumerable<Holding> holdings) {
            if(holdings==null)throw new ArgumentNullException("holdings");
            var values=holdings.Select(HoldingValuation.From).ToList();
            var result=new AccountValuation {HoldingCount=values.Count};
            if(values.All(x=>x.CostBasis.HasValue))result.CostBasis=values.Sum(x=>x.CostBasis.Value);
            if(values.All(x=>x.MarketValue.HasValue))result.MarketValue=values.Sum(x=>x.MarketValue.Value);
            if(result.IsComplete) {
                result.UnrealizedProfit=result.MarketValue.Value-result.CostBasis.Value;
                if(result.CostBasis.Value>0)result.ReturnPercent=result.UnrealizedProfit.Value/result.CostBasis.Value*100m;
            }
            return result;
        }
    }

    public static class AccountQueries {
        // Only our own fixed query/field descriptions may reach diagnostics.
        // An arbitrary provider exception is never trusted as displayable text.
        sealed class AccountQueryException : InvalidOperationException {
            public readonly string Context;
            public AccountQueryException(string context):base(context){Context=context;}
        }
        internal static string FailureContext(Exception error) {
            var failure=error as AccountQueryException;
            return failure==null?null:failure.Context;
        }
        const int MaximumRows=10000;
        static void Credentials(string account,string password) {
            var number=(account??"").Trim();
            if(number.Length!=11||number.Any(c=>c<'0'||c>'9'))throw new ArgumentException("An eleven digit account number is required.");
            if(string.IsNullOrWhiteSpace(password)||password.Length>9||password.Any(char.IsControl))
                throw new ArgumentException("A supported account password is required.");
        }
        static void Utc(DateTime utc) {if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC response time required.");}
        static string Date(string date) {
            DateTime parsed;
            if(!DateTime.TryParseExact(date,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))
                throw new ArgumentException("A valid yyyyMMdd trading date is required.");
            return date;
        }
        static string Code(string code) {
            var normalized=Rules.Code(code);
            if(normalized=="")throw new ArgumentException("A six digit stock symbol is required.");
            return normalized;
        }
        public static Dictionary<int,string> CashInputs(string account,string password) {
            Credentials(account,password);
            return new Dictionary<int,string>{{0,account.Trim()},{1,"01"},{2,password},{3,"1"}};
        }
        public static Dictionary<int,string> HoldingsInputs(string account,string password) {
            Credentials(account,password);
            // Average cost, trade basis, ordinary product, excluding ELW, and
            // both single/multi output are explicitly bound by this request.
            return new Dictionary<int,string>{{0,account.Trim()},{1,"01"},{2,password},{3,"1"},{4,"1"},{5,"1"},{6,"0"}};
        }
        public static Dictionary<int,string> CapacityInputs(string account,string password,string code,decimal limitPrice) {
            Credentials(account,password);code=Code(code);
            if(limitPrice<=0||limitPrice!=decimal.Truncate(limitPrice))throw new ArgumentException("A positive whole-won limit price is required.");
            return new Dictionary<int,string>{{0,account.Trim()},{1,"01"},{2,password},{3,"A"+code},
                {4,"1"},{5,"2"},{6,"0"},{7,"00"},{8,limitPrice.ToString(CultureInfo.InvariantCulture)}};
        }
        public static Dictionary<int,string> OrdersInputs(string account,string password,string tradingDate) {
            Credentials(account,password);Date(tradingDate);
            return new Dictionary<int,string>{{0,tradingDate},{1,account.Trim()},{2,password},{3,"00"},{4,"0"},
                {5,"0"},{6,"*"},{7,"01"},{8,"Y"}};
        }
        static string Single(IIndiTransport io,int field,string query) {
            try {return (Convert.ToString(io.Invoke("GetSingleData",field),CultureInfo.InvariantCulture)??"").Trim();}
            catch {throw new AccountQueryException(query+" single["+field+"] read failed; provider text withheld.");}
        }
        static string Multi(IIndiTransport io,int row,int field,string query) {
            try {return (Convert.ToString(io.Invoke("GetMultiData",row,field),CultureInfo.InvariantCulture)??"").Trim();}
            catch {throw new AccountQueryException(query+" multi["+row+","+field+"] read failed; provider text withheld.");}
        }
        static decimal Number(string value,string field,bool signed=false,bool optional=false) {
            return IndiResponseParser.RequiredNumber(value,field,signed,optional);
        }
        static decimal Quantity(string value,string field,bool optional=false) {
            var quantity=Number(value,field,false,optional);
            if(quantity!=decimal.Truncate(quantity)||quantity>int.MaxValue)throw new AccountQueryException("Invalid quantity: "+field);
            return quantity;
        }
        static int Rows(IIndiTransport io,string query) {
            object raw;
            try {raw=io.Invoke("GetMultiRowCount");}catch {throw new AccountQueryException(query+" row count read failed; provider text withheld.");}
            var count=IndiResponseParser.RequiredNumber(raw,query+" row count");
            if(count!=decimal.Truncate(count)||count>=MaximumRows)throw new AccountQueryException(query+" row count outside verified bounds.");
            return (int)count;
        }
        static string OrderId(string value,bool original=false) {
            if(original&&(value==""||value.All(c=>c=='0')))return "";
            if(value.Length==0||value.Length>20||value.Any(c=>c<'0'||c>'9')||value.All(c=>c=='0'))
                throw new AccountQueryException("SABA231Q1 invalid "+(original?"original order multi[1]":"order number multi[0]")+".");
            return value;
        }
        static string Text(string value) {
            // Names/statuses may be displayed, but control/bidi data cannot create
            // arbitrary log records. Neither account nor password is read here.
            return new string(value.Where(c=>!char.IsControl(c)&&char.GetUnicodeCategory(c)!=UnicodeCategory.Format).Take(120).ToArray());
        }
        static decimal? OptionalSingleNumber(IIndiTransport io,int field,string query) {
            string value;
            // Some installed server versions omit aggregate output. This must
            // remain unknown, rather than inventing a zero profit or rejecting
            // independently readable positions. Provider text is never kept.
            try {value=Single(io,field,query);}catch(AccountQueryException){return null;}
            return value==""?null:(decimal?)Number(value,query+" single["+field+"]",true);
        }
        public static BrokerHoldingsSnapshot ReadHoldings(IIndiTransport io,DateTime receivedUtc) {
            if(io==null)throw new ArgumentNullException("io");Utc(receivedUtc);
            const string query="SABA609Q1";
            var result=new BrokerHoldingsSnapshot {ReceivedUtc=receivedUtc,BrokerDate=""};
            for(int row=0,count=Rows(io,query);row<count;row++) {
                var code=Rules.Code(Multi(io,row,0,query));
                if(code=="")throw new AccountQueryException(query+" invalid stock symbol multi[0].");
                if(result.SellableQuantities.ContainsKey(code))
                    throw new AccountQueryException(query+" duplicate stock symbol multi[0]; cash/credit rows cannot be merged safely.");
                if(Multi(io,row,2,query)!="1")
                    throw new AccountQueryException(query+" trade-basis response required in multi[2].");
                var kind=Multi(io,row,3,query);
                var quantity=Quantity(Multi(io,row,4,query),query+" holding quantity[4]");
                // These fields are unsettled trades and settlement-day stock,
                // not open buy/sell orders. Validate, but do not relabel them.
                for(int field=5;field<=9;field++)Quantity(Multi(io,row,field,query),query+" settlement quantity["+field+"]");
                var sellable=Quantity(Multi(io,row,10,query),query+" orderable quantity[10]");
                var unfilled=Quantity(Multi(io,row,11,query),query+" unfilled quantity[11]");
                if(sellable>quantity)
                    throw new AccountQueryException(query+" orderable quantity exceeds trade-basis holdings multi[4/10].");
                var loanText=Multi(io,row,22,query);
                var loan=Number(loanText,query+" loan amount[22]",false,true);
                // The official description documents the literal '보통'. No
                // undocumented numeric alias is assumed to mean cash stock.
                var cash=kind=="보통"&&loanText!=""&&loan==0;
                result.Holdings.Add(new Holding {Code=code,Name=Text(Multi(io,row,1,query)),Quantity=quantity,
                    AvgPrice=Number(Multi(io,row,12,query),query+" average price[12]"),
                    Price=Number(Multi(io,row,13,query),query+" current price[13]"),CreditQty=cash?0m:quantity});
                result.SellableQuantities.Add(code,(int)sellable);
                result.UnfilledQuantities.Add(code,(int)unfilled);
                result.CashSourceValidated.Add(code,cash);
            }
            string brokerDate;
            try {brokerDate=Single(io,0,query);}catch(AccountQueryException){brokerDate="";}
            DateTime parsed;
            if(brokerDate.Length==8&&brokerDate.All(c=>c>='0'&&c<='9')&&
                DateTime.TryParseExact(brokerDate,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)) {
                result.BrokerDate=brokerDate;result.BrokerDateValidated=true;
            }
            result.RealizedProfit=OptionalSingleNumber(io,16,query);
            result.UnrealizedProfit=OptionalSingleNumber(io,19,query);
            return result;
        }
        public static AccountCashSnapshot ReadCash(IIndiTransport io,DateTime receivedUtc) {
            if(io==null)throw new ArgumentNullException("io");Utc(receivedUtc);
            const string query="SABA602Q1";
            return new AccountCashSnapshot {DepositCash=Number(Single(io,1,query),query+" single[1]",true),
                CashOrderable=Number(Single(io,20,query),query+" single[20]",true),
                Cash100Orderable=Number(Single(io,30,query),query+" single[30]",true),ReceivedUtc=receivedUtc};
        }
        public static AccountOrderCapacity ReadCapacity(IIndiTransport io,string code,decimal limitPrice,DateTime receivedUtc) {
            if(io==null)throw new ArgumentNullException("io");Utc(receivedUtc);code=Code(code);
            if(limitPrice<=0||limitPrice!=decimal.Truncate(limitPrice))throw new ArgumentException("A positive whole-won limit price is required.");
            const string query="SABA415Q1";AccountOrderCapacity result=null;
            for(int row=0,count=Rows(io,query);row<count;row++) {
                var ratio=Number(Multi(io,row,0,query),query+" ratio[0]");
                if(ratio!=100m)continue;
                if(result!=null)throw new AccountQueryException("SABA415Q1 returned multiple cash100 rows.");
                result=new AccountOrderCapacity {Code=code,LimitPrice=limitPrice,Ratio=ratio,ReceivedUtc=receivedUtc,
                    Quantity=Quantity(Multi(io,row,1,query),query+" quantity[1]"),
                    MaximumQuantity=Quantity(Multi(io,row,2,query),query+" maximum quantity[2]"),
                    Amount=Number(Multi(io,row,4,query),query+" amount[4]"),
                    MaximumAmount=Number(Multi(io,row,5,query),query+" maximum amount[5]")};
                if(result.Quantity>result.MaximumQuantity||result.Amount>result.MaximumAmount)
                    throw new AccountQueryException("SABA415Q1 returned inconsistent order capacity.");
            }
            if(result==null)throw new AccountQueryException("SABA415Q1 did not return an unambiguous cash100 row.");
            return result;
        }
        public static BrokerOrdersSnapshot ReadOrders(IIndiTransport io,string tradingDate,DateTime receivedUtc) {
            if(io==null)throw new ArgumentNullException("io");Utc(receivedUtc);Date(tradingDate);
            const string query="SABA231Q1";
            var result=new BrokerOrdersSnapshot {TradingDate=tradingDate,ReceivedUtc=receivedUtc};
            var seen=new HashSet<string>(StringComparer.Ordinal);
            for(int row=0,count=Rows(io,query);row<count;row++) {
                var id=OrderId(Multi(io,row,0,query));
                if(!seen.Add(id))throw new AccountQueryException("SABA231Q1 duplicate aggregate order identifier multi[0].");
                var code=Rules.Code(Multi(io,row,13,query));var side=Multi(io,row,4,query);
                if(code=="")throw new AccountQueryException("SABA231Q1 invalid stock symbol multi[13].");
                if(side!="1"&&side!="2")throw new AccountQueryException("SABA231Q1 invalid stock side multi[4].");
                var item=new BrokerOrderRow {OrderNumber=id,OriginalOrderNumber=OrderId(Multi(io,row,1,query),true),
                    Code=code,Name=Text(Multi(io,row,14,query)),SideCode=side,AmendCancelCode=Text(Multi(io,row,6,query)),
                    ProductCode=Text(Multi(io,row,17,query)),CreditCode=Text(Multi(io,row,29,query)),
                    Quantity=Quantity(Multi(io,row,15,query),query+" quantity[15]"),
                    LimitPrice=Number(Multi(io,row,16,query),query+" price[16]"),
                    FilledQuantity=Quantity(Multi(io,row,24,query),query+" filled quantity[24]"),
                    AverageFillPrice=Number(Multi(io,row,25,query),query+" average fill[25]",false,true),
                    OpenQuantity=Quantity(Multi(io,row,26,query),query+" open quantity[26]"),
                    ConfirmedQuantity=Quantity(Multi(io,row,28,query),query+" confirmed quantity[28]",true),
                    AutoCancelledQuantity=Quantity(Multi(io,row,35,query),query+" auto cancelled[35]",true),
                    OrderClock=Text(Multi(io,row,18,query)),FillClock=Text(Multi(io,row,22,query)),
                    RejectCode=Text(Multi(io,row,19,query)),RejectName=Text(Multi(io,row,20,query)),
                    ConfirmationCode=Text(Multi(io,row,21,query)),StatusName=Text(Multi(io,row,31,query))};
                if(item.FilledQuantity>item.Quantity||item.OpenQuantity>item.Quantity||item.FilledQuantity+item.OpenQuantity>item.Quantity)
                    throw new AccountQueryException("SABA231Q1 inconsistent aggregate order quantities multi[15/24/26].");
                if(item.FilledQuantity>0&&item.AverageFillPrice<=0)
                    throw new AccountQueryException("SABA231Q1 filled order is missing its average fill price multi[25].");
                result.Orders.Add(item);
            }
            return result;
        }
    }
}
