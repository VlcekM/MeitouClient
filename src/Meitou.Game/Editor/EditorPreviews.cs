using System.Diagnostics;
using Meitou.Data.Characters;
using Meitou.Simulation;
using Meitou.Simulation.Editing;

namespace Meitou.Game.Editor;

/// <summary>
/// The looks the character editor shows on the characters in the world (<see cref="CharacterEditorPanel"/>): each draft's look, built when the
/// draft changes and shown once the renderer has prepared it, the glide from the look it replaced, and the looks the next click can make prepared
/// ahead.
/// </summary>
sealed class EditorPreviews(CharacterEditorPanel panel)
{
    /// <summary>
    /// What the editor shows of a character: the draft version and clothes it was made for, the look on screen, the one being prepared, and the look
    /// the shown one replaced since when (the glide).
    /// </summary>
    internal sealed record Preview(int Version, bool Clothes, CharacterAppearance? Shown, CharacterAppearance? Pending,
        CharacterAppearance? From = null, double Started = 0);

    /// <summary>
    /// Each character's preview while the editor has a draft of it.
    /// </summary>
    readonly Dictionary<CharacterId, Preview> _previews = [];

    /// <summary>
    /// What the catalogue of looks prepared ahead was made for: (character, race, gender, clothes).
    /// </summary>
    (CharacterId Id, string Race, bool Female, bool Clothes)? _catalogued;

    /// <summary>
    /// How long the character glides from one look to the next: about the time between two looks while a slider is dragged, so the movement is
    /// continuous.
    /// </summary>
    const double BlendSeconds = 0.12;

    /// <summary>
    /// Builds a preview look from a record id, a loadout and the character's shave (the world's appearance source; the host sets it).
    /// </summary>
    public Func<string, Loadout, bool, CharacterAppearance?>? PreviewSource { get; set; }

    /// <summary>
    /// Has the renderer prepare the look being prepared at once, ahead of its ordinary queue (the host sets it).
    /// </summary>
    public Action<CharacterAppearance>? PreviewFetch { get; set; }

    /// <summary>
    /// Has the renderer prepare a look's meshes ahead without building it, urgently or on its ordinary queue (the host sets it): the looks the next
    /// click can make.
    /// </summary>
    public Action<CharacterAppearance, bool>? PreviewWarm { get; set; }

    /// <summary>
    /// What the looks prepared ahead were made for: the next click's (character, seed, race, gender, clothes) and the catalogue's (character, race,
    /// gender, clothes).
    /// </summary>
    (CharacterId Id, int Seed, string Race, bool Female, bool Clothes)? _anticipated;

    /// <summary>
    /// The pending look already handed to <see cref="PreviewFetch"/>.
    /// </summary>
    CharacterAppearance? _warmedPending;

    /// <summary>
    /// Whether the renderer has prepared a look (the host sets it); a preview is shown only once it is.
    /// </summary>
    public Func<CharacterAppearance, bool>? PreviewReady { get; set; }

    /// <summary>
    /// The clock the previews are rebuilt and glide by.
    /// </summary>
    readonly Stopwatch _previewClock = Stopwatch.StartNew();

    /// <summary>
    /// When the last preview look was built.
    /// </summary>
    double _lastPreviewBuild = double.NegativeInfinity;

    /// <summary>
    /// The look to draw for a character while the editor has a draft of it (null: its own). A changed draft gets a new look (at most every 0.12 s
    /// while a slider is dragged); the last ready one stays on screen until the renderer has prepared the new one (<see cref="PreviewReady"/>), so
    /// the character never blinks out.
    /// </summary>
    public CharacterAppearance? EditorPreview(CharacterId id, CharacterAppearance? own)
    {
        if (!panel.EditorOpen || !panel.Editor!.Drafts.TryGetValue(id, out var draft) || own?.Loadout is not { } loadout || PreviewSource is null)
            return null;

        bool clothes = panel.Editor.Clothes;
        if (draft.Version == 0 && clothes)
        {
            _previews.Remove(id);
            return null;
        }
        var entry = _previews.TryGetValue(id, out var found) ? found : new Preview(-1, true, own, null);
        double now = _previewClock.Elapsed.TotalSeconds;
        if (entry.Pending is { } pending && (PreviewReady?.Invoke(pending) ?? true))
            entry = entry with { Shown = pending, Pending = null, From = entry.Shown ?? own, Started = now };
        if ((entry.Version != draft.Version || entry.Clothes != clothes) && now - _lastPreviewBuild >= 0.12)
        {
            _lastPreviewBuild = now;
            if (panel.EditorData is { } data && Simulation.Saving.SavedKit.Wear(loadout, draft.Record, data.Db) is { } worn)
            {
                if (!clothes)
                {
                    worn.Clothing.Clear();
                    worn.Backpack = null;
                }
                if (PreviewSource(own.Character?.StringId ?? "", worn, own.Shaved) is { } look) entry = entry with { Pending = look };
            }
            entry = entry with { Version = draft.Version, Clothes = clothes };
        }
        _previews[id] = entry;
        if (entry.Pending is { } waiting && !ReferenceEquals(waiting, _warmedPending))
        {
            _warmedPending = waiting;
            PreviewFetch?.Invoke(waiting);
        }
        Anticipate(id, draft, loadout, own);
        return entry.Shown ?? own;
    }

