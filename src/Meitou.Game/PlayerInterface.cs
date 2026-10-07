using System.Numerics;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The player's hands and the minimal HUD (docs/game/ui-input.md): click or box select, right click move orders (shift queues),
/// the squad keys, the clock and speed buttons. Everything the player does becomes a <see cref="SimCommand"/> at the world's
/// current tick; this class never touches the simulation's state, it reads the published snapshot.
/// Positions are framebuffer pixels, top-left origin. The host sets <see cref="ViewProjection"/>, <see cref="Eye"/>, <see cref="Width"/>
/// and <see cref="Height"/> each frame (events arrive on the same thread, between frames).
/// </summary>
sealed class PlayerInterface(WorldSession session, Func<float, float, float> ground, Func<CharacterSnapshot, Vector3> where)
{
    public Matrix4x4 ViewProjection = Matrix4x4.Identity;
    public Vector3 Eye;
    public int Width = 1, Height = 1;
    /// <summary>How far from the camera a box select still takes characters.</summary>
    public const float BoxSelectRange = 7500;
    /// <summary>Character height used for picking and for the box select's aim point.</summary>
    public const float CharacterHeight = 2.2f;
    const float DragThreshold = 5;

    Vector2? dragFrom;
    Vector2 mouse;
    bool dragging;

    // ---- geometry ----

    /// <summary>The picking ray through a pixel: the near-plane point and the unit direction.</summary>
    public (Vector3 Origin, Vector3 Direction)? Ray(Vector2 pixel)
    {
        if (!Matrix4x4.Invert(ViewProjection, out var inverse)) return null;
        Vector3 At(float z)
        {
            var p = Vector4.Transform(new Vector4(pixel.X / Width * 2 - 1, 1 - pixel.Y / Height * 2, z, 1), inverse);
            return new Vector3(p.X, p.Y, p.Z) / p.W;
        }
        var near = At(0);
        var far = At(1);
        return (near, Vector3.Normalize(far - near));
    }

