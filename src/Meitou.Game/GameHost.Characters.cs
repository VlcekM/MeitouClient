using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Content;
using Meitou.Engine;
using Meitou.Engine.Cameras;
using Meitou.Engine.Input;
using Meitou.Engine.Time;
using Meitou.Rendering;
using Meitou.Rendering.Characters;
using Meitou.Simulation;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Display;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using EngineKey = Meitou.Engine.Input.Key;
using EngineButton = Meitou.Engine.Input.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkButton = Silk.NET.Input.MouseButton;

namespace Meitou.Game;

/// <summary>The draw list from the snapshots, and markers for what has nothing to draw.</summary>
sealed partial class GameHost
{
    /// <summary>Tells the world where the camera looks, when it has moved enough to matter (the zones follow it).</summary>
    void SendFocus()
    {
        var target = session.Camera.Current.Target;
        if (sentFocus && Vector3.DistanceSquared(target, lastFocus) < 50 * 50) return;
        session.World.Commands.Enqueue(new Meitou.Simulation.FocusCommand(target) { Tick = session.World.Tick });
        lastFocus = target;
        sentFocus = true;
    }

    void IndexPrevious()
    {
        var previous = session.PreviousSnapshot;
        if (ReferenceEquals(indexedFor, previous)) return;
        previousById.Clear();
        foreach (var c in previous.Characters) previousById[c.Id] = c;
        indexedFor = previous;
    }

    /// <summary>Where a character is drawn: between the last two snapshots, by the simulation's alpha.</summary>
    Vector3 DrawnPosition(CharacterSnapshot c)
    {
        IndexPrevious();
        return previousById.TryGetValue(c.Id, out var before) ? Vector3.Lerp(before.Position, c.Position, session.SimulationAlpha) : c.Position;
    }

    /// <summary>Fills the renderer's list from the last two snapshots, interpolated by the simulation's alpha.</summary>
    void FillDrawList(CharacterDrawList list)
    {
        long f0 = Stopwatch.GetTimestamp();
        try { FillDrawListCore(list); }
        finally { fillTotal += Stopwatch.GetElapsedTime(f0).TotalMilliseconds; fillCalls++; }
    }


    void FillDrawListCore(CharacterDrawList list)
    {
        var current = session.CurrentSnapshot;
        IndexPrevious();
        float alpha = session.SimulationAlpha;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is null) continue;
            var position = c.Position;
            float yaw = c.Yaw;
            CharacterSnapshot? before = null;
            if (previousById.TryGetValue(c.Id, out var found))
            {
                before = found;
                position = Vector3.Lerp(found.Position, c.Position, alpha);
                yaw = Meitou.Engine.Time.Interp.LerpAngle(found.Yaw, c.Yaw, alpha);
            }
            var poses = new CharacterPose[c.Animations.Count];
            for (int i = 0; i < poses.Length; i++)
            {
                var layer = c.Animations[i];
                float time = layer.Time, weight = layer.Weight;
                // Between ticks the clip time and weight move on from the last snapshot (a wrap of a looping clip just shows the new time).
                if (before is not null)
                    foreach (var old in before.Animations)
                        if (old.Name == layer.Name)
                        {
                            if (layer.Time >= old.Time) time = old.Time + (layer.Time - old.Time) * alpha;
                            weight = old.Weight + (layer.Weight - old.Weight) * alpha;
                            break;
                        }
                poses[i] = new CharacterPose(layer.Name, time, weight);
            }
            list.Add(new CharacterInstance(((long)c.Id.Slot << 32 | (uint)c.Id.Generation) + 1, c.Appearance, position, yaw, poses));
        }
    }

    /// <summary>Squares over the characters that have no drawing (animals, failed builds), coloured by faction.</summary>
    void DrawMarkers(DebugOverlay overlay, int width, int height)
    {
        var view = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        var current = session.CurrentSnapshot;
        foreach (var c in current.Characters)
        {
            if (c.Appearance is not null) continue;
            var clip = Vector4.Transform(new Vector4(c.Position + new Vector3(0, 10, 0), 1), view);
            if (clip.W <= 0.1f) continue;
            float x = (clip.X / clip.W * 0.5f + 0.5f) * width, y = (0.5f - clip.Y / clip.W * 0.5f) * height;
            if (x < -10 || y < -10 || x > width + 10 || y > height + 10) continue;
            float hue = c.Faction < 0 ? 0 : (c.Faction * 0.618034f) % 1f;
            var colour = new Vector4(0.5f + 0.5f * MathF.Sin(hue * MathF.Tau), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 2.1f), 0.5f + 0.5f * MathF.Sin(hue * MathF.Tau + 4.2f), 1);
            overlay.Rect(x - 3, y - 3, x + 3, y + 3, colour);
        }
        overlay.Flush(width, height);
    }
}
