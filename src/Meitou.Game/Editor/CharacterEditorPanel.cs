using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Editing;

namespace Meitou.Game.Editor;

/// <summary>
/// The character editor (<c>CharacterEditWindow</c>, layout <c>Kenshi_CharacterEditor</c>; docs/characters.md "Character editor"): the start's
/// characters after a new game, or one character at a plastic surgeon. The window edits <see cref="CharacterDraft"/>s, shows them on the characters
/// in the world (<see cref="EditorPreview"/>), and on CONFIRM sends each one as an <see cref="EditCharacter"/> command; nothing in the world changes
/// before that.
/// </summary>
sealed class CharacterEditorPanel(PlayerInterface player)
{
    #region Editor session

    /// <summary>
    /// One opening of the editor: what it edits (a new game's characters or a surgeon's patient), the characters with their drafts, and the camera.
    /// </summary>
    internal sealed class EditorSession(EditKind kind, IReadOnlyList<CharacterId> characters, IReadOnlySet<CharacterId> animals,
        CharacterId surgeon, bool paid = false)
    {
        /// <summary>
        /// A surgeon's dialogue took the money already (<see cref="EditCharacter.Paid"/>).
        /// </summary>
        public bool Paid { get; } = paid;
        /// <summary>
        /// Whether the editor makes the start's characters or a plastic surgeon edits one.
        /// </summary>
        public EditKind Kind { get; } = kind;
        /// <summary>
        /// The characters listed, in the order the arrows step through.
        /// </summary>
        public IReadOnlyList<CharacterId> Characters { get; } = characters;
        /// <summary>
        /// The plastic surgeon (none at creation).
        /// </summary>
        public CharacterId Surgeon { get; } = surgeon;
        /// <summary>
        /// The index of the character shown in <see cref="Characters"/>.
        /// </summary>
        public int Index;
        /// <summary>
        /// The tab shown (body, face, hair).
        /// </summary>
        public EditorTab Tab = EditorTab.Body;
        /// <summary>
        /// Whether the characters are shown with their clothes on.
        /// </summary>
        public bool Clothes = true;
        /// <summary>
        /// The drafts made so far, by character.
        /// </summary>
        public Dictionary<CharacterId, CharacterDraft> Drafts { get; } = [];
        /// <summary>
        /// Each drafted character's name when its draft was made, to tell a renamed one.
        /// </summary>
        public Dictionary<CharacterId, string> StartNames { get; } = [];
        /// <summary>
        /// The animals among <see cref="Characters"/>: listed and shown, but only their name can be changed (<c>FUN_1405efd60</c>); no draft is made
        /// of them.
        /// </summary>
        public IReadOnlySet<CharacterId> Animals { get; } = animals;
        /// <summary>
        /// The animals' names as typed in NameText.
        /// </summary>
        public Dictionary<CharacterId, string> AnimalNames { get; } = [];
        /// <summary>
        /// The characters shown so far: CONFIRM asks first when some never were.
        /// </summary>
        public HashSet<CharacterId> Shown { get; } = [];
        /// <summary>
        /// The next seed of a random roll (randomise, a race change); counts up from the tick count at opening.
        /// </summary>
        public int Seed = Environment.TickCount;
        /// <summary>
        /// The character shown.
        /// </summary>
        public CharacterId Current => Characters[Index];
        /// <summary>
        /// The camera's orbit round the shown character.
        /// </summary>
        public EditorOrbit Orbit { get; } = new();
        /// <summary>
        /// The character the camera's orbit was last set for.
        /// </summary>
        public CharacterId OrbitCharacter;
        /// <summary>
        /// The mouse button dragging the orbit and where the pointer was last; null while none drags.
        /// </summary>
        public (Host.Input.MouseButton Button, Vector2 Last)? Drag;
    }

    /// <summary>
    /// The editor's session; null while it is closed.
    /// </summary>
    internal EditorSession? Editor;
    /// <summary>
    /// The game data and the install root the editor reads races and editor limits from (the host sets them).
    /// </summary>
    public (GameDatabase Db, string InstallRoot)? EditorData { get; set; }

    /// <summary>
    /// The looks the editor shows on the characters in the world while it has drafts of them.
    /// </summary>
    internal EditorPreviews Previews => field ??= new(this);

    /// <summary>
    /// IMPORT and EXPORT, and the import dialog.
    /// </summary>
    internal EditorBodyFiles Files => field ??= new(player, this);

    /// <summary>
    /// The RACE STATS panel.
    /// </summary>
    internal EditorRaceStats RaceStats => field ??= new(player, this);

    #endregion

    #region Editor control

