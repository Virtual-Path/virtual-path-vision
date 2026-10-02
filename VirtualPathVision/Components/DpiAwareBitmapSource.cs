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
        private static double _cachedDpi;
        private static IntPtr _cachedSource = IntPtr.Zero;

        /// <summary>
        /// 获取当前屏幕 DPI。
        ///
        /// 缓存是按 PresentationSource（显示器）而非按进程缓存的：
        /// 旧实现只算一次就永久缓存，把窗口拖到缩放比例不同的显示器上后，
        /// 所有画面仍按旧 DPI 渲染。这里在 PresentationSource 变化时自动重算。
        /// </summary>
        private static double GetScreenDpi()
        {
            try
            {
                var app = Application.Current;
                var window = app?.MainWindow;
                var source = window != null ? PresentationSource.FromVisual(window) : null;

                if (source?.CompositionTarget != null)
                {
                    // 句柄在 HwndSource 上，不在 CompositionTarget 上
                    var handle = (source as System.Windows.Interop.HwndSource)?.Handle ?? IntPtr.Zero;
                    var dpi = 96.0 * source.CompositionTarget.TransformToDevice.M11;

                    if (dpi > 0 && (handle != _cachedSource || dpi != _cachedDpi))
                    {
                        _cachedDpi = dpi;
                        _cachedSource = handle;
                    }
                    return _cachedDpi;
                }
            }
            catch { }

            return _cachedDpi > 0 ? _cachedDpi : 96.0;
        }

        /// <summary>
        /// 将 Mat 转换为 BitmapSource，并设置为屏幕 DPI。
        /// </summary>
        public static BitmapSource FromMat(Mat mat)
        {
            if (mat == null || mat.Empty())
                return null!;

            // 先用 OpenCvSharp 转换为 BitmapSource（96 DPI）
            var bs = OpenCvSharp.WpfExtensions.BitmapSourceConverter.ToBitmapSource(mat);

            double dpi = GetScreenDpi();

            // 已经是目标 DPI，无需重建（省掉一次整帧像素拷贝）
            if (Math.Abs(dpi - 96.0) < 0.5) return bs;

            // 复制像素数据
            int bytesPerPixel = bs.Format.BitsPerPixel / 8;
            if (bytesPerPixel <= 0)
                return bs; // 非字节对齐的像素格式，stride 无法正确计算，直接返回原图

            int stride = bs.PixelWidth * bytesPerPixel;
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
