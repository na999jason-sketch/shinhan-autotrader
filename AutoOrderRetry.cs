using System;
using System.Globalization;
using System.Linq;

namespace ShinhanNativeTrader {
    public enum AutoRetryState { NotNeeded,Eligible,Waiting,RefreshRequired,Blocked }

    // A plan does not send an order or reuse a prior preflight. The caller must
    // reevaluate the current strategy and run every fresh financial check.
    public sealed class AutoRetryDecision {
        public AutoRetryState State {get;internal set;}
        public string ClientKey {get;internal set;}
        public string PreviousOrderId {get;internal set;}
        public int Attempt {get;internal set;}
        public bool LegacyReevaluation {get;internal set;}
        public DateTime? NextAttemptUtc {get;internal set;}
        public string Status {get;internal set;}
    }

    public static class AutoOrderRetry {
        public const string KeyPrefix="auto-retry-";
        public const string LegacyKeyPrefix="auto-legacy-";
        public const int MaximumAttemptsPerDay=3;
        public static TimeSpan Cooldown(int attempt) {
            if(attempt<1||attempt>MaximumAttemptsPerDay)throw new ArgumentOutOfRangeException("attempt");
            return TimeSpan.FromSeconds(30*(1<<(attempt-1)));
        }
        static AutoRetryDecision Result(AutoRetryState state,string status,string key=null) {
            return new AutoRetryDecision{State=state,Status=status,ClientKey=key};
        }
        static bool Zero(string value){return !string.IsNullOrEmpty(value)&&value.All(character=>character=='0');}
        static bool EmptyOrZero(string value){return string.IsNullOrEmpty(value)||Zero(value);}
        static bool ReferenceValue(string value){return string.IsNullOrEmpty(value)||value.Length<=32&&value.All(character=>character>='0'&&character<='9');}
        static bool RetryRecord(LiveOrderRecord value) {
            return value.ClientKey!=null&&(value.ClientKey.StartsWith(KeyPrefix,StringComparison.Ordinal)
                ||value.ClientKey.StartsWith(LegacyKeyPrefix,StringComparison.Ordinal)
                ||value.ClientKey.StartsWith("retry-",StringComparison.Ordinal));
        }
        // A provider error alone is not rejection evidence. All identified
        // venue fields must explicitly be zero, followed by fresh healthy
        // account evidence and a completely new caller preflight.
        public static bool IsDefiniteUnacceptedRejection(LiveOrderRecord order) {
            if(order==null||order.Status!=LiveOrderStatus.Rejected
                ||(order.Kind!=LiveOrderKind.Buy&&order.Kind!=LiveOrderKind.Sell)||order.Kind!=order.TradeKind
                ||!string.IsNullOrEmpty(order.BrokerOrderNumber)||!EmptyOrZero(order.OriginalBrokerOrderNumber)
                ||order.Quantity<=0||order.LimitPrice<=0||order.FilledQuantity!=0||order.CancelledQuantity!=0||order.FillCheckpoint!=null
                ||!order.RequestId.HasValue||order.RequestId.Value<=0)return false;
            var evidence=order.ResponseEvidence;
            if(evidence==null||evidence.RequestId!=order.RequestId.Value||evidence.Disposition!=OrderResponseDisposition.Rejected
                ||evidence.ParseFailed||!string.IsNullOrEmpty(evidence.BrokerOrderNumber))return false;
            if(evidence.Layout==OrderResponseLayout.Legacy)return Zero(evidence.Slot0OrderNumber)&&ReferenceValue(evidence.Slot1OrderNumber);
            return evidence.Layout==OrderResponseLayout.Modern&&Zero(evidence.Slot0OrderNumber)&&Zero(evidence.Slot1OrderNumber)
                &&Zero(evidence.Slot2OrderNumber)&&ReferenceValue(evidence.Slot3OrderNumber);
        }
        public static AutoRetryDecision Evaluate(LiveOrderJournalState journal,string strategyKey,string code,LiveOrderKind side,
            string accountKey,BrokerOrdersSnapshot snapshot,DateTime utc) {
            if(utc.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC automatic retry time required.");
            if(string.IsNullOrWhiteSpace(strategyKey)||strategyKey.Length>128||Rules.Code(code)!=code||string.IsNullOrEmpty(code)
                ||(side!=LiveOrderKind.Buy&&side!=LiveOrderKind.Sell))throw new ArgumentException("A strategy key, symbol and new-order side are required.");
            if(journal==null||journal.Mode!="RealAccount"||string.IsNullOrEmpty(journal.AccountFingerprint)||journal.Orders==null||journal.Executions==null
                ||journal.Orders.Any(value=>value==null))return Result(AutoRetryState.Blocked,"주문 기록·계좌 확인 필요");
            if(journal.Orders.Any(value=>value.Status==LiveOrderStatus.Unknown))return Result(AutoRetryState.Blocked,"미확정 주문 대사 중 / 자동 재전송 없음");
            if(journal.Orders.Any(value=>value.Code==code&&LiveOrderJournal.IsUnresolved(value)))
                return Result(AutoRetryState.Blocked,"동일 종목 접수·체결 확인 중 / 자동 재전송 없음");
            var roots=journal.Orders.Where(value=>value.ClientKey==strategyKey).ToArray();
            if(roots.Length==0)return Result(AutoRetryState.NotNeeded,"새 전략 주문 검증",strategyKey);
            if(roots.Length!=1)return Result(AutoRetryState.Blocked,"전략 주문 식별 충돌 / 자동 재전송 없음");
            string day=MarketTime.ToKorea(utc).ToString("yyyyMMdd",CultureInfo.InvariantCulture);
            var root=roots[0];
            if(root.TradingDate!=day||root.Code!=code||root.Kind!=side||root.TradeKind!=side)
                return Result(AutoRetryState.Blocked,"당일 전략 주문의 종목·방향 확인 필요");
            var same=journal.Orders.Where(value=>value.TradingDate==day&&value.Code==code&&value.TradeKind==side).ToArray();
            if(same.Any(value=>value.Status!=LiveOrderStatus.Rejected&&value.Status!=LiveOrderStatus.NotSent))
                return Result(AutoRetryState.Blocked,"이미 접수·체결된 당일 주문 / 자동 재전송 없음");
            var attempts=same.Where(value=>RetryRecord(value)&&value.Status!=LiveOrderStatus.NotSent).ToArray();
            if(attempts.Length>=MaximumAttemptsPerDay)return Result(AutoRetryState.Blocked,"당일 자동·선택 재시도 한도 도달");
            var previous=attempts.Concat(new[]{root}).OrderByDescending(value=>value.UpdatedUtc).ThenByDescending(value=>value.CreatedUtc).First();
            bool typedRoot=IsDefiniteUnacceptedRejection(root);
            bool authorizedLegacy=!typedRoot&&LiveOrderJournal.IsAuthorizedLegacySellRecheck(journal,root,utc);
            string legacyKey=LegacyKeyPrefix+root.Id+"-1";
            bool firstLegacy=authorizedLegacy&&previous.Id==root.Id&&!attempts.Any(value=>value.ClientKey==legacyKey);
            if((!typedRoot&&!authorizedLegacy)||(!IsDefiniteUnacceptedRejection(previous)&&!firstLegacy))
                return Result(AutoRetryState.Blocked,"미접수 거절 근거 미확인 / 자동 재전송 없음");
            if(previous.CreatedUtc.Kind!=DateTimeKind.Utc||previous.UpdatedUtc.Kind!=DateTimeKind.Utc
                ||previous.UpdatedUtc<previous.CreatedUtc||previous.UpdatedUtc>utc||journal.Executions.Any(value=>value==null
                    ||same.Any(order=>order.Id==value.OrderId)))return Result(AutoRetryState.Blocked,"거절 주문의 시각·체결 기록 확인 필요");
            int attempt=attempts.Length+1;
            var next=previous.UpdatedUtc+Cooldown(attempt);
            var decision=new AutoRetryDecision{State=AutoRetryState.Waiting,PreviousOrderId=previous.Id,Attempt=attempt,
                NextAttemptUtc=next,LegacyReevaluation=firstLegacy,
                Status=(firstLegacy?"사용자 미접수 확인 기록의 자동 재평가 대기 / ":"거절 자동 재검증 대기 / ")+attempt.ToString(CultureInfo.InvariantCulture)+"/"+MaximumAttemptsPerDay.ToString(CultureInfo.InvariantCulture)};
            if(utc<next)return decision;
            var grant=authorizedLegacy?journal.LegacySellRechecks.First(value=>value.RootOrderId==root.Id
                &&value.AccountFingerprint==journal.AccountFingerprint&&value.TradingDate==root.TradingDate
                &&value.Source==LiveOrderJournal.LegacySellRecheckSource):null;
            DateTime scopeBarrier=grant!=null&&grant.GrantedUtc>previous.UpdatedUtc?grant.GrantedUtc:previous.UpdatedUtc;
            if(snapshot==null||!snapshot.RequestScopeVerified||!snapshot.MatchesRequestAccountKey(accountKey)||snapshot.TradingDate!=day
                ||snapshot.RequestStartedUtc<=scopeBarrier||snapshot.ReceivedUtc>utc||utc-snapshot.ReceivedUtc>TimeSpan.FromSeconds(5)) {
                decision.State=AutoRetryState.RefreshRequired;decision.Status="거절 자동 재검증 / 최신 계좌·주문 조회 대기";return decision;
            }
            if(snapshot.Orders.Any(value=>value==null||value.Code==code&&value.OpenQuantity>0))
                return Result(AutoRetryState.Blocked,"동일 종목 증권사 미체결 확인 / 자동 재전송 없음");
            string key=firstLegacy?legacyKey:KeyPrefix+root.Id+"-"+attempt.ToString(CultureInfo.InvariantCulture);
            if(string.IsNullOrEmpty(root.Id)||key.Length>128||journal.Orders.Any(value=>value.ClientKey==key))
                return Result(AutoRetryState.Blocked,"자동 재시도 감사 키 충돌 / 자동 재전송 없음");
            decision.State=AutoRetryState.Eligible;decision.ClientKey=key;
            decision.Status=firstLegacy?"사용자 미접수 확인 기록 자동 재평가 / 과거 거절의 증권사 증거로 간주하지 않음":
                "거절 자동 재검증 / 전략·시세·잔고·주문가능금액 재확인";return decision;
        }
    }
}
