namespace IMESwitcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 自检开关：不建窗口、不装钩子，只跑纯逻辑断言（结果写 selftest.txt，退出码 = 失败条数）
        if (args.Length > 0 && args[0] == "--selftest")
        {
            Environment.Exit(SelfTest());
            return;
        }

        Logger.Reset(); // 每次启动清理旧运行日志
        CrashReporter.Install();
        NativeCrashFilter.Install(); // 原生崩溃（AV 等）捕获，写 crash_native_*.txt

        // 单实例（Global 命名空间，管理员权限下跨会话有效）
        using var mutex = new Mutex(true, @"Global\IMESwitcher_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 按已注册的窗口类名查找（不要按窗口标题找：标题是界面文案，改文案/多语言就静默失效）
            var hwnd = NativeMethods.FindWindowW(MainWindow.ClassName, null);
            if (hwnd != IntPtr.Zero)
            {
                // 隐藏（默认启动到托盘时窗口是隐藏的）用 SW_RESTORE 不会显示窗口，必须分流：
                // 隐藏 → SW_SHOW；最小化 → SW_RESTORE。否则表现为"双击 exe 毫无反应"
                NativeMethods.ShowWindow(hwnd,
                    NativeMethods.IsWindowVisible(hwnd) ? NativeMethods.SW_RESTORE : NativeMethods.SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            else
            {
                Logger.Log("已有实例在运行，但未找到其窗口（可能托盘图标不可用或窗口尚未创建）");
            }
            return;
        }

        var app = new App();
        app.Run();

        try { mutex.ReleaseMutex(); } catch { }
    }

    /// <summary>
    /// 热键逻辑自检（`IME_Switcher.exe --selftest`）。
    /// 覆盖三处易回归的逻辑：ParseHotkey 对 "num +" 这类含 '+' 键名的切分、
    /// SameHotkey 的"同键异名"等价类、修饰键必须精确匹配。返回失败条数（0 = 全过）。
    /// </summary>
    private static int SelfTest()
    {
        var lines = new List<string>();
        int failed = 0;
        void Check(string name, bool ok)
        {
            lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok) failed++;
        }

        var ctrlNumPlus = HotkeyManager.ParseHotkey("ctrl+num +");
        Check("ParseHotkey(\"num +\") 主键为 \"num +\"", HotkeyManager.ParseHotkey("num +")?.MainKey == "num +");
        Check("ParseHotkey(\"ctrl+num +\") 主键 num + 且只有 ctrl 修饰",
            ctrlNumPlus is { MainKey: "num +" } s1 && s1.Modifiers.Count == 1 && s1.Modifiers[0] == "ctrl");
        Check("ParseHotkey(\"f5\") 主键为 \"f5\"", HotkeyManager.ParseHotkey("f5")?.MainKey == "f5");
        Check("ParseHotkey(\"caps lock\") 主键为 \"caps lock\"",
            HotkeyManager.ParseHotkey("caps lock")?.MainKey == "caps lock");

        Check("SameHotkey 忽略大小写", HotkeyManager.SameHotkey("Caps Lock", "caps lock"));
        Check("SameHotkey 忽略修饰键顺序", HotkeyManager.SameHotkey("shift+ctrl+a", "ctrl+shift+a"));
        Check("SameHotkey 兼容旧格式 vk107 与 num +", HotkeyManager.SameHotkey("num +", "vk107"));
        Check("SameHotkey 兼容旧格式 vk116 与 f5", HotkeyManager.SameHotkey("f5", "vk116"));
        Check("SameHotkey 区分不同主键", !HotkeyManager.SameHotkey("ctrl+a", "ctrl+b"));
        Check("SameHotkey 区分鼠标键", !HotkeyManager.SameHotkey("mouse.x1", "mouse.x2"));
        Check("SameHotkey 鼠标键忽略大小写", HotkeyManager.SameHotkey("mouse.x1", "Mouse.X1"));
        Check("SameHotkey 区分键与鼠标", !HotkeyManager.SameHotkey("mouse.x1", "caps lock"));
        Check("SameHotkey 空串不算同一热键", !HotkeyManager.SameHotkey("", ""));
        Check("SameHotkey 区分有无修饰键", !HotkeyManager.SameHotkey("ctrl+num +", "num +"));

        var caps = HotkeyManager.ParseHotkey("caps lock")!;
        var ctrlA = HotkeyManager.ParseHotkey("ctrl+a")!;
        var ctrlShiftA = HotkeyManager.ParseHotkey("ctrl+shift+a")!;
        Check("修饰键：无修饰键热键在按着 ctrl 时不触发", !HotkeyManager.ModifiersMatch(caps, m => m == "ctrl"));
        Check("修饰键：无修饰键热键在无修饰键时触发", HotkeyManager.ModifiersMatch(caps, _ => false));
        Check("修饰键：要求 ctrl 且按下 ctrl 时触发", HotkeyManager.ModifiersMatch(ctrlA, m => m == "ctrl"));
        Check("修饰键：要求 ctrl 但没按 ctrl 时不触发", !HotkeyManager.ModifiersMatch(ctrlA, _ => false));
        Check("修饰键：要求 ctrl 却多按 shift 时不触发", !HotkeyManager.ModifiersMatch(ctrlA, m => m is "ctrl" or "shift"));
        Check("修饰键：要求 ctrl+shift 且两者都按下时触发",
            HotkeyManager.ModifiersMatch(ctrlShiftA, m => m is "ctrl" or "shift"));

        // 右 Win(0x5C) 曾经漏判：只认左 Win 时，无修饰热键会被"右Win+A"误触发，
        // 录制时按右Win+A 也会存成 "a"（修饰键丢失）
        Check("win 修饰键：左 Win(0x5B) 按下算按下",
            HotkeyManager.IsModifierDown("win", vk => vk == HotkeyManager.VkLWin));
        Check("win 修饰键：右 Win(0x5C) 按下也算按下",
            HotkeyManager.IsModifierDown("win", vk => vk == HotkeyManager.VkRWin));
        Check("win 修饰键：左右都没按时为假", !HotkeyManager.IsModifierDown("win", _ => false));
        Check("修饰键：按着右 Win 时无修饰热键不触发（走统一判定）",
            !HotkeyManager.ModifiersMatch(caps,
                m => m == "win" && HotkeyManager.IsModifierDown("win", vk => vk == HotkeyManager.VkRWin)));

        var report = string.Join(Environment.NewLine, lines);
        var path = Path.Combine(CrashReporter.ReportDir, "selftest.txt");
        try { File.WriteAllText(path, report + Environment.NewLine); } catch { }
        Console.WriteLine(report);
        Console.WriteLine(failed == 0 ? $"ALL PASS ({lines.Count})" : $"{failed} FAILED / {lines.Count}");
        return failed;
    }
}
