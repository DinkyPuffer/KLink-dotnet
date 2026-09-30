using KLink.Bot.Engine;

namespace KLink.Bot.Effects.Blueprint;

/// <summary>
/// Kismet 字节码解释器 —— 用 IR 直接驱动 <see cref="CardApi"/> 的原语。
///
/// 为什么这么做：手工给 267 张卡写 C# 效果脚本既慢又容易错；
/// 而字节码里的数据流（<c>CallFunc_X_Y</c> 变量名）和它是一一对应的。
/// 解释执行一次到位，覆盖率随原语层的完善自动提升。
///
/// 执行模型：
/// - 从 <see cref="KismetProgram.Entry"/> 对应的语句开始
/// - 顺序执行，遇到 <c>jumpIfNot</c> / <c>return</c> 改变控制流
/// - <c>call</c> 步骤交给 <see cref="CardApi.InvokeByName"/> 派发到原语
/// - 未识别的指令计入统计，不中断（保证一张卡失败不影响整局）
/// </summary>
public sealed class KismetVm
{
    /// <summary>单次执行的步数上限 —— 防止字节码异常导致死循环。</summary>
    public const int MaxStepsPerProgram = 5000;

    /// <summary>
    /// **局部函数**的步数上限（比事件程序高得多）。
    ///
    /// 为什么必须分开：`GetChooseSpawnCards` 是"扫一遍整个卡池再过滤"的形状
    /// （`card_event_pams` 的判据是「英国 + 指令 + 总费&lt;5」，见 `out/gcs/pams.bpasm`
    /// 第 256–457 行），每张卡一轮循环体 ~8 步 × 2021 张卡 ≈ **16000 步**，
    /// 用 <see cref="MaxStepsPerProgram"/> 的 5000 会在第 5000 步被硬截断 ——
    /// 表现是"候选表恒为空、而且**不报任何错**"（`cards` 那一步根本没走到）。
    /// 事件程序仍然用 5000：那条路上从来没有这么长的循环，
    /// 放宽它只会削弱"控制流切片不对"这个死循环哨兵。
    /// </summary>
    public const int MaxStepsPerLocalProgram = 400_000;

    /// <summary>
    /// 诊断用：非 null 时记录每一次原语调用（`函数名(卡名#cardID)`）。
    /// 只在诊断工具里挂，正常运行保持 null。
    /// </summary>
    public List<string>? Trace { get; set; }

    /// <summary>
    /// 诊断用：非 null 时记录**每一步**（`pc i=.. op ..`），以及跳转/弹栈的去向。
    /// 控制流类 bug（执行流栈、跳转解析）只能靠它定位。
    /// </summary>
    public List<string>? StepTrace { get; set; }

    private readonly CardApi _api;

    public KismetVm(CardApi api) => _api = api;

