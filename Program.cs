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
        internal const string Version="2.0.0";
        [STAThread]
        static int Main(string[] args) {
            if(args.Contains("--selftest")) {
                int exit=0;string report;
                try{report="PASS: "+SelfTests.Run()+" assertions; isolated test responses only.";}
                catch(Exception ex){exit=1;report="FAIL: "+ex;}
                report+=Environment.NewLine+"Runtime="+Environment.Version+"; pointerBytes="+IntPtr.Size
                    +Environment.NewLine+"Actual Indi server and money orders: NOT RUN. Production order gate: BLOCKED.";
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
        sealed class EntryRequest {public Candidate Candidate;public IndiReply Quote;public DateTime QuoteUtc;public AccountOrderCapacity Capacity;public bool CapacityRequested,CashRequested;}
        readonly Panel nativeArea=new Panel();
        readonly TextBox logs=new TextBox(),account=new TextBox(),password=new TextBox(),code=new TextBox{Text="005930",Width=85};
        readonly NumericUpDown budget=Number(0,1000000000,0,0),orderQuantity=Number(1,1000000,1,0),orderPrice=Number(1,10000000,1,0);
        readonly Label state=new Label{AutoSize=true},portfolio=new Label{AutoSize=true},cashLabel=new Label{AutoSize=true},orderGate=new Label{AutoSize=true,ForeColor=Color.DarkRed};
        readonly DataGridView holdingsGrid=Grid("코드","종목명","수량","평균단가","현재가","매입금액","평가금액","평가손익","수익률(%)","시세 조회시각"),
            ordersGrid=Grid("주문번호","종목","구분","주문수량","누적체결","미체결","주문단가","체결평균가","확인상태"),
            candidatesGrid=Grid("코드","종목명","신호 기준가","점수","조건","주문상태");
        readonly Dictionary<string,NumericUpDown> options=new Dictionary<string,NumericUpDown>();
        readonly Timer scheduler=new Timer{Interval=1000};
        readonly UnattendedSession session=new UnattendedSession();
        readonly UnattendedAwakeLease awake=new UnattendedAwakeLease();
        readonly List<Stock> symbols=new List<Stock>();
        readonly List<Holding> holdings=new List<Holding>();
        readonly Queue<Stock> scans=new Queue<Stock>();
        readonly Queue<Candidate> buyQuotes=new Queue<Candidate>();
        readonly Queue<string> accountReads=new Queue<string>();
        readonly Dictionary<string,DateTime> quoteDue=new Dictionary<string,DateTime>(),priceReceivedUtc=new Dictionary<string,DateTime>();
        readonly HashSet<string> blockedSignals=new HashSet<string>(StringComparer.Ordinal);
        IndiApi api;
        AccountAppSettingsStore settingsStore;
        AccountAppSettings configuration=new AccountAppSettings();
        AccountCredentialSession credentials;
        AccountCashSnapshot cash;
        BrokerOrdersSnapshot brokerOrders;
        LiveOrderJournal journal;
        LiveOrderGateway gateway;
        Candidate requestedBuy;
        EntryRequest entry;
        DateTime? holdingsReceivedUtc;
        bool orderAcknowledgementPending;
        DateTime nextAccountReadUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc),nextReportUtc=DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc);
        bool scanning,requestedScan,monitoring,firstQueryQueued,pendingSessionScan,storageFault,closingForm;
        int scanned,holdingRequests,connectionEpoch;
        string boundKey,lastReportKey;

        public MainForm() {
            Text="Shinhan Native Trader "+Program.Version+" | 실계좌 | 실주문 검증 잠금";
            Size=new Size(1370,860);MinimumSize=new Size(1120,730);Font=new Font("Malgun Gothic",9);
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6,Padding=new Padding(10)};
            foreach(int height in new[]{35,100,30,40})root.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,52));Controls.Add(root);
            orderGate.Text=LiveOrderSafety.BlockReason;root.Controls.Add(orderGate,0,0);
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=true};root.Controls.Add(buttons,0,1);
            AddButton(buttons,"Indi 연결·종목 조회",Connect);buttons.Controls.Add(code);
            AddButton(buttons,"현재가 조회",()=>{RequireIdle();requestedBuy=null;api.SnapshotQuote(ValidCode());});
            AddButton(buttons,"일봉 분석",()=>{RequireIdle();requestedScan=false;api.Bars(ValidCode(),BarCount());});
            AddButton(buttons,"전체 스캔",StartScan);
            AddButton(buttons,"부재중 운영 시작",Arm);
            AddButton(buttons,"운영 중단",Halt);
            buttons.Controls.Add(new Label{Text="계좌",AutoSize=true});account.Width=125;buttons.Controls.Add(account);
            buttons.Controls.Add(new Label{Text="계좌암호",AutoSize=true});password.Width=95;password.UseSystemPasswordChar=true;buttons.Controls.Add(password);
            AddButton(buttons,"실계좌 조회·감시",AttachAccount);
            AddButton(buttons,"보고서 폴더",OpenReports);
            state.Text="Indi 연결 후 계좌를 조회하고 부재중 운영을 시작하십시오.";root.Controls.Add(state,0,2);
            var summary=new FlowLayoutPanel{Dock=DockStyle.Fill};summary.Controls.Add(portfolio);summary.Controls.Add(cashLabel);root.Controls.Add(summary,0,3);
            var tabs=new TabControl{Dock=DockStyle.Fill};root.Controls.Add(tabs,0,4);
            AddTab(tabs,"실계좌 잔고·손익",holdingsGrid);
            var ordersPanel=new TableLayoutPanel{Dock=DockStyle.Fill,RowCount=2,ColumnCount=1};ordersPanel.RowStyles.Add(new RowStyle(SizeType.Absolute,38));ordersPanel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var orderButtons=new FlowLayoutPanel{Dock=DockStyle.Fill};orderButtons.Controls.Add(new Label{Text="선택 주문 수량",AutoSize=true});orderButtons.Controls.Add(orderQuantity);orderButtons.Controls.Add(new Label{Text="정정 단가",AutoSize=true});orderButtons.Controls.Add(orderPrice);
            AddButton(orderButtons,"선택 주문 취소",()=>ChangeSelectedOrder(false));AddButton(orderButtons,"선택 주문 정정",()=>ChangeSelectedOrder(true));
            ordersPanel.Controls.Add(orderButtons,0,0);ordersPanel.Controls.Add(ordersGrid,0,1);AddTab(tabs,"주문·체결",ordersPanel);AddTab(tabs,"발굴 후보",candidatesGrid);
            var settings=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false};AddTab(tabs,"전략·운용한도",settings);
            AddSetting(settings,"실계좌 운용한도 (원)",budget);
            Setting(settings,"FastPeriod","단기 이동평균 기간",1,199,5);Setting(settings,"SlowPeriod","장기 이동평균 기간",2,200,20);
            Setting(settings,"BreakoutPeriod","돌파 고점 기간",1,200,20);Setting(settings,"VolumeMultiplier","평균 거래량 배수",1,20,1.3m,2);
            Setting(settings,"MinPrice","최저 종목 가격",1,10000000,2000);Setting(settings,"MaxPrice","최고 종목 가격",1,10000000,400000);
            Setting(settings,"AllocationFraction","종목당 가용금액 비율 (%)",1,100,20,2);Setting(settings,"MaxPositions","최대 보유 종목 수",1,100,3);
            Setting(settings,"StopLossPercent","손절 (%)",0.1m,90,6,2);Setting(settings,"TakeProfitPercent","익절 (%)",0.1m,500,12,2);
            Setting(settings,"TrailingActivationPercent","추적손절 활성 수익률 (%)",0.1m,500,4,2);Setting(settings,"TrailingDrawdownPercent","고점 대비 추적손절 (%)",0.1m,90,7,2);
            Setting(settings,"FeeRate","주문금액 비용 여유율 (%)",0,10,0.3m,3);AddButton(settings,"전략·운용한도 저장",SaveSettings);
            settings.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(1000,0),Text="운용한도는 실제 예수금이 아닙니다. 증권사의 현금100% 주문가능금액과 종목별 주문가능수량을 함께 검증합니다.\n부재중 운영: 평일 09:00~15:20, 전체 스캔 완료 후20분 간격 재검색. 휴장일 달력은 미연동입니다.\n평가손익은 실제 잔고·평균단가·조회 가격으로 계산하며 수수료·세금은 제외합니다. 실현손익은 임의 추정하지 않습니다.\n설정은 계좌번호·암호 없이 저장됩니다. 부재중 조회용 암호는 종료 또는 운영 중단 전까지 메모리에만 보관합니다."});
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
            Halt();credentials=captured;boundKey=captured.Key;lastReportKey=boundKey;account.ReadOnly=true;ClearAccountData();
            try{journal=new LiveOrderJournal(Path.Combine(Program.DataDir,"live-orders-"+boundKey+".xml"));journal.BindAccount(credentials.Account);gateway=new LiveOrderGateway(journal,api.CreateLiveOrderWire());}
            catch{ReleaseAccount();throw;}
            monitoring=true;nextAccountReadUtc=DateTime.UtcNow;nextReportUtc=DateTime.UtcNow.AddMinutes(5);QueueAccountRefresh();
            state.Text="실계좌 잔고·주문가능금액·주문 조회 중";Log("실계좌 조회 세션 시작. 암호는 메모리에서만 사용합니다.");
        }
        void QueueAccountRefresh(){if(credentials==null||accountReads.Count!=0)return;accountReads.Enqueue("HOLDINGS");accountReads.Enqueue("CASH");accountReads.Enqueue("ORDERS");nextAccountReadUtc=DateTime.UtcNow.AddSeconds(60);}
        void Arm() {
            RequireConnected();RequireStorage();
            if(credentials==null){if(api.Busy){state.Text="조회 응답 완료 후 실계좌를 연결하고 부재중 운영을 시작하십시오.";return;}AttachAccount();}
            if(symbols.Count==0)throw new AccountUserActionException("종목 조회를 먼저 실행하십시오.");
            session.Arm(DateTime.UtcNow);awake.Start();monitoring=true;
            state.Text="부재중 운영 예약: "+(session.NextScanUtc.HasValue?MarketTime.ToKorea(session.NextScanUtc.Value).ToString("MM-dd HH:mm",CultureInfo.InvariantCulture):"장중")+" / 실주문 검증 잠금";
            Log("부재중 운영 시작. 장중 자동 검색·잔고 감시·보고서를 기록합니다.");
        }
        void StartScan(){RequireIdle();RequireStorage();StartScanCore();}
        void StartScanCore() {
            if(symbols.Count==0)throw new InvalidOperationException("종목 조회가 필요합니다.");
            if(scanning)return;scans.Clear();buyQuotes.Clear();candidatesGrid.Rows.Clear();scanned=0;
            foreach(var symbol in symbols.OrderBy(x=>x.Code))scans.Enqueue(symbol);scanning=true;Log("전체 종목 검색 시작.");
        }
        void ClearWork(){scanning=false;requestedScan=false;firstQueryQueued=false;pendingSessionScan=false;connectionEpoch++;scans.Clear();buyQuotes.Clear();requestedBuy=null;entry=null;accountReads.Clear();}
        public void Halt() {
            session.Disarm();awake.Dispose();monitoring=false;ClearWork();
            if(api!=null)api.CancelPending();
            CloseGateway();
            if(boundKey!=null)try{WriteReport(MarketTime.NowKorea.Date);}catch(Exception ex){Log("보고서 저장 실패: "+ex.GetType().Name);storageFault=true;}
            ReleaseAccount();password.Clear();state.Text=closingForm?"종료 중":"운영 중단 / 최근 실계좌 조회값 표시 / 실주문 잠금";
        }
        void CloseGateway(){if(gateway!=null){gateway.Dispose();gateway=null;}orderAcknowledgementPending=false;}
        void ReleaseAccount(){CloseGateway();if(journal!=null){journal.Dispose();journal=null;}if(credentials!=null){credentials.Dispose();credentials=null;}boundKey=null;account.ReadOnly=false;}
        void ClearAccountData(){holdings.Clear();priceReceivedUtc.Clear();quoteDue.Clear();cash=null;brokerOrders=null;holdingsReceivedUtc=null;blockedSignals.Clear();RefreshAccount();RefreshOrders();}
        void CloseSession() {
            DateTime day=session.SessionDateKorea??MarketTime.NowKorea.Date;string reason=session.CloseReason;
            ClearWork();if(api!=null)api.CancelPending();
            CloseGateway();
            WriteReport(day);session.SessionClosed();Log("장중 운영 종료·보고서 저장: "+day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)+" / "+reason);
            nextAccountReadUtc=DateTime.UtcNow;state.Text="장중 운영 종료 / 실계좌 조회 감시 계속 / 다음 평일 예약";
        }
        void Schedule() {
            var now=DateTime.UtcNow;
            if(gateway!=null) {
                gateway.Timeout(now);
                if(orderAcknowledgementPending&&!gateway.Busy) {
                    orderAcknowledgementPending=false;RefreshOrders();
                    if(journal!=null&&journal.RecoveryRequired){Halt();Log("실주문 접수 결과 불명: 증권사 대사 전까지 운영을 중단했습니다.");return;}
                }
            }
            var action=session.Tick(now,(api!=null&&api.Busy)||scanning||pendingSessionScan||entry!=null||accountReads.Count>0);
            if(action==UnattendedAction.CloseSession){CloseSession();return;}
            if(action==UnattendedAction.StartScan) {
                pendingSessionScan=true;
                if(credentials!=null&&gateway==null&&journal!=null)gateway=new LiveOrderGateway(journal,api.CreateLiveOrderWire());
                if(!api.Stocks()&&pendingSessionScan){Halt();state.Text="장중 종목 갱신 실패로 운영 중단";}return;
            }
            if(api==null||!api.Connected||api.Busy||firstQueryQueued)return;
            if(credentials!=null&&entry!=null) {
                if(!entry.CapacityRequested){entry.CapacityRequested=true;credentials.Use((number,secret)=>api.Capacity(number,secret,entry.Candidate.Code,entry.Quote.Price));return;}
                if(entry.Capacity!=null&&!entry.CashRequested){entry.CashRequested=true;credentials.Use((number,secret)=>api.Cash(number,secret));return;}
            }
            if(monitoring&&credentials!=null&&now>=nextAccountReadUtc)QueueAccountRefresh();
            if(accountReads.Count>0&&credentials!=null) {
                string kind=accountReads.Dequeue();
                credentials.Use((number,secret)=>kind=="HOLDINGS"?api.Holdings(number,secret):kind=="CASH"?api.Cash(number,secret):api.Orders(number,secret,MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)));return;
            }
            if(monitoring&&credentials!=null&&now>=nextReportUtc){WriteReport(MarketTime.ToKorea(now).Date);nextReportUtc=now.AddMinutes(5);}
            var due=monitoring?holdings.Where(x=>x.Quantity>0&&(!quoteDue.ContainsKey(x.Code)||quoteDue[x.Code]<=now)).OrderBy(x=>quoteDue.ContainsKey(x.Code)?quoteDue[x.Code]:DateTime.MinValue).FirstOrDefault():null;
            bool research=buyQuotes.Count>0||(scanning&&scans.Count>0);
            if(due!=null&&(holdingRequests<3||!research)) {
                holdingRequests=Math.Min(3,holdingRequests+1);quoteDue[due.Code]=now.AddSeconds(15);requestedBuy=null;
                if(ActiveSession)api.Quote(due.Code);else api.SnapshotQuote(due.Code);return;
            }
            holdingRequests=0;
            if(entry==null&&buyQuotes.Count>0){requestedBuy=buyQuotes.Dequeue();api.Quote(requestedBuy.Code);return;}
            if(scanning&&scans.Count>0){requestedScan=true;api.Bars(scans.Dequeue().Code,BarCount());return;}
            if(scanning&&entry==null&&buyQuotes.Count==0){scanning=false;session.ScanCompleted(now);state.Text="전체 검색 완료 / 후보 "+candidatesGrid.Rows.Count+"개 / 실계좌 감시 계속";Log("전체 검색 완료. 다음 검색은 완료 후20분 간격입니다.");if(boundKey!=null)WriteReport(MarketTime.ToKorea(now).Date);}
        }
        void Reply(IndiReply reply) {
            var now=DateTime.UtcNow;
            if(reply.Kind=="STOCKS"){symbols.Clear();symbols.AddRange(reply.Stocks);state.Text="종목 응답 "+reply.Count+"건 / 일반주식 "+symbols.Count+"개";if(pendingSessionScan&&ActiveSession){pendingSessionScan=false;QueueAccountRefresh();StartScanCore();}return;}
            if(reply.Kind=="HOLDINGS") {
                holdings.Clear();holdings.AddRange(reply.Holdings);holdingsReceivedUtc=now;
                foreach(var holding in holdings)if(holding.Price>0)priceReceivedUtc[holding.Code]=now;
                if(settingsStore!=null&&boundKey!=null){settingsStore.RefreshPositions(boundKey,holdings);configuration=settingsStore.Snapshot;}
                RefreshAccount();state.Text="실계좌 잔고 "+holdings.Count+"종목 조회 / 평가손익은 비용 제외";return;
            }
            if(reply.Kind=="CASH"){cash=reply.Cash;RefreshAccount();if(entry!=null&&entry.Capacity!=null&&entry.CashRequested)SubmitEntry();return;}
            if(reply.Kind=="ORDERS"){brokerOrders=reply.BrokerOrders;RefreshOrders();return;}
            if(reply.Kind=="CAPACITY"){if(entry!=null&&reply.Capacity!=null&&reply.Capacity.Code==entry.Candidate.Code&&reply.Capacity.LimitPrice==entry.Quote.Price)entry.Capacity=reply.Capacity;else entry=null;return;}
            if(reply.Kind=="BARS") {
                bool fromScan=requestedScan;requestedScan=false;var symbol=symbols.FirstOrDefault(x=>x.Code==reply.Code);
                var candidate=Rules.Entry(reply.Code,symbol==null?reply.Code:symbol.Name,reply.Bars,configuration.Strategy);scanned++;
                if(candidate!=null){candidatesGrid.Rows.Add(candidate.Code,candidate.Name,candidate.Price,candidate.Score,candidate.Reason,"실주문 검증 잠금");if(fromScan&&ActiveSession)buyQuotes.Enqueue(candidate);}
                state.Text="일봉 분석 "+scanned+" / 대기 "+scans.Count+" / 후보 "+candidatesGrid.Rows.Count;return;
            }
            if(reply.Kind!="QUOTE")return;
            var intent=requestedBuy;requestedBuy=null;var held=holdings.SingleOrDefault(x=>x.Code==reply.Code);
            if(held!=null){held.Price=reply.Price;priceReceivedUtc[held.Code]=now;RefreshAccount();}
            state.Text="현재가 "+reply.Code+" "+reply.Price.ToString("N0",CultureInfo.CurrentCulture)+"원 / "+(reply.HasTradeClock?"체결시각 "+reply.Clock:"거래시각 미확인 · 주문 사용 제외");
            Log("현재가 "+reply.Code+" "+reply.Price.ToString(CultureInfo.InvariantCulture)+" / 거래시각="+(reply.HasTradeClock?reply.Clock:"미확인"));
            if(!ActiveSession||credentials==null||!MarketTime.CanUseQuote(reply,now))return;
            if(held!=null&&held.Quantity>0&&held.AvgPrice>0&&held.CreditQty==0) {
                settingsStore.ObserveHigh(boundKey,held,reply,now);
                string reason=Rules.Exit(new PositionRiskState{Code=held.Code,Qty=checked((int)held.Quantity),Entry=held.AvgPrice,High=settingsStore.High(boundKey,held.Code,held.AvgPrice)},reply.Price,configuration.Strategy);
                if(!string.IsNullOrEmpty(reason))TryOrder(new LiveOrderIntent{Kind=LiveOrderKind.Sell,Code=held.Code,Quantity=checked((int)held.Quantity),LimitPrice=reply.Price,ClientKey="exit-"+MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-"+held.Code},reply,null,now);
            } else if(intent!=null&&intent.Code==reply.Code&&entry==null&&held==null&&ProjectedPositionCount()<configuration.Strategy.MaxPositions
                &&intent.SignalDate==MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture)&&reply.Price>=configuration.Strategy.MinPrice&&reply.Price<=configuration.Strategy.MaxPrice
                &&reply.Price>=intent.Price*0.97m&&reply.Price<=intent.Price*1.03m)entry=new EntryRequest{Candidate=intent,Quote=reply,QuoteUtc=now};
        }
        void SubmitEntry() {
            var pending=entry;entry=null;if(pending==null||cash==null||credentials==null||!ActiveSession||!MarketTime.CanUseQuote(pending.Quote,DateTime.UtcNow))return;
            var totals=AccountValuation.From(holdings);if(!totals.CostBasis.HasValue)return;
            decimal remaining=Math.Max(0m,configuration.BudgetLimit-totals.CostBasis.Value-(journal==null?0m:journal.ReservedBuyAmount));
            int qty=Rules.OrderQuantity(Math.Min(cash.NonNegativeCash100,Math.Min(remaining,pending.Capacity.Amount)),pending.Quote.Price,configuration.Strategy.AllocationFraction,configuration.Strategy.FeeRate);
            qty=Math.Min(qty,(int)Math.Min(int.MaxValue,pending.Capacity.Quantity));if(qty<=0){Log("신규 주문 제외: 실제 가용금액 또는 운용한도 부족");return;}
            TryOrder(new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code=pending.Candidate.Code,Quantity=qty,LimitPrice=pending.Quote.Price,
                ClientKey="entry-"+MarketTime.NowKorea.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+"-"+pending.Candidate.Code},pending.Quote,pending.Capacity,pending.QuoteUtc);
        }
        void TryOrder(LiveOrderIntent intent,IndiReply quote,AccountOrderCapacity capacity,DateTime quoteUtc) {
            if(credentials==null||gateway==null||journal==null)return;
            var utc=DateTime.UtcNow;var valuation=AccountValuation.From(holdings);
            // The gateway subtracts the journal's outstanding buy reservations
            // again at its own boundary. Supply the limit after actual holdings
            // cost here, without double-subtracting those journal reservations.
            var checks=new LiveOrderPreflight{AccountAuthorized=true,OperatingLimit=valuation.CostBasis.HasValue?Math.Max(0m,configuration.BudgetLimit-valuation.CostBasis.Value):0m,
                Cash100Orderable=cash==null?0:cash.Cash100Orderable,CapacityAmount=capacity==null?0:capacity.Amount,CapacityQuantity=capacity==null?0:checked((int)capacity.Quantity),
                SnapshotUtc=cash==null?DateTime.SpecifyKind(DateTime.MinValue,DateTimeKind.Utc):cash.ReceivedUtc,QuoteUtc=quoteUtc,
                QuoteVerified=quote!=null&&MarketTime.CanUseQuote(quote,utc),OrderableAmountVerified=cash!=null&&capacity!=null&&cash.OrderSemanticsVerified&&capacity.OrderSemanticsVerified,
                OrdersReconciled=brokerOrders!=null&&brokerOrders.IsComplete,FillsReconciled=brokerOrders!=null&&brokerOrders.HasExecutionIds,
                RecoveryVerified=brokerOrders!=null&&brokerOrders.IsComplete&&!journal.RecoveryRequired,PriceRuleVerified=false,SellableQuantityVerified=false};
            if(!LiveOrderSafety.ProductionContractVerified){LogBlocked(intent.Kind,intent.Code);return;}
            var sent=credentials.Use((number,secret)=>gateway.Submit(intent,new LiveAccountCredentials(number,secret),checks,utc));
            orderAcknowledgementPending=gateway.Busy;
            Log("실주문 요청 "+sent.Code+" / "+sent.Kind+" / 접수·체결 확인 대기");RefreshOrders();
        }
        int ProjectedPositionCount() {
            var occupied=new HashSet<string>(holdings.Where(x=>x.Quantity>0).Select(x=>x.Code),StringComparer.Ordinal);
            if(journal!=null)foreach(var order in journal.Snapshot.Orders.Where(x=>x.TradeKind==LiveOrderKind.Buy&&LiveOrderJournal.IsUnresolved(x)))occupied.Add(order.Code);
            if(brokerOrders!=null)foreach(var order in brokerOrders.Orders.Where(x=>x.SideCode=="2"&&x.OpenQuantity>0))occupied.Add(order.Code);
            return occupied.Count;
        }
        void LogBlocked(LiveOrderKind kind,string symbol){string key=MarketTime.NowKorea.ToString("yyyyMMdd",CultureInfo.InvariantCulture)+":"+kind+":"+symbol;if(blockedSignals.Add(key))Log("주문 차단 "+symbol+" / "+kind+" / "+LiveOrderSafety.BlockReason);}
        void ChangeSelectedOrder(bool amend) {
            RequireIdle();if(credentials==null||ordersGrid.SelectedRows.Count==0)throw new AccountUserActionException("실계좌와 증권사 주문을 선택하십시오.");
            var selected=ordersGrid.SelectedRows[0].Tag as BrokerOrderRow;if(selected==null||selected.OpenQuantity<=0)throw new AccountUserActionException("미체결 증권사 주문을 선택하십시오.");
            var intent=new LiveOrderIntent{Kind=amend?LiveOrderKind.Amend:LiveOrderKind.Cancel,Code=selected.Code,
                Quantity=checked((int)Math.Min(orderQuantity.Value,selected.OpenQuantity)),LimitPrice=amend?orderPrice.Value:selected.LimitPrice,
                OriginalBrokerOrderNumber=selected.OrderNumber,ClientKey=(amend?"amend-":"cancel-")+Guid.NewGuid().ToString("N")};
            TryOrder(intent,null,null,DateTime.UtcNow);
        }
        static string Amount(decimal? value){return value.HasValue?value.Value.ToString("N0",CultureInfo.CurrentCulture):"미확인";}
        void RefreshAccount() {
            holdingsGrid.Rows.Clear();
            foreach(var held in holdings) {
                var value=HoldingValuation.From(held);DateTime stamp;
                holdingsGrid.Rows.Add(value.Code,value.Name,value.Quantity,Amount(value.AveragePrice>0?(decimal?)value.AveragePrice:null),Amount(value.CurrentPrice>0?(decimal?)value.CurrentPrice:null),Amount(value.CostBasis),Amount(value.MarketValue),Amount(value.UnrealizedProfit),value.ReturnPercent.HasValue?value.ReturnPercent.Value.ToString("N2",CultureInfo.CurrentCulture):"미확인",priceReceivedUtc.TryGetValue(held.Code,out stamp)?MarketTime.ToKorea(stamp).ToString("MM-dd HH:mm:ss",CultureInfo.InvariantCulture):"미조회");
            }
            if(holdingsReceivedUtc.HasValue){var total=AccountValuation.From(holdings);portfolio.Text="매입 "+Amount(total.CostBasis)+"원 / 평가 "+Amount(total.MarketValue)+"원 / 평가손익 "+Amount(total.UnrealizedProfit)+"원 (비용 제외)";}
            else portfolio.Text="실계좌 잔고·평가손익: 미조회";
            cashLabel.Text=cash==null?"  현금100% 주문가능금액: 미조회":"  예수금 "+cash.DepositCash.ToString("N0",CultureInfo.CurrentCulture)+"원 / 현금100% 주문가능금액 "+cash.Cash100Orderable.ToString("N0",CultureInfo.CurrentCulture)+"원";
        }
        void RefreshOrders() {
            ordersGrid.Rows.Clear();var shown=new HashSet<string>(StringComparer.Ordinal);
            if(brokerOrders!=null)foreach(var order in brokerOrders.Orders) {
                int index=ordersGrid.Rows.Add(order.OrderNumber,order.Code,order.SideCode=="1"?"매도":order.SideCode=="2"?"매수":"미확인",order.Quantity,order.FilledQuantity,order.OpenQuantity,order.LimitPrice,order.AverageFillPrice,order.HasRejection?"증권사 거부":"증권사 누적 조회");
                ordersGrid.Rows[index].Tag=order;shown.Add(order.OrderNumber);
            }
            if(journal!=null)foreach(var order in journal.Snapshot.Orders.Where(x=>string.IsNullOrEmpty(x.BrokerOrderNumber)||!shown.Contains(x.BrokerOrderNumber)))
                ordersGrid.Rows.Add(order.BrokerOrderNumber??"접수 미확인",order.Code,order.Kind,order.Quantity,order.FilledQuantity,order.Quantity-order.FilledQuantity-order.CancelledQuantity,order.LimitPrice,"미확인",order.Status);
        }
        string ReportDirectory {get{return lastReportKey==null?Path.Combine(Program.DataDir,"reports"):Path.Combine(Program.DataDir,"reports",lastReportKey);}}
        void WriteReport(DateTime koreaDate) {
            if(boundKey==null)return;
            SessionReport.Write(ReportDirectory,koreaDate,DateTime.UtcNow,holdings,holdingsReceivedUtc,cash,brokerOrders,
                journal==null?(IEnumerable<LiveOrderRecord>)new LiveOrderRecord[0]:journal.Snapshot.Orders,LiveOrderSafety.BlockReason,priceReceivedUtc);
        }
        void OpenReports(){string path=ReportDirectory;Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo{FileName=path,UseShellExecute=true});}
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
