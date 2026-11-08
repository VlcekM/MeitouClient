using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The character editor's set (docs/characters.md "Character editor", Preview): the original stands the edited characters on a building part in its own scene; here
/// that part, or the bar's meshes (the <c>editorbar</c> switch), are loose instances (so the streaming loads their meshes and textures) drawn into the character renderer's stage picture after the character, lit as it is.
/// </summary>
public sealed unsafe partial class WorldObjectRenderer
{
    readonly List<ObjectStreamer.Instance> stageInstances = [];
    Vector3 stageCasterCentre;
    /// <summary>How far from the character, past its own radius, a prop still casts into the studio's maps (they hold the character's sphere; the rest of a room need not be drawn 34 times).</summary>
    const float StageCasterReach = 80;

    /// <summary>The stage's props are not ready to draw yet: their meshes or materials are loading, or their textures are not all resident (a still waits for them).</summary>
    public bool StagePropPending => stageInstances.Exists(inst => inst.Mesh.Status != ObjectMesh.State.Failed
        && (inst.Gpu is null || inst.Materials is not { } set || !set.Parts.All(p => p.TexturesSettled)));

    /// <summary>Sets the meshes the stage shows round the character (none: the editor is closed). <paramref name="casterCentre"/>: where the character stands.</summary>
    public void SetStageProps(IReadOnlyList<PlacedMesh> props, Vector3 casterCentre)
    {
        foreach (var inst in stageInstances) streamer.DropLoose(inst);
        stageInstances.Clear();
        stageCasterCentre = casterCentre;
        foreach (var prop in props) stageInstances.Add(streamer.MakeLoose(prop));
    }

    /// <summary>The loaded props into this frame's instance data, relative to <paramref name="origin"/>; <paramref name="castersOnly"/>: those near enough the character to cast. The number written.</summary>
    int FillStageInstances(Vector3 origin, bool castersOnly)
    {
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();
        long now = Environment.TickCount64;
        foreach (var inst in stageInstances)
        {
            if (inst is not { Gpu: { } gpu, Materials: { } materials }) continue;
            if (castersOnly && Vector3.Distance(inst.Centre, stageCasterCentre) - inst.Radius > StageCasterReach) continue;
            inst.Mesh.LastUsed = now;
            Add(gpu, materials, gpu.MinLevel, inst.Transform * Matrix4x4.CreateTranslation(-origin), 0, 2, 0);
        }
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        if (total == 0) return 0;
        instances = Gpu.Frame.AllocateConstants((ulong)total * InstanceStride, 16);
        var target = new Span<byte>(instances.Pointer, total * InstanceStride);
        foreach (var b in active)
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
        return total;
    }

    /// <summary>
    /// Draws the stage's prop into <paramref name="colour"/> and <paramref name="depth"/>, which hold the character already (outside any pass): the stage's camera and light,
    /// the stage's lighting (a fog distance of -2) and no weather. What is not loaded yet is left out.
    /// </summary>
    /// <param name="studio">The characters, whose stage has a studio view this frame (Meitou, previewsun): the platform is lit by it.</param>
    /// <summary>
    /// The stage's prop into the pass the caller has open (the character renderer's studio maps: the sun's, the sky's, the camera's depth), seen through
    /// <paramref name="viewProjection"/> (world) from <paramref name="origin"/>, as a studio depth draw (cut-outs only). What is not loaded yet is left out.
    /// </summary>
    internal void RecordStagePropDepth(Matrix4x4 viewProjection, Vector3 origin)
    {
        if (FillStageInstances(origin, castersOnly: true) > 0)
        {
            var constants = new ViewConstants { ViewProjection = Matrix4x4.CreateTranslation(origin) * viewProjection, Eye = Vector3.Zero, FogDistance = -5 };
            PrepareDraws(iconOptions, wire: false);
            for (int d = 0; d < drawCount; d++) draws[d].Push.Spare = MeshSurface.NoWeather;
            RecordDraws(colourProg, wire: false, in constants, default);
        }
        foreach (var b in active) b.Count = 0;
        active.Clear();
    }

    internal void DrawStageProp(GpuTexture colour, GpuTexture depth, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, float fogDistance = -2, Characters.CharacterRenderer? studio = null)
    {
        // Relative to the eye: at world coordinates (tens of thousands of units) the plates and bolts a hair above the planks would fight for depth.
        if (FillStageInstances(eye, castersOnly: false) == 0) return;
        var cmd = Gpu.BeginNative("character stage platform");
        var targets = PassTargets.Of(colour, depth);
        cmd.BeginRendering(new RenderingDesc(targets.Colour, targets.Depth, targets.Width, targets.Height));
        Gpu.BeginHostPass(cmd, targets, DrawState.Scene(targets.Formats));
        Gpu.SetPassViewport(new PixelViewport(0, 0, targets.Width, targets.Height, 0, 1), new PixelRect(0, 0, (uint)targets.Width, (uint)targets.Height));
        var constants = new ViewConstants { ViewProjection = Matrix4x4.CreateTranslation(eye) * viewProjection, Eye = Vector3.Zero, LightDir = light, FogDistance = fogDistance };
        // The studio: its shadow and sky fields, the platform drawn eye-relative as before; no shadow cut (the objects read cutVolumes as one).
        if (studio?.StageStudio is { } sv)
        {
            constants = sv;
            constants.ViewProjection = Matrix4x4.CreateTranslation(eye) * viewProjection;
            constants.Eye = Vector3.Zero;
            constants.CutVolumes = 0;
            // Eye-relative like the platform's positions (vWorld here is the point less the eye).
            constants.CutRanges[0] = new Vector4(new Vector3(sv.CutRanges[0].X, sv.CutRanges[0].Y, sv.CutRanges[0].Z) - eye, sv.CutRanges[0].W);
        }
        PrepareDraws(iconOptions, wire: false);
        for (int d = 0; d < drawCount; d++) draws[d].Push.Spare = MeshSurface.NoWeather;
        RecordDraws(colourProg, wire: false, in constants, default);
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        foreach (var b in active) b.Count = 0;
        active.Clear();
    }
}
