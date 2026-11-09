using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// A playable race in the editor's lists: its RACE_GROUP (the RACE buttons) and the group's playable RACEs (the SUBRACE buttons), in data order.
/// </summary>
public sealed record EditorRaceGroup(GameRecord Group, IReadOnlyList<GameRecord> Races);

/// <summary>
/// The races the character editor offers (docs/characters.md "Character editor"), found once per database.
/// </summary>
public static class PlayableRaces
{
    /// <summary>
    /// The playable races by database, made once: the editor's interface asks every frame, and the search walks every record.
    /// </summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameDatabase, IReadOnlyList<EditorRaceGroup>> Cache = new();

    /// <summary>
    /// The races the editor offers: every RACE_GROUP in data order with its <c>races</c> that are <c>playable</c> and have an <c>editor limits</c>
    /// file (fcs.def: "For it to be completely playable it must have a editor limits file and be on any race group"); groups left empty are not
    /// offered.
    /// </summary>
    public static IReadOnlyList<EditorRaceGroup> Of(GameDatabase db) =>
        Cache.GetValue(db, static db => [.. Enumerable
            .Where<EditorRaceGroup>(db.OfType(FcsRecordType.RaceGroup)
                .Select(g => new EditorRaceGroup(g, [.. g.GetReferences("races").Select(r => db.Find(r.TargetStringId))
                    .Where(r => r is { Type: FcsRecordType.Race } && r.GetBool("playable", true)
                            && r.GetPath("editor limits").Length > 0).Select(r => r!)])), g => g.Races.Count > 0)]);

    /// <summary>
    /// <see cref="Of(GameDatabase)"/> limited to <paramref name="only"/> (a start's <c>force race</c>); when that leaves none, the
    /// original says "Game start has no valid races" and offers every playable race (docs/characters.md "Character editor").
    /// </summary>
    public static IReadOnlyList<EditorRaceGroup> Of(GameDatabase db, IReadOnlyCollection<string> only)
    {
        var all = Of(db);
        if (only.Count == 0) return all;
        var limited = all.Select(g => new EditorRaceGroup(g.Group,
                [.. g.Races.Where(r => only.Contains(r.StringId))])).Where(g => g.Races.Count > 0).ToList();
        return limited.Count > 0 ? limited : all;
    }
}
