using System.Drawing;
using System.Runtime.InteropServices;

namespace IMESwitcher;

/// <summary>系统托盘图标（Shell_NotifyIcon）</summary>
internal sealed class TrayIcon : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _hicon;
    private bool _added;

    public void Add(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _hicon = CreateIcon();
        var data = BuildData();
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
        if (!_added)
            Logger.Log("托盘图标添加失败");
    }

    public void Remove()
    {
        if (_added)
        {
            var data = BuildData();
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }
        _appIcon?.Dispose();
        _appIcon = null;
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
        catch { }
        // 兜底：自绘（句柄交由 Shell_NotifyIcon 使用，进程结束时释放）
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
