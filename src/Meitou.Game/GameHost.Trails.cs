using Meitou.Game.Audio;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Impostors;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Rendering.Shadows;
using Meitou.Game.Rendering.Sky;
using Meitou.Game.Rendering.Terrain;
using Meitou.Game.Rendering.Ui;
using Meitou.Game.Rendering.Upscalers;
using Meitou.Game.Rendering.Water;
using Meitou.Game.Rendering.Gpu;
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

    IGpuContext? _trailContext;
    GroundSoftness? _groundSoftness;
    World? _trailsFor;
    int _trailFrame;
    long _trailLast;

    /// <summary>The real seconds since the last frame the trails were updated (0 on the first), for the capsule speeds.</summary>
    float _trailDt;
    readonly Dictionary<long, Walker> _walkers = [];

    /// <summary>A character's last frame: where its body was, its capsules' contacts (<see cref="BodyContact"/>) and the way walked since the last fallback mark.</summary>
    struct Walker
    {
        public Vector3 Body;
        public float Walked;
        public int Frame;
        public Contact[]? Contacts;
        public Limb[]? Limbs;
        public Vector3[]? Recent;   // the last prints: x, z, length
        public int RecentNext;
    }

    /// <summary>Once a frame, before the world is drawn: the marks the characters near what the camera looks at press this frame (the renderer's window is centred there).</summary>
    void UpdateTrails()
    {
        if (!TerrainTrails.Enabled || _trailContext is null || Scene.Database is not { } db)
        {
            _walkers.Clear();
            return;
        }
        var trails = Gpu.Trails ??= new TerrainTrailRenderer(_trailContext);
        var ground = Sound.GroundSurfacesOf(db);
        if (!ReferenceEquals(_trailsFor, Session.World))
        {
            _trailsFor = Session.World;
            trails.Clear();
            _walkers.Clear();
        }
        _trailFrame++;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        _trailDt = _trailLast == 0 ? 0 : Math.Clamp((now - _trailLast) / (float)System.Diagnostics.Stopwatch.Frequency, 0.0005f, 0.1f);
        _trailLast = now;
        IndexPrevious();
        var focus = Camera.Target;
        float alpha = Session.SimulationAlpha;
        foreach (var c in Session.CurrentSnapshot.Characters)
        {
            var flat = new Vector2(c.Position.X - focus.X, c.Position.Z - focus.Z);
            if (flat.LengthSquared() > TerrainTrailRenderer.Reach * TerrainTrailRenderer.Reach || c.Appearance is null) continue;
            if (!c.CarriedBy.IsNone || c.InBed || c.Imprisoned) continue;
            WalkTrail(trails, ground, c, PreviousById.TryGetValue(c.Id, out var before) ? Meitou.Simulation.Session.Interp.Lerp(before.Position, c.Position, alpha) : c.Position);
        }
        if (_trailFrame % 256 == 0)
            foreach (var (key, w) in _walkers)
                if (w.Frame != _trailFrame) _walkers.Remove(key);
    }

    /// <summary>One character's frame: its posed body against the ground, or, with no pose, a crawler's furrow (a foot's print needs the body that makes it).</summary>
    void WalkTrail(TerrainTrailRenderer trails, GroundSurfaces ground, CharacterSnapshot c, Vector3 body)
    {
        long key = DrawKey(c.Id);
        ref var w = ref CollectionsMarshal.GetValueRefOrAddDefault(_walkers, key, out bool known);
        bool continues = known && w.Frame == _trailFrame - 1;
        var moved = new Vector2(body.X - w.Body.X, body.Z - w.Body.Z);
        (w.Body, w.Frame) = (body, _trailFrame);
        float stepped = moved.Length();
        bool animal = c.Appearance!.Animal is not null;
        bool lying = c.Down || c.StayingLow || c.Ragdoll is not null || c.Body is { Crawling: true };
        if (BodyContact(trails, ground, key, ref w, body, stepped > 1e-4f && continues ? moved / stepped : Vector2.Zero, animal, !lying, continues && _trailDt > 0 ? stepped / _trailDt : 0, continues)) return;
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
        float height = Gpu.Terrain.HeightAt(stamp.Centre.X, stamp.Centre.Y);
        if (at.Y - height > MarkAboveGround || height < WorldWater.Height) return;
        var (type, texture) = ground.LayerAt(stamp.Centre.X, stamp.Centre.Y);
        float soft = type < 0 ? 0 : (_groundSoftness ??= new GroundSoftness(Install, Scene.Database!)).Of(type, texture);
        if (soft > 0) trails.Stamp(stamp with { Strength = stamp.Strength * soft });
    }

    /// <summary>How far above the water a character's feet may be and still reach it (a body lying in it, a leg stepping in).</summary>
    const float WaterReachAbove = 2;

    readonly List<WaterBody> _waterBodies = [];

    /// <summary>
    /// Once a frame: the posed bodies near what the camera looks at that reach into the water, for the <c>contactfoam</c> switch's waves (docs/render-water.md
    /// "Wakes and ripples"); the field's window follows the camera's target.
    /// </summary>
    void UpdateWakes()
    {
        if (!WaterContact.Enabled || Gpu.Water?.Ripples is not { } ripples || Gpu.Characters is not { } characters) return;
        var focus = Camera.Target;
        ripples.Begin(focus);
        float reach = WaterRipples.Reach + 20;
        foreach (var c in Session.CurrentSnapshot.Characters)
        {
            if (MathF.Abs(c.Position.X - focus.X) > reach || MathF.Abs(c.Position.Z - focus.Z) > reach) continue;
            if (c.Position.Y > WorldWater.Height + WaterReachAbove || c.Appearance is null) continue;
            _waterBodies.Clear();
            characters.WaterCapsules(DrawKey(c.Id), WorldWater.Height, WaterRipples.BodyDepth, _waterBodies);
            ripples.AddBody(CollectionsMarshal.AsSpan(_waterBodies));
        }
    }
}
