using System;

namespace ShinhanNativeTrader {
    public sealed class CashInstrument {
        public string Code,Market,Product;
        public bool Suspended;
    }

    public static class CashMarketRules {
        // This narrow route uses an observed regular-session execution price as
        // the cash limit price. It does not invent price bands or support arbitrary
        // manual amendments, margin, derivatives or unknown instrument types.
        public static bool CanUseQuoteLimit(CashInstrument instrument,RealtimeQuote quote,decimal limitPrice,DateTime utc) {
            return instrument!=null&&quote!=null&&!instrument.Suspended
                &&(instrument.Market=="0"||instrument.Market=="1")&&(instrument.Product=="ST"||instrument.Product=="EF")
                &&Rules.Code(instrument.Code)==instrument.Code&&!string.IsNullOrEmpty(instrument.Code)&&instrument.Code==quote.Code
                &&limitPrice>0&&limitPrice==decimal.Truncate(limitPrice)&&limitPrice==quote.Price
                &&quote.SequenceConfirmed&&quote.MarketCode=="1"&&MarketTime.CanUseQuote(quote.ToReply(),utc);
        }
    }
}
