using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zentwik.UI.Services;

/// <summary>托盘图标渲染器：矢量按目标物理像素渲染成 HICON，避开 .ico 缩放的黑边。</summary>
internal static class TrayIconRenderer
{
    /// <summary>渲染托盘图标</summary>
    public static IntPtr CreateHIcon(Drawing drawing, int pixelSize)
    {
        if (drawing is null || pixelSize <= 0)
        {
            System.Diagnostics.Debug.WriteLine("[TrayIconRenderer] 早退：drawing 为空或尺寸非法");
            return IntPtr.Zero;
        }

        try
        {
            return CreateHIconCore(drawing, pixelSize);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[TrayIconRenderer] 异常：" + ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>把矢量图标渲染成指定**物理**像素边长的位图</summary>
    public static RenderTargetBitmap RenderDrawing(Drawing drawing, int pixelSize)
    {
        // 用 Pbgra32（预乘 alpha，内存序 BGRA）。
        var rtb = new RenderTargetBitmap(
            pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 背景全透明，只画图标本身
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, pixelSize, pixelSize));

            // 图标几何坐标在 1000×1000 网格里，等比缩放到目标像素并居中
            var bounds = drawing.Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                double scale = Math.Min(pixelSize / bounds.Width, pixelSize / bounds.Height);
                double w = bounds.Width * scale;
                double h = bounds.Height * scale;
                double ox = (pixelSize - w) / 2 - bounds.X * scale;
                double oy = (pixelSize - h) / 2 - bounds.Y * scale;

                dc.PushTransform(new TranslateTransform(ox, oy));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawDrawing(drawing);
                dc.Pop();
                dc.Pop();
            }
        }
        rtb.Render(visual);

        return rtb;
    }

    private static IntPtr CreateHIconCore(Drawing drawing, int pixelSize)
    {
        var rtb = RenderDrawing(drawing, pixelSize);

        // 2. 建 DIB 段，把渲染结果拷进去
        IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return IntPtr.Zero;

        IntPtr hIcon = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hMask = IntPtr.Zero;

        try
        {
            var bmi = new NativeMethods.BITMAPINFO
            {
                biSize = 40,
                biWidth = pixelSize,
                biHeight = -pixelSize,   // 负值 = 自上而下
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            };

            IntPtr bits;
            hBitmap = NativeMethods.CreateDIBSection(
                hdc, ref bmi, NativeMethods.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);

            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TrayIconRenderer] CreateDIBSection 失败 err={Marshal.GetLastWin32Error()}");
                return IntPtr.Zero;
            }

            var block = new byte[pixelSize * pixelSize * 4];
            rtb.CopyPixels(block, pixelSize * 4, 0);
            Marshal.Copy(block, 0, bits, block.Length);

            // 3. 掩码位图
            // 32bpp 图标自带 alpha 通道，Windows 合成时以 alpha 为准，
            // 掩码只是协议上要求存在的合法位图 —— 全 0 即可。
            hMask = NativeMethods.CreateBitmap(
                pixelSize, pixelSize, 1, 1, IntPtr.Zero);

            if (hMask == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TrayIconRenderer] CreateBitmap(掩码) 失败 err={Marshal.GetLastWin32Error()}");
                return IntPtr.Zero;
            }

            // 4. 组装 ICONINFO
            var ii = new NativeMethods.ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = hMask,
                hbmColor = hBitmap,
            };

            hIcon = NativeMethods.CreateIconIndirect(ref ii);

            if (hIcon == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[TrayIconRenderer] CreateIconIndirect 失败 err={Marshal.GetLastWin32Error()}");
            }

        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, hdc);

            // CreateIconIndirect 只复制位图，原始 GDI 句柄必须由本方法释放。
            // 每次换主题 / DPI 变化都会重画，漏一对就是两个永久占用的句柄。
            if (hMask != IntPtr.Zero)
                NativeMethods.DeleteObject(hMask);
            if (hBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(hBitmap);
        }

        return hIcon;
    }
}
