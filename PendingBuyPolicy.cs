using System;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public enum PendingBuyState { NotNeeded, Waiting, RefreshRequired, Blocked, CancelEligible }

    // A decision is a plan for a new, independently checked cancellation. It
    // neither changes reservations nor infers a fill or cancellation.
    public sealed class PendingBuyDecision {
        public PendingBuyState State {get;internal set;}
        public string Status {get;internal set;}
        public string OriginalOrderId {get;internal set;}
        public string OriginalBrokerOrderNumber {get;internal set;}
        public string Code {get;internal set;}
        public string ClientKey {get;internal set;}
        public int RemainingQuantity {get;internal set;}
        public DateTime? DueUtc {get;internal set;}
        public DateTime? ExpiresUtc {get;internal set;}
        public LiveOrderIntent Intent {get;internal set;}
    }

    public static class PendingBuyPolicy {
        public const string CancelKeyPrefix="timeout-cancel-";
        public const string ReentryKeyPrefix="timeout-reentry-";
        public const int DefaultTimeoutSeconds=120;
        public static readonly TimeSpan MaximumSnapshotAge=TimeSpan.FromSeconds(5);

        static PendingBuyDecision Result(PendingBuyState state,string status) {
            return new PendingBuyDecision{State=state,Status=status};
        }
        static bool Absent(string number){return string.IsNullOrEmpty(number)||number.All(character=>character=='0');}
        static bool Whole(decimal quantity){return quantity>=0&&quantity<=int.MaxValue&&quantity==decimal.Truncate(quantity);}
        static bool Transit(LiveOrderRecord order){return order.Status==LiveOrderStatus.Unknown||order.Status==LiveOrderStatus.Prepared
            ||order.Status==LiveOrderStatus.Dispatching||order.Status==LiveOrderStatus.AwaitingAcknowledgement;}
        static bool Accepted(LiveOrderRecord order) {
            var evidence=order.ResponseEvidence;
            return order.RequestId.HasValue&&order.RequestId.Value>0&&evidence!=null&&evidence.RequestId==order.RequestId.Value
                &&evidence.Disposition==OrderResponseDisposition.Accepted&&!evidence.ProviderError&&!evidence.ParseFailed
                &&LiveOrderProtocol.ValidBrokerNumber(order.BrokerOrderNumber)
                &&LiveOrderProtocol.BrokerNumbersEqual(evidence.BrokerOrderNumber,order.BrokerOrderNumber)
                &&(evidence.Layout==OrderResponseLayout.Legacy&&LiveOrderProtocol.BrokerNumbersEqual(evidence.Slot0OrderNumber,order.BrokerOrderNumber)
                    ||evidence.Layout==OrderResponseLayout.Modern&&LiveOrderProtocol.BrokerNumbersEqual(evidence.Slot1OrderNumber,order.BrokerOrderNumber)
                        &&Absent(evidence.Slot0OrderNumber)&&Absent(evidence.Slot2OrderNumber));
        }
        static bool OriginalBuy(LiveOrderRecord order) {
            return order.Kind==LiveOrderKind.Buy&&order.TradeKind==LiveOrderKind.Buy&&Absent(order.OriginalBrokerOrderNumber)
                &&order.Quantity>0&&order.Quantity<=1000000&&order.LimitPrice>0&&order.LimitPrice==decimal.Truncate(order.LimitPrice)
                &&order.FilledQuantity>=0&&order.CancelledQuantity>=0
                &&(long)order.FilledQuantity+order.CancelledQuantity<=order.Quantity&&Accepted(order);
        }
        static bool StrategyOwned(LiveOrderJournalState journal,LiveOrderRecord order) {
            var daily="entry-"+order.TradingDate+"-"+order.Code;
            if(order.ClientKey==daily)return true;
            // Rejected strategy orders use separate automatic retry keys. A
            // broker-only/manual order has no matching local strategy lineage.
            if(journal.Orders.Any(root=>root.Code==order.Code&&root.TradingDate==order.TradingDate&&root.Id!=order.Id
                &&order.ClientKey==ReentryKeyPrefix+root.Id+"-1"&&root.Status==LiveOrderStatus.Cancelled
                &&root.Kind==LiveOrderKind.Buy&&root.TradeKind==LiveOrderKind.Buy
                &&(root.ClientKey==daily||journal.Orders.Any(anchor=>anchor.ClientKey==daily&&anchor.Code==root.Code&&anchor.TradingDate==root.TradingDate
                    &&AutoOrderRetry.IsDefiniteUnacceptedRejection(anchor)&&Enumerable.Range(1,AutoOrderRetry.MaximumAttemptsPerDay)
                        .Any(attempt=>root.ClientKey==AutoOrderRetry.KeyPrefix+anchor.Id+"-"+attempt.ToString(CultureInfo.InvariantCulture))))))return true;
            return journal.Orders.Any(root=>root.ClientKey==daily&&root.Code==order.Code&&root.TradingDate==order.TradingDate
                &&root.Kind==LiveOrderKind.Buy&&root.TradeKind==LiveOrderKind.Buy&&AutoOrderRetry.IsDefiniteUnacceptedRejection(root)
                &&Enumerable.Range(1,AutoOrderRetry.MaximumAttemptsPerDay).Any(attempt=>order.ClientKey==AutoOrderRetry.KeyPrefix+root.Id+"-"+attempt.ToString(CultureInfo.InvariantCulture)));
        }
        static bool Normal(BrokerOrderRow row) {
            return row!=null&&!row.HasRejectionConflict
                &&(row.ConfirmationKind==BrokerConfirmationKind.Normal||row.ConfirmationKind==BrokerConfirmationKind.StatusCorroborated)
                &&(row.QueryProfile==BrokerOrderQueryProfile.Unverified||row.HasRecognizedModernSchema&&row.ModernVenueCompatible);
        }
        static bool ExecutionMatches(LiveOrderJournalState journal,LiveOrderRecord order,DateTime utc) {
            var fills=journal.Executions.Where(value=>value.OrderId==order.Id).ToArray();
            return fills.All(value=>!string.IsNullOrWhiteSpace(value.ExecutionId)&&value.Quantity>0&&value.Price>0&&value.Price<=order.LimitPrice
                &&value.ExecutedUtc.Kind==DateTimeKind.Utc&&value.ExecutedUtc>=order.CreatedUtc&&value.ExecutedUtc<=utc
                &&value.TradingDate==order.TradingDate&&LiveOrderProtocol.BrokerNumbersEqual(value.BrokerOrderNumber,order.BrokerOrderNumber))
                &&fills.Select(value=>value.ExecutionId).Distinct(StringComparer.Ordinal).Count()==fills.Length
                &&fills.Sum(value=>(long)value.Quantity)==order.FilledQuantity;
        }
        static bool SnapshotCurrent(BrokerOrdersSnapshot snapshot,string accountKey,string day,DateTime barrier,DateTime utc) {
            return snapshot!=null&&snapshot.RequestScopeVerified&&snapshot.MatchesRequestAccountKey(accountKey)&&snapshot.TradingDate==day
                &&snapshot.RequestStartedUtc>barrier&&snapshot.ReceivedUtc<=utc&&utc-snapshot.ReceivedUtc<=MaximumSnapshotAge
                &&snapshot.Orders!=null&&!snapshot.Orders.Any(value=>value==null);
        }
        // The caller supplies the journal already bound to its current account;
        // requestAccountKey independently scopes the broker response.
        public static PendingBuyDecision Evaluate(LiveOrderJournalState journal,BrokerOrdersSnapshot snapshot,string requestAccountKey,
            DateTime utc,int timeoutSeconds=DefaultTimeoutSeconds) {
            if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC pending-buy policy time required.");
            if(timeoutSeconds<30||timeoutSeconds>1800)throw new ArgumentOutOfRangeException("timeoutSeconds");
            if(journal==null||journal.Mode!="RealAccount"||string.IsNullOrEmpty(journal.AccountFingerprint)||journal.Orders==null||journal.Executions==null
                ||journal.Orders.Any(value=>value==null)||journal.Executions.Any(value=>value==null)
                ||journal.Orders.GroupBy(value=>value.Id).Any(group=>string.IsNullOrEmpty(group.Key)||group.Count()!=1)
                ||journal.Orders.GroupBy(value=>value.ClientKey).Any(group=>string.IsNullOrEmpty(group.Key)||group.Count()!=1))
                return Result(PendingBuyState.Blocked,"미체결 관리: 계좌·주문 기록 확인 필요");
            if(journal.Orders.Any(Transit))return Result(PendingBuyState.Blocked,"미확정·전송 중 주문 대사 대기 / 자동 취소 재전송 없음");
            var day=MarketTime.ToKorea(utc).ToString("yyyyMMdd",CultureInfo.InvariantCulture);
            var working=journal.Orders.Where(value=>value.TradingDate==day&&value.Kind==LiveOrderKind.Buy&&value.TradeKind==LiveOrderKind.Buy
                &&(value.Status==LiveOrderStatus.Working||value.Status==LiveOrderStatus.PartiallyFilled)&&StrategyOwned(journal,value))
                .OrderBy(value=>value.CreatedUtc).ThenBy(value=>value.Id,StringComparer.Ordinal).ToArray();
            if(working.Length==0)return Result(PendingBuyState.NotNeeded,"대기시간 관리 대상 매수 미체결 없음");
            PendingBuyDecision firstWait=null,firstBlocked=null,firstRefresh=null;
            foreach(var root in working) {
                var decision=Result(PendingBuyState.Blocked,"매수 미체결 원주문·체결 근거 확인 필요");
                decision.OriginalOrderId=root.Id;decision.OriginalBrokerOrderNumber=root.BrokerOrderNumber;decision.Code=root.Code;
                if(!OriginalBuy(root)||Rules.Code(root.Code)!=root.Code||string.IsNullOrEmpty(root.Code)
                    ||root.CreatedUtc.Kind!=DateTimeKind.Utc||root.UpdatedUtc.Kind!=DateTimeKind.Utc||root.CreatedUtc>root.UpdatedUtc||root.UpdatedUtc>utc
                    ||MarketTime.ToKorea(root.CreatedUtc).ToString("yyyyMMdd",CultureInfo.InvariantCulture)!=day
                    ||root.FillCheckpoint!=null||!ExecutionMatches(journal,root,utc)
                    ||root.Status==LiveOrderStatus.Working&&root.FilledQuantity!=0
                    ||root.Status==LiveOrderStatus.PartiallyFilled&&root.FilledQuantity<=0) {
                    if(firstBlocked==null)firstBlocked=decision;continue;
                }
                decision.ClientKey=CancelKeyPrefix+root.Id+"-1";
                if(decision.ClientKey.Length>128||journal.Orders.Any(value=>value.ClientKey==decision.ClientKey
                    ||value.TradingDate==day&&(value.Kind==LiveOrderKind.Cancel||value.Kind==LiveOrderKind.Amend)
                        &&LiveOrderProtocol.BrokerNumbersEqual(value.OriginalBrokerOrderNumber,root.BrokerOrderNumber)&&LiveOrderJournal.IsUnresolved(value))) {
                    decision.Status="매수 미체결 취소·정정 처리 기록 있음 / 중복 취소 없음";
                    if(firstBlocked==null)firstBlocked=decision;continue;
                }
                decision.DueUtc=root.CreatedUtc.AddSeconds(timeoutSeconds);
                if(utc<decision.DueUtc.Value) {
                    decision.State=PendingBuyState.Waiting;decision.Status="매수 미체결 대기 / "+Math.Ceiling((decision.DueUtc.Value-utc).TotalSeconds).ToString(CultureInfo.InvariantCulture)+"초 후 잔량 취소 검증";
                    if(firstWait==null)firstWait=decision;continue;
                }
                var barrier=root.UpdatedUtc>journal.LastBrokerChangeUtc?root.UpdatedUtc:journal.LastBrokerChangeUtc;
                if(!SnapshotCurrent(snapshot,requestAccountKey,day,barrier,utc)) {
                    decision.State=PendingBuyState.RefreshRequired;decision.Status="매수 대기시간 경과 / 최신 계좌·주문 조회 대기";
                    if(firstRefresh==null)firstRefresh=decision;continue;
                }
                var rows=snapshot.Orders.Where(value=>LiveOrderProtocol.BrokerNumbersEqual(value.OrderNumber,root.BrokerOrderNumber)).ToArray();
                int remaining=root.Quantity-root.FilledQuantity-root.CancelledQuantity;
                if(rows.Length!=1||remaining<=0) {if(firstBlocked==null)firstBlocked=decision;continue;}
                var row=rows[0];
                if(!Normal(row)||row.Code!=root.Code||row.SideCode!="2"||row.ProductCode!="01"||row.CreditCode!="00"
                    ||row.AmendCancelCode!="0"||!Absent(row.OriginalOrderNumber)||row.Quantity!=root.Quantity||row.LimitPrice!=root.LimitPrice
                    ||!Whole(row.FilledQuantity)||!Whole(row.OpenQuantity)||row.FilledQuantity!=root.FilledQuantity||row.OpenQuantity!=remaining
                    ||row.ConfirmedQuantity!=root.CancelledQuantity||row.AutoCancelledQuantity!=0
                    ||snapshot.Orders.Any(value=>LiveOrderProtocol.BrokerNumbersEqual(value.OriginalOrderNumber,root.BrokerOrderNumber))) {
                    if(firstBlocked==null)firstBlocked=decision;continue;
                }
                decision.State=PendingBuyState.CancelEligible;decision.RemainingQuantity=remaining;decision.ExpiresUtc=snapshot.ReceivedUtc+MaximumSnapshotAge;
                decision.Status="매수 대기시간 경과 / 미체결 "+remaining.ToString(CultureInfo.InvariantCulture)+"주 취소 검증";
                decision.Intent=new LiveOrderIntent{Kind=LiveOrderKind.Cancel,Code=root.Code,ClientKey=decision.ClientKey,
                    OriginalBrokerOrderNumber=root.BrokerOrderNumber,Quantity=remaining,LimitPrice=root.LimitPrice};
                return decision;
            }
            return firstRefresh??firstBlocked??firstWait??Result(PendingBuyState.NotNeeded,"대기시간 관리 대상 매수 미체결 없음");
        }

        // Full cancellation is proved by the durable confirmation transition,
        // never by an accepted cancellation response. This merely permits one
        // new strategy preflight; the caller must recheck the current signal,
        // quote, holdings, budget and broker capacity before sending a new buy.
        public static AutoRetryDecision EvaluateReentry(LiveOrderJournalState journal,string strategyKey,string code,string accountKey,
            BrokerOrdersSnapshot snapshot,DateTime utc) {
            if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC cancellation reentry time required.");
            var result=new AutoRetryDecision{State=AutoRetryState.NotNeeded,Status="시간초과 취소 후 재평가 대상 없음"};
            if(journal==null||journal.Mode!="RealAccount"||string.IsNullOrEmpty(journal.AccountFingerprint)||journal.Orders==null
                ||journal.Executions==null||journal.ConfirmationIds==null||journal.Orders.Any(value=>value==null)||journal.Executions.Any(value=>value==null)) {
                result.State=AutoRetryState.Blocked;result.Status="취소 후 재평가: 주문·계좌 기록 확인 필요";return result;
            }
            var day=MarketTime.ToKorea(utc).ToString("yyyyMMdd",CultureInfo.InvariantCulture);
            if(string.IsNullOrEmpty(code)||Rules.Code(code)!=code||strategyKey!="entry-"+day+"-"+code)
                throw new ArgumentException("A current-day strategy entry key is required.");
            var roots=journal.Orders.Where(value=>value.TradingDate==day&&value.Code==code&&value.Kind==LiveOrderKind.Buy
                &&value.TradeKind==LiveOrderKind.Buy&&value.Status==LiveOrderStatus.Cancelled&&StrategyOwned(journal,value)
                &&!value.ClientKey.StartsWith(ReentryKeyPrefix,StringComparison.Ordinal)).ToArray();
            var possible=roots.Where(root=>journal.Orders.Any(value=>value.ClientKey==CancelKeyPrefix+root.Id+"-1")).ToArray();
            if(possible.Length==0)return result;
            result.State=AutoRetryState.Blocked;result.Status="취소 확정·무체결 확인 대기 / 접수·불명 주문 재전송 없음";
            if(possible.Length!=1||journal.Orders.Any(Transit)||journal.Orders.Any(value=>value.Code==code&&LiveOrderJournal.IsUnresolved(value)))return result;
            var original=possible[0];var key=ReentryKeyPrefix+original.Id+"-1";
            if(string.IsNullOrEmpty(original.Id)||key.Length>128)return result;
            if(journal.Orders.Any(value=>value.TradingDate==day&&value.Code==code&&value.Kind==LiveOrderKind.Buy
                &&(value.ClientKey==key||value.OriginalClientKey==key||value.ClientKey!=null&&value.ClientKey.StartsWith(ReentryKeyPrefix,StringComparison.Ordinal)))) {
                result.Status="시간초과 취소 후 당일 1회 재평가 한도 도달";return result;
            }
            var cancellations=journal.Orders.Where(value=>value.ClientKey==CancelKeyPrefix+original.Id+"-1").ToArray();
            if(cancellations.Length!=1||!OriginalBuy(original)||original.FilledQuantity!=0||original.CancelledQuantity!=original.Quantity
                ||original.FillCheckpoint!=null||journal.Executions.Any(value=>value.OrderId==original.Id)
                ||original.CreatedUtc.Kind!=DateTimeKind.Utc||original.UpdatedUtc.Kind!=DateTimeKind.Utc||original.CreatedUtc>original.UpdatedUtc
                ||original.UpdatedUtc>utc||MarketTime.ToKorea(original.CreatedUtc).ToString("yyyyMMdd",CultureInfo.InvariantCulture)!=day)return result;
            var cancellation=cancellations[0];
            if(cancellation.Kind!=LiveOrderKind.Cancel||cancellation.TradeKind!=LiveOrderKind.Buy||cancellation.Status!=LiveOrderStatus.Cancelled
                ||cancellation.TradingDate!=day||cancellation.Code!=code||cancellation.Quantity!=original.Quantity||cancellation.FilledQuantity!=0
                ||cancellation.CancelledQuantity!=0||!LiveOrderProtocol.BrokerNumbersEqual(cancellation.OriginalBrokerOrderNumber,original.BrokerOrderNumber)
                ||!Accepted(cancellation)||cancellation.CreatedUtc.Kind!=DateTimeKind.Utc||cancellation.UpdatedUtc.Kind!=DateTimeKind.Utc
                ||cancellation.CreatedUtc<original.CreatedUtc||cancellation.CreatedUtc>cancellation.UpdatedUtc||cancellation.UpdatedUtc>utc)return result;
            var confirmation=day+"|"+LiveOrderProtocol.BrokerNumberKey(cancellation.BrokerOrderNumber)+"|AA05|"+original.Quantity.ToString(CultureInfo.InvariantCulture);
            if(!journal.ConfirmationIds.Contains(confirmation))return result;
            var barrier=new[]{original.UpdatedUtc,cancellation.UpdatedUtc,journal.LastBrokerChangeUtc}.Max();
            if(!SnapshotCurrent(snapshot,accountKey,day,barrier,utc)) {
                result.State=AutoRetryState.RefreshRequired;result.Status="취소 확정 후 최신 계좌·주문 조회 대기";return result;
            }
            var rows=snapshot.Orders.Where(value=>LiveOrderProtocol.BrokerNumbersEqual(value.OrderNumber,original.BrokerOrderNumber)).ToArray();
            if(rows.Length!=1||!Normal(rows[0]))return result;
            var row=rows[0];
            if(row.Code!=code||row.SideCode!="2"||row.ProductCode!="01"||row.CreditCode!="00"||row.AmendCancelCode!="0"
                ||!Absent(row.OriginalOrderNumber)||row.Quantity!=original.Quantity||row.LimitPrice!=original.LimitPrice
                ||row.FilledQuantity!=0||row.OpenQuantity!=0||row.ConfirmedQuantity!=0||row.AutoCancelledQuantity!=0
                ||snapshot.Orders.Any(value=>value.Code==code&&value.OpenQuantity>0))return result;
            result.State=AutoRetryState.Eligible;result.ClientKey=key;result.PreviousOrderId=original.Id;result.Attempt=1;
            result.Status="미체결 전량 취소 확정 / 현재 전략·시세·잔고·한도 재평가 1회";return result;
        }
    }
}
