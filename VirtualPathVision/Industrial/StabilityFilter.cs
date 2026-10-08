using System;
using System.Collections.Generic;

namespace VirtualPathVision.Industrial
{
    /// <summary>
    /// 单个目标的稳定判定状态。
    /// </summary>
    public enum StabilityVerdict
    {
        /// <summary>尚未积累足够帧数</summary>
        Pending,

        /// <summary>已稳定：连续 N 帧观测一致</summary>
        Stable,

        /// <summary>不稳定：连续观测被打断，计时已清零</summary>
        Unstable
    }

    /// <summary>
    /// 连续 N 帧一致才确认的稳定判定器。
    ///
    /// <para><b>为什么需要</b>：产线工件不可能静止不动，颜色检测在单帧上
    /// 会因为光照抖动、运动模糊、压缩噪声而出现"闪现"——某一帧检出、下一帧丢失。
    /// 若按单帧结果直接报工，会产生大量误报与重复计数。因此必须要求
    /// <b>同一目标</b>（<see cref="TrackKey"/>）<b>连续 N 帧</b>给出相同结论，
    /// 才认为该结论可信。</para>
    ///
    /// <para><b>与常见"多数投票"的区别</b>：投票允许中间穿插少数反对票仍能通过；
    /// 这里要求严格连续。理由是产线的时序有意义——工件正在经过检测区，
    /// 一旦结论跳变，通常意味着遮挡或失焦，此时任何结论都不可信，
    /// 应当重新观察而不是等待票数攒够。</para>
    ///
    /// <para><b>线程模型</b>：无锁。所有状态由 <see cref="Observe"/> /
    /// <see cref="Expire"/> 在单一采集线程上调用，读取方通过快照取值。
    /// 快照是不可变对象，跨线程读取安全。</para>
    /// </summary>
    public sealed class StabilityFilter
    {
        private readonly int _requiredFrames;
        private readonly double _timeoutSeconds;

        /// <summary>每个目标的当前连续计数与最近观测时间。</summary>
        private readonly Dictionary<string, Candidate> _candidates = new();

        private readonly object _gate = new();

        /// <summary>
        /// 创建稳定判定器。
        /// </summary>
        /// <param name="requiredFrames">
        /// 判定为稳定所需的连续帧数。25fps 下 3 帧约 120ms，
        /// 足以跨过一次光照抖动，又不至于让工件通过检测区后还没确认。
        /// </param>
        /// <param name="timeoutSeconds">
        /// 同一目标超过此时长没有新观测，视为已离开检测区，状态作废。
        /// 防止工件离开后残留状态导致下一件被误判为"已稳定"。
        /// </param>
        public StabilityFilter(int requiredFrames = 3, double timeoutSeconds = 1.0)
        {
            if (requiredFrames < 1)
                throw new ArgumentOutOfRangeException(nameof(requiredFrames));
            if (timeoutSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

            _requiredFrames = requiredFrames;
            _timeoutSeconds = timeoutSeconds;
        }

        /// <summary>判定为稳定所需的连续帧数。</summary>
        public int RequiredFrames => _requiredFrames;

        /// <summary>当前被跟踪的目标数量。</summary>
        public int TrackedCount
        {
            get { lock (_gate) return _candidates.Count; }
        }

        /// <summary>
        /// 记录一次观测并返回当前判定。
        /// </summary>
        /// <param name="trackKey">
        /// 目标标识。通常由位置派生（如 "cx=412,cy=268"），使同一目标
        /// 的相邻帧能归为一组。目标移动较快时应放宽位置分桶的粒度。
        /// </param>
        /// <param name="signature">
        /// 本次观测的特征签名。同一目标连续观测的签名相同才算稳定。
        /// 例如颜色检测传 "Red"、形状检测传 "circle"。
        /// </param>
        /// <param name="timestampSeconds">单调递增的时间戳（秒）。</param>
        public StabilityVerdict Observe(string trackKey, string signature, double timestampSeconds)
        {
            if (string.IsNullOrEmpty(trackKey)) return StabilityVerdict.Pending;
            if (signature == null) return StabilityVerdict.Pending;

            lock (_gate)
            {
                // 顺带清理已超时的目标，避免字典无限增长
                ExpireLocked(timestampSeconds);

                if (!_candidates.TryGetValue(trackKey, out var c))
                {
                    c = new Candidate();
                    _candidates[trackKey] = c;
                }

                if (c.Signature == signature)
                {
                    c.Count++;
                }
                else
                {
                    // 结论跳变：说明被遮挡或失焦，之前的计数全部作废。
                    // 重新从 1 开始观察当前结论，而不是接着上一段的计数往下加。
                    c.Signature = signature;
                    c.Count = 1;
                }

                c.LastSeen = timestampSeconds;
                return c.Count >= _requiredFrames
                    ? StabilityVerdict.Stable
                    : StabilityVerdict.Pending;
            }
        }

        /// <summary>
        /// 主动作废某目标的计时。
        /// 用于目标离开检测区、或已确认判定并上报之后。
        /// </summary>
        public void Reset(string trackKey)
        {
            lock (_gate) _candidates.Remove(trackKey);
        }

        /// <summary>
        /// 作废全部目标。
        /// </summary>
        public void ResetAll()
        {
            lock (_gate) _candidates.Clear();
        }

        /// <summary>
        /// 清理超时的目标，返回被清理的数量。
        /// </summary>
        public int Expire(double timestampSeconds)
        {
            lock (_gate) return ExpireLocked(timestampSeconds);
        }

        private int ExpireLocked(double timestampSeconds)
        {
            if (_candidates.Count == 0) return 0;

            // 收集键后再删除，避免在遍历中修改字典
            List<string>? stale = null;
            double cutoff = timestampSeconds - _timeoutSeconds;

            foreach (var kv in _candidates)
            {
                if (kv.Value.LastSeen < cutoff)
                    (stale ??= new List<string>()).Add(kv.Key);
            }

            if (stale == null) return 0;

            foreach (string key in stale)
                _candidates.Remove(key);

            return stale.Count;
        }

        /// <summary>
        /// 取某目标当前的连续帧数，UI 调试用。
        /// </summary>
        public int GetCount(string trackKey)
        {
            lock (_gate)
                return _candidates.TryGetValue(trackKey, out var c) ? c.Count : 0;
        }

        private sealed class Candidate
        {
            public string Signature = "";
            public int Count;
            public double LastSeen;
        }
    }
}