    /// <summary>
    /// Prepares ahead the looks the next click can give, so their meshes are ready when it comes (the original loads its meshes at once): urgently
    /// RANDOMISE ALL's next roll (the rolls are seeded) and, at creation, the other gender and the next and previous race and subrace, again whenever
    /// the seed, race, gender or clothes change; then, on the ordinary queue, a look with each head, hair and beard of the race and gender, once for
    /// each. Looks whose meshes are all prepared are skipped by the renderer.
    /// </summary>
    void Anticipate(CharacterId id, CharacterDraft draft, Loadout loadout, CharacterAppearance own)
    {
        if (panel.Editor is not { } s || PreviewWarm is null || PreviewSource is null || panel.EditorData is not { } data) return;
        void Warm(IEnumerable<Data.Fcs.FcsRecord> records, bool urgent)
        {
            foreach (var record in records)
                if (Simulation.Saving.SavedKit.Wear(loadout, record, data.Db) is { } worn)
                {
                    if (!s.Clothes)
                    {
                        worn.Clothing.Clear();
                        worn.Backpack = null;
                    }
                    if (PreviewSource(own.Character?.StringId ?? "", worn, own.Shaved) is { } look) PreviewWarm(look, urgent);
                }
        }
        if (_anticipated != (id, s.Seed, draft.Race.StringId, draft.Female, s.Clothes))
        {
            _anticipated = (id, s.Seed, draft.Race.StringId, draft.Female, s.Clothes);
            var next = new List<Data.Fcs.FcsRecord> { draft.RandomisedLook(s.Seed) };
            if (s.Kind == EditKind.Creation)
            {
                if (!draft.Race.GetBool("single gender")) next.Add(draft.SwitchedLook(draft.Race, !draft.Female, s.Seed));
                var groups = panel.EditorRaces(data.Db);
                int g = CharacterEditorPanel.IndexOf(groups, draft.Race);
                if (g >= 0)
                {
                    var races = groups[g].Races;
                    int r = races.ToList().FindIndex(x => x.StringId == draft.Race.StringId);
                    var neighbours = new[] { races[(r + 1) % races.Count], races[(r + races.Count - 1) % races.Count],
                        groups[(g + 1) % groups.Count].Races[0], groups[(g + groups.Count - 1) % groups.Count].Races[0] };
                    foreach (var race in neighbours.DistinctBy(x => x.StringId).Where(x => x.StringId != draft.Race.StringId))
                        next.Add(draft.SwitchedLook(race, draft.Female && !race.GetBool("single gender"), s.Seed));
                }
            }
            Warm(next, urgent: true);
        }
        if (_catalogued != (id, draft.Race.StringId, draft.Female, s.Clothes))
        {
            _catalogued = (id, draft.Race.StringId, draft.Female, s.Clothes);
            var catalogue = new List<Data.Fcs.FcsRecord>();
            foreach (var kind in new[] { EditorControlKind.Head, EditorControlKind.HairStyle, EditorControlKind.Beard })
                for (int i = 0; i < draft.Options(kind).Count; i++)
                    if (draft.Options(kind)[i] is not null) catalogue.Add(draft.WithOption(kind, i));
            Warm(catalogue, urgent: false);
        }
    }

    /// <summary>
    /// The preview look being prepared for a character (the host lists it hidden, so the renderer builds it), or null.
    /// </summary>
    public CharacterAppearance? EditorPending(CharacterId id) => panel.EditorOpen && _previews.TryGetValue(id, out var entry) ? entry.Pending : null;

    /// <summary>
    /// The look the character's shown one has just replaced and how far the glide to the shown one is (0..1), or null once it has arrived (the
    /// renderer mixes the two).
    /// </summary>
    public (CharacterAppearance From, float Blend)? EditorBlend(CharacterId id)
    {
        if (!panel.EditorOpen || !_previews.TryGetValue(id, out var entry) || entry.From is not { } from) return null;
        double t = (_previewClock.Elapsed.TotalSeconds - entry.Started) / BlendSeconds;
        return t >= 1 ? null : (from, (float)Math.Max(t, 0));
    }

    /// <summary>
    /// Forgets every preview (the editor opens or closes, or the game leaves the world).
    /// </summary>
    internal void Clear() => _previews.Clear();
}
