using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Locks;

/// <summary>
/// What the original's house record keeps of the squads housed in it (docs/game/locks.md "Building record"): the building designation (<c>+0xc4</c>),
/// the public day byte (<c>+0xc8</c>) and the squad template of the last assignment (<c>+0xf0</c>). Bars, shops, beds and public tests read these,
/// not the residents of the moment.
/// </summary>
public sealed class BuildingRecord
{
    /// <summary>
    /// BuildingDesignation (0 none); set while 0 by the setup and the assignment, over any value when a platoon is made at the building.
    /// </summary>
    public int Designation { get; set; }
    /// <summary>
    /// The <c>public day</c> of the template of the last setup; null until one ran.
    /// </summary>
    public bool? PublicDay { get; set; }
    /// <summary>
    /// The SQUAD_TEMPLATE id of the last squad assigned to the building; null: none.
    /// </summary>
    public string? Template { get; set; }
}

/// <summary>
/// The registry's building records: the designation, template and public day of the houses squads were housed in.
/// </summary>
public sealed partial class FixtureRegistry
{
    /// <summary>
    /// Meitou's GAMESTATE_BUILDING keys of a building record (designation, template) and the original's <c>is public day</c>.
    /// </summary>
    const string DesignationKey = "meitou designation", TemplateKey = "meitou template", PublicDayKey = "is public day";

    /// <summary>
    /// The building records by building key.
    /// </summary>
    readonly Dictionary<string, BuildingRecord> _records = new(StringComparer.Ordinal);

    /// <summary>
    /// The record of a building, null before any squad was housed in it.
    /// </summary>
    public BuildingRecord? RecordOf(string building) => _records.GetValueOrDefault(building);

    /// <summary>
    /// The record of a building, made when absent.
    /// </summary>
    BuildingRecord RecordFor(string building) => _records.TryGetValue(building, out var r) ? r : _records[building] = new BuildingRecord();

    /// <summary>
    /// The house setup for a template (<c>FUN_14057f1f0</c>, Verified): its designation when the building has none, its public day over the
    /// building's byte (the last setup wins).
    /// </summary>
    public void NoteSetup(string building, SquadTemplate t)
    {
        var r = RecordFor(building);
        if (r.Designation == 0) r.Designation = t.BuildingDesignation;
        r.PublicDay = t.PublicDay;
    }

    /// <summary>
    /// A platoon made at the building (<c>FUN_14057f4c0</c>, Verified): its template's designation over whatever the building had.
    /// </summary>
    public void NoteMade(string building, SquadTemplate t) => RecordFor(building).Designation = t.BuildingDesignation;

    /// <summary>
    /// A squad given the building as its home (<c>FUN_140553210</c>, Verified): the template is stored (the last wins) and its designation taken when
    /// the building has none.
    /// </summary>
    public void NoteAssigned(string building, SquadTemplate t)
    {
        var r = RecordFor(building);
        if (r.Designation == 0) r.Designation = t.BuildingDesignation;
        r.Template = t.Id;
    }

    /// <summary>
    /// The building's stored public day byte, null when no setup wrote it.
    /// </summary>
    public bool? PublicDayOf(string building) => _records.TryGetValue(building, out var r) ? r.PublicDay : null;

    /// <summary>
    /// Whether the building is public as far as a flag says: the player's toggle, else the stored public day; null when neither exists.
    /// </summary>
    public bool? PublicFlagOf(string building) => PublicOf(building) ?? PublicDayOf(building);

    /// <summary>
    /// Reads a building record from its state (nothing without Meitou's keys).
    /// </summary>
    void ReadRecord(string building, FcsRecord state)
    {
        if (!state.Ints.ContainsKey(DesignationKey) && !state.Strings.ContainsKey(TemplateKey)) return;
        _records[building] = new BuildingRecord
        {
            Designation = state.Ints.GetValueOrDefault(DesignationKey),
            Template = state.Strings.GetValueOrDefault(TemplateKey) is { Length: > 0 } id ? id : null,
            PublicDay = state.Bools.TryGetValue(PublicDayKey, out bool open) ? open : null,
        };
    }

    /// <summary>
    /// Writes a building record into its state.
    /// </summary>
    void WriteRecord(string building, FcsRecord state)
    {
        if (!_records.TryGetValue(building, out var r)) return;
        state.Ints[DesignationKey] = r.Designation;
        if (r.Template is { } id) state.Strings[TemplateKey] = id;
        else state.Strings.Remove(TemplateKey);
        if (r.PublicDay is { } open && !_publicFlags.ContainsKey(building)) state.Bools[PublicDayKey] = open;
    }

    /// <summary>
    /// Adds the building records to the world's state hash, once any squad was housed.
    /// </summary>
    void HashRecords(ref StateHasher h)
    {
        if (_records.Count == 0) return;   // only once a squad was housed, so worlds without one hash as before
        foreach (var (b, r) in _records.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            h.Add(Rng.StableHash(b));
            h.Add(r.Designation);
            h.Add(r.Template is null ? 0UL : Rng.StableHash(r.Template));
            h.Add(r.PublicDay is { } open ? open ? 2 : 1 : 0);
        }
    }
}