    /// <summary>Where the ray meets the ground: marched in growing steps, then bisected. Null when it leaves the world first.</summary>
    public Vector3? GroundHit(Vector3 origin, Vector3 direction, float maxDistance = 40000)
    {
        float Above(float t)
        {
            var p = origin + direction * t;
            return p.Y - ground(p.X, p.Z);
        }
        if (Above(0) <= 0) return origin;
        float previous = 0, step = 2;
        for (float t = step; t <= maxDistance; t += step)
        {
            if (Above(t) <= 0)
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) / 2;
                    if (Above(mid) > 0) lo = mid; else hi = mid;
                }
                var p = origin + direction * hi;
                return new Vector3(p.X, ground(p.X, p.Z), p.Z);
            }
            previous = t;
            step = MathF.Max(2, t * 0.01f);
        }
        return null;
    }

    /// <summary>The distance along the ray at which it passes closest to the segment a-b, when within <paramref name="radius"/>.</summary>
    static float? RaySegment(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, float radius)
    {
        var u = direction;
        var v = b - a;
        var w = origin - a;
        float vv = Vector3.Dot(v, v), uv = Vector3.Dot(u, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
        float denominator = vv - uv * uv;   // |u| = 1
        float s = denominator < 1e-6f ? 0 : Math.Clamp((uv * vw - vv * uw) / denominator, 0, 1e9f);
        float q = vv < 1e-6f ? 0 : Math.Clamp((vw + s * uv) / vv, 0, 1);
        s = MathF.Max(0, uv * q - uw);
        var onRay = origin + u * s;
        var onSegment = a + v * q;
        return Vector3.Distance(onRay, onSegment) <= radius ? s : null;
    }

    /// <summary>A pixel for a world point, or null behind the camera.</summary>
    public Vector2? Project(Vector3 p)
    {
        var clip = Vector4.Transform(new Vector4(p, 1), ViewProjection);
        if (clip.W <= 0.1f) return null;
        return new Vector2((clip.X / clip.W * 0.5f + 0.5f) * Width, (0.5f - clip.Y / clip.W * 0.5f) * Height);
    }

    /// <summary>The player's character under the pixel (capsule from the feet up, a little fatter with distance), if any.</summary>
    public CharacterId? Pick(Vector2 pixel)
    {
        if (Ray(pixel) is not { } ray) return null;
        CharacterId? best = null;
        float bestT = float.MaxValue;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer) continue;
            var feet = where(c);
            float distance = Vector3.Distance(Eye, feet);
            float radius = MathF.Max(1.0f, distance * 0.012f);
            if (RaySegment(ray.Origin, ray.Direction, feet, feet + new Vector3(0, CharacterHeight, 0), radius) is { } t && t < bestT)
            {
                bestT = t;
                best = c.Id;
            }
        }
        return best;
    }

    IReadOnlyList<CharacterId> InBox(Vector2 a, Vector2 b)
    {
        float x0 = MathF.Min(a.X, b.X), x1 = MathF.Max(a.X, b.X), y0 = MathF.Min(a.Y, b.Y), y1 = MathF.Max(a.Y, b.Y);
        var list = new List<CharacterId>();
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.IsPlayer) continue;
            var p = where(c) + new Vector3(0, CharacterHeight / 2, 0);
            if (Vector3.Distance(Eye, p) > BoxSelectRange) continue;
            if (Project(p) is { } s && s.X >= x0 && s.X <= x1 && s.Y >= y0 && s.Y <= y1) list.Add(c.Id);
        }
        return list;
    }

    IReadOnlyList<CharacterId> PlayerCharacters() =>
        [.. session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).Select(c => c.Id)];

    // ---- commands ----

    void Say(SimCommand command) => session.World.Commands.Enqueue(command);

    public void Select(IReadOnlyList<CharacterId> ids, bool additive) =>
        Say(new SelectCommand(ids, additive) { Tick = session.World.Tick });

    public void Move(Vector3 target, bool queued) =>
        Say(new MoveOrder([], target) { Tick = session.World.Tick, Queued = queued });

    // ---- events ----

    /// <summary>Returns true when the click belonged to the HUD or the interface (the camera input should not see it).</summary>
    public bool MouseDown(MouseButton button, Vector2 p, bool shift)
    {
        mouse = p;
        if (button == MouseButton.Left)
        {
            if (HudButtonAt(p) is { } index)
            {
                PressButton(index);
                return true;
            }
            if (OverHud(p)) return true;
            dragFrom = p;
            dragging = false;
            return true;
        }
        if (button == MouseButton.Right && !OverHud(p))
        {
            if (Ray(p) is { } ray && GroundHit(ray.Origin, ray.Direction) is { } hit) Move(hit, shift);
            return true;
        }
        return false;
    }

    public void MouseMove(Vector2 p)
    {
        mouse = p;
        if (dragFrom is { } from && !dragging && Vector2.Distance(from, p) >= DragThreshold) dragging = true;
    }

    public void MouseUp(MouseButton button, Vector2 p, bool shift)
    {
        mouse = p;
        if (button != MouseButton.Left || dragFrom is not { } from) return;
        dragFrom = null;
        if (dragging)
        {
            dragging = false;
            Select(InBox(from, p), shift);
        }
        else
        {
            var hit = Pick(p);
            Select(hit is { } id ? new[] { id } : [], shift);
        }
    }

    public void Key(Key key, bool shift)
    {
        if (key >= Meitou.Engine.Input.Key.D1 && key <= Meitou.Engine.Input.Key.D9)
        {
            var members = PlayerCharacters();
            int n = key - Meitou.Engine.Input.Key.D1;
            if (n < members.Count) Select([members[n]], shift);
        }
        else if (key == Meitou.Engine.Input.Key.Grave) Select(PlayerCharacters(), shift);
        else if (key == Meitou.Engine.Input.Key.R && !session.Camera.IsFree) Say(new StopCommand([]) { Tick = session.World.Tick });
    }

    // ---- HUD ----

    static readonly string[] ButtonLabels = ["||", "1x", "2x", "5x"];
    static readonly double[] ButtonSpeeds = [0, 1, 2, 5];
    const float Margin = 10, Pad = 6;

    float ButtonWidth(DebugOverlay o) => 4 * o.CharWidth + 2 * Pad;
    DebugOverlay? measure;

    (float X0, float Y0, float X1, float Y1) HudBox()
    {
        var o = measure;
        if (o is null) return (0, 0, 0, 0);
        float width = MathF.Max(26 * o.CharWidth, 4 * (ButtonWidth(o) + 4)) + 2 * Pad;
        float height = Pad + o.LineHeight * 2 + 4 + o.LineHeight + Pad + 6 + o.LineHeight * (1 + Math.Min(8, SelectedNames().Count));
        return (Margin, Margin, Margin + width, Margin + height);
    }

    bool OverHud(Vector2 p)
    {
        var b = HudBox();
        return p.X >= b.X0 && p.X <= b.X1 && p.Y >= b.Y0 && p.Y <= b.Y1;
    }

    (float X0, float Y0, float X1, float Y1) ButtonBox(int i)
    {
        var o = measure!;
        var box = HudBox();
        float w = ButtonWidth(o), x = box.X0 + Pad + i * (w + 4), y = box.Y0 + Pad + o.LineHeight * 2 + 4;
        return (x, y, x + w, y + o.LineHeight + 4);
    }

    int? HudButtonAt(Vector2 p)
    {
        if (measure is null) return null;
        for (int i = 0; i < ButtonLabels.Length; i++)
        {
            var b = ButtonBox(i);
            if (p.X >= b.X0 && p.X <= b.X1 && p.Y >= b.Y0 && p.Y <= b.Y1) return i;
        }
        return null;
    }

    void PressButton(int index)
    {
        if (index == 0) session.Simulation.TogglePause();
        else session.Simulation.SetSpeed(ButtonSpeeds[index]);
    }

    List<string> SelectedNames() =>
        [.. session.CurrentSnapshot.Characters.Where(c => c.Selected).Select(c => string.IsNullOrEmpty(c.Name) ? "?" : c.Name)];

    /// <summary>The HUD, the selection rings and the drag box, over the finished picture.</summary>
    public void Draw(DebugOverlay o, int width, int height)
    {
        measure = o;
        Width = width;
        Height = height;
        var white = DebugOverlay.TextColour;
        var green = new Vector4(0.35f, 1f, 0.45f, 1f);

        // Rings under the selected characters.
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (!c.Selected) continue;
            var centre = where(c);
            Vector2? previous = null, first = null;
            for (int i = 0; i <= 24; i++)
            {
                float a = i * MathF.Tau / 24;
                var point = centre + new Vector3(MathF.Sin(a) * 1.6f, 0.15f, MathF.Cos(a) * 1.6f);
                var s = Project(point);
                if (s is null) { previous = null; continue; }
                if (previous is { } prev) o.Line(prev.X, prev.Y, s.Value.X, s.Value.Y, 2, green);
                first ??= s;
                previous = s;
            }
        }

        // The box.
        if (dragging && dragFrom is { } from)
        {
            var edge = new Vector4(0.6f, 1f, 0.6f, 1);
            o.Rect(MathF.Min(from.X, mouse.X), MathF.Min(from.Y, mouse.Y), MathF.Max(from.X, mouse.X), MathF.Max(from.Y, mouse.Y), new Vector4(0.4f, 1f, 0.5f, 0.12f));
            o.Line(from.X, from.Y, mouse.X, from.Y, 1, edge);
            o.Line(mouse.X, from.Y, mouse.X, mouse.Y, 1, edge);
            o.Line(mouse.X, mouse.Y, from.X, mouse.Y, 1, edge);
            o.Line(from.X, mouse.Y, from.X, from.Y, 1, edge);
        }

        // The panel.
        var box = HudBox();
        o.Rect(box.X0, box.Y0, box.X1, box.Y1, DebugOverlay.PanelColour);
        var clock = session.Clock;
        o.Text($"{clock.TimeText}   {clock.DayText}", box.X0 + Pad, box.Y0 + Pad, white);
        o.Text($"Money: {session.World.Player.Money}", box.X0 + Pad, box.Y0 + Pad + o.LineHeight, white);
        double speed = session.Simulation.Speed;
        for (int i = 0; i < ButtonLabels.Length; i++)
        {
            var b = ButtonBox(i);
            bool active = i == 0 ? speed == 0 : speed == ButtonSpeeds[i];
            o.Rect(b.X0, b.Y0, b.X1, b.Y1, active ? new Vector4(0.25f, 0.55f, 0.3f, 0.95f) : new Vector4(0.2f, 0.2f, 0.22f, 0.95f));
            o.Text(ButtonLabels[i], b.X0 + (b.X1 - b.X0 - ButtonLabels[i].Length * o.CharWidth) / 2, b.Y0 + 2, white);
        }
        float y = ButtonBox(0).Y1 + 6;
        var names = SelectedNames();
        o.Text(names.Count == 0 ? "Selected: none" : $"Selected ({names.Count}):", box.X0 + Pad, y, white);
        y += o.LineHeight;
        foreach (var name in names.Take(8)) { o.Text(name, box.X0 + Pad + o.CharWidth, y, green); y += o.LineHeight; }
        o.Flush(width, height);
    }
}
