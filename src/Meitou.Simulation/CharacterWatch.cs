namespace Meitou.Simulation;

/// <summary>
/// The world's watch on characters leaving its table (<see cref="CharacterTable.Watch"/>), made only while a code mod's death observer needs it: a
/// character removed with its death not yet reported is reported first (<see cref="Bodies.DeathObservers"/>), so a death in the commit or the slow
/// world followed by the character's removal in the same tick (a squad unloaded or despawned) is never lost.
/// </summary>
sealed class CharacterWatch(World world)
{
    /// <summary>
    /// Whether this is <paramref name="of"/>'s watch (a table handed to another world gets that world's).
    /// </summary>
    internal bool Of(World of) => ReferenceEquals(world, of);

    /// <summary>
    /// The character in <paramref name="slot"/> is about to leave the table; serial phases or between ticks.
    /// </summary>
    internal void Removing(int slot) => Bodies.DeathObservers.Report(world, slot, CharacterId.None, Bodies.DeathSite.Other);
}
