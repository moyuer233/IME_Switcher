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
    public const int H = 524;
    public const int TitleH = 48; // PCL 标题栏高度（固定值，不要改成 44/52）
    public const string ClassName = "IMESwitcherMain"; // 单实例激活按类名查找（比按窗口标题可靠，标题只是界面文案）
    public const uint WM_REFRESH = NativeMethods.WM_USER + 2;

    private static NativeMethods.WndProc? _wndProcDelegate; // 保持委托引用防止 GC
    private readonly App _app;
    private IntPtr _hwnd;

    // 双缓冲后备缓冲：创建一次复用（见 EnsureBackBuffer / ReleaseBackBuffer）
    private IntPtr _memDc, _memBmp, _memOldBmp;

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
            lpszClassName = ClassName,
        };
        if (NativeMethods.RegisterClassW(ref wc) == 0) return false;

        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW,
            ClassName, "输入法一键切换",
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

    /// <summary>线程安全的刷新请求：热键回调跑在后台线程，统一投递消息交给 UI 线程重绘</summary>
    private void PostRefresh()
    {
        if (_hwnd != IntPtr.Zero) NativeMethods.PostMessageW(_hwnd, WM_REFRESH, IntPtr.Zero, IntPtr.Zero);
    }

    public void SetListeningState(bool on, string? hotkey, string? toggle)
    {
        Listening = on;
        if (on)
        {
            HotkeyText = string.IsNullOrEmpty(hotkey) ? "未设置" : hotkey;
            ToggleText = string.IsNullOrEmpty(toggle) ? "未设置" : toggle;
        }
        PostRefresh();
    }

    public void SetRecordingStarted(string? target)
    {
        RecordingTarget = target;
        PostRefresh();
    }

    public void SetRecordingFinished(string? target, string? value)
    {
        RecordingTarget = null;
        if (target == "toggle") ToggleText = string.IsNullOrEmpty(value) ? "未设置" : value;
        else HotkeyText = string.IsNullOrEmpty(value) ? "未设置" : value;
        PostRefresh();
    }

    public void SetRecordingCanceled()
    {
        RecordingTarget = null;
        PostRefresh();
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
                ReleaseBackBuffer();
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
                else if (wParam.ToInt32() == AnimTimerId)
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
        EnsureBackBuffer(hdc);
        Render(_memDc);
        NativeMethods.BitBlt(hdc, 0, 0, W, H, _memDc, 0, 0, 0x00CC0020); // SRCCOPY
        NativeMethods.EndPaint(_hwnd, ref ps);
    }

    /// <summary>后备缓冲按需创建一次并复用（窗口尺寸固定，无需处理尺寸变化）</summary>
    private void EnsureBackBuffer(IntPtr hdc)
    {
        if (_memDc != IntPtr.Zero) return;
        _memDc = NativeMethods.CreateCompatibleDC(hdc);
        _memBmp = NativeMethods.CreateCompatibleBitmap(hdc, W, H);
        _memOldBmp = NativeMethods.SelectObject(_memDc, _memBmp);
    }

    /// <summary>释放后备缓冲（WM_DESTROY 调用）：漏掉就会在窗口重建时泄漏位图</summary>
    private void ReleaseBackBuffer()
    {
        if (_memDc == IntPtr.Zero) return;
        if (_memOldBmp != IntPtr.Zero) NativeMethods.SelectObject(_memDc, _memOldBmp);
        if (_memBmp != IntPtr.Zero) NativeMethods.DeleteObject(_memBmp);
        NativeMethods.DeleteDC(_memDc);
        _memDc = _memBmp = _memOldBmp = IntPtr.Zero;
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
        else if (id != UiId.None)
        {
            NativeMethods.SetCapture(_hwnd); // 捕获鼠标：拖到窗口外松开也能收到 WM_LBUTTONUP，否则按下态会一直粘住
        }
        Invalidate();
    }

    private void OnMouseUp()
    {
        var id = _pressed;
        _pressed = UiId.None;
        NativeMethods.ReleaseCapture();
        Invalidate();
        if (id != UiId.None && id == _hover)
            OnClick(id);
    }

    // ---------------- 布局（命中测试与绘制共用同一份矩形：改一处即两处生效） ----------------

    private const int ContentLeft = 20;
    private const int ContentW = 520;
    private const int Gap = 15; // PCL 卡片间距
    private const int Pad = 25; // PCL 卡片内容左右内边距

    // 纵向骨架：整页的位置全部由这几个常量推导，改标题栏或卡片高度时文字与控件一起走
    private const int StatusTop = TitleH + 8;
    private const int StatusH = 24;
    private const int Card1Top = StatusTop + StatusH + Gap;
    private const int Card1H = 190;
    private const int Card2Top = Card1Top + Card1H + Gap;
    private const int Card2H = 154;
    private const int ActionsTop = Card2Top + Card2H + Gap;
    private const int ActionsH = 35; // PCL 按钮高度

    // 卡内几何（PCL：卡片标题偏移 (15,12)，内容上内边距 40）
    private const int DyTitle = 12;
    private const int DyContent = 40;
    private const int RowH = 35;    // 一行的高度（输入框 / 按钮）
    private const int RowGap = 10;  // 行间距
    private const int SwitchH = 24;
    private const int DyRow1 = DyContent;
    private const int DyRow2 = DyRow1 + RowH + RowGap;
    private const int DyHint = DyRow2 + RowH + RowGap;
    private const int DyNotice = DyHint + 24;
    private const int DySwitch2 = DyRow2 + SwitchH + 6;

    private static NativeMethods.RECT R(int l, int t, int w, int h)
        => new() { left = l, top = t, right = l + w, bottom = t + h };

    // 卡片内的列：标签 / 值 / 行内按钮
    private const int RowLeft = ContentLeft + Pad;             // 45
    private const int RowRight = ContentLeft + ContentW - Pad; // 515
    private const int LabelW = 83;
    private const int RowBtnW = 75;
    private static readonly int RowFieldLeft = RowLeft + LabelW;
    private static readonly int RowBtnLeft = RowRight - RowBtnW;
    private static readonly int RowFieldW = RowBtnLeft - RowGap - RowFieldLeft;

    private static readonly NativeMethods.RECT RTitleMin = R(W - 88, 0, 44, TitleH);
    private static readonly NativeMethods.RECT RTitleClose = R(W - 44, 0, 44, TitleH);
    private static readonly NativeMethods.RECT RHotkeyCard = R(ContentLeft, Card1Top, ContentW, Card1H);
    private static readonly NativeMethods.RECT ROptionsCard = R(ContentLeft, Card2Top, ContentW, Card2H);
    private static readonly NativeMethods.RECT RHotkeyField = R(RowFieldLeft, Card1Top + DyRow1, RowFieldW, RowH);
    private static readonly NativeMethods.RECT RToggleField = R(RowFieldLeft, Card1Top + DyRow2, RowFieldW, RowH);
    private static readonly NativeMethods.RECT RBtnChange1 = R(RowBtnLeft, Card1Top + DyRow1, RowBtnW, RowH);
    private static readonly NativeMethods.RECT RBtnChange2 = R(RowBtnLeft, Card1Top + DyRow2, RowBtnW, RowH);
    // 分段胶囊（PCL MyRadioButton：高 27、全圆角）
    private static readonly int SegY = Card2Top + DyRow1 + 4;
    private static readonly int SegW = RowRight - RowFieldLeft;
    private static readonly NativeMethods.RECT RSegApi = R(RowFieldLeft, SegY, SegW / 2, 27);
    private static readonly NativeMethods.RECT RSegSim = R(RowFieldLeft + SegW / 2, SegY, SegW - SegW / 2, 27);
    private static readonly NativeMethods.RECT RChkAuto = R(RowLeft, Card2Top + DyRow2, RowRight - RowLeft, SwitchH);
    private static readonly NativeMethods.RECT RChkTray = R(RowLeft, Card2Top + DySwitch2, RowRight - RowLeft, SwitchH);
    // 底部三按钮：等宽居中
    private const int BtnW = 116;
    private const int BtnH = ActionsH;
    private const int BtnGap = RowGap;
    private static readonly int BtnLeft = ContentLeft + (ContentW - (BtnW * 3 + BtnGap * 2)) / 2;
    private static readonly NativeMethods.RECT RBtnStart = R(BtnLeft, ActionsTop, BtnW, BtnH);
    private static readonly NativeMethods.RECT RBtnStop = R(BtnLeft + BtnW + BtnGap, ActionsTop, BtnW, BtnH);
    private static readonly NativeMethods.RECT RBtnDebug = R(BtnLeft + (BtnW + BtnGap) * 2, ActionsTop, BtnW, BtnH);

    // ---------------- 命中测试 ----------------

    private static bool InRect(int x, int y, NativeMethods.RECT r)
        => x >= r.left && x < r.right && y >= r.top && y < r.bottom;

    private UiId HitTest(int x, int y)
    {
        if (y < TitleH)
        {
            if (InRect(x, y, RTitleClose)) return UiId.BtnClose;
            if (InRect(x, y, RTitleMin)) return UiId.BtnMin;
            return UiId.TitleBar;
        }
        // 热键设置卡片
        if (InRect(x, y, RBtnChange1)) return UiId.BtnChange1;
        if (InRect(x, y, RBtnChange2))
            return RecordingTarget == "toggle" ? UiId.BtnCancel : UiId.BtnChange2;
        if (InRect(x, y, RHotkeyField)) return UiId.HotkeyField;
        if (InRect(x, y, RToggleField)) return UiId.ToggleField;
        // 选项卡片
        if (InRect(x, y, RSegApi)) return UiId.RbApi;
        if (InRect(x, y, RSegSim)) return UiId.RbSim;
        if (InRect(x, y, RChkAuto)) return UiId.ChkAuto;
        if (InRect(x, y, RChkTray)) return UiId.ChkTray;
        // 操作按钮
        if (InRect(x, y, RBtnStart)) return UiId.BtnStart;
        if (InRect(x, y, RBtnStop)) return UiId.BtnStop;
        if (InRect(x, y, RBtnDebug)) return UiId.BtnDebug;
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

    /// <summary>
    /// 启动时一次性把配置同步到 UI（单一写入通道：字段 + 刷新 + 动画初值一起做，避免漏项）。
    /// </summary>
    public void SetInitialState(string? hotkey, string? toggle, int method, bool autostart, bool trayStart)
    {
        HotkeyText = string.IsNullOrEmpty(hotkey) ? "未设置" : hotkey;
        ToggleText = string.IsNullOrEmpty(toggle) ? "未设置" : toggle;
        Method = method;
        Autostart = autostart;
        TrayStart = trayStart;
        SyncSwitchAnim();
        PostRefresh();
    }

    /// <summary>同步滑块动画初值（由 SetInitialState 调用）</summary>
    private void SyncSwitchAnim()
    {
        _animAuto = Autostart ? 1f : 0f;
        _animTray = TrayStart ? 1f : 0f;
    }

    /// <summary>显示短暂红色提示（如热键冲突）</summary>
    public void ShowNotice(string text)
    {
        if (_hwnd == IntPtr.Zero) return; // 窗口未建好时 SetTimer 会被静默忽略，提示永远不显示
        Notice = text;
        NativeMethods.SetTimer(_hwnd, new IntPtr(NoticeTimerId), 5000, IntPtr.Zero);
        PostRefresh();
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
        // PCL 标题栏：水平三段蓝渐变（HSL 210/85/48 → 54 → 48），图标与标题都是白色
        Gdi.FillGradientH(hdc, 0, 0, W, TitleH, Theme.TitleBarL, Theme.TitleBarM, Theme.TitleBarL);
        Gdi.TextLeft(hdc, "⌨", 16, 0, 32, TitleH, Color.White, Gdi.FontSymbol);
        Gdi.TextLeft(hdc, "输入法一键切换", 50, 0, 220, TitleH, Color.White, Gdi.FontBold);

        RenderTitleBtn(hdc, RTitleMin, TitleBtnType.Min, _hover == UiId.BtnMin);
        RenderTitleBtn(hdc, RTitleClose, TitleBtnType.Close, _hover == UiId.BtnClose);
    }

    private enum TitleBtnType { Min, Close }

    /// <summary>标题栏按钮（PCL 风格：白色线条 + 悬停时 28×28 半透明白底；关闭键悬停转红）</summary>
    private void RenderTitleBtn(IntPtr hdc, NativeMethods.RECT r, TitleBtnType type, bool hover)
    {
        int cx = r.left + (r.right - r.left) / 2, cy = r.top + (r.bottom - r.top) / 2;
        if (hover)
        {
            Gdi.FillRounded(hdc, cx - 14, cy - 14, cx + 14, cy + 14,
                type == TitleBtnType.Close ? Theme.Danger : Color.FromArgb(0x50, 255, 255, 255), 4);
        }
        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.White, 1.4f);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
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
        // 状态徽章（PCL 提示条配色：绿底绿字 / 灰底灰字），圆角与按钮一致
        string label = Listening ? "运行中" : "已停止";
        var bg = Listening ? Theme.SuccessSoftBg : Theme.BgSubtle;
        var fg = Listening ? Theme.Success : Theme.TextFaint;
        Gdi.FillRounded(hdc, ContentLeft, StatusTop, ContentLeft + 66, StatusTop + StatusH, bg, Theme.RadiusBtn);
        Gdi.TextCentered(hdc, label, ContentLeft, StatusTop, 66, StatusH, fg, Gdi.FontSmall);

        string status = Listening
            ? $"监听中 ({HotkeyText}{(string.IsNullOrEmpty(ToggleText) || ToggleText == "未设置" ? "" : " · 开关 " + ToggleText)})"
            : "未启动";
        Gdi.TextLeft(hdc, status, ContentLeft + 78, StatusTop, 424, StatusH, Theme.TextMuted, Gdi.FontSmall);
    }

    private void RenderHotkeyCard(IntPtr hdc)
    {
        var card = RHotkeyCard;
        Gdi.Shadow(hdc, card.left, card.top, card.right, card.bottom, Theme.Radius);
        Gdi.FillRounded(hdc, card.left, card.top, card.right, card.bottom, Theme.Card, Theme.Radius);
        // PCL 卡片标题：相对卡片偏移 (15, 12)
        Gdi.TextLeft(hdc, "热键设置", card.left + 15, Card1Top + DyTitle, 200, 22, Theme.Text, Gdi.FontCard);

        // 切换热键（标签与字段同高居中：共用同一行的矩形）
        Gdi.TextLeft(hdc, "切换热键", RowLeft, RHotkeyField.top, LabelW, RowH, Theme.TextMuted, Gdi.FontSmall);
        var recording = RecordingTarget == "hotkey";
        RenderField(hdc, RHotkeyField, recording ? "按下热键... (ESC 取消)" : HotkeyText, recording);
        RenderButton(hdc, RBtnChange1, "更改", BtnKind.Normal, _hover == UiId.BtnChange1, _pressed == UiId.BtnChange1, true);

        // 开关热键
        Gdi.TextLeft(hdc, "开关热键", RowLeft, RToggleField.top, LabelW, RowH, Theme.TextMuted, Gdi.FontSmall);
        var recording2 = RecordingTarget == "toggle";
        RenderField(hdc, RToggleField, recording2 ? "按下热键... (ESC 取消)" : ToggleText, recording2);
        if (RecordingTarget == "toggle")
            RenderButton(hdc, RBtnChange2, "取消", BtnKind.Normal, _hover == UiId.BtnCancel, _pressed == UiId.BtnCancel, true);
        else
            RenderButton(hdc, RBtnChange2, "更改", BtnKind.Normal, _hover == UiId.BtnChange2, _pressed == UiId.BtnChange2, true);

        Gdi.TextLeft(hdc, "点击热键框或「更改」后按下键盘按键 / 鼠标侧键（X1/X2），按 ESC 取消",
            RowLeft, Card1Top + DyHint, RowRight - RowLeft, 22, Theme.TextMuted, Gdi.FontSmall);

        if (!string.IsNullOrEmpty(Notice))
            Gdi.TextLeft(hdc, Notice, RowLeft, Card1Top + DyNotice, RowRight - RowLeft, 22, Theme.Danger, Gdi.FontBold);
    }

    private void RenderField(IntPtr hdc, NativeMethods.RECT r, string text, bool recording)
    {
        // PCL 输入框：白底 + 1px 灰描边 + 圆角 3
        Gdi.FillRounded(hdc, r.left, r.top, r.right, r.bottom,
            recording ? Color.FromArgb(0xFF, 0xF8, 0xC5) : Theme.FieldBg, Theme.RadiusBtn);
        Gdi.DrawBorder(hdc, r, recording ? Color.FromArgb(0xF0, 0xD9, 0x8A) : Theme.BorderInput, Theme.RadiusBtn);
        // 值左对齐、常规字重：居中加粗会看起来像标题而不像"可编辑的内容"
        var inner = new NativeMethods.RECT { left = r.left + 10, top = r.top, right = r.right - 8, bottom = r.bottom };
        Gdi.Text(hdc, text, inner, Theme.Text,
            recording ? Gdi.FontBold : Gdi.FontNormal,
            NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
    }

    private void RenderOptionsCard(IntPtr hdc)
    {
        var card = ROptionsCard;
        Gdi.Shadow(hdc, card.left, card.top, card.right, card.bottom, Theme.Radius);
        Gdi.FillRounded(hdc, card.left, card.top, card.right, card.bottom, Theme.Card, Theme.Radius);
        Gdi.TextLeft(hdc, "选项", card.left + 15, Card2Top + DyTitle, 200, 22, Theme.Text, Gdi.FontCard);

        Gdi.TextLeft(hdc, "切换方式", RowLeft, RSegApi.top, LabelW, 27, Theme.TextMuted, Gdi.FontSmall);
        RenderSegmented(hdc, RSegApi, RSegSim, Method);

        // PCL 的设置项排版：复选框在左、标签紧跟其后（MyCheckBox 标签左边距 26）
        RenderCheck(hdc, RowLeft, RChkAuto.top + 3, _animAuto, _hover == UiId.ChkAuto);
        Gdi.TextLeft(hdc, "开机自动启动", RowLeft + 26, RChkAuto.top, 200, SwitchH, Theme.Text, Gdi.FontSmall);

        RenderCheck(hdc, RowLeft, RChkTray.top + 3, _animTray, _hover == UiId.ChkTray);
        Gdi.TextLeft(hdc, "默认启动到托盘", RowLeft + 26, RChkTray.top, 200, SwitchH, Theme.Text, Gdi.FontSmall);
    }

    /// <summary>
    /// PCL 胶囊分段（MyRadioButton）：高 27、全圆角；
    /// 选中＝实心蓝 + 白字，未选中＝无底色 + 蓝字（原先是浅蓝底 + 深蓝字，与 PCL 相反）。
    /// </summary>
    private static void RenderSegmented(IntPtr hdc, NativeMethods.RECT api, NativeMethods.RECT sim, int method)
    {
        bool apiOn = method == 1;
        var sel = apiOn ? api : sim;
        Gdi.FillRounded(hdc, sel.left, sel.top, sel.right, sel.bottom, Theme.AccentHover, (sel.bottom - sel.top) / 2);
        Gdi.TextCentered(hdc, "API（优先库）", api.left, api.top, api.right - api.left, api.bottom - api.top,
            apiOn ? Color.White : Theme.AccentHover, apiOn ? Gdi.FontBold : Gdi.FontNormal);
        Gdi.TextCentered(hdc, "模拟（Win+Space）", sim.left, sim.top, sim.right - sim.left, sim.bottom - sim.top,
            apiOn ? Theme.AccentHover : Color.White, apiOn ? Gdi.FontNormal : Gdi.FontBold);
    }

    /// <summary>
    /// PCL 复选框（MyCheckBox）：18×18 圆角 3 的空心方框 + 勾。
    /// 未选中描边 #343D4A（悬停 #1370F3），选中描边 #0B5BCB；勾带回弹地弹出（PCL 用 AniEaseOutBack）。
    /// PCL 里"开/关"就是靠它承担的 —— 全仓没有 iOS 风格滑动开关。
    /// </summary>
    private static void RenderCheck(IntPtr hdc, int l, int t, float progress, bool hover)
    {
        const int box = 18;
        bool on = progress > 0.5f;
        var line = hover ? Theme.AccentHover : on ? Theme.Accent : Theme.AccentInk;
        Gdi.FillRounded(hdc, l, t, l + box, t + box, Theme.Card, Theme.RadiusBtn);
        Gdi.DrawBorder(hdc, new NativeMethods.RECT { left = l, top = t, right = l + box, bottom = t + box },
            line, Theme.RadiusBtn);
        if (progress <= 0.02f) return;

        using var g = Graphics.FromHdc(hdc);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = EaseOutBack(progress);
        float cx = l + box / 2f, cy = t + box / 2f;
        PointF P(float x, float y) => new(cx + (x - 6f) * s, cy + (y - 6f) * s);
        using var pen = new Pen(line, 1.8f)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[] { P(2.6f, 6.4f), P(5f, 8.8f), P(9.4f, 3.6f) });
    }

    /// <summary>回弹缓动（PCL 的 AniEaseOutBack）：t 过 0.7 后略超 1 再收回，勾弹出时有个小回弹</summary>
    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    private void RenderActions(IntPtr hdc)
    {
        RenderButton(hdc, RBtnStart, "启动", BtnKind.Highlight, _hover == UiId.BtnStart, _pressed == UiId.BtnStart, !Listening);
        RenderButton(hdc, RBtnStop, "停止", BtnKind.Red, _hover == UiId.BtnStop, _pressed == UiId.BtnStop, Listening);
        RenderButton(hdc, RBtnDebug, "调试日志", BtnKind.Normal, _hover == UiId.BtnDebug, _pressed == UiId.BtnDebug, true);
    }

    private enum BtnKind { Normal, Highlight, Red }

    /// <summary>
    /// PCL 按钮做法：浅底 + 1px 描边，且**文字颜色 = 描边颜色**（PCL 里 Foreground 绑定 BorderBrush）。
    /// PCL 没有实心填充按钮 —— 主按钮同样是蓝描边蓝字，只有 hover 才铺一层浅蓝底并把描边提到 #1370F3；
    /// 危险按钮同理（红描边红字），hover 才铺浅红底。禁用态统一灰 4 描边。
    /// </summary>
    private static void RenderButton(IntPtr hdc, NativeMethods.RECT r, string text, BtnKind kind,
        bool hover, bool pressed, bool enabled)
    {
        bool active = enabled && (hover || pressed);
        Color line = !enabled ? Theme.TextDisabled
            : kind switch
            {
                BtnKind.Highlight => active ? Theme.AccentHover : Theme.Accent,
                BtnKind.Red => active ? Theme.DangerHover : Theme.Danger,
                _ => active ? Theme.AccentHover : Theme.AccentInk,
            };
        Color fill = !enabled ? Theme.BgSubtle
            : pressed ? Theme.AccentSoft
            : active ? (kind == BtnKind.Red ? Theme.DangerSoftBg : Theme.AccentHoverBg)
            : Theme.Card;
        Gdi.FillRounded(hdc, r.left, r.top, r.right, r.bottom, fill, Theme.RadiusBtn);
        Gdi.DrawBorder(hdc, r, line, Theme.RadiusBtn);
        // 按下时文字下沉 1px，给一点"真的按到了"的手感
        int dy = pressed ? 1 : 0;
        Gdi.TextCentered(hdc, text, r.left, r.top + dy, r.right - r.left, r.bottom - r.top, line, Gdi.FontBold);
    }
}
