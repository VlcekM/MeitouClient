using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Game.Building;

namespace Meitou.Tests.Gameplay;

/// <summary>
/// <see cref="WallLine.Layout"/> (docs/game/buildings-production.md "Walls"): the greedy split of a straight run and the bend pieces
/// at a snapped joint.
/// </summary>
public class WallLineTests
{
    static readonly BuildingBuildData Short = Piece("short", 4, 40);
    static readonly BuildingBuildData Connector = Piece("connector", 1, 0);
    static readonly BuildingBuildData Wedge = Piece("wedge", 2, 0);
    static readonly BuildingBuildData Wall = Piece("wall", 0, 255) with { WallSubsections = ["short", "connector", "wedge"] };
    static readonly Dictionary<string, BuildingBuildData> All = new[] { Wall, Short, Connector, Wedge }.ToDictionary(b => b.Id);

    static BuildingBuildData Piece(string id, int type, float length) => new()
    {
        Id = id, Name = id, LinksTogether = true, LinkType = type, LinkLength = length,
    };

    static float Flat(float x, float z) => 100;

    [Fact]
    public void A_flat_line_takes_full_pieces_then_shorts_and_may_overshoot_by_up_to_10()
    {
        var pieces = WallLine.Layout(Wall, All.GetValueOrDefault, Flat, new(0, 100, 0), null, new(1000, 100, 0), null, reversed: false);

        Assert.Equal(["wall", "wall", "wall", "short", "short", "short", "short", "short", "short"], pieces.Select(p => p.Data.Id));
        float[] starts = [0, 255, 510, 765, 805, 845, 885, 925, 965];
        for (int i = 0; i < pieces.Count; i++)
        {
            Assert.Equal(new Vector3(starts[i], 100, 0), pieces[i].Position, new Vector3Comparer(1e-3f));
            Assert.Equal(Vector3.UnitX, Vector3.Transform(Vector3.UnitZ, pieces[i].Rotation), new Vector3Comparer(1e-4f));
        }
    }

    [Fact]
    public void A_right_angle_at_a_snapped_end_gets_one_short_turned_halfway()
    {
        var links = WallLine.Links([(Wall, new Vector3(-255, 100, 0), WallLine.LookRotation(Vector3.UnitX))]);
        var snap = WallLine.Find(links, new(20, 100, 30));
        Assert.Equal(1, snap!.Value.Code);   // the free end
        Assert.Equal(new Vector3(0, 100, 0), snap.Value.Point, new Vector3Comparer(1e-3f));

        var pieces = WallLine.Layout(Wall, All.GetValueOrDefault, Flat, snap.Value.Point, snap, new(0, 100, 800), null, reversed: false);

        Assert.Equal(["short", "wall", "wall", "wall"], pieces.Select(p => p.Data.Id));
        float h = 40 / MathF.Sqrt(2);
        Assert.Equal(new Vector3(h, 100, h), WallLine.Ends(Short, pieces[0].Position, pieces[0].Rotation).End, new Vector3Comparer(1e-2f));
        Assert.Equal(pieces[0].Position + new Vector3(h, 0, h), pieces[1].Position, new Vector3Comparer(1e-2f));
        var lastEnd = WallLine.Ends(Wall, pieces[^1].Position, pieces[^1].Rotation).End;
        Assert.True(Vector3.Distance(lastEnd, new(0, 100, 800)) < WallLine.StopDistance);
    }

    sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector3 v) => 0;
    }
}