    /// <summary>累计统计（整局）</summary>
    public int ProgramsExecuted { get; private set; }
    public int StepsExecuted { get; private set; }
    public int UnsupportedInstructions { get; private set; }
    public int FaultedPrograms { get; private set; }
    public Dictionary<string, int> UnimplementedCalls { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> UnsupportedOps { get; } = new(StringComparer.Ordinal);

    /// <summary>执行一个事件程序。</summary>
    public void Run(KismetProgram program, EffectContext ctx) => RunCore(program, ctx, null, null);

    /// <summary>
    /// 执行一张卡**自己的局部函数**（例如 <c>GetPlayFromHandDamage</c>），
    /// 并把它写进输出参数 <paramref name="returnVar"/> 的值取回来。
    ///
    /// 为什么要走这条路：<c>GetPlayFromHandDamage</c> **不是**引擎里的通用函数，
    /// 而是每张卡蓝图各自实现的普通函数（编译成独立 export）。
    /// 全卡池 129 张卡定义了它，函数体各不相同 —— 78 张就是
    /// <c>damage = &lt;字面量&gt;</c>，其余是真正的判据（读 HQ 防御、数手牌、循环场面…）。
    /// 所以「在派发表里给这个名字写一个通用实现」是不可能的；
    /// 唯一忠实的做法是**执行那张卡自己的函数体**。
    ///
    /// 语义与客户端一致：函数体只读它自己的入参 + 卡的实例变量，
    /// 这些都由 <paramref name="ctx"/> 和 <paramref name="seed"/> 提供。
    /// </summary>
    /// <param name="seed">入参变量（<c>targetCard</c> 等）—— 覆盖 <see cref="Frame"/> 的默认值。</param>
    /// <param name="returnVar">输出参数名（实测 129 张卡全部叫 <c>damage</c>）。</param>
    public object? RunLocalProgram(KismetProgram program, EffectContext ctx,
                                   IReadOnlyDictionary<string, object?>? seed, string returnVar)
        => RunCore(program, ctx, seed, returnVar, null, MaxStepsPerLocalProgram);

    /// <summary>
    /// 执行一张卡自己的局部函数，并取回**多个**输出参数。
    ///
    /// 需要它是因为 <c>GetChooseSpawnCards(out cards, out markAsSeen, out keepOrder)</c>
    /// 有三个输出参数（<c>BP_CardFunctions.selectCardToDraw</c> 的 <c>L_0680</c> 就是这么调的），
    /// 而 <see cref="RunLocalProgram"/> 只取得回一个。
    /// 返回值就是「输出参数名 → 值」的字典；函数体没写到的名字也会出现（值为 null），
    /// 免得调用方分不清"没这个出参"和"出参是空"。
    /// </summary>
    public IReadOnlyDictionary<string, object?> RunLocalProgramMulti(
        KismetProgram program, EffectContext ctx,
        IReadOnlyDictionary<string, object?>? seed, params string[] returnVars)
        => (IReadOnlyDictionary<string, object?>?)RunCore(program, ctx, seed, null, returnVars,
                                                          MaxStepsPerLocalProgram)
           ?? new Dictionary<string, object?>(StringComparer.Ordinal);

    private object? RunCore(KismetProgram program, EffectContext ctx,
                            IReadOnlyDictionary<string, object?>? seed, string? returnVar,
                            IReadOnlyList<string>? returnVars = null,
                            int maxSteps = MaxStepsPerProgram)
    {
        // 按 StatementIndex 建索引，便于按 target 跳转
        var byIndex = new Dictionary<int, int>(program.Steps.Count);
        for (int i = 0; i < program.Steps.Count; i++)
        {
            byIndex[program.Steps[i].Index] = i;
        }

        if (!byIndex.TryGetValue(program.Entry, out int pc))
        {
            // 入口不在本程序里：可能事件链被拆到别处，跳过
            UnsupportedOps["<missing-entry>"] = UnsupportedOps.GetValueOrDefault("<missing-entry>") + 1;
            return null;
        }

        var frame = new Frame(ctx);
        int steps = 0;
        ProgramsExecuted++;

        // 局部函数的入参（`targetCard` 等）覆盖帧里的默认值
        if (seed is not null)
        {
            foreach (var (name, value) in seed)
            {
                frame.Set(name, value);
            }
        }

        // 取回局部函数的输出参数。`returnVar` 为 null（事件程序）时是恒等的 null。
        object? Capture()
        {
            if (returnVars is not null)
            {
                var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (string name in returnVars)
                {
                    bag[name] = frame.Get(name);
                }

                return bag;
            }

            return returnVar is null ? null : frame.Get(returnVar);
        }

        // Execution Flow 栈：Blueprint 用它做「共享代码块 + 返回」。
        //   pushFlow(addr)  记住 addr，然后**继续往下**执行
        //   popFlow         返回到最近记住的 addr
        //   popFlowIfNot(c) 条件为假时返回；为真则继续
        // 栈空时 pop = 返回 ubergraph 调度器 = 本次事件结束。
        var flowStack = new Stack<int>(4);

        try
        {
            while (pc >= 0 && pc < program.Steps.Count && steps++ < maxSteps)
            {
                var step = program.Steps[pc];
                StepsExecuted++;

                // ⚠️ 必须 `if (StepTrace is not null)` 包起来，不能写 `StepTrace?.Add($"…")` ——
                //    后者会把**字符串插值先算出来**再判空，等于每执行一步就分配一个字符串。
                //    诊断默认关闭，但这条路径是整局最热的地方（实测 ~1800 步/局）。
                if (StepTrace is not null)
                {
                    StepTrace.Add($"pc={pc} i={step.Index} {step.Op} {step.Function ?? step.DestinationVar ?? ""}");
                }

                switch (step.Op)
                {
                    case "call":
                        ExecuteCall(step, frame, ctx);
                        pc++;
                        break;

                    case "math":
                        // 顶层数学节点：结果写入第一个输出槽（若有）
                        frame.SetOutSlot(step, EvalMath(step.Function, step.Args, frame, ctx));
                        pc++;
                        break;

                    case "set":
                        if (!string.IsNullOrEmpty(step.DestinationVar))
                        {
                            var value = Eval(step.Source, frame, ctx);
                            frame.Set(step.DestinationVar, value);

                            // 同上：先判空再插值，别在热路径上白分配字符串
                            if (StepTrace is not null)
                            {
                                StepTrace.Add($"      set {step.DestinationVar} = {value ?? "null"}");
                            }
                        }

                        pc++;
                        break;

                    case "setArray":
                        // MakeArray 节点：把若干值组装成一个数组存进变量
                        if (!string.IsNullOrEmpty(step.DestinationVar))
                        {
                            var items = new List<CardInstance>();
                            foreach (var a in step.Args)
                            {
                                if (Eval(a, frame, ctx) is CardInstance c)
                                {
                                    items.Add(c);
                                }
                            }

                            frame.Set(step.DestinationVar, items);
                        }

                        pc++;
                        break;

                    case "jumpIfNot":
                        if (!Truthy(Eval(step.Condition, frame, ctx)))
                        {
                            if (!TryJump(byIndex, step.JumpTarget, ref pc))
                            {
                                return Capture();
                            }
                        }
                        else
                        {
                            pc++;
                        }

                        break;

                    case "jumpIf":
                        if (Truthy(Eval(step.Condition, frame, ctx)))
                        {
                            if (!TryJump(byIndex, step.JumpTarget, ref pc))
                            {
                                return Capture();
                            }
                        }
                        else
                        {
                            pc++;
                        }

                        break;

                    case "jump":
                        if (!TryJump(byIndex, step.JumpTarget, ref pc))
                        {
                            return Capture();
                        }

                        break;

                    case "pushFlow":
                        // 记住返回地址后继续往下（注意：不是跳转）
                        flowStack.Push(step.JumpTarget);
                        pc++;
                        break;

                    case "popFlow":
                        if (!PopAndJump(flowStack, byIndex, ref pc))
                        {
                            return Capture();   // 栈空 = 返回调度器 = 结束
                        }

                        break;

                    case "popFlowIfNot":
                        if (!Truthy(Eval(step.Condition, frame, ctx)))
                        {
                            if (StepTrace is not null)
                            {
                                StepTrace.Add("      popFlowIfNot 条件为假，弹栈");
                            }

                            if (!PopAndJump(flowStack, byIndex, ref pc))
                            {
                                return Capture();
                            }
                        }
                        else
                        {
                            if (StepTrace is not null)
                            {
                                StepTrace.Add($"      popFlowIfNot 条件为真，继续 → pc={pc + 1}（总 {program.Steps.Count}）");
                            }

                            pc++;
                        }

                        break;

                    case "return":
                        return Capture();

                    default:
                        UnsupportedOps[step.UnknownInst ?? step.Op] =
                            UnsupportedOps.GetValueOrDefault(step.UnknownInst ?? step.Op) + 1;
                        UnsupportedInstructions++;
                        pc++;
                        break;
                }
            }

            // 走到这里说明 pc 越界或撞上了步数上限。
            // 撞上限 = 程序里有环，必须记下来。
            if (steps >= maxSteps)
            {
                StepLimitHits++;
                string key = $"<step-limit:{program.Entry}>";
                UnsupportedOps[key] = UnsupportedOps.GetValueOrDefault(key) + 1;
            }

            return Capture();
        }
        catch (Exception ex)
        {
            FaultedPrograms++;
            UnsupportedOps[$"<fault:{ex.GetType().Name}>"] =
                UnsupportedOps.GetValueOrDefault($"<fault:{ex.GetType().Name}>") + 1;
            StepTrace?.Add($"      [!!] 未捕获异常 {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return Capture();
        }
    }

    /// <summary>
    /// 跳转到目标 StatementIndex。
    ///
    /// **结论（实测 14,659 条跳转，100% 命中）**：Kismet 的 <c>CodeOffset</c>
    /// 就是整个函数的 StatementIndex，不是字节偏移。
    /// （`card_event_aans` 里 `jump-&gt;252` 正好是 `Return` 的下标，不是巧合。）
    ///
    /// 之所以能 100% 命中，是因为 IR 现在把**整个 ubergraph 编成一个程序**、
    /// 事件只是不同的入口点。之前按「entry → 第一个 Return」切子集，
    /// 只能覆盖 69% —— 事件链之间共享语句（尤其共享尾段），切片必然丢控制流。
    /// </summary>
    private bool TryJump(Dictionary<int, int> byIndex, int target, ref int pc)
    {
        if (byIndex.TryGetValue(target, out int next))
        {
            pc = next;
            return true;
        }

        // 理论上不该发生；发生了说明 IR 与字节码不同步
        UnresolvedJumps++;
        UnsupportedOps["<jump-unresolved>"] =
            UnsupportedOps.GetValueOrDefault("<jump-unresolved>") + 1;
        return false;
    }

    /// <summary>弹出返回地址并跳过去。栈空返回 false（= 结束本次事件）。</summary>
    private bool PopAndJump(Stack<int> flowStack, Dictionary<int, int> byIndex, ref int pc)
    {
        if (flowStack.Count == 0)
        {
            FlowReturns++;
            return false;
        }

        int target = flowStack.Pop();
        if (!byIndex.TryGetValue(target, out int next))
        {
            UnresolvedJumps++;
            UnsupportedOps["<pop-unresolved>"] =
                UnsupportedOps.GetValueOrDefault("<pop-unresolved>") + 1;
            return false;
        }

        pc = next;
        return true;
    }

    /// <summary>跳转目标无法解析的次数（应恒为 0）。</summary>
    public int UnresolvedJumps { get; private set; }

    /// <summary>通过「流栈为空」正常结束事件的次数（即 popFlow 返回调度器）。</summary>
    public int FlowReturns { get; private set; }

    /// <summary>撞到单程序步数上限的次数 —— 大于 0 说明有程序在打转（控制流切片不对）。</summary>
    public int StepLimitHits { get; private set; }

    private void ExecuteCall(KismetStep step, Frame frame, EffectContext ctx)
    {
        string fn = step.Function ?? "";
        if (fn.Length == 0)
        {
            return;
        }

        // 诊断钩子：把每次原语调用记下来。
        // 为什么要有它：`entrypoints` 表只能说明「IR 里注册了这个事件」，
        // 说明不了「这次事件真的执行到效果那一步了」。执行流栈相关的 bug
        // （见 KismetLibrary.FindProgram 的注释）从这个钩子一眼就能看出来 ——
        // 事件跑完却没出现任何写状态的调用，就是控制流没走到。
        // 默认关闭，只在诊断工具里开，别让热路径付代价。
        if (Trace is not null)
        {
            Trace.Add($"{fn}({ctx.Self?.Name}#{ctx.Self?.CardId})");
        }

        // 求值入参。输出槽位置传 null —— 它们不是输入。
        var outSet = new HashSet<int>(step.OutParams);
        var raw = new object?[step.Args.Count];
        SeedArrayTarget(fn, step.Args, frame);
        for (int i = 0; i < step.Args.Count; i++)
        {
            raw[i] = outSet.Contains(i) ? null : Eval(step.Args[i], frame, ctx);
        }

        // ⚠️ **不要摘 `args[0]` 的 `{self:true}` 占位**。
        //
        // 这里原先照着 CCB-TEAM 那份 Kismet 直译模拟器文档的「坑与复盘 §9
        // （a[0] 是接收者，实参从 a[1] 起）」把 args[0] 摘掉了。**那条约定是错的**，
        // 实测全卡池（`klink bot/tools/analyze-call-shapes.py` 同源的扫描）：
        //
        //   ChangeAttack       args[0]={self:true}, args[1]=cardID, args[2]=数值…
        //   ChangeAttack       args[0]=tempCard,    args[1]=cardID, args[2]=数值…
        //
        // 两种写法的**参数个数完全一样**，`{self:true}` 就是第一个参数
        // （"作用在哪张卡上"，即效果自己的卡），不是接收者占位 ——
        // 接收者已经被生成器放进 `recv`（这里是 `cardFunction`）了。
        // 635 个被调函数里有 71 个出现过 `{self:true}`，其中 30 个两种写法混用，
        // 摘掉会让这些函数的实参整体错位一位：
        //   · `JSON_GetBool(card, buffActive, out, out)` → key 读成 out 槽 → 恒 false
        //     （实测症状：`card_unit_85_pioneer_company` 的 `buffActive` 明明写着 1，
        //       `OnOtherCardPlayedFromHand` 的还原分支却判假、永远不还原费用）
        //   · `GainKreditSlot(card, side)` → side 读成卡对象
        //   · `ChangeOperationCost(card, cardID, -1, 1, …)` → 数值读成 changeType
        //
        // 本文件顶部的签名注释（`ChangeDefense(targetCard, instigatorID, 数值, …)`、
        // `JSON_SetBool(card, key, value, out)`）本来就是**不摘**的约定，两边现在对齐了。
        object?[] args = raw;

        // 接收者：card.IsUnit() 这类调用的主语
        object? receiver = step.Receiver is not null ? Eval(step.Receiver, frame, ctx) : null;

        var result = _api.InvokeByName(fn, receiver, args, ctx, out bool handled);

        // ---- ★ 卡自己的局部函数（P0 第 3 族，2026-09-27）----
        //
        // 蓝图里的「卡内私有函数」（`ApplyBuff` / `RemoveBuff` / `didPlayBritishInfantryLastTurn` /
        // `hasGuardAdjacentUnit` / `Random Card` …）编译成**独立 export**、不在 ubergraph 里，
        // 所以它们既不是引擎原语、也没有 C# 替身 —— 旧实现只会记一笔 Unimplemented、
        // **等于什么都不做**（审计 §5.7：67 个名字属于这一类，覆盖 ~70 张卡）。
        // 现在 IR 带了这些函数体（`locals`；生成器 `LOCAL_FUNCTIONS` 2 → 78 个：
        // 见 `klink bot/tools/gen-kismet-ir.py` 与清单 `out/audit/private-fns-cards-only.txt`），
        // 于是这里补一条兜底：**派发表认不出来 ⇒ 看这张卡自己有没有同名函数体**。
        //
        // ⚠️ 三条必须写清楚的取舍：
        // 1. **派发表优先**，不是 locals 优先。派发表里有 9 个同名手写 C# 替身
        //    （`ApplyTheBuff`/`RemoveTheBuff`/`updateCustomJsonIfNeeded`/`anyOrderPlayedThisTurn`/
        //    `checkAndUpdateBuffOnCard`/`checkAndUpdateBuffOnAllCards`/`getTwoCardsFromPossibleCards`/
        //    `getPossibleCardsFromStaticCards`/`_isBigRedOne`），它们有自测守着（四条光环自测）。
        //    locals 优先会把它们换掉 —— 那要单独验证，本轮不做。
        // 2. **出参名从调用点的 out 槽名反推**：蓝图里 out 槽叫 `CallFunc_<函数名>_<出参名>`
        //    （实测 `CallFunc_HasCustomAbilityFromCard_doesIt` → `doesIt`），
        //    于是把函数名前缀剥掉就是函数体里那个变量名。剥不出来就只跑副作用。
        // 3. **入参不 seed**：dump 里**没有参数名**（`cards.full.json` 的函数项只有
        //    `expr_count` + `bytecode`）。所以函数体只能靠帧默认值（`cardFunction` = `ctx.Self`、
        //    `side`、`cardID`、实例变量）工作 —— 这正好覆盖这一族的主流形态
        //    （实测 `ApplyBuff` 体读的是 `_tmp_card`(自身局部) / `side` / `cardFunction`；
        //     `didPlayBritishInfantryLastTurn` 读 `side` / `cardFunction`）。
        //    **需要真入参的私有函数会拿到 null**，这一点用 `<local-ran:名字>` 计数器留痕，
        //    不假装它对 —— 要是回归数字恶化，就把这条兜底关掉。
        if (!handled && LocalProgramFor(ctx, fn) is { } local)
        {
            var outNames = new List<string>();
            foreach (int p in step.OutParams)
            {
                if (p < step.Args.Count && step.Args[p].Var is { } slot)
                {
                    outNames.Add(slot);
                    string prefix = "CallFunc_" + fn + "_";
                    outNames.Add(slot.StartsWith(prefix, StringComparison.Ordinal)
                        ? slot[prefix.Length..]
                        : slot);
                }
            }

            var bag = outNames.Count > 0
                ? RunLocalProgramMulti(local, ctx, null, outNames.ToArray())
                : RunLocalProgramMulti(local, ctx, null);
            result = outNames.Count > 0 ? bag[outNames[0]] : null;
            handled = true;
            _api.NotifyUnimplemented($"<local-ran:{fn}>");
        }

        if (!handled)
        {
            UnimplementedCalls[fn] = UnimplementedCalls.GetValueOrDefault(fn) + 1;
            _api.NotifyUnimplemented(fn);
            StepTrace?.Add($"      [!] {fn} 未实现");
            return;
        }

        // 诊断开关关闭时不要插值（这是每次原语调用的必经之路）
        if (StepTrace is not null)
        {
            StepTrace.Add($"      {fn} → {result ?? "null"}" +
                          (receiver is CardInstance rc ? $"  recv={rc.Name}#{rc.CardId}@{rc.Location}" : ""));
        }

        // ⚠️ **多输出原语**必须把每个输出槽都写回。
        //
        // 实测全卡池有 31 个函数带多个 out 槽（`GetLocationCardBySide` 263 次、
        // `JSON_GetInt` 138、`JSON_GetBool` 97…）。旧实现只写 `OutParams[0]`：
        //   · `JSON_GetBool(card, key, out value, out found)` 的 `found` 永远是 null
        //     → 卡蓝图里的 `BooleanAND(value, found)` **恒为假**
        //     → 实测 `card_unit_85_pioneer_company` 的还原分支明明 value=True 也进不去
        //       （"打出一张指令后费用不还原"）
        //   · `JSON_GetInt` 的第二个 out 同理
        // 约定：原语返回 `object?[]` 表示"按下标对应各 out 槽"；
        // 返回别的类型仍然只写第一个槽（普通单返回值）。
        if (step.OutParams.Count > 1 && result is object?[] multi)
        {
            for (int i = 0; i < step.OutParams.Count; i++)
            {
                if (step.Args[step.OutParams[i]].Var is not { } slot)
                {
                    continue;
                }

                frame.Set(slot, i < multi.Length ? multi[i] : null);
            }

            StepTrace?.Add($"      写入多输出 {string.Join(", ", step.OutParams.Select((p, i) =>
                $"{step.Args[p].Var}={((i < multi.Length ? multi[i] : null) ?? "null")}"))}");
            return;
        }

        // 把返回值写进第一个输出槽
        if (step.OutParams.Count > 0 && result is not null)
        {
            var slotExpr = step.Args[step.OutParams[0]];
            if (slotExpr.Var is { } slot)
            {
                frame.Set(slot, result);
                if (StepTrace is not null)
                {
                    StepTrace.Add($"      写入 {slot} = {result}");
                }
            }
            else
            {
                StepTrace?.Add($"      [!] {fn} 的输出槽不是变量: {slotExpr}");
            }
        }
        else if (step.OutParams.Count > 0)
        {
            StepTrace?.Add($"      [!] {fn} 返回 null，输出槽未写入");
        }
    }

