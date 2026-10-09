using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

public sealed unsafe partial class FoliageRenderer
{
    // ---- card trimming (docs/render-foliage.md, "Card trimming") ----
    // The alpha-tested cards of a mesh (leaf quads, fern fronds) are cut down to the outline of what the cut-out lets through, so the rasteriser spends no fragments on the
    // transparent corners. The mesh is trimmed when it is decoded (on the worker, with the masks read from FoliageCardMask's disk cache); the trimmed triangles follow the
    // original ones in the part's index buffer, and a draw picks one range or the other, so the Meitou switch can flip it.

    /// <summary>
    /// The Meitou <c>card-trim</c> switch: the alpha-tested cards of FOLIAGE-mode meshes and leaves are drawn cut to the opaque outline of their texture. Faithful draws the
    /// whole cards. Off for good with <c>MEITOU_CARD_TRIM=0</c>, which also leaves the trimmed index lists unmade.
    /// </summary>
    public bool CardTrim { get; set; } = FoliageCardTrimming.Enabled;

    /// <summary>What the trimming did to the meshes decoded so far (cards, triangles and areas; the sum over the parts).</summary>
    public FoliageCardTrimStats CardTrimStats { get; private set; }

    /// <summary>The first index and the index count a draw of <paramref name="part"/> uses.</summary>
    uint DrawFirst(GpuPart part) => CardTrim && part.TrimCount > 0 ? (uint)part.Count : 0;
    uint DrawCount(GpuPart part) => CardTrim && part.TrimCount > 0 ? (uint)part.TrimCount : (uint)part.Count;

    /// <summary>
    /// <see cref="Decode"/>, then the trimming when the mesh is cut out at draw time: FOLIAGE mode (or the leaves mesh) with the diffuse and the normal map both found,
    /// no tiling (the card's texture coordinates are the texture's). The mesh's bounds stay the untrimmed ones, so nothing that depends on them (size classes, impostor
    /// distances) moves.
    /// </summary>
    Model? DecodeTrimmed(string name, FoliageMesh mesh, bool leaves)
    {
        var model = Decode(name);
        if (model is null || !FoliageCardTrimming.Enabled) return model;
        bool cutOut = leaves || mesh.MaterialType == 4;
        float threshold = (leaves ? mesh.LeavesAlphaThreshold : mesh.AlphaThreshold) / 255f;
        var diffuse = leaves ? mesh.LeavesTexture : mesh.Texture;
        var normal = leaves ? mesh.LeavesNormal : mesh.Normal;
        if (!cutOut || !(threshold > 0) || (!leaves && (mesh.TileX != 1 || mesh.TileY != 1))) return model;
        string? Find(string? n) => n is null ? null : assets.Find(Path.GetFileName(n.Replace('\\', '/'))) ?? assets.Find(n);
        if (Find(diffuse) is null || Find(normal) is not { } normalPath) return model;
        var stats = FoliageCardTrimming.Apply(model, normalPath, threshold);
        lock (Messages) CardTrimStats += stats;
        return model;
    }
}
