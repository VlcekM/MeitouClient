using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class JoinTests
{
    static MoveOrder Order(long tick, float x) => new([new CharacterId(0, 0)], new Vector3(x, 0, 0)) { Tick = tick };

    [Fact]
    public void CommandsComeOutByTickThenQueueOrder()
    {
        var q = new CommandQueue();
        q.Enqueue(Order(5, 1));
        q.Enqueue(Order(3, 2));
        q.Enqueue(Order(5, 3));
        q.Enqueue(Order(9, 4));

        Assert.Empty(q.TakeDue(2));

        var due = q.TakeDue(5).Cast<MoveOrder>().Select(o => o.Target.X).ToArray();

        Assert.Equal([2f, 1f, 3f], due);
        Assert.Equal(1, q.Count);
    }
}
