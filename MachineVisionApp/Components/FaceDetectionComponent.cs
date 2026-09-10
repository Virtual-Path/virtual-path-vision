using OpenCvSharp;

namespace MachineVisionApp.Components
{
    /// <summary>
    /// 人脸检测组件，基于 OpenCV 5 DNN 人脸检测器 (YuNet) 实现。
    /// 相比传统 Haar 级联分类器，YuNet 基于深度学习，精度更高、误检更少。
    /// 支持配置检测灵敏度、最小人脸尺寸以及绘制边框的样式。
    /// </summary>
    public class FaceDetectionComponent
    {
        private readonly FaceDetectorYN _faceDetector;

        // 检测参数
        private float _scoreThreshold = 0.6f;        // 置信度阈值，越低越灵敏
        private float _nmsThreshold = 0.3f;           // NMS 阈值，抑制重叠框
        private int _topK = 5000;                     // NMS 前保留的最大候选框数
        private int _minFaceSize = 40;                 // 最小人脸尺寸（像素），用于过滤

        // 绘制参数
        private Scalar _rectangleColor = new Scalar(0, 255, 0); // 绿色框
        private int _rectangleThickness = 2;

        /// <summary>
        /// 初始化人脸检测组件，加载 YuNet DNN 模型。
        /// </summary>
        /// <param name="modelPath">YuNet ONNX 模型文件路径</param>
        public FaceDetectionComponent(string modelPath)
        {
            _faceDetector = FaceDetectorYN.Create(
                model: modelPath,
                config: "",
                inputSize: new Size(320, 320),
                scoreThreshold: _scoreThreshold,
                nmsThreshold: _nmsThreshold,
                topK: _topK);
        }

        /// <summary>置信度阈值（默认 0.6），值越低检测越灵敏但误检可能增加</summary>
        public float ScoreThreshold
        {
            get => _scoreThreshold;
            set
            {
                _scoreThreshold = Math.Clamp(value, 0.1f, 1.0f);
                _faceDetector?.SetScoreThreshold(_scoreThreshold);
            }
        }

        /// <summary>NMS 阈值（默认 0.3），值越低抑制重叠框越强</summary>
        public float NmsThreshold
        {
            get => _nmsThreshold;
            set
            {
                _nmsThreshold = Math.Clamp(value, 0.1f, 1.0f);
                _faceDetector?.SetNMSThreshold(_nmsThreshold);
            }
        }

        /// <summary>最小人脸尺寸（默认 40px），低于此尺寸的候选区域将被过滤</summary>
        public int MinFaceSize
        {
            get => _minFaceSize;
            set => _minFaceSize = Math.Max(value, 10);
        }

        /// <summary>人脸框颜色（默认绿色）</summary>
        public Scalar RectangleColor
        {
            get => _rectangleColor;
            set => _rectangleColor = value;
        }

        /// <summary>人脸框线宽（默认 2）</summary>
        public int RectangleThickness
        {
            get => _rectangleThickness;
            set => _rectangleThickness = Math.Max(value, 1);
        }

        /// <summary>
        /// 在图像中检测人脸，并在原始图像上绘制矩形框和 5 个关键点。
        /// YuNet 输出格式：每行 15 个 float 值
        ///   0-1: bbox 左上角 (x, y)
        ///   2-3: bbox 宽高 (w, h)
        ///   4-5: 右眼 (x, y)
        ///   6-7: 左眼 (x, y)
        ///   8-9: 鼻尖 (x, y)
        ///   10-11: 右嘴角 (x, y)
        ///   12-13: 左嘴角 (x, y)
        ///   14: 置信度分数
        /// </summary>
        /// <param name="frame">输入图像（BGR 或灰度均可，内部自动处理）</param>
        /// <returns>检测到的人脸数量</returns>
        public int DetectFaces(Mat frame)
        {
            // 设置输入尺寸为当前帧尺寸，确保坐标映射正确
            _faceDetector.SetInputSize(frame.Size());

            using Mat faces = new Mat();
            int faceCount = _faceDetector.Detect(frame, faces);

            if (faceCount == 0 || faces.Empty())
                return 0;

            // 逐行解析检测结果并绘制
            for (int i = 0; i < faceCount; i++)
            {
                float x = faces.At<float>(i, 0);
                float y = faces.At<float>(i, 1);
                float w = faces.At<float>(i, 2);
                float h = faces.At<float>(i, 3);
                float score = faces.At<float>(i, 14);

                // 过滤过小的人脸
                if (w < _minFaceSize || h < _minFaceSize)
                    continue;

                var bbox = new OpenCvSharp.Rect((int)x, (int)y, (int)w, (int)h);
                Cv2.Rectangle(frame, bbox, _rectangleColor, _rectangleThickness);

                // 绘制 5 个面部关键点
                DrawLandmark(frame, faces, i, 4, new Scalar(255, 0, 0));   // 右眼 (蓝)
                DrawLandmark(frame, faces, i, 6, new Scalar(0, 0, 255));   // 左眼 (红)
                DrawLandmark(frame, faces, i, 8, new Scalar(0, 255, 0));   // 鼻尖 (绿)
                DrawLandmark(frame, faces, i, 10, new Scalar(255, 0, 255)); // 右嘴角 (粉)
                DrawLandmark(frame, faces, i, 12, new Scalar(0, 255, 255)); // 左嘴角 (黄)

                // 绘制置信度标签
                Cv2.PutText(frame, $" {score:F2}",
                    new OpenCvSharp.Point((int)x, Math.Max(20, (int)y - 5)),
                    HersheyFonts.HersheySimplex, 0.5, _rectangleColor, 1, LineTypes.AntiAlias);
            }

            return faceCount;
        }

        /// <summary>在检测结果 Mat 中绘制单个关键点</summary>
        private void DrawLandmark(Mat frame, Mat faces, int faceIndex, int colOffset, Scalar color)
        {
            float lx = faces.At<float>(faceIndex, colOffset);
            float ly = faces.At<float>(faceIndex, colOffset + 1);
            Cv2.Circle(frame, new OpenCvSharp.Point((int)lx, (int)ly), 2, color, -1);
        }
    }
}
