using System.Text.Json;

namespace Meitou.Data.Save;

/// <summary>
/// The lasting ids code mods gave characters (Meitou-only, docs/formats/save.md "Character ids"), kept in <see cref="SaveFolder.LastingIdsName"/>
/// beside the original's files, which are not touched: UTF-8 JSON <c>{ "format": 1, "prefix": p, "next": n, "characters": [ { "hand": [type, C, CS,
/// I, S], "id", "record" } ] }</c>, each entry keyed by the character's save handle as written by the same save, with the record the character was
/// made from. A missing file reads as no ids; one that is not JSON of this format reads as unreadable.
/// </summary>
public static class SaveLastingIds
{
    /// <summary>
    /// The file format this code writes and reads; any other reads as unreadable.
    /// </summary>
    const int Format = 1;

    /// <summary>
    /// One character's id: its save handle, the id (never 0) and the string id of the record the character was made from.
    /// </summary>
    public readonly record struct Entry(Hand Hand, ulong Id, string Record);

    /// <summary>
    /// What a file holds: the entries, the high half of the ids the game gives (0: none chosen) and the counter their low half is made from.
    /// </summary>
    public readonly record struct Contents(List<Entry> Entries, uint Prefix, ulong Next);

    #region Writing

    /// <summary>
    /// The file for <paramref name="entries"/>, the ids' high half <paramref name="prefix"/> and the counter <paramref name="next"/>.
    /// </summary>
    public static byte[] Write(IEnumerable<Entry> entries, uint prefix, ulong next)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Format);
            w.WriteNumber("prefix", prefix);
            w.WriteNumber("next", next);
            w.WriteStartArray("characters");
            foreach (var e in entries) WriteEntry(w, e);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// One entry's object.
    /// </summary>
    static void WriteEntry(Utf8JsonWriter w, in Entry e)
    {
        w.WriteStartObject();
        w.WriteStartArray("hand");
        foreach (int v in (ReadOnlySpan<int>)[e.Hand.Type, e.Hand.C, e.Hand.Cs, e.Hand.I, e.Hand.S]) w.WriteNumberValue(v);
        w.WriteEndArray();
        w.WriteNumber("id", e.Id);
        w.WriteString("record", e.Record);
        w.WriteEndObject();
    }

    #endregion

    #region Reading

    /// <summary>
    /// The contents of <paramref name="json"/>: empty for no file, null for one that is not JSON of this format (a damaged file never fails a load).
    /// Entries are taken one by one: one that is malformed (not an object, a handle that is not five numbers, no id or an id of 0, no record) or that
    /// repeats an earlier entry's handle or id is dropped, and the rest are kept.
    /// </summary>
    public static Contents? Read(byte[]? json)
    {
        if (json is null) return new Contents([], 0, 0);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || Number(format) is not Format
                || !root.TryGetProperty("characters", out var characters) || characters.ValueKind != JsonValueKind.Array)
                return null;

            uint prefix = root.TryGetProperty("prefix", out var pf) && pf.ValueKind == JsonValueKind.Number && pf.TryGetUInt32(out uint p) ? p : 0;
            ulong next = root.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetUInt64(out ulong v) ? v : 0;
            return new Contents(Entries(characters), prefix, next);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The value of a whole number that fits an int; null for anything else.
    /// </summary>
    static int? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) ? v : null;

    /// <summary>
    /// The well-formed entries of the array, each handle and id once.
    /// </summary>
    static List<Entry> Entries(JsonElement characters)
    {
        var entries = new List<Entry>();
        var hands = new HashSet<Hand>();
        var ids = new HashSet<ulong>();
        foreach (var item in characters.EnumerateArray())
        {
            if (ReadEntry(item) is not { } entry || hands.Contains(entry.Hand) || ids.Contains(entry.Id)) continue;

            hands.Add(entry.Hand);
            ids.Add(entry.Id);
            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// One entry of the file; null for a malformed one.
    /// </summary>
    static Entry? ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("hand", out var h) || h.ValueKind != JsonValueKind.Array || h.GetArrayLength() != 5
            || !item.TryGetProperty("id", out var i) || i.ValueKind != JsonValueKind.Number || !i.TryGetUInt64(out ulong id) || id == 0
            || !item.TryGetProperty("record", out var r) || r.ValueKind != JsonValueKind.String)
            return null;

        var parts = new int[5];
        int k = 0;
        foreach (var part in h.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetInt32(out parts[k++])) return null;
        }

        return new Entry(new Hand(parts[0], parts[1], parts[2], parts[3], parts[4]), id, r.GetString()!);
    }

    #endregion
}
