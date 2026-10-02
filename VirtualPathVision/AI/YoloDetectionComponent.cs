using System.IO;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using CvDnn = OpenCvSharp.Cv2.Dnn;

namespace VirtualPathVision.AI
{
    /// <summary>
    /// 目标类别信息
    /// </summary>
    public class Detection
    {
        public int ClassId { get; set; } = -1;
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
        private readonly object _netLock = new();
        private readonly string[] _classNames;
        private readonly Size _inputSize;
        private readonly float _confidenceThreshold;
        private readonly float _nmsThreshold;
        private bool _disposed;

        // 解析输出张量的维度假设
        private const int ExpectedDims = 3;
        private const int BatchIndex = 0;
        private const int MinAttributes = 5;   // 至少 cx, cy, w, h, obj
        private const int MinRoiSize = 16;    // 小于该边长的输入区域不做 ROI 推理

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

        /// <summary>NMS IoU 阈值</summary>
        public float NmsThreshold => _nmsThreshold;

        /// <summary>是否已加载模型（释放后返回 false）</summary>
        public bool IsModelLoaded => !_disposed && _net != null;

        /// <summary>
        /// 初始化 YOLO 检测器。
        /// </summary>
        /// <param name="modelPath">YOLO ONNX 模型路径</param>
        /// <param name="classNames">类别名称数组（null 则使用 COCO 80 类）</param>
        /// <param name="inputSize">模型输入尺寸（默认 640x640）</param>
        /// <param name="confidenceThreshold">置信度阈值（默认 0.5，会被裁剪到 (0, 1]）</param>
        /// <param name="nmsThreshold">NMS 阈值（默认 0.45，会被裁剪到 (0, 1]）</param>
        /// <param name="backend">DNN 后端（默认 CPU）</param>
        /// <param name="target">DNN 目标设备（默认 CPU）</param>
        /// <exception cref="ArgumentException">modelPath 为空</exception>
        /// <exception cref="FileNotFoundException">模型文件不存在</exception>
        /// <exception cref="InvalidOperationException">模型加载失败</exception>
        public YoloDetectionComponent(
            string modelPath,
            string[]? classNames = null,
            Size? inputSize = null,
            float confidenceThreshold = 0.5f,
            float nmsThreshold = 0.45f,
            Backend backend = Backend.OPENCV,
            Target target = Target.CPU)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new ArgumentException("必须提供 YOLO ONNX 模型路径", nameof(modelPath));

            // 模型缺失必须显式失败：静默保持 _net == null 会让调用方误报"模型已加载"
            if (!File.Exists(modelPath))
                throw new FileNotFoundException($"YOLO 模型文件不存在: {modelPath}", modelPath);

            var size = inputSize ?? new Size(640, 640);
            if (size.Width <= 0 || size.Height <= 0)
                size = new Size(640, 640);

            _classNames = classNames is { Length: > 0 } ? classNames : CocoLabels;
            _inputSize = size;
            // 阈值裁剪：MainWindow 直接透传用户输入，越界阈值会导致"全部检测被拒"或"全部通过"
            _confidenceThreshold = float.IsFinite(confidenceThreshold)
                ? Math.Clamp(confidenceThreshold, 0.001f, 1f)
                : 0.5f;
            _nmsThreshold = float.IsFinite(nmsThreshold)
                ? Math.Clamp(nmsThreshold, 0.001f, 1f)
                : 0.45f;

            Net? net = CvDnn.ReadNetFromONNX(modelPath, EngineType.Auto);
            if (net == null)
            {
                throw new InvalidOperationException($"YOLO 模型加载失败（ReadNetFromONNX 返回空）: {modelPath}");
            }

