using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Production;

/// <summary>
/// Who may take a mod's job (<see cref="ModJobKind.Takers"/>).
/// </summary>
[Flags]
public enum ModJobTakers
{
    /// <summary>
    /// Nobody.
    /// </summary>
    None = 0,

    /// <summary>
    /// The player's people (not animals).
    /// </summary>
    People = 1,

    /// <summary>
    /// The player's animals.
    /// </summary>
    Animals = 2,
}

/// <summary>
/// A job a code mod adds (docs/modding.md "Mod jobs"): the building context menu of a finished player building of record <paramref name="Building"/>
/// offers <paramref name="Caption"/> to a selection holding a <paramref name="Takers"/> character, and each such character then lists
/// <paramref name="Text"/>: (the building's name) in its jobs list. <paramref name="Id"/> is the mod's own name for it (one mod per id).
/// </summary>
public sealed record ModJobKind(string Id, string Building, string Caption, string Text, ModJobTakers Takers);

/// <summary>
/// One mod job a character holds: the kind's <see cref="ModJobKind.Id"/> and the player building (<see cref="Construction.BuildSite.Id"/>) it is
/// on.
/// </summary>
public sealed record ModJob(string Kind, string Site);

/// <summary>
/// Gives characters (the selection when empty) the mod job <paramref name="Kind"/> on <paramref name="Site"/> (the building menu's mod verb).
/// </summary>
public sealed record AddModJob(IReadOnlyList<CharacterId> Characters, string Kind, string Site) : SimCommand;

/// <summary>
/// Takes the mod job <paramref name="Kind"/> off a character (the jobs list's X on its row).
/// </summary>
public sealed record RemoveModJob(CharacterId Character, string Kind) : SimCommand;

/// <summary>
/// The mod jobs (docs/modding.md "Mod jobs", engine API, not in the original). The engine only offers and lists them: a character holds at most one
/// job of a kind (<see cref="CharacterCold.ModJobs"/>, a new building replacing the old) and the mod's own system reads the list in the tick and
/// does the work, saves it and takes a job off when its building goes. Filled once at startup, before the first world; read by any thread after.
/// </summary>
public static class ModJobs
{
    /// <summary>
    /// The kinds in registration order.
    /// </summary>
    static readonly List<ModJobKind> Kinds = [];

    /// <summary>
    /// Every registered kind.
    /// </summary>
    public static IReadOnlyList<ModJobKind> All => Kinds;

    /// <summary>
    /// The kind with this id, or null.
    /// </summary>
    public static ModJobKind? Of(string id) => Kinds.Find(k => k.Id == id);

    /// <summary>
    /// The kinds offered on a building of record <paramref name="building"/>.
    /// </summary>
    public static IEnumerable<ModJobKind> On(string building) => Kinds.Where(k => k.Building == building);

    /// <summary>
    /// Why <paramref name="kind"/> cannot be registered (an empty field, no takers, a taken id), or null.
    /// </summary>
    public static string? Refusal(ModJobKind kind)
    {
        if (string.IsNullOrWhiteSpace(kind.Id) || string.IsNullOrWhiteSpace(kind.Building)) return "A mod job needs an id and a building record.";
        if (string.IsNullOrWhiteSpace(kind.Caption) || string.IsNullOrWhiteSpace(kind.Text)) return "A mod job needs a caption and a list text.";
        if (kind.Takers == ModJobTakers.None) return "A mod job needs takers.";

        return Of(kind.Id) is null ? null : $"The mod job '{kind.Id}' is taken.";
    }

    /// <summary>
    /// Registers a kind (the mod host, once a mod's <c>Initialize</c> returned). Throws when <see cref="Refusal"/> refuses it.
    /// </summary>
    public static void Add(ModJobKind kind)
    {
        if (Refusal(kind) is { } refusal) throw new ArgumentException(refusal, nameof(kind));
        Kinds.Add(kind);
    }

    /// <summary>
    /// Forgets every kind (tests).
    /// </summary>
    internal static void Clear() => Kinds.Clear();

    /// <summary>
    /// Whether a character may take the kind: a living player character whose people/animal side the kind's takers include.
    /// </summary>
    public static bool Takes(ModJobKind kind, CharacterCold cold)
    {
        var side = SquadRegistry.IsAnimal(cold) ? ModJobTakers.Animals : ModJobTakers.People;
        return cold is { IsPlayer: true, DiedTick: < 0 } && (kind.Takers & side) != 0;
    }

    /// <summary>
    /// <see cref="AddModJob"/>: each named player character the kind takes holds it on the site, when the site is a finished building of the
    /// player's faction and of the kind's record. Serial phases only.
    /// </summary>
    internal static void Give(World world, AddModJob add, IReadOnlyList<CharacterId> who)
    {
        if (Of(add.Kind) is not { } kind || world.Buildings.Find(add.Site) is not { Complete: true } site) return;
        if (site.Record != kind.Building || site.Owner != world.Player.Faction) return;

        var table = world.Characters;
        foreach (var id in who)
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold || !Takes(kind, cold)) continue;

            cold.ModJobs.RemoveAll(j => j.Kind == kind.Id);
            cold.ModJobs.Add(new ModJob(kind.Id, site.Id));
        }
    }

    /// <summary>
    /// <see cref="RemoveModJob"/>: the character no longer holds a job of the kind. Serial phases only.
    /// </summary>
    internal static void Take(World world, RemoveModJob remove)
    {
        if (world.Characters.TryResolveNext(remove.Character, out int slot) && world.Characters.Cold(slot) is { } cold)
            cold.ModJobs.RemoveAll(j => j.Kind == remove.Kind);
    }
}
