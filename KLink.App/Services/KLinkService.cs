using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLink.Server;

namespace KLink.App.Services;

/// <summary>
/// 启动器统一编排服务（KLinkBridge 的服务器控制逻辑）。
/// 管理三种模式：本地（127.0.0.1 服务器）/ 局域网（0.0.0.0 服务器 + UDP 广播）/ 远程（TCP 透明代理）。
/// </summary>
public sealed class KLinkService
{
    /// <summary>全局单例：所有页面共享同一服务器状态（启动/停止/模式）。</summary>
    public static KLinkService Instance { get; } = new();

    private readonly SettingsService _settings;
    private readonly GameServer _kardsServer = new();
    private ProxyServer? _proxy;
    private readonly LanDiscovery _lanDiscovery = new();

    public event Action? StateChanged;

    private KLinkService() => _settings = SettingsService.Instance;

    /// <summary>当前模式：local / lan / remote / none。</summary>
    public string CurrentMode { get; private set; } = "none";

    public bool ServerRunning { get; private set; }

    /// <summary>数据库文件路径：启动器数据目录 data\kards_server.db（相对路径，不硬编码）。</summary>
    private string DatabasePath => Path.Combine(AppContext.BaseDirectory, "data", "kards_server.db");

    // ==================== 启动 / 停止 ====================

    /// <summary>启动服务器。返回错误信息，成功返回 null。</summary>
    public string? StartServer(string mode, string roomName, string hostName,
        string remoteAddress, int remotePort, string adminToken, string preferredPlayerName,
        int httpPort = 5231, int wsPort = 5232)
    {
        try
        {
            if (ServerRunning)
                StopServer();

            switch (mode)
            {
                case "local":
                    StartLocalServer(roomName, hostName, adminToken, preferredPlayerName, httpPort, wsPort);
                    break;
                case "lan":
                    StartLanServer(roomName, hostName, adminToken, preferredPlayerName, httpPort, wsPort);
                    break;
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
        try { _kardsServer.Stop(); } catch { /* 忽略 */ }
        try { _proxy?.Stop(); } catch { /* 忽略 */ }
        _proxy = null;
        _lanDiscovery.StopBroadcast();
        ServerRunning = false;
        CurrentMode = "none";
        LogService.Instance.Info("服务器已停止");
        StateChanged?.Invoke();
    }

    /// <summary>异步停止（后台线程执行，避免阻塞 UI；Kestrel 停止会等待连接清理）。</summary>
    public Task StopServerAsync() => Task.Run(StopServer);

    private void StartLocalServer(string roomName, string hostName, string adminToken, string preferredPlayerName,
        int httpPort = 5231, int wsPort = 5232)
    {
        var config = BuildServerConfig("127.0.0.1", roomName, hostName, adminToken, preferredPlayerName);
        config.HttpPort = httpPort;
        config.WsPort = wsPort;
        EnsureDatabaseDirectory();
        _kardsServer.Start(config, DatabasePath);
    }

    private void StartLanServer(string roomName, string hostName, string adminToken, string preferredPlayerName,
        int httpPort = 5231, int wsPort = 5232)
    {
        var config = BuildServerConfig("0.0.0.0", roomName, hostName, adminToken, preferredPlayerName);
        config.HttpPort = httpPort;
        config.WsPort = wsPort;
        EnsureDatabaseDirectory();
        _kardsServer.Start(config, DatabasePath);
        // LAN 模式自动开始广播房间
        _lanDiscovery.SetRoomInfo(roomName, hostName, httpPort, wsPort, 0);
        _lanDiscovery.StartBroadcast();
    }

    private void EnsureDatabaseDirectory()
    {
        string? dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private void StartRemoteProxy(string remoteAddress, int remotePort)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
            throw new ArgumentException("请输入远程服务器地址");
        // 屏蔽官方服务器域名
        if (remoteAddress.ToLowerInvariant().Contains("kards.live.1939api.com"))
            throw new ArgumentException("BLOCKED");
        _proxy?.Stop();
        _proxy = new ProxyServer(remoteAddress.Trim(), remotePort);
        _proxy.Start();
    }

    private ServerConfig BuildServerConfig(string bindHost, string roomName, string hostName,
        string adminToken, string preferredPlayerName)
    {
        var config = new ServerConfig
        {
            BindHost = bindHost,
            HttpPort = 5231,
            WsPort = 5232,
            RoomName = string.IsNullOrWhiteSpace(roomName) ? "KLink Room" : roomName,
            HostName = string.IsNullOrWhiteSpace(hostName) ? "Host" : hostName,
            AdminToken = adminToken ?? "",
            PreferredPlayerName = preferredPlayerName ?? "",
        };

        // JWT 密钥：首次随机生成并持久化，避免硬编码
        string? secret = _settings.Settings.JwtSecret;
        if (string.IsNullOrEmpty(secret))
        {
            secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _settings.Settings.JwtSecret = secret;
            _settings.Save();
        }
        config.JwtSecret = secret;
        return config;
    }

    // ==================== 状态 ====================

    public JsonObject GetStatus()
    {
        var json = ServerRunning && _proxy is null ? _kardsServer.GetStatusJson() : new JsonObject();
        json["running"] = ServerRunning;
        json["mode"] = CurrentMode;
        json["remoteAddress"] = _settings.Settings.RemoteAddress;
        json["remotePort"] = _settings.Settings.RemotePort;
        return json;
    }

    public string GetHttpUrl() => ServerRunning && _proxy is null ? _kardsServer.GetHttpUrl() : "";
    public string GetWebSocketUrl() => ServerRunning && _proxy is null ? _kardsServer.GetWebSocketUrl() : "";

    // ==================== 房间发现 ====================

    public void StartScanRooms() => _lanDiscovery.StartScan();
    public void StopScanRooms() => _lanDiscovery.StopScan();
    public event Action<JsonObject>? RoomFound
    {
        add => _lanDiscovery.RoomFound += value;
        remove => _lanDiscovery.RoomFound -= value;
    }
}
