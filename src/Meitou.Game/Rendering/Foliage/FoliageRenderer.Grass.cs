using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// The GPU grass path (docs/renderer-native.md 5.4 and 5.6.2) over the pages' store (<see cref="FoliageGrassStore"/>). Every page's blades sit
/// in one arena (<see cref="FoliageGrassGpu"/>), so a blade draw is <c>Draw(vertices, blades, 0, firstBlade)</c> from one vertex binding. The CPU path
/// (<see cref="GpuGrass"/> off) walks the pages and records those draws; the GPU path writes one patch table per view and lets the cull kernels
/// pick the pages, the blades and the order, then records one indirect draw.
/// </summary>
public sealed unsafe partial class FoliageRenderer
{
    /// <summary>
    /// The grass pages' store, the zones' numbers in its tables and the blade programs' inputs over its arena.
    /// </summary>
    readonly FoliageGrassStore _grass;

    /// <summary>
    /// The grass of a colour view is drawn twice (docs/render-foliage.md "Grass depth first"): its depth alone, then its colour where the depth is
    /// its own, so a pixel is lit once however many blades cover it. Off: one draw, every layer lit.
    /// </summary>
    public bool GrassDepthFirst { get; set; } = true;

    /// <summary>
    /// The grass drawn by the GPU (docs/renderer-native.md 5.6.2; where the device allows it): per view two compute kernels choose the pages in view,
    /// the blades each shows and the order, and one indirect draw records them all. Off is the CPU path (the reference): the CPU walks the pages,
    /// frustum-tests and sorts them.
    /// </summary>
    public bool GpuGrass { get; set; } = true;

    /// <summary>
    /// The GPU path draws the grass (the switch, and the device runs the kernels).
    /// </summary>
    bool GpuGrassActive => GpuGrass && _grass.GpuProgram is not null;

    // ---- the GPU path ----

    /// <summary>
    /// The view's patch rows (<see cref="BuildGrassRows"/>), reused.
    /// </summary>
    readonly List<GrassPatchRow> _grassPatchRows = [];
    /// <summary>
    /// The view's zone rows, by zone, grown as needed.
    /// </summary>
    GrassZoneRow[] _grassZoneRows = new GrassZoneRow[16];

    /// <summary>
    /// The view's zone and patch tables: for every zone with pages in the store, its patches' rows (a zone's patches can change when it is laid out
    /// again, so the rows are written per view), as <see cref="PrepareGrass"/> and <see cref="PrepareMotion"/> read them: the range at the current
    /// grass range setting, the sprite's readiness, the push values of <see cref="GrassPush"/>. Textures are GL ids here (see
    /// <see cref="BindRows"/>). Returns the number of patches that can draw.
    /// </summary>
    int BuildGrassRows(bool motion, WorldRenderOptions options, bool coverage)
    {
        _grassPatchRows.Clear();
        if (_grassZoneRows.Length < _grass.ZoneHigh)
            _grassZoneRows = new GrassZoneRow[Math.Max(_grass.ZoneHigh, _grassZoneRows.Length * 2)];
        else Array.Clear(_grassZoneRows);
        int active = 0;
        foreach (var state in _zones.Values)
        {
            if (state.GrassZone < 0) continue;
            _grassZoneRows[state.GrassZone] = new GrassZoneRow { PatchBase = (uint)_grassPatchRows.Count, PatchCount = (uint)state.Patches.Count };
            for (int i = 0; i < state.Patches.Count; i++)
            {
                var patch = state.Patches[i];
                var g = patch.Grass;
                var (sprite, colour) = state.PatchTextures[i];

                // As Patch does on each call: counts as use, and brings an unloaded texture back.
                uint spriteId = sprite?.Key ?? 0, colourId = colour?.Key ?? 0;
                bool wind = patch.Layer.Wind;
                bool isActive = spriteId != 0 && (!motion || (wind && g.SwayLength != 0));
                bool hasColour = !motion && options.Textures && colourId != 0;
                uint flags = (g.CrossQuads ? FoliageGrassShaders.FlagCross : 0) | (hasColour ? FoliageGrassShaders.FlagColourMap : 0) |
                    (!motion && coverage ? FoliageGrassShaders.FlagCoverage : 0) |
                    (!motion && options.Wireframe == 2 ? FoliageGrassShaders.FlagWireframe : 0) |
                    (isActive ? FoliageGrassShaders.FlagActive : 0);
                if (isActive) active++;
                _grassPatchRows.Add(new GrassPatchRow
                {
                    Size = new Vector4(g.QuadMinWidth, g.QuadMaxWidth, g.QuadMinHeight, g.QuadMaxHeight),
                    ColourBounds = new Vector4(patch.X0, patch.Z0, patch.X1, patch.Z1),
                    Sway = motion || wind ? g.SwayLength : 0f,
                    Range = GrassRange(patch),
                    Frequency = 2f,
                    Flags = flags,
                    Sprite = motion || options.Textures ? spriteId : 0,
                    ColourMap = hasColour ? colourId : 0,
                    VertexCount = g.CrossQuads ? 12u : 6u,
                });
            }
        }
        return active;
    }

