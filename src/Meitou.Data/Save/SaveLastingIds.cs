using System.Text.Json;

namespace Meitou.Data.Save;

/// <summary>
/// The lasting ids code mods gave characters (Meitou-only, docs/formats/save.md "Character ids"), kept in <see cref="SaveFolder.LastingIdsName"/>
/// beside the original's files, which are not touched: UTF-8 JSON <c>{ "format": 1, "prefix": p, "next": n, "characters": [ { "hand": [type, C, CS,
/// I, S], "id", "record", "name", "portrait" } ] }</c>. Each entry is keyed by the character's save handle as written by the same save; the record
/// id, name and portrait serial let an entry whose handle the save no longer has find its character again (<see cref="Rebind"/>). A file that is
/// missing, unreadable or from another format reads as no ids.
/// </summary>
public static class SaveLastingIds
{
    /// <summary>
    /// The file format this code writes and reads; any other reads as no ids.
    /// </summary>
    const int Format = 1;

    /// <summary>
    /// One character's id: its save handle, the id (never 0), and the character's record string id, name and portrait serial as written.
    /// </summary>
    public readonly record struct Entry(Hand Hand, ulong Id, string Record, string Name, int Portrait);

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
        w.WriteString("name", e.Name);
        w.WriteNumber("portrait", e.Portrait);
        w.WriteEndObject();
    }

    /// <summary>
    /// The entry of a character of a save: its handle, its record, name and portrait serial (the saved <c>portrait_serial</c>, else the handle's
    /// serial, as both games take it).
    /// </summary>
    public static Entry EntryOf(SaveCharacter sc, ulong id)
    {
        var hand = sc.Handle;
        int portrait = sc.GameState is { } g && g.Ints.TryGetValue("portrait_serial", out int serial) ? serial : hand.S;
        return new Entry(hand, id, sc.RecordId, sc.Name, portrait);
    }

    #endregion

    #region Reading

    /// <summary>
    /// The contents of <paramref name="json"/>: no entries, prefix and counter for anything that is not a file of this format (a stale or damaged file
    /// never fails a load). An entry with a handle that is not five numbers, an id of 0, or a handle or id an earlier entry has is dropped.
    /// </summary>
    public static Contents Read(byte[]? json)
    {
        var entries = new List<Entry>();
        if (json is null) return new Contents(entries, 0, 0);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.GetInt32() != Format
                || !root.TryGetProperty("characters", out var characters) || characters.ValueKind != JsonValueKind.Array)
                return new Contents(entries, 0, 0);

            uint prefix = root.TryGetProperty("prefix", out var pf) ? pf.GetUInt32() : 0;
            ulong next = root.TryGetProperty("next", out var n) ? n.GetUInt64() : 0;
            var hands = new HashSet<Hand>();
            var ids = new HashSet<ulong>();
            foreach (var item in characters.EnumerateArray())
            {
                if (ReadEntry(item) is { } entry && hands.Add(entry.Hand) && ids.Add(entry.Id)) entries.Add(entry);
            }

            return new Contents(entries, prefix, next);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return new Contents([], 0, 0);
        }
    }

    /// <summary>
    /// One entry of the file; null for one without a five-number hand or with an id of 0.
    /// </summary>
    static Entry? ReadEntry(JsonElement item)
    {
        var h = item.GetProperty("hand").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        ulong id = item.GetProperty("id").GetUInt64();
        if (h.Length != 5 || id == 0) return null;

        string record = item.TryGetProperty("record", out var r) ? r.GetString() ?? "" : "";
        string name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
        int portrait = item.TryGetProperty("portrait", out var p) ? p.GetInt32() : 0;
        return new Entry(new Hand(h[0], h[1], h[2], h[3], h[4]), id, record, name, portrait);
    }

    #endregion

    #region Finding characters again

    /// <summary>
    /// The entries keyed by the handles of <paramref name="game"/>'s characters. An entry whose handle no character of the save has (the save was
    /// written again by a game that gave its characters other handles, such as the original after a squad move) takes the handle of the one
    /// character that has its record, name and portrait serial, when exactly one such character has no entry of its own and no other such entry
    /// wants it; otherwise it is dropped.
    /// </summary>
    public static List<Entry> Rebind(List<Entry> entries, SaveGame game)
    {
        var byHand = new Dictionary<Hand, SaveCharacter>();
        foreach (var sc in game.Platoons.SelectMany(p => p.Characters))
            if (sc.Handle is { IsNull: false } hand) byHand.TryAdd(hand, sc);

        var kept = entries.Where(e => byHand.ContainsKey(e.Hand)).ToList();
        var orphans = entries.Where(e => !byHand.ContainsKey(e.Hand)).ToList();
        if (orphans.Count == 0) return kept;

        // The characters with no entry, by what an orphan can match them on.
        var taken = kept.Select(e => e.Hand).ToHashSet();
        var free = byHand.Values
            .Where(sc => !taken.Contains(sc.Handle))
            .Select(sc => EntryOf(sc, 0))
            .GroupBy(Fingerprint)
            .ToDictionary(g => g.Key, g => g.ToList());
        var wanted = orphans.GroupBy(Fingerprint).ToDictionary(g => g.Key, g => g.Count());
        foreach (var orphan in orphans)
        {
            var key = Fingerprint(orphan);
            if (wanted[key] == 1 && free.TryGetValue(key, out var candidates) && candidates.Count == 1)
                kept.Add(orphan with { Hand = candidates[0].Hand });
        }

        return kept;
    }

    /// <summary>
    /// What an entry is matched on when its handle is gone: record, name and portrait serial.
    /// </summary>
    static (string Record, string Name, int Portrait) Fingerprint(Entry e) => (e.Record, e.Name, e.Portrait);

    #endregion
}
