using System.Net;
using System.Text.Json.Nodes;
using KLink.Server.Data;
using KLink.Server.Http;
using KLink.Server.Model;
using KLink.Server.Ws;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KLink.Server;

/// <summary>
/// 本地服务器宿主（KardsLocalServer）。
/// 内嵌 Kestrel 提供 HTTP + WebSocket，统一管理生命周期。
/// </summary>
public sealed class GameServer
{
    private readonly object _lock = new();
    private ServerConfig? _config;
    private AssetStore? _assets;
    private AppDatabase? _database;
    private MatchManager? _matches;
    private GameWebSocketServer? _webSocketServer;
    private ApiHandler? _httpHandler;
    private WebApplication? _app;

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _app is not null && _webSocketServer is not null
                    && _webSocketServer.Running && _app.Services is not null;
            }
        }
    }

    /// <summary>启动服务器。dbPath 为 SQLite 数据文件路径；bindHost 为 "0.0.0.0" 或具体地址。</summary>
    public void Start(ServerConfig requestedConfig, string dbPath)
    {
        lock (_lock)
        {
            if (IsRunning)
                return;
            _config = requestedConfig.Copy();
            _assets = new AssetStore();
            _database = new AppDatabase(dbPath);
            _matches = new MatchManager(_database, _assets);
            _webSocketServer = new GameWebSocketServer(_config, _database, _matches);
            _httpHandler = new ApiHandler(_config, _database, _assets, _matches, _webSocketServer);

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                var address = _config.BindHost == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(_config.BindHost);
                // 与 Java SimpleHttpServer 一致：不发送 "Server: Kestrel" 头（客户端可能据此区分服务器实现）
                options.AddServerHeader = false;
                options.Listen(address, _config.HttpPort);   // HTTP + WS 升级
                options.Listen(address, _config.WsPort);     // 纯 WS（Java 版为独立端口 5232）
            });
            _app = builder.Build();
            _app.UseWebSockets();
            // WS 升级请求不限定路径与端口（与 Java 版一致）；5232 端口上的非 WS 请求直接 400
            _app.Use(async (context, next) =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                    await _webSocketServer.Handle(context);
                else if (context.Connection.LocalPort == _config.WsPort)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Bad Request");
                }
                else
                {
                    // CORS 头(与 Java 端一致:所有响应带 Access-Control-Allow-Origin: *),并处理 OPTIONS 预检
                    if (HttpMethods.IsOptions(context.Request.Method))
                    {
                        context.Response.StatusCode = 204;
                        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
                        context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, x-kards-room-admin";
                        context.Response.Headers["Access-Control-Max-Age"] = "86400";
                        context.Response.Headers["Keep-Alive"] = "timeout=5";   // 与 Java SimpleHttpServer 一致
                        return;
                    }
                    context.Response.OnStarting(() =>
                    {
                        // 与 Java SimpleHttpServer 一致：所有响应带 Access-Control-Allow-Origin: * 与 Keep-Alive: timeout=5
                        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                        context.Response.Headers["Keep-Alive"] = "timeout=5";
                        context.Response.Headers["Connection"] = "keep-alive";
                        return Task.CompletedTask;
                    });
                    LogPacketRequest(context);   // 数据包：请求行 + 关键头
                    var originalBody = context.Response.Body;
                    using var buffer = new MemoryStream();
                    context.Response.Body = buffer;
                    try
                    {
                        await next();
                    }
                    finally
                    {
                        context.Response.Body = originalBody;
                        buffer.Position = 0;
                        string body = new System.IO.StreamReader(buffer, System.Text.Encoding.UTF8).ReadToEnd();
                        LogPacketResponse(context.Response.StatusCode, body);   // 数据包：状态 + 响应体开头
                        buffer.Position = 0;
                        await buffer.CopyToAsync(originalBody);
                    }
                }
            });
            _app.MapFallback((HttpContext ctx) => _httpHandler.Handle(ctx));

            try
            {
                _app.Start();
            }
            catch
            {
                _app = null;
                _webSocketServer = null;
                _httpHandler = null;
                _matches = null;
                _database = null;
                throw;
            }
            _webSocketServer.Running = true;
            ServerLog.Add("room", $"server started: {_config.RoomName}");
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            // 先关闭所有 WebSocket 连接，让 Kestrel 快速优雅退出（否则会等待长连接超时）
            _webSocketServer?.CloseAll();
            if (_app is not null)
            {
                try
                {
                    _app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                }
                catch
                {
                    // 忽略停止异常
                }
                _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            if (_webSocketServer is not null)
                _webSocketServer.Running = false;
            ServerLog.Add("room", "server stopped");
            _app = null;
            _webSocketServer = null;
            _httpHandler = null;
            _matches = null;
            _database = null;
            _assets = null;
        }
    }

    // ==================== 状态 ====================

    public string GetHttpUrl()
    {
        if (_config is null) return "";
        return $"http://{HostForDisplay()}:{_config.HttpPort}/";
    }

    public string GetWebSocketUrl()
    {
        if (_config is null) return "";
        return $"ws://{HostForDisplay()}:{_config.WsPort}/ws";
    }

    public JsonObject GetStatusJson()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["running"] = IsRunning,
                ["http_url"] = GetHttpUrl(),
                ["websocket_url"] = GetWebSocketUrl(),
                ["room_name"] = _config?.RoomName ?? "",
                ["host_name"] = _config?.HostName ?? "",
                ["online_players"] = _webSocketServer?.OnlineCount() ?? 0,
                ["matches"] = _matches?.MatchCount() ?? 0,
                ["players"] = RoomPlayersJson(),
                ["logs"] = ServerLog.Recent(),
            };
        }
    }

    private JsonArray RoomPlayersJson()
    {
        var array = new JsonArray();
        if (_database is null || _matches is null)
            return array;
        foreach (var user in _database.ListUsers())
        {
            bool kicked = _matches.IsKicked(user.Id);
            if (!user.IsOnline && !kicked)
                continue;
            array.Add(new JsonObject
            {
                ["player_id"] = user.Id,
                ["player_name"] = user.PlayerName,
                ["player_tag"] = user.PlayerTag,
                ["username"] = user.Username,
                ["online"] = user.IsOnline,
                ["kicked"] = kicked,
            });
        }
        return array;
    }

    private string HostForDisplay()
    {
        if (_config is null) return "";
        if (!string.IsNullOrWhiteSpace(_config.AdvertisedHost))
            return _config.AdvertisedHost.Trim();
        return _config.BindHost == "0.0.0.0" ? "<device-ip>" : _config.BindHost;
    }

    // ==================== 数据包日志（[PKT]） ====================

    /// <summary>记录请求包：方法 + 路径 + 查询 + 关键头（ApiKey/Authorization 脱敏截断）。</summary>
    private static void LogPacketRequest(HttpContext context)
    {
        var req = context.Request;
        string query = req.QueryString.HasValue ? req.QueryString.Value! : "";
        string apiKey = Truncate(req.Headers["X-Api-Key"].ToString(), 60);
        string auth = Truncate(req.Headers["Authorization"].ToString(), 40);
        ServerEvents.Log($"[PKT] {req.Method} {req.Path}{query} | ApiKey: {apiKey} | Auth: {auth}");
    }

    /// <summary>记录响应包：状态码 + 响应体开头（单行、截断 1200 字符）；若含 server_options 则额外完整打印。</summary>
    private static void LogPacketResponse(int status, string body)
    {
        string flat = body.Replace("\r", "").Replace("\n", " ");
        string head = flat.Length > 1200 ? flat[..1200] + "…" : flat;
        ServerEvents.Log($"[PKT] → {status} {head}");
        // server_options 位于登录响应的末尾，截断看不到，单独完整打印便于分析
        try
        {
            if (body.Contains("\"server_options\"", StringComparison.Ordinal))
            {
                var node = JsonNode.Parse(body);
                var so = node?["server_options"];
                if (so is not null)
                    ServerEvents.Log($"[PKT] server_options: {so.ToJsonString()}");
            }
        }
        catch
        {
            // 解析失败忽略
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s))
            return "-";
        return s.Length > max ? s[..max] + "…" : s;
    }
}
