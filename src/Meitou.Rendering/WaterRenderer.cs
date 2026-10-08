using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Textures;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// Water as the game places it (docs/formats/terrain.md, "Water"): one surface at <see cref="WorldWater.Height"/>
/// over the whole world, hidden wherever the terrain is higher. Shading follows the facts of Kenshi's water material:
/// colour from <c>watercolourmap.png</c>, flow from <c>flowmap.png</c>, three phase-shifted scrolling samples of the
/// normal map, the biomes' water parameters, alpha from the water depth near the camera and opaque beyond 4000
/// units. With a <see cref="ReflectionPass"/> the water reflects the mirrored scene, else the sky colour. Not reproduced: scum, turbulence,
/// rain ripples and per-biome normal maps (all use <c>water.png</c>).
/// </summary>
public sealed unsafe class WaterRenderer : IDisposable
{
    const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 aXZ;
        uniform mat4 uViewProjection;
        uniform float uWaterHeight;
        uniform vec2 uCentre;          // the plane follows the eye, so the sea has no edge
        uniform float uExtent;         // half its side, past the far plane
        out vec3 vWorld;
        void main()
        {
            vWorld = vec3(uCentre.x + aXZ.x * uExtent, uWaterHeight, uCentre.y + aXZ.y * uExtent);
            gl_Position = uViewProjection * vec4(vWorld, 1.0);
        }
        """;

    static readonly string Fragment = "#version 330 core\n" + TerrainShaders.HeightFunctions + SkyRenderer.SkyFunctions + """

        in vec3 vWorld;
        out vec4 fragColour;
        uniform vec3 uEye;
        uniform float uWaterHeight;
        uniform float uHalfWorld;
        uniform float uTime;
        uniform vec3 uSunDir;
        uniform vec3 uSunColour;
        uniform vec3 uFogColour;
        uniform float uFogDistance;
        uniform sampler2D uColourMap;   // watercolourmap.png: the biomes' water colour, whole world
        uniform sampler2D uFlowMap;     // flowmap.png: RG flow direction, B scum amount
        uniform sampler2D uNormalMap;   // water.png
        uniform sampler2D uRainMap;     // rain-ripples.png: R the ring's size, G B its normal, A the drop's phase
        uniform sampler2D uParamsA;     // per pixel, blended over biomes: scale X, scale Y (repeats per unit), invStrength, invOpacity
        uniform sampler2D uParamsB;     // gloss, glow, distortion, -
        uniform vec4 uSeaA;             // the open sea past the world's edge: parameters as in the two maps above, and colour
        uniform vec4 uSeaB;
        uniform vec3 uSeaColour;
        uniform sampler2D uReflection;  // the scene mirrored about the water (ReflectionPass), looked up with uReflectionViewProjection
        uniform mat4 uReflectionViewProjection;
        uniform float uReflect;         // 1 with a reflection this frame, else the sky colour is reflected

        vec3 sampleNormal(vec2 coord, vec2 direction, float speed, float time)
        {
            float t = fract(time);
            vec3 n = texture(uNormalMap, coord + direction * speed * t).rgb * 2.0 - 1.0;
            return n.xzy * (1.0 - abs(t * 2.0 - 1.0));
        }

        // forward/water.hlsl rainRipple: a ring per texel, its phase scrolling with the time, the normal's xz scaled by the ring's profile.
        vec3 rainRipple(vec2 coord, float time, float weight)
        {
            vec4 ripple = texture(uRainMap, coord);
            float dropTime = fract(ripple.a + time * 0.5);
            float dropFrac = dropTime - 1.0 + ripple.r * 0.5 + 0.5;
            float s = -sin(clamp(dropFrac * 16.0, 0.0, 2.0) * 3.14159);
            vec3 rainNormal = ripple.grb;
            rainNormal.xz *= s;
            return rainNormal * weight * ripple.r;
        }

