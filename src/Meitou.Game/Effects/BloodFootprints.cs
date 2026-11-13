using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Game.Effects;

/// <summary>
/// Visual-only transfer from wet terrain blood to moving characters' feet. Sampled from the shown world on both renderers.
/// </summary>
sealed class BloodFootprints(GameHost host)
{
    /// <summary>
    /// Distance between walking contacts, across the ground.
    /// </summary>
    const float StepLength = 4.2f;

    /// <summary>
    /// Seconds a print remains on the terrain, and seconds blood stays wet on a sole.
    /// </summary>
    const float PrintLife = 18, SoleLife = 10;

    /// <summary>
    /// Only nearby walkers are tracked. Longer movements between shown snapshots are treated as relocations.
    /// </summary>
    const float Range = 160, MaxMovement = 24;

    /// <summary>
    /// Blood a foot can still transfer, in descending strength.
    /// </summary>
    record struct Sole(uint Colour, int Steps, double Until)
    {
        /// <summary>
        /// A foot freshly wetted by a source pool or splash.
        /// </summary>
        public static Sole Wet(uint colour, double now) => new(colour, 4, now + SoleLife);
    }

    /// <summary>
    /// Last sampled position and the gait's progress for a single character.
    /// </summary>
    sealed class Trail(Vector3 position)
    {
        /// <summary>
        /// Last position of the character.
        /// </summary>
        public Vector3 Position = position;
        /// <summary>
        /// Ground distance travelled since the last footfall.
        /// </summary>
        public float Distance = StepLength / 2;
        /// <summary>
        /// Which foot takes the next step.
        /// </summary>
        public bool Right;
        /// <summary>
        /// Whether the last sample moved, used to wet the feet when starting from a pool.
        /// </summary>
        public bool Moving;
        /// <summary>
        /// Last shown tick in which the character was eligible.
        /// </summary>
        public long Seen;
        /// <summary>
        /// The remaining blood on each foot, separately.
        /// </summary>
        public Sole Left, RightSole;
    }

    /// <summary>
    /// Nearby characters that could step in a source or still have blood to transfer.
    /// </summary>
    readonly Dictionary<CharacterId, Trail> _trails = [];

    /// <summary>
    /// Scratch list of trails that were not seen on the current snapshot.
    /// </summary>
    readonly List<CharacterId> _gone = [];

    /// <summary>
    /// The last snapshot examined, so pauses and multiple render frames per tick don't leave extra prints.
    /// </summary>
    long _tick = -1;

    /// <summary>
    /// Clears the feet's residual blood when the game loads another world.
    /// </summary>
    public void Clear()
    {
        _trails.Clear();
        _tick = -1;
    }

    /// <summary>
    /// Advances every nearby walker at most once for the shown snapshot. Disabling the effect drops tracks and blood on soles.
    /// </summary>
    public void Update(TerrainDecalStore? store)
    {
        if (!host.WorldOptions.MeitouFootprints)
        {
            if (_tick < 0) return;

            Clear();
            host.Gpu.Decals?.ClearFootprints();
            return;
        }
        if (store is not { Count: > 0 }) return;

        var snapshot = host.Session.CurrentSnapshot;
        if (snapshot.Tick == _tick) return;
        _tick = snapshot.Tick;

        var focus = Xz(host.Camera.Target);
        double now = _tick * (double)host.Session.World.TickSeconds;
        foreach (var actor in snapshot.Characters)
        {
            if (actor.Down || actor.Body is { Crawling: true } || actor.Appearance is not { Animal: null } look) continue;
            var here = Xz(actor.Position);
            if (Vector2.DistanceSquared(focus, here) > Range * Range) continue;

            bool inDecalCell = HasNearbyDecals(store, here);
            if (!_trails.TryGetValue(actor.Id, out var trail))
            {
                if (!inDecalCell) continue;
                _trails.Add(actor.Id, trail = new Trail(actor.Position));
            }

            if (!inDecalCell && !Wet(trail.Left, now) && !Wet(trail.RightSole, now)) continue;
            trail.Seen = _tick;
            Advance(store, trail, actor, look, now);
        }

        foreach (var (id, trail) in _trails)
            if (trail.Seen != _tick) _gone.Add(id);
        foreach (var id in _gone) _trails.Remove(id);
        _gone.Clear();
    }

