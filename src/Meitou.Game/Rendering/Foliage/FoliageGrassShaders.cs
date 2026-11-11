using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;

namespace Meitou.Game.Rendering.Foliage;

/// <summary>
/// The GPU grass (docs/renderer-native.md 5.4 and 5.6.2): two compute kernels that decide, per view, which (page, patch) draws exist, how
/// many blades each shows and in which order (nearest page first), and the native variants of the blade programs that read their per-draw
/// values (<see cref="GrassPatchRow"/>) from a storage buffer instead of push constants, so every visible draw of a view is one entry of a
/// single indirect draw.
/// </summary>
public static partial class FoliageGrassShaders
{
    /// <summary>
    /// The workgroup size of both kernels (one thread per slot).
    /// </summary>
    public const int Group = 256;

    /// <summary>
    /// Bit flags of <see cref="GrassPatchRow.Flags"/>.
    /// </summary>
    public const uint FlagCross = 1, FlagColourMap = 2, FlagCoverage = 4, FlagWireframe = 8, FlagActive = 16;

    /// <summary>
    /// The blades of a draw sit 16 vertices apart in the patch table: <c>firstVertex = row * 16</c>, <c>gl_VertexIndex &gt;&gt; 4</c> is the row.
    /// </summary>
    public const int RowShift = 4;

    const string Common = """
        #version 450
        layout(local_size_x = 256) in;
        struct Slot { float x0; float z0; float yMin; float yMax; uint firstBlade; uint count; uint zone; uint local; uint tie; };
        struct Zone { uint patchBase; uint patchCount; };
        struct Patch { vec4 size; vec4 colourBounds; float sway; float range; float frequency; uint flags; uint sprite; uint colourMap; uint nearDepth; uint vertexCount; };
        struct Draw { uint vertexCount; uint instanceCount; uint firstVertex; uint firstInstance; };
        layout(std430, set = 0, binding = 0) readonly buffer View { vec4 planes[8]; vec2 eye; uint planeCount; uint slotCount; float pageSize; float fraction; uint prefixIndex; float motionScale; vec4 hz[6]; uvec4 hzOff[3]; } view;
        layout(std430, set = 0, binding = 1) readonly buffer Slots { Slot slots[]; };
        layout(std430, set = 0, binding = 3) readonly buffer Zones { Zone zones[]; };
        layout(std430, set = 0, binding = 4) readonly buffer Patches { Patch patches[]; };
        layout(std430, set = 0, binding = 5) buffer Keys { uvec4 keys[]; };
        layout(std430, set = 0, binding = 8) buffer Firsts { uint firsts[]; };
        """;

