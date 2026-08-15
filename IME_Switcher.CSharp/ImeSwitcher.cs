namespace IMESwitcher;

/// <summary>
/// 输入法切换核心（对应旧版 ime_switcher.py）
/// API 方式：LoadKeyboardLayout + ActivateKeyboardLayout + PostMessage（异步，绝不死锁）
/// 模拟方式：keybd_event 模拟 Win+Space
/// </summary>
public static class ImeSwitcher
{
    // 主语言 ID
    public const int PRIMARY_ENGLISH = 0x09;
    public const int PRIMARY_CHINESE = 0x04;
    public const int LANG_EN = 0x0409;
    public const int LANG_ZH = 0x0804;

    private static DateTime _lastToggle = DateTime.MinValue;
    private const double DebounceSeconds = 0.3;

    public static int GetCurrentLangId()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var tid = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var hkl = NativeMethods.GetKeyboardLayout(tid);
        return (int)(hkl.ToInt64() & 0xFFFF);
    }

    public static int PrimaryLangId(int langId) => langId & 0x3FF;

    public static bool IsChinese(int langId) => PrimaryLangId(langId) == PRIMARY_CHINESE;

    public static int[] GetInstalledLangIds()
    {
        try
        {
            int count = (int)NativeMethods.GetKeyboardLayoutList(0, Array.Empty<IntPtr>());
            if (count <= 0) return Array.Empty<int>();
            var list = new IntPtr[count];
            int n = (int)NativeMethods.GetKeyboardLayoutList(count, list);
            var result = new List<int>();
            foreach (var hkl in list)
            {
                var langId = (int)(hkl.ToInt64() & 0xFFFF);
                if (!result.Contains(langId)) result.Add(langId);
            }
            return result.ToArray();
        }
        catch { return Array.Empty<int>(); }
    }

    public static int PickTargetLangId(bool wantChinese)
    {
        var installed = GetInstalledLangIds();
        if (wantChinese)
        {
            if (installed.Contains(LANG_ZH)) return LANG_ZH;
            foreach (var id in installed)
                if (IsChinese(id)) return id;
            return LANG_ZH;
        }
        else
        {
            if (installed.Contains(LANG_EN)) return LANG_EN;
            foreach (var id in installed)
                if (PrimaryLangId(id) == PRIMARY_ENGLISH) return id;
            return LANG_EN;
        }
    }

    /// <summary>从系统已加载的布局列表中查找目标语言对应的 hkl（前台线程认识的真实句柄）</summary>
    public static IntPtr FindLoadedHkl(int targetLangId)
    {
        try
        {
            int count = (int)NativeMethods.GetKeyboardLayoutList(0, Array.Empty<IntPtr>());
            if (count <= 0) return IntPtr.Zero;
            var list = new IntPtr[count];
            int n = (int)NativeMethods.GetKeyboardLayoutList(count, list);
            for (int i = 0; i < n; i++)
            {
                if (((int)(list[i].ToInt64() & 0xFFFF)) == targetLangId) return list[i];
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    /// <summary>API 方式切换（异步 PostMessage，绝不阻塞；发给自己窗口由上层拦截）</summary>
    public static bool SwitchApi(int targetLangId)
    {
        Logger.Log($"API: 尝试切换到 0x{targetLangId:X4}");
        try
        {
            // 优先使用系统已加载布局的句柄（前台线程认识它，切换更可靠）
            var hkl = FindLoadedHkl(targetLangId);
            if (hkl == IntPtr.Zero)
                hkl = NativeMethods.LoadKeyboardLayout($"0x{targetLangId:X8}", NativeMethods.KLF_ACTIVATE);
            if (hkl == IntPtr.Zero)
            {
                Logger.Log("API: 找不到目标布局句柄");
                return false;
            }
            NativeMethods.ActivateKeyboardLayout(hkl, NativeMethods.KLF_SETFORPROCESS);

            var hwnd = NativeMethods.GetForegroundWindow();
            var ok = NativeMethods.PostMessageW(hwnd, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
            Logger.Log($"API: PostMessageW 返回 {ok}");
            NativeMethods.NotifyWinEvent(NativeMethods.EVENT_OBJECT_INPUTSTATE, hwnd, 0, 0);
            return ok;
        }
        catch (Exception e)
        {
            Logger.Log($"API 异常: {e.Message}");
            return false;
        }
    }

    /// <summary>模拟 Win+Space 切换</summary>
    public static bool SwitchSimulate(int targetLangId)
    {
        Logger.Log("模拟: 发送 Win+Space");
        try
        {
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            NativeMethods.keybd_event(NativeMethods.VK_SPACE, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            NativeMethods.keybd_event(NativeMethods.VK_SPACE, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(50);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Logger.Log("模拟: 发送完成");
            return true;
        }
        catch (Exception e)
        {
            Logger.Log($"模拟异常: {e.Message}");
            return false;
        }
    }

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>
    /// 切换入口。force=true（手动切换按钮）：前台若是自身窗口则改用模拟方式做系统级切换；
    /// force=false（热键触发）：前台若是自身窗口则忽略并提示（应切换到目标应用后使用热键）。
    /// </summary>
    public static void ToggleIme(int method, bool force = false)
    {
        if ((DateTime.Now - _lastToggle).TotalSeconds < DebounceSeconds) return;
        _lastToggle = DateTime.Now;

        if (!force && IsOwnWindow(NativeMethods.GetForegroundWindow()))
        {
            Logger.Log("主窗口在前台，请切换到目标应用后使用热键");
            return;
        }
        if (IsOwnWindow(NativeMethods.GetForegroundWindow()))
        {
            Logger.Log("前台为自身窗口，手动切换使用模拟方式（系统级切换）");
            method = 2;
        }

        int current = GetCurrentLangId();
        Logger.Log($"当前语言ID: 0x{current:X4}");

        bool wantChinese = !IsChinese(current);
        int target = PickTargetLangId(wantChinese);
        string targetName = wantChinese ? "中文" : "英文";
        Logger.Log($"切换到 {targetName} (目标 0x{target:X4})");

        bool success;
        if (method == 1)
        {
            success = SwitchApi(target);
            if (!success)
            {
                Logger.Log("API 切换失败，回退为模拟 Win+Space");
                success = SwitchSimulate(target);
            }
            else
            {
                // PostMessage 异步生效，稍候验证；未生效则回退模拟
                Thread.Sleep(150);
                int after = GetCurrentLangId();
                if (after == target)
                {
                    Logger.Log($"验证成功，当前 0x{after:X4}");
                }
                else
                {
                    Logger.Log($"API 未生效（当前 0x{after:X4}），回退模拟 Win+Space");
                    success = SwitchSimulate(target);
                }
            }
        }
        else
        {
            success = SwitchSimulate(target);
        }

        Logger.Log(success ? $"切换{targetName}指令已执行" : $"切换{targetName}失败");
    }
}
