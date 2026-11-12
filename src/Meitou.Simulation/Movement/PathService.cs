using System.Collections.Concurrent;
using System.Numerics;

namespace Meitou.Simulation.Movement;

/// <summary>
/// A path wanted by one character.
/// </summary>
public readonly record struct PathRequest(
    long Id, CharacterId Character, Vector3 From, Vector3 To, float Radius = 0, WaterCost Water = default, float RoadPreference = 0);

/// <summary>
/// The answer to a <see cref="PathRequest"/>: the path (empty when none).
/// </summary>
public readonly record struct PathAnswer(long Id, CharacterId Character, PathResult Path);

/// <summary>
/// Runs path queries off the tick (docs/simulation.md, "Threading": asynchronous services). In <c>synchronous</c> mode a query runs inside
/// <see cref="Submit"/> and its answer is waiting at once, which is what the determinism tests use; otherwise <see cref="Threads"/> threads of its
/// own answer, and an answer is taken in at the first tick after it is ready, so timing can differ between runs (as for the original, whose queries
/// go to a NavMesh thread). A request the same character has replaced by a newer one before a thread took it up is dropped unanswered (the newer
/// one's answer settles the walker). <see cref="IWalkability.FindPath"/> must be safe to call from several threads at once.
/// </summary>
public sealed class PathService : IDisposable
{
    /// <summary>
    /// The surface the paths are found on (its <c>FindPath</c> runs on the path threads).
    /// </summary>
    readonly IWalkability _walkability;
    /// <summary>
    /// The requests waiting for a path thread; null for a synchronous service.
    /// </summary>
    readonly BlockingCollection<PathRequest>? _requests;
    /// <summary>
    /// The answers ready, taken by <see cref="Drain"/>.
    /// </summary>
    readonly ConcurrentQueue<PathAnswer> _answers = new();

    /// <summary>
    /// Each character's newest request: an older one still queued is skipped.
    /// </summary>
    readonly ConcurrentDictionary<CharacterId, long> _newest = new();
    /// <summary>
    /// The path threads (none for a synchronous service).
    /// </summary>
    readonly Thread[] _threads = [];
    /// <summary>
    /// The last request id handed out (ids count from 1), and the requests dropped as replaced.
    /// </summary>
    long _nextId, _skipped;
    /// <summary>
    /// Requests asked and not answered or dropped yet.
    /// </summary>
    int _pending;
    /// <summary>
    /// Set by <see cref="Dispose"/>, which runs once.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// The path threads of an asynchronous service (engine choice: a quarter of the cores, 2 to 4; the simulation's own workers take half).
    /// </summary>
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 4, 2, 4);

    /// <summary>
    /// A path service over <paramref name="walkability"/>: <paramref name="synchronous"/> answers each request at once on the caller's thread;
    /// otherwise <see cref="Threads"/> background threads answer them.
    /// </summary>
    public PathService(IWalkability walkability, bool synchronous)
    {
        this._walkability = walkability;
        IsSynchronous = synchronous;
        if (synchronous) return;

        _requests = new BlockingCollection<PathRequest>();
        _threads = new Thread[Threads];
        for (int i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Name = $"Meitou.Sim paths {i + 1}" };
            _threads[i].Start();
        }
    }

    /// <summary>
    /// Whether requests are answered at once on the caller's thread (no path threads).
    /// </summary>
    public bool IsSynchronous { get; }

    /// <summary>
    /// Queries asked so far.
    /// </summary>
    public long Submitted => Interlocked.Read(ref _nextId);

    /// <summary>
    /// Requests dropped because the same character asked again before a thread took them up.
    /// </summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>
    /// Requests asked and not answered or dropped yet (queued or being searched).
    /// </summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// Asks for a path; returns the request id.
    /// </summary>
    public long Submit(CharacterId character, Vector3 from, Vector3 to, float radius = 0, WaterCost water = default, float roadPreference = 0)
    {
        long id = Interlocked.Increment(ref _nextId);
        var request = new PathRequest(id, character, from, to, radius, water, roadPreference);
        if (IsSynchronous)
        {
            _answers.Enqueue(Answer(request));
            return id;
        }

        _newest[character] = id;
        Interlocked.Increment(ref _pending);
        _requests!.Add(request);
        return id;
    }

    /// <summary>
    /// The path for one request: through <see cref="IAgentWalkability"/> when the request has a radius, water costs or a road preference and the
    /// surface takes them, else the plain query.
    /// </summary>
    PathAnswer Answer(PathRequest r)
    {
        bool agentQuery = r.Radius > 0 || r.Water.IsSet || r.RoadPreference > 0;
        var path = agentQuery && _walkability is IAgentWalkability agent
            ? agent.FindPath(r.From, r.To, r.Radius, r.Water, r.RoadPreference)
            : _walkability.FindPath(r.From, r.To);

        return new(r.Id, r.Character, path);
    }

    /// <summary>
    /// A path thread's loop: answers each queued request unless the same character has asked again since.
    /// </summary>
    void Work()
    {
        foreach (var r in _requests!.GetConsumingEnumerable())
        {
            if (_newest.TryGetValue(r.Character, out long latest) && latest != r.Id)
                Interlocked.Increment(ref _skipped);
            else
            {
                _answers.Enqueue(Answer(r));
                _newest.TryRemove(new KeyValuePair<CharacterId, long>(r.Character, r.Id));
            }

            Interlocked.Decrement(ref _pending);
        }
    }

    /// <summary>
    /// The answers ready now, by request id.
    /// </summary>
    public List<PathAnswer> Drain()
    {
        var list = new List<PathAnswer>();
        while (_answers.TryDequeue(out var a))
            list.Add(a);

        list.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    /// <summary>
    /// Stops the threads; safe to call twice (the owning system and the host may both dispose it).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _requests?.CompleteAdding();
        foreach (var thread in _threads)
            thread.Join();

        _requests?.Dispose();
    }
}
