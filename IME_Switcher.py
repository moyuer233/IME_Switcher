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
import win32gui
import py_win_keyboard_layout

print(r"""
#==============================================================================#
                                                   ___       __       __
 /'\_/`\                                         /'___`\   /'__`\   /'__`\ 
/\      \    ___   __  __  __  __     __   _ __ /\_\ /\ \ /\_\L\ \ /\_\L\ \
\ \ \__\ \  / __`\/\ \/\ \/\ \/\ \  /'__`\/\`'__\/_/// /__\/_/_\_<_\/_/_\_<_ 
 \ \ \_/\ \/\ \L\ \ \ \_\ \ \ \_\ \/\  __/\ \ \/   // /_\ \ /\ \L\ \ /\ \L\ \ 
  \ \_\\ \_\ \____/\/`____ \ \____/\ \____\\ \_\  /\______/ \ \____/ \ \____/ 
   \/_/ \/_/\/___/  `/___/> \/___/  \/____/ \/_/  \/_____/   \/___/   \/___/ 
                       /\___/ 
                       \/__/ 

   ██████╗ ███████╗███████╗██████╗ ███████╗███████╗███████╗██╗  ██╗
   ██╔══██╗██╔════╝██╔════╝██╔══██╗██╔════╝██╔════╝██╔════╝██║ ██╔╝
   ██║  ██║█████╗  █████╗  ██████╔╝███████╗█████╗  █████╗  █████╔╝
   ██║  ██║██╔══╝  ██╔══╝  ██╔═══╝ ╚════██║██╔══╝  ██╔══╝  ██╔═██╗
   ██████╔╝███████╗███████╗██║     ███████║███████╗███████╗██║  ██╗
   ╚═════╝ ╚══════╝╚══════╝╚═╝     ╚══════╝╚══════╝╚══════╝╚═╝  ╚═╝
#===============================================================================#
""")

# =============================================================================
# 日志系统
# 负责将程序运行日志输出到控制台、文件（%APPDATA%\IMESwitcher\log.txt）
# 以及调试窗口（如果已打开）。日志文件便于事后排查问题，调试窗口提供实时查看。
# =============================================================================

# 日志目录和文件路径
LOG_DIR = os.path.join(os.getenv('APPDATA'), 'IMESwitcher')
LOG_FILE = os.path.join(LOG_DIR, 'log.txt')
_log_buffer = []  # 内存日志缓冲区，用于调试窗口显示


def ensure_log_dir():
    """确保日志目录存在，如果不存在则创建"""
    if not os.path.exists(LOG_DIR):
        os.makedirs(LOG_DIR)


def log(msg):
    """
    写入日志消息
    1. 添加时间戳
    2. 输出到控制台（如果有）
    3. 写入日志文件
    4. 如果调试窗口打开，追加到调试窗口
    """
    timestamp = time.strftime('%H:%M:%S')
    full_msg = f"[{timestamp}] {msg}"

    # 输出到控制台（仅当控制台存在时，避免打包后报错）
    if sys.stdout is not None:
        print(full_msg)
        sys.stdout.flush()

    # 写入日志文件
    try:
        ensure_log_dir()
        with open(LOG_FILE, 'a', encoding='utf-8') as f:
            f.write(full_msg + '\n')
    except:
        pass

    # 更新调试窗口（如果已打开）
    if hasattr(sys.modules[__name__], 'debug_text') and sys.modules[__name__].debug_text is not None:
        try:
            sys.modules[__name__].debug_text.insert(tk.END, full_msg + '\n')
            sys.modules[__name__].debug_text.see(tk.END)
        except:
            pass


# 全局变量，用于调试窗口的文本控件引用
debug_text = None

# =============================================================================
# 核心切换逻辑
# 负责获取当前语言 ID，以及通过 API 或模拟方式切换输入法
# =============================================================================

# Windows API 相关常量
user32 = ctypes.windll.user32
WM_INPUTLANGCHANGEREQUEST = 0x0050
KLF_ACTIVATE = 0x00000001
KLF_SETFORPROCESS = 0x00000100

# 语言 ID 常量
LANG_EN = 0x0409  # 美式英语
LANG_ZH = 0x0804  # 简体中文

