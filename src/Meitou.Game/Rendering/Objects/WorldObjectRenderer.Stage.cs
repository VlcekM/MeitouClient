using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Sky;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The character editor's platform (docs/characters.md "Character editor", Preview): the original stands the edited characters on a building part in
/// its own scene; here that part is a loose instance (so the streaming loads its mesh and textures) drawn into the character renderer's stage picture
/// after the character, lit as it is.
/// </summary>
public sealed unsafe partial class WorldObjectRenderer
{
    /// <summary>
    /// The prop the stage shows (null: none).
    /// </summary>
    PlacedMesh? _stageProp;

    /// <summary>
    /// The prop's loose instance (null: none).
    /// </summary>
    ObjectStreamer.Instance? _stageInstance;

    /// <summary>
    /// The stage's prop is not ready to draw yet: its mesh or materials are loading, or its textures are not all resident (a still waits for it).
    /// </summary>
    public bool StagePropPending => _stageInstance is { } inst && inst.Mesh.Status != ObjectMesh.State.Failed
        && (inst.Gpu is null || inst.Materials is not { } set || !set.Parts.All(p => p.TexturesSettled));

    /// <summary>
    /// Sets the mesh the stage shows under the character (null: none, the editor is closed).
    /// </summary>
    public void SetStageProp(PlacedMesh? prop)
    {
        if (prop == _stageProp) return;
        if (_stageInstance is not null) _streamer.DropLoose(_stageInstance);
        _stageProp = prop;
        _stageInstance = prop is null ? null : _streamer.MakeLoose(prop);
    }

    /// <summary>
    /// The stage's prop into the pass the caller has open (the character renderer's studio maps: the sun's, the sky's, the camera's depth), seen
    /// through <paramref name="viewProjection"/> (world) from <paramref name="origin"/>, as a studio depth draw (cut-outs only). Nothing while its
    /// mesh is not loaded.
    /// </summary>
    internal void RecordStagePropDepth(Matrix4x4 viewProjection, Vector3 origin)
    {
        if (_stageInstance is not { Gpu: { } gpu, Materials: { } materials } inst) return;

        int total = BatchStageProp(gpu, materials, inst.Transform * Matrix4x4.CreateTranslation(-origin));
        if (total > 0)
        {
            UploadStageInstances(total);
            var constants = new ViewConstants
            {
                ViewProjection = Matrix4x4.CreateTranslation(origin) * viewProjection,
                Eye = Vector3.Zero,
                FogDistance = -5,
            };
            RecordStageDraws(constants);
        }

        ClearStageBatches();
    }

    /// <summary>
    /// Draws the stage's prop into <paramref name="colour"/> and <paramref name="depth"/>, which hold the character already (outside any pass): the
    /// stage's camera and light, the stage's lighting (a fog distance of -2) and no weather. Nothing while its mesh is not loaded.
    /// </summary>
    /// <param name="studio">The characters, whose stage has a studio view this frame (Meitou, previewsun): the platform is lit by it.</param>
    internal void DrawStageProp(GpuTexture colour, GpuTexture depth, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, float fogDistance = -2,
        Characters.CharacterRenderer? studio = null)
    {
        if (_stageInstance is not { Gpu: { } gpu, Materials: { } materials } inst) return;

        inst.Mesh.LastUsed = Environment.TickCount64;

        // Relative to the eye: at world coordinates (tens of thousands of units) the plates and bolts a hair above the planks would fight for depth.
        int total = BatchStageProp(gpu, materials, inst.Transform * Matrix4x4.CreateTranslation(-eye));
        if (total == 0) return;

        UploadStageInstances(total);
        var cmd = Gpu.BeginNative("character stage platform");
        var targets = PassTargets.Of(colour, depth);
        cmd.BeginRendering(new RenderingDesc(targets.Colour, targets.Depth, targets.Width, targets.Height));
        Gpu.BeginHostPass(cmd, targets, DrawState.Scene(targets.Formats));
        Gpu.SetPassViewport(new PixelViewport(0, 0, targets.Width, targets.Height, 0, 1),
            new PixelRect(0, 0, (uint)targets.Width, (uint)targets.Height));
        RecordStageDraws(StagePropConstants(viewProjection, eye, light, fogDistance, studio));
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        ClearStageBatches();
    }

    /// <summary>
    /// The batches emptied and the prop added at its coarsest level with <paramref name="model"/>; each batch's offset set. Returns the instance count.
    /// </summary>
    int BatchStageProp(GpuObjectMesh gpu, ObjectMaterialSet materials, Matrix4x4 model)
    {
        foreach (var b in _batchMap.Values) b.Count = 0;
        _active.Clear();
        Add(gpu, materials, gpu.MinLevel, model, 0, 2, 0);

        int total = 0;
        foreach (var b in _active)
        {
            b.Offset = total;
            total += b.Count;
        }

        return total;
    }

    /// <summary>
    /// The active batches' instances into this frame's constants.
    /// </summary>
    void UploadStageInstances(int total)
    {
        _instances = Gpu.Frame.AllocateConstants((ulong)total * InstanceStride, 16);
        var target = new Span<byte>(_instances.Pointer, total * InstanceStride);
        foreach (var b in _active)
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
    }

    /// <summary>
    /// The stage's view block: its camera eye-relative and light, or the studio's shadow and sky fields when <paramref name="studio"/> has a studio
    /// view this frame.
    /// </summary>
    static ViewConstants StagePropConstants(Matrix4x4 viewProjection, Vector3 eye, Vector3 light, float fogDistance,
        Characters.CharacterRenderer? studio)
    {
        var constants = new ViewConstants
        {
            ViewProjection = Matrix4x4.CreateTranslation(eye) * viewProjection,
            Eye = Vector3.Zero,
            LightDir = light,
            FogDistance = fogDistance,
        };

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

        return constants;
    }

    /// <summary>
    /// The active batches drawn with <paramref name="constants"/>, without weather.
    /// </summary>
    void RecordStageDraws(in ViewConstants constants)
    {
        PrepareDraws(IconOptions, wire: false);
        for (int d = 0; d < _drawCount; d++) _draws[d].Push.Spare = MeshSurface.NoWeather;
        RecordDraws(_colourProg, wire: false, in constants, default);
    }

    /// <summary>
    /// The active batches emptied.
    /// </summary>
    void ClearStageBatches()
    {
        foreach (var b in _active) b.Count = 0;
        _active.Clear();
    }
}