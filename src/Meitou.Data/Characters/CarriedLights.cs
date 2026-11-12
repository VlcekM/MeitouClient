using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Physics;
using Meitou.Data.World;

namespace Meitou.Data.Characters;

/// <summary>
/// A light a character carries (docs/formats/lights.md "Carried lanterns"): the bone it hangs from and the light at its binding-pose position
/// (model space).
/// </summary>
public sealed record CarriedLight(string Bone, WorldLight Light);

/// <summary>
/// The lights of worn records (docs/formats/lights.md "Carried lanterns"): a record's <c>physics attachment</c> (CHARACTER_PHYSICS_ATTACHMENT) names
/// a LIGHT in <c>light data</c>, a bone in <c>bone name</c> and a physics file whose bodies hang from that bone. The light sits at the hanging body
/// (engine choice, <b>Observed</b>: the body the original lights is chosen from a list whose filling is <b>Unknown</b>). Lit only at night, as every
/// such light is created with the night flag.
/// </summary>
public static class CarriedLights
{
    /// <summary>
    /// The hanging body of each physics file by path (ignoring case); null when it has none or does not read.
    /// </summary>
    static readonly ConcurrentDictionary<string, Vector3?> HangingCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The light <paramref name="worn"/> carries: its first <c>physics attachment</c>'s LIGHT at the attachment's hanging body (the female file when
    /// <paramref name="female"/> and set, else the male one), its power rolled with <paramref name="random"/>; null when any link is missing.
    /// </summary>
    public static CarriedLight? From(GameDatabase db, string installRoot, GameRecord worn, bool female, Random random)
    {
        if (worn.GetReferences("physics attachment") is not [var link, ..] || db.Find(link.TargetStringId) is not { } attachment)
            return null;
        if (attachment.GetReferences("light data") is not [var lightLink, ..]
                || db.Find(lightLink.TargetStringId) is not { Type: FcsRecordType.Light } light)
            return null;
        string file = attachment.GetPath(female ? "file female" : "file male");
        if (file.Length == 0) file = attachment.GetPath("file male");
        if (file.Length == 0 || Hanging(CharacterGenerator.Resolve(installRoot, file)) is not { } at) return null;

        // Power: brightness plus a roll in ±variance/2, as a building's lights (the original rolls when it creates the light).
        float power = light.GetFloat("brightness") + (random.NextSingle() - 0.5f) * light.GetFloat("variance");
        return new CarriedLight(attachment.GetString("bone name"),
            WorldLights.FromRecord(light, at, -Vector3.UnitY, power, WorldLightRule.Night, $"carried:{attachment.StringId}", worn.StringId));
    }

    /// <summary>
    /// The body that hangs from a spherical joint (below its anchor, not the <c>root</c> body) of the last such joint; null when the file has none or
    /// does not read.
    /// </summary>
    static Vector3? Hanging(string path) => HangingCache.GetOrAdd(path, p =>
    {
        try
        {
            var file = PhysicsFile.ReadFile(p);
            Vector3? found = null;
            foreach (var joint in file.Joints.Where(j => j.Type == PhysicsJointType.Spherical))
                foreach (int a in new[] { joint.Actor0, joint.Actor1 })
                    if (a >= 0 && a < file.Actors.Count && file.Actors[a] is { Name: not "root" } body && body.Position.Y < joint.Anchor.Y)
                        found = body.Position;
            return found;
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    });
}