using Meitou.Game.Rendering.Frame;
namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The <c>chars2</c> switch's shading of the characters (docs/character-renderer.md "Hair, cloth, skin and eyes"): what a light adds to a
/// fragment on top of <c>kenshiLight</c>, the sun's and the lamps' alike. <c>pc.fill.a</c> carries the switch, so one program serves both sides
/// and off runs none of it.
/// </summary>
static class CharacterSurface
{
    /// <summary>
    /// Ahead of the atmosphere text: what its two hooks (<see cref="Lighting"/>) call.
    /// </summary>
    public const string Prototypes = """
        bool surfaceOn = false;
        uint surfaceParts = 0u;   // the Advanced materials' parts that are on (CharacterRenderer.SurfaceParts): 1 hair, 2 cloth, 4 skin, 8 eyes, 16 metal, 32 water beads
        vec3 surfaceSun(vec3 sun, float sunScale, vec3 diffuseAlbedo, float share);
        vec3 surfaceSunTraced(vec3 sun, vec3 diffuseAlbedo);
        vec3 surfaceSpecular(vec3 l);
        vec3 surfaceAmbient(vec3 sky);

        """;

    /// <summary>
    /// The line of the atmosphere text that <see cref="SunHook"/> follows: the sun's and the sky's light summed.
    /// </summary>
    const string SunAnchor = "vec3 lit = albedo * (diffuse + envDiffuse) + sunSpecular + envSpecular;";

    /// <summary>
    /// What the surface adds to the sun's and the sky's light, after <see cref="SunAnchor"/>.
    /// </summary>
    const string SunHook = "if (surfaceOn) { lit += surfaceSun(sun, am.a * 2.0, albedo * fresnelDiffuse, 1.0 - tracedShare) + surfaceAmbient(envDiffuse); if (tracedShare > 0.0) tracedSunOut += surfaceSunTraced(tracedSun, albedo * fresnelDiffuse); }";

    /// <summary>
    /// The line of the atmosphere text that <see cref="LampHook"/> follows: a lamp's specular.
    /// </summary>
    const string LampAnchor = "specular += radiance * (nl * D * (0.04 * vis + 0.96 * fresnel * vis)) / 3.14159265358979;";

    /// <summary>
    /// What the surface adds to a lamp's specular, after <see cref="LampAnchor"/>.
    /// </summary>
    const string LampHook = "if (surfaceOn) specular += radiance * surfaceSpecular(l);";

