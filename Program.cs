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
        [STAThread]
        static int Main(string[] args) {
            if(args.Contains("--selftest")) {
                try{SelfTests.Run();return 0;}catch{return 1;}
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException+=(s,e)=>{
                DiagnosticFile("GUI ERROR: "+e.Exception);
                MessageBox.Show("예외 발생: "+e.Exception.GetBaseException().Message+"\n실주문은 지원되지 않습니다.","Shinhan Native Trader");
            };
            AppDomain.CurrentDomain.UnhandledException+=(s,e)=>DiagnosticFile("UNHANDLED: "+e.ExceptionObject);
            Application.Run(new MainForm());return 0;
        }
        internal static string DataDir {
            get {
                string p=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShinhanNativeTrader");
                Directory.CreateDirectory(p);return p;
            }
        }
        internal static void DiagnosticFile(string msg){
            try{File.AppendAllText(Path.Combine(DataDir,"diagnostics.log"),DateTime.Now.ToString("s")+" "+msg+Environment.NewLine,Encoding.UTF8);}catch{}
        }
    }
    public sealed class MainForm : Form {
        readonly Panel nativeArea=new Panel();
        readonly TextBox logs=new TextBox(), account=new TextBox(), password=new TextBox(), code=new TextBox();
        readonly NumericUpDown paperCash=new NumericUpDown();
        readonly Button connect=new Button(), lookBar=new Button(), lookAccount=new Button(), scan=new Button(), stop=new Button(), quote=new Button();
        readonly Label state=new Label();
        readonly DataGridView grid=new DataGridView(), holdingsGrid=new DataGridView();
        readonly Timer pacing=new Timer(), paperMonitoring=new Timer();
        IndiApi api;
        readonly List<Stock> symbols=new List<Stock>();
        readonly List<Candidate> candidates=new List<Candidate>();
        readonly Queue<Stock> work=new Queue<Stock>();
        readonly Dictionary<string,PaperPosition> paper=new Dictionary<string,PaperPosition>();
        readonly Dictionary<string,Holding> brokerHoldings=new Dictionary<string,Holding>();
        bool scanning=false, connected=false, accountOk=false;
        decimal availablePaper=1000000m;
        DateTime lastPaperQuote=DateTime.MinValue;
        int scanDone=0;
        readonly string journal=Path.Combine(Program.DataDir,"paper_trades.csv");

        public MainForm() {
            Text="Shinhan Native Trader 1.0 | C# Win32 ActiveX (Python 없이 실행)";
            Size=new Size(1150,760);MinimumSize=new Size(1050,680);
            Font=new Font("Malgun Gothic",9);
            var root=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(10)};
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,39));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,120));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,95));
            Controls.Add(root);
            var top=new Label{Text="국내주식 실데이터 연결·전략 시험 | 실제 주문은 잠금 (미검증 API에 돈을 보내지 않습니다)",
                Dock=DockStyle.Fill,ForeColor=Color.DarkRed,Font=new Font("Malgun Gothic",11,FontStyle.Bold)};
            root.Controls.Add(top,0,0);
            var bar=new Panel{Dock=DockStyle.Fill};root.Controls.Add(bar,0,1);
            connect.Text="① 신한 종목조회 연결";connect.SetBounds(8,4,166,33);
            lookBar.Text="② 일봉 분석";lookBar.SetBounds(179,4,126,33);
            quote.Text="현재가 조회";quote.SetBounds(310,4,117,33);
            scan.Text="③ 전체종목 순차스캔";scan.SetBounds(432,4,165,33);
            stop.Text="즉시 스캔 중단";stop.SetBounds(602,4,141,33);
            lookAccount.Text="④ 실제 잔고 조회 (주문 없음)";lookAccount.SetBounds(748,4,218,33);
            foreach(var b in new[]{connect,lookBar,quote,scan,stop,lookAccount})bar.Controls.Add(b);
            var lblCode=new Label{Text="종목코드",Location=new Point(12,52),AutoSize=true};bar.Controls.Add(lblCode);
            code.SetBounds(74,49,92,25);code.Text="005930";bar.Controls.Add(code);
            var lblAccount=new Label{Text="계좌번호",Location=new Point(193,52),AutoSize=true};bar.Controls.Add(lblAccount);
            account.SetBounds(260,49,148,25);bar.Controls.Add(account);
            var lblPwd=new Label{Text="계좌암호",Location=new Point(427,52),AutoSize=true};bar.Controls.Add(lblPwd);
            password.SetBounds(493,49,116,25);password.UseSystemPasswordChar=true;bar.Controls.Add(password);
            var lblPaper=new Label{Text="가상현금(원)",Location=new Point(628,52),AutoSize=true};bar.Controls.Add(lblPaper);
            paperCash.SetBounds(717,49,138,25);paperCash.Minimum=10000;paperCash.Maximum=1000000000;paperCash.Increment=100000;paperCash.Value=1000000;paperCash.ThousandsSeparator=true;bar.Controls.Add(paperCash);
            state.Text="상태: 아직 OCX 미연결 | 주문 전송 기능 없음";state.Location=new Point(12,87);state.Size=new Size(1050,26);bar.Controls.Add(state);
            var tabs=new TabControl{Dock=DockStyle.Fill};root.Controls.Add(tabs,0,2);
            var t1=new TabPage("실제 데이터 종목 후보"),t2=new TabPage("증권사 보유잔고(읽기만)"),t3=new TabPage("시스템 로그");
            tabs.TabPages.AddRange(new[]{t1,t2,t3});
            SetupGrid(grid,new[]{"종목코드","종목명","기준가","점수","선별 조건"});t1.Controls.Add(grid);
            SetupGrid(holdingsGrid,new[]{"종목코드","종목명","잔고수량","매도미체결","매수미체결","평균단가","현재가"});t2.Controls.Add(holdingsGrid);
            logs.Dock=DockStyle.Fill;logs.Multiline=true;logs.ReadOnly=true;logs.ScrollBars=ScrollBars.Vertical;
            logs.Font=new Font("Consolas",9);t3.Controls.Add(logs);
            var footer=new Panel{Dock=DockStyle.Fill};root.Controls.Add(footer,0,3);
            var footerText=new Label{Text="이 버전은 Indi Helper에서 성공한 stock_mst 조회를 C# ActiveX 이벤트로 재구현합니다.\n"+
                "전체 종목을 순차 분석하여 가상 매수·매도를 시험합니다. 실계좌 주문·현금주문가능액 조회·체결복구는 확인 전까지 사용 불가입니다.",
                Dock=DockStyle.Fill,ForeColor=Color.DarkSlateGray};footer.Controls.Add(footerText);
            nativeArea.SetBounds(0,68,12,12);nativeArea.BackColor=Color.Transparent;footer.Controls.Add(nativeArea);
            nativeArea.BringToFront();
            pacing.Interval=950;pacing.Tick+=OnPace;
            paperMonitoring.Interval=15000;paperMonitoring.Tick+=OnPaperMonitor;
            connect.Click+=(s,e)=>Safe(ConnectAndLoad);
            lookBar.Click+=(s,e)=>Safe(()=>AskBars(code.Text.Trim()));
            quote.Click+=(s,e)=>Safe(()=>AskQuote(code.Text.Trim()));
            scan.Click+=(s,e)=>Safe(StartScan);
            stop.Click+=(s,e)=>StopScan("사용자 중단");
            lookAccount.Click+=(s,e)=>Safe(AskHoldings);
            FormClosing+=(s,e)=> {StopScan("프로그램 종료");if(api!=null)api.Dispose();password.Clear();};
            PaperRestore();
            Log("신규 네이티브 C# x86 버전 실행; PyQt/PyInstaller 미사용. 실주문 경로는 UI에 연결하지 않았습니다.");
        }
        static void SetupGrid(DataGridView view,string[] cols){
            view.Dock=DockStyle.Fill;view.ReadOnly=true;view.AllowUserToAddRows=false;
            view.RowHeadersVisible=false;view.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;
            view.SelectionMode=DataGridViewSelectionMode.FullRowSelect;
            foreach(var c in cols)view.Columns.Add(c,c);
        }
        void Safe(Action fn){try{fn();}catch(Exception e){Log("ERROR "+e.GetBaseException().Message);StopScan("오류");MessageBox.Show(e.GetBaseException().Message,"연결/조회 오류");}}
        void Log(string msg){
            var line=DateTime.Now.ToString("HH:mm:ss")+" "+msg;
            logs.AppendText(line+Environment.NewLine);Program.DiagnosticFile(line);
            if(logs.TextLength>65000)logs.Text=logs.Text.Substring(logs.TextLength-45000);
        }
        void ConnectAndLoad() {
            if(api==null){
                api=new IndiApi(nativeArea);api.Trace+=Log;api.Done+=OnReply;
                api.Fatal+= msg=> {connected=false;StopScan("연결 오류");state.Text="상태: API 중단 — "+msg;};
            }
            if(!api.Connected)api.Connect();
            connected=true;
            state.Text="상태: OCX 연결 / stock_mst 실제 응답 대기";
            if(!api.Stocks())throw new InvalidOperationException("이전 TR이 완료되지 않았습니다");
        }
        void OnReply(IndiReply r){
            try {
                if(r.Kind=="STOCKS"){
                    symbols.Clear();symbols.AddRange(r.Stocks);
                    state.Text="실제 종목조회 성공: 원본 "+r.Count+"개 / 코스피·코스닥 일반주식 "+symbols.Count+"개";
                    Log("INDI stock_mst returned "+r.Count+" raw records, "+symbols.Count+" eligible common stocks");
                } else if(r.Kind=="BARS"){
                    var s=symbols.FirstOrDefault(x=>x.Code==r.Code);
                    var name=s==null?r.Code:s.Name;
                    var result=Rules.Entry(r.Code,name,r.Bars);
                    if(result!=null){
                        candidates.Add(result);
                        if(grid.Rows.Count<200)grid.Rows.Add(result.Code,result.Name,result.Price.ToString("N0"),result.Score,result.Reason);
                        Log("후보 "+result.Code+" "+name+" "+result.Score);
                        if(scanning && Rules.ValidTime(DateTime.Now))PaperBuy(result);
                    }
                    scanDone++;
                    state.Text="스캔 "+scanDone+"/"+(scanDone+work.Count)+" | 후보 "+candidates.Count+" | 가상보유 "+paper.Count;
                } else if(r.Kind=="QUOTE"){
                    Log("실제 현재가 "+r.Code+" "+r.Price.ToString("N0")+"원 체결시각="+r.Clock);
                    PaperExit(r.Code,r.Price,r.Clock);
                } else if(r.Kind=="HOLDINGS"){
                    brokerHoldings.Clear();holdingsGrid.Rows.Clear();
                    foreach(var h in r.Holdings){
                        brokerHoldings[h.Code]=h;
                        holdingsGrid.Rows.Add(h.Code,h.Name,h.Quantity,h.SellPending,h.BuyPending,h.AvgPrice.ToString("N0"),h.Price.ToString("N0"));
                    }
                    accountOk=true;Log("신한 실제 잔고 조회 성공: "+r.Holdings.Count+"건 (주문하지 않음)");
                }
            }catch(Exception e){Log("REPLY ERROR "+e);StopScan("응답 처리 실패");}
        }
        void AskBars(string c){
            if(!connected || symbols.Count==0)throw new InvalidOperationException("먼저 ① 종목조회 연결을 성공시켜야 합니다.");
            if(Rules.Code(c)=="")throw new InvalidOperationException("6자리 종목코드를 입력하십시오.");
            if(api.Busy){Log("기존 TR 응답 대기 중");return;}
            api.Bars(c);
        }
        void AskQuote(string c){
            if(!connected)throw new InvalidOperationException("먼저 ① 신한 연결을 하십시오.");
            if(Rules.Code(c)=="")throw new InvalidOperationException("6자리 종목코드 필요");
            if(!api.Busy)api.Quote(c);
        }
        void AskHoldings(){
            if(!connected)throw new InvalidOperationException("먼저 ① 연결을 확인하십시오.");
            if(api.Busy)throw new InvalidOperationException("기존 조회 응답 대기 중");
            if(!api.Holdings(account.Text.Trim(),password.Text))throw new InvalidOperationException("잔고 조회가 실행되지 않았습니다");
            password.Clear();
        }
        void StartScan(){
            if(!connected || symbols.Count==0)throw new InvalidOperationException("먼저 전체 종목조회에 성공해야 합니다.");
            if(scanning)return;
            candidates.Clear();grid.Rows.Clear();work.Clear();scanDone=0;
            foreach(var s in symbols.OrderBy(x=>x.Code))work.Enqueue(s);
            scanning=true;RecalculatePaperCash();
            pacing.Start();paperMonitoring.Start();Log("시작: "+work.Count+"개 실제 종목 순차 일봉 조회; 950ms 간격. 실주문 0건");
        }
        void StopScan(string why){
            if(scanning){scanning=false;pacing.Stop();paperMonitoring.Stop();Log("스캔 중단 "+why);}
        }
        void OnPace(object sender,EventArgs args){
            if(!scanning)return;
            if(api==null || !api.Connected){StopScan("OCX 연결 없음");return;}
            if(api.Busy)return;
            if(work.Count==0){StopScan("모든 종목 스캔 완료");return;}
            var s=work.Dequeue();api.Bars(s.Code);
        }
        void OnPaperMonitor(object sender,EventArgs args){
            if(!scanning||paper.Count==0||api.Busy)return;
            if((DateTime.Now-lastPaperQuote).TotalSeconds<30)return;
            lastPaperQuote=DateTime.Now;
            var codes=paper.Keys.ToArray();
            if(codes.Length>0)api.Quote(codes[(DateTime.Now.Second/10)%codes.Length]);
        }
        void PaperBuy(Candidate c){
            if(paper.ContainsKey(c.Code)||paper.Count>=3)return;
            var qty=Rules.PaperQty(availablePaper,c.Price);
            if(qty<=0)return;
            var cost=c.Price*qty*1.003m;
            if(availablePaper<cost)return;
            availablePaper-=cost;
            paper[c.Code]=new PaperPosition{Code=c.Code,Name=c.Name,Qty=qty,Entry=c.Price,High=c.Price};
            WritePaper("BUY",c.Code,qty,c.Price,"signal");
            Log("가상매수 (실제 주문 아님) "+c.Code+" "+qty+"주 "+c.Price.ToString("N0"));
        }
        void PaperExit(string c,decimal price,string clock){
            PaperPosition p;if(!paper.TryGetValue(c,out p))return;
            if(!Rules.ValidTime(DateTime.Now))return;
            // Only accept today's regular-session quotes; stale and after-hours do not trigger even simulated sells.
            var text=(clock??"").Trim();
            if(text.Length<6||!text.Take(6).All(char.IsDigit))return;
            int hh,mm,ss;
            if(!int.TryParse(text.Substring(0,2),out hh)||!int.TryParse(text.Substring(2,2),out mm)||!int.TryParse(text.Substring(4,2),out ss))return;
            if(hh>23||mm>59||ss>59)return;
            var stamp=DateTime.Today.AddHours(hh).AddMinutes(mm).AddSeconds(ss);
            if(Math.Abs((DateTime.Now-stamp).TotalSeconds)>45)return;
            if(price>p.High)p.High=price;
            var reason=Rules.Exit(p,price);
            if(reason=="")return;
            availablePaper += price*p.Qty*0.997m;
            WritePaper("SELL",c,p.Qty,price,reason);
            paper.Remove(c);
            Log("가상자동매도 (실제 주문 아님) "+c+" "+reason+" "+price.ToString("N0"));
        }
        void RecalculatePaperCash(){
            availablePaper=paperCash.Value;
            if(!File.Exists(journal))return;
            foreach(var line in File.ReadAllLines(journal,Encoding.UTF8).Skip(1)){
                var v=line.Split(',');if(v.Length<6)continue;
                int qty;decimal price;
                if(!int.TryParse(v[3],out qty)||!decimal.TryParse(v[4],NumberStyles.Any,CultureInfo.InvariantCulture,out price))continue;
                if(v[1]=="BUY")availablePaper-=qty*price*1.003m;
                else if(v[1]=="SELL")availablePaper+=qty*price*0.997m;
            }
            availablePaper=Math.Max(0,availablePaper);
            Log("가상현금 보유분/거래기록 반영: "+availablePaper.ToString("N0")+"원 (실계좌 무관)");
        }
        void PaperRestore(){
            if(!File.Exists(journal))return;
            try{
                foreach(var line in File.ReadAllLines(journal,Encoding.UTF8).Skip(1)){
                    var v=line.Split(',');
                    if(v.Length<6)continue;
                    var c=v[2];int qty;decimal price;
                    if(!int.TryParse(v[3],out qty) || !decimal.TryParse(v[4],NumberStyles.Any,CultureInfo.InvariantCulture,out price))continue;
                    if(v[1]=="BUY")paper[c]=new PaperPosition{Code=c,Name=c,Qty=qty,Entry=price,High=price};
                    else if(v[1]=="SELL")paper.Remove(c);
                }
                if(paper.Count>0)Log("이전 가상보유 "+paper.Count+"종목 복원. 실제 잔고와 무관합니다.");
            }catch(Exception e){Log("가상기록 복원 오류 "+e.Message);}
        }
        void WritePaper(string side,string c,int qty,decimal price,string reason){
            if(!File.Exists(journal))File.WriteAllText(journal,"time,side,code,qty,price,reason"+Environment.NewLine,Encoding.UTF8);
            var safeReason=(reason??"").Replace(","," ").Replace("\n"," ");
            File.AppendAllText(journal,string.Join(",",DateTime.Now.ToString("s"),side,c,qty,
                price.ToString(CultureInfo.InvariantCulture),safeReason)+Environment.NewLine,Encoding.UTF8);
        }
    }
}
