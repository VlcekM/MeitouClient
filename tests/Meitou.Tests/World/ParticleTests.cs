using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Particles;

namespace Meitou.Tests.World;

/// <summary>ParticleUniverse scripts and their CPU simulation (docs/formats/particle-universe.md).</summary>
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
    public void Dynamic_curves_and_oscillation()
    {
        var s = System("""
            system d
            {
                technique
                {
                    emitter Point
                    {
                        emission_rate dyn_oscillate
                        {
                            oscillate_frequency 0.5
                            oscillate_phase 0
                            oscillate_base 20
                            oscillate_amplitude 10
                            oscillate_type sine
                        }
                    }
                    affector Scale
                    {
                        xyz_scale dyn_curved_linear
                        {
                            control_point 0 2
                            control_point 1 6
                        }
                    }
                    affector Scale
                    {
                        x_scale dyn_curved_spline
                        {
                            control_point 0 0
                            control_point 0.5 4
                            control_point 1 0
                        }
                    }
                }
            }
            """);
        var t = s.Techniques[0];
        Assert.Equal(20, t.Emitters[0].Rate.Evaluate(0), 4);
        Assert.Equal(30, t.Emitters[0].Rate.Evaluate(0.5f), 3);   // a quarter of a period at 0.5 Hz: sin(π/2)
        var linear = t.Affectors[0].ScaleXyz!;
        Assert.Equal(2, linear.Evaluate(-1), 4);
        Assert.Equal(4, linear.Evaluate(0.5f), 4);
        Assert.Equal(6, linear.Evaluate(2), 4);
        var spline = t.Affectors[1].ScaleX!;
        Assert.Equal(4, spline.Evaluate(0.5f), 4);                 // passes through its control points
        Assert.Equal(0, spline.Evaluate(1), 4);
    }

    [Fact]
    public void Malformed_scripts_throw()
    {
        Assert.Throws<PuScriptException>(() => PuScriptReader.Parse("system a\n{\ntechnique\n{\n}\n"));
        Assert.Throws<PuScriptException>(() => PuScriptReader.Parse("}\n"));
        Assert.Throws<PuScriptException>(() => PuScriptReader.Parse("system a\n{\nvelocity dyn_bogus\n{\n}\n}\n"));
    }

    static ParticleSimulation RainSim(int seed)
    {
        var sim = new ParticleSimulation(System(Rain), seed) { Origin = new Vector3(1000, 50, -300) };
        sim.Advance(1.5f, ParticleEnvironment.None);
        return sim;
    }

    [Fact]
    public void Same_seed_gives_the_same_particles_and_another_seed_does_not()
    {
        var a = RainSim(7).Positions(0);
        var b = RainSim(7).Positions(0);
        var c = RainSim(8).Positions(0);
        Assert.NotEmpty(a.X);
        Assert.Equal(a.X, b.X);
        Assert.Equal(a.Y, b.Y);
        Assert.Equal(a.Z, b.Z);
        Assert.NotEqual(a.X, c.X);
    }

    [Fact]
    public void Steady_state_count_is_rate_times_life_and_the_observer_ends_particles_below_the_plane()
    {
        var sim = RainSim(1);
        var (x, y, z) = sim.Positions(0);
        // 600 per second; a particle lives 0.3..1 s but dies at y < -5.85 relative to the origin when it falls that far first.
        Assert.InRange(sim.TechniqueParticleCount(0), 100, 600);
        Assert.All(y, v => Assert.True(v >= -5.85f - 182f / 20 - 1e-3f, $"y {v}"));   // the observer compares the world position; a step is at most 182 / 20 long
        var (age, life) = sim.Ages(0);
        Assert.All(age.Zip(life), p => Assert.True(p.First < p.Second));
        Assert.All(z, v => Assert.InRange(v, -300 - 60, -300 + 60));   // the box is 100 deep: ±50 (and a little drift)
    }

    [Fact]
    public void Quota_caps_the_pool()
    {
        var s = System("system q\n{\ntechnique\n{\nvisual_particle_quota 40\nemitter Point\n{\nemission_rate 100\ntime_to_live 50\nvelocity 1\n}\n}\n}\n");
        var sim = new ParticleSimulation(s, 3);
        sim.Advance(2, ParticleEnvironment.None);
        Assert.Equal(40, sim.TechniqueParticleCount(0));
    }

    [Fact]
    public void Wrapping_keeps_particles_inside_the_cube_modulo_its_edge()
    {
        var s = System("""
            system w
            {
                technique
                {
                    visual_particle_quota 100
                    emitter Point
                    {
                        emission_rate 100
                        time_to_live 50
                        velocity 40
                        direction 1 0 0
                    }
                }
            }
            """);
        var sim = new ParticleSimulation(s, 1);
        for (int i = 0; i < 40; i++)
        {
            sim.Advance(0.25f, ParticleEnvironment.None);
            sim.Wrap(Vector3.Zero, 30);
        }
        var (x, y, z) = sim.Positions(0);
        Assert.NotEmpty(x);
        Assert.All(x, v => Assert.InRange(v, -30f, 30f));
        Assert.All(y, v => Assert.InRange(v, -30f, 30f));
        // A particle that left through +x came back in at -x: some are on both sides.
        Assert.Contains(x, v => v < -10);
        Assert.Contains(x, v => v > 10);
    }

    [Fact]
    public void Colour_ramp_and_scale_rate_follow_the_particles_life()
    {
        var s = System("""
            system c
            {
                technique
                {
                    default_particle_width 10
                    default_particle_height 10
                    emitter Point
                    {
                        emission_rate 1
                        time_to_live 4
                        velocity 0
                        colour 1 1 1 1
                    }
                    affector Colour
                    {
                        time_colour 0 0 0 0 0
                        time_colour 0.5 1 0.5 0.25 1
                        time_colour 1 0 0 0 0
                    }
                    affector Scale
                    {
                        xyz_scale 5
                    }
                }
            }
            """);
        var sim = new ParticleSimulation(s, 1);
        sim.Advance(1.5f, ParticleEnvironment.None);   // one particle, 1.5 s... the first was emitted at 1 s: 0.5 s old
        var buffer = new ParticleInstance[8];
        int n = sim.Collect(0, buffer, Vector3.One, 1);
        Assert.Equal(1, n);
        float age = sim.Ages(0).Age[0];
        float f = age / 4;
        Assert.Equal(f / 0.5f, buffer[0].Colour.X, 2);
        Assert.Equal(10 + 5 * age, buffer[0].Width, 2);   // 5 units per second added
    }

    [Fact]
    public void Wind_span_scales_the_emission()
    {
        var effect = new EffectRecord("e", EffectType.Camera, "s", true, false, 1, 0.2f, 40, Vector3.One, 1, false, 0, 0, 0, 30, 10);
        Assert.Equal(0, EffectGroup.WindSpanScale(effect, 0.2f), 4);
        Assert.Equal(0.5f, EffectGroup.WindSpanScale(effect, 20.1f), 3);
        Assert.Equal(1, EffectGroup.WindSpanScale(effect, 100), 4);
        Assert.Equal(1, EffectGroup.WindSpanScale(effect with { MinWindSpan = 0, MaxWindSpan = 0 }, 0), 4);
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
        Assert.All(library.Systems.Values.SelectMany(s => s.Techniques), tech => Assert.True(tech.Material.Length == 0 || library.FindMaterial(tech.Material) is not null || tech.Material == "SandBlown_Streaks", tech.Material));
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

        var group = EffectGroups.Create(rain.Effects[0], library, 1)!;
        var camera = new EffectCamera(new Vector3(0, 100, 0), -Vector3.UnitZ);
        group.Prewarm(2, camera, rain);
        Assert.InRange(group.Simulation.ParticleCount, 500, 5000);   // 6400 a second and a life of 0.3..1 s: about 4000 (the observer plane, world y -5.85, is far below the eye)
        // The cube: its edge 120 (2d / 1.5 for d = 0.75 x 120 = 90), centred 90 ahead of the eye; the simulation's coordinates are relative to the group's anchor.
        var (x, y, z) = group.Simulation.Positions(0);
        Assert.Equal(new Vector3(0, 100, 0), group.Anchor);
        Assert.All(x, v => Assert.InRange(v + group.Anchor.X, -60f, 60f));
        Assert.All(z, v => Assert.InRange(v + group.Anchor.Z, -150f, -30f));
        Assert.All(y, v => Assert.InRange(v + group.Anchor.Y, 40f, 160f));
    }
}
