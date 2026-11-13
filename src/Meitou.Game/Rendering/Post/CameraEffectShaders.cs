using Meitou.Game.Rendering.Frame;
namespace Meitou.Game.Rendering.Post;

/// <summary>
/// GLSL of the camera effects (docs/render-post.md "Depth of field"), run with <see cref="PostProcessShaders.Vertex"/>. The picture after the
/// upscaler is HDR at gamma 2.2 with no sRGB step, so every blur decodes to linear light (power 2.2), sums there and encodes back; nothing is clamped
/// at 1.
/// </summary>
static class CameraEffectShaders
{
    /// <summary>
    /// The depth of field's thin lens: a point at view depth z blurs over a disc proportional to (z - s) / z, s the focus distance. The CoC is the
    /// disc's radius in half-size pixels, positive behind the focus, negative in front, the sky at infinity. <c>uJitterUv</c> moves a picture
    /// position onto the jittered depth.
    /// </summary>
    const string DofLens = """
        uniform sampler2D uDepth;
        uniform vec2 uPlanes, uJitterUv;
        uniform vec3 uLens;    // the CoC at infinity, the focus distance, the largest CoC
        uniform sampler2D uFocus;   // the autofocus (DofFocus): one texel, the focus distance
        uniform vec4 uAuto;         // autofocus: x the CoC at infinity times (the focus in mm less the focal length), y the focal length in mm, z 1 on; w the sharp zone's half depth
        """ + DepthConvention.Glsl + """

        // The lens now: x the CoC at infinity, y the focus distance (the autofocus's when it is on, ThinLens.InfinityCoc's rule).
        vec2 dofLens()
        {
            if (uAuto.z < 0.5) return uLens.xy;
            float s = texelFetch(uFocus, ivec2(0), 0).r;
            return vec2(uAuto.x / (max(s * 100.0, uAuto.y + 1.0) - uAuto.y), s);
        }

        float dofCoc(float d)
        {
            vec2 lens = dofLens();
            if (depthIsSky(d)) return min(lens.x, uLens.z);
            float z = depthViewZ(d, uPlanes);
            // The sharp zone (PostOptions.LensSharpZone) is taken out of the distance from the focus: what lies in it is sharp, the blur starts at its ends.
            float off = z - lens.y;
            off = sign(off) * max(abs(off) - uAuto.w, 0.0);
            return clamp(lens.x * off / max(lens.y + off, 1e-3), -uLens.z, uLens.z);
        }

        """;

