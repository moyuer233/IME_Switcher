using System.Drawing;
using System.Runtime.InteropServices;

namespace IMESwitcher;

/// <summary>系统托盘图标（Shell_NotifyIcon）</summary>
internal sealed class TrayIcon : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _hicon;  // 自绘图标句柄（GetHicon 创建，需要自己 DestroyIcon）
    private Icon? _appIcon; // exe 内嵌图标对象（句柄随 Icon 释放）
    private bool _added;

    /// <summary>托盘图标是否真的加上了；false 时调用方必须禁止隐藏窗口，否则没有任务栏也没有托盘入口，用户无法唤出/退出</summary>
    public bool Added => _added;

    public bool Add(IntPtr hwnd)
    {
        _hwnd = hwnd;
        ReleaseIcon(); // 重复 Add（托盘重建）时先释放上一次的图标，否则图标句柄泄漏
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
        ReleaseIcon();
    }

    /// <summary>图标句柄只有两条来源，统一在这里释放：内嵌图标交给 Icon 对象，自绘图标自己 DestroyIcon</summary>
    private void ReleaseIcon()
    {
        if (_appIcon != null)
        {
            _appIcon.Dispose();
            _appIcon = null;
        }
        else if (_hicon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hicon);
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

    private IntPtr CreateIcon()
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
