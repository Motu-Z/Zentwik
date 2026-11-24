using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Zentwik.UI.Services
{
    /// <summary>
    /// 托盘图标的位图包与离屏渲染。后台服务是 Native AOT，没有 WPF 渲染路径，
    /// 两档灰阶在这里按物理像素渲染好，随程序集内嵌给 Worker。
    /// </summary>
    /// <remarks>
    /// 包格式：magic "ZWT1" | int32 档数 | 每档 [int32 宽][int32 高][BGRA 直通 alpha 像素]。
    /// 直接塞预乘 alpha 会让 CreateDIBSection 合成后边缘发暗。
    /// </remarks>
    internal static class TrayIconAssets
    {
        private const uint Magic = 0x3154575A;

        private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48 };

        /// <summary>生成深色任务栏与浅色任务栏两套，返回两个文件路径。</summary>
        public static (string Dark, string Light) Write(string directory)
        {
            Directory.CreateDirectory(directory);

            AppThemeService theme = AppThemeService.Instance;
            var dark = LogoPaletteFactory.BuildForTray(darkTaskbar: true);
            var light = LogoPaletteFactory.BuildForTray(darkTaskbar: false);

            string darkPath = Path.Combine(directory, "tray-on-dark.bin");
            string lightPath = Path.Combine(directory, "tray-on-light.bin");

            WritePackage(darkPath, theme.BuildLogoDrawing(dark.Body, dark.Shadow));
            WritePackage(lightPath, theme.BuildLogoDrawing(light.Body, light.Shadow));

            return (darkPath, lightPath);
        }

        /// <summary>把矢量图标渲染成指定物理像素边长的位图。</summary>
        public static RenderTargetBitmap RenderDrawing(Drawing drawing, int pixelSize)
        {
            RenderTargetBitmap bitmap = new(pixelSize, pixelSize, 96, 96, PixelFormats.Pbgra32);

            DrawingVisual visual = new();

            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, pixelSize, pixelSize));

                Rect bounds = drawing.Bounds;

                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    // 几何坐标在 1000×1000 网格里，等比缩放到目标像素并居中。
                    double scale = Math.Min(pixelSize / bounds.Width, pixelSize / bounds.Height);

                    dc.PushTransform(new TranslateTransform(
                        (pixelSize - bounds.Width * scale) / 2 - bounds.X * scale,
                        (pixelSize - bounds.Height * scale) / 2 - bounds.Y * scale));
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawDrawing(drawing);
                    dc.Pop();
                    dc.Pop();
                }
            }

            bitmap.Render(visual);
            return bitmap;
        }

        private static void WritePackage(string path, Drawing drawing)
        {
            using FileStream stream = File.Create(path);
            using BinaryWriter writer = new(stream);

            writer.Write(Magic);
            writer.Write(Sizes.Length);

            foreach (int size in Sizes)
            {
                writer.Write(size);
                writer.Write(size);
                writer.Write(RenderStraightAlpha(drawing, size));
            }
        }

        /// <summary>渲染成 BGRA 直通 alpha（WPF 输出的是预乘，这里反算回去）。</summary>
        private static byte[] RenderStraightAlpha(Drawing drawing, int size)
        {
            int stride = size * 4;
            byte[] block = new byte[stride * size];

            RenderDrawing(drawing, size).CopyPixels(block, stride, 0);

            for (int i = 0; i < block.Length; i += 4)
            {
                byte alpha = block[i + 3];

                if (alpha == 0 || alpha == 255)
                {
                    continue;
                }

                block[i] = Unpremultiply(block[i], alpha);
                block[i + 1] = Unpremultiply(block[i + 1], alpha);
                block[i + 2] = Unpremultiply(block[i + 2], alpha);
            }

            return block;
        }

        private static byte Unpremultiply(byte value, byte alpha)
        {
            int scaled = value * 255 / alpha;
            return scaled > 255 ? (byte)255 : (byte)scaled;
        }
    }
}
