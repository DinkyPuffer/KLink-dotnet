using KLink.Bot.Engine;

namespace KLink.Bot.Bots;

/// <summary>一个决策策略。将来神经网络也是实现这个接口。</summary>
public interface IPlayerPolicy
{
    /// <summary>执行该方的一整个回合，返回是否正常结束（false 表示对局已结束）。</summary>
    bool PlayTurn(MatchEngine engine, Side side);

    string Name { get; }
}

/// <summary>
/// 贪心策略（baseline）。规则很简单，目的只是让对局能跑完、能产出数据：
/// 1. 能打牌就打（费用高的优先，单位优先于指令）
/// 2. 能攻击就攻击（优先打得死的、其次是 HQ）
/// 3. 没得做就结束回合
///
/// 这就是 goal.txt 里说的「简单决策树」，也是将来神经网络要打败的基准。
/// </summary>
public sealed class GreedyBot : IPlayerPolicy
{
    private readonly int _maxActionsPerTurn;

    public GreedyBot(string name = "greedy", int maxActionsPerTurn = 200)
    {
        Name = name;
        _maxActionsPerTurn = maxActionsPerTurn;
    }

    public string Name { get; }

    public bool PlayTurn(MatchEngine engine, Side side)
    {
        var state = engine.State;
        int guard = 0;

        while (!state.IsFinished && guard++ < _maxActionsPerTurn)
        {
            if (state.ActiveSide != side)
            {
                return true;
            }

            if (TryPlayCard(engine, side))
            {
                continue;
            }

            if (TryAttack(engine, side))
            {
                continue;
            }

            if (TryMove(engine, side))
            {
                continue;
            }

            engine.EndTurn(side);
            return !state.IsFinished;
        }

        return !state.IsFinished;
    }

    private static bool TryPlayCard(MatchEngine engine, Side side)
    {
        var state = engine.State;
        // 单位优先（能站场），其次按费用从高到低
        var playable = state.Hand(side)
            .Where(c => engine.CanPlay(c, out _))
            .OrderByDescending(c => c.Definition.IsUnit)
            .ThenByDescending(c => c.KreditCost)
            .ToList();

        foreach (var card in playable)
        {
            var target = ChooseTarget(engine, card, side);
            // 需要目标但选不到目标的牌先不打
            if (NeedsTarget(card) && target is null)
            {
                continue;
            }

            if (engine.PlayCard(card, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryAttack(MatchEngine engine, Side side)
    {
        var state = engine.State;
        foreach (var unit in state.Board(side).Where(u => u.CanOperateThisTurn(state)).ToList())
        {
            var targets = engine.LegalTargets(unit).ToList();
            if (targets.Count == 0)
            {
                continue;
            }

            // 优先能一击打死的单位，其次打 HQ
            var kill = targets
                .Where(t => !t.IsHq && t.Defense <= unit.Attack)
                .OrderByDescending(t => t.Attack)
                .FirstOrDefault();

            var chosen = kill ?? targets.FirstOrDefault(t => t.IsHq) ?? targets[0];

            if (engine.Attack(unit, chosen))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMove(MatchEngine engine, Side side)
    {
        // v0 不做移动决策（前线模型还没定）。TODO 待回放确认后排上。
        return false;
    }

    private static bool NeedsTarget(CardInstance card)
        => card.Definition.ExternalCalls.Contains("GetTargetedCard", StringComparer.Ordinal);

    private static CardInstance? ChooseTarget(MatchEngine engine, CardInstance card, Side side)
    {
        if (!NeedsTarget(card))
        {
            return null;
        }

        var enemy = side.Opposite();
        var units = engine.State.Board(enemy).ToList();
        return units.Count > 0 ? units.OrderByDescending(u => u.Attack).First() : null;
    }
}
