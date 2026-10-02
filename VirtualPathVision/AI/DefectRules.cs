using System;
using System.Collections.Generic;
using System.Linq;

namespace VirtualPathVision.AI
{
    /// <summary>
    /// 缺陷判定规则 —— 全应用唯一定义处。
    ///
    /// 历史问题：多处代码把 <c>Confidence &gt; 0.8f</c> 直接当作「缺陷」，
    /// 于是「一个高置信度的普通检测」反而被计入缺陷数，
    /// 导致合格率 / passRate 对任何 COCO 类模型都是错的。
    ///
    /// 正确语义：
    /// 1. 类别显式标记：类别名命中缺陷关键词（defect / damage / anomaly …）→ 缺陷；
    /// 2. 可疑置信度带：模型自己都不确定的区间 → 疑似缺陷；
    /// 3. 其余（含高置信度的普通目标）→ 不计为缺陷。
    ///    高置信度只说明模型「确信」，不代表「缺陷」。
    /// </summary>
    public static class DefectRules
    {
        /// <summary>默认缺陷类别关键词（不区分大小写，子串匹配）</summary>
        public static readonly IReadOnlyList<string> DefaultDefectClassKeywords = new[]
        {
            "defect", "damage", "broken", "crack", "scratch",
            "anomaly", "fault", "dent", " stain", "stain", "missing"
        };

        /// <summary>可疑置信度带下界（含）</summary>
        public const float DefaultSuspiciousMin = 0.35f;

        /// <summary>可疑置信度带上界（不含）</summary>
        public const float DefaultSuspiciousCeiling = 0.75f;

        /// <summary>
        /// 按默认规则统计缺陷数量。
        /// </summary>
        public static int CountDefects(IEnumerable<Detection>? detections)
        {
            if (detections == null) return 0;
            return detections.Count(d =>
                IsDefect(d, DefaultDefectClassKeywords, DefaultSuspiciousMin, DefaultSuspiciousCeiling));
        }

        /// <summary>
        /// 判断单个检测是否属于缺陷。
        /// </summary>
        /// <param name="detection">检测结果</param>
        /// <param name="defectKeywords">缺陷类别关键词；传 null 使用默认集合</param>
        /// <param name="suspiciousMin">可疑置信度带下界</param>
        /// <param name="suspiciousCeiling">可疑置信度带上界</param>
        public static bool IsDefect(
            Detection? detection,
            IReadOnlyList<string>? defectKeywords = null,
            float suspiciousMin = DefaultSuspiciousMin,
            float suspiciousCeiling = DefaultSuspiciousCeiling)
        {
            if (detection == null) return false;

            // 1) 显式缺陷类别
            if (!string.IsNullOrEmpty(detection.ClassName))
            {
                var keywords = defectKeywords ?? DefaultDefectClassKeywords;
                foreach (var keyword in keywords)
                {
                    if (!string.IsNullOrEmpty(keyword) &&
                        detection.ClassName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // 2) 可疑置信度带
            float conf = detection.Confidence;
            if (!float.IsFinite(conf)) return false;

            // 区间被配置反序时交换，避免永远匹配不到
            if (suspiciousMin > suspiciousCeiling)
                (suspiciousMin, suspiciousCeiling) = (suspiciousCeiling, suspiciousMin);

            return conf >= suspiciousMin && conf < suspiciousCeiling;
        }
    }
}