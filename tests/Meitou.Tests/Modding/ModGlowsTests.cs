using System.Numerics;
using Meitou.Content;
using Meitou.Data.Characters;
using Meitou.Game.Rendering.Characters;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' glows (docs/modding.md "Glow"): the state between <see cref="ModContext.Glow"/> and the character renderer, read here as the
/// renderer reads it each frame, and which of a character's parts each target lights.
/// </summary>
public class ModGlowsTests
{
    /// <summary>
    /// A context for the mod <paramref name="id"/> on <paramref name="registry"/>, without data or a world.
    /// </summary>
    static ModContext Context(ModRegistry registry, string id) =>
        new(new LoadedMod(new ModInfo(id, $"mods/{id}/{id}.mod", ModSource.Local),
                new CodeModManifest(id, ModApi.Version, [$"{id}.dll"], $"{id}.Mod")),
            registry, null!, null!, () => null);

    /// <summary>
    /// The glows as the renderer would draw them now.
    /// </summary>
    static Dictionary<GlowTarget, GlowNow> Now(ModRegistry registry)
    {
        var list = new List<(GlowTarget Target, GlowNow Glow)>();
        registry.Glows.Current(list);
        return list.ToDictionary(g => g.Target, g => g.Glow);
    }

    /// <summary>
    /// A glow comes up over the fade, holds its colour and strength, is replaced by a newer request on the same target (renewed without a
    /// restart), fades out early on a stop and ends with its lifetime; targets are apart; strength 0 shows nothing; invalid requests are rejected
    /// and each problem logged once per mod; a mod's drop and a new world's clear remove glows.
    /// </summary>
    [Fact]
    public void Glows_reach_the_renderer_fade_replace_expire_and_reject_bad_requests()
    {
        var log = new List<string>();
        var registry = new ModRegistry();
        registry.Glows.Log = log.Add;
        var fire = Context(registry, "fire");
        var ice = Context(registry, "ice");
        var hero = GlowTarget.Weapon(new CharacterId(3, 1));
        var foe = GlowTarget.Character(new CharacterId(4, 2));
        var orange = new Vector3(1, 0.45f, 0.1f);
        var cyan = new Vector3(0.3f, 0.9f, 1);
        float fade = ModGlows.FadeSeconds;

        Assert.True(fire.Glow(hero, orange, 2, 1.5f));
        registry.Glows.Advance(0);
        Assert.Empty(Now(registry));   // not up yet

        registry.Glows.Advance(fade / 2);
        Assert.Equal(new GlowNow(orange, 0.75f), Now(registry)[hero]);
        registry.Glows.Advance(fade);
        Assert.Equal(new GlowNow(orange, 1.5f), Now(registry)[hero]);

        // A newer request replaces the colour and comes up from where the glow stood (at full: no restart); another target is apart.
        Assert.True(ice.Glow(hero, cyan, 5));
        Assert.True(fire.Glow(foe, orange, 1));
        Assert.True(fire.Glow(GlowTarget.Worn(new CharacterId(3, 1), AttachSlot.Hat), orange, 1, 0));
        registry.Glows.Advance(0);
        Assert.Equal(new GlowNow(cyan, 1), Now(registry)[hero]);
        Assert.Equal((3, 1), (registry.Glows.Count, Now(registry).Count));

        // Only the mod that set a glow stops it, fading out; the others' lifetime runs out.
        Assert.False(fire.StopGlow(hero));
        Assert.True(ice.StopGlow(hero));
        registry.Glows.Advance(fade / 2);
        Assert.Equal(new GlowNow(cyan, 0.5f), Now(registry)[hero]);
        registry.Glows.Advance(1);
        Assert.Equal(0, registry.Glows.Count);

        // Invalid requests: each problem once per mod in the log.
        Assert.False(fire.Glow(GlowTarget.Character(new CharacterId(-1, 0)), orange, 1));
        Assert.False(fire.Glow(hero with { Kind = (GlowTargetKind)9 }, orange, 1));
        Assert.False(fire.Glow(hero, new Vector3(float.NaN, 0, 0), 1));
        Assert.False(fire.Glow(hero, new Vector3(-1, 0, 0), 1));
        Assert.False(fire.Glow(hero, orange, 0));
        Assert.False(fire.Glow(hero, orange, ModGlows.MaxSeconds + 1));
        Assert.False(fire.Glow(hero, orange, 1, -1));
        Assert.False(ice.Glow(hero, orange, 0));
        Assert.Equal(0, registry.Glows.Count);
        Assert.Equal(5, log.Count);
        Assert.Single(log, l => l.Contains("ice") && l.Contains("lifetime"));

        // A mod that leaves loses its own glows only; a new world clears the rest.
        Assert.True(fire.Glow(hero, orange, 10));
        Assert.True(ice.Glow(foe, cyan, 10));
        registry.Glows.Drop("fire");
        Assert.Equal(1, registry.Glows.Count);
        registry.Glows.Clear();
        Assert.Equal(0, registry.Glows.Count);
    }

    /// <summary>
    /// Which parts each target lights: the weapon target only a held blade (with the blade mask), a slot target that slot's parts, the character
    /// target everything else (the body included); the more specific wins. The packing: the colour over its brightest channel, the level in
    /// eighths (capped), the mask bit.
    /// </summary>
    [Fact]
    public void Targets_pick_their_parts_and_pack_for_the_instances()
    {
        var on = new CharacterId(3, 1);
        var glow = new CharacterGlow();
        glow.Set(GlowTarget.Character(on), new Vector3(0, 0, 2), 1);
        glow.Set(GlowTarget.Weapon(on), new Vector3(1, 0.5f, 0), 1);
        glow.Set(GlowTarget.Worn(on, AttachSlot.Hat), new Vector3(0, 1, 0), 0.5f);

        uint whole = CharacterGlow.Pack(new Vector3(0, 0, 2), 1, false);
        Assert.Equal(0x0000FF00u | 16, whole);
        Assert.Equal(0xFF800000u | 8 | 128, glow.For(heldBlade: true, AttachSlot.Weapon));
        Assert.Equal(0x00FF0000u | 4, glow.For(heldBlade: false, AttachSlot.Hat));
        Assert.Equal(whole, glow.For(heldBlade: false, AttachSlot.Body));
        Assert.Equal(whole, glow.For(heldBlade: false, null));   // the body
        Assert.Equal(whole, glow.For(heldBlade: false, AttachSlot.Weapon));   // a sheathed weapon

        var bladeOnly = new CharacterGlow();
        bladeOnly.Set(GlowTarget.Weapon(on), Vector3.One, 1);
        Assert.Equal(0u, bladeOnly.For(heldBlade: false, null));
        Assert.Equal(127u | 128, CharacterGlow.Pack(new Vector3(4, 4, 4), 4, true) & 255);
        Assert.Equal(0u, CharacterGlow.Pack(Vector3.One, 0, false));
    }
}
