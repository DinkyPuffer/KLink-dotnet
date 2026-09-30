using KLink.Bot.Cards;

namespace KLink.Bot.Engine;

/// <summary>
/// 对局中的一张卡。字段命名尽量贴近协议与反编译产物，方便和真实回放对齐。
///
/// 协议里的卡对象只有 5 个字段（card_id / is_gold / location / location_number / name），
/// 其余全部是内核自己维护的运行时状态。
/// </summary>
public sealed class CardInstance
{
    /// <summary>协议里的 cardID。左侧 HQ=1、首张牌=2 起；右侧 HQ=41、首张牌=42 起。</summary>
    public required int CardId { get; init; }

    public required string Name { get; init; }

    public required Side Owner { get; init; }

    public required CardDefinition Definition { get; init; }

    public bool IsGold { get; set; }

    // ---- 位置 ----
    public CardLocation Location { get; set; }
    public int LocationNumber { get; set; }

    // ---- 数值（可被效果修改，所以与 Definition 分开）----
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int MaxDefense { get; set; }
    public int KreditCost { get; set; }
    public int OperationCost { get; set; }

    // ---- 回合内状态 ----
    public int EnteredPlayOnTurn { get; set; } = -1;
    public int OperationsUsedThisTurn { get; set; }
    public bool HasAttackedThisTurn { get; set; }

    /// <summary>
    /// 「三选一」卡（<c>Choose One</c>）选的是第几个分支（0/1）。
    ///
    /// 数据来源是**真实动作流**，不是猜的：<c>ZActionPlayCardFromHand</c> 的参数表里
    /// 有一个 <c>Int:chooseOneIndex</c> 槽（见 BP_OnlineMatch 的
    /// <c>AddSubActionPlayCardFromHand</c>），而客户端紧凑 <c>PC</c> 的 `3` 号槽
    /// 在 295 条 PC 里只有 1 条非 0 —— 那一条（634651 #122）打的正是
    /// <c>card_event_planned_attack</c>，卡面「Choose One - … OR …」、
    /// 蓝图里用 <c>WhichChooseOne</c> + <c>SwitchEnum(0/1)</c> 分支。
    /// 所以 `PC` 的 `3` 号槽就是 <c>chooseOneIndex</c>。
    ///
    /// 默认 0 与 kardsim 的 <c>EngineHost.ChooseOne = _ =&gt; 0</c> 一致。
    /// 老实现是 <c>Random.Next(2)</c>，会让回放里的分支选择和真实对局不一致。
    /// </summary>
    public int ChooseOne { get; set; }

    /// <summary>
    /// 本回合是否已移动过战线。
    ///
    /// ⚠️ 移动和攻击**是两笔分开的额度**，不是共用一个「每回合一次行动」。
    /// 判据（真实回放 310284）：同一张单位在同一回合里先 `ML` 再 `AC`
    /// —— A15 `ML 48→槽0` 紧接 A16 `AC 48 打 敌方HQ`；
    /// A25 `ML 77→槽0` 紧接 A26 `AC 77 打 36`。
    /// 原先用单一 `OperationsUsedThisTurn` 卡住，会让这些攻击全部被拒。
    /// </summary>
    public bool HasMovedThisTurn { get; set; }

    /// <summary>
    /// **召唤失调**：这张卡是不是"刚部署、本回合还不能动"。
    ///
    /// 判据（逐字来自蓝图，不是推断）：
    /// <code>
    /// HasDeploymentSickness(Card) =
    ///       Card.IsLocatedOnBoard()
    ///    &amp;&amp; (Card.enterPlayOnTurn == GetTurnNumber())
    ///    &amp;&amp; !Card.getHasBlitz()
    /// </code>
    /// 出处：
    /// - `BP_Logic::HasDeploymentSickness` i=0/23/64/93/194/232（具名版本）
    /// - `cardsCheckFunctions::CanAttack` i=1919-2108：失败原因写死
    ///   <c>failReason = "deployment_sickness"</c>（i=2108），
    ///   `BP_Logic::GetCantAttackText` i=1299 把它翻成
    ///   <c>text_Notifications.try_attack_deployment_sickness</c> ——
    ///   这是**客户端自带的正式失败原因**，不是我们发明的词
    /// - `BP_Logic::CanCardDoAnything` i=1352-1568：这段在**攻击检查（i=2608）
    ///   和移动检查（i=2817）之前**，命中就 `canIt = False` 直接 return
    ///   ⇒ **部署当回合攻击和移动都被挡**
    /// </summary>
    public bool HasDeploymentSickness(GameState state)
        => Location.IsBoard()
           && EnteredPlayOnTurn == state.Turn
           && !Keywords.Contains(Keyword.Blitz);