# 防抖时间，防止短时间内重复触发切换
last_toggle_time = 0


def get_current_lang_id():
    """
    获取当前活动窗口的输入法语言 ID
    通过 Windows API 获取前台窗口的键盘布局，然后提取低 16 位作为语言 ID
    返回值为 0x0409（英文）或 0x0804（中文）等
    """
    hwnd = user32.GetForegroundWindow()
    thread_id = user32.GetWindowThreadProcessId(hwnd, None)
    hkl = user32.GetKeyboardLayout(thread_id)
    return hkl & 0xFFFF


def switch_to_lang_api(target_lang_id):
    """
    使用 API 方式切换输入法（优先方案）
    1. 首先尝试 py_win_keyboard_layout 库（第三方库，更可靠）
    2. 如果失败，降级到 CTypes 直接调用 Windows API
    """
    log(f"API: 尝试切换到 0x{target_lang_id:04X}")

    # 方法1：使用 py_win_keyboard_layout 库
    try:
        full_id = int(f"{target_lang_id:08x}{target_lang_id:08x}", 16)
        py_win_keyboard_layout.change_foreground_window_keyboard_layout(full_id)
        log("API(py_win_keyboard_layout): 切换成功")
        return True
    except Exception as e:
        log(f"API(py_win_keyboard_layout) 异常: {e}，降级到 CTypes 方案")

    # 方法2：使用 CTypes 调用 Windows API（降级方案）
    try:
        # 加载键盘布局
        hkl = user32.LoadKeyboardLayoutW(f"0x{target_lang_id:08x}", KLF_ACTIVATE)
        if not hkl:
            log("API(CTypes): LoadKeyboardLayoutW 失败")
            return False

        # 激活当前线程的键盘布局
        user32.ActivateKeyboardLayout(hkl, KLF_SETFORPROCESS)

        # 向前台窗口发送切换消息
        hwnd = user32.GetForegroundWindow()
        result = user32.PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)
        log(f"API(CTypes): PostMessageW 返回 {result}")

        # 触发系统事件，刷新窗口状态
        user32.NotifyWinEvent(0x8000, hwnd, 0, 0)
        return True
    except Exception as e:
        log(f"API(CTypes) 异常: {e}")
        return False


def switch_to_lang_simulate(target_lang_id):
    """
    使用模拟方式切换输入法（备选方案）
    通过 keybd_event 模拟按下 Win+Space 系统快捷键
    兼容性更好，适用于 API 方式无效的场景（如游戏、UWP 应用）
    """
    log("模拟: 发送 Win+Space")

    # 按下 Win 键
    win32api.keybd_event(win32con.VK_LWIN, 0, 0, 0)
    time.sleep(0.05)

    # 按下并释放 Space 键
    win32api.keybd_event(win32con.VK_SPACE, 0, 0, 0)
    time.sleep(0.05)
    win32api.keybd_event(win32con.VK_SPACE, 0, win32con.KEYEVENTF_KEYUP, 0)
    time.sleep(0.05)

    # 释放 Win 键
    win32api.keybd_event(win32con.VK_LWIN, 0, win32con.KEYEVENTF_KEYUP, 0)

    log("模拟: 发送完成")
    return True


def toggle_ime():
    """
    输入法切换入口函数
    1. 防抖检查（0.3 秒内不重复触发）
    2. 获取当前语言 ID，决定目标语言
    3. 根据 SWITCH_METHOD 选择切换方式（API 或模拟）
    4. 切换成功后在独立线程中验证结果
    """
    global last_toggle_time

    # 防抖：0.3 秒内不重复触发
    now = time.time()
    if now - last_toggle_time < 0.3:
        return
    last_toggle_time = now

    # 获取当前语言 ID
    current = get_current_lang_id()
    log(f"当前语言ID: 0x{current:04X}")

    # 决定目标语言
    if current == LANG_ZH:
        target = LANG_EN
        target_name = "英文"
    else:
        target = LANG_ZH
        target_name = "中文"
    log(f"切换到 {target_name}")

    # 根据配置选择切换方式
    if SWITCH_METHOD == 1:
        success = switch_to_lang_api(target)
    else:
        success = switch_to_lang_simulate(target)

    # 处理切换结果
    if success:
        log(f"切换{target_name}指令已执行")
        # 在独立线程中验证结果，避免阻塞主线程
        threading.Thread(target=verify_switch, args=(target, target_name), daemon=True).start()
    else:
        log(f"切换{target_name}失败")


