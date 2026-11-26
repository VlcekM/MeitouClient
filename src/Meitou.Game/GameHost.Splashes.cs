using Meitou.Game.Audio;
using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
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
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The animations' ground effects in the water (docs/audio.md "Ground effect"): an ANIMATION_EVENT with <c>ground effect</c> set fires, at its time in
/// the clip, the BIOMES record's <c>water effects</c> entry for the bone's depth and the character's speed at the water surface, as the original does.
/// With the <c>contactfoam</c> switch a foot that lands in the water also splashes the wave field (a crown, rings and foam), and one in water too
/// shallow to cover its ankle bone throws the shallow splash the game leaves out there. On dry ground it fires the biome's <c>ground effects</c>
/// (the dust kicks, <see cref="Effects.DryGroundEffects"/>). A machine's ground events ride on the clip its operator works with. Display only.
/// </summary>
sealed partial class GameHost
{
    /// <summary>
    /// The camera's frustum is widened by this (units) for the test (<c>FUN_1406ae7b0</c> with the constant 100 at <c>DAT_141683250</c>,
    /// <b>Verified</b>).
    /// </summary>
    const float SplashFrustumMargin = 100;

    /// <summary>
    /// Characters further from the eye than this fire no ground effects: the original has no distance (its pool and the frustum limit it); far
    /// splashes are under a pixel.
    /// </summary>
    const float SplashReach = 1100;

    /// <summary>
    /// How long a fired system's unit emits: its emitters stop at their own <c>duration</c> first (2.5 s at most in the base game's water effects).
    /// </summary>
    const float SplashEmitSeconds = 3;

    /// <summary>
    /// The <c>contactfoam</c> splash: how high above the water a foot's ground may lie and still be wet, and the deepest foot bone that splashes the
    /// field.
    /// </summary>
    const float WetGround = 0.05f, SplashDeepest = 6;

    /// <summary>
    /// Each animation definition's ground events, sorted by time; null for one without. Null until <see cref="LoadGroundEvents"/> ran.
    /// </summary>
    GroundEvent[]?[]? _definitionGround;

    /// <summary>
    /// Whether each animation definition carries any event (ground or sound): only the heaviest such clip fires.
    /// </summary>
    bool[]? _definitionAnyEvents;

    /// <summary>
    /// Each BUILDING_FUNCTIONALITY's ground events (its <c>animation events</c>, the farms' <c>Impact Farm</c>), in time order, by string id; a
    /// functionality without any is left out.
    /// </summary>
    Dictionary<string, GroundEvent[]> _workGround = [];

    /// <summary>
    /// A clip's ground events with a machine's put on it, by functionality and animation definition.
    /// </summary>
    readonly Dictionary<(string Functionality, int Definition), GroundEvent[]> _mergedGround = [];

    /// <summary>
    /// Each biome's water effects, by its 24-bit index.
    /// </summary>
    Dictionary<uint, WaterEffect[]>? _waterEffects;

    /// <summary>
    /// The water effects of the biome record without a biome colour (<c>EMPTY</c>), used where the ground has no biome.
    /// </summary>
    WaterEffect[] _emptyBiomeWater = [];

    /// <summary>
    /// Each character's event cursors, by draw key.
    /// </summary>
    readonly Dictionary<long, Splasher> _splashers = [];

    /// <summary>
    /// The world the splashers were made in; a new world clears them.
    /// </summary>
    World? _splashesFor;

    /// <summary>
    /// The count of frames the splashes were updated in, which tells a cursor or splasher seen this frame from a stale one.
    /// </summary>
    int _splashFrame;

    /// <summary>
    /// The camera's frustum planes this frame, normalised, for <see cref="InSplashFrustum"/>.
    /// </summary>
    Vector4[] _splashPlanes = [];

    /// <summary>
    /// One ground event on a clip: the ANIMATION_EVENT's record, the bone it is fired at (empty: the character's position) and its time in the clip
    /// (value x 0.01).
    /// </summary>
    sealed record GroundEvent(string Record, string Bone, float Fraction) : IAnimationEvent;

    /// <summary>
    /// One <c>water effects</c> entry of a biome: the depth (v0) and speed (v1) it needs at least, and the EFFECT's particle system.
    /// </summary>
    readonly record struct WaterEffect(int Depth, int Speed, string System);

    /// <summary>
    /// One character's ground-event cursors, one per clip it plays.
    /// </summary>
    sealed class Splasher
    {
        /// <summary>
        /// Each clip's cursor, the splash frame it was last updated in and the events it walks (a machine's added change them).
        /// </summary>
        public readonly List<(int Definition, AnimationEventCursor Cursor, int Frame, GroundEvent[] Events)> Cursors = [];

