using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// DPI 感知的图像转换辅助类。
    /// 确保 OpenCV Mat 转换为 WPF BitmapSource 时使用屏幕 DPI，
    /// 避免在高 DPI 屏幕（2K/4K）上图像模糊。
    /// </summary>
    public static class DpiAwareBitmapSource
    {
        private static double _cachedDpi = 0;

        /// <summary>
        /// 获取当前屏幕 DPI（缓存，避免重复调用）。
        /// </summary>
        private static double GetScreenDpi()
        {
            if (_cachedDpi > 0) return _cachedDpi;

            try
            {
                var source = PresentationSource.FromVisual(Application.Current.MainWindow);
                if (source?.CompositionTarget != null)
                {
                    _cachedDpi = 96.0 * source.CompositionTarget.TransformToDevice.M11;
                    return _cachedDpi;
                }
            }
            catch { }

            _cachedDpi = 96.0;
            return _cachedDpi;
        }

        /// <summary>
        /// 将 Mat 转换为 BitmapSource，并设置为屏幕 DPI。
        /// </summary>
        public static BitmapSource FromMat(Mat mat)
        {
            // 先用 OpenCvSharp 转换为 BitmapSource（96 DPI）
            var bs = OpenCvSharp.WpfExtensions.BitmapSourceConverter.ToBitmapSource(mat);

            double dpi = GetScreenDpi();

            // 如果 DPI 就是 96，直接返回（无需重建）
            if (dpi <= 96.0) return bs;

            // 复制像素数据
            int stride = bs.PixelWidth * (bs.Format.BitsPerPixel / 8);
            byte[] pixels = new byte[stride * bs.PixelHeight];
            bs.CopyPixels(pixels, stride, 0);

            // 用正确的 DPI 重新创建 BitmapSource
            return BitmapSource.Create(
                bs.PixelWidth,
                bs.PixelHeight,
                dpi,
                dpi,
                bs.Format,
                bs.Palette,
                pixels,
                stride);
        }
    }
}
