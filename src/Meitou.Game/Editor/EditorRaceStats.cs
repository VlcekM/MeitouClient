using System.Globalization;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace Meitou.Game.Editor;

/// <summary>
/// The character editor's RACE STATS panel (<see cref="CharacterEditorPanel"/>): the shown character's hit points and its race's stat
/// multipliers, scrolled when they overflow.
/// </summary>
sealed class EditorRaceStats(PlayerInterface player, CharacterEditorPanel panel)
{
    /// <summary>
    /// The scroll state's key of the RACE STATS list: the editor's screen (the scrollbar's arrows, track and wheel act on the screen they are in).
    /// </summary>
    const string EditorStatsScroll = "editor";

    /// <summary>
    /// The race the list was last shown for; a new one starts at the list's top.
    /// </summary>
    string? _editorStatsRace;

    /// <summary>
    /// Each race's stat lines (<see cref="RaceStatLines"/>), by its string id.
    /// </summary>
    readonly Dictionary<string, IReadOnlyList<(string, string, bool)>> _raceStatCache = [];

    /// <summary>
    /// Each race's new person's hit points (<see cref="RaceHitPoints"/>), by its string id.
    /// </summary>
    readonly Dictionary<string, float?> _raceHitPointCache = [];

    /// <summary>
    /// The RACE STATS panel (<see cref="RaceStatLines"/>): a DatapanelGUI in <c>StatsPanelText</c> (docs/game/ui-screens.md "Text panels"), a row of
    /// 19.2 pixels at 1080 lines per line (Observed: 25.55 at 1440 in the base game), the label from 3 % of the canvas width and the value
    /// right-aligned to 96 %; lines past the panel's height scroll, with the vertical bar shown only then (the panel's ScrollView,
    /// <c>FUN_1406fd4e0</c>).
    /// </summary>
    internal void AddRaceStats(List<UiControl> controls, GameRecord race, UiControl host)
    {
        var area = host.Bounds;
        var lines = new List<(string Label, string Value, bool? Good)>();
        // The first line is the character's hit points: its first body part's maximum (FUN_1405ee0c0 reads the medical state's first part, Observed),
        // as an integer. While the draft shows another race than the character has, that race's new medical state's (the character takes it on
        // CONFIRM).
        float? hitPoints = null;
        if (panel.Editor is { } s && player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == s.Current) is { } shown)
            hitPoints = shown.Appearance?.Race.StringId == race.StringId && shown.HealthParts is [var first, ..]
                ? first.Maximum : RaceHitPoints(race);
        if (hitPoints is { } hp) lines.Add((Loc.Tr("Hit Points:"), ((int)(hp + 0.01f)).ToString(CultureInfo.InvariantCulture), null));
        lines.AddRange(RaceStatLines(race).Select(l => (l.Label, l.Value, (bool?)l.Good)));
        float scale = player.UiScale, row = 19.2f * scale;
        // A new race's list starts at its top (the original fills the panel anew).
        if (_editorStatsRace != race.StringId)
        {
            player.ScrollBars.InventoryScroll.Remove(EditorStatsScroll);
            _editorStatsRace = race.StringId;
        }
        float maximum = Math.Max(0, lines.Count * row - area.Height);
        if (maximum < 0.01f) maximum = 0;
        float scroll = Math.Clamp(player.ScrollBars.InventoryScroll.GetValueOrDefault(EditorStatsScroll), 0, maximum);
        // The panel's ScrollView (Kenshi_ScrollViewEmpty: its bars 20 pixels): the lines are laid out on a canvas the bar's width narrower than the
        // panel; with the bar (overflow) the canvas fills the client, without it the canvas sits 13 pixels in from the panel's left and top (Observed
        // at 1440 lines, a Hive Prince's and a skeleton's stats against the base game's: the same 355-pixel rows, 13 pixels right and down without
        // the bar; the text 6 pixels above the panel's top).
        const float barWidth = 20, edgeInset = 13, textLift = 6;
        float bar = maximum > 0 ? barWidth : 0, width = area.Width - barWidth, inset = maximum > 0 ? 0 : edgeInset;
        var clip = area.Intersect(host.Clip ?? area);
        for (int i = 0; i < lines.Count; i++)
        {
            var (label, value, good) = lines[i];
            var bounds = new UiRect(area.X + inset + width * 0.03f, area.Y + inset - textLift + i * row - scroll, width * 0.93f, row);
            controls.Add(new($"editor-stat-{i}", "TextBox", bounds, label, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium",
                    ["TextColour"] = PlayerInterface.MainColour, ["TextAlign"] = "Left Top" },
                Vector4.Zero, Pickable: false, Clip: clip));
            controls.Add(new($"editor-stat-value-{i}", "TextBox", bounds, value, "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string>
                {
                    ["FontName"] = "Kenshi_StandardFont_Medium",
                    ["TextAlign"] = "Right Top",
                    ["TextColour"] = good is null ? PlayerInterface.MainColour
                        : good.Value ? PlayerInterface.GoodColour
                        : PlayerInterface.BadBrightColour
                }, Vector4.Zero, Pickable: false, Clip: clip));
        }
        if (maximum <= 0)
        {
            player.ScrollBars.InventoryViews.Remove(EditorStatsScroll);
            player.ScrollBars.InventoryTracks.Remove(EditorStatsScroll);
            return;
        }
        player.ScrollBars.InventoryScroll[EditorStatsScroll] = scroll;
        player.ScrollBars.InventoryViews[EditorStatsScroll] = (area, maximum);
        player.ScrollBars.AddScrollBar(EditorStatsScroll, controls, new(area.X + width, area.Y, bar, area.Height), area.Height,
            lines.Count * row, scroll, maximum, host.Clip);
    }

    /// <summary>
    /// A stat's name as the editor's race stats give it (<c>FUN_140888440</c>): its own table, not the stats window's (Medic is "First Aid",
    /// Labouring, Weapon Smithing); the vet and hive medic are "Vet", mass combat, survival and polearms have no name.
    /// </summary>
    internal static string EditorStatName(StatsEnumerated stat) => Loc.Tr(stat switch
    {
        StatsEnumerated.Strength => "Strength",
        StatsEnumerated.MeleeAttack => "Attack",
        StatsEnumerated.Labouring => "Labouring",
        StatsEnumerated.Science => "Science",
        StatsEnumerated.Engineering => "Engineering",
        StatsEnumerated.Robotics => "Robotics",
        StatsEnumerated.SmithingWeapon => "Weapon Smithing",
        StatsEnumerated.SmithingArmour => "Armour Smithing",
        StatsEnumerated.Medic => "First Aid",
        StatsEnumerated.Thieving => "Thievery",
        StatsEnumerated.Turrets => "Turrets",
        StatsEnumerated.Farming => "Farming",
        StatsEnumerated.Cooking => "Cooking",
        StatsEnumerated.HiveMedic or StatsEnumerated.Vet => "Vet",
        StatsEnumerated.Stealth => "Stealth",
        StatsEnumerated.Athletics => "Athletics",
        StatsEnumerated.Dexterity => "Dexterity",
        StatsEnumerated.MeleeDefence => "Defence",
        StatsEnumerated.Weapons => "Weaponry",
        StatsEnumerated.Toughness => "Toughness",
        StatsEnumerated.Assassination => "Assassination",
        StatsEnumerated.Swimming => "Swimming",
        StatsEnumerated.Perception => "Perception",
        StatsEnumerated.Katanas => "Katanas",
        StatsEnumerated.Sabres => "Sabres",
        StatsEnumerated.Hackers => "Hackers",
        StatsEnumerated.HeavyWeapons => "Heavy weapons",
        StatsEnumerated.Blunt => "Blunt",
        StatsEnumerated.MartialArts => "Martial arts",
        StatsEnumerated.Dodge => "Dodging",
        StatsEnumerated.Crossbows => "Crossbows",
        StatsEnumerated.FriendlyFire => "Precision Shooting",
        StatsEnumerated.Lockpicking => "Lockpicking",
        StatsEnumerated.SmithingBow => "Crossbow Smithing",
        _ => "",
    });

    /// <summary>
    /// A healthy new person of <paramref name="race"/>: its first medical part's maximum (<see
    /// cref="Meitou.Simulation.Bodies.MedicalState.Create"/>); null without anatomy.
    /// </summary>
    float? RaceHitPoints(GameRecord race)
    {
        if (_raceHitPointCache.TryGetValue(race.StringId, out var cached)) return cached;
        if (panel.EditorData is not { } data) return null;
        var parts = Simulation.Bodies.MedicalState.Create(RaceData.From(race, data.Db), 1).Parts;
        return _raceHitPointCache[race.StringId] = parts.Count > 0 ? parts[0].MaxHp : null;
    }

    /// <summary>
    /// The race's stat lines: label, value and whether it is good.
    /// </summary>
    IReadOnlyList<(string Label, string Value, bool Good)> RaceStatLines(GameRecord race)
    {
        if (_raceStatCache.TryGetValue(race.StringId, out var cached)) return cached;
        var lines = new List<(string, string, bool)>();
        if (panel.EditorData is { } data)
        {
            // As the original's "Character_Editor_Race_Stats" table (FUN_1405ee0c0): "No bleeding" below a bleed rate of 0.1, then the stat
            // multipliers above 1, then those below 1 (RACE stats good / bad, strength, dexterity: RaceData.StatMultipliers), each as the stat's name
            // and (m - 1) x 100 %, in the order the original's hash map iterates (RaceData.StatMapOrder).
            var r = RaceData.From(race, data.Db);
            if (race.GetFloat("bleed rate", 1) < 0.1f) lines.Add((Loc.Tr("No bleeding"), "", true));
            foreach (bool good in new[] { true, false })
                foreach (var stat in RaceData.StatMapOrder)
                {
                    int i = (int)stat;
                    if (i < 1 || i >= r.StatMultipliers.Length) continue;
                    float m = r.StatMultipliers[i];
                    if (Math.Abs(m - 1) < 0.001f || m <= 0 || m > 1 != good) continue;
                    lines.Add((EditorStatName((StatsEnumerated)i), ((m - 1) * 100).ToString("+0;-0", CultureInfo.InvariantCulture) + "%", good));
                }
        }
        return _raceStatCache[race.StringId] = lines;
    }
}
