using Meitou.Data.Fcs;

namespace Meitou.Data.Characters;

/// <summary>
/// The skeleton files a character's skeleton takes extra animations from (docs/animation.md, "Animation files"): the ANIMATION_FILE records of its
/// race's <c>animation files</c>, then of its own record's, latest-created first, each giving its <c>male animation</c> or <c>female animation</c>. A
/// clip is looked for in the body's own skeleton first, then in these in order; a file named twice is linked once.
/// </summary>
public static class AnimationFiles
{
    /// <summary>
    /// The files of a character of <paramref name="race"/> made from <paramref name="character"/> (its CHARACTER or ANIMAL_CHARACTER record, if any),
    /// in link order.
    /// </summary>
    public static IReadOnlyList<string> Of(GameDatabase db, GameRecord race, GameRecord? character, bool female) =>
        Linked(db, Records(db, race).Concat(character is null ? [] : Records(db, character)), female ? "female animation" : "male animation");

    /// <summary>
    /// Every ANIMATION_FILE's male and female file, in the order <see cref="Of"/> would link them: for the tables that know clips by name alone, a
    /// clip's first file here is the one a character linking every file would play.
    /// </summary>
    public static IReadOnlyList<string> All(GameDatabase db)
    {
        var records = db.OfType(FcsRecordType.AnimationFile).OrderBy(r => r.StringId, StringComparer.Ordinal).ToList();
        return Linked(db, records, "male animation").Concat(Linked(db, records, "female animation")).DistinctBy(BareName,
                StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The ANIMATION_FILE records <paramref name="owner"/>'s <c>animation files</c> list names, in list order (missing ones left out).
    /// </summary>
    static IEnumerable<GameRecord> Records(GameDatabase db, GameRecord owner) => owner.GetReferences("animation files")
        .Select(r => db.Find(r.TargetStringId)).OfType<GameRecord>().Where(r => r.Type == FcsRecordType.AnimationFile);

    /// <summary>
    /// The <paramref name="field"/> paths of <paramref name="records"/>, sorted by the load-order index of the file that created each record, highest
    /// first (the game's qsort on <c>GameData::createdIndex</c>); records of one file keep their list order (the game's qsort leaves ties in no
    /// defined order). The game links the bare file name, so one name links once.
    /// </summary>
    static IReadOnlyList<string> Linked(GameDatabase db, IEnumerable<GameRecord> records, string field) => records
        .OrderByDescending(r => db.Files.IndexOf(r.DefinedBy))
        .Select(r => r.GetPath(field)).Where(p => p.Length > 0)
        .DistinctBy(BareName, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The file name of an FCS path, whichever separator it uses.
    /// </summary>
    public static string BareName(string fcsPath) => Path.GetFileName(fcsPath.Replace('\\', '/'));
}