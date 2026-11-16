using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Editing;

namespace Meitou.Game.DevTools;

/// <summary>
/// The debug menu's "Edit characters" window (docs/game/ui-input.md "Edit characters window"): a modal window in the middle of the screen, drawn over
/// the interface, with the player's characters by squad on the left and the picked one's stats on the right, each with a slider (0 to 100) and a
/// number box (attributes up to 1000), and buttons for its appearance, max attributes and max skills. It is a screen of the game's own window system
/// (<see cref="MyGuiScreenHost"/>), so the mouse, the keyboard, the clipboard and the scroll bars work as they do in the other windows; the picked
/// character is the window's own, not the game's selection. A slider's value is sent when the button is released, a number as it is typed. Layout:
/// <see cref="DebugCharactersLayout"/>.
/// </summary>
sealed class DebugCharactersWindow(PlayerInterface player)
{
    #region State

    /// <summary>
    /// The stat the max out button of the skills leaves alone: the attributes.
    /// </summary>
    static readonly StatsEnumerated[] Attributes = StatsWindow.StatPanels[0];

    /// <summary>
    /// The value the max out buttons set.
    /// </summary>
    const float MaxedOut = 100;

    /// <summary>
    /// The window's size in layout pixels at the interface's scale.
    /// </summary>
    static readonly Vector2 WindowSize = new(DebugCharactersLayout.WindowWidth, DebugCharactersLayout.WindowHeight);

    /// <summary>
    /// The layout in use, its roster and the stats it edits; null before the first opening.
    /// </summary>
    DebugCharactersLayout.Built? _built;

    /// <summary>
    /// The roster the layout was made for, flattened to the characters in the order of their buttons.
    /// </summary>
    IReadOnlyList<RosterSquad> _roster = [];

    /// <summary>
    /// The characters of <see cref="_roster"/> in the order of their buttons.
    /// </summary>
    CharacterId[] _characters = [];

    /// <summary>
    /// The stat sections the layout was made for.
    /// </summary>
    IReadOnlyList<StatSection> _sections = [];

    /// <summary>
    /// The stats the layout's rows edit, in the order of the rows.
    /// </summary>
    EditableStat[] _stats = [];

    /// <summary>
    /// What the layout was made from: the roster's ids and the number of stats; the layout is made again when it changes.
    /// </summary>
    string _layoutKey = "";

    /// <summary>
    /// The character being edited; none until one is picked in the list.
    /// </summary>
    CharacterId _character = CharacterId.None;

    /// <summary>
    /// The sliders being moved with the button held, by stat row: the value shown and sent when the button is released.
    /// </summary>
    readonly Dictionary<int, float> _sliding = [];

    /// <summary>
    /// The appearance editor is open from here: the window opens again when it closes.
    /// </summary>
    bool _returnFromEditor;

    /// <summary>
    /// The digits typed in each section's set-all box, by section.
    /// </summary>
    readonly Dictionary<int, string> _sectionValues = [];

    /// <summary>
    /// The box that had the keyboard focus at the last <see cref="Sync"/>: one that loses it applies its value.
    /// </summary>
    (string Screen, string Widget)? _lastFocus;

    /// <summary>
    /// The values Copy values kept, by stat number and by <see cref="ModSkills.All"/> order; null until one is copied, and forgotten when the window
    /// closes.
    /// </summary>
    (float[] Stats, float[] ModSkills)? _copied;

    /// <summary>

    /// <summary>
    /// The window is open.
    /// </summary>
    internal bool Shown => player.FaithfulScreens.IsRegistered(DebugCharactersLayout.Screen)
        && player.FaithfulScreens.IsOpen(DebugCharactersLayout.Screen);

    #endregion

    #region Lifetime

    /// <summary>
    /// Opens the window (the debug menu's Edit characters row): the interface pauses the game while it is open.
    /// </summary>
    internal void Open()
    {
        Refresh();
        player.FaithfulScreens.Open(DebugCharactersLayout.Screen);
        player.UpdateMenuPause();
    }

