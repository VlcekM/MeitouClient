using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Gameplay;

/// <summary>
/// The population half of a BIOME_GROUP record (docs/game/factions-squads-towns.md 6.2, 6.3; the weather half is <see cref="RegionDef"/>): how many
/// nests the group's area holds and which, and the homeless (roaming) squads of its area sectors. Weights are v0 (fcs.def says v1; the data and the
/// code use v0, <b>Verified</b>, "Data corrections").
/// </summary>
public sealed record BiomeGroupData
{
    /// <summary>
    /// The BIOME_GROUP's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The BIOME_GROUP's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>index</c>: the colour (0xRRGGBB) that paints the group's cells in <c>areasmap.tga</c>.
    /// </summary>
    public int Colour { get; init; }

    /// <summary>
    /// <c>num nests</c>: the nests the group's area keeps (before the new game's <i>Number of nests multiplier</i>).
    /// </summary>
    public int NumNests { get; init; }

    /// <summary>
    /// <c>nests at fixed markers only</c>: the nests stand on the placed NEST_MARKER towns (TownType 8) of the area.
    /// </summary>
    public bool NestsAtFixedMarkersOnly { get; init; }

    /// <summary>
    /// <c>homeless spawn amount</c>: the target number of homeless squads per loaded sector (times the population multiplier).
    /// </summary>
    public float HomelessSpawnAmount { get; init; }

    /// <summary>
    /// <c>homeless spawns</c>: squad templates, v0 = the target share.
    /// </summary>
    public IReadOnlyList<RecordLink> HomelessSpawns { get; init; } = [];

    /// <summary>
    /// <c>nests</c>: TOWN records of type NEST, v0 = the weight (0 counts as 100).
    /// </summary>
    public IReadOnlyList<RecordLink> Nests { get; init; } = [];

    /// <summary>
    /// Reads the population fields of a BIOME_GROUP record.
    /// </summary>
    public static BiomeGroupData From(GameRecord r)
    {
        IReadOnlyList<RecordLink> Links(string list) => [.. r.GetReferences(list).Select(RecordLink.From)];

        return new BiomeGroupData
        {
            Id = r.StringId,
            Name = r.Name,
            Colour = r.GetInt("index"),
            NumNests = r.GetInt("num nests"),
            NestsAtFixedMarkersOnly = r.GetBool("nests at fixed markers only"),
            HomelessSpawnAmount = r.GetFloat("homeless spawn amount"),
            HomelessSpawns = Links("homeless spawns"),
            Nests = Links("nests"),
        };
    }

    /// <summary>
    /// Every BIOME_GROUP record, in the database's order.
    /// </summary>
    public static List<BiomeGroupData> LoadAll(GameDatabase db) => [.. db.OfType(FcsRecordType.BiomeGroup).Select(From)];
}

/// <summary>
/// The area sectors of the world (6.2): the 64 × 64 zone cells, each with the BIOME_GROUP whose colour paints it in <c>areasmap.tga</c>; a colour
/// without a group, and every cell when the map is missing, belongs to the group <c>NONE</c> (<see cref="WeatherData.NoneRegionId"/>). When two
/// groups share a colour the later record wins (as <see cref="WeatherData"/> does). The game computes the cell as <c>trunc((coordinate + 147465) /
/// 4608)</c>, 9 units off the zone grid; the engine uses the zone grid (<see cref="WeatherAreas.CellOf"/>), an engine choice.
/// </summary>
public sealed class BiomeMap
{
    /// <summary>
    /// Each cell's index into <see cref="Groups"/> (-1: none), row by row (z then x).
    /// </summary>
    readonly int[] _cells;
    /// <summary>
    /// The areas map painted (null: none, every point is painted).
    /// </summary>
    readonly WeatherAreas? _areas;

    /// <summary>
    /// A map of <paramref name="groups"/> with each of the 64 x 64 cells' group index (row by row); another cell count throws.
    /// <paramref name="areas"/>: the areas map <see cref="IsPainted"/> reads.
    /// </summary>
    public BiomeMap(IReadOnlyList<BiomeGroupData> groups, int[] cellGroups, WeatherAreas? areas = null)
    {
        if (cellGroups.Length != WeatherAreas.Cells * WeatherAreas.Cells)
            throw new ArgumentException("Expected 64 x 64 cells.", nameof(cellGroups));

        Groups = groups;
        _cells = cellGroups;
        _areas = areas;
    }

    /// <summary>
    /// The groups the cells index.
    /// </summary>
    public IReadOnlyList<BiomeGroupData> Groups { get; }

    /// <summary>
    /// The group of a cell, a position in <see cref="Groups"/>; -1 outside the map or when no group applies.
    /// </summary>
    public int GroupOf(int cx, int cz) => WeatherAreas.InMap(cx, cz) ? _cells[cz * WeatherAreas.Cells + cx] : -1;

    /// <summary>
    /// Whether the areas map is not black at a world point (<see cref="WeatherAreas.IsPainted"/>); true without a map.
    /// </summary>
    public bool IsPainted(float x, float z) => _areas?.IsPainted(x, z) ?? true;

    /// <summary>
    /// Paints the cells from the areas map's colours (null: every cell is NONE).
    /// </summary>
    public static BiomeMap Create(IReadOnlyList<BiomeGroupData> groups, WeatherAreas? areas)
    {
        var byColour = new Dictionary<int, int>();
        int none = -1;
        for (int i = 0; i < groups.Count; i++)
        {
            byColour[groups[i].Colour] = i;
            if (groups[i].Id == WeatherData.NoneRegionId) none = i;
        }

        var cells = new int[WeatherAreas.Cells * WeatherAreas.Cells];
        for (int z = 0; z < WeatherAreas.Cells; z++)
            for (int x = 0; x < WeatherAreas.Cells; x++)
            {
                int colour = areas?.ColourOf(x, z) ?? 0;
                cells[z * WeatherAreas.Cells + x] = areas is not null && byColour.TryGetValue(colour, out int g) ? g : none;
            }

        return new BiomeMap(groups, cells, areas);
    }

    /// <summary>
    /// Paints the cells of every BIOME_GROUP of <paramref name="db"/> from the areas map's colours.
    /// </summary>
    public static BiomeMap Create(GameDatabase db, WeatherAreas? areas) => Create(BiomeGroupData.LoadAll(db), areas);
}
