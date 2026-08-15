# -*- coding: utf-8 -*-
"""
配置管理
负责加载和保存用户配置（热键、开机自启、切换方式、默认托盘启动等），
以及开机自启注册表的读写。
配置文件保存在 %APPDATA%\\IMESwitcher\\config.json
"""
import json
import os
import sys
import winreg

from logger import log

CONFIG_DIR = os.path.join(os.getenv('APPDATA'), 'IMESwitcher')
CONFIG_FILE = os.path.join(CONFIG_DIR, 'config.json')
DEFAULT_HOTKEY = 'caps lock'  # 默认热键
DEFAULT_TOGGLE_HOTKEY = ''  # 监听开关热键，空字符串表示未设置
SWITCH_METHOD = 1  # 1=API, 2=模拟，默认使用 API


def ensure_config_dir():
    """确保配置目录存在"""
    if not os.path.exists(CONFIG_DIR):
        os.makedirs(CONFIG_DIR)


def load_config():
    """
    从配置文件加载所有设置
    返回元组：(hotkey, toggle_hotkey, autostart, method, start_to_tray)
    如果配置文件不存在或读取失败，返回默认值
    """
    ensure_config_dir()
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return (
                    data.get('hotkey', DEFAULT_HOTKEY),  # 切换热键
                    data.get('toggle_hotkey', DEFAULT_TOGGLE_HOTKEY),  # 监听开关热键
                    data.get('autostart', False),  # 开机自启
                    data.get('method', 1),  # 切换方式
                    data.get('start_to_tray', False)  # 默认启动到托盘
                )
        except Exception:
            pass
    return DEFAULT_HOTKEY, DEFAULT_TOGGLE_HOTKEY, False, 1, False


def save_config(hotkey, toggle_hotkey, autostart, method, start_to_tray):
    """
    保存所有配置到文件
    """
    ensure_config_dir()
    with open(CONFIG_FILE, 'w', encoding='utf-8') as f:
        json.dump({
            'hotkey': hotkey,
            'toggle_hotkey': toggle_hotkey,
            'autostart': autostart,
            'method': method,
            'start_to_tray': start_to_tray
        }, f, indent=2)


# =============================================================================
# 开机自启管理
# 通过写入当前用户注册表实现开机自动启动
# 路径：HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
# =============================================================================

AUTOSTART_KEY_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"
AUTOSTART_VALUE_NAME = "IMESwitcher"


def set_autostart(enabled):
    """
    设置或取消开机自启
    参数 enabled: True 表示启用，False 表示禁用
    返回是否设置成功
    """
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, AUTOSTART_KEY_PATH, 0, winreg.KEY_SET_VALUE)
        if enabled:
            # 获取当前程序路径（支持打包后的 exe）
            exe_path = sys.executable if getattr(sys, 'frozen', False) else f'"{sys.executable}" "{__file__}"'
            winreg.SetValueEx(key, AUTOSTART_VALUE_NAME, 0, winreg.REG_SZ, exe_path)
        else:
            # 删除注册表项
            try:
                winreg.DeleteValue(key, AUTOSTART_VALUE_NAME)
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
    try:
        key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, AUTOSTART_KEY_PATH, 0, winreg.KEY_READ)
        winreg.QueryValueEx(key, AUTOSTART_VALUE_NAME)
        winreg.CloseKey(key)
        return True
    except Exception:
        return False
