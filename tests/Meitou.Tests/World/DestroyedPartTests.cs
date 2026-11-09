using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Tests.Gameplay;

namespace Meitou.Tests.World;

/// <summary>
/// How a destroyed building lays out its parts (FUN_14055f6e0:144-176).
/// </summary>
public class DestroyedPartTests
{
    static FcsRecord Part(string id, Action<FcsRecord> fill) => EconomyTests.Rec(id, FcsRecordType.BuildingPart, fill);

    [Fact]
    public void Upper_parts_without_a_destroyed_mesh_stay_as_collision_only()
    {
        var db = EconomyTests.Db(
            EconomyTests.Rec("b", FcsRecordType.Building,
                    r => r.References["parts"] = [new("ground", 0, 0, 0), new("upper", 0, 0, 0), new("stairs", 0, 0, 0), new("ruined", 0, 0, 0)]),
            Part("ground", r => { r.Filenames["phs or mesh"] = "g.mesh"; r.Filenames["xml collision"] = "g.xml"; }),
            Part("upper", r => { r.Filenames["phs or mesh"] = "u.mesh"; r.Filenames["xml collision"] = "u.xml"; r.Ints["building floor"] = 1; }),
            Part("stairs", r => { r.Filenames["phs or mesh"] = "s.mesh"; r.Bools["is stairs"] = true; }),
            Part("ruined", r =>
            {
                r.Filenames["destroyed mesh"] = "r.mesh";
                r.Filenames["xml collision"] = "r.xml";
                r.Filenames["destroyed collision"] = "rd.xml";
                r.Ints["building floor"] = 2;
            }));
        var parts = WorldObjectLayout.Building(db, db.Find("b")!, "p", Vector3.Zero, Quaternion.Identity, new BuildingState(Destroyed: true));
        var byId = parts.ToDictionary(p => p.Source.StringId);
        Assert.False(byId["ground"].RenderHidden);
        Assert.True(byId["upper"].RenderHidden);
        Assert.Equal("u.xml", byId["upper"].CollisionPath(true));
        Assert.False(byId.ContainsKey("stairs"));
        Assert.True(byId["ruined"].BlankXmlCollision);
        Assert.Equal("rd.xml", byId["ruined"].CollisionPath(true));
    }
}