    /// <summary>
    /// The autofocus (docs/render-post.md "Depth of field"), one texel: the nearest surface in a small spot at the picture's centre, eased from the
    /// last frame's focus in log distance (a subject crossing the centre pulls the focus over, it does not snap); the fallback where the spot shows
    /// only sky.
    /// </summary>
    public const string DofFocus = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform sampler2D uPrevious;
        uniform vec2 uPlanes, uJitterUv;
        uniform vec2 uCentre;   // the spot's centre in the picture (PostOptions.LensFocusSpot)
        uniform vec4 uSpot;   // x the ease this frame (1 snaps), y the fallback distance, z the spot's half height as a share of the picture's, w the picture's aspect
        """ + DepthConvention.Glsl + """
        void main()
        {
            float nearest = 1.0e9;
            for (int y = -2; y <= 2; y++)
                for (int x = -2; x <= 2; x++)
                {
                    vec2 uv = uCentre + vec2(float(x) / uSpot.w, float(y)) * 0.5 * uSpot.z;
                    float d = textureLod(uDepth, uv + uJitterUv, 0.0).r;
                    if (!depthIsSky(d)) nearest = min(nearest, depthViewZ(d, uPlanes));
                }
            float target = nearest < 1.0e8 ? nearest : uSpot.y;
            float previous = texelFetch(uPrevious, ivec2(0), 0).r;
            float focus = uSpot.x >= 1.0 || !(previous > 0.0) ? target : exp(mix(log(previous), log(target), uSpot.x));
            fragColour = vec4(focus, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// The gathers' rings: ring k at R k / rings holds 8 k taps, three half-size pixels apart up to R = 12, turned per pixel by static noise. Nothing
    /// reads another frame.
    /// </summary>
    const string DofRings = """
        const float TAU = 6.2831853;
        int dofRings(float radius) { return int(clamp(ceil(radius / 3.0), 1.0, 4.0)); }
        float dofSpin(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }

        """;

    /// <summary>
    /// Depth of field, pass 1, half size: the four picture pixels under each texel averaged as light (NaN and Inf to 0: a gather would spread them
    /// over a disc), and the CoC of the nearest surface of the four, so a silhouette's texel takes the subject's CoC and not a mix that names no
    /// surface.
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

    /// <summary>
    /// Depth of field, pass 2: the largest near CoC of each tile of pass 1, the spans taken from both sizes.
    /// </summary>
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

    /// <summary>
    /// Depth of field, pass 3: the largest near CoC that can reach each tile: a tile t tiles away is at least (t - 1) spans from this one, and a disc
    /// covers its CoC plus one.
    /// </summary>
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
    /// Depth of field, pass 4, the far field, half size: scatter as gather over this texel's own CoC. A tap counts only where its own disc reaches,
    /// so a sharp subject's taps reach nothing and cannot ring the blur behind it, and near taps take no part. Light in RGB, this CoC in A for the
    /// composite's upsample.
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
            // A sample's light is spread over its own disc: its weight is one over that disc's area. Weighed alike, a blurred figure in front of a more
            // blurred background gave the background's pixels next to it a few taps among hundreds, and kept a sharp outline.
            float spin = dofSpin(gl_FragCoord.xy), total = 1.0 / max(coc * coc, 1.0);
            vec3 sum = centre.rgb * total;
            for (int k = 1; k <= rings; k++)
            {
                float r = coc * float(k) / float(rings), turn = TAU / float(8 * k), start = (spin + 0.5 * float(k)) * turn;
                float s = sin(turn), c = cos(turn);
                vec2 dir = vec2(cos(start), sin(start));
                for (int j = 0; j < 8 * k; j++)
                {
                    vec4 t = textureLod(uPrepared, vUv + dir * r * texel, 0.0);
                    float reach = max(t.a, 0.0);
                    float w = clamp(reach - r + 1.0, 0.0, 1.0) / max(reach * reach, 1.0);
                    sum += t.rgb * w;
                    total += w;
                    dir = vec2(dir.x * c - dir.y * s, dir.x * s + dir.y * c);
                }
            }
            fragColour = vec4(sum / total, coc);
        }
        """;

    /// <summary>
    /// Depth of field, after the far gather: a 5 x 5 binomial over the gathered field, its step growing with the texel's CoC (a quarter texel in
    /// focus, one texel from a CoC of 4), each tap weighed by how blurred it is. The gather weighs a sample by one over its disc's area, and its few
    /// taps then leave a grain that this takes away; in-focus texels pass unchanged.
    /// </summary>
    public const string DofFarFilter = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uFar;
        void main()
        {
            vec4 centre = textureLod(uFar, vUv, 0.0);
            if (centre.a < 1.0) { fragColour = centre; return; }
            vec2 step = clamp(centre.a * 0.25, 0.25, 1.0) / vec2(textureSize(uFar, 0));
            const float k[5] = float[5](1.0, 4.0, 6.0, 4.0, 1.0);
            vec3 sum = vec3(0.0);
            float total = 0.0;
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 5; x++)
                {
                    vec4 t = textureLod(uFar, vUv + vec2(float(x - 2), float(y - 2)) * step, 0.0);
                    float w = k[x] * k[y] * clamp(t.a, 0.0, 1.0);
                    sum += t.rgb * w;
                    total += w;
                }
            fragColour = vec4(sum / max(total, 1e-4), centre.a);
        }
        """;

    /// <summary>
    /// Depth of field, pass 5, the near field, half size: each near tap spreads its light over its own disc, so its share here is its coverage times
    /// the area a tap stands for over its disc's area. A uniform foreground covers exactly 1, half of one about half, so its edge is translucent over
    /// what is behind it. Premultiplied, so the upsample fades an edge instead of darkening it.
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
    /// Depth of field, pass 6, at the picture's size: the far field over the sharp picture by the full-size CoC, so a sharp edge stays sharp,
    /// upsampled by CoC so a subject's colour does not ring the blur beside it; then the near field over both by its coverage. A pixel neither
    /// touches is returned as it came, bit for bit.
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