    /// <summary>
    /// Kernel 1, one thread per slot (a page's blades of one patch): the decisions of <c>FoliageRenderer.PrepareGrass</c> in its operation order,
    /// <c>precise</c> (no FMA): the page box against the planes (<c>WorldCamera.Intersects</c>), the page's distance (<c>BoxDistance</c> with the
    /// correctly rounded root) against the patch's range, the density's blade prefix (<c>FoliageGrassField.PrefixCount</c>). A slot that draws takes the
    /// next place of the view's list (the draws' count, in whatever order the threads come) and writes there <c>keys[place] = (distance bits, tie,
    /// blades, patch row)</c> and its first blade; counts the draws and blades. The list holds the slots that draw and no others, so kernel 2 ranks
    /// among those alone. With the last frame's depth pyramid (<c>view.hz</c>, <c>hzOff</c> and <c>hiz</c> as <see cref="FoliageShaders"/>' cull
    /// reads them; level count 0: none) a page whose box (its square grown by half the patch's widest quad and its sway, the heights its blades span) lay wholly
    /// behind that frame's surfaces is left out, by the rule of the meshes' occlusion cull (docs/formats/foliage.md "Occlusion culling").
    /// </summary>
    public static readonly string CullCompute = Common + FoliageShaders.CrSqrt + "\n" + """
        layout(std430, set = 0, binding = 2) readonly buffer Prefixes { uint prefixes[]; };
        layout(std430, set = 0, binding = 7) buffer Counters { uint counters[]; };
        layout(std430, set = 0, binding = 9) readonly buffer Hiz { vec2 hiz[]; };
        // The texels covering the pixel rectangle r (x0, y0, x1, y1): the level where it spans at most 2 x 2 texels; false when it leaves the picture.
        bool HizRegion(vec4 r, out float least, out float most)
        {
            least = 3.0e38; most = 0.0;
            if (r.x < 0.0 || r.y < 0.0 || r.z >= view.hz[4].z || r.w >= view.hz[4].w) return false;
            int bw = int(view.hz[5].z), bh = int(view.hz[5].w), levels = int(view.hz[5].y);
            int x0 = int(r.x) >> 2, y0 = int(r.y) >> 2, x1 = min(int(r.z) >> 2, bw - 1), y1 = min(int(r.w) >> 2, bh - 1);
            int l = 0;
            while (l < levels - 1 && (((x1 >> l) - (x0 >> l)) > 1 || ((y1 >> l) - (y0 >> l)) > 1)) l++;
            int w = (bw + (1 << l) - 1) >> l;
            uint off = view.hzOff[l >> 2][l & 3];
            for (int y = y0 >> l; y <= (y1 >> l); y++)
                for (int x = x0 >> l; x <= (x1 >> l); x++)
                {
                    vec2 t = hiz[off + uint(y * w + x)];
                    least = min(least, t.x);
                    most = max(most, t.y);
                }
            return true;
        }
        // True when every ray from the eye to the box is stopped by the surfaces of the previous frame: the pixel rectangle its corners cover there, grown by a pixel
        // (the depth was drawn with a jitter) and by the parallax of the eye's step against the nearest surface in it, holds nothing but surfaces nearer than the
        // box by a margin. A box that reaches within 40 units of the eye's plane is drawn.
        bool PageOccluded(vec3 lo, vec3 hi)
        {
            if (view.hz[5].y < 0.5) return false;
            float zn = 3.0e38, tx0 = 3.0e38, tx1 = -3.0e38, ty0 = 3.0e38, ty1 = -3.0e38;
            for (int i = 0; i < 8; i++)
            {
                vec3 d = vec3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z) - view.hz[0].xyz;
                float zc = -dot(view.hz[3].xyz, d);
                if (zc < 40.0) return false;
                float tx = dot(view.hz[1].xyz, d) / zc, ty = dot(view.hz[2].xyz, d) / zc;
                zn = min(zn, zc);
                tx0 = min(tx0, tx); tx1 = max(tx1, tx);
                ty0 = min(ty0, ty); ty1 = max(ty1, ty);
            }
            vec2 size = view.hz[4].zw, tn = view.hz[4].xy;
            vec4 rect = vec4((0.5 + 0.5 * tx0 / tn.x) * size.x - 1.0, (0.5 + 0.5 * ty0 / tn.y) * size.y - 1.0,
                             (0.5 + 0.5 * tx1 / tn.x) * size.x + 1.0, (0.5 + 0.5 * ty1 / tn.y) * size.y + 1.0);
            float delta = view.hz[0].w, f = view.hz[5].x, pad = 0.0, least, most;
            for (int i = 0; i < 3; i++)
            {
                if (!HizRegion(rect + vec4(-pad, -pad, pad, pad), least, most)) return false;
                float need = delta * f / max(least, 1.0);
                if (need <= pad + 0.5) return most + 0.01 * zn + 3.0 < zn;
                pad = need;
            }
            return false;
        }
        void main()
        {
            uint s = gl_GlobalInvocationID.x;
            if (s >= view.slotCount) return;
            Slot slot = slots[s];
            if (slot.count == 0u) return;
            Zone zone = zones[slot.zone];
            if (slot.local >= zone.patchCount) return;
            uint row = zone.patchBase + slot.local;
            Patch pt = patches[row];
            if ((pt.flags & 16u) == 0u) return;
            precise float dx = max(max(slot.x0 - view.eye.x, view.eye.x - (slot.x0 + view.pageSize)), 0.0);
            precise float dz = max(max(slot.z0 - view.eye.y, view.eye.y - (slot.z0 + view.pageSize)), 0.0);
            precise float d2 = dx * dx + dz * dz;
            float d = CrSqrt(d2);
            precise float reach = pt.range;
            if (view.motionScale > 0.0) reach = min(reach, pt.sway * view.motionScale);   // the motion pass: only blades that move enough to show
            if (d >= reach) return;
            precise float minY = slot.yMin;
            precise float maxY = slot.yMax;
            precise float maxX = slot.x0 + view.pageSize;
            precise float maxZ = slot.z0 + view.pageSize;
            for (uint p = 0u; p < view.planeCount; p++)
            {
                vec4 q = view.planes[p];
                float vx = q.x >= 0.0 ? maxX : slot.x0;
                float vy = q.y >= 0.0 ? maxY : minY;
                float vz = q.z >= 0.0 ? maxZ : slot.z0;
                precise float side = q.x * vx + q.y * vy + q.z * vz + q.w;
                if (side < 0.0) return;
            }
            uint at = s * 65u + view.prefixIndex;
            int first = int(prefixes[at]);
            precise float part = float(int(prefixes[at + 1u]) - first) * view.fraction;
            int shown = first + int(part);
            if (shown == 0) return;
            // A blade stands on the page; half its widest quad and its sway reach past it.
            float past = 0.5 * pt.size.y + abs(pt.sway) + 1.0;
            if (PageOccluded(vec3(slot.x0 - past, minY, slot.z0 - past), vec3(maxX + past, maxY, maxZ + past))) return;
            uint place = atomicAdd(counters[0], 1u);
            keys[place] = uvec4(floatBitsToUint(d), slot.tie, uint(shown), row);
            firsts[place] = slot.firstBlade;
            atomicAdd(counters[1], uint(shown));
        }
        """;

