using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Meitou.Rendering;

/// <summary>
/// The GPU grass (docs/renderer-native.md 5.4 and 5.6.2): two compute kernels that decide, per view, which (page, patch) draws exist, how
/// many blades each shows and in which order (nearest page first), and the native variants of the blade programs that read their per-draw
/// values (<see cref="GrassPatchRow"/>) from a storage buffer instead of push constants, so every visible draw of a view is one entry of a
/// single indirect draw.
/// </summary>
public static partial class FoliageGrassShaders
{
    /// <summary>The workgroup size of both kernels (one thread per slot).</summary>
    public const int Group = 256;

    /// <summary>Bit flags of <see cref="GrassPatchRow.Flags"/>.</summary>
    public const uint FlagCross = 1, FlagColourMap = 2, FlagCoverage = 4, FlagWireframe = 8, FlagActive = 16;

    /// <summary>The blades of a draw sit 16 vertices apart in the patch table: <c>firstVertex = row * 16</c>, <c>gl_VertexIndex &gt;&gt; 4</c> is the row.</summary>
    public const int RowShift = 4;

    const string Common = """
        #version 450
        layout(local_size_x = 256) in;
        struct Slot { float x0; float z0; float y; uint firstBlade; uint count; uint zone; uint local; uint tie; };
        struct Zone { uint patchBase; uint patchCount; };
        struct Patch { vec4 size; vec4 colourBounds; float sway; float range; float frequency; uint flags; uint sprite; uint colourMap; uint nearDepth; uint vertexCount; };
        struct Draw { uint vertexCount; uint instanceCount; uint firstVertex; uint firstInstance; };
        layout(std430, set = 0, binding = 0) readonly buffer View { vec4 planes[8]; vec2 eye; uint planeCount; uint slotCount; float pageSize; float fraction; uint prefixIndex; float motionScale; } view;
        layout(std430, set = 0, binding = 1) readonly buffer Slots { Slot slots[]; };
        layout(std430, set = 0, binding = 3) readonly buffer Zones { Zone zones[]; };
        layout(std430, set = 0, binding = 4) readonly buffer Patches { Patch patches[]; };
        layout(std430, set = 0, binding = 5) buffer Keys { uvec4 keys[]; };
        """;

    /// <summary>
    /// Kernel 1, one thread per slot (a page's blades of one patch): the decisions of <c>FoliageRenderer.PrepareGrass</c> in its operation
    /// order, <c>precise</c> (no FMA): the page box against the planes (<c>WorldCamera.Intersects</c>), the page's distance
    /// (<c>BoxDistance</c> with the correctly rounded root) against the patch's range, the density's blade prefix (<c>FoliageGrassField.PrefixCount</c>).
    /// Writes <c>keys[s] = (distance bits, tie, blades, patch row)</c>, or distance bits <c>0xFFFFFFFF</c> for no draw; counts the draws and blades.
    /// </summary>
    public static readonly string CullCompute = Common + FoliageShaders.CrSqrt + """
        layout(std430, set = 0, binding = 2) readonly buffer Prefixes { uint prefixes[]; };
        layout(std430, set = 0, binding = 7) buffer Counters { uint counters[]; };
        void main()
        {
            uint s = gl_GlobalInvocationID.x;
            if (s >= view.slotCount) return;
            keys[s] = uvec4(0xFFFFFFFFu, 0u, 0u, 0u);
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
            precise float minY = slot.y - 2000.0;
            precise float maxY = slot.y + 2000.0;
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
            keys[s] = uvec4(floatBitsToUint(d), slot.tie, uint(shown), row);
            atomicAdd(counters[0], 1u);
            atomicAdd(counters[1], uint(shown));
        }
        """;

    /// <summary>
    /// Kernel 2, one thread per slot: the draw's place in the view's list, the number of draws with a smaller (distance, tie): nearest page
    /// first, equal distances in the order of the page's creation and the patch's index (a total order, so the result does not depend on the
    /// scheduling). Writes the <c>VkDrawIndirectCommand</c>: the patch's vertex count, the blades, the patch row as the first vertex and
    /// the slot's first blade as the first instance.
    /// </summary>
    public static readonly string OrderCompute = Common + """
        layout(std430, set = 0, binding = 6) writeonly buffer Draws { Draw draws[]; };
        shared uvec2 tile[256];
        void main()
        {
            uint s = gl_GlobalInvocationID.x;
            uint t = gl_LocalInvocationID.x;
            uvec4 mine = s < view.slotCount ? keys[s] : uvec4(0xFFFFFFFFu);
            bool valid = mine.x != 0xFFFFFFFFu;
            uint rank = 0u;
            for (uint b = 0u; b < view.slotCount; b += 256u)
            {
                uint j = b + t;
                tile[t] = j < view.slotCount ? keys[j].xy : uvec2(0xFFFFFFFFu);
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
            draws[rank] = Draw(patches[mine.w].vertexCount, mine.z, mine.w << 4, slots[s].firstBlade);
        }
        """;

