// 入口：DPI 感知 + 单实例（第二实例唤醒首实例窗口后退出）
// --hidden：自启动时不弹窗，仅驻留托盘
using System;
using System.Threading;
using System.Windows.Forms;

static class Program {
    [STAThread]
    static void Main(string[] args) {
        Native.SetProcessDPIAware();
        bool created;
        using (var mutex = new Mutex(true, @"Local\DUO.SingleInstance", out created)) {
            if (!created) {
                try { EventWaitHandle.OpenExisting(@"Local\DUO.Show").Set(); } catch { }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DuoApp(Array.IndexOf(args, "--hidden") >= 0));
        }
    }
}
