using System.IO;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using CvDnn = OpenCvSharp.Cv2.Dnn;

namespace VirtualPathVision.AI
{
    /// <summary>
    /// 目标类别信息</summary>
    public class Detection
    {
        public int ClassId { get; set; }
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public Point2f Center { get; set; }
    }

    /// <summary>
    /// YOLO 目标检测组件，基于 OpenCV 5 DNN 推理引擎。
    /// 支持 YOLOv5/v8 ONNX 模型，实现实时目标检测。
    /// 输出包含边界框、类别 ID、置信度，供下游跟踪器使用。
    /// </summary>
    public class YoloDetectionComponent : IDisposable
    {
        private Net? _net;
        private readonly string[] _classNames;
        private readonly Size _inputSize;
        private readonly float _confidenceThreshold;
        private readonly float _nmsThreshold;
        private bool _disposed;

        // COCO 80 类默认标签
        private static readonly string[] CocoLabels = {
            "person","bicycle","car","motorcycle","airplane","bus","train","truck",
            "boat","traffic light","fire hydrant","stop sign","parking meter","bench",
            "bird","cat","dog","horse","sheep","cow","elephant","bear","zebra",
            "giraffe","backpack","umbrella","handbag","tie","suitcase","frisbee",
            "skis","snowboard","sports ball","kite","baseball bat","baseball glove",
            "skateboard","surfboard","tennis racket","bottle","wine glass","cup",
            "fork","knife","spoon","bowl","banana","apple","sandwich","orange",
            "broccoli","carrot","hot dog","pizza","donut","cake","chair","couch",
            "potted plant","bed","dining table","toilet","tv","laptop","mouse",
            "remote","keyboard","cell phone","microwave","oven","toaster","sink",
            "refrigerator","book","clock","vase","scissors","teddy bear",
            "hair drier","toothbrush"
        };

        /// <summary>检测结果置信度阈值（默认 0.5）</summary>
        public float ConfidenceThreshold => _confidenceThreshold;

        /// <summary>是否已加载模型</summary>
        public bool IsModelLoaded => _net != null;

        /// <summary>
        /// 初始化 YOLO 检测器。
        /// </summary>
        /// <param name="modelPath">YOLO ONNX 模型路径</param>
        /// <param name="classNames">类别名称数组（null 则使用 COCO 80 类）</param>
        /// <param name="inputSize">模型输入尺寸（默认 640x640）</param>
        /// <param name="confidenceThreshold">置信度阈值（默认 0.5）</param>
        /// <param name="nmsThreshold">NMS 阈值（默认 0.45）</param>
        /// <param name="backend">DNN 后端（默认 CPU）</param>
        /// <param name="target">DNN 目标设备（默认 CPU）</param>
        public YoloDetectionComponent(
            string modelPath,
            string[]? classNames = null,
            Size? inputSize = null,
            float confidenceThreshold = 0.5f,
            float nmsThreshold = 0.45f,
            Backend backend = Backend.OPENCV,
            Target target = Target.CPU)
        {
            _classNames = classNames ?? CocoLabels;
            _inputSize = inputSize ?? new Size(640, 640);
            _confidenceThreshold = confidenceThreshold;
            _nmsThreshold = nmsThreshold;

            if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
            {
                Net? net = CvDnn.ReadNetFromONNX(modelPath, EngineType.Auto);
                if (net != null)
                {
                    net.SetPreferableBackend(backend);
                    net.SetPreferableTarget(target);
                    _net = net;
                }
            }
        }

        /// <summary>
        /// 对输入帧执行目标检测。
        /// </summary>
        /// <param name="frame">输入 BGR 帧</param>
        /// <returns>检测到的目标列表</returns>
        public List<Detection> Detect(Mat frame)
        {
            if (_net == null || frame.Empty())
                return new List<Detection>();

            // 预处理：Resize + Blob
            using Mat blob = CvDnn.BlobFromImage(
                frame, 1.0 / 255.0, _inputSize, Scalar.All(0), swapRB: true, crop: false);
            _net.SetInput(blob);

            // 前向推理
            using Mat output = _net.Forward();

            // 解析 YOLO 输出
            return ParseYoloOutput(output, frame.Width, frame.Height);
        }

