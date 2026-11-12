using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// The scrollbars of scrolled windows and HUD lists (<c>Kenshi_ScrollBarV</c>): offsets, tracks, thumbs, wheel and drag.
/// </summary>
sealed class ScrollBars(PlayerInterface player)
{
    /// <summary>
    /// The scroll offsets of the scrolled windows and HUD lists, pixels, by id.
    /// </summary>
    internal readonly Dictionary<string, float> InventoryScroll = new(StringComparer.Ordinal);

    /// <summary>
    /// Each scrolled view's rectangle and its greatest scroll, by id.
    /// </summary>
    internal readonly Dictionary<string, (UiRect View, float Maximum)> InventoryViews = new(StringComparer.Ordinal);

    /// <summary>
    /// Per scrolled window, where its scrollbar's thumb runs and how tall it is (for dragging it).
    /// </summary>
    internal readonly Dictionary<string, (UiRect Track, float Thumb)> InventoryTracks = new(StringComparer.Ordinal);

    /// <summary>
    /// A scrollbar thumb being dragged: its window and where on the thumb it was taken.
    /// </summary>
    internal (string Screen, float Grab)? ScrollDrag;

    /// <summary>
    /// A vertical scrollbar as the original's <c>Kenshi_ScrollBarV</c> template draws it in <paramref name="bar"/>: arrows at both ends, the track
    /// between them and a thumb <c>MinTrackSize</c> (10) or more tall, sized to the visible share of the canvas. The arrows scroll by steps, a click
    /// on the track beside the thumb by a page, the thumb drags.
    /// </summary>
    internal void AddScrollBar(string id, List<UiControl> controls, UiRect bar, float visible, float canvas, float scroll, float maximum,
        UiRect? clip, string prefix = "inventory-scroll")
    {
        float scale = player.UiScale;
        void Part(string name, UiRect bounds, string skin, string action = "", bool pickable = false)
        {
            bool clickable = action.Length > 0 || pickable;
            controls.Add(new(name, clickable ? "Button" : "Widget", bounds, "", action, skin, true,
                new Dictionary<string, string>(), Vector4.Zero, Pickable: clickable, Clip: clip));
        }

        Part(prefix, bar, "Kenshi_TextBoxEmptySkin");
        Part(prefix + "-up", new(bar.X, bar.Y + 2 * scale, bar.Width, 12 * scale), "Kenshi_SmallArrowUpSkin", prefix + ":1");
        Part(prefix + "-down", new(bar.X, bar.Y + bar.Height - 14 * scale, bar.Width, 12 * scale), "Kenshi_SmallArrowDownSkin", prefix + ":-1");

        var track = new UiRect(bar.X, bar.Y + 14 * scale, bar.Width, Math.Max(0, bar.Height - 28 * scale));
        float thumb = Math.Clamp(track.Height * visible / Math.Max(1, canvas), Math.Min(10 * scale, track.Height), track.Height);
        float top = track.Y + (track.Height - thumb) * scroll / maximum;
        Part(prefix + "-page-up", new(track.X, track.Y, track.Width, top - track.Y), "", prefix + "-page:1");
        Part(prefix + "-page-down", new(track.X, top + thumb, track.Width, track.Y + track.Height - top - thumb), "", prefix + "-page:-1");
        Part(prefix + "-thumb", new(track.X, top, track.Width, thumb), "Kenshi_SmallBarSkin", pickable: true);
        InventoryTracks[id] = (track, thumb);
    }

    /// <summary>
    /// Takes the thumb of a window's scrollbar under the pointer (true: taken).
    /// </summary>
    internal bool ScrollThumbDown(string screen, UiControl picked, Vector2 p)
    {
        if (!picked.Name.EndsWith("-thumb", StringComparison.Ordinal) || !InventoryTracks.ContainsKey(screen)) return false;
        ScrollDrag = (screen, p.Y - picked.Bounds.Y);
        return true;
    }

    /// <summary>
    /// Moves the dragged thumb's canvas so the thumb follows the pointer.
    /// </summary>
    internal bool ScrollThumbMove(Vector2 p)
    {
        if (ScrollDrag is not { } drag) return false;
        if (!InventoryTracks.TryGetValue(drag.Screen, out var t) || !InventoryViews.TryGetValue(drag.Screen, out var view)
            || t.Track.Height <= t.Thumb)
            return true;

        float share = Math.Clamp((p.Y - drag.Grab - t.Track.Y) / (t.Track.Height - t.Thumb), 0, 1);
        InventoryScroll[drag.Screen] = share * view.Maximum;
        return true;
    }

    /// <summary>
    /// The wheel over a scrolled window's view scrolls it; false elsewhere.
    /// </summary>
    internal bool InventoryWheel(Vector2 p, float steps)
    {
        if (player.FaithfulScreens.HitTest(p) is not { } hit || !InventoryViews.TryGetValue(hit.Screen, out var view)
            || !view.View.Contains(p.X, p.Y))
            return false;

        ScrollInventory(hit.Screen, steps);
        return true;
    }

    /// <summary>
    /// Scrolls view <paramref name="id"/> by three cells a wheel step, within its range.
    /// </summary>
    internal void ScrollInventory(string id, float steps)
    {
        if (InventoryViews.TryGetValue(id, out var view))
            InventoryScroll[id] = Math.Clamp(InventoryScroll.GetValueOrDefault(id) - steps * player.InventoryCell * 3, 0, view.Maximum);
    }

    /// <summary>
    /// A click on the scrollbar track beside the thumb: a page (the visible height) up (1) or down (-1).
    /// </summary>
    internal void ScrollInventoryPage(string id, float direction)
    {
        if (InventoryViews.TryGetValue(id, out var view))
            InventoryScroll[id] = Math.Clamp(InventoryScroll.GetValueOrDefault(id) - direction * view.View.Height, 0, view.Maximum);
    }

    /// <summary>
    /// The list a HUD scrollbar part scrolls, by the part's name (AddScrollBar's prefix), or null.
    /// </summary>
    internal static string? HudScrollKey(string name)
    {
        if (name.StartsWith("hud-jobs", StringComparison.Ordinal)) return "JobsPanel";
        if (name.StartsWith("hud-squad", StringComparison.Ordinal)) return "SquadPanel";

        return null;
    }

    /// <summary>
    /// A HUD scrollbar's arrow (<c>prefix:direction</c>, a line) or track (<c>prefix-page:direction</c>, a page).
    /// </summary>
    internal void HudScrollAction(string key, string action)
    {
        int colon = action.LastIndexOf(':');
        if (colon < 0 || !float.TryParse(action.AsSpan(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float direction))
            return;
        if (!InventoryViews.TryGetValue(key, out var view)) return;

        float delta = action[..colon].EndsWith("-page", StringComparison.Ordinal) ? view.View.Height : 24 * player.UiScale;
        InventoryScroll[key] = Math.Clamp(InventoryScroll.GetValueOrDefault(key) - direction * delta, 0, view.Maximum);
        if (key == "SquadPanel") player.PortraitBar.SquadBarScroll = InventoryScroll[key];
    }
}