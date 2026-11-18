// Shared window-side helpers of the town windows (task 9.3e, the M8 carry-in): the town arena window (src/Ui/TownArenaUI.cs) and the two bookie
// windows (src/Ui/TownBookieUI.cs, TownBookieResultUI.cs) each carried their own copies of these, written for 7.6 and copied by 8.1b. Moved here
// unchanged: no rectangle, text, colour or click differs from the copies they replace (PgCheck ui and the layout tests are the evidence).
//
// Engine calls and what replaces them (the same as the copies'):
//   setCoord in the parent (every Layout / LayoutCard)                   -> UiRect fractions of the parent's size (Fraction / ParentSize)
//   InputManager::getMousePositionByLayer + MyGUI's mouse focus widget    -> ScreenHost.Pointer, MyGuiScreenHost.HitTest (HoverWidget)
//   eventToolTip + MyGUI's ToolTipManager (PortraitTip, PlaceTip, HideTip) -> TooltipTimer per window, the client origin found once per layout,
//                                                                          TownArenaUILayout.FighterTip / PlaceTip (WindowTip)
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Ui;

/// <summary>The town windows' shared layout helpers: a widget's rectangle as fractions of its parent, and its parent's size.</summary>
static class TownWindowParts
{
    /// <summary>The properties <c>setVisible(true)</c> binds (shared by the three windows: the tip background, a portrait's placeholder).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Shown = new Dictionary<string, string>(StringComparer.Ordinal) { ["Visible"] = "true" };

    /// <summary>MyGUI's setCoord in the parent: <paramref name="r"/> (pixels in the parent) as fractions of the parent's size (at least 1 px).</summary>
    internal static UiRect Fraction(LayoutRect r, (int W, int H) parent)
    {
        float w = Math.Max(1, parent.W), h = Math.Max(1, parent.H);
        return new UiRect(r.X / w, r.Y / h, r.W / w, r.H / h);
    }

    /// <summary>The size a child of <paramref name="parent"/> is laid out in: the client for "", a scroll view's canvas, else the parent's
    /// rectangle (1 x 1 when the layout has none).</summary>
    internal static (int W, int H) ParentSize(TownWindowLayout l, string parent) => ParentSize(l.Rects, l.Canvas, l.ClientWidth, l.ClientHeight, parent);

    /// <inheritdoc cref="ParentSize(TownWindowLayout, string)"/>
    internal static (int W, int H) ParentSize(PgWindowLayout l, string parent) => ParentSize(l.Rects, l.Canvas, l.ClientWidth, l.ClientHeight, parent);

    static (int W, int H) ParentSize(Dictionary<string, LayoutRect> rects, Dictionary<string, (int W, int H)> canvas, int clientW, int clientH, string parent)
    {
        if (parent.Length == 0) return (clientW, clientH);
        if (canvas.TryGetValue(parent, out var c)) return c;   // the bookie result window's layout has no canvas: its children read their rectangle
        return rects.TryGetValue(parent, out var r) ? (r.W, r.H) : (1, 1);
    }

    /// <summary>The widget of window <paramref name="screen"/> under the pointer (MyGUI's mouse focus widget), or null.</summary>
    internal static string? HoverWidget(Meitou.Game.Ui.MyGuiScreenHost host, Vector2 at, string screen)
    {
        if (!float.IsFinite(at.X)) return null;
        return host.HitTest(at) is { } hit && hit.Screen == screen ? hit.Control.Name : null;
    }
}

/// <summary>
/// One town window's portrait tip (TownArenaUI.cpp PortraitTip :184-221 / PlaceTip :171-183; TownBookieUI.cpp PortraitTip :87-110 / PlaceTip
/// :75-86): MyGUI's tooltip timing for the widget under the pointer, the window client's origin on the screen, the fighter tip's text and size, and
/// the tip background placed beside the pointer. Window thread only; one instance per window (its state is that window's).
/// </summary>
sealed class WindowTip(string screen, string window, string background, string fighterTip, TextMeasure text, RewardsTextMeasure measure)
{
    readonly TooltipTimer timer = new();
    string? owner;   // the widget the tip timer follows (MyGUI's tooltip owner: a new owner restarts the wait)
    (object Layout, Vector2 Origin)? originFor;
    (int W, int H, string Text, int ClientW, int ClientH, int Font) fighterSize;

