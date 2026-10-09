namespace Meitou.Rendering;

/// <summary>
/// The terrain cost probes (docs/render-terrain.md, "Where the terrain's 0.99 ms goes"): <c>MEITOU_TERRAIN_PROBE=name[,name...]</c> makes a second terrain
/// colour program from the shader text with the named parts cut or simplified (<see cref="TerrainShaders.ProbeNative"/>), and <c>--ab terrain-probe</c> draws
/// the main view with it on side B (A = the normal program; <c>MEITOU_TERRAIN_PROBE_ON=1</c> starts with it on, for <c>--screenshot</c>). Nothing is made
/// without the variable. The picture changes: these are cost probes, not options.
/// </summary>
public static class TerrainProbe
{
    /// <summary>The probe names from the environment (null: no probe program is made).</summary>
    public static readonly string? Names = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_PROBE");

    /// <summary>True: the main view's terrain draws with the probe program (MEITOU_TERRAIN_PROBE_ON=1 starts it on, for screenshots).</summary>
    public static bool On { get; set; } = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_PROBE_ON") == "1";

    public static readonly bool NoDraw = Names is not null && Names.Split(',').Contains("nodraw");
}

static partial class TerrainShaders
{
    static string Patch(string text, string from, string to)
    {
        int at = text.IndexOf(from, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"TerrainShaders probe: '{from}' not found; update the patch.");
        return text.Remove(at, from.Length).Insert(at, to);
    }

    const string TopRank = """
        float wv[5] = float[5](weights.x, weights.y, weights.z, weights.w, rest);
                        int topRank = 0;
                        for (int j = 0; j < 5; j++) if (wv[j] > wv[k] || (wv[j] == wv[k] && j < k)) topRank++;
        """;

