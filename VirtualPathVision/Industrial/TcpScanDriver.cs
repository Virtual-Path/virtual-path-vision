using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VirtualPathVision.Industrial
{
    /// <summary>
    /// TCP 扫码源驱动。
    /// 监听指定端口，接收网络条码枪 / RFID 网关推送的扫码数据
    /// （每行一条，回车/换行结尾），支持多客户端同时连接。
    /// </summary>
    public class TcpScanDriver : IScanSource
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly List<TcpClient> _clients = new();

        public string Name => "TCP 网络";

        /// <summary>监听端口</summary>
        public int Port { get; set; } = 9001;

        public bool IsRunning { get; private set; }

        public event Action<string>? OnBarcodeScanned;
        public event Action<string>? OnError;

        /// <summary>启动 TCP 监听</summary>
        public Task<bool> StartAsync()
        {
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
                IsRunning = true;
                _ = AcceptLoopAsync(_cts.Token);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                IsRunning = false;
                OnError?.Invoke(ex.Message);
                return Task.FromResult(false);
            }
        }

        /// <summary>停止监听并断开所有客户端</summary>
        public void Stop()
        {
            IsRunning = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            lock (_clients)
            {
                foreach (var c in _clients)
                {
                    try { c.Close(); } catch { }
                }
                _clients.Clear();
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(ct);
                }
                catch
                {
                    break;
                }
                lock (_clients) _clients.Add(client);
                _ = HandleClientAsync(client, ct);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new StringBuilder();
                byte[] buf = new byte[256];
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf, ct);
                    if (n <= 0) break;
                    foreach (char ch in Encoding.ASCII.GetString(buf, 0, n))
                    {
                        if (ch == '\r' || ch == '\n')
                        {
                            if (buffer.Length > 0)
                            {
                                string code = buffer.ToString().Trim();
                                buffer.Clear();
                                if (code.Length > 0)
                                    OnBarcodeScanned?.Invoke(code);
                            }
                        }
                        else
                        {
                            buffer.Append(ch);
                        }
                    }
                }
            }
            catch
            {
                // 客户端断开或监听停止
            }
            finally
            {
                try { client.Close(); } catch { }
                lock (_clients) _clients.Remove(client);
            }
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
