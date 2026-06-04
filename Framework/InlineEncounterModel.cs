using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace TestTheSpire;

public sealed class InlineEncounterModel : EncounterModel
{
    private List<CombatTestBattleBuilder.MonsterFactory> _monsterFactories = new();
    private RoomType _roomType = RoomType.Monster;

    public override RoomType RoomType => _roomType;

    public override bool IsDebugEncounter => true;

    public override IEnumerable<MonsterModel> AllPossibleMonsters
        => _monsterFactories.Select(static factory => factory.Canonical);

    internal void Configure(
        RoomType roomType,
        IEnumerable<CombatTestBattleBuilder.MonsterFactory> monsterFactories)
    {
        AssertMutable();
        _roomType = roomType;
        _monsterFactories = monsterFactories.ToList();
    }

    protected override IReadOnlyList<(MonsterModel, string?)> GenerateMonsters()
    {
        return _monsterFactories
            .Select(static factory => (factory.Create(), (string?)null))
            .ToArray();
    }

    protected override void DeepCloneFields()
    {
        base.DeepCloneFields();
        _monsterFactories = _monsterFactories.ToList();
    }
}
