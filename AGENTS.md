# AGENTS.md — IME Switcher 项目专属规则

> 本文件是该项目的专属层：DSH 把工作区根目录的 `AGENTS.md` 叠加注入到本工作区的会话
> （全局层先注入、项目层随后叠加，更具体的优先）。
> 通用铁律在 `~/.dsh/AGENTS.md`（自动注入、权威）；踩坑案例在 `~/.agent-lessons/`（按需 `read`，不要全读）。

## 项目前提（实测）

- Windows 10/11 x64 工具：全局键盘/鼠标低层钩子（`WH_KEYBOARD_LL` / `WH_MOUSE_LL`）切换中英文输入法，托盘常驻、单实例。
- C# / .NET 9 + NativeAOT，**纯 Win32 自绘**（不用 WinForms/WPF，二者都不支持 NativeAOT），
  构建：`dotnet publish -c Release -r win-x64 --self-contained true`，产物约 3MB 单文件。
- **需要管理员权限**（低层钩子），启动会请求 UAC；开了开机自启后每次登录必定弹一次，这是系统行为。
- 界面字体：思源黑体（Source Han Sans SC）为主，未安装时回退微软雅黑。
- 日志：`run.log`（每次启动清空，崩溃转存 `crash_run_*.log` + `crash_*.txt`）；
  热键解析/比较逻辑有内置自检 `IME_Switcher.exe --selftest`（20 条断言，退出码 = 失败条数）。
- 许可证 MIT；Python + pynput 的旧实现保留在 `python-legacy` 分支，已废弃不再维护。

## README 与素材规范（2026-09-13）

- README 头部按 nonebot2 三段式（全局铁律 24）：logo → `# 名字` + 斜体一句话 → 徽章一行，三块都居中。
- **logo 就用用户自制的 `icon512x.png`**（512×512，白底不透明），显示 200×200。
  源图是 512，所以高分屏也不糊；放大别超过源图的一半。
- ⚠️ **不要再做"透明底 + 深浅双版本"**（2026-09-13 做过一次，用户判定效果差、已回退）：
  这张是黑字设计 —— 把白底抠成透明后，深色主题下黑字几乎看不见；配 `<picture>` + `prefers-color-scheme`
  又要长期维护两个文件。当时生成的 `icon512-transparent.png` / `icon512-white.png` 已从仓库删除，
  要看那次的实现就 `git show 3473e47`。**结论：原图直接用，别再抠底。**
- 素材与用途：

  | 文件 | 用途 |
  |---|---|
  | `icon512x.png` | README 头部 logo（512×512，白底不透明） |
  | `icon.ico` | 应用图标（`.csproj` 的 `ApplicationIcon` 引用，128×128） |
  | `icon*.psd` | 设计稿，**不入库**（`.gitignore` 已忽略 `*.psd`） |

- 换 logo：直接替换 `icon512x.png`（保持 512×512）即可，README 不用动；必要时看一眼深色主题下的观感。
- 加文件只 `git add` 具体路径，**别用 `git add -A`** —— 本仓库有过把不该提交的文件扫进去的先例。

## 记忆路径

- 过程记忆：`~/.dsh/memory/easyIME/` —— 新会话先读 `project_memory.md` 与最近日期的 `topics.md`，收尾回写。
