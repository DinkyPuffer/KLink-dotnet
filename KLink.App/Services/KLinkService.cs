using System.IO;
using System.Text.Json.Nodes;

namespace KLink.App.Services;

/// <summary>
/// 启动器统一编排服务（KLinkBridge 的服务器控制逻辑）。
/// 管理三种模式：本地 / 局域网（托管 fyserver 子进程）/ 远程（TCP 透明代理）。
///
/// 服务端已由内嵌 KLink.Server 换成 CCB-TEAM/fyserver（独立进程），
/// 集成方案与依据见 需求/fyserver集成规划.md。
/// </summary>
public sealed class KLinkService
{
    /// <summary>全局单例：所有页面共享同一服务器状态（启动/停止/模式）。</summary>
    public static KLinkService Instance { get; } = new();

    /// <summary>fyserver 默认端口：HTTP 与 WebSocket 共用（单端口模型）。</summary>
    public const int DefaultHttpPort = 5231;

    private readonly SettingsService _settings;
    private readonly FyServerHost _fyServer = new();
    private readonly FyServerAdminClient _adminClient = new();
    private ProxyServer? _proxy;
    private readonly LanDiscovery _lanDiscovery = new();

    public event Action? StateChanged;

    private KLinkService()
    {
        _settings = SettingsService.Instance;
        // fyserver 子进程死亡时同步本地状态，否则「服务器」页会一直显示"运行中"
        // （轮询读的是本地 ServerRunning 标志，不会自动感知子进程退出）
        _fyServer.Exited += OnFyServerExited;
    }

    /// <summary>
    /// fyserver 意外退出：清零本地状态让界面如实显示，并提示用户。
    /// 注意处于"主动停止"流程时会先置 ServerRunning=false，这里的判断可避免误报。
    /// </summary>
    private void OnFyServerExited(int exitCode)
    {
        if (!ServerRunning)
            return;     // 主动停止，无需处理
        ServerRunning = false;
        CurrentMode = "none";
        _lanDiscovery.StopBroadcast();
        LogService.Instance.Error($"服务器进程已意外退出（退出码 {exitCode}），请查看上方日志");
        StateChanged?.Invoke();
    }

    /// <summary>当前模式：local / lan / remote / none。</summary>
    public string CurrentMode { get; private set; } = "none";

    public bool ServerRunning { get; private set; }

    /// <summary>当前实际使用的 HTTP/WS 端口（fyserver 单端口）。未运行时为默认端口。</summary>
    private int _activePort = DefaultHttpPort;

    /// <summary>fyserver 输出行（转发给 UI 日志区）。</summary>
    public event Action<string>? ServerOutput
    {
        add => _fyServer.OutputLine += value;
        remove => _fyServer.OutputLine -= value;
    }

    /// <summary>fyserver 意外退出时触发（用于刷新界面状态并提示用户）。</summary>
    public event Action<int>? ServerExited
    {
        add => _fyServer.Exited += value;
        remove => _fyServer.Exited -= value;
    }

    // ==================== 启动 / 停止 ====================

    /// <summary>启动服务器。返回错误信息，成功返回 null。</summary>
    public string? StartServer(string mode, string roomName, string hostName,
        string remoteAddress, int remotePort, string adminToken, string preferredPlayerName,
        int httpPort = DefaultHttpPort, int wsPort = 5232)
    {
        try
        {
            if (ServerRunning)
                StopServer();

            switch (mode)
            {
                case "local":
                    {
                        var error = StartLocalServer(roomName, hostName, adminToken, preferredPlayerName, httpPort);
                        if (error is not null)
                            return error;
                        break;
                    }
                case "lan":
                    {
                        var error = StartLanServer(roomName, hostName, adminToken, preferredPlayerName, httpPort);
                        if (error is not null)
                            return error;
                        break;
                    }
                case "remote":
                    StartRemoteProxy(remoteAddress, remotePort);
                    break;
                default:
                    return $"未知模式: {mode}";
            }

            ServerRunning = true;
            CurrentMode = mode;
            _settings.Settings.LastMode = mode;
            _settings.Settings.RoomName = roomName;
            _settings.Settings.HostName = hostName;
            _settings.Settings.RemoteAddress = remoteAddress;
            _settings.Settings.RemotePort = remotePort;
            _settings.Save();
            LogService.Instance.Info($"服务器已启动（{mode}）");
            StateChanged?.Invoke();
            return null;
        }
        catch (Exception ex)
        {
            StopServer();
            CurrentMode = "none";
            LogService.Instance.Error($"启动失败：{ex.Message}");
            StateChanged?.Invoke();
            return $"启动失败：{ex.Message}";
        }
    }

