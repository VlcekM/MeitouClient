using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Crime;

/// <summary>
/// The crime system's thievery training: the fence's reward.
/// </summary>
public sealed partial class CrimeSystem
{
    /// <summary>
    /// What a successful fence gives the seller (<c>FUN_140793770</c>, <b>Observed</b>, only while the seller's chance of getting away is below
    /// 0.98): the generic entry with 5 x 2, or 20 x 2 when the shop caught it.
    /// </summary>
    public const float FenceXp = 10, FenceCaughtXp = 40, FenceXpBelow = 0.98f;

    /// <summary>
    /// Trains thievery by the generic continuous entry with dt 1 (<c>FUN_1408c7310(stats, 10, amount)</c>).
    /// </summary>
    public void TrainThievery(World world, int slot, float amount)
    {
        if (Xp is not { } xp || world.Characters.Cold(slot) is not { Stats: { } stats, Race: { } race }) return;

        xp.Continuous(stats, race, StatsEnumerated.Thieving, 1, amount);
    }
}
