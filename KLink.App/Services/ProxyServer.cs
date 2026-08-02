using System.Net;
using System.Net.Sockets;

namespace KLink.App.Services;

/// <summary>
/// TCP 透明代理（ProxyServer）。
/// 监听 127.0.0.1:5231（HTTP）与 127.0.0.1:5232（WebSocket），
/// 分别双向转发到远程服务器 remoteHost:remoteHttpPort / remoteHttpPort+1（纯字节隧道，不解析协议）。
/// 游戏客户端固定连本地 5231/5232，与本地/局域网模式端口一致。
/// </summary>
public sealed class ProxyServer
{
    private readonly string _remoteHost;
    private readonly int _remoteHttpPort;
    private readonly int _remoteWsPort;
    private readonly int _listenHttpPort;
    private readonly int _listenWsPort;
    private readonly bool _enableWs;

    private volatile bool _running;
    private TcpListener? _listener;
    private TcpListener? _wsListener;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _tasks = new();
    private readonly List<TcpClient> _clients = new();
    private readonly object _lock = new();

    /// <param name="remoteHost">远程服务器地址。</param>
    /// <param name="remoteHttpPort">远程 HTTP 端口。</param>
    /// <param name="listenPort">本地 HTTP 监听端口（默认 5231）。</param>
    /// <param name="enableWs">是否同时代理 WebSocket 端口（默认 true，游戏对战必需）。</param>
    /// <param name="remoteWsPort">远程 WebSocket 端口（默认 5232，KARDS 服务端标准 WS 端口）。</param>
    /// <param name="listenWsPort">本地 WebSocket 监听端口（默认 = 远程 WS 端口，因客户端按服务器返回的 websocketurl 连本地对应端口）。</param>
    public ProxyServer(string remoteHost, int remoteHttpPort, int listenPort = 5231, bool enableWs = true,
        int? remoteWsPort = null, int? listenWsPort = null)
    {
        _remoteHost = remoteHost;
        _remoteHttpPort = remoteHttpPort;
        _remoteWsPort = remoteWsPort ?? 5232;
        _listenHttpPort = listenPort;
        _listenWsPort = listenWsPort ?? _remoteWsPort;
        _enableWs = enableWs;
    }

    /// <summary>
    /// 解析远程地址：支持 http(s)://host[:port]、host[:port]、host 三种格式，返回 (主机名, 端口)。
    /// 地址未带端口时用 defaultPort。
    /// </summary>
    public static (string Host, int Port) ParseAddress(string input, int defaultPort)
    {
        string s = input.Trim();
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            s = s[7..];
        else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = s[8..];
        // 去掉路径
        int slash = s.IndexOf('/');
        if (slash >= 0)
            s = s[..slash];
        // 提取端口（IPv6 用 [..]:port 形式，直接取最后一个冒号）
        int colon = s.LastIndexOf(':');
        if (colon >= 0 && int.TryParse(s[(colon + 1)..], out int port) && port > 0 && port < 65536)
            return (s[..colon], port);
        return (s, defaultPort);
    }

    public bool IsRunning => _running;

    public string RemoteHost => _remoteHost;
    public int RemoteHttpPort => _remoteHttpPort;

    /// <summary>启动代理（异步 accept 循环，HTTP + WS 双端口）。</summary>
    public void Start()
    {
        if (_running)
            return;
        _running = true;
        _cts = new CancellationTokenSource();

        _listener = new TcpListener(IPAddress.Loopback, _listenHttpPort);
        _listener.Start(50);
        _tasks.Add(Task.Run(() => AcceptLoop(_listener, _remoteHttpPort, _cts.Token)));

        if (_enableWs)
        {
            _wsListener = new TcpListener(IPAddress.Loopback, _listenWsPort);
            _wsListener.Start(50);
            _tasks.Add(Task.Run(() => AcceptLoop(_wsListener, _remoteWsPort, _cts.Token)));
            LogService.Instance.Info($"代理已启动：127.0.0.1:{_listenHttpPort}→{_remoteHost}:{_remoteHttpPort}，127.0.0.1:{_listenWsPort}→{_remoteHost}:{_remoteWsPort}");
        }
        else
        {
            LogService.Instance.Info($"代理已启动：127.0.0.1:{_listenHttpPort}→{_remoteHost}:{_remoteHttpPort}");
        }
    }

    public void Stop()
    {
        if (!_running)
            return;
        _running = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* 忽略 */ }
        try { _wsListener?.Stop(); } catch { /* 忽略 */ }
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

    private async Task AcceptLoop(TcpListener listener, int remotePort, CancellationToken token)
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                break;
            }
            lock (_lock)
            {
                _clients.Add(client);
            }
            _ = Task.Run(() => HandleConnection(client, remotePort));
        }
    }

    private async Task HandleConnection(TcpClient client, int remotePort)
    {
        TcpClient? remote = null;
        try
        {
            client.NoDelay = true;
            remote = new TcpClient { NoDelay = true };
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await remote.ConnectAsync(_remoteHost, remotePort, timeoutCts.Token);

            var clientStream = client.GetStream();
            var remoteStream = remote.GetStream();

            // 双向转发（一方 EOF 即关闭另一方）
            var c2r = ForwardAsync(clientStream, remoteStream, "C→R");
            var r2c = ForwardAsync(remoteStream, clientStream, "R→C");
            await Task.WhenAny(c2r, r2c);
        }
        catch (Exception ex)
        {
            // 连接失败/中断：记录一次（避免刷屏仅对连接异常打日志）
            LogService.Instance.Write("PROXY", $"连接失败：{_remoteHost}:{remotePort}（{ex.Message}）");
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