    /// <summary>
    /// 该卡本回合是否还能发起攻击。
    ///
    /// ⚠️ **有召唤失调** —— 这里曾经写着"没有召唤失调"，并用真实回放 310284 的
    /// 三条证据（A24 `PC 77` → A25 `ML` → A26 `AC`；A34 `PC 65` → A35 `AC`；
    /// A70 `PC 79` → A71 `ML`）来论证"打出的当回合就能动"。
    /// **那段论证是错的**：那三张卡 `m20_scout_car` / `7_schutzen` / `sd_kfz_10_38`
    /// 在 CDO 里 `hasBlitz` **全是 True** —— 它们恰恰是"Blitz 例外"的正面证据。
    /// 反向检查：非 Blitz 单位 14 例，出牌 → 首次 ML/AC 的间隔全部 ≥ 2 回合，零反例
    /// （`klink bot/tools/verify-summoning-sickness.py`）。
    /// </summary>
    public bool CanOperateThisTurn(GameState state)
        => Location.IsBoard() && !HasAttackedThisTurn && !HasDeploymentSickness(state);

    /// <summary>该卡本回合是否还能移动战线（同样受召唤失调限制，见
    /// <see cref="HasDeploymentSickness"/> 里 `CanCardDoAnything` 的出处）。</summary>
    public bool CanMoveThisTurn(GameState state)
        => Location.IsBoard() && !HasMovedThisTurn && !HasDeploymentSickness(state);

    // ---- 关键字 ----
    public HashSet<string> Keywords { get; } = new(StringComparer.Ordinal);

    /// <summary>自定义能力（对应子动作 ActionCustomAbilityAdd/Remove 与 CustomAbilityAdd 调用）。</summary>
    public string? CustomAbility { get; set; }

    /// <summary>卡牌私有 JSON 暂存（对应游戏里的 JSON_Get*/JSON_Set*/JSON_Clear 一族调用）。</summary>
    public Dictionary<string, string> CustomJson { get; } = new(StringComparer.Ordinal);

    /// <summary>buff 记录：来源卡 → 该来源施加的修正。用于 RemoveTheBuff / checkAndUpdateBuffOnCard。</summary>
    public Dictionary<int, CardBuff> BuffsBySource { get; } = new();

    /// <summary>
    /// 是否为 HQ 卡（`card_location_*`，类型 <c>location</c>）。
    ///
    /// ⚠️ **判据必须是"卡的类型"，不能是"卡在哪个位置"。**
    /// 这里曾经写的是 `Location is BoardHqLeft or BoardHqRight` ——
    /// 那个写法在"所有单位都塞前线（7）"的旧模型下碰巧能用，
    /// 但真实模型里**半场就是 5/6**（蓝图 `GetSupportLineLocationBySide`：
    /// side1→5、side2→6；HQ 和单位同处一格，HQ 占 1 格容量）。
    /// 按位置判会让**每一个部署到半场的单位都被当成 HQ**：
    /// 被 `Board()` 过滤掉、被 `LegalTargets` 挡掉、被 `CheckDeaths` 跳过。
    /// </summary>
    public bool IsHq => Definition.IsLocationCard;

    /// <summary>这张卡是不是在**自己那侧的半场**（含 HQ 占的那一格所在的区域）。</summary>
    public bool IsInOwnHalf => Location == Owner.HqOf();

    /// <summary>
    /// 重甲点数 —— 基础值（卡面自带）加上所有来源的临时加成。
    ///
    /// 为什么要单独算而不能只看 <see cref="Keywords"/>：`ChangeHeavyArmor(卡, 来源, +1)`
    /// 是可以叠加的数值（客户端另有 `getTotalHeavyArmor`），关键字只能表示「有没有」。
    /// 关键字仍然同步维护（<c>HeavyArmor</c> 出现 ⟺ 点数 &gt; 0），
    /// 因为快照对拍、`AddHeavyArmor` 那条老路径都按关键字判。
    /// </summary>
    public int HeavyArmor
    {
        get
        {
            int total = Definition.HeavyArmor;
            foreach (var buff in BuffsBySource.Values)
            {
                total += buff.HeavyArmor;
            }

            return total;
        }
    }

