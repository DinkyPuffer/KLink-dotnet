namespace KLink.Server.Util;

/// <summary>时间工具（TimeUtil）。</summary>
public static class TimeUtil
{
    /// <summary>UTC ISO-8601 毫秒格式，如 2026-08-01T12:00:00.123Z。</summary>
    public static string NowIso()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }

    public static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
