namespace Meitou.Rendering;

/// <summary>
/// GLSL of the camera effects (docs/render-post.md "Motion blur", "Depth of field"), run with <see cref="PostProcessShaders.Vertex"/>. The picture after the upscaler is HDR at gamma 2.2
/// with no sRGB step, so every blur decodes to linear light (power 2.2), sums there and encodes back; nothing is clamped at 1.
/// </summary>
static class CameraEffectShaders
{
    /// <summary>
    /// Motion blur, first pass at the render size: RG the blur vector in display pixels (<c>uPixels</c> scales the motion target's UV over the shutter), limited to
    /// <c>uMaxRadius</c>, B log2 of the view depth (a half float holds a world from 1 to 10^6 units to 1 %). The sky's motion is a far plane's and the clouds', so it is
    /// zero there; water over the sky keeps its motion (the velocity pass's reactivity marks it).
    /// </summary>
    public const string MotionBlurPrepare = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uMotion, uDepth;
        uniform vec2 uPlanes, uPixels;
        uniform float uMaxRadius;
        """ + DepthConvention.Glsl + """

        void main()
        {
            ivec2 p = ivec2(vUv * vec2(textureSize(uMotion, 0)));
            vec4 m = texelFetch(uMotion, p, 0);
            float d = texelFetch(uDepth, p, 0).r;
            vec2 v = m.rg * uPixels;
            float len = length(v);
            if (isnan(len) || isinf(len) || depthIsSky(d) && m.a <= 0.0) v = vec2(0.0);
            else if (len > uMaxRadius) v *= uMaxRadius / len;
            fragColour = vec4(v, log2(max(depthViewZ(d, uPlanes), 1.0)), 1.0);
        }
        """;

    /// <summary>Motion blur's tile maximum (McGuire et al. 2012): the longest blur vector of each tile, the spans taken from both sizes as a tile can cover a column more.</summary>
    public const string MotionBlurTile = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSource;
        uniform vec2 uTiles;
        void main()
        {
            vec2 size = vec2(textureSize(uSource, 0)), tile = floor(vUv * uTiles);
            ivec2 first = ivec2(floor(tile * size / uTiles));
            ivec2 last = min(ivec2(ceil((tile + 1.0) * size / uTiles)), ivec2(size));
            vec2 longest = vec2(0.0);
            float most = 0.0;
            for (int y = first.y; y < last.y; y++)
                for (int x = first.x; x < last.x; x++)
                {
                    vec2 v = texelFetch(uSource, ivec2(x, y), 0).rg;
                    float l = dot(v, v);
                    if (l > most) { most = l; longest = v; }
                }
            fragColour = vec4(longest, 0.0, 1.0);
        }
        """;

