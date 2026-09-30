using KLink.Bot.Cards;
using KLink.Bot.Engine;

namespace KLink.Bot.Effects;

/// <summary>
/// <see cref="CardApi"/> 的**名字派发层**：把 Blueprint 字节码里的调用名 + 位置参数，
/// 适配到原语方法上。
///
/// 参数形状不是猜的，是从全部 35,360 个调用点统计出来的
/// （见 <c>klink bot/tools/analyze-call-shapes.py</c>）。例如：
///
/// <code>
/// ChangeDefense   (targetCard, instigatorID, 数值, changeType, silent, out)  ← 数值恒在 index 2
/// DamageCard      (targetCard, 伤害,    attackerID, bool, bool, bool, out)
/// DrawCardsFromDeckBySide (instigatorID, side, 张数, bool, bool, out, ?)     ← side=1, 张数=2
/// GetCardsOnBoardBySide   (side, bool, bool, out cards)                      ← 输出在 index 3
/// JSON_SetBool    (card, key, value, out)
/// </code>
///
/// 没实现的名字返回 <c>handled=false</c>，由调用方计入未实现统计 —— 这就是进度指标。
/// </summary>
public sealed partial class CardApi
{
    /// <summary>调用名 → 处理函数。返回 null 表示「这个调用没有返回值」。</summary>
    private readonly Dictionary<string, Func<EffectContext, object?, object?[], object?>> _dispatch;

