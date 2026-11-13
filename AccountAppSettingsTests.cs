using System;
using System.IO;

namespace ShinhanNativeTrader {
    public static class AccountAppSettingsTests {
        static int checks;
        static void Check(bool value,string name){checks++;if(!value)throw new Exception("AccountAppSettingsTests: "+name);}
        public static int Run() {
            checks=0;string directory=Path.Combine(Path.GetTempPath(),"real-settings-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"live-settings-v1.xml"),accountKey=new string('a',64),otherKey=new string('b',64);
            var utc=new DateTime(2026,11,12,0,15,30,DateTimeKind.Utc);
            try {
                using(var store=new AccountAppSettingsStore(path)) {
                    Check(store.Snapshot.BudgetLimit==0,"new account has no assumed spendable cash or operating budget");
                    var strategy=store.Snapshot.Strategy;strategy.StopLossPercent=4.5m;store.Update(strategy,2500000m);
                    Check(store.Snapshot.BudgetLimit==2500000m&&store.Snapshot.Strategy.StopLossPercent==4.5m,"strategy and independent operating limit persist");
                    var held=new Holding{Code="005930",Quantity=2,AvgPrice=70000,Price=71000};
                    var quote=new IndiReply{Code="005930",Price=75000,Clock="091530",HasTradeClock=false};
                    Check(!store.ObserveHigh(accountKey,held,quote,utc)&&store.High(accountKey,"005930",70000)==70000,"display-only snapshot cannot change a trailing high");
                    quote.HasTradeClock=true;Check(store.ObserveHigh(accountKey,held,quote,utc)&&store.High(accountKey,"005930",70000)==75000,"fresh broker quote advances actual holding risk high");
                    Check(store.High(otherKey,"005930",70000)==70000,"another account cannot inherit a risk high");
                    Check(!store.ObserveHigh(accountKey,held,quote,utc),"duplicate quote does not rewrite settings");
                    quote.Price=90000;Check(!store.ObserveHigh(accountKey,held,quote,utc.AddMinutes(2)),"stale quote cannot advance a risk high");
                    quote.Code="000660";Check(!store.ObserveHigh(accountKey,held,quote,utc),"another symbol cannot mutate holding risk state");
                    var copy=store.Snapshot;copy.BudgetLimit=9;copy.Highs.Clear();Check(store.Snapshot.Highs.Count==1&&store.Snapshot.BudgetLimit==2500000m,"settings snapshots are detached copies");
                }
                using(var restored=new AccountAppSettingsStore(path)) {
                    Check(restored.High(accountKey,"005930",70000)==75000,"risk high survives application restart");
                    restored.RefreshPositions(accountKey,new[]{new Holding{Code="005930",Quantity=2,AvgPrice=72000}});
                    Check(restored.High(accountKey,"005930",72000)==72000,"changed broker average resets incompatible high");
                    var quote=new IndiReply{Code="005930",Price=78000,Clock="091530",HasTradeClock=true};
                    restored.ObserveHigh(accountKey,new Holding{Code="005930",Quantity=2,AvgPrice=72000},quote,utc);
                    restored.RefreshPositions(accountKey,new Holding[0]);Check(restored.Snapshot.Highs.Count==0,"closed broker position does not leave an inherited high");
                    bool rejected=false;try{restored.Update(new StrategySettings(),-1);}catch(InvalidOperationException){rejected=true;}
                    Check(rejected&&restored.Snapshot.BudgetLimit==2500000m,"invalid limit cannot replace persisted strategy");
                }
                string xml=File.ReadAllText(path);Check(!xml.Contains("Password")&&!xml.Contains("Cash")&&!xml.Contains("Fill")&&!xml.Contains("Paper"),"settings contain no credentials, broker cash or generated fills");
                File.WriteAllText(path,"<invalid>");bool corrupt=false;try{using(var unused=new AccountAppSettingsStore(path)){} }catch(InvalidOperationException){corrupt=true;}
                Check(corrupt&&File.ReadAllText(path)=="<invalid>","corrupt settings are preserved and rejected");
            } finally {Directory.Delete(directory,true);}
            return checks;
        }
    }
}
