using System.IO;
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
    /// <summary>调试日志开关：记录每个连接的首包(前 400 字节)，用于诊断远程服务器交互问题。</summary>
    public static bool DebugLog { get; set; }

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

            // 请求方向改写 Host 头为远程真实地址（远程服务器前有 CDN/反代会校验 Host，
            // 游戏经本地代理后 Host=127.0.0.1:5231 会被 CDN 以 456 拒绝）
            string rewriteHost = $"{_remoteHost}:{remotePort}";
            var c2r = ForwardAsync(clientStream, remoteStream, "C→R", rewriteHost);
            var r2c = ForwardAsync(remoteStream, clientStream, "R→C", null);
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

    /// <summary>数据包捕获上限（响应累计前 48KB，覆盖登录响应/server_options）。</summary>
    private const int PacketCaptureMax = 48 * 1024;

    /// <summary>
    /// 双向转发。请求方向（rewriteHost 非空）时改写首个数据包里的 Host 头。
    /// 同时捕获首个数据包（请求）/整段流（响应）用于 [PKT] 数据包日志。
    /// </summary>
    private static async Task ForwardAsync(NetworkStream from, NetworkStream to, string direction, string? rewriteHost)
    {
        var buffer = new byte[32768];
        bool first = true;
        var captured = new MemoryStream(); // 响应方向累计数据包内容
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                byte[] outData = buffer;
                int outLen = n;
                if (first && rewriteHost is not null)
                {
                    var rewritten = TryRewriteHost(buffer, n, rewriteHost);
                    if (rewritten is not null)
                    {
                        outData = rewritten.Value.Data;
                        outLen = rewritten.Value.Length;
                    }
                }
                // 累积数据包内容（前 PacketCaptureMax 字节）
                if (captured.Length < PacketCaptureMax)
                {
                    int take = Math.Min(outLen, PacketCaptureMax - (int)captured.Length);
                    captured.Write(outData, 0, take);
                }
                if (first)
                {
                    if (direction == "C→R")
                        LogPacketRequest(outData, outLen);   // 请求：首个包即完整头
                    first = false;
                }
                await to.WriteAsync(outData.AsMemory(0, outLen));
                await to.FlushAsync();
            }
        }
        catch
        {
            // 一方断开
        }
        finally
        {
            // 响应：流结束时用累计内容记录（含跨包 body）
            if (direction == "R→C" && captured.Length > 0)
                LogPacketResponse(captured.GetBuffer(), (int)captured.Length);
            try { to.Close(); } catch { /* 忽略 */ }
            if (DebugLog)
                LogService.Instance.Write("PROXY", $"连接关闭（{direction}）");
        }
    }

    // ==================== 数据包日志（[PKT]，与服务器端一致） ====================

    /// <summary>记录请求包：请求行 + Host + ApiKey + Authorization（脱敏截断）。</summary>
    private static void LogPacketRequest(byte[] data, int length)
    {
        try
        {
            string head = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(length, 2048));
            int headerEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
                headerEnd = head.Length;
            string header = head[..headerEnd].Replace("\r\n", " ⏎ ");
            // 提取关键头
            string apiKey = ExtractHeader(head, "X-Api-Key");
            string auth = ExtractHeader(head, "Authorization");
            LogService.Instance.Write("PKT", $"[PKT] {header} | ApiKey: {Truncate(apiKey, 60)} | Auth: {Truncate(auth, 40)}");
        }
        catch { /* 解析失败忽略 */ }
    }

    /// <summary>记录响应包：状态行 + 响应体开头；含 server_options 时完整打印。</summary>
    private static void LogPacketResponse(byte[] data, int length)
    {
        try
        {
            string flat = System.Text.Encoding.UTF8.GetString(data, 0, length).Replace("\r", "").Replace("\n", " ");
            int bodyStart = flat.IndexOf(" ", StringComparison.Ordinal) + 1; // 略过 "HTTP/1.1"
            int jsonStart = flat.IndexOf("{", StringComparison.Ordinal);
            string statusLine = jsonStart >= 0 ? flat[..jsonStart].Trim() : flat;
            string body = jsonStart >= 0 ? flat[jsonStart..] : "";
            string shown = body.Length > 1000 ? body[..1000] + "…" : body;
            LogService.Instance.Write("PKT", $"[PKT] → {statusLine} {shown}");
            // server_options 完整打印（便于分析 anzac 等）
            if (body.Contains("\"server_options\"", StringComparison.Ordinal))
            {
                int so = body.IndexOf("\"server_options\":", StringComparison.Ordinal) + 17;
                LogService.Instance.Write("PKT", $"[PKT] server_options: {body[so..]}");
            }
        }
        catch { /* 解析失败忽略 */ }
    }

    private static string ExtractHeader(string headerText, string name)
    {
        foreach (var line in headerText.Split('\n'))
        {
            var t = line.TrimEnd('\r').Trim();
            if (t.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return t[(name.Length + 1)..].Trim();
        }
        return "";
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "-";
        return s.Length > max ? s[..max] + "…" : s;
    }

    /// <summary>改写 HTTP 请求的 Host 头（返回 null 表示非 HTTP 或头不完整，原样转发）。</summary>
    private static (byte[] Data, int Length)? TryRewriteHost(byte[] data, int length, string newHost)
    {
        if (length > 8192 || length < 12)
            return null;
        string head = System.Text.Encoding.ASCII.GetString(data, 0, length);
        // 只处理 HTTP 请求首行（避免改动二进制/帧数据）
        if (!head.StartsWith("GET ", StringComparison.Ordinal)
            && !head.StartsWith("POST ", StringComparison.Ordinal)
            && !head.StartsWith("PUT ", StringComparison.Ordinal)
            && !head.StartsWith("DELETE ", StringComparison.Ordinal)
            && !head.StartsWith("PATCH ", StringComparison.Ordinal)
            && !head.StartsWith("OPTIONS ", StringComparison.Ordinal)
            && !head.StartsWith("HEAD ", StringComparison.Ordinal))
            return null;
        int headerEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0)
            return null; // 头部未收完整，原样转发（首包一般已包含完整头）
        string header = head[..headerEnd];
        string rest = head[headerEnd..];
        if (!System.Text.RegularExpressions.Regex.IsMatch(header, "(?im)^Host:.*$"))
            return null;
        string rewritten = System.Text.RegularExpressions.Regex.Replace(header, "(?im)^Host:.*$", "Host: " + newHost);
        byte[] result = System.Text.Encoding.ASCII.GetBytes(rewritten + rest);
        return (result, result.Length);
    }
}
