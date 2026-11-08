using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using Meitou.Simulation.Player;

namespace Meitou.Game;

/// <summary>
/// The stealth indicators of a sneaking player character (docs/game/perception.md "Spotting a sneaker"): a 3D arrow towards each hostile NPC noticing (yellow) or
/// detecting (red) it, and a tag over its head tinted by its state, the original's name-tag colours (hidden blue, almost seen yellow, seen red).
/// </summary>
sealed partial class PlayerInterface
{
    // The original's arrow colours (FUN_1408586d0) and name-tag fallback colours (NameTagStealthHidden / AlmostSeen / Seen).
    static readonly Vector3 ArrowNoticed = new(1, 0xdd / 255f, 0), ArrowDetected = new(0xdd / 255f, 0, 0);
    static readonly Vector4 TagHidden = new(0.25f, 0.521f, 0.745f, 1), TagAlmostSeen = new(0.745f, 0.721f, 0.25f, 1), TagSeen = new(0.745f, 0.25f, 0.29f, 1);

    /// <summary>The original's name-tag range: 1.2e8 squared (about 10954 units) from the camera.</summary>
    const float NameTagRangeSquared = 1.2e8f;
    /// <summary>The frames of the <c>LightLevel</c> icon in <c>Kenshi_CharacterNameTags</c> (Verified, kenshi_images.xml).</summary>
    const int LightLevelFrames = 7;

    readonly List<MarkerRenderer.Arrow> arrows = [];