    /// <summary>
    /// Every frame before the interface is arranged: opens the window again when the appearance editor it opened has closed, drops the sliders of a
    /// closed window, applies a section's value when its box lost the keyboard focus, and makes the layout again when the roster changed.
    /// </summary>
    internal void Sync()
    {
        if (_returnFromEditor && !player.CharacterEditor.EditorOpen)
        {
            _returnFromEditor = false;
            Open();
        }

        if (!Shown)
        {
            _sliding.Clear();
            _lastFocus = null;
            _copied = null;
            return;
        }

        ApplyLeftBox();
        Refresh();
    }

    /// <summary>
    /// A section's value box that had the keyboard focus and no longer does applies its value now (a click elsewhere, Tab, Esc).
    /// </summary>
    void ApplyLeftBox()
    {
        var focus = player.FaithfulScreens.Focus is { } f ? (f.Screen, f.Widget) : ((string, string)?)null;
        if (_lastFocus is { } last && last != focus && last.Screen == DebugCharactersLayout.Screen
            && IndexOf(last.Widget, DebugCharactersLayout.SectionSetValueName) is >= 0 and var section)
        {
            SetSection(section);
        }

        _lastFocus = focus;
    }

    /// <summary>
    /// Makes the layout for the player's squads and the stat sections when they differ from the ones it was made for; the screen's state (its
    /// scrolls and texts) stays.
    /// </summary>
    void Refresh()
    {
        var roster = RosterNow();
        var sections = DebugCharactersLayout.Sections();
        string key = string.Join('/', roster.Select(s => string.Join(',', s.Members))) + "#" + sections.Sum(s => s.Stats.Count);
        if (key == _layoutKey && _built is not null) return;

        (_layoutKey, _roster, _sections) = (key, roster, sections);
        _characters = [.. roster.SelectMany(s => s.Members)];
        _stats = [.. sections.SelectMany(s => s.Stats)];
        _built = DebugCharactersLayout.Build(roster, sections);

        if (player.FaithfulScreens.IsRegistered(DebugCharactersLayout.Screen))
            player.FaithfulScreens.Replace(DebugCharactersLayout.Screen, _built.Layout);
        else player.FaithfulScreens.Register(DebugCharactersLayout.Screen, _built.Layout, modal: true);

        if (!_characters.Contains(_character)) _character = CharacterId.None;
    }

    /// <summary>
    /// The player's squads in the game's order with the characters of each that the world shows; a squad without any is left out.
    /// </summary>
    List<RosterSquad> RosterNow()
    {
        var snapshot = player.Session.CurrentSnapshot;
        var present = snapshot.Characters.Where(c => c.IsPlayer).Select(c => c.Id).ToHashSet();
        var roster = new List<RosterSquad>();
        foreach (var squad in snapshot.PlayerSquads)
        {
            var members = squad.Members.Where(present.Contains).ToArray();
            if (members.Length > 0) roster.Add(new(squad.Name, members));
        }

        return roster;
    }

    #endregion

    #region Bindings

    /// <summary>
    /// The window's bindings: its size, the captions, the buttons, the list of characters and, for the picked one, the stat rows.
    /// </summary>
    internal void Bind(UiBindings b)
    {
        if (_built is not { } built) return;

        float scale = player.UiScale;
        b.TemplateScale = scale;
        b.Positions[DebugCharactersLayout.Root] = new(0, 0, WindowSize.X * scale, WindowSize.Y * scale);
        b.Text[DebugCharactersLayout.Root] = DebugCharactersLayout.CaptionFor(NameOf(_character));

        BindCharacters(b, built);
        BindStats(b, built);
    }

    /// <summary>
    /// The squads' headings and the characters' buttons, the picked one marked, stacked in the list's scroll view.
    /// </summary>
    void BindCharacters(UiBindings b, DebugCharactersLayout.Built built)
    {
        var names = player.Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToDictionary(c => c.Id, c => c.Name);
        foreach (var row in built.Characters.Where(r => r.Name.Length > 0))
        {
            if (row.Heading)
            {
                b.Text[DebugCharactersLayout.HeadingText(row.Name)] = _roster[row.Index].Name.ToUpperInvariant();
                continue;
            }

            var id = _characters[row.Index];
            b.Text[row.Name] = names.GetValueOrDefault(id, "");
            Enable(b, row.Name);
            if (id == _character)
                b.Properties[row.Name] = new Dictionary<string, string> { ["StateSelected"] = "true", ["Checked"] = "true" };
        }

        Stack(b, DebugCharactersLayout.CharacterView, built.Characters);
    }

