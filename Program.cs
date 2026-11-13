using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace ShinhanNativeTrader {
    sealed class AccountUserActionException : InvalidOperationException {
        public AccountUserActionException(string message):base(message){}
    }
    internal static class Program {
        internal const string Version="2.2.6";
        [STAThread]
        static int Main(string[] args) {
            if(args.Contains("--selftest")) {
                int exit=0;string report;
                try{report="PASS: "+SelfTests.Run()+" assertions; isolated test responses only.";}
                catch(Exception ex){exit=1;report="FAIL: "+ex;}
                report+=Environment.NewLine+"Runtime="+Environment.Version+"; pointerBytes="+IntPtr.Size
                    +Environment.NewLine+"Actual Indi server and money orders: NOT RUN. Test authorization uses isolated fake accounts.";
                Console.WriteLine(report);int index=Array.IndexOf(args,"--selftest-report");
                if(index>=0) {
                    if(index+1>=args.Length)return 2;
                    try{string path=Path.GetFullPath(args[index+1]);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,report+Environment.NewLine,Encoding.UTF8);}
                    catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
                }
                return exit;
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException+=(s,e)=>{
                DiagnosticFile("GUI ERROR: "+e.Exception.GetType().Name);
                foreach(MainForm form in Application.OpenForms.OfType<MainForm>())form.Halt();
            };
            Application.Run(new MainForm());return 0;
        }
        internal static string DataDir {
            get{string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShinhanNativeTrader");Directory.CreateDirectory(path);return path;}
        }
        internal static void DiagnosticFile(string message) {
            try{File.AppendAllText(Path.Combine(DataDir,"diagnostics.log"),DateTime.UtcNow.ToString("s",CultureInfo.InvariantCulture)+"Z "+message+Environment.NewLine,Encoding.UTF8);}
            catch{ /* Account reports and the order journal have independent, checked writes. */ }
        }
    }

    // Credentials are needed for repeated broker queries while the operator is
    // away. They stay in this session only and are not serialized with settings.
    sealed class AccountCredentialSession : IDisposable {
        readonly SecureString secret=new SecureString();
        bool disposed;
        public string Account {get;private set;}
        public string Key {get;private set;}
        public AccountCredentialSession(string account,string password) {
            Account=(account??"").Trim();
            if(Account.Length!=11||Account.Any(c=>c<'0'||c>'9')||string.IsNullOrWhiteSpace(password)||password.Length>9||password.Any(char.IsControl))
                throw new AccountUserActionException("계좌번호11자리와 계좌암호를 입력하십시오.");
            foreach(char character in password)secret.AppendChar(character);secret.MakeReadOnly();
            using(var hash=SHA256.Create())Key=string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes("ShinhanNativeTrader/real-account/v1/"+Account)).Select(x=>x.ToString("x2",CultureInfo.InvariantCulture)));
        }
        public T Use<T>(Func<string,string,T> action) {
            if(disposed)throw new ObjectDisposedException("AccountCredentialSession");
            IntPtr pointer=Marshal.SecureStringToBSTR(secret);
            try{return action(Account,Marshal.PtrToStringBSTR(pointer));}
            finally{Marshal.ZeroFreeBSTR(pointer);}
        }
        public void Dispose(){if(disposed)return;disposed=true;secret.Dispose();Account=null;Key=null;}
    }

    sealed class UnattendedAwakeLease : IDisposable {
        bool active;
        [DllImport("kernel32.dll",SetLastError=true)]static extern uint SetThreadExecutionState(uint flags);
        public void Start() {
            if(active)return;
            if(Environment.OSVersion.Platform==PlatformID.Win32NT&&SetThreadExecutionState(0x80000001)==0)
                throw new InvalidOperationException("Windows 부재중 절전 방지 요청이 실패했습니다.");
            active=true;
        }
        public void Dispose(){if(!active)return;active=false;if(Environment.OSVersion.Platform==PlatformID.Win32NT)SetThreadExecutionState(0x80000000);}
    }

    public sealed class MainForm : Form {
        sealed class EntryRequest {
            public Candidate Candidate;
            public LiveOrderIntent Intent;
            public RealtimeQuote Quote;
            public AccountOrderCapacity Capacity;
            public bool AutoSize;
            public readonly Queue<string> Reads=new Queue<string>();
            public string WaitingFor;
            public int Attempts,NotSentAttempts;
            public DateTime NotBeforeUtc;
        }
        sealed class HoldingSellDecision {public decimal AveragePrice,Quantity,CreditQuantity;public string Reason;}
        sealed class ResearchState {
            public DateTime Day;
            public int Progress,Total;
            public bool Complete,Requested,Scanning;
            public List<PremarketReference> References;
            public List<Stock> Remaining;
        }
        readonly Panel nativeArea=new Panel();
        readonly TextBox logs=new TextBox(),account=new TextBox(),password=new TextBox(),code=new TextBox{Text="005930",Width=85};
        readonly TextBox orderResponseDetails=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Text="주문 행을 선택하면 응답 사유를 표시합니다."};
        readonly NumericUpDown budget=Number(0,1000000000,0,0),orderQuantity=Number(1,1000000,1,0),orderPrice=Number(1,10000000,1,0);
        readonly Label state=new Label{AutoSize=true},portfolio=new Label{AutoSize=true},cashLabel=new Label{AutoSize=true},orderGate=new Label{AutoSize=true,ForeColor=Color.DarkRed},
            reportStatus=new Label{AutoSize=true,Text="보고서: 계좌 조회 응답 후 자동 저장 (CSV)"},
            researchStatus=new Label{AutoSize=true,Text="장전 분석: 미준비 / 실주문 시작 또는 전체 스캔으로 준비"},
            quoteStatus=new Label{AutoSize=true,Text="실시간 SC: 아직 수신 확인 전 / 수동 현재가 조회와 별도"},
            basisStatus=new Label{AutoSize=true,Text="주문 잔고기준: 미조회"};
        readonly DataGridView holdingsGrid=Grid("코드","종목명","수량","평균단가","현재가","매입금액","평가금액","평가손익","수익률(%)","시세 조회시각","매도판정"),
            ordersGrid=Grid("주문번호","종목","구분","주문수량","누적체결","미체결","주문단가","체결평균가","확인상태","응답 사유"),
            candidatesGrid=Grid("코드","종목명","신호 기준가","점수","조건","주문상태");
        readonly Dictionary<string,NumericUpDown> options=new Dictionary<string,NumericUpDown>();
        readonly Timer scheduler=new Timer{Interval=1000};
        readonly UnattendedSession session=new UnattendedSession(rescanDuringSession:false);
        readonly LiveTradingSession authorization=new LiveTradingSession();
        readonly UnattendedAwakeLease awake=new UnattendedAwakeLease();
        readonly List<Stock> symbols=new List<Stock>();
        readonly List<Holding> holdings=new List<Holding>();
        readonly Queue<Stock> scans=new Queue<Stock>();
        readonly Queue<Candidate> buyQuotes=new Queue<Candidate>();
        readonly Queue<string> accountReads=new Queue<string>();
        readonly Dictionary<string,DateTime> quoteDue=new Dictionary<string,DateTime>(),priceReceivedUtc=new Dictionary<string,DateTime>();
        readonly Dictionary<string,DateTime> candidateQuoteSince=new Dictionary<string,DateTime>(),candidateQuoteDue=new Dictionary<string,DateTime>();
        readonly Dictionary<string,HoldingSellDecision> sellDecisions=new Dictionary<string,HoldingSellDecision>(StringComparer.Ordinal);
        readonly Dictionary<string,CashInstrument> instruments=new Dictionary<string,CashInstrument>(StringComparer.Ordinal);
        readonly Dictionary<string,string> balanceSignatures=new Dictionary<string,string>(StringComparer.Ordinal);
        readonly HashSet<string> trackedQuotes=new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> blockedSignals=new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string,PremarketReference> preparedReferences=new Dictionary<string,PremarketReference>(StringComparer.Ordinal);
        readonly List<PremarketReference> watchedReferences=new List<PremarketReference>();
        DateTime? researchDateKorea;
        bool researchComplete,openingAdopted,researchRequested;
        int researchTotal;
        IndiApi api;
        AccountAppSettingsStore settingsStore;
        AccountAppSettings configuration=new AccountAppSettings();
        AccountCredentialSession credentials;
        AccountCashSnapshot cash;
        BrokerOrdersSnapshot brokerOrders;
        BrokerHoldingsSnapshot tradeHoldings,ordinaryHoldings;
        LiveOrderJournal journal;
        LiveOrderGateway gateway;
        RealtimeIndi realtime;
        BrokerReadiness readiness;
        BrokerAccountEventRouter accountEvents;
        EntryRequest entry;
        DateTime? holdingsReceivedUtc;
        bool orderAcknowledgementPending;
        DateTime nextAccountReadUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc),nextReportUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        bool scanning,requestedScan,monitoring,firstQueryQueued,pendingSessionScan,storageFault,closingForm,realtimeFailed,displayDirty,submitting,preflightDirty,liveDayCompleted;
        int scanned,holdingRequests,connectionEpoch;
        string boundKey,lastReportKey;
        string gateDetail="실주문 미시작";
        string lastBasisDiagnostic;
        string lastCashProfileDiagnostic;
        DateTime? lastBasisDiagnosticUtc;
        DateTime? lastRealtimeDiagnosticUtc;
        string lastSavedReportPath;
        DateTime? lastReportSavedUtc;
        bool reportWriteFault,reportSaving,initialReportComplete;

        public MainForm() {
            Text="Shinhan Native Trader "+Program.Version+" | 실계좌 자동매매";
            Size=new Size(1370,860);MinimumSize=new Size(1120,730);Font=new Font("Malgun Gothic",9);
            holdingsGrid.Columns["매도판정"].FillWeight=170;
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(10)};
            foreach(int height in new[]{35,100,138,40})root.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,52));Controls.Add(root);
            UpdateOrderStatus();root.Controls.Add(orderGate,0,0);
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};root.Controls.Add(buttons,0,1);
            AddButton(buttons,"Indi 연결·종목 조회",Connect);buttons.Controls.Add(code);
            AddButton(buttons,"현재가 조회",()=>{RequireIdle();api.SnapshotQuote(ValidCode());});
            AddButton(buttons,"일봉 분석",()=>{RequireIdle();requestedScan=false;api.Bars(ValidCode(),BarCount());});
            AddButton(buttons,"전체 스캔",StartScan);
            AddButton(buttons,"실주문 운영 시작",Arm);
            AddButton(buttons,"운영 중단",Halt);
            buttons.Controls.Add(new Label{Text="계좌",AutoSize=true});account.Width=125;buttons.Controls.Add(account);
            buttons.Controls.Add(new Label{Text="계좌암호",AutoSize=true});password.Width=95;password.UseSystemPasswordChar=true;buttons.Controls.Add(password);
            AddButton(buttons,"실계좌 조회·감시",AttachAccount);
            AddButton(buttons,"보고서 폴더",OpenReports);
            AddButton(buttons,"조회 정의 내보내기",ExportIndiDefinitions);
            state.Text="Indi 연결 후 계좌를 조회하고 부재중 운영을 시작하십시오.";
            var statusArea=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false};
            statusArea.SizeChanged+=(s,e)=>{quoteStatus.MaximumSize=new Size(Math.Max(400,statusArea.ClientSize.Width-12),0);};
            statusArea.Controls.Add(state);statusArea.Controls.Add(reportStatus);statusArea.Controls.Add(researchStatus);statusArea.Controls.Add(quoteStatus);statusArea.Controls.Add(basisStatus);root.Controls.Add(statusArea,0,2);
            var summary=new FlowLayoutPanel{Dock=DockStyle.Fill};summary.Controls.Add(portfolio);summary.Controls.Add(cashLabel);root.Controls.Add(summary,0,3);
            var tabs=new TabControl{Dock=DockStyle.Fill};root.Controls.Add(tabs,0,4);
            AddTab(tabs,"실계좌 잔고·손익",holdingsGrid);
            var ordersPanel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=3,ColumnCount=1};ordersPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,38));ordersPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));ordersPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,90));
            var orderButtons=new FlowLayoutPanel{Dock=DockStyle.Fill};orderButtons.Controls.Add(new Label{Text="선택 주문 수량",AutoSize=true});orderButtons.Controls.Add(orderQuantity);orderButtons.Controls.Add(new Label{Text="정정 단가",AutoSize=true});orderButtons.Controls.Add(orderPrice);
            AddButton(orderButtons,"선택 주문 취소",()=>ChangeSelectedOrder(false));AddButton(orderButtons,"선택 주문 정정",()=>ChangeSelectedOrder(true));
            AddButton(orderButtons,"선택 거절 매도 재시도",()=>RetrySelectedRejectedSellAt(DateTime.UtcNow));
            ordersPanel.Controls.Add(orderButtons,0,0);ordersPanel.Controls.Add(ordersGrid,0,1);ordersPanel.Controls.Add(orderResponseDetails,0,2);
            ordersGrid.SelectionChanged+=(s,e)=>UpdateSelectedOrderResponse();
            AddTab(tabs,"주문·체결",ordersPanel);AddTab(tabs,"발굴 후보",candidatesGrid);
            var settings=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};AddTab(tabs,"전략·운용한도",settings);
            AddSetting(settings,"실계좌 운용한도 (원)",budget);
            Setting(settings,"FastPeriod","단기 이동평균 기간",1,199,5);Setting(settings,"SlowPeriod","장기 이동평균 기간",2,200,20);
            Setting(settings,"BreakoutPeriod","돌파 고점 기간",1,200,20);Setting(settings,"VolumeMultiplier","평균 거래량 배수",1,20,1.3m,2);
            Setting(settings,"MinPrice","최저 종목 가격",1,10000000,2000);Setting(settings,"MaxPrice","최고 종목 가격",1,10000000,400000);
            Setting(settings,"AllocationFraction","종목당 가용금액 비율 (%)",1,100,20,2);Setting(settings,"MaxPositions","최대 보유 종목 수",1,100,3);
            Setting(settings,"StopLossPercent","손절 (%)",0.1m,90,6,2);Setting(settings,"TakeProfitPercent","익절 (%)",0.1m,500,12,2);
            Setting(settings,"TrailingActivationPercent","추적손절 활성 수익률 (%)",0.1m,500,4,2);Setting(settings,"TrailingDrawdownPercent","고점 대비 추적손절 (%)",0.1m,90,7,2);
            Setting(settings,"FeeRate","주문금액 비용 여유율 (%)",0,10,0.3m,3);AddButton(settings,"전략·운용한도 저장",SaveSettings);
            settings.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(1000,0),Text="운용한도는 실제 예수금이 아닙니다. 실제 잔고·현금100% 가능금액·매도가능수량을 주문마다 새로 조회합니다.\n실주문 운영 시작을 누른 계좌에서만 주문합니다. 앱을 다시 열거나 운영을 중단하면 실주문 시작 상태가 해제됩니다.\n대상 거래일 00시부터 완료 일봉으로 사전분석, 개장 후 준비 후보의 실제 SC 시세·거래량 확인. 동일 일봉을20분마다 다시 전수 조회하지 않습니다.\n평일 09:00~15:20 주문 운영. 장전 분석이 덜 끝났으면 완료 자료를 유지하고 잔여 분석을 이어갑니다.\n기존 보유와 새 보유는 같은 손절·익절·추적손절 조건으로 감시합니다. 개장 시 강제 매도하지 않습니다.\n평가손익은 비용 제외 계산이며 실현손익은 증권사 응답이 있을 때만 표시합니다. 휴장일 달력은 미연동입니다.\n계좌번호·암호는 설정에 저장하지 않습니다. 자동 조회용 암호는 운영 중단·종료 전까지 메모리에만 보관합니다."});
            logs.Multiline=true;logs.ReadOnly=true;logs.ScrollBars=ScrollBars.Vertical;logs.Dock=DockStyle.Fill;AddTab(tabs,"진단 로그",logs);
            var footer=new FlowLayoutPanel{Dock=DockStyle.Fill};footer.Controls.Add(new Label{AutoSize=true,Text="실제 잔고·주문 응답을 표시합니다. 주문 접수를 체결로 처리하지 않습니다. 연결·수신·저장 오류 시 자동 운영을 중단합니다.\n부재중 운영 중에는 PC 자동 절전을 억제합니다. PC 종료·수동 절전·Indi 로그인 만료 후에는 자동 재개하지 않습니다."});
            nativeArea.Size=new Size(12,12);footer.Controls.Add(nativeArea);root.Controls.Add(footer,0,5);
            try{settingsStore=new AccountAppSettingsStore(Path.Combine(Program.DataDir,"live-settings-v1.xml"));configuration=settingsStore.Snapshot;LoadSettings();}
            catch(Exception ex){storageFault=true;Log("전략 설정 열기 실패: "+ex.GetType().Name+". 기존 파일을 보존하고 자동 운영을 차단합니다.");}
            Log("앱 "+Program.Version+"; x"+(IntPtr.Size*8)+"; 실제 계좌 전용. 첫 Indi 응답 전까지 연결 미확인.");RefreshAccount();
            scheduler.Tick+=(s,e)=>Safe(Schedule);scheduler.Start();
            FormClosing+=(s,e)=>{closingForm=true;Halt();scheduler.Stop();scheduler.Dispose();if(api!=null)api.Dispose();if(settingsStore!=null)settingsStore.Dispose();};
        }

        static NumericUpDown Number(decimal min,decimal max,decimal value,int decimals){return new NumericUpDown{Minimum=min,Maximum=max,Value=value,DecimalPlaces=decimals,ThousandsSeparator=true,Width=145,Increment=decimals==0?1:0.1m};}
        static DataGridView Grid(params string[] columns){var grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};foreach(var column in columns)grid.Columns.Add(column,column);return grid;}
        static void AddTab(TabControl tabs,string name,Control content){var tab=new TabPage(name);tab.Controls.Add(content);tabs.TabPages.Add(tab);}
        void AddButton(Control parent,string text,Action action){var button=new Button{Text=text,AutoSize=true};button.Click+=(s,e)=>Safe(action);parent.Controls.Add(button);}
        static void AddSetting(Control parent,string label,Control input){var row=new FlowLayoutPanel{Width=730,Height=34};row.Controls.Add(new Label{Text=label,Width=310});row.Controls.Add(input);parent.Controls.Add(row);}
        void Setting(Control parent,string name,string label,decimal min,decimal max,decimal value,int decimals=0){var input=Number(min,max,value,decimals);options.Add(name,input);AddSetting(parent,label,input);}
        void Safe(Action action){try{action();}catch(AccountUserActionException ex){state.Text=ex.Message;Log(ex.Message);}catch(Exception ex){Halt();Log("작업 중단: "+ex.GetBaseException().GetType().Name+" HRESULT=0x"+ex.GetBaseException().HResult.ToString("X8",CultureInfo.InvariantCulture));if(ex is IndiConnectionException)Log(ex.Message);state.Text="오류로 운영 중단. 진단 로그와 최근 조회시각을 확인하십시오.";}}
        void Log(string message){logs.AppendText(MarketTime.NowKorea.ToString("HH:mm:ss",CultureInfo.InvariantCulture)+" "+message+Environment.NewLine);if(logs.TextLength>65000)logs.Text=logs.Text.Substring(logs.TextLength-45000);Program.DiagnosticFile(message);}
        void RequireConnected(){if(api==null||!api.Connected)throw new AccountUserActionException("먼저 Indi 연결·종목 조회를 실행하십시오.");}
        void RequireIdle(){RequireConnected();if(api.Busy)throw new AccountUserActionException("조회 응답을 기다리고 있습니다. 완료 후 다시 실행하십시오.");}
        void RequireStorage(){if(storageFault||settingsStore==null||settingsStore.Faulted)throw new AccountUserActionException("설정·보고서 저장 상태를 확인한 뒤 앱을 다시 실행하십시오.");}
        string ValidCode(){string value=Rules.Code(code.Text);if(value=="")throw new AccountUserActionException("6자리 종목코드를 입력하십시오.");return value;}
        bool ActiveSession {get{return session.Armed&&!session.Closing&&session.SessionDateKorea.HasValue;}}
        bool LiveStarted {get{return credentials!=null&&authorization.Active&&authorization.MatchesAccount(credentials.Account);}}
        void UpdateOrderStatus(string detail=null) {
            if(detail!=null)gateDetail=detail;
            orderGate.Text=authorization.Active?"실주문 운영 시작됨 / "+gateDetail:"실주문 미시작 / 시작 버튼을 누른 계좌에서만 주문합니다.";
            orderGate.ForeColor=authorization.Active?Color.DarkGreen:Color.DarkRed;
        }
        bool EnsureRealtime(bool required) {
            if(credentials==null||readiness==null)return false;
            if(realtime!=null&&realtime.Connected&&realtime.AccountSubscribed)return true;
            DisposeRealtime();realtimeFailed=false;
            try {
                realtime=new RealtimeIndi(nativeArea,Log);realtime.QuoteReceived+=OnRealtimeQuote;
                realtime.OrderReceived+=OnRealtimeOrder;realtime.BalanceReceived+=OnRealtimeBalance;
                realtime.Faulted+=OnRealtimeFailure;realtime.Invalidated+=OnRealtimeFailure;
                realtime.AttachAccount(credentials.Account);
                readiness.SetAccountStream(realtime.AccountSubscribed,realtime.AccountStreamObserved,DateTime.UtcNow);
                foreach(var holding in holdings.Where(x=>x.Quantity>0))EnsureLiveQuote(holding.Code);
                return realtime.Connected&&realtime.AccountSubscribed;
            }catch(Exception ex) {
                DisposeRealtime();readiness.SetAccountStream(false,DateTime.UtcNow);authorization.Revoke();readiness.SetAuthorization(false);
                string message="실시간 계좌 연결 실패 ("+ex.GetType().Name+"). 조회 감시는 계속할 수 있습니다.";
                Log(message);UpdateOrderStatus(message);if(required)state.Text=message;return false;
            }
        }
        void DisposeRealtime() {
            if(realtime!=null) {
                var previous=realtime;realtime=null;
                previous.QuoteReceived-=OnRealtimeQuote;previous.OrderReceived-=OnRealtimeOrder;previous.BalanceReceived-=OnRealtimeBalance;
                previous.Faulted-=OnRealtimeFailure;previous.Invalidated-=OnRealtimeFailure;previous.Dispose();
            }
            trackedQuotes.Clear();balanceSignatures.Clear();
            lastRealtimeDiagnosticUtc=null;
        }
        bool EnsureLiveQuote(string symbol) {
            if(realtime==null||!realtime.Connected||realtime.Failed)return false;
            if(trackedQuotes.Contains(symbol))return true;
            ReleaseUnusedQuotes();
            if(!realtime.EnsureQuote(symbol)){UpdateOrderStatus("실시간 종목 등록 대기");return false;}
            trackedQuotes.Add(symbol);return true;
        }
        void ReleaseUnusedQuotes() {
            if(realtime==null||!realtime.Connected)return;
            var keep=new HashSet<string>(holdings.Where(x=>x.Quantity>0).Select(x=>x.Code),StringComparer.Ordinal);
            if(entry!=null&&entry.Intent!=null)keep.Add(entry.Intent.Code);
            foreach(var candidate in buyQuotes)keep.Add(candidate.Code);
            foreach(var prepared in watchedReferences)keep.Add(prepared.Code);
            if(journal!=null)foreach(var order in journal.Snapshot.Orders.Where(LiveOrderJournal.IsUnresolved))keep.Add(order.Code);
            foreach(var symbol in trackedQuotes.Where(x=>!keep.Contains(x)).ToList()){realtime.RemoveQuote(symbol);trackedQuotes.Remove(symbol);}
        }
        void OnRealtimeQuote(RealtimeQuote quote) {
            var held=holdings.SingleOrDefault(x=>x.Code==quote.Code);if(held==null)return;
            held.Price=quote.Price;priceReceivedUtc[held.Code]=quote.ReceivedUtc;displayDirty=true;
        }
        void OnRealtimeOrder(CashOrderEvent value) {
            if(readiness==null||accountEvents==null)return;
            readiness.SetAccountStream(realtime!=null&&realtime.AccountSubscribed,true,value.ReceivedUtc);
            accountEvents.ObserveAA(value);
        }
        void OnRealtimeBalance(CashBalanceEvent value) {
            if(readiness==null)return;
            readiness.SetAccountStream(realtime!=null&&realtime.AccountSubscribed,true,value.ReceivedUtc);
            string signature=value.Quantity.ToString(CultureInfo.InvariantCulture)+":"+value.OrderableQuantity.ToString(CultureInfo.InvariantCulture)+":"
                +value.SellPendingQuantity.ToString(CultureInfo.InvariantCulture)+":"+value.AveragePrice.ToString(CultureInfo.InvariantCulture)+":"+value.StockKindCode;
            string previous;
            bool changed=!balanceSignatures.TryGetValue(value.Code,out previous)||previous!=signature;
            balanceSignatures[value.Code]=signature;
            if(changed){readiness.ObserveBalanceSellable(value.Code,value.OrderableQuantity,value.ReceivedUtc);OnAccountInvalidated(value.ReceivedUtc,"실계좌 잔고 변동 후 재조회");}
        }
        void OnAccountInvalidated(DateTime utc,string reason) {
            if(readiness==null)return;readiness.ObserveAccountEvent(utc);
            if(entry!=null&&!submitting)preflightDirty=true;
            nextAccountReadUtc=DateTime.UtcNow;QueueAccountRefresh();UpdateOrderStatus("계좌 변동 후 최신 조회 중");
        }
        void OnGatewayOrderChanged(LiveOrderRecord record) {
            if(record!=null&&record.ResponseEvidence!=null){Log("주문 응답 "+record.Code+" / "+record.ResponseEvidence.SafeDiagnostic);UpdateOrderStatus("주문 응답 "+record.Code+" / "+LocalOrderStatus(record.Status));}
            if(readiness!=null)readiness.Invalidate(DateTime.UtcNow);
            if(accountEvents!=null)accountEvents.DrainOnGatewayOrderChanged();
            OnBrokerOrderChanged(record);
        }
        void OnBrokerOrderChanged(LiveOrderRecord record) {
            if(readiness!=null)readiness.Invalidate(DateTime.UtcNow);
            if(entry!=null&&!submitting)preflightDirty=true;
            QueueAccountRefresh();RefreshOrders();displayDirty=true;
            if(record!=null&&record.Status==LiveOrderStatus.Unknown){Halt();Log("실주문 결과 불명: 자동 재전송 없이 운영을 중단합니다.");}
        }
        void OnRealtimeFailure(string ignoredProviderText) {
            if(realtimeFailed)return;realtimeFailed=true;bool mustStop=authorization.Active||(journal!=null&&journal.Snapshot.Orders.Any(LiveOrderJournal.IsUnresolved));
            authorization.Revoke();if(readiness!=null){readiness.SetAuthorization(false);readiness.SetAccountStream(false,DateTime.UtcNow);}
            if(gateway!=null)gateway.Invalidate(DateTime.UtcNow);
            if(mustStop){Halt();Log("실시간 연결 오류로 실주문 운영을 중단했습니다. 다시 연결하고 시작해야 합니다.");}
            else{UpdateOrderStatus("실시간 연결 미확인 / 조회 감시 유지");Log("실시간 연결 미확인. 조회용 가격만 표시합니다.");}
        }

        void Connect() {
            if(api!=null&&api.Busy)return;
            Halt();ClearAccountData();
            if(api==null){api=new IndiApi(nativeArea);api.Trace+=Log;api.Done+=reply=>Safe(()=>Reply(reply));api.Fatal+=reason=>{Halt();state.Text="Indi 수신 오류로 운영 중단. 진단 로그를 확인하십시오.";};}
            state.Text="Indi 초기화 및 종목 응답 확인 중";api.Connect();firstQueryQueued=true;int epoch=++connectionEpoch;
            BeginInvoke(new Action(()=>{if(IsDisposed||epoch!=connectionEpoch)return;firstQueryQueued=false;Safe(()=>{if(!api.Stocks()&&api.TransportReady)state.Text="종목 요청을 보내지 못했습니다.";});}));
        }
        void AttachAccount() {
            RequireIdle();RequireStorage();
            var captured=new AccountCredentialSession(account.Text,password.Text);password.Clear();
            var publicResearch=CaptureResearch();
            Halt();credentials=captured;boundKey=captured.Key;lastReportKey=boundKey;account.ReadOnly=true;ClearAccountData();
            try {
                journal=new LiveOrderJournal(Path.Combine(Program.DataDir,"live-orders-"+boundKey+".xml"));journal.BindAccount(credentials.Account);
                readiness=new BrokerReadiness(boundKey,journal.Snapshot.AccountFingerprint);readiness.SetAuthorization(false);
                accountEvents=new BrokerAccountEventRouter(journal,OnAccountInvalidated);
                accountEvents.Changed+=OnBrokerOrderChanged;
                accountEvents.Unmatched+=message=>{UpdateOrderStatus("계좌 변동 후 조회 갱신 중");QueueAccountRefresh();};
                accountEvents.Unknown+=message=>{Halt();Log("주문 상태 불명: 증권사 대사 전까지 실주문을 중단합니다.");};
                CreateGateway();EnsureRealtime(false);
            }
            catch{ReleaseAccount();throw;}
            monitoring=true;nextAccountReadUtc=DateTime.UtcNow;nextReportUtc=DateTime.UtcNow;QueueAccountRefresh();
            RestoreResearch(publicResearch,DateTime.UtcNow);
            state.Text="실계좌 매매기준 잔고·주문가능금액·주문 조회 중";Log("실계좌 조회 세션 시작. 조회만으로 실주문을 시작하지 않습니다.");
        }
        void CreateGateway(){if(gateway!=null||journal==null)return;gateway=new LiveOrderGateway(journal,api.CreateLiveOrderWire());gateway.OrderChanged+=OnGatewayOrderChanged;}
        void QueueAccountRefresh(){if(credentials==null||accountReads.Count!=0)return;accountReads.Enqueue("TRADEHOLDINGS");accountReads.Enqueue("ORDINARYHOLDINGS");accountReads.Enqueue("CASH");accountReads.Enqueue("ORDERS");nextAccountReadUtc=DateTime.UtcNow.AddSeconds(60);}
        void ReadAccountKind(string kind,DateTime now) {
            credentials.Use((number,secret)=>kind=="TRADEHOLDINGS"?api.TradeHoldings(number,secret):kind=="ORDINARYHOLDINGS"?api.OrdinaryHoldings(number,secret):kind=="CASH"?api.Cash(number,secret):api.Orders(number,secret,MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)));
        }
        void Arm() {
            RequireConnected();RequireStorage();
            if(configuration.BudgetLimit<=0)throw new AccountUserActionException("전략·운용한도에서 0원보다 큰 운용한도를 저장하십시오.");
            if(credentials==null){if(api.Busy){state.Text="조회 응답 완료 후 실계좌를 연결하고 부재중 운영을 시작하십시오.";return;}AttachAccount();}
            if(symbols.Count==0)throw new AccountUserActionException("종목 조회를 먼저 실행하십시오.");
            if(!EnsureRealtime(true))return;
            var now=DateTime.UtcNow;authorization.Authorize(credentials.Account,now);readiness.SetAuthorization(true);
            session.Arm(now);awake.Start();monitoring=true;liveDayCompleted=false;blockedSignals.Clear();QueueAccountRefresh();
            BeginResearch(now,false);
            UpdateOrderStatus("매 주문 전 실제 계좌·실시간 시세 확인");
            state.Text="실주문 운영 시작: "+(session.NextScanUtc.HasValue?MarketTime.ToKorea(session.NextScanUtc.Value).ToString("MM-dd HH:mm",CultureInfo.InvariantCulture):"장중")+" / 기존·신규 보유 같은 매도 규칙 적용";
            Log("사용자 버튼으로 현재 계좌 실주문 운영 시작. 데이터 조건이 충족된 주문만 전송합니다.");
        }
        void StartScan(){RequireIdle();RequireStorage();StartScanCore();}
        void StartScanCore(){BeginResearch(DateTime.UtcNow,true);}
        DateTime AnalysisTarget(DateTime now) {
            if(authorization.Active&&authorization.TradingDateKorea.HasValue)return authorization.TradingDateKorea.Value;
            var korea=MarketTime.ToKorea(now);var day=korea.Date;
            if(korea.TimeOfDay>=new TimeSpan(15,20,0))day=day.AddDays(1);
            while(day.DayOfWeek==DayOfWeek.Saturday||day.DayOfWeek==DayOfWeek.Sunday)day=day.AddDays(1);
            return day;
        }
        void InvalidateResearch() {
            preparedReferences.Clear();watchedReferences.Clear();researchDateKorea=null;researchComplete=false;
            openingAdopted=false;researchRequested=false;researchTotal=0;
            researchStatus.Text="장전 분석: 미준비 / 새 분석이 필요합니다.";
        }
        ResearchState CaptureResearch() {
            if(!researchDateKorea.HasValue)return null;
            return new ResearchState{Day=researchDateKorea.Value,Progress=scanned,Total=researchTotal,Complete=researchComplete,
                Requested=researchRequested,Scanning=scanning,References=preparedReferences.Values.ToList(),Remaining=scans.ToList()};
        }
        bool RestoreResearch(ResearchState saved,DateTime now) {
            if(saved==null||saved.Day!=AnalysisTarget(now)||saved.References.Any(x=>!x.Matches(configuration.Strategy)
                ||!symbols.Any(s=>s.Code==x.Code&&s.Market==x.Market))||saved.Remaining.Any(x=>!symbols.Any(s=>s.Code==x.Code&&s.Market==x.Market)))return false;
            preparedReferences.Clear();foreach(var prepared in saved.References)preparedReferences.Add(prepared.Code,prepared);
            scans.Clear();foreach(var symbol in saved.Remaining)scans.Enqueue(symbol);
            researchDateKorea=saved.Day;scanned=saved.Progress;researchTotal=saved.Total;researchComplete=saved.Complete;
            researchRequested=saved.Requested;scanning=saved.Scanning;openingAdopted=false;candidatesGrid.Rows.Clear();
            foreach(var prepared in saved.References)candidatesGrid.Rows.Add(prepared.Code,prepared.Name,prepared.PriorHigh,prepared.RankScore,"완료봉 "+prepared.LastCompletedBarDate,"실제 장중 시세 대기");
            RefreshPreparedWatchlist(now);return true;
        }
        void BeginResearch(DateTime now,bool force=false) {
            if(symbols.Count==0)throw new AccountUserActionException("종목 조회를 먼저 실행하십시오.");
            DateTime target=AnalysisTarget(now);
            if(force||researchDateKorea!=target) {
                InvalidateResearch();researchDateKorea=target;scanning=false;requestedScan=false;scans.Clear();buyQuotes.Clear();
                entry=null;preflightDirty=false;candidateQuoteSince.Clear();candidateQuoteDue.Clear();candidatesGrid.Rows.Clear();scanned=0;
            }
            researchRequested=true;
            // Avoid freezing a still-forming afternoon bar for the next day.
            // Preparation starts on the target calendar day, from midnight.
            if(MarketTime.ToKorea(now).Date<target){UpdateResearchStatus(now);return;}
            if(scanning||researchComplete){UpdateResearchStatus(now);return;}
            foreach(var symbol in symbols.OrderBy(x=>x.Code))scans.Enqueue(symbol);
            researchTotal=scans.Count;scanning=true;RefreshPreparedWatchlist(now);UpdateResearchStatus(now);
            Log("장전·장중 공통 사전분석 시작: 목표 "+target.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+" / 완료봉만 사용");
        }
        void RefreshPreparedWatchlist(DateTime now) {
            var reserved=new HashSet<string>(holdings.Where(x=>x.Quantity>0).Select(x=>x.Code),StringComparer.Ordinal);
            if(journal!=null)foreach(var order in journal.Snapshot.Orders.Where(LiveOrderJournal.IsUnresolved))reserved.Add(order.Code);
            PremarketReference pinned=null;
            if(entry!=null&&entry.AutoSize&&entry.Intent!=null&&!reserved.Contains(entry.Intent.Code))preparedReferences.TryGetValue(entry.Intent.Code,out pinned);
            int capacity=Math.Max(0,200-reserved.Count);
            watchedReferences.Clear();
            if(pinned!=null&&capacity>0){watchedReferences.Add(pinned);capacity--;}
            watchedReferences.AddRange(PremarketResearch.Rank(preparedReferences.Values.Where(x=>!reserved.Contains(x.Code)&&(pinned==null||x.Code!=pinned.Code)),capacity));
            var selected=new HashSet<string>(watchedReferences.Select(x=>x.Code),StringComparer.Ordinal);
            var retained=buyQuotes.Where(x=>selected.Contains(x.Code)).ToList();buyQuotes.Clear();foreach(var candidate in retained)buyQuotes.Enqueue(candidate);
            ReleaseUnusedQuotes();UpdateResearchStatus(now);
        }
        void UpdateResearchStatus(DateTime now) {
            if(!researchDateKorea.HasValue)return;
            string progress=scanned.ToString(CultureInfo.InvariantCulture)+"/"+researchTotal.ToString(CultureInfo.InvariantCulture);
            string counts=" / 준비 "+preparedReferences.Count+" · 감시목록 "+watchedReferences.Count+" · 미감시 "+Math.Max(0,preparedReferences.Count-watchedReferences.Count);
            if(MarketTime.ToKorea(now).Date<researchDateKorea.Value)
                researchStatus.Text="장전 분석 예약: "+researchDateKorea.Value.ToString("MM-dd",CultureInfo.InvariantCulture)+" 00:00부터 / 미완성 당일봉 제외";
            else researchStatus.Text=researchDateKorea.Value.ToString("MM-dd",CultureInfo.InvariantCulture)+" "+(openingAdopted?"장중: 준비 자료 사용":"장전 준비")+" / "+(researchComplete?"분석 완료":("분석 "+progress))+counts+(scanning?" / 잔여 분석 계속":"");
        }
        void AdoptPreparedResearch(DateTime now) {
            BeginResearch(now,false);openingAdopted=true;RefreshPreparedWatchlist(now);
            if(researchComplete)session.ScanCompleted(now);
            UpdateResearchStatus(now);Log("개장: 준비 자료와 잔여 분석을 유지하고 실제 장중 시세를 확인합니다.");
        }
        void RegisterPreparedQuotes() {
            if(realtime==null||!realtime.Connected||realtime.Failed)return;
            foreach(var prepared in watchedReferences.Where(x=>!trackedQuotes.Contains(x.Code)).Take(5).ToList())
                if(!EnsureLiveQuote(prepared.Code))break;
        }
        void EvaluatePreparedSignals(DateTime now) {
            int ready=0;RealtimeQuote quote;
            if(realtime!=null)foreach(var symbol in trackedQuotes.ToList())if(realtime.TryGetQuote(symbol,now,out quote))ready++;
            bool marketOpen=Rules.ValidTime(MarketTime.ToKorea(now));
            string diagnostic=realtime==null?"실시간 채널 미연결":realtime.DiagnosticsSummary(now);
            quoteStatus.Text="실시간 SC: 확인 "+ready+" / 등록 "+trackedQuotes.Count+(marketOpen?" 종목 / 연속 체결·시각 확인 기준":" 종목 / 장전·장외 대기")+Environment.NewLine+diagnostic;
            quoteStatus.ForeColor=marketOpen&&trackedQuotes.Count>0&&ready==0?Color.DarkRed:Color.Black;
            if(realtime!=null&&trackedQuotes.Count>0&&(!lastRealtimeDiagnosticUtc.HasValue||now-lastRealtimeDiagnosticUtc.Value>=TimeSpan.FromSeconds(ready==0?30:60))) {
                Log("SC 진단: "+diagnostic);lastRealtimeDiagnosticUtc=now;
            }
            if(!LiveStarted||!ActiveSession||realtime==null)return;
            foreach(var prepared in watchedReferences.ToList()) {
                if(holdings.Any(x=>x.Code==prepared.Code&&x.Quantity>0)||buyQuotes.Any(x=>x.Code==prepared.Code)
                    ||entry!=null&&entry.Intent!=null&&entry.Intent.Code==prepared.Code)continue;
                if(!realtime.TryGetQuote(prepared.Code,now,out quote)){SetCandidateStatus(prepared.Code,"실시간 연속 시세 확인 대기");continue;}
                string status;var candidate=PremarketResearch.EvaluateLive(prepared,quote,configuration.Strategy,now,out status);
                if(candidate==null){SetCandidateStatus(prepared.Code,status);continue;}
                SetCandidateStatus(prepared.Code,status);
                string key="entry-"+MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-"+candidate.Code;
                if(journal==null||journal.Snapshot.Orders.Any(x=>x.ClientKey==key))continue;
                buyQuotes.Enqueue(candidate);SetCandidateStatus(candidate.Code,"실시간 돌파·거래량 확인 / 계좌 재조회 대기");
            }
        }
        void ClearWork(){scanning=false;requestedScan=false;firstQueryQueued=false;pendingSessionScan=false;connectionEpoch++;scans.Clear();buyQuotes.Clear();entry=null;preflightDirty=false;accountReads.Clear();InvalidateResearch();}
        public void Halt() {
            authorization.Revoke();if(readiness!=null)readiness.SetAuthorization(false);
            session.Disarm();awake.Dispose();monitoring=false;ClearWork();
            if(api!=null)api.CancelPending();
            try{if(journal!=null&&journal.Snapshot.Orders.Any(LiveOrderJournal.IsUnresolved))Log("운영 중단은 증권사 미체결 주문을 취소하지 않습니다. 주문 상태를 별도로 확인하십시오.");}
            catch{Program.DiagnosticFile("Order journal is unavailable while stopping.");}
            CloseGateway();
            if(boundKey!=null&&!reportWriteFault)try{WriteReport(MarketTime.NowKorea.Date);}catch(Exception ex){Log("보고서 저장 실패: "+ex.GetType().Name);storageFault=true;}
            ReleaseAccount();password.Clear();UpdateOrderStatus();state.Text=closingForm?"종료 중":"운영 중단 / 실주문 시작 해제 / 최근 실계좌 조회값 표시";
            sellDecisions.Clear();RefreshAccount();
        }
        void CloseGateway(){if(gateway!=null){var previous=gateway;gateway=null;previous.OrderChanged-=OnGatewayOrderChanged;previous.Dispose();}orderAcknowledgementPending=false;}
        void ReleaseAccount(){authorization.Revoke();CloseGateway();DisposeRealtime();accountEvents=null;readiness=null;ordinaryHoldings=null;if(journal!=null){journal.Dispose();journal=null;}if(credentials!=null){credentials.Dispose();credentials=null;}boundKey=null;account.ReadOnly=false;}
        void ClearAccountData(){holdings.Clear();priceReceivedUtc.Clear();quoteDue.Clear();sellDecisions.Clear();balanceSignatures.Clear();cash=null;brokerOrders=null;tradeHoldings=null;ordinaryHoldings=null;holdingsReceivedUtc=null;lastBasisDiagnostic=null;lastCashProfileDiagnostic=null;lastSavedReportPath=null;lastReportSavedUtc=null;initialReportComplete=false;if(!reportWriteFault)reportStatus.Text="보고서: 계좌 조회 응답 대기 (CSV)";blockedSignals.Clear();RefreshAccount();RefreshOrders();}
        void CloseSession() {
            DateTime day=session.SessionDateKorea??MarketTime.NowKorea.Date;string reason=session.CloseReason;
            authorization.Revoke();if(readiness!=null)readiness.SetAuthorization(false);UpdateOrderStatus();
            ClearWork();if(api!=null)api.CancelPending();
            // Keep the separate ACK and AA channels alive for orders already
            // accepted by the broker. Revocation only prevents new submissions.
            WriteReport(day);session.SessionClosed();session.Disarm();Log("장중 운영 종료·보고서 저장: "+day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+" / "+reason);
            sellDecisions.Clear();RefreshAccount();
            liveDayCompleted=true;nextAccountReadUtc=DateTime.UtcNow;UpdateClosedMonitoringStatus();
        }
        void Schedule(){ScheduleAt(DateTime.UtcNow);}
        void ScheduleAt(DateTime now) {
            if(now.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC scheduler time required.");
            if(api!=null)api.PollReadOnlyRetry(now);
            // Persist the copied account state independently of TR queues and
            // preflight work. A busy broker must not starve a due report.
            SaveReportIfDue(now);
            if(gateway!=null) {
                gateway.Timeout(now);
                if(orderAcknowledgementPending&&!gateway.Busy) {
                    orderAcknowledgementPending=false;RefreshOrders();
                    if(journal!=null&&journal.RecoveryRequired){Halt();Log("실주문 접수 결과 불명: 증권사 대사 전까지 운영을 중단했습니다.");return;}
                }
            }
            if(displayDirty){displayDirty=false;RefreshAccount();}
            if(liveDayCompleted)UpdateClosedMonitoringStatus();
            var action=session.Tick(now,(api!=null&&api.Busy)||pendingSessionScan||entry!=null||accountReads.Count>0);
            if(action==UnattendedAction.CloseSession){CloseSession();return;}
            if(action==UnattendedAction.StartScan) {
                AdoptPreparedResearch(now);
            }
            if(researchRequested&&!scanning&&!researchComplete&&api!=null&&api.Connected)BeginResearch(now,false);
            RegisterPreparedQuotes();EvaluatePreparedSignals(now);
            if(api==null||!api.Connected||api.Busy||firstQueryQueued)return;
            if(credentials!=null&&entry!=null) {
                if(!LiveStarted){entry=null;preflightDirty=false;}
                else if(now>=entry.NotBeforeUtc){RunPreflight(now);return;}
            }
            if(monitoring&&credentials!=null&&now>=nextAccountReadUtc)QueueAccountRefresh();
            if(accountReads.Count>0&&credentials!=null) {
                string kind=accountReads.Dequeue();
                ReadAccountKind(kind,now);return;
            }
            var due=monitoring&&(ActiveSession||!scanning)?holdings.Where(x=>x.Quantity>0&&(!quoteDue.ContainsKey(x.Code)||quoteDue[x.Code]<=now)).OrderBy(x=>quoteDue.ContainsKey(x.Code)?quoteDue[x.Code]:DateTime.MinValue).FirstOrDefault():null;
            bool research=buyQuotes.Count>0||(scanning&&scans.Count>0);
            if(due!=null&&(holdingRequests<3||!research)) {
                holdingRequests=Math.Min(3,holdingRequests+1);quoteDue[due.Code]=now.AddSeconds(ActiveSession?1:15);
                RealtimeQuote quote;
                if(EnsureLiveQuote(due.Code)&&realtime.TryGetQuote(due.Code,now,out quote)) {
                    string reason=EvaluateHoldingSell(due,quote.ToReply(),now);RefreshAccount();
                    if(LiveStarted&&ActiveSession&&!string.IsNullOrEmpty(reason)&&entry==null)
                        QueueOrder(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code=due.Code,Quantity=checked((int)due.Quantity),LimitPrice=quote.Price,
                            ClientKey="exit-"+MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-"+due.Code},quote,null,false,now);
                } else if(!ActiveSession&&!scanning)api.SnapshotQuote(due.Code);
                else{SetHoldingDecision(due,"실시간 연속 시세 확인 대기");RefreshAccount();}
                return;
            }
            holdingRequests=0;
            if(entry==null&&buyQuotes.Count>0&&LiveStarted&&ActiveSession) {
                var candidate=buyQuotes.Dequeue();DateTime next;
                if(candidateQuoteDue.TryGetValue(candidate.Code,out next)&&now<next)buyQuotes.Enqueue(candidate);
                else {
                    RealtimeQuote quote;
                    if(EnsureLiveQuote(candidate.Code)&&realtime.TryGetQuote(candidate.Code,now,out quote)) {
                        candidateQuoteSince.Remove(candidate.Code);candidateQuoteDue.Remove(candidate.Code);
                        if(ValidEntrySignal(candidate,quote,now)) {
                            QueueOrder(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code=candidate.Code,Quantity=1,LimitPrice=quote.Price,
                                ClientKey="entry-"+MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-"+candidate.Code},quote,candidate,true,now);
                            return;
                        }
                    } else {
                        DateTime started;if(!candidateQuoteSince.TryGetValue(candidate.Code,out started)){started=now;candidateQuoteSince[candidate.Code]=now;}
                        if(now-started<=TimeSpan.FromSeconds(30)){candidateQuoteDue[candidate.Code]=now.AddSeconds(5);buyQuotes.Enqueue(candidate);}
                        else{Log("후보 시세 확인 대기 종료 "+candidate.Code+" / 연속 장중 시세 미확인");ReleaseUnusedQuotes();}
                    }
                }
            }
            if(scanning&&scans.Count>0){requestedScan=true;api.Bars(scans.Dequeue().Code,BarCount());return;}
            if(scanning&&scans.Count==0&&!requestedScan){scanning=false;researchComplete=true;if(ActiveSession)session.ScanCompleted(now);RefreshPreparedWatchlist(now);UpdateResearchStatus(now);state.Text="사전분석 완료 / 실제 장중 시세로 준비 후보 검증";Log("사전분석 완료. 동일 거래일 완료봉을 반복 조회하지 않고 실시간 시세로 조건을 확인합니다.");if(boundKey!=null)WriteReport(MarketTime.ToKorea(now).Date);}
        }
        void Reply(IndiReply reply) {
            ApplyReply(reply);
            if(!initialReportComplete&&(reply.Kind=="TRADEHOLDINGS"||reply.Kind=="HOLDINGS"||reply.Kind=="CASH"||reply.Kind=="ORDERS"))
                WriteReport(MarketTime.NowKorea.Date);
        }
        void ApplyReply(IndiReply reply) {
            var now=DateTime.UtcNow;bool stamped=reply.RequestStartedUtc.Kind==DateTimeKind.Utc&&reply.ReceivedUtc.Kind==DateTimeKind.Utc&&reply.RequestStartedUtc<=reply.ReceivedUtc;
            if(reply.Kind=="STOCKS") {
                symbols.Clear();symbols.AddRange(reply.Stocks);instruments.Clear();foreach(var instrument in reply.Instruments)instruments[instrument.Code]=instrument;
                state.Text="종목 응답 "+reply.Count+"건 / 일반주식 "+symbols.Count+"개";
                if(researchRequested)BeginResearch(now,false);return;
            }
            if(reply.Kind=="TRADEHOLDINGS") {
                tradeHoldings=reply.TradeHoldings;if(tradeHoldings==null)throw new InvalidOperationException("매매기준 잔고 응답이 없습니다.");
                if(readiness!=null&&stamped)readiness.ObserveTradeBalance(tradeHoldings);
                if(accountEvents!=null&&tradeHoldings.BrokerDateValidated)accountEvents.SetBrokerDate(tradeHoldings.BrokerDate,tradeHoldings.ReceivedUtc);
                ApplyHoldings(tradeHoldings.Holdings,stamped?(DateTime?)reply.ReceivedUtc:null);
                LogBasisDiagnostic();FinishPreflight("TRADEHOLDINGS");
                if(tradeHoldings.BasisVerified)state.Text="매매기준 실계좌 잔고 "+holdings.Count+"종목 / 기존·신규 보유 함께 감시";
                else{state.Text="잔고 조회 계속 / 주문 잔고기준 확인 대기 · "+holdings.Count+"종목 표시";UpdateOrderStatus("조회 계속 / 주문 잔고기준 확인 대기");}
                return;
            }
            if(reply.Kind=="ORDINARYHOLDINGS") {
                ordinaryHoldings=reply.TradeHoldings;if(ordinaryHoldings==null)throw new InvalidOperationException("보통잔고 조회 응답이 없습니다.");
                if(readiness!=null&&stamped)readiness.ObserveOrdinaryBalance(ordinaryHoldings);
                sellDecisions.Clear();LogBasisDiagnostic();FinishPreflight("ORDINARYHOLDINGS");RefreshAccount();return;
            }
            if(reply.Kind=="HOLDINGS") {
                if(tradeHoldings==null)ApplyHoldings(reply.Holdings,stamped?(DateTime?)reply.ReceivedUtc:null);
                state.Text="결제일 잔고 응답 / 자동 주문은 매매기준 잔고를 별도 확인합니다.";return;
            }
            if(reply.Kind=="CASH"){cash=reply.Cash;if(readiness!=null&&stamped)readiness.ObserveCash(cash,reply.RequestStartedUtc);FinishPreflight("CASH");RefreshAccount();return;}
            if(reply.Kind=="ORDERS"){brokerOrders=reply.BrokerOrders;if(readiness!=null&&stamped)readiness.ObserveOrders(brokerOrders,reply.RequestStartedUtc);FinishPreflight("ORDERS");RefreshOrders();return;}
            if(reply.Kind=="CAPACITY") {
                if(readiness!=null&&stamped)readiness.ObserveCapacity(reply.Capacity,reply.RequestStartedUtc);
                if(entry!=null&&reply.Capacity!=null&&reply.Capacity.Code==entry.Intent.Code&&reply.Capacity.LimitPrice==entry.Intent.LimitPrice)entry.Capacity=reply.Capacity;
                FinishPreflight("CAPACITY");return;
            }
            if(reply.Kind=="BARS") {
                bool preflightBars=entry!=null&&entry.WaitingFor=="BARS";
                if(preflightBars&&reply.Bars.Count>0&&stamped) {
                    string date=reply.Bars.Select(x=>x.Date).OrderByDescending(x=>x,StringComparer.Ordinal).First();
                    if(readiness!=null)readiness.ObserveTradingDate(reply.Code,date,reply.RequestStartedUtc,reply.ReceivedUtc);
                    if(accountEvents!=null&&date==MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture))accountEvents.SetBrokerDate(date,reply.ReceivedUtc);
                }
                FinishPreflight("BARS");if(preflightBars)return;
                bool fromScan=requestedScan;requestedScan=false;var symbol=symbols.FirstOrDefault(x=>x.Code==reply.Code);
                if(fromScan&&researchDateKorea.HasValue) {
                    var prepared=PremarketResearch.Create(symbol,reply.Bars,configuration.Strategy,researchDateKorea.Value);scanned++;
                    if(prepared!=null){preparedReferences[prepared.Code]=prepared;candidatesGrid.Rows.Add(prepared.Code,prepared.Name,prepared.PriorHigh,prepared.RankScore,"완료봉 "+prepared.LastCompletedBarDate+" / 장중 돌파·거래량 확인 필요","장중 실제 시세 대기");}
                    RefreshPreparedWatchlist(now);UpdateResearchStatus(now);return;
                }
                var candidate=Rules.Entry(reply.Code,symbol==null?reply.Code:symbol.Name,reply.Bars,configuration.Strategy);scanned++;
                if(candidate!=null){candidatesGrid.Rows.Add(candidate.Code,candidate.Name,candidate.Price,candidate.Score,candidate.Reason,LiveStarted?"실시간 시세·계좌 확인 대기":"실주문 미시작");if(fromScan&&ActiveSession&&LiveStarted)buyQuotes.Enqueue(candidate);}
                state.Text="일봉 분석 "+scanned+" / 대기 "+scans.Count+" / 후보 "+candidatesGrid.Rows.Count;return;
            }
            if(reply.Kind!="QUOTE")return;
            var held=holdings.SingleOrDefault(x=>x.Code==reply.Code);
            if(held!=null){held.Price=reply.Price;if(stamped)priceReceivedUtc[held.Code]=reply.ReceivedUtc;RefreshAccount();}
            state.Text="조회 전용 현재가 "+reply.Code+" "+reply.Price.ToString("N0",CultureInfo.CurrentCulture)+"원 / "+(reply.HasTradeClock?"응답 체결시각 "+reply.Clock:"이 조회는 체결시각을 제공하지 않습니다. 실시간 SC 상태는 별도 표시합니다.");
            Log("현재가 "+reply.Code+" "+reply.Price.ToString(CultureInfo.InvariantCulture)+" / 거래시각="+(reply.HasTradeClock?reply.Clock:"미확인"));
            // RequestData snapshots are display-only. Only the dedicated SC
            // stream's advancing events may enter the automatic order path.
        }
        void ApplyHoldings(IEnumerable<Holding> values,DateTime? receivedUtc) {
            sellDecisions.Clear();holdings.Clear();holdings.AddRange(values);holdingsReceivedUtc=receivedUtc;
            foreach(var holding in holdings) {
                if(holding.Price>0&&receivedUtc.HasValue)priceReceivedUtc[holding.Code]=receivedUtc.Value;
                if(holding.Quantity>0)EnsureLiveQuote(holding.Code);
            }
            if(settingsStore!=null&&boundKey!=null&&(tradeHoldings==null||tradeHoldings.BasisVerified)){settingsStore.RefreshPositions(boundKey,holdings);configuration=settingsStore.Snapshot;}
            if(researchDateKorea.HasValue)RefreshPreparedWatchlist(DateTime.UtcNow);
            RefreshAccount();
        }
        void LogBasisDiagnostic() {
            if(tradeHoldings==null)return;
            var categories=new[]{"TradeBasisCode1","SettlementBasisCode2","CreditBalanceCode3","BondBalanceCode4","OrdinaryBalanceCode5","Blank","Unknown"};
            var counts=categories.ToDictionary(x=>x,x=>0,StringComparer.Ordinal);
            foreach(var holding in tradeHoldings.Holdings) {
                string category;
                if(!tradeHoldings.BasisDiagnostics.TryGetValue(holding.Code,out category)||!counts.ContainsKey(category??""))category="Unknown";
                counts[category]++;
            }
            string line="SABA609Q1 잔고기준 분류: "+string.Join("; ",categories.Select(x=>x+"="+counts[x].ToString(CultureInfo.InvariantCulture)))
                +"; BasisVerified="+(tradeHoldings.BasisVerified?"True":"False");
            string details=AccountQueries.BasisDiagnosticsSummary(tradeHoldings);
            basisStatus.Text="잔고 검증: "+(tradeHoldings.BasisVerified?"정상 조회 응답 확인 / 실제 주문가능금액·매도가능수량은 주문 직전 재확인":
                tradeHoldings.RequestBasisVerified?"조회 설정과 응답이 일치하지 않아 재확인 필요":"계좌 조회 요청·응답 확인 대기");
            var now=DateTime.UtcNow;
            int cashReady=holdings.Count(held=>HoldingSourcePendingAt(held,now)==null);
            basisStatus.Text="현물 구분 "+cashReady+"/"+holdings.Count+" / "+basisStatus.Text;
            string cashDiagnostic="현물 구분 대조: 보통잔고 요청="+(ordinaryHoldings!=null&&ordinaryHoldings.RequestOrdinaryOnlyVerified?"확인":"대기")+"; "+string.Join("; ",holdings.Select(held=> {
                string detail;return held.Code+" "+(tradeHoldings.CashProfileDiagnostics.TryGetValue(held.Code,out detail)?detail:"Unavailable")+"; 대조="+(HoldingSourcePendingAt(held,now)==null?"확인":"대기");
            }));
            if(cashDiagnostic!=lastCashProfileDiagnostic){lastCashProfileDiagnostic=cashDiagnostic;Log(cashDiagnostic);}
            if(line+details!=lastBasisDiagnostic||!lastBasisDiagnosticUtc.HasValue||now-lastBasisDiagnosticUtc.Value>=TimeSpan.FromMinutes(1)) {
                lastBasisDiagnostic=line+details;lastBasisDiagnosticUtc=now;Log(line);Log(details);
            }
        }
        string HoldingSourcePending(Holding held) {return HoldingSourcePendingAt(held,DateTime.UtcNow);}
        string HoldingSourcePendingAt(Holding held,DateTime utc) {
            if(tradeHoldings==null)return null;
            bool verified;
            if(!tradeHoldings.BasisVerified||!tradeHoldings.BasisValidated.TryGetValue(held.Code,out verified)||!verified)return "주문 잔고기준 확인 대기";
            if((!tradeHoldings.CashSourceValidated.TryGetValue(held.Code,out verified)||!verified)
                &&!AccountQueries.IsOrdinaryCashSource(tradeHoldings,ordinaryHoldings,held.Code,utc))return "현물 잔고구분 확인 대기";
            return null;
        }
        bool ValidEntrySignal(Candidate candidate,RealtimeQuote quote,DateTime now) {
            PremarketReference prepared;
            return candidate!=null&&quote!=null&&preparedReferences.TryGetValue(candidate.Code,out prepared)
                &&PremarketResearch.EvaluateLive(prepared,quote,configuration.Strategy,now)!=null
                &&watchedReferences.Any(x=>x.Code==candidate.Code&&x.TargetDateKorea==MarketTime.ToKorea(now).Date)&&candidate.Code==quote.Code&&candidate.SignalDate==MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)
                &&quote.Price>=configuration.Strategy.MinPrice&&quote.Price<=configuration.Strategy.MaxPrice&&quote.Price>=candidate.Price*0.97m&&quote.Price<=candidate.Price*1.03m
                &&!holdings.Any(x=>x.Code==candidate.Code&&x.Quantity>0)&&ProjectedPositionCount()<configuration.Strategy.MaxPositions;
        }
        bool AlreadyRecorded(LiveOrderIntent intent) {
            if(journal==null)return true;
            return journal.Snapshot.Orders.Any(x=>(x.ClientKey==intent.ClientKey&&x.Status!=LiveOrderStatus.NotSent)||((intent.Kind==LiveOrderKind.Buy||intent.Kind==LiveOrderKind.Sell)&&x.Code==intent.Code&&LiveOrderJournal.IsUnresolved(x)));
        }
        void QueueOrder(LiveOrderIntent intent,RealtimeQuote quote,Candidate candidate,bool autoSize,DateTime now) {
            if(!LiveStarted||entry!=null||gateway==null||gateway.Busy||readiness==null||journal==null)return;
            if(journal.RecoveryRequired){Halt();Log("실주문 결과 불명 상태를 먼저 증권사와 대사해야 합니다.");return;}
            try{LiveOrderProtocol.ValidateIntent(intent);}catch(ArgumentException){UpdateOrderStatus("주문 종목·수량·가격 확인 필요");return;}
            CashInstrument instrument;instruments.TryGetValue(intent.Code,out instrument);
            if(intent.Kind!=LiveOrderKind.Cancel&&!CashMarketRules.CanUseQuoteLimit(instrument,quote,intent.LimitPrice,now)) {
                SetCandidateStatus(intent.Code,"현재 정규장 체결가격 확인 필요");UpdateOrderStatus("현재 정규장 체결가격과 같은 지정가만 지원합니다.");return;
            }
            if(AlreadyRecorded(intent)){SetCandidateStatus(intent.Code,"기존 주문 접수·체결 확인");return;}
            // Initialize the dedicated order control before invalidating and
            // collecting fresh proofs. Initial account queries never create it.
            var activeGateway=gateway;string accountBinding=boundKey;
            activeGateway.PrepareChannel();
            if(gateway!=activeGateway||boundKey!=accountBinding||!LiveStarted||!ActiveSession||readiness==null)return;
            entry=new EntryRequest{Intent=intent,Quote=quote,Candidate=candidate,AutoSize=autoSize};RestartPreflight(now,false);
        }
        void RestartPreflight(DateTime now,bool delay) {
            if(entry==null)return;entry.Reads.Clear();entry.WaitingFor=null;entry.Capacity=null;entry.Attempts++;
            entry.Reads.Enqueue("BARS");
            if(entry.Intent.Kind==LiveOrderKind.Sell||entry.Intent.Kind==LiveOrderKind.Amend)entry.Reads.Enqueue("ORDINARYHOLDINGS");
            entry.Reads.Enqueue("CASH");entry.Reads.Enqueue("TRADEHOLDINGS");entry.Reads.Enqueue("ORDERS");
            if(entry.Intent.Kind==LiveOrderKind.Buy||entry.Intent.Kind==LiveOrderKind.Amend)entry.Reads.Enqueue("CAPACITY");
            entry.NotBeforeUtc=delay?now.AddSeconds(Math.Min(15,entry.Attempts*3)):now.AddMilliseconds(1);
            accountReads.Clear();preflightDirty=false;readiness.Invalidate(now);
        }
        void FinishPreflight(string kind){if(entry!=null&&entry.WaitingFor==kind)entry.WaitingFor=null;}
        void RunPreflight(DateTime now) {
            if(entry==null||!LiveStarted||gateway==null||gateway.Busy)return;
            if(preflightDirty){RestartPreflight(now,false);return;}
            if(entry.WaitingFor!=null)return;
            if(entry.Reads.Count==0){SubmitPending(now);return;}
            string kind=entry.Reads.Peek();
            if(kind=="CAPACITY") {
                RealtimeQuote quote;
                if(!EnsureLiveQuote(entry.Intent.Code)||!realtime.TryGetQuote(entry.Intent.Code,now,out quote)){UpdateOrderStatus("실시간 연속 시세 확인 대기");RestartPreflight(now,true);return;}
                if(entry.AutoSize)entry.Intent.LimitPrice=quote.Price;
                entry.Quote=quote;
            }
            entry.Reads.Dequeue();entry.WaitingFor=kind;
            if(kind=="BARS"){requestedScan=false;api.Bars(entry.Intent.Code,2);return;}
            if(kind=="CAPACITY")credentials.Use((number,secret)=>api.Capacity(number,secret,entry.Intent.Code,entry.Intent.LimitPrice));
            else ReadAccountKind(kind,now);
        }
        void SubmitPending(DateTime now) {
            var pending=entry;if(pending==null||!LiveStarted||readiness==null||journal==null)return;
            if(AlreadyRecorded(pending.Intent)){entry=null;return;}
            RealtimeQuote quote=pending.Quote;
            if(pending.Intent.Kind!=LiveOrderKind.Cancel) {
                RealtimeQuote latest;
                if(!EnsureLiveQuote(pending.Intent.Code)||!realtime.TryGetQuote(pending.Intent.Code,now,out latest)){RestartPreflight(now,true);return;}
                if(pending.AutoSize) {
                    PremarketReference reference;
                    if(!preparedReferences.TryGetValue(pending.Intent.Code,out reference)||PremarketResearch.EvaluateLive(reference,latest,configuration.Strategy,now)==null){SetCandidateStatus(pending.Intent.Code,"장중 돌파·거래량 조건 해제");entry=null;return;}
                }
                if(pending.Intent.Kind==LiveOrderKind.Sell){quote=latest;pending.Quote=latest;pending.Intent.LimitPrice=latest.Price;}
                if(quote==null||!MarketTime.CanUseQuote(quote.ToReply(),now)) {
                    if(!EnsureLiveQuote(pending.Intent.Code)||!realtime.TryGetQuote(pending.Intent.Code,now,out quote)){RestartPreflight(now,true);return;}
                    if(pending.AutoSize||pending.Intent.Kind==LiveOrderKind.Sell){pending.Intent.LimitPrice=quote.Price;pending.Quote=quote;RestartPreflight(now,false);return;}
                }
            }
            if(pending.AutoSize) {
                if(!ValidEntrySignal(pending.Candidate,quote,now)||cash==null||pending.Capacity==null){entry=null;ReleaseUnusedQuotes();return;}
                var totals=AccountValuation.From(holdings);if(!totals.CostBasis.HasValue){RestartPreflight(now,true);return;}
                decimal remaining=Math.Max(0m,configuration.BudgetLimit-totals.CostBasis.Value-journal.ReservedBuyAmount);
                int quantity=Rules.OrderQuantity(Math.Min(cash.NonNegativeCash100,Math.Min(remaining,pending.Capacity.Amount)),pending.Intent.LimitPrice,configuration.Strategy.AllocationFraction,configuration.Strategy.FeeRate);
                quantity=Math.Min(quantity,(int)Math.Min(int.MaxValue,pending.Capacity.Quantity));
                if(quantity<=0){SetCandidateStatus(pending.Intent.Code,"가용금액·운용한도 부족");entry=null;ReleaseUnusedQuotes();return;}
                pending.Intent.Quantity=quantity;
            } else if(pending.Intent.Kind==LiveOrderKind.Sell) {
                var held=holdings.SingleOrDefault(x=>x.Code==pending.Intent.Code&&x.Quantity>0);
                if(held==null||string.IsNullOrEmpty(EvaluateHoldingSell(held,quote.ToReply(),now))){entry=null;return;}
                pending.Intent.Quantity=checked((int)held.Quantity);
            }
            CashInstrument instrument;instruments.TryGetValue(pending.Intent.Code,out instrument);
            bool priceRule=pending.Intent.Kind==LiveOrderKind.Cancel||CashMarketRules.CanUseQuoteLimit(instrument,quote,pending.Intent.LimitPrice,now);
            var valuation=AccountValuation.From(holdings);decimal remainingLimit=valuation.CostBasis.HasValue?Math.Max(0m,configuration.BudgetLimit-valuation.CostBasis.Value):0m;
            var result=readiness.Evaluate(pending.Intent,quote==null?null:quote.ToReply(),quote==null?now:quote.ReceivedUtc,
                quote!=null&&quote.SequenceConfirmed,priceRule,journal.Snapshot,remainingLimit,now);
            UpdateOrderStatus(result.Status);
            if(!result.Ready){SetCandidateStatus(pending.Intent.Code,result.Status);RestartPreflight(now,true);return;}
            result.Checks.Authorization=authorization;result.Checks.EvidenceStillCurrent=result.EvidenceStillCurrent;
            submitting=true;
            try {
                var activeGateway=gateway;
                var sent=credentials.Use((number,secret)=>activeGateway.Submit(pending.Intent,new LiveAccountCredentials(number,secret),result.Checks,DateTime.UtcNow));
                if(gateway!=activeGateway||credentials==null){entry=null;return;}
                orderAcknowledgementPending=gateway.Busy;entry=null;preflightDirty=false;QueueAccountRefresh();
                SetCandidateStatus(sent.Code,"주문 기록 "+sent.Status);Log("실주문 기록 "+sent.Code+" / "+sent.Kind+" / "+sent.Status);RefreshOrders();
            }catch(LiveOrderNotSentException) {
                if(!LiveStarted||readiness==null||gateway==null||!authorization.ValidUntilUtc.HasValue||DateTime.UtcNow>=authorization.ValidUntilUtc.Value){entry=null;return;}
                pending.NotSentAttempts++;
                if(pending.NotSentAttempts>=3) {
                    authorization.Revoke();readiness.SetAuthorization(false);entry=null;
                    UpdateOrderStatus("미전송 반복으로 실주문 시작 해제");Log("계좌 자료 변경으로 주문을 전송하지 않았습니다. 3회 재확인 후 실주문 시작을 해제했습니다.");
                } else {
                    entry=pending;RestartPreflight(DateTime.UtcNow,true);UpdateOrderStatus("계좌 자료 변경으로 미전송 / 다시 조회 중");
                }
                RefreshOrders();
            }finally{submitting=false;}
        }
        void SetCandidateStatus(string symbol,string status) {
            foreach(DataGridViewRow row in candidatesGrid.Rows)if(Convert.ToString(row.Cells[0].Value,CultureInfo.InvariantCulture)==symbol)row.Cells[5].Value=status;
        }
        int ProjectedPositionCount() {
            var occupied=new HashSet<string>(holdings.Where(x=>x.Quantity>0).Select(x=>x.Code),StringComparer.Ordinal);
            if(journal!=null)foreach(var order in journal.Snapshot.Orders.Where(x=>x.TradeKind==LiveOrderKind.Buy&&LiveOrderJournal.IsUnresolved(x)))occupied.Add(order.Code);
            if(brokerOrders!=null)foreach(var order in brokerOrders.Orders.Where(x=>x.SideCode=="2"&&x.OpenQuantity>0))occupied.Add(order.Code);
            return occupied.Count;
        }
        void ChangeSelectedOrder(bool amend) {
            RequireIdle();if(credentials==null||ordersGrid.SelectedRows.Count==0)throw new AccountUserActionException("실계좌와 증권사 주문을 선택하십시오.");
            if(!LiveStarted)throw new AccountUserActionException("먼저 이 계좌의 실주문 운영 시작을 누르십시오.");
            if(entry!=null||gateway==null||gateway.Busy)throw new AccountUserActionException("앞선 주문 확인 후 다시 실행하십시오.");
            var selected=ordersGrid.SelectedRows[0].Tag as BrokerOrderRow;if(selected==null||selected.OpenQuantity<=0)throw new AccountUserActionException("미체결 증권사 주문을 선택하십시오.");
            var intent=new LiveOrderIntent{Kind=amend?LiveOrderKind.Amend:LiveOrderKind.Cancel,Code=selected.Code,
                Quantity=checked((int)Math.Min(orderQuantity.Value,selected.OpenQuantity)),LimitPrice=amend?orderPrice.Value:selected.LimitPrice,
                OriginalBrokerOrderNumber=selected.OrderNumber,ClientKey=(amend?"amend-":"cancel-")+Guid.NewGuid().ToString("N")};
            var now=DateTime.UtcNow;RealtimeQuote quote=null;
            if(amend&&(!EnsureLiveQuote(selected.Code)||!realtime.TryGetQuote(selected.Code,now,out quote)))throw new AccountUserActionException("선택 종목의 실제 장중 연속 시세 확인을 기다리십시오.");
            QueueOrder(intent,quote,null,false,now);
        }
        void RetrySelectedRejectedSellAt(DateTime now) {
            if(now.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC retry time required.");
            RequireIdle();RequireStorage();
            if(journal==null||ordersGrid.SelectedRows.Count==0)throw new AccountUserActionException("거절된 매도 기록을 선택하십시오.");
            var selected=ordersGrid.SelectedRows[0].Tag as LiveOrderRecord;
            var previous=selected==null?null:journal.Snapshot.Orders.SingleOrDefault(item=>item.Id==selected.Id);
            if(previous==null||previous.Status!=LiveOrderStatus.Rejected||previous.Kind!=LiveOrderKind.Sell
                ||previous.TradingDate!=MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)
                ||!string.IsNullOrEmpty(previous.BrokerOrderNumber)||previous.FilledQuantity!=0)
                throw new AccountUserActionException("접수번호 없는 거절 매도만 재검증할 수 있습니다. 결과 불명·접수된 주문은 증권사 대사가 필요합니다.");
            if(!LiveStarted||!ActiveSession||gateway==null||gateway.Busy||entry!=null||journal.RecoveryRequired)
                throw new AccountUserActionException("현재 계좌의 운영 시작과 앞선 주문 확인이 필요합니다.");
            if(journal.Snapshot.Orders.Count(item=>item.Code==previous.Code&&item.TradingDate==previous.TradingDate&&item.ClientKey.StartsWith("retry-",StringComparison.Ordinal))>=3)
                throw new AccountUserActionException("이 종목의 거절 재시도3회 기록을 먼저 확인하십시오.");
            var held=holdings.SingleOrDefault(item=>item.Code==previous.Code&&item.Quantity>0);RealtimeQuote quote;
            if(held==null||!EnsureLiveQuote(previous.Code)||!realtime.TryGetQuote(previous.Code,now,out quote))
                throw new AccountUserActionException("보유수량과 최신 연속 시세를 확인해야 합니다.");
            if(string.IsNullOrEmpty(EvaluateHoldingSell(held,quote.ToReply(),now)))
                throw new AccountUserActionException("현재 매도 조건 또는 현물 잔고 확인이 완료되지 않았습니다.");
            var intent=new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code=previous.Code,Quantity=checked((int)held.Quantity),LimitPrice=quote.Price,
                ClientKey="retry-"+previous.Id+"-"+Guid.NewGuid().ToString("N")};
            QueueOrder(intent,quote,null,false,now);
            if(entry!=null)Log("선택 거절 매도 재검증 "+previous.Code+" / 이전 기록 보존 / 새 계좌·수량·주문 명세 확인 시작");
        }
        static string Amount(decimal? value){return value.HasValue?value.Value.ToString("N0",CultureInfo.CurrentCulture):"미확인";}
        string HoldingDecision(Holding held) {
            string sourcePending=HoldingSourcePending(held);if(sourcePending!=null)return sourcePending;
            HoldingSellDecision decision;
            if(sellDecisions.TryGetValue(held.Code,out decision)) {
                if(decision.AveragePrice==held.AvgPrice&&decision.Quantity==held.Quantity&&decision.CreditQuantity==held.CreditQty)return decision.Reason;
                sellDecisions.Remove(held.Code);
            }
            if(held.Quantity<=0)return "보유수량 없음";
            if(held.AvgPrice<=0)return "평균단가 확인 대기";
            if(held.CreditQty!=0)return "신용·매도수량 검증 대기";
            return ActiveSession?"최신 시세 확인 대기":"장중 감시 대기";
        }
        void SetHoldingDecision(Holding held,string reason) {
            string previous=HoldingDecision(held);
            sellDecisions[held.Code]=new HoldingSellDecision{AveragePrice=held.AvgPrice,Quantity=held.Quantity,CreditQuantity=held.CreditQty,Reason=reason};
            if(previous!=reason)Log("매도 판정 "+held.Code+" / "+reason);
        }
        string EvaluateHoldingSell(Holding held,IndiReply quote,DateTime utc) {
            string pending=HoldingSourcePendingAt(held,utc);
            if(pending!=null){SetHoldingDecision(held,pending);return null;}
            if(held.Quantity<=0)pending="보유수량 없음";
            else if(held.AvgPrice<=0)pending="평균단가 확인 대기";
            else if(held.CreditQty!=0)pending="신용·매도수량 검증 대기";
            else if(!ActiveSession)pending="장중 감시 대기";
            else if(quote==null||quote.Code!=held.Code)pending="종목·시세 확인 대기";
            else if(!MarketTime.CanUseQuote(quote,utc))pending="최신 체결시각 확인 대기";
            else if(credentials==null||boundKey==null||settingsStore==null)pending="실계좌 확인 대기";
            if(pending!=null){SetHoldingDecision(held,pending);return null;}
            settingsStore.ObserveHigh(boundKey,held,quote,utc);
            string reason=Rules.Exit(new PositionRiskState{Code=held.Code,Qty=checked((int)held.Quantity),Entry=held.AvgPrice,
                High=settingsStore.High(boundKey,held.Code,held.AvgPrice)},quote.Price,configuration.Strategy);
            SetHoldingDecision(held,string.IsNullOrEmpty(reason)?"보유":reason);return reason;
        }
        void RefreshAccount() {
            holdingsGrid.Rows.Clear();
            foreach(var held in holdings) {
                var value=HoldingValuation.From(held);DateTime stamp;
                holdingsGrid.Rows.Add(value.Code,value.Name,value.Quantity,Amount(value.AveragePrice>0?(decimal?)value.AveragePrice:null),Amount(value.CurrentPrice>0?(decimal?)value.CurrentPrice:null),Amount(value.CostBasis),Amount(value.MarketValue),Amount(value.UnrealizedProfit),value.ReturnPercent.HasValue?value.ReturnPercent.Value.ToString("N2",CultureInfo.CurrentCulture):"미확인",priceReceivedUtc.TryGetValue(held.Code,out stamp)?MarketTime.ToKorea(stamp).ToString("MM-dd HH:mm:ss",CultureInfo.InvariantCulture):"미조회",HoldingDecision(held));
            }
            if(holdingsReceivedUtc.HasValue){var total=AccountValuation.From(holdings);portfolio.Text="매입 "+Amount(total.CostBasis)+"원 / 평가 "+Amount(total.MarketValue)+"원 / 평가손익 "+Amount(total.UnrealizedProfit)+"원 (비용 제외)";}
            else portfolio.Text="실계좌 잔고·평가손익: 미조회";
            portfolio.Text+=" / 실현손익(증권사 조회) "+Amount(tradeHoldings==null?null:tradeHoldings.RealizedProfit)+"원"
                +(tradeHoldings!=null&&tradeHoldings.ReceivedUtc.Kind==DateTimeKind.Utc?" [조회 "+MarketTime.ToKorea(tradeHoldings.ReceivedUtc).ToString("MM-dd HH:mm:ss",CultureInfo.InvariantCulture)+"]":"");
            cashLabel.Text=cash==null?"  현금100% 주문가능금액: 미조회":"  예수금 "+cash.DepositCash.ToString("N0",CultureInfo.CurrentCulture)+"원 / 현금100% 주문가능금액 "+cash.Cash100Orderable.ToString("N0",CultureInfo.CurrentCulture)+"원";
        }
        void RefreshOrders() {
            ordersGrid.Rows.Clear();var shown=new HashSet<string>(StringComparer.Ordinal);
            if(brokerOrders!=null)foreach(var order in brokerOrders.Orders) {
                int index=ordersGrid.Rows.Add(order.OrderNumber,order.Code,order.SideCode=="1"?"매도":order.SideCode=="2"?"매수":"미확인",order.Quantity,order.FilledQuantity,order.OpenQuantity,order.LimitPrice,order.AverageFillPrice,order.HasRejection?"증권사 거부":"증권사 누적 조회");
                ordersGrid.Rows[index].Tag=order;shown.Add(order.OrderNumber);
            }
            if(journal!=null)foreach(var order in journal.Snapshot.Orders.Where(x=>string.IsNullOrEmpty(x.BrokerOrderNumber)||!shown.Contains(x.BrokerOrderNumber))) {
                string number=order.Status==LiveOrderStatus.NotSent?"미전송":!string.IsNullOrEmpty(order.BrokerOrderNumber)?order.BrokerOrderNumber:order.Status==LiveOrderStatus.Rejected?"접수 거절":"접수 미확인";
                object open=order.Status==LiveOrderStatus.Rejected||order.Status==LiveOrderStatus.NotSent?(object)"해당 없음":order.Quantity-order.FilledQuantity-order.CancelledQuantity;
                string reason=order.ResponseEvidence!=null?order.ResponseEvidence.SafeDiagnostic:
                    order.Status==LiveOrderStatus.Rejected?"이전 버전: 상세 응답 미기록":
                    order.Status==LiveOrderStatus.NotSent?"전송 전 조건·주문 정의 확인 중 중단. 진단 로그의 주문 정의 사유를 확인하십시오.":"상세 응답 미기록";
                int index=ordersGrid.Rows.Add(number,order.Code,order.Kind,order.Quantity,order.FilledQuantity,open,order.LimitPrice,"미확인",LocalOrderStatus(order.Status),reason);
                ordersGrid.Rows[index].Tag=order;ordersGrid.Rows[index].Cells[9].ToolTipText=reason;
            }
            UpdateSelectedOrderResponse();
        }
        void UpdateSelectedOrderResponse() {
            orderResponseDetails.Text=ordersGrid.SelectedRows.Count==0?"주문 행을 선택하면 응답 사유를 표시합니다.":
                Convert.ToString(ordersGrid.SelectedRows[0].Cells[9].Value,CultureInfo.InvariantCulture);
        }
        static string LocalOrderStatus(LiveOrderStatus status) {
            switch(status) {
                case LiveOrderStatus.Prepared:return "전송 준비";
                case LiveOrderStatus.Dispatching:return "전송 중";
                case LiveOrderStatus.AwaitingAcknowledgement:return "접수 응답 대기";
                case LiveOrderStatus.Working:return "접수됨 · 체결 확인 중";
                case LiveOrderStatus.PartiallyFilled:return "부분 체결";
                case LiveOrderStatus.AwaitingConfirmation:return "정정·취소 확인 대기";
                case LiveOrderStatus.Filled:return "체결 확인";
                case LiveOrderStatus.Cancelled:return "취소 확인";
                case LiveOrderStatus.Rejected:return "거절";
                case LiveOrderStatus.Replaced:return "정정 확인";
                case LiveOrderStatus.NotSent:return "미전송";
                default:return "결과 불명";
            }
        }
        void UpdateClosedMonitoringStatus() {
            if(!monitoring)return;
            var open=new Dictionary<string,decimal>(StringComparer.Ordinal);
            if(brokerOrders!=null)foreach(var order in brokerOrders.Orders.Where(x=>x.OpenQuantity>0))open[order.OrderNumber.TrimStart('0')]=order.OpenQuantity;
            if(journal!=null)foreach(var order in journal.Snapshot.Orders.Where(x=>!string.IsNullOrEmpty(x.BrokerOrderNumber))) {
                if(brokerOrders!=null&&order.UpdatedUtc<=brokerOrders.ReceivedUtc)continue;
                string key=order.BrokerOrderNumber.TrimStart('0');
                if((order.Status==LiveOrderStatus.Working||order.Status==LiveOrderStatus.PartiallyFilled)&&order.Kind!=LiveOrderKind.Cancel)
                    open[key]=Math.Max(0,order.Quantity-order.FilledQuantity-order.CancelledQuantity);
                else if(!LiveOrderJournal.IsUnresolved(order))open.Remove(key);
            }
            state.Text="신규주문 종료 · 증권사 미체결 "+open.Count(x=>x.Value>0)+"건 감시 중 / 다음 실주문은 시작 버튼 필요";
        }
        string ReportDirectory {get{string key=boundKey??lastReportKey;return key==null?Path.Combine(Program.DataDir,"reports"):Path.Combine(Program.DataDir,"reports",key);}}
        bool HasReportSnapshot {get{return holdingsReceivedUtc.HasValue||tradeHoldings!=null||cash!=null||brokerOrders!=null;}}
        void SaveReportIfDue(DateTime now) {
            if(!monitoring||boundKey==null||!HasReportSnapshot||reportWriteFault||reportSaving||now<nextReportUtc)return;
            WriteReport(MarketTime.ToKorea(now).Date);
        }
        string WriteReport(DateTime koreaDate) {
            if(boundKey==null||!HasReportSnapshot||reportSaving)return null;
            reportSaving=true;
            try {
                var writtenUtc=DateTime.UtcNow;
                var path=SessionReport.Write(ReportDirectory,koreaDate,writtenUtc,holdings,holdingsReceivedUtc,cash,brokerOrders,
                    journal==null?(IEnumerable<LiveOrderRecord>)new LiveOrderRecord[0]:journal.Snapshot.Orders,authorization.Active?"실주문 운영 시작됨":"실주문 미시작",priceReceivedUtc,tradeHoldings);
                lastSavedReportPath=path;lastReportSavedUtc=writtenUtc;nextReportUtc=writtenUtc.AddMinutes(5);
                initialReportComplete=holdingsReceivedUtc.HasValue&&cash!=null&&brokerOrders!=null;
                reportStatus.Text="보고서 저장: "+MarketTime.ToKorea(lastReportSavedUtc.Value).ToString("MM-dd HH:mm:ss",CultureInfo.InvariantCulture)+" / "+Path.GetFileName(lastSavedReportPath);
                reportStatus.ForeColor=Color.DarkGreen;
                Log("보고서 저장 완료: "+path);return path;
            }catch(Exception ex) {
                reportWriteFault=true;storageFault=true;
                var fileFailure=ex as ReportFileWriteException;
                string reason=fileFailure==null?ex.GetType().Name:fileFailure.FailureCode=="AccessDenied"?"폴더·파일 쓰기 권한 확인":
                    fileFailure.FailureCode=="SharingViolation"?"엑셀 등에서 열린 CSV를 닫으십시오":"쓰기 실패: 파일 열림·디스크 상태 확인";
                reportStatus.Text="보고서 저장 실패 ("+reason+")"
                    +(lastReportSavedUtc.HasValue?" / 마지막 성공 "+MarketTime.ToKorea(lastReportSavedUtc.Value).ToString("MM-dd HH:mm:ss",CultureInfo.InvariantCulture):" / 저장된 파일 없음")+" / 진단 로그에서 경로 확인";
                reportStatus.ForeColor=Color.DarkRed;
                Log("보고서 저장 실패: "+ex.GetType().Name+" / "+ReportDirectory+" / 기존 파일은 보존합니다.");
                throw;
            }finally{reportSaving=false;}
        }
        string PrepareReports() {
            if(boundKey!=null&&HasReportSnapshot)WriteReport(MarketTime.NowKorea.Date);
            string path=ReportDirectory;
            if(boundKey==null&&lastReportKey==null) {
                string previous=SessionReport.FindLatestReport(Path.Combine(Program.DataDir,"reports"));
                if(previous!=null){path=Path.GetDirectoryName(previous);reportStatus.Text="이전 보고서: "+Path.GetFileName(previous)+" / 현재 계좌 미조회";}
                else reportStatus.Text="저장된 보고서 없음 / 실계좌 조회·감시를 먼저 실행하십시오.";
            } else if(boundKey!=null&&!HasReportSnapshot)reportStatus.Text="보고서 저장 대기: 잔고·현금·주문 응답을 먼저 수신해야 합니다.";
            return path;
        }
        void ExportIndiDefinitions() {
            try {
                var type=Type.GetTypeFromProgID("GIEXPERTCONTROL.GiExpertControlCtrl.1",false);
                if(type==null)throw new AccountUserActionException("등록된 Indi 설치 경로를 찾을 수 없습니다.");
                var starter=IndiInstallation.FindStarter(type.GUID);
                if(starter==null)throw new AccountUserActionException("등록된 Indi의 조회 정의 폴더를 찾을 수 없습니다.");
                var file=IndiDefinitionExport.Save(starter,Path.Combine(Program.DataDir,"support"));
                state.Text="조회 정의 진단파일 저장: "+Path.GetFileName(file)+" / 이 XML 파일을 첨부해 주십시오.";
                Log("정적 조회 정의 진단파일 생성: "+Path.GetFileName(file)+" / 계좌 조회·주문 전송 없음");
                if(Environment.OSVersion.Platform==PlatformID.Win32NT)
                    Process.Start(new ProcessStartInfo{FileName="explorer.exe",Arguments="/select,\""+file+"\"",UseShellExecute=true});
            }catch(AccountUserActionException){throw;}
            catch(Exception ex){throw new AccountUserActionException("조회 정의 내보내기 실패: "+ex.GetType().Name+". 파일을 변경하지 않고 중단했습니다.");}
        }
        void OpenReports(){
            string path=PrepareReports();
            try{Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo{FileName=path,UseShellExecute=true});}
            catch(Exception ex){state.Text="보고서 폴더 열기 실패 ("+ex.GetType().Name+"): "+path;Log("보고서 폴더 열기 실패: "+ex.GetType().Name+" / "+path);}
        }
        int BarCount(){return Math.Max(45,Math.Max(configuration.Strategy.SlowPeriod,configuration.Strategy.BreakoutPeriod)+2);}
        static bool IsRatio(string key){return key=="AllocationFraction"||key=="FeeRate";}
        void LoadSettings(){foreach(var pair in options){var field=typeof(StrategySettings).GetField(pair.Key);decimal value=Convert.ToDecimal(field.GetValue(configuration.Strategy),CultureInfo.InvariantCulture);if(IsRatio(pair.Key))value*=100;pair.Value.Value=Math.Min(pair.Value.Maximum,Math.Max(pair.Value.Minimum,value));}budget.Value=configuration.BudgetLimit;}
        void SaveSettings() {
            RequireStorage();Halt();var strategy=configuration.Strategy.Copy();
            foreach(var pair in options){var field=typeof(StrategySettings).GetField(pair.Key);decimal value=pair.Value.Value;if(IsRatio(pair.Key))value/=100;field.SetValue(strategy,Convert.ChangeType(value,field.FieldType,CultureInfo.InvariantCulture));}
            strategy.Validate();settingsStore.Update(strategy,budget.Value);configuration=settingsStore.Snapshot;Log("전략·운용한도 저장. 실계좌 조회 후 부재중 운영을 시작하십시오.");
        }
    }
}
