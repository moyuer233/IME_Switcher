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

## README 与素材规范（2026-09-13 用户要求）

- README 头部按 nonebot2 三段式（全局铁律 24）：logo → `# 名字` + 斜体一句话 → 徽章一行，三块都居中。
- **logo 必须透明底，且分深浅两版**：这张图是黑字设计，深色主题下黑字看不见，而不透明的白底方块在深色下又刺眼。
  用 GitHub 支持的 `<picture>` + `prefers-color-scheme` 切换：

  ```html
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="icon512-white.png">
    <img src="icon512-transparent.png" width="200" height="200" alt="IME Switcher">
  </picture>
  ```

- 仓库根目录现有素材与用途：

  | 文件 | 用途 |
  |---|---|
  | `icon512x.png` | 原始源图（512×512，白底不透明，只作生成素材，README 不引用） |
  | `icon512-transparent.png` | 抠掉白底的黑字版，浅色主题用 |
  | `icon512-white.png` | 同形状白字版，深色主题用 |
  | `icon.ico` | 应用图标（`.csproj` 的 `ApplicationIcon` 引用，128×128） |

- 重新生成的步骤（不引入新依赖，用系统自带 `System.Drawing`；改完图要重跑并肉眼验两种主题）：

  1. 抠底：逐像素 `alpha = round((255 - 感知亮度) × 原alpha / 255)`，RGB 一律置 0 —— 这样字形的抗锯齿边会转成半透明，白底变成全透明。
  2. 白字版：在同一张结果上把 RGB 置 255、保留 alpha。
  3. 验证：把结果分别叠到 `#0d1117`（GitHub 深色底）与 `#ffffff` 上各看一遍。
- 显示尺寸统一 **200×200**（nonebot2 同规格）；源图 512 是为了高 DPI 不糊，别再放大超过源图 1/2。
- **`*.psd` 设计稿不入库**：体积大、二进制、改一行看不出差异；本仓库有过 `git add -A` 扫进不该提交文件的先例，加文件只 `git add` 具体路径。

## 记忆路径

- 过程记忆：`~/.dsh/memory/easyIME/` —— 新会话先读 `project_memory.md` 与最近日期的 `topics.md`，收尾回写。
