using System.Numerics;
using Meitou.Rendering;
using Meitou.Rendering.Characters;

namespace Meitou.Tests.Characters;

public class CharacterWorldBoundsTests
{
    static CharacterInstance Actor() => new(1, null!, new(1, 500, 2), 0, []) { Scale = 3 };

    [Fact]
    public void Posed_head_bounds_are_already_world_space_and_keep_the_physical_head_in_the_view()
    {
        var supplied = new CharacterWorldBounds(new(20, 502, 17), 2);
        var actor = Actor() with { WorldBounds = supplied };
        var actual = CharacterRenderer.BoundsOf(in actor, height: 36, radius: 4, scale: 3);
        Assert.Equal(supplied.Centre, actual.Centre);
        Assert.Equal(2, actual.Radius);
        Assert.Equal((supplied.Centre, supplied.Radius), CharacterRenderer.LoadingBounds(in actor));
        Vector4[] aroundHead = [new(1,0,0,-17), new(-1,0,0,23), new(0,1,0,-499), new(0,-1,0,505), new(0,0,1,-14), new(0,0,-1,20)];
        Assert.True(FrustumTests.SphereVisible(aroundHead, actual.Centre, actual.Radius));
        var ordinary = Actor();
        var old = CharacterRenderer.BoundsOf(in ordinary, height: 36, radius: 4, scale: 3);
        Assert.False(FrustumTests.SphereVisible(aroundHead, old.Centre, old.Radius));
    }

    [Fact]
    public void Absent_or_default_invalid_bounds_preserve_native_height_scale_and_loading_position()
    {
        var actor = Actor();
        var expected = (new Vector3(1, 554, 2), 12f);
        Assert.Equal(expected, CharacterRenderer.BoundsOf(in actor, 36, 4, 3));
        Assert.Equal((actor.Position, 0f), CharacterRenderer.LoadingBounds(in actor));
        actor = actor with { WorldBounds = default(CharacterWorldBounds) };
        Assert.False(actor.WorldBounds!.Value.IsValid);
        Assert.Equal(expected, CharacterRenderer.BoundsOf(in actor, 36, 4, 3));
        Assert.Equal((actor.Position, 0f), CharacterRenderer.LoadingBounds(in actor));
    }

    [Fact]
    public void A_large_posed_actor_sphere_does_not_inflate_small_parts_or_move_their_bone_attachments()
    {
        var actor = Actor() with { WorldBounds = new CharacterWorldBounds(new(20, 502, 17), 80) };
        var actorBounds = CharacterRenderer.BoundsOf(in actor, height: 36, radius: 4, scale: 3);
        Assert.Equal(80, actorBounds.Radius);
        var part = new AssetPart
        {
            Label = "small part", Mesh = null!, Shared = true, Material = new(),
            BoundsCentre = new(0, 16, 0), BoundsRadius = 2, FileRadius = 3,
        };
        var model = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(1, 500, 2);
        var shared = CharacterRenderer.PartBounds(part, in model, 2, actorBounds.Centre, customBounds: true);
        Assert.Equal(actorBounds.Centre, shared.Centre);
        Assert.Equal(4, shared.Radius);
        Assert.Equal(6, shared.FileRadius);
        Assert.True(shared.Radius < 5); // A five-unit shadow texel can omit it despite the much larger actor sphere.
        var ordinary = CharacterRenderer.PartBounds(part, in model, 2, actorBounds.Centre, customBounds: false);
        Assert.Equal(new Vector3(1, 532, 2), ordinary.Centre);
        Assert.Equal(shared.Radius, ordinary.Radius);
        part.Shared = false;
        var attached = CharacterRenderer.PartBounds(part, in model, 2, actorBounds.Centre, customBounds: true);
        Assert.Equal(ordinary, attached);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Explicit_invalid_world_radii_are_rejected(float radius) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterWorldBounds(Vector3.Zero, radius));

    [Fact]
    public void World_centres_must_be_finite_and_a_positive_finite_sphere_is_valid()
    {
        foreach (var centre in new[] { new Vector3(float.NaN, 0, 0), new Vector3(0, float.PositiveInfinity, 0), new Vector3(0, 0, float.NegativeInfinity) })
            Assert.Throws<ArgumentOutOfRangeException>(() => new CharacterWorldBounds(centre, 1));
        Assert.False(default(CharacterWorldBounds).IsValid);
        Assert.True(new CharacterWorldBounds(new(-2000, 500, 9000), .01f).IsValid);
    }
}