    public void StopServer()
    {
        // 先清零本地状态再停子进程：FyServerHost.Stop() 结束时子进程会触发 Exited，
        // 若那时 ServerRunning 仍为 true，会被 OnFyServerExited 误判为"意外退出"。
        bool wasRunning = ServerRunning;
        ServerRunning = false;
        CurrentMode = "none";

        try { _fyServer.Stop(); } catch { /* 忽略 */ }
        try { _proxy?.Stop(); } catch { /* 忽略 */ }
        _proxy = null;
        _lanDiscovery.StopBroadcast();

        if (wasRunning)
            LogService.Instance.Info("服务器已停止");
        StateChanged?.Invoke();
    }

    /// <summary>异步停止（后台线程执行，避免阻塞 UI）。</summary>
    public Task StopServerAsync() => Task.Run(StopServer);

    /// <summary>
    /// 本地模式：fyserver 只绑 127.0.0.1（listenIp），客户端固定连 127.0.0.1:5231。
    /// 新版 fyserver 的 listenIp 真正参与监听，因此本地模式不会再对整个局域网开放。
    /// </summary>
    private string? StartLocalServer(string roomName, string hostName, string adminToken, string preferredPlayerName,
        int httpPort = DefaultHttpPort)
    {
        return StartFyServer("127.0.0.1", "127.0.0.1", adminToken, preferredPlayerName, httpPort);
    }

    /// <summary>
    /// 局域网模式：绑 0.0.0.0 供同网段设备连接；
    /// 对外通告地址写成局域网 IP，让下发的 websocketurl 是其它设备可达的地址。
    /// </summary>
    private string? StartLanServer(string roomName, string hostName, string adminToken, string preferredPlayerName,
        int httpPort = DefaultHttpPort)
    {
        string ip = GetLanIpAddress();
        // 绑 0.0.0.0 供同网段设备连接；对外通告局域网 IP
        var error = StartFyServer("0.0.0.0", ip, adminToken, preferredPlayerName, httpPort);
        if (error is not null)
            return error;

        // 局域网模式下别的设备要被防火墙放通才能连进来。
        // fyserver 是自建 socket 监听（不走 HTTP.sys），Windows 不会弹"允许此应用"的提示，
        // 所以这里主动检查并提权添加一条只针对本 exe 的入站规则（已存在则跳过，不会重复弹 UAC）。
        var firewallNote = FirewallService.EnsureInboundAllowed();
        if (firewallNote is not null)
            LogService.Instance.Warn($"防火墙：{firewallNote}");
        else
            LogService.Instance.Info("防火墙：已放通本程序的入站连接");

        // LAN 模式自动开始广播房间
        _lanDiscovery.SetRoomInfo(roomName, hostName, httpPort, httpPort, 0);
        _lanDiscovery.StartBroadcast();
        LogService.Instance.Info($"局域网地址：http://{ip}:{httpPort}/（其它设备用这个地址连接）");
        return null;
    }

