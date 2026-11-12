using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace ShinhanNativeTrader {
    internal static class Program {
        internal const string Version="1.4.0";
        [STAThread]
        static int Main(string[] args) {
            if (args.Contains("--selftest")) {
                int exit = 0; string report;
                try { report = "PASS: " + SelfTests.Run() + " assertions; fake transport and paper trading only."; }
                catch (Exception ex) { exit = 1; report = "FAIL: " + ex; }
                report += Environment.NewLine + "Runtime=" + Environment.Version + "; pointerBytes=" + IntPtr.Size
                    + Environment.NewLine + "Windows Indi COM/server/live validation: NOT RUN. Live orders: DISABLED.";
                Console.WriteLine(report);
                int index = Array.IndexOf(args, "--selftest-report");
                if (index >= 0) {
                    if (index + 1 >= args.Length) return 2;
                    try {
                        string path = Path.GetFullPath(args[index + 1]);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, report + Environment.NewLine, Encoding.UTF8);
                    } catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
                }
                return exit;
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => {
                DiagnosticFile("GUI ERROR: " + e.Exception.GetType().Name);
                foreach (MainForm form in Application.OpenForms.OfType<MainForm>()) form.Halt();
                MessageBox.Show("화면 오류로 작업을 중단했습니다. 실주문은 잠겨 있습니다.");
            };
            Application.Run(new MainForm()); return 0;
        }
        internal static string DataDir {
            get { string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShinhanNativeTrader"); Directory.CreateDirectory(p); return p; }
        }
        internal static void DiagnosticFile(string message) {
            try { File.AppendAllText(Path.Combine(DataDir, "diagnostics.log"), DateTime.UtcNow.ToString("s") + "Z " + message + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
    }

    public sealed class MainForm : Form {
        readonly Panel nativeArea = new Panel();
        readonly TextBox logs = new TextBox(), account = new TextBox(), password = new TextBox(), code = new TextBox { Text = "005930", Width = 85 };
        readonly NumericUpDown capital = Number(10000, 1000000000, 1000000, 0);
        readonly Label state = new Label { AutoSize = true }, portfolio = new Label { AutoSize = true };
        readonly DataGridView candidatesGrid = Grid("코드", "종목명", "신호 기준가", "점수", "조건"),
            holdingsGrid = Grid("코드", "종목명", "잔고", "매도미체결", "매수미체결", "평균단가", "현재가"),
            paperGrid = Grid("코드", "종목명", "수량", "평균단가", "추적고점"),
            ordersGrid = Grid("주문ID", "방향", "코드", "수량", "체결수량", "지정가", "예약금", "상태");
        readonly Dictionary<string, NumericUpDown> options = new Dictionary<string, NumericUpDown>();
        readonly Timer scheduler = new Timer { Interval = 1000 };
        readonly List<Stock> symbols = new List<Stock>();
        readonly Queue<Stock> scans = new Queue<Stock>();
        readonly Queue<Candidate> buyQuotes = new Queue<Candidate>();
        readonly Dictionary<string, DateTime> quoteDue = new Dictionary<string, DateTime>();
        IndiApi api; PaperTradingEngine engine; Candidate requestedBuy;
        bool scanning, trading, halted, requestedScan,firstQueryQueued; int scanned, holdingRequests,connectionEpoch;

        public MainForm() {
            Text = "Shinhan Native Trader "+Program.Version+" | .NET 4.8 x86 | 실주문 잠금";
            Size = new Size(1240, 830); MinimumSize = new Size(1050, 700); Font = new Font("Malgun Gothic", 9);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
            foreach (var height in new[] {35, 90, 35}) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); Controls.Add(root);
            root.Controls.Add(new Label { Text = "실제 시세·종목·잔고 조회 / 자동매수·매도는 모의매매 전용 / 실주문 영구 잠금", AutoSize = true, ForeColor = Color.DarkRed }, 0, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true }; root.Controls.Add(buttons, 0, 1);
            AddButton(buttons, "Indi 연결·종목 조회", Connect); buttons.Controls.Add(code);
            AddButton(buttons, "일봉 분석", () => { RequireIdle(); requestedScan = false; api.Bars(ValidCode(), BarCount()); });
            AddButton(buttons, "현재가 조회", () => { RequireIdle(); requestedBuy = null; api.Quote(ValidCode()); });
            AddButton(buttons, "전체 스캔·모의매매 시작", StartScan);
            AddButton(buttons, "모의 보유 감시 재개", () => { RequireIdle(); RequireEngine(); trading = true; Log("모의 보유종목 감시 시작"); });
            AddButton(buttons, "자동 처리 중단", Halt);
            buttons.Controls.Add(new Label { Text = "계좌", AutoSize = true }); account.Width = 120; buttons.Controls.Add(account);
            buttons.Controls.Add(new Label { Text = "계좌암호", AutoSize = true }); password.Width = 100; password.UseSystemPasswordChar = true; buttons.Controls.Add(password);
            AddButton(buttons, "실제 잔고 조회", () => { RequireIdle(); requestedBuy = null; try { api.Holdings(account.Text, password.Text); } finally { password.Clear(); } });
            state.Text = "연결 전. 실제 주문 API는 차단되어 있습니다."; root.Controls.Add(state, 0, 2);
            var tabs = new TabControl { Dock = DockStyle.Fill }; root.Controls.Add(tabs, 0, 3);
            AddTab(tabs, "발굴 후보", candidatesGrid); AddTab(tabs, "증권사 잔고 (조회)", holdingsGrid);
            var paperPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            paperPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); paperPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            paperPanel.Controls.Add(portfolio, 0, 0); paperPanel.Controls.Add(paperGrid, 0, 1); AddTab(tabs, "모의 잔고", paperPanel); AddTab(tabs, "모의 주문·체결", ordersGrid);
            var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false }; AddTab(tabs, "전략·투자원금", settings);
            AddSetting(settings, "투자원금 (모의 원화)", capital);
            Setting(settings, "FastPeriod", "단기 이동평균 기간", 1, 199, 5);
            Setting(settings, "SlowPeriod", "장기 이동평균 기간", 2, 200, 20);
            Setting(settings, "BreakoutPeriod", "돌파 고점 기간", 1, 200, 20);
            Setting(settings, "VolumeMultiplier", "평균 거래량 배수", 1, 20, 1.3m, 2);
            Setting(settings, "MinPrice", "최저 종목 가격", 1, 10000000, 2000);
            Setting(settings, "MaxPrice", "최고 종목 가격", 1, 10000000, 400000);
            Setting(settings, "AllocationFraction", "종목당 가용현금 비율 (%)", 1, 100, 20, 2);
            Setting(settings, "MaxPositions", "최대 보유 종목 수", 1, 100, 3);
            Setting(settings, "StopLossPercent", "손절 (%)", 0.1m, 90, 6, 2);
            Setting(settings, "TakeProfitPercent", "익절 (%)", 0.1m, 500, 12, 2);
            Setting(settings, "TrailingActivationPercent", "추적손절 활성 수익률 (%)", 0.1m, 500, 4, 2);
            Setting(settings, "TrailingDrawdownPercent", "고점 대비 추적손절 (%)", 0.1m, 90, 7, 2);
            Setting(settings, "FeeRate", "모의 체결 비용·슬리피지 (%)", 0, 10, 0.3m, 3);
            AddButton(settings, "설정·원금 저장", SaveSettings);
            AddButton(settings, "재시작 모의 미체결 취소·예약금 해제", () => { RequireEngine(); Halt(); engine.RecoverPaperOrders(); RefreshPaper(); Log("모의 미체결 취소 완료. 증권사 주문 복구와 별도입니다."); });
            settings.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(850, 0), Text = "설정은 모의 계정에 저장됩니다. 원금 변경 시 기존 거래·고점·손익을 유지합니다.\n새 계정 파일은 paper-v2.xml입니다. 기존 CSV 기록은 자동 변환하지 않습니다.\n장 운영시간은 한국시간 평일 09:00~15:20입니다. 휴장일 달력은 미연동입니다.\n모의체결은 조회 가격에 비용을 반영하며 실제 유동성·호가·체결을 보장하지 않습니다." });
            logs.Multiline = true; logs.ReadOnly = true; logs.ScrollBars = ScrollBars.Vertical; logs.Dock = DockStyle.Fill; AddTab(tabs, "진단 로그", logs);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill };
            footer.Controls.Add(new Label { AutoSize = true, Text = "스캔 완료 후에도 모의 보유 감시는 계속됩니다. 수신 오류·응답 지연 시 자동 처리를 중단합니다.\nWindows Indi COM·서버 검증과 공식 주문 명세 확인이 필요합니다. 계좌 암호는 저장하지 않습니다." });
            nativeArea.Size = new Size(12, 12); footer.Controls.Add(nativeArea); root.Controls.Add(footer, 0, 4);
            Log("앱 "+Program.Version+"; x"+(IntPtr.Size*8)+"; 실제 Indi 수신은 첫 응답 전까지 미확인입니다.");
            try { engine = new PaperTradingEngine(Path.Combine(Program.DataDir, "paper-v2.xml"), 1000000m); LoadSettings(); RefreshPaper(); Log("모의 상태 복원. 미체결은 복구 전 거래 차단. 실주문 DISABLED."); }
            catch (Exception ex) { halted = true; Log("모의 상태 열기 실패. 파일을 보존하고 거래를 차단합니다: " + ex.GetType().Name); }
            scheduler.Tick += (s, e) => Safe(Schedule); scheduler.Start();
            FormClosing += (s, e) => { Halt(); scheduler.Stop(); scheduler.Dispose(); if (api != null) api.Dispose(); if (engine != null) engine.Dispose(); password.Clear(); };
        }
        static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals) { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, ThousandsSeparator = true, Width = 155, Increment = decimals == 0 ? 1 : 0.1m }; }
        static DataGridView Grid(params string[] columns) { var view = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill }; foreach (var column in columns) view.Columns.Add(column, column); return view; }
        static void AddTab(TabControl tabs, string title, Control content) { var tab = new TabPage(title); tab.Controls.Add(content); tabs.TabPages.Add(tab); }
        void AddButton(Control parent, string text, Action action) { var b = new Button { Text = text, AutoSize = true }; b.Click += (s, e) => Safe(action); parent.Controls.Add(b); }
        static void AddSetting(Control parent, string label, Control input) { var row = new FlowLayoutPanel { Width = 620, Height = 34 }; row.Controls.Add(new Label { Text = label, Width = 300 }); row.Controls.Add(input); parent.Controls.Add(row); }
        void Setting(Control parent, string name, string label, decimal min, decimal max, decimal value, int decimals = 0) { var n = Number(min, max, value, decimals); options.Add(name, n); AddSetting(parent, label, n); }
        void Safe(Action action) {
            try { action(); } catch (Exception ex) {
                Halt(); Log("작업 중단: " + ex.GetBaseException().GetType().Name+" HRESULT=0x"+ex.GetBaseException().HResult.ToString("X8"));
                if(ex is IndiConnectionException)Log(ex.Message);
                // Vendor exceptions can echo request fields; never persist their text.
                state.Text = "작업 오류로 자동 처리 중단. 입력·상태 확인 (" + ex.GetType().Name + ")";
            }
        }
        void Log(string message) { logs.AppendText(MarketTime.NowKorea.ToString("HH:mm:ss") + " " + message + Environment.NewLine); if (logs.TextLength > 65000) logs.Text = logs.Text.Substring(logs.TextLength - 45000); Program.DiagnosticFile(message); }
        void RequireEngine() { if (engine == null || halted) throw new InvalidOperationException("모의 원장 사용 불가"); }
        void RequireConnected() { if (api == null || !api.Connected) throw new InvalidOperationException("Indi 연결 필요"); }
        void RequireIdle() { RequireConnected(); if (api.Busy) throw new InvalidOperationException("조회 대기 중"); }
        string ValidCode() { var value = Rules.Code(code.Text); if (value == "") throw new ArgumentException("6자리 종목코드 필요"); return value; }
        void Connect() {
            if (api == null) { api = new IndiApi(nativeArea); api.Trace += Log; api.Done += reply => Safe(() => Reply(reply)); api.Fatal += message => { Halt(); state.Text = "Indi 응답 실패. 진단 로그의 INDI 오류 정보를 확인하십시오."; }; }
            if(api.Busy||firstQueryQueued)return;
            // Set waiting text first: RequestData may call Done or Fatal synchronously.
            state.Text = "OCX 준비 및 stock_mst 응답 확인 중 (수신 전에는 연결 성공 아님)";
            api.Connect();
            firstQueryQueued=true;int epoch=++connectionEpoch;
            // Let the initialized ActiveX return to its owning UI pump before its first
            // query. The standard designer-created control receives this pump turn too.
            BeginInvoke(new Action(()=> {
                if(IsDisposed||epoch!=connectionEpoch)return;
                firstQueryQueued=false;
                Safe(()=>{if(!api.Stocks()&&api.TransportReady)state.Text="종목 요청을 보내지 못했습니다. 진단 로그를 확인하십시오.";});
            }));
        }
        void StartScan() {
            RequireIdle(); RequireEngine(); if (symbols.Count == 0) throw new InvalidOperationException("종목조회 필요"); if (scanning) return;
            scans.Clear(); buyQuotes.Clear(); candidatesGrid.Rows.Clear(); scanned = 0;
            foreach (var s in symbols.OrderBy(x => x.Code)) scans.Enqueue(s);
            scanning = true; trading = true; Log("전체 스캔 시작. 최신 현재가 조회 후에만 모의매수합니다.");
        }
        public void Halt() { scanning = false; trading = false; requestedScan = false; firstQueryQueued=false;connectionEpoch++;scans.Clear(); buyQuotes.Clear(); requestedBuy = null; state.Text = "자동 처리 중단 / 실주문 잠금"; }
        void Schedule() {
            if (!trading || api == null || !api.Connected || api.Busy) return; RequireEngine();
            var due = engine.Snapshot.Positions.Where(p => !quoteDue.ContainsKey(p.Code) || quoteDue[p.Code] <= DateTime.UtcNow)
                .OrderBy(p => quoteDue.ContainsKey(p.Code) ? quoteDue[p.Code] : DateTime.MinValue).FirstOrDefault();
            bool researchWaiting = buyQuotes.Count > 0 || (scanning && scans.Count > 0);
            if (due != null && (holdingRequests < 3 || !researchWaiting)) {
                holdingRequests = Math.Min(3, holdingRequests + 1);
                quoteDue[due.Code] = DateTime.UtcNow.AddSeconds(15); requestedBuy = null; api.Quote(due.Code); return;
            }
            holdingRequests = 0;
            if (buyQuotes.Count > 0) { requestedBuy = buyQuotes.Dequeue(); api.Quote(requestedBuy.Code); return; }
            if (scanning && scans.Count > 0) { requestedScan = true; api.Bars(scans.Dequeue().Code, BarCount()); return; }
            if (scanning) { scanning = false; state.Text = "스캔 완료 / 모의 보유 감시 계속"; Log("스캔 완료. 보유종목 매도 감시는 계속합니다."); }
        }
        void Reply(IndiReply r) {
            if (r.Kind == "STOCKS") { symbols.Clear(); symbols.AddRange(r.Stocks); state.Text = "종목 응답 " + r.Count + "건 / 일반주식 " + symbols.Count + "개"; }
            else if (r.Kind == "HOLDINGS") { holdingsGrid.Rows.Clear(); foreach (var h in r.Holdings) holdingsGrid.Rows.Add(h.Code, h.Name, h.Quantity, h.SellPending, h.BuyPending, h.AvgPrice, h.Price); state.Text = "실제 잔고 응답 " + r.Holdings.Count + "건 (모의 원장과 별도)"; }
            else if (r.Kind == "BARS") {
                bool fromScan = requestedScan; requestedScan = false;
                var s = symbols.FirstOrDefault(x => x.Code == r.Code);
                var c = Rules.Entry(r.Code, s == null ? r.Code : s.Name, r.Bars, engine == null ? new StrategySettings() : engine.Settings); scanned++;
                if (c != null) { candidatesGrid.Rows.Add(c.Code, c.Name, c.Price, c.Score, c.Reason); if (fromScan && trading && scanning) buyQuotes.Enqueue(c); }
                state.Text = "일봉 분석 " + scanned + " / 대기 " + scans.Count + " / 후보 " + candidatesGrid.Rows.Count;
            } else if (r.Kind == "QUOTE") {
                var intent = requestedBuy; requestedBuy = null; Log("현재가 " + r.Code + " " + r.Price.ToString("N0") + " 시각=" + r.Clock);
                if (!trading || engine == null || !Rules.ValidTime(MarketTime.NowKorea) || !MarketTime.IsFreshQuote(r.Clock, DateTime.UtcNow) || r.Price <= 0) return;
                var p = engine.Snapshot.Positions.FirstOrDefault(x => x.Code == r.Code);
                if (p != null) {
                    var reason = engine.UpdateQuote(r.Code, r.Price);
                    if (!string.IsNullOrEmpty(reason)) { var o = engine.SubmitSell(r.Code, p.Qty, r.Price, "exit-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow); engine.ApplyFill(o.Id, o.Id + "-paper", p.Qty, r.Price, DateTime.UtcNow); Log("모의매도 " + r.Code + " " + reason); }
                } else if (intent != null && intent.Code == r.Code && engine.Snapshot.Positions.Count < engine.Settings.MaxPositions) {
                    if (intent.SignalDate != MarketTime.NowKorea.ToString("yyyyMMdd", CultureInfo.InvariantCulture)) { Log("모의매수 제외: 당일 일봉 신호가 아닙니다"); return; }
                    if (r.Price < engine.Settings.MinPrice || r.Price > engine.Settings.MaxPrice) { Log("모의매수 제외: 설정 가격 범위 밖의 현재가"); return; }
                    if (r.Price > intent.Price * 1.03m || r.Price < intent.Price * 0.97m) { Log("모의매수 제외: 신호 기준가 대비 3% 초과 변동"); return; }
                    string key = "entry-" + MarketTime.NowKorea.ToString("yyyyMMdd") + "-" + r.Code;
                    if (engine.Snapshot.Orders.Any(o => o.ClientKey == key)) return;
                    int qty = Rules.PaperQty(engine.AvailableCash, r.Price, engine.Settings.AllocationFraction, engine.Settings.FeeRate);
                    if (qty > 0) { var o = engine.SubmitBuy(r.Code, intent.Name, qty, r.Price, key, DateTime.UtcNow); engine.ApplyFill(o.Id, o.Id + "-paper", qty, r.Price, DateTime.UtcNow); Log("모의매수 " + r.Code + " " + qty + "주 (실제 체결 아님)"); }
                }
                RefreshPaper();
            }
        }
        void RefreshPaper() {
            if (engine == null) return; var snapshot = engine.Snapshot; paperGrid.Rows.Clear(); ordersGrid.Rows.Clear();
            foreach (var p in snapshot.Positions) paperGrid.Rows.Add(p.Code, p.Name, p.Qty, p.Entry, p.High);
            foreach (var o in snapshot.Orders) ordersGrid.Rows.Add(o.Id, o.Side, o.Code, o.Quantity, o.FilledQuantity, o.LimitPrice, o.ReservedCash, o.Status);
            portfolio.Text = "모의 가용현금 " + engine.AvailableCash.ToString("N0") + "원 / 보유 " + snapshot.Positions.Count + "개 / 실제 주문 0건";
        }
        int BarCount() { var s = engine == null ? new StrategySettings() : engine.Settings; return Math.Max(45, Math.Max(s.SlowPeriod, s.BreakoutPeriod) + 2); }
        static bool IsRatio(string key) { return key == "AllocationFraction" || key == "FeeRate"; }
        void LoadSettings() {
            var settings = engine.Settings;
            foreach (var pair in options) { var field = typeof(StrategySettings).GetField(pair.Key); decimal v = Convert.ToDecimal(field.GetValue(settings), CultureInfo.InvariantCulture); if (IsRatio(pair.Key)) v *= 100; pair.Value.Value = Math.Min(pair.Value.Maximum, Math.Max(pair.Value.Minimum, v)); }
            capital.Value = Math.Min(capital.Maximum, Math.Max(capital.Minimum, engine.Snapshot.Capital));
        }
        void SaveSettings() {
            RequireEngine(); Halt(); var settings = engine.Settings;
            foreach (var pair in options) { var field = typeof(StrategySettings).GetField(pair.Key); decimal v = pair.Value.Value; if (IsRatio(pair.Key)) v /= 100; field.SetValue(settings, Convert.ChangeType(v, field.FieldType, CultureInfo.InvariantCulture)); }
            settings.Validate(); engine.SetCapital(capital.Value); engine.UpdateSettings(settings); RefreshPaper(); Log("설정·원금 저장 완료. 시작 버튼으로 자동 처리를 재개하십시오.");
        }
    }
}