        void main()
        {
            vec2 map = (vWorld.xz + uHalfWorld) / (2.0 * uHalfWorld);
            vec4 pa = texture(uParamsA, map);
            vec4 pb = texture(uParamsB, map);
            vec3 waterColour = texture(uColourMap, map).rgb;
            // Past the world's edge the data's last pixels would stretch outwards: fade to the open sea instead.
            vec2 beyond = abs(vWorld.xz) - uHalfWorld;
            float outside = max(max(beyond.x, beyond.y), 0.0);
            // The data is shallow-coast colours right up to its edge: fade into the open sea across the edge, from inside it, so there is no straight seam.
            float sea = smoothstep(-30000.0, 12000.0, max(beyond.x, beyond.y));
            pa = mix(pa, uSeaA, sea);
            pb = mix(pb, uSeaB, sea);
            waterColour = mix(waterColour, uSeaColour, sea);
            vec3 toEye = uEye - vWorld;
            float dist = length(toEye);
            vec3 view = toEye / dist;

            // Flow: direction from the flow map, speed from the texture scale and the biome's distortion.
            vec2 tex = vWorld.xz * pa.xy;
            vec2 direction = (texture(uFlowMap, map).rg * 2.0 - 1.0) * (pa.xy * 5000.0);
            float speed = length(direction);
            direction /= max(speed, 1e-5);
            float distortion = max(pb.z, 1.0);
            speed /= distortion;
            float time = uTime * distortion;
            vec3 n = vec3(0.0, 1.0, 0.0);
            n += sampleNormal(tex, direction, speed, time);
            n += sampleNormal(tex + vec2(0.1, 0.3), direction, speed, time + 0.33);
            n += sampleNormal(tex + vec2(0.4, 0.7), direction, speed, time + 0.66);
            n.y *= pa.z;
            // Rain ripples (docs/formats/weather.md): three layers weighted by rainAmount, faded out at grazing view and with distance.
            float rain = uWeatherWet.y * clamp(view.y * 2.0 - dist * 0.0001, 0.0, 1.0);
            if (rain > 0.0)
            {
                vec2 rainCoord = vWorld.xz * 0.01;
                n += rainRipple(rainCoord * 6.0, time * 1.5, rain * 2.0);
                n += rainRipple(rainCoord.yx * 6.0, time * 1.5 + 0.5, rain * 2.0);
                n += rainRipple(rainCoord * 2.0, time + 0.3, rain * 0.8);
            }
            n = normalize(n);

            // Lighting: sun specular, sky reflection, a little diffuse for the water colour.
            float gloss = clamp(pb.x, 0.0, 1.0);
            vec3 l = normalize(uSunDir);
            vec3 h = normalize(l + view);
            // The ripples are finer than a pixel far away, so the glint widens and dims with distance instead of staying a hot point.
            float power = max(exp2(gloss * 11.0 + 1.0) / (1.0 + dist / 6000.0), 12.0);
            float spec = pow(max(dot(n, h), 0.0), power) * (power + 8.0) / 25.0 * gloss;
            // Above the game's camera heights (uAtmoAltitude.z, a viewer choice) every water pixel is far away and its widened glint covers
            // a large patch of sea: weigh it by the Fresnel term the game's own sun specular has (F0 0.04), so it stays a soft sheen.
            float lh = clamp(dot(l, h), 0.0, 1.0);
            spec *= mix(1.0, 0.04 + 0.96 * exp2((-5.55473 * lh - 6.98316) * lh), uAtmoAltitude.z);
            float cosv = max(dot(view, n), 0.0);
            float schlick = 0.02 + 0.98 * pow(1.0 - cosv, 5.0);
            vec3 reflected = skyColour(reflect(-view, n), false);
            if (uReflect > 0.5)
            {
                // As the game's water: the normal pushes the lookup sideways by 60 / depth, less the further away.
                vec4 rc = uReflectionViewProjection * vec4(vWorld, 1.0);
                vec2 offset = n.xz * (60.0 / rc.w);
                offset *= min(1.0, 0.04 / max(length(offset), 1e-5));
                vec2 uv = rc.xy / rc.w * 0.5 + 0.5 + offset;
                // Past the picture's border there is nothing to reflect: fade to the sky colour.
                vec2 inside = smoothstep(vec2(0.0), vec2(0.04), uv) * smoothstep(vec2(0.0), vec2(0.04), 1.0 - uv);
                reflected = mix(reflected, min(texture(uReflection, uv).rgb, vec3(3.0)), inside.x * inside.y);
            }
            vec3 diffuse = waterColour * (max(dot(n, l), 0.0) * uSunColour * 0.6 + uSkyZenith * 0.5 + 0.03);
            vec3 colour = mix(diffuse, reflected, schlick * gloss) + min(spec, 4.0) * uSunColour * 0.25 + pb.y * waterColour;

            // Alpha as the game's water: see-through near the camera where shallow, opaque beyond 4000 units.
            float depth = max(0.0, uWaterHeight - terrainHeight(vWorld.xz)) / max(view.y, 0.05);
            if (outside > 0.0) depth = 1e4;   // past the edge of the world there is only open sea
            float fresnel = 1.0 - pow(1.0 - cosv, 2.0);
            float a = clamp(1.0 - (dist - 4000.0) / 1000.0, 0.0, 1.0);
            a *= mix(1.0, clamp(depth * pa.w, 0.0, 1.0), fresnel);
            a *= clamp(depth / 2.0, 0.0, 1.0);
            a = mix(1.0, a, clamp((4000.0 - dist) / 400.0, 0.0, 1.0));

            fragColour = vec4(atmoApply(colour, uEye, vWorld), a);   // aerial perspective (AtmosphereShaders)
        }
        """;

    /// <summary>
    /// The Meitou water's motion, shared by both stages (docs/viewer.md "Water"): the open water's Gerstner waves (four, led by the wind; the CPU
    /// advances their phases on the game clock) and the shore's breakers. A breaker's phase runs along the distance to the shore, estimated as
    /// the water depth over the bottom's slope, so its crests follow the depth contours as real waves turn to do.
    /// </summary>
    const string MeitouMotion = """

        uniform float uWaterHeight;
        uniform float uHalfWorld;
        uniform vec4 uWaveDirX;         // the four waves' unit directions, x components
        uniform vec4 uWaveDirZ;         // and z components
        uniform vec4 uWaveK;            // wave numbers (2 pi / wavelength)
        uniform vec4 uWaveAmp;          // amplitudes (units)
        uniform vec4 uWavePhase;        // phases (radians, advanced on the CPU)
        uniform vec4 uWaveSteep;        // Gerstner steepness per wave
        uniform vec4 uShore;            // x breaker phase (cycles), y run-up height, z breaker wavelength, w breaker height
        uniform vec4 uWaveFade;         // eye distances over which the open waves (x to y) and the shore's breakers and foam (z to w) fade out
        uniform float uWaterDebug;      // 1: show the shore fields (MEITOU_WATER_DEBUG=1)

        vec3 waveDisplace(vec2 p, float scale)
        {
            vec4 theta = uWaveK * (uWaveDirX * p.x + uWaveDirZ * p.y) - uWavePhase;
            vec4 c = cos(theta);
            vec4 qa = uWaveSteep * uWaveAmp * scale;
            return vec3(dot(qa * uWaveDirX, c), dot(uWaveAmp * scale, sin(theta)), dot(qa * uWaveDirZ, c));
        }

        // xyz: the waves' normal at p; w: the surface's compression (below about 0.7 the crests are sharp enough to foam).
        vec4 waveNormal(vec2 p, float scale)
        {
            vec4 theta = uWaveK * (uWaveDirX * p.x + uWaveDirZ * p.y) - uWavePhase;
            vec4 c = cos(theta);
            vec4 ka = uWaveK * uWaveAmp * scale;
            float ny = 1.0 - dot(uWaveSteep * ka, sin(theta));
            return vec4(normalize(vec3(-dot(uWaveDirX * ka, c), ny, -dot(uWaveDirZ * ka, c))), ny);
        }

