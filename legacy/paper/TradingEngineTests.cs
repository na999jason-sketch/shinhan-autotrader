using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShinhanNativeTrader {
    public static class TradingEngineTests {
        static int _checks;
        static readonly DateTime Utc = new DateTime(2026,11,12,1,0,0,DateTimeKind.Utc);
        static void Check(bool pass, string label) { _checks++; if(!pass) throw new Exception("TradingEngineTests: " + label); }
        static void Throws(Action action, string label) {
            bool threw = false;
            try { action(); } catch { threw = true; }
            Check(threw,label);
        }
        static string Buy(PaperTradingEngine engine, string code, int qty, decimal price, string key) {
            return engine.SubmitBuy(code,code,qty,price,key,Utc).Id;
        }
        public static int Run() {
            _checks = 0;
            var directory = Path.Combine(Path.GetTempPath(),"ShinhanEngineTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                TestOrders(); TestWeightedFills(); TestPersistence(directory); TestStrategies();
                Check(!LiveOrderPolicy.Enabled,"live permanently disabled");
                Throws(LiveOrderPolicy.EnsureAllowed,"live entry point refuses orders");
                return _checks;
            } finally { Directory.Delete(directory,true); }
        }
        static void TestOrders() {
            using(var e = new PaperTradingEngine(null,100000m)) {
                var order = Buy(e,"005930",5,10000,"signal-1");
                Check(e.AvailableCash == 49850m,"reserve buy cash including simulated fee");
                Throws(() => Buy(e,"005930",1,10000,"signal-2"),"same symbol pending blocked");
                Throws(() => Buy(e,"000660",5,10000,"signal-3"),"cash reservation blocks overspend");
                Throws(() => Buy(e,"000660",1,10000,"signal-1"),"global idempotency key unique");
                Throws(() => e.ApplyFill(order,"bad-price",1,10001,Utc),"buy above limit rejected");
                Throws(() => e.ApplyFill(order,"before-order",1,9900,Utc.AddSeconds(-1)),"fill before order timestamp blocked");
                Check(e.ApplyFill(order,"fill-1",2,9900,Utc),"partial fill accepted");
                var s = e.Snapshot;
                Check(s.Orders[0].Status == PaperOrderStatus.PartiallyFilled && s.Orders[0].FilledQuantity == 2,"partial order state");
                Check(s.Cash == 80140.6m && e.AvailableCash == 50050.6m,"partial fill consumes cash and retains remaining reserve");
                Check(!e.ApplyFill(order,"fill-1",2,9900,Utc),"duplicate identical fill ignored");
                Throws(() => e.ApplyFill(order,"fill-1",3,9900,Utc),"duplicate fill payload mismatch blocked");
                Throws(() => e.ApplyFill(order,"too-much",4,9900,Utc),"overfill blocked");
                e.CancelOrder(order); e.CancelOrder(order);
                Check(e.AvailableCash == e.Snapshot.Cash && e.Snapshot.Positions[0].Qty == 2,"cancel releases remaining reserve preserves filled shares");
                Throws(() => e.ApplyFill(order,"late-fill",1,9900,Utc),"late fill after terminal paper cancel blocked");
                Throws(() => Buy(e,"005930",1,10000,"signal-1"),"cancelled order key remains blocked");
                Throws(() => Buy(e,"005930",1,10000,"signal-4"),"held symbol prevents new buy");
                Throws(() => e.SubmitSell("005930",3,9800,"sell-too-many",Utc),"overselling blocked");
                var sell = e.SubmitSell("005930",2,9800,"exit-1",Utc);
                Throws(() => e.SubmitSell("005930",1,9800,"exit-2",Utc),"duplicate sell reservation blocked");
                Throws(() => e.ApplyFill(sell.Id,"cheap-sell",1,9700,Utc),"sell below limit blocked");
                e.ApplyFill(sell.Id,"sell-fill-1",1,10000,Utc);
                e.ApplyFill(sell.Id,"sell-fill-2",1,11000,Utc);
                Check(e.Snapshot.Positions.Count == 0 && e.Snapshot.Cash == 101077.6m,"partial sells produce realized net paper cash");
                Check(e.Snapshot.Orders.Last().Status == PaperOrderStatus.Filled,"terminal filled status");
                var rejected = Buy(e,"000660",1,10000,"rejected");
                e.RejectOrder(rejected,"모의 거절");
                Check(e.Snapshot.Orders.Last().Status == PaperOrderStatus.Rejected && e.AvailableCash == 101077.6m,"reject releases reserved cash");
                var expiring = Buy(e,"000660",1,10000,"expiry");
                e.ExpireWorkingOrders(Utc.AddMinutes(5),TimeSpan.FromMinutes(1));
                Check(e.Snapshot.Orders.Single(o => o.Id == expiring).Status == PaperOrderStatus.Cancelled,"working order expires deterministically");
                var settings = e.Settings; settings.MaxPositions = 1; e.UpdateSettings(settings);
                var max = Buy(e,"005930",1,10000,"max-1");
                Throws(() => Buy(e,"000660",1,10000,"max-2"),"pending buy occupies max position slot");
                e.ApplyFill(max,"max-fill",1,10000,Utc);
                Throws(() => Buy(e,"000660",1,10000,"max-3"),"filled buy occupies max position slot");
                Throws(() => e.SetCapital(1m),"capital cannot underfund active assets");
                e.SetCapital(200000m);
                Check(e.Snapshot.Capital == 200000 && e.Snapshot.Cash == 191047.6m,"capital adjustment preserves previous realized cash");
                var external = e.Snapshot; external.Cash = 0; external.Positions.Clear();
                Check(e.Snapshot.Cash > 0 && e.Snapshot.Positions.Count == 1,"snapshot cannot mutate ledger");
                Throws(() => Buy(e,"１２３４５６",1,10000,"unicode"),"non ASCII stock code rejected");
                Throws(() => e.SubmitBuy("000660","x",1,10000,"local-date",DateTime.Now),"ambiguous local timestamps rejected");
                var invalid = e.Settings; invalid.StopLossPercent = 100;
                Throws(() => e.UpdateSettings(invalid),"invalid strategy rejected");
            }
        }
        static void TestWeightedFills() {
            using(var e = new PaperTradingEngine(null,100000m)) {
                var order = Buy(e,"005930",3,10000,"weighted");
                e.ApplyFill(order,"weighted-1",1,9000,Utc);
                e.ApplyFill(order,"weighted-2",2,9900,Utc);
                var p = e.Snapshot.Positions.Single();
                Check(p.Qty == 3 && p.Entry == 9600 && p.High == 9900,"partial buy fills use weighted cost basis");
                var sell = e.SubmitSell("005930",1,10000,"weighted-sell",Utc);
                e.ApplyFill(sell.Id,"weighted-sell-fill",1,10000,Utc);
                Check(e.Snapshot.Positions.Single().Entry == 9600 && e.Snapshot.Positions.Single().Qty == 2,"partial sell preserves cost basis");
            }
        }
        static void TestPersistence(string directory) {
            var path = Path.Combine(directory,"paper.xml");
            string pending;
            using(var e = new PaperTradingEngine(path,100000m)) {
                var first = Buy(e,"005930",2,10000,"persist-1");
                e.ApplyFill(first,"persist-fill",2,10000,Utc);
                e.UpdateQuote("005930",10800);
                pending = Buy(e,"000660",3,5000,"persist-pending");
                e.ApplyFill(pending,"pending-partial",1,4900,Utc);
                var settings = e.Settings; settings.TrailingDrawdownPercent = 3; e.UpdateSettings(settings);
                Throws(() => { using(var conflict = new PaperTradingEngine(path,100000m)) {} },"second writer cannot open state");
            }
            using(var e = new PaperTradingEngine(path,999999m)) {
                Check(e.Snapshot.Capital == 100000 && e.Snapshot.Positions.Count == 2,"restart restores original capital and fills");
                Check(e.Snapshot.Positions.Single(p => p.Code == "005930").High == 10800,"restart preserves trailing high");
                Check(e.Settings.TrailingDrawdownPercent == 3,"restart preserves strategy");
                Check(e.RecoveryRequired && e.Snapshot.Orders.Single(o => o.Id == pending).Status == PaperOrderStatus.NeedsReconciliation,"restart marks pending uncertain");
                Check(e.AvailableCash == 64995.3m,"restart retains pending reserve");
                Throws(() => Buy(e,"035420",1,10000,"after-crash"),"new submits blocked until recovery");
                Throws(() => e.ApplyFill(pending,"blind-retry",1,5000,Utc),"uncertain order fill blocked");
                Check(e.RecoverPaperOrders() == 1 && !e.RecoveryRequired,"explicit paper recovery cancels unfilled remainder");
                Check(e.Snapshot.Positions.Single(p => p.Code == "000660").Qty == 1 && e.AvailableCash == 75025.3m,"recovery retains partial fill and releases only remainder");
                Check(e.UpdateQuote("005930",10400).Contains("추적손절"),"persisted high triggers configured trailing stop");
            }
            var costPath = Path.Combine(directory,"cost-tampered.xml");
            var costTampered = File.ReadAllText(path).Replace("<Entry>10000</Entry>","<Entry>5000</Entry>");
            Check(costTampered.Contains("<Entry>5000</Entry>"),"tamper test targets persisted entry");
            File.WriteAllText(costPath,costTampered);
            Throws(() => { using(var e = new PaperTradingEngine(costPath,100000m)) {} },"entry not matching replayed fills refuses restart");
            var bad = Path.Combine(directory,"bad.xml"); File.WriteAllText(bad,"not XML");
            Throws(() => { using(var e = new PaperTradingEngine(bad,100000m)) {} },"corrupt state refuses initialization");
            var document = new System.Xml.XmlDocument(); document.Load(path);
            var cashNode = document.SelectSingleNode("/PaperTradingState/Cash");
            Check(cashNode != null,"tamper test targets persisted cash");
            cashNode.InnerText = "999999"; document.Save(path);
            Throws(() => { using(var e = new PaperTradingEngine(path,100000m)) {} },"cash not matching fill ledger refuses restart");
            var faultPath = Path.Combine(directory,"fault.xml");
            using(var e = new PaperTradingEngine(faultPath,100000m)) {
                File.Delete(faultPath); Directory.CreateDirectory(faultPath);
                Throws(() => Buy(e,"005930",1,10000,"io-fault"),"storage write failure returned to caller");
                Check(e.StorageFault,"storage fault latches engine");
                Throws(() => Buy(e,"000660",1,10000,"after-io-fault"),"further trading blocked after storage failure");
            }
        }
        static void TestStrategies() {
            var s = new StrategySettings();
            var p = new PaperPosition { Code = "005930",Qty = 1,Entry = 10000,High = 10800 };
            Check(Rules.Exit(p,9400,s).Contains("손절"),"config stop loss");
            Check(Rules.Exit(p,11200,s).Contains("익절"),"config take profit");
            s.TrailingDrawdownPercent = 3;
            Check(Rules.Exit(p,10400,s).Contains("추적손절"),"config trailing drawdown");
            Check(Rules.PaperQty(100000,10000,0.5m,0.003m) == 4,"allocation includes costs");
            Check(MarketTime.ToKorea(Utc).Hour == 10,"KST conversion independent of local timezone");
            Check(MarketTime.IsFreshQuote("100000",Utc),"fresh regular-session quote accepted");
            Check(!MarketTime.IsFreshQuote("095900",Utc),"stale quote rejected");
            Check(!MarketTime.IsFreshQuote("251200",Utc),"invalid quote clock rejected");
            Check(!MarketTime.IsFreshQuote("153000",new DateTime(2026,11,12,6,30,0,DateTimeKind.Utc)),"after-hours clock rejected");
            Check(!MarketTime.IsFreshQuote("100000",new DateTime(2026,11,14,1,0,0,DateTimeKind.Utc)),"weekend quote rejected");
            decimal number;
            Check(Rules.TryNum("1,235.5",out number) && number == 1235.5m,"strict numeric parsing");
            Check(!Rules.TryNum("broken",out number),"malformed numeric distinct from zero");
            var bars = new List<Bar>();
            for(int i=0;i<35;i++) bars.Add(new Bar { Date = new DateTime(2026,1,1).AddDays(i).ToString("yyyyMMdd"), Open=10000+i*120,Close=10000+i*120,High=10100+i*120,Low=9900+i*120,Volume=100000 });
            bars[34].Close=20000; bars[34].High=20100; bars[34].Volume=500000;
            var signal = Rules.Entry("005930","삼성",bars,s);
            Check(signal != null && signal.SignalDate == bars[34].Date,"breakout carries date for durable duplicate key");
            s.VolumeMultiplier=6;
            Check(Rules.Entry("005930","삼성",bars,s) == null,"editable volume threshold changes selection");
            s.VolumeMultiplier=1.3m; s.MaxPrice=19000;
            Check(Rules.Entry("005930","삼성",bars,s) == null,"editable price threshold changes selection");
        }
    }
}
