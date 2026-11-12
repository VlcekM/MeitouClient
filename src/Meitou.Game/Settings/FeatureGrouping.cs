using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Settings;

/// <summary>
/// The Graphics tabs as features (docs/native-ui.md "Settings"): each feature is one header row (its switch, or a title when it has none) with its
/// own settings folded under it, closed until opened. Settings in no group stay rows of their own.
/// </summary>
sealed class FeatureGrouping(SettingsMenu menu)
{
    #region Features

    /// <summary>
    /// A feature: the setting heading it (its switch or slider, by label) or a <see cref="Title"/> when nothing heads it, and its members' labels.
    /// <see cref="LauncherHeader"/>: the launcher's heading where it differs (the technique choice, which the game's menu shows as the effect's
    /// switch and a technique row).
    /// </summary>
    internal sealed record FeatureGroup(string Id, string? Header, string[] Members, string? Title = null, string Hint = "", string? LauncherHeader = null);

    /// <summary>
    /// The group of the race switches (every <c>race:</c> setting is in it).
    /// </summary>
    const string SkinGroup = "Skin";

    /// <summary>
    /// The features, in the Graphics tabs' order: each group's id, heading setting, members, title and hint.
    /// </summary>
    internal static readonly FeatureGroup[] FeatureGroups =
    [
        // Display
        new("Upscaling", WorldFrame.UpscalerSliders[0], [WorldFrame.UpscalerSliders[1], WorldFrame.UpscalerSliders[2], WorldFrame.DlssPresetSlider]),
        new("HDR output", "hdr", ["hdrPaperNits", "hdrPeakNits"]),

        // Shadows
        new("Shadows", WorldFrame.SunShadowsChoice, [SettingsMenu.TechniqueKey(WorldFrame.SunShadowsChoice), WorldFrame.TracedGrassSwitch,
            WorldFrame.ShadowDistanceSlider, WorldFrame.ShadowMapSlider, WorldFrame.ShadowFilterSlider,
            WorldFrame.FifthCascadeSwitch]),
        new("Contact shadows", "Contact shadows", []),
        new("Lamp shadows", "Lamp shadows", []),
        new("Soft grazing sun", "Soft grazing sun", []),

        // Lighting
        new("Indirect light", "Indirect light", [SettingsMenu.TechniqueKey(WorldFrame.GiChoice), "Indoor GI", WorldFrame.GiRelocateSwitch,
            WorldFrame.GiCloudShadowsSwitch, WorldFrame.GiCacheSwitch], LauncherHeader: WorldFrame.GiChoice),
        new("Ambient occlusion", "Ambient occlusion", [SettingsMenu.TechniqueKey(WorldFrame.OcclusionChoice), WorldFrame.GtaoQualitySlider],
            LauncherHeader: WorldFrame.OcclusionChoice),
        new("Light shafts", "Light shafts", [
            "Light shafts strength", "Light shafts air (0 = none)", "Light shafts air at dawn and dusk (x)", WorldFrame.ShaftQualitySlider]),
        new("Planetshine", "Planetshine", ["Planetshine strength (x physical)"]),
        new("Ambient light", null, ["Ambient light strength (1 = game)", "Night ambient light strength (1 = 1.2x game)"],
            "Ambient light", "The sky's light in shade, at night and indoors."),

        // Water & reflections
        new("Water", "Water", ["Water clarity x (Meitou)", "Far water clarity (Meitou)", "Water see-through distance (Meitou, game 4000)",
            WorldFrame.WaterReflectionSlider, WorldFrame.ReflectionRangeSlider, WorldFrame.ReflectionSamplesSlider, WorldFrame.WaterGridSlider,
            WorldFrame.WaterOceanSlider]),
        new("Water contact foam, wakes and ripples", "Water contact foam, wakes and ripples", []),
        new("Reflections", "Reflections", []),

        // Terrain
        new("Terrain detail", null, ["Terrain detail: error px (less = finer)", WorldFrame.MaterialDistanceSlider], "Terrain detail",
            "How finely the ground's shape and its textures are drawn with distance."),
        new("Terrain relief", null, ["Ground parallax", WorldFrame.GroundParallaxDepthSlider, "Ground parallax distance (m)", "Ground displacement (tessellation)",
            "Ground displacement distance (m)", "Ground relief depth (cm, typical ground)", WorldFrame.GroundReliefQualitySlider,
            "Rock smoothing (tessellation)", "Terrain outline refinement (tessellation)"],
            "Terrain relief", "Depth on the ground and smoother rocks and hills, from tessellation and parallax."),
        new("Ground trails", "Ground trails", []),

        // Vegetation
        new("Foliage ranges", "Foliage ranges", ["Foliage draw distance x", "Foliage LOD distance x (lod Meitou)", "Large foliage range (F6 Meitou)",
            "Medium foliage range", "Small foliage range", "Grass draw distance x", WorldFrame.GrassDensitySlider, "Foliage levels"]),
        new("Far impostors", "Far impostors", ["Impostor distance (F7 Meitou)", "Large impostor distance (F7 Meitou)", WorldFrame.ImpostorBudgetSlider]),
        new(WorldFrame.GrassCoverSwitch, WorldFrame.GrassCoverSwitch, []),

        // Objects
        new("Draw distances", "Draw distances", [WorldFrame.ObjectDistanceSlider, "Landmark distance (Meitou)", "Distant towns (zones)",
            "Object LOD distance x"]),
        new("Mesh welding", "Mesh welding", []),
        new("Metal on buildings", "Metal on buildings", []),

        // Characters: the skin switch, then one row per race (added by Entries)
        new(SkinGroup, "Skin", []),
        new("Advanced materials", "Advanced materials", [WorldFrame.MaterialHairSwitch, WorldFrame.MaterialClothSwitch, WorldFrame.MaterialSkinSwitch,
            WorldFrame.MaterialEyesSwitch, WorldFrame.MaterialMetalSwitch, WorldFrame.MaterialBeadsSwitch]),
        new("Blood on weapons and gear", "Blood on weapons and gear", []),
        new("Blood cells", "Blood cells", []),
        new("Blood on surfaces", "Blood on surfaces", []),
        new("Bolt tracers", "Bolt tracers", []),

        // Sky & weather
        new("Haze", "Haze", ["Haze strength (1 = game)", WorldFrame.HazeDistanceSlider]),
        new("Clouds", "Clouds", [WorldFrame.CloudHeightSlider, "Cloud shadows", "Cloud shadows in fog"]),
        new("Weather particles", WorldFrame.ParticleSliders[0], [WorldFrame.ParticleSliders[1]]),
        new("Fog", "Fog", []),
        new(WorldFrame.WetWorldSwitch, WorldFrame.WetWorldSwitch, []),

        // Colour
        new("Tone map and grade", "Tone map and grade", ["Tone map (0 = game)", "Hybrid tone map: ACES share", "Grading (0 = game)"]),
        new("Night grading", "Night grading", ["Night grading strength (0 = game)"]),

        // Post FX
        new(WorldFrame.BloomSwitch, WorldFrame.BloomSwitch, [WorldFrame.BloomStrengthSlider]),
        new(WorldFrame.DepthOfFieldSwitch, WorldFrame.DepthOfFieldSwitch, [WorldFrame.DofApertureSlider, WorldFrame.CameraEffectsSwitch]),
        new("Heat haze", "Heat haze", ["Heat haze strength (1 = game)"]),
    ];

