using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ShinhanNativeTrader {
    public sealed class ReportFileWriteException : IOException {
        public string ReportPath { get; private set; }
        public string FailureCode { get; private set; }

        internal ReportFileWriteException(string reportPath,Exception cause)
            :base("보고서 파일을 저장하지 못했습니다. 파일 열림 상태, 쓰기 권한 또는 디스크 상태를 확인하세요.",cause) {
            ReportPath=reportPath;
            var io=cause as IOException;
            var nativeCode=io==null?0:io.HResult&0xffff;
            FailureCode=cause is UnauthorizedAccessException?"AccessDenied"
                :nativeCode==32||nativeCode==33?"SharingViolation":"IoFailure";
        }
    }

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
        static string RejectionMetadataShape(string value) {
            return string.IsNullOrWhiteSpace(value)?"Blank":value.All(character=>character=='0')?"Zero":"Present";
        }
        static string BrokerStatusClass(string value) {
            // Keep the vendor's documented finite statuses; never export
            // arbitrary provider text that might contain account credentials.
            return value=="의뢰"||value=="접수"||value=="확인"||value=="거부"?value:
                string.IsNullOrWhiteSpace(value)?"Blank":"Other";
        }
        static string FillConfirmationComparison(BrokerOrderRow order) {
            bool full=order.Quantity>0&&order.FilledQuantity==order.Quantity&&order.OpenQuantity==0;
            if(full&&!order.TerminalConfirmationCompatible)return "전량체결 수량·확인명세 충돌";
            return order.HasRejectionConflict?"거부명세 확인 필요":order.ConfirmationKind==BrokerConfirmationKind.Unavailable?"확인구분 공란":
                order.ConfirmationKind==BrokerConfirmationKind.Unknown?"확인구분 미검증":"정상코드";
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
            // Accept only finite UI-owned states, never arbitrary provider text
            // or a detailed error that could accidentally contain credentials.
            if(requested=="실주문 운영 시작됨"||requested=="실주문 미시작")return requested;
            return string.Equals(requested,LiveOrderSafety.BlockReason,StringComparison.Ordinal)
                ?LiveOrderSafety.BlockReason:"실주문 상태 미확인";
        }

        sealed class ReportCandidate {
            public string Path;
            public DateTime WrittenUtc,ReportDate;
        }
        static bool IsAccountDirectory(string path) {
            var name=Path.GetFileName(path);
            return name.Length==64&&name.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'||c>='A'&&c<='F');
        }
        static bool IsPlainDirectory(string path) {
            var attributes=File.GetAttributes(path);
            return (attributes&FileAttributes.Directory)!=0&&(attributes&FileAttributes.ReparsePoint)==0;
        }
        static void CollectReports(string directory,List<ReportCandidate> candidates) {
            string[] files;
            try{files=Directory.GetFiles(directory,"account-report-*.csv",SearchOption.TopDirectoryOnly);}
            catch(IOException){return;}
            catch(UnauthorizedAccessException){return;}
            catch(System.Security.SecurityException){return;}
            foreach(var file in files)try {
                var name=Path.GetFileName(file);
                if(name.Length!=27||!name.StartsWith("account-report-",StringComparison.Ordinal)
                    ||!name.EndsWith(".csv",StringComparison.Ordinal))continue;
                var text=name.Substring(15,8);
                DateTime day;
                if(!text.All(c=>c>='0'&&c<='9')||!DateTime.TryParseExact(text,"yyyyMMdd",
                    CultureInfo.InvariantCulture,DateTimeStyles.None,out day))continue;
                var attributes=File.GetAttributes(file);
                if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)continue;
                candidates.Add(new ReportCandidate{Path=file,ReportDate=day,WrittenUtc=File.GetLastWriteTimeUtc(file)});
            }catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}
        }
        // Finding a previous file never establishes an active account or order
        // state. Only the standard report root and immediate account hashes are searched.
        public static string FindLatestReport(string reportsRoot) {
            if(string.IsNullOrWhiteSpace(reportsRoot))return null;
            try {
                var root=Path.GetFullPath(reportsRoot);
                if(!Directory.Exists(root)||!IsPlainDirectory(root))return null;
                var candidates=new List<ReportCandidate>();
                CollectReports(root,candidates);
                string[] directories;
                try{directories=Directory.GetDirectories(root);}
                catch(IOException){directories=new string[0];}
                catch(UnauthorizedAccessException){directories=new string[0];}
                catch(System.Security.SecurityException){directories=new string[0];}
                foreach(var directory in directories)try {
                    if(IsAccountDirectory(directory)&&IsPlainDirectory(directory))CollectReports(directory,candidates);
                }catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}
                return candidates.OrderByDescending(candidate=>candidate.WrittenUtc)
                    .ThenByDescending(candidate=>candidate.ReportDate)
                    .ThenBy(candidate=>candidate.Path,StringComparer.Ordinal)
                    .Select(candidate=>candidate.Path).FirstOrDefault();
            }catch(ArgumentException){return null;}
            catch(NotSupportedException){return null;}
            catch(IOException){return null;}
            catch(UnauthorizedAccessException){return null;}
            catch(System.Security.SecurityException){return null;}
        }

        public static string Write(string directory,DateTime koreaDate,DateTime writtenUtc,
            IEnumerable<Holding> holdings,DateTime? holdingsReceivedUtc,AccountCashSnapshot cash,
            BrokerOrdersSnapshot orders,IEnumerable<LiveOrderRecord> journalOrders,string orderGateStatus,
            IDictionary<string,DateTime> priceReceivedUtc=null,BrokerHoldingsSnapshot tradeHoldings=null) {
            if(string.IsNullOrWhiteSpace(directory))throw new ArgumentException("A report directory is required.");
            if(koreaDate.Kind!=DateTimeKind.Unspecified||koreaDate.TimeOfDay!=TimeSpan.Zero)
                throw new ArgumentException("An unspecified KST calendar date is required.");
            RequireUtc(writtenUtc);
            if(holdingsReceivedUtc.HasValue)RequireUtc(holdingsReceivedUtc.Value);
            if(cash!=null)RequireUtc(cash.ReceivedUtc);
            if(orders!=null)RequireUtc(orders.ReceivedUtc);
            DateTime? tradeRequestUtc=null;
            if(tradeHoldings!=null) {
                RequireUtc(tradeHoldings.ReceivedUtc);
                if(tradeHoldings.RequestStartedUtc!=default(DateTime)) {
                    RequireUtc(tradeHoldings.RequestStartedUtc);tradeRequestUtc=tradeHoldings.RequestStartedUtc;
                }
            }

            // Materialize and validate before opening any output file. A failed
            // valuation must leave the preceding complete report untouched.
            var positions=holdings==null?null:holdings.ToList();
            var values=positions==null?null:positions.Select(HoldingValuation.From).ToList();
            var evidence=journalOrders==null?null:journalOrders.ToList();
            bool holdingsKnown=values!=null&&holdingsReceivedUtc.HasValue;
            var total=holdingsKnown?AccountValuation.From(positions):null;
            // A journal fill price or sale proceeds do not establish realized
            // profit. Use only the broker field for the report's validated day.
            decimal? realized=tradeHoldings!=null&&tradeHoldings.RealizedProfit.HasValue&&tradeHoldings.BrokerDateValidated
                &&string.Equals(tradeHoldings.BrokerDate,koreaDate.ToString("yyyyMMdd",CultureInfo.InvariantCulture),StringComparison.Ordinal)
                ?tradeHoldings.RealizedProfit:null;
            var report=new StringBuilder();
            Row(report,"실계좌 운영 보고서",koreaDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
            Row(report,"보고서 생성시각",Stamp(writtenUtc));
            Row(report,"잔고 응답시각",Stamp(holdingsReceivedUtc));
            Row(report,"예수금 응답시각",cash==null?Unknown:Stamp(cash.ReceivedUtc));
            Row(report,"주문 조회 응답시각",orders==null?Unknown:Stamp(orders.ReceivedUtc));
            Row(report,"실현손익 조회 요청시각",Stamp(tradeRequestUtc));
            Row(report,"실현손익 조회 응답시각",tradeHoldings==null?Unknown:Stamp(tradeHoldings.ReceivedUtc));
            Row(report,"실현손익 기준일(증권사)",tradeHoldings!=null&&tradeHoldings.BrokerDateValidated?TradingDate(tradeHoldings.BrokerDate):Unknown);
            Row(report,"실주문 상태",GateStatus(orderGateStatus));
            Row(report,"평가 기준","평가손익·수익률은 수수료·세금 제외. 실현손익을 추정하지 않음.");
            Row(report,"실현손익 기준","증권사 응답값을 기록하며 날짜 미검증·불일치는 미확인으로 표시.");
            Row(report,"잔고 조회상태",holdingsKnown?"응답 수신":Unknown);
            Row(report,"주문 잔고기준 검증",tradeHoldings==null?Unknown:tradeHoldings.BasisVerified?"매매기준 확인":"미확인: 주문 근거로 사용하지 않음");
            Row(report,"매매기준 요청·응답 확인",tradeHoldings==null?Unknown:tradeHoldings.RequestBasisVerified?"확인":"미확인");
            Row(report,"잔고 기준값 진단",tradeHoldings==null?Unknown:AccountQueries.BasisDiagnosticsSummary(tradeHoldings));
            Row(report,"보유 종목 수",holdingsKnown?(object)values.Count:null);
            Row(report,"총 매입금액",total==null?null:(object)total.CostBasis);
            Row(report,"총 평가금액",total==null?null:(object)total.MarketValue);
            Row(report,"평가손익(비용제외)",total==null?null:(object)total.UnrealizedProfit);
            Row(report,"평가수익률(%)",total==null?null:(object)total.ReturnPercent);
            Row(report,"실현손익(증권사 조회)",realized);
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
            Row(report,"주문번호","원주문번호","코드","매매구분","주문수량","주문가격","누적체결수량","평균체결가격","미체결수량","확인수량","확인구분",
                "거부코드 형태","거부코드명 형태","상태명 분류","확인구분 분류","체결·확인 대조");
            if(orders!=null)foreach(var order in orders.Orders) {
                if(order==null)throw new InvalidOperationException("A broker order report row is missing.");
                Row(report,BrokerNumber(order.OrderNumber),BrokerNumber(order.OriginalOrderNumber),Symbol(order.Code),
                    Side(order.SideCode),order.Quantity,order.LimitPrice,order.FilledQuantity,
                    order.AverageFillPrice,order.OpenQuantity,order.ConfirmedQuantity,Flag(order.ConfirmationCode),
                    RejectionMetadataShape(order.RejectCode),RejectionMetadataShape(order.RejectName),BrokerStatusClass(order.StatusName),
                    order.ConfirmationShape,FillConfirmationComparison(order));
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

            var path=Path.Combine(directory,"account-report-"+koreaDate.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+".csv");
            string temporary=null;
            try {
                path=Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    using(var writer=new StreamWriter(file,new UTF8Encoding(true),4096,true)) {
                        writer.Write(report.ToString());writer.Flush();
                    }
                    file.Flush(true);
                }
                if(File.Exists(path))File.Replace(temporary,path,path+".bak");
                else File.Move(temporary,path);
                return path;
            }catch(IOException ex){throw new ReportFileWriteException(path,ex);}
            catch(UnauthorizedAccessException ex){throw new ReportFileWriteException(path,ex);}
            finally {
                // A failed write must preserve its original error and the preceding
                // complete daily report even if temporary-file cleanup also fails.
                if(temporary!=null)try{if(File.Exists(temporary))File.Delete(temporary);}
                    catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
}
