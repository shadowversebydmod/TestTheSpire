using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Multiplayer;

namespace TestTheSpire;

public sealed class CombatTestBattleBuilder
{
    private readonly List<MonsterFactory> _monsterFactories = new();
    private readonly List<PlayerFactory> _players = new();
    private readonly List<PlayerFactory> _extraPlayers = new();
    private Func<EncounterModel>? _encounterFactory;
    private CharacterModel _playerCharacter = ModelDb.Character<Ironclad>();
    private ulong _localNetId = NetSingleplayerGameService.defaultNetId;
    private bool _hasExplicitPlayers;
    private bool _removeStartingRelics = true;
    private RoomType _roomType = RoomType.Monster;
    private string? _seed;
    private bool _useMultiplayerHost;

    public CombatTestBattleBuilder Player<TCharacter>() where TCharacter : CharacterModel
    {
        _playerCharacter = ModelDb.Character<TCharacter>();
        return this;
    }

    public CombatTestBattleBuilder Player(CharacterModel character)
    {
        _playerCharacter = character ?? throw new ArgumentNullException(nameof(character));
        return this;
    }

    public CombatTestBattleBuilder AddPlayer<TCharacter>(ulong netId)
        where TCharacter : CharacterModel
    {
        _players.Add(new PlayerFactory(ModelDb.Character<TCharacter>(), netId));
        _hasExplicitPlayers = true;
        return this;
    }

    public CombatTestBattleBuilder AddPlayer(CharacterModel character, ulong netId)
    {
        _players.Add(new PlayerFactory(character ?? throw new ArgumentNullException(nameof(character)), netId));
        _hasExplicitPlayers = true;
        return this;
    }

    public CombatTestBattleBuilder LocalNetId(ulong netId)
    {
        _localNetId = netId;
        return this;
    }

    public CombatTestBattleBuilder AddRemotePlayer<TCharacter>(ulong netId = 2)
        where TCharacter : CharacterModel
    {
        var player = new PlayerFactory(ModelDb.Character<TCharacter>(), netId);
        if (_hasExplicitPlayers)
            _players.Add(player);
        else
            _extraPlayers.Add(player);
        _useMultiplayerHost = true;
        return this;
    }

    public CombatTestBattleBuilder AddEnemy<TMonster>(Action<TMonster>? configure = null)
        where TMonster : MonsterModel
    {
        _monsterFactories.Add(new MonsterFactory(
            ModelDb.Monster<TMonster>(),
            () =>
            {
                var monster = (TMonster)ModelDb.Monster<TMonster>().ToMutable();
                configure?.Invoke(monster);
                return monster;
            }));
        return this;
    }

    public CombatTestBattleBuilder Encounter<TEncounter>() where TEncounter : EncounterModel
    {
        _encounterFactory = static () => (EncounterModel)ModelDb.Encounter<TEncounter>().ToMutable();
        return this;
    }

    public CombatTestBattleBuilder Encounter(Func<EncounterModel> encounterFactory)
    {
        _encounterFactory = encounterFactory ?? throw new ArgumentNullException(nameof(encounterFactory));
        return this;
    }

    public CombatTestBattleBuilder KeepStartingRelics()
    {
        _removeStartingRelics = false;
        return this;
    }

    public CombatTestBattleBuilder WithSeed(string seed)
    {
        _seed = seed;
        return this;
    }

    public CombatTestBattleBuilder WithRoomType(RoomType roomType)
    {
        _roomType = roomType;
        return this;
    }

    internal CombatTestBattleDefinition Build()
    {
        var encounterFactory = _encounterFactory ?? BuildInlineEncounterFactory();
        var players = BuildPlayers();
        ValidatePlayers(players);
        return new CombatTestBattleDefinition(
            players,
            _localNetId,
            _useMultiplayerHost || players.Length > 1,
            encounterFactory,
            _roomType,
            _removeStartingRelics,
            _seed);
    }

    private PlayerFactory[] BuildPlayers()
    {
        if (_hasExplicitPlayers) return _players.ToArray();

        return new[]
            {
                new PlayerFactory(_playerCharacter, _localNetId)
            }
            .Concat(_extraPlayers)
            .ToArray();
    }

    private void ValidatePlayers(IReadOnlyList<PlayerFactory> players)
    {
        if (players.Count == 0)
            throw new InvalidOperationException("At least one player must be configured for a combat test.");

        var duplicateNetIds = players
            .GroupBy(static player => player.NetId)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();
        if (duplicateNetIds.Length > 0)
            throw new InvalidOperationException(
                $"Combat test players must have unique net IDs. Duplicates: {string.Join(", ", duplicateNetIds)}.");

        if (players.All(player => player.NetId != _localNetId))
            throw new InvalidOperationException(
                $"Local net ID {_localNetId} was not found in the configured combat test players.");
    }

    private Func<EncounterModel> BuildInlineEncounterFactory()
    {
        if (_monsterFactories.Count == 0)
            throw new InvalidOperationException("No encounter or monsters were configured for this combat test.");

        var factories = _monsterFactories.ToArray();
        var roomType = _roomType;

        return () =>
        {
            var encounter = (InlineEncounterModel)ModelDb.Encounter<InlineEncounterModel>().ToMutable();
            encounter.Configure(roomType, factories);
            return encounter;
        };
    }

    internal sealed record MonsterFactory(MonsterModel Canonical, Func<MonsterModel> Create);

    internal sealed record PlayerFactory(CharacterModel Character, ulong NetId);
}

internal sealed record CombatTestBattleDefinition(
    IReadOnlyList<CombatTestBattleBuilder.PlayerFactory> Players,
    ulong LocalNetId,
    bool UseMultiplayerHost,
    Func<EncounterModel> EncounterFactory,
    RoomType RoomType,
    bool RemoveStartingRelics,
    string? Seed)
{
    public CombatTestBattleDefinition WithLocalNetId(ulong localNetId)
    {
        if (Players.All(player => player.NetId != localNetId))
            throw new InvalidOperationException(
                $"Local net ID {localNetId} was not found in the configured combat test players.");

        return this with
        {
            LocalNetId = localNetId,
            UseMultiplayerHost = UseMultiplayerHost || Players.Count > 1
        };
    }
}