    // ==================== 表达式求值 ====================

    /// <summary>
    /// 蓝图里「**原地修改成员数组**」的那一族（<c>Array_Add</c> / <c>Array_Clear</c> …）。
    ///
    /// 为什么需要单独处理：UE 的 <c>Array Add</c> 节点把**目标数组按引用**传进去、
    /// 原地追加，返回值只是新元素下标。实测字节码形状（<c>card_event_pams</c> 的
    /// <c>GetChooseSpawnCards</c>，见 <c>out/gcs/pams.bpasm</c> 第 408–420 行）：
    /// <code>
    /// let "CallFunc_Array_Add_ReturnValue" {
    ///     Array_Add { instancevariable "PossibleCards"  localvariable "…_Item" }
    /// }
    /// </code>
    /// 这里的 <c>PossibleCards</c> 是**卡的实例变量**（`instancevariable @path(owner=1)`），
    /// 不是局部变量、也不是事件入参 —— IR 里只以 <c>{"var":"PossibleCards"}</c> 出现，
    /// 而 <see cref="Frame.Get"/> 对它返回 null。
    ///
    /// 后果（改这一处之前）：<c>Array_Add(null, x)</c> 只会造一个临时数组再丢掉，
    /// 累加循环整个失效 —— 全卡池有 30 张卡在用这个形状攒数组
    /// （<c>card_event_anzac_spirit</c> 的 <c>cardsToDamage</c>、
    ///  <c>card_event_atlantic_convoy</c> 的 <c>possibleCards</c> …）。
    ///
    /// 处理办法：在求值这次调用**之前**，如果第一个实参是裸变量名且帧里还没有它，
    /// 就塞一个空数组进去（相当于"这张卡的成员数组的初值"），
    /// 然后由派发表里的原地实现去改它。
    /// </summary>
    private static readonly HashSet<string> InPlaceArrayOps = new(StringComparer.Ordinal)
    {
        "Array_Add", "Array_Clear", "Array_Append", "Array_Insert",
        "Array_Remove", "Array_RemoveItem", "Array_Set",
    };

