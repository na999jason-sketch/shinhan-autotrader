using System;

namespace ShinhanNativeTrader {
    public static class LiveOrderPolicy {
        public const string BlockReason=LiveOrderSafety.BlockReason;
        // Compatibility name only. There is no process-wide on/off switch.
        public static bool IsEnabled(LiveTradingSession authorization,string account){return authorization!=null&&authorization.Allows(account,DateTime.UtcNow);}
        public static void EnsureAllowed(LiveOrderPreflight checks,LiveAccountCredentials credentials,DateTime utc){LiveOrderSafety.EnsureAllowed(checks,credentials,utc);}
    }
}
