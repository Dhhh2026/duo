// DUO 应用核心：托盘图标、轮询线程、充电推断、闪烁、自启动、Win11 托盘提升
// 相比 Electron 版的简化：
//   - 无 worker 进程：重叠 IO 自带超时，不存在 node-hid 原生冻结问题
//   - 无 IPC/预加载/CSP：单进程单消息循环，内存占用从 ~100MB 降到 ~20MB
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class DuoApp : ApplicationContext {
    public static readonly Color Green = Color.FromArgb(48, 209, 88);
    public static readonly Color Blue = Color.FromArgb(77, 165, 247);
    public static readonly Color Red = Color.FromArgb(255, 69, 58);
    public static readonly Color Gray = Color.FromArgb(154, 154, 160);

    public readonly Config Cfg = Config.Load();

    public class DevInfo { public bool Connected, Charging; public int Capacity = -1; public int Rise; public DateTime At; }

    readonly Dictionary<string, DevInfo> states = new Dictionary<string, DevInfo>();
    readonly List<NotifyIcon> trayIcons = new List<NotifyIcon>(); // 图标物理位置注册后固定，内容按 trayOrder 动态映射
    readonly Control pump; // UI 线程封送器（后台轮询 → UI）
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly System.Windows.Forms.Timer flashTimer = new System.Windows.Forms.Timer { Interval = 600 };
    Thread pollThread;
    volatile bool quitting, anyDown;
    volatile int refreshMs;
    int flashPhase;
    MainForm form;

    public event Action StateChanged;
    public event Action Polled; // 每读完一轮所有设备触发一次（主界面"刷新中"反馈靠它收尾）
    public int RefreshMs { get { return refreshMs; } }
    void Raise() { var h = StateChanged; if (h != null) h(); }

    // DUO_DEBUG=1 时在 exe 旁写 duo-debug.log（平时零日志）
    static readonly bool DEBUG = Environment.GetEnvironmentVariable("DUO_DEBUG") == "1";
    public static void Log(string msg) {
        if (!DEBUG) return;
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "duo-debug.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + msg + "\r\n"); } catch { }
    }

    public DuoApp(bool hidden) {
        refreshMs = Cfg.settings.refreshMs;
        pump = new Control();
        var unused = pump.Handle; // 强制创建句柄用于 Invoke
        flashTimer.Tick += (s, e) => { flashPhase ^= 1; RefreshTrays(); Raise(); };
        CreateTrays();
        StartPoll();
        ApplyAutostart(); // 每次启动刷新注册表路径（exe 移动后自愈）
        ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2500); PromoteTrays(); });
        ListenShow();
        Application.ThreadExit += (s, e) => Log("ThreadExit");
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("UNHANDLED " + e.ExceptionObject);
        Log("ctor done hidden=" + hidden);
        if (!hidden) ShowMain();
    }

    // ---- 设备状态 ----
    public List<Drivers.Def> OrderedDefs() {
        var list = new List<Drivers.Def>();
        foreach (var id in Cfg.settings.trayOrder) {
            var d = Drivers.Defs.FirstOrDefault(x => x.Id == id);
            if (d != null && !list.Contains(d)) list.Add(d);
        }
        foreach (var d in Drivers.Defs) if (!list.Contains(d)) list.Add(d);
        return list;
    }

    public DevInfo GetState(string id) {
        lock (states) {
            DevInfo st;
            if (states.TryGetValue(id, out st))
                return new DevInfo { Connected = st.Connected, Capacity = st.Capacity, Charging = st.Charging, At = st.At };
            return new DevInfo();
        }
    }

    public string DeviceLabel(Drivers.Def def) { return Cfg.DeviceName(def); }

    // 托盘视觉：文字/颜色/是否熄灭帧
    public void Visual(Drivers.Def def, out string text, out Color color, out bool blank) {
        var st = GetState(def.Id);
        text = "--"; color = Gray; blank = false;
        if (!st.Connected || st.Capacity < 0) return;
        text = st.Capacity.ToString();
        var s = Cfg.settings;
        if (s.flashThreshold > 0 && st.Capacity <= s.flashThreshold && !st.Charging) { color = Red; blank = flashPhase == 1; return; } // 0=关闭闪烁（低于 lowThreshold 只红不闪）
        color = (st.Charging || st.Capacity >= s.lowThreshold) ? (def.Accent == "blue" ? Blue : Green) : Red;
    }

    // ---- 轮询 ----
    void StartPoll() {
        pollThread = new Thread(() => {
            while (!quitting) {
                foreach (var def in Drivers.Defs) {
                    if (quitting) return;
                    var r = Drivers.Read(def);
                    Log("poll " + def.Id + " connected=" + r.Connected + " cap=" + r.Capacity);
                    ApplyReading(r);
                }
                if (!quitting) pump.BeginInvoke((Action)(() => { var p = Polled; if (p != null) p(); }));
                // 有设备断开时 10s 快速探测重连，否则按设定频率
                wake.WaitOne(anyDown ? Math.Min(refreshMs, 10000) : refreshMs);
            }
        }) { IsBackground = true };
        pollThread.Start();
    }

    void ApplyReading(Reading r) {
        lock (states) {
            DevInfo st;
            if (!states.TryGetValue(r.Id, out st)) states[r.Id] = st = new DevInfo();
            if (!r.Connected) {
                st.Connected = false; st.Charging = false; st.At = DateTime.Now;
            } else {
                bool charging; int rise = 0;
                if (r.Charging.HasValue) {
                    charging = r.Charging.Value;
                } else {
                    // SC620 无硬件标志：±1 抖动防误判，连续 2 次跳升才判充电，持平衰减、下降清零
                    bool prevNum = st.Connected && st.Capacity >= 0;
                    bool rising = prevNum && r.Capacity > st.Capacity;
                    bool flat = prevNum && r.Capacity == st.Capacity;
                    rise = rising ? st.Rise + 1 : flat ? Math.Max(0, st.Rise - 1) : 0;
                    charging = rise >= 2;
                }
                st.Connected = true; st.Capacity = r.Capacity; st.Charging = charging; st.Rise = rise; st.At = DateTime.Now;
            }
            anyDown = states.Values.Any(v => !v.Connected);
        }
        if (!quitting) pump.BeginInvoke((Action)(() => { RefreshTrays(); UpdateFlash(); Raise(); }));
    }

    public void RefreshNow() { wake.Set(); }

    // ---- 托盘 ----
    void CreateTrays() {
        // Win11 按 exe 记忆托盘图标槽位：销毁重建会回到原位置，所以顺序变化绝不重建图标，
        // 只重渲染内容。首次注册倒序创建（新图标插到最左），让用户顺序第一个落在最左边；
        // 创建序 i 的图标此后固定渲染 trayOrder[Count-1-i]
        foreach (var def in Enumerable.Reverse(OrderedDefs())) {
            var ni = new NotifyIcon { Visible = true, Text = DeviceLabel(def) };
            TrayRenderer.SetIcon(ni, TrayRenderer.RenderIcon("--", Gray, false));
            ni.Click += (s, e) => { if (((MouseEventArgs)e).Button == MouseButtons.Left) ShowMain(); };
            ni.ContextMenuStrip = BuildMenu();
            trayIcons.Add(ni);
        }
    }

    ContextMenuStrip BuildMenu() {
        var m = new ContextMenuStrip();
        m.Items.Add("打开主界面", null, (s, e) => ShowMain());
        m.Items.Add(new ToolStripSeparator());
        var auto = new ToolStripMenuItem("开机自启动");
        auto.Click += (s, e) => SetAutostart(!Cfg.settings.autostart);
        m.Items.Add(auto);
        var rate = new ToolStripMenuItem("刷新频率");
        foreach (var pair in new[] { new KeyValuePair<int, string>(10000, "10 秒"), new KeyValuePair<int, string>(30000, "30 秒"), new KeyValuePair<int, string>(60000, "1 分钟"), new KeyValuePair<int, string>(300000, "5 分钟") }) {
            var item = new ToolStripMenuItem(pair.Value) { Tag = pair.Key };
            item.Click += (s, e) => SetRefreshMs(pair.Key);
            rate.DropDownItems.Add(item);
        }
        m.Items.Add(rate);
        m.Items.Add("立即刷新", null, (s, e) => RefreshNow());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出（结束程序）", null, (s, e) => Exit());
        m.Opening += (s, e) => {
            auto.Checked = Cfg.settings.autostart;
            foreach (ToolStripMenuItem it in rate.DropDownItems) it.Checked = (int)it.Tag == refreshMs;
        };
        return m;
    }

    public void RefreshTrays() {
        var order = Cfg.settings.trayOrder;
        for (int i = 0; i < trayIcons.Count; i++) {
            int oi = trayIcons.Count - 1 - i; // 创建序 ↔ 用户顺序的映射
            if (oi < 0 || oi >= order.Count) continue;
            var def = Drivers.Defs.FirstOrDefault(x => x.Id == order[oi]);
            if (def == null) continue;
            string text; Color color; bool blank;
            Visual(def, out text, out color, out blank);
            TrayRenderer.SetIcon(trayIcons[i], TrayRenderer.RenderIcon(text, color, blank));
            var st = GetState(def.Id);
            trayIcons[i].Text = DeviceLabel(def) + " " + (st.Connected ? st.Capacity + "%" : "未连接") + (st.Charging ? "（充电中）" : "");
        }
    }

    public void RebuildTrays() {
        foreach (var ni in trayIcons) {
            ni.Visible = false;
            TrayRenderer.Destroy(ni.Icon);
            ni.Dispose();
        }
        trayIcons.Clear();
        CreateTrays();
        RefreshTrays();
    }

    void UpdateFlash() {
        bool need;
        lock (states) {
            var s = Cfg.settings;
            need = s.flashThreshold > 0 && states.Values.Any(v => v.Connected && !v.Charging && v.Capacity >= 0 && v.Capacity <= s.flashThreshold);
        }
        flashTimer.Enabled = need;
        if (!need) flashPhase = 0;
    }

    // ---- 设置变更 ----
    public void SetRefreshMs(int ms) {
        refreshMs = ms;
        Cfg.settings.refreshMs = ms;
        Cfg.Save();
        wake.Set();
        Raise();
    }

    public void SetAutostart(bool on) {
        Cfg.settings.autostart = on;
        Cfg.Save();
        ApplyAutostart();
        Raise();
    }

    public void UpdateThresholds(int low, int flash) {
        var s = Cfg.settings;
        s.lowThreshold = Math.Min(99, Math.Max(1, low));
        s.flashThreshold = Math.Min(s.lowThreshold - 1, Math.Max(0, flash));
        SaveDeferred(); // 拖滑杆高频触发，合并写盘
        RefreshTrays();
        UpdateFlash();
        Raise();
    }

    readonly object saveLock = new object();
    System.Threading.Timer saveTimer;

    // 高频设置变更 800ms 防抖写盘，避免拖滑杆时每 tick 都落盘
    void SaveDeferred() {
        lock (saveLock) {
            if (saveTimer == null)
                saveTimer = new System.Threading.Timer(_ => { lock (saveLock) Cfg.Save(); }, null, 800, Timeout.Infinite);
            else saveTimer.Change(800, Timeout.Infinite);
        }
    }

    public void MoveDevice(string id, int delta) {
        var o = Cfg.settings.trayOrder;
        int i = o.IndexOf(id), j = i + delta;
        if (i < 0 || j < 0 || j >= o.Count) return;
        o.RemoveAt(i);
        o.Insert(j, id);
        Cfg.Save();
        RefreshTrays(); // 图标物理位置不动，内容按新顺序重渲染（Win11 记忆槽位，重建无效）
        Raise();
    }

    public void RenameDevice(string id, string name) {
        Cfg.SetDeviceName(id, name);
        RefreshTrays();
        Raise();
    }

    // ---- 窗口 ----
    public void ShowMain() {
        Log("ShowMain enter form=" + (form == null ? "null" : form.IsDisposed ? "disposed" : "ok"));
        if (form == null || form.IsDisposed) { form = new MainForm(this); form.Show(); Log("ShowMain shown handle=" + form.Handle + " visible=" + form.Visible); }
        else { form.Show(); form.Activate(); }
        RefreshNow(); // 打开界面立即取一次最新数据
    }

    public void FormClosed() { form = null; }

    // ---- 自启动：HKCU Run 项写 exe 真实路径 + --hidden（便携版安全，移动后下次启动自愈）----
    void ApplyAutostart() {
        try {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) {
                if (Cfg.settings.autostart) k.SetValue("DUO", "\"" + Application.ExecutablePath + "\" --hidden");
                else k.DeleteValue("DUO", false);
            }
        } catch { }
    }

    // ---- Win11 托盘提升：默认收进折叠区，写 IsPromoted=1 提升到任务栏；顺带清理历史失效条目 ----
    void PromoteTrays() {
        try {
            string exe = Application.ExecutablePath;
            using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true)) {
                if (root == null) return;
                bool changed = false;
                foreach (var name in root.GetSubKeyNames()) {
                    try {
                        using (var k = root.OpenSubKey(name, true)) {
                            if (k == null) continue;
                            var p = k.GetValue("ExecutablePath") as string;
                            if (p == null) continue;
                            if (string.Equals(p, exe, StringComparison.OrdinalIgnoreCase)) {
                                if (!Equals(k.GetValue("IsPromoted"), 1)) { k.SetValue("IsPromoted", 1, RegistryValueKind.DWord); changed = true; }
                            } else if (Regex.IsMatch(p, @"\\(duo|batteryduo)\.exe$", RegexOptions.IgnoreCase) && !File.Exists(p)) {
                                root.DeleteSubKeyTree(name, false);
                            }
                        }
                    } catch { }
                }
                if (changed) pump.BeginInvoke((Action)RebuildTrays); // 重新注册托盘让提升立即生效
            }
        } catch { }
    }

    // ---- 单实例：第二个实例通过命名事件唤醒首实例窗口 ----
    void ListenShow() {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DUO.Show");
        new Thread(() => {
            while (!quitting) {
                try { if (ev.WaitOne(500)) pump.BeginInvoke((Action)ShowMain); }
                catch { break; }
            }
            ev.Dispose();
        }) { IsBackground = true }.Start();
    }

    public void Exit() {
        quitting = true;
        wake.Set();
        if (pollThread != null) pollThread.Join(3000);
        lock (saveLock) { // 防抖定时器可能还压着未写的配置，退出前强制落盘
            if (saveTimer != null) { saveTimer.Dispose(); saveTimer = null; }
            Cfg.Save();
        }
        Drivers.DisposeAll();
        if (form != null && !form.IsDisposed) form.Dispose();
        foreach (var ni in trayIcons) {
            ni.Visible = false;
            TrayRenderer.Destroy(ni.Icon);
            ni.Dispose();
        }
        trayIcons.Clear();
        ExitThread();
    }
}
