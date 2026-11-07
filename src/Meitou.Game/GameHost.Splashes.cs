using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Audio;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Rendering;
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

    GroundEvent[]?[]? definitionGround;
    bool[]? definitionAnyEvents;
    Dictionary<uint, WaterEffect[]>? waterEffects;
    WaterEffect[] emptyBiomeWater = [];
    readonly Dictionary<long, Splasher> splashers = [];
    World? splashesFor;
    int splashFrame;
    Vector4[] splashPlanes = [];

    sealed class Splasher
    {
        public readonly List<(int Definition, AnimationEventCursor Cursor, int Frame)> Cursors = [];
        public int Frame;
    }

    /// <summary>Once a frame, after the wave field's bodies (<see cref="UpdateWakes"/>): the ground events the characters' dominant clips passed since the last frame.</summary>
    void UpdateSplashes()
    {
        if (gpu.Particles is not { } particles || scene.Database is not { } db || animationLibrary is not { } library || gpu.Characters is not { } characters) return;
        if (definitionGround is null) LoadGroundEvents(db, library, particles);
        if (!ReferenceEquals(splashesFor, session.World))
        {
            splashesFor = session.World;
            splashers.Clear();
        }
        splashFrame++;
        splashPlanes = Meitou.Rendering.WorldCamera.FrustumPlanes(camera.View * camera.Projection(birdAspect, camera.Near, camera.ViewDistance));
        for (int i = 0; i < splashPlanes.Length; i++) splashPlanes[i] /= new Vector3(splashPlanes[i].X, splashPlanes[i].Y, splashPlanes[i].Z).Length();
        IndexPrevious();
        var eye = camera.Eye;
        float alpha = session.SimulationAlpha, tick = session.World.TickSeconds;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            if (c.Appearance is null || Vector3.DistanceSquared(c.Position, eye) > SplashReach * SplashReach) continue;
            var position = c.Position;
            float speed = 0;
            if (previousById.TryGetValue(c.Id, out var before))
            {
                position = Meitou.Engine.Time.Interp.Lerp(before.Position, c.Position, alpha);
                speed = new Vector2(c.Position.X - before.Position.X, c.Position.Z - before.Position.Z).Length() / tick;
            }
            long key = DrawKey(c.Id);
            ref var s = ref CollectionsMarshal.GetValueRefOrAddDefault(splashers, key, out _);
            s ??= new Splasher();
            s.Frame = splashFrame;
            // Only the heaviest clip that carries events fires them (FUN_1405b5c30), the same cursor as the sounds' (FUN_140064690: one list, each event its own fire).
            int dominant = -1;
            for (int k = 0; k < c.Animations.Count; k++)
            {
                int d = c.Animations[k].Definition;
                if ((uint)d < (uint)definitionAnyEvents!.Length && definitionAnyEvents[d] && (dominant < 0 || c.Animations[k].Weight > c.Animations[dominant].Weight)) dominant = k;
            }
            for (int k = 0; k < c.Animations.Count; k++)
            {
                var layer = c.Animations[k];
                if ((uint)layer.Definition >= (uint)definitionGround!.Length || definitionGround[layer.Definition] is not { } events) continue;
                int at = s.Cursors.FindIndex(x => x.Definition == layer.Definition);
                if (at < 0)
                {
                    s.Cursors.Add((layer.Definition, default, 0));
                    at = s.Cursors.Count - 1;
                }
                var cursor = s.Cursors[at].Cursor;
                cursor.Update(events, Math.Clamp(ClipFraction(layer, library.Definitions[layer.Definition].Loop), 0, 1), k == dominant,
                    ev => GroundEffect(particles, characters, key, ev, position, speed));
                s.Cursors[at] = (layer.Definition, cursor, splashFrame);
            }
            s.Cursors.RemoveAll(x => x.Frame != splashFrame);
        }
        if (splashFrame % 256 == 0)
            foreach (var (key, s) in splashers)
                if (s.Frame != splashFrame) splashers.Remove(key);
    }

    /// <summary>
    /// One ground event (<c>FUN_140065330</c>): at the bone (the character's position without one), under the water the biome's entry for the depth and speed, at the surface;
    /// only inside the frustum (<c>FUN_140a08c00</c>). With <c>contactfoam</c> a foot on wet ground splashes the wave field too.
    /// </summary>
    void GroundEffect(ParticleRenderer particles, Meitou.Rendering.Characters.CharacterRenderer characters, long key, GroundEvent ev, Vector3 position, float speed)
    {
        var point = ev.Bone.Length > 0 && characters.BonePosition(key, ev.Bone) is { } bone && bone.Y != 0 ? bone : position;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)) return;
        var surface = point with { Y = WorldWater.Height };
        if (!InSplashFrustum(surface)) return;
        float depth = WorldWater.Height - point.Y;
        string? fired = depth >= 0 ? WaterEffectAt(point, depth, speed) : null;
        if (fired is not null) particles.Burst(fired, surface, Quaternion.Identity, SplashEmitSeconds);
        if (!WaterContact.Enabled || ev.Bone.Length == 0 || depth > SplashDeepest) return;
        float ground = gpu.Terrain.HeightAt(point.X, point.Z);
        if (!float.IsFinite(ground) || ground > WorldWater.Height - WetGround || point.Y - ground > 4) return;   // dry, or a floor above the water
        float strength = Math.Clamp(speed / 50, 0.5f, 1) * Math.Clamp(1 - depth / SplashDeepest, 0.3f, 1);
        gpu.Water?.Ripples?.AddSplash(new Vector2(point.X, point.Z), strength);
        // Water too shallow to reach the ankle bone: the game shows nothing; the biome's shallowest splash.
        if (fired is null && WaterEffectAt(point, 0, speed) is { } shallow) particles.Burst(shallow, surface, Quaternion.Identity, SplashEmitSeconds);
    }

    /// <summary>The biome's <c>water effects</c> entry with the greatest depth at most <paramref name="depth"/>, then the greatest speed at most <paramref name="speed"/>; null for none.</summary>
    string? WaterEffectAt(Vector3 point, float depth, float speed)
    {
        var effects = groundSurfaces?.BiomeAt(point.X, point.Z) is { } biome && waterEffects!.TryGetValue(biome, out var found) ? found : emptyBiomeWater;
        int bestDepth = -1, bestSpeed = -1;
        string? system = null;
        foreach (var e in effects)
            if (e.Depth <= depth && e.Speed <= speed && (e.Depth > bestDepth || (e.Depth == bestDepth && e.Speed > bestSpeed)))
                (bestDepth, bestSpeed, system) = (e.Depth, e.Speed, e.System);
        return system;
    }

    bool InSplashFrustum(Vector3 p)
    {
        foreach (var plane in splashPlanes)
            if (plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z + plane.W < -SplashFrustumMargin) return false;
        return true;
    }

    /// <summary>
    /// The clips' ground events (the <c>events</c> of ANIMATION, ANIMAL_ANIMATION and COMBAT_TECHNIQUE records whose ANIMATION_EVENT has <c>ground effect</c>, with its <c>bone</c>;
    /// <c>FUN_140064e10</c> / <c>FUN_140064d30</c>) and the biomes' water effects (the record without a biome colour: <c>EMPTY</c>, <c>FUN_140a0ac00</c>).
    /// </summary>
    void LoadGroundEvents(GameDatabase db, Meitou.Data.Gameplay.AnimationLibrary library, ParticleRenderer particles)
    {
        animationSounds ??= AnimationSoundEvents.FromDatabase(db);
        groundSurfaces ??= new GroundSurfaces(install, db);
        var ground = new GroundEvent[]?[library.Definitions.Count];
        var any = new bool[ground.Length];
        for (int i = 0; i < ground.Length; i++)
        {
            List<GroundEvent>? list = null;
            if (db.Find(library.Definitions[i].Id) is { } record)
                foreach (var reference in record.GetReferences("events"))
                {
                    if (db.Find(reference.TargetStringId) is not { Type: FcsRecordType.ANIMATION_EVENT } def || !def.GetBool("ground effect")) continue;
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
        (definitionGround, definitionAnyEvents) = (ground, any);

        waterEffects = [];
        var systems = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in db.OfType(FcsRecordType.BIOMES).OrderBy(r => r.StringId, StringComparer.Ordinal))
        {
            var list = new List<WaterEffect>();
            foreach (var reference in r.GetReferences("water effects"))
                if (db.Find(reference.TargetStringId) is { Type: FcsRecordType.EFFECT } effect && EffectRecord.From(effect, db).ParticleSystem is { Length: > 0 } system)
                {
                    list.Add(new WaterEffect(reference.Values.Value0, reference.Values.Value1, system));
                    systems.Add(system);
                }
            waterEffects.TryAdd((uint)r.GetInt("index") & 0xFFFFFF, [.. list]);
            if (r.StringId == "EMPTY" || r.Name == "EMPTY") emptyBiomeWater = [.. list];
        }
        particles.PrepareBursts([.. systems]);
        Console.WriteLine($"water     splashes: {ground.Count(g => g is not null)} clips with ground events; water effects {string.Join(", ", waterEffects.Values.SelectMany(x => x).Distinct().Select(e => $"{e.System} at depth {e.Depth} speed {e.Speed}"))}");
    }
}
