namespace Meitou.ModelViewer;

/// <summary>
/// GLSL for the world view's terrain (OpenGL 3.3 core). The layer model follows the facts in
/// docs/formats/terrain.md ("How the terrain is textured"): per biome six layers (base, slope, cliff, grass,
/// dirt, road) chosen by slope and the overlay map, tinted by the colour map, up to four biomes per pixel
/// weighted by the blend map. Far away (beyond the game's material distance) and outside the loaded region the
/// terrain takes the biomes' blended ground colour, like the game's distant terrain.
/// </summary>
static class TerrainShaders
{
    /// <summary>Rows of the per-biome parameter texture (RGBA32F, one row per biome).</summary>
    public const int ParamTexels = 11;

    /// <summary>Texture units of the world-wide maps (TerrainTextures binds units 0..6 for the region).</summary>
    public const int HeightCoarseUnit = 7, HeightFineUnit = 8, GroundUnit = 9, WorldColourUnit = 10;

    /// <summary>
    /// Terrain height anywhere in the world: a coarse whole-world grid, replaced by the loaded region's finer grid
    /// inside the region (blended over a band at its edge so the height stays continuous). Shared by the terrain and
    /// water shaders.
    /// </summary>
    public const string HeightFunctions = """
        uniform sampler2D uHeightCoarse;   // whole world, R16: raw / 65535
        uniform vec4 uCoarseRect;          // world x0, z0, x1, z1 of the first and last samples
        uniform vec2 uCoarseCells;         // samples - 1 along X and Z
        uniform sampler2D uHeightFine;     // the loaded region
        uniform vec4 uFineRect;
        uniform vec2 uFineCells;
        uniform float uFineBand;           // world units over which the region fades into the coarse grid
        uniform bool uHasFine;

        float gridHeight(sampler2D s, vec2 p, vec4 rect, vec2 cells)
        {
            vec2 t = (p - rect.xy) / (rect.zw - rect.xy);
            return texture(s, (t * cells + 0.5) / (cells + 1.0)).r * 9800.0;
        }
        float fineWeight(vec2 p)
        {
            if (!uHasFine) return 0.0;
            vec2 d = min(p - uFineRect.xy, uFineRect.zw - p);
            return clamp(min(d.x, d.y) / uFineBand, 0.0, 1.0);
        }
        float terrainHeight(vec2 p)
        {
            float c = gridHeight(uHeightCoarse, p, uCoarseRect, uCoarseCells);
            float w = fineWeight(p);
            return w <= 0.0 ? c : mix(c, gridHeight(uHeightFine, p, uFineRect, uFineCells), w);
        }
        float terrainSpacing(vec2 p)
        {
            float coarse = (uCoarseRect.z - uCoarseRect.x) / uCoarseCells.x;
            return uHasFine ? mix(coarse, (uFineRect.z - uFineRect.x) / uFineCells.x, fineWeight(p)) : coarse;
        }
        vec3 terrainNormal(vec2 p)
        {
            float e = terrainSpacing(p);
            float hx = terrainHeight(p - vec2(e, 0.0)) - terrainHeight(p + vec2(e, 0.0));
            float hz = terrainHeight(p - vec2(0.0, e)) - terrainHeight(p + vec2(0.0, e));
            return normalize(vec3(hx, 2.0 * e, hz));
        }
        """;

