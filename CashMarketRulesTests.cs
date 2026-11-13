using System;

namespace ShinhanNativeTrader {
    public static class CashMarketRulesTests {
        static int checks;
        static void Check(bool value,string message){checks++;if(!value)throw new Exception("CashMarketRulesTests: "+message);}
        public static int Run() {
            checks=0;
            var utc=new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc);
            var stock=new CashInstrument{Code="005930",Market="0",Product="ST"};
            var quote=new RealtimeQuote{Code="005930",Price=70000,Clock="091530",MarketCode="1",SequenceConfirmed=true,ReceivedUtc=utc};
            Check(CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc),"known ordinary cash stock uses its actual regular-session execution price");
            stock.Product="EF";Check(CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc),"existing ETF holdings use the same cash-price validation route");
            stock.Product="OP";Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc),"derivative product is outside the cash route");stock.Product="ST";
            stock.Suspended=true;Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc),"suspended instrument cannot acquire order readiness");stock.Suspended=false;
            Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,71000,utc),"an arbitrary manual limit price does not inherit execution-price proof");
            quote.Price=70000.5m;Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,70000.5m,utc),"fractional won price is rejected");quote.Price=70000;
            quote.SequenceConfirmed=false;Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc),"first or cached quote cannot prove live execution price");quote.SequenceConfirmed=true;
            quote.MarketCode="4";Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc),"after-hours stream is outside the regular cash order route");quote.MarketCode="1";
            Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc.AddMinutes(2)),"stale execution time cannot acquire readiness");
            quote.Code="000660";Check(!CashMarketRules.CanUseQuoteLimit(stock,quote,70000,utc),"another instrument cannot supply price proof");
            return checks;
        }
    }
}
