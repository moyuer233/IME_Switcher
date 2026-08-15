# -*- coding: utf-8 -*-
"""
程序入口
负责管理员权限检查、单实例检查，然后创建并运行主应用
"""
import ctypes
import sys

import crash_report
from app import App
from logger import log
from single_instance import check_single_instance, bring_existing_window_to_front

MB_YESNO = 0x00000004
MB_ICONWARNING = 0x00000030
IDYES = 6


def ask_yes_no(title, message):
    """使用 ctypes 弹出 Windows 原生「是/否」对话框"""
    try:
        ret = ctypes.windll.user32.MessageBoxW(None, message, title, MB_YESNO | MB_ICONWARNING)
        return ret == IDYES
    except Exception:
        return False

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


def main():
    # 尽早安装崩溃报告处理：未捕获异常时在应用目录生成崩溃报告文件
    crash_report.install_crash_handler()

    # 检查是否以管理员权限运行
    try:
        is_admin = ctypes.windll.shell32.IsUserAnAdmin()
    except Exception:
        is_admin = False

    # 如果没有管理员权限，提示用户并请求重新启动
    if not is_admin:
        reply = ask_yes_no("权限不足", "需要管理员权限。\n是否以管理员身份重新启动？")
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

    # 创建并运行应用
    app = App()
    app.run()


if __name__ == "__main__":
    main()
