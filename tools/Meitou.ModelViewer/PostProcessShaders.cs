namespace Meitou.ModelViewer;

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
    /// First bloom level: 13-tap downsample of the scene, each sample reduced to what is over the threshold (soft
    /// knee), with Karis' brightness weighting so a single hot pixel cannot flicker.
    /// </summary>
    public const string BloomPrefilter = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSrc;
        uniform vec2 uTexel;
        uniform float uThreshold;
        vec3 over(vec3 c)
        {
            c = clamp(c, 0.0, 4.0); // a blown-out glint must not feed the blur with unbounded energy
            float br = max(c.r, max(c.g, c.b));
            float knee = 0.5 * uThreshold + 1e-4;
            float rq = clamp(br - uThreshold + knee, 0.0, 2.0 * knee);
            rq = rq * rq / (4.0 * knee);
            return c * max(rq, br - uThreshold) / max(br, 1e-4);
        }
        vec3 tap(vec2 o) { return over(texture(uSrc, vUv + o * uTexel).rgb); }
        float karis(vec3 c) { return 1.0 / (1.0 + dot(c, vec3(0.2126, 0.7152, 0.0722))); }
        void main()
        {
            vec3 a = tap(vec2(-2, 2)), b = tap(vec2(0, 2)), c = tap(vec2(2, 2));
            vec3 d = tap(vec2(-2, 0)), e = tap(vec2(0, 0)), f = tap(vec2(2, 0));
            vec3 g = tap(vec2(-2, -2)), h = tap(vec2(0, -2)), i = tap(vec2(2, -2));
            vec3 j = tap(vec2(-1, 1)), k = tap(vec2(1, 1)), l = tap(vec2(-1, -1)), m = tap(vec2(1, -1));
            vec3 g0 = (a + b + d + e) * 0.25, g1 = (b + c + e + f) * 0.25, g2 = (d + e + g + h) * 0.25, g3 = (e + f + h + i) * 0.25, g4 = (j + k + l + m) * 0.25;
            float w0 = 0.125 * karis(g0), w1 = 0.125 * karis(g1), w2 = 0.125 * karis(g2), w3 = 0.125 * karis(g3), w4 = 0.5 * karis(g4);
            fragColour = vec4((g0 * w0 + g1 * w1 + g2 * w2 + g3 * w3 + g4 * w4) / (w0 + w1 + w2 + w3 + w4), 1.0);
        }
        """;

    /// <summary>13-tap downsample (the Call of Duty: Advanced Warfare filter).</summary>
    public const string BloomDown = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSrc;
        uniform vec2 uTexel;
        vec3 tap(vec2 o) { return texture(uSrc, vUv + o * uTexel).rgb; }
        void main()
        {
            vec3 a = tap(vec2(-2, 2)), b = tap(vec2(0, 2)), c = tap(vec2(2, 2));
            vec3 d = tap(vec2(-2, 0)), e = tap(vec2(0, 0)), f = tap(vec2(2, 0));
            vec3 g = tap(vec2(-2, -2)), h = tap(vec2(0, -2)), i = tap(vec2(2, -2));
            vec3 j = tap(vec2(-1, 1)), k = tap(vec2(1, 1)), l = tap(vec2(-1, -1)), m = tap(vec2(1, -1));
            fragColour = vec4(e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125, 1.0);
        }
        """;

    /// <summary>3x3 tent upsample, added onto the level above by blending.</summary>
    public const string BloomUp = """
        #version 330 core
        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSrc;
        uniform vec2 uTexel;
        void main()
        {
            vec3 s = texture(uSrc, vUv + vec2(-1, 1) * uTexel).rgb + texture(uSrc, vUv + vec2(1, 1) * uTexel).rgb
                   + texture(uSrc, vUv + vec2(-1, -1) * uTexel).rgb + texture(uSrc, vUv + vec2(1, -1) * uTexel).rgb;
            s += 2.0 * (texture(uSrc, vUv + vec2(0, 1) * uTexel).rgb + texture(uSrc, vUv + vec2(0, -1) * uTexel).rgb
                      + texture(uSrc, vUv + vec2(-1, 0) * uTexel).rgb + texture(uSrc, vUv + vec2(1, 0) * uTexel).rgb);
            s += 4.0 * texture(uSrc, vUv).rgb;
            fragColour = vec4(s / 16.0, 1.0);
        }
        """;

    /// <summary>Exposure, occlusion, bloom, tone map, grade, vignette and dither: HDR scene in, display colour out.</summary>
    public const string Composite = "#version 330 core\n" + Noise + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uScene, uAo, uBloom;
        uniform float uExposure, uBloomIntensity, uSaturation, uContrast, uVignette;
        uniform int uUseAo, uUseBloom, uTone, uGrade, uDither, uDebug;

        vec3 shoulder(vec3 c)
        {
            const float k = 0.8;
            float m = max(c.r, max(c.g, c.b));
            if (m <= k) return c;
            float f = k + (1.0 - k) * (1.0 - exp(-(m - k) / (1.0 - k)));
            return c * (f / m);
        }
        vec3 aces(vec3 x) { return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0); }

        void main()
        {
            vec3 c = max(texture(uScene, vUv).rgb, 0.0) * uExposure;
            if (uUseAo != 0) c *= texture(uAo, vUv).r;
            if (uUseBloom != 0) c += min(texture(uBloom, vUv).rgb * uBloomIntensity, vec3(0.5));
            if (uTone == 1) c = shoulder(c);
            else if (uTone == 2) c = aces(c);
            c = clamp(c, 0.0, 1.0);
            if (uGrade != 0)
            {
                float l = dot(c, vec3(0.299, 0.587, 0.114));
                c = mix(vec3(l), c, uSaturation);
                c = clamp((c - 0.5) * uContrast + 0.5, 0.0, 1.0);
            }
            if (uVignette > 0.0)
            {
                vec2 q = vUv - 0.5;
                c *= 1.0 - uVignette * smoothstep(0.25, 0.9, dot(q, q) * 2.0);
            }
            if (uDebug == 1) c = vec3(texture(uAo, vUv).r);
            else if (uDebug == 2) c = texture(uBloom, vUv).rgb * uBloomIntensity;
            if (uDither != 0) c += (ign(gl_FragCoord.xy) + ign(gl_FragCoord.xy + 17.0) - 1.0) / 255.0;
            fragColour = vec4(c, 1.0);
        }
        """;

    /// <summary>
    /// FXAA from the public algorithm: luma contrast test, sub-pixel blend, edge direction, end-of-edge search (8 steps of
    /// growing size) and a shift across the edge. Kenshi runs FXAA 3.11 with sub-pixel 0.75, edge threshold 0.166 and
    /// minimum 0.0833 and green as luma; the same constants are used here (the search is shorter than its quality preset 12).
    /// </summary>
    public const string Fxaa = "#version 330 core\n" + Noise + """

        in vec2 vUv;
        out vec4 fragColour;
        uniform sampler2D uSrc;
        uniform vec2 uTexel;
        uniform float uSubpix;
        uniform int uDither;
        const float EDGE_THRESHOLD = 0.166, EDGE_MIN = 0.0833;
        float luma(vec2 uv) { return texture(uSrc, uv).g; }
        void main()
        {
            vec3 cM = texture(uSrc, vUv).rgb;
            float lM = cM.g;
            float lN = luma(vUv + vec2(0, uTexel.y)), lS = luma(vUv - vec2(0, uTexel.y));
            float lE = luma(vUv + vec2(uTexel.x, 0)), lW = luma(vUv - vec2(uTexel.x, 0));
            float lMax = max(max(max(lN, lS), max(lE, lW)), lM), lMin = min(min(min(lN, lS), min(lE, lW)), lM);
            float range = lMax - lMin;
            if (range < max(EDGE_MIN, lMax * EDGE_THRESHOLD))
            {
                if (uDither != 0) cM += (ign(gl_FragCoord.xy) + ign(gl_FragCoord.xy + 17.0) - 1.0) / 255.0;
                fragColour = vec4(cM, 1.0);
                return;
            }
            float lNE = luma(vUv + uTexel), lNW = luma(vUv + vec2(-uTexel.x, uTexel.y));
            float lSE = luma(vUv + vec2(uTexel.x, -uTexel.y)), lSW = luma(vUv - uTexel);

            float lAvg = (2.0 * (lN + lS + lE + lW) + lNE + lNW + lSE + lSW) / 12.0;
            float sub = smoothstep(0.0, 1.0, clamp(abs(lAvg - lM) / range, 0.0, 1.0));
            sub = sub * sub * uSubpix;

            float hor = 2.0 * abs(lN + lS - 2.0 * lM) + abs(lNE + lSE - 2.0 * lE) + abs(lNW + lSW - 2.0 * lW);
            float ver = 2.0 * abs(lE + lW - 2.0 * lM) + abs(lNE + lNW - 2.0 * lN) + abs(lSE + lSW - 2.0 * lS);
            bool isH = hor >= ver;
            float pStep = isH ? uTexel.y : uTexel.x;
            float lPos = isH ? lN : lE, lNeg = isH ? lS : lW;
            float gPos = abs(lPos - lM), gNeg = abs(lNeg - lM);
            float opp, grad;
            if (gPos < gNeg) { pStep = -pStep; opp = lNeg; grad = gNeg; } else { opp = lPos; grad = gPos; }

            vec2 uvEdge = vUv;
            vec2 along = isH ? vec2(uTexel.x, 0.0) : vec2(0.0, uTexel.y);
            if (isH) uvEdge.y += pStep * 0.5; else uvEdge.x += pStep * 0.5;
            float edgeL = 0.5 * (lM + opp);
            float thresh = 0.25 * grad;

            const float steps[8] = float[8](1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 2.0, 4.0);
            vec2 pUv = uvEdge + along;
            float pD = luma(pUv) - edgeL;
            bool pEnd = abs(pD) >= thresh;
            for (int i = 0; i < 8 && !pEnd; i++) { pUv += along * steps[i]; pD = luma(pUv) - edgeL; pEnd = abs(pD) >= thresh; }
            if (!pEnd) pUv += along * 8.0;
            vec2 nUv = uvEdge - along;
            float nD = luma(nUv) - edgeL;
            bool nEnd = abs(nD) >= thresh;
            for (int i = 0; i < 8 && !nEnd; i++) { nUv -= along * steps[i]; nD = luma(nUv) - edgeL; nEnd = abs(nD) >= thresh; }
            if (!nEnd) nUv -= along * 8.0;

            float dP = isH ? pUv.x - vUv.x : pUv.y - vUv.y;
            float dN = isH ? vUv.x - nUv.x : vUv.y - nUv.y;
            bool positive = dP < dN ? pD >= 0.0 : nD >= 0.0;
            float edgeBlend = positive == (lM - edgeL >= 0.0) ? 0.0 : 0.5 - min(dP, dN) / (dP + dN);
            float blend = max(sub, edgeBlend);
            vec2 uv = vUv + (isH ? vec2(0.0, pStep) : vec2(pStep, 0.0)) * blend;
            vec3 c = texture(uSrc, uv).rgb;
            if (uDither != 0) c += (ign(gl_FragCoord.xy) + ign(gl_FragCoord.xy + 17.0) - 1.0) / 255.0;
            fragColour = vec4(c, 1.0);
        }
        """;
}
