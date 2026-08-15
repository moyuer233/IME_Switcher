# -*- coding: utf-8 -*-
"""
热键处理
负责热键字符串的解析、热键录制监听以及热键匹配触发
"""
import win32api
import win32con
from pynput import keyboard, mouse

from logger import log

# 按键名称映射表（pynput 的 Key 对象名称与字符串的对应关系）
KEY_MAP = {
    'caps lock': keyboard.Key.caps_lock,
    'space': keyboard.Key.space,
    'enter': keyboard.Key.enter,
    'tab': keyboard.Key.tab,
    'backspace': keyboard.Key.backspace,
    'delete': keyboard.Key.delete,
    'insert': keyboard.Key.insert,
    'home': keyboard.Key.home,
    'end': keyboard.Key.end,
    'page up': keyboard.Key.page_up,
    'page down': keyboard.Key.page_down,
    'up': keyboard.Key.up,
    'down': keyboard.Key.down,
    'left': keyboard.Key.left,
    'right': keyboard.Key.right,
    'f1': keyboard.Key.f1, 'f2': keyboard.Key.f2, 'f3': keyboard.Key.f3,
    'f4': keyboard.Key.f4, 'f5': keyboard.Key.f5, 'f6': keyboard.Key.f6,
    'f7': keyboard.Key.f7, 'f8': keyboard.Key.f8, 'f9': keyboard.Key.f9,
    'f10': keyboard.Key.f10, 'f11': keyboard.Key.f11, 'f12': keyboard.Key.f12,
    'esc': keyboard.Key.esc,
    'print screen': keyboard.Key.print_screen,
    'scroll lock': keyboard.Key.scroll_lock,
    'pause': keyboard.Key.pause,
    'menu': keyboard.Key.menu,
    'num lock': keyboard.Key.num_lock,
}

# 单独的修饰键（录制与监听时忽略）
MODIFIER_KEYS = [
    'ctrl', 'shift', 'alt', 'cmd',
    'ctrl_l', 'ctrl_r', 'shift_l', 'shift_r',
    'alt_l', 'alt_r', 'cmd_l', 'cmd_r',
]

# 修饰键字符串 -> pynput Key 对象
MODIFIER_MAP = {
    'ctrl': keyboard.Key.ctrl,
    'shift': keyboard.Key.shift,
    'alt': keyboard.Key.alt,
    'win': keyboard.Key.cmd,
}


def parse_hotkey(hotkey_str):
    """
    解析热键字符串，返回包含类型、修饰键和主键的字典
    支持格式：
    - "caps lock" → 单键
    - "ctrl+shift+a" → 组合键
    - "mouse.x1" → 鼠标侧键
    """
    # 鼠标侧键
    if hotkey_str.startswith('mouse.'):
        btn = hotkey_str.split('.')[1]
        if btn == 'x1':
            return {'type': 'mouse', 'button': mouse.Button.x1}
        elif btn == 'x2':
            return {'type': 'mouse', 'button': mouse.Button.x2}
        else:
            return None

    # 键盘按键
    parts = hotkey_str.split('+')
    main_key = parts[-1].strip().lower()
    modifiers = [m.strip().lower() for m in parts[:-1]]

    # 转换修饰键字符串为 pynput Key 对象
    mod_keys = []
    for m in modifiers:
        if m in MODIFIER_MAP:
            mod_keys.append(MODIFIER_MAP[m])
        else:
            log(f"未知修饰键: {m}")
            return None

    # 转换主键
    main_key_obj = KEY_MAP.get(main_key, main_key)
    return {'type': 'keyboard', 'modifiers': mod_keys, 'main_key': main_key_obj}


def key_to_name(key):
    """将 pynput 的 Key 对象转换为名称字符串"""
    if hasattr(key, 'name'):
        return key.name
    elif hasattr(key, 'char') and key.char is not None:
        return key.char
    else:
        return str(key).replace('Key.', '')


