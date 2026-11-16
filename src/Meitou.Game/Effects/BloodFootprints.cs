using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;

namespace Meitou.Game.Effects;

/// <summary>
/// A human foot settling on the ground, found from the posed body by the ground trails (docs/render-terrain.md "Ground trails"): the character, which
/// foot, the sole's centre on the ground and the way its toes point.
/// </summary>
readonly record struct Footfall(CharacterSnapshot Actor, bool Right, Vector3 At, Vector2 Along);

/// <summary>
/// Visual-only transfer from wet terrain blood to moving characters' feet (docs/game/blood-decals.md "Bloody footprints"). Every step is a ground
/// trails' footfall (<see cref="Footfall"/>) of a drawn body; a body with no pose leaves none.
/// </summary>
sealed class BloodFootprints(GameHost host)
{
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
    /// A walker near blood: where it was last seen and the blood on its soles.
    /// </summary>
    sealed class Trail(Vector3 position)
    {
        /// <summary>
        /// Last position of the character.
        /// </summary>
        public Vector3 Position = position;
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
    /// The game time of the last snapshot examined, for the footfalls raised between snapshots.
    /// </summary>
    double _now;

    /// <summary>
    /// Clears the feet's residual blood when the game loads another world.
    /// </summary>
    public void Clear()
    {
        _trails.Clear();
        _tick = -1;
    }

    /// <summary>
    /// Whether footfalls are wanted at all: the switch is on and there is blood on the terrain.
    /// </summary>
    public bool Wanted(TerrainDecalStore? store) => host.WorldOptions.MeitouFootprints && store is { Count: > 0 };

    /// <summary>
    /// Whether a character's footfalls are wanted: a walking human by blood, or one with blood left on a sole.
    /// </summary>
    public bool Wants(TerrainDecalStore store, CharacterSnapshot actor)
    {
        if (!Walks(actor, out _)) return false;
        if (_trails.TryGetValue(actor.Id, out var trail) && (Wet(trail.Left, _now) || Wet(trail.RightSole, _now))) return true;

        return HasNearbyDecals(store, Xz(actor.Position));
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
        double now = _now = _tick * (double)host.Session.World.TickSeconds;
        foreach (var actor in snapshot.Characters)
        {
            if (!Walks(actor, out _)) continue;
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
            Follow(trail, actor);
        }

        foreach (var (id, trail) in _trails)
            if (trail.Seen != _tick) _gone.Add(id);
        foreach (var id in _gone) _trails.Remove(id);
        _gone.Clear();
    }

    /// <summary>
    /// A footfall from the posed body: wets that foot in a source, or leaves a print where it settled while the sole is still wet.
    /// </summary>
    public void Footfall(TerrainDecalStore store, in Footfall step)
    {
        if (!Wanted(store) || !Walks(step.Actor, out var look) || !HasFoot(step.Actor, step.Right)) return;

        if (!_trails.TryGetValue(step.Actor.Id, out var trail))
        {
            if (!HasNearbyDecals(store, Xz(step.At))) return;
            _trails.Add(step.Actor.Id, trail = new Trail(step.Actor.Position) { Seen = _tick });
        }

        var across = new Vector2(step.Along.Y, -step.Along.X);
        Deposit(store, trail, step.Right, step.At, across, Boot(look), look, _now);
    }

    /// <summary>
    /// A human on its feet, the only kind that leaves these prints.
    /// </summary>
    static bool Walks(CharacterSnapshot actor, out CharacterAppearance look)
    {
        look = actor.Appearance!;
        return !actor.Down && actor.Body is not { Crawling: true } && actor.Appearance is { Animal: null };
    }

    /// <summary>
    /// The boots worn, whose sole the print takes; null barefoot.
    /// </summary>
    static CharacterPart? Boot(CharacterAppearance look) =>
        look.Parts.FirstOrDefault(static part => part.Slot == AttachSlot.Boots && part.Record.Type == FcsRecordType.Armour);

    /// <summary>
    /// The leg fitted on that side (LIMB_REPLACEMENT <c>slot</c> 52 left, 53 right: a prosthetic, or a Human or Hive leg); null for the body's own.
    /// </summary>
    static CharacterPart? Prosthetic(CharacterAppearance look, bool right)
    {
        int slot = right ? 53 : 52;
        return look.Parts.FirstOrDefault(part => part.Record.Type == FcsRecordType.LimbReplacement && part.Record.GetInt("slot") == slot);
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
    /// Follows the walker's position; a jump longer than <see cref="MaxMovement"/> is a relocation (a teleport, a load), which dries its soles.
    /// </summary>
    static void Follow(Trail trail, CharacterSnapshot actor)
    {
        var from = trail.Position;
        trail.Position = actor.Position;
        if (Xz(actor.Position - from).Length() > MaxMovement || MathF.Abs(actor.Position.Y - from.Y) > MaxMovement)
            trail.Left = trail.RightSole = default;
    }

    /// <summary>
    /// Wets the contacting foot inside the source or leaves a smaller deposit on dry ground.
    /// </summary>
    void Deposit(TerrainDecalStore store, Trail trail, bool right, Vector3 at, Vector2 across, CharacterPart? boot, CharacterAppearance look, double now)
    {
        ref Sole sole = ref (right ? ref trail.RightSole : ref trail.Left);
        var point = Xz(at);
        if (!OnTerrain(at))
        {
            if (at.Y < WorldWater.Height + .5f) sole = default;
            return;
        }
        var source = SourceColour(store, point);
        if (source is null && !Wet(sole, now)) return;

        // A boot prints its sole's outline. A foot, the body's or a fitted leg's, prints its skin sole, else (a robot's frame) its outline; with no
        // mesh, the generic mask: a boot's for a boot or a prosthetic, the bare one for a natural foot.
        bool bootOnThisFoot = boot is not null && boot.HideSide != (right ? 2 : 1);
        var leg = Prosthetic(look, right);
        string? mesh = host.Assets.FindMesh(bootOnThisFoot ? boot!.Mesh : leg?.Mesh ?? look.BodyMesh);
        if (source is { } colour)
        {
            sole = Sole.Wet(colour, now);
            if (mesh is not null) host.Gpu.Decals?.PrepareFootprint(mesh, bare: !bootOnThisFoot);
            if (leg is not null && host.Assets.FindMesh(look.BodyMesh) is { } own) host.Gpu.Decals?.PrepareFootprint(own, bare: true);
            return;
        }

        uint alpha = (uint)(50 + 45 * sole.Steps);
        byte variant = (byte)((bootOnThisFoot || leg is not null ? 0 : 2) + (right ? 1 : 0));
        at.Y = host.Gpu.Terrain.HeightAt(point.X, point.Y);
        host.Gpu.Decals?.AddFootprint(at, across, alpha << 24 | sole.Colour, PrintLife, variant, mesh, skin: !bootOnThisFoot,
            host.Assets.FindMesh(look.BodyMesh));
        sole.Steps--;
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
