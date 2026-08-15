using System.Text;

namespace IMESwitcher;

/// <summary>
/// 崩溃报告：未捕获异常时在应用所在目录生成崩溃报告文件，附带异常堆栈与最近日志。
/// 原生崩溃（AccessViolation 等）由 NativeCrashFilter 单独捕获。
/// </summary>
public static class CrashReporter
{
    public static string ReportDir
    {
        get
        {
            var p = Environment.ProcessPath;
            return p != null ? (Path.GetDirectoryName(p) ?? ".") : ".";
        }
    }

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteReport("未处理异常(CLR)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteReport("未观察任务异常", e.Exception);
            e.SetObserved();
        };
    }

    private static void WriteReport(string kind, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("IME 输入法切换 - 崩溃报告");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"系统: {Environment.OSVersion}");
        sb.AppendLine($"程序: {Environment.ProcessPath}");
        sb.AppendLine($"类型: {kind}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine(ex?.ToString() ?? "(无异常对象)");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine("最近日志:");
        sb.AppendLine(string.Join("\n", Logger.GetRecent(80)));
        try
        {
            Directory.CreateDirectory(ReportDir);
            var path = Path.Combine(ReportDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, sb.ToString());
            BackupRunLog(); // 完整运行日志转存为崩溃日志
            Logger.WriteDiagnostic($"[crash] {kind} -> 崩溃报告: {path}");
        }
        catch { }
    }

    /// <summary>把当前运行日志（run.log）转存为崩溃日志（crash_run_*.log）</summary>
    public static void BackupRunLog()
    {
        try
        {
            if (File.Exists(Logger.LogFile))
            {
                var logPath = Path.Combine(ReportDir, $"crash_run_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.Copy(Logger.LogFile, logPath, true);
            }
        }
        catch { }
    }
}