    private static void SeedArrayTarget(string fn, IReadOnlyList<KismetExpr> argExprs, Frame frame)
    {
        if (!InPlaceArrayOps.Contains(fn) || argExprs.Count == 0)
        {
            return;
        }

        KismetExpr first = argExprs[0];
        if (first.Var is { } name && first.Context is null && frame.Get(name) is null)
        {
            frame.Set(name, new List<CardInstance>());
        }
    }

    private object? Eval(KismetExpr? expr, Frame frame, EffectContext ctx)
    {
        if (expr is null || expr.None)
        {
            return null;
        }

        if (expr.Int is { } i) return i;
        if (expr.Float is { } f) return (int)f;
        if (expr.Bool is { } b) return b;
        if (expr.Str is { } s) return s;
        if (expr.Name is { } n) return n;
        if (expr.Obj is { } o) return o;
        if (expr.Self) return ctx.Self;

        if (expr.Var is { } v)
        {
            // ⚠️ 带 `ctx` 的变量读是**成员访问**，不是读本地槽。
            //    IR 里这种形状极其常见（实测 `cardID` 881 次、`side` 610 次、
            //    `name` 230 次、`faction` 109 次、`location` 50 次…），
            //    它们是 `Context(某张卡) → InstanceVariable xxx` 编译出来的，
            //    生成器把它压成 `{"var":"side","ctx":{"var":"tempCard"}}`。
            //    旧实现直接 `frame.Get(v)`，**把 ctx 整个丢了** —— 于是
            //    `attackerCard.side` 读到的是"当前控制方"、`attackerCard.faction`
    //    读到的是 null。后果是「同阵营判定」永远为真、「阵营判定」永远为假。
            if (expr.Context is not null)
            {
                return GetMember(Eval(expr.Context, frame, ctx), v);
            }

            return frame.Get(v);
        }

