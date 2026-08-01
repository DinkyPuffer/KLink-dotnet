namespace KLink.Server.Model;

/// <summary>用户数据模型（UserRecord）。</summary>
public sealed class UserRecord
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    public string PlayerName { get; set; } = "";
    public int PlayerTag { get; set; }
    public string PlayerJwt { get; set; } = "";
    public bool IsOnline { get; set; }
    public Dictionary<string, string?> Equipment { get; } = new();
}
