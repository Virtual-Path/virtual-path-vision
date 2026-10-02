using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// <remarks>
    /// 安全说明：监听 <see cref="IPAddress.Any"/>，即同网段任意主机都可连接。
    /// 因此单行缓冲设置了硬上限，超限即上报错误并断开该连接，防止内存被撑爆。
    /// </remarks>
    public class TcpScanDriver : IScanSource
    {
        /// <summary>单行扫码数据的最大字节数（含终止符之前的全部内容）</summary>
        public const int MaxLineBytes = 4096;

        private readonly object _sync = new();
        private readonly List<TcpClient> _clients = new();

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private bool _isRunning;
        private bool _disposed;

        public string Name => "TCP 网络";

        /// <summary>监听端口（1..65535，启动时校验）</summary>
        public int Port { get; set; } = 9001;

        public bool IsRunning
        {
            get { lock (_sync) return _isRunning; }
        }

        public event Action<string>? OnBarcodeScanned;
        public event Action<string>? OnError;

        /// <summary>启动 TCP 监听</summary>
        public Task<bool> StartAsync()
        {
            TcpListener? listener = null;
            CancellationTokenSource? cts = null;
            try
            {
                int port = ValidatePort(Port);

                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    // 重复启动保护：否则会覆盖 _cts/_listener，泄漏上一个监听套接字
                    if (_isRunning)
                        return Task.FromResult(true);

                    cts = new CancellationTokenSource();
                    listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();

                    _cts = cts;
                    _listener = listener;
                    _isRunning = true;
                    _acceptTask = AcceptLoopAsync(listener, cts.Token);
                }
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                // 启动失败必须回收已创建的资源，并复位运行标志
                CancelAndDispose(cts);
                StopListener(listener);
                lock (_sync)
                {
                    _cts = null;
                    _listener = null;
                    _acceptTask = null;
                    _isRunning = false;
                }
                OnError?.Invoke(ex.Message);
                return Task.FromResult(false);
            }
        }

        /// <summary>停止监听并断开所有客户端</summary>
        public void Stop()
        {
            CancellationTokenSource? cts;
            TcpListener? listener;
            List<TcpClient> clients;

            lock (_sync)
            {
                _isRunning = false;
                cts = _cts;
                listener = _listener;
                _acceptTask = null;
                _cts = null;
                _listener = null;
                clients = new List<TcpClient>(_clients);
                _clients.Clear();
            }

            CancelAndDispose(cts);
            StopListener(listener);
            foreach (var c in clients)
                CloseClient(c);
        }

        private static int ValidatePort(int port)
        {
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException(
                    nameof(port), port, $"TCP 扫码监听端口必须在 1..65535 之间，当前值: {port}");
            return port;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;   // 正常停止
                    }
                    catch (ObjectDisposedException)
                    {
                        break;   // 监听器已释放
                    }
                    catch (SocketException ex)
                    {
                        // 监听被 Stop() 打断时也可能抛 SocketException，仅在非主动停止时视为故障
                        if (!ct.IsCancellationRequested)
                        {
                            ReportError($"TCP 扫码监听异常退出: {ex.Message}");
                            MarkStopped();
                        }
                        break;
                    }

                    lock (_sync)
                    {
                        _clients.Add(client);
                    }

                    // fire-and-forget：挂接观察器，把未捕获异常显式上报，避免变成 unobserved task
                    var handler = HandleClientAsync(client, ct);
                    _ = handler.ContinueWith(
                        t => ReportError($"扫码客户端处理失败: {t.Exception?.GetBaseException().Message}"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            finally
            {
                MarkStopped();
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using var stream = client.GetStream();

                // 按字节累积，遇到换行才解码：避免按块解码破坏跨块的多字节字符
                var buffer = new List<byte>(256);
                byte[] buf = new byte[1024];
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                    if (n <= 0)
                        break;

                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (b == (byte)'\r' || b == (byte)'\n')
                        {
                            if (buffer.Count > 0)
                            {
                                string code = DecodeLine(buffer);
                                buffer.Clear();
                                RaiseBarcodeScanned(code);
                            }
                        }
                        else
                        {
                            if (buffer.Count >= MaxLineBytes)
                            {
                                // 不再无限增长：上报错误并断开该连接
                                throw new InvalidDataException(
                                    $"扫码数据超过 {MaxLineBytes} 字节且未见行结束符，已断开该连接");
                            }
                            buffer.Add(b);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Stop() 触发的正常取消
            }
            catch (ObjectDisposedException)
            {
                // 监听/连接被释放
            }
            catch (InvalidDataException ex)
            {
                OnError?.Invoke(ex.Message);
            }
            catch (Exception ex)
            {
                // 订阅者回调异常不应让监听循环退出，这里统一上报
                OnError?.Invoke($"扫码客户端处理异常: {ex.Message}");
            }
            finally
            {
                lock (_sync)
                {
                    _clients.Remove(client);
                }
                CloseClient(client);
            }
        }

        private static string DecodeLine(List<byte> buffer)
        {
            // 扫码枪一般为 ASCII/UTF-8；用 UTF-8 且不抛异常，非法字节替换为 U+FFFD
            return Encoding.UTF8.GetString(buffer.ToArray()).Trim();
        }

        private void RaiseBarcodeScanned(string code)
        {
            if (code.Length == 0)
                return;
            try
            {
                OnBarcodeScanned?.Invoke(code);
            }
            catch (Exception ex)
            {
                // 订阅者（UI 派发）异常不能中断读取循环，否则会丢掉后续条码
                ReportError($"条码回调异常: {ex.Message}");
            }
        }

        private void ReportError(string message)
        {
            try
            {
                OnError?.Invoke(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TcpScanDriver] OnError 订阅者异常: {ex.Message}");
            }
        }

        /// <summary>复位运行标志（监听循环退出时调用）</summary>
        private void MarkStopped()
        {
            lock (_sync)
            {
                _isRunning = false;
            }
        }

        private static void CancelAndDispose(CancellationTokenSource? cts)
        {
            if (cts == null)
                return;
            try
            {
                if (!cts.IsCancellationRequested)
                    cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已释放，忽略
            }
            finally
            {
                try { cts.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        private static void StopListener(TcpListener? listener)
        {
            if (listener == null)
                return;
            try
            {
                listener.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TcpScanDriver] 停止监听失败: {ex.Message}");
            }
        }

        private static void CloseClient(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TcpScanDriver] 关闭客户端连接失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_sync)
            {
                _disposed = true;
                _acceptTask = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}
