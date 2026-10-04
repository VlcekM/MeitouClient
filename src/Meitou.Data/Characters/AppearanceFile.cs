using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// A character body file (<c>.bod2</c>, named by CHARACTER <c>body</c>): an FCS file (type 15 or 16) holding one
/// CHARACTER_APPEARANCE record (docs/characters.md). Floats are slider values (mostly 0–200, 100 = neutral) and pose
/// weights keyed by the body mesh's pose names; strings name the head, hair and beard records.
/// </summary>
public sealed class AppearanceFile
{
    public required FcsRecord Record { get; init; }

    public bool Female => Record.Bools.TryGetValue("sex female", out var f) && f;

    /// <summary>String id of the RACE (reference list <c>race</c>), or null.</summary>
    public string? RaceId => Record.References.TryGetValue("race", out var refs) && refs.Count > 0 ? refs[0].TargetStringId : null;

    /// <summary>String id of the HEAD record, or null when empty.</summary>
    public string? HeadId => NonEmpty("head");
    /// <summary>String id of the hair ATTACHMENT, or null when empty.</summary>
    public string? HairId => NonEmpty("hair style");
    /// <summary>String id of the beard ATTACHMENT, or null when empty.</summary>
    public string? BeardId => NonEmpty("beard");
    public string? IdleStance => NonEmpty("idle stance");

    /// <summary>The <c>Skin Tone</c> colour (RGB 0–1), or null if the file has none.</summary>
    public Vector3? SkinTone => Record.Vector3s.TryGetValue("Skin Tone", out var v) ? v : null;

    public IReadOnlyDictionary<string, float> Floats => Record.Floats;

    public float Get(string key, float fallback = 0) => Record.Floats.TryGetValue(key, out var v) ? v : fallback;

    string? NonEmpty(string key) => Record.Strings.TryGetValue(key, out var s) && s.Length > 0 ? s : null;

    public static AppearanceFile Read(string path)
    {
        var file = FcsReader.ReadFile(path);
        var record = file.Records.FirstOrDefault(r => r.RecordType == FcsRecordType.CHARACTER_APPEARANCE)
            ?? throw new FcsFormatException("Body file has no CHARACTER_APPEARANCE record.", 0);
        return new AppearanceFile { Record = record };
    }
}