    /// <summary>The tip's wait forgotten (HideTip at Close; a new opening of the window).</summary>
    public void Reset()
    {
        timer.Reset();
        owner = null;
    }

    /// <summary>The client origin found again at the next tip (a new world: the window made again; PgCheck ui).</summary>
    public void ForgetOrigin() => originFor = null;

    /// <summary>The widget of this window under the pointer, or null.</summary>
    public string? Hover(Meitou.Game.Ui.MyGuiScreenHost host, Vector2 at) => TownWindowParts.HoverWidget(host, at, screen);

    /// <summary>One frame of MyGUI's tooltip timing with <paramref name="tipOwner"/> under the pointer (null = none): whether the tip shows.</summary>
    public bool Follow(string? tipOwner, Vector2 at)
    {
        if (tipOwner != owner)   // MyGUI: leaving one widget hides its tip; the next one waits its own delay
        {
            timer.Update(false, false, at.X, at.Y, ScreenHost.UiClock.Elapsed.TotalSeconds);
            owner = tipOwner;
        }
        return timer.Update(tipOwner is not null, ScreenHost.AnyHeld, at.X, at.Y, ScreenHost.UiClock.Elapsed.TotalSeconds);
    }

    /// <summary>The client's top left on the screen (the arranged window's client part, else the layout's window + the template's client offset),
    /// found once per layout (the arranged client, once it is there).</summary>
    public Vector2 ClientOrigin(TownWindowLayout l) => ClientOrigin(l, l.Window);

    /// <inheritdoc cref="ClientOrigin(TownWindowLayout)"/>
    public Vector2 ClientOrigin(PgWindowLayout l) => ClientOrigin(l, l.Window);

    Vector2 ClientOrigin(object layout, LayoutRect windowRect)
    {
        if (originFor is { } o && ReferenceEquals(o.Layout, layout)) return o.Origin;
        if (ScreenHost.ControlsOf(screen) is { } controls)
            foreach (var c in controls)
                if (c.Properties.GetValueOrDefault("TemplateOwner") == window && c.Properties.GetValueOrDefault("TemplatePart") == "Client")
                {
                    var at = new Vector2(c.Bounds.X, c.Bounds.Y);
                    originFor = (layout, at);
                    return at;
                }
        return new Vector2(windowRect.X + ArenaUILayout.WindowClientX, windowRect.Y + ArenaUILayout.WindowClientY);
    }

    /// <summary>The fighter tip's text bound and its size (TownArenaUILayout.FighterTip, measured once per text, client and font): Wrap(fighterTip,
    /// 0, 0, width, ...), the background its size.</summary>
    public (int W, int H) BindFighterTip(ScreenBinding b, string tipText, int clientW, int clientH, int font)
    {
        if (fighterSize.Text != tipText || fighterSize.ClientW != clientW || fighterSize.ClientH != clientH || fighterSize.Font != font)
        {
            text.UseCurrent();
            var (tw, th) = TownArenaUILayout<CharacterId>.FighterTip(clientW, clientH, font, tipText, measure);
            fighterSize = (tw, th, tipText, clientW, clientH, font);
        }
        b.Text(fighterTip, tipText);
        b.Size(fighterTip, new UiRect(0, 0, 1, 1));
        return (fighterSize.W, fighterSize.H);
    }

    /// <summary>PlaceTip: the tip background shown, <paramref name="w"/> x <paramref name="h"/>, beside the pointer and kept inside the client.</summary>
    public void Place(ScreenBinding b, int clientW, int clientH, int font, Vector2 at, Vector2 origin, int w, int h)
    {
        var (x, y) = TownArenaUILayout<CharacterId>.PlaceTip(clientW, clientH, font, (int)(at.X - origin.X), (int)(at.Y - origin.Y), w, h);
        b.Props(background, TownWindowParts.Shown);
        b.Size(background, TownWindowParts.Fraction(new LayoutRect(x, y, w, h), (clientW, clientH)));
    }
}
