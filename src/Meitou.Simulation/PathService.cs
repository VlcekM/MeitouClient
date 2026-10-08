using System.Collections.Concurrent;
using System.Numerics;

namespace Meitou.Simulation;

/// <summary>A path wanted by one character.</summary>
public readonly record struct PathRequest(long Id, CharacterId Character, Vector3 From, Vector3 To, float Radius = 0, float WaterFactor = 0);

/// <summary>The answer to a <see cref="PathRequest"/>: the path (empty when none).</summary>
public readonly record struct PathAnswer(long Id, CharacterId Character, PathResult Path);

/// <summary>
/// Runs path queries off the tick (docs/simulation.md, "Threading": asynchronous services). In <c>synchronous</c> mode a query runs
/// inside <see cref="Submit"/> and its answer is waiting at once, which is what the determinism tests use; otherwise a thread of its
/// own answers, and an answer is taken in at the first tick after it is ready, so timing can differ between runs (as for the original,
/// whose queries go to a NavMesh thread). <see cref="IWalkability.FindPath"/> must be safe to call from that thread.
/// </summary>
public sealed class PathService : IDisposable
{
    readonly IWalkability walkability;
    readonly BlockingCollection<PathRequest>? requests;
    readonly ConcurrentQueue<PathAnswer> answers = new();
    readonly Thread? thread;
    long nextId;

    public PathService(IWalkability walkability, bool synchronous)
    {
        this.walkability = walkability;
        IsSynchronous = synchronous;
        if (synchronous) return;
        requests = new BlockingCollection<PathRequest>();
        thread = new Thread(Work) { IsBackground = true, Name = "Meitou.Sim paths" };
        thread.Start();
    }

    public bool IsSynchronous { get; }

    /// <summary>Queries asked so far.</summary>
    public long Submitted => Interlocked.Read(ref nextId);

    /// <summary>Asks for a path; returns the request id.</summary>
    public long Submit(CharacterId character, Vector3 from, Vector3 to, float radius = 0, float waterFactor = 0)
    {
        long id = Interlocked.Increment(ref nextId);
        var request = new PathRequest(id, character, from, to, radius, waterFactor);
        if (IsSynchronous) answers.Enqueue(Answer(request));
        else requests!.Add(request);
        return id;
    }

    PathAnswer Answer(PathRequest r) => new(r.Id, r.Character, (r.Radius > 0 || r.WaterFactor > 0) && walkability is IAgentWalkability agent ? agent.FindPath(r.From, r.To, r.Radius, r.WaterFactor) : walkability.FindPath(r.From, r.To));

    void Work()
    {
        foreach (var r in requests!.GetConsumingEnumerable()) answers.Enqueue(Answer(r));
    }

    /// <summary>The answers ready now, by request id.</summary>
    public List<PathAnswer> Drain()
    {
        var list = new List<PathAnswer>();
        while (answers.TryDequeue(out var a)) list.Add(a);
        list.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    bool disposed;

    /// <summary>Stops the thread; safe to call twice (the owning system and the host may both dispose it).</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        requests?.CompleteAdding();
        thread?.Join();
        requests?.Dispose();
    }
}
