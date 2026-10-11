using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Particles;
using Meitou.Data.World;
using EffectType = Meitou.Data.Particles.EffectType;
using WeatherEffectEntry = Meitou.Data.Particles.WeatherEffectEntry;

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
        Assert.Equal(-2, linear.Evaluate(-1), 4);                  // below the first point the first segment goes on
        Assert.Equal(4, linear.Evaluate(0.5f), 4);
        Assert.Equal(6, linear.Evaluate(2), 4);                    // above the last it holds
        var spline = t.Affectors[1].ScaleX!;
        Assert.Equal(4, spline.Evaluate(0.5f), 4);                 // passes through its control points
        Assert.Equal(0, spline.Evaluate(1), 4);
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

    const string Puff = """
        system test_puff
        {
            technique t
            {
                visual_particle_quota 200
                emitter Box
                {
                    emission_rate 50
                    time_to_live 5
                    velocity 1
                    direction 0 1 0
                    box_width 10
                    box_height 10
                }
            }
        }
        """;

    static EffectRecord Record(EffectType type, float view = 0, float min = 0, float max = 0) =>
        new("e", type, "test_puff", false, false, 1, 0, 0, Vector3.One, 1, false, view, 0, min, max, 10) { MinAltitude = 0, MaxAltitude = 0 };

    [Fact]
    public void Region_area_picks_inside_the_middle_80_percent_of_a_cell()
    {
        var area = new RegionArea([(0f, 0f, 1000f, 1000f), (5000f, 0f, 6000f, 1000f)]);
        var random = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            Assert.True(area.TryPick(random, out float x, out float z));
            Assert.InRange(z, 100f, 900f);
            Assert.True(x is >= 100 and <= 900 or >= 5100 and <= 5900, $"x {x}");
        }
        Assert.False(new RegionArea([]).TryPick(random, out _, out _));
    }

    [Fact]
    public void Placement_looks_for_the_altitude_window()
    {
        var world = new EffectWorld { GroundHeight = (x, _) => x > 0 ? 500 : 50, Area = new DiscArea(0, 0, 1000) };
        var effect = Record(EffectType.Point) with { MinAltitude = 200, MaxAltitude = 1000 };
        var random = new Random(5);
        int high = 0;
        for (int i = 0; i < 100; i++) if (world.Place(effect, random, new EffectCamera(Vector3.Zero, Vector3.UnitZ)).Y == 500) high++;
        Assert.True(high >= 98, $"{high} of 100 in the window");   // 10 tries, each half likely
        // Both limits 0: any height.
        Assert.Contains(Enumerable.Range(0, 50).Select(_ => world.Place(Record(EffectType.Point), random, default).Y), y => y == 50);
    }

    [Fact]
    public void Respawning_group_keeps_to_its_count_and_global_group_makes_all_at_once()
    {
        var system = System(Puff);
        var world = new EffectWorld { Area = new DiscArea(0, 0, 500) };
        var camera = new EffectCamera(new Vector3(0, 200, 0), Vector3.UnitZ);
        var input = new WeatherEffectInput();
        var point = new RespawningEffectGroup(new WeatherEffectEntry(Record(EffectType.Point, min: 0, max: 0), 3, 0, 0), system, 1, world);
        for (int i = 0; i < 10; i++) point.Update(0.5f, camera, input);
        Assert.Equal(3, point.Units.Count);
        var unlimited = new RespawningEffectGroup(new WeatherEffectEntry(Record(EffectType.Point), 0, 1, 1), system, 1, world);
        for (int i = 0; i < 20; i++) unlimited.Update(1f, camera, input);
        Assert.InRange(unlimited.Units.Count, 15, 21);   // count 0: no limit, one a second
        var global = new GlobalEffectGroup(new WeatherEffectEntry(Record(EffectType.Global, view: 300, min: 5, max: 10), 4, 100, 200), system, 1, world);
        global.Update(0.1f, camera, input);
        Assert.Equal(4, global.Units.Count);
        Assert.All(global.Units, u => Assert.True(float.IsPositiveInfinity(u.Life)));
        var none = new GlobalEffectGroup(new WeatherEffectEntry(Record(EffectType.Global), 0, 1, 1), system, 1, world);
        none.Update(1f, camera, input);
        Assert.Empty(none.Units);
    }

    [Fact]
    public void Global_point_group_rings_use_horizontal_distance_and_emit_with_the_eye_high()
    {
        var system = System(Puff);
        var world = new EffectWorld { GroundHeight = (_, _) => 0, Area = new DiscArea(0, 0, 500) };
        var camera = new EffectCamera(new Vector3(0, 400, 0), Vector3.UnitZ);   // 400 up, the effect's ring only 50 wide
        var group = new GlobalPointEffectGroup(new WeatherEffectEntry(Record(EffectType.GlobalPoint, view: 50, min: 40, max: 60), 1, 0, 0), system, 1, world);
        group.Prewarm(5, camera, new WeatherEffectInput());
        Assert.InRange(group.Units.Count, 1, 3);
        Assert.All(group.Units, u => Assert.True(Math.Sqrt(u.Position.X * u.Position.X + u.Position.Z * u.Position.Z) <= 100));
        Assert.True(group.ParticleCount > 50, $"{group.ParticleCount} particles");
    }

    [Fact]
    public void Map_placers_make_one_static_immortal_unit_per_placement()
    {
        var path = Path.Combine(Path.GetTempPath(), "meitou-puff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "scripts"));
        Directory.CreateDirectory(Path.Combine(path, "materials"));
        try
        {
            File.WriteAllText(Path.Combine(path, "scripts", "test_puff.pu"), Puff);
            var library = ParticleLibrary.Load(path);
            var effect = Record(EffectType.Point, view: 200);
            var placements = new[] { new Vector3(100, 5, 100), new Vector3(9000, 5, 100) }.Select(p => new MapEffectPlacement(effect, p, "placer")).ToList();
            var world = new EffectWorld();
            var group = Assert.Single(MapEffectPlacers.Groups(placements, library, world, 1));
            Assert.Equal(2, group.Units.Count);
            Assert.All(group.Units, u => Assert.True(float.IsPositiveInfinity(u.Life)));
            group.Update(0.5f, new EffectCamera(new Vector3(0, 50, 0), Vector3.UnitZ), new WeatherEffectInput());
            Assert.True(group.Units[0].Active);    // in range
            Assert.False(group.Units[1].Active);   // 9000 away: past the active radius plus 200
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void Backlog_is_caught_up_over_several_frames()
    {
        var unit = new EffectUnit(Record(EffectType.Point), System(Puff), 1) { Position = Vector3.Zero, Anchor = Vector3.Zero };
        unit.Activate();
        unit.Pending = 30;
        unit.Advance();
        Assert.True(unit.Pending > 0 && unit.Pending < 30);   // at most 40 steps a call
        Assert.True(unit.CatchingUp || unit.Pending < 1);
        for (int i = 0; i < 20 && unit.Pending > 0; i++) unit.Advance();
        Assert.Equal(0, unit.Pending, 4);
        Assert.True(unit.Simulation!.ParticleCount > 100);
    }

    [Fact]
    [Slow]
    public void Twister_effects_carry_their_fog_volumes_and_placers_read_the_map()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var db = GameDatabase.Load(LoadOrder.BaseGame(install!));
        var storm = WeatherEffectAdapter.FromName(db, "Twister Storm");
        var twister = storm.Effects.Select(e => e.Effect).First(e => e.FogVolumes.Count > 0);
        Assert.All(twister.FogVolumes, f => Assert.InRange(f.Radius, 100f, 3000f));
        var placements = MapEffectPlacers.Find(db, MapFeatureFile.Open(install!));
        Assert.Equal(24, placements.Count(p => p.Effect.Name == "Volk-Cloud"));
        Assert.Equal(18, placements.Count(p => p.Effect.Name == "Volc-small-steamers"));
        Assert.Equal(1, placements.Count(p => p.Effect.Name == "Permanent-dust-storm"));
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
    public void Drifting_foliage_emits_in_the_wind()
    {
        var install = GameInstall.Locate();
        Assert.SkipWhen(install is null, $"No Kenshi install configured ({GameInstall.EnvironmentVariable}).");
        var library = ParticleLibrary.Load(install!);
        var sim = new ParticleSimulation(library.FindSystem("Drifting-foliage")!, 1);
        var env = new ParticleEnvironment(new Vector2(60, 0), 1, 1, true, false);
        sim.Prewarm(5, env);
        Assert.True(sim.ParticleCount > 100, $"{sim.ParticleCount} particles");
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
        Assert.InRange(group.Simulation.ParticleCount, 500, 5000);   // 6400 a second and a life of 0.3..1 s: about 4000 (the observer plane, world y -5.85, is far below the eye)
        // The cube: its edge 120 (2d / 1.5 for d = 0.75 x 120 = 90), centred 90 ahead of the eye; the simulation's coordinates are relative to the group's anchor.
        var (x, y, z) = group.Simulation.Positions(0);
        Assert.Equal(new Vector3(0, 100, 0), group.Anchor);
        Assert.All(x, v => Assert.InRange(v + group.Anchor.X, -60f, 60f));
        Assert.All(z, v => Assert.InRange(v + group.Anchor.Z, -150f, -30f));
        Assert.All(y, v => Assert.InRange(v + group.Anchor.Y, 40f, 160f));
    }
}