    /// <summary>
    /// The right side: the name, the buttons and the stat rows of the picked character; a line asking for a pick while there is none.
    /// </summary>
    void BindStats(UiBindings b, DebugCharactersLayout.Built built)
    {
        var picked = Stats();
        if (picked is null)
        {
            b.Text[DebugCharactersLayout.PickText] = _characters.Length == 0 ? "There are no characters." : "Pick a character on the left.";
            foreach (string hidden in new[] { DebugCharactersLayout.StatView, DebugCharactersLayout.AppearanceButton,
                DebugCharactersLayout.MaxAttributesButton, DebugCharactersLayout.MaxSkillsButton, DebugCharactersLayout.CopyButton,
                DebugCharactersLayout.PasteButton })
            {
                b.Hidden.Add(hidden);
            }

            return;
        }

        b.Hidden.Add(DebugCharactersLayout.PickText);
        b.Text[DebugCharactersLayout.AppearanceButton] = "EDIT APPEARANCE";
        b.Text[DebugCharactersLayout.MaxAttributesButton] = "MAX ATTRIBUTES";
        b.Text[DebugCharactersLayout.MaxSkillsButton] = "MAX SKILLS";
        b.Text[DebugCharactersLayout.CopyButton] = "COPY VALUES";
        b.Text[DebugCharactersLayout.PasteButton] = "PASTE VALUES";
        Enable(b, DebugCharactersLayout.AppearanceButton, DebugCharactersLayout.MaxAttributesButton, DebugCharactersLayout.MaxSkillsButton,
            DebugCharactersLayout.CopyButton);
        if (_copied is null) b.Disabled.Add(DebugCharactersLayout.PasteButton);
        else Enable(b, DebugCharactersLayout.PasteButton);

        foreach (var row in built.Stats.Where(r => r.Heading && r.Name.Length > 0))
        {
            b.Text[DebugCharactersLayout.HeadingText(row.Name)] = _sections[row.Index].Title.ToUpperInvariant();
            BindSectionSet(b, row.Index);
        }

        for (int i = 0; i < _stats.Length; i++) BindStat(b, i, picked);
        Stack(b, DebugCharactersLayout.StatView, built.Stats);
    }

    /// <summary>
    /// A section heading's set-all control: its label, and its value's box left alone while it is typed in.
    /// </summary>
    void BindSectionSet(UiBindings b, int section)
    {
        string n = section.ToString(CultureInfo.InvariantCulture);
        string value = DebugCharactersLayout.SectionSetValueName + n;
        b.Text[DebugCharactersLayout.SectionSetLabelName + n] = "Set all values to";

        bool typing = player.FaithfulScreens.Focus is { } focus && focus.Screen == DebugCharactersLayout.Screen && focus.Widget == value;
        if (!typing)
            player.FaithfulScreens.SetText(DebugCharactersLayout.Screen, value, _sectionValues.GetValueOrDefault(section, ""));
    }

    /// <summary>
    /// One stat's row: the label, the slider at the value (the one being moved while it is) and the number box, left alone while it is typed in.
    /// </summary>
    void BindStat(UiBindings b, int i, CharacterStats picked)
    {
        string n = i.ToString(CultureInfo.InvariantCulture);
        string value = DebugCharactersLayout.StatValueName + n;
        float level = _sliding.TryGetValue(i, out float moved) ? moved : LevelOf(_stats[i], picked);

        b.Text[DebugCharactersLayout.StatLabelName + n] = _stats[i].Label;
        player.FaithfulScreens.SetScroll(DebugCharactersLayout.Screen, DebugCharactersLayout.StatBarName + n,
            Math.Clamp(MathF.Round(level), 0, DebugCharactersLayout.SliderMax));

        bool typing = player.FaithfulScreens.Focus is { } focus && focus.Screen == DebugCharactersLayout.Screen && focus.Widget == value;
        if (!typing) player.FaithfulScreens.SetText(DebugCharactersLayout.Screen, value, LevelText(level));
    }

