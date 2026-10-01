using System;
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
    public class OpcUaDriver : IDeviceDriver
    {
        private string _endpointUrl;
        private Session? _session;
        private ApplicationConfiguration? _config;

        public string Name => "OPC-UA";

        /// <summary>OPC-UA 端点地址（opc.tcp://ip:4840）</summary>
        public string EndpointUrl => _endpointUrl;

        public DeviceDriverState State { get; private set; } = DeviceDriverState.Disconnected;

        public event Action<DeviceDriverState>? OnStateChanged;
        public event Action<string>? OnError;

        public OpcUaDriver(string endpointUrl)
        {
            _endpointUrl = endpointUrl;
        }

        /// <summary>更新端点地址（先断开再应用）</summary>
        public void UpdateSettings(string endpointUrl)
        {
            Disconnect();
            _endpointUrl = endpointUrl;
        }

        /// <summary>异步连接 OPC-UA 服务器</summary>
        public async Task<bool> ConnectAsync()
        {
            if (State == DeviceDriverState.Connected)
                return true;

            SetState(DeviceDriverState.Connecting);
            try
            {
                _config = new ApplicationConfiguration
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
                        AutoAcceptUntrustedCertificates = true,
                        AddAppCertToTrustedStore = true
                    },
                    TransportConfigurations = new TransportConfigurationCollection(),
                    TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                    ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
                    TraceConfiguration = new TraceConfiguration()
                };

                await _config.Validate(ApplicationType.Client);

                // 自动创建/校验应用证书（静默模式）
                var application = new ApplicationInstance(_config);
                bool certOk = await application.CheckApplicationInstanceCertificates(true, 0);
                if (!certOk)
                    throw new Exception("OPC-UA 应用证书无效");

                _config = application.ApplicationConfiguration;
                _config.ApplicationUri =
                    X509Utils.GetApplicationUrisFromCertificate(
                        _config.SecurityConfiguration.ApplicationCertificate.Certificate)
                    .FirstOrDefault() ?? _config.ApplicationUri;

                // 演示模式：自动接受未受信任的服务器证书
                _config.CertificateValidator.CertificateValidation += (sender, e) =>
                {
                    e.Accept = e.Error.StatusCode == StatusCodes.BadCertificateUntrusted;
                };

                // 选择无安全策略端点（None），用户名密码/证书策略可按需扩展
#pragma warning disable CS0618
                EndpointDescription endpoint = await CoreClientUtils.SelectEndpointAsync(
                    _config, _endpointUrl, false, 15000, CancellationToken.None);
#pragma warning restore CS0618
                var configuredEndpoint = new ConfiguredEndpoint(
                    null, endpoint, EndpointConfiguration.Create(_config));

                _session = (Session)await new DefaultSessionFactory().CreateAsync(
                    _config, configuredEndpoint, false,
                    "VirtualPathVision Session", 60000, null, null, CancellationToken.None);

                SetState(DeviceDriverState.Connected);
                return true;
            }
            catch (Exception ex)
            {
                SetState(DeviceDriverState.Failed);
                OnError?.Invoke(ex.Message);
                return false;
            }
        }

        /// <summary>断开连接</summary>
        public void Disconnect()
        {
            if (_session != null)
            {
                try
                {
                    _session.CloseAsync(CancellationToken.None).GetAwaiter().GetResult();
                    _session.Dispose();
                }
                catch { }
            }
            _session = null;
            SetState(DeviceDriverState.Disconnected);
        }

        /// <summary>异步读取节点值（如 ns=2;s=Temperature）</summary>
        public async Task<string?> ReadNodeAsync(string nodeId)
        {
            if (_session == null)
                throw new InvalidOperationException("OPC-UA 未连接");
            DataValue value = await _session.ReadValueAsync(
                NodeId.Parse(nodeId), CancellationToken.None);
            return value?.Value?.ToString() ?? "null";
        }

        /// <summary>异步写入节点值</summary>
        public async Task WriteNodeAsync(string nodeId, object value)
        {
            if (_session == null)
                throw new InvalidOperationException("OPC-UA 未连接");

            var nodesToWrite = new WriteValueCollection
            {
                new WriteValue
                {
                    NodeId = NodeId.Parse(nodeId),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                }
            };
            WriteResponse response = await _session.WriteAsync(
                null, nodesToWrite, CancellationToken.None);
            if (StatusCode.IsBad(response.Results[0]))
                throw new Exception($"写入失败: {response.Results[0]}");
        }

        private void SetState(DeviceDriverState state)
        {
            State = state;
            OnStateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }
    }
}