        if (expr.Call is { } c)
        {
            // 同上：**不摘** `args[0]` 的 `{self:true}` —— 它是第一个实参。
            SeedArrayTarget(c, expr.Args, frame);
            var callArgs = expr.Args.Select(a => Eval(a, frame, ctx)).ToArray();
            object? recv = expr.Context is not null ? Eval(expr.Context, frame, ctx) : null;
            var r = _api.InvokeByName(c, recv, callArgs, ctx, out bool handled);
            if (!handled)
            {
                UnimplementedCalls[c] = UnimplementedCalls.GetValueOrDefault(c) + 1;
                _api.NotifyUnimplemented(c);
            }

            return r;
        }

        if (expr.Math is { } m)
        {
            return EvalMath(m, expr.Args, frame, ctx);
        }

        if (expr.Array.Count > 0)
        {
            return expr.Array.Select(a => Eval(a, frame, ctx)).ToList();
        }

        // 结构体常量（`/Script/GameplayTags.GameplayTag`）。
        // ⚠️ 必须放在 `Array` **之后**：生成器把结构体成员摊平进了 `array`，
        //    先判 Array 才能拿到标签名。这里返回结构体名而不是 null，
        //    是为了让下游至少能看出「这是个结构体」而不是「什么都没有」。
        if (expr.Struct is { } structName)
        {
            return structName;
        }

        if (expr.Unknown is { } u)
        {
            UnsupportedOps[u] = UnsupportedOps.GetValueOrDefault(u) + 1;
            UnsupportedInstructions++;
            return null;
        }

        return null;
    }

    private object? EvalMath(string? fn, IReadOnlyList<KismetExpr> argExprs, Frame frame, EffectContext ctx)
    {
        var a = argExprs.Count > 0 ? Eval(argExprs[0], frame, ctx) : null;
        var b = argExprs.Count > 1 ? Eval(argExprs[1], frame, ctx) : null;

