using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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

    public enum BrokerOrderQueryProfile { Unverified, ModernOrderState54 }
    public enum BrokerConfirmationKind { Normal, Rejected, Unavailable, Unknown, StatusCorroborated }

    public sealed class BrokerOrderRow {
        public string OrderNumber,OriginalOrderNumber,Code,Name,SideCode,AmendCancelCode;
        public string ProductCode,CreditCode,OrderClock,FillClock,RejectCode,RejectName,ConfirmationCode,StatusName;
        public string QuerySchemaSha256,OrderMethodCode,SorOrderNumber,SorOriginalOrderNumber;
        public BrokerOrderQueryProfile QueryProfile;
        public decimal Quantity,LimitPrice,FilledQuantity,AverageFillPrice,OpenQuantity,ConfirmedQuantity,AutoCancelledQuantity;
        // The verified 0x0208/54-field resource identifies MO21 as
        // ord_stat_tp_code. Its observed receipt code is corroborated by the
        // separate MO31 status, never normalized into legacy "0 normal".
        internal static bool SchemaHashValid(string value){return value!=null&&value.Length==64
            &&value.All(character=>character>='0'&&character<='9'||character>='a'&&character<='f'||character>='A'&&character<='F');}
        public int QuerySerializationVersion {get{return QueryProfile==BrokerOrderQueryProfile.ModernOrderState54?0x0208:0;}}
        public bool HasRecognizedModernSchema {get{return QueryProfile==BrokerOrderQueryProfile.ModernOrderState54&&SchemaHashValid(QuerySchemaSha256);}}
        public bool ModernVenueCompatible {get{return (string.IsNullOrWhiteSpace(OrderMethodCode)||OrderMethodCode=="1")
            &&NoRejectionValue(SorOrderNumber)&&NoRejectionValue(SorOriginalOrderNumber);}}
        bool HasCorroboratedModernReceipt {get{return HasRecognizedModernSchema&&ModernVenueCompatible
            &&ConfirmationCode=="1"&&StatusName=="접수"&&NoRejectionValue(RejectCode)&&NoRejectionValue(RejectName);}}
        public bool HasRejection { get { return ConfirmationCode=="1"&&!HasCorroboratedModernReceipt||ConfirmationCode=="2"||ConfirmationCode=="3"; } }
        // The legacy Helper describes a one-character enum; the current XTR
        // does not embed that mapping. Missing metadata never becomes code 0,
        // and current received-state corroboration remains a separate class.
        public BrokerConfirmationKind ConfirmationKind { get {
            return ConfirmationCode=="0"?BrokerConfirmationKind.Normal:HasCorroboratedModernReceipt?BrokerConfirmationKind.StatusCorroborated:
                HasRejection?BrokerConfirmationKind.Rejected:
                string.IsNullOrWhiteSpace(ConfirmationCode)?BrokerConfirmationKind.Unavailable:BrokerConfirmationKind.Unknown;
        } }
        public string ConfirmationShape { get {
            return ConfirmationKind==BrokerConfirmationKind.Unavailable?"Blank":ConfirmationKind==BrokerConfirmationKind.Normal?"Zero":
                ConfirmationKind==BrokerConfirmationKind.Rejected?"Reject":ConfirmationKind==BrokerConfirmationKind.StatusCorroborated?"StatusCorroborated":
                ConfirmationCode.All(value=>value=='0')?"PaddedZero":"Other";
        } }
        static bool NoRejectionValue(string value){return string.IsNullOrWhiteSpace(value)||value.All(character=>character=='0');}
        public string RejectCodeShape {get{return string.IsNullOrWhiteSpace(RejectCode)?"Blank":NoRejectionValue(RejectCode)?"Zero":"Present";}}
        public string RejectNameShape {get{return string.IsNullOrWhiteSpace(RejectName)?"Blank":NoRejectionValue(RejectName)?"Zero":"Present";}}
        public bool HasRejectionConflict { get {
            return HasRejection||!NoRejectionValue(RejectCode)||!NoRejectionValue(RejectName)
                ||!string.IsNullOrEmpty(StatusName)&&(StatusName.IndexOf("거부",StringComparison.Ordinal)>=0
                    ||StatusName.IndexOf("접속실패",StringComparison.Ordinal)>=0);
        } }
        // This is only one prerequisite for a separately verified, exact,
        // known-accepted terminal fill. It cannot prove acceptance or a fill.
        public bool TerminalConfirmationCompatible {get{return !HasRejectionConflict
            &&(QueryProfile==BrokerOrderQueryProfile.Unverified||HasRecognizedModernSchema&&ModernVenueCompatible)
            &&(ConfirmationKind==BrokerConfirmationKind.Normal||ConfirmationKind==BrokerConfirmationKind.Unavailable
                ||ConfirmationKind==BrokerConfirmationKind.StatusCorroborated);}}
        // SABA231Q1 aggregate rows are a snapshot, never individual executions.
        public bool HasExecutionId { get { return false; } }
    }

    public sealed class BrokerOrdersSnapshot {
        public string TradingDate;
        public DateTime ReceivedUtc,RequestStartedUtc;
        // Set only after the coordinator correlates a successful callback with
        // the positive request ID and the exact accepted account/date profile.
        // This scopes returned rows; it does not prove that absent rows do not
        // exist or manufacture individual execution identifiers.
        bool requestScopeBound;
        string scopeTradingDate;
        DateTime scopeStartedUtc,scopeReceivedUtc;
        byte[] requestAccountSalt,requestAccountDigest;
        [System.Xml.Serialization.XmlIgnore]
        public bool RequestScopeVerified {get{return requestScopeBound&&TradingDate==scopeTradingDate
            &&RequestStartedUtc.Kind==DateTimeKind.Utc&&ReceivedUtc.Kind==DateTimeKind.Utc
            &&RequestStartedUtc==scopeStartedUtc&&ReceivedUtc==scopeReceivedUtc
            &&requestAccountSalt!=null&&requestAccountSalt.Length==32&&requestAccountDigest!=null&&requestAccountDigest.Length==32;}}
        static bool AccountShape(string number){return number!=null&&number.Length==11&&number.All(value=>value>='0'&&value<='9');}
        static string AccountProfileKey(string number) {
            var input=Encoding.UTF8.GetBytes("ShinhanNativeTrader/real-account/v1/"+number);
            try{using(var sha=SHA256.Create())return string.Concat(sha.ComputeHash(input).Select(value=>value.ToString("x2",CultureInfo.InvariantCulture)));}
            finally{Array.Clear(input,0,input.Length);}
        }
        static byte[] AccountDigest(byte[] salt,string key) {
            var account=Encoding.ASCII.GetBytes(key);var input=new byte[salt.Length+account.Length];
            Buffer.BlockCopy(salt,0,input,0,salt.Length);Buffer.BlockCopy(account,0,input,salt.Length,account.Length);
            try{using(var sha=SHA256.Create())return sha.ComputeHash(input);}
            finally{Array.Clear(account,0,account.Length);Array.Clear(input,0,input.Length);}
        }
        public bool MatchesRequestAccount(string number) {
            if(!RequestScopeVerified||!AccountShape(number))return false;
            return MatchesRequestAccountKey(AccountProfileKey(number));
        }
        public bool MatchesRequestAccountKey(string key) {
            if(!RequestScopeVerified||key==null||key.Length!=64||key.Any(value=>!(value>='0'&&value<='9'||value>='a'&&value<='f')))return false;
            var digest=AccountDigest(requestAccountSalt,key);
            try{int different=0;for(int index=0;index<digest.Length;index++)different|=digest[index]^requestAccountDigest[index];return different==0;}
            finally{Array.Clear(digest,0,digest.Length);}
        }
        // Transient account binding is deliberately private and XmlIgnore.
        // It is never an account identifier persisted in a report or journal.
        internal void BindRequestScope(DateTime startedUtc,DateTime receivedUtc,bool verified,string number) {
            requestScopeBound=false;requestAccountSalt=null;requestAccountDigest=null;
            RequestStartedUtc=startedUtc;ReceivedUtc=receivedUtc;DateTime parsed;
            if(!verified||!AccountShape(number)||startedUtc.Kind!=DateTimeKind.Utc||receivedUtc.Kind!=DateTimeKind.Utc||startedUtc>receivedUtc
                ||TradingDate==null||TradingDate.Length!=8||!DateTime.TryParseExact(TradingDate,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))return;
            var salt=new byte[32];using(var random=RandomNumberGenerator.Create())random.GetBytes(salt);
            requestAccountSalt=salt;requestAccountDigest=AccountDigest(salt,AccountProfileKey(number));
            scopeTradingDate=TradingDate;scopeStartedUtc=startedUtc;scopeReceivedUtc=receivedUtc;requestScopeBound=true;
        }
        internal void CopyRequestScopeFrom(BrokerOrdersSnapshot source) {
            requestScopeBound=false;requestAccountSalt=null;requestAccountDigest=null;
            if(source==null||!source.RequestScopeVerified||TradingDate!=source.TradingDate
                ||RequestStartedUtc!=source.RequestStartedUtc||ReceivedUtc!=source.ReceivedUtc)return;
            scopeTradingDate=source.scopeTradingDate;scopeStartedUtc=source.scopeStartedUtc;scopeReceivedUtc=source.scopeReceivedUtc;
            requestAccountSalt=(byte[])source.requestAccountSalt.Clone();requestAccountDigest=(byte[])source.requestAccountDigest.Clone();requestScopeBound=true;
        }
        public readonly List<BrokerOrderRow> Orders=new List<BrokerOrderRow>();
        // The recovered resources do not establish continuation/end-of-stream
        // guarantees. An empty response must not unlock an unknown live order.
        public bool IsComplete { get { return false; } }
        public bool HasExecutionIds { get { return false; } }
    }

    public sealed class BrokerHoldingsSnapshot {
        // Quantity basis comes from a successfully bound SI3=1 full request and
        // its correlated successful callback, as verified by the coordinator.
        // MO2 remains diagnostic metadata: known conflicting bases invalidate
        // the row, while a missing echo never substitutes for request evidence.
        public readonly List<Holding> Holdings=new List<Holding>();
        public bool BasisVerified,RequestBasisVerified,RequestOrdinaryOnlyVerified;
        public readonly Dictionary<string,bool> BasisValidated=new Dictionary<string,bool>(StringComparer.Ordinal);
        public readonly Dictionary<string,string> BasisDiagnostics=new Dictionary<string,string>(StringComparer.Ordinal);
        // Bounded enum/shape classes only, never raw account response strings.
        public readonly Dictionary<string,string> BasisValueDiagnostics=new Dictionary<string,string>(StringComparer.Ordinal);
        public readonly Dictionary<string,int> SellableQuantities=new Dictionary<string,int>(StringComparer.Ordinal);
        public readonly Dictionary<string,bool> CashSourceValidated=new Dictionary<string,bool>(StringComparer.Ordinal);
        public readonly Dictionary<string,bool> CashProfileCompatible=new Dictionary<string,bool>(StringComparer.Ordinal);
        public readonly Dictionary<string,string> CashProfileDiagnostics=new Dictionary<string,string>(StringComparer.Ordinal);
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
            // both single/multi output and the installed 0x0208 definition's
            // SI7=1 KRX quotation profile are explicitly bound by this request.
            return new Dictionary<int,string>{{0,account.Trim()},{1,"01"},{2,password},{3,"1"},{4,"1"},{5,"1"},{6,"0"},{7,"1"}};
        }
        public static Dictionary<int,string> OrdinaryHoldingsInputs(string account,string password) {
            var fields=HoldingsInputs(account,password);fields[3]="5";return fields;
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
        static string OptionalProofMulti(IIndiTransport io,int row,int field,string query) {
            try {return Multi(io,row,field,query);}catch(AccountQueryException){return null;}
        }
        static decimal? OptionalProofNumber(string value,string field) {
            if(string.IsNullOrEmpty(value))return null;
            try {return Number(value,field);}catch(InvalidOperationException){return null;}
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
            if(value=="")return null;
            try {return Number(value,query+" single["+field+"]",true);}
            catch(InvalidOperationException){return null;}
        }
        static string BasisDiagnostic(string basis) {
            // Fixed classifications are safe to report. Raw account response
            // strings must not be copied into logs to diagnose an enum mismatch.
            switch(basis) {
                case "1":return "TradeBasisCode1";
                case "2":return "SettlementBasisCode2";
                case "3":return "CreditBalanceCode3";
                case "4":return "BondBalanceCode4";
                case "5":return "OrdinaryBalanceCode5";
                case "":return "Blank";
                default:return "Unknown";
            }
        }
        static string BasisValueDiagnostic(string basis) {
            if(basis==null)return "Unavailable";
            if(basis.Length==0)return "Blank";
            if(basis.Length!=1)return "UnsupportedLength";
            return basis[0]>='0'&&basis[0]<='9'?"Code"+basis:"UnsupportedCharacter";
        }
        public static string BasisDiagnosticsSummary(BrokerHoldingsSnapshot snapshot) {
            if(snapshot==null)return "SABA609Q1 MO2 enum: SnapshotUnavailable";
            var categories=new[]{"Code0","Code1","Code2","Code3","Code4","Code5","Code6","Code7","Code8","Code9",
                "Blank","Unavailable","UnsupportedLength","UnsupportedCharacter"};
            var counts=categories.ToDictionary(x=>x,x=>0,StringComparer.Ordinal);
            foreach(var holding in snapshot.Holdings) {
                string category;
                if(holding==null||holding.Code==null||!snapshot.BasisValueDiagnostics.TryGetValue(holding.Code,out category))category="Unavailable";
                if(!counts.ContainsKey(category??""))category="UnsupportedCharacter";
                counts[category]++;
            }
            return "SABA609Q1 MO2 enum: "+string.Join("; ",categories.Select(x=>x+"="+counts[x].ToString(CultureInfo.InvariantCulture)));
        }
        static bool CreditKind(string kind) {
            return kind=="유통융자"||kind=="자기융자"||kind=="유통대주"||kind=="자기대주"||
                kind=="차입매도"||kind=="매입자금"||kind=="예탁담보"||kind=="차입";
        }
        static string CashKindDiagnostic(string kind) {
            if(kind==null)return "Unavailable";if(kind=="")return "Blank";
            if(kind=="보통")return "Ordinary";if(CreditKind(kind))return "Credit";
            if(kind.Length<=2&&kind.All(c=>c>='0'&&c<='9'))return "Numeric"+kind.Length;
            return "Unknown";
        }
        static string LoanDiagnostic(string raw,decimal? value) {
            if(raw==null)return "Unavailable";if(raw=="")return "Blank";
            return value.HasValue?(value.Value==0?"Zero":"Positive"):"Malformed";
        }
        public static BrokerHoldingsSnapshot ReadHoldings(IIndiTransport io,DateTime receivedUtc,bool tradeBasisRequested=false,bool ordinaryOnlyRequested=false) {
            if(io==null)throw new ArgumentNullException("io");Utc(receivedUtc);
            const string query="SABA609Q1";
            var result=new BrokerHoldingsSnapshot {ReceivedUtc=receivedUtc,BrokerDate="",RequestBasisVerified=tradeBasisRequested,RequestOrdinaryOnlyVerified=ordinaryOnlyRequested};
            for(int row=0,count=Rows(io,query);row<count;row++) {
                var code=Rules.Code(Multi(io,row,0,query));
                if(code=="")throw new AccountQueryException(query+" invalid stock symbol multi[0].");
                if(result.SellableQuantities.ContainsKey(code))
                    throw new AccountQueryException(query+" duplicate stock symbol multi[0]; cash/credit rows cannot be merged safely.");
                var basis=OptionalProofMulti(io,row,2,query);
                var explicitNonTrade=basis=="2"||basis=="3"||basis=="4"||basis=="5";
                var basisValidated=tradeBasisRequested&&!explicitNonTrade;
                var kind=OptionalProofMulti(io,row,3,query);
                var quantity=Quantity(Multi(io,row,4,query),query+" holding quantity[4]");
                // These fields are unsettled trades and settlement-day stock,
                // not open buy/sell orders. They are not consumed, and optional
                // blank settlement metadata must not discard valid positions.
                var sellable=Quantity(Multi(io,row,10,query),query+" orderable quantity[10]");
                var unfilled=Quantity(Multi(io,row,11,query),query+" unfilled quantity[11]");
                if(sellable>quantity)
                    throw new AccountQueryException(query+" orderable quantity exceeds trade-basis holdings multi[4/10].");
                var loanRaw=OptionalProofMulti(io,row,22,query);
                var loan=OptionalProofNumber(loanRaw,query+" loan amount[22]");
                // The official description documents the literal '보통'. No
                // undocumented numeric alias is assumed to mean cash stock.
                var cash=basisValidated&&kind=="보통"&&loan.HasValue&&loan.Value==0;
                result.Holdings.Add(new Holding {Code=code,Name=Text(Multi(io,row,1,query)),Quantity=quantity,
                    AvgPrice=Number(Multi(io,row,12,query),query+" average price[12]"),
                    // XTR binds this field to TRDPRC_1, like the other quote
                    // parsers: the encoded sign is not a negative share price.
                    Price=Math.Abs(Number(Multi(io,row,13,query),query+" current price[13]",true)),
                    CreditQty=CreditKind(kind)||(loan.HasValue&&loan.Value>0)?quantity:0m});
                result.BasisValidated.Add(code,basisValidated);
                result.BasisDiagnostics.Add(code,BasisDiagnostic(basis));
                result.BasisValueDiagnostics.Add(code,BasisValueDiagnostic(basis));
                result.SellableQuantities.Add(code,(int)sellable);
                result.UnfilledQuantities.Add(code,(int)unfilled);
                result.CashSourceValidated.Add(code,cash);
                result.CashProfileCompatible.Add(code,!CreditKind(kind)&&(string.IsNullOrEmpty(loanRaw)||loan.HasValue&&loan.Value==0));
                result.CashProfileDiagnostics.Add(code,"Kind="+CashKindDiagnostic(kind)+";Loan="+LoanDiagnostic(loanRaw,loan));
            }
            // The callback must be tied to the documented trade-basis request,
            // even if rows echo '1'. Missing/unknown echoes are not promoted to
            // that enum. Explicit nontrade metadata remains a contradiction.
            result.BasisVerified=tradeBasisRequested&&result.BasisValidated.Values.All(x=>x);
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
        static bool RecentHoldings(BrokerHoldingsSnapshot value,DateTime utc,int maxAgeSeconds) {
            return value!=null&&value.RequestStartedUtc.Kind==DateTimeKind.Utc&&value.ReceivedUtc.Kind==DateTimeKind.Utc
                &&value.RequestStartedUtc<=value.ReceivedUtc&&value.ReceivedUtc<=utc
                &&utc-value.ReceivedUtc<=TimeSpan.FromSeconds(maxAgeSeconds)
                &&MarketTime.ToKorea(value.ReceivedUtc).Date==MarketTime.ToKorea(utc).Date;
        }
        // SI3=5 establishes selection of ordinary positions, not trade quantity.
        // Actual quantities always remain those of the separate SI3=1 response.
        public static bool IsOrdinaryCashSource(BrokerHoldingsSnapshot trade,BrokerHoldingsSnapshot ordinary,string code,DateTime utc,int maxAgeSeconds=120) {
            Utc(utc);if(maxAgeSeconds<0||maxAgeSeconds>120)throw new ArgumentOutOfRangeException("maxAgeSeconds");
            if(Rules.Code(code)!=code||string.IsNullOrEmpty(code)||!RecentHoldings(trade,utc,maxAgeSeconds)
                ||!trade.RequestBasisVerified||!trade.BasisVerified)return false;
            bool basis;if(!trade.BasisValidated.TryGetValue(code,out basis)||!basis)return false;
            var position=trade.Holdings.SingleOrDefault(x=>x.Code==code);
            if(position==null||position.Quantity<=0||position.AvgPrice<=0||position.CreditQty!=0)return false;
            bool direct;if(trade.CashSourceValidated.TryGetValue(code,out direct)&&direct)return true;
            if(!RecentHoldings(ordinary,utc,maxAgeSeconds)||!ordinary.RequestOrdinaryOnlyVerified)return false;
            bool compatible;
            if(!trade.CashProfileCompatible.TryGetValue(code,out compatible)||!compatible
                ||!ordinary.CashProfileCompatible.TryGetValue(code,out compatible)||!compatible)return false;
            string category;
            if(ordinary.BasisDiagnostics.TryGetValue(code,out category)&&(category=="CreditBalanceCode3"||category=="BondBalanceCode4"))return false;
            var selected=ordinary.Holdings.SingleOrDefault(x=>x.Code==code);
            int tradeQty,ordinaryQty,tradePending,ordinaryPending;
            return selected!=null&&selected.CreditQty==0&&selected.Quantity==position.Quantity&&selected.AvgPrice==position.AvgPrice
                &&trade.SellableQuantities.TryGetValue(code,out tradeQty)&&ordinary.SellableQuantities.TryGetValue(code,out ordinaryQty)&&tradeQty==ordinaryQty
                &&trade.UnfilledQuantities.TryGetValue(code,out tradePending)&&ordinary.UnfilledQuantities.TryGetValue(code,out ordinaryPending)&&tradePending==ordinaryPending;
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
        public static BrokerOrdersSnapshot ReadOrders(IIndiTransport io,string tradingDate,DateTime receivedUtc,
            BrokerOrderQueryProfile queryProfile=BrokerOrderQueryProfile.Unverified,string querySchemaSha256=null) {
            if(io==null)throw new ArgumentNullException("io");Utc(receivedUtc);Date(tradingDate);
            if(!Enum.IsDefined(typeof(BrokerOrderQueryProfile),queryProfile))throw new ArgumentException("Unknown broker order query profile.","queryProfile");
            if(queryProfile==BrokerOrderQueryProfile.ModernOrderState54&&!BrokerOrderRow.SchemaHashValid(querySchemaSha256))
                throw new ArgumentException("A recognized modern query definition digest is required.","querySchemaSha256");
            const string query="SABA231Q1";
            var result=new BrokerOrdersSnapshot {TradingDate=tradingDate,ReceivedUtc=receivedUtc};
            var seen=new HashSet<string>(StringComparer.Ordinal);
            for(int row=0,count=Rows(io,query);row<count;row++) {
                var id=OrderId(Multi(io,row,0,query));
                if(!seen.Add(id))throw new AccountQueryException("SABA231Q1 duplicate aggregate order identifier multi[0].");
                var code=Rules.Code(Multi(io,row,13,query));var side=Multi(io,row,4,query);
                if(code=="")throw new AccountQueryException("SABA231Q1 invalid stock symbol multi[13].");
                if(side!="1"&&side!="2")throw new AccountQueryException("SABA231Q1 invalid stock side multi[4].");
                var item=new BrokerOrderRow {OrderNumber=id,OriginalOrderNumber=OrderId(Multi(io,row,1,query),true),QueryProfile=queryProfile,
                    QuerySchemaSha256=queryProfile==BrokerOrderQueryProfile.ModernOrderState54?querySchemaSha256.ToLowerInvariant():null,
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
                if(queryProfile==BrokerOrderQueryProfile.ModernOrderState54) {
                    item.OrderMethodCode=Text(Multi(io,row,41,query));item.SorOrderNumber=Text(Multi(io,row,47,query));
                    item.SorOriginalOrderNumber=Text(Multi(io,row,48,query));
                }
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
