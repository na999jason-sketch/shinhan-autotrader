using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public sealed class BrokerReadinessResult {
        readonly List<string> reasons=new List<string>();
        public LiveOrderPreflight Checks {get;internal set;}
        public Func<bool> EvidenceStillCurrent {get;internal set;}
        public bool Ready {get{return reasons.Count==0;}}
        public IList<string> Reasons {get{return reasons.AsReadOnly();}}
        public string ReconciliationScope {get;internal set;}
        public string Status {get{return Ready?"주문별 계좌·시세 확인 완료":string.Join(" / ",reasons);}}
        internal void Require(bool condition,string reason){if(!condition&&!reasons.Contains(reason))reasons.Add(reason);}
    }

    // Account-scoped evidence, not a trading switch or a source of invented
    // executions. Aggregate query rows remain aggregates. Reconciliation is
    // limited to the journal's known orders and an explicitly empty baseline.
    public sealed class BrokerReadiness {
        sealed class Stamp {
            public DateTime Started,Received;
            public Stamp(DateTime started,DateTime received){Utc(started);Utc(received);if(started>received)throw new ArgumentException("Request start must precede its response.");Started=started;Received=received;}
        }
        sealed class Sellable {
            public string Code;
            public int Quantity,Unfilled;
            public bool Validated;
            public Stamp Time;
        }
        sealed class TradingDateEvidence {public string Day;public bool Valid;public Stamp Time;}
        readonly object sync=new object();
        readonly string accountKey,journalFingerprint;
        readonly Dictionary<string,AccountOrderCapacity> capacities=new Dictionary<string,AccountOrderCapacity>(StringComparer.Ordinal);
        readonly Dictionary<string,Stamp> capacityTimes=new Dictionary<string,Stamp>(StringComparer.Ordinal);
        readonly Dictionary<string,Sellable> sellable=new Dictionary<string,Sellable>(StringComparer.Ordinal);
        readonly Dictionary<string,Tuple<int,DateTime>> balanceSellable=new Dictionary<string,Tuple<int,DateTime>>(StringComparer.Ordinal);
        readonly Dictionary<string,TradingDateEvidence> tradingDates=new Dictionary<string,TradingDateEvidence>(StringComparer.Ordinal);
        List<Holding> holdings;
        Stamp holdingTime,cashTime,orderTime,brokerDateTime;
        AccountCashSnapshot cash;
        BrokerOrdersSnapshot orders;
        string brokerDate;
        bool brokerDateValidated,authorized,subscribed,streamObserved,holdingsTradeBasis;
        long evidenceEpoch;
        DateTime invalidatedUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        DateTime streamEpochUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);

        public BrokerReadiness(string opaqueAccountKey,string journalAccountFingerprint) {
            if(opaqueAccountKey==null||opaqueAccountKey.Length!=64||opaqueAccountKey.Any(c=>!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F'))))
                throw new ArgumentException("An opaque account key is required.");
            byte[] fingerprint;
            try{fingerprint=Convert.FromBase64String(journalAccountFingerprint??"");}catch{throw new ArgumentException("A bound journal account fingerprint is required.");}
            if(fingerprint.Length!=32)throw new ArgumentException("A bound journal account fingerprint is required.");
            accountKey=opaqueAccountKey;journalFingerprint=journalAccountFingerprint;
        }
        public string AccountKey {get{return accountKey;}}
        public string BrokerDateDiagnostic {get{lock(sync){return brokerDate;}}}
        public bool BrokerDateDiagnosticIsFullCalendarDate {get{lock(sync){return brokerDateValidated;}}}
        public bool AccountStreamObserved {get{lock(sync){return streamObserved;}}}
        public DateTime InvalidatedUtc {get{lock(sync){return invalidatedUtc;}}}
        public void SetAuthorization(bool enabled){lock(sync){if(authorized!=enabled){authorized=enabled;AdvanceEpoch();}}}
        public void SetAccountStream(bool registered,DateTime utc){SetAccountStream(registered,false,utc);}
        public void SetAccountStream(bool registered,bool eventObserved,DateTime utc) {
            Utc(utc);lock(sync){
                if(!registered){subscribed=false;streamObserved=false;InvalidateCore(utc);return;}
                if(!subscribed){streamEpochUtc=utc;InvalidateCore(utc);}
                subscribed=true;if(eventObserved&&!streamObserved){streamObserved=true;AdvanceEpoch();}
            }
        }
        public void ObserveAccountEvent(DateTime utc){Utc(utc);lock(sync){if(subscribed)streamObserved=true;InvalidateCore(utc);}}
        public void Invalidate(DateTime utc){Utc(utc);lock(sync){InvalidateCore(utc);}}
        // Provider messages must not become readiness reasons or leak credentials.
        public void Invalidate(DateTime utc,string ignoredProviderText){Invalidate(utc);}
        void AdvanceEpoch(){checked{evidenceEpoch++;}}
        void InvalidateCore(DateTime utc){if(utc>invalidatedUtc)invalidatedUtc=utc;AdvanceEpoch();}
        static void Utc(DateTime utc){if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC evidence timestamps are required.");}
        static string CanonicalCode(string code){if(string.IsNullOrEmpty(code)||Rules.Code(code)!=code)throw new ArgumentException("A normalized stock symbol is required.");return code;}
        static bool Newer(Stamp old,Stamp value){return old==null||value.Received>old.Received;}
        static string CapacityKey(string code,decimal price){return code+":"+price.ToString("G29",CultureInfo.InvariantCulture);}
        static string NumberKey(string value){return LiveOrderProtocol.BrokerNumberKey(value);}
        static Holding Copy(Holding h){return new Holding{Code=h.Code,Name=h.Name,Quantity=h.Quantity,AvgPrice=h.AvgPrice,Price=h.Price,BuyPending=h.BuyPending,SellPending=h.SellPending,CreditQty=h.CreditQty};}
        static bool Whole(decimal value){return value>=0&&value==decimal.Truncate(value)&&value<=int.MaxValue;}
        static List<Holding> CopyHoldings(IEnumerable<Holding> values) {
            if(values==null)throw new ArgumentNullException("values");var result=new List<Holding>();var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var h in values) {
                if(h==null||string.IsNullOrEmpty(h.Code)||Rules.Code(h.Code)!=h.Code||!seen.Add(h.Code)||!Whole(h.Quantity)||!Whole(h.BuyPending)||!Whole(h.SellPending)||!Whole(h.CreditQty)||h.AvgPrice<0||h.Price<0)
                    throw new ArgumentException("Malformed broker holdings evidence.");
                result.Add(Copy(h));
            }
            return result;
        }
        public bool ObserveHoldings(IEnumerable<Holding> values,DateTime requestStartedUtc,DateTime receivedUtc) {
            var stamp=new Stamp(requestStartedUtc,receivedUtc);var copy=CopyHoldings(values);
            lock(sync){if(!Newer(holdingTime,stamp))return false;holdings=copy;holdingTime=stamp;holdingsTradeBasis=false;AdvanceEpoch();return true;}
        }
        public bool ObserveCash(AccountCashSnapshot value,DateTime requestStartedUtc) {
            if(value==null)throw new ArgumentNullException("value");var stamp=new Stamp(requestStartedUtc,value.ReceivedUtc);
            var copy=new AccountCashSnapshot{DepositCash=value.DepositCash,CashOrderable=value.CashOrderable,Cash100Orderable=value.Cash100Orderable,ReceivedUtc=value.ReceivedUtc};
            lock(sync){if(!Newer(cashTime,stamp))return false;cash=copy;cashTime=stamp;AdvanceEpoch();return true;}
        }
        public bool ObserveOrders(BrokerOrdersSnapshot value,DateTime requestStartedUtc) {
            if(value==null)throw new ArgumentNullException("value");DateTime day;
            if(!DateTime.TryParseExact(value.TradingDate,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out day))throw new ArgumentException("A broker query trading date is required.");
            var stamp=new Stamp(requestStartedUtc,value.ReceivedUtc);var copy=new BrokerOrdersSnapshot{TradingDate=value.TradingDate,ReceivedUtc=value.ReceivedUtc};
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in value.Orders) {
                if(row==null||!LiveOrderProtocol.ValidBrokerNumber(row.OrderNumber)||!seen.Add(NumberKey(row.OrderNumber))||string.IsNullOrEmpty(row.Code)||Rules.Code(row.Code)!=row.Code
                    ||(row.SideCode!="1"&&row.SideCode!="2")||!Whole(row.Quantity)||!Whole(row.FilledQuantity)||!Whole(row.OpenQuantity)||row.FilledQuantity+row.OpenQuantity>row.Quantity)
                    throw new ArgumentException("Malformed aggregate broker order evidence.");
                copy.Orders.Add(new BrokerOrderRow{OrderNumber=row.OrderNumber,OriginalOrderNumber=row.OriginalOrderNumber,Code=row.Code,SideCode=row.SideCode,AmendCancelCode=row.AmendCancelCode,
                    ProductCode=row.ProductCode,CreditCode=row.CreditCode,Quantity=row.Quantity,FilledQuantity=row.FilledQuantity,OpenQuantity=row.OpenQuantity,LimitPrice=row.LimitPrice,
                    AverageFillPrice=row.AverageFillPrice,ConfirmationCode=row.ConfirmationCode,ConfirmedQuantity=row.ConfirmedQuantity,AutoCancelledQuantity=row.AutoCancelledQuantity});
            }
            lock(sync){if(!Newer(orderTime,stamp))return false;orders=copy;orderTime=stamp;AdvanceEpoch();return true;}
        }
        public bool ObserveCapacity(AccountOrderCapacity value,DateTime requestStartedUtc) {
            if(value==null)throw new ArgumentNullException("value");CanonicalCode(value.Code);var stamp=new Stamp(requestStartedUtc,value.ReceivedUtc);
            if(value.LimitPrice<=0||value.Ratio!=100||!Whole(value.Quantity)||!Whole(value.MaximumQuantity)||value.Amount<0||value.MaximumAmount<value.Amount||value.MaximumQuantity<value.Quantity)
                throw new ArgumentException("Malformed cash100 broker capacity evidence.");
            var key=CapacityKey(value.Code,value.LimitPrice);var copy=new AccountOrderCapacity{Code=value.Code,LimitPrice=value.LimitPrice,Ratio=value.Ratio,Quantity=value.Quantity,MaximumQuantity=value.MaximumQuantity,Amount=value.Amount,MaximumAmount=value.MaximumAmount,ReceivedUtc=value.ReceivedUtc};
            lock(sync){Stamp old;if(capacityTimes.TryGetValue(key,out old)&&!Newer(old,stamp))return false;capacities[key]=copy;capacityTimes[key]=stamp;AdvanceEpoch();return true;}
        }
        public bool ObserveBrokerDate(string value,DateTime requestStartedUtc,DateTime receivedUtc,bool sourceValidated) {
            var stamp=new Stamp(requestStartedUtc,receivedUtc);DateTime parsed;
            bool valid=sourceValidated&&DateTime.TryParseExact(value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed);
            lock(sync){if(!Newer(brokerDateTime,stamp))return false;brokerDate=value;brokerDateValidated=valid;brokerDateTime=stamp;AdvanceEpoch();return true;}
        }
        // This full yyyyMMdd comes from TR_SCHART's actual date field for the
        // requested symbol. The two-character SABA609 day header is diagnostic
        // data only and must never be expanded with the PC year/month.
        public bool ObserveTradingDate(string code,string value,DateTime requestStartedUtc,DateTime receivedUtc) {
            CanonicalCode(code);var stamp=new Stamp(requestStartedUtc,receivedUtc);DateTime parsed;
            bool valid=value!=null&&value.Length==8&&DateTime.TryParseExact(value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed);
            lock(sync){TradingDateEvidence old;if(tradingDates.TryGetValue(code,out old)&&!Newer(old.Time,stamp))return false;
                tradingDates[code]=new TradingDateEvidence{Day=value,Valid=valid,Time=stamp};AdvanceEpoch();return true;}
        }
        public bool ObserveSellable(string code,int quantity,DateTime requestStartedUtc,DateTime receivedUtc,bool sourceValidated,int unfilledQuantity=0) {
            CanonicalCode(code);if(quantity<0||unfilledQuantity<0)throw new ArgumentException("Nonnegative broker quantities are required.");var stamp=new Stamp(requestStartedUtc,receivedUtc);
            lock(sync){Sellable old;if(sellable.TryGetValue(code,out old)&&!Newer(old.Time,stamp))return false;sellable[code]=new Sellable{Code=code,Quantity=quantity,Unfilled=unfilledQuantity,Validated=sourceValidated,Time=stamp};AdvanceEpoch();return true;}
        }
        public void ObserveBalanceSellable(string code,int quantity,DateTime receivedUtc) {
            CanonicalCode(code);Utc(receivedUtc);if(quantity<0)throw new ArgumentException("A nonnegative AD quantity is required.");
            lock(sync){Tuple<int,DateTime> old;if(balanceSellable.TryGetValue(code,out old)&&receivedUtc<=old.Item2)return;
                balanceSellable[code]=Tuple.Create(quantity,receivedUtc);streamObserved=subscribed;
                AdvanceEpoch();
                if(old==null||old.Item1!=quantity)InvalidateCore(receivedUtc);
            }
        }
        public bool ObserveTradeBalance(BrokerHoldingsSnapshot value) {
            if(value==null)throw new ArgumentNullException("value");
            var stamp=new Stamp(value.RequestStartedUtc,value.ReceivedUtc);var values=CopyHoldings(value.Holdings);
            lock(sync){
                if(!Newer(holdingTime,stamp))return false;
                var next=new Dictionary<string,Sellable>(StringComparer.Ordinal);
                foreach(var h in values) {
                    int quantity,unfilled;bool validated;
                    if(!value.SellableQuantities.TryGetValue(h.Code,out quantity)||quantity<0||!value.UnfilledQuantities.TryGetValue(h.Code,out unfilled)||unfilled<0)
                        throw new ArgumentException("Trade-basis holdings are missing broker quantities.");
                    value.CashSourceValidated.TryGetValue(h.Code,out validated);
                    next[h.Code]=new Sellable{Code=h.Code,Quantity=quantity,Unfilled=unfilled,Validated=validated,Time=stamp};
                }
                if(value.UnfilledQuantities.Any(x=>x.Value>0&&!next.ContainsKey(x.Key)))throw new ArgumentException("Unmatched pending broker position evidence.");
                holdings=values;holdingTime=stamp;holdingsTradeBasis=true;sellable.Clear();foreach(var pair in next)sellable[pair.Key]=pair.Value;
                brokerDate=value.BrokerDate;brokerDateValidated=value.BrokerDateValidated;brokerDateTime=stamp;AdvanceEpoch();return true;
            }
        }

        static bool Fresh(Stamp stamp,DateTime now,DateTime barrier){return stamp!=null&&stamp.Started>barrier&&stamp.Received<=now&&now-stamp.Received<=TimeSpan.FromSeconds(5);}
        static bool IsTransit(LiveOrderRecord order){return order.Status==LiveOrderStatus.Prepared||order.Status==LiveOrderStatus.Dispatching||order.Status==LiveOrderStatus.AwaitingAcknowledgement||order.Status==LiveOrderStatus.AwaitingConfirmation||order.Status==LiveOrderStatus.Unknown;}
        static bool IsWorking(LiveOrderRecord order){return order.Status==LiveOrderStatus.Working||order.Status==LiveOrderStatus.PartiallyFilled;}
        public BrokerReadinessResult Evaluate(LiveOrderIntent intent,IndiReply quote,DateTime quoteReceivedUtc,bool quoteIsLiveAndAdvanced,bool priceRuleVerified,
            LiveOrderJournalState journal,decimal operatingRemainingLimit,DateTime nowUtc) {
            Utc(nowUtc);Utc(quoteReceivedUtc);lock(sync){
                var result=new BrokerReadinessResult{Checks=new LiveOrderPreflight(),ReconciliationScope="Unverified"};var checks=result.Checks;
                long capturedEpoch=evidenceEpoch;
                result.EvidenceStillCurrent=()=>{lock(sync){return evidenceEpoch==capturedEpoch&&authorized&&subscribed;}};
                checks.EvidenceStillCurrent=result.EvidenceStillCurrent;
                result.Require(authorized,"실계좌 주문 사용 확인 필요");checks.AccountAuthorized=authorized;
                result.Require(subscribed,"실시간 계좌 등록 확인 필요");
                bool journalValid=journal!=null&&journal.Mode=="RealAccount"&&journal.AccountFingerprint==journalFingerprint&&journal.Orders!=null&&journal.Executions!=null&&journal.LastBrokerChangeUtc.Kind==DateTimeKind.Utc
                    &&journal.Orders.All(x=>x!=null&&x.Quantity>0&&x.FilledQuantity>=0&&x.CancelledQuantity>=0&&x.FilledQuantity+x.CancelledQuantity<=x.Quantity&&x.LimitPrice>0);
                result.Require(journalValid,"현재 계좌 주문 기록 확인 필요");
                bool validIntent=true;try{LiveOrderProtocol.ValidateIntent(intent);}catch(ArgumentException){validIntent=false;}
                result.Require(validIntent,"주문 입력 확인 필요");if(!validIntent)return result;
                string day=MarketTime.ToKorea(nowUtc).ToString("yyyyMMdd",CultureInfo.InvariantCulture);
                DateTime barrier=invalidatedUtc>streamEpochUtc?invalidatedUtc:streamEpochUtc;
                if(journalValid&&journal.LastBrokerChangeUtc>barrier)barrier=journal.LastBrokerChangeUtc;
                bool hf=Fresh(holdingTime,nowUtc,barrier),of=Fresh(orderTime,nowUtc,barrier);
                result.Require(hf&&holdings!=null&&holdingsTradeBasis,"변경 후 체결기준 잔고 조회 필요");
                result.Require(of&&orders!=null&&orders.TradingDate==day,"변경 후 당일 주문 조회 필요");
                TradingDateEvidence calendar;
                bool dayVerified=tradingDates.TryGetValue(intent.Code,out calendar)&&calendar.Valid&&calendar.Day==day&&calendar.Time.Started>streamEpochUtc&&calendar.Time.Received<=nowUtc;
                result.Require(dayVerified,"현재 종목의 당일 일봉 거래일 확인 필요");
                var stamps=new List<DateTime>();if(hf)stamps.Add(holdingTime.Received);if(of)stamps.Add(orderTime.Received);
                bool rows=journalValid&&hf&&holdingsTradeBasis&&of&&orders!=null&&orders.TradingDate==day,fillEvidence=rows;
                var local=journalValid?journal.Orders.Where(x=>x!=null&&x.TradingDate==day).ToList():new List<LiveOrderRecord>();
                if(local.Where(x=>NumberKey(x.BrokerOrderNumber)!=null).GroupBy(x=>NumberKey(x.BrokerOrderNumber),StringComparer.Ordinal).Any(x=>x.Count()>1)) {
                    rows=false;fillEvidence=false;result.Require(false,"주문번호 표기 중복 확인 필요");
                }
                if(journalValid&&journal.Orders.Any(x=>x==null||IsTransit(x))){rows=false;fillEvidence=false;result.Require(false,"미확정 주문의 접수·체결·복구 대사 필요");}
                if(journalValid&&journal.Orders.Any(x=>LiveOrderJournal.IsUnresolved(x)&&x.TradingDate!=day)){rows=false;fillEvidence=false;result.Require(false,"이전 거래일 미완료 주문 확인 필요");}
                if(journalValid&&journal.Orders.Any(x=>x.ClientKey==intent.ClientKey))result.Require(false,"동일 중복방지 키의 주문이 이미 기록됨");
                if(journalValid&&(intent.Kind==LiveOrderKind.Buy||intent.Kind==LiveOrderKind.Sell)&&journal.Orders.Any(x=>x.Code==intent.Code&&LiveOrderJournal.IsUnresolved(x)))result.Require(false,"동일 종목의 미완료 주문 확인 필요");
                if(rows) {
                    var known=new HashSet<string>(local.Where(x=>LiveOrderProtocol.ValidBrokerNumber(x.BrokerOrderNumber)).Select(x=>NumberKey(x.BrokerOrderNumber)),StringComparer.Ordinal);
                    if(orders.Orders.Any(x=>x.OpenQuantity>0&&!known.Contains(NumberKey(x.OrderNumber)))){rows=false;result.Require(false,"프로그램 외 미체결 주문 확인 필요");}
                    foreach(var order in local) {
                        if(order.Status==LiveOrderStatus.Rejected&&string.IsNullOrEmpty(order.BrokerOrderNumber))continue;
                        if(order.Status==LiveOrderStatus.NotSent&&string.IsNullOrEmpty(order.BrokerOrderNumber)&&!order.RequestId.HasValue
                            &&order.FilledQuantity==0&&order.CancelledQuantity==0&&order.ClientKey=="not-sent/"+order.Id
                            &&!string.IsNullOrWhiteSpace(order.OriginalClientKey)&&!journal.Executions.Any(x=>x!=null&&x.OrderId==order.Id))continue;
                        var match=orders.Orders.SingleOrDefault(x=>NumberKey(x.OrderNumber)==NumberKey(order.BrokerOrderNumber));
                        decimal expectedOpen=IsWorking(order)?order.Quantity-order.FilledQuantity-order.CancelledQuantity:0;
                        string side=order.TradeKind==LiveOrderKind.Buy?"2":order.TradeKind==LiveOrderKind.Sell?"1":"";
                        if(match==null||match.Code!=order.Code||match.SideCode!=side||match.ProductCode!="01"||match.CreditCode!="00"
                            ||NumberKey(match.OriginalOrderNumber)!=NumberKey(order.OriginalBrokerOrderNumber)||match.Quantity!=order.Quantity||match.FilledQuantity!=order.FilledQuantity||match.OpenQuantity!=expectedOpen||match.LimitPrice!=order.LimitPrice)rows=false;
                        var executions=journal.Executions.Where(x=>x!=null&&x.OrderId==order.Id).ToList();
                        if(executions.Any(x=>string.IsNullOrWhiteSpace(x.ExecutionId)||NumberKey(x.BrokerOrderNumber)!=NumberKey(order.BrokerOrderNumber)||x.TradingDate!=order.TradingDate||x.Quantity<=0||x.Price<=0||x.ExecutedUtc.Kind!=DateTimeKind.Utc||x.ExecutedUtc>nowUtc)
                            ||executions.Select(x=>x.ExecutionId).Distinct(StringComparer.Ordinal).Count()!=executions.Count||executions.Sum(x=>(long)x.Quantity)!=order.FilledQuantity)fillEvidence=false;
                    }
                    var pending=orders.Orders.Where(x=>x.OpenQuantity>0).GroupBy(x=>x.Code).ToDictionary(x=>x.Key,x=>x.Sum(o=>o.OpenQuantity),StringComparer.Ordinal);
                    foreach(var position in holdings) {
                        decimal expected;pending.TryGetValue(position.Code,out expected);
                        Sellable source;decimal reported=sellable.TryGetValue(position.Code,out source)?source.Unfilled:position.BuyPending+position.SellPending;
                        if(reported!=expected)rows=false;
                    }
                    if(pending.Any(x=>!holdings.Any(h=>h.Code==x.Key)))rows=false;
                }
                fillEvidence=fillEvidence&&rows;
                result.Require(rows,"확인된 주문·잔고 수량 대사 필요");
                result.Require(fillEvidence,"주문별 실제 체결번호·누적수량 대사 필요");
                checks.OrdersReconciled=rows;checks.FillsReconciled=fillEvidence;checks.RecoveryVerified=journalValid&&!journal.Orders.Any(x=>x==null||IsTransit(x))&&rows&&fillEvidence;
                result.ReconciliationScope=rows&&fillEvidence?(local.Any(IsWorking)?"KnownLocalOrdersAndExecutions":"NoLocalUnresolvedBaseline"):"Unverified";
                var effective=intent.Kind;
                if(intent.Kind==LiveOrderKind.Amend||intent.Kind==LiveOrderKind.Cancel) {
                    var originals=local.Where(x=>NumberKey(x.BrokerOrderNumber)==NumberKey(intent.OriginalBrokerOrderNumber)&&IsWorking(x)).ToList();
                    var original=originals.Count==1?originals[0]:null;
                    checks.OriginalOrderVerified=rows&&original!=null&&original.Code==intent.Code&&intent.Quantity<=original.Quantity-original.FilledQuantity-original.CancelledQuantity;
                    if(original!=null)checks.OriginalTradeKind=original.TradeKind;
                    result.Require(checks.OriginalOrderVerified,"정정·취소 원주문과 미체결 수량 확인 필요");
                    effective=intent.Kind==LiveOrderKind.Cancel?LiveOrderKind.Cancel:checks.OriginalTradeKind;
                }
                bool market=intent.Kind==LiveOrderKind.Cancel?MarketTime.ToKorea(nowUtc).DayOfWeek!=DayOfWeek.Saturday&&MarketTime.ToKorea(nowUtc).DayOfWeek!=DayOfWeek.Sunday&&MarketTime.ToKorea(nowUtc).TimeOfDay>=TimeSpan.FromHours(9)&&MarketTime.ToKorea(nowUtc).TimeOfDay<new TimeSpan(15,30,0):Rules.ValidTime(MarketTime.ToKorea(nowUtc));
                result.Require(market,"정규 주문 운영시간 확인 필요");
                checks.QuoteUtc=quoteReceivedUtc;checks.PriceRuleVerified=priceRuleVerified&&quote!=null&&quote.Price==intent.LimitPrice;
                checks.QuoteVerified=quoteIsLiveAndAdvanced&&quote!=null&&quote.Code==intent.Code&&MarketTime.CanUseQuote(quote,nowUtc)&&quoteReceivedUtc<=nowUtc&&nowUtc-quoteReceivedUtc<=TimeSpan.FromSeconds(45);
                if(intent.Kind!=LiveOrderKind.Cancel){result.Require(checks.QuoteVerified,"종목별 실제 장중 시세 확인 필요");result.Require(checks.PriceRuleVerified,"종목별 가격·호가단위 확인 필요");}
                checks.OperatingLimit=operatingRemainingLimit;
                if(effective==LiveOrderKind.Buy) {
                    bool cf=Fresh(cashTime,nowUtc,barrier);AccountOrderCapacity capacity;Stamp ct;
                    bool capacityFound=capacities.TryGetValue(CapacityKey(intent.Code,intent.LimitPrice),out capacity)&&capacityTimes.TryGetValue(CapacityKey(intent.Code,intent.LimitPrice),out ct);
                    bool capf=capacityFound&&Fresh(capacityTimes[CapacityKey(intent.Code,intent.LimitPrice)],nowUtc,barrier);
                    result.Require(cf&&cash!=null&&cash.Cash100Orderable>=0,"변경 후 현금100% 주문가능금액 확인 필요");
                    result.Require(capf&&capacity!=null&&capacity.Ratio==100,"현재 종목·가격의 현금100% 가능수량 확인 필요");
                    if(cf){checks.Cash100Orderable=cash.Cash100Orderable;stamps.Add(cashTime.Received);}
                    if(capf){checks.CapacityAmount=capacity.Amount;checks.CapacityQuantity=(int)capacity.Quantity;stamps.Add(capacityTimes[CapacityKey(intent.Code,intent.LimitPrice)].Received);}
                    checks.OrderableAmountVerified=cf&&capf&&cash.Cash100Orderable>=0;
                    decimal reserved=journalValid?journal.Orders.Where(x=>x!=null&&IsWorking(x)&&x.TradeKind==LiveOrderKind.Buy).Sum(x=>(x.Quantity-x.FilledQuantity-x.CancelledQuantity)*x.LimitPrice):0;
                    var amount=intent.Quantity*intent.LimitPrice;
                    result.Require(checks.OrderableAmountVerified&&intent.Quantity<=checks.CapacityQuantity&&amount<=checks.Cash100Orderable&&amount<=checks.CapacityAmount,"실제 주문가능금액·수량 범위 확인 필요");
                    result.Require(operatingRemainingLimit>0&&amount<=operatingRemainingLimit-reserved,"보유 원가·미체결 예약금을 반영한 운용한도 확인 필요");
                } else if(effective==LiveOrderKind.Sell) {
                    Sellable value;var position=holdings==null?null:holdings.SingleOrDefault(x=>x.Code==intent.Code);
                    bool sf=sellable.TryGetValue(intent.Code,out value)&&Fresh(value.Time,nowUtc,barrier)&&value.Validated&&position!=null&&position.CreditQty==0&&value.Quantity<=position.Quantity
                        &&holdingTime!=null&&value.Time.Started==holdingTime.Started&&value.Time.Received==holdingTime.Received;
                    Tuple<int,DateTime> ad;
                    if(sf&&balanceSellable.TryGetValue(intent.Code,out ad)&&ad.Item2<=nowUtc&&nowUtc-ad.Item2<=TimeSpan.FromSeconds(5)&&ad.Item1!=value.Quantity)sf=false;
                    checks.SellableQuantityVerified=sf;if(sf){checks.SellableQuantity=value.Quantity;stamps.Add(value.Time.Received);}
                    result.Require(sf,"현재 현물 매도가능수량 확인 필요");result.Require(sf&&intent.Quantity<=checks.SellableQuantity,"실제 매도가능수량 범위 확인 필요");
                }
                checks.SnapshotUtc=stamps.Count==0?DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc):stamps.Min();
                return result;
            }
        }
    }
}
