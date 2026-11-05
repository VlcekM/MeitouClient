using Meitou.Data.Characters;
using Meitou.Data.Ogre;

namespace Meitou.Rendering.Characters;

internal sealed partial class CharacterContent
{
    readonly record struct RuntimeAssetKey(CharacterAppearance Appearance, RuntimeCharacterGeometry Geometry, bool Close);
    readonly record struct RuntimeMeshKey(OgreMesh Mesh, int Bones, string? PartMap, bool VertexColours, LimbHide? Limbs, bool Close, bool RawChannels);
    sealed record RuntimeInput(RuntimeMeshKey Key, string Path, float Scale, string? PartMap);
    sealed record RuntimeFile(Lazy<Prepared> Prepared);
    sealed record RuntimePrepared(Dictionary<RuntimeMeshKey, Prepared> Meshes)
    {
        internal long Bytes
        {
            get
            {
                long bytes = 0;
                foreach (var prepared in Meshes.Values)
                {
                    if (prepared.Mesh is not { } mesh) continue;
                    foreach (var part in mesh.Parts) bytes += (long)part.Part.Vertices.Length * Vertex.Size + (long)part.All.Length * sizeof(uint);
                    if (mesh.PackedVertices is { } packed) foreach (var vertices in packed) bytes += vertices.Length;
                    if (mesh.Morphs is { } morphs) foreach (var pose in morphs.Poses.Values) bytes += (long)pose.Slots.Length * sizeof(int) + (long)pose.Offsets.Length * 12;
                }
                return bytes;
            }
        }
    }
    readonly RuntimeCharacterPreparationCache<RuntimeAssetKey, RuntimePrepared> runtimePreparing = new(prepared => prepared.Bytes);
    RuntimePrepared? buildingRuntime;
    readonly HashSet<RuntimeAssetKey> runtimeDemand = [];
    internal bool HasRuntimePreparations => runtimePreparing.Count > 0 || runtimePreparing.Running > 0;
    internal void BeginRuntimePreparationFrame() => runtimeDemand.Clear();
    internal void KeepRuntimePreparation(CharacterAppearance appearance, RuntimeCharacterGeometry geometry) =>
        runtimeDemand.Add(new(appearance, geometry, closeUp.Contains(appearance)));
    internal void SweepRuntimePreparationDemand() => runtimePreparing.Retain(runtimeDemand.Contains);
    internal void SweepRuntimePreparations(ISet<RuntimeCharacterGeometry> live) =>
        runtimePreparing.Retain(key => live.Contains(key.Geometry));

    /// <summary>Interactive readiness includes every supplied CPU mesh, not just the appearance's native files.
    /// Missing/failed meshes are resolved outcomes, reported by Build as for ordinary files; no partial asset is uploaded while another part waits.</summary>
    public bool Fetched(CharacterAppearance appearance, RuntimeCharacterGeometry? geometry)
    {
        if (geometry is not null) KeepRuntimePreparation(appearance, geometry);
        if (!Fetched(appearance)) return false;
        if (geometry is null) return true;
        bool close = closeUp.Contains(appearance);
        var assetKey = new RuntimeAssetKey(appearance, geometry, close);
        if (runtimePreparing.TryGet(assetKey, out _, out _)) return true;
        var path = FindMesh(appearance.BodyMesh);
        if (path is null) return true; // Build reports the same missing body/skeleton as the ordinary path.
        string? skeleton;
        try { skeleton = BodySkeletonName(path); }
        catch (Exception error) when (error is Meitou.Data.Ogre.OgreFormatException or IOException or EndOfStreamException or InvalidDataException) { return true; }
        if (skeleton is null || Rigs.Rig(skeleton, appearance) is not { } rig) return true;
        var inputs = new Dictionary<RuntimeMeshKey, RuntimeInput>();
        var files = new List<RuntimeFile>();
        if (geometry.BodyMesh is { } body)
            Add(body, path, rig.BoneCount, 1, false, LimbHideOf(appearance, rig), PartMapPath(appearance), geometry.PreserveRawBodyVertexChannels);
        if (geometry.Parts is { } parts)
            foreach (var supplied in parts)
            {
                var part = supplied.Part;
                int? bones = part.Mode == AttachMode.SharedSkeleton ? rig.BoneCount : null;
                float scale = PartLodScale(appearance, part);
                if (supplied.Mesh is { } mesh)
                    Add(mesh, part.Mesh, bones, scale, supplied.VertexColours, null, null, supplied.PreserveRawVertexChannels);
                else if (FindMesh(part.Mesh) is { } file)
                {
                    var prepared = Prepare(file, bones, scale, false, LodPixelsPerRadian, close: close);
                    if (!prepared.IsValueCreated) files.Add(new(prepared));
                }
            }
        if (inputs.Count == 0 && files.Count == 0) return true;
        var captured = inputs.Values.ToArray(); var extraFiles = files.ToArray(); float pixels = LodPixelsPerRadian;
        long estimate = 0;
        foreach (var input in captured) estimate += Estimate(input.Key.Mesh);
        return runtimePreparing.Ready(assetKey, () =>
        {
            foreach (var file in extraFiles) _ = file.Prepared.Value;
            var prepared = new Dictionary<RuntimeMeshKey, Prepared>();
            foreach (var input in captured)
            {
                var key = input.Key;
                prepared.Add(key, PrepareMesh(input.Path, key.Bones < 0 ? null : key.Bones, input.Scale, false, pixels,
                    key.Limbs, key.Close, partMap: input.PartMap, runtime: key.Mesh, vertexColours: key.VertexColours, rawChannels: key.RawChannels));
            }
            return new RuntimePrepared(prepared);
        }, Math.Max(1, estimate));

        void Add(OgreMesh mesh, string label, int? bones, float scale, bool colours, LimbHide? limbs, string? partMap, bool raw)
        {
            var key = new RuntimeMeshKey(mesh, bones ?? -1, partMap?.ToLowerInvariant(), colours, limbs, close, raw);
            var uploadKey = (mesh, bones ?? -1, key.PartMap, colours, limbs, close, raw);
            if (!runtimeMeshes.ContainsKey(uploadKey)) inputs.TryAdd(key, new(key, label, scale, partMap));
        }
    }

    static long Estimate(OgreMesh mesh)
    {
        long bytes = 0;
        foreach (var part in mesh.SubMeshes)
        {
            var vertices = part.UseSharedVertices ? mesh.SharedVertexData : part.VertexData;
            bytes += (long)(vertices?.VertexCount ?? 0) * 384 + (long)part.Indices.Indices.Length * 12;
        }
        foreach (var pose in mesh.Poses) bytes += (long)pose.Vertices.Count * 32;
        return Math.Max(1, bytes);
    }

    /// <summary>Direct/still builds (LoadBudget0) may intentionally prepare synchronously. Interactive AssetFor always preflights first.</summary>
    public CharacterAsset? Build(CharacterAppearance appearance, RuntimeCharacterGeometry? geometry = null)
    {
        var key = geometry is null ? (RuntimeAssetKey?)null : new RuntimeAssetKey(appearance, geometry, closeUp.Contains(appearance));
        var previous = buildingRuntime;
        try
        {
            if (key is { } request && runtimePreparing.TryGet(request, out var prepared, out var error))
            {
                if (error is not null) { Messages.Add($"runtime character mesh: {error.Message}"); return null; }
                buildingRuntime = prepared;
            }
            else buildingRuntime = null;
            return BuildCore(appearance, geometry);
        }
        finally
        {
            buildingRuntime = previous;
            if (key is { } request) runtimePreparing.Remove(request); // GPU uploads now own the live buffers; discard CPU vertices/packing immediately.
        }
    }
}
