using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace KLink.App.Services;

/// <summary>
/// fyserver 后台统计客户端：给启动器「服务器」页提供真实的在线人数与对局数。
///
/// 说明：fyserver 的 /admin/api/* **本机访问同样需要凭据**（会话 Cookie 或 X-Admin-Key 二者之一），
/// 所以这里固定带 X-Admin-Key（值即启动器设置页的「管理员令牌」，会被写入 setting.json:adminApiKey）。
/// 取不到时返回 null，由调用方回退为 0（不影响界面可用性）。
/// </summary>
public sealed class FyServerAdminClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>最近一次成功读取的统计（失败时为 null）。</summary>
    public sealed record Stats(int OnlinePlayers, int Matches, int WaitingPlayers, int TotalUsers, int BannedUsers);

    /// <summary>
    /// 读取 /admin/api/stats。任何失败（未启动、密钥不对、超时）都返回 null。
    /// </summary>
    public Stats? TryGetStats(int port, string adminApiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/admin/api/stats");
            if (!string.IsNullOrEmpty(adminApiKey))
                request.Headers.TryAddWithoutValidation("X-Admin-Key", adminApiKey);

            using var resp = _http.Send(request);
            if (!resp.IsSuccessStatusCode)
                return null;

            string body = new StreamReader(resp.Content.ReadAsStream()).ReadToEnd();
            if (JsonNode.Parse(body) is not JsonObject json)
                return null;

            return new Stats(
                OnlinePlayers: ReadInt(json, "online_players", "online", "online_users"),
                Matches: ReadInt(json, "active_matches", "matches", "playing_matches"),
                WaitingPlayers: ReadInt(json, "waiting_players", "queued_players", "queue_players"),
                TotalUsers: ReadInt(json, "total_users", "users"),
                BannedUsers: ReadInt(json, "banned_users", "banned"));
        }
        catch
        {
            // 服务器未启动 / 密钥不对 / 超时：静默回退
            return null;
        }
    }

    /// <summary>字段名容错：fyserver 的 stats 结构可能随版本调整，按候选名逐个找。</summary>
    private static int ReadInt(JsonObject json, params string[] candidates)
    {
        foreach (string name in candidates)
        {
            if (json.TryGetPropertyValue(name, out var node) && node is not null)
            {
                try { return node.GetValue<int>(); } catch { /* 继续试下一个候选名 */ }
            }
        }
        return 0;
    }
}
