using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Synthetic windows and disposable child processes only; no Indi installation is touched.
internal static class UipiTests {
    const int WmCopyData = 0x004A, WmClose = 0x0010;
    const int LowIntegrity = 0x1000, HighIntegrity = 0x3000;
    const uint TokenQuery = 0x0008, TokenAllAccess = 0x000F01FF;
    const uint DisableMaxPrivilege = 0x1, SeGroupIntegrity = 0x20;
    const uint CreateNoWindow = 0x08000000;
    const uint WaitObject0 = 0, WaitTimeout = 258;
    const int TokenIntegrityLevel = 25, ErrorAccessDenied = 5;
    const int ChildTimeoutMilliseconds = 20000;
    const int PayloadTag = 0x55495049;
    const string Payload = "synthetic-uipi-copydata";
    static readonly List<string> Report = new List<string>();
    static int checks;

    [STAThread]
    static int Main(string[] args) {
        if(args.Length > 0 && (args[0] == "--equal-child" || args[0] == "--low-child"))
            return RunChild(args);
        int exit = 0;
        try { Run(); Report.Add("PASS: " + checks + " actual Windows UIPI assertions; pointerBytes=" + IntPtr.Size); }
        catch(Exception ex) { exit = 1; Report.Add("FAIL: " + ex); }
        Report.Add("Synthetic Windows WM_COPYDATA transport only. Indi OCX, login, server readiness and live orders: NOT TESTED.");
        string output = string.Join(Environment.NewLine,Report) + Environment.NewLine;
        Console.Write(output);
        if(args.Length > 0) {
            try {
                string path = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path,output,Encoding.UTF8);
            } catch(Exception ex) { Console.Error.WriteLine(ex); return 2; }
        }
        return exit;
    }

    static void Check(bool pass, string message) {
        checks++;
        if(!pass) throw new InvalidOperationException(message);
        Report.Add("PASS " + message);
    }

    static void Run() {
        Check(Environment.OSVersion.Platform == PlatformID.Win32NT,"fixture is running on Windows");
        Check(IntPtr.Size == 4,"fixture is x86, matching the Indi OCX process architecture");
        int parentIntegrity = CurrentIntegrity();
        Check(parentIntegrity >= HighIntegrity,"receiver process is elevated/high integrity (RID=" + parentIntegrity + "); launch from an elevated shell");
        string executable = Assembly.GetExecutingAssembly().Location;
        uint parentId = GetCurrentProcessId();
        using(var receiver = new Receiver()) {
            receiver.Start();
            uint windowProcessId;
            uint receiverThreadId = GetWindowThreadProcessId(receiver.Handle,out windowProcessId);
            Check(IsWindow(receiver.Handle) && receiverThreadId != 0 && windowProcessId == parentId,
                "synthetic receiver HWND belongs to this elevated process");
            // CreateProcessAsUser can otherwise select a different, noninteractive desktop.
            string desktop = ObjectName(GetProcessWindowStation()) + "\\" + ObjectName(GetThreadDesktop(receiverThreadId));
            string childArguments = receiver.Handle.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + parentId.ToString(CultureInfo.InvariantCulture) + " " + parentIntegrity.ToString(CultureInfo.InvariantCulture);
            using(var equalChild = StartEqualChild(executable,"--equal-child " + childArguments,desktop)) {
                uint exitCode = equalChild.WaitForExit(ChildTimeoutMilliseconds);
                Check(exitCode == 0,"separate equal/high-integrity child verifies its token and receives WM_COPYDATA result 73" + ChildFailure(exitCode));
            }
            Check(receiver.Failure == null && receiver.MessageCount == 1,"pumped STA receiver processed exactly one equal-integrity WM_COPYDATA request");
            IntPtr lowToken = CreateLowToken();
            try {
                Check(IntegrityOf(lowToken) == LowIntegrity,"restricted primary token has the Low mandatory integrity label");
                string launchApi;
                using(var lowChild = StartLowChild(lowToken,executable,"--low-child " + childArguments,desktop,out launchApi)) {
                    Report.Add("INFO low-integrity child launched by " + launchApi);
                    uint exitCode = lowChild.WaitForExit(ChildTimeoutMilliseconds);
                    Check(exitCode == 0,"separate child verifies its actual Low integrity and receives WM_COPYDATA result 0, ERROR_ACCESS_DENIED 5" + ChildFailure(exitCode));
                }
            } finally { CloseHandle(lowToken); }
            Check(receiver.Failure == null && receiver.MessageCount == 1,"UIPI blocked the lower-integrity send before it reached the high-integrity receiver");
        }
        Check(true,"receiver message loop and both child processes have been shut down");
    }

    // A successful child exit proves the observations below; failure codes retain the failed assertion.
    static int RunChild(string[] args) {
        try {
            if(args.Length != 4 || Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 4) return 31;
            IntPtr target = new IntPtr(long.Parse(args[1],CultureInfo.InvariantCulture));
            uint expectedProcessId = uint.Parse(args[2],CultureInfo.InvariantCulture);
            int expectedHighIntegrity = int.Parse(args[3],CultureInfo.InvariantCulture);
            bool lower = args[0] == "--low-child";
            int actualIntegrity = CurrentIntegrity();
            if(actualIntegrity != (lower ? LowIntegrity : expectedHighIntegrity)) return 32;
            if(!IsWindow(target)) return 33;
            uint targetProcessId;
            if(GetWindowThreadProcessId(target,out targetProcessId) == 0 || targetProcessId != expectedProcessId) return 34;
            IntPtr payload = Marshal.StringToHGlobalUni(Payload);
            try {
                var copyData = new CopyData { Data = new IntPtr(PayloadTag), ByteCount = (Payload.Length + 1) * 2, Buffer = payload };
                SetLastError(0);
                IntPtr result = SendMessageW(target,WmCopyData,IntPtr.Zero,ref copyData);
                int error = Marshal.GetLastWin32Error();
                if(lower) {
                    if(result != IntPtr.Zero) return 35;
                    if(error != ErrorAccessDenied) return 36;
                } else {
                    if(result != new IntPtr(73)) return 37;
                }
            } finally { Marshal.FreeHGlobal(payload); }
            return 0;
        } catch { return 39; }
    }

    static string ChildFailure(uint exitCode) {
        if(exitCode == 0) return "";
        string detail;
        switch(exitCode) {
            case 31: detail="invalid child arguments, platform or architecture"; break;
            case 32: detail="child's actual integrity differs from required integrity"; break;
            case 33: detail="receiver HWND is invalid"; break;
            case 34: detail="receiver HWND does not belong to the expected parent"; break;
            case 35: detail="lower-integrity SendMessage returned a nonzero value"; break;
            case 36: detail="lower-integrity SendMessage did not set ERROR_ACCESS_DENIED (5)"; break;
            case 37: detail="equal-integrity SendMessage did not return 73"; break;
            case 39: detail="child encountered an exception"; break;
            default: detail="child failed during startup or execution"; break;
        }
        return " (exit=" + exitCode.ToString(CultureInfo.InvariantCulture) + ": " + detail + ")";
    }

    static int CurrentIntegrity() {
        IntPtr token;
        if(!OpenProcessToken(GetCurrentProcess(),TokenQuery,out token)) throw Error("OpenProcessToken(query)");
        try { return IntegrityOf(token); } finally { CloseHandle(token); }
    }

    static int IntegrityOf(IntPtr token) {
        int length;
        GetTokenInformation(token,TokenIntegrityLevel,IntPtr.Zero,0,out length);
        if(length <= 0) throw Error("GetTokenInformation(size)");
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try {
            if(!GetTokenInformation(token,TokenIntegrityLevel,buffer,length,out length)) throw Error("GetTokenInformation(integrity)");
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if(count == 0) throw new InvalidOperationException("Integrity SID has no RID.");
            return Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)(count - 1)));
        } finally { Marshal.FreeHGlobal(buffer); }
    }

    static IntPtr CreateLowToken() {
        IntPtr source;
        if(!OpenProcessToken(GetCurrentProcess(),TokenAllAccess,out source)) throw Error("OpenProcessToken(restrict)");
        IntPtr restricted = IntPtr.Zero;
        try {
            if(!CreateRestrictedToken(source,DisableMaxPrivilege,0,IntPtr.Zero,0,IntPtr.Zero,0,IntPtr.Zero,out restricted)) throw Error("CreateRestrictedToken(DISABLE_MAX_PRIVILEGE)");
            IntPtr sid;
            if(!ConvertStringSidToSidW("S-1-16-4096",out sid)) throw Error("ConvertStringSidToSidW(Low)");
            try {
                int labelSize = Marshal.SizeOf(typeof(MandatoryLabel));
                int sidSize = checked((int)GetLengthSid(sid));
                int totalSize = checked(labelSize + sidSize);
                IntPtr label = Marshal.AllocHGlobal(totalSize);
                try {
                    IntPtr embeddedSid = IntPtr.Add(label,labelSize);
                    if(!CopySid((uint)sidSize,embeddedSid,sid)) throw Error("CopySid(Low)");
                    Marshal.StructureToPtr(new MandatoryLabel { Label = new SidAndAttributes { Sid = embeddedSid, Attributes = SeGroupIntegrity } },label,false);
                    if(!SetTokenInformation(restricted,TokenIntegrityLevel,label,totalSize)) throw Error("SetTokenInformation(Low integrity)");
                } finally { Marshal.FreeHGlobal(label); }
            } finally { LocalFree(sid); }
            IntPtr result = restricted;
            restricted = IntPtr.Zero;
            return result;
        } finally {
            if(restricted != IntPtr.Zero) CloseHandle(restricted);
            CloseHandle(source);
        }
    }

    static string ObjectName(IntPtr handle) {
        if(handle == IntPtr.Zero) throw Error("Get desktop/window station handle");
        int bytes;
        GetUserObjectInformationW(handle,2,null,0,out bytes); // UOI_NAME; lengths are bytes.
        if(bytes <= 0) throw Error("GetUserObjectInformationW(name size)");
        var name = new StringBuilder((bytes + 1) / 2);
        if(!GetUserObjectInformationW(handle,2,name,name.Capacity * 2,out bytes)) throw Error("GetUserObjectInformationW(name)");
        return name.ToString();
    }

    static ChildProcess StartEqualChild(string executable, string arguments, string desktop) {
        var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = desktop };
        ProcessInformation process;
        var command = new StringBuilder("\"" + executable + "\" " + arguments);
        if(!CreateProcessW(executable,command,IntPtr.Zero,IntPtr.Zero,false,CreateNoWindow,IntPtr.Zero,Path.GetDirectoryName(executable),ref startup,out process))
            throw Error("CreateProcessW(equal-integrity child)");
        CloseHandle(process.Thread);
        return new ChildProcess(process.Process);
    }

    static ChildProcess StartLowChild(IntPtr token, string executable, string arguments, string desktop, out string launchApi) {
        var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)), Desktop = desktop };
        ProcessInformation process;
        string commandLine = "\"" + executable + "\" " + arguments;
        if(CreateProcessAsUserW(token,executable,new StringBuilder(commandLine),IntPtr.Zero,IntPtr.Zero,false,CreateNoWindow,IntPtr.Zero,Path.GetDirectoryName(executable),ref startup,out process)) {
            launchApi = "CreateProcessAsUserW";
        } else {
            int asUserError = Marshal.GetLastWin32Error();
            if(!CreateProcessWithTokenW(token,0,executable,new StringBuilder(commandLine),CreateNoWindow,IntPtr.Zero,Path.GetDirectoryName(executable),ref startup,out process))
                throw new Win32Exception(Marshal.GetLastWin32Error(),"CreateProcessWithTokenW fallback failed after CreateProcessAsUserW error " + asUserError);
            launchApi = "CreateProcessWithTokenW (CreateProcessAsUserW error " + asUserError + ")";
        }
        CloseHandle(process.Thread);
        return new ChildProcess(process.Process);
    }

    static Win32Exception Error(string operation) { return new Win32Exception(Marshal.GetLastWin32Error(),operation); }

    sealed class ChildProcess : IDisposable {
        IntPtr handle;
        public ChildProcess(IntPtr handle) { this.handle=handle; }
        public uint WaitForExit(int milliseconds) {
            uint wait = WaitForSingleObject(handle,(uint)milliseconds);
            if(wait == WaitTimeout) throw new TimeoutException("Child exceeded " + milliseconds + " ms; it will be terminated.");
            if(wait != WaitObject0) throw Error("WaitForSingleObject(child)");
            uint exit;
            if(!GetExitCodeProcess(handle,out exit)) throw Error("GetExitCodeProcess");
            return exit;
        }
        public void Dispose() {
            if(handle == IntPtr.Zero) return;
            try {
                if(WaitForSingleObject(handle,0) == WaitTimeout) {
                    if(!TerminateProcess(handle,124)) throw Error("TerminateProcess(timed-out child)");
                    if(WaitForSingleObject(handle,5000) != WaitObject0) throw new TimeoutException("Terminated child did not exit within 5000 ms.");
                }
            } finally { CloseHandle(handle); handle=IntPtr.Zero; }
        }
    }

    sealed class Receiver : IDisposable {
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        Thread thread;
        IntPtr handle;
        int count;
        volatile Exception failure;
        public IntPtr Handle { get { return handle; } }
        public int MessageCount { get { return Volatile.Read(ref count); } }
        public Exception Failure { get { return failure; } }
        public void Start() {
            thread = new Thread(() => {
                ReceiverWindow window = null;
                try {
                    window = new ReceiverWindow(this);
                    window.CreateHandle(new CreateParams { Caption="Shinhan-UIPI-Fixture-"+Guid.NewGuid().ToString("N") });
                    handle=window.Handle;
                    ready.Set();
                    Application.Run();
                } catch(Exception ex) { failure=ex; ready.Set(); }
                finally { if(window != null && window.Handle != IntPtr.Zero) window.DestroyHandle(); }
            });
            thread.IsBackground=true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if(!ready.WaitOne(10000)) throw new TimeoutException("Receiver window did not become ready within 10000 ms.");
            if(failure != null) throw new InvalidOperationException("Receiver window failed.",failure);
        }
        public void Dispose() {
            try {
                if(thread != null && thread.IsAlive) {
                    if(handle != IntPtr.Zero && !PostMessageW(handle,WmClose,IntPtr.Zero,IntPtr.Zero)) throw Error("PostMessageW(close receiver)");
                    if(!thread.Join(5000)) throw new TimeoutException("Receiver message loop did not stop within 5000 ms.");
                }
            } finally { if(thread == null || !thread.IsAlive) ready.Dispose(); }
        }
        sealed class ReceiverWindow : NativeWindow {
            readonly Receiver owner;
            public ReceiverWindow(Receiver owner) { this.owner=owner; }
            protected override void WndProc(ref Message message) {
                if(message.Msg == WmClose) {
                    DestroyHandle();
                    Application.ExitThread();
                    return;
                }
                if(message.Msg == WmCopyData) {
                    try {
                        var data = (CopyData)Marshal.PtrToStructure(message.LParam,typeof(CopyData));
                        if(data.Data != new IntPtr(PayloadTag) || data.ByteCount != (Payload.Length + 1) * 2 || data.Buffer == IntPtr.Zero || Marshal.PtrToStringUni(data.Buffer,Payload.Length) != Payload)
                            throw new InvalidOperationException("Received unexpected WM_COPYDATA payload.");
                        Interlocked.Increment(ref owner.count);
                        message.Result=new IntPtr(73);
                    } catch(Exception ex) { owner.failure=ex; message.Result=IntPtr.Zero; }
                    return;
                }
                base.WndProc(ref message);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CopyData { public IntPtr Data; public int ByteCount; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct MandatoryLabel { public SidAndAttributes Label; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct StartupInfo {
        public int Size;
        public string Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
    [DllImport("kernel32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetExitCodeProcess(IntPtr process,out uint exitCode);
    [DllImport("kernel32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool TerminateProcess(IntPtr process,uint exitCode);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,[MarshalAs(UnmanagedType.Bool)] bool inheritHandles,uint creationFlags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInformation process);
    [DllImport("advapi32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool OpenProcessToken(IntPtr process,uint desiredAccess,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetTokenInformation(IntPtr token,int informationClass,IntPtr information,int length,out int requiredLength);
    [DllImport("advapi32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetTokenInformation(IntPtr token,int informationClass,IntPtr information,int length);
    [DllImport("advapi32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateRestrictedToken(IntPtr existing,uint flags,uint disableSidCount,IntPtr disableSids,uint deletePrivilegeCount,IntPtr deletePrivileges,uint restrictedSidCount,IntPtr restrictedSids,out IntPtr token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ConvertStringSidToSidW(string text,out IntPtr sid);
    [DllImport("advapi32.dll")] static extern uint GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CopySid(uint length,IntPtr target,IntPtr source);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid,uint index);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcessAsUserW(IntPtr token,string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,[MarshalAs(UnmanagedType.Bool)] bool inheritHandles,uint creationFlags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInformation process);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CreateProcessWithTokenW(IntPtr token,uint logonFlags,string application,StringBuilder command,uint creationFlags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInformation process);
    [DllImport("user32.dll",SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetUserObjectInformationW(IntPtr handle,int index,StringBuilder information,int length,out int requiredLength);
    [DllImport("user32.dll",SetLastError=true)] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SendMessageW(IntPtr window,int message,IntPtr wParam,ref CopyData lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostMessageW(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
}
