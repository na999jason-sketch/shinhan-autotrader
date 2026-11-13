using System;
using System.Collections.Generic;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class SelfTests {
        static void Check(bool ok, string label){if(!ok)throw new Exception("TEST FAIL: "+label);}
        public static int Run() {
            Check(Rules.Code("A005930")=="005930","normalize account code");
            Check(Rules.Code("a005930")=="","invalid code");
            Check(Rules.Num("1,235")==1235m,"numeric parse");
            Check(Rules.Entry("005930","삼성",new Bar[0])==null,"not enough historical candles");
            var flat=new List<Bar>();
            var rising=new List<Bar>();
            for(int i=0;i<35;i++) {
                var date=new DateTime(2026,1,1).AddDays(i).ToString("yyyyMMdd");
                flat.Add(new Bar{Date=date,Open=10000,High=10100,Low=9900,Close=10000,Volume=100000});
                rising.Add(new Bar{Date=date,Open=10000+i*120,High=10100+i*120,Low=9900+i*120,Close=10000+i*120,Volume=100000});
            }
            Check(Rules.Entry("005930","삼성",flat)==null,"flat series must not buy");
            rising[34].Close=20000;rising[34].High=20100;rising[34].Volume=500000;
            Check(Rules.Entry("005930","삼성",rising)!=null,"real breakout entry");
            var p=new PositionRiskState{Entry=10000,High=10500,Qty=10};
            Check(Rules.Exit(p,9300).Contains("손절"),"stop loss");
            Check(Rules.Exit(p,11500).Contains("익절"),"take profit");
            Check(Rules.OrderQuantity(100000,10000)==1,"budget sizing includes buffer");
            Check(Rules.OrderQuantity(0,10000)==0,"zero available cash");
            Check(!Rules.ValidTime(new DateTime(2026,11,10,18,0,0)),"do not trade after hours");
            Check(!typeof(SelfTests).Assembly.GetTypes().Any(type=>type.Name=="PaperTradingEngine"||type.Name=="PaperTradingState"),"actual account EXE excludes the archived simulation engine");
            return 12 + IndiTests.Run() + IndiDiagnosticTests.Run() + IndiInstallationTests.Run()
                + AccountQueriesTests.Run() + UnattendedSessionTests.Run() + LiveOrdersTests.Run() + SessionReportTests.Run()
                + AccountAppSettingsTests.Run() + RealtimeIndiTests.Run() + RealtimeDiagnosticsTests.Run() + CashMarketRulesTests.Run()
                + BrokerReadinessTests.Run() + BrokerAccountEventRouterTests.Run() + PremarketResearchTests.Run()
                + LiveOrderResponseParserTests.Run() + IndiOrderSchemaTests.Run() + IndiDefinitionExportTests.Run() + AutoOrderRetryTests.Run();
        }
    }
}