    /// <summary>
    /// 建卡时灌入**卡面自带**的关键字（Blitz / Guard / Ambush / Fury / Smokescreen …），
    /// 然后把派生数值算一遍。
    ///
    /// ⚠️ 这一步以前根本没有 —— `CardInstance.Keywords` 建出来永远是空集，
    /// 于是 **205 张天生 Blitz、128 张 Guard 全部不生效**。
    /// 直接后果就是召唤失调没法正确实现（判据里的 `!getHasBlitz()` 读不到，
    /// 加了规则反而会误伤所有 Blitz 单位），以及 `MatchEngine.LegalTargets`
    /// 的嘲讽判定失效。数据来源见 `CardDefinition.Keywords` 的注释
    /// （pak CDO 抽的 `CardInnateTable`，不是 `cards.live.json`）。
    /// </summary>
    public void InitializeFromDefinition()
    {
        foreach (string keyword in Definition.Keywords)
        {
            Keywords.Add(keyword);
        }

        RecalculateStats();
    }

    /// <summary>
    /// 把所有「基础值 + 各来源 buff」的派生数值重算一遍。
    ///
    /// ⚠️ **必须在每次改 buff 之后调用**，而且**必须是纯函数**（只看
    /// <see cref="Definition"/> 与 <see cref="BuffsBySource"/>，不累加当前值）。
    /// 光环类效果（`ApplyTheBuff`/`RemoveTheBuff`）会反复施加/撤销同一来源，
    /// 增量式写法第二次就会翻倍；绝对值重算天然幂等。
    /// 落点：<see cref="KreditCost"/>、<see cref="OperationCost"/>、重甲关键字。
    /// </summary>
    public void RecalculateStats()
    {
        KreditCost = EffectiveKreditCost;
        OperationCost = EffectiveOperationCost;

        if (HeavyArmor > 0)
        {
            Keywords.Add(Keyword.HeavyArmor);
        }
        else
        {
            Keywords.Remove(Keyword.HeavyArmor);
        }
    }

    /// <summary>
    /// 有效费用 = 卡面费用 + 各来源的改费之和，并按卡面规则夹下限。
    ///
    /// 下限规则（实测来自 `card_event_committed_crew` 与 `card_unit_85_pioneer_company`
    /// 两张光环的差别）：
    /// - 普通改费（`ChangeKreditCost` 的 changeType=0，例如 85 先驱的「指令 -1」）
    ///   **下限 1** —— 卡面上写的就是「costs 1 less」，1 费指令不该变成 0 费；
    /// - 显式设费（changeType=1，例如 committed_crew 的 `getTotalKreditCost * -1`）
    ///   允许到 0 —— 它的卡面明说「Spitfires cost 0 to deploy」。
    ///
    /// 只要某个来源声明了「可到 0」，整体下限就放开：committed_crew 的
    /// 「-当前总费用」本来就是把费用设成绝对 0，不该被 1 卡住。
    /// </summary>
    public int EffectiveKreditCost
    {
        get
        {
            int total = Definition.Kredits;
            bool mayReachZero = Definition.Kredits <= 0;
            foreach (var buff in BuffsBySource.Values)
            {
                total += buff.KreditCost;
                mayReachZero |= buff.KreditCostSetsAbsoluteValue;
            }

            int floor = mayReachZero ? 0 : MinKreditCost;
            return Math.Max(floor, total);
        }
    }

    /// <summary>非「可到 0」卡的改费下限。见 <see cref="EffectiveKreditCost"/>。</summary>
    public const int MinKreditCost = 1;

    /// <summary>有效行动费用 = 卡面行动费用 + 各来源的加减（下限 0）。</summary>
    public int EffectiveOperationCost
    {
        get
        {
            int total = Definition.OperationCost;
            foreach (var buff in BuffsBySource.Values)
            {
                total += buff.OperationCost;
            }

            return Math.Max(0, total);
        }
    }

    public bool IsAlive => Location != CardLocation.Discard && Location != CardLocation.NotAvailable;

    public override string ToString()
        => $"[{CardId}]{Name}@{Location}#{LocationNumber}" + (Definition.IsUnit ? $" {Attack}/{Defense}" : "");

