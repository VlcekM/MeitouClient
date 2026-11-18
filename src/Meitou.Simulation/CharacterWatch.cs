namespace Meitou.Simulation;

/// <summary>
/// The world's watch on characters entering and leaving its table (<see cref="CharacterTable.Watch"/>), made only while a code mod's death observer
/// or experience filter needs it. A character removed with its death not yet reported is reported first (<see cref="Bodies.DeathObservers"/>), so
/// a death in the commit or the slow world followed by the character's removal in the same tick (a squad unloaded or despawned) is never lost. A
/// character's stats are bound to the world and its id as it enters and cleared as it leaves (<see cref="Bodies.ExperienceFilters"/>), so its
/// gains are filtered from its first step, and stats that left the world keep no reference to it.
/// </summary>
sealed class CharacterWatch(World world)
{
    /// <summary>
    /// Whether this is <paramref name="of"/>'s watch (a table handed to another world gets that world's).
    /// </summary>
    internal bool Of(World of) => ReferenceEquals(world, of);

    /// <summary>
    /// The character <paramref name="id"/> has entered the table with <paramref name="cold"/>; serial phases or between ticks.
    /// </summary>
    internal void Added(CharacterId id, CharacterCold cold)
    {
        if (world.ExperienceFilters.Active) cold.Stats?.BindExperience(world, id);
    }

    /// <summary>
    /// The character in <paramref name="slot"/> is about to leave the table; serial phases or between ticks.
    /// </summary>
    internal void Removing(int slot, CharacterCold cold)
    {
        Bodies.DeathObservers.Report(world, slot, CharacterId.None, Bodies.DeathSite.Other);
        cold.Stats?.UnbindExperience();
    }
}