    /// <summary>
    /// Kernel 2, one thread per draw of kernel 1's list (the groups past its count end at once): the draw's place in the view's order, the number of
    /// draws with a smaller (distance, tie): nearest page first, equal distances in the order of the page's creation and the patch's index (a total
    /// order, so the result depends neither on the scheduling nor on the list's order). Writes the <c>VkDrawIndirectCommand</c>: the patch's vertex
    /// count, the blades, the patch row as the first vertex and the slot's first blade as the first instance. Ranking among the draws alone: every
    /// slot used to be ranked against every slot, 0.65 ms a view whatever was in sight (docs/render-foliage.md).
    /// </summary>
    public static readonly string OrderCompute = Common + """
        layout(std430, set = 0, binding = 6) writeonly buffer Draws { Draw draws[]; };
        layout(std430, set = 0, binding = 7) readonly buffer Counters { uint counters[]; };
        shared uvec2 tile[256];
        void main()
        {
            uint count = counters[0];
            if (gl_WorkGroupID.x * 256u >= count) return;
            uint s = gl_GlobalInvocationID.x;
            uint t = gl_LocalInvocationID.x;
            bool valid = s < count;
            uvec4 mine = valid ? keys[s] : uvec4(0xFFFFFFFFu);
            uint rank = 0u;
            for (uint b = 0u; b < count; b += 256u)
            {
                uint j = b + t;
                tile[t] = j < count ? keys[j].xy : uvec2(0xFFFFFFFFu);
                barrier();
                if (valid)
                    for (uint i = 0u; i < 256u; i++)
                    {
                        uvec2 k = tile[i];
                        if (k.x < mine.x || (k.x == mine.x && k.y < mine.y)) rank++;
                    }
                barrier();
            }
            if (!valid) return;
            draws[rank] = Draw(patches[mine.w].vertexCount, mine.z, mine.w << 4, firsts[s]);
        }
        """;

    // ---- the blade programs reading their patch row ----

    /// <summary>
    /// The push block the GPU blade programs declare (they read no push constant; a block must have a member).
    /// </summary>
    const string PushMembers = "            uint spare;";

    /// <summary>
    /// Set 0, binding <see cref="Binding"/>: the patch rows of the view (<see cref="GrassPatchRow"/>, std430).
    /// </summary>
    public const int Binding = 6;

    const string PatchBlock = """
        struct GrassPatch { vec4 size; vec4 colourBounds; float sway; float range; float frequency; uint flags; uint sprite; uint colourMap; uint nearDepth; uint vertexCount; };
        layout(std430, set = 0, binding = 6) readonly buffer GrassPatches { GrassPatch rows[]; } grassPatches;

        """;

    static string Row(string member) => $"grassPatches.rows[GRASS_ROW].{member}";
    static string Flag(uint bit) => $"((grassPatches.rows[GRASS_ROW].flags & {bit}u) != 0u)";
    static string Tex(string member) => $"textures2D[nonuniformEXT(grassPatches.rows[GRASS_ROW].{member})]";