    public CardSnapshot Snapshot() => new(
        CardId, Name, Owner, Location, LocationNumber, IsGold,
        Attack, Defense, MaxDefense, KreditCost, OperationCost,
        EnteredPlayOnTurn, OperationsUsedThisTurn, HasAttackedThisTurn, HasMovedThisTurn,
        Keywords.OrderBy(k => k, StringComparer.Ordinal).ToArray(),
        CustomAbility,
        CustomJson.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value));
}

/// <summary>某个来源施加在卡上的持续修正。</summary>
public sealed class CardBuff
{
    public int SourceCardId { get; init; }
    public int Attack { get; set; }
    public int Defense { get; set; }

    /// <summary>
    /// 费用相对**卡面费用**的偏移量。
    ///
    /// ⚠️ 存的是偏移量而不是「改完之后是多少」：光环会反复 Apply/Remove，
    /// 存绝对值的话第二次 Apply 就没法判断该不该再减一次了。
    /// </summary>
    public int KreditCost { get; set; }

    /// <summary>行动费用相对**卡面行动费用**的偏移量。</summary>
    public int OperationCost { get; set; }

    /// <summary>重甲点数（可叠加的数值，不是布尔）。</summary>
    public int HeavyArmor { get; set; }

    /// <summary>
    /// 这个来源的改费是「显式设成绝对值」而不是「相对减费」。
    ///
    /// 判据：`ChangeKreditCost(卡, 来源, 数值, changeType)` 的 changeType=1
    /// （实测 `card_event_committed_crew` 用 `-getTotalKreditCost` + changeType=1
    /// 把 Spitfire 设成 0 费，而 `card_unit_85_pioneer_company` 用 -1 + changeType=0）。
    /// 它决定 <see cref="CardInstance.EffectiveKreditCost"/> 的下限要不要放开到 0。
    /// </summary>
    public bool KreditCostSetsAbsoluteValue { get; set; }

    public int Duration { get; set; } = -1;   // -1 = 永久

    public bool IsEmpty => Attack == 0 && Defense == 0 && KreditCost == 0
                           && OperationCost == 0 && HeavyArmor == 0;
}

/// <summary>某一时刻的卡状态快照 —— 用于和客户端逐步对拍。</summary>
public sealed record CardSnapshot(
    int CardId,
    string Name,
    Side Owner,
    CardLocation Location,
    int LocationNumber,
    bool IsGold,
    int Attack,
    int Defense,
    int MaxDefense,
    int KreditCost,
    int OperationCost,
    int EnteredPlayOnTurn,
    int OperationsUsedThisTurn,
    bool HasAttackedThisTurn,
    bool HasMovedThisTurn,
    string[] Keywords,
    string? CustomAbility,
    Dictionary<string, string> CustomJson);

/// <summary>
/// 游戏里的关键字。名字取自反编译出的子动作名
/// （ZActionGive* / ZActionRemove* / ZActionAddHeavyArmor / ZActionMakeVeteran …）。
/// </summary>
public static class Keyword
{
    public const string Alpine = "Alpine";
    public const string Ambush = "Ambush";
    public const string Blitz = "Blitz";
    public const string Bond = "Bond";
    public const string Fury = "Fury";
    public const string Guard = "Guard";
    public const string Immune = "Immune";
    public const string Mobilize = "Mobilize";
    public const string Salvage = "Salvage";
    public const string Shock = "Shock";
    public const string Smokescreen = "Smokescreen";
    public const string HeavyArmor = "HeavyArmor";
    public const string Veteran = "Veteran";
    public const string Suppressed = "Suppressed";
    public const string Pinned = "Pinned";

    // ---- P1 新增（2026-09-30）----
    //
    // 下面 5 个是 CDO 里**本来就有**的 `has*` 字段，只是内核的 `Keyword` 集合一直没收录
    // （`gen-card-keywords.py` 的 `BOOL_FLAGS` 里注释写着"硬映射会编译不过，也不该为了
    // 这一步去扩关键字集"）。后果是 IR 里以**成员读**出现的 `hasDeployment` /
    // `hasDestruction` / `hasCovert` / `hasPincer` 全部读成 null → 判假（审计 §4.1 第 1 条）。
    //
    // 卡数（`out/cards-full2.json` 的 CDO）：hasDeployment 249 / hasDestruction 73 /
    // hasCovert 11 / hasPincer 15 / hasScrying 1。出现时**恒为 True**，缺席即默认 false。
    public const string Deployment = "Deployment";
    public const string Destruction = "Destruction";
    public const string Covert = "Covert";
    public const string Pincer = "Pincer";
    public const string Scrying = "Scrying";
}
