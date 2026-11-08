using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Objects;
using Meitou.Simulation;
using Meitou.Simulation.Items;

namespace Meitou.Game;

/// <summary>
/// Severed limbs on the ground, falling and at rest (docs/game/blood-decals.md "On the body and on severed limbs"): drawn by the debris renderer with the original's SeveredLimb look
/// (its material's textures, the skin-tone mask, blood with the cut end solid), not as a world object.
/// </summary>
sealed partial class GameHost
{
    /// <summary>A severed limb at rest: its mesh and look, its rotation and where its origin is.</summary>
    sealed record RestingLimb(string Mesh, LimbLook Look, Quaternion Rotation, Vector3 Origin);

    readonly Dictionary<int, RestingLimb> restLimbs = [];
    Dictionary<string, GameRecord>? limbRaces;
    readonly Dictionary<string, LimbLook?> limbLooks = new(StringComparer.Ordinal);

    /// <summary>Sets a landed limb down where the object path would have put it, drawn as debris with the severed limb's look.</summary>
    void RestLimb(GroundItemSnapshot g, Quaternion rotation, float y)
    {
        if (scene.Database?.Find(g.Record) is not { } item || WorldObjectLayout.ItemMesh(item) is not { Length: > 0 } mesh || LimbLookOf(g.Record) is not { } look) { ShowGround(g, rotation, y); return; }
        restLimbs[g.Id] = new RestingLimb(mesh, ToneOf(look, g)!, rotation, new Vector3(g.Position.X, y, g.Position.Z));
    }

    /// <summary>
    /// The look of a ground item that is a race's <c>severed limbs</c> entry (null for any other item): its MATERIAL record's texture map and colour map (the skin-tone mask), the race's blood
    /// colour and its normal map. The skin tone comes with the cut (<see cref="ToneOf"/>).
    /// </summary>
    LimbLook? LimbLookOf(string record)
    {
        if (limbLooks.TryGetValue(record, out var known)) return known;
        LimbLook? look = null;
        if (scene.Database is { } db)
        {
            if (limbRaces is null)
            {
                limbRaces = new(StringComparer.Ordinal);
                foreach (var race in db.Records.Values)
                    if (race.Type == FcsRecordType.RACE)
                        foreach (var limb in race.GetReferences("severed limbs")) limbRaces.TryAdd(limb.TargetStringId, race);
            }
            if (limbRaces.TryGetValue(record, out var owner) && db.Find(record) is { } item)
            {
                var spec = item.GetReferences("material").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(r => r is not null);
                string? Name(string key) => spec?.GetPath(key) is { Length: > 0 } p ? Path.GetFileName(p.Replace('\\', '/')) : null;
                if (Name("texture map") is { } diffuse)
                    look = new LimbLook(diffuse, Name("color map"), Vector3.Zero, CharacterAppearance.Rgb(owner.GetInt("blood colour", 0x800000) & 0xFFFFFF), owner.StringId, Meitou.Game.Rendering.Characters.CharacterRenderer.SkinByDefault(owner), Name("normal map"));
            }
        }
        return limbLooks[record] = look;
    }

    /// <summary>The look with the skin tone of the character the limb was cut from (<see cref="FallSource.SkinTone"/>; none for a limb read from a save).</summary>
    static LimbLook? ToneOf(LimbLook? look, GroundItemSnapshot g) => look is null ? null : look with { SkinTone = g.Fall?.SkinTone ?? default };

    readonly Dictionary<(string Record, Vector3 Tone), MouthMesh?> mouthMeshes = [];

    /// <summary>
    /// The limb an animal holds in its mouth (<see cref="CharacterSnapshot.Mouth"/>), drawn at its <c>mouth</c> point by the character renderer: the item's <c>mesh</c> (the original attaches
    /// that one, <c>FUN_140537690</c>) with the severed limb's look and the cut character's skin tone. Null for none, or an item without a mesh.
    /// </summary>
    MouthMesh? MouthOf(CharacterSnapshot c)
    {
        if (c.Mouth is not { } record) return null;
        if (mouthMeshes.TryGetValue((record, c.MouthTone), out var known)) return known;
        MouthMesh? made = null;
        if (scene.Database?.Find(record) is { } item && item.GetPath("mesh") is { Length: > 0 } mesh)
            made = new MouthMesh(mesh, LimbLookOf(record) is { } look ? look with { SkinTone = c.MouthTone } : null);
        return mouthMeshes[(record, c.MouthTone)] = made;
    }

    /// <summary>The position of a limb's piece: its mesh's origin (a limb keeps the mesh's own space, <see cref="DebrisRenderer"/>; the other pieces are centred on their bounds).</summary>
    static Vector3 LimbMiddle(string mesh, LimbLook? look, Quaternion rotation, Vector3 origin) => origin;

    void AddRestingLimbs()
    {
        foreach (var (id, r) in restLimbs)
            if (!hiddenGround.Contains(id) && gpu.Debris?.Extent(r.Mesh, null, r.Look) is not null)
                gpu.DebrisPieces.Add(new DebrisPiece(r.Mesh, 1, r.Rotation, LimbMiddle(r.Mesh, r.Look, r.Rotation, r.Origin), 1, r.Look));
    }
}
