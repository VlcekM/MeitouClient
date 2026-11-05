using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Meitou.Data.Ogre;
using Meitou.Rendering;
using Meitou.Rendering.Characters;

namespace Meitou.Tests.Characters;

public sealed class RuntimeCharacterPreparationTests
{
    sealed record ReadyAsset(int Revision, byte[] Bytes);
    sealed class Jobs
    {
        readonly Queue<(Func<ReadyAsset> Work, TaskCompletionSource<ReadyAsset> Result)> queued = [];
        public int Started { get; private set; }
        public Task<ReadyAsset> Start(Func<ReadyAsset> work)
        {
            var result = new TaskCompletionSource<ReadyAsset>(TaskCreationOptions.RunContinuationsAsynchronously);
            queued.Enqueue((work, result)); Started++; return result.Task;
        }
        public void Complete()
        {
            var job = queued.Dequeue();
            try { job.Result.SetResult(job.Work()); } catch (Exception error) { job.Result.SetException(error); }
        }
    }

    [Fact]
    public void Whole_assets_wait_without_blocking_and_byte_budget_is_released_when_the_upload_consumes_them()
    {
        var jobs = new Jobs();
        using var cache = new RuntimeCharacterPreparationCache<int, ReadyAsset>(asset => asset.Bytes.Length,
            workers: 2, pendingLimit: 3, byteLimit: 100, schedule: jobs.Start);
        Assert.False(cache.Ready(1, () => new(1, new byte[40]), 40));
        Assert.False(cache.Ready(2, () => new(2, new byte[40]), 40));
        Assert.False(cache.Ready(3, () => new(3, new byte[40]), 40));
        Assert.False(cache.Ready(4, () => new(4, new byte[40]), 40));
        Assert.Equal(3, cache.Count); Assert.Equal(2, jobs.Started); Assert.Equal(80, cache.ReservedBytes);
        Assert.False(cache.TryGet(1, out _, out _)); // A render poll never runs or waits for the blocked factory.
        jobs.Complete(); cache.Pump();
        Assert.True(cache.TryGet(1, out var first, out var error)); Assert.Null(error); Assert.Equal(1, first.Revision);
        Assert.Equal(2, jobs.Started); // Completed bytes still count until consumed.
        cache.Remove(1); cache.Pump();
        Assert.Equal(3, jobs.Started); Assert.Equal(80, cache.ReservedBytes);
        jobs.Complete(); jobs.Complete(); cache.Pump();
        Assert.True(cache.TryGet(3, out var third, out error)); Assert.Equal(3, third.Revision); Assert.Null(error);
        cache.Remove(2); cache.Remove(3); Assert.Equal(0, cache.ReservedBytes); Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void One_oversized_multi_part_asset_can_finish_alone_instead_of_starving_its_last_part()
    {
        var jobs = new Jobs();
        using var cache = new RuntimeCharacterPreparationCache<int, ReadyAsset>(asset => asset.Bytes.Length,
            workers: 2, byteLimit: 100, schedule: jobs.Start);
        Assert.False(cache.Ready(1, () => new(1, new byte[180]), 180));
        Assert.False(cache.Ready(2, () => new(2, new byte[20]), 20));
        Assert.Equal(1, jobs.Started); Assert.Equal(180, cache.ReservedBytes);
        jobs.Complete(); Assert.True(cache.TryGet(1, out var first, out _)); Assert.Equal(180, first.Bytes.Length);
        Assert.Equal(1, jobs.Started);
        cache.Remove(1); cache.Pump(); Assert.Equal(2, jobs.Started);
        jobs.Complete(); cache.Pump(); Assert.True(cache.TryGet(2, out _, out _));
    }

    [Fact]
    public void An_abandoned_inflight_result_never_republishes_and_reused_source_keys_get_the_new_revision()
    {
        var jobs = new Jobs();
        using var cache = new RuntimeCharacterPreparationCache<int, ReadyAsset>(asset => asset.Bytes.Length, schedule: jobs.Start);
        cache.Ready(1, () => new(1, new byte[40]), 40);
        cache.Retain(_ => false); Assert.Equal(0, cache.Count);
        cache.Ready(1, () => new(2, new byte[20]), 20);
        jobs.Complete(); cache.Pump(); Assert.False(cache.TryGet(1, out _, out _));
        jobs.Complete(); cache.Pump();
        Assert.True(cache.TryGet(1, out var current, out _)); Assert.Equal(2, current.Revision);
        cache.Remove(1); Assert.Equal(0, cache.ReservedBytes);
    }

    [Fact]
    public void A_ready_oversized_asset_that_loses_actual_demand_releases_the_budget_for_a_new_visible_asset()
    {
        var jobs = new Jobs();
        using var cache = new RuntimeCharacterPreparationCache<int, ReadyAsset>(asset => asset.Bytes.Length,
            byteLimit: 100, schedule: jobs.Start);
        cache.Ready(1, () => new(1, new byte[180]), 180); jobs.Complete(); cache.Pump();
        Assert.True(cache.TryGet(1, out _, out _));
        cache.Ready(2, () => new(2, new byte[20]), 20); Assert.Equal(1, jobs.Started);
        // Actor1 can still exist in the world's draw-list input while the camera no longer requests its asset.
        cache.Retain(key => key == 2);
        Assert.Equal(2, jobs.Started); Assert.Equal(20, cache.ReservedBytes);
        jobs.Complete(); Assert.True(cache.TryGet(2, out var visible, out _)); Assert.Equal(2, visible.Revision);
        Assert.False(cache.TryGet(1, out _, out _));
    }

    [Fact]
    public void A_failed_preparation_is_a_completed_reportable_result_and_does_not_retry_every_frame()
    {
        var jobs = new Jobs(); int attempts = 0;
        using var cache = new RuntimeCharacterPreparationCache<int, ReadyAsset>(asset => asset.Bytes.Length, schedule: jobs.Start);
        ReadyAsset Fail() { attempts++; throw new InvalidDataException("bad generated mesh"); }
        cache.Ready(1, Fail, 100); jobs.Complete(); cache.Pump();
        Assert.True(cache.TryGet(1, out _, out var error)); Assert.IsType<InvalidDataException>(error);
        for (int i = 0; i < 10; i++) Assert.True(cache.Ready(1, Fail, 100));
        Assert.Equal(1, attempts); Assert.Equal(0, cache.ReservedBytes);
        cache.Remove(1); Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task The_default_scheduler_runs_cpu_preparation_away_from_the_calling_render_thread()
    {
        int caller = Environment.CurrentManagedThreadId;
        using var cache = new RuntimeCharacterPreparationCache<int, ReadyAsset>(asset => asset.Bytes.Length);
        cache.Ready(1, () => new(Environment.CurrentManagedThreadId, new byte[1]), 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cache.TryGet(1, out _, out _)) await Task.Delay(1, timeout.Token);
        Assert.True(cache.TryGet(1, out var prepared, out var error)); Assert.Null(error);
        Assert.NotEqual(caller, prepared.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_runtime_cpu_preparation_pre_packs_the_exact_upload_bytes_in_both_character_layouts(bool raw)
    {
        var mesh = Triangle();
        var method = typeof(CharacterContent).GetMethod("PrepareMesh", BindingFlags.Static | BindingFlags.NonPublic)!;
        var ready = method.Invoke(null, ["generated.mesh", 1, 1f, false, 1000f, null, false, false, null, mesh, true, raw])!;
        var prepared = ready.GetType().GetProperty("Mesh")!.GetValue(ready)!;
        var buffers = (byte[][])prepared.GetType().GetProperty("PackedVertices")!.GetValue(prepared)!;
        var decoded = Model.Build(mesh, 1, gameBinormals: true, preserveRawVertexChannels: raw);
        Assert.Equal(GpuVertex.PackCharacter(decoded.Parts[0].Vertices, raw), Assert.Single(buffers));
        // Source arrays stay immutable: the same input can be used by a second live presentation variant.
        var again = Model.Build(mesh, 1, gameBinormals: true, preserveRawVertexChannels: raw);
        Assert.Equal(decoded.Parts[0].Vertices, again.Parts[0].Vertices);
    }

    static OgreMesh Triangle()
    {
        Vertex[] vertices = [new() { Position = Vector3.Zero }, new() { Position = Vector3.UnitX }, new() { Position = Vector3.UnitZ }];
        for (int i = 0; i < vertices.Length; i++)
        { vertices[i].Normal = Vector3.UnitY; vertices[i].Tangent = new(12, -8, 4, 35); vertices[i].Colour = new(.25f, 6.75f, 2.5f, .8f); vertices[i].Weights = Vector4.UnitX; }
        var data = new OgreVertexData { VertexCount = 3 };
        data.Elements.AddRange([new(0, OgreVertexElementType.Float3, OgreVertexSemantic.Position, 0, 0),
            new(0, OgreVertexElementType.Float3, OgreVertexSemantic.Normal, 12, 0),
            new(0, OgreVertexElementType.Float2, OgreVertexSemantic.TextureCoordinates, 24, 0),
            new(0, OgreVertexElementType.Float4, OgreVertexSemantic.Tangent, 32, 0),
            new(0, OgreVertexElementType.Float4, OgreVertexSemantic.Diffuse, 48, 0)]);
        data.Buffers[0] = new((ushort)Vertex.Size, MemoryMarshal.AsBytes(vertices.AsSpan()).ToArray());
        var part = new OgreSubMesh { VertexData = data, Indices = new(false, [0, 1, 2]) };
        for (uint i = 0; i < 3; i++) part.BoneAssignments.Add(new(i, 0, 1));
        var mesh = new OgreMesh(); mesh.SubMeshes.Add(part); return mesh;
    }
}