    /// <summary>
    /// A level as the number box shows it: the whole part, as the stats window does.
    /// </summary>
    static string LevelText(float level) => (float.IsFinite(level) ? (int)MathF.Floor(level) : 0).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Stacks <paramref name="rows"/> in <paramref name="view"/> at their heights, the cells of a line side by side: the canvas is as wide as the view
    /// and as tall as the lines.
    /// </summary>
    void Stack(UiBindings b, string view, IReadOnlyList<WindowRow> rows)
    {
        float scale = player.UiScale;
        float total = rows.Where((r, k) => EndsLine(rows, k)).Sum(r => r.Height) * scale;
        if (total <= 0) return;

        var layout = player.FaithfulScreens.Layout(DebugCharactersLayout.Screen);
        b.Canvas[view] = (0, total);
        float y = 0;
        for (int k = 0; k < rows.Count; k++)
        {
            var row = rows[k];
            if (row.Name.Length > 0 && layout.Find(row.Name) is { } widget)
                b.RelativePositions[widget] = new((float)row.Column / row.Columns, y / total, 1f / row.Columns, row.Height * scale / total);

            if (EndsLine(rows, k)) y += row.Height * scale;
        }
    }

    /// <summary>
    /// Whether row <paramref name="k"/> is the last of its line: a row of its own, the last cell, or the one before the next line's first.
    /// </summary>
    static bool EndsLine(IReadOnlyList<WindowRow> rows, int k) =>
        rows[k].Column == rows[k].Columns - 1 || k + 1 >= rows.Count || rows[k + 1].Column == 0;

    /// <summary>
    /// Makes the buttons <paramref name="names"/> take clicks, each raising an event named as itself.
    /// </summary>
    static void Enable(UiBindings b, params string[] names)
    {
        foreach (string name in names)
        {
            b.Disabled.Remove(name);
            b.Actions[name] = name;
        }
    }

    #endregion

    #region Content

    /// <summary>
    /// The colour of the dividers between the stat cells of a line: the window's light ink, faint.
    /// </summary>
    static readonly Vector4 DividerColour = new(0.894737f, 0.860072f, 0.811019f, 0.22f);

    /// <summary>
    /// What the window draws over its widgets: the characters' portraits and the dividers between the stat cells.
    /// </summary>
    internal void AddContent(List<UiControl> controls)
    {
        if (_built is not { } built) return;

        AddPortraits(controls, built);
        if (Stats() is not null) AddDividers(controls, built);
        AddButtonSeparators(controls);
    }

    /// <summary>
    /// A thin vertical line between the groups of the buttons at the bottom: the appearance editor, the two max outs, and copy and paste.
    /// </summary>
    void AddButtonSeparators(List<UiControl> controls)
    {
        float scale = player.UiScale;
        foreach (var (left, right) in new[]
        {
            (DebugCharactersLayout.AppearanceButton, DebugCharactersLayout.MaxAttributesButton),
            (DebugCharactersLayout.MaxSkillsButton, DebugCharactersLayout.CopyButton),
        })
        {
            if (controls.LastOrDefault(c => c.Name == left) is not { } before || controls.LastOrDefault(c => c.Name == right) is not { } after)
                continue;

            float x = (before.Bounds.X + before.Bounds.Width + after.Bounds.X) / 2;
            var at = new UiRect(x, before.Bounds.Y + 4 * scale, Math.Max(1, MathF.Round(1.5f * scale)), before.Bounds.Height - 8 * scale);
            controls.Add(new($"{left}-separator", "Panel", at, "", "", "", true, new Dictionary<string, string>(), DividerColour, Pickable: false));
        }
    }

