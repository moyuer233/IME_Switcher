import sys
import os
import json
import threading
import time
import ctypes
import winreg
import tkinter as tk
from tkinter import ttk, scrolledtext, messagebox
from PIL import Image, ImageDraw
import pystray
from pynput import keyboard, mouse
import win32api
import win32con
import win32event
import winerror

# ==================== 导入 py_win_keyboard_layout（若无则报错，让用户安装） ====================
import py_win_keyboard_layout

# ==================== 日志系统（修复无控制台时的 NoneType 错误） ====================
LOG_DIR = os.path.join(os.getenv('APPDATA'), 'IMESwitcher')
LOG_FILE = os.path.join(LOG_DIR, 'log.txt')
_log_buffer = []

def ensure_log_dir():
    if not os.path.exists(LOG_DIR):
        os.makedirs(LOG_DIR)

def log(msg):
    timestamp = time.strftime('%H:%M:%S')
    full_msg = f"[{timestamp}] {msg}"
    # 安全地输出到控制台（如果存在）
    if sys.stdout is not None:
        print(full_msg)
        sys.stdout.flush()
    # 写入日志文件（始终执行）
    try:
        ensure_log_dir()
        with open(LOG_FILE, 'a', encoding='utf-8') as f:
            f.write(full_msg + '\n')
    except:
        pass
    # 更新调试窗口（如果存在）
    if hasattr(sys.modules[__name__], 'debug_text') and sys.modules[__name__].debug_text is not None:
        try:
            sys.modules[__name__].debug_text.insert(tk.END, full_msg + '\n')
            sys.modules[__name__].debug_text.see(tk.END)
        except:
            pass

# 用于调试窗口的全局引用
debug_text = None

# ==================== 核心切换逻辑 ====================
user32 = ctypes.windll.user32
WM_INPUTLANGCHANGEREQUEST = 0x0050
KLF_ACTIVATE = 0x00000001
KLF_SETFORPROCESS = 0x00000100
LANG_EN = 0x0409
LANG_ZH = 0x0804
last_toggle_time = 0

def get_current_lang_id():
    hwnd = user32.GetForegroundWindow()
    thread_id = user32.GetWindowThreadProcessId(hwnd, None)
    hkl = user32.GetKeyboardLayout(thread_id)
    return hkl & 0xFFFF

def switch_to_lang_api(target_lang_id):
    """使用 py_win_keyboard_layout 切换，失败则降级到 CTypes"""
    log(f"API: 尝试切换到 0x{target_lang_id:04X}")
    # 方法1：py_win_keyboard_layout
    try:
        full_id = int(f"{target_lang_id:08x}{target_lang_id:08x}", 16)
        py_win_keyboard_layout.change_foreground_window_keyboard_layout(full_id)
        log("API(py_win_keyboard_layout): 切换成功")
        return True
    except Exception as e:
        log(f"API(py_win_keyboard_layout) 异常: {e}，降级到 CTypes 方案")
    # 方法2：CTypes
    try:
        hkl = user32.LoadKeyboardLayoutW(f"0x{target_lang_id:08x}", KLF_ACTIVATE)
        if not hkl:
            log("API(CTypes): LoadKeyboardLayoutW 失败")
            return False
        user32.ActivateKeyboardLayout(hkl, KLF_SETFORPROCESS)
        hwnd = user32.GetForegroundWindow()
        result = user32.PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)
        log(f"API(CTypes): PostMessageW 返回 {result}")
        user32.NotifyWinEvent(0x8000, hwnd, 0, 0)
        return True
    except Exception as e:
        log(f"API(CTypes) 异常: {e}")
        return False

def switch_to_lang_simulate(target_lang_id):
    """模拟 Win+Space（简单可靠）"""
    log("模拟: 发送 Win+Space")
    win32api.keybd_event(win32con.VK_LWIN, 0, 0, 0)
    time.sleep(0.05)
    win32api.keybd_event(win32con.VK_SPACE, 0, 0, 0)
    time.sleep(0.05)
    win32api.keybd_event(win32con.VK_SPACE, 0, win32con.KEYEVENTF_KEYUP, 0)
    time.sleep(0.05)
    win32api.keybd_event(win32con.VK_LWIN, 0, win32con.KEYEVENTF_KEYUP, 0)
    log("模拟: 发送完成")
    return True

