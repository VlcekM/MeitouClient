using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The beds of a squad's home its sleepers take: the first step of the NPC bed search (<c>FUN_1408cfe00</c>, docs/game/locks.md "Bed search, other
/// characters", <b>Verified</b>). Its candidates are the home building's BF_BED furniture (function 6, not skeleton beds) in the building's order, and
/// a sleeper takes the first that is free and that no squad mate has as its target. A home with such beds gives no other candidates, so with all of
/// them taken the search finds no bed and going to bed is not wanted. The thinks run in parallel, so the targets are claimed on the package cadence
/// (serial, member order, <see cref="Claim"/>) for every member that may go to bed at this hour, and the thinks only read them (engine choice for
/// which members get the beds). Derived, not hashed.
/// </summary>
sealed class HomeBedClaims
{
    /// <summary>
    /// The beds the members claimed before this pass, by slot; scratch.
    /// </summary>
    readonly Dictionary<int, string> _kept = [];

    /// <summary>
    /// The beds claimed in this pass; scratch.
    /// </summary>
    readonly HashSet<string> _taken = [];

    /// <summary>
    /// Claims the squad's home beds in member order: the bed a member lies in, then the bed a member that may sleep keeps (the search keeps a target
    /// already set, while the bed is still free), then for each other member that may sleep the first free bed no one claimed. A member that may
    /// sleep is going to bed, or its role list has going to bed open at this hour; any other holds none.
    /// </summary>
    public void Claim(World world, Blackboard board, Squad squad)
    {
        _kept.Clear();
        foreach (var (slot, bed) in board.BedClaims) _kept[slot] = bed;
        board.BedClaims.Clear();
        _taken.Clear();
        if (board.Home is not { } home) return;

        ClaimLying(world, board, squad);
        ClaimKept(world, board, squad);
        ClaimNew(world, board, squad, home);
    }

    /// <summary>
    /// Whether the home's beds are the sleeper's candidates: it has a home within reach with at least one BF_BED bed.
    /// </summary>
    internal static bool HasBeds(World world, Blackboard board, AiMind mind)
    {
        if (board.Home is not { } home || mind.HomeUnreachable) return false;

        foreach (var f in world.Fixtures.OfBuilding(home.Key))
            if (IsHomeBed(f)) return true;

        return false;
    }

    /// <summary>
    /// The home bed the sleeper in <paramref name="slot"/> goes to: the one it claimed while that is still free (or its own), else the free bed of the
    /// home no squad mate claimed that the next pass would give it (the members before it that may sleep at this hour and hold none take the ones
    /// before); null when it has none (no home within reach, no beds, or all taken).
    /// </summary>
    internal static Fixture? Of(World world, Blackboard board, AiMind mind, int slot)
    {
        if (board.Home is not { } home || mind.HomeUnreachable) return null;

        var table = world.Characters;
        if (board.BedClaims.TryGetValue(slot, out var id) && world.Fixtures.Find(id) is { } claimed && FreeFor(table, claimed, slot))
            return claimed;

        int before = UnclaimedBefore(world, board, mind.Squad, slot);
        foreach (var f in world.Fixtures.OfBuilding(home.Key))
        {
            if (!IsHomeBed(f) || !FixtureRegistry.IsFree(table, f) || ClaimedByMate(board, slot, f.Id)) continue;
            if (before-- == 0) return f;
        }

        return null;
    }

