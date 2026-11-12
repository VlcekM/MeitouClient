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
/// The animations' ground effects in the water (docs/audio.md "Ground effect"): an ANIMATION_EVENT with <c>ground effect</c> set fires, at its time in the clip, the BIOMES record's
/// <c>water effects</c> entry for the bone's depth and the character's speed at the water surface, as the original does. With the <c>contactfoam</c> switch a foot that lands in the
/// water also splashes the wave field (a crown, rings and foam), and one in water too shallow to cover its ankle bone throws the shallow splash the game leaves out there.
/// Display only. The dry ground's dust (the biome's <c>ground effects</c>) is not done.
/// </summary>
sealed partial class GameHost
{
    /// <summary>One ground event on a clip: the ANIMATION_EVENT's record, the bone it is fired at (empty: the character's position) and its time in the clip (value x 0.01).</summary>
    sealed record GroundEvent(string Record, string Bone, float Fraction) : IAnimationEvent;

    /// <summary>One <c>water effects</c> entry of a biome: the depth (v0) and speed (v1) it needs at least, and the EFFECT's particle system.</summary>
    readonly record struct WaterEffect(int Depth, int Speed, string System);

    /// <summary>The camera's frustum is widened by this (units) for the test (<c>FUN_1406ae7b0</c> with the constant 100 at <c>DAT_141683250</c>, <b>Verified</b>).</summary>
    const float SplashFrustumMargin = 100;

    /// <summary>
    /// Characters further from the eye than this fire no ground effects: the original has no distance (its pool and the frustum limit it); far splashes are under a pixel.
    /// </summary>
    const float SplashReach = 1100;

    /// <summary>How long a fired system's unit emits: its emitters stop at their own <c>duration</c> first (2.5 s at most in the base game's water effects).</summary>
    const float SplashEmitSeconds = 3;

    /// <summary>The <c>contactfoam</c> splash: how high above the water a foot's ground may lie and still be wet, and the deepest foot bone that splashes the field.</summary>
    const float WetGround = 0.05f, SplashDeepest = 6;

    GroundEvent[]?[]? _definitionGround;
    bool[]? _definitionAnyEvents;
    Dictionary<uint, WaterEffect[]>? _waterEffects;
    WaterEffect[] _emptyBiomeWater = [];
    readonly Dictionary<long, Splasher> _splashers = [];
    World? _splashesFor;
    int _splashFrame;
    Vector4[] _splashPlanes = [];

    sealed class Splasher
    {
        public readonly List<(int Definition, AnimationEventCursor Cursor, int Frame)> Cursors = [];
        public int Frame;
    }

    /// <summary>Once a frame, after the wave field's bodies (<see cref="UpdateWakes"/>): the ground events the characters' dominant clips passed since the last frame.</summary>
    void UpdateSplashes()
    {
        if (Gpu.Particles is not { } particles || Scene.Database is not { } db || AnimationLibrary is not { } library || Gpu.Characters is not { } characters) return;
        if (_definitionGround is null) LoadGroundEvents(db, library, particles);
        if (!ReferenceEquals(_splashesFor, Session.World))
        {
            _splashesFor = Session.World;
            _splashers.Clear();
        }
        _splashFrame++;
        _splashPlanes = Meitou.Game.Rendering.Frame.WorldCamera.FrustumPlanes(Camera.View * Camera.Projection(_frameAspect, Camera.Near, Camera.ViewDistance));
        for (int i = 0; i < _splashPlanes.Length; i++) _splashPlanes[i] /= new Vector3(_splashPlanes[i].X, _splashPlanes[i].Y, _splashPlanes[i].Z).Length();
        IndexPrevious();
        var eye = Camera.Eye;
        float alpha = Session.SimulationAlpha, tick = Session.World.TickSeconds;
        foreach (var c in Session.CurrentSnapshot.Characters)
        {
            if (c.Appearance is null || Vector3.DistanceSquared(c.Position, eye) > SplashReach * SplashReach) continue;
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
            // Only the heaviest clip that carries events fires them (FUN_1405b5c30), the same cursor as the sounds' (FUN_140064690: one list, each event its own fire).
            int dominant = -1;
            for (int k = 0; k < c.Animations.Count; k++)
            {
                int d = c.Animations[k].Definition;
                if ((uint)d < (uint)_definitionAnyEvents!.Length && _definitionAnyEvents[d] && (dominant < 0 || c.Animations[k].Weight > c.Animations[dominant].Weight)) dominant = k;
            }
            for (int k = 0; k < c.Animations.Count; k++)
            {
                var layer = c.Animations[k];
                if ((uint)layer.Definition >= (uint)_definitionGround!.Length || _definitionGround[layer.Definition] is not { } events) continue;
                int at = s.Cursors.FindIndex(x => x.Definition == layer.Definition);
                if (at < 0)
                {
                    s.Cursors.Add((layer.Definition, default, 0));
                    at = s.Cursors.Count - 1;
                }
                var cursor = s.Cursors[at].Cursor;
                cursor.Update(events, Math.Clamp(Audio.GameSound.ClipFraction(layer, library.Definitions[layer.Definition].Loop), 0, 1), k == dominant,
                    ev => GroundEffect(particles, characters, key, ev, position, speed));
                s.Cursors[at] = (layer.Definition, cursor, _splashFrame);
            }
            s.Cursors.RemoveAll(x => x.Frame != _splashFrame);
        }
        if (_splashFrame % 256 == 0)
            foreach (var (key, s) in _splashers)
                if (s.Frame != _splashFrame) _splashers.Remove(key);
    }

