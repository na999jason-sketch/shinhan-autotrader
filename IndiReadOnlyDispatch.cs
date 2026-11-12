using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ShinhanNativeTrader {
    // Read-only subset of the generated _DGiExpertControl dispatch contract. The IID,
    // DISPIDs, PreserveSig, VARIANT and Int16 signatures were compared with the
    // matching-IID public Interop metadata. Session initialization is exposed only
    // through a separate blank-credential method; no order API is declared.
    [ComImport, Guid("B4A0D92F-6683-4824-9C63-53F7DAD7F57C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IIndiReadOnlyDispatch {
        [DispId(1), PreserveSig]
        bool SetSingleData(short index,[MarshalAs(UnmanagedType.Struct)] object data);
        [DispId(3), PreserveSig]
        bool SetQueryName([MarshalAs(UnmanagedType.Struct)] object name);
        [DispId(4), PreserveSig]
        [return:MarshalAs(UnmanagedType.Struct)] object GetQueryName();
        [DispId(5), PreserveSig] short RequestData();
        [DispId(8), PreserveSig]
        [return:MarshalAs(UnmanagedType.Struct)] object GetSingleData(short index);
        [DispId(9), PreserveSig]
        [return:MarshalAs(UnmanagedType.Struct)] object GetMultiData(short row,short index);
        [DispId(13), PreserveSig] short GetMultiRowCount();
        [DispId(14), PreserveSig] short GetErrorState();
        [DispId(15), PreserveSig]
        [return:MarshalAs(UnmanagedType.Struct)] object GetErrorCode();
        [DispId(16), PreserveSig]
        [return:MarshalAs(UnmanagedType.Struct)] object GetErrorMessage();
        [DispId(17), PreserveSig] short GetCommState();
        [DispId(25), PreserveSig]
        bool StartIndi([MarshalAs(UnmanagedType.Struct)] object id,
            [MarshalAs(UnmanagedType.Struct)] object password,
            [MarshalAs(UnmanagedType.Struct)] object certificatePassword,
            [MarshalAs(UnmanagedType.Struct)] object starterPath);
    }

    public sealed class IndiReadOnlyDispatch {
        public static readonly Guid InterfaceId=new Guid("B4A0D92F-6683-4824-9C63-53F7DAD7F57C");
        readonly IIndiReadOnlyDispatch dispatch;
        public int? LastRequestNativeError {get;private set;}
        [DllImport("kernel32.dll",EntryPoint="GetLastError",SetLastError=false)]
        static extern uint ReadNativeError();
        [DllImport("kernel32.dll",EntryPoint="SetLastError",SetLastError=false)]
        static extern void ClearNativeError(uint value);
        public IndiReadOnlyDispatch(object nativeControl) {
            if(nativeControl==null)throw new ArgumentNullException("nativeControl");
            if(!Marshal.IsComObject(nativeControl))throw new ArgumentException("An installed native Indi COM control is required.","nativeControl");
            dispatch=(IIndiReadOnlyDispatch)nativeControl;
            ReadNativeError(); // Resolve the P/Invoke stub before capturing an actual request.
        }
        public bool Initialize(string starterPath) {
            if(string.IsNullOrWhiteSpace(starterPath)||!Path.IsPathRooted(starterPath)
                ||!string.Equals(Path.GetFileName(starterPath),"giexpertstarter.exe",StringComparison.OrdinalIgnoreCase)
                ||!File.Exists(starterPath))throw new ArgumentException("An existing installed giexpertstarter.exe path is required.","starterPath");
            return dispatch.StartIndi("","","",Path.GetFullPath(starterPath));
        }
        static short Index(object value) {
            if(value is short&&((short)value)>=0)return (short)value;
            if(value is int&&((int)value)>=0&&((int)value)<=short.MaxValue)return (short)(int)value;
            throw new ArgumentException("Indi field and row indices must be non-negative Int16 integers.");
        }
        static void Count(object[] args,int expected) {
            if(args.Length!=expected)throw new ArgumentException("Unexpected read-only Indi argument count.");
        }
        public object Invoke(string method,params object[] args) {
            args=args??new object[0];
            switch(method) {
                case "SetQueryName":
                    Count(args,1);
                    var query=args[0] as string;
                    if(query!="stock_mst"&&query!="TR_SCHART"&&query!="SC"&&query!="SABA200QB")
                        throw new InvalidOperationException("Only known read-only Indi queries are allowed.");
                    return dispatch.SetQueryName(query);
                case "SetSingleData":
                    Count(args,2);
                    if(!(args[1] is string))throw new ArgumentException("Read-only query input values must be strings.");
                    return dispatch.SetSingleData(Index(args[0]),args[1]);
                case "GetQueryName":Count(args,0);return dispatch.GetQueryName();
                case "RequestData":
                    Count(args,0);ClearNativeError(0);
                    short id=dispatch.RequestData();
                    LastRequestNativeError=unchecked((int)ReadNativeError());
                    return id;
                case "GetSingleData":Count(args,1);return dispatch.GetSingleData(Index(args[0]));
                case "GetMultiData":Count(args,2);return dispatch.GetMultiData(Index(args[0]),Index(args[1]));
                case "GetMultiRowCount":Count(args,0);return dispatch.GetMultiRowCount();
                case "GetErrorState":Count(args,0);return dispatch.GetErrorState();
                case "GetErrorCode":Count(args,0);return dispatch.GetErrorCode();
                case "GetErrorMessage":Count(args,0);return dispatch.GetErrorMessage();
                case "GetCommState":Count(args,0);return dispatch.GetCommState();
                default:throw new InvalidOperationException("Read-only Indi dispatch method is not allowed.");
            }
        }
    }
}
