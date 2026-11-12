using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// A data panel's progress line (<c>DataPanelLine_Progress</c>, <c>FUN_1406f8990</c> / <c>FUN_1406fb1b0</c>, docs/game/ui-screens.md "State lines"): a
/// <c>Kenshi_ProgressBarFill</c> bar across the row with its text centred over it, filled as MyGUI's ProgressBar with TrackFill (as
/// <see cref="Host.ResourceLoadBar"/> draws it).
/// </summary>
static class ProgressLine
{
    /// <summary>
    /// The bar's widget name, which owns the template's parts.
    /// </summary>
    const string BarName = "ProgressLine";

    /// <summary>
    /// The expanded bar and its track skin, made from <see cref="s_laidOutFor"/>; other resources lay it out again.
    /// </summary>
    static MyGuiLayout? s_layout;
    /// <summary>
    /// The track skin of <c>Kenshi_ProgressBarFill</c>'s root.
    /// </summary>
    static string? s_trackSkin;
    /// <summary>
    /// The resources <see cref="s_layout"/> was made from.
    /// </summary>
    static MyGuiResources? s_laidOutFor;

    /// <summary>
    /// Adds the bar's parts at <paramref name="at"/> (pixels on a <paramref name="width"/> × <paramref name="height"/> screen) filled by
    /// <paramref name="fraction"/>, named with <paramref name="prefix"/>, not pickable and clipped to <paramref name="clip"/>.
    /// </summary>
    public static void Add(List<UiControl> controls, MyGuiResources resources, int width, int height, UiRect at, float fraction, string prefix,
        UiRect? clip)
    {
        if (Layout(resources) is not { Roots: [var root, ..] } layout || width <= 0 || height <= 0) return;

        var b = new UiBindings();
        b.RelativePositions[root] = new(at.X / width, at.Y / height, at.Width / width, at.Height / height);
        var parts = new List<UiControl>();
        foreach (var u in MyGuiPresentation.Arrange(layout, width, height, b, resources.Layers))
            parts.Add(u with { Name = prefix + u.Name, Pickable = false, Clip = clip });

        // TrackFill: the track runs from the start of TrackPlace to the position, drawn under the frame that follows it.
        int place = parts.FindIndex(IsTrackPlace);
        if (place >= 0 && s_trackSkin is not null && fraction > 0)
        {
            var bounds = parts[place].Bounds;
            var fill = bounds with { Width = MathF.Round(bounds.Width * Math.Clamp(fraction, 0, 1)) };
            parts.Insert(place + 1, new UiControl(prefix + "track", "Widget", fill, "", "", s_trackSkin, true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false, Clip: clip));
        }

        controls.AddRange(parts);
    }

    /// <summary>
    /// The bar expanded for <paramref name="resources"/>, made once per resources.
    /// </summary>
    static MyGuiLayout? Layout(MyGuiResources resources)
    {
        if (ReferenceEquals(resources, s_laidOutFor)) return s_layout;

        s_laidOutFor = resources;
        string xml = $"""<MyGUI type="Layout"><Widget type="ProgressBar" skin="Kenshi_ProgressBarFill" position_real="0 0 1 1" name="{BarName}"/></MyGUI>""";
        s_layout = MyGuiTemplates.Expand(MyGuiReader.ParseLayout(xml), resources);
        s_trackSkin = resources.Templates.TryGetValue("Kenshi_ProgressBarFill", out var template)
            ? MyGuiReader.ReadResourceLayout(template).Roots.FirstOrDefault()?.UserStrings.GetValueOrDefault("TrackSkin")
            : null;
        return s_layout;
    }

    /// <summary>
    /// Whether <paramref name="u"/> is the bar's <c>TrackPlace</c>, where the fill starts.
    /// </summary>
    static bool IsTrackPlace(UiControl u) =>
        u.Properties.GetValueOrDefault("TemplateOwner") == BarName && u.Properties.GetValueOrDefault("TemplatePart") == "TrackPlace";
}
