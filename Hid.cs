// HID 封装：枚举指定 VID/PID 的全部集合（含 usagePage/usage），重叠 IO 读写。
// 枚举时用 access=0 打开（键盘/鼠标集合被系统独占也能读属性），仅命令通道才用读写打开。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class Hid {
    public class Interface {
        public string Path;
        public ushort UsagePage, Usage;
    }

    public static List<Interface> Enumerate(ushort vid, ushort pid) {
        var list = new List<Interface>();
        Guid guid; Native.HidD_GetHidGuid(out guid);
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try {
            var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA)) };
            int detailSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DETAIL_DATA));
            for (int i = 0; Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++) {
                int need = 0;
                var detail = new Native.SP_DEVICE_INTERFACE_DETAIL_DATA { cbSize = IntPtr.Size == 8 ? 8 : 6 };
                if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, ref detail, detailSize, ref need, IntPtr.Zero)) continue;
                var it = Probe(detail.DevicePath, vid, pid);
                if (it != null) list.Add(it);
            }
        } finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    static Interface Probe(string path, ushort vid, ushort pid) {
        using (var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero)) {
            if (h.IsInvalid) return null;
            Native.HIDD_ATTRIBUTES attr;
            if (!Native.HidD_GetAttributes(h, out attr) || attr.VendorID != vid || attr.ProductID != pid) return null;
            var it = new Interface { Path = path };
            IntPtr pp;
            if (Native.HidD_GetPreparsedData(h, out pp)) {
                Native.HIDP_CAPS caps;
                if (Native.HidP_GetCaps(pp, out caps) == 0x110000) { it.UsagePage = caps.UsagePage; it.Usage = caps.Usage; }
                Native.HidD_FreePreparsedData(pp);
            }
            return it;
        }
    }

    public static Device Open(string path) {
        var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new Device(h);
    }

    public sealed class Device : IDisposable {
        readonly SafeFileHandle h;
        readonly IntPtr evt;
        public Device(SafeFileHandle handle) { h = handle; evt = Native.CreateEvent(IntPtr.Zero, true, false, null); }

        public bool Write(byte[] data) {
            var ov = new Native.OVERLAPPED { hEvent = evt };
            Native.ResetEvent(evt);
            int n;
            if (!Native.WriteFile(h, data, data.Length, out n, ref ov)) {
                if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;
                if (Native.WaitForSingleObject(evt, 2000) != Native.WAIT_OBJECT_0) { Native.CancelIo(h); return false; }
                if (!Native.GetOverlappedResult(h, ref ov, out n, false)) return false;
            }
            return true;
        }

        public int Read(byte[] buf, int ms) {
            var ov = new Native.OVERLAPPED { hEvent = evt };
            Native.ResetEvent(evt);
            int n;
            if (!Native.ReadFile(h, buf, buf.Length, out n, ref ov)) {
                if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return -1;
                if (Native.WaitForSingleObject(evt, ms) != Native.WAIT_OBJECT_0) { Native.CancelIo(h); Native.WaitForSingleObject(evt, 300); return -1; }
                if (!Native.GetOverlappedResult(h, ref ov, out n, false)) return -1;
            }
            return n;
        }

        public void Dispose() { h.Dispose(); if (evt != IntPtr.Zero) Native.CloseHandle(evt); }
    }

    // 单帧问答：写请求 → 等匹配帧；repokeMs 周期性重发（设备偶发丢帧），timeoutMs 总超时
    public static T Transact<T>(Device dev, byte[] report, Func<byte[], T> tryParse, int timeoutMs, int repokeMs) where T : class {
        int start = Environment.TickCount, deadline = start + timeoutMs;
        int nextWrite = start; // 立即首发
        var buf = new byte[65];
        while (Environment.TickCount - start < timeoutMs) {
            if (Environment.TickCount - nextWrite >= 0) {
                if (!dev.Write(report)) return null;
                nextWrite = Environment.TickCount + repokeMs;
            }
            int remain = deadline - Environment.TickCount;
            if (remain <= 0) break;
            if (dev.Read(buf, Math.Min(repokeMs, remain)) > 0) {
                var r = tryParse(buf);
                if (r != null) return r;
            }
        }
        return null;
    }
}