    /// <summary>
    /// One ground event (<c>FUN_140065330</c>): at the bone (the character's position without one), under the water the biome's entry for the depth and speed, at the surface;
    /// only inside the frustum (<c>FUN_140a08c00</c>). With <c>contactfoam</c> a foot on wet ground splashes the wave field too.
    /// </summary>
    void GroundEffect(ParticleRenderer particles, Meitou.Game.Rendering.Characters.CharacterRenderer characters, long key, GroundEvent ev, Vector3 position, float speed)
    {
        var point = ev.Bone.Length > 0 && characters.BonePosition(key, ev.Bone) is { } bone && bone.Y != 0 ? bone : position;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return;
        if (ev.Bone.Length > 0 && PuddleSplash(particles, point, speed)) return;
        var surface = point with { Y = WorldWater.Height };
        if (!InSplashFrustum(surface)) return;
        float depth = WorldWater.Height - point.Y;
        string? fired = depth >= 0 ? WaterEffectAt(point, depth, speed) : null;
        if (fired is not null) particles.Burst(fired, surface, Quaternion.Identity, SplashEmitSeconds);
        if (!WaterContact.Enabled || ev.Bone.Length == 0 || depth > SplashDeepest) return;
        float ground = Gpu.Terrain.HeightAt(point.X, point.Z);
        if (!float.IsFinite(ground) || ground > WorldWater.Height - WetGround || point.Y - ground > 4) return;   // dry, or a floor above the water
        float strength = Math.Clamp(speed / 50, 0.5f, 1) * Math.Clamp(1 - depth / SplashDeepest, 0.3f, 1);
        Gpu.Water?.Ripples?.AddSplash(new Vector2(point.X, point.Z), strength);
        // Water too shallow to reach the ankle bone: the game shows nothing; the biome's shallowest splash.
        if (fired is null && WaterEffectAt(point, 0, speed) is { } shallow) particles.Burst(shallow, surface, Quaternion.Identity, SplashEmitSeconds);
    }

    /// <summary>
    /// A foot's ground event in one of the wet world's puddles (Meitou, the "Rain drops and puddles" row; <see cref="WetPuddles"/>): the biome's
    /// shallowest splash at the ground and rings in the puddle (<see cref="WeatherSurfaces.AddSplash"/>). False when the foot is in none.
    /// </summary>
    bool PuddleSplash(ParticleRenderer particles, Vector3 point, float speed)
    {
        if (Gpu.Surfaces is not { WetWorld: true } wet || wet.Wetness <= 0.2f || point.Y <= WorldWater.Height + 2) return false;
        float ground = Gpu.Terrain.HeightAt(point.X, point.Z);
        if (!float.IsFinite(ground) || MathF.Abs(point.Y - ground) > 4) return false;   // on a floor, not on the ground
        const float step = 2;
        float dx = (Gpu.Terrain.HeightAt(point.X + step, point.Z) - Gpu.Terrain.HeightAt(point.X - step, point.Z)) / (2 * step);
        float dz = (Gpu.Terrain.HeightAt(point.X, point.Z + step) - Gpu.Terrain.HeightAt(point.X, point.Z - step)) / (2 * step);
        if (!float.IsFinite(dx) || !float.IsFinite(dz) || !WetPuddles.Under(point.X, point.Z, wet.Wetness, 1 / MathF.Sqrt(1 + dx * dx + dz * dz))) return false;
        var at = new Vector3(point.X, ground, point.Z);
        if (!InSplashFrustum(at)) return true;
        wet.AddSplash(new Vector2(point.X, point.Z), Math.Clamp(speed / 50, 0.5f, 1));
        if (WaterEffectAt(point, 0, speed) is { } shallow) particles.Burst(shallow, at, Quaternion.Identity, SplashEmitSeconds);
        return true;
    }

