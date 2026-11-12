using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Terrain;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The outlined buildings (the selected and the hovered one; Meitou only, docs/game/ui-input.md "Hover outline"): the main view's colour pass keeps
/// its draws of each building's parts and of the layout objects that go with it (<see cref="Selected"/>), with the levels, fades and construction
/// clip it drew them at; after the post chain each building's are drawn alone into a mask (<see cref="DrawSelectionMasks"/>) that
/// <see cref="HoverOutlineRenderer"/> outlines and tints as it does a hovered character. Nothing for a building the interior view shows.
/// </summary>
public sealed partial class WorldObjectRenderer
{
    /// <summary>
    /// One draw <see cref="Emit"/> added for an outlined building (its key, <see cref="AddLevels"/>'s arguments) and the instance's bounding sphere.
    /// </summary>
    readonly record struct SelectionDraw(string Key, GpuObjectMesh Gpu, ObjectMaterialSet Materials, LodBlend Blend, Matrix4x4 M, float Weight,
        float Near, bool BackCull,
        bool Site, Vector3 Centre, float Radius);

    /// <summary>
    /// The draws the last colour pass kept for the outlined buildings (cleared at the next update: the meshes they name may go).
    /// </summary>
    readonly List<SelectionDraw> _selectionDraws = [];

    /// <summary>
    /// The scratch list of one building's draws for its mask.
    /// </summary>
    readonly List<SelectionDraw> _maskDraws = [];

    /// <summary>
    /// The masks drawn this frame, one per outlined building (<see cref="SelectionMasks"/>).
    /// </summary>
    readonly List<HoverMask> _selectionMasks = [];

    /// <summary>
    /// The mask targets (colour, depth), one per outlined building, kept and remade when the size changes.
    /// </summary>
    readonly List<(GpuTexture Colour, GpuTexture Depth)> _selectionTargets = [];

    /// <summary>
    /// The buildings the running colour pass keeps draws of (<c>Selected</c>, less those the interior view shows); empty outside one.
    /// </summary>
    readonly List<string> _selecting = [];

    /// <summary>
    /// The buildings whose draws the next colour pass keeps for <see cref="DrawSelectionMasks"/> (their keys: their parts and their layout objects);
    /// the main view sets it round its <see cref="Draw"/>, as it does <see cref="FogCull"/>. Empty: none.
    /// </summary>
    public IReadOnlyList<(string Key, float Floor, Vector4 Colour)> Selected { get; set; } = [];

    /// <summary>
    /// This frame's masks of the outlined buildings (empty: none).
    /// </summary>
    public IReadOnlyList<HoverMask> SelectionMasks => _selectionMasks;

    /// <summary>
    /// Whether a placement is building <paramref name="key"/> or one of its parts or layout objects.
    /// </summary>
    static bool Selects(PlacedMesh p, string key) => p.PlacementId == key || p.ParentPlacementId == key;

    /// <summary>
    /// The outlined building <paramref name="p"/> belongs to (<see cref="_selecting"/>), null: none.
    /// </summary>
    string? SelectingKey(PlacedMesh p)
    {
        foreach (var key in _selecting)
            if (Selects(p, key)) return key;
        return null;
    }

    /// <summary>
    /// A colour pass with <see cref="Selected"/> set keeps its draws of those buildings afresh, less the ones the interior view shows
    /// (<see cref="Update"/> drops what is left).
    /// </summary>
    void BeginSelection()
    {
        _selecting.Clear();
        if (_depthPass || Selected.Count == 0) return;
        _selectionDraws.Clear();
        foreach (var (key, _, _) in Selected)
            if (!InteriorView.ContainsKey(key)) _selecting.Add(key);
    }

    /// <summary>
    /// Draws what the main view drew of each building of <paramref name="outlines"/> above its floor alone, through the colour pass (it writes alpha
    /// 1), into a
    /// <paramref name="width"/> × <paramref name="height"/> mask of its own outlined in its colour, seen from <paramref name="eye"/> along
    /// <paramref name="view"/> (unjittered), its planes hugging the building. Called after the frame's colour pass, outside any pass; the draw
    /// counters stay as that pass left them.
    /// </summary>
    public void DrawSelectionMasks(IReadOnlyList<(string Key, float Floor, Vector4 Colour)> outlines, int width, int height, Matrix4x4 view,
        float fieldOfView, Vector3 eye,
        WorldRenderOptions options)
    {
        _selectionMasks.Clear();
        if (_selectionDraws.Count > 0 && width > 0 && height > 0)
            foreach (var outline in outlines)
            {
                _maskDraws.Clear();
                foreach (var d in _selectionDraws)
                    if (d.Key == outline.Key) _maskDraws.Add(d);
                if (_maskDraws.Count > 0) DrawSelectionMask(outline, width, height, view, fieldOfView, eye, options);
            }
        _selectionDraws.Clear();
        _maskDraws.Clear();
    }