def toggle_ime():
    global last_toggle_time
    now = time.time()
    if now - last_toggle_time < 0.3:
        return
    last_toggle_time = now
    current = get_current_lang_id()
    log(f"当前语言ID: 0x{current:04X}")
    if current == LANG_ZH:
        target = LANG_EN
        target_name = "英文"
    else:
        target = LANG_ZH
        target_name = "中文"
    log(f"切换到 {target_name}")
    if SWITCH_METHOD == 1:
        success = switch_to_lang_api(target)
    else:
        success = switch_to_lang_simulate(target)
    if success:
        log(f"切换{target_name}指令已执行")
        time.sleep(0.15)
        new_lang = get_current_lang_id()
        if new_lang == target:
            log(f"✅ 验证成功，当前 {target_name}")
        else:
            log(f"❌ 验证失败，当前仍为 0x{new_lang:04X}")
    else:
        log(f"切换{target_name}失败")

# ==================== 配置管理 ====================
CONFIG_DIR = os.path.join(os.getenv('APPDATA'), 'IMESwitcher')
CONFIG_FILE = os.path.join(CONFIG_DIR, 'config.json')
DEFAULT_HOTKEY = 'caps lock'
SWITCH_METHOD = 1  # 1=API, 2=模拟

def ensure_config_dir():
    if not os.path.exists(CONFIG_DIR):
        os.makedirs(CONFIG_DIR)

def load_config():
    ensure_config_dir()
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return data.get('hotkey', DEFAULT_HOTKEY), data.get('autostart', False), data.get('method', 1)
        except:
            pass
    return DEFAULT_HOTKEY, False, 1

def save_config(hotkey, autostart, method):
    ensure_config_dir()
    with open(CONFIG_FILE, 'w', encoding='utf-8') as f:
        json.dump({'hotkey': hotkey, 'autostart': autostart, 'method': method}, f, indent=2)

# ==================== 开机自启 ====================
def set_autostart(enabled):
    key_path = r"Software\Microsoft\Windows\CurrentVersion\Run"
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_SET_VALUE)
        if enabled:
            exe_path = sys.executable if getattr(sys, 'frozen', False) else f'"{sys.executable}" "{__file__}"'
            winreg.SetValueEx(key, "IMESwitcher", 0, winreg.REG_SZ, exe_path)
        else:
            try:
                winreg.DeleteValue(key, "IMESwitcher")
            except FileNotFoundError:
                pass
        winreg.CloseKey(key)
        return True
    except Exception as e:
        log(f"开机自启设置失败: {e}")
        return False

def is_autostart_enabled():
    key_path = r"Software\Microsoft\Windows\CurrentVersion\Run"
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_READ)
        value, _ = winreg.QueryValueEx(key, "IMESwitcher")
        winreg.CloseKey(key)
        return True
    except:
        return False

