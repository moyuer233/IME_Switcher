# -*- coding: utf-8 -*-
"""
崩溃报告模块
捕获未捕获异常（主线程 / 线程 / 原生信号），在应用所在目录生成崩溃报告文件，
附带异常堆栈、各线程堆栈与最近日志，便于事后定位崩溃原因。
"""
import os
import sys
import threading
import time
import traceback

import logger

# 崩溃报告附带的最近日志条数
_RECENT_LOG_LINES = 80


def report_dir():
    """崩溃报告目录：打包后为 exe 所在目录，开发时为脚本所在目录"""
    if getattr(sys, 'frozen', False):
        return os.path.dirname(sys.executable)
    return os.path.dirname(os.path.abspath(__file__))


def _threads_text():
    """收集所有 Python 线程当前堆栈（用于定位崩溃/卡死现场）"""
    lines = []
    frames = sys._current_frames()
    for tid, frame in frames.items():
        stack = traceback.extract_stack(frame)
        if not stack:
            continue
        f = stack[-1]
        lines.append(
            f"thread[{tid}] {f.filename}:{f.lineno} {f.name}() -> {f.line}")
    return '\n'.join(lines)


def _recent_logs():
    """最近日志（供崩溃报告参考）"""
    buf = logger.get_log_buffer()
    return '\n'.join(buf[-_RECENT_LOG_LINES:]) if buf else '(无日志)'


def _write_report(kind, exc_text):
    try:
        d = report_dir()
        os.makedirs(d, exist_ok=True)
        fname = time.strftime('crash_%Y%m%d_%H%M%S.txt')
        path = os.path.join(d, fname)
        with open(path, 'w', encoding='utf-8') as f:
            f.write("=" * 60 + "\n")
            f.write("IME 输入法切换 - 崩溃报告\n")
            f.write("=" * 60 + "\n")
            f.write(f"时间: {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
            f.write(f"系统: {sys.platform}\n")
            f.write(f"Python: {sys.version.replace(chr(10), ' ')}\n")
            f.write(f"程序: {sys.executable}\n")
            f.write(f"类型: {kind}\n")
            f.write("-" * 60 + "\n")
            f.write(exc_text)
            f.write("\n" + "-" * 60 + "\n")
            f.write("各线程堆栈:\n")
            f.write(_threads_text() + "\n")
            f.write("-" * 60 + "\n")
            f.write("最近日志:\n")
            f.write(_recent_logs() + "\n")
        return path
    except Exception:
        return None


def install_crash_handler():
    """安装全局未捕获异常处理（主线程 + 线程 + 原生崩溃信号）"""

    def _on_uncaught(kind, exc_type, exc_value, exc_tb):
        exc_text = ''.join(traceback.format_exception(exc_type, exc_value, exc_tb))
        path = _write_report(kind, exc_text)
        # 同步写入日志文件，确保崩溃现场被记录
        try:
            logger.write_watchdog_report(
                f"[crash] {kind}: {exc_text.strip()}"
                + (f" -> 崩溃报告: {path}" if path else ""))
        except Exception:
            pass

    def handle_excepthook(exc_type, exc_value, exc_tb):
        _on_uncaught('主线程未捕获异常', exc_type, exc_value, exc_tb)
        sys.__excepthook__(exc_type, exc_value, exc_tb)

    def handle_thread_excepthook(args):
        _on_uncaught('线程未捕获异常', args.exc_type, args.exc_value, args.exc_traceback)

    sys.excepthook = handle_excepthook
    try:
        threading.excepthook = handle_thread_excepthook
    except AttributeError:
        pass  # Python < 3.8 没有 threading.excepthook

    # 原生崩溃（段错误等）写入固定文件（每次启动覆盖，不累积）
    try:
        import faulthandler
        d = report_dir()
        os.makedirs(d, exist_ok=True)
        native_path = os.path.join(d, 'crash_native.log')
        faulthandler.enable(open(native_path, 'w'))
    except Exception:
        pass
