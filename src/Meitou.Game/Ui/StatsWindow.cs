using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Simulation.Bodies;

namespace Meitou.Game.Ui;

/// <summary>
/// The character's stats window (docs/game/ui-screens.md): the skills by category with their levels and the next level's progress.
/// </summary>
sealed class StatsWindow(PlayerInterface player)
{
    /// <summary>
    /// The stats' help texts (the catalog's lines).
    /// </summary>
    internal StatHelpCatalog StatHelp = new();

    /// <summary>
    /// The stat whose description shows, by its number as text (none at first).
    /// </summary>
    internal string SelectedStat = "";

    /// <summary>
    /// Whether the window was open at the last <see cref="PollStatHover"/>.
    /// </summary>
    bool _statsOpen;

    /// <summary>
    /// The category titles are the catalog's own words in dashes ("-Weapons-"): the layout has no text for them (docs/native-ui.md). Each column's
    /// headings: the row they come before and the caption.
    /// </summary>
    static readonly (int Before, string Caption)[][] StatHeadings =
    [
        [], [(0, "Weapons")], [(0, "Combat"), (4, "Ranged")],
        [(0, "Thievery"), (4, "Athletics")], [(0, "Sciences"), (4, "Trades")],
    ];

    /// <summary>
    /// The attributes and four skill columns, ordered as observed in the supplied original-game screenshot.
    /// </summary>
    internal static readonly StatsEnumerated[][] StatPanels =
    [
        [StatsEnumerated.Strength, StatsEnumerated.Toughness, StatsEnumerated.Dexterity, StatsEnumerated.Perception],
        [
            StatsEnumerated.Katanas, StatsEnumerated.Sabres, StatsEnumerated.Hackers, StatsEnumerated.HeavyWeapons, StatsEnumerated.Blunt,
            StatsEnumerated.Polearms,
        ],
        [StatsEnumerated.MeleeAttack, StatsEnumerated.MeleeDefence, StatsEnumerated.Dodge, StatsEnumerated.MartialArts,
            StatsEnumerated.Turrets, StatsEnumerated.Crossbows, StatsEnumerated.FriendlyFire],
        [StatsEnumerated.Stealth, StatsEnumerated.Lockpicking, StatsEnumerated.Thieving, StatsEnumerated.Assassination, StatsEnumerated.Athletics,
            StatsEnumerated.Swimming],
        [StatsEnumerated.Medic, StatsEnumerated.Engineering, StatsEnumerated.Robotics, StatsEnumerated.Science,
            StatsEnumerated.SmithingWeapon, StatsEnumerated.SmithingArmour, StatsEnumerated.SmithingBow, StatsEnumerated.Labouring,
            StatsEnumerated.Farming,
            StatsEnumerated.Cooking],
    ];

    /// <summary>
    /// The rows a skill column fits at full row height (the column is 296 layout pixels high, rows 20), and the most it takes with code mods'
    /// skills, whose rows then share its height.
    /// </summary>
    const int ColumnLines = 14, MaxColumnLines = 18;

    /// <summary>
    /// The prefix of a code mod's skill in <see cref="SelectedStat"/> and its rows' actions, before its index in <see cref="ModSkills.All"/>.
    /// </summary>
    const string ModSkillPrefix = "mod:";

