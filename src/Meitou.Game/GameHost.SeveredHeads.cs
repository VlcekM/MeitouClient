using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Cut-off heads (Meitou decapitation, docs/game/character-stats.md "Decapitation (Meitou)"): drawn by the character renderer, not the simulation's. When a character the
/// renderer drew with its head last frame is listed headless, its head falls from where it was: the look's <see cref="CharacterAppearance.SeveredHead"/>, holding the bones
/// of that last frame (taken at once, <see cref="Rendering.Characters.CharacterRenderer.HoldPose"/>), placed by the body's placement then, turned about the head bone and dropped
/// until it meets the ground or a body lying under it. The fall's clock starts once the head's look is ready (so it is seen falling), timed by the shown tick as the severed
/// limbs are (still while paused). Not saved: a load or a new game clears them; past the dropped-items limit the oldest goes.
/// </summary>
sealed partial class GameHost
{
    sealed class FallingHead
    {
        public required long Key, From;
        public required CharacterAppearance Look;
        /// <summary>The body's placement and its head bone's place when it was cut.</summary>
        public required Matrix4x4 World;
        public required Vector3 Start, Axis;
        /// <summary>Where it drifts to across the ground (x, z).</summary>
        public required Vector2 Aside;
        /// <summary>The speed it is thrown with (units/s): the blow that cut it off's, as the body's ragdoll (<see cref="CharacterSnapshot.Knockdown"/>).</summary>
        public required Vector3 Velocity;
        public required float Spin, Flight;
        public required ulong Blood;
        /// <summary>The shown tick its fall started (NaN until its look is drawn) and the seconds into it that it landed, at what height (NaN: still falling).</summary>
        public double Tick = double.NaN;
        public float Landed = float.NaN, RestY;
    }

    readonly List<FallingHead> heads = [];
    /// <summary>The characters whose cut was looked at (a head spawned, or none to spawn: drawn headless already, or not drawn), for <see cref="headsEpoch"/>.</summary>
    readonly HashSet<CharacterId> headsSeen = [];
    long headsEpoch = -1;
    /// <summary>How far above what it lies on the head bone rests (about a head's half height).</summary>
    const float HeadRestHeight = 1.3f;
    /// <summary>A body's bones a falling head can land on (the torso thicker than the limbs), and how far from a bone it still lands on it.</summary>
    static readonly (string Bone, float Radius)[] BodyBones =
    [
        ("Bip01 Pelvis", 2.2f), ("Bip01 Spine1", 2.2f), ("Bip01 Spine2", 2.4f), ("Bip01 Neck", 1.2f), ("Bip01 Head", 1.4f),
        ("Bip01 L Thigh", 1.3f), ("Bip01 R Thigh", 1.3f), ("Bip01 L Calf", 1f), ("Bip01 R Calf", 1f),
        ("Bip01 L UpperArm", 1f), ("Bip01 R UpperArm", 1f), ("Bip01 L Forearm", 0.8f), ("Bip01 R Forearm", 0.8f),
    ];
    /// <summary>A bone this low over the ground is lying down (the head lands on it); a standing body's are higher and let it fall past.</summary>
    const float LyingBelow = 6;

