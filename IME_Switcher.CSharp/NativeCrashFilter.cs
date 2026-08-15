using System.Runtime.InteropServices;
using System.Text;

namespace IMESwitcher;

/// <summary>
/// 原生崩溃过滤器：捕获 NativeAOT 下的 AccessViolation 等静默崩溃
/// （这类崩溃不触发托管异常，进程会直接终止且无报告）。
/// </summary>
public static class NativeCrashFilter
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint TopLevelFilter(nint exceptionInfo);
    private static TopLevelFilter? _filterDelegate;

    public static void Install()
    {
        _filterDelegate = Handler;
        NativeMethods.SetUnhandledExceptionFilter(
            Marshal.GetFunctionPointerForDelegate(_filterDelegate));
    }

    private static nint Handler(nint exceptionInfo)
    {
        try
        {
            uint code = 0;
            nint addr = 0;
            if (exceptionInfo != 0)
            {
                // EXCEPTION_POINTERS: ExceptionRecord* 在偏移 0，ContextRecord* 在偏移 8
                nint exRecord = Marshal.ReadIntPtr(exceptionInfo);
                if (exRecord != 0)
                {
                    code = (uint)Marshal.ReadInt32(exRecord); // ExceptionCode
                    addr = Marshal.ReadIntPtr(exRecord, 8);    // ExceptionAddress
                }
            }
            var sb = new StringBuilder();
            sb.AppendLine("[native-crash] 原生崩溃");
            sb.AppendLine($"ExceptionCode=0x{code:X8} Address=0x{addr:X}");
            var frames = new IntPtr[40];
            uint n = NativeMethods.CaptureStackBackTrace(0, (uint)frames.Length, frames, out _);
            for (uint i = 0; i < n; i++)
                sb.AppendLine($"  frame[{i}] 0x{frames[i].ToInt64():X}");
            WriteRaw(sb.ToString());
        }
        catch { }
        return 0; // 交给系统默认处理
    }

    private static void WriteRaw(string text)
    {
        try
        {
            string dir = CrashReporter.ReportDir;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"crash_native_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, text + "\n" + string.Join("\n", Logger.GetRecent(50)));
            CrashReporter.BackupRunLog(); // 完整运行日志转存为崩溃日志
        }
        catch { }
    }
}