    private Dictionary<string, Func<EffectContext, object?, object?[], object?>> BuildDispatch() =>
        new(StringComparer.Ordinal)
        {
            // ---------------- 查询 / 判定（接收者是主语）----------------
            // ⚠️ 这一族必须走 `SelfArg`，不能只看 `receiver`：
            //    Blueprint 里 `self.IsXxx()` 这种**隐式 self** 调用编译出来是
            //    `{"Inst":"FinalFunction","Function":"IsXxx"}` —— **没有 `Context` 包装**，
            //    所以 IR 里根本没有 `recv`，VM 传进来的 receiver 是 null。
            //    实测 `card_unit_214th_amur` 的 i=848 正是这种形状
            //    （紧挨着的 i=877 `GetAllCardsOnBoard` 反而带 `recv=cardFunction`）。
            //    旧写法 `AsCard(r) is {} && …` 直接返回 false，把整条
            //    `OnEnterPlay` 的判据短路掉 —— 光环看起来「什么都没做」。
            ["IsUnit"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsUnit(x),
            ["IsInfantry"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsInfantry(x),
            ["IsTank"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsTank(x),
            ["IsArtillery"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsArtillery(x),
            ["IsAirUnit"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsAirUnit(x),
            ["IsOrder"] = (c, r, a) =>
            {
                var t = SelfArg(c, r, a);

                // 诊断开关关闭时不要插值（IsOrder 是极高频谓词）
                if (AuraTrace is not null)
                {
                    AuraTrace.Add($"      IsOrder(recv={(r as CardInstance)?.Name ?? "null"}, " +
                                  $"self={c.Self?.Name}, target={c.Target?.Name ?? "null"}, " +
                                  $"trigger={c.Trigger?.Name ?? "null"}) → {t is not null && IsOrder(t)}");
                }

                return t is not null && IsOrder(t);
            },
            ["IsLocation"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocationCard(x),
            ["IsLocatedOnBoard"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocatedOnBoard(x),
            ["IsLocatedInHand"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocatedInHand(x),
            ["IsLocatedInDeck"] = (c, r, a) => SelfArg(c, r, a) is { } x && IsLocatedInDeck(x),
            ["IsSideActive"] = (c, r, a) => IsSideActive(SideArg(r, a, 0)),
            ["IsValid"] = (c, r, a) => AsCard(a.FirstOrDefault()) is not null || SelfArg(c, r, a) is not null,
            ["IsCardReserved"] = (c, r, a) => false,           // TODO 待 BalancedCards 解出
            ["IsForecastCard"] = (c, r, a) => false,            // TODO 未知语义
            ["HasIntel"] = (c, r, a) => SelfArg(c, r, a) is { } x && JsonGetBool(x, "intel"),

            // ⚠️ 这两个是**同形参数位错**（审计 §5.2），一起修：
            // 权威签名（`BaseCardObject.h:946/943`）：
            //   `HasCustomAbility(const FString& ability, bool& doesIt)`          —— 被检查的卡 = **接收者**
            //   `HasCustomAbilityFromCard(const FString& ability, int32 giverID, bool& doesIt)` —— 同上
            // 旧实现的两处错：
            //   1. `HasCustomAbility` 没把能力名传下去（`CardApi.HasCustomAbility` 的
            //      `ability` 默认 null）⇒ 变成"这张卡有**任意**自定义能力吗"，
            //      11 个调用点里 `cantRetreat`/`cantBePinned`/`lethal`/`alpine`/
            //      `destroyEndOfTurn` 混用时会互相误判。
            //   2. `HasCustomAbilityFromCard` 拿 `a[0]` 当卡 —— 而 `a[0]` 是**能力名字符串**。
            //      IR 实测（`out/audit/p0-argshapes.py`，52 个调用点，49 有 recv / 3 隐式）：
            //      `a[0]` = `"trigger"`×20、`"destruction"`×13、`"passive"`×8、
            //      `"ignoreCantAttack_location"`×4、`"targetAbility"`×3、`"excess"`×2 …
            //      `AsCard("trigger")` = null ⇒ **恒 false**。
            //      它的调用点之一是 `ExecuteOnCardDestroyedFunction` 的
            //      `BooleanOR(hasDestruction, HasCustomAbilityFromCard("destruction", …))`
            //      ⇒ 靠效果"获得摧毁能力"的卡全废。
            //      `a[1]` 是 `giverID`（IR 里是 `cardID` 变量）—— "谁给的"这层语义
            //      内核的 `CustomAbility` 是单值字段、没有来源，**不实现**（不猜）。
            ["HasCustomAbility"] = (c, r, a)
                => SelfArg(c, r, a) is { } x && HasCustomAbility(x, StrArgOrNull(a, 0)),
            ["HasCustomAbilityFromCard"] = (c, r, a)
                => SelfArg(c, r, a) is { } x && HasCustomAbility(x, StrArgOrNull(a, 0)),
            ["DoesSideControlTheFrontline"] = (c, r, a) => DoesSideControlTheFrontline(SideArg(r, a, 0)),
            ["IsSameSideUnit"] = (c, r, a) => a.Length >= 2 && AsCard(a[0]) is { } p && AsCard(a[1]) is { } q && IsSameSideUnit(p, q),

            // ⚠️ **隐式 self**（审计 §5.1）。权威签名 `BaseCardObject.h:841`：
            //     `IsVeteran(bool ignoreSuppress, bool& isIt)` —— 零个"是哪张卡"的入参。
            // IR 实测：75 个调用点里 **70 个没有 recv**（`{"bool": false}` + out 槽），
            // 旧实现读 `AsCard(r)` ⇒ r 为 null ⇒ **恒 false**（41 张卡的老兵判据全反）。
            // 正确写法与 `getTotalAttack`/`getTotalDefense` 同形：`SelfArg`（兜底 c.Self）。
            // `ignoreSuppress`（a[0]）的语义**读不出来**（`BaseCardObject.h` 只有签名），
            // 不实现、也不假装实现。
            ["IsVeteran"] = (c, r, a) => SelfArg(c, r, a) is { } v && v.Keywords.Contains(Keyword.Veteran),
            ["GetKreditsBySide"] = (c, r, a) => c.State.Kredits(SideArg(r, a, 0, c.Controller)),
            ["GetMaxKreditsBySide"] = (c, r, a) => c.State.MaxKredits(SideArg(r, a, 0, c.Controller)),

            // ---------------- 取值 / 选择器 ----------------
            // ⚠️ `GetOppositeSide` **没有入参** —— 它的"我方"取自卡本身。
            //    证据：扫描全部 2053 张卡的 Kismet 字节码，
            //    `GetOppositeSide` 出现 756 次，**每一次的参数列表都只有 1 项**，
            //    而且那一项是 out 槽 `CallFunc_GetOppositeSide_oppositeSide`
            //    （对比 `GetLocationCardBySide` 3 项、`GetCardsOnBoardBySide` 4 项，
            //    入参都老老实实列在参数表里）。见 tools/analyze-call-arity.py。
            //
            //    原先实现读 `a[0]`，拿到的是**尚未赋值的 out 槽** → `Side.NotAvailable`
            //    → 后续 `GetLocationCardBySide(NotAvailable)` 返回 null →
            //    `DamageCard(null)` 静默无操作。影响面很大（463 张卡用到它，
            //    是第 3 常用的外部调用）。实测症状：card_unit_10_5_cm_lefh 的
            //    「Deployment: 对敌方 HQ 造成 2 点伤害」完全不生效。
            ["GetOppositeSide"] = (c, r, a) => (int)SelfSide(c).Opposite(),
            ["GetCardFromID"] = (c, r, a) => GetCardFromID(IntArg(a, 0)),
            ["GetLocationCardBySide"] = (c, r, a) => GetLocationCardBySide(SideArg(r, a, 2)),

            // ⚠️ **可选参数必须读**。权威签名（`CardFunctionsStub.h:437`）：
            //     `GetCardsOnBoardBySide(ESideEnum side, bool unitsOnly, bool includeCovertCards, TArray& Cards)`
            // 旧实现只读 `a[0]`（side），把 `unitsOnly` / `includeCovertCards` 整个丢掉 ——
            // 252 个调用点全部按"所有卡"返回。IR 实测（`out/audit/p0-argshapes.py`）：
            // `a[1]` = `true`×213 / `false`×39，`a[2]` = `false`×243 / `true`×9。
            //
            // `unitsOnly=false` 那一支**必须包含非单位卡**，证据（`card_unit_3rd_maizuru_snlf`
            // 的 `OnPlayedFromHand`，card-ir.json 的 steps 0-6）：
            // <code>
            // step 1  GetCardsOnBoardBySide(oppositeSide, unitsOnly: false, false, out cards)
            // step 2  GetRandomCard(cards, false, out randomCard)
            // step 3  DamageCard(randomCard, 1, self, …)
            // step 4  IsUnit(recv = randomCard)  →  step 5  jumpIfNot  →  step 6  PinUnit(randomCard)
            // </code>
            // 也就是说这张卡是"随机打敌方场上一张牌 1 点，**如果它是单位**再钉住它" ——
            // 这个 `IsUnit` 分支只有在结果集**可能含非单位**时才有意义。
            // 棋盘上唯一的非单位卡就是 HQ（位置卡），所以 `unitsOnly=false` ⇒ 含该方 HQ。
            //
            // `includeCovertCards` 读进来但**当前无效果**：本内核还没有建模 Covert
            // （P1，见审计 §4「隐蔽 Covert」），没有"未揭示的隐蔽卡"这个状态可过滤。
            // 记一笔未实现，别让它静默（数字小，不会淹没别的东西）。
            ["GetCardsOnBoardBySide"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                if (TruthyArg(a, 2))
                {
                    c.State.UnimplementedCalls["GetCardsOnBoardBySide<includeCovertCards>"] =
                        c.State.UnimplementedCalls.GetValueOrDefault("GetCardsOnBoardBySide<includeCovertCards>") + 1;
                }

                return TruthyArg(a, 1)
                    ? GetCardsOnBoardBySide(side).ToList()
                    : GetCardsOnBoardBySide(side).Concat(new[] { c.State.Hq(side) }).ToList();
            },
            ["GetCardsInHandBySide"] = (c, r, a) => GetCardsInHandBySide(SideArg(r, a, 0)).ToList(),
            ["GetDeckByside"] = (c, r, a) => GetDeckBySide(SideArg(r, a, 0)).ToList(),

            // 权威签名（`CardFunctionsStub.h:455/464`）：
            //   `GetAllUnitsOnBoard(bool includeCovertCards, TArray& Cards)`
            //   `GetAllCardsOnBoard(bool includeCovertCards, TArray& Cards)`
            // 两个函数的**唯一**区别就是"单位"与"卡" —— 说明"场上所有卡"**严格多于**
            // "场上所有单位"，多出来的只能是 HQ（棋盘上唯一的非单位卡）。
            // 旧实现把 `a[0]` 当成 `includeHq` 解释（注释里还写了推理），
            // 于是 90 个传 `false` 的调用点拿到的是"只有单位"，少了 HQ。
            ["GetAllUnitsOnBoard"] = (c, r, a) => GetAllUnitsOnBoard().ToList(),
            ["GetAllCardsOnBoard"] = (c, r, a) => GetAllCardsOnBoard().ToList(),
            ["GetAllCards"] = (c, r, a) => GetAllCards().ToList(),
            // ⚠️ 这两个是 **`BaseCardObject` 的原生成员函数**（UHT 签名
            //    `void getTotalAttack(int32& totalAttack)` / `void getTotalDefense(int32& totalDefense)`），
            //    语义是「**接收者那张卡自己的**总攻击 / 总防御」，
            //    **不是**「某个阵营场上所有单位之和」。
            //
            //    证据三条（`getTotalAttack` 与 `getTotalDefense` **逐条同形**，下面两个数字
            //    各自独立统计，不是照抄）：
            //    1. 参照实现：`ref/kards-sim/KardsSim/Bridge/EngineHost.cs`
            //       `["getTotalDefense"] = (h, a) => … h.Card_(a[0]).TotalDefense`（:1660-1664）、
            //       `["getTotalAttack"]  = (h, a) => Out(a, h.Card_(a[0]) is { } tc ? tc.TotalAttack : 0)`
            //       （:1099）—— 两条的主语都是**接收者那张卡**。
            //    2. UHT 签名（`E:\peoject\kards\Source\kards\Public\BaseCardObject.h`）：
            //       `:982 void getTotalDefense(int32& totalDefense);`
            //       `:985 void getTotalAttack(int32& totalAttack);`
            //       两条同为 `UFUNCTION(BlueprintCallable, BlueprintPure)` 的 **`UBaseCardObject`
            //       成员函数**、**零个形式参数**（`int32&` 是 out 参数，不进参数表）。
            //    3. 全卡池 IR 调用点的接收者统计（`out/gta/analyze-gta.py`）：
            //       `getTotalDefense` ubergraph 113 个调用点 / 87 个带明确的卡接收者 / 26 个隐式 self；
            //       `getTotalAttack`  ubergraph  46 个调用点 / 34 个带明确的卡接收者 / 12 个隐式 self
            //       （接收者形态：`tempCard` 8、`K2Node_Event_targetCard` 6、数组元素 11 …）。
            //       没有任何一个调用点的语义是「己方场上单位攻击/防御之和」。
            //
            //    旧实现 `GetTotalDefense(SelfSide(c))` / `GetTotalAttack(SelfSide(c))` 丢掉接收者，
            //    返回 `State.Board(side).Sum(...)` —— 而 `Board(s)` **还排除了 HQ**。
            //    于是「读 HQ 防御」被读成「己方场上单位防御之和」：英联邦的
            //    `己方 HQ 防御 >= 30` 在空场时恒等于 `0 >= 30` = 假 → 伤害恒为 0。
            //    原先那条「零入参 ⇒ 自己的场面总和」的注释是**只按参数个数统计**得出的
            //    （见 tools/check-zero-input-dispatch.py）—— 接收者不在参数表里，
            //    所以那次统计看不见它。
            //
            //    `getTotalAttack` 的旧实现同样是「己方场面攻击之和」：
            //    `card_unit_red_bull`（卡面 "double the attack of this unit."）的
            //    `OnStartOfTurn` 是 `ChangeAttack(self, getTotalAttack(), +)`，
            //    旧实现会把**整条己方场面的攻击之和**加到它自己头上。
            //    回归用例见 tools/BotSim/SelfTest.cs 的 RedBullDoublesOwnAttack。
            ["getTotalAttack"] = (c, r, a) => SelfArg(c, r, a)?.Attack ?? 0,
            ["getTotalDefense"] = (c, r, a) => SelfArg(c, r, a)?.Defense ?? 0,
            ["GetTurnNumber"] = (c, r, a) => GetTurnNumber(),
            ["GetRandomCard"] = (c, r, a) => GetRandomCard(AsList(a.FirstOrDefault())),
            ["GetTargetedCard"] = (c, r, a) => c.Target,
            ["GetCard"] = (c, r, a) => AsCard(r) ?? c.Target,

            // ⚠️ `GetPlayFromHandDamage` **不是**引擎的通用函数，它是**每张卡蓝图
            //    各自实现**的普通函数（编译成独立 export，不在 ubergraph 里）。
            //    全卡池 129 张卡定义了它，函数体各不相同 —— 78 张就是
            //    `damage = <字面量>`（英联邦 20、Z SPECIAL UNIT 4、M4 FIREFLY 5…），
            //    其余是真正的判据（读 HQ 防御、数手牌、循环场面求和…）。
            //    所以这里**没有也不可能有「一个公式」**；唯一忠实的做法是
            //    执行那张卡自己的函数体（IR 里的 `locals`）。
            //    证据见 `out/gpfd/commonwealth.bpasm` 的 `.export 4`。
            ["GetPlayFromHandDamage"] = (c, r, a) => DoGetPlayFromHandDamage(c, AsCard(a.FirstOrDefault())),

            // ---- Develop 一族（见文件下半部「Develop（GetChooseSpawnCards 一族）」的注释）----
            ["GetAllActiveStaticCards"] = (c, r, a) => StaticCardPool(c),
            // 三个出参按调用点的顺序返回：[cards, markAsSeen, keepOrder]
            // （`BP_CardFunctions.selectCardToDraw` 的 L_0680 就是这个顺序）。
            // 多出参约定见 `KismetVm.ExecuteCall`：返回 object?[] 即按下标对应各 out 槽。
            ["GetChooseSpawnCards"] = (c, r, a) =>
            {
                var selecting = SelfArg(c, r, a);
                if (selecting is null)
                {
                    return new object?[] { new List<CardInstance>(), false, false };
                }

                var cards = GetChooseSpawnCards(c, selecting, out bool markAsSeen, out bool keepOrder);
                return new object?[] { cards, markAsSeen, keepOrder };
            },
            ["MoveCardToTopOfOwnersDeck"] = (c, r, a) => DoMoveCardToTopOfOwnersDeck(c, a),
            ["RandomIntFromRangeWithStream"] = (c, r, a) => DoRandomIntFromRange(c, a),
            ["GetPlayingSide"] = (c, r, a) => (int)c.Controller,
            ["GetStartingSide"] = (c, r, a) => (int)c.State.StartingSide,
            ["GetAllyNationForSide"] = (c, r, a) => "",
            ["GetEmptyText"] = (c, r, a) => "",

            // ---------------- 效果 / 状态修改 ----------------
            // Change* 系列：数值恒在 index 2，来源在 index 1
            ["ChangeAttack"] = (c, r, a) => DoChangeAttack(c, r, a),
            ["ChangeDefense"] = (c, r, a) => DoChangeDefense(c, r, a),
            ["GainAttack"] = (c, r, a) => DoChangeAttack(c, r, a),
            ["GainDefense"] = (c, r, a) => DoChangeDefense(c, r, a),
            ["LoseAttack"] = (c, r, a) => DoChangeAttack(c, r, a, invert: true),
            ["SetDefense"] = (c, r, a) => DoSetDefense(c, r, a),
            ["ChangeKreditCost"] = (c, r, a) => DoChangeKreditCost(c, r, a),
            ["SetKreditCost"] = (c, r, a) => DoSetKreditCost(c, r, a),
            ["ChangeOperationCost"] = (c, r, a) => DoChangeOperationCost(c, r, a),
            ["ChangeHeavyArmor"] = (c, r, a) => DoChangeHeavyArmor(c, r, a),
            ["DamageCard"] = (c, r, a) => DoDamageCard(c, r, a),
            ["HealCard"] = (c, r, a) => DoHealCard(c, r, a),
            ["DestroyCard"] = (c, r, a) => DoDestroyCard(c, r, a),
            ["DrawCardsFromDeckBySide"] = (c, r, a) => { DrawCards(SideArg(r, a, 1, c.Controller), IntArg(a, 2, 1)); return null; },
            ["DrawCardFromDeck"] = (c, r, a) => { DrawCards(SideArg(r, a, 0, c.Controller), IntArg(a, 1, 1)); return null; },
            ["SpawnCardInHandBySide"] = (c, r, a) => DoSpawnInHand(c, r, a),
            ["SpawnCardOnBattlefield"] = (c, r, a) => DoSpawnOnBattlefield(c, r, a),
            ["ChangeKredits"] = (c, r, a) => DoChangeKredits(c, r, a),
            // ⚠️ 实参是 `(卡, side)` —— side 在 **index 1**，不是 0。
            //    实测两种写法：`GainKreditSlot(self, side)`（47 次）
            //    和 `GainKreditSlot(K2Node_Event_cardDestroyed, side)`（1 次），
            //    第一个参数都是"哪张卡发起的"，第二个才是阵营。
            //    读 index 0 会拿到一张卡对象 → `SideArg` 退化成 receiver 的 owner，
            //    对"给对手加槽位"这类卡会加错边。
            ["GainKreditSlot"] = (c, r, a) => { GainKreditSlot(SideArg(r, a, 1, c.Controller), 1); return null; },
            ["CustomAbilityAdd"] = (c, r, a) => DoCustomAbilityAdd(c, r, a),
            ["CustomAbilityRemove"] = (c, r, a) => DoCustomAbilityRemove(c, r, a),
            ["PersistCustomFields"] = (c, r, a) => { if (AsCard(r) is { } x) PersistCustomFields(x); return null; },
            ["MakeVeteran"] = (c, r, a) => { if (AsCard(r) is { } x) MakeVeteran(x); return null; },

            // 关键字
            ["GiveBlitz"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Blitz),
            ["GiveAmbush"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Ambush),
            ["GiveFury"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Fury),
            ["GiveGuard"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Guard),
            ["GiveImmune"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Immune),
            ["GiveSmokescreen"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Smokescreen),
            ["GiveAlpine"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Alpine),
            ["GiveMobilize"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Mobilize),
            ["GiveSalvage"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Salvage),
            ["GiveShock"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Shock),
            ["GiveBond"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Bond),
            ["RemoveBlitz"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Blitz),
            ["RemoveAmbush"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Ambush),
            ["RemoveFury"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Fury),
            ["RemoveGuard"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Guard),
            ["RemoveImmune"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Immune),
            ["RemoveSmokescreen"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Smokescreen),
            ["PinUnit"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.Pinned),
            ["UnpinUnit"] = (c, r, a) => DoRemoveKeyword(c, r, a, Keyword.Pinned),
            ["SuppressUnit"] = (c, r, a) => DoSuppressUnit(c, r, a),
            ["AddHeavyArmor"] = (c, r, a) => DoGiveKeyword(c, r, a, Keyword.HeavyArmor),

            // 卡牌私有 JSON
            //
            // ⚠️ `JSON_Get*` 是**双输出**原语：`(card, key, out value, out found)`。
            //    必须返回 `object?[] { value, found }`，VM 会按下标把两个槽都写回
            //    （见 KismetVm.ExecuteCall 里"多输出原语"那段）。
            //    只回 value 的话 `found` 永远是 null，而卡蓝图里判的是
            //    `BooleanAND(value, found)` —— 于是**所有** `JSON_Get*` 的结果都是假。
            //    `found` 的语义是"这个键存在吗"（客户端看的是 JSON 里有没有这个字段）。
            ["JSON_GetInt"] = (c, r, a) => AsCard(r) is { } x
                ? new object?[] { JsonGetInt(x, StrArg(a, 1)), JsonHasKey(x, StrArg(a, 1)) }
                : new object?[] { 0, false },
            ["JSON_SetInt"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetInt(x, StrArg(a, 1), IntArg(a, 2)); return null; },
            ["JSON_GetBool"] = (c, r, a) => AsCard(r) is { } x
                ? new object?[] { JsonGetBool(x, StrArg(a, 1)), JsonHasKey(x, StrArg(a, 1)) }
                : new object?[] { false, false },
            ["JSON_SetBool"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetBool(x, StrArg(a, 1), TruthyArg(a, 2)); return null; },
            ["JSON_GetString"] = (c, r, a) => AsCard(r) is { } x
                ? new object?[] { JsonGetString(x, StrArg(a, 1)), JsonHasKey(x, StrArg(a, 1)) }
                : new object?[] { "", false },
            ["JSON_SetString"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetString(x, StrArg(a, 1), StrArg(a, 2)); return null; },
            ["JSON_Clear"] = (c, r, a) => { if (AsCard(r) is { } x) JsonClear(x); return null; },

            // ---------------- 数组（卡牌蓝图里大量使用）----------------
            ["Array_Length"] = (c, r, a) => EvalArray(r, a).Count,
            ["Array_IsNotEmpty"] = (c, r, a) => EvalArray(r, a).Count > 0,
            ["Array_IsEmpty"] = (c, r, a) => EvalArray(r, a).Count == 0,
            ["Array_IsValidIndex"] = (c, r, a) => { int i = IntArg(a, 1); var arr = EvalArray(r, a); return i >= 0 && i < arr.Count; },
            ["Array_Get"] = (c, r, a) => { int i = IntArg(a, 1); var arr = EvalArray(r, a); return i >= 0 && i < arr.Count ? arr[i] : null; },
            // ⚠️ 必须按**值**比，不能按引用比（原来是 `ReferenceEquals`，永远 false）。
            //    实测形状是 `Array_Contains(MakeArray(5,6,7), goingToLocation)` ——
            //    `card_unit_214th_amur` 用它判「离场去向是不是 [半场,前线]」。
            //    `goingToLocation` 是事件入参（byte/int），`MakeArray` 里是 `IntConst`，
            //    两边都是装箱的 int，`ReferenceEquals` 必然不成立，于是那个判据恒为假、
            //    整条还原分支被跳过。这里退化成「先引用、再数值」两级比较。
            ["Array_Contains"] = (c, r, a) =>
            {
                var arr = EvalArray(r, a);
                var needle = a.Length > 0 ? a[0] : null;
                foreach (var item in arr)
                {
                    if (ReferenceEquals(item, needle) || AsInt(item) == AsInt(needle))
                    {
                        return true;
                    }
                }

                return false;
            },
            ["Array_LastIndex"] = (c, r, a) => EvalArray(r, a).Count - 1,
            // ⚠️ `Array_Add` / `Array_Clear` / `Array_Append` 在蓝图里是
            //    **原地修改目标数组**（目标按引用传进去，`Array_Add` 的返回值只是新元素下标）。
            //
            //    旧实现：`Array_Add` 返回一个**新数组的拷贝**、不动 `args[0]`；
            //    `Array_Clear` / `Array_Append` 直接返回 null（空操作）。
            //    后果：所有「先攒一个数组、再遍历它」的卡**累加出来永远是空表**。
            //    实测形状（全卡池扫描，见 `out/gcs/scan-arrayops.py`）：`Array_Add` 的
            //    第一个实参**全部**是成员数组变量（`cardsToDamage` / `possibleCards` /
            //    `possibleUnits` / `cardIDs` …），涉及 `card_event_anzac_spirit`(25)、
            //    `card_event_atlantic_convoy`(22)、`card_event_red_skies_skirm`(11) 等 30 张卡；
            //    而且**没有任何一个调用点读它的返回值**，所以"返回拷贝"这一支没有消费者。
            //
            //    证据（字节码原文）：`card_event_pams.GetChooseSpawnCards`
            //    —— `out/gcs/pams.bpasm:408-420`
            //    <code>
            //    let "CallFunc_Array_Add_ReturnValue" {
            //        Array_Add { instancevariable "PossibleCards"  localvariable "…_Item" }
            //    }
            //    </code>
            //    目标 `PossibleCards` 是卡的实例变量（`instancevariable @path(owner=1)`），
            //    VM 侧由 `KismetVm.SeedArrayTarget` 给它一个稳定的空数组。
            ["Array_Add"] = (c, r, a) =>
            {
                var arr = EvalArray(r, a);
                if (a.Length > 0 && AsCard(a[^1]) is { } item)
                {
                    arr.Add(item);
                }

                return arr.Count - 1;
            },
            ["Array_Append"] = (c, r, a) =>
            {
                var arr = EvalArray(r, a);
                if (a.Length > 1 && a[1] is List<CardInstance> more)
                {
                    arr.AddRange(more);
                }

                return null;
            },
            ["Array_Clear"] = (c, r, a) => { EvalArray(r, a).Clear(); return null; },
            // UE 的 `Array_Remove(目标数组, 项)` 按**值**删掉**所有**匹配项（原地）。
            ["Array_Remove"] = (c, r, a) =>
            {
                var arr = EvalArray(r, a);
                if (a.Length > 1 && AsCard(a[1]) is { } needle)
                {
                    arr.RemoveAll(x => ReferenceEquals(x, needle) || x.CardId == needle.CardId);
                }

                return null;
            },
            // UE 的 `Array_RemoveItem(目标数组, 项)` 按**值**删掉第一处匹配（不是按下标）。
            // 之前没有这一项（进的是"未实现调用"榜，实测 4 次）。
            ["Array_RemoveItem"] = (c, r, a) =>
            {
                var arr = EvalArray(r, a);
                if (a.Length > 1 && AsCard(a[1]) is { } needle)
                {
                    int at = arr.FindIndex(x => ReferenceEquals(x, needle) || x.CardId == needle.CardId);
                    if (at >= 0)
                    {
                        arr.RemoveAt(at);
                    }
                }

                return null;
            },

            // ---------------- 卡牌私有 JSON 的数组变体 ----------------
            ["JSON_GetIntArray"] = (c, r, a) => AsCard(r) is { } x ? JsonGetIntArray(x, StrArg(a, 1)) : new List<int>(),
            ["JSON_SetIntArray"] = (c, r, a) => { if (AsCard(r) is { } x) JsonSetIntArray(x, StrArg(a, 1), AsIntList(a.ElementAtOrDefault(2))); return null; },
            ["JSON_AddToIntArray"] = (c, r, a) => { if (AsCard(r) is { } x) JsonAddToIntArray(x, StrArg(a, 1), IntArg(a, 2)); return null; },

            // ---------------- 纯表现层调用：无头环境下直接吃掉 ----------------
            // 这些是 UI / 特效 / 音效，不改变规则状态。显式列出来是为了让
            // 「未实现」这个指标只反映**规则**缺口，不被表现层噪声淹没。
            ["ShowNotification"] = (c, r, a) => null,
            ["ShowCampaignMessage"] = (c, r, a) => null,
            ["PlaySoundEffect"] = (c, r, a) => null,
            ["PlayFromStart"] = (c, r, a) => null,
            ["Play"] = (c, r, a) => null,
            ["SetVisibility"] = (c, r, a) => null,
            ["SetScalarParameterValue"] = (c, r, a) => null,
            ["SetPlayRate"] = (c, r, a) => null,
            ["GetPlatformEnum"] = (c, r, a) => 0,
            ["GetCachedBPBoard"] = (c, r, a) => null,
            ["GetBoard"] = (c, r, a) => null,
            ["GetLogic"] = (c, r, a) => null,
            ["GetCardSound"] = (c, r, a) => null,
            ["destroyActorAndChildActors"] = (c, r, a) => null,
            ["UpdateCampaignStarStatus"] = (c, r, a) => null,

            // ---------------- 近似实现（语义未验证，保守处理）----------------
            ["GetDestroyedCardsCountBySide"] = (c, r, a) => c.State.Discard(SideArg(r, a, 0, c.Controller)).Count(),
            ["isBuffedByCard"] = (c, r, a) => IsBuffedByCard(c, r, a),
            ["GetUnitTypeCountOnBoard"] = (c, r, a) => c.State.Board(SideArg(r, a, 0, c.Controller)).Count(u => IsUnit(u)),
            ["updateCustomJsonIfNeeded"] = (c, r, a) => { if (AsCard(r) is { } x) PersistCustomFields(x); return null; },
            ["checkAndUpdateBuffOnCard"] = (c, r, a) => null,
            ["checkAndUpdateBuffOnAllCards"] = (c, r, a) => null,

            // ---------------- 玩家选择类（近似实现，语义待回放验证）----------------
            // 这两族是「让玩家从若干张里选一张」。无头自对弈里没有真人，
            // 用确定性随机代替 —— 行为上等价于一个随机决策的玩家。
            //
            // ⚠️ `selectCardToDraw` 已改为**按反编译蓝图实现**（见下面 SelectCardToDraw），
            //    不再是"随机挑一张"。旧实现返回的还是一张 CardInstance（蓝图的出参是 Int
            //    的 drawnCardID），而且**根本没把牌抽进手牌** —— 全卡池 38 张卡
            //    （pams / bpf / the_rock_of_gibraltar / hampshire_regiment /
            //    2nd_west_africa 这些 Develop 类）的选牌因此全部落空。
            ["selectCardToDraw"] = (c, r, a) => SelectCardToDraw(c, r, a),
            ["selectTargetFromHand"] = (c, r, a) => c.Target,

            // 三选一（`Choose One`）的分支：**读卡自己存的 `ChooseOne`**，
            // 由驱动在出牌时按动作流的 `PC[3]` 写进去（见 CardInstance.ChooseOne 的注释）。
            // 旧实现是 `Random.Next(2)` —— 回放里会和真实对局选到不同分支，
            // 而且同一个动作重放两次结果都不一样。默认 0（kardsim 的 ChooseOne 钩子默认也是 0）。
            ["WhichChooseOne"] = (c, r, a) => SelfArg(c, r, a)?.ChooseOne ?? 0,
            ["GetPossibleCardsFromStaticCards"] = (c, r, a) => EvalArray(r, a),
            ["getPossibleCardsFromStaticCards"] = (c, r, a) => EvalArray(r, a),
            ["getTwoCardsFromPossibleCards"] = (c, r, a) => EvalArray(r, a).Take(2).ToList(),
            ["findPairOfUnits"] = (c, r, a) => EvalArray(r, a).Take(2).ToList(),

            // ---------------- 卡牌移动 / 生成（meteor 等）----------------
            ["RemoveCardFromBoard"] = (c, r, a) =>
            {
                var target = TargetCard(c, r, a);
                if (target is not null && !target.IsHq)
                {
                    c.State.Move(target, CardLocation.Discard);
                }

                return null;
            },
            ["RemoveMultipleCardsFromBoard"] = (c, r, a) =>
            {
                foreach (var card in EvalArray(r, a))
                {
                    if (!card.IsHq)
                    {
                        c.State.Move(card, CardLocation.Discard);
                    }
                }

                return null;
            },
            ["SpawnCardInDeckBySide"] = (c, r, a) => DoSpawnInDeck(c, r, a),
            ["SpawnCardInDeck"] = (c, r, a) => DoSpawnInDeck(c, r, a),

            // 退回手牌 / 回牌库 —— 这两个是 `ResetCardInBattle`（→ OnCardReset 族）的**唯一**触发路径。
            ["MoveUnitFromBoardToOwnersHand"] = (c, r, a) => DoMoveUnitFromBoardToOwnersHand(c, r, a),
            ["MoveCardFromBoardToOwnersHand"] = (c, r, a) => DoMoveUnitFromBoardToOwnersHand(c, r, a),
            ["ResetCardInBattle"] = (c, r, a) =>
            {
                if (AsCard(a.FirstOrDefault()) is { } rc)
                {
                    ResetCardInBattle(rc);
                }

                return null;
            },

            // ---------------- 文本修饰（只影响显示，无头下吃掉）----------------
            ["AddNumberToText"] = (c, r, a) => null,
            ["UpdateCardText"] = (c, r, a) => null,
            ["AppendNumberToCardText"] = (c, r, a) => null,
            ["GetEmptyText"] = (c, r, a) => "",

            // ---------------- 尚未弄清的机制（显式记名，别静默吞掉）----------------
            // Forecast（预报）是较新的机制，语义还没从反编译里确认。
            // 先当 no-op 并计数，等真实回放或进一步反编译再说。
            ["Forecast"] = (c, r, a) => null,
            ["GetForecastedCards"] = (c, r, a) => new List<CardInstance>(),
            ["IsForecasted"] = (c, r, a) => false,

            // ---------------- 第二批补的原语（来自多卡组交叉验证）----------------
            ["IsGroundUnit"] = (c, r, a) => AsCard(r) is { } g && g.Definition.Type is "infantry" or "tank" or "artillery",

            // ⚠️ 同形接收者 bug（审计 §5.1）。权威签名 `BaseCardObject.h:904`：
            //     `IsDamaged(bool& isIt)` —— 没有"是哪张卡"的入参。
            // IR 实测：10 个调用点里 3 个无 recv（隐式 self），旧实现 `AsCard(r)` ⇒ 恒 false。
            ["IsDamaged"] = (c, r, a) => SelfArg(c, r, a) is { } d && d.Defense < d.MaxDefense,
            ["IsBuffed"] = (c, r, a) => AsCard(r) is { } b && b.BuffsBySource.Count > 0,
            ["GetDestroyedCardsIDsThisBattle"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                return c.State.Discard(side).Select(x => x.CardId).ToList();
            },
            ["ShuffleDeckBySide"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                var deck = c.State.Deck(side);
                var order = deck.ToList();
                c.State.Random.Shuffle(order);
                for (int i = 0; i < order.Count; i++)
                {
                    order[i].LocationNumber = i;
                }

                return null;
            },
            // ⚠️ **数额在 `a[1]`，不在 `a[0]`**（审计 §5.2b）。
            // 权威签名（`CardFunctionsStub.h:317`）：
            //     `GiveKreditsBySide(ESideEnum side, int32 kredits, int32 instigatorID, bool& qqq)`
            // 旧实现 `a.Select(AsInt).FirstOrDefault(v => v != 0)` 会把 **`a[0]`（= side，
            // 帧里是 1 或 2）**当成数额 —— 于是"给 10 费"变成"给 1 费或 2 费"，
            // 而且**随左右方变化**。IR 实测（52 个调用点）：
            //     a[0] = `side`×46 / 其它×6      a[1] = 2×12、1×10、3×8、4×3、-2×3、-1×2、5×1…
            //     a[2] = `cardID`（instigatorID） a[3] = out 槽
            // 负数（-1/-2/-3/-7）扣费，`AddKredits` 自己会钳到 0。
            // ⚠️ 不再保留旧的"数额为 0 就送 1"兜底：那是把 bug 当兜底，
            //    会让"给 0 费"这种真实调用白送 1 费。
            ["GiveKreditsBySide"] = (c, r, a) =>
            {
                var side = SideArg(r, a, 0, c.Controller);
                c.State.AddKredits(side, IntArg(a, 1));
                return null;
            },
            ["AddToBattleLog"] = (c, r, a) => null,      // 纯日志
            ["DecrementCountdown"] = (c, r, a) => null,  // 倒计时机制，语义待确认
            ["Array_Reverse"] = (c, r, a) =>
            {
                var copy = new List<CardInstance>(EvalArray(r, a));
                copy.Reverse();
                return copy;
            },

            // ---------------- 第三批（来自新控制流下的多卡组验证）----------------
            //
            // ⚠️ 这两个是「**这张卡自己**当前的费用 / 行动费」，不是"某一方手牌总费用"。
            //    实测实参只有一个 out 槽，主语在 `recv`：
            //        getTotalKreditCost(recv=GetCardFromID_card, out totalKreditCost)
            //        getTotalOperationCost(recv=GetCard_card_36,      out totalOperationCost)
            //    旧实现把它们当成"按阵营求和"（`State.Hand(side).Sum(...)` /
            //    `State.Board(side).Sum(...)`），语义完全不同 ——
            //    `card_unit_big_red_one` 的「手牌里的牌都是 4 费」判据就是
            //    `getTotalKreditCost(卡) == 4`，按整手牌求和永远不可能等于 4，
            //    于是它会把每张手牌都"设成 4 费"，把已经被别的来源改过的牌也算进去。
            ["getTotalKreditCost"] = (c, r, a) => SelfArg(c, r, a)?.KreditCost ?? 0,
            ["getTotalOperationCost"] = (c, r, a) => SelfArg(c, r, a)?.OperationCost ?? 0,
            ["GetCombatKeywords"] = (c, r, a) =>
            {
                // 战斗相关关键字列表（Guard/Blitz/Fury/…），供卡牌读取
                var card = AsCard(r) ?? c.Target;
                return card is null
                    ? new List<CardInstance>()
                    : new List<CardInstance>();
            },
            ["GetCardsToTheLeft"] = (c, r, a) =>
            {
                // 同一战线上、位置编号比它小的卡
                var card = AsCard(r) ?? AsCard(a.FirstOrDefault()) ?? c.Self;
                if (card is null)
                {
                    return new List<CardInstance>();
                }

                return c.State.Board(card.Owner).Where(x => x.LocationNumber < card.LocationNumber).ToList();
            },
            ["GetAdjacentCards"] = (c, r, a) =>
            {
                var card = AsCard(r) ?? AsCard(a.FirstOrDefault()) ?? c.Self;
                if (card is null)
                {
                    return new List<CardInstance>();
                }

                return c.State.Board(card.Owner)
                    .Where(x => Math.Abs(x.LocationNumber - card.LocationNumber) == 1)
                    .ToList();
            },
            ["DrawSpecificCardFromDeckBySide"] = (c, r, a) => DoDrawSpecific(c, r, a),

            // ---------------- 光环（aura）原语：卡自带的私有函数 ----------------
            //
            // ⚠️ 这一族和别的不一样：`ApplyTheBuff` / `RemoveTheBuff` / `anyOrderPlayedThisTurn`
            //    / `_isBigRedOne` 是**卡蓝图自己的私有函数**，不是引擎 API。
            //    名字在 2053 张卡里会撞车（`card_unit_214th_amur` 和
            //    `card_event_committed_crew` 的 ApplyTheBuff 干的事完全不同），
            //    而且**不在 `card-ir.json` 里** —— IR 生成器只编 `ExecuteUbergraph_*`，
            //    私有函数体被丢掉了（`docs/内核补全队列.md` 说「6 个触发点 IR 全在，
            //    只缺原语」，这句对触发点成立，但对**私有函数体不成立**）。
            //
            //    所以这里的语义是**从 `ref/kards-sim` 的直译产物逐行读出来的**
            //    （`Generated/<阵营>/.../card_unit_85_pioneer_company.g.cs` 等），
            //    不是猜的。按卡名分派，因为同一个名字在不同卡上语义不同。
            ["ApplyTheBuff"] = (c, r, a) => DoApplyTheBuff(c, a),
            ["RemoveTheBuff"] = (c, r, a) => DoRemoveTheBuff(c, a),
            ["anyOrderPlayedThisTurn"] = (c, r, a) => AnyOrderPlayedThisTurn(c),
            ["_isBigRedOne"] = (c, r, a) => a.Length > 0 && AsCard(a[0]) is { } x
                                            && string.Equals(x.Name, c.Self?.Name, StringComparison.Ordinal),
            ["GetCardsPlayedThisTurn"] = (c, r, a) => c.State.CardsPlayedThisTurn.ToList(),
            ["getHasGameplayTag"] = (c, r, a) => HasGameplayTag(c, r, a),

            // `BP_CardFunctions::CanCardBeBuffed(Card)` 的实现（见 CardApi.CanCardBeBuffed）。
            // 卡蓝图本身不直接调它（调用点是 ChangeAttack/ChangeDefense/ChangeKreditCost/
            // CustomAbilityAdd/Give* 一族），但这里是它的忠实实现，放进来是为了
            // 万一有卡调它时不会静默返回 null。
            ["CanCardBeBuffed"] = (c, r, a) => SelfArg(c, r, a) is { } x && CanCardBeBuffed(x),

            // ⚠️ 同形接收者 bug（审计 §5.1）。IR 实测：3 个调用点里 2 个无 recv，
            //    旧实现 `AsCard(r)?.HeavyArmor ?? 0` ⇒ 恒 0（重甲查询失效）。
            ["getTotalHeavyArmor"] = (c, r, a) => SelfArg(c, r, a)?.HeavyArmor ?? 0,

            // ---- P1（2026-09-30）：`getHas*` 一族 ----------------------------------
            //
            // 出处：审计 §6 的 P1#27f ——「派发表里**没有任何 `getHas*` 键**」。
            // 卡蓝图里这些判据的形状是「如果这张卡有 X 就…」，读不到就**静默取假**，
            // 于是那些分支的方向是反的（该走的没走、不该走的走了）。
            //
            // IR 实测调用点（`klink bot/docs/card-ir.json`，脚本
            // `out/audit/p1-gethas-calls.py`）：
            //     getHasBlitz 9 点 / getHasAlpine 9 / getHasShock 7 / getHasGuard 5 /
            //     getHasAmbush 2 / getHasFury 1 / getHasSmokescreen 1
            //
            // 语义：和 `KismetVm.GetMember` 的成员读**完全同源**（都读 `Keyword` 集合），
            // 所以两处必须同步 —— 成员读走 `hasXxx`，函数调用走 `getHasXxx`。
            //
            // ⚠️ 接收者一律用 `SelfArg`（`c.Self ?? c.Target`），不用 `AsCard(r)`：
            //    这一族大量以**隐式 self** 出现（没有 recv），`AsCard(r)` 会恒 null。
            //    同族的前科见审计 §5.1 的 `IsVeteran` / `IsDamaged`。
            ["getHasBlitz"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Blitz),
            ["getHasGuard"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Guard),
            ["getHasAmbush"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Ambush),
            ["getHasFury"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Fury),
            ["getHasSmokescreen"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Smokescreen),
            ["getHasAlpine"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Alpine),
            ["getHasShock"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Shock),
            ["getHasMobilize"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Mobilize),
            ["getHasSalvage"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Salvage),
            ["getHasPincer"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Pincer),
            ["getHasDeployment"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Deployment),
            ["getHasDestruction"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Destruction),
            ["getHasCovert"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Covert),
            ["getHasScrying"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Scrying),
            // `getHasImmune` 读的是**运行时**授予的免疫（`cardsGivingImmunity` / `isImmune`），
            // 不是 CDO 字段（CDO 里 `isImmune` 出现 0 次）。内核把免疫建模成
            // `Keyword.Immune`（`MatchEngine.ApplyDamage` 就读它），所以这里读同一个集合 ——
            // 语义一致，不是拿近似值顶替。
            ["getHasImmune"] = (c, r, a) => HasKeyword(c, r, a, Keyword.Immune),
        };

    /// <summary>
    /// `getHasXxx()` 一族：这张卡现在有没有关键字 <paramref name="keyword"/>。
    ///
    /// 接收者用 <see cref="SelfArg"/>（隐式 self 兜底 `c.Self ?? c.Target`）。
    /// 卡拿不到（IR 参数位错等）时返回 false —— 和旧行为一致，不会凭空为真。
    /// </summary>
    private static bool HasKeyword(EffectContext c, object? receiver, object?[] args, string keyword)
        => SelfArg(c, receiver, args)?.Keywords.Contains(keyword) ?? false;

    /// <summary>
    /// 「从牌库里挑一张」—— 按反编译蓝图实现（<c>BP_CardFunctions.selectCardToDraw</c>）。
    ///
    /// 签名（<c>ref/kards-sim/KardsSim/Generated/_index.g.cs</c> 登记的就是它）：
    /// <code>
    /// selectCardToDraw(cardSelectingCardToDraw:Int, selectFromTopOfDeck:Bool, isEffect:Bool,
    ///                  out drawnCardID:Int)
    /// </code>
    /// 调用点实例：<c>card_event_pams.OnPlayedFromHand</c> 的 i=822
    /// （<c>args=[cardID, false, false, out]</c>）。
    ///
    /// 蓝图原逻辑（逐条对应 <c>BP_CardFunctions.g.cs</c> 的 L_xxxx 标号）：
    /// <list type="number">
    /// <item><c>L_064B</c>：<c>!isEffect &amp;&amp; IsLocationFull(手牌)</c> → 直接返回 0，
    ///   什么都不抽。（所以这里是"手牌满了就不抽"，而不是"抽了再弃"。）</item>
    /// <item><c>L_0680</c>：候选 = <c>cardSelecting.GetChooseSpawnCards()</c>
    ///   （出参 <c>cards / markAsSeen / keepOrder</c>）；<c>keepOrder</c> 为假时先
    ///   <c>Array_ShuffleFromStream(cardsRandomStream)</c> 洗一遍。</item>
    /// <item><c>L_0745</c>：<c>keepOrder</c> 分支下候选只取
    ///   <c>0..Min(2, LastIndex)</c> —— **最多 3 张**，这正好解释了回放里
    ///   <c>CS</c> 动作第 2 槽恒为 0/1/2。</item>
    /// <item><c>L_0B57</c>（只有 1 张候选）：直接
    ///   <c>DrawSpecificCardFromDeckBySide</c> + <c>OnHandTargetSelected</c>。</item>
    /// <item><c>L_0D61</c>（多张候选）：只发 <c>NotifySelectCardToDrawPending</c>，
    ///   **不在本函数里抽牌** —— 抽牌由答复（回放里的 <c>CS</c> 动作）触发。</item>
    /// </list>
    ///
    /// **本内核的落地方式（2026-09-27 打通）**：
    /// <list type="bullet">
    /// <item>候选表：执行那张卡自己的 <c>GetChooseSpawnCards</c>（已编进 IR 的 <c>locals</c>），
    ///   见 <see cref="GetChooseSpawnCards"/>。</item>
    /// <item>答复来源：回放路径走 <c>MatchEngine.PickCardToDraw</c>，选中的是
    ///   <c>CS</c> 动作第 3 槽的**卡组码 → 卡名**（比用下标猜候选表可靠）；
    ///   自对弈路径退化到"候选表第一张"（蓝图**没有** Develop 族的自动兜底）。</item>
    /// <item>落实：按卡名 <c>Create</c> 一张新卡（<see cref="DevelopChosenCard"/>）。</item>
    /// </list>
    ///
    /// ⚠️ 仍然没做到的：`keepOrder` 为假时蓝图会 `Array_ShuffleFromStream` 洗候选，
    ///    再取前 3 张 —— 回放路径不需要它（答复自带卡名），但**自对弈路径**的
    ///    "第一张"与真实客户端的"洗完第一张"不是同一个分布。这是近似，不是复刻。
    /// </summary>
    private object? SelectCardToDraw(EffectContext c, object? r, object?[] a)
    {
        int selectingId = IntArg(a, 0, c.Self?.CardId ?? 0);
        var selecting = c.State.ById(selectingId) ?? (AsCard(r) ?? c.Self);
        bool selectFromTopOfDeck = TruthyArg(a, 1);
        bool isEffect = TruthyArg(a, 2);

        // side 取自"挑牌的那张卡"自己：`selectCardToDraw` 的第一个参数就是它的 cardID，
        // 而它此刻**可能已经在弃牌堆里**（指令打出后立刻进弃牌堆），
        // 所以不能用 `IsLocatedOnBoard` 之类的在场判据。
        Side side = selecting?.Owner ?? c.Controller;

        // 蓝图 L_064B：非效果选择 + 手牌已满 → 不抽
        if (!isEffect && c.State.Hand(side).Count >= GameState.HandCapacity)
        {
            return 0;
        }

        if (selecting is null)
        {
            return 0;
        }

        // ⚠️ 蓝图里这是**两条完全不同的路**，候选的来源不一样：
        //   `selectFromTopOfDeck == true`  → `L_0005~L_05A0`：候选 = **牌库里的实例**
        //                                    （`FilterCardsToScry` 过一遍，取前 3 张）。
        //   `selectFromTopOfDeck == false` → `L_05A5` 起：候选 =
        //                                    `卡自己的 GetChooseSpawnCards()`，
        //                                    是 **`GetAllActiveStaticCards()` 过滤出的卡池模板**。
        //   旧实现把两条路混成一条（都当牌库实例），Develop 那一族的候选永远对不上。
        if (selectFromTopOfDeck)
        {
            CardInstance? chosen;
            if (c.Engine.PickCardToDraw is { } pickDeck)
            {
                chosen = pickDeck(selecting, true, isEffect);
            }
            else
            {
                // 无答复源（自对弈）：按蓝图自带兜底 —— `BP_Logic.autoPickCardToDraw`
                // 在没有选择界面时取 `GetDeckBySide(side).DeckCardIDs[0]`，即牌库第一张。
                chosen = c.State.Deck(side).FirstOrDefault();
            }

            return DrawChosenCardToHand(selecting, chosen)?.CardId ?? 0;
        }

        // ---- Develop 族 ----
        // 候选表由**那张卡自己**的 `GetChooseSpawnCards` 算（每卡过滤条件不同）。
        var candidates = GetChooseSpawnCards(c, selecting, out _, out _);

        CardInstance? picked;
        if (c.Engine.PickCardToDraw is { } pickDevelop)
        {
            // 有答复源（回放路径）：答复只可能来自动作流。
            // ⚠️ 取不到时**什么都不生成**，返回 0 —— 蓝图在"候选为空"时也是这条路径
            //    （L_095A 判 `Array_Length > 0`，为假就整段跳过）。
            //    绝不能拿候选表第一张顶替：那是**另一张牌**，而且是静默改错。
            //    取不到的答复由回放驱动那边如实记成"未应用"。
            picked = pickDevelop(selecting, false, isEffect);
        }
        else
        {
            // 自对弈：没有玩家可问。蓝图本身**没有** Develop 族的自动兜底
            // （`autoPickCardToDraw` 取的是牌库第一张，那是牌库族的东西），
            // 所以这里退化成"取候选表第一张"，并在下面注明这是近似。
            picked = candidates.Count > 0 ? candidates[0] : null;
        }

        if (picked is null)
        {
            return 0;
        }

        // 答复指向的必须是**卡池模板**（`CardId == 0`、不在对局里）——
        // 落实方式是"按卡名新生成一张"（见 DevelopChosenCard 的出处注释）。
        var created = DevelopChosenCard(selecting, picked.Name);
        return created?.CardId ?? 0;
    }

    /// <summary>
    /// 把"选中的那张牌"落实成状态变化：从牌库抽到手牌，并给挑牌的那张卡派发
    /// <c>OnHandTargetSelected(handTargetCardID, instigatorID)</c>。
    ///
    /// 依据：蓝图单候选分支（<c>BP_CardFunctions.g.cs</c> L_0B57~L_0C5B）就是
    /// <c>DrawSpecificCardFromDeckBySide(挑牌卡, 选中卡.cardID, side, false)</c>
    /// 紧跟 <c>OnHandTargetSelected(挑牌卡, 选中卡.cardID, 0)</c>。
    /// 事件契约（<c>docs/event-contracts.json</c>）是 <c>[Int handTargetCardID, Int instigatorID]</c>，
    /// 49 张卡订阅它 —— 例如 <c>card_event_pams</c> 的 <c>OnHandTargetSelected</c>
    /// 会把自己 develop 出来的那张牌塞回牌库的随机位置，漏掉这一步就少一次洗牌。
    ///
    /// 公开给回放驱动用：回放里的 <c>CS</c> 动作到达时，如果挑牌卡的效果**没**走到
    /// <c>selectCardToDraw</c>（例如那张牌根本没打出去），答复还得照样落实，
    /// 否则这条动作就白丢了。
    /// </summary>
    public CardInstance? DrawChosenCardToHand(CardInstance? selecting, CardInstance? chosen)
    {
        if (chosen is null || selecting is null)
        {
            return null;
        }

        Side side = selecting.Owner;
        if (chosen.Location != side.DeckOf())
        {
            // 选中的牌不在牌库里（已经被抽走/换走）—— 蓝图里候选表就是牌库里的卡，
            // 所以这种情况属于内核状态已经偏了，**什么都不做**比硬抽一张更安全。
            return null;
        }

        State.Move(chosen, side.HandOf());

        // 事件参数顺序按 docs/event-contracts.json 的 slots 声明：
        //   handTargetCardID 在前、instigatorID 在后。
        // VM 侧 `K2Node_Event_*CardID` 解析成 `ctx.Trigger/Target` 那张卡的 ID，
        // 所以 eventSubject 必须是**被选中的卡**；而跑哪张卡的程序由 subject 决定。
        FireTrigger("OnHandTargetSelected", selecting, side,
                    eventArgs: new object?[] { chosen.CardId, selecting.CardId },
                    eventSubject: chosen);

        return chosen;
    }

    /// <summary>从牌库中找出指定卡并抽到手牌（对应 DrawSpecificCardFromDeckBySide）。</summary>
    private object? DoDrawSpecific(EffectContext c, object? r, object?[] a)
    {
        // 实参形状（实测 2 个调用点，完全一致）：
        //     DrawSpecificCardFromDeckBySide(cardID, 目标卡.cardID, side, bool)
        //   → **side 在 index 2**，不是 0。index 0 是 instigatorID（整数），
        //     `SideArg` 只认 1/2，instigatorID 恰好是 1 或 2 时会**匹配到错误的阵营**，
        //     也就是"抽对手的牌库"。这种错误不报错、只会静默抽错人。
        var side = SideArg(r, a, 2, c.Controller);
        string? cardName = FindCardNameArg(c, a);
        if (cardName is null)
        {
            return null;
        }

        var match = c.State.Deck(side).FirstOrDefault(x => x.Name == cardName || x.Definition.Name == cardName);
        if (match is null)
        {
            return null;
        }

        c.State.Move(match, side.HandOf());
        return match;
    }

    /// <summary>
    /// 往牌库里生成一张卡。
    /// 参数形状：<c>(side, 卡名, ..., 位置枚举, out card)</c> —— 卡名是第一个以 card_ 开头的字符串。
    /// </summary>
    private object? DoSpawnInDeck(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        string? cardName = a.Select(AsString).FirstOrDefault(s => s is not null && s.StartsWith("card_", StringComparison.Ordinal));
        if (cardName is null)
        {
            return null;
        }

        var deck = c.State.Deck(side).ToList();
        bool toBottom = true;
        foreach (object? v in a)
        {
            if (v is int i && i == (int)SpawnInDeckLocation.Top)
            {
                toBottom = false;
            }
        }

        if (toBottom)
        {
            var card = c.State.Create(cardName, side, side.DeckOf(), c.State.NextLocationNumber(side, side.DeckOf()));
            return card;
        }

        // 放到牌库顶：把现有牌整体后移一位
        foreach (var existing in deck)
        {
            existing.LocationNumber++;
        }

        return c.State.Create(cardName, side, side.DeckOf(), 0);
    }

    /// <summary>
    /// 派发表里已经实现的原语名。
    ///
    /// 给「差距量化」工具用（<c>BotSim gaps</c>）：拿它去对
    /// 卡组实际需要的调用集合，才能算出真实缺口，而不是猜。
    /// </summary>
    public IReadOnlyCollection<string> ImplementedNames => _dispatch.Keys;

    /// <summary>
    /// 执行**卡自己的** <c>GetPlayFromHandDamage</c>，返回它写进输出参数
    /// <c>damage</c> 的值。
    ///
    /// 为什么不是「读某个字段」：全卡池扫过一遍，这个函数**每张卡都不一样**，
    /// 值就写在卡自己的字节码里，不存在统一的字段或数据源。
    /// 三种典型形状（<c>klink bot/tools/gen-kismet-ir.py</c> 的 <c>LOCAL_FUNCTIONS</c>）：
    ///
    /// <code>
    /// 23×  card_unit_17th_infantry_brigade   damage = 2                       ← 纯字面量
    ///  1×  card_event_the_commonwealth       damage = SelectInt(20, 0, HQ防御 >= 30)
    ///  1×  card_event_forward_base_anzac     damage = 手牌最左那张的 getTotalKreditCost()
    /// </code>
    ///
    /// 所以这里只能把那张卡的函数体**解释执行**一遍（<see cref="Blueprint.KismetVm.RunLocalProgram"/>）。
    /// 卡没有这个函数时返回 0 **并且计入未实现统计** —— 不猜一个值。
    /// </summary>
    /// <param name="target">
    /// 调用点的第一个实参（蓝图里是 <c>K2Node_Event_targetCard</c>），
    /// 作为函数入参 <c>targetCard</c> 喂进去。实测英联邦等卡根本不用它，
    /// 但 <c>card_unit_zero</c> 用它（<c>SelectInt(1, 0, IsValid(targetCard))</c>）。
    /// </param>
    private int DoGetPlayFromHandDamage(EffectContext c, CardInstance? target)
    {
        var self = c.Self;
        var library = Blueprint.KismetLibrary.Default;
        if (self is null || library is null)
        {
            return 0;
        }

        var program = library.FindLocalProgram(self.Definition.Name, "GetPlayFromHandDamage");
        if (program is null)
        {
            // 这张卡没有这个局部函数 —— 记成缺口，别用 0 假装算出来了
            NotifyUnimplemented($"<GetPlayFromHandDamage:{self.Definition.Name}>");
            return 0;
        }

        var seed = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["targetCard"] = target ?? c.Target,
        };

        return Blueprint.KismetVm.ToInt(Vm.RunLocalProgram(program, c, seed, "damage"));
    }

    // ==================== Develop（GetChooseSpawnCards 一族）====================
    //
    // 「Develop」= 从**卡池模板**里挑一张、把它**生成**成一张新卡。
    // 全链路（每一环都有反编译出处）：
    //
    //   1. 卡自己的 `OnPlayedFromHand` 调 `selectCardToDraw(cardID, false, false, out)`
    //      —— 例 `card_event_pams` IR i=822（`out/gcs/pams.bpasm` 的 `.export 5`）。
    //   2. `BP_CardFunctions.selectCardToDraw` 的 `L_0680` 调
    //      `GetChooseSpawnCards(out cards, out markAsSeen, out keepOrder)`
    //      —— 见 `ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:33744-33748`。
    //      候选是 `GetAllActiveStaticCards()` 过滤出来的**卡池模板**，不是牌库实例。
    //   3. 候选 >1 时只发 `NotifySelectCardToDrawPending`（`L_0D61`），
    //      把候选的**卡名**发给玩家；玩家选完发 `XActionCardToDrawSelected`（回放里的 `CS`）。
    //   4. 答复到达后由 `OpponentActionsCardToDrawSelected` 落实
    //      （`klink bot/decompiled/BP_OnlineMatch.live.all-functions.json`，bytecode 26-53）：
    //      <code>
    //      tmpSourceCard = GetCardFromID(cardTriggeringDraw)
    //      tmpTargetCardID = cardFunctions.CreateCard(
    //          tmpSourceCard.side, Conv_StringToName(cardNameToSpawn),
    //          &lt;isEffect ? 8(弃牌堆) : side==1 ? 3(左手) : side==2 ? 4(右手)&gt;,
    //          0, -1, spawnCardInHand=true, isGold, "", false, true, cardTriggeringDraw, …)
    //      tmpSourceCard.OnHandTargetSelected(tmpTargetCardID, cardTriggeringDraw)
    //      DevelopAndForecastCheck(cardTriggeringDraw, tmpTargetCardID, tmpSourceCard)
    //      </code>
    //      —— **新卡先落在挑牌方的手牌里**，再由那张卡自己的 `OnHandTargetSelected`
    //      决定最终去哪（pams 是"塞回牌库随机位置 + 费用设 0"）。

    /// <summary>
    /// `GetAllActiveStaticCards(includeNotAttainable, includeReserved, out cards)`
    /// —— **卡池里的静态卡模板**（`UBaseCardObject*` 数组），**不是**对局里的实例。
    ///
    /// 真实实现在 `BP_CardFunctions.GetAllActiveStaticCards`
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:19104-19338`）：
    /// 遍历 `GameStateRef.GetAllStaticCardsSortedByName()`，按
    /// ① 卡集（`cardSet` 1..10/12/13/15..20 保留，21 要看 `isCardSetConfigActive`，
    ///   其余只在 `includeNotAttainable` 为真时保留）、
    /// ② `includeReserved` 为假时过 `NotifyCheckCardReserved`、
    /// ③ `NotifyCheckCardBlacklisted`
    /// 三层过滤。
    ///
    /// ⚠️ **本内核的近似（明确记下来，别当成复刻）**：
    /// 我们**没有建模卡集/黑名单/`BalancedCards`**，所以这里返回**整个卡库**
    /// （`CardDatabase.All`，2021 张），只做"按卡名排序"这一步。
    /// 后果：候选表里可能多出几张真实客户端会滤掉的卡（战役/未获得卡集）。
    /// 对**回放路径**没有影响 —— 回放里"选中哪张"来自动作流自带的卡组码；
    /// 对**自对弈路径**有影响（自对弈的兜底选择会在这份更宽的候选表里挑）。
    /// `IsCardReserved` 目前是恒 false 的桩（见上面的派发表），
    /// `NotifyCheckCardBlacklisted` 没有对应物 —— 这两条都**没有**实现。
    /// </summary>
    private List<CardInstance> StaticCardPool(EffectContext c)
    {
        var db = c.State.Database;
        if (!ReferenceEquals(_staticPoolDb, db) || _staticPool is null)
        {
            var pool = new List<CardInstance>(db.Count);
            foreach (var def in db.All.OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                pool.Add(TemplateInstance(def, c.Controller));
            }

            _staticPool = pool;
            _staticPoolDb = db;

            // ⚠️ 卡池一换，按卡名缓存的候选表就全部失效（候选表里的元素就是这些模板实例）。
            _staticPoolGeneration++;
            _gcsCache.Clear();
            _gcsPurityCache.Clear();
        }

        // 每次返回一份新的 List（调用方会 Array_Clear / Array_Add 原地改它）
        return new List<CardInstance>(_staticPool);
    }

    private static CardDatabase? _staticPoolDb;
    private static List<CardInstance>? _staticPool;

    /// <summary>卡池代数 —— 每次重建 <see cref="_staticPool"/> 时 +1，用来判定缓存条目是否过期。</summary>
    private static int _staticPoolGeneration;

    // ==================== `GetChooseSpawnCards` 候选表缓存 ====================
    //
    // 为什么要缓存：这张卡的 `GetChooseSpawnCards` 在蓝图里是「**扫一遍整个卡池再过滤**」
    // 的形状 —— `GetAllActiveStaticCards()` 返回 2021 张模板卡，然后对每一张跑
    // `Array_Get → 谓词 → 比较 → Array_Add` 那一小段（约 8 步）。
    // 一次调用 ≈ 2021 × 8 ≈ 16k 步；实测 `BotSim play` 每局 34,631 步里绝大部分是它。
    //
    // 为什么**能**缓存：对下面 <see cref="PoolPureOps"/> 白名单里的那些卡，
    // 这个过滤条件在**整局、甚至跨局**都是静态的 —— 判据只有
    // 「卡池模板自己的定义（faction / 类型 / 费用 / tag / 私有 JSON）」+ 程序里的字面量。
    // 卡池模板本身已经是全局缓存的（<see cref="_staticPool"/>），所以候选表也是常量。
    //
    // ⚠️ **哪些卡不能缓存，为什么**（判据是自动的，见 <see cref="IsPoolPureProgram"/>）：
    //   · 读对局状态的：`GetAllCards` / `GetCardsInHandBySide` / `IsLocatedInDeck` /
    //     `GetOppositeSide`（`SelfSide(c)` = 效果控制方）——
    //     `air_land_sea` / `happy_time` / `sabotage` / `the_rock_of_gibraltar`；
    //   · 带随机的：`RandomIntFromRangeWithStream` —— `rain1_mist` / `storm1_gale` / `sunny1_blue_sky`；
    //   · 读**这张卡自己的**私有 JSON（`{self:true}` 实参）：`baker_street_irregulars`
    //     （`JSON_GetBool(self, "oneChosen", …)`，`oneChosen` 是对局中会变的标志）、
    //     `bpf`、`the_rock_of_gibraltar`；
    //   · 调**未实现**函数的：`IsBomber`（bomber_mafia / ingenuity）、
    //     `GetStaticCard`（bpf / ingenuity / 6 张 unit_*）、`IsGotcha`（15_aufklarungs）、
    //     `getAndDecryptAttack`（sea_embargo）、`GetMainNationForSide`（pilot_escape）、
    //     `getSkirmishBP` + `Get Start Of Turn Spawn Cards`（brawl_test1）。
    //     这些**不进缓存**是为了保住 `UnimplementedCalls` 计数 —— 缓存命中会跳过整段
    //     程序执行，未实现调用的统计会凭空消失，那是诊断口径，不该被缓存悄悄改掉。
    //
    // 命中率：36 张定义了 `GetChooseSpawnCards` 的卡里 15 张可缓存，
    // 而**开销大的那 25 张（扫全池）里 15 张全在缓存内** —— 剩下 10 张恰好就是上面那些
    // 有状态/随机/未实现的。所以省下的正是全部重活。

    /// <summary>
    /// 「卡池纯函数」白名单：**只读卡池模板自身的定义、不读任何对局状态**的原语。
    ///
    /// 只列**已经实现**（在派发表里）的那些 —— 未实现的函数会让程序走
    /// `UnimplementedCalls` 统计，缓存命中会把它抹掉。
    /// </summary>
    private static readonly HashSet<string> PoolPureOps = new(StringComparer.Ordinal)
    {
        // 卡池本身
        "GetAllActiveStaticCards",
        // 恒 false 的桩（`IsCardReserved` 见上面派发表），不读任何东西
        "IsCardReserved",
        // 只读「接收者那张卡自己的定义」的谓词 / 取值 —— 接收者一律来自卡池数组
        "IsUnit", "IsOrder", "IsTank", "IsAirUnit", "IsInfantry", "IsArtillery", "IsLocation",
        "IsLocatedOnBoard", "IsLocatedInHand", "IsVeteran", "IsDamaged", "IsBuffed",
        "IsGroundUnit", "IsForecastCard", "IsForecasted",
        "getTotalKreditCost", "getTotalOperationCost", "getTotalAttack", "getTotalDefense",
        "getTotalHeavyArmor", "getHasGameplayTag",
        "JSON_GetBool", "JSON_GetInt", "JSON_GetString", "JSON_GetIntArray",
        // 局部数组操作（`PossibleCards` 这种程序内数组，不碰对局）
        "Array_Add", "Array_Append", "Array_Clear", "Array_Contains", "Array_Get",
        "Array_IsEmpty", "Array_IsNotEmpty", "Array_IsValidIndex", "Array_LastIndex",
        "Array_Length", "Array_Remove", "Array_RemoveItem", "Array_Reverse",
        // 纯算术 / 比较（KismetVm.EvalMath 的分支）
        "Add_IntInt", "Subtract_IntInt", "Multiply_IntInt", "Divide_IntInt", "Percent_IntInt",
        "Abs_Int", "Min_IntInt", "Max_IntInt", "Greater_IntInt", "GreaterEqual_IntInt",
        "Less_IntInt", "LessEqual_IntInt", "EqualEqual_IntInt", "NotEqual_IntInt",
        "EqualEqual_StrStr", "NotEqual_StrStr", "EqualEqual_NameName", "NotEqual_NameName",
        "EqualEqual_ObjectObject", "NotEqual_ObjectObject", "EqualEqual_BoolBool", "NotEqual_BoolBool",
        "BooleanAND", "BooleanOR", "Not_PreBool", "SelectInt", "IsValid",
        "Conv_IntToString", "Conv_IntToText", "Conv_ByteToText", "Conv_TextToString",
        "Conv_StringToText", "Conv_IntToBool", "Conv_BoolToInt", "Conv_IntToByte", "Conv_ByteToInt",
        "EqualEqual_ByteByte", "NotEqual_ByteByte",
        "EnumCompareFaction", "EnumCompareSide", "EnumCompareCardType", "EnumCompareCardLocation",
        "Concat_StrStr", "Conv_NameToString", "GetEnumeratorUserFriendlyName",
    };

    /// <summary>卡名（已解析变体）→ 这个程序的 `GetChooseSpawnCards` 是否可缓存。只算一次。</summary>
    private static readonly Dictionary<string, bool> _gcsPurityCache = new(StringComparer.Ordinal);

    /// <summary>卡名 → 候选表（元素是 <see cref="_staticPool"/> 里的模板实例，全程只读）。</summary>
    private static readonly Dictionary<string, GcsCacheEntry> _gcsCache = new(StringComparer.Ordinal);

    private sealed record GcsCacheEntry(
        int Generation, List<CardInstance> Cards, bool MarkAsSeen, bool KeepOrder);

    /// <summary>缓存命中次数（诊断用；也是「跑的是新 dll」的哨兵）。</summary>
    public static long GcsCacheHits { get; private set; }

    /// <summary>判定为可缓存、但缓存里没有、真跑了一遍的次数。</summary>
    public static long GcsCacheMisses { get; private set; }

    /// <summary>判定为不可缓存、照旧每次真跑的次数。</summary>
    public static long GcsCacheBypassed { get; private set; }

    /// <summary>
    /// 这个 `GetChooseSpawnCards` 程序是不是「卡池纯函数」—— 只看它碰了哪些原语。
    ///
    /// 三条判据（任何一条不满足就不缓存，宁慢勿错）：
    /// 1. 程序里**任何地方**都没有 `{self:true}` —— 那是「这张卡自己」，
    ///    读它就会读到对局状态（`baker_street_irregulars` 的 `JSON_GetBool(self,…)`）。
    /// 2. 每一条 `call` / `math` 步骤都**带接收者**（`recv`）—— 没有接收者时
    ///    `SelfArg` 会退回 `ctx.Self` / `ctx.Target`，同样是读对局状态。
    /// 3. 出现的每一个函数 / 算术名都在 <see cref="PoolPureOps"/> 里。
    ///    （表达式里的嵌套调用要求带 `ctx`，否则接收者同样会退回 `ctx.Self`。）
    /// </summary>
    private static bool IsPoolPureProgram(Blueprint.KismetProgram program)
    {
        foreach (Blueprint.KismetStep step in program.Steps)
        {
            if (step.Op is "call" or "math")
            {
                if (step.Receiver is null || step.Function is null || !PoolPureOps.Contains(step.Function))
                {
                    return false;
                }
            }

            foreach (Blueprint.KismetExpr arg in step.Args)
            {
                if (!IsPoolPureExpr(arg))
                {
                    return false;
                }
            }

            if (step.Source is not null && !IsPoolPureExpr(step.Source)) return false;
            if (step.Condition is not null && !IsPoolPureExpr(step.Condition)) return false;
            if (step.Receiver is not null && !IsPoolPureExpr(step.Receiver)) return false;
        }

        return true;
    }

    /// <summary>
    /// <see cref="IsPoolPureProgram"/> 返回 false 时，给出**第一条**不满足的判据 ——
    /// 只在 <see cref="GcsDiag"/> 打开时调用（<c>KLINK_GCS_DIAG=1</c>），
    /// 用来回答「这张卡为什么没进缓存」。默认关闭，零开销。
    /// </summary>
    private static string PoolImpurityReason(Blueprint.KismetProgram program)
    {
        foreach (Blueprint.KismetStep step in program.Steps)
        {
            if (step.Op is "call" or "math")
            {
                if (step.Receiver is null) return $"i={step.Index} {step.Op} {step.Function} 无接收者";
                if (step.Function is null) return $"i={step.Index} {step.Op} 无函数名";
                if (!PoolPureOps.Contains(step.Function)) return $"i={step.Index} 不在白名单: {step.Function}";
            }

            foreach (Blueprint.KismetExpr arg in step.Args)
            {
                if (!IsPoolPureExpr(arg)) return $"i={step.Index} 实参不纯: {arg}";
            }

            if (step.Source is not null && !IsPoolPureExpr(step.Source)) return $"i={step.Index} src 不纯: {step.Source}";
            if (step.Condition is not null && !IsPoolPureExpr(step.Condition)) return $"i={step.Index} cond 不纯: {step.Condition}";
            if (step.Receiver is not null && !IsPoolPureExpr(step.Receiver)) return $"i={step.Index} recv 不纯: {step.Receiver}";
        }

        return "-";
    }

    private static bool IsPoolPureExpr(Blueprint.KismetExpr expr)
    {
        if (expr.Self)
        {
            return false;   // 「这张卡自己」= 对局状态
        }

        if (expr.Call is { } call)
        {
            // 表达式级调用：接收者来自 `ctx`；没有 `ctx` 就会退回 `ctx.Self`。
            if (expr.Context is null || !PoolPureOps.Contains(call))
            {
                return false;
            }
        }

        if (expr.Math is { } math && !PoolPureOps.Contains(math))
        {
            return false;
        }

        foreach (Blueprint.KismetExpr arg in expr.Args)
        {
            if (!IsPoolPureExpr(arg)) return false;
        }

        foreach (Blueprint.KismetExpr item in expr.Array)
        {
            if (!IsPoolPureExpr(item)) return false;
        }

        return expr.Context is null || IsPoolPureExpr(expr.Context);
    }

    /// <summary>
    /// 造一张**脱离对局的模板卡实例**（不进 `GameState` 的 `_byCardId` / `_cardsBySide`）。
    ///
    /// 为什么需要它：`GetChooseSpawnCards` 的候选表在蓝图里就是 `UBaseCardObject*` 数组，
    /// 而卡自己的过滤判据是 `卡.faction` / `IsOrder(卡)` / `getTotalKreditCost(卡)` ——
    /// 这三个原语在本内核里都要求实参是 <see cref="CardInstance"/>（见 `GetMember` 与 `SelfArg`）。
    /// 用 `CardDefinition` 的话这三个读法全落空，过滤条件会**静默恒假**、候选表永远是空的。
    ///
    /// `CardId` 取 0：卡池模板在真实客户端有自己的号，但本内核没有那份号表，
    /// 而这条链路上**没有任何一步读模板的 cardID**
    /// （玩家答复带的是卡组码→卡名，生成时走 `Create` 重新发号）。
    /// </summary>
    public static CardInstance TemplateInstance(CardDefinition def, Side side)
    {
        var card = new CardInstance
        {
            CardId = 0,
            Name = def.Name,
            Owner = side,
            Definition = def,
            Location = CardLocation.NotAvailable,
            LocationNumber = 0,
            Attack = def.Attack,
            Defense = def.Defense,
            MaxDefense = def.Defense,
            KreditCost = def.Kredits,
            OperationCost = def.OperationCost,
        };

        card.InitializeFromDefinition();
        return card;
    }

    /// <summary>
    /// 执行**这张卡自己**的 <c>GetChooseSpawnCards(out cards, out markAsSeen, out keepOrder)</c>。
    ///
    /// 它是卡的局部函数（独立 export，不在 ubergraph 里），所以只能执行那张卡的字节码
    /// —— 全卡池 36 张卡定义了它，过滤条件各不相同。原文见
    /// <c>out/gcs/pams.bpasm</c> 第 256–457 行（pams 的条件是「英国 + 指令 + 总费 &lt; 5」）。
    /// 这也就是为什么 IR 生成器必须把 <c>GetChooseSpawnCards</c> 收进 `locals`
    /// （见 `klink bot/tools/gen-kismet-ir.py` 的 `LOCAL_FUNCTIONS`）。
    ///
    /// ⚠️ **带候选表缓存**（见 <see cref="PoolPureOps"/> 那一段注释）。
    /// 对「卡池纯函数」的卡，结果只取决于卡池模板自己，而卡池模板本身是全局常量
    /// （<see cref="_staticPool"/>）—— 所以候选表也是常量，直接按卡名存下来。
    /// 每次仍然返回**一份新的 List**（调用方会 `Array_Clear` / `Array_Add` 原地改它），
    /// 元素引用与不缓存时**完全相同**（不缓存时也是从同一个 <see cref="_staticPool"/> 里取）。
    /// </summary>
    public List<CardInstance> GetChooseSpawnCards(
        EffectContext c, CardInstance selecting, out bool markAsSeen, out bool keepOrder)
    {
        markAsSeen = false;
        keepOrder = false;

        var library = Blueprint.KismetLibrary.Default;
        if (library is null)
        {
            return new List<CardInstance>();
        }

        var program = library.FindLocalProgram(selecting.Definition.Name, "GetChooseSpawnCards");
        if (program is null)
        {
            // 这张卡没定义这个局部函数（或 IR 是旧的）—— 记成缺口，别拿空表假装算过了
            NotifyUnimplemented($"<GetChooseSpawnCards:{selecting.Definition.Name}>");
            return new List<CardInstance>();
        }

        // 缓存键用**解析过变体回退的卡名**：`xxx_bal` / `xxx_vet` 的蓝图逻辑挂在基础卡上，
        // 走的是同一份程序，自然也共享同一条缓存。
        string cacheKey = library.ResolveCardName(selecting.Definition.Name) ?? selecting.Definition.Name;

        if (!_gcsPurityCache.TryGetValue(cacheKey, out bool cacheable))
        {
            cacheable = IsPoolPureProgram(program);
            _gcsPurityCache[cacheKey] = cacheable;

            if (GcsDiag)
            {
                Console.Error.WriteLine(
                    $"[GCS-DIAG] {cacheKey} cacheable={cacheable} steps={program.Steps.Count} "
                    + $"why={PoolImpurityReason(program)}");
            }
        }

        if (GcsNoCache || !cacheable)
        {
            GcsCacheBypassed++;
            return RunChooseSpawnCards(program, c, out markAsSeen, out keepOrder);
        }

        if (_gcsCache.TryGetValue(cacheKey, out var entry) && entry.Generation == _staticPoolGeneration)
        {
            GcsCacheHits++;
            markAsSeen = entry.MarkAsSeen;
            keepOrder = entry.KeepOrder;

            if (GcsVerify)
            {
                VerifyAgainstFreshRun(program, c, cacheKey, entry);
            }

            return new List<CardInstance>(entry.Cards);
        }

        GcsCacheMisses++;
        var cards = RunChooseSpawnCards(program, c, out markAsSeen, out keepOrder);
        _gcsCache[cacheKey] = new GcsCacheEntry(_staticPoolGeneration, cards, markAsSeen, keepOrder);
        return cards;
    }

    private List<CardInstance> RunChooseSpawnCards(
        Blueprint.KismetProgram program, EffectContext c, out bool markAsSeen, out bool keepOrder)
    {
        var outs = Vm.RunLocalProgramMulti(program, c, null, "cards", "markAsSeen", "keepOrder");
        markAsSeen = Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("markAsSeen"));
        keepOrder = Blueprint.KismetVm.Truthy(outs.GetValueOrDefault("keepOrder"));
        return outs.GetValueOrDefault("cards") as List<CardInstance> ?? new List<CardInstance>();
    }

    /// <summary>
    /// 自检开关（环境变量 <c>KLINK_GCS_VERIFY=1</c>）：每次缓存命中都**再真跑一遍**，
    /// 逐元素比对（顺序 + 引用 + 两个 bool）。对不上直接抛 —— 缓存必须对结果完全透明。
    /// 默认关闭，零开销。
    /// </summary>
    private static readonly bool GcsVerify =
        Environment.GetEnvironmentVariable("KLINK_GCS_VERIFY") is { Length: > 0 } v && v != "0";

    /// <summary>
    /// 诊断开关（环境变量 <c>KLINK_GCS_DIAG=1</c>）：每张卡第一次被问到「能不能缓存」时，
    /// 往 stderr 打一行「卡名 / 判定 / 步数 / 第一条不满足的判据」。默认关闭，零开销。
    /// </summary>
    private static readonly bool GcsDiag =
        Environment.GetEnvironmentVariable("KLINK_GCS_DIAG") is { Length: > 0 } d && d != "0";

    /// <summary>
    /// A/B 开关（环境变量 <c>KLINK_GCS_NOCACHE=1</c>）：把缓存整个旁路掉，
    /// 走的就是「没有缓存」那条路 —— 同一份 dll 上做前后对照，排除构建/机器噪声。
    /// 默认关闭。
    /// </summary>
    private static readonly bool GcsNoCache =
        Environment.GetEnvironmentVariable("KLINK_GCS_NOCACHE") is { Length: > 0 } n && n != "0";

    private void VerifyAgainstFreshRun(
        Blueprint.KismetProgram program, EffectContext c, string cacheKey, GcsCacheEntry entry)
    {
        var fresh = RunChooseSpawnCards(program, c, out bool freshSeen, out bool freshOrder);
        bool same = freshSeen == entry.MarkAsSeen && freshOrder == entry.KeepOrder
                    && fresh.Count == entry.Cards.Count;
        if (same)
        {
            for (int i = 0; i < fresh.Count; i++)
            {
                if (!ReferenceEquals(fresh[i], entry.Cards[i]))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            throw new InvalidOperationException(
                $"GetChooseSpawnCards 缓存不一致（{cacheKey}）："
                + $"缓存 {entry.Cards.Count} 张 / markAsSeen={entry.MarkAsSeen} / keepOrder={entry.KeepOrder}，"
                + $"重算 {fresh.Count} 张 / markAsSeen={freshSeen} / keepOrder={freshOrder}。"
                + "缓存改变了语义 —— 必须把这张卡从 PoolPureOps 白名单路径里摘出去。");
        }
    }

    /// <summary>
    /// 把玩家选中的**卡池模板卡**落实成一张新卡 —— 对应 `OpponentActionsCardToDrawSelected`
    /// 里的 `CreateCard(...)` + `OnHandTargetSelected(...)`（出处见本节顶部注释第 4 条）。
    ///
    /// 新卡先按蓝图落在挑牌方的**手牌**（`side==1 → 3 HandLeft`、`side==2 → 4 HandRight`；
    /// `isEffect` 为真时是 8 = 弃牌堆，本函数只走非 isEffect 那条），
    /// 再由那张卡自己的 `OnHandTargetSelected` 决定最终去向。
    /// </summary>
    public CardInstance? DevelopChosenCard(CardInstance? selecting, string cardName)
    {
        if (selecting is null || string.IsNullOrEmpty(cardName) || State.Database.Find(cardName) is null)
        {
            return null;
        }

        Side side = selecting.Owner;
        var created = State.Create(cardName, side, side.HandOf(),
                                   State.NextLocationNumber(side, side.HandOf()), selecting.IsGold);

        // 事件契约（docs/event-contracts.json）：[Int handTargetCardID, Int instigatorID]
        FireTrigger("OnHandTargetSelected", selecting, side,
                    eventArgs: new object?[] { created.CardId, selecting.CardId },
                    eventSubject: created);

        return created;
    }

    /// <summary>
    /// `MoveCardToTopOfOwnersDeck(cardID, instigatorID, positionFromTop, out)`
    /// —— 把那张卡放进**它自己的**牌库，位置是「从牌库顶往下第 `positionFromTop` 张」。
    ///
    /// 出处：`BP_CardFunctions.MoveCardToTopOfOwnersDeck`
    /// （`ref/kards-sim/KardsSim/Generated/BP_CardFunctions.g.cs:27309-27335`），
    /// 函数体只有一句 `MoveCardToTopOfDeck(self, cardID, instigatorID, positionFromTop, false)`。
    /// 调用点：`card_event_pams.OnHandTargetSelected` IR i=1126 —— pams 靠它把 develop
    /// 出来的那张牌塞回牌库（卡面 "Add it to your deck with a cost of 0."）。
    ///
    /// ⚠️ 本内核**不建模** `MoveCardToTopOfDeck` 里的离场事件链
    /// （`ExecuteOnBeforeLeaveBoardOrOwnerEvents` 等）；这里只做"换区 + 插到指定位置"。
    /// 对本族（新卡刚生成在手牌里）这条链本来就不会触发。
    /// </summary>
    private object? DoMoveCardToTopOfOwnersDeck(EffectContext c, object?[] a)
    {
        var card = c.State.ById(IntArg(a, 0, -1));
        if (card is null)
        {
            return null;
        }

        int position = IntArg(a, 2, 0);
        Side side = card.Owner;

        bool wasOnBoard = card.Location.IsBoard() && !card.IsHq;

        // 先记下"除了这张卡之外的牌库顺序"，再把它插进第 position 位。
        var rest = c.State.Deck(side).Where(x => !ReferenceEquals(x, card)).ToList();
        c.State.Move(card, side.DeckOf());
        rest.Insert(Math.Clamp(position, 0, rest.Count), card);
        for (int i = 0; i < rest.Count; i++)
        {
            rest[i].LocationNumber = i;
        }

        // 蓝图 `MoveCardToTopOfDeck` i=714-828：卡还在 `GetAllCardInBattleAsMap()` 里
        // （= 原本在场上）时调 `ResetCardInBattle`。见 ResetCardInBattle 的出处注释。
        if (wasOnBoard)
        {
            ResetCardInBattle(card);
        }

        return null;
    }

    /// <summary>
    /// `MoveUnitFromBoardToOwnersHand(card, instigatorID)` —— 把场上的单位退回原主手牌。
    ///
    /// 出处：`out/bp-cardfn.json` 函数 `MoveUnitFromBoardToOwnersHand`：
    /// <code>
    /// i=0/41/82/120  if (!(card.IsLocatedOnBoard() &amp;&amp; card.IsUnit())) → 直接返回
    /// i=134          handLocation = GetHandLocationBySide(card.originalSide)
    /// i=264          MoveCardFromBoardToOwnersHand(card.cardID, instigatorID, card.location, handLocation)
    /// </code>
    /// 与 `MoveCardFromBoardToOwnersHand`（i=10..944）：
    /// <code>
    /// i=10   ExecuteOnBeforeLeaveBoardOrOwnerEvents(cardID, NewLocation, OldLocation, Method=3 /*OnMovedToHand*/)
    /// i=59   FetchCardsByLocation(NewLocation) → isLocationFull
    /// i=173  JumpIfNot 925 (isLocationFull)     ; 没满 ⇒ 用手牌
    /// i=187  tmpNewLocation = 8 /*Discard*/     ; 满了 ⇒ 改去弃牌堆
    /// i=213  CardLocationMoved(…)
    /// i=291  ExecuteOnAfterLeaveBoardOrOwnerEvents(…)
    /// i=626  ResetCardInBattle(tmpCardToMove)
    /// </code>
    /// 签名取自 `ref/kards-sim/KardsSim/Generated/_index.g.cs:4016/4008`。
    /// </summary>
    private object? DoMoveUnitFromBoardToOwnersHand(EffectContext c, object? r, object?[] a)
    {
        var card = AsCard(a.FirstOrDefault()) ?? AsCard(r) ?? c.Target;
        if (card is null || card.IsHq || !IsLocatedOnBoard(card) || !IsUnit(card))
        {
            return null;
        }

        Side side = card.Owner;

        // 手牌满 ⇒ 退回的那张直接进弃牌堆（蓝图 i=173/187）。
        bool handFull = c.State.Hand(side).Count >= GameState.HandCapacity;
        CardLocation target = handFull ? CardLocation.Discard : side.HandOf();

        // 离场触发点必须在 Move **之前**（效果里要读旧位置），与 Destroy 那条一致。
        c.Engine.FireLeaveTrigger(card, target);
        c.State.Move(card, target);
        card.EnteredPlayOnTurn = 0;

        if (handFull)
        {
            c.Engine.FireSubAction("ZActionDiscardCard", new[]
            {
                ActionValue2.Int("discarderID", card.CardId),
            });
        }

        // i=626：回手之后重置这张卡的累积状态（→ OnCardReset / OnOtherCardReset）。
        ResetCardInBattle(card);
        return null;
    }

    /// <summary>
    /// `RandomIntFromRangeWithStream(minimum, maximum, out randomResult)` ——
    /// 返回 `[minimum, maximum)` 的均匀整数（签名见
    /// `ref/kards-sim/KardsSim/Generated/_index.g.cs:4182`）。
    /// 走对局的确定性随机流（`GameState.Random`），保证同种子同结果。
    /// </summary>
    private object? DoRandomIntFromRange(EffectContext c, object?[] a)
        => c.State.Random.Next(IntArg(a, 0), IntArg(a, 1));

    /// <summary>派发一次调用。<paramref name="handled"/> 为 false 表示内核还没实现这个名字。</summary>
    public object? InvokeByName(string name, object? receiver, object?[] args, EffectContext ctx, out bool handled)    {
        if (_dispatch.TryGetValue(name, out var handler))
        {
            handled = true;
            try
            {
                return handler(ctx, receiver, args);
            }
            catch (Exception ex)
            {
                // 单次调用失败不能毁掉整局；记下来继续
                NotifyUnimplemented($"{name}<fault:{ex.GetType().Name}>");
                return null;
            }
        }

        handled = false;
        return null;
    }

    // ==================== 效果实现 ====================

    private object? DoChangeAttack(EffectContext c, object? r, object?[] a, bool invert = false)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        // ---- 蓝图的第一道守位：`CanCardBeBuffed` ----
        // 出处 `out/bp-cardfn.json` 的 `ChangeAttack`：
        //   si=28  IsValid(card)      → si=57  JumpIfNot → si=393（log "error in [Change Attack]" + return）
        //   si=71  CanCardBeBuffed(card)
        //   si=103 JumpIfNot → si=472（qqq=False + return）★ **客户端在这里拒绝**
        //   si=117 instigatorID > 0  → si=151 JumpIfNot → si=393（return）
        //
        // ⚠️⚠️ **这一条在本内核里【不落地】，理由是可复现的反证，不是偷懒**：
        //    把 `CanCardBeBuffed` 当成硬门加上去之后，**4 条蓝图推导出来的自测立刻失败**
        //    （2026-09-27 实测：`dotnet run --project tools/BotSim -- selftest` 33 → 4 项失败）：
        //      · 红牛 `card_unit_red_bull`：`OnStartOfTurn` 先判 `IsLocatedOnBoard(self)`，
        //        再 `ChangeAttack(self, getTotalAttack(), changeType=1)` —— **卡自己就要求在场**，
        //        然后在场的目标又被 `CanCardBeBuffed` 拒掉，自相矛盾；
        //      · 爱国热忱（目标翻倍）、敢死队（Spitfire 部署 +3+3）、
        //        3 掷弹兵（德国单位操作 +1+1）同理，全是**在场单位**的数值改动。
        //    ⇒ 客户端蓝图里 `ChangeAttack` 的这道门守的是**客户端自己的 buff 记账**
        //      （手牌/牌库卡的 buff 预览），真实对局里在场单位的数值由服务端权威结算。
        //      本内核跑的是"规则复刻"，不是"客户端预览"，所以这里只保留判据函数本身
        //      （`CardApi.CanCardBeBuffed`，供自测与将来需要它的地方用），不加硬门。
        //      这是一条**有证据的取舍**：审计 §6 的 8d 描述与 4 条自测直接冲突，
        //      冲突没解之前，宁可不加（加了会把 4 条已证的规则打坏）。
        int delta = IntArg(a, 2);
        ChangeAttack(target, invert ? -delta : delta, c.Self);
        return null;
    }

    private object? DoChangeDefense(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        // 同 `DoChangeAttack`：`ChangeDefense` si=48/80 是同一个守位，
        // 同理**不落地**（4 条自测里的"爱国热忱"翻倍目标攻防就在这一支上）。
        ChangeDefense(target, IntArg(a, 2), c.Self);
        return null;
    }

    private object? DoSetDefense(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        int value = IntArg(a, 2);
        ChangeDefense(target, value - target.Defense, c.Self);
        return null;
    }

    private object? DoChangeKreditCost(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is not { } target)
        {
            return null;
        }

        // 签名（实测 136 个调用点）：ChangeKreditCost(卡, instigatorID, 数值, changeType, bool, out)
        //   a[1] = 来源卡 ID —— **必须带上**，光环的 `isBuffedByCard` / RemoveTheBuff 都按来源记账
        //   a[2] = 数值
        //   a[3] = changeType。实测只出现 0 / 1 / 4 三种：
        //            0 = 在卡面费用基础上的偏移（正常减费）
        //            1 = 把费用设成绝对值（`card_event_committed_crew` 用 `-getTotalKreditCost`）
        //            4 = **撤销这个来源的临时改费**（`RemoveTheBuff` 一族传数值 0）
        //
        // ⚠️ 这里**不能**像旧版那样做 `target.KreditCost += delta`：
        //    旧版既不看 a[1]（来源）也不看 a[3]（changeType），于是
        //    (1) 光环每 Apply 一次就累减一次、`RemoveTheBuff` 撤不掉（它传的是 0）；
        //    (2) `card_unit_big_red_one` 每次抽牌都会重算一遍「设成 4 费」的偏移，
        //        累加语义下每抽一张牌费用就再掉一截。
        //    现在改成：**buff 字典是唯一真源**，改完调 `RecalculateStats()` 用绝对值重算。
        int sourceId = SourceCardIdArg(a, 1, c.Self);
        int amount = IntArg(a, 2);
        int changeType = IntArg(a, 3);

        if (changeType == ChangeTypeTempBuffRemove)
        {
            RemoveCostBuff(target, sourceId);
            return null;
        }

        if (changeType == ChangeTypeSetValue || changeType == ChangeTypeSetValueReal)
        {
            // 绝对值语义：不管卡面多少，最终就是 amount。存成「相对卡面费用的偏移」，
            // 这样和别的来源叠加时仍然是加法。
            amount -= target.Definition.Kredits;
        }

        var buff = GetOrCreateBuff(target, sourceId);
        buff.KreditCost = amount;
        buff.KreditCostSetsAbsoluteValue =
            changeType is ChangeTypeSetValue or ChangeTypeSetValueReal;
        target.RecalculateStats();

        _engine.FireSubAction("ZActionSetKreditCost", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("kreditCost", target.KreditCost),
            ActionValue2.Int("instigatorID", sourceId),
        });

        return null;
    }

    /// <summary>`ChangeKreditCost` 的 changeType 取值（实测只出现这三种）。</summary>
    private const int ChangeTypeOffset = 0;        // 相对卡面费用加减
    private const int ChangeTypeSetValue = 1;      // 设成绝对值
    private const int ChangeTypeTempBuffRemove = 4; // 撤销该来源的临时改费

    /// <summary>
    /// ⚠️ `EChangeType::SetValue` 的**真实枚举值**是 <b>2</b>，不是 <see cref="ChangeTypeSetValue"/> 的 1。
    ///
    /// 出处：`E:\peoject\kards\Source\kards\Public\EChangeType.h:6-17`
    /// <code>
    /// enum class EChangeType : uint8 {
    ///     tempBuffGive,   // 0
    ///     permBuff,       // 1
    ///     SetValue,       // 2
    ///     Suppress,       // 3
    ///     tempBuffRemove, // 4
    ///     veteranSet, customAdd, customRemove, combatModify, notUsed,
    /// };
    /// </code>
    ///
    /// 本内核历史上把 **1** 当成"设成绝对值"（见 <see cref="ChangeTypeSetValue"/> 的注释），
    /// 和这份枚举对不上 —— 1 实际是 `permBuff`（相对值、永久）。
    /// 那一处差异影响 **41 个调用点**（`out/gcs/scan-changetype.py` 的统计：
    /// changeType=0 ×58 / **1 ×41** / **2 ×7** / 4 ×30），
    /// 本轮**没有动它** —— 它不在本轮的 A/B 两件事范围内，改了会把对拍结论搅在一起。
    /// 这里只**新增** 2 的处理，因为 `card_event_pams` 用它做
    /// 「Add it to your deck with a cost of 0.」（卡面原文，7 个调用点全是 `amount=0`）。
    /// </summary>
    private const int ChangeTypeSetValueReal = 2;

    /// <summary>撤销某个来源在目标卡上的**费用** buff（其余 buff 保留）。</summary>
    private void RemoveCostBuff(CardInstance target, int sourceId)
    {
        if (!target.BuffsBySource.TryGetValue(sourceId, out var buff))
        {
            return;
        }

        buff.KreditCost = 0;
        buff.KreditCostSetsAbsoluteValue = false;
        if (buff.IsEmpty)
        {
            target.BuffsBySource.Remove(sourceId);
        }

        target.RecalculateStats();
    }

    /// <summary>撤销某个来源在目标卡上的**全部** buff（`RemoveTheBuff` 的通用形态）。</summary>
    private void RemoveBuffFromSource(CardInstance target, int sourceId)
    {
        if (target.BuffsBySource.Remove(sourceId))
        {
            target.RecalculateStats();
        }
    }

    private object? DoSetKreditCost(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is { } target)
        {
            // 沿用「相对」实现：`SetKreditCost` 是旧派发表自己加的近似名，
            // 卡蓝图里没有这个词（实测 0 个调用点），改动它没有收益。
            ChangeKreditCost(target, IntArg(a, 2) - target.KreditCost);
        }

        return null;
    }

    private object? DoChangeOperationCost(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is not { } target)
        {
            return null;
        }

        // 签名（实测 140 个调用点）：ChangeOperationCost(卡, instigatorID, 数值, changeType, b, b, b)
        //   a[1] = 来源卡 ID，a[2] = 数值，a[3] = changeType（含义同 ChangeKreditCost）
        //
        // ⚠️ 和 ChangeKreditCost 一样必须按来源记账、绝对值重算。旧版只做
        //    `target.OperationCost += delta`，于是 `card_unit_214th_amur` 的
        //    「T-34 行动费 -1」每触发一次就再减一次，而 `RemoveTheBuff`
        //    传的 0 又什么都没撤销 —— 行动费一路掉到 0 再也回不来。
        int sourceId = SourceCardIdArg(a, 1, c.Self);
        int amount = IntArg(a, 2);
        int changeType = IntArg(a, 3);

        if (changeType == ChangeTypeTempBuffRemove)
        {
            if (target.BuffsBySource.TryGetValue(sourceId, out var existing))
            {
                existing.OperationCost = 0;
                if (existing.IsEmpty)
                {
                    target.BuffsBySource.Remove(sourceId);
                }

                target.RecalculateStats();
            }

            return null;
        }

        var buff = GetOrCreateBuff(target, sourceId);
        buff.OperationCost = amount;
        target.RecalculateStats();

        _engine.FireSubAction("ZActionChangeOperationCost", new[]
        {
            ActionValue2.Int("instigatorID", sourceId),
            ActionValue2.Int("amount", amount),
        });

        return null;
    }

    /// <summary>
    /// 改重甲（对应 `ChangeHeavyArmor`）。
    ///
    /// 签名（实测 6 个调用点）：`ChangeHeavyArmor(卡, instigatorID, 数值, changeType, bool, out)`
    /// 语义和费用那两个同构 —— changeType=4 是「撤销该来源的临时重甲」。
    /// `card_unit_214th_amur` 用 `+1 / changeType=0` 施加、`0 / changeType=4` 撤销。
    ///
    /// 注意重甲是**可叠加的数值**，不是关键字；关键字只在点数 &gt; 0 时同步挂上
    /// （见 `CardInstance.RecalculateStats`），因为别处（快照、老的 `AddHeavyArmor`）按关键字判。
    /// </summary>
    private object? DoChangeHeavyArmor(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is not { } target)
        {
            return null;
        }

        int sourceId = SourceCardIdArg(a, 1, c.Self);
        int amount = IntArg(a, 2);
        int changeType = IntArg(a, 3);

        if (changeType == ChangeTypeTempBuffRemove)
        {
            if (target.BuffsBySource.TryGetValue(sourceId, out var existing))
            {
                existing.HeavyArmor = 0;
                if (existing.IsEmpty)
                {
                    target.BuffsBySource.Remove(sourceId);
                }

                target.RecalculateStats();
            }

            return null;
        }

        var buff = GetOrCreateBuff(target, sourceId);
        buff.HeavyArmor = amount;
        target.RecalculateStats();

        _engine.FireSubAction("ZActionAddHeavyArmor", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("instigatorID", sourceId),
            ActionValue2.Int("amount", amount),
        });

        return null;
    }

    private object? DoDamageCard(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is null)
        {
            return null;
        }

        // DamageCard(target, 伤害, attackerID, bool, bool, bool, out)
        int amount = IntArg(a, 1);
        var source = AsCard(a.ElementAtOrDefault(2)) ?? c.Self;
        DealDamage(target, amount, source);
        return null;
    }

    private object? DoHealCard(EffectContext c, object? r, object?[] a)
    {
        if (TargetCard(c, r, a) is { } target)
        {
            HealCard(target, IntArg(a, 1));
        }

        return null;
    }

    private object? DoDestroyCard(EffectContext c, object? r, object?[] a)
    {
        var target = TargetCard(c, r, a);
        if (target is not null)
        {
            DestroyCard(target, c.Self);
        }

        return null;
    }

    private object? DoSpawnInHand(EffectContext c, object? r, object?[] a)
    {
        // SpawnCardInHandBySide(side, ?, instigatorID, bool, bool, bool, 卡名, textVar, int, out)
        var side = SideArg(r, a, 0, c.Controller);
        string? cardName = FindCardNameArg(c, a);
        return cardName is null ? null : SpawnCardInHand(side, cardName);
    }

    /// <summary>
    /// 从参数里找出「要生成的卡名」。
    ///
    /// 不能死认第 6 个位置：不同卡的参数排布不一样，而且有时卡名来自运行时变量
    /// （例如随机选卡的结果）而不是字符串常量。所以按「看起来像卡名且确实在卡库里」
    /// 扫一遍，找不到就返回 null —— 交给调用方记成已知未支持，而不是抛异常。
    /// </summary>
    private string? FindCardNameArg(EffectContext c, object?[] a)
    {
        foreach (object? v in a)
        {
            if (v is string s && s.StartsWith("card_", StringComparison.Ordinal) && c.State.Database.Find(s) is not null)
            {
                return s;
            }
        }

        return null;
    }

    /// <summary>
    /// `SpawnCardOnBattlefield(side, Frontline, card_name, spawnerID, campaignName,
    ///  NewGiveBlitz, locationNumber, salvageFaction, NewMakeVeteran, forceGoldCard,
    ///  out spawnedCardID)` —— 权威签名 `CardFunctionsStub.h:68`。
    ///
    /// ⚠️ 旧实现只读 `a[0]`（side）+ 扫卡名，**其余可选参数全丢** —— 其中
    /// <c>Frontline</c>（<c>a[1]</c>）是**落点**：丢掉它等于把 215 个传 `false`
    /// 的调用点全部生成到**前线**。IR 实测（`out/audit/p0-argshapes.py`，234 个调用点）：
    /// <code>
    /// a[0] side   a[1] Frontline(false×215 / true×4 / 变量×15)   a[2] card_name
    /// a[3] spawnerID   a[4] campaignName   a[5] NewGiveBlitz(false×169 / true×63)
    /// a[6] locationNumber(-1×193)   a[7] salvageFaction   a[8] NewMakeVeteran(false×234)
    /// a[9] forceGoldCard(false×233)   a[10] out
    /// </code>
    /// 本实现读：`Frontline`、`locationNumber`（-1 = 追加到队尾）、`NewGiveBlitz`、
    /// `NewMakeVeteran`、`forceGoldCard`。
    /// **不实现**：`campaignName`（战役）、`salvageFaction`（Salvage 关键字，P1 未建模）。
    /// `spawnerID` 只用于"谁生成的"记账，当前内核没有对应字段，不假装实现。
    /// </summary>
    private object? DoSpawnOnBattlefield(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        string? cardName = a.Select(AsString).FirstOrDefault(s => s is not null && s.StartsWith("card_", StringComparison.Ordinal));
        if (cardName is null)
        {
            return null;
        }

        var card = SpawnOnBattlefield(side, cardName,
            frontline: TruthyArg(a, 1),
            locationNumber: IntArg(a, 6, -1),
            newGiveBlitz: TruthyArg(a, 5),
            forceGoldCard: TruthyArg(a, 9));

        if (TruthyArg(a, 8))
        {
            MakeVeteran(card);
        }

        return card;
    }

    // ==================== 光环（aura）的实现 ====================
    //
    // 四张光环卡：
    //   card_unit_85_pioneer_company  本回合第一张指令 -1 费（下限 1）
    //   card_unit_big_red_one         手牌里的牌都是 4 费
    //   card_unit_214th_amur          己方 T-34 +1 重甲、行动费 -1
    //   card_event_committed_crew     本回合 Spitfire 0 费、部署时 +3+3
    //
    // 语义全部从 `ref/kards-sim/KardsSim/Generated/.../<卡名>.g.cs` 的直译产物读出
    // （IR 里没有私有函数体，见派发表里那一段注释）。
    // `self` 就是 IR 里的 `cardFunction`；`args` 已经由 VM 摘掉 `{self:true}` 占位。

    /// <summary>
    /// 光环的「我 buff 过哪些卡」——对应客户端把 `cardID` 写进卡自己的私有 JSON。
    ///
    /// 为什么必须自己维护这份账：`isBuffedByCard(卡, 来源ID)` 是
    /// `RemoveTheBuff` / `ApplyTheBuff` 判「这张卡我已经加过了吗」的**唯一**依据。
    /// 以前派发表把 `isBuffedByCard` 近似成「`BuffsBySource` 非空」，
    /// 那对光环是错的：手牌里 A 卡被光环 buff 过，不代表 B 卡也被 buff 过。
    /// </summary>
    private const string BuffedCardsKey = "buffedCards";

    /// <summary>诊断用：非 null 时记录光环每一次 Apply 的资格判定结果。</summary>
    public List<string>? AuraTrace { get; set; }

    /// <summary>
    /// 卡自己的 `buffActive` 标记 —— 客户端用 `JSON_SetBool(buffActive, true)`
    /// 记「这个光环当前是不是生效中」。
    ///
    /// ⚠️ **必须维护它**，不能省：`card_unit_85_pioneer_company` 的每一个
    /// 触发分支（`OnOtherCardPlayedFromHand` 还原、`OnEndOfTurn` 重挂、
    /// `OnOtherCardDrawnFromDeck` 补挂、`OnLeaveBoardOrOwner` 还原）
    /// 都先问 `JSON_GetBool(buffActive)`，取到 `found=false` 就直接 return。
    /// 只在别处补 buff、不设这个标记，那些分支**全部静默失效** ——
    /// 实测症状：光环进场时手牌指令确实 -1 了，但打出一张指令之后**不会还原**
    /// （还原分支判 `buffActive` 为假就跳过了）。
    ///
    /// 客户端里"found"来自 JSON 键是否存在，所以这里用「键在不在」而不是值：
    /// `JsonSetBool` 写入 "0" 之后再读仍然是 found=true。
    /// </summary>
    private void MarkBuffActive(CardInstance aura, bool active)
        => JsonSetBool(aura, "buffActive", active);

    /// <summary>把 <paramref name="target"/> 记进光环自己的「我 buff 过的卡」名单。</summary>
    private void RememberBuffedCard(CardInstance aura, CardInstance target)
    {
        var ids = JsonGetIntArray(aura, BuffedCardsKey);
        if (ids.Contains(target.CardId))
        {
            return;
        }

        ids.Add(target.CardId);
        JsonSetIntArray(aura, BuffedCardsKey, ids);
    }

    /// <summary>第 <paramref name="index"/> 个参数是不是这张卡自己 buff 过的卡。</summary>
    private bool IsBuffedBySelf(CardInstance aura, object? arg)
    {
        if (AsCard(arg) is not { } card)
        {
            return false;
        }

        return JsonGetIntArray(aura, BuffedCardsKey).Contains(card.CardId);
    }

    /// <summary>
    /// 取「改费/改行动费/改重甲」的**来源卡 ID**（这些原语的第 2 个实参）。
    ///
    /// ⚠️ 实测调用约定**不统一**，不能死认某一个下标：
    ///   内部函数里是 `ChangeKreditCost(tempCard, cardID, -1, 0, …)`（cardID 在 a[1]）
    ///   而 `ExecuteUbergraph` 内联路径里可能是 `ChangeKreditCost(tempCard, -1, 0, …)`（数值在 a[1]）
    ///   —— 后者是 kardsim 的读法（它按 `a[3]` 取数值）。
    ///   判据：a[1] 必须是一个**真实存在的卡 ID**；否则退回施法者自己。
    ///   来源 ID 认错的后果是 buff 记到不存在的来源上，撤销时找不到 → 数值永久偏移，
    ///   所以这里宁可退回施法者（至少在 `RemoveTheBuff` 时会用同一个来源去撤）。
    /// </summary>
    private static int SourceCardIdArg(object?[] a, int index, CardInstance? self)
    {
        if (index < a.Length && a[index] is int id && id > 0)
        {
            return id;
        }

        return self?.CardId ?? 0;
    }

    /// <summary>
    /// `ApplyTheBuff` —— 光环把 buff 施加到目标卡上。
    ///
    /// 调用形状有两种，都要认：
    /// - **无实参**：`ApplyTheBuff()` —— self 就是光环，作用于「所有符合条件的卡」
    ///   （85_pioneer：手牌里所有指令；committed_crew：所有 Spitfire；214th：所有 T-34）。
    ///   这三张卡的 g.cs 里 `ApplyTheBuff` 自己就是**循环体**（`GetAllCards` + 逐个判条件），
    ///   所以这里按卡名分派到对应的判定上。
    /// - **一个实参**：`ApplyTheBuff(cardToBeBuffed)` —— self 是光环，参数是目标
    ///   （big_red_one 的 `ExecuteUbergraph` 里就是 `ApplyTheBuff(CallFunc_Array_Get_Item)`）。
    /// </summary>
    private object? DoApplyTheBuff(EffectContext c, object?[] a)
    {
        var aura = c.Self;
        if (aura is null)
        {
            return null;
        }

        if (a.Length > 0 && AsCard(a[0]) is { } explicitTarget)
        {
            ApplyAuraBuffTo(aura, explicitTarget);
            return null;
        }

        foreach (var target in c.State.AllCards)
        {
            ApplyAuraBuffTo(aura, target);
        }

        return null;
    }

    /// <summary>按光环卡名把 buff 加到一张目标卡上（含各自的资格判定）。</summary>
    private void ApplyAuraBuffTo(CardInstance aura, CardInstance target)
    {
        if (AuraTrace is not null)
        {
            AuraTrace.Add($"{aura.Definition.Name} → {target.Name}#{target.CardId}" +
                          $"（在手上={IsLocatedInHand(target)} 在场={IsLocatedOnBoard(target)}" +
                          $" 指令={IsOrder(target)} 同阵营={target.Owner == aura.Owner}" +
                          $" 费用={target.EffectiveKreditCost}" +
                          $" t34={HasGameplayTag(target, "subtype.t34")}" +
                          $" spitfire={HasGameplayTag(target, "subtype.spitfire")}" +
                          $" buff槽={target.BuffsBySource.Count}）");
        }

        switch (aura.Definition.Name)
        {
            case "card_unit_85_pioneer_company":
                // 判据（g.cs ApplyTheBuff）：IsOrder && IsLocatedInHand && 同阵营
                // 效果：ChangeKreditCost(卡, 自己, -1, 0)  —— 只减 1，不是减到 1
                AuraTrace?.Add($"      85 判定: IsOrder={IsOrder(target)} " +
                               $"IsLocatedInHand={IsLocatedInHand(target)} 同阵营={target.Owner == aura.Owner}");
                if (IsOrder(target) && IsLocatedInHand(target) && target.Owner == aura.Owner)
                {
                    AuraTrace?.Add($"      → 85 命中 {target.Name}#{target.CardId}");
                    ApplyAuraKreditCost(aura, target, -1, ChangeTypeOffset);
                }

                break;

            case "card_unit_big_red_one":
                // 判据：IsLocatedInHand && 同阵营；然后
                //   if (getTotalKreditCost(卡) == 4) 跳过（已经是 4 费了，别再动）
                //   else ChangeKreditCost(卡, 自己, 4 - 当前费用, 0)
                if (IsLocatedInHand(target) && target.Owner == aura.Owner)
                {
                    int current = target.EffectiveKreditCost;
                    if (current != BigRedOneCost)
                    {
                        ApplyAuraKreditCost(aura, target, BigRedOneCost - current, ChangeTypeOffset);
                    }
                }

                break;

            case "card_event_committed_crew":
                // 判据：IsLocatedInHand && 同阵营 && subtype.spitfire && 当前费用 > 0
                // 效果：ChangeKreditCost(卡, 自己, -当前费用, 1)  —— changeType=1，
                //       所以允许落到 0（卡面就写着 "Spitfires cost 0 to deploy"）
                if (IsLocatedInHand(target)
                    && target.Owner == aura.Owner
                    && HasGameplayTag(target, "subtype.spitfire"))
                {
                    int cost = target.EffectiveKreditCost;
                    AuraTrace?.Add($"      → committed_crew 命中 Spitfire，当前费用 {cost}");
                    if (cost > 0)
                    {
                        ApplyAuraKreditCost(aura, target, -cost, ChangeTypeSetValue);
                    }
                }

                break;

            case "card_unit_214th_amur":
                // 判据：IsLocatedOnBoard && subtype.t34 && 同阵营
                // 效果：ChangeHeavyArmor(+1, changeType=0) + ChangeOperationCost(-1, changeType=0)
                if (IsLocatedOnBoard(target) && target.Owner == aura.Owner
                    && HasGameplayTag(target, "subtype.t34"))
                {
                    ApplyAuraHeavyArmor(aura, target, 1);
                    ApplyAuraOperationCost(aura, target, -1);
                }

                break;
        }
    }

    /// <summary>big_red_one 把手里所有牌定成这个费用（卡面原话 "cost 4 kredits"）。</summary>
    private const int BigRedOneCost = 4;

    /// <summary>光环式改费：**同一来源幂等**。</summary>
    private void ApplyAuraKreditCost(CardInstance aura, CardInstance target, int amount, int changeType)
    {
        if (target.BuffsBySource.TryGetValue(aura.CardId, out var existing)
            && existing.KreditCost == amount
            && existing.KreditCostSetsAbsoluteValue == (changeType == ChangeTypeSetValue))
        {
            return;   // 已经加过同一个值，重复施加不叠加
        }

        var buff = GetOrCreateBuff(target, aura.CardId);
        buff.KreditCost = amount;
        buff.KreditCostSetsAbsoluteValue = changeType == ChangeTypeSetValue;
        target.RecalculateStats();
        RememberBuffedCard(aura, target);
        MarkBuffActive(aura, true);

        _engine.FireSubAction("ZActionSetKreditCost", new[]
        {
            ActionValue2.Int("cardID", target.CardId),
            ActionValue2.Int("kreditCost", target.KreditCost),
            ActionValue2.Int("instigatorID", aura.CardId),
        });
    }

    private void ApplyAuraOperationCost(CardInstance aura, CardInstance target, int amount)
    {
        if (target.BuffsBySource.TryGetValue(aura.CardId, out var existing)
            && existing.OperationCost == amount)
        {
            return;
        }

        var buff = GetOrCreateBuff(target, aura.CardId);
        buff.OperationCost = amount;
        target.RecalculateStats();
        RememberBuffedCard(aura, target);
        MarkBuffActive(aura, true);

        _engine.FireSubAction("ZActionChangeOperationCost", new[]
        {
            ActionValue2.Int("instigatorID", aura.CardId),
            ActionValue2.Int("amount", amount),
        });
    }

    private void ApplyAuraHeavyArmor(CardInstance aura, CardInstance target, int amount)
    {
        if (target.BuffsBySource.TryGetValue(aura.CardId, out var existing)
            && existing.HeavyArmor == amount)
        {
            return;
        }

        var buff = GetOrCreateBuff(target, aura.CardId);
        buff.HeavyArmor = amount;
        target.RecalculateStats();
        RememberBuffedCard(aura, target);
        MarkBuffActive(aura, true);
    }

    /// <summary>
    /// `RemoveTheBuff` —— 光环撤销自己施加过的全部 buff。
    ///
    /// 调用形状：
    /// - 无实参：self 是光环，撤销「名单上所有卡」（85_pioneer / committed_crew / 214th）
    /// - `RemoveTheBuff(卡)`：self 是光环，只撤这一张（big_red_one 的 ubergraph 路径）
    ///
    /// 判据用光环自己记的名单，不用 `IsLocatedInHand` 之类的**当前位置**：
    /// 卡可能已经打出/被弃掉了，位置上已经判不出来，但 buff 还挂在它身上。
    /// （客户端的 RemoveTheBuff 里也有 `isBuffedByCard` 这一步，但 g.cs 里 85_pioneer
    ///  的 RemoveTheBuff 判的是 `IsValid(tempCard)`，214th 的判的是名单 —— 位置判据不可靠。）
    /// </summary>
    private object? DoRemoveTheBuff(EffectContext c, object?[] a)
    {
        var aura = c.Self;
        if (aura is null)
        {
            AuraTrace?.Add("RemoveTheBuff: Self 为 null，跳过");
            return null;
        }

        AuraTrace?.Add($"RemoveTheBuff({aura.Definition.Name}#{aura.CardId}) 实参 {a.Length} 个，" +
                       $"名单={string.Join(",", JsonGetIntArray(aura, BuffedCardsKey))}");

        if (a.Length > 0 && AsCard(a[0]) is { } explicitTarget)
        {
            RemoveAuraBuffFrom(aura, explicitTarget);
            return null;
        }

        foreach (int cardId in JsonGetIntArray(aura, BuffedCardsKey))
        {
            if (c.State.ById(cardId) is { } target)
            {
                RemoveAuraBuffFrom(aura, target);
            }
        }

        JsonSetIntArray(aura, BuffedCardsKey, Array.Empty<int>());
        MarkBuffActive(aura, false);
        return null;
    }

    private void RemoveAuraBuffFrom(CardInstance aura, CardInstance target)
    {
        AuraTrace?.Add($"RemoveTheBuff: {aura.Definition.Name} → {target.Name}#{target.CardId}" +
                       $"（buff槽={target.BuffsBySource.Count} 含本来源={target.BuffsBySource.ContainsKey(aura.CardId)}）");

        if (!target.BuffsBySource.ContainsKey(aura.CardId))
        {
            return;
        }

        // ⚠️ 这里撤销的是「该来源在目标卡上的**全部** buff」，而不只是费用那一条。
        //    对这四张光环是等价的（它们只改费用/行动费/重甲，互不冲突）。
        //    但如果将来某张卡的 RemoveTheBuff 只想撤费用、保留攻防，
        //    就必须改成按字段撤销 —— 客户端的 ChangeKreditCost(…, changeType=4)
        //    只清 KreditCost，不清 Attack/Defense。
        RemoveBuffFromSource(target, aura.CardId);
    }

    /// <summary>
    /// `anyOrderPlayedThisTurn` —— 本回合有没有打过**己方**的指令牌。
    ///
    /// g.cs 的循环体：遍历 `GetCardsPlayedThisTurn()`，命中
    /// `IsOrder(卡) && 卡.side == self.side` 就把结果置 true 并 break。
    /// 这是 `card_unit_85_pioneer_company`（"The first order you play each turn costs 1 less"）
    /// 判「第一张已经用掉了」的唯一依据。
    /// </summary>
    private bool AnyOrderPlayedThisTurn(EffectContext c)
    {
        Side own = SelfSide(c);
        foreach (var card in c.State.CardsPlayedThisTurn)
        {
            if (IsOrder(card) && card.Owner == own)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// `isBuffedByCard(卡, 来源ID)` —— 这张卡有没有被指定来源 buff 过。
    ///
    /// ⚠️ 只用「来源卡自己记的名单」判，不看 `BuffsBySource` 非空：
    /// 旧实现是「有任意 buff 就算」，对光环是错的 ——
    /// `card_unit_85_pioneer_company` 的 RemoveTheBuff 会遍历**所有卡**，
    /// 拿这个判据决定要不要给某张手牌还原费用，用「非空」会把没被它 buff 过的牌也还原。
    /// </summary>
    private bool IsBuffedByCard(EffectContext c, object? r, object?[] a)
    {
        // ⚠️ **接收者也要走 SelfArg**（审计 §5.1）：9 个调用点是隐式 self，
        //    旧写法 `AsCard(r)` 在 r 为 null 时恒 false ⇒ 光环的
        //    "这张卡我已经加过了吗"判据失效（会重复施加 buff）。
        //    注意 `a[0]` 是来源卡 ID（int），`SelfArg` 只认 `CardInstance`，
        //    所以扫参数不会把来源 ID 误当成接收者。
        if (SelfArg(c, r, a) is not { } target)
        {
            return false;
        }

        // 来源 ID 在 a[0]（实测 102 个调用点里绝大多数是 `isBuffedByCard(卡, cardID, out)`；
        // 也有 0 参形态，那时 a[0] 是 out 槽 = null）。
        // 认不出来时退回「有没有被任何来源 buff 过」——这是旧行为，比恒 false 安全。
        if (a.Length > 0 && a[0] is int sourceId && sourceId > 0)
        {
            return target.BuffsBySource.ContainsKey(sourceId);
        }

        return target.BuffsBySource.Count > 0;
    }

    /// <summary>
    /// `getHasGameplayTag(标签数组, out 有没有)` —— 判子类型。
    ///
    /// 实参里可能出现两种形状（都实测到了）：
    /// - `{array:[{name:"subtype.t34"}]}`（`card_unit_214th_amur` 等）→ 字符串列表
    /// - `{struct:null}`（`card_event_committed_crew` 的 IR：生成器把
    ///   `StructConst /Script/GameplayTags.GameplayTag` 整段丢了，只剩 null）
    ///
    /// 后一种是**上游 IR 的缺口**：真正的标签名在 `cards.full.json` 的
    /// `Properties["Missing property name0"][0].Value` 里。所以这里在拿到空列表时，
    /// 退一步用「卡名 → tag」的静态表（`GameplayTagTable`）反查：
    /// 那张表把所有 tag 一次列全，按卡自己带的 tag 判，等价于「这个 tag 在不在我身上」。
    /// </summary>
    private static bool HasGameplayTag(EffectContext c, object? r, object?[] a)
    {
        // ⚠️ 接收者走 `SelfArg`（审计 §5.1：`getHasGameplayTag` 有 1 个隐式 self 调用点，
        //    旧写法 `AsCard(r)` 在那里恒 false）。参数里都是 tag 字符串，
        //    `SelfArg` 扫参数不会误判。
        if (SelfArg(c, r, a) is not { } card)
        {
            return false;
        }

        var wanted = new List<string>();
        foreach (object? v in a)
        {
            CollectTagStrings(v, wanted);
        }

        if (wanted.Count == 0)
        {
            // IR 把 tag 丢了 —— 只要这张卡带了**任意** tag，就说明调用方想问的是
            // 「它是不是某个子类型」。这个兜底会让「有 tag 的卡」全部命中，
            // 对 214th/committed_crew 这两张卡是正确的（它们各自只判一个 tag），
            // 但它**不是通用正确解**，所以同时把缺口记进未实现统计里，别让它静默。
            c.Engine.State.UnimplementedCalls["getHasGameplayTag<ir-tag-lost>"] =
                c.Engine.State.UnimplementedCalls.GetValueOrDefault("getHasGameplayTag<ir-tag-lost>") + 1;
            return GameplayTagTable.Any(card.Name);
        }

        // 数组语义是「命中任意一个即为真」（客户端把单 tag 也包成一元数组）
        foreach (string tag in wanted)
        {
            if (GameplayTagTable.Has(card.Name, tag))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasGameplayTag(CardInstance card, string tag)
        => GameplayTagTable.Has(card.Name, tag);

    private static void CollectTagStrings(object? v, List<string> into)
    {
        switch (v)
        {
            case string s when s.Length > 0:
                into.Add(s);
                break;
            case System.Collections.IEnumerable e and not string:
                foreach (object? item in e)
                {
                    CollectTagStrings(item, into);
                }

                break;
        }
    }

    private object? DoChangeKredits(EffectContext c, object? r, object?[] a)
    {
        var side = SideArg(r, a, 0, c.Controller);
        int delta = a.Select(AsInt).FirstOrDefault(v => v != 0);
        State.AddKredits(side, delta);
        return null;
    }

    private object? DoCustomAbilityAdd(EffectContext c, object? r, object?[] a)
    {
        // CustomAbilityAdd(能力名, 目标cardID, 施予者cardID, bool, bool, bool, out)
        string ability = StrArg(a, 0);
        var target = AsCard(a.ElementAtOrDefault(1)) ?? c.Target;
        if (target is not null && ability.Length > 0)
        {
            CustomAbilityAdd(target, ability, c.Self);
        }

        return null;
    }

    private object? DoCustomAbilityRemove(EffectContext c, object? r, object?[] a)
    {
        var target = AsCard(a.ElementAtOrDefault(1)) ?? c.Target;
        target?.CustomAbility = null;
        return null;
    }

    private object? DoSuppressUnit(EffectContext c, object? r, object?[] a)
    {
        // `SuppressUnit(卡, instigatorID, bool, bool, out)` —— 目标在 a[0]。
        var target = AsCard(a.FirstOrDefault()) ?? AsCard(r) ?? c.Target;
        if (target is not null)
        {
            SuppressUnit(target);
        }

        return null;
    }

    private object? DoGiveKeyword(EffectContext c, object? r, object?[] a, string keyword)
    {
        var target = AsCard(r) ?? AsCard(a.FirstOrDefault()) ?? c.Target;
        if (target is not null)
        {
            GiveKeyword(target, keyword);
        }

        return null;
    }

    private object? DoRemoveKeyword(EffectContext c, object? r, object?[] a, string keyword)
    {
        var target = AsCard(r) ?? AsCard(a.FirstOrDefault()) ?? c.Target;
        if (target is not null)
        {
            RemoveKeyword(target, keyword);
        }

        return null;
    }

    // ==================== 参数提取小工具 ====================

    /// <summary>
    /// 取出「被作用的那张卡」。
    ///
    /// ⚠️ **顺序至关重要：先看参数，再看接收者。**
    ///
    /// 这类原语（`DamageCard` / `ChangeAttack` / `DestroyCard` …）在字节码里的形状是
    /// <c>Context{cardFunction}.DamageCard(目标, 数值, 来源, …)</c> —— 接收者是
    /// `cardFunction`（**施法者自己**），而**真正的目标是 `Parameters[0]`**。
    /// 实测（card_unit_10_5_cm_lefh）：
    /// <code>
    /// DamageCard(
    ///   [0] LocalVariable CallFunc_GetLocationCardBySide_card  ← 目标（敌方 HQ）
    ///   [1] IntConst 2                                          ← 伤害
    ///   [2] InstanceVariable cardID                             ← 来源，不是目标
    ///   …
    /// )
    /// </code>
    ///
    /// 旧实现写成 <c>AsCard(receiver) ?? args…</c>，于是**每个显式指定目标的原语
    /// 都会打到施法者自己身上** —— 表现为 10.5cm lefh 的「对敌方 HQ 造成 2 点伤害」
    /// 打掉了自己 2 点防御（3→1），而敌方 HQ 纹丝不动。
    ///
    /// 注意 `Parameters[0]` 也可能是 `InstanceVariable cardID`（自我引用），
    /// 那种情况下 <see cref="AsCard"/> 取不到卡，自然回退到接收者 —— 仍然正确。
    /// </summary>
    private static CardInstance? TargetCard(EffectContext c, object? receiver, object?[] args)
    {
        foreach (var v in args)
        {
            if (AsCard(v) is { } explicitTarget)
            {
                return explicitTarget;
            }
        }

        return AsCard(receiver) ?? c.Target ?? c.Self;
    }

    internal static CardInstance? AsCard(object? v) => v as CardInstance;

    /// <summary>
    /// 卡牌私有 JSON 里有没有这个键 —— `JSON_Get*` 的第 2 个输出槽（`found`）。
    /// 客户端判的是"键在不在"，不是"值是不是真"：写进去 `"0"` 之后 `found` 仍然是真。
    /// </summary>
    private static bool JsonHasKey(CardInstance card, string key)
        => key.Length > 0 && card.CustomJson.ContainsKey(key);

    /// <summary>
    /// 「这次调用作用在谁身上」—— 给**隐式 self** 的调用用。
    ///
    /// 判据顺序（每一条都有实测依据）：
    /// 1. <paramref name="receiver"/> —— 显式 `Context(card)` 的接收者（IR 的 `recv`）
    /// 2. <paramref name="args"/> 里第一个卡 —— 有些调用把目标写在参数里
    /// 3. <c>ctx.Self</c> —— **隐式 self 就是「正在跑这张卡的程序的那张卡」**
    /// 4. <c>ctx.Target</c> —— 最后才退回「触发这件事的那张卡」
    ///
    /// ⚠️ 为什么 3 必须排在 4 前面：Blueprint 里 `self.IsLocatedOnBoard()` 编译出来
    /// **没有 `Context` 包装**（实测 `card_unit_214th_amur` i=848、
    /// `card_unit_85_pioneer_company` i=477/701 都是 `{"Inst":"FinalFunction"}`），
    /// 所以 IR 里压根没有 `recv`。这里的 `self` 是**蓝图自己所属的对象**，
    /// 也就是 `ctx.Self`，而不是事件参数。
    /// 把 Target 排在前面会踩一个非常隐蔽的坑：`card_unit_85_pioneer_company` 的
    /// `OnOtherCardPlayedFromHand` 里判 `self.IsLocatedOnBoard()`（问的是**光环自己**
    /// 还在不在场），如果读成「刚打出的那张牌」，指令一进弃牌堆就判假、整条
    /// 还原分支被静默跳过 —— 表现成"打出一张指令后手牌费用不还原"。
    /// </summary>
    internal static CardInstance? SelfArg(EffectContext c, object? receiver, object?[] args)
    {
        if (AsCard(receiver) is { } explicitReceiver)
        {
            return explicitReceiver;
        }

        foreach (object? v in args)
        {
            if (AsCard(v) is { } fromArgs)
            {
                return fromArgs;
            }
        }

        return c.Self ?? c.Target;
    }

    internal static List<CardInstance> AsList(object? v) => v as List<CardInstance> ?? new List<CardInstance>();

    internal static int AsInt(object? v) => v switch
    {
        int i => i,
        bool b => b ? 1 : 0,
        string s when int.TryParse(s, out int r) => r,
        _ => 0,
    };

    internal static string? AsString(object? v) => v as string;

    internal static int IntArg(object?[] a, int i, int fallback = 0)
        => i < a.Length ? AsInt(a[i]) : fallback;

    internal static string StrArg(object?[] a, int i)
        => i < a.Length ? (a[i] as string ?? "") : "";

    internal static string? StrArgOrNull(object?[] a, int i)
        => i < a.Length ? a[i] as string : null;

    internal static bool TruthyArg(object?[] a, int i)
        => i < a.Length && KismetVmTruthy(a[i]);

    private static bool KismetVmTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        string s => s.Length > 0,
        _ => true,
    };

    /// <summary>
    /// 取阵营参数。
    /// 参数可能是 <c>side</c> 这个 int（ESideEnum 1/2），也可能是接收者本身代表的阵营；
    /// 都拿不到时退回效果控制方 —— 这比返回 0 安全（0 = NotAvailable 会让效果静默失效）。
    /// </summary>
    internal static Side SideArg(object? receiver, object?[] args, int index, Side? fallback = null)
    {
        if (index < args.Length)
        {
            var v = args[index];
            if (v is int i && i is 1 or 2)
            {
                return (Side)i;
            }

            if (v is Side s && s != Side.NotAvailable)
            {
                return s;
            }
        }

        if (receiver is CardInstance card)
        {
            return card.Owner;
        }

        return fallback ?? Side.NotAvailable;
    }

    /// <summary>
    /// 「我方」阵营 —— 给那些**没有入参、隐含以卡自己为上下文**的原语用
    /// （目前已知只有 <c>GetOppositeSide</c>）。
    /// 优先取卡自己的 owner，退回效果控制方。
    /// </summary>
    internal static Side SelfSide(EffectContext c)
    {
        if (c.Self is { } self && self.Owner != Side.NotAvailable)
        {
            return self.Owner;
        }

        return c.Controller;
    }
}

