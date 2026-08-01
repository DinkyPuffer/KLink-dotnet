using System.Net;
using System.Net.Sockets;

namespace KLink.App.Services;

/// <summary>
/// TCP 透明代理（ProxyServer）。
/// 监听 127.0.0.1:5231，将 HTTP/WebSocket 流量双向转发到远程服务器（纯字节隧道，不解析协议）。
/// </summary>
public sealed class ProxyServer
{
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly int _listenPort;

    private volatile bool _running;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly List<TcpClient> _clients = new();
    private readonly object _lock = new();

    public ProxyServer(string remoteHost, int remotePort, int listenPort = 5231)
    {
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _listenPort = listenPort;
    }

    public bool IsRunning => _running;

    public string RemoteHost => _remoteHost;
    public int RemotePort => _remotePort;

    /// <summary>启动代理（异步 accept 循环）。</summary>
    public void Start()
    {
        if (_running)
            return;
        _listener = new TcpListener(IPAddress.Loopback, _listenPort);
        _listener.Start(50);
        _running = true;
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        LogService.Instance.Info($"代理已启动：127.0.0.1:{_listenPort} → {_remoteHost}:{_remotePort}");
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* 忽略 */ }
        lock (_lock)
        {
            foreach (var client in _clients)
            {
                try { client.Close(); } catch { /* 忽略 */ }
            }
            _clients.Clear();
        }
        LogService.Instance.Info("代理已停止");
    }

    private async Task AcceptLoop(CancellationToken token)
    {
        while (_running && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                break;
            }
            lock (_lock)
            {
                _clients.Add(client);
            }
            _ = Task.Run(() => HandleConnection(client));
        }
    }

    private async Task HandleConnection(TcpClient client)
    {
        TcpClient? remote = null;
        try
        {
            client.NoDelay = true;
            remote = new TcpClient { NoDelay = true };
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await remote.ConnectAsync(_remoteHost, _remotePort, timeoutCts.Token);

            var clientStream = client.GetStream();
            var remoteStream = remote.GetStream();

            // 双向转发（一方 EOF 即关闭另一方）
            var c2r = ForwardAsync(clientStream, remoteStream, "C→R");
            var r2c = ForwardAsync(remoteStream, clientStream, "R→C");
            await Task.WhenAny(c2r, r2c);
        }
        catch
        {
            // 连接中断是正常情况（游戏关闭、网络波动等）
        }
        finally
        {
            try { remote?.Close(); } catch { /* 忽略 */ }
            try { client.Close(); } catch { /* 忽略 */ }
            lock (_lock)
            {
                _clients.Remove(client);
            }
        }
    }

    private static async Task ForwardAsync(NetworkStream from, NetworkStream to, string direction)
    {
        var buffer = new byte[32768];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                await to.FlushAsync();
            }
        }
        catch
        {
            // 一方断开
        }
        finally
        {
            try { to.Close(); } catch { /* 忽略 */ }
            LogService.Instance.Write("PROXY", $"连接关闭（{direction}）");
        }
    }
}