# ==================== 主应用 ====================
class App:
    def __init__(self):
        global SWITCH_METHOD
        saved_hotkey, saved_autostart, saved_method = load_config()
        self.hotkey_str = saved_hotkey
        self.autostart = saved_autostart
        SWITCH_METHOD = saved_method

        self.root = tk.Tk()
        self.root.title("输入法一键切换")
        self.root.geometry("500x340")
        self.root.minsize(420, 280)
        self.root.resizable(True, True)
        self.root.protocol("WM_DELETE_WINDOW", self.hide_window)

        self.hotkey_display_var = tk.StringVar(value=self.hotkey_str)
        self.autostart_var = tk.BooleanVar(value=self.autostart)
        self.method_var = tk.IntVar(value=SWITCH_METHOD)
        self.listening = False
        self.listeners = []
        self.tray_icon = None
        self.recording = False
        self.record_listeners = []
        self.was_listening = False

        self.build_ui()
        self.setup_tray()
        if self.autostart:
            self.start_listening()

        log("程序启动完成")
        log(f"热键: {self.hotkey_str}, 方法: {'API' if SWITCH_METHOD==1 else '模拟Win+Space'}")

    def build_ui(self):
        main_frame = ttk.Frame(self.root, padding=15)
        main_frame.pack(fill=tk.BOTH, expand=True)

        main_frame.columnconfigure(0, weight=0)
        main_frame.columnconfigure(1, weight=1)
        main_frame.columnconfigure(2, weight=0)
        main_frame.columnconfigure(3, weight=0)
        main_frame.rowconfigure(5, weight=1)

        ttk.Label(main_frame, text="切换热键：").grid(row=0, column=0, sticky='w', pady=5)

        self.hotkey_label = tk.Label(
            main_frame,
            textvariable=self.hotkey_display_var,
            relief='solid',
            bg='white',
            padx=5,
            pady=2,
            anchor='w',
            width=20
        )
        self.hotkey_label.grid(row=0, column=1, sticky='ew', pady=5, padx=5)
        self.hotkey_label.bind("<Button-1>", self.start_recording)

        self.change_btn = ttk.Button(main_frame, text="更改", command=self.start_recording)
        self.change_btn.grid(row=0, column=2, sticky='w', pady=5, padx=(0,5))

        self.cancel_btn = ttk.Button(main_frame, text="取消", command=self._cancel_recording)
        self.cancel_btn.grid(row=0, column=3, sticky='w', pady=5, padx=(0,5))
        self.cancel_btn.grid_remove()

        ttk.Label(main_frame, text="切换方式：").grid(row=1, column=0, sticky='w', pady=5)
        method_frame = ttk.Frame(main_frame)
        method_frame.grid(row=1, column=1, columnspan=3, sticky='w', pady=5)
        ttk.Radiobutton(method_frame, text="API", variable=self.method_var, value=1,
                       command=self.on_method_change).pack(side=tk.LEFT, padx=5)
        ttk.Radiobutton(method_frame, text="模拟", variable=self.method_var, value=2,
                       command=self.on_method_change).pack(side=tk.LEFT, padx=5)

        self.autostart_check = ttk.Checkbutton(
            main_frame,
            text="开机自动启动",
            variable=self.autostart_var,
            command=self.on_autostart_toggle
        )
        self.autostart_check.grid(row=2, column=0, columnspan=4, sticky='w', pady=8)

        self.status_label = ttk.Label(main_frame, text="状态：未启动", foreground='gray')
        self.status_label.grid(row=3, column=0, columnspan=4, sticky='w', pady=8)

        btn_frame = ttk.Frame(main_frame)
        btn_frame.grid(row=4, column=0, columnspan=4, pady=10, sticky='ew')
        btn_frame.columnconfigure(0, weight=1)
        btn_frame.columnconfigure(1, weight=1)
        btn_frame.columnconfigure(2, weight=1)
        btn_frame.columnconfigure(3, weight=1)

        self.start_btn = ttk.Button(btn_frame, text="启动", command=self.start_listening)
        self.start_btn.grid(row=0, column=0, padx=5, sticky='ew')

        self.stop_btn = ttk.Button(btn_frame, text="停止", command=self.stop_listening, state=tk.DISABLED)
        self.stop_btn.grid(row=0, column=1, padx=5, sticky='ew')

        ttk.Button(btn_frame, text="手动切换", command=self.manual_test).grid(row=0, column=2, padx=5, sticky='ew')

        debug_btn = ttk.Button(btn_frame, text="显示调试", command=self.show_debug_window)
        debug_btn.grid(row=0, column=3, padx=5, sticky='ew')

    def manual_test(self):
        log(">>> 手动切换测试 <<<")
        toggle_ime()

    def on_method_change(self):
        global SWITCH_METHOD
        SWITCH_METHOD = self.method_var.get()
        save_config(self.hotkey_str, self.autostart_var.get(), SWITCH_METHOD)
        log(f"切换方式改为: {'API' if SWITCH_METHOD==1 else '模拟'}")

    def on_autostart_toggle(self):
        enabled = self.autostart_var.get()
        success = set_autostart(enabled)
        if success:
            save_config(self.hotkey_str, enabled, SWITCH_METHOD)
            messagebox.showinfo("提示", f"开机自启{'已启用' if enabled else '已禁用'}")
            log(f"开机自启设置为: {enabled}")
        else:
            messagebox.showerror("错误", "设置开机自启失败，请检查权限")
            self.autostart_var.set(not enabled)

    # ==================== 热键录制 ====================
    def start_recording(self, event=None):
        if self.recording:
            self._force_stop_recording()
        self.was_listening = self.listening
        if self.listening:
            self.stop_listening()

        self.recording = True
        self.hotkey_display_var.set("按下热键...")
        self.hotkey_label.config(bg='lightyellow')
        self.change_btn.config(state=tk.DISABLED)
        self.cancel_btn.grid()
        log("开始录制热键")

        try:
            self.keyboard_listener = keyboard.Listener(on_press=self._on_record_press)
            self.mouse_listener = mouse.Listener(on_click=self._on_record_click)
            self.keyboard_listener.start()
            self.mouse_listener.start()
            self.record_listeners = [self.keyboard_listener, self.mouse_listener]
        except Exception as e:
            self._force_stop_recording()
            log(f"录制启动失败: {e}")
            messagebox.showerror("错误", f"启动录制失败：{e}")

    def _on_record_press(self, key):
        if not self.recording:
            return
        try:
            if hasattr(key, 'name'):
                key_name = key.name
            elif hasattr(key, 'char') and key.char is not None:
                key_name = key.char
            else:
                key_name = str(key).replace('Key.', '')
            if key_name in ['ctrl', 'shift', 'alt', 'cmd', 'ctrl_l', 'ctrl_r', 'shift_l', 'shift_r', 'alt_l', 'alt_r', 'cmd_l', 'cmd_r']:
                return
            if key == keyboard.Key.esc:
                log("录制取消 (ESC)")
                self.root.after(0, self._cancel_recording)
                return
            mods = []
            if win32api.GetKeyState(win32con.VK_CONTROL) & 0x8000:
                mods.append('ctrl')
            if win32api.GetKeyState(win32con.VK_SHIFT) & 0x8000:
                mods.append('shift')
            if win32api.GetKeyState(win32con.VK_MENU) & 0x8000:
                mods.append('alt')
            if win32api.GetKeyState(win32con.VK_LWIN) & 0x8000 or win32api.GetKeyState(win32con.VK_RWIN) & 0x8000:
                mods.append('win')
            new_hotkey = '+'.join(mods + [key_name]) if mods else key_name
            log(f"录制到热键: {new_hotkey}")
            self.root.after(0, self._finish_recording, new_hotkey)
        except Exception as e:
            log(f"录制异常: {e}")
            self.root.after(0, self._cancel_recording)

    def _on_record_click(self, x, y, button, pressed):
        if not self.recording or not pressed:
            return
        if button == mouse.Button.x1:
            self.root.after(0, self._finish_recording, 'mouse.x1')
        elif button == mouse.Button.x2:
            self.root.after(0, self._finish_recording, 'mouse.x2')

    def _finish_recording(self, new_hotkey):
        if not self.recording:
            return
        self._force_stop_recording()
        self.hotkey_str = new_hotkey
        self.hotkey_display_var.set(new_hotkey)
        self.hotkey_label.config(bg='white')
        self.change_btn.config(state=tk.NORMAL)
        self.cancel_btn.grid_remove()
        save_config(self.hotkey_str, self.autostart_var.get(), SWITCH_METHOD)
        log(f"热键已保存: {self.hotkey_str}")
        if self.was_listening:
            self.start_listening()

    def _cancel_recording(self):
        if not self.recording:
            return
        self._force_stop_recording()
        self.hotkey_display_var.set(self.hotkey_str)
        self.hotkey_label.config(bg='white')
        self.change_btn.config(state=tk.NORMAL)
        self.cancel_btn.grid_remove()
        if self.was_listening:
            self.start_listening()

    def _force_stop_recording(self):
        self.recording = False
        for lis in getattr(self, 'record_listeners', []):
            try:
                lis.stop()
            except:
                pass
        self.record_listeners = []
        if hasattr(self, 'hotkey_label'):
            self.hotkey_label.config(bg='white')
        if hasattr(self, 'change_btn'):
            self.change_btn.config(state=tk.NORMAL)
        if hasattr(self, 'cancel_btn'):
            self.cancel_btn.grid_remove()

    # ==================== 监听控制 ====================
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

    def parse_hotkey(self, hotkey_str):
        if hotkey_str.startswith('mouse.'):
            btn = hotkey_str.split('.')[1]
            if btn == 'x1':
                return {'type': 'mouse', 'button': mouse.Button.x1}
            elif btn == 'x2':
                return {'type': 'mouse', 'button': mouse.Button.x2}
            else:
                return None
        parts = hotkey_str.split('+')
        main_key = parts[-1].strip().lower()
        modifiers = [m.strip().lower() for m in parts[:-1]]
        mod_keys = []
        for m in modifiers:
            if m == 'ctrl':
                mod_keys.append(keyboard.Key.ctrl)
            elif m == 'shift':
                mod_keys.append(keyboard.Key.shift)
            elif m == 'alt':
                mod_keys.append(keyboard.Key.alt)
            elif m == 'win':
                mod_keys.append(keyboard.Key.cmd)
            else:
                log(f"未知修饰键: {m}")
                return None
        main_key_obj = self.KEY_MAP.get(main_key, main_key)
        return {'type': 'keyboard', 'modifiers': mod_keys, 'main_key': main_key_obj}

    def start_listening(self):
        if self.listening:
            return
        self.hotkey_parsed = self.parse_hotkey(self.hotkey_str)
        if not self.hotkey_parsed:
            messagebox.showerror("错误", f"无效热键: {self.hotkey_str}")
            log(f"热键解析失败: {self.hotkey_str}")
            return
        self.listening = True
        self.status_label.config(text=f"状态：监听中 ({self.hotkey_str})", foreground='green')
        self.start_btn.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)
        log(f"监听启动，热键: {self.hotkey_str}")
        try:
            self.keyboard_listener = keyboard.Listener(on_press=self._on_key_press)
            self.mouse_listener = mouse.Listener(on_click=self._on_mouse_click)
            self.keyboard_listener.start()
            self.mouse_listener.start()
            self.listeners = [self.keyboard_listener, self.mouse_listener]
            log("监听器已启动，等待热键...")
        except Exception as e:
            self.listening = False
            log(f"启动监听失败: {e}")
            messagebox.showerror("错误", f"启动监听失败: {e}")

    def stop_listening(self):
        if not self.listening:
            return
        self.listening = False
        for lis in getattr(self, 'listeners', []):
            try:
                lis.stop()
            except:
                pass
        self.listeners = []
        self.status_label.config(text="状态：已停止", foreground='gray')
        self.start_btn.config(state=tk.NORMAL)
        self.stop_btn.config(state=tk.DISABLED)
        log("监听已停止")

    def _on_key_press(self, key):
        if not self.listening or self.hotkey_parsed['type'] != 'keyboard':
            return
        try:
            key_str = key.name if hasattr(key, 'name') else (key.char if key.char else str(key))
        except:
            key_str = str(key)
        if key_str in ['ctrl', 'shift', 'alt', 'cmd', 'ctrl_l', 'ctrl_r', 'shift_l', 'shift_r']:
            return
        mods_ok = all(self._is_key_pressed(mod) for mod in self.hotkey_parsed['modifiers'])
        if not mods_ok:
            return
        main_key = self.hotkey_parsed['main_key']
        matched = False
        if isinstance(main_key, keyboard.Key):
            if key == main_key:
                matched = True
        else:
            try:
                if key.char and key.char.lower() == main_key.lower():
                    matched = True
            except:
                pass
        if matched:
            log(f"热键匹配: {key_str}")
            self.root.after(0, toggle_ime)

    def _on_mouse_click(self, x, y, button, pressed):
        if not self.listening or not pressed or self.hotkey_parsed['type'] != 'mouse':
            return
        if button == self.hotkey_parsed['button']:
            log(f"鼠标热键匹配: {button}")
            self.root.after(0, toggle_ime)

    def _is_key_pressed(self, key_obj):
        vk_map = {
            keyboard.Key.ctrl: win32con.VK_CONTROL,
            keyboard.Key.shift: win32con.VK_SHIFT,
            keyboard.Key.alt: win32con.VK_MENU,
            keyboard.Key.cmd: win32con.VK_LWIN,
        }
        if key_obj in vk_map:
            return win32api.GetKeyState(vk_map[key_obj]) & 0x8000
        return False

    # ==================== 调试窗口 ====================
    def show_debug_window(self):
        if hasattr(self, 'debug_win') and self.debug_win.winfo_exists():
            self.debug_win.lift()
            return
        self.debug_win = tk.Toplevel(self.root)
        self.debug_win.title("调试日志")
        self.debug_win.geometry("600x400")
        frame = ttk.Frame(self.debug_win, padding=10)
        frame.pack(fill=tk.BOTH, expand=True)
        self.debug_text = scrolledtext.ScrolledText(frame, wrap=tk.WORD, font=("Consolas", 9))
        self.debug_text.pack(fill=tk.BOTH, expand=True)
        global debug_text
        debug_text = self.debug_text  # 供 log 函数使用
        for msg in _log_buffer:
            self.debug_text.insert(tk.END, msg + '\n')
        self.debug_text.see(tk.END)

        def on_close():
            global debug_text
            debug_text = None
            self.debug_win.destroy()
        self.debug_win.protocol("WM_DELETE_WINDOW", on_close)

    # ==================== 托盘等 ====================
    def setup_tray(self):
        image = Image.new('RGB', (64, 64), color='white')
        draw = ImageDraw.Draw(image)
        draw.text((20, 10), "I", fill='black')
        self.tray_icon = pystray.Icon("ime_switcher", image, "输入法切换",
                                      menu=pystray.Menu(
                                          pystray.MenuItem("显示设置", self.show_window),
                                          pystray.MenuItem("启动", self.start_listening),
                                          pystray.MenuItem("停止", self.stop_listening),
                                          pystray.MenuItem("退出", self.quit_app)
                                      ))
        threading.Thread(target=self.tray_icon.run, daemon=True).start()

    def show_window(self, icon=None, item=None):
        self.root.deiconify()
        self.root.lift()

    def hide_window(self):
        self.root.withdraw()

    def quit_app(self, icon=None, item=None):
        self.stop_listening()
        if self.tray_icon:
            self.tray_icon.stop()
        self.root.quit()
        self.root.destroy()
        os._exit(0)

    def run(self):
        self.root.mainloop()

# ==================== 单实例 ====================
def check_single_instance():
    mutex_name = "Global\\IMESwitcher_SingleInstance"
    try:
        mutex = win32event.CreateMutex(None, False, mutex_name)
        if win32api.GetLastError() == winerror.ERROR_ALREADY_EXISTS:
            messagebox.showerror("错误", "程序已在运行中！")
            return False
        return True
    except:
        return True

# ==================== 入口 ====================
if __name__ == "__main__":
    try:
        is_admin = ctypes.windll.shell32.IsUserAnAdmin()
    except:
        is_admin = False
    if not is_admin:
        reply = messagebox.askyesno("权限不足", "需要管理员权限。\n是否以管理员身份重新启动？")
        if reply:
            ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, " ".join(sys.argv), None, 1)
        sys.exit(0)

    try:
        if not check_single_instance():
            sys.exit(0)
    except:
        pass

    app = App()
    app.run()