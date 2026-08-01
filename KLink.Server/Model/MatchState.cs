using System.Text.Json.Nodes;

namespace KLink.Server.Model;

/// <summary>对局状态模型（MatchState）。</summary>
public sealed class MatchState
{
    public const int BotPlayerId = 900000001;
    public const int BotTurnDelayMs = 3000;

    public int MatchId { get; set; }
    public int PlayerLeft { get; set; }
    public int PlayerRight { get; set; }
    public int DeckIdLeft { get; set; }
    public int DeckIdRight { get; set; }
    public string Status { get; set; } = "pending";
    public string MatchType { get; set; } = "battle";
    public string WinnerSide { get; set; } = "";
    public int WinnerId { get; set; }
    public int CurrentTurn { get; set; } = 1;
    public int CurrentActionId { get; set; }
    public int ActionSessionId { get; set; }
    public int ActionPlayerId { get; set; }
    public int EndConfirmCount { get; set; }
    public bool LeftOnline { get; set; } = true;
    public bool RightOnline { get; set; } = true;
    public int LevelLoadedLeft { get; set; }
    public int LevelLoadedRight { get; set; }
    public string PlayerStatusLeft { get; set; } = "not_done";
    public string PlayerStatusRight { get; set; } = "not_done";

    public JsonObject LeftDeckData { get; set; } = new();
    public JsonObject RightDeckData { get; set; } = new();
    public JsonArray LeftCardsData { get; set; } = new();
    public JsonArray RightCardsData { get; set; } = new();
    public JsonArray LeftHandCards { get; set; } = new();
    public JsonArray RightHandCards { get; set; } = new();
    public JsonArray LeftDeckCards { get; set; } = new();
    public JsonArray RightDeckCards { get; set; } = new();
    public JsonArray LeftDiscardedCards { get; set; } = new();
    public JsonArray RightDiscardedCards { get; set; } = new();
    public JsonArray LeftReplacementCards { get; set; } = new();
    public JsonArray RightReplacementCards { get; set; } = new();
    public JsonArray Notifications { get; } = new();
    public JsonArray Actions { get; } = new();
    public Dictionary<int, string> ActionsData { get; } = new();

    // ---- Bot 字段 ----
    public bool BotEnabled { get; set; }
    public string BotSide { get; set; } = "right";
    public int BotLastEndedTurn { get; set; }
    public int BotPendingTurn { get; set; }
    public long BotTurnReadyAt { get; set; }
}
