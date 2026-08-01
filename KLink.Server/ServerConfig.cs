namespace KLink.Server;

/// <summary>服务器配置模型（ServerConfig）。</summary>
public sealed class ServerConfig
{
    public string BindHost { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 5231;
    public int WsPort { get; set; } = 5232;
    public string? AdvertisedHost { get; set; }
    public string DatabaseName { get; set; } = "kards_server.db";
    public string JwtSecret { get; set; } = "CometKards-is-a-help-much-kards-players-that-can't-find-gameuser-or-baned";
    public string JwtAlgorithm { get; set; } = "HS256";
    public string GameVersion { get; set; } = "Kards 1.52.25476.launcher";
    public string RoomName { get; set; } = "KLink Room";
    public string HostName { get; set; } = "Host";
    public string AdminToken { get; set; } = "";
    public string PreferredPlayerName { get; set; } = "";

    public ServerConfig Copy() => new()
    {
        BindHost = BindHost,
        HttpPort = HttpPort,
        WsPort = WsPort,
        AdvertisedHost = AdvertisedHost,
        DatabaseName = DatabaseName,
        JwtSecret = JwtSecret,
        JwtAlgorithm = JwtAlgorithm,
        GameVersion = GameVersion,
        RoomName = RoomName,
        HostName = HostName,
        AdminToken = AdminToken,
        PreferredPlayerName = PreferredPlayerName,
    };
}
