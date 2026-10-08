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
    /// <remarks>
    /// 字段与网关侧 <c>com.mes.quality.dto.CreateQualityRecordDTO</c> 一一对应。
    /// 该 DTO 未配置 Jackson 命名策略，因此用<b>camelCase</b> 而非 snake_case。
    /// </remarks>
    /// <param name="Sn">产品序列号（DTO 的 <c>sn</c>，最长 100）</param>
    /// <param name="Passed">是否合格，映射为 <c>checkResult</c></param>
    /// <param name="DefectType">缺陷分类（DTO 的 <c>defectType</c>，最长 50）</param>
    /// <param name="DefectDesc">缺陷描述（DTO 的 <c>defectDesc</c>，最长 500）</param>
    /// <param name="Remark">备注（DTO 的 <c>remark</c>，最长 500）</param>
    /// <param name="WorkOrderNo">工单号（DTO 的 <c>workOrderNo</c>，最长 50）</param>
    /// <param name="CheckType">
    /// 检测类型，<b>必填</b>。DTO 用 <c>@Pattern</c> 限定为
    /// <c>IPQC | FQC | OQC | 巡检 | 首检 | 终检</c>，填错会被 <c>@Valid</c> 拒为 400。
    /// </param>
    /// <param name="DeviceId">设备 ID，DTO 为 <c>Long</c></param>
    /// <param name="WorkstationId">工位 ID，DTO 为 <c>Long</c></param>
    /// <param name="OperatorId">操作员 ID，DTO 为 <c>Long</c></param>
    public sealed record QualityRecord(
        string Sn,
        bool Passed,
        string? DefectType = null,
        string? DefectDesc = null,
        string? Remark = null,
        string? WorkOrderNo = null,
        string CheckType = MesClient.DefaultCheckType,
        long? DeviceId = null,
        long? WorkstationId = null,
        long? OperatorId = null);

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
    /// <param name="RecordId">
    /// 网关分配的质量记录主键，由 <c>POST /record</c> 的响应 <c>data</c> 字段带回。
    /// 放行/剔除接口是 <c>/record/{id}/pass</c> 与 <c>/record/{id}/fail</c>，
    /// 必须先拿到这个 id 才能调用。
    /// </param>
    public sealed record MesReportResult(
        MesReportState State,
        int StatusCode,
        string? ResponseBody,
        int Attempt,
        long? RecordId = null)
    {
        public bool Success => State == MesReportState.Accepted;
    }

    /// <summary>
    /// MES 网关客户端。
    ///
    /// <para><b>协议来源</b>：网关 <c>virtual-path-mes/mes-gateway</c>（端口 9090）把
    /// <c>/api/quality/**</c> 以 <c>StripPrefix=1</c> 转发到 <c>mes-quality:8084</c>，
    /// 因此对外路径是 <c>/api/quality/...</c>，后端实际收到 <c>/quality/...</c>。
    /// 端点定义在 <c>QualityController</c>：
    /// <list type="bullet">
    /// <item><c>POST /api/quality/record</c> —— 建记录，返回 <c>Result&lt;Long&gt;</c>（新记录 id）</item>
    /// <item><c>POST /api/quality/record/{id}/pass</c> —— 放行，无请求体</item>
    /// <item><c>POST /api/quality/record/{id}/fail?reason=...</c> —— 剔除，
    ///       <c>reason</c> 是<b>必填</b>的查询参数</item>
    /// </list></para>
    ///
    /// <para><b>鉴权</b>：网关的 <c>JwtAuthGlobalFilter</c> 白名单只有
    /// <c>/api/auth/login</c>、<c>/api/auth/register</c>、<c>/actuator/**</c>。
    /// quality 路径<b>不在白名单</b>，缺少 <c>Authorization: Bearer &lt;token&gt;</c>
    /// 会直接 401。</para>
    ///
    /// <para><b>失败处理</b>：网络异常与非 2xx 都视为失败并进入指数退避重试。
    /// 产线不能因为网关暂时不可用就停机，因此客户端<b>不阻塞</b>采集线程——
    /// 所有方法都是 <c>Task</c>，由调用方决定是否等待。重试在后台队列中进行。</para>
    /// </summary>
    public sealed class MesClient : IDisposable
    {
        /// <summary>
        /// 默认检测类型。视觉工位属于工序内检验，故取 <c>IPQC</c>。
        /// </summary>
        public const string DefaultCheckType = "IPQC";

        /// <summary>
        /// <c>CreateQualityRecordDTO.checkType</c> 的 <c>@Pattern</c> 允许值。
        /// 不在此列表内的输入会退回 <see cref="DefaultCheckType"/>，
        /// 否则整条记录会被 <c>@Valid</c> 拒为 400。
        /// </summary>
        private static readonly string[] AllowedCheckTypes =
            { "IPQC", "FQC", "OQC", "巡检", "首检", "终检" };

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _baseUrl;
        private readonly string? _token;
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
        /// <param name="baseUrl">网关基地址，如 <c>http://localhost:9090</c>（尾部斜杠会被去掉）</param>
        /// <param name="token">
        /// JWT。quality 路径受全局鉴权过滤器保护，为空将导致所有上报 401。
        /// </param>
        /// <param name="maxAttempts">含首次请求的最大尝试次数</param>
        /// <param name="initialBackoff">首次重试前的等待时长，之后按 2 倍递增</param>
        /// <param name="httpClient">
        /// 复用外部 <see cref="HttpClient"/> 时传入，此时不接管其生命周期。
        /// 生产环境应复用同一实例以复用连接池。
        /// </param>
        public MesClient(
            string baseUrl,
            string? token = null,
            int maxAttempts = 3,
            TimeSpan? initialBackoff = null,
            HttpClient? httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("baseUrl 不能为空", nameof(baseUrl));

            _baseUrl = baseUrl.TrimEnd('/');
            _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
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

        /// <summary>是否携带了鉴权令牌。未携带时上报必然 401。</summary>
        public bool HasToken => _token != null;

        /// <summary>待重试队列长度，可用于监控积压。</summary>
        public int PendingRetryCount
        {
            get { lock (_queueGate) return _retryQueue.Count; }
        }

        /// <summary>
        /// 上报单件检测结论到 <c>/api/quality/record</c>。
        /// </summary>
        /// <remarks>
        /// 成功时 <see cref="MesReportResult.RecordId"/> 携带网关分配的记录 id，
        /// 后续 <see cref="PassAsync"/> / <see cref="FailAsync"/> 依赖它。
        /// </remarks>
        public Task<MesReportResult> ReportQualityAsync(
            QualityRecord record, CancellationToken ct = default)
            => PostRecordAsync(record, ct);

        /// <summary>
        /// 请求合格件放行。<paramref name="recordId"/> 取自
        /// <see cref="ReportQualityAsync"/> 的返回值。端点无请求体。
        /// </summary>
        public Task<MesReportResult> PassAsync(
            long recordId, CancellationToken ct = default)
            => PostVoidAsync($"/api/quality/record/{recordId}/pass", null, ct);

        /// <summary>
        /// 请求不合格件剔除。
        /// </summary>
        /// <param name="recordId">取自 <see cref="ReportQualityAsync"/> 的返回值</param>
        /// <param name="reason">
        /// 剔除原因。端点声明为 <c>@RequestParam String reason</c>，
        /// <b>必填</b>——留空会被 Spring 拒为 400。
        /// </param>
        public Task<MesReportResult> FailAsync(
            long recordId, string reason, CancellationToken ct = default)
        {
            // 查询参数必须 URL 编码：缺陷描述里可能有空格、斜杠、中文
            string urlEncoded = Uri.EscapeDataString(
                string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim());

            return PostVoidAsync(
                $"/api/quality/record/{recordId}/fail?reason={urlEncoded}", null, ct);
        }

        private async Task<MesReportResult> PostRecordAsync(
            QualityRecord record, CancellationToken ct)
        {
            var payload = BuildPayload(record);

            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                var result = await TryOnceAsync("/api/quality/record", payload, ct)
                    .ConfigureAwait(false);

                if (result.State != MesReportState.Failed || attempt == _maxAttempts)
                    return result with { Attempt = attempt };

                Enqueue(new PendingItem(record, attempt + 1));

                var backoff = TimeSpan.FromMilliseconds(
                    _initialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }

            return new MesReportResult(MesReportState.Failed, 0, "exhausted retries", _maxAttempts);
        }

        private async Task<MesReportResult> PostVoidAsync(
            string path, string? json, CancellationToken ct)
        {
            for (int attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                var result = await TryOnceAsync(path, json, ct).ConfigureAwait(false);

                if (result.State != MesReportState.Failed || attempt == _maxAttempts)
                    return result with { Attempt = attempt };

                var backoff = TimeSpan.FromMilliseconds(
                    _initialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }

            return new MesReportResult(MesReportState.Failed, 0, "exhausted retries", _maxAttempts);
        }

        private async Task<MesReportResult> TryOnceAsync(
            string path, string? json, CancellationToken ct)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path);

                if (json != null)
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                request.Headers.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                if (_token != null)
                {
                    // 网关 JwtAuthGlobalFilter 只认 "Bearer " 前缀，
                    // 缺这个头会直接 401，且不会被路由到后端。
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
                }

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);

                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                body = Truncate(body, 512);

                int status = (int)response.StatusCode;

                if (!response.IsSuccessStatusCode)
                {
                    // 4xx 通常是请求本身有问题，重试无意义；5xx 才是服务端暂时故障
                    bool retryable = status >= 500;
                    return new MesReportResult(
                        retryable ? MesReportState.Failed : MesReportState.Rejected,
                        status, body, 1);
                }

                // HTTP 200 不等于业务成功：网关统一返回 {code,message,data,timestamp}，
                // code != 200 是业务失败，不应重试，也不应被当成"上报成功"。
                var envelope = ParseEnvelope(body);
                if (envelope.Code != 0 && envelope.Code != 200)
                {
                    return new MesReportResult(
                        MesReportState.Rejected, status, body, 1, RecordId: null);
                }

                return new MesReportResult(
                    MesReportState.Accepted, status, body, 1, envelope.DataId);
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
        /// 解析 <c>Result</c> 响应封装，取出 <c>code</c> 与 <c>data</c>（记录 id）。
        /// </summary>
        /// <remarks>
        /// 非 JSON 或结构不符时 <c>Code</c> 返回 0，语义上等同"成功但无 id"——
        /// 因为 HTTP 层已经是 2xx，此时不该把它误判成业务失败。
        /// </remarks>
        private static (int Code, long? DataId) ParseEnvelope(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return (0, null);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                int code = 0;
                if (root.TryGetProperty("code", out var codeEl)
                    && codeEl.ValueKind == JsonValueKind.Number
                    && codeEl.TryGetInt32(out int c))
                {
                    code = c;
                }

                long? id = null;
                if (root.TryGetProperty("data", out var dataEl)
                    && dataEl.ValueKind == JsonValueKind.Number
                    && dataEl.TryGetInt64(out long d))
                {
                    id = d;
                }

                return (code, id);
            }
            catch (JsonException)
            {
                // 网关前置（如反向代理）可能返回非 JSON 的错误页
                return (0, null);
            }
        }

        /// <summary>
        /// 组装上报报文。
        ///
        /// <para>字段名与 <c>CreateQualityRecordDTO</c> 完全一致（camelCase）。
        /// 这里显式手写而非依赖序列化策略，避免将来改动
        /// <see cref="JsonSerializerOptions"/> 时悄悄改变线上报文的字段名。</para>
        ///
        /// <para>字符串长度按 DTO 的 <c>@Size</c> 截断：超长会让 <c>@Valid</c> 失败并返回 400，
        /// 而截断是静默的——宁可少几个字符，也不要整条记录被拒。</para>
        /// </summary>
        public static string BuildPayload(QualityRecord r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));

            if (string.IsNullOrWhiteSpace(r.Sn))
                throw new ArgumentException("Sn 不能为空：网关按 sn 建立产品追溯", nameof(r));

            // checkType 是 @NotBlank + @Pattern，只接受白名单值。
            string checkType = Array.IndexOf(AllowedCheckTypes, r.CheckType) >= 0
                ? r.CheckType
                : DefaultCheckType;

            // DTO 的 @Pattern 同时接受 PASSED/FAILED/REWORK 与 PASS/FAIL，
            // 但统一用 PASSED/FAILED 以与实体枚举保持一致。
            string checkResult = r.Passed ? "PASSED" : "FAILED";

            return JsonSerializer.Serialize(new
            {
                sn = Clip(r.Sn, 100),
                workOrderNo = r.WorkOrderNo is null ? null : Clip(r.WorkOrderNo, 50),
                checkType,
                checkResult,
                defectType = r.DefectType is null ? null : Clip(r.DefectType, 50),
                defectDesc = r.DefectDesc is null ? null : Clip(r.DefectDesc, 500),
                remark = r.Remark is null ? null : Clip(r.Remark, 500),
                deviceId = r.DeviceId,
                workstationId = r.WorkstationId,
                operatorId = r.OperatorId,
            });
        }

        private static string Clip(string s, int max)
            => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);

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
        /// <remarks>
        /// 只重试<b>建记录</b>。放行/剔除依赖建记录返回的 id，
        /// 建记录没成功就没有 id，动作请求无法构造，因此不入队。
        /// </remarks>
        private async Task RetryLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                PendingItem item;
                lock (_queueGate)
                {
                    if (_retryQueue.Count == 0)
                    {
                        item = default;
                    }
                    else
                    {
                        item = _retryQueue.Dequeue();
                    }
                }

                if (item.Record is null)
                {
                    try { await _retrySignal.WaitAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                string payload = BuildPayload(item.Record);
                var result = await TryOnceAsync("/api/quality/record", payload, ct)
                    .ConfigureAwait(false);

                if (result.State == MesReportState.Failed && item.Attempt < _maxAttempts)
                {
                    Enqueue(item with { Attempt = item.Attempt + 1 });
                }

                try { await Task.Delay(_initialBackoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
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