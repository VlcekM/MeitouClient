using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Tests.Gameplay;

namespace Meitou.Tests.World;

/// <summary>
/// A building's position marker: its position plus its orientation applied to the flagged part's offset times the building scale (FUN_14055f6e0).
/// </summary>
public class PositionMarkerTests
{
    [Fact]
    public void Marker_is_the_flagged_parts_offset_turned_and_scaled()
    {
        var db = EconomyTests.Db(
            EconomyTests.Rec("building", FcsRecordType.Building, r =>
            {
                r.References["parts"] = [new("marker", 0, 0, 0), new("plain", 0, 0, 0)];
                r.Floats["scale"] = 2;
            }),
            EconomyTests.Rec("marker", FcsRecordType.BuildingPart, r =>
            {
                r.Filenames["phs or mesh"] = "bed.mesh";
                r.Bools["is for position marker"] = true;
                r.Floats["offset X"] = 10;
                r.Floats["offset Y"] = 1;
            }),
            EconomyTests.Rec("plain", FcsRecordType.BuildingPart, r => r.Filenames["phs or mesh"] = "plain.mesh"));
        var at = new Vector3(100, 5, 200);
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var marker = WorldObjectLayout.PositionMarker(db, db.Find("building")!, at, turn);
        var expected = at + Vector3.Transform(new Vector3(20, 2, 0), turn);
        Assert.NotNull(marker);
        Assert.True(Vector3.Distance(expected, marker!.Value) < 1e-3f);
        Assert.Null(WorldObjectLayout.PositionMarker(db, db.Find("plain")!, at, turn));
    }
}