        switch (fn)
        {
            case "Add_IntInt": return ToInt(a) + ToInt(b);
            case "Subtract_IntInt": return ToInt(a) - ToInt(b);
            case "Multiply_IntInt": return ToInt(a) * ToInt(b);
            case "Divide_IntInt": return ToInt(b) == 0 ? 0 : ToInt(a) / ToInt(b);
            case "Percent_IntInt": return ToInt(b) == 0 ? 0 : ToInt(a) % ToInt(b);
            case "Abs_Int": return Math.Abs(ToInt(a));
            case "Min_IntInt": return Math.Min(ToInt(a), ToInt(b));
            case "Max_IntInt": return Math.Max(ToInt(a), ToInt(b));
            case "Greater_IntInt": return ToInt(a) > ToInt(b);
            case "GreaterEqual_IntInt": return ToInt(a) >= ToInt(b);
            case "Less_IntInt": return ToInt(a) < ToInt(b);
            case "LessEqual_IntInt": return ToInt(a) <= ToInt(b);
            case "EqualEqual_IntInt": return ToInt(a) == ToInt(b);
            case "NotEqual_IntInt": return ToInt(a) != ToInt(b);
            case "EqualEqual_StrStr":
            case "EqualEqual_StriStri": return string.Equals(a?.ToString(), b?.ToString(), StringComparison.Ordinal);
            case "NotEqual_StrStr":
            case "NotEqual_StriStri": return !string.Equals(a?.ToString(), b?.ToString(), StringComparison.Ordinal);
            case "EqualEqual_NameName": return string.Equals(a?.ToString(), b?.ToString(), StringComparison.Ordinal);
            case "EqualEqual_ObjectObject": return ReferenceEquals(a, b);
            case "NotEqual_ObjectObject": return !ReferenceEquals(a, b);
            case "EqualEqual_BoolBool": return Truthy(a) == Truthy(b);
            case "NotEqual_BoolBool": return Truthy(a) != Truthy(b);
            case "BooleanAND": return Truthy(a) && Truthy(b);
            case "BooleanOR": return Truthy(a) || Truthy(b);
            case "Not_PreBool": return !Truthy(a);

            // KismetMathLibrary.SelectInt(A, B, bPickA) —— bPickA 为真取 A，否则取 B。
            // ⚠️ 它是**三入参**，而上面只求了 a/b；第三个必须单独求值。
            //
            // 为什么必须有它：`GetPlayFromHandDamage` 的 129 张卡里有 5 处用它，
            // 其中就有英联邦（`SelectInt(20, 0, 己方HQ防御 >= 30)`，
            // 见 out/gpfd/commonwealth.bpasm `.export 4`）。
            // 缺了它 → 输出槽写不进值 → `damage` 保持 null → 伤害恒为 0。
            case "SelectInt":
                return Truthy(argExprs.Count > 2 ? Eval(argExprs[2], frame, ctx) : null)
                    ? ToInt(a)
                    : ToInt(b);
            case "IsValid": return a is CardInstance;
            case "Array_Length": return a is System.Collections.ICollection col ? col.Count : 0;
            case "Conv_IntToString": return ToInt(a).ToString();
            case "Conv_IntToText": return ToInt(a).ToString();
            case "Conv_ByteToText": return ToInt(a).ToString();
            case "Conv_TextToString": return a?.ToString() ?? "";
            case "Conv_StringToText": return a?.ToString() ?? "";
            case "Conv_IntToBool": return ToInt(a) != 0;
            case "Conv_BoolToInt": return Truthy(a) ? 1 : 0;
            case "Conv_IntToByte": return ToInt(a);
            case "Conv_ByteToInt": return ToInt(a);
            case "EqualEqual_ByteByte": return ToInt(a) == ToInt(b);
            case "NotEqual_ByteByte": return ToInt(a) != ToInt(b);

            // ---- /Script/kards.FunctionLibrary 的「枚举比较」族 ----
            //
            // 签名：`EnumCompareXxx(A, B, out EnumAreTheyEqual Branches)`
            // 枚举值（从 kards1.60_No_UE4SS.jmap 的 `/Script/kards.EnumAreTheyEqual` 读出）：
            //     Equal = 0, NotEqual = 1
            //
            // ⚠️ 调用方一律写成 `CmpSuccess = NotEqual_ByteByte(Branches, 0)` 再
            //    `JumpIfNot(CmpSuccess, <效果>)` —— 也就是「**相等**才执行效果」。
            //    **不要**在这里返回 bool：返回 bool 时 `NotEqual_ByteByte(true, 0)`
            //    会变成 1，判据整个反过来（"阵营相符才触发"变成"阵营不符才触发"）。
            //
            // 旧实现把 EnumCompareFaction 当字符串比较、EnumCompareSide 当 int 比较，
            // 都是在返回 bool，语义是错的。
            case "EnumCompareFaction":
            case "EnumCompareSide":
            case "EnumCompareCardType":
            case "EnumCompareCardLocation":
                return ToInt(a) == ToInt(b) ? 0 : 1;
            case "Concat_StrStr": return string.Concat(a?.ToString(), b?.ToString());
            case "Conv_NameToString": return a?.ToString() ?? "";
            case "GetEnumeratorUserFriendlyName": return a?.ToString() ?? "";
            default:
                UnimplementedCalls[$"math:{fn}"] = UnimplementedCalls.GetValueOrDefault($"math:{fn}") + 1;
                _api.NotifyUnimplemented($"math:{fn}");
                return null;
        }
    }

    internal static int ToInt(object? v) => v switch
    {
        null => 0,
        int i => i,
        bool b => b ? 1 : 0,
        string s when int.TryParse(s, out int r) => r,
        _ => 0,
    };

    /// <summary>
    /// 读一个**成员变量**（`某张卡.side` / `某张卡.faction` / `某张卡.cardID` …）。
    ///
    /// 成员名取自 IR 里实际出现的那些（见 <see cref="Eval"/> 的注释）。
    /// 认不出来就返回 null —— VM 对 null 是容错的（比较为假），
    /// 但**不要**在这里做任何"猜"的兜底：读错一个成员会让判据静默反向。
    /// </summary>
    internal static object? GetMember(object? target, string member)
    {
        if (target is not CardInstance card)
        {
            return null;
        }