            try
            {
                net.SetPreferableBackend(backend);
                net.SetPreferableTarget(target);
                _net = net;
            }
            catch
            {
                // 配置失败时释放刚创建的 Net，避免原生资源泄漏
                net.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 对输入帧执行目标检测。
        /// </summary>
        /// <param name="frame">输入 BGR 帧</param>
        /// <returns>检测到的目标列表</returns>
        public List<Detection> Detect(Mat frame)
        {
            if (_disposed || frame is null || frame.Empty())
                return new List<Detection>();

            // 帧尺寸过小时 ROI 缩放会失真，直接跳过（保留坐标缩放的一致性）
            if (frame.Width < MinRoiSize || frame.Height < MinRoiSize)
                return new List<Detection>();

            // Net 非线程安全：推理串行化，避免多线程前向传播崩溃
            lock (_netLock)
            {
                if (_disposed || _net == null)
                    return new List<Detection>();

                // 预处理：Resize + Blob
                using Mat blob = CvDnn.BlobFromImage(
                    frame, 1.0 / 255.0, _inputSize, Scalar.All(0), swapRB: true, crop: false);
                _net.SetInput(blob);

                // 前向推理
                using Mat output = _net.Forward();

                if (output is null || output.Empty())
                    return new List<Detection>();

                // 解析 YOLO 输出
                return ParseYoloOutput(output, frame.Width, frame.Height);
            }
        }

        /// <summary>
        /// 在帧上绘制检测结果。
        /// </summary>
        public void DrawDetections(Mat frame, List<Detection> detections)
        {
            if (frame is null || detections is null) return;

            foreach (var det in detections)
            {
                if (det == null) continue;

                // 边界框
                Cv2.Rectangle(frame, det.BoundingBox, new Scalar(0, 255, 0), 2);

                // 标签背景
                string label = $"{det.ClassName} {det.Confidence:F2}";
                int baseline;
                var textSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.6, 1, out baseline);
                var labelBg = new Rect(
                    det.BoundingBox.X, Math.Max(0, det.BoundingBox.Y - textSize.Height - 10),
                    textSize.Width + 10, textSize.Height + 10);

                Cv2.Rectangle(frame, labelBg, new Scalar(0, 255, 0), Cv2.FILLED);
                Cv2.PutText(frame, label,
                    new OpenCvSharp.Point(det.BoundingBox.X + 5, Math.Max(10, det.BoundingBox.Y - 5)),
                    HersheyFonts.HersheySimplex, 0.6, new Scalar(0, 0, 0), 1, LineTypes.AntiAlias);
            }
        }

        /// <summary>
        /// 解析 YOLO 模型输出张量。
        /// YOLOv5 输出格式: [1, num_detections, 5 + num_classes]，前 5 值为 (cx, cy, w, h, obj_conf)
        /// YOLOv8 输出格式: [1, 4 + num_classes, num_anchors]，无独立 obj 通道，类别概率即置信度
        /// </summary>
        private List<Detection> ParseYoloOutput(Mat output, int frameWidth, int frameHeight)
        {
            var detections = new List<Detection>();

            // 张量形状守卫：只接受 [1, rows, cols] 的 3 维 CV_32F 输出
            if (output.Dims != ExpectedDims || output.Type() != MatType.CV_32F)
                return detections;

            int batch = output.Size(BatchIndex);
            int rows = output.Size(1);
            int cols = output.Size(2);
            if (batch != 1 || rows <= 0 || cols <= 0)
                return detections;

            // YOLOv8: [1, 4+80, 8400] (cols < rows)；YOLOv5: [1, 25200, 85] (cols > rows)
            bool isV8Format = cols < rows;
            int numDetections = isV8Format ? rows : cols;   // 锚点/候选框数量
            int numAttributes = isV8Format ? cols : rows;   // 每框属性数
            int classOffset = isV8Format ? 4 : 5;           // 类别通道起始索引

            if (numAttributes < MinAttributes || numAttributes <= classOffset)
                return detections;

            float scaleX = (float)frameWidth / _inputSize.Width;
            float scaleY = (float)frameHeight / _inputSize.Height;

            var classIds = new List<int>();
            var confidences = new List<float>();
            var boxes = new List<Rect>();

            for (int i = 0; i < numDetections; i++)
            {
                float cx, cy, w, h, objConf;
                // 最佳类别必须在两个分支之外声明，bestClassId < 0 表示该框无有效类别
                int bestClassId = -1;
                float bestScore = 0;

                if (isV8Format)
                {
                    // v8: 属性维度为最内层维度
                    cx = output.At<float>(BatchIndex, i, 0) * scaleX;
                    cy = output.At<float>(BatchIndex, i, 1) * scaleY;
                    w = output.At<float>(BatchIndex, i, 2) * scaleX;
                    h = output.At<float>(BatchIndex, i, 3) * scaleY;

                    // 找最大类别概率（v8 通道即最终置信度）
                    for (int j = classOffset; j < numAttributes; j++)
                    {
                        float score = output.At<float>(BatchIndex, i, j);
                        if (!float.IsFinite(score)) continue;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestClassId = j - classOffset;
                        }
                    }

                    objConf = bestScore;
                    // 全部类别分数 <= 0（含 bestClassId == -1）时直接跳过，
                    // 否则后续 _classNames[-1] 会抛 IndexOutOfRangeException
                    if (bestClassId < 0 || objConf < _confidenceThreshold) continue;
                }
                else
                {
                    // v5: 每行一个候选框
                    cx = output.At<float>(i, 0) * scaleX;
                    cy = output.At<float>(i, 1) * scaleY;
                    w = output.At<float>(i, 2) * scaleX;
                    h = output.At<float>(i, 3) * scaleY;
                    objConf = output.At<float>(i, 4);

                    if (!float.IsFinite(objConf) || objConf < _confidenceThreshold) continue;

                    // 找最大类别概率
                    for (int j = classOffset; j < numAttributes; j++)
                    {
                        float score = output.At<float>(i, j) * objConf;
                        if (!float.IsFinite(score)) continue;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestClassId = j - classOffset;
                        }
                    }

                    if (bestClassId < 0 || bestScore < _confidenceThreshold) continue;
                    objConf = bestScore;
                }

