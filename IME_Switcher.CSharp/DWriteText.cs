using System.Drawing;
using System.Runtime.InteropServices;

namespace IMESwitcher;

/// <summary>
/// DirectWrite + Direct2D 文字渲染：与 WPF 使用同一渲染引擎（ClearType/抗锯齿），
/// 渲染质量远超 GDI/GDI+。纯 COM 调用，NativeAOT 安全。
/// 使用 DC 渲染目标绑定到窗口/内存 DC，与现有 GDI 自绘管线无缝配合。
/// </summary>
internal static class DWriteText
{
    private static readonly object Gate = new();
    private static IDWriteFactory? _dw;
    private static ID2D1DCRenderTarget? _rt;
    private static readonly Dictionary<(string, float, bool), IDWriteTextFormat> Formats = new();
    private static bool _failed;

    public static bool TryDraw(IntPtr hdc, string text, NativeMethods.RECT rect, Color color,
        string face, float size, bool bold, uint align)
    {
        try
        {
            if (string.IsNullOrEmpty(text)) return true;
            var rt = EnsureReady();
            if (rt == null) return false;

            var fmt = GetFormat(face, size, bold);
            fmt.SetTextAlignment(align == 1 ? 2 : align == 2 ? 1 : 0); // 左/中/右
            fmt.SetParagraphAlignment(2); // 垂直居中

            var colorF = new D2D1_COLOR_F
            {
                r = color.R / 255f, g = color.G / 255f, b = color.B / 255f, a = 1f,
            };
            rt.BindDC(hdc, ref rect);
            rt.BeginDraw();
            rt.CreateSolidColorBrush(ref colorF, IntPtr.Zero, out var brush);
            var lr = new D2D1_RECT_F { left = rect.left, top = rect.top, right = rect.right, bottom = rect.bottom };
            var fmtPtr = Marshal.GetIUnknownForObject(fmt);
            try
            {
                rt.DrawText(text, (uint)text.Length, fmtPtr, ref lr, brush, 0, 0);
            }
            finally
            {
                Marshal.Release(fmtPtr);
            }
            rt.EndDraw(IntPtr.Zero, IntPtr.Zero);
            if (brush != IntPtr.Zero) Marshal.Release(brush);
            return true;
        }
        catch
        {
            return false; // 回退由调用方处理
        }
    }

    private static ID2D1DCRenderTarget? EnsureReady()
    {
        if (_rt != null || _failed) return _rt;
        lock (Gate)
        {
            if (_rt != null || _failed) return _rt;
            try
            {
                var iidDWrite = typeof(IDWriteFactory).GUID;
                DWriteCreateFactory(0, ref iidDWrite, out var dw);
                _dw = dw;

                var iidD2D = typeof(ID2D1Factory).GUID;
                D2D1CreateFactory(0, ref iidD2D, IntPtr.Zero, out var d2d);
                var props = new D2D1_RENDER_TARGET_PROPERTIES
                {
                    type = 0,
                    pixelFormat = new D2D1_PIXEL_FORMAT { format = 87, alphaMode = 0 }, // B8G8R8A8 + IGNORE
                    dpiX = 96,
                    dpiY = 96,
                };
                d2d.CreateDCRenderTarget(ref props, out var rt);
                _rt = rt;
                _rt.SetTextAntialiasMode(1); // D2D1_TEXT_ANTIALIAS_MODE_CLEARTYPE（PCL/WPF 同款）
            }
            catch
            {
                _failed = true;
            }
            return _rt;
        }
    }

    private static IDWriteTextFormat GetFormat(string face, float size, bool bold)
    {
        var key = (face, size, bold);
        if (Formats.TryGetValue(key, out var f)) return f;
        _dw!.CreateTextFormat(face, IntPtr.Zero, bold ? 700 : 400, 0, 5, size, "zh-cn", out var fmt);
        Formats[key] = fmt;
        return fmt;
    }

    // ================= COM 接口声明（vtable 顺序，与 Windows SDK 一致） =================