    static readonly Dictionary<string, string> Map = new()
    {
        ["uSize"] = Row("size"),
        ["uColourBounds"] = Row("colourBounds"),
        ["uSway"] = Row("sway"),
        ["uRange"] = Row("range"),
        ["uFrequency"] = Row("frequency"),
        ["uCross"] = Flag(FlagCross),
        ["uHasColourMap"] = Flag(FlagColourMap),
        ["uCoverage"] = Flag(FlagCoverage),
        ["uWireframe"] = Flag(FlagWireframe),
        ["uSprite"] = Tex("sprite"),
        ["uColourMap"] = Tex("colourMap"),
        ["uNearDepth"] = Tex("nearDepth"),
    };

    const string PreludeEnd = "#define gl_VertexID gl_VertexIndex\n";

    /// <summary>
    /// A vertex program ported with the patch row taken from <c>gl_VertexIndex &gt;&gt; 4</c> (the draw's first vertex) and passed on, flat.
    /// </summary>
    static string Vertex(string legacy)
    {
        string text = NativeShaders.Port(legacy, NativeShaders.Map(Map), PushMembers);
        if (!text.Contains(PreludeEnd))
            throw new InvalidOperationException("FoliageGrassShaders: the native prelude changed");
        text = text.Replace(PreludeEnd,
            $"#define gl_VertexID (gl_VertexIndex & 15)\n#define GRASS_ROW (uint(gl_VertexIndex) >> {RowShift})\nflat out uint vRow;\n{PatchBlock}");
        var main = MainRegex().Match(text);
        if (!main.Success)
            throw new InvalidOperationException("FoliageGrassShaders: no main() in the blade vertex program");
        return text.Insert(main.Index + main.Length, "\n    vRow = GRASS_ROW;");
    }

    static string Fragment(string legacy)
    {
        string text = NativeShaders.Port(legacy, NativeShaders.Map(Map), PushMembers);
        if (!text.Contains(PreludeEnd))
            throw new InvalidOperationException("FoliageGrassShaders: the native prelude changed");
        return text.Replace(PreludeEnd, $"{PreludeEnd}flat in uint vRow;\n#define GRASS_ROW vRow\n{PatchBlock}");
    }

    public static string GrassVertexGpu() => Vertex(FoliageShaders.GrassVertex);
    /// <summary>
    /// The blades' colour program, with the interior cut and the GI resolve's surface output, as <see cref="FoliageShaders.GrassFragmentNative"/>.
    /// </summary>
    public static string GrassFragmentGpu() => Gi.GiResolveShaders.WithAlbedo(Fragment(InteriorMaskRenderer.WithCut(FoliageShaders.GrassFragment)),
        inShader: true);
    public static string GrassMotionVertexGpu() => Vertex(FoliageShaders.GrassMotionVertex);
    public static string GrassMotionFragmentGpu() => Fragment(FoliageShaders.GrassMotionFragment);

    [GeneratedRegex(@"void\s+main\s*\(\s*\)\s*\{")]
    private static partial Regex MainRegex();
}

/// <summary>
/// One slot of the slot table (std430, 36 bytes): a page's blades of one patch. <see cref="X0"/>, <see cref="Z0"/> are the page's corner, <see cref="YMin"/>
/// and <see cref="YMax"/> the heights its blades span (the lowest root to the highest tip), as <c>PrepareGrass</c> reads them from the page; <see cref="FirstBlade"/> the index of its first blade in the
/// arena (the draw's first instance); <see cref="Zone"/> and <see cref="Local"/> reach its patch (the zone's patches change when a zone is laid out
/// again); <see cref="Tie"/> orders pages of equal distance (creation order, then patch index).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GrassSlot
{
    public float X0, Z0, YMin, YMax;
    public uint FirstBlade, Count, Zone, Local, Tie;

    public const int Size = 36;
}

/// <summary>
/// A zone's patch rows in the view's patch table (std430, 8 bytes): the first row and the number of patches the zone has now.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GrassZoneRow
{
    public uint PatchBase, PatchCount;
}

/// <summary>
/// A patch of the view's patch table (std430, 64 bytes): what <see cref="GrassPush"/> holds per draw, read by the blade programs and, for
/// <see cref="Range"/>, <see cref="Flags"/> and <see cref="VertexCount"/>, by the cull. Flags: see <see cref="FoliageGrassShaders.FlagCross"/> and
/// following; <see cref="FoliageGrassShaders.FlagActive"/> when the patch's sprite is loaded (and, for the motion pass, the patch sways).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GrassPatchRow
{
    public Vector4 Size, ColourBounds;
    public float Sway, Range, Frequency;
    public uint Flags, Sprite, ColourMap, NearDepth, VertexCount;

    public const int Size64 = 64;
}