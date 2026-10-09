using System.Numerics;
using Meitou.Data.World;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The renderer's lines of the F11 statistics panel (video memory, the passes' costs, what is drawn and resident), shared by the game and
/// the viewer; each adds its own lines (frame rate, simulation) before them.
/// </summary>
static class WorldStats
{
    /// <summary>The weather's name for the heat-haze line: the camera's weather (the scheduler's or the forced one).</summary>
    public static string WeatherName(WorldFrame.Gpu gpu) => gpu.Weather is { } w ? w.State.Weather.Name : gpu.Sky.Weather.Name;

    /// <summary>The weather lines: camera region, season, the weather with strength, wind and the time left; then the sky, fog and rain values it feeds.</summary>
    public static void AddWeather(List<string> stats, WorldFrame.Gpu gpu)
    {
        if (gpu.Weather is not { } w) return;
        var s = w.State;
        stats.Add($"weather     zone {(w.CameraZone is { } z ? $"{z.X},{z.Y}" : "-")}; {w.Describe()}");
        stats.Add($"  sky x {s.SkyColourMultiplier.X:0.00} {s.SkyColourMultiplier.Y:0.00} {s.SkyColourMultiplier.Z:0.00}, clouds {s.CloudDensity:0.00}, fog {(s.FogEnabled > 0 ? $"{s.FogEnabled:0.00} to {s.FogDistance:0}" : "off")}, rain {s.Rain:0}, wet {s.Wetness:0.00}");
    }

    public static void Add(List<string> stats, GpuContext context, WorldFrame.Gpu gpu, WorldRenderOptions render, Vector3 camera)
    {
        var (vramUsed, vramBudget) = context.Device.VideoMemory();
        var alloc = context.Device.Allocator;
        stats.Add($"vram        {vramUsed / 1073741824.0:0.00} of {vramBudget / 1073741824.0:0.0} GB; our blocks {alloc.TotalAllocatedBytes / 1073741824.0:0.00} GB, {alloc.TotalUsedBytes / 1073741824.0:0.00} used");
        if (gpu.Guard is { } vramGuard) stats.Add($"  {vramGuard.Status}");
        // The largest owners (GpuAllocator.Breakdown: names without their numbers), to see what fills the VRAM.
        foreach (var (name, count, bytes, _) in alloc.Breakdown().Take(8))
            stats.Add($"  {name,-26} {bytes / 1048576.0,7:0} MB  x{count}");
        if (gpu.Reflection is { Valid: true } refl && render.Reflections) stats.Add($"reflection  cpu {refl.CpuMs:0.00} ms, gpu {refl.GpuMs:0.00} ms");
        gpu.Sky.Poll();
        stats.Add(gpu.Sky.Physical ? $"sky         cpu {gpu.Sky.PrepareMs:0.00} ms, gpu {gpu.Sky.GpuMs:0.00} ms" : "sky         simple");
        if (gpu.Post is { } post) stats.Add($"post gpu    {post.DescribeCosts()}");
        if (gpu.Post is { } hazy && (hazy.HeatHazeAmount > 0 || gpu.HeatHazeTarget > 0))
            stats.Add($"heat haze   {hazy.HeatHazeAmount:0.00} (target {gpu.HeatHazeTarget:0.00}, weather {WorldStats.WeatherName(gpu)})" + (hazy.HeatHazeRuns ? "" : hazy.HasHeatHaze ? ", off" : ", no textures"));
        AddWeather(stats, gpu);
        stats.Add($"camera      {camera.X:0}, {camera.Y:0}, {camera.Z:0}, zone {WorldLayout.ZoneOf(camera.X, camera.Z)}");
        if (gpu.FogVolumes?.DescribeCull() is { } fogCull) stats.Add($"fog cull    {fogCull}");
        if (gpu.Gi is { } gi) stats.Add($"gi          {gi.Describe()}");
        if (gpu.Probes is { } probes) stats.Add($"gi probes   {probes.Describe()}");
        if (gpu.Post is { OcclusionCull: true } occlusionPost && gpu.Foliage is { } occlusionFoliage) stats.Add($"occlusion   {occlusionFoliage.OccludedInstances} foliage instances left out (pyramid {occlusionPost.Hiz?.Describe ?? "none"})");
        stats.Add($"terrain     {gpu.Terrain.DrawnChunks} chunks, {gpu.Terrain.DrawnTriangles / 1000}k tris" + (gpu.Streamer is { Pending: > 0 } st ? $", loading {st.Pending}" : ""));
        if (gpu.Objects is { } ob && render.Objects)
            stats.Add($"objects     {ob.DrawnInstances}, {ob.DrawCalls} calls, draw cpu {ob.LastDrawCpuMs:0.00} ms" + (ob.Pending > 0 ? $", loading {ob.Pending}" : ""));
        if (gpu.Foliage is { Enabled: true } fo)
            stats.Add($"foliage     {fo.DrawnInstances} + {fo.DrawnBlades / 1000}k grass, {fo.DrawCalls} calls, cpu {fo.LastDrawCpuMs:0.00} ms, gpu {fo.GpuMs:0.00} ms" + (fo.Pending > 0 ? $", loading {fo.Pending}" : ""));
        if (gpu.Characters is { } chars) stats.Add($"characters  {chars.Statistics()}");
        stats.Add($"resident    {((gpu.Objects?.ResidentBytes ?? 0) + (gpu.Foliage?.ResidentBytes ?? 0)) / 1048576} MB");
        if (gpu.Foliage is { } fr) stats.Add($"  foliage   {fr.ResidentDescription}; {fr.Describe()}");
        if (gpu.Foliage is { } fs) stats.Add($"  scratch   {fs.ScratchDescription}");
        if (gpu.Objects is { } orr) stats.Add($"  objects   {orr.ResidentDescription}");
    }
}
