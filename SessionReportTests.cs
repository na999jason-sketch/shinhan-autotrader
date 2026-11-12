using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ShinhanNativeTrader {
    public static class SessionReportTests {
        static int checks;
        static void Check(bool value,string name){checks++;if(!value)throw new Exception("SessionReportTests: "+name);}
        static void Reject(Action action,string name) {
            bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}
            Check(rejected,name);
        }
        public static int Run() {
            checks=0;
            var directory=Path.Combine(Path.GetTempPath(),"ShinhanAccountReport-"+Guid.NewGuid().ToString("N"));
            var day=new DateTime(2026,11,13);
            var utc=new DateTime(2026,11,13,6,20,0,DateTimeKind.Utc);
            var positions=new[]{new Holding{Code="005930",Name="SECRET_ACCOUNT_12345678901",Quantity=2,AvgPrice=70000,Price=65000},
                new Holding{Code="000660",Name="=HYPERLINK(\"SECRET\")",Quantity=1,AvgPrice=100000,Price=110000}};
            var cash=new AccountCashSnapshot{DepositCash=500000,CashOrderable=400000,Cash100Orderable=350000,ReceivedUtc=utc.AddSeconds(-3)};
            var orders=new BrokerOrdersSnapshot{TradingDate="20261113",ReceivedUtc=utc.AddSeconds(-2)};
            orders.Orders.Add(new BrokerOrderRow{OrderNumber="123",OriginalOrderNumber="0",Code="005930",Name="PASSWORD_4321",
                SideCode="2",Quantity=3,LimitPrice=65000,FilledQuantity=1,AverageFillPrice=65000,OpenQuantity=2,
                ConfirmationCode="0",RejectName="SECRET_ACCOUNT_12345678901",StatusName="PASSWORD_4321"});
            var journal=new[]{new LiveOrderRecord{Id="SECRET_ACCOUNT_12345678901",ClientKey="PASSWORD_4321",Code="005930",
                Kind=LiveOrderKind.Buy,Status=LiveOrderStatus.Working,Quantity=3,FilledQuantity=0,LimitPrice=65000,
                BrokerOrderNumber="123",UpdatedUtc=utc.AddSeconds(-1)}};
            try {
                var path=SessionReport.Write(directory,day,utc,positions,utc.AddSeconds(-5),cash,orders,journal,LiveOrderSafety.BlockReason,
                    new Dictionary<string,DateTime>{{"005930",utc.AddSeconds(-1)}});
                Check(Path.GetDirectoryName(path)==Path.GetFullPath(directory)&&Path.GetFileName(path)=="account-report-20261113.csv","report has fixed date-only filename inside requested directory");
                var bytes=File.ReadAllBytes(path);var text=File.ReadAllText(path,Encoding.UTF8);
                Check(bytes.Length>3&&bytes[0]==0xef&&bytes[1]==0xbb&&bytes[2]==0xbf,"Excel report is UTF8 with BOM");
                Check(text.Contains("\"'005930\",2,70000,65000,140000,130000,-10000"),"actual quantities and negative unrealized profit are numeric, not synthetic fills");
                Check(text.Contains("\"평가손익(비용제외)\",0\r\n")&&text.Contains("\"총 평가금액\",240000"),"portfolio totals derive from broker holdings only");
                Check(text.Contains("\"현금100% 주문가능금액\",350000"),"cash100 value remains separate from deposit and general capacity");
                Check(text.Contains("2026-11-13 06:19:55 UTC")&&text.Contains("2026-11-13 06:19:59 UTC"),"holdings and per-symbol price response timestamps remain distinct");
                Check(text.Contains("수수료·세금 제외")&&text.Contains("실현손익을 추정하지 않음"),"unrealized valuation excludes fees and does not invent realized profit");
                Check(text.Contains("\"실현손익(증권사 조회)\",\"미확인\""),"missing broker realized-profit response never becomes zero or sale proceeds");
                Check(text.Contains("\"'123\",\"미확인\",\"'005930\",\"매수\",3,65000,1,65000,2"),"broker cumulative fill snapshot is reported independently");
                Check(text.Contains("\"'123\",\"미확인\",\"'005930\",\"Buy\",3,0,0,65000,\"Working\""),"acknowledged working journal order is not changed to broker aggregate fill");
                Check(!text.Contains("SECRET")&&!text.Contains("PASSWORD")&&!text.Contains("12345678901")&&!text.Contains("4321"),"provider names, reject text and internal arbitrary identifiers are omitted");
                Check(positions[0].Price==65000&&orders.Orders[0].FilledQuantity==1&&journal[0].FilledQuantity==0,"reporting does not mutate holding or order evidence");

                positions[0].Price=60000;
                var second=SessionReport.Write(directory,day,utc.AddMinutes(1),positions,utc,cash,orders,journal,LiveOrderSafety.BlockReason);
                Check(second==path&&File.Exists(path+".bak"),"rewriting one day atomically replaces the report and retains prior version");
                Check(File.ReadAllText(path).Contains("2,70000,60000,140000,120000,-20000")&&File.ReadAllText(path+".bak").Contains("2,70000,65000,140000,130000,-10000"),"atomic overwrite contains the new complete snapshot and old complete backup");
                var beforeFailure=File.ReadAllBytes(path);
                positions[0].Quantity=-1;
                bool failed=false;
                try{SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,LiveOrderSafety.BlockReason);}catch(InvalidOperationException){failed=true;}
                Check(failed&&beforeFailure.SequenceEqual(File.ReadAllBytes(path))&&!Directory.GetFiles(directory,"*.tmp-*").Any(),"invalid data cannot replace a valid report or leave temporary data");
                positions[0].Quantity=2;

                var unknownPath=SessionReport.Write(directory,day.AddDays(1),utc,null,null,null,null,null,"PASSWORD_4321");
                var unknown=File.ReadAllText(unknownPath);
                Check(unknown.Contains("\"보유 종목 수\",\"미확인\"")&&unknown.Contains("\"총 평가금액\",\"미확인\"")&&unknown.Contains("\"예수금\",\"미확인\""),"never-queried snapshots show unknown instead of zero balances");
                Check(!unknown.Contains("PASSWORD")&&unknown.Contains("실주문 상태 미확인"),"arbitrary gate text cannot inject credentials into a report");
                var missing=new[]{new Holding{Code="005930",Quantity=2,AvgPrice=0,Price=65000}};
                var missingPath=SessionReport.Write(directory,day.AddDays(2),utc,missing,utc,null,null,null,LiveOrderSafety.BlockReason);
                Check(File.ReadAllText(missingPath).Contains("\"'005930\",2,\"미확인\",65000,\"미확인\",130000,\"미확인\""),"missing average cost never becomes a fictitious gain");

                var tradeHoldings=new BrokerHoldingsSnapshot{BrokerDate="20261113",BrokerDateValidated=true,
                    RealizedProfit=-12345.67m,UnrealizedProfit=999999m,RequestStartedUtc=utc.AddSeconds(-8),ReceivedUtc=utc.AddSeconds(-4)};
                var realizedPath=SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 운영 시작됨",null,tradeHoldings);
                var realizedText=File.ReadAllText(realizedPath);
                Check(realizedText.Contains("\"실현손익(증권사 조회)\",-12345.67\r\n"),"validated same-day negative broker realized profit remains numeric");
                Check(realizedText.Contains("\"실현손익 조회 요청시각\",\"2026-11-13 06:19:52 UTC\"")
                    &&realizedText.Contains("\"실현손익 조회 응답시각\",\"2026-11-13 06:19:56 UTC\""),"realized-profit request and response UTC metadata remain distinct");
                Check(realizedText.Contains("\"실현손익 기준일(증권사)\",\"2026-11-13\"")
                    &&realizedText.Contains("\"실주문 상태\",\"실주문 운영 시작됨\""),"finite authorized UI state and validated broker date can be reported");
                Check(tradeHoldings.RealizedProfit==-12345.67m&&tradeHoldings.UnrealizedProfit==999999m,"realized reporting neither mutates nor substitutes other broker profit fields");
                tradeHoldings.RealizedProfit=5555.5m;
                SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 미시작",null,tradeHoldings);
                realizedText=File.ReadAllText(realizedPath);
                Check(realizedText.Contains("\"실현손익(증권사 조회)\",5555.5\r\n")
                    &&realizedText.Contains("\"실주문 상태\",\"실주문 미시작\""),"positive broker realized profit is independent from order authorization state");
                tradeHoldings.RealizedProfit=0;
                SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 미시작",null,tradeHoldings);
                Check(File.ReadAllText(realizedPath).Contains("\"실현손익(증권사 조회)\",0\r\n"),"verified broker zero is distinct from an unknown profit");
                tradeHoldings.RealizedProfit=null;
                SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 미시작",null,tradeHoldings);
                Check(File.ReadAllText(realizedPath).Contains("\"실현손익(증권사 조회)\",\"미확인\""),"nullable broker realized profit remains unknown despite a valid date");
                tradeHoldings.RealizedProfit=987654321m;tradeHoldings.BrokerDate="20261112";
                SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 미시작",null,tradeHoldings);
                realizedText=File.ReadAllText(realizedPath);
                Check(realizedText.Contains("\"실현손익(증권사 조회)\",\"미확인\"")&&!realizedText.Contains("987654321"),"a validated profit for another day is not assigned to the requested report day");
                tradeHoldings.BrokerDate="20261113";tradeHoldings.BrokerDateValidated=false;
                SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 미시작",null,tradeHoldings);
                Check(File.ReadAllText(realizedPath).Contains("\"실현손익(증권사 조회)\",\"미확인\""),"an unvalidated same-day broker date cannot establish realized profit");
                tradeHoldings.BrokerDate="PASSWORD_4321";tradeHoldings.BrokerDateValidated=true;
                SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 미시작",null,tradeHoldings);
                Check(!File.ReadAllText(realizedPath).Contains("PASSWORD")&&!File.ReadAllText(realizedPath).Contains("4321"),"invalid broker date text cannot disclose credentials");
                tradeHoldings.BrokerDate="20261113";tradeHoldings.RequestStartedUtc=default(DateTime);
                SessionReport.Write(directory,day,utc,positions,utc,cash,orders,journal,"실주문 미시작",null,tradeHoldings);
                Check(File.ReadAllText(realizedPath).Contains("\"실현손익 조회 요청시각\",\"미확인\""),"missing request timestamp is explicit rather than a fabricated epoch");

                Check(SessionReport.EscapeText(" =SUM(A1)").StartsWith("\"' =SUM",StringComparison.Ordinal),"leading whitespace cannot bypass CSV formula protection");
                Check(SessionReport.EscapeText("+SUM(A1)").StartsWith("\"'+",StringComparison.Ordinal)&&SessionReport.EscapeText("-CMD()").StartsWith("\"'-",StringComparison.Ordinal),"plus and minus text prefixes cannot become formulas");
                Check(SessionReport.EscapeText("@SUM(A1)").StartsWith("\"'@",StringComparison.Ordinal)&&SessionReport.EscapeText("\t=CMD()").StartsWith("\"'\t",StringComparison.Ordinal),"at-sign and initial controls cannot become formulas");
                Check(SessionReport.EscapeText("comma,quote\"")=="\"comma,quote\"\"\"","CSV commas and quotes remain one escaped cell");

                Reject(()=>SessionReport.Write(" ",day,utc,null,null,null,null,null,""),"missing output directory rejected");
                Reject(()=>SessionReport.Write(directory,day.AddHours(1),utc,null,null,null,null,null,""),"report date cannot contain a hidden wall-clock time");
                Reject(()=>SessionReport.Write(directory,DateTime.SpecifyKind(day,DateTimeKind.Local),utc,null,null,null,null,null,""),"report calendar date cannot depend on PC local timezone");
                Reject(()=>SessionReport.Write(directory,day,DateTime.SpecifyKind(utc,DateTimeKind.Unspecified),null,null,null,null,null,""),"report timestamp requires UTC");
                Reject(()=>SessionReport.Write(directory,day,utc,null,day,null,null,null,""),"holdings timestamp requires UTC when present");
                tradeHoldings.RequestStartedUtc=day;
                Reject(()=>SessionReport.Write(directory,day,utc,null,null,null,null,null,"",null,tradeHoldings),"realized-profit request timestamp requires UTC when provided");
                tradeHoldings.RequestStartedUtc=default(DateTime);tradeHoldings.ReceivedUtc=day;
                Reject(()=>SessionReport.Write(directory,day,utc,null,null,null,null,null,"",null,tradeHoldings),"realized-profit response timestamp requires UTC");
            }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
            return checks;
        }
    }
}
