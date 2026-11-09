using System.Numerics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The renderer's sections of the F11 statistics panel in the game and the viewer (what is drawn, memory, the camera and the weather;
/// each host's performance lines come before them).
/// </summary>
static class WorldStats
{
    /// <summary>
    /// A frame's triangles in the main colour view, averaged over the panel's second as the bench counts them (<c>BenchSession.MeasureCounts</c>):
    /// the objects' include the water reflection's, the foliage's are null until its GPU cull's tally has been read back
    /// (<see cref="FoliageGpuCull.CountTriangles"/>), and grass blades are counted apart.
    /// </summary>
    public readonly record struct Triangles(double Terrain, double Objects, double? Foliage, double Characters, double GrassBlades)
    {
        /// <summary>
        /// All the frame's triangles (grass blades not counted); the foliage's count as 0 while unknown.
        /// </summary>
        public double Total => Terrain + Objects + (Foliage ?? 0) + Characters;
    }

    /// <summary>
    /// The characters line's checkboxes (<see cref="Add"/> with <c>characterBoxes</c>): the host ticks each as its switch stands and toggles it on a
    /// click.
    /// </summary>
    public const string CharacterBoxesOff = "[ ] boxes", CharacterBoxesOn = "[x] boxes";

    /// <summary>
    /// The scene line's paths checkbox, ticked as the host's path switch stands.
    /// </summary>
    public const string PathsOff = "[ ] paths", PathsOn = "[x] paths";

    /// <summary>
    /// Adds the Scene, Memory and World sections (what is drawn and loading, video memory and RAM, the camera and the weather) to <paramref
    /// name="stats"/>; <paramref name="characterBoxes"/> puts the boxes and paths checkboxes on the characters line.
    /// </summary>
    public static void Add(List<string> stats, GpuContext context, WorldGpu gpu, Vector3 camera, in Triangles tris, bool characterBoxes = false)
    {
        stats.Add("");
        stats.Add("Scene");
        stats.Add($"  triangles     {Millions(tris.Total)} a frame: terrain {Millions(tris.Terrain)}, objects {Millions(tris.Objects)}, " +
                  $"plants {(tris.Foliage is { } f ? Millions(f) : "-")}, characters {Millions(tris.Characters)}");
        if (gpu.Foliage is { Enabled: true })
            stats.Add($"  grass         {tris.GrassBlades / 1000:0}k blades (not in the triangles)");

        if (gpu.Characters is { } chars)
        {
            string boxes = characterBoxes ? "   " + CharacterBoxesOff + "   " + PathsOff : "";
            stats.Add($"  characters    {chars.DrawnCharacters} drawn{boxes}");
        }

        int loadingTerrain = gpu.Streamer?.Pending ?? 0, loadingObjects = gpu.Objects?.Pending ?? 0, loadingFoliage = gpu.Foliage?.Pending ?? 0;
        if (loadingTerrain + loadingObjects + loadingFoliage > 0)
            stats.Add($"  loading       terrain {loadingTerrain}, objects {loadingObjects}, plants {loadingFoliage}");

        stats.Add("");
        stats.Add("Memory");
        var (vramUsed, vramBudget) = context.Device.VideoMemory();
        stats.Add($"  video memory  {vramUsed / 1073741824.0:0.00} of {vramBudget / 1073741824.0:0.0} GB");
        if (gpu.Guard is { Active: true } vramGuard) stats.Add($"  {vramGuard.Status}");
        stats.Add($"  ram           {(GC.GetGCMemoryInfo().HeapSizeBytes + Data.NativeBuffer.LiveBytes) / 1073741824.0:0.00} GB");

        stats.Add("");
        stats.Add("World");
        stats.Add($"  camera        {camera.X:0}, {camera.Y:0}, {camera.Z:0}, zone {WorldLayout.ZoneOf(camera.X, camera.Z)}");
        if (gpu.Weather is { } w) stats.Add($"  weather       {w.Describe()}");
    }

    /// <summary>
    /// <paramref name="triangles"/> in millions with two decimals.
    /// </summary>
    static string Millions(double triangles) => $"{triangles / 1e6:0.00} M";
}