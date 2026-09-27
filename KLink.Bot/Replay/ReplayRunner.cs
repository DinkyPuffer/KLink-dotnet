using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Replay;

/// <summary>
/// 真实对局重放器 —— 内核验证的主入口。
///
/// 做的事：用开局快照还原卡池，按真实动作流驱动内核，**每一步都比对
/// <c>action_data["84"]</c>（**对手**的 HQ 防御力，动作结算前的采样）**。
///
/// 为什么这个比对有意义：
/// - `84` 已被 21 回合 / 94 条动作的回放确认是「行动方对手的 HQ 当前防御力」，
///   而且整条轨迹与终局的 `Victory_DestroyHQ` 完全闭合
/// - HQ 防御只在「受到伤害」时变化，所以它对**攻击结算、伤害数值、摧毁判定**
///   高度敏感 —— 对不上就说明这些地方理解有误
/// - 它是动作流里唯一携带的状态量，不需要额外的状态导出
///
/// 首次对不上的位置就是最有价值的线索：那里就是规则理解第一个出错的地方。
///
/// ⚠️ **已知保真度缺口**（会污染结论，必须一起看）：
/// 1. 起始手牌不是快照给的那个。实测快照判为「牌库」的牌会被客户端直接打出，
///    原因是 fyserver 自己洗牌发牌，而真实客户端并不按它发。
///    → 本器用「按需注入手牌」对冲（记 `InjectedToHand`），
///    只保证**棋盘与 HQ 的演化**可比，不保证手牌经济完全一致。
/// 2. 动作流里有**重复记录**（同一 cardID 连续被 `PC` 4 次），按重复丢弃。
/// 3. 对局中生成的卡（如 cardID 11001）不在快照里，用动作自带的卡组码补建。
/// </summary>
public sealed class ReplayRunner
{
    private readonly CardDatabase _db;

    public ReplayRunner(CardDatabase db) => _db = db;

    /// <summary>
    /// 可选的**开局分区覆盖**：cardID → 该卡开局在哪个位置。
    ///
    /// 不设时所有非棋盘卡一律进牌库，打出时再按需注入手牌 —— 这样牌库/手牌的分界
    /// 是假的，任何「区域」对拍都会被这条人为缺口污染（实测区域准确率只有 43%，
    /// 而真实开局是 4/5 张手牌 + 35/34 张牌库）。用真实快照的开局分区做种子之后，
    /// 区域这条指标衡量的才是**动力学**，而不是种子。
    /// </summary>
    public IReadOnlyDictionary<int, CardLocation>? InitialLocations { get; set; }

    /// <summary>诊断开关：打开后把每个失败动作的完整堆栈打到 stderr。</summary>
    public static bool TraceExceptions { get; set; }

    /// <summary>单个动作的重放结果。</summary>
    public sealed record StepResult(
        int ActionId,
        int Turn,
        string ActionType,
        string PlayerSide,
        bool Applied,
        string? Failure,
        int ExpectedHq,
        int ActualHq,
        bool HqMatches,
        int InjectedToHand,
        bool Duplicate);

    public sealed class Report
    {
        public required int MatchId { get; init; }
        public required int Turns { get; init; }
        public required string WinnerSide { get; init; }
        public required IReadOnlyList<StepResult> Steps { get; init; }
        public required int TotalActions { get; init; }

        /// <summary>快照 cardID → 卡名 与动作自带卡组码不一致的次数（应为 0）。</summary>
        public required int IdentityConflicts { get; init; }

        /// <summary>卡组码解析不出来的次数。</summary>
        public required int UnknownCodes { get; init; }

        /// <summary>推出来的「对手 HQ」字段下标。</summary>
        public required string? HqKey { get; init; }

        /// <summary>回放里 XActionCheat 的条数（&gt;0 说明是开作弊打的测试局）。</summary>
        public required int CheatActions { get; init; }

        public int AppliedCount => Steps.Count(s => s.Applied);
        public int NotUnderstood => Steps.Count(s => !s.Applied && !s.Duplicate);
        public int Duplicates => Steps.Count(s => s.Duplicate);
        public int Injected => Steps.Sum(s => s.InjectedToHand);
        public int HqChecked => Steps.Count(s => s.ExpectedHq > 0);
        public int HqMatched => Steps.Count(s => s.ExpectedHq > 0 && s.HqMatches);

