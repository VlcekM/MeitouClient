using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// The character editor (<c>CharacterEditWindow</c>, layout <c>Kenshi_CharacterEditor</c>; docs/characters.md "Character editor"): the start's characters after a new
/// game, or one character at a plastic surgeon. The window edits <see cref="CharacterDraft"/>s, shows them on the characters in the world (<see cref="EditorPreview"/>),
/// and on CONFIRM sends each one as an <see cref="EditCharacter"/> command; nothing in the world changes before that.
/// </summary>
sealed partial class PlayerInterface
{
    sealed class EditorSession(EditKind kind, IReadOnlyList<CharacterId> characters, CharacterId surgeon, bool paid = false)
    {
        /// <summary>A surgeon's dialogue took the money already (<see cref="EditCharacter.Paid"/>).</summary>
        public bool Paid { get; } = paid;
        public EditKind Kind { get; } = kind;
        public IReadOnlyList<CharacterId> Characters { get; } = characters;
        public CharacterId Surgeon { get; } = surgeon;
        public int Index;
        public EditorTab Tab = EditorTab.Body;
        public bool Clothes = true;
        public Dictionary<CharacterId, CharacterDraft> Drafts { get; } = [];
        public Dictionary<CharacterId, string> StartNames { get; } = [];
        public int Seed = Environment.TickCount;
        public CharacterId Current => Characters[Index];
        /// <summary>The camera's orbit, the character it was last set for, and the button dragging it (with where the pointer was last).</summary>
        public EditorOrbit Orbit { get; } = new();
        public CharacterId OrbitCharacter;
        public (Meitou.Engine.Input.MouseButton Button, Vector2 Last)? Drag;
    }

    EditorSession? editor;
    /// <summary>The game data and the install root the editor reads races and editor limits from (the host sets them).</summary>
    public (GameDatabase Db, string InstallRoot)? EditorData { get; set; }
    /// <summary>Builds a preview look from a record id and a loadout (the world's appearance source; the host sets it).</summary>
    public Func<string, Loadout, CharacterAppearance?>? PreviewSource { get; set; }
    /// <summary>What the editor shows of a character: the draft version and clothes it was made for, the look on screen, the one being prepared, and the look the shown one replaced since when (the glide).</summary>
    sealed record Preview(int Version, bool Clothes, CharacterAppearance? Shown, CharacterAppearance? Pending, CharacterAppearance? From = null, double Started = 0);
    readonly Dictionary<CharacterId, Preview> previews = [];
    /// <summary>How long the character glides from one look to the next: about the time between two looks while a slider is dragged, so the movement is continuous.</summary>
    const double BlendSeconds = 0.12;
    /// <summary>Has the renderer prepare the look being prepared at once, ahead of its ordinary queue (the host sets it).</summary>
    public Action<CharacterAppearance>? PreviewFetch { get; set; }
    /// <summary>Has the renderer prepare a look's meshes ahead without building it, urgently or on its ordinary queue (the host sets it): the looks the next click can make.</summary>
    public Action<CharacterAppearance, bool>? PreviewWarm { get; set; }
    /// <summary>What the looks prepared ahead were made for: the next click's (character, seed, race, gender, clothes) and the catalogue's (character, race, gender, clothes).</summary>
    (CharacterId Id, int Seed, string Race, bool Female, bool Clothes)? anticipated;
    (CharacterId Id, string Race, bool Female, bool Clothes)? catalogued;
    /// <summary>The pending look already handed to <see cref="PreviewFetch"/>.</summary>
    CharacterAppearance? warmedPending;
    /// <summary>Whether the renderer has prepared a look (the host sets it); a preview is shown only once it is.</summary>
    public Func<CharacterAppearance, bool>? PreviewReady { get; set; }
    readonly Stopwatch previewClock = Stopwatch.StartNew();
    double lastPreviewBuild = double.NegativeInfinity;

    public bool EditorOpen => editor is not null && faithfulScreens.IsOpen("editor");
    /// <summary>The character the editor shows and its tab, for the host's camera; null while the editor is closed.</summary>
    public (CharacterId Character, EditorTab Tab)? EditorFocus => EditorOpen ? (editor!.Current, editor.Tab) : null;
    /// <summary>Every character the open editor edits, the shown one included; empty while it is closed.</summary>
    public IReadOnlyList<CharacterId> EditorCharacters => EditorOpen ? editor!.Characters : [];
    /// <summary>The host draws the editor's scene in the world (the Meitou bar, <see cref="EditorBar"/>): the backdrop is left out.</summary>
    public bool EditorInWorld { get; set; }

    /// <summary>
    /// Opens the editor for <paramref name="characters"/> (the start's squad at creation; one character at a surgeon). Characters without a person's look (animals) are
    /// left out; nothing opens when none is left.
    /// </summary>
    public bool OpenEditor(EditKind kind, IReadOnlyList<CharacterId> characters, CharacterId surgeon = default, bool paid = false)
    {
        if (EditorData is null) return false;
        var people = characters.Where(id => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { Appearance: { Animal: null } look } && RecordOf(look) is not null).ToList();
        if (people.Count == 0) return false;
        editor = new EditorSession(kind, people, surgeon, paid);
        previews.Clear();
        faithfulScreens.Open("editor");
        ShowEditorCharacter();
        UpdateMenuPause();
        return true;
    }

    static Meitou.Data.Fcs.FcsRecord? RecordOf(CharacterAppearance look) => look.Loadout?.Appearance.Record ?? look.Body?.Record;

