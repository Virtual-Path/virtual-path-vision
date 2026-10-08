using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VirtualPathVision.Industrial
{
    /// <summary>单件产品的最终判定结果。</summary>
    public enum PieceVerdict
    {
        /// <summary>尚未稳定判定</summary>
        Undecided,

        /// <summary>合格</summary>
        Passed,

        /// <summary>不合格</summary>
        Failed
    }

    /// <summary>
    /// 一件产品的完整生命周期记录。
    /// </summary>
    /// <param name="WorkpieceId">目标标识</param>
    /// <param name="Verdict">判定结果</param>
    /// <param name="StableFrames">达到稳定所用的连续帧数</param>
    /// <param name="Confidence">判定置信度</param>
    /// <param name="FirstSeenAt">首次进入检测区</param>
    /// <param name="SettledAt">判定稳定时刻</param>
    public sealed record PieceResult(
        string WorkpieceId,
        PieceVerdict Verdict,
        int StableFrames,
        double Confidence,
        DateTime FirstSeenAt,
        DateTime SettledAt)
    {
        public bool Passed => Verdict == PieceVerdict.Passed;
    }

    /// <summary>
    /// 产线编排层：把「检测结果 → 稳定判定 → 计数 → MES 上报」串成一条链。
    ///
    /// <para><b>处理流程</b>：对每一帧调用 <see cref="ProcessFrame"/>，
    /// 传入该帧检出的目标列表。编排层对每个目标：</para>
    /// <list type="number">
    /// <item>按位置派生 <c>trackKey</c>，使同一工件在相邻帧归为一组；</item>
    /// <item>交给 <see cref="StabilityFilter"/> 要求连续 N 帧结论一致；</item>
    /// <item>首次稳定即判定完成，产出 <see cref="PieceResult"/> 并触发事件；</item>
    /// <item>已判定过的目标在同一批次内不重复上报。</item>
    /// </list>
    ///
    /// <para><b>位置分桶</b>：工件在传送带上移动，连续帧的位置会变。
    /// <see cref="PositionBucketSize"/> 决定多大的位置变化算作"同一个目标"。
    /// 太大则相邻两件被并为一组，太小则同一件在帧间被拆成多个目标。
    /// 默认 24px 对应 0.75 单位/秒、1280x720 视场下约 1.4px/帧 的位移。</para>
    ///
    /// <para><b>去重</b>：一旦某目标判定完成，其 trackKey 进入 <c>_settled</c>，
    /// 直到它超时离开检测区才移除。这防止同一件因短暂遮挡丢失后又重新检出，
    /// 被判第二次、重复计数。</para>
    ///
    /// <para><b>线程模型</b>：<see cref="ProcessFrame"/> 应在采集线程上调用；
    /// MES 上报以 fire-and-forget 方式发起，不阻塞采集线程。</para>
    /// </summary>
    public sealed class ProductionLineService : IDisposable
    {
        /// <summary>一帧内检出的单个目标。</summary>
        /// <param name="CenterX">目标中心 X（像素）</param>
        /// <param name="CenterY">目标中心 Y（像素）</param>
        /// <param name="Signature">特征签名，如 "Red"、"circle"、"barcode:ABC"</param>
        /// <param name="Confidence">该次检出的置信度</param>
        public readonly record struct Detection(
            float CenterX, float CenterY, string Signature, double Confidence);

        private readonly StabilityFilter _stability;
        private readonly MesClient? _mes;

        private readonly HashSet<string> _settled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _firstSeen = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        private int _passedCount;
        private int _failedCount;
        private DateTime _startedAt = DateTime.Now;

        /// <summary>
        /// 创建产线编排服务。
        /// </summary>
        /// <param name="stability">稳定判定器，为 null 时按默认 3 帧 / 1.0s 创建</param>
        /// <param name="mes">MES 客户端，为 null 表示不上报（仅本地统计）</param>
        /// <param name="positionBucketSize">位置分桶粒度（像素），保留给不需要跟踪的调用方</param>
        /// <param name="matchRadius">
        /// 同一目标在相邻帧之间允许的最大横向位移（像素）。取值应略大于
        /// 「速度 x 帧间隔」：默认 60px 对应 30fps 下约 2px/帧 的位移，
        /// 留了很大余量，同时远小于工件间距。
        /// </param>
        public ProductionLineService(
            StabilityFilter? stability = null,
            MesClient? mes = null,
            float positionBucketSize = 24f,
            float matchRadius = 60f)
        {
            if (positionBucketSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(positionBucketSize));
            if (matchRadius <= 0)
                throw new ArgumentOutOfRangeException(nameof(matchRadius));

            _stability = stability ?? new StabilityFilter();
            _mes = mes;
            PositionBucketSize = positionBucketSize;
            MatchRadius = matchRadius;
        }

        /// <summary>位置分桶粒度（像素）。</summary>
        public float PositionBucketSize { get; }

        /// <summary>同一目标允许的帧间最大位移（像素）。</summary>
        public float MatchRadius { get; }

        /// <summary>累计合格数。</summary>
        public int PassedCount { get { lock (_gate) return _passedCount; } }

        /// <summary>累计不合格数。</summary>
        public int FailedCount { get { lock (_gate) return _failedCount; } }

        /// <summary>累计已判定总数。</summary>
        public int TotalCount { get { lock (_gate) return _passedCount + _failedCount; } }

        /// <summary>
        /// 良率。分母为 0 时返回 0 表示"尚未生产"。
        /// </summary>
        public double YieldRate
        {
            get
            {
                lock (_gate)
                {
                    int total = _passedCount + _failedCount;
                    return total == 0 ? 0 : (double)_passedCount / total;
                }
            }
        }

        /// <summary>连续 N 帧一致即判定完成时触发。</summary>
        public event Action<PieceResult>? OnPieceSettled;

        /// <summary>MES 上报失败时触发，携带诊断信息。</summary>
        public event Action<string, MesReportResult>? OnMesReportFailed;

        /// <summary>
        /// 处理一帧的检出结果，返回本帧新判定的件数。
        /// </summary>
        /// <param name="detections">本帧检出的目标</param>
        /// <param name="timestamp">当前时间</param>
        public int ProcessFrame(IReadOnlyList<Detection> detections, DateTime timestamp)
        {
            if (detections == null || detections.Count == 0)
                return 0;

            int settledNow = 0;

            foreach (var d in detections)
            {
                string key = ResolveTrack(d);

                // 已判定过：不再重复处理，等它离开检测区后由 Expire 清理
                lock (_gate)
                {
                    if (_settled.Contains(key)) continue;
                }

                if (!_firstSeen.TryGetValue(key, out DateTime first))
                {
                    _firstSeen[key] = timestamp;
                    first = timestamp;
                }

                var verdict = _stability.Observe(key, d.Signature, NowSeconds(timestamp));
                if (verdict != StabilityVerdict.Stable) continue;

                var piece = new PieceResult(
                    key,
                    Classify(d.Signature),
                    _stability.RequiredFrames,
                    d.Confidence,
                    first,
                    timestamp);

                lock (_gate)
                {
                    if (!_settled.Add(key)) continue;   // 并发下重复判定

                    if (piece.Passed) _passedCount++;
                    else _failedCount++;
                }

                settledNow++;
                OnPieceSettled?.Invoke(piece);
                ReportToMes(piece);
            }

            return settledNow;
        }

        /// <summary>
        /// 由特征签名推断合格与否。
        ///
        /// 约定：签名以 <c>ng:</c> 或 <c>defect:</c> 开头表示已识别为缺陷，
        /// <c>ok:</c> 开头表示合格，其余按合格处理。真实产线里检测算法
        /// 会给出更细的缺陷分类，这里提供统一的转换入口。
        /// </summary>
        public static PieceVerdict Classify(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return PieceVerdict.Undecided;

            string s = signature.TrimStart();
            if (s.StartsWith("ng:", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("defect:", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("fail:", StringComparison.OrdinalIgnoreCase))
            {
                return PieceVerdict.Failed;
            }

            return PieceVerdict.Passed;
        }

        /// <summary>
        /// 清理已离开检测区的目标。
        /// </summary>
        /// <remarks>
        /// <b>为什么必须清理</b>：<see cref="StabilityFilter"/> 会作废超时目标的
        /// 计时，但本类维护的 <c>_settled</c> 与 <c>_firstSeen</c> 不会自动同步。
        /// 若不清理，下一件恰好停在同一分桶内时会被误判为"已判定过"而漏检。
        /// </remarks>
        public int ExpireIdle(DateTime timestamp)
        {
            double now = NowSeconds(timestamp);
            int expired = _stability.Expire(now);

            double timeout = 1.0;
            var required = _stability.RequiredFrames;

            lock (_gate)
            {
                List<string>? stale = null;
                foreach (var kv in _firstSeen)
                {
                    // 与 StabilityFilter 使用同一时间基准：超过一个超时窗口即视为离开
                    if (now - NowSeconds(kv.Value) > timeout + 0.5)
                        (stale ??= new List<string>()).Add(kv.Key);
                }

                if (stale != null)
                {
                    foreach (string key in stale)
                    {
                        _firstSeen.Remove(key);
                        _settled.Remove(key);
                    }
                }

                return expired + (stale?.Count ?? 0);
            }
        }

        /// <summary>重置全部计数与状态（换批次时调用）。</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _passedCount = 0;
                _failedCount = 0;
                _startedAt = DateTime.Now;
                _settled.Clear();
                _firstSeen.Clear();
            }
            _stability.ResetAll();
        }

        private void ReportToMes(PieceResult piece)
        {
            if (_mes == null) return;

            var record = new QualityRecord(
                TraceId: piece.WorkpieceId,
                Passed: piece.Passed,
                DefectCode: piece.Passed ? null : piece.WorkpieceId,
                Confidence: piece.Confidence,
                WorkpieceId: piece.WorkpieceId);

            // 采集线程不等待网关：先记结论，再异步上报
            _ = ReportAsync(piece, record);
        }

        private async Task ReportAsync(PieceResult piece, QualityRecord record)
        {
            try
            {
                var result = piece.Passed
                    ? await _mes!.ReportQualityAsync(record).ConfigureAwait(false)
                    : await _mes!.ReportQualityAsync(record).ConfigureAwait(false);

                if (result.Success)
                {
                    // 结论记录成功后再请求执行动作，顺序不能反
                    var action = piece.Passed
                        ? await _mes!.PassAsync(record).ConfigureAwait(false)
                        : await _mes!.FailAsync(record).ConfigureAwait(false);

                    if (!action.Success)
                        OnMesReportFailed?.Invoke(piece.WorkpieceId, action);

                    return;
                }

                OnMesReportFailed?.Invoke(piece.WorkpieceId, result);
            }
            catch (Exception ex)
            {
                OnMesReportFailed?.Invoke(piece.WorkpieceId,
                    new MesReportResult(MesReportState.Failed, 0, ex.Message, 0));
            }
        }

        /// <summary>
        /// 由像素坐标派生目标标识。
        ///
        /// <para><b>为什么不能只按位置分桶</b>：传送带上的工件在连续帧之间会移动，
        /// 分桶粒度小于单帧位移时，同一件每帧都落进新的桶而被判为新件。
        /// 粒度放大到能覆盖整段行程，又会把相邻两件并为一组。</para>
        /// </summary>
        private string MakeTrackKey(float x, float y)
            => $"@{MathF.Round(x)},{MathF.Round(y)}";

        /// <summary>
        /// 把一次检出归入某个已有目标：签名相同且横向中心距在
        /// <see cref="MatchRadius"/> 内视为同一目标；否则视为新目标。
        ///
        /// <para>签名不同必然是不同类型的目标（红箱 vs 绿球），不会误并；
        /// 签名相同但距离超过半径，则是同类型的前后两件，也不误并。
        /// 这解决了纯位置分桶在匀速移动下"每帧一个新 key"的问题。</para>
        /// </summary>
        private string ResolveTrack(in Detection d)
        {
            string prefix = d.Signature + "@";

            lock (_gate)
            {
                string? best = null;
                float bestDist = MatchRadius;

                // 已判定的目标优先匹配，避免重新生成 key
                foreach (string key in _settled)
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!TryParseKey(key, out float kx, out _)) continue;

                    float dist = MathF.Abs(kx - d.CenterX);
                    if (dist < bestDist) { bestDist = dist; best = key; }
                }
                if (best != null) return best;

                // 未判定的目标也要匹配，否则同一件在尚未稳定时每帧生成新 key
                foreach (var kv in _firstSeen)
                {
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!TryParseKey(kv.Key, out float kx, out _)) continue;

                    float dist = MathF.Abs(kx - d.CenterX);
                    if (dist < bestDist) { bestDist = dist; best = kv.Key; }
                }

                return best ?? (d.Signature + MakeTrackKey(d.CenterX, d.CenterY));
            }
        }

        private static bool TryParseKey(string key, out float x, out float y)
        {
            x = 0; y = 0;
            int at = key.LastIndexOf('@');
            if (at < 0) return false;

            string[] parts = key[(at + 1)..].Split(',');
            if (parts.Length != 2) return false;

            return float.TryParse(parts[0], out x) && float.TryParse(parts[1], out y);
        }

        /// <summary>
        /// 把 DateTime 转成单调递增的秒数。
        ///
        /// 用 <see cref="DateTime.Now"/> 的相对值而非 Unix 时间戳，
        /// 这样即使系统时间被调整，已积累的连续计数也不会突然失效。
        /// </summary>
        private static double NowSeconds(DateTime t) => (t - DateTime.MinValue).TotalSeconds;

        public void Dispose() => _mes?.Dispose();
    }
}