        /// <summary>
        /// 在帧上绘制检测结果。
        /// </summary>
        public void DrawDetections(Mat frame, List<Detection> detections)
        {
            foreach (var det in detections)
            {
                // 边界框
                Cv2.Rectangle(frame, det.BoundingBox, new Scalar(0, 255, 0), 2);

                // 标签背景
                string label = $"{det.ClassName} {det.Confidence:F2}";
                int baseline;
                var textSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.6, 1, out baseline);
                var labelBg = new Rect(
                    det.BoundingBox.X, det.BoundingBox.Y - textSize.Height - 10,
                    textSize.Width + 10, textSize.Height + 10);

                Cv2.Rectangle(frame, labelBg, new Scalar(0, 255, 0), Cv2.FILLED);
                Cv2.PutText(frame, label,
                    new OpenCvSharp.Point(det.BoundingBox.X + 5, det.BoundingBox.Y - 5),
                    HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 0, 0), 1, LineTypes.AntiAlias);
            }
        }

        /// <summary>
        /// 解析 YOLO 模型输出张量。
        /// YOLOv5/v8 输出格式: [1, num_detections, 5 + num_classes]
        /// 前 4 值为 (cx, cy, w, h)，第 5 值为 confidence，后续为类别概率。
        /// </summary>
        private List<Detection> ParseYoloOutput(Mat output, int frameWidth, int frameHeight)
        {
            var detections = new List<Detection>();

            // YOLOv8 输出: [1, 84, 8400] (转置后)
            // YOLOv5 输出: [1, 25200, 85]
            int rows = output.Size(1);
            int cols = output.Size(2);

            bool isV8Format = cols > rows; // v8: [1,84,8400], v5: [1,25200,85]

            float scaleX, scaleY;
            int numDetections, numAttributes;

            if (isV8Format)
            {
                // v8: cols = 4+80=84, rows = 8400
                numAttributes = cols;
                numDetections = rows;
                scaleX = (float)frameWidth / _inputSize.Width;
                scaleY = (float)frameHeight / _inputSize.Height;
            }
            else
            {
                // v5: rows = 25200, cols = 4+1+80=85
                numAttributes = cols;
                numDetections = rows;
                scaleX = (float)frameWidth / _inputSize.Width;
                scaleY = (float)frameHeight / _inputSize.Height;
            }

            var classIds = new List<int>();
            var confidences = new List<float>();
            var boxes = new List<Rect>();

            for (int i = 0; i < numDetections; i++)
            {
                float cx, cy, w, h, objConf;

                if (isV8Format)
                {
                    cx = output.At<float>(0, i, 0) * scaleX;
                    cy = output.At<float>(0, i, 1) * scaleY;
                    w = output.At<float>(0, i, 2) * scaleX;
                    h = output.At<float>(0, i, 3) * scaleY;

                    // 找最大类别概率
                    int bestClassId = -1;
                    float bestScore = 0;
                    for (int j = 4; j < numAttributes; j++)
                    {
                        float score = output.At<float>(0, i, j);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestClassId = j - 4;
                        }
                    }
                    objConf = bestScore;
                    if (objConf < _confidenceThreshold) continue;

                    classIds.Add(bestClassId);
                    confidences.Add(objConf);
                    boxes.Add(new Rect(
                        (int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h));
                }
                else
                {
                    cx = output.At<float>(i, 0) * scaleX;
                    cy = output.At<float>(i, 1) * scaleY;
                    w = output.At<float>(i, 2) * scaleX;
                    h = output.At<float>(i, 3) * scaleY;
                    objConf = output.At<float>(i, 4);

                    if (objConf < _confidenceThreshold) continue;

                    // 找最大类别概率
                    int bestClassId = -1;
                    float bestScore = 0;
                    for (int j = 5; j < numAttributes; j++)
                    {
                        float score = output.At<float>(i, j) * objConf;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestClassId = j - 5;
                        }
                    }

                    if (bestScore < _confidenceThreshold) continue;

                    classIds.Add(bestClassId);
                    confidences.Add(bestScore);
                    boxes.Add(new Rect(
                        (int)(cx - w / 2), (int)(cy - h / 2), (int)w, (int)h));
                }
            }

            // NMS 去重
            CvDnn.NMSBoxes(
                boxes.ToArray(), confidences.ToArray(),
                _confidenceThreshold, _nmsThreshold, out int[] indices);

            foreach (int idx in indices)
            {
                string className = classIds[idx] < _classNames.Length
                    ? _classNames[classIds[idx]]
                    : $"class_{classIds[idx]}";

                detections.Add(new Detection
                {
                    ClassId = classIds[idx],
                    ClassName = className,
                    Confidence = confidences[idx],
                    BoundingBox = boxes[idx],
                    Center = new Point2f(
                        boxes[idx].X + boxes[idx].Width / 2f,
                        boxes[idx].Y + boxes[idx].Height / 2f)
                });
            }

            return detections;
        }

        /// <summary>
        /// 加载自定义 YOLO 模型（运行时切换模型）。
        /// </summary>
        public bool LoadModel(string modelPath, Backend backend = Backend.OPENCV, Target target = Target.CPU)
        {
            try
            {
                _net?.Dispose();
                _net = null;
                Net? net = CvDnn.ReadNetFromONNX(modelPath, EngineType.Auto);
                if (net == null)
                    return false;
                net.SetPreferableBackend(backend);
                net.SetPreferableTarget(target);
                _net = net;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _net?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
