using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Effects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>Damage and healing numbers over the characters (docs/game/ui-screens.md "Damage floaters").</summary>
sealed class DamageFloaters(PlayerInterface player)
{
    /// <summary>The original's squared distance (4000 units) past which a hit shows no floater.</summary>
    const float FloaterRangeSquared = 1.6e7f;
    /// <summary>
    /// The original's <c>ScreenLabel</c> (<c>FUN_1406ea9f0</c>, <b>Verified</b>): 15 units over the character, rising 4 world units a second (a hit's speed index 1 in the table 0, 4, 8),
    /// fading from 8 units of rise (alpha 1 - (rise - 8) x 0.25) and gone at 12, so 3 s in all.
    /// </summary>
    const float FloaterHeight = 15, FloaterRiseSpeed = 4, FloaterFadeFrom = 8, FloaterGone = 12;

    /// <summary>A floater on screen; <paramref name="Height"/> the units over its anchor it starts at (a machine label: 5, its point already over the building).</summary>
    internal sealed record Live(CharacterId Target, string Text, Vector4 Colour, long Born, FloaterMode Mode, float Height = FloaterHeight)
    {
        public Vector3 Last { get; set; }
    }

    internal readonly List<Live> floaters = [];

    internal void DrawFloaters(DebugOverlay o)
    {
        var queue = player.session.World.Floaters ??= new();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        while (queue.TryDequeue(out var f))
        {
            if (player.Floaters == FloaterMode.Off && f.Kind is FloaterKind.Damage or FloaterKind.Building) continue;
            if (f.Kind == FloaterKind.Damage && Vector3.DistanceSquared(player.Eye, f.Position) >= FloaterRangeSquared) continue;
            if (FloaterText.Format(f, player.Floaters) is not { } text) continue;
            // A machine label starts at its own point.
            float height = f.Kind == FloaterKind.Label ? 0 : FloaterHeight;
            floaters.Add(new Live(f.Target, text, FloaterText.Colour(f), now, FontMode(f.Kind), height) { Last = f.Kind == FloaterKind.Building ? Jittered(f.Position) : f.Position });
        }
        // A long fast-forward queues many at once: only the newest are worth showing.
        if (floaters.Count > 24) floaters.RemoveRange(0, floaters.Count - 24);
        floaters.RemoveAll(l => Rise(l, now) >= FloaterGone);
        if (floaters.Count == 0) return;

        var ids = floaters.Select(l => l.Target).ToHashSet();
        foreach (var c in player.session.CurrentSnapshot.Characters)
            if (ids.Contains(c.Id))
                foreach (var l in floaters)
                    if (l.Target == c.Id) l.Last = player.where(c);
    }

    /// <summary>
    /// The font a floater takes: a machine label (<c>FUN_1403567a0</c>) and a pick result (<c>FUN_140359160</c>) pass font 0, the small floater font; the rest follow the option.
    /// </summary>
    FloaterMode FontMode(FloaterKind kind) => kind is FloaterKind.Label or FloaterKind.PickSuccess or FloaterKind.PickFailed ? FloaterMode.Full : player.Floaters;

    /// <summary>A building's damage number starts at the point hit moved a little at random in x, y and z (<c>FUN_14029d2b0</c>; the original's amounts are <b>Unknown</b>, display only).</summary>
    static Vector3 Jittered(Vector3 at) => at + new Vector3(Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f, Random.Shared.NextSingle() - 0.5f) * BuildingFloaterJitter;

    const float BuildingFloaterJitter = 20;

    static float Rise(Live l, long now) => (float)((now - l.Born) / (double)System.Diagnostics.Stopwatch.Frequency) * FloaterRiseSpeed;

    /// <summary>
    /// The floaters as the original draws them (<c>FUN_1406ea9f0</c>, <b>Verified</b>): a plain MyGUI text box (the <c>Kenshi_TextboxStandardText</c> template: no shadow) in
    /// <c>Kenshi_FloaterFont_Medium</c> for Simple info, <c>Kenshi_FloaterFont_Small</c> for Full info, its top left at the screen point of the rising anchor, sized to the text plus
    /// 20 pixels at the UI scale. Blows landing together print over each other, as in the original.
    /// </summary>
    internal void AddFloaters(List<UiControl> controls)
    {
        if (floaters.Count == 0) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float scale = player.Height / 1080f;
        int n = 0;
        foreach (var l in floaters)
        {
            float rise = Rise(l, now);
            if (rise >= FloaterGone || player.Project(l.Last + new Vector3(0, l.Height + rise, 0)) is not { } at) continue;
            float alpha = rise > FloaterFadeFrom ? 1 - (rise - FloaterFadeFrom) * 0.25f : 1;
            controls.Add(new($"floater-{n++}", "TextBox", new(at.X, at.Y, 600 * scale, 40 * scale), l.Text, "", "", true,
                new Dictionary<string, string>
                {
                    ["FontName"] = l.Mode == FloaterMode.Full ? "Kenshi_FloaterFont_Small" : "Kenshi_FloaterFont_Medium", ["TextAlign"] = "Left Top",
                }, Vector4.Zero, l.Colour with { W = l.Colour.W * alpha }, Pickable: false));
        }
    }
}
