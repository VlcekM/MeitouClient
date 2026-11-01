using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;

// Natural sites (docs/simulation.md "Production"; the original's rocks are ProductionBuildings with "is foliage", docs/game/buildings-production.md "Resources"): a rock the foliage
// system places is worked like a player's mine. It is no BuildSite of the registry: its site is made from its id, a pure function of the rock's BUILDING and position, so orders and
// perma-jobs name it and a saved job finds it again. Its machine lives in World.Machines like any other and is not saved (the original saves no rock either, Observed).
public sealed partial class ProductionSystem
{
    const string NaturalPrefix = "natural:";

    readonly Dictionary<string, BuildSite?> naturalSites = new(StringComparer.Ordinal);
    /// <summary>
    /// A rock site's centre and bounds radius from the host's rock source, with its mesh's bounds box and the instance's transform when the source has the mesh: an operator inside
    /// them has reached the rock.
    /// </summary>
    readonly record struct RockBounds(Vector3 Centre, float Radius, RockShape? Shape, Matrix4x4 Transform)
    {
        public Vector2 Flat => new(Centre.X, Centre.Z);

        /// <summary>The X/Z gap from <paramref name="from"/> to the rock: to its bounds box where the instance places it (taken at the box's height), else to its bounds circle.</summary>
        public float Gap(Vector3 from)
        {
            var at = new Vector2(from.X, from.Z);
            if (Shape is not { } shape || !Matrix4x4.Invert(Transform, out var inverse)) return MathF.Max(0, Vector2.Distance(at, Flat) - Radius);
            var local = Vector3.Clamp(Vector3.Transform(new Vector3(from.X, Centre.Y, from.Z), inverse), shape.Min, shape.Max);
            var nearest = Vector3.Transform(local, Transform);
            return Vector2.Distance(at, new Vector2(nearest.X, nearest.Z));
        }

        /// <summary>Whether <paramref name="from"/> is inside the bounds box scaled 1.2 about its middle (taken at the box's height), else inside the bounds circle scaled 1.2.</summary>
        public bool Touches(Vector3 from)
        {
            if (Shape is not { } shape || !Matrix4x4.Invert(Transform, out var inverse)) return Vector2.Distance(new Vector2(from.X, from.Z), Flat) < 1.2f * Radius;
            var local = Vector3.Transform(new Vector3(from.X, Centre.Y, from.Z), inverse);
            Vector3 mid = (shape.Min + shape.Max) / 2, half = 1.2f * (shape.Max - shape.Min) / 2;
            return MathF.Abs(local.X - mid.X) <= half.X && MathF.Abs(local.Z - mid.Z) <= half.Z;
        }
    }

    readonly Dictionary<string, RockBounds> naturalBounds = new(StringComparer.Ordinal);

    /// <summary>The site id of the rock of BUILDING <paramref name="record"/> at <paramref name="at"/>.</summary>
    public static string NaturalId(string record, Vector3 at) =>
        string.Create(CultureInfo.InvariantCulture, $"{NaturalPrefix}{record}@{at.X:R},{at.Y:R},{at.Z:R}");

    /// <summary>Whether a site id names a rock (<see cref="NaturalId"/>).</summary>
    public static bool IsNatural(string id) => id.StartsWith(NaturalPrefix, StringComparison.Ordinal);

    /// <summary>A player site of the registry, else the rock a natural id names; null for neither.</summary>
    BuildSite? Site(World world, string id) => world.Buildings.Find(id) ?? Seat(world, id) ?? Natural(id);

    /// <summary>
    /// The rock a natural id names: a BUILDING with <c>is foliage</c> whose machine is an operable mine of a <c>world resource mining</c>; with a rock source, one of that BUILDING must
    /// stand within a unit of the position. Owned by no faction (-1: the original's <c>nofac</c> of its town NO_TOWN) until a player character works it; complete.
    /// </summary>
    BuildSite? Natural(string id)
    {
        if (!IsNatural(id)) return null;
        if (naturalSites.TryGetValue(id, out var known)) return known;
        BuildSite? site = null;
        int at = id.LastIndexOf('@');
        var parts = at > 0 ? id[(at + 1)..].Split(',') : [];
        if (parts.Length == 3 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            string record = id[NaturalPrefix.Length..at];
            var position = new Vector3(x, y, z);
            if (db.Find(record) is { Type: FcsRecordType.BUILDING } building && building.GetBool("is foliage") && Data(record) is { Operable: true, Resource: not 0 })
            {
                // No rock there is not remembered: the host's rock source may not have placed that zone yet.
                if (nodes is not null)
                {
                    if (nodes.Near(0, x, z, 1).FirstOrDefault(n => n.Record == record) is not { Record.Length: > 0 } rock) return null;
                    naturalBounds[id] = new RockBounds(rock.Centre ?? rock.Position, rock.Radius, rock.Shape, rock.Transform);
                }
                site = new BuildSite { Id = id, Record = record, Name = building.Name, Position = position, Owner = -1, Materials = [], Complete = true };
            }
        }
        return naturalSites[id] = site;
    }

    readonly Dictionary<string, float> naturalYields = new(StringComparer.Ordinal);

    /// <summary>
    /// The richness line of the building <paramref name="instance"/> names (<c>ProductionBuilding::getGUIFertility</c>, function 1 or 27 with a resource): a machine's yield, or a rock's
    /// before it has been mined (no machine yet); null for anything else.
    /// </summary>
    public RichnessView? RichnessOf(World world, string? instance)
    {
        if (instance is null) return null;
        if (world.Machines.Find(instance) is { } m)
            return m.Data is { Function: MachineData.MineFunction or MachineData.NaturalMineFunction, Resource: not 0 and var r } ? new(instance, r, m.Yield) { Owner = Natural(instance)?.Owner ?? -1, TownName = TownNameOf(world, Natural(instance)) } : null;
        if (Natural(instance) is not { } site || Data(site.Record) is not { Resource: not 0 and var resource } data) return null;
        if (!naturalYields.TryGetValue(instance, out float y)) naturalYields[instance] = y = resources.NodeYield(resource, site.Position);
        // A rock nobody has mined yet: no machine, so nobody works it; whole, no power draw; research counts only for the player's buildings (FUN_14082ec70).
        return new(instance, resource, y) { Owner = site.Owner, TownName = TownNameOf(world, site), Efficiency = new(0, data.PowerOperators, 1, 1, data.IsConsumer,
            data.OutputRate * world.Research.Upgrade(world, data.Id, site.Owner).ProductionMult, y) };
    }

    /// <summary>The name of the player town a worked rock joined (null: none, the original's NO_TOWN, which the panel's <c>Town:</c> line leaves out).</summary>
    static string? TownNameOf(World world, BuildSite? site) =>
        site is { Town: not 0 and var number } ? world.Buildings.Towns.FirstOrDefault(t => t.Number == number)?.Name : null;
}
