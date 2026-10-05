namespace Meitou.Rendering;

/// <summary>GLSL for the world view's post-processing chain (written from the public techniques, not from Kenshi's shaders).</summary>
static class PostProcessShaders
{
    /// <summary>Full-screen triangle; <c>vUv</c> runs 0..1 over the screen.</summary>
    public const string Vertex = """
        #version 330 core
        out vec2 vUv;
        void main()
        {
            vec2 p = vec2((gl_VertexID & 1) * 4.0 - 1.0, (gl_VertexID & 2) * 2.0 - 1.0);
            vUv = p * 0.5 + 0.5;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <summary>Interleaved gradient noise (Jimenez): a cheap per-pixel value in 0..1.</summary>
    const string Noise = """
        float ign(vec2 p) { return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715)))); }
        """;

    /// <summary>
    /// Screen-space ambient occlusion at half resolution, from depth alone (normals from depth differences). Alchemy-style:
    /// each of 12 spiral taps adds the cosine of the sample direction against the normal over its distance, ignoring
    /// samples further than twice the radius. Writes R = occlusion factor (1 = open), G = view depth for the blur.
    /// </summary>
    public const string Ssao = "#version 330 core\n" + Noise + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uDepth;
        uniform vec2 uTan;      // tan(fov/2) * aspect, tan(fov/2)
        uniform vec2 uNearFar;  // of the near depth slice
        uniform vec2 uSize;     // full-resolution size
        uniform float uRadius, uStrength, uFadeStart, uFadeEnd;

        float viewZ(float d)
        {
            float n = 2.0 * d - 1.0;
            return 2.0 * uNearFar.x * uNearFar.y / (uNearFar.y + uNearFar.x - n * (uNearFar.y - uNearFar.x));
        }
        vec3 viewPos(vec2 uv)
        {
            float z = viewZ(textureLod(uDepth, uv, 0.0).r);
            return vec3((uv * 2.0 - 1.0) * uTan * z, -z);
        }
        void main()
        {
            float d = textureLod(uDepth, vUv, 0.0).r;
            if (d >= 0.99999) { fragColour = vec4(1.0, 60000.0, 0.0, 1.0); return; }
            vec2 px = 1.0 / uSize;
            vec3 p = viewPos(vUv);
            float z = -p.z;
            vec3 l = viewPos(vUv - vec2(px.x, 0.0)), r = viewPos(vUv + vec2(px.x, 0.0));
            vec3 u = viewPos(vUv + vec2(0.0, px.y)), b = viewPos(vUv - vec2(0.0, px.y));
            vec3 dx = abs(r.z - p.z) < abs(p.z - l.z) ? r - p : p - l;
            vec3 dy = abs(u.z - p.z) < abs(p.z - b.z) ? u - p : p - b;
            vec3 n = normalize(cross(dx, dy));
            if (n.z < 0.0) n = -n;

            float radius = max(uRadius, 0.03 * z); // world radius, at least a few percent of the distance so far objects still get contact shading
            vec2 radiusUv = 0.5 * radius / (uTan * z);
            radiusUv = min(radiusUv, vec2(0.06)); // bound the cost of close-ups
            float spin = ign(gl_FragCoord.xy) * 6.2831853;
            float sum = 0.0;
            const int N = 12;
            for (int i = 0; i < N; i++)
            {
                float t = (float(i) + 0.5) / float(N);
                float a = spin + float(i) * 2.399963;
                vec2 uv = vUv + vec2(cos(a), sin(a)) * radiusUv * t;
                vec3 v = viewPos(uv) - p;
                float len = length(v);
                float vn = dot(v, n) - 0.02 * z;
                float range = 1.0 - smoothstep(radius, 2.0 * radius, len);
                sum += max(vn, 0.0) / (len * len + 0.05 * radius * radius) * radius * range;
            }
            float ao = 1.0 - clamp(uStrength * sum / float(N), 0.0, 1.0);
            ao = mix(ao, 1.0, smoothstep(uFadeStart, uFadeEnd, z));
            fragColour = vec4(ao, z, 0.0, 1.0);
        }
        """;

    /// <summary>Depth-aware blur (7 taps) of the occlusion, one direction per pass.</summary>
    public const string SsaoBlur = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uAo;
        uniform vec2 uStep;
        void main()
        {
            vec2 c = texture(uAo, vUv).rg;
            float sum = c.r, weights = 1.0;
            const float w[3] = float[3](0.75, 0.45, 0.2);
            for (int i = 1; i <= 3; i++)
            for (int s = -1; s <= 1; s += 2)
            {
                vec2 t = texture(uAo, vUv + uStep * float(i * s)).rg;
                float dw = exp(-abs(t.g - c.g) / max(c.g * 0.02, 0.05));
                sum += t.r * w[i - 1] * dw;
                weights += w[i - 1] * dw;
            }
            fragColour = vec4(sum / weights, c.g, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Kenshi's luminance measure (docs/formats/post-processing.md): Rec. 601 luminance, floored at 0.0001, averaged linearly. One texel of the
    /// small luminance target is the mean of a 4 × 4 grid of bilinear taps over its share of the scene; its mipmaps average the rest.
    /// </summary>
    public const string Luminance = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene;
        uniform vec2 uCell;   // one texel of this target in the scene's 0..1 coordinates
        void main()
        {
            float sum = 0.0;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    vec2 uv = vUv + (vec2(x, y) - 1.5) * 0.25 * uCell;
                    sum += max(dot(texture(uScene, uv).rgb, vec3(0.299, 0.587, 0.114)), 0.0001);
                }
            fragColour = vec4(sum / 16.0, 0.0, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// Kenshi's adaptation: <c>adapted = last + (mean − last) (1 − exp(−dt · rate))</c>, clamped to <c>[MIN_LUMINANCE, MAX_LUMINANCE]</c>; a 1 × 1 target.
    /// </summary>
    public const string Adapt = """
        #version 330 core
        out vec4 fragColour;
        uniform sampler2D uLuminance, uLast;
        uniform float uLevel, uBlend;   // the luminance target's 1 × 1 level; the share of the new mean (1: no smoothing)
        uniform vec2 uBand;             // MIN_LUMINANCE, MAX_LUMINANCE
        void main()
        {
            float mean = textureLod(uLuminance, vec2(0.5), uLevel).r;
            float last = texture(uLast, vec2(0.5)).r;
            float adapted = mix(last, mean, uBlend);
            fragColour = vec4(clamp(adapted, uBand.x, max(uBand.x, uBand.y)), mean, 0.0, 1.0);
        }
        """;

    /// <summary>Exposure, occlusion, the game's clip at 1 (no tone curve) and dither: HDR scene in, display colour out.</summary>
    public const string Composite = "#version 330 core\n" + Noise + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene, uAo, uAdapted;
        uniform float uExposure;
        uniform int uUseAo, uDither, uDebug, uAuto;
        const float EXPOSURE_KEY = 0.55;   // hdr.material's EXPOSURE_KEY

        void main()
        {
            float exposure = uExposure;
            if (uAuto != 0) exposure *= max(EXPOSURE_KEY / texture(uAdapted, vec2(0.5)).r, 0.001);   // Kenshi's exposure: key over the adapted luminance
            vec3 c = max(texture(uScene, vUv).rgb, 0.0) * exposure;
            if (uUseAo != 0) c *= texture(uAo, vUv).r;
            c = clamp(c, 0.0, 1.0);   // Kenshi has no tone curve: values over 1 clip (docs/formats/post-processing.md)
            if (uDebug == 1) c = vec3(texture(uAo, vUv).r);
            if (uDither != 0) c += (ign(gl_FragCoord.xy) + ign(gl_FragCoord.xy + 17.0) - 1.0) / 255.0;
            fragColour = vec4(c, 1.0);
        }
        """;

    /// <summary>
    /// FXAA on the final LDR picture, with the game's settings (docs/formats/post-processing.md "FXAA"): Lottes' FXAA 3.11 quality
    /// algorithm, green as luma, sub-pixel amount 0.75, edge threshold 0.166, minimum 0.0833, the end-of-edge search in the steps of
    /// quality preset 12 (1, 1.5, 2, 4, 12 pixels). Finds the local contrast, the edge's direction and which side it runs along,
    /// searches both ways for the edge's ends, and moves the lookup across the edge by how far the pixel is from the nearer end,
    /// or by the sub-pixel blend for lone pixels, whichever is larger.
    /// </summary>
    public const string Fxaa = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uImage;
        uniform vec2 uTexel;
        const float SUBPIX = 0.75, EDGE_THRESHOLD = 0.166, EDGE_THRESHOLD_MIN = 0.0833;
        const int STEPS = 5;
        const float STEP_SIZE[STEPS] = float[](1.0, 1.5, 2.0, 4.0, 12.0);

        float luma(vec2 p) { return textureLod(uImage, p, 0.0).g; }
        float lumaAt(vec2 p, vec2 o) { return luma(p + o * uTexel); }

        void main()
        {
            vec2 pos = vUv;
            vec4 centre = textureLod(uImage, pos, 0.0);
            float m = centre.g;
            float n = lumaAt(pos, vec2(0.0, -1.0)), s = lumaAt(pos, vec2(0.0, 1.0));
            float w = lumaAt(pos, vec2(-1.0, 0.0)), e = lumaAt(pos, vec2(1.0, 0.0));
            float hi = max(max(max(n, s), max(w, e)), m), lo = min(min(min(n, s), min(w, e)), m);
            float range = hi - lo;
            if (range < max(EDGE_THRESHOLD_MIN, hi * EDGE_THRESHOLD)) { fragColour = centre; return; }

            float nw = lumaAt(pos, vec2(-1.0, -1.0)), ne = lumaAt(pos, vec2(1.0, -1.0));
            float sw = lumaAt(pos, vec2(-1.0, 1.0)), se = lumaAt(pos, vec2(1.0, 1.0));

            // Direction: second differences across rows (a horizontal edge) against across columns (a vertical one).
            float horizontal = abs(nw - 2.0 * w + sw) + 2.0 * abs(n - 2.0 * m + s) + abs(ne - 2.0 * e + se);
            float vertical = abs(nw - 2.0 * n + ne) + 2.0 * abs(w - 2.0 * m + e) + abs(sw - 2.0 * s + se);
            bool horizontalSpan = horizontal >= vertical;

            // The neighbour across the edge with the larger gradient decides which side the edge lies on.
            float a = horizontalSpan ? n : w, b = horizontalSpan ? s : e;
            float gradientA = abs(a - m), gradientB = abs(b - m);
            float stepAcross = horizontalSpan ? uTexel.y : uTexel.x;
            bool sideA = gradientA >= gradientB;
            if (sideA) stepAcross = -stepAcross;
            float edgeLuma = 0.5 * ((sideA ? a : b) + m);
            float gradient = 0.25 * max(gradientA, gradientB);

            // Walk along the edge, half a pixel over onto it, until the luma leaves the edge's average on either end.
            vec2 along = horizontalSpan ? vec2(uTexel.x, 0.0) : vec2(0.0, uTexel.y);
            vec2 onEdge = pos + (horizontalSpan ? vec2(0.0, 0.5 * stepAcross) : vec2(0.5 * stepAcross, 0.0));
            vec2 posN = onEdge - along * STEP_SIZE[0], posP = onEdge + along * STEP_SIZE[0];
            float endN = luma(posN) - edgeLuma, endP = luma(posP) - edgeLuma;
            bool doneN = abs(endN) >= gradient, doneP = abs(endP) >= gradient;
            for (int i = 1; i < STEPS && !(doneN && doneP); i++)
            {
                if (!doneN) { posN -= along * STEP_SIZE[i]; endN = luma(posN) - edgeLuma; doneN = abs(endN) >= gradient; }
                if (!doneP) { posP += along * STEP_SIZE[i]; endP = luma(posP) - edgeLuma; doneP = abs(endP) >= gradient; }
            }
            float distN = horizontalSpan ? pos.x - posN.x : pos.y - posN.y;
            float distP = horizontalSpan ? posP.x - pos.x : posP.y - pos.y;
            bool nearerN = distN < distP;
            float nearest = min(distN, distP);
            // Only blend when the nearer end turns the other way from the centre (the pixel lies on the edge's stair).
            bool centreBelow = m - edgeLuma < 0.0;
            bool goodSpan = ((nearerN ? endN : endP) < 0.0) != centreBelow;
            float edgeOffset = goodSpan ? 0.5 - nearest / (distN + distP) : 0.0;

            // Sub-pixel aliasing: how far the centre is from its 3 × 3 neighbourhood's weighted mean, relative to the range.
            float mean = (2.0 * (n + s + w + e) + (nw + ne + sw + se)) / 12.0;
            float t = clamp(abs(mean - m) / range, 0.0, 1.0);
            float subpix = (-2.0 * t + 3.0) * t * t;
            float subpixOffset = subpix * subpix * SUBPIX;

            float offset = max(edgeOffset, subpixOffset);
            vec2 p = pos + (horizontalSpan ? vec2(0.0, offset * stepAcross) : vec2(offset * stepAcross, 0.0));
            fragColour = vec4(textureLod(uImage, p, 0.0).rgb, 1.0);
        }
        """;
}
