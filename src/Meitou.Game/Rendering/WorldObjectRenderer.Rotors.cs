using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

/// <summary>
/// The parts that turn by themselves (<see cref="RotorSpec"/>, the original's <c>RotatingEnt</c>; docs/game/buildings-production.md "Rotating parts", docs/render-objects.md "Rotating parts"). A part's state
/// is made the first time one of its meshes is drawn (the original makes the entity with the building's physics) and dropped when none has been drawn for <see cref="RotorKeepFrames"/> frames.
/// Each frame <see cref="AdvanceRotors"/> moves them all, before any is drawn, so a head and the blades hanging from it read the same angles.
/// </summary>
public sealed partial class WorldObjectRenderer
{
    /// <summary>Frames a part's state outlives its last drawing (its zone unloaded: made again from its start when it comes back, as the original makes the entity again).</summary>
    public const int RotorKeepFrames = 600;

    /// <summary>The wind at a place (x, z): speed in units/s and the unit direction it blows along in (x, z). Null: no wind (wind parts stand still).</summary>
    public Func<float, float, (float Speed, Vector2 Direction)>? RotorWind { get; set; }

    /// <summary>What a building's ROTATION_OUTPUT parts turn by (<c>getOutputBasedRotationSpeedMult</c>), asked with one of its meshes. Null: 1, the plain building's.</summary>
    public Func<PlacedMesh, float>? RotorOutput { get; set; }

    /// <summary>Whether a building is not built (a construction site): its parts stand still, as the original turns them only in a built building's update. Null: all are built.</summary>
    public Func<PlacedMesh, bool>? RotorUnbuilt { get; set; }

    sealed class RotorState(RotorSpec spec, PlacedMesh placed, Vector3 pivot)
    {
        public readonly RotorSpec Spec = spec;
        public readonly PlacedMesh Placed = placed;
        public readonly Vector3 Pivot = pivot;
        /// <summary>Functions 1 to 3: the turn about the axis since the part first moved (the original sets the orientation to it, from 0); a head: its angle, from its start.</summary>
        public float Angle;
        public bool Moved;
        public bool Notified;
        // A head's turn in progress (the slerp's ends, its time so far and its length) and the wind it last heard.
        public float From, To, Elapsed, Duration;
        public float WindSpeed;
        public Vector2 WindDirection;
        public long Seen;
    }

    // By placement, part and pivot (a part record used twice in one building is two parts).
    readonly Dictionary<(string Placement, string Part, Vector3 Pivot), RotorState> rotors = [];
    readonly List<(string, string, Vector3)> rotorsGone = [];
    long rotorFrame;

    /// <summary>
    /// One frame of every part's turning by <paramref name="dt"/> game seconds (0 while paused; <c>RotatingEnt::update</c> with the frame time <c>DAT_142133798</c>, <b>Verified</b>):
    /// ROTATION_CONSTANT at its maximum speed, ROTATION_OUTPUT at (max − min) × the building's <see cref="RotorOutput"/>, ROTATION_WIND_SPEED at its wind speed, all from angle 0 once moving; a
    /// ROTATION_WIND_DIRECTION head turns toward the wind (<see cref="RotorSpec.HeadAngle"/>) whenever the wind there changes, as described at <see cref="StepHead"/>.
    /// </summary>
    public void AdvanceRotors(float dt)
    {
        rotorFrame++;
        rotorsGone.Clear();
        foreach (var (key, r) in rotors)
        {
            if (rotorFrame - r.Seen > RotorKeepFrames) { rotorsGone.Add(key); continue; }
            if (RotorUnbuilt?.Invoke(r.Placed) == true) continue;
            var spec = r.Spec;
            if (spec.Function == RotorSpec.WindDirection) { StepHead(r, dt); continue; }
            float speed = spec.Function switch
            {
                RotorSpec.Constant => spec.SpeedMax,
                RotorSpec.Output => (spec.SpeedMax - spec.SpeedMin) * (RotorOutput?.Invoke(r.Placed) ?? 1),
                _ => RotorWind is { } wind ? spec.WindSpeedAt(wind(r.Pivot.X, r.Pivot.Z).Speed) : 0,
            };
            if (speed == 0) continue;
            r.Angle = MathF.IEEERemainder(r.Angle + speed * dt, MathF.Tau);
            r.Moved = true;
        }
        foreach (var key in rotorsGone) rotors.Remove(key);
    }

    /// <summary>
    /// A head's frame (<c>RotatingEnt::weatherUpdated</c> and <c>update</c>, <b>Verified</b>): the original tells it the weather when it is made and on every frame the region's wind is changing; each time
    /// it starts a turn from where it is to the wind's angle, lasting |from − to| / max speed (the angles in (−π, π], not wrapped, so a turn across ±π takes longer than its short way). Each
    /// frame it stands at the short-way interpolation by elapsed / length while that is below 1, then the time steps on. A head without speed never turns.
    /// </summary>
    void StepHead(RotorState r, float dt)
    {
        var (speed, direction) = RotorWind?.Invoke(r.Pivot.X, r.Pivot.Z) ?? (0, Vector2.Zero);
        if (!r.Notified || speed != r.WindSpeed || direction != r.WindDirection)
        {
            (r.Notified, r.WindSpeed, r.WindDirection) = (true, speed, direction);
            if (r.Spec.SpeedMax != 0 && direction != Vector2.Zero)
            {
                r.From = MathF.IEEERemainder(r.Angle, MathF.Tau);
                r.To = r.Spec.HeadAngle(direction);
                r.Elapsed = 0;
                r.Duration = MathF.Abs(r.From - r.To) / r.Spec.SpeedMax;
            }
        }
        if (!(r.Elapsed < r.Duration)) return;
        float u = Math.Clamp(r.Elapsed / r.Duration, 0, 1);
        r.Angle = r.From + MathF.IEEERemainder(r.To - r.From, MathF.Tau) * u;
        r.Elapsed += dt;
    }

    /// <summary>The turn of a rotor joint from its placement (its angle less its start), making its state the first time.</summary>
    float RotorTurn(PlacedMesh placed, in TurnJoint joint)
    {
        var spec = joint.Rotor!;
        var key = (placed.PlacementId, joint.PartId, joint.Pivot);
        if (!rotors.TryGetValue(key, out var r))
            rotors[key] = r = new RotorState(spec, placed, joint.Pivot) { Angle = spec.Function == RotorSpec.WindDirection ? spec.Start : 0, Seen = rotorFrame };
        r.Seen = rotorFrame;
        bool shown = r.Moved || spec.Function == RotorSpec.WindDirection;
        return shown ? r.Angle - spec.Start : 0;
    }
}
