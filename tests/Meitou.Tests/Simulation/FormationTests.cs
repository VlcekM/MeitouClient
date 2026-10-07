using System.Numerics;
using Meitou.Simulation;

namespace Meitou.Tests.Simulation;

public class FormationTests
{
    [Fact]
    public void One_character_goes_to_the_point_and_a_group_is_a_centred_block_with_spaced_slots()
    {
        var target = new Vector2(1000, 500);
        Assert.Equal([target], Formation.Place([new Vector2(0, 0)], target));
        var from = Enumerable.Range(0, 7).Select(i => new Vector2(i * 5, 0)).ToArray();
        var slots = Formation.Place(from, target);
        Assert.Equal(7, slots.Length);
        // Centred on the click, neighbours at least the spacing apart, nobody far from the click.
        Assert.InRange(Vector2.Distance(slots.Aggregate(Vector2.Zero, (a, b) => a + b) / 7, target), 0, Formation.Spacing);
        for (int i = 0; i < slots.Length; i++)
        {
            Assert.InRange(Vector2.Distance(slots[i], target), 0, Formation.Spacing * 2.5f);
            for (int j = i + 1; j < slots.Length; j++) Assert.True(Vector2.Distance(slots[i], slots[j]) >= Formation.Spacing - 0.01f);
        }
    }

    [Fact]
    public void Characters_keep_their_left_to_right_order_and_the_ones_nearest_the_target_take_the_front_row()
    {
        // Heading +Z (north on the map, towards the click): a character on the +X side stays on that side, the one further north leads.
        var target = new Vector2(0, 1000);
        var positions = new[] { new Vector2(-20, 0), new Vector2(20, 0), new Vector2(0, 30), new Vector2(0, -30) };
        var slots = Formation.Place(positions, target);
        float Along(Vector2 p) => p.Y;
        Assert.True(Along(slots[2]) >= Along(slots[0]) && Along(slots[2]) >= Along(slots[1]) && Along(slots[2]) > Along(slots[3]), "the most advanced character is in the front row");
        Assert.True(Along(slots[3]) <= Along(slots[0]) && Along(slots[3]) <= Along(slots[1]), "the one furthest back is in the back row");
        // Within a row the order across the line of travel is kept, so paths do not cross.
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                if (i != j && MathF.Abs(slots[i].Y - slots[j].Y) < 0.01f && positions[i].X != positions[j].X)
                    Assert.Equal(MathF.Sign(positions[i].X - positions[j].X), MathF.Sign(slots[i].X - slots[j].X));
    }

    [Fact]
    public void The_result_does_not_depend_on_anything_but_the_input()
    {
        var from = Enumerable.Range(0, 12).Select(i => new Vector2(i % 4 * 6, i / 4 * 6)).ToArray();
        Assert.Equal(Formation.Place(from, new(900, -300)), Formation.Place(from, new(900, -300)));
    }
}
