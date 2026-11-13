using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    // AA contains no trading date. Only the broker date read from SABA609 may
    // scope an AA event; a workstation date is never promoted to broker evidence.
    // Account filtering is performed by the dedicated AA transport before this
    // router. The journal must already be bound to that same account session.
    public sealed class BrokerAccountEventRouter {
        sealed class Buffered {
            public CashOrderEvent Value;
            public string BrokerDate;
        }
        sealed class FillRange {
            public string ExecutionId;
            public int Start,End;
        }
        const int MaximumPerBrokerOrder=16,MaximumBuffered=256;
        readonly LiveOrderJournal journal;
        readonly Action<DateTime,string> invalidate;
        readonly string accountFingerprint;
        readonly List<Buffered> buffered=new List<Buffered>();
        readonly Dictionary<string,List<FillRange>> observedFillRanges=new Dictionary<string,List<FillRange>>(StringComparer.Ordinal);
        string brokerDate;
        DateTime brokerDateReceivedUtc;
        bool draining;
        public event Action<LiveOrderRecord> Changed;
        public event Action<string> Unmatched;
        public event Action<string> Unknown;
        public int BufferedCount {get{return buffered.Count;}}

        public BrokerAccountEventRouter(LiveOrderJournal journal,Action<DateTime,string> invalidate) {
            this.journal=journal??throw new ArgumentNullException("journal");
            this.invalidate=invalidate??throw new ArgumentNullException("invalidate");
            accountFingerprint=journal.Snapshot.AccountFingerprint;
            if(string.IsNullOrEmpty(accountFingerprint))throw new InvalidOperationException("Bind the account before routing broker events.");
        }
        public void SetBrokerDate(string date,DateTime receivedUtc) {
            LiveOrderSafety.RequireUtc(receivedUtc);DateTime parsed;
            if(!DateTime.TryParseExact(date,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed))
                throw new ArgumentException("An actual broker trading date is required.");
            if(brokerDate!=null&&receivedUtc<brokerDateReceivedUtc)return;
            if(brokerDate!=null&&string.CompareOrdinal(date,brokerDate)<0)throw new InvalidOperationException("Broker trading date regressed.");
            brokerDate=date;brokerDateReceivedUtc=receivedUtc;
            DrainOnGatewayOrderChanged();
        }
        public void ObserveAA(CashOrderEvent value) {
            if(value==null)throw new ArgumentNullException("value");
            LiveOrderSafety.RequireUtc(value.ReceivedUtc);
            var state=State();
            var date=DateFor(value.ReceivedUtc);
            if(KnownDuplicate(value,date,state))return;
            // Every new account event invalidates cash/order/holding snapshots, even
            // when it belongs to a manual order absent from the local journal.
            invalidate(value.ReceivedUtc,"실계좌 AA 수신: 잔고·주문가능금액·미체결 재조회 필요");
            Expire(value.ReceivedUtc,state);
            state=State();
            var copy=Copy(value);
            if(date==null) {
                if(state.Orders.Any(o=>LiveOrderJournal.IsUnresolved(o)&&o.Status!=LiveOrderStatus.Prepared))
                    SignalUnknown("AA 거래일을 확인할 증권사 잔고 응답이 없습니다.");
                else if(Unmatched!=null)Unmatched("증권사 거래일 확인 전 실계좌 AA: 전체 계좌 재조회 필요");
                return;
            }
            if(!Route(copy,date,state)&&HasAwaitingAcknowledgement(state))Buffer(copy,date,state);
        }
        static bool KnownDuplicate(CashOrderEvent value,string date,LiveOrderJournalState state) {
            if(date==null||!value.IsExecution)return false;
            var matches=state.Orders.Where(o=>o.TradingDate==date&&SameBrokerNumber(o.BrokerOrderNumber,value.BrokerOrderNumber)).ToList();
            if(matches.Count!=1||!Valid(value,matches[0])||value.CumulativeFilledQuantity>matches[0].FilledQuantity)return false;
            // A terminal broker checkpoint already accounts for the whole order.
            // A delayed individual event inside that total is historical evidence,
            // not another quantity delta. Conflicts must still reach Route.
            if(matches[0].FillCheckpoint!=null)return CoveredByCheckpoint(value,matches[0],state);
            var execution=state.Executions.SingleOrDefault(e=>e.OrderId==matches[0].Id&&e.ExecutionId==value.ExecutionId);
            return execution!=null&&execution.Quantity==value.IndividualFillQuantity&&execution.Price==value.IndividualFillPrice;
        }
        static bool CoveredByCheckpoint(CashOrderEvent value,LiveOrderRecord order,LiveOrderJournalState state) {
            var checkpoint=order.FillCheckpoint;
            if(checkpoint==null||!LiveOrderJournal.HasTerminalFillCheckpoint(order,state.AccountFingerprint)||!value.IsExecution||!Valid(value,order)
                ||checkpoint.QueryName!="SABA231Q1"||checkpoint.AccountFingerprint!=state.AccountFingerprint
                ||checkpoint.TradingDate!=order.TradingDate||checkpoint.Code!=order.Code
                ||!SameBrokerNumber(checkpoint.BrokerOrderNumber,order.BrokerOrderNumber)
                ||checkpoint.TradeKind!=order.TradeKind||checkpoint.OrderQuantity!=order.Quantity
                ||checkpoint.FilledQuantity!=order.Quantity||checkpoint.LimitPrice!=order.LimitPrice
                ||order.FilledQuantity!=order.Quantity||order.CancelledQuantity!=0
                ||value.CumulativeFilledQuantity>checkpoint.FilledQuantity
                ||value.IndividualFillQuantity>value.CumulativeFilledQuantity
                ||value.CumulativeFilledQuantity+value.OpenQuantity>order.Quantity
                ||order.TradeKind==LiveOrderKind.Buy&&value.IndividualFillPrice>order.LimitPrice
                ||order.TradeKind==LiveOrderKind.Sell&&value.IndividualFillPrice<order.LimitPrice)return false;
            // Check each delayed individual as a subset of the authoritative
            // total. It need not have the aggregate average price, but its value
            // must leave a possible remainder within the submitted limit. Do
            // not sum unrelated late IDs without proven disjoint ranges, and
            // do not manufacture an average-price rounding tolerance.
            decimal total=checkpoint.FilledQuantity*checkpoint.AverageFillPrice;
            decimal remainderValue=total-value.IndividualFillQuantity*value.IndividualFillPrice;
            int remainderQuantity=checkpoint.FilledQuantity-value.IndividualFillQuantity;
            if(remainderQuantity==0?remainderValue!=0:remainderValue<=0
                ||order.TradeKind==LiveOrderKind.Buy&&remainderValue>remainderQuantity*order.LimitPrice
                ||order.TradeKind==LiveOrderKind.Sell&&remainderValue<remainderQuantity*order.LimitPrice)return false;
            var execution=state.Executions.SingleOrDefault(e=>e.OrderId==order.Id&&e.ExecutionId==value.ExecutionId);
            return execution==null||execution.Quantity==value.IndividualFillQuantity&&execution.Price==value.IndividualFillPrice;
        }
        public void DrainOnGatewayOrderChanged() {
            if(draining)return;draining=true;
            try {
                var state=State();
                var pending=buffered.ToList();buffered.Clear();
                foreach(var item in pending) {
                    state=State();
                    if(!Route(item.Value,item.BrokerDate,state)&&HasAwaitingAcknowledgement(state))
                        Buffer(item.Value,item.BrokerDate,state);
                }
            }finally{draining=false;}
        }
        LiveOrderJournalState State() {
            var state=journal.Snapshot;
            if(state.AccountFingerprint!=accountFingerprint)throw new InvalidOperationException("Broker event account session changed.");
            return state;
        }
        string DateFor(DateTime receiptUtc) {
            if(brokerDate==null||receiptUtc<brokerDateReceivedUtc)return null;
            // This only rejects a local day rollover/stale session. The value
            // returned is still exclusively the date supplied by SABA609 SO0.
            if(brokerDate!=MarketTime.ToKorea(receiptUtc).ToString("yyyyMMdd",CultureInfo.InvariantCulture))return null;
            return brokerDate;
        }
        static bool HasAwaitingAcknowledgement(LiveOrderJournalState state) {
            return state.Orders.Any(o=>string.IsNullOrEmpty(o.BrokerOrderNumber)
                &&(o.Status==LiveOrderStatus.Dispatching||o.Status==LiveOrderStatus.AwaitingAcknowledgement));
        }
        void Buffer(CashOrderEvent value,string date,LiveOrderJournalState state) {
            if(value.IsExecution&&buffered.Any(i=>i.BrokerDate==date&&SameExecution(i.Value,value)))return;
            if(buffered.Count>=MaximumBuffered||buffered.Count(i=>i.BrokerDate==date&&SameBrokerNumber(i.Value.BrokerOrderNumber,value.BrokerOrderNumber))>=MaximumPerBrokerOrder) {
                BlockPending(state,value.ReceivedUtc,"접수 전 AA 대기열 한도를 초과해 주문 대사가 필요합니다.");
                buffered.Clear();return;
            }
            buffered.Add(new Buffered{Value=value,BrokerDate=date});
        }
        static bool SameExecution(CashOrderEvent left,CashOrderEvent right) {
            return left.IsExecution&&SameBrokerNumber(left.BrokerOrderNumber,right.BrokerOrderNumber)&&left.ExecutionId==right.ExecutionId
                &&left.Code==right.Code&&SameOptionalBrokerNumber(left.OriginalBrokerOrderNumber,right.OriginalBrokerOrderNumber)
                &&left.ProductCode==right.ProductCode&&left.SideCode==right.SideCode&&left.TradeTypeCode==right.TradeTypeCode
                &&left.OrderStatusCode==right.OrderStatusCode&&left.OriginalResultCode==right.OriginalResultCode
                &&left.OrderQuantity==right.OrderQuantity&&left.OrderPrice==right.OrderPrice
                &&left.IndividualFillQuantity==right.IndividualFillQuantity&&left.IndividualFillPrice==right.IndividualFillPrice
                &&left.CumulativeFilledQuantity==right.CumulativeFilledQuantity&&left.OpenQuantity==right.OpenQuantity&&left.Clock==right.Clock;
        }
        void Expire(DateTime utc,LiveOrderJournalState state) {
            if(!buffered.Any(i=>utc>=i.Value.ReceivedUtc&&utc-i.Value.ReceivedUtc>TimeSpan.FromSeconds(30)))return;
            buffered.RemoveAll(i=>utc>=i.Value.ReceivedUtc&&utc-i.Value.ReceivedUtc>TimeSpan.FromSeconds(30));
            BlockPending(state,utc,"접수 전 AA 대기 시간이 지나 주문 대사가 필요합니다.");
        }
        void BlockPending(LiveOrderJournalState state,DateTime utc,string reason) {
            foreach(var order in state.Orders.Where(o=>string.IsNullOrEmpty(o.BrokerOrderNumber)
                &&(o.Status==LiveOrderStatus.Dispatching||o.Status==LiveOrderStatus.AwaitingAcknowledgement)))
                MarkUnknown(order,utc,false);
            SignalUnknown(reason);
        }
        // False means an unknown order may be waiting for its separate TR ACK.
        // A symbol, price or receipt time is never used to attach an order.
        bool Route(CashOrderEvent value,string date,LiveOrderJournalState state) {
            var matches=state.Orders.Where(o=>o.TradingDate==date&&SameBrokerNumber(o.BrokerOrderNumber,value.BrokerOrderNumber)).ToList();
            if(matches.Count>1) {
                foreach(var ambiguous in matches)MarkUnknown(ambiguous,value.ReceivedUtc,false,ValidProfile(value,ambiguous));
                SignalUnknown("동일 거래일의 증권사 주문번호 정규화 결과가 중복돼 주문 대사가 필요합니다.");return true;
            }
            var order=matches.SingleOrDefault();
            if(order==null) {
                if(!HasAwaitingAcknowledgement(state)&&Unmatched!=null)Unmatched("로컬 주문번호와 일치하지 않는 실계좌 AA: 전체 계좌 재조회 필요");
                return false;
            }
            // Original-result records are echoes of amend/cancel activity. They
            // are not a second execution or confirmation for the child request.
            if(value.OriginalResultCode=="1"&&ValidProfile(value,order))return true;
            if(!Valid(value,order)) {
                MarkUnknown(order,value.ReceivedUtc,true,order.FillCheckpoint!=null||ValidProfile(value,order));SignalUnknown("AA의 종목·매매구분·수량·필수 필드가 주문 기록과 다릅니다.");return true;
            }
            try {
                var utc=ObservationUtc(value.ReceivedUtc,order);
                if(value.IsExecution) {
                    if(order.FillCheckpoint!=null) {
                        if(!CoveredByCheckpoint(value,order,state))
                            throw new InvalidOperationException("Individual execution contradicts the terminal broker checkpoint.");
                        // Do not turn an aggregate checkpoint into manufactured
                        // per-execution records or count delayed fills twice.
                        return true;
                    }
                    var previous=state.Executions.SingleOrDefault(e=>e.OrderId==order.Id&&e.ExecutionId==value.ExecutionId);
                    List<FillRange> ranges;
                    if(!observedFillRanges.TryGetValue(order.Id,out ranges)){ranges=new List<FillRange>();observedFillRanges[order.Id]=ranges;}
                    int start=value.CumulativeFilledQuantity-value.IndividualFillQuantity;
                    if(previous==null) {
                        if(ranges.Any(r=>start<r.End&&value.CumulativeFilledQuantity>r.Start))
                            throw new InvalidOperationException("Overlapping broker individual-fill ranges.");
                        // A new raw execution ID covering already-accounted volume
                        // may be a differently formatted duplicate. Older fills
                        // can be imported only when this session observed all
                        // existing individual ranges and proves no overlap.
                        bool allRangesKnown=state.Executions.Where(e=>e.OrderId==order.Id).All(e=>ranges.Any(r=>r.ExecutionId==e.ExecutionId&&r.End-r.Start==e.Quantity));
                        if(value.CumulativeFilledQuantity<order.FilledQuantity+value.IndividualFillQuantity&&!allRangesKnown)
                            throw new InvalidOperationException("Ambiguous older individual execution.");
                    }
                    bool applied=order.Status==LiveOrderStatus.Unknown
                        ?journal.ImportRecoveryExecution(order.Id,order.BrokerOrderNumber,value.ExecutionId,value.IndividualFillQuantity,value.IndividualFillPrice,utc)
                        :journal.RecordExecution(order.BrokerOrderNumber,value.ExecutionId,value.IndividualFillQuantity,value.IndividualFillPrice,utc);
                    if(applied)ranges.Add(new FillRange{ExecutionId=value.ExecutionId,Start=start,End=value.CumulativeFilledQuantity});
                    var current=State().Orders.Single(o=>o.Id==order.Id);
                    bool gap=value.CumulativeFilledQuantity>current.FilledQuantity;
                    if(gap)MarkUnknown(current,utc,false,true);
                    if(applied||gap)NotifyChanged(order.Id);
                    if(gap)SignalUnknown("AA 누적 체결과 고유 체결번호 기록에 차이가 있어 추가 대사가 필요합니다.");
                }else if(value.IsAmendmentConfirmation||value.IsCancellationConfirmation) {
                    var originals=state.Orders.Where(o=>o.TradingDate==date&&SameBrokerNumber(o.BrokerOrderNumber,value.OriginalBrokerOrderNumber)).ToList();
                    var original=originals.Count==1?originals[0]:null;
                    if(original==null||original.Code!=order.Code||original.TradeKind!=order.TradeKind
                        ||order.OriginalBrokerOrderNumber!=original.BrokerOrderNumber||value.OrderStatusCode!="02")
                        throw new InvalidOperationException("Unmatched broker confirmation.");
                    utc=ObservationUtc(utc,original);
                    if(value.IsCancellationConfirmation) {
                        if(order.Kind!=LiveOrderKind.Cancel)throw new InvalidOperationException("Cancel confirmation kind mismatch.");
                        journal.ConfirmCancellation(original.BrokerOrderNumber,order.BrokerOrderNumber,"AA05",value.ConfirmedQuantity,utc);
                    }else {
                        if(order.Kind!=LiveOrderKind.Amend)throw new InvalidOperationException("Amend confirmation kind mismatch.");
                        journal.ConfirmAmendment(original.BrokerOrderNumber,order.BrokerOrderNumber,"AA04",value.ConfirmedQuantity,value.OrderPrice,utc);
                    }
                    NotifyChanged(original.Id);NotifyChanged(order.Id);
                }else if(value.IsRejection) {
                    if(value.OrderStatusCode!="03")throw new InvalidOperationException("Missing rejection status.");
                    journal.RecordBrokerRejection(order.BrokerOrderNumber,date,value.ProcessingCode,utc);NotifyChanged(order.Id);
                }else if(value.ProcessingCode!="00")throw new InvalidOperationException("Unsupported account event.");
                return true;
            }catch(InvalidOperationException) {
                MarkUnknown(order,value.ReceivedUtc,true,true);SignalUnknown("AA와 주문 기록을 일관되게 반영하지 못해 추가 대사가 필요합니다.");return true;
            }catch(ArgumentException) {
                MarkUnknown(order,value.ReceivedUtc,true,true);SignalUnknown("AA 필수 체결·확인 값이 유효하지 않아 추가 대사가 필요합니다.");return true;
            }
        }
        static bool Valid(CashOrderEvent value,LiveOrderRecord order) {
            if(!ValidProfile(value,order)||value.OrderQuantity!=order.Quantity
                ||value.OriginalResultCode!="0"&&value.OriginalResultCode!="1"
                ||!SameOptionalBrokerNumber(value.OriginalBrokerOrderNumber,order.OriginalBrokerOrderNumber)
                ||value.CumulativeFilledQuantity<0||value.CumulativeFilledQuantity>order.Quantity||value.OpenQuantity<0||value.OpenQuantity>order.Quantity)
                return false;
            if(value.IsExecution) {
                DateTime clock;
                return !string.IsNullOrEmpty(value.ExecutionId)&&value.ExecutionId.Length<=64
                    &&!value.ExecutionId.All(c=>c=='0')&&value.ExecutionId.All(c=>char.IsLetterOrDigit(c)||c=='-'||c=='_')
                    &&value.IndividualFillQuantity>0&&value.IndividualFillQuantity<=value.CumulativeFilledQuantity
                    &&value.IndividualFillPrice>0&&value.OrderPrice==order.LimitPrice
                    &&DateTime.TryParseExact(value.Clock,"HHmmss",CultureInfo.InvariantCulture,DateTimeStyles.None,out clock)
                    &&(string.IsNullOrEmpty(value.OriginalClock)||RealtimeRecordParser.NormalizeExecutionClock(value.OriginalClock)==value.Clock);
            }
            return true;
        }
        static bool ValidProfile(CashOrderEvent value,LiveOrderRecord order) {
            return value.ProductCode=="01"&&value.Code==order.Code
                &&value.SideCode==(order.TradeKind==LiveOrderKind.Buy?"02":"01")
                &&value.TradeTypeCode==(order.TradeKind==LiveOrderKind.Buy?"02":"01");
        }
        static bool SameBrokerNumber(string left,string right) {
            var key=LiveOrderProtocol.BrokerNumberKey(left);
            return key!=null&&key==LiveOrderProtocol.BrokerNumberKey(right);
        }
        static bool SameOptionalBrokerNumber(string left,string right) {
            return string.IsNullOrEmpty(left)&&string.IsNullOrEmpty(right)||SameBrokerNumber(left,right);
        }
        static DateTime ObservationUtc(DateTime receivedUtc,LiveOrderRecord order) {
            // OriginalClock preserves provider precision in CashOrderEvent;
            // Clock is its validated normalization. This journal time is an observation time, with no
            // fractional-second rounding or claim that PC time is broker time.
            return receivedUtc<order.UpdatedUtc?order.UpdatedUtc:receivedUtc;
        }
        void MarkUnknown(LiveOrderRecord order,DateTime utc,bool includeOriginal,bool verifiedIdentity=false) {
            if(verifiedIdentity)journal.MarkBrokerEvidenceUnknown(order.Id,ObservationUtc(utc,order));
            else if(LiveOrderJournal.IsUnresolved(order))journal.MarkUnknown(order.Id,ObservationUtc(utc,order));
            if(includeOriginal&&!string.IsNullOrEmpty(order.OriginalBrokerOrderNumber)) {
                var original=State().Orders.SingleOrDefault(o=>o.TradingDate==order.TradingDate&&o.BrokerOrderNumber==order.OriginalBrokerOrderNumber);
                if(original!=null&&original.Code==order.Code&&original.TradeKind==order.TradeKind&&LiveOrderJournal.IsUnresolved(original))journal.MarkUnknown(original.Id,ObservationUtc(utc,original));
            }
        }
        void NotifyChanged(string id){if(Changed!=null)Changed(State().Orders.Single(o=>o.Id==id));}
        void SignalUnknown(string message){if(Unknown!=null)Unknown(message);}
        static CashOrderEvent Copy(CashOrderEvent value) {
            return new CashOrderEvent{ProcessingCode=value.ProcessingCode,Code=value.Code,BrokerOrderNumber=value.BrokerOrderNumber,
                OriginalBrokerOrderNumber=value.OriginalBrokerOrderNumber,ExecutionId=value.ExecutionId,Clock=value.Clock,OriginalClock=value.OriginalClock,ProductCode=value.ProductCode,
                SideCode=value.SideCode,TradeTypeCode=value.TradeTypeCode,OrderStatusCode=value.OrderStatusCode,OriginalResultCode=value.OriginalResultCode,
                OrderQuantity=value.OrderQuantity,CumulativeFilledQuantity=value.CumulativeFilledQuantity,IndividualFillQuantity=value.IndividualFillQuantity,
                ConfirmedQuantity=value.ConfirmedQuantity,OpenQuantity=value.OpenQuantity,OrderPrice=value.OrderPrice,IndividualFillPrice=value.IndividualFillPrice,ReceivedUtc=value.ReceivedUtc};
        }
    }
}