        /// <summary>第一次 HQ 对不上的位置 —— 最有价值的线索。</summary>
        public StepResult? FirstMismatch
            => Steps.FirstOrDefault(s => s.ExpectedHq > 0 && !s.HqMatches);

        public IEnumerable<IGrouping<string, StepResult>> NotUnderstoodByType
            => Steps.Where(s => !s.Applied && !s.Duplicate).GroupBy(s => s.ActionType);

        public int LastCheckedTurn => Steps.Where(s => s.ExpectedHq > 0).Select(s => s.Turn).DefaultIfEmpty(0).Max();

        /// <summary>重放过程中撞到的未实现原语调用（次数从多到少）。</summary>
        public required IReadOnlyList<KeyValuePair<string, int>> Unimplemented { get; init; }

        /// <summary>HQ 轨迹一直对到第几回合 —— 内核可信度的下界。</summary>
        public int HqCleanTurns
        {
            get
            {
                var first = FirstMismatch;
                return first is null ? LastCheckedTurn : first.Turn - 1;
            }
        }
    }

    /// <summary>把一个动作映射成内核操作所需的参数（用于诊断）。</summary>
    private static string Describe(WireAction a) => a.ActionType switch
    {
        "PC" => $"打出 cardID={a.CardId} 槽位={a.SecondId} 目标={a.TargetId}",
        "AC" => $"cardID={a.CardId} 攻击 cardID={a.SecondId}",
        "ML" => $"移动 cardID={a.CardId} 到槽位={a.SecondId}",
        "CS" => $"选牌答复：cardID={a.CardId} 候选下标={a.SecondId} " +
                $"选中码={a.Get(WireAction.KeyIndex.CodeSlotA)}",
        "XActionStartOfTurn" => "回合开始",
        "XActionEndOfTurn" => "回合结束",
        _ => a.ActionType,
    };

    public Report Run(ReplayData replay, bool verbose)
        => Run(replay, verbose, null);