    /// <summary>
    /// The features among a launcher section's <paramref name="keys"/> (docs/launcher.md "Graphics"), by the keys their settings are saved under.
    /// </summary>
    internal static List<global::Meitou.Settings.SettingsGroup> LauncherGroups(IReadOnlyCollection<string> keys)
    {
        string? Key(string label) =>
            keys.Contains(label)
                ? label
                : keys.Contains(SettingsMenu.SwitchKeyPrefix + label)
                    ? SettingsMenu.SwitchKeyPrefix + label
                    : null;

        return [.. FeatureGroups
            .Select(g => new global::Meitou.Settings.SettingsGroup
            {
                Header = (g.LauncherHeader ?? g.Header) is { } header ? Key(header) : null,
                Title = g.Title ?? g.Id,
                Description = g.Hint,
                Members = [.. g.Members.Select(Key).OfType<string>()],
            })
            .Where(g => g.Members.Count > 0)];
    }

    /// <summary>
    /// Where a Graphics row stands on its tab, in both menus: its feature's place in <see cref="FeatureGroups"/>, the rows of no feature after every
    /// feature (within each, the catalogue's order). <paramref name="label"/> without the switch key's prefix.
    /// </summary>
    internal static int GroupRank(string label)
    {
        int i = Array.FindIndex(FeatureGroups, g => g.Header == label || g.LauncherHeader == label || InGroup(g, label));
        return i < 0 ? FeatureGroups.Length : i;
    }

