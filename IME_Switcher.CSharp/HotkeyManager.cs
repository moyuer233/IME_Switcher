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

    public void Start()
    {
        if (_running) return;
        _running = true;
        Logger.WriteDiagnostic("[dbg] HotkeyManager.Start: 启动钩子线程");
        _hookThread = new Thread(HookThreadMain) { IsBackground = true, Name = "hook-thread" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_hookThread != null && _hookThread.IsAlive)
        {
            _hookThread.Join(1000);
        }
        _hookThread = null;
    }

    private void HookThreadMain()
    {
        _keyProc = KeyboardProc;
        _mouseProc = MouseProc;
        _keyHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _keyProc, NativeMethods.GetModuleHandle(null), 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
        Logger.WriteDiagnostic($"[dbg] 钩子安装: 键盘=0x{_keyHook.ToInt64():X}, 鼠标=0x{_mouseHook.ToInt64():X}");

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

    private static readonly Dictionary<uint, string> VkName = new()
    {
        [0x08] = "backspace", [0x09] = "tab", [0x0D] = "enter", [0x13] = "pause",
        [0x14] = "caps lock", [0x1B] = "esc", [0x20] = "space", [0x21] = "page up",
        [0x22] = "page down", [0x23] = "end", [0x24] = "home", [0x25] = "left",
        [0x26] = "up", [0x27] = "right", [0x28] = "down", [0x2C] = "print screen",
        [0x2D] = "insert", [0x2E] = "delete", [0x5B] = "win", [0x5D] = "menu",
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
        ["insert"] = 0x2D, ["delete"] = 0x2E, ["win"] = 0x5B, ["menu"] = 0x5D,
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

    private static uint ModVk(string m) => m switch
    {
        "ctrl" => 0x11, "shift" => 0x10, "alt" => 0x12, "win" => 0x5B, _ => 0,
    };

    private static bool ModDown(uint vk) => NativeMethods.GetAsyncKeyState((int)vk) < 0;

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
        var main = parts[^1].Trim().ToLowerInvariant();
        var spec = new HotkeySpec { Type = HotkeySpec.TypeKeyboard, MainKey = main };
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var m = parts[i].Trim().ToLowerInvariant();
            if (Array.IndexOf(ModifierNames, m) >= 0)
                spec.Modifiers.Add(m);
        }
        return spec;
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
                    Logger.WriteDiagnostic($"[dbg] 键盘钩子捕获: vk=0x{vk:X2}, 进入录制");
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
                // 用 VK 码比较，兼容任意名称格式（"num +"、"vk107"、"enter" 等）
                var specVk = NameToVk(spec.MainKey);
                if (specVk == 0 || specVk != vk) continue;
                bool ok = true;
                foreach (var m in spec.Modifiers)
                {
                    if (!ModDown(ModVk(m))) { ok = false; break; }
                }
                if (ok) { hit = (spec, cb); break; }
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
                Logger.WriteDiagnostic($"[dbg] 鼠标钩子收到 XBUTTONDOWN: mouseData=0x{ms.mouseData:X}, xbtn=0x{xbtn:X}, recording={_recording}");
                string btn = xbtn == NativeMethods.XBUTTON1 ? "x1" :
                             xbtn == NativeMethods.XBUTTON2 ? "x2" : "";
                if (btn.Length > 0)
                {
                    Logger.WriteDiagnostic($"[dbg] 鼠标钩子捕获 XBUTTON: btn={btn}, recording={_recording}");
                    if (_recording)
                    {
                        var on = _onRecorded;
                        if (on != null)
                        {
                            _recording = false;
                            Logger.WriteDiagnostic($"[dbg] 鼠标录制完成，触发回调: mouse.{btn}");
                            Task.Run(() => on($"mouse.{btn}"));
                        }
                        else
                        {
                            Logger.WriteDiagnostic("[dbg] 鼠标录制: _onRecorded 为空，未触发");
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
        Logger.WriteDiagnostic($"[dbg] 录制按键: {name}");
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

        // 组合当前按下的修饰键
        var mods = new List<string>();
        if (ModDown(0x11)) mods.Add("ctrl");
        if (ModDown(0x10)) mods.Add("shift");
        if (ModDown(0x12)) mods.Add("alt");
        if (ModDown(0x5B)) mods.Add("win");

        var hotkey = mods.Count > 0 ? string.Join("+", mods) + "+" + name : name;
        _recording = false;
        var recorded = _onRecorded;
        _onRecorded = null;
        if (recorded != null) Task.Run(() => recorded(hotkey));
    }

    public void Dispose()
    {
        Stop();
        CancelRecording();
    }
}