    /// <param name="onStepped">
    /// 每个动作结算**之后**回调一次 <c>(动作, 当前状态)</c>。
    /// 这是给「和真实快照逐字段对拍」用的钩子 —— 对拍工具不需要重写一遍驱动逻辑，
    /// 也就不会漏掉这里已经踩过的坑（手牌注入、重复动作、XActionStartOfTurn 只算一次）。
    /// </param>
    public Report Run(ReplayData replay, bool verbose, Action<WireAction, GameState>? onStepped)
    {
        // ---- 1) 卡池：快照里每个 cardID 都是唯一的，直接全建成「牌库」，
        //         真正需要时再按需注入手牌（见类注释的保真度缺口 1） ----
        var engine = new MatchEngine(_db, Array.Empty<string>(), Array.Empty<string>(),
                                     seed: (ulong)replay.MatchId);
        var state = engine.State;

        int known = 0, skipped = 0;
        foreach (var c in replay.Cards)
        {
            if (c.Location.IsBoard())
            {
                continue;   // HQ 单独处理
            }

            if (_db.Find(c.Name) is null)
            {
                skipped++;
                continue;
            }

            // ⚠️ 必须用**分侧的**牌库枚举（DeckLeft/DeckRight）：`CardLocation.Deck`
            //    是另一个枚举值，`State.Deck(side)` 查的是 DeckOf()，用错的话牌库会被
            //    当成空的 —— 表现为每回合凭空吃疲劳伤害（HQ 20→19→17→14→10→5）。
            state.CreateWithId(c.Name, c.Owner, c.CardId,
                InitialLocations?.GetValueOrDefault(c.CardId) ?? c.Owner.DeckOf(), 0, c.IsGold);
            known++;
        }

        // HQ 卡：快照里的 board_hqleft / board_hqright
        foreach (var c in replay.Cards.Where(c => c.Location.IsBoard()))
        {
            state.CreateWithId(c.Name, c.Owner, c.CardId, c.Location, c.LocationNumber, c.IsGold);
        }

        // HQ 初始防御 20（已由实测确认）
        foreach (var side in new[] { Side.Left, Side.Right })
        {
            if (state.Cards(side).Any(c => c.IsHq))
            {
                state.SetHqDefense(side, MatchEngine.InitialHqDefense);
            }
        }

        // ---- 2) 逐步重放 ----
        var steps = new List<StepResult>();
        int identityConflicts = 0, unknownCodes = 0;
        bool turnStarted = false;
        var seenPlayedIds = new HashSet<int>();
        string? hqKey = replay.InferHqKey();

        // 回放里可能带 XActionCheat（SetKredits / SpawnCard）—— 那几局是开作弊打的测试局，
        // kredit 对不上是预期的，不能让它们污染「规则错误」的结论。
        int cheats = replay.Actions.Count(a => a.ActionType == "XActionCheat");

        // ---- CS 答复队列：「从候选里挑一张」的玩家选择 ----
        //
        // `CS` = `XActionCardToDrawSelected`（依据见 WireAction.CompactToFull 的注释），
        // 是 `selectCardToDraw` 里那条 `NotifySelectCardToDrawPending` 分支的**答复**：
        //   `0` = 挑牌的那张卡的 cardID，`1` = 候选下标（0..2），`2` = 选中卡的卡组码。
        //
        // ⚠️ 时序：`CS` 排在触发它的 `PC` **之后**（真实对局里客户端要等玩家点完），
        //    而内核是在 `PC` 的效果里**同步**就把 `selectCardToDraw` 跑完了。
        //    所以这里**先整体扫一遍**，把答复按 cardID 排成队列预挂到引擎上，
        //    等效果调 `selectCardToDraw` 时按顺序取用。
        //    同一张卡连续答复多次是实测存在的（206428 的 #57/#58、#61/#62
        //    都是同一张卡连选两次），所以必须是队列、不能只存一个值。
        var csQueue = new Dictionary<int, Queue<(int Index, string? Code)>>();
        foreach (var act in replay.Actions)
        {
            if (act.ActionType != "CS")
            {
                continue;
            }

            if (!csQueue.TryGetValue(act.CardId, out var q))
            {
                csQueue[act.CardId] = q = new Queue<(int, string?)>();
            }

            q.Enqueue((act.SecondId, act.Get(WireAction.KeyIndex.CodeSlotA)));
        }

        // 每张卡"已被效果消费掉几条答复"的计数，供下面 `case "CS"` 判断是否需要补做。
        var csConsumed = new Dictionary<int, int>();

        // 每张卡"被效果取走、但解不出选中卡"的计数 —— 这些答复**没有落实**，
        // 必须在 `case "CS"` 里如实记成未应用，不能因为"效果跑过了"就算成功。
        var csUnresolved = new Dictionary<int, int>();

        engine.PickCardToDraw = (selecting, fromTopOfDeck, _) =>
        {
            if (selecting is null
                || !csQueue.TryGetValue(selecting.CardId, out var q)
                || q.Count == 0)
            {
                return null;   // 没有答复 → 由 CardApi 走兜底（自对弈才用得上）
            }

            var (index, code) = q.Dequeue();
            csConsumed[selecting.CardId] = csConsumed.GetValueOrDefault(selecting.CardId) + 1;

            string? name = code is not null && _db.DeckCodeIds.TryGetValue(code, out var nm) ? nm : null;
            if (name is null)
            {
                csUnresolved[selecting.CardId] = csUnresolved.GetValueOrDefault(selecting.CardId) + 1;
                if (verbose)
                {
                    Console.WriteLine($"        CS 答复解不出卡名：cardID={selecting.CardId} 下标={index} 码={code}");
                }

                return null;
            }

            // ⚠️ 两条路的"选中物"不是同一种东西（见 MatchEngine.PickCardToDraw 的注释）：
            //   · 牌库/占卜族：候选是**牌库里的实例**，要在牌库里找到它。
            //   · Develop 族：候选是 `GetAllActiveStaticCards()` 过滤出的**卡池模板**，
            //     牌库里根本没有这张牌 —— 内核按**卡名**新生成一张
            //     （`CardApi.DevelopChosenCard`，出处见那边的注释）。
            //     这里只需要回一个"带正确卡名的模板实例"，`CardId` 不会被用到。
            if (fromTopOfDeck)
            {
                var deckCard = CardFromDeckCode(selecting, code, state);
                if (deckCard is null)
                {
                    csUnresolved[selecting.CardId] = csUnresolved.GetValueOrDefault(selecting.CardId) + 1;
                    if (verbose)
                    {
                        Console.WriteLine($"        CS 答复取不到牌库实例：cardID={selecting.CardId} " +
                                          $"下标={index} 码={code}");
                    }
                }

                return deckCard;
            }

            var def = _db.Find(name);
            if (def is null)
            {
                csUnresolved[selecting.CardId] = csUnresolved.GetValueOrDefault(selecting.CardId) + 1;
                if (verbose)
                {
                    Console.WriteLine($"        CS 答复的卡名不在卡库里：cardID={selecting.CardId} " +
                                      $"码={code} 名={name}");
                }

                return null;
            }

            return KLink.Bot.Effects.CardApi.TemplateInstance(def, selecting.Owner);
        };

        foreach (var a in replay.Actions)
        {
            int turn = a.TurnNumber;
            Side side = replay.SideOf(a.PlayerId);
            string sideStr = side.ToWire() is { Length: > 0 } s ? s : "?";

            // 动作结算**前**采样对手 HQ
            Side foe = side == Side.Left ? Side.Right : side == Side.Right ? Side.Left : Side.NotAvailable;
            int expected = a.OpponentHqOf(hqKey);
            int actual = foe != Side.NotAvailable && state.Cards(foe).Any(c => c.IsHq)
                ? state.HqDefense(foe)
                : -1;

            if (side == Side.NotAvailable)
            {
                // 服务端合成的动作（ActionEndMatch 的 player_id 是 0）
                steps.Add(new StepResult(a.ActionId, turn, a.ActionType, sideStr,
                    false, "无法确定行动方", -1, -1, true, 0, false));
                // 状态没变，但动作序号推进了 —— 对拍方需要这个序号才知道该比对了
                onStepped?.Invoke(a, state);
                continue;
            }

            bool applied = false;
            bool duplicate = false;
            int injected = 0;
            string? failure = null;

            try
            {
                // 卡牌身份自检：动作自带的卡组码 vs 快照卡名
                if (!CheckIdentity(a, replay, ref identityConflicts, ref unknownCodes, out string? idNote))
                {
                    failure = idNote;
                }
                else
                {
                    switch (a.ActionType)
                    {
                        case "XActionStartOfTurn":
                            // ⚠️ `EndTurn` 内部已经 `Turn++` 并 `StartTurn(对手)`，
                            //    所以动作流里的 `XActionStartOfTurn` 只有**第一条**是
                            //    「真正要开回合」，其余都是流里的标记，重复调用会把
                            //    kredit 上限和抽牌都翻倍（实测右方 T8 上限变成 8 而不是 4）。
                            if (!turnStarted)
                            {
                                // 不传 `draw`：本局的第一个 `StartTurn` 恰好是全局回合 1，
                                // `StartTurn` 会按 `BP_Logic.CanSideDrawCards` 的规则
                                // 自动跳过摸牌（先手第 1 回合不摸）。
                                //
                                // 这和本器自己的口径也一致：`InitialLocations` 是拿
                                // **开局快照**（act=1，先手 StartOfTurn 已结算之后的状态）
                                // 当种子的，再摸一张等于把同一个回合开始算两遍 ——
                                // 实测症状：act=1 真实手牌 9 张、内核 10 张（差正好 1）。
                                engine.StartTurn(side);
                                turnStarted = true;
                                applied = true;
                            }
                            else if (state.ActiveSide != side)
                            {
                                failure = $"回合归属不符：流里是 {sideStr}，内核当前行动方是 {state.ActiveSide.ToWire()}";
                            }
                            else
                            {
                                applied = true;   // 标记动作，无需再做
                            }

                            break;

                        case "XActionEndOfTurn":
                            engine.EndTurn(side);
                            applied = true;
                            break;

                        case "PC":
                        {
                            var card = ResolveCard(a, replay, side, foe, state);
                            if (card is null)
                            {
                                failure = $"找不到/建不出 cardID={a.CardId}（码 {a.Get("4")}）";
                                break;
                            }

                            if (card.Location.IsBoard() || seenPlayedIds.Contains(card.CardId))
                            {
                                duplicate = true;
                                failure = "重复记录（该 cardID 已在场/已打出过）";
                                break;
                            }

                            if (card.Location != side.HandOf())
                            {
                                state.Move(card, side.HandOf());
                                injected++;
                            }

                            var target = a.TargetId > 0 ? state.ById(a.TargetId) : null;
                            if (!engine.CanPlay(card, out string why))
                            {
                                failure = $"打不出：{why}（kredits={state.Kredits(side)}，费用={card.KreditCost}）";
                                break;
                            }

                            // 三选一（Choose One）的分支就藏在 `PC` 的 `3` 号槽里 ——
                            // 对应 `ZActionPlayCardFromHand` 参数表的 `Int:chooseOneIndex`。
                            // 判据：6 局 295 条 PC 里 `3` 只有 1 条非 0（634651 #122 的
                            // `card_event_planned_attack`，卡面「Choose One - … OR …」、
                            // 蓝图走 `WhichChooseOne` 的 0/1 分支）。
                            // 不写进去的话 `WhichChooseOne` 只能返回默认 0，
                            // 选 1 分支的那次出牌会走错分支。
                            card.ChooseOne = WireAction.ParseInt(a.Get(WireAction.KeyIndex.ChooseOneIndex));

                            engine.PlayCard(card, target);
                            seenPlayedIds.Add(card.CardId);
                            applied = true;
                            break;
                        }

                        case "ML":
                        {
                            var card = ResolveCard(a, replay, side, foe, state);
                            if (card is null)
                            {
                                failure = $"找不到 cardID={a.CardId}";
                                break;
                            }

                            applied = engine.MoveUnit(card, a.SecondId);
                            if (!applied)
                            {
                                failure = $"移动被拒（当前 {card.Location}）";
                            }

                            break;
                        }

                        case "AC":
                        {
                            var attacker = ResolveCard(a, replay, side, foe, state);
                            var defender = ResolveTarget(a, replay, side, foe, state);
                            if (attacker is null || defender is null)
                            {
                                failure = $"找不到攻击者({a.CardId})或防御者({a.SecondId})";
                                break;
                            }

                            applied = engine.Attack(attacker, defender);
                            if (!applied)
                            {
                                failure = "攻击被拒";
                            }

                            break;
                        }

                        // `CS` = XActionCardToDrawSelected：「从候选里挑一张」的答复。
                        //
                        // 正常路径上，答复已经在 PC 的效果结算时被 `selectCardToDraw`
                        // 通过 `engine.PickCardToDraw` 取走并落实了，这里只需记成"已应用"。
                        // 但如果那张卡的效果**没**走到 `selectCardToDraw`
                        // （PC 被判失败、或该卡的效果脚本缺失），队列里那条答复就没人取 ——
                        // 那种情况在这里补做一次抽取，免得白丢一条真实动作。
                        case "CS":
                        {
                            var selecting = state.ById(a.CardId);
                            if (selecting is null)
                            {
                                failure = $"找不到挑牌的卡 cardID={a.CardId}";
                                break;
                            }

                            // 效果已经把答复取走了、但选中卡解不出来 —— 效果**没落实**，
                            // 如实记未应用（原因见 CardFromDeckCode 的注释）。
                            if (csUnresolved.GetValueOrDefault(a.CardId) > 0)
                            {
                                csUnresolved[a.CardId]--;
                                failure = UnresolvedCsReason(a);
                                break;
                            }

                            if (csConsumed.GetValueOrDefault(a.CardId) > 0)
                            {
                                csConsumed[a.CardId]--;   // 已被效果消费并落实
                                applied = true;
                                break;
                            }

                            // 没人取的答复：把它从队列里摘掉（否则会被同一张卡的下一次
                            // `selectCardToDraw` 误用），然后在这里自己落实。
                            if (csQueue.TryGetValue(a.CardId, out var q) && q.Count > 0)
                            {
                                q.Dequeue();
                            }

                            string? code = a.Get(WireAction.KeyIndex.CodeSlotA);
                            string? chosenName = code is not null
                                                 && _db.DeckCodeIds.TryGetValue(code, out var nm)
                                ? nm : null;
                            if (chosenName is null)
                            {
                                failure = UnresolvedCsReason(a);
                                break;
                            }

                            // 走哪一族看**这张卡自己有没有 GetChooseSpawnCards**
                            // （有 = Develop 族，候选是卡池模板；没有 = 牌库/占卜族）。
                            bool developFamily = Effects.Blueprint.KismetLibrary.Default?
                                .FindLocalProgram(selecting.Definition.Name, "GetChooseSpawnCards") is not null;

                            if (developFamily)
                            {
                                if (engine.Api.DevelopChosenCard(selecting, chosenName) is null)
                                {
                                    failure = UnresolvedCsReason(a);
                                    break;
                                }

                                applied = true;
                                break;
                            }

                            var picked = CardFromDeckCode(selecting, code, state);
                            if (picked is null)
                            {
                                failure = UnresolvedCsReason(a);
                                break;
                            }

                            engine.Api.DrawChosenCardToHand(selecting, picked);
                            applied = true;
                            break;
                        }

                        case "ActionEndMatch":
                            applied = true;   // 不影响棋盘
                            break;

                        // 客户端调试作弊（实测两条）：
                        //   {"0":"SetKredits","1":"right","2":"12"}
                        //   {"0":"SpawnCard","1":"right","2":"dy","3":"Hand_Right","4":"0"}
                        // 回放要保真就必须照做，否则 165924 那局连「打不出的 5 费牌」都对不上。
                        case "XActionCheat":
                            applied = ApplyCheat(a, side, state, ref failure);
                            break;

                        default:
                            failure = $"未处理的动作类型 {a.ActionType}";
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                if (TraceExceptions)
                {
                    Console.Error.WriteLine($"--- action {a.ActionId} {a.ActionType} T{turn} ---");
                    Console.Error.WriteLine(ex);
                }
            }

            bool hqMatches = expected <= 0 || actual < 0 || expected == actual;

            steps.Add(new StepResult(a.ActionId, turn, a.ActionType, sideStr,
                applied, failure, expected, actual, hqMatches, injected, duplicate));

            onStepped?.Invoke(a, state);

            if (verbose && (!applied && !duplicate || !hqMatches))
            {
                Console.WriteLine($"  [{a.ActionId,3}] T{turn,-3} {sideStr,-6} {a.ActionType,-20} " +
                                  $"{(applied ? "" : "未应用: " + failure)}" +
                                  $"{(hqMatches ? "" : $"  ⚠ 对手HQ 期望 {expected} 实际 {actual}")}");
                Console.WriteLine($"        {Describe(a)}");

                if (!hqMatches)
                {
                    Console.WriteLine($"        状态: 左HQ={Hq(state, Side.Left)} 右HQ={Hq(state, Side.Right)} " +
                                      $"左k={state.Kredits(Side.Left)}/{state.MaxKredits(Side.Left)} " +
                                      $"右k={state.Kredits(Side.Right)}/{state.MaxKredits(Side.Right)}");
                    Console.WriteLine($"        左场: {Board(state, Side.Left)}");
                    Console.WriteLine($"        右场: {Board(state, Side.Right)}");
                    foreach (var act in state.ActionLog.TakeLast(10))
                    {
                        Console.WriteLine($"        · [{act.ActionId}] {act.ActionType} " +
                                          $"{string.Join(" ", act.ActionData.Select(kv => $"{kv.Key}={kv.Value}"))}");

                        foreach (var sub in act.SubActions)
                        {
                            Console.WriteLine($"            └ {sub.Name} " +
                                              $"{string.Join(" ", sub.Values.Select(v => $"{v.Name}={v.Text}{v.Value}"))}");
                        }
                    }
                }
            }
        }

        if (verbose)
        {
            Console.WriteLine($"\n  卡池 {known} 张（跳过 {skipped} 张不在卡库）");
            Console.WriteLine($"  身份自检：冲突 {identityConflicts}，未知卡组码 {unknownCodes}");
        }

        var unimplemented = state.UnimplementedCalls
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        return new Report
        {
            MatchId = replay.MatchId,
            Turns = replay.Turns,
            WinnerSide = replay.WinnerSide,
            Steps = steps,
            TotalActions = replay.Actions.Count,
            IdentityConflicts = identityConflicts,
            UnknownCodes = unknownCodes,
            HqKey = hqKey,
            CheatActions = cheats,
            Unimplemented = unimplemented,
        };
    }

    private static int Hq(GameState s, Side side)
        => s.Cards(side).Any(c => c.IsHq) ? s.HqDefense(side) : -1;

    /// <summary>
    /// 执行客户端的调试作弊动作（<c>XActionCheat</c>）。
    ///
    /// 键位：<c>0</c>=命令名，<c>1</c>=目标阵营，<c>2</c>=参数，<c>3</c>=目标区域。
    /// 目前实测到 <c>SetKredits</c> 与 <c>SpawnCard</c> 两条。
    /// </summary>
    private bool ApplyCheat(WireAction a, Side fallbackSide, GameState state, ref string? failure)
    {
        string op = a.Get("0") ?? "";
        string? sideRaw = a.Get("1");
        Side side = sideRaw switch
        {
            "left" => Side.Left,
            "right" => Side.Right,
            _ => fallbackSide,
        };

        switch (op)
        {
            case "SetKredits":
                if (!int.TryParse(a.Get("2"), out int amount))
                {
                    failure = $"SetKredits 参数不是整数：{a.Get("2")}";
                    return false;
                }

                state.SetKredits(side, amount);
                state.SetMaxKredits(side, Math.Max(state.MaxKredits(side), amount));
                return true;

            case "SpawnCard":
            {
                string code = a.Get("2") ?? "";
                if (!_db.DeckCodeIds.TryGetValue(code, out string? name))
                {
                    failure = $"SpawnCard 卡组码 {code} 不在 deckCodeIDsTable2 里";
                    return false;
                }

                // 区域名（实测 "Hand_Right"）→ 分侧枚举
                string loc = a.Get("3") ?? "";
                CardLocation target = loc switch
                {
                    "Hand_Left" => CardLocation.HandLeft,
                    "Hand_Right" => CardLocation.HandRight,
                    "Deck_Left" => CardLocation.DeckLeft,
                    "Deck_Right" => CardLocation.DeckRight,
                    "Board_Frontline" => CardLocation.BoardFrontline,
                    _ => side.HandOf(),
                };

                int cardId = int.TryParse(a.Get("4"), out int explicitId) && explicitId > 0
                    ? explicitId
                    : state.NextCardId(side);
                state.CreateWithId(name, side, cardId, target, state.NextLocationNumber(side, target));
                return true;
            }

            // 其它作弊命令先当无害动作放行，别让它污染「规则错误」的统计
            default:
                return true;
        }
    }

    private static string Board(GameState s, Side side)
    {
        var cards = s.Board(side);
        return cards.Count == 0
            ? "（空）"
            : string.Join("  ", cards.Select(c => $"{c.Name}#{c.CardId}(槽{c.LocationNumber} {c.Attack}/{c.Defense})"));
    }

    /// <summary>
    /// 自检：动作自带的卡组码解析出的卡名，必须与快照里同 cardID 的卡名一致。
    /// 5 局真实回放实测 0 冲突 —— 这是「快照 cardID 编号空间可信」的直接证据。
    /// </summary>
    private bool CheckIdentity(WireAction a, ReplayData replay,
                               ref int conflicts, ref int unknownCodes, out string? note)
    {
        note = null;

        // 只有 PC/AC/ML 的 action_data 里带卡组码；其它动作（XActionCheat 等）的
        // 下标语义完全不同（实测 XActionCheat 的键里会出现 "Hand_Right" 这种区名），
        // 硬套 CardCodes 会得到假冲突。
        if (a.ActionType is not ("PC" or "AC" or "ML"))
        {
            return true;
        }

        var codes = a.CardCodes;
        if (codes.Count == 0)
        {
            return true;
        }

        int primaryId = a.ActionType == "AC" ? a.CardId : a.CardId;
        for (int i = 0; i < codes.Count; i++)
        {
            string code = codes[i];
            if (!_db.DeckCodeIds.TryGetValue(code, out string? name))
            {
                unknownCodes++;
                note = $"卡组码 {code} 不在 deckCodeIDsTable2 里";
                return false;
            }

            // AC 的第 2 个码属于防御者，跳过交叉核对
            if (a.ActionType == "AC" && i == 1)
            {
                continue;
            }

            if (!replay.ById.TryGetValue(primaryId, out var snap))
            {
                continue;   // 对局中生成的卡，快照里没有
            }

            string snapBase = CardDatabase.ResolveBaseName(snap.Name);
            string codeBase = CardDatabase.ResolveBaseName(name);
            if (!string.Equals(snapBase, codeBase, StringComparison.Ordinal))
            {
                conflicts++;
                note = $"身份冲突 cardID={primaryId}：快照={snap.Name} 码{code}={name}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// `CS` 答复**没能落实**时的失败原因（要写清楚是缺什么，别写成一句"不支持"）。
    ///
    /// 这一族是 **Develop** —— 候选来自卡自己的 <c>GetChooseSpawnCards()</c>，
    /// 它的实现是 <c>GetAllActiveStaticCards()</c> + 过滤（例：<c>card_event_pams</c> 取
    /// 「英国 + 指令 + 总费 &lt; 5」），也就是**卡池里的模板卡**，不是牌库里的实例。
    ///
    /// 现在这条路已经打通（见 <c>CardApi.DevelopChosenCard</c>），所以剩下的失败只有两类：
    /// ① 卡组码不在 <c>deck_code_ids</c> 表里；② 卡名不在卡库里。
    /// </summary>
    private static string UnresolvedCsReason(WireAction a)
        => $"Develop 选牌：选中码 {a.Get(WireAction.KeyIndex.CodeSlotA)} 解不出卡名" +
           "（不在 deck_code_ids 表里，或卡名不在卡库中）";

    /// <summary>
    /// 把 <c>CS</c> 答复里的卡组码解成「那张卡的实例」。
    ///
    /// 为什么用卡组码而不是用下标：内核**算不出候选表** —— 候选来自卡自己的
    /// <c>GetChooseSpawnCards()</c>，而 IR 生成器只编事件入口，卡内私有函数没有进
    /// <c>card-ir.json</c>。回放的答复自带「选中卡的卡组码」，直接查
    /// <c>deckCodeIDsTable2</c> 拿卡名更可靠（也顺带绕开了"下标指向哪张"这个未知量）。
    ///
    /// 只在挑牌方**自己的牌库**里找：蓝图里的候选就是牌库里的卡对象。
    /// </summary>
    private CardInstance? CardFromDeckCode(CardInstance selecting, string? code, GameState state)
    {
        if (code is null || !_db.DeckCodeIds.TryGetValue(code, out string? name))
        {
            return null;
        }

        return state.Deck(selecting.Owner)
                    .FirstOrDefault(x => x.Name == name || x.Definition.Name == name);
    }

    /// <summary>拿到（必要时创建）动作引用的卡，归属 <paramref name="owner"/>。</summary>
    private CardInstance? ResolveCard(WireAction a, ReplayData replay, Side owner, Side foe, GameState state)
    {
        if (state.ById(a.CardId) is { } existing)
        {
            return existing;
        }

        string? name = NameOf(a, replay, 0) ?? NameOf(a, replay, 1);
        if (name is null)
        {
            return null;
        }

        // 归属：PC/ML/AC 的 0 号键都是行动方自己的卡
        return state.CreateWithId(name, owner, a.CardId, owner.DeckOf(), 0);
    }

    /// <summary>AC 的目标（1 号键 = 防御者 cardID，卡码在 3 号键）。</summary>
    private CardInstance? ResolveTarget(WireAction a, ReplayData replay, Side owner, Side foe, GameState state)
    {
        if (state.ById(a.SecondId) is { } existing)
        {
            return existing;
        }

        string? name = NameOf(a, replay, 1);
        return name is null ? null : state.CreateWithId(name, foe, a.SecondId, foe.DeckOf(), 0);
    }

    private string? NameOf(WireAction a, ReplayData replay, int codeIndex)
    {
        var codes = a.CardCodes;
        if (codeIndex < codes.Count && _db.DeckCodeIds.TryGetValue(codes[codeIndex], out string? name))
        {
            return name;
        }

        return null;
    }
}