    /// <summary>The probe program's vertex and fragment text for the probe names (comma separated); each name cuts or simplifies one part.</summary>
    public static (string Vertex, string Fragment) ProbeNative(string names)
    {
        string v = PatchVertex, f = Fragment;
        foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (name)
            {
                case "nodraw": break;
                case "none": break;
                case "shadow-off": f = Patch(f, "* kenshiShadow(world, n);", "* 1.0;"); break;
                case "no-blocker": f = Patch(f, "if (full && uMsFlags.x > 0.5)", "if (false)"); break;
                case "shadow-8tap": f = Patch(f, "bool full = float(c) < uMsFlags.z;", "bool full = false;"); break;
                case "shadow-1tap": f = Patch(f, "bool full = float(c) < uMsFlags.z;", "bool full = float(c) < uMsFlags.z; if (true) return textureLod(uShadowMap, vec3(rect.xy + t.xy * rect.zw, t.z), 0.0);"); break;
                case "no-extra-shadow": f = Patch(f, "return min(csm, min(msTerrain(world, depth), msLandmark(world, ng, depth, noise)));", "return csm;"); break;
                case "no-cascade-blend": f = Patch(f, "if (c + 1 < count)", "if (false)"); break;
                case "light-flat": f = Patch(f, "colourOut = kenshiLight(albedo.rgb, shadingNormal, v, gloss, vWorld);", "colourOut = albedo.rgb * (uAtmoSunLight * max(dot(shadingNormal, uAtmoLight.xyz), 0.0) + 0.5);"); break;
                case "no-env":
                    f = Patch(f, "vec4 am = atmoAmbientMapAt(world);", "vec4 am = vec4(1.0, 1.0, 1.0, 0.5);");
                    f = Patch(f, "vec3 envDiffuse = atmoIrradiance(n) *", "vec3 envDiffuse = vec3(1.0) *");
                    f = Patch(f, "if (uAtmoMaps.y > 0.5)", "if (false)");
                    break;
                case "no-env-spec": f = Patch(f, "if (uAtmoMaps.y > 0.5)", "if (false)"); break;
                case "haze-off": f = Patch(f, "colourOut = atmoApply(colourOut, uEye, vWorld);", ""); break;
                case "wet-off": f = Patch(f, "makeWet(albedo, wetAmount, 1.0 - albedo.a + absorbance, uWaterHeight - vWorld.y, 2.0);", ""); break;
                case "normal-flat": f = Patch(f, "vec3 n = uHeightNormals ? terrainNormal(vWorld.xz) : normalize(vNormal);", "vec3 n = vec3(0.0, 1.0, 0.0);"); break;
                case "normal-vertex":
                    v = Patch(v, "vNormal = vec3(0.0, 1.0, 0.0);", "vNormal = terrainNormal(p);");
                    f = Patch(f, "vec3 n = uHeightNormals ? terrainNormal(vWorld.xz) : normalize(vNormal);", "vec3 n = normalize(vNormal);");
                    break;
                case "biome1": case "biome2": case "biome3":
                    f = Patch(f, "float wk = k < 4 ? weights[k] : rest;", "float wk = k < 4 ? weights[k] : rest;\n                        " + TopRank + "\n                        if (topRank >= " + name[^1] + ") continue;");
                    break;
                case "layers-base": f = Patch(f, "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);", "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0); map = vec4(0.0); w = vec4(0.0);"); break;
                case "no-grass": f = Patch(f, "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);", "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0); map.r = 0.0;"); break;
                case "no-slope": f = Patch(f, "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);", "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0); w.x = 0.0;"); break;
                case "no-dirt": f = Patch(f, "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);", "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0); map.b = 0.0;"); break;
                case "no-road": f = Patch(f, "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);", "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0); map.a = 0.0;"); break;
                case "no-cliff": f = Patch(f, "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);", "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0); w.y = 0.0;"); break;
                case "no-maps": f = Patch(f, "map = texture(uOverlay, fract((vWorld.xz + uHalfWorld) * uRegion.zw));", "map = vec4(0.0);"); break;
                case "count-lo": case "count-hi": case "count-bio":
                    f = Patch(f, "vec4 tex(sampler2DArray s, Coord c, float layer) { return textureGrad(s, vec3(c.p, layer), c.dx, c.dy); }", "float gTex = 0.0, gBio = 0.0; vec4 tex(sampler2DArray s, Coord c, float layer) { gTex += 1.0; return textureGrad(s, vec3(c.p, layer), c.dx, c.dy); }");
                    f = Patch(f, "Surface s = biome(int(b), n, slope, map, colour, distance);", "gBio += 1.0; Surface s = biome(int(b), n, slope, map, colour, distance);");
                    f = Patch(f, "fragColour = vec4(colourOut, 1.0);", name == "count-bio" ? "int cv = int(gBio + 0.5); fragColour = vec4(1000.0 * float(cv & 1), 1000.0 * float((cv >> 1) & 1), 1000.0 * float((cv >> 2) & 1), 1.0);"
                        : "int cv = int(gTex * 0.5 + 0.25); if (" + (name == "count-hi" ? "true" : "false") + ") cv >>= 3; fragColour = vec4(1000.0 * float(cv & 1), 1000.0 * float((cv >> 1) & 1), 1000.0 * float((cv >> 2) & 1), 1.0);");
                    break;
                case "cliff-1proj":
                    f = Patch(f, "vec4 cCliff = (tex(uDiffuse, cliffX, layersA.z) * cb.x + tex(uDiffuse, cliffZ, layersA.z) * cb.y) * mix(white, colour, omult.x);",
                        "bool domX = cb.x >= cb.y; vec4 cCliff = (domX ? tex(uDiffuse, cliffX, layersA.z) : tex(uDiffuse, cliffZ, layersA.z)) * mix(white, colour, omult.x);");
                    f = Patch(f, "vec4 nCliffX = tex(uNormal, cliffX, layersA.z), nCliffZ = tex(uNormal, cliffZ, layersA.z);",
                        "bool domX = cb.x >= cb.y; vec4 nFlat = vec4(0.5, 0.5, 1.0, 1.0); vec4 nCliffX = domX ? tex(uNormal, cliffX, layersA.z) : nFlat, nCliffZ = domX ? nFlat : tex(uNormal, cliffZ, layersA.z); cb = domX ? vec2(1.0, 0.0) : vec2(0.0, 1.0);");
                    break;
                case "cliff-nonormal":
                    f = Patch(f, "vec4 nCliffX = tex(uNormal, cliffX, layersA.z), nCliffZ = tex(uNormal, cliffZ, layersA.z);", "vec4 nCliffX = vec4(0.5, 0.5, 1.0, 1.0), nCliffZ = vec4(0.5, 0.5, 1.0, 1.0);");
                    break;
                case "count-cliff":
                    f = Patch(f, "struct Surface { vec4 albedo; vec4 normal; float absorb; };", "float gCliff = 0.0; struct Surface { vec4 albedo; vec4 normal; float absorb; };");
                    f = Patch(f, "vec4 cCliff = (tex(uDiffuse, cliffX", "gCliff = 1.0; vec4 cCliff = (tex(uDiffuse, cliffX");
                    f = Patch(f, "fragColour = vec4(colourOut, 1.0);", "fragColour = vec4(gCliff * 1000.0, 0.0, gCliff * 1000.0, 1.0);");
                    break;
                case "weights-a": case "weights-b": case "weights-c": case "weights-d": case "weights-e":
                    f = Patch(f, "struct Surface { vec4 albedo; vec4 normal; float absorb; };", "vec3 gW = vec3(0.0); struct Surface { vec4 albedo; vec4 normal; float absorb; };");
                    f = Patch(f, "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);", "float far = clamp(distance * fade.a - 0.3, 0.0, 1.0); gW = " + (name == "weights-a" ? "vec3(float(w.x != 0.0), float(w.y != 0.0), float(map.r != 0.0));" : name == "weights-d" ? "vec3(float(w.y > 0.02), float(w.y > 0.05), float(w.y > 0.1));" : name == "weights-e" ? "vec3(float(w.x > 0.02), float(w.x > 0.05), float(w.x > 0.1));" : name == "weights-b" ? "vec3(float(w.x > 0.004), float(w.y > 0.004), float(map.r > 0.004));" : "vec3(float(map.b > 0.004), float(map.a > 0.004), float(map.b != 0.0));"));
                    f = Patch(f, "fragColour = vec4(colourOut, 1.0);", "fragColour = vec4(gW * 1000.0, 1.0);");
                    break;
                case "eps-cliff":
                    f = f.Replace("if (w.y != 0.0)", "if (w.y > 0.004)");
                    break;
                case "eps-layers":
                    f = f.Replace("if (w.y != 0.0)", "if (w.y > 0.004)").Replace("if (w.x != 0.0)", "if (w.x > 0.004)").Replace("if (map.r != 0.0)", "if (map.r > 0.004)").Replace("if (map.b != 0.0)", "if (map.b > 0.004)").Replace("if (map.a != 0.0)", "if (map.a > 0.004)");
                    break;
                case "eps-cliff-02": f = f.Replace("if (w.y != 0.0)", "if (w.y > 0.02)"); break;
                case "eps-cliff-05": f = f.Replace("if (w.y != 0.0)", "if (w.y > 0.05)"); break;
                case "eps-slope-05": f = f.Replace("if (w.x != 0.0)", "if (w.x > 0.05)"); break;
                case "eps-both-05": f = f.Replace("if (w.y != 0.0)", "if (w.y > 0.05)").Replace("if (w.x != 0.0)", "if (w.x > 0.05)"); break;
                case "paint": f = Patch(f, "fragColour = vec4(colourOut, 1.0);", "fragColour = vec4(1000.0, 0.0, 1000.0, 1.0);"); break;
                default: throw new InvalidOperationException($"MEITOU_TERRAIN_PROBE: unknown probe '{name}'.");
            }
        }
        return (Native(v), Native(f));
    }
}
