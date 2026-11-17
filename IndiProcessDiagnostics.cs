using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShinhanNativeTrader {
    // Metadata only. Never changes another process, its message filter or its token.
    public static class IndiProcessDiagnostics {
        const uint ProcessQueryLimitedInformation=0x1000,TokenQuery=0x0008;
        const int TokenIntegrityLevel=25;
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]
        static extern IntPtr FindWindow(string className,string windowName);
        [DllImport("user32.dll",SetLastError=true)]
        static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
        [DllImport("kernel32.dll",SetLastError=true)]
        static extern IntPtr OpenProcess(uint access,bool inherit,uint processId);
        [DllImport("advapi32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool GetTokenInformation(IntPtr token,int informationClass,IntPtr data,uint length,out uint required);
        [DllImport("advapi32.dll")]
        static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")]
        static extern IntPtr GetSidSubAuthority(IntPtr sid,uint index);
        [DllImport("kernel32.dll")]
        [return:MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);

        public static int? IntegrityLevel(uint processId) {
            IntPtr process=IntPtr.Zero,token=IntPtr.Zero,buffer=IntPtr.Zero;
            try {
                process=OpenProcess(ProcessQueryLimitedInformation,false,processId);
                if(process==IntPtr.Zero||!OpenProcessToken(process,TokenQuery,out token))return null;
                uint length;GetTokenInformation(token,TokenIntegrityLevel,IntPtr.Zero,0,out length);
                if(length==0||length>65536)return null;
                buffer=Marshal.AllocHGlobal((int)length);
                if(!GetTokenInformation(token,TokenIntegrityLevel,buffer,length,out length))return null;
                IntPtr sid=Marshal.ReadIntPtr(buffer);
                if(sid==IntPtr.Zero)return null;
                byte count=Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return count==0?(int?)null:Marshal.ReadInt32(GetSidSubAuthority(sid,(uint)count-1));
            } catch {return null;}
            finally {
                if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);
                if(token!=IntPtr.Zero)CloseHandle(token);
                if(process!=IntPtr.Zero)CloseHandle(process);
            }
        }
        public static string Describe() {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)return "Indi IPC integrity: Windows only.";
            int? current=IntegrityLevel((uint)Process.GetCurrentProcess().Id);
            IntPtr window=FindWindow("GiExpertMainApp-CLASS",null);
            if(window==IntPtr.Zero)return "Indi IPC integrity: app="+Label(current)+"; receiver window not found.";
            uint processId;GetWindowThreadProcessId(window,out processId);
            int? receiver=IntegrityLevel(processId);
            bool? lower=current.HasValue&&receiver.HasValue?(bool?)(current.Value<receiver.Value):null;
            return "Indi IPC integrity: app="+Label(current)+"; receiver="+Label(receiver)+
                "; appBelowReceiver="+(lower.HasValue?lower.Value.ToString():"unknown")+"; receiverPid="+processId+".";
        }
        static string Label(int? value) {
            if(!value.HasValue)return "unknown";
            int level=value.Value;
            string label=level>=0x4000?"System":level>=0x3000?"High":level>=0x2000?"Medium":"Low";
            return label+"("+level+")";
        }
    }
}
