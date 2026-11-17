using System.Text;
using System.Text.RegularExpressions;
using Meitou.Game.Rendering.Frame;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The programs of the characters' visibility buffer (<see cref="CharacterVisibility"/>, docs/character-renderer.md "Visibility buffer"): the id
/// pass's fragment, and the resolve made from the colour pass's own text. The resolve runs the vertex program (<see cref="CharacterShaders.VertexBody"/>)
/// for the three corners of each pixel's triangle, interpolates its outputs with the barycentrics the id pass kept, and runs the colour pass's
/// <c>main</c> (<see cref="CharacterShaders.FragmentBody"/>) with its macros taking the derivatives from the triangle instead of the quad.
/// </summary>
static partial class CharacterVisibilityShaders
{
    #region Layout

    /// <summary>
    /// The resolve's storage buffers after the colour pass's four (palettes, materials, morphs, last frame's palettes): the instance rows
    /// (<see cref="CharInstance"/>, 9 uvec4 a row) and each row's mesh (uvec4: the vertices' and the level's first index's device addresses).
    /// </summary>
    public const uint RowsBinding = 10, MeshesBinding = 11;

    /// <summary>
    /// The id target's second word: the primitive below bit <see cref="RateShift"/>, the shading rate (<c>gl_ShadingRateEXT</c>) from it, front
    /// facing in bit 31.
    /// </summary>
    public const int RateShift = 27;

    /// <summary>
    /// The resolve's push block: the colour pass's, then the id and slope targets' bindless indices.
    /// </summary>
    internal const string PushMembers = CharacterShaders.PushMembers + "\n" + """
            uint visIds;        // the id target (UintTexture2D): x the instance row + 1 (0: no character), y primitive | rate | facing, zw barycentrics 1 and 2
            uint visSlopes;     // the slopes target: the barycentrics' fine screen derivatives (dFdx, dFdy), xy along x, zw along y
            uint visCoarse;     // the coarse ones (the quad's, which the implicit lookups take)
        """;

    #endregion

    #region Id pass

    /// <summary>
    /// The id pass's fragment program, after the colour pass's vertex program with the row (<see cref="CharacterShaders.IdVertex"/>): the colour
    /// pass's discards (hidden vertices, a cut-out still streaming in, the hair cards' and cut-out items' alpha, at the same lookups), then the row,
    /// the primitive, the rate and facing, and the hardware's perspective-correct barycentrics with their derivatives (taken before the cut's
    /// discard, while the whole quad runs). <paramref name="rate"/>: the rendering has a shading rate attachment.
    /// </summary>
    public static string IdFragment(bool rate) => "#version 450\n#extension GL_EXT_fragment_shader_barycentric : require\n"
        + (rate ? "#extension GL_EXT_fragment_shading_rate : require\n" : "") + NativeShaders.Prelude(CharacterShaders.PushMembers)
        + CharacterShaders.Structs + CharacterShaders.Lookup + $$"""
        in vec2 vUv;
        flat in uint vMaterial;
        in float vHidden;
        flat in uint vRow;
        layout(location = 0) out uvec4 oVisible;
        layout(location = 1) out vec4 oSlopes;
        layout(location = 2) out vec4 oCoarse;

        void main()
        {
            if (vHidden > 0.0) discard;
            CharMaterial m = materials.items[vMaterial];
            uint f = m.flags;
            if ((f & {{CharacterShaders.AlphaPending}}u) != 0u) discard;
            bool cut = false;
            if (m.shading == {{CharacterShaders.ShadeHair}}u) cut = (f & {{CharacterShaders.HasDiffuse}}u) != 0u && dot(hairTex(m, vUv), m.alphaChannel) < m.alphaThreshold;
            else if (m.shading == {{CharacterShaders.ShadeItem}}u && (f & {{CharacterShaders.ClipNormalAlpha}}u) != 0u)
                cut = tex(m.tex[{{CharacterShaders.SlotNormal}}], vUv).a < (view.fogDistance < 0.0 ? 0.5 : 0.6);
            vec2 b = gl_BaryCoordEXT.yz;
            vec4 slopes = vec4(dFdxFine(b), dFdyFine(b)), coarse = vec4(dFdxCoarse(b), dFdyCoarse(b));
            if (cut) discard;
            uint rate = {{(rate ? "uint(gl_ShadingRateEXT) & 15u" : "0u")}};
            oVisible = uvec4(vRow + 1u, uint(gl_PrimitiveID) | (rate << {{RateShift}}) | (gl_FrontFacing ? 0x80000000u : 0u), floatBitsToUint(b));
            oSlopes = slopes;
            oCoarse = coarse;
        }
        """;

