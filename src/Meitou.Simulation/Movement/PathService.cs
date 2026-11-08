using System.Collections.Concurrent;
using System.Numerics;

namespace Meitou.Simulation;

/// <summary>A path wanted by one character.</summary>
public readonly record struct PathRequest(long Id, CharacterId Character, Vector3 From, Vector3 To, float Radius = 0, WaterCost Water = default, float RoadPreference = 0);

/// <summary>The answer to a <see cref="PathRequest"/>: the path (empty when none).</summary>
public readonly record struct PathAnswer(long Id, CharacterId Character, PathResult Path);

/// <summary>
/// Runs path queries off the tick (docs/simulation.md, "Threading": asynchronous services). In <c>synchronous</c> mode a query runs
/// inside <see cref="Submit"/> and its answer is waiting at once, which is what the determinism tests use; otherwise <see cref="Threads"/> threads of its
/// own answer, and an answer is taken in at the first tick after it is ready, so timing can differ between runs (as for the original,
/// whose queries go to a NavMesh thread). A request the same character has replaced by a newer one before a thread took it up is dropped
/// unanswered (the newer one's answer settles the walker). <see cref="IWalkability.FindPath"/> must be safe to call from several threads at once.
/// </summary>
public sealed class PathService : IDisposable
{
    readonly IWalkability walkability;
    readonly BlockingCollection<PathRequest>? requests;
    readonly ConcurrentQueue<PathAnswer> answers = new();
    // Each character's newest request: an older one still queued is skipped.
    readonly ConcurrentDictionary<CharacterId, long> newest = new();
    readonly Thread[] threads = [];
    long nextId, skipped;
    int pending;

    /// <summary>The path threads of an asynchronous service (engine choice: a quarter of the cores, 2 to 4; the simulation's own workers take half).</summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    public PathService(IWalkability walkability, bool synchronous)
    {
        this.walkability = walkability;
        IsSynchronous = synchronous;
        if (synchronous) return;
        requests = new BlockingCollection<PathRequest>();
        threads = new Thread[Threads];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(Work) { IsBackground = true, Name = $"Meitou.Sim paths {i + 1}" };
            threads[i].Start();
        }
    }

    public bool IsSynchronous { get; }

    /// <summary>Queries asked so far.</summary>
    public long Submitted => Interlocked.Read(ref nextId);

    /// <summary>Requests dropped because the same character asked again before a thread took them up.</summary>
    public long Skipped => Interlocked.Read(ref skipped);

    /// <summary>Requests asked and not answered or dropped yet (queued or being searched).</summary>
    public int Pending => Volatile.Read(ref pending);

    /// <summary>Asks for a path; returns the request id.</summary>
    public long Submit(CharacterId character, Vector3 from, Vector3 to, float radius = 0, WaterCost water = default, float roadPreference = 0)
    {
        long id = Interlocked.Increment(ref nextId);
        var request = new PathRequest(id, character, from, to, radius, water, roadPreference);
        if (IsSynchronous) { answers.Enqueue(Answer(request)); return id; }
        newest[character] = id;
        Interlocked.Increment(ref pending);
        requests!.Add(request);
        return id;
    }

    PathAnswer Answer(PathRequest r) => new(r.Id, r.Character, (r.Radius > 0 || r.Water.IsSet || r.RoadPreference > 0) && walkability is IAgentWalkability agent
        ? agent.FindPath(r.From, r.To, r.Radius, r.Water, r.RoadPreference) : walkability.FindPath(r.From, r.To));

    void Work()
    {
        foreach (var r in requests!.GetConsumingEnumerable())
        {
            if (newest.TryGetValue(r.Character, out long latest) && latest != r.Id) Interlocked.Increment(ref skipped);
            else
            {
                answers.Enqueue(Answer(r));
                newest.TryRemove(new KeyValuePair<CharacterId, long>(r.Character, r.Id));
            }
            Interlocked.Decrement(ref pending);
        }
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

    /// <summary>Stops the threads; safe to call twice (the owning system and the host may both dispose it).</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        requests?.CompleteAdding();
        foreach (var thread in threads) thread.Join();
        requests?.Dispose();
    }
}
