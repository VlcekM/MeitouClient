using Meitou.Data.Gameplay;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>
/// What the tutorial triggers ask of the player's characters beyond the character snapshots (docs/game/ui-screens.md "Tutorials"): one of them in an
/// outpost or town, with stolen goods, a faction uniform or a blueprint (or, as the original's inverted test reads, any backpack), and imprisoned by
/// another faction or by the Cannibals.
/// </summary>
public readonly record struct TutorialFacts(bool InTown, bool Stolen, bool Uniform, bool Blueprint, bool InPrison, bool InCannibalCage);

/// <summary>
/// The world's tutorial facts.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// The Cannibals faction (<c>518-gamedata.base</c>), whose cages have their own escape tutorial.
    /// </summary>
    const string CannibalFaction = "518-gamedata.base";
    /// <summary>
    /// ITEM_BLUEPRINT (item function 11).
    /// </summary>
    const int BlueprintFunction = 11;

    /// <summary>
    /// What the tutorial triggers read this tick (<see cref="TutorialFacts"/>). A fact already found is not looked for again, and the walk ends once
    /// every one is found.
    /// </summary>
    TutorialFacts PublishTutorialFacts(PopulationData? population)
    {
        var facts = new TutorialFacts();
        var table = Characters;

        for (int i = 0; i < table.HighWater && !AllFound(facts); i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: true } c) continue;
            if (!facts.InTown && population is not null && InTown(population, table.Next[i].Position))
                facts = facts with { InTown = true };

            if (c.Inventory is { } inv) facts = ItemFacts(facts, c, inv, population);
            if (c.InSomething == Locks.HeldState.Imprisoned && c.InWhat is { } held)
                facts = PrisonFacts(facts, held, population);
        }

        return facts;
    }

    /// <summary>
    /// Whether every one of the <paramref name="facts"/> is found.
    /// </summary>
    static bool AllFound(TutorialFacts facts) =>
        facts is { InTown: true, Stolen: true, Uniform: true, Blueprint: true, InPrison: true, InCannibalCage: true };

    /// <summary>
    /// <paramref name="facts"/> with what player character <paramref name="c"/>'s <paramref name="inv"/> adds: stolen goods, a uniform, a blueprint
    /// (or any backpack).
    /// </summary>
    static TutorialFacts ItemFacts(TutorialFacts facts, CharacterCold c, Items.Inventory inv, PopulationData? population)
    {
        if (inv.Backpack is not null) facts = facts with { Blueprint = true };
        if (facts is { Stolen: true, Uniform: true, Blueprint: true }) return facts;

        var (stolen, uniform, blueprint) = (facts.Stolen, facts.Uniform, facts.Blueprint);
        ScanItems(inv, c.Faction, population, ref stolen, ref uniform, ref blueprint);
        return facts with { Stolen = stolen, Uniform = uniform, Blueprint = blueprint };
    }

    /// <summary>
    /// Sets the item facts the items of <paramref name="inv"/> and of the bags among them show (<see cref="Items.Inventory.All"/>'s items, walked
    /// without an iterator); stops once all three are set.
    /// </summary>
    static void ScanItems(Items.Inventory inv, int faction, PopulationData? population, ref bool stolen, ref bool uniform, ref bool blueprint)
    {
        foreach (var item in inv.Items)
        {
            if (stolen && uniform && blueprint) return;

            stolen = stolen || item.IsStolenFor(faction, population);
            uniform = uniform || item.Uniform.Length > 0;
            blueprint = blueprint || item.ItemFunction == BlueprintFunction;
            if (item.Contents is { } bag) ScanItems(bag, faction, population, ref stolen, ref uniform, ref blueprint);
        }
    }

    /// <summary>
    /// <paramref name="facts"/> with an imprisoned player character held by fixture <paramref name="held"/>: in the Cannibals' cage, else in
    /// another faction's prison.
    /// </summary>
    TutorialFacts PrisonFacts(TutorialFacts facts, string held, PopulationData? population)
    {
        int owner = Fixtures.Find(held)?.Owner ?? -1;
        bool cannibal = population is not null && (uint)owner < (uint)population.Factions.Count && population.Factions[owner].Id == CannibalFaction;
        return cannibal ? facts with { InCannibalCage = true } : facts with { InPrison = true };
    }

    /// <summary>
    /// Whether <paramref name="p"/> lies in an outpost or a town (<see cref="TownSiteAt"/>).
    /// </summary>
    static bool InTown(PopulationData population, System.Numerics.Vector3 p) =>
        TownSiteAt(population, p) is { Town.Type: TownType.Outpost or TownType.Town };

    /// <summary>
    /// The town whose radius holds <paramref name="p"/>, the nearest such (the character's current town).
    /// </summary>
    static TownSite? TownSiteAt(PopulationData population, System.Numerics.Vector3 p)
    {
        TownSite? best = null;
        float bestD = float.MaxValue;

        foreach (var site in population.Sites)
        {
            float dx = p.X - site.Position.X, dz = p.Z - site.Position.Z, d = dx * dx + dz * dz, r = site.Town.Radius;
            if (d <= r * r && d < bestD) (best, bestD) = (site, d);
        }

        return best;
    }
}