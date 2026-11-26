using System.Numerics;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Player;

/// <summary>
/// The pause of a character holding an inventory window it was told to open (Meitou only, <b>engine choice</b>; docs/simulation.md "Inventory
/// pause"): the loot window (<see cref="Carrying.OpenLootOrder"/>) or a container's (<see cref="Items.OpenContainerOrder"/>). Its order keeps
/// running while the window is open, so no job, goal or reaction takes it off (<see cref="DecisionSystem.Ordered"/>); the player's next order
/// or the window's closing ends it. Combat ends it only when the character was out of combat when told (<see cref="Told"/>,
/// <see cref="CombatEnds"/>). A window the player closes holds the character's jobs <see cref="ResumeSeconds"/> more (<see cref="Closed"/>).
/// </summary>
public static class InventoryPause
{
    #region Rules

    /// <summary>
    /// How long the jobs of a character whose window the player closed wait before they run again.
    /// </summary>
    public const float ResumeSeconds = 0.5f;

    /// <summary>
    /// The character in <paramref name="slot"/> is told to open a window: its pause ignores combat when it or an ally near it is in combat now.
    /// </summary>
    internal static void Told(World world, int slot) => world.Characters.Cold(slot)!.PauseIgnoresCombat = Fighting(world, slot);

    /// <summary>
    /// Whether combat ends the pause of the character in <paramref name="slot"/>: it was out of combat when told, and it or an ally near it is in
    /// combat now.
    /// </summary>
    internal static bool CombatEnds(World world, int slot, CharacterCold cold) => !cold.PauseIgnoresCombat && Fighting(world, slot);

    /// <summary>
    /// The window <paramref name="opener"/> held was closed by the player: its jobs wait <see cref="ResumeSeconds"/>.
    /// </summary>
    internal static void Closed(World world, CharacterId opener)
    {
        if (!world.Characters.TryResolveNext(opener, out int slot)) return;

        var cold = world.Characters.Cold(slot)!;
        cold.PauseIgnoresCombat = false;
        cold.JobsWaitUntil = world.Tick + (long)MathF.Ceiling(ResumeSeconds / world.TickSeconds);
    }

    /// <summary>
    /// Whether the character's jobs still wait after its window closed (<see cref="Closed"/>).
    /// </summary>
    internal static bool JobsWait(World world, CharacterCold cold) => cold.JobsWaitUntil > world.Tick;

    /// <summary>
    /// Whether the character's jobs' wait is over and not yet used up: the decision step asks its chooser again then (the character stands on the
    /// order's provenance, which no turn re-asks) and clears it.
    /// </summary>
    internal static bool WaitOver(World world, CharacterCold cold) => cold.JobsWaitUntil != 0 && cold.JobsWaitUntil <= world.Tick;

    #endregion

    #region Focus

    /// <summary>
    /// A window opened for the player's character in <paramref name="slot"/>: it alone is selected and its squad is shown, as a click on it.
    /// </summary>
    internal static void Focus(World world, int slot)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var player = world.Player;

        if (!cold.IsPlayer || world.Squads.IsIndependent(cold.SquadId)) return;

        player.Inspected = CharacterId.None;
        player.Selection.Clear();
        player.Selection.Add(table.IdOf(slot));

        if (world.Squads.Find(cold.SquadId) is { } squad && squad.Faction == player.Faction) player.Squad = squad.Id;
    }

    #endregion

    #region Combat

    /// <summary>
    /// How near an ally of the character's faction counts for its combat (the original's <c>isWithin50Meters</c>,
    /// <see cref="AiSystem.Within50Meters"/>, on the ground plane).
    /// </summary>
    public const float AllyReach = AiSystem.Within50Meters;

    /// <summary>
    /// Whether the character in <paramref name="slot"/> is in combat (<see cref="InCombat"/>, or a melee threat on it,
    /// <see cref="CombatSystem.ImmediateThreat"/>) or an ally of its faction within <see cref="AllyReach"/> is (<see cref="InCombat"/>).
    /// </summary>
    static bool Fighting(World world, int slot)
    {
        var table = world.Characters;
        if (InCombat(table, slot)) return true;
        if (world.SystemOf<CombatSystem>() is { } combat && combat.ImmediateThreat(world, slot)) return true;

        return AllyFighting(world, slot);
    }

    /// <summary>
    /// Whether an ally of the character's faction (alive, not down) within <see cref="AllyReach"/> of it is in combat.
    /// </summary>
    static bool AllyFighting(World world, int slot)
    {
        var table = world.Characters;
        var me = table.Cold(slot)!;
        var at = Flat(table.Next[slot].Position);

        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !table.Next[s].Alive || table.Cold(s) is not { } ally || ally.Faction != me.Faction) continue;
            if (Bodies.CharacterFacts.Down(ally) || Vector2.Distance(at, Flat(table.Next[s].Position)) > AllyReach) continue;
            if (InCombat(table, s)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the character in <paramref name="slot"/> fights: in combat (<see cref="CharacterCold.InCombat"/>) or attacking.
    /// </summary>
    static bool InCombat(CharacterTable table, int slot) =>
        table.Cold(slot)!.InCombat || table.Next[slot].Task == (byte)CharacterTask.Attack;

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    #endregion
}
