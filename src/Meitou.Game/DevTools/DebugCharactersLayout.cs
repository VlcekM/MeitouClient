using System.Globalization;
using System.Text;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Game.DevTools;

/// <summary>
/// A stat the Edit characters window edits: a game stat, or a code mod's skill when <paramref name="ModSkill"/> is its id. <paramref name="Max"/> is
/// the highest value the number box takes (the slider always runs 0 to 100).
/// </summary>
readonly record struct EditableStat(string Label, StatsEnumerated Stat, string? ModSkill, int Max);

/// <summary>
/// A titled group of the stats the window edits (the stats window's categories).
/// </summary>
readonly record struct StatSection(string Title, IReadOnlyList<EditableStat> Stats);

/// <summary>
/// A squad of the window's list: its name and the player's characters in it, in the squad's order.
/// </summary>
readonly record struct RosterSquad(string Name, IReadOnlyList<CharacterId> Members);

/// <summary>
/// A row of a scroll view of the Edit characters window, stacked at its height in layout pixels: a heading (<paramref name="Index"/> its squad or
/// section) or a row (<paramref name="Index"/> its running character or stat number, <paramref name="Part"/> its squad or section). A gap has no
/// name. A row of <paramref name="Columns"/> columns shares its line with the others of the line: it is cell <paramref name="Column"/> of the line,
/// which starts at the row of column 0.
/// </summary>
readonly record struct WindowRow(string Name, float Height, bool Heading, int Index, int Part = -1, int Column = 0, int Columns = 1);

/// <summary>
/// The Edit characters window's layout: a window with the characters by squad on the left and the stats of the picked one on the right, each in a
/// scroll view, with the buttons under them. The rows follow the roster (squads and characters) and the stat sections, so the layout is made again
/// when either changes (<see cref="DebugCharactersWindow"/>).
/// </summary>
static class DebugCharactersLayout
{
    #region Names

    /// <summary>
    /// The screen's id.
    /// </summary>
    internal const string Screen = "debug-characters";

    /// <summary>
    /// The window's root widget (its caption).
    /// </summary>
    internal const string Root = "Root";

    /// <summary>
    /// The scroll view of the squads and their characters, and the one of the stat rows.
    /// </summary>
    internal const string CharacterView = "CharacterView", StatView = "StatView";

    /// <summary>
    /// The text shown while no character is picked. The picked character's name is the window's caption (<see cref="Caption"/>).
    /// </summary>
    internal const string PickText = "PickText";

    /// <summary>
    /// The buttons: the appearance editor, max out the attributes, max out the skills, and copy and paste the values.
    /// </summary>
    internal const string AppearanceButton = "AppearanceButton", MaxAttributesButton = "MaxAttributesButton",
        MaxSkillsButton = "MaxSkillsButton", CopyButton = "CopyButton", PasteButton = "PasteButton";

    /// <summary>
    /// The widget names of a squad heading, a character button, a section heading and a stat row's parts; each is followed by its index.
    /// </summary>
    internal const string SquadHeadName = "SquadHead", CharacterButtonName = "Character", SectionHeadName = "SectionHead",
        StatLabelName = "StatLabel", StatBarName = "StatBar", StatValueName = "StatValue", StatRowName = "StatRow";

    /// <summary>
    /// The parts of a section heading's set-all control, each followed by the section's index: the label and the value's box (which applies its value
    /// when it loses the keyboard focus, or on Enter).
    /// </summary>
    internal const string SectionSetLabelName = "SectionSetLabel", SectionSetValueName = "SectionSetValue";

    /// <summary>
    /// The action a value box raises on Enter, before it lets go of the keys: the stats' and the sections' (the window applies the value).
    /// </summary>
    internal const string StatSubmit = "stat-set", SectionSubmit = "section-set";

    #endregion

    #region Sizes

    /// <summary>
    /// The window's size in layout pixels (at a 1080 line screen); it shrinks to a smaller viewport.
    /// </summary>
    internal const float WindowWidth = 1320, WindowHeight = 860;

