using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Game;
using Meitou.Game.Effects;

namespace Meitou.Tests.Game;

/// <summary>
/// The birds' rules of the original's <c>BirdManager</c> (docs/game/birds.md): spawning against the amounts in range,
/// the return to the kind's cells, the attractors.
/// </summary>
public sealed class BirdFlocksTests
{
    static readonly Vector3 Away = new(-3000, 0, -3000);

    static BirdFlockData Kind(int count = 6, int amount = 6) => new()
    {
        Id = "birds", Mesh = "bird.mesh", SpawnCount = count, Speed = 300, Height = 800, ContourStrength = 1, Distance = 500000,
        Flocking = true, SpawnHeightMin = 0, SpawnHeightMax = 4000, Amounts = new Dictionary<string, int> { ["g"] = amount },
    };

    /// <summary>
    /// One weather cell (32, 32: x and z from 0 to 4608) lists the kind.
    /// </summary>
    static string? OneCell(int cx, int cz) => cx == 32 && cz == 32 ? "g" : null;

    static void Run(BirdFlocks birds, float seconds, Action? each = null)
    {
        for (float t = 0; t < seconds; t += 0.05f)
        {
            birds.Update(0.05f, Away, Away, null);
            each?.Invoke();
        }
    }

    [Fact]
    public void Flocks_spawn_while_the_birds_alive_plus_the_spawn_count_fit_the_amounts_of_the_cells_in_range()
    {
        var few = new BirdFlocks([Kind(count: 6, amount: 5)], (_, _) => 100, OneCell);
        Run(few, 10);
        Assert.Equal(0, few.BirdCount);
        var fits = new BirdFlocks([Kind(count: 6, amount: 11)], (_, _) => 100, OneCell);
        Run(fits, 10);
        Assert.Equal(6, fits.BirdCount);
        Run(fits, 10);
        Assert.Equal(6, fits.BirdCount);   // 6 alive + 6 more is over 11
    }

    [Fact]
    public void Birds_fly_to_an_attractor_up_to_its_maximum_and_leave_it_when_it_goes()
    {
        var birds = new BirdFlocks([Kind(count: 6, amount: 12)], (_, _) => 100, OneCell, seed: 5);
        var attractor = new BirdAttractor("hut", "birds", new Vector3(2000, 100, 2000), 1000, 2);
        birds.SetAttractors([attractor]);
        int most = 0;
        Run(birds, 200, () => most = Math.Max(most, birds.CountIn(BirdFlocks.BirdState.Attracted)));
        Assert.Equal(2, most);
        birds.SetAttractors([]);
        Assert.Equal(0, birds.CountIn(BirdFlocks.BirdState.Attracted));
    }
}
