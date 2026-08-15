# -*- coding: utf-8 -*-
"""
日志系统（异步版）
log() 只将日志放入内存队列并立即返回，由后台日志线程统一执行
文件写入、控制台输出、内存缓冲与前端推送等 I/O 操作。

背景：热键监听使用 pynput 全局低层钩子，钩子回调一旦被同步磁盘 I/O
卡住（杀毒扫描、日志文件膨胀等），整个系统的鼠标/键盘输入都会冻结。
异步化后，任何线程（尤其是钩子回调）调用 log() 都不会阻塞。
"""
import os
import queue
import sys
import threading
import time

# 日志目录和文件路径
LOG_DIR = os.path.join(os.getenv('APPDATA'), 'IMESwitcher')
LOG_FILE = os.path.join(LOG_DIR, 'log.txt')

# 内存日志缓冲区，用于调试抽屉显示历史日志
_log_buffer = []
MAX_BUFFER_SIZE = 2000  # 防止内存无限增长

# 前端日志推送回调（pywebview 界面使用，由 app 模块注入）
_log_sink = None

# 日志队列：所有线程只入队，I/O 由后台日志线程统一执行
_log_queue = queue.Queue(maxsize=2000)  # 队列满时丢弃新日志，绝不阻塞调用方
_log_thread = None
_log_thread_lock = threading.Lock()


def set_log_sink(callback):
    """设置日志推送回调（用于前端调试抽屉实时显示日志）"""
    global _log_sink
    _log_sink = callback


def get_log_buffer():
    """返回内存中的日志缓冲列表（供前端初始化时显示历史日志）"""
    return list(_log_buffer)


def ensure_log_dir():
    """确保日志目录存在，如果不存在则创建"""
    if not os.path.exists(LOG_DIR):
        os.makedirs(LOG_DIR)


def _log_worker():
    """后台日志线程：依次执行写文件、控制台输出、内存缓冲、前端推送"""
    while True:
        item = _log_queue.get()
        if item is None:
            return
        full_msg = item

        # 输出到控制台（仅当控制台存在时，避免打包后报错）
        if sys.stdout is not None:
            try:
                print(full_msg)
                sys.stdout.flush()
            except Exception:
                pass

        # 写入日志文件
        try:
            ensure_log_dir()
            with open(LOG_FILE, 'a', encoding='utf-8') as f:
                f.write(full_msg + '\n')
        except Exception:
            pass

        # 写入内存缓冲区，超过上限时丢弃最旧日志
        _log_buffer.append(full_msg)
        if len(_log_buffer) > MAX_BUFFER_SIZE:
            del _log_buffer[:len(_log_buffer) - MAX_BUFFER_SIZE]

        # 推送给前端调试抽屉（app 的 _push_log 只入队，不会阻塞）
        if _log_sink is not None:
            try:
                _log_sink(full_msg)
            except Exception:
                pass


def _ensure_log_thread():
    """确保日志线程已启动（线程安全）"""
    global _log_thread
    with _log_thread_lock:
        if _log_thread is None or not _log_thread.is_alive():
            _log_thread = threading.Thread(target=_log_worker, daemon=True,
                                           name='log-worker')
            _log_thread.start()


def log(msg):
    """
    写入日志消息（异步）：仅入队立即返回，绝不阻塞调用线程。
    文件/控制台/缓冲/前端推送由后台日志线程完成。
    """
    timestamp = time.strftime('%H:%M:%S')
    full_msg = f"[{timestamp}] {msg}"

    _ensure_log_thread()
    try:
        _log_queue.put_nowait(full_msg)
    except queue.Full:
        pass  # 队列满时丢弃，绝不阻塞


def write_watchdog_report(report):
    """
    紧急诊断报告（同步写文件，供 watchdog 定位卡死现场）。
    与 log() 无关，绕过日志队列直接落盘。
    """
    try:
        ensure_log_dir()
        with open(LOG_FILE, 'a', encoding='utf-8') as f:
            f.write(report + '\n')
    except Exception:
        pass
