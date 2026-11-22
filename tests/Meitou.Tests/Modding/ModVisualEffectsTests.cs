using System.Numerics;
using Meitou.Content;
using Meitou.Data.Particles;
using Meitou.Game.Rendering.Characters;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' display-only effects (docs/modding.md "Visual effects"): the queue between <see cref="ModContext.PlayEffect(string, EffectAnchor,
/// float, Vector3?)"/> and the renderer, played here through a recording sink in place of the particles, and the anchors' frames.
/// </summary>
public class ModVisualEffectsTests
{
    /// <summary>
    /// Records what it is asked to start, follow and renew, in order; the anchors are there while <see cref="Located"/>.
    /// </summary>
    sealed class Recorder : IModEffectSink
    {
        public bool Located = true;
        public readonly List<ModEffectRequest> Started = [];
        public readonly List<ModEffectRequest> Followed = [];
        public readonly List<float> Renewed = [];

        public object? Start(in ModEffectRequest request)
        {
            if (!Located) return null;

            Started.Add(request);
            return new object();
        }

        public bool Follow(object playing, in ModEffectRequest request)
        {
            Followed.Add(request);
            return Located;
        }

        public void Renew(object playing, float seconds) => Renewed.Add(seconds);
    }

    /// <summary>
    /// Frames for the anchors that follow something: a bone at (1, 2, 3) turned a quarter about Y, a weapon at (0, 1, 0) with stretch 0.2, the
    /// building <c>hut</c> at (10, 0, 0) turned a quarter about Y; nothing else.
    /// </summary>
    sealed class Frames : IEffectFrameSource
    {
        public static readonly Quaternion Quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

        public EffectFrame? Bone(CharacterId on, string bone, Vector3 offset, Quaternion turn) =>
            bone == "Bip01 Head" ? new EffectFrame(new Vector3(1, 2, 3) + Vector3.Transform(offset, Quarter), Quarter * turn, 1) : null;

        public EffectFrame? Weapon(CharacterId on) => on.Slot == 3 ? new EffectFrame(Vector3.UnitY, Quaternion.Identity, 0.2f) : null;

        public EffectFrame? Building(string id) => id == "hut" ? new EffectFrame(new Vector3(10, 0, 0), Quarter, 1) : null;
    }

    /// <summary>
    /// A context for the mod <paramref name="id"/> on <paramref name="registry"/>, without data or a world.
    /// </summary>
    static ModContext Context(ModRegistry registry, string id) =>
        new(new LoadedMod(new ModInfo(id, $"mods/{id}/{id}.mod", ModSource.Local),
                new CodeModManifest(id, ModApi.Version, [$"{id}.dll"], $"{id}.Mod")),
            registry, null!, null!, () => null);

    /// <summary>
    /// A registry whose effects know only the EFFECT <c>spark</c> (particle system <c>sparks</c>), logging into <paramref name="log"/>.
    /// </summary>
    static ModRegistry Registry(List<string> log)
    {
        var registry = new ModRegistry();
        registry.Effects.Resolve = id => id == "spark" ? "sparks" : null;
        registry.Effects.Log = log.Add;
        return registry;
    }

