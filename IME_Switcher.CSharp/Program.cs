namespace IMESwitcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Logger.Reset(); // 每次启动清理旧运行日志
        CrashReporter.Install();
        NativeCrashFilter.Install(); // 原生崩溃（AV 等）捕获，写 crash_native_*.txt

        // 单实例（Global 命名空间，管理员权限下跨会话有效）
        using var mutex = new Mutex(true, @"Global\IMESwitcher_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            var hwnd = NativeMethods.FindWindowW(null, "输入法一键切换");
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            return;
        }

        var app = new App();
        app.Run();

        try { mutex.ReleaseMutex(); } catch { }
    }
}