    /// <summary>The biome's <c>water effects</c> entry with the greatest depth at most <paramref name="depth"/>, then the greatest speed at most <paramref name="speed"/>; null for none.</summary>
    string? WaterEffectAt(Vector3 point, float depth, float speed)
    {
        var effects = Sound.Ground?.BiomeAt(point.X, point.Z) is { } biome && _waterEffects!.TryGetValue(biome, out var found) ? found : _emptyBiomeWater;
        int bestDepth = -1, bestSpeed = -1;
        string? system = null;
        foreach (var e in effects)
            if (e.Depth <= depth && e.Speed <= speed && (e.Depth > bestDepth || (e.Depth == bestDepth && e.Speed > bestSpeed)))
                (bestDepth, bestSpeed, system) = (e.Depth, e.Speed, e.System);
        return system;
    }

    bool InSplashFrustum(Vector3 p)
    {
        foreach (var plane in _splashPlanes)
            if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W < -SplashFrustumMargin) return false;
        return true;
    }

    /// <summary>
    /// The clips' ground events (the <c>events</c> of ANIMATION, ANIMAL_ANIMATION and COMBAT_TECHNIQUE records whose ANIMATION_EVENT has <c>ground effect</c>, with its <c>bone</c>;
    /// <c>FUN_140064e10</c> / <c>FUN_140064d30</c>) and the biomes' water effects (the record without a biome colour: <c>EMPTY</c>, <c>FUN_140a0ac00</c>).
    /// </summary>
    void LoadGroundEvents(GameDatabase db, Meitou.Data.Gameplay.AnimationLibrary library, ParticleRenderer particles)
    {
        var animationSounds = Sound.AnimationSoundsOf(db);
        Sound.GroundSurfacesOf(db);
        var ground = new GroundEvent[]?[library.Definitions.Count];
        var any = new bool[ground.Length];
        for (int i = 0; i < ground.Length; i++)
        {
            List<GroundEvent>? list = null;
            if (db.Find(library.Definitions[i].Id) is { } record)
                foreach (var reference in record.GetReferences("events"))
                {
                    if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.AnimationEvent } def || !def.GetBool("ground effect")) continue;
                    var ev = new GroundEvent(def.StringId, def.GetString("bone"), reference.Values.Value0 * 0.01f);
                    list ??= [];
                    // A sorted insert that skips the same event at the same time (FUN_140064870).
                    int at = 0;
                    for (; at < list.Count && list[at].Fraction <= ev.Fraction; at++) { }
                    if (!list.Any(x => x.Record == ev.Record && x.Fraction == ev.Fraction)) list.Insert(at, ev);
                }
            ground[i] = list is null ? null : [.. list];
            any[i] = list is not null || animationSounds.Of(library.Definitions[i].Id) is { Length: > 0 };
        }
        (_definitionGround, _definitionAnyEvents) = (ground, any);

        _waterEffects = [];
        var systems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in db.OfType(FcsRecordType.Biomes).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            var list = new List<WaterEffect>();
            foreach (var reference in r.GetReferences("water effects"))
                if (db.Find(reference.TargetStringId) is { Type: FcsRecordType.Effect } effect && EffectRecord.From(effect, db).ParticleSystem is { Length: > 0 } system)
                {
                    list.Add(new WaterEffect(reference.Values.Value0, reference.Values.Value1, system));
                    systems.Add(system);
                }
            _waterEffects.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, [.. list]);
            if (r.StringId == "EMPTY" || r.Name == "EMPTY") _emptyBiomeWater = [.. list];
        }
        particles.PrepareBursts([.. systems]);
        Console.WriteLine($"water     splashes: {ground.Count(g => g is not null)} clips with ground events; water effects {string.Join(", ", _waterEffects.Values.SelectMany(x => x).Distinct().Select(e => $"{e.System} at depth {e.Depth} speed {e.Speed}"))}");
    }
}
