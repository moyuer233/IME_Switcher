# -*- coding: utf-8 -*-
"""
单实例控制
使用 Windows 互斥体（Mutex）确保程序只能运行一个实例
如果已有一个实例在运行，弹出提示并退出
"""
import win32api
import win32con
import win32event
import win32gui
import winerror

from logger import log

# 全局互斥体句柄，确保不被垃圾回收
_mutex_handle = None

# 互斥体名称
MUTEX_NAME = "Global\\IMESwitcher_SingleInstance"

# 主窗口标题（用于激活已有实例）
WINDOW_TITLE = "输入法一键切换"


def check_single_instance():
    """
    检查是否已有程序实例在运行
    使用 Windows 全局互斥体实现
    返回 True 表示没有其他实例，可以继续运行
    返回 False 表示已有实例，应退出
    """
    global _mutex_handle

    try:
        # 尝试创建互斥体
        _mutex_handle = win32event.CreateMutex(None, False, MUTEX_NAME)

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
            handle = win32event.OpenMutex(win32con.SYNCHRONIZE, False, MUTEX_NAME)
            if handle:
                win32api.CloseHandle(handle)
                return False
        except Exception:
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
        hwnd = win32gui.FindWindow(None, WINDOW_TITLE)
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
