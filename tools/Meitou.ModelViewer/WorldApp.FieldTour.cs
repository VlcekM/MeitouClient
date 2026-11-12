using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Terrain;
using static Meitou.Game.Rendering.Frame.WorldFrame;

namespace Meitou.ModelViewer;

/// <summary>
/// The world viewer's terrain sandbox arguments and its field tour (<c>--field-tour</c>).
/// </summary>
static partial class WorldApp
{
    /// <summary>
    /// <c>--field-tour &lt;dir&gt;</c> (with <c>--terrain-sandbox</c>): a picture of every texture field tile, close up, and an index.html.
    /// </summary>
    static string? s_fieldTour;

    /// <summary>
    /// The tour's camera: distance from the tile's centre and degrees above the horizon (<c>--tour-distance</c>, <c>--tour-pitch</c>).
    /// </summary>
    static float s_tourDistance = 120, s_tourPitch = 30;

    /// <summary>
    /// Takes <c>--terrain-sandbox [x,z]</c> and <c>--field-tour &lt;dir&gt;</c> out of <paramref name="args"/> (docs/render-terrain.md, "Texture
    /// field"): the field round the spot (default the game's), the camera there, no foliage or objects, clear weather at 10:00 unless given; a tour
    /// also asks for a picture (the overview, taken after the tour).
    /// </summary>
    static string[] TerrainSandboxArgs(string[] args)
    {
        var rest = args.ToList();
        ReliefArgs(rest);
        int at = rest.IndexOf("--terrain-sandbox");
        if (at < 0) return [.. rest];
        var spot = new Vector2(38376, -8280);
        rest.RemoveAt(at);
        if (at < rest.Count && rest[at] == "cliffs")
        {
            (spot, GroundRelief.FieldCliffs) = (new Vector2(92164, 123124), true);
            rest.RemoveAt(at);
            if (!rest.Contains("--yaw")) rest.AddRange(["--yaw", "180"]);
        }
        if (at < rest.Count && rest[at].Split(',') is [var x, var z]
                && float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out float fx)
            && float.TryParse(z, NumberStyles.Float, CultureInfo.InvariantCulture, out float fz))
        {
            spot = new Vector2(fx, fz);
            rest.RemoveAt(at);
        }
        GroundRelief.FieldCentre = spot;
        int tour = rest.IndexOf("--field-tour");
        if (tour >= 0 && tour + 1 < rest.Count)
        {
            s_fieldTour = rest[tour + 1];
            rest.RemoveRange(tour, 2);
            float Take(string name, float value)
            {
                int k = rest.IndexOf(name);
                if (k < 0 || k + 1 >= rest.Count) return value;
                value = float.Parse(rest[k + 1], CultureInfo.InvariantCulture);
                rest.RemoveRange(k, 2);
                return value;
            }
            (s_tourDistance, s_tourPitch) = (Take("--tour-distance", s_tourDistance), Take("--tour-pitch", s_tourPitch));
            Directory.CreateDirectory(s_fieldTour);
            if (!rest.Contains("--screenshot"))
                rest.AddRange(["--screenshot", Path.Combine(s_fieldTour, "overview.png")]);
        }
        if (!rest.Contains("--at"))
            rest.AddRange(["--at", string.Create(CultureInfo.InvariantCulture, $"{spot.X},{spot.Y}")]);
        if (!rest.Contains("--weather")) rest.AddRange(["--weather", "Default"]);
        if (!rest.Contains("--time")) rest.AddRange(["--time", "10"]);
        rest.AddRange(["--no-foliage", "--no-objects"]);
        return [.. rest];
    }

    /// <summary>
    /// Takes every <c>--relief Name=value</c> out of <paramref name="rest"/>: a <see cref="GroundRelief"/> or <see cref="RockSmoothing"/> setting for
    /// the run.
    /// </summary>
    static void ReliefArgs(List<string> rest)
    {
        for (int k; (k = rest.IndexOf("--relief")) >= 0 && k + 1 < rest.Count;)
        {
            var (name, value) = (rest[k + 1].Split('=')[0], float.Parse(rest[k + 1].Split('=')[1], CultureInfo.InvariantCulture));
            var property = typeof(GroundRelief).GetProperty(name) ?? typeof(RockSmoothing).GetProperty(name)
                    ?? throw new ArgumentException($"--relief {name}: no such setting");
            property.SetValue(null, property.PropertyType == typeof(bool) ? value != 0
                    : Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
            Console.WriteLine($"relief    {name} = {property.GetValue(null)}");
            rest.RemoveRange(k, 2);
        }
    }

    /// <summary>
    /// The tour: each tile from <see cref="s_tourDistance"/> back at the camera's heading and <see cref="s_tourPitch"/> up, saved as tile-NNN.png.
    /// </summary>
    static void FieldTour(WorldGpu gpu, WorldScene scene, WorldCamera camera, WorldRenderOptions render, WorldOptions o, int w, int h,
            GpuContext context, GpuTexture target)
    {
        if (s_fieldTour is null || gpu.Terrain.TextureSet is not { FieldCount: > 0 } t) return;
        var (yaw, pitch, distance, targetAt) = (camera.Yaw, camera.Pitch, camera.Distance, camera.Target);
        (camera.Pitch, camera.Distance) = (s_tourPitch * MathF.PI / 180, s_tourDistance);
        var html = new StringBuilder("""
            <!doctype html><html><head><meta charset="utf-8"><title>Field tour</title><style>
            body{font:13px system-ui,sans-serif;background:#1b1b1b;color:#ddd;margin:16px}div.t{display:inline-block;margin:4px;width:480px;vertical-align:top}
            img{width:480px;display:block}</style></head><body>

            """);
        for (int i = 0; i < t.FieldCount; i++)
        {
            float cx = t.FieldOrigin.X + (i % t.FieldColumns + 0.5f) * t.FieldTile, cz = t.FieldOrigin.Y + (i / t.FieldColumns + 0.5f) * t.FieldTile;
            camera.Target = new Vector3(cx, gpu.Terrain.HeightAt(cx, cz), cz);
            gpu.Streamer?.Settle(camera.Eye);
            for (int f = 0; f < gpu.Post!.WarmupFrames + 3; f++)
            {
                Draw(gpu, scene, camera, render, w, h, o.Hour, 0, o.FogDistance);
                EndFrame(context);
            }
            context.Finish();
            string name = $"tile-{i + 1:000}.png", label = t.FieldLabel(cx, cz) ?? "";
            FramebufferCapture.SavePng(context, target, Path.Combine(s_fieldTour, name), w, h);
            html.Append(CultureInfo.InvariantCulture,
                    $"<div class=\"t\"><img loading=\"lazy\" src=\"{name}\" alt=\"\">{System.Net.WebUtility.HtmlEncode(label)}</div>\n");
            Console.WriteLine($"tour      {label}");
        }
        File.WriteAllText(Path.Combine(s_fieldTour, "index.html"), html.Append("</body></html>\n").ToString());
        Console.WriteLine($"tour      {Path.Combine(s_fieldTour, "index.html")}");
        (camera.Yaw, camera.Pitch, camera.Distance, camera.Target) = (yaw, pitch, distance, targetAt);
    }
}
