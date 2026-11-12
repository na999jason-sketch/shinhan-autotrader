using System;

namespace ShinhanNativeTrader {
    public static class LiveOrderPolicy {
        public static bool Enabled { get { return LiveOrderSafety.ProductionContractVerified; } }
        public const string BlockReason=LiveOrderSafety.BlockReason;
        public static void EnsureAllowed(){LiveOrderSafety.EnsureAllowed();}
    }
}
