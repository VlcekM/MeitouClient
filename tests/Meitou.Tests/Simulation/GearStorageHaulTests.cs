using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Tests.Simulation;

/// <summary>
/// Which storages a "Hauling to" job fills with any loose or carried item of their type (<see cref="MachineData.HaulsAnyOfType"/>,
/// docs/game/buildings-production.md "Hauling"): a storage for one kind of gear with no <c>limit inventory</c>.
/// </summary>
public class GearStorageHaulTests
{
    [Theory]
    [InlineData(FcsRecordType.Armour)]
    [InlineData(FcsRecordType.Weapon)]
    [InlineData(FcsRecordType.Crossbow)]
    public void AGearStorageWithNoListTakesAnyItemOfItsType(FcsRecordType type)
    {
        Assert.True(new MachineData { Id = "rack", Name = "Rack", ItemTypeLimit = type }.HaulsAnyOfType);
    }

    [Fact]
    public void AListedOrGeneralStorageTakesOnlyWhatItLists()
    {
        Assert.False(new MachineData { Id = "rack", Name = "Rack", ItemTypeLimit = FcsRecordType.Weapon, StorageLimit = ["katana"] }.HaulsAnyOfType);
        Assert.False(new MachineData { Id = "chest", Name = "Chest", ItemTypeLimit = FcsRecordType.Item }.HaulsAnyOfType);
    }
}
