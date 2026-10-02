using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace VirtualPathVision.Industrial
{
    /// <summary>
    /// OPC-UA 客户端驱动。
    /// 基于 OPC Foundation 官方 .NET 协议栈（1.5.x），全部使用异步 API。
    /// 支持匿名/证书安全连接与节点读写，用于与支持 OPC-UA 的 PLC
    /// （西门子、倍福等）直连。
    /// </summary>
    /// <remarks>
    /// 线程安全约定：<c>_sync</c> 保护 Session 的安装/摘除，<c>_connectGate</c> 保证
    /// 同一时刻只有一条连接流程，因此不会出现被覆盖而无人关闭的孤儿 Session。
    /// <see cref="Disconnect"/> 为非阻塞版本（不占用调用线程，最长可占用 DefaultSessionTimeout），
    /// 真实关闭在后台串行完成，<see cref="Dispose"/> 会带超时地等待收尾。
    /// </remarks>
    public class OpcUaDriver : IDeviceDriver
    {
        /// <summary>Dispose 时等待后台关闭完成的最长时间（毫秒）</summary>
        private const int CloseWaitTimeoutMs = 5000;

        private readonly object _sync = new();
        private readonly SemaphoreSlim _connectGate = new(1, 1);

        private string _endpointUrl;
        private Session? _session;
        private Task? _pendingClose;
        private DeviceDriverState _state = DeviceDriverState.Disconnected;
        private bool _disposed;

        public string Name => "OPC-UA";

        /// <summary>OPC-UA 端点地址（opc.tcp://ip:4840）</summary>
        public string EndpointUrl
        {
            get { lock (_sync) return _endpointUrl; }
        }

        public DeviceDriverState State
        {
            get { lock (_sync) return _state; }
        }

        public event Action<DeviceDriverState>? OnStateChanged;
        public event Action<string>? OnError;

        public OpcUaDriver(string endpointUrl)
        {
            _endpointUrl = ValidateEndpoint(endpointUrl, nameof(endpointUrl));
        }

        /// <summary>
        /// 是否自动接受不受信任的服务器证书。
        /// 默认 true 保持现场联调便利；置为 false 后证书链、主机名、有效期等问题一律拒绝。
        /// 安全提示：开启后不再校验服务器身份，链路被劫持时无法察觉，仅建议在隔离产线或调试期使用。
        /// </summary>
        public bool AutoAcceptUntrustedCertificates { get; set; } = true;

        /// <summary>更新端点地址（先断开再应用）</summary>
        public void UpdateSettings(string endpointUrl)
        {
            string newUrl = ValidateEndpoint(endpointUrl, nameof(endpointUrl));

            Disconnect();

            lock (_sync)
            {
                _endpointUrl = newUrl;
            }
        }

        private static string ValidateEndpoint(string endpointUrl, string paramName)
        {
            if (string.IsNullOrWhiteSpace(endpointUrl))
                throw new ArgumentOutOfRangeException(paramName, endpointUrl, "OPC-UA 端点地址不能为空");
            if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out _))
                throw new ArgumentOutOfRangeException(paramName, endpointUrl, $"无效的 OPC-UA 端点地址: {endpointUrl}");
            return endpointUrl.Trim();
        }

        /// <summary>异步连接 OPC-UA 服务器</summary>
        public async Task<bool> ConnectAsync()
        {
            await _connectGate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_state == DeviceDriverState.Connected)
                        return true;
                }

                SetState(DeviceDriverState.Connecting);

                ApplicationConfiguration? config = null;
                Session? session = null;
                try
                {
                    bool autoAccept = AutoAcceptUntrustedCertificates;

                    config = new ApplicationConfiguration
                    {
                        ApplicationName = "VirtualPathVision",
                        ApplicationUri = "urn:machinevisionapp:client",
                        ApplicationType = ApplicationType.Client,
                        SecurityConfiguration = new SecurityConfiguration
                        {
                            ApplicationCertificate = new CertificateIdentifier
                            {
                                StoreType = "Directory",
                                StorePath = "%CommonApplicationData%/VirtualPathVision/pki/own",
                                SubjectName = "CN=VirtualPathVision, O=VirtualPathVision, DC=localhost"
                            },
                            TrustedPeerCertificates = new CertificateTrustList
                            {
                                StoreType = "Directory",
                                StorePath = "%CommonApplicationData%/VirtualPathVision/pki/trusted"
                            },
                            TrustedIssuerCertificates = new CertificateTrustList
                            {
                                StoreType = "Directory",
                                StorePath = "%CommonApplicationData%/VirtualPathVision/pki/issuer"
                            },
                            RejectedCertificateStore = new CertificateTrustList
                            {
                                StoreType = "Directory",
                                StorePath = "%CommonApplicationData%/VirtualPathVision/pki/rejected"
                            },
                            AutoAcceptUntrustedCertificates = autoAccept,
                            AddAppCertToTrustedStore = true
                        },
                        TransportConfigurations = new TransportConfigurationCollection(),
                        TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                        ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
                        TraceConfiguration = new Opc.Ua.TraceConfiguration()
                    };

#pragma warning disable CS0618 // OPC UA 1.5.x 将以下 API 标记为过时，其替代品要求传 ITelemetryContext；
                    // 为保持现有连接行为不变，此处继续使用旧 API 并统一抑制告警
                    await config.Validate(ApplicationType.Client).ConfigureAwait(false);

                    // 自动创建/校验应用证书（静默模式）
                    var application = new ApplicationInstance(config);
                    bool certOk = await application.CheckApplicationInstanceCertificates(true, 0).ConfigureAwait(false);
                    if (!certOk)
                        throw new Exception("OPC-UA 应用证书无效");

                    config = application.ApplicationConfiguration;
                    config.ApplicationUri =
                        X509Utils.GetApplicationUrisFromCertificate(
                            config.SecurityConfiguration.ApplicationCertificate.Certificate)
                        .FirstOrDefault() ?? config.ApplicationUri;

                    if (autoAccept)
                    {
                        // 仅对“证书不受信任”系列状态强制放行，其余状态保留校验器自身的判定结果
                        config.CertificateValidator.CertificateValidation += (_, e) =>
                        {
                            if (IsUntrustedCertificateStatus(e.Error.StatusCode))
                                e.Accept = true;
                        };
                    }

                    // 选择无安全策略端点（None），用户名密码/证书策略可按需扩展
                    EndpointDescription endpoint = await CoreClientUtils.SelectEndpointAsync(
                        config, EndpointUrl, false, 15000, CancellationToken.None).ConfigureAwait(false);
                    var configuredEndpoint = new ConfiguredEndpoint(
                        null, endpoint, EndpointConfiguration.Create(config));

                    session = (Session)await new DefaultSessionFactory().CreateAsync(
                        config, configuredEndpoint, false,
                        "VirtualPathVision Session", 60000, null, null, CancellationToken.None).ConfigureAwait(false);
#pragma warning restore CS0618

                    // 原 _session 若非空（极端竞态）也要走同一关闭队列，避免会话泄漏
                    QueueClose(session);
                }
                catch (Exception ex)
                {
                    DisposeQuietly(session, "会话");
                    SetState(DeviceDriverState.Failed);
                    OnError?.Invoke(ex.Message);
                    return false;
                }

                SetState(DeviceDriverState.Connected);
                return true;
            }
            finally
            {
                _connectGate.Release();
            }
        }

        /// <summary>
        /// 判断状态码是否属于“不受信任的证书”系列：信任链不完整、主机名/有效期不匹配、
        /// 吊销状态未知等。这些在自签名证书或现场临时证书（未部署吊销检查）的 PLC 上很常见，
        /// 自动接受模式下统一放行；真正表示证书本身无效的
        /// （BadCertificateInvalid / BadCertificateRevoked 等）不在此列，保持拒绝。
        /// </summary>
        private static bool IsUntrustedCertificateStatus(StatusCode status)
        {
            return status == StatusCodes.BadCertificateUntrusted
                || status == StatusCodes.BadCertificateChainIncomplete
                || status == StatusCodes.BadCertificateHostNameInvalid
                || status == StatusCodes.BadCertificateTimeInvalid
                || status == StatusCodes.BadCertificateIssuerTimeInvalid
                || status == StatusCodes.BadCertificateUriInvalid
                || status == StatusCodes.BadCertificateRevocationUnknown
                || status == StatusCodes.BadCertificateIssuerRevocationUnknown;
        }

        /// <summary>
        /// 断开连接。真实关闭为异步且可能耗时（最长 DefaultSessionTimeout），
        /// 此处仅同步摘除 _session 并把关闭排入后台队列，绝不阻塞调用线程（UI 线程）。
        /// </summary>
        public void Disconnect()
        {
            Session? session;
            lock (_sync)
            {
                session = _session;
                _session = null;
            }
            QueueClose(session);
            SetState(DeviceDriverState.Disconnected);
        }

        /// <summary>把会话关闭排入串行后台队列（可重复调用，传入 null 时为空操作）</summary>
        private void QueueClose(Session? session)
        {
            if (session == null)
                return;

            Task previous;
            Task next;
            lock (_sync)
            {
                previous = _pendingClose ?? Task.CompletedTask;
                next = CloseSessionAsync(session, previous);
                _pendingClose = next;
            }
        }

        private static async Task CloseSessionAsync(Session session, Task previous)
        {
            // 强制异步：QueueClose 在 _sync 内调用本方法，
            // 若同步段执行到 CloseAsync 就会把会话关闭的网络 I/O 带进锁里
            await Task.Yield();

            // 串行化关闭：避免并发操作同一 Session，并让 Dispose 的等待能覆盖全部关闭动作
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpcUaDriver] 前序关闭任务异常: {ex.Message}");
            }

            try
            {
                await session.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpcUaDriver] 关闭会话失败: {ex.Message}");
            }
            finally
            {
                // CloseAsync 抛异常时也必须释放，否则会话与套接字泄漏
                try
                {
                    session.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OpcUaDriver] 释放会话失败: {ex.Message}");
                }
            }
        }

        private static void DisposeQuietly(Session? session, string what)
        {
            if (session == null)
                return;
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpcUaDriver] 释放{what}失败: {ex.Message}");
            }
        }

        /// <summary>获取当前会话快照（在锁内取局部变量，避免 await 期间被 Disconnect 置空）</summary>
        private Session GetSessionSnapshot()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _session ?? throw new InvalidOperationException("OPC-UA 未连接");
            }
        }

        /// <summary>异步读取节点值（如 ns=2;s=Temperature）</summary>
        public async Task<string?> ReadNodeAsync(string nodeId)
        {
            NodeId parsed = NodeId.Parse(nodeId);
            Session session = GetSessionSnapshot();

            DataValue value = await session.ReadValueAsync(
                parsed, CancellationToken.None).ConfigureAwait(false);
            return value?.Value?.ToString() ?? "null";
        }

        /// <summary>异步写入节点值</summary>
        public async Task WriteNodeAsync(string nodeId, object value)
        {
            NodeId parsed = NodeId.Parse(nodeId);
            Session session = GetSessionSnapshot();

            var nodesToWrite = new WriteValueCollection
            {
                new WriteValue
                {
                    NodeId = parsed,
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                }
            };
            WriteResponse response = await session.WriteAsync(
                null, nodesToWrite, CancellationToken.None).ConfigureAwait(false);

            if (response.Results == null || response.Results.Count == 0)
                throw new InvalidOperationException($"写入失败: 服务器未返回结果项 ({nodeId})");
            if (StatusCode.IsBad(response.Results[0]))
                throw new InvalidOperationException($"写入失败: {response.Results[0]}");
        }

        private void SetState(DeviceDriverState state)
        {
            lock (_sync)
            {
                if (_state == state)
                    return;
                _state = state;
            }
            OnStateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            Disconnect();

            Task? pending;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                pending = _pendingClose;
                _pendingClose = null;
            }

            // 带上限地等待后台关闭，避免应用退出时被 Session 关闭长期阻塞
            if (pending != null)
            {
                try
                {
                    if (!pending.Wait(CloseWaitTimeoutMs))
                        Debug.WriteLine($"[OpcUaDriver] 等待会话关闭超过 {CloseWaitTimeoutMs}ms，放弃等待");
                }
                catch (AggregateException ex)
                {
                    Debug.WriteLine($"[OpcUaDriver] 等待会话关闭失败: {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            _connectGate.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