    /// <summary>
    /// The stats window's geometry: 980 x 650 at the inventory scale, the attributes and skills titles placed, the derived stats hidden.
    /// </summary>
    internal void BindStatsGeometry(MyGuiLayout layout, UiBindings bindings)
    {
        bindings.TemplateScale = player.UiScale;
        bindings.Positions["Root"] = new(0, 0, 980 * player.UiScale, 650 * player.UiScale);
        bindings.Hidden.Add("lbDerivedStats");

        // The installed layout includes an unused derived-statistics column. The runtime screen
        // gives that space to skills and description (observed in the original-game reference).
        foreach (var widget in layout.Widgets)
        {
            if (widget.Children.Any(c => c.Name == "StatisticsPanel")) bindings.HiddenWidgets.Add(widget);
            if (widget.Children.Any(c => c.Name == "Skills1Panel"))
                bindings.RelativePositions[widget] = new(0.272f, 0.0409836f, 0.723f, 0.508197f);
            if (widget.Children.Any(c => c.Name == "Description1Panel"))
                bindings.RelativePositions[widget] = new(0.0039557f, 0.555738f, 0.991f, 0.427869f);
            if (widget.Children.Any(c => c.Name == "AttributesPanel"))
                bindings.RelativePositions[widget] = new(0.00791139f, 0.0409836f, 0.25f, 0.295082f);
        }

        if (layout.Find("lbAttributes") is { } attributes)
            bindings.RelativePositions[attributes] = new(0.00791139f, 0.0163934f, 0.25f, 0.0327869f);
        if (layout.Find("lbSkills") is { } skills)
            bindings.RelativePositions[skills] = new(0.272f, 0.0163934f, 0.723f, 0.0327869f);
    }

    /// <summary>
    /// How many more code mods' skills <paramref name="column"/> takes besides <paramref name="pending"/> unregistered ones: the column's lines up
    /// to <see cref="MaxColumnLines"/>.
    /// </summary>
    internal static int ModRoom(SkillColumn column, int pending) =>
        MaxColumnLines - VanillaLines((int)column) - ModSkills.All.Count(s => s.Column == column) - pending;

    /// <summary>
    /// Once a frame: the description shows the stat whose row is under the pointer and keeps the last one once the pointer leaves the rows; the
    /// window opens with none (docs/game/ui-screens.md).
    /// </summary>
    internal void PollStatHover()
    {
        bool open = player.FaithfulScreens.IsOpen("stats");
        if (open && !_statsOpen) SelectedStat = "";
        _statsOpen = open;

        if (open && player.FaithfulScreens.HitTest(player.Mouse) is { Screen: "stats", Control.Action: var action }
            && action.StartsWith("stat:", StringComparison.Ordinal))
        {
            SelectedStat = action[5..];
        }
    }

    /// <summary>
    /// The stat rows of the first selected character: each column's headings, labels and values (pointing at a row or clicking it shows the
    /// stat's description).
    /// </summary>
    internal void AddStatsContent(List<UiControl> controls)
    {
        var lead = player.Selected().FirstOrDefault();

        // The skill columns sit in Kenshi_GenericTextBoxSkin frames (the attributes column's frame has no text colour and keeps its grey).
        string statInk = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_GenericTextBoxSkin");
        for (int column = 0; column < StatPanels.Length; column++)
        {
            string name = column == 0 ? "AttributesPanel" : $"Skills{column}Panel";
            if (controls.FirstOrDefault(c => c.Name == name) is not { } panel) continue;
            new StatColumn(controls, panel, name, player.UiScale, RowHeight(column, panel)).Add(column, lead, statInk);
        }

        AddStatDescription(controls);
    }

    /// <summary>
    /// A column's row height: 20 at the UI scale, less when its code mods' skills make more lines than fit.
    /// </summary>
    float RowHeight(int column, UiControl panel)
    {
        float full = 20 * player.UiScale;
        int lines = VanillaLines(column) + ModSkills.All.Count(s => (int)s.Column == column);
        return lines <= ColumnLines ? full : Math.Min(full, panel.Bounds.Height / lines);
    }

    /// <summary>
    /// The lines the game's rows of <paramref name="column"/> take: its stats, the two-line ones once more, and its headings.
    /// </summary>
    static int VanillaLines(int column) => StatPanels[column].Length + StatPanels[column].Count(WrapsLabel) + StatHeadings[column].Length;

    /// <summary>
    /// Whether a stat's label is long enough to take two lines.
    /// </summary>
    static bool WrapsLabel(StatsEnumerated stat) => stat is StatsEnumerated.FriendlyFire or StatsEnumerated.SmithingBow;

