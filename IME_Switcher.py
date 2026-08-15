# -*- coding: utf-8 -*-
"""
核心切换逻辑
负责获取当前语言 ID，以及通过 API 或模拟方式切换输入法
"""
import ctypes
import os
import threading
import time

import config
from logger import log

# Windows API 相关常量
user32 = ctypes.windll.user32
WM_INPUTLANGCHANGEREQUEST = 0x0050
KLF_ACTIVATE = 0x00000001
KLF_SETFORPROCESS = 0x00000100

# 语言 ID 常量（作为兜底目标值，实际目标优先从已安装布局中选取）
LANG_EN = 0x0409  # 美式英语
LANG_ZH = 0x0804  # 简体中文

# 主语言 ID（LANGID 低 10 位）：0x09=英文, 0x04=中文
PRIMARY_ENGLISH = 0x09
PRIMARY_CHINESE = 0x04

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


def get_installed_lang_ids():
    """
    枚举系统已安装的键盘布局语言 ID 列表（去重）
    用于动态挑选目标语言，避免硬编码 0x0409/0x0804 导致的误判
    """
    try:
        count = user32.GetKeyboardLayoutList(0, None)
        if count <= 0:
            return []
        hkl_list = (ctypes.c_void_p * count)()
        n = user32.GetKeyboardLayoutList(count, hkl_list)
        lang_ids = []
        for i in range(n):
            lang_id = hkl_list[i] & 0xFFFF
            if lang_id not in lang_ids:
                lang_ids.append(lang_id)
        return lang_ids
    except Exception as e:
        log(f"枚举键盘布局失败: {e}")
        return []


def primary_lang_id(lang_id):
    """提取主语言 ID（LANGID 低 10 位）"""
    return lang_id & 0x3FF


def is_chinese(lang_id):
    """判断语言 ID 是否属于中文（按主语言 ID 判断，兼容搜狗等各类中文输入法）"""
    return primary_lang_id(lang_id) == PRIMARY_CHINESE


def pick_target_lang_id(want_chinese):
    """
    从已安装布局中挑选目标语言 ID
    want_chinese=True 优先中文（简体 0x0804），False 优先英文（美式 0x0409）
    找不到时回退到硬编码的兜底值
    """
    installed = get_installed_lang_ids()
    if want_chinese:
        if LANG_ZH in installed:
            return LANG_ZH
        for lang_id in installed:
            if is_chinese(lang_id):
                return lang_id
        return LANG_ZH
    else:
        if LANG_EN in installed:
            return LANG_EN
        for lang_id in installed:
            if primary_lang_id(lang_id) == PRIMARY_ENGLISH:
                return lang_id
        return LANG_EN


def switch_to_lang_api(target_lang_id):
    """
    使用 API 方式切换输入法（异步投递，绝不阻塞）

    注意：不能用 SendMessage 把 WM_INPUTLANGCHANGEREQUEST 发给前台窗口——
    当本程序窗口为前台时，同进程同步 SendMessage 会在 WebView2 的 IME 处理链上
    死锁（GUI 线程无法泵消息），导致界面卡死、鼠标冻结。
    统一改用 PostMessage 异步投递。
    """
    log(f"API: 尝试切换到 0x{target_lang_id:04X}")

    try:
        # 加载键盘布局
        hkl = user32.LoadKeyboardLayoutW(f"0x{target_lang_id:08x}", KLF_ACTIVATE)
        if not hkl:
            log("API: LoadKeyboardLayoutW 失败")
            return False

        # 激活当前进程线程的键盘布局
        user32.ActivateKeyboardLayout(hkl, KLF_SETFORPROCESS)

        # 向前台窗口异步投递切换消息（PostMessage 不阻塞，避免死锁）
        hwnd = user32.GetForegroundWindow()
        result = user32.PostMessageW(hwnd, WM_INPUTLANGCHANGEREQUEST, 0, hkl)
        log(f"API: PostMessageW 返回 {result}")

        # 触发系统事件，刷新窗口状态
        user32.NotifyWinEvent(0x8000, hwnd, 0, 0)
        return True
    except Exception as e:
        log(f"API 异常: {e}")
        return False


def switch_to_lang_simulate(target_lang_id):
    """
    使用模拟方式切换输入法（备选方案）
    通过 keybd_event 模拟按下 Win+Space 系统快捷键
    兼容性更好，适用于 API 方式无效的场景（如游戏、UWP 应用）
    """
    import win32api
    import win32con
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


def is_own_window(hwnd):
    """判断指定窗口是否属于本进程（避免向自己窗口发送切换消息）"""
    if not hwnd:
        return False
    try:
        pid = ctypes.c_ulong()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return pid.value == os.getpid()
    except Exception:
        return False


def toggle_ime(force=False):
    """
    输入法切换入口函数
    1. 防抖检查（0.3 秒内不重复触发）
    2. 前台窗口是本程序自身时忽略（自己的窗口里切换输入法无意义，且避免窗口消息卡顿）；
       force=True（手动切换按钮）时强制执行，用于测试
    3. 获取当前语言 ID，决定目标语言
    4. 根据 SWITCH_METHOD 选择切换方式；API 方式若未真正生效，自动回退模拟 Win+Space
    5. 切换成功后在独立线程中验证结果
    """
    global last_toggle_time

    # 防抖：0.3 秒内不重复触发
    now = time.time()
    if now - last_toggle_time < 0.3:
        return
    last_toggle_time = now

    # 前台窗口是自身主窗口时忽略（不发送切换消息，避免与自身窗口/WebView2 交互）
    if not force and is_own_window(user32.GetForegroundWindow()):
        log("主窗口在前台，忽略热键切换（切换输入法针对其他应用）")
        return

    # 获取当前语言 ID
    current = get_current_lang_id()
    log(f"当前语言ID: 0x{current:04X}")

    # 判断当前是否为中文（按主语言 ID，避免中文输入法非 0x0804 时误判）
    if is_chinese(current):
        target = pick_target_lang_id(want_chinese=False)
        target_name = "英文"
    else:
        target = pick_target_lang_id(want_chinese=True)
        target_name = "中文"
    log(f"切换到 {target_name} (目标 0x{target:04X})")

    # 根据配置选择切换方式
    if config.SWITCH_METHOD == 1:
        success = switch_to_lang_api(target)
        # 仅当 API 方式明确失败（如加载布局失败）时才回退模拟 Win+Space；
        # 不做语言验证回退——PostMessage 异步生效存在延迟，立即验证会误判并导致双重切换"回弹"
        if not success:
            log("API 切换失败，回退为模拟 Win+Space")
            success = switch_to_lang_simulate(target)
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
    按主语言 ID 比较，兼容同语言不同子语言/IME 的布局变体
    """
    time.sleep(0.15)
    new_lang = get_current_lang_id()
    if primary_lang_id(new_lang) == primary_lang_id(target):
        log(f"✅ 验证成功，当前 {target_name}")
    else:
        log(f"❌ 验证失败，当前仍为 0x{new_lang:04X}")
