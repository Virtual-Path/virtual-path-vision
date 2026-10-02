using System;
using OpenCvSharp;
using ZXing;
using ZXing.Common;

namespace VirtualPathVision.Components
{
    /// <summary>
    /// 二维码/条形码识别组件，基于 ZXing.Net 实现。
    /// 支持 QR Code、DataMatrix、EAN/UPC、Code128、Code39、ITF 等常见码制。
    /// 采用帧节流策略：每隔若干帧识别一次，结果缓存并叠加绘制到画面上，
    /// 识别丢失超过 1.5 秒后自动清除旧结果，避免闪烁。
    /// </summary>
    public class BarcodeDetectionComponent
    {
        private readonly BarcodeReaderGeneric _reader; // ZXing 解码器
        private int _frameCounter;                   // 帧计数器（用于节流）
        private const int DecodeIntervalFrames = 6;  // 每 6 帧识别一次
        private string? _lastText;                   // 最近一次识别的文本
        private long _lastDecodeTime;                   // 最近一次成功识别时间（Stopwatch 时间戳）

        public BarcodeDetectionComponent()
        {
            _reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,   // 自动旋转支持任意方向的码
                Options = new DecodingOptions
                {
                    TryHarder = true,   // 更激进的识别策略
                    TryInverted = true, // 支持反色码（浅色码印在深色背景）
                    PossibleFormats = new[]
                    {
                        BarcodeFormat.QR_CODE,
                        BarcodeFormat.DATA_MATRIX,
                        BarcodeFormat.CODE_128,
                        BarcodeFormat.CODE_39,
                        BarcodeFormat.EAN_13,
                        BarcodeFormat.EAN_8,
                        BarcodeFormat.UPC_A,
                        BarcodeFormat.UPC_E,
                        BarcodeFormat.ITF
                    }
                }
            };
        }

        /// <summary>
        /// 识别帧中的二维码/条码，并在显示图像上绘制结果文本。
        /// </summary>
        /// <param name="frame">原始 BGR 帧</param>
        /// <param name="display">用于绘制的显示图像</param>
        /// <returns>识别出的文本，未识别到时返回 null</returns>
        public string? Detect(Mat frame, Mat display)
        {
            _frameCounter++;
            if (_frameCounter % DecodeIntervalFrames != 0)
            {
                DrawLastResult(display);
                return _lastText;
            }

            try
            {
                // 转 RGB 字节数组（ZXing 需要 RGB24 格式）
                using Mat rgbFull = new Mat();
                Cv2.CvtColor(frame, rgbFull, ColorConversionCodes.BGR2RGB);

                // Marshal.Copy 要求缓冲区连续；若将来传入 ROI/子 Mat，
                // 直接按 Total*ElemSize 拷贝会得到错位像素数据。
                using Mat rgb = rgbFull.IsContinuous() ? rgbFull : rgbFull.Clone();

                byte[] buffer = new byte[rgb.Total() * rgb.ElemSize()];
                System.Runtime.InteropServices.Marshal.Copy(rgb.Data, buffer, 0, buffer.Length);

                var source = new RGBLuminanceSource(
                    buffer, rgb.Width, rgb.Height, RGBLuminanceSource.BitmapFormat.RGB24);
                Result? result = _reader.Decode(source);
                if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                {
                    _lastText = result.Text;
                    // 使用单调时钟而非 DateTime.Now：后者受 NTP 校时/DST 影响，
                    // 时钟回拨会让结果永不过期，时钟前跳会提前清除结果。
                    _lastDecodeTime = System.Diagnostics.Stopwatch.GetTimestamp();
                    DrawLastResult(display);
                    return _lastText;
                }
            }
            catch
            {
                // 识别异常不致命，保留上一次结果
            }

            // 超过 1.5 秒未识别到新码则清除旧结果
            if (_lastText != null && ElapsedSecondsSinceLastDecode() > 1.5)
                _lastText = null;

            DrawLastResult(display);
            return _lastText;
        }

        /// <summary>距上次成功解码经过的秒数（基于单调时钟）</summary>
        private double ElapsedSecondsSinceLastDecode()
            => (System.Diagnostics.Stopwatch.GetTimestamp() - _lastDecodeTime)
               / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>在显示图像左上角绘制最近识别结果</summary>
        private void DrawLastResult(Mat display)
        {
            if (string.IsNullOrEmpty(_lastText)) return;

            // 过长文本会溢出画面，按画面宽度截断
            string text = _lastText!;
            int maxChars = Math.Max(8, display.Width / 14);
            if (text.Length > maxChars)
                text = text[..maxChars];

            Cv2.PutText(display, text, new OpenCvSharp.Point(20, 40),
                HersheyFonts.HersheySimplex, 0.9, new Scalar(0, 200, 255), 2, LineTypes.AntiAlias);
        }
    }
}
