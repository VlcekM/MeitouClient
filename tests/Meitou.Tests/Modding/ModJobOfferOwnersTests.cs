using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Production;

namespace Meitou.Tests.Modding;

/// <summary>
/// Whose buildings a code mod's job offer is shown on (<see cref="ModJobOffer.Owners"/>, docs/modding.md "Mod jobs").
/// </summary>
public class ModJobOfferOwnersTests
{
    static ModJobOffer Offer(ModJobOwners owners = ModJobOwners.Player) =>
        new((TaskType)4501, ["post"], "Hitch", ModJobTakers.Animals) { Owners = owners };

    [Fact]
    public void ByDefaultOnlyThePlayersBuildingsOfferIt()
    {
        var offer = Offer();

        Assert.Equal(ModJobOwners.Player, offer.Owners);
        Assert.True(offer.OwnedBy(owner: 3, player: 3));
        Assert.False(offer.OwnedBy(owner: 7, player: 3));
    }

    [Fact]
    public void AnOfferForAnyOwnerIsOnEveryFactionsBuildings()
    {
        var offer = Offer(ModJobOwners.Any);

        Assert.True(offer.OwnedBy(owner: 3, player: 3));
        Assert.True(offer.OwnedBy(owner: 7, player: 3));
        Assert.Null(offer.Refusal());
    }

    [Fact]
    public void AnOwnersValueOutsideTheEnumIsRefused()
    {
        Assert.NotNull(Offer((ModJobOwners)5).Refusal());
    }
}