def verify_switch(target, target_name):
    """
    验证切换是否成功（在独立线程中执行）
    等待 0.15 秒后检查当前语言 ID 是否与目标匹配
    """
    time.sleep(0.15)
    new_lang = get_current_lang_id()
    if new_lang == target:
        log(f"✅ 验证成功，当前 {target_name}")
    else:
        log(f"❌ 验证失败，当前仍为 0x{new_lang:04X}")


# =============================================================================
# 配置管理
# 负责加载和保存用户配置（热键、开机自启、切换方式、默认托盘启动等）
# 配置文件保存在 %APPDATA%\IMESwitcher\config.json
# =============================================================================

CONFIG_DIR = os.path.join(os.getenv('APPDATA'), 'IMESwitcher')
CONFIG_FILE = os.path.join(CONFIG_DIR, 'config.json')
DEFAULT_HOTKEY = 'caps lock'  # 默认热键
SWITCH_METHOD = 1  # 1=API, 2=模拟，默认使用 API


def ensure_config_dir():
    """确保配置目录存在"""
    if not os.path.exists(CONFIG_DIR):
        os.makedirs(CONFIG_DIR)


def load_config():
    """
    从配置文件加载所有设置
    返回元组：(hotkey, autostart, method, start_to_tray)
    如果配置文件不存在或读取失败，返回默认值
    """
    ensure_config_dir()
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return (
                    data.get('hotkey', DEFAULT_HOTKEY),  # 热键
                    data.get('autostart', False),  # 开机自启
                    data.get('method', 1),  # 切换方式
                    data.get('start_to_tray', False)  # 默认启动到托盘
                )
        except:
            pass
    return DEFAULT_HOTKEY, False, 1, False


def save_config(hotkey, autostart, method, start_to_tray):
    """
    保存所有配置到文件
    """
    ensure_config_dir()
    with open(CONFIG_FILE, 'w', encoding='utf-8') as f:
        json.dump({
            'hotkey': hotkey,
            'autostart': autostart,
            'method': method,
            'start_to_tray': start_to_tray
        }, f, indent=2)


# =============================================================================
# 开机自启管理
# 通过写入当前用户注册表实现开机自动启动
# 路径：HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
# =============================================================================

def set_autostart(enabled):
    """
    设置或取消开机自启
    参数 enabled: True 表示启用，False 表示禁用
    返回是否设置成功
    """
    key_path = r"Software\Microsoft\Windows\CurrentVersion\Run"
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_SET_VALUE)
        if enabled:
            # 获取当前程序路径（支持打包后的 exe）
            exe_path = sys.executable if getattr(sys, 'frozen', False) else f'"{sys.executable}" "{__file__}"'
            winreg.SetValueEx(key, "IMESwitcher", 0, winreg.REG_SZ, exe_path)
        else:
            # 删除注册表项
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
    """
    检查当前是否已启用开机自启
    返回 True 或 False
    """
    key_path = r"Software\Microsoft\Windows\CurrentVersion\Run"
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, key_path, 0, winreg.KEY_READ)
        value, _ = winreg.QueryValueEx(key, "IMESwitcher")
        winreg.CloseKey(key)
        return True
    except:
        return False


# =============================================================================
# 单实例控制
# 使用 Windows 互斥体（Mutex）确保程序只能运行一个实例
# 如果已有一个实例在运行，弹出提示并退出
# =============================================================================

# 全局互斥体句柄，确保不被垃圾回收
_mutex_handle = None


