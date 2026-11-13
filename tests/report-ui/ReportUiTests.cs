using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using ShinhanNativeTrader;

// Exercises the real form's report callbacks without Connect, AttachAccount,
// Arm, COM activation, real account data or any order transport.
static class ReportUiTests {
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static readonly StringBuilder Results=new StringBuilder();
    static int assertions;

    static void Check(bool condition,string label) {
        assertions++;
        if(!condition)throw new InvalidOperationException(label);
        Results.AppendLine("PASS "+label);
    }
    static T Get<T>(object owner,string name) {return (T)owner.GetType().GetField(name,Hidden).GetValue(owner);}
    static void Set(object owner,string name,object value) {owner.GetType().GetField(name,Hidden).SetValue(owner,value);}
    static object Call(object owner,string name,params object[] arguments) {
        try{return owner.GetType().GetMethod(name,Hidden).Invoke(owner,arguments);}
        catch(TargetInvocationException error){throw error.InnerException??error;}
    }
    static void Reply(MainForm form,IndiReply reply) {Call(form,"Reply",reply);}
    static MainForm NewForm() {
        var form=new MainForm();Get<Timer>(form,"scheduler").Stop();
        // An unshown WinForms form may dispose without raising FormClosing on
        // Windows. Create its window so Close follows the production lifecycle
        // and releases the application settings lease.
        form.ShowInTaskbar=false;form.Show();Application.DoEvents();
        Get<Timer>(form,"scheduler").Stop();return form;
    }
    static void CheckSettingsLeaseReleased(string data,string label) {
        using(var lease=new FileStream(Path.Combine(data,"live-settings-v1.xml.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            Check(lease.CanWrite,label);
    }
    static object BindSyntheticAccount(MainForm form) {
        var type=typeof(MainForm).Assembly.GetType("ShinhanNativeTrader.AccountCredentialSession",true);
        var credential=Activator.CreateInstance(type,new object[]{"12345678901","1111"});
        var key=(string)type.GetProperty("Key").GetValue(credential,null);
        Set(form,"credentials",credential);Set(form,"boundKey",key);Set(form,"lastReportKey",key);
        return credential;
    }
    static IndiReply HoldingsReply() {
        var now=DateTime.UtcNow;
        var snapshot=new BrokerHoldingsSnapshot{RequestStartedUtc=now.AddMilliseconds(-20),ReceivedUtc=now,BasisVerified=false};
        snapshot.Holdings.Add(new Holding{Code="005930",Name="SYNTHETIC",Quantity=2,AvgPrice=70000,Price=65000});
        snapshot.BasisValidated["005930"]=false;snapshot.BasisDiagnostics["005930"]="Unknown";snapshot.CashSourceValidated["005930"]=false;
        return new IndiReply{Kind="TRADEHOLDINGS",TradeHoldings=snapshot,RequestStartedUtc=snapshot.RequestStartedUtc,ReceivedUtc=now};
    }
    static void CashReply(MainForm form,decimal amount) {
        var now=DateTime.UtcNow;
        Reply(form,new IndiReply{Kind="CASH",RequestStartedUtc=now.AddMilliseconds(-20),ReceivedUtc=now,
            Cash=new AccountCashSnapshot{DepositCash=amount+100,CashOrderable=amount+50,Cash100Orderable=amount,ReceivedUtc=now}});
    }
    static string[] Reports(string data) {return Directory.Exists(data)?Directory.GetFiles(data,"account-report-*.csv",SearchOption.AllDirectories):new string[0];}
    static bool ContainsCash(string path,decimal amount) {return File.ReadAllText(path).Contains(","+amount.ToString(CultureInfo.InvariantCulture)+"\r\n");}
    static void Capture(MainForm form,string path) {
        form.Show();form.PerformLayout();Application.DoEvents();form.Refresh();Application.DoEvents();
        using(var bitmap=new System.Drawing.Bitmap(form.Width,form.Height)) {
            using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(form.PointToScreen(System.Drawing.Point.Empty),System.Drawing.Point.Empty,bitmap.Size);
            bitmap.Save(path);
        }
    }

    sealed class BusyTransport : IIndiTransport {
        public event Action<int> ReceiveData {add{} remove{}}
        public event Action<int> ReceiveSysMsg {add{} remove{}}
        public event Action<string> Error {add{} remove{}}
        public int Requests;
        string query;
        public object Invoke(string method,params object[] arguments) {
            if(method=="SetQueryName"){query=(string)arguments[0];return true;}
            if(method=="GetQueryName")return query;
            if(method=="SetSingleData")return true;
            if(method=="RequestData"){Requests++;return (short)7;}
            throw new InvalidOperationException("Unexpected synthetic transport call: "+method);
        }
        public void Dispose(){}
    }

    static void Run(string data,string screenshot,bool windows) {
        using(var form=NewForm()) {
            Check(form.Text.Contains("2.2.2"),"report regression runs against version 2.2.2");
            Check(!Get<LiveTradingSession>(form,"authorization").Active,"startup has no live authorization");
            Call(form,"PrepareReports");
            Check(Reports(data).Length==0,"opening report folder before account data does not fabricate CSV");
            var credential=BindSyntheticAccount(form);
            Call(form,"PrepareReports");
            Check(Reports(data).Length==0&&Get<Label>(form,"reportStatus").Text.Contains("저장 대기"),"bound account without a received snapshot waits without an empty CSV");

            Reply(form,HoldingsReply());
            string report=Get<string>(form,"lastSavedReportPath");
            Check(report!=null&&File.Exists(report),"first holdings callback immediately writes CSV while operation is unarmed");
            Check(File.ReadAllText(report).Contains("70000")&&File.ReadAllText(report).Contains("65000"),"immediate CSV contains received holding prices even when basis is unknown");
            Check(Get<IndiApi>(form,"api")==null&&Get<RealtimeIndi>(form,"realtime")==null&&Get<LiveOrderJournal>(form,"journal")==null,"initial report creates no broker, realtime channel or journal");
            Check(!Get<UnattendedSession>(form,"session").Armed&&!Get<LiveTradingSession>(form,"authorization").Active,"initial report requires neither market session nor order approval");
            CashReply(form,1234567);
            var now=DateTime.UtcNow;
            var orders=new BrokerOrdersSnapshot{TradingDate=MarketTime.ToKorea(now).ToString("yyyyMMdd",CultureInfo.InvariantCulture),ReceivedUtc=now};
            Reply(form,new IndiReply{Kind="ORDERS",BrokerOrders=orders,RequestStartedUtc=now.AddMilliseconds(-20),ReceivedUtc=now});
            Check(Get<bool>(form,"initialReportComplete")&&ContainsCash(report,1234567),"initial account replies persist cash and finish the first snapshot");

            CashReply(form,2333444);
            Check(!ContainsCash(report,2333444),"later cached cash waits for due or explicit report save");
            var wire=new BusyTransport();var coordinator=new IndiRequestCoordinator(wire);
            Check(coordinator.Begin("QUOTE","SC",new Dictionary<int,string>{{0,"005930"}},"005930"),"synthetic read request stays pending without activating COM");
            var api=new IndiApi(Get<Panel>(form,"nativeArea"));
            Set(api,"requests",coordinator);Set(api,"hasReceivedData",true);Set(form,"api",api);
            var queue=Get<Queue<string>>(form,"accountReads");queue.Enqueue("TRADEHOLDINGS");
            var entryType=typeof(MainForm).GetNestedType("EntryRequest",BindingFlags.NonPublic);
            var entry=Activator.CreateInstance(entryType,true);Set(form,"entry",entry);
            Set(form,"monitoring",true);Set(form,"nextReportUtc",DateTime.UtcNow.AddMinutes(-1));
            Call(form,"Schedule");
            Check(ContainsCash(report,2333444),"due report saves before busy API, queued account read and preflight early returns");
            Check(api.Busy&&wire.Requests==1&&queue.Count==1&&ReferenceEquals(Get<object>(form,"entry"),entry),"report scheduling does not consume pending reads or send another request");
            Set(form,"api",null);api.Dispose();queue.Clear();Set(form,"entry",null);Set(form,"monitoring",false);

            CashReply(form,3444555);
            var orderReference=Get<BrokerOrdersSnapshot>(form,"brokerOrders");
            string folder=(string)Call(form,"PrepareReports");
            Check(ContainsCash(report,3444555)&&folder==Path.GetDirectoryName(report),"explicit report preparation writes latest cached snapshot and returns its actual folder");
            Check(Get<string>(form,"lastSavedReportPath")==report&&Get<DateTime?>(form,"lastReportSavedUtc").HasValue&&Get<Label>(form,"reportStatus").Text.Contains(Path.GetFileName(report)),"report status identifies saved filename and successful timestamp");
            Check(!Get<LiveTradingSession>(form,"authorization").Active&&ReferenceEquals(Get<object>(form,"credentials"),credential),"explicit save preserves account binding without granting order permission");
            Check(ReferenceEquals(Get<BrokerOrdersSnapshot>(form,"brokerOrders"),orderReference)&&orders.Orders.Count==0&&Get<LiveOrderJournal>(form,"journal")==null,"explicit report save neither changes order evidence nor invents executions");
            Check(!File.ReadAllText(report).Contains("12345678901"),"report does not contain the synthetic account number");
            if(screenshot!=null)Capture(form,screenshot);

            // Windows must prove the real exclusive-open failure. POSIX replacement
            // can succeed despite FileShare.None, so Linux exercises the equivalent
            // checked storage error with an unusable backup destination instead.
            var approval=Get<LiveTradingSession>(form,"authorization");approval.Authorize("12345678901",DateTime.UtcNow);
            Set(form,"monitoring",true);CashReply(form,4555666);
            byte[] previous=File.ReadAllBytes(report);
            if(windows) {
                using(var locked=new FileStream(report,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    Call(form,"Safe",new Action(()=>Call(form,"PrepareReports")));
                Check(Get<bool>(form,"reportWriteFault"),"exclusive-open CSV failure is reported by the real form on Windows");
            } else {
                string backup=report+".bak";if(File.Exists(backup))File.Delete(backup);Directory.CreateDirectory(backup);
                Call(form,"Safe",new Action(()=>Call(form,"PrepareReports")));
                Check(Get<bool>(form,"reportWriteFault"),"checked CSV storage failure is reported by the real form on Linux");
                Directory.Delete(backup);
            }
            Check(File.ReadAllBytes(report).SequenceEqual(previous),"failed atomic report replacement preserves the last complete CSV");
            Check(!approval.Active&&!Get<bool>(form,"monitoring")&&Get<object>(form,"credentials")==null,"report write failure stops unattended operation and revokes memory-only approval");
            Check(Get<Label>(form,"reportStatus").Text.Contains("저장 실패")&&!Get<bool>(form,"reportSaving"),"failed save leaves explicit error status and releases save guard");
            Check(!Directory.GetFiles(Path.GetDirectoryName(report),"*.tmp-*").Any(),"report failure cleans temporary files without retrying during stop");
            form.Close();
            CheckSettingsLeaseReleased(data,"closing the report form releases its exclusive settings lease");
        }
        string[] beforeRestart=Reports(data);
        using(var restarted=NewForm()) {
            string folder=(string)Call(restarted,"PrepareReports");
            Check(beforeRestart.Length==1&&Directory.GetFiles(folder,"account-report-*.csv").Length==1,"fresh application discovers previous account report directory");
            Check(Reports(data).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(beforeRestart.OrderBy(x=>x,StringComparer.Ordinal)),"finding prior reports does not create a new empty account CSV");
            Check(Get<object>(restarted,"credentials")==null&&Get<string>(restarted,"boundKey")==null&&!Get<LiveTradingSession>(restarted,"authorization").Active,"prior report discovery establishes no current account or live approval");
            Check(Get<DataGridView>(restarted,"holdingsGrid").Rows.Count==0&&Get<IndiApi>(restarted,"api")==null,"restart does not treat report contents as current broker positions");
            Check(Get<Label>(restarted,"reportStatus").Text.Contains("이전 보고서"),"restart distinguishes previous file from a current account snapshot");
            restarted.Close();
            CheckSettingsLeaseReleased(data,"closing the restarted form releases its exclusive settings lease");
        }
    }

    [STAThread] static int Main(string[] args) {
        string resultPath=args.Length>0?Path.GetFullPath(args[0]):null;
        string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShinhanNativeTrader");
        bool ownsData=false;int exit=0;
        try {
            bool windows=Environment.OSVersion.Platform==PlatformID.Win32NT;
            bool isolatedLinux=args.Contains("--isolated-linux-smoke")&&data.StartsWith("/tmp/shinhan-report-ui-",StringComparison.Ordinal);
            if(!(windows&&Environment.GetEnvironmentVariable("GITHUB_ACTIONS")=="true")&&!isolatedLinux)
                throw new InvalidOperationException("Report UI tests require a fresh GitHub Actions Windows profile or explicitly isolated Linux smoke directory.");
            if(Directory.Exists(data)||File.Exists(data))throw new InvalidOperationException("Refusing to touch an existing application data path.");
            if(resultPath==null||resultPath.StartsWith(data+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An external report output path is required.");
            // Leave the path absent until the application constructor creates it.
            ownsData=true;
            CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            int screenshotIndex=Array.IndexOf(args,"--screenshot");
            Run(data,screenshotIndex>=0?Path.GetFullPath(args[screenshotIndex+1]):null,windows);
            Results.AppendLine("PASS "+assertions+" report UI assertions; synthetic responses only, no actual Indi server or money orders.");
            if(!windows)Results.AppendLine("Windows exclusive-open locking is not verified by the Linux storage-error smoke test.");
        }catch(Exception error){exit=1;Results.AppendLine("FAIL "+error);}
        finally {
            if(ownsData&&Directory.Exists(data))try{Directory.Delete(data,true);}catch(Exception error){exit=1;Results.AppendLine("FAIL test profile cleanup: "+error);}
            Console.Write(Results.ToString());
            if(resultPath!=null)try{Directory.CreateDirectory(Path.GetDirectoryName(resultPath));File.WriteAllText(resultPath,Results.ToString(),Encoding.UTF8);}
                catch(Exception error){Console.Error.WriteLine(error);exit=1;}
        }
        return exit;
    }
}
