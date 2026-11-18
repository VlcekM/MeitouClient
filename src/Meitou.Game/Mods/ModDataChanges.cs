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
    /// A text that changes whenever <paramref name="changes"/> would change the merged records: the mod's id and a SHA-256 of every change's id,
    /// type, flags, name, values (floats by their bits), references and instances, in order.
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
        text.Append('R').Append(r.StringId).Append('|').Append(r.Type).Append('|').Append(r.Flags).Append('|').Append(r.Name).Append('\n');
        foreach (var (k, v) in r.Bools) text.Append("b ").Append(k).Append('=').Append(v).Append('\n');
        foreach (var (k, v) in r.Ints) text.Append("i ").Append(k).Append('=').Append(v).Append('\n');
        foreach (var (k, v) in r.Floats) text.Append("f ").Append(k).Append('=').Append(Bits(v)).Append('\n');
        foreach (var (k, v) in r.Vector3S) text.Append("v ").Append(k).Append('=').Append(Bits(v.X, v.Y, v.Z)).Append('\n');
        foreach (var (k, v) in r.Vector4S) text.Append("w ").Append(k).Append('=').Append(Bits(v.X, v.Y, v.Z, v.W)).Append('\n');
        foreach (var (k, v) in r.Strings) text.Append("s ").Append(k).Append('=').Append(v).Append('\n');
        foreach (var (k, v) in r.Filenames) text.Append("p ").Append(k).Append('=').Append(v).Append('\n');
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
                text.Append("r ").Append(list).Append('=').Append(x.TargetStringId);
                text.Append(',').Append(x.Value0).Append(',').Append(x.Value1).Append(',').Append(x.Value2).Append('\n');
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
            text.Append("n ").Append(x.Id).Append('=').Append(x.Target).Append(',').Append(Bits(p.X, p.Y, p.Z)).Append(',')
                .Append(Bits(q.X, q.Y, q.Z, q.W)).Append(',').Append(string.Join(';', x.States)).Append('\n');
        }
    }

    /// <summary>
    /// Floats by their bits, so the text is exact and the same on every system.
    /// </summary>
    static string Bits(params float[] values) =>
        string.Join(',', values.Select(v => BitConverter.SingleToInt32Bits(v).ToString("x8", CultureInfo.InvariantCulture)));
}
