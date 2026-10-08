namespace Meitou.Rendering;

/// <summary>The viewer's own GLSL (GLSL 3.30 core): skinning, a directional light, optional normal and dual maps.</summary>
static class Shaders
{
    public const int MaxBones = 128;

    public const string MeshVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aTangent;
        layout(location = 4) in vec4 aColour;
        layout(location = 5) in uvec4 aBones;
        layout(location = 6) in vec4 aWeights;

        uniform mat4 uViewProjection;
        uniform mat4 uModel;
        uniform bool uSkinned;
        uniform mat4 uBones[128];

        out vec3 vWorld;
        out vec3 vNormal;
        out vec4 vTangent;
        out vec2 vUv;
        out vec4 vColour;

        void main()
        {
            mat4 skin = mat4(1.0);
            if (uSkinned && dot(aWeights, vec4(1.0)) > 0.0)
                skin = uBones[aBones.x] * aWeights.x + uBones[aBones.y] * aWeights.y
                     + uBones[aBones.z] * aWeights.z + uBones[aBones.w] * aWeights.w;
            mat4 world = uModel * skin;
            vec4 p = world * vec4(aPosition, 1.0);
            vWorld = p.xyz;
            mat3 n = mat3(world);
            vNormal = n * aNormal;
            vTangent = vec4(n * aTangent.xyz, aTangent.w);
            vUv = aUv;
            vColour = aColour;
            gl_Position = uViewProjection * p;
        }
        """;

    public static readonly string MeshFragment = "#version 330 core\n" + AtmosphereShaders.Functions + """
        in vec3 vWorld;
        in vec3 vNormal;
        in vec4 vTangent;
        in vec2 vUv;
        in vec4 vColour;

        uniform sampler2D uDiffuse;
        uniform sampler2D uNormal;
        uniform sampler2D uDiffuse2;
        uniform sampler2D uNormal2;
        uniform sampler2D uHeadDiffuse;
        uniform sampler2D uHeadNormal;
        uniform bool uHasHead;
        uniform bool uNormalSwizzled;   // X in alpha, Y in green, Z rebuilt ("DXT5 normal")
        uniform bool uHasDiffuse;
        uniform bool uHasNormal;
        uniform bool uHasDual;
        uniform bool uTriplanar;
        uniform float uTriplanarScale;
        uniform vec2 uTile;
        uniform int uAlphaSource;      // 0 none, 1 diffuse alpha, 2 normal-map alpha, 3 diffuse channel
        uniform int uAlphaChannel;
        uniform int uGreyChannel;      // >= 0: diffuse is one channel of the texture, tinted
        uniform vec3 uTint;
        uniform float uAlphaThreshold;
        uniform bool uEmissive;
        uniform bool uUseVertexColour;
        uniform float uSpecular;
        uniform vec3 uLightDir;        // towards the light
        uniform vec3 uEye;
        uniform bool uWireframe;
        uniform vec3 uFlatColour;
        uniform uint uSurface;         // weather bits of the draw (below)
        uniform vec3 uFogColour;       // world view: distance haze (off while uFogDistance is 0)
        uniform float uFogDistance;

        out vec4 fragColour;

        vec4 sampleMap(sampler2D map, vec2 uv, vec3 n)
        {
            if (!uTriplanar) return texture(map, uv);
            vec3 w = abs(normalize(n));
            w /= (w.x + w.y + w.z);
            vec3 p = vWorld * uTriplanarScale;
            return texture(map, p.zy * uTile) * w.x + texture(map, p.xz * uTile) * w.y + texture(map, p.xy * uTile) * w.z;
        }

        vec3 decodeNormal(vec4 t)
        {
            if (!uNormalSwizzled) return t.xyz * 2.0 - 1.0;
            vec2 xy = vec2(t.a, t.g) * 2.0 - 1.0;
            return vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
        }

