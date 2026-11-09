using Meitou.Game.Rendering.Frame;
namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The <c>chars2</c> switch's shading of the characters (docs/character-renderer.md "Hair, cloth, skin and eyes"): what a light adds to a fragment on top of
/// <c>kenshiLight</c>, the sun's and the lamps' alike. <c>pc.fill.a</c> carries the switch, so one program serves both sides and off runs none of it.
/// </summary>
static class CharacterSurface
{
    /// <summary>Ahead of the atmosphere text: what its two hooks (<see cref="Lighting"/>) call.</summary>
    public const string Prototypes = """
        bool surfaceOn = false;
        vec3 surfaceSun(vec3 sun, float sunScale, vec3 diffuseAlbedo);
        vec3 surfaceSpecular(vec3 l);

        """;

    /// <summary>The atmosphere text with the two hooks only the character fragment has, so no other program's text changes.</summary>
    public static string Lighting() => Hook(Hook(NativeShaders.AtmosphereFunctions,
        "vec3 lit = albedo * (diffuse + envDiffuse) + sunSpecular + envSpecular;", "if (surfaceOn) lit += surfaceSun(sun, am.a * 2.0, albedo * fresnelDiffuse);"),
        "specular += radiance * (nl * D * (0.04 * vis + 0.96 * fresnel * vis)) / 3.14159265358979;", "if (surfaceOn) specular += radiance * surfaceSpecular(l);");

    static string Hook(string text, string anchor, string added)
    {
        if (!text.Contains(anchor)) throw new InvalidOperationException($"CharacterSurface: the lighting text no longer holds '{anchor}'");
        return text.Replace(anchor, anchor + "\n" + added);
    }

