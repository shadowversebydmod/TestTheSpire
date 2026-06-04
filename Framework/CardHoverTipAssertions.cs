using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using Xunit;

namespace TestTheSpire;

public static class CardHoverTipAssertions
{
    public static void ContainsPower<TPower>(CardModel card) where TPower : PowerModel
    {
        var expectedId = HoverTipFactory.FromPower<TPower>().Id;
        Assert.Contains(card.HoverTips, tip => tip.Id == expectedId);
    }

    public static void DoesNotContainPower<TPower>(CardModel card) where TPower : PowerModel
    {
        var expectedId = HoverTipFactory.FromPower<TPower>().Id;
        Assert.DoesNotContain(card.HoverTips, tip => tip.Id == expectedId);
    }
}