        return member switch
        {
            "side" => (int)card.Owner,
            "faction" => card.Definition.FactionId,
            "name" => card.Name,
            "cardID" => card.CardId,
            "location" => (int)card.Location,
            "locationNumber" => card.LocationNumber,
            "attack" => card.Attack,
            "defense" => card.Defense,
            "maxDefense" => card.MaxDefense,
            "enterPlayOnTurn" => card.EnteredPlayOnTurn,
            // 「三选一」卡把自己选的分支存在卡的 `ChooseOne` 成员上再回读
            // （kardsim 注释里点名的例子：card_event_strategic_focus 用
            //  `Switch(GetMember(self,"ChooseOne"), [(0,IsGroundUnit),(1,IsAirUnit)])`）。
            "ChooseOne" => card.ChooseOne,
            "chooseOne" => card.ChooseOne,
            "kredits" => card.KreditCost,
            "operationCost" => card.OperationCost,
            "isGoldCard" => card.IsGold,
            // ⚠️ 这里**不列** `hasCovert` / `covert`：本内核的 `Keyword` 里没有 Covert
            //    （关键字集只覆盖了 CDO 的 has* 字段里已建模的那些）。
            //    宁可不认（返回 null → 判据为假），也不要拿一个近似的关键字顶替。
            "hasGuard" => card.Keywords.Contains(Keyword.Guard),
            "hasBlitz" => card.Keywords.Contains(Keyword.Blitz),
            "hasAmbush" => card.Keywords.Contains(Keyword.Ambush),
            "hasFury" => card.Keywords.Contains(Keyword.Fury),
            "hasSmokescreen" => card.Keywords.Contains(Keyword.Smokescreen),
            "hasHeavyArmor" => card.Keywords.Contains(Keyword.HeavyArmor),
            "pinned" => card.Keywords.Contains(Keyword.Pinned),

            // ---- 「值就是自己的名字」的实例变量（JSON 键名）----
            //
            // 这**不是猜**：全卡池 1636 张卡里，被当作 `JSON_Get*/JSON_Set*` 的
            // **第 2 个实参**（也就是键名）用的裸实例变量只有这三个。
            // 客户端在卡蓝图里声明了一个 FName 实例变量、默认值就是键名本身，
            // 调用写成 `JSON_GetBool(card, self.buffActive, out value, out found)`。
            // 实测出处（tools 里扫出来的）：
            //   buffActive       —— `card_unit_85_pioneer_company` 的"光环现在生效中"标记
            //   friendlyAttacked —— 友军攻击过标记
            //   A6M2Effect       —— 零式特效标记
            // 不认它们的话键名读成空串，`JSON_GetBool` 恒返回 false ——
            // 表现成"光环的 buffActive 明明写进去了、还原分支却永远判假"。
            "buffActive" => "buffActive",
            "friendlyAttacked" => "friendlyAttacked",
            "A6M2Effect" => "A6M2Effect",

            _ => null,
        };
    }