    /// <summary>
    /// Whether setting <paramref name="raw"/> is a member of <paramref name="g"/> (a race switch of the Skin group too).
    /// </summary>
    static bool InGroup(FeatureGroup g, string raw) =>
        g.Members.Contains(raw) || g.Id == SkinGroup && raw.StartsWith("race:", StringComparison.Ordinal);

    #endregion

    #region Open state

    /// <summary>
    /// The groups the player opened (this session).
    /// </summary>
    internal readonly HashSet<string> OpenGroups = [];

    /// <summary>
    /// Opens or closes group <paramref name="id"/> and drops the keyboard focus.
    /// </summary>
    internal void ToggleGroup(string id)
    {
        if (!OpenGroups.Remove(id)) OpenGroups.Add(id);
        menu.Focus = -1;
    }

    /// <summary>
    /// Opens groups <paramref name="ids"/> (the settings showcase).
    /// </summary>
    internal void ShowcaseOpen(params string[] ids)
    {
        foreach (var id in ids)
        {
            OpenGroups.Add(id);
        }
    }

    #endregion

    #region Rows

    /// <summary>
    /// A tab's rows as features: each group's header where its first row was, its members under it while it is open; the rest as they were.
    /// </summary>
    internal List<SettingsMenu.Entry> Grouped(List<SettingsMenu.Entry> rows)
    {
        var output = new List<SettingsMenu.Entry>();
        var done = new HashSet<string>();
        foreach (var row in rows)
        {
            var group = row.Raw is not { } raw ? null : FeatureGroups.FirstOrDefault(g => g.Header == raw || InGroup(g, raw));
            if (group is null)
            {
                output.Add(row);
                continue;
            }

            if (!done.Add(group.Id)) continue;

            // In the group's own order (the races after it), named without the feature's name they repeat ("Light shafts strength" is "Strength").
            var members = rows.Where(r => r.Raw is { } m && InGroup(group, m))
                .OrderBy(r => Array.IndexOf(group.Members, r.Raw) is var i and >= 0 ? i : int.MaxValue).ToList();
            var header = rows.FirstOrDefault(r => r.Raw == group.Header) ?? new SettingsMenu.Entry(group.Title ?? group.Id, group.Hint);
            bool open = OpenGroups.Contains(group.Id);
            var resets = new[] { header }.Concat(members).Where(m => m.Changed).Select(m => m.Reset).OfType<Action>().ToList();
            output.Add(header with
            {
                Open = members.Count > 0 ? open : null,
                GroupId = group.Id,
                Changed = header.Changed || members.Any(m => m.Changed),
                Reset = resets.Count > 0 ? () => { foreach (var reset in resets) reset(); }
                : null,
            });

            string name = header.Label;
            // A member that sits under another already (a mode's custom value) goes one step further in.
            if (open) output.AddRange(members.Select(m => m with { Depth = m.Depth + 1, Label = WithinGroup(m.Label, name) }));
        }

        return output;
    }

    /// <summary>
    /// A member's <paramref name="label"/> without the feature's name it starts with ("Light shafts strength" in Light shafts is "Strength").
    /// </summary>
    static string WithinGroup(string label, string group) =>
        label.Length > group.Length + 1 && label.StartsWith(group + " ", StringComparison.OrdinalIgnoreCase)
            ? char.ToUpperInvariant(label[group.Length + 1]) + label[(group.Length + 2)..]
            : label;

    #endregion
}
