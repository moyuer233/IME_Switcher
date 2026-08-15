using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace IMESwitcher;

/// <summary>
/// 绘制辅助：形状用 GDI（高效圆角矩形/边框），文字用 GDI+（抗锯齿网格对齐，
/// 渲染质量接近 WPF/PCL 的 ClearType 效果）。
/// </summary>
internal static class Gdi
{
    // 预建字体（进程生命周期内常驻；用像素单位避免 DPI 缩放模糊）
    // 小字号（≤13px）用微软雅黑（TrueType 强 hinting，小字更锐利，PCL 同款）；
    // 大字号（标题/卡片标题）用思源黑体展示美感。
    public static readonly Font FontNormal = MakeFont("Microsoft YaHei UI", 13, false);
    public static readonly Font FontBold = MakeFont("Microsoft YaHei UI", 13, true);
    public static readonly Font FontSmall = MakeFont("Microsoft YaHei UI", 12, false);
    public static readonly Font FontTitle = MakeFont("Source Han Sans SC", 15, true);
    public static readonly Font FontCard = MakeFont("Source Han Sans SC", 14, true);
    public static readonly Font FontMono = new("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    public static readonly Font FontSymbol = new("Segoe UI Symbol", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
    public static readonly Font FontSymbolSmall = new("Segoe UI Symbol", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static Font MakeFont(string face, int size, bool bold)
        => new(face, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);

    public static void Fill(IntPtr hdc, NativeMethods.RECT r, Color color)
    {
        var brush = NativeMethods.CreateSolidBrush(NativeMethods.ColorToCOLORREF(color));
        NativeMethods.FillRect(hdc, ref r, brush);
        NativeMethods.DeleteObject(brush);
    }

    public static void Fill(IntPtr hdc, int l, int t, int r, int b, Color color)
        => Fill(hdc, new NativeMethods.RECT { left = l, top = t, right = r, bottom = b }, color);

    public static void FillRounded(IntPtr hdc, int l, int t, int r, int b, Color color, int radius)
    {
        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias; // 平滑圆角（PCL/WPF 同款矢量渲染）
        using var path = RoundedRectPath(l, t, r, b, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawBorder(IntPtr hdc, NativeMethods.RECT r, Color color, int radius)
    {
        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRectPath(r.left, r.top, r.right, r.bottom, radius);
        using var pen = new Pen(color, 1f);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectPath(int l, int t, int r, int b, int radius)
    {
        var path = new GraphicsPath();
        int w = r - l, h = b - t;
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(w, h)));
        var rect = new Rectangle(l, t, w, h);
        var arc = new Rectangle(rect.X, rect.Y, d, d);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>文字渲染：优先 DirectWrite（WPF 同款引擎），失败回退 GDI+</summary>
    public static void Text(IntPtr hdc, string text, NativeMethods.RECT r, Color color, Font font, uint format)
    {
        uint align = (format & NativeMethods.DT_CENTER) != 0 ? 1u
            : (format & NativeMethods.DT_RIGHT) != 0 ? 2u : 0u;
        if (DWriteText.TryDraw(hdc, text, r, color, font.Name, font.Size, font.Bold, align))
            return;

        // 回退：GDI+ 渲染
        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        sf.Alignment = (format & NativeMethods.DT_CENTER) != 0 ? StringAlignment.Center
            : (format & NativeMethods.DT_RIGHT) != 0 ? StringAlignment.Far : StringAlignment.Near;
        g.DrawString(text, font, brush, new RectangleF(r.left, r.top, r.right - r.left, r.bottom - r.top), sf);
    }

    public static void TextCentered(IntPtr hdc, string text, int l, int t, int w, int h, Color color, Font font)
        => Text(hdc, text, new NativeMethods.RECT { left = l, top = t, right = l + w, bottom = t + h }, color, font,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);

    public static void TextLeft(IntPtr hdc, string text, int l, int t, int w, int h, Color color, Font font)
        => Text(hdc, text, new NativeMethods.RECT { left = l, top = t, right = l + w, bottom = t + h }, color, font,
            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
}
