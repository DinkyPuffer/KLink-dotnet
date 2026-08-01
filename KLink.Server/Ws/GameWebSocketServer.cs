using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using KLink.Server.Data;
using KLink.Server.Model;
using KLink.Server.Util;
using Microsoft.AspNetCore.Http;

namespace KLink.Server.Ws;

/// <summary>
/// WebSocket 服务器（SimpleWebSocketServer 的业务逻辑）。
/// 传输层由 Kestrel WebSocket 中间件提供；认证、消息分发、连接管理照搬 Java 版。
/// </summary>
public sealed class GameWebSocketServer
{
    private readonly ServerConfig _config;
    private readonly AppDatabase _database;
    private readonly MatchManager _matches;
    private readonly ConcurrentDictionary<int, WebSocket> _clients = new();

    /// <summary>服务器运行状态（由宿主 GameServer 维护）。</summary>
    public bool Running { get; set; }

    public GameWebSocketServer(ServerConfig config, AppDatabase database, MatchManager matches)
    {
        _config = config;
        _database = database;
        _matches = matches;
    }

    public bool IsOnline(int userId) => _clients.ContainsKey(userId);

    public bool Kick(int userId)
    {
        _clients.TryRemove(userId, out var client);
        _matches.KickPlayer(userId);
        _database.SetOnline(userId, false);
        ServerLog.Add("room", "kick player " + userId);
        if (client is null)
            return false;
        try
        {
            var message = new JsonObject
            {
                ["channel"] = "notification",
                ["message"] = "kicked",
                ["context"] = "room",
            };
            SendJson(client, message);
            SendClose(client).GetAwaiter().GetResult();
        }
        catch
        {
            // 忽略发送异常
        }
        CloseQuietly(client);
        return true;
    }

    public int OnlineCount() => _clients.Count;

    /// <summary>关闭所有客户端连接（服务器停止时调用，让 Kestrel 快速优雅退出）。</summary>
    public void CloseAll()
    {
        foreach (var (_, client) in _clients)
            CloseQuietly(client);
        _clients.Clear();
    }

