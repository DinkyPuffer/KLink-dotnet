namespace KLink.Server.Model;

/// <summary>卡组数据模型（DeckRecord）。</summary>
public sealed class DeckRecord
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public string CardBack { get; set; } = "";
    public string MainFaction { get; set; } = "";
    public string AllyFaction { get; set; } = "";
    public string DeckCode { get; set; } = "";
    public bool Favorite { get; set; }
    public string LastPlayed { get; set; } = "";
    public string CreateDate { get; set; } = "";
    public string ModifyDate { get; set; } = "";
}