    #endregion

    #region Resolve

    /// <summary>
    /// The resolve's vertex program: one triangle over the target.
    /// </summary>
    public static string ResolveVertex() => "#version 450\n" + NativeShaders.Prelude(PushMembers)
        + "void main() { gl_Position = vec4(vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0, 0.0, 1.0); }\n";

    /// <summary>
    /// The resolve's fragment program (before <c>GiResolveShaders.WithAlbedo</c>, which adds the surface and sun term outputs as for the colour pass).
    /// </summary>
    public static string ResolveFragment()
    {
        var varyings = new List<Varying>();
        string vertex = VertexCode(varyings);
        string body = CharacterShaders.FragmentBody(false);
        body = InRegex().Replace(body, "$1$2");
        body = Require(body, "dFdx(vBind)", "vBindDx");
        body = Require(body, "dFdy(vBind)", "vBindDy");
        body = Require(body, SkinEntry, SkinEntryResolved);

        int mainAt = body.IndexOf("void main()", StringComparison.Ordinal);
        if (mainAt < 0) throw new InvalidOperationException("CharacterVisibilityShaders: the colour fragment has no main");
        string functions = body[..mainAt], main = body[mainAt..];
        string lighting = ResolveLighting(CharacterSurface.Lighting());

        string text = "#version 450\n#extension GL_EXT_buffer_reference : require\n#extension GL_EXT_buffer_reference_uvec2 : require\n"
            + "#extension GL_EXT_scalar_block_layout : require\n" + NativeShaders.Prelude(PushMembers) + "#define LAMP_SKIN\n"
            + Globals(varyings) + CharacterSurface.Prototypes + lighting + CharacterShaders.Structs + CharacterShaders.Lookup + ResolveLookup
            + ResolveMacros + functions
            + "#define PROBING false\n" + main.Replace("void main()", "void vbShade()", StringComparison.Ordinal)
            + "\n#undef PROBING\n#define PROBING true\n" + main.Replace("void main()", "void vbProbe()", StringComparison.Ordinal) + "\n";
        string reset = ResetGlobals(CharacterSurface.Prototypes + lighting + functions);
        text = text.Replace("gl_FragCoord", "vbFragCoord", StringComparison.Ordinal);

        return text + vertex + Corners(varyings) + reset + ResolveMain;
    }

    /// <summary>
    /// One output of the vertex program: whether it is flat, its type and name.
    /// </summary>
    readonly record struct Varying(bool Flat, string Type, string Name);

    /// <summary>
    /// The vertex program as functions of the resolve: its inputs and outputs plain globals (the outputs are the colour pass's inputs, declared by its
    /// text), <c>main</c> as <c>vbVertex</c> writing <c>vbPosition</c>. Fills <paramref name="varyings"/> with the outputs.
    /// </summary>
    static string VertexCode(List<Varying> varyings)
    {
        string body = CharacterShaders.VertexBody(false, false);
        foreach (Match m in OutRegex().Matches(body))
            varyings.Add(new Varying(m.Groups[1].Success, m.Groups[2].Value, m.Groups[3].Value));
        if (varyings.Count == 0) throw new InvalidOperationException("CharacterVisibilityShaders: the vertex program has no outputs");

        body = OutRegex().Replace(body, "");
        body = LocationRegex().Replace(body, "");
        body = Require(body, "void main()", "void vbVertex()");
        return "vec4 vbPosition;\n" + body.Replace("gl_Position", "vbPosition", StringComparison.Ordinal) + "\n";
    }

