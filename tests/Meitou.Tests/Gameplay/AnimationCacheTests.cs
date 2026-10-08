using Meitou.Data.Gameplay;

namespace Meitou.Tests.Gameplay;

public class AnimationCacheTests
{
    static AnimationDefinition Def(string name, AnimationArea area, float move = 0, bool idle = false, float lMin = 0, float lMax = 100, float rMin = 0, float rMax = 100, float min = 0, float max = 0) => new()
    {
        Id = name, Name = name, Clip = name, Area = area, MoveSpeed = move, MinSpeed = min, MaxSpeed = max, PlaySpeed = 0.05f, Idle = idle, IdleChance = 100, Chance = 100,
        WeaponLeft = 2, WeaponRight = 2, CombatMode = 2, StealthMode = 2, Kinds = WeaponKinds.All,
        LeftLegMin = lMin, LeftLegMax = lMax, RightLegMin = rMin, RightLegMax = rMax,
    };

    /// <summary>Healthy clips (both legs 0 to 100), a left limp (the left leg from -100 to 40, the right unused) and a right limp, as the base data lays them out.</summary>
    static AnimationLibrary Library() => new(
    [
        Def("stand", AnimationArea.All, idle: true),
        Def("limp stand L", AnimationArea.All, idle: true, lMin: -100, lMax: 40, rMin: 1000, rMax: 1000),
        Def("limp stand R", AnimationArea.All, idle: true, lMin: 1000, lMax: 1000, rMin: -100, rMax: 40),
        Def("walk lower", AnimationArea.Lower, 14, min: -50, max: 40), Def("jog lower", AnimationArea.Lower, 45, min: 18, max: 74), Def("run lower", AnimationArea.Lower, 90, min: 45, max: 99999),
        Def("limp lower L", AnimationArea.Lower, 14, min: -50, max: 40, lMin: -100, lMax: 40, rMin: 1000, rMax: 1000),
        Def("limp lower R", AnimationArea.Lower, 14, min: -50, max: 40, lMin: 1000, lMax: 1000, rMin: -100, rMax: 40),
    ]);

    [Fact]
    public void A_leg_that_heals_or_rots_picks_the_clips_its_value_fits_and_leaves_the_caches_small()
    {
        var lib = Library();
        var values = new List<float>();
        for (float h = -150; h <= 150; h += 0.25f) values.Add(h);
        foreach (float b in new[] { -100f, 0f, 40f, 100f, 1000f })
            values.AddRange([MathF.BitDecrement(b), b, MathF.BitIncrement(b)]);
        foreach (float left in values)
            foreach (float right in new[] { 100f, 40f, 39.9f, -100.5f })
            {
                var stance = new AnimationStance { LeftLeg = left, RightLeg = right };
                var wantIdles = Enumerable.Range(0, lib.Definitions.Count)
                    .Where(i => lib.Definitions[i].Idle && lib.Definitions[i].Area == AnimationArea.All && lib.Definitions[i].Fits(stance)).ToArray();
                Assert.Equal(wantIdles, lib.Idles(stance).Select(p => p.Index).ToArray());

                // Movement weighs the legs per call: every lower clip whose speed and leg weights are positive, normalised.
                var weights = Enumerable.Range(0, lib.Definitions.Count)
                    .Where(i => lib.Definitions[i].Area == AnimationArea.Lower && lib.Definitions[i].MoveSpeed > 0)
                    .Select(i => (i, w: lib.Definitions[i].SpeedWeight(14) * lib.Definitions[i].LegWeight(left, right))).Where(p => p.w > 0).ToList();
                var into = new List<(int Index, float Weight)>();
                lib.Movement(AnimationArea.Lower, 14, stance, into);
                Assert.Equal(weights.Select(p => p.i).Order().ToArray(), into.Select(p => p.Index).Order().ToArray());
                float total = weights.Sum(p => p.w);
                foreach (var (i, w) in weights) Assert.Equal(w / total, into.Single(p => p.Index == i).Weight, 4);
            }
        // Five bounds cut a leg into 11 intervals (below, on and between): at most 11 x 11 idle stances; the movement candidates ignore the legs.
        Assert.True(lib.CachedChoices <= 11 * 11 + 1, $"{lib.CachedChoices} cache entries for {values.Count * 4} leg values");
    }
}
