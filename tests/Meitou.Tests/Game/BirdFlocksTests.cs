using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Game;
using Meitou.Rendering;

namespace Meitou.Tests.Game;

/// <summary>The birds' rules of the original's <c>BirdManager</c> (docs/game/birds.md): spawning against the amounts in range, the return to the kind's cells, the attractors.</summary>
public sealed class BirdFlocksTests
{
    static readonly Vector3 Away = new(-3000, 0, -3000);

    static BirdFlockData Kind(int count = 6, int amount = 6) => new()
    {
        Id = "birds", Mesh = "bird.mesh", SpawnCount = count, Speed = 300, Height = 800, ContourStrength = 1, Distance = 500000, Flocking = true,
        SpawnHeightMin = 0, SpawnHeightMax = 4000, Amounts = new Dictionary<string, int> { ["g"] = amount },
    };

    /// <summary>One weather cell (32, 32: x and z from 0 to 4608) lists the kind.</summary>
    static string? OneCell(int cx, int cz) => cx == 32 && cz == 32 ? "g" : null;

    static void Run(BirdFlocks birds, float seconds, Action? each = null)
    {
        for (float t = 0; t < seconds; t += 0.05f) { birds.Update(0.05f, Away, Away, null); each?.Invoke(); }
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
    public void A_flock_spawns_only_between_the_kinds_spawn_heights_and_far_from_the_camera_focus()
    {
        var low = new BirdFlocks([Kind(amount: 12)], (_, _) => 5000, OneCell);
        Run(low, 10);
        Assert.Equal(0, low.BirdCount);
        var near = new BirdFlocks([Kind(amount: 12)], (_, _) => 100, OneCell);
        for (int i = 0; i < 100; i++) near.Update(0.05f, new Vector3(2300, 0, 2300), new Vector3(2300, 0, 2300), null);
        Assert.Equal(12, near.BirdCount);   // a spawn point within 1000 of the focus is refused, the next update tries again
    }

    [Fact]
    public void A_bird_over_a_cell_the_kind_is_not_listed_in_turns_back_and_the_heading_turns_at_most_about_a_radian_a_second()
    {
        var birds = new BirdFlocks([Kind(amount: 6)], (_, _) => 100, OneCell, seed: 3);
        bool returned = false;
        Vector3 last = default;
        float worst = 0;
        Run(birds, 400, () =>
        {
            returned |= birds.CountIn(BirdFlocks.BirdState.Return) > 0;
            var pieces = new List<DebrisPiece>();
            birds.AddPieces(pieces);
            if (pieces.Count == 0) return;
            var forward = Vector3.Transform(Vector3.UnitZ, pieces[0].Rotation);
            if (last != default) worst = MathF.Max(worst, MathF.Acos(Math.Clamp(Vector3.Dot(last, forward), -1, 1)));
            last = forward;
        });
        Assert.True(returned);
        Assert.True(worst < 0.2f, $"{worst} rad in one 0.05 s step");
        var all = new List<DebrisPiece>();
        birds.AddPieces(all);
        Assert.All(all, p => Assert.InRange(p.Position.X, -3000, 8000));
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