class HotkeyRecorder:
    """
    热键录制器
    监听键盘和鼠标事件，识别用户按下的按键组合，通过回调返回结果
    """

    def __init__(self, on_recorded, on_cancel, schedule):
        """
        :param on_recorded: 录制成功回调，参数为热键字符串
        :param on_cancel: 取消录制回调
        :param schedule: 主线程调度函数，用于线程安全地触发回调
        """
        self.on_recorded = on_recorded
        self.on_cancel = on_cancel
        self.schedule = schedule
        self.recording = False
        self.listeners = []

    def start(self):
        """启动录制监听器"""
        self.recording = True
        try:
            self.keyboard_listener = keyboard.Listener(on_press=self._on_press)
            self.mouse_listener = mouse.Listener(on_click=self._on_click)
            self.keyboard_listener.start()
            self.mouse_listener.start()
            self.listeners = [self.keyboard_listener, self.mouse_listener]
            return True
        except Exception as e:
            self.stop()
            log(f"录制启动失败: {e}")
            return False

    def stop(self):
        """停止录制并清理监听器"""
        self.recording = False
        for lis in getattr(self, 'listeners', []):
            try:
                lis.stop()
            except Exception:
                pass
        self.listeners = []

    def _on_press(self, key):
        """录制时的键盘事件回调"""
        if not self.recording:
            return
        key_name = key_to_name(key)

        # 忽略单独的修饰键
        if key_name in MODIFIER_KEYS:
            return

        # ESC 取消录制
        if key == keyboard.Key.esc:
            log("录制取消 (ESC)")
            self.schedule(self.on_cancel)
            return

        # 检测当前按下的修饰键
        mods = []
        if win32api.GetKeyState(win32con.VK_CONTROL) & 0x8000:
            mods.append('ctrl')
        if win32api.GetKeyState(win32con.VK_SHIFT) & 0x8000:
            mods.append('shift')
        if win32api.GetKeyState(win32con.VK_MENU) & 0x8000:
            mods.append('alt')
        if (win32api.GetKeyState(win32con.VK_LWIN) & 0x8000
                or win32api.GetKeyState(win32con.VK_RWIN) & 0x8000):
            mods.append('win')

        # 生成热键字符串（如 "ctrl+shift+a"）
        new_hotkey = '+'.join(mods + [key_name]) if mods else key_name
        log(f"录制到热键: {new_hotkey}")
        self.schedule(self.on_recorded, new_hotkey)

    def _on_click(self, x, y, button, pressed):
        """录制时的鼠标事件回调（识别鼠标侧键 X1/X2）"""
        if not self.recording or not pressed:
            return
        if button == mouse.Button.x1:
            self.schedule(self.on_recorded, 'mouse.x1')
        elif button == mouse.Button.x2:
            self.schedule(self.on_recorded, 'mouse.x2')


class HotkeyListener:
    """
    后台热键监听器
    在后台监听键盘和鼠标事件，匹配多个热键规则，命中时触发对应回调
    """

    def __init__(self, rules, schedule):
        """
        :param rules: 热键规则列表，每项为 (hotkey_parsed, callback) 元组
        :param schedule: 主线程调度函数
        """
        self.rules = rules
        self.schedule = schedule
        self.listening = False
        self.listeners = []

    def start(self):
        """启动监听器"""
        self.listening = True
        try:
            self.keyboard_listener = keyboard.Listener(on_press=self._on_key_press)
            self.mouse_listener = mouse.Listener(on_click=self._on_mouse_click)
            self.keyboard_listener.start()
            self.mouse_listener.start()
            self.listeners = [self.keyboard_listener, self.mouse_listener]
            return True
        except Exception as e:
            self.listening = False
            log(f"启动监听失败: {e}")
            return False

    def stop(self):
        """停止监听器"""
        self.listening = False
        for lis in getattr(self, 'listeners', []):
            try:
                lis.stop()
            except Exception:
                pass
        self.listeners = []

    def _on_key_press(self, key):
        """监听时的键盘事件回调"""
        if not self.listening:
            return

        key_str = key_to_name(key)

        # 忽略单独的修饰键
        if key_str in MODIFIER_KEYS:
            return

        # 遍历所有键盘规则，命中任一即触发对应回调
        for parsed, callback in self.rules:
            if parsed['type'] != 'keyboard':
                continue

            # 检查所有修饰键是否都按下
            matched = True
            for mod in parsed['modifiers']:
                if not self._is_key_pressed(mod):
                    matched = False
                    break
            if not matched:
                continue

            # 检查主键是否匹配
            main_key = parsed['main_key']
            if isinstance(main_key, keyboard.Key):
                if key != main_key:
                    continue
            else:
                try:
                    if not (key.char and key.char.lower() == main_key.lower()):
                        continue
                except Exception:
                    continue

            log(f"热键匹配: {key_str}")
            # 使用 schedule 在主线程中触发回调，避免线程冲突
            self.schedule(callback)
            return

    def _on_mouse_click(self, x, y, button, pressed):
        """监听时的鼠标事件回调"""
        if not self.listening or not pressed:
            return

        # 遍历所有鼠标规则，命中任一即触发对应回调
        for parsed, callback in self.rules:
            if parsed['type'] == 'mouse' and button == parsed['button']:
                log(f"鼠标热键匹配: {button}")
                # 延迟 100ms 触发切换，避免与鼠标事件冲突
                self.schedule(callback, delay=100)
                return

    def _is_key_pressed(self, key_obj):
        """检查指定的键盘键是否当前处于按下状态"""
        vk_map = {
            keyboard.Key.ctrl: win32con.VK_CONTROL,
            keyboard.Key.shift: win32con.VK_SHIFT,
            keyboard.Key.alt: win32con.VK_MENU,
            keyboard.Key.cmd: win32con.VK_LWIN,
        }
        if key_obj in vk_map:
            return win32api.GetKeyState(vk_map[key_obj]) & 0x8000
        return False
