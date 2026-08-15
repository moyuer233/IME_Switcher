using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace IMESwitcher;

/// <summary>
/// 纯 Win32 主窗口：无边框 + GDI 自绘 GitHub 风格界面。
/// 固定尺寸 560x660，所有 UI 元素自绘并自管理交互。
/// </summary>
internal sealed class MainWindow
{
    public const int W = 560;
    public const int H = 660;
    public const uint WM_REFRESH = NativeMethods.WM_USER + 2;

    private static NativeMethods.WndProc? _wndProcDelegate; // 保持委托引用防止 GC
    private readonly App _app;
    private IntPtr _hwnd;

    // 状态（UI 线程读写；App 通过方法更新）
    public bool Listening;
    public string HotkeyText = "未设置";
    public string ToggleText = "未设置";
    public string? RecordingTarget;
    public int Method = 1;
    public bool Autostart;
    public bool TrayStart;

    // 交互状态
    private UiId _hover;
    private UiId _pressed;
    private bool _mouseInWindow;

    // 开关滑块动画（0=关，1=开）
    private float _animAuto;
    private float _animTray;
    private bool _animRunning;
    private const int AnimTimerId = 1;
    private const int NoticeTimerId = 2;

    // 提示消息（红色，短暂显示）
    public string? Notice;

    // 独立调试日志窗口
    private readonly LogWindow _logWin;

    private enum UiId
    {
        None, TitleBar, BtnMin, BtnClose, HotkeyField, ToggleField,
        BtnChange1, BtnChange2, BtnCancel, RbApi, RbSim, ChkAuto, ChkTray,
        BtnStart, BtnStop, BtnDebug,
    }

    public MainWindow(App app)
    {
        _app = app;
        _logWin = new LogWindow(app);
    }

    public IntPtr Handle => _hwnd;

    public bool Create()
    {
        _wndProcDelegate = WndProc;
        var wc = new NativeMethods.WNDCLASSW
        {
            style = 0,
            lpfnWndProc = _wndProcDelegate,
            hInstance = NativeMethods.GetModuleHandle(null),
            hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(32512)), // IDC_ARROW
            lpszClassName = "IMESwitcherMain",
        };
        if (NativeMethods.RegisterClassW(ref wc) == 0) return false;

        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW,
            "IMESwitcherMain", "输入法一键切换",
            NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE,
            100, 100, W, H, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
        {
            int round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        return _hwnd != IntPtr.Zero;
    }

    // ---------------- 对外接口（App 调用） ----------------

