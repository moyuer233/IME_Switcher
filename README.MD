<img width="128" height="128" alt="icon" src="https://github.com/user-attachments/assets/26a1f20e-15a8-45aa-9ad8-75e3c4e80101" />

# 输入法一键切换 (IME Switcher)

一个轻量级的 Windows 工具，通过自定义快捷键（键盘按键或鼠标侧键）一键切换中英文输入法。**基于 C# NativeAOT 完全重写**，单文件仅约 2.8MB，无任何运行时依赖。

---

## ✨ 功能特点

- 🔑 **自定义热键**：支持键盘按键（含数字小键盘）与鼠标侧键（X1/X2），点击界面录制热键，按 `ESC` 取消。
- 🖱️ **鼠标侧键**：X1 / X2 侧键可直接作为切换或开关热键。
- 🔄 **两种切换方式**：
  - **API 模式**：`LoadKeyboardLayout` + `PostMessage(WM_INPUTLANGCHANGEREQUEST)`，自动验证并回退模拟。
  - **模拟模式**：模拟 `Win+Space`，兼容性最强。
- 🖥️ **PCL 风格界面**：纯 Win32 自绘（无 WinForms/WPF），DirectWrite 文字渲染，GDI+ 平滑圆角，思源黑体 + 微软雅黑，圆角窗口 + 滑块动画。
- 🖱️ **系统托盘**：支持最小化到托盘、托盘右键菜单（显示/启动/停止/退出）。
- 📜 **独立日志窗口**：调试日志独立窗口显示，不遮挡主界面，支持滚动与「手动切换」。
- 📝 **运行日志**：应用目录持续写入 `run.log`，每次启动自动清理；崩溃时自动转存为 `crash_run_*.log` 并生成崩溃报告。
- 🔒 **开机自启**：一键写入当前用户注册表。
- 🚀 **单实例运行**：防止多实例冲突。

---

## 📥 下载

访问 [Releases](https://github.com/moyuer233/IME_Switcher/releases) 下载 **v1.0** 的 `IME_Switcher.exe`，**以管理员身份**运行（程序会自动请求权限，全局热键钩子需要管理员权限）。

---

## 🧑‍💻 使用说明

1. **设置热键**：点击「更改」或热键显示框 → 按下目标按键 / 鼠标侧键（X1/X2）→ `ESC` 取消。开关热键与切换热键不能相同。
2. **切换方式**：选择 `API` 或 `模拟（Win+Space）`。
3. **启动监听**：点击「启动」开始监听热键，点击「停止」结束。
4. **手动切换**：在「调试日志」窗口右上角点击「手动切换」立即触发一次切换。
5. **托盘**：关闭窗口即最小化到托盘，双击托盘图标恢复显示。
6. **调试日志**：点击「调试日志」打开独立日志窗口。

> 提示：热键需要在**目标应用**（如浏览器、编辑器）中获得焦点时按下才生效；主窗口在前台时按热键会被忽略并提示。

---

## 🛠️ 构建

环境要求：Windows 10/11，.NET 9 SDK，Visual Studio 2022（含 C++ 链接器，用于 NativeAOT）。

```bash
cd IME_Switcher.CSharp
dotnet publish -c Release -r win-x64 --self-contained true
```

产物位于 `IME_Switcher.CSharp/bin/Release/net9.0-windows/win-x64/publish/IME_Switcher.exe`（约 2.8MB）。

### 技术栈

- C# / .NET 9 **NativeAOT**（自包含单文件，体积 5MB 以内）
- 纯 **Win32 API**（GDI / GDI+ / DirectWrite / D2D / DWM）自绘界面
- 全局低层钩子（`WH_KEYBOARD_LL` / `WH_MOUSE_LL`）实现热键监听

---

## 🗂️ 旧版说明

原 **Python（pywebview + pynput）版本**已废弃，代码保留在 **`python-legacy`** 分支。如有需要请切换到该分支查看。

---

## ❓ 常见问题

**Q：热键设置了但不生效？**  
A：请确认已点击「启动」开始监听，且当前焦点在目标应用中（主窗口前台时热键会被忽略）。

**Q：切换输入法没有反应？**  
A：某些应用（如游戏、UWP 应用）可能不支持 API 切换，请在「选项」中改用**模拟（Win+Space）**方式。

**Q：程序需要管理员权限？**  
A：全局键盘/鼠标钩子需要管理员权限，程序启动时会自动请求。

**Q：运行日志在哪？**  
A：程序所在目录下的 `run.log`。崩溃时自动生成 `crash_*.txt`（报告）和 `crash_run_*.log`（完整运行日志）。

---

如果觉得好用，请给个 ⭐ Star 支持一下！欢迎提交 Issue 和 Pull Request。
