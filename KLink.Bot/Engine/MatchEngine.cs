using KLink.Bot.Cards;
using KLink.Bot.Effects;

namespace KLink.Bot.Engine;

/// <summary>
/// 对局引擎主干：回合循环、部署、移动、攻击、胜负判定。
///
/// 设计约束（来自确定性锁步）：
/// - 所有随机走 <see cref="GameState.Random"/>
/// - 不依赖字典遍历顺序、时间、GUID
/// - 同一份输入 + 同一个种子 ⇒ 完全相同的输出
///
/// ⚠️ 尚未确定、需要真实回放确认的点（已在代码里标 TODO）：
/// - 前线/支援线的具体槽位编码（ECardLocationEnum 里只有一个 Board_Frontline）
/// - kredit 上限、疲劳公式、部署线容量
/// - 攻击结算顺序（反击、Guard、Smokescreen 的交互）
/// </summary>
public sealed class MatchEngine
{
    /// <summary>HQ 初始防御。依据：fyserver 注入的 bot 动作里出现 <c>{"side":"right","75":"20"}</c>。</summary>
    public const int InitialHqDefense = 20;

    /// <summary>kredit 上限（KARDS 为 12）。TODO 待回放确认。</summary>
    public const int MaxKreditCap = 12;

    /// <summary>
    /// 支援线（半场）容量 —— **5 格，HQ 占其中 1 格 ⇒ 每边最多 4 个单位**。
    ///
    /// 见 <see cref="GameState.HalfBoardCapacity"/>（蓝图 `FetchCardsByLocation`
    /// case 5,6 → `MaxQty = 5`，且计数不排除 HQ）。
    /// 前线的 5 格是**另一个独立上限**，不与这里合计。
    /// </summary>
    public const int SupportLineCapacity = GameState.HalfBoardCapacity;

    /// <summary>半场里能放的单位数 = 总格数 − HQ 占的那 1 格。</summary>
    public const int HalfBoardUnitCapacity = GameState.HalfBoardCapacity - 1;

    /// <summary>先手 4 张、后手 5 张（与服务器 DeckCodeManager 的分牌一致）。</summary>
    private const int FirstPlayerHand = 4;
    private const int SecondPlayerHand = 5;

    private readonly CardDatabase _db;
    private int _actionId;

    public MatchEngine(CardDatabase database, IReadOnlyList<string> leftDeck, IReadOnlyList<string> rightDeck, ulong seed)
    {
        _db = database;
        State = new GameState(database, seed);
        LeftDeckList = leftDeck;
        RightDeckList = rightDeck;

        // ⚠️ 必须在构造函数里就建好 Api：`Start()` 只是「打一副新牌局」的便捷入口，
        //    回放驱动是直接手工摆盘的，不会走 `Start()`。以前 Api 只在 `Start()` 里赋值，
        //    导致回放路径上每个动作都在 `Api.FireTrigger` 上抛 NullReferenceException。
        Api = new CardApi(this);

        // 卡「换区」与「被创建」的钩子。
        //
        // 为什么挂在 GameState 上、而不是在每个调用点手工补一句：
        // 蓝图里这两个事件是从 `CardLocationMoved` / `CreateCardObject` 两个**唯一入口**
        // 派发的（见下面 FireLocationMoved / FireCardCreated 的出处注释），
        // 而内核的 `GameState.Move` / `GameState.Create` 正好是它们对应的唯一入口。
        // 挂在这里等于"每一处换区都发"，不会漏。
        //
        // ⚠️ 开局摆牌阶段发这些事件是**无害**的：`CardApi.FireTrigger` 的快照只含
        //    「双方棋盘 + 弃牌堆」，那时两者都是空的（HQ 不算棋盘卡），所以没有人收到。
        State.CardMoved = FireLocationMoved;
        State.CardCreated = card => Api.FireTrigger("OnCreateCard", card, card.Owner);
    }

    /// <summary>
    /// 「某张卡换区了」—— 出处 `out/bp-cardfn.json` 函数
    /// `ExecuteOnCardLocationMoved(cardID, oldLocation, newLocation, changeOwner, moveReason)`：
    /// <code>
    /// i=38   cardToMove = GetCardFromID(cardID)
    /// i=89   JumpIfNot 469 (cardToMove.isSuppressed)   ; 未压制 ⇒ 跳去自己那一路
    /// i=125  FetchAllCardsWithEventTrigger(47)         ; 47 = OnOtherCardLocationMoved
    /// i=394  CallFunc_EqualEqual_IntInt(item.cardID, cardID)
    /// i=454  JumpIfNot 666                             ; 不等 ⇒ 走"别人"那一路
    /// i=515  cardToMove.OnCardLocationMoved(oldLocation, newLocation, ChangeOwner, MoveReason)
    /// i=771  item.OnOtherCardLocationMoved(cardToMove, oldLocation, newLocation, ChangeOwner, MoveReason)
    /// </code>
    /// 触发号 47 的依据：`ERegisteredCardFunction.h` 逐项数下来第 48 项 = `OnOtherCardLocationMoved`。
    /// 签名 `BaseCardObject.h:721/586`。
    ///
    /// ⚠️ `moveReason` 蓝图侧是 `/Game/Structs/CardMoveReason` 结构体，
    ///    事件桩里被 `GetEnumeratorUserFriendlyName` 转成 **String**（i=469/725）。
    ///    本内核**没有建模这张枚举表**，一律传空串 —— 这是近似，不是复刻。
    ///    影响面：`MoveReason` 的订阅者（读它的卡）会拿到空串。
    /// </summary>
    private void FireLocationMoved(CardInstance card, CardLocation oldLocation, CardLocation newLocation)
    {
        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardMoved"] = card,
            ["oldLocation"] = (int)oldLocation,
            ["newLocation"] = (int)newLocation,
            ["ChangeOwner"] = false,
            ["MoveReason"] = "",
        };

        // ---- 烟幕：移到**前线**就消失（P1，2026-09-30）----
        //
        // 出处 `out/bp-cardfn.json` → `CardLocationMoved`（61 条语句）：
        // <code>
        // si=638  PushExecutionFlow off=770
        // si=643  EqualEqual_ByteByte(newLocation, 7)          ; 7 = 前线
        // si=674  PopExecutionFlowIfNot(...)                    ; 不是 7 ⇒ 返回
        // si=684  tmpCard.getHasSmokescreen(out doesIt)
        // si=725  PopExecutionFlowIfNot(...)                    ; 没有烟幕 ⇒ 返回
        // si=735  RemoveSmokescreen(cardID, cardID, true, false)
        // </code>
        // 放在这里而不是 `MoveUnit` 里：`CardLocationMoved` 是**所有**移位路径的
        // 唯一入口（移动 / 生成落场 / 退却），蓝图也是在它里面判的。
        if (newLocation == CardLocation.BoardFrontline
            && card.Keywords.Contains(Keyword.Smokescreen))
        {
            Api.RemoveKeyword(card, Keyword.Smokescreen);
        }