        /// <summary>
        /// The splash frame the character was last seen in.
        /// </summary>
        public int Frame;
    }

    /// <summary>
    /// Once a frame, after the wave field's bodies (<see cref="UpdateWakes"/>): the ground events the characters' dominant clips passed since the last
    /// frame.
    /// </summary>
    internal void UpdateSplashes()
    {
        if (Gpu.Particles is not { } particles || Scene.Database is not { } db || AnimationLibrary is not { } library
            || Gpu.Characters is not { } characters)
            return;

        if (_definitionGround is null) LoadGroundEvents(db, library, particles);
        if (!ReferenceEquals(_splashesFor, Session.World))
        {
            _splashesFor = Session.World;
            _splashers.Clear();
        }

        _splashFrame++;
        DryGround.Advance();
        PrepareSplashFrustum();
        IndexPrevious();
        var eye = Camera.Eye;

        // The speed is the distance between the two snapshots over the time between them: a frame can follow more than one tick.
        long ticks = Math.Max(Session.CurrentSnapshot.Tick - Session.PreviousSnapshot.Tick, 1);
        float alpha = Session.SimulationAlpha, tick = Session.World.TickSeconds * ticks;
        foreach (var c in Session.CurrentSnapshot.Characters) SplashCharacter(particles, characters, library, c, eye, alpha, tick);

        if (_splashFrame % 256 == 0)
            foreach (var (key, s) in _splashers)
                if (s.Frame != _splashFrame) _splashers.Remove(key);
    }

    /// <summary>
    /// The camera's frustum planes for this frame, each normalised by the length of its normal.
    /// </summary>
    void PrepareSplashFrustum()
    {
        var viewProjection = Camera.View * Camera.Projection(_frameAspect, Camera.Near, Camera.ViewDistance);
        _splashPlanes = Meitou.Game.Rendering.Frame.WorldCamera.FrustumPlanes(viewProjection);
        for (int i = 0; i < _splashPlanes.Length; i++)
            _splashPlanes[i] /= new Vector3(_splashPlanes[i].X, _splashPlanes[i].Y, _splashPlanes[i].Z).Length();
    }

    /// <summary>
    /// One character within <see cref="SplashReach"/> of the eye: moves its clips' cursors to their fractions this frame, firing the dominant clip's
    /// ground events at its interpolated position and ground speed; drops the cursors of clips it no longer plays.
    /// </summary>
    void SplashCharacter(
        ParticleRenderer particles, Meitou.Game.Rendering.Characters.CharacterRenderer characters,
        Meitou.Data.Gameplay.AnimationLibrary library, CharacterSnapshot c, Vector3 eye, float alpha, float tick)
    {
        if (c.Appearance is null || Vector3.DistanceSquared(c.Position, eye) > SplashReach * SplashReach) return;

        var position = c.Position;
        float speed = 0;
        if (PreviousById.TryGetValue(c.Id, out var before))
        {
            position = Meitou.Simulation.Session.Interp.Lerp(before.Position, c.Position, alpha);
            speed = new Vector2(c.Position.X - before.Position.X, c.Position.Z - before.Position.Z).Length() / tick;
        }

        long key = DrawKey(c.Id);
        ref var s = ref CollectionsMarshal.GetValueRefOrAddDefault(_splashers, key, out _);
        s ??= new Splasher();
        s.Frame = _splashFrame;
        int worked = WorkedDefinition(c);
        int dominant = DominantEventClip(c, worked);
        for (int k = 0; k < c.Animations.Count; k++)
        {
            var layer = c.Animations[k];
            if (ClipGround(c, layer.Definition, worked) is not { } events) continue;

            int at = s.Cursors.FindIndex(x => x.Definition == layer.Definition);
            if (at < 0)
            {
                s.Cursors.Add((layer.Definition, default, 0, events));
                at = s.Cursors.Count - 1;
            }

            var cursor = s.Cursors[at].Cursor;
            if (!ReferenceEquals(s.Cursors[at].Events, events)) cursor.Rebase(events);

            float fraction = Math.Clamp(Audio.GameSound.ClipFraction(layer, library.Definitions[layer.Definition].Loop), 0, 1);
            cursor.Update(events, fraction, k == dominant, ev => GroundEffect(particles, characters, key, ev, position, speed));
            s.Cursors[at] = (layer.Definition, cursor, _splashFrame, events);
        }

        s.Cursors.RemoveAll(x => x.Frame != _splashFrame);
    }