    [DllImport("dwrite.dll")]
    private static extern int DWriteCreateFactory(int factoryType, ref Guid iid, out IDWriteFactory factory);

    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(int factoryType, ref Guid iid, IntPtr factoryOptions, out ID2D1Factory factory);

    [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory
    {
        [PreserveSig] int GetSystemFontCollection(IntPtr a, int b, int c);
        [PreserveSig] int CreateCustomFontCollection(IntPtr a, IntPtr b, int c, IntPtr d);
        [PreserveSig] int RegisterFontCollectionLoader(IntPtr a);
        [PreserveSig] int UnregisterFontCollectionLoader(IntPtr a);
        [PreserveSig] int CreateFontFileReference(string a, IntPtr b, int c, IntPtr d);
        [PreserveSig] int CreateCustomFontFileReference(IntPtr a, int b, int c, IntPtr d);
        [PreserveSig] int CreateFontFace(int a, IntPtr b, int c, int d, IntPtr e);
        [PreserveSig] int CreateRenderingParams(IntPtr a);
        [PreserveSig] int CreateMonitorRenderingParams(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateCustomRenderingParams(float a, float b, float c, float d, int e, int f, IntPtr g);
        [PreserveSig] int RegisterFontFileLoader(IntPtr a);
        [PreserveSig] int UnregisterFontFileLoader(IntPtr a);
        [PreserveSig] int CreateTextFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string fontFamilyName,
            IntPtr fontCollection, int fontWeight, int fontStyle, int fontStretch,
            float fontSize, [MarshalAs(UnmanagedType.LPWStr)] string localeName,
            out IDWriteTextFormat textFormat);
    }

    [ComImport, Guid("9c9064b6-1366-4c9c-9e6b-2d96b73f9c9a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteTextFormat
    {
        [PreserveSig] int GetFontFamilyName(IntPtr a, int b);
        [PreserveSig] int GetFontFamilyNameLength();
        [PreserveSig] int GetFontWeight();
        [PreserveSig] int GetFontStyle();
        [PreserveSig] int GetFontStretch();
        [PreserveSig] float GetFontSize();
        [PreserveSig] int GetLocaleName(IntPtr a, int b);
        [PreserveSig] int GetLocaleNameLength();
        [PreserveSig] int SetTextAlignment(int textAlignment);
        [PreserveSig] int GetTextAlignment();
        [PreserveSig] int SetParagraphAlignment(int paragraphAlignment);
    }

    [ComImport, Guid("06152247-6f50-465a-9245-118bfd3b6007"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID2D1Factory
    {
        [PreserveSig] int ReloadSystemMetrics();
        [PreserveSig] int GetDesktopDpi(out float a, out float b);
        [PreserveSig] int CreateRectangleGeometry(IntPtr a, IntPtr b);
        [PreserveSig] int CreateRoundedRectangleGeometry(IntPtr a, IntPtr b);
        [PreserveSig] int CreateEllipseGeometry(IntPtr a, IntPtr b);
        [PreserveSig] int CreateGeometryGroup(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int CreateTransformedGeometry(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreatePathGeometry(IntPtr a);
        [PreserveSig] int CreateGeometryFromSink(IntPtr a, IntPtr b);
        [PreserveSig] int CreateStrokeStyle(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateDrawingStateBlock(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateWicBitmapRenderTarget(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateHwndRenderTarget(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateDxgiSurfaceRenderTarget(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateDCRenderTarget(ref D2D1_RENDER_TARGET_PROPERTIES properties, out ID2D1DCRenderTarget renderTarget);
    }

    [ComImport, Guid("2cd906a2-12e2-11dc-9fed-001143a055f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID2D1RenderTarget
    {
        [PreserveSig] int BeginDraw();
        [PreserveSig] int EndDraw(IntPtr a, IntPtr b);
        [PreserveSig] void Clear(IntPtr a);
        [PreserveSig] void SetTransform(IntPtr a);
        [PreserveSig] void GetTransform(IntPtr a);
        [PreserveSig] void SetAntialiasMode(int a);
        [PreserveSig] int GetAntialiasMode();
        [PreserveSig] void SetTextAntialiasMode(int a);
        [PreserveSig] int GetTextAntialiasMode();
        [PreserveSig] void SetTextRenderingParams(IntPtr a);
        [PreserveSig] void GetTextRenderingParams(IntPtr a);
        [PreserveSig] void SetTags(int a, int b);
        [PreserveSig] void GetTags(IntPtr a, IntPtr b);
        [PreserveSig] void SetViewport(IntPtr a);
        [PreserveSig] void GetViewport(IntPtr a);
        [PreserveSig] void SetPixelSize(int a, int b);
        [PreserveSig] void GetPixelSize(IntPtr a);
        [PreserveSig] void SetDpi(float a, float b);
        [PreserveSig] void GetDpi(IntPtr a, IntPtr b);
        [PreserveSig] void GetSize(IntPtr a);
        [PreserveSig] int GetMaximumBitmapSize();
        [PreserveSig] int IsSupported(IntPtr a);
        [PreserveSig] int CreateBitmap(int a, IntPtr b, int c, IntPtr d);
        [PreserveSig] int CreateBitmapFromWicBitmap(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateSharedBitmap(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateBitmapBrush(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int CreateSolidColorBrush(ref D2D1_COLOR_F color, IntPtr properties, out IntPtr brush);
        [PreserveSig] int CreateGradientStopCollection(IntPtr a, int b, int c, IntPtr d);
        [PreserveSig] int CreateLinearGradientBrush(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateRadialGradientBrush(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateCompatibleRenderTarget(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int CreateLayer(IntPtr a, IntPtr b);
        [PreserveSig] int CreateMesh(IntPtr a);
        [PreserveSig] void DrawLine(IntPtr a, IntPtr b, IntPtr c, float d, IntPtr e);
        [PreserveSig] void DrawRectangle(IntPtr a, IntPtr b, float c, IntPtr d);
        [PreserveSig] void FillRectangle(IntPtr a, IntPtr b);
        [PreserveSig] void DrawRoundedRectangle(IntPtr a, IntPtr b, float c, IntPtr d);
        [PreserveSig] void FillRoundedRectangle(IntPtr a, IntPtr b);
        [PreserveSig] void DrawEllipse(IntPtr a, IntPtr b, float c, IntPtr d);
        [PreserveSig] void FillEllipse(IntPtr a, IntPtr b);
        [PreserveSig] void DrawGeometry(IntPtr a, IntPtr b, float c, IntPtr d);
        [PreserveSig] void FillGeometry(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] void FillMesh(IntPtr a, IntPtr b);
        [PreserveSig] void FillOpacityMask(IntPtr a, IntPtr b, int c, IntPtr d);
        [PreserveSig] void DrawBitmap(IntPtr a, IntPtr b, float c, int d, IntPtr e);
        [PreserveSig] void DrawText([MarshalAs(UnmanagedType.LPWStr)] string text, uint stringLength,
            IntPtr textFormat, ref D2D1_RECT_F layoutRect, IntPtr brush, uint options, int measuringMode);
    }

    [ComImport, Guid("c095b945-bbcd-43d7-8470-6702e1c79e17"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID2D1DCRenderTarget : ID2D1RenderTarget
    {
        [PreserveSig] int BindDC(IntPtr hdc, ref NativeMethods.RECT rect);
    }

    // ================= 结构 =================

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_RENDER_TARGET_PROPERTIES
    {
        public int type;
        public D2D1_PIXEL_FORMAT pixelFormat;
        public float dpiX;
        public float dpiY;
        public int usage;
        public int minLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_PIXEL_FORMAT
    {
        public int format;
        public int alphaMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_RECT_F
    {
        public float left;
        public float top;
        public float right;
        public float bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D2D1_COLOR_F
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }
}