    /// <summary>
    /// The body or either foot can touch a cell's decals when it straddles the edge.
    /// </summary>
    static bool HasNearbyDecals(TerrainDecalStore store, Vector2 body)
    {
        const float reach = 1;
        uint key = TerrainDecalCells.Key(body.X, body.Y);
        if (!store.In(key).IsEmpty) return true;

        var (x, z) = TerrainDecalCells.Origin(key);
        float side = TerrainDecalCells.Size;
        float dx = body.X - x <= reach ? -reach : x + side - body.X <= reach ? reach : 0;
        float dz = body.Y - z <= reach ? -reach : z + side - body.Y <= reach ? reach : 0;
        return (dx != 0 && !store.In(TerrainDecalCells.Key(body.X + dx, body.Y)).IsEmpty)
            || (dz != 0 && !store.In(TerrainDecalCells.Key(body.X, body.Y + dz)).IsEmpty)
            || (dx != 0 && dz != 0 && !store.In(TerrainDecalCells.Key(body.X + dx, body.Y + dz)).IsEmpty);
    }

    /// <summary>
    /// Wets both feet where a walker begins moving out of a pool.
    /// </summary>
    void WetFeet(TerrainDecalStore store, Trail trail, CharacterSnapshot actor, Vector3 at, Vector2 across, double now)
    {
        if (!OnTerrain(at)) return;

        var point = Xz(at);
        if (HasFoot(actor, false) && SourceColour(store, point + across) is { } left) trail.Left = Sole.Wet(left, now);
        if (HasFoot(actor, true) && SourceColour(store, point - across) is { } right) trail.RightSole = Sole.Wet(right, now);
    }

    /// <summary>
    /// Uses distance along the motion segment to keep steps spaced consistently at different frame rates.
    /// </summary>
    void Advance(TerrainDecalStore store, Trail trail, CharacterSnapshot actor, CharacterAppearance look, double now)
    {
        var from = trail.Position;
        trail.Position = actor.Position;
        var movement = Xz(actor.Position - from);
        float length = movement.Length();
        if (length > MaxMovement || MathF.Abs(actor.Position.Y - from.Y) > MaxMovement)
        {
            trail.Left = trail.RightSole = default;
            trail.Distance = StepLength / 2;
            trail.Moving = false;
            return;
        }
        if (length <= .002f)
        {
            trail.Moving = false;
            return;
        }

        var forward = movement / length;
        var across = new Vector2(forward.Y, -forward.X);
        if (!trail.Moving) WetFeet(store, trail, actor, from, across, now);
        trail.Moving = true;

        float travel = StepLength - trail.Distance;
        if (travel > length)
        {
            trail.Distance += length;
            return;
        }

        var boot = look.Parts.FirstOrDefault(static part => part.Slot == AttachSlot.Boots && part.Record.Type == FcsRecordType.Armour);
        for (; travel <= length; travel += StepLength)
        {
            var at = Vector3.Lerp(from, actor.Position, travel / length);
            if (HasFoot(actor, trail.Right)) Footfall(store, trail, actor.Id, at, across, boot, now);
            trail.Right = !trail.Right;
        }
        trail.Distance = length - travel + StepLength;
    }

    /// <summary>
    /// Wets the contacting foot inside the source or leaves a smaller deposit on dry ground.
    /// </summary>
    void Footfall(TerrainDecalStore store, Trail trail, CharacterId actor, Vector3 at, Vector2 across, CharacterPart? boot, double now)
    {
        bool right = trail.Right;
        ref Sole sole = ref (right ? ref trail.RightSole : ref trail.Left);
        var point = Xz(at) + across * (right ? -1 : 1);
        (point, across) = FootPlacement(actor, right, point, across);
        at.X = point.X;
        at.Z = point.Y;
        if (!OnTerrain(at))
        {
            if (at.Y < WorldWater.Height + .5f) sole = default;
            return;
        }
        var source = SourceColour(store, point);
        if (source is null && !Wet(sole, now)) return;

        bool bootOnThisFoot = boot is not null && boot.HideSide != (right ? 2 : 1);
        string? mesh = bootOnThisFoot ? host.Assets.FindMesh(boot!.Mesh) : null;
        if (source is { } colour)
        {
            sole = Sole.Wet(colour, now);
            if (mesh is not null) host.Gpu.Decals?.PrepareFootprint(mesh);
            return;
        }

        uint alpha = (uint)(50 + 45 * sole.Steps);
        byte variant = (byte)((bootOnThisFoot ? 0 : 2) + (right ? 1 : 0));
        at.Y = host.Gpu.Terrain.HeightAt(point.X, point.Y);
        host.Gpu.Decals?.AddFootprint(at, across, alpha << 24 | sole.Colour, PrintLife, variant, mesh);
        sole.Steps--;
    }