    internal static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        string s => s.Length > 0,
        CardInstance => true,
        _ => true,
    };

    /// <summary>
    /// 当前正在跑的那张卡有没有叫这个名字的**卡内私有函数**（IR 的 `locals`）。
    ///
    /// 只在派发表认不出来时用（见 <c>ExecuteCall</c> 里的兜底那一段）。
    /// 主体取 <see cref="EffectContext.Self"/> —— 蓝图里 `LocalVirtualFunction` 的
    /// `cardFunction` 就是它。
    /// </summary>
    private static KismetProgram? LocalProgramFor(EffectContext ctx, string functionName)
    {
        if (ctx.Self is not { } self || functionName.Length == 0)
        {
            return null;
        }

        return KismetLibrary.Default?.FindLocalProgram(self.Definition.Name, functionName);
    }

    // ==================== 变量帧 ====================

    /// <summary>
    /// 一次程序执行的变量帧。
    /// 实例变量（side / cardID / targetCardID 等）来自效果上下文；
    /// 其余（tempCard、CallFunc_X_Y 等）是本次执行的局部槽。
    /// </summary>
    private sealed class Frame
    {
        private readonly Dictionary<string, object?> _locals = new(StringComparer.Ordinal);
        private readonly EffectContext _ctx;

        public Frame(EffectContext ctx)
        {
            _ctx = ctx;
            _locals["self"] = ctx.Self;
            _locals["cardFunction"] = ctx.Self;
            _locals["cardID"] = ctx.Self?.CardId ?? 0;
            _locals["targetCardID"] = ctx.Target?.CardId ?? 0;
            _locals["targetCard"] = ctx.Target;
            _locals["side"] = (int)ctx.Controller;
            _locals["mySide"] = (int)ctx.Controller;
            _locals["tempCard"] = ctx.Target;
            _locals["instigatorID"] = ctx.Self?.CardId ?? 0;
            _locals["triggerCardID"] = ctx.Trigger?.CardId ?? 0;
        }

        public object? Get(string name)
        {
            if (_locals.TryGetValue(name, out var v))
            {
                return v;
            }

            if (name.StartsWith("K2Node_Event_", StringComparison.Ordinal))
            {
                return ResolveEventVar(name);
            }

            // 既不是本地槽、也不是事件入参 —— 按「效果自己那张卡的实例变量」读。
            // 实测需要的有 `enterPlayOnTurn`（`card_event_committed_crew` 用它判
            // 「这张牌是不是本回合打出的」）、`faction`、`attack` 等；
            // 见 KismetVm.GetMember 的成员表。
            return GetMember(_ctx.Self, name);
        }

        /// <summary>
        /// 兜底解析 `K2Node_Event_xxx` —— **事件入参变量**。
        ///
        /// 为什么需要它：卡蓝图的触发程序读的是事件入参，编译成变量
        /// `K2Node_Event_cardPlayed` / `_drawnCardID` / `_goingToLocation` 等
        /// （全卡池实测 114 个不同名字，最常用的 `K2Node_Event_targetCard` 出现 774 次）。
        /// 官方字节码里这些变量在**事件 stub** 里被赋值，而 IR 生成器只编
        /// `ExecuteUbergraph_*`，赋值那一步被丢掉了 —— 于是 IR 里读它们永远是 null。
        /// 实测症状（`card_unit_85_pioneer_company` 的还原分支）：
        /// `IsOrder(K2Node_Event_cardPlayed)` 恒为假 → 打出一张指令后光环不还原。
        ///
        /// 这里按**名字**把它们映射到 <see cref="EffectContext"/> 上：
        /// 这是唯一可行的办法 —— IR 里没有「哪个变量是第几个入参」的元数据，
        /// 但变量名本身就带着语义（`…CardID` 是 ID、`…Card` 是卡、`…Side` 是阵营）。
        /// </summary>
        private object? ResolveEventVar(string name)
        {
            const string prefix = "K2Node_Event_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }

            string bare = name[prefix.Length..];

            // 去掉 `_1` / `_2` 这类「第几个同名入参」的后缀（实测 K2Node_Event_cardPlayed_1）
            int underscore = bare.LastIndexOf('_');
            int argIndex = -1;
            if (underscore > 0 && int.TryParse(bare[(underscore + 1)..], out int n))
            {
                argIndex = n;
                bare = bare[..underscore];
            }

            var subject = _ctx.Trigger ?? _ctx.Target;

            // ⚠️ `instigatorID` 必须**排在那条 `…ID ⇒ 事件主体` 的分支前面**。
            //
            // 它虽然以 "ID" 结尾，但语义不是"事件参数里那张卡"，而是
            // 「**谁发起的这件事**」= 正在跑这张程序的那张卡（`ctx.Self`）。
            // 事件契约（`docs/event-contracts.json`）里 `OnHandTargetSelected` 的
            // slots 是 `[Int handTargetCardID, Int instigatorID]` —— 前者是"被选中的卡"
            // （= 事件主体），后者是"挑牌的那张卡"（= 程序自己的卡），两者**必然不同**。
            //
            // 走错分支的后果是**静默失效**（不报错、不记缺口）：
            // `card_event_pams.OnHandTargetSelected` 的第一句就是
            // `if (K2Node_Event_instigatorID == cardID)`（IR i=906 → i=944 的
            // `popFlowIfNot`）。读成"新生成那张卡的 ID"之后判据恒假，
            // 整段效果被弹栈跳过 —— 表现成"develop 出来的牌既不进牌库、费用也不归零"。
            // 全卡池有 7 张卡读这个变量（pams / yank_the_army_weekly /
            // 312th_novgorod / 51st_rifle_brigade / bergmann_battalion /
            // sd_kfz_10_38 / winter_regiment），全是这一族。
            if (bare is "instigatorID" or "instigatorId")
            {
                return _ctx.EventArg(argIndex) ?? _ctx.Self?.CardId ?? 0;
            }

            // ⚠️ 具名载荷**优先于**位置推断，而且必须排在下面那条 `…ID ⇒ 事件主体` 之前。
            //
            // 理由：事件桩里的槽位名**不是**入参下标。实测
            // `OnOtherCardDrawnFromDeck(int32 drawnCardID, bool StartOfTurnDraw, ESideEnum drawnSide)`
            // 的槽位是 `K2Node_Event_drawnSide_1`（**下标 2** 的入参却带 `_1` 后缀），
            // 而 `OnCardDrawnFromDeck(bool StartOfTurnDraw, ESideEnum drawnSide)`
            // 的槽位也是 `K2Node_Event_drawnSide_1`（下标 1）。
            // 同一个名字在两个事件里指向不同下标 ⇒ 只靠 `_N` 推必然有一个读错。
            // 派发方（`CardApi.FireTrigger` 的 `namedArgs`）按名字给值就没有这个问题。
            if (_ctx.NamedArgs.Count > 0 && _ctx.NamedArgs.TryGetValue(bare, out var named))
            {
                return named;
            }

            if (bare.EndsWith("CardID", StringComparison.Ordinal)
                || bare.EndsWith("CardId", StringComparison.Ordinal)
                || bare.EndsWith("ID", StringComparison.Ordinal))
            {
                return subject?.CardId ?? _ctx.Self?.CardId ?? 0;
            }

            switch (bare)
            {
                case "turnnumber":
                    // 事件自带"第几回合"这个入参时优先用它（派发方按事件语义放进 EventArgs）；
                    // 拿不到才退回当前回合数。`OnEndOfTurn` 是在 `State.Turn++` **之前**发的，
                    // 两者本来一致；用入参是为了将来支持"补发上一回合事件"这类场景。
                    return _ctx.EventArg(argIndex) ?? _ctx.State.Turn;
                case "goingToLocation":
                    return _ctx.GoingToLocation is { } loc ? (int)loc : _ctx.EventArg(argIndex);
                // `OnCardLocationMoved` / `OnOtherCardLocationMoved` 同时带**新旧两个**位置，
                // 一个 `GoingToLocation` 装不下 —— 用 ctx 上专门的字段。
                // 槽位名在同一个事件里两种写法都有（`oldLocation` 与 `oldLocation_1`），
                // 去掉后缀后同名，所以两条都指同一个值（出处见 EffectContext.OldLocation）。
                case "oldLocation":
                    return _ctx.OldLocation is { } oldLoc ? (int)oldLoc : _ctx.EventArg(argIndex);
                case "newLocation":
                    return _ctx.NewLocation is { } newLoc ? (int)newLoc : _ctx.EventArg(argIndex);
                case "drawnSide":
                case "spawnedSide":
                case "side":
                case "sideGaining":
                case "isNegativeGain":
                    // 阵营类入参：派发方按事件语义放进 EventArgs；
                    // 拿不到就退回「事件主体所属阵营」。
                    return _ctx.EventArg(argIndex) ?? (int?)(subject?.Owner ?? _ctx.Controller);                case "method":
                case "StartOfTurnDraw":
                    return _ctx.EventArg(argIndex);
            }

            // 其余一律当「事件里的那张卡」（`cardPlayed` / `cardLeaving` / `card` /
            // `targetCard` / `cardDestroyed` / `cardReset` …）—— 这是 114 个名字里绝大多数。
            //
            // `subject` 是**事件参数**（`FireTrigger` 的 `eventSubject`），不是被派发到的卡：
            // 例如「别的卡被打出」时，被派发到的是光环、事件参数才是被打出的那张牌。
            // 混用会让光环读到自己的 cardID（实测症状：`IsOrder(光环)` 恒假，
            // 于是 85 先驱连永远不还原费用）。
            var eventSubject = _ctx.Trigger ?? _ctx.Target;
            return eventSubject ?? _ctx.Self;
        }

        public void Set(string name, object? value)
        {
            if (name.Length > 0)
            {
                _locals[name] = value;
            }
        }

        public void SetOutSlot(KismetStep step, object? value)
        {
            if (step.OutParams.Count == 0)
            {
                return;
            }

            if (step.Args[step.OutParams[0]].Var is { } slot)
            {
                Set(slot, value);
            }
        }
    }
}

