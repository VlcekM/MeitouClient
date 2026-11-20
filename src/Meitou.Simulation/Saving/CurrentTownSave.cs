using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The town a squad's blackboard has it in and the game hour it came in (<see cref="Blackboard.InTown"/>, <see cref="Blackboard.InTownSince"/>; the
/// original's Blackboard <c>+0x138</c> and <c>+0x140</c>), on its PLATOON record as the handle <c>currenttown</c> (the null handle for none) and the
/// float <c>towntime</c>: written by <c>FUN_14026c2d0</c> and read by <c>FUN_140272c20</c> (<b>Verified</b>, docs/formats/save.md "Platoon
/// blackboard").
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
    /// Writes the town <paramref name="board"/> has the squad in, by <paramref name="townHand"/> (a town's save handle), and its arrival hour. A town
    /// with no save handle (a nest) keeps the handle the record holds, as does a record written without a blackboard; no town over a null handle
    /// keeps it as it was read (its other parts are stale values).
    /// </summary>
    public static void Write(FcsRecord platoon, Blackboard? board, Func<CampaignTown, Hand>? townHand)
    {
        if (board is null || townHand is null) return;

        var hand = board.InTown.IsNone ? Hand.Null : townHand(board.InTown);
        bool keep = hand.IsNull && (!board.InTown.IsNone || Hand.Read(platoon, TownKey).IsNull);
        if (!keep) hand.Write(platoon, TownKey);
        platoon.Floats[SinceKey] = (float)board.InTownSince;
    }

    /// <summary>
    /// Puts the saved town and hour on a squad's new blackboard, as the original's load: no town for the null handle (the next in-town step then
    /// stamps the hour and says ENTER_TOWN, as <c>FUN_1402688d0</c> does), the town <paramref name="townOf"/> finds, else (a nest, or a town the
    /// world does not hold) the one the blackboard was started in.
    /// </summary>
    public void Apply(Blackboard board, Func<Hand, CampaignTown> townOf)
    {
        board.InTownSince = Since;
        if (Town.IsNull)
        {
            board.InTown = CampaignTown.None;
            return;
        }

        if (townOf(Town) is { IsNone: false } town) board.InTown = town;
    }
}