    /// <summary>
    /// How many members before the one in <paramref name="slot"/> (member order) hold no bed, lie in none and have going to bed open at this hour in
    /// their list. Only what no parallel think writes is read: the claims, the beds lain in and the role lists.
    /// </summary>
    static int UnclaimedBefore(World world, Blackboard board, Squad squad, int slot)
    {
        var table = world.Characters;
        int count = 0;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s)) continue;
            if (s == slot) return count;
            if (board.BedClaims.ContainsKey(s) || table.Cold(s) is not { Ai: not null, InSomething: not HeldState.Bed } cold) continue;
            if (BedOpen(world, board, squad, id, cold)) count++;
        }

        return count;
    }

    /// <summary>
    /// The members lying in a bed hold it.
    /// </summary>
    void ClaimLying(World world, Blackboard board, Squad squad)
    {
        var table = world.Characters;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { InSomething: HeldState.Bed, InWhat: { } bed }) continue;

            board.BedClaims[s] = bed;
            _taken.Add(bed);
        }
    }

    /// <summary>
    /// A member going to bed keeps the bed it claimed while that is free and no one before it took it.
    /// </summary>
    void ClaimKept(World world, Blackboard board, Squad squad)
    {
        var table = world.Characters;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || board.BedClaims.ContainsKey(s) || !MaySleep(world, board, squad, id, table.Cold(s))) continue;
            if (!_kept.TryGetValue(s, out var bed) || _taken.Contains(bed)) continue;
            if (world.Fixtures.Find(bed) is not { } kept || !FixtureRegistry.IsFree(table, kept)) continue;

            board.BedClaims[s] = bed;
            _taken.Add(bed);
        }
    }

    /// <summary>
    /// Each other member that may sleep claims the first free bed of the home no one claimed.
    /// </summary>
    void ClaimNew(World world, Blackboard board, Squad squad, TownBuilding home)
    {
        var table = world.Characters;
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || board.BedClaims.ContainsKey(s)) continue;
            if (table.Cold(s) is not { Ai: { HomeUnreachable: false } } cold || !MaySleep(world, board, squad, id, cold)) continue;
            if (FirstFree(world, home) is not { } bed) return;

            board.BedClaims[s] = bed.Id;
            _taken.Add(bed.Id);
        }
    }

    /// <summary>
    /// The first bed of the home that is free and not claimed in this pass.
    /// </summary>
    Fixture? FirstFree(World world, TownBuilding home)
    {
        foreach (var f in world.Fixtures.OfBuilding(home.Key))
            if (IsHomeBed(f) && !_taken.Contains(f.Id) && FixtureRegistry.IsFree(world.Characters, f)) return f;

        return null;
    }

    /// <summary>
    /// Whether the member may go to bed: its current goal is going to bed (153, 239), or the list it plans with (its role's, or its master squad's
    /// Slave AI Goals when it obeys one) has going to bed open at this hour.
    /// </summary>
    static bool MaySleep(World world, Blackboard board, Squad squad, CharacterId id, CharacterCold? cold) =>
        cold?.Ai is not null && (IsBedGoal(cold.Ai.Goal?.Task.Type) || BedOpen(world, board, squad, id, cold));

    /// <summary>
    /// Whether the list the member plans with has going to bed open at this hour.
    /// </summary>
    static bool BedOpen(World world, Blackboard board, Squad squad, CharacterId id, CharacterCold cold)
    {
        var roles = AiSystem.ObeyedRoles(world, squad, cold) ?? board.Roles[(int)AiSystem.RoleOf(squad, id, cold)];
        int hour = world.Hour;
        foreach (var goal in roles)
            if (IsBedGoal(goal.Task.Type) && goal.InWindow(hour)) return true;

        return false;
    }

    /// <summary>
    /// Whether a goal row is going to bed (153, 239).
    /// </summary>
    static bool IsBedGoal(TaskType? type) => type is TaskType.GoHomeAndGoToBed or TaskType.GoHomeAndGoToBedSecure;

    /// <summary>
    /// Whether a fixture is a bed the home's list takes: function 6 BF_BED (the lister <c>FUN_14054d4f0</c> matches the function exactly). A bed is
    /// of function 6 or 25, so it is any bed but a skeleton bed.
    /// </summary>
    static bool IsHomeBed(Fixture f) => f.Kind == FixtureKind.Bed && f.Bed.Function != TownBuildings.SkeletonBedFunction;

    /// <summary>
    /// Whether the bed is free, or the character in <paramref name="slot"/> lies in it.
    /// </summary>
    static bool FreeFor(CharacterTable table, Fixture bed, int slot) =>
        FixtureRegistry.IsFree(table, bed) || table.Cold(slot) is { InSomething: HeldState.Bed } c && c.InWhat == bed.Id;

    /// <summary>
    /// Whether a squad mate of the character in <paramref name="slot"/> claimed the bed.
    /// </summary>
    static bool ClaimedByMate(Blackboard board, int slot, string bed)
    {
        foreach (var (other, claimed) in board.BedClaims)
            if (other != slot && claimed == bed) return true;

        return false;
    }
}