    /// <summary>
    /// CDLOD patch: a grid of <c>cells</c>² quads placed over one quadtree node; vertices slide onto the next
    /// coarser grid with distance (TerrainQuadtree), heights come from <see cref="HeightFunctions"/>.
    /// </summary>
    public const string PatchVertex = "#version 330 core\n" + HeightFunctions + """

        layout(location = 0) in vec2 aGrid;   // integer grid coordinates 0..cells
        uniform mat4 uViewProjection;
        uniform vec3 uEye;
        uniform vec4 uNode;                    // x0, z0, size, cells
        uniform vec2 uMorph;                   // distance where morphing starts and ends (start >= 1e30: never)
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vCliffBlend;                  // unused: the terrain's cliff projection weights come from the per-pixel normal
        void main()
        {
            vCliffBlend = vec2(0.5);
            vec2 p = uNode.xy + aGrid * (uNode.z / uNode.w);
            float d = distance(vec3(p.x, terrainHeight(p), p.y), uEye);
            float k = uMorph.x >= 1e30 ? 0.0 : clamp((d - uMorph.x) / (uMorph.y - uMorph.x), 0.0, 1.0);
            vec2 g = aGrid - mod(aGrid, 2.0) * k;
            p = uNode.xy + g * (uNode.z / uNode.w);
            vWorld = vec3(p.x, terrainHeight(p), p.y);
            vNormal = vec3(0.0, 1.0, 0.0);   // the fragment shader takes the normal from the height field
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    /// <summary>
    /// Plain meshes drawn with the terrain material (TERRAIN-mode map features, the game's <c>Feature_Terrain_DX11</c>;
    /// docs/formats/foliage.md, "TERRAIN-mode meshes"). The cliff projection weights are made per vertex from the mesh
    /// normal, and a surface facing up (normal.y above 0.9) takes the (z, height) projection alone, as the game's feature
    /// vertex program does.
    /// </summary>
    public const string MeshVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uViewProjection;
        uniform mat4 uModel;           // the placement of the map feature
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vCliffBlend;
        void main()
        {
            vWorld = (uModel * vec4(aPosition, 1.0)).xyz;
            vNormal = transpose(inverse(mat3(uModel))) * aNormal;
            vec3 n = normalize(vNormal);
            vec2 cb = max(abs(normalize(n.xz + vec2(1e-6))) - 0.2, vec2(0.0)) * 7.0;
            cb *= cb;
            cb /= max(cb.x + cb.y, 1e-6);
            if (n.y > 0.9) cb = vec2(1.0, 0.0);
            vCliffBlend = cb;
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    public static readonly string Fragment = "#version 330 core\n" + HeightFunctions + AtmosphereShaders.Functions + """

        in vec3 vWorld;
        in vec3 vNormal;
        in vec2 vCliffBlend;           // meshes: the per-vertex cliff projection weights (MeshVertex)
        out vec4 fragColour;

        uniform vec3 uEye;
        uniform vec3 uLightDir;
        uniform vec3 uSunColour;
        uniform vec3 uAmbientSky;
        uniform vec3 uAmbientGround;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        uniform bool uWireframe;
        uniform bool uHeightNormals;   // terrain patches: normal from the height field; meshes: from the vertices
        uniform float uWaterHeight;    // underwater ground is darkened (the game's wetness rule)

        uniform bool uTextured;        // biome layers available
        uniform bool uNormalMaps;
        uniform bool uHasMaps;         // overlay + colour window textures available
        uniform int uMapState;         // 0 no maps, 1 maps still loading (ground colour only), 2 ready
        uniform int uDebug;            // 0 normal, 1 slot weights, 2 layer weights, 3 LOD-free shading
        uniform bool uFeature;         // map features (Feature_Terrain): no road layer, slope clamped to 1, cliff weights per vertex
        uniform int uFeatureBiome;     // map features: the one biome (parameter row) of the feature's origin; -1 = blend as the terrain
        uniform float uFarStart;       // distance where the textured terrain gives way to the ground colour
        uniform float uFarEnd;
        uniform bool uHasGround;       // whole-world ground colour map available
        uniform bool uHasWorldColour;  // whole-world colour map available

