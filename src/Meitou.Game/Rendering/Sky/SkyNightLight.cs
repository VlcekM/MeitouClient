using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// What the big planet adds to the sky's light by night (Meitou, docs/formats/sky.md "Planetshine", "Night air"): the light it casts, and the air's
/// own faint radiance.
/// </summary>
static class SkyNightLight
{
    /// <summary>
    /// The planet's light counts as light for the shadows when its luminance is above this share of the reference sun light (a new planet casts
    /// nothing worth drawing).
    /// </summary>
    const float PlanetLitFloor = 1e-4f;

    /// <summary>
    /// Meitou planetshine (docs/formats/sky.md "Planetshine"): from the end of the sun's light the planet is the light, its colour in the sun light's
    /// own unit (the sun at the zenith, undimmed by the air in between, is the reference), added to the sun's, the direction weighted by their
    /// brightness. What the water's glint is lit by (WorldLighting.GlintDirection / GlintColour): the sun's own radiance and the planet disc's
    /// radiance as the sky pass draws it (unboosted: the strength-scaled light below is an irradiance made 60 times brighter than the disc, which
    /// blew the glint out). Left default (the sun's) by day. <paramref name="on"/>: the <see cref="SkyRenderer.Planetshine"/> switch;
    /// <paramref name="albedo"/> and <paramref name="strength"/>: the planet's colour and the planetshine's strength.
    /// </summary>
    public static (Vector3 LightDirection, Vector3 SunLight, Vector3 CloudPlanet, bool PlanetLit, Vector3 GlintDirection, Vector3 GlintColour)
        ApplyPlanetshine(bool on, Vector3 albedo, float strength, Vector3 sun, Vector3 lightDir, Vector3 sunLight, float reference)
    {
        bool planetLit = false;
        var cloudPlanet = Vector3.Zero;   // the planet's light the Meitou clouds take (MeitouClouds.Key), with the planetshine
        Vector3 glintDirection = default, glintColour = default;
        if (on && Meitou.Data.World.Planetshine.Weight(sun.Y) is var weight and > 0)
        {
            var planetLight = Meitou.Data.World.Planetshine.Light(sun, SkyPlanet.All[0], albedo, reference, strength) * weight;
            cloudPlanet = planetLight;
            var disc = Meitou.Data.World.Planetshine.DiscRadiance(sun, SkyPlanet.All[0], albedo) * weight;
            var sunRadiance = sunLight * (MathF.PI * (1 - KenshiLighting.DielectricSpecular));
            (glintDirection, glintColour) = Meitou.Data.World.Planetshine.Combine(lightDir, sunRadiance, SkyPlanet.All[0].Towards, disc);

            (lightDir, sunLight) = Meitou.Data.World.Planetshine.Combine(lightDir, sunLight, SkyPlanet.All[0].Towards, planetLight);
            planetLit = Meitou.Data.World.Planetshine.Luminance(planetLight) > PlanetLitFloor * reference;
        }
        return (lightDir, sunLight, cloudPlanet, planetLit, glintDirection, glintColour);
    }

    /// <summary>
    /// Meitou night air (docs/formats/sky.md "Night air"): the colour at full night from the airglow and the planet's light (the strength-scaled one,
    /// whatever the planetshine switch says), and its weight by the sun's height; the shaders add it to the haze's colour and to the sky's.
    /// <paramref name="on"/>: the <see cref="SkyRenderer.NightAir"/> switch.
    /// </summary>
    public static Vector4 NightAirAt(bool on, Vector3 albedo, float strength, Vector3 sun, float reference)
    {
        var night = Vector4.Zero;
        if (on && Meitou.Data.World.NightAir.Weight(sun.Y) is var airWeight and > 0)
        {
            var planetLight = Meitou.Data.World.Planetshine.Light(sun, SkyPlanet.All[0], albedo, reference, strength);
            var air = Meitou.Data.World.NightAir.Colour(planetLight);
            night = new Vector4(air, airWeight);
        }
        return night;
    }
}
