using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Serialization;

namespace ShinhanNativeTrader {
    public enum LiveOrderKind { Buy, Sell, Amend, Cancel }
    public enum LiveOrderStatus { Prepared, Dispatching, AwaitingAcknowledgement, Working, PartiallyFilled, AwaitingConfirmation, Filled, Cancelled, Rejected, Replaced, Unknown, NotSent }
    // Only a transport that has not entered RequestData may produce this proof.
    // It contains no provider exception, credentials or request field values.
    public sealed class LiveOrderNotSentException : InvalidOperationException {
        internal LiveOrderNotSentException():base("주문 준비 또는 조건 확인 중 중단되어 증권사로 전송되지 않았습니다. 상태를 새로 확인하십시오."){}
    }

    public sealed class LiveOrderIntent {
        public string ClientKey, Code, OriginalBrokerOrderNumber;
        public LiveOrderKind Kind;
        public int Quantity;
        public decimal LimitPrice;
    }
    // Memory only. These values are never written to the order journal or traces.
    public sealed class LiveAccountCredentials {
        public readonly string Account, Password;
        public LiveAccountCredentials(string account,string password) {
            if(account==null||account.Length!=11||account.Any(c=>c<'0'||c>'9')||string.IsNullOrEmpty(password))
                throw new ArgumentException("실계좌번호와 계좌암호를 확인하십시오.");
            Account=account;Password=password;
        }
    }
    // Explicit user approval for one account in this process. This token says
    // nothing about broker readiness; every submission still validates live data.
    // No account number, password or activation state is serialized to disk.
    public sealed class LiveTradingSession {
        readonly object sync=new object();
        readonly string salt=Guid.NewGuid().ToString("N");
        string accountHash;DateTime? authorizedUtc,validUntilUtc,tradingDateKorea;bool active;
        public bool Active {get{lock(sync)return active;}}
        public DateTime? AuthorizedUtc {get{lock(sync)return authorizedUtc;}}
        public DateTime? ValidUntilUtc {get{lock(sync)return validUntilUtc;}}
        public DateTime? TradingDateKorea {get{lock(sync)return tradingDateKorea;}}
        public void Authorize(string account,DateTime utc) {
            LiveOrderSafety.RequireUtc(utc);
            if(!ValidAccount(account))throw new ArgumentException("실주문 운영을 시작할 계좌번호를 확인하십시오.");
            var hash=HashAccount(account);
            var korea=MarketTime.ToKorea(utc);var sessionDate=korea.Date;
            if(korea.TimeOfDay>=new TimeSpan(15,20,0))sessionDate=sessionDate.AddDays(1);
            while(sessionDate.DayOfWeek==DayOfWeek.Saturday||sessionDate.DayOfWeek==DayOfWeek.Sunday)sessionDate=sessionDate.AddDays(1);
            var expiry=DateTime.SpecifyKind(sessionDate.AddHours(15).AddMinutes(20).AddHours(-9),DateTimeKind.Utc);
            lock(sync){accountHash=hash;authorizedUtc=utc;validUntilUtc=expiry;tradingDateKorea=sessionDate;active=true;}
        }
        public void Revoke(){lock(sync){active=false;accountHash=null;authorizedUtc=null;validUntilUtc=null;tradingDateKorea=null;}}
        public bool MatchesAccount(string account) {
            if(!ValidAccount(account))return false;
            var hash=HashAccount(account);
            lock(sync)return active&&accountHash==hash;
        }
        internal bool Allows(string account,DateTime utc) {
            if(!ValidAccount(account))return false;
            var hash=HashAccount(account);
            lock(sync)return active&&accountHash==hash&&authorizedUtc.HasValue&&utc>=authorizedUtc.Value&&validUntilUtc.HasValue&&utc<validUntilUtc.Value
                &&tradingDateKorea.HasValue&&MarketTime.ToKorea(utc).Date==tradingDateKorea.Value;
        }
        static bool ValidAccount(string account){return account!=null&&account.Length==11&&account.All(c=>c>='0'&&c<='9');}
        string HashAccount(string account){using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(salt+":"+account)));}
    }
    public sealed class LiveOrderPreflight {
        [XmlIgnore] public LiveTradingSession Authorization;
        [XmlIgnore] public Func<bool> EvidenceStillCurrent;
        public bool AccountAuthorized, OrderableAmountVerified, SellableQuantityVerified, OrdersReconciled, FillsReconciled, RecoveryVerified, PriceRuleVerified, QuoteVerified,OriginalOrderVerified;
        public LiveOrderKind OriginalTradeKind;
        public decimal Cash100Orderable, CapacityAmount, OperatingLimit;
        public int CapacityQuantity, SellableQuantity;
        public DateTime SnapshotUtc, QuoteUtc;
    }
    public static class LiveOrderSafety {
        public const string BlockReason="실주문 운영 중지: 이 계좌의 실주문 운영 시작 승인이 필요합니다.";
        public static void EnsureAllowed(LiveOrderPreflight checks,LiveAccountCredentials credentials,DateTime utc) {
            RequireUtc(utc);
            if(checks==null||credentials==null||checks.Authorization==null||!checks.Authorization.Allows(credentials.Account,utc))
                throw new InvalidOperationException(BlockReason);
        }
        public static void ValidatePreflight(LiveOrderIntent intent,LiveOrderPreflight checks,DateTime utc,DateTime lastBrokerChangeUtc,decimal reservedBuyAmount=0) {
            LiveOrderProtocol.ValidateIntent(intent);RequireUtc(utc);
            if(checks==null||!checks.AccountAuthorized||!checks.OrdersReconciled||!checks.FillsReconciled||!checks.RecoveryVerified)
                throw new InvalidOperationException("실계좌 승인 및 주문·체결·복구 대사가 필요합니다.");
            bool current=false;
            try{current=checks.EvidenceStillCurrent!=null&&checks.EvidenceStillCurrent();}catch{ /* Provider/runtime details must not escape a proof callback. */ }
            if(!current)throw new InvalidOperationException("실계좌·시세 조회 증거가 변경됐거나 현재 상태를 확인할 수 없습니다. 재조회가 필요합니다.");
            RequireUtc(checks.SnapshotUtc);RequireUtc(checks.QuoteUtc);
            var korea=MarketTime.ToKorea(utc);
            bool session=intent.Kind==LiveOrderKind.Cancel?korea.DayOfWeek!=DayOfWeek.Saturday&&korea.DayOfWeek!=DayOfWeek.Sunday&&korea.TimeOfDay>=TimeSpan.FromHours(9)&&korea.TimeOfDay<new TimeSpan(15,30,0):Rules.ValidTime(korea);
            if(!session||checks.SnapshotUtc>utc||utc-checks.SnapshotUtc>TimeSpan.FromSeconds(5)||checks.SnapshotUtc<lastBrokerChangeUtc)
                throw new InvalidOperationException("장 운영시간 또는 실계좌·시세 조회 기준시각 검증 실패.");
            if(intent.Kind!=LiveOrderKind.Cancel&&(!checks.PriceRuleVerified||!checks.QuoteVerified||checks.QuoteUtc>utc||utc-checks.QuoteUtc>TimeSpan.FromSeconds(45)))throw new InvalidOperationException("가격·호가단위·시세 검증이 필요합니다.");
            var effective=intent.Kind;
            if(intent.Kind==LiveOrderKind.Amend||intent.Kind==LiveOrderKind.Cancel) {
                if(!checks.OriginalOrderVerified||(checks.OriginalTradeKind!=LiveOrderKind.Buy&&checks.OriginalTradeKind!=LiveOrderKind.Sell))throw new InvalidOperationException("정정·취소 원주문의 방향·미체결 수량 대사가 필요합니다.");
                effective=intent.Kind==LiveOrderKind.Cancel?LiveOrderKind.Cancel:checks.OriginalTradeKind;
            }
            if(effective==LiveOrderKind.Buy) {
                if(!checks.OrderableAmountVerified||checks.Cash100Orderable<0||checks.CapacityAmount<0||checks.OperatingLimit<=0||checks.CapacityQuantity<0||reservedBuyAmount<0)
                    throw new InvalidOperationException("현금100% 주문가능금액 검증이 필요합니다.");
                var amount=intent.Quantity*intent.LimitPrice;
                if(intent.Quantity>checks.CapacityQuantity||amount>checks.Cash100Orderable||amount>checks.CapacityAmount||amount>checks.OperatingLimit-reservedBuyAmount)
                    throw new InvalidOperationException("실계좌 주문가능금액·수량 또는 운용한도를 초과합니다.");
            } else if(effective==LiveOrderKind.Sell&&(!checks.SellableQuantityVerified||intent.Quantity>checks.SellableQuantity))
                throw new InvalidOperationException("실계좌 매도가능수량 검증이 필요합니다.");
        }
        internal static void RequireUtc(DateTime utc){if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC timestamp required.");}
    }
    public static class LiveOrderProtocol {
        public static void ValidateIntent(LiveOrderIntent intent) {
            if(intent==null||string.IsNullOrWhiteSpace(intent.ClientKey)||intent.ClientKey.Length>128||string.IsNullOrEmpty(intent.Code)
                ||Rules.Code(intent.Code)!=intent.Code||!Enum.IsDefined(typeof(LiveOrderKind),intent.Kind)||intent.Quantity<=0||intent.Quantity>1000000
                ||intent.LimitPrice<=0||intent.LimitPrice!=decimal.Truncate(intent.LimitPrice))
                throw new ArgumentException("유효한 실계좌 지정가 주문의 종목·수량·가격·중복방지 키가 필요합니다.");
            if((intent.Kind==LiveOrderKind.Amend||intent.Kind==LiveOrderKind.Cancel)&&!ValidBrokerNumber(intent.OriginalBrokerOrderNumber))
                throw new ArgumentException("정정·취소에는 확인된 원주문번호가 필요합니다.");
            if((intent.Kind==LiveOrderKind.Buy||intent.Kind==LiveOrderKind.Sell)&&!string.IsNullOrEmpty(intent.OriginalBrokerOrderNumber))
                throw new ArgumentException("신규 주문에 원주문번호를 지정할 수 없습니다.");
        }
        public static bool ValidBrokerNumber(string value) {
            return !string.IsNullOrEmpty(value)&&value.Length<=32&&value.All(c=>c>='0'&&c<='9')&&value.Any(c=>c!='0');
        }
        public static string BrokerNumberKey(string value){return ValidBrokerNumber(value)?value.TrimStart('0'):null;}
        public static bool BrokerNumbersEqual(string first,string second){var key=BrokerNumberKey(first);return key!=null&&key==BrokerNumberKey(second);}
        public static string QueryName(LiveOrderIntent intent) {ValidateIntent(intent);return intent.Kind==LiveOrderKind.Buy||intent.Kind==LiveOrderKind.Sell?"SABA101U1":"SABA102U1";}
        public static SortedDictionary<int,string> BuildFields(LiveOrderIntent intent,LiveAccountCredentials credentials) {
            ValidateIntent(intent);if(credentials==null)throw new ArgumentNullException("credentials");
            bool original=intent.Kind==LiveOrderKind.Amend||intent.Kind==LiveOrderKind.Cancel;
            var result=new SortedDictionary<int,string> {
                {0,credentials.Account},{1,"01"},{2,credentials.Password},{3,""},{4,""},{5,"0"},{6,"00"},
                {7,intent.Kind==LiveOrderKind.Buy?"2":intent.Kind==LiveOrderKind.Sell?"1":intent.Kind==LiveOrderKind.Amend?"3":"4"},
                {8,"A"+intent.Code},{9,intent.Quantity.ToString(CultureInfo.InvariantCulture)},{10,intent.LimitPrice.ToString(CultureInfo.InvariantCulture)},
                {11,"1"},{12,"2"},{13,"0"},{14,original?"":"0"},{15,""},{16,original?intent.OriginalBrokerOrderNumber:""}
            };
            // Only documented regular cash-limit inputs are emitted. Additional
            // XTR defaults and current market routing require broker validation;
            // authorization is not a claim that static metadata proves live behavior.
            if(!original)result[21]="Y";
            return result;
        }
        public static void ApplyInstalledSchema(LiveOrderIntent intent,SortedDictionary<int,string> fields,IndiOrderSchemaInfo schema) {
            ValidateIntent(intent);
            if(fields==null||schema==null||schema.QueryName!=QueryName(intent)||schema.Kind==IndiOrderSchemaKind.Unknown)
                throw new InvalidOperationException("설치된 주문 정의를 확인할 수 없습니다.");
            if(schema.Kind==IndiOrderSchemaKind.Legacy)return;
            if(schema.Kind!=IndiOrderSchemaKind.Modern)throw new InvalidOperationException("지원되지 않는 주문 정의입니다.");
            if(!schema.KrxRouteInputIndex.HasValue||string.IsNullOrEmpty(schema.KrxRouteCode))
                throw new InvalidOperationException("설치된 주문 정의의 KRX 전송 코드가 확인되지 않았습니다.");
            bool original=intent.Kind==LiveOrderKind.Amend||intent.Kind==LiveOrderKind.Cancel;
            int expectedRoute=original?35:37;
            if(schema.KrxRouteInputIndex.Value!=expectedRoute)
                throw new InvalidOperationException("설치된 주문 방식 입력 위치가 일치하지 않습니다.");
            fields[expectedRoute]=schema.KrxRouteCode;
            // Actual 0x0208 definitions include conditional price and SOR
            // original identity. Ordinary KRX limit orders use neither field.
            if(!original&&schema.InputCount==39){fields[36]="";fields[38]="";}
            else if(original&&schema.InputCount==38){fields[36]="";fields[37]="";}
        }
    }

    public sealed class LiveOrderRecord {
        public string Id,ClientKey,OriginalClientKey,Code,BrokerOrderNumber,OriginalBrokerOrderNumber,TradingDate;
        public LiveOrderKind Kind,TradeKind;
        public LiveOrderStatus Status;
        public int Quantity,FilledQuantity,CancelledQuantity;
        public decimal LimitPrice;
        public int? RequestId;
        public DateTime CreatedUtc,UpdatedUtc;
        public OrderResponseEvidence ResponseEvidence;
    }
    public sealed class LiveExecutionRecord {
        public string ExecutionId,OrderId,BrokerOrderNumber,TradingDate;
        public int Quantity;
        public decimal Price;
        public DateTime ExecutedUtc;
    }
    public sealed class LiveRecoveryOrderEvidence {
        public string AccountFingerprint,BrokerOrderNumber,TradingDate,Code,OriginalBrokerOrderNumber;
        public LiveOrderKind Kind,TradeKind;
        public int Quantity;
        public decimal LimitPrice;
        public DateTime ReceivedUtc;
        public bool AccountMatched,IdentityProven,SnapshotComplete;
    }
    public sealed class LiveOrderJournalState {
        public int Version=1;
        public string Mode="RealAccount";
        public string AccountSalt=Guid.NewGuid().ToString("N"),AccountFingerprint;
        public DateTime LastBrokerChangeUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        public List<LiveOrderRecord> Orders=new List<LiveOrderRecord>();
        public List<LiveExecutionRecord> Executions=new List<LiveExecutionRecord>();
        public List<string> ConfirmationIds=new List<string>();
    }
    // This is a journal of broker evidence, not an execution simulator. Submission,
    // acknowledgement, execution and cancellation are distinct durable transitions.
    public sealed class LiveOrderJournal : IDisposable {
        static readonly XmlSerializer Serializer=new XmlSerializer(typeof(LiveOrderJournalState));
        readonly string path;readonly FileStream lease;readonly object sync=new object();
        LiveOrderJournalState state;bool disposed,storageFault;
        public LiveOrderJournal(string journalPath) {
            if(string.IsNullOrWhiteSpace(journalPath))throw new ArgumentException("A durable live-order journal path is required.");
            path=Path.GetFullPath(journalPath);Directory.CreateDirectory(Path.GetDirectoryName(path));
            try {
                lease=new FileStream(path+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                if(File.Exists(path)){using(var file=File.OpenRead(path))state=(LiveOrderJournalState)Serializer.Deserialize(file);Validate(state);}
                else{state=new LiveOrderJournalState();Save(state);}
                if(state.Orders.Any(IsUnresolved))Change(copy=>{foreach(var order in copy.Orders.Where(IsUnresolved))order.Status=LiveOrderStatus.Unknown;});
            }catch{if(lease!=null)lease.Dispose();throw;}
        }
        public LiveOrderJournalState Snapshot {get{lock(sync){CheckUsable();return Copy(state);}}}
        public bool RecoveryRequired {get{lock(sync){CheckUsable();return state.Orders.Any(o=>o.Status==LiveOrderStatus.Unknown);}}}
        public bool StorageFault {get{return storageFault;}}
        public decimal ReservedBuyAmount {get{lock(sync){CheckUsable();return state.Orders.Where(o=>IsUnresolved(o)&&o.TradeKind==LiveOrderKind.Buy&&o.Kind!=LiveOrderKind.Cancel)
            .Sum(o=>o.Kind==LiveOrderKind.Amend&&o.Status!=LiveOrderStatus.Working&&o.Status!=LiveOrderStatus.PartiallyFilled&&o.Status!=LiveOrderStatus.Unknown
                ?Math.Max(0,Remaining(o)*o.LimitPrice-state.Orders.Where(p=>p.TradingDate==o.TradingDate&&LiveOrderProtocol.BrokerNumbersEqual(p.BrokerOrderNumber,o.OriginalBrokerOrderNumber)&&IsUnresolved(p)).Sum(p=>Remaining(p)*p.LimitPrice))
                :Remaining(o)*o.LimitPrice);}}}
        public void BindAccount(string account) {
            if(account==null||account.Length!=11||account.Any(c=>c<'0'||c>'9'))throw new ArgumentException("실계좌번호 형식을 확인하십시오.");
            Change(copy=>{
                string fingerprint;
                using(var hash=SHA256.Create())fingerprint=Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(copy.AccountSalt+":"+account)));
                if(string.IsNullOrEmpty(copy.AccountFingerprint))copy.AccountFingerprint=fingerprint;
                else if(copy.AccountFingerprint!=fingerprint)throw new InvalidOperationException("현재 실계좌와 주문 기록의 계좌가 다릅니다.");
            });
        }
        public static bool IsUnresolved(LiveOrderRecord order) {
            return order.Status!=LiveOrderStatus.Filled&&order.Status!=LiveOrderStatus.Cancelled&&order.Status!=LiveOrderStatus.Rejected&&order.Status!=LiveOrderStatus.Replaced&&order.Status!=LiveOrderStatus.NotSent;
        }
        public LiveOrderRecord Prepare(LiveOrderIntent intent,DateTime utc) {
            LiveOrderProtocol.ValidateIntent(intent);LiveOrderSafety.RequireUtc(utc);string id=Guid.NewGuid().ToString("N");
            Change(copy=>{
                if(copy.Orders.Any(o=>o.Status==LiveOrderStatus.Unknown))throw new InvalidOperationException("불명 주문의 증권사 대사가 필요합니다.");
                if(copy.Orders.Any(o=>o.ClientKey==intent.ClientKey))throw new InvalidOperationException("동일 중복방지 키의 주문이 이미 기록됐습니다.");
                var tradeKind=intent.Kind;
                if(intent.Kind==LiveOrderKind.Buy||intent.Kind==LiveOrderKind.Sell) {
                    if(copy.Orders.Any(o=>o.Code==intent.Code&&IsUnresolved(o)))throw new InvalidOperationException("동일 종목의 미완료 주문이 있습니다.");
                } else {
                    var original=FindBroker(copy,intent.OriginalBrokerOrderNumber,TradeDate(utc));
                    if(original.Code!=intent.Code||!IsWorking(original)||intent.Quantity>Remaining(original))throw new InvalidOperationException("원주문의 종목·미체결 수량·상태 검증 실패.");
                    if(intent.Kind==LiveOrderKind.Amend&&intent.Quantity!=Remaining(original))throw new InvalidOperationException("현재 정정 코드는 원주문 잔여 전량 정정만 지원합니다.");
                    tradeKind=original.TradeKind;
                    if(copy.Orders.Any(o=>o.TradingDate==original.TradingDate&&LiveOrderProtocol.BrokerNumbersEqual(o.OriginalBrokerOrderNumber,original.BrokerOrderNumber)&&IsUnresolved(o)))throw new InvalidOperationException("원주문의 정정·취소가 이미 진행 중입니다.");
                }
                copy.Orders.Add(new LiveOrderRecord{Id=id,ClientKey=intent.ClientKey,Code=intent.Code,Kind=intent.Kind,TradeKind=tradeKind,TradingDate=TradeDate(utc),
                    OriginalBrokerOrderNumber=intent.OriginalBrokerOrderNumber,Quantity=intent.Quantity,LimitPrice=intent.LimitPrice,
                    Status=LiveOrderStatus.Prepared,CreatedUtc=utc,UpdatedUtc=utc});
            });return Snapshot.Orders.Single(o=>o.Id==id);
        }
        public void MarkDispatching(string id,DateTime utc){Update(id,utc,order=>{if(order.Status!=LiveOrderStatus.Prepared)throw new InvalidOperationException("주문은 한 번만 전송할 수 있습니다.");order.Status=LiveOrderStatus.Dispatching;});}
        public void MarkRequestAccepted(string id,int requestId,DateTime utc) {
            if(requestId<=0)throw new ArgumentException("A positive broker request ID is required.");
            Update(id,utc,order=>{if(order.Status!=LiveOrderStatus.Dispatching)throw new InvalidOperationException("주문 전송 상태가 아닙니다.");order.RequestId=requestId;order.Status=LiveOrderStatus.AwaitingAcknowledgement;});
        }
        public void MarkUnknown(string id,DateTime utc){Update(id,utc,order=>{if(IsUnresolved(order))order.Status=LiveOrderStatus.Unknown;});}
        internal void RecordNotSent(string id,DateTime utc) {
            Update(id,utc,order=>{
                if((order.Status!=LiveOrderStatus.Dispatching&&order.Status!=LiveOrderStatus.Prepared)||order.RequestId.HasValue||!string.IsNullOrEmpty(order.BrokerOrderNumber)
                    ||order.FilledQuantity!=0||order.CancelledQuantity!=0)throw new InvalidOperationException("전송되지 않았다는 증명이 현재 주문 상태와 일치하지 않습니다.");
                order.OriginalClientKey=order.ClientKey;
                // Keep a durable audit record without consuming the strategy key.
                // Only this proven pre-RequestData state can free that original key.
                order.ClientKey="not-sent/"+order.Id;order.Status=LiveOrderStatus.NotSent;
            });
        }
        public void MarkBrokerEvidenceUnknown(string id,DateTime utc) {
            Update(id,utc,order=>{
                if(!LiveOrderProtocol.ValidBrokerNumber(order.BrokerOrderNumber))throw new InvalidOperationException("증권사 주문번호와 일치하는 상충 증거가 필요합니다.");
                // Contradictory broker facts invalidate even a previously terminal
                // order. They cannot be dismissed merely because local state closed.
                order.Status=LiveOrderStatus.Unknown;
            },true);
        }
        public void RecordAcknowledgement(string id,int requestId,string brokerOrderNumber,bool providerError,DateTime utc,OrderResponseEvidence evidence=null) {
            Update(id,utc,order=>{
                if(order.Status!=LiveOrderStatus.AwaitingAcknowledgement||order.RequestId!=requestId)throw new InvalidOperationException("주문 접수 응답의 요청번호가 다릅니다.");
                if(evidence!=null) {
                    ValidateResponse(evidence,requestId);
                    order.ResponseEvidence=CopyResponse(evidence);
                    // The identified response layout, not SO0 alone, decides whether
                    // a zero SOR ID represents rejection or a separate KRX order.
                    if(evidence.Disposition==OrderResponseDisposition.Unknown){order.Status=LiveOrderStatus.Unknown;return;}
                    if(evidence.Disposition==OrderResponseDisposition.Rejected){order.Status=LiveOrderStatus.Rejected;return;}
                    brokerOrderNumber=evidence.BrokerOrderNumber;providerError=false;
                }
                // Legacy callers supply an explicitly identified legacy response.
                // Indi can report ErrorState0 with order number0. That is rejection.
                bool positive=LiveOrderProtocol.ValidBrokerNumber(brokerOrderNumber);
                bool zero=!string.IsNullOrEmpty(brokerOrderNumber)&&brokerOrderNumber.All(c=>c=='0');
                if(providerError&&!positive||zero){order.Status=LiveOrderStatus.Rejected;return;}
                if(!positive){order.Status=LiveOrderStatus.Unknown;return;}
                if(state.Orders.Any(o=>o.Id!=id&&o.TradingDate==order.TradingDate&&LiveOrderProtocol.BrokerNumbersEqual(o.BrokerOrderNumber,brokerOrderNumber)))throw new InvalidOperationException("증권사 주문번호가 중복됐습니다.");
                order.BrokerOrderNumber=brokerOrderNumber;
                order.Status=providerError?LiveOrderStatus.Unknown:order.Kind==LiveOrderKind.Amend||order.Kind==LiveOrderKind.Cancel?LiveOrderStatus.AwaitingConfirmation:LiveOrderStatus.Working;
            },true);
        }
        static OrderResponseEvidence CopyResponse(OrderResponseEvidence value) {
            return new OrderResponseEvidence{RequestId=value.RequestId,Layout=value.Layout,Disposition=value.Disposition,
                BrokerOrderNumber=value.BrokerOrderNumber,Slot0OrderNumber=value.Slot0OrderNumber,Slot1OrderNumber=value.Slot1OrderNumber,
                Slot2OrderNumber=value.Slot2OrderNumber,Slot3OrderNumber=value.Slot3OrderNumber,ProviderError=value.ProviderError,
                ParseFailed=value.ParseFailed,Message1Number=value.Message1Number,Message2Number=value.Message2Number,
                Message3Number=value.Message3Number,SafeDiagnostic=value.SafeDiagnostic};
        }
        static bool ResponseSlot(string value){return value!=null&&(value.Length==0||value.Length<=32&&value.All(c=>c>='0'&&c<='9'));}
        static bool ResponseZero(string value){return !string.IsNullOrEmpty(value)&&value.All(c=>c=='0');}
        static void ValidateResponse(OrderResponseEvidence value,int requestId) {
            if(value==null||value.RequestId<=0||value.RequestId!=requestId||!Enum.IsDefined(typeof(OrderResponseLayout),value.Layout)
                ||!Enum.IsDefined(typeof(OrderResponseDisposition),value.Disposition)||value.SafeDiagnostic==null
                ||value.SafeDiagnostic.Length>IndiDiagnosticFormatter.MaximumLength
                ||value.SafeDiagnostic.Any(c=>char.IsControl(c)||char.GetUnicodeCategory(c)==UnicodeCategory.Format)
                ||!ResponseSlot(value.Slot0OrderNumber)||!ResponseSlot(value.Slot1OrderNumber)||!ResponseSlot(value.Slot2OrderNumber)||!ResponseSlot(value.Slot3OrderNumber)
                ||value.BrokerOrderNumber==null||value.BrokerOrderNumber.Length>0&&!LiveOrderProtocol.ValidBrokerNumber(value.BrokerOrderNumber))
                throw new InvalidDataException("지원하지 않는 주문 응답 증거입니다.");
            if(value.Layout==OrderResponseLayout.Unknown&&value.Disposition!=OrderResponseDisposition.Unknown)
                throw new InvalidDataException("미확인 응답 형식은 접수 또는 거절 근거가 아닙니다.");
            var ids=value.Layout==OrderResponseLayout.Legacy?new[]{value.Slot0OrderNumber}:new[]{value.Slot0OrderNumber,value.Slot1OrderNumber,value.Slot2OrderNumber};
            var positive=ids.Where(LiveOrderProtocol.ValidBrokerNumber).Select(LiveOrderProtocol.BrokerNumberKey).Distinct(StringComparer.Ordinal).ToArray();
            if(value.Disposition==OrderResponseDisposition.Accepted&&(value.ProviderError||value.ParseFailed
                ||positive.Length!=1||positive[0]!=LiveOrderProtocol.BrokerNumberKey(value.BrokerOrderNumber)))
                throw new InvalidDataException("주문 접수번호와 응답 근거가 일치하지 않습니다.");
            if(value.Disposition==OrderResponseDisposition.Rejected&&(value.ParseFailed||value.BrokerOrderNumber.Length!=0
                ||positive.Length!=0||!(ids.All(ResponseZero)||value.ProviderError)))
                throw new InvalidDataException("거절 응답 근거가 일치하지 않습니다.");
            if(value.Disposition==OrderResponseDisposition.Unknown&&value.BrokerOrderNumber.Length!=0)
                throw new InvalidDataException("미확인 응답의 후보 번호는 접수번호가 아닙니다.");
        }
        public bool RecordExecution(string brokerOrderNumber,string executionId,int quantity,decimal price,DateTime utc) {
            return RecordExecutionCore(null,brokerOrderNumber,executionId,quantity,price,utc,false);
        }
        public bool ImportRecoveryExecution(string orderId,string brokerOrderNumber,string executionId,int quantity,decimal price,DateTime utc) {
            if(string.IsNullOrWhiteSpace(orderId))throw new ArgumentException("복구 대상 주문 식별자가 필요합니다.");
            return RecordExecutionCore(orderId,brokerOrderNumber,executionId,quantity,price,utc,true);
        }
        bool RecordExecutionCore(string orderId,string brokerOrderNumber,string executionId,int quantity,decimal price,DateTime utc,bool recovery) {
            LiveOrderSafety.RequireUtc(utc);if(string.IsNullOrWhiteSpace(executionId)||quantity<=0||price<=0)throw new ArgumentException("증권사 체결번호·양수 체결수량·체결가격이 필요합니다.");
            bool applied=false;
            Change(copy=>{
                var order=recovery?Find(copy,orderId):FindBroker(copy,brokerOrderNumber,TradeDate(utc));
                if(!LiveOrderProtocol.BrokerNumbersEqual(order.BrokerOrderNumber,brokerOrderNumber))throw new InvalidOperationException("복구 체결의 증권사 주문번호가 일치하지 않습니다.");
                var previous=copy.Executions.SingleOrDefault(f=>f.OrderId==order.Id&&f.ExecutionId==executionId);
                if(previous!=null) {
                    if(!LiveOrderProtocol.BrokerNumbersEqual(previous.BrokerOrderNumber,brokerOrderNumber)||previous.Quantity!=quantity||previous.Price!=price)throw new InvalidOperationException("동일 체결번호의 내용이 다릅니다.");
                    return;
                }
                if(order.Kind==LiveOrderKind.Cancel||order.Kind==LiveOrderKind.Amend&&order.Status==LiveOrderStatus.AwaitingConfirmation)throw new InvalidOperationException("미확인 정정·취소 요청 자체를 체결로 처리할 수 없습니다.");
                if((recovery?order.Status!=LiveOrderStatus.Unknown:!IsWorking(order))||utc<order.CreatedUtc||quantity>Remaining(order)
                    ||(order.TradeKind==LiveOrderKind.Buy&&price>order.LimitPrice)||(order.TradeKind==LiveOrderKind.Sell&&price<order.LimitPrice))
                    throw new InvalidOperationException("체결의 수량·지정가·주문 상태 검증 실패.");
                order.FilledQuantity+=quantity;order.UpdatedUtc=utc;
                order.Status=recovery?LiveOrderStatus.Unknown:Remaining(order)==0?(order.CancelledQuantity>0?LiveOrderStatus.Cancelled:LiveOrderStatus.Filled):LiveOrderStatus.PartiallyFilled;
                copy.Executions.Add(new LiveExecutionRecord{ExecutionId=executionId,OrderId=order.Id,BrokerOrderNumber=order.BrokerOrderNumber,TradingDate=order.TradingDate,Quantity=quantity,Price=price,ExecutedUtc=utc});
                BrokerChanged(copy,utc);applied=true;
            });return applied;
        }
        public void ConfirmCancellation(string originalBrokerNumber,string cancellationBrokerNumber,string confirmationId,int cancelledQuantity,DateTime utc) {
            LiveOrderSafety.RequireUtc(utc);if(string.IsNullOrWhiteSpace(confirmationId)||cancelledQuantity<=0)throw new ArgumentException("증권사 취소확인 식별자와 취소수량이 필요합니다.");
            Change(copy=>{
                var key=TradeDate(utc)+"|"+LiveOrderProtocol.BrokerNumberKey(cancellationBrokerNumber)+"|"+confirmationId+"|"+cancelledQuantity.ToString(CultureInfo.InvariantCulture);
                if(copy.ConfirmationIds.Contains(key))return;
                if(copy.ConfirmationIds.Any(k=>k.StartsWith(TradeDate(utc)+"|"+LiveOrderProtocol.BrokerNumberKey(cancellationBrokerNumber)+"|"+confirmationId+"|",StringComparison.Ordinal)))throw new InvalidOperationException("동일 취소확인 식별자의 내용이 다릅니다.");
                var original=FindBroker(copy,originalBrokerNumber,TradeDate(utc));var cancellation=FindBroker(copy,cancellationBrokerNumber,TradeDate(utc));
                if(cancellation.Kind!=LiveOrderKind.Cancel||!LiveOrderProtocol.BrokerNumbersEqual(cancellation.OriginalBrokerOrderNumber,originalBrokerNumber)
                    ||cancellation.Status!=LiveOrderStatus.AwaitingConfirmation||!IsWorking(original)||cancelledQuantity>Remaining(original)||cancelledQuantity>cancellation.Quantity||utc<cancellation.CreatedUtc||utc<original.CreatedUtc)
                    throw new InvalidOperationException("증권사 취소확인과 원주문이 일치하지 않습니다.");
                original.CancelledQuantity+=cancelledQuantity;original.UpdatedUtc=utc;
                original.Status=Remaining(original)==0?LiveOrderStatus.Cancelled:(original.FilledQuantity>0?LiveOrderStatus.PartiallyFilled:LiveOrderStatus.Working);
                cancellation.Status=LiveOrderStatus.Cancelled;cancellation.UpdatedUtc=utc;
                copy.ConfirmationIds.Add(key);BrokerChanged(copy,utc);
            });
        }
        public void ConfirmAmendment(string originalBrokerNumber,string amendmentBrokerNumber,string confirmationId,int remainingQuantity,decimal limitPrice,DateTime utc) {
            LiveOrderSafety.RequireUtc(utc);if(string.IsNullOrWhiteSpace(confirmationId)||remainingQuantity<=0||limitPrice<=0)throw new ArgumentException("증권사 정정확인 값이 필요합니다.");
            Change(copy=>{
                var key=TradeDate(utc)+"|"+LiveOrderProtocol.BrokerNumberKey(amendmentBrokerNumber)+"|"+confirmationId+"|"+remainingQuantity.ToString(CultureInfo.InvariantCulture)+"|"+limitPrice.ToString(CultureInfo.InvariantCulture);
                if(copy.ConfirmationIds.Contains(key))return;
                if(copy.ConfirmationIds.Any(k=>k.StartsWith(TradeDate(utc)+"|"+LiveOrderProtocol.BrokerNumberKey(amendmentBrokerNumber)+"|"+confirmationId+"|",StringComparison.Ordinal)))throw new InvalidOperationException("동일 정정확인 식별자의 내용이 다릅니다.");
                var original=FindBroker(copy,originalBrokerNumber,TradeDate(utc));var amendment=FindBroker(copy,amendmentBrokerNumber,TradeDate(utc));
                if(amendment.Kind!=LiveOrderKind.Amend||!LiveOrderProtocol.BrokerNumbersEqual(amendment.OriginalBrokerOrderNumber,originalBrokerNumber)||amendment.Status!=LiveOrderStatus.AwaitingConfirmation
                    ||!IsWorking(original)||remainingQuantity!=Remaining(original)||remainingQuantity!=amendment.Quantity||limitPrice!=amendment.LimitPrice||utc<amendment.CreatedUtc||utc<original.CreatedUtc)
                    throw new InvalidOperationException("증권사 정정확인과 원주문이 일치하지 않습니다. 추가 대사가 필요합니다.");
                original.Status=LiveOrderStatus.Replaced;original.UpdatedUtc=utc;
                amendment.Status=LiveOrderStatus.Working;amendment.UpdatedUtc=utc;
                copy.ConfirmationIds.Add(key);BrokerChanged(copy,utc);
            });
        }
        public void RecordBrokerRejection(string brokerOrderNumber,string tradingDate,string eventKind,DateTime utc) {
            LiveOrderSafety.RequireUtc(utc);
            if(eventKind!="09"&&eventKind!="19"&&eventKind!="98"&&eventKind!="99")throw new ArgumentException("명시적인 증권사 주문 거부 이벤트가 필요합니다.");
            Change(copy=>{
                var order=FindBroker(copy,brokerOrderNumber,tradingDate);
                bool isNew=order.Kind==LiveOrderKind.Buy||order.Kind==LiveOrderKind.Sell;
                if(isNew!=(eventKind=="09"||eventKind=="19")||utc<order.CreatedUtc||order.FilledQuantity!=0)
                    throw new InvalidOperationException("주문 거부 이벤트와 주문 종류·체결 기록이 상충합니다.");
                if(order.Status==LiveOrderStatus.Rejected)return;
                if(order.Status==LiveOrderStatus.Filled||order.Status==LiveOrderStatus.Cancelled||order.Status==LiveOrderStatus.Replaced)
                    throw new InvalidOperationException("종료된 주문과 거부 이벤트가 상충합니다.");
                order.Status=LiveOrderStatus.Rejected;order.UpdatedUtc=utc;BrokerChanged(copy,utc);
                // Only the rejected command closes. In particular a rejected
                // cancellation/amendment never releases the original order.
            });
        }
        // Recovery consumes an authoritative order snapshot; it cannot invent fills.
        // Existing per-execution evidence must exactly explain FilledQuantity before
        // a restarted order can leave Unknown. Missing executions keep it blocked.
        public void BindRecoveryOrder(string id,LiveRecoveryOrderEvidence evidence,DateTime utc) {
            LiveOrderSafety.RequireUtc(utc);
            if(evidence==null||!evidence.AccountMatched||!evidence.IdentityProven||!evidence.SnapshotComplete)throw new InvalidOperationException("실계좌와 주문의 동일성이 증명된 완전한 증권사 조회가 필요합니다.");
            LiveOrderSafety.RequireUtc(evidence.ReceivedUtc);
            if(evidence.ReceivedUtc>utc||utc-evidence.ReceivedUtc>TimeSpan.FromSeconds(5))throw new InvalidOperationException("복구 주문 조회가 오래됐습니다.");
            Change(copy=>{
                var order=Find(copy,id);
                // Matching symbol/amount alone is insufficient: a verified reconciler
                // must prove this is our submission, not a similar manual order.
                if(order.Status!=LiveOrderStatus.Unknown||string.IsNullOrEmpty(copy.AccountFingerprint)||copy.AccountFingerprint!=evidence.AccountFingerprint
                    ||!LiveOrderProtocol.ValidBrokerNumber(evidence.BrokerOrderNumber)||order.TradingDate!=evidence.TradingDate||order.Code!=evidence.Code
                    ||order.Kind!=evidence.Kind||order.TradeKind!=evidence.TradeKind||order.Quantity!=evidence.Quantity||order.LimitPrice!=evidence.LimitPrice
                    ||((!string.IsNullOrEmpty(order.OriginalBrokerOrderNumber)||!string.IsNullOrEmpty(evidence.OriginalBrokerOrderNumber))&&!LiveOrderProtocol.BrokerNumbersEqual(order.OriginalBrokerOrderNumber,evidence.OriginalBrokerOrderNumber))||utc<order.CreatedUtc
                    ||(!string.IsNullOrEmpty(order.BrokerOrderNumber)&&!LiveOrderProtocol.BrokerNumbersEqual(order.BrokerOrderNumber,evidence.BrokerOrderNumber))
                    ||copy.Orders.Any(o=>o.Id!=id&&o.TradingDate==order.TradingDate&&LiveOrderProtocol.BrokerNumbersEqual(o.BrokerOrderNumber,evidence.BrokerOrderNumber)))
                    throw new InvalidOperationException("복구 주문의 계좌·거래일·종목·방향·수량·가격이 일치하지 않습니다.");
                if(string.IsNullOrEmpty(order.BrokerOrderNumber))order.BrokerOrderNumber=evidence.BrokerOrderNumber;order.UpdatedUtc=utc;BrokerChanged(copy,utc);
            });
        }
        public void ReconcileWorking(string id,string brokerNumber,int ordered,int filled,int open,DateTime utc,string tradingDate=null) {
            LiveOrderSafety.RequireUtc(utc);
            Change(copy=>{
                var order=Find(copy,id);
                if(order.Status!=LiveOrderStatus.Unknown||order.TradingDate!=(tradingDate??TradeDate(utc))||!LiveOrderProtocol.ValidBrokerNumber(brokerNumber)||ordered!=order.Quantity
                    ||filled!=order.FilledQuantity||filled<0||open<0||filled+open+order.CancelledQuantity!=order.Quantity
                    ||order.Kind==LiveOrderKind.Cancel||order.Kind==LiveOrderKind.Amend&&string.IsNullOrEmpty(order.BrokerOrderNumber))throw new InvalidOperationException("재시작 주문과 증권사 체결·미체결 대사가 일치하지 않습니다.");
                if(copy.Orders.Any(o=>o.Id!=id&&o.TradingDate==order.TradingDate&&LiveOrderProtocol.BrokerNumbersEqual(o.BrokerOrderNumber,brokerNumber))
                    ||!string.IsNullOrEmpty(order.BrokerOrderNumber)&&!LiveOrderProtocol.BrokerNumbersEqual(order.BrokerOrderNumber,brokerNumber))throw new InvalidOperationException("증권사 주문번호가 중복되거나 기존 기록과 다릅니다.");
                if(string.IsNullOrEmpty(order.BrokerOrderNumber))order.BrokerOrderNumber=brokerNumber;order.UpdatedUtc=utc;
                order.Status=open==0?(order.CancelledQuantity>0?LiveOrderStatus.Cancelled:LiveOrderStatus.Filled):(filled>0?LiveOrderStatus.PartiallyFilled:LiveOrderStatus.Working);
                BrokerChanged(copy,utc);
            });
        }
        static int Remaining(LiveOrderRecord order){return order.Quantity-order.FilledQuantity-order.CancelledQuantity;}
        static bool IsWorking(LiveOrderRecord order){return order.Status==LiveOrderStatus.Working||order.Status==LiveOrderStatus.PartiallyFilled;}
        static LiveOrderRecord Find(LiveOrderJournalState copy,string id){var order=copy.Orders.SingleOrDefault(o=>o.Id==id);if(order==null)throw new InvalidOperationException("실계좌 주문 기록을 찾을 수 없습니다.");return order;}
        static string TradeDate(DateTime utc){return MarketTime.ToKorea(utc).ToString("yyyyMMdd",CultureInfo.InvariantCulture);}
        static void BrokerChanged(LiveOrderJournalState copy,DateTime utc){if(utc>copy.LastBrokerChangeUtc)copy.LastBrokerChangeUtc=utc;}
        static LiveOrderRecord FindBroker(LiveOrderJournalState copy,string number,string tradingDate){if(!LiveOrderProtocol.ValidBrokerNumber(number))throw new InvalidOperationException("증권사 주문번호가 필요합니다.");var order=copy.Orders.SingleOrDefault(o=>LiveOrderProtocol.BrokerNumbersEqual(o.BrokerOrderNumber,number)&&o.TradingDate==tradingDate);if(order==null)throw new InvalidOperationException("증권사 주문·거래일과 로컬 기록이 일치하지 않습니다.");return order;}
        void Update(string id,DateTime utc,Action<LiveOrderRecord> action,bool brokerChange=false) {
            LiveOrderSafety.RequireUtc(utc);Change(copy=>{var order=Find(copy,id);if(utc<order.CreatedUtc)throw new InvalidOperationException("주문 전 시각의 이벤트입니다.");action(order);order.UpdatedUtc=utc;if(brokerChange)BrokerChanged(copy,utc);});
        }
        void CheckUsable(){if(disposed)throw new ObjectDisposedException("LiveOrderJournal");if(storageFault)throw new InvalidOperationException("실주문 기록 저장 실패로 주문이 차단됐습니다.");}
        void Change(Action<LiveOrderJournalState> action) {
            lock(sync){CheckUsable();var copy=Copy(state);action(copy);Validate(copy);try{Save(copy);}catch{storageFault=true;throw;}state=copy;}
        }
        void Save(LiveOrderJournalState value) {
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){Serializer.Serialize(file,value);file.Flush(true);}if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        static LiveOrderJournalState Copy(LiveOrderJournalState value){using(var stream=new MemoryStream()){Serializer.Serialize(stream,value);stream.Position=0;return(LiveOrderJournalState)Serializer.Deserialize(stream);}}
        static void Validate(LiveOrderJournalState value) {
            if(value==null||value.Version!=1||value.Mode!="RealAccount"||string.IsNullOrEmpty(value.AccountSalt)||value.Orders==null||value.Executions==null||value.ConfirmationIds==null||value.LastBrokerChangeUtc.Kind!=DateTimeKind.Utc)
                throw new InvalidDataException("손상되거나 지원하지 않는 실주문 기록입니다.");
            if(value.Orders.Any(o=>o==null||string.IsNullOrWhiteSpace(o.Id)||o.Id.Length>64||string.IsNullOrWhiteSpace(o.ClientKey)||o.ClientKey.Length>128||string.IsNullOrEmpty(o.Code)||Rules.Code(o.Code)!=o.Code
                ||o.Quantity<=0||o.Quantity>1000000||o.LimitPrice<=0||o.FilledQuantity<0||o.CancelledQuantity<0||Remaining(o)<0||o.CreatedUtc.Kind!=DateTimeKind.Utc||o.UpdatedUtc.Kind!=DateTimeKind.Utc||o.UpdatedUtc<o.CreatedUtc||o.TradingDate!=TradeDate(o.CreatedUtc)
                ||!Enum.IsDefined(typeof(LiveOrderKind),o.Kind)||(o.TradeKind!=LiveOrderKind.Buy&&o.TradeKind!=LiveOrderKind.Sell)||!Enum.IsDefined(typeof(LiveOrderStatus),o.Status)||(!string.IsNullOrEmpty(o.BrokerOrderNumber)&&!LiveOrderProtocol.ValidBrokerNumber(o.BrokerOrderNumber))))
                throw new InvalidDataException("손상된 실주문 항목입니다.");
            if(value.Orders.GroupBy(o=>o.Id).Any(g=>g.Count()>1)||value.Orders.GroupBy(o=>o.ClientKey).Any(g=>g.Count()>1)
                ||value.Orders.Where(o=>!string.IsNullOrEmpty(o.BrokerOrderNumber)).GroupBy(o=>o.TradingDate+":"+LiveOrderProtocol.BrokerNumberKey(o.BrokerOrderNumber)).Any(g=>g.Count()>1)
                ||value.Executions.GroupBy(f=>f.OrderId+":"+f.ExecutionId).Any(g=>g.Count()>1)||value.ConfirmationIds.Distinct().Count()!=value.ConfirmationIds.Count)
                throw new InvalidDataException("중복 실주문·체결 식별자입니다.");
            if(value.Executions.Any(f=>f==null||string.IsNullOrWhiteSpace(f.ExecutionId)||f.Quantity<=0||f.Price<=0||f.ExecutedUtc.Kind!=DateTimeKind.Utc
                ||!value.Orders.Any(o=>o.Id==f.OrderId&&o.BrokerOrderNumber==f.BrokerOrderNumber&&o.TradingDate==f.TradingDate&&f.ExecutedUtc>=o.CreatedUtc)))
                throw new InvalidDataException("손상된 증권사 체결 기록입니다.");
            foreach(var order in value.Orders) {
                if(order.ResponseEvidence!=null) {
                    if(!order.RequestId.HasValue)throw new InvalidDataException("주문 응답에 요청번호가 없습니다.");
                    ValidateResponse(order.ResponseEvidence,order.RequestId.Value);
                }
                if(value.Executions.Where(f=>f.OrderId==order.Id).Sum(f=>f.Quantity)!=order.FilledQuantity
                    ||(order.Status==LiveOrderStatus.Filled&&Remaining(order)!=0)
                    ||(order.Status==LiveOrderStatus.PartiallyFilled&&(order.FilledQuantity==0||Remaining(order)==0))
                    ||(order.Status==LiveOrderStatus.AwaitingAcknowledgement&&(!order.RequestId.HasValue||order.RequestId.Value<=0))
                    ||((order.Status==LiveOrderStatus.Working||order.Status==LiveOrderStatus.PartiallyFilled||order.Status==LiveOrderStatus.Filled||order.Status==LiveOrderStatus.AwaitingConfirmation)&&!LiveOrderProtocol.ValidBrokerNumber(order.BrokerOrderNumber)))
                    throw new InvalidDataException("실주문과 체결 기록 합계가 다릅니다.");
                if(order.Status==LiveOrderStatus.NotSent&&(order.ClientKey!="not-sent/"+order.Id||string.IsNullOrWhiteSpace(order.OriginalClientKey)||order.OriginalClientKey.Length>128
                    ||order.RequestId.HasValue||!string.IsNullOrEmpty(order.BrokerOrderNumber)||order.FilledQuantity!=0||order.CancelledQuantity!=0)
                    ||order.Status!=LiveOrderStatus.NotSent&&!string.IsNullOrEmpty(order.OriginalClientKey))
                    throw new InvalidDataException("전송 전 중단 기록과 원래 전략 키의 감사 이력이 다릅니다.");
            }
        }
        public void Dispose(){lock(sync){if(disposed)return;disposed=true;lease.Dispose();}}
    }

    public sealed class LiveOrderAcknowledgement {
        public int RequestId;
        public string BrokerOrderNumber;
        public bool ProviderError;
        public bool ParseFailed;
        public OrderResponseEvidence ResponseEvidence;
    }
    public interface ILiveOrderWire : IDisposable {
        event Action<LiveOrderAcknowledgement> Acknowledged;
        int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc);
    }
    public interface ILiveOrderChannelPreparation {void PrepareChannel();}
    // Deliberately separate from the read-only adapter's allowlist. A dedicated
    // AxHost-created OCX on the owning STA is required; this class never creates a
    // login session or changes credentials/permissions on the user's behalf.
    public sealed class ComLiveOrderWire : ILiveOrderWire {
        readonly IIndiReadOnlyDispatch dispatch;readonly ComEventSubscriptions subscriptions;readonly int ownerThread;
        readonly Func<LiveOrderIntent,OrderResponseLayout> layoutProvider;
        readonly Func<IndiDiagnosticSnapshot> diagnosticsProvider;readonly Action<string> trace;
        readonly Action<LiveOrderIntent,SortedDictionary<int,string>> configureFields;
        readonly List<string> redactionValues=new List<string>();
        OrderResponseLayout responseLayout=OrderResponseLayout.Unknown;
        bool disposed,busy;int? pendingId;readonly List<LiveOrderAcknowledgement> early=new List<LiveOrderAcknowledgement>();
        public event Action<LiveOrderAcknowledgement> Acknowledged;
        public ComLiveOrderWire(object nativeControl,Func<LiveOrderIntent,OrderResponseLayout> layoutProvider=null,
            Func<IndiDiagnosticSnapshot> diagnosticsProvider=null,Action<string> trace=null,
            Action<LiveOrderIntent,SortedDictionary<int,string>> configureFields=null) {
            if(nativeControl==null||!Marshal.IsComObject(nativeControl))throw new ArgumentException("An actual native Indi ActiveX control is required.");
            if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Indi orders require an owning STA.");
            ownerThread=Thread.CurrentThread.ManagedThreadId;dispatch=(IIndiReadOnlyDispatch)nativeControl;
            this.layoutProvider=layoutProvider;this.diagnosticsProvider=diagnosticsProvider;this.trace=trace;this.configureFields=configureFields;
            var source=ComTypeDiscovery.Discover(nativeControl);subscriptions=new ComEventSubscriptions(nativeControl,source.SourceInterface);
            try{subscriptions.AddInt32Callback(source.DispIds["ReceiveData"],source.ArgumentTypes["ReceiveData"],OnData);}catch{subscriptions.Dispose();throw;}
        }
        public int Send(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            bool enteredRequestData=false,ownsPending=false;
            try {
                LiveOrderSafety.EnsureAllowed(checks,credentials,utc);LiveOrderSafety.ValidatePreflight(intent,checks,utc,DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc));
                if(disposed)throw new ObjectDisposedException("ComLiveOrderWire");if(Thread.CurrentThread.ManagedThreadId!=ownerThread||busy)throw new InvalidOperationException("Indi order control is busy or outside its owning STA.");
                busy=true;ownsPending=true;pendingId=null;early.Clear();redactionValues.Clear();
                redactionValues.Add(credentials.Account);redactionValues.Add(credentials.Password);
                responseLayout=layoutProvider==null?OrderResponseLayout.Unknown:layoutProvider(intent);
                if(responseLayout==OrderResponseLayout.Unknown||!Enum.IsDefined(typeof(OrderResponseLayout),responseLayout))
                    throw new InvalidOperationException("Installed order response definition is not identified.");
                var fields=LiveOrderProtocol.BuildFields(intent,credentials);
                if(configureFields!=null)configureFields(intent,fields);
                var query=LiveOrderProtocol.QueryName(intent);if(!dispatch.SetQueryName(query)||!string.Equals(Convert.ToString(dispatch.GetQueryName(),CultureInfo.InvariantCulture),query,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Indi order query selection failed.");
                foreach(var field in fields)if(!dispatch.SetSingleData(checked((short)field.Key),field.Value))throw new InvalidOperationException("Indi order input failed.");
                // COM calls may pump the UI. A user stop/account change during
                // input preparation must revoke permission before the side effect.
                var dispatchUtc=DateTime.UtcNow;
                LiveOrderSafety.EnsureAllowed(checks,credentials,dispatchUtc);
                LiveOrderSafety.ValidatePreflight(intent,checks,dispatchUtc,DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc));
                enteredRequestData=true;
                int id=dispatch.RequestData();if(id<=0)throw new InvalidOperationException("Indi order submission did not return an accepted request ID.");
                pendingId=id;var match=early.FirstOrDefault(a=>a.RequestId==id);early.Clear();if(match!=null)Emit(match);return id;
            }catch{
                if(ownsPending){busy=false;pendingId=null;early.Clear();redactionValues.Clear();responseLayout=OrderResponseLayout.Unknown;}
                if(!enteredRequestData)throw new LiveOrderNotSentException();
                throw new InvalidOperationException("실주문 요청 실행 이후 응답을 확인할 수 없습니다. 증권사 주문·체결 대사가 필요합니다.");
            }
        }
        void OnData(int id) {
            if(disposed||!busy||Thread.CurrentThread.ManagedThreadId!=ownerThread)return;
            if(pendingId.HasValue&&pendingId.Value!=id)return;
            LiveOrderAcknowledgement acknowledgement;
            try {
                // Snapshot provider status before field getters can replace the
                // native diagnostic buffer, then copy every field in this callback.
                var diagnostic=diagnosticsProvider==null?ReadDiagnostics():diagnosticsProvider();
                var evidence=LiveOrderResponseParser.Read(id,index=>dispatch.GetSingleData(checked((short)index)),diagnostic,responseLayout,redactionValues);
                acknowledgement=new LiveOrderAcknowledgement{RequestId=id,ProviderError=evidence.ProviderError,
                    BrokerOrderNumber=evidence.BrokerOrderNumber,ParseFailed=evidence.ParseFailed,ResponseEvidence=evidence};
            }catch{acknowledgement=new LiveOrderAcknowledgement{RequestId=id,ParseFailed=true,BrokerOrderNumber="",
                ResponseEvidence=new OrderResponseEvidence{RequestId=id,Layout=OrderResponseLayout.Unknown,ParseFailed=true,SafeDiagnostic="OrderResponse: response evidence unavailable; provider text omitted."}};}
            if(!pendingId.HasValue){if(early.Count<16)early.Add(acknowledgement);return;}Emit(acknowledgement);
        }
        IndiDiagnosticSnapshot ReadDiagnostics() {
            var value=new IndiDiagnosticSnapshot();
            try{value.ErrorState=dispatch.GetErrorState();}catch{}
            try{value.ErrorCode=Convert.ToString(dispatch.GetErrorCode(),CultureInfo.InvariantCulture);}catch{}
            try{value.Message=Convert.ToString(dispatch.GetErrorMessage(),CultureInfo.InvariantCulture);}catch{}
            try{value.CommState=dispatch.GetCommState();}catch{}
            return value;
        }
        void Emit(LiveOrderAcknowledgement value) {
            busy=false;pendingId=null;redactionValues.Clear();responseLayout=OrderResponseLayout.Unknown;
            try{if(trace!=null&&value.ResponseEvidence!=null)trace(value.ResponseEvidence.SafeDiagnostic);}catch{}
            if(Acknowledged!=null)Acknowledged(value);
        }
        public void Dispose(){if(disposed)return;disposed=true;try{subscriptions.Dispose();}finally{early.Clear();redactionValues.Clear();responseLayout=OrderResponseLayout.Unknown;}}
    }
    public sealed class LiveOrderGateway : IDisposable {
        readonly LiveOrderJournal journal;readonly ILiveOrderWire wire;readonly Func<DateTime> clock;
        readonly List<LiveOrderAcknowledgement> early=new List<LiveOrderAcknowledgement>();string pendingOrder;int? pendingRequest;DateTime? pendingSince;bool disposed,invalidating;
        public bool Busy {get{return pendingOrder!=null;}}
        public event Action<LiveOrderRecord> OrderChanged;
        public void PrepareChannel() {
            if(disposed||Busy)throw new InvalidOperationException("주문 채널을 준비할 수 없습니다.");
            var preparation=wire as ILiveOrderChannelPreparation;if(preparation!=null)preparation.PrepareChannel();
        }
        public LiveOrderGateway(LiveOrderJournal journal,ILiveOrderWire wire,Func<DateTime> utcNow=null) {
            if(journal==null||wire==null)throw new ArgumentNullException("Live-order journal and wire are required.");
            this.journal=journal;this.wire=wire;clock=utcNow??(()=>DateTime.UtcNow);wire.Acknowledged+=OnAcknowledged;
        }
        public LiveOrderRecord Submit(LiveOrderIntent intent,LiveAccountCredentials credentials,LiveOrderPreflight checks,DateTime utc) {
            if(disposed)throw new ObjectDisposedException("LiveOrderGateway");LiveOrderSafety.EnsureAllowed(checks,credentials,utc);
            if(credentials==null)throw new ArgumentNullException("credentials");
            // The operating limit is independent of broker cash/capacity. Preserve
            // outstanding buy exposure across different symbols and through cancel
            // acknowledgement. Amendments conservatively retain the original reserve
            // until authoritative replacement confirmation; no optimistic release.
            LiveOrderSafety.ValidatePreflight(intent,checks,utc,journal.Snapshot.LastBrokerChangeUtc,journal.ReservedBuyAmount);
            if(intent.Kind==LiveOrderKind.Amend||intent.Kind==LiveOrderKind.Cancel) {
                var original=journal.Snapshot.Orders.SingleOrDefault(o=>LiveOrderProtocol.BrokerNumbersEqual(o.BrokerOrderNumber,intent.OriginalBrokerOrderNumber)&&o.TradingDate==MarketTime.ToKorea(utc).ToString("yyyyMMdd",CultureInfo.InvariantCulture));
                if(original==null||original.TradeKind!=checks.OriginalTradeKind)throw new InvalidOperationException("정정·취소 원주문의 방향과 실계좌 대사가 일치하지 않습니다.");
            }
            if(journal.RecoveryRequired||pendingOrder!=null)throw new InvalidOperationException("불명 주문 대사 또는 진행 중 주문 확인이 필요합니다.");
            journal.BindAccount(credentials.Account);
            var order=journal.Prepare(intent,utc);pendingOrder=order.Id;pendingRequest=null;pendingSince=utc;early.Clear();
            // Durable Dispatching precedes the external side effect. Only the
            // wire's proven pre-entry refusal can establish NotSent; other
            // exceptions leave the request Unknown, never broker-rejected.
            journal.MarkDispatching(order.Id,utc);
            bool wireReturned=false;
            try {
                var id=wire.Send(intent,credentials,checks,utc);wireReturned=true;journal.MarkRequestAccepted(order.Id,id,clock());pendingRequest=id;
                var match=early.FirstOrDefault(a=>a.RequestId==id);early.Clear();if(match!=null)OnAcknowledged(match);
                return journal.Snapshot.Orders.Single(o=>o.Id==order.Id);
            }catch(LiveOrderNotSentException){
                // A typed refusal is proof only while the wire has not returned
                // from submission. Never reclassify a post-entry/returned request.
                if(wireReturned) {
                    try{journal.MarkUnknown(order.Id,clock());}catch{}
                    pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();NotifyChanged(order.Id);
                    throw new InvalidOperationException("실주문 전송 결과 불명: 증권사 주문·체결 대사가 필요합니다.");
                }
                try{journal.RecordNotSent(order.Id,clock());}
                catch{
                    try{journal.MarkUnknown(order.Id,clock());}catch{}
                    pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();NotifyChanged(order.Id);
                    throw new InvalidOperationException("미전송 감사 기록 저장 실패: 주문 기록 확인이 필요합니다.");
                }
                pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();NotifyChanged(order.Id);
                throw;
            }catch{
                try{journal.MarkUnknown(order.Id,clock());}catch{ /* StorageFault keeps this instance blocked; durable Dispatching recovers as Unknown. */ }
                pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();NotifyChanged(order.Id);throw new InvalidOperationException("실주문 전송 결과 불명: 재전송하지 말고 증권사 주문·체결을 대사해야 합니다.");
            }
        }
        void OnAcknowledged(LiveOrderAcknowledgement acknowledgement) {
            if(disposed||pendingOrder==null||acknowledgement==null)return;
            if(!pendingRequest.HasValue){if(early.Count<16)early.Add(acknowledgement);return;}
            if(acknowledgement.RequestId!=pendingRequest.Value)return;
            var changedId=pendingOrder;
            try {
                if(acknowledgement.ResponseEvidence!=null)
                    journal.RecordAcknowledgement(pendingOrder,acknowledgement.RequestId,acknowledgement.BrokerOrderNumber,acknowledgement.ProviderError,clock(),acknowledgement.ResponseEvidence);
                else if(acknowledgement.ParseFailed)journal.MarkUnknown(pendingOrder,clock());
                else journal.RecordAcknowledgement(pendingOrder,acknowledgement.RequestId,acknowledgement.BrokerOrderNumber,acknowledgement.ProviderError,clock());
            }catch{try{journal.MarkUnknown(pendingOrder,clock());}catch{ /* StorageFault is latched. Never turn read/validation failure into rejection. */ }}
            pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();
            NotifyChanged(changedId);
        }
        void NotifyChanged(string id) {
            if(OrderChanged==null)return;
            try{var order=journal.Snapshot.Orders.SingleOrDefault(o=>o.Id==id);if(order!=null)OrderChanged(order);}
            catch{try{journal.MarkUnknown(id,clock());}catch{ /* Failed consumers/storage cannot establish reconciliation. */ }}
        }
        public void Timeout(DateTime utc){
            LiveOrderSafety.RequireUtc(utc);if(pendingOrder==null||!pendingSince.HasValue||utc-pendingSince.Value<TimeSpan.FromSeconds(20))return;
            var changedId=pendingOrder;try{journal.MarkUnknown(changedId,utc);}finally{pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();}NotifyChanged(changedId);
        }
        public void Invalidate(DateTime utc) {
            LiveOrderSafety.RequireUtc(utc);
            if(invalidating)return;invalidating=true;
            try {
                var unresolved=journal.Snapshot.Orders.Where(o=>LiveOrderJournal.IsUnresolved(o)&&o.Status!=LiveOrderStatus.Unknown).Select(o=>o.Id).ToArray();
                pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();
                foreach(var id in unresolved){journal.MarkUnknown(id,utc);NotifyChanged(id);}
            }finally{invalidating=false;}
        }
        public void Dispose(){
            if(disposed)return;disposed=true;
            var changedId=pendingOrder;
            if(changedId!=null){try{journal.MarkUnknown(changedId,clock());}catch{ /* Durable in-flight states recover as Unknown on restart. */ }}
            pendingOrder=null;pendingRequest=null;pendingSince=null;early.Clear();
            if(changedId!=null)NotifyChanged(changedId);
            wire.Acknowledged-=OnAcknowledged;wire.Dispose();
        }
    }
}