def check_single_instance():
    """
    检查是否已有程序实例在运行
    使用 Windows 全局互斥体实现
    返回 True 表示没有其他实例，可以继续运行
    返回 False 表示已有实例，应退出
    """
    global _mutex_handle

    mutex_name = "Global\\IMESwitcher_SingleInstance"
    try:
        # 尝试创建互斥体
        _mutex_handle = win32event.CreateMutex(None, False, mutex_name)

        # 检查是否已存在
        if win32api.GetLastError() == winerror.ERROR_ALREADY_EXISTS:
            # 已有实例在运行
            if _mutex_handle:
                win32api.CloseHandle(_mutex_handle)
                _mutex_handle = None
            return False

        return True
    except Exception as e:
        log(f"单实例检查异常: {e}")
        # 尝试另一种方式检查 - 尝试打开已存在的互斥体
        try:
            handle = win32event.OpenMutex(win32con.SYNCHRONIZE, False, mutex_name)
            if handle:
                win32api.CloseHandle(handle)
                return False
        except:
            pass
        # 如果都失败，允许程序继续运行（但记录日志）
        log("单实例检查失败，可能存在风险")
        return True


def release_mutex():
    """释放互斥体（程序退出时调用）"""
    global _mutex_handle
    if _mutex_handle:
        try:
            win32api.CloseHandle(_mutex_handle)
        except Exception as e:
            log(f"释放互斥体异常: {e}")
        _mutex_handle = None


def bring_existing_window_to_front():
    """查找并激活已有实例的窗口"""
    try:
        # 查找窗口标题
        hwnd = win32gui.FindWindow(None, "输入法一键切换")
        if hwnd:
            # 如果窗口最小化，恢复它
            if win32gui.IsIconic(hwnd):
                win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
            # 将窗口置于前台
            win32gui.SetForegroundWindow(hwnd)
            log("已激活已有实例窗口")
            return True
    except Exception as e:
        log(f"激活已有窗口失败: {e}")
    return False


# =============================================================================
# 主应用类 App
# 负责创建 GUI 界面、系统托盘、热键录制和监听、以及所有事件处理
# =============================================================================