    /// <summary>
    /// One column's rows as they are laid down from the panel's top: the game's stats under their headings, then the code mods' skills of the
    /// column.
    /// </summary>
    sealed class StatColumn(List<UiControl> controls, UiControl panel, string name, float scale, float rowHeight)
    {
        /// <summary>
        /// The clip of every row: the panel within its own clip.
        /// </summary>
        readonly UiRect _clip = panel.Bounds.Intersect(panel.Clip ?? panel.Bounds);

        /// <summary>
        /// The next row's top.
        /// </summary>
        float _y = panel.Bounds.Y;

        /// <summary>
        /// Adds the column's rows for <paramref name="lead"/>.
        /// </summary>
        public void Add(int column, Simulation.CharacterSnapshot? lead, string statInk)
        {
            for (int i = 0; i < StatPanels[column].Length; i++)
            {
                foreach (var heading in StatHeadings[column].Where(h => h.Before == i)) AddHeading($"{name}-heading-{i}", heading.Caption);
                AddStat(column, i, lead, statInk);
            }

            for (int i = 0; i < ModSkills.All.Count; i++)
                if ((int)ModSkills.All[i].Column == column) AddModSkill(i, lead, statInk);
        }

        /// <summary>
        /// A category title in dashes.
        /// </summary>
        void AddHeading(string controlName, string caption)
        {
            string title = "-" + Loc.Tr(caption) + "-";
            Text(controlName, title, "Kenshi_StandardFont_Large", PlayerInterface.TitleColour, "Left VCenter", "", heading: true);
            _y += rowHeight;
        }

        /// <summary>
        /// The game's stat <paramref name="i"/> of the column: its label and whole level.
        /// </summary>
        void AddStat(int column, int i, Simulation.CharacterSnapshot? lead, string statInk)
        {
            var stat = StatPanels[column][i];
            string id = ((int)stat).ToString(CultureInfo.InvariantCulture), action = "stat:" + id;
            string label = column == 0 ? StatLabel(stat).ToUpperInvariant() : StatLabel(stat);
            string font = column == 0 ? "Kenshi_PaintedTextFont_Medium" : "Kenshi_StandardFont_Medium";
            string colour = column == 0 ? "0.4 0.380392 0.356863" : statInk;
            bool wrapLabel = WrapsLabel(stat);
            Text($"{name}-label-{i}", label, font, colour, "Left VCenter", action, wrap: wrapLabel);

            // The whole level, truncated like the next-level bar's split (rounding showed 12.5 as 13 with the bar at 50 %, #514).
            float level = MathF.Floor(lead?.AllSkills.ElementAtOrDefault((int)stat) ?? 0);
            Text($"{name}-value-{i}", level.ToString("0", CultureInfo.InvariantCulture), font, colour, "Right VCenter", action);
            _y += column == 0 ? 20 * scale : rowHeight * (wrapLabel ? 2 : 1);
        }

        /// <summary>
        /// Code mod skill <paramref name="index"/> of <see cref="ModSkills.All"/>: its label and whole level, on one line.
        /// </summary>
        void AddModSkill(int index, Simulation.CharacterSnapshot? lead, string statInk)
        {
            string action = "stat:" + ModSkillPrefix + index.ToString(CultureInfo.InvariantCulture);
            string level = MathF.Floor(lead?.ModSkills.ElementAtOrDefault(index) ?? 0).ToString("0", CultureInfo.InvariantCulture);
            Text($"{name}-mod-label-{index}", Loc.Tr(ModSkills.All[index].Label), "Kenshi_StandardFont_Medium", statInk, "Left VCenter", action);
            Text($"{name}-mod-value-{index}", level, "Kenshi_StandardFont_Medium", statInk, "Right VCenter", action);
            _y += rowHeight;
        }

