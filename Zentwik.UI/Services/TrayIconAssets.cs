using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zentwik.UI.Services;

/// <summary>
/// 托盘图标的位图包生成器。后台服务是 Native AOT，没有 WPF 渲染路径，
/// 所以两档灰阶在这里按物理像素渲染好，随程序集内嵌给 Worker。
/// </summary>
/// <remarks>
/// 包格式：magic "ZWT1" | int32 档数 | 每档 [int32 宽][int32 高][BGRA 像素]。
/// 像素是非预乘 alpha —— CreateDIBSection 的 32bpp 位图按直通 alpha 合成，
/// 直接塞预乘数据会让边缘发暗。
/// </remarks>
internal static class TrayIconAssets
{
    private const uint Magic = 0x3154575A;

    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48 };

    /// <summary>生成深色任务栏与浅色任务栏两套，返回两个文件路径。</summary>
    public static (string Dark, string Light) Write(string directory)
    {
        Directory.CreateDirectory(directory);

        var theme = AppThemeService.Instance;

        var dark = LogoPaletteFactory.BuildForTray(darkTaskbar: true);
        var light = LogoPaletteFactory.BuildForTray(darkTaskbar: false);

        string darkPath = Path.Combine(directory, "tray-on-dark.bin");
        string lightPath = Path.Combine(directory, "tray-on-light.bin");

        WritePackage(darkPath, theme.BuildLogoDrawing(dark.Body, dark.Shadow));
        WritePackage(lightPath, theme.BuildLogoDrawing(light.Body, light.Shadow));

        return (darkPath, lightPath);
    }

    private static void WritePackage(string path, Drawing drawing)
    {
        List<byte[]> frames = new(Sizes.Length);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(Magic);
        writer.Write(Sizes.Length);

        foreach (int size in Sizes)
        {
            byte[] pixels = RenderStraightAlpha(drawing, size);
            writer.Write(size);
            writer.Write(size);
            writer.Write(pixels);
            frames.Add(pixels);
        }

        writer.Flush();
        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>渲染成 BGRA 直通 alpha（WPF 输出的是预乘，这里反算回去）。</summary>
    private static byte[] RenderStraightAlpha(Drawing drawing, int size)
    {
        RenderTargetBitmap bitmap = TrayIconRenderer.RenderDrawing(drawing, size);

        int stride = size * 4;
        byte[] block = new byte[stride * size];
        bitmap.CopyPixels(block, stride, 0);

        for (int i = 0; i < block.Length; i += 4)
        {
            byte a = block[i + 3];
            if (a == 0 || a == 255) continue;

            block[i] = Unpremultiply(block[i], a);
            block[i + 1] = Unpremultiply(block[i + 1], a);
            block[i + 2] = Unpremultiply(block[i + 2], a);
        }

        return block;
    }

    private static byte Unpremultiply(byte value, byte alpha)
    {
        int v = value * 255 / alpha;
        return v > 255 ? (byte)255 : (byte)v;
    }
}