    /// <summary>
    /// A thin vertical line in the right margin of every stat cell that has another after it on its line, as tall as the cell less a little.
    /// </summary>
    void AddDividers(List<UiControl> controls, DebugCharactersLayout.Built built)
    {
        float scale = player.UiScale;
        foreach (var row in built.Stats.Where(r => !r.Heading && r.Column < r.Columns - 1))
        {
            if (controls.LastOrDefault(c => c.Name == row.Name) is not { } cell || cell.Bounds.Height <= 0) continue;

            var at = new UiRect(cell.Bounds.X + DebugCharactersLayout.DividerAt * cell.Bounds.Width, cell.Bounds.Y + 4 * scale,
                Math.Max(1, MathF.Round(1.5f * scale)), cell.Bounds.Height - 8 * scale);
            controls.Add(new($"{row.Name}-divider", "Panel", at, "", "", "", true, new Dictionary<string, string>(), DividerColour,
                Pickable: false, Clip: cell.Clip));
        }
    }

    /// <summary>
    /// The characters' portraits over their buttons: the portrait atlas's cell of each, as the squads screen draws them.
    /// </summary>
    void AddPortraits(List<UiControl> controls, DebugCharactersLayout.Built built)
    {

        var cells = player.PortraitBar.PortraitMembers().Select((c, i) => (c.Id, Cell: i)).ToDictionary(p => p.Id, p => p.Cell);
        foreach (var row in built.Characters.Where(r => !r.Heading))
        {
            if (!cells.TryGetValue(_characters[row.Index], out int cell)) continue;
            if (controls.LastOrDefault(c => c.Name == row.Name) is not { } button || button.Bounds.Height <= 0)
                continue;

            float size = button.Bounds.Height - 6 * player.UiScale;
            var at = new UiRect(button.Bounds.X + 5 * player.UiScale, button.Bounds.Y + 3 * player.UiScale, size, size);
            var texture = new Dictionary<string, string>
            {
                ["ImageTexture"] = PortraitBar.PortraitTexture,
                ["ImageFlipV"] = "true",
                ["ImageRect"] = player.PortraitBar.PortraitRect(cell),
            };
            controls.Add(new($"{row.Name}-portrait", "ImageBox", at, "", "", "", true, texture, Vector4.Zero, Pickable: false, Clip: button.Clip));
        }
    }

    #endregion

    #region Events

    /// <summary>
    /// A widget's event: a character picked, a button, a slider moved (shown at once, sent when the button is released) or a number typed.
    /// </summary>
    internal void Action(MyGuiEvent e)
    {
        if (e.Action == "scroll" && IndexOf(e.Widget, DebugCharactersLayout.StatBarName) is >= 0 and var bar)
        {
            _sliding[bar] = MathF.Round(e.Value);
            return;
        }

        if (e.Action == "text" && IndexOf(e.Widget, DebugCharactersLayout.StatValueName) is >= 0 and var typed)
        {
            Typed(typed, e.Text);
            return;
        }

        if (e.Action == "text" && IndexOf(e.Widget, DebugCharactersLayout.SectionSetValueName) is >= 0 and var section)
        {
            SectionTyped(section, e.Text);
            return;
        }

        // Enter in a value box: its value is applied and the box lets go of the keys (the host blurs it after this).
        if (e.Action == DebugCharactersLayout.StatSubmit && IndexOf(e.Widget, DebugCharactersLayout.StatValueName) is >= 0 and var entered)
        {
            Typed(entered, e.Text);
            return;
        }

        if (e.Action == DebugCharactersLayout.SectionSubmit && IndexOf(e.Widget, DebugCharactersLayout.SectionSetValueName) is >= 0 and var submitted)
        {
            SectionTyped(submitted, e.Text);
            SetSection(submitted);
            return;
        }

        if (IndexOf(e.Widget, DebugCharactersLayout.CharacterButtonName) is >= 0 and var picked && picked < _characters.Length)
        {
            Pick(_characters[picked]);
            return;
        }

        switch (e.Widget)
        {
            case DebugCharactersLayout.AppearanceButton:
                OpenAppearance();
                break;
            case DebugCharactersLayout.MaxAttributesButton:
                MaxOut(Attributes, includeModSkills: false);
                break;
            case DebugCharactersLayout.MaxSkillsButton:
                MaxOut(SkillStats(), includeModSkills: true);
                break;
            case DebugCharactersLayout.CopyButton:
                CopyValues();
                break;
            case DebugCharactersLayout.PasteButton:
                PasteValues();
                break;
        }
    }

