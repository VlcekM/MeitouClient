using Meitou.Data.Gameplay;
using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>
/// What the tutorial triggers ask of the player's characters beyond the character snapshots (docs/game/ui-screens.md "Tutorials"): one of them in an outpost or town,
/// with stolen goods, a faction uniform or a blueprint (or, as the original's inverted test reads, any backpack), and imprisoned by another faction or by the Cannibals.
/// </summary>
public readonly record struct TutorialFacts(bool InTown, bool Stolen, bool Uniform, bool Blueprint, bool InPrison, bool InCannibalCage);

public sealed partial class World
{
    /// <summary>The Cannibals faction (<c>518-gamedata.base</c>), whose cages have their own escape tutorial.</summary>
    const string CannibalFaction = "518-gamedata.base";
    /// <summary>ITEM_BLUEPRINT (item function 11).</summary>
    const int BlueprintFunction = 11;

    TutorialFacts PublishTutorialFacts(PopulationData? population)
    {
        bool town = false, stolen = false, uniform = false, blueprint = false, prison = false, cannibal = false;
        var table = Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: true } c) continue;
            if (!town && population is not null && TownSiteAt(population, table.Next[i].Position) is { Town.Type: TownType.Outpost or TownType.Town }) town = true;
            if (c.Inventory is { } inv)
            {
                if (inv.Backpack is not null) blueprint = true;
                foreach (var item in inv.All())
                {
                    stolen |= item.IsStolenFor(c.Faction, population);
                    uniform |= item.Uniform.Length > 0;
                    blueprint |= item.ItemFunction == BlueprintFunction;
                }
            }
            if (c.InSomething == Locks.HeldState.Imprisoned && c.InWhat is { } held)
            {
                int owner = Fixtures.Find(held)?.Owner ?? -1;
                if (population is not null && (uint)owner < (uint)population.Factions.Count && population.Factions[owner].Id == CannibalFaction) cannibal = true;
                else prison = true;
            }
        }
        return new(town, stolen, uniform, blueprint, prison, cannibal);
    }

    /// <summary>The town whose radius holds <paramref name="p"/>, the nearest such (the character's current town).</summary>
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