    /// <summary>
    /// Queued effects start oldest first with their system, tint and lifetime, no more than the per-frame and alive caps, attached ones follow
    /// their character each later frame, and an effect stops playing when its lifetime runs out.
    /// </summary>
    [Fact]
    public void Queued_effects_start_in_order_within_the_caps_and_end_with_their_lifetime()
    {
        var registry = Registry([]);
        var mod = Context(registry, "gore");
        var sink = new Recorder();
        var red = new Vector3(1.5f, 0, 0);

        Assert.True(mod.PlayEffect("spark", new CharacterId(3, 1), "Bip01 Head", 0.5f, red));
        for (int i = 0; i < ModEffects.MaxAlive + 10; i++) Assert.True(mod.PlayEffect("spark", new Vector3(i, 0, 0), 2));
        registry.Effects.Advance(0, sink);

        Assert.Equal(ModEffects.MaxPerFrame, sink.Started.Count);
        var head = EffectAnchor.Bone(new CharacterId(3, 1), "Bip01 Head");
        Assert.Equal(new ModEffectRequest("gore", "spark", "sparks", head, red, 0.5f), sink.Started[0]);
        Assert.Equal([.. Enumerable.Range(0, ModEffects.MaxPerFrame - 1).Select(i => (float)i)], sink.Started.Skip(1).Select(r => r.Anchor.Offset.X));
        Assert.All(sink.Started.Skip(1), r => Assert.Equal(EffectAnchorKind.Point, r.Anchor.Kind));
        Assert.Empty(sink.Followed);

        for (int frame = 0; frame < 10; frame++) registry.Effects.Advance(0, sink);
        Assert.Equal(ModEffects.MaxAlive, registry.Effects.Alive);
        Assert.Equal(11, registry.Effects.Queued);
        Assert.Equal(10, sink.Followed.Count);
        Assert.All(sink.Followed, r => Assert.Equal(head, r.Anchor));

        // The attached effect's half second runs out first: one queued effect takes its place, and it is no longer followed.
        registry.Effects.Advance(1, sink);
        Assert.Equal((ModEffects.MaxAlive, 10, 10), (registry.Effects.Alive, registry.Effects.Queued, sink.Followed.Count));

        registry.Effects.Advance(5, sink);
        Assert.Equal((10, 0, ModEffects.MaxAlive + 11), (registry.Effects.Alive, registry.Effects.Queued, sink.Started.Count));
    }

    /// <summary>
    /// A bad request (unknown effect, a point or lifetime that is not usable, a bad tint, no character, a full queue) is dropped and logged once per
    /// mod and problem; a mod that leaves the session takes its queued and playing effects with it, and no one else's.
    /// </summary>
    [Fact]
    public void Bad_requests_are_dropped_and_logged_once_and_a_dropped_mod_takes_only_its_effects()
    {
        var log = new List<string>();
        var registry = Registry(log);
        var gore = Context(registry, "gore");
        var other = Context(registry, "other");

        for (int i = 0; i < 2; i++)
        {
            Assert.False(gore.PlayEffect("nope", Vector3.Zero));
            Assert.False(gore.PlayEffect("spark", new Vector3(float.NaN, 0, 0)));
            Assert.False(gore.PlayEffect("spark", Vector3.Zero, 0));
            Assert.False(gore.PlayEffect("spark", Vector3.Zero, ModEffects.MaxSeconds + 1));
            Assert.False(gore.PlayEffect("spark", Vector3.Zero, tint: new Vector3(-1, 0, 0)));
            Assert.False(gore.PlayEffect("spark", CharacterId.None));
        }
        Assert.False(other.PlayEffect("nope", Vector3.Zero));
        Assert.Equal(5, log.Count(l => l.StartsWith("mod       gore: effect", StringComparison.Ordinal)));
        Assert.Single(log, l => l.StartsWith("mod       other: effect 'nope'", StringComparison.Ordinal));
        Assert.Equal(0, registry.Effects.Queued);

        for (int i = 0; i < ModEffects.MaxQueued; i++) Assert.True((i % 2 == 0 ? gore : other).PlayEffect("spark", Vector3.Zero));
        Assert.False(gore.PlayEffect("spark", Vector3.Zero));
        Assert.False(gore.PlayEffect("spark", Vector3.Zero));
        Assert.Equal(6, log.Count(l => l.StartsWith("mod       gore: effect", StringComparison.Ordinal)));

        var sink = new Recorder();
        registry.Effects.Advance(0, sink);
        registry.Effects.Drop("gore");
        Assert.Equal(ModEffects.MaxQueued / 2 - ModEffects.MaxPerFrame / 2, registry.Effects.Queued);
        Assert.Equal(ModEffects.MaxPerFrame / 2, registry.Effects.Alive);
        registry.Effects.Advance(0, sink);
        Assert.All(sink.Started.Skip(ModEffects.MaxPerFrame), r => Assert.Equal("other", r.Owner));
    }