        Api.FireTrigger("OnCardLocationMoved", card, card.Owner, "OnOtherCardLocationMoved",
            eventArgs: new object?[] { card, (int)oldLocation, (int)newLocation, false, "" },
            eventSubject: card,
            namedArgs: named,
            oldLocation: oldLocation,
            newLocation: newLocation);
    }

    public GameState State { get; }

    public IReadOnlyList<string> LeftDeckList { get; }
    public IReadOnlyList<string> RightDeckList { get; }

    public CardApi Api { get; private set; } = null!;

    /// <summary>
    /// 因为**射程不够跨前线**而被拒的攻击次数（诊断用，只增不改行为）。
    ///
    /// 判据见 <see cref="CanReachAcrossFrontline"/>。加这个计数器是因为
    /// 「攻击被拒」在回放里只有一句笼统的失败原因，分不清是射程拒的还是
    /// 召唤失调拒的 —— 而这次改动会让一大批历史回放里的攻击被拒。
    /// </summary>
    public int OutOfRangeAttacksRejected { get; private set; }

    /// <summary>
    /// 「从牌库里挑一张」的外部答复源 —— 对应 <c>CardApi</c> 的 <c>selectCardToDraw</c>
    /// （Develop / 选牌）里的**玩家选择**那一步。
    ///
    /// 为什么需要这个钩子（依据是反编译，不是推测）：
    /// <list type="bullet">
    /// <item>卡自己的 <c>selectCardToDraw(cardSelectingCardToDraw, selectFromTopOfDeck, isEffect,
    ///   out drawnCardID)</c>（签名见 <c>ref/kards-sim/.../_index.g.cs</c>）在候选多于 1 张时
    ///   **不自己抽牌**，而是 <c>NotifySelectCardToDrawPending</c> 把候选交给玩家
    ///   （BP_CardFunctions 里那条 <c>L_0D61</c> 分支），抽牌发生在**答复到达之后**
    ///   （同函数的单候选分支 <c>L_0B57</c> 是现成的对照：直接
    ///   <c>DrawSpecificCardFromDeckBySide</c> + <c>OnHandTargetSelected</c>）。</item>
    /// <item>真实客户端把答复作为紧凑动作 <c>CS</c>（全名
    ///   <c>XActionCardToDrawSelected</c>，见 BP_OnlineMatch 的分派开关链）发上来，
    ///   参数是 <c>[cardBeingPlayed, 候选下标, 选中卡的卡组码]</c>。</item>
    /// </list>
    ///
    /// 参数：正在结算的那张卡（挑牌的主体）、<c>selectFromTopOfDeck</c>（决定走哪一族）、
    /// <c>isEffect</c>（蓝图原样传入）。
    ///
    /// ⚠️ 返回值有**两种含义**，取决于 <c>selectFromTopOfDeck</c>：
    /// <list type="bullet">
    /// <item><c>true</c>（牌库/占卜族）：必须返回**牌库里那张卡的实例**（仍在牌库中）。</item>
    /// <item><c>false</c>（Develop 族）：返回一张**卡池模板**实例即可 ——
    ///   只有 <c>Name</c> 会被用到（内核按这个名字 `Create` 一张新卡，
    ///   依据是 `OpponentActionsCardToDrawSelected` 里的 `CreateCard(...)`）。
    ///   传牌库实例也不会出错，但语义上不是同一回事。</item>
    /// </list>
    ///
    /// 返回 null = 没有答复，由 <c>CardApi</c> 走兜底：牌库族取
    /// <c>GetDeckBySide(side).DeckCardIDs[0]</c>（蓝图 `BP_Logic.autoPickCardToDraw`），
    /// Develop 族取候选表第一张（**蓝图没有这条兜底**，是本内核的近似）。
    /// </summary>
    public Func<CardInstance?, bool, bool, CardInstance?>? PickCardToDraw { get; set; }

    /// <summary>统计信息。</summary>
    public int TurnCount => State.Turn;
    public List<string> Log { get; } = new();

    private void Say(string msg) => Log.Add($"[T{State.Turn}/{State.ActiveSide.ToWire()}] {msg}");

    // ==================== 开局 ====================

    public void Start()
    {
        // HQ：左右各一，防御 20
        var leftHq = State.Create("card_location_london", Side.Left, CardLocation.BoardHqLeft, 0);
        leftHq.Defense = InitialHqDefense;
        leftHq.MaxDefense = InitialHqDefense;

        var rightHq = State.Create("card_location_london", Side.Right, CardLocation.BoardHqRight, 0);
        rightHq.Defense = InitialHqDefense;
        rightHq.MaxDefense = InitialHqDefense;

        BuildDeck(Side.Left, LeftDeckList);
        BuildDeck(Side.Right, RightDeckList);

        // 洗牌（确定性）
        ShuffleDeck(Side.Left);
        ShuffleDeck(Side.Right);

        // 起手
        DealOpeningHand(Side.Left, FirstPlayerHand);
        DealOpeningHand(Side.Right, SecondPlayerHand);

        // kredit 初始 **0**，让下面的 `StartTurn(Left)` 自己加出先手 T1 的 1 点。
        //
        // ⚠️ 这里曾经写的是 1，注释还写着"先手方 kredit 上限 1，后手 1"，
        //    但 `StartTurn` 里是 `MaxKredits + 1` —— 于是先手 T1 的上限变成 **2**。
        //    后果不是"多一点费用"这么轻：先手 T1 能连出两张 1 费牌，
        //    200 局自对弈的**先手胜率因此到 98%**（真实 KARDS 约 50~55%）。
        //
        // 真实数据（回放 kredit 追踪实测）：
        //     right T2 max=1   left T3 max=2   left T5 max=3
        // 也就是**每人第 1 回合都是 1**，之后每个自己的回合 +1。正确序列是：
        //     init 0 → StartTurn(Left) → left 1（T1）
        //            → StartTurn(Right) → right 1（T2）
        //            → StartTurn(Left) → left 2（T3）
        // 所以初始值必须是 0。**别再把它们改成 1** —— 那等于先手白送一个 kredit 槽位。
        State.SetMaxKredits(Side.Left, 0);
        State.SetMaxKredits(Side.Right, 0);
        State.SetKredits(Side.Left, 0);
        State.SetKredits(Side.Right, 0);

        State.StartingSide = Side.Left;
        State.ActiveSide = Side.Left;
        State.Turn = 1;

        Api.FireTrigger("OnStartOfGame", null, Side.Left);
        Api.FireTrigger("OnStartOfGame", null, Side.Right);

        // 先手的第 1 回合不摸牌 —— 判据在 `StartTurn` 里（`State.Turn != 1`），
        // 这里不用传参。见 `StartTurn` 的 `draw` 参数说明（BP_Logic.CanSideDrawCards）。
        StartTurn(Side.Left);
    }

    private void BuildDeck(Side side, IReadOnlyList<string> cards)
    {
        int n = 0;
        foreach (string name in cards)
        {
            State.Create(name, side, side.DeckOf(), n++);
        }
    }

    private void ShuffleDeck(Side side)
    {
        var deck = State.Deck(side).ToList();
        State.Random.Shuffle(deck);
        for (int i = 0; i < deck.Count; i++)
        {
            deck[i].LocationNumber = i;
        }
    }

    private void DealOpeningHand(Side side, int count)
    {
        var deck = State.Deck(side).ToList();
        for (int i = 0; i < count && i < deck.Count; i++)
        {
            State.Move(deck[i], side.HandOf());
        }
    }

    // ==================== 回合 ====================

    /// <summary>开始一个回合。</summary>
    /// <param name="draw">
    /// 回合开始是否摸牌。**null = 按客户端规则自动判定**（推荐），
    /// 只有调用方有明确理由时才显式覆盖。
    ///
    /// 规则来自**反编译的 `BP_Logic.CanSideDrawCards`**（不是口述、不是猜）：
    /// <code>
    /// canDraw = false  当 (GetTurnNumber() == 1 &amp;&amp; !isTutorialGame)
    /// </code>
    /// 也就是**全局回合号 == 1 的那一次回合开始不摸牌**（只有先手的第 1 回合），
    /// 之后每回合都摸。
    ///
    /// 实测三方一致：反编译蓝图判据 + 服务端发牌数据（left 4/35、right 5/34）
    /// + 真机快照（act=1 手牌与发牌逐数相同，act=3 后手才 +1）。
    /// </param>
    public void StartTurn(Side side, bool? draw = null)
    {
        bool doDraw = draw ?? (State.Turn != 1);

        State.ActiveSide = side;

        // kredit 上限 +1（上限 12），并回满
        State.SetMaxKredits(side, Math.Min(MaxKreditCap, State.MaxKredits(side) + 1));
        State.SetKredits(side, State.MaxKredits(side));

        // 「本回合打出过哪些牌」按回合清空（客户端 GetCardsPlayedThisTurn 的语义）。
        // ⚠️ 必须在这里清、而不是在 EndTurn 里清：回放路径上 XActionStartOfTurn 与
        //    EndTurn 的配对并不严格（见 ReplayRunner 的 turnStarted 处理）。
        State.CardsPlayedThisTurn.Clear();

        // 重置本单位行动状态
        foreach (var unit in State.Board(side).ToList())
        {
            unit.HasAttackedThisTurn = false;
            unit.HasMovedThisTurn = false;
            unit.OperationsUsedThisTurn = 0;
        }

        RecordAction("XActionStartOfTurn", side, new Dictionary<string, object?>
        {
            ["side"] = side.ToWire(),
        });

        // 「回合开始之前」—— 出处 `out/bp-cardfn.json` 函数
        // `ExecuteBeforeStartOfTurnEvents`（i=307 广播 `OnBeforeStartOfTurn`），
        // 唯一调用点是 `BP_Logic::StartTurnBySide` i=1047（`out/bp-logic.json`）。
        // 签名 `BaseCardObject.h:733 void OnBeforeStartOfTurn();`（无参，无"别人"变体）。
        Api.FireTrigger("OnBeforeStartOfTurn", null, side);

        Api.FireTrigger("OnStartOfTurn", null, side, "OnOtherStartOfTurn");

        // 抽牌（全局回合 1 跳过，见 `draw` 参数说明）
        if (doDraw)
        {
            // `StartOfTurnDraw = true` —— 出处 `BP_Logic::StartTurnBySide` i=1175 调
            // `DrawTopCardFromDeck(sideStartTurn, 0, False, False, **True**, 0.4, False, out)`，
            // 参数顺序取自 `ref/kards-sim/KardsSim/Generated/_index.g.cs:1952`
            //   ["DrawTopCardFromDeck"] = { deckSide, instigatorID, opponentDraw,
            //                              cardSeen, startOfTurnDraw, delay, scryingDraw, drawnCard }
            // 对照：`ExecuteScryingEffectBySide` i=2005 传的是 (…, False, 0.4, True, …)
            // —— 即"占卜抽"而不是"回合开始抽"，两者互斥。
            DrawCard(side, startOfTurnDraw: true);
        }
    }

    public void EndTurn(Side side)
    {
        Api.FireTrigger("OnEndOfTurn", null, side, "OnOtherEndOfTurn",
            eventArgs: new object?[] { State.Turn });

        RecordAction("XActionEndOfTurn", side, new Dictionary<string, object?>
        {
            ["side"] = side.ToWire(),
            ["reason"] = "endTurnButton",
        });

        if (State.IsFinished)
        {
            return;
        }

        State.Turn++;
        StartTurn(side.Opposite());
    }

    // ==================== 抽牌 ====================

    /// <summary>
    /// 抽一张牌。牌库空则吃疲劳伤害；**手牌已满则这张牌直接进弃牌堆（不进手牌）**。
    ///
    /// 手牌上限的判据与动作全部照抄反编译蓝图：
    /// <code>
    /// BP_CardFunctions.DrawTopCardFromDeck
    ///   i=712  FetchCardsByLocation(手牌) → nextHandLocation(=QtyInLocation), isHandFull
    ///   i=739  （此时牌刚从牌库移除、还没进手牌 ⇒ 这个值就是「抽之前」的手牌数）
    ///   i=758  JumpIfNot(isHandFull) → 1042        ← 没满：走 1042 那条（进手牌）
    ///   i=772      drawnCardRef.location = 8       ← ★ 满了：直接置弃牌堆
    ///   i=883      NotifyDrawCardFromDeck(…, PreQtyInHand=nextHandLocation, HandFull=isHandFull, …)
    ///   i=1042 drawnCardRef.locationNumber = nextHandLocation   ← 没满才写位置
    ///   i=1091 drawnCardRef.location = handLocation
    /// BP_CardFunctions.DiscardOnDrawingWithFullHands(cardID, insitigatorID, oldLocation)
    ///   i=0    SetCardLocationAndLocNumber(cardID, 8 /*Discard*/, 0)
    ///   i=53   JumpIfNot(IsActionProcess()) → 132
    ///   i=67   CardFunctionsNotifier.NotifyDiscardCard(cardID, insitigatorID, oldLocation, false, false)
    /// </code>
    /// 上限 9 的来源是 `BP_GameState_Battle.FetchCardsByLocation` case 3,4 → `MaxQty = IntConst(9)`
    /// （见 <see cref="GameState.HandCapacity"/>）。
    ///
    /// ⚠️ 2026-09-26 修：**这条检查以前完全不存在**，`DrawCard` 无条件把牌塞进手牌，
    /// 于是手牌能涨到 12 张（NN 对局日志实测），而真实规则是满了直接烧牌。
    /// </summary>
    public CardInstance? DrawCard(Side side, bool startOfTurnDraw = false)
    {
        var deck = State.Deck(side).ToList();
        if (deck.Count == 0)
        {
            int fatigue = State.Fatigue(side) + 1;
            State.SetFatigue(side, fatigue);
            Say($"{side.ToWire()} 牌库空，疲劳伤害 {fatigue}");
            DamageHq(side, fatigue);
            return null;
        }

        var card = deck[0];

        // 「抽**之前**」的手牌数 —— 蓝图 `DrawTopCardFromDeck` i=712 就是在这个时刻
        // （牌已从牌库移除、还没进手牌）取 `FetchCardsByLocation(手牌).QtyInLocation`，
        // 而且同一个值在「没满」分支里被当成 `locationNumber` 用（= 追加下标 = 当前张数），
        // 反证它确实是「抽之前」。
        // ⚠️ 以前这里写的是 `State.Move(...)` **之后**再读 `Hand(side).Count()`，是「抽之后」，
        //    比蓝图多 1（`ZActionDrawCardFromDeck` 的 PreQtyInHand 一直偏大 1）。
        int preQtyInHand = State.Hand(side).Count();

        // ★ 手牌上限：满了就烧牌，不进手牌。
        if (preQtyInHand >= GameState.HandCapacity)
        {
            Say($"{side.ToWire()} 手牌已满（{preQtyInHand}/{GameState.HandCapacity}），{card} 被弃掉");
            State.Move(card, CardLocation.Discard);
            FireSubAction("ZActionDrawCardFromDeck", new[]
            {
                ActionValue2.Int("cardID", card.CardId),
                ActionValue2.Str("side", side.ToWire()),
                ActionValue2.Int("PreQtyInHand", preQtyInHand),
                ActionValue2.Bool("handIsFull", true),
                ActionValue2.Int("nextFatigueDamage", State.Fatigue(side) + 1),
            });
            return null;
        }

        State.Move(card, side.HandOf());
        FireSubAction("ZActionDrawCardFromDeck", new[]
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Str("side", side.ToWire()),
            ActionValue2.Int("PreQtyInHand", preQtyInHand),
            ActionValue2.Bool("handIsFull", false),
            ActionValue2.Int("nextFatigueDamage", State.Fatigue(side) + 1),
        });

        // 「别的卡被抽到手」触发点。**必须接**：`card_unit_85_pioneer_company` /
        // `card_unit_big_red_one` / `card_event_committed_crew` 三张光环都订阅它，
        // 靠它把 buff 补给**新入手**的牌（这就是实测「没打出指令前手牌里所有指令都显示 -1」
        // 那个现象的来源：光环进场时只补了当时手牌里的，后续抽上来的得靠这个事件）。
        //
        // ⚠️ **两个事件都要发**，这是本轮的 bug 修复点之一。
        //
        // 出处：`out/bp-cardfn.json` 函数 `ExecuteOnDrawnFromDeck(DrawnCardID, StartOfTurnDraw, drawnSide)`：
        //   i=132  `drawnCard.OnCardDrawnFromDeck()`      ← **自己**（此前从未派发）
        //   i=191  FetchAllCardsWithEventTrigger(42)      ; 42 = OnOtherCardDrawnFromDeck
        //   i=708  `item.OnOtherCardDrawnFromDeck(drawnCardID, StartOfTurnDraw, drawnSide)`
        //
        // 旧实现只发了 `OnOtherCardDrawnFromDeck`，而 `CardApi.FireTrigger` 的
        // `broadcast` 判定（`programName.StartsWith("OnOther")`）会**把主体自己排除**，
        // 兜底那一段又被 `subjectBroadcast` 挡住 ⇒ 22 张订阅 `OnCardDrawnFromDeck`
        // 的卡永远收不到（`card_event_guarilla_warfare_school` 这类"抽到牌时"效果全死）。
        var drawnNamed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["drawnCardID"] = card.CardId,
            ["StartOfTurnDraw"] = startOfTurnDraw,
            ["drawnSide"] = (int)side,
        };

        Api.FireTrigger("OnCardDrawnFromDeck", card, side,
            eventArgs: new object?[] { startOfTurnDraw, (int)side },
            eventSubject: card, namedArgs: drawnNamed);

        Api.FireTrigger("OnOtherCardDrawnFromDeck", card, side,
            eventArgs: new object?[] { card.CardId, startOfTurnDraw, (int)side },
            eventSubject: card, namedArgs: drawnNamed);
        return card;
    }

    // ==================== 部署 ====================

    /// <summary>能否打出这张牌（费用 + 目标）。</summary>
    public bool CanPlay(CardInstance card, out string reason)
    {
        reason = "";
        if (card.Owner != State.ActiveSide)
        {
            reason = "不是当前行动方";
            return false;
        }

        if (card.Location != State.ActiveSide.HandOf())
        {
            reason = "不在手牌";
            return false;
        }

        if (card.KreditCost > State.Kredits(card.Owner))
        {
            reason = "kredit 不足";
            return false;
        }

        if (card.Definition.IsUnit && HalfBoardFull(card.Owner))
        {
            reason = "半场已满";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 半场是不是满了（单位出不来）。
    ///
    /// ⚠️ **半场和前线是两个独立上限**，不能合计 —— 这里原先写的是
    /// `Board(side).Count() >= SupportLineCapacity + State.FrontlineLimiter`
    /// （= 4 + 1 = 5），把「半场 4 + 前线 1」当成一个总数。
    /// 蓝图 `FetchCardsByLocation` 里 case 5,6 和 case 7 各给各的 `MaxQty`
    /// （半场 5、前线 5），判满也是分别判的。
    ///
    /// 出厂口 `BP_Logic::CanPlayCardFromHand` 印证：i=890 只对单位查，
    /// i=959 `IsLocationFull(SupplyLineLocationFromSide(side))` ——
    /// **只查半场（5/6），不查前线**。
    ///
    /// 计数要**含 HQ**（蓝图的循环只按 location 过滤、不排除 HQ）。
    /// </summary>
    private bool HalfBoardFull(Side side)
        => State.Cards(side, side.HqOf()).Count >= GameState.HalfBoardCapacity;

    /// <summary>从手牌打出一张牌。</summary>
    /// <param name="skipLeaveTrigger">
    /// 跳过「离场」触发点。给「同一张卡先离场再进场」的移位用（见 <see cref="MoveUnit"/>）：
    /// 不跳过的话 <c>MoveUnit</c> 里那次 `OnLeaveBoardOrOwner` 和这里的
    /// `OnEnterPlay` 会各触发一次，光环类卡会被撤销再重挂。
    /// </param>
    public bool PlayCard(CardInstance card, CardInstance? target = null, bool skipLeaveTrigger = false)
    {
        if (!CanPlay(card, out string reason))
        {
            return false;
        }

        State.AddKredits(card.Owner, -card.KreditCost);

        RecordAction("XActionPlayCardFromHand", card.Owner, new Dictionary<string, object?>
        {
            ["cardID"] = card.CardId,
            ["location"] = "Hand",
            ["side"] = card.Owner.ToWire(),
        });

        FireSubAction(card.Definition.IsUnit || card.Definition.IsLocationCard
            ? "ZActionPlayCardFromHand"
            : "ZActionPlayOrderCardFromHand", new[]
        {
            ActionValue2.Int("instigatorID", card.CardId),
            ActionValue2.Int("targetCardID", target?.CardId ?? 0),
            ActionValue2.Int("playedDirectly", 1),
        });

        if (!skipLeaveTrigger && card.Location.IsBoard())
        {
            // 已经在场上的卡被打出（`MoveUnit` 的「先回手再进场」路径）：
            // 先广播离场触发点，让挂在它身上的光环把 buff 撤掉。
            FireLeaveTrigger(card, CardLocation.BoardFrontline);
        }

        // ---- 「别的卡**即将**从手牌被打出」----
        //
        // 出处 `out/bp-cardfn.json` 函数 `CardPlayedFromHand`：
        //   i=1322..1386  遍历 FetchAllCardsWithEventTrigger(19) 并把主体排除
        //                 （i=1386 `NotEqual_ObjectObject(item, cardPlayed)`）
        //   i=1493        `item.OnBeforeOtherCardPlayedFromHand(cardPlayed)`
        // 触发号 19 的依据：`ERegisteredCardFunction.h` 逐项数下来第 20 项
        //   = `OnBeforeOtherCardPlayedFromHand`。
        // 签名 `BaseCardObject.h:739`。**没有"自己"那一路** —— 39 张订阅者全是别人。
        // 时机：在卡离手/落场**之前**（蓝图的 `CardPlayedFromHand` 开头段就是这里）。
        // ⚠️ 必须显式 `broadcastName: true`：这个名字以 `OnBefore` 开头、不是 `OnOther`，
        //    靠命名约定判广播会把它当成"只发给主体" ⇒ 39 张订阅者全部收不到。
        Api.FireTrigger("OnBeforeOtherCardPlayedFromHand", card, card.Owner,
            eventArgs: new object?[] { card },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardPlayed"] = card,
            },
            broadcastName: true);

        // ---- ① 先离开手牌，再结算效果 ----
        //
        // ⚠️ 顺序**不能反**，这是从 `ref/kards-sim` 的可运行实现里逐行确认的
        //    （`GameEngine.Actions.cs` 的 `DoPlayCard`）：
        //        p.Hand.RemoveAt(...)          ← 先离手
        //        if (c.IsOrder) PlayOrder(c);  ← 再结算指令
        //        else { c.Loc = Board; ... }   ← 单位先落场
        //        FirePlayTriggers(c); FireEnterPlayTriggers(c);
        //        RunCardEffect(c, OnPlayedFromHand);   ← 最后才是战吼
        //
        //    旧实现是「先结算效果、后离手」，会踩两个坑：
        //    (a) 指令结算时**还带着手牌身份**，`card_unit_85_pioneer_company`
        //        这类只作用于「手牌里的牌」的光环会在结算过程中把刚打出的那张
        //        重新 buff 一遍；
        //    (b) 光环的还原分支判 `IsLocatedOnBoard(刚打出的牌)`（真机里这条判据
        //        对指令也成立），「先结算后离手」时判据必然为假 → 永不还原。
        if (card.Definition.IsUnit || card.Definition.IsLocationCard)
        {
            PlaceOnBoard(card);
        }
        else
        {
            // 指令牌：先进弃牌堆再结算（游戏里指令是"用完即弃"，
            // 结算期间它已经不在手牌里了）
            State.Move(card, CardLocation.Discard);
        }

        // 「本回合进场的回合数」——**指令也要记**。
        // 卡蓝图里的实例变量 `enterPlayOnTurn` 就是它，实测
        // `card_event_committed_crew` 的每个分支都先判 `enterPlayOnTurn > 0`
        // （"这张牌是不是本回合打出的"）—— 只有单位记的话，指令类光环
        // （committed_crew 自己就是 10 费指令）会全部静默失效。
        // `PlaceOnBoard` 里也会设一次，值相同，重复设没有副作用。
        card.EnteredPlayOnTurn = State.Turn;

        // ---- ② 本回合打出过的牌 ----
        // 放在 `OnEnterPlay` **之前**：光环的 `anyOrderPlayedThisTurn` 是
        // 「本回合第一张指令打完了没有」的判据，而 `OnEnterPlay` 是**刚打出的这张**
        // 自己的进场时机 —— 它自己当然算"已经打出"。
        State.CardsPlayedThisTurn.Add(card);

        // ---- ③ 进场触发点 ----
        Api.FireTrigger("OnEnterPlay", card, card.Owner, "OnOtherCardEnterPlay", eventArgs: new object?[] { card, 0 });

        // ---- ④ 部署 / 战吼 ----
        //
        // 蓝图 `BP_CardFunctions::CardPlayedFromHand` 把「战吼」拆成两条路
        // （`si=3640 JumpIfNot 5840 cond=cardPlayed.hasDeployment`）：
        //   · 没有 `hasDeployment`（**全部 732 张指令** + 21 张没有该字段的单位）
        //     → si=5840 处 `_triggerMultiple` 恒为 0，落到 si=6114 **跑一次** OnPlayedFromHand。
        //   · 有 `hasDeployment`（249 张单位）→ 事件 14 取消钩子 → 事件 23 取 triggerMultiple
        //     → OnPlayedFromHand 跑 `1 + triggerMultiple` 次。
        // 两条路在「不取消、不翻倍」时**完全一样**，所以旧实现（无条件跑一次）对绝大多数
        // 对局是对的；差别只在取消与翻倍。见 `RunDeploymentEffect` 的注释。
        RunDeploymentEffect(card, target);

        // ---- ⑤「别的卡从手牌被打出」----
        // 这一条以前**根本没接**，所以 card_unit_85_pioneer_company /
        // card_event_committed_crew 的 OnOtherCardPlayedFromHand 分支
        // （就是它们还原 buff / 给部署单位加成的那一支）从来没执行过。
        Api.FireTrigger("OnOtherCardPlayedFromHand", card, card.Owner, eventArgs: new object?[] { card });

        // ---- ⑥ 山地加成（`GiveAlpineBonus`）----
        //
        // 出处 `out/bp-cardfn.json` → `PlayCardFromHand`（105 条语句）：
        // <code>
        // si=2114  CardPlayedFromHand(tmpCardToPlay, targetCardID)   ; 上面 ①..⑤ 全在这里面
        // si=2147  GameStateRef.GetExecuteWaitPlayFromHand(out ShouldExecuteWait)
        // si=2192  JumpIfNot(ShouldExecuteWait) -> si=2207
        // si=2207      GiveAlpineBonus(tmpCardToPlay)
        // </code>
        // ⇒ ①「**没走 wait 路径**才在这里给」，走 wait 路径的由
        //    `AfterWaitCardPlayFromHand` si=974 自己给（两条互斥，不会双给）；
        //    ② 时机是 **`CardPlayedFromHand` 整条跑完之后**，所以放在这里而不是 `PlaceOnBoard` 里。
        // ⚠️ 加成是**加法**（`changeType=1`），一个单位只能给一次 —— 见 `GiveAlpineBonus` 的注释。
        Api.GiveAlpineBonus(card);

        CheckDeaths();
        return true;
    }

    /// <summary>
    /// 部署链 —— 蓝图 <c>BP_CardFunctions::CardPlayedFromHand</c> 的 si=3640..6434。
    ///
    /// **这是「一个机制」，不是「249 张卡各写各的」。** 249 张 `hasDeployment` 卡的
    /// 部署文本各自写在**那张卡自己的 `OnPlayedFromHand`** 里；引擎侧只有这一条链：
    /// 门 → 取消钩子（事件 14）→ 取翻倍数（事件 23）→ 跑 `1 + triggerMultiple` 次自己的
    /// `OnPlayedFromHand`。所以内核只要实现这一条，249 张卡的**公共时序**就全对了。
    ///
    /// 逐条出处（`out/bp-cardfn.json`，`StatementIndex` 与 Jump 的 `Offset` 同坐标系，
    /// 已用 `out/audit/p1-jump-targets.py` 验证 1599 条跳转 100% 落在语句集内）：
    /// <code>
    /// si=3640  JumpIfNot 5840 cond=cardPlayed.hasDeployment
    /// si=3676  FetchAllCardsWithEventTrigger(14)     ; OnBeforeOtherCardDeploymentTrigger
    /// si=4081  PopExecutionFlowIfNot(cancelDeploymentEffect)
    /// si=4197  breakFlag = true                      ; 有订阅者取消 ⇒ 跳出
    /// si=4283  JumpIfNot 5702 if !cancelDeploymentEffect
    /// si=4297..4881  取消分支（NotifySideEffectTrigger 'sideeffect.blockdeployment' + 返回）
    /// si=5702  EqualEqual_IntInt(targetCardID, 0)    ; ★ 只有**非指向性**部署才取翻倍数
    /// si=5750  ExecuteOnDeploymentTriggered(cardPlayed, cardPlayed.cardID, out triggerMultiple)
    /// si=5813  _triggerMultiple = triggerMultiple
    /// si=6082/6114  cardPlayed.OnPlayedFromHand(GetCardFromID(targetCardID))   ; 第 1 次
    /// si=6159  if (_triggerMultiple &gt; 0)
    /// si=6230  while (Temp_int_Variable &lt;= _triggerMultiple) { si=6319 OnPlayedFromHand(...) }
    /// </code>
    ///
    /// 「取消 ⇒ 不跑效果」这一条不是靠语句顺序推的：用
    /// `out/audit/p1-cfg.py CardPlayedFromHand 4297` 做可达性分析，从取消分支出发
    /// 可达的 44 条语句**全部**终止于 si=6853 `Return`，不经过 5702/6114。
    /// 卡面文本独立互证：`card_unit_petlyakov_pe_2ft`「Deployment effects do not trigger.」。
    ///
    /// ⚠️ **没做**的一件小事：`si=4308 NotifySideEffectTrigger(side, 'sideeffect.blockdeployment')`
    /// —— 内核里 `NotifySideEffectTrigger` 整条原语都没有实现（副作用通知通道），
    /// 不是本次范围，这里不猜它的子动作名。
    /// </summary>
    private void RunDeploymentEffect(CardInstance card, CardInstance? target)
    {
        // si=3640：没有 hasDeployment 的卡（全部指令 + 21 张没这个字段的单位）走 si=5840，
        // 那条路上 `_triggerMultiple` 恒 0 ⇒ 只跑一次，且**没有取消钩子**。
        if (!card.Keywords.Contains(Keyword.Deployment))
        {
            Api.RunCardEffect(card, target);
            return;
        }

        // si=3676..4278：事件 14 的取消钩子。
        if (Api.FireDeploymentCancelHook(card))
        {
            return;   // si=4297..4881：取消 ⇒ 整条部署效果不跑
        }

        // si=5702/5750：只在「非指向性」部署时取翻倍数（targetCardID == 0）。
        int triggerMultiple = target is null ? Api.SumDeploymentTriggerMultiple(card) : 0;

        // si=6114（第 1 次）+ si=6230..6434（再 triggerMultiple 次）。
        for (int i = 0; i <= triggerMultiple; i++)
        {
            Api.RunCardEffect(card, target);
        }
    }

    /// <summary>
    /// 把刚打出的单位放到**自己半场（支援线）**。
    ///
    /// ⚠️ **不是"前线有空位就上前线"**（那是以往的写法，也是错的）。
    /// 真实数据：250 条真实快照里**前线为空的占 83%**（207/250）——
    /// 单位绝大多数待在底线，上前线是**主动动作**（走 `MoveUnit`，
    /// 且要过「对面占着就推不进去」那条互斥检查，见 <see cref="MoveUnit"/>）。
    ///
    /// 出厂口 `BP_Logic::CanPlayCardFromHand` i=959 判的也是
    /// `IsLocationFull(SupplyLineLocationFromSide(side))` —— **只查半场**，
    /// 说明部署的落点就是半场（5/6），和前线的空位无关。
    ///
    /// `locationNumber` 按蓝图 `GetNextCardLocationNumber` 的规则「追加到队尾」
    /// （`= QtyInLocation`），而且计数**含 HQ** —— 所以第一个单位的槽位是 1，不是 0。
    /// </summary>
    private void PlaceOnBoard(CardInstance card)
    {
        card.EnteredPlayOnTurn = State.Turn;

        CardLocation half = card.Owner.HqOf();
        int slot = State.Cards(card.Owner, half).Count;   // 追加到队尾（含 HQ 占的第 0 格）
        State.Move(card, half, slot);
        Say($"{card} 部署到半场 {half} 槽位 {slot}");
    }

    // ==================== 移动 ====================

    /// <summary>
    /// 把单位推进**前线**（对应 `XActionMoveCardToLine` / `MoveUnitFromSupportToFrontLine`）。
    ///
    /// 规则（`BP_CardFunctions::MoveUnitFromSupportToFrontLine`，27 条语句）：
    /// <code>
    /// i=281  (card.GetOppositeSide() == GameStateRef.GetFrontlineOwnerSide())
    /// i=333      拒绝                       ← ★ 对面占着前线 ⇒ 推不进去
    /// i=468  FetchCardsByLocation(7, …) → isLocationFull
    /// i=543      拒绝                       ← 我方占着但已满(5) ⇒ 也推不进去
    /// </code>
    /// **没有"把对面挤回半场"这条规则** —— 唯一的赶人手段是
    /// `card_unit_black_prince` 的打出效果 `MakeCardRetreat(GetAllCardsInFrontline)`。
    ///
    /// 拥有权由 `BP_CardFunctions::UpdateFrontlineIfNeeded` 维护：
    /// 前线空 → owner = NotAvailable；非空 → owner = 第一张牌的 side。
    /// </summary>
    public bool MoveUnit(CardInstance unit, int targetSlot)
    {
        if (unit.Owner != State.ActiveSide || !unit.Location.IsBoard() || unit.IsHq)
        {
            return false;
        }

        if (!unit.CanMoveThisTurn(State))
        {
            return false;
        }

        if (unit.OperationCost > State.Kredits(unit.Owner))
        {
            return false;
        }

        // 前线互斥：对面占着就推不进去；我方占着且满了也推不进去。
        // （`MoveUnitFromSupportToFrontLine` i=281/333 与 i=468/543）
        //
        // 两条拒绝各记一笔，好在对拍里看出"这条规则是不是在误伤真实动作" ——
        // 静默拒绝是最难查的一类 bug。
        if (State.FrontlineOwner != Side.NotAvailable && State.FrontlineOwner != unit.Owner)
        {
            State.UnimplementedCalls["<frontline-blocked-by-opponent>"] =
                State.UnimplementedCalls.GetValueOrDefault("<frontline-blocked-by-opponent>") + 1;
            return false;
        }

        if (State.FrontlineOwner == unit.Owner
            && State.Cards(unit.Owner, CardLocation.BoardFrontline).Count >= State.FrontlineCapacity)
        {
            State.UnimplementedCalls["<frontline-blocked-full>"] =
                State.UnimplementedCalls.GetValueOrDefault("<frontline-blocked-full>") + 1;
            return false;
        }

        State.AddKredits(unit.Owner, -unit.OperationCost);
        unit.HasMovedThisTurn = true;

        RecordAction("XActionMoveCardToLine", unit.Owner, new Dictionary<string, object?>
        {
            ["cardID"] = unit.CardId,
        });

        FireSubAction("ZActionMoveCardToNewLocation", new[]
        {
            ActionValue2.Int("instigatorID", unit.CardId),
            ActionValue2.Int("locationNumber", targetSlot),
        });

        // 我方已经占着前线时是"挪槽位"，否则是"推进"。
        // 两种情况都要发 `OnFrontlineOwnershipChange`（如果归属真的变了）。
        Side oldOwner = State.FrontlineOwner;
        State.Move(unit, CardLocation.BoardFrontline, targetSlot);
        UpdateFrontlineOwner();
        Api.FireTrigger("OnMoveToFrontline", unit, unit.Owner, "OnOtherCardMoveToFrontline");

        if (oldOwner != State.FrontlineOwner)
        {
            Api.FireTrigger("OnFrontlineOwnershipChange", unit, unit.Owner, "OnOtherFrontlineOwnershipChange");
        }

        return true;
    }

    /// <summary>
    /// 维护前线归属（对应 `BP_CardFunctions::UpdateFrontlineIfNeeded`）：
    /// 前线空 → <see cref="Side.NotAvailable"/>；非空 → **第一张牌的 side**。
    ///
    /// ⚠️ 蓝图用的是 `GetAllCardInBattle()` 的**数组顺序**第一张，
    /// 不是 `locationNumber` 最小的那张。实战里前线只可能有一方的卡
    /// （互斥规则保证），所以两者等价；这里取数组顺序以保持逐字一致。
    /// </summary>
    private void UpdateFrontlineOwner()
    {
        var frontline = State.Cards(Side.Left, CardLocation.BoardFrontline)
            .Concat(State.Cards(Side.Right, CardLocation.BoardFrontline))
            .Where(c => !c.IsHq)
            .ToList();

        State.FrontlineOwner = frontline.Count == 0 ? Side.NotAvailable : frontline[0].Owner;
    }

    // ==================== 离场触发点 ====================

    /// <summary>
    /// 广播「这张卡要离开棋盘（或离开原主）」。
    ///
    /// 卡面里写「部署后持续生效、离场还原」的效果（`OnLeaveBoardOrOwner`，
    /// 全卡池 67 张）全靠这个触发点撤销自己的 buff —— 光环（①）只是它的一个子类。
    /// **必须在真正移动之前调用**：效果里要读卡的旧位置。
    ///
    /// 三个名字一起发，因为实测三种订阅都存在且都在同一个时机：
    /// - `OnLeaveBoardOrOwner`（卡自己）
    /// - `OnOtherCardLeaveBoardOrOwner`（别人，`card_unit_214th_amur` 用）
    /// - `OnAfterOtherCardLeaveBoardOrOwner`（别人，`card_unit_big_red_one` 用；
    ///   名字里的 After 指的是「在其它触发点之后」，实测它和上面那个同一个入口）
    /// </summary>
    /// <param name="goingToLocation">卡要去哪（卡牌蓝图的第 1 个入参就是它）。</param>
    public void FireLeaveTrigger(CardInstance card, CardLocation goingToLocation)
    {
        if (card.IsHq)
        {
            return;   // HQ 不会「离场」
        }

        var subject = card;
        FireSubActionWithCard("ZActionLeaveBoardOrOwner", subject, (int)goingToLocation);

        // 两遍发，因为「别人」那一支有两个并列的程序名：
        //   OnOtherCardLeaveBoardOrOwner       ← card_unit_214th_amur
        //   OnAfterOtherCardLeaveBoardOrOwner  ← card_unit_big_red_one
        // 主体自己（selfProgramName = OnLeaveBoardOrOwner）只在第一遍发一次。
        //
        // `goingToLocation` 是这两个事件的**第一个入参**，`card_unit_214th_amur`
        // 拿它和 [5,6,7]（双方半场 + 前线）比 —— 去弃牌堆(8)/手牌(3,4)/牌库(1,2) 时
        // 它不还原 buff。这不是 bug：真机走 discard 时**根本不发这个事件**
        // （走的是 OnDestroyed 那条链），所以这里传真实去向，让判据自然成立/不成立。
        var eventArgs = new object?[] { card, (int)goingToLocation, 0 };
        Api.FireTrigger("OnLeaveBoardOrOwner", subject, subject.Owner,
            "OnOtherCardLeaveBoardOrOwner", "OnLeaveBoardOrOwner", eventArgs, goingToLocation);
        Api.FireTrigger("OnLeaveBoardOrOwner", subject, subject.Owner,
            "OnAfterOtherCardLeaveBoardOrOwner", "OnLeaveBoardOrOwner", eventArgs, goingToLocation);

        // 「离场之后」—— 出处 `out/bp-cardfn.json` 函数
        // `ExecuteOnAfterLeaveBoardOrOwnerEvents`：
        //   `OnAfterLeaveBoard(ECardLocationEnum goingToLocation)`（自己，签名 `BaseCardObject.h:808`）
        //   `OnAfterOtherCardLeaveBoardOrOwner(UBaseCardObject* cardLeaving, ECardLocationEnum OldLocation)`
        // 注意：**两个变体的第 2 个参数语义不同** —— 自己那个是"去向"，
        // 别人那个是"**旧位置**"（槽位名 `OldLocation`）。所以这里两个名字都放进具名载荷。
        var afterNamed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["goingToLocation"] = (int)goingToLocation,
            ["OldLocation"] = (int)card.Location,
            ["cardLeaving"] = card,
        };
        Api.FireTrigger("OnAfterLeaveBoard", subject, subject.Owner, "OnAfterOtherCardLeaveBoardOrOwner",
            eventArgs: eventArgs, goingToLocation: goingToLocation, namedArgs: afterNamed);
    }
    /// <summary>给「离场」这类带参数的事件记一条子动作（便于回放比对）。</summary>
    private void FireSubActionWithCard(string name, CardInstance card, int goingToLocation)
        => FireSubAction(name, new[]
        {
            ActionValue2.Int("instigatorID", card.CardId),
            ActionValue2.Int("goingToLocation", goingToLocation),
        });

    // ==================== 攻击 ====================

    /// <summary>单位攻击目标（单位或 HQ）。</summary>
    public bool Attack(CardInstance attacker, CardInstance defender)
    {
        if (attacker.Owner != State.ActiveSide || !attacker.Location.IsBoard() || attacker.IsHq)
        {
            return false;
        }

        if (!attacker.CanOperateThisTurn(State))
        {
            return false;
        }

        // 防御者合法性：**必须还在场上**。
        //
        // 为什么单独加这一条：`Attack` 原先对防御者**一个字都不校验**，
        // 完全依赖调用方（GreedyBot 用 `LegalTargets`）给对目标。
        // 拿一个已经进弃牌堆的卡来打，会走完整套结算：扣行动费、加攻防 buff、
        // 触发 OnAfterAttack —— 等于凭空多打一次。
        // 调用方一旦持有过期引用（决策与结算之间夹了别的效果），就会静默发生。
        // 宁可在这里拒掉并记一笔，也不要"看着能跑"。
        if (!defender.IsAlive || !defender.Location.IsBoard())
        {
            State.UnimplementedCalls["<attack-on-non-board-target>"] =
                State.UnimplementedCalls.GetValueOrDefault("<attack-on-non-board-target>") + 1;
            return false;
        }

        if (attacker.OperationCost > State.Kredits(attacker.Owner))
        {
            return false;
        }

        // ⚠️ 这里**不再**因为 `attacker` 被压制就禁止攻击。
        //
        // 蓝图里"压制"的语义是**不触发伤害修正**，不是"不能行动"：
        // `cardsCheckFunctions::CanAttack`（210 条语句）里 **`isSuppressed` 出现 0 次**
        // （`out/audit/p0-guard-buff-evidence.py`；dump = `out/bp-cardscheck.json`）；
        // 它只出现在伤害链路的分流上：
        //   `ExecuteBeforeReceiveDamage`  si=38  `JumpIfNot(card.isSuppressed)` → 跳过修正
        //   `ExecuteOnDealDamageAddDamage` si=5  同上
        //   `ExecuteOnSurvivedCombatEvents` si=38 / `MakeVeteran` si=2427 也是同一种"压制就不广播"
        // 旧实现把"被压制 ⇒ 禁攻击"当成规则，会把被压制的单位整个冻住（比蓝图严格得多）。
        // `Pinned`（i=1839 `attacker.IsPinned() && !HasCustomAbility("canOperateWhilePinned")`）
        // 是真的禁止攻击，保留。
        if (attacker.Keywords.Contains(Keyword.Pinned))
        {
            return false;
        }

        // 掩护（`isBeingGuarded`）—— 出处 `cardsCheckFunctions::CanAttack`：
        // <code>
        // si=2492 IsBomber(attackerCard)   si=2533 IsArtillery(attackerCard)
        // si=2574 or = IsBomber || IsArtillery
        // si=2612 JumpIfNot(or) -> si=2627      ; 为假（不是轰炸/火炮）⇒ 落下去查掩护
        // si=2626     PopExecutionFlow           ; 是轰炸/火炮 ⇒ 跳过整个掩护判定
        // si=2627 PopExecutionFlowIfNot(defenderCard.isBeingGuarded)   ; 没被掩护 ⇒ 跳过
        // si=2659 IsLocation(defenderCard)
        // si=2700 JumpIfNot(IsLocation) -> si=2788
        // si=2714     canAttack=False; failReason="hq_is_being_garded"; return
        // si=2788     canAttack=False; failReason="is_being_guarded";   return
        // </code>
        if (!IsBomber(attacker) && !Api.IsArtillery(attacker) && IsBeingGuarded(defender))
        {
            State.UnimplementedCalls["<attack-on-guarded-target>"] =
                State.UnimplementedCalls.GetValueOrDefault("<attack-on-guarded-target>") + 1;
            return false;
        }

        // 跨前线射程判据 —— 逐字来自蓝图，见 CanReachAcrossFrontline 的出处注释。
        // 这是本次改动**唯一**新增的判据，位置放在其它合法性检查之后、
        // 任何状态变更（扣 kredit / 记 hasAttackedThisTurn / 发触发）之前。
        if (!CanReachAcrossFrontline(attacker, defender))
        {
            OutOfRangeAttacksRejected++;
            return false;
        }

        // 烟幕（`CanAttack` si=3596-3793，出处见 `LegalTargets` 里那段注释）。
        // 蓝图里这一段的落点比掩护更靠后（si=3596 vs si=2627），所以放在射程之后、
        // 与蓝图同序 —— 拒绝本身与顺序无关，但保持同序便于以后逐条对账。
        if (defender.Keywords.Contains(Keyword.Smokescreen))
        {
            State.UnimplementedCalls[defender.IsHq
                ? "<attack-on-location-with-smokescreen>"
                : "<attack-on-smokescreen-target>"] =
                State.UnimplementedCalls.GetValueOrDefault(defender.IsHq
                    ? "<attack-on-location-with-smokescreen>"
                    : "<attack-on-smokescreen-target>") + 1;
            return false;
        }

        State.AddKredits(attacker.Owner, -attacker.OperationCost);
        attacker.HasAttackedThisTurn = true;

        // ---- 烟幕：**自己攻击之后消失** ----
        //
        // 出处 `out/bp-cardfn.json` → `AttackCard`（168 条语句）：
        // <code>
        // si=2911  _attackerCard.IsLocatedOnBoard(out isIt_2)
        // si=2952  JumpIfNot 3640 if !isIt_2                 ; 攻击者不在场 ⇒ 跳过整段
        // si=2966  JumpIfNot 3590 if _attackerCard.isSuppressed ; ★ 被压制 ⇒ 跳过
        // si=3511  _attackerCard.cardFunction.RemoveSmokescreen(attackerCardID, attackerCardID, true, false)
        // si=3590  _attackerCard.OnBeforeAttack(_defenderCard)
        // </code>
        // ⚠️ 两个门槛都要：**不在场** 和 **被压制** 都不移除。
        //    时机在 `OnBeforeAttack` **之前**（si=3511 < si=3590）。
        if (!attacker.Keywords.Contains(Keyword.Suppressed)
            && attacker.Keywords.Contains(Keyword.Smokescreen))
        {
            Api.RemoveKeyword(attacker, Keyword.Smokescreen);
        }

        RecordAction("XActionAttackCard", attacker.Owner, new Dictionary<string, object?>
        {
            ["attackerCardID"] = attacker.CardId,
            ["defenderCardID"] = defender.CardId,
        });

        Api.FireTrigger("OnBeforeAttack", attacker, attacker.Owner, "OnBeforeOtherCardAttacks");
        Api.FireTrigger("OnBeforeAttack", defender, defender.Owner);

        int attackerDamage = attacker.Attack;
        int defenderDamage = defender.IsHq ? 0 : defender.Attack;   // 反击

        // 攻击日志：原先只记「谁被摧毁」「HQ 受伤」，看不出**谁打的、打了几次**。
        // 实测就因此说不清「PANTHER A ZIMMERIT 是不是一回合攻击了两次」——
        // 那次它先进场打死 M2A4、接着又对 HQ 打了 11 点（卡面只有 2 攻）。
        // 把攻击者/目标/伤害/费用/剩余全部打出来，一眼就能看出重复攻击和数值异常。
        //
        // ⚠️ **必须在 `DealDamage` 之前打印**。原来写在结算之后，`target.ToString()`
        //    读的是**打完、且已经 `Destroy` 进弃牌堆**之后的位置，于是日志里出现
        //    `→ xxx@Discard#2` 这种"攻击弃牌堆里的卡"的假象 —— 实际上目标在
        //    这一击开始时还好端端在场上（`Attack` 入口刚校验过 `defender.Location.IsBoard()`）。
        //    `card_event_lotta_svard`（+1 防御）这类效果也会在中间改数值，
        //    之后再打印就分不清"打的时候是多少"。
        //    「目标剩」改成**算出来的**（`当前防御 - 本次伤害`），数值和以前一致，
        //    但不再依赖结算后的现场。
        Say($"⚔ {attacker} → {(defender.IsHq ? $"{defender.Owner.ToWire()} HQ" : defender.ToString())}"
            + $"  伤害 {attackerDamage}（攻 {attacker.Attack}，行动费 {attacker.OperationCost}，"
            + $"余 kredit {State.Kredits(attacker.Owner)}）"
            + $"  目标剩 {defender.Defense - attackerDamage}"
            + (defenderDamage > 0 && !defender.IsHq ? $"  反击 {defenderDamage}" : ""));

        // ⚠️ `isCombatDamage: true` / `counterDamage` 以前**没有传**，两个都恒为 false。
        //    蓝图 `ExecuteAttackCard` 的两处 `ExecuteOnCardDealDamageEffects` 是
        //    si=3363 `(defender, attacker, finalDamageToDefender, True, False, False)`
        //    si=3451 `(attacker, defender, damageToAttacker,      True, True,  False)`
        //    —— 这两个 bool 就是 `OnCardDealDamage` 的 `isCombatDamage` / `CounterDamage`，
        //    也决定伤害修正链的 `fromAttack` / `isDefenderDamage`
        //    （`CalculateDamageDealt` si=473/734）。不传等于「攻击不算战斗伤害」。
        Api.DealDamage(defender, attackerDamage, attacker, isCombatDamage: true);
        if (!defender.IsHq && defender.IsAlive && defenderDamage > 0)
        {
            Api.DealDamage(attacker, defenderDamage, defender, isCombatDamage: true, counterDamage: true);
        }

        // ---- 「战斗存活」----
        //
        // 出处 `out/bp-cardfn.json` 函数 `ExecuteAttackCard`（UAssetCLI StatementIndex）：
        //   si=3130 IsUnit(defender) → si=3171 PopExecutionFlowIfNot  ; ★ 打 HQ 不进这一段
        //   si=3186 JumpIfNot(attackerDestroyed) → si=3201
        //   si=3201 ExecuteOnSurvivedCombatEvents(attacker, defender)
        //   si=3234 JumpIfNot(defenderDestroyed) → si=3249
        //   si=3249 ExecuteOnSurvivedCombatEvents(defender, attacker)
        // `JumpIfNot(xDestroyed) -> <存活那一句>`：**为假（没被摧毁）才执行** ——
        // 这点不是猜的：同一条语句上 Push/PopExecutionFlow 配的是"跳过整段"的落点
        // （si=3181 PushExecutionFlow(3234) + si=3200 PopExecutionFlow）。
        // 内核在伤害结算**之后**按 `IsAlive` 判（近似，见 CardApi.FireSurvivedCombat 的注释）。
        if (Api.IsUnit(defender) && !defender.IsHq)
        {
            if (attacker.IsAlive)
            {
                Api.FireSurvivedCombat(attacker, defender);
            }

            if (defender.IsAlive)
            {
                Api.FireSurvivedCombat(defender, attacker);
            }
        }

        FireSubAction("ZActionAttackCard", new[]
        {
            ActionValue2.Int("attackerCardID", attacker.CardId),
            ActionValue2.Int("defenderCardID", defender.CardId),
            ActionValue2.Int("damageDefender", attackerDamage),
            ActionValue2.Int("damageAttacker", defenderDamage),
            ActionValue2.Int("attackerAttackLeft", attacker.Attack),
            ActionValue2.Int("defenderDefense", defender.Defense),
        });

        // ⚠️ 「别人攻击之后」的正确程序名是 **`OnAfterOtherCardAttacks`**，
        //    不是 `OnOtherCardAttacks`（后者在全部 1636 张卡的 entrypoints 里
        //    **一个订阅者都没有**）。写错名字的后果不是报错，而是**静默不派发** ——
        //    实测 `card_unit_3_panzergrenadier`（"after you operate a German unit"）
        //    的带阵营判定那一支就挂在这个名字上，于是它只剩 `OnAfterAttack`
        //    那条**无条件** +1+1 的分支在生效，表现成"涨超"。
        Api.FireTrigger("OnAfterAttack", attacker, attacker.Owner, "OnAfterOtherCardAttacks");
        CheckDeaths();
        return true;
    }

    /// <summary>列出当前可以攻击的目标。</summary>
    public IEnumerable<CardInstance> LegalTargets(CardInstance attacker)
    {
        var enemy = attacker.Owner.Opposite();
        var enemyUnits = State.Board(enemy).Where(u => u.IsAlive).ToList();

        // 前线有敌方单位时不能越过打后方（本内核的**简化模型**，TODO 待回放确认；
        // 蓝图 `CanAttack` 里对应的规则是射程判据，见 CanReachAcrossFrontline）。
        var enemyFrontline = enemyUnits
            .Where(u => u.Location == CardLocation.BoardFrontline).ToList();

        List<CardInstance> targets = enemyFrontline.Count > 0
            ? new List<CardInstance>(enemyFrontline)
            : new List<CardInstance>(enemyUnits) { State.Hq(enemy) };

        // ---- 掩护（Guard）----
        //
        // ⚠️ 旧实现把 Guard 做成了**嘲讽**（"有 Guard 就必须先打它、HQ 直接不可选"），
        //    而蓝图 `BP_CardFunctions::UpdateGuarded`（62 条语句，`out/bp-cardfn.json`）的语义是
        //    「**邻卡有 Guard ⇒ 这张卡被掩护**」：
        // <code>
        // si=5/36/67/174   location ∈ {5,6,7} 之外直接返回（只对棋盘上的卡算）
        // si=188           FetchCardsByLocation(location) —— 遍历**同一条线**上的卡
        // si=523/634       getHasGuard(_currentCard) && !IsUnrevealedCovertCard(_currentCard)
        // si=672           JumpIfNot(那个条件) -> si=836
        // si=686/718       有 Guard 的卡：isBeingGuarded = False     ← ★ 掩护卡自己不免疫
        // si=836/847       _removeGuarded = True; GetAdjacentCards(_currentCard, true, out 邻卡)
        // si=1276/1336     邻卡 hasGuard && !IsUnrevealedCovertCard ⇒ 成立
        // si=1346/1405     _currentCard.isBeingGuarded = True        ← ★ 邻卡有 Guard ⇒ 被掩护
        // si=1517/1587     _removeGuarded && isBeingGuarded ⇒ 置回 False（清掉过期标记）
        // </code>
        //    再配合 `CanAttack` si=2627 的拒绝（上面 Attack 里的注释），三条结论：
        //      · 孤立单位**可以**被打（没有 Guard 邻卡）
        //      · 掩护卡**自己可以**被打（它的 isBeingGuarded 恒 False）
        //      · HQ 只在**被邻卡掩护**时不可打（HQ 也在 5/6 这条线上，占第 0 格）
        //    轰炸机/炮兵跳过整条判定（si=2492-2626）。旧实现这三条全反 —— 128 张天生
        //    Guard + `GiveGuard` 的子集，目标集两个方向都错。
        if (!IsBomber(attacker) && !Api.IsArtillery(attacker))
        {
            targets = targets.Where(t => !IsBeingGuarded(t)).ToList();
        }

        // ---- 烟幕（Smokescreen）：带烟幕的单位**不能被攻击** ----
        //
        // 出处 `out/bp-cardscheck.json` → `cardsCheckFunctions::CanAttack`：
        // <code>
        // si=3596  defenderCard.getHasSmokescreen(out doesIt)
        // si=3637  PopExecutionFlowIfNot(doesIt)                ; 没有烟幕 ⇒ 跳过
        // si=3647  defenderCard.IsLocation(out isIt)
        // si=3688  JumpIfNot(isIt) -> si=3782
        // si=3702/3713  canAttack=False; failReason="location_has_smokescreen"
        // si=3782/3793  canAttack=False; failReason="defender_has_smokescreen"
        // </code>
        // 卡面互证：`card_unit_85_pioneer_company` / `card_unit_coastwatchers` /
        // `card_unit_royal_ulster_rifles` 等 54 张的「Smokescreen」文本。
        targets = targets.Where(t => !t.Keywords.Contains(Keyword.Smokescreen)).ToList();

        // 末尾这条 `CanReachAcrossFrontline` 是射程判据（见 CanReachAcrossFrontline 的出处注释）。
        // ⚠️ 它现在对**所有**分支一致生效（包括原来那条 Guard 分支）——
        //    旧实现里 Guard 分支也套了它，但那是因为分支结构不同；
        //    统一过滤后语义不变：够不着的目标就是不能打（蓝图 si=90-99）。
        return targets.Where(t => CanReachAcrossFrontline(attacker, t));
    }

    /// <summary>轰炸机（`IsBomber`，`CanAttack` si=2492 用它跳过掩护判定）。</summary>
    public static bool IsBomber(CardInstance card) => card.Definition.Type == "bomber";

    /// <summary>
    /// 「这张卡被掩护了吗」—— 逐字对应 `BP_CardFunctions::UpdateGuarded`
    /// （62 条语句，出处见 <see cref="LegalTargets"/> 里的逐语句引用）。
    ///
    /// 判据：
    /// 1. 只对棋盘上的卡（location ∈ {5,6,7}）成立；
    /// 2. **自己有 Guard ⇒ 恒 false**（掩护卡自己不免疫，si=686/718）；
    /// 3. 否则 ⟺ **同一条线上 locationNumber ± 1 的邻卡有 Guard**（si=847/1276/1405）。
    ///
    /// ⚠️ 蓝图里还有一条 `!IsUnrevealedCovertCard` 的过滤（si=564/1206）——
    /// 本内核没有建模 Covert（P1），所有卡都不是"未揭示的隐蔽卡"，
    /// 所以这个条件恒真、不改变结果。**这是已知的近似，不是遗漏。**
    /// </summary>
    public bool IsBeingGuarded(CardInstance card)
    {
        if (!card.Location.IsBoard())
        {
            return false;   // si=174：location ∉ {5,6,7} 直接返回
        }

        if (card.Keywords.Contains(Keyword.Guard))
        {
            return false;   // si=686/718：掩护卡自己不被掩护
        }

        // 同一条线上的邻卡（locationNumber ± 1）—— GetAdjacentCards 的语义。
        // 只看**同阵营**的卡：掩护是给自己人挡的。
        foreach (var other in State.Cards(card.Owner, card.Location))
        {
            if (ReferenceEquals(other, card))
            {
                continue;
            }

            if (Math.Abs(other.LocationNumber - card.LocationNumber) == 1
                && other.Keywords.Contains(Keyword.Guard))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// **跨前线射程判据**：攻击者能不能够到目标。
    ///
    /// <para>
    /// 逐字来自 <c>cardsCheckFunctions::CanAttack</c>
    /// （资产 <c>klink bot/live/Library/kards/Content/Library/cardsCheckFunctions.uasset</c>）。
    /// 反编译产物：<c>out/xr-cardscheck.bpasm</c>（bpasm）/ <c>out/bp-cardscheck.json</c>（UAssetCLI）。
    /// </para>
    ///
    /// <para>蓝图语句（UAssetCLI 语句号；bpasm 行号 467-544）：</para>
    /// <code>
    /// i=90  NotEqual_ByteByte(defenderCard.location, 7)          ; 7 = Board_Frontline
    /// i=91  NotEqual_ByteByte(attackerCard.location, 7)
    /// i=92  BooleanAND(i=91, i=90)
    /// i=93  Less_IntInt(attackerCard.range, 2)
    /// i=94  BooleanAND(i=92, i=93)
    /// i=95  PopExecutionFlowIfNot(i=94)      ; 条件为假 ⇒ 跳过下面三条
    /// i=96  canAttack = false
    /// i=97  failReason = "not_enough_range"
    /// i=99  Jump 5098                        ; return
    /// </code>
    ///
    /// <para>
    /// <c>PopExecutionFlowIfNot(cond)</c> 的语义是「<c>cond</c> 为**假**时跳走、
    /// 为真时落下去执行块体」—— 这一点不是猜的：同一个函数里的
    /// <c>deployment_sickness</c> 块（i=81-89）用的是同一条指令，而已有自测
    /// <c>DeploymentSickness</c> 证明那条块的判据方向是对的
    /// （<c>enterPlayOnTurn == currentTurn &amp;&amp; !getHasBlitz()</c> ⇒ 不能攻击）。
    /// </para>
    ///
    /// <para>
    /// 所以规则是：**当攻击者和目标都不在前线时，攻击者需要 <c>range ≥ 2</c>**。
    /// 等价地 <c>range ≥ 距离</c>：支援线→前线距离 1、支援线→支援线距离 2。
    /// 任一方在前线则距离 ≤ 1，任何单位都够得着。
    /// </para>
    ///
    /// <para>
    /// 「只有炮兵/战斗机/轰炸机能跨前线」是这条射程规则的**数据后果**，
    /// 不是另有一条按类型的判据 —— <c>CanAttack</c> 里**没有** <c>IsFighter</c>，
    /// <c>IsArtillery</c>/<c>IsBomber</c> 只出现在 <c>isBeingGuarded</c> 那条分支上
    /// （i=100-104），与射程无关。卡池实测（<c>klink bot/docs/cards.live.json</c>）：
    /// infantry 567 张 range=1（1 张 range=2）、tank 154 张 range=1、
    /// artillery 60 张 range=2、fighter 144 张 range=2、bomber 97 张 range=2（1 张 range=4）。
    /// </para>
    /// </summary>
    public static bool CanReachAcrossFrontline(CardInstance attacker, CardInstance defender)
    {
        // 任一方在前线 ⇒ 距离 ≤ 1 ⇒ 任何射程都够得着（蓝图里就是「条件为假」）
        if (attacker.Location == CardLocation.BoardFrontline
            || defender.Location == CardLocation.BoardFrontline)
        {
            return true;
        }

        // 双方都不在前线（支援线 ↔ 支援线 / HQ）⇒ 要跨过前线，需要 range ≥ 2
        return attacker.Definition.Range >= 2;
    }

    // ==================== 伤害与死亡 ====================

    /// <summary>造成伤害（由 <see cref="CardApi"/> 统一入口以保证事件顺序）。</summary>
    internal int ApplyDamage(CardInstance target, int amount, CardInstance? source,
                             bool ignoreHeavyArmor = false)
    {
        if (amount <= 0 || !target.IsAlive)
        {
            return 0;
        }

        if (target.Keywords.Contains(Keyword.Immune))
        {
            return 0;
        }

        // ---- 重甲减伤（P1，2026-09-30）----
        //
        // 出处 `out/bp-cardfn.json` → `CalculateDamageDealt`（伤害结算的唯一出口）：
        // <code>
        // si=2936  _damageRecieverCard.getTotalHeavyArmor(out totalHeavyArmor)
        // si=2977  SelectInt(0, totalHeavyArmor, ignoreHeavyArmor)
        // si=3028  _damageRecieverCard.GetPassiveDefenseBuff(out amount)
        // si=3082  SelectInt(GetPassiveDefenseBuff_amount, 0, applyBeforeAttackBuffs)
        // si=3133  Add_IntInt(上面两个 SelectInt)
        // si=3179  Subtract_IntInt(_dealerCalculatedDamage, 上面那个和)
        // si=3225  Max(.., 0)
        // </code>
        // `SelectInt(A, B, pick)` 的语义是 `pick ? A : B`（`KismetVm.EvalMath` 的
        // `case "SelectInt"`），所以
        //     damage = Max(damage − (ignoreHeavyArmor ? 0 : 重甲) − (applyBeforeAttackBuffs ? 被动防buff : 0), 0)
        //
        // ⚠️ 只落地**重甲**那一半。`GetPassiveDefenseBuff` 是另一条机制
        //    （被动防御 buff），内核里没有对应实现，不拿近似值顶替。
        //
        // ⚠️ `ignoreHeavyArmor` 在蓝图里是 `CalculateDamageDealt` 的入参；
        //    内核没有 `CalculateDamageDealt` 这一层，所以做成 `ApplyDamage` 的可选入参。
        //    目前**没有任何调用点传 true** —— 因为「谁能无视重甲」这条还没有证据，
        //    先只做「默认减伤」这一半（52 张卡的减伤从完全无效变成生效）。
        int reduction = ignoreHeavyArmor ? 0 : target.HeavyArmor;
        if (reduction > 0)
        {
            amount = Math.Max(amount - reduction, 0);
            if (amount == 0)
            {
                return 0;
            }
        }

        target.Defense -= amount;
        if (target.IsHq)
        {
            Say($"HQ {target.Owner.ToWire()} 受到 {amount} 伤害，剩余 {target.Defense}");
            if (target.Defense <= 0)
            {
                State.Finish(target.Owner.Opposite(), "Victory_DestroyHQ");
            }
        }

        return amount;
    }

    /// <summary>直接对 HQ 造成伤害（疲劳等无来源伤害）。</summary>
    public void DamageHq(Side side, int amount) => ApplyDamage(State.Hq(side), amount, null);

    /// <summary>清理防御 ≤ 0 的单位。</summary>
    public void CheckDeaths()
    {
        if (State.IsFinished)
        {
            return;
        }

        // 热路径：用无序枚举收集再销毁，避免每次 AllCards 的排序 + 分配
        List<CardInstance>? dying = null;
        foreach (var card in State.CardsUnordered())
        {
            // ⚠️ 必须限定在**场上**（IsBoard），这是 2026-09-26 对拍抓出来的一个严重 bug：
            //    指令牌（Type=order）的 defense 天然是 0，而这里原来遍历的是**所有卡**，
            //    于是**第一次** CheckDeaths（= 本局第一张牌被打出时）就把双方牌库 + 手牌里
            //    全部指令牌一次性"消灭"进弃牌堆。
            //    实测 match40 act=4：牌库 68→27、弃牌 0→50，其中 49 张正好是该局
            //    defense<=0 的指令牌总数，第 50 张是刚打出的那张。
            //    "阵亡"只适用于在场单位 —— 手牌和牌库里的牌不会因为防御力是 0 而死。
            if (!card.IsHq && card.IsAlive && card.Location.IsBoard() && card.Defense <= 0)
            {
                (dying ??= new List<CardInstance>()).Add(card);
            }
        }

        if (dying is null)
        {
            return;
        }

        foreach (var card in dying)
        {
            Destroy(card);
        }
    }

    public void Destroy(CardInstance card, CardInstance? destroyer = null)
    {
        if (!card.IsAlive)
        {
            return;
        }

        Say($"{card} 被摧毁");

        // ---- 「即将被摧毁」----
        //
        // 出处 `out/bp-cardfn.json` 函数 `ExecuteOnBeforeOtherCardDestroyed`
        // （`cardDestroyedID, AttackerID, TriggerNotDestroyed, DestroyedInCombat`，30 条语句）：
        // <code>
        // si=140  JumpIfNot(_cardDestroyed.isSuppressed) -> si=520   ; ★ 被压制 ⇒ 两个名字都不发
        // si=176  FetchAllCardsWithEventTrigger(15)                  ; 15 = 这一族
        // si=445      EqualEqual_IntInt(cardDestroyedID, item.cardID)
        // si=505      JumpIfNot(...) -> si=653
        // si=520          _cardDestroyed.OnBeforeDestroyed(_attacker, TriggerNotDestroyed)    ← 自己
        // si=653      item.OnBeforeOtherCardDestroyed(_cardDestroyed, _attacker,
        //                                            TriggerNotDestroyed, DestroyedInCombat) ← 别人
        // </code>
        // ⇒ 一次 FireTrigger 同时覆盖"自己那一路"和"别人那一路"；
        //   被压制的卡**两个都不发**（旧实现无条件发 `OnBeforeDestroyed`）。
        // `DestroyedInCombat` 这个入参内核没有建模，恒传 false（近似，不猜）。
        if (!card.Keywords.Contains(Keyword.Suppressed))
        {
            Api.FireTrigger("OnBeforeDestroyed", card, card.Owner, "OnBeforeOtherCardDestroyed",
                eventArgs: new object?[] { card, destroyer, false, false },
                eventSubject: card,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardDestroyed"] = card,
                    ["attacker"] = destroyer,
                    ["TriggerNotDestroyed"] = false,
                    ["DestroyedInCombat"] = false,
                });
        }

        FireSubAction("ZActionDestroyUnit", new[]
        {
            ActionValue2.Int("destroyerCardID", destroyer?.CardId ?? 0),
            ActionValue2.Int("destroyedLocation", (int)card.Location),
        });

        // 离场触发点在**移动之前**发：光环（OnLeaveBoardOrOwner 族）要在这里把
        // 自己挂上去的 buff 撤掉，而效果里判的是「卡还在不在场」，
        // 先 Move 再发的话判据会变成 false、还原逻辑整段跳过。
        if (card.Location.IsBoard() && !card.IsHq)
        {
            FireLeaveTrigger(card, CardLocation.Discard);
        }

        State.Move(card, CardLocation.Discard);
        Api.FireTrigger("OnDestroyed", card, card.Owner, "OnOtherCardDestroyed");

        // ---- 「一个摧毁效果触发了」（事件 24）----
        //
        // 出处 `out/bp-cardfn.json` 的 `TriggerDestruction`：
        // <code>
        // si=905   BooleanAND(Not(CustomName1HasAttribute("StopDestructionEffect")), card.hasDestruction)
        // si=965   PopExecutionFlowIfNot(...)                      ; 假 ⇒ 整块跳过
        // si=1237  localCardUsedForTriggeringDestructionEffect.OnDestroyed(NoObject{}, true)
        // si=1286  ExecuteOnDestructionEffectTriggered(..., out TriggerMultiple)
        // si=1346  if (TriggerMultiple &gt; 0) { loop 1..TriggerMultiple: si=1515 再整轮派发 }
        // </code>
        //
        // ⚠️ **这里只加事件 24，没有给上面那句 `OnDestroyed` 加 `hasDestruction` 门** ——
        //    是查过之后有意不加的，不是漏了：
        //    IR 里订阅 `OnDestroyed` 的有 79 张卡，其中 **7 张没有 `hasDestruction`**。
        //    逐张看过它们的函数体，**不是空壳**：
        //      · `card_unit_daimler_mk_ii_cam1` / `card_unit_kumamoto_regiment_cam1`
        //        —— `HasCampaignUpgrade(6)` 门后面才是战役升级效果
        //      · `card_unit_14_panzergrenadier` —— `IsVeteran` / `RemovePinnedOverride` 收尾
        //      · `card_unit_superman` —— `CustomName1Remove("StopDestructionEffect")` 收尾
        //      · 另 3 张（`641st_rifles` / `dornier_do_17` / `t_28_pincer`）确实是空壳
        //    也就是说「`OnDestroyed` ⟺ hasDestruction」这条不成立：这些卡的 `OnDestroyed`
        //    是**另一条路径**进来的。拿 `hasDestruction` 去卡它们会静默砍掉 4 张卡的真实逻辑，
        //    所以宁可保持原样、把不确定性写在这里。
        //    事件 24 那一侧没有这个矛盾：4 张订阅者的卡面文本**全部**写着
        //    "when a Destruction effect triggers"，门取 `hasDestruction ‖ HasCustomAbility`。
        if (Api.ShouldTriggerDestructionEffect(card))
        {
            int extra = Api.FireDestructionEffectTriggered(card, destroyer);
            for (int i = 0; i < extra; i++)
            {
                Api.FireDestructionEffectTriggered(card, destroyer);
            }
        }
    }

    // ==================== 动作记录 ====================

    internal void RecordAction(string actionType, Side player, Dictionary<string, object?> data, IReadOnlyList<SubAction>? subs = null)
    {
        _actionId++;
        State.ActionLog.Add(new GameAction(
            _actionId, actionType, player, State.Turn,
            subs ?? Array.Empty<SubAction>(),
            data));
    }

    /// <summary>产生并记录一个子动作（对应游戏里的 CreateAction_AddSubAction / AddSubAction*）。</summary>
    internal void FireSubAction(string name, IEnumerable<ActionValue2> values)
        => State.ActionLog.Add(new GameAction(
            ++_actionId, "SubAction", State.ActiveSide, State.Turn,
            new[] { new SubAction(name, values.ToList()) },
            new Dictionary<string, object?>()));

    /// <summary>该动作名是否是「效果」而不是纯查询 —— 用于统计未实现的效果调用。</summary>
    public IReadOnlyDictionary<string, int> UnimplementedCalls => State.UnimplementedCalls;
}

