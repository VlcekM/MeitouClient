namespace Meitou.ModelViewer;

/// <summary>
/// GLSL for the world view's terrain (OpenGL 3.3 core). The layer model follows the facts in
/// docs/formats/terrain.md ("How the terrain is textured"): per biome six layers (base, slope, cliff, grass,
/// dirt, road) chosen by slope and the overlay map, tinted by the colour map, up to four biomes per pixel
/// weighted by the blend map.
/// </summary>
static class TerrainShaders
{
    /// <summary>Rows of the per-biome parameter texture (RGBA32F, one row per biome).</summary>
    public const int ParamTexels = 11;

    public const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uViewProjection;
        uniform mat4 uModel;           // identity for terrain chunks; the placement for TERRAIN-mode map features
        out vec3 vWorld;
        out vec3 vNormal;
        void main()
        {
            vWorld = (uModel * vec4(aPosition, 1.0)).xyz;
            vNormal = transpose(inverse(mat3(uModel))) * aNormal;
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    public const string Fragment = """
        #version 330 core
        in vec3 vWorld;
        in vec3 vNormal;
        out vec4 fragColour;

        uniform vec3 uEye;
        uniform vec3 uLightDir;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        uniform bool uWireframe;

        uniform bool uTextured;        // biome layers available
        uniform bool uNormalMaps;
        uniform bool uHasMaps;         // overlay + colour region textures available
        uniform int uDebug;            // 0 normal, 1 slot weights, 2 layer weights
        uniform bool uNoRoads;         // map features: no road layer

        uniform sampler2DArray uDiffuse;
        uniform sampler2DArray uNormal;
        uniform sampler2D uParams;     // row = biome, ParamTexels texels
        uniform usampler2D uCells;     // blend cells, two texels each: slots 0..3, slot 4; region biome index (255 = none)
        uniform sampler2D uBlendMap;   // whole world: weights of slots 0..3; slot 4 takes the remainder
        uniform sampler2D uOverlay;    // region: R/G grass, B dirt, A road
        uniform sampler2D uColour;     // region: tint (×1.2), A gloss
        uniform vec4 uRegion;          // x0, z0, 1/width, 1/depth of the overlay region texture
        uniform vec4 uColourRegion;    // the same for the colour region texture
        uniform vec2 uCellGrid;        // cells along X and Z
        uniform float uHalfWorld;

        vec4 P(int biome, int k) { return texelFetch(uParams, ivec2(k, biome), 0); }

        struct Surface { vec4 albedo; vec4 normal; };

        vec4 tex(sampler2DArray s, vec2 uv, float layer) { return texture(s, vec3(uv, layer)); }

        // One biome's surface (the layer model of docs/formats/terrain.md).
        Surface biome(int b, vec3 n, float slope, vec4 map, vec4 colour, float distance)
        {
            vec4 layersA = P(b, 0), layersB = P(b, 1);
            vec4 sA = P(b, 2), sB = P(b, 3), sC = P(b, 4);
            vec4 smin = P(b, 5), smax = P(b, 6), sblend = P(b, 7);
            vec4 omult = P(b, 8), fade = P(b, 9), distort = P(b, 10);

            vec2 uv = vWorld.xz / 5000.0;
            float vert = 1.0 - vWorld.y / 5000.0
                + (cos(vWorld.x * distort.x) + cos(vWorld.z * distort.x)) * distort.y;
            // Cliff: two vertical projections, weighted by how much the surface faces X or Z.
            vec2 cb = max(vec2(0.0), pow((abs(normalize(n.xz + vec2(1e-5))) - 0.2) * 7.0, vec2(2.0)));
            cb /= max(cb.x + cb.y, 1e-5);
            if (n.y > 0.995) cb = vec2(0.5);

            vec4 w = smoothstep(smin - sblend, smin, vec4(slope)) * smoothstep(smax + sblend, smax, vec4(slope));
            vec4 white = vec4(1.0);

            vec4 cBase = tex(uDiffuse, uv * sB.xy, layersA.x) * colour;
            vec4 cSlope = tex(uDiffuse, uv * sA.xy, layersA.y) * colour;
            vec4 cCliff = (tex(uDiffuse, vec2(uv.y, vert) * sA.zw, layersA.z) * cb.x + tex(uDiffuse, vec2(uv.x, vert) * sA.zw, layersA.z) * cb.y)
                * mix(white, colour, omult.x);
            vec4 cGrass = tex(uDiffuse, uv * sB.zw, layersA.w) * mix(white, colour, omult.y);
            vec4 cDirt = tex(uDiffuse, uv * sC.xy, layersB.x) * mix(white, colour, omult.z);
            vec4 cRoad = tex(uDiffuse, uv * sC.zw, layersB.y) * mix(white, colour, omult.w);
            float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);

            Surface s;
            s.albedo = mix(cBase, cGrass, map.r);
            s.albedo = mix(s.albedo, cSlope, w.x);
            s.albedo = mix(s.albedo, cDirt, map.b);
            s.albedo.rgb = mix(s.albedo.rgb, fade.rgb * colour.rgb, far);
            s.albedo.rgb = mix(s.albedo.rgb, cRoad.rgb, map.a);
            s.albedo = mix(s.albedo, cCliff, w.y);
            s.albedo.rgb *= layersB.z;

            if (uNormalMaps)
            {
                vec4 nBase = tex(uNormal, uv * sB.xy, layersA.x);
                vec4 nSlope = tex(uNormal, uv * sA.xy, layersA.y);
                vec4 nCliff = tex(uNormal, vec2(uv.y, vert) * sA.zw, layersA.z) * cb.x + tex(uNormal, vec2(uv.x, vert) * sA.zw, layersA.z) * cb.y;
                vec4 nGrass = tex(uNormal, uv * sB.zw, layersA.w);
                vec4 nDirt = tex(uNormal, uv * sC.xy, layersB.x);
                vec4 nRoad = tex(uNormal, uv * sC.zw, layersB.y);
                s.normal = mix(nBase, nGrass, map.r);
                s.normal = mix(s.normal, nSlope, w.x);
                s.normal = mix(s.normal, nDirt, map.b);
                s.normal = mix(s.normal, vec4(0.5, 0.5, 1.0, 1.0), far);
                s.normal = mix(s.normal, nRoad, map.a);
                s.normal = mix(s.normal, nCliff, w.y);
            }
            else s.normal = vec4(0.5, 0.5, 1.0, 1.0);
            if (uDebug == 2) { s.albedo = vec4(w.y, w.x, map.r, 1.0); }
            return s;
        }

        void main()
        {
            if (uWireframe) { fragColour = vec4(0.1, 0.1, 0.1, 1.0); return; }
            vec3 n = normalize(vNormal);
            float distance = length(vWorld - uEye);
            float slope = 1.0 - n.y;

            vec4 map = vec4(0.0);
            vec4 colour = vec4(1.0);
            if (uHasMaps)
            {
                map = texture(uOverlay, (vWorld.xz - uRegion.xy) * uRegion.zw);
                map.r = max(map.r, map.g);
                if (uNoRoads) map.a = 0.0;
                colour = texture(uColour, (vWorld.xz - uColourRegion.xy) * uColourRegion.zw) * 1.2;
            }

            vec4 albedo;
            vec3 shadingNormal = n;
            if (uTextured)
            {
                vec2 world01 = (vWorld.xz + uHalfWorld) / (2.0 * uHalfWorld);
                ivec2 cell = clamp(ivec2(floor(world01 * uCellGrid)), ivec2(0), ivec2(uCellGrid) - 1);
                uvec4 slots = texelFetch(uCells, ivec2(cell.x * 2, cell.y), 0);
                uint slot4 = texelFetch(uCells, ivec2(cell.x * 2 + 1, cell.y), 0).r;
                vec4 weights = texture(uBlendMap, world01);
                float rest = max(0.0, 1.0 - dot(weights, vec4(1.0)));
                vec4 sumA = vec4(0.0), sumN = vec4(0.0);
                float total = 0.0;
                for (int k = 0; k < 5; k++)
                {
                    uint b = k < 4 ? slots[k] : slot4;
                    float wk = k < 4 ? weights[k] : rest;
                    if (b == 255u || wk < 0.004) continue;
                    Surface s = biome(int(b), n, slope, map, colour, distance);
                    sumA += s.albedo * wk;
                    sumN += s.normal * wk;
                    total += wk;
                }
                uint fallback = slot4 != 255u ? slot4 : slots.x;
                if (total <= 0.0 && fallback != 255u)
                {
                    Surface s = biome(int(fallback), n, slope, map, colour, distance);
                    sumA = s.albedo; sumN = s.normal; total = 1.0;
                }
                if (total > 0.0) { albedo = sumA / total; sumN /= total; }
                else { albedo = vec4(0.6, 0.55, 0.45, 0.2); sumN = vec4(0.5, 0.5, 1.0, 1.0); }
                if (uDebug == 1) albedo = vec4(weights.rgb + weights.a * vec3(1.0, 1.0, 0.0) + rest * vec3(1.0), 1.0);
                if (uNormalMaps)
                {
                    // Tangent frame as the game builds it for terrain: binormal = n x (-1, 0, 0), tangent = binormal x n.
                    vec3 bn = normalize(cross(n, vec3(-1.0, 0.0, 0.0)));
                    vec3 t = normalize(cross(bn, n));
                    vec3 tn = sumN.rgb * 2.0 - 1.0;
                    shadingNormal = normalize(t * tn.x + bn * tn.y + n * tn.z);
                }
            }
            else
            {
                // Untextured: height tint (sand, olive, grey) times the colour map when present.
                float h = clamp(vWorld.y / 3500.0, 0.0, 1.0);
                vec3 low = vec3(0.76, 0.70, 0.50), mid = vec3(0.55, 0.50, 0.36), high = vec3(0.62, 0.60, 0.58);
                vec3 c = h < 0.5 ? mix(low, mid, h * 2.0) : mix(mid, high, h * 2.0 - 1.0);
                albedo = vec4(uHasMaps ? colour.rgb / 1.2 : c, 0.2);
            }

            vec3 l = normalize(uLightDir);
            float diff = max(dot(shadingNormal, l), 0.0);
            vec3 ambient = mix(vec3(0.20, 0.19, 0.17), vec3(0.36, 0.40, 0.46), 0.5 + 0.5 * shadingNormal.y);
            vec3 v = normalize(uEye - vWorld);
            float gloss = clamp(albedo.a, 0.0, 1.0);
            float spec = pow(max(dot(shadingNormal, normalize(l + v)), 0.0), 8.0 + 40.0 * gloss) * gloss * 0.25;
            vec3 colourOut = albedo.rgb * (ambient + diff * vec3(1.0, 0.96, 0.88)) + spec * diff;
            float fog = clamp(distance / uFogDistance, 0.0, 1.0);
            colourOut = mix(colourOut, uFogColour, fog * fog * 0.85);
            fragColour = vec4(colourOut, 1.0);
        }
        """;
}
