using Meitou.Rendering;
using Meitou.Rendering.Impostors;

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
    public void Steps_run_until_the_budget_has_passed()
    {
        var (q, clock) = Queue();
        int ran = 0;
        for (int i = 0; i < 10; i++) q.Add(() => { clock.Now += 0.5; ran++; }, "small");
        q.Run(2);
        // 0.5 ms each: four fit in 2 ms; the budget has passed after the fourth.
        Assert.Equal(4, ran);
        Assert.Equal(6, q.Count);
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

    [Fact]
    public void A_step_that_ends_within_the_slack_past_the_budget_still_runs_so_small_steps_fill_the_budget_as_before()
    {
        var (q, clock) = Queue();
        void Medium() => clock.Now += 1.5;
        void Small() => clock.Now += 0.5;
        q.Add(Medium, "medium");
        q.Run(2);   // learns 1.5 ms
        q.Add(Small, "small");
        q.Add(Small, "small");
        q.Add(Medium, "medium");
        q.Add(Small, "small");
        q.Run(2);
        // 0.5 + 0.5 = 1.0 ms used, 1.0 left; medium would end at 2.5 ms: within the budget plus the 1 ms slack, so it runs.
        Assert.StartsWith("small, small, medium", q.LastRun);
        Assert.Equal(1, q.Count);
    }

    [Fact]
    public void A_kind_never_seen_is_started_and_one_stall_is_forgotten_after_a_few_steps()
    {
        var costs = new StepCosts();
        Assert.True(costs.Fits("new", 1.9, 2));   // nothing known: it fits
        costs.Learn("k", 0.2);
        costs.Learn("k", 20);   // a collection landed in this one
        Assert.False(costs.Fits("k", 0.5, 2));
        for (int i = 0; i < 6; i++) costs.Learn("k", 0.2);
        Assert.True(costs.Fits("k", 0.5, 2), $"{costs.Expected("k")}");
    }

    [Theory]
    [InlineData(ImpostorEncoding.Bc1, 3072)]
    [InlineData(ImpostorEncoding.Bc3, 3072)]
    [InlineData(ImpostorEncoding.Bc5, 1536)]
    [InlineData(ImpostorEncoding.Rgba8, 768)]
    [InlineData(ImpostorEncoding.Bc1, 96)]
    [InlineData(ImpostorEncoding.Bc1, 4)]
    public void An_impostor_level_is_written_in_slabs_that_are_whole_rows_and_together_the_whole_level(ImpostorEncoding encoding, int size)
    {
        int bytes = ImpostorTexture.LevelBytes(encoding, size);
        int steps = ImpostorTextures.StepsOfLevel(encoding, size);
        int atLeast = (bytes + ImpostorTextures.SlabBytes - 1) / ImpostorTextures.SlabBytes;
        Assert.InRange(steps, atLeast, atLeast + 1);   // as many as the slab size asks for (a whole row may push one over)
        Assert.True(steps >= 1);
        // A row is bytes / rows; a slab is a whole number of them and at most SlabBytes unless one row is larger.
        int rows = encoding == ImpostorEncoding.Rgba8 ? size : size / 4;
        int rowBytes = bytes / rows;
        int perSlab = Math.Max(1, ImpostorTextures.SlabBytes / rowBytes);
        Assert.Equal((rows + perSlab - 1) / perSlab, steps);
        Assert.True((long)perSlab * rowBytes <= Math.Max(ImpostorTextures.SlabBytes, rowBytes));
    }
}