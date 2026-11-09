using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The AI's guard and stranger rules: the buildings strangers visit and the town guards' posts.
/// </summary>
public sealed partial class AiSystem
{
    /// <summary>
    /// The BuildingDesignation values of the shop (1), the bar (3) and the headquarters (8): the buildings strangers visit (<c>FUN_14099edd0</c>,
    /// <b>Verified</b>: designation <c>+0xc4</c> in {1, 3, 8}).
    /// </summary>
    const int DesignationShop = 1, DesignationHq = 8;

    /// <summary>
    /// The static guard's finder factor (goals 38 and 268, <c>FUN_14099edd0</c>, <b>Verified</b>): in a public home of designation shop, bar or
    /// headquarters of a real faction, a sensed neutral stranger (the sense iterator takes NEUTRAL and leaves out allies, KO and imprisoned, and the
    /// tags 0x20 and 0x80) that does not sneak and stands inside a building other than the home is drawn uniformly; the guard then stands at the
    /// guard post nearest it (<see cref="AiMind.GuardVisitorAt"/>, read by <see cref="SpotFor"/>) with factor 1.0, else the plain post with 0.5. The
    /// home's designation is its first squad's template's (<see cref="Note"/>'s simplification).
    /// </summary>
    float GuardFactor(World world, CharacterId self, Blackboard board, AiMind mind)
    {
        mind.GuardVisitorAt = null;
        var table = world.Characters;
        if (board.Home is not { } home || !board.HomePublic || _aggro?.SensesOf(self.Slot) is not { } senses
            || table.Cold(self.Slot) is not { } me) return 0.5f;

        int designation = TemplateOf(mind.Squad.TemplateId).Template?.BuildingDesignation ?? 0;
        if (designation is not (DesignationShop or DesignationHq or DesignationBar) || me.Faction < 0
            || Relations.Factions[me.Faction].NotReal) return 0.5f;

        const SenseTag excluded = SenseTag.Ally | SenseTag.Ko | SenseTag.Imprisoned;
        var strangers = new List<Vector3>();
        foreach (var e in senses.Entries)
        {
            if ((e.Tags & SenseTag.Neutral) == 0 || (e.Tags & excluded) != 0
                || (uint)e.Target >= (uint)table.Previous.Length || !table.Previous[e.Target].Alive
                || table.Previous[e.Target].Generation != e.Generation)
                continue;

            if (table.Cold(e.Target) is not { } other || (other.Orders & CharacterOrders.Sneak) != 0 || !other.CarriedBy.IsNone)
                continue;

            if (world.Sight.BuildingOf(e.Target) is { } inside && inside != home.Key)
                strangers.Add(table.Previous[e.Target].Position);
        }

        if (strangers.Count == 0) return 0.5f;

        int pick = strangers.Count == 1 ? 0 : (int)(TargetRoll(world, self, AiBehaviour.Post) % (ulong)strangers.Count);
        var at = strangers[pick];
        if (Nearest(home, HomeSpotKind.Guard, at) is null) return 0.5f;

        mind.GuardVisitorAt = at;
        return 1;
    }

    /// <summary>
    /// The spot of <paramref name="kind"/> nearest <paramref name="to"/> (3D, <c>FUN_14038ab50</c>); null when the building has none.
    /// </summary>
    static HomeSpot? Nearest(TownBuilding home, HomeSpotKind kind, Vector3 to)
    {
        HomeSpot? best = null;
        float bestD = float.MaxValue;
        foreach (var s in home.Spots)
        {
            if (s.Kind != kind) continue;
            float d = Vector3.DistanceSquared(s.Position, to);
            if (d < bestD) (best, bestD) = (s, d);
        }

        return best;
    }
}