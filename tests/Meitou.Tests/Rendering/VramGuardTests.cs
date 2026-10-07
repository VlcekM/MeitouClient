using Meitou.Rendering;

namespace Meitou.Tests.Rendering;

/// <summary>The memory-pressure guard's state machine (<see cref="VramGuard"/>), driven by injected memory fractions and a fake clock.</summary>
public class VramGuardTests
{
    const ulong Budget = 10_000;

    sealed class Rig
    {
        public double Fraction, Now;
        public readonly List<string> Log = [];
        public readonly VramGuard Guard;

        public Rig() => Guard = new VramGuard(() => ((ulong)(Fraction * Budget), Budget), () => Now, Log.Add);

        /// <summary>Runs <paramref name="seconds"/> of frames (60 a second) at the given use.</summary>
        public void Run(double seconds, double fraction)
        {
            Fraction = fraction;
            for (double end = Now + seconds; Now < end; Now += 1.0 / 60) Guard.Tick();
        }
    }

    [Fact]
    public void Stays_idle_below_the_high_mark()
    {
        var r = new Rig();
        r.Run(30, 0.35);
        r.Run(30, 0.89);
        Assert.False(r.Guard.Pressure);
        Assert.True(r.Guard.Streaming);
        Assert.Equal(1f, r.Guard.RangeScale);
        Assert.Equal(0, r.Guard.Activations);
        Assert.False(r.Guard.Active);
        Assert.Empty(r.Log);
        Assert.StartsWith("guard: ok", r.Guard.Status);
    }

    [Fact]
    public void Enters_pressure_at_90_percent_pauses_streaming_and_clamps_ranges_step_by_step()
    {
        var r = new Rig();
        r.Run(2, 0.5);
        r.Run(0.3, 0.92);
        Assert.True(r.Guard.Pressure);
        Assert.False(r.Guard.Streaming);
        Assert.Equal(1, r.Guard.Activations);
        Assert.Equal(VramGuard.StepDown, r.Guard.RangeScale, 4);   // the first step at once
        Assert.InRange(r.Guard.ExcessBytes, 1190, 1210);   // 92% less the low mark's 80% of 10,000
        r.Run(3, 0.92);
        Assert.True(r.Guard.RangeScale < 0.6f && r.Guard.RangeScale > 0.4f, $"scale {r.Guard.RangeScale}");
        Assert.Contains("streaming paused", r.Guard.Status);
        // Down to the floor and no further.
        r.Run(60, 0.95);
        Assert.Equal(VramGuard.MinScale, r.Guard.RangeScale, 4);
        Assert.Equal(VramGuard.MinScale, r.Guard.LowestScale, 4);
    }

    [Fact]
    public void Logs_once_however_often_it_triggers()
    {
        var r = new Rig();
        for (int i = 0; i < 3; i++)
        {
            r.Run(1, 0.95);
            r.Run(1, 0.5);
        }
        Assert.Equal(3, r.Guard.Activations);
        var line = Assert.Single(r.Log);
        Assert.Contains("guard", line);
        Assert.Contains("streaming paused", line);
    }

    [Fact]
    public void Pressure_ends_under_80_percent_and_the_ranges_come_back_only_after_calm_under_74()
    {
        var r = new Rig();
        r.Run(2, 0.95);
        float clamped = r.Guard.RangeScale;
        Assert.True(clamped < 1f);
        // Between the low mark and the high mark pressure stays (hysteresis).
        r.Run(5, 0.85);
        Assert.True(r.Guard.Pressure);
        // Under the low mark streaming resumes, but the ranges stay while the use is above the recovery mark.
        r.Run(10, 0.78);
        Assert.False(r.Guard.Pressure);
        Assert.True(r.Guard.Streaming);
        Assert.Equal(0, r.Guard.ExcessBytes);
        float held = r.Guard.RangeScale;
        Assert.True(held <= clamped);
        Assert.Contains("recovering", r.Guard.Status);
        // Calm under 74%: four seconds of rest, then 4% a second.
        r.Run(3, 0.60);
        Assert.Equal(held, r.Guard.RangeScale);
        r.Run(3, 0.60);
        Assert.True(r.Guard.RangeScale > held);
        r.Run(120, 0.60);
        Assert.Equal(1f, r.Guard.RangeScale);
        Assert.False(r.Guard.Active);
    }

    [Fact]
    public void A_relapse_while_recovering_clamps_again()
    {
        var r = new Rig();
        r.Run(2, 0.95);
        r.Run(5.5, 0.5);   // four seconds of calm, then a step or two up
        float recovering = r.Guard.RangeScale;
        Assert.InRange(recovering, 0.01f, 0.99f);
        r.Run(1, 0.93);
        Assert.True(r.Guard.Pressure);
        Assert.True(r.Guard.RangeScale < recovering);
        Assert.Equal(2, r.Guard.Activations);
    }

    [Fact]
    public void Samples_a_few_times_a_second_not_every_frame()
    {
        int samples = 0;
        double now = 0;
        var guard = new VramGuard(() => { samples++; return (100, 1000); }, () => now, _ => { });
        for (; now < 10; now += 1.0 / 60) guard.Tick();
        Assert.InRange(samples, 38, 41);
    }

    [Fact]
    public void An_unknown_budget_does_nothing()
    {
        var guard = new VramGuard(() => (5, 0), () => 100, _ => { });
        guard.Tick();
        Assert.False(guard.Pressure);
        Assert.Equal(1f, guard.RangeScale);
    }

    [Fact]
    public void Scratch_is_granted_past_the_ceiling_up_to_the_priority_ceiling_while_ordinary_allocations_are_refused()
    {
        var r = new Rig();
        r.Run(1, 0.915);
        Assert.False(r.Guard.Allows(100));            // 91.5% + 1% is past the 92% ceiling
        Assert.True(r.Guard.AllowsPriority(100));     // under the 94.5% priority ceiling
        Assert.Equal(1, r.Guard.PriorityGrants);
        Assert.False(r.Guard.AllowsPriority(400));    // 91.5% + 1% + 4% is past it
        Assert.Contains("scratch grants", r.Guard.Status);
    }

    [Fact]
    public void A_refused_scratch_squeezes_the_ranges_at_once_but_paced()
    {
        var r = new Rig();
        r.Run(1, 0.93);
        float before = r.Guard.RangeScale;
        Assert.False(r.Guard.AllowsPriority(900));
        float after = r.Guard.RangeScale;
        Assert.True(after < before, $"{before} -> {after}");
        Assert.False(r.Guard.AllowsPriority(900));     // in the same instant: no second step
        Assert.Equal(after, r.Guard.RangeScale);
        r.Now += 0.06;
        Assert.False(r.Guard.AllowsPriority(900));
        Assert.True(r.Guard.RangeScale < after);
        Assert.True(r.Guard.Squeezes >= 2);
    }
}