    public void Show()
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
    }

    public void Hide()
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    }

    public void Minimize()
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_MINIMIZE);
    }

    public void Activate()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(_hwnd);
    }

    public void Refresh() => Invalidate();

    private void Invalidate()
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    public void SetListeningState(bool on, string? hotkey, string? toggle)
    {
        Listening = on;
        if (on)
        {
            HotkeyText = string.IsNullOrEmpty(hotkey) ? "未设置" : hotkey;
            ToggleText = string.IsNullOrEmpty(toggle) ? "未设置" : toggle;
        }
        Invalidate();
    }

    public void SetRecordingStarted(string? target)
    {
        RecordingTarget = target;
        Invalidate();
    }

    public void SetRecordingFinished(string? target, string? value)
    {
        RecordingTarget = null;
        if (target == "toggle") ToggleText = string.IsNullOrEmpty(value) ? "未设置" : value;
        else HotkeyText = string.IsNullOrEmpty(value) ? "未设置" : value;
        Invalidate();
    }

    public void SetRecordingCanceled()
    {
        RecordingTarget = null;
        Invalidate();
    }

    // ---------------- 窗口消息 ----------------

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                OnPaint();
                return IntPtr.Zero;
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);
            case NativeMethods.WM_MOUSEMOVE:
                OnMouseMove(lParam);
                return IntPtr.Zero;
            case NativeMethods.WM_MOUSELEAVE:
                _hover = UiId.None;
                _mouseInWindow = false;
                Invalidate();
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONDOWN:
                OnMouseDown(lParam);
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONUP:
                OnMouseUp();
                return IntPtr.Zero;
            case NativeMethods.WM_CLOSE:
                Logger.Log("WM_CLOSE 收到");
                _app.HideToTray();
                return IntPtr.Zero;
            case NativeMethods.WM_DESTROY:
                Logger.Log("WM_DESTROY 收到");
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
            case NativeMethods.WM_TRAYICON:
                OnTrayIcon((int)(lParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;
            case NativeMethods.WM_TIMER:
                if (wParam.ToInt32() == NoticeTimerId)
                {
                    Notice = null;
                    NativeMethods.KillTimer(_hwnd, new IntPtr(NoticeTimerId));
                    Invalidate();
                }
                else
                {
                    OnTimer();
                }
                return IntPtr.Zero;
            case WM_REFRESH:
                Invalidate();
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void OnTrayIcon(int msg)
    {
        switch (msg)
        {
            case (int)NativeMethods.WM_LBUTTONDOWN:
            case (int)NativeMethods.WM_LBUTTONDBLCLK:
                _app.ShowWindow();
                break;
            case (int)NativeMethods.WM_CONTEXTMENU:
            case (int)NativeMethods.WM_RBUTTONDOWN:
                ShowTrayMenu();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        var hmenu = NativeMethods.CreatePopupMenu();
        NativeMethods.AppendMenuW(hmenu, NativeMethods.MF_STRING, (UIntPtr)1001, "显示设置");
        NativeMethods.AppendMenuW(hmenu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(hmenu, NativeMethods.MF_STRING, (UIntPtr)1002, "启动");
        NativeMethods.AppendMenuW(hmenu, NativeMethods.MF_STRING, (UIntPtr)1003, "停止");
        NativeMethods.AppendMenuW(hmenu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
        NativeMethods.AppendMenuW(hmenu, NativeMethods.MF_STRING, (UIntPtr)1004, "退出");

        NativeMethods.GetCursorPos(out var pt);
        NativeMethods.SetForegroundWindow(_hwnd);
        int cmd = NativeMethods.TrackPopupMenu(hmenu,
            NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
            pt.x, pt.y, 0, _hwnd, IntPtr.Zero);
        NativeMethods.DestroyMenu(hmenu);

        switch (cmd)
        {
            case 1001: _app.ShowWindow(); break;
            case 1002: _app.StartListening(); break;
            case 1003: _app.StopListening(); break;
            case 1004: _app.Quit(); break;
        }
    }

    private void OnPaint()
    {
        NativeMethods.BeginPaint(_hwnd, out var ps);
        var hdc = ps.hdc;
        // 双缓冲：先画到内存 DC 再整块拷贝，避免闪烁、提升渲染质量
        var mem = NativeMethods.CreateCompatibleDC(hdc);
        var bmp = NativeMethods.CreateCompatibleBitmap(hdc, W, H);
        var oldBmp = NativeMethods.SelectObject(mem, bmp);
        Render(mem);
        NativeMethods.BitBlt(hdc, 0, 0, W, H, mem, 0, 0, 0x00CC0020); // SRCCOPY
        NativeMethods.SelectObject(mem, oldBmp);
        NativeMethods.DeleteObject(bmp);
        NativeMethods.DeleteDC(mem);
        NativeMethods.EndPaint(_hwnd, ref ps);
    }

    private void OnMouseMove(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var id = HitTest(x, y);
        if (id != _hover)
        {
            _hover = id;
            Invalidate();
        }
        if (!_mouseInWindow)
        {
            _mouseInWindow = true;
            var tme = new NativeMethods.TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                dwFlags = NativeMethods.TME_LEAVE,
                hwndTrack = _hwnd,
            };
            NativeMethods.TrackMouseEvent(ref tme);
        }
    }

    private void OnMouseDown(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var id = HitTest(x, y);
        _pressed = id;
        if (id == UiId.TitleBar)
        {
            // 拖动窗口
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(_hwnd, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, IntPtr.Zero);
            _pressed = UiId.None;
        }
        Invalidate();
    }

    private void OnMouseUp()
    {
        var id = _pressed;
        _pressed = UiId.None;
        Invalidate();
        if (id != UiId.None && id == _hover)
            OnClick(id);
    }

    // ---------------- 命中测试 ----------------

    private static bool InRect(int x, int y, int l, int t, int w, int h)
        => x >= l && x < l + w && y >= t && y < t + h;

    private static NativeMethods.RECT R(int l, int t, int w, int h)
        => new() { left = l, top = t, right = l + w, bottom = t + h };

    private const int ContentLeft = 20;
    private const int ContentW = 520;

    private UiId HitTest(int x, int y)
    {
        if (y < 48)
        {
            if (InRect(x, y, W - 46, 0, 46, 48)) return UiId.BtnClose;
            if (InRect(x, y, W - 92, 0, 46, 48)) return UiId.BtnMin;
            return UiId.TitleBar;
        }
        // 热键设置卡片
        if (InRect(x, y, 436, 136, 80, 30)) return UiId.BtnChange1;
        if (InRect(x, y, 436, 178, 80, 30))
            return RecordingTarget == "toggle" ? UiId.BtnCancel : UiId.BtnChange2;
        if (InRect(x, y, 110, 136, 316, 30)) return UiId.HotkeyField;
        if (InRect(x, y, 110, 178, 316, 30)) return UiId.ToggleField;
        // 选项卡片
        if (InRect(x, y, 110, 324, 175, 30)) return UiId.RbApi;
        if (InRect(x, y, 285, 324, 175, 30)) return UiId.RbSim;
        if (InRect(x, y, 36, 368, 478, 26)) return UiId.ChkAuto;
        if (InRect(x, y, 36, 396, 478, 26)) return UiId.ChkTray;
        // 操作按钮
        if (InRect(x, y, 20, 444, 116, 34)) return UiId.BtnStart;
        if (InRect(x, y, 148, 444, 116, 34)) return UiId.BtnStop;
        if (InRect(x, y, 276, 444, 116, 34)) return UiId.BtnDebug;
        return UiId.None;
    }

    private void OnClick(UiId id)
    {
        Notice = null; // 交互时清除提示
        switch (id)
        {
            case UiId.BtnMin: Minimize(); break;
            case UiId.BtnClose: _app.HideToTray(); break;
            case UiId.HotkeyField:
            case UiId.BtnChange1: _app.StartRecording("hotkey"); break;
            case UiId.ToggleField:
            case UiId.BtnChange2: _app.StartRecording("toggle"); break;
            case UiId.BtnCancel: _app.CancelRecording(); break;
            case UiId.RbApi: if (Method != 1) _app.SetMethod(1); break;
            case UiId.RbSim: if (Method != 2) _app.SetMethod(2); break;
            case UiId.ChkAuto:
                _app.SetAutostart(!Autostart);
                _animAuto = Autostart ? 0f : 1f; // 从反方向开始动画到新状态
                StartSwitchAnim();
                break;
            case UiId.ChkTray:
                _app.SetTrayStart(!TrayStart);
                _animTray = TrayStart ? 0f : 1f;
                StartSwitchAnim();
                break;
            case UiId.BtnStart: _app.StartListening(); break;
            case UiId.BtnStop: _app.StopListening(); break;
            case UiId.BtnDebug: _logWin.Toggle(); break;
        }
    }

    // ---------------- 绘制 ----------------

    /// <summary>同步滑块动画初值（App 加载配置后调用）</summary>
    public void SyncSwitchAnim()
    {
        _animAuto = Autostart ? 1f : 0f;
        _animTray = TrayStart ? 1f : 0f;
    }

    /// <summary>显示短暂红色提示（如热键冲突）</summary>
    public void ShowNotice(string text)
    {
        Notice = text;
        NativeMethods.SetTimer(_hwnd, new IntPtr(NoticeTimerId), 5000, IntPtr.Zero);
        Invalidate();
    }

    private void StartSwitchAnim()
    {
        if (!_animRunning)
        {
            _animRunning = true;
            NativeMethods.SetTimer(_hwnd, new IntPtr(AnimTimerId), 16, IntPtr.Zero);
        }
    }

    private void OnTimer()
    {
        bool done = true;
        _animAuto = StepAnim(_animAuto, Autostart, ref done);
        _animTray = StepAnim(_animTray, TrayStart, ref done);
        if (done)
        {
            _animRunning = false;
            NativeMethods.KillTimer(_hwnd, new IntPtr(AnimTimerId));
        }
        Invalidate();
    }

    private static float StepAnim(float cur, bool target, ref bool done)
    {
        float t = target ? 1f : 0f;
        if (Math.Abs(cur - t) < 0.01f) return t;
        done = false;
        return cur + (t - cur) * 0.28f; // 指数缓动
    }

    private static Color LerpColor(Color a, Color b, float t)
        => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));

    private void Render(IntPtr hdc)
    {
        // 背景
        Gdi.Fill(hdc, 0, 0, W, H, Theme.Bg);

        // 标题栏
        RenderTitleBar(hdc);
        // 状态行
        RenderStatus(hdc);
        // 卡片一
        RenderHotkeyCard(hdc);
        // 卡片二
        RenderOptionsCard(hdc);
        // 操作按钮
        RenderActions(hdc);
    }

    private void RenderTitleBar(IntPtr hdc)
    {
        Gdi.Fill(hdc, 0, 0, W, 48, Theme.Card); // 白色标题栏
        Gdi.TextLeft(hdc, "⌨", 16, 0, 32, 48, Theme.Accent, Gdi.FontSymbol);
        Gdi.TextLeft(hdc, "输入法一键切换", 50, 0, 220, 48, Theme.Text, Gdi.FontBold);
        Gdi.Fill(hdc, 0, 47, W, 48, Theme.BorderMuted);

        RenderTitleBtn(hdc, W - 92, 0, 46, 48, TitleBtnType.Min, _hover == UiId.BtnMin);
        RenderTitleBtn(hdc, W - 46, 0, 46, 48, TitleBtnType.Close, _hover == UiId.BtnClose);
    }

    private enum TitleBtnType { Min, Close }

    /// <summary>右上角窗口按钮：GDI+ 圆角线段图形，hover 圆角底色</summary>
    private void RenderTitleBtn(IntPtr hdc, int l, int t, int w, int h, TitleBtnType type, bool hover)
    {
        if (hover)
        {
            Gdi.FillRounded(hdc, l + 2, t + 4, l + w - 2, t + h - 4,
                type == TitleBtnType.Close ? Theme.Danger : Theme.BgSubtle, 6);
        }
        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(hover && type == TitleBtnType.Close ? Color.White : Theme.TextMuted, 1.6f);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        int cx = l + w / 2, cy = t + h / 2;
        if (type == TitleBtnType.Min)
        {
            // 最小化：水平短线
            g.DrawLine(pen, cx - 7, cy, cx + 7, cy);
        }
        else
        {
            // 关闭：✕ 两条对角线
            g.DrawLine(pen, cx - 6, cy - 6, cx + 6, cy + 6);
            g.DrawLine(pen, cx - 6, cy + 6, cx + 6, cy - 6);
        }
    }

    private void RenderStatus(IntPtr hdc)
    {
        // PCL 风格状态徽章（胶囊），与右侧文字垂直居中对齐（中心 y≈74）
        string label = Listening ? "运行中" : "已停止";
        var bg = Listening ? Color.FromArgb(0xE6, 0xF4, 0xEA) : Theme.BgSubtle;
        var fg = Listening ? Theme.Success : Theme.TextMuted;
        Gdi.FillRounded(hdc, 24, 63, 90, 85, bg, 11);
        Gdi.TextCentered(hdc, label, 24, 64, 66, 21, fg, Gdi.FontSmall);

        string status = Listening
            ? $"监听中 ({HotkeyText}{(string.IsNullOrEmpty(ToggleText) || ToggleText == "未设置" ? "" : " · 开关 " + ToggleText)})"
            : "未启动";
        Gdi.TextLeft(hdc, status, 102, 64, 420, 21, Theme.TextMuted, Gdi.FontSmall);
    }

    private void RenderHotkeyCard(IntPtr hdc)
    {
        var card = R(ContentLeft, 92, ContentW, 180);
        Gdi.FillRounded(hdc, card.left, card.top, card.right, card.bottom, Theme.Card, Theme.Radius);
        Gdi.DrawBorder(hdc, card, Theme.Border, Theme.Radius);
        Gdi.TextLeft(hdc, "热键设置", 36, 102, 200, 24, Theme.Text, Gdi.FontCard);

        // 切换热键
        Gdi.TextLeft(hdc, "切换热键", 36, 136, 70, 30, Theme.TextMuted, Gdi.FontSmall);
        var recording = RecordingTarget == "hotkey";
        RenderField(hdc, R(110, 136, 316, 30), recording ? "按下热键... (ESC 取消)" : HotkeyText, recording);
        RenderButton(hdc, R(436, 136, 80, 30), "更改", GitHubBtn.Secondary, _hover == UiId.BtnChange1, _pressed == UiId.BtnChange1, true);

        // 开关热键
        Gdi.TextLeft(hdc, "开关热键", 36, 178, 70, 30, Theme.TextMuted, Gdi.FontSmall);
        var recording2 = RecordingTarget == "toggle";
        RenderField(hdc, R(110, 178, 316, 30), recording2 ? "按下热键... (ESC 取消)" : ToggleText, recording2);
        if (RecordingTarget == "toggle")
            RenderButton(hdc, R(436, 178, 80, 30), "取消", GitHubBtn.Secondary, _hover == UiId.BtnCancel, _pressed == UiId.BtnCancel, true);
        else
            RenderButton(hdc, R(436, 178, 80, 30), "更改", GitHubBtn.Secondary, _hover == UiId.BtnChange2, _pressed == UiId.BtnChange2, true);

        Gdi.TextLeft(hdc, "点击热键框或「更改」后按下键盘按键 / 鼠标侧键（X1/X2），按 ESC 取消",
            36, 222, 480, 20, Theme.TextMuted, Gdi.FontSmall);

        if (!string.IsNullOrEmpty(Notice))
            Gdi.TextLeft(hdc, Notice, 36, 244, 480, 20, Theme.Danger, Gdi.FontBold);
    }

    private void RenderField(IntPtr hdc, NativeMethods.RECT r, string text, bool recording)
    {
        if (recording)
            Gdi.FillRounded(hdc, r.left, r.top, r.right, r.bottom, Color.FromArgb(0xFF, 0xF8, 0xC5), Theme.Radius);
        else
            Gdi.FillRounded(hdc, r.left, r.top, r.right, r.bottom, Theme.Card, Theme.Radius);
        Gdi.DrawBorder(hdc, r, Theme.Border, Theme.Radius);
        var color = text.StartsWith("按下") ? Theme.Text : Theme.Text;
        Gdi.Text(hdc, text, new NativeMethods.RECT { left = r.left, top = r.top, right = r.right, bottom = r.bottom },
            color, Gdi.FontBold, NativeMethods.DT_CENTER | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
    }

    private void RenderOptionsCard(IntPtr hdc)
    {
        var card = R(ContentLeft, 282, ContentW, 150);
        Gdi.FillRounded(hdc, card.left, card.top, card.right, card.bottom, Theme.Card, Theme.Radius);
        Gdi.DrawBorder(hdc, card, Theme.Border, Theme.Radius);
        Gdi.TextLeft(hdc, "选项", 36, 292, 200, 24, Theme.Text, Gdi.FontCard);

        Gdi.TextLeft(hdc, "切换方式", 36, 326, 70, 30, Theme.TextMuted, Gdi.FontSmall);
        RenderSegmented(hdc, R(110, 324, 350, 30), Method);

        Gdi.TextLeft(hdc, "开机自动启动", 36, 368, 200, 26, Theme.Text, Gdi.FontSmall);
        RenderSwitch(hdc, 470, 368, _animAuto, _hover == UiId.ChkAuto);

        Gdi.TextLeft(hdc, "默认启动到托盘", 36, 396, 200, 26, Theme.Text, Gdi.FontSmall);
        RenderSwitch(hdc, 470, 396, _animTray, _hover == UiId.ChkTray);
    }

    /// <summary>PCL 风格分段选择控件</summary>
    private static void RenderSegmented(IntPtr hdc, NativeMethods.RECT r, int method)
    {
        int mid = r.left + (r.right - r.left) / 2;
        Gdi.FillRounded(hdc, r.left, r.top, r.right, r.bottom, Theme.BgSubtle, Theme.Radius);
        if (method == 1)
            Gdi.FillRounded(hdc, r.left, r.top, mid, r.bottom, Theme.Accent, Theme.Radius);
        Gdi.TextCentered(hdc, "API（优先库）", r.left, r.top, mid - r.left, r.bottom - r.top,
            method == 1 ? Color.White : Theme.Text, Gdi.FontBold);
        if (method == 2)
            Gdi.FillRounded(hdc, mid, r.top, r.right, r.bottom, Theme.Accent, Theme.Radius);
        Gdi.TextCentered(hdc, "模拟（Win+Space）", mid, r.top, r.right - mid, r.bottom - r.top,
            method == 2 ? Color.White : Theme.Text, Gdi.FontBold);
    }

    /// <summary>PCL 风格开关滑块（progress 0=关 1=开，支持动画）</summary>
    private static void RenderSwitch(IntPtr hdc, int l, int t, float progress, bool hover)
    {
        const int trackW = 44, trackH = 24, knob = 18;
        var trackColor = LerpColor(Theme.TrackOff, Theme.Accent, progress);
        Gdi.FillRounded(hdc, l, t, l + trackW, t + trackH, trackColor, trackH / 2);
        int kx = l + 3 + (int)((trackW - knob - 6) * progress);
        int ky = t + (trackH - knob) / 2;
        var brush = NativeMethods.CreateSolidBrush(NativeMethods.ColorToCOLORREF(Color.White));
        var oldB = NativeMethods.SelectObject(hdc, brush);
        var pen = NativeMethods.CreatePen((int)NativeMethods.PS_SOLID, 1,
            NativeMethods.ColorToCOLORREF(LerpColor(Color.FromArgb(0xBF, 0xC3, 0xC7), Theme.Accent, progress)));
        var oldP = NativeMethods.SelectObject(hdc, pen);
        NativeMethods.Ellipse(hdc, kx, ky, kx + knob, ky + knob);
        NativeMethods.SelectObject(hdc, oldB);
        NativeMethods.SelectObject(hdc, oldP);
        NativeMethods.DeleteObject(brush);
        NativeMethods.DeleteObject(pen);
    }

    private void RenderActions(IntPtr hdc)
    {
        RenderButton(hdc, R(20, 444, 116, 34), "启动", GitHubBtn.Primary, _hover == UiId.BtnStart, _pressed == UiId.BtnStart, !Listening);
        RenderButton(hdc, R(148, 444, 116, 34), "停止", GitHubBtn.Danger, _hover == UiId.BtnStop, _pressed == UiId.BtnStop, Listening);
        RenderButton(hdc, R(276, 444, 116, 34), "调试日志", GitHubBtn.Secondary, _hover == UiId.BtnDebug, _pressed == UiId.BtnDebug, true);
    }

    private enum GitHubBtn { Primary, Danger, Secondary }

    private static void RenderButton(IntPtr hdc, NativeMethods.RECT r, string text, GitHubBtn style,
        bool hover, bool pressed, bool enabled)
    {
        Color bg, fg;
        switch (style)
        {
            case GitHubBtn.Primary:
                bg = !enabled ? Theme.BgSubtle : pressed || hover ? Theme.AccentHover : Theme.Accent;
                fg = !enabled ? Theme.TextMuted : Color.White;
                break;
            case GitHubBtn.Danger:
                bg = !enabled ? Theme.BgSubtle : pressed || hover ? Theme.DangerHover : Theme.Danger;
                fg = !enabled ? Theme.TextMuted : Color.White;
                break;
            default:
                bg = !enabled ? Color.FromArgb(0xFA, 0xFA, 0xFA)
                    : pressed ? Color.FromArgb(0xE8, 0xEA, 0xED)
                    : hover ? Color.FromArgb(0xF3, 0xF4, 0xF6) : Theme.BgSubtle;
                fg = Theme.Text;
                break;
        }
        Gdi.FillRounded(hdc, r.left, r.top, r.right, r.bottom, bg, Theme.Radius);
        if (style == GitHubBtn.Secondary)
            Gdi.DrawBorder(hdc, r, Theme.Border, Theme.Radius);
        Gdi.TextCentered(hdc, text, r.left, r.top, r.right - r.left, r.bottom - r.top, fg, Gdi.FontBold);
    }
}