    /// <summary>The fragment's functions: after the skin's inputs, ahead of <c>main</c>.</summary>
    public const string Functions = """
        vec3 surfaceNormal = vec3(0.0, 1.0, 0.0), surfaceView = vec3(0.0, 0.0, 1.0);
        vec3 strand = vec3(0.0), strandHue = vec3(1.0);   // hair: the strand's direction and the hair's own hue
        float strandGrain = 0.5, strandStrength = 0.0;    // hair: the texture's brightness (it shifts the lobes along the strand) and the highlight's height
        vec3 skinSunBand = vec3(0.0);                     // skin: what the table adds to the sun's N.L (skinDiffuse)
        float eyeShare = 0.0, eyeWiden = 0.0;             // eyes: how much of the fragment is eyeball; the pixel's normal variance on the lobe's alpha squared
        float clothSheen = 0.0;                           // cloth: the sheen's height (PartMaterial.Sheen, never over the metal the colour map marks)
        vec3 clothTint = vec3(1.0);

        // sin(strand, half vector)^power, the strand tilted by shift along the normal (Kajiya-Kay; Scheuermann's shifted lobes).
        float strandLobe(vec3 h, float shift, float power)
        {
            float th = dot(normalize(strand + surfaceNormal * shift), h);
            return pow(max(1.0 - th * th, 0.0), power * 0.5);
        }
        // Hair: a sharp white lobe tilted towards the root, a wide lobe in the hair's own colour tilted towards the tip (Marschner's R and TRT).
        vec3 hairLight(vec3 l)
        {
            float nl = dot(surfaceNormal, l);
            if (strandStrength <= 0.0 || nl <= 0.0) return vec3(0.0);
            vec3 h = normalize(l + surfaceView);
            float grain = (strandGrain - 0.5) * 0.3;
            float primary = strandLobe(h, grain - 0.08, 48.0);
            float secondary = strandLobe(h, grain + 0.08, 12.0);
            return strandStrength * smoothstep(0.0, 0.25, nl) * (primary * mix(vec3(1.0), strandHue, 0.3) + 0.5 * secondary * strandHue);
        }

        // Cloth: the Charlie sheen (Estevez and Kulla) with Neubelt's visibility (capped: it runs away where light and view both graze), at a fixed roughness of 0.6, in the units of the diffuse term: nothing head-on, a soft
        // bright edge at grazing angles, tinted by the cloth's own colour.
        vec3 sheenLight(vec3 l)
        {
            float nl = dot(surfaceNormal, l), nv = dot(surfaceNormal, surfaceView);
            if (clothSheen <= 0.0 || nl <= 0.0 || nv <= 0.0) return vec3(0.0);
            float nh = clamp(dot(surfaceNormal, normalize(l + surfaceView)), 0.0, 1.0);
            float d = (2.0 + 1.0 / 0.6) * pow(1.0 - nh * nh, 0.5 / 0.6) / (2.0 * ATMO_PI);
            float vis = 1.0 / (4.0 * max(nl + nv - nl * nv, 0.25));
            return clothSheen * clothTint * d * vis * nl * ATMO_PI;
        }

        vec3 skinHue()
        {
            vec3 hue = skinAlbedo / max(max(skinAlbedo.r, max(skinAlbedo.g, skinAlbedo.b)), 1e-3);
            return hue * hue;
        }
        // Light through a thin part (ears, fingers), in the units of the diffuse N.L: its share of skin, facing away from the light. A thin part curves tightly and is about
        // two radii thick (the curvature bake), and passes exp(-thickness / 0.25 units); the colour is the skin's own, as skinDiffuse's.
        vec3 skinThrough(vec3 l)
        {
            float back = clamp(-dot(skinGeometric, l), 0.0, 1.0);
            float thin = smoothstep(3.0, 9.0, skinCurvature);
            if (skinShare <= 0.0 || back * thin <= 0.0) return vec3(0.0);
            return skinHue() * skinShare * thin * back * exp(-8.0 / skinCurvature) * 0.8;
        }
        // The sun on skin beyond what kenshiLight gave it, in the units of the diffuse N.L. visible: its shadow here. In a shadow's penumbra the light under the skin bleeds
        // into the dark side, warm. The band past the terminator and the light through a thin part entered elsewhere, so the fragment's own shadow (its own body) must not
        // cut them: their shadow is asked where the light went in, across the part, the chord 2 r cos of a sphere of the curvature's radius. A real caster still shades them.
        vec3 skinSunExtra(vec3 l, float visible)
        {
            vec3 extra = skinHue() * skinShare * clamp(dot(surfaceNormal, l), 0.0, 1.0) * 0.5 * (sqrt(visible) - visible);
            vec3 through = skinThrough(l);
            if (max(skinSunBand.r, max(skinSunBand.g, skinSunBand.b)) + through.r + through.g + through.b < 1e-3) return extra;
            float chord = 2.0 * max(-dot(skinGeometric, l), 0.0) / max(skinCurvature, 0.25);
            float entry = kenshiShadow(vWorld + l * (min(chord, 1.0) + 0.1), l);
            return extra + skinSunBand * max(entry - visible, 0.0) + through * entry;
        }

        // The tear film's catchlight (KenshiNative's eyes2): GGX at gloss 0.95 widened to the pixel (Tokuyoshi and Kaplanyan), F0 0.025, bounded: a lobe this sharp peaks at
        // thousands of times its source, which is a flare.
        vec3 eyeLight(vec3 l)
        {
            float nl = dot(surfaceNormal, l);
            if (eyeShare <= 0.0 || nl <= 0.0) return vec3(0.0);
            vec3 h = normalize(l + surfaceView);
            float nh = clamp(dot(surfaceNormal, h), 0.0, 1.0), lh = clamp(dot(l, h), 0.0, 1.0);
            const float a = 0.0595 * 0.0595;
            float a2 = a * a + eyeWiden;
            float denom = max(nh * nh * (a2 - 1.0) + 1.0, 1e-6);
            float k = sqrt(a2) * 0.5;
            float vis = 1.0 / (lh * lh * (1.0 - k * k) + k * k);
            float fresnel = 0.025 + 0.975 * pow(1.0 - lh, 5.0);
            return vec3(eyeShare * min(nl * a2 / (ATMO_PI * denom * denom) * fresnel * vis / ATMO_PI, 4.0));
        }

        // What a light adds besides the game's lobes, per unit of its light (the sun's after its shadow, a lamp's radiance).
        vec3 surfaceSpecular(vec3 l) { return hairLight(l) + sheenLight(l) + eyeLight(l); }

        vec3 surfaceSun(vec3 sun, float sunScale, vec3 diffuseAlbedo)
        {
            vec3 l = uAtmoLight.xyz;
            vec3 added = sun * surfaceSpecular(l);
            if (skinShare <= 0.0) return added;
            vec3 open = uAtmoSunLight * sunScale * atmoCloudShadow(vWorld);
            float visible = clamp((sun.r + sun.g + sun.b) / max(open.r + open.g + open.b, 1e-5), 0.0, 1.0);
            return added + ATMO_PI * open * diffuseAlbedo * skinSunExtra(l, visible);
        }

        // How much of a body fragment is eyeball (KenshiNative's knEyeWeight): a head texel, bare (share: not painted over, and only where the race's skin is on, so no robot's
        // lens), without the skin tone, with the island's high gloss. The curvature (the ball is a small dome) keeps it off a polished head shell: no list of races.
        float eyeOf(float v, float tone, float headGloss, float curvature, float share)
        {
            if (v >= 0.0) return 0.0;
            return share * (1.0 - smoothstep(0.02, 0.1, tone)) * smoothstep(0.5, 0.7, headGloss) * smoothstep(2.5, 4.5, curvature);
        }

        // The game's wetness in the rain (common/wet.hlsl makeWet, character.hlsl: skin takes a fifth of the absorbance), as the world's objects have it; indoors dry; no water
        // line. The eye is wet already: it keeps its colour and takes the sky's wide lobe, not the island's mirror of the landscape (KenshiNative's eyes2).
        void wetSurface(inout vec3 albedo, inout float gloss, float metal, float skin)
        {
            vec4 wet = vec4(albedo, gloss);
            if (view.fogDistance > 0.0 && vInterior == 0u && uWeatherWet.x > 0.0)
                makeWet(wet, uWeatherWet.x, (1.0 - gloss) * (1.0 - metal) * (1.0 - 0.8 * skin), -1.0e4, 0.5);
            albedo = mix(wet.rgb, albedo, eyeShare);
            gloss = mix(wet.a, 0.4, eyeShare);
        }

        // A hair fragment's inputs. The strand is the mesh's up axis (the vertex stage's vColour here) laid on the surface: down the sides, out from the crown.
        // Beards and fur use the same mode; animals (no Strands flag) keep the game's lobe, their fur does not fall down.
        void hairSurface(inout vec3 n, vec3 v, vec3 albedo, float gloss, bool strands)
        {
            if (dot(n, v) < 0.0) n = -n;   // a card seen from behind, or a mesh with inward normals: the side the camera sees
            if (!strands) return;
            vec3 up = normalize(vColour.xyz);
            vec3 along = up - n * dot(up, n);
            float len = length(along);
            strand = along / max(len, 1e-4);
            strandHue = albedo / max(max(albedo.r, max(albedo.g, albedo.b)), 1e-3);
            strandStrength = 0.4 * clamp(gloss * 2.0, 0.0, 1.0) * smoothstep(0.02, 0.3, len);   // none where the strand has no direction (the crown)
        }

        """;
}
