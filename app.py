# -*- coding: utf-8 -*-
"""
主应用（pywebview 版本）
使用 pywebview + HTML/CSS/JS 承载界面，配合系统托盘、热键录制与监听
"""
import json
import os
import queue
import sys
import threading
import time
import traceback

import webview

import config
import hotkey
import ime_switcher
import logger
import tray
from logger import log


def resource_path(relative):
    """获取资源文件的绝对路径（兼容 PyInstaller 打包环境）"""
    if getattr(sys, 'frozen', False):
        base_path = sys._MEIPASS
    else:
        base_path = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base_path, relative)


class Api:
    """
    暴露给前端 JS 调用的接口
    前端通过 window.pywebview.api 调用这些方法
    """

    def __init__(self, app):
        self.app = app

    def get_config(self):
        """读取配置供前端初始化界面"""
        return {
            'hotkey': self.app.hotkey_str,
            'toggle_hotkey': self.app.toggle_hotkey_str,
            'autostart': self.app.autostart,
            'start_to_tray': self.app.start_to_tray,
            'method': config.SWITCH_METHOD,
            'listening': bool(self.app.listener and self.app.listener.listening),
        }

    def start_listening(self):
        """启动热键监听"""
        return self.app.start_listening()

    def stop_listening(self):
        """停止热键监听"""
        self.app.stop_listening()
        return True

    def manual_test(self):
        """手动触发一次输入法切换（强制执行，用于测试）"""
        ime_switcher.toggle_ime(force=True)
        return True

    def set_method(self, method):
        """切换方式变化"""
        config.SWITCH_METHOD = int(method)
        config.save_config(self.app.hotkey_str, self.app.toggle_hotkey_str,
                           self.app.autostart, config.SWITCH_METHOD,
                           self.app.start_to_tray)
        log(f"切换方式改为: {'API' if config.SWITCH_METHOD == 1 else '模拟'}")
        return True

    def set_autostart(self, enabled):
        """开机自启切换"""
        enabled = bool(enabled)
        success = config.set_autostart(enabled)
        if success:
            self.app.autostart = enabled
            config.save_config(self.app.hotkey_str, self.app.toggle_hotkey_str,
                               enabled, config.SWITCH_METHOD, self.app.start_to_tray)
            log(f"开机自启设置为: {enabled}")
        return success

    def set_tray_start(self, enabled):
        """默认启动到托盘切换"""
        self.app.start_to_tray = bool(enabled)
        config.save_config(self.app.hotkey_str, self.app.toggle_hotkey_str,
                           self.app.autostart, config.SWITCH_METHOD,
                           self.app.start_to_tray)
        log(f"默认启动到托盘: {'已启用' if enabled else '已禁用'}")
        return True

    def cancel_recording(self):
        """取消录制"""
        self.app.cancel_recording()
        return True

    def get_logs(self):
        """获取日志缓冲，供前端初始化时显示历史日志"""
        return logger.get_log_buffer()

    def start_recording(self, target):
        """开始录制热键（target: hotkey=切换, toggle=开关）"""
        return self.app.start_recording(target)

    def minimize_window(self):
        """最小化窗口"""
        try:
            import win32con
            import win32gui
            hwnd = self.app._get_hwnd()
            if hwnd:
                win32gui.ShowWindow(hwnd, win32con.SW_MINIMIZE)
                return True
        except Exception:
            pass
        try:
            self.app.window.minimize()
        except Exception:
            pass
        return True

    def close_to_tray(self):
        """关闭窗口（隐藏到托盘）"""
        self.app.hide_window()
        log("窗口已隐藏到托盘")
        return True


class App:
    def __init__(self):
        """加载配置并准备界面"""
        saved = config.load_config()
        self.hotkey_str = saved[0]
        self.toggle_hotkey_str = saved[1]
        self.autostart = saved[2]
        self.start_to_tray = saved[4]
        config.SWITCH_METHOD = saved[3]

        # 监听与录制状态
        self.listener = None
        self.recorder = None
        self.recording_target = None
        self.was_listening = False

        # pywebview 窗口
        self.window = None
        self._quitting = False
        self._gui_ready = False  # 页面加载完成（桥接注入完毕）前不向前端推送

        # 前端日志/事件推送队列：所有线程只入队、绝不阻塞，由专用后台线程统一推送
        # （直接同步调用 evaluate_js 会跨线程阻塞，在 WebView2 未就绪或钩子回调里会导致鼠标卡死）
        self._push_queue = queue.Queue()
        threading.Thread(target=self._push_worker, daemon=True,
                         name='js-push-worker').start()

        # 托盘图标
        self.tray_icon = None

    # -------------------------------------------------------------------------
    # 主循环
    # -------------------------------------------------------------------------

    def run(self):
        """创建窗口并启动 pywebview 主循环"""
        index_url = resource_path(os.path.join('web', 'index.html'))

        self.window = webview.create_window(
            '输入法一键切换',
            url=index_url,
            js_api=Api(self),
            width=560,
            height=640,
            min_size=(480, 560),
            resizable=True,
            confirm_close=False,
            frameless=True,  # 无边框窗口，窗口控制按钮由前端自定义（PCL 风格）
            easy_drag=False,  # 禁用 pywebview 整窗 JS 拖拽，标题栏拖动走 pywebview-drag-region
            hidden=self.start_to_tray,  # 默认启动到托盘：窗口直接隐藏，不显示无响应窗口
        )

        # 窗口关闭时隐藏到托盘而非退出
        self.window.events.closing += self._on_closing
        # 页面加载完成（pywebview 桥接注入完毕）后才允许向前端推送，避免桥接未就绪时阻塞
        self.window.events.loaded += self._on_page_loaded

        # 日志推送到前端调试抽屉
        logger.set_log_sink(self._push_log)

        # 托盘图标
        self.tray_icon = tray.create_tray_icon(self)

        # 记录启动日志
        log("程序启动完成")
        log(f"热键: {self.hotkey_str}, 方法: {'API' if config.SWITCH_METHOD == 1 else '模拟Win+Space'}")

        # 自动监听：开机自启或默认托盘启动时后台常驻
        if self.autostart or self.start_to_tray:
            self.start_listening()

        # 启动 GUI 主循环（阻塞直到所有窗口关闭）
        # storage_path 固定 WebView2 用户数据目录 + 关闭 private_mode：
        # 否则 pywebview 每次启动都用全新临时目录，WebView2 必须完整重建 profile/缓存，
        # 导致启动卡顿 20+ 秒（用户感知为"卡死"）
        webview.start(
            func=self._on_gui_started,
            private_mode=False,
            storage_path=os.path.join(os.getenv('APPDATA'), 'IMESwitcher', 'webview'),
        )

        # 清理并退出
        import single_instance
        single_instance.release_mutex()
        os._exit(0)

    def _on_gui_started(self):
        """GUI 主循环就绪后回调（就绪标志改由页面加载完成后置位，见 _on_page_loaded）"""
        # 若页面长时间未加载完成，抓取各线程堆栈写入日志，用于定位卡死现场
        self._start_init_watchdog()
        # 周期性检测主窗口是否无响应（死锁），发现后自动 dump 线程堆栈
        self._start_hang_watchdog()
        # 若开启"默认启动到托盘"，窗口已在创建时隐藏（hidden=True），无需再次隐藏
        if self.start_to_tray:
            log("默认启动到托盘")

    def _start_hang_watchdog(self):
        """挂起检测：每 3 秒用带超时的 SendMessageTimeout 探测主窗口。
        若窗口无响应（GUI 线程死锁），把各线程堆栈写入日志，便于事后定位。"""
        def watchdog():
            import ctypes
            result_ptr = ctypes.c_ulong()
            while True:
                time.sleep(3)
                if self._quitting:
                    return
                try:
                    import win32gui
                    hwnd = win32gui.FindWindow(None, '输入法一键切换')
                    if not hwnd:
                        continue
                    # SMTO_ABORTIFHUNG：目标窗口挂起时立即返回，不等待
                    ok = ctypes.windll.user32.SendMessageTimeoutW(
                        hwnd, 0, 0, 0, 0x0002, 500,
                        ctypes.byref(result_ptr))
                    if ok == 0:
                        frames = sys._current_frames()
                        lines = [
                            "[hang] 主窗口 GUI 线程无响应（疑似死锁），各线程堆栈："]
                        for tid, frame in frames.items():
                            stack = traceback.extract_stack(frame)
                            if stack:
                                f = stack[-1]
                                lines.append(
                                    f"  thread[{tid}] {f.filename}:{f.lineno} "
                                    f"{f.name}() -> {f.line}")
                        logger.write_watchdog_report('\n'.join(lines))
                except Exception:
                    pass
        threading.Thread(target=watchdog, daemon=True,
                         name='hang-watchdog').start()

    def _start_init_watchdog(self):
        """GUI 初始化 watchdog：8 秒后页面仍未加载完成则把线程堆栈写入日志。"""
        def watchdog():
            time.sleep(8)
            if self._gui_ready:
                return
            frames = sys._current_frames()
            lines = ["[watchdog] GUI 初始化超过 8 秒未完成，各线程堆栈："]
            for tid, frame in frames.items():
                stack = traceback.extract_stack(frame)
                if stack:
                    f = stack[-1]
                    lines.append(
                        f"  thread[{tid}] {f.filename}:{f.lineno} {f.name}() "
                        f"-> {f.line}")
            logger.write_watchdog_report('\n'.join(lines))
        threading.Thread(target=watchdog, daemon=True,
                         name='gui-watchdog').start()

    def _on_page_loaded(self):
        """页面加载完成（pywebview 桥接注入完毕）后置位就绪标志"""
        self._gui_ready = True
        log("GUI 已就绪")

    # -------------------------------------------------------------------------
    # 窗口管理
    # -------------------------------------------------------------------------

    def _get_hwnd(self):
        """获取主窗口原生句柄"""
        # 优先从 pywebview 原生窗口对象取句柄（winforms 后端 native.Handle）
        try:
            native = getattr(self.window, 'native', None)
            if native is not None:
                handle = getattr(native, 'Handle', None)
                if handle is not None:
                    return int(handle)
        except Exception:
            pass

        # 兜底：按窗口标题查找
        try:
            import win32gui
            return win32gui.FindWindow(None, '输入法一键切换')
        except Exception:
            return None

    def _on_closing(self):
        """窗口关闭按钮：隐藏到托盘而非退出"""
        if self._quitting:
            return True
        try:
            self.hide_window()
        except Exception:
            pass
        log("窗口已隐藏到托盘")
        return False  # 阻止关闭，改为隐藏

    def show_window(self, icon=None, item=None):
        """从托盘恢复显示主窗口"""
        # 优先使用 Win32 API 直接显示窗口（pywebview 的 show() 依赖 shown 事件，隐藏后可能超时）
        try:
            import win32con
            import win32gui
            hwnd = self._get_hwnd()
            if hwnd:
                win32gui.ShowWindow(hwnd, win32con.SW_SHOW)
                win32gui.SetForegroundWindow(hwnd)
                log("已从托盘显示窗口")
                return
        except Exception as e:
            log(f"Win32 显示窗口失败: {e}")

        # 兜底：使用 pywebview 原生 API
        try:
            self.window.show()
        except Exception as e:
            log(f"显示窗口失败: {e}")

    def hide_window(self):
        """隐藏主窗口（最小化到托盘）"""
        # 优先使用 Win32 API 直接隐藏窗口
        try:
            import win32con
            import win32gui
            hwnd = self._get_hwnd()
            if hwnd:
                win32gui.ShowWindow(hwnd, win32con.SW_HIDE)
                return
        except Exception:
            pass

        # 兜底：使用 pywebview 原生 API
        try:
            self.window.hide()
        except Exception:
            pass

    def quit_app(self, icon=None, item=None):
        """退出应用程序"""
        self._quitting = True
        self.stop_listening()
        if self.tray_icon:
            try:
                self.tray_icon.stop()
            except Exception:
                pass
        import single_instance
        single_instance.release_mutex()
        if self.window:
            try:
                self.window.destroy()
            except Exception:
                pass
        os._exit(0)

    # -------------------------------------------------------------------------
    # 热键录制
    # -------------------------------------------------------------------------

    def _schedule(self, func, *args, delay=0):
        """
        调度回调（pywebview 无 tk 主线程概念，用线程 + 延迟实现）
        :param func: 要执行的回调
        :param delay: 延迟毫秒数
        """
        def runner():
            if delay > 0:
                time.sleep(delay / 1000.0)
            try:
                func(*args)
            except Exception as e:
                log(f"回调异常: {e}")
        threading.Thread(target=runner, daemon=True).start()

    def start_recording(self, target='hotkey'):
        """开始录制热键（target: hotkey=切换, toggle=开关）"""
        if self.recorder and self.recorder.recording:
            self.force_stop_recording()

        self.recording_target = target
        self.was_listening = self.listener is not None and self.listener.listening
        if self.was_listening:
            self.stop_listening()

        self.recorder = hotkey.HotkeyRecorder(
            on_recorded=self.finish_recording,
            on_cancel=self.cancel_recording,
            schedule=self._schedule,
        )
        if not self.recorder.start():
            self.force_stop_recording()
            log("录制启动失败")
            return False
        log(f"开始录制{'开关' if target == 'toggle' else '切换'}热键")
        return True

    def finish_recording(self, new_hotkey):
        """完成录制，保存热键并恢复监听"""
        if not (self.recorder and self.recorder.recording):
            return

        target = self.recording_target
        self.force_stop_recording()

        if target == 'toggle':
            self.toggle_hotkey_str = new_hotkey
            log(f"开关热键已保存: {self.toggle_hotkey_str}")
        else:
            self.hotkey_str = new_hotkey
            log(f"切换热键已保存: {self.hotkey_str}")

        config.save_config(self.hotkey_str, self.toggle_hotkey_str,
                           self.autostart, config.SWITCH_METHOD, self.start_to_tray)

        # 通知前端刷新热键显示
        self._emit('hotkey_saved', {'target': target, 'value': new_hotkey})

        if self.was_listening:
            self.start_listening()

    def cancel_recording(self):
        """取消录制"""
        if not (self.recorder and self.recorder.recording):
            return
        self.force_stop_recording()
        self._emit('recording_cancel', {})
        if self.was_listening:
            self.start_listening()

    def force_stop_recording(self):
        """强制停止录制，清理监听器"""
        if self.recorder:
            self.recorder.stop()
            self.recorder = None
        self.recording_target = None

    # -------------------------------------------------------------------------
    # 热键监听
    # -------------------------------------------------------------------------

    def start_listening(self):
        """启动热键监听"""
        if self.listener and self.listener.listening:
            return True

        # 解析切换热键
        hotkey_parsed = hotkey.parse_hotkey(self.hotkey_str)
        if not hotkey_parsed:
            log(f"热键解析失败: {self.hotkey_str}")
            return False

        # 构建热键规则：切换热键 + 监听开关热键（如已设置）
        rules = [(hotkey_parsed, self.on_hotkey_triggered)]
        if self.toggle_hotkey_str:
            toggle_parsed = hotkey.parse_hotkey(self.toggle_hotkey_str)
            if toggle_parsed:
                rules.append((toggle_parsed, self.on_toggle_listening))
            else:
                log(f"开关热键解析失败: {self.toggle_hotkey_str}")

        self.listener = hotkey.HotkeyListener(rules=rules, schedule=self._schedule)
        if not self.listener.start():
            self.listener = None
            log("启动监听失败")
            return False

        log(f"监听启动，切换热键: {self.hotkey_str}"
            + (f"，开关热键: {self.toggle_hotkey_str}" if self.toggle_hotkey_str else ""))
        self._emit('listening', {'hotkey': self.hotkey_str,
                                 'toggle_hotkey': self.toggle_hotkey_str})
        return True

    def stop_listening(self):
        """停止热键监听"""
        if not (self.listener and self.listener.listening):
            return
        self.listener.stop()
        self.listener = None
        log("监听已停止")
        self._emit('stopped', {})

    def on_hotkey_triggered(self):
        """切换热键触发：执行输入法切换"""
        ime_switcher.toggle_ime()

    def on_toggle_listening(self):
        """开关热键触发：在启动与停止监听之间切换"""
        if self.listener and self.listener.listening:
            self.stop_listening()
        else:
            self.start_listening()

    # -------------------------------------------------------------------------
    # 前端通信
    # -------------------------------------------------------------------------

    def _push_worker(self):
        """后台线程：把队列中的日志/事件脚本推送到前端。

        仅此线程会调用 evaluate_js（跨线程同步阻塞），其余任何线程（pynput 钩子、
        托盘、定时器等）都只入队后立即返回，从而杜绝钩子回调阻塞导致的全局鼠标卡死。
        """
        while True:
            try:
                script = self._push_queue.get(timeout=0.5)
            except queue.Empty:
                continue
            if self.window is None or not self._gui_ready:
                continue
            try:
                self.window.evaluate_js(script)
            except Exception:
                pass

    def _emit(self, event, data=None):
        """向 JS 端推送事件（入队异步推送，绝不阻塞调用线程）"""
        if self.window is None or not self._gui_ready:
            return
        try:
            payload = json.dumps(data if data is not None else {})
            self._push_queue.put(
                f"window.__onEvent({json.dumps(event)}, {payload})")
        except Exception:
            pass

    def _push_log(self, msg):
        """将日志推送到前端调试抽屉（入队异步推送，绝不阻塞调用线程）"""
        if self.window is None or not self._gui_ready:
            return
        try:
            self._push_queue.put(
                f"window.__onEvent('log', {json.dumps(str(msg))})")
        except Exception:
            pass
