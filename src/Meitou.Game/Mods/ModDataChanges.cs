using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Meitou.Data.Fcs;

namespace Meitou.Modding;

/// <summary>
/// Checks on a data phase's queued changes (docs/modding.md "Data phase") before any is merged, and the fingerprint of what they change for the
/// navmesh cache key (<see cref="Data.GameDatabase.CodeEdits"/>).
/// </summary>
static class ModDataChanges
{
    /// <summary>
    /// The first reason the merge could not take <paramref name="changes"/> whole (a missing id, a null name, value, reference target or instance),
    /// or null when every change can be merged. Checked before any is merged, so a mod's changes go in whole or not at all.
    /// </summary>
    public static string? Problem(IReadOnlyList<FcsRecord> changes)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            if (ProblemOf(changes[i]) is { } problem) return $"change {i + 1} ({changes[i].StringId ?? "no id"}): {problem}";
        }

        return null;
    }

    /// <summary>
    /// The first reason one record could not be merged, or null.
    /// </summary>
    static string? ProblemOf(FcsRecord r)
    {
        if (string.IsNullOrEmpty(r.StringId)) return "no string id";
        if (r.Name is null) return "no name";
        if (r.Strings.Any(v => v.Value is null)) return "a string field without a value";
        if (r.Filenames.Any(v => v.Value is null)) return "a file field without a value";

        foreach (var (list, refs) in r.References)
        {
            if (refs is null) return $"reference list '{list}' is null";
            if (refs.Any(x => string.IsNullOrEmpty(x.TargetStringId))) return $"reference list '{list}' has an entry without a target";
        }

        foreach (var instance in r.Instances)
        {
            if (instance is null || string.IsNullOrEmpty(instance.Id) || instance.Target is null) return "an instance without an id or target";
            if (instance.States.Any(s => s is null)) return $"instance '{instance.Id}' has a null state";
        }

        return null;
    }

    /// <summary>
    /// A copy of the queued changes (their tables, reference lists and instances), so what is checked is what is merged even if the mod still
    /// writes to the records it queued. A null list or instance is copied as null for <see cref="Problem"/> to report.
    /// </summary>
    public static List<FcsRecord> Snapshot(IReadOnlyList<FcsRecord> changes) => [.. changes.Select(Copy)];

    /// <summary>
    /// One record copied.
    /// </summary>
    static FcsRecord Copy(FcsRecord r)
    {
        var copy = new FcsRecord { Type = r.Type, Id = r.Id, Name = r.Name, StringId = r.StringId, Flags = r.Flags };
        foreach (var (k, v) in r.Bools) copy.Bools[k] = v;
        foreach (var (k, v) in r.Ints) copy.Ints[k] = v;
        foreach (var (k, v) in r.Floats) copy.Floats[k] = v;
        foreach (var (k, v) in r.Vector3S) copy.Vector3S[k] = v;
        foreach (var (k, v) in r.Vector4S) copy.Vector4S[k] = v;
        foreach (var (k, v) in r.Strings) copy.Strings[k] = v;
        foreach (var (k, v) in r.Filenames) copy.Filenames[k] = v;
        foreach (var (k, v) in r.References) copy.References[k] = v is null ? null! : [.. v];
        copy.Instances.AddRange(r.Instances.Select(CopyInstance));
        return copy;
    }

    /// <summary>
    /// One instance copied (null stays null).
    /// </summary>
    static FcsInstance CopyInstance(FcsInstance x)
    {
        if (x is null) return null!;

        var copy = new FcsInstance { Id = x.Id, Target = x.Target, Position = x.Position, Rotation = x.Rotation };
        copy.States.AddRange(x.States);
        return copy;
    }

    /// <summary>
    /// A text that changes whenever <paramref name="changes"/> would change the merged records, and is the same on every machine and in every
    /// language for the same changes: the mod's id and a SHA-256 of every change's id, type, flags, name (only when it defines or renames the
    /// record, so a translated name copied by <see cref="ModDataContext.Edit"/> does not count), values (floats by their bits), references and
    /// instances, in order. Numbers are written without culture and every text with its length, so no two change sets give one text.
    /// </summary>
    public static string Fingerprint(string modId, IReadOnlyList<FcsRecord> changes)
    {
        var text = new StringBuilder();
        foreach (var r in changes) Append(text, r);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return $"data phase {modId} {Convert.ToHexString(hash)}";
    }

    /// <summary>
    /// One record into the fingerprint's text.
    /// </summary>
    static void Append(StringBuilder text, FcsRecord r)
    {
        Text(text.Append('R'), r.StringId);
        Number(text, r.Type);
        Number(text, r.Flags);
        if (!r.IsModified || r.IsRenamed) Text(text.Append('N'), r.Name);
        foreach (var (k, v) in r.Bools) Number(Text(text.Append('b'), k), v ? 1 : 0);
        foreach (var (k, v) in r.Ints) Number(Text(text.Append('i'), k), v);
        foreach (var (k, v) in r.Floats) Text(Text(text.Append('f'), k), Bits(v));
        foreach (var (k, v) in r.Vector3S) Text(Text(text.Append('v'), k), Bits(v.X, v.Y, v.Z));
        foreach (var (k, v) in r.Vector4S) Text(Text(text.Append('w'), k), Bits(v.X, v.Y, v.Z, v.W));
        foreach (var (k, v) in r.Strings) Text(Text(text.Append('s'), k), v);
        foreach (var (k, v) in r.Filenames) Text(Text(text.Append('p'), k), v);
        AppendReferences(text, r);
        AppendInstances(text, r);
    }

    /// <summary>
    /// A record's references into the fingerprint's text.
    /// </summary>
    static void AppendReferences(StringBuilder text, FcsRecord r)
    {
        foreach (var (list, refs) in r.References)
        {
            foreach (var x in refs)
            {
                Text(Text(text.Append('r'), list), x.TargetStringId);
                Number(Number(Number(text, x.Value0), x.Value1), x.Value2);
            }
        }
    }

    /// <summary>
    /// A record's instances into the fingerprint's text.
    /// </summary>
    static void AppendInstances(StringBuilder text, FcsRecord r)
    {
        foreach (var x in r.Instances)
        {
            var (p, q) = (x.Position, x.Rotation);
            Text(Text(Text(text.Append('n'), x.Id), x.Target), Bits(p.X, p.Y, p.Z, q.X, q.Y, q.Z, q.W));
            Number(text, x.States.Count);
            foreach (var state in x.States) Text(text, state);
        }
    }

    /// <summary>
    /// A text with its length before it, so a value holding a separator cannot stand for two.
    /// </summary>
    static StringBuilder Text(StringBuilder text, string value) =>
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    /// <summary>
    /// A number without culture (a minus sign is always '-'), ended by ';'.
    /// </summary>
    static StringBuilder Number(StringBuilder text, long value) => text.Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');

    /// <summary>
    /// Floats by their bits, so the text is exact and the same on every system.
    /// </summary>
    static string Bits(params float[] values) =>
        string.Join(',', values.Select(v => BitConverter.SingleToInt32Bits(v).ToString("x8", CultureInfo.InvariantCulture)));
}
