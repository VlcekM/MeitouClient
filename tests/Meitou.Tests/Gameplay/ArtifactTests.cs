using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Economy;
using static Meitou.Tests.Gameplay.EconomyTests;

namespace Meitou.Tests.Gameplay;

/// <summary>
/// The towns' artifacts of a new game (docs/game/economy.md "Artifacts") on invented records, and the boost draws they are shared with.
/// </summary>
public class ArtifactTests
{
    static FcsReference Ref(string id, int v0 = 0, int v1 = 0) => new(id, v0, v1, 0);

    [Fact]
    public void The_draws_are_boosts_uniform_int_and_msvcs_random_shuffle_on_the_mt19937()
    {
        // Worked out from the published algorithms (MT19937 seeded 11667085, boost 1.60's bucket rejection, MSVC 2010's random_shuffle).
        var rng = new BoostRandom(Artifacts.Seed);
        Assert.Equal(0, rng.UniformInt(0, 0));   // a range of 0 draws nothing
        Assert.Equal(1, rng.UniformInt(0, 2));
        var copy = rng.Clone();
        Assert.Equal([1, 2], [rng.UniformInt(0, 2), rng.UniformInt(0, 2)]);
        Assert.Equal([1, 2], [copy.UniformInt(0, 2), copy.UniformInt(0, 2)]);
        var list = new List<int> { 0, 1, 2, 3, 4 };
        new BoostRandom(Artifacts.Seed).Shuffle(list);
        Assert.Equal([0, 2, 1, 4, 3], list);
    }

    [Fact]
    public void Gear_gives_each_town_one_unit_then_the_rest_by_draws_and_items_are_spread_by_the_towns_maxima()
    {
        var db = Db(
            Rec("book", FcsRecordType.Item, r => r.Ints["value"] = 100),
            Rec("core", FcsRecordType.Item, r => r.Ints["value"] = 1000),
            Rec("relic", FcsRecordType.Item, r => r.Ints["value"] = 50000),
            Rec("plate", FcsRecordType.Armour, r => r.Ints["value"] = 500),
            Rec("sword", FcsRecordType.Weapon, r => r.Ints["value"] = 10),
            Rec("axe", FcsRecordType.Weapon, r => r.Ints["value"] = 10),
            Rec("mk1", FcsRecordType.MaterialSpecsWeapon),
            Rec("smith", FcsRecordType.WeaponManufacturer, r =>
            {
                r.Floats["price mod"] = 1;
                r.References["weapon models"] = [Ref("mk1", 50)];
                r.References["weapon types"] = [Ref("sword"), Ref("axe")];
            }),
            Rec("art", FcsRecordType.Artifacts, r =>
            {
                r.References["items"] = [Ref("book", 4), Ref("core", 2), Ref("relic", 1)];
                r.References["armours"] = [Ref("plate", 3)];
                r.References["weapons"] = [Ref("mk1", 1)];
            }),
            Town("A", 0, 2000, 0, 1000),
            Town("B", 0, 1000, 0, 600),
            Town("C", 500, 2000, 0, 0),
            Town("N", 0, 2000, 0, 1000, type: 0));
        var plan = Artifacts.Plan(
            db,
            new[] { "A", "B", "C", "N" }.Select((id, k) => new ArtifactTown(k, TownData.From(db.Find(id)!), new Vector3(k, 0, 0))));

        // Towns in order B (max 1000), A, C. Book (100): B and A, spread 2 / 2 by their maxima;
        // core (1000): B, A and C, 1 / 1. The relic no town takes, nor the nest.
        // Gear: B takes a plate (500), A a plate; the third plate is drawn between them (A);
        // each weapon (mk1 at level 50: 875) only A takes, a type each.
        Assert.Equal(
            [
                new ArtifactStack("plate", "", "", 80, 2), new("axe", "mk1", "smith", 50, 1), new("sword", "mk1", "smith", 50, 1),
                new("book", "", "", 0, 2), new("core", "", "", 0, 1),
            ],
            plan[0]);
        Assert.Equal([new ArtifactStack("plate", "", "", 80, 1), new("book", "", "", 0, 2), new("core", "", "", 0, 1)], plan[1]);
        Assert.False(plan.ContainsKey(2));
        Assert.False(plan.ContainsKey(3));
    }

    static FcsRecord Town(string id, int itemMin, int itemMax, int gearMin, int gearMax, int type = 2) => Rec(id, FcsRecordType.Town, r =>
    {
        r.Ints["type"] = type;
        (r.Ints["item artifacts min value"], r.Ints["item artifacts max value"]) = (itemMin, itemMax);
        (r.Ints["gear artifacts min value"], r.Ints["gear artifacts max value"]) = (gearMin, gearMax);
    });
}
