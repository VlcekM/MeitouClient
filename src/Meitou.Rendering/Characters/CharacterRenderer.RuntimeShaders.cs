using Meitou.Data.Characters;

namespace Meitou.Rendering.Characters;

internal sealed unsafe partial class CharacterRenderer
{
    sealed class RuntimePrograms(ReflectedProgram colour, ReflectedProgram depth, ReflectedProgram motion) : IDisposable
    {
        public readonly ReflectedProgram Colour = colour, Depth = depth, Motion = motion;
        public IEnumerable<ReflectedProgram> All => [Colour, Depth, Motion];
        public void Dispose() { Colour.Dispose(); Depth.Dispose(); Motion.Dispose(); }
    }
    RuntimeCharacterProgramCache<RuntimePrograms>? runtimePrograms;
    readonly RuntimeCharacterNativeCache<GpuObjectPart, ReflectedProgram, MotionHolder> runtimeNatives = new();

    RuntimePrograms Programs(RuntimeCharacterShaderSet source) => (runtimePrograms ??= new(CreateRuntimePrograms,
        programs => Gpu.DeferDelete(programs.Dispose))).Get(source);

    RuntimePrograms CreateRuntimePrograms(RuntimeCharacterShaderSet source)
    {
        ReflectedProgram? colour = null, depth = null;
        try
        {
            colour = new(Gpu, nativeFrame, source.ColourVertex,
                RuntimeColourFragment(source.ColourFragment), "runtime characters", CharacterShaders.InstanceLocation);
            depth = new(Gpu, nativeFrame, source.DepthVertex, source.DepthFragment, "runtime characters depth", CharacterShaders.InstanceLocation);
            var motion = new ReflectedProgram(Gpu, nativeFrame, source.MotionVertex, source.MotionFragment, "runtime characters motion", CharacterShaders.InstanceLocation);
            return new(colour, depth, motion);
        }
        catch { colour?.Dispose(); depth?.Dispose(); throw; }
    }

    internal static string RuntimeColourFragment(string source) => Gi.GiResolveShaders.WithAlbedo(source,
        inShader: true, none: !source.Contains("giAlbedoOut", StringComparison.Ordinal));

    ReflectedProgram ProgramFor(RuntimeCharacterShaderSet? shaders)
    {
        if (shaders is null) return motionPass ? motionProg : depthPass ? depthProg : colourProg;
        var programs = Programs(shaders);
        return motionPass ? programs.Motion : depthPass ? programs.Depth : programs.Colour;
    }

    ref ObjectNativeMesh RuntimeNative(GpuObjectPart part, ReflectedProgram program)
    {
        return ref runtimeNatives.Get(part, program, static () => new MotionHolder()).Native;
    }

    void SweepRuntimePrograms()
    {
        if (runtimePrograms is null) return;
        var live = new HashSet<RuntimeCharacterShaderSet>(ReferenceEqualityComparer.Instance);
        foreach (var key in runtimeAssets.Keys) if (key.Geometry.Shaders is { } shaders) live.Add(shaders);
        foreach (var instance in Draws.Items.Concat(Draws.Distant).Concat(Draws.Preparations)) if (instance.Geometry?.Shaders is { } shaders) live.Add(shaders);
        if (!runtimePrograms.Retain(live)) return;
        var programs = runtimePrograms.Values.SelectMany(value => value.All).ToHashSet();
        runtimeNatives.RetainPrograms(programs);
        ResetRuntimeBatches();
    }

    // A shader set can outlive thousands of geometry revisions. Retire mesh metadata independently of its programs.
    void SweepRuntimeMeshLayouts()
    {
        var parts = new HashSet<GpuObjectPart>(ReferenceEqualityComparer.Instance);
        foreach (var asset in assets.Values.Concat(runtimeAssets.Values).Concat(iconByAsset.Keys))
            if (asset is not null) foreach (var part in asset.Parts) foreach (var meshPart in part.Mesh.Parts) parts.Add(meshPart);
        bool changed = runtimeNatives.RetainParts(parts);
        if (changed || batchKeys.Any(key => !parts.Contains(key.Part))) ResetRuntimeBatches();
    }

    void ResetRuntimeBatches()
    {
        // DrawJob already copied its pipeline/binding rows, so resetting CPU gather caches cannot invalidate queued frames.
        batchIds.Clear();
        batchKeys.Clear();
        foreach (var asset in assets.Values.Concat(runtimeAssets.Values).Concat(iconByAsset.Keys))
            if (asset is not null) foreach (var part in asset.Parts) part.Batches = null;
        Reset(mainView);
        foreach (var view in cascadeViews) if (view is not null) Reset(view);
        static void Reset(ViewBatches view) { view.Begin(); view.ById = new Batch?[256]; view.Chunks.Clear(); view.Stamp = -1; }
    }
}
