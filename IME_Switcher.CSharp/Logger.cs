namespace IMESwitcher;

/// <summary>
/// 异步日志：log() 只入队立即返回，由后台线程统一写文件/缓冲/推送。
/// 避免在全局钩子回调中做同步 I/O 导致系统输入冻结。
/// 注意：不使用静态构造函数与 BlockingCollection（NativeAOT 下曾触发类型初始化失败）。
/// </summary>
public static class Logger
{
    /// <summary>运行日志：写在应用所在目录，每次启动清理，崩溃时转存为崩溃日志</summary>
    public static readonly string LogFile = Path.Combine(CrashReporter.ReportDir, "run.log");

    private static readonly object Gate = new();
    private static readonly Queue<string> Queue = new();
    private static readonly List<string> Buffer = new();
    private const int MaxBuffer = 2000;
    private const int MaxQueue = 2000;
    private static bool _started;

    /// <summary>日志推送到 UI（在后台线程触发，UI 需自行处理线程安全）</summary>
    public static event Action<string>? LogPushed;

    /// <summary>每次启动清理旧运行日志</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(LogFile)) File.Delete(LogFile);
        }
        catch { }
    }

    private static void EnsureStarted()
    {
        if (_started) return;
        lock (Gate)
        {
            if (_started) return;
            _started = true;
            var worker = new Thread(WriteLoop) { IsBackground = true, Name = "log-worker" };
            worker.Start();
        }
    }

    private static void WriteLoop()
    {
        while (true)
        {
            string full;
            lock (Gate)
            {
                while (Queue.Count == 0)
                    Monitor.Wait(Gate);
                full = Queue.Dequeue();
            }

            try
            {
                var dir = Path.GetDirectoryName(LogFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogFile, full + "\n");
            }
            catch { }

            lock (Gate)
            {
                Buffer.Add(full);
                if (Buffer.Count > MaxBuffer)
                    Buffer.RemoveRange(0, Buffer.Count - MaxBuffer);
            }

            try { LogPushed?.Invoke(full); } catch { }
        }
    }

    public static void Log(string msg)
    {
        EnsureStarted();
        var full = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        lock (Gate)
        {
            if (Queue.Count < MaxQueue)
            {
                Queue.Enqueue(full);
                Monitor.Pulse(Gate);
            }
        }
    }

    public static string[] GetRecent(int count = 300)
    {
        lock (Gate)
        {
            var start = Math.Max(0, Buffer.Count - count);
            return Buffer.Skip(start).ToArray();
        }
    }

    /// <summary>绕过队列直接落盘（崩溃报告/watchdog 用）</summary>
    public static void WriteDiagnostic(string report)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(LogFile, report + "\n");
        }
        catch { }
    }
}
