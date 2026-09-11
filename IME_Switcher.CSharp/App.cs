namespace IMESwitcher;

/// <summary>
/// 应用协调器：持有配置、主窗口、托盘与热键管理器，
/// 负责监听启停、热键录制、设置保存与退出。纯 Win32 消息循环驱动。
/// </summary>
public sealed class App
{
    public AppConfig Settings { get; private set; }
    public bool Quitting { get; private set; }

    private MainWindow _win = null!;
    private readonly HotkeyManager _hotkey = new();
    private readonly TrayIcon _tray = new();
    private bool _listening;
    private bool _wasListening;
    private readonly object _stateLock = new(); // 保护 _listening 的 check-then-act（热键回调在后台线程）

    public App()
    {
        Settings = Config.Load();
    }

    public void Run()
    {
        // 自愈：旧版可能把开关热键存成与切换热键相同（冲突），启动时自动清空开关热键
        if (!string.IsNullOrEmpty(Settings.ToggleHotkey) &&
            HotkeyManager.SameHotkey(Settings.Hotkey, Settings.ToggleHotkey))
        {
            Logger.Log($"检测到开关热键与切换热键相同（{Settings.ToggleHotkey}），已自动清空开关热键，请重新设置");
            Settings.ToggleHotkey = "";
            Config.Save(Settings);
        }
        // 自愈：以注册表实际状态为准（用户在任务管理器里禁用自启后，界面不应继续显示"已启用"）
        bool autostartReal = Config.IsAutostartEnabled();
        if (Settings.Autostart != autostartReal)
        {
            Logger.Log($"开机自启状态校正: 配置={Settings.Autostart} 注册表={autostartReal}");
            Settings.Autostart = autostartReal;
            Config.Save(Settings);
        }

        _win = new MainWindow(this);
        if (!_win.Create())
        {
            Logger.WriteDiagnostic("[fatal] 主窗口创建失败");
            return;
        }

        // 居中窗口
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        NativeMethods.SetWindowPos(_win.Handle, IntPtr.Zero,
            (screenW - MainWindow.W) / 2, (screenH - MainWindow.H) / 2,
            0, 0, 0x0001 | 0x0002); // SWP_NOSIZE | SWP_NOZORDER

        // 托盘图标若添加失败就禁止隐藏窗口：窗口是 WS_EX_TOOLWINDOW（无任务栏按钮），
        // 藏起来又没有托盘入口，用户将再也找不到、也退不掉程序
        bool trayOk = _tray.Add(_win.Handle);
        _hotkey.Start(); // 钩子线程常驻，监听/录制共用

        // 配置 -> UI
        _win.HotkeyText = string.IsNullOrEmpty(Settings.Hotkey) ? "未设置" : Settings.Hotkey;
        _win.ToggleText = string.IsNullOrEmpty(Settings.ToggleHotkey) ? "未设置" : Settings.ToggleHotkey;
        _win.Method = Settings.Method;
        _win.Autostart = Settings.Autostart;
        _win.TrayStart = Settings.StartToTray;
        _win.SyncSwitchAnim();

        Logger.Log("程序启动完成");
        Logger.Log($"热键: {Settings.Hotkey}, 方法: {(Settings.Method == 1 ? "API" : "模拟Win+Space")}");

        if (Settings.Autostart || Settings.StartToTray)
            StartListening();
        if (Settings.StartToTray && trayOk)
            _win.Hide();

        // 消息循环
        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
        Logger.Log("消息循环已退出");

        QuitCleanup();
    }

    private void QuitCleanup()
    {
        _hotkey.Dispose();
        _tray.Dispose();
    }

    // ---------------- 监听 ----------------

    public bool StartListening()
    {
        // 热键回调在后台线程调用本方法，check-then-act 必须原子，否则连按两次会双 Start/双 Stop（界面与规则表不一致）
        lock (_stateLock)
        {
            if (_listening) return true;
            if (HotkeyManager.ParseHotkey(Settings.Hotkey) == null)
            {
                Logger.Log($"热键解析失败: {Settings.Hotkey}");
                _win.ShowNotice("切换热键未设置或无效");
                return false;
            }
            // 非法开关热键会被 SetRules 静默丢弃（表现为"设置成功却不生效"），这里清掉并提示
            if (!string.IsNullOrEmpty(Settings.ToggleHotkey) &&
                HotkeyManager.ParseHotkey(Settings.ToggleHotkey) == null)
            {
                Logger.Log($"开关热键解析失败: {Settings.ToggleHotkey}，已清空");
                Settings.ToggleHotkey = "";
                Config.Save(Settings);
                _win.ShowNotice("开关热键无效，已清空");
            }

            var rules = new List<(string, Action)>
            {
                (Settings.Hotkey, () => ImeSwitcher.ToggleIme(Settings.Method)),
            };
            if (!string.IsNullOrEmpty(Settings.ToggleHotkey))
            {
                rules.Add((Settings.ToggleHotkey, ToggleListeningFromHotkey));
            }
            _hotkey.SetRules(rules);
            _listening = true;
            Logger.Log($"监听启动，切换热键: {Settings.Hotkey}"
                + (string.IsNullOrEmpty(Settings.ToggleHotkey) ? "" : $"，开关热键: {Settings.ToggleHotkey}"));
            _win.SetListeningState(true, Settings.Hotkey, Settings.ToggleHotkey);
            return true;
        }
    }

