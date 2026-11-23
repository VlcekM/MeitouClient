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
    /// The colour sets made so far, by march (3 apart), relief quality, trails (6 apart), the simple material (12 apart) and the lean
    /// lighting (24 apart, <see cref="Frame.LeanLighting"/>).
    /// </summary>
    readonly ColourSet?[] _colourSets = new ColourSet?[48];

    /// <summary>
    /// The resolved colour's programs (TerrainRenderer.Resolve) by parallax march, relief quality, trails, the simple material, the lean
    /// lighting and normal maps.
    /// </summary>
    readonly Dictionary<(bool March, int Quality, bool Trails, bool Simple, bool Lean, bool Normal, TerrainShaders.ResolvePart Part), TerrainProgram>
        _resolvePrograms = [];

    /// <summary>
    /// Makes the depth and prepass programs, the colour set for the current relief, and the tessellated variants where the device has tessellation.
    /// </summary>
    public TerrainPrograms(IGpuContext gpu, GpuSetLayout[] sets)
    {
        _gpu = gpu;
        _sets = sets;
        UseColour(GroundRelief.ParallaxActive, GroundRelief.Quality, simple: TerrainSimpleShading.Enabled);
        PatchDepth = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.DepthFragmentNative(), "terrain depth");
        PatchPrepass = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.PrepassFragmentNative(), "terrain prepass");
        PatchPrepassCut = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(),
            TerrainShaders.PrepassFragmentNative(interiorCut: true), "terrain prepass interior cut");
        PatchPrepassResolve = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(), TerrainShaders.PrepassResolveFragmentNative(),
            "terrain resolve prepass");
        PatchPrepassResolveCut = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexNative(),
            TerrainShaders.PrepassResolveFragmentNative(interiorCut: true), "terrain resolve prepass interior cut");

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
            PatchPrepassResolveTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(),
                TerrainShaders.PrepassResolveFragmentNative(), "terrain relief resolve prepass", TerrainShaders.PatchControlNative(),
                TerrainShaders.PatchEvaluationNative());
            PatchPrepassResolveCutTess = new TerrainProgram(gpu, sets, TerrainShaders.PatchVertexTessNative(),
                TerrainShaders.PrepassResolveFragmentNative(interiorCut: true), "terrain relief resolve prepass interior cut",
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
    /// The prepass of the resolved colour (TerrainRenderer.Resolve): the prepass's stages with a fragment stage that writes the resolve's inputs.
    /// </summary>
    public TerrainProgram PatchPrepassResolve { get; }

    /// <summary>
    /// <see cref="PatchPrepassResolve"/> with the interior cut.
    /// </summary>
    public TerrainProgram PatchPrepassResolveCut { get; }

    /// <summary>
    /// <see cref="PatchPrepassResolve"/> tessellated (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? PatchPrepassResolveTess { get; }

    /// <summary>
    /// <see cref="PatchPrepassResolveCut"/> tessellated (null without tessellation shaders).
    /// </summary>
    public TerrainProgram? PatchPrepassResolveCutTess { get; }

    /// <summary>
    /// The parallax march, relief quality, trails, simple material and lean lighting of the colour set in use (<see cref="UseColour"/>).
    /// </summary>
    public (bool March, int Quality, bool Trails, bool Simple, bool Lean) ColourKey { get; private set; }

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
        /// The trails' set: its patch programs are its own, its mesh programs the plain set's (<see cref="MakeTrailed"/>).
        /// </summary>
        public bool SharesMeshes { get; init; }

        /// <summary>
        /// Frees the set's programs (not the meshes' of a trails set, which the plain set holds).
        /// </summary>
        public void Dispose()
        {
            foreach (var p in new[] { Patch, PatchNoNormal, PatchCut, PatchCutNoNormal, PatchTess, PatchCutTess }) p?.Dispose();
            if (SharesMeshes) return;

            foreach (var p in new[] { Mesh, MeshNoNormal, MeshSolid, MeshSolidNoNormal, MeshTess, MeshSolidTess }) p?.Dispose();
        }
    }

    /// <summary>
    /// Draws with the colour set of <paramref name="march"/>, the relief <paramref name="quality"/>, <paramref name="simple"/> and
    /// <paramref name="lean"/> (compiled in), made the first time it is asked for (at load the settings'; a change in play makes the other once, and
    /// keeps it: switching back costs nothing).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(PatchColour), nameof(PatchColourNoNormal), nameof(PatchColourCut),
        nameof(PatchColourCutNoNormal), nameof(MeshColour), nameof(MeshColourNoNormal), nameof(MeshSolid), nameof(MeshSolidNoNormal))]
    public void UseColour(bool march, int quality, bool trails = false, bool simple = false, bool lean = false)
    {
        quality = Math.Clamp(quality, 0, 2);
        ColourKey = (march, quality, trails, simple, lean);
        var set = trails
            ? _colourSets[SetIndex(march, quality, simple, lean) + 6] ??= MakeTrailed(march, quality, simple, lean)
            : Plain(march, quality, simple, lean);
        (PatchColour, PatchColourNoNormal, PatchColourCut,
            PatchColourCutNoNormal) = (set.Patch, set.PatchNoNormal, set.PatchCut, set.PatchCutNoNormal);
        (MeshColour, MeshColourNoNormal, MeshSolid, MeshSolidNoNormal) = (set.Mesh, set.MeshNoNormal, set.MeshSolid, set.MeshSolidNoNormal);
        (PatchColourTess, PatchColourCutTess, MeshColourTess, MeshSolidTess) = (set.PatchTess, set.PatchCutTess, set.MeshTess, set.MeshSolidTess);
    }

    /// <summary>
    /// The colour set of <paramref name="march"/>, <paramref name="quality"/>, <paramref name="simple"/> and <paramref name="lean"/>, compiled now.
    /// The simple material
    /// changes only the blended patches; the map features' meshes shade their one biome in full either way.
    /// </summary>
    ColourSet MakeColour(bool march, int quality, bool simple, bool lean)
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
        string Fragment(bool cut, bool normal) =>
            TerrainShaders.FragmentNative(cut, march, normal ? null : false, quality, simple: simple, lean: lean);
        string MeshFragment(bool normal) => TerrainShaders.MeshFragmentNative(march, normal ? null : false, quality, lean);
        string MeshSolid(bool normal) => TerrainShaders.MeshFragmentSolidNative(march, normal ? null : false, quality, lean);
        return new ColourSet(march,
            Patch(Fragment(false, true), "terrain"),
            Patch(Fragment(false, false), "terrain no normal"),
            Patch(Fragment(true, true), "terrain interior cut"),
            Patch(Fragment(true, false), "terrain interior cut no normal"),
            Mesh(MeshFragment(true), "terrain meshes"),
            Mesh(MeshFragment(false), "terrain meshes no normal"),
            Mesh(MeshSolid(true), "terrain meshes solid"),
            Mesh(MeshSolid(false), "terrain meshes solid no normal"),
            PatchTess(Fragment(false, true), "terrain relief"),
            PatchTess(Fragment(true, true), "terrain relief interior cut"),
            MeshTess(MeshFragment(true), "terrain meshes smoothed"),
            MeshTess(MeshSolid(true), "terrain meshes solid smoothed"));
    }

    /// <summary>
    /// The resolve's program for the colour set in use (<see cref="ColourKey"/>) and <paramref name="normalMaps"/>, made the first time.
    /// </summary>
    public TerrainProgram ResolveProgram(bool normalMaps, TerrainShaders.ResolvePart part = TerrainShaders.ResolvePart.Whole)
    {
        var key = (ColourKey.March, ColourKey.Quality, ColourKey.Trails, ColourKey.Simple, ColourKey.Lean, normalMaps, part);
        if (_resolvePrograms.TryGetValue(key, out var p)) return p;

        bool? normal = normalMaps ? null : false;
        var fragment = TerrainShaders.ResolveFragmentNative(key.March, normal, key.Quality, key.Trails, part, key.Simple, key.Lean);
        string name = part switch
        {
            TerrainShaders.ResolvePart.Near => "terrain resolve near",
            TerrainShaders.ResolvePart.Far => "terrain resolve far",
            _ => "terrain resolve",
        };
        return _resolvePrograms[key] = new TerrainProgram(_gpu, _sets, TerrainShaders.ResolveVertexNative(), fragment, name);
    }

    /// <summary>
    /// The plain colour set of <paramref name="march"/>, <paramref name="quality"/>, <paramref name="simple"/> and <paramref name="lean"/>, made
    /// the first time.
    /// </summary>
    ColourSet Plain(bool march, int quality, bool simple, bool lean) =>
        _colourSets[SetIndex(march, quality, simple, lean)] ??= MakeColour(march, quality, simple, lean);

    /// <summary>
    /// The plain set's index in <see cref="_colourSets"/> (a trails set is 6 further).
    /// </summary>
    static int SetIndex(bool march, int quality, bool simple, bool lean) => (lean ? 24 : 0) + (simple ? 12 : 0) + (march ? 3 : 0) + quality;

    /// <summary>
    /// The plain set with its patch programs built with the ground trails; the map features' meshes take no trails.
    /// </summary>
    ColourSet MakeTrailed(bool march, int quality, bool simple, bool lean)
    {
        string Fragment(bool cut, bool normal) => TerrainShaders.FragmentTrails(cut, march, normal, quality, simple, lean);
        return Plain(march, quality, simple, lean) with
        {
            Patch = TrailedPatch(Fragment(false, true), "terrain trails"),
            PatchNoNormal = TrailedPatch(Fragment(false, false), "terrain trails no normal"),
            PatchCut = TrailedPatch(Fragment(true, true), "terrain trails interior cut"),
            PatchCutNoNormal = TrailedPatch(Fragment(true, false), "terrain trails interior cut no normal"),
            PatchTess = TrailedPatchTess(Fragment(false, true), "terrain trails relief"),
            PatchCutTess = TrailedPatchTess(Fragment(true, true), "terrain trails relief interior cut"),
            SharesMeshes = true,
        };
    }

    /// <summary>
    /// A trails set's patch program of <paramref name="fragment"/>.
    /// </summary>
    TerrainProgram TrailedPatch(string fragment, string name) => new(_gpu, _sets, TerrainShaders.PatchVertexNative(), fragment, name);

    /// <summary>
    /// A trails set's tessellated patch program of <paramref name="fragment"/>; null without tessellation.
    /// </summary>
    TerrainProgram? TrailedPatchTess(string fragment, string name) => _gpu.HasTessellation
        ? new(_gpu, _sets, TerrainShaders.PatchVertexTessNative(), fragment, name, TerrainShaders.PatchControlNative(),
            TerrainShaders.PatchEvaluationNative())
        : null;

    /// <summary>
    /// Frees every program and colour set.
    /// </summary>
    public void Dispose()
    {
        foreach (var p in new[] { PatchDepth, PatchPrepass, PatchPrepassCut, MeshDepth, PatchPrepassResolve, PatchPrepassResolveCut }) p.Dispose();
        PatchPrepassResolveTess?.Dispose();
        PatchPrepassResolveCutTess?.Dispose();
        foreach (var p in _resolvePrograms.Values) p.Dispose();
        foreach (var set in _colourSets) set?.Dispose();
        PatchPrepassTess?.Dispose();
        PatchPrepassCutTess?.Dispose();
        MeshDepthTess?.Dispose();
    }
}
