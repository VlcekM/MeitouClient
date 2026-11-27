using System.Collections.Concurrent;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// Optional load-time armour clipping fixes (Fixes page, off by default): fits female-worn male armour meshes (<c>mesh female == mesh</c>) clear of
/// the female body, and tucks bulky leg armour under long coats when no <c>overlap mesh</c> is supplied.
/// </summary>
public static class ArmourClipFix
{
    #region State

    /// <summary>
    /// Cached parsed reference meshes (bodies and outer coats) so each reference file is read from disk once.
    /// </summary>
    static readonly ConcurrentDictionary<string, OgreMesh?> Refs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether load-time armour clipping fixes are active (read from <see cref="Host.UserConfig.FixArmourClipping"/>; off by default).
    /// </summary>
    public static bool Enabled { get; set; }

    #endregion

    #region Spec and matching

    /// <summary>
    /// One procedural fit to run on an armour mesh: <see cref="PushOut"/> pushes a female-worn male mesh clear of <see cref="BodyPath"/>;
    /// <see cref="CoatPath"/> tucks leg armour under the outer coat while keeping clearance from <see cref="BodyPath"/>.
    /// </summary>
    internal sealed record Spec(string BodyPath, bool PushOut, string? CoatPath);

    /// <summary>
    /// Returns the procedural fit to apply to <paramref name="part"/> on <paramref name="c"/>, or null when <see cref="Enabled"/> is off, the
    /// character is an animal, or the part needs no fix (e.g. a mod already supplied a distinct female mesh or overlap mesh).
    /// </summary>
    internal static Spec? ForPart(CharacterAppearance c, CharacterPart part, Func<string, string?> findMesh)
    {
        if (!Enabled || c.Animal is not null || part.Mode != AttachMode.SharedSkeleton || part.Record.Type != FcsRecordType.Armour)
            return null;

        var bodyPath = findMesh(c.BodyMesh);
        if (bodyPath is null) return null;

        // 1. Female slot pointing straight at the male mesh file: push penetrating vertices clear of the female body.
        bool pushOut = false;
        if (c.Female && part.Field == "mesh female")
        {
            string male = part.Record.GetPath("mesh");
            string female = part.Record.GetPath("mesh female");
            pushOut = male.Length > 0 && string.Equals(BareName(male), BareName(female), StringComparison.OrdinalIgnoreCase);
        }

        // 2. Leg armour with no overlap mesh worn under a body-slot coat: collapse poking outer plates and tuck remaining cloth.
        string? coatPath = null;
        if (part.Slot == AttachSlot.Legs && part.Field is "mesh" or "mesh female")
        {
            foreach (var other in c.Parts)
            {
                if (other.Mode != AttachMode.SharedSkeleton || other.Slot != AttachSlot.Body || other.Record.Type != FcsRecordType.Armour)
                    continue;
                var found = findMesh(other.Mesh);
                if (found is not null)
                {
                    coatPath = found.ToLowerInvariant();
                    break;
                }
            }
        }

        return pushOut || coatPath is not null ? new Spec(bodyPath.ToLowerInvariant(), pushOut, coatPath) : null;
    }

    /// <summary>
    /// Applies <paramref name="spec"/> to <paramref name="mesh"/> in place before welding and GPU upload.
    /// </summary>
    internal static void Apply(OgreMesh mesh, Spec spec)
    {
        var body = ReferenceMesh(spec.BodyPath);
        if (body is null) return;

        if (spec.PushOut) OgreMeshArmourFit.PushOutFromBody(mesh, body);

        if (spec.CoatPath is not null && ReferenceMesh(spec.CoatPath) is { } coat)
            OgreMeshArmourFit.TuckUnderCoat(mesh, coat, body);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Bare file name of a stored FCS mesh path (Kenshi resolves meshes by file name).
    /// </summary>
    static string BareName(string fcsPath) => Path.GetFileName(fcsPath.Replace('\\', '/'));

    /// <summary>
    /// Reads and caches a reference body or coat mesh; null if unreadable.
    /// </summary>
    static OgreMesh? ReferenceMesh(string path) => Refs.GetOrAdd(path, static p =>
    {
        try
        {
            return OgreMeshReader.ReadFile(p);
        }
        catch (Exception e) when (e is OgreFormatException or IOException or EndOfStreamException or InvalidDataException)
        {
            return null;
        }
    });

    #endregion
}