    /// <summary>
    /// The index of the character's heaviest clip that carries events (the <paramref name="worked"/> clip does, with its machine's), or -1: only it
    /// fires them (FUN_1405b5c30), the same cursor as the sounds' (FUN_140064690: one list, each event its own fire).
    /// </summary>
    int DominantEventClip(CharacterSnapshot c, int worked)
    {
        int dominant = -1;
        for (int k = 0; k < c.Animations.Count; k++)
        {
            int d = c.Animations[k].Definition;
            bool events = d == worked || (uint)d < (uint)_definitionAnyEvents!.Length && _definitionAnyEvents[d];
            if (events && (dominant < 0 || c.Animations[k].Weight > c.Animations[dominant].Weight)) dominant = k;
        }

        return dominant;
    }

    /// <summary>
    /// The definition of the clip a working character's machine adds its ground events to (<c>Task_OperateMachine</c>, <c>FUN_140065280</c>: the
    /// snapshot's <see cref="WorkEvents"/>); -1 when it works no machine with any, or plays no such clip.
    /// </summary>
    int WorkedDefinition(CharacterSnapshot c) =>
        c.WorkEvents is { } work && _workGround.ContainsKey(work.Functionality) ? CharacterAudio.LayerOf(c, work.Clip) : -1;

    /// <summary>
    /// The ground events a clip fires: its record's, with the machine's put on the <paramref name="worked"/> clip; null for none.
    /// </summary>
    GroundEvent[]? ClipGround(CharacterSnapshot c, int definition, int worked)
    {
        if ((uint)definition >= (uint)_definitionGround!.Length) return null;

        var events = _definitionGround[definition];
        if (definition != worked) return events;

        string functionality = c.WorkEvents!.Value.Functionality;
        if (_mergedGround.TryGetValue((functionality, definition), out var merged)) return merged;

        var list = new List<GroundEvent>(events ?? []);
        foreach (var ev in _workGround[functionality]) InsertGround(list, ev);
        return _mergedGround[(functionality, definition)] = [.. list];
    }

    /// <summary>
    /// A sorted insert that skips the same event at the same time (FUN_140064870).
    /// </summary>
    static void InsertGround(List<GroundEvent> list, GroundEvent ev)
    {
        if (list.Any(x => x.Record == ev.Record && x.Fraction == ev.Fraction)) return;

        int at = 0;
        for (; at < list.Count && list[at].Fraction <= ev.Fraction; at++) { }
        list.Insert(at, ev);
    }

    /// <summary>
    /// One ground event (<c>FUN_140065330</c>): at the bone (the character's position without one), under the water the biome's entry for the depth and
    /// speed, at the surface; only inside the frustum (<c>FUN_140a08c00</c>). With <c>contactfoam</c> a foot on wet ground splashes the wave field too.
    /// </summary>
    void GroundEffect(
        ParticleRenderer particles, Meitou.Game.Rendering.Characters.CharacterRenderer characters, long key, GroundEvent ev, Vector3 position,
        float speed)
    {
        var point = ev.Bone.Length > 0 && characters.BonePosition(key, ev.Bone) is { } bone && bone.Y != 0 ? bone : position;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return;
        if (ev.Bone.Length > 0 && PuddleSplash(particles, point, speed)) return;

        // Above the water the game's dry half (dust); with contactfoam a foot over wet ground takes the shallow splash instead.
        float depth = WorldWater.Height - point.Y;
        bool foam = WaterContact.Enabled && ev.Bone.Length > 0 && depth <= SplashDeepest && WetGroundUnder(point);
        if (depth < 0 && !foam)
        {
            DryKick(particles, point, ev.Bone.Length > 0, position, speed);
            return;
        }

        var surface = point with { Y = WorldWater.Height };
        if (!InSplashFrustum(surface)) return;

        string? fired = depth >= 0 ? WaterEffectAt(point, depth, speed) : null;
        if (fired is not null) particles.Burst(fired, surface, Quaternion.Identity, SplashEmitSeconds, scatters: true);
        if (!foam) return;

        float strength = Math.Clamp(speed / 50, 0.5f, 1) * Math.Clamp(1 - depth / SplashDeepest, 0.3f, 1);
        Gpu.Water?.Ripples?.AddSplash(new Vector2(point.X, point.Z), strength);

        // Water too shallow to reach the ankle bone: the game shows nothing; the biome's shallowest splash.
        if (fired is null && WaterEffectAt(point, 0, speed) is { } shallow)
            particles.Burst(shallow, surface, Quaternion.Identity, SplashEmitSeconds, scatters: true);
    }

