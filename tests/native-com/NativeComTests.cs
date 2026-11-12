using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ShinhanNativeTrader;

// Standalone runner links the production subscription helper; no production EXE or Indi broker.
internal static class NativeComTests {
    static readonly Guid EventInterface = new Guid("802FE718-F37A-4A88-904F-389B5B597131");
    const int DataId = 11, SystemId = 17, ShortId = 29;
    static readonly List<string> Report = new List<string>();
    static int checks;
    [DllImport("NativeComFixture.dll", CallingConvention=CallingConvention.Cdecl, ExactSpelling=true)]
    static extern int CreateFixture(out IntPtr value);
    [DllImport("NativeComFixture.dll", CallingConvention=CallingConvention.Cdecl, ExactSpelling=true)]
    static extern int GetLiveFixtureCount();
    static void Check(bool pass, string message) {
        checks++;
        if(!pass) throw new Exception(message);
        Report.Add("PASS " + message);
    }
    static object Invoke(object source, string method, params object[] args) {
        return source.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, source, args, CultureInfo.InvariantCulture);
    }
    static int Number(object source, string method) { return Convert.ToInt32(Invoke(source,method),CultureInfo.InvariantCulture); }
    [STAThread]
    static int Main(string[] args) {
        int exit = 0;
        try { Run(); Report.Add("PASS: " + checks + " native COM assertions; pointerBytes=" + IntPtr.Size); }
        catch(Exception ex) { exit = 1; Report.Add("FAIL: " + ex); }
        Report.Add("Synthetic native COM fixture only. Indi OCX, server readiness and live orders: NOT TESTED.");
        var output = string.Join(Environment.NewLine,Report) + Environment.NewLine;
        Console.Write(output);
        if(args.Length > 0) {
            try { var path = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path,output,Encoding.UTF8); }
            catch(Exception ex) { Console.Error.WriteLine(ex); return 2; }
        }
        return exit;
    }
    static void Run() {
        Check(IntPtr.Size == 4,"test process is x86");
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,"test process owns an STA");
        Check(GetLiveFixtureCount() == 0,"native fixture starts without live objects");
        IntPtr pointer; Marshal.ThrowExceptionForHR(CreateFixture(out pointer));
        object source;
        try { source = Marshal.GetObjectForIUnknown(pointer); } finally { Marshal.Release(pointer); }
        Check(Marshal.IsComObject(source),"event source is a native COM RCW, not a managed fake");
        var subscriptions = new ComEventSubscriptions(source,EventInterface);
        try {
            Check(Number(source,"GetSinkCount") == 0,"no connection point sink before bind");
            var received = new List<int>();
            int systemValue=0, shortValue=0, callbackThread=0;
            bool insideRequest=false, reentered=false;
            string buffer=null;
            Action<int> data = value => {
                received.Add(value); callbackThread=Thread.CurrentThread.ManagedThreadId;
                if(insideRequest) { reentered=true; buffer=Convert.ToString(Invoke(source,"GetBufferText"),CultureInfo.InvariantCulture); }
            };
            subscriptions.AddInt32Callback(DataId,VarEnum.VT_I4,data);
            subscriptions.AddInt32Callback(SystemId,VarEnum.VT_INT,value => { systemValue=value; });
            subscriptions.AddInt32Callback(ShortId,VarEnum.VT_I2,value => { shortValue=value; });
            Check(subscriptions.Count == 3,"production helper roots all three exact adapted delegates");
            Check(Number(source,"GetSinkCount") == 1,"three DISPIDs share one successfully advised native sink");
            Invoke(source,"EmitData",41);
            Check(received.Count == 1 && received[0] == 41,"native IDispatch callback reaches the rooted ReceiveData delegate");
            Check(callbackThread == Thread.CurrentThread.ManagedThreadId,"native callback executes on the owning STA");
            Invoke(source,"EmitSystem",91);
            Check(systemValue == 91,"separate native system DISPID reaches its delegate");
            Invoke(source,"EmitShort",1234);
            Check(shortValue == 1234,"VT_I2 event argument reaches a short delegate");
            insideRequest=true;
            int requestId=Number(source,"RequestData");
            insideRequest=false;
            Check(requestId == 73 && reentered && received[received.Count-1] == requestId,"ReceiveData can reenter before RequestData returns a positive ID");
            Check(buffer == "copied-during-native-callback","native receive buffer can be copied during reentrant callback");
            Check(Convert.ToString(Invoke(source,"GetBufferText"),CultureInfo.InvariantCulture) == "changed-after-native-callback","buffer changes after callback, demonstrating why delayed reads lose data");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Invoke(source,"EmitData",52);
            Check(received[received.Count-1] == 52,"event subscription survives forced GC while delegates are rooted");
            subscriptions.Dispose(); subscriptions.Dispose();
            Check(subscriptions.Count == 0,"production helper clears roots and permits repeated disposal");
            int before=received.Count;
            Invoke(source,"EmitData",66); Invoke(source,"EmitSystem",92); Invoke(source,"EmitShort",42);
            Check(received.Count == before && systemValue == 91 && shortValue == 1234,"disposed production helper receives no further callbacks");
            Check(Number(source,"GetSinkCount") == 0,"production helper disposal unadvises the final native sink");
            bool disposedRejected=false;
            try { subscriptions.AddInt32Callback(DataId,VarEnum.VT_I4,data); }
            catch(ObjectDisposedException) { disposedRejected=true; }
            Check(disposedRejected,"disposed production helper rejects a new subscription");
            using(var fresh = new ComEventSubscriptions(source,EventInterface)) {
                fresh.AddInt32Callback(DataId,VarEnum.VT_INT,data);
                Invoke(source,"EmitData",74);
                Check(fresh.Count == 1 && received[received.Count-1] == 74,"new production helper can reconnect after exact detach");
            }
            Check(Number(source,"GetSinkCount") == 0,"reconnected production helper also releases its native sink");
            bool refused=false;
            using(var wrong = new ComEventSubscriptions(source,Guid.NewGuid())) {
                try { wrong.AddInt32Callback(DataId,VarEnum.VT_I4,data); }
                catch(COMException) { refused=true; }
                Check(refused && wrong.Count == 0 && Number(source,"GetSinkCount") == 0,"wrong outgoing IID is refused without rooting a failed subscription");
            }
            bool unsupported=false;
            using(var invalid = new ComEventSubscriptions(source,EventInterface)) {
                try { invalid.AddInt32Callback(DataId,VarEnum.VT_BSTR,data); }
                catch(ArgumentException) { unsupported=true; }
                Check(unsupported && invalid.Count == 0 && Number(source,"GetSinkCount") == 0,"unsupported argument type is rejected before native Advise");
            }
        } finally {
            subscriptions.Dispose();
            Marshal.FinalReleaseComObject(source);
        }
        Check(GetLiveFixtureCount() == 0,"native fixture releases after exact delegate detach and RCW disposal");
    }
}