    /// <summary>
    /// Whether the editor's window is open with a session.
    /// </summary>
    public bool EditorOpen => Editor is not null && player.FaithfulScreens.IsOpen("editor");
    /// <summary>
    /// The character the editor shows and its tab, for the host's camera; null while the editor is closed.
    /// </summary>
    public (CharacterId Character, EditorTab Tab)? EditorFocus => EditorOpen ? (Editor!.Current, Editor.Tab) : null;
    /// <summary>
    /// Every character the open editor edits, the shown one included; empty while it is closed.
    /// </summary>
    public IReadOnlyList<CharacterId> EditorCharacters => EditorOpen ? Editor!.Characters : [];
    /// <summary>
    /// Whether the open editor lists <paramref name="id"/> as an animal (its page offers the name only).
    /// </summary>
    public bool IsEditorAnimal(CharacterId id) => EditorOpen && Editor!.Animals.Contains(id);
    /// <summary>
    /// The host draws the editor's scene in the world (the Meitou bar, <see cref="EditorBar"/>): the backdrop is left out.
    /// </summary>
    public bool EditorInWorld { get; set; }

    /// <summary>
    /// Opens the editor for <paramref name="characters"/> (the start's squad at creation; one character at a surgeon). Animals are listed too, as the
    /// original's window takes every character it is given (<c>FUN_1405f4dd0</c>): the Guy with a Dog start's dog is its second character. Characters
    /// without a look are left out; nothing opens when none is left.
    /// </summary>
    public bool OpenEditor(EditKind kind, IReadOnlyList<CharacterId> characters, CharacterId surgeon = default, bool paid = false)
    {
        if (EditorData is null) return false;
        var listed = new List<CharacterId>();
        var animals = new HashSet<CharacterId>();
        foreach (var id in characters)
        {
            if (player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.Appearance is not { } look) continue;
            if (look.Animal is not null) animals.Add(id);
            else if (RecordOf(look) is null) continue;
            listed.Add(id);
        }
        if (listed.Count == 0) return false;
        Editor = new EditorSession(kind, listed, animals, surgeon, paid);
        Previews.Clear();
        player.FaithfulScreens.Open("editor");
        ShowEditorCharacter();
        player.UpdateMenuPause();
        return true;
    }

    /// <summary>
    /// The CHARACTER (or animal) record of a look: its loadout's appearance record, else its body's.
    /// </summary>
    static Data.Fcs.FcsRecord? RecordOf(CharacterAppearance look) => look.Loadout?.Appearance.Record ?? look.Body?.Record;

    /// <summary>
    /// The session's draft of a character, made from its look on first use; null for an animal or a character without a look.
    /// </summary>
    internal CharacterDraft? Draft(CharacterId id)
    {
        if (Editor is null || EditorData is not { } data || Editor.Animals.Contains(id)) return null;
        if (Editor.Drafts.TryGetValue(id, out var draft)) return draft;
        if (player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { Appearance: { } look } snapshot
                || RecordOf(look) is not { } record)
            return null;
        draft = new CharacterDraft(data.Db, data.InstallRoot, record, snapshot.Name);
        Editor.Drafts[id] = draft;
        Editor.StartNames[id] = snapshot.Name;
        return draft;
    }

    /// <summary>
    /// Shows the current character: its name in NameText, selected in the world; at creation a race the editor does not offer becomes the first
    /// offered one.
    /// </summary>
    void ShowEditorCharacter()
    {
        if (Editor is null) return;
        Editor.Shown.Add(Editor.Current);
        if (Editor.Animals.Contains(Editor.Current))
        {
            player.FaithfulScreens.SetText("editor", "NameText", AnimalName(Editor, Editor.Current));
            player.Select([Editor.Current], false);
            return;
        }
        if (Draft(Editor.Current) is not { } draft) return;
        // At creation a character of a race the editor doesn't offer (a start's `force race`) takes the first offered race (FUN_1405efd60).
        if (Editor.Kind == EditKind.Creation && EditorData is { } data && EditorRaces(data.Db) is [{ } first, ..] groups
                && IndexOf(groups, draft.Race) < 0)
            draft.SetRace(first.Races[0], Editor.Seed++);
        player.FaithfulScreens.SetText("editor", "NameText", draft.Name);
        player.Select([Editor.Current], false);
    }

    /// <summary>
    /// Closes the editor (and its file dialog) and drops the session and its previews.
    /// </summary>
    void CloseEditor()
    {
        player.FaithfulScreens.Close("file");
        player.FaithfulScreens.Close("editor");
        Editor = null;
        Previews.Clear();
        player.UpdateMenuPause();
    }

    #endregion

    #region Editor scene

    /// <summary>
    /// The ImageBox texture name of the renderer's stage picture (<see cref="Meitou.Game.Rendering.Characters.CharacterRenderer.StageTexture"/>).
    /// </summary>
    public const string StageTexture = "meitou:stage";
    /// <summary>
    /// Finds a game asset's path by file name (the host sets it); null for one not found.
    /// </summary>
    internal Func<string, string?>? FindAsset;
    /// <summary>
    /// The path of <c>SandyGround.dds</c>, the scene's ground, found once; empty when it is missing.
    /// </summary>
    string? _groundFile;