    /// <summary>
    /// The rows' texture keys as bindless indices (inside the segment, as the CPU path's <see cref="Texture"/> calls are), and the tables into the
    /// frame's constants.
    /// </summary>
    GrassTables BindRows(uint nearDepth)
    {
        var rows = CollectionsMarshal.AsSpan(_grassPatchRows);
        for (int i = 0; i < rows.Length; i++)
        {
            ref var r = ref rows[i];
            r.Sprite = GrassTexture(r.Sprite);
            r.ColourMap = Texture(r.ColourMap);
            r.NearDepth = nearDepth;
        }
        return _grass.Store.Prepare(_grassZoneRows.AsSpan(0, Math.Max(_grass.ZoneHigh, 1)), rows);
    }

    /// <summary>
    /// One view's grass on the GPU: the cull into <see cref="GpuFrame.PreFrame"/>, then one indirect draw in a native segment. False when nothing can
    /// draw.
    /// </summary>
    bool DrawGrassGpu(Matrix4x4 viewProjection, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, Vector3 light, Vector3 fogColour,
        float fogDistance, bool coverage)
    {
        if (_grass.Store.SlotHigh == 0 || frustum.Length > 8) return false;
        if (BuildGrassRows(false, options, coverage) == 0) return false;
        var gp = _grass.GpuProgram!;
        var p = gp.P;

        // Prepare (wave 4, docs/renderer-native.md 6.2): the pass state, the cull (into PreFrame), the sets and the draw, into a job.
        var targets = Gpu.CurrentTargets();
        var state = GrassState(Gpu, coverage);
        int segment = SegmentId(GrassKind, p, targets, state);
        NewTextureSegment();
        var tables = BindRows(0);
        var (prefixIndex, fraction) = FoliageGrassGpu.DensityStep(Math.Min(GrassDensitySetting, MaxGrassDensity) / MaxGrassDensity);
        var result = _grass.Store.Dispatch(frustum, new Vector2(eye.X, eye.Z), PageSize, prefixIndex, fraction, in tables,
            occlusion: FoliageGrassGpu.OcclusionCull ? Occlusion : default);
        var view = new ViewConstants { ViewProjection = Projected(viewProjection, eye), Eye = eye, LightDir = light, FogColour = fogColour,
            FogDistance = fogDistance, Time = SwayPhase() };
        if (!_depthPass) InteriorMaskRenderer.Apply(ref view, InteriorCut);

        // Depth first: the same draw with the colour masked and the fragment stopping after its cut (uGrassDepthOnly), then the colour with the
        // depth test EQUAL and no depth write. Blades cover each other many times over and the lighting ran for every layer the depth test had not
        // yet hidden (Gut: 4.2 ms of 17.5, with the traced sun's rays for pixels that were then covered).
        bool depthFirst = GrassDepthFirst && !_depthPass && !coverage && !result.IsEmpty && state.DepthTest && state.DepthWrite;
        if (depthFirst)
        {
            RecordGrassDepth(gp, targets, state, view, in tables, result);
            state = state with { Compare = DepthFunction.Equal, DepthWrite = false };
            segment = SegmentId(GrassEqualKind, p, targets, state);
        }

        var job = _grassGpuJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program) = (this, targets, state, p);
        job.Frame = _grass.Frame!.Prepare(in view, [tables.Patches.Binding]);
        if (depthFirst)
        {
            job.Pipeline = PipelineFor(ref _grass.MeshGpuEqual, gp, segment, state, targets.Formats, "foliage grass gpu after depth");
            (job.Vertices, job.Result) = (_grass.MeshGpuEqual.Vertices, result);
        }
        else
        {
            job.Pipeline = result.IsEmpty ? null : PipelineFor(ref _grass.MeshGpu, gp, segment, state, targets.Formats, "foliage grass gpu");
            (job.Vertices, job.Result) = (_grass.MeshGpu.Vertices, result);
        }

