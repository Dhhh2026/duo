// 主界面：无边框深色窗口，设备卡片（图标/命名/排序）+ 设置区
// 设置项按用户偏好用图标/滑杆/按钮，不用下拉框；关闭即销毁释放内存（托盘常驻）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

class MainForm : Form {
    static readonly Color Bg = Color.FromArgb(16, 20, 24);
    static readonly Color Card = Color.FromArgb(26, 32, 40);
    static readonly Color Btn = Color.FromArgb(35, 42, 52);
    static readonly Color Fg = Color.FromArgb(230, 230, 230);
    static readonly Color Dim = Color.FromArgb(130, 136, 144);

    readonly DuoApp app;
    readonly Dictionary<string, Panel> cards = new Dictionary<string, Panel>();
    readonly Dictionary<string, PictureBox> pics = new Dictionary<string, PictureBox>();
    readonly Dictionary<string, Label> status = new Dictionary<string, Label>();
    readonly Dictionary<string, TextBox> names = new Dictionary<string, TextBox>();
    readonly Dictionary<string, Button> ups = new Dictionary<string, Button>();
    readonly Dictionary<string, Button> downs = new Dictionary<string, Button>();
    readonly List<KeyValuePair<Button, int>> rateBtns = new List<KeyValuePair<Button, int>>();
    Slider lowBar, flashBar;
    Label lowVal, flashVal;
    Toggle autoBox;
    Panel scroll, content, vtrack, vthumb;
    Button refreshBtn;
    Label refreshHint;
    System.Windows.Forms.Timer hintTimer, clockTimer, hoverTimer;
    readonly Dictionary<Panel, float> hoverCur = new Dictionary<Panel, float>();
    readonly Dictionary<Panel, bool> hoverGoal = new Dictionary<Panel, bool>();
    bool syncing, refreshing, vdrag;
    int contentH, viewportH, vdragY, vdragVal;

    public MainForm(DuoApp app) {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Bg; ForeColor = Fg;
        // 绝对像素布局：禁用自动缩放（Font 模式会在 125% DPI 下放大控件导致内容溢出被裁）
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(380, 466);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        ShowInTaskbar = false;
        MaximizeBox = false; // 标题区是 HTCAPTION，防双击触发最大化
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BuildTitle();
        // 窗口高度不超出屏幕工作区；内容视口 + 右沿垂直滚动条（内容超高时启用）
        var wa = Screen.PrimaryScreen.WorkingArea;
        if (wa.Height < 466) ClientSize = new Size(380, wa.Height);
        scroll = new Panel { BackColor = Bg };
        Make(scroll, this, 0, 36, 380, ClientSize.Height - 36);
        content = new Panel { BackColor = Bg };
        Make(content, scroll, 0, 0, 380, 420);
        BuildDevices(content);
        BuildSettings(content);
        BuildScrollbar(); // 右沿 8px 深色迷你滚动条，内容超高才出现
        MouseWheel += WheelScroll; // 滚轮冒泡到窗体统一翻页（滑杆聚焦时滚轮=调值，不到这里）
        Resize += (s, e) => LayoutViewport(); // 窗口高度变化时视口/滚动条跟随（小屏工作区）
        app.StateChanged += OnState;
        app.Polled += OnPolled;
        FormClosed += (s, e) => {
            DuoApp.Log("MainForm FormClosed");
            app.StateChanged -= OnState;
            app.Polled -= OnPolled;
            hintTimer.Dispose();
            clockTimer.Dispose();
            hoverTimer.Dispose();
            app.FormClosed();
        };
        Shown += (s, e) => { ActiveControl = null; }; // 打开窗口不聚焦任何控件（否则第一个改名框自动全选蓝底）
        hintTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        hintTimer.Tick += (s, e) => { hintTimer.Stop(); refreshHint.ForeColor = Dim; };
        hoverTimer = new System.Windows.Forms.Timer { Interval = 18 };
        hoverTimer.Tick += HoverTick;
        clockTimer = new System.Windows.Forms.Timer { Interval = 5000 }; // 相对时间/闪烁图标定期重绘
        clockTimer.Tick += (s, e) => OnState();
        clockTimer.Start();
        OnState();
        UpdateScrollRange();
        DuoApp.Log("MainForm ctor done");
    }

