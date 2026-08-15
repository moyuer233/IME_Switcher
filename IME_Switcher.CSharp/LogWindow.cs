using System.Drawing;
using System.Runtime.InteropServices;

namespace IMESwitcher;

/// <summary>
/// 独立调试日志窗口：不遮挡主界面，支持拖拽、关闭、手动切换按钮与日志滚动。
/// 窗口销毁时不退出主消息循环（与主窗口共享消息循环）。
/// </summary>
internal sealed class LogWindow
{
    public const int W = 620;
    public const int H = 440;
    private const int TitleH = 32;
    private const uint WM_REFRESH = NativeMethods.WM_USER + 3;

    private static NativeMethods.WndProc? _wndProcDelegate; // 保持委托引用防止 GC
    private readonly App _app;
    private IntPtr _hwnd;
    private bool _created;

    private readonly object _lock = new();
    private readonly List<string> _lines = new();
    private const int MaxLines = 2000;
    private int _scroll;

    private bool _hoverClose;
    private bool _hoverManual;
    private bool _pressedClose;
    private bool _pressedManual;

    public IntPtr Handle => _hwnd;

    public LogWindow(App app)
    {
        _app = app;
        Logger.LogPushed += msg => OnLog(msg);
    }

    private void OnLog(string msg)
    {
        lock (_lock)
        {
            _lines.Add(msg);
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);
            _scroll = 0;
        }
        if (_hwnd != IntPtr.Zero)
            NativeMethods.PostMessageW(_hwnd, WM_REFRESH, IntPtr.Zero, IntPtr.Zero);
    }

    public void Show()
    {
        if (!_created)
        {
            _created = true;
            _wndProcDelegate = WndProc;
            var wc = new NativeMethods.WNDCLASSW
            {
                style = 0,
                lpfnWndProc = _wndProcDelegate,
                hInstance = NativeMethods.GetModuleHandle(null),
                hCursor = NativeMethods.LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                lpszClassName = "IMESwitcherLog",
            };
            NativeMethods.RegisterClassW(ref wc);
            _hwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW,
                "IMESwitcherLog", "调试日志",
                NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE,
                120, 120, W, H, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd != IntPtr.Zero)
            {
                int round = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }

            foreach (var line in Logger.GetRecent(MaxLines))
                OnLog(line);
        }
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
            NativeMethods.SetForegroundWindow(_hwnd);
        }
    }

    public void Toggle()
    {
        if (_hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_hwnd))
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        else
            Show();
    }

    private enum Hit { None, Title, BtnManual, BtnClose }

    private Hit HitTest(int x, int y)
    {
        if (y < TitleH)
        {
            if (x >= W - 40) return Hit.BtnClose;
            if (x >= W - 160) return Hit.BtnManual;
            return Hit.Title;
        }
        return Hit.None;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                OnPaint(hWnd);
                return IntPtr.Zero;
            case NativeMethods.WM_ERASEBKGND:
                return new IntPtr(1);
            case NativeMethods.WM_MOUSEWHEEL:
                OnWheel(wParam);
                return IntPtr.Zero;
            case NativeMethods.WM_MOUSEMOVE:
                OnMove(lParam);
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONDOWN:
                OnDown(lParam);
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONUP:
                OnUp(lParam);
                return IntPtr.Zero;
            case NativeMethods.WM_CLOSE:
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
                return IntPtr.Zero;
            case NativeMethods.WM_DESTROY:
                _hwnd = IntPtr.Zero;
                return IntPtr.Zero;
            case WM_REFRESH:
                NativeMethods.InvalidateRect(hWnd, IntPtr.Zero, false);
                return IntPtr.Zero;
            default:
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void OnMove(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var hit = HitTest(x, y);
        bool hc = hit == Hit.BtnClose;
        bool hm = hit == Hit.BtnManual;
        if (hc != _hoverClose || hm != _hoverManual)
        {
            _hoverClose = hc;
            _hoverManual = hm;
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void OnDown(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        switch (HitTest(x, y))
        {
            case Hit.BtnClose:
                _pressedClose = true;
                break;
            case Hit.BtnManual:
                _pressedManual = true;
                break;
            case Hit.Title:
                // 拖动窗口
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(_hwnd, NativeMethods.WM_NCLBUTTONDOWN, NativeMethods.HTCAPTION, IntPtr.Zero);
                break;
        }
    }

    private void OnUp(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var hit = HitTest(x, y);
        if (_pressedClose && hit == Hit.BtnClose)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }
        else if (_pressedManual && hit == Hit.BtnManual)
        {
            _app.ManualTest();
        }
        _pressedClose = false;
        _pressedManual = false;
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void OnWheel(IntPtr wParam)
    {
        int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        lock (_lock)
        {
            var visible = (H - TitleH - 8) / 17;
            _scroll = Math.Clamp(_scroll - (delta > 0 ? 3 : -3), 0, Math.Max(0, _lines.Count - visible));
        }
        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    private void OnPaint(IntPtr hWnd)
    {
        NativeMethods.BeginPaint(hWnd, out var ps);
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
        NativeMethods.EndPaint(hWnd, ref ps);
    }

    private void Render(IntPtr hdc)
    {
        Gdi.Fill(hdc, 0, 0, W, H, Theme.Bg);

        // 标题栏
        Gdi.TextLeft(hdc, "调试日志（滚轮滚动）", 16, 0, 260, TitleH, Theme.Text, Gdi.FontBold);
        // 手动切换按钮
        Gdi.FillRounded(hdc, W - 160, 4, W - 44, TitleH - 4,
            _pressedManual ? Color.FromArgb(0xE8, 0xEA, 0xED) :
            _hoverManual ? Color.FromArgb(0xF3, 0xF4, 0xF6) : Theme.BgSubtle, Theme.Radius);
        Gdi.DrawBorder(hdc, new NativeMethods.RECT { left = W - 160, top = 4, right = W - 44, bottom = TitleH - 4 }, Theme.Border, Theme.Radius);
        Gdi.TextCentered(hdc, "手动切换", W - 160, 4, 116, TitleH - 8, Theme.Text, Gdi.FontBold);
        // 关闭按钮
        Gdi.Fill(hdc, W - 40, 0, W, TitleH, _pressedClose || _hoverClose ? Theme.Danger : Theme.Bg);
        Gdi.TextCentered(hdc, "✕", W - 40, 0, 40, TitleH, _hoverClose || _pressedClose ? Color.White : Theme.TextMuted, Gdi.FontNormal);
        Gdi.Fill(hdc, 0, TitleH - 1, W, TitleH, Theme.BorderMuted);

        // 日志区
        var lr = new NativeMethods.RECT { left = 12, top = TitleH + 8, right = W - 12, bottom = H - 12 };
        Gdi.FillRounded(hdc, lr.left, lr.top, lr.right, lr.bottom, Theme.BgSubtle, Theme.Radius);

        lock (_lock)
        {
            var visible = (lr.bottom - lr.top) / 17;
            int start = Math.Max(0, _lines.Count - visible - _scroll);
            int y = lr.top + 4;
            for (int i = start; i < _lines.Count && y < lr.bottom; i++, y += 17)
            {
                var r = new NativeMethods.RECT { left = lr.left + 8, top = y, right = lr.right - 8, bottom = y + 17 };
                Gdi.Text(hdc, _lines[i], r, Theme.Text, Gdi.FontMono,
                    NativeMethods.DT_LEFT | NativeMethods.DT_VCENTER | NativeMethods.DT_SINGLELINE);
            }
        }
    }
}
