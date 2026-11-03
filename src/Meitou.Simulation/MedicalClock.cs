using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>
/// The camera the world is looked at through, passed by the host once a frame (nothing is on screen without it, as in a headless world): where the eye is, which way it looks, the vertical field of
/// view and the aspect ratio, and the NPC view range (<c>npc range</c>) beyond which a character is not shown.
/// </summary>
public sealed record CameraViewCommand(Vector3 Eye, Vector3 Forward, float FovY, float Aspect, float NpcRange) : SimCommand;

/// <summary>
/// The medical clock the original's update tiers give a character (docs/game/character-stats.md "Knock-out timer", issue #134). Every character of a zone gets the frame's time added once
/// (<c>FUN_1405c7ae0</c>); a rotating window of the zone's characters also gets the full update, which adds it again (<c>FUN_1405cfd90</c>, <c>FUN_140786e30</c>). The window is 6 a frame for the
/// characters whose priority flag (<c>+0xe4</c>, <c>FUN_1405c9f60</c>) is clear, <c>max(4, N1 / 4)</c> for the flagged ones (N1 their count in the zone), so the long-run clock of a
/// character is <c>1 + min(1, w / N)</c> times the real time. The flag is set for the player's characters (<c>FUN_1405c79c0</c>, <b>Verified</b>) and for those on screen: within the NPC range
/// of the eye and inside the view frustum (<b>Observed</b>; the original's building and town refinements are not modelled). The expected share stands for the rotation (the cursors move by
/// frames, which a tick-driven simulation has not).
/// </summary>
public static class MedicalClock
{
    /// <summary>The frame's full-update window for the unflagged characters of a zone.</summary>
    public const int UnflaggedWindow = 6;
    /// <summary>The least full-update window of the flagged ones.</summary>
    public const int FlaggedWindowMin = 4;
    /// <summary>The size of the sphere tested against the frustum (engine choice: about a standing body).</summary>
    public const float BodyRadius = 100;

    /// <summary>The clock factor of a character in a class of <paramref name="count"/> in its zone.</summary>
    public static float Factor(bool flagged, int count)
    {
        if (count <= 0) return 1;
        int window = flagged ? Math.Max(FlaggedWindowMin, count / 4) : UnflaggedWindow;
        return 1 + MathF.Min(1, (float)window / count);
    }

    /// <summary>Whether a point's body is inside the view: within the range of the eye and the frustum.</summary>
    public static bool OnScreen(CameraViewCommand view, Vector3 at)
    {
        var v = at + new Vector3(0, BodyRadius, 0) - view.Eye;
        if (v.LengthSquared() > view.NpcRange * view.NpcRange) return false;
        var forward = Vector3.Normalize(view.Forward);
        var right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 1e-8f) right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        var up = Vector3.Cross(right, forward);
        float z = Vector3.Dot(v, forward);
        float tanY = MathF.Tan(view.FovY / 2), tanX = tanY * view.Aspect;
        float slackY = BodyRadius * MathF.Sqrt(1 + tanY * tanY), slackX = BodyRadius * MathF.Sqrt(1 + tanX * tanX);
        return z > -BodyRadius && MathF.Abs(Vector3.Dot(v, up)) <= z * tanY + slackY && MathF.Abs(Vector3.Dot(v, right)) <= z * tanX + slackX;
    }

    /// <summary>Sets every character's <see cref="CharacterCold.ClockFactor"/> from the zone counts (serial).</summary>
    public static void Refresh(World world, CameraViewCommand? view, Dictionary<(int, int), (int Plain, int Flagged)> counts, List<(int Slot, (int, int) Zone, bool Flagged)> members)
    {
        var table = world.Characters;
        counts.Clear();
        members.Clear();
        var prev = table.Previous;
        for (int i = 0; i < prev.Length; i++)
        {
            if (!prev[i].Alive || table.Cold(i) is not { } cold) continue;
            var zone = WorldLayout.ZoneOf(prev[i].Position.X, prev[i].Position.Z);
            bool flagged = cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction || view is not null && OnScreen(view, prev[i].Position);
            var key = (zone.X, zone.Y);
            var (plain, flag) = counts.GetValueOrDefault(key);
            counts[key] = flagged ? (plain, flag + 1) : (plain + 1, flag);
            members.Add((i, key, flagged));
        }
        foreach (var (slot, zone, flagged) in members)
        {
            var (plain, flag) = counts[zone];
            table.Cold(slot)!.ClockFactor = Factor(flagged, flagged ? flag : plain);
        }
    }
}
