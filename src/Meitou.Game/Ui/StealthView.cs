using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using Meitou.Simulation.Player;

namespace Meitou.Game.Ui;

/// <summary>
/// The stealth indicators of a sneaking player character (docs/game/perception.md "Spotting a sneaker"): a 3D arrow towards each hostile NPC noticing
/// (yellow) or detecting (red) it, and a tag over its head tinted by its state, the original's name-tag colours (hidden blue, almost seen yellow,
/// seen red).
/// </summary>
sealed class StealthView(PlayerInterface player)
{
    #region Stealth arrows

    // The original's arrow colours (FUN_1408586d0).
    static readonly Vector3 ArrowNoticed = new(1, 0xdd / 255f, 0);
    static readonly Vector3 ArrowDetected = new(0xdd / 255f, 0, 0);

    readonly List<MarkerRenderer.Arrow> _arrows = [];

    /// <summary>
    /// Hands the world renderer this frame's stealth arrows (the original's <c>FUN_1408586d0</c>): per watcher of a sneaking player character an
    /// <c>arrow.mesh</c> pointing from it at the looker, its tail 12 up and min(d - 7, 15) out towards the looker (d the 3D distance; negative puts it
    /// on the far side, as in the original).
    /// </summary>
    internal void UpdateStealthArrows()
    {
        if (player.Markers is not { } markers) return;

        _arrows.Clear();
        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            if (c.Stealth is null) continue;

            var feet = player.Where(c);
            foreach (var w in c.Watchers)
            {
                var d = w.Position - feet;
                float dist = d.Length();
                if (dist < 1e-3f) continue;

                d /= dist;
                var colour = w.Level == StealthExposure.Detected ? ArrowDetected : ArrowNoticed;
                _arrows.Add(new(feet + new Vector3(0, 12, 0) + d * MathF.Min(dist - 7, 15), d, colour));
            }
        }

        markers.Arrows = _arrows.Count == 0 ? [] : [.. _arrows];
    }

    #endregion

    #region Name tags

    // The original's name-tag fallback colours (NameTagStealthHidden / AlmostSeen / Seen).
    static readonly Vector4 TagHidden = new(0.25f, 0.521f, 0.745f, 1);
    static readonly Vector4 TagAlmostSeen = new(0.745f, 0.721f, 0.25f, 1);
    static readonly Vector4 TagSeen = new(0.745f, 0.25f, 0.29f, 1);

    /// <summary>
    /// The original's name-tag range: 1.2e8 squared (about 10954 units) from the camera.
    /// </summary>
    const float NameTagRangeSquared = 1.2e8f;

    /// <summary>
    /// The frames of the <c>LightLevel</c> icon in <c>Kenshi_CharacterNameTags</c> (Verified, kenshi_images.xml).
    /// </summary>
    const int LightLevelFrames = 7;

    /// <summary>
    /// A drawn head this far above the feet or higher is upright (standing 17.4, on a chair 13.1); from <see cref="UprightHead"/> down to
    /// <see cref="LyingHead"/> the tag moves over to the head (crouching 9.7, sitting on the ground 6.8, in a bed 7.4; heights of the clips' <c>Bip01 Head</c>,
    /// measured), so a sneaker's tag does not jump.
    /// </summary>
    const float UprightHead = 12, LyingHead = 8;

    /// <summary>
    /// Whether the world leaves a character undrawn (above the viewed floor, or inside a closed building): its tag shows only while it is selected.
    /// </summary>
    public Func<CharacterId, bool>? HiddenInWorld { get; set; }

    /// <summary>
    /// Where a character's head was last drawn in the world (its <c>Bip01 Head</c>); null when it was not drawn.
    /// </summary>
    public Func<CharacterId, Vector3?>? DrawnHead { get; set; }

    /// <summary>
    /// The name tag over each of the player's characters within the original's range: the <c>Kenshi_CharacterNamePanel</c> layout (<b>Verified</b>),
    /// 0.1057 x 0.0296 of the screen, the name centred in <c>Kenshi_StandardFont_Large</c> with a shadow in the middle 0.655, and while the character
    /// sneaks the
    /// <c>Stealth</c> icon on the left in the state's tag colour. The name is white and in square brackets when the character is selected, grey and bare
    /// otherwise (<c>NameTagSelected</c> / <c>NameTagUnselected</c>, the brackets from a screenshot of the original; <b>Observed</b>);
    /// while it sneaks, also the <c>LightLevel</c> icon on the right from its light level. The panel stands on the head like a speech bubble does (the
    /// original's anchor is not mapped).
    /// </summary>
    internal void AddNameTags(List<UiControl> controls)
    {
        float width = player.Width * 0.105729f, height = player.Height * 0.0296296f, scale = player.Height / 1080f;
        int n = 0;
        foreach (var c in player.Session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer || c.Name.Length == 0 || player.CharacterEditor.EditorOpen || c.CarriedBy != CharacterId.None) continue;
            if (!c.Selected && HiddenInWorld?.Invoke(c.Id) == true) continue;

            var feet = player.Where(c);
            if (Vector3.DistanceSquared(player.Eye, feet) >= NameTagRangeSquared) continue;
            if (player.Project(TagAnchor(c, feet)) is not { } head) continue;

            float x = head.X - width / 2;
            float y = head.Y - 4 * scale - height;
            if (player.ShowNames) AddNameText(controls, c, n, new UiRect(x + width * 0.187192f, y, width * 0.655172f, height));

            // Without the name the panel is the two icons side by side (FUN_1406e2fd0 sizes it to them), centred over the head.
            float stealthX = player.ShowNames ? x : head.X - width * (0.187192f + 0.157635f) / 2;
            float lightX = player.ShowNames ? x + width * 0.842365f : stealthX + width * 0.187192f;
            if (c.Stealth is { } state)
            {
                var stealthBox = new UiRect(stealthX, y + height * 0.09375f, width * 0.187192f, height * 0.84375f);
                var lightBox = new UiRect(lightX, y, width * 0.157635f, height);
                AddSneakIcons(controls, c, state, n, stealthBox, lightBox);
            }

            n++;
        }
    }

    /// <summary>
    /// The name of a player character in its name tag's text box.
    /// </summary>
    void AddNameText(List<UiControl> controls, CharacterSnapshot c, int n, UiRect box)
    {
        string caption = c.Selected ? $"[{c.Name}]" : c.Name;
        var style = new Dictionary<string, string>
        {
            ["FontName"] = "Kenshi_StandardFont_Large",
            ["TextAlign"] = "Center",
            ["TextShadow"] = "true",
            ["TextColour"] = c.Selected ? "1 1 1" : "0.702 0.702 0.702",
        };
        controls.Add(new($"nametag-{n}", "TextBox", box, caption, "", "", true, style, Vector4.Zero, Pickable: false));
    }

    /// <summary>
    /// The sneaking character's <c>Stealth</c> icon in its state's colour, in <paramref name="stealthBox"/>, and its <c>LightLevel</c> icon, in
    /// <paramref name="lightBox"/>.
    /// </summary>
    void AddSneakIcons(List<UiControl> controls, CharacterSnapshot c, StealthExposure state, int n, UiRect stealthBox, UiRect lightBox)
    {
        var tint = state switch { StealthExposure.Detected => TagSeen, StealthExposure.Noticed => TagAlmostSeen, _ => TagHidden };
        controls.Add(new($"nametag-stealth-{n}", "ImageBox", stealthBox, "", "", "", true,
            new Dictionary<string, string>
            {
                ["ImageResource"] = "Kenshi_CharacterNameTags",
                ["ImageGroup"] = "Stealth",
                ["ImageName"] = "Stealth",
                ["Colour"] = FormattableString.Invariant($"{tint.X} {tint.Y} {tint.Z} 1"),
            }, default, Pickable: false));

        // The LightLevel icon on the right in the layout's colour: frame round((1 - light) x 6) of its 7, the sun at full light (FUN_1406e8100).
        int frame = (int)MathF.Round((1 - Math.Clamp(c.Light, 0, 1)) * (LightLevelFrames - 1));
        controls.Add(new($"nametag-light-{n}", "ImageBox", lightBox, "", "", "", true,
            new Dictionary<string, string>
            {
                ["ImageResource"] = "Kenshi_CharacterNameTags",
                ["ImageGroup"] = "LightLevel",
                ["ImageName"] = "LightLevel",
                ["ImageFrame"] = frame.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Colour"] = "0.94152 0.805549 0.34137 1",
            }, default, Pickable: false));
    }

    /// <summary>
    /// The point a name tag, task bar or speech bubble stands on (Meitou's: the original's anchor is not mapped): over a standing character's height,
    /// or just over the drawn head of one lying down, whose body its clip may lay away from its feet (a sleeper's from the bed's use node onto the
    /// mattress).
    /// </summary>
    internal Vector3 TagAnchor(CharacterSnapshot c, Vector3 feet)
    {
        var standing = feet + new Vector3(0, PlayerInterface.CharacterHeight + 4, 0);
        if (DrawnHead?.Invoke(c.Id) is not { } head) return standing;

        float lying = Math.Clamp((UprightHead - (head.Y - feet.Y)) / (UprightHead - LyingHead), 0, 1);
        return Vector3.Lerp(standing, head + new Vector3(0, 4, 0), lying);
    }

    #endregion

    #region Task bars

    internal MyGuiLayout? TaskBarLayout;

    string? _taskBarTrack;

    /// <summary>
    /// The floating progress bar over a player character at work (<c>FloatingProgressBar::ProgressBarWidget</c>, the <c>Kenshi_ProgressBarPanel</c> layout,
    /// <b>Verified</b>): the bar's fill and the centred caption of <see cref="CharacterSnapshot.Bar"/>. It stands just above the name tag (the original's anchor
    /// is not mapped).
    /// </summary>
    internal void AddTaskBars(List<UiControl> controls)
    {
        if (TaskBarLayout is not { Roots: [var root, ..] } || player.CharacterEditor.EditorOpen) return;

        float tag = player.Height * 0.0296296f;
        float scale = player.Height / 1080f;
        int n = 0;
        var all = player.Session.CurrentSnapshot.Characters;
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c.Bar is not { } bar || c.CarriedBy != CharacterId.None) continue;

            var feet = player.Where(c);
            if (Vector3.DistanceSquared(player.Eye, feet) >= NameTagRangeSquared) continue;
            if (player.Project(TagAnchor(c, feet)) is not { } head) continue;

            float x = head.X / player.Width - root.Position.Width / 2;
            float y = (head.Y - 8 * scale - tag) / player.Height - root.Position.Height;
            AddTaskBar(controls, TaskBarLayout, root, root.Position with { X = x, Y = y }, bar, $"taskbar-{n++}-");
        }
    }

    /// <summary>
    /// One character's bar at <paramref name="at"/>: the layout's controls renamed with <paramref name="prefix"/>, not pickable, and its fill.
    /// </summary>
    void AddTaskBar(List<UiControl> controls, MyGuiLayout layout, MyGuiWidget root, UiRect at, TaskBar bar, string prefix)
    {
        var b = new UiBindings();
        b.RelativePositions[root] = at;
        b.Text["Text"] = bar.Text;
        var arranged = MyGuiPresentation.Arrange(layout, player.Width, player.Height, b, player.UiResources.Layers);
        var parts = new List<UiControl>(arranged.Count + 1);
        foreach (var u in arranged) parts.Add(u with { Name = u.Name.Length > 0 ? prefix + u.Name : u.Name, Pickable = false });

        // The fill is the skin's TrackSkin from the start of TrackPlace (MyGUI ProgressBar, as ResourceLoadBar draws it), under the frame that follows it.
        int place = parts.FindIndex(u => u.Properties.GetValueOrDefault("TemplateOwner") == "ProgressBar"
            && u.Properties.GetValueOrDefault("TemplatePart") == "TrackPlace");
        if (place >= 0 && TaskBarTrack() is { } track)
        {
            var bounds = parts[place].Bounds;
            var fill = bounds with { Width = MathF.Round(bounds.Width * Math.Clamp(bar.Progress, 0, 1)) };
            parts.Insert(place + 1, new UiControl(prefix + "track", "Widget", fill, "", "", track, true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        }

        controls.AddRange(parts);
    }

    string? TaskBarTrack()
    {
        if (_taskBarTrack is not null) return _taskBarTrack;
        if (!player.UiResources.Templates.TryGetValue("Kenshi_ProgressBarFill", out var template)) return null;

        _taskBarTrack = MyGuiReader.ReadResourceLayout(template).Roots.FirstOrDefault()?.UserStrings.GetValueOrDefault("TrackSkin");
        return _taskBarTrack;
    }

    #endregion
}