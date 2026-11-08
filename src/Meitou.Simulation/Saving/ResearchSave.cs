using System.Globalization;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The player's research in <c>quick.save</c> (docs/formats/save.md "Research"; writer <c>FUN_1408310b0</c>, reader <c>FUN_1408360f0</c>): the RESEARCH record holds the floats <c>num finished</c>
/// and <c>num currents</c>, the strings <c>finished&lt;k&gt;</c> (finished ids in order), <c>current&lt;k&gt;</c> with the floats <c>current prog&lt;k&gt;</c> (the queue and its seconds) and
/// <c>paid&lt;k&gt;</c> (the ids already paid for). <b>Verified</b>: the five sample saves hold <c>num finished</c> 1, <c>finished0</c> <c>5359-gamedata.base</c> and <c>num currents</c> 0; the rest
/// <b>Observed</b> by code. The progress of research taken off the queue is not saved (as the original).
/// </summary>
public static class ResearchSave
{
    static string K(string prefix, int k) => prefix + k.ToString(CultureInfo.InvariantCulture);

    static List<string> List(FcsRecord? record, string prefix)
    {
        var ids = new List<string>();
        if (record is null) return ids;
        for (int k = 0; record.Strings.TryGetValue(K(prefix, k), out var id); k++) ids.Add(id);
        return ids;
    }

    /// <summary>The finished research ids a RESEARCH record lists (none for a null record).</summary>
    public static List<string> Read(FcsRecord? record) => List(record, "finished");

    /// <summary>Loads the record's research into the world.</summary>
    public static void Load(FcsRecord? record, ResearchState research, GameDatabase db)
    {
        var queued = new List<(string, float)>();
        int n = record is not null && record.Floats.TryGetValue("num currents", out float c) ? (int)c : 0;
        for (int k = 0; record is not null && k < n && record.Strings.TryGetValue(K("current", k), out var id); k++)
            queued.Add((id, record.Floats.GetValueOrDefault(K("current prog", k))));
        research.Set(db, Read(record), queued, List(record, "paid"), []);
    }

    static void Replace(FcsRecord record, string prefix, IReadOnlyList<string> ids)
    {
        for (int k = 0; record.Strings.ContainsKey(K(prefix, k)); k++) record.Strings.Remove(K(prefix, k));
        for (int k = 0; k < ids.Count; k++) record.Strings[K(prefix, k)] = ids[k];
    }

    /// <summary>Writes the research into the record, replacing what it listed; a record that already says the same is left as it is.</summary>
    public static void Write(FcsRecord? record, ResearchState research)
    {
        if (record is null) return;
        bool same = Read(record).SequenceEqual(research.Completed, StringComparer.Ordinal) && record.Floats.TryGetValue("num finished", out float n) && n == research.Completed.Count
            && research.Queue.Count == 0 && (int)record.Floats.GetValueOrDefault("num currents") == 0 && List(record, "paid").SequenceEqual(research.Paid, StringComparer.Ordinal);
        if (same) return;
        Replace(record, "finished", research.Completed);
        record.Floats["num finished"] = research.Completed.Count;
        for (int k = 0; record.Strings.ContainsKey(K("current", k)); k++)
        {
            record.Strings.Remove(K("current", k));
            record.Floats.Remove(K("current prog", k));
        }
        for (int k = 0; k < research.Queue.Count; k++)
        {
            record.Strings[K("current", k)] = research.Queue[k].Id;
            record.Floats[K("current prog", k)] = research.Queue[k].Seconds;
        }
        record.Floats["num currents"] = research.Queue.Count;
        Replace(record, "paid", research.Paid);
    }
}