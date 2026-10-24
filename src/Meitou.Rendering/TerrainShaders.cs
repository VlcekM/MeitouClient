namespace Meitou.Rendering;

/// <summary>
/// GLSL for the world view's terrain (OpenGL 3.3 core). The layer model follows the facts in
/// docs/formats/terrain.md ("How the terrain is textured"): per biome six layers (base, slope, cliff, grass,
/// dirt, road) chosen by slope and the overlay map, tinted by the colour map, up to four biomes per pixel
/// weighted by the blend map. Far away (beyond the game's material distance) and outside the loaded region the
/// terrain takes the biomes' blended ground colour, like the game's distant terrain.
/// </summary>
static partial class TerrainShaders
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
            float w = fineWeight(p);
            // Inside the fine window (weight 1) the coarse grid is not read: mix(c, f, 1) is f, and the window covers nearly all the picture's terrain.
            if (w >= 1.0) return gridHeight(uHeightFine, p, uFineRect, uFineCells);
            float c = gridHeight(uHeightCoarse, p, uCoarseRect, uCoarseCells);
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
            vNormal = vWorld;   // patches: the undisplaced point (the material's coordinates, as the tessellated twin's); the normal comes from the height field
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    /// <summary>A layer's relief depth and height (GroundRelief, docs/render-terrain.md "Ground relief"), shared by the displacement's stages and the fragment.</summary>
    const string ReliefDepth = """

        uniform sampler2D uRelief;     // per layer slot: 1 / (side × its height's steepness), 0 flat (TerrainTextures.LoadHeight)
        uniform float uMaxDepth;       // GroundRelief.MaxDepth over ReferenceDepth: the most a layer's depth gives
        uniform float uTargetSlope;    // GroundRelief.TargetSlope: the root-mean-square slope a layer is given at its tile's size
        uniform float uScaleFollow;    // GroundRelief.ScaleFollow: the power that depth is raised to (0 every layer as deep, 1 in proportion)
        uniform float uCliffScale;     // GroundRelief.CliffScale: the cliff layer's depth times this (its cap too)

        // A layer's depth as a multiple of the reference depth (GroundRelief.ReferenceDepth, 2 units; GroundRelief.LayerDepthCm); 0 flat: the depth
        // that gives it a root-mean-square slope of uTargetSlope over its tile (5000 / scale units across), raised to uScaleFollow, at most uMaxDepth.
        float reliefDepth(float layer, vec2 scale)
        {
            float r = texelFetch(uRelief, ivec2(int(layer), 0), 0).r;
            if (r <= 0.0) return 0.0;
            float tile = 5000.0 / max(0.5 * (scale.x + scale.y), 1e-3);
            return min(pow(uTargetSlope * r * tile / 2.0, uScaleFollow), uMaxDepth);
        }

        // The texture field (the terrain sandbox, GroundRelief.FieldCentre): square tiles from uFieldOrigin, uFieldColumns a row, each one biome row
        // and the layer it shows (TerrainLayer) in uFieldTiles (R row, G layer, B 1 once the biome is resident).
        uniform usampler2D uFieldTiles;
        uniform vec2 uFieldOrigin;
        uniform float uFieldTile;
        uniform int uFieldColumns;
        uniform int uFieldCount;

        // The field's tile at xz: its biome row and layer (x -1 outside the field or while the biome loads), and inside: 0 at the tile's edge, 1 from
        // 8% of its side in (the relief fades out to the edge, so neighbouring tiles meet on the flat ground).
        ivec2 fieldTile(vec2 xz, out float inside)
        {
            inside = 1.0;
            if (uFieldCount <= 0) return ivec2(-1);
            vec2 t = (xz - uFieldOrigin) / uFieldTile;
            ivec2 c = ivec2(floor(t));
            int i = c.y * uFieldColumns + c.x;
            if (c.x < 0 || c.y < 0 || c.x >= uFieldColumns || i >= uFieldCount) return ivec2(-1);
            uvec4 e = texelFetch(uFieldTiles, ivec2(i, 0), 0);
            if (e.b == 0u) return ivec2(-1);
            vec2 f = fract(t);
            inside = smoothstep(0.0, 0.08, min(min(f.x, 1.0 - f.x), min(f.y, 1.0 - f.y)));
            return ivec2(int(e.r), int(e.g));
        }

        // A field tile's layer as the material's weights: the overlay (R/G grass, B dirt, A road) and the slope (x) and cliff (y) weights.
        void fieldMask(int layer, inout vec4 map, inout vec4 w)
        {
            map = vec4(0.0);
            w = vec4(0.0);
            if (layer == 1) w.x = 1.0;
            else if (layer == 2) w.y = 1.0;
            else if (layer == 3) map.r = 1.0;
            else if (layer == 4) map.b = 1.0;
            else if (layer == 5) map.a = 1.0;
        }
        """;

    /// <summary>
    /// The ground displacement (GroundRelief, docs/render-terrain.md "Ground relief"): how far the ground is moved at a point, from the biome
    /// layers the material shows there. Only a layer with a height (its depth factor is not 0) moves it, by its height about the middle (half up, half
    /// down: what stands on the ground sits level with the bumps) times its depth factor and how much of the pixel it shows: the base layer, and with
    /// <c>uOverlayRelief</c> the grass, slope, dirt and road layers at that share (not the cliff), weighted by the biome blend. <c>reliefMove(p, false, ...)</c>
    /// is that coverage alone (the control stage's test).
    /// </summary>
    const string ReliefFunctions = ReliefDepth + """

        uniform bool uTextured;
        uniform bool uHasMaps;
        uniform int uMapState;
        uniform sampler2DArray uHeight;
        uniform sampler2D uParams;
        uniform usampler2D uCells;
        uniform sampler2D uBlendMap;
        uniform sampler2D uOverlay;
        uniform vec4 uRegion;
        uniform vec2 uCellGrid;
        uniform float uHalfWorld;
        uniform float uDisplace;       // world units from the lowest point to the highest, centred on the ground; 0 off
        uniform float uDisplaceEnd;    // GroundRelief.DisplaceDistance: moved in full to 60% of it, none from it
        uniform float uBroadCut;       // GroundRelief.BroadCut: the displacement leaves out shapes wider than this (world units; 0 keeps all)
        uniform float uRefine;         // GroundRelief.RefinePixels: the terrain's deviation from an edge, in pixels, past which the edge is split (0 off)
        uniform float uPixelScale;     // pixels per radian of the picture (TerrainPixelScale)
        uniform float uOverlayRelief;  // GroundRelief.OverlayRelief: the grass, slope, dirt and road layers' share of relief (0: the base layer alone)
        uniform float uTessDetail;     // GroundRelief.TessDetail: the segments' length divided by it
        uniform vec3 uEye;

        // Segments of at least TessMinEdge, or TessPixelAngle times the distance (both over uTessDetail).
        const float TessMinEdge = 0.6, TessPixelAngle = 0.004, TessMax = 64.0;

        vec4 reliefP(uint b, int k) { return texelFetch(uParams, ivec2(k, int(b)), 0); }

        // One layer's term of reliefMove's ground: its share of the pixel times its depth factor, times its height about the middle (height) or not. The height is
        // read at the level whose texels are half a segment apart (segment: the mesh's spacing there, world units; 0 the finest), so what the mesh
        // cannot carry is left out rather than aliased into spikes.
        float reliefLayer(vec2 xz, float layer, vec2 scale, float share, bool height, float segment)
        {
            if (share <= 0.0) return 0.0;
            float depth = reliefDepth(layer, scale);
            if (depth <= 0.0) return 0.0;   // flat: no relief (no normal map)
            if (!height) return share * depth;
            float texelsPerUnit = 0.5 * (scale.x + scale.y) * float(textureSize(uHeight, 0).x) / 5000.0;
            vec3 c = vec3(xz / 5000.0 * scale, layer);
            float lod = segment > 0.0 ? max(0.0, log2(2.0 * segment * texelsPerUnit)) : 0.0;
            // The broad shapes left out (the height less its level whose texels are half the cut): what stands on the ground (the characters walk on the
            // undisplaced surface) is not swallowed by a swell; the bumps and grain stay.
            float broad = uBroadCut > 0.0 ? textureLod(uHeight, c, max(0.0, log2(0.5 * uBroadCut * texelsPerUnit))).r : 0.5;
            return share * depth * (textureLod(uHeight, c, lod).r - broad);
        }

        // The relief at p (one pass for both, one blend lookup and one normal): ground, how far the ground's layers lift it (upward; reliefLayer), and
        // cliff, how far the cliff layer moves it along the ground's normal n, from its two vertical projections blended as the material blends them
        // (a texel's height is the same however the texture is laid: cracks stay low), each by how much of the pixel it shows and the biome blend.
        // height false: those coverages alone (the vertex stage's test for the control stage).
        void reliefMove(vec3 p, bool height, float segment, out float ground, out float cliff, out vec3 n)
        {
            ground = 0.0;
            cliff = 0.0;
            n = terrainNormal(p.xz);
            if (!uTextured) return;
            vec2 xz = p.xz;
            vec2 world01 = (xz + uHalfWorld) / (2.0 * uHalfWorld);
            ivec2 cell = clamp(ivec2(floor(world01 * uCellGrid)), ivec2(0), ivec2(uCellGrid) - 1);
            uvec4 slots = texelFetch(uCells, ivec2(cell.x * 2, cell.y), 0);
            uint slot4 = texelFetch(uCells, ivec2(cell.x * 2 + 1, cell.y), 0).r;
            vec4 weights = textureLod(uBlendMap, world01, 0.0);
            float rest = max(0.0, 1.0 - dot(weights, vec4(1.0)));
            vec4 overlay = vec4(0.0);
            if (uHasMaps && uMapState == 2)
            {
                overlay = textureLod(uOverlay, fract((xz + uHalfWorld) * uRegion.zw), 0.0);
                overlay.r = max(overlay.r, overlay.g);
            }
            float slope = 1.0 - n.y;
            vec2 cb = max(abs(normalize(n.xz + vec2(1e-5))) - 0.2, vec2(0.0)) * 7.0;
            cb *= cb;
            cb /= max(cb.x + cb.y, 1e-5);
            if (n.y > 0.995) cb = vec2(0.5);
            float inside;
            ivec2 field = fieldTile(xz, inside);
            if (field.x >= 0) { slots = uvec4(uint(field.x), 255u, 255u, 255u); slot4 = 255u; weights = vec4(1.0, 0.0, 0.0, 0.0); rest = 0.0; }
            vec2 uv = xz / 5000.0;
            float texels = float(textureSize(uHeight, 0).x);
            float total = 0.0;
            for (int k = 0; k < 5; k++)
            {
                uint b = k < 4 ? slots[k] : slot4;
                float wk = k < 4 ? weights[k] : rest;
                if (b >= 254u || wk < 0.004) continue;
                total += wk;
                vec4 map = overlay;
                vec4 smin = reliefP(b, 5), smax = reliefP(b, 6), sblend = reliefP(b, 7);
                vec4 w = smoothstep(smin - sblend, smin, vec4(slope)) * smoothstep(smax + sblend, smax, vec4(slope));
                if (field.x >= 0) fieldMask(field.y, map, w);
                // What each layer shows, in the material's order: base, grass, slope, dirt, road, the cliff over all.
                float top = 1.0 - w.y, aboveDirt = top * (1.0 - map.a), aboveSlope = aboveDirt * (1.0 - map.b);
                vec4 la = reliefP(b, 0), lb = reliefP(b, 1), sA = reliefP(b, 2), sB = reliefP(b, 3), sC = reliefP(b, 4);
                if (top > 0.0)
                {
                    float sum = reliefLayer(xz, la.x, sB.xy, aboveSlope * (1.0 - w.x) * (1.0 - map.r), height, segment);
                    if (uOverlayRelief > 0.0)
                        sum += reliefLayer(xz, la.w, sB.zw, uOverlayRelief * aboveSlope * (1.0 - w.x) * map.r, height, segment)
                            + reliefLayer(xz, la.y, sA.xy, uOverlayRelief * aboveSlope * w.x, height, segment)
                            + reliefLayer(xz, lb.x, sC.xy, uOverlayRelief * aboveDirt * map.b, height, segment)
                            + reliefLayer(xz, lb.y, sC.zw, uOverlayRelief * top * map.a, height, segment);
                    ground += wk * sum;
                }
                if (w.y <= 0.0) continue;
                float depth = reliefDepth(la.z, sA.zw) * uCliffScale;
                if (depth <= 0.0) continue;
                if (!height) { cliff += wk * w.y * depth; continue; }
                vec4 distort = reliefP(b, 10);
                float vert = 1.0 - p.y / 5000.0 + (cos(p.x * distort.x) + cos(p.z * distort.x)) * distort.y;
                // reliefLayer's level: texels half a segment apart.
                float lod = segment > 0.0 ? max(0.0, log2(2.0 * segment * 0.5 * (sA.z + sA.w) * texels / 5000.0)) : 0.0;
                float hx = textureLod(uHeight, vec3(vec2(uv.y, vert) * sA.zw, la.z), lod).r;
                float hz = textureLod(uHeight, vec3(vec2(uv.x, vert) * sA.zw, la.z), lod).r;
                cliff += wk * w.y * depth * (cb.x * hx + cb.y * hz - 0.5);
            }
            if (total > 0.0) { ground *= inside / total; cliff *= inside / total; }
        }
        """;

    /// <summary><see cref="PatchVertex"/> for the tessellated patches: the same morphed grid point, to the control stage (no clip position).</summary>
    public const string PatchVertexTess = "#version 330 core\n" + HeightFunctions + ReliefFunctions + """

        layout(location = 0) in vec2 aGrid;
        uniform vec4 uNode;
        uniform vec2 uMorph;
        layout(location = 0) out vec3 vcWorld;
        layout(location = 1) out float vcCover;   // whether relief lies here (reliefMove's coverage): the control stage splits an edge only then
        void main()
        {
            vec2 p = uNode.xy + aGrid * (uNode.z / uNode.w);
            float d = distance(vec3(p.x, terrainHeight(p), p.y), uEye);
            float k = uMorph.x >= 1e30 ? 0.0 : clamp((d - uMorph.x) / (uMorph.y - uMorph.x), 0.0, 1.0);
            vec2 g = aGrid - mod(aGrid, 2.0) * k;
            p = uNode.xy + g * (uNode.z / uNode.w);
            vcWorld = vec3(p.x, terrainHeight(p), p.y);
            float ground, cliff;
            vec3 n;
            vcCover = 0.0;
            if (uDisplace > 0.0 && distance(vcWorld, uEye) < uDisplaceEnd + 50.0)
            {
                reliefMove(vcWorld, false, 0.0, ground, cliff, n);
                vcCover = ground + cliff;
            }
        }
        """;

    /// <summary>
    /// The control stage: each edge's level from the edge alone (its length, its midpoint's distance, whether relief lies at its ends or middle), so
    /// the two triangles sharing an edge split it alike and nothing cracks; 1 (not split) where there is no relief or beyond the displacement.
    /// </summary>
    public const string PatchControl = "#version 330 core\n" + HeightFunctions + ReliefFunctions + """

        layout(vertices = 3) out;
        layout(location = 0) in vec3 vcWorld[];
        layout(location = 1) in float vcCover[];
        layout(location = 0) out vec3 teWorld[];

        // The refinement (GroundRelief.RefinePixels): how far the terrain leaves the straight edge (at its quarters and middle), in pixels; a split in n
        // takes a smooth curve's deviation down by about n², so the level is the root of that over the tolerance.
        float refineLevel(vec3 a, vec3 b, float d)
        {
            if (uRefine <= 0.0) return 1.0;
            float dev = 0.0;
            for (int k = 1; k <= 3; k++)
            {
                vec3 q = mix(a, b, float(k) * 0.25);
                dev = max(dev, abs(terrainHeight(q.xz) - q.y));
            }
            return clamp(sqrt(dev * uPixelScale / max(d, 1.0) / uRefine), 1.0, 16.0);
        }

        // An edge's level from the edge alone (its ends, length, distance), so both triangles sharing it split it alike: the relief's where the ground is
        // displaced and relief lies at its ends, the refinement's everywhere.
        float edgeLevel(int i, int j)
        {
            vec3 a = vcWorld[i], b = vcWorld[j];
            float d = distance((a + b) * 0.5, uEye);
            float level = refineLevel(a, b, d);
            if (uDisplace <= 0.0 || d >= uDisplaceEnd || max(vcCover[i], vcCover[j]) <= 0.0) return level;
            return max(level, clamp(distance(a, b) * uTessDetail / max(TessMinEdge, d * TessPixelAngle), 1.0, TessMax));
        }

        void main()
        {
            teWorld[gl_InvocationID] = vcWorld[gl_InvocationID];
            if (gl_InvocationID == 0)
            {
                // Outer level i is the edge facing control point i (where its coordinate is 0).
                float e0 = edgeLevel(1, 2), e1 = edgeLevel(2, 0), e2 = edgeLevel(0, 1);
                gl_TessLevelOuter[0] = e0;
                gl_TessLevelOuter[1] = e1;
                gl_TessLevelOuter[2] = e2;
                gl_TessLevelInner[0] = max(e0, max(e1, e2));
            }
        }
        """;

    /// <summary>
    /// The evaluation stage: the point on the flat triangle (the base surface is interpolated, not sampled again, so an unraised split edge is the
    /// plain patch's edge), moved by the ground's height, fading out from 60% of <c>uDisplaceEnd</c> to it. Its outputs are the patch
    /// vertex stage's, so the terrain's fragment stage is unchanged.
    /// </summary>
    public const string PatchEvaluation = "#version 330 core\n" + HeightFunctions + ReliefFunctions + """

        layout(triangles, fractional_odd_spacing, ccw) in;
        layout(location = 0) in vec3 teWorld[32];   // gl_MaxPatchVertices, written out: InterfaceLocations sizes every array it reads
        uniform mat4 uViewProjection;
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vCliffBlend;
        invariant gl_Position;
        void main()
        {
            vec3 p = gl_TessCoord.x * teWorld[0] + gl_TessCoord.y * teWorld[1] + gl_TessCoord.z * teWorld[2];
            // The refinement: the new point on the terrain, not on the flat triangle (an unsplit edge keeps its ends: the neighbours meet there).
            if (uRefine > 0.0) p.y = terrainHeight(p.xz);
            vNormal = p;   // the undisplaced point: the material's coordinates are read there, so the texture stays on the moved surface (Fragment's texWorld)
            float d = distance(p, uEye), fade = 1.0 - smoothstep(0.6 * uDisplaceEnd, uDisplaceEnd, d);
            // The mesh's spacing here (PatchControl's edge levels): the height is read no finer than it can carry.
            if (uDisplace > 0.0 && fade > 0.0)
            {
                float segment = max(TessMinEdge, d * TessPixelAngle) / uTessDetail;
                // From the flat triangle's point: the ground's upward, the cliff's along the ground's normal (its height runs up the face).
                float ground, cliff;
                vec3 n;
                reliefMove(p, true, segment, ground, cliff, n);
                p += (vec3(0.0, ground, 0.0) + n * cliff) * uDisplace * fade;
            }
            vWorld = p;
            vCliffBlend = vec2(0.5);
            gl_Position = uViewProjection * vec4(p, 1.0);
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

    /// <summary>
    /// <see cref="MeshVertex"/> for the tessellated TERRAIN-mode meshes (RockSmoothing, docs/render-terrain.md "Rocks"), to the control stage (no clip
    /// position): also the geometry normal the triangles curve to, the tangent slot's xyz where its w is RockSmoothing.Tag (zero otherwise: not curved).
    /// The colour and the shadow programs share it, so the casters curve as the surface does.
    /// </summary>
    public const string MeshVertexTess = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 3) in vec4 aTangent;
        layout(location = 7) in vec4 aModel0;
        layout(location = 8) in vec4 aModel1;
        layout(location = 9) in vec4 aModel2;
        layout(location = 10) in vec4 aModel3;
        layout(location = 0) out vec3 vcWorld;
        layout(location = 1) out vec3 vcNormal;
        layout(location = 2) out vec3 vcCurve;
        layout(location = 3) out vec2 vcCliffBlend;
        layout(location = 4) flat out int vcFeatureBiome;
        layout(location = 5) flat out float vcFade;
        void main()
        {
            mat4 model = mat4(vec4(aModel0.xyz, 0.0), aModel1, aModel2, aModel3);
            vcFeatureBiome = int(aModel0.w);
            vcFade = aModel1.w;
            vcWorld = (model * vec4(aPosition, 1.0)).xyz;
            mat3 normalMatrix = transpose(inverse(mat3(model)));
            vcNormal = normalMatrix * aNormal;
            vcCurve = aTangent.w > 1.5 ? normalize(normalMatrix * aTangent.xyz) : vec3(0.0);
            vec3 n = normalize(vcNormal);
            vec2 cb = max(abs(normalize(n.xz + vec2(1e-6))) - 0.2, vec2(0.0)) * 7.0;
            cb *= cb;
            cb /= max(cb.x + cb.y, 1e-6);
            if (n.y > 0.9) cb = vec2(1.0, 0.0);
            vcCliffBlend = cb;
        }
        """;

    /// <summary>
    /// The meshes' control stage: each edge split by how far its Phong curve leaves the straight edge (a quarter of the ends' normals' components along it,
    /// times <c>uRockRound</c>) against what a pixel or so spans at its midpoint, from the edge alone, so the two triangles sharing it split it alike.
    /// Not split where an end has no geometry normal or the edge lies flat.
    /// </summary>
    public const string MeshControl = """
        #version 330 core
        layout(vertices = 3) out;
        layout(location = 0) in vec3 vcWorld[];
        layout(location = 1) in vec3 vcNormal[];
        layout(location = 2) in vec3 vcCurve[];
        layout(location = 3) in vec2 vcCliffBlend[];
        layout(location = 4) flat in int vcFeatureBiome[];
        layout(location = 5) flat in float vcFade[];
        layout(location = 0) out vec3 teWorld[];
        layout(location = 1) out vec3 teNormal[];
        layout(location = 2) out vec3 teCurve[];
        layout(location = 3) out vec2 teCliffBlend[];
        layout(location = 4) out int teFeatureBiome[];
        layout(location = 5) out float teFade[];
        uniform vec3 uEye;
        uniform float uRockRound;      // RockSmoothing.Roundness: the curve's share (MeshEvaluation's)
        uniform float uTessDetail;     // GroundRelief.TessDetail: the deviation a segment may keep divided by it
        // The deviation a segment may keep in radians of the view, and the most segments an edge takes.
        const float SmoothPixelAngle = 0.0015, SmoothMax = 16.0;

        float edgeLevel(int i, int j)
        {
            vec3 a = vcWorld[i], b = vcWorld[j], e = b - a;
            if (dot(vcCurve[i], vcCurve[i]) < 0.25 || dot(vcCurve[j], vcCurve[j]) < 0.25) return 1.0;
            float bend = uRockRound * 0.25 * length(dot(e, vcCurve[j]) * vcCurve[j] - dot(e, vcCurve[i]) * vcCurve[i]);
            float pixel = distance((a + b) * 0.5, uEye) * SmoothPixelAngle / uTessDetail;
            return clamp(sqrt(bend / max(pixel, 1e-4)), 1.0, SmoothMax);
        }

        void main()
        {
            teWorld[gl_InvocationID] = vcWorld[gl_InvocationID];
            teNormal[gl_InvocationID] = vcNormal[gl_InvocationID];
            teCurve[gl_InvocationID] = vcCurve[gl_InvocationID];
            teCliffBlend[gl_InvocationID] = vcCliffBlend[gl_InvocationID];
            teFeatureBiome[gl_InvocationID] = vcFeatureBiome[gl_InvocationID];
            teFade[gl_InvocationID] = vcFade[gl_InvocationID];
            if (gl_InvocationID == 0)
            {
                // Outer level i is the edge facing control point i (where its coordinate is 0).
                float e0 = edgeLevel(1, 2), e1 = edgeLevel(2, 0), e2 = edgeLevel(0, 1);
                gl_TessLevelOuter[0] = e0;
                gl_TessLevelOuter[1] = e1;
                gl_TessLevelOuter[2] = e2;
                gl_TessLevelInner[0] = max(e0, max(e1, e2));
            }
        }
        """;

    /// <summary>The Phong curve (Boubekeur and Alexa): the point on the flat triangle, pulled toward its projections on the corners' tangent planes.</summary>
    const string MeshCurve = """

        layout(triangles, fractional_odd_spacing, ccw) in;
        layout(location = 0) in vec3 teWorld[32];   // gl_MaxPatchVertices, written out: InterfaceLocations sizes every array it reads
        layout(location = 2) in vec3 teCurve[32];
        uniform mat4 uViewProjection;
        uniform float uRockRound;      // RockSmoothing.Roundness, MeshControl's
        invariant gl_Position;

        vec3 curvedPoint()
        {
            vec3 b = gl_TessCoord;
            vec3 p = b.x * teWorld[0] + b.y * teWorld[1] + b.z * teWorld[2];
            vec3 q = b.x * (p - dot(p - teWorld[0], teCurve[0]) * teCurve[0])
                + b.y * (p - dot(p - teWorld[1], teCurve[1]) * teCurve[1])
                + b.z * (p - dot(p - teWorld[2], teCurve[2]) * teCurve[2]);
            return mix(p, q, uRockRound);
        }
        """;

    /// <summary>The meshes' evaluation stage: the curved point, and <see cref="MeshVertex"/>'s outputs interpolated as the rasteriser would.</summary>
    public const string MeshEvaluation = "#version 330 core\n" + MeshCurve + """

        layout(location = 1) in vec3 teNormal[32];
        layout(location = 3) in vec2 teCliffBlend[32];
        layout(location = 4) in int teFeatureBiome[32];
        layout(location = 5) in float teFade[32];
        out vec3 vWorld;
        out vec3 vNormal;
        out vec2 vCliffBlend;
        flat out int vFeatureBiome;
        flat out float vFade;
        void main()
        {
            vec3 b = gl_TessCoord;
            vWorld = curvedPoint();
            vNormal = b.x * teNormal[0] + b.y * teNormal[1] + b.z * teNormal[2];
            vCliffBlend = b.x * teCliffBlend[0] + b.y * teCliffBlend[1] + b.z * teCliffBlend[2];
            vFeatureBiome = teFeatureBiome[0];
            vFade = teFade[0];
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    /// <summary>The shadow casters' evaluation stage: the same curved point (<see cref="MeshEvaluation"/>), the position alone.</summary>
    public const string MeshEvaluationDepth = "#version 330 core\n" + MeshCurve + """

        void main() { gl_Position = uViewProjection * vec4(curvedPoint(), 1.0); }
        """;

    public static readonly string Fragment = "#version 330 core\n#extension GL_ARB_derivative_control : require\n" + HeightFunctions + ReliefDepth + AtmosphereShaders.Functions + """

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
        uniform float uCliffEps;       // the cliff weights up to this are dropped, the next as much ramp in (0: every nonzero weight counts)

        uniform sampler2DArray uDiffuse;
        uniform sampler2DArray uNormal;
        uniform sampler2DArray uHeight;   // the layers' heights (GroundRelief): R from their normal maps, G from their diffuse; 1 the top, flat 1 without one
        uniform float uParallax;       // ground parallax: the depth of the height range in world units (1 unit = 10 cm); 0 off
        uniform float uDisplace;       // ground displacement (tessellated patches near the eye) in world units; 0 off. The parallax gives way to it there.
        uniform float uDisplaceEnd;    // GroundRelief.DisplaceDistance: displaced in full to 60% of it
        uniform float uParallaxEnd;    // GroundRelief.ParallaxDistance: the parallax in full to half of it, none from it
        uniform float uTessDetail;     // GroundRelief.TessDetail (PatchControl's segments)
        uniform float uOverlayRelief;  // GroundRelief.OverlayRelief: the grass, slope, dirt and road layers' share of relief (0: the base layer alone)
        uniform float uSelfShadow;     // GroundRelief.SelfShadow: the self-shadow's darkening
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
        uniform sampler2DArray uBlood; // the blood decal cells near the eye (TerrainDecalRenderer): one layer each
        uniform int uBloodCount;       // cells in use (0: no blood)
        uniform float uBloodCellSize;
        uniform vec4 uBloodCells[32];  // column, row, layer

        vec4 P(int biome, int k) { return texelFetch(uParams, ivec2(k, biome), 0); }

        struct Surface { vec4 albedo; vec4 normal; float absorb; float shade; };

        // Where the material is read: the patches' undisplaced point (vNormal carries it; the relief moves vWorld, and a cliff's projections would slide
        // with it), the meshes' own position. Set by main before biome().
        vec3 texWorld;

        int fieldLayer = -1;   // the texture field's layer at this pixel (fieldTile), -1 outside it; set by main before biome()   // shade: the relief's own shadow on the sun (1 none)

        vec4 tex(sampler2DArray s, vec2 uv, float layer) { return texture(s, vec3(uv, layer)); }

        // A layer's coordinate with its screen derivatives, taken before the layer branches below (derivatives inside a branch
        // that only some pixels of a 2 x 2 quad take are undefined), so a skipped neighbour cannot change a sampled pixel's mip.
        struct GradCoord { vec2 p, dx, dy; };
        GradCoord gradCoord(vec2 p) { GradCoord c; c.p = p; c.dx = dFdxCoarse(p); c.dy = dFdyCoarse(p); return c; }
        vec4 tex(sampler2DArray s, GradCoord c, float layer) { return textureGrad(s, vec3(c.p, layer), c.dx, c.dy); }

        // A layer weight with its small values dropped: 0 up to eps, rising smoothly to the weight itself at twice eps (continuous, so no seam where a
        // layer stops being sampled); eps 0 is the weight unchanged. docs/render-terrain.md, "Layer weight threshold".
        float keepWeight(float x, float eps) { return eps > 0.0 ? x * smoothstep(eps, 2.0 * eps, x) : x; }

        // Layers whose relief is marched together (GroundRelief): their common unscaled coordinate with its derivatives, and per layer its tiling (xy: the
        // coordinate's scale, also its move per unit of the march's offset), array layer (z) and weight (w: its share of the pixel times its depth factor).
        // Kept this small on purpose: the march carries it through every step, and a larger one (a coordinate and derivatives per layer) cost the
        // parallax several times over in registers (docs/render-terrain.md, "Cost").
        const int ReliefMax = 5;
        struct Relief { GradCoord base; vec4 layer[ReliefMax]; int n; float total; };

        Relief noRelief(GradCoord base) { Relief r; r.base = base; r.n = 0; r.total = 0.0; return r; }

        void addRelief(inout Relief r, vec2 scale, float layer, float share)
        {
            // A layer showing under 5% of the pixel is left out, fading in to 15% (continuous: no seam where it drops out); the march's cost is per layer.
            float w = share * smoothstep(0.05, 0.15, share) * reliefDepth(layer, scale);
            if (w <= 0.0 || r.n >= ReliefMax) return;
            r.layer[r.n] = vec4(scale, layer, w);
            r.n++;
            r.total += w;
        }

        // Where the ground is displaced (ReliefFunctions), the mesh carries the relief down to its spacing there (reliefSegment, world units; 0 not
        // displaced); the view ray marches what is finer, the height less its level at that spacing, as much as 1 - reliefWhole (all of it again where the
        // displacement fades out). Set per pixel in biome().
        float reliefSegment = 0.0, reliefWhole = 1.0;

        // The march's steps (view ray, sun), fewer with distance (set in biomeRelief(): 16 and 8 near, 6 and 3 at the parallax's end), and the share of
        // the pixel the biome being marched has (main sets it before biomeRelief(): a biome showing little is not marched).
        int reliefSteps = 16, reliefShadowSteps = 12;
        float biomeShare = 1.0;

        // The relief's height at offset o (1 the top): the layers' heights by weight; finer: only the detail the displaced mesh leaves out (above).
        // Sampled with the unmoved coordinates' derivatives, so the mip does not jump.
        // blur: the derivatives' scale (the sun's march reads 4: two levels coarser, its shadow is soft, and the coarser level is far cheaper to fetch).
        float reliefHeight(Relief r, vec2 o, bool finer, float blur)
        {
            float h = 0.0;
            float texels = float(textureSize(uHeight, 0).x);
            for (int i = 0; i < r.n; i++)
            {
                vec4 l = r.layer[i];
                vec3 c = vec3((r.base.p + o) * l.xy, l.z);
                float x = textureGrad(uHeight, c, r.base.dx * l.xy * blur, r.base.dy * l.xy * blur).r;
                if (finer && reliefSegment > 0.0 && reliefWhole < 1.0)
                {
                    // ReliefFunctions' level for this spacing: texels half a segment apart.
                    float lod = max(0.0, log2(2.0 * reliefSegment * 0.5 * (l.x + l.y) * texels / 5000.0));
                    x = mix(clamp(x - textureLod(uHeight, c, lod).r + 0.5, 0.0, 1.0), x, reliefWhole);
                }
                h += l.w * x;
            }
            return h / r.total;
        }

        // The relief march (GroundRelief, docs/render-terrain.md): the offset along the view ray to where it meets the height (viewSpan: the ray's move
        // over the full depth), then a march toward the sun (sunSpan, likewise; lit: the sun is above the surface) for a soft self-shadow. Flat layers
        // (height 1) stop at once. Returns the shadow on the sun (1 none), as much as strength. finer: the view ray marches only what the displaced mesh
        // leaves out (reliefHeight); the sun's march takes the whole height (the displaced mesh casts no shadow of its own on itself).
        float reliefMarch(Relief r, out vec2 moved, vec2 viewSpan, vec2 sunSpan, bool lit, float strength, bool finer)
        {
            // The detail finer than a displaced mesh is shallow and fine: half the steps do (each also reads its level at the spacing).
            int ShadowSteps = reliefShadowSteps;
            int Steps = finer && reliefSegment > 0.0 && reliefWhole < 1.0 ? min(8, reliefSteps) : reliefSteps;
            vec2 rayStep = viewSpan / float(Steps);
            bool moving = dot(viewSpan, viewSpan) > 0.0;
            // Centred, as the displacement is: the flat surface is the relief's middle (half the depth above it, half below), so what stands on the ground
            // stands in it. The ray enters the top half a span before the pixel's point, where it is at the middle.
            vec2 o = moving ? -0.5 * viewSpan : vec2(0.0);
            float rayH = 1.0, prevRay = 1.0;
            float h = reliefHeight(r, o, finer, 1.0), prevH = h;
            for (int i = 0; moving && i < Steps && h < rayH; i++)
            {
                prevH = h; prevRay = rayH;
                o += rayStep; rayH -= 1.0 / float(Steps);
                h = reliefHeight(r, o, finer, 1.0);
            }
            if (h >= rayH && rayH < 1.0)
            {
                // Between the last two steps, where the ray crossed the height.
                float above = prevRay - prevH, below = h - rayH;
                o -= rayStep * (below / max(above + below, 1e-5));
            }
            moved = o;
            if (!lit || uSelfShadow <= 0.0) return 1.0;
            float hp = reliefHeight(r, o, false, 4.0), occlusion = 0.0;
            vec2 lightStep = sunSpan / float(ShadowSteps);
            for (int k = 1; k <= ShadowSteps; k++)
            {
                float ray = hp + float(k) / float(ShadowSteps);
                if (ray >= 1.0) break;
                // Soft: how far a step's height rises above the ray, less the further along it is (a lone far peak only dims).
                float above = reliefHeight(r, o + lightStep * float(k), false, 4.0) - ray;
                occlusion = max(occlusion, above * (1.0 - float(k) / float(ShadowSteps + 1)));
            }
            return 1.0 - uSelfShadow * clamp(occlusion * 4.0, 0.0, 1.0) * strength;
        }

        // The cliff projections' vertical coordinate of biome b (its distortion).
        float cliffVert(int b)
        {
            vec4 distort = P(b, 10);
            return 1.0 - texWorld.y / 5000.0 + (cos(texWorld.x * distort.x) + cos(texWorld.z * distort.x)) * distort.y;
        }

        // Cliff: two vertical projections, weighted by how much the surface faces X or Z (meshes: per vertex).
        vec2 cliffBlend(vec3 n)
        {
            vec2 cb = max(abs(normalize(n.xz + vec2(1e-5))) - 0.2, vec2(0.0)) * 7.0;
            cb *= cb;
            cb /= max(cb.x + cb.y, 1e-5);
            if (n.y > 0.995) cb = vec2(0.5);
            if (uFeature) cb = vCliffBlend;
            return cb;
        }

        // The slope-chosen layers' weights of biome b (x slope, y cliff), the texture field's mask applied (it may change map).
        vec4 layerWeights(int b, float slope, inout vec4 map)
        {
            vec4 smin = P(b, 5), smax = P(b, 6), sblend = P(b, 7);
            vec4 w = smoothstep(smin - sblend, smin, vec4(slope)) * smoothstep(smax + sblend, smax, vec4(slope));
            w.y = keepWeight(w.y, uCliffEps);
            if (fieldLayer >= 0) fieldMask(fieldLayer, map, w);
            return w;
        }

        // What the relief march gives a biome: the moves of the ground layers' and of the cliff projections' unscaled coordinates, and the shadow on the sun.
        struct ReliefOut { vec2 ground; vec2 cliffX; vec2 cliffZ; float shade; };

        // Biome b's relief march, run for every biome before any is shaded (main): inside biome() the march's registers stacked on the shading's, which
        // halved the pixels in flight (docs/render-terrain.md "Registers"). Without it (NO_MARCH, Marched) no move and no shadow.
        ReliefOut biomeRelief(int b, vec3 n, float slope, vec4 map, float distance)
        {
            ReliefOut res = ReliefOut(vec2(0.0), vec2(0.0), vec2(0.0), 1.0);
            #ifndef NO_MARCH
            vec4 layersA = P(b, 0), layersB = P(b, 1);
            vec4 sA = P(b, 2), sB = P(b, 3), sC = P(b, 4);
            vec2 uv = texWorld.xz / 5000.0;
            float vert = cliffVert(b);
            vec2 cb = cliffBlend(n);
            vec4 w = layerWeights(b, slope, map);
            // The march's unscaled coordinates (Relief), taken outside the branch for their derivatives.
            GradCoord groundBase = gradCoord(uv), cliffBaseX = gradCoord(vec2(uv.y, vert)), cliffBaseZ = gradCoord(vec2(uv.x, vert));
            // Ground parallax (GroundRelief, docs/render-terrain.md): the ground layers' relief, uParallax world units deep (times each layer's own factor and
            // how much of the pixel it shows), and the cliff layer's on its two projections, each into the surface along the projection's axis (the cliff's on
            // map features too: the rocks).
            if (uParallax > 0.0 && distance < uParallaxEnd && biomeShare > 0.1)
            {
                // A biome showing under 10% of the pixel is not marched, fading in to 30% (the march runs once per biome blended).
                float strength = (1.0 - smoothstep(0.5 * uParallaxEnd, uParallaxEnd, distance)) * smoothstep(0.1, 0.3, biomeShare);
                float far = clamp(distance / uParallaxEnd, 0.0, 1.0), shade = 1.0;
                reliefSteps = int(round(mix(16.0, 6.0, far)));
                reliefShadowSteps = int(round(mix(8.0, 3.0, far)));
                vec3 v = normalize(uEye - vWorld);
                vec3 sun = normalize(uLightDir);   // not "l": RockBakeFragment patches the lighting at that line
                // Where the geometry is displaced (ReliefFunctions, to uDisplaceEnd) the view ray marches only the detail finer than the mesh's spacing
                // there (PatchControl: max(0.6, 0.004 × distance) over the detail), the whole height again as the displacement fades; the ground's and the
                // cliff's alike.
                reliefWhole = 1.0;
                reliefSegment = 0.0;
                if (uDisplace > 0.0 && distance < uDisplaceEnd)
                {
                    reliefWhole = smoothstep(0.6 * uDisplaceEnd, uDisplaceEnd, distance);
                    reliefSegment = max(0.6, distance * 0.004) / uTessDetail;
                }
                // What each layer shows, in the material's order below (base, grass, slope, dirt, road, the cliff over all): the base layer, and the
                // others at uOverlayRelief's share. One march over their heights by weight moves every layer's coordinate alike.
                float top = 1.0 - w.y, aboveDirt = top * (1.0 - map.a), aboveSlope = aboveDirt * (1.0 - map.b);
                Relief ground = noRelief(groundBase);
                addRelief(ground, sB.xy, layersA.x, aboveSlope * (1.0 - w.x) * (1.0 - map.r));
                if (uOverlayRelief > 0.0)
                {
                    addRelief(ground, sB.zw, layersA.w, uOverlayRelief * aboveSlope * (1.0 - w.x) * map.r);
                    addRelief(ground, sA.xy, layersA.y, uOverlayRelief * aboveSlope * w.x);
                    addRelief(ground, sC.xy, layersB.x, uOverlayRelief * aboveDirt * map.b);
                    addRelief(ground, sC.zw, layersB.y, uOverlayRelief * top * map.a);
                }
                if (ground.n > 0)
                {
                    float depth = uParallax * ground.total * strength / 5000.0;   // the full depth in world units, in the unscaled coordinate (uv)
                    vec2 o;
                    shade = reliefMarch(ground, o, -v.xz / max(v.y, 0.25) * depth, sun.xz / max(sun.y, 0.15) * depth, sun.y > 0.0, strength, true);
                    res.ground = o;
                }
                float cliffDepth = w.y > 0.0 ? uParallax * reliefDepth(layersA.z, sA.zw) * uCliffScale * strength : 0.0;
                if (cliffDepth > 0.0)
                {
                    // A projection's coordinate is (along the surface, vert): vert runs down the world's y. Its depth runs along the surface's normal n, as the
                    // displacement moves it (reliefMove): a ray point s along the view ray is s (v · n) under the surface, over the point s (v − n (v · n))
                    // back along the surface, which is what the projection's coordinate moves by (on a vertical wall: along its axis, as before). Both read the
                    // height as stored: the shading turns the normal map round (below), but a texel's height does not change with that (cracks stay low).
                    float depth = cliffDepth / 5000.0;
                    vec3 tv = n * dot(v, n) - v, ts = sun - n * dot(sun, n);
                    float vn = max(dot(v, n), 0.25), sn = max(dot(sun, n), 0.15);
                    bool lit = dot(sun, n) > 0.0;
                    float shadeX = 1.0, shadeZ = 1.0;
                    vec2 o;
                    if (cb.x > 0.1)
                    {
                        Relief r = noRelief(cliffBaseX);
                        addRelief(r, sA.zw, layersA.z, 1.0);
                        shadeX = reliefMarch(r, o, vec2(tv.z, -tv.y) / vn * depth, vec2(ts.z, -ts.y) / sn * depth, lit, strength, true);
                        res.cliffX = o;
                    }
                    if (cb.y > 0.1)
                    {
                        Relief r = noRelief(cliffBaseZ);
                        addRelief(r, sA.zw, layersA.z, 1.0);
                        shadeZ = reliefMarch(r, o, vec2(tv.x, -tv.y) / vn * depth, vec2(ts.x, -ts.y) / sn * depth, lit, strength, true);
                        res.cliffZ = o;
                    }
                    shade = mix(shade, shadeX * cb.x + shadeZ * cb.y, w.y);
                }
                res.shade = shade;
            }

            #endif
            return res;
        }

        // One biome's surface (the layer model of docs/formats/terrain.md), its coordinates moved by the march's result (biomeRelief).
        Surface biome(int b, vec3 n, float slope, vec4 map, vec4 colour, float distance, ReliefOut relief)
        {
            vec4 layersA = P(b, 0), layersB = P(b, 1);
            vec4 sA = P(b, 2), sB = P(b, 3), sC = P(b, 4);
            vec4 omult = P(b, 8), fade = P(b, 9);

            vec2 uv = texWorld.xz / 5000.0;
            float vert = cliffVert(b);
            vec2 cb = cliffBlend(n);
            vec4 w = layerWeights(b, slope, map);
            vec4 white = vec4(1.0);

            // A layer whose weight is exactly 0 at this pixel is not sampled: mixing in a weight of 0 leaves the value as it was
            // (x + 0 * (y - x) and x * 1 + y * 0 are both x). On the swamp the dirt weight is exactly 0 on 57 % of the ground, but the slope and
            // cliff weights almost never are, so this skips only some of the 14 layer samples per biome (docs/render-terrain.md, "Where the terrain's 0.99 ms goes").
            GradCoord base = gradCoord(uv * sB.xy), grass = gradCoord(uv * sB.zw), slopeUv = gradCoord(uv * sA.xy), dirt = gradCoord(uv * sC.xy), road = gradCoord(uv * sC.zw);
            GradCoord cliffX = gradCoord(vec2(uv.y, vert) * sA.zw), cliffZ = gradCoord(vec2(uv.x, vert) * sA.zw);
            float far = clamp(distance * fade.a - 0.3, 0.0, 1.0);

            // The march's moves (biomeRelief; none without it, and adding 0 leaves a coordinate as it was).
            base.p += relief.ground * sB.xy; grass.p += relief.ground * sB.zw; slopeUv.p += relief.ground * sA.xy; dirt.p += relief.ground * sC.xy; road.p += relief.ground * sC.zw;
            cliffX.p += relief.cliffX * sA.zw; cliffZ.p += relief.cliffZ * sA.zw;

            Surface s;
            s.albedo = (uParallax > 0.0 ? tex(uDiffuse, base, layersA.x) : tex(uDiffuse, base.p, layersA.x)) * colour;
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
            s.shade = relief.shade;

            if (uNormalMaps)
            {
                s.normal = uParallax > 0.0 ? tex(uNormal, base, layersA.x) : tex(uNormal, base.p, layersA.x);
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
            texWorld = uHeightNormals ? vNormal : vWorld;
            vec3 n = uHeightNormals ? terrainNormal(texWorld.xz) : normalize(vNormal);
            float distance = length(vWorld - uEye);
            // Map features clamp the slope at 1, so overhangs (normal pointing down) keep the cliff layer.
            float slope = uFeature ? min(1.0, 1.0 - n.y) : 1.0 - n.y;
            vec2 world01 = (texWorld.xz + uHalfWorld) / (2.0 * uHalfWorld);

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
            float reliefShade = 1.0;   // the relief's own shadow on the sun (GroundRelief), given to the lighting
            vec3 shadingNormal = n;
            if (nearWeight > 0.0)
            {
                vec4 map = vec4(0.0);
                vec4 colour = vec4(1.0);
                if (uHasMaps)
                {
                    map = texture(uOverlay, fract((texWorld.xz + uHalfWorld) * uRegion.zw));
                    map.r = max(map.r, map.g);
                    if (uFeature) map.a = 0.0;
                    colour = texture(uColour, fract((texWorld.xz + uHalfWorld) * uRegion.zw)) * 1.2;
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
                    float sumShade = 0.0;
                    float total = 0.0, pending = 0.0;
                    float fieldInside;
                    ivec2 field = uFeature ? ivec2(-1) : fieldTile(texWorld.xz, fieldInside);
                    fieldLayer = field.y;
                    int single = field.x >= 0 ? field.x : uFeatureBiome;
                    if (single >= 0)
                    {
                        // A map feature has one biome for its whole surface (the game builds its material for the biome at its origin).
                        Surface s = biome(single, n, slope, map, colour, distance, biomeRelief(single, n, slope, map, distance));
                        sumA = s.albedo; sumN = s.normal; sumAbsorb = s.absorb; sumShade = s.shade; total = 1.0;
                    }
                    else
                    {
                        // Every biome's march first, then the shading (biomeRelief). Without the march (NO_MARCH) the results' array is left out: kept with
                        // constants, it still cost the pass 0.28 ms.
                        #ifndef NO_MARCH
                        ReliefOut reliefs[5];
                        for (int k = 0; k < 5; k++)
                        {
                            uint b = k < 4 ? slots[k] : slot4;
                            float wk = k < 4 ? weights[k] : rest;
                            reliefs[k] = ReliefOut(vec2(0.0), vec2(0.0), vec2(0.0), 1.0);
                            if (b >= 254u || wk < 0.004) continue;
                            biomeShare = wk;
                            reliefs[k] = biomeRelief(int(b), n, slope, map, distance);
                        }
                        #define RELIEF_OF(k) reliefs[k]
                        #else
                        #define RELIEF_OF(k) ReliefOut(vec2(0.0), vec2(0.0), vec2(0.0), 1.0)
                        #endif
                        for (int k = 0; k < 5; k++)
                        {
                            uint b = k < 4 ? slots[k] : slot4;
                            float wk = k < 4 ? weights[k] : rest;
                            if (b == 254u && wk >= 0.004) pending += wk;   // biome still loading: its share shows the ground colour
                            if (b >= 254u || wk < 0.004) continue;
                            Surface s = biome(int(b), n, slope, map, colour, distance, RELIEF_OF(k));
                            sumA += s.albedo * wk;
                            sumN += s.normal * wk;
                            sumAbsorb += s.absorb * wk;
                            sumShade += s.shade * wk;
                            total += wk;
                        }
                    }
                    uint fallback = slot4 < 254u ? slot4 : slots.x;
                    if (total <= 0.0 && pending <= 0.0 && fallback < 254u)
                    {
                        biomeShare = 1.0;
                        Surface s = biome(int(fallback), n, slope, map, colour, distance, biomeRelief(int(fallback), n, slope, map, distance));
                        sumA = s.albedo; sumN = s.normal; sumAbsorb = s.absorb; sumShade = s.shade; total = 1.0;
                    }
                    if (total > 0.0) { albedo = sumA / total; sumN /= total; absorbance = sumAbsorb / total; reliefShade = mix(1.0, sumShade / total, nearWeight); }
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

            // Blood on the ground (docs/game/blood-decals.md): the cell's decal texture over the albedo, the gloss kept (the game's TerrainBlood pass into the G-buffer).
            if (uBloodCount > 0)
            {
                vec2 bloodUv = (vWorld.xz + uHalfWorld) / uBloodCellSize;
                vec2 bloodCell = floor(bloodUv);
                for (int k = 0; k < uBloodCount; k++)
                {
                    if (uBloodCells[k].xy != bloodCell) continue;
                    vec4 blood = textureLod(uBlood, vec3(bloodUv - bloodCell, uBloodCells[k].z), 0.0);
                    albedo.rgb = mix(albedo.rgb, blood.rgb, clamp(blood.a, 0.0, 1.0));
                    break;
                }
            }

            // The game's wetness (common/wet.hlsl, terrainfp4.hlsl): the rain's wetness plus the water line (a 2-unit edge) on an absorbance of 1 - gloss plus
            // the layers'. A rock bake (uWaterHeight far below the world) is without weather, so its impostor does not keep a rain.
            float wetAmount = uWaterHeight > -1.0e5 ? uWeatherWet.x : 0.0;
            makeWet(albedo, wetAmount, 1.0 - albedo.a + absorbance, uWaterHeight - vWorld.y, 2.0);

            atmoSelfShadow = reliefShade;
            // A trough displaced below the ground looks up the sun shadow from the ground, where the flat shadow casters are, not from under them.
            atmoShadowLift = uDisplace > 0.0 && distance < uDisplaceEnd ? max(terrainHeight(vWorld.xz) - vWorld.y, 0.0) : 0.0;
            vec3 l = normalize(uLightDir);
            float diff = max(dot(shadingNormal, l), 0.0) * reliefShade;
            vec3 v = normalize(uEye - vWorld);
            float gloss = clamp(albedo.a, 0.0, 1.0);
            vec3 colourOut;
            // Game sky: the game's deferred lighting (docs/formats/lighting.md); terrainfp4.hlsl writes the biome albedo's alpha as the gloss.
            if (uAtmoParams.x > 0.5) colourOut = kenshiLight(albedo.rgb, shadingNormal, v, gloss, vWorld);
            else
            {
                // The viewer's plain lighting (the simple sky); the game sky above does not need it.
                vec3 ambient = mix(uAmbientGround, uAmbientSky, 0.5 + 0.5 * shadingNormal.y);
                float spec = pow(max(dot(shadingNormal, normalize(l + v)), 0.0), 8.0 + 40.0 * gloss) * gloss * 0.25;
                colourOut = albedo.rgb * (ambient + diff * uSunColour) + spec * diff * uSunColour;
            }
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
                colourOut = mix(colourOut, vec3(0.15), 0.35 * minor);
                colourOut = mix(colourOut, vec3(0.1), 0.75 * major);
                colourOut = mix(colourOut, vec3(1.0, 0.45, 0.4), 0.8 * axis.y);   // the X axis (z = 0) red
                colourOut = mix(colourOut, vec3(0.45, 0.6, 1.0), 0.8 * axis.x);   // the Z axis (x = 0) blue
            }
            fragColour = vec4(colourOut, 1.0);
        }
        """;

    /// <summary><see cref="Fragment"/> for the instanced <see cref="MeshVertex"/>: the feature's biome per instance instead of the uniform.</summary>
    public static readonly string MeshFragment = PerInstanceBiome(Fragment, fade: true);

    /// <summary><see cref="MeshFragment"/> without the cross-fade's <c>discard</c>, for the placements that are not mid-fade (no discard anywhere in the program, so the depth test runs before it; <see cref="EarlyDepth"/>).</summary>
    public static readonly string MeshFragmentSolid = PerInstanceBiome(Fragment, fade: false);

    static string PerInstanceBiome(string fragment, bool fade)
    {
        const string uniform = "uniform int uFeatureBiome;";
        if (!fragment.Contains(uniform)) throw new InvalidOperationException("TerrainShaders.Fragment no longer declares uFeatureBiome.");
        string f = fragment.Replace(uniform, "flat in int vFeatureBiome;\n#define uFeatureBiome vFeatureBiome\nflat in float vFade;\n");
        // A rock crossfading with its impostor (docs/impostors.md section 13): the cull's dither threshold in the placement's row 1 w, as the foliage meshes dither
        // (a threshold in (0, 1) keeps that share of the pixels; 0, what every other placement has, keeps all).
        const string wireframe = "if (uWireframe) { fragColour = vec4(0.1, 0.1, 0.1, 1.0); return; }";
        int at = f.IndexOf(wireframe, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("TerrainShaders.Fragment no longer starts with the wireframe line.");
        return !fade ? f : f.Insert(at, "if (vFade > 0.0 && vFade < 1.0 && fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) >= vFade) discard;\n            ");
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
            float cliffEps;
            uint interiorMask;
            bool interiorCut;
            uint blood;
            int bloodCount;
            float bloodCellSize;
            uint height;
            float parallax;
            vec4 bloodCells[32];
            float displace;
            uint relief;
            float displaceEnd;
            float parallaxEnd;
            float overlayRelief;
            float selfShadow;
            float tessDetail;
            float rockRound;
            vec2 fieldOrigin;
            float fieldTile;
            int fieldColumns;
            int fieldCount;
            uint fieldTiles;
            float maxDepth;
            float targetSlope;
            float scaleFollow;
            float cliffScale;
            float refine;
            float pixelScale;
            float broadCut;
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
        ["uCliffEps"] = "terrain.cliffEps", ["uHasGround"] = "terrain.hasGround", ["uHasWorldColour"] = "terrain.hasWorldColour",
        ["uDiffuse"] = "textures2DArray[terrain.diffuse]", ["uNormal"] = "textures2DArray[terrain.normal]",
        ["uParams"] = "textures2D[terrain.params]", ["uCells"] = "utextures2D[terrain.cells]", ["uBlendMap"] = "textures2D[terrain.blendMap]",
        ["uOverlay"] = "textures2D[terrain.overlay]", ["uColour"] = "textures2D[terrain.colour]", ["uGround"] = "textures2D[terrain.ground]",
        ["uWorldColour"] = "textures2D[terrain.worldColour]", ["uRegion"] = "terrain.region", ["uCellGrid"] = "terrain.cellGrid",
        ["uHalfWorld"] = "terrain.halfWorld", ["uInteriorMask"] = "textures2D[terrain.interiorMask]",
        ["uInteriorCut"] = "terrain.interiorCut",   // declared by the cut patch variants and the meshes' colour program (InteriorMaskRenderer.WithCut)
        ["uBlood"] = "textures2DArray[terrain.blood]", ["uBloodCount"] = "terrain.bloodCount", ["uBloodCellSize"] = "terrain.bloodCellSize",
        ["uBloodCells"] = "terrain.bloodCells",
        ["uHeight"] = "textures2DArray[terrain.height]", ["uParallax"] = "terrain.parallax", ["uDisplace"] = "terrain.displace", ["uRelief"] = "textures2D[terrain.relief]",
        ["uDisplaceEnd"] = "terrain.displaceEnd", ["uParallaxEnd"] = "terrain.parallaxEnd",
        ["uOverlayRelief"] = "terrain.overlayRelief", ["uSelfShadow"] = "terrain.selfShadow", ["uTessDetail"] = "terrain.tessDetail", ["uRockRound"] = "terrain.rockRound",
        ["uFieldTiles"] = "utextures2D[terrain.fieldTiles]", ["uFieldOrigin"] = "terrain.fieldOrigin", ["uFieldTile"] = "terrain.fieldTile",
        ["uFieldColumns"] = "terrain.fieldColumns", ["uFieldCount"] = "terrain.fieldCount", ["uMaxDepth"] = "terrain.maxDepth", ["uTargetSlope"] = "terrain.targetSlope", ["uScaleFollow"] = "terrain.scaleFollow", ["uCliffScale"] = "terrain.cliffScale", ["uRefine"] = "terrain.refine", ["uPixelScale"] = "terrain.pixelScale", ["uBroadCut"] = "terrain.broadCut",
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
    /// <summary>The ground displacement's stages (GroundRelief): <see cref="PatchVertexTess"/>, <see cref="PatchControl"/>, <see cref="PatchEvaluation"/>.</summary>
    public static string PatchVertexTessNative() => Native(PatchVertexTess);
    public static string PatchControlNative() => Native(PatchControl);
    public static string PatchEvaluationNative() => Native(PatchEvaluation);
    public static string FragmentNative(bool interiorCut = false, bool march = true) =>
        Gi.GiResolveShaders.WithAlbedo(Native(interiorCut ? InteriorMaskRenderer.WithCut(Marched(Fragment, march)) : Marched(Fragment, march)));

    /// <summary>
    /// <paramref name="fragment"/> with the ground parallax's march (march false: left out, by defining NO_MARCH). Off it never runs (uParallax 0), but its registers were the
    /// shader's: about 220 a thread, which held the terrain colour pass to a tenth of the warps; without it the pass took 2.4 ms instead of 3.4
    /// (docs/render-terrain.md "Cost"). TerrainRenderer keeps a set of each and draws with the one the setting asks for.
    /// </summary>
    static string Marched(string fragment, bool march) => march ? fragment : fragment.Insert(fragment.IndexOf('\n') + 1, "#define NO_MARCH\n");
    /// <summary>The terrain colour pass's depth-only twin: the same vertex stage (so the depths are the same), a fragment stage that writes nothing (<see cref="TerrainLateColour"/>).</summary>
    public static string PrepassFragmentNative(bool interiorCut = false)
    {
        const string empty = "#version 330 core\nvoid main()\n{\n}\n";
        return Native(interiorCut ? InteriorMaskRenderer.WithCut(empty) : empty);
    }
    public static string MeshVertexNative() => Native(MeshVertex);
    /// <summary>The TERRAIN-mode meshes' colour program, with the interior cut (on per draw: <see cref="TerrainConstants.InteriorCut"/>; the cross-fade already discards).</summary>
    public static string MeshFragmentNative(bool march = true) => Gi.GiResolveShaders.WithAlbedo(Native(InteriorMaskRenderer.WithCut(Marched(MeshFragment, march))));
    public static string MeshFragmentSolidNative(bool march = true) => Gi.GiResolveShaders.WithAlbedo(Native(Marched(MeshFragmentSolid, march)));
    public static string MeshInstancedDepthVertexNative() => Native(MeshInstancedDepthVertex);
    /// <summary>The rocks' smoothing (RockSmoothing): <see cref="MeshVertexTess"/>, <see cref="MeshControl"/>, <see cref="MeshEvaluation"/> and its casters' twin.</summary>
    public static string MeshVertexTessNative() => Native(MeshVertexTess);
    public static string MeshControlNative() => Native(MeshControl);
    public static string MeshEvaluationNative() => Native(MeshEvaluation);
    public static string MeshEvaluationDepthNative() => Native(MeshEvaluationDepth);
    /// <summary><see cref="ShadowShaders.DepthFragment"/> (the caster block at set 0, binding 2) with the terrain's push block, so both
    /// stages of a depth program declare the same one.</summary>
    public static string DepthFragmentNative() => Native(ShadowShaders.DepthFragment);
}

/// <summary>The C# side of <see cref="TerrainShaders.ConstantsBlock"/> (std140; offsets checked against the reflection by a test). GLSL bools
/// are 32-bit (0 / 1).</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 864)]
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
    [System.Runtime.InteropServices.FieldOffset(224)] public float CliffEps;
    [System.Runtime.InteropServices.FieldOffset(228)] public uint InteriorMask;
    [System.Runtime.InteropServices.FieldOffset(232)] public uint InteriorCut;
    [System.Runtime.InteropServices.FieldOffset(236)] public uint Blood;
    [System.Runtime.InteropServices.FieldOffset(240)] public int BloodCount;
    [System.Runtime.InteropServices.FieldOffset(244)] public float BloodCellSize;
    [System.Runtime.InteropServices.FieldOffset(248)] public uint Height;
    [System.Runtime.InteropServices.FieldOffset(252)] public float Parallax;
    [System.Runtime.InteropServices.FieldOffset(256)] public BloodCells BloodCells;
    [System.Runtime.InteropServices.FieldOffset(768)] public float Displace;
    [System.Runtime.InteropServices.FieldOffset(772)] public uint Relief;
    [System.Runtime.InteropServices.FieldOffset(776)] public float DisplaceEnd;
    [System.Runtime.InteropServices.FieldOffset(780)] public float ParallaxEnd;
    [System.Runtime.InteropServices.FieldOffset(784)] public float OverlayRelief;
    [System.Runtime.InteropServices.FieldOffset(788)] public float SelfShadow;
    [System.Runtime.InteropServices.FieldOffset(792)] public float TessDetail;
    [System.Runtime.InteropServices.FieldOffset(796)] public float RockRound;
    [System.Runtime.InteropServices.FieldOffset(800)] public System.Numerics.Vector2 FieldOrigin;
    [System.Runtime.InteropServices.FieldOffset(808)] public float FieldTile;
    [System.Runtime.InteropServices.FieldOffset(812)] public int FieldColumns;
    [System.Runtime.InteropServices.FieldOffset(816)] public int FieldCount;
    [System.Runtime.InteropServices.FieldOffset(820)] public uint FieldTiles;
    [System.Runtime.InteropServices.FieldOffset(824)] public float MaxDepth;
    [System.Runtime.InteropServices.FieldOffset(828)] public float TargetSlope;
    [System.Runtime.InteropServices.FieldOffset(832)] public float ScaleFollow;
    [System.Runtime.InteropServices.FieldOffset(836)] public float CliffScale;
    [System.Runtime.InteropServices.FieldOffset(840)] public float Refine;
    [System.Runtime.InteropServices.FieldOffset(844)] public float PixelScale;
    [System.Runtime.InteropServices.FieldOffset(848)] public float BroadCut;
}

/// <summary>The cells of <see cref="TerrainConstants.BloodCells"/>: column, row, layer (std140 <c>vec4[32]</c>).</summary>
[System.Runtime.CompilerServices.InlineArray(TerrainDecalRenderer.MaxSlots)]
struct BloodCells
{
    System.Numerics.Vector4 cell;
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