    /// <summary>启动 fyserver 并等待就绪（它没有就绪信号，只能探测后台 session 接口）。</summary>
    private string? StartFyServer(string listenIp, string publicIp, string adminToken,
        string preferredPlayerName, int httpPort)
    {
        // 设置页留空表示"用默认名"，此时把默认值显式写进配置，避免线上出现空名字
        string playerName = string.IsNullOrWhiteSpace(preferredPlayerName)
            ? AppSettings.DefaultPlayerName
            : preferredPlayerName.Trim();
        string adminName = string.IsNullOrWhiteSpace(_settings.Settings.AdminDisplayName)
            ? AppSettings.DefaultAdminDisplayName
            : _settings.Settings.AdminDisplayName.Trim();
        var error = _fyServer.Start(listenIp, httpPort, publicIp, adminToken, playerName, adminName);
        if (error is not null)
            return error;

        // 端口被占时 fyserver 不会退出也不会报错，只能靠超时判定
        bool ready = _fyServer.WaitUntilReadyAsync(httpPort, TimeSpan.FromSeconds(20))
            .GetAwaiter().GetResult();
        if (!ready)
        {
            _fyServer.Stop();
            return $"fyserver 启动超时（{httpPort} 端口未在 20 秒内响应）。\n" +
                   "常见原因：端口被占用，或 data\\fyserver 下的发布产物不完整。详见日志区。";
        }

        _activePort = httpPort;
        LogService.Instance.Info($"fyserver 就绪：http://127.0.0.1:{httpPort}/");
        return null;
    }

    /// <summary>获取本机局域网 IP（UDP 连接法，不实际发包）。失败回退 127.0.0.1。</summary>
    private static string GetLanIpAddress()
    {
        try
        {
            using var socket = new System.Net.Sockets.UdpClient("8.8.8.8", 80);
            var ep = socket.Client.LocalEndPoint as System.Net.IPEndPoint;
            return ep?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private void StartRemoteProxy(string remoteAddress, int remotePort)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
            throw new ArgumentException("请输入远程服务器地址");
        // 解析地址：支持 http(s)://host[:port]、host[:port]、host 格式
        var (host, port) = ProxyServer.ParseAddress(remoteAddress, remotePort);
        // 屏蔽官方服务器域名
        if (host.ToLowerInvariant().Contains("kards.live.1939api.com"))
            throw new ArgumentException("BLOCKED");
        _proxy?.Stop();
        int wsPort = _settings.Settings.RemoteWsPort > 0 ? _settings.Settings.RemoteWsPort : 5232;
        _proxy = new ProxyServer(host, port, enableWs: true, remoteWsPort: wsPort);
        _proxy.Start();
    }

    // ==================== 状态 ====================

    public JsonObject GetStatus()
    {
        bool running = ServerRunning;
        bool local = running && _proxy is null;

        // 在线人数/对局数：读 fyserver 的 /admin/api/stats（需带 X-Admin-Key）。
        // 取不到（未启动/密钥不对/超时）时回退为 0，不影响界面可用性。
        int online = 0;
        int matches = 0;
        if (local)
        {
            var stats = _adminClient.TryGetStats(_activePort, _settings.Settings.AdminToken);
            if (stats is not null)
            {
                online = stats.OnlinePlayers;
                matches = stats.Matches;
            }
        }

        var json = new JsonObject
        {
            ["running"] = running,
            ["mode"] = CurrentMode,
            ["http_url"] = local ? $"http://127.0.0.1:{_activePort}/" : "",
            ["websocket_url"] = local ? $"ws://127.0.0.1:{_activePort}/" : "",
            ["room_name"] = _settings.Settings.RoomName,
            ["host_name"] = _settings.Settings.HostName,
            ["online_players"] = online,
            ["matches"] = matches,
            ["remoteAddress"] = _settings.Settings.RemoteAddress,
            ["remotePort"] = _settings.Settings.RemotePort,
        };
        return json;
    }

    public string GetHttpUrl()
        => ServerRunning && _proxy is null ? $"http://127.0.0.1:{_activePort}/" : "";

    public string GetWebSocketUrl()
        => ServerRunning && _proxy is null ? $"ws://127.0.0.1:{_activePort}/" : "";

    /// <summary>后台管理界面地址（fyserver 的 Web UI，与实际启动端口一致）。</summary>
    public string GetAdminUiUrl() => $"http://127.0.0.1:{_activePort}/admin-ui/";

    // ==================== 房间发现 ====================

    public void StartScanRooms() => _lanDiscovery.StartScan();
    public void StopScanRooms() => _lanDiscovery.StopScan();
    public event Action<JsonObject>? RoomFound
    {
        add => _lanDiscovery.RoomFound += value;
        remove => _lanDiscovery.RoomFound -= value;
    }
}
