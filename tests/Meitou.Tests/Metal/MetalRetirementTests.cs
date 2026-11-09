#if MEITOU_METAL
using Meitou.Game.Rendering.Metal;

namespace Meitou.Tests.MetalBackend;

public sealed class MetalRetirementTests
{
    [Fact]
    public void DeferredReleaseWaitsForTheWholeFrameRing()
    {
        var queue = new DeferredReleaseQueue();
        int released = 0;
        queue.Add(frame: 10, () => released++);

        Assert.Equal(0, queue.ReleaseDue(currentFrame: 12, ringDepth: 3, budget: 64));
        Assert.Equal(0, released);

        Assert.Equal(1, queue.ReleaseDue(currentFrame: 13, ringDepth: 3, budget: 64));
        Assert.Equal(1, released);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void DeferredReleaseHonoursItsPerFrameBudget()
    {
        var queue = new DeferredReleaseQueue();
        int released = 0;
        for (int i = 0; i < 100; i++) queue.Add(frame: 10, () => released++);

        Assert.Equal(64, queue.ReleaseDue(currentFrame: 13, ringDepth: 3, budget: 64));
        Assert.Equal(64, released);
        Assert.Equal(36, queue.Count);

        Assert.Equal(36, queue.ReleaseDue(currentFrame: 13, ringDepth: 3, budget: 64));
        Assert.Equal(100, released);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void ForcedDeferredReleaseIgnoresTheFrameWindow()
    {
        var queue = new DeferredReleaseQueue();
        int released = 0;
        queue.Add(frame: 100, () => released++);

        Assert.Equal(1, queue.ReleaseDue(currentFrame: 100, ringDepth: 3, budget: 64, force: true));
        Assert.Equal(1, released);
    }

    [Fact]
    public void DeferredReleaseRunsActionsOutsideTheQueueLock()
    {
        var queue = new DeferredReleaseQueue();
        int released = 0;
        queue.Add(frame: 0, () =>
        {
            // A release is allowed to enqueue more work (the MetalFX scaler path can do this while unwinding).
            queue.Add(frame: 0, () => released++);
        });

        Assert.Equal(1, queue.ReleaseDue(currentFrame: 3, ringDepth: 3, budget: 64));
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, released);

        Assert.Equal(1, queue.ReleaseDue(currentFrame: 3, ringDepth: 3, budget: 64));
        Assert.Equal(1, released);
    }
}
#endif