    /// <summary>A headless character listed this frame: its head falls if the renderer drew it with one in its last update.</summary>
    void CutHead(CharacterSnapshot c, CharacterAppearance look)
    {
        if (!look.Headless || gpu.Characters is not { } characters || !headsSeen.Add(c.Id)) return;
        long from = DrawKey(c.Id), key = from ^ (1L << 54);
        if (characters.LastDrawn(from, "Bip01 Head") is not ({ } world, { Headless: false } before, var start) || !characters.HoldPose(key, from)) return;
        ulong roll = unchecked((ulong)from * 0x9E3779B97F4A7C15UL ^ (ulong)session.ShownTick * 0xBF58476D1CE4E5B9UL);   // a look, not the simulation's: no world roll
        roll ^= roll >> 31;
        float angle = (roll & 0xFFFF) / 65536f * MathF.Tau, reach = 3 + ((roll >> 16) & 0xFFFF) / 65536f * 4;
        var thrown = c.Knockdown;
        // Thrown: it tumbles over the way it flies, faster the faster it goes.
        var flat = new Vector2(thrown.X, thrown.Z);
        if (flat.LengthSquared() > 1) angle = MathF.Atan2(flat.Y, flat.X);
        var head = new FallingHead
        {
            Key = key, From = from, Look = before.SeveredHead(), World = world, Start = start,
            Aside = new Vector2(start.X + MathF.Cos(angle) * reach, start.Z + MathF.Sin(angle) * reach),
            Axis = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle)), Spin = 2.5f + ((roll >> 32) & 0xFFFF) / 65536f * 3 + flat.Length() * 0.1f, Velocity = thrown,
            Flight = MathF.Sqrt(2 * MathF.Max(start.Y - session.World.Walkability.GroundHeight(start.X, start.Z, start.Y) - HeadRestHeight, 0) / FallGravity), Blood = c.BloodAmounts,
        };
        heads.Add(head);
        characters.Prefetch(head.Look);
        Spurt(from, look, start);
        while (heads.Count > Math.Max(config.GroundItemLimit, 1)) heads.RemoveAt(0);
    }

    /// <summary>The heads, each placed where its fall has it now: the head bone along the drop, the whole head turned about it until it lands.</summary>
    void AddHeads(CharacterDrawList list)
    {
        if (headsEpoch != worldEpoch) { heads.Clear(); headsSeen.Clear(); spurts.Clear(); headsEpoch = worldEpoch; }
        UpdateSpurts();
        if (heads.Count == 0 || gpu.Characters is not { } characters) return;
        double clock = session.ShownTick + session.SimulationAlpha;
        float tick = session.World.TickSeconds;
        foreach (var h in heads)
        {
            // Its clock starts with the first frame its look is drawn; until then it is listed (so it gets ready) where it was cut.
            if (double.IsNaN(h.Tick) && characters.IsPrepared(h.Look)) h.Tick = clock;
            float t = double.IsNaN(h.Tick) ? 0 : MathF.Max(0, (float)((clock - h.Tick) * tick));
            if (!float.IsNaN(h.Landed)) t = h.Landed;
            float s = h.Flight > 0 ? MathF.Min(t / h.Flight, 1) : 1;
            var at = new Vector3(h.Start.X + (h.Aside.X - h.Start.X) * s + h.Velocity.X * t, h.Start.Y + h.Velocity.Y * t - 0.5f * FallGravity * t * t, h.Start.Z + (h.Aside.Y - h.Start.Z) * s + h.Velocity.Z * t);
            if (float.IsNaN(h.Landed))
            {
                float floor = SurfaceUnder(characters, new Vector2(at.X, at.Z), h) + HeadRestHeight;
                if (at.Y <= floor) (h.Landed, h.RestY) = (t, floor);
            }
            if (!float.IsNaN(h.Landed)) at.Y = h.RestY;
            var world = h.World * Matrix4x4.CreateTranslation(-h.Start) * Matrix4x4.CreateFromAxisAngle(h.Axis, h.Spin * t) * Matrix4x4.CreateTranslation(at);
            list.Add(new CharacterInstance(h.Key, h.Look, at, 0, []) { World = world, FreezeFrom = h.From, Blood = h.Blood });
        }
    }

    /// <summary>
    /// The top of what lies at (x, z) under the falling head <paramref name="head"/>: the ground (the walkable surface below where it was cut: terrain, floor), or the highest bone of a body lying there (one within <see cref="LyingBelow"/> of
    /// the ground, as the renderer drew it last frame) plus that bone's thickness. Only what is below where the fall began counts (a head cut from a body already down starts
    /// among its bones), and never its own corpse's neck and head, where it was cut. Engine choice: the bodies are spheres round their bones, not their meshes or ragdoll shapes.
    /// </summary>
    float SurfaceUnder(Rendering.Characters.CharacterRenderer characters, Vector2 at, FallingHead head)
    {
        float ground = session.World.Walkability.GroundHeight(at.X, at.Y, head.Start.Y), top = ground;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (Vector2.DistanceSquared(new Vector2(c.Position.X, c.Position.Z), at) > 30 * 30) continue;
            long key = DrawKey(c.Id);
            foreach (var (bone, radius) in BodyBones)
            {
                if (key == head.From && bone is "Bip01 Neck" or "Bip01 Head") continue;
                if (characters.BonePosition(key, bone) is not { } p || p.Y >= ground + LyingBelow || Vector2.DistanceSquared(new Vector2(p.X, p.Z), at) >= radius * radius) continue;
                float surface = p.Y + radius * 0.6f;
                if (surface + HeadRestHeight < head.Start.Y) top = MathF.Max(top, surface);
            }
        }
        return top;
    }
}
