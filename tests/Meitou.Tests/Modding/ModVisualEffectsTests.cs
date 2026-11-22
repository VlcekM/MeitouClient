using System.Numerics;
using Meitou.Content;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' display-only effects (docs/modding.md "Visual effects"): the queue between <see cref="ModContext.PlayEffect(string, Vector3,
/// float, Vector3?)"/> and the renderer, played here through a recording sink in place of the particles.
/// </summary>
public class ModVisualEffectsTests
{
    /// <summary>
    /// Records what it is asked to start and follow, in order.
    /// </summary>
    sealed class Recorder : IModEffectSink
    {
        public readonly List<ModEffectRequest> Started = [];
        public readonly List<ModEffectRequest> Followed = [];

        public object? Start(in ModEffectRequest request)
        {
            Started.Add(request);
            return new object();
        }

        public void Follow(object playing, in ModEffectRequest request) => Followed.Add(request);
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
        Assert.Equal(new ModEffectRequest("gore", "spark", "sparks", default, new CharacterId(3, 1), "Bip01 Head", red, 0.5f), sink.Started[0]);
        Assert.Equal([.. Enumerable.Range(0, ModEffects.MaxPerFrame - 1).Select(i => (float)i)], sink.Started.Skip(1).Select(r => r.At.X));
        Assert.Empty(sink.Followed);

        for (int frame = 0; frame < 10; frame++) registry.Effects.Advance(0, sink);
        Assert.Equal(ModEffects.MaxAlive, registry.Effects.Alive);
        Assert.Equal(11, registry.Effects.Queued);
        Assert.Equal(10, sink.Followed.Count);
        Assert.All(sink.Followed, r => Assert.Equal(new CharacterId(3, 1), r.On));

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
}