        struct Shore { float depth; float dist; float g; vec2 dir; };

        // depth: still water over the ground (negative on land); dist: horizontal distance to the waterline (depth / slope, negative inland);
        // g: the breaker phase in [0, 1), the crest at 0.5 with its steep front towards the shore; dir: the unit direction towards the shore (up the bottom).
        Shore shoreAt(vec2 p)
        {
            Shore r;
            float e = max(terrainSpacing(p), 4.0) * 1.5;
            vec2 grad = vec2(terrainHeight(p + vec2(e, 0.0)) - terrainHeight(p - vec2(e, 0.0)),
                             terrainHeight(p + vec2(0.0, e)) - terrainHeight(p - vec2(0.0, e))) / (2.0 * e);
            r.depth = uWaterHeight - terrainHeight(p);
            float slope = length(grad);
            r.dist = r.depth / max(slope, 0.015);
            r.dir = slope > 1e-5 ? grad / slope : vec2(0.0);
            // Along the shore the waves arrive at different times.
            float along = sin(p.x * 0.0021 + sin(p.y * 0.0013) * 2.0) + sin(p.y * 0.0017 + p.x * 0.0009);
            r.g = fract(r.dist / uShore.z + uShore.x + along * 0.6);
            return r;
        }

        // A breaker's height over its phase: a steep front (g below 0.5, the shore side) and a long back.
        float breakerProfile(float g) { return g < 0.5 ? smoothstep(0.32, 0.5, g) : 1.0 - smoothstep(0.5, 0.97, g); }
        """;

    const string MeitouVertex = "#version 330 core\n" + TerrainShaders.HeightFunctions + MeitouMotion + """

        layout(location = 0) in vec2 aXZ;   // the polar grid around the eye; the outer ring is marked by a length over 1e5
        uniform mat4 uViewProjection;
        uniform vec2 uCentre;
        uniform float uExtent;
        uniform vec3 uEye;
        out vec3 vWorld;
        out vec2 vBase;                     // the undisplaced position: the fragment's waves and shore are evaluated there
        void main()
        {
            float r = length(aXZ);
            vec2 p = uCentre + (r > 1.0e5 ? aXZ / r * uExtent : aXZ);
            vec3 w = vec3(p.x, uWaterHeight, p.y);
            float dist = distance(w, uEye);
            bool inside = max(abs(p.x), abs(p.y)) < uHalfWorld;
            if (dist < uWaveFade.w && inside)
            {
                float fade = 1.0 - smoothstep(uWaveFade.x, uWaveFade.y, dist), shoreFade = 1.0 - smoothstep(uWaveFade.z, uWaveFade.w, dist);
                Shore s = shoreAt(p);
                // The breaker's hump where the water shoals, and a flat top over the run-up band (the fragment cuts the swash's edge there).
                float hump = uShore.w * breakerProfile(s.g) * smoothstep(420.0, 200.0, s.dist) * smoothstep(-5.0, 25.0, s.dist)
                    * smoothstep(250.0, 120.0, s.depth);
                float lift = uShore.y * (1.0 - smoothstep(10.0, 60.0, s.dist));
                vec3 d = waveDisplace(p, smoothstep(0.5, 20.0, s.depth) * fade);
                w += vec3(d.x, d.y + max(hump, lift) * shoreFade, d.z);
            }
            vBase = p;
            vWorld = w;
            gl_Position = uViewProjection * vec4(w, 1.0);
        }
        """;

    static readonly string MeitouFragment = "#version 330 core\n" + TerrainShaders.HeightFunctions + SkyRenderer.SkyFunctions + MeitouMotion + """

        in vec3 vWorld;
        in vec2 vBase;
        out vec4 fragColour;
        uniform vec3 uEye;
        uniform float uTime;
        uniform vec3 uSunDir;
        uniform vec3 uSunColour;
        uniform sampler2D uColourMap;
        uniform sampler2D uFlowMap;
        uniform sampler2D uFoamMap;     // WaterFoam: a lace of bubble rims in R
        uniform sampler2D uNormalMap;
        uniform sampler2D uRainMap;
        uniform sampler2D uParamsA;
        uniform sampler2D uParamsB;
        uniform vec4 uSeaA;
        uniform vec4 uSeaB;
        uniform vec3 uSeaColour;
        uniform sampler2D uReflection;
        uniform mat4 uReflectionViewProjection;
        uniform float uReflect;

        vec3 sampleNormal(vec2 coord, vec2 direction, float speed, float time)
        {
            float t = fract(time);
            vec3 n = texture(uNormalMap, coord + direction * speed * t).rgb * 2.0 - 1.0;
            return n.xzy * (1.0 - abs(t * 2.0 - 1.0));
        }

        vec3 rainRipple(vec2 coord, float time, float weight)
        {
            vec4 ripple = texture(uRainMap, coord);
            float dropTime = fract(ripple.a + time * 0.5);
            float dropFrac = dropTime - 1.0 + ripple.r * 0.5 + 0.5;
            float s = -sin(clamp(dropFrac * 16.0, 0.0, 2.0) * 3.14159);
            vec3 rainNormal = ripple.grb;
            rainNormal.xz *= s;
            return rainNormal * weight * ripple.r;
        }

