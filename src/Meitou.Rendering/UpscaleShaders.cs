namespace Meitou.Rendering;

/// <summary>GLSL for temporal upscaling (written from the public techniques: Karis 2014, Salvi 2016, Jimenez 2016), run with <see cref="PostProcessShaders.Vertex"/>.</summary>
static class UpscaleShaders
{
    /// <summary>
    /// Camera motion from depth: each render pixel's surface, taken from the near slice's depth (the far slice's where the near one is
    /// empty), is carried back into the previous frame by one matrix per slice (this frame's jittered clip space to the previous frame's
    /// unjittered one, built relative to the eye on the CPU). Writes RG = motion in UV (previous UV = UV − motion, jitter removed),
    /// B = the depth of one D3D-style projection with fixed planes (PostProcess.UpscaleNear .. UpscaleFar), for the upscalers and TAA's
    /// closest-sample pick. A = reactivity: how much the upscalers should trust this frame over the history, set on water (moving, see-through, no motion vectors).
    /// <c>uMode</c> 1 writes the depth alone (R32F for FSR / DLSS), 2 the reactivity alone (R8 for FSR).
    /// </summary>
    public const string Velocity = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth, uFarDepth;
        uniform mat4 uNearToPrev, uFarToPrev;
        uniform vec2 uNearPlanes, uFarPlanes, uFullPlanes;
        uniform vec2 uJitterNdc;
        uniform int uHasFar, uMode;            // 0: motion, depth, reactive; 1: depth alone; 2: reactive alone
        uniform int uClipDepthZeroToOne;       // Metal's NDC depth is 0..1; Vulkan's is -1..1 (stored as 0..1)
        uniform int uFlipDepthY;               // Metal's top-down targets: a standard fullscreen vUv reads the depth bottom-up
        uniform vec2 uTan;                     // tan(fov / 2) * aspect, tan(fov / 2)
        uniform vec3 uRight, uUp, uBack;       // the view axes in the world
        uniform float uEyeY, uWaterY, uWaterReactive;
        uniform vec4 uSun, uSunMotion;         // the sun's direction and its inner cos; its motion, outer cos, on (PostProcess.SunMotion.cs)

        // Distance along the view axis from a depth-buffer value. System.Numerics' projection gives clip depth 0..1; Vulkan's
        // viewport maps that from -1..1, while Metal's NDC depth is already 0..1.
        float ndcDepth(float d) { return uClipDepthZeroToOne != 0 ? d : 2.0 * d - 1.0; }
        float viewZ(float d, vec2 nf) { float zd = ndcDepth(d); return nf.x * nf.y / (nf.y - zd * (nf.y - nf.x)); }