    /// <summary>
    /// Hands the world renderer this frame's stealth arrows (the original's <c>FUN_1408586d0</c>): per watcher of a sneaking player character an
    /// <c>arrow.mesh</c> pointing from it at the looker, its tail 12 up and min(d - 7, 15) out towards the looker (d the 3D distance; negative puts it
    /// on the far side, as in the original).
    /// </summary>
    void UpdateStealthArrows()
    {
        if (Markers is not { } markers) return;
        arrows.Clear();
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (c.Stealth is null) continue;
            var feet = where(c);
            foreach (var w in c.Watchers)
            {
                var d = w.Position - feet;
                float dist = d.Length();
                if (dist < 1e-3f) continue;
                d /= dist;
                var colour = w.Level == StealthExposure.Detected ? ArrowDetected : ArrowNoticed;
                arrows.Add(new(feet + new Vector3(0, 12, 0) + d * MathF.Min(dist - 7, 15), d, colour));
            }
        }
        markers.Arrows = arrows.Count == 0 ? [] : [.. arrows];
    }

    /// <summary>
    /// The name tag over each of the player's characters within the original's range: the <c>Kenshi_CharacterNamePanel</c> layout (<b>Verified</b>), 0.1057 x 0.0296 of the
    /// screen, the name centred in <c>Kenshi_StandardFont_Large</c> with a shadow in the middle 0.655, and while the character sneaks the <c>Stealth</c> icon on the left in
    /// the state's tag colour. The name is white and in square brackets when the character is selected, grey and bare otherwise (<c>NameTagSelected</c> / <c>NameTagUnselected</c>, the brackets from a screenshot of the original; <b>Observed</b>);
    /// while it sneaks, also the <c>LightLevel</c> icon on the right from its light level. The panel stands on the head like a speech bubble does (the original's anchor is not mapped).
    /// </summary>
    void AddNameTags(List<UiControl> controls)
    {
        float width = Width * 0.105729f, height = Height * 0.0296296f, scale = Height / 1080f;
        int n = 0;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer || c.Name.Length == 0 || EditorOpen || c.CarriedBy != CharacterId.None || !c.Selected && HiddenInWorld?.Invoke(c.Id) == true) continue;
            var feet = where(c);
            if (Vector3.DistanceSquared(Eye, feet) >= NameTagRangeSquared) continue;
            if (Project(TagAnchor(c, feet)) is not { } head) continue;
            float x = head.X - width / 2, y = head.Y - 4 * scale - height;
            if (ShowNames) controls.Add(new($"nametag-{n}", "TextBox", new(x + width * 0.187192f, y, width * 0.655172f, height), c.Selected ? $"[{c.Name}]" : c.Name, "", "", true,
                new Dictionary<string, string>
                {
                    ["FontName"] = "Kenshi_StandardFont_Large", ["TextAlign"] = "Center", ["TextShadow"] = "true", ["TextColour"] = c.Selected ? "1 1 1" : "0.702 0.702 0.702",
                }, Vector4.Zero, Pickable: false));
            // Without the name the panel is the two icons side by side (FUN_1406e2fd0 sizes it to them), centred over the head.
            float stealthX = ShowNames ? x : head.X - width * (0.187192f + 0.157635f) / 2, lightX = ShowNames ? x + width * 0.842365f : stealthX + width * 0.187192f;
            if (c.Stealth is { } state)
            {
                var tint = state switch { StealthExposure.Detected => TagSeen, StealthExposure.Noticed => TagAlmostSeen, _ => TagHidden };
                controls.Add(new($"nametag-stealth-{n}", "ImageBox", new(stealthX, y + height * 0.09375f, width * 0.187192f, height * 0.84375f), "", "", "", true,
                    new Dictionary<string, string>
                    {
                        ["ImageResource"] = "Kenshi_CharacterNameTags", ["ImageGroup"] = "Stealth", ["ImageName"] = "Stealth",
                        ["Colour"] = FormattableString.Invariant($"{tint.X} {tint.Y} {tint.Z} 1"),
                    }, default, Pickable: false));
                // The LightLevel icon on the right in the layout's colour: frame round((1 - light) x 6) of its 7, the sun at full light (FUN_1406e8100).
                int frame = (int)MathF.Round((1 - Math.Clamp(c.Light, 0, 1)) * (LightLevelFrames - 1));
                controls.Add(new($"nametag-light-{n}", "ImageBox", new(lightX, y, width * 0.157635f, height), "", "", "", true,
                    new Dictionary<string, string>
                    {
                        ["ImageResource"] = "Kenshi_CharacterNameTags", ["ImageGroup"] = "LightLevel", ["ImageName"] = "LightLevel",
                        ["ImageFrame"] = frame.ToString(System.Globalization.CultureInfo.InvariantCulture), ["Colour"] = "0.94152 0.805549 0.34137 1",
                    }, default, Pickable: false));
            }
            n++;
        }
    }

    /// <summary>Whether the world leaves a character undrawn (above the viewed floor, or inside a closed building): its tag shows only while it is selected.</summary>
    public Func<CharacterId, bool>? HiddenInWorld { get; set; }

    /// <summary>Where a character's head was last drawn in the world (its <c>Bip01 Head</c>); null when it was not drawn.</summary>
    public Func<CharacterId, Vector3?>? DrawnHead { get; set; }

    /// <summary>
    /// A drawn head this far above the feet or higher is upright (standing 17.4, on a chair 13.1); from <see cref="UprightHead"/> down to <see cref="LyingHead"/> the tag
    /// moves over to the head (crouching 9.7, sitting on the ground 6.8, in a bed 7.4; heights of the clips' <c>Bip01 Head</c>, measured), so a sneaker's tag does not jump.
    /// </summary>
    const float UprightHead = 12, LyingHead = 8;

    /// <summary>
    /// The point a name tag, task bar or speech bubble stands on (Meitou's: the original's anchor is not mapped): over a standing character's height, or just over the drawn
    /// head of one lying down, whose body its clip may lay away from its feet (a sleeper's from the bed's use node onto the mattress).
    /// </summary>
    Vector3 TagAnchor(CharacterSnapshot c, Vector3 feet)
    {
        var standing = feet + new Vector3(0, CharacterHeight + 4, 0);
        if (DrawnHead?.Invoke(c.Id) is not { } head) return standing;

        float lying = Math.Clamp((UprightHead - (head.Y - feet.Y)) / (UprightHead - LyingHead), 0, 1);
        return Vector3.Lerp(standing, head + new Vector3(0, 4, 0), lying);
    }

    /// <summary>The original's <c>Show names</c> option (<c>FUN_1406e2fd0</c>): off, the name tag keeps only its icons.</summary>
    public bool ShowNames { get; set; } = true;
    /// <summary>The <c>font size2</c> option: points added to every GUI font's default height (applied to the painter once it exists).</summary>
    public float FontSize
    {
        get => fontSize;
        set { fontSize = value; if (uiPainter is not null) uiPainter.FontSizeSetting = value; }
    }
    float fontSize = Meitou.Data.Ui.KenshiFontSize.DefaultSetting;

    MyGuiLayout? taskBarLayout;

    /// <summary>
    /// The floating progress bar over a player character at work (<c>FloatingProgressBar::ProgressBarWidget</c>, the <c>Kenshi_ProgressBarPanel</c> layout, <b>Verified</b>): the
    /// bar's fill and the centred caption of <see cref="CharacterSnapshot.Bar"/>. It stands just above the name tag (the original's anchor is not mapped).
    /// </summary>
    void AddTaskBars(List<UiControl> controls)
    {
        if (taskBarLayout is not { Roots: [var root, ..] } || EditorOpen) return;
        float tag = Height * 0.0296296f, scale = Height / 1080f;
        int n = 0;
        var all = session.CurrentSnapshot.Characters;
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c.Bar is not { } bar || c.CarriedBy != CharacterId.None) continue;
            var feet = where(c);
            if (Vector3.DistanceSquared(Eye, feet) >= NameTagRangeSquared) continue;
            if (Project(TagAnchor(c, feet)) is not { } head) continue;
            float x = head.X / Width - root.Position.Width / 2, y = (head.Y - 8 * scale - tag) / Height - root.Position.Height;
            AddTaskBar(controls, taskBarLayout, root, root.Position with { X = x, Y = y }, bar, $"taskbar-{n++}-");
        }
    }

    /// <summary>One character's bar at <paramref name="at"/>: the layout's controls renamed with <paramref name="prefix"/>, not pickable, and its fill.</summary>
    void AddTaskBar(List<UiControl> controls, MyGuiLayout layout, MyGuiWidget root, UiRect at, TaskBar bar, string prefix)
    {
        var b = new UiBindings();
        b.RelativePositions[root] = at;
        b.Text["Text"] = bar.Text;
        var arranged = MyGuiPresentation.Arrange(layout, Width, Height, b, uiResources.Layers);
        var parts = new List<UiControl>(arranged.Count + 1);
        foreach (var u in arranged) parts.Add(u with { Name = u.Name.Length > 0 ? prefix + u.Name : u.Name, Pickable = false });

        // The fill is the skin's TrackSkin from the start of TrackPlace (MyGUI ProgressBar, as ResourceLoadBar draws it), under the frame that follows it.
        int place = parts.FindIndex(u => u.Properties.GetValueOrDefault("TemplateOwner") == "ProgressBar" && u.Properties.GetValueOrDefault("TemplatePart") == "TrackPlace");
        if (place >= 0 && TaskBarTrack() is { } track)
        {
            var bounds = parts[place].Bounds;
            parts.Insert(place + 1, new UiControl(prefix + "track", "Widget", bounds with { Width = MathF.Round(bounds.Width * Math.Clamp(bar.Progress, 0, 1)) }, "", "", track, true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        }
        controls.AddRange(parts);
    }

    string? taskBarTrack;

    string? TaskBarTrack() => taskBarTrack ??= uiResources.Templates.TryGetValue("Kenshi_ProgressBarFill", out var template)
        ? MyGuiReader.ReadResourceLayout(template).Roots.FirstOrDefault()?.UserStrings.GetValueOrDefault("TrackSkin") : null;
}
