using DotRecast.Recast;
using Meitou.Data.World;

namespace Meitou.Simulation.Navigation;

internal static partial class ZoneNavMeshBuilder
{
    /// <summary>
    /// How far (units) the height between two neighbouring cells may jump beyond what both surfaces' own slopes explain before it counts as a step; also the
    /// largest rise between two cells of one surface.
    /// </summary>
    const float StepJump = 3;

    /// <summary>
    /// Cosine of the largest angle between the two edges of a step that still joins (engine choice: about 37°, clear of the slopes' sampling error; a spiral
    /// stair's top meets its floor at about 28°, the side of a 45° ramp a flat ledge at 45°).
    /// </summary>
    const float StepEdgeAlignment = 0.8f;

    /// <summary>How near (units) the terrain under its cell a span counts as standing on the terrain (the heightmap is sampled bilinearly here).</summary>
    const float TerrainSlack = 4;

    /// <summary>
    /// Cells along the edge over which each side's slope is measured, at least (engine choice: a slope from one or two cells is noise at a surface's rim:
    /// a spiral stair's top was cut from its floor).
    /// </summary>
    const int SlopeBaseCells = 3;

    /// <summary>Cells each way along the edge a side's slope is measured over, at most.</summary>
    const int SlopeReachCells = 3;

    /// <summary>
    /// A link between span <see cref="Span"/> and its neighbour <see cref="Neighbour"/> in direction <see cref="Direction"/>, with the connection
    /// indices Recast gave both ends.
    /// </summary>
    readonly record struct StepLink(int Span, int Direction, int Neighbour, int Con, int NeighbourCon);

    /// <summary>
    /// The links between neighbouring spans that meet on a step whose two edges rise differently along it. Recast joins any two cells within the climb, so a
    /// raised flat ledge joined the side of a stair ramp where the ramp came within the climb of it, and the ledge was kept with the stair; the original joins
    /// two surfaces only along edges that run alike (docs/game/pathfinding.md, "Own builder", side steps). Cells on one smooth surface, whose height and slope
    /// along the edge change as their own slopes say, and steps from the terrain keep their links.
    /// </summary>
    static List<StepLink> SideSteps(RcCompactHeightfield chf, HeightWindow? terrain)
    {
        var steps = new List<StepLink>();
        for (int z = 0; z < chf.height; z++)
            for (int x = 0; x < chf.width; x++)
            {
                var cell = chf.cells[x + z * chf.width];
                for (int i = cell.index, end = cell.index + cell.count; i < end; i++)
                    for (int d = 1; d <= 2; d++)
                    {
                        if (Neighbour(chf, x, z, i, d) is not { } j || !IsSideStep(chf, x, z, i, d, j) || OnTerrain(chf, terrain, x, z, i, d, j)) continue;
                        steps.Add(new StepLink(i, d, j, RcRecast.GetCon(chf.spans[i], d), RcRecast.GetCon(chf.spans[j], (d + 2) % 4)));
                    }
            }
        return steps;
    }

    /// <summary>Unlinks the side steps before the regions are built, so a surface reached only over one becomes a region of its own.</summary>
    static void CutSteps(RcCompactHeightfield chf, List<StepLink> steps)
    {
        foreach (var s in steps)
        {
            SetCon(chf, s.Span, s.Direction, RcRecast.RC_NOT_CONNECTED);
            SetCon(chf, s.Neighbour, (s.Direction + 2) % 4, RcRecast.RC_NOT_CONNECTED);
        }
    }

    /// <summary>
    /// Links again the side steps whose two spans ended in one region: the surfaces join elsewhere anyway, and a link missing inside a region leaves a slit in
    /// its outline, which Recast's triangulation can drop the whole region over.
    /// </summary>
    static void RelinkWithinRegions(RcCompactHeightfield chf, List<StepLink> steps)
    {
        foreach (var s in steps)
        {
            if (chf.spans[s.Span].reg != chf.spans[s.Neighbour].reg) continue;
            SetCon(chf, s.Span, s.Direction, s.Con);
            SetCon(chf, s.Neighbour, (s.Direction + 2) % 4, s.NeighbourCon);
        }
    }

    static void SetCon(RcCompactHeightfield chf, int span, int d, int con)
    {
        var builder = RcCompactSpanBuilder.NewBuilder(ref chf.spans[span]);
        RcRecast.SetCon(builder, d, con);
        chf.spans[span] = builder.Build();
    }

