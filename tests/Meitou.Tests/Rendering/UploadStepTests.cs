using Meitou.Game.Rendering.Terrain;


namespace Meitou.Tests.Rendering;

/// <summary>
/// The upload queues' budget rules and the size of upload steps (docs/renderer-native.md "Upload steps"): the first step of a run always runs, a
/// later one waits when steps of its kind have cost more than what is left, and nothing is written in one piece that is more than a slab.
/// </summary>
public class UploadStepTests
{
    sealed class Clock { public double Now; }

    static (UploadQueue Queue, Clock Clock) Queue()
    {
        var clock = new Clock();
        return (new UploadQueue(() => clock.Now), clock);
    }

    [Fact]
    public void The_first_step_runs_even_when_it_alone_overruns_the_budget_and_the_queue_moves_every_call()
    {
        var (q, clock) = Queue();
        int ran = 0;
        q.Add(() => { clock.Now += 10; ran++; }, "big");
        q.Add(() => { clock.Now += 10; ran++; }, "big");
        q.Run(2);

        Assert.Equal(1, ran);
        Assert.Equal(1, q.Count);

        q.Run(2);

        Assert.Equal(2, ran);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void A_step_of_a_kind_that_has_cost_more_than_what_is_left_waits_for_a_call_of_its_own()
    {
        var (q, clock) = Queue();
        void Small() => clock.Now += 0.1;
        void Heavy() => clock.Now += 3;
        q.Add(Heavy, "heavy");
        q.Run(2);   // learns that heavy costs 3 ms

        Assert.Equal(3, q.ExpectedMs("heavy"), 6);

        for (int i = 0; i < 3; i++) q.Add(Small, "small");
        q.Add(Heavy, "heavy");
        q.Add(Small, "small");
        q.Run(2);

        // The three small ones (0.3 ms) ran; heavy (3 ms) would end at 3.3 ms, more than the slack past the budget: it and the last small one wait.
        Assert.Equal(2, q.Count);
        Assert.StartsWith("small, small, small, ", q.LastRun);
        Assert.DoesNotContain("heavy", q.LastRun);

        q.Run(2);

        // Now heavy is first and runs; the budget is gone after it.
        Assert.StartsWith("heavy", q.LastRun);
        Assert.Equal(1, q.Count);
    }
}