        uniform sampler2DArray uDiffuse;
        uniform sampler2DArray uNormal;
        uniform sampler2D uParams;     // row = biome, ParamTexels texels
        uniform usampler2D uCells;     // blend cells, two texels each: slots 0..3, slot 4; biome row, 254 = not loaded yet, 255 = unused
        uniform sampler2D uBlendMap;   // whole world: weights of slots 0..3; slot 4 takes the remainder
        uniform sampler2D uOverlay;    // window: R/G grass, B dirt, A road
        uniform sampler2D uColour;     // window: tint (×1.2), A gloss
        uniform sampler2D uGround;     // whole world: blended biome ground colour × brightness fix
        uniform sampler2D uWorldColour;// whole world: the colour map, downsampled
        uniform vec4 uRegion;          // x0, z0, 1/width, 1/depth of the overlay and colour windows (the same square); they wrap: texel = world mod width
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
            // Cliff: two vertical projections, weighted by how much the surface faces X or Z (meshes: per vertex).
            vec2 cb = max(abs(normalize(n.xz + vec2(1e-5))) - 0.2, vec2(0.0)) * 7.0;
            cb *= cb;
            cb /= max(cb.x + cb.y, 1e-5);
            if (n.y > 0.995) cb = vec2(0.5);
            if (uFeature) cb = vCliffBlend;

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
                // The cliff projections' normals are turned into the surface frame: red flipped on both, green flipped on
                // the (z, height) one where the surface faces -X and on the (x, height) one where it faces +Z (terrain.md).
                vec4 nCliffX = tex(uNormal, vec2(uv.y, vert) * sA.zw, layersA.z), nCliffZ = tex(uNormal, vec2(uv.x, vert) * sA.zw, layersA.z);
                nCliffX.rg = vec2(1.0 - nCliffX.r, n.x > 0.0 ? nCliffX.g : 1.0 - nCliffX.g);
                nCliffZ.rg = vec2(1.0 - nCliffZ.r, n.z < 0.0 ? nCliffZ.g : 1.0 - nCliffZ.g);
                vec4 nCliff = nCliffX * cb.x + nCliffZ * cb.y;
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

        // Untextured: height tint (sand, olive, grey).
        vec3 heightTint(float y)
        {
            float h = clamp(y / 3500.0, 0.0, 1.0);
            vec3 low = vec3(0.76, 0.70, 0.50), mid = vec3(0.55, 0.50, 0.36), high = vec3(0.62, 0.60, 0.58);
            return h < 0.5 ? mix(low, mid, h * 2.0) : mix(mid, high, h * 2.0 - 1.0);
        }

