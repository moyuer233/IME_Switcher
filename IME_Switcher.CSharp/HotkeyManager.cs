using System.Runtime.InteropServices;

namespace IMESwitcher;

/// <summary>解析后的热键规格</summary>
public sealed class HotkeySpec
{
    public const string TypeKeyboard = "keyboard";
    public const string TypeMouse = "mouse";

    public string Type = TypeKeyboard;
    public List<string> Modifiers = new(); // ctrl / shift / alt / win
    public string MainKey = "";            // 主键名称
    public string MouseButton = "";        // x1 / x2
    public uint MainVk;                    // 预解析的主键 VK 码（0 = 无效）；避免钩子回调里每次按键都重查表
}

/// <summary>
/// 全局热键钩子（WH_KEYBOARD_LL + WH_MOUSE_LL）与热键录制。
/// 钩子在独立 STA 线程安装，回调只做规则匹配并触发后台任务，绝不阻塞系统输入。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly object _lock = new();
    private readonly List<(HotkeySpec Spec, Action Callback)> _rules = new();
    private NativeMethods.HookProc? _keyProc;
    private NativeMethods.HookProc? _mouseProc;
    private IntPtr _keyHook;
    private IntPtr _mouseHook;
    private Thread? _hookThread;
    private volatile bool _running;

    // 录制状态
    private volatile bool _recording;
    private volatile Action<string>? _onRecorded;
    private volatile Action? _onCancel;

    public bool Recording => _recording;

    public void SetRules(List<(string Hotkey, Action Callback)> rules)
    {
        lock (_lock)
        {
            _rules.Clear();
            foreach (var (hotkey, cb) in rules)
            {
                var spec = ParseHotkey(hotkey);
                if (spec != null) _rules.Add((spec, cb));
            }
        }
    }

    /// <summary>
    /// 启动钩子线程。整个检查-设置放在 _lock 内（原来非原子：两个线程同时进来会装两套钩子 → 热键双触发）；
    /// 并且拒绝在上一个线程还活着时再装一套。
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            if (_hookThread is { IsAlive: true })
            {
                Logger.Log("上一个钩子线程尚未退出，拒绝重复安装钩子");
                return;
            }
            _running = true;
            Logger.WriteDiagnostic("[dbg] HotkeyManager.Start: 启动钩子线程");
            _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "hook-thread" };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
        }
    }

    /// <summary>
    /// 停止钩子线程。Join 必须放在锁外（否则会阻塞钩子回调里的规则匹配）；
    /// 超时后**绝不能把 _hookThread 置 null** —— 旧线程还活着并持有钩子句柄，
    /// 置 null 会让 Start() 再装一套钩子（热键双触发），而旧线程退出时还会把自己字段里的句柄
    /// Unhook 掉（那可能已经属于新线程）。
    /// </summary>
    public void Stop()
    {
        Thread? t;
        lock (_lock)
        {
            _running = false;
            t = _hookThread;
        }
        if (t is { IsAlive: true })
        {
            // 钩子线程阻塞在 GetMessageW 上，必须投递 WM_QUIT 唤醒它，
            // 否则 Join 必然超时、UnhookWindowsHookEx 永远执行不到（钩子残留到进程结束）
            NativeMethods.PostThreadMessageW((uint)t.ManagedThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (!t.Join(1000) && !t.Join(3000))
                Logger.Log("钩子线程未在 4 秒内退出，保留线程引用（不会重复安装钩子），钩子将由进程结束回收");
        }
        if (t == null || !t.IsAlive)
        {
            lock (_lock)
            {
                _hookThread = null;
                _keyHook = IntPtr.Zero;
                _mouseHook = IntPtr.Zero;
            }
        }
    }

    private void HookThreadMain()
    {
        // 整个线程主体包一层：SetWindowsHookEx 失败、GetMessageW 返回 -1、Unhook 抛异常
        // 都会终结这个线程 —— 没有 catch 的话线程直接死掉，而"钩子没装上"此前只有一行 dbg 日志
        try
        {
            _keyProc = KeyboardProc;
            _mouseProc = MouseProc;
            _keyHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, _keyProc, NativeMethods.GetModuleHandle(null), 0);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
            Logger.WriteDiagnostic($"[dbg] 钩子安装: 键盘=0x{_keyHook.ToInt64():X}, 鼠标=0x{_mouseHook.ToInt64():X}");
            if (_keyHook == IntPtr.Zero)
                Logger.Log("键盘钩子安装失败，热键不会生效（通常是权限不足，需要以管理员身份运行）");
            if (_mouseHook == IntPtr.Zero)
                Logger.Log("鼠标钩子安装失败，鼠标侧键热键不会生效");

            // 钩子回调由本线程的消息循环驱动
            while (_running)
            {
                if (!NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
                    break;
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            if (_keyHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_keyHook);
            if (_mouseHook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _keyHook = IntPtr.Zero;
            _mouseHook = IntPtr.Zero;
        }
        catch (Exception e)
        {
            Logger.Log($"钩子线程异常退出: {e.Message}");
        }
    }

    // 左右 Win 键：名称表与 ModDown 判定必须共用这一组常量 ——
    // 0x5B 曾散落在三处，改一处漏两处就是"录制出来能存、匹配不到"的静默失效
    public const uint VkLWin = 0x5B;
    public const uint VkRWin = 0x5C;

    private static readonly Dictionary<uint, string> VkName = new()
    {
        [0x08] = "backspace", [0x09] = "tab", [0x0D] = "enter", [0x13] = "pause",
        [0x14] = "caps lock", [0x1B] = "esc", [0x20] = "space", [0x21] = "page up",
        [0x22] = "page down", [0x23] = "end", [0x24] = "home", [0x25] = "left",
        [0x26] = "up", [0x27] = "right", [0x28] = "down", [0x2C] = "print screen",
        [0x2D] = "insert", [0x2E] = "delete", [VkLWin] = "win", [0x5D] = "menu",
        [0x90] = "num lock",
        // 数字小键盘
        [0x60] = "num 0", [0x61] = "num 1", [0x62] = "num 2", [0x63] = "num 3",
        [0x64] = "num 4", [0x65] = "num 5", [0x66] = "num 6", [0x67] = "num 7",
        [0x68] = "num 8", [0x69] = "num 9", [0x6A] = "num *", [0x6B] = "num +",
        [0x6C] = "num sep", [0x6D] = "num -", [0x6E] = "num .", [0x6F] = "num /",
    };

    private static string VkToName(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A)
            return ((char)vk).ToString().ToLowerInvariant();
        if (vk >= 0x30 && vk <= 0x39)
            return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87)
            return $"f{vk - 0x70 + 1}";
        return VkName.TryGetValue(vk, out var n) ? n : $"vk{vk}";
    }

    private static readonly Dictionary<string, uint> NameVk = new()
    {
        ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["pause"] = 0x13,
        ["caps lock"] = 0x14, ["esc"] = 0x1B, ["space"] = 0x20, ["page up"] = 0x21,
        ["page down"] = 0x22, ["end"] = 0x23, ["home"] = 0x24, ["left"] = 0x25,
        ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["print screen"] = 0x2C,
        ["insert"] = 0x2D, ["delete"] = 0x2E, ["win"] = VkLWin, ["menu"] = 0x5D,
        ["num lock"] = 0x90,
        // 数字小键盘
        ["num 0"] = 0x60, ["num 1"] = 0x61, ["num 2"] = 0x62, ["num 3"] = 0x63,
        ["num 4"] = 0x64, ["num 5"] = 0x65, ["num 6"] = 0x66, ["num 7"] = 0x67,
        ["num 8"] = 0x68, ["num 9"] = 0x69, ["num *"] = 0x6A, ["num +"] = 0x6B,
        ["num sep"] = 0x6C, ["num -"] = 0x6D, ["num ."] = 0x6E, ["num /"] = 0x6F,
    };

    private static uint NameToVk(string name)
    {
        if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
            return char.ToUpperInvariant(name[0]);
        if (name.StartsWith("f", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(1), out var fn) && fn >= 1 && fn <= 24)
            return (uint)(0x70 + fn - 1);
        // 兼容旧格式 "vk107"（旧版本无小键盘名称映射时保存的）
        if (name.StartsWith("vk", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(name.AsSpan(2), out var vk) && vk <= 0xFF)
            return vk;
        return NameVk.TryGetValue(name.ToLowerInvariant(), out var v) ? v : 0;
    }

    private static readonly string[] ModifierNames = { "ctrl", "shift", "alt", "win" };

    private static bool ModDown(uint vk) => NativeMethods.GetAsyncKeyState((int)vk) < 0;

    /// <summary>
    /// 某个修饰键当前是否按下。**Win 键必须同时认左(0x5B)与右(0x5C)** ——
    /// 只认左键时，"无修饰键的热键 a"会被"右Win+A"误触发（修饰键精确匹配形同虚设），
    /// 录制时按右Win+A 也会被存成 "a"（修饰键丢失），之后单按 a 就触发。
    /// isDown 作为参数注入，便于 --selftest 脱离真实键盘状态验证本逻辑。
    /// </summary>
    public static bool IsModifierDown(string m) => IsModifierDown(m, ModDown);

    /// <summary>
    /// 同上，但按键状态由外部注入 —— 便于 --selftest 脱离真实键盘验证。
    /// 右 Win(0x5C) 曾经漏判（只认 0x5B），这条重载就是那次修复的回归保护。
    /// </summary>
    public static bool IsModifierDown(string m, Func<uint, bool> keyDown) => m switch
    {
        "ctrl" => keyDown(0x11),
        "shift" => keyDown(0x10),
        "alt" => keyDown(0x12),
        "win" => keyDown(VkLWin) || keyDown(VkRWin),
        _ => false,
    };

    /// <summary>
    /// 修饰键是否与规格完全一致：要求的都按下，且没有多余修饰键按下。
    /// 缺这条判定时，把 Caps Lock 设为热键后按 Ctrl+CapsLock / Shift+CapsLock 也会误触发切换。
    /// </summary>
    public static bool ModifiersMatch(HotkeySpec spec, Func<string, bool> isDown)
    {
        foreach (var m in ModifierNames)
        {
            if (isDown(m) != spec.Modifiers.Contains(m)) return false;
        }
        return true;
    }

    public static HotkeySpec? ParseHotkey(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return null;
        if (hotkey.StartsWith("mouse.", StringComparison.OrdinalIgnoreCase))
        {
            var btn = hotkey.AsSpan(6).ToString().ToLowerInvariant();
            if (btn is "x1" or "x2")
                return new HotkeySpec { Type = HotkeySpec.TypeMouse, MouseButton = btn };
            return null;
        }

        var parts = hotkey.Split('+');
        int modEnd = parts.Length - 1;
        var main = parts[modEnd].Trim().ToLowerInvariant();
        // 兼容 "num +" / "num -" 这类键名本身含 '+' 的情况：Split 后末段为空，
        // 主键应取"前一段 + '+'"，例如 "num +" → ["num ",""] → main="num +"；"ctrl+num +" → main="num +"，修饰键=["ctrl"]
        // 注意：先拼接再整体 Trim，保留 "num +" 中空格（Trim 会把 "num " 的空格吃掉拼成 "num+"，查表失败）
        if (main.Length == 0 && modEnd > 0)
        {
            main = (parts[modEnd - 1] + "+").Trim().ToLowerInvariant();
            modEnd--;
        }
        var spec = new HotkeySpec { Type = HotkeySpec.TypeKeyboard, MainKey = main };
        for (int i = 0; i < modEnd; i++)
        {
            var m = parts[i].Trim().ToLowerInvariant();
            if (Array.IndexOf(ModifierNames, m) >= 0)
                spec.Modifiers.Add(m);
        }
        spec.MainVk = NameToVk(spec.MainKey); // 一次算好，钩子回调里只做整数比较
        return spec;
    }

    /// <summary>
    /// 两个热键字符串是否表示同一物理热键。
    /// 按解析后的规格比较：键盘比较主键 VK 码 + 修饰键集合（忽略大小写、顺序、
    /// 旧格式 "vk107" 与 "num +" 等同键异名），鼠标比较按钮。
    /// 防止 "caps lock" vs "Caps Lock"、"shift+ctrl+a" vs "ctrl+shift+a" 等漏判。
    /// </summary>
    public static bool SameHotkey(string a, string b)
    {
        // 空值/空串视为"未设置"，不算同一热键（调用方需先判空）
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var sa = ParseHotkey(a);
        var sb = ParseHotkey(b);
        if (sa == null || sb == null) return false;
        if (sa.Type != sb.Type) return false;
        if (sa.Type == HotkeySpec.TypeMouse)
            return sa.MouseButton == sb.MouseButton;
        if (NameToVk(sa.MainKey) != NameToVk(sb.MainKey)) return false;
        if (sa.Modifiers.Count != sb.Modifiers.Count) return false;
        foreach (var m in sa.Modifiers)
            if (!sb.Modifiers.Contains(m)) return false;
        return true;
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                uint vk = data.vkCode;
                if (_recording)
                {
                    Logger.Log($"录制按键: vk=0x{vk:X2}"); // 钩子回调内只入队，绝不同步写磁盘
                    HandleRecordingKey(vk);
                }
                else
                    HandleKeyboardMatch(vk);
            }
        }
        return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private void HandleKeyboardMatch(uint vk)
    {
        var name = VkToName(vk);
        if (Array.IndexOf(ModifierNames, name) >= 0) return; // 忽略修饰键
        if (name == "win") return;

        (HotkeySpec spec, Action cb)? hit = null;
        lock (_lock)
        {
            foreach (var (spec, cb) in _rules)
            {
                if (spec.Type != HotkeySpec.TypeKeyboard) continue;
                // 用预解析好的 VK 码比较（ParseHotkey 时算过一次），兼容任意名称格式
                var specVk = spec.MainVk;
                if (specVk == 0 || specVk != vk) continue;
                if (ModifiersMatch(spec, m => IsModifierDown(m))) { hit = (spec, cb); break; }
            }
        }
        if (hit != null)
        {
            Logger.Log($"热键匹配: {name}");
            Task.Run(hit.Value.cb); // 后台执行，绝不阻塞钩子
        }
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_XBUTTONDOWN)
            {
                // X1/X2 按钮信息在 MSLLHOOKSTRUCT.mouseData 的高 16 位（不在 wParam）
                var ms = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                int xbtn = (int)((ms.mouseData >> 16) & 0xFFFF);
                if (xbtn == 0) xbtn = (int)(ms.mouseData & 0xFFFF); // 兼容部分设备
                string btn = xbtn == NativeMethods.XBUTTON1 ? "x1" :
                             xbtn == NativeMethods.XBUTTON2 ? "x2" : "";
                if (btn.Length > 0)
                {
                    // 注意：钩子回调里绝不能做同步磁盘 I/O（超 LowLevelHooksTimeout 会被系统静默摘掉钩子），
                    // 因此这里不做任何逐次诊断日志
                    if (_recording)
                    {
                        var on = _onRecorded;
                        if (on != null)
                        {
                            _recording = false;
                            Task.Run(() => on($"mouse.{btn}"));
                        }
                        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                    }
                    HandleMouseMatch(btn);
                }
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void HandleMouseMatch(string btn)
    {
        (HotkeySpec spec, Action cb)? hit = null;
        lock (_lock)
        {
            foreach (var (spec, cb) in _rules)
            {
                if (spec.Type == HotkeySpec.TypeMouse && spec.MouseButton == btn)
                { hit = (spec, cb); break; }
            }
        }
        if (hit != null)
        {
            Logger.Log($"鼠标热键匹配: {btn}");
            Task.Run(hit.Value.cb);
        }
    }

    // ---------------- 录制 ----------------

    public void StartRecording(Action<string> onRecorded, Action onCancel)
    {
        Logger.WriteDiagnostic("[dbg] HotkeyManager.StartRecording: 进入录制");
        _recording = true;
        _onRecorded = onRecorded;
        _onCancel = onCancel;
    }

    public void CancelRecording()
    {
        Logger.WriteDiagnostic("[dbg] HotkeyManager.CancelRecording");
        _recording = false;
        _onRecorded = null;
        _onCancel = null;
    }

    private void HandleRecordingKey(uint vk)
    {
        var name = VkToName(vk);
        if (Array.IndexOf(ModifierNames, name) >= 0) return;
        if (name == "win") return;
        if (name == "esc")
        {
            _recording = false;
            var on = _onCancel;
            _onCancel = null;
            if (on != null) Task.Run(on);
            return;
        }

        // 组合当前按下的修饰键（走统一判定，左右 Win 都算）
        var mods = new List<string>();
        foreach (var m in ModifierNames)
        {
            if (IsModifierDown(m)) mods.Add(m);
        }

        var hotkey = mods.Count > 0 ? string.Join("+", mods) + "+" + name : name;
        _recording = false;
        var recorded = _onRecorded;
        _onRecorded = null;
        _onCancel = null; // 与 esc 分支对称：成功出口也必须清掉取消回调，
                          // 否则"按 ESC 取消 → 重新录制 → 再按 ESC"会用上一次遗留的 onCancel 取消掉新录制
        if (recorded != null) Task.Run(() => recorded(hotkey));
    }

    public void Dispose()
    {
        Stop();
        CancelRecording();
    }
}
