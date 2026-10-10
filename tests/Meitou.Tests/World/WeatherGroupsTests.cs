using System.Numerics;
using Meitou.Data.Particles;

namespace Meitou.Tests.World;

/// <summary>
/// How the weather's particle groups change (docs/formats/particle-universe.md "Weather changes"): the warm-up in two halves, the new groups made on the
/// side and swapped in when ready, the dwell before the camera's crossing into another region rebuilds anything, and the frame's simulation on its own thread.
/// </summary>
public class WeatherGroupsTests
{
    const string Puff = """
        system test_puff
        {
            technique t
            {
                visual_particle_quota 400
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

    static readonly EffectCamera Camera = new(new Vector3(0, 200, 0), Vector3.UnitZ);

    static ParticleLibrary Library()
    {
        var library = new ParticleLibrary();
        library.AddScript(Puff, "test_puff.pu");
        return library;
    }

    static EffectWorld World() => new() { GroundHeight = (_, _) => 0, Area = new DiscArea(0, 0, 500) };

    static EffectRecord Record(EffectType type) =>
        new("e", type, "test_puff", false, false, 1, 0, 0, Vector3.One, 1, false, 0, 0, 0, 0, 10) { MinAltitude = 0, MaxAltitude = 0 };

    /// <summary>A weather with one GLOBAL group of <paramref name="count"/> units (a list object of its own, as the scheduler keeps one per weather).</summary>
    static WeatherEffectInput Weather(string region = "", int count = 2, float strength = 1) =>
        new() { Effects = [new WeatherEffectEntry(Record(EffectType.Global), count, 0, 0)], Region = region, Strength = strength };

    static WeatherEffectInput Clear(string region = "") => new() { Effects = [], Region = region };

    /// <summary>Calls <see cref="WeatherGroups.Advance"/> until the groups are replaced (the worker is quick for a few units); false after ten seconds.</summary>
    static bool AdvanceUntilChanged(WeatherGroups groups, double now)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (groups.Advance(Camera, now)) return true;
            Thread.Sleep(2);
        }
        return false;
    }

    [Fact]
    public void Staged_warm_up_queues_the_time_and_simulates_to_the_same_particles_as_prewarm()
    {
        var entry = new WeatherEffectEntry(Record(EffectType.Global), 2, 0, 0);
        var system = PuSystemDef.From(PuScriptReader.Parse(Puff).Single(n => n.Kind == "system"));
        var input = new WeatherEffectInput();
        var whole = new GlobalEffectGroup(entry, system, 1, World());
        whole.Prewarm(4, Camera, input);
        var staged = new GlobalEffectGroup(entry, system, 1, World());
        staged.BeginWarm(4, Camera, input);
        Assert.Equal(2, staged.Units.Count);
        Assert.All(staged.Units, u => Assert.True(u.Active && u.Pending >= 4 && u.CatchingUp));   // queued, hidden until caught up
        Assert.Equal(0, staged.ParticleCount);
        Assert.True(staged.SimulateSerial(CancellationToken.None));
        Assert.All(staged.Units, u => Assert.Equal(0, u.Pending, 4));
        Assert.True(whole.ParticleCount > 100, $"{whole.ParticleCount} particles");
        Assert.Equal(whole.ParticleCount, staged.ParticleCount);
    }

    [Fact]
    public void Cancelled_warm_up_stops_with_the_time_still_queued()
    {
        var entry = new WeatherEffectEntry(Record(EffectType.Global), 1, 0, 0);
        var group = new GlobalEffectGroup(entry, PuSystemDef.From(PuScriptReader.Parse(Puff).Single(n => n.Kind == "system")), 1, World());
        group.BeginWarm(30, Camera, new WeatherEffectInput());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.False(group.SimulateSerial(cancel.Token));
        Assert.True(group.Units[0].Pending > 0);
    }

    [Fact]
    public void The_first_groups_are_made_and_warmed_at_once()
    {
        using var groups = new WeatherGroups(Library(), World());
        groups.Want(Weather(), 0);
        Assert.Empty(groups.Groups);   // nothing happens before Advance
        Assert.True(groups.Advance(Camera, 0));
        var group = Assert.Single(groups.Groups);
        Assert.True(group.ParticleCount > 100);
        Assert.False(groups.Changing);
    }

    [Fact]
    public void A_change_inside_the_region_is_warmed_on_a_worker_and_swapped_in_when_ready()
    {
        using var groups = new WeatherGroups(Library(), World());
        var first = Weather("Vain", count: 1);
        groups.Want(first, 0);
        groups.Advance(Camera, 0);
        var shown = groups.Groups[0];
        var second = Weather("Vain", count: 3, strength: 0.5f);
        groups.Want(second, 1);
        Assert.False(groups.Advance(Camera, 1));          // the build starts; the old groups stay
        Assert.True(groups.Building);
        Assert.Same(shown, groups.Groups[0]);
        Assert.Single(groups.Groups);
        Assert.True(AdvanceUntilChanged(groups, 1));
        Assert.NotSame(shown, groups.Groups[0]);
        Assert.Equal(3, groups.Groups[0].Units.Count);
        Assert.Same(second, groups.Input);
        Assert.False(groups.Changing);
        // The same particles as a warm-up on the calling thread would give (same seed, same camera).
        var reference = new GlobalEffectGroup(second.Effects[0], PuSystemDef.From(PuScriptReader.Parse(Puff).Single(n => n.Kind == "system")), 1, World());
        reference.Prewarm(5, Camera, second);
        Assert.Equal(reference.ParticleCount, groups.Groups[0].ParticleCount);
    }

    [Fact]
    public void A_change_of_region_waits_for_the_dwell()
    {
        using var groups = new WeatherGroups(Library(), World()) { RegionDwell = 2 };
        var vain = Weather("Vain");
        groups.Want(vain, 0);
        groups.Advance(Camera, 0);
        var dreg = Clear("Dreg");
        groups.Want(dreg, 10);
        Assert.True(groups.Changing);
        Assert.False(groups.Advance(Camera, 11.5));
        Assert.False(groups.Building);
        Assert.Single(groups.Groups);
        Assert.True(groups.Advance(Camera, 12.1));        // held for two seconds; a clear weather has nothing to warm, so it is swapped in at once
        Assert.Empty(groups.Groups);
        Assert.Same(dreg, groups.Input);
    }

    [Fact]
    public void Flipping_back_to_the_shown_weather_forgets_the_change_and_builds_nothing()
    {
        using var groups = new WeatherGroups(Library(), World()) { RegionDwell = 2 };
        var vain = Weather("Vain");
        groups.Want(vain, 0);
        groups.Advance(Camera, 0);
        var shown = groups.Groups[0];
        for (int flip = 0; flip < 5; flip++)
        {
            double t = 10 + flip;
            groups.Want(Clear("Dreg"), t);
            Assert.False(groups.Advance(Camera, t + 0.4));
            groups.Want(vain, t + 0.5);                    // the same list object again
            Assert.False(groups.Changing);
            Assert.False(groups.Advance(Camera, t + 0.6));
            Assert.False(groups.Building);
        }
        Assert.Same(shown, groups.Groups[0]);
    }

    [Fact]
    public void A_change_is_dropped_while_building_when_the_weather_returns_to_the_shown_one()
    {
        using var groups = new WeatherGroups(Library(), World());
        var first = Weather("Vain", count: 1);
        groups.Want(first, 0);
        groups.Advance(Camera, 0);
        var shown = groups.Groups[0];
        groups.Want(Weather("Vain", count: 3), 1);
        groups.Advance(Camera, 1);
        Assert.True(groups.Building);
        groups.Want(first, 1.1);                           // back before the warm-up is swapped in
        Assert.False(groups.Advance(Camera, 1.2));
        Assert.False(groups.Building);
        Assert.Same(shown, groups.Groups[0]);
    }

    [Fact]
    public void A_newer_change_replaces_one_being_built()
    {
        using var groups = new WeatherGroups(Library(), World());
        groups.Want(Weather("Vain", count: 1), 0);
        groups.Advance(Camera, 0);
        groups.Want(Weather("Vain", count: 3), 1);
        groups.Advance(Camera, 1);
        var third = Weather("Vain", count: 4);
        groups.Want(third, 1.1);
        groups.Advance(Camera, 1.2);                       // the first build is dropped, the new one starts
        Assert.True(AdvanceUntilChanged(groups, 1.3));
        Assert.Equal(4, groups.Groups[0].Units.Count);
        Assert.Same(third, groups.Input);
    }

    [Fact]
    public void Strength_and_wind_of_the_shown_weather_pass_through_without_a_change()
    {
        using var groups = new WeatherGroups(Library(), World());
        var effects = new List<WeatherEffectEntry> { new(Record(EffectType.Global), 1, 0, 0) };
        groups.Want(new WeatherEffectInput { Effects = effects, Strength = 0.2f }, 0);
        groups.Advance(Camera, 0);
        var windier = new WeatherEffectInput { Effects = effects, Strength = 0.9f, Wind = new Vector2(40, 0) };
        groups.Want(windier, 1);
        Assert.False(groups.Changing);
        Assert.Same(windier, groups.Input);
    }

    [Fact]
    public void Without_a_background_a_change_is_made_at_once()
    {
        using var groups = new WeatherGroups(Library(), World()) { Background = false, RegionDwell = 5 };
        groups.Want(Weather("Vain", count: 1), 0);
        groups.Advance(Camera, 0);
        groups.Want(Weather("Dreg", count: 2), 1);
        Assert.True(groups.Advance(Camera, 1));
        Assert.Equal(2, groups.Groups[0].Units.Count);
        Assert.True(groups.Groups[0].ParticleCount > 100);
    }

    [Fact]
    public void A_forced_weather_has_no_region_and_is_not_held_back()
    {
        using var groups = new WeatherGroups(Library(), World()) { RegionDwell = 5 };
        groups.Want(Weather("Vain", count: 1), 0);
        groups.Advance(Camera, 0);
        groups.Want(Weather("", count: 2), 1);             // a forced weather names no region
        groups.Advance(Camera, 1);
        Assert.True(groups.Building);
    }

    [Fact]
    public void A_background_warm_up_reads_the_frozen_ground_height_and_the_groups_go_back_to_the_live_world()
    {
        int owner = Environment.CurrentManagedThreadId, liveCalls = 0, liveOffThread = 0, frozenOffThread = 0, frozenMade = 0, frozenCalls = 0;
        var world = new EffectWorld
        {
            GroundHeight = (_, _) =>
            {
                Interlocked.Increment(ref liveCalls);
                if (Environment.CurrentManagedThreadId != owner) Interlocked.Increment(ref liveOffThread);
                return 0;
            },
            FreezeGroundHeight = () =>
            {
                frozenMade++;
                if (Environment.CurrentManagedThreadId != owner) frozenOffThread++;
                return (_, _) => { Interlocked.Increment(ref frozenCalls); return 0; };
            },
            Area = new DiscArea(0, 0, 500),
        };
        var weather = Weather(count: 2);
        using var warmer = new EffectWarmer();
        var set = EffectSet.BeginInBackground(weather, Library(), 1, world, 4, Camera, warmer);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!set.Ready && DateTime.UtcNow < until) Thread.Sleep(2);
        Assert.True(set.Ready);
        Assert.Null(set.Failure);
        Assert.Equal((1, 0), (frozenMade, frozenOffThread));   // frozen once, on the calling thread
        Assert.True(frozenCalls > 0);
        Assert.Equal(0, liveCalls);                           // the build never touched the live function
        var group = Assert.Single(set.Groups);
        Assert.True(group.ParticleCount > 100);
        group.Update(0.05f, Camera, weather);                  // handed over: the group now asks the live world
        Assert.True(liveCalls > 0);
        Assert.Equal(0, liveOffThread);
    }

    [Fact]
    public void A_world_without_a_freeze_is_copied_with_its_own_height_function()
    {
        var area = new DiscArea(1, 2, 3);
        Func<float, float, float> height = static (_, _) => 7;
        var copy = new EffectWorld { GroundHeight = height, Area = area, ActiveRadius = 11, FallbackRadius = 12, Density = 0.5f }.Frozen();
        Assert.Same(height, copy.GroundHeight);
        Assert.Same(area, copy.Area);
        Assert.Equal((11f, 12f, 0.5f), (copy.ActiveRadius, copy.FallbackRadius, copy.Density));
    }

    [Fact]
    public void The_simulator_runs_the_units_off_the_calling_thread_and_the_caller_may_claim_the_job()
    {
        var entry = new WeatherEffectEntry(Record(EffectType.Global), 3, 0, 0);
        var group = new GlobalEffectGroup(entry, PuSystemDef.From(PuScriptReader.Parse(Puff).Single(n => n.Kind == "system")), 1, World());
        group.BeginWarm(0, Camera, new WeatherEffectInput());
        foreach (var u in group.Units) u.Pending = 0.5f;
        using var simulator = new EffectSimulator();
        for (int i = 0; i < 20; i++)
        {
            foreach (var u in group.Units) u.Pending = 0.2f;
            var job = simulator.Start([.. group.Units]);
            job.Wait();
            Assert.True(job.IsCompleted);
            Assert.All(group.Units, u => Assert.Equal(0, u.Pending, 4));
        }
        Assert.True(group.ParticleCount > 50);
    }
}
