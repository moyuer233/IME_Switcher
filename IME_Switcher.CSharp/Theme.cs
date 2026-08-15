using System.Drawing;

namespace IMESwitcher;

/// <summary>PCL 风格主题配色：浅灰页面 + 白色圆角卡片 + 蓝色主色</summary>
public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0xF2, 0xF3, 0xF5);      // 页面背景（浅灰）
    public static readonly Color Card = Color.White;                          // 卡片背景（白）
    public static readonly Color BgSubtle = Color.FromArgb(0xF6, 0xF7, 0xF9); // 控件浅底
    public static readonly Color Border = Color.FromArgb(0xE1, 0xE3, 0xE6);
    public static readonly Color BorderMuted = Color.FromArgb(0xE8, 0xEA, 0xED);
    public static readonly Color Text = Color.FromArgb(0x1A, 0x1D, 0x21);
    public static readonly Color TextMuted = Color.FromArgb(0x55, 0x5B, 0x63);
    public static readonly Color Accent = Color.FromArgb(0x0A, 0x84, 0xFF);  // PCL 蓝
    public static readonly Color AccentHover = Color.FromArgb(0x0A, 0x78, 0xE8);
    public static readonly Color Danger = Color.FromArgb(0xE5, 0x4D, 0x42);
    public static readonly Color DangerHover = Color.FromArgb(0xCF, 0x42, 0x37);
    public static readonly Color Success = Color.FromArgb(0x2E, 0xA0, 0x43);
    public static readonly Color TrackOff = Color.FromArgb(0xD9, 0xDC, 0xE0); // 开关未开轨道
    public const int Radius = 8;
}