        void main()
        {
            if (uWireframe) { fragColour = vec4(0.1, 0.1, 0.1, 1.0); return; }
            vec3 n = uHeightNormals ? terrainNormal(vWorld.xz) : normalize(vNormal);
            float distance = length(vWorld - uEye);
            // Map features clamp the slope at 1, so overhangs (normal pointing down) keep the cliff layer.
            float slope = uFeature ? min(1.0, 1.0 - n.y) : 1.0 - n.y;
            vec2 world01 = (vWorld.xz + uHalfWorld) / (2.0 * uHalfWorld);

            // How much of the textured shading applies: before the far distance, and inside the overlay window once the
            // maps are loaded (the window follows the eye, so its edge lies beyond the material distance).
            float nearWeight = 1.0;
            if (uMapState == 1) nearWeight = 0.0;
            else if (uMapState == 2)
            {
                vec2 wuv = (vWorld.xz - uRegion.xy) * uRegion.zw;
                nearWeight = clamp(min(min(wuv.x, 1.0 - wuv.x), min(wuv.y, 1.0 - wuv.y)) / (3000.0 * uRegion.z), 0.0, 1.0);
            }
            nearWeight *= 1.0 - smoothstep(uFarStart, uFarEnd, distance);

            vec4 albedo = vec4(0.0);
            vec3 shadingNormal = n;
            if (nearWeight > 0.0)
            {
                vec4 map = vec4(0.0);
                vec4 colour = vec4(1.0);
                if (uHasMaps)
                {
                    map = texture(uOverlay, fract((vWorld.xz + uHalfWorld) * uRegion.zw));
                    map.r = max(map.r, map.g);
                    if (uFeature) map.a = 0.0;
                    colour = texture(uColour, fract((vWorld.xz + uHalfWorld) * uRegion.zw)) * 1.2;
                }
                if (uTextured)
                {
                    ivec2 cell = clamp(ivec2(floor(world01 * uCellGrid)), ivec2(0), ivec2(uCellGrid) - 1);
                    uvec4 slots = texelFetch(uCells, ivec2(cell.x * 2, cell.y), 0);
                    uint slot4 = texelFetch(uCells, ivec2(cell.x * 2 + 1, cell.y), 0).r;
                    vec4 weights = texture(uBlendMap, world01);
                    float rest = max(0.0, 1.0 - dot(weights, vec4(1.0)));
                    vec4 sumA = vec4(0.0), sumN = vec4(0.0);
                    float total = 0.0, pending = 0.0;
                    if (uFeatureBiome >= 0)
                    {
                        // A map feature has one biome for its whole surface (the game builds its material for the biome at its origin).
                        Surface s = biome(uFeatureBiome, n, slope, map, colour, distance);
                        sumA = s.albedo; sumN = s.normal; total = 1.0;
                    }
                    else for (int k = 0; k < 5; k++)
                    {
                        uint b = k < 4 ? slots[k] : slot4;
                        float wk = k < 4 ? weights[k] : rest;
                        if (b == 254u && wk >= 0.004) pending += wk;   // biome still loading: its share shows the ground colour
                        if (b >= 254u || wk < 0.004) continue;
                        Surface s = biome(int(b), n, slope, map, colour, distance);
                        sumA += s.albedo * wk;
                        sumN += s.normal * wk;
                        total += wk;
                    }
                    uint fallback = slot4 < 254u ? slot4 : slots.x;
                    if (total <= 0.0 && pending <= 0.0 && fallback < 254u)
                    {
                        Surface s = biome(int(fallback), n, slope, map, colour, distance);
                        sumA = s.albedo; sumN = s.normal; total = 1.0;
                    }
                    if (total > 0.0) { albedo = sumA / total; sumN /= total; }
                    else { albedo = vec4(0.6, 0.55, 0.45, 0.2); sumN = vec4(0.5, 0.5, 1.0, 1.0); }
                    // Biomes still loading show the ground colour in their share.
                    if (total > 0.0) nearWeight *= total / (total + pending);
                    else if (pending > 0.0) nearWeight = 0.0;
                    if (uDebug == 1) albedo = vec4(weights.rgb + weights.a * vec3(1.0, 1.0, 0.0) + rest * vec3(1.0), 1.0);
                    if (uNormalMaps)
                    {
                        // Tangent frame as the game builds it for terrain: binormal = n x (-1, 0, 0), tangent = binormal x n.
                        vec3 bn = normalize(cross(n, vec3(-1.0, 0.0, 0.0)));
                        vec3 t = normalize(cross(bn, n));
                        vec3 tn = sumN.rgb * 2.0 - 1.0;
                        shadingNormal = normalize(mix(n, normalize(t * tn.x + bn * tn.y + n * tn.z), nearWeight));
                    }
                }
                else albedo = vec4(uHasMaps ? colour.rgb / 1.2 : heightTint(vWorld.y), 0.2);
            }
            if (nearWeight < 1.0)
            {
                // Far: the blended biome ground colour (what the textured layers fade to), tinted by the colour map.
                vec3 far = uHasGround && uTextured ? texture(uGround, world01).rgb : heightTint(vWorld.y);
                if (uHasWorldColour && uTextured) far *= texture(uWorldColour, world01).rgb * 1.2;
                albedo = mix(vec4(far, 0.2), albedo, nearWeight);
            }

            // Underwater ground looks wet and darker (the game's wetness rule with a 2-unit edge, absorbance 0.5).
            float under = clamp((uWaterHeight + 2.0 - vWorld.y) / 2.0, 0.0, 1.0);
            float darken = min((1.0 - 1.0 / (under + 0.7)) * 0.5 + under * 0.2, 0.4);
            albedo.rgb *= 1.0 - max(darken, 0.0) * 0.5;

            vec3 l = normalize(uLightDir);
            float diff = max(dot(shadingNormal, l), 0.0);
            vec3 ambient = mix(uAmbientGround, uAmbientSky, 0.5 + 0.5 * shadingNormal.y);
            vec3 v = normalize(uEye - vWorld);
            float gloss = clamp(albedo.a, 0.0, 1.0);
            float spec = pow(max(dot(shadingNormal, normalize(l + v)), 0.0), 8.0 + 40.0 * gloss) * gloss * 0.25;
            vec3 colourOut = albedo.rgb * (ambient + diff * uSunColour) + spec * diff * uSunColour;
            // Game sky: the game's deferred lighting (docs/formats/lighting.md); terrainfp4.hlsl writes the biome albedo's alpha as the gloss.
            if (uAtmoParams.x > 0.5) colourOut = kenshiLight(albedo.rgb, shadingNormal, v, gloss, vWorld);
            if (uDebug == 3) colourOut = vec3(0.5) * (0.3 + 0.7 * diff);
            colourOut = atmoApply(colourOut, uEye, vWorld);   // aerial perspective (AtmosphereShaders)
            fragColour = vec4(colourOut, 1.0);
        }
        """;
}