    /// <summary>
    /// The highest value an attribute's and a skill's number box takes; the sliders run 0 to <see cref="SliderMax"/>.
    /// </summary>
    internal const int AttributeMax = 1000, SkillMax = 100, SliderMax = 100;

    /// <summary>
    /// The row heights in layout pixels: a heading, a character, a stat, and the gap after a squad's characters.
    /// </summary>
    const float HeadingHeight = 38, CharacterHeight = 60, StatHeight = 46, SquadGap = 10, SectionGap = 16;

    /// <summary>
    /// How many stats of a section share a line.
    /// </summary>
    const int StatColumns = 2;

    /// <summary>
    /// The light ink of text on the window's dark surface (the original's name boxes use it).
    /// </summary>
    const string LightInk = "0.894737 0.860072 0.811019";

    /// <summary>
    /// The titles of the stat sections: the stats window's panels.
    /// </summary>
    static readonly string[] SectionTitles = ["Attributes", "Weapons", "Combat", "Thievery", "Sciences"];

    #endregion

    #region Sections

    /// <summary>
    /// The stat sections: the stats window's panels with the code mods' skills at the foot of their column.
    /// </summary>
    internal static IReadOnlyList<StatSection> Sections()
    {
        var sections = new List<StatSection>();
        for (int column = 0; column < StatsWindow.StatPanels.Length; column++)
        {
            int max = column == 0 ? AttributeMax : SkillMax;
            var stats = StatsWindow.StatPanels[column].Select(s => new EditableStat(StatsWindow.StatLabel(s), s, null, max)).ToList();
            foreach (var skill in ModSkills.All.Where(s => (int)s.Column == column))
                stats.Add(new(Loc.Tr(skill.Label), StatsEnumerated.None, skill.Id, SkillMax));

            sections.Add(new(SectionTitles[column], stats));
        }

        return sections;
    }

    #endregion

    #region Layout

    /// <summary>
    /// The layout and its rows, made for a roster and the stat sections.
    /// </summary>
    internal sealed record Built(MyGuiLayout Layout, IReadOnlyList<WindowRow> Characters, IReadOnlyList<WindowRow> Stats);

    /// <summary>
    /// Makes the layout for <paramref name="roster"/> and <paramref name="sections"/>.
    /// </summary>
    internal static Built Build(IReadOnlyList<RosterSquad> roster, IReadOnlyList<StatSection> sections)
    {
        var characterRows = new List<WindowRow>();
        var statRows = new List<WindowRow>();
        string characters = CharacterRows(roster, characterRows);
        string stats = StatRows(sections, statRows);

        var layout = MyGuiReader.ParseLayout($"""
            <MyGUI type="Layout">
              <Widget type="Window" skin="Kenshi_WindowCX" position_real="0 0 0.62 0.8" layer="Window" name="{Root}">
                <Property key="Caption" value="{Caption}"/>
                {ScrollView(CharacterView, "0.012 0.015 0.22 0.87", characters)}
                {ScrollView(StatView, "0.25 0.015 0.738 0.87", stats)}
                <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.25 0.4 0.738 0.1" name="{PickText}">
                  <Property key="TextAlign" value="Center"/><Property key="TextColour" value="{LightInk}"/>
                </Widget>
                {Button(AppearanceButton, "0.25 0.905 0.18 0.08")}
                {Button(MaxAttributesButton, "0.46 0.905 0.14 0.08")}
                {Button(MaxSkillsButton, "0.61 0.905 0.13 0.08")}
                {Button(CopyButton, "0.77 0.905 0.105 0.08")}
                {Button(PasteButton, "0.885 0.905 0.103 0.08")}
              </Widget>
            </MyGUI>
            """);

        return new(layout, characterRows, statRows);
    }

    /// <summary>
    /// The window's title; the picked character's name is added to it (<see cref="CaptionFor"/>).
    /// </summary>
    internal const string Caption = "EDIT CHARACTERS";

