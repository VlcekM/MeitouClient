using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The character editor's platform (docs/characters.md "Character editor", Preview): the original stands the edited characters on a building part in its own scene; here
/// that part is a loose instance (so the streaming loads its mesh and textures) drawn into the character renderer's stage picture after the character, lit as it is.
/// </summary>
public sealed unsafe partial class WorldObjectRenderer
{
    PlacedMesh? stageProp;
    ObjectStreamer.Instance? stageInstance;

    /// <summary>The stage's prop is not ready to draw yet: its mesh or materials are loading, or its textures are not all resident (a still waits for it).</summary>
    public bool StagePropPending => stageInstance is { } inst && inst.Mesh.Status != ObjectMesh.State.Failed
        && (inst.Gpu is null || inst.Materials is not { } set || !set.Parts.All(p => p.TexturesSettled));

    /// <summary>Sets the mesh the stage shows under the character (null: none, the editor is closed).</summary>
    public void SetStageProp(PlacedMesh? prop)
    {
        if (prop == stageProp) return;
        if (stageInstance is not null) streamer.DropLoose(stageInstance);
        stageProp = prop;
        stageInstance = prop is null ? null : streamer.MakeLoose(prop);
    }

    /// <summary>
    /// Draws the stage's prop into <paramref name="colour"/> and <paramref name="depth"/>, which hold the character already (outside any pass): the stage's camera and light,
    /// the stage's lighting (a fog distance of -2) and no weather. Nothing while its mesh is not loaded.
    /// </summary>
    public void DrawStageProp(GpuTexture colour, GpuTexture depth, Matrix4x4 viewProjection, Vector3 eye, Vector3 light, float fogDistance = -2)
    {
        if (stageInstance is not { Gpu: { } gpu, Materials: { } materials } inst) return;
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();
        inst.Mesh.LastUsed = Environment.TickCount64;
        // Relative to the eye: at world coordinates (tens of thousands of units) the plates and bolts a hair above the planks would fight for depth.
        Add(gpu, materials, gpu.MinLevel, inst.Transform * Matrix4x4.CreateTranslation(-eye), 0, 2, 0);
        int total = 0;
        foreach (var b in active) { b.Offset = total; total += b.Count; }
        if (total == 0) return;
        instances = Gpu.Frame.AllocateConstants((ulong)total * InstanceStride, 16);
        var target = new Span<byte>(instances.Pointer, total * InstanceStride);
        foreach (var b in active)
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Data.AsSpan(0, b.Count)).CopyTo(target[(b.Offset * InstanceStride)..]);
        var cmd = Gpu.BeginNative("character stage platform");
        var targets = PassTargets.Of(colour, depth);
        cmd.BeginRendering(new RenderingDesc(targets.Colour, targets.Depth, targets.Width, targets.Height));
        Gpu.BeginHostPass(cmd, targets, DrawState.Scene(targets.Formats));
        Gpu.SetPassViewport(new PixelViewport(0, 0, targets.Width, targets.Height, 0, 1), new PixelRect(0, 0, (uint)targets.Width, (uint)targets.Height));
        var constants = new ViewConstants { ViewProjection = Matrix4x4.CreateTranslation(eye) * viewProjection, Eye = Vector3.Zero, LightDir = light, FogDistance = fogDistance };
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
