using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// A character body file (<c>.bod2</c>, named by CHARACTER <c>body</c>): an FCS file (type 15 or 16, or a legacy 8 to 14) holding one
/// CHARACTER_APPEARANCE record (docs/characters.md). Floats are slider values (mostly 0–200, 100 = neutral) and pose
/// weights keyed by the body mesh's pose names; strings name the head, hair and beard records.
/// </summary>
public sealed class AppearanceFile
{
    /// <summary>
    /// The old appearance keys the game erases from a body it loads (docs/characters.md "Body files"), by value map.
    /// </summary>
    static readonly string[] ErasedFloats =
        ["a", "b", "c", "blue 1", "Breasts", "brightness 1", "contrast 1", "green 1", "Hair Color Blue", "Hair Color Green", "Hair Color Red",
            "Hair Contrast"];

    /// <summary>
    /// The old file keys the game erases from a loaded body.
    /// </summary>
    static readonly string[] ErasedFilenames = ["face", "hair", "skintone"];

    /// <summary>
    /// The old int keys the game erases from a loaded body.
    /// </summary>
    static readonly string[] ErasedInts = ["hair bright", "hair contrast", "hair saturation", "hair style"];

    /// <summary>
    /// The file's CHARACTER_APPEARANCE record (the first one).
    /// </summary>
    public required FcsRecord Record { get; init; }

    /// <summary>
    /// <c>sex female</c>.
    /// </summary>
    public bool Female => Record.Bools.TryGetValue("sex female", out var f) && f;

    /// <summary>
    /// String id of the RACE (reference list <c>race</c>), or null.
    /// </summary>
    public string? RaceId => Record.References.TryGetValue("race", out var refs) && refs.Count > 0 ? refs[0].TargetStringId : null;

    /// <summary>
    /// String id of the HEAD record, or null when empty.
    /// </summary>
    public string? HeadId => NonEmpty("head");

    /// <summary>
    /// String id of the hair ATTACHMENT, or null when empty.
    /// </summary>
    public string? HairId => NonEmpty("hair style");

    /// <summary>
    /// String id of the beard ATTACHMENT, or null when empty.
    /// </summary>
    public string? BeardId => NonEmpty("beard");

    /// <summary>
    /// The <c>idle stance</c> ANIMATION name, or null when empty.
    /// </summary>
    public string? IdleStance => NonEmpty("idle stance");

    /// <summary>
    /// The <c>Skin Tone</c> colour (RGB 0–1), or null if the file has none.
    /// </summary>
    public Vector3? SkinTone => Record.Vector3S.TryGetValue("Skin Tone", out var v) ? v : null;

    /// <summary>
    /// Every float of the record: slider values and pose weights by name.
    /// </summary>
    public IReadOnlyDictionary<string, float> Floats => Record.Floats;

    /// <summary>
    /// The float <paramref name="key"/>, or <paramref name="fallback"/> when the file has none.
    /// </summary>
    public float Get(string key, float fallback = 0) => Record.Floats.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>
    /// The string <paramref name="key"/>, or null when it is missing or empty.
    /// </summary>
    string? NonEmpty(string key) => Record.Strings.TryGetValue(key, out var s) && s.Length > 0 ? s : null;

    /// <summary>
    /// Reads the body file at <paramref name="path"/> (the current formats or a legacy one, 8 to 14) and drops the old keys the game erases; one
    /// with no CHARACTER_APPEARANCE record throws.
    /// </summary>
    public static AppearanceFile Read(string path)
    {
        var file = ReadFcs(path);
        var record = file.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.CharacterAppearance)
            ?? throw new FcsFormatException("Body file has no CHARACTER_APPEARANCE record.", 0);
        EraseOldKeys(record);
        return new AppearanceFile { Record = record };
    }

    /// <summary>
    /// The FCS file at <paramref name="path"/>: a legacy format (Crumblejon's <c>oldman.bod2</c> is format 13) by <see cref="FcsLegacyReader"/>,
    /// the others by <see cref="FcsReader"/>.
    /// </summary>
    static FcsFile ReadFcs(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int format = bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0;
        if (!FcsLegacyReader.IsLegacy(format)) return FcsReader.Read(new MemoryStream(bytes, writable: false));

        return FcsLegacyReader.Read(new MemoryStream(bytes, writable: false), format).Data;
    }

    /// <summary>
    /// Erases what the game's body load (<c>FUN_1400765e0</c>, after it lays the file over a default appearance) erases: the old hair colour,
    /// colour-correction and texture keys and <c>Nickname</c>.
    /// </summary>
    static void EraseOldKeys(FcsRecord record)
    {
        foreach (var key in ErasedFloats) record.Floats.Remove(key);
        foreach (var key in ErasedFilenames) record.Filenames.Remove(key);
        foreach (var key in ErasedInts) record.Ints.Remove(key);
        record.Strings.Remove("Nickname");
    }
}