    /// <summary>
    /// The terrain under a foot lies under the water (by <see cref="WetGround"/>) within 4 units below it: not dry ground, nor a floor above the water.
    /// </summary>
    bool WetGroundUnder(Vector3 point)
    {
        float ground = Gpu.Terrain.HeightAt(point.X, point.Z);
        return float.IsFinite(ground) && ground <= WorldWater.Height - WetGround && point.Y - ground <= 4;
    }

    /// <summary>
    /// A ground event above the water: the biome's dust kick there (<see cref="Effects.DryGroundEffects.Pick"/>), only inside the widened frustum
    /// (<c>FUN_140a08c00</c>).
    /// </summary>
    void DryKick(ParticleRenderer particles, Vector3 point, bool bone, Vector3 position, float speed)
    {
        if (DryGround.Pick(point, bone, position, speed) is { } kick && InSplashFrustum(kick.At))
            DryGround.Fire(particles, kick.System, kick.At);
    }

    /// <summary>
    /// A foot's ground event in one of the wet world's puddles (Meitou, the "Rain drops and puddles" row; <see cref="WetPuddles"/>): the biome's
    /// shallowest splash at the ground and rings in the puddle (<see cref="WeatherSurfaces.AddSplash"/>). False when the foot is in none.
    /// </summary>
    bool PuddleSplash(ParticleRenderer particles, Vector3 point, float speed)
    {
        if (!InPuddle(point) || Gpu.Surfaces is not { } wet) return false;

        var at = new Vector3(point.X, Gpu.Terrain.HeightAt(point.X, point.Z), point.Z);
        if (!InSplashFrustum(at)) return true;

        wet.AddSplash(new Vector2(point.X, point.Z), Math.Clamp(speed / 50, 0.5f, 1));
        if (WaterEffectAt(point, 0, speed) is { } shallow) particles.Burst(shallow, at, Quaternion.Identity, SplashEmitSeconds, scatters: true);
        return true;
    }

    /// <summary>
    /// A point on the ground (a foot, a character's position) lies in one of the wet world's puddles: the row is on, the ground is wet, above the water
    /// line, the point within 4 units of the terrain (not on a floor), and <see cref="WetPuddles.Under"/> finds water there. The footsteps' sound asks
    /// too.
    /// </summary>
    internal bool InPuddle(Vector3 point)
    {
        if (Gpu?.Surfaces is not { WetWorld: true } wet || wet.Wetness <= 0.2f || point.Y <= WorldWater.Height + 2) return false;

        float ground = Gpu.Terrain.HeightAt(point.X, point.Z);
        if (!float.IsFinite(ground) || MathF.Abs(point.Y - ground) > 4) return false;

        const float step = 2;
        float dx = (Gpu.Terrain.HeightAt(point.X + step, point.Z) - Gpu.Terrain.HeightAt(point.X - step, point.Z)) / (2 * step);
        float dz = (Gpu.Terrain.HeightAt(point.X, point.Z + step) - Gpu.Terrain.HeightAt(point.X, point.Z - step)) / (2 * step);
        return float.IsFinite(dx) && float.IsFinite(dz)
            && WetPuddles.Under(
                point.X, point.Z, wet.Wetness, 1 / MathF.Sqrt(1 + dx * dx + dz * dz), WetPuddles.Hollow(Gpu.Terrain.HeightAt, point.X, point.Z));
    }

    /// <summary>
    /// The biome's <c>water effects</c> entry with the greatest depth at most <paramref name="depth"/>, then the greatest speed at most
    /// <paramref name="speed"/>; null for none.
    /// </summary>
    string? WaterEffectAt(Vector3 point, float depth, float speed)
    {
        var effects = Sound.Ground?.BiomeAt(point.X, point.Z) is { } biome && _waterEffects!.TryGetValue(biome, out var found)
            ? found
            : _emptyBiomeWater;
        int bestDepth = -1, bestSpeed = -1;
        string? system = null;
        foreach (var e in effects)
        {
            if (e.Depth <= depth && e.Speed <= speed && (e.Depth > bestDepth || (e.Depth == bestDepth && e.Speed > bestSpeed)))
                (bestDepth, bestSpeed, system) = (e.Depth, e.Speed, e.System);
        }

        return system;
    }

    /// <summary>
    /// Whether a point lies inside this frame's camera frustum widened by <see cref="SplashFrustumMargin"/>.
    /// </summary>
    bool InSplashFrustum(Vector3 p)
    {
        foreach (var plane in _splashPlanes)
        {
            if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W < -SplashFrustumMargin) return false;
        }