    /// <summary>
    /// Whether the link from span <paramref name="i"/> in direction <paramref name="d"/> to span <paramref name="j"/> joins edges that do not run alike: a
    /// step the surfaces' own slopes do not explain, or two surfaces crossing at one height, such as a stair ramp rising past the side of a ledge (Stephouse,
    /// whose outer steps passed its wall top).
    /// </summary>
    static bool IsSideStep(RcCompactHeightfield chf, int x, int z, int i, int d, int j)
    {
        if (chf.areas[i] == NavArea.Null || chf.areas[j] == NavArea.Null) return false;

        float jump = (chf.spans[j].y - chf.spans[i].y) * chf.ch;
        int nx = x + RcRecast.GetDirOffsetX(d), nz = z + RcRecast.GetDirOffsetY(d);
        if (MathF.Abs(jump) >= StepJump && MathF.Abs(jump - ExpectedRise(chf, x, z, i, d, nx, nz, j)) < StepJump) return false;

        if (SlopeAlong(chf, x, z, i, d) is not { } here || SlopeAlong(chf, nx, nz, j, d) is not { } there) return false;

        float cos = (1 + here * there) / MathF.Sqrt((1 + here * here) * (1 + there * there));
        return cos < StepEdgeAlignment;
    }

    /// <summary>The rise across the link the two surfaces' own slopes towards it explain (their mean, or the one measured; 0 when neither is).</summary>
    static float ExpectedRise(RcCompactHeightfield chf, int x, int z, int i, int d, int nx, int nz, int j)
    {
        float? before = Rise(chf, x, z, i, (d + 2) % 4) is { } b ? -b : null;
        float? after = Rise(chf, nx, nz, j, d);
        return (before, after) switch
        {
            ({ } p, { } q) => (p + q) / 2,
            ({ } p, null) => p,
            (null, { } q) => q,
            _ => 0,
        };
    }

    /// <summary>Whether either span of a link stands on the terrain: steps onto rocks, features and slabs from the ground keep Recast's links.</summary>
    static bool OnTerrain(RcCompactHeightfield chf, HeightWindow? terrain, int x, int z, int i, int d, int j)
    {
        if (terrain is null) return false;

        int nx = x + RcRecast.GetDirOffsetX(d), nz = z + RcRecast.GetDirOffsetY(d);
        return NearTerrain(chf, terrain, x, z, i) || NearTerrain(chf, terrain, nx, nz, j);
    }

    static bool NearTerrain(RcCompactHeightfield chf, HeightWindow terrain, int x, int z, int i)
    {
        if (!terrain.TryHeightAt(chf.bmin.X + (x + 0.5f) * chf.cs, chf.bmin.Z + (z + 0.5f) * chf.cs, out float ground)) return false;
        return MathF.Abs(chf.bmin.Y + chf.spans[i].y * chf.ch - ground) <= TerrainSlack;
    }

    /// <summary>
    /// The rise (units) from span <paramref name="i"/> to its walkable neighbour in direction <paramref name="d"/> on the same surface; null when it has none,
    /// or only one a step away.
    /// </summary>
    static float? Rise(RcCompactHeightfield chf, int x, int z, int i, int d)
    {
        if (Neighbour(chf, x, z, i, d) is not { } n || chf.areas[n] == NavArea.Null) return null;

        float rise = (chf.spans[n].y - chf.spans[i].y) * chf.ch;
        return MathF.Abs(rise) < StepJump ? rise : null;
    }

    /// <summary>
    /// The surface's slope at span <paramref name="i"/> along the edge of a step in direction <paramref name="d"/> (rise per unit), over up to
    /// <see cref="SlopeReachCells"/> cells each way on its own surface; null when the surface runs on for less than <see cref="SlopeBaseCells"/>
    /// along the edge.
    /// </summary>
    static float? SlopeAlong(RcCompactHeightfield chf, int x, int z, int i, int d)
    {
        var (aheadCells, ahead) = Walk(chf, x, z, i, (d + 1) % 4);
        var (behindCells, behind) = Walk(chf, x, z, i, (d + 3) % 4);
        int cells = aheadCells + behindCells;
        return cells < SlopeBaseCells ? null : (ahead - behind) / (cells * chf.cs);
    }

    /// <summary>
    /// How many cells (up to <see cref="SlopeReachCells"/>) the surface runs on from span <paramref name="i"/> in direction <paramref name="d"/>, and
    /// its rise over them.
    /// </summary>
    static (int Cells, float Rise) Walk(RcCompactHeightfield chf, int x, int z, int i, int d)
    {
        int cells = 0;
        float rise = 0;
        while (cells < SlopeReachCells && Rise(chf, x, z, i, d) is { } r && Neighbour(chf, x, z, i, d) is { } n)
        {
            (x, z, i) = (x + RcRecast.GetDirOffsetX(d), z + RcRecast.GetDirOffsetY(d), n);
            rise += r;
            cells++;
        }
        return (cells, rise);
    }

    /// <summary>
    /// The span linked to span <paramref name="i"/> of cell (<paramref name="x"/>, <paramref name="z"/>) in direction <paramref name="d"/>; null when none is.
    /// </summary>
    static int? Neighbour(RcCompactHeightfield chf, int x, int z, int i, int d)
    {
        int con = RcRecast.GetCon(chf.spans[i], d);
        if (con == RcRecast.RC_NOT_CONNECTED) return null;

        int nx = x + RcRecast.GetDirOffsetX(d), nz = z + RcRecast.GetDirOffsetY(d);
        return chf.cells[nx + nz * chf.width].index + con;
    }
}