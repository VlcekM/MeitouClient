using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The TERRAIN-mode meshes' placements sorted into instanced groups (<see cref="Group"/>), and each mesh's group kept with its native state across
/// calls (<see cref="For"/>).
/// </summary>
sealed unsafe class TerrainMeshGroups
{
    /// <summary>
    /// The groups at most kept between calls: past it the table is cleared (meshes come and go with streaming).
    /// </summary>
    const int MaxKept = 4096;

    /// <summary>
    /// The context whose frame constants take the placements.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// Every group by its key, kept between calls.
    /// </summary>
    readonly Dictionary<(MeshBindings, int, bool), MeshGroup> _byKey = [];

    /// <summary>
    /// Per placement of the call, its group's index in <see cref="Groups"/>; per group, the next placement's slot.
    /// </summary>
    int[] _groupOf = new int[1024], _cursors = new int[64];

    /// <summary>
    /// An empty table on <paramref name="gpu"/>.
    /// </summary>
    public TerrainMeshGroups(IGpuContext gpu) => _gpu = gpu;

    /// <summary>
    /// This call's groups, in the order of their first placement.
    /// </summary>
    public List<MeshGroup> Groups { get; } = [];

    /// <summary>
    /// This call's placements (each group's contiguous, <see cref="MeshGroup.Offset"/> instances in), in the frame's constants.
    /// </summary>
    public Transient Placements { get; private set; }

    /// <summary>
    /// Sorts the placements into <see cref="Groups"/> and writes them into this frame's constants (<see cref="Placements"/>; each one's matrix as its
    /// four rows; with <paramref name="biomes"/> row 0's w, which only feeds the position's unused w, carries the biome row from
    /// <paramref name="textures"/>). False when there is none.
    /// </summary>
    public bool Group(List<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> meshes, bool biomes, TerrainTextures? textures)
    {
        foreach (var g in Groups) g.Count = 0;
        Groups.Clear();
        Trim();
        var items = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(meshes);
        if (_groupOf.Length < items.Length) _groupOf = new int[Math.Max(items.Length, _groupOf.Length * 2)];
        AssignGroups(items);
        if (Groups.Count == 0) return false;

        int total = LayOutGroups();
        WritePlacements(items, total, biomes, textures);
        return true;
    }

    /// <summary>
    /// The group of (<paramref name="mesh"/>, <paramref name="indexCount"/>, <paramref name="mirrored"/>), made the first time.
    /// </summary>
    public MeshGroup For(MeshBindings mesh, int indexCount, bool mirrored)
    {
        var key = (mesh, indexCount, mirrored);
        if (!_byKey.TryGetValue(key, out var g))
            _byKey[key] = g = new MeshGroup { Mesh = mesh, IndexCount = indexCount, Mirrored = mirrored };
        return g;
    }

    /// <summary>
    /// Forgets every group once more than <see cref="MaxKept"/> are kept.
    /// </summary>
    public void Trim()
    {
        if (_byKey.Count > MaxKept) _byKey.Clear();   // meshes come and go with streaming
    }

    /// <summary>
    /// Pass 1: each placement's group (the callers list a mesh's placements in runs, so the last group usually answers without a lookup).
    /// </summary>
    void AssignGroups(ReadOnlySpan<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> items)
    {
        MeshGroup? last = null;
        for (int i = 0; i < items.Length; i++)
        {
            ref readonly var item = ref items[i];
            bool mirrored = item.Model.GetDeterminant() < 0;
            var g = last;
            if (g is null || g.Mesh != item.Mesh || g.IndexCount != item.IndexCount || g.Mirrored != mirrored)
            {
                var key = (item.Mesh, item.IndexCount, mirrored);
                if (!_byKey.TryGetValue(key, out g))
                    _byKey[key] = g = new MeshGroup { Mesh = item.Mesh, IndexCount = item.IndexCount, Mirrored = mirrored };
                if (g.Count == 0)
                {
                    g.Index = Groups.Count;
                    Groups.Add(g);
                }
                last = g;
            }
            g.Count++;
            _groupOf[i] = g.Index;
        }
    }

    /// <summary>
    /// Each group's first slot (<see cref="MeshGroup.Offset"/>, the cursors' start); returns the placements in all.
    /// </summary>
    int LayOutGroups()
    {
        int total = 0;
        if (_cursors.Length < Groups.Count)
            _cursors = new int[Math.Max(Groups.Count, _cursors.Length * 2)];
        for (int k = 0; k < Groups.Count; k++)
        {
            var g = Groups[k];
            _cursors[k] = g.Offset = total;
            total += g.Count;
        }
        return total;
    }

    /// <summary>
    /// Pass 2: each placement straight into its group's place in the frame's (write-combined) constants, one 64-byte line each.
    /// </summary>
    void WritePlacements(ReadOnlySpan<(MeshBindings Mesh, int IndexCount, Matrix4x4 Model)> items, int total, bool biomes, TerrainTextures? textures)
    {
        Placements = _gpu.Frame.AllocateConstants((ulong)(total * 64), 64);
        var target = (Matrix4x4*)Placements.Pointer;
        var t = biomes ? textures : null;
        for (int i = 0; i < items.Length; i++)
        {
            var placed = items[i].Model;
            // Not resident yet (or no textures): -1, blend the biomes as the terrain does.
            if (biomes) placed.M14 = t?.FeatureBiomeRow(placed.M41, placed.M43) ?? -1;
            target[_cursors[_groupOf[i]]++] = placed;
        }
    }
}

/// <summary>
/// The placements of one (mesh, index count, winding) drawn as one instanced draw, and the mesh's native state per program.
/// </summary>
sealed class MeshGroup
{
    /// <summary>
    /// The mesh's vertex array.
    /// </summary>
    public MeshBindings Mesh = null!;

    /// <summary>
    /// The mesh's index count; the placements in this call; the first of them in <see cref="TerrainMeshGroups.Placements"/>; the group's place in
    /// <see cref="TerrainMeshGroups.Groups"/>.
    /// </summary>
    public int IndexCount, Count, Offset, Index;

    /// <summary>
    /// The placements mirror (the winding turns round).
    /// </summary>
    public bool Mirrored;

    /// <summary>
    /// What a native draw of this mesh needs, per program. Inline (no object of its own), so a draw reads what grouping has just touched.
    /// </summary>
    public TerrainRenderer.NativeMesh ColourNative, DepthNative, SolidNative;

    /// <summary>
    /// The native state for program kind <paramref name="kind"/> (<see cref="TerrainRenderer.Colour"/>, <see cref="TerrainRenderer.Depth"/>,
    /// <see cref="TerrainRenderer.Solid"/>).
    /// </summary>
    public ref TerrainRenderer.NativeMesh Native(int kind) => ref kind == TerrainRenderer.Colour
        ? ref ColourNative
        : ref (kind == TerrainRenderer.Depth ? ref DepthNative : ref SolidNative);
}
