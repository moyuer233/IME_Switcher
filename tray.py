# -*- coding: utf-8 -*-
"""
系统托盘
负责创建系统托盘图标与菜单，以及图标加载（支持打包环境）
"""
import os
import sys
import threading

import pystray
from PIL import Image, ImageDraw

from logger import log


def resource_path(relative_path):
    """
    获取资源文件的绝对路径（兼容 PyInstaller 打包环境）
    打包后资源位于 sys._MEIPASS 临时目录
    """
    if getattr(sys, 'frozen', False):
        base_path = sys._MEIPASS
    else:
        base_path = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base_path, relative_path)


def load_icon_image():
    """
    加载托盘图标图像
    优先加载本地的 icon.png，如果不存在或加载失败则生成默认的 "I" 图标
    """
    icon_path = resource_path('icon.png')
    if os.path.exists(icon_path):
        try:
            image = Image.open(icon_path)
            log("已加载本地 icon.png 作为托盘图标")
            return image
        except Exception as e:
            log(f"加载 icon.png 失败: {e}，使用默认图标")
    else:
        log("未找到 icon.png，使用默认图标")
    return create_default_icon()


def create_default_icon():
    """创建默认托盘图标（白色背景，黑色字母 "I"）"""
    image = Image.new('RGB', (64, 64), color='white')
    draw = ImageDraw.Draw(image)
    draw.text((20, 10), "I", fill='black')
    return image


def create_tray_icon(app):
    """
    创建系统托盘图标并在独立线程中运行
    :param app: App 实例，托盘菜单回调指向其方法
    :return: 创建的托盘图标对象
    """
    tray_icon = pystray.Icon(
        "ime_switcher",
        load_icon_image(),
        "输入法切换",
        menu=pystray.Menu(
            # default=True：单击/双击托盘图标时直接打开主窗口
            pystray.MenuItem("显示设置", app.show_window, default=True),
            pystray.MenuItem("启动", app.start_listening),
            pystray.MenuItem("停止", app.stop_listening),
            pystray.MenuItem("退出", app.quit_app),
        ),
    )
    threading.Thread(target=tray_icon.run, daemon=True).start()
    return tray_icon
