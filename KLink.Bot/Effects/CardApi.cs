using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

/// <summary>
/// 卡牌级效果 API —— 就是那 170 个「外部调用」的内核实现。
///
/// 名字与游戏反编译出的调用名**逐字一致**，这样：
/// 1. <c>docs/card-effects.json</c> 里的调用清单可以直接对着查
/// 2. 未实现的调用能被精确统计（<see cref="GameState.UnimplementedCalls"/>）
/// 3. 将来做「Kismet 字节码 → 效果脚本」的反编译器时，输出可以直接对接这里
///
/// ⚠️ 已知限制：反编译产物给的是**调用集合**，不是数据流图。
/// 也就是说我们知道一张卡调用了 <c>GetTargetedCard</c> 和 <c>ChangeDefense</c>，
/// 但不知道 ChangeDefense 用的是不是 GetTargetedCard 的返回值、数值是几。
/// 所以本类分成两层：
/// - <b>原语层</b>（下面这些方法）：语义明确、可复用，是真正要实现的
/// - <b>编排层</b>（<see cref="CardEffectScripts"/>）：每张卡具体怎么组合
/// 编排层目前只手工写了一批，其余记入未实现统计。
/// </summary>
public sealed partial class CardApi
{
    private readonly MatchEngine _engine;

    public CardApi(MatchEngine engine)
    {
        _engine = engine;
        _dispatch = BuildDispatch();
        Vm = new Blueprint.KismetVm(this);
    }

    /// <summary>Kismet 解释器 —— 执行卡牌蓝图的效果程序。</summary>
    public Blueprint.KismetVm Vm { get; }

    private GameState State => _engine.State;

    // ==================================================================
    //  编排层：执行一张卡的效果
    // ==================================================================

    /// <summary>
    /// 执行一张卡从手牌打出时的效果。
    ///
    /// 顺序：
    /// 1. 手写脚本（<see cref="CardEffectScripts"/>）—— 用于需要人工语气澄清的卡
    /// 2. **Kismet 解释器** —— 直接跑反编译出来的效果程序（覆盖全部 1636 张有蓝图的卡）
    /// 3. 都没有 → 计入未实现统计
    /// </summary>
    public void RunCardEffect(CardInstance card, CardInstance? target)
    {
        var ctx = new EffectContext
        {
            Engine = _engine,
            State = State,
            Self = card,
            Target = target,
            Controller = card.Owner,
            Calls = card.Definition.PlayFromHandCalls().Distinct(StringComparer.Ordinal).ToList(),
        };

        // 1) 手写脚本优先
        if (CardEffectScripts.TryGet(card.Name, out var script)
            || CardEffectScripts.TryGet(card.Definition.Name, out script))
        {
            script(ctx);
            return;
        }

        // 2) Kismet 解释器
        var library = Blueprint.KismetLibrary.Default;
        var program = library?.FindProgram(card.Definition.Name, "OnPlayedFromHand");
        if (program is not null)
        {
            Vm.Run(program, ctx);
            return;
        }

        // 3) 没有 OnPlayedFromHand —— 先判断这是不是**正常**的。
        //    竞技卡组里大量单位只有触发式效果（例如「本回合受到攻击时 +1」），
        //    它们本来就没有战吼。这种情况不是缺口，不该计入。
        if (library?.ProgramNames(card.Definition.Name).Any() == true)
        {
            return;   // 触发式卡：效果挂在它自己的事件程序上，等事件派发
        }

        // 4) 连蓝图逻辑都没有 → 才算真的缺口
        NotifyUnimplemented($"<card:{card.Definition.Name}>");
    }

