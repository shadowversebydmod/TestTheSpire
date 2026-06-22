using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace TestTheSpire;

public static class ModelHookCompatExtensions
{
    public static Task AfterTurnEnd(
        this AbstractModel model,
        PlayerChoiceContext choiceContext,
        CombatSide side)
    {
        var participants = model is PowerModel { Owner.CombatState: { } combatState }
            ? combatState.GetCreaturesOnSide(side)
            : Array.Empty<Creature>();
        return model.AfterSideTurnEnd(choiceContext, side, participants);
    }
}
