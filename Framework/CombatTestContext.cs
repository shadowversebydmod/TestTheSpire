using Godot;
using System.Reflection;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace TestTheSpire;

public sealed class CombatTestContext
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly FieldInfo? CombatStateChangedField = typeof(CombatStateTracker).GetField(
        nameof(CombatStateTracker.CombatStateChanged),
        BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly NGame _game;
    private Action<GameAction>? _trackChecksumActionStart;
    private Action<GameAction>? _generateMissingPostActionChecksum;
    private Action<GameAction>? _trackExecutedAction;
    private bool _checksumTrackingFallbackInstalled;
    private bool _actionExecutionTrackingInstalled;
    private int _executedActionCount;
    private uint _checksumNextIdAtActionStart;

    public Player Player { get; }

    public IReadOnlyList<Player> Players { get; }

    public RunState RunState { get; }

    public CombatState CombatState { get; }

    public IReadOnlyList<Creature> Enemies => CombatState.Enemies;

    public int ExecutedActionCount => _executedActionCount;

    private CombatTestContext(
        NGame game,
        Player player,
        IReadOnlyList<Player> players,
        RunState runState,
        CombatState combatState)
    {
        _game = game;
        Player = player;
        Players = players;
        RunState = runState;
        CombatState = combatState;
    }

    internal static async Task<CombatTestContext> CreateAsync(
        NGame game,
        CombatTestBattleDefinition definition,
        string seed)
    {
        await ResetEnvironmentAsync(game);

        TestMode.TurnOnInternal();
        game.StartOnMainMenu = false;

        var players = definition.Players
            .Select(player => Player.CreateForNewRun(
                player.Character,
                UnlockState.all,
                player.NetId))
            .ToArray();

        var player = players.Single(p => p.NetId == definition.LocalNetId);

        if (definition.RemoveStartingRelics)
            foreach (var runPlayer in players)
            foreach (var relic in runPlayer.Relics.ToList())
                runPlayer.RemoveRelicInternal(relic, true);

        var runState = RunState.CreateForTest(players, seed: seed);
        INetGameService netService = definition.UseMultiplayerHost
            ? new TestNetGameService(definition.LocalNetId)
            : new NetSingleplayerGameService();
        RunManager.Instance.SetUpTest(
            runState,
            netService);
        RunManager.Instance.GenerateRooms();

        await PreloadManager.LoadRunAssets(runState.Players.Select(static p => p.Character));
        await PreloadManager.LoadActAssets(runState.Act);
        await RunManager.Instance.FinalizeStartingRelics();

        RunManager.Instance.Launch();
        game.RootSceneContainer.SetCurrentScene(NRun.Create(runState));

        await WaitForStatic(() => NRun.Instance != null, "Run scene did not initialize.");

        var encounter = definition.EncounterFactory();
        await RunManager.Instance.EnterRoomDebug(
            definition.RoomType,
            model: encounter,
            showTransition: false);

        await WaitForStatic(
            () => CombatManager.Instance.IsInProgress && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play,
            "Combat play phase did not begin.");

        game.SetScreenShakeTarget(game.RootSceneContainer);

        var combatState = CombatManager.Instance.DebugOnlyGetState()
                          ?? throw new InvalidOperationException("Combat state was not available.");

        return new CombatTestContext(game, player, players, runState, combatState);
    }

    public Player PlayerWithNetId(ulong netId)
    {
        return Players.Single(player => player.NetId == netId);
    }

    public void EnableChecksumTracking()
    {
        var checksumTracker = RunManager.Instance.ChecksumTracker;
        var isEnabledProperty = checksumTracker
            .GetType()
            .GetProperty("IsEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (isEnabledProperty is { CanWrite: true } && isEnabledProperty.PropertyType == typeof(bool))
            isEnabledProperty.SetValue(checksumTracker, true);

        EnsurePostActionChecksumFallback();
    }

    public void BeginActionExecutionTracking()
    {
        RemoveActionExecutionTracking();
        _executedActionCount = 0;
        _trackExecutedAction = _ => _executedActionCount++;
        RunManager.Instance.ActionExecutor.AfterActionExecuted += _trackExecutedAction;
        _actionExecutionTrackingInstalled = true;
    }

    public void EndActionExecutionTracking()
    {
        RemoveActionExecutionTracking();
    }

    public Creature EnemyAt(int index)
    {
        if (index < 0 || index >= Enemies.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Enemy index {index} is out of range.");

        return Enemies[index];
    }

    public Creature Enemy<TMonster>() where TMonster : MonsterModel
    {
        var matches = Enemies
            .Where(static enemy => enemy.Monster is TMonster)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No enemy of type {typeof(TMonster).Name} was found."),
            _ => throw new InvalidOperationException(
                $"Expected exactly one enemy of type {typeof(TMonster).Name}, found {matches.Count}.")
        };
    }

    public async Task<TCard> AddToHand<TCard>() where TCard : CardModel
    {
        return await AddToHand<TCard>(Player);
    }

    public async Task<TCard> AddToHand<TCard>(Player owner) where TCard : CardModel
    {
        var card = CombatState.CreateCard<TCard>(owner);
        await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, owner);
        await WaitFor(
            () => PileType.Hand.GetPile(owner).Cards.Contains(card),
            $"Injected {typeof(TCard).Name} did not appear in hand.");
        return card;
    }

    public async Task Play(CardModel card, Creature? target = null)
    {
        if (!card.TryManualPlay(target)) throw new InvalidOperationException($"Failed to play {card.Id.Entry}.");

        await WaitFor(
            () => !PileType.Hand.GetPile(card.Owner).Cards.Contains(card),
            $"{card.Id.Entry} never left the hand.");

        await WaitForIdle();
    }

    public async Task PlayNetworkAction(CardModel card, Creature? target = null)
    {
        await EnqueueNetworkAction(new PlayCardAction(card, target));

        await WaitFor(
            () => !PileType.Hand.GetPile(card.Owner).Cards.Contains(card),
            $"{card.Id.Entry} never left the hand.");
    }

    public async Task EnqueueNetworkAction(GameAction action)
    {
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
        await WaitForIdle();
    }

    public async Task EndPlayerTurn()
    {
        if (CombatState.CurrentSide != CombatSide.Player)
            throw new InvalidOperationException("Cannot end the player turn while it is not the player's turn.");

        var currentRound = CombatState.RoundNumber;
        PlayerCmd.EndTurn(Player, false);

        await WaitFor(
            () => CombatState.CurrentSide == CombatSide.Player && CombatState.RoundNumber > currentRound,
            "The next player turn did not start in time.");

        await WaitForIdle();
    }

    public async Task<TPower?> ApplyPower<TPower>(
        Creature target,
        int amount,
        Creature? applier = null,
        CardModel? cardSource = null)
        where TPower : PowerModel
    {
        var power = await PowerCmd.Apply<TPower>(
            new BlockingPlayerChoiceContext(),
            target,
            amount,
            applier,
            cardSource,
            true);
        await WaitForIdle();
        return power;
    }

    public Task WaitFor(Func<bool> condition, string timeoutMessage)
    {
        return WaitForStatic(condition, timeoutMessage);
    }

    public async Task WaitForIdle()
    {
        await NextFrame();
        await WaitFor(
            () => !RunManager.Instance.ActionExecutor.IsRunning,
            "Action queue did not finish.");
        await NextFrame();
    }

    public async Task ResetAsync()
    {
        RemoveActionExecutionTracking();
        RemovePostActionChecksumFallback();
        await ResetEnvironmentAsync(_game);
    }

    private void EnsurePostActionChecksumFallback()
    {
        if (_checksumTrackingFallbackInstalled) return;

        var actionExecutor = RunManager.Instance.ActionExecutor;
        _trackChecksumActionStart = _ => _checksumNextIdAtActionStart = RunManager.Instance.ChecksumTracker.NextId;
        _generateMissingPostActionChecksum = action =>
        {
            if (!CombatManager.Instance.IsInProgress) return;
            if (action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction) return;
            if (RunManager.Instance.ChecksumTracker.NextId != _checksumNextIdAtActionStart) return;

            RunManager.Instance.ChecksumTracker.GenerateChecksum($"finished action execution {action}", action);
        };

        actionExecutor.BeforeActionExecuted += _trackChecksumActionStart;
        actionExecutor.AfterActionExecuted += _generateMissingPostActionChecksum;
        _checksumTrackingFallbackInstalled = true;
    }

    private void RemovePostActionChecksumFallback()
    {
        if (!_checksumTrackingFallbackInstalled) return;

        var runManager = RunManager.Instance;
        if (_trackChecksumActionStart != null)
            runManager.ActionExecutor.BeforeActionExecuted -= _trackChecksumActionStart;
        if (_generateMissingPostActionChecksum != null)
            runManager.ActionExecutor.AfterActionExecuted -= _generateMissingPostActionChecksum;

        _trackChecksumActionStart = null;
        _generateMissingPostActionChecksum = null;
        _checksumTrackingFallbackInstalled = false;
    }

    private void RemoveActionExecutionTracking()
    {
        if (!_actionExecutionTrackingInstalled) return;

        if (_trackExecutedAction != null)
            RunManager.Instance.ActionExecutor.AfterActionExecuted -= _trackExecutedAction;

        _trackExecutedAction = null;
        _actionExecutionTrackingInstalled = false;
    }

    private static async Task ResetEnvironmentAsync(NGame game)
    {
        RemoveBackendIncompatibleCombatStateSubscribers();

        if (RunManager.Instance.DebugOnlyGetState() != null)
            try
            {
                RunManager.Instance.CleanUp();
            }
            catch (Exception ex)
            {
                Log.Warn($"[{CombatTestBootstrap.LogPrefix}] Cleanup raised an exception: {ex}");
            }

        game.RootSceneContainer.SetCurrentScene(new Control { Name = "CombatTestBlankScene" });

        await WaitForStatic(
            () => NRun.Instance == null
                  && RunManager.Instance.DebugOnlyGetState() == null
                  && CombatManager.Instance.DebugOnlyGetState() == null
                  && !CombatManager.Instance.IsInProgress,
            "Previous combat environment did not reset cleanly.");

        await NextFrame();
        await NextFrame();
    }

    private static void RemoveBackendIncompatibleCombatStateSubscribers()
    {
        if (!TestMode.IsOn || CombatStateChangedField == null) return;

        var tracker = CombatManager.Instance.StateTracker;
        if (CombatStateChangedField.GetValue(tracker) is not Delegate subscribers) return;

        CombatStateChangedField.SetValue(tracker, null);
        Log.Warn(
            $"[{CombatTestBootstrap.LogPrefix}] Removed {subscribers.GetInvocationList().Length} " +
            "CombatStateChanged subscriber(s) that are invalid while STS2 TestMode is active.");
    }

    private static async Task WaitForStatic(Func<bool> condition, string timeoutMessage)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException(timeoutMessage);

            await NextFrame();
        }
    }

    private static async Task NextFrame()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }
}