    /// <summary>
    /// Each anchor kind resolves to its frame: a point as given (its turn normalized), a bone and a building moved and turned in their own frame,
    /// a weapon with its blade's stretch unless the anchor scales it; one not there is null. The chest is the bone overload's default. The weapon's
    /// frame from its mesh: the base at the hilt end, +Y to the tip, X across the blade's width, stretch = length / 8.5.
    /// </summary>
    [Fact]
    public void Anchors_resolve_to_their_frames()
    {
        var frames = new Frames();
        var hero = new CharacterId(3, 1);
        var half = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);

        var point = EffectFrames.Locate(EffectAnchor.At(new Vector3(4, 5, 6), half * 3), frames)!.Value;
        Assert.Equal((new Vector3(4, 5, 6), 1f), (point.At, point.Stretch));
        AssertNear(Vector3.Transform(Vector3.UnitY, half), Vector3.Transform(Vector3.UnitY, point.Turn));
        var bone = EffectFrames.Locate(EffectAnchor.Bone(hero, "Bip01 Head", Vector3.UnitX, half), frames)!.Value;
        AssertNear(new Vector3(1, 2, 2), bone.At);
        AssertNear(Vector3.Transform(Vector3.UnitY, Frames.Quarter * half), Vector3.Transform(Vector3.UnitY, bone.Turn));
        Assert.Null(EffectFrames.Locate(EffectAnchor.Bone(hero, "Bip01 Tail"), frames));

        Assert.Equal(new EffectFrame(Vector3.UnitY, Quaternion.Identity, 0.2f), EffectFrames.Locate(EffectAnchor.Weapon(hero), frames));
        Assert.Equal(2, EffectFrames.Locate(EffectAnchor.Weapon(hero).Scaled(2), frames)!.Value.Stretch);
        Assert.Null(EffectFrames.Locate(EffectAnchor.Weapon(new CharacterId(4, 1)), frames));

        var hut = EffectFrames.Locate(EffectAnchor.Building("hut", Vector3.UnitX), frames)!.Value;
        AssertNear(new Vector3(10, 0, -1), hut.At);
        Assert.Equal(1f, hut.Stretch);
        AssertNear(Vector3.Transform(Vector3.UnitX, Frames.Quarter), Vector3.Transform(Vector3.UnitX, hut.Turn));
        Assert.Null(EffectFrames.Locate(EffectAnchor.Building("tower"), frames));

        var registry = Registry([]);
        var sink = new Recorder();
        Assert.True(Context(registry, "gore").PlayEffect("spark", hero));
        registry.Effects.Advance(0, sink);
        Assert.Equal(EffectAnchor.Bone(hero, "Bip01 Spine2"), Assert.Single(sink.Started).Anchor);

