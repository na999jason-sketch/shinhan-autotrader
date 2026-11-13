using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Runs the actual form and read-only coordinator against synthetic stock/bar
// data. No Connect, AttachAccount, Arm, realtime constructor or order wire runs.
static class PremarketUiTests {
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly StringBuilder Results=new StringBuilder();
    static readonly DateTime Opening=new DateTime(2026,11,16,0,0,0,DateTimeKind.Utc);
    static readonly DateTime BeforeOpen=Opening.AddMinutes(-30);
    static int assertions;
    static void Check(bool value,string label) {
        assertions++;if(!value)throw new InvalidOperationException(label);
        Results.AppendLine("PASS "+label);
    }
    static T Get<T>(object owner,string field) {return (T)owner.GetType().GetField(field,Hidden).GetValue(owner);}
    static void Set(object owner,string field,object value) {owner.GetType().GetField(field,Hidden).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] arguments) {
        try{return owner.GetType().GetMethod(name,Hidden).Invoke(owner,arguments);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    static MainForm NewForm() {
        var form=new MainForm();Get<Timer>(form,"scheduler").Stop();
        form.ShowInTaskbar=false;form.Show();Application.DoEvents();Get<Timer>(form,"scheduler").Stop();return form;
    }
    static Dictionary<string,PremarketReference> Prepared(MainForm form) {return Get<Dictionary<string,PremarketReference>>(form,"preparedReferences");}
    static void Schedule(MainForm form,DateTime now) {Call(form,"ScheduleAt",now);}
    static void Close(MainForm form,string data) {
        form.Close();
        using(var file=new FileStream(Path.Combine(data,"live-settings-v1.xml.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Check(file.CanWrite,"window close releases its settings lease");
    }
    static void Bind(MainForm form) {
        Get<NumericUpDown>(form,"budget").Value=1000000;
        Call(form,"SaveSettings");
        var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
        var credentials=Activator.CreateInstance(type,new object[]{"12345678901","1111"});
        var key=(string)type.GetProperty("Key").GetValue(credentials,null);
        Set(form,"credentials",credentials);Set(form,"boundKey",key);Set(form,"lastReportKey",key);
        Get<LiveTradingSession>(form,"authorization").Authorize("12345678901",BeforeOpen);
        Get<UnattendedSession>(form,"session").Arm(BeforeOpen);
        Set(form,"nextAccountReadUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
        Set(form,"nextReportUtc",DateTime.SpecifyKind(DateTime.MaxValue,DateTimeKind.Utc));
    }

    sealed class SyntheticTransport : IIndiTransport {
        public event Action<int> ReceiveData;
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public readonly List<string> Queries=new List<string>();
        public readonly List<Bar> Bars=new List<Bar>();
        public readonly string[] Codes={"000660","005930","035420"};
        readonly Dictionary<int,string> inputs=new Dictionary<int,string>();
        string query;int nextId,pending;
        public SyntheticTransport() {
            var dates=new List<DateTime>();var date=Opening.Date.AddDays(-1);
            while(dates.Count<45) {
                if(date.DayOfWeek!=DayOfWeek.Saturday&&date.DayOfWeek!=DayOfWeek.Sunday)dates.Add(date);
                date=date.AddDays(-1);
            }
            dates.Reverse();
            for(int i=0;i<dates.Count;i++) {
                decimal close=7000+i*10;
                Bars.Add(new Bar{Date=dates[i].ToString("yyyyMMdd",CultureInfo.InvariantCulture),Open=close,High=close+10,Low=close-10,Close=close,Volume=1000});
            }
        }
        public object Invoke(string method,params object[] arguments) {
            if(method=="SetQueryName"){query=(string)arguments[0];inputs.Clear();return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData"){inputs[(int)arguments[0]]=(string)arguments[1];return true;}
            if(method=="RequestData") {
                if(query!="stock_mst"&&query!="TR_SCHART")throw new InvalidOperationException("Unexpected non-research request: "+query);
                Queries.Add(query);pending=++nextId;return (short)pending;
            }
            if(method=="GetMultiRowCount")return query=="stock_mst"?Codes.Length:Bars.Count;
            if(method=="GetMultiData") {
                int row=(int)arguments[0],column=(int)arguments[1];
                if(query=="stock_mst") {
                    if(column==1)return Codes[row];if(column==2||column==6||column==7||column==8)return "0";
                    if(column==3)return "SYNTHETIC "+row;if(column==14)return "ST";return "";
                }
                var bar=Bars[row];
                if(column==0)return bar.Date;
                decimal number=column==2?bar.Open:column==3?bar.High:column==4?bar.Low:column==5?bar.Close:column==9?bar.Volume:0;
                return number.ToString(CultureInfo.InvariantCulture);
            }
            throw new InvalidOperationException("Unexpected synthetic provider method: "+method);
        }
        public void Complete() {
            if(pending==0)throw new InvalidOperationException("No synthetic response is pending.");
            int id=pending;pending=0;if(ReceiveData!=null)ReceiveData(id);
        }
        public void Dispose(){}
    }
    sealed class Fixture {
        public readonly SyntheticTransport Wire=new SyntheticTransport();
        public readonly IndiApi Api;
        public Fixture(MainForm form) {
            Api=new IndiApi(Get<Panel>(form,"nativeArea"));
            var requests=new IndiRequestCoordinator(Wire);
            requests.Fatal+=message=>{throw new InvalidOperationException("Synthetic coordinator failed: "+message);};
            requests.Done+=reply=>{Get<Timer>(Api,"timeout").Stop();Call(form,"Reply",reply);};
            Set(Api,"requests",requests);Set(Api,"hasReceivedData",true);Set(form,"api",Api);
            if(!Api.Stocks())throw new InvalidOperationException("Synthetic stock request did not start.");Wire.Complete();
        }
    }
    static void Begin(MainForm form) {Call(form,"BeginResearch",BeforeOpen,false);}
    static void FinishPreparation(MainForm form,Fixture fixture,int completed) {
        for(int i=completed;i<fixture.Wire.Codes.Length;i++) {
            Schedule(form,BeforeOpen.AddSeconds(i+1));
            Check(fixture.Api.Busy,"research scheduler issues the next synthetic daily-bar request");
            fixture.Wire.Complete();
        }
        Schedule(form,BeforeOpen.AddSeconds(10));
    }
    static void CheckNoOrderTransport(MainForm form,string label) {
        Check(Get<RealtimeIndi>(form,"realtime")==null&&Get<LiveOrderGateway>(form,"gateway")==null
            &&Get<LiveOrderJournal>(form,"journal")==null&&Get<object>(form,"entry")==null,label);
    }
    static void Capture(MainForm form,string path) {
        foreach(var tabs in All(form).OfType<TabControl>())
            tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(page=>page.Text=="발굴 후보");
        form.PerformLayout();Application.DoEvents();form.Refresh();Application.DoEvents();
        using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)) {
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(form.PointToScreen(System.Drawing.Point.Empty),System.Drawing.Point.Empty,bitmap.Size);
            bitmap.Save(path);
        }
    }
    static IEnumerable<Control> All(Control owner) {
        foreach(Control child in owner.Controls){yield return child;foreach(var nested in All(child))yield return nested;}
    }

    static void RealtimeStatus(string data) {
        using(var form=NewForm()) {
            var fields=new Dictionary<int,object>{{1,"005930"},{2,"09:00:00"},{3,"70000"},{7,"100"},{17,"1"}};
            var processor=new RealtimeEventProcessor(index=>fields[index]);processor.Track("005930");
            // Deliberately bypass the COM constructor: only the in-memory
            // processor and status labels run. No OCX or registration API exists.
            var realtime=(RealtimeIndi)FormatterServices.GetUninitializedObject(typeof(RealtimeIndi));
            Set(realtime,"processor",processor);Set(realtime,"watch",new HashSet<string>(StringComparer.Ordinal){"005930"});
            Set(form,"realtime",realtime);Get<HashSet<string>>(form,"trackedQuotes").Add("005930");
            Call(form,"EvaluatePreparedSignals",Opening);
            string status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 0 / 등록 1")&&status.Contains("콜백 없음")&&Get<Label>(form,"quoteStatus").ForeColor==System.Drawing.Color.DarkRed,
                "registered quotes with zero callbacks are identified in red rather than described as an entry-signal wait");
            processor.Process("SC",Opening);
            Call(form,"EvaluatePreparedSignals",Opening);
            Check(Get<Label>(form,"quoteStatus").Text.Contains("확인 0 / 등록 1"),"one actual callback still waits for sequence progression");
            fields[2]="09:00:01";fields[7]="101";processor.Process("SC",Opening.AddSeconds(1));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(1));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 1 / 등록 1")&&status.Contains("정상")&&Get<Label>(form,"quoteStatus").ForeColor==System.Drawing.Color.Black,
                "advancing colon-formatted exchange clocks establish a confirmed realtime quote in the actual form status");
            fields[2]="unrecognized";processor.Process("SC",Opening.AddSeconds(2));
            Call(form,"EvaluatePreparedSignals",Opening.AddSeconds(2));
            status=Get<Label>(form,"quoteStatus").Text;
            Check(status.Contains("확인 0 / 등록 1")&&status.Contains("시각 형식"),"received SC with an invalid clock is distinguished from missing callbacks and cannot remain trade-ready");
            Check(!processor.Failed&&Get<LiveOrderGateway>(form,"gateway")==null&&Get<LiveOrderJournal>(form,"journal")==null
                &&Get<object>(form,"entry")==null&&!Get<LiveTradingSession>(form,"authorization").Active,"realtime status regression never constructs an order transport or authorizes a live order");
            Close(form,data);
        }
    }

    static void Run(string data,string screenshot) {
        using(var form=NewForm()) {
            Check(form.Text.Contains("2.2.1"),"premarket regression runs against version 2.2.1");
            Check(!Get<LiveTradingSession>(form,"authorization").Active,"application startup never authorizes live orders");
            var fixture=new Fixture(form);Bind(form);
            Call(form,"BeginResearch",Opening.AddHours(-10),false);
            Check(!Get<bool>(form,"scanning")&&Prepared(form).Count==0&&fixture.Wire.Queries.Count==1,
                "before target-day midnight the reservation sends no unfinished daily-bar research requests");
            Begin(form);
            Check(Get<List<Stock>>(form,"symbols").Count==3,"synthetic stock master traverses the actual coordinator and form callback");
            Check(Get<DateTime?>(form,"researchDateKorea")==MarketTime.ToKorea(Opening).Date,"preparation is bound to the authorized trading date");
            FinishPreparation(form,fixture,0);
            Check(Prepared(form).Count==3&&Get<bool>(form,"researchComplete"),"preopen daily-bar callbacks complete and retain all prepared references");
            Check(Prepared(form).Values.All(x=>x.LastCompletedBarDate=="20261113"),"prepared references preserve the prior completed trading date");
            Check(Get<DataGridView>(form,"candidatesGrid").Rows.Count==3,"prepared watchlist rows are visible before opening");
            Check(!Get<UnattendedSession>(form,"session").SessionDateKorea.HasValue,"preopen preparation does not open the market trading session");
            Check(Get<Queue<Candidate>>(form,"buyQuotes").Count==0,"historical preopen references are not promoted into actionable buy candidates");
            CheckNoOrderTransport(form,"preopen preparation activates no realtime or order transport");
            var before=Prepared(form).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal);
            int requests=fixture.Wire.Queries.Count;
            Schedule(form,Opening);
            Check(Get<bool>(form,"openingAdopted"),"opening adopts completed premarket preparation");
            Check(Prepared(form).Count==before.Count&&before.All(x=>ReferenceEquals(Prepared(form)[x.Key],x.Value)),"opening retains the original prepared reference objects");
            Check(Get<List<PremarketReference>>(form,"watchedReferences").Count==3,"opening monitors the prepared watchlist");
            Check(Get<Queue<Candidate>>(form,"buyQuotes").Count==0,"opening adoption waits for live validation before creating actionable buy candidates");
            Check(fixture.Wire.Queries.Count==requests,"opening does not restart stock master or full daily-bar discovery");
            Schedule(form,Opening.AddSeconds(1));
            CheckNoOrderTransport(form,"prepared history alone cannot queue an order without confirmed live SC");
            Check(Get<DataGridView>(form,"candidatesGrid").Rows.Count==3,"live quote wait preserves prepared candidate rows");
            var reference=before.Values.First();
            var live=new RealtimeQuote{Code=reference.Code,MarketCode="1",Clock="090001",Price=reference.PriorHigh+10,
                CumulativeVolume=reference.AverageVolume*2,ReceivedUtc=Opening.AddSeconds(1),SequenceConfirmed=false};
            var strategy=Get<AccountAppSettings>(form,"configuration").Strategy;
            Check(PremarketResearch.EvaluateLive(reference,live,strategy,Opening.AddSeconds(1))==null,"UI-prepared reference cannot become a signal from one unconfirmed SC update");
            live.SequenceConfirmed=true;
            var signal=PremarketResearch.EvaluateLive(reference,live,strategy,Opening.AddSeconds(1));
            Check(signal!=null&&signal.SignalDate=="20261116"&&reference.LastCompletedBarDate=="20261113","confirmed live validation produces a current-session signal without rewriting historical source date");

            var source=new BrokerHoldingsSnapshot{BasisVerified=true,RequestStartedUtc=DateTime.UtcNow.AddMilliseconds(-10),ReceivedUtc=DateTime.UtcNow};
            source.Holdings.Add(new Holding{Code="069500",Name="SYNTHETIC ETF",Quantity=2,AvgPrice=70000,Price=70000});
            source.BasisValidated["069500"]=true;source.CashSourceValidated["069500"]=true;source.BasisDiagnostics["069500"]="TradeBasisCode1";
            var requestType=typeof(MainForm).GetNestedType("EntryRequest",BindingFlags.NonPublic);
            var pending=Activator.CreateInstance(requestType,true);
            requestType.GetField("AutoSize").SetValue(pending,true);
            requestType.GetField("Intent").SetValue(pending,new LiveOrderIntent{Kind=LiveOrderKind.Buy,Code=reference.Code,Quantity=1,LimitPrice=live.Price,ClientKey="entry-20261116-"+reference.Code});
            requestType.GetField("Candidate").SetValue(pending,signal);
            Set(form,"entry",pending);
            Call(form,"Reply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=source,RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});
            Check(Get<List<PremarketReference>>(form,"watchedReferences").Any(x=>ReferenceEquals(x,reference)),"preflight holdings refresh retains the pending buy's original prepared reference");
            Check((bool)Call(form,"ValidEntrySignal",signal,live,Opening.AddSeconds(1)),"a pending buy remains signal-valid after fresh other-symbol holdings are observed");
            Set(form,"entry",null);
            Check(Get<List<Holding>>(form,"holdings").Single().Code=="069500"&&!Prepared(form).ContainsKey("069500"),"existing holdings remain monitored outside the prepared discovery universe");
            var decision=(string)Call(form,"EvaluateHoldingSell",source.Holdings[0],new IndiReply{Code="069500",Price=70000,Clock="090001",HasTradeClock=true},Opening.AddSeconds(1));
            Check(string.IsNullOrEmpty(decision),"opening preparation does not force a sale of existing holdings");
            string streamStatus=Get<Label>(form,"quoteStatus").Text;
            Call(form,"Reply",new IndiReply{Kind="QUOTE",Code="069500",Price=71000,HasTradeClock=false,Clock=""});
            Check(Get<Label>(form,"state").Text.Contains("조회 전용")&&Get<Label>(form,"state").Text.Contains("실시간 SC 상태는 별도")
                &&Get<Label>(form,"quoteStatus").Text==streamStatus,"manual snapshot's missing clock is described separately from realtime SC status");
            Call(form,"UpdateResearchStatus",Opening.AddSeconds(1));
            if(screenshot!=null)Capture(form,screenshot);

            Get<Queue<Candidate>>(form,"buyQuotes").Enqueue(signal);
            var newlyHeld=new Holding{Code=reference.Code,Name="SYNTHETIC NEW HOLDING",Quantity=1,AvgPrice=live.Price,Price=live.Price};
            source.Holdings.Add(newlyHeld);source.BasisValidated[reference.Code]=true;source.CashSourceValidated[reference.Code]=true;source.BasisDiagnostics[reference.Code]="TradeBasisCode1";
            Call(form,"Reply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=source,RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});
            Check(!Get<List<PremarketReference>>(form,"watchedReferences").Any(x=>x.Code==reference.Code)
                &&!Get<Queue<Candidate>>(form,"buyQuotes").Any(x=>x.Code==reference.Code),"a newly held symbol is removed from both watchlist and queued buy signals");
            source.Holdings.Remove(newlyHeld);
            Call(form,"Reply",new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=source,RequestStartedUtc=source.RequestStartedUtc,ReceivedUtc=source.ReceivedUtc});

            object saved=Call(form,"CaptureResearch");form.Halt();
            Check((bool)Call(form,"RestoreResearch",saved,BeforeOpen)&&Prepared(form).Count==3
                &&before.All(x=>ReferenceEquals(Prepared(form)[x.Key],x.Value)),"public research can survive account attachment's capture-stop-restore lifecycle");
            Check(!Get<LiveTradingSession>(form,"authorization").Active&&Get<object>(form,"credentials")==null
                &&Get<Queue<Candidate>>(form,"buyQuotes").Count==0&&Get<RealtimeIndi>(form,"realtime")==null,"restoring public research restores no account approval, credentials, realtime stream or buy signals");
            form.Halt();
            Check(!(bool)Call(form,"RestoreResearch",saved,BeforeOpen.AddDays(1))&&Prepared(form).Count==0,"public research from a different target trading day is refused");
            Call(form,"RestoreResearch",saved,BeforeOpen);

            Get<Dictionary<string,NumericUpDown>>(form,"options")["FastPeriod"].Value=6;
            Call(form,"SaveSettings");
            Check(Prepared(form).Count==0&&Get<List<PremarketReference>>(form,"watchedReferences").Count==0,"strategy save invalidates all prepared and watched references");
            Check(!Get<LiveTradingSession>(form,"authorization").Active,"strategy save requires explicit new live authorization");
            Check(Get<List<Holding>>(form,"holdings").Count==1,"strategy change preserves last received holdings for display");
            Check(!(bool)Call(form,"RestoreResearch",saved,BeforeOpen)&&Prepared(form).Count==0,"public research from a different strategy is refused");
            Close(form,data);
        }
        using(var form=NewForm()) {
            var fixture=new Fixture(form);Bind(form);Begin(form);
            Schedule(form,BeforeOpen.AddSeconds(1));fixture.Wire.Complete();
            Check(Prepared(form).Count==1,"partial preparation retains the first completed reference");
            var first=Prepared(form).Values.Single();
            Schedule(form,BeforeOpen.AddSeconds(2));
            Check(fixture.Api.Busy,"a later preopen daily-bar request can remain in flight");
            int beforeOpenRequests=fixture.Wire.Queries.Count;
            Schedule(form,Opening);
            Check(Prepared(form).Count==1&&ReferenceEquals(Prepared(form)[first.Code],first),"opening while a bar request is in flight preserves completed references");
            Check(fixture.Api.Busy&&fixture.Wire.Queries.Count==beforeOpenRequests,"opening does not cancel or duplicate the in-flight research request");
            fixture.Wire.Complete();Schedule(form,Opening.AddSeconds(1));
            Check(Get<bool>(form,"openingAdopted")&&ReferenceEquals(Prepared(form)[first.Code],first),"partial research is adopted after the pending response without clearing earlier work");
            // The remaining discovery may proceed after adoption. Only complete
            // requests issued by the scheduler; no forced extra scan is started.
            for(int i=0;i<6&&!Get<bool>(form,"researchComplete");i++) {
                if(fixture.Api.Busy)fixture.Wire.Complete();
                Schedule(form,Opening.AddSeconds(i+2));
            }
            Check(Prepared(form).Count==3&&Get<bool>(form,"researchComplete"),"remaining preopen research finishes during the session without restarting the scan");
            Check(fixture.Wire.Queries.Count(x=>x=="stock_mst")==1&&fixture.Wire.Queries.Count(x=>x=="TR_SCHART")==3,"partial transition queries each discovery symbol exactly once");
            CheckNoOrderTransport(form,"partial opening transition never creates a financial order from historical bars");
            Close(form,data);
        }
        RealtimeStatus(data);
    }

    [STAThread] static int Main(string[] arguments) {
        string result=arguments.Length>0?Path.GetFullPath(arguments[0]):null;
        string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShinhanNativeTrader");
        bool ownsData=false;int exit=0;
        try {
            bool windows=Environment.OSVersion.Platform==PlatformID.Win32NT;
            bool linux=arguments.Contains("--isolated-linux-smoke")&&data.StartsWith("/tmp/shinhan-premarket-ui-",StringComparison.Ordinal);
            if(!(windows&&Environment.GetEnvironmentVariable("GITHUB_ACTIONS")=="true")&&!linux)
                throw new InvalidOperationException("Premarket UI tests require a fresh GitHub Actions Windows profile or explicitly isolated Linux directory.");
            if(Directory.Exists(data)||File.Exists(data))throw new InvalidOperationException("Refusing to touch existing application data.");
            if(result==null||result.StartsWith(data+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An external result path is required.");
            ownsData=true;CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            int image=Array.IndexOf(arguments,"--screenshot");
            Run(data,image>=0?Path.GetFullPath(arguments[image+1]):null);
            Results.AppendLine("PASS "+assertions+" premarket UI assertions; synthetic data only, no actual Indi server or money orders.");
        }catch(Exception error){exit=1;Results.AppendLine("FAIL "+error);}
        finally {
            if(ownsData&&Directory.Exists(data))try{Directory.Delete(data,true);}catch(Exception error){exit=1;Results.AppendLine("FAIL test profile cleanup: "+error);}
            Console.Write(Results.ToString());
            if(result!=null)try{Directory.CreateDirectory(Path.GetDirectoryName(result));File.WriteAllText(result,Results.ToString(),Encoding.UTF8);}
                catch(Exception error){Console.Error.WriteLine(error);exit=1;}
        }
        return exit;
    }
}