    /// <summary>
    /// One building's mask from its draws (<see cref="_maskDraws"/>), into the next free target.
    /// </summary>
    void DrawSelectionMask((string Key, float Floor, Vector4 Colour) outline, int width, int height, Matrix4x4 view, float fieldOfView, Vector3 eye,
        WorldRenderOptions options)
    {
        var (near, far, spheres) = SelectionPlanes(eye);
        var projection = DepthConvention.Perspective(fieldOfView, width / (float)height, near, far);
        var (colourTarget, depthTarget) = SelectionTarget(_selectionMasks.Count, width, height);
        var counters = (DrawCalls, DrawnTriangles, untexturedDraws: _untexturedDraws);
        BatchSelection();
        RecordSelection(PassTargets.Of(colourTarget, depthTarget), SelectionView(view * projection, eye, outline.Floor), options);
        (DrawCalls, DrawnTriangles, _untexturedDraws) = counters;
        _selectionMasks.Add(new HoverMask(colourTarget, depthTarget, near, far, outline.Colour, spheres));
    }

    /// <summary>
    /// How far above its floor the outline starts: the floor's own top (the slab's, at the building's height) is left out with what is below it.
    /// </summary>
    const float FloorClearance = 1;

    /// <summary>
    /// The mask's view: one cut volume with no footprint edges, everything at or below <paramref name="floor"/> + <see cref="FloorClearance"/>, which
    /// the draws (all cut, <see cref="BatchSelection"/>) leave out: the foundations, piers and the base slab the building stands on, hidden or not,
    /// get no outline.
    /// </summary>
    static ViewConstants SelectionView(Matrix4x4 viewProjection, Vector3 eye, float floor)
    {
        var view = new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = Vector3.UnitY, CutVolumes = 1 };
        view.CutRanges[0] = new Vector4(0, 0, float.MinValue, floor + FloorClearance);
        return view;
    }

    /// <summary>
    /// The mask's instances' bounding spheres and the planes round them, as the characters' masks have theirs.
    /// </summary>
    (float Near, float Far, List<(Vector3 Centre, float Radius)> Spheres) SelectionPlanes(Vector3 eye)
    {
        var spheres = new List<(Vector3 Centre, float Radius)>(_maskDraws.Count);
        float near = float.MaxValue, far = 0;
        foreach (var d in _maskDraws)
        {
            spheres.Add((d.Centre, d.Radius));
            float distance = Vector3.Distance(eye, d.Centre);
            (near, far) = (Math.Min(near, distance - d.Radius * 2), Math.Max(far, distance + d.Radius * 2));
        }
        return (Math.Max(0.25f, near), far, spheres);
    }

    /// <summary>
    /// The mask's draws as the only batches, all cut (<see cref="SelectionView"/>), their matrices in this frame's constants (the frame's next
    /// <see cref="Draw"/> starts its own from nothing).
    /// </summary>
    void BatchSelection()
    {
        foreach (var b in _batchMap.Values) b.Count = 0;
        _active.Clear();
        foreach (var d in _maskDraws) AddLevels(d.Gpu, d.Materials, d.Blend, d.M, d.Weight, d.Near, d.BackCull, d.Site, cut: true);
        UploadInstances();
    }

    /// <summary>
    /// The batches into the mask, cleared to nothing in the scene's depth convention (the outline reads its depth as the scene's).
    /// </summary>
    void RecordSelection(PassTargets target, in ViewConstants view, WorldRenderOptions options)
    {
        var cmd = Gpu.BeginNative("selection mask");
        cmd.BeginRendering(new RenderingDesc(
            target.Colour with { Load = LoadOp.Clear, Clear = new ClearColour(0f, 0f, 0f, 0f) },
            target.Depth with { Load = LoadOp.Clear, DepthClear = DepthConvention.Clear }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.SceneDepth(target.Formats));
        PrepareDraws(options, wire: false);
        RecordDraws(_colourProg, wire: false, in view, default);
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
        foreach (var b in _active) b.Count = 0;
        _active.Clear();
    }

    /// <summary>
    /// Mask <paramref name="index"/>'s targets, made again at a new size (the characters' masks' formats).
    /// </summary>
    (GpuTexture Colour, GpuTexture Depth) SelectionTarget(int index, int width, int height)
    {
        if (index < _selectionTargets.Count)
        {
            var made = _selectionTargets[index];
            if (made.Colour.Desc.Width == width && made.Colour.Desc.Height == height) return made;
            made.Colour.Dispose();
            made.Depth.Dispose();
        }
        var colour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Srgb8Alpha8, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget,
            Name: "selection mask"));
        var depth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height,
            Use: TextureUse.Sampled | TextureUse.DepthTarget,
            Name: "selection mask depth"));
        if (index < _selectionTargets.Count) _selectionTargets[index] = (colour, depth);
        else _selectionTargets.Add((colour, depth));
        return (colour, depth);
    }

    /// <summary>
    /// Frees the mask targets and forgets the masks.
    /// </summary>
    void DisposeSelection()
    {
        foreach (var (colour, depth) in _selectionTargets)
        {
            colour.Dispose();
            depth.Dispose();
        }
        _selectionTargets.Clear();
        _selectionMasks.Clear();
    }
}