    // ---- the blade programs reading their patch row ----

    /// <summary>The push block the GPU blade programs declare (they read no push constant; a block must have a member).</summary>
    const string PushMembers = "            uint spare;";

    /// <summary>Set 0, binding <see cref="Binding"/>: the patch rows of the view (<see cref="GrassPatchRow"/>, std430).</summary>
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
        ["uSize"] = Row("size"), ["uColourBounds"] = Row("colourBounds"), ["uSway"] = Row("sway"), ["uRange"] = Row("range"), ["uFrequency"] = Row("frequency"),
        ["uCross"] = Flag(FlagCross), ["uHasColourMap"] = Flag(FlagColourMap), ["uCoverage"] = Flag(FlagCoverage), ["uWireframe"] = Flag(FlagWireframe),
        ["uSprite"] = Tex("sprite"), ["uColourMap"] = Tex("colourMap"), ["uNearDepth"] = Tex("nearDepth"),
    };

    const string PreludeEnd = "#define gl_VertexID gl_VertexIndex\n";

    /// <summary>A vertex program ported with the patch row taken from <c>gl_VertexIndex &gt;&gt; 4</c> (the draw's first vertex) and passed on, flat.</summary>
    static string Vertex(string legacy)
    {
        string text = NativeShaders.Port(legacy, NativeShaders.Map(Map), PushMembers);
        if (!text.Contains(PreludeEnd)) throw new InvalidOperationException("FoliageGrassShaders: the native prelude changed");
        text = text.Replace(PreludeEnd, $"#define gl_VertexID (gl_VertexIndex & 15)\n#define GRASS_ROW (uint(gl_VertexIndex) >> {RowShift})\nflat out uint vRow;\n{PatchBlock}");
        var main = MainRegex().Match(text);
        if (!main.Success) throw new InvalidOperationException("FoliageGrassShaders: no main() in the blade vertex program");
        return text.Insert(main.Index + main.Length, "\n    vRow = GRASS_ROW;");
    }

    static string Fragment(string legacy)
    {
        string text = NativeShaders.Port(legacy, NativeShaders.Map(Map), PushMembers);
        if (!text.Contains(PreludeEnd)) throw new InvalidOperationException("FoliageGrassShaders: the native prelude changed");
        return text.Replace(PreludeEnd, $"{PreludeEnd}flat in uint vRow;\n#define GRASS_ROW vRow\n{PatchBlock}");
    }

    public static string GrassVertexGpu() => Vertex(FoliageShaders.GrassVertex);
    public static string GrassFragmentGpu() => Fragment(FoliageShaders.GrassFragment);
    public static string GrassMotionVertexGpu() => Vertex(FoliageShaders.GrassMotionVertex);
    public static string GrassMotionFragmentGpu() => Fragment(FoliageShaders.GrassMotionFragment);

    [GeneratedRegex(@"void\s+main\s*\(\s*\)\s*\{")]
    private static partial Regex MainRegex();
}

/// <summary>One slot of the slot table (std430, 32 bytes): a page's blades of one patch. <see cref="X0"/>, <see cref="Z0"/> and <see cref="Y"/> are
/// the page's corner and the ground height at its centre as <c>PrepareGrass</c> computes them; <see cref="FirstBlade"/> the index of its first blade in the
/// arena (the draw's first instance); <see cref="Zone"/> and <see cref="Local"/> reach its patch (the zone's patches change when a zone is laid out again);
/// <see cref="Tie"/> orders pages of equal distance (creation order, then patch index).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GrassSlot
{
    public float X0, Z0, Y;
    public uint FirstBlade, Count, Zone, Local, Tie;

    public const int Size = 32;
}

/// <summary>A zone's patch rows in the view's patch table (std430, 8 bytes): the first row and the number of patches the zone has now.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GrassZoneRow
{
    public uint PatchBase, PatchCount;
}

/// <summary>A patch of the view's patch table (std430, 64 bytes): what <see cref="GrassPush"/> holds per draw, read by the blade programs and, for
/// <see cref="Range"/>, <see cref="Flags"/> and <see cref="VertexCount"/>, by the cull. Flags: see <see cref="FoliageGrassShaders.FlagCross"/> and following;
/// <see cref="FoliageGrassShaders.FlagActive"/> when the patch's sprite is loaded (and, for the motion pass, the patch sways).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GrassPatchRow
{
    public Vector4 Size, ColourBounds;
    public float Sway, Range, Frequency;
    public uint Flags, Sprite, ColourMap, NearDepth, VertexCount;

    public const int Size64 = 64;
}
