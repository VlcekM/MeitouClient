using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The crops of the player's farms (docs/render-objects.md "Farm plants"): one loose instance per plant mesh, drawn like the building's parts by
/// <see cref="WorldObjectRenderer"/>, shadows included.
/// </summary>
sealed class FarmPlants(ObjectStreamer streamer)
{
    /// <summary>
    /// A farm's plants: one loose instance per plant mesh and whether it is drawn now.
    /// </summary>
    public sealed class Slot
    {
        /// <summary>
        /// The plant meshes the instances were made from (a new list makes them again).
        /// </summary>
        public object? Meshes;

        /// <summary>
        /// One loose instance per plant mesh.
        /// </summary>
        public readonly List<ObjectStreamer.Instance> Instances = [];

        /// <summary>
        /// Whether each instance is drawn now (false: harvested).
        /// </summary>
        public bool[] Shown = [];
    }

    /// <summary>
    /// The farms' plants, by building id.
    /// </summary>
    readonly Dictionary<string, Slot> _farmSlots = new(StringComparer.Ordinal);

    /// <summary>
    /// The number of farms with plants.
    /// </summary>
    public int Count => _farmSlots.Count;

    /// <summary>
    /// Every farm's plants, for the draw.
    /// </summary>
    public Dictionary<string, Slot>.ValueCollection Slots => _farmSlots.Values;

    /// <summary>
    /// A farm's plants (docs/game/buildings-production.md "Farm plants"): <paramref name="meshes"/> are its plant meshes (the same list keeps its
    /// instances),
    /// <paramref name="transforms"/> where each is drawn now, null for one not drawn (harvested), <paramref name="tints"/> each one's colour (death
    /// tint and brightness). Drawn like the building's parts, shadows included.
    /// </summary>
    public void Set(string site, IReadOnlyList<PlacedMesh> meshes, IReadOnlyList<Matrix4x4?> transforms, IReadOnlyList<Vector3> tints)
    {
        if (!_farmSlots.TryGetValue(site, out var slot)) _farmSlots[site] = slot = new();
        if (!ReferenceEquals(slot.Meshes, meshes))
        {
            Drop(slot);
            slot.Meshes = meshes;
            foreach (var p in meshes) slot.Instances.Add(streamer.MakeLoose(p));
            slot.Shown = new bool[meshes.Count];
        }
        for (int i = 0; i < slot.Instances.Count; i++)
        {
            slot.Shown[i] = transforms[i] is not null;
            if (transforms[i] is not { } t) continue;

            var inst = slot.Instances[i];
            inst.Placed = inst.Placed with { Transform = t };
            inst.Transform = t;
            inst.Tint = PackTint(tints[i]);
            if (inst.Gpu is not { } gpu) continue;

            inst.Centre = Vector3.Transform(gpu.Centre, t);
            inst.Radius = gpu.Radius * new Vector3(t.M11, t.M12, t.M13).Length();
        }
    }

    /// <summary>
    /// A farm's plants are gone (the building is).
    /// </summary>
    public void Remove(string site)
    {
        if (_farmSlots.Remove(site, out var slot)) Drop(slot);
    }

    /// <summary>
    /// A tint for the instance matrix's spare M44 (<see cref="BuildingLodShaders"/> <c>instanceTint</c>): each channel in [0,
    /// <see cref="BuildingLodShaders.TintRange"/>] to 8 bits, packed r + 256 g + 65536 b, stored as -(packed + 1) so it is exact in a float and below
    /// every other use of M44.
    /// </summary>
    internal static float PackTint(Vector3 c)
    {
        static int Q(float v) => (int)MathF.Round(Math.Clamp(v / BuildingLodShaders.TintRange, 0, 1) * 255);
        return -(Q(c.X) + 256 * Q(c.Y) + 65536 * Q(c.Z) + 1);
    }

    /// <summary>
    /// Gives a farm's instances back to the streamer.
    /// </summary>
    void Drop(Slot slot)
    {
        foreach (var inst in slot.Instances) streamer.DropLoose(inst);
        slot.Instances.Clear();
        slot.Meshes = null;
    }
}