    // ---- 右沿迷你滚动条：轨道 8px 深色，内容超出一屏才出现；拖滑块/点轨道翻页/滚轮 ----
    void BuildScrollbar() {
        vtrack = new Panel { BackColor = Color.FromArgb(22, 27, 33), Visible = false, TabStop = false };
        Make(vtrack, this, 372, 40, 8, ClientSize.Height - 48);
        vthumb = new Panel { BackColor = Color.FromArgb(64, 74, 90), TabStop = false };
        Make(vthumb, vtrack, 1, 0, 6, 28);
        vthumb.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { vdrag = true; vdragY = e.Y; vdragVal = -content.Top; vthumb.Capture = true; } };
        vthumb.MouseMove += (s, e) => {
            if (!vdrag) return;
            int range = vtrack.Height - vthumb.Height;
            if (range > 0 && scrollMax > 0) ScrollTo(vdragVal + (int)((e.Y - vdragY) * (double)scrollMax / range));
        };
        vthumb.MouseUp += (s, e) => { vdrag = false; vthumb.Capture = false; };
        vthumb.MouseEnter += (s, e) => vthumb.BackColor = Color.FromArgb(92, 104, 124);
        vthumb.MouseLeave += (s, e) => { if (!vdrag) vthumb.BackColor = Color.FromArgb(64, 74, 90); };
        vtrack.MouseDown += (s, e) => ScrollTo(-content.Top + (e.Y < vthumb.Top ? -viewportH : viewportH)); // 点轨道翻页
        vtrack.BringToFront(); // Controls.Add 后进者居 z 底，必须提层免得被全宽 scroll 盖住
    }

    int scrollMax { get { return Math.Max(0, contentH - viewportH); } }

    void ScrollTo(int v) {
        v = Math.Max(0, Math.Min(scrollMax, v));
        if (content.Top == -v) return;
        content.Top = -v;
        UpdateThumb();
    }

    void WheelScroll(object s, MouseEventArgs e) { if (vtrack.Visible) ScrollTo(-content.Top + (e.Delta > 0 ? -60 : 60)); }

    void UpdateThumb() {
        int range = vtrack.Height - vthumb.Height;
        vthumb.Top = range <= 0 || scrollMax <= 0 ? 0 : (int)((long)(-content.Top) * range / scrollMax);
    }

    void LayoutViewport() {
        scroll.SetBounds(0, 36, 380, ClientSize.Height - 36);
        vtrack.SetBounds(372, 40, 8, ClientSize.Height - 48);
        UpdateScrollRange();
    }

    void UpdateScrollRange() {
        contentH = 0;
        foreach (Control c in content.Controls) contentH = Math.Max(contentH, c.Bottom);
        viewportH = scroll.ClientSize.Height;
        vtrack.Visible = contentH > viewportH; // 一屏放得下就整条隐藏
        if (!vtrack.Visible) { content.Top = 0; return; }
        vthumb.Height = Math.Max(28, Math.Min(vtrack.Height, (int)((long)vtrack.Height * viewportH / contentH)));
        if (content.Top < -scrollMax) content.Top = -scrollMax;
        UpdateThumb();
    }

    // 悬停高亮（18ms 渐变）：进入（含子控件）向亮态过渡，真正离开卡片区域才回落
    static readonly Color Hover = Color.FromArgb(48, 60, 75);
    void HookHover(Panel root) {
        hoverCur[root] = 0f; hoverGoal[root] = false;
        EventHandler enter = (s, e) => { hoverGoal[root] = true; hoverTimer.Start(); };
        EventHandler leave = (s, e) => {
            if (!root.ClientRectangle.Contains(root.PointToClient(Control.MousePosition))) { hoverGoal[root] = false; hoverTimer.Start(); }
        };
        HookHoverRecursive(root, enter, leave);
    }

    void HoverTick(object s, EventArgs e) {
        bool busy = false;
        foreach (var kv in new List<KeyValuePair<Panel, float>>(hoverCur)) { // 遍历中改值，先复制一份
            float cur = kv.Value, goal = hoverGoal[kv.Key] ? 1f : 0f;
            if (cur == goal) continue;
            cur += (goal - cur) * 0.35f;
            if (Math.Abs(goal - cur) < 0.04f) cur = goal;
            hoverCur[kv.Key] = cur;
            if (!kv.Key.IsDisposed) kv.Key.BackColor = Lerp(Card, Hover, cur);
            busy = true;
        }
        if (!busy) hoverTimer.Stop(); // 全部到位即停，零空转
    }

    static Color Lerp(Color a, Color b, float t) {
        return Color.FromArgb(a.R + (int)((b.R - a.R) * t), a.G + (int)((b.G - a.G) * t), a.B + (int)((b.B - a.B) * t));
    }

    static void HookHoverRecursive(Control c, EventHandler enter, EventHandler leave) {
        c.MouseEnter += enter;
        c.MouseLeave += leave;
        foreach (Control child in c.Controls) HookHoverRecursive(child, enter, leave);
    }

    Control Make(Control c, Control parent, int x, int y, int w, int h) {
        c.SetBounds(x, y, w, h);
        parent.Controls.Add(c);
        return c;
    }

    void BuildTitle() {
        // 标题文字直接画在窗体上（OnPaint）；拖动走 WndProc 命中测试，标题区不放任何子面板挡路
        var close = new Button { Text = "✕", FlatStyle = FlatStyle.Flat, ForeColor = Dim, BackColor = Bg, TabStop = false, Cursor = Cursors.Hand };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 60, 56); // 关闭键悬停变红
        close.FlatAppearance.MouseDownBackColor = Color.FromArgb(120, 40, 38);
        close.Click += (s, e) => Close();
        Make(close, this, 344, 4, 28, 28);
    }

    // 无边框窗口拖动：顶部 36px 命中测试改判 HTCAPTION，交给系统原生移动循环
    // （旧方案绑子面板 MouseDown + ReleaseCapture/SendMessage，实际拖不动）
    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x84) { // WM_NCHITTEST
            base.WndProc(ref m);
            if (m.Result == (IntPtr)1) { // HTCLIENT：只有窗体裸面会走到这（子控件各自接收命中）
                var lp = (long)m.LParam;
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (p.Y < 36) m.Result = (IntPtr)2; // HTCAPTION
            }
            return;
        }
        base.WndProc(ref m);
    }

    void BuildDevices(Control parent) {
        var panel = new Panel { BackColor = Bg };
        Make(panel, parent, 12, 8, 344, 148);
        foreach (var def in app.OrderedDefs()) {
            var card = new Panel { BackColor = Card, Tag = def.Id };
            Make(card, panel, 0, 0, 344, 68);
            var pic = new PictureBox { SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.Transparent };
            Make(pic, card, 10, 18, 32, 32);
            // 改名框包 1px 边框容器：聚焦时边框变蓝（TextBox 原生边框不可配色）；
            // TabStop=false 防止开窗时按 Tab 序自动聚焦全选
            var wrap = new Panel { BackColor = Color.FromArgb(42, 50, 60), Padding = new Padding(1) };
            Make(wrap, card, 52, 9, 208, 25);
            var name = new TextBox { BackColor = Bg, ForeColor = Fg, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, Text = app.DeviceLabel(def), MaxLength = 24, TabStop = false };
            wrap.Controls.Add(name);
            name.GotFocus += (s, e) => wrap.BackColor = DuoApp.Blue;
            name.LostFocus += (s, e) => wrap.BackColor = Color.FromArgb(42, 50, 60);
            string id = def.Id;
            name.KeyDown += (s, e) => {
                if (e.KeyCode == Keys.Enter) { app.RenameDevice(id, name.Text); ActiveControl = null; e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { name.Text = app.DeviceLabel(def); ActiveControl = null; e.SuppressKeyPress = true; } // Esc 放弃修改
            };
            name.Validated += (s, e) => {
                if (string.IsNullOrWhiteSpace(name.Text)) name.Text = app.DeviceLabel(def); // 空名不保存，还原显示
                else app.RenameDevice(id, name.Text);
            };
            var st = new Label { ForeColor = Dim, AutoSize = false };
            Make(st, card, 52, 38, 230, 18);
            var up = IconBtn(card, "▲", 296, 8);
            var down = IconBtn(card, "▼", 296, 36);
            up.Click += (s, e) => app.MoveDevice(id, -1);
            down.Click += (s, e) => app.MoveDevice(id, 1);
            HookHover(card); // 子控件就绪后统一挂悬停高亮（渐变）
            cards[id] = card; pics[id] = pic; names[id] = name; status[id] = st; ups[id] = up; downs[id] = down;
        }
    }

    Button IconBtn(Control parent, string glyph, int x, int y) {
        var b = new Button { Text = glyph, FlatStyle = FlatStyle.Flat, ForeColor = Fg, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TabStop = false, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 66, 80);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(24, 30, 38);
        Make(b, parent, x, y, 24, 24);
        return b;
    }

    Button FlatBtn(Control parent, string text, int x, int y, int w) {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, ForeColor = Fg, BackColor = Btn, TabStop = false };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 56, 68);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(24, 30, 38);
        Make(b, parent, x, y, w, 28);
        return b;
    }

    Label GroupLabel(Control parent, string text, int y) {
        var l = new Label { Text = text, ForeColor = Dim, AutoSize = false };
        Make(l, parent, 0, y, 280, 18); // 200 宽放不下"低电阈值（低于此值显示红色）"会被截断
        return l;
    }

    void BuildSettings(Control parent) {
        var p = new Panel { BackColor = Bg };
        Make(p, parent, 12, 164, 344, 256);

        GroupLabel(p, "刷新频率", 4);
        int x = 0;
        foreach (var pair in new[] { new KeyValuePair<int, string>(10000, "10 秒"), new KeyValuePair<int, string>(30000, "30 秒"), new KeyValuePair<int, string>(60000, "1 分钟"), new KeyValuePair<int, string>(300000, "5 分钟") }) {
            var b = FlatBtn(p, pair.Value, x, 24, 80);
            int ms = pair.Key;
            b.Click += (s, e) => app.SetRefreshMs(ms);
            rateBtns.Add(new KeyValuePair<Button, int>(b, ms));
            x += 88;
        }

        GroupLabel(p, "低电阈值（低于此值显示红色）", 66);
        lowBar = new Slider { Minimum = 1, Maximum = 99 };
        Make(lowBar, p, 0, 86, 278, 30);
        lowVal = new Label { ForeColor = Fg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        Make(lowVal, p, 284, 88, 56, 20);
        lowBar.ValueChanged += (s, e) => { if (!syncing) app.UpdateThresholds(lowBar.Value, flashBar.Value); };

        GroupLabel(p, "闪烁阈值（0 为关闭）", 124);
        flashBar = new Slider { Minimum = 0, Maximum = 98 };
        Make(flashBar, p, 0, 144, 278, 30);
        flashVal = new Label { ForeColor = Fg, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        Make(flashVal, p, 284, 146, 56, 20);
        flashBar.ValueChanged += (s, e) => { if (!syncing) app.UpdateThresholds(lowBar.Value, flashBar.Value); };

        autoBox = new Toggle { Text = "开机自启动", ForeColor = Fg };
        Make(autoBox, p, 0, 184, 200, 22);
        autoBox.CheckedChanged += (s, e) => { if (!syncing) app.SetAutostart(autoBox.Checked); };

        refreshBtn = FlatBtn(p, "立即刷新", 0, 216, 120);
        refreshBtn.Click += (s, e) => {
            if (refreshing) return;
            refreshing = true;
            refreshBtn.Enabled = false;
            refreshBtn.Text = "刷新中…";
            refreshHint.ForeColor = Dim;
            refreshHint.Text = "正在读取设备电量…";
            app.RefreshNow(); // 唤醒轮询线程；读完所有设备后 Polled 事件恢复按钮
        };
        refreshHint = new Label { ForeColor = Dim, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        Make(refreshHint, p, 130, 220, 214, 20);
    }

    void OnState() {
        if (syncing || IsDisposed) return;
        syncing = true;
        try {
            var order = app.OrderedDefs();
            for (int i = 0; i < order.Count; i++) {
                var def = order[i];
                cards[def.Id].Location = new Point(0, i * 74);
                string text; Color color; bool blank;
                app.Visual(def, out text, out color, out blank);
                var old = pics[def.Id].Image;
                pics[def.Id].Image = TrayRenderer.RenderBitmap(text, color, blank, 32);
                if (old != null) old.Dispose();
                var st = app.GetState(def.Id);
                status[def.Id].Text = st.Connected
                    ? st.Capacity + "%" + (st.Charging ? " · 充电中" : "") + " · " + Relative(st.At)
                    : "未连接";
                status[def.Id].ForeColor = st.Connected ? color : Dim;
                if (!names[def.Id].Focused) names[def.Id].Text = app.DeviceLabel(def);
                ups[def.Id].Enabled = i > 0;
                downs[def.Id].Enabled = i < order.Count - 1;
            }
            foreach (var kv in rateBtns) kv.Key.BackColor = kv.Value == app.RefreshMs ? DuoApp.Blue : Btn;
            var s = app.Cfg.settings;
            lowBar.Value = s.lowThreshold; // Slider setter 内自夹取，无系统 TrackBar 的赋值顺序坑
            flashBar.Maximum = Math.Max(0, s.lowThreshold - 1);
            flashBar.Value = s.flashThreshold;
            lowVal.Text = s.lowThreshold + "%";
            flashVal.Text = s.flashThreshold <= 0 ? "关" : s.flashThreshold + "%";
            autoBox.Checked = s.autostart;
        } finally { syncing = false; }
    }

    // 一轮设备读取完成：结束"刷新中"反馈，显示完成时间（蓝色，1.5s 后转暗保留）
    void OnPolled() {
        if (!refreshing || IsDisposed) return;
        refreshing = false;
        refreshBtn.Enabled = true;
        refreshBtn.Text = "立即刷新";
        refreshHint.ForeColor = DuoApp.Blue;
        refreshHint.Text = "已于 " + DateTime.Now.ToString("HH:mm:ss") + " 更新";
        hintTimer.Stop();
        hintTimer.Start();
    }

    static string Relative(DateTime at) {
        if (at == default(DateTime)) return "";
        var sec = (int)(DateTime.Now - at).TotalSeconds;
        if (sec < 5) return "刚刚";
        if (sec < 60) return sec + " 秒前";
        return (sec / 60) + " 分钟前";
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        using (var f = new Font("Segoe UI", 10f, FontStyle.Bold))
        using (var b = new SolidBrush(Dim))
            e.Graphics.DrawString("DUO", f, b, 14, 8);
        using (var pen = new Pen(Color.FromArgb(40, 48, 58)))
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

// 自绘滑杆：深色圆角轨道 + 蓝色填充段 + 圆形滑钮（系统 TrackBar 在深色窗口渲染成经典灰样式，无法改色）
class Slider : Control {
    int min, max = 100, val;
    bool drag, hover;
    public event EventHandler ValueChanged;
    public int Minimum { get { return min; } set { min = value; Value = val; Invalidate(); } }
    public int Maximum { get { return max; } set { max = value; Value = val; Invalidate(); } }
    public int Value {
        get { return val; }
        set {
            int v = Math.Max(min, Math.Min(max, value));
            if (v == val) return;
            val = v; Invalidate();
            var h = ValueChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }
    public Slider() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false; Cursor = Cursors.Hand;
    }
    int PosOf(int v) { return max <= min ? 8 : 8 + (int)((long)(v - min) * (Width - 16) / (max - min)); } // 滑钮中心 x
    int ValAt(int x) { return max <= min ? min : min + (int)Math.Round((x - 8.0) * (max - min) / Math.Max(1, Width - 16)); } // x → 值
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { drag = true; Capture = true; Value = ValAt(e.X); } base.OnMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) Value = ValAt(e.X); base.OnMouseMove(e); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; Capture = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int cy = Height / 2, tx = PosOf(val);
        using (var b = new SolidBrush(Color.FromArgb(34, 41, 51))) FillRound(g, b, 4, cy - 3, Width - 8, 6); // 轨道
        using (var b = new SolidBrush(DuoApp.Blue)) FillRound(g, b, 4, cy - 3, tx - 4, 6); // 已填充段
        int r = drag || hover ? 7 : 6; // 悬停/拖动中滑钮微微变大
        using (var b = new SolidBrush(drag || hover ? Color.White : Color.FromArgb(198, 205, 213)))
            g.FillEllipse(b, tx - r, cy - r, r * 2, r * 2);
    }
    public static GraphicsPath RoundPath(int x, int y, int w, int h) {
        var p = new GraphicsPath();
        p.AddArc(x, y, h, h, 90, 180); p.AddArc(x + w - h, y, h, h, 270, 180);
        p.CloseFigure();
        return p;
    }
    public static void FillRound(Graphics g, Brush b, int x, int y, int w, int h) {
        if (w <= 0) return;
        if (w < h) { g.FillEllipse(b, x, y, w, w); return; } // 填充段极短时退化为圆点
        using (var p = RoundPath(x, y, w, h)) g.FillPath(b, p);
    }
}

// 自绘勾选框：深色圆角框 + 选中蓝底白勾（系统 CheckBox 白底黑勾，深色 UI 里突兀）
class Toggle : Control {
    bool chk, hover;
    public event EventHandler CheckedChanged;
    public bool Checked {
        get { return chk; }
        set {
            if (chk == value) return;
            chk = value; Invalidate();
            var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty);
        }
    }
    public Toggle() {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false; Cursor = Cursors.Hand;
    }
    protected override void OnMouseClick(MouseEventArgs e) { if (e.Button == MouseButtons.Left) Checked = !Checked; base.OnMouseClick(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e) {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int by = (Height - 16) / 2;
        if (chk) {
            using (var b = new SolidBrush(DuoApp.Blue)) Slider.FillRound(g, b, 1, by, 16, 16);
            using (var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new Point(5, by + 8), new Point(8, by + 11), new Point(13, by + 5) });
        } else {
            using (var p = Slider.RoundPath(1, by, 16, 16))
            using (var pen = new Pen(hover ? Color.FromArgb(150, 160, 175) : Color.FromArgb(90, 100, 115)))
                g.DrawPath(pen, p);
        }
        using (var b = new SolidBrush(hover ? Color.White : ForeColor))
            g.DrawString(Text, Font, b, 24, (Height - Font.Height) / 2f);
    }
}
