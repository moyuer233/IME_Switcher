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

    private static readonly object Gate = new();     // 保护 Queue / Buffer / _started / _stopping
    private static readonly object FileGate = new(); // 只保护文件写入：磁盘 I/O 不能占用 Gate，
                                                     // 否则钩子回调里的 Log() 会被别的线程写盘阻塞
    private static readonly Queue<string> Queue = new();
    private static readonly List<string> Buffer = new();
    private const int MaxBuffer = 2000;
    private const int MaxQueue = 2000;
    private static bool _started;
    private static bool _stopping;  // Shutdown() 置位：队列排空后日志线程自行退出
    private static Thread? _worker;

    /// <summary>写盘失败次数与最近错误：日志写不进去时必须留痕，否则 run.log 看起来"没有错误"、实际什么都没记</summary>
    public static int WriteFailures { get; private set; }
    public static string? LastWriteError { get; private set; }

    /// <summary>队列满时被丢弃的条数。静默丢弃会让 run.log 看起来"一切正常"，必须记账</summary>
    public static int DroppedCount { get; private set; }

    /// <summary>外部登记一次写盘失败（崩溃路径用：它自己写不了文件时，至少要留下计数与原因）</summary>
    public static void NoteWriteFailure(string reason)
    {
        WriteFailures++;
        LastWriteError = reason;
    }

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
            _worker = new Thread(WriteLoop) { IsBackground = true, Name = "log-worker" };
            _worker.Start();
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
                {
                    if (_stopping) return;    // 队列已排空且收到退出请求
                    Monitor.Wait(Gate, 200);  // 带超时：即使 Pulse 丢失也能醒过来
                }
                full = Queue.Dequeue();
            }

            WriteLine(full);

            lock (Gate)
            {
                Buffer.Add(full);
                if (Buffer.Count > MaxBuffer)
                    Buffer.RemoveRange(0, Buffer.Count - MaxBuffer);
            }

            try { LogPushed?.Invoke(full); } catch { }
        }
    }

    /// <summary>
    /// 落盘。与 WriteDiagnostic 共用 FileGate —— File.AppendAllText 默认 FileShare.Read，
    /// 两处并发打开同一文件必然冲突，而丢掉的恰恰是最关键的诊断行（实测丢过一整行）。
    /// </summary>
    private static void WriteLine(string full)
    {
        lock (FileGate)
        {
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
        }
    }

    /// <summary>
    /// 退出前排空队列并等日志线程写完。日志线程是后台线程，进程结束会被直接掐死 ——
    /// 不排空的话队列里最后几行（"消息循环已退出"、配置保存失败等）永远不落盘，
    /// 这正是"run.log 看不到退出原因"的根源。调用方在退出流程的最后一步调它。
    /// </summary>
    public static void Shutdown(int timeoutMs = 1500)
    {
        Thread? t;
        lock (Gate)
        {
            _stopping = true;
            Monitor.PulseAll(Gate);
            t = _worker;
        }
        if (t is { IsAlive: true }) t.Join(timeoutMs);
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
            else
            {
                DroppedCount++; // 队列满：记账，绝不静默丢弃
            }
        }
    }

    /// <summary>
    /// 取最近日志（崩溃路径专用）：最多等 timeoutMs 拿锁，拿不到就返回空数组。
    /// 崩溃时若有线程正持 Gate（日志线程卡在磁盘 I/O 很常见），死等会让崩溃报告永远写不出来。
    /// </summary>
    public static string[] GetRecentNoWait(int count, int timeoutMs = 200)
    {
        if (!Monitor.TryEnter(Gate, timeoutMs)) return Array.Empty<string>();
        try
        {
            var start = Math.Max(0, Buffer.Count - count);
            return Buffer.Skip(start).ToArray();
        }
        finally { Monitor.Exit(Gate); }
    }

    public static string[] GetRecent(int count = 300)
    {
        lock (Gate)
        {
            var start = Math.Max(0, Buffer.Count - count);
            return Buffer.Skip(start).ToArray();
        }
    }

    /// <summary>绕过队列直接落盘（崩溃报告/watchdog 用），与日志线程共用 FileGate 串行化</summary>
    public static void WriteDiagnostic(string report) => WriteLine(report);

    /// <summary>内存中保有的全部日志（崩溃转存用：不依赖磁盘 run.log，日志是异步落盘的）。同样不等锁</summary>
    public static string[] GetAllNoWait(int timeoutMs = 200)
    {
        if (!Monitor.TryEnter(Gate, timeoutMs)) return Array.Empty<string>();
        try { return Buffer.ToArray(); }
        finally { Monitor.Exit(Gate); }
    }

    /// <summary>内存中保有的全部日志（界面/自检用，可以等锁）</summary>
    public static string[] GetAll()
    {
        lock (Gate) return Buffer.ToArray();
    }
}