                // 非有限值直接拒绝：NaN/Inf 会让 (int) 转换溢出为 int.MinValue
                if (!float.IsFinite(cx) || !float.IsFinite(cy) ||
                    !float.IsFinite(w) || !float.IsFinite(h))
                    continue;

                // 裁剪到图像范围，并丢弃退化框
                int x1 = Math.Clamp((int)MathF.Round(cx - w / 2f), 0, Math.Max(0, frameWidth - 1));
                int y1 = Math.Clamp((int)MathF.Round(cy - h / 2f), 0, Math.Max(0, frameHeight - 1));
                int x2 = Math.Clamp((int)MathF.Round(cx + w / 2f), x1, frameWidth);
                int y2 = Math.Clamp((int)MathF.Round(cy + h / 2f), y1, frameHeight);

                int boxWidth = x2 - x1;
                int boxHeight = y2 - y1;
                if (boxWidth <= 0 || boxHeight <= 0) continue;

                classIds.Add(bestClassId);
                confidences.Add(objConf);
                boxes.Add(new Rect(x1, y1, boxWidth, boxHeight));
            }

            if (boxes.Count == 0)
                return detections;

            // 按类别分组执行 NMS：不同类别的重叠框不应互相抑制
            var perClass = new Dictionary<int, List<int>>();
            for (int i = 0; i < classIds.Count; i++)
            {
                if (!perClass.TryGetValue(classIds[i], out var group))
                {
                    group = new List<int>();
                    perClass[classIds[i]] = group;
                }
                group.Add(i);
            }

            var keptIndices = new List<int>();
            foreach (var group in perClass.Values)
            {
                if (group.Count == 0) continue;
                if (group.Count == 1)
                {
                    keptIndices.Add(group[0]);
                    continue;
                }

                var groupBoxes = new Rect[group.Count];
                var groupScores = new float[group.Count];
                for (int k = 0; k < group.Count; k++)
                {
                    groupBoxes[k] = boxes[group[k]];
                    groupScores[k] = confidences[group[k]];
                }

                CvDnn.NMSBoxes(groupBoxes, groupScores,
                    _confidenceThreshold, _nmsThreshold, out int[] indices);

                foreach (int localIdx in indices)
                {
                    if (localIdx >= 0 && localIdx < group.Count)
                        keptIndices.Add(group[localIdx]);
                }
            }

            foreach (int idx in keptIndices)
            {
                // 下标与类别 ID 双重守卫：避免越界访问 _classNames
                if (idx < 0 || idx >= classIds.Count) continue;

                int classId = classIds[idx];
                string className = classId >= 0 && classId < _classNames.Length
                    ? _classNames[classId]
                    : $"class_{classId}";

                var box = boxes[idx];
                detections.Add(new Detection
                {
                    ClassId = classId,
                    ClassName = className,
                    Confidence = confidences[idx],
                    BoundingBox = box,
                    Center = new Point2f(box.X + box.Width / 2f, box.Y + box.Height / 2f)
                });
            }

            return detections;
        }

        /// <summary>
        /// 加载自定义 YOLO 模型（运行时切换模型）。
        /// </summary>
        public bool LoadModel(string modelPath, Backend backend = Backend.OPENCV, Target target = Target.CPU)
        {
            if (_disposed || string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                return false;

            try
            {
                Net? net = CvDnn.ReadNetFromONNX(modelPath, EngineType.Auto);
                if (net == null)
                    return false;

                try
                {
                    net.SetPreferableBackend(backend);
                    net.SetPreferableTarget(target);
                }
                catch
                {
                    net.Dispose();
                    throw;
                }

                // 成功加载后再原子替换，替换失败时保留原模型
                lock (_netLock)
                {
                    var old = _net;
                    _net = net;
                    old?.Dispose();
                }
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

            lock (_netLock)
            {
                _net?.Dispose();
                _net = null; // 释放后不再暴露给 IsModelLoaded / Detect
            }

            GC.SuppressFinalize(this);
        }
    }
}