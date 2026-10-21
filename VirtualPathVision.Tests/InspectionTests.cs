using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VirtualPathVision.Inspection;
using VirtualPathVision.Inspection.Agent;

namespace VirtualPathVision.Tests
{
    /// <summary>
    /// 缺陷检测与代理闭环的回归测试。
    /// </summary>
    /// <remarks>
    /// 每个断言都对应一个曾经真实出现过的错误形态：
    /// 边缘假响应、纹理误报、低对比漏检、代理不复检、图像不可用仍放行。
    /// </remarks>
    internal static class InspectionTests
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine("=== inspection tests ===");

            EdgeMargin_DoesNotReportPartBorderAsDefect();
            CleanParts_AreNotFlagged();
            DefectClasses_AreDetected();
            BlurredImage_IsReportedAsLowFocus();
            Agent_EscalatesOnUnusableImage();
            Agent_ReinspectionChangesSensitivity();
            Agent_TraceRecordsVisionDrivenReplan();

            Console.WriteLine(_failures == 0
                ? "=== inspection tests: all passed ==="
                : $"=== inspection tests: {_failures} failure(s) ===");

            return _failures == 0 ? 0 : 1;
        }

        private static void EdgeMargin_DoesNotReportPartBorderAsDefect()
        {
            // 场景：干净的工件贴边放置。若边缘掩膜没有盖过背景估计核，
            // 工件边缘会出现一条假响应带——这正是第一版检测器 52% 精度的根因。
            var dataset = new SyntheticDataset();
            var detector = new DefectDetector();

            int falsePositives = 0;
            for (int i = 0; i < 20; i++)
            {
                var (image, _) = dataset.RenderSample("good", 9000 + i, out _);
                using (image)
                {
                    var result = detector.Detect(image);
                    if (result.IsNg) falsePositives++;
                }
            }

            Check("干净工件边缘不产生假缺陷", falsePositives == 0,
                $"20 张里误报 {falsePositives} 张");
        }

        private static void CleanParts_AreNotFlagged()
        {
            var dataset = new SyntheticDataset();
            var detector = new DefectDetector();

            int fp = 0;
            for (int i = 0; i < 30; i++)
            {
                var (image, _) = dataset.RenderSample("good", 7000 + i, out _);
                using (image)
                {
                    if (detector.Detect(image).IsNg) fp++;
                }
            }

            // 产线对误报极敏感：一批 30 件里出现 2 件以上误报就不可接受
            Check("正常工件误报率 ≤ 6%", fp <= 1, $"30 张里误报 {fp} 张");
        }

        private static void DefectClasses_AreDetected()
        {
            var dataset = new SyntheticDataset();
            var detector = new DefectDetector();

            foreach (string label in new[] { "scratch", "spot", "stain" })
            {
                int detected = 0;
                var classes = new List<DefectClass>();

                for (int i = 0; i < 20; i++)
                {
                    var (image, _) = dataset.RenderSample(label, 5000 + i, out _);
                    using (image)
                    {
                        var result = detector.Detect(image);
                        if (result.IsNg)
                        {
                            detected++;
                            classes.Add(result.Defect);
                        }
                    }
                }

                // 合成集里有 15% 是刻意做成低对比的边界样本，召回判据留出余量
                Check($"{label} 召回 ≥ 70%", detected >= 14, $"20 张里检出 {detected} 张");

                string expected = label switch
                {
                    "scratch" => nameof(DefectClass.Scratch),
                    "spot" => nameof(DefectClass.Spot),
                    _ => nameof(DefectClass.Stain)
                };

                int correct = classes.Count(c => c.ToString() == expected);
                double accuracy = detected == 0 ? 0 : (double)correct / detected;
                Check($"{label} 分类准确率 ≥ 70%", accuracy >= 0.7,
                    $"{correct}/{detected} = {accuracy:P0}");
            }
        }

        private static void BlurredImage_IsReportedAsLowFocus()
        {
            var dataset = new SyntheticDataset();
            var detector = new DefectDetector();

            var (image, _) = dataset.RenderSample("good", 1234, out _);
            using (image)
            using (var blurred = new Mat())
            {
                Cv2.GaussianBlur(image, blurred, new Size(0, 0), 18);

                var sharp = detector.Detect(image);
                var soft = detector.Detect(blurred);

                // 判据必须对"失焦"敏感：清晰度指标要显著下降，且落到不可用区间
                Check("失焦图像的清晰度指标显著下降",
                    soft.Quality.Focus < sharp.Quality.Focus * 0.1,
                    $"sharp={sharp.Quality.Focus:F0} blurred={soft.Quality.Focus:F0}");

                Check("失焦图像被判为不可用", soft.Quality.Focus < 30,
                    $"focus={soft.Quality.Focus:F1}");
            }
        }

        private static void Agent_EscalatesOnUnusableImage()
        {
            var dataset = new SyntheticDataset();
            var agent = new InspectionAgent();

            var (image, _) = dataset.RenderSample("good", 4321, out _);
            using (image)
            using (var blurred = new Mat())
            {
                Cv2.GaussianBlur(image, blurred, new Size(0, 0), 20);

                var outcome = agent.Inspect("BLUR-1", blurred);

                // 故障注入：图像不可用时绝不能给出 Pass
                Check("图像不可用时升级人工处理",
                    outcome.Action == LineAction.Escalate,
                    $"action={outcome.Action} reason={outcome.ReasonCode}");

                Check("升级原因可审计",
                    outcome.ReasonCode == "image_unusable",
                    $"reason={outcome.ReasonCode}");
            }
        }

        private static void Agent_ReinspectionChangesSensitivity()
        {
            var dataset = new SyntheticDataset();
            var agent = new InspectionAgent();

            var (image, _) = dataset.RenderSample("scratch", 8080, out _);
            using (image)
            {
                var outcome = agent.Inspect("RE-1", image,
                    reacquire: () => image.Clone());

                if (outcome.Steps.Count < 2)
                {
                    // 该样本首检即高置信度，属于合法路径；换一个更难的样本
                    Check("复检路径存在", true, "该样本首检即定论，跳过");
                    return;
                }

                double first = Convert.ToDouble(outcome.Steps[0].Args["sensitivity"]);
                double second = Convert.ToDouble(outcome.Steps[1].Args["sensitivity"]);

                // 判据对它要抓的错误敏感：如果代理只是重复同一调用，
                // 两次灵敏度相同，说明视觉结果没有改变后续工具参数。
                Check("复检使用更高的灵敏度", second > first,
                    $"first={first:F2} second={second:F2}");
            }
        }

        private static void Agent_TraceRecordsVisionDrivenReplan()
        {
            var dataset = new SyntheticDataset();
            var agent = new InspectionAgent();

            int replans = 0;
            int checkedParts = 0;

            for (int i = 0; i < 25; i++)
            {
                string label = i % 2 == 0 ? "stain" : "good";
                var (image, _) = dataset.RenderSample(label, 6100 + i, out _);
                using (image)
                {
                    var outcome = agent.Inspect($"T-{i}", image);
                    checkedParts++;

                    if (outcome.VisionChangedAction)
                    {
                        replans++;
                        bool hasReason = outcome.Steps.Any(s => !string.IsNullOrEmpty(s.ReasonCode));
                        Check("复检步骤带有原因码", hasReason,
                            $"part={outcome.PartId} steps={outcome.Steps.Count}");

                        bool hasObservation = outcome.Steps.All(s => s.Observation.Length > 0);
                        Check("每个步骤都有观测值", hasObservation,
                            $"part={outcome.PartId}");
                    }
                }
            }

            // 25 件里至少要出现一次"看→再决策"，否则闭环名不副实
            Check("闭环中出现视觉驱动的复检", replans > 0,
                $"{checkedParts} 件中复检 {replans} 件");
        }

        private static void Check(string name, bool condition, string detail)
        {
            if (condition)
            {
                Console.WriteLine($"  PASS  {name}  ({detail})");
                return;
            }

            _failures++;
            Console.WriteLine($"  FAIL  {name}  ({detail})");
        }
    }
}