    /// <summary>Motion blur's neighbour maximum: the longest vector of the 3 x 3 tiles round each tile. A tile is the widest blur's radius, so this reaches every pixel a blur can.</summary>
    public const string MotionBlurNeighbour = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uTile;
        void main()
        {
            ivec2 last = textureSize(uTile, 0) - 1, here = ivec2(vUv * vec2(last + 1));
            vec2 longest = vec2(0.0);
            float most = 0.0;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    vec2 v = texelFetch(uTile, clamp(here + ivec2(x, y), ivec2(0), last), 0).rg;
                    float l = dot(v, v);
                    if (l > most) { most = l; longest = v; }
                }
            fragColour = vec4(longest, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Motion blur's reconstruction (McGuire, Hennessy, Bukowski, Osman 2012), at the display size: 15 taps along the neighbourhood's longest vector, offset by static noise.
    /// A tap in front of the pixel lands on it by the tap's own blur; one behind shows through the pixel's own blur; two moving ones cover each other. In linear light;
    /// a pixel no blur reaches is returned as it came, bit for bit.
    /// </summary>
    public const string MotionBlurReconstruct = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene, uPrepare, uNeighbour;
        const int SAMPLES = 15;
        const float SOFT_Z = 0.1;   // log2 of the view depth: surfaces within 7 % count as level
        float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
        vec3 decode(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }
        float cone(float r, float v) { return clamp(1.0 - r / v, 0.0, 1.0); }
        float cylinder(float r, float v) { return 1.0 - smoothstep(0.95 * v, 1.05 * v, r); }
        float inFront(float a, float b) { return clamp(1.0 - (a - b) / SOFT_Z, 0.0, 1.0); }

        void main()
        {
            ivec2 size = textureSize(uScene, 0), last = size - 1, p = clamp(ivec2(vUv * vec2(size)), ivec2(0), last);
            vec4 own = texelFetch(uScene, p, 0);
            ivec2 tiles = textureSize(uNeighbour, 0), prepared = textureSize(uPrepare, 0);
            float noise = ign(gl_FragCoord.xy);
            // Up to a quarter of a tile either way, so the tiles' grid does not show in the blur's edge.
            vec2 shift = (vec2(noise, ign(gl_FragCoord.yx + 13.7)) - 0.5) * 0.5;
            vec2 reach = texelFetch(uNeighbour, clamp(ivec2(vUv * vec2(tiles) + shift), ivec2(0), tiles - 1), 0).rg;
            float span = length(reach);
            if (span < 0.5) { fragColour = own; return; }

            vec3 x = texelFetch(uPrepare, p * prepared / size, 0).rgb;
            float vx = max(length(x.xy), 0.5), weight = 1.0 / vx;
            vec3 sum = decode(own.rgb) * weight;
            for (int i = 0; i < SAMPLES; i++)
            {
                float t = mix(-1.0, 1.0, (float(i) + noise + 0.5) / float(SAMPLES + 1));
                ivec2 q = clamp(ivec2(floor(vec2(p) + 0.5 + reach * t)), ivec2(0), last);
                if (q == p) continue;
                vec3 y = texelFetch(uPrepare, q * prepared / size, 0).rgb;
                float vy = max(length(y.xy), 0.5), r = span * abs(t);
                float a = inFront(y.z, x.z) * cone(r, vy) + inFront(x.z, y.z) * cone(r, vx) + cylinder(r, vy) * cylinder(r, vx) * 2.0;
                sum += decode(texelFetch(uScene, q, 0).rgb) * a;
                weight += a;
            }
            fragColour = vec4(pow(sum / weight, vec3(1.0 / 2.2)), own.a);
        }
        """;

    /// <summary>
    /// The depth of field's thin lens: a point at view depth z blurs over a disc proportional to (z - s) / z, s the focus distance. The CoC is the disc's radius in half-size
    /// pixels, positive behind the focus, negative in front, the sky at infinity. <c>uJitterUv</c> moves a picture position onto the jittered depth.
    /// </summary>
    const string DofLens = """
        uniform sampler2D uDepth;
        uniform vec2 uPlanes, uJitterUv;
        uniform vec3 uLens;    // the CoC at infinity, the focus distance, the largest CoC
        """ + DepthConvention.Glsl + """

        float dofCoc(float d)
        {
            if (depthIsSky(d)) return min(uLens.x, uLens.z);
            float z = depthViewZ(d, uPlanes);
            return clamp(uLens.x * (z - uLens.y) / z, -uLens.z, uLens.z);
        }

        """;

    /// <summary>The gathers' rings: ring k at R k / rings holds 8 k taps, three half-size pixels apart up to R = 12, turned per pixel by static noise. Nothing reads another frame.</summary>
    const string DofRings = """
        const float TAU = 6.2831853;
        int dofRings(float radius) { return int(clamp(ceil(radius / 3.0), 1.0, 4.0)); }
        float dofSpin(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

        """;

    /// <summary>
    /// Depth of field, pass 1, half size: the four picture pixels under each texel averaged as light (NaN and Inf to 0: a gather would spread them over a disc), and the CoC of the
    /// nearest surface of the four, so a silhouette's texel takes the subject's CoC and not a mix that names no surface.
    /// </summary>
    public const string DofPrepare = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        """ + DofLens + """
        float finite(float v) { return isnan(v) || isinf(v) ? 0.0 : v; }
        vec3 decode(vec3 c) { return pow(max(vec3(finite(c.r), finite(c.g), finite(c.b)), vec3(0.0)), vec3(2.2)); }
        void main()
        {
            ivec2 size = textureSize(uScene, 0), last = size - 1, half_ = (size + 1) / 2, p = ivec2(vUv * vec2(half_));
            vec3 light = vec3(0.0);
            float nearest = DEPTH_SKY;
            for (int i = 0; i < 4; i++)
            {
                ivec2 q = min(p * 2 + ivec2(i & 1, i >> 1), last);
                light += decode(texelFetch(uScene, q, 0).rgb);
                nearest = depthNearer(nearest, textureLod(uDepth, (vec2(q) + 0.5) / vec2(size) + uJitterUv, 0.0).r);
            }
            fragColour = vec4(light * 0.25, dofCoc(nearest));
        }
        """;

    /// <summary>Depth of field, pass 2: the largest near CoC of each tile of pass 1, the spans taken from both sizes.</summary>
    public const string DofTile = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uPrepared;
        uniform vec2 uTiles;
        void main()
        {
            vec2 size = vec2(textureSize(uPrepared, 0)), tile = floor(vUv * uTiles);
            ivec2 first = ivec2(floor(tile * size / uTiles));
            ivec2 last = min(ivec2(ceil((tile + 1.0) * size / uTiles)), ivec2(size));
            float nearest = 0.0;
            for (int y = first.y; y < last.y; y++)
                for (int x = first.x; x < last.x; x++)
                    nearest = max(nearest, -texelFetch(uPrepared, ivec2(x, y), 0).a);
            fragColour = vec4(nearest, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>Depth of field, pass 3: the largest near CoC that can reach each tile: a tile t tiles away is at least (t - 1) spans from this one, and a disc covers its CoC plus one.</summary>
    public const string DofDilate = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uTile;
        uniform vec2 uReach;   // the largest CoC, half-size pixels per tile
        void main()
        {
            ivec2 last = textureSize(uTile, 0) - 1, here = ivec2(vUv * vec2(last + 1));
            int tiles = int(clamp(ceil((uReach.x + 1.0) / uReach.y), 1.0, 4.0));
            float nearest = 0.0;
            for (int y = -tiles; y <= tiles; y++)
                for (int x = -tiles; x <= tiles; x++)
                {
                    float n = texelFetch(uTile, clamp(here + ivec2(x, y), ivec2(0), last), 0).r;
                    if (n + 1.0 > float(max(abs(x), abs(y)) - 1) * uReach.y) nearest = max(nearest, n);
                }
            fragColour = vec4(nearest, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Depth of field, pass 4, the far field, half size: scatter as gather over this texel's own CoC. A tap counts only where its own disc reaches, so a sharp subject's taps reach
    /// nothing and cannot ring the blur behind it, and near taps take no part. Light in RGB, this CoC in A for the composite's upsample.
    /// </summary>
    public const string DofFar = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uPrepared;
        """ + DofRings + """
        void main()
        {
            vec2 texel = 1.0 / vec2(textureSize(uPrepared, 0));
            vec4 centre = textureLod(uPrepared, vUv, 0.0);
            float coc = max(centre.a, 0.0);
            if (coc < 0.5) { fragColour = vec4(centre.rgb, coc); return; }
            int rings = dofRings(coc);
            float spin = dofSpin(gl_FragCoord.xy), total = 1.0;
            vec3 sum = centre.rgb;
            for (int k = 1; k <= rings; k++)
            {
                float r = coc * float(k) / float(rings), turn = TAU / float(8 * k), start = (spin + 0.5 * float(k)) * turn;
                float s = sin(turn), c = cos(turn);
                vec2 dir = vec2(cos(start), sin(start));
                for (int j = 0; j < 8 * k; j++)
                {
                    vec4 t = textureLod(uPrepared, vUv + dir * r * texel, 0.0);
                    float w = clamp(max(t.a, 0.0) - r + 1.0, 0.0, 1.0);
                    sum += t.rgb * w;
                    total += w;
                    dir = vec2(dir.x * c - dir.y * s, dir.x * s + dir.y * c);
                }
            }
            fragColour = vec4(sum / total, coc);
        }
        """;

    /// <summary>
    /// Depth of field, pass 5, the near field, half size: each near tap spreads its light over its own disc, so its share here is its coverage times the area a tap stands for
    /// over its disc's area. A uniform foreground covers exactly 1, half of one about half, so its edge is translucent over what is behind it. Premultiplied, so the upsample fades
    /// an edge instead of darkening it.
    /// </summary>
    public const string DofNear = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uPrepared, uDilated;
        """ + DofRings + """
        float nearShare(float n, float r, float area)
        {
            float own = n + 0.5;
            return clamp(n - r + 1.0, 0.0, 1.0) * clamp(2.0 * n - 1.0, 0.0, 1.0) * area / (own * own);
        }
        void main()
        {
            float radius = textureLod(uDilated, vUv, 0.0).r;
            if (radius < 0.5) { fragColour = vec4(0.0); return; }
            vec2 texel = 1.0 / vec2(textureSize(uPrepared, 0));
            int rings = dofRings(radius);
            float area = (radius + 0.5) * (radius + 0.5) / (1.0 + 4.0 * float(rings * (rings + 1)));
            float spin = dofSpin(gl_FragCoord.xy);
            vec4 centre = textureLod(uPrepared, vUv, 0.0);
            float cover = nearShare(max(-centre.a, 0.0), 0.0, area);
            vec3 sum = centre.rgb * cover;
            for (int k = 1; k <= rings; k++)
            {
                float r = radius * float(k) / float(rings), turn = TAU / float(8 * k), start = (spin + 0.5 * float(k)) * turn;
                float s = sin(turn), c = cos(turn);
                vec2 dir = vec2(cos(start), sin(start));
                for (int j = 0; j < 8 * k; j++)
                {
                    vec4 t = textureLod(uPrepared, vUv + dir * r * texel, 0.0);
                    float a = nearShare(max(-t.a, 0.0), r, area);
                    sum += t.rgb * a;
                    cover += a;
                    dir = vec2(dir.x * c - dir.y * s, dir.x * s + dir.y * c);
                }
            }
            if (cover <= 0.0) { fragColour = vec4(0.0); return; }
            float alpha = min(cover, 1.0);
            fragColour = vec4(sum * (alpha / cover), alpha);
        }
        """;

    /// <summary>
    /// Depth of field, pass 6, at the picture's size: the far field over the sharp picture by the full-size CoC, so a sharp edge stays sharp, upsampled by CoC so a subject's colour
    /// does not ring the blur beside it; then the near field over both by its coverage. A pixel neither touches is returned as it came, bit for bit.
    /// </summary>
    public const string DofComposite = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene, uFarField, uNearField;
        """ + DofLens + """
        vec3 farField(vec2 uv, float coc)
        {
            ivec2 size = textureSize(uFarField, 0);
            vec2 p = uv * vec2(size) - 0.5, base = floor(p), f = p - base;
            vec3 sum = vec3(0.0);
            float total = 0.0;
            for (int i = 0; i < 4; i++)
            {
                ivec2 o = ivec2(i & 1, i >> 1);
                vec4 t = texelFetch(uFarField, clamp(ivec2(base) + o, ivec2(0), size - 1), 0);
                float bx = o.x != 0 ? f.x : 1.0 - f.x, by = o.y != 0 ? f.y : 1.0 - f.y;
                float w = (bx * by + 1e-4) / (0.25 + abs(t.a - coc));
                sum += t.rgb * w;
                total += w;
            }
            return sum / total;
        }
        void main()
        {
            ivec2 size = textureSize(uScene, 0);
            vec4 scene = texelFetch(uScene, clamp(ivec2(vUv * vec2(size)), ivec2(0), size - 1), 0);
            float coc = dofCoc(textureLod(uDepth, vUv + uJitterUv, 0.0).r), farMix = smoothstep(0.5, 1.5, coc);
            vec4 nearLayer = textureLod(uNearField, vUv, 0.0);
            if (farMix <= 0.0 && nearLayer.a <= 0.0) { fragColour = scene; return; }
            vec3 light = pow(max(scene.rgb, vec3(0.0)), vec3(2.2));
            light = mix(light, farField(vUv, max(coc, 0.0)), farMix);
            fragColour = vec4(pow(light * (1.0 - nearLayer.a) + nearLayer.rgb, vec3(1.0 / 2.2)), scene.a);
        }
        """;
}