    /// <summary>
    /// The editor's scene behind its panels (the original's compositor <c>Kenshi_Character_Editor</c>): <c>SandyGround.dds</c> over the whole window,
    /// stretched as the original's full-screen quad is, and the character's picture on it. First in the screen's controls, so the panels draw over
    /// both.
    /// </summary>
    internal List<UiControl> EditorBackdrop()
    {
        var backdrop = new List<UiControl>();
        if (player.UiPainter is not { } painter || EditorInWorld) return backdrop;
        _groundFile ??= FindAsset?.Invoke("SandyGround.dds") ?? "";
        var all = new UiRect(0, 0, player.Width, player.Height);
        // A sand colour first, so the window is covered before the ground texture is decoded.
        backdrop.Add(new("editor-base", "Widget", all, "", "", "", true, new Dictionary<string, string>(),
            new Vector4(0.42f, 0.40f, 0.34f, 1), Pickable: false));
        if (_groundFile.Length > 0 && painter.IconReady(_groundFile))
            backdrop.Add(new("editor-ground", "ImageBox", all, "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = Rendering.Ui.UiPainter.IconTexture(_groundFile) },
                Vector4.Zero, Pickable: false));
        if (painter.HasTexture(StageTexture))
            backdrop.Add(new("editor-stage", "ImageBox", all, "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = StageTexture, ["ImageFlipV"] = "true" }, Vector4.Zero, Pickable: false));
        return backdrop;
    }

    /// <summary>
    /// The idle stance the editor shows a character in: its draft's <c>idle stance</c> (the original's preview plays the chosen stance; the world's
    /// idles are the simulation's), or null while the editor is closed or has no draft of it. An animal keeps the simulation's idle: the stance is a
    /// human clip its skeleton lacks, and it stood frozen in its binding pose (#337).
    /// </summary>
    public string? EditorIdle(CharacterId id) =>
        EditorOpen && !Editor!.Animals.Contains(id) && Editor.Drafts.TryGetValue(id, out var draft)
            && draft.Record.Strings.GetValueOrDefault("idle stance") is { Length: > 0 } stance ? stance : null;

    /// <summary>
    /// The editor's camera for the host: the character, the orbit and whether it is on the head. The orbit starts over when the tab changes between
    /// the body and the head (FACE and HAIR share one view) or the character changes, as the original's camera resets then.
    /// </summary>
    public (CharacterId Character, float Yaw, float Elevation, float Distance, bool Head)? EditorView()
    {
        if (!EditorOpen || Editor is not { } s) return null;
        bool head = s.Tab != EditorTab.Body && !s.Animals.Contains(s.Current);   // an animal is always shown whole (FUN_1405efd60)
        if (s.Orbit.Head != head || s.OrbitCharacter != s.Current)
        {
            s.Orbit.Reset(head);
            s.OrbitCharacter = s.Current;
            s.Drag = null;
        }
        return (s.Current, s.Orbit.Yaw, s.Orbit.Elevation, s.Orbit.Distance, head);
    }

    #endregion

    #region Pointer and actions

    /// <summary>
    /// Whether <paramref name="p"/> is on the editor's scene rather than on one of its panels or a window above it (the editor's screen is modal:
    /// what no widget of it covers counts as the scene).
    /// </summary>
    bool OverEditorScene(Vector2 p) => player.FaithfulScreens.OpenScreens[^1] == "editor"
        && player.FaithfulScreens.HitTest(p) is { Control.Name: "modal" };

    /// <summary>
    /// A mouse button pressed away from the editor's panels starts turning the camera; false when the press belongs to a panel.
    /// </summary>
    internal bool EditorPointerDown(Host.Input.MouseButton button, Vector2 p)
    {
        if (!EditorOpen || !player.UiVisible || Editor is not { } s || !OverEditorScene(p)) return false;
        s.Drag = (button, p);
        return true;
    }

    /// <summary>
    /// Moves the camera's orbit while a button drags it; false when none does.
    /// </summary>
    internal bool EditorPointerMove(Vector2 p)
    {
        if (Editor?.Drag is not { } drag) return false;
        Editor.Orbit.Drag(p.X - drag.Last.X, p.Y - drag.Last.Y);
        Editor.Drag = (drag.Button, p);
        return true;
    }

    /// <summary>
    /// A button went up: the drag it held ends.
    /// </summary>
    internal void EditorPointerUp(Host.Input.MouseButton button)
    {
        if (Editor?.Drag is { } drag && drag.Button == button) Editor.Drag = null;
    }

    /// <summary>
    /// The wheel away from the panels zooms the camera.
    /// </summary>
    internal bool EditorWheel(Vector2 p, float steps)
    {
        if (!EditorOpen || Editor is not { } s || !OverEditorScene(p)) return false;
        s.Orbit.Zoom(steps);
        return true;
    }

    /// <summary>
    /// A widget's event in the editor's window: the tab and draft buttons, the arrows, CONFIRM, IMPORT and EXPORT; an animal's own handling for one.
    /// </summary>
    internal void EditorAction(MyGuiEvent e)
    {
        if (Editor is { } open && open.Animals.Contains(open.Current))
        {
            AnimalEditorAction(open, e);
            return;
        }
        if (Editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        bool creation = s.Kind == EditKind.Creation;
        switch (e.Widget)
        {
            case "BodyTabButton":
                s.Tab = EditorTab.Body;
                return;
            case "FaceTabButton":
                s.Tab = EditorTab.Face;
                return;
            case "HairTabButton":
                s.Tab = EditorTab.Hair;
                return;
            case "NameText" when e.Action == "text":
                draft.Name = e.Text;
                return;
            case "RandomiseButton":
                draft.Randomise(s.Tab, s.Seed++);
                return;
            case "RandomiseAllButton":
                draft.Randomise(null, s.Seed++);
                return;
            case "ResetButton":
                draft.Reset();
                return;
            case "ClothesButton":
                s.Clothes = !s.Clothes;
                return;
            case "CharLeftArrow" or "CharRightArrow":
                ChangeEditorCharacter(s, e.Widget);
                return;
            case "RaceLeftButton" or "RaceRightButton" or "SubRaceLeftButton" or "SubRaceRightButton" when creation:
                {
                    var groups = EditorRaces(data.Db);
                    if (groups.Count == 0) return;
                    int g = Math.Max(0, IndexOf(groups, draft.Race));
                    int r = Math.Max(0, groups[g].Races.ToList().FindIndex(x => x.StringId == draft.Race.StringId));
                    int step = e.Widget.Contains("Left", StringComparison.Ordinal) ? -1 : 1;
                    if (e.Widget.StartsWith("Race", StringComparison.Ordinal))
                    {
                        g = (g + step + groups.Count) % groups.Count;
                        r = 0;
                    }
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
                Files.ExportLook(draft, data.InstallRoot);
                return;
            case "ImportButton":
                Files.ShowExportFolder(data.InstallRoot);
                player.FaithfulScreens.Open("file");
                return;
        }
        // A row's bar moved (its arrows, track or thumb) or a picker's arrow was pressed; the row's widget names the control.
        if (_editorRows.FirstOrDefault(r => r.Owner == e.Widget) is { Owner: not null } row
                && draft.Controls(s.Tab).ElementAtOrDefault(row.Control) is { } control)
        {
            if (e.Action == "scroll" && !control.IsPicker) draft.Set(control, control.Min + (int)MathF.Round(e.Value));
            else if (e.Action == "step" && control.IsPicker) draft.Step(control.Kind, (int)e.Value);
        }
    }


    /// <summary>
    /// Steps to the previous or next listed character, wrapping round.
    /// </summary>
    void ChangeEditorCharacter(EditorSession s, string arrow)
    {
        s.Index = (s.Index + (arrow == "CharLeftArrow" ? s.Characters.Count - 1 : 1)) % s.Characters.Count;
        ShowEditorCharacter();
    }

    /// <summary>
    /// An animal's page: its name, the character arrows, CLOTHES and CONFIRM; nothing else is shown for it (<see cref="BindAnimalEditor"/>).
    /// </summary>
    void AnimalEditorAction(EditorSession s, MyGuiEvent e)
    {
        switch (e.Widget)
        {
            case "NameText" when e.Action == "text":
                s.AnimalNames[s.Current] = e.Text;
                return;
            case "ClothesButton":
                s.Clothes = !s.Clothes;
                return;
            case "CharLeftArrow" or "CharRightArrow":
                ChangeEditorCharacter(s, e.Widget);
                return;
            case "ConfirmButton":
                ConfirmEditor();
                return;
        }
    }

    /// <summary>
    /// An animal's name in the editor: as typed, else its name in the world, which is kept as its start name (an unchanged name is not sent).
    /// </summary>
    string AnimalName(EditorSession s, CharacterId id)
    {
        if (s.AnimalNames.TryGetValue(id, out var typed)) return typed;
        string name = player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "";
        s.StartNames.TryAdd(id, name);
        return name;
    }

    /// <summary>
    /// The race group holding <paramref name="race"/>; -1 when none does.
    /// </summary>
    internal static int IndexOf(IReadOnlyList<EditorRaceGroup> groups, GameRecord race)
    {
        for (int i = 0; i < groups.Count; i++)
            if (groups[i].Races.Any(r => r.StringId == race.StringId)) return i;
        return -1;
    }

    /// <summary>
    /// The races this session offers: at creation the playable ones, limited to the start's <c>force race</c>; a surgeon offers none (race stays).
    /// </summary>
    internal IReadOnlyList<EditorRaceGroup> EditorRaces(GameDatabase db) =>
        Editor?.Kind == EditKind.Creation ? PlayableRaces.Of(db, [.. player.Session.World.Player.CreationRaces]) : [];

    /// <summary>
    /// CONFIRM. Like the original (<c>FUN_1405e9f40</c>), a squad with characters never shown asks first ("There are other characters whose
    /// appearance haven't been changed..."). Every changed character is sent to the world (a surgeon's patient always: the surgery is paid on
    /// confirming); at creation the creation then ends.
    /// </summary>
    void ConfirmEditor(bool asked = false)
    {
        if (Editor is not { } s) return;
        if (!asked && s.Shown.Count < s.Characters.Count)
        {
            player.Confirm("CONFIRM",
                "There are other characters whose appearance haven't been changed. Are you sure you want to continue?", () => ConfirmEditor(true));
            return;
        }
        long tick = player.Session.World.Tick;
        foreach (var (id, draft) in s.Drafts)
            if (s.Kind == EditKind.Surgery || draft.Version > 0 || draft.Name != s.StartNames[id])
                player.Say(new EditCharacter(id, CharacterDraft.Copy(draft.Record), draft.Name, s.Kind) { Tick = tick,
                    Surgeon = s.Surgeon, Paid = s.Paid });
        // An animal's page changes its name only (FUN_1405e9410 renames whichever character is shown).
        foreach (var (id, name) in s.AnimalNames)
            if (name != s.StartNames.GetValueOrDefault(id))
                player.Say(new EditCharacter(id, null, name, s.Kind) { Tick = tick, Surgeon = s.Surgeon, Paid = s.Paid });
        if (s.Kind == EditKind.Creation) player.Say(new FinishCreation { Tick = tick });
        CloseEditor();
        player.Session.Simulation.SetSpeed(1);   // CONFIRM unpauses and sets the speed to Normal for every kind of editor (FUN_1405e9f40)
    }

    /// <summary>
    /// For the screenshot showcase: the open editor's character gets a new random look and name, and CONFIRM is pressed (the creation path end to
    /// end).
    /// </summary>
    internal void ShowcaseEdit()
    {
        if (Editor is not { } s || Draft(s.Current) is not { } draft) return;
        draft.Randomise(null, 7);
        draft.Name = "Edited " + draft.Name;
        ConfirmEditor(asked: true);
    }
    #endregion

    #region Bindings

    /// <summary>
    /// What an animal's page hides (<c>FUN_1405efd60</c>, <c>FUN_1405ee0c0</c>).
    /// </summary>
    static readonly string[] AnimalHidden =
    [
        "BodyTabButton", "FaceTabButton", "HairTabButton", "TabsPanelContent", "RandomiseButton", "RandomiseAllButton", "ResetButton",
        "ImportButton", "ExportButton",
        "RaceLeftButton", "RaceRightButton", "SubRaceLeftButton", "SubRaceRightButton", "GenderLeftButton", "GenderRightButton",
    ];

    /// <summary>
    /// The widgets whose captions the original sets from the catalog (<c>FUN_1405f1a40</c>), upper-cased, and their messages.
    /// </summary>
    static readonly (string Widget, string Key)[] EditorCaptions =
    [
        ("RandomiseButton", "Randomise"), ("ResetButton", "Reset"), ("RandomiseAllButton", "Randomise All"),
        ("BodyTabButton", "Body"), ("FaceTabButton", "Face"),
        ("HairTabButton", "Hair"), ("ImportButton", "Import"), ("ExportButton", "Export"), ("ClothesButton", "Clothes"),
        ("ConfirmButton", "Confirm"), ("lbRace", "Race"),
        ("lbSubRace", "Subrace"), ("lbGender", "Gender"), ("lbChangeCharacter", "Change Character"), ("lbRaceDescription", "Race Description"),
        ("lbRaceStats", "Race Stats"),
    ];

    /// <summary>
    /// Fills the editor window's bindings for the current character and tab (an animal's own for one).
    /// </summary>
    internal void BindEditor(UiBindings b)
    {
        if (Editor is { } open && open.Animals.Contains(open.Current))
        {
            BindAnimalEditor(b, open);
            return;
        }
        if (Editor is not { } s || Draft(s.Current) is not { } draft || EditorData is not { } data) return;
        bool creation = s.Kind == EditKind.Creation;
        string[] buttons =
        [
            "BodyTabButton", "FaceTabButton", "HairTabButton", "RandomiseButton", "RandomiseAllButton", "ResetButton", "ClothesButton", "ConfirmButton",
        ];
        foreach (string name in buttons)
        {
            b.Disabled.Remove(name);
            b.Actions[name] = name;
        }

        // Race, subrace and gender arrows: only at creation, and only where there is a choice (FUN_1405ee0c0); hidden otherwise.
        var groups = EditorRaces(data.Db);
        int g = IndexOf(groups, draft.Race);
        void Arrows(bool live, params string[] names)
        {
            foreach (string name in names)
                if (live)
                {
                    b.Disabled.Remove(name);
                    b.Actions[name] = name;
                }
                else b.Hidden.Add(name);
        }
        Arrows(creation && groups.Count > 1, "RaceLeftButton", "RaceRightButton");
        Arrows(creation && g >= 0 && groups[g].Races.Count > 1, "SubRaceLeftButton", "SubRaceRightButton");
        Arrows(creation && !draft.Race.GetBool("single gender"), "GenderLeftButton", "GenderRightButton");
        // CHANGE CHARACTER stays with one character, its arrows and "1/1" disabled (FUN_1405f1a40; the original's screen shows them).
        if (s.Characters.Count > 1)
            foreach (string name in new[] { "CharLeftArrow", "CharRightArrow" })
            {
                b.Disabled.Remove(name);
                b.Actions[name] = name;
            }
        else b.Disabled.Add("CharIndexText");
        foreach (string name in new[] { "ImportButton", "ExportButton" })
        {
            b.Disabled.Remove(name);
            b.Actions[name] = name;
        }
        // The captions the code sets (FUN_1405f1a40): the catalog's message, upper case; the layout's own captions (RESET ALL, SUBRACE...) never
        // show.
        foreach (var (name, key) in EditorCaptions) b.Text[name] = Loc.Tr(key).ToUpperInvariant();
        string tabButton = s.Tab switch { EditorTab.Face => "FaceTabButton", EditorTab.Hair => "HairTabButton", _ => "BodyTabButton" };
        b.Properties[tabButton] = new Dictionary<string, string> { ["Checked"] = "true" };
        b.Properties["ClothesButton"] = new Dictionary<string, string> { ["Checked"] = s.Clothes ? "true" : "false",
            ["StateSelected"] = s.Clothes ? "true" : "false" };
        var everyRace = PlayableRaces.Of(data.Db);
        int shown = IndexOf(everyRace, draft.Race);
        b.Text["RaceText"] = shown >= 0 ? everyRace[shown].Group.Name : draft.Race.Name;
        b.Text["SubRaceText"] = draft.Race.Name;
        // "-" without a choice of gender (FUN_1405ee0c0: the race's gender list has fewer than two entries).
        b.Text["GenderText"] = draft.Race.GetBool("single gender") ? "-" : draft.Female ? "Female" : "Male";
        b.Text["CharIndexText"] = $"{s.Index + 1}/{s.Characters.Count}";
        b.Text["DescriptionPanelText"] = draft.Race.GetString("description");
        b.Properties["DescriptionPanelText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true",
            ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_WordWrap") };
        b.Properties["NameText"] = new Dictionary<string, string> { ["DefaultAction"] = "false" };
        b.Properties["ConfirmButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
        BindEditorRows(b, draft, s.Tab);
    }

    /// <summary>
    /// An animal's page (<c>FUN_1405efd60</c>, <c>FUN_1405ee0c0</c>): its name, its race's name as race and subrace, gender "-", the race's
    /// description and stats, CLOTHES, CONFIRM and the character arrows. The tabs and their rows, RANDOMISE, RESET, RANDOMISE ALL, IMPORT, EXPORT and
    /// every race and gender arrow are hidden.
    /// </summary>
    void BindAnimalEditor(UiBindings b, EditorSession s)
    {
        if (player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == s.Current)?.Appearance is not { } look) return;
        foreach (string name in new[] { "ClothesButton", "ConfirmButton" })
        {
            b.Disabled.Remove(name);
            b.Actions[name] = name;
        }
        foreach (string name in AnimalHidden) b.Hidden.Add(name);
        if (s.Characters.Count > 1)
        {
            foreach (string name in new[] { "CharLeftArrow", "CharRightArrow" })
            {
                b.Disabled.Remove(name);
                b.Actions[name] = name;
            }
        }
        else b.Disabled.Add("CharIndexText");
        foreach (var (name, key) in EditorCaptions) b.Text[name] = Loc.Tr(key).ToUpperInvariant();

        string clothes = s.Clothes ? "true" : "false";
        b.Properties["ClothesButton"] = new Dictionary<string, string> { ["Checked"] = clothes, ["StateSelected"] = clothes };
        b.Text["RaceText"] = look.Race.Name;
        b.Text["SubRaceText"] = look.Race.Name;
        b.Text["GenderText"] = "-";
        b.Text["CharIndexText"] = $"{s.Index + 1}/{s.Characters.Count}";
        b.Text["DescriptionPanelText"] = look.Race.GetString("description");
        string ink = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_WordWrap");
        b.Properties["DescriptionPanelText"] = new Dictionary<string, string> { ["Static"] = "true", ["WordWrap"] = "true",
            ["TextAlign"] = "Left Top", ["TextColour"] = ink };
        b.Properties["NameText"] = new Dictionary<string, string> { ["DefaultAction"] = "false" };
        b.Properties["ConfirmButton"] = new Dictionary<string, string> { ["DefaultAction"] = "true" };
    }

    #endregion

    #region Editor rows

    /// <summary>
    /// The editor's layout as read; the shown tab's rows are added to a copy of it (<see cref="EnsureEditorRows"/>).
    /// </summary>
    internal MyGuiLayout? EditorLayout;
    /// <summary>
    /// The kinds of the rows the editor's layout has now, so it is composed again only when they change (another tab, race or gender).
    /// </summary>
    internal string EditorRowsShape = "";
    /// <summary>
    /// The shown tab's rows: the widget whose events and texts are the row's (the template's owner) and the control it edits.
    /// </summary>
    readonly List<(string Owner, int Control)> _editorRows = [];
    /// <summary>
    /// The row widgets in the scroll view, top to bottom, with their template's height (layout pixels).
    /// </summary>
    readonly List<(string Name, float Height)> _editorRowWidgets = [];
    /// <summary>
    /// The scroll view widget the tab's rows are made in.
    /// </summary>
    const string EditorRowsView = "EditorRows";

    /// <summary>
    /// The rows of a tab, as the original makes them (<c>FUN_1405f1a40</c>, <c>FUN_1405ebd50</c>; docs/characters.md "Character editor"): a
    /// <c>Kenshi_ScrollViewEmptyLight</c> scroll view in <c>TabsPanelContent</c> (vertical bar only), and in it one widget per control, stacked at
    /// its template's height: a picker is a <c>Kenshi_ListSlider</c>, a slider a <c>Kenshi_Slider</c> (name box, a <c>Kenshi_ScrollBar</c> with its
    /// arrows, value box), the three hair colour sliders one <c>Kenshi_MultiSlider</c>.
    /// </summary>
    void EnsureEditorRows(CharacterDraft draft, EditorTab tab)
    {
        if (EditorLayout is null) return;
        var controls = draft.Controls(tab);
        string shape = tab + ":" + string.Join(",", controls.Select(c => c.Kind));
        if (shape == EditorRowsShape) return;
        EditorRowsShape = shape;
        _editorRows.Clear();
        _editorRowWidgets.Clear();
        var rows = new List<MyGuiWidget>();
        var parts = new List<(string Row, string? Part, int Control)>();
        for (int i = 0; i < controls.Count; i++)
        {
            string name = "EditorRow" + i.ToString(CultureInfo.InvariantCulture);
            string template = controls[i].Kind == EditorControlKind.HairHue ? "Kenshi_MultiSlider" : controls[i].IsPicker
                ? "Kenshi_ListSlider" : "Kenshi_Slider";
            float height = player.UiResources.Templates.TryGetValue(template, out var resource)
                ? MyGuiReader.ReadResourceLayout(resource).Roots[0].Position.Height : 55;
            rows.Add(new("Widget", name, template, "", "Left Top", new(0, 0, 455, height), false, new Dictionary<string, string>(),
                new Dictionary<string, string>(), []));
            _editorRowWidgets.Add((name, height));
            if (template == "Kenshi_MultiSlider")
            {
                for (int k = 0; k < 3 && i + k < controls.Count; k++)
                    parts.Add((name, "Slider" + (k + 1).ToString(CultureInfo.InvariantCulture), i + k));
                i += 2;
            }
            else parts.Add((name, null, i));
        }
        var view = new MyGuiWidget("ScrollView", EditorRowsView, "Kenshi_ScrollViewEmptyLight", "", "Stretch", new(0, 0, 1, 1), true,
            new Dictionary<string, string> { ["VisibleHScroll"] = "false", ["VisibleVScroll"] = "true", ["CanvasAlign"] = "Left Top" },
                new Dictionary<string, string>(), rows);
        static MyGuiWidget Fill(MyGuiWidget w, MyGuiWidget view) =>
            w.Name == "TabsPanelContent" ? w with { Children = [view] } : w with { Children = [.. w.Children.Select(c => Fill(c, view))] };
        player.FaithfulScreens.Replace("editor", EditorLayout with { Roots = [.. EditorLayout.Roots.Select(r => Fill(r, view))] });
        player.FaithfulScreens.SetScroll("editor", EditorRowsView, 0);
        // A hair colour slider's own template widget (Slider1..3 of the multi-slider) owns its bar and texts.
        var widgets = player.FaithfulScreens.Layout("editor").Widgets.ToList();
        foreach (var (row, part, control) in parts)
        {
            string rowOwner = part is null ? row : widgets.FirstOrDefault(w => w.Properties.GetValueOrDefault("TemplateOwner") == row
                && w.Properties.GetValueOrDefault("TemplatePart") == part)?.Name ?? row;
            _editorRows.Add((rowOwner, control));
        }
    }

    /// <summary>
    /// The rows' texts and bars: the name box the control's message (the face and hair lists "Face", "Hair", "Beard"; the hair colour sliders
    /// "Colour", "Saturation", "Brightness" under "Hair colour"), a slider's bar its range and value and the value box the number, a list its choice
    /// and "n/count". The canvas is as wide as the view (less the bar when the rows overflow it), the rows as tall as their templates.
    /// </summary>
    void BindEditorRows(UiBindings b, CharacterDraft draft, EditorTab tab)
    {
        EnsureEditorRows(draft, tab);
        float scale = player.UiScale;
        b.TemplateScale = scale;
        var layout = player.FaithfulScreens.Layout("editor");
        var controls = draft.Controls(tab);
        foreach (var (owner, index) in _editorRows)
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
                EditorControlKind.HairHue => "Colour",
                EditorControlKind.HairSaturation => "Saturation",
                EditorControlKind.HairBrightness => "Brightness",
                _ => c.Label,
            };
            b.Text[owner + ".NumberText"] = value.ToString(CultureInfo.InvariantCulture);
            if (layout.Widgets.FirstOrDefault(w => w.Type == "ScrollBar" && w.Properties.GetValueOrDefault("TemplateOwner") == owner
                    && w.Properties.GetValueOrDefault("TemplatePart") == "Slider") is { } bar)
            {
                b.Properties[bar.Name] = new Dictionary<string, string> { ["Range"] = (c.Max - c.Min + 1).ToString(CultureInfo.InvariantCulture) };
                player.FaithfulScreens.SetScroll("editor", bar.Name, value - c.Min);
            }
            if (c.Kind == EditorControlKind.HairHue
                && _editorRowWidgets.FirstOrDefault(r => owner.StartsWith(r.Name + "@", StringComparison.Ordinal)).Name is { } multi)
                b.Text[multi + ".Name"] = "Hair colour";
        }
        // Rows stacked at their templates' heights on a canvas as wide as the view: its client, less the vertical bar when the rows are taller than
        // it.
        var content = RealBounds(layout, "TabsPanelContent");
        float total = _editorRowWidgets.Sum(r => r.Height) * scale;
        var scrollBar = layout.Widgets.FirstOrDefault(w => w.Properties.GetValueOrDefault("TemplateOwner") == EditorRowsView
            && w.Properties.GetValueOrDefault("TemplatePart") == "VScroll");
        float barWidth = (scrollBar?.Position.Width ?? 32) * scale;
        float width = total > content.Height ? content.Width - barWidth : content.Width;
        if (total <= 0) return;
        b.Canvas[EditorRowsView] = (width, total);
        float y = 0;
        foreach (var (name, height) in _editorRowWidgets)
        {
            if (layout.Find(name) is { } row) b.RelativePositions[row] = new(0, y / total, 1, height * scale / total);
            y += height * scale;
        }
    }

    /// <summary>
    /// A widget's bounds on the screen from its layout's relative positions (the editor's panels are all <c>position_real</c>).
    /// </summary>
    internal UiRect RealBounds(MyGuiLayout layout, string name)
    {
        UiRect? Walk(MyGuiWidget w, UiRect parent)
        {
            var bounds = w.RealPosition ? w.Position.RelativeTo(parent, true) : w.Position.RelativeTo(parent, false);
            if (w.Name == name) return bounds;
            foreach (var child in w.Children) if (Walk(child, bounds) is { } found) return found;
            return null;
        }
        foreach (var root in layout.Roots) if (Walk(root, new(0, 0, player.Width, player.Height)) is { } found) return found;
        return default;
    }

    /// <summary>
    /// The editor's own controls after the layout's: the shown character's race stats.
    /// </summary>
    internal void AddEditorContent(List<UiControl> controls)
    {
        if (Editor is not { } s) return;
        // An animal's page shows its race's stats too (FUN_1405ee0c0 fills them for any character).
        var race = s.Animals.Contains(s.Current)
            ? player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == s.Current)?.Appearance?.Race : Draft(s.Current)?.Race;
        if (race is not null && controls.FirstOrDefault(c => c.Name == "StatsPanelText") is { } stats) RaceStats.AddRaceStats(controls, race, stats);
    }

    #endregion
}
