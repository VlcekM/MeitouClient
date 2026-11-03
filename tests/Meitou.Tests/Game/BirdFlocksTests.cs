using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Game;
using Meitou.Rendering;

namespace Meitou.Tests.Game;

public sealed class BirdFlocksTests
{
    static BirdFlockData Kind(string id = "birds", int count = 6) => new()
    {
        Id = id, Mesh = "bird.mesh", SpawnCount = count, Speed = 500, Height = 800, ContourStrength = 1, Distance = 500000, Flocking = true, Circling = true,
        SpawnHeightMin = 0, SpawnHeightMax = 4000, Amounts = new Dictionary<string, int> { ["group"] = 3 },
    };

    [Fact]
    public void A_group_holds_as_many_flocks_of_a_kind_as_its_amount_and_a_group_without_the_kind_none()
    {
        var birds = new BirdFlocks([Kind()], (_, _) => 0);
        birds.Update(0.05f, Vector3.Zero, "other");
        Assert.Equal(0, birds.FlockCount);
        birds.Update(0.05f, Vector3.Zero, "group");
        Assert.Equal(3, birds.FlockCount);
        var pieces = new List<DebrisPiece>();
        birds.AddPieces(pieces);
        Assert.Equal(18, pieces.Count);
    }

    [Fact]
    public void Flocks_settle_to_their_cruising_height_over_the_ground_and_stay_within_the_kinds_radius()
    {
        var kind = Kind();
        var birds = new BirdFlocks([kind], (_, _) => 100, seed: 7);
        for (int i = 0; i < 4000; i++) birds.Update(0.05f, Vector3.Zero, "group");
        var pieces = new List<DebrisPiece>();
        birds.AddPieces(pieces);
        float radius = BirdFlocks.RadiusOf(kind);
        foreach (var p in pieces)
        {
            Assert.InRange(p.Position.Y, 100 + 800 - 80, 100 + 800 + 80);
            Assert.True(new Vector2(p.Position.X, p.Position.Z).Length() <= radius * 1.3f);
        }
    }
}