    /// <summary>
    /// The atmosphere text with the two hooks only the character fragment has, so no other program's text changes.
    /// </summary>
    public static string Lighting()
    {
        string withSun = Hook(NativeShaders.AtmosphereFunctions, SunAnchor, SunHook);
        return Hook(withSun, LampAnchor, LampHook);
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="added"/> on a line of its own after <paramref name="anchor"/>; throws when the anchor is gone.
    /// </summary>
    static string Hook(string text, string anchor, string added)
    {
        if (!text.Contains(anchor)) throw new InvalidOperationException($"CharacterSurface: the lighting text no longer holds '{anchor}'");

        return text.Replace(anchor, anchor + "\n" + added);
    }

    /// <summary>
    /// The fragment's functions: after the skin's inputs, ahead of <c>main</c>.
    /// </summary>
    public const string Functions = """
        vec3 surfaceNormal = vec3(0.0, 1.0, 0.0), surfaceView = vec3(0.0, 0.0, 1.0);
        vec3 strand = vec3(0.0), strandHue = vec3(1.0), strandTint = vec3(0.0);   // hair: the strand's direction, the hair's own hue and its colour
        bool strandFlipped = false;
        float strandGrain = 0.5, strandStrength = 0.0;    // hair: the texture's brightness (it shifts the lobes along the strand) and the highlight's height
        vec3 skinSunBand = vec3(0.0);                     // skin: what the table adds to the sun's N.L (skinDiffuse)
        float eyeShare = 0.0, eyeWiden = 0.0;             // eyes: how much of the fragment is eyeball; the pixel's normal variance on the lobe's alpha squared
        float clothSheen = 0.0;                           // cloth: the sheen's height (PartMaterial.Sheen, never over the metal the colour map marks)
        vec3 clothTint = vec3(1.0);
        float metalShare = 0.0, metalGloss = 0.0;         // metal: how much of the fragment is bare metal, and its gloss
        vec3 metalTint = vec3(0.0);
        vec3 studioAmbient(vec3 n, float open);

        // sin(strand, half vector)^power, the strand tilted by shift along the normal (Kajiya-Kay; Scheuermann's shifted lobes).
        float strandLobe(vec3 h, float shift, float power)
        {
            float th = dot(normalize(strand + surfaceNormal * shift), h);
            return pow(max(1.0 - th * th, 0.0), power * 0.5);
        }
        // Hair: a sharp near-white lobe and a wide one in the hair's own colour (Marschner's R and TRT), shifted along the strand by the texture's brightness only: a tangent's sign
        // changes between the mesh's islands, so a fixed shift would cut the highlight at their edges.
        vec3 hairLight(vec3 l)
        {
            if (strandStrength <= 0.0) return vec3(0.0);
            float nl = dot(surfaceNormal, l);
            vec3 h = normalize(l + surfaceView);
            float grain = (strandGrain - 0.5) * 0.15;
            float front = smoothstep(-0.05, 0.3, nl);
            float r = strandLobe(h, grain, mix(90.0, 40.0, max(strandTint.r, max(strandTint.g, strandTint.b))));
            float trt = strandLobe(h, grain, 12.0);
            vec3 spec = front * strandStrength * (r * mix(vec3(1.0), strandHue, 0.25) * 0.5 + trt * strandTint * 0.35);
            if (strandFlipped) spec *= 0.35;   // lit on the face the camera does not see: the light has passed through the card
            return spec;
        }

        // Cloth: the Charlie sheen (Estevez and Kulla) with Neubelt's visibility (capped: it runs away where light and view both graze), at a fixed roughness of 0.6, in the units of the diffuse:
        // a bright edge at grazing angles, tinted by the cloth's own colour.
        vec3 sheenLight(vec3 l)
        {
            float nl = dot(surfaceNormal, l), nv = dot(surfaceNormal, surfaceView);
            if (clothSheen <= 0.0 || nl <= 0.0 || nv <= 0.0) return vec3(0.0);
            float nh = clamp(dot(surfaceNormal, normalize(l + surfaceView)), 0.0, 1.0);
            float d = (2.0 + 1.0 / 0.6) * pow(1.0 - nh * nh, 0.5 / 0.6) / (2.0 * ATMO_PI);
            float vis = 1.0 / (4.0 * max(nl + nv - nl * nv, 0.25));
            return 2.0 * clothSheen * clothTint * d * vis * nl * ATMO_PI;
        }

        float lobeD(float nh, float a) { float a2 = a * a, d = nh * nh * (a2 - 1.0) + 1.0; return a2 / (ATMO_PI * d * d); }

        // How much of a fragment is bare metal. A worn item says so in its colour map (metal); nothing marks it on a robot's body or a held item, so there it is what shines
        // and has no colour of its own: rust and paint are saturated, and rust is matte.
        float surfaceMetal(float metal, vec3 albedo, inout float gloss, uint shading, bool mechanical, bool worn, bool mapped, float wornGlossMap, float heldGlossMap)
        {
            float top = max(albedo.r, max(albedo.g, albedo.b));
            float saturation = 1.0 - min(albedo.r, min(albedo.g, albedo.b)) / max(top, 1e-3);
            float bare = smoothstep(0.04, 0.16, top) * (1.0 - smoothstep(0.35, 0.7, saturation));
            if (shading == 2u) metal = 0.0;
            else if (mapped) metal = bare * 0.65 * smoothstep(0.02, 0.2, metal);   // the map says where the metal is, the estimate how much
            else if (shading == 1u ? mechanical : !worn)
            {
                // A worn plate: the diffuse's alpha is the gloss the artists painted: cloth is black in it. Wholly opaque means none was painted.
                float painted = wornGlossMap >= 0.0 ? smoothstep(0.08, 0.22, wornGlossMap) * (1.0 - smoothstep(0.97, 1.0, wornGlossMap))
                    : heldGlossMap >= 0.0 ? smoothstep(0.04, 0.14, heldGlossMap) : 1.0;   // a held item's gloss map is painted lower: wood is near 0 in it
                metal = max(metal, bare * 0.65 * painted);
            }
            // Worn plate has no gloss of its own in the game: the painted one says where it is polished and where it is dull.
            float polish = wornGlossMap >= 0.0 && wornGlossMap < 0.97 ? mix(0.25, 0.8, smoothstep(0.1, 0.7, wornGlossMap)) : mix(0.45, 0.9, gloss);
            if (metal > 0.0) gloss = mix(gloss, polish, metal);
            metalShare = metal;
            metalGloss = gloss;
            metalTint = pow(clamp(albedo, 0.0, 1.0), vec3(0.75));
            return metal;
        }
        // The sun on a metal where the game's lighting does not do it (the studio): GGX tinted by the metal's colour.
        vec3 metalLight(vec3 l)
        {
            if (metalShare <= 0.0 || view.fogDistance >= 0.0) return vec3(0.0);
            float nl = dot(surfaceNormal, l);
            if (nl <= 0.0) return vec3(0.0);
            vec3 h = normalize(l + surfaceView);
            float a = max(1.0 - metalGloss, 0.12);
            return metalShare * metalTint * min(nl * lobeD(clamp(dot(surfaceNormal, h), 0.0, 1.0), a * a) * 0.6, 12.0);
        }

        vec3 skinHue()
        {
            vec3 hue = skinAlbedo / max(max(skinAlbedo.r, max(skinAlbedo.g, skinAlbedo.b)), 1e-3);
            return hue * hue;
        }
        // Skin's oil: two lobes (a tight sheen over a broad one, d'Eon's fit of skin), F0 0.028, and the fine hairs' soft bright edge.
        vec3 skinLight(vec3 l)
        {
            float nl = dot(surfaceNormal, l), nv = clamp(dot(surfaceNormal, surfaceView), 0.0, 1.0);
            if ((surfaceParts & 4u) == 0u || skinShare <= 0.0 || nl <= 0.0) return vec3(0.0);
            vec3 h = normalize(l + surfaceView);
            float nh = clamp(dot(surfaceNormal, h), 0.0, 1.0), lh = clamp(dot(l, h), 0.0, 1.0);
            float fresnel = 0.028 + 0.972 * pow(1.0 - lh, 5.0);
            float oil = min(nl * (0.6 * lobeD(nh, 0.22) + 0.4 * lobeD(nh, 0.42)) * fresnel * 0.25 / max(lh * lh, 0.1), 1.5);
            float fuzz = pow(1.0 - nv, 3.0) * nl * 0.35;
            return skinShare * (vec3(oil) + skinAlbedo * (view.fogDistance > 0.0 ? ATMO_PI : 1.0) * fuzz);
        }
        // Light through a thin part (ears, fingers), in the units of the diffuse N.L: its share of skin, facing away from the light. A thin part curves tightly and is about
        // two radii thick (the curvature bake), and passes exp(-thickness / 0.05 units): red light's reach in skin is some 5 mm, so an ear or a finger glows and a nose
        // or a cheek does not (with 0.25 the whole nose and the eye sockets glowed orange); the colour is the skin's own, as skinDiffuse's.
        vec3 skinThrough(vec3 l)
        {
            float back = clamp(-dot(skinGeometric, l), 0.0, 1.0);
            if ((surfaceParts & 4u) == 0u || skinShare <= 0.0 || back <= 0.0 || skinThickness >= 0.35) return vec3(0.0);
            return skinHue() * skinShare * back * exp(-skinThickness / 0.05) * 0.8;
        }
        // The sun on skin beyond what kenshiLight gave it, in the units of the diffuse N.L. visible: its shadow here. In a shadow's penumbra the light under the skin bleeds
        // into the dark side, warm. The band past the terminator and the light through a thin part entered elsewhere, so the fragment's own shadow (its own body) must not
        // cut them: their shadow is asked where the light went in, across the part, the chord 2 r cos of a sphere of the curvature's radius. A real caster still shades them.
        vec3 skinSunExtra(vec3 l, float visible)
        {
            if ((surfaceParts & 4u) == 0u) return vec3(0.0);
            vec3 extra = skinHue() * skinShare * clamp(dot(surfaceNormal, l), 0.0, 1.0) * 0.5 * (sqrt(visible) - visible);
            vec3 through = skinThrough(l);
            if (max(skinSunBand.r, max(skinSunBand.g, skinSunBand.b)) + through.r + through.g + through.b < 1e-3) return extra;
            float chord = min(skinThickness, 2.0 * max(-dot(skinGeometric, l), 0.0) / max(skinCurvature, 0.25));
            float entry = kenshiShadow(vWorld + l * (min(chord, 1.0) + 0.1), l);
            return extra + skinSunBand * max(entry - visible, 0.0) * (1.0 - smoothstep(-0.05, 0.2, dot(skinGeometric, l))) + through * entry;
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
        vec3 surfaceSpecular(vec3 l) { return hairLight(l) + sheenLight(l) + eyeLight(l) + skinLight(l) + metalLight(l); }

        // What the sky adds besides the game's ambient: its sheen along the hair, the edge of skin and cloth, its reflection on the eye.
        vec3 surfaceAmbient(vec3 sky)
        {
            float nv = clamp(dot(surfaceNormal, surfaceView), 0.0, 1.0), edge = 1.0 - nv;
            vec3 added = vec3(0.0);
            if (strandStrength > 0.0) added += strandTint * sky * strandStrength * 0.3 * strandLobe(normalize(vec3(0.0, 1.0, 0.0) + surfaceView), (strandGrain - 0.5) * 0.4, 8.0);
            if (skinShare > 0.0 && (surfaceParts & 4u) != 0u) added += skinAlbedo * sky * skinShare * 0.5 * edge * edge * edge;
            if (clothSheen > 0.0) added += clothSheen * clothTint * sky * 0.8 * edge * edge;
            if (eyeShare > 0.0)
            {
                vec3 r = reflect(-surfaceView, surfaceNormal);
                added += eyeShare * sky * (0.05 + 0.95 * pow(edge, 5.0)) * (1.0 + 3.0 * smoothstep(0.1, 0.8, r.y));
            }
            if (metalShare > 0.0 && view.fogDistance < 0.0)   // the studio has no reflection of its own: the sky in the mirror direction, blurred by the gloss
            {
                vec3 r = reflect(-surfaceView, surfaceNormal);
                const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
                float open = dot(sky, lum) / max(dot(studioAmbient(surfaceNormal, 1.0), lum), 1e-3);   // the occlusion the sky was given
                // The mirror sees a bright sky over a dark ground, with a horizon as sharp as the surface is polished: that contrast is what reads as metal.
                float horizon = 0.5 + 0.5 * smoothstep(-mix(0.7, 0.06, metalGloss), mix(0.7, 0.06, metalGloss), r.y);
                vec3 mirror = mix(vec3(0.16, 0.12, 0.09), vec3(1.15, 1.25, 1.4), horizon * horizon);
                added += metalShare * metalTint * mix(sky, mirror * open, metalGloss) * 1.1;
            }
            return added;
        }

        // sun: the share of the sun the cascades shadow (share of it: none while the rays take it all, surfaceSunTraced has theirs).
        vec3 surfaceSun(vec3 sun, float sunScale, vec3 diffuseAlbedo, float share)
        {
            vec3 l = uAtmoLight.xyz;
            vec3 added = sun * surfaceSpecular(l);
            if (skinShare <= 0.0 || share <= 0.0) return added;
            vec3 open = uAtmoSunLight * sunScale * atmoCloudShadow(vWorld) * share;
            float visible = clamp((sun.r + sun.g + sun.b) / max(open.r + open.g + open.b, 1e-5), 0.0, 1.0);
            return added + ATMO_PI * open * diffuseAlbedo * skinSunExtra(l, visible);
        }
        // The same for the sun written apart, which the rays shadow (the traced sun): the highlights and the light through a thin part. The band past the terminator is in
        // the diffuse term already and the rays start past a body's own turned facets (TracedSunShaders), so nothing is asked at an entry point: a strand of hair's shadow
        // stays dark (the entry point's shadow, a coarse one, lit the band inside it, red).
        vec3 surfaceSunTraced(vec3 sun, vec3 diffuseAlbedo)
        {
            vec3 l = uAtmoLight.xyz;
            return sun * surfaceSpecular(l) + ATMO_PI * sun * diffuseAlbedo * skinThrough(l);
        }

        // How much of a body fragment is eyeball (KenshiNative's knEyeWeight): a head texel, bare (share: not painted over, and only where the race's skin is on, so no robot's
        // lens), without the skin tone, with the island's high gloss. The curvature (the ball is a small dome) keeps it off a polished head shell: no list of races.
        float eyeOf(float v, float tone, float headGloss, float curvature, float share)
        {
            if (v >= 0.0) return 0.0;
            return share * (1.0 - smoothstep(0.02, 0.1, tone)) * smoothstep(0.5, 0.7, headGloss) * smoothstep(2.5, 4.5, curvature);
        }

        // The game's wetness in the rain (common/wet.hlsl makeWet, character.hlsl: skin takes a fifth of the absorbance), as the world's objects have it; indoors dry; and the water's
        // line. The eye is wet already: it keeps its colour and takes the sky's wide lobe, not the island's mirror of the landscape (KenshiNative's eyes2).
        void wetSurface(inout vec3 albedo, inout float gloss, float metal, float skin, float soak)
        {
            vec4 wet = vec4(albedo, gloss);
            if (view.fogDistance > 0.0)
            {
                // By the weather out of doors and by the mark the water left; what is under the water now is darker and does not shine.
                float amount = max(vInterior == 0u ? uWeatherWet.x : 0.0, soak), under = uWeatherWet.w - vWorld.y;
                if (amount > 0.0 || under > -0.5)
                    makeWet(wet, amount, max(1.0 - 2.0 * gloss, 0.0) * (1.0 - metal) * (1.0 - 0.8 * skin), under, 0.5);
            }
            albedo = mix(wet.rgb, albedo, eyeShare);
            gloss = mix(wet.a, 0.4, eyeShare);
        }

        // Drops of water on a wet character (docs/character-renderer.md "Water beads"): where the weather or the water's mark leaves the surface wet, drops stand on it
        // (AtmosphereShaders wetDrops: the world's objects wear the same). They sit in the mesh's own space (vBind), so they stay put on the body as it moves, taken
        // to world units by the mesh's scale (vAxisX's length), so a large or scaled mesh wears drops of the same size. Cloth, which soaks the water up, holds few.
        void wetBeads(inout vec3 n, inout vec3 albedo, inout float gloss, float dryGloss, float metal, float skin, float soak)
        {
            if (view.fogDistance <= 0.0 || uWeatherWet.w > vWorld.y) return;   // not the world's view, or under the water
            float rain = vInterior == 0u ? uWeatherWet.y : 0.0;
            float fromSky = (vInterior == 0u ? max(uWeatherWet.x, rain) : 0.0) * mix(0.3, 1.0, smoothstep(-0.2, 0.6, n.y));   // the rain wets what faces it
            float absorbs = max(1.0 - 2.0 * dryGloss, 0.0) * (1.0 - metal) * (1.0 - 0.8 * skin);
            float hold = max(soak, fromSky) * (1.0 - 0.8 * absorbs) * (1.0 - eyeShare);
            if (hold <= 0.02) return;
            float scale = length(vAxisX);
            vec3 X = vAxisX / max(scale, 1e-6), Y = normalize(vColour.xyz), Z = normalize(cross(X, Y));
            vec3 nb = vec3(dot(n, X), dot(n, Y), dot(n, Z)), sun = uAtmoLight.xyz, bead;
            float lens, cover = wetDrops(vBind * scale, max(length(dFdx(vBind)), length(dFdy(vBind))) * scale, hold, rain, nb, -vec3(X.y, Y.y, Z.y),
                vec3(dot(sun, X), dot(sun, Y), dot(sun, Z)), bead, lens);
            if (cover <= 0.0) return;
            n = normalize(mix(n, X * bead.x + Y * bead.y + Z * bead.z, 0.8 * cover));
            gloss = mix(gloss, 0.9, cover);
            albedo *= 1.0 + cover * (0.25 * smoothstep(0.0, 0.9, -lens) - 0.2 * smoothstep(0.1, 0.9, lens));
        }

        // A hair fragment's inputs. The strand is the mesh's bitangent (the cards' streaks run along v) averaged with its up axis: the tangents alone change from triangle to triangle
        // on some styles, which showed as facets in the highlight. Beards and fur use the same mode; animals (no Strands flag) keep the game's lobe, their fur does not fall down.
        void hairSurface(inout vec3 n, vec3 v, vec3 albedo, float gloss, bool strands)
        {
            // A card seen from behind, or a mesh with inward normals: the side the camera sees, unless the sun is on the other face. The game lights that face, its shadow is asked
            // from it, and turning it towards the camera would put the lock in its own shadow (black tips against the sun).
            vec3 sun = view.fogDistance < 0.0 ? normalize(view.lightDir) : uAtmoLight.xyz;
            strandFlipped = dot(n, v) < 0.0;
            if (strandFlipped && dot(n, sun) <= 0.0) { n = -n; strandFlipped = false; }
            if (!strands) return;
            vec3 alongV = vTangent.xyz; alongV = cross(n, alongV) * vTangent.w;
            vec3 along = alongV - n * dot(alongV, n);
            vec3 down = vColour.xyz - n * dot(vColour.xyz, n);
            if (dot(along, down) < 0.0) along = -along;
            along = normalize(along) + normalize(down) * 0.8;
            float len = length(along);
            strand = along / max(len, 1e-6);
            strandHue = albedo / max(max(albedo.r, max(albedo.g, albedo.b)), 1e-3);
            strandTint = sqrt(clamp(albedo, 0.0, 1.0));
            strandStrength = len > 1e-6 ? mix(0.2, 0.7, clamp(gloss * 2.0, 0.0, 1.0)) : 0.0;
        }

        """;
}
