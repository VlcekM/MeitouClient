using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game;

/// <summary>
/// The original's background-loading bar (the resource loader's update, <c>FUN_14044bf40</c>; docs/game/ui-screens.md "Background loading bar"): a
/// <c>Kenshi_ProgressBarFill</c> progress bar at the top right of the screen (real coordinates 0.9, 0, 0.1, 0.02, layer Top), over the loading screen and the
/// game alike. Each frame the pending requests are counted: new ones add to the total, finished ones to the done count, both reset when none are left; the bar
/// shows done / total, and only after six frames in a row with work pending. Faithful UI mode only (the host decides).
/// </summary>
sealed class ResourceLoadBar
{
    const int Delay = 6;
    int previous, total, done, wait = Delay;
    bool visible;
    MyGuiLayout? layout;
    string? trackSkin;
    MyGuiResources? laidOutFor;

    /// <summary>One frame of the loader with <paramref name="pending"/> requests waiting (Meitou: the objects' and characters' pending loads; engine choice).</summary>
    public void Update(int pending)
    {
        pending = Math.Max(pending, 0);
        if (pending > previous) total += pending - previous;
        else done += previous - pending;
        if (pending == 0) total = done = 0;
        previous = pending;
        // Shown with the wait as it was before this frame's step, as the original tests it before counting down.
        visible = total != 0 && wait == 0;
        if (total == 0) wait = Delay;
        else if (wait > 0) wait--;
    }

    public bool Visible => visible;

    /// <summary>The bar's parts for a <paramref name="width"/> × <paramref name="height"/> screen, its fill added in the template's <c>TrackPlace</c>; empty while hidden.</summary>
    public IReadOnlyList<UiControl> Controls(MyGuiResources resources, int width, int height)
    {
        if (!visible) return [];
        if (!ReferenceEquals(resources, laidOutFor))
        {
            laidOutFor = resources;
            layout = MyGuiTemplates.Expand(MyGuiReader.ParseLayout(
                """<MyGUI type="Layout"><Widget type="ProgressBar" skin="Kenshi_ProgressBarFill" position_real="0.9 0 0.1 0.02" layer="Top" name="LoadBar"/></MyGUI>"""), resources);
            trackSkin = resources.Templates.TryGetValue("Kenshi_ProgressBarFill", out var template)
                ? MyGuiReader.ReadResourceLayout(template).Roots.FirstOrDefault()?.UserStrings.GetValueOrDefault("TrackSkin") : null;
        }
        if (layout is null) return [];
        var controls = MyGuiPresentation.Arrange(layout, width, height, new UiBindings(), resources.Layers).ToList();
        int place = controls.FindIndex(c => c.Properties.GetValueOrDefault("TemplateOwner") == "LoadBar" && c.Properties.GetValueOrDefault("TemplatePart") == "TrackPlace");
        if (place >= 0 && trackSkin is not null && total > 0)
        {
            // TrackFill: the track runs from the start of TrackPlace to the position (MyGUI ProgressBar), drawn under the frame that follows it.
            var bounds = controls[place].Bounds;
            float fraction = Math.Clamp(done / (float)total, 0, 1);
            controls.Insert(place + 1, new UiControl("LoadBar@track", "Widget", bounds with { Width = MathF.Round(bounds.Width * fraction) }, "", "", trackSkin, true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        }
        return controls;
    }
}
