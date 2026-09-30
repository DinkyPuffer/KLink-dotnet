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

    /// <summary>
    /// 广播一个**带出参**的事件，并把每个订阅者写回的出参收集起来。
    ///
    /// 为什么 <see cref="FireTrigger"/> 不够：蓝图里有一族事件是靠出参回传结果的
    /// —— 调用方循环 `FetchAllCardsWithEventTrigger(N)`，对每张订阅卡
    /// `item.OnXxx(..., out value)`，然后**累加/判断**这个 value。
    /// <see cref="FireTrigger"/> 丢掉返回值，这一族就整条断掉。
    ///
    /// 出处（`out/bp-cardfn.json`）：
    /// <code>
    /// ExecuteOnDeploymentTriggered（24 条语句）
    ///   si=61   FetchAllCardsWithEventTrigger(23)
    ///   si=334  item
    ///   si=397  _triggerMultipleDeployment += item.OnDeploymentEffectTriggered_TriggerMultiple
    ///   si=471  out:triggerMultiple = _triggerMultipleDeployment
    ///
    /// CardPlayedFromHand si=3676..4278（事件 14 的取消钩子）
    ///   si=3676 FetchAllCardsWithEventTrigger(14)
    ///   si=4081 PopExecutionFlowIfNot(cancelDeploymentEffect)   ; 假 ⇒ 继续循环
    ///   si=4197 breakFlag = true                                ; 真 ⇒ 跳出
    ///   ★ 这一段**没有** `NotEqual(item, cardPlayed)` 排除主体
    ///     （对比事件 19 在 si=1386 就有）—— 所以订阅者集合**含主体自己**。
    /// </code>
    /// </summary>
    /// <param name="outParamName">
    /// 出参在函数体里的变量名（IR 把 `out:X` 编成普通的 `set dst="X"`）。
    /// 例：`cancelDeploymentEffect` / `TriggerMultiple`。
    /// </param>
    /// <param name="seed">
    /// 事件函数的**入参**。独立事件函数体里读的是裸变量名
    /// （`cardDeploying` / `cardTriggered`），`Frame` 不认识它们，必须显式喂。
    /// </param>
    /// <returns>每个成功执行的订阅者写回的出参值，按派发顺序。</returns>
    public List<object?> BroadcastWithOutParam(
        string programName, CardInstance? subject, Side controller, string outParamName,
        IReadOnlyDictionary<string, object?>? seed = null,
        IReadOnlyList<object?>? eventArgs = null,
        CardInstance? eventSubject = null,
        IReadOnlyDictionary<string, object?>? namedArgs = null,
        Func<CardInstance, IReadOnlyDictionary<string, object?>?>? seedFactory = null)
    {
        var bags = BroadcastWithOutParams(programName, subject, controller, new[] { outParamName },
                                          seed, eventArgs, eventSubject, namedArgs, seedFactory);
        var results = new List<object?>(bags.Count);
        foreach (var (_, outs) in bags)
        {
            results.Add(outs.GetValueOrDefault(outParamName));
        }

        return results;
    }

    /// <summary>一次「带出参的派发」的结果：派发到哪张卡 + 它写回的出参。</summary>
    public readonly record struct OutParamHit(CardInstance Card, IReadOnlyDictionary<string, object?> Outs);

    /// <summary>
    /// <see cref="BroadcastWithOutParam"/> 的多出参版本。
    ///
    /// 需要它是因为 `OnOtherCardDealDamageAddDamage` 有**两个**出参
    /// （`damageToAdd` + `reRunAtEnd`，见 <see cref="ExecuteOnDealDamageAddDamage"/>），
    /// 而单出参版只能取回一个。返回的字典对每个请求过的名字都给键
    /// （函数体没写到的值为 null），免得调用方分不清「没这个出参」和「出参是空」。
    ///
    /// 额外回传**是哪张卡**：蓝图的 `reRunAtEnd` 语义是「把这张**卡**记下来，
    /// 最后整轮再派发一次」（`si=623 Array_Add(addDamageToReRun, item)`），
    /// 光有出参值重建不出这一批卡。
    /// </summary>
    public List<OutParamHit> BroadcastWithOutParams(
        string programName, CardInstance? subject, Side controller, string[] outParamNames,
        IReadOnlyDictionary<string, object?>? seed = null,
        IReadOnlyList<object?>? eventArgs = null,
        CardInstance? eventSubject = null,
        IReadOnlyDictionary<string, object?>? namedArgs = null,
        Func<CardInstance, IReadOnlyDictionary<string, object?>?>? seedFactory = null,
        IReadOnlyList<CardInstance>? only = null)
    {
        var results = new List<OutParamHit>();
        var library = Blueprint.KismetLibrary.Default;
        if (library is null)
        {
            return results;
        }

        // 快照规则与 FireTrigger 一致：棋盘 + 弃牌堆，按嵌套深度分开缓冲。
        // ⚠️ 必须先快照：被派发的程序内部会创建/销毁卡（`receiver` 可能被打死）。
        List<CardInstance> snapshot;
        if (only is not null)
        {
            snapshot = new List<CardInstance>(only);
        }
        else
        {
            snapshot = SnapshotBuffer(_triggerDepth);
            snapshot.Clear();
            for (int i = 0; i < 2; i++)
            {
                Side s = i == 0 ? Side.Left : Side.Right;
                snapshot.AddRange(State.Board(s));
                snapshot.AddRange(State.Discard(s));
            }
        }

        CardInstance? eventCard = eventSubject ?? subject;
        foreach (var card in snapshot)
        {
            if (card.Location == CardLocation.NotAvailable)
            {
                continue;
            }

            var program = library.FindProgram(card.Name, programName);
            if (program is null)
            {
                continue;
            }

            TriggerTrace?.Add($"{programName}(out {string.Join("/", outParamNames)}) → {card.Name}#{card.CardId}" +
                              $"（eventCard={eventCard?.Name ?? "null"}#{eventCard?.CardId}）");

            var ctx = new EffectContext
            {
                Engine = _engine,
                State = State,
                Self = card,
                Target = eventCard,
                Trigger = eventCard,
                Controller = card.Owner,
                EventArgs = eventArgs ?? Array.Empty<object?>(),
                NamedArgs = namedArgs ?? EffectContext.EmptyNamedArgsPublic,
            };

            if (_triggerDepth >= MaxTriggerDepth)
            {
                Vm.UnsupportedOps["<trigger-depth-limit>"] =
                    Vm.UnsupportedOps.GetValueOrDefault("<trigger-depth-limit>") + 1;
                continue;
            }

            _triggerDepth++;
            try
            {
                var perCardSeed = seedFactory?.Invoke(card) ?? seed;
                results.Add(new OutParamHit(card,
                    Vm.RunLocalProgramMulti(program, ctx, perCardSeed, outParamNames)));
            }
            finally
            {
                _triggerDepth--;
            }
        }

        return results;
    }

    /// <summary>
    /// 部署链的第一环：**「别的卡即将部署」钩子，任一订阅者可以取消整条部署效果**。
    ///
    /// 出处 `out/bp-cardfn.json` 的 `CardPlayedFromHand`（si=3640 起、`hasDeployment` 门内）：
    /// <code>
    /// si=3676  FetchAllCardsWithEventTrigger(14)          ; 14 = OnBeforeOtherCardDeploymentTrigger
    /// si=4081  PopExecutionFlowIfNot(cancelDeploymentEffect)   ; 假 ⇒ 继续循环
    /// si=4197  Temp_bool_True_if_break_was_hit_Variable = true ; 真 ⇒ 跳出
    /// si=4283  JumpIfNot 5702 if !cancelDeploymentEffect
    /// si=4297  （取消分支）OnPlayedFromHandExecuted = false
    /// si=4308  NotifySideEffectTrigger(side, 'sideeffect.blockdeployment')
    /// si=4881  Jump 6853                                    ; ★ 取消分支到此返回，
    ///                                                        **不会**跑到 si=6114 的 OnPlayedFromHand
    /// </code>
    /// 控制流可达性用 `out/audit/p1-cfg.py CardPlayedFromHand 4297` 复核过：
    /// 从 si=4297 出发可达 44 条语句，全部终止于 si=6853 `Return`，**不经过 5702/6114**。
    ///
    /// 语义与卡面文本互证（`out/cards-full2.json`）：
    ///   · `card_unit_petlyakov_pe_2ft`「Deployment effects do not trigger.」
    ///     —— 函数体 `cardDeploying.hasDeployment &amp;&amp; self.IsLocatedOnBoard()` ⇒ true
    ///   · `card_event_evasive_action`「… Cancel the effect.」⇒ true
    ///   · `card_event_close_call`「Counter an order or deployment effect…」⇒ true
    ///   · `card_unit_buffs`「Gets +1+1 when it is targeted by an order or deployment effect.」
    ///     —— 两条分支都写 false（它只加 buff，不取消）
    /// </summary>
    /// <returns>被取消了就返回 true。</returns>
    public bool FireDeploymentCancelHook(CardInstance card)
    {
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // 函数体里的裸变量名（见 IR 的 locals 表）：
            //   card_unit_petlyakov_pe_2ft / card_event_evasive_action / card_unit_buffs
            //   都读 `cardDeploying`；`card_event_close_call` 还读 `cardDeploying.currentTarget`。
            ["cardDeploying"] = card,
            ["instigatorID"] = card.CardId,
        };

        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardDeploying"] = card,
        };

        var outs = BroadcastWithOutParam(
            "OnBeforeOtherCardDeploymentTrigger", card, card.Owner, "cancelDeploymentEffect",
            seed: seed, eventArgs: new object?[] { card }, eventSubject: card, namedArgs: named);

        foreach (var v in outs)
        {
            if (Blueprint.KismetVm.Truthy(v))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 部署链的第二环：<c>BP_CardFunctions::ExecuteOnDeploymentTriggered</c>（24 条语句）。
    ///
    /// <code>
    /// si=5    _triggerMultipleDeployment = 0
    /// si=61   FetchAllCardsWithEventTrigger(23)   ; 23 = OnDeploymentEffectTriggered
    /// si=334  item
    /// si=397  _triggerMultipleDeployment += item.OnDeploymentEffectTriggered_TriggerMultiple
    /// si=443  _triggerMultipleDeployment = ...
    /// si=471  out:triggerMultiple = _triggerMultipleDeployment
    /// </code>
    ///
    /// 调用点 `CardPlayedFromHand` si=5702/5750：**只在 `targetCardID == 0` 时调用**
    /// —— 也就是「**非指向性**部署效果」才算。卡面互证：
    /// `card_unit_b_26_marauder`「Your **non-targeting** deployment effects trigger twice.」
    /// 它的函数体正是 `cardTriggered.side == self.side &amp;&amp; self.IsLocatedOnBoard()`
    /// ⇒ `TriggerMultiple = 1`（于是效果跑 `1 + 1 = 2` 次）。
    /// </summary>
    public int SumDeploymentTriggerMultiple(CardInstance card)
    {
        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardTriggered"] = card,
            ["instigatorID"] = card.CardId,
        };

        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardTriggered"] = card,
            ["instigatorID"] = card.CardId,
        };

        var outs = BroadcastWithOutParam(
            "OnDeploymentEffectTriggered", card, card.Owner, "TriggerMultiple",
            seed: seed, eventArgs: new object?[] { card, card.CardId }, eventSubject: card,
            namedArgs: named);

        int total = 0;
        foreach (var v in outs)
        {
            total += AsInt(v);
        }

        return total;
    }

    /// <summary>
    /// 摧毁链的第二环：**「一个摧毁效果触发了」**（事件 24 <c>OnDestructionEffectTriggered</c>）。
    ///
    /// 出处 <c>out/bp-cardfn.json</c>：
    /// <code>
    /// TriggerDestruction（123 条语句）
    ///   si=905  BooleanAND(Not(CustomName1HasAttribute("StopDestructionEffect")), card.hasDestruction)
    ///   si=965  PopExecutionFlowIfNot(...)                       ; 假 ⇒ 整块跳过
    ///   si=1237 localCardUsedForTriggeringDestructionEffect.OnDestroyed(NoObject{}, true)
    ///   si=1286 ExecuteOnDestructionEffectTriggered(card, instigatorID,
    ///               MakeArray(cardID), localDestructionEffectTriggerCards, false, out TriggerMultiple)
    ///   si=1346 if (TriggerMultiple &gt; 0) { si=1390..1645
    ///               loop Temp_int = 1..TriggerMultiple:
    ///                   si=1515 ExecuteOnDestructionEffectTriggered(...) }   ; ★ 再整轮派发
    ///
    /// ExecuteOnDestructionEffectTriggered（27 条语句）
    ///   si=74..171  loop over DestructionEffectTriggerCards（= FetchAllCardsWithEventTrigger(24)）
    ///   si=276      localCardID_inLoop = item.cardID
    ///   si=325/710  if (!skipSuppressCheck &amp;&amp; cardTriggered.isSuppressed) return
    ///   si=398      contains = CardsToDestroy.Contains(localCardID_inLoop)
    ///   si=458      item.OnDestructionEffectTriggered(cardTriggered, instigatorID, contains, out TriggerMultiple)
    ///   si=530      localTriggerMultiple += TriggerMultiple
    ///   si=604      out:TriggerMultiple = localTriggerMultiple
    /// </code>
    ///
    /// 第 3 个入参（bool）在订阅者函数体里叫 <c>SelfAlsoDestroyed</c> —— 也就是
    /// 「这张订阅卡自己是不是也在被摧毁的那批里」。调用点传的是
    /// <c>MakeArray(被摧毁那张卡的 cardID)</c>，所以它 = 「订阅卡就是被摧毁的卡」。
    /// 卡面互证（4 张订阅者全是这个语义）：
    ///   · `card_unit_matsumoto_regiment`「Deal 1 damage to the enemy HQ when a **Destruction
    ///     effect** triggers.」   —— `BooleanOR(SelfAlsoDestroyed, IsLocatedOnBoard(self))`
    ///   · `card_unit_oita_regiment`「Gets +1+1 when a Destruction effect triggers.」—— 同上
    ///   · `card_unit_114th_infantry_regiment`「When a **friendly** Destruction effect triggers,
    ///     it triggers twice.」   —— `cardTriggered.side == side` ⇒ TriggerMultiple = 1
    ///   · `card_event_japan_duty`「Give your units: "Destruction effects on this unit trigger
    ///     an extra time."」      —— `HasCustomAbilityFromCard(能力名, cardTriggered, …)`
    /// </summary>
    /// <param name="card">触发摧毁效果的那张卡（`cardTriggered`）。</param>
    /// <param name="instigator">摧毁者（`instigatorID` 的来源）。</param>
    /// <returns>订阅者累加出来的 TriggerMultiple —— 调用方要再整轮派发这么多次。</returns>
    public int FireDestructionEffectTriggered(CardInstance card, CardInstance? instigator)
    {
        // si=325/710：skipSuppressCheck=false 且 cardTriggered 被压制 ⇒ 整轮不派发。
        if (card.Keywords.Contains(Keyword.Suppressed))
        {
            return 0;
        }

        var named = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["cardTriggered"] = card,
            ["instigatorID"] = instigator?.CardId ?? 0,
        };

        var outs = BroadcastWithOutParam(
            "OnDestructionEffectTriggered", card, card.Owner, "TriggerMultiple",
            eventArgs: new object?[] { card, instigator?.CardId ?? 0, false },
            eventSubject: card,
            namedArgs: named,
            seedFactory: observer => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardTriggered"] = card,
                ["instigatorID"] = instigator?.CardId ?? 0,
                // si=398：CardsToDestroy 就是「被摧毁的那一张」，所以这个 bool 等价于
                // 「订阅卡自己也在被摧毁的那批里」。
                ["SelfAlsoDestroyed"] = observer.CardId == card.CardId,
            });

        int total = 0;
        foreach (var v in outs)
        {
            total += AsInt(v);
        }

        return total;
    }

    /// <summary>
    /// 这张卡被摧毁时，它的**摧毁效果**该不该触发（事件 24 的门）。
    ///
    /// 出处 <c>TriggerDestruction</c> si=905/965：
    /// <c>BooleanAND(Not(CustomName1HasAttribute("StopDestructionEffect")), card.hasDestruction)</c>；
    /// 另有 si=1728 的一条并列分支 <c>HasCustomAbility("destruction")</c>（同一门的另一份实现）。
    ///
    /// ⚠️ **没做** <c>StopDestructionEffect</c> 那一半：它是 `CustomName1` 属性
    /// （`si=3539 CustomName1Add("StopDestructionEffect")`），而 `CustomName1*` 三件套
    /// 内核一个都没进派发表（审计 §6 的 P1#10，~60 张卡）。没有写方就没有读方的意义，
    /// 这里不假装判过 —— 门只取剩下两条。
    /// </summary>
    public bool ShouldTriggerDestructionEffect(CardInstance card)
        => card.Keywords.Contains(Keyword.Destruction) || HasCustomAbility(card, "destruction");

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

    /// <summary>
    /// 跑**这张卡自己的**局部函数（IR 的 `locals`，独立 export 的函数图），取回若干出参。
    ///
    /// 与 <see cref="BroadcastWithOutParams"/> 的区别：那条是「广播给所有订阅者」，
    /// 这条是「只问这一张卡」—— `OnCardDealDamage_ModifyDamageDealt` 在蓝图里就是
    /// `_damageDealerCard.OnCardDealDamage_ModifyDamageDealt(…)`（`si=692` 一次直接调用），
    /// 不是 `FetchAllCardsWithEventTrigger` 那种订阅表。
    ///
    /// 用 `FindLocalProgram` 而不是 `FindProgram`：后者会先查 `entrypoints`，
    /// 命中就合成 ubergraph 的分派前导 —— 对带出参的普通函数是错的
    /// （它们根本不在 ubergraph 里，见 P1 §1）。
    ///
    /// 返回 null = **这张卡没有这个函数**（全卡池 1735 张里只有 33 张有
    /// `OnCardDealDamage_ModifyDamageDealt`）。调用方必须把 null 当「默认体」处理，
    /// 不能拿一个猜的默认值顶替。
    /// </summary>
    private IReadOnlyDictionary<string, object?>? RunOwnLocal(
        CardInstance card, string functionName,
        IReadOnlyDictionary<string, object?>? seed, params string[] outNames)
    {
        var program = Blueprint.KismetLibrary.Default?.FindLocalProgram(card.Definition.Name, functionName);
        if (program is null)
        {
            return null;
        }

        if (_triggerDepth >= MaxTriggerDepth)
        {
            Vm.UnsupportedOps["<trigger-depth-limit>"] =
                Vm.UnsupportedOps.GetValueOrDefault("<trigger-depth-limit>") + 1;
            return null;
        }

        var ctx = new EffectContext
        {
            Engine = _engine,
            State = State,
            Self = card,
            Target = seed is not null && seed.TryGetValue("toCard", out var t) ? t as CardInstance : null,
            Controller = card.Owner,
            NamedArgs = EffectContext.EmptyNamedArgsPublic,
        };

        TriggerTrace?.Add($"{functionName}(out {string.Join("/", outNames)}) → {card.Name}#{card.CardId}" +
                          $"（本卡自己的函数体）");

        _triggerDepth++;
        try
        {
            return Vm.RunLocalProgramMulti(program, ctx, seed, outNames);
        }
        finally
        {
            _triggerDepth--;
        }
    }

    private CardInstance? FindOnBoard(string cardName, CardInstance? prefer)
    {        if (prefer is not null && prefer.IsAlive
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

    /// <summary>
    /// **伤害修正链**：`BP_CardFunctions::ExecuteOnDealDamageAddDamage`（46 条语句，
    /// 带出参 `calculatedDamage`）—— 每一次伤害结算**之前**的唯一修正入口。
    ///
    /// 出处 `out/bp-cardfn.json`（`si` = StatementIndex，`Jump/JumpIfNot` 的 `Offset`
    /// 就是目标 `StatementIndex`，1599/1599 已验证）：
    /// <code>
    /// si=0    PushExecutionFlow(1453)                       ; 返回哨兵
    /// si=5    JumpIfNot(_damageDealerCard.isSuppressed) -> 692
    /// si=41       _dealerCalculatedDamage = damage          ; ★ 被压制：**不调** ModifyDamageDealt
    /// si=68   Array_Clear(addDamageToReRun)
    /// si=109  FetchAllCardsWithEventTrigger(37)             ; 37 = OnOtherCardDealDamageAddDamage
    /// si=382      item.OnOtherCardDealDamageAddDamage(_damageDealerCard, _damageRecieverCard,
    ///                     _dealerCalculatedDamage, _fromAttack, _isDefenderDamage,
    ///                     out damageToAdd, out reRunAtEnd)
    /// si=481/527  _dealerCalculatedDamage += damageToAdd
    /// si=554      PopExecutionFlowIfNot(reRunAtEnd)         ; 假 ⇒ 直接 continue
    /// si=623      addDamageToReRun.Add(item)                ; 真 ⇒ 记下来
    /// si=805   loop2 over addDamageToReRun                  ; 整轮再派发一次（出参 reRunAtEnd 不再读）
    /// si=1053     item.OnOtherCardDealDamageAddDamage(…, out damageToAdd, out reRunAtEnd)
    /// si=1198     _dealerCalculatedDamage += damageToAdd
    /// si=1300 Clamp(_dealerCalculatedDamage, 0, 99)  -> out calculatedDamage
    /// si=692  （dealer **没被压制**才到这里）
    ///         _damageDealerCard.OnCardDealDamage_ModifyDamageDealt(_damageRecieverCard, damage,
    ///                     _fromAttack, fromFight, out newDamage)
    /// si=773      _dealerCalculatedDamage = newDamage
    /// si=800  Jump -> 68                                    ; 再进上面那两轮观察者循环
    /// </code>
    ///
    /// ⚠️ **`si=5` 的极性**（容易读反）：`JumpIfNot` 是「条件为假才跳」，
    /// 所以 **没被压制 ⇒ 跳到 692 调 `ModifyDamageDealt`**；被压制 ⇒ 落到 si=41
    /// 直接 `_dealerCalculatedDamage = damage`。也就是「压制 = 关掉这张卡自己的伤害修正」，
    /// 与 `MatchEngine.Attack` 里那条「压制不禁止攻击、只不触发伤害修正」一致。
    ///
    /// ⚠️ 观察者循环**不**受压制影响：`OnOtherCardDealDamageAddDamage` 是**别人**的
    /// 加成（例如 `card_unit_the_rangers`「友方单位造成伤害时 +1」），压制某一张卡
    /// 不该关掉别人的能力。
    ///
    /// 入参顺序来自资产里的 `FunctionExport.LoadedProperties`（`CPF_Parm` 声明序，
    /// 见 `ref/kards-sim/KardsTranspiler/BlueprintSignatures.cs` 的取法）：
    /// <c>_damageDealerCard, _damageRecieverCard, damage, _fromAttack, fromFight,
    /// _isDefenderDamage, out calculatedDamage</c>。
    /// 调用点互证（同名入参在函数体里就是这些裸变量名）：
    /// <code>
    /// DamageCard si=690          (dealer, card, amount, False, fromFight, False)      ; 效果伤害
    /// MakeCardsFight si=303/488  (a, b, dmg, False, True, False)                     ; 互斗
    /// CalculateDamageDealt si=473 (dealer, recv, dmg, True, False, !dealerIsAttacker)
    /// CalculateDamageDealt si=734 (recv, dealer, dmg, True, False, dealerIsAttacker)
    ///   ⇒ 主伤害 isDefenderDamage=False、反击 isDefenderDamage=True（两次调用同结论）
    /// </code>
    ///
    /// 调用点（`out/bp-cardfn.json`）：`CalculateDamageDealt`(si=473/734/4552)、
    /// `DamageCard`(si=690)、`AttackCard`、`MakeCardsFight`、`ApplyDamageToMultipleCards`。
    ///
    /// ⚠️ **本内核的近似（写清楚）**：蓝图里 `AttackCard` 会调两次 `CalculateDamageDealt`
    /// （si=3807 / si=4004，各算两个方向），于是每个方向会被修正 **两次**。
    /// 33 张 `OnCardDealDamage_ModifyDamageDealt` 的函数体**全是纯函数**
    /// （只用 `IsTank`/`IsAirUnit`/`getTotalAttack` 这类只读谓词，见
    /// `out/audit/p1b-vars.py`），所以重复调用幂等、结果相同。
    /// 内核没有 `CalculateDamageDealt` 这一层，`DealDamage` 是唯一漏斗，
    /// 因此**每次伤害实例只修正一次** —— 对纯函数等价，且天然不会重复计副作用。
    /// </summary>
    /// <param name="dealer">造成伤害的那张卡（`_damageDealerCard`）。可为 null（无来源伤害）。</param>
    /// <param name="receiver">挨打的那张卡（`_damageRecieverCard`）。</param>
    /// <param name="damage">修正前的伤害值。</param>
    /// <param name="fromAttack">是不是攻击结算的伤害（`isCombatDamage`）。</param>
    /// <param name="fromFight">是不是「让两个单位互斗」产生的伤害（`MakeCardsFight` 传 true）。</param>
    /// <param name="isDefenderDamage">是不是**防御方的反击**伤害。</param>
    /// <returns>`Clamp(修正后的伤害, 0, 99)`。</returns>
    public int ExecuteOnDealDamageAddDamage(CardInstance? dealer, CardInstance receiver, int damage,
                                            bool fromAttack, bool fromFight, bool isDefenderDamage)
    {
        int calculated = damage;

        // si=5 / si=692：没被压制 ⇒ 问这张卡自己的修正函数；被压制 ⇒ 原样。
        // 入参喂的是**原始** `damage`（si=692 传的是 `damage`，不是累加值）。
        if (dealer is not null && !dealer.Keywords.Contains(Keyword.Suppressed))
        {
            var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["toCard"] = receiver,
                ["damage"] = damage,
                ["fromAttack"] = fromAttack,
                ["fromFight"] = fromFight,
            };

            var outs = RunOwnLocal(dealer, "OnCardDealDamage_ModifyDamageDealt", seed, "newDamage");
            if (outs is not null)
            {
                calculated = AsInt(outs.GetValueOrDefault("newDamage"));
            }
            // outs == null ⇒ 这张卡没有这个函数（全卡池 1735 张里只有 33 张有），
            // 蓝图里 `BlueprintNativeEvent` 的默认体就是「不修改伤害」。
        }

        // si=109：事件 37 的订阅者。**含主体自己**（这一段没有 `NotEqual(item, dealer)`）。
        var first = BroadcastWithOutParams(
            "OnOtherCardDealDamageAddDamage", dealer, receiver.Owner, new[] { "damageToAdd", "reRunAtEnd" },
            eventArgs: new object?[] { dealer, receiver, calculated, fromAttack, isDefenderDamage },
            eventSubject: dealer,
            namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDealingDamage"] = dealer,
                ["toCard"] = receiver,
                ["damage"] = calculated,
                ["fromAttack"] = fromAttack,
                ["isDefenderDamage"] = isDefenderDamage,
            },
            seedFactory: _ => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cardDealingDamage"] = dealer,
                ["toCard"] = receiver,
                ["damage"] = calculated,
                ["fromAttack"] = fromAttack,
                ["isDefenderDamage"] = isDefenderDamage,
            });

        var reRun = new List<CardInstance>();
        foreach (var (card, outs) in first)
        {
            calculated += AsInt(outs.GetValueOrDefault("damageToAdd"));
            if (Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("reRunAtEnd")))
            {
                // si=623：记的是**卡**（`Array_Add(addDamageToReRun, item)`），不是值。
                reRun.Add(card);
            }
        }

        // si=805..1295：对 reRunAtEnd 的那批再整轮派发一次（第二次不再看 reRunAtEnd）。
        if (reRun.Count > 0)
        {
            var second = BroadcastWithOutParams(
                "OnOtherCardDealDamageAddDamage", dealer, receiver.Owner, new[] { "damageToAdd", "reRunAtEnd" },
                eventArgs: new object?[] { dealer, receiver, calculated, fromAttack, isDefenderDamage },
                eventSubject: dealer,
                namedArgs: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cardDealingDamage"] = dealer,
                    ["toCard"] = receiver,
                    ["damage"] = calculated,
                    ["fromAttack"] = fromAttack,
                    ["isDefenderDamage"] = isDefenderDamage,
                },
                only: reRun);
            foreach (var (_, outs) in second)
            {
                calculated += AsInt(outs.GetValueOrDefault("damageToAdd"));
            }
        }

        // si=1300：Clamp(.., 0, 99)
        return Math.Clamp(calculated, 0, 99);
    }

    /// <summary>「造成伤害。所有伤害都走这里，保证事件顺序一致。」</summary>
    /// <param name="isCombatDamage">
    /// 是不是战斗伤害（攻击结算）。判据来自蓝图：`ExecuteAttackCard` 调
    /// `ExecuteOnCardDealDamageEffects(…, isCombatDamage=True, …)` 两次
    /// （si=3363 防守方受伤、si=3451 攻击方反击受伤），
    /// 而 `ApplyDamageToCard` si=1808 / `ApplyDamageToMultipleCards` si=4933
    /// 都是 `False` —— 也就是"效果伤害"。
    /// </param>
    /// <param name="counterDamage">是不是反击伤害（`ExecuteAttackCard` si=3451 传 True）。</param>
    /// <param name="fromFight">
    /// 是不是「两个单位互斗」（`MakeCardsFight` 传 True）产生的伤害。
    /// 出处 `MakeCardsFight` si=303/488：`ExecuteOnDealDamageAddDamage(a, b, dmg, False, True, False)`。
    /// </param>
    public void DealDamage(CardInstance target, int amount, CardInstance? source,
                           bool isCombatDamage = false, bool counterDamage = false, bool isRedirected = false,
                           bool fromFight = false)
    {
        if (amount <= 0 || !target.IsAlive)
        {
            return;
        }

        // ---- 伤害修正链（P1，2026-09-30）----
        //
        // 位置与蓝图一致：`ExecuteOnDealDamageAddDamage` 在**伤害真正落地之前**跑
        // （`DamageCard` si=690 → si=190 `ExecuteOnDealDamageAddDamageAfterCalc` → si=251
        //  `ApplyDamageToCard`），所以这里放在 `ApplyDamage` 之前，
        // 后面的事件/ZAction 自然带的就是**修正后**的值。
        //
        // ⚠️ `isRedirected` 为 true 时**整条修正链跳过** —— 蓝图 `DamageCard`
        // si=149 `JumpIfNot(isRedirected) -> si=690`：重定向伤害（把伤害原样转给另一张卡）
        // 不再问一遍修正者，否则同一笔伤害会被加两次。
        if (!isRedirected)
        {
            amount = ExecuteOnDealDamageAddDamage(source, target, amount,
                                                  isCombatDamage, fromFight, counterDamage);
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
    /// si=0    IsUnrevealedCovertCard(Card)
    /// si=41   JumpIfNot(...) -> si=730
    ///         ★ JumpIfNot 是「条件为**假**才跳」⇒ **不是**未揭示的隐蔽卡 ⇒ si=730
    ///           `CanBeBuffed = True`（si=741 Jump 773 Return）
    /// si=55..711  switch(Card.location)      ; ★ 这张表**只对未揭示的隐蔽卡**生效
    ///             0(NotAvailable)                      → si=746  False
    ///             1/2(牌库) 3/4(手牌) 9(牌库)          → si=762  True
    ///             5/6(半场/HQ) 7(前线) 8(弃牌堆)        → si=746  False
    /// si=725  （无匹配）Jump si=773 Return   ; out 参数默认值 False
    /// </code>
    ///
    /// ⚠️⚠️ **2026-09-30 修正（旧实现是错的，而且是个地雷）**：
    /// 旧实现把 `si=55..711` 那张 switch **套在了所有卡上**，于是得出「在场单位 → false」。
    /// 那是把 `si=41` 的分支极性读反了（注释里当时还写着「未揭示的隐蔽卡 ⇒ 可以直接 buff」）。
    /// 三条**同资产内语义自明**的校准点把它钉死（都不是猜）：
    /// <code>
    /// ChangeAttack si=57   JumpIfNot(IsValid(card)) -> si=393              ; 卡**无效**才去报错
    /// GiveSalvage  si=141  JumpIfNot(_card.hasSalvage) -> si=254           ; **没有** salvage 才去发
    /// ChangeAttack si=934  JumpIfNot(EqualEqual(getAndDecryptAttack(card), amount)) -> si=960
    ///                                                                       ; 数值**没变**才提前返回
    /// </code>
    /// 反向读法（真时跳）会让 `ChangeAttack` 只在卡无效时继续、`GiveSalvage` 只给已有 salvage 的卡发。
    /// 完整取证见 `klink bot/docs/CanCardBeBuffed矛盾调查.md`（含 `xr-cardfunctions.bpasm` 与
    /// `ref/kards-sim` 的独立解码交叉验证，偏移 730/746/762/773 三方一致）。
    ///
    /// 调用点（同一份 dump，`CanCardBeBuffed` 出现在这些函数的守位）：
    /// `ChangeAttack` si=71/103、`ChangeDefense` si=48/80、`ChangeKreditCost` si=385/417、
    /// `CustomAbilityAdd` si=99/131，以及 `GiveGuard`/`GiveAlpine`/`GiveAmbush`/`GiveBlitz`/
    /// `GiveBond`/`GiveFury`/`GiveShock`/`GiveSmokescreen` 一族各 si=94/126，
    /// 以及 `GiveAlpineBonus` si=5。
    ///
    /// ⚠️ **本内核里这道门恒为 true**：`IsUnrevealedCovertCard` 需要 Covert 的
    /// 「已揭示 / 未揭示」状态机，而内核只做到 `Keyword.Covert` + `getHasCovert` 的**判据面**
    /// （P1 §2），没有揭示状态 ⇒ 恒假 ⇒ 恒走 `si=730` 那一支。
    /// 位置表保留在下面**不是为了留死代码**，而是等 Covert 状态落地时只改
    /// <see cref="IsUnrevealedCovertCard"/> 一处。
    /// </summary>
    public static bool CanCardBeBuffed(CardInstance card)
    {
        // si=41 `JumpIfNot(IsUnrevealedCovertCard(Card)) -> si=730`（730 = CanBeBuffed = True）
        if (!IsUnrevealedCovertCard(card))
        {
            return true;
        }

        return CanUnrevealedCovertBeBuffed(card.Location);
    }

    /// <summary>
    /// `CanCardBeBuffed` 的**位置表本体**（`si=55..711` 那 10 个
    /// `NotEqual_ByteByte(location, N)` + `JumpIfNot`，落点 si=746/762）。
    ///
    /// 单独抽出来有两个理由：
    /// 1. 蓝图的形状就是「先过 `IsUnrevealedCovertCard` 门，再查这张表」；
    /// 2. 本内核拿不到「未揭示的隐蔽卡」，这张表在 `CanCardBeBuffed` 上**不可达** ——
    ///    抽出来才能让自测**逐条**核对它（否则就是一段没人验的死代码）。
    /// </summary>
    public static bool CanUnrevealedCovertBeBuffed(CardLocation location) => location switch
    {
        CardLocation.DeckLeft or CardLocation.DeckRight => true,   // 1 / 2  → si=762
        CardLocation.HandLeft or CardLocation.HandRight => true,   // 3 / 4  → si=762
        CardLocation.Deck => true,                                 // 9      → si=762
        _ => false,                                                // 0 / 5 / 6 / 7 / 8 → si=746
    };

    /// <summary>
    /// `UBaseCardObject::IsUnrevealedCovertCard`（`CanCardBeBuffed` si=0 读的谓词）。
    ///
    /// ⚠️ **恒 false，如实说：内核没有建模「隐蔽卡的已揭示/未揭示」状态**。
    /// P1 §2 只打通了 `Keyword.Covert` 常量 + `getHasCovert` / 成员读 `hasCovert`
    /// 这一层**判据面**（11 张卡），揭示状态机（`IsUnrevealedCovertCard` 的真判据、
    /// 以及揭示时机）整条都还没做。
    ///
    /// 单独抽成函数而不是在门里写 `if (true)`：这样 Covert 状态落地时只改这一处，
    /// 而且调用方（`CanCardBeBuffed`）的形状与蓝图逐字一致。
    /// </summary>
    public static bool IsUnrevealedCovertCard(CardInstance card) => false;

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