    CharacterDraft? Draft(CharacterId id)
    {
        if (editor is null || EditorData is not { } data) return null;
        if (editor.Drafts.TryGetValue(id, out var draft)) return draft;
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { Appearance: { } look } snapshot || RecordOf(look) is not { } record) return null;
        draft = new CharacterDraft(data.Db, data.InstallRoot, record, snapshot.Name);
        editor.Drafts[id] = draft;
        editor.StartNames[id] = snapshot.Name;
        return draft;
    }

    void ShowEditorCharacter()
    {
        if (editor is null || Draft(editor.Current) is not { } draft) return;
        // At creation a character of a race the editor doesn't offer (a start's `force race`) takes the first offered race (FUN_1405efd60).
        if (editor.Kind == EditKind.Creation && EditorData is { } data && EditorRaces(data.Db) is [{ } first, ..] groups && IndexOf(groups, draft.Race) < 0)
            draft.SetRace(first.Races[0], editor.Seed++);
        faithfulScreens.SetText("editor", "NameText", draft.Name);
        Select([editor.Current], false);
    }

    void CloseEditor()
    {
        faithfulScreens.Close("file");
        faithfulScreens.Close("editor");
        editor = null;
        previews.Clear();
        UpdateMenuPause();
    }

    /// <summary>
    /// The look to draw for a character while the editor has a draft of it (null: its own). A changed draft gets a new look (at most every 0.12 s while a slider is
    /// dragged); the last ready one stays on screen until the renderer has prepared the new one (<see cref="PreviewReady"/>), so the character never blinks out.
    /// </summary>
    public CharacterAppearance? EditorPreview(CharacterId id, CharacterAppearance? own)
    {
        if (!EditorOpen || !editor!.Drafts.TryGetValue(id, out var draft) || own?.Loadout is not { } loadout || PreviewSource is null) return null;
        bool clothes = editor.Clothes;
        if (draft.Version == 0 && clothes) { previews.Remove(id); return null; }
        var entry = previews.TryGetValue(id, out var found) ? found : new Preview(-1, true, own, null);
        double now = previewClock.Elapsed.TotalSeconds;
        if (entry.Pending is { } pending && (PreviewReady?.Invoke(pending) ?? true)) entry = entry with { Shown = pending, Pending = null, From = entry.Shown ?? own, Started = now };
        if ((entry.Version != draft.Version || entry.Clothes != clothes) && now - lastPreviewBuild >= 0.12)
        {
            lastPreviewBuild = now;
            if (EditorData is { } data && Meitou.Simulation.Saving.SavedKit.Wear(loadout, draft.Record, data.Db) is { } worn)
            {
                if (!clothes) { worn.Clothing.Clear(); worn.Backpack = null; }
                if (PreviewSource(own.Character?.StringId ?? "", worn) is { } look) entry = entry with { Pending = look };
            }
            entry = entry with { Version = draft.Version, Clothes = clothes };
        }
        previews[id] = entry;
        if (entry.Pending is { } waiting && !ReferenceEquals(waiting, warmedPending)) { warmedPending = waiting; PreviewFetch?.Invoke(waiting); }
        Anticipate(id, draft, loadout, own);
        return entry.Shown ?? own;
    }

    /// <summary>
    /// Prepares ahead the looks the next click can give, so their meshes are ready when it comes (the original loads its meshes at once): urgently RANDOMISE ALL's next roll
    /// (the rolls are seeded) and, at creation, the other gender and the next and previous race and subrace, again whenever the seed, race, gender or clothes change; then, on the
    /// ordinary queue, a look with each head, hair and beard of the race and gender, once for each. Looks whose meshes are all prepared are skipped by the renderer.
    /// </summary>
    void Anticipate(CharacterId id, CharacterDraft draft, Loadout loadout, CharacterAppearance own)
    {
        if (editor is not { } s || PreviewWarm is null || PreviewSource is null || EditorData is not { } data) return;
        void Warm(IEnumerable<Meitou.Data.Fcs.FcsRecord> records, bool urgent)
        {
            foreach (var record in records)
                if (Meitou.Simulation.Saving.SavedKit.Wear(loadout, record, data.Db) is { } worn)
                {
                    if (!s.Clothes) { worn.Clothing.Clear(); worn.Backpack = null; }
                    if (PreviewSource(own.Character?.StringId ?? "", worn) is { } look) PreviewWarm(look, urgent);
                }
        }
        if (anticipated != (id, s.Seed, draft.Race.StringId, draft.Female, s.Clothes))
        {
            anticipated = (id, s.Seed, draft.Race.StringId, draft.Female, s.Clothes);
            var next = new List<Meitou.Data.Fcs.FcsRecord> { draft.RandomisedLook(s.Seed) };
            if (s.Kind == EditKind.Creation)
            {
                if (!draft.Race.GetBool("single gender")) next.Add(draft.SwitchedLook(draft.Race, !draft.Female, s.Seed));
                var groups = EditorRaces(data.Db);
                int g = IndexOf(groups, draft.Race);
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
        if (catalogued != (id, draft.Race.StringId, draft.Female, s.Clothes))
        {
            catalogued = (id, draft.Race.StringId, draft.Female, s.Clothes);
            var catalogue = new List<Meitou.Data.Fcs.FcsRecord>();
            foreach (var kind in new[] { EditorControlKind.Head, EditorControlKind.HairStyle, EditorControlKind.Beard })
                for (int i = 0; i < draft.Options(kind).Count; i++)
                    if (draft.Options(kind)[i] is not null) catalogue.Add(draft.WithOption(kind, i));
            Warm(catalogue, urgent: false);
        }
    }

    /// <summary>
    /// The idle stance the editor shows a character in: its draft's <c>idle stance</c> (the original's preview plays the chosen stance; the world's idles are the
    /// simulation's), or null while the editor is closed or has no draft of it.
    /// </summary>
    public string? EditorIdle(CharacterId id) =>
        EditorOpen && editor!.Drafts.TryGetValue(id, out var draft) && draft.Record.Strings.GetValueOrDefault("idle stance") is { Length: > 0 } stance ? stance : null;

    /// <summary>The preview look being prepared for a character (the host lists it hidden, so the renderer builds it), or null.</summary>
    public CharacterAppearance? EditorPending(CharacterId id) => EditorOpen && previews.TryGetValue(id, out var entry) ? entry.Pending : null;

    /// <summary>The ImageBox texture name of the renderer's stage picture (<see cref="Meitou.Rendering.Characters.CharacterRenderer.StageTexture"/>).</summary>
    public const string StageTexture = "meitou:stage";
    Func<string, string?>? findAsset;
    string? groundFile;

    /// <summary>
    /// The editor's scene behind its panels (the original's compositor <c>Kenshi_Character_Editor</c>): <c>SandyGround.dds</c> over the whole window, stretched as the original's full-screen
    /// quad is, and the character's picture on it. First in the screen's controls, so the panels draw over both.
    /// </summary>
    List<UiControl> EditorBackdrop()
    {
        var backdrop = new List<UiControl>();
        if (uiPainter is not { } painter || EditorInWorld) return backdrop;
        groundFile ??= findAsset?.Invoke("SandyGround.dds") ?? "";
        var all = new UiRect(0, 0, Width, Height);
        // A sand colour first, so the window is covered before the ground texture is decoded.
        backdrop.Add(new("editor-base", "Widget", all, "", "", "", true, new Dictionary<string, string>(), new Vector4(0.42f, 0.40f, 0.34f, 1), Pickable: false));
        if (groundFile.Length > 0 && painter.IconReady(groundFile))
            backdrop.Add(new("editor-ground", "ImageBox", all, "", "", "", true, new Dictionary<string, string> { ["ImageTexture"] = Meitou.Rendering.Ui.UiPainter.IconTexture(groundFile) }, Vector4.Zero, Pickable: false));
        if (painter.HasTexture(StageTexture))
            backdrop.Add(new("editor-stage", "ImageBox", all, "", "", "", true, new Dictionary<string, string> { ["ImageTexture"] = StageTexture, ["ImageFlipV"] = "true" }, Vector4.Zero, Pickable: false));
        return backdrop;
    }

    /// <summary>The look the character's shown one has just replaced and how far the glide to the shown one is (0..1), or null once it has arrived (the renderer mixes the two).</summary>
    public (CharacterAppearance From, float Blend)? EditorBlend(CharacterId id)
    {
        if (!EditorOpen || !previews.TryGetValue(id, out var entry) || entry.From is not { } from) return null;
        double t = (previewClock.Elapsed.TotalSeconds - entry.Started) / BlendSeconds;
        return t >= 1 ? null : (from, (float)Math.Max(t, 0));
    }

    /// <summary>
    /// The editor's camera for the host: the character, the orbit and whether it is on the head. The orbit starts over when the tab changes between the body and the
    /// head (FACE and HAIR share one view) or the character changes, as the original's camera resets then.
    /// </summary>
    public (CharacterId Character, float Yaw, float Elevation, float Distance, bool Head)? EditorView()
    {
        if (!EditorOpen || editor is not { } s) return null;
        bool head = s.Tab != EditorTab.Body;
        if (s.Orbit.Head != head || s.OrbitCharacter != s.Current) { s.Orbit.Reset(head); s.OrbitCharacter = s.Current; s.Drag = null; }
        return (s.Current, s.Orbit.Yaw, s.Orbit.Elevation, s.Orbit.Distance, head);
    }

    /// <summary>Whether <paramref name="p"/> is on the editor's scene rather than on one of its panels or a window above it (the editor's screen is modal: what no widget of it covers counts as the scene).</summary>
    bool OverEditorScene(Vector2 p) => faithfulScreens.OpenScreens[^1] == "editor" && faithfulScreens.HitTest(p) is { Control.Name: "modal" };

    /// <summary>A mouse button pressed away from the editor's panels starts turning the camera; false when the press belongs to a panel.</summary>
    bool EditorPointerDown(Meitou.Engine.Input.MouseButton button, Vector2 p)
    {
        if (!EditorOpen || !UiVisible || editor is not { } s || !OverEditorScene(p)) return false;
        s.Drag = (button, p);
        return true;
    }

    bool EditorPointerMove(Vector2 p)
    {
        if (editor?.Drag is not { } drag) return false;
        editor.Orbit.Drag(p.X - drag.Last.X, p.Y - drag.Last.Y);
        editor.Drag = (drag.Button, p);
        return true;
    }

    void EditorPointerUp(Meitou.Engine.Input.MouseButton button)
    {
        if (editor?.Drag is { } drag && drag.Button == button) editor.Drag = null;
    }

    /// <summary>The wheel away from the panels zooms the camera.</summary>
    bool EditorWheel(Vector2 p, float steps)
    {
        if (!EditorOpen || editor is not { } s || !OverEditorScene(p)) return false;
        s.Orbit.Zoom(steps);
        return true;
    }

    void EditorAction(MyGuiEvent e)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        bool creation = s.Kind == EditKind.Creation;
        switch (e.Widget)
        {
            case "BodyTabButton": s.Tab = EditorTab.Body; return;
            case "FaceTabButton": s.Tab = EditorTab.Face; return;
            case "HairTabButton": s.Tab = EditorTab.Hair; return;
            case "NameText" when e.Action == "text": draft.Name = e.Text; return;
            case "RandomiseButton": draft.Randomise(s.Tab, s.Seed++); return;
            case "RandomiseAllButton": draft.Randomise(null, s.Seed++); return;
            case "ResetButton": draft.Reset(); return;
            case "ClothesButton": s.Clothes = !s.Clothes; return;
            case "CharLeftArrow" or "CharRightArrow":
                s.Index = (s.Index + (e.Widget == "CharLeftArrow" ? s.Characters.Count - 1 : 1)) % s.Characters.Count;
                ShowEditorCharacter();
                return;
            case "RaceLeftButton" or "RaceRightButton" or "SubRaceLeftButton" or "SubRaceRightButton" when creation:
            {
                var groups = EditorRaces(data.Db);
                if (groups.Count == 0) return;
                int g = Math.Max(0, IndexOf(groups, draft.Race));
                int r = Math.Max(0, groups[g].Races.ToList().FindIndex(x => x.StringId == draft.Race.StringId));
                int step = e.Widget.Contains("Left", StringComparison.Ordinal) ? -1 : 1;
                if (e.Widget.StartsWith("Race", StringComparison.Ordinal)) { g = (g + step + groups.Count) % groups.Count; r = 0; }
                else r = (r + step + groups[g].Races.Count) % groups[g].Races.Count;
                draft.SetRace(groups[g].Races[r], s.Seed++);
                return;
            }
            case "GenderLeftButton" or "GenderRightButton" when creation:
                draft.SetFemale(!draft.Female, s.Seed++);
                return;
            case "ConfirmButton":
                ConfirmEditor();
                return;
            case "ExportButton":
                ExportLook(draft, data.InstallRoot);
                return;
            case "ImportButton":
                ShowImportFolder("./" + ExportFolder.TrimEnd('/'), data.InstallRoot);
                faithfulScreens.Open("file");
                return;
        }
        // A row's bar moved (its arrows, track or thumb) or a picker's arrow was pressed; the row's widget names the control.
        if (editorRows.FirstOrDefault(r => r.Owner == e.Widget) is { Owner: not null } row && draft.Controls(s.Tab).ElementAtOrDefault(row.Control) is { } control)
        {
            if (e.Action == "scroll" && !control.IsPicker) draft.Set(control, control.Min + (int)MathF.Round(e.Value));
            else if (e.Action == "step" && control.IsPicker) draft.Step(control.Kind, (int)e.Value);
        }
    }


    static int IndexOf(IReadOnlyList<EditorRaceGroup> groups, GameRecord race)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Races.Any(r => r.StringId == race.StringId)) return i;
        return -1;
    }

    /// <summary>The races this session offers: at creation the playable ones, limited to the start's <c>force race</c>; a surgeon offers none (race stays).</summary>
    IReadOnlyList<EditorRaceGroup> EditorRaces(GameDatabase db) =>
        editor?.Kind == EditKind.Creation ? CharacterDraft.PlayableRaces(db, [.. session.World.Player.CreationRaces]) : [];

    /// <summary>
    /// CONFIRM. Like the original (<c>FUN_1405e9f40</c>), a squad with characters never shown asks first ("There are other characters whose appearance haven't
    /// been changed..."). Every changed character is sent to the world (a surgeon's patient always: the surgery is paid on confirming); at creation the creation then ends.
    /// </summary>
    void ConfirmEditor(bool asked = false)
    {
        if (editor is not { } s) return;
        if (!asked && s.Drafts.Count < s.Characters.Count)
        {
            Confirm("CONFIRM", "There are other characters whose appearance haven't been changed. Are you sure you want to continue?", () => ConfirmEditor(true));
            return;
        }
        long tick = session.World.Tick;
        foreach (var (id, draft) in s.Drafts)
            if (s.Kind == EditKind.Surgery || draft.Version > 0 || draft.Name != s.StartNames[id])
                Say(new EditCharacter(id, CharacterDraft.Copy(draft.Record), draft.Name, s.Kind) { Tick = tick, Surgeon = s.Surgeon, Paid = s.Paid });
        if (s.Kind == EditKind.Creation) Say(new FinishCreation { Tick = tick });
        CloseEditor();
    }

    /// <summary>For the screenshot showcase: the open editor's character gets a new random look and name, and CONFIRM is pressed (the creation path end to end).</summary>
    internal void ShowcaseEdit()
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft) return;
        draft.Randomise(null, 7);
        draft.Name = "Edited " + draft.Name;
        ConfirmEditor(asked: true);
    }
    /// <summary>The body files IMPORT and EXPORT use, under the install (<c>FUN_141539ad0</c>), and their extension.</summary>
    const string ExportFolder = "data/character/bodies/export/", BodyExtension = ".bod2";
    /// <summary>The folder the import dialog shows, as the original keeps it (install-relative, no trailing separator), its lines and the chosen one.</summary>
    string importFolder = "./" + ExportFolder.TrimEnd('/');
    IReadOnlyList<UiListItem> importEntries = [];
    string? importChoice;
    /// <summary>A line's id for a folder (the folder's path), against a file's (its path).</summary>
    const string FolderEntry = "folder:";

    /// <summary>EXPORT (<c>FUN_1405ea410</c>): the look goes to the export folder under the name typed in NameText, replacing a file of that name.</summary>
    void ExportLook(CharacterDraft draft, string installRoot)
    {
        string name = draft.Name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Notice($"Can't export: '{name}' is not a valid file name.", true); return; }
        string relative = ExportFolder + name + BodyExtension;
        try
        {
            Directory.CreateDirectory(Path.Combine(installRoot, ExportFolder));
            Meitou.Data.Fcs.FcsWriter.WriteFile(draft.Export(), Path.Combine(installRoot, relative));
            Notice(Loc.Tr("Character '{1}' exported to file {2}", name, "./" + relative));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice($"Can't export '{name}': {e.Message}", true); }
    }

    /// <summary>
    /// Shows <paramref name="folder"/> in the import dialog (<c>FUN_14019fd70</c>): "[..]" and its folders as "[name]", then its body files by their file names, each group in
    /// ordinal order; the line ids are the paths (folder + "/" + name). Nothing is chosen.
    /// </summary>
    void ShowImportFolder(string folder, string installRoot)
    {
        importFolder = folder;
        importChoice = null;
        string full = Path.Combine(installRoot, folder);
        var entries = new List<UiListItem>();
        try
        {
            foreach (string name in new[] { ".." }.Concat(Directory.EnumerateDirectories(full).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)))
                entries.Add(new(FolderEntry + folder + "/" + name, "[" + name + "]"));
            foreach (string name in Directory.EnumerateFiles(full, "*" + BodyExtension).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
                entries.Add(new(folder + "/" + name, name));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        importEntries = entries;
    }

    /// <summary>
    /// IMPORT (<c>FUN_1405f4280</c>): the chosen file's look replaces the draft's, race and sex included. A file without an appearance record fails with the original's
    /// message. Meitou: a file of a race the editor doesn't offer, or (at a surgeon, where race and sex are fixed) of another race or sex, is refused.
    /// </summary>
    void ImportLook(string path)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        string file = Path.GetFileNameWithoutExtension(path);
        Meitou.Data.Fcs.FcsRecord appearance;
        try { appearance = AppearanceFile.Read(Path.Combine(data.InstallRoot, path)).Record; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Meitou.Data.Fcs.FcsFormatException or InvalidDataException)
        { Notice(Loc.Tr("Error loading appearance file '{1}'. The file is in an old format or invalid.", file), true); return; }
        bool female = appearance.Bools.TryGetValue("sex female", out var f) && f;
        bool allowed = CharacterDraft.RaceOf(data.Db, appearance) is { } race && (s.Kind == EditKind.Creation
            ? IndexOf(EditorRaces(data.Db), race) >= 0
            : race.StringId == draft.Race.StringId && (female == draft.Female || race.GetBool("single gender")));
        if (!allowed || !draft.Import(appearance)) { Notice($"Can't import '{file}': this character can't take that race or sex.", true); return; }
        faithfulScreens.Close("file");
    }

    /// <summary>The import dialog's buttons and list: a folder line opens on a double click or IMPORT, a file line imports; the up arrow cuts the folder at its last separator.</summary>
    void FileDialogAction(MyGuiEvent e)
    {
        if (EditorData is not { } data) return;
        if (e.Widget == "ButtonCancel") faithfulScreens.Close("file");
        else if (e.Widget == "ButtonUp" && importFolder.LastIndexOfAny(['/', '\\']) is var cut and > 0) ShowImportFolder(importFolder[..cut], data.InstallRoot);
        else if (e.Widget == "ListFiles" && e.Action == "select") importChoice = e.Text;
        else if (e.Widget == "ListFiles" && e.Action == "accept" || e.Widget == "ButtonOpenSave")
        {
            if (importChoice is not { } choice) return;
            if (choice.StartsWith(FolderEntry, StringComparison.Ordinal)) ShowImportFolder(choice[FolderEntry.Length..], data.InstallRoot);
            else ImportLook(choice);
        }
    }

    /// <summary>
    /// The import dialog (<c>Kenshi_OpenSaveFileDialog</c>, titled "Import character", <c>FUN_1405f4dd0</c>, <c>FUN_1401a0b10</c>): the folder in the combo box, the up arrow, the list,
    /// IMPORT and CANCEL; no file name box (it opens files).
    /// </summary>
    void BindFileDialog(UiBindings b)
    {
        b.TemplateScale = Height / 1080f;
        b.Text["Root"] = "Import character";
        b.Text["ButtonOpenSave"] = Loc.Tr("Import").ToUpperInvariant();
        b.Text["ButtonCancel"] = Loc.Tr("Cancel").ToUpperInvariant();
        foreach (string name in new[] { "ButtonCancel", "ButtonOpenSave", "ButtonUp" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
        if (importChoice is null) b.Disabled.Add("ButtonOpenSave");
        b.Hidden.Add("EditFileName");
        b.Text["CurrentFolder"] = importFolder;
        b.Disabled.Add("CurrentFolder");
        b.Lists["ListFiles"] = importEntries;
        b.SelectedItems["ListFiles"] = importEntries.ToList().FindIndex(i => i.Id == importChoice);
        b.Properties["ButtonOpenSave"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
    }

    void BindEditor(UiBindings b)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        bool creation = s.Kind == EditKind.Creation;
        foreach (string name in new[] { "BodyTabButton", "FaceTabButton", "HairTabButton", "RandomiseButton", "RandomiseAllButton", "ResetButton", "ClothesButton", "ConfirmButton" })
        { b.Disabled.Remove(name); b.Actions[name] = name; }
        // Race, subrace and gender arrows: only at creation, and only where there is a choice (FUN_1405ee0c0); hidden otherwise.
        var groups = EditorRaces(data.Db);
        int g = IndexOf(groups, draft.Race);
        void Arrows(bool live, params string[] names)
        {
            foreach (string name in names)
                if (live) { b.Disabled.Remove(name); b.Actions[name] = name; }
                else b.Hidden.Add(name);
        }
        Arrows(creation && groups.Count > 1, "RaceLeftButton", "RaceRightButton");
        Arrows(creation && g >= 0 && groups[g].Races.Count > 1, "SubRaceLeftButton", "SubRaceRightButton");
        Arrows(creation && !draft.Race.GetBool("single gender"), "GenderLeftButton", "GenderRightButton");
        // CHANGE CHARACTER stays with one character, its arrows and "1/1" disabled (FUN_1405f1a40; the original's screen shows them).
        if (s.Characters.Count > 1)
            foreach (string name in new[] { "CharLeftArrow", "CharRightArrow" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
        else b.Disabled.Add("CharIndexText");
        foreach (string name in new[] { "ImportButton", "ExportButton" }) { b.Disabled.Remove(name); b.Actions[name] = name; }
        // The captions the code sets (FUN_1405f1a40): the catalog's message, upper case; the layout's own captions (RESET ALL, SUBRACE...) never show.
        foreach (var (name, key) in EditorCaptions) b.Text[name] = Loc.Tr(key).ToUpperInvariant();
        string tabButton = s.Tab switch { EditorTab.Face => "FaceTabButton", EditorTab.Hair => "HairTabButton", _ => "BodyTabButton" };
        b.Properties[tabButton] = new Dictionary<string, string> { ["Checked"] = "true" };
        b.Properties["ClothesButton"] = new Dictionary<string, string> { ["Checked"] = s.Clothes ? "true" : "false", ["StateSelected"] = s.Clothes ? "true" : "false" };
        var everyRace = CharacterDraft.PlayableRaces(data.Db);
        int shown = IndexOf(everyRace, draft.Race);
        b.Text["RaceText"] = shown >= 0 ? everyRace[shown].Group.Name : draft.Race.Name;
        b.Text["SubRaceText"] = draft.Race.Name;
        // "-" without a choice of gender (FUN_1405ee0c0: the race's gender list has fewer than two entries).
        b.Text["GenderText"] = draft.Race.GetBool("single gender") ? "-" : draft.Female ? "Female" : "Male";
        b.Text["CharIndexText"] = $"{s.Index + 1}/{s.Characters.Count}";
        b.Text["DescriptionPanelText"] = draft.Race.GetString("description");
        b.Properties["DescriptionPanelText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(uiResources, "Kenshi_WordWrap") };
        b.Properties["NameText"] = new Dictionary<string, string> { ["DefaultAction"] = "false" };
        b.Properties["ConfirmButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        BindEditorRows(b, draft, s.Tab);
    }

    /// <summary>The widgets whose captions the original sets from the catalog (<c>FUN_1405f1a40</c>), upper-cased, and their messages.</summary>
    static readonly (string Widget, string Key)[] EditorCaptions =
    [
        ("RandomiseButton", "Randomise"), ("ResetButton", "Reset"), ("RandomiseAllButton", "Randomise All"), ("BodyTabButton", "Body"), ("FaceTabButton", "Face"),
        ("HairTabButton", "Hair"), ("ImportButton", "Import"), ("ExportButton", "Export"), ("ClothesButton", "Clothes"), ("ConfirmButton", "Confirm"), ("lbRace", "Race"),
        ("lbSubRace", "Subrace"), ("lbGender", "Gender"), ("lbChangeCharacter", "Change Character"), ("lbRaceDescription", "Race Description"), ("lbRaceStats", "Race Stats"),
    ];

    /// <summary>The editor's layout as read; the shown tab's rows are added to a copy of it (<see cref="EnsureEditorRows"/>).</summary>
    MyGuiLayout? editorLayout;
    /// <summary>The kinds of the rows the editor's layout has now, so it is composed again only when they change (another tab, race or gender).</summary>
    string editorRowsShape = "";
    /// <summary>The shown tab's rows: the widget whose events and texts are the row's (the template's owner) and the control it edits.</summary>
    readonly List<(string Owner, int Control)> editorRows = [];
    /// <summary>The row widgets in the scroll view, top to bottom, with their template's height (layout pixels).</summary>
    readonly List<(string Name, float Height)> editorRowWidgets = [];
    const string EditorRowsView = "EditorRows";

    /// <summary>
    /// The rows of a tab, as the original makes them (<c>FUN_1405f1a40</c>, <c>FUN_1405ebd50</c>; docs/characters.md "Character editor"): a <c>Kenshi_ScrollViewEmptyLight</c> scroll view
    /// in <c>TabsPanelContent</c> (vertical bar only), and in it one widget per control, stacked at its template's height: a picker is a <c>Kenshi_ListSlider</c>, a slider a
    /// <c>Kenshi_Slider</c> (name box, a <c>Kenshi_ScrollBar</c> with its arrows, value box), the three hair colour sliders one <c>Kenshi_MultiSlider</c>.
    /// </summary>
    void EnsureEditorRows(CharacterDraft draft, EditorTab tab)
    {
        if (editorLayout is null) return;
        var controls = draft.Controls(tab);
        string shape = tab + ":" + string.Join(",", controls.Select(c => c.Kind));
        if (shape == editorRowsShape) return;
        editorRowsShape = shape;
        editorRows.Clear();
        editorRowWidgets.Clear();
        var rows = new List<MyGuiWidget>();
        var parts = new List<(string Row, string? Part, int Control)>();
        for (int i = 0; i < controls.Count; i++)
        {
            string name = "EditorRow" + i.ToString(CultureInfo.InvariantCulture);
            string template = controls[i].Kind == EditorControlKind.HairHue ? "Kenshi_MultiSlider" : controls[i].IsPicker ? "Kenshi_ListSlider" : "Kenshi_Slider";
            float height = uiResources.Templates.TryGetValue(template, out var resource) ? MyGuiReader.ReadResourceLayout(resource).Roots[0].Position.Height : 55;
            rows.Add(new("Widget", name, template, "", "Left Top", new(0, 0, 455, height), false, new Dictionary<string, string>(), new Dictionary<string, string>(), []));
            editorRowWidgets.Add((name, height));
            if (template == "Kenshi_MultiSlider")
            {
                for (int k = 0; k < 3 && i + k < controls.Count; k++) parts.Add((name, "Slider" + (k + 1).ToString(CultureInfo.InvariantCulture), i + k));
                i += 2;
            }
            else parts.Add((name, null, i));
        }
        var view = new MyGuiWidget("ScrollView", EditorRowsView, "Kenshi_ScrollViewEmptyLight", "", "Stretch", new(0, 0, 1, 1), true,
            new Dictionary<string, string> { ["VisibleHScroll"] = "false", ["VisibleVScroll"] = "true", ["CanvasAlign"] = "Left Top" }, new Dictionary<string, string>(), rows);
        static MyGuiWidget Fill(MyGuiWidget w, MyGuiWidget view) =>
            w.Name == "TabsPanelContent" ? w with { Children = [view] } : w with { Children = [.. w.Children.Select(c => Fill(c, view))] };
        faithfulScreens.Replace("editor", editorLayout with { Roots = [.. editorLayout.Roots.Select(r => Fill(r, view))] });
        faithfulScreens.SetScroll("editor", EditorRowsView, 0);
        // A hair colour slider's own template widget (Slider1..3 of the multi-slider) owns its bar and texts.
        var widgets = faithfulScreens.Layout("editor").Widgets.ToList();
        foreach (var (row, part, control) in parts)
            editorRows.Add((part is null ? row : widgets.FirstOrDefault(w => w.Properties.GetValueOrDefault("TemplateOwner") == row && w.Properties.GetValueOrDefault("TemplatePart") == part)?.Name ?? row, control));
    }

    /// <summary>
    /// The rows' texts and bars: the name box the control's message (the face and hair lists "Face", "Hair", "Beard"; the hair colour sliders "Colour", "Saturation",
    /// "Brightness" under "Hair colour"), a slider's bar its range and value and the value box the number, a list its choice and "n/count".
    /// The canvas is as wide as the view (less the bar when the rows overflow it), the rows as tall as their templates.
    /// </summary>
    void BindEditorRows(UiBindings b, CharacterDraft draft, EditorTab tab)
    {
        EnsureEditorRows(draft, tab);
        float scale = Height / 1080f;
        b.TemplateScale = scale;
        var layout = faithfulScreens.Layout("editor");
        var controls = draft.Controls(tab);
        foreach (var (owner, index) in editorRows)
        {
            if (controls.ElementAtOrDefault(index) is not { } c) continue;
            if (c.IsPicker)
            {
                var options = draft.Options(c.Kind);
                b.Text[owner + ".ElementText"] = c.Kind switch { EditorControlKind.Head => "Face", EditorControlKind.Beard => "Beard", _ => "Hair" };
                b.Text[owner + ".ElementValueText"] = draft.OptionName(c.Kind);
                b.Text[owner + ".NumberText"] = $"{draft.OptionIndex(c.Kind) + 1}/{options.Count}";
                continue;
            }
            int value = draft.Value(c);
            b.Text[owner + ".ElementText"] = c.Kind switch
            {
                EditorControlKind.HairHue => "Colour", EditorControlKind.HairSaturation => "Saturation", EditorControlKind.HairBrightness => "Brightness", _ => c.Label,
            };
            b.Text[owner + ".NumberText"] = value.ToString(CultureInfo.InvariantCulture);
            if (layout.Widgets.FirstOrDefault(w => w.Type == "ScrollBar" && w.Properties.GetValueOrDefault("TemplateOwner") == owner && w.Properties.GetValueOrDefault("TemplatePart") == "Slider") is { } bar)
            {
                b.Properties[bar.Name] = new Dictionary<string, string> { ["Range"] = (c.Max - c.Min + 1).ToString(CultureInfo.InvariantCulture) };
                faithfulScreens.SetScroll("editor", bar.Name, value - c.Min);
            }
            if (c.Kind == EditorControlKind.HairHue && editorRowWidgets.FirstOrDefault(r => owner.StartsWith(r.Name + "@", StringComparison.Ordinal)).Name is { } multi)
                b.Text[multi + ".Name"] = "Hair colour";
        }
        // Rows stacked at their templates' heights on a canvas as wide as the view: its client, less the vertical bar when the rows are taller than it.
        var content = RealBounds(layout, "TabsPanelContent");
        float total = editorRowWidgets.Sum(r => r.Height) * scale;
        float barWidth = (layout.Widgets.FirstOrDefault(w => w.Properties.GetValueOrDefault("TemplateOwner") == EditorRowsView && w.Properties.GetValueOrDefault("TemplatePart") == "VScroll")?.Position.Width ?? 32) * scale;
        float width = total > content.Height ? content.Width - barWidth : content.Width;
        if (total <= 0) return;
        b.Canvas[EditorRowsView] = (width, total);
        float y = 0;
        foreach (var (name, height) in editorRowWidgets)
        {
            if (layout.Find(name) is { } row) b.RelativePositions[row] = new(0, y / total, 1, height * scale / total);
            y += height * scale;
        }
    }

    /// <summary>A widget's bounds on the screen from its layout's relative positions (the editor's panels are all <c>position_real</c>).</summary>
    UiRect RealBounds(MyGuiLayout layout, string name)
    {
        UiRect? Walk(MyGuiWidget w, UiRect parent)
        {
            var bounds = w.RealPosition ? w.Position.RelativeTo(parent, true) : w.Position.RelativeTo(parent, false);
            if (w.Name == name) return bounds;
            foreach (var child in w.Children) if (Walk(child, bounds) is { } found) return found;
            return null;
        }
        foreach (var root in layout.Roots) if (Walk(root, new(0, 0, Width, Height)) is { } found) return found;
        return default;
    }

    void AddEditorContent(List<UiControl> controls)
    {
        if (editor is not { } s || Draft(s.Current) is not { } draft) return;
        if (controls.FirstOrDefault(c => c.Name == "StatsPanelText") is { } stats) AddRaceStats(controls, draft.Race, stats);
    }

    /// <summary>The scroll state's key of the RACE STATS list: the editor's screen (the scrollbar's arrows, track and wheel act on the screen they are in).</summary>
    const string EditorStatsScroll = "editor";
    string? editorStatsRace;

    /// <summary>
    /// The RACE STATS panel (<see cref="RaceStatLines"/>): a DatapanelGUI in <c>StatsPanelText</c> (docs/game/ui-screens.md "Text panels"), a row of 19.2 pixels at 1080
    /// lines per line (Observed: 25.55 at 1440 in the base game), the label from 3 % of the canvas width and the value right-aligned to 96 %; lines past the panel's height
    /// scroll, with the vertical bar shown only then (the panel's ScrollView, <c>FUN_1406fd4e0</c>).
    /// </summary>
    void AddRaceStats(List<UiControl> controls, GameRecord race, UiControl host)
    {
        var area = host.Bounds;
        var lines = new List<(string Label, string Value, bool? Good)>();
        // The first line is the character's hit points: its first body part's maximum (FUN_1405ee0c0 reads the medical state's first part, Observed), as an integer. While the
        // draft shows another race than the character has, that race's new medical state's (the character takes it on CONFIRM).
        float? hitPoints = null;
        if (editor is { } s && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == s.Current) is { } shown)
            hitPoints = shown.Appearance?.Race.StringId == race.StringId && shown.HealthParts is [var first, ..] ? first.Maximum : RaceHitPoints(race);
        if (hitPoints is { } hp) lines.Add((Loc.Tr("Hit Points:"), ((int)(hp + 0.01f)).ToString(CultureInfo.InvariantCulture), null));
        lines.AddRange(RaceStatLines(race).Select(l => (l.Label, l.Value, (bool?)l.Good)));
        float scale = Height / 1080f, row = 19.2f * scale;
        // A new race's list starts at its top (the original fills the panel anew).
        if (editorStatsRace != race.StringId) { inventoryScroll.Remove(EditorStatsScroll); editorStatsRace = race.StringId; }
        float maximum = Math.Max(0, lines.Count * row - area.Height);
        if (maximum < 0.01f) maximum = 0;
        float scroll = Math.Clamp(inventoryScroll.GetValueOrDefault(EditorStatsScroll), 0, maximum);
        // The panel's ScrollView (Kenshi_ScrollViewEmpty: its bars 20 pixels): the lines are laid out on a canvas the bar's width narrower than the panel; with the bar
        // (overflow) the canvas fills the client, without it the canvas sits 13 pixels in from the panel's left and top (Observed at 1440 lines, a Hive Prince's and a
        // skeleton's stats against the base game's: the same 355-pixel rows, 13 pixels right and down without the bar; the text 6 pixels above the panel's top).
        const float Bar = 20, Inset = 13, Lift = 6;
        float bar = maximum > 0 ? Bar : 0, width = area.Width - Bar, inset = maximum > 0 ? 0 : Inset;
        var clip = area.Intersect(host.Clip ?? area);
        for (int i = 0; i < lines.Count; i++)
        {
            var (label, value, good) = lines[i];
            var bounds = new UiRect(area.X + inset + width * 0.03f, area.Y + inset - Lift + i * row - scroll, width * 0.93f, row);
            controls.Add(new($"editor-stat-{i}", "TextBox", bounds, label, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextColour"] = MainColour, ["TextAlign"] = "Left Top" },
                Vector4.Zero, Pickable: false, Clip: clip));
            controls.Add(new($"editor-stat-value-{i}", "TextBox", bounds, value, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Right Top",
                    ["TextColour"] = good is null ? MainColour : good.Value ? GoodColour : BadBrightColour }, Vector4.Zero, Pickable: false, Clip: clip));
        }
        if (maximum <= 0) { inventoryViews.Remove(EditorStatsScroll); inventoryTracks.Remove(EditorStatsScroll); return; }
        inventoryScroll[EditorStatsScroll] = scroll;
        inventoryViews[EditorStatsScroll] = (area, maximum);
        AddScrollBar(EditorStatsScroll, controls, new(area.X + width, area.Y, bar, area.Height), area.Height, lines.Count * row, scroll, maximum, host.Clip);
    }

    /// <summary>
    /// A stat's name as the editor's race stats give it (<c>FUN_140888440</c>): its own table, not the stats window's (Medic is "First Aid", Labouring, Weapon Smithing);
    /// the vet and hive medic are "Vet", mass combat, survival and polearms have no name.
    /// </summary>
    static string EditorStatName(StatsEnumerated stat) => Loc.Tr(stat switch
    {
        StatsEnumerated.Strength => "Strength", StatsEnumerated.MeleeAttack => "Attack", StatsEnumerated.Labouring => "Labouring", StatsEnumerated.Science => "Science",
        StatsEnumerated.Engineering => "Engineering", StatsEnumerated.Robotics => "Robotics", StatsEnumerated.SmithingWeapon => "Weapon Smithing",
        StatsEnumerated.SmithingArmour => "Armour Smithing", StatsEnumerated.Medic => "First Aid", StatsEnumerated.Thieving => "Thievery", StatsEnumerated.Turrets => "Turrets",
        StatsEnumerated.Farming => "Farming", StatsEnumerated.Cooking => "Cooking", StatsEnumerated.HiveMedic or StatsEnumerated.Vet => "Vet", StatsEnumerated.Stealth => "Stealth",
        StatsEnumerated.Athletics => "Athletics", StatsEnumerated.Dexterity => "Dexterity", StatsEnumerated.MeleeDefence => "Defence", StatsEnumerated.Weapons => "Weaponry",
        StatsEnumerated.Toughness => "Toughness", StatsEnumerated.Assassination => "Assassination", StatsEnumerated.Swimming => "Swimming", StatsEnumerated.Perception => "Perception",
        StatsEnumerated.Katanas => "Katanas", StatsEnumerated.Sabres => "Sabres", StatsEnumerated.Hackers => "Hackers", StatsEnumerated.HeavyWeapons => "Heavy weapons",
        StatsEnumerated.Blunt => "Blunt", StatsEnumerated.MartialArts => "Martial arts", StatsEnumerated.Dodge => "Dodging", StatsEnumerated.Crossbows => "Crossbows",
        StatsEnumerated.FriendlyFire => "Precision Shooting", StatsEnumerated.Lockpicking => "Lockpicking", StatsEnumerated.SmithingBow => "Crossbow Smithing", _ => "",
    });

    readonly Dictionary<string, IReadOnlyList<(string, string, bool)>> raceStatCache = [];
    readonly Dictionary<string, float?> raceHitPointCache = [];

    /// <summary>A healthy new person of <paramref name="race"/>: its first medical part's maximum (<see cref="Meitou.Simulation.Bodies.MedicalState.Create"/>); null without anatomy.</summary>
    float? RaceHitPoints(GameRecord race)
    {
        if (raceHitPointCache.TryGetValue(race.StringId, out var cached)) return cached;
        if (EditorData is not { } data) return null;
        var parts = Meitou.Simulation.Bodies.MedicalState.Create(Meitou.Data.Gameplay.Bodies.RaceData.From(race, data.Db), 1).Parts;
        return raceHitPointCache[race.StringId] = parts.Count > 0 ? parts[0].MaxHp : null;
    }

    IReadOnlyList<(string Label, string Value, bool Good)> RaceStatLines(GameRecord race)
    {
        if (raceStatCache.TryGetValue(race.StringId, out var cached)) return cached;
        var lines = new List<(string, string, bool)>();
        if (EditorData is { } data)
        {
            // As the original's "Character_Editor_Race_Stats" table (FUN_1405ee0c0): "No bleeding" below a bleed rate of 0.1, then the stat multipliers above 1, then those
            // below 1 (RACE stats good / bad, strength, dexterity: RaceData.StatMultipliers), each as the stat's name and (m - 1) x 100 %, in the order the original's
            // hash map iterates (RaceData.StatMapOrder).
            var r = Meitou.Data.Gameplay.Bodies.RaceData.From(race, data.Db);
            if (race.GetFloat("bleed rate", 1) < 0.1f) lines.Add((Loc.Tr("No bleeding"), "", true));
            foreach (bool good in new[] { true, false })
                foreach (var stat in Meitou.Data.Gameplay.Bodies.RaceData.StatMapOrder)
                {
                    int i = (int)stat;
                    if (i < 1 || i >= r.StatMultipliers.Length) continue;
                    float m = r.StatMultipliers[i];
                    if (Math.Abs(m - 1) < 0.001f || m <= 0 || m > 1 != good) continue;
                    lines.Add((EditorStatName((Meitou.Data.Gameplay.Bodies.StatsEnumerated)i), ((m - 1) * 100).ToString("+0;-0", CultureInfo.InvariantCulture) + "%", good));
                }
        }
        return raceStatCache[race.StringId] = lines;
    }
}
