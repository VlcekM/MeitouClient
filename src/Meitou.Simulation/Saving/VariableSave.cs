using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;

namespace Meitou.Simulation.Saving;

/// <summary>
/// BFrizz's Extra Extensions variables in <c>quick.save</c> (docs/formats/save.md "Variables"), as the plugin keeps them: on save every VARIABLE record
/// of the game data is copied into <c>quick.save</c> under its own string id and name with its current <c>value</c>; on load (not on import) each such
/// record whose id the data has a VARIABLE of gives that variable its value.
/// </summary>
public static class VariableSave
{
    /// <summary>
    /// Drops the VARIABLE records the save being written was copied with: the original writes its save container afresh, so only
    /// <see cref="Write"/>'s are kept.
    /// </summary>
    public static void Strip(SaveGame game) => game.Quick.Records.RemoveAll(IsVariable);

    /// <summary>
    /// Copies every VARIABLE record of <paramref name="db"/> into <c>quick.save</c>, each under the file's next number, with its fields and the
    /// world's value.
    /// </summary>
    public static void Write(SaveGame game, World world, GameDatabase db)
    {
        int n = SaveBuilder.NextNumber(game.Quick);
        var variables = db.OfType((FcsRecordType)VariableData.RecordType)
            .OrderBy(r => r.StringId, StringComparer.Ordinal);
        foreach (var variable in variables)
        {
            var record = new FcsRecord { Type = VariableData.RecordType, Id = n, Name = variable.Name, StringId = variable.StringId };
            CopyFields(variable, record);
            if (record.Ints.ContainsKey("value")) record.Ints["value"] = world.Variables.Value(db, variable.StringId);
            game.Quick.Records.Add(record);
            game.Quick.Data.NextId = n;
            n++;
        }
    }

    /// <summary>
    /// Gives each variable the value its <c>quick.save</c> record holds; a record the data has no VARIABLE of, or one without a <c>value</c>, is
    /// skipped (the original's <c>updateData</c> finds the data's record by type and id).
    /// </summary>
    public static void Load(SaveGame save, World world, GameDatabase db)
    {
        foreach (var record in save.Quick.Records)
        {
            if (!IsVariable(record) || !record.Ints.TryGetValue("value", out int value)) continue;
            if (db.Find(record.StringId) is not { Type: (FcsRecordType)VariableData.RecordType }) continue;

            world.Variables.Set(record.StringId, value);
        }
    }

    /// <summary>
    /// Whether <paramref name="record"/> is a VARIABLE.
    /// </summary>
    public static bool IsVariable(FcsRecord record) => record.Type == VariableData.RecordType;

    /// <summary>
    /// The data record's fields into the save's copy (<c>GameData::updateFrom</c>).
    /// </summary>
    static void CopyFields(GameRecord from, FcsRecord to)
    {
        foreach (var (key, value) in from.Bools) to.Bools[key] = value;
        foreach (var (key, value) in from.Floats) to.Floats[key] = value;
        foreach (var (key, value) in from.Ints) to.Ints[key] = value;
        foreach (var (key, value) in from.Strings) to.Strings[key] = value;
    }
}
