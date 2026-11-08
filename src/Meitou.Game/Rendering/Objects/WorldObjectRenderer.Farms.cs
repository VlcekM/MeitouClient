using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Rendering;

public sealed partial class WorldObjectRenderer
{
    // The crops of the player's farms, by building id: one loose instance per plant mesh and whether it is drawn now (docs/render-objects.md "Farm plants").
    sealed class FarmSlot
    {
        public object? Meshes;
        public readonly List<ObjectStreamer.Instance> Instances = [];
        public bool[] Shown = [];
    }

    readonly Dictionary<string, FarmSlot> farmSlots = new(StringComparer.Ordinal);

    /// <summary>
    /// A farm's plants (docs/game/buildings-production.md "Farm plants"): <paramref name="meshes"/> are its plant meshes (the same list keeps its instances),
    /// <paramref name="transforms"/> where each is drawn now, null for one not drawn (harvested), <paramref name="tints"/> each one's colour (death tint and
    /// brightness). Drawn like the building's parts, shadows included.
    /// </summary>
    public void SetFarmPlants(string site, IReadOnlyList<PlacedMesh> meshes, IReadOnlyList<Matrix4x4?> transforms, IReadOnlyList<Vector3> tints)
    {
        if (!farmSlots.TryGetValue(site, out var slot)) farmSlots[site] = slot = new();
        if (!ReferenceEquals(slot.Meshes, meshes))
        {
            DropFarm(slot);
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
    /// A tint for the instance matrix's spare M44 (<see cref="BuildingLodShaders"/> <c>instanceTint</c>): each channel in [0, <see cref="BuildingLodShaders.TintRange"/>] to 8 bits,
    /// packed r + 256 g + 65536 b, stored as -(packed + 1) so it is exact in a float and below every other use of M44.
    /// </summary>
    internal static float PackTint(Vector3 c)
    {
        static int Q(float v) => (int)MathF.Round(Math.Clamp(v / BuildingLodShaders.TintRange, 0, 1) * 255);
        return -(Q(c.X) + 256 * Q(c.Y) + 65536 * Q(c.Z) + 1);
    }

    /// <summary>A farm's plants are gone (the building is).</summary>
    public void RemoveFarmPlants(string site)
    {
        if (farmSlots.Remove(site, out var slot)) DropFarm(slot);
    }

    /// <summary>The top of a mesh (its bounds' maximum y), from its file's bounds chunk; 0 when not found.</summary>
    public float MeshTop(string meshPath) => MeshBounds(meshPath).Item2.Y;

    void DropFarm(FarmSlot slot)
    {
        foreach (var inst in slot.Instances) streamer.DropLoose(inst);
        slot.Instances.Clear();
        slot.Meshes = null;
    }

    void DrawFarms(float range, Vector3 eye, Vector4[] frustum, WorldRenderOptions options, long now)
    {
        foreach (var slot in farmSlots.Values)
            for (int i = 0; i < slot.Instances.Count; i++)
                if (slot.Shown[i]) DrawReal(streamer.Loose, slot.Instances[i], range, eye, frustum, options, now);
    }
}