    /// <summary>
    /// The title with <paramref name="name"/> after it, or the title alone for no name.
    /// </summary>
    internal static string CaptionFor(string name) => name.Length > 0 ? $"{Caption} - {name}" : Caption;

    /// <summary>
    /// A scroll view of <paramref name="name"/> at <paramref name="position"/> (fractions of the window's client) holding <paramref name="rows"/>:
    /// vertical bar only, the rows from the top.
    /// </summary>
    static string ScrollView(string name, string position, string rows) => $"""
        <Widget type="ScrollView" skin="Kenshi_ScrollViewEmptyLight" position_real="{position}" name="{name}">
          <Property key="VisibleHScroll" value="false"/><Property key="VisibleVScroll" value="true"/><Property key="CanvasAlign" value="Left Top"/>
          {rows}
        </Widget>
        """;

    /// <summary>
    /// A button of the game's own skin.
    /// </summary>
    static string Button(string name, string position) => $"""<Widget type="Button" skin="Kenshi_Button1" position_real="{position}" name="{name}"/>""";

    /// <summary>
    /// A heading: a flat bar across the row (the flat panel the character editor's slider names sit on) holding the title in large light ink, so it
    /// reads apart from the names of the rows under it, indented by <paramref name="indent"/> (a fraction of the bar's width; the character list's
    /// bar is narrower than the stats'). The title's widget is <see cref="HeadingText"/> of <paramref name="name"/>.
    /// </summary>
    static string Heading(string name, string indent) => $"""
        <Widget type="Widget" skin="Kenshi_GenericTextBoxFlatSkin" position_real="0 0 1 0.1" name="{name}">
          <Property key="NeedMouse" value="false"/>
          <Widget type="TextBox" skin="Kenshi_TextboxStandardText_Large" position_real="{indent} 0 0.9 1" name="{HeadingText(name)}">
            <Property key="TextAlign" value="Left VCenter"/><Property key="TextColour" value="{LightInk}"/>
          </Widget>
        </Widget>
        """;

    /// <summary>
    /// A stat section's heading: the title's bar across the whole row, the title in its left half and the set-all control (a label and the value's
    /// box) at its right end, so every stat of the section can be set at once.
    /// </summary>
    static string SectionHeading(int section) => $"""
        <Widget type="Widget" skin="PanelEmpty" position_real="0 0 1 0.1" name="{SectionHeadName}{section}">
          <Widget type="Widget" skin="Kenshi_GenericTextBoxFlatSkin" position_real="0 0 1 1">
            <Property key="NeedMouse" value="false"/>
          </Widget>
          <Widget type="TextBox" skin="Kenshi_TextboxStandardText_Large" position_real="0.015 0 0.45 1" name="{HeadingText(SectionHeadName + section)}">
            <Property key="TextAlign" value="Left VCenter"/><Property key="TextColour" value="{LightInk}"/>
          </Widget>
          <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.55 0 0.28 1" name="{SectionSetLabelName}{section}">
            <Property key="TextAlign" value="Right VCenter"/><Property key="TextColour" value="{LightInk}"/>
          </Widget>
          <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.85 0.12 0.12 0.76" name="{SectionSetValueName}{section}">
            <Property key="MaxTextLength" value="4"/><Property key="TextAlign" value="Center"/>
            <Property key="SubmitAction" value="{SectionSubmit}"/>
          </Widget>
        </Widget>
        """;

    /// <summary>
    /// The title's widget of the heading <paramref name="heading"/>.
    /// </summary>
    internal static string HeadingText(string heading) => heading + "Text";

    #endregion

    #region Character rows

