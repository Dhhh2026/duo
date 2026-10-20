// 托盘数字图标：3x5 点阵字按 16x16 逻辑尺寸设计，物理像素随 DPI 缩放（100%:16、125%:20、150%:24）
// 内部 4 倍超采样渲染再缩到目标尺寸：整数倍 DPI 下边缘锐利不变，非整数倍（125%）比例恒定不缩水
// 颜色语义：蓝/绿=充足或充电，红=低电，灰=未连接；blank=闪烁帧的熄灭态
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

static class TrayRenderer {
    static readonly Dictionary<char, string[]> Font = new Dictionary<char, string[]> {
        { '0', new[] { "111", "101", "101", "101", "111" } },
        { '1', new[] { "010", "110", "010", "010", "111" } },
        { '2', new[] { "111", "001", "111", "100", "111" } },
        { '3', new[] { "111", "001", "011", "001", "111" } },
        { '4', new[] { "101", "101", "111", "001", "001" } },
        { '5', new[] { "111", "100", "111", "001", "111" } },
        { '6', new[] { "111", "100", "111", "101", "111" } },
        { '7', new[] { "111", "001", "001", "001", "001" } },
        { '8', new[] { "111", "101", "111", "101", "111" } },
        { '9', new[] { "111", "101", "111", "001", "111" } },
        { '-', new[] { "000", "000", "111", "000", "000" } },
    };

    public static Bitmap RenderBitmap(string text, Color color, bool blank, int size) {
        var bmp = new Bitmap(size, size);
        if (blank) return bmp;
        const int SS = 4;          // 超采样倍率
        int big = 16 * SS, dot = 2 * SS; // 逻辑基准 16x16、每点 2 逻辑像素 → 64px 画布 8px 点
        using (var hi = new Bitmap(big, big))
        using (var g = Graphics.FromImage(hi))
        using (var brush = new SolidBrush(color)) {
            int n = text.Length;
            int x0 = (big - (n * 3 + (n - 1)) * dot) / 2, y0 = (big - 5 * dot) / 2;
            foreach (var ch in text) {
                string[] gl;
                if (Font.TryGetValue(ch, out gl))
                    for (int gy = 0; gy < 5; gy++)
                        for (int gx = 0; gx < 3; gx++)
                            if (gl[gy][gx] == '1')
                                g.FillRectangle(brush, x0 + gx * dot, y0 + gy * dot, dot, dot);
                x0 += 4 * dot;
            }
            using (var g2 = Graphics.FromImage(bmp)) {
                g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g2.DrawImage(hi, 0, 0, size, size);
            }
        }
        return bmp;
    }

    // FromHandle 不接管句柄：替换/销毁时必须显式 DestroyIcon，否则 GDI 泄漏
    public static Icon RenderIcon(string text, Color color, bool blank) {
        using (var bmp = RenderBitmap(text, color, blank, SystemInformation.SmallIconSize.Width))
            return Icon.FromHandle(bmp.GetHicon());
    }

    public static void SetIcon(NotifyIcon ni, Icon icon) {
        var old = ni.Icon;
        ni.Icon = icon;
        Destroy(old);
    }

    public static void Destroy(Icon icon) {
        if (icon == null) return;
        Native.DestroyIcon(icon.Handle);
        icon.Dispose();
    }
}
