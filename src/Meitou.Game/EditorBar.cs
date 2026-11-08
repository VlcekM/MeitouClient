using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The character editor's bar set (the <c>editorbar</c> switch, docs/characters.md "Character editor", Meitou): the walls and furniture of a Storm House with its <c>Bar</c> interior,
/// laid out from the records alone and placed round the edited character in the stage picture, where the platform otherwise stands. Nothing is read from the world's towns, so the
/// world (a save, a world mod) has no say in it.
/// </summary>
static class EditorBar
{
    /// <summary>The base data's Storm House.</summary>
    const string BuildingId = "3384-TwoStorey.mod", PlacementKey = "meitou:editor-bar", LayoutName = "Bar";
    /// <summary>Where the edited character stands in the building's frame (x across, z along, y up; the floor is 4.4 up): on the open floor between the counter and the tables.</summary>
    static readonly Vector3 StandLocal = new(12, 4.4f, -14);
    /// <summary>The building's +x, the way the character faces, turned onto the stage frame's +z (the character's front).</summary>
    static readonly Matrix4x4 FacingToStage = Matrix4x4.CreateRotationY(-MathF.PI / 2);

    /// <summary>The set's meshes in the stage's frame round <paramref name="feet"/> (the stage's transform of the character); null when the data has no such building or layout.</summary>
    public static IReadOnlyList<PlacedMesh>? Set(WorldObjects world, Matrix4x4 feet)
    {
        if (world.Database.Find(BuildingId) is not { Type: FcsRecordType.BUILDING } record || world.Layouts.Find(record, LayoutName, exterior: false) is null) return null;
        // Any place but the zero one (which means unplaced); the parts are moved off it below, and it seeds the choice among a building's part variants.
        var at = new Vector3(1, 0, 0);
        var placement = new BuildingPlacement(WorldLayout.ZoneOf(at.X, at.Z), PlacementKey, record.StringId, at, Quaternion.Identity, null, at.Y)
        {
            Key = PlacementKey, InteriorLayout = LayoutName, ExteriorLayout = "",
        };
        var toStage = Matrix4x4.CreateTranslation(-(at + StandLocal)) * FacingToStage * feet;
        return [.. world.BuildRuntime(placement).Items.Select(p => p with { Transform = p.Transform * toStage })];
    }
}