    /// <summary>
    /// The resolve's globals, ahead of everything that reads them: the facing and fragment position the id pass kept, the quad's directions (+1: the
    /// neighbour is the next fragment), the derivatives the colour pass took from its quad (each smooth output's, the geometric normal's, the shadow
    /// point's) and the neighbours' blood film from the probes.
    /// </summary>
    static string Globals(List<Varying> varyings)
    {
        var s = new StringBuilder("""
            bool vbFrontFacing = true;
            vec4 vbFragCoord = vec4(0.5, 0.5, 0.0, 1.0);
            float vbSx = 1.0, vbSy = 1.0;
            vec3 vbGeometricDx = vec3(0.0), vbGeometricDy = vec3(0.0), vbShadowDx = vec3(0.0), vbShadowDy = vec3(0.0);
            float vbLiesX = 0.0, vbLiesY = 0.0, vbRiseX = 0.0, vbRiseY = 0.0, vbProbeLies = 0.0, vbProbeRise = 0.0;
            bool vbFilmQuad = false, vbRiseQuad = false;   // every lane of the quad reaches the blood film's derivatives (its edge, its slope)
            bool vbBloodQuad = true;                       // every lane of the quad looks the worn item's splatter up (its part bleeds there)
            float vbChord(float sx, float sy, vec3 l);

            // A derivative the colour pass takes inside a branch: the difference to the quad partner along s when all four lanes of the quad took the
            // branch, else none (NVIDIA's quad derivatives in divergent flow are 0 while a lane of the quad is out: Observed, docs/character-renderer.md).
            float vbPartner(bool quad, float neighbour, float self, float s) { return quad ? s * (neighbour - self) : 0.0; }
            // Whether a smooth output is above 0 at all four lanes of the pixel's quad (the branch it opens is taken by the whole quad).
            bool vbQuad(float v, float dx, float dy)
            {
                return v > 0.0 && v + vbSx * dx > 0.0 && v + vbSy * dy > 0.0 && v + vbSx * dx + vbSy * dy > 0.0;
            }

            """);
        foreach (var v in varyings)
            if (!v.Flat) s.Append(CultureInvariant($"{v.Type} {v.Name}Dx, {v.Name}Dy, {v.Name}Gx, {v.Name}Gy;\n"));
        return s.ToString();
    }

    /// <summary>
    /// The functions that gather a pixel's outputs from its triangle's corners: <c>vbBegin</c> clears the sums, <c>vbCorner</c> adds the corner the
    /// vertex program just ran for (its barycentric and its derivatives; a flat output is the first corner's, the provoking vertex),
    /// <c>vbFinish</c> sets the pixel's, <c>vbShift</c> its neighbour's along the quad's directions.
    /// </summary>
    static string Corners(List<Varying> varyings)
    {
        var decls = new StringBuilder();
        var begin = new StringBuilder("void vbBegin()\n{\n");
        var corner = new StringBuilder("void vbCorner(bool first, float b, float dx, float dy, float gx, float gy)\n{\n");
        var finish = new StringBuilder("void vbFinish()\n{\n");
        var shift = new StringBuilder("void vbShift(float sx, float sy)\n{\n");
        foreach (var (flat, type, name) in varyings)
        {
            decls.Append(CultureInvariant($"{type} vbAt_{name};\n"));
            finish.Append(CultureInvariant($"    {name} = vbAt_{name};\n"));
            if (flat)
            {
                corner.Append(CultureInvariant($"    if (first) vbAt_{name} = {name};\n"));
                continue;
            }

            // The first corner's value, then the others' differences to it times their barycentrics and slopes.
            decls.Append(CultureInvariant($"{type} vbFirst_{name};\n"));
            begin.Append(CultureInvariant($"    {name}Dx = {type}(0.0); {name}Dy = {type}(0.0); {name}Gx = {type}(0.0); {name}Gy = {type}(0.0);\n"));
            corner.Append(CultureInvariant($"    if (first) {{ vbFirst_{name} = {name}; vbAt_{name} = {name} * b; }}\n"));
            corner.Append(CultureInvariant($"    else {{ {type} e = {name} - vbFirst_{name}; vbAt_{name} += {name} * b; {name}Dx += e * dx; {name}Dy += e * dy; {name}Gx += e * gx; {name}Gy += e * gy; }}\n"));
            shift.Append(CultureInvariant($"    {name} = vbAt_{name} + sx * {name}Dx + sy * {name}Dy;\n"));
        }

        return decls + begin.Append("}\n").ToString() + corner.Append("}\n") + finish.Append("}\n") + shift.Append("}\n");
    }

