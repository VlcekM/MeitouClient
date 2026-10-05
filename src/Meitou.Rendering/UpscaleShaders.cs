namespace Meitou.Rendering;

/// <summary>GLSL for temporal upscaling (written from the public techniques: Karis 2014, Salvi 2016, Jimenez 2016), run with <see cref="PostProcessShaders.Vertex"/>.</summary>
static class UpscaleShaders
{
    /// <summary>
    /// Camera motion from depth: each render pixel's surface, taken from the near slice's depth (the far slice's where the near one is
    /// empty), is carried back into the previous frame by one matrix per slice (this frame's jittered clip space to the previous frame's
    /// unjittered one, built relative to the eye on the CPU). Writes RG = motion in UV (previous UV = UV − motion, jitter removed),
    /// B = the depth of one D3D-style projection with fixed planes (PostProcess.UpscaleNear .. UpscaleFar), for the upscalers and TAA's
    /// closest-sample pick. With <c>uDepthOnly</c> it writes that depth alone (R32F for FSR / DLSS).
    /// </summary>
    public const string Velocity = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth, uFarDepth;
        uniform mat4 uNearToPrev, uFarToPrev;
        uniform vec2 uNearPlanes, uFarPlanes, uFullPlanes;
        uniform vec2 uJitterNdc;
        uniform int uHasFar, uDepthOnly;

        // Distance along the view axis from a depth-buffer value: System.Numerics' projection gives clip depth 0..1 (D3D style), which GL's
        // depth range stores as 0.5..1.
        float viewZ(float d, vec2 nf) { float zd = 2.0 * d - 1.0; return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

        void main()
        {
            float d = textureLod(uNearDepth, vUv, 0.0).r;
            mat4 toPrev = uNearToPrev;
            vec2 planes = uNearPlanes;
            if (d >= 1.0 && uHasFar != 0)
            {
                d = textureLod(uFarDepth, vUv, 0.0).r;
                toPrev = uFarToPrev;
                planes = uFarPlanes;
            }
            float z = viewZ(d, planes);
            float depth = clamp(uFullPlanes.y * (z - uFullPlanes.x) / (z * (uFullPlanes.y - uFullPlanes.x)), 0.0, 1.0);
            if (uDepthOnly != 0) { fragColour = vec4(depth, 0.0, 0.0, 1.0); return; }
            vec2 ndc = vUv * 2.0 - 1.0;
            vec4 prev = toPrev * vec4(ndc, 2.0 * d - 1.0, 1.0);
            vec2 motion = ((ndc - uJitterNdc) - prev.xy / prev.w) * 0.5;
            fragColour = vec4(motion, depth, 1.0);
        }
        """;

    /// <summary>
    /// Temporal anti-aliasing and upscaling into the display-size history. The current frame at each display pixel is a Gaussian
    /// (Blackman-Harris fit) of the 3 × 3 jittered render samples around it; the history is fetched where the pixel was last frame
    /// (motion of the nearest sample in the neighbourhood, Catmull-Rom), clipped to the neighbourhood's colour spread in YCoCg
    /// (variance clipping), and the two blended in a tone-mapped space (c / (1 + max c)) so bright HDR samples don't dominate.
    /// The current frame's share grows with how close its nearest sample lies; off-screen history or a reset takes the current frame.
    /// </summary>
    public const string Taa = """
        #version 330 core
        out vec4 fragColour;
        uniform sampler2D uColour, uMotion, uHistory;
        uniform vec2 uRenderSize, uDisplaySize, uJitter;
        uniform float uBlend;
        uniform int uReset;

        float maxc(vec3 c) { return max(c.r, max(c.g, c.b)); }
        vec3 tm(vec3 c) { return c / (1.0 + maxc(c)); }
        vec3 itm(vec3 c) { return c / max(1.0 - maxc(c), 1e-4); }
        vec3 ycocg(vec3 c) { return vec3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b, 0.5 * c.r - 0.5 * c.b, -0.25 * c.r + 0.5 * c.g - 0.25 * c.b); }
        vec3 rgb(vec3 y) { return vec3(y.x + y.y - y.z, y.x + y.z, y.x - y.y - y.z); }

        // Catmull-Rom in five bilinear taps (the corners dropped).
        vec3 history(vec2 uv)
        {
            vec2 pos = uv * uDisplaySize;
            vec2 c = floor(pos - 0.5) + 0.5;
            vec2 f = pos - c;
            vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
            vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
            vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
            vec2 w3 = f * f * (-0.5 + 0.5 * f);
            vec2 w12 = w1 + w2;
            vec2 t0 = (c - 1.0) / uDisplaySize, t3 = (c + 2.0) / uDisplaySize, t12 = (c + w2 / w12) / uDisplaySize;
            vec3 s = textureLod(uHistory, vec2(t12.x, t0.y), 0.0).rgb * (w12.x * w0.y)
                   + textureLod(uHistory, vec2(t0.x, t12.y), 0.0).rgb * (w0.x * w12.y)
                   + textureLod(uHistory, t12, 0.0).rgb * (w12.x * w12.y)
                   + textureLod(uHistory, vec2(t3.x, t12.y), 0.0).rgb * (w3.x * w12.y)
                   + textureLod(uHistory, vec2(t12.x, t3.y), 0.0).rgb * (w12.x * w3.y);
            float w = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
            return max(s / w, vec3(0.0));
        }

        void main()
        {
            vec2 uv = gl_FragCoord.xy / uDisplaySize;
            // The render sample of pixel i shows the unjittered picture at i + 0.5 − jitter (render pixels).
            vec2 rp = uv * uRenderSize - 0.5 + uJitter;
            ivec2 base = ivec2(floor(rp + 0.5));
            ivec2 last = ivec2(uRenderSize) - 1;
            vec3 sum = vec3(0.0), m1 = vec3(0.0), m2 = vec3(0.0);
            float wsum = 0.0, wmax = 0.0, closest = 2.0;
            vec2 motion = vec2(0.0);
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    ivec2 p = clamp(base + ivec2(x, y), ivec2(0), last);
                    vec3 c = tm(max(texelFetch(uColour, p, 0).rgb, vec3(0.0)));
                    vec2 d = vec2(base + ivec2(x, y)) - rp;
                    float w = exp(-2.29 * dot(d, d));
                    sum += c * w;
                    wsum += w;
                    wmax = max(wmax, w);
                    vec3 q = ycocg(c);
                    m1 += q;
                    m2 += q * q;
                    vec4 m = texelFetch(uMotion, p, 0);
                    if (m.b < closest) { closest = m.b; motion = m.rg; }
                }
            vec3 current = sum / max(wsum, 1e-5);
            vec2 prevUv = uv - motion;
            if (uReset != 0 || any(lessThan(prevUv, vec2(0.0))) || any(greaterThan(prevUv, vec2(1.0))))
            {
                fragColour = vec4(itm(current), 1.0);
                return;
            }
            vec3 mean = m1 / 9.0;
            vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));
            vec3 extent = 1.25 * sigma + 1e-4;
            vec3 h = ycocg(tm(history(prevUv))) - mean;
            vec3 a = abs(h / extent);
            float over = max(a.x, max(a.y, a.z));
            if (over > 1.0) h /= over;
            vec3 hist = rgb(h + mean);
            float alpha = clamp(uBlend * wmax, 0.0, 1.0);
            fragColour = vec4(itm(mix(hist, current, alpha)), 1.0);
        }
        """;
}
