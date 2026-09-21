using System.Drawing;

namespace IMESwitcher;

/// <summary>
/// 主题配色。视觉规范参考 Plain Craft Launcher（PCL，作者 龙腾猫跃）：
/// 主色是 HSL(210, 85, L) 生成的一套浓度梯度，正文不用纯黑，层级靠字重与颜色区分而不是放大字号。
/// 仅参考设计参数，无代码派生。
/// </summary>
public static class Theme
{
    // ---- 主题色浓度梯度（HSL 210 / 85，浓度 1~8）----
    public static readonly Color AccentInk = Color.FromArgb(0x34, 0x3D, 0x4A);     // 浓度1：正文 / 次按钮描边
    public static readonly Color Accent = Color.FromArgb(0x0B, 0x5B, 0xCB);        // 浓度2：主色 / 主按钮描边
    public static readonly Color AccentHover = Color.FromArgb(0x13, 0x70, 0xF3);   // 浓度3：hover / 强调
    public static readonly Color AccentTitle = Color.FromArgb(0x48, 0x90, 0xF5);   // 浓度4：标题栏渐变端
    public static readonly Color AccentSoft = Color.FromArgb(0xD5, 0xE6, 0xFD);    // 浓度6：按下底
    public static readonly Color AccentHoverBg = Color.FromArgb(0xE0, 0xEA, 0xFD); // 浓度7：hover 底
    public static readonly Color AccentFaint = Color.FromArgb(0xEA, 0xF2, 0xFE);   // 浓度8：极浅底
    public static readonly Color TitleBarL = Color.FromArgb(0x0D, 0x61, 0xD7);     // 标题栏渐变端（HSL 210/85/48）
    public static readonly Color TitleBarM = Color.FromArgb(0x12, 0x6E, 0xEF);     // 标题栏渐变中（HSL 210/85/54）

    // ---- 页面 / 文字（PCL 灰阶）----
    public static readonly Color Bg = Color.FromArgb(0xF5, 0xF5, 0xF5);            // 灰8：页面背景
    public static readonly Color Card = Color.White;                               // 卡片底
    public static readonly Color BgSubtle = Color.FromArgb(0xF0, 0xF0, 0xF0);      // 灰7：控件浅底
    public static readonly Color Border = Color.FromArgb(0xEB, 0xEB, 0xEB);        // 灰6
    public static readonly Color BorderMuted = Color.FromArgb(0xF0, 0xF0, 0xF0);   // 灰7
    public static readonly Color BorderInput = Color.FromArgb(0xCC, 0xCC, 0xCC);   // 灰5：输入框描边
    public static readonly Color Text = Color.FromArgb(0x34, 0x3D, 0x4A);          // 浓度1（不是纯黑）
    public static readonly Color TextMuted = Color.FromArgb(0x73, 0x73, 0x73);     // 灰2
    public static readonly Color TextFaint = Color.FromArgb(0x8C, 0x8C, 0x8C);     // 灰3
    public static readonly Color TextDisabled = Color.FromArgb(0xA6, 0xA6, 0xA6);  // 灰4

    // ---- 状态色 ----
    public static readonly Color Danger = Color.FromArgb(0xCE, 0x21, 0x11);
    public static readonly Color DangerHover = Color.FromArgb(0xFF, 0x4C, 0x4C);
    public static readonly Color DangerSoftBg = Color.FromArgb(0xFB, 0xDD, 0xDD);
    public static readonly Color Success = Color.FromArgb(0x2E, 0xA0, 0x43);
    public static readonly Color SuccessSoftBg = Color.FromArgb(0xE6, 0xF4, 0xEA);

    // ---- 控件 ----
    public static readonly Color FieldBg = Color.White;                            // 输入框底（PCL 输入框是白底 + 描边）
    public static readonly Color TrackOff = Color.FromArgb(0xCC, 0xCC, 0xCC);       // 灰5：开关未开
    public static readonly Color TrackOffHover = Color.FromArgb(0xA6, 0xA6, 0xA6);  // 灰4
    public static readonly Color ShadowInk = Color.FromArgb(0x34, 0x3D, 0x4A);      // 卡片阴影色（PCL 同款）

    // ---- 旧名保留（值已按 PCL 规范对齐，避免遗漏引用点）----
    public static readonly Color BtnBorder = Color.FromArgb(0x34, 0x3D, 0x4A);
    public static readonly Color BtnHoverBg = Color.FromArgb(0xE0, 0xEA, 0xFD);
    public static readonly Color BtnPressedBg = Color.FromArgb(0xD5, 0xE6, 0xFD);
    public static readonly Color BtnDisabledBg = Color.FromArgb(0xF0, 0xF0, 0xF0);
    public static readonly Color BtnDisabledFg = Color.FromArgb(0xA6, 0xA6, 0xA6);
    public static readonly Color AccentPressed = Color.FromArgb(0x0B, 0x5B, 0xCB);
    public static readonly Color DangerSoftPressed = Color.FromArgb(0xFB, 0xDD, 0xDD);
    public static readonly Color SoftBlueBg = Color.FromArgb(0xD5, 0xE6, 0xFD);
    public static readonly Color SoftBlueFg = Color.FromArgb(0x0B, 0x5B, 0xCB);

    public const int Radius = 5;      // 卡片圆角（PCL）
    public const int RadiusBtn = 3;   // 按钮圆角（PCL）
    public const int RadiusPill = 14; // 胶囊圆角（高 27 → 13.5，取 14）
}