    /// <summary>
    /// <c>vbResetGlobals</c>: every global of <paramref name="text"/> declared with a value set back to it, after the probes ran the colour pass's
    /// <c>main</c> at the neighbours.
    /// </summary>
    static string ResetGlobals(string text)
    {
        var s = new StringBuilder("void vbResetGlobals()\n{\n");
        int depth = 0;
        foreach (string raw in text.Split('\n'))
        {
            int comment = raw.IndexOf("//", StringComparison.Ordinal);
            string line = (comment >= 0 ? raw[..comment] : raw).Trim();
            if (depth == 0 && !line.StartsWith('#') && GlobalRegex().Match(line) is { Success: true } m)
                foreach (string declarator in SplitTopLevel(m.Groups[2].Value))
                    s.Append("    ").Append(declarator.Trim()).Append(";\n");
            depth += line.Count(c => c == '{') - line.Count(c => c == '}');
        }

        return s.Append("}\n").ToString();
    }

    /// <summary>
    /// <paramref name="list"/> split at the commas outside parentheses.
    /// </summary>
    static IEnumerable<string> SplitTopLevel(string list)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] == '(') depth++;
            else if (list[i] == ')') depth--;
            else if (list[i] == ',' && depth == 0)
            {
                yield return list[start..i];
                start = i + 1;
            }
        }

        yield return list[start..];
    }

    /// <summary>
    /// The atmosphere text for the resolve: the shadow's own plane from the triangle (<c>vbShadowDx</c>, the shadow point's derivatives) and the
    /// ambient map's footprint from the fragment's (its one caller passes <c>vWorld</c>).
    /// </summary>
    static string ResolveLighting(string lighting)
    {
        // Both places that take the plane: kenshiLight's (msPlane, where every lane of the quad is) and the lookup's own fallback.
        lighting = Require(lighting, "cross(dFdx(world), dFdy(world))", "cross(vbShadowDx, vbShadowDy)");
        return Require(lighting, "texture(uAtmoAmbientMap, (world.xz + uAtmoMaps.w) / (2.0 * uAtmoMaps.w))",
            "textureGrad(uAtmoAmbientMap, (world.xz + uAtmoMaps.w) / (2.0 * uAtmoMaps.w), vWorldDx.xz / (2.0 * uAtmoMaps.w), vWorldDy.xz / (2.0 * uAtmoMaps.w))");
    }

    /// <summary>
    /// The skin's sun beyond kenshiLight (CharacterSurface skinSunExtra) asks the shadow at a point the chord moves: the line that does.
    /// </summary>
    const string SkinEntry = "float entry = kenshiShadow(vWorld + l * (min(chord, 1.0) + 0.1), l);";

    /// <summary>
    /// <see cref="SkinEntry"/> in the resolve: the shadow point's derivatives are the fragment's plus the light times the chord's, the chord taken at
    /// the quad's neighbours as their lanes take it (<c>vbChord</c>).
    /// </summary>
    const string SkinEntryResolved = """
        vbShadowDx = vWorldDx + l * (vbSx * (min(vbChord(vbSx, 0.0, l), 1.0) - min(chord, 1.0)));
                vbShadowDy = vWorldDy + l * (vbSy * (min(vbChord(0.0, vbSy, l), 1.0) - min(chord, 1.0)));
                float entry = kenshiShadow(vWorld + l * (min(chord, 1.0) + 0.1), l);
                vbShadowDx = vWorldDx;
                vbShadowDy = vWorldDy;
        """;

    /// <summary>
    /// The lookups with the derivatives given. A gradient lookup takes no bias: <c>hairTexGrad</c> widens the derivatives by the attachment's
    /// mipmap bias instead (the same level). A second entry of the texture with the bias in its sampler was freed under the records that held it
    /// (the cache keeps two entries a texture), and the hair drew black.
    /// </summary>
    static readonly string ResolveLookup = "\n" + $$"""
        vec4 texGrad(uint i, vec2 uv, vec2 dx, vec2 dy) { return textureGrad(textures2D[nonuniformEXT(i)], uv, dx, dy); }
        vec4 hairTexGrad(CharMaterial m, vec2 uv, vec2 dx, vec2 dy) { float wider = exp2(m.hairColour.w); return textureGrad(textures2D[nonuniformEXT(m.tex[{{CharacterShaders.SlotDiffuse}}])], uv, dx * wider, dy * wider); }

        """;

    /// <summary>
    /// What the colour pass's macros (<c>CharacterShaders.ForwardMacros</c>) are in the resolve: the lookups' derivatives and the derivatives
    /// themselves from the triangle, no discards (the id pass made them), the facing the id pass kept, and the probes' exits (<c>PROBING</c>: in
    /// <c>vbProbe</c>, which ends once the blood film's coverage and height are known).
    /// </summary>
    const string ResolveMacros = "\n" + """
        #define TEXG(i, uv, dx, dy) texGrad(i, uv, dx, dy)
        #define HAIRTEXG(m, uv, dx, dy) hairTexGrad(m, uv, dx, dy)
        #define DFDX(e, d) (d)
        #define DFDY(e, d) (d)
        #define FWIDTH(e, d) (d)
        #define DISCARD {}
        #define FRONT_FACING vbFrontFacing
        #define BLOOD_DX(sx, sy) (vbSx * (BLOOD_UV(vBlood + vbSx * vBloodGx, sx, sy) - BLOOD_UV(vBlood, sx, sy)))
        #define BLOOD_DY(sx, sy) (vbSy * (BLOOD_UV(vBlood + vbSy * vBloodGy, sx, sy) - BLOOD_UV(vBlood, sx, sy)))
        #define PROBE_BARE if (PROBING && !(gearCover > 0.0)) { vbProbeLies = 0.0; vbProbeRise = 0.0; return; }
        #define PROBE_FILM(lies) if (PROBING) { vbProbeLies = (lies); vbProbeRise = gearRise; return; }
        #define PROBE_END if (PROBING) return;

        """;

    /// <summary>
    /// The resolve's entry: the pixel's id (none: discarded), its corners fetched by address and run through the vertex program, the outputs
    /// interpolated, the derivatives the colour pass would take, the probes where a blood film needs its neighbours, then the colour pass's
    /// <c>main</c>. The fragment position and the quad are the shading rate's (a coarse fragment's centre, its neighbours a fragment away).
    /// </summary>
    static readonly string ResolveMain = $$"""
        layout(std430, set = 0, binding = {{RowsBinding}}) readonly buffer VisibleRows { uvec4 words[]; } visibleRows;
        layout(std430, set = 0, binding = {{MeshesBinding}}) readonly buffer VisibleMeshes { uvec4 meshes[]; } visibleMeshes;
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer VisFloat2 { vec2 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer VisFloat3 { vec3 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer VisFloat4 { vec4 v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer VisWord { uint v; };
        layout(buffer_reference, scalar, buffer_reference_align = 4) readonly buffer VisWords { uvec2 v; };
        uvec2 vbOffset(uvec2 a, uint bytes) { uint lo = a.x + bytes; return uvec2(lo, a.y + (lo < a.x ? 1u : 0u)); }

        // A corner of the row's triangle as the vertex program's inputs: the mesh's vertex (GpuVertex's character layout, as the vertex input decodes it) and
        // the instance row.
        void vbFetch(uint row, uint prim, uint corner)
        {
            uvec4 mesh = visibleMeshes.meshes[row];
            uint index = VisWord(vbOffset(mesh.zw, (prim * 3u + corner) * 4u)).v;
            uvec2 at = vbOffset(mesh.xy, index * {{GpuVertex.CharacterSize}}u);
            aPosition = VisFloat3(at).v;
            uvec2 normal = VisWords(vbOffset(at, 12u)).v;
            aNormal = vec3(unpackSnorm2x16(normal.x), unpackSnorm2x16(normal.y).x);
            aUv = VisFloat2(vbOffset(at, 20u)).v;
            uvec2 tangent = VisWords(vbOffset(at, 28u)).v;
            aTangent = vec4(unpackSnorm2x16(tangent.x), unpackSnorm2x16(tangent.y));
            aColour = VisFloat4(vbOffset(at, 36u)).v;
            uint bones = VisWord(vbOffset(at, 52u)).v;
            aBones = uvec4(bones & 255u, (bones >> 8u) & 255u, (bones >> 16u) & 255u, bones >> 24u);
            uvec2 weights = VisWords(vbOffset(at, 56u)).v;
            aWeights = vec4(unpackUnorm2x16(weights.x), unpackUnorm2x16(weights.y));
            uint base = row * {{CharInstance.Size / 16}}u;
            aInstance0 = uintBitsToFloat(visibleRows.words[base]);
            aInstance1 = uintBitsToFloat(visibleRows.words[base + 1u]);
            aInstance2 = uintBitsToFloat(visibleRows.words[base + 2u]);
            aInstance3 = uintBitsToFloat(visibleRows.words[base + 3u]);
            aInstanceData = visibleRows.words[base + 4u];
        }

        // The skin's chord (CharacterSurface skinSunExtra) at a neighbour of the quad: its thickness, curvature and smooth normal there.
        float vbChord(float sx, float sy, vec3 l)
        {
            float side = dot(skinGeometric, normalize(vNormal)) < 0.0 ? -1.0 : 1.0;
            vec3 geometric = side * normalize(vNormal + sx * vNormalDx + sy * vNormalDy);
            float thickness = vThickness + sx * vThicknessDx + sy * vThicknessDy;
            float curvature = vCurvature + sx * vCurvatureDx + sy * vCurvatureDy;
            return min(thickness, 2.0 * max(-dot(geometric, l), 0.0) / max(curvature, 0.25));
        }

        // The blood film's coverage and height at a neighbour of the quad (the colour pass's main up to them, vbProbe), the globals it set put back.
        void vbProbeAt(float sx, float sy, vec2 step, out float lies, out float rise)
        {
            vbShift(sx, sy);
            vec4 kept = vbFragCoord;
            vbFragCoord.xy += vec2(sx, sy) * step;
            vbProbeLies = 0.0;
            vbProbeRise = 0.0;
            vbProbe();
            lies = vbProbeLies;
            rise = vbProbeRise;
            vbFragCoord = kept;
            vbResetGlobals();
        }

        void main()
        {
            ivec2 px = ivec2(gl_FragCoord.xy);
            uvec4 id = texelFetch(utextures2D[pc.visIds], px, 0);
            // The return matters: a discard compiled as a demote (ShaderCompileOptions.DemoteDiscard) runs on, and row -1 has no mesh.
            if (id.x == 0u) { discard; return; }
            vec4 slopes = texelFetch(textures2D[pc.visSlopes], px, 0), coarse = texelFetch(textures2D[pc.visCoarse], px, 0);
            uint row = id.x - 1u, prim = id.y & {{(1u << RateShift) - 1}}u, rate = (id.y >> {{RateShift}}) & 15u;
            vbFrontFacing = (id.y & 0x80000000u) != 0u;

            // The fragment the colour pass ran: a coarse one's centre under the fog's shading rate (gl_ShadingRateFlag2HorizontalPixelsEXT 4, 4Horizontal 8,
            // 2Vertical 1, 4Vertical 2), its quad's neighbours a fragment away.
            ivec2 size = ivec2((rate & 8u) != 0u ? 4 : (rate & 4u) != 0u ? 2 : 1, (rate & 2u) != 0u ? 4 : (rate & 1u) != 0u ? 2 : 1);
            ivec2 cell = px / size;
            vbFragCoord = vec4(vec2(cell * size) + vec2(size) * 0.5, 0.0, 1.0);
            vbSx = (cell.x & 1) == 0 ? 1.0 : -1.0;
            vbSy = (cell.y & 1) == 0 ? 1.0 : -1.0;

            vec2 b12 = uintBitsToFloat(id.zw);
            vec3 b = vec3(1.0 - b12.x - b12.y, b12);
            vec3 dx = vec3(-slopes.x - slopes.y, slopes.xy), dy = vec3(-slopes.z - slopes.w, slopes.zw);
            vec3 gx = vec3(-coarse.x - coarse.y, coarse.xy), gy = vec3(-coarse.z - coarse.w, coarse.zw);
            vbBegin();
            vbFetch(row, prim, 0u);
            vbVertex();
            vbCorner(true, b.x, dx.x, dy.x, gx.x, gy.x);
            vbFetch(row, prim, 1u);
            vbVertex();
            vbCorner(false, b.y, dx.y, dy.y, gx.y, gy.y);
            vbFetch(row, prim, 2u);
            vbVertex();
            vbCorner(false, b.z, dx.z, dy.z, gx.z, gy.z);
            vbFinish();

            vec3 g = normalize(vNormal);
            vbGeometricDx = vbSx * (normalize(vNormal + vbSx * vNormalDx) - g);
            vbGeometricDy = vbSy * (normalize(vNormal + vbSy * vNormalDy) - g);
            vbShadowDx = vWorldDx;
            vbShadowDy = vWorldDy;
            vbBloodQuad = vbQuad(vBloodAmount, vBloodAmountDx, vBloodAmountDy);

            // Blood on gear: its film's edge and height take derivatives of what the whole material makes, so the neighbours run it too.
            uint f = materials.items[vMaterial].flags;
            if ((f & {{CharacterShaders.GearBloody}}u) != 0u && ((f & {{CharacterShaders.Worn}}u) != 0u ? vBloodAmount > 0.0 : vGear.a > 0.0))
            {
                float liesD, riseD;
                vbProbeAt(vbSx, 0.0, vec2(size), vbLiesX, vbRiseX);
                vbProbeAt(0.0, vbSy, vec2(size), vbLiesY, vbRiseY);
                vbProbeAt(vbSx, vbSy, vec2(size), liesD, riseD);
                vbFilmQuad = vbLiesX > 0.0 && vbLiesY > 0.0 && liesD > 0.0;
                vbRiseQuad = vbRiseX > 0.0 && vbRiseY > 0.0 && riseD > 0.0;
                vbFinish();
            }

            vbShade();
        }

        """;

    #endregion

    #region Text helpers

    /// <summary>
    /// <paramref name="text"/> with <paramref name="anchor"/> replaced; throws when the anchor is gone (the colour pass's text changed).
    /// </summary>
    static string Require(string text, string anchor, string with)
    {
        if (!text.Contains(anchor, StringComparison.Ordinal))
            throw new InvalidOperationException($"CharacterVisibilityShaders: the character programs no longer hold '{anchor}'");

        return text.Replace(anchor, with, StringComparison.Ordinal);
    }

    /// <summary>
    /// A formatted line in the invariant culture.
    /// </summary>
    static string CultureInvariant(FormattableString s) => FormattableString.Invariant(s);

    /// <summary>
    /// An input declaration of the colour fragment (<c>in vec3 vWorld;</c>, <c>flat in uint vMaterial;</c>): group 2 is it as a plain global.
    /// </summary>
    [GeneratedRegex(@"^(\s*)(?:flat\s+)?in\s+(\w+\s+\w+\s*;)", RegexOptions.Multiline)]
    private static partial Regex InRegex();

    /// <summary>
    /// An output declaration of the vertex program, with its line: flat, type and name.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(flat\s+)?out\s+(\w+)\s+(\w+)\s*;[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex OutRegex();

    /// <summary>
    /// A vertex input's location qualifier.
    /// </summary>
    [GeneratedRegex(@"layout\(location = \d+\) in ")]
    private static partial Regex LocationRegex();

    /// <summary>
    /// A global declared with a value (<c>float a = 0.0, b = 1.0;</c>): group 2 the declarators.
    /// </summary>
    [GeneratedRegex(@"^(bool|int|uint|float|vec[234]|uvec[234]|ivec[234])\s+(\w+\s*=[^;]*);$")]
    private static partial Regex GlobalRegex();

    #endregion
}
