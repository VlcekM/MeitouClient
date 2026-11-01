using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The Graphics tabs as features (docs/native-ui.md "Settings"): each feature is one header row (its switch, or a title when it has none) with its own
/// settings folded under it, closed until opened. Settings in no group stay rows of their own.
/// </summary>
sealed partial class SettingsMenu
{
    /// <summary>A feature: the setting heading it (its switch or slider, by label) or a <see cref="Title"/> when nothing heads it, and its members' labels.</summary>
    sealed record FeatureGroup(string Id, string? Header, string[] Members, string? Title = null, string Hint = "");

    const string SkinGroup = "Skin";

    static readonly FeatureGroup[] FeatureGroups =
    [
        // Lighting
        new("Shadows", "Shadows", [WorldFrame.ShadowDistanceSlider, WorldFrame.ShadowMapSlider, WorldFrame.ShadowFilterSlider,
            WorldFrame.FifthCascadeSwitch]),
        new("Indirect light", "Indirect light", ["Indoor GI"]),
        new("Light shafts", "Light shafts", ["Light shafts strength", "Light shafts air (0 = none)", "Light shafts air at dawn and dusk (x)", WorldFrame.ShaftQualitySlider]),
        new("Planetshine", "Planetshine", ["Planetshine strength (x physical)"]),
        new("Ambient light", null, ["Ambient light strength (1 = game)", "Night ambient light strength (1 = 1.2x game)"],
            "Ambient light", "The sky's light in shade, at night and indoors."),
        // Sky & weather
        new("Haze", "Haze", ["Haze strength (1 = game)"]),
        new("Clouds", "Clouds", ["Cloud shadows", "Cloud shadows in fog"]),
        new("Heat haze", "Heat haze", ["Heat haze strength (1 = game)"]),
        new("Weather particles", WorldFrame.ParticleSliders[0], [WorldFrame.ParticleSliders[1]]),
        new("Fog", "Fog", []),
        // Colour
        new("Tone map and grade", "Tone map and grade", ["Tone map (0 = game)", "Hybrid tone map: ACES share", "Grading (0 = game)"]),
        new("Night grading", "Night grading", ["Night grading strength (0 = game)"]),
        new(WorldFrame.BloomSwitch, WorldFrame.BloomSwitch, [WorldFrame.BloomStrengthSlider]),
        // World
        new("Draw distances", "Draw distances", [WorldFrame.ObjectDistanceSlider, "Landmark distance (Meitou)", "Distant towns (zones)", "Object LOD distance x",
            "Terrain detail: error px (less = finer)"]),
        new("Terrain relief", null, ["Ground parallax", "Ground parallax distance (m)", "Ground displacement (tessellation)", "Ground displacement distance (m)",
            "Ground relief depth (cm, typical ground)", WorldFrame.GroundReliefQualitySlider, "Rock smoothing (tessellation)", "Terrain outline refinement (tessellation)"],
            "Terrain relief", "Depth on the ground and smoother rocks and hills, from tessellation and parallax."),
        new("Foliage ranges", "Foliage ranges", ["Foliage draw distance x", "Foliage LOD distance x (lod Meitou)", "Large foliage range (F6 Meitou)",
            "Medium foliage range", "Small foliage range", "Grass draw distance x", WorldFrame.GrassDensitySlider, "Foliage levels"]),
        new("Far impostors", "Far impostors", ["Impostor distance (F7 Meitou)", "Large impostor distance (F7 Meitou)"]),
        new("Mesh welding", "Mesh welding", []),
        new("Bolt tracers", "Bolt tracers", []),
        new("Water", "Water", ["Water clarity x (Meitou)", "Far water clarity (Meitou)", "Water see-through distance (Meitou, game 4000)",
            WorldFrame.WaterReflectionSlider, WorldFrame.ReflectionRangeSlider]),
        // Characters: the skin switch, then one row per race (added by Entries)
        new(SkinGroup, "Skin", []),
    ];

    /// <summary>The features among a launcher section's <paramref name="keys"/> (docs/launcher.md "Graphics"), by the keys their settings are saved under.</summary>
    internal static List<global::Meitou.Settings.SettingsGroup> LauncherGroups(IReadOnlyCollection<string> keys)
    {
        string? Key(string label) => keys.Contains(label) ? label : keys.Contains(SwitchKeyPrefix + label) ? SwitchKeyPrefix + label : null;
        return [.. FeatureGroups.Select(g => new global::Meitou.Settings.SettingsGroup
            {
                Header = g.Header is { } header ? Key(header) : null, Title = g.Title ?? g.Id, Description = g.Hint,
                Members = [.. g.Members.Select(Key).OfType<string>()],
            })
            .Where(g => g.Members.Count > 0)];
    }

    /// <summary>The groups the player opened (this session).</summary>
    readonly HashSet<string> openGroups = [];

    /// <summary>
    /// Where a Graphics row stands on its tab, in both menus: its feature's place in <see cref="FeatureGroups"/>, the rows of no feature after every feature
    /// (within each, the catalogue's order). <paramref name="label"/> without the switch key's prefix.
    /// </summary>
    internal static int GroupRank(string label)
    {
        int i = Array.FindIndex(FeatureGroups, g => g.Header == label || InGroup(g, label));
        return i < 0 ? FeatureGroups.Length : i;
    }

    static bool InGroup(FeatureGroup g, string raw) => g.Members.Contains(raw) || g.Id == SkinGroup && raw.StartsWith("race:", StringComparison.Ordinal);

    /// <summary>A tab's rows as features: each group's header where its first row was, its members under it while it is open; the rest as they were.</summary>
    List<Entry> Grouped(List<Entry> rows)
    {
        var output = new List<Entry>();
        var done = new HashSet<string>();
        foreach (var row in rows)
        {
            var group = row.Raw is not { } raw ? null : FeatureGroups.FirstOrDefault(g => g.Header == raw || InGroup(g, raw));
            if (group is null) { output.Add(row); continue; }
            if (!done.Add(group.Id)) continue;
            // In the group's own order (the races after it), named without the feature's name they repeat ("Light shafts strength" is "Strength").
            var members = rows.Where(r => r.Raw is { } m && InGroup(group, m))
                .OrderBy(r => Array.IndexOf(group.Members, r.Raw) is var i and >= 0 ? i : int.MaxValue).ToList();
            var header = rows.FirstOrDefault(r => r.Raw == group.Header) ?? new Entry(group.Title ?? group.Id, group.Hint);
            bool open = openGroups.Contains(group.Id);
            var resets = new[] { header }.Concat(members).Where(m => m.Changed).Select(m => m.Reset).OfType<Action>().ToList();
            output.Add(header with
            {
                Open = members.Count > 0 ? open : null, GroupId = group.Id, Changed = header.Changed || members.Any(m => m.Changed),
                Reset = resets.Count > 0 ? () => { foreach (var reset in resets) reset(); } : null,
            });
            string name = header.Label;
            if (open) output.AddRange(members.Select(m => m with { Depth = 1, Label = WithinGroup(m.Label, name) }));
        }
        return output;
    }

    static string WithinGroup(string label, string group) =>
        label.Length > group.Length + 1 && label.StartsWith(group + " ", StringComparison.OrdinalIgnoreCase)
            ? char.ToUpperInvariant(label[group.Length + 1]) + label[(group.Length + 2)..] : label;

    void ToggleGroup(string id) { if (!openGroups.Remove(id)) openGroups.Add(id); focus = -1; }

    internal void ShowcaseOpen(params string[] ids) { foreach (var id in ids) openGroups.Add(id); }
}
