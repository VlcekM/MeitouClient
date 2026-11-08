using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Audio;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The ground trails (docs/render-terrain.md "Ground trails"): with the <c>trails</c> switch on, the characters near what the camera looks at press marks into the soft ground, read from the
/// shown snapshots and the posed feet only. Display only: the simulation is not affected and nothing is saved.
/// </summary>
sealed partial class GameHost
{
    /// <summary>How far above the terrain a mark is still left: an ankle's height; higher is a floor or a roof.</summary>
    const float MarkAboveGround = 2.5f;

    /// <summary>The furrow of a crawling or sliding body: one every this many units, its length and width.</summary>
    const float DragStep = 1.2f, DragLength = 3, DragWidth = 3;

    /// <summary>How deep packed dirt takes a mark, against sand, mud and snow.</summary>
    const float DirtMark = 0.45f;

    IGpuContext? trailContext;
    World? trailsFor;
    int trailFrame;
    long trailLast;

    /// <summary>The real seconds since the last frame the trails were updated (0 on the first), for the capsule speeds.</summary>
    float trailDt;
    readonly Dictionary<long, Walker> walkers = [];

    /// <summary>A character's last frame: where its body was, its capsules' contacts (<see cref="BodyContact"/>) and the way walked since the last fallback mark.</summary>
    struct Walker
    {
        public Vector3 Body;
        public float Walked;
        public int Frame;
        public Contact[]? Contacts;
        public Vector3[]? Recent;   // the last prints: x, z, length
        public int RecentNext;
    }

    /// <summary>Once a frame, before the world is drawn: the marks the characters near what the camera looks at press this frame (the renderer's window is centred there).</summary>
    void UpdateTrails()
    {
        if (!TerrainTrails.Enabled || trailContext is null || scene.Database is not { } db)
        {
            walkers.Clear();
            return;
        }
        var trails = gpu.Trails ??= new TerrainTrailRenderer(trailContext);
        var ground = groundSurfaces ??= new GroundSurfaces(install, db);
        if (!ReferenceEquals(trailsFor, session.World))
        {
            trailsFor = session.World;
            trails.Clear();
            walkers.Clear();
        }
        trailFrame++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        trailDt = trailLast == 0 ? 0 : Math.Clamp((now - trailLast) / (float)System.Diagnostics.Stopwatch.Frequency, 0.0005f, 0.1f);
        trailLast = now;
        IndexPrevious();
        var focus = camera.Target;
        float alpha = session.SimulationAlpha;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            var flat = new Vector2(c.Position.X - focus.X, c.Position.Z - focus.Z);
            if (flat.LengthSquared() > TerrainTrailRenderer.Reach * TerrainTrailRenderer.Reach || c.Appearance is null) continue;
            if (!c.CarriedBy.IsNone || c.InBed || c.Imprisoned) continue;
            WalkTrail(trails, ground, c, previousById.TryGetValue(c.Id, out var before) ? Meitou.Engine.Time.Interp.Lerp(before.Position, c.Position, alpha) : c.Position);
        }
        if (trailFrame % 256 == 0)
            foreach (var (key, w) in walkers)
                if (w.Frame != trailFrame) walkers.Remove(key);
    }

    /// <summary>One character's frame: its posed body against the ground, or, with no pose, a crawler's furrow (a foot's print needs the body that makes it).</summary>
    void WalkTrail(TerrainTrailRenderer trails, GroundSurfaces ground, CharacterSnapshot c, Vector3 body)
    {
        long key = DrawKey(c.Id);
        ref var w = ref CollectionsMarshal.GetValueRefOrAddDefault(walkers, key, out bool known);
        bool continues = known && w.Frame == trailFrame - 1;
        var moved = new Vector2(body.X - w.Body.X, body.Z - w.Body.Z);
        (w.Body, w.Frame) = (body, trailFrame);
        float stepped = moved.Length();
        bool animal = c.Appearance!.Animal is not null;
        bool lying = c.Down || c.StayingLow || c.Ragdoll is not null || c.Body is { Crawling: true };
        if (BodyContact(trails, ground, key, ref w, body, stepped > 1e-4f && continues ? moved / stepped : Vector2.Zero, animal, !lying, continues && trailDt > 0 ? stepped / trailDt : 0, continues)) return;
        if (!continues || stepped < 1e-4f) return;
        var along = moved / stepped;
        if (c.Down || c.StayingLow || c.Body is { Crawling: true }) Drag(trails, ground, ref w, body, along, stepped);
    }

    void Drag(TerrainTrailRenderer trails, GroundSurfaces ground, ref Walker w, Vector3 body, Vector2 along, float stepped)
    {
        w.Walked += stepped;
        if (w.Walked < DragStep) return;
        w.Walked = 0;
        Press(trails, ground, body, new TrailStamp(new Vector2(body.X, body.Z), along, DragLength, DragWidth, 1, TrailKind.Drag));
    }

    /// <summary>Queues a mark where the ground takes one: soft, above the water, and right under what pressed it (not from a floor or a roof).</summary>
    void Press(TerrainTrailRenderer trails, GroundSurfaces ground, Vector3 at, in TrailStamp stamp)
    {
        float height = gpu.Terrain.HeightAt(stamp.Centre.X, stamp.Centre.Y);
        if (at.Y - height > MarkAboveGround || height < WorldWater.Height) return;
        float soft = ground.At(stamp.Centre.X, stamp.Centre.Y) switch
        {
            GroundSurfaces.Sand or GroundSurfaces.Mud or GroundSurfaces.Snow => 1,
            GroundSurfaces.Dirt => DirtMark,
            _ => 0,
        };
        if (soft > 0) trails.Stamp(stamp with { Strength = stamp.Strength * soft });
    }

    /// <summary>How far above the water a character's feet may be and still reach it (a body lying in it, a leg stepping in).</summary>
    const float WaterReachAbove = 2;

    readonly List<WaterBody> waterBodies = [];

    /// <summary>
    /// Once a frame: the posed bodies near what the camera looks at that reach into the water, for the <c>contactfoam</c> switch's waves (docs/render-water.md
    /// "Wakes and ripples"); the field's window follows the camera's target.
    /// </summary>
    void UpdateWakes()
    {
        if (!WaterContact.Enabled || gpu.Water?.Ripples is not { } ripples || gpu.Characters is not { } characters) return;
        var focus = camera.Target;
        ripples.Begin(focus);
        float reach = WaterRipples.Reach + 20;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (MathF.Abs(c.Position.X - focus.X) > reach || MathF.Abs(c.Position.Z - focus.Z) > reach) continue;
            if (c.Position.Y > WorldWater.Height + WaterReachAbove || c.Appearance is null) continue;
            waterBodies.Clear();
            characters.WaterCapsules(DrawKey(c.Id), WorldWater.Height, WaterRipples.BodyDepth, waterBodies);
            ripples.AddBody(CollectionsMarshal.AsSpan(waterBodies));
        }
    }
}