        void main()
        {
            if (uWireframe) { fragColour = vec4(uFlatColour, 1.0); return; }
            vec3 n = normalize(vNormal);
            if (!gl_FrontFacing) n = -n;
            vec2 uv = vUv * uTile;
            float blend = uHasDual ? 1.0 - vColour.a : 0.0; // vertex alpha 1 = first set, 0 = second (Kenshi objects.hlsl)

            vec4 base = vec4(0.72, 0.72, 0.72, 1.0);
            if (uHasDiffuse)
            {
                base = sampleMap(uDiffuse, uv, n);
                if (uHasDual) base = mix(base, sampleMap(uDiffuse2, uv, n), blend);
                if (uHasHead) base += texture(uHeadDiffuse, uv + vec2(0.0, 1.0));
            }
            float alpha = 1.0;
            vec4 nm = vec4(0.5, 0.5, 1.0, 1.0);
            if (uHasNormal)
            {
                nm = sampleMap(uNormal, uv, n);
                if (uHasDual) nm = mix(nm, sampleMap(uNormal2, uv, n), blend);
                if (uHasHead) nm += texture(uHeadNormal, uv + vec2(0.0, 1.0));
            }
            if (uAlphaSource == 1) alpha = base.a;
            else if (uAlphaSource == 2) alpha = nm.a;
            else if (uAlphaSource == 3) alpha = base[uAlphaChannel];
            if (uAlphaThreshold > 0.0 && alpha < uAlphaThreshold) discard;
            float gloss = uAlphaSource == 1 || uAlphaSource == 3 ? 0.3 : base.a; // diffuse alpha is shininess (fcs.def)
            vec3 albedo = uGreyChannel >= 0 ? vec3(base[uGreyChannel]) * uTint : base.rgb;
            if (uUseVertexColour) albedo *= vColour.rgb;

            vec3 geometricNormal = n;
            if (uHasNormal && !uTriplanar && dot(vTangent.xyz, vTangent.xyz) > 1e-8)
            {
                vec3 t = normalize(vTangent.xyz - n * dot(n, vTangent.xyz));
                vec3 b = cross(n, t) * vTangent.w;
                vec3 tn = decodeNormal(nm);
                n = normalize(t * tn.x + b * tn.y + n * tn.z);
            }

            // The weather's dust and wetness (objects.hlsl, triplanar.hlsl, foliage.hlsl; docs/formats/weather.md). uSurface: 1 DUST, 2 foliage shader (a fixed
            // absorbance), 4 no weather (an impostor bake), 8 interior (the dust of inside, no rain).
            float glossLit = gloss * uSpecular;
            if ((uSurface & 4u) == 0u)
            {
                bool inside = (uSurface & 8u) != 0u;
                float dustAmount = inside ? uWeatherDust.y : uWeatherDust.x;
                if (((uSurface & 1u) != 0u || ((uSurface & 16u) != 0u && uWeatherDust.w > 0.5)) && dustAmount > 0.0)
                {
                    float dust = dustCover(n, gloss, vWorld, dustAmount);
                    albedo = mix(albedo, dustColour(vWorld), dust);
                    n = normalize(mix(n, geometricNormal, clamp(dust * 0.5, 0.0, 1.0)));   // flatten the normal map
                }
                if (!inside && uWeatherWet.x > 0.0)
                {
                    // Absorbance (1 - gloss)(1 - metalness) for objects (the viewer has no metalness map), 0.9 for foliage; the water line is not drawn here.
                    vec4 wetSurface = vec4(albedo, glossLit);
                    makeWet(wetSurface, uWeatherWet.x, (uSurface & 2u) != 0u ? 0.9 : 1.0 - glossLit, -1.0e4, 0.5);
                    albedo = wetSurface.rgb;
                    glossLit = wetSurface.a;
                }
            }

            vec3 l = normalize(uLightDir);
            vec3 v = normalize(uEye - vWorld);
            float diff = max(dot(n, l), 0.0);
            float hemi = 0.5 + 0.5 * n.y;
            vec3 ambient = mix(vec3(0.22, 0.20, 0.18), vec3(0.42, 0.45, 0.50), hemi);
            vec3 h = normalize(l + v);
            float spec = pow(max(dot(n, h), 0.0), 8.0 + 56.0 * gloss) * gloss * uSpecular * 0.5;
            vec3 sunLight = vec3(1.0, 0.97, 0.92);
            vec3 colour = albedo * (ambient + diff * sunLight) + spec * diff * sunLight;
            // World view, game sky: the game's deferred lighting (docs/formats/lighting.md); its gloss is diffuse alpha times `specular mult`.
            if (uFogDistance > 0.0 && uAtmoParams.x > 0.5) colour = kenshiLight(albedo, n, v, glossLit, vWorld);
            if (uEmissive) colour += albedo * nm.a;
            if (uFogDistance > 0.0) colour = atmoApply(colour, uEye, vWorld);   // aerial perspective (AtmosphereShaders)
            fragColour = vec4(colour, 1.0);
        }
        """;

    public const string LineVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aColour;
        uniform mat4 uViewProjection;
        uniform mat4 uModel;
        out vec3 vColour;
        void main() { vColour = aColour; gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0); }
        """;

    public const string LineFragment = """
        #version 330 core
        in vec3 vColour;
        out vec4 fragColour;
        void main() { fragColour = vec4(vColour, 1.0); }
        """;
}