        /// <summary>
        /// One text of the row at the current top: a heading across the panel, a value at its right, else a label beside the value.
        /// </summary>
        void Text(string controlName, string caption, string font, string colour, string align, string action, bool heading = false,
            bool wrap = false)
        {
            var properties = new Dictionary<string, string> { ["FontName"] = font, ["TextColour"] = colour, ["TextAlign"] = align };
            if (wrap) properties["WordWrap"] = "true";

            // Runtime category titles are larger than the surrounding standard text.
            if (heading) properties["FontPixels"] = Math.Max(8, (int)MathF.Round(18 * scale)).ToString(CultureInfo.InvariantCulture);
            bool value = align.StartsWith("Right", StringComparison.Ordinal);
            float valueWidth = 32 * scale;
            controls.Add(new(controlName, action.Length > 0 ? "Button" : "TextBox",
                new(panel.Bounds.X + (heading ? 5 * scale : value ? panel.Bounds.Width - valueWidth : 0), _y - (heading ? 2 * scale : 0),
                    heading ? panel.Bounds.Width - 5 * scale : value ? valueWidth : Math.Max(0, panel.Bounds.Width - valueWidth - 4 * scale),
                    heading ? 24 * scale : rowHeight * (wrap ? 2 : 1)), caption, action, "", true,
                properties,
                Vector4.Zero, Pickable: action.Length > 0, Clip: _clip));
        }
    }

    /// <summary>
    /// The selected stat's description panels: the left one lists what the stat affects (with the lead's numbers) and how it trains, the right
    /// one shows the progress to the next level, the description and the lead's XP multiplier. Nothing when no stat is selected or the panels
    /// are missing.
    /// </summary>
    void AddStatDescription(List<UiControl> controls)
    {
        if (controls.FirstOrDefault(c => c.Name == "Description1Panel") is not { } left
            || controls.FirstOrDefault(c => c.Name == "Description2Panel") is not { } right)
            return;

        var lead = player.Selected().FirstOrDefault();
        if (SelectedModSkill() is { } index)
        {
            AddModSkillDescription(controls, left, right, lead, index);
            return;
        }
        if (!int.TryParse(SelectedStat, out int selected) || selected <= 0 || selected >= StatsInfo.Count) return;

        var stat = (StatsEnumerated)selected;
        var readouts = lead?.Hud?.StatReadouts?.GetValueOrDefault(stat) ?? [];
        float value = lead?.AllSkills.ElementAtOrDefault(selected) ?? 0;
        float multiplier = lead?.SkillXpMultipliers.ElementAtOrDefault(selected) ?? 0;
        AddDescriptionPanels(controls, left, right, StatLabel(stat), StatHelp.For(stat), readouts, value, multiplier);
    }

