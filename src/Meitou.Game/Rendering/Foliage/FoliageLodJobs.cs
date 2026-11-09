using System.Diagnostics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// The jobs that make the generated levels of TERRAIN-mode meshes (docs/formats/foliage.md, "Generated levels"): one per mesh file on a worker,
/// building the levels or reading them from the disk cache (<see cref="FoliageLodCache"/>), and what they did. <see cref="FoliageRenderer"/> uploads
/// the finished sets next to the meshes' own buffers.
/// </summary>
sealed class FoliageLodJobs(AssetLocator assets, List<string> messages, Func<string, bool, Model?> decode)
{
    #region Jobs

    /// <summary>
    /// A job's result: the levels (null: none worth making), whether they came from the cache, its CPU time and the cache bytes it wrote.
    /// </summary>
    sealed record LodResult(FoliageLodSet? Set, bool Cached, double Ms, long FileBytes);

    /// <summary>
    /// The jobs not finished, and the meshes waiting for a free one (as of the last <see cref="Pump"/>).
    /// </summary>
    int _lodRunning, _lodWaiting;

    /// <summary>
    /// The level jobs by mesh file (kept when done).
    /// </summary>
    readonly Dictionary<string, Task<LodResult>> _lodTasks = [];

    /// <summary>
    /// The meshes whose levels were built, read from the cache, found to need none; the cache bytes written.
    /// </summary>
    long _lodBuilt, _lodCacheHits, _lodNone, _lodFileBytes;

    /// <summary>
    /// CPU time on the workers, building and reading.
    /// </summary>
    double _lodBuildMs, _lodLoadMs;

    /// <summary>
    /// The finished sets of the last <see cref="Pump"/> to upload, in the order the meshes were walked: the mesh, the main mesh they are for, the
    /// levels.
    /// </summary>
    readonly List<(FoliageMeshAsset Asset, FoliageGpuMesh Main, FoliageLodSet Set)> _finished = [];

    /// <summary>
    /// The jobs not finished (as of the last <see cref="Pump"/>).
    /// </summary>
    public int Running => _lodRunning;

    /// <summary>
    /// The meshes waiting for a free job (as of the last <see cref="Pump"/>).
    /// </summary>
    public int Waiting => _lodWaiting;

    /// <summary>
    /// Some mesh got its levels (built or from the cache).
    /// </summary>
    public bool AnyMade => _lodBuilt + _lodCacheHits > 0;

    /// <summary>
    /// Generated levels: the meshes built, read from the cache, found to need none; the cache bytes written; CPU time on the workers (building,
    /// reading).
    /// </summary>
    public string Summary => $"{_lodBuilt} built ({_lodBuildMs:0} ms), {_lodCacheHits} from the cache ({_lodLoadMs:0} ms), " +
        $"{_lodNone} without a level; {_lodFileBytes / 1024} KB written";

    /// <summary>
    /// Every job made (for waiting on them at the end).
    /// </summary>
    public IEnumerable<Task> Tasks => _lodTasks.Values;

    /// <summary>
    /// The finished sets of the last <see cref="Pump"/> to upload, in the order the meshes were walked.
    /// </summary>
    public List<(FoliageMeshAsset Asset, FoliageGpuMesh Main, FoliageLodSet Set)> Finished => _finished;

    /// <summary>
    /// Starts and collects the level jobs of the resident TERRAIN-mode meshes among <paramref name="meshes"/> (once a frame; nothing but the count
    /// unless <paramref name="enabled"/>). One job per mesh file, however many mesh records name it; a finished job's result stays, so a mesh that
    /// is unloaded and loaded again gets its levels at once. The sets to upload are left in <see cref="Finished"/>.
    /// </summary>
    public void Pump(bool enabled, Dictionary<FoliageMesh, FoliageMeshAsset>.ValueCollection meshes)
    {
        _finished.Clear();
        _lodWaiting = _lodRunning = 0;
        foreach (var t in _lodTasks.Values) if (!t.IsCompleted) _lodRunning++;
        if (!enabled) return;
        foreach (var a in meshes)
        {
            if (!a.Terrain || a.LodDone || !a.Resident || a.Main is null || a.Triangles < FoliageLodBuilder.MinTriangles)
                continue;
            string name = a.Mesh.MeshPath, key = name + MeshWeld.CacheVariant(a.Weld);
            if (!_lodTasks.TryGetValue(key, out var task))
            {
                if (_lodRunning >= 3)
                {
                    _lodWaiting++;
                    continue;
                }
                _lodRunning++;
                _lodTasks[key] = task = StartLods(name, a.Radius, a.Weld);
            }
            if (!task.IsCompleted) continue;
            a.LodDone = true;   // an unloaded mesh makes them again with its next residency
            LodResult? result = null;
            try { result = task.Result; }
            catch (AggregateException e) { messages.Add($"foliage levels {name}: {e.InnerException?.Message ?? e.Message}"); }
            if (result?.Set is { } set && a.Main is { } main && main.Parts.Count == set.Indices.Length)
                _finished.Add((a, main, set));
        }
    }

    // Apart from Pump: a lambda there made its captured variables a new object on every pass of the loop, every frame, started or not.
    /// <summary>
    /// Starts the job of one mesh file on a worker.
    /// </summary>
    Task<LodResult> StartLods(string name, float radius, bool weld) => BackgroundWork.Run(() => MakeLods(name, radius, weld));

    /// <summary>
    /// A job (on a worker): the mesh decoded, its levels read from the cache or built and saved, the counts updated.
    /// </summary>
    LodResult MakeLods(string name, float radius, bool weld)
    {
        var watch = Stopwatch.StartNew();
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        if (path is null) return new LodResult(null, false, 0, 0);
        var model = decode(name, weld);
        if (model is null) return new LodResult(null, false, 0, 0);
        int parts = model.Parts.Count(p => p.Indices.Length > 0);
        string cache = FoliageLodCache.PathFor(name, File.ReadAllBytes(path), variant: MeshWeld.CacheVariant(weld));
        var (hit, set) = FoliageLodCache.TryLoad(cache, parts);
        long bytes = 0;
        if (!hit)
        {
            set = FoliageLodBuilder.Build(model, radius);
            bytes = FoliageLodCache.Save(cache, set, parts);
        }
        double ms = watch.Elapsed.TotalMilliseconds;
        lock (this)
        {
            if (hit)
            {
                _lodCacheHits++;
                _lodLoadMs += ms;
            }
            else
            {
                _lodBuilt++;
                _lodBuildMs += ms;
                _lodFileBytes += bytes;
            }
            if (set is null) _lodNone++;
        }
        return new LodResult(set, hit, ms, bytes);
    }

    #endregion
}