    private static bool RequestedWsProtocol(HttpRequest request)
    {
        string protocols = request.Headers["Sec-WebSocket-Protocol"].ToString();
        foreach (string part in protocols.Split(','))
        {
            if (part.Trim().Equals("ws", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public bool SendToId(int userId, JsonObject message)
    {
        if (!_clients.TryGetValue(userId, out var client))
            return false;
        SendJson(client, message);
        return true;
    }

    public int Broadcast(JsonObject message)
    {
        string text = message?.ToJsonString() ?? "{}";
        int sent = 0;
        foreach (var client in _clients.Values)
        {
            SendText(client, text);
            sent++;
        }
        return sent;
    }

    /// <summary>WS 端点处理入口：认证 → 升级 → 读循环。接受任意路径的升级请求（与 Java 版一致）。</summary>
    public async Task Handle(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }
        var user = Authenticate(context.Request);
        if (user is null)
        {
            ServerEvents.Log("WS 握手认证失败：" + context.Request.Path +
                $"（authorization='{context.Request.Headers["authorization"]}'，token='{context.Request.Headers["x-token"]}'）");
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
        // 仅当客户端请求了 ws 子协议时才回显（Java 版行为；Kestrel 在子协议不匹配时会拒绝握手）
        string? subProtocol = RequestedWsProtocol(context.Request) ? "ws" : null;
        using var ws = await context.WebSockets.AcceptWebSocketAsync(subProtocol);

        // 同用户重复连接：关闭旧连接
        if (_clients.TryGetValue(user.Id, out var old))
            CloseQuietly(old);
        _clients[user.Id] = ws;
        _matches.SetOnline(user.Id, true);
        _database.SetOnline(user.Id, true);
        ServerLog.Add("ws", $"player {user.Id} connected");
        ServerEvents.Log($"WS 玩家 {user.Id}（{user.Username}）已连接");

        try
        {
            await ReadLoop(user, ws);
        }
        catch
        {
            // 连接异常断开
        }
        finally
        {
            _clients.TryRemove(user.Id, out _);
            _matches.SetOnline(user.Id, false);
            _database.SetOnline(user.Id, false);
            ServerLog.Add("ws", $"player {user.Id} disconnected");
            ServerEvents.Log($"WS 玩家 {user.Id} 已断开");
        }
    }

    private async Task ReadLoop(UserRecord user, WebSocket ws)
    {
        var buffer = new byte[4096];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await SendClose(ws);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string text = Encoding.UTF8.GetString(ms.ToArray());
            HandleMessage(user, ws, text);
        }
    }

    private void HandleMessage(UserRecord user, WebSocket ws, string text)
    {
        try
        {
            var data = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException();
            string channel = data["channel"]?.GetValue<string>() ?? "";
            if (channel == "ping")
            {
                var response = BaseMessage("pong", "ping", "", user.Id, "");
                SendJson(ws, response);
                return;
            }
            if (channel is "touchcard" or "emoji")
            {
                Forward(ws, data, channel, data["message"]?.GetValue<string>() ?? "");
                return;
            }
            if (channel == "notification")
            {
                string message = data["message"]?.GetValue<string>() ?? "";
                if (message is "websocketcheck" or "matchaction" or "im_here")
                    Forward(ws, data, "notification", message);
            }
        }
        catch
        {
            SendText(ws, "Echo: " + text);
        }
    }

    private void Forward(WebSocket ws, JsonObject data, string channel, string message)
    {
        string receiver = data["receiver"]?.GetValue<string>() ?? "";
        var response = BaseMessage(message, channel, data["context"], GetSenderId(ws), receiver);
        if (int.TryParse(receiver, out int receiverId))
            SendToId(receiverId, response);
    }

    private int GetSenderId(WebSocket ws)
    {
        foreach (var (id, socket) in _clients)
        {
            if (ReferenceEquals(socket, ws))
                return id;
        }
        return 0;
    }

    private static JsonObject BaseMessage(string message, string channel, JsonNode? context, int sender, string receiver)
        => new()
        {
            ["message"] = message,
            ["channel"] = channel,
            ["context"] = context ?? "",
            ["timestamp"] = TimeUtil.NowIso(),
            ["sender"] = sender,
            ["receiver"] = receiver ?? "",
        };

    // ==================== 认证 ====================

    private UserRecord? Authenticate(HttpRequest request)
    {
        var tokens = ExtractTokens(request);
        foreach (string token in tokens)
        {
            var exact = _database.FindUserByJwt(token);
            if (exact is not null)
                return _matches.IsKicked(exact.Id) ? null : exact;
            try
            {
                var payload = JwtUtil.Verify(_config.JwtSecret, token);
                int userId = payload["user_id"]?.GetValue<int>() ?? 0;
                var user = _database.FindUserById(userId);
                if (user is not null && TokenMatchesUserSession(token, payload, user))
                    return _matches.IsKicked(user.Id) ? null : user;
            }
            catch
            {
                // 继续尝试下一个 token
            }
        }
        return null;
    }

    private bool TokenMatchesUserSession(string token, JsonObject payload, UserRecord user)
    {
        try
        {
            if (string.IsNullOrEmpty(user.PlayerJwt))
                return false;
            if (token == user.PlayerJwt)
                return true;
            var serverPayload = JwtUtil.Verify(_config.JwtSecret, user.PlayerJwt);
            long clientExp = payload["exp"]?.GetValue<long>() ?? 0;
            long serverExp = serverPayload["exp"]?.GetValue<long>() ?? 0;
            return clientExp > 0 && serverExp > 0 && Math.Abs(clientExp - serverExp) < 86400;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ExtractTokens(HttpRequest request)
    {
        var tokens = new List<string>();
        AddNormalizedTokens(tokens, request.Headers["authorization"]);
        AddNormalizedTokens(tokens, TokenFromRequestLine(request));
        AddNormalizedTokens(tokens, request.Headers["x-authorization"]);
        AddNormalizedTokens(tokens, request.Headers["x-token"]);
        AddNormalizedTokens(tokens, request.Headers["sec-websocket-protocol"]);
        return tokens;
    }

    private static void AddNormalizedTokens(List<string> tokens, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;
        foreach (string part in value.Split(','))
        {
            string? token = NormalizeAuthToken(part);
            if (token is not null && !tokens.Contains(token))
                tokens.Add(token);
            int space = part.IndexOf(' ');
            while (space > 0 && space + 1 < part.Length)
            {
                string? tail = NormalizeAuthToken(part[(space + 1)..]);
                if (tail is not null && !tokens.Contains(tail))
                    tokens.Add(tail);
                space = part.IndexOf(' ', space + 1);
            }
        }
    }

    private static string? NormalizeAuthToken(string? value)
    {
        if (value is null)
            return null;
        string token = value.Trim();
        if ((token.StartsWith('"') && token.EndsWith('"')) || (token.StartsWith('\'') && token.EndsWith('\'')))
            token = token[1..^1].Trim();
        if (token.Length == 0)
            return null;
        if (token.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            token = token[14..].Trim();
        if (token.StartsWith("JWT ", StringComparison.OrdinalIgnoreCase))
            token = token[4..].Trim();
        else if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        else if (token.StartsWith("JWT:", StringComparison.OrdinalIgnoreCase))
            token = token[4..].Trim();
        else if (token.StartsWith("Bearer:", StringComparison.OrdinalIgnoreCase))
            token = token[7..].Trim();
        else if (token.StartsWith("JWT%20", StringComparison.OrdinalIgnoreCase))
            token = token[6..].Trim();
        else if (token.StartsWith("Bearer%20", StringComparison.OrdinalIgnoreCase))
            token = token[9..].Trim();
        if (token.StartsWith('='))
            token = token[1..].Trim();
        if (token.Length == 0 || token.Equals("ws", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!token.Contains('.'))
            return null;
        return token;
    }

    private static string? TokenFromRequestLine(HttpRequest request)
    {
        // Kestrel 中请求行 query 即 request.Query
        foreach (string key in request.Query.Keys)
        {
            if (key.Equals("token", StringComparison.OrdinalIgnoreCase)
                || key.Equals("jwt", StringComparison.OrdinalIgnoreCase)
                || key.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeAuthToken(request.Query[key].ToString());
            }
        }
        return null;
    }

    // ==================== 发送 ====================

    private static void SendJson(WebSocket ws, JsonObject message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 忽略发送异常
        }
    }

    private static void SendText(WebSocket ws, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // 忽略发送异常
        }
    }

    private static async Task SendClose(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch
        {
            // 忽略
        }
    }

    private static void CloseQuietly(WebSocket ws)
    {
        try
        {
            ws.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}