    /// <summary>
    /// The index in <see cref="ModSkills.All"/> of the selected code mod's skill; null when a game stat (or none) is selected.
    /// </summary>
    int? SelectedModSkill() =>
        SelectedStat.StartsWith(ModSkillPrefix, StringComparison.Ordinal)
        && int.TryParse(SelectedStat.AsSpan(ModSkillPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
        && index < ModSkills.All.Count
            ? index
            : null;

    /// <summary>
    /// A code mod's skill's description panels: its name, the mod's Affects and Training lines, the lead's progress and the mod's description.
    /// </summary>
    void AddModSkillDescription(List<UiControl> controls, UiControl left, UiControl right, Simulation.CharacterSnapshot? lead, int index)
    {
        var skill = ModSkills.All[index];
        var help = new StatHelpCatalog.Help(skill.Description, skill.Affects, skill.Training, "");
        float value = lead?.ModSkills.ElementAtOrDefault(index) ?? 0;
        AddDescriptionPanels(controls, left, right, Loc.Tr(skill.Label), help, [], value, 0);
    }

    /// <summary>
    /// Both description panels for a stat titled <paramref name="title"/> at <paramref name="value"/>.
    /// </summary>
    void AddDescriptionPanels(List<UiControl> controls, UiControl left, UiControl right, string title, StatHelpCatalog.Help help,
        IReadOnlyList<Simulation.StatReadout> readouts, float value, float multiplier)
    {
        string statInk = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_GenericTextBoxSkin");
        float scale = player.UiScale;
        float row = Math.Min(20 * scale, (left.Bounds.Height - 4 * scale) / Math.Max(1, 3 + help.Affected.Count + help.Training.Count));
        AddStatHelpRows(controls, left, title, readouts, help, statInk, scale, row);

        var rightClip = right.Bounds.Intersect(right.Clip ?? right.Bounds);
        var bar = AddNextLevelBar(controls, right, value, scale, row, rightClip);
        AddStatDescriptionBody(controls, right, bar, help, multiplier, statInk, scale, row, rightClip);
    }

    /// <summary>
    /// The left description panel: the stat's title, what it affects (each with the lead's number right-aligned when it has one) and how it trains.
    /// </summary>
    void AddStatHelpRows(List<UiControl> controls, UiControl left, string title, IReadOnlyList<Simulation.StatReadout> readouts,
        StatHelpCatalog.Help help, string statInk, float scale, float row)
    {
        float y = left.Bounds.Y + 4 * scale;
        var leftClip = left.Bounds.Intersect(left.Clip ?? left.Bounds);

        void Line(string name, string text, string colour, int pixels = 14)
        {
            DescriptionText(controls, scale, name, new(left.Bounds.X + 19 * scale, y, left.Bounds.Width - 27 * scale, pixels == 18 ? 24 * scale : row),
                text, colour, pixels, leftClip);
            y += row;
        }

        Line("stat-description-title", title, PlayerInterface.TitleColour, 18);
        Line("stat-affected-heading", StatHelp.TextAt(681), PlayerInterface.MainColour);

        // The rows' numbers (FUN_1408b1250, HudView.StatReadouts), matched to the catalog's rows by their label, right-aligned on the row.
        for (int i = 0; i < help.Affected.Count; i++)
        {
            var readout = readouts.FirstOrDefault(r => Loc.Tr(r.Label) == help.Affected[i]);
            string ink = readout.Main ? PlayerInterface.MainColour : statInk;
            var valueBox = new UiRect(left.Bounds.X + 19 * scale, y, left.Bounds.Width - 27 * scale, row);
            if (readout.Value is { Length: > 0 } number)
                DescriptionText(controls, scale, $"stat-affected-value-{i}", valueBox, number, ink, 14, leftClip, "Right Top");
            Line($"stat-affected-{i}", "-" + help.Affected[i].TrimEnd(':'), ink);
        }

        Line("stat-training-heading", StatHelp.TextAt(684), PlayerInterface.MainColour);
        for (int i = 0; i < help.Training.Count; i++)
            Line($"stat-training-{i}", "-" + help.Training[i], statInk);
    }

    /// <summary>
    /// The right panel's next-level bar (track, fill by the fraction of <paramref name="value"/> past its whole level, frame) with its label and
    /// percent; returns the bar's rectangle.
    /// </summary>
    UiRect AddNextLevelBar(List<UiControl> controls, UiControl right, float value, float scale, float row, UiRect rightClip)
    {
        float progress = float.IsFinite(value) ? value - MathF.Floor(value) : 0;
        var bar = new UiRect(right.Bounds.X + 17 * scale, right.Bounds.Y + 8 * scale, Math.Max(0, right.Bounds.Width - 30 * scale), 12 * scale);
        controls.Add(new("stat-next-level-track", "Widget", bar, "", "", "Kenshi_ProgressBarEmptySkin", true,
            new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: rightClip));
        if (progress > 0)
        {
            controls.Add(new("stat-next-level-fill", "Widget", bar with { Width = bar.Width * progress }, "", "", "Kenshi_ProgressBarFilledSkin", true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: rightClip));
        }

        controls.Add(new("stat-next-level-frame", "Widget", bar, "", "", "Kenshi_ProgressBarSkin", true,
            new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: rightClip));
        var barText = new UiRect(bar.X + 10 * scale, bar.Y - 4 * scale, bar.Width - 20 * scale, row);
        DescriptionText(controls, scale, "stat-next-level-label", barText, StatHelp.TextAt(220), PlayerInterface.MainColour, 14, rightClip);
        string percent = ((int)(progress * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        DescriptionText(controls, scale, "stat-next-level-percent", barText, percent, PlayerInterface.MainColour, 14, rightClip, "Right Top");
        return bar;
    }

    /// <summary>
    /// The right panel's description under the bar: the catalog text (its {1}/{2} slots dropped), the physical effect, and the XP multiplier as a
    /// percent when it is not 1.
    /// </summary>
    void AddStatDescriptionBody(List<UiControl> controls, UiControl right, UiRect bar, StatHelpCatalog.Help help, float multiplier, string statInk,
        float scale, float row, UiRect rightClip)
    {
        DescriptionText(controls, scale, "stat-description-heading", new(bar.X + 10 * scale, bar.Y + 12 * scale, bar.Width - 20 * scale, row),
            StatHelp.TextAt(440), PlayerInterface.MainColour, 14, rightClip);

        string description = System.Text.RegularExpressions.Regex.Replace(help.Description, @"\{[12]\}", "");
        if (help.PhysicalEffect.Length > 0) description += "\n\n" + help.PhysicalEffect;
        if (multiplier > 0 && Math.Abs(multiplier - 1) > 0.001f)
        {
            description += "\n\n" + StatHelp.TextAt(451) + " " + ((multiplier - 1) * 100).ToString("+0;-0;0", CultureInfo.InvariantCulture) + "%";
        }

        var body = new UiRect(bar.X + 10 * scale, bar.Y + 28 * scale, bar.Width - 20 * scale, Math.Max(0, right.Bounds.Height - 36 * scale));
        DescriptionText(controls, scale, "stat-description-body", body, description, statInk, 14, rightClip, wrap: true);
    }

    /// <summary>
    /// One unpickable text box of the description panels; <paramref name="pixels"/> is the font size at 1080p, scaled by
    /// <paramref name="scale"/> (8 at least).
    /// </summary>
    static void DescriptionText(List<UiControl> controls, float scale, string name, UiRect bounds, string caption, string colour, int pixels,
        UiRect clip, string align = "Left Top", bool wrap = false)
    {
        controls.Add(new(name, "TextBox", bounds, caption, "", "", true,
            new Dictionary<string, string>
            {
                ["FontPixels"] = Math.Max(8, (int)MathF.Round(pixels * scale)).ToString(CultureInfo.InvariantCulture),
                ["TextColour"] = colour,
                ["TextAlign"] = align,
                ["WordWrap"] = wrap ? "true" : "false"
            },
            Vector4.Zero, Pickable: false, Clip: clip));
    }

    /// <summary>
    /// A stat's label, translated.
    /// </summary>
    internal static string StatLabel(StatsEnumerated stat) => Loc.Tr(StatLabelEnglish(stat));

    /// <summary>
    /// A stat's English label (the catalog's words where they differ from the enum's).
    /// </summary>
    static string StatLabelEnglish(StatsEnumerated stat) => stat switch
    {
        StatsEnumerated.Thieving => "Thievery",
        StatsEnumerated.HeavyWeapons => "Heavy weapons",
        StatsEnumerated.MeleeAttack => "Melee attack",
        StatsEnumerated.MeleeDefence => "Melee defence",
        StatsEnumerated.SmithingWeapon => "Weapon smith",
        StatsEnumerated.SmithingArmour => "Armour smith",
        StatsEnumerated.SmithingBow => "Crossbow smith",
        StatsEnumerated.FriendlyFire => "Precision shooting",
        StatsEnumerated.MartialArts => "Martial arts",
        StatsEnumerated.Medic => "Field medic",
        StatsEnumerated.Engineering => "Engineer",
        _ => stat.ToString(),
    };
}