class App:
    def __init__(self):
        """
        初始化应用程序
        1. 加载配置
        2. 创建主窗口
        3. 构建 UI 界面
        4. 设置系统托盘
        5. 根据配置决定是否自动启动监听或隐藏窗口
        """
        global SWITCH_METHOD

        # 加载配置
        saved_hotkey, saved_autostart, saved_method, saved_start_to_tray = load_config()
        self.hotkey_str = saved_hotkey
        self.autostart = saved_autostart
        self.start_to_tray = saved_start_to_tray
        SWITCH_METHOD = saved_method

        # 创建 Tkinter 主窗口
        self.root = tk.Tk()
        self.root.title("输入法一键切换")
        self.root.geometry("500x380")
        self.root.minsize(420, 300)
        self.root.resizable(True, True)

        # 窗口关闭事件绑定到 on_close（最小化到托盘，而非退出）
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

        # 界面变量
        self.hotkey_display_var = tk.StringVar(value=self.hotkey_str)
        self.autostart_var = tk.BooleanVar(value=self.autostart)
        self.start_to_tray_var = tk.BooleanVar(value=self.start_to_tray)
        self.method_var = tk.IntVar(value=SWITCH_METHOD)

        # 监听状态
        self.listening = False
        self.listeners = []

        # 托盘图标
        self.tray_icon = None

        # 录制状态
        self.recording = False
        self.record_listeners = []
        self.was_listening = False

        # 构建 UI 和托盘
        self.build_ui()
        self.setup_tray()

        # 如果开启了"默认启动到托盘"，启动时隐藏主窗口
        if self.start_to_tray:
            self.root.withdraw()
            log("默认启动到托盘")

        # 如果开启了开机自启，自动启动监听
        if self.autostart:
            self.start_listening()

        # 记录启动日志
        log("程序启动完成")
        log(f"热键: {self.hotkey_str}, 方法: {'API' if SWITCH_METHOD == 1 else '模拟Win+Space'}")

    # -------------------------------------------------------------------------
    # 窗口管理
    # -------------------------------------------------------------------------

    def on_close(self):
        """
        点击窗口关闭按钮时的行为
        最小化到系统托盘，不退出程序
        """
        self.root.withdraw()
        log("窗口已隐藏到托盘")

    def show_window(self, icon=None, item=None):
        """显示主窗口（从托盘恢复）"""
        self.root.deiconify()
        self.root.lift()

    def hide_window(self):
        """隐藏主窗口（最小化到托盘）"""
        self.root.withdraw()

    # -------------------------------------------------------------------------
    # 创建 UI 界面
    # -------------------------------------------------------------------------

    def build_ui(self):
        """
        构建主窗口的所有 UI 组件
        包含：热键显示/录制、切换方式选择、开机自启、默认托盘启动、状态栏和操作按钮
        使用 grid 布局，支持窗口自适应
        """
        main_frame = ttk.Frame(self.root, padding=15)
        main_frame.pack(fill=tk.BOTH, expand=True)

        # 配置网格权重，实现自适应布局
        main_frame.columnconfigure(0, weight=0)  # 标签列
        main_frame.columnconfigure(1, weight=1)  # 热键显示框（可拉伸）
        main_frame.columnconfigure(2, weight=0)  # 按钮列
        main_frame.columnconfigure(3, weight=0)  # 按钮列
        main_frame.rowconfigure(6, weight=1)

        # 第一行：热键设置
        ttk.Label(main_frame, text="切换热键：").grid(row=0, column=0, sticky='w', pady=5)

        # 热键显示框（点击可开始录制）
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
        self.hotkey_label.bind("<Button-1>", self.start_recording)  # 单击进入录制

        # 更改按钮
        self.change_btn = ttk.Button(main_frame, text="更改", command=self.start_recording)
        self.change_btn.grid(row=0, column=2, sticky='w', pady=5, padx=(0, 5))

        # 取消按钮（录制时显示）
        self.cancel_btn = ttk.Button(main_frame, text="取消", command=self._cancel_recording)
        self.cancel_btn.grid(row=0, column=3, sticky='w', pady=5, padx=(0, 5))
        self.cancel_btn.grid_remove()  # 默认隐藏

        # 第二行：切换方式选择
        ttk.Label(main_frame, text="切换方式：").grid(row=1, column=0, sticky='w', pady=5)
        method_frame = ttk.Frame(main_frame)
        method_frame.grid(row=1, column=1, columnspan=3, sticky='w', pady=5)
        ttk.Radiobutton(method_frame, text="API", variable=self.method_var, value=1,
                        command=self.on_method_change).pack(side=tk.LEFT, padx=5)
        ttk.Radiobutton(method_frame, text="模拟", variable=self.method_var, value=2,
                        command=self.on_method_change).pack(side=tk.LEFT, padx=5)

        # 第三行：开机自启
        self.autostart_check = ttk.Checkbutton(
            main_frame,
            text="开机自动启动",
            variable=self.autostart_var,
            command=self.on_autostart_toggle
        )
        self.autostart_check.grid(row=2, column=0, columnspan=4, sticky='w', pady=5)

        # 第四行：默认启动到托盘
        self.tray_start_check = ttk.Checkbutton(
            main_frame,
            text="默认启动到托盘（启动后自动隐藏窗口）",
            variable=self.start_to_tray_var,
            command=self.on_tray_start_toggle
        )
        self.tray_start_check.grid(row=3, column=0, columnspan=4, sticky='w', pady=5)

        # 第五行：状态栏
        self.status_label = ttk.Label(main_frame, text="状态：未启动", foreground='gray')
        self.status_label.grid(row=4, column=0, columnspan=4, sticky='w', pady=8)

        # 第六行：操作按钮
        btn_frame = ttk.Frame(main_frame)
        btn_frame.grid(row=5, column=0, columnspan=4, pady=10, sticky='ew')
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

    # -------------------------------------------------------------------------
    # UI 事件处理
    # -------------------------------------------------------------------------

    def manual_test(self):
        """手动切换测试按钮：直接调用 toggle_ime 触发一次切换"""
        log(">>> 手动切换测试 <<<")
        toggle_ime()

    def on_method_change(self):
        """
        切换方式 RadioButton 变化事件
        更新全局 SWITCH_METHOD 并保存配置
        """
        global SWITCH_METHOD
        SWITCH_METHOD = self.method_var.get()
        save_config(self.hotkey_str, self.autostart_var.get(), SWITCH_METHOD, self.start_to_tray_var.get())
        log(f"切换方式改为: {'API' if SWITCH_METHOD == 1 else '模拟'}")

    def on_autostart_toggle(self):
        """
        开机自启 Checkbutton 变化事件
        写入或删除注册表项
        """
        enabled = self.autostart_var.get()
        success = set_autostart(enabled)
        if success:
            save_config(self.hotkey_str, enabled, SWITCH_METHOD, self.start_to_tray_var.get())
            messagebox.showinfo("提示", f"开机自启{'已启用' if enabled else '已禁用'}")
            log(f"开机自启设置为: {enabled}")
        else:
            messagebox.showerror("错误", "设置开机自启失败，请检查权限")
            self.autostart_var.set(not enabled)  # 恢复状态

    def on_tray_start_toggle(self):
        """
        默认启动到托盘 Checkbutton 变化事件
        保存配置
        """
        enabled = self.start_to_tray_var.get()
        save_config(self.hotkey_str, self.autostart_var.get(), SWITCH_METHOD, enabled)
        log(f"默认启动到托盘: {'已启用' if enabled else '已禁用'}")

    # -------------------------------------------------------------------------
    # 热键录制
    # 点击热键显示框或"更改"按钮进入录制模式
    # 录制期间监听键盘和鼠标事件，识别用户按下的按键组合
    # -------------------------------------------------------------------------

    def start_recording(self, event=None):
        """
        开始录制热键
        1. 强制停止之前的录制（如果有）
        2. 如果正在监听，先停止监听
        3. 进入录制状态，更新 UI 提示
        4. 启动键盘和鼠标监听器
        """
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
        """
        录制时的键盘事件回调
        识别用户按下的按键，检测修饰键状态，生成热键字符串
        按 ESC 取消录制
        """
        if not self.recording:
            return

        try:
            # 获取按键名称
            if hasattr(key, 'name'):
                key_name = key.name
            elif hasattr(key, 'char') and key.char is not None:
                key_name = key.char
            else:
                key_name = str(key).replace('Key.', '')

            # 忽略单独的修饰键
            if key_name in ['ctrl', 'shift', 'alt', 'cmd', 'ctrl_l', 'ctrl_r', 'shift_l', 'shift_r', 'alt_l', 'alt_r',
                            'cmd_l', 'cmd_r']:
                return

            # ESC 取消录制
            if key == keyboard.Key.esc:
                log("录制取消 (ESC)")
                self.root.after(0, self._cancel_recording)
                return

            # 检测当前按下的修饰键
            mods = []
            if win32api.GetKeyState(win32con.VK_CONTROL) & 0x8000:
                mods.append('ctrl')
            if win32api.GetKeyState(win32con.VK_SHIFT) & 0x8000:
                mods.append('shift')
            if win32api.GetKeyState(win32con.VK_MENU) & 0x8000:
                mods.append('alt')
            if win32api.GetKeyState(win32con.VK_LWIN) & 0x8000 or win32api.GetKeyState(win32con.VK_RWIN) & 0x8000:
                mods.append('win')

            # 生成热键字符串（如 "ctrl+shift+a"）
            new_hotkey = '+'.join(mods + [key_name]) if mods else key_name
            log(f"录制到热键: {new_hotkey}")
            self.root.after(0, self._finish_recording, new_hotkey)

        except Exception as e:
            log(f"录制异常: {e}")
            self.root.after(0, self._cancel_recording)

    def _on_record_click(self, x, y, button, pressed):
        """
        录制时的鼠标事件回调
        识别鼠标侧键（X1/X2）
        """
        if not self.recording or not pressed:
            return
        if button == mouse.Button.x1:
            self.root.after(0, self._finish_recording, 'mouse.x1')
        elif button == mouse.Button.x2:
            self.root.after(0, self._finish_recording, 'mouse.x2')

    def _finish_recording(self, new_hotkey):
        """完成录制，保存热键并恢复监听状态"""
        if not self.recording:
            return

        self._force_stop_recording()
        self.hotkey_str = new_hotkey
        self.hotkey_display_var.set(new_hotkey)
        self.hotkey_label.config(bg='white')
        self.change_btn.config(state=tk.NORMAL)
        self.cancel_btn.grid_remove()
        save_config(self.hotkey_str, self.autostart_var.get(), SWITCH_METHOD, self.start_to_tray_var.get())
        log(f"热键已保存: {self.hotkey_str}")

        if self.was_listening:
            self.start_listening()

    def _cancel_recording(self):
        """取消录制，恢复原有热键显示"""
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
        """强制停止录制，清理所有监听器"""
        self.recording = False
        for lis in getattr(self, 'record_listeners', []):
            try:
                lis.stop()
            except:
                pass
        self.record_listeners = []

        # 恢复 UI 状态
        if hasattr(self, 'hotkey_label'):
            self.hotkey_label.config(bg='white')
        if hasattr(self, 'change_btn'):
            self.change_btn.config(state=tk.NORMAL)
        if hasattr(self, 'cancel_btn'):
            self.cancel_btn.grid_remove()

    # -------------------------------------------------------------------------
    # 热键监听
    # 在后台监听键盘和鼠标事件，当匹配热键时触发切换
    # -------------------------------------------------------------------------

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

    def parse_hotkey(self, hotkey_str):
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

        # 转换主键
        main_key_obj = self.KEY_MAP.get(main_key, main_key)
        return {'type': 'keyboard', 'modifiers': mod_keys, 'main_key': main_key_obj}

    def start_listening(self):
        """
        启动热键监听
        1. 解析热键
        2. 启动键盘和鼠标监听器
        3. 更新 UI 状态
        """
        if self.listening:
            return

        # 解析热键
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
        """
        停止热键监听
        关闭键盘和鼠标监听器，更新 UI 状态
        """
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
        """
        监听时的键盘事件回调
        检查按键是否匹配热键（包括修饰键组合）
        """
        if not self.listening or self.hotkey_parsed['type'] != 'keyboard':
            return

        # 获取按键名称
        try:
            key_str = key.name if hasattr(key, 'name') else (key.char if key.char else str(key))
        except:
            key_str = str(key)

        # 忽略单独的修饰键
        if key_str in ['ctrl', 'shift', 'alt', 'cmd', 'ctrl_l', 'ctrl_r', 'shift_l', 'shift_r']:
            return

        # 检查所有修饰键是否都按下
        for mod in self.hotkey_parsed['modifiers']:
            if not self._is_key_pressed(mod):
                return

        # 检查主键是否匹配
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
            # 使用 after(0) 在主线程中调用 toggle_ime，避免线程冲突
            self.root.after(0, toggle_ime)

    def _on_mouse_click(self, x, y, button, pressed):
        """
        监听时的鼠标事件回调
        检查鼠标点击是否匹配热键（鼠标侧键）
        """
        if not self.listening or not pressed or self.hotkey_parsed['type'] != 'mouse':
            return

        if button == self.hotkey_parsed['button']:
            log(f"鼠标热键匹配: {button}")
            # 延迟 100ms 触发切换，避免与鼠标事件冲突
            self.root.after(100, toggle_ime)

    def _is_key_pressed(self, key_obj):
        """
        检查指定的键盘键是否当前处于按下状态
        使用 win32api.GetKeyState 获取按键状态
        """
        vk_map = {
            keyboard.Key.ctrl: win32con.VK_CONTROL,
            keyboard.Key.shift: win32con.VK_SHIFT,
            keyboard.Key.alt: win32con.VK_MENU,
            keyboard.Key.cmd: win32con.VK_LWIN,
        }

        if key_obj in vk_map:
            return win32api.GetKeyState(vk_map[key_obj]) & 0x8000
        return False

    # -------------------------------------------------------------------------
    # 调试窗口
    # 显示实时日志，便于排查问题
    # -------------------------------------------------------------------------

    def show_debug_window(self):
        """
        打开或激活调试日志窗口
        显示内存中的日志缓冲区内容，并实时追加新日志
        """
        # 如果窗口已存在，激活它
        if hasattr(self, 'debug_win') and self.debug_win.winfo_exists():
            self.debug_win.lift()
            return

        # 创建新的调试窗口
        self.debug_win = tk.Toplevel(self.root)
        self.debug_win.title("调试日志")
        self.debug_win.geometry("600x400")

        frame = ttk.Frame(self.debug_win, padding=10)
        frame.pack(fill=tk.BOTH, expand=True)

        self.debug_text = scrolledtext.ScrolledText(frame, wrap=tk.WORD, font=("Consolas", 9))
        self.debug_text.pack(fill=tk.BOTH, expand=True)

        # 更新全局引用，让 log 函数可以写入
        global debug_text
        debug_text = self.debug_text

        # 显示已有的日志
        for msg in _log_buffer:
            self.debug_text.insert(tk.END, msg + '\n')
        self.debug_text.see(tk.END)

        def on_close():
            global debug_text
            debug_text = None
            self.debug_win.destroy()

        self.debug_win.protocol("WM_DELETE_WINDOW", on_close)

    # -------------------------------------------------------------------------
    # 系统托盘
    # -------------------------------------------------------------------------

    def setup_tray(self):
        """
        创建系统托盘图标
        优先加载本地的 icon.png 作为图标，如果不存在则生成默认的 "I" 图标
        托盘菜单包含：显示设置、启动、停止、退出
        """
        # 确定图标文件路径（支持打包后的环境）
        if getattr(sys, 'frozen', False):
            base_path = sys._MEIPASS
        else:
            base_path = os.path.dirname(os.path.abspath(__file__))
        icon_path = os.path.join(base_path, 'icon.png')

        # 加载图标
        if os.path.exists(icon_path):
            try:
                image = Image.open(icon_path)
                log("已加载本地 icon.png 作为托盘图标")
            except Exception as e:
                log(f"加载 icon.png 失败: {e}，使用默认图标")
                image = self._create_default_icon()
        else:
            log("未找到 icon.png，使用默认图标")
            image = self._create_default_icon()

        # 创建托盘图标
        self.tray_icon = pystray.Icon(
            "ime_switcher",
            image,
            "输入法切换",
            menu=pystray.Menu(
                pystray.MenuItem("显示设置", self.show_window),
                pystray.MenuItem("启动", self.start_listening),
                pystray.MenuItem("停止", self.stop_listening),
                pystray.MenuItem("退出", self.quit_app)
            )
        )

        # 在独立线程中运行托盘图标（避免阻塞主线程）
        threading.Thread(target=self.tray_icon.run, daemon=True).start()

    def _create_default_icon(self):
        """
        创建默认托盘图标（白色背景，黑色字母 "I"）
        作为 icon.png 不存在或加载失败时的备选方案
        """
        image = Image.new('RGB', (64, 64), color='white')
        draw = ImageDraw.Draw(image)
        draw.text((20, 10), "I", fill='black')
        return image

    def quit_app(self, icon=None, item=None):
        """
        退出应用程序
        1. 停止监听
        2. 关闭托盘图标
        3. 释放互斥体
        4. 销毁主窗口
        5. 强制退出进程
        """
        self.stop_listening()
        if self.tray_icon:
            self.tray_icon.stop()

        # 释放互斥体
        release_mutex()

        self.root.quit()
        self.root.destroy()
        os._exit(0)

    def run(self):
        """启动 Tkinter 主事件循环"""
        self.root.mainloop()


# =============================================================================
# 程序入口
# =============================================================================

if __name__ == "__main__":
    # 检查是否以管理员权限运行
    try:
        is_admin = ctypes.windll.shell32.IsUserAnAdmin()
    except:
        is_admin = False

    # 如果没有管理员权限，提示用户并请求重新启动
    if not is_admin:
        reply = messagebox.askyesno("权限不足", "需要管理员权限。\n是否以管理员身份重新启动？")
        if reply:
            ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, " ".join(sys.argv), None, 1)
        sys.exit(0)

    # 检查是否已有实例在运行
    try:
        if not check_single_instance():
            # 已有实例，激活已有窗口并退出
            bring_existing_window_to_front()
            sys.exit(0)
    except Exception as e:
        log(f"单实例检查异常: {e}")
        pass

    # 创建并运行应用
    app = App()
    app.run()
