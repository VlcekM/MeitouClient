using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The selected building's outline (Meitou only, docs/game/ui-input.md "Hover outline"): the main view's colour pass keeps its draws of the building's parts and of
/// the layout objects that go with it (<see cref="Selected"/>), with the levels, fades and construction clip it drew them at; after the post chain they are drawn alone
/// into a mask (<see cref="DrawSelectionMask"/>) that <see cref="HoverOutlineRenderer"/> outlines and tints as it does a hovered character. Nothing while the
/// interior view shows the building.
/// </summary>
public sealed partial class WorldObjectRenderer
{
    /// <summary>One draw <see cref="Emit"/> added for the selected building (<see cref="AddLevels"/>'s arguments) and the instance's bounding sphere.</summary>
    readonly record struct SelectionDraw(GpuObjectMesh Gpu, ObjectMaterialSet Materials, LodBlend Blend, Matrix4x4 M, float Weight, float Near, bool BackCull, bool Site,
        Vector3 Centre, float Radius);

    readonly List<SelectionDraw> selectionDraws = [];
    readonly List<(Vector3 Centre, float Radius)> selectionSpheres = [];
    readonly List<HoverMask> selectionMasks = [];
    GpuTexture? selectionColour, selectionDepth;
    // The building the running colour pass keeps draws of (Selected, unless the interior view shows it); null outside one.
    string? selecting;

    /// <summary>
    /// The placement whose draws the next colour pass keeps for <see cref="DrawSelectionMask"/> (the selected building's key: its parts and its layout objects);
    /// the main view sets it round its <see cref="Draw"/>, as it does <see cref="FogCull"/>. Null: none.
    /// </summary>
    public string? Selected { get; set; }

    /// <summary>This frame's mask of the selected building (empty: none).</summary>
    public IReadOnlyList<HoverMask> SelectionMasks => selectionMasks;

    static bool Selects(PlacedMesh p, string key) => p.PlacementId == key || p.ParentPlacementId == key;

    /// <summary>A colour pass with <see cref="Selected"/> set keeps its draws of that building afresh, unless the interior view shows it (<see cref="Update"/> drops what is left).</summary>
    void BeginSelection()
    {
        selecting = null;
        if (depthPass || Selected is not { } key) return;
        selectionDraws.Clear();
        if (!InteriorView.ContainsKey(key)) selecting = key;
    }

    /// <summary>
    /// Draws what the main view drew of the selected building alone, through the colour pass (it writes alpha 1), into a <paramref name="width"/> × <paramref name="height"/>
    /// mask outlined in <paramref name="tint"/> (null: no outline), seen from <paramref name="eye"/> along <paramref name="view"/> (unjittered), its planes hugging the
    /// building. Called after the frame's colour pass, outside any pass; the draw counters stay as that pass left them.
    /// </summary>
    public void DrawSelectionMask(Vector4? tint, int width, int height, Matrix4x4 view, float fieldOfView, Vector3 eye, WorldRenderOptions options)
    {
        selectionMasks.Clear();
        if (tint is { } colour && selectionDraws.Count > 0 && width > 0 && height > 0)
        {
            var (near, far) = SelectionPlanes(eye);
            var projection = DepthConvention.Perspective(fieldOfView, width / (float)height, near, far);
            var (colourTarget, depthTarget) = SelectionTarget(width, height);
            var counters = (DrawCalls, DrawnTriangles, untexturedDraws);
            BatchSelection();
            RecordSelection(PassTargets.Of(colourTarget, depthTarget), new ViewConstants { ViewProjection = view * projection, Eye = eye, LightDir = Vector3.UnitY }, options);
            (DrawCalls, DrawnTriangles, untexturedDraws) = counters;
            selectionMasks.Add(new HoverMask(colourTarget, depthTarget, near, far, colour, selectionSpheres));
        }
        selectionDraws.Clear();
    }

    /// <summary>The kept instances' bounding spheres (into <see cref="selectionSpheres"/>) and the planes round them, as the characters' masks have theirs.</summary>
    (float Near, float Far) SelectionPlanes(Vector3 eye)
    {
        selectionSpheres.Clear();
        float near = float.MaxValue, far = 0;
        foreach (var d in selectionDraws)
        {
            selectionSpheres.Add((d.Centre, d.Radius));
            float distance = Vector3.Distance(eye, d.Centre);
            (near, far) = (Math.Min(near, distance - d.Radius * 2), Math.Max(far, distance + d.Radius * 2));
        }
        return (Math.Max(0.25f, near), far);
    }

    /// <summary>The kept draws as the only batches, their matrices in this frame's constants (the frame's next <see cref="Draw"/> starts its own from nothing).</summary>
    void BatchSelection()
    {
        foreach (var b in batchMap.Values) b.Count = 0;
        active.Clear();
        foreach (var d in selectionDraws) AddLevels(d.Gpu, d.Materials, d.Blend, d.M, d.Weight, d.Near, d.BackCull, d.Site);
        UploadInstances();
    }

    /// <summary>The batches into the mask, cleared to nothing in the scene's depth convention (the outline reads its depth as the scene's).</summary>
    void RecordSelection(PassTargets target, in ViewConstants view, WorldRenderOptions options)
    {
        var cmd = Gpu.BeginNative("selection mask");
        cmd.BeginRendering(new RenderingDesc(
            target.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0f, 0f, 0f, 0f) },
            target.Depth with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.SceneDepth(target.Formats));
        PrepareDraws(options, wire: false);
        RecordDraws(colourProg, wire: false, in view, default);
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        foreach (var b in active) b.Count = 0;
        active.Clear();
    }

    /// <summary>The mask's targets, made again at a new size (the characters' masks' formats).</summary>
    (GpuTexture Colour, GpuTexture Depth) SelectionTarget(int width, int height)
    {
        if (selectionColour is { } made && made.Desc.Width == width && made.Desc.Height == height) return (made, selectionDepth!);
        DisposeSelection();

        selectionColour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Srgb8Alpha8, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "selection mask"));
        selectionDepth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height, Use: TextureUse.Sampled | TextureUse.DepthTarget,
            Name: "selection mask depth"));
        return (selectionColour, selectionDepth);
    }

    void DisposeSelection()
    {
        selectionColour?.Dispose();
        selectionDepth?.Dispose();
        (selectionColour, selectionDepth) = (null, null);
        selectionMasks.Clear();
    }
}
