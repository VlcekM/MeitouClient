using System.Numerics;
using Meitou.Data.Particles;

namespace Meitou.Tests.World;

/// <summary>
/// How the weather's particle groups change (docs/formats/particle-universe.md "Weather changes"): the warm-up in two halves, the new groups made on
/// the side and swapped in when ready, the dwell before the camera's crossing into another region rebuilds anything, and the frame's simulation on
/// its own thread.
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

    /// <summary>
    /// A weather with one GLOBAL group of <paramref name="count"/> units (a list object of its own, as the scheduler keeps one per weather).
    /// </summary>
    static WeatherEffectInput Weather(string region = "", int count = 2, float strength = 1) =>
        new() { Effects = [new WeatherEffectEntry(Record(EffectType.Global), count, 0, 0)], Region = region, Strength = strength };

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
}