        AssertBladeFrame();
    }

    /// <summary>
    /// A blade along -Z from its hilt end at z = 0.1 over 1.2 units, its pick boxes centred on x = 0.02, y = 0 and wider in x: its line, and its
    /// frame as held (unplaced, then scaled by 2, turned and moved).
    /// </summary>
    static void AssertBladeFrame()
    {
        PickBox[] boxes = [new(-1, new Vector3(0, -0.01f, -1.1f), new Vector3(0.04f, 0.01f, -0.5f)), new(-1, new Vector3(0, -0.01f, -0.5f),
            new Vector3(0.04f, 0.01f, 0.1f))];
        var axis = -Vector3.UnitZ;
        AssertNear(new Vector3(0.02f, 0, 0.1f), BladeSpan.Point(boxes, axis, -0.1f, 1 / 1.2f, 0));
        AssertNear(new Vector3(0.02f, 0, -1.1f), BladeSpan.Point(boxes, axis, -0.1f, 1 / 1.2f, 1));

        var frame = BladeSpan.Frame(boxes, axis, -0.1f, 1 / 1.2f, Matrix4x4.Identity);
        AssertNear(new Vector3(0.02f, 0, 0.1f), frame.Base);
        AssertNear(axis, Vector3.Transform(Vector3.UnitY, frame.Turn));
        AssertNear(Vector3.UnitX, Vector3.Transform(Vector3.UnitX, frame.Turn));
        Assert.Equal(1.2f, frame.Length, 1e-4f);
        Assert.Equal(1.2f / 8.5f, frame.Stretch, 1e-6f);

        var world = Matrix4x4.CreateScale(2) * Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(10, 0, 0);
        var placed = BladeSpan.Frame(boxes, axis, -0.1f, 1 / 1.2f, world);
        AssertNear(Vector3.Transform(frame.Base, world), placed.Base);
        AssertNear(Vector3.Normalize(Vector3.TransformNormal(axis, world)), Vector3.Transform(Vector3.UnitY, placed.Turn));
        Assert.Equal(2 * frame.Length, placed.Length, 1e-4f);
    }


    /// <summary>
    /// An effect whose anchor is not there waits, paused, and starts once it is; one that loses its anchor stops emitting and starts again where
    /// the anchor is, for the seconds it has left. A kept effect is renewed, not restarted, by the same mod, effect and anchor (another tint
    /// restarts it), and stopped by its key; a played one is never renewed. Bad anchors are rejected and logged. HasEffect answers without a
    /// logged rejection, false before the particles load.
    /// </summary>
    [Fact]
    public void Effects_pause_while_unlocated_and_kept_ones_renew_until_stopped()
    {
        var log = new List<string>();
        var registry = Registry(log);
        var mod = Context(registry, "aura");
        var sink = new Recorder { Located = false };
        var head = EffectAnchor.Bone(new CharacterId(3, 1), "Bip01 Head");

        Assert.False(mod.HasEffect("spark"));   // before the particles load
        registry.Effects.Exists = id => registry.Effects.System(id) is not null;
        Assert.True(mod.HasEffect("spark"));
        Assert.False(mod.HasEffect("nope"));

        Assert.True(mod.KeepEffect("spark", head, 10));
        registry.Effects.Advance(1, sink);
        Assert.Equal((1, 0, 0), (registry.Effects.Alive, registry.Effects.Playing, sink.Started.Count));
        sink.Located = true;
        registry.Effects.Advance(1, sink);
        Assert.Equal(9, Assert.Single(sink.Started).Seconds);   // its seconds count from its first frame alive

        Assert.True(mod.KeepEffect("spark", head, 20));
        registry.Effects.Advance(1, sink);
        Assert.Single(sink.Started);
        Assert.Equal([19f], sink.Renewed);

        sink.Located = false;
        registry.Effects.Advance(1, sink);
        Assert.Equal([19f, 0f], sink.Renewed);
        Assert.Equal((1, 0), (registry.Effects.Alive, registry.Effects.Playing));
        sink.Located = true;
        registry.Effects.Advance(1, sink);
        Assert.Equal(17, sink.Started[^1].Seconds);

        Assert.True(mod.KeepEffect("spark", head, 20, new Vector3(0, 1, 0)));   // another tint: restarted with it
        registry.Effects.Advance(0, sink);
        Assert.Equal((3, new Vector3(0, 1, 0)), (sink.Started.Count, sink.Started[^1].Tint));
        Assert.True(mod.PlayEffect("spark", head, 5));
        registry.Effects.Advance(0, sink);
        Assert.Equal((2, 4), (registry.Effects.Alive, sink.Started.Count));

        Assert.True(mod.StopEffect("spark", head));
        Assert.False(mod.StopEffect("spark", head));
        registry.Effects.Advance(0, sink);
        Assert.Equal(1, registry.Effects.Alive);   // the played one goes on

        Assert.False(mod.PlayEffect("spark", EffectAnchor.Building("")));
        Assert.False(mod.PlayEffect("spark", head.Scaled(0)));
        Assert.False(mod.PlayEffect("spark", EffectAnchor.At(Vector3.Zero, default(Quaternion))));
        Assert.Equal(3, log.Count);
    }

    /// <summary>
    /// A burst kept in its frame (an effect on a bone or weapon) holds its particles there: they move and turn with its origin and turn. A
    /// world-space burst's particles stay where they were thrown. Both throw them at the same place.
    /// </summary>
    [Fact]
    public void Kept_local_particles_follow_the_burst_and_world_ones_stay()
    {
        var system = PuSystemDef.From(PuScriptReader.Parse("""
            system held
            {
                technique
                {
                    visual_particle_quota 50
                    emitter Point
                    {
                        position 0 1 0
                        emission_rate 10
                        time_to_live 100
                        velocity 0
                    }
                }
            }
            """).Single(n => n.Kind == "system"));
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);   // +Y to -X
        var world = new ParticleSimulation(system, 1) { Origin = new Vector3(5, 0, 0), Orientation = quarter };
        var local = new ParticleSimulation(system, 1) { Origin = new Vector3(5, 0, 0), Orientation = quarter, KeepLocal = true };
        world.Advance(1, ParticleEnvironment.None);
        local.Advance(1, ParticleEnvironment.None);
        AssertAll(world, new Vector3(4, 0, 0));
        AssertAll(local, new Vector3(4, 0, 0));

        foreach (var sim in new[] { world, local })
        {
            sim.Origin = new Vector3(10, 0, 0);
            sim.Orientation = Quaternion.Identity;
        }
        AssertAll(world, new Vector3(4, 0, 0));
        AssertAll(local, new Vector3(10, 1, 0));
    }

    /// <summary>
    /// A burst's stretch is a uniform system scale: the emitter's place, its particles' size and their speed all grow by it; stretch 1 reads
    /// exactly as an unstretched burst.
    /// </summary>
    [Fact]
    public void Stretch_scales_places_sizes_and_speeds()
    {
        var system = PuSystemDef.From(PuScriptReader.Parse("""
            system blade
            {
                technique
                {
                    visual_particle_quota 50
                    default_particle_width 0.5
                    default_particle_height 2
                    default_particle_depth 1
                    emitter Point
                    {
                        position 0 1 0
                        emission_rate 10
                        time_to_live 100
                        velocity 3
                        direction 0 1 0
                        angle 0
                    }
                }
            }
            """).Single(n => n.Kind == "system"));
        var plain = Read(new ParticleSimulation(system, 1));
        var one = Read(new ParticleSimulation(system, 1) { Stretch = 1 });
        var twice = Read(new ParticleSimulation(system, 1) { Stretch = 2 });

        Assert.Equal(plain, one);
        Assert.Equal(plain.Length, twice.Length);
        for (int i = 0; i < plain.Length; i++)
        {
            AssertNear(plain[i].Position * 2, twice[i].Position);   // the emitter's place and the distance flown since
            Assert.Equal((plain[i].Width * 2, plain[i].Height * 2, plain[i].Depth * 2), (twice[i].Width, twice[i].Height, twice[i].Depth));
        }

        static ParticleInstance[] Read(ParticleSimulation sim)
        {
            sim.Advance(1, ParticleEnvironment.None);
            var buffer = new ParticleInstance[sim.TechniqueParticleCount(0)];
            return buffer[..sim.Collect(0, buffer, Vector3.One, 1)];
        }
    }

    /// <summary>
    /// Every particle <paramref name="sim"/> reads out is at <paramref name="at"/>.
    /// </summary>
    static void AssertAll(ParticleSimulation sim, Vector3 at)
    {
        var buffer = new ParticleInstance[sim.TechniqueParticleCount(0)];
        int n = sim.Collect(0, buffer, Vector3.One, 1);
        Assert.True(n > 0);
        foreach (var p in buffer.AsSpan(0, n)) AssertNear(at, p.Position);
    }

    /// <summary>
    /// <paramref name="actual"/> within a thousandth of <paramref name="expected"/>.
    /// </summary>
    static void AssertNear(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"{actual} not {expected}");
}
