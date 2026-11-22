using System.Numerics;
using Meitou.Content;
using Meitou.Game.Rendering.Characters;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' weapon glows (docs/modding.md "Weapon glow"): the state between <see cref="ModContext.GlowWeapon"/> and the character renderer,
/// read here as the renderer reads it each frame.
/// </summary>
public class ModWeaponGlowTests
{
    /// <summary>
    /// A context for the mod <paramref name="id"/> on <paramref name="registry"/>, without data or a world.
    /// </summary>
    static ModContext Context(ModRegistry registry, string id) =>
        new(new LoadedMod(new ModInfo(id, $"mods/{id}/{id}.mod", ModSource.Local),
                new CodeModManifest(id, ModApi.Version, [$"{id}.dll"], $"{id}.Mod")),
            registry, null!, null!, () => null);

    /// <summary>
    /// Plays the glows' particles: starts and tracks them while <see cref="Drawn"/> (the blade is drawn), recording what it is asked.
    /// </summary>
    sealed class Recorder : IModEffectSink
    {
        public bool Drawn = true;
        public readonly List<ModEffectRequest> Started = [];
        public readonly List<(object Handle, float Seconds)> Renewed = [];
        public int Tracked;

        public object? Start(in ModEffectRequest request)
        {
            if (!Drawn) return null;

            Started.Add(request);
            return new object();
        }

        public bool Follow(object playing, in ModEffectRequest request)
        {
            Tracked++;
            return Drawn;
        }

        public void Renew(object playing, float seconds) => Renewed.Add((playing, seconds));
    }

    /// <summary>
    /// The glows as the renderer would draw them now.
    /// </summary>
    static Dictionary<CharacterId, WeaponGlow> Now(ModRegistry registry)
    {
        var list = new List<(CharacterId On, WeaponGlow Glow)>();
        registry.WeaponGlows.Current(list);
        return list.ToDictionary(g => g.On, g => g.Glow);
    }

    /// <summary>
    /// A glow comes up over the fade, holds its colour and strength, is replaced by a newer request, fades out early on a stop and ends with its
    /// lifetime; invalid requests are rejected and each problem logged once per mod; a mod's drop and a new world's clear remove glows.
    /// </summary>
    [Fact]
    public void Glows_reach_the_renderer_fade_replace_expire_and_reject_bad_requests()
    {
        var log = new List<string>();
        var registry = new ModRegistry();
        registry.WeaponGlows.Log = log.Add;
        var fire = Context(registry, "fire");
        var ice = Context(registry, "ice");
        var hero = new CharacterId(3, 1);
        var foe = new CharacterId(4, 2);
        var orange = new Vector3(1, 0.45f, 0.1f);
        var cyan = new Vector3(0.3f, 0.9f, 1);
        float fade = ModWeaponGlows.FadeSeconds;

        Assert.True(fire.GlowWeapon(hero, orange, 2, 1.5f));
        registry.WeaponGlows.Advance(0);
        Assert.Empty(Now(registry));   // not up yet

        registry.WeaponGlows.Advance(fade / 2);
        Assert.Equal(new WeaponGlow(orange, 0.75f), Now(registry)[hero]);
        registry.WeaponGlows.Advance(fade);
        Assert.Equal(new WeaponGlow(orange, 1.5f), Now(registry)[hero]);

        // A newer request replaces the colour and comes up from where the glow stood; another character's is apart.
        Assert.True(ice.GlowWeapon(hero, cyan, 5));
        Assert.True(fire.GlowWeapon(foe, orange, 1));
        registry.WeaponGlows.Advance(0);
        Assert.Equal(new WeaponGlow(cyan, 1), Now(registry)[hero]);
        Assert.Equal(2, registry.WeaponGlows.Count);

        // Only the mod that set a glow stops it, fading out; the foe's lifetime runs out.
        Assert.False(fire.StopWeaponGlow(hero));
        Assert.True(ice.StopWeaponGlow(hero));
        registry.WeaponGlows.Advance(fade / 2);
        Assert.Equal(new WeaponGlow(cyan, 0.5f), Now(registry)[hero]);
        registry.WeaponGlows.Advance(1);
        Assert.Equal(0, registry.WeaponGlows.Count);

        // Invalid requests: each problem once per mod in the log.
        Assert.False(fire.GlowWeapon(new CharacterId(-1, 0), orange, 1));
        Assert.False(fire.GlowWeapon(hero, new Vector3(float.NaN, 0, 0), 1));
        Assert.False(fire.GlowWeapon(hero, new Vector3(-1, 0, 0), 1));
        Assert.False(fire.GlowWeapon(hero, orange, 0));
        Assert.False(fire.GlowWeapon(hero, orange, ModWeaponGlows.MaxSeconds + 1));
        Assert.False(fire.GlowWeapon(hero, orange, 1, -1));
        Assert.False(ice.GlowWeapon(hero, orange, 0));
        Assert.Equal(0, registry.WeaponGlows.Count);
        Assert.Equal(5, log.Count);
        Assert.Single(log, l => l.Contains("ice") && l.Contains("lifetime"));

        // A mod that leaves loses its own glows only; a new world clears the rest.
        Assert.True(fire.GlowWeapon(hero, orange, 10));
        Assert.True(ice.GlowWeapon(foe, cyan, 10));
        registry.WeaponGlows.Drop("fire");
        Assert.Equal(1, registry.WeaponGlows.Count);
        registry.WeaponGlows.Clear();
        Assert.Equal(0, registry.WeaponGlows.Count);
    }

