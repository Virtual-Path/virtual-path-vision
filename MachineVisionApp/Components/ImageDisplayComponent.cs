using System.Windows.Controls;
using OpenCvSharp;

namespace MachineVisionApp.Components
{
    /// <summary>
    /// 图像显示组件，负责将 OpenCV Mat 帧更新到 WPF Image 控件。
    /// 使用 Dispatcher.Invoke 确保跨线程更新 UI 的安全性。
    /// </summary>
    public class ImageDisplayComponent
    {
        private readonly Image _originalImage; // 原始视频流显示控件
        private readonly Image _edgeImage;     // 边缘检测结果显示控件

        /// <summary>
        /// 初始化图像显示组件。
        /// </summary>
        /// <param name="originalImage">原始图像控件</param>
        /// <param name="edgeImage">边缘图像控件</param>
        public ImageDisplayComponent(Image originalImage, Image edgeImage)
        {
            _originalImage = originalImage;
            _edgeImage = edgeImage;
        }

        /// <summary>
        /// 将原始帧和边缘检测结果同时更新到 UI（单次 Dispatcher 调用）。
        /// </summary>
        /// <param name="originalFrame">原始彩色帧</param>
        /// <param name="edges">边缘检测结果</param>
        /// <param name="threshold1">当前 Canny 低阈值（预留）</param>
        /// <param name="threshold2">当前 Canny 高阈值（预留）</param>
        public void UpdateImages(Mat originalFrame, Mat edges, int threshold1, int threshold2)
        {
            _originalImage.Dispatcher.Invoke(() =>
            {
                _originalImage.Source = DpiAwareBitmapSource.FromMat(originalFrame);
                _edgeImage.Source = DpiAwareBitmapSource.FromMat(edges);
            });
        }
    }
}