    /// <summary>
    /// 派发一个触发事件。
    ///
    /// 事件名**就是**蓝图里的程序名（<c>OnStartOfTurn</c> / <c>OnAfterAttack</c> /
    /// <c>OnOtherCardDestroyed</c> …），不做二次映射 —— IR 里有什么名字就用什么名字。
    ///
    /// <paramref name="fireOthers"/> 为 true 时，还会给**其它**卡派发对应的
    /// <c>OnOtherXxx</c> 事件（例如「当其它卡被打出时」）。
    /// </summary>
    /// <param name="selfProgramName">
    /// 给事件主体（<paramref name="subject"/>）单独用的程序名。
    ///
    /// ⚠️ 需要它的理由：同一个时机常常有**两个**不同的「别人」程序名，
    /// 例如离场时 <c>OnOtherCardLeaveBoardOrOwner</c>（`card_unit_214th_amur`）
    /// 和 <c>OnAfterOtherCardLeaveBoardOrOwner</c>（`card_unit_big_red_one`）
    /// 是并列的，第二遍调用时要用 <c>selfProgramName</c> 把主体那一路置空，
    /// 否则主体会被触发两次、光环被撤销两次。
    /// </param>
    /// <param name="eventSubject">
    /// **事件参数**里那张卡 —— 也就是 <c>K2Node_Event_cardPlayed</c> /
    /// <c>K2Node_Event_cardLeaving</c> 应当读到的卡。
    ///
    /// ⚠️ 它和 <paramref name="subject"/> **不是一回事**，这个区分是必须的：
    /// <paramref name="subject"/> 是「事件的主角」（决定哪张卡的程序被跑、
    /// 以及"别人"分支要排除谁），而 <paramref name="eventSubject"/> 是事件**携带的数据**。
    /// 典型场景：A 被打出 → 给场上的光环派发 `OnOtherCardPlayedFromHand`，
    /// 这时主角是 A，但**被派发到的是光环**。光环的程序里
    /// `tempCard = K2Node_Event_cardPlayed` 想拿的是 A；
    /// 如果只传 subject、让 VM 拿 Self 兜底，光环会读到自己 ——
    /// 实测症状：`IsOrder(光环)` 恒假，85 先驱连永远不还原费用。
    /// 传 null 时退回 <paramref name="subject"/>（大部分事件两者本来就相同）。
    /// </param>
    /// <param name="namedArgs">
    /// 具名参数（按蓝图出参槽名）。`KismetVm.GetMember` 在具名载荷里找不到时，
    /// 回退到位置参数 <paramref name="eventArgs"/>。
    /// </param>
    /// <param name="broadcastName">
    /// **这个程序名本身是"广播给别人"的，即使它不以 `OnOther` 开头。**
    ///
    /// ⚠️ 为什么需要它：广播判据原先是纯命名约定（`StartsWith("OnOther")`），
    /// 但蓝图里有一批广播名不满足这个约定，最典型的是
    /// `OnBeforeOtherCardPlayedFromHand`（39 张订阅者）——
    /// 它名字里带 `Other` 但前缀是 `OnBefore`，于是被当成"只发给主体"，
    /// **39 张卡的整条分支静默死掉**。同一个坑还有
    /// `OnAfterOtherCardSuppressed`（见 `SuppressUnit`，那里用 otherProgramName 绕开）。
    ///
    /// 出处（`out/bp-cardfn.json`，函数 `CardPlayedFromHand`）：
    /// <code>
    /// si=1322  FetchAllCardsWithEventTrigger(19)      ; 19 = OnBeforeOtherCardPlayedFromHand
    /// si=1386      NotEqual_ObjectObject(item, cardPlayed)   ; ★ 广播**排除主体**
    /// si=1493      item.OnBeforeOtherCardPlayedFromHand(cardPlayed)
    /// </code>
    /// </param>
    public void FireTrigger(string programName, CardInstance? subject, Side controller,
                            string? otherProgramName = null, string? selfProgramName = null,
                            IReadOnlyList<object?>? eventArgs = null,
                            CardLocation? goingToLocation = null,
                            CardInstance? eventSubject = null,
                            IReadOnlyDictionary<string, object?>? namedArgs = null,
                            CardLocation? oldLocation = null,
                            CardLocation? newLocation = null,
                            bool broadcastName = false)
    {
        CardInstance? eventCard = eventSubject ?? subject;
        var library = Blueprint.KismetLibrary.Default;
        if (library is null)
        {
            return;
        }

        // 性能要点：**遍历"还活着的卡"**，而不是遍历「实现了该事件的卡名列表」。
        // 后者可能有几百个名字，每次派发都要对每个名字扫一遍找实例；
        // 前者每张卡一次字典查询。实测差几十倍。
        //
        // ⚠️ 必须先快照：触发程序内部会创建/销毁卡（例如 meteor 打完自己移除），
        //    直接迭代活列表会抛 "Collection was modified"。
        //
        // ⚠️ 而且 FireTrigger 会**重入**（触发效果里又派发事件），
        //    所以缓冲不能共用一份 —— 内层 Clear() 会把外层正在遍历的列表清空。
        //    这里按嵌套深度取不同的缓冲。
        //
        // ⚠️⚠️ **快照必须包含弃牌堆**，不能只有棋盘。
        //    判据（全卡池统计）：订阅 `OnOtherCardPlayedFromHand` 的 131 张里有
        //    **23 张是指令**、订阅 `OnEndOfTurn` 的 188 张里有 **69 张指令**、
        //    `OnOtherCardReset` 39 张里 12 张指令。指令打出后**立刻进弃牌堆**，
        //    却仍然订阅这些"打出之后才发生"的事件 —— 说明客户端是把触发派发给
        //    仍在局内的指令卡的。最典型的例子就是 `card_event_committed_crew`：
        //    卡面写"Until end of turn, Spitfires … get +3+3 when deployed"，
        //    它自己是指令、在弃牌堆里，却要靠 `OnOtherCardPlayedFromHand`
        //    给之后部署的 Spitfire 加成。只扫棋盘的话这条永远不生效。
        //
        //    为什么不怕"死掉的单位也被派发"：卡蓝图里每个分支**开头都自带**
        //    `IsLocatedOnBoard` / `IsValid` 守卫（实测 85_pioneer 的每条分支、
        //    committed_crew 的每条分支都是这么写的），不在场的卡会被自己挡掉。
        var snapshot = SnapshotBuffer(_triggerDepth);
        snapshot.Clear();
        for (int i = 0; i < 2; i++)
        {
            Side s = i == 0 ? Side.Left : Side.Right;
            snapshot.AddRange(State.Board(s));
            snapshot.AddRange(State.Discard(s));
        }

        foreach (var card in snapshot)
        {
            // ⚠️ 这里是 **`!= NotAvailable`**，不是 `IsAlive`。
            //
            // `IsAlive` 把「在弃牌堆」也算作"不在场"（它的语义是"还能被打/被选中"），
            // 而触发派发要的是"这张卡**还在局内**"：指令打出后就躺在弃牌堆里，
            // 但它的"本回合内持续生效"效果还得继续响应事件
            // （`card_event_committed_crew` 就是这类，见上面快照那段注释）。
            // 只有 `NotAvailable`（被移出对局）才真正不该再收到任何触发。
            // 不在场的卡会不会误触发，由它自己程序里的 `IsLocatedOnBoard` 守卫负责。
            if (card.Location == CardLocation.NotAvailable)
            {
                continue;
            }

            string name = card.Name;
            bool isSubject = ReferenceEquals(card, subject);

            // 主体那一路要跑的程序名（调用方可用 selfProgramName 覆盖）
            string selfProgram = selfProgramName ?? programName;

            // 程序名是「广播给别的卡」还是「只有主体自己」？
            //
            // 判据是**命名约定**，而且是实测出来的：
            //   · `OnOtherXxx` 是广播 —— `OnOtherCardPlayedFromHand` 有 131 个订阅者，
            //     而它的事件主体是"刚被打出的那张牌"，那张牌自己显然不是订阅者。
            //   · 其余 `OnXxx` 是「**自己**发生了 X」—— 只给主体。
            //
            // ⚠️ 旧实现把 `programName` 无条件发给**场上每一张卡**，这个错误非常隐蔽：
            //    `card_unit_3_panzergrenadier` 的 `OnAfterAttack` 程序体是
            //    **无条件** `ChangeAttack(+1)+ChangeDefense(+1)`（"我攻击了就 +1+1"），
            //    于是别人攻击时它也跟着涨 —— 实测一个美国 M2A4 操作也能让它 +1+1，
            //    表现成"累计涨超"（T5 6/6、T7 11/12，比实际德国单位操作次数多）。
            //    它的 `OnOtherCardAttacks` 才是那条带阵营判定的分支。
            bool broadcast = selfProgramName is null
                             && (broadcastName
                                 || programName.StartsWith("OnOther", StringComparison.Ordinal));

            if (broadcast)
            {
                // 广播：除主体之外的所有卡
                if (!isSubject && library.FindProgram(name, programName) is not null)
                {
                    TriggerTrace?.Add($"{programName} → {name}#{card.CardId}" +
                                      $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");
                    RunTriggerProgram(library, card, name, programName, eventCard, eventArgs, goingToLocation,
                                      namedArgs, oldLocation, newLocation);
                }
            }
            else if (subject is null || isSubject)
            {
                // 自己那一路：主体在场就只发主体；主体为 null（全局事件）时发给所有卡
                if (library.FindProgram(name, selfProgram) is not null)
                {
                    TriggerTrace?.Add($"{selfProgram} → {name}#{card.CardId}" +
                                      $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");
                    RunTriggerProgram(library, card, name, selfProgram, eventCard, eventArgs, goingToLocation,
                                      namedArgs, oldLocation, newLocation);
                }
            }

            // 显式的「别的卡」那一路：除主体之外的所有卡
            if (otherProgramName is not null
                && !isSubject
                && library.FindProgram(name, otherProgramName) is not null)
            {
                TriggerTrace?.Add($"{otherProgramName} → {name}#{card.CardId}" +
                                  $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");
                RunTriggerProgram(library, card, name, otherProgramName, eventCard, eventArgs, goingToLocation,
                                  namedArgs, oldLocation, newLocation);
            }
        }

        // 事件主体可能不在场上（例如刚被打出、或已被移走），单独补一次。
        // ⚠️ 只补「自己那一路」：`OnOtherXxx` 是广播给**别人**的，
        //    把主体也算进去会让"刚抽到手的牌"响应自己的 `OnOtherCardDrawnFromDeck`。
        string subjectProgram = selfProgramName ?? programName;
        bool subjectBroadcast = selfProgramName is null
                                && (broadcastName
                                    || programName.StartsWith("OnOther", StringComparison.Ordinal));
        if (!subjectBroadcast
            && subject is not null && subject.IsAlive && !subject.IsHq && !subject.Location.IsBoard()
            && library.FindProgram(subject.Name, subjectProgram) is not null)
        {
            // ⚠️ 这一支**也必须记 TriggerTrace**。原先漏了它，于是"主体不在棋盘上"
            //    （刚抽到手 / 刚生成到手 / 刚进弃牌堆…）的派发在诊断记录里**完全看不见** ——
            //    自测据此判"没派发"，会把好代码判死。属于诊断口径的 bug，不是规则行为。
            TriggerTrace?.Add($"{subjectProgram} → {subject.Name}#{subject.CardId}" +
                              $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}，主体不在棋盘，兜底那一路）");
            RunTriggerProgram(library, subject, subject.Name, subjectProgram, eventCard, eventArgs, goingToLocation,
                              namedArgs, oldLocation, newLocation);
        }
    }

    /// <summary>触发派发用的快照缓冲，按嵌套深度索引（FireTrigger 会重入）。</summary>
    private readonly List<List<CardInstance>> _triggerBuffers = new();

    /// <summary>诊断用：非 null 时记录每一次实际派发（`事件名 → 卡名#ID`）。</summary>
    public List<string>? TriggerTrace { get; set; }

    private List<CardInstance> SnapshotBuffer(int depth)
    {
        while (_triggerBuffers.Count <= depth)
        {
            _triggerBuffers.Add(new List<CardInstance>(16));
        }

        return _triggerBuffers[depth];
    }

    private CardInstance? FindOnBoard(string cardName, CardInstance? prefer)
    {
        if (prefer is not null && prefer.IsAlive
            && (prefer.Name == cardName || prefer.Definition.Name == cardName))
        {
            return prefer;
        }

        foreach (var side in new[] { Side.Left, Side.Right })
        {
            foreach (var card in State.Board(side))
            {
                if (card.IsAlive && (card.Name == cardName || card.Definition.Name == cardName))
                {
                    return card;
                }
            }
        }

        return null;
    }

    private void RunTriggerProgram(Blueprint.KismetLibrary library, CardInstance card, string cardName,
                                   string programName, CardInstance? trigger,
                                   IReadOnlyList<object?>? eventArgs = null,
                                   CardLocation? goingToLocation = null,
                                   IReadOnlyDictionary<string, object?>? namedArgs = null,
                                   CardLocation? oldLocation = null,
                                   CardLocation? newLocation = null)
    {
        var program = library.FindProgram(cardName, programName);
        if (program is null)
        {
            return;
        }

        // 递归保护：一个效果触发另一个事件、那个事件又触发回来，是很容易出现的环。
        // 超过深度就直接放弃这次派发（真实客户端用「动作队列」串行化，不会无限递归）。
        if (_triggerDepth >= MaxTriggerDepth)
        {
            Vm.UnsupportedOps["<trigger-depth-limit>"] = Vm.UnsupportedOps.GetValueOrDefault("<trigger-depth-limit>") + 1;
            return;
        }

        var ctx = new EffectContext
        {
            Engine = _engine,
            State = State,
            Self = card,
            Target = trigger,
            Trigger = trigger,
            Controller = card.Owner,
            EventArgs = eventArgs ?? Array.Empty<object?>(),
            GoingToLocation = goingToLocation,
            NamedArgs = namedArgs ?? EffectContext.EmptyNamedArgsPublic,
            OldLocation = oldLocation,
            NewLocation = newLocation,
        };

        _triggerDepth++;
        try
        {
            Vm.Run(program, ctx);
        }
        finally
        {
            _triggerDepth--;
        }
    }

    /// <summary>触发派发的最大嵌套深度。</summary>
    public const int MaxTriggerDepth = 8;

    private int _triggerDepth;



    // ==================================================================
    //  通用操作（被效果脚本使用）
    // ==================================================================

    /// <summary>造成伤害。所有伤害都走这里，保证事件顺序一致。</summary>
    /// <param name="isCombatDamage">
    /// 是不是战斗伤害（攻击结算）。判据来自蓝图：`ExecuteAttackCard` 调
    /// `ExecuteOnCardDealDamageEffects(…, isCombatDamage=True, …)` 两次
    /// （i=3363 防守方受伤、i=3451 攻击方反击受伤），
    /// 而 `ApplyDamageToCard` i=1808 / `ApplyDamageToMultipleCards` i=4933
    /// 都是 `False` —— 也就是"效果伤害"。
    /// </param>
    /// <param name="counterDamage">是不是反击伤害（`ExecuteAttackCard` i=3451 传 True）。</param>
    public void DealDamage(CardInstance target, int amount, CardInstance? source,
                           bool isCombatDamage = false, bool counterDamage = false, bool isRedirected = false)
    {
        if (amount <= 0 || !target.IsAlive)
        {
            return;
        }

        _engine.ApplyDamage(target, amount, source);

        _engine.FireSubAction("ZActionDamageCard", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("damage", amount),
            ActionValue2.Int("oldDefense", target.Defense + amount),
            ActionValue2.Bool("destroyed", target.Defense <= 0),
            ActionValue2.Int("attackerCardID", source?.CardId ?? 0),
        });

        FireTrigger("OnReceiveDamage", target, target.Owner, "OnOtherCardReceiveDamage");

        // ---- 「造成伤害」事件 ----
        //
        // 出处（`out/bp-cardfn.json`，函数 `ExecuteOnCardDealDamageEffects`，
        // 调用方 i=1808 / i=4933 / i=3363 / i=3451）：
        //   i=541  FetchAllCardsWithEventTrigger(36)      ; 36 = OnOtherCardDealDamage
        //   i=1582 item.OnOtherCardDealDamage(damageDealer, toCard, damage, isCombatDamage, CounterDamage, isRedirected)
        //   i=1198 damageDealer.OnCardDealDamage(toCard, damage, isCombatDamage, CounterDamage, isRedirected, out qqq)
        // 触发号 36 的依据：`ERegisteredCardFunction.h` 逐项数下来
        //   0=NotAvailable … 36=OnOtherCardDealDamage（第 37 项）。
        if (source is not null && source.IsAlive)
        {
            var named = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDealingDamage"] = source,
                ["toCard"] = target,
                ["Damage"] = amount,
                ["damage"] = amount,
                ["isCombatDamage"] = isCombatDamage,
                ["CounterDamage"] = counterDamage,
                ["isRedirected"] = isRedirected,
            };

            FireTrigger("OnCardDealDamage", source, source.Owner, "OnOtherCardDealDamage",
                eventArgs: new object?[] { source, target, amount, isCombatDamage, counterDamage, isRedirected },
                eventSubject: source, namedArgs: named);
        }

        if (target.Defense <= 0 && !target.IsHq)
        {
            _engine.Destroy(target, source);
        }
    }

    /// <summary>
    /// 「在战斗里活下来了」—— 对应 `BP_CardFunctions::ExecuteOnSurvivedCombatEvents`
    /// （`out/bp-cardfn.json`，25 条语句；9 张订阅 `OnSurvivedCombat`、6 张订阅
    /// `OnOtherCardSurvivedCombat`）：
    /// <code>
    /// si=38   JumpIfNot(cardSurviving.isSuppressed) -> si=549   ; 被压制 ⇒ 不广播
    /// si=74   FetchAllCardsWithEventTrigger(59)                ; 59 = OnOtherCardSurvivedCombat
    /// si=343      NotEqual_IntInt(item.cardID, cardSurviving.cardID)   ; 广播**排除自己**
    /// si=494      item.OnOtherCardSurvivedCombat(cardSurviving, cardCombatted)
    /// si=549  cardSurviving.OnSurvivedCombat(cardCombatted)     ; ★ 自己：压制与否都发
    /// </code>
    /// 调用点（同一份 dump，`ExecuteAttackCard`）：si=3201 `(attacker, defender)`、
    /// si=3249 `(defender, attacker)`，两者的门都是"**没被摧毁**"
    /// （si=3186/3234 `JumpIfNot(xDestroyed)`），且整段在
    /// si=3130 `IsUnit(defender)` 的 `PopExecutionFlowIfNot`（si=3171）之内 ——
    /// 也就是**打 HQ 不触发这一族**。
    ///
    /// ⚠️ **本内核的近似**：蓝图先算好 `xDestroyed` 再发事件（语句顺序上事件在
    /// "造成伤害"之前），内核没有那套预算，只能在伤害结算**之后**按
    /// `IsAlive` 判"活下来了"。对活下来的卡，事件里的防御力因此已经是打完之后的值。
    /// </summary>
    public void FireSurvivedCombat(CardInstance surviving, CardInstance combatted)
    {
        if (!surviving.Keywords.Contains(Keyword.Suppressed))
        {
            FireTrigger("OnOtherCardSurvivedCombat", surviving, surviving.Owner,
                eventArgs: new object?[] { surviving, combatted },
                eventSubject: surviving,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardSurviving"] = surviving,
                    ["cardCombatted"] = combatted,
                });
        }

        FireTrigger("OnSurvivedCombat", surviving, surviving.Owner,
            eventArgs: new object?[] { combatted },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardCombatted"] = combatted,
            });
    }

    public void HealCard(CardInstance target, int amount)
    {
        if (!target.IsAlive || amount <= 0)
        {
            return;
        }

        int before = target.Defense;
        target.Defense = Math.Min(target.MaxDefense, target.Defense + amount);
        if (target.Defense != before)
        {
            _engine.FireSubAction("ZActionHealCard", new[]
            {
                ActionValue2.Int("cardID", target.CardId),
                ActionValue2.Int("amount", target.Defense - before),
            });
        }

        // 「被完全修复」—— 出处 `out/bp-cardfn.json` 函数 `FullyHealCard`
        // （i=1683 `card.OnFullyRepaired`），签名 `BaseCardObject.h:667`：
        //   `OnFullyRepaired(int32 amount)`
        //   `OnOtherCardFullyRepaired(UBaseCardObject* cardRepaired, int32 amount)`
        // 判据：修完之后防御力**等于上限**（"fully repaired"）。
        if (target.Defense == target.MaxDefense && target.Defense > before)
        {
            FireTrigger("OnFullyRepaired", target, target.Owner, "OnOtherCardFullyRepaired",
                eventArgs: new object?[] { target, target.Defense - before },
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["amount"] = target.Defense - before,
                    ["cardRepaired"] = target,
                });
        }
    }

    /// <summary>
    /// 「这张卡能被加 buff 吗」—— 逐字对应 `BP_CardFunctions::CanCardBeBuffed`
    /// （`out/bp-cardfn.json`，30 条语句）：
    /// <code>
    /// si=41   JumpIfNot(IsUnrevealedCovertCard(Card)) -> si=730   ; 未揭示的隐蔽卡 ⇒ 可以直接 buff
    /// si=55..711  switch(Card.location)：
    ///             0(NotAvailable)                      → si=746  False
    ///             1/2(牌库) 3/4(手牌)                  → si=762  True
    ///             5/6(半场/HQ) 7(前线) 8(弃牌堆)        → si=746  False
    ///             9(牌库)                              → si=762  True
    /// </code>
    /// 调用点（同一份 dump，`CanCardBeBuffed` 出现在这些函数的守位）：
    /// `ChangeAttack` si=71/103、`ChangeDefense` si=48/80、`ChangeKreditCost` si=385/417、
    /// `CustomAbilityAdd` si=99/131，以及 `GiveGuard`/`GiveAlpine`/`GiveAmbush`/`GiveBlitz`/
    /// `GiveBond`/`GiveFury`/`GiveShock`/`GiveSmokescreen` 一族各 si=94/126。
    /// 语义：**buff 只能加在牌库/手牌里的卡上**；已经在场(5/6/7)或进了弃牌堆(8)的卡不再接受 buff
    /// （那些卡的数值改动由别的路径负责，例如 `SetValue` 的绝对值设置与战斗结算）。
    ///
    /// ⚠️ 本内核**没有建模 Covert**（P1），所以 `IsUnrevealedCovertCard` 恒假 ——
    /// 也就是说这里不会出现"隐蔽卡例外"。这是已知近似。
    /// </summary>
    public static bool CanCardBeBuffed(CardInstance card) => card.Location switch
    {
        CardLocation.DeckLeft or CardLocation.DeckRight => true,   // 1 / 2
        CardLocation.HandLeft or CardLocation.HandRight => true,   // 3 / 4
        CardLocation.Deck => true,                                 // 9
        _ => false,                                                // 0 / 5 / 6 / 7 / 8
    };

    public void ChangeAttack(CardInstance target, int delta, CardInstance? source, int duration = -1)
    {
        if (!target.IsAlive || delta == 0)
        {
            return;
        }

        target.Attack = Math.Max(0, target.Attack + delta);
        if (source is not null)
        {
            var buff = GetOrCreateBuff(target, source.CardId);
            buff.Attack += delta;
            buff.Duration = duration;
        }

        _engine.FireSubAction("ZActionGainAttack", new[]
        {
            ActionValue2.Int("instigatorID", source?.CardId ?? target.CardId),
            ActionValue2.Int("gained", delta),
            ActionValue2.Int("newAttackValue", target.Attack),
        });

        // 「攻击力变了」事件 —— 出处 `out/bp-cardfn.json` 函数
        // `ExecuteAfterChangeAttackEvents`（签名 `BaseCardObject.h:829/799`）：
        //   `OnAfterChangeAttack(int32 changedAmount, int32 instigatorID)`（自己）
        //   `OnAfterOtherCardChangeAttack(UBaseCardObject* cardChanged, int32 changedAmount, int32 instigatorID)`
        FireTrigger("OnAfterChangeAttack", target, target.Owner, "OnAfterOtherCardChangeAttack",
            eventArgs: new object?[] { target, delta, source?.CardId ?? target.CardId },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["changedAmount"] = delta,
                ["instigatorID"] = source?.CardId ?? target.CardId,
                ["cardChanged"] = target,
            });
    }

    public void ChangeDefense(CardInstance target, int delta, CardInstance? source)
    {
        if (!target.IsAlive || delta == 0)
        {
            return;
        }

        target.Defense += delta;
        target.MaxDefense = Math.Max(target.MaxDefense, target.Defense);
        if (source is not null)
        {
            GetOrCreateBuff(target, source.CardId).Defense += delta;
        }

        _engine.FireSubAction("ZActionGainDefense", new[]
        {
            ActionValue2.Int("instigatorID", source?.CardId ?? target.CardId),
            ActionValue2.Int("gained", delta),
            ActionValue2.Int("newDefenseValue", target.Defense),
        });

        // 「防御力变了」事件 —— 出处 `out/bp-cardfn.json` 函数 `ChangeDefense`
        // （i=4879 `cardToChangeRef.OnAfterGainDefense`、i=1589 `…OnAfterDefenseIsSet`），
        // 签名 `BaseCardObject.h:814/793`：
        //   `OnAfterGainDefense(int32 defenseGained)`（自己）
        //   `OnAfterOtherCardGainDefense(UBaseCardObject* cardGainingDefense, int32 defenseGained)`
        if (delta > 0)
        {
            FireTrigger("OnAfterGainDefense", target, target.Owner, "OnAfterOtherCardGainDefense",
                eventArgs: new object?[] { target, delta },
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["defenseGained"] = delta,
                    ["cardGainingDefense"] = target,
                });
        }

        if (target.Defense <= 0 && !target.IsHq)
        {
            _engine.Destroy(target, source);
        }
    }

    /// <summary>
    /// 「防御力被设成某个值」—— 对应蓝图 `ChangeDefense` 的 `SetValue` 分支
    /// （`EChangeType::SetValue`，i=1589 `OnAfterDefenseIsSet`）。
    /// 与 <see cref="ChangeDefense"/> 分开，因为事件名不同。
    /// </summary>
    public void SetDefenseValue(CardInstance target, int value, CardInstance? source)
    {
        ChangeDefense(target, value - target.Defense, source);

        if (!target.IsAlive)
        {
            return;
        }

        FireTrigger("OnAfterDefenseIsSet", target, target.Owner, "OnAfterOtherCardDefenseIsSet",
            eventArgs: new object?[] { target },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDefenseSet"] = target,
            });
    }

    public void ChangeKreditCost(CardInstance target, int delta)
    {
        target.KreditCost = Math.Max(0, target.KreditCost + delta);
        _engine.FireSubAction("ZActionSetKreditCost", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("kreditCost", target.KreditCost),
        });
    }

    public void ChangeOperationCost(CardInstance target, int delta, CardInstance? source)
        => ChangeOperationCost(target, delta, source?.CardId ?? 0);

    public void ChangeOperationCost(CardInstance target, int delta, int instigatorId)
    {
        target.OperationCost = Math.Max(0, target.OperationCost + delta);
        _engine.FireSubAction("ZActionChangeOperationCost", new[]
        {
            ActionValue2.Int("instigatorID", instigatorId),
            ActionValue2.Int("amount", delta),
        });
    }

    /// <summary>增加 kredit 槽位上上限并回满（ZActionChangeKredits 的常见用法）。</summary>
    public void GainKreditSlot(Side side, int count)
    {
        State.AddMaxKredits(side, count);
        State.AddKredits(side, count);
        _engine.FireSubAction("ZActionChangeKredits", new[]
        {
            ActionValue2.Str("side", side.ToWire()),
            ActionValue2.Int("newMaxKredits", State.MaxKredits(side)),
            ActionValue2.Int("newKredits", State.Kredits(side)),
        });

        FireExtraKreditSlotGain(side, count, giver: null);
    }

    /// <summary>
    /// 「额外 kredit 槽位到手」事件 —— 出处 `out/bp-cardfn.json` 函数 `GainKreditSlot`
    /// （i=680）与 `LoseKreditSlot`（i=574），签名 `BaseCardObject.h:817`：
    /// <code>void OnAfterExtraKreditSlotGain(UBaseCardObject* cardGivingKredit,
    ///                                      ESideEnum sideGaining, bool isNegativeGain);</code>
    /// 没有「自己/别人」两个变体 —— 它是**广播给所有订阅者**的（`giver` 作为事件参数）。
    /// </summary>
    public void FireExtraKreditSlotGain(Side side, int count, CardInstance? giver)
        => FireTrigger("OnAfterExtraKreditSlotGain", null, side,
            eventArgs: new object?[] { giver, (int)side, count < 0 },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardGivingKredit"] = giver,
                ["sideGaining"] = (int)side,
                ["isNegativeGain"] = count < 0,
            });

    public void DrawCards(Side side, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _engine.DrawCard(side);
        }
    }

    /// <summary>把一张卡生成到手牌（对应 SpawnCardInHandBySide / doSpawnCardInHand）。</summary>
    public CardInstance SpawnCardInHand(Side side, string cardName)
    {
        var card = State.Create(cardName, side, side.HandOf(), State.NextLocationNumber(side, side.HandOf()));
        _engine.FireSubAction("ZActionSpawnCard", new[]
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Int("location", (int)side.HandOf()),
        });

        // ⚠️ **两个事件都要发**，这是本轮的 bug 修复点之一。
        //
        // 出处：`out/bp-cardfn.json` 函数 `ExecuteOnSpawnedInHandEvents(spawnedCardID, spawnedSide)`
        //   i=132  `spawnedCard.OnCardSpawnedInHand()`        ← **自己**（此前从未派发）
        //   i=177  FetchAllCardsWithEventTrigger(57)         ; 57 = OnOtherCardSpawnedInHand
        //   i=750  `item.OnOtherCardSpawnedInHand(spawnedCardID, spawnedSide)`
        //
        // 旧实现只发了 `OnOtherCardSpawnedInHand`，而 `CardApi.FireTrigger` 的
        // `broadcast` 判定（`programName.StartsWith("OnOther")`）会**把主体自己排除**，
        // 于是"刚被生成到手牌的那张卡自己的进场逻辑"永远不跑（21 张卡订阅它）。
        FireTrigger("OnCardSpawnedInHand", card, side,
            eventArgs: new object?[] { (int)side },
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["spawnedSide"] = (int)side,
            });

        FireTrigger("OnOtherCardSpawnedInHand", card, side,
            eventArgs: new object?[] { card.CardId, (int)side },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["spawnedCardID"] = card.CardId,
                ["spawnedSide"] = (int)side,
            });
        return card;
    }

    /// <summary>把一张卡生成到战场（对应 `SpawnCardOnBattlefield`）。</summary>
    /// <param name="frontline">
    /// 落在**前线**（true）还是**半场**（false）—— 就是蓝图签名的第 2 个参数 `bool Frontline`
    /// （`CardFunctionsStub.h:68`）。⚠️ 234 个调用点里 **215 个传 `false`**，
    /// 旧实现写死 `BoardFrontline`，等于把这些卡全放错地方。
    /// </param>
    /// <param name="locationNumber">
    /// 指定槽位；`-1`（IR 里 193/234 个调用点）表示"追加到队尾"。
    /// </param>
    public CardInstance SpawnOnBattlefield(Side side, string cardName,
                                           bool frontline = true,
                                           int locationNumber = -1,
                                           bool newGiveBlitz = false,
                                           bool forceGoldCard = false)
    {
        CardLocation where = frontline ? CardLocation.BoardFrontline : side.HqOf();
        var card = State.Create(cardName, side, where, 0, isGold: forceGoldCard);
        card.EnteredPlayOnTurn = State.Turn;

        int slot = locationNumber >= 0
            ? locationNumber
            : State.Cards(side, where).Where(c => c != card).Select(c => c.LocationNumber).DefaultIfEmpty(-1).Max() + 1;
        State.Move(card, where, slot);

        if (newGiveBlitz)
        {
            card.Keywords.Add(Keyword.Blitz);
        }

        _engine.FireSubAction("ZActionSpawnCard", new[]
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Int("location", (int)where),
        });
        return card;
    }

    public void DestroyCard(CardInstance target, CardInstance? source)
    {
        if (target.IsHq)
        {
            DealDamage(target, target.Defense, source);
            return;
        }

        _engine.Destroy(target, source);
    }

    /// <summary>
    /// 弃一张牌（对应 `BP_CardFunctions::DiscardCardFromHand` / `DiscardCard`）。
    ///
    /// <paramref name="discarder"/> 是"谁弃的"（事件第 2 个入参 `discarderID`）。
    /// 内核大量调用点是"效果让某张牌被弃"，没有明确的施加者，
    /// 这时传 null ⇒ `discarderID = 0`。**这是近似**，不猜一个假的施动者。
    /// </summary>
    public void DiscardCard(CardInstance card, CardInstance? discarder = null)
    {
        bool suppressed = card.Keywords.Contains(Keyword.Suppressed);
        State.Move(card, CardLocation.Discard);
        _engine.FireSubAction("ZActionDiscardCard", new[]
        {
            ActionValue2.Int("discarderID", discarder?.CardId ?? card.CardId),
        });

        // 「别的卡被弃了」—— 出处 `out/bp-cardfn.json` 函数 `DiscardCardFromHand`
        // （签名 `CardFunctionsStub.h`；9 张订阅者）：
        //   si=500  JumpIfNot(tmpCardToDiscard.isSuppressed) -> si=1236   ; 被压制 ⇒ 不广播
        //   si=536  FetchAllCardsWithEventTrigger(41)
        //   si=809  item.OnOtherCardDiscarded(tmpCardToDiscard, discarderID)
        // 广播集**不排除被弃的那张卡自己**（蓝图里没有 cardID 比对），
        // 但内核 FireTrigger 的 `OnOther*` 约定会排除主体 —— 见 FireTrigger 的注释。
        if (!suppressed)
        {
            FireTrigger("OnOtherCardDiscarded", card, card.Owner,
                eventArgs: new object?[] { card, discarder?.CardId ?? 0 },
                eventSubject: card,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardDiscarded"] = card,
                    ["discarderID"] = discarder?.CardId ?? 0,
                });
        }
    }

    public void GiveKeyword(CardInstance target, string keyword)
    {
        target.Keywords.Add(keyword);
        _engine.FireSubAction($"ZActionGive{keyword}", new[]
        {
            ActionValue2.Int("giverID", target.CardId),
        });

        FireAbilitiesChanged(target);
    }

    public void RemoveKeyword(CardInstance target, string keyword)
    {
        if (!target.Keywords.Remove(keyword))
        {
            return;
        }

        _engine.FireSubAction($"ZActionRemove{keyword}", new[]
        {
            ActionValue2.Int("giverID", target.CardId),
        });

        FireAbilitiesChanged(target);
    }

    /// <summary>
    /// 「这张卡的能力集变了」—— 出处 `out/bp-cardfn.json` 函数
    /// `ExecuteOnOtherCardsAbilitiesChanged`（i=307），签名 `BaseCardObject.h:643`：
    /// <code>void OnOtherCardAbilitiesChanged(UBaseCardObject* cardChanging);</code>
    /// 只有广播这一种形态（没有"自己"那个变体）。
    /// 调用点（同一份 dump）：`ChangeHeavyArmor`、以及 Give/Remove 关键字一族。
    /// </summary>
    public void FireAbilitiesChanged(CardInstance card)
        => FireTrigger("OnOtherCardAbilitiesChanged", card, card.Owner,
            eventArgs: new object?[] { card },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardChanging"] = card,
            });

    /// <summary>
    /// 压制一张单位（对应 `BP_CardFunctions::SuppressUnit` → `SuppressMultipleUnits`）。
    ///
    /// 出处：`out/bp-cardfn.json` 函数 `SuppressMultipleUnits`（`SuppressUnit` i=20 转发过来）：
    /// <code>
    /// i=548  _card.IsLocatedOnBoard()
    /// i=589  Not_PreBool(_card.isSuppressed)
    /// i=618  BooleanAND(i=548, i=589)              ; 守卫：在场 && 尚未被压制
    /// i=656  PopExecutionFlowIfNot                 ; 不满足就整段跳过
    /// i=681  _wasAlreadySuppressed = _card.isSuppressed
    /// i=722  _card.isSuppressed = true
    /// i=788  JumpIfNot 6194 (_wasAlreadySuppressed) ; 为假 ⇒ 跳去 6194
    /// i=6194 _card.OnSuppressed()                   ; ★ 自己（此前从未派发）
    /// i=6230 Jump 802                               ; 回来跑广播循环
    /// i=802  FetchAllCardsWithEventTrigger(58)      ; 58 = OnOtherCardSuppressed
    /// i=1071 item.OnOtherCardSuppressed(_card)
    /// </code>
    /// 触发号 58 的依据：`ERegisteredCardFunction.h` 逐项数下来第 59 项 = `OnOtherCardSuppressed`。
    /// </summary>
    public void SuppressUnit(CardInstance target)
    {
        if (!IsLocatedOnBoard(target) || target.Keywords.Contains(Keyword.Suppressed))
        {
            return;
        }

        target.Keywords.Add(Keyword.Suppressed);
        _engine.FireSubAction("ZActionSuppressUnit", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
        });

        // ⚠️ **顺序照抄蓝图**：`SuppressMultipleUnits` 里"别人"那一遍在**前**、
        //    被压制的卡自己的 `OnSuppressed()` 在**后**：
        //      si=6074  item.OnAfterOtherCardSuppressed(_card)   ← 广播（触发号 11）
        //      si=6194  _card.OnSuppressed()                      ← 自己
        //      si=802   FetchAllCardsWithEventTrigger(58) → item.OnOtherCardSuppressed(_card)
        //    （触发号 58 那一遍在 si=6230 `Jump 802` 之后，所以排在最后。）
        // ⚠️ 名字以 `OnAfter` 开头、但语义是**广播**（蓝图 si=6074 那一遍遍历的是
        //    `FetchAllCardsWithEventTrigger(11)` 的**全部订阅者**，没有排除自己）。
        //    `FireTrigger` 的广播判据是「程序名以 `OnOther` 开头」，这个名字不满足，
        //    所以必须**同时**用 `otherProgramName` 再发一遍 —— 只传 programName 的话
        //    它只会发给主体，4 张订阅者里除主体外全部收不到。
        FireTrigger("OnAfterOtherCardSuppressed", target, target.Owner,
            otherProgramName: "OnAfterOtherCardSuppressed",
            eventArgs: new object?[] { target },
            eventSubject: target,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["card"] = target,
            });

        FireTrigger("OnSuppressed", target, target.Owner);
        FireTrigger("OnOtherCardSuppressed", target, target.Owner,
            eventArgs: new object?[] { target },
            eventSubject: target,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["card"] = target,
            });
    }

    /// <summary>
    /// 让一张卡成为"老兵"（对应 `BP_CardFunctions::MakeVeteran`，81 条语句）。
    ///
    /// 蓝图控制流（UAssetCLI 的 **StatementIndex**；`out/bp-cardfn.json`）：
    /// <code>
    /// si=2427 JumpIfNot(card.isSuppressed) -> si=2806   ; 被压制 ⇒ 跳过整个广播段
    /// si=2463 FetchAllCardsWithEventTrigger(32)         ; 32 = OnOtherCardBecomingVeteran
    /// si=2736     item.OnOtherCardBecomingVeteran(card)
    /// si=2842     Jump -> si=2463                       ; 循环回边
    /// si=2782 ExecuteOnOtherCardsAbilitiesChanged(card) ; 能力集变了（同一段里，只一次）
    /// si=2806 card.OnBecomingVeteran()                  ; ★ 自己：**压制与否都发**
    /// </code>
    /// 所以本内核的顺序是：广播（仅未被压制）→ 能力变化 → 自己。
    ///
    /// ⚠️ **没读懂的一处（不猜，照实说）**：si=2842 那条回边指向 si=2463（连订阅表都重取），
    /// 而循环自增在 si=2847/2889 —— 两处读数在"什么条件下再跑一遍"上不自洽
    /// （si=2672 的 `PushExecutionFlow(2847)` + si=2805 的 `PopExecutionFlow`
    /// 也可能把流程直接送进出参段）。可观测的部分（广播一次 + 能力变化一次 + 自己一次）
    /// 两种读法一致，所以按这一种实现。
    /// </summary>
    public void MakeVeteran(CardInstance target)
    {
        if (target.Keywords.Add(Keyword.Veteran))
        {
            _engine.FireSubAction("ZActionMakeVeteran", new[]
            {
                ActionValue2.Int("cardID", target.CardId),
            });

            // si=2427：被压制时**不广播** `OnOtherCardBecomingVeteran`（9 张订阅者）。
            if (!target.Keywords.Contains(Keyword.Suppressed))
            {
                FireTrigger("OnOtherCardBecomingVeteran", target, target.Owner,
                    eventArgs: new object?[] { target },
                    eventSubject: target,
                    namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["card"] = target,
                    });
            }

            // si=2782 ExecuteOnOtherCardsAbilitiesChanged(card)
            FireAbilitiesChanged(target);

            FireTrigger("OnBecomingVeteran", target, target.Owner);
        }
    }

    public void CustomAbilityAdd(CardInstance target, string ability, CardInstance? giver)
    {
        target.CustomAbility = ability;
        _engine.FireSubAction("ZActionCustomAbilityAdd", new[]
        {
            ActionValue2.Int("giverID", giver?.CardId ?? 0),
            ActionValue2.Str("ability", ability),
        });

        FireAbilitiesChanged(target);
    }

    /// <summary>
    /// 「这张卡的累积状态被重置了」—— 对应 `BP_CardFunctions::ResetCardInBattle(cardToChange)`。
    ///
    /// 出处：`out/bp-cardfn.json` 函数 `ResetCardInBattle`：
    /// <code>
    /// i=749  cardToChange.ResetCardAttributes()      ; 原生函数，实现体不在客户端
    /// i=782  IsActionProcess()
    /// i=815  cardToChange.OnCardReset()              ; ★ 自己（此前从未派发，64 张订阅）
    /// i=851  FetchAllCardsWithEventTrigger(53)       ; 53 = OnOtherCardReset
    /// i=1120 item.OnOtherCardReset(cardReset, resetCardID)
    /// </code>
    /// 触发号 53 的依据：`ERegisteredCardFunction.h` 逐项数下来第 54 项 = `OnOtherCardReset`。
    /// 签名 `BaseCardObject.h:718/565`。
    ///
    /// ⚠️ **触发点只有两处**（全 dump 搜 `ResetCardInBattle` 只有这两个调用方）：
    /// `MoveCardFromBoardToOwnersHand` i=626 与 `MoveCardToTopOfDeck` i=828 ——
    /// 也就是「**卡从场上回手 / 回牌库**」。
    /// `ResetCardAttributes` 是原生函数、实现体不在客户端 dump 里，
    /// 所以"它还重置了什么"**读不出来**；这里只落实两个可证的触发点，不猜别的时机。
    /// </summary>
    public void ResetCardInBattle(CardInstance card)
    {
        FireTrigger("OnCardReset", card, card.Owner);

        FireTrigger("OnOtherCardReset", card, card.Owner,
            eventArgs: new object?[] { card, card.CardId },
            eventSubject: card,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardReset"] = card,
                ["resetCardID"] = card.CardId,
            });
    }

    public bool HasCustomAbility(CardInstance card, string? ability = null)
        => card.CustomAbility is not null && (ability is null || card.CustomAbility == ability);

    /// <summary>对应 PersistCustomFields —— 游戏用它把卡上的临时状态写进子动作。</summary>
    public void PersistCustomFields(CardInstance card)
    {
        _engine.FireSubAction("ZActionPersistCustomFields", new[]
        {
            ActionValue2.Int("cardID", card.CardId),
            ActionValue2.Str("customJson", string.Join(";", card.CustomJson.Select(kv => $"{kv.Key}={kv.Value}"))),
        });
    }

    // ---- 卡牌私有 JSON（JSON_* 一族，被 19~21 套牌需要）----

    public int JsonGetInt(CardInstance card, string key)
        => card.CustomJson.TryGetValue(key, out string? v) && int.TryParse(v, out int i) ? i : 0;

    public void JsonSetInt(CardInstance card, string key, int value) => card.CustomJson[key] = value.ToString();

    public bool JsonGetBool(CardInstance card, string key)
        => card.CustomJson.TryGetValue(key, out string? v) && v == "1";

    public void JsonSetBool(CardInstance card, string key, bool value) => card.CustomJson[key] = value ? "1" : "0";

    public string JsonGetString(CardInstance card, string key)
        => card.CustomJson.GetValueOrDefault(key, "");

    public void JsonSetString(CardInstance card, string key, string value) => card.CustomJson[key] = value;

    public void JsonClear(CardInstance card) => card.CustomJson.Clear();

    // ---- 卡牌私有 JSON 的数组变体（JSON_GetIntArray / JSON_AddToIntArray）----
    // 存成逗号分隔字符串，既省内存又天然确定性（字典遍历顺序不影响它）。

    private const char JsonListSeparator = ',';

    public List<int> JsonGetIntArray(CardInstance card, string key)
    {
        if (!card.CustomJson.TryGetValue(key, out string? raw) || raw.Length == 0)
        {
            return new List<int>();
        }

        var result = new List<int>();
        foreach (string part in raw.Split(JsonListSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int v))
            {
                result.Add(v);
            }
        }

        return result;
    }

    public void JsonSetIntArray(CardInstance card, string key, IReadOnlyList<int> values)
        => card.CustomJson[key] = string.Join(JsonListSeparator, values);

    public void JsonAddToIntArray(CardInstance card, string key, int value)
    {
        var list = JsonGetIntArray(card, key);
        list.Add(value);
        JsonSetIntArray(card, key, list);
    }

    /// <summary>把 VM 传来的值当作卡牌数组。</summary>
    internal static List<CardInstance> EvalArray(object? receiver, object?[] args)
    {
        if (receiver is List<CardInstance> rl)
        {
            return rl;
        }

        foreach (object? v in args)
        {
            if (v is List<CardInstance> list)
            {
                return list;
            }
        }

        if (receiver is CardInstance single)
        {
            return new List<CardInstance> { single };
        }

        return new List<CardInstance>();
    }

    internal static List<int> AsIntList(object? v) => v switch
    {
        List<int> list => list,
        System.Collections.IEnumerable e and not string => e.Cast<object?>().Select(AsInt).ToList(),
        _ => new List<int>(),
    };

    // ==================================================================
    //  查询层（只读，实现成本低但被调用最频繁）
    // ==================================================================

    public bool IsLocatedOnBoard(CardInstance c) => c.Location.IsBoard() && !c.IsHq;
    public bool IsLocatedInHand(CardInstance c) => c.Location is CardLocation.HandLeft or CardLocation.HandRight;
    public bool IsLocatedInDeck(CardInstance c) => c.Location is CardLocation.DeckLeft or CardLocation.DeckRight;
    public bool IsUnit(CardInstance c) => c.Definition.IsUnit;
    public bool IsInfantry(CardInstance c) => c.Definition.Type == "infantry";
    public bool IsTank(CardInstance c) => c.Definition.Type == "tank";
    public bool IsArtillery(CardInstance c) => c.Definition.Type == "artillery";
    public bool IsAirUnit(CardInstance c) => c.Definition.Type is "fighter" or "bomber";
    public bool IsOrder(CardInstance c) => c.Definition.IsOrder;
    public bool IsLocationCard(CardInstance c) => c.Definition.IsLocationCard;
    public bool IsSameSideUnit(CardInstance a, CardInstance b) => a.Owner == b.Owner && IsUnit(a) && IsUnit(b);

    public bool IsSideActive(Side s) => State.ActiveSide == s;
    public Side GetOppositeSide(Side s) => s.Opposite();
    public int GetTurnNumber() => State.Turn;
    public bool DoesSideControlTheFrontline(Side s) => State.FrontlineOwner == s;

    public CardInstance? GetCardFromID(int cardId) => State.ById(cardId);
    public CardInstance? GetLocationCardBySide(Side s) => State.Hq(s);

    public IEnumerable<CardInstance> GetCardsOnBoardBySide(Side s) => State.Board(s);
    public IEnumerable<CardInstance> GetAllUnitsOnBoard() => State.Board(Side.Left).Concat(State.Board(Side.Right));
    public IEnumerable<CardInstance> GetAllCardsOnBoard() => GetAllUnitsOnBoard().Concat(new[] { State.Hq(Side.Left), State.Hq(Side.Right) });
    public IEnumerable<CardInstance> GetAllCards() => State.AllCards;
    public IEnumerable<CardInstance> GetCardsInHandBySide(Side s) => State.Hand(s);
    public IEnumerable<CardInstance> GetDeckBySide(Side s) => State.Deck(s);

    public int GetTotalAttack(Side s) => State.Board(s).Sum(u => u.Attack);
    public int GetTotalDefense(Side s) => State.Board(s).Sum(u => u.Defense);

    public CardInstance? GetRandomCard(IReadOnlyList<CardInstance> pool)
        => pool.Count == 0 ? null : pool[State.Random.Next(pool.Count)];

    // ==================================================================
    //  未实现统计
    // ==================================================================

    /// <summary>记录一次「内核还没实现」的调用。这是衡量进度最重要的指标。</summary>
    public void NotifyUnimplemented(string name)
    {
        State.UnimplementedCalls[name] = State.UnimplementedCalls.GetValueOrDefault(name) + 1;
    }

    private static CardBuff GetOrCreateBuff(CardInstance card, int sourceCardId)
    {
        if (!card.BuffsBySource.TryGetValue(sourceCardId, out var buff))
        {
            buff = new CardBuff { SourceCardId = sourceCardId };
            card.BuffsBySource[sourceCardId] = buff;
        }

        return buff;
    }
}


