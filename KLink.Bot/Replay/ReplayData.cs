using System.Text.Json;
using System.Text.Json.Nodes;
using KLink.Bot.Engine;
namespace KLink.Bot.Replay;

/// <summary>
/// 一局真实对局的回放数据 —— 从 fyserver 的只读接口导出：
/// <code>
/// GET /replays/{id}                        开局快照（双方手牌/牌库/HQ，带 cardID）
/// GET /replays/{id}/actions?limit=1000     全部动作（服务端已解密）
/// </code>
///
/// 这是**目前唯一能证伪规则内核的东西**：它给了完全确定的初始状态
/// 和一条真实的动作流，而动作流里还带着一个关键状态量
/// （<c>action_data["84"]</c> = 行动方自己的 HQ 当前防御力）。
/// </summary>
public sealed class ReplayData
{
    public required int MatchId { get; init; }
    public required int Turns { get; init; }
    public required int LeftPlayerId { get; init; }
    public required int RightPlayerId { get; init; }
    public required string WinnerSide { get; init; }

    /// <summary>开局快照里的全部卡（含 HQ）。</summary>
    public required IReadOnlyList<SnapshotCard> Cards { get; init; }

    public required IReadOnlyList<WireAction> Actions { get; init; }

    private Dictionary<int, SnapshotCard>? _byId;

    /// <summary>
    /// 开局快照里 id → 卡。
    ///
    /// 这个映射是**可信的**：5 局回放里，动作流自带的卡组码与快照里的卡名
    /// **零冲突**（见 tools/check-id-semantics.py）。也就是说 `action_data["0"]`
    /// 的编号空间和快照的 `card_id` 是同一个。
    ///
    /// ⚠️ 但**手牌/牌库的归属不可信**：fyserver 用 `Random.Shared` 洗牌后
    /// `Take(4/5)` 当手牌（MatchManagerService.GetCardsFromDeck），而真实客户端
    /// 会打出快照里被判为「在牌库」的牌。所以回放驱动只能把快照当**卡池**用。
    /// </summary>
    public IReadOnlyDictionary<int, SnapshotCard> ById
        => _byId ??= Cards.GroupBy(c => c.CardId).ToDictionary(g => g.Key, g => g.First());

    public Side SideOf(int playerId) => playerId == LeftPlayerId ? Side.Left
        : playerId == RightPlayerId ? Side.Right
        : Side.NotAvailable;

    private string? _hqKey;
    private bool _hqKeyResolved;

    /// <summary>
    /// 推断「对手 HQ 防御力」这一字段在 <c>action_data</c> 里的下标。
    ///
    /// 为什么需要推断：客户端发的是「下标 → 值」，下标来自一张**每局重新登记**的
    /// 字段名表，所以 HQ 字段的下标每局都不同。实测：
    /// <code>
    /// 310284 → 84   165924 → 24   955337 → 37   130691 → 91   563868 → 68
    /// </code>
    ///
    /// 判据（三条同时满足）：
    /// 1. 键名是纯数字，且不是动作参数 0..4
    /// 2. 出现次数最多（HQ 是每个动作都会带的场上状态）
    /// 3. 首个取值是 20（HQ 初始防御）
    /// </summary>
    public string? InferHqKey()
    {
        if (_hqKeyResolved)
        {
            return _hqKey;
        }

        _hqKeyResolved = true;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var first = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in Actions)
        {
            foreach (string k in a.ExtraStateKeys)
            {
                counts[k] = counts.GetValueOrDefault(k) + 1;
                first.TryAdd(k, a.Get(k) ?? "");
            }
        }

        _hqKey = counts
            .Where(kv => first[kv.Key] == MatchEngine.InitialHqDefense.ToString())
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Length)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .FirstOrDefault();

        return _hqKey;
    }

    public sealed record SnapshotCard(int CardId, string Name, CardLocation Location, int LocationNumber,
                                      Side Owner, bool IsGold, string? Faction);

    // ==================== 加载 ====================

    public static ReplayData Load(string snapshotPath, string actionsPath)
    {
        var snap = JsonNode.Parse(File.ReadAllText(snapshotPath))?.AsObject()
                   ?? throw new FormatException("快照不是 JSON 对象");
        var summary = snap["summary"]?.AsObject() ?? throw new FormatException("快照缺少 summary");
        var startingData = snap["starting_info"]?["match_and_starting_data"]?["starting_data"]?.AsObject()
                           ?? throw new FormatException("快照缺少 starting_data");

        int leftId = summary["left_player_id"]?.GetValue<int>() ?? 0;
        int rightId = summary["right_player_id"]?.GetValue<int>() ?? 0;

        var cards = new List<SnapshotCard>();

        void AddCard(JsonNode? node, Side owner)
        {
            if (node is not JsonObject c)
            {
                return;
            }

            string? loc = c["location"]?.GetValue<string>();
            if (loc is null || !TryParseLocation(loc, out CardLocation location))
            {
                return;
            }

            cards.Add(new SnapshotCard(
                c["card_id"]?.GetValue<int>() ?? 0,
                c["name"]?.GetValue<string>() ?? "",
                location,
                c["location_number"]?.GetValue<int>() ?? 0,
                owner,
                c["is_gold"]?.GetValue<bool>() ?? false,
                c["faction"]?.GetValue<string>()));
        }

        AddCard(startingData["location_card_left"], Side.Left);
        AddCard(startingData["location_card_right"], Side.Right);

        foreach (string field in new[] { "starting_hand_left", "deck_left" })
        {
            if (startingData[field] is JsonArray arr)
            {
                foreach (var c in arr)
                {
                    AddCard(c, Side.Left);
                }
            }
        }

        foreach (string field in new[] { "starting_hand_right", "deck_right" })
        {
            if (startingData[field] is JsonArray arr)
            {
                foreach (var c in arr)
                {
                    AddCard(c, Side.Right);
                }
            }
        }

        var actionsDoc = JsonNode.Parse(File.ReadAllText(actionsPath))?.AsObject();
        var actions = new List<WireAction>();
        if (actionsDoc?["actions"] is JsonArray actionArr)
        {
            foreach (var a in actionArr)
            {
                if (a is not null)
                {
                    actions.Add(WireAction.Parse(a));
                }
            }
        }

        return new ReplayData
        {
            MatchId = summary["match_id"]?.GetValue<int>() ?? 0,
            Turns = summary["turns"]?.GetValue<int>() ?? 0,
            LeftPlayerId = leftId,
            RightPlayerId = rightId,
            WinnerSide = summary["winner_side"]?.GetValue<string>() ?? "",
            Cards = cards,
            Actions = actions,
        };
    }

    public static bool TryParseLocation(string wire, out CardLocation location)
    {
        location = wire switch
        {
            "deck_left" => CardLocation.DeckLeft,
            "deck_right" => CardLocation.DeckRight,
            "hand_left" => CardLocation.HandLeft,
            "hand_right" => CardLocation.HandRight,
            "board_hqleft" => CardLocation.BoardHqLeft,
            "board_hqright" => CardLocation.BoardHqRight,
            "board_frontline" => CardLocation.BoardFrontline,
            "board_left" => CardLocation.BoardFrontline,
            "board_right" => CardLocation.BoardFrontline,
            "discard" => CardLocation.Discard,
            "deck" => CardLocation.Deck,
            var _ => CardLocation.NotAvailable,
        };

        return location != CardLocation.NotAvailable;
    }
}
