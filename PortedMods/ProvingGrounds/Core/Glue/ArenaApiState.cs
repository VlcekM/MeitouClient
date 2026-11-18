// Port only (no C++ counterpart; task 10.1, the interop API asked for by The Wasteland Remembers' author on the Meitou Discord, 2026-11-17):
// what other Meitou code mods can read about the arena through ProvingGrounds.ProvingGroundsApi (src/Api/ProvingGroundsApi.cs).
//
// The glue (src/Game/ArenaInterop.cs) feeds this once per SlowWorld on the sim thread: every character the arena has in a bout (the running
// match's fighters, benched and knocked out ones included, its walk-in; the town bout's fighters from the walk to the ring to the end of the
// aftercare; the match prisoners from their cage back to it; the bench, borrowed-fighter and patient holds), and every few seconds the roster
// of Scratch's town fighters. It publishes an immutable view only when an answer changed; readers on any thread take the view through one
// volatile reference (ArenaInterop.View) and never see a half-built one.
//
// A character stays "in a bout" for GraceTicks after the arena lets it go: a mod that books the bout's last blow (the knock-out) a tick or two
// later, from its own queue, must still see the blow as part of the bout.
// Tests/Glue/ArenaApiStateTests.cs.
namespace ProvingGrounds.Core.Glue;

/// <summary>The arena's answers for other mods, built once and never written after (read from any thread).</summary>
public sealed class ArenaApiView<TId> where TId : notnull
{
    readonly HashSet<TId> inBout, townFighterIds;
    readonly HashSet<string> townFighterNames;

    /// <summary>No world yet (before the arena's first tick in this game, or the mod is off): every answer is false.</summary>
    public static ArenaApiView<TId> NotReady { get; } = new(false, 0, [], [], []);

    /// <summary>A world is loaded and the arena's tick system has run in it.</summary>
    public bool Ready { get; }

    /// <summary>The tick of the world this view was built on (the last change).</summary>
    public long Tick { get; }

    public int InBoutCount => inBout.Count;
    public int TownFighterCount => townFighterIds.Count;

    public ArenaApiView(bool ready, long tick, IEnumerable<TId> inBout, IEnumerable<TId> townFighterIds, IEnumerable<string> townFighterNames)
    {
        Ready = ready;
        Tick = tick;
        this.inBout = [.. inBout];
        this.townFighterIds = [.. townFighterIds];
        this.townFighterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in townFighterNames)
            if (NameKey(n) is { } k) this.townFighterNames.Add(k);
    }

    /// <summary>In a bout, being walked to or from one, benched in it, or let go less than <see cref="ArenaApiFeed{TId}.GraceTicks"/> ago.</summary>
    public bool InBout(TId id) => inBout.Contains(id);

    /// <summary>One of Scratch's town fighters (the town faction, a fighter template of the arena's catalog).</summary>
    public bool IsTownFighterId(TId id) => townFighterIds.Contains(id);

    /// <summary>A town fighter of that name (as the game shows it; spaces around it and letter case ignored).</summary>
    public bool IsTownFighter(string? name) => NameKey(name) is { } k && townFighterNames.Contains(k);

    /// <summary>The characters in a bout (the test verb's listing).</summary>
    public IReadOnlyCollection<TId> InBoutIds => inBout;

    /// <summary>The town fighters' names (the test verb's listing).</summary>
    public IReadOnlyCollection<string> TownFighterNames => townFighterNames;

    /// <summary>The name as the views compare it: trimmed; null for null or blank.</summary>
    public static string? NameKey(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}

/// <summary>Builds <see cref="ArenaApiView{TId}"/> on the sim thread (one per world). Call <see cref="Begin"/>, then <see cref="Seen"/> for every
/// character in a bout this tick, optionally <see cref="SetRoster"/>, then <see cref="End"/>, which publishes a new view only when an answer
/// changed (an unchanged tick allocates nothing).</summary>
public sealed class ArenaApiFeed<TId> where TId : notnull
{
    /// <summary>30 s of simulation time (one tick = 1/30 s, SimClock.TickSeconds).</summary>
    public const long GraceTicks = 30 * 30;

    readonly Dictionary<TId, long> lastSeen = [];
    readonly List<TId> expired = [];
    HashSet<TId> rosterIds = [];
    HashSet<string> rosterNames = new(StringComparer.OrdinalIgnoreCase);
    long tick;
    bool changed;
    volatile ArenaApiView<TId> view = ArenaApiView<TId>.NotReady;

    /// <summary>The published view (any thread).</summary>
    public ArenaApiView<TId> View => view;

    /// <summary>The characters <see cref="InBout"/> answers for now (in a bout this tick or within the grace).</summary>
    public int Count => lastSeen.Count;

    /// <summary>Starts this tick's list.</summary>
    public void Begin(long worldTick) => tick = worldTick;

    /// <summary>The character is in a bout this tick (a repeat in the same tick is harmless).</summary>
    public void Seen(TId id)
    {
        if (!lastSeen.ContainsKey(id)) changed = true;
        lastSeen[id] = tick;
    }

    /// <summary>The town fighters now (each with its name as the game shows it). Replaces the last roster; a change is published at <see cref="End"/>.</summary>
    public void SetRoster(IReadOnlyList<(TId Id, string Name)> roster)
    {
        var ids = new HashSet<TId>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in roster)
        {
            ids.Add(id);
            if (ArenaApiView<TId>.NameKey(name) is { } k) names.Add(k);
        }
        if (ids.SetEquals(rosterIds) && SameNames(names, rosterNames)) return;
        rosterIds = ids;
        rosterNames = names;
        changed = true;
    }

    // the same names, letter case included (a rename that changes only the case is still a change: the listing shows the game's spelling)
    static bool SameNames(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var n in a)
            if (!b.TryGetValue(n, out var m) || !string.Equals(n, m, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Drops the characters let go more than <see cref="GraceTicks"/> ago and publishes the view when anything changed (and always the
    /// first time, so <see cref="ArenaApiView{TId}.Ready"/> turns true on the world's first tick).</summary>
    public void End()
    {
        expired.Clear();
        foreach (var (id, seen) in lastSeen)
            if (tick - seen > GraceTicks) expired.Add(id);
        foreach (var id in expired) lastSeen.Remove(id);
        if (expired.Count > 0) changed = true;
        if (!changed && view.Ready) return;
        view = new ArenaApiView<TId>(true, tick, lastSeen.Keys, rosterIds, rosterNames);
        changed = false;
    }

    /// <summary>The world is gone: every answer false again (no reads of the old world).</summary>
    public void Clear()
    {
        lastSeen.Clear();
        rosterIds = [];
        rosterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        changed = false;
        view = ArenaApiView<TId>.NotReady;
    }
}

/// <summary>Who counts as one of Scratch's town fighters (port only, task 10.1): a character of the town faction (91, TownArena.TownFactionId)
/// whose template is a fighter of the arena's catalog (TownFighterCatalog, the town roster's own rule) or one of its unique fighters (Senn,
/// Torka, Skarn, Ressa Vane, Bum-Per, Veyr: TownChallengePolicy.Role). The glue tests the faction; this the template.</summary>
public static class ArenaApiRules
{
    public static bool IsTownFighterRecord(string? recordId) =>
        TownFighterCatalog.Find(recordId) is not null || ArenaNews.UniqueOf(recordId) >= 0;
}