        return true;
    }

    /// <summary>
    /// The clips' ground events (the <c>events</c> of ANIMATION, ANIMAL_ANIMATION and COMBAT_TECHNIQUE records whose ANIMATION_EVENT has
    /// <c>ground effect</c>, with its <c>bone</c>; <c>FUN_140064e10</c> / <c>FUN_140064d30</c>) and the biomes' water effects (the record without a
    /// biome colour: <c>EMPTY</c>, <c>FUN_140a0ac00</c>).
    /// </summary>
    void LoadGroundEvents(GameDatabase db, Meitou.Data.Gameplay.AnimationLibrary library, ParticleRenderer particles)
    {
        var animationSounds = Sound.AnimationSoundsOf(db);
        Sound.GroundSurfacesOf(db);
        var ground = new GroundEvent[]?[library.Definitions.Count];
        var any = new bool[ground.Length];
        for (int i = 0; i < ground.Length; i++)
        {
            var list = ClipGroundEvents(db, library.Definitions[i]);
            ground[i] = list is null ? null : [.. list];
            any[i] = list is not null || animationSounds.Of(library.Definitions[i].Id) is { Length: > 0 };
        }

        (_definitionGround, _definitionAnyEvents) = (ground, any);

        var systems = LoadWaterEffects(db);
        systems.UnionWith(DryGround.Load(db));
        _workGround = LoadWorkGround(db);
        _mergedGround.Clear();
        particles.PrepareBursts([.. systems]);
        int clips = ground.Count(g => g is not null);
        var described = _waterEffects!.Values
            .SelectMany(x => x)
            .Distinct()
            .Select(e => $"{e.System} at depth {e.Depth} speed {e.Speed}");
        Console.WriteLine($"water     splashes: {clips} clips with ground events; water effects {string.Join(", ", described)}");
    }

    /// <summary>
    /// One clip's ground events in time order; null when its record has none.
    /// </summary>
    static List<GroundEvent>? ClipGroundEvents(GameDatabase db, Meitou.Data.Gameplay.AnimationDefinition definition)
    {
        List<GroundEvent>? list = null;
        if (db.Find(definition.Id) is not { } record) return list;

        foreach (var reference in record.GetReferences("events"))
        {
            if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.AnimationEvent } def || !def.GetBool("ground effect")) continue;

            InsertGround(list ??= [], new GroundEvent(def.StringId, def.GetString("bone"), reference.Values.Value0 * 0.01f));
        }

        return list;
    }

    /// <summary>
    /// Each BUILDING_FUNCTIONALITY's ground events: the ANIMATION_EVENTs with <c>ground effect</c> among its <c>animation events</c> (value x 0.01 the
    /// time in the operator's clip), in time order; functionalities without any are left out.
    /// </summary>
    static Dictionary<string, GroundEvent[]> LoadWorkGround(GameDatabase db)
    {
        var work = new Dictionary<string, GroundEvent[]>(StringComparer.Ordinal);
        foreach (var record in db.OfType(FcsRecordType.BuildingFunctionality))
        {
            var list = new List<GroundEvent>();
            foreach (var reference in record.GetReferences("animation events"))
            {
                if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.AnimationEvent } def || !def.GetBool("ground effect")) continue;
                InsertGround(list, new GroundEvent(def.StringId, def.GetString("bone"), reference.Values.Value0 * 0.01f));
            }

            if (list.Count > 0) work.TryAdd(record.StringId, [.. list]);
        }

        return work;
    }

    /// <summary>
    /// Fills <see cref="_waterEffects"/> and <see cref="_emptyBiomeWater"/> from the biome records; returns the particle systems they fire.
    /// </summary>
    HashSet<string> LoadWaterEffects(GameDatabase db)
    {
        _waterEffects = [];
        var systems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in db.OfType(FcsRecordType.Biomes).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            var list = new List<WaterEffect>();
            foreach (var reference in r.GetReferences("water effects"))
            {
                if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.Effect } effect
                    || EffectRecord.From(effect, db).ParticleSystem is not { Length: > 0 } system)
                    continue;

                list.Add(new WaterEffect(reference.Values.Value0, reference.Values.Value1, system));
                systems.Add(system);
            }

            _waterEffects.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, [.. list]);
            if (r.StringId == "EMPTY" || r.Name == "EMPTY") _emptyBiomeWater = [.. list];
        }

        return systems;
    }
}
