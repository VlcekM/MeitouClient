namespace Meitou.Rendering;

/// <summary>
/// GLSL of the camera effects (docs/render-post.md "Motion blur"), run with <see cref="PostProcessShaders.Vertex"/>. The picture after the upscaler is HDR at gamma 2.2
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
}
