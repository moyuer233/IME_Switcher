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

    /// <summary>写盘失败次数与最近错误：日志写不进去时必须留痕，否则 run.log 看起来"没有错误"、实际什么都没记</summary>
    public static int WriteFailures { get; private set; }
    public static string? LastWriteError { get; private set; }

    /// <summary>日志推送到 UI（在后台线程触发，UI 需自行处理线程安全）</summary>
    public static event Action<string>? LogPushed;

    /// <summary>每次启动清理旧运行日志</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(LogFile)) File.Delete(LogFile);
        }
        catch (Exception e)
        {
            WriteFailures++;
            LastWriteError = e.Message;
        }
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
                LastWriteError = null;
            }
            catch (Exception e)
            {
                // 不能在 catch 里再写日志（会递归），只留痕，由崩溃报告暴露出来
                WriteFailures++;
                LastWriteError = e.Message;
            }

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
        catch (Exception e)
        {
            WriteFailures++;
            LastWriteError = e.Message;
        }
    }

    /// <summary>内存中保有的全部日志（崩溃转存用：不依赖磁盘 run.log，日志是异步落盘的）</summary>
    public static string[] GetAll()
    {
        lock (Gate) return Buffer.ToArray();
    }
}
