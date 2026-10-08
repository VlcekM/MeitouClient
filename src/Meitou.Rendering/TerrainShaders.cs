namespace Meitou.Rendering;

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
    public const int ParamTexels = 13;

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
    /// vertex program does. Instanced: the placement's rows at <see cref="MeshInstanceLocation"/>, row 0's w the feature's biome row
    /// (read by <see cref="MeshFragment"/>; the w row of a placement only reaches the position's unused w).
    /// </summary>
    public const string MeshVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 7) in vec4 aModel0;
        layout(location = 8) in vec4 aModel1;
        layout(location = 9) in vec4 aModel2;
        layout(location = 10) in vec4 aModel3;
        uniform mat4 uViewProjection;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vCliffBlend;
        flat out int vFeatureBiome;
        flat out float vFade;
        void main()
        {
            mat4 model = mat4(vec4(aModel0.xyz, 0.0), aModel1, aModel2, aModel3);   // the placement of the map feature
            vFeatureBiome = int(aModel0.w);
            vFade = aModel1.w;   // the foliage cull's dither threshold for a rock crossfading with its impostor (0: none; a placement's row 1 w is otherwise 0)
            vWorld = (model * vec4(aPosition, 1.0)).xyz;
            vNormal = transpose(inverse(mat3(model))) * aNormal;
            vec3 n = normalize(vNormal);
            vec2 cb = max(abs(normalize(n.xz + vec2(1e-6))) - 0.2, vec2(0.0)) * 7.0;
            cb *= cb;
            cb /= max(cb.x + cb.y, 1e-6);
            if (n.y > 0.9) cb = vec2(1.0, 0.0);
            vCliffBlend = cb;
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    /// <summary>First of the four locations the instanced depth path reads a placement's rows from (the meshes use 0 to 6).</summary>
    public const int MeshInstanceLocation = 7;

    /// <summary>
    /// <see cref="MeshVertex"/>'s position for the shadow map, the placement per instance instead of <c>uModel</c> (the
    /// TERRAIN-mode meshes' casters drawn instanced; the arithmetic is the uniform form's, so the map is the same).
    /// </summary>
    public const string MeshInstancedDepthVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 7) in vec4 aModel0;
        layout(location = 8) in vec4 aModel1;
        layout(location = 9) in vec4 aModel2;
        layout(location = 10) in vec4 aModel3;
        uniform mat4 uViewProjection;
        void main()
        {
            mat4 model = mat4(aModel0, aModel1, aModel2, aModel3);
            vec3 world = (model * vec4(aPosition, 1.0)).xyz;
            gl_Position = uViewProjection * vec4(world, 1.0);
        }
        """;

    public static readonly string Fragment = "#version 330 core\n#extension GL_ARB_derivative_control : require\n" + HeightFunctions + AtmosphereShaders.Functions + """

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
        uniform bool uGrid;            // the sandbox's floor grid: lines every 10 units, stronger every 100, within 1000 of the origin

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

        struct Surface { vec4 albedo; vec4 normal; float absorb; };

        vec4 tex(sampler2DArray s, vec2 uv, float layer) { return texture(s, vec3(uv, layer)); }

        // A layer's coordinate with its screen derivatives, taken before the layer branches below (derivatives inside a branch
        // that only some pixels of a 2 x 2 quad take are undefined), so a skipped neighbour cannot change a sampled pixel's mip.
        struct Coord { vec2 p, dx, dy; };
        Coord coord(vec2 p) { Coord c; c.p = p; c.dx = dFdxCoarse(p); c.dy = dFdyCoarse(p); return c; }
        vec4 tex(sampler2DArray s, Coord c, float layer) { return textureGrad(s, vec3(c.p, layer), c.dx, c.dy); }

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

            // A layer whose weight is exactly 0 at this pixel is not sampled: mixing in a weight of 0 leaves the value as it was
            // (x + 0 * (y - x) and x * 1 + y * 0 are both x). Most ground has no road, dirt, slope or cliff, so this skips most of
            // the 14 layer samples per biome (docs/formats/terrain.md, "In the viewer").
            Coord base = coord(uv * sB.xy), grass = coord(uv * sB.zw), slopeUv = coord(uv * sA.xy), dirt = coord(uv * sC.xy), road = coord(uv * sC.zw);
            Coord cliffX = coord(vec2(uv.y, vert) * sA.zw), cliffZ = coord(vec2(uv.x, vert) * sA.zw);
            float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);

            Surface s;
            s.albedo = tex(uDiffuse, base.p, layersA.x) * colour;
            if (map.r != 0.0) s.albedo = mix(s.albedo, tex(uDiffuse, grass, layersA.w) * mix(white, colour, omult.y), map.r);
            if (w.x != 0.0) s.albedo = mix(s.albedo, tex(uDiffuse, slopeUv, layersA.y) * colour, w.x);
            if (map.b != 0.0) s.albedo = mix(s.albedo, tex(uDiffuse, dirt, layersB.x) * mix(white, colour, omult.z), map.b);
            s.albedo.rgb = mix(s.albedo.rgb, fade.rgb * colour.rgb, far);
            if (map.a != 0.0) s.albedo.rgb = mix(s.albedo.rgb, (tex(uDiffuse, road, layersB.y) * mix(white, colour, omult.w)).rgb, map.a);
            if (w.y != 0.0)
            {
                vec4 cCliff = (tex(uDiffuse, cliffX, layersA.z) * cb.x + tex(uDiffuse, cliffZ, layersA.z) * cb.y) * mix(white, colour, omult.x);
                s.albedo = mix(s.albedo, cCliff, w.y);
            }
            s.albedo.rgb *= layersB.z;

            if (uNormalMaps)
            {
                s.normal = tex(uNormal, base.p, layersA.x);
                if (map.r != 0.0) s.normal = mix(s.normal, tex(uNormal, grass, layersA.w), map.r);
                if (w.x != 0.0) s.normal = mix(s.normal, tex(uNormal, slopeUv, layersA.y), w.x);
                if (map.b != 0.0) s.normal = mix(s.normal, tex(uNormal, dirt, layersB.x), map.b);
                s.normal = mix(s.normal, vec4(0.5, 0.5, 1.0, 1.0), far);
                if (map.a != 0.0) s.normal = mix(s.normal, tex(uNormal, road, layersB.y), map.a);
                if (w.y != 0.0)
                {
                    // The cliff projections' normals are turned into the surface frame: red flipped on both, green flipped on
                    // the (z, height) one where the surface faces -X and on the (x, height) one where it faces +Z (terrain.md).
                    vec4 nCliffX = tex(uNormal, cliffX, layersA.z), nCliffZ = tex(uNormal, cliffZ, layersA.z);
                    nCliffX.rg = vec2(1.0 - nCliffX.r, n.x > 0.0 ? nCliffX.g : 1.0 - nCliffX.g);
                    nCliffZ.rg = vec2(1.0 - nCliffZ.r, n.z < 0.0 ? nCliffZ.g : 1.0 - nCliffZ.g);
                    s.normal = mix(s.normal, nCliffX * cb.x + nCliffZ * cb.y, w.y);
                }
            }
            else s.normal = vec4(0.5, 0.5, 1.0, 1.0);
            if (uDebug == 2) { s.albedo = vec4(w.y, w.x, map.r, 1.0); }
            // The rain absorbance of the layers, blended like the textures (terrainfp4.hlsl computeBiome).
            vec4 ab0 = P(b, 11), ab1 = P(b, 12);
            float absorb = mix(ab0.x, ab0.w, map.r);
            absorb = mix(absorb, ab0.y, w.x);
            absorb = mix(absorb, ab1.x, map.b);
            absorb = mix(absorb, ab1.y, map.a);
            s.absorb = mix(absorb, ab0.z, w.y);
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
            float absorbance = 0.5;   // the layers' rain absorbance; the ground colour (far, untextured) takes the old fixed 0.5
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
                    float sumAbsorb = 0.0;
                    float total = 0.0, pending = 0.0;
                    if (uFeatureBiome >= 0)
                    {
                        // A map feature has one biome for its whole surface (the game builds its material for the biome at its origin).
                        Surface s = biome(uFeatureBiome, n, slope, map, colour, distance);
                        sumA = s.albedo; sumN = s.normal; sumAbsorb = s.absorb; total = 1.0;
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
                        sumAbsorb += s.absorb * wk;
                        total += wk;
                    }
                    uint fallback = slot4 < 254u ? slot4 : slots.x;
                    if (total <= 0.0 && pending <= 0.0 && fallback < 254u)
                    {
                        Surface s = biome(int(fallback), n, slope, map, colour, distance);
                        sumA = s.albedo; sumN = s.normal; sumAbsorb = s.absorb; total = 1.0;
                    }
                    if (total > 0.0) { albedo = sumA / total; sumN /= total; absorbance = sumAbsorb / total; }
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
                absorbance = mix(0.5, absorbance, nearWeight);
            }

            // The game's wetness (common/wet.hlsl, terrainfp4.hlsl): the rain's wetness plus the water line (a 2-unit edge) on an absorbance of 1 - gloss plus
            // the layers'. A rock bake (uWaterHeight far below the world) is without weather, so its impostor does not keep a rain.
            float wetAmount = uWaterHeight > -1.0e5 ? uWeatherWet.x : 0.0;
            makeWet(albedo, wetAmount, 1.0 - albedo.a + absorbance, uWaterHeight - vWorld.y, 2.0);

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
            if (uGrid && max(abs(vWorld.x), abs(vWorld.z)) <= 500.0)
            {
                // Anti-aliased lines a pixel wide (two on the hundreds), by the distance to the nearest line in screen derivatives.
                vec2 w = fwidth(vWorld.xz);
                vec2 m10 = abs(fract(vWorld.xz / 10.0 + 0.5) - 0.5) * 10.0 / w;
                vec2 m100 = abs(fract(vWorld.xz / 100.0 + 0.5) - 0.5) * 100.0 / w;
                float minor = 1.0 - clamp(min(m10.x, m10.y), 0.0, 1.0);
                float major = 1.0 - clamp(min(m100.x, m100.y) - 0.5, 0.0, 1.0);
                vec2 axis = 1.0 - clamp(abs(vWorld.xz) / w - 0.5, 0.0, 1.0);
                colourOut = mix(colourOut, vec3(0.1, 0.12, 0.2), 0.3 * minor);
                colourOut = mix(colourOut, vec3(0.05, 0.07, 0.15), 0.75 * major);
                colourOut = mix(colourOut, vec3(1.0, 0.45, 0.4), 0.8 * axis.y);   // the X axis (z = 0) red
                colourOut = mix(colourOut, vec3(0.45, 0.6, 1.0), 0.8 * axis.x);   // the Z axis (x = 0) blue
            }
            fragColour = vec4(colourOut, 1.0);
        }
        """;

    /// <summary><see cref="Fragment"/> for the instanced <see cref="MeshVertex"/>: the feature's biome per instance instead of the uniform.</summary>
    public static readonly string MeshFragment = PerInstanceBiome(Fragment);

    static string PerInstanceBiome(string fragment)
    {
        const string uniform = "uniform int uFeatureBiome;";
        if (!fragment.Contains(uniform)) throw new InvalidOperationException("TerrainShaders.Fragment no longer declares uFeatureBiome.");
        string f = fragment.Replace(uniform, "flat in int vFeatureBiome;\n#define uFeatureBiome vFeatureBiome\nflat in float vFade;\n");
        // A rock crossfading with its impostor (docs/impostors.md section 13): the cull's dither threshold in the placement's row 1 w, as the foliage meshes dither
        // (a threshold in (0, 1) keeps that share of the pixels; 0, what every other placement has, keeps all).
        const string wireframe = "if (uWireframe) { fragColour = vec4(0.1, 0.1, 0.1, 1.0); return; }";
        int at = f.IndexOf(wireframe, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("TerrainShaders.Fragment no longer starts with the wireframe line.");
        return f.Insert(at, "if (vFade > 0.0 && vFade < 1.0 && fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) >= vFade) discard;\n            ");
    }

    // ---- the native model (docs/renderer-native.md 3.3, step O): the texts above through NativeShaders.Port, bodies unchanged ----

    /// <summary>Set of <see cref="ConstantsBlock"/> (after the native model's frame set 0 and bindless set 1).</summary>
    public const int ConstantsSet = 2;

    /// <summary>
    /// The terrain's own per-segment values (set 2, binding 0, a dynamic uniform buffer in the frame's constants; <see cref="TerrainConstants"/>
    /// is the C# side): what <c>TerrainRenderer.Apply</c> and <c>BindHeights</c> set as loose uniforms, and the bindless indices of the
    /// terrain's textures. Too big for the 128-byte push range, and the same for every draw of a segment.
    /// </summary>
    public const string ConstantsBlock = """
        layout(std140, set = 2, binding = 0) uniform TerrainConstants
        {
            vec4 coarseRect;
            vec4 fineRect;
            vec4 region;
            vec3 sunColour;
            float waterHeight;
            vec3 ambientSky;
            float halfWorld;
            vec3 ambientGround;
            float farStart;
            vec2 coarseCells;
            vec2 fineCells;
            vec2 cellGrid;
            float fineBand;
            float farEnd;
            bool hasFine;
            bool wireframe;
            bool heightNormals;
            bool textured;
            bool normalMaps;
            bool hasMaps;
            int mapState;
            int debug;
            bool feature;
            int featureBiome;
            bool hasGround;
            bool hasWorldColour;
            uint heightCoarse;
            uint heightFine;
            uint diffuse;
            uint normal;
            uint params;
            uint cells;
            uint blendMap;
            uint overlay;
            uint colour;
            uint ground;
            uint worldColour;
            bool grid;
        } terrain;

        """;

    /// <summary>The per-draw values of a patch (<see cref="TerrainPush"/> is the C# side; std430): <c>uNode</c> and <c>uMorph</c>.</summary>
    public const string PushMembers = """
            vec4 node;
            vec2 morph;
        """;

    /// <summary>The terrain's uniforms: <see cref="ConstantsBlock"/> members, the textures by bindless index (the array each one's format
    /// selects: the layer arrays in <c>textures2DArray</c>, the integer blend cells in <c>utextures2D</c>), the patch's node in the push block.
    /// The camera, light and fog are <see cref="ViewConstants"/> (<see cref="NativeShaders.ViewMap"/>).</summary>
    static readonly Dictionary<string, string> NativeMap = new()
    {
        ["uHeightCoarse"] = "textures2D[terrain.heightCoarse]", ["uHeightFine"] = "textures2D[terrain.heightFine]",
        ["uCoarseRect"] = "terrain.coarseRect", ["uCoarseCells"] = "terrain.coarseCells", ["uFineRect"] = "terrain.fineRect",
        ["uFineCells"] = "terrain.fineCells", ["uFineBand"] = "terrain.fineBand", ["uHasFine"] = "terrain.hasFine",
        ["uNode"] = "pc.node", ["uMorph"] = "pc.morph",
        ["uSunColour"] = "terrain.sunColour", ["uAmbientSky"] = "terrain.ambientSky", ["uAmbientGround"] = "terrain.ambientGround",
        ["uWireframe"] = "terrain.wireframe", ["uGrid"] = "terrain.grid", ["uHeightNormals"] = "terrain.heightNormals", ["uWaterHeight"] = "terrain.waterHeight",
        ["uTextured"] = "terrain.textured", ["uNormalMaps"] = "terrain.normalMaps", ["uHasMaps"] = "terrain.hasMaps",
        ["uMapState"] = "terrain.mapState", ["uDebug"] = "terrain.debug", ["uFeature"] = "terrain.feature",
        ["uFeatureBiome"] = "terrain.featureBiome", ["uFarStart"] = "terrain.farStart", ["uFarEnd"] = "terrain.farEnd",
        ["uHasGround"] = "terrain.hasGround", ["uHasWorldColour"] = "terrain.hasWorldColour",
        ["uDiffuse"] = "textures2DArray[terrain.diffuse]", ["uNormal"] = "textures2DArray[terrain.normal]",
        ["uParams"] = "textures2D[terrain.params]", ["uCells"] = "utextures2D[terrain.cells]", ["uBlendMap"] = "textures2D[terrain.blendMap]",
        ["uOverlay"] = "textures2D[terrain.overlay]", ["uColour"] = "textures2D[terrain.colour]", ["uGround"] = "textures2D[terrain.ground]",
        ["uWorldColour"] = "textures2D[terrain.worldColour]", ["uRegion"] = "terrain.region", ["uCellGrid"] = "terrain.cellGrid",
        ["uHalfWorld"] = "terrain.halfWorld",
    };

    const string DerivativeControl = "#extension GL_ARB_derivative_control : require\n";

    /// <summary>
    /// The native variant of a terrain text: <see cref="NativeShaders.Port"/> with <see cref="NativeMap"/> and <see cref="PushMembers"/>, and
    /// <see cref="ConstantsBlock"/> after the prelude. Only declarations move: <see cref="Fragment"/>'s <c>#extension</c> line goes up to
    /// right after <c>#version</c> (an extension directive must come before the prelude's declarations).
    /// </summary>
    static string Native(string legacy, string pushMembers = PushMembers)
    {
        const string preludeEnd = "#define gl_VertexID gl_VertexIndex\n";
        string text = NativeShaders.Port(legacy, NativeShaders.Map(NativeMap), pushMembers);
        int at = text.IndexOf(preludeEnd, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("TerrainShaders.Native: the native prelude no longer ends with the gl_VertexID define.");
        text = text.Insert(at + preludeEnd.Length, ConstantsBlock);
        if (text.Contains(DerivativeControl, StringComparison.Ordinal))
            text = text.Replace(DerivativeControl, "", StringComparison.Ordinal).Replace("#version 450\n", "#version 450\n" + DerivativeControl, StringComparison.Ordinal);
        return text;
    }

    /// <summary>The rock bake's extra push members (after <see cref="PushMembers"/>; std430 offsets 32, 48, 64, 80; <see cref="TerrainBakePush"/> is the C# side):
    /// the frame's direction (towards the viewer; w the distance the material is shaded at), its image axes and the output pass.</summary>
    public const string RockBakePushMembers = PushMembers + """

            vec4 bakeDir;
            vec4 bakeRight;
            vec4 bakeUp;
            int bakePass;
        """;

    /// <summary>
    /// <see cref="MeshFragment"/> for the impostor bake of a TERRAIN-mode rock (docs/impostors.md section 13): the same layer model with the biome of the
    /// instance's row 0 w, three changes in a copy (the shader the terrain draws with is untouched): the distance the material fades by is the bake's
    /// (<c>bakeDir.w</c>, the rock's transition distance) instead of the eye's, the material is always fully applied (no window or far fade: those
    /// are applied per pixel at draw time by the impostor), and an output switch before the lighting writes the albedo (pass 0), the shading normal in
    /// the frame's basis (pass 1) or the gloss (pass 2), as <see cref="Impostors.ImpostorShaders.BakeFragmentNative"/> does for the mesh shader.
    /// </summary>
    public static string RockBakeFragmentNative()
    {
        static string Patch(string text, string from, string to)
        {
            int at = text.IndexOf(from, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException($"TerrainShaders.RockBakeFragmentNative: '{from}' not found in the terrain fragment shader; update the patch.");
            return text.Remove(at, from.Length).Insert(at, to);
        }
        string f = MeshFragment;
        f = Patch(f, "float distance = length(vWorld - uEye);", "float distance = pc.bakeDir.w;");
        f = Patch(f, "nearWeight *= 1.0 - smoothstep(uFarStart, uFarEnd, distance);", "nearWeight = 1.0;");
        f = Patch(f, "vec3 l = normalize(uLightDir);", """
            if (pc.bakePass == 0) { fragColour = vec4(albedo.rgb, 1.0); return; }
            if (pc.bakePass == 1)
            {
                vec3 bn = normalize(shadingNormal);
                fragColour = vec4(vec3(dot(bn, pc.bakeRight.xyz), dot(bn, pc.bakeUp.xyz), dot(bn, pc.bakeDir.xyz)) * 0.5 + 0.5, 1.0);
                return;
            }
            fragColour = vec4(0.0, clamp(albedo.a, 0.0, 1.0), 0.0, 1.0);
            return;
            vec3 l = normalize(uLightDir);
            """);
        return Native(f, RockBakePushMembers);
    }

    /// <summary>The vertex stage of the rock bake: <see cref="MeshVertex"/> with the bake's push block (both stages declare the same members).</summary>
    public static string RockBakeVertexNative() => Native(MeshVertex, RockBakePushMembers);

    public static string PatchVertexNative() => Native(PatchVertex);
    public static string FragmentNative() => Native(Fragment);
    public static string MeshVertexNative() => Native(MeshVertex);
    public static string MeshFragmentNative() => Native(MeshFragment);
    public static string MeshInstancedDepthVertexNative() => Native(MeshInstancedDepthVertex);
    /// <summary><see cref="ShadowShaders.DepthFragment"/> (the caster block at set 0, binding 2) with the terrain's push block, so both
    /// stages of a depth program declare the same one.</summary>
    public static string DepthFragmentNative() => Native(ShadowShaders.DepthFragment);
}

/// <summary>The C# side of <see cref="TerrainShaders.ConstantsBlock"/> (std140; offsets checked against the reflection by a test). GLSL bools
/// are 32-bit (0 / 1).</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 224)]
struct TerrainConstants
{
    [System.Runtime.InteropServices.FieldOffset(0)] public System.Numerics.Vector4 CoarseRect;
    [System.Runtime.InteropServices.FieldOffset(16)] public System.Numerics.Vector4 FineRect;
    [System.Runtime.InteropServices.FieldOffset(32)] public System.Numerics.Vector4 Region;
    [System.Runtime.InteropServices.FieldOffset(48)] public System.Numerics.Vector3 SunColour;
    [System.Runtime.InteropServices.FieldOffset(60)] public float WaterHeight;
    [System.Runtime.InteropServices.FieldOffset(64)] public System.Numerics.Vector3 AmbientSky;
    [System.Runtime.InteropServices.FieldOffset(76)] public float HalfWorld;
    [System.Runtime.InteropServices.FieldOffset(80)] public System.Numerics.Vector3 AmbientGround;
    [System.Runtime.InteropServices.FieldOffset(92)] public float FarStart;
    [System.Runtime.InteropServices.FieldOffset(96)] public System.Numerics.Vector2 CoarseCells;
    [System.Runtime.InteropServices.FieldOffset(104)] public System.Numerics.Vector2 FineCells;
    [System.Runtime.InteropServices.FieldOffset(112)] public System.Numerics.Vector2 CellGrid;
    [System.Runtime.InteropServices.FieldOffset(120)] public float FineBand;
    [System.Runtime.InteropServices.FieldOffset(124)] public float FarEnd;
    [System.Runtime.InteropServices.FieldOffset(128)] public uint HasFine;
    [System.Runtime.InteropServices.FieldOffset(132)] public uint Wireframe;
    [System.Runtime.InteropServices.FieldOffset(136)] public uint HeightNormals;
    [System.Runtime.InteropServices.FieldOffset(140)] public uint Textured;
    [System.Runtime.InteropServices.FieldOffset(144)] public uint NormalMaps;
    [System.Runtime.InteropServices.FieldOffset(148)] public uint HasMaps;
    [System.Runtime.InteropServices.FieldOffset(152)] public int MapState;
    [System.Runtime.InteropServices.FieldOffset(156)] public int Debug;
    [System.Runtime.InteropServices.FieldOffset(160)] public uint Feature;
    [System.Runtime.InteropServices.FieldOffset(164)] public int FeatureBiome;
    [System.Runtime.InteropServices.FieldOffset(168)] public uint HasGround;
    [System.Runtime.InteropServices.FieldOffset(172)] public uint HasWorldColour;
    [System.Runtime.InteropServices.FieldOffset(176)] public uint HeightCoarse;
    [System.Runtime.InteropServices.FieldOffset(180)] public uint HeightFine;
    [System.Runtime.InteropServices.FieldOffset(184)] public uint Diffuse;
    [System.Runtime.InteropServices.FieldOffset(188)] public uint Normal;
    [System.Runtime.InteropServices.FieldOffset(192)] public uint Params;
    [System.Runtime.InteropServices.FieldOffset(196)] public uint Cells;
    [System.Runtime.InteropServices.FieldOffset(200)] public uint BlendMap;
    [System.Runtime.InteropServices.FieldOffset(204)] public uint Overlay;
    [System.Runtime.InteropServices.FieldOffset(208)] public uint Colour;
    [System.Runtime.InteropServices.FieldOffset(212)] public uint Ground;
    [System.Runtime.InteropServices.FieldOffset(216)] public uint WorldColour;
    [System.Runtime.InteropServices.FieldOffset(220)] public uint Grid;
}

/// <summary>The C# side of <see cref="TerrainShaders.PushMembers"/> (std430 push constants): a patch's <c>uNode</c> and <c>uMorph</c>.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 24)]
struct TerrainPush
{
    [System.Runtime.InteropServices.FieldOffset(0)] public System.Numerics.Vector4 Node;
    [System.Runtime.InteropServices.FieldOffset(16)] public System.Numerics.Vector2 Morph;
}

/// <summary>The C# side of <see cref="TerrainShaders.RockBakePushMembers"/> (std430 push constants, 84 bytes; the node and morph at 0 and 16 are unused): a bake draw's frame.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 96)]
struct TerrainBakePush
{
    [System.Runtime.InteropServices.FieldOffset(32)] public System.Numerics.Vector4 Dir;
    [System.Runtime.InteropServices.FieldOffset(48)] public System.Numerics.Vector4 Right;
    [System.Runtime.InteropServices.FieldOffset(64)] public System.Numerics.Vector4 Up;
    [System.Runtime.InteropServices.FieldOffset(80)] public int Pass;
}
