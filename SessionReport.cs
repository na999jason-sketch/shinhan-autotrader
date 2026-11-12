using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ShinhanNativeTrader {
    // Reports broker snapshots and durable order evidence only. No credentials,
    // provider free-text descriptions, inferred executions or synthetic balances.
    public static class SessionReport {
        const string Unknown="미확인";

        static void RequireUtc(DateTime value) {
            if(value.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC report timestamps are required.");
        }
        static string Stamp(DateTime? value) {
            if(!value.HasValue)return Unknown;
            RequireUtc(value.Value);
            return value.Value.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture)+" UTC";
        }
        internal static string EscapeText(string value) {
            value=value??Unknown;
            // Excel can evaluate quoted CSV cells as formulas. Check the first
            // non-whitespace character as well as an initial control character.
            var first=value.TrimStart();
            bool formula=(first.Length>0&&"=+-@".IndexOf(first[0])>=0)
                ||(value.Length>0&&(char.IsControl(value[0])||value[0]=='\ufeff'));
            if(formula)value="'"+value;
            return "\""+value.Replace("\"","\"\"")+"\"";
        }
        static string Cell(object value) {
            if(value==null)return EscapeText(Unknown);
            if(value is decimal)return ((decimal)value).ToString("G29",CultureInfo.InvariantCulture);
            if(value is int)return ((int)value).ToString(CultureInfo.InvariantCulture);
            if(value is long)return ((long)value).ToString(CultureInfo.InvariantCulture);
            return EscapeText(Convert.ToString(value,CultureInfo.InvariantCulture));
        }
        static void Row(StringBuilder report,params object[] values) {
            report.Append(string.Join(",",values.Select(Cell))).Append("\r\n");
        }
        static string Symbol(string code) {
            var normalized=Rules.Code(code);
            // Apostrophe is data, not an Excel formula, and preserves leading zero.
            return normalized==""?Unknown:"'"+normalized;
        }
        static string BrokerNumber(string value) {
            return LiveOrderProtocol.ValidBrokerNumber(value)?"'"+value:Unknown;
        }
        static string Flag(string value) {
            return !string.IsNullOrEmpty(value)&&value.Length<=2&&value.All(c=>c>='0'&&c<='9')?value:Unknown;
        }
        static string TradingDate(string value) {
            DateTime date;
            return DateTime.TryParseExact(value,"yyyyMMdd",CultureInfo.InvariantCulture,DateTimeStyles.None,out date)
                ?date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture):Unknown;
        }
        static string Side(string code) {return code=="1"?"매도":code=="2"?"매수":Unknown;}
        static string EnumName<T>(T value) where T:struct {
            return Enum.IsDefined(typeof(T),value)?value.ToString():Unknown;
        }
        static string GateStatus(string requested) {
            // The UI passes the compiled gate message, never arbitrary provider
            // text. Refuse other strings instead of risking credential echo.
            return string.Equals(requested,LiveOrderSafety.BlockReason,StringComparison.Ordinal)
                ?LiveOrderSafety.BlockReason:"실주문 상태 미확인";
        }

        public static string Write(string directory,DateTime koreaDate,DateTime writtenUtc,
            IEnumerable<Holding> holdings,DateTime? holdingsReceivedUtc,AccountCashSnapshot cash,
            BrokerOrdersSnapshot orders,IEnumerable<LiveOrderRecord> journalOrders,string orderGateStatus,
            IDictionary<string,DateTime> priceReceivedUtc=null) {
            if(string.IsNullOrWhiteSpace(directory))throw new ArgumentException("A report directory is required.");
            if(koreaDate.Kind!=DateTimeKind.Unspecified||koreaDate.TimeOfDay!=TimeSpan.Zero)
                throw new ArgumentException("An unspecified KST calendar date is required.");
            RequireUtc(writtenUtc);
            if(holdingsReceivedUtc.HasValue)RequireUtc(holdingsReceivedUtc.Value);
            if(cash!=null)RequireUtc(cash.ReceivedUtc);
            if(orders!=null)RequireUtc(orders.ReceivedUtc);

            // Materialize and validate before opening any output file. A failed
            // valuation must leave the preceding complete report untouched.
            var positions=holdings==null?null:holdings.ToList();
            var values=positions==null?null:positions.Select(HoldingValuation.From).ToList();
            var evidence=journalOrders==null?null:journalOrders.ToList();
            bool holdingsKnown=values!=null&&holdingsReceivedUtc.HasValue;
            var total=holdingsKnown?AccountValuation.From(positions):null;
            var report=new StringBuilder();
            Row(report,"실계좌 운영 보고서",koreaDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            Row(report,"보고서 생성시각",Stamp(writtenUtc));
            Row(report,"잔고 응답시각",Stamp(holdingsReceivedUtc));
            Row(report,"예수금 응답시각",cash==null?Unknown:Stamp(cash.ReceivedUtc));
            Row(report,"주문 조회 응답시각",orders==null?Unknown:Stamp(orders.ReceivedUtc));
            Row(report,"실주문 상태",GateStatus(orderGateStatus));
            Row(report,"평가 기준","평가손익·수익률은 수수료·세금 제외. 실현손익을 추정하지 않음.");
            Row(report,"잔고 조회상태",holdingsKnown?"응답 수신":Unknown);
            Row(report,"보유 종목 수",holdingsKnown?(object)values.Count:null);
            Row(report,"총 매입금액",total==null?null:(object)total.CostBasis);
            Row(report,"총 평가금액",total==null?null:(object)total.MarketValue);
            Row(report,"평가손익(비용제외)",total==null?null:(object)total.UnrealizedProfit);
            Row(report,"평가수익률(%)",total==null?null:(object)total.ReturnPercent);
            Row(report,"예수금",cash==null?null:(object)cash.DepositCash);
            Row(report,"주문가능현금",cash==null?null:(object)cash.CashOrderable);
            Row(report,"현금100% 주문가능금액",cash==null?null:(object)cash.Cash100Orderable);
            Row(report,"주문가능금액 의미 검증",cash!=null&&cash.OrderSemanticsVerified?"확인":"미검증");
            Row(report);
            Row(report,"실계좌 보유 종목");
            Row(report,"코드","수량","평균단가","현재가","매입금액","평가금액","평가손익(비용제외)","수익률(%)","가격 조회시각");
            if(holdingsKnown)foreach(var value in values) {
                DateTime quoteUtc;
                var received=priceReceivedUtc!=null&&priceReceivedUtc.TryGetValue(value.Code,out quoteUtc)
                    ?(DateTime?)quoteUtc:holdingsReceivedUtc;
                Row(report,Symbol(value.Code),value.Quantity,value.AveragePrice>0?(object)value.AveragePrice:null,
                    value.CurrentPrice>0?(object)value.CurrentPrice:null,value.CostBasis,value.MarketValue,
                    value.UnrealizedProfit,value.ReturnPercent,Stamp(received));
            }
            Row(report);
            Row(report,"증권사 주문 조회(누적 체결 스냅샷)");
            Row(report,"거래일",orders==null?Unknown:TradingDate(orders.TradingDate));
            Row(report,"응답 범위",orders!=null&&orders.IsComplete?"완료 확인":"완료 여부 미검증");
            Row(report,"개별 체결번호",orders!=null&&orders.HasExecutionIds?"제공":"미제공 또는 미검증");
            Row(report,"주문번호","원주문번호","코드","매매구분","주문수량","주문가격","누적체결수량","평균체결가격","미체결수량","확인수량","확인구분");
            if(orders!=null)foreach(var order in orders.Orders) {
                if(order==null)throw new InvalidOperationException("A broker order report row is missing.");
                Row(report,BrokerNumber(order.OrderNumber),BrokerNumber(order.OriginalOrderNumber),Symbol(order.Code),
                    Side(order.SideCode),order.Quantity,order.LimitPrice,order.FilledQuantity,
                    order.AverageFillPrice,order.OpenQuantity,order.ConfirmedQuantity,Flag(order.ConfirmationCode));
            }
            Row(report);
            Row(report,"프로그램 실주문 기록(접수·체결 구분)");
            Row(report,"주문번호","원주문번호","코드","종류","수량","확인된 체결수량","확인된 취소수량","지정가","상태","기록시각");
            if(evidence!=null)foreach(var order in evidence) {
                if(order==null)throw new InvalidOperationException("A journal report row is missing.");
                Row(report,BrokerNumber(order.BrokerOrderNumber),BrokerNumber(order.OriginalBrokerOrderNumber),Symbol(order.Code),
                    EnumName(order.Kind),order.Quantity,order.FilledQuantity,order.CancelledQuantity,order.LimitPrice,
                    EnumName(order.Status),Stamp(order.UpdatedUtc));
            }

            var root=Path.GetFullPath(directory);
            Directory.CreateDirectory(root);
            var path=Path.Combine(root,"account-report-"+koreaDate.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+".csv");
            var temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
            try {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    using(var writer=new StreamWriter(file,new UTF8Encoding(true),4096,true)) {
                        writer.Write(report.ToString());writer.Flush();
                    }
                    file.Flush(true);
                }
                if(File.Exists(path))File.Replace(temporary,path,path+".bak");
                else File.Move(temporary,path);
                return path;
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
