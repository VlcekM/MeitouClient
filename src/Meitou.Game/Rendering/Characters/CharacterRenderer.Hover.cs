using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Overlays;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The outlined characters drawn alone into masks of their own, one per colour, for the hover outline (<see cref="HoverOutlineRenderer"/>,
/// docs/game/ui-input.md "Hover outline").
/// </summary>
internal sealed unsafe partial class CharacterRenderer
{
    /// <summary>
    /// The mask targets (colour and depth) by group index, kept between frames and remade when the size changes.
    /// </summary>
    readonly List<(GpuTexture Colour, GpuTexture Depth)> _hoverTargets = [];
    /// <summary>
    /// This frame's masks, one per outline colour (<see cref="HoverMasks"/>).
    /// </summary>
    readonly List<HoverMask> _hoverMasks = [];

    /// <summary>
    /// This frame's masks (empty when nothing is outlined).
    /// </summary>
    public IReadOnlyList<HoverMask> HoverMasks => _hoverMasks;

    /// <summary>
    /// Draws the characters <paramref name="wanted"/> (by draw-list key) alone, in their live poses and the picture's levels, through the colour pass
    /// (it writes alpha 1), one <paramref name="width"/> × <paramref name="height"/> mask per colour, seen from <paramref name="eye"/> along
    /// <paramref name="view"/> (unjittered). Each mask's planes hug its characters, so their depth stays precise. Nothing for a character not drawn
    /// in the world (beyond the draw distance, or hidden by the interior view) or off screen. Called after <see cref="Update"/> and before the
    /// world's colour pass, whose statistics then stand.
    /// </summary>
    public void DrawHoverMasks(IReadOnlyList<(long Key, Vector4 Colour)> wanted, int width, int height, Matrix4x4 view, float fieldOfView,
        Vector3 eye)
    {
        _hoverMasks.Clear();
        if (wanted.Count == 0 || width <= 0 || height <= 0) return;
        var groups = HoverGroups(wanted, width, height, view, fieldOfView);
        for (int g = 0; g < groups.Count; g++)
        {
            var (colourTarget, depthTarget) = HoverTarget(g, width, height);
            var members = groups[g].Live;

            // The mask's planes hug its characters, so their depth stays precise.
            float near = float.MaxValue, far = 0;
            var spheres = new List<(Vector3, float)>();
            foreach (int i in members)
            {
                float distance = Vector3.Distance(eye, _live[i].Centre);
                (near, far) = (Math.Min(near, distance - _live[i].Radius * 2), Math.Max(far, distance + _live[i].Radius * 2));
                spheres.Add((_live[i].Centre, _live[i].Radius));
            }
            near = Math.Max(0.25f, near);
            var projection = DepthConvention.Perspective(fieldOfView, width / (float)height, near, far);

            var target = PassTargets.Of(colourTarget, depthTarget);
            for (int m = 0; m < members.Count; m++) DrawHoverMember(target, members[m], m == 0, view * projection, eye);
            _hoverMasks.Add(new HoverMask(colourTarget, depthTarget, near, far, groups[g].Colour, spheres));
        }
    }

    /// <summary>
    /// The wanted characters drawn in the world and on screen, grouped by colour (their live indices).
    /// </summary>
    List<(Vector4 Colour, List<int> Live)> HoverGroups(IReadOnlyList<(long Key, Vector4 Colour)> wanted, int width, int height, Matrix4x4 view,
        float fieldOfView)
    {
        var groups = new List<(Vector4 Colour, List<int> Live)>();
        var onScreen = view * Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, width / (float)height, 0.25f, 1e5f);
        foreach (var (key, colour) in wanted)
        {
            int index = -1;
            for (int i = 0; i < _liveCount; i++)
            {
                if (_live[i].Key == key)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0 || _live[index].PortraitOnly) continue;
            ref var l = ref _live[index];
            if (HoverOutlineRenderer.Bounds(l.Centre, l.Radius, onScreen, width, height, 0) is null) continue;

            int g = groups.FindIndex(x => x.Colour == colour);
            if (g < 0)
            {
                g = groups.Count;
                groups.Add((colour, []));
            }
            groups[g].Live.Add(index);
        }
        return groups;
    }

    /// <summary>
    /// Mask <paramref name="g"/>'s targets, made again at a new size (the portraits' formats, so the masks share their pipelines).
    /// </summary>
    (GpuTexture Colour, GpuTexture Depth) HoverTarget(int g, int width, int height)
    {
        if (_hoverTargets.Count > g && _hoverTargets[g].Colour.Desc.Width == width && _hoverTargets[g].Colour.Desc.Height == height)
            return _hoverTargets[g];
        if (_hoverTargets.Count > g)
        {
            _hoverTargets[g].Colour.Dispose();
            _hoverTargets[g].Depth.Dispose();
        }

        var colour = Gpu.CreateTexture(new TextureDesc(InternalFormat.Srgb8Alpha8, width, height, Use: TextureUse.Sampled | TextureUse.ColourTarget,
            Name: "hover mask"));
        var depth = Gpu.CreateTexture(new TextureDesc(InternalFormat.DepthComponent24, width, height,
            Use: TextureUse.Sampled | TextureUse.DepthTarget, Name: "hover mask depth"));
        if (_hoverTargets.Count > g) _hoverTargets[g] = (colour, depth);
        else _hoverTargets.Add((colour, depth));
        return (colour, depth);
    }

    /// <summary>
    /// One character (live index <paramref name="index"/>) into the mask through the colour pass; the first clears it.
    /// </summary>
    void DrawHoverMember(PassTargets target, int index, bool first, Matrix4x4 viewProjection, Vector3 eye)
    {
        var cmd = Gpu.BeginNative("hover mask");
        var load = first ? LoadOp.Clear : LoadOp.Load;
        cmd.BeginRendering(new RenderingDesc(
            target.Colour with { Load = load, Clear = new ClearColour(0f, 0f, 0f, 0f) },
            target.Depth with { Load = load, DepthClear = DepthConvention.Clear }, target.Width, target.Height));
        Gpu.BeginHostPass(cmd, target, DrawState.SceneDepth(target.Formats));
        _portraitLive = index;
        try
        {
            DrawView(new ViewConstants { ViewProjection = viewProjection, Eye = eye, LightDir = Vector3.UnitY }, eye, []);
        }
        finally
        {
            _portraitLive = -1;
        }
        Gpu.EndHostPass(cmd);
        cmd.EndRendering();
        Gpu.EndNative(cmd);
    }

    /// <summary>
    /// Frees the mask targets and forgets the masks.
    /// </summary>
    void DisposeHover()
    {
        foreach (var (colour, depth) in _hoverTargets)
        {
            colour.Dispose();
            depth.Dispose();
        }

        _hoverTargets.Clear();
        _hoverMasks.Clear();
    }
}