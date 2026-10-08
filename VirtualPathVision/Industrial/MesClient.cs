using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VirtualPathVision.Industrial
{
    /// <summary>
    /// 单件产品的检测结论，用于上报 MES。
    /// </summary>
    /// <param name="TraceId">产线追踪码，通常来自扫码枪或工件二维码</param>
    /// <param name="Passed">是否合格</param>
    /// <param name="DefectCode">缺陷代码，合格时为空</param>
    /// <param name="Confidence">判定置信度 0..1</param>
    /// <param name="WorkpieceId">目标标识，缺省等于 TraceId</param>
    /// <param name="TimestampUtc">判定时刻</param>
    public sealed record QualityRecord(
        string TraceId,
        bool Passed,
        string? DefectCode,
        double Confidence,
        string? WorkpieceId = null,
        DateTime? TimestampUtc = null)
    {
        public string EffectiveWorkpieceId
            => string.IsNullOrWhiteSpace(WorkpieceId) ? TraceId : WorkpieceId!;

        public DateTime EffectiveTimestamp => TimestampUtc ?? DateTime.UtcNow;
    }

    /// <summary>
    /// 上报结果。
    /// </summary>
    public enum MesReportState
    {
        /// <summary>尚未上报</summary>
        Pending,

        /// <summary>已成功送达网关</summary>
        Accepted,

        /// <summary>送达但网关返回业务错误</summary>
        Rejected,

        /// <summary>网络失败或超时，已进入重试队列</summary>
        Failed
    }

    /// <summary>
    /// 一次上报的结果与诊断信息。
    /// </summary>
    /// <param name="State">上报状态</param>
    /// <param name="StatusCode">HTTP 状态码，未发出请求时为 0</param>
    /// <param name="ResponseBody">网关返回体，截断到 512 字符用于日志</param>
    /// <param name="Attempt">这是第几次尝试（1 起）</param>
    public sealed record MesReportResult(
        MesReportState State,
        int StatusCode,
        string? ResponseBody,
        int Attempt)
    {
        public bool Success => State == MesReportState.Accepted;
    }

    /// <summary>
    /// MES 网关客户端。
    ///
    /// <para><b>协议约定</b>（与网关 <c>mes-gateway:9090</c> 对接）：
    /// <list type="bullet">
    /// <item><c>POST /api/quality/record</c> —— 上报单件检测结论</item>
    /// <item><c>POST /api/quality/pass</c> —— 合格件放行指令</item>
    /// <item><c>POST /api/quality/fail</c> —— 不合格件剔除指令</item>
    /// </list>
    /// 请求体为 JSON，<c>Accept: application/json</c>。</para>
    ///
    /// <para><b>失败处理</b>：网络异常与非 2xx 都视为失败并进入指数退避重试。
    /// 产线不能因为网关暂时不可用就停机，因此客户端<b>不阻塞</b>采集线程——
    /// 所有方法都是 <c>Task</c>，由调用方决定是否等待。重试在后台队列中进行。</para>
    ///
    /// <para><b>线程模型</b>：内部持有单个 <see cref="HttpClient"/>（可复用连接池）。
    /// 所有公开方法线程安全。重试队列由一个后台任务串行消费。</para>
    /// </summary>
    public sealed class MesClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _baseUrl;
        private readonly int _maxAttempts;
        private readonly TimeSpan _initialBackoff;

        private readonly Queue<PendingItem> _retryQueue = new();
        private readonly object _queueGate = new();
        private readonly SemaphoreSlim _retrySignal = new(0);
        private CancellationTokenSource? _retryCts;
        private Task? _retryTask;

        private readonly record struct PendingItem(QualityRecord Record, int Attempt);

        /// <summary>
        /// 创建 MES 客户端。
        /// </summary>
        /// <param name="baseUrl">网关基地址，如 <c>http://mes-gateway:9090</c>（尾部斜杠会被去掉）</param>
        /// <param name="maxAttempts">含首次请求的最大尝试次数</param>
        /// <param name="initialBackoff">首次重试前的等待时长，之后按 2 倍递增</param>
        /// <param name="httpClient">
        /// 复用外部 <see cref="HttpClient"/> 时传入，此时不接管其生命周期。
        /// 生产环境应复用同一实例以复用连接池。
        /// </param>
        public MesClient(
            string baseUrl,
            int maxAttempts = 3,
            TimeSpan? initialBackoff = null,
            HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl 不能为空", nameof(baseUrl));

            _baseUrl = baseUrl.TrimEnd('/');
            _maxAttempts = Math.Max(1, maxAttempts);
            _initialBackoff = initialBackoff ?? TimeSpan.FromMilliseconds(500);

            if (httpClient != null)
            {
                _http = httpClient;
                _ownsHttp = false;
            }
            else
            {
                _http = new HttpClient
                {
                    // 单次请求超时必须短于产线节拍，否则会拖慢整条流水线
                    Timeout = TimeSpan.FromSeconds(5),
                };
                _ownsHttp = true;
            }

            _retryCts = new CancellationTokenSource();
            _retryTask = Task.Run(() => RetryLoopAsync(_retryCts.Token));
        }

        /// <summary>网关基地址。</summary>
        public string BaseUrl => _baseUrl;

        /// <summary>待重试队列长度，可用于监控积压。</summary>
        public int PendingRetryCount
        {
            get { lock (_queueGate) return _retryQueue.Count; }
        }

        /// <summary>
        /// 上报单件检测结论到 <c>/api/quality/record</c>。
        /// </summary>
        public Task<MesReportResult> ReportQualityAsync(
            QualityRecord record, CancellationToken ct = default)
            => PostAsync("/api/quality/record", record, ct);

        /// <summary>
        /// 请求合格件放行。上报结论之后再调用，避免放行早于记录。
        /// </summary>
        public Task<MesReportResult> PassAsync(
            QualityRecord record, CancellationToken ct = default)
            => PostAsync("/api/quality/pass", record, ct);

        /// <summary>
        /// 请求不合格件剔除。
        /// </summary>
        public Task<MesReportResult> FailAsync(
            QualityRecord record, CancellationToken ct = default)
            => PostAsync("/api/quality/fail", record, ct);

        private async Task<MesReportResult> PostAsync(
            string path, QualityRecord record, CancellationToken ct)
        {
            var payload = BuildPayload(record);

            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                var result = await TryOnceAsync(path, payload, ct).ConfigureAwait(false);

                if (result.State != MesReportState.Failed || attempt == _maxAttempts)
                    return result with { Attempt = attempt };

                // 进重试队列，由后台串行消费，避免阻塞采集线程
                Enqueue(new PendingItem(record, attempt + 1));

                var backoff = TimeSpan.FromMilliseconds(
                    _initialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }

            return new MesReportResult(MesReportState.Failed, 0, "exhausted retries", _maxAttempts);
        }

        private async Task<MesReportResult> TryOnceAsync(
            string path, string json, CancellationToken ct)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
                {
                    Content = content,
                };
                request.Headers.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                body = Truncate(body, 512);

                if (response.IsSuccessStatusCode)
                    return new MesReportResult(MesReportState.Accepted, (int)response.StatusCode, body, 1);

                // 4xx 通常是请求本身有问题，重试无意义；5xx 才是服务端暂时故障
                bool retryable = (int)response.StatusCode >= 500;
                return new MesReportResult(
                    retryable ? MesReportState.Failed : MesReportState.Rejected,
                    (int)response.StatusCode, body, 1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 调用方主动取消，不算失败
                throw;
            }
            catch (Exception ex)
            {
                return new MesReportResult(MesReportState.Failed, 0, ex.Message, 1);
            }
        }

        /// <summary>
        /// 组装上报报文。
        ///
        /// 字段名用 snake_case，与网关约定一致；这里显式手写而非依赖
        /// 序列化策略，避免将来改动 <see cref="JsonSerializerOptions"/> 时
        /// 悄悄改变线上报文的字段名。
        /// </summary>
        public static string BuildPayload(QualityRecord r) => JsonSerializer.Serialize(new
        {
            trace_id = r.TraceId,
            workpiece_id = r.EffectiveWorkpieceId,
            passed = r.Passed,
            defect_code = r.DefectCode,
            confidence = Math.Round(r.Confidence, 4),
            timestamp_utc = r.EffectiveTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        });

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

        private void Enqueue(PendingItem item)
        {
            lock (_queueGate) _retryQueue.Enqueue(item);
            try { _retrySignal.Release(); } catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// 后台重试循环：串行消费队列，失败则放回队尾。
        /// 不抛异常——采集线程不应因为网关问题而中断。
        /// </summary>
        private async Task RetryLoopAsync(CancellationToken ct)
        {
            var payloadCache = new Dictionary<string, string>();

            while (!ct.IsCancellationRequested)
            {
                PendingItem item;
                lock (_queueGate)
                {
                    if (_retryQueue.Count == 0)
                    {
                        item = default;
                        payloadCache.Clear();
                    }
                    else
                    {
                        item = _retryQueue.Dequeue();
                    }
                }

                if (payloadCache.Count == 0 && item.Record is null)
                {
                    try { await _retrySignal.WaitAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                string path = item.Record!.Passed ? "/api/quality/pass" : "/api/quality/fail";
                if (!payloadCache.TryGetValue(item.Record.TraceId, out string? json))
                {
                    json = BuildPayload(item.Record);
                    payloadCache[item.Record.TraceId] = json;
                }

                var result = await TryOnceAsync(path, json, ct).ConfigureAwait(false);

                if (result.State == MesReportState.Failed && item.Attempt < _maxAttempts)
                {
                    Enqueue(item with { Attempt = item.Attempt + 1 });
                }

                // Accepted / Rejected / 超过次数上限：都算处理完毕，丢弃
                if (result.State != MesReportState.Failed)
                    payloadCache.Remove(item.Record.TraceId);

                await Task.Delay(_initialBackoff, ct).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _retryCts?.Cancel();

            try { _retrySignal.Release(); } catch { }
            try { _retryTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

            _retryCts?.Dispose();
            _retrySignal.Dispose();

            // 只释放自己创建的 HttpClient；外部传入的由调用方管理
            if (_ownsHttp) _http.Dispose();
        }
    }
}