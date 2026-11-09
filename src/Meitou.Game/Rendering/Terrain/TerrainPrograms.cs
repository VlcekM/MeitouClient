using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// The terrain's native programs (docs/renderer-native.md 7.1, step O), all made at load so a draw never compiles: the depth and prepass programs,
/// and the colour set the parallax setting asks for (<see cref="UseColour"/>).
/// </summary>
sealed class TerrainPrograms : IDisposable
{
    /// <summary>
    /// The context the programs are compiled on.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The sets the programs bind: the frame set, the bindless arrays, the terrain set.
    /// </summary>
    readonly GpuSetLayout[] _sets;

    /// <summary>
    /// The colour sets made so far, by march (3 apart) and relief quality.
    /// </summary>
    readonly ColourSet?[] _colourSets = new ColourSet?[6];

    /// <summary>
    /// Makes the depth and prepass programs, the colour set for the current relief, and the tessellated variants where the device has tessellation.
    /// </summary>
    public TerrainPrograms(IGpuContext gpu, GpuSetLayout[] sets)
    {
        _gpu = gpu;
        _sets = sets;
        UseColour(GroundRelief.ParallaxActive, GroundRelief.Quality);
        PatchDepth = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.DepthFragmentNative(), "terrain depth");
        PatchPrepass = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.PrepassFragmentNative(), "terrain prepass");
        PatchPrepassCut = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(),
            TerrainShaders.PrepassFragmentNative(interiorCut: true), "terrain prepass interior cut");
        // The ground displacement (GroundRelief): the colour and prepass programs tessellated, for the patches near the eye (the same stages in both,
        // so the late colour's depth test meets the prepass's depths).
        if (gpu.HasTessellation)
        {
            PatchPrepassTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(), TerrainShaders.PrepassFragmentNative(),
                "terrain relief prepass",
                TerrainShaders.PatchControlNative(), TerrainShaders.PatchEvaluationNative());
            PatchPrepassCutTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(),
                TerrainShaders.PrepassFragmentNative(interiorCut: true), "terrain relief prepass interior cut",
                TerrainShaders.PatchControlNative(), TerrainShaders.PatchEvaluationNative());
        }
        MeshDepth = new TerrainProgram(gpu, sets, TerrainShaders.MeshInstancedDepthVertexNative(), TerrainShaders.DepthFragmentNative(),
            "terrain mesh depth");
        // The rocks' smoothing (RockSmoothing): the mesh programs tessellated, the casters with the same curve.
        if (gpu.HasTessellation)
        {
            MeshDepthTess = new TerrainProgram(gpu, sets, TerrainShaders.MeshVertexTessNative(), TerrainShaders.DepthFragmentNative(),
                "terrain mesh depth smoothed",
                TerrainShaders.MeshControlNative(), TerrainShaders.MeshEvaluationDepthNative());
        }
    }

    /// <summary>
    /// The shadow casters' patches.
    /// </summary>
    public TerrainProgram PatchDepth { get; }

    /// <summary>
    /// The deferred colour's depth prepass of the patches.
    /// </summary>
    public TerrainProgram PatchPrepass { get; }

    /// <summary>
    /// The prepass of the patches overlapping an interior cut.
    /// </summary>
    public TerrainProgram PatchPrepassCut { get; }

    /// <summary>
    /// The shadow casters' TERRAIN-mode meshes.
    /// </summary>
    public TerrainProgram MeshDepth { get; }

    /// <summary>
    /// <see cref="PatchPrepass"/> tessellated (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? PatchPrepassTess { get; }

    /// <summary>
    /// <see cref="PatchPrepassCut"/> tessellated (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? PatchPrepassCutTess { get; }

    /// <summary>
    /// <see cref="MeshDepth"/> smoothed (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? MeshDepthTess { get; }

    // The colour programs, from the set the parallax setting asks for (UseColour): meshSolid is meshColour without the cross-fade discard
    // (EarlyDepth).

    /// <summary>
    /// The patches' colour.
    /// </summary>
    public TerrainProgram PatchColour { get; private set; }

    /// <summary>
    /// <see cref="PatchColour"/> without the normal-map samples.
    /// </summary>
    public TerrainProgram PatchColourNoNormal { get; private set; }

    /// <summary>
    /// The colour of the patches overlapping an interior cut.
    /// </summary>
    public TerrainProgram PatchColourCut { get; private set; }

    /// <summary>
    /// <see cref="PatchColourCut"/> without the normal-map samples.
    /// </summary>
    public TerrainProgram PatchColourCutNoNormal { get; private set; }

    /// <summary>
    /// The TERRAIN-mode meshes' colour.
    /// </summary>
    public TerrainProgram MeshColour { get; private set; }

    /// <summary>
    /// <see cref="MeshColour"/> without the normal-map samples.
    /// </summary>
    public TerrainProgram MeshColourNoNormal { get; private set; }

    /// <summary>
    /// <see cref="MeshColour"/> without the cross-fade discard (EarlyDepth).
    /// </summary>
    public TerrainProgram MeshSolid { get; private set; }

    /// <summary>
    /// <see cref="MeshSolid"/> without the normal-map samples.
    /// </summary>
    public TerrainProgram MeshSolidNoNormal { get; private set; }

    /// <summary>
    /// <see cref="PatchColour"/> tessellated (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? PatchColourTess { get; private set; }

    /// <summary>
    /// <see cref="PatchColourCut"/> tessellated (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? PatchColourCutTess { get; private set; }

    /// <summary>
    /// <see cref="MeshColour"/> smoothed (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? MeshColourTess { get; private set; }

    /// <summary>
    /// <see cref="MeshSolid"/> smoothed (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? MeshSolidTess { get; private set; }

    /// <summary>
    /// The colour programs, with the parallax's march (<see cref="GroundRelief.ParallaxActive"/>) or without it
    /// (<see cref="TerrainShaders.FragmentNative"/>), and with the normal-map samples or without them (the no-normal variants are compile-time
    /// programs for Metal's shader compiler).
    /// </summary>
    sealed record ColourSet(bool March, TerrainProgram Patch, TerrainProgram PatchNoNormal, TerrainProgram PatchCut, TerrainProgram PatchCutNoNormal,
        TerrainProgram Mesh, TerrainProgram MeshNoNormal, TerrainProgram MeshSolid, TerrainProgram MeshSolidNoNormal,
        TerrainProgram? PatchTess, TerrainProgram? PatchCutTess, TerrainProgram? MeshTess, TerrainProgram? MeshSolidTess) : IDisposable
    {
        /// <summary>
        /// Frees every program of the set.
        /// </summary>
        public void Dispose()
        {
            foreach (var p in new[] { Patch, PatchNoNormal, PatchCut, PatchCutNoNormal, Mesh, MeshNoNormal, MeshSolid, MeshSolidNoNormal,
                         PatchTess, PatchCutTess, MeshTess, MeshSolidTess }) p?.Dispose();
        }
    }

    /// <summary>
    /// Draws with the colour set of <paramref name="march"/> and the relief <paramref name="quality"/> (compiled in), made the first time it is asked
    /// for (at load the settings'; a change in play makes the other once, and keeps it: switching back costs nothing).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(PatchColour), nameof(PatchColourNoNormal), nameof(PatchColourCut),
        nameof(PatchColourCutNoNormal), nameof(MeshColour), nameof(MeshColourNoNormal), nameof(MeshSolid), nameof(MeshSolidNoNormal))]
    public void UseColour(bool march, int quality)
    {
        quality = Math.Clamp(quality, 0, 2);
        var set = _colourSets[(march ? 3 : 0) + quality] ??= MakeColour(march, quality);
        (PatchColour, PatchColourNoNormal, PatchColourCut,
            PatchColourCutNoNormal) = (set.Patch, set.PatchNoNormal, set.PatchCut, set.PatchCutNoNormal);
        (MeshColour, MeshColourNoNormal, MeshSolid, MeshSolidNoNormal) = (set.Mesh, set.MeshNoNormal, set.MeshSolid, set.MeshSolidNoNormal);
        (PatchColourTess, PatchColourCutTess, MeshColourTess, MeshSolidTess) = (set.PatchTess, set.PatchCutTess, set.MeshTess, set.MeshSolidTess);
    }

    /// <summary>
    /// The colour set of <paramref name="march"/> and <paramref name="quality"/>, compiled now.
    /// </summary>
    ColourSet MakeColour(bool march, int quality)
    {
        bool tess = _gpu.HasTessellation;
        TerrainProgram Patch(string fragment, string name) => new(_gpu, _sets, TerrainShaders.PatchVertexNative(), fragment, name);
        TerrainProgram? PatchTess(string fragment, string name) => tess
            ? new(_gpu, _sets, TerrainShaders.PatchVertexTessNative(), fragment, name, TerrainShaders.PatchControlNative(),
                TerrainShaders.PatchEvaluationNative())
            : null;
        TerrainProgram Mesh(string fragment, string name) => new(_gpu, _sets, TerrainShaders.MeshVertexNative(), fragment, name);
        TerrainProgram? MeshTess(string fragment, string name) => tess
            ? new(_gpu, _sets, TerrainShaders.MeshVertexTessNative(), fragment, name, TerrainShaders.MeshControlNative(),
                TerrainShaders.MeshEvaluationNative())
            : null;
        return new ColourSet(march,
            Patch(TerrainShaders.FragmentNative(march: march, reliefQuality: quality), "terrain"),
            Patch(TerrainShaders.FragmentNative(march: march, normalMaps: false, reliefQuality: quality), "terrain no normal"),
            Patch(TerrainShaders.FragmentNative(interiorCut: true, march: march, reliefQuality: quality), "terrain interior cut"),
            Patch(TerrainShaders.FragmentNative(interiorCut: true, march: march, normalMaps: false, reliefQuality: quality),
                    "terrain interior cut no normal"),
            Mesh(TerrainShaders.MeshFragmentNative(march, reliefQuality: quality), "terrain meshes"),
            Mesh(TerrainShaders.MeshFragmentNative(march, normalMaps: false, reliefQuality: quality), "terrain meshes no normal"),
            Mesh(TerrainShaders.MeshFragmentSolidNative(march, reliefQuality: quality), "terrain meshes solid"),
            Mesh(TerrainShaders.MeshFragmentSolidNative(march, normalMaps: false, reliefQuality: quality), "terrain meshes solid no normal"),
            PatchTess(TerrainShaders.FragmentNative(march: march, reliefQuality: quality), "terrain relief"),
            PatchTess(TerrainShaders.FragmentNative(interiorCut: true, march: march, reliefQuality: quality), "terrain relief interior cut"),
            MeshTess(TerrainShaders.MeshFragmentNative(march, reliefQuality: quality), "terrain meshes smoothed"),
            MeshTess(TerrainShaders.MeshFragmentSolidNative(march, reliefQuality: quality), "terrain meshes solid smoothed"));
    }

    /// <summary>
    /// Frees every program and colour set.
    /// </summary>
    public void Dispose()
    {
        foreach (var p in new[] { PatchDepth, PatchPrepass, PatchPrepassCut, MeshDepth }) p.Dispose();
        foreach (var set in _colourSets) set?.Dispose();
        PatchPrepassTess?.Dispose();
        PatchPrepassCutTess?.Dispose();
        MeshDepthTess?.Dispose();
    }
}
