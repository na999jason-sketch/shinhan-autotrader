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
        sealed class ExpandedHoldingsResponse : IIndiTransport {
            public event Action<int> ReceiveData {add{} remove{}}
            public event Action<int> ReceiveSysMsg {add{} remove{}}
            public event Action<string> Error {add{} remove{}}
            readonly Dictionary<int,string> multi=new Dictionary<int,string>{{0,"A005930"},{1,"SYNTHETIC"},{2,"0"},{3,"보통"},
                {4,"0"},{5,"0"},{6,"0"},{7,"0"},{8,"18"},{9,"18"},{10,"18"},{11,"0"},{12,"8160"},{13,"7870"},
                {18,"141660"},{19,"-5220"},{20,"-3.5539"},{22,"0"},{32,"18"}};
            public object Invoke(string method,params object[] arguments) {
                if(method=="GetMultiRowCount")return 1;
                if(method=="GetMultiData") {
                    string value;return multi.TryGetValue((int)arguments[1],out value)?value:"";
                }
                if(method=="GetSingleData") {
                    int field=(int)arguments[0];
                    return field==15?"146880":field==18?"141660":field==19?"-5220":field==22?"-3.5539":"";
                }
                throw new InvalidOperationException("Unexpected synthetic holdings reader method: "+method);
            }
            public void Dispose(){}
        }
        static string ReportFixture(string directory,string name,DateTime modifiedUtc) {
            Directory.CreateDirectory(directory);
            var path=Path.Combine(directory,name);
            File.WriteAllText(path,"complete previous report");
            File.SetLastWriteTimeUtc(path,modifiedUtc);
            return path;
        }
        static void TestReportDiscovery(string directory,DateTime utc) {
            var root=Path.Combine(directory,"lookup");
            Check(SessionReport.FindLatestReport(null)==null&&SessionReport.FindLatestReport(" ")==null
                &&SessionReport.FindLatestReport(root)==null,"missing or unspecified report roots have no previous report");
            Check(SessionReport.FindLatestReport("invalid\0path")==null,"invalid report lookup path is safely ignored");
            Directory.CreateDirectory(root);
            Check(SessionReport.FindLatestReport(root)==null,"an empty report root has no previous report");
            var direct=ReportFixture(root,"account-report-20261113.csv",utc);
            var account=Path.Combine(root,new string('a',64));
            var accountReport=ReportFixture(account,"account-report-20261112.csv",utc.AddMinutes(1));
            Check(SessionReport.FindLatestReport(root)==accountReport,"latest save timestamp wins across root and one account-hash directory");
            Check(SessionReport.FindLatestReport(direct)==null,"a regular file cannot be used as a report directory");

            var ignoredNames=new[]{"account-report-20260230.csv","account-report-20261113.csv.bak",
                "account-report-20261113.csv.tmp-test","account-report-2026111.csv","account-report-20261114.CSV",
                "other-20261114.csv"};
            foreach(var name in ignoredNames)ReportFixture(root,name,utc.AddHours(1));
            ReportFixture(Path.Combine(root,"another-account"),"account-report-20261114.csv",utc.AddHours(1));
            ReportFixture(Path.Combine(root,new string('g',64)),"account-report-20261114.csv",utc.AddHours(1));
            ReportFixture(Path.Combine(root,new string('a',63)),"account-report-20261114.csv",utc.AddHours(1));
            ReportFixture(Path.Combine(account,new string('b',64)),"account-report-20261114.csv",utc.AddHours(1));
            Check(SessionReport.FindLatestReport(root)==accountReport,"backups, temporaries, invalid dates, non-hash and deeper directories are excluded");

            File.SetLastWriteTimeUtc(direct,utc.AddMinutes(1));
            Check(SessionReport.FindLatestReport(root)==direct,"equal save timestamps are ordered by valid report date");
            var tied=ReportFixture(Path.Combine(root,new string('b',64)),"account-report-20261113.csv",utc.AddMinutes(1));
            var expected=new[]{direct,tied}.OrderBy(value=>value,StringComparer.Ordinal).First();
            Check(SessionReport.FindLatestReport(root)==expected&&SessionReport.FindLatestReport(root)==expected,
                "equal dates and timestamps have deterministic ordinal path ordering");

            if(Environment.OSVersion.Platform==PlatformID.Win32NT) {
                var unreadable=Path.Combine(directory,"windows-unreadable");
                ReportFixture(unreadable,"account-report-20261113.csv",utc);
                var original=Directory.GetAccessControl(unreadable,System.Security.AccessControl.AccessControlSections.Access)
                    .GetSecurityDescriptorBinaryForm();
                var denied=new System.Security.AccessControl.DirectorySecurity();
                denied.SetSecurityDescriptorBinaryForm(original,System.Security.AccessControl.AccessControlSections.Access);
                using(var identity=System.Security.Principal.WindowsIdentity.GetCurrent()) {
                    denied.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User,
                        System.Security.AccessControl.FileSystemRights.ListDirectory,System.Security.AccessControl.AccessControlType.Deny));
                    try {
                        Directory.SetAccessControl(unreadable,denied);
                        Check(SessionReport.FindLatestReport(unreadable)==null,"an actual Windows directory-listing denial safely yields no previous report");
                    }finally {
                        // SetAccessControl persists only sections marked modified.
                        // A fresh descriptor marks this saved DACL for restoration.
                        var restored=new System.Security.AccessControl.DirectorySecurity();
                        restored.SetSecurityDescriptorBinaryForm(original,System.Security.AccessControl.AccessControlSections.Access);
                        Directory.SetAccessControl(unreadable,restored);
                    }
                }
                Check(Directory.GetFiles(unreadable).Length==1&&SessionReport.FindLatestReport(unreadable)!=null,
                    "original listing access is restored after the real permission denial");
                Directory.Delete(unreadable,true);
                Check(!Directory.Exists(unreadable),"the restored Windows ACL fixture can be fully removed");
            }
        }
        static void TestWriteFailures(string directory,DateTime day,DateTime utc) {
            var blockedDirectory=Path.Combine(directory,"not-a-directory");
            File.WriteAllText(blockedDirectory,"must remain unchanged");
            ReportFileWriteException error=null;
            try{SessionReport.Write(blockedDirectory,day,utc,null,null,null,null,null,"");}
            catch(ReportFileWriteException ex){error=ex;}
            var intended=Path.Combine(Path.GetFullPath(blockedDirectory),"account-report-20261113.csv");
            Check(error!=null&&error.ReportPath==intended&&error.InnerException is IOException
                &&error.FailureCode=="IoFailure","directory creation failure carries the actual intended report path and fixed failure code");
            Check(File.ReadAllText(blockedDirectory)=="must remain unchanged"&&!error.Message.Contains(error.InnerException.Message),
                "owned failure leaves existing data untouched and does not echo arbitrary filesystem exception text");
            Check(SessionReport.FindLatestReport(blockedDirectory)==null,"unusable report directory has no readable previous report");

            // File replacement honours Windows share modes. Linux rename semantics
            // do not reproduce an Excel lock, so only Windows proves this case.
            if(Environment.OSVersion.Platform==PlatformID.Win32NT) {
                var lockedRoot=Path.Combine(directory,"windows-lock");
                var path=SessionReport.Write(lockedRoot,day,utc,null,null,null,null,null,"");
                var original=File.ReadAllBytes(path);
                error=null;
                using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)) {
                    try{SessionReport.Write(lockedRoot,day,utc.AddSeconds(1),null,null,null,null,null,"");}
                    catch(ReportFileWriteException ex){error=ex;}
                    Check(error!=null&&error.ReportPath==path&&error.FailureCode=="SharingViolation",
                        "a real Windows exclusive file lock produces a path-aware sharing failure");
                }
                Check(original.SequenceEqual(File.ReadAllBytes(path))&&!Directory.GetFiles(lockedRoot,"*.tmp-*").Any()
                    &&!File.Exists(path+".bak"),"a locked report is never overwritten and its unsuccessful temporary snapshot is removed");
            }
        }
        static void TestQuantityRefresh(string directory,DateTime day,DateTime utc) {
            var holding=new Holding{Code="005930",Name="SYNTHETIC",Quantity=0,AvgPrice=10000,Price=10100};
            var balance=new BrokerHoldingsSnapshot{BasisVerified=true,RequestBasisVerified=true,
                RequestStartedUtc=utc.AddMilliseconds(-10),ReceivedUtc=utc};
            balance.Holdings.Add(holding);balance.SellableQuantities[holding.Code]=2;
            balance.ReportedQuantities[holding.Code]=0;
            balance.UnfilledQuantities[holding.Code]=2;
            var accepted=new LiveOrderRecord{Code=holding.Code,Kind=LiveOrderKind.Buy,Quantity=2,
                LimitPrice=10000,BrokerOrderNumber="5522",Status=LiveOrderStatus.Working,UpdatedUtc=utc};
            var orders=new BrokerOrdersSnapshot{TradingDate="20261113",ReceivedUtc=utc};
            orders.Orders.Add(new BrokerOrderRow{OrderNumber="5522",Code=holding.Code,SideCode="2",
                Quantity=2,LimitPrice=10000,FilledQuantity=0,OpenQuantity=2,ConfirmationCode="0"});
            var root=Path.Combine(directory,"quantity-refresh");
            var path=SessionReport.Write(root,day,utc,new[]{holding},utc,null,orders,new[]{accepted},
                "실주문 운영 시작됨",null,balance);
            var text=File.ReadAllText(path);
            Check(text.Contains("\"잔고 수량 대조\",\"불일치: 추가 매수 보류 / 계좌 조회·감시 계속\"")
                &&text.Contains("\"평가 확정상태\",\"수량 대사 중: 수량·단가 계산금액 미확인 / 증권사 평가 원본 별도 기록\""),
                "quantity disagreement is reported as provisional exposure while monitoring and authorization remain distinct");
            Check(text.Contains("\"가격 조회시각\",\"주문가능수량(MO10)\",\"미체결수량(MO11)\",\"수량 대조\",\"평가 기준\"")
                &&text.Contains("\"'005930\",0,10000,10100,\"미확인\",\"미확인\",\"미확인\",\"미확인\",\"2026-11-13 06:20:00 UTC\",2,2,\"주문가능수량이 잔고수량보다 큼\",\"잠정: 수량 대사 중 / 체결 추정 없음\""),
                "appended columns preserve original zero holdings, positive orderable and directionless pending quantities without shifting existing values");
            Check(text.Contains("\"총 매입금액\",\"미확인\"")&&text.Contains("\"총 평가금액\",\"미확인\"")
                &&text.Contains("\"'5522\",\"미확인\",\"'005930\",\"Buy\",2,0,0,10000,\"Working\"")
                &&!text.Contains("\"Filled\""),
                "uncertain valuation is not a reported zero or inflated from orderable shares and accepted broker identity remains working without an invented fill");
            Check(holding.Quantity==0&&balance.SellableQuantities[holding.Code]==2
                &&accepted.Status==LiveOrderStatus.Working&&accepted.FilledQuantity==0,
                "reporting disagreement preserves source quantities and durable order evidence");
            holding.Quantity=2;balance.ReportedQuantities[holding.Code]=2;balance.UnfilledQuantities[holding.Code]=0;
            balance.RequestStartedUtc=utc.AddSeconds(1);balance.ReceivedUtc=utc.AddSeconds(2);
            SessionReport.Write(root,day,utc.AddSeconds(2),new[]{holding},balance.ReceivedUtc,null,orders,
                new[]{accepted},"실주문 운영 시작됨",null,balance);
            text=File.ReadAllText(path);
            Check(text.Contains("\"잔고 수량 대조\",\"관측 수량 불일치 없음 / 주문 직전 별도 검증\"")
                &&text.Contains("\"평가 확정상태\",\"응답 잔고수량 기준(비용 제외)\"")
                &&!text.Contains("잠정: 수량 대사 중"),
                "a fresh consistent holdings snapshot clears provisional quantity status through its actual values");
            Check(text.Contains("\"'005930\",2,10000,10100,20000,20200,200,1,")
                &&text.Contains(",2,0,\"관측 수량 불일치 없음\",\"응답 잔고수량 기준(비용 제외)\"")
                &&accepted.FilledQuantity==0&&accepted.Status==LiveOrderStatus.Working,
                "updated broker quantities change valuation while order fill confirmation still requires independent evidence");
        }
        static void TestRawBrokerHoldings(string directory,DateTime day,DateTime utc) {
            var held=new Holding{Code="005930",Quantity=0,AvgPrice=10000,Price=9900};
            var trade=new BrokerHoldingsSnapshot{RequestBasisVerified=true,BasisVerified=true,
                RequestStartedUtc=utc.AddSeconds(-2),ReceivedUtc=utc.AddSeconds(-1),BrokerCostBasis=20200,
                BrokerMarketValue=19800,UnrealizedProfit=-400,BrokerReturnPercent=-1.9802m};
            trade.Holdings.Add(held);trade.SellableQuantities[held.Code]=18;trade.UnfilledQuantities[held.Code]=0;
            trade.ReportedQuantities[held.Code]=0;
            trade.Details[held.Code]=new BrokerHoldingDetails{PreviousSellQuantity=1,TodaySellQuantity=2,
                PreviousBuyQuantity=3,TodayBuyQuantity=4,SettlementQuantity=5,EvaluationQuantity=6,
                MarketValue=19800,UnrealizedProfit=-246.8m,ReturnPercent=-1.234m,AveragePrice=10000,CurrentPrice=9900};
            var ordinary=new BrokerHoldingsSnapshot{RequestOrdinaryOnlyVerified=true,
                RequestStartedUtc=utc.AddSeconds(-4),ReceivedUtc=utc.AddSeconds(-3)};
            ordinary.Holdings.Add(new Holding{Code=held.Code,Quantity=9,AvgPrice=10000,Price=9900});
            ordinary.ReportedQuantities[held.Code]=9;
            ordinary.SellableQuantities[held.Code]=8;ordinary.UnfilledQuantities[held.Code]=7;
            ordinary.Details[held.Code]=new BrokerHoldingDetails{PreviousSellQuantity=6,TodaySellQuantity=5,
                PreviousBuyQuantity=4,TodayBuyQuantity=3,SettlementQuantity=2,EvaluationQuantity=1,
                MarketValue=12345,UnrealizedProfit=678,ReturnPercent=9.87m};
            var accepted=new LiveOrderRecord{Code=held.Code,Kind=LiveOrderKind.Buy,Quantity=18,
                LimitPrice=10000,BrokerOrderNumber="5523",Status=LiveOrderStatus.Working,UpdatedUtc=utc};
            var path=SessionReport.Write(Path.Combine(directory,"raw-broker-holdings"),day,utc,new[]{held},trade.ReceivedUtc,
                null,null,new[]{accepted},"실주문 운영 시작됨",null,trade,ordinary);
            var text=File.ReadAllText(path);
            Check(text.Contains("\"'005930\",0,10000,9900,\"미확인\",\"미확인\",\"미확인\",-1.234,")
                &&text.Contains("\"증권사 SABA609Q1 MO20 / 매매기준 SI3=1\",\"2026-11-13 06:19:58 UTC\",\"2026-11-13 06:19:59 UTC\",-1"),
                "a signed broker rate remains numeric for zero reported shares with its exact source and callback times while unit-price change is separately named");
            Check(text.Contains("\"총 매입금액\",\"미확인\"\r\n")&&text.Contains("\"총 평가금액\",\"미확인\"\r\n")
                &&text.Contains("\"평가손익(비용제외)\",\"미확인\"\r\n")
                &&text.Contains("\"평가수익률(%)\",\"미확인\"\r\n")
                &&text.Contains("\"총 매입금액(증권사 SO15)\",20200\r\n")
                &&text.Contains("\"총 평가금액(증권사 SO18)\",19800\r\n")
                &&text.Contains("\"평가손익(증권사 SO19)\",-400\r\n")
                &&text.Contains("\"평가수익률(증권사 SO22)\",-1.9802\r\n"),
                "broker totals remain distinct from unknown MO4-derived totals and never replace reported holdings quantity");
            int tradeStart=text.IndexOf("\"잔고 원본 수량·평가(매매기준)\"",StringComparison.Ordinal);
            int ordinaryStart=text.IndexOf("\"잔고 원본 수량·평가(보통잔고)\"",StringComparison.Ordinal);
            int ordersStart=text.IndexOf("\"증권사 주문 조회(누적 체결 스냅샷)\"",StringComparison.Ordinal);
            var tradeText=text.Substring(tradeStart,ordinaryStart-tradeStart);
            var ordinaryText=text.Substring(ordinaryStart,ordersStart-ordinaryStart);
            Check(tradeText.Contains("\"조회 기준\",\"매매기준 SI3=1\"")
                &&tradeText.Contains("\"'005930\",0,1,2,3,4,5,18,0,19800,-246.8,-1.234,6,10000,9900,")
                &&tradeText.Contains("\"요청시각\",\"2026-11-13 06:19:58 UTC\"")
                &&tradeText.Contains("\"응답시각\",\"2026-11-13 06:19:59 UTC\""),
                "trade raw fields preserve every independently named quantity and signed valuation from their own request and callback");
            Check(ordinaryText.Contains("\"조회 기준\",\"보통잔고 SI3=5\"")
                &&ordinaryText.Contains("\"'005930\",9,6,5,4,3,2,8,7,12345,678,9.87,1,\"미확인\",\"미확인\",")
                &&ordinaryText.Contains("\"요청시각\",\"2026-11-13 06:19:56 UTC\"")
                &&ordinaryText.Contains("\"응답시각\",\"2026-11-13 06:19:57 UTC\"")
                &&!ordinaryText.Contains("-246.8")&&!tradeText.Contains("9.87"),
                "ordinary-basis raw values and provenance cannot be merged into trade-basis quantities or rates");
            Check(held.Quantity==0&&trade.SellableQuantities[held.Code]==18&&trade.Details[held.Code].EvaluationQuantity==6
                &&accepted.FilledQuantity==0&&accepted.Status==LiveOrderStatus.Working
                &&text.Contains("\"'5523\",\"미확인\",\"'005930\",\"Buy\",18,0,0,10000,\"Working\""),
                "raw diagnostic values neither infer a balance nor fabricate an order fill or change the accepted identity");
            held.Price=9800;
            SessionReport.Write(Path.GetDirectoryName(path),day,utc,new[]{held},trade.ReceivedUtc,null,null,
                new[]{accepted},"실주문 운영 시작됨",null,trade,ordinary);
            text=File.ReadAllText(path);
            Check(text.Contains("\"'005930\",0,10000,9800,")
                &&text.Contains("\"'005930\",0,1,2,3,4,5,18,0,19800,-246.8,-1.234,6,10000,9900,")
                &&trade.Details[held.Code].CurrentPrice==9900,
                "a later quote updates the displayed holding price while raw MO12 and MO13 remain the original callback values");
            held.Price=9900;
            trade.BrokerMarketValue=0;
            SessionReport.Write(Path.GetDirectoryName(path),day,utc,new[]{held},trade.ReceivedUtc,null,null,
                new[]{accepted},"실주문 운영 시작됨",null,trade,ordinary);
            text=File.ReadAllText(path);
            Check(text.Contains("\"'005930\",0,10000,9900,\"미확인\",\"미확인\",\"미확인\",")
                &&text.Contains("\"총 평가금액\",\"미확인\"\r\n")
                &&text.Contains("\"총 평가금액(증권사 SO18)\",0\r\n")&&held.Quantity==0,
                "raw zero shares, unknown quantity-derived money and an independently reported broker zero remain three distinct facts");
            trade.Details[held.Code]=new BrokerHoldingDetails();trade.BrokerCostBasis=null;
            trade.BrokerMarketValue=null;trade.UnrealizedProfit=null;trade.BrokerReturnPercent=null;
            SessionReport.Write(Path.GetDirectoryName(path),day,utc,new[]{held},trade.ReceivedUtc,null,null,
                new[]{accepted},"실주문 운영 시작됨",null,trade);
            text=File.ReadAllText(path);tradeStart=text.IndexOf("\"잔고 원본 수량·평가(매매기준)\"",StringComparison.Ordinal);
            ordinaryStart=text.IndexOf("\"잔고 원본 수량·평가(보통잔고)\"",StringComparison.Ordinal);
            tradeText=text.Substring(tradeStart,ordinaryStart-tradeStart);
            ordinaryText=text.Substring(ordinaryStart);
            Check(tradeText.Contains("\"'005930\",0,\"미확인\",\"미확인\",\"미확인\",\"미확인\",\"미확인\",18,0,\"미확인\",\"미확인\",\"미확인\",\"미확인\",\"미확인\",\"미확인\",")
                &&text.Contains("\"총 매입금액(증권사 SO15)\",\"미확인\"")
                &&text.Contains("\"평가수익률(증권사 SO22)\",\"미확인\""),
                "absent optional raw quantities, valuation and rates remain unknown rather than fabricated zeros");
            Check(text.Contains("\"'005930\",0,10000,9900,\"미확인\",\"미확인\",\"미확인\",\"미확인\",")
                &&!text.Contains("증권사 SABA609Q1 MO20 /")
                &&ordinaryText.StartsWith("\"잔고 원본 수량·평가(보통잔고)\"\r\n\"조회 기준\",\"미확인\"\r\n\"요청시각\",\"미확인\"",StringComparison.Ordinal),
                "without a broker rate zero holdings return remains unknown while absent ordinary data has no invented basis or timestamps");
            ordinary.RequestStartedUtc=day;
            Reject(()=>SessionReport.Write(Path.GetDirectoryName(path),day,utc,new[]{held},trade.ReceivedUtc,
                null,null,null,"",null,trade,ordinary),"ordinary raw snapshot request time must explicitly be UTC");
            ordinary.RequestStartedUtc=utc;ordinary.ReceivedUtc=day;
            Reject(()=>SessionReport.Write(Path.GetDirectoryName(path),day,utc,new[]{held},trade.ReceivedUtc,
                null,null,null,"",null,trade,ordinary),"ordinary raw snapshot response time must explicitly be UTC");
        }
        static void TestVerifiedCurrentQuantity(string directory,DateTime day,DateTime utc) {
            var schema=IndiHoldingsSchema.Inspect(IndiHoldingsSchemaTests.ModernDefinition());
            var trade=AccountQueries.ReadHoldings(new ExpandedHoldingsResponse(),utc,true,false,schema);
            var ordinary=AccountQueries.ReadHoldings(new ExpandedHoldingsResponse(),utc.AddSeconds(1),false,true,schema);
            trade.RequestStartedUtc=utc.AddMilliseconds(-10);ordinary.RequestStartedUtc=utc.AddMilliseconds(10);
            var path=SessionReport.Write(Path.Combine(directory,"verified-current-quantity"),day,utc.AddSeconds(2),
                trade.Holdings,trade.ReceivedUtc,null,null,null,"실주문 운영 시작됨",null,trade,ordinary);
            var text=File.ReadAllText(path);
            Check(schema.Recognized&&trade.Holdings[0].Quantity==18&&trade.ReportedQuantities["005930"]==0
                &&trade.QuantitySource("005930")==BrokerHoldingQuantitySource.VerifiedEvaluationMo32
                &&trade.IsCurrentQuantityVerified("005930"),
                "a structurally recognized synthetic callback selects direct evaluation quantity while preserving original MO4 zero");
            Check(text.Contains("\"'005930\",18,8160,7870,146880,141660,-5220,-3.5539,")
                &&text.Contains("\"총 매입금액\",146880\r\n")&&text.Contains("\"총 평가금액\",141660\r\n"),
                "report holdings and valuation use verified direct current quantity rather than zero legacy quantity or orderable inference");
            int tradeStart=text.IndexOf("\"잔고 원본 수량·평가(매매기준)\"",StringComparison.Ordinal);
            int ordinaryStart=text.IndexOf("\"잔고 원본 수량·평가(보통잔고)\"",StringComparison.Ordinal);
            int ordersStart=text.IndexOf("\"증권사 주문 조회(누적 체결 스냅샷)\"",StringComparison.Ordinal);
            var tradeText=text.Substring(tradeStart,ordinaryStart-tradeStart);
            var ordinaryText=text.Substring(ordinaryStart,ordersStart-ordinaryStart);
            Check(tradeText.Contains("\"'005930\",0,0,0,0,18,18,18,0,141660,-5220,-3.5539,18,8160,7870,18,")
                &&tradeText.Contains("\"VerifiedEvaluationMo32\",\"확인\"")
                &&tradeText.Contains("\"Modern0208\",\""+schema.Sha256+"\""),
                "raw trade export retains MO4 zero and exact independent fields before appending canonical MO32 quantity and immutable schema evidence");
            Check(ordinaryText.Contains("\"조회 기준\",\"보통잔고 SI3=5\"")
                &&ordinaryText.Contains("\"'005930\",0,0,0,0,18,18,18,0,141660,-5220,-3.5539,18,8160,7870,")
                &&trade.ReportedQuantities["005930"]==0&&ordinary.ReportedQuantities["005930"]==0,
                "ordinary and trade views preserve their own original MO4 zeros even when their direct evaluation quantity is positive");
            var unknownSchema=IndiHoldingsSchema.Inspect(new byte[]{0});
            var unverified=AccountQueries.ReadHoldings(new ExpandedHoldingsResponse(),utc,true,false,unknownSchema);
            unverified.RequestStartedUtc=utc.AddMilliseconds(-10);
            SessionReport.Write(Path.GetDirectoryName(path),day,utc,unverified.Holdings,unverified.ReceivedUtc,
                null,null,null,"실주문 운영 시작됨",null,unverified);
            text=File.ReadAllText(path);
            Check(unverified.Holdings[0].Quantity==0&&!unverified.IsCurrentQuantityVerified("005930")
                &&text.Contains("\"'005930\",0,8160,7870,\"미확인\",\"미확인\",\"미확인\",")
                &&text.Contains("\"ReportedMo4\",\"미확인\"")
                &&text.Contains("\"총 매입금액\",\"미확인\"\r\n"),
                "an unrecognized expanded profile keeps original quantity and unknown derived money instead of certifying positive evaluation or orderable shares");
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
                Check(text.Contains("\"거부코드 형태\",\"거부코드명 형태\",\"상태명 분류\",\"확인구분 분류\",\"체결·확인 대조\"")
                    &&text.Contains("\"Blank\",\"Present\",\"Other\",\"Zero\",\"거부명세 확인 필요\""),
                    "appended broker metadata classifications preserve the old columns without exposing arbitrary provider text");
                Check(positions[0].Price==65000&&orders.Orders[0].FilledQuantity==1&&journal[0].FilledQuantity==0,"reporting does not mutate holding or order evidence");

                var conflictOrders=new BrokerOrdersSnapshot{TradingDate="20261113",ReceivedUtc=utc};
                var conflictRow=new BrokerOrderRow{OrderNumber="9021",Code="005930",SideCode="1",Quantity=2,LimitPrice=8500,
                    FilledQuantity=2,AverageFillPrice=8500,OpenQuantity=0,ConfirmedQuantity=0,ConfirmationCode="1",StatusName="확인"};
                conflictOrders.Orders.Add(conflictRow);
                var conflictPath=SessionReport.Write(directory,day.AddDays(3),utc,null,null,null,conflictOrders,null,"실주문 미시작");
                var conflictText=File.ReadAllText(conflictPath);
                Check(conflictText.Contains("\"'9021\",\"미확인\",\"'005930\",\"매도\",2,8500,2,8500,0,0,\"1\",\"Blank\",\"Blank\",\"확인\",\"Reject\",\"전량체결 수량·확인명세 충돌\""),
                    "a synthetic full-fill row with literal confirmation 1 remains visible as conflicting evidence rather than normal or an inferred execution");
                Check(conflictRow.ConfirmationCode=="1"&&conflictRow.HasRejection&&!conflictRow.TerminalConfirmationCompatible,
                    "diagnostic reporting cannot turn conflicting confirmation 1 into acceptance or recovery authorization");
                foreach(var status in new[]{"의뢰","접수","확인","거부","=CMD()","SECRET_ACCOUNT_12345678901"}) {
                    conflictRow.StatusName=status;conflictRow.RejectCode="PASSWORD_4321";conflictRow.RejectName=" =SUM(SECRET_ACCOUNT_12345678901)";
                    SessionReport.Write(directory,day.AddDays(3),utc,null,null,null,conflictOrders,null,"실주문 미시작");
                    conflictText=File.ReadAllText(conflictPath);
                    Check(!conflictText.Contains("SECRET")&&!conflictText.Contains("PASSWORD")&&!conflictText.Contains("=CMD")&&!conflictText.Contains("=SUM"),
                        "provider metadata remains finite before CSV formula escaping or sensitive free text could enter the report");
                    Check(conflictText.Contains("\"Present\",\"Present\",\""+(status=="의뢰"||status=="접수"||status=="확인"||status=="거부"?status:"Other")+"\",\"Reject\""),
                        "only documented broker statuses are retained while rejection-field presence remains diagnosable");
                }
                conflictRow.QueryProfile=BrokerOrderQueryProfile.ModernOrderState54;conflictRow.QuerySchemaSha256=new string('a',64);
                conflictRow.StatusName="접수";conflictRow.RejectCode="";conflictRow.RejectName="";
                SessionReport.Write(directory,day.AddDays(3),utc,null,null,null,conflictOrders,null,"실주문 미시작");
                conflictText=File.ReadAllText(conflictPath);
                Check(conflictText.Contains("\"1\",\"Blank\",\"Blank\",\"접수\",\"StatusCorroborated\",\"설치 정의·접수 상태 대조 / 전량체결 근거 별도\"")
                    &&conflictText.Contains("\"ModernOrderState54\",\""+new string('a',64)+"\""),
                    "verified modern receipt corroboration exports raw 1 and source profile without renaming it normal");
                Check(conflictRow.ConfirmationCode=="1"&&conflictRow.ConfirmationKind==BrokerConfirmationKind.StatusCorroborated
                    &&!conflictText.Contains("\"정상코드\""),
                    "corroborated status reporting preserves raw metadata and does not authorize or fabricate journal fills");

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
                TestReportDiscovery(directory,utc);
                TestWriteFailures(directory,day,utc);
                TestQuantityRefresh(directory,day,utc);
                TestRawBrokerHoldings(directory,day,utc);
                TestVerifiedCurrentQuantity(directory,day,utc);
            }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
            return checks;
        }
    }
}
