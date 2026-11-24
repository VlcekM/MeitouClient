using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Effects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// Damage and healing numbers over the characters (docs/game/ui-screens.md "Damage floaters").
/// </summary>
sealed class DamageFloaters(PlayerInterface player)
{
    /// <summary>
    /// The original's squared distance (4000 units) past which a hit shows no floater.
    /// </summary>
    const float FloaterRangeSquared = 1.6e7f;

    /// <summary>
    /// The original's <c>ScreenLabel</c> (<c>FUN_1406ea9f0</c>, <b>Verified</b>): 15 units over the character, rising 4 world units a second (a hit's
    /// speed index 1 in the table 0, 4, 8), fading from 8 units of rise (alpha 1 - (rise - 8) x 0.25) and gone at 12, so 3 s in all.
    /// </summary>
    const float FloaterHeight = 15, FloaterRiseSpeed = 4, FloaterFadeFrom = 8, FloaterGone = 12;

    /// <summary>
    /// How far at random a building's damage number starts from its door along x and z, either way, and how much higher than
    /// <see cref="FloaterHeight"/> at most (<c>FUN_14029d2b0</c>, <b>Verified</b>: 15 up, then -20..20, 0..10 and -20..20).
    /// </summary>
    const float BuildingFloaterSpread = 20, BuildingFloaterRise = 10;

    /// <summary>
    /// A floater on screen; <paramref name="Height"/> the units over its anchor it starts at (a machine label: 5, its point already over the
    /// building).
    /// </summary>
    internal sealed record Live(CharacterId Target, string Text, Vector4 Colour, FloaterMode Mode, float Height = FloaterHeight)
    {
        /// <summary>
        /// The large floater font (a shopper's price passes font 2, <c>Kenshi_FloaterFont_Large</c>), whatever <see cref="Mode"/> says.
        /// </summary>
        public bool Large { get; init; }
        /// <summary>
        /// The anchor's last position (the character's as drawn).
        /// </summary>
        public Vector3 Last { get; set; }
        /// <summary>
        /// The units risen so far: game time, so it stands still while the game is paused and runs faster at the fast speeds (<c>FUN_1406ea9f0</c>
        /// adds the game's frame time).
        /// </summary>
        public float Rise { get; set; }
    }

    /// <summary>
    /// The floaters on screen.
    /// </summary>
    internal readonly List<Live> Floaters = [];
    /// <summary>
    /// The timestamp of the last draw, for the real seconds that passed since.
    /// </summary>
    long _lastDraw;

    /// <summary>
    /// Takes the world's new floaters (none when off, out of range or without text), keeps the newest 24 still rising, and moves each to its
    /// character.
    /// </summary>
    internal void DrawFloaters(DebugOverlay o)
    {
        var queue = player.Session.World.Floaters ??= new();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float seconds = _lastDraw == 0 ? 0 : (float)((now - _lastDraw) / (double)System.Diagnostics.Stopwatch.Frequency);
        _lastDraw = now;
        float risen = seconds * (float)player.Session.Simulation.Speed * FloaterRiseSpeed;
        foreach (var l in Floaters) l.Rise += risen;

        while (queue.TryDequeue(out var f))
        {
            if (player.Floaters == FloaterMode.Off && f.Kind is FloaterKind.Damage or FloaterKind.Building) continue;
            if (f.Kind == FloaterKind.Damage && Vector3.DistanceSquared(player.Eye, f.Position) >= FloaterRangeSquared) continue;
            if (FloaterText.Format(f, player.Floaters) is not { } text) continue;

            // A machine label starts at its own point.
            float height = f.Kind == FloaterKind.Label ? 0 : FloaterHeight;
            var live = new Live(f.Target, text, FloaterText.Colour(f), FontMode(f.Kind), height) { Large = f.Kind == FloaterKind.Price };
            live.Last = f.Kind == FloaterKind.Building ? Jittered(f.Position) : f.Position;
            Floaters.Add(live);
        }

        // A long fast-forward queues many at once: only the newest are worth showing.
        if (Floaters.Count > 24) Floaters.RemoveRange(0, Floaters.Count - 24);
        Floaters.RemoveAll(l => l.Rise >= FloaterGone);
        if (Floaters.Count == 0) return;

        var ids = Floaters.Select(l => l.Target).ToHashSet();
        foreach (var c in player.Session.CurrentSnapshot.Characters)
            if (ids.Contains(c.Id))
                foreach (var l in Floaters)
                    if (l.Target == c.Id) l.Last = player.Where(c);
    }

    /// <summary>
    /// The font a floater takes: a machine label (<c>FUN_1403567a0</c>), a pick result (<c>FUN_140359160</c>) and a failed harvest
    /// (<c>FUN_1400e59e0</c>) pass font 0, the small floater font; the rest follow the option.
    /// </summary>
    FloaterMode FontMode(FloaterKind kind) =>
        kind is FloaterKind.Label or FloaterKind.PickSuccess or FloaterKind.PickFailed or FloaterKind.HarvestFailed
            ? FloaterMode.Full
            : player.Floaters;

    /// <summary>
    /// The floater's MyGUI font: the large one for a price, else the small one for Full info (and the fixed small-font floaters), the medium one
    /// otherwise.
    /// </summary>
    static string FontName(Live l) =>
        l.Large ? "Kenshi_FloaterFont_Large"
        : l.Mode == FloaterMode.Full ? "Kenshi_FloaterFont_Small"
        : "Kenshi_FloaterFont_Medium";

    /// <summary>
    /// A building's damage number starts at its door's origin moved at random (<c>FUN_14029d2b0</c>, <b>Verified</b>; display only, so not the
    /// world's random).
    /// </summary>
    static Vector3 Jittered(Vector3 at)
    {
        float x = (Random.Shared.NextSingle() * 2 - 1) * BuildingFloaterSpread;
        float z = (Random.Shared.NextSingle() * 2 - 1) * BuildingFloaterSpread;
        return at + new Vector3(x, Random.Shared.NextSingle() * BuildingFloaterRise, z);
    }

    /// <summary>
    /// The floaters as the original draws them (<c>FUN_1406ea9f0</c>, <b>Verified</b>): a plain MyGUI text box (the <c>Kenshi_TextboxStandardText</c>
    /// template: no shadow) in <c>Kenshi_FloaterFont_Medium</c> for Simple info, <c>Kenshi_FloaterFont_Small</c> for Full info, its top left at the
    /// screen point of the rising anchor, sized to the text plus 20 pixels at the UI scale. Blows landing together print over each other, as in the
    /// original.
    /// </summary>
    internal void AddFloaters(List<UiControl> controls)
    {
        if (Floaters.Count == 0) return;

        float scale = player.UiScale;
        int n = 0;
        foreach (var l in Floaters)
        {
            float rise = l.Rise;
            if (rise >= FloaterGone
                || player.Project(l.Last + new Vector3(0, l.Height + rise, 0)) is not { } at) continue;

            float alpha = rise > FloaterFadeFrom ? 1 - (rise - FloaterFadeFrom) * 0.25f : 1;
            var properties = new Dictionary<string, string>
            {
                ["FontName"] = FontName(l),
                ["TextAlign"] = "Left Top",
            };
            controls.Add(new($"floater-{n++}", "TextBox", new(at.X, at.Y, 600 * scale, 40 * scale), l.Text, "", "", true, properties,
                Vector4.Zero, l.Colour with { W = l.Colour.W * alpha }, Pickable: false));
        }
    }
}