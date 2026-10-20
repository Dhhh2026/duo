// 全部 Win32 P/Invoke 声明
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- hid.dll ----
    [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetAttributes(SafeFileHandle h, out HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
            NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    // ---- setupapi.dll ----
    public const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    [DllImport("setupapi.dll", SetLastError = true)] public static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr hwndParent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, ref SP_DEVICE_INTERFACE_DETAIL_DATA detail, int size, ref int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct SP_DEVICE_INTERFACE_DETAIL_DATA { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string DevicePath; }

    // ---- kernel32 重叠 IO（读/写均带超时，杜绝 node-hid 式冻结）----
    public const int GENERIC_READ = unchecked((int)0x80000000), GENERIC_WRITE = 0x40000000;
    public const int FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    public const int ERROR_IO_PENDING = 997, WAIT_OBJECT_0 = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern SafeFileHandle CreateFile(string path, int access, int share, IntPtr security, int creation, int flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int bytes, out int read, ref OVERLAPPED ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, byte[] buffer, int bytes, out int written, ref OVERLAPPED ov);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetOverlappedResult(SafeFileHandle h, ref OVERLAPPED ov, out int transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CancelIo(SafeFileHandle h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateEvent(IntPtr attr, bool manualReset, bool initial, string name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ResetEvent(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern int WaitForSingleObject(IntPtr h, int ms);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED { public IntPtr Internal, InternalHigh; public int Offset, OffsetHigh; public IntPtr hEvent; }
}