    public void StopListening()
    {
        lock (_stateLock)
        {
            if (!_listening) return;
            _hotkey.SetRules(new List<(string, Action)>());
            _listening = false;
            Logger.Log("监听已停止");
            _win.SetListeningState(false, null, null);
        }
    }

    private void ToggleListeningFromHotkey()
    {
        // lock 可重入：持锁后再调用 Start/StopListening 不会死锁
        lock (_stateLock)
        {
            if (_listening) StopListening();
            else StartListening();
        }
    }

    public void ManualTest() => ImeSwitcher.ToggleIme(Settings.Method, force: true);

    // ---------------- 设置 ----------------

    public void SetMethod(int method)
    {
        Settings.Method = method;
        Config.Save(Settings);
        _win.Method = method;
        _win.Refresh();
        Logger.Log($"切换方式改为: {(method == 1 ? "API" : "模拟")}");
    }

    public void SetAutostart(bool enabled)
    {
        var ok = Config.SetAutostart(enabled);
        if (ok)
        {
            Settings.Autostart = enabled;
            Config.Save(Settings);
            _win.Autostart = enabled;
            _win.Refresh();
            Logger.Log($"开机自启设置为: {enabled}");
        }
    }

    public void SetTrayStart(bool enabled)
    {
        Settings.StartToTray = enabled;
        Config.Save(Settings);
        _win.TrayStart = enabled;
        _win.Refresh();
        Logger.Log($"默认启动到托盘: {(enabled ? "已启用" : "已禁用")}");
    }

    // ---------------- 热键录制 ----------------

    public void StartRecording(string target)
    {
        Logger.WriteDiagnostic($"[dbg] App.StartRecording: target={target}");
        // 只在真正进入录制时记录"录制前是否在监听"：录制过程中再点一次"更改"时 _listening 已是 false，
        // 无条件覆盖会让录制结束后不再恢复监听（全局热键静默失效）
        if (!_hotkey.Recording) _wasListening = _listening;
        if (_listening) StopListening();
        _win.SetRecordingStarted(target);
        _hotkey.StartRecording(
            onRecorded: value => FinishRecording(target, value),
            onCancel: CancelRecording);
    }

    public void CancelRecording()
    {
        Logger.WriteDiagnostic("[dbg] App.CancelRecording");
        _hotkey.CancelRecording();
        _win.SetRecordingCanceled();
        if (_wasListening) StartListening();
    }

    private void FinishRecording(string target, string value)
    {
        // 开关热键与切换热键不能相同（按解析后的规格比较，兼容大小写/旧格式/修饰键顺序）
        if (target == "toggle" && HotkeyManager.SameHotkey(value, Settings.Hotkey))
        {
            Logger.Log($"开关热键不能与切换热键相同（{value}），已取消设置");
            _win.ShowNotice("开关热键不能与切换热键相同");
            _win.SetRecordingCanceled();
            if (_wasListening) StartListening();
            return;
        }
        if (target == "hotkey" && !string.IsNullOrEmpty(Settings.ToggleHotkey) &&
            HotkeyManager.SameHotkey(value, Settings.ToggleHotkey))
        {
            Logger.Log($"切换热键不能与开关热键相同（{value}），已取消设置");
            _win.ShowNotice("切换热键不能与开关热键相同");
            _win.SetRecordingCanceled();
            if (_wasListening) StartListening();
            return;
        }

        Logger.WriteDiagnostic($"[dbg] FinishRecording 进入: target={target}, value={value}");
        if (target == "toggle") Settings.ToggleHotkey = value;
        else Settings.Hotkey = value;
        Config.Save(Settings);
        Logger.WriteDiagnostic($"[dbg] FinishRecording 配置已保存: hotkey={Settings.Hotkey}");
        Logger.Log($"{(target == "toggle" ? "开关" : "切换")}热键已保存: {value}");
        _win.SetRecordingFinished(target, value);
        Logger.WriteDiagnostic("[dbg] FinishRecording UI 已更新");
        if (_wasListening) StartListening();
        Logger.WriteDiagnostic("[dbg] FinishRecording 完成");
    }

    // ---------------- 窗口 ----------------

    public void HideToTray()
    {
        // 托盘不可用时绝不隐藏：窗口无任务栏按钮，藏了就再没有入口唤出或退出
        if (!_tray.Added)
        {
            Logger.Log("托盘图标不可用，已阻止隐藏窗口");
            return;
        }
        if (_win != null) _win.Hide();
        Logger.Log("窗口已隐藏到托盘");
    }

    public void ShowWindow()
    {
        _win.Activate();
        Logger.Log("已从托盘显示窗口");
    }

    public void Quit()
    {
        Quitting = true;
        StopListening();
        _tray.Remove(); // 先移除托盘图标
        // 销毁主窗口 → WM_DESTROY → PostQuitMessage → 退出消息循环 → 进程结束
        NativeMethods.DestroyWindow(_win.Handle);
    }
}