    /// <summary>
    /// A glow's effect resolves as PlayEffect's (HasEffect asks without a logged rejection) and is kept on the weapon anchor, recoloured to the
    /// glow's colour but for white, starting once the blade is drawn; a call again with the same effect and colour renews it (no restart); it
    /// pauses while the blade is not drawn and ends with the glow or its stop. Strength 0 shows the particles with no tint on the blade.
    /// </summary>
    [Fact]
    public void Glow_particles_are_kept_on_the_weapon_and_renewed()
    {
        var log = new List<string>();
        var registry = new ModRegistry();
        registry.Effects.Resolve = id => id == "frost" ? "SB_Ice" : null;
        registry.WeaponGlows.Log = log.Add;
        registry.Effects.Log = log.Add;
        var ice = Context(registry, "ice");
        var hero = new CharacterId(3, 1);
        var blade = EffectAnchor.Weapon(hero);
        var cyan = new Vector3(0.3f, 0.9f, 1);
        var sink = new Recorder { Drawn = false };

        Assert.False(ice.HasEffect("frost"));   // before the particles load
        registry.Effects.Exists = id => registry.Effects.System(id) is not null;
        Assert.True(ice.HasEffect("frost"));
        Assert.False(ice.HasEffect("no such effect"));
        Assert.Empty(log);
        Assert.False(ice.GlowWeapon(hero, cyan, 5, 1, "no such effect"));
        Assert.Single(log, l => l.Contains("effect id"));

        Assert.True(ice.GlowWeapon(hero, cyan, 5, 1, "frost"));
        registry.Effects.Advance(0.1f, sink);
        Assert.Empty(sink.Started);   // sheathed: it waits
        sink.Drawn = true;
        registry.Effects.Advance(0.1f, sink);
        var started = Assert.Single(sink.Started);
        Assert.Equal(("ice", "frost", "SB_Ice", blade, cyan), (started.Owner, started.Effect, started.System, started.Anchor, started.Tint));

        // Kept alive: the same effect and colour renew the playing one rather than starting another.
        Assert.True(ice.GlowWeapon(hero, cyan, 20, 1, "frost"));
        registry.Effects.Advance(0.1f, sink);
        Assert.Single(sink.Started);
        Assert.True(Assert.Single(sink.Renewed).Seconds > 19.8f);

        // Sheathed: it stops (renewed to 0); drawn again: it starts again; the glow's end ends it.
        sink.Drawn = false;
        registry.Effects.Advance(0.1f, sink);
        Assert.Equal((1, 0), (sink.Renewed.Count(r => r.Seconds == 0), registry.Effects.Playing));
        sink.Drawn = true;
        registry.Effects.Advance(0.1f, sink);
        Assert.Equal(2, sink.Started.Count);
        registry.WeaponGlows.Advance(25);
        registry.Effects.Advance(0, sink);
        Assert.Equal(2, sink.Renewed.Count(r => r.Seconds == 0));
        Assert.Equal((0, 0), (registry.WeaponGlows.Count, registry.Effects.Alive));

        // Strength 0 and white: the particles in their own colours, no tint on the blade; a stop ends them.
        Assert.True(ice.GlowWeapon(hero, Vector3.One, 5, 0, "frost"));
        registry.WeaponGlows.Advance(0.5f);
        registry.Effects.Advance(0.5f, sink);
        Assert.Null(sink.Started[^1].Tint);
        Assert.Empty(Now(registry));
        Assert.True(ice.StopWeaponGlow(hero));
        Assert.Equal(0, registry.Effects.Alive);

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
    /// <paramref name="actual"/> within a millimetre of <paramref name="expected"/>.
    /// </summary>
    static void AssertNear(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"{actual} not {expected}");
}