    /// <summary>
    /// The current sole centre and heading from the last visible pose. While a foot is in the air or unposed, use the stride position.
    /// </summary>
    (Vector2 Point, Vector2 Across) FootPlacement(CharacterId actor, bool right, Vector2 point, Vector2 across)
    {
        if (host.Gpu.Characters is not { } characters) return (point, across);

        long key = host.DrawKey(actor);
        var foot = characters.BonePosition(key, right ? "Bip01 R Foot" : "Bip01 L Foot");
        var toe = characters.BonePosition(key, right ? "Bip01 R Toe0" : "Bip01 L Toe0");
        if (foot is not { } heel || toe is not { } tip) return (point, across);

        var centre = Xz(Vector3.Lerp(heel, tip, .46f));
        var forward = Xz(tip - heel);
        float floor = host.Gpu.Terrain.HeightAt(centre.X, centre.Y);
        bool grounded = Vector2.DistanceSquared(centre, point) <= 6.25f && forward.LengthSquared() >= .09f && MathF.Abs(tip.Y - floor) <= 1.15f;
        if (!grounded) return (point, across);

        forward = Vector2.Normalize(forward);
        return (centre, new Vector2(forward.Y, -forward.X));
    }

    /// <summary>
    /// Only the terrain receives these marks. Upper storeys and submerged terrain use no ground imprint.
    /// </summary>
    bool OnTerrain(Vector3 at) =>
        at.Y >= WorldWater.Height + .5f && MathF.Abs(at.Y - host.Gpu.Terrain.HeightAt(at.X, at.Z)) <= 3;

    /// <summary>
    /// An original or replaced leg has a foot. The legs occupy limb slots two and three.
    /// </summary>
    static bool HasFoot(CharacterSnapshot actor, bool right) =>
        (LimbState)((actor.LimbBits >> (right ? 6 : 4)) & 3) is LimbState.Original or LimbState.Replaced;

    /// <summary>
    /// Whether another mark can still be transferred by this foot.
    /// </summary>
    static bool Wet(Sole sole, double now) => sole.Steps > 0 && now < sole.Until;

    /// <summary>
    /// Blood colour under a foot, from the newest still-wet pool or splash whose centre patch contains it.
    /// </summary>
    static uint? SourceColour(TerrainDecalStore store, Vector2 foot)
    {
        uint key = TerrainDecalCells.Key(foot.X, foot.Y);
        var decals = store.In(key);
        var (x, z) = TerrainDecalCells.Origin(key);
        var cell = (foot - new Vector2(x, z)) / TerrainDecalCells.Size;
        for (int i = decals.Length - 1; i >= 0; i--)
        {
            ref readonly var mark = ref decals[i];
            if (mark.Type is not (TerrainDecalType.Pool or TerrainDecalType.Splash) || store.LifeLeft(mark) <= 0) continue;
            if (Inside(mark, cell)) return mark.Colour & 0xFFFFFF;
        }
        return null;
    }

    /// <summary>
    /// Tests the inner ellipse of the rotated decal. The transparent fringe cannot wet a sole.
    /// </summary>
    static bool Inside(in TerrainDecal mark, Vector2 cell)
    {
        const float opaqueRadius = .65f / 40960f;
        float width = mark.HalfWidth * opaqueRadius, height = mark.HalfHeight * opaqueRadius;
        if (width <= 0 || height <= 0) return false;
        var delta = cell - new Vector2(mark.LocalX, mark.LocalZ) / 1000;
        if (delta.LengthSquared() > MathF.Max(width, height) * MathF.Max(width, height)) return false;

        var (sin, cos) = MathF.SinCos(mark.Rotation / 1000f);
        float u = (delta.X * sin + delta.Y * cos) / width;
        float v = (delta.X * cos - delta.Y * sin) / height;
        return u * u + v * v <= 1;
    }

    /// <summary>
    /// A ground position in the X/Z plane.
    /// </summary>
    static Vector2 Xz(Vector3 at) => new(at.X, at.Z);
}