        Gpu.Record("foliage grass", job);
        DrawCalls += _grass.Store.LateDraws;
        DrawnBlades += (int)_grass.Store.LateBlades;
        return true;
    }

    /// <summary>
    /// The depth draw of the depth-first pair (<see cref="GrassDepthFirst"/>): the blades with the colour masked, the fragment stopping after its
    /// cut (the view's PreviousTime -1 is its uGrassDepthOnly).
    /// </summary>
    void RecordGrassDepth(NativeProg gp, PassTargets targets, DrawState state, ViewConstants view, in GrassTables tables, GrassResult result)
    {
        var p = gp.P;
        var depthState = state with { ColourMask = 0 };
        view.PreviousTime = -1;
        var job = _grassGpuJobs.Rent();
        (job.Owner, job.Targets, job.State, job.Program) = (this, targets, depthState, p);
        job.Frame = _grass.Frame!.Prepare(in view, [tables.Patches.Binding]);
        int segment = SegmentId(GrassDepthKind, p, targets, depthState);
        job.Pipeline = PipelineFor(ref _grass.MeshGpuDepth, gp, segment, depthState, targets.Formats, "foliage grass gpu depth");
        (job.Vertices, job.Result) = (_grass.MeshGpuDepth.Vertices, result);
        Gpu.Record("foliage grass depth", job);
    }

    /// <summary>
    /// The pool of <see cref="GrassGpuJob"/>s.
    /// </summary>
    readonly JobPool<GrassGpuJob> _grassGpuJobs = new();

    /// <summary>
    /// One view's GPU grass as prepared (<see cref="DrawGrassGpu"/>): recorded on any thread, the same commands as before wave 4.
    /// </summary>
    sealed class GrassGpuJob : RecordJob
    {
        /// <summary>
        /// The renderer whose pool the job returns to.
        /// </summary>
        public FoliageRenderer Owner = null!;
        /// <summary>
        /// The pass targets (viewport and scissor).
        /// </summary>
        public PassTargets Targets = null!;
        /// <summary>
        /// The pass's raster, depth and bias state.
        /// </summary>
        public DrawState State = null!;
        /// <summary>
        /// The grass program.
        /// </summary>
        public GpuProgram Program = default;
        /// <summary>
        /// The native frame sets of the segment.
        /// </summary>
        public FrameBinding Frame;
        /// <summary>
        /// The grass pipeline (null: nothing is drawn).
        /// </summary>
        public GpuPipeline? Pipeline;
        /// <summary>
        /// The blade mesh's vertex buffers.
        /// </summary>
        public GpuBufferBinding[] Vertices = [];
        /// <summary>
        /// The view's cull result, whose draws and count the indirect draw reads.
        /// </summary>
        public GrassResult Result;

        public override void Record(IGpuCommandList cmd)
        {
            var (targets, state) = (Targets, State);
            cmd.SetViewport(targets.Viewport);
            cmd.SetScissor(targets.Scissor);
            cmd.SetRaster(state.Cull, state.Front);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            NativeFrame.Record(cmd, Program, in Frame);
            if (Pipeline is { } pipeline)
            {
                var r = Result;
                cmd.BindPipeline(pipeline);
                cmd.BindVertexBuffers(0, Vertices);
                cmd.DrawIndirectCount(r.Draws, r.DrawsOffset, r.Counters, r.CountersOffset, (uint)r.MaxDraws);
            }
        }

        public override void Release()
        {
            Pipeline = null;
            Owner._grassGpuJobs.Return(this);
        }
    }

    /// <summary>
    /// <see cref="DrawGrassMotion"/> on the GPU: the same cull for the scene camera over the patches that sway, one indirect draw.
    /// </summary>
    void DrawGrassMotionGpu(MotionTargets targets, Vector3 eye, float time, float previousTime, Matrix4x4 previous)
    {
        if (_grass.Store.SlotHigh == 0 || _motionFrustum.Length > 8) return;
        if (BuildGrassRows(true, default!, false) == 0) return;
        var gp = _grass.MotionGpuProgram!;
        var p = gp.P;
        var cmd = Gpu.BeginGuest("foliage grass motion");
        var pass = Gpu.CurrentTargets();
        var drawState = MotionState(Gpu);
        int segment = SegmentId(MotionKind, p, pass, drawState);
        cmd.SetViewport(pass.Viewport);
        cmd.SetScissor(pass.Scissor);
        cmd.SetRaster(drawState.Cull, drawState.Front);
        cmd.SetDepth(drawState.DepthTest, drawState.DepthWrite, drawState.Compare);
        cmd.SetDepthBias(drawState.BiasEnable, drawState.BiasConstant, drawState.BiasSlope);
        var view = new ViewConstants
        {
            ViewProjection = _motionRelative,
            PreviousViewProjection = previous,
            Eye = eye,
            Time = time,
            PreviousTime = previousTime,
            NearPlanes = targets.Planes,
            JitterNdc = targets.JitterNdc,
        };
        NewTextureSegment();
        var tables = BindRows(NearDepthIndex(targets.Depth));
        var (prefixIndex, fraction) = FoliageGrassGpu.DensityStep(Math.Min(GrassDensitySetting, MaxGrassDensity) / MaxGrassDensity);
        var result = _grass.Store.Dispatch(_motionFrustum, new Vector2(eye.X, eye.Z), PageSize, prefixIndex, fraction, in tables, _motionScale);
        _grass.Frame!.Bind(cmd, p, in view, [tables.Patches.Binding]);
        if (!result.IsEmpty)
        {
            cmd.BindPipeline(PipelineFor(ref _grass.MeshMotionGpu, gp, segment, drawState, pass.Formats, "foliage grass motion gpu"));
            cmd.BindVertexBuffers(0, _grass.MeshMotionGpu.Vertices);
            cmd.DrawIndirectCount(result.Draws, result.DrawsOffset, result.Counters, result.CountersOffset, (uint)result.MaxDraws);
        }
        Gpu.EndGuest(cmd);
    }
}