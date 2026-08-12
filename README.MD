
---

# 输入法一键切换 (IME Switcher)

一个轻量级的 Windows 工具，通过自定义快捷键（键盘或鼠标侧键）一键切换中英文输入法，支持两种切换方式，并提供图形界面和系统托盘。

---

## ✨ 功能特点

- 🔑 自定义热键：支持键盘按键和鼠标侧键，通过点击界面录制热键。
- 🔄 两种切换方式：
  - API 模式：优先使用 `py_win_keyboard_layout` 库，失败后自动降级为 `ctypes` 调用的 Windows API。
  - 模拟模式：模拟 `Win+Space` 系统快捷键，兼容性更强。
- 🖥️ 图形界面：直观的配置窗口，支持热键录制、切换方式选择、开机自启设置。
- 🖱️ 系统托盘：可最小化到托盘，后台静默运行。
- 📜 调试窗口：实时查看日志，便于排查问题。
- 🔒 开机自启：一键写入当前用户注册表，随系统启动。
- 🚀 单实例运行：防止多个程序实例冲突。

---

## 📥 使用方法

### 下载预编译 EXE（推荐）
访问 [Releases](https://github.com/moyuer233/IME_Switcher/releases) 页面，下载最新版本的 `IME_Switcher.exe`，**以管理员身份** 双击运行即可。

### 从源码运行
1. 克隆仓库：
   ```bash
   git clone https://github.com/moyuer233/IME_Switcher.git
   cd IME_Switcher
   ```
2. 安装依赖（推荐使用虚拟环境）：
   ```bash
   pip install -r requirements.txt
   ```
3. 以管理员身份运行：
   ```bash
   python IME_Switcher.py
   ```

---

## 🧑‍💻 使用说明

- 录制热键：点击“更改”按钮或热键显示框，按下你想要的键盘按键或鼠标侧键（X1/X2），按 `ESC` 取消。
- 切换方式：选择 `API`（优先库，失败降级）或 `模拟`（`Win+Space`）。
- 开机自启：勾选后自动写入注册表。
- 启动/停止监听：点击“启动”或“停止”控制热键监听。
- 手动测试：点击“手动切换”立即触发一次切换，验证功能。
- 调试窗口：点击“显示调试”查看实时日志。

---

## 📦 依赖库

运行或构建本程序需要以下 Python 库：

| 库名 | 用途 |
|------|------|
| `pywin32` | Windows API 调用（键盘模拟、注册表操作等） |
| `pynput` | 键盘/鼠标事件监听和录制 |
| `pystray` | 系统托盘图标支持 |
| `Pillow` | 生成托盘图标图像 |
| `py_win_keyboard_layout` | 输入法切换 API（优先方案） |

安装命令：
```bash
pip install pywin32 pynput pystray pillow py_win_keyboard_layout
```

---

## 🔧 自行打包（可选）

如需自行构建可执行文件，使用 PyInstaller 打包。由于依赖了 Tcl/Tk 和 Pillow，打包时需要添加相应的 DLL 和 Tcl/Tk 库文件。具体参数请根据你的 Python 环境调整（已包含在源码注释中）。

---

## ❓ 常见问题

**Q：运行 exe 时提示“需要管理员权限”？**  
A：本程序需要全局键盘钩子，必须以管理员身份运行。打包时已设置 `--uac-admin`，运行时会自动请求权限。

**Q：API 切换方式无效怎么办？**  
A：某些应用（如游戏、UWP 应用）可能不支持 API 切换，可切换为 **模拟** 方式（`Win+Space`）。

**Q：开机自启设置失败？**  
A：请确保以管理员身份运行，如果仍失败，可手动将 exe 快捷方式放入 `shell:startup` 文件夹。

---

## 📜 许可证

MIT License，详见 [LICENSE](LICENSE)。

---

如果觉得好用，请给个 ⭐ Star 支持一下！欢迎提交 Issue 和 Pull Request。