    /// <summary>
    /// A section's value box now holds <paramref name="text"/>: anything but digits is dropped, and the digits are kept until the box loses the
    /// keyboard focus.
    /// </summary>
    void SectionTyped(int section, string text)
    {
        string digits = new([.. text.Where(char.IsAsciiDigit)]);
        _sectionValues[section] = digits;
        if (digits != text)
            player.FaithfulScreens.SetText(DebugCharactersLayout.Screen, DebugCharactersLayout.SectionSetValueName + section, digits);
    }

    /// <summary>
    /// Sets every stat of section <paramref name="section"/> of the character being edited to the value in its box (over the section's maximum
    /// becomes the maximum); an empty box does nothing. The box applies itself when it loses the keyboard focus, or on Enter.
    /// </summary>
    void SetSection(int section)
    {
        if (_character.IsNone || section >= _sections.Count || !_sectionValues.TryGetValue(section, out var digits) || digits.Length == 0)
            return;

        var stats = _sections[section].Stats;
        int value = Math.Min(int.Parse(digits, CultureInfo.InvariantCulture), stats.Max(s => s.Max));
        var world = player.Session.World;
        var game = stats.Where(s => s.ModSkill is null).Select(s => s.Stat).ToArray();
        var skills = stats.Where(s => s.ModSkill is not null).Select(s => s.ModSkill!).ToArray();
        if (game.Length > 0) world.Commands.Enqueue(new DebugSetStats([_character], game, value) { Tick = world.Tick });
        if (skills.Length > 0)
            world.Commands.Enqueue(new DebugSetModSkills([_character], skills, value) { Tick = world.Tick });

        _sliding.Clear();
        ApplyWhilePaused();
    }

    /// <summary>
    /// The pointer was released anywhere: the sliders moved with the button held send their values now.
    /// </summary>
    internal void PointerUp()
    {
        if (_sliding.Count == 0) return;

        var moved = _sliding.ToArray();
        _sliding.Clear();
        if (!Shown) return;

        foreach (var (i, value) in moved) Send(_stats[i], value);
    }

    /// <summary>
    /// The number of the widget <paramref name="name"/> when it is <paramref name="prefix"/> and a number below the stats' or characters' count;
    /// -1 for another widget.
    /// </summary>
    static int IndexOf(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n
            : -1;

    /// <summary>
    /// Makes <paramref name="id"/> the character being edited.
    /// </summary>
    void Pick(CharacterId id)
    {
        _character = id;
        _sectionValues.Clear();
        _sliding.Clear();
    }

    /// <summary>
    /// A stat's number box now holds <paramref name="text"/>: anything but digits is dropped, a value over the box's maximum becomes the maximum, and
    /// the number is sent; an empty box sends nothing.
    /// </summary>
    void Typed(int i, string text)
    {
        if (i >= _stats.Length) return;

        var stat = _stats[i];
        string digits = new([.. text.Where(char.IsAsciiDigit)]);
        string box = DebugCharactersLayout.StatValueName + i.ToString(CultureInfo.InvariantCulture);
        if (digits.Length == 0)
        {
            if (text.Length > 0) player.FaithfulScreens.SetText(DebugCharactersLayout.Screen, box, "");
            return;
        }

        int value = Math.Min(int.Parse(digits, CultureInfo.InvariantCulture), stat.Max);
        string shown = value.ToString(CultureInfo.InvariantCulture);
        if (shown != text) player.FaithfulScreens.SetText(DebugCharactersLayout.Screen, box, shown);

        _sliding.Remove(i);
        Send(stat, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Sets <paramref name="stat"/> of the character being edited to <paramref name="value"/>.
    /// </summary>
    void Send(EditableStat stat, float value)
    {
        if (_character.IsNone) return;

        var world = player.Session.World;
        world.Commands.Enqueue(stat.ModSkill is { } skill
            ? new DebugSetModSkills([_character], [skill], value) { Tick = world.Tick }
            : new DebugSetStats([_character], [stat.Stat], value) { Tick = world.Tick });
        ApplyWhilePaused();
    }

    /// <summary>
    /// Sets <paramref name="stats"/> (and, with <paramref name="includeModSkills"/>, every code mod's skill) of the character being edited to 100.
    /// </summary>
    void MaxOut(IReadOnlyList<StatsEnumerated> stats, bool includeModSkills)
    {
        if (_character.IsNone) return;

        var world = player.Session.World;
        world.Commands.Enqueue(new DebugSetStats([_character], stats, MaxedOut) { Tick = world.Tick });
        if (includeModSkills && ModSkills.All.Count > 0)
            world.Commands.Enqueue(new DebugSetModSkills([_character], [.. ModSkills.All.Select(s => s.Id)], MaxedOut) { Tick = world.Tick });

        _sliding.Clear();
        ApplyWhilePaused();
    }

    /// <summary>
    /// Copy values: keeps the character being edited's stats and code mods' skills, by their own values, for <see cref="PasteValues"/>. They live in
    /// this window only: closing it forgets them, and nothing is saved.
    /// </summary>
    void CopyValues()
    {
        if (Stats() is not { } picked) return;

        _copied = (picked.ToArray(), ModSkills.Levels(picked));
    }

    /// <summary>
    /// Paste values: sets every stat and code mod's skill of the character being edited to the copied ones (the stats the window edits; a copied
    /// value over a stat's maximum is kept as copied).
    /// </summary>
    void PasteValues()
    {
        if (_copied is not { } copied || _character.IsNone) return;

        var stats = new Dictionary<StatsEnumerated, float>();
        foreach (var stat in _stats.Where(s => s.ModSkill is null)) stats[stat.Stat] = copied.Stats[(int)stat.Stat];
        var skills = new Dictionary<string, float>(StringComparer.Ordinal);
        for (int i = 0; i < ModSkills.All.Count && i < copied.ModSkills.Length; i++) skills[ModSkills.All[i].Id] = copied.ModSkills[i];

        var world = player.Session.World;
        world.Commands.Enqueue(new DebugSetStatValues([_character], stats, skills) { Tick = world.Tick });
        _sliding.Clear();
        ApplyWhilePaused();
    }

    /// <summary>
    /// Runs one simulation tick while the game is paused (as it is under this window), so the command just queued takes effect and the window shows it.
    /// </summary>
    void ApplyWhilePaused()
    {
        if (player.Session.Simulation.IsPaused) player.Session.Step(1);
    }

    /// <summary>
    /// Closes the window and opens the character editor on the character being edited, as a recruit's opens; the window opens again when the editor
    /// closes.
    /// </summary>
    void OpenAppearance()
    {
        if (_character.IsNone) return;

        if (!player.CharacterEditor.OpenEditor(EditKind.Recruit, [_character]))
        {
            player.Notice("The character editor did not open", true);
            return;
        }

        _returnFromEditor = true;
        player.FaithfulScreens.Close(DebugCharactersLayout.Screen);
    }

    /// <summary>
    /// Every stat with storage that is not an attribute.
    /// </summary>
    static StatsEnumerated[] SkillStats() =>
    [
        .. Enumerable.Range(1, StatsInfo.Count - 1).Select(i => (StatsEnumerated)i).Where(s => StatsInfo.StorageOf(s) == s && !Attributes.Contains(s)),
    ];

    #endregion

    #region Reading

    /// <summary>
    /// The picked character's stats in the world; null when none is picked or it has none.
    /// </summary>
    CharacterStats? Stats()
    {
        if (_character.IsNone) return null;

        var table = player.Session.World.Characters;
        return table.TryResolveNext(_character, out int slot) ? table.Cold(slot)?.Stats : null;
    }

    /// <summary>
    /// The stored level of <paramref name="stat"/> in <paramref name="stats"/> (its trained value, without any modifier).
    /// </summary>
    static float LevelOf(EditableStat stat, CharacterStats stats) =>
        stat.ModSkill is { } id ? ModSkills.All.FirstOrDefault(s => s.Id == id)?.Level(stats) ?? 0 : stats.Stored(stat.Stat);

    /// <summary>
    /// A character's name; empty when the snapshot does not have it.
    /// </summary>
    string NameOf(CharacterId id) => player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.Name ?? "";

    #endregion
}
