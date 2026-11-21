using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The town a squad's blackboard has it in and the game hour it came in (<see cref="Blackboard.InTown"/>, <see cref="Blackboard.InTownSince"/>; the
/// original's Blackboard <c>+0x138</c> and <c>+0x140</c>), on its PLATOON record as the handle <c>currenttown</c> (the null handle for none; a
/// nest's type-85 handle in one) and the float <c>towntime</c>: written by <c>FUN_14026c2d0</c> and read by <c>FUN_140272c20</c> (<b>Verified</b>,
/// docs/formats/save.md "Platoon blackboard").
/// </summary>
public sealed record CurrentTownSave(Hand Town, double Since)
{
    /// <summary>
    /// The keys: the town's handle prefix and the arrival hour.
    /// </summary>
    const string TownKey = "currenttown", SinceKey = "towntime";

    /// <summary>
    /// What the record holds; null when it holds neither key.
    /// </summary>
    public static CurrentTownSave? Read(FcsRecord platoon)
    {
        if (!platoon.Ints.ContainsKey(TownKey + "I") && !platoon.Floats.ContainsKey(SinceKey)) return null;

        return new CurrentTownSave(Hand.Read(platoon, TownKey), platoon.Floats.GetValueOrDefault(SinceKey));
    }

    /// <summary>
    /// Writes the arrival hour <paramref name="since"/> and the town the squad is in, by <paramref name="townHand"/> (a town's save handle). A nest's
    /// handle is written once the nests are (<see cref="WriteNest"/>); a town with no save handle keeps the handle the record holds; no town over a
    /// null handle keeps it as it was read (its other parts are stale values).
    /// </summary>
    public static void Write(FcsRecord platoon, CampaignTown town, double since, Func<CampaignTown, Hand> townHand)
    {
        platoon.Floats[SinceKey] = (float)since;
        if (town.Nest > 0) return;

        var hand = town.IsNone ? Hand.Null : townHand(town);
        bool keep = hand.IsNull && (!town.IsNone || Hand.Read(platoon, TownKey).IsNull);
        if (!keep) hand.Write(platoon, TownKey);
    }

    /// <summary>
    /// Writes the handle of the nest a squad is in (<see cref="Write"/>): the nest's state's <c>hand</c>, the null handle for a nest the save does
    /// not hold.
    /// </summary>
    public static void WriteNest(FcsRecord platoon, Hand nest) => nest.Write(platoon, TownKey);

    /// <summary>
    /// Puts the saved town and hour on a squad's new blackboard, as the original's load (<see cref="Applied"/>).
    /// </summary>
    public void Apply(Blackboard board, Func<Hand, CampaignTown> townOf) => (board.InTown, board.InTownSince) = Applied(board.InTown, townOf);

    /// <summary>
    /// The town and hour a squad has after the load: no town for the null handle (the next in-town step then stamps the hour and says ENTER_TOWN, as
    /// <c>FUN_1402688d0</c> does), the town or nest <paramref name="townOf"/> finds (<c>FUN_1409f9100</c> resolves a town's and a nest's handle), else
    /// (a town the world does not hold) <paramref name="started"/>, the one the squad was started in.
    /// </summary>
    public (CampaignTown Town, double Since) Applied(CampaignTown started, Func<Hand, CampaignTown> townOf)
    {
        if (Town.IsNull) return (CampaignTown.None, Since);

        return (townOf(Town) is { IsNone: false } town ? town : started, Since);
    }

    /// <summary>
    /// The town or nest a saved <c>currenttown</c> names (<c>FUN_1409f9100</c>: a type-13 or type-85 handle resolves through the handle table):
    /// a placed or player town (<see cref="WarSave.Town"/>), else the nest site the population holds the handle for; none when it names neither.
    /// </summary>
    internal static CampaignTown TownOf(World world, SavedPopulation saved, Hand h)
    {
        if (h.Type != (int)FcsRecordType.Nest) return WarSave.Town(world, saved, h);

        int site = world.SystemOf<PopulationSystem>()?.NestSiteOf(h) ?? -1;
        return site >= 0 ? CampaignTown.OfNest(site) : CampaignTown.None;
    }
}
