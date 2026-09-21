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
    // 界面字体族：按优先级探测系统已装字体，兜底一定是 Windows 自带的微软雅黑。
    // 之前直接写死 "Source Han Sans SC"：没装时既不会自动回退、也没有任何提示，
    // 实际落到 Microsoft Sans Serif（中文再靠字体链接）—— 属于静默失效，所以改成显式探测。
    private static readonly string UiFace = PickFace("Noto Sans SC", "HarmonyOS Sans SC", "Microsoft YaHei UI");

    public static readonly Font FontNormal = MakeFont(UiFace, 13, false);
    public static readonly Font FontBold = MakeFont(UiFace, 13, true);
    public static readonly Font FontSmall = MakeFont(UiFace, 12, false);
    public static readonly Font FontCard = MakeFont(UiFace, 13, true); // PCL 的卡片标题与正文同号（13），靠字重区分层级
    public static readonly Font FontMono = new("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    public static readonly Font FontSymbol = new("Segoe UI Symbol", 15f, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>按优先级挑第一个系统已安装的字体族；全都装不上就用最后一个（必须是系统必有的）</summary>
    private static string PickFace(params string[] candidates)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in installed.Families) have.Add(f.Name);
            foreach (var c in candidates)
                if (have.Contains(c)) return c;
        }
        catch (Exception e)
        {
            Logger.Log($"枚举系统字体失败，改用兜底字体: {e.Message}");
        }
        return candidates[candidates.Length - 1];
    }

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

    /// <summary>
    /// 卡片柔和投影。PCL 的卡片阴影是自绘的 6 采样点渐变（ShadowRadius=3、色 #343D4A、常态 α≈0.07）；
    /// GDI 没有渐变 alpha 笔刷，这里用 6 层同心圆角矩形等效（外扩 0~3px、alpha 6~28），
    /// 最内层只露出卡片下沿的一条边。必须画在卡片本体之前。
    /// </summary>
    public static void Shadow(IntPtr hdc, int l, int t, int r, int b, int radius)
    {
        var ink = Theme.ShadowInk;
        FillRounded(hdc, l - 3, t - 2, r + 3, b + 4, Color.FromArgb(6, ink), radius + 3);
        FillRounded(hdc, l - 2, t - 2, r + 2, b + 4, Color.FromArgb(11, ink), radius + 2);
        FillRounded(hdc, l - 2, t - 1, r + 2, b + 3, Color.FromArgb(16, ink), radius + 2);
        FillRounded(hdc, l - 1, t - 1, r + 1, b + 2, Color.FromArgb(21, ink), radius + 1);
        FillRounded(hdc, l - 1, t, r + 1, b + 2, Color.FromArgb(25, ink), radius + 1);
        FillRounded(hdc, l, t, r, b + 1, Color.FromArgb(28, ink), radius);
    }

    /// <summary>
    /// 水平三段渐变。PCL 标题栏是 HSL(210,85,48) → (210,85,54) → (210,85,48)，
    /// 等效 RGB 端点约 #0F63E2 → #1968E8；用两个线性渐变刷拼出中段最亮的观感。
    /// </summary>
    public static void FillGradientH(IntPtr hdc, int l, int t, int r, int b, Color left, Color mid, Color right)
    {
        using var g = Graphics.FromHdc(hdc);
        int half = (r - l) / 2;
        using (var br = new LinearGradientBrush(new Rectangle(l, t, half, b - t), left, mid, LinearGradientMode.Horizontal))
            g.FillRectangle(br, l, t, half, b - t);
        using (var br = new LinearGradientBrush(new Rectangle(l + half, t, r - l - half, b - t), mid, right, LinearGradientMode.Horizontal))
            g.FillRectangle(br, l + half, t, r - l - half, b - t);
    }

    /// <summary>
    /// 平滑圆点（GDI+ 抗锯齿）。GDI 的 <c>Ellipse</c> 是硬边绘制、没有抗锯齿，
    /// 18px 的圆点放在平滑的圆角轨道上会露出明显锯齿 —— 圆点必须走这里。
    /// 坐标为浮点：量化成整数会让滑块动画一跳一跳。
    /// </summary>
    public static void FillEllipse(IntPtr hdc, float cx, float cy, float radius, Color color)
    {
        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, cx - radius, cy - radius, radius * 2, radius * 2);
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

    /// <summary>文字渲染：DirectWrite（当前 NativeAOT 下不可用，会自动跳过）→ GDI ClearType → GDI+ 兜底</summary>
    public static void Text(IntPtr hdc, string text, NativeMethods.RECT r, Color color, Font font, uint format)
    {
        uint align = (format & NativeMethods.DT_CENTER) != 0 ? 1u
            : (format & NativeMethods.DT_RIGHT) != 0 ? 2u : 0u;
        if (DWriteText.TryDraw(hdc, text, r, color, font.Name, font.Size, font.Bold, align))
            return;
        if (TryTextGdi(hdc, text, r, color, font, format))
            return;

        // 兜底：GDI+ 渲染
        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            // 尊重 DT_SINGLELINE：不折行，超宽用省略号（否则长热键会折行并被矩形下半裁掉）
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };
        sf.Alignment = (format & NativeMethods.DT_CENTER) != 0 ? StringAlignment.Center
            : (format & NativeMethods.DT_RIGHT) != 0 ? StringAlignment.Far : StringAlignment.Near;
        g.DrawString(text, font, brush, new RectangleF(r.left, r.top, r.right - r.left, r.bottom - r.top), sf);
    }

    /// <summary>
    /// GDI 原生文字：CLEARTYPE_QUALITY 走子像素抗锯齿，比 GDI+ 的 AntiAliasGridFit（纯灰阶）锐利得多
    /// —— 12/13px 的加粗中文在灰阶抗锯齿下发糊，是界面"不精致"的主因。
    /// HFONT 按 (字族, 字号, 粗体) 缓存复用，进程生命周期内常驻（与预建 Font 同样不释放）。
    /// </summary>
    private static readonly Dictionary<(string, int, bool), IntPtr> GdiFonts = new();

    private static bool TryTextGdi(IntPtr hdc, string text, NativeMethods.RECT r, Color color, Font font, uint format)
    {
        try
        {
            var key = (font.Name, (int)font.Size, font.Bold);
            if (!GdiFonts.TryGetValue(key, out var hfont))
            {
                // 负高度 = 字符高度，与 GDI+ 的 GraphicsUnit.Pixel 同义，字号不变
                hfont = NativeMethods.CreateFontW(-(int)font.Size, 0, 0, 0,
                    font.Bold ? (int)NativeMethods.FW_BOLD : (int)NativeMethods.FW_NORMAL,
                    0, 0, 0, NativeMethods.DEFAULT_CHARSET, NativeMethods.OUT_DEFAULT_PRECIS,
                    NativeMethods.CLIP_DEFAULT_PRECIS, NativeMethods.CLEARTYPE_QUALITY,
                    NativeMethods.DEFAULT_PITCH, font.Name);
                if (hfont == IntPtr.Zero) return false;
                GdiFonts[key] = hfont;
            }
            var oldFont = NativeMethods.SelectObject(hdc, hfont);
            var oldColor = NativeMethods.SetTextColor(hdc, NativeMethods.ColorToCOLORREF(color));
            int oldBk = NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);
            var rect = r; // DrawTextW 会就地改写矩形，必须传副本
            NativeMethods.DrawTextW(hdc, text, text.Length, ref rect, format | NativeMethods.DT_NOPREFIX);
            NativeMethods.SetBkMode(hdc, oldBk);
            NativeMethods.SetTextColor(hdc, oldColor);
            NativeMethods.SelectObject(hdc, oldFont);
            return true;
        }
        catch (Exception e)
        {
            if (!_gdiFailureLogged)
            {
                _gdiFailureLogged = true;
                Logger.Log($"GDI 文字渲染异常，已回退 GDI+: {e.Message}");
            }
            return false;
        }
    }

    private static bool _gdiFailureLogged;

    public static void TextCentered(IntPtr hdc, string text, int l, int t, int w, int h, Color color, Font font)
        => Text(hdc, text, new NativeMethods.RECT { left = l, top = t, right = l + w, bottom = t + h }, color, font,
            NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);

    public static void TextLeft(IntPtr hdc, string text, int l, int t, int w, int h, Color color, Font font)
        => Text(hdc, text, new NativeMethods.RECT { left = l, top = t, right = l + w, bottom = t + h }, color, font,
            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
}