        void main()
        {
            vec2 p = vBase;
            vec2 map = (p + uHalfWorld) / (2.0 * uHalfWorld);
            vec4 pa = texture(uParamsA, map);
            vec4 pb = texture(uParamsB, map);
            vec3 waterColour = texture(uColourMap, map).rgb;
            vec2 beyond = abs(p) - uHalfWorld;
            float outside = max(max(beyond.x, beyond.y), 0.0);
            float sea = smoothstep(-30000.0, 12000.0, max(beyond.x, beyond.y));
            pa = mix(pa, uSeaA, sea);
            pb = mix(pb, uSeaB, sea);
            waterColour = mix(waterColour, uSeaColour, sea);
            vec3 toEye = uEye - vWorld;
            float dist = length(toEye);
            vec3 view = toEye / dist;

            // The motion fades with the eye distance; beyond it (and past the world's edge) this is the game's flat water.
            float fade = outside <= 0.0 ? 1.0 - smoothstep(uWaveFade.z, uWaveFade.w, dist) : 0.0;
            float waveFade = outside <= 0.0 ? 1.0 - smoothstep(uWaveFade.x, uWaveFade.y, dist) : 0.0;
            Shore s;
            s.depth = 1.0e4; s.dist = 1.0e6; s.g = 0.0; s.dir = vec2(0.0);
            if (fade > 0.0) s = shoreAt(p);
            else if (outside <= 0.0) s.depth = uWaterHeight - terrainHeight(p);

            // The swash: a breaker that reached the shore runs up the beach (to where the ground is `level` above the still water) and back,
            // leaving the sand wet behind it; above both the water is not there.
            float runup = uShore.y * fade;
            float above = -s.depth;
            float sinceCrest = fract(s.g - 0.5);
            float level = runup * breakerProfile(s.g);
            float wetLevel = runup * (1.0 - 0.8 * sinceCrest);
            if (above > 0.0 && above >= max(level, wetLevel)) discard;
            bool swash = above > 0.0;
            float sheet = swash ? level - above : 0.0;   // the swash's thickness (negative: wet sand only)

            // The normal: the game's scrolled normal map along the flow, the rain's ripples, then the open waves' slopes added.
            vec2 tex = p * pa.xy;
            vec2 direction = (texture(uFlowMap, map).rg * 2.0 - 1.0) * (pa.xy * 5000.0);
            float speed = length(direction);
            direction /= max(speed, 1e-5);
            float distortion = max(pb.z, 1.0);
            speed /= distortion;
            float time = uTime * distortion;
            vec3 nm = vec3(0.0, 1.0, 0.0);
            nm += sampleNormal(tex, direction, speed, time);
            nm += sampleNormal(tex + vec2(0.1, 0.3), direction, speed, time + 0.33);
            nm += sampleNormal(tex + vec2(0.4, 0.7), direction, speed, time + 0.66);
            nm.y *= pa.z;
            float rain = uWeatherWet.y * clamp(view.y * 2.0 - dist * 0.0001, 0.0, 1.0);
            if (rain > 0.0)
            {
                vec2 rainCoord = p * 0.01;
                nm += rainRipple(rainCoord * 6.0, time * 1.5, rain * 2.0);
                nm += rainRipple(rainCoord.yx * 6.0, time * 1.5 + 0.5, rain * 2.0);
                nm += rainRipple(rainCoord * 2.0, time + 0.3, rain * 0.8);
            }
            nm = normalize(nm);
            float open = smoothstep(0.5, 20.0, s.depth) * waveFade;
            vec4 wn = waveNormal(p, open);
            vec2 slope = nm.xz / max(nm.y, 0.05) + wn.xz / max(wn.y, 0.05);
            // The breaker's hump tilts the surface: its height falls towards the shore on the front and rises on the back.
            float surf = smoothstep(420.0, 200.0, s.dist) * smoothstep(-5.0, 25.0, s.dist) * smoothstep(250.0, 120.0, s.depth) * fade;
            float dProfile = (breakerProfile(fract(s.g + 0.01)) - breakerProfile(fract(s.g - 0.01))) / 0.02;
            slope += s.dir * (uShore.w * surf * dProfile / uShore.z);
            vec3 n = normalize(vec3(slope.x, 1.0, slope.y));

            float gloss = clamp(pb.x, 0.0, 1.0);
            vec3 l = normalize(uSunDir);
            vec3 h = normalize(l + view);
            float power = max(exp2(gloss * 11.0 + 1.0) / (1.0 + dist / 6000.0), 12.0);
            float spec = pow(max(dot(n, h), 0.0), power) * (power + 8.0) / 25.0 * gloss;
            float lh = clamp(dot(l, h), 0.0, 1.0);
            spec *= mix(1.0, 0.04 + 0.96 * exp2((-5.55473 * lh - 6.98316) * lh), uAtmoAltitude.z);
            float cosv = max(dot(view, n), 0.0);
            float schlick = 0.02 + 0.98 * pow(1.0 - cosv, 5.0);
            vec3 reflected = skyColour(reflect(-view, n), false);
            if (uReflect > 0.5)
            {
                vec4 rc = uReflectionViewProjection * vec4(vWorld, 1.0);
                vec2 offset = n.xz * (60.0 / rc.w);
                offset *= min(1.0, 0.04 / max(length(offset), 1e-5));
                vec2 uv = rc.xy / rc.w * 0.5 + 0.5 + offset;
                vec2 inside = smoothstep(vec2(0.0), vec2(0.04), uv) * smoothstep(vec2(0.0), vec2(0.04), 1.0 - uv);
                reflected = mix(reflected, min(texture(uReflection, uv).rgb, vec3(3.0)), inside.x * inside.y);
            }
            vec3 light = max(dot(n, l), 0.0) * uSunColour * 0.6 + uSkyZenith * 0.5 + 0.03;
            vec3 colour = mix(waterColour * light, reflected, schlick * gloss) + min(spec, 4.0) * uSunColour * 0.25 + pb.y * waterColour;

            // Foam (WaterFoam's lace, let through the more the more foam there is): sharp open-water crests in a wind; the breaking crest as a line
            // and the foam it leaves behind; a thin edge at the waterline; the swash's front and the lace in its sheet.
            float breakZone = smoothstep(280.0, 150.0, s.dist) * smoothstep(-2.0, 10.0, s.dist) * smoothstep(250.0, 120.0, s.depth) * fade * min(uShore.w, 1.0);
            float crestLine = smoothstep(0.4, 0.5, s.g) * (1.0 - smoothstep(0.5, 0.58, s.g));
            float trail = smoothstep(0.5, 0.55, s.g) * (1.0 - smoothstep(0.55, 0.97, s.g));
            float amount = clamp((0.72 - wn.w) * 2.5, 0.0, 1.0);
            // Along the shore the breakers break harder in some stretches than others, and not at all in a few.
            float stretch = smoothstep(0.2, 0.75, texture(uFoamMap, p * 0.0011).g);
            amount = max(amount, max(crestLine * 0.8, trail * (1.0 - smoothstep(0.55, 0.95, s.g)) * 0.7) * breakZone * (0.35 + 0.85 * stretch));
            amount = max(amount, (1.0 - smoothstep(0.0, 4.0, abs(s.dist))) * 0.4 * fade);
            if (swash && sheet > 0.0) amount = max(amount, max(0.85 * (1.0 - smoothstep(0.0, runup * 0.12, sheet)), 0.35 * (1.0 - sinceCrest)));
            vec2 drift = direction * speed * uTime * 0.2;
            // Blotches (the multi-octave noise at two scales) with bubble rims in them; the more foam, the lower the threshold.
            vec2 a1 = texture(uFoamMap, p * 0.009 + drift).rg, a2 = texture(uFoamMap, p * 0.027 - drift * 1.3).rg;
            float pattern = (a1.g * 0.6 + a2.g * 0.4) * 0.7 + max(a1.r, a2.r) * 0.45;
            float foam = smoothstep(0.95 - amount, 1.25 - amount, pattern);
            vec3 foamColour = vec3(0.72) * (max(dot(n, l), 0.0) * uSunColour * 0.9 + uSkyZenith * 0.8 + 0.04);
            foam *= 0.92;
            colour = mix(colour, foamColour, foam);

            // Alpha as the game's water from its depth (the swash: its own thin sheet), then the foam over it.
            float depth = (swash ? max(sheet, 0.0) : max(0.0, vWorld.y - terrainHeight(p))) / max(view.y, 0.05);
            if (outside > 0.0) depth = 1e4;
            float fresnel = 1.0 - pow(1.0 - cosv, 2.0);
            float a = clamp(1.0 - (dist - 4000.0) / 1000.0, 0.0, 1.0);
            a *= mix(1.0, clamp(depth * pa.w, 0.0, 1.0), fresnel);
            a *= clamp(depth / 2.0, 0.0, 1.0);
            if (swash) a = max(a, sheet > 0.0 ? 0.3 : 0.0);
            a = max(a, foam);
            if (swash && sheet <= 0.0)
            {
                // Wet sand: darker and glossy, drying out until the next wave.
                float wet = (1.0 - sinceCrest) * 0.6;
                colour = reflected * schlick * gloss + min(spec, 4.0) * uSunColour * 0.15;
                a = wet * 0.45;
            }
            a = mix(1.0, a, clamp((4000.0 - dist) / 400.0, 0.0, 1.0));

            if (uWaterDebug > 0.5) { fragColour = vec4(clamp(s.dist / 400.0, 0.0, 1.0), s.g, clamp(s.depth / 40.0, 0.0, 1.0), 1.0); return; }   // MEITOU_WATER_DEBUG=1
            fragColour = vec4(atmoApply(colour, uEye, vWorld), a);
        }
        """;

    /// <summary>The native GPU API (phase 8 stage 2: the water makes no GL call; docs/renderer-native.md 8.6).</summary>
    public GpuContext Gpu { get; }
    readonly DeviceBuffer quad, grid, gridIndices;
    readonly VertexArrayBindings quadSource, gridSource;   // the vertex inputs (the pipeline cache's key)
    readonly BufferBinding[] quadVertices, gridVertices;
    readonly uint gridIndexCount;
    readonly SampledImage[] maps;   // colour, flow, normal, parameters A and B, rain ripples, foam (Meitou), in the order of WaterProgram.MapNames
    readonly Vector4 seaA, seaB;
    readonly Vector3 seaColour;
    readonly WaterProgram faithful, meitou;
    readonly WaveSet waves = new();

    /// <summary>The Enhancements switch <c>water</c>: the game's flat water (Faithful) or Meitou's waves, breakers and foam (the default).</summary>
    public bool Meitou { get; set; } = true;

    /// <summary>The Meitou polar grid: segments round the eye (128 low, 256 default, 512 high), rings follow from it.</summary>
    public const int DefaultGridSegments = 256;

    /// <summary>One water program with its handles resolved at load (the Faithful one has no motion uniforms: those handles are inactive).</summary>
    sealed class WaterProgram : IDisposable
    {
        public static readonly string[] MapNames = ["uColourMap", "uFlowMap", "uNormalMap", "uParamsA", "uParamsB", "uRainMap", "uFoamMap"];
        public readonly LegacyProgram P;
        public readonly NativeSegment Segment;
        public readonly UniformHandle ViewProjection, WaterHeight, Centre, Extent, Eye, HalfWorld, SeaA, SeaB, SeaColour, Time, SunDir, SunColour,
            FogColour, FogDistance, Reflect, ReflectionViewProjection, WaveDirX, WaveDirZ, WaveK, WaveAmp, WavePhase, WaveSteep, Shore, WaveFade, Debug;
        public readonly SkyColourHandles Sky;
        public readonly SamplerSlot[] Maps;
        public readonly SamplerSlot Reflection;

        public WaterProgram(GpuContext gpu, string vertex, string fragment, string name, Silk.NET.Vulkan.PrimitiveTopology topology)
        {
            P = LegacyProgram.Create(gpu, vertex, fragment, name);
            Segment = new NativeSegment(gpu, P, topology, name);
            (ViewProjection, WaterHeight, Centre, Extent, Eye, HalfWorld) = (P.Uniform("uViewProjection"), P.Uniform("uWaterHeight"), P.Uniform("uCentre"),
                P.Uniform("uExtent"), P.Uniform("uEye"), P.Uniform("uHalfWorld"));
            (SeaA, SeaB, SeaColour, Time, SunDir, SunColour) = (P.Uniform("uSeaA"), P.Uniform("uSeaB"), P.Uniform("uSeaColour"), P.Uniform("uTime"),
                P.Uniform("uSunDir"), P.Uniform("uSunColour"));
            (FogColour, FogDistance, Reflect, ReflectionViewProjection) = (P.Uniform("uFogColour"), P.Uniform("uFogDistance"), P.Uniform("uReflect"),
                P.Uniform("uReflectionViewProjection"));
            (WaveDirX, WaveDirZ, WaveK, WaveAmp, WavePhase, WaveSteep, Shore, WaveFade) = (P.Uniform("uWaveDirX"), P.Uniform("uWaveDirZ"), P.Uniform("uWaveK"),
                P.Uniform("uWaveAmp"), P.Uniform("uWavePhase"), P.Uniform("uWaveSteep"), P.Uniform("uShore"), P.Uniform("uWaveFade"));
            Debug = P.Uniform("uWaterDebug");
            Sky = SkyColourHandles.Resolve(P);
            Maps = MapNames.Select(P.Sampler).ToArray();
            Reflection = P.Sampler("uReflection");
        }

        public void Dispose() => P.Dispose();
    }

    WaterRenderer(GpuContext gpu, SampledImage[] maps, Vector4 seaA, Vector4 seaB, Vector3 seaColour, int gridSegments)
    {
        (this.seaA, this.seaB, this.seaColour) = (seaA, seaB, seaColour);
        Gpu = gpu;
        this.maps = maps;
        faithful = new WaterProgram(gpu, Vertex, Fragment, "water", Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip);
        meitou = new WaterProgram(gpu, MeitouVertex, MeitouFragment, "water meitou", Silk.NET.Vulkan.PrimitiveTopology.TriangleList);
        float[] corners = [-1, -1, -1, 1, 1, -1, 1, 1]; // triangle strip, counter-clockwise from above
        quad = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)corners.Length, BufferUse.Vertex, "water quad");
        using (var batch = gpu.Uploads.Begin()) batch.Write(quad, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(corners.AsSpan()));
        LegacyProgram.Attribute?[] attributes = [QuadAttribute(quad)];
        quadSource = new VertexArrayBindings(attributes, default);
        quadVertices = faithful.P.VertexBuffers(attributes, 0, 1);

        var (positions, indices) = PolarGrid(gridSegments);
        grid = DeviceBuffer.Create(gpu, sizeof(float) * (ulong)positions.Length, BufferUse.Vertex, "water grid");
        gridIndices = DeviceBuffer.Create(gpu, sizeof(uint) * (ulong)indices.Length, BufferUse.Index, "water grid indices");
        using (var batch = gpu.Uploads.Begin())
        {
            batch.Write(grid, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(positions.AsSpan()));
            batch.Write(gridIndices, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()));
        }
        LegacyProgram.Attribute?[] gridAttributes = [QuadAttribute(grid)];
        gridSource = new VertexArrayBindings(gridAttributes, new BufferBinding(gridIndices.Handle, 0, gridIndices.Size));
        gridVertices = meitou.P.VertexBuffers(gridAttributes, 0, 1);
        gridIndexCount = (uint)indices.Length;
    }

    /// <summary>The quad's only vertex input, location 0: two floats, 8 bytes apart (the GL vertex array's
    /// <c>VertexAttribPointer(0, 2, FLOAT, false, 8, 0)</c>); no element buffer.</summary>
    internal static LegacyProgram.Attribute QuadAttribute(DeviceBuffer quad) =>
        new(new BufferBinding(quad.Handle, 0), GlConventions.VertexFormat(GLEnum.Float, 2, false, false), 8, false);

    /// <summary>The Meitou water's mesh: a centre vertex and rings round it whose spacing grows with the radius (as fine across as along, from
    /// 1 unit out to <see cref="GridDetailRadius"/>), then one ring marked to reach the plane's extent. Offsets from the eye, triangle list,
    /// counter-clockwise from above.</summary>
    internal static (float[] Positions, uint[] Indices) PolarGrid(int segments)
    {
        double q = 1 + 2 * Math.PI / segments;
        var radii = new List<double>();
        for (double r = 1; r < GridDetailRadius; r *= q) radii.Add(r);
        radii.Add(GridDetailRadius);
        radii.Add(1e6);   // the outer ring: scaled to the extent in the vertex shader
        var positions = new float[(1 + radii.Count * segments) * 2];
        for (int ring = 0; ring < radii.Count; ring++)
            for (int s = 0; s < segments; s++)
            {
                double a = 2 * Math.PI * s / segments;
                int i = 1 + ring * segments + s;
                positions[i * 2] = (float)(Math.Cos(a) * radii[ring]);
                positions[i * 2 + 1] = (float)(Math.Sin(a) * radii[ring]);
            }
        var indices = new List<uint>(segments * 3 + (radii.Count - 1) * segments * 6);
        uint V(int ring, int s) => (uint)(1 + ring * segments + (s % segments));
        for (int s = 0; s < segments; s++) indices.AddRange([0u, V(0, s + 1), V(0, s)]);
        for (int ring = 0; ring + 1 < radii.Count; ring++)
            for (int s = 0; s < segments; s++)
                indices.AddRange([V(ring, s), V(ring, s + 1), V(ring + 1, s + 1), V(ring, s), V(ring + 1, s + 1), V(ring + 1, s)]);
        return (positions, indices.ToArray());
    }

    /// <summary>Out to this radius the Meitou grid is fine enough for the waves; beyond it the water is flat.</summary>
    public const float GridDetailRadius = 6000;

    /// <param name="sky">Unused since phase 8 stage 2: the atmosphere comes through the frame globals.</param>
    public static WaterRenderer Create(GpuContext gpu, GameInstall install, GameDatabase db, AssetLocator assets, SkyRenderer? sky, List<string> messages,
        int gridSegments = DefaultGridSegments)
    {
        _ = sky;
        var colour = Load(install, WorldWater.ColourMap);
        var flow = Load(install, WorldWater.FlowMap);
        var normalPath = assets.Find("water.png");
        var normal = normalPath is not null ? TextureLoader.LoadFile(normalPath, allMips: false).Levels[0] : null;
        var rainPath = assets.Find("rain-ripples.png");
        var rain = rainPath is not null ? TextureLoader.LoadFile(rainPath, allMips: false).Levels[0] : null;
        if (normal is null) messages.Add("water.png not found: flat water");
        if (rain is null) messages.Add("rain-ripples.png not found: no rain ripples");

        // Biome water parameters blended per blend-map pixel.
        var info = BlendInfoFile.Open(install);
        var water = BiomeWater.ByIndex(db);
        var blend = TextureLoader.LoadImage(File.ReadAllBytes(Path.Combine(install.DataDirectory, TerrainMaps.BlendMap)));
        var fallback = BiomeWater.FromRecord(db.OfType(global::Meitou.Data.Fcs.FcsRecordType.BIOMES).First());
        Vector4 A(BiomeWater w) => new(w.Scale.X, w.Scale.Y, w.InvStrength, w.InvOpacity);
        Vector4 B(BiomeWater w) => new(w.Gloss, w.Glow, w.Distortion.X, 0);
        var a = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? A(w) : null, A(fallback));
        var b = BiomeField.Bake(info, blend.Width, blend.Height, blend.Pixels, c => water.TryGetValue(c, out var w) ? B(w) : null, B(fallback));

        var sea = OpenSea(a, b, blend.Width, blend.Height, colour);
        Console.WriteLine($"sea       open-sea water: scale {sea.A.X * 5000:0.#}, {sea.A.Y * 5000:0.#}, gloss {sea.B.X:0.##}, colour {sea.Colour.X:0.##} {sea.Colour.Y:0.##} {sea.Colour.Z:0.##}");
        // As the GL textures were made (WorldGl.Texture2D): a found map has mips only where it repeats; the 1 × 1 stand-in for a missing one
        // always had its (single-level) chain and a trilinear filter.
        SampledImage Rgba(RgbaImage? img, bool repeat, byte[] flat, string name) =>
            img is null ? SampledImage.Rgba8(gpu, 1, 1, flat, repeat, mipmaps: true, name) : SampledImage.Rgba8(gpu, img, repeat, mipmaps: repeat, name);
        return new WaterRenderer(gpu,
            [Rgba(colour, false, [0, 32, 64, 255], "water colour map"), Rgba(flow, false, [128, 128, 0, 255], "water flow map"),
             Rgba(normal, true, [128, 255, 128, 255], "water normal map"),
             SampledImage.Rgba32F(gpu, a, blend.Width, blend.Height, "water parameters a"), SampledImage.Rgba32F(gpu, b, blend.Width, blend.Height, "water parameters b"),
             Rgba(rain, true, [0, 0, 0, 0], "water rain ripples"),
             SampledImage.Rgba8(gpu, WaterFoam.Size, WaterFoam.Size, WaterFoam.Bake(), repeat: true, mipmaps: true, "water foam")],
            sea.A, sea.B, sea.Colour, gridSegments);
    }

    /// <summary>
    /// The water of the open sea: the most common parameters among the pixels of the maps' outer ring (the world's edge is
    /// sea nearly all round), with the watercolourmap colour at one such pixel. The water plane uses it beyond the world.
    /// </summary>
    static (Vector4 A, Vector4 B, Vector3 Colour) OpenSea(Vector4[] a, Vector4[] b, int width, int height, RgbaImage? colour)
    {
        const int ring = 8;
        var counts = new Dictionary<(Vector4, Vector4), (int Count, int Pixel)>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (x >= ring && x < width - ring && y >= ring && y < height - ring) { x = width - ring - 1; continue; }
                int i = y * width + x;
                var key = (a[i], b[i]);
                counts[key] = counts.TryGetValue(key, out var c) ? (c.Count + 1, c.Pixel) : (1, i);
            }
        var best = counts.MaxBy(p => p.Value.Count);
        var rgb = new Vector3(0, 32, 64) / 255;
        if (colour is not null)
        {
            int px = best.Value.Pixel % width * colour.Width / width, py = best.Value.Pixel / width * colour.Height / height;
            int o = (py * colour.Width + px) * 4;
            rgb = new Vector3(colour.Pixels[o], colour.Pixels[o + 1], colour.Pixels[o + 2]) / 255;
        }
        return (best.Key.Item1, best.Key.Item2, rgb);
    }

    static RgbaImage? Load(GameInstall install, string relative)
    {
        var path = Path.Combine(install.DataDirectory, relative);
        return File.Exists(path) ? TextureLoader.LoadImage(File.ReadAllBytes(path)) : null;
    }

    /// <summary>
    /// Steps the Meitou water's motion once per frame: <paramref name="gameHours"/> is the game clock (hours since the load; it stands still while
    /// paused), the wind is the weather's at the camera. Each wave's phase is integrated, so a change of wind changes the waves smoothly.
    /// </summary>
    public void Animate(double gameHours, Vector2 windDirection, float windSpeed) => waves.Step(gameHours, windDirection, windSpeed);

    /// <summary>
    /// The water surface: one draw, blended, into the pass VkGl has open. Prepare sets the uniforms and the textures the shader reads (the height
    /// textures, the atmosphere and the heights' uniforms come through the frame globals); Record is one native segment with the pass's state
    /// and the water's own: no culling, depth test without write, alpha blending. No GL state is changed (phase 8 stage 2): the GL code set
    /// these and put back the depth write and blending after the draw; what it left differently (the cull face off, the blend factors) is read by
    /// no later draw (docs/renderer-native.md 8.6). Faithful draws the game's quad; Meitou the polar grid with <see cref="Animate"/>'s motion.
    /// </summary>
    /// <param name="time">Animation time; the game's unit for it is Unknown (the viewer uses real hours). Meitou uses the game clock instead.</param>
    public void Draw(Matrix4x4 viewProjection, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent, ReflectionPass? reflection = null)
    {
        var p = Meitou ? meitou : faithful;
        Prepare(p, viewProjection, eye, light, colours, Meitou ? waves.NormalTime : time, extent, reflection);
        if (Meitou) Record(p, gridSource, gridVertices, gridIndexCount);
        else Record(p, quadSource, quadVertices, 0);
    }

    void Prepare(WaterProgram h, Matrix4x4 viewProjection, Vector3 eye, WorldLighting light, SkyColours colours, float time, float extent, ReflectionPass? reflection)
    {
        var p = h.P;
        p.Set(h.ViewProjection, in viewProjection);
        p.Set(h.WaterHeight, WorldWater.Height);
        p.Set(h.Centre, eye.X, eye.Z);
        p.Set(h.Extent, extent);
        p.Set(h.Eye, eye.X, eye.Y, eye.Z);
        p.Set(h.HalfWorld, (float)WorldLayout.HalfWorldSize);
        p.Set(h.SeaA, seaA.X, seaA.Y, seaA.Z, seaA.W);
        p.Set(h.SeaB, seaB.X, seaB.Y, seaB.Z, seaB.W);
        p.Set(h.SeaColour, seaColour.X, seaColour.Y, seaColour.Z);
        p.Set(h.Time, time);
        p.Set(h.SunDir, light.SunDirection.X, light.SunDirection.Y, light.SunDirection.Z);
        p.Set(h.SunColour, light.SunColour.X, light.SunColour.Y, light.SunColour.Z);
        p.Set(h.FogColour, light.FogColour.X, light.FogColour.Y, light.FogColour.Z);
        p.Set(h.FogDistance, light.FogDistance);
        if (h == meitou)
        {
            p.Set(h.WaveDirX, waves.DirX);
            p.Set(h.WaveDirZ, waves.DirZ);
            p.Set(h.WaveK, waves.K);
            p.Set(h.WaveAmp, waves.Amplitude);
            p.Set(h.WavePhase, waves.Phase);
            p.Set(h.WaveSteep, waves.Steepness);
            p.Set(h.Shore, waves.Shore);
            p.Set(h.WaveFade, WaveFadeStart, WaveFadeEnd, ShoreFadeStart, ShoreFadeEnd);
            p.Set(h.Debug, DebugView ? 1f : 0f);
        }
        h.Sky.Set(p, colours);
        p.ApplyGlobals();   // the atmosphere's and the heights' uniforms and textures, through the frame globals
        for (int i = 0; i < maps.Length; i++) p.Bind(h.Maps[i], maps[i].Sampled());
        bool reflect = reflection is { Valid: true };
        p.Set(h.Reflect, reflect ? 1f : 0f);
        if (reflect)
        {
            p.Bind(h.Reflection, reflection!.Sampled);
            p.Set(h.ReflectionViewProjection, reflection.ViewProjection);
        }
    }

    /// <summary>The eye distances over which the Meitou open waves fade into the flat water (the grid coarsens with the distance), and over which
    /// the shore's breakers and foam fade (they read from much further).</summary>
    public const float WaveFadeStart = 1500, WaveFadeEnd = 3500, ShoreFadeStart = 7000, ShoreFadeEnd = 10000;

    /// <summary><c>MEITOU_WATER_DEBUG=1</c>: the Meitou water shows its shore fields (red the distance to the shore over 400, green the breaker phase, blue the depth over 40).</summary>
    static readonly bool DebugView = Environment.GetEnvironmentVariable("MEITOU_WATER_DEBUG") == "1";

    static readonly BlendState AlphaBlend = new(true, Silk.NET.Vulkan.BlendFactor.SrcAlpha, Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha);

    void Record(WaterProgram p, VertexArrayBindings va, BufferBinding[] vertices, uint indexCount)
    {
        var cmd = Gpu.BeginGuest(p == meitou ? "water meitou" : "water");
        var targets = Gpu.CurrentTargets();
        // What the GL code's Enable(DepthTest), DepthMask(false), Disable(CullFace), Enable(Blend) and BlendFunc made of the pass's state (the
        // depth test and blending only with the attachment they need, as VkGl's CurrentState).
        bool hasDepth = targets.Formats.Depth != Silk.NET.Vulkan.Format.Undefined, hasColour = targets.Formats.Colour != Silk.NET.Vulkan.Format.Undefined;
        var state = Gpu.CurrentState() with
        {
            Cull = Silk.NET.Vulkan.CullModeFlags.None, DepthTest = hasDepth, DepthWrite = false, Blend = hasColour ? AlphaBlend : BlendState.Off,
        };
        cmd.SetViewport(targets.Viewport);
        cmd.SetScissor(targets.Scissor);
        cmd.SetRaster(state.Cull, state.Front);
        cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
        cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
        cmd.BindPipeline(p.Segment.Get(state, targets.Formats, va));
        cmd.BindVertexBuffers(0, vertices);
        p.P.Flush(cmd);
        if (indexCount > 0)
        {
            cmd.BindIndexBuffer(va.Elements, Silk.NET.Vulkan.IndexType.Uint32);
            cmd.DrawIndexed(indexCount);
        }
        else cmd.Draw(4);
        Gpu.EndGuest(cmd);
    }

    public void Dispose()
    {
        quad.Dispose();
        grid.Dispose();
        gridIndices.Dispose();
        foreach (var t in maps) t.Dispose();
        faithful.Dispose();
        meitou.Dispose();
    }
}