        void main()
        {
            // The NDC below is the projection's (the vertex shader's vUv); the depth image can have the other row
            // order (Metal's targets are top-down, so its standard vUv reads the depth bottom-up).
            vec2 depthUv = uFlipDepthY != 0 ? vec2(vUv.x, 1.0 - vUv.y) : vUv;
            float d = textureLod(uNearDepth, depthUv, 0.0).r;
            mat4 toPrev = uNearToPrev;
            vec2 planes = uNearPlanes;
            if (d >= 1.0 && uHasFar != 0)
            {
                d = textureLod(uFarDepth, depthUv, 0.0).r;
                toPrev = uFarToPrev;
                planes = uFarPlanes;
            }
            float z = viewZ(d, planes);
            float depth = clamp(uFullPlanes.y * (z - uFullPlanes.x) / (z * (uFullPlanes.y - uFullPlanes.x)), 0.0, 1.0);
            if (uMode == 1) { fragColour = vec4(depth, 0.0, 0.0, 1.0); return; }
            vec2 ndc = vUv * 2.0 - 1.0;
            // Water is blended over the seabed without writing depth: a pixel shows water where the eye is above the plane and the
            // surface behind it below (the water quad reaches past the far plane, so the sky below the horizon counts too).
            vec3 offset = (uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack) * z;
            float reactive = uEyeY > uWaterY && uEyeY + offset.y < uWaterY ? uWaterReactive : 0.0;
            if (uMode == 2) { fragColour = vec4(reactive, 0.0, 0.0, 1.0); return; }
            vec4 prev = toPrev * vec4(ndc, ndcDepth(d), 1.0);
            vec2 motion = ((ndc - uJitterNdc) - prev.xy / prev.w) * 0.5;
            // The sky round the sun moves with the sun (PostProcess.SunMotion.cs).
            if (uSunMotion.w != 0.0 && d >= 1.0)
                motion += uSunMotion.xy * smoothstep(uSunMotion.z, uSun.w, dot(normalize(offset), uSun.xyz));
            fragColour = vec4(motion, depth, reactive);
        }
        """;

    /// <summary>
    /// The volumetric clouds' motion over the sky's (docs/render-clouds.md "Temporal, fog and shadows"): the velocity pass gives every sky pixel the motion of the
    /// far plane (the camera's turn alone), but a cloud is a few kilometres away and drifts with the wind, so the upscalers dragged its history (streaks). Here a
    /// sky pixel under a cloud takes the motion of the cloud's opacity-weighted point along its ray: its parallax through the slice's reprojection, plus the
    /// wind's shift on screen. Blended over the velocity target by the cloud's opacity (red and green only; source alpha, one minus source alpha); round the
    /// sun by the pixel's own opacity only, so the sky there keeps the sun's motion (the velocity pass's term).
    /// </summary>
    public const string CloudMotion = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uNearDepth, uFarDepth, uClouds, uCloudDistance;
        uniform mat4 uToPrev;                  // the far slice's reprojection (the near one's when there is no far slice)
        uniform vec2 uPlanes, uJitterNdc;
        uniform int uHasFar, uClipDepthZeroToOne;
        uniform vec2 uTan;                     // tan(fov / 2) * aspect, tan(fov / 2)
        uniform vec3 uRight, uUp, uBack;       // the view axes in the world
        uniform vec4 uSun, uSunMotion;         // as the velocity pass's: the sun's direction and inner cos; its motion, outer cos, on
        uniform vec2 uWind;                    // the clouds' shift since the last frame (world x, z): last frame's content is at point + wind
        float ndcDepth(float d) { return uClipDepthZeroToOne != 0 ? d : 2.0 * d - 1.0; }
        vec2 project(vec3 v) { float z = -dot(v, uBack); return vec2(dot(v, uRight) / (z * uTan.x), dot(v, uUp) / (z * uTan.y)); }
        void main()
        {
            vec2 depthUv = vUv;
            if (textureLod(uNearDepth, depthUv, 0.0).r < 1.0 || (uHasFar != 0 && textureLod(uFarDepth, depthUv, 0.0).r < 1.0)) discard;
            // The cloud's motion wherever a cloud is near, not by this pixel's opacity: a pixel a cloud has just left (or its thin edge) kept the sky's
            // motion, so DLSS fetched last frame's cloud in place and trailed it (rows of streaks behind drifting edges, camera still). The cost: a star or
            // planet within that reach moves with the cloud too. 3 x 3 taps two texels apart (about 4 render pixels round): the strongest opacity sets the
            // weight, the opacity-weighted distance the depth.
            vec2 texel = 1.0 / vec2(textureSize(uClouds, 0));
            float strongest = 0.0, own = 0.0, opacitySum = 0.0, distanceSum = 0.0;
            for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    vec2 uv = depthUv + vec2(x, y) * 2.0 * texel;
                    float a = textureLod(uClouds, uv, 0.0).a;
                    float d = textureLod(uCloudDistance, uv, 0.0).r;
                    if (d <= 0.0) continue;
                    if (x == 0 && y == 0) own = a;
                    strongest = max(strongest, a);
                    opacitySum += a;
                    distanceSum += a * d;
                }
            vec2 ndc = vUv * 2.0 - 1.0;
            vec3 ray = normalize(uRight * ndc.x * uTan.x + uUp * ndc.y * uTan.y - uBack);
            float sun = uSunMotion.w != 0.0 ? smoothstep(uSunMotion.z, uSun.w, dot(ray, uSun.xyz)) : 0.0;
            float weight = mix(smoothstep(0.0, 0.03, strongest), own, sun);   // round the sun by this pixel's opacity: the sky keeps the sun's motion
            if (weight <= 0.0 || opacitySum <= 0.0) discard;
            float distance = distanceSum / opacitySum;
            vec3 point = ray * distance;
            float z = -dot(point, uBack);
            // The point's depth in the slice's projection, reprojected as the velocity pass reprojects a surface.
            float stored = uPlanes.y * (1.0 - uPlanes.x / z) / (uPlanes.y - uPlanes.x);
            float zd = uClipDepthZeroToOne != 0 ? stored : 2.0 * stored - 1.0;
            vec4 prev = uToPrev * vec4(ndc, zd, 1.0);
            vec2 motion = ((ndc - uJitterNdc) - prev.xy / prev.w) * 0.5;
            // The wind: last frame this texel's cloud was at point + wind (VolumetricCloudShaders.Resolve).
            motion -= (project(point + vec3(uWind.x, 0.0, uWind.y)) - project(point)) * 0.5;
            fragColour = vec4(motion, 0.0, weight);
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
        // NaN and Inf to 0: max() need not drop a NaN (AMD keeps it), and one kept in the history spreads through the taps frame after frame.
        float finite(float v) { return isnan(v) || isinf(v) ? 0.0 : v; }
        vec3 finite(vec3 c) { return vec3(finite(c.r), finite(c.g), finite(c.b)); }

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
            return max(finite(s / w), vec3(0.0));
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
                    vec3 c = tm(max(finite(texelFetch(uColour, p, 0).rgb), vec3(0.0)));
                    vec2 d = (vec2(base + ivec2(x, y)) - rp) * uDisplaySize / uRenderSize;   // in display pixels, so upscaling doesn't widen it
                    float w = exp(-2.29 * dot(d, d));
                    sum += c * w;
                    wsum += w;
                    wmax = max(wmax, w);
                    vec3 q = ycocg(c);
                    m1 += q;
                    m2 += q * q;
                    vec4 m = texelFetch(uMotion, p, 0);
                    if (m.b < closest) { closest = m.b; motion = vec2(finite(m.r), finite(m.g)); }
                }
            vec3 current = sum / max(wsum, 1e-5);
            vec2 prevUv = uv - motion;
            if (uReset != 0 || any(lessThan(prevUv, vec2(0.0))) || any(greaterThan(prevUv, vec2(1.0))))
            {
                fragColour = vec4(finite(itm(current)), 1.0);
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
            // Reactive pixels (water) lean on the current frame.
            float reactive = texelFetch(uMotion, clamp(base, ivec2(0), last), 0).a;
            float alpha = clamp(max(uBlend * wmax, 0.5 * reactive), 0.0, 1.0);
            fragColour = vec4(finite(itm(mix(hist, current, alpha))), 1.0);
        }
        """;
}
