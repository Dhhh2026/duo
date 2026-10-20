// 设备驱动：NUT75 键鼠（0x0C45:0xFEF9）+ AULA SC620 鼠标（0xA8A5:0x2255）
// 协议移植自 Electron 版实测成果：
//   NUT75 — usagePage 0xFF60，必须同时打开设备的全部 HID 集合才应答；句柄常驻缓存，出错后重开。
//           请求 64B [AA 10 30 00 00 00 01 ...]（写时前置 0x00），响应 [55 10]... 电量@25 充电@26
//   SC620 — usagePage 0xFF01 单通道，无需握手，每次轮询新开新关。
//           请求 64B [55 30 01 2E 2E ...]（写时前置 0x00），响应 [AA 30]... 电量@8
using System;
using System.Collections.Generic;
using System.Linq;

class Reading {
    public string Id;
    public bool Connected;
    public int Capacity;
    public bool? Charging; // null = 无硬件充电标志，需上层按趋势推断
}

static class Drivers {
    public class Def {
        public string Id, DefaultName, Accent;
        public ushort Vid, Pid, UsagePage;
    }
    public static readonly Def[] Defs = {
        new Def { Id = "nut75", Vid = 0x0C45, Pid = 0xFEF9, UsagePage = 0xFF60, DefaultName = "NUT75 键鼠", Accent = "blue" },
        new Def { Id = "sc620", Vid = 0xA8A5, Pid = 0x2255, UsagePage = 0xFF01, DefaultName = "AULA SC620", Accent = "green" },
    };

    static readonly object sync = new object();
    static List<KeyValuePair<string, Hid.Device>> nut75; // NUT75 常驻句柄（path → device）

    public static Reading Read(Def def) {
        var r = new Reading { Id = def.Id };
        try {
            var ifaces = Hid.Enumerate(def.Vid, def.Pid);
            if (ifaces.Count == 0) return r;
            return def.Id == "nut75" ? ReadNut75(ifaces) : ReadSc620(ifaces);
        } catch { return r; }
    }

    public static void DisposeAll() { lock (sync) InvalidateNut75(); }

    static void InvalidateNut75() {
        if (nut75 == null) return;
        foreach (var e in nut75) e.Value.Dispose();
        nut75 = null;
    }

    static byte[] Request(params byte[] head) {
        var b = new byte[65]; // [0] = report id 0x00（unnumbered report 前置）+ 64 字节负载
        Array.Copy(head, 0, b, 1, head.Length);
        return b;
    }

    // 读缓冲首字节可能是 report id(0) 也可能直接是数据：两种偏移都认
    static int Sig(byte[] b, byte s0, byte s1) {
        if (b[0] == s0 && b[1] == s1) return 0;
        if (b[0] == 0 && b[1] == s0 && b[2] == s1) return 1;
        return -1;
    }

    static Reading ReadNut75(List<Hid.Interface> ifaces) {
        lock (sync) {
            var miss = new Reading { Id = "nut75" };
            if (nut75 == null) {
                nut75 = new List<KeyValuePair<string, Hid.Device>>();
                foreach (var c in ifaces) { // 个别集合（键盘/鼠标）被独占打不开不致命
                    var d = Hid.Open(c.Path);
                    if (d != null) nut75.Add(new KeyValuePair<string, Hid.Device>(c.Path, d));
                }
            }
            var cmd = ifaces.FirstOrDefault(c => c.UsagePage == 0xFF60) ?? ifaces[0];
            var entry = nut75.FirstOrDefault(e => e.Key == cmd.Path);
            if (entry.Value == null) entry = nut75.FirstOrDefault();
            if (entry.Value == null) { InvalidateNut75(); return miss; }

            var result = Hid.Transact(entry.Value, Request(0xAA, 0x10, 0x30, 0, 0, 0, 0x01), b => {
                int o = Sig(b, 0x55, 0x10);
                if (o < 0) return null;
                int cap = b[o + 25];
                if (cap < 1 || cap > 100) return null;
                return new Reading { Id = "nut75", Connected = true, Capacity = cap, Charging = b[o + 26] != 0 };
            }, 4000, 600);
            if (result == null) InvalidateNut75(); // 拔出/句柄失效：下次轮询全新打开
            return result ?? miss;
        }
    }

    static Reading ReadSc620(List<Hid.Interface> ifaces) {
        var miss = new Reading { Id = "sc620" };
        var ch = ifaces.FirstOrDefault(c => c.UsagePage == 0xFF01) ?? ifaces[0];
        using (var dev = Hid.Open(ch.Path)) {
            if (dev == null) return miss;
            var result = Hid.Transact(dev, Request(0x55, 0x30, 0x01, 0x2E, 0x2E), b => {
                int o = Sig(b, 0xAA, 0x30);
                if (o < 0) return null;
                int cap = b[o + 8];
                if (cap > 100) return null;
                return new Reading { Id = "sc620", Connected = true, Capacity = cap, Charging = null };
            }, 3000, 500);
            return result ?? miss;
        }
    }
}
