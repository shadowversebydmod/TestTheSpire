using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace TestTheSpire;

public abstract class CombatTestSuite
{
    private CombatTestContext? _context;

    protected CombatTestContext Context => _context
                                           ?? throw new InvalidOperationException(
                                               "Combat test context has not been attached yet.");

    protected Player Player => Context.Player;

    protected IReadOnlyList<Player> Players => Context.Players;

    protected CombatState Combat => Context.CombatState;

    internal void AttachContext(CombatTestContext context)
    {
        _context = context;
    }

    internal CombatTestBattleDefinition BuildBattleDefinition()
    {
        CombatTestBattleBuilder battle = new();
        ConfigureBattle(battle);
        return battle.Build();
    }

    internal Task InitializeInternalAsync()
    {
        return InitializeAsync();
    }

    internal Task DisposeInternalAsync()
    {
        return DisposeAsync();
    }

    protected virtual void ConfigureBattle(CombatTestBattleBuilder battle) { }

    protected virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    protected virtual Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    protected Creature EnemyAt(int index)
    {
        return Context.EnemyAt(index);
    }

    protected Player PlayerWithNetId(ulong netId)
    {
        return Context.PlayerWithNetId(netId);
    }

    protected void EnableChecksumTracking()
    {
        Context.EnableChecksumTracking();
    }

    protected Creature Enemy<TMonster>() where TMonster : MonsterModel
    {
        return Context.Enemy<TMonster>();
    }

    protected Task<TCard> AddToHand<TCard>() where TCard : CardModel
    {
        return Context.AddToHand<TCard>();
    }

    protected Task<TCard> AddToHand<TCard>(Player owner) where TCard : CardModel
    {
        return Context.AddToHand<TCard>(owner);
    }

    protected Task Play(CardModel card, Creature? target = null)
    {
        return Context.Play(card, target);
    }

    protected Task PlayNetworkAction(CardModel card, Creature? target = null)
    {
        return Context.PlayNetworkAction(card, target);
    }

    protected Task EnqueueNetworkAction(GameAction action)
    {
        return Context.EnqueueNetworkAction(action);
    }

    protected Task EndTurn()
    {
        return Context.EndPlayerTurn();
    }

    protected Task<TPower?> ApplyPower<TPower>(
        Creature target,
        int amount,
        Creature? applier = null,
        CardModel? cardSource = null)
        where TPower : PowerModel
    {
        return Context.ApplyPower<TPower>(target, amount, applier, cardSource);
    }

    protected Task WaitFor(Func<bool> condition, string timeoutMessage)
    {
        return Context.WaitFor(condition, timeoutMessage);
    }

    protected Task WaitForIdle()
    {
        return Context.WaitForIdle();
    }
}
