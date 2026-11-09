using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Particles;
using EffectType = Meitou.Data.Particles.EffectType;

namespace Meitou.Tests.World;

/// <summary>
/// ParticleUniverse scripts and their CPU simulation (docs/formats/particle-universe.md).
/// </summary>
public class ParticleTests
{
    const string Rain = """
        system test_rain
        {
            technique
            {
                visual_particle_quota 5000
                material m
                default_particle_width 0.85
                default_particle_height 4
                renderer Billboard
                {
                    billboard_type oriented_self
                    billboard_origin bottom_center
                }
                emitter Box
                {
                    emission_rate 600
                    angle 1
                    time_to_live dyn_random
                    {
                        min 0.3
                        max 1
                    }
                    velocity dyn_random
                    {
                        min 90
                        max 182
                    }
                    position 0 15 0
                    direction 0.2 -1 0
                    colour 0.3 0.3 0.3 1
                    box_width 120
                    box_height 120
                }
                observer OnPosition
                {
                    handler DoExpire
                    {
                    }
                    position_y less_than -5.85
                }
            }
        }
        """;

    static PuSystemDef System(string text) => PuSystemDef.From(PuScriptReader.Parse(text).Single(n => n.Kind == "system"));

    [Fact]
    public void Script_reads_blocks_properties_and_dynamic_attributes()
    {
        var s = System(Rain);
        Assert.Equal("test_rain", s.Name);
        var t = Assert.Single(s.Techniques);
        Assert.Equal(5000, t.VisualQuota);
        Assert.Equal("oriented_self", t.Renderer.BillboardType);
        Assert.Equal("bottom_center", t.Renderer.Origin);
        var e = Assert.Single(t.Emitters);
        Assert.Equal(PuEmitterType.Box, e.Type);
        Assert.Equal(600, e.Rate.A);
        Assert.Equal((90f, 182f), e.Velocity.Range);
        Assert.Equal(new Vector3(120, 120, 100), e.BoxSize);   // the depth is left out: 100
        Assert.Equal(new Vector3(0.2f, -1, 0), e.Direction);
        var o = Assert.Single(t.Observers);
        Assert.Equal((PuObserverType.OnPosition, 1, PuCompare.LessThan, -5.85f), (o.Type, o.Axis, o.Compare, o.Value));
        Assert.Contains("DoExpire", o.Handlers);
    }

    [Fact]
    public void Splines_are_parametric_over_the_x_range()
    {
        // Sand-Stream's all_particle_dimensions: points evenly spaced in the spline's parameter, read at x / (last x - first x), at most 1.
        var dims = PuDynamic.Curve([new(0, 0.221606f), new(0.359779f, 2.96953f), new(0.464945f, 15.7784f)], spline: true);
        Assert.Equal(0.221606f, dims.Evaluate(0), 4);
        Assert.Equal(2.96953f, dims.Evaluate(0.464945f / 2), 4);   // the middle point at half the range, not at its own x
        Assert.Equal(15.7784f, dims.Evaluate(0.464945f), 4);
        Assert.Equal(15.7784f, dims.Evaluate(30), 4);              // held after the range
        // The x range is not shifted by the first x: points from 2 to 4 are read at x / 2.
        var shifted = PuDynamic.Curve([new(2, 10), new(4, 20)], spline: true);
        Assert.Equal(10, shifted.Evaluate(0), 4);
        Assert.Equal(20, shifted.Evaluate(2), 4);
    }

    [Fact]
    public void Emitter_sizes_and_the_scale_affector_follow_the_plugin()
    {
        // all_particle_dimensions wins over particle_width and is read at the system's age; xyz_scale alone drives the growth, times the scale.
        var s = System("""
            system z
            {
                scale 2 2 2
                technique
                {
                    visual_particle_quota 100
                    emitter Point
                    {
                        emission_rate 10
                        time_to_live 100
                        velocity 0
                        all_particle_dimensions dyn_curved_linear
                        {
                            control_point 0 1
                            control_point 1 5
                        }
                        particle_width 40
                    }
                    affector Scale
                    {
                        xyz_scale 3
                        x_scale 1000
                    }
                }
            }
            """);
        var sim = new ParticleSimulation(s, 1);
        sim.Advance(2, ParticleEnvironment.None);
        var buffer = new ParticleInstance[sim.TechniqueParticleCount(0)];
        int n = sim.Collect(0, buffer, Vector3.One, 1);
        Assert.True(n > 5);
        // Emitted at age a (0..2 s) with (1 + 4 min(a, 1)) x scale 2, then grown by 3 x 2 = 6 units a second for 2 - a s: 10..16 units.
        // Read as particle_width (80) or with x_scale (+2000 a second) it would be far larger.
        foreach (var p in buffer.AsSpan(0, n))
        {
            Assert.InRange(p.Width, 9, 17);
            Assert.Equal(p.Width, p.Height, 3);
        }
        Assert.True(buffer.AsSpan(0, n).ToArray().Max(p => p.Width) > 10);
    }

