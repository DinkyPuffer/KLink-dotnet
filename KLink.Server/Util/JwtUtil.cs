using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KLink.Server.Util;

/// <summary>
/// HS256 JWT 签发/验证（JwtUtil）。
/// 与 Java 版字节级兼容：header/payload 为 JSON，Base64URL 无填充，payload 含 user_id/username/exp。
/// </summary>
public static class JwtUtil
{
    public static string Create(string secret, int userId, string username, long expiresAtSeconds)
    {
        var header = new JsonObject { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new JsonObject
        {
            ["user_id"] = userId,
            ["username"] = username,
            ["exp"] = expiresAtSeconds,
        };
        string signingInput = Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString())) + "."
            + Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        return signingInput + "." + Sign(secret, signingInput);
    }

    public static JsonObject Verify(string secret, string? token)
    {
        var parts = token?.Split('.') ?? Array.Empty<string>();
        if (parts.Length != 3)
            throw new ArgumentException("Bad JWT");
        string signingInput = parts[0] + "." + parts[1];
        string expected = Sign(secret, signingInput);
        if (!ConstantEquals(expected, parts[2]))
            throw new ArgumentException("Bad JWT signature");
        var payload = JsonNode.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1]))) as JsonObject
            ?? throw new ArgumentException("Bad JWT payload");
        long exp = payload["exp"]?.GetValue<long>() ?? 0;
        if (exp > 0 && exp < TimeUtil.NowSeconds())
            throw new ArgumentException("JWT expired");
        return payload;
    }

    private static string Sign(string secret, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    private static bool ConstantEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
