using System.Drawing;
using System.Runtime.InteropServices;

namespace IMESwitcher;

/// <summary>系统托盘图标（Shell_NotifyIcon）</summary>
internal sealed class TrayIcon : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _hicon;
    private bool _added;

    /// <summary>托盘图标是否真的加上了；false 时调用方必须禁止隐藏窗口，否则没有任务栏也没有托盘入口，用户无法唤出/退出</summary>
    public bool Added => _added;

    public bool Add(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _hicon = CreateIcon();
        var data = BuildData();
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
        if (!_added)
            Logger.Log("托盘图标添加失败（将禁止隐藏窗口，避免程序失联）");
        return _added;
    }

    public void Remove()
    {
        if (_added)
        {
            var data = BuildData();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }
        if (_appIcon != null)
        {
            _appIcon.Dispose(); // 内嵌图标：句柄由 Icon 对象释放
            _appIcon = null;
        }
        else if (_hicon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hicon); // 兜底自绘图标：GetHicon 创建的句柄必须自己销毁
        }
        _hicon = IntPtr.Zero;
    }

    private NativeMethods.NOTIFYICONDATAW BuildData()
    {
        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _hicon,
            szTip = "输入法切换",
        };
        return nid;
    }

    private static Icon? _appIcon;

    private static IntPtr CreateIcon()
    {
        // 优先使用 exe 内嵌图标（icon.ico）
        try
        {
            var path = Environment.ProcessPath;
            if (path != null)
            {
                _appIcon = Icon.ExtractAssociatedIcon(path);
                if (_appIcon != null) return _appIcon.Handle;
            }
        }
        catch (Exception e)
        {
            Logger.Log($"读取内嵌图标失败，改用自绘图标: {e.Message}");
        }
        // 兜底：自绘图标（句柄在 Remove() 里用 DestroyIcon 销毁）
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Theme.Accent);
            using var f = new Font("Segoe UI", 17f, FontStyle.Bold);
            using var b = new SolidBrush(Color.White);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("⌨", f, b, new RectangleF(0, 0, 32, 32), fmt);
        }
        return bmp.GetHicon();
    }

    public void Dispose() => Remove();
}