    [Fact]
    [Slow]
    public void Every_shipped_script_and_material_reads()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var library = ParticleLibrary.Load(install!);
        Assert.Empty(library.Errors);
        Assert.Equal(93, library.ScriptFiles);
        Assert.Equal(93, library.Materials.Count);
        Assert.Equal(93, library.AllSystems.Count);   // the base game has no second system in a file

        var rain = library.FindSystem("Kenshi_Heavy_Rain")!;
        var t = Assert.Single(rain.Techniques);
        Assert.Equal(5000, t.VisualQuota);
        var e = Assert.Single(t.Emitters);
        Assert.Equal(PuEmitterType.Box, e.Type);
        Assert.Equal(120, e.BoxSize.X);
        Assert.Equal(120, e.BoxSize.Y);
        Assert.Equal(6400, e.Rate.A);
        Assert.Equal((90f, 182f), e.Velocity.Range);
        Assert.Equal((0.3f, 1f), e.Life.Range);
        Assert.Equal(new Vector3(0.2f, -1, 0), e.Direction);
        Assert.Equal(1, rain.FastForward!.Value.Time);
        Assert.Equal("Kenshi_rain_basic", t.Material);
        Assert.Equal("oriented_self", t.Renderer.BillboardType);
        Assert.Equal(-5.85f, Assert.Single(t.Observers).Value);
        Assert.Equal(120, rain.LargestEmitterExtent);

        var light = library.FindSystem("kenshi_rain1")!;
        var lt = Assert.Single(light.Techniques);
        Assert.Equal(1000, lt.VisualQuota);
        var le = Assert.Single(lt.Emitters);
        Assert.Equal(new Vector3(150, 150, 150), le.BoxSize);
        Assert.Equal(1000, le.Rate.A);
        Assert.Equal(0.15f, le.Width!.A, 4);
        Assert.Equal((4f, 16f), le.Height!.Range);
        Assert.Equal("top_center", lt.Renderer.Origin);

        // A system is found by its own name, not its file's.
        var ash = library.FindSystem("Ash-Flakes_Light")!;
        Assert.Equal("Ashland_MultiFlakes.pu", ash.File);
        Assert.Equal(4, ash.Techniques.Count);
        Assert.Equal(120, ash.LargestEmitterExtent);

        var material = library.FindMaterial("Kenshi_rain_basic")!;
        Assert.Equal(ParticleBlend.Add, material.Blend);
        Assert.False(material.DepthWrite);
        Assert.True(material.Ambient);
        Assert.Equal("Kenshi_Rain_Basic.png", material.Texture);
        Assert.All(library.Systems.Values.SelectMany(s => s.Techniques),
                tech => Assert.True(tech.Material.Length == 0 || library.FindMaterial(tech.Material) is not null
                        || tech.Material == "SandBlown_Streaks", tech.Material));
    }

    [Fact]
    [Slow]
    public void Forced_weather_gives_its_camera_effects()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var library = ParticleLibrary.Load(install!);
        var ash = WeatherEffectAdapter.FromName(db, "Kenshi_Ash-Flakes");
        Assert.Equal(["Kenshi_Ash-Flakes_Light", "poison gas [White]"], ash.Effects.Select(e => e.Effect.Name));
        Assert.Equal(EffectType.Camera, ash.Effects[0].Effect.Type);
        Assert.Equal(EffectType.WanderingGas, ash.Effects[1].Effect.Type);
        Assert.Equal(70, ash.Effects[1].Count);
        var rain = WeatherEffectAdapter.FromName(db, "Heavy_Rain");
        var entry = Assert.Single(rain.Effects);
        Assert.Equal(EffectType.CameraRain, entry.Effect.Type);
        Assert.Equal((1, 60, 600), (entry.Count, entry.RespawnMin, entry.RespawnMax));
        Assert.Equal("rain_light", WeatherEffectAdapter.FromName(db, "light rain").Effects.Single().Effect.Name);

        var group = (CameraEffectGroup)EffectGroups.Create(rain.Effects[0], library, 1)!;
        var camera = new EffectCamera(new Vector3(0, 100, 0), -Vector3.UnitZ);
        group.Prewarm(2, camera, rain);
        Assert.InRange(group.Simulation.ParticleCount, 500,
                5000);   // 6400 a second and a life of 0.3..1 s: about 4000 (the observer plane, world y -5.85, is far below the eye)
        // The cube: its edge 120 (2d / 1.5 for d = 0.75 x 120 = 90), centred 90 ahead of the eye; the simulation's coordinates are relative to the
        // group's anchor.
        var (x, y, z) = group.Simulation.Positions(0);
        Assert.Equal(new Vector3(0, 100, 0), group.Anchor);
        Assert.All(x, v => Assert.InRange(v + group.Anchor.X, -60f, 60f));
        Assert.All(z, v => Assert.InRange(v + group.Anchor.Z, -150f, -30f));
        Assert.All(y, v => Assert.InRange(v + group.Anchor.Y, 40f, 160f));
    }
}