    /// <summary>
    /// The characters' rows, a heading and a button per character for each squad; fills <paramref name="rows"/> and returns their widgets.
    /// </summary>
    static string CharacterRows(IReadOnlyList<RosterSquad> roster, List<WindowRow> rows)
    {
        var xml = new StringBuilder();
        int character = 0;
        for (int squad = 0; squad < roster.Count; squad++)
        {
            string head = SquadHeadName + squad.ToString(CultureInfo.InvariantCulture);
            xml.AppendLine(Heading(head, "0.06"));
            rows.Add(new(head, HeadingHeight, true, squad));

            for (int member = 0; member < roster[squad].Members.Count; member++, character++)
            {
                string name = CharacterButtonName + character.ToString(CultureInfo.InvariantCulture);
                xml.AppendLine($"""<Widget type="Button" skin="Kenshi_Button1" position_real="0 0 1 0.1" name="{name}"/>""");
                rows.Add(new(name, CharacterHeight, false, character, squad));
            }

            rows.Add(new("", SquadGap, true, -1));
        }

        return xml.ToString();
    }

    #endregion

    #region Stat rows

    /// <summary>
    /// Where the divider between a stat cell and the next one on its line sits in the cell, as a fraction of its width (in the margin the row's parts
    /// leave at the right).
    /// </summary>
    internal const float DividerAt = 0.96f;

    /// <summary>
    /// The stats' rows, a heading per section and a row per stat, <see cref="StatColumns"/> to a line: its name, the 0 to 100 slider (the character
    /// editor's <c>Kenshi_ScrollBar</c>, with a wheel that scrolls the list instead of moving it) and an edit box for the number. Fills
    /// <paramref name="rows"/> and returns their widgets.
    /// </summary>
    static string StatRows(IReadOnlyList<StatSection> sections, List<WindowRow> rows)
    {
        var xml = new StringBuilder();
        int stat = 0;
        for (int section = 0; section < sections.Count; section++)
        {
            string head = SectionHeadName + section.ToString(CultureInfo.InvariantCulture);
            xml.AppendLine(SectionHeading(section));
            rows.Add(new(head, HeadingHeight, true, section));

            int cell = 0;
            foreach (var entry in sections[section].Stats)
            {
                xml.AppendLine(StatRow(stat, entry.Max));
                string name = StatRowName + stat.ToString(CultureInfo.InvariantCulture);
                rows.Add(new(name, StatHeight, false, stat, section, cell++ % StatColumns, StatColumns));
                stat++;
            }

            rows.Add(new("", SectionGap, true, -1));
        }

        return xml.ToString();
    }

    /// <summary>
    /// One stat row, <paramref name="n"/> its running index: the name, the slider and the number box, each a share of the row's width (the row is
    /// sized when the rows are stacked, and its parts follow), between a margin at the left and a wider one at the right, where the window draws the
    /// divider from the next cell on the line (<see cref="DividerAt"/>).
    /// </summary>
    static string StatRow(int n, int max)
    {
        string i = n.ToString(CultureInfo.InvariantCulture);
        string digits = max.ToString(CultureInfo.InvariantCulture).Length.ToString(CultureInfo.InvariantCulture);
        return $"""
            <Widget type="Widget" skin="PanelEmpty" position_real="0 0 1 0.1" name="{StatRowName}{i}">
              <Widget type="TextBox" skin="Kenshi_TextboxStandardText" position_real="0.04 0.08 0.27 0.84" name="{StatLabelName}{i}">
                <Property key="TextAlign" value="Left VCenter"/><Property key="TextColour" value="{LightInk}"/>
              </Widget>
              <Widget type="ScrollBar" skin="Kenshi_ScrollBar" position_real="0.32 0 0.4 1" name="{StatBarName}{i}">
                <Property key="Range" value="{SliderMax + 1}"/><Property key="MoveToClick" value="true"/><Property key="WheelPage" value="0"/>
              </Widget>
              <Widget type="EditBox" skin="Kenshi_EditBox" position_real="0.74 0.08 0.18 0.84" name="{StatValueName}{i}">
                <Property key="MaxTextLength" value="{digits}"/><Property key="TextAlign" value="Center"/>
                <Property key="SubmitAction" value="{StatSubmit}"/>
              </Widget>
            </Widget>
            """;
    }

    #endregion
}
