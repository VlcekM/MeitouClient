using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Host;

/// <summary>
/// The original's background-loading bar (the resource loader's update, <c>FUN_14044bf40</c>; docs/game/ui-screens.md "Background loading bar"): a
/// <c>Kenshi_ProgressBarFill</c> progress bar at the top right of the screen (real coordinates 0.9, 0, 0.1, 0.02, layer Top), over the loading screen
/// and the game alike. Each frame the pending requests are counted: new ones add to the total, finished ones to the done count, both reset when none
/// are left; the bar shows done / total, and only after six frames in a row with work pending. Faithful UI mode only (the host decides).
/// </summary>
sealed class ResourceLoadBar
{
    /// <summary>
    /// Frames in a row with work pending before the bar shows: six.
    /// </summary>
    const int Delay = 6;
    /// <summary>
    /// Last frame's pending requests, the requests counted since none were left, those finished, and the frames still to wait.
    /// </summary>
    int _previous, _total, _done, _wait = Delay;
    /// <summary>
    /// Whether the bar shows this frame.
    /// </summary>
    bool _visible;
    /// <summary>
    /// The bar's layout, made on first draw.
    /// </summary>
    MyGuiLayout? _layout;
    /// <summary>
    /// The track's skin, from the layout.
    /// </summary>
    string? _trackSkin;
    /// <summary>
    /// The resources <see cref="_layout"/> was made from: other resources lay it out again.
    /// </summary>
    MyGuiResources? _laidOutFor;

    /// <summary>
    /// One frame of the loader with <paramref name="pending"/> requests waiting (Meitou: the objects' and characters' pending loads; engine choice).
    /// </summary>
    public void Update(int pending)
    {
        pending = Math.Max(pending, 0);
        if (pending > _previous) _total += pending - _previous;
        else _done += _previous - pending;
        if (pending == 0) _total = _done = 0;
        _previous = pending;
        // Shown with the wait as it was before this frame's step, as the original tests it before counting down.
        _visible = _total != 0 && _wait == 0;
        if (_total == 0) _wait = Delay;
        else if (_wait > 0) _wait--;
    }

    /// <summary>
    /// Whether the bar shows this frame (<see cref="Update"/>).
    /// </summary>
    public bool Visible => _visible;

    /// <summary>
    /// The bar's parts for a <paramref name="width"/> × <paramref name="height"/> screen, its fill added in the template's <c>TrackPlace</c>; empty
    /// while hidden.
    /// </summary>
    public IReadOnlyList<UiControl> Controls(MyGuiResources resources, int width, int height)
    {
        if (!_visible) return [];
        if (!ReferenceEquals(resources, _laidOutFor))
        {
            _laidOutFor = resources;
            _layout = MyGuiTemplates.Expand(MyGuiReader.ParseLayout(
                """<MyGUI type="Layout"><Widget type="ProgressBar" skin="Kenshi_ProgressBarFill" position_real="0.9 0 0.1 0.02" layer="Top" name="LoadBar"/></MyGUI>"""), resources);
            _trackSkin = resources.Templates.TryGetValue("Kenshi_ProgressBarFill", out var template)
                ? MyGuiReader.ReadResourceLayout(template).Roots.FirstOrDefault()?.UserStrings.GetValueOrDefault("TrackSkin") : null;
        }
        if (_layout is null) return [];
        var controls = MyGuiPresentation.Arrange(_layout, width, height, new UiBindings(), resources.Layers).ToList();
        int place = controls.FindIndex(c => c.Properties.GetValueOrDefault("TemplateOwner") == "LoadBar"
            && c.Properties.GetValueOrDefault("TemplatePart") == "TrackPlace");
        if (place >= 0 && _trackSkin is not null && _total > 0)
        {
            // TrackFill: the track runs from the start of TrackPlace to the position (MyGUI ProgressBar), drawn under the frame that follows it.
            var bounds = controls[place].Bounds;
            float fraction = Math.Clamp(_done / (float)_total, 0, 1);
            controls.Insert(place + 1,
                new UiControl("LoadBar@track", "Widget", bounds with { Width = MathF.Round(bounds.Width * fraction) }, "", "", _trackSkin, true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: false));
        }
        return controls;
    }
}