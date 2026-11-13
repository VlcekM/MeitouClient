using System.Text.Json;

namespace Meitou.Data.Save;

/// <summary>
/// The blood on characters' weapons (Meitou-only, docs/game/blood-decals.md "On weapons and gear"), kept in <see cref="SaveFolder.WeaponBloodName"/>
/// beside the original's files, which are not touched: UTF-8 JSON <c>{ "format": 1, "weapons": [ { "hand": [type, C, CS, I, S], "amount", "wet",
/// "colour": [r, g, b], "back" } ] }</c>, each entry keyed by the character's save handle (what survives a load). A file that is missing, unreadable or
/// from a newer format reads as no weapon blood (clean weapons), and an entry naming no character is dropped by the host.
/// </summary>
public static class SaveWeaponBlood
{
    /// <summary>
    /// The file format this code writes and reads; any other reads as no weapon blood.
    /// </summary>
    const int Format = 1;

    /// <summary>
    /// One character's weapon: the blood on it (0..1), how wet it still is (1 just struck), the colour of the victim's blood (0..1 RGB, not yet dried)
    /// and whether the weapon on the back is the one.
    /// </summary>
    public readonly record struct Entry(Hand Hand, float Amount, float Wet, float R, float G, float B, bool Back);

    /// <summary>
    /// The file for <paramref name="entries"/>.
    /// </summary>
    public static byte[] Write(IEnumerable<Entry> entries)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("format", Format);
            w.WriteStartArray("weapons");
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
        w.WriteNumber("amount", e.Amount);
        w.WriteNumber("wet", e.Wet);
        w.WriteStartArray("colour");
        w.WriteNumberValue(e.R);
        w.WriteNumberValue(e.G);
        w.WriteNumberValue(e.B);
        w.WriteEndArray();
        w.WriteBoolean("back", e.Back);
        w.WriteEndObject();
    }

    /// <summary>
    /// The entries of <paramref name="json"/>; empty for anything that is not a file of this format (a stale or damaged file never fails a load).
    /// </summary>
    public static List<Entry> Read(byte[]? json)
    {
        var entries = new List<Entry>();
        if (json is null) return entries;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.GetInt32() != Format
                || !root.TryGetProperty("weapons", out var weapons) || weapons.ValueKind != JsonValueKind.Array)
                return entries;

            foreach (var item in weapons.EnumerateArray())
            {
                if (ReadEntry(item) is { } entry) entries.Add(entry);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            entries.Clear();
        }

        return entries;
    }

    /// <summary>
    /// One entry of the file, its values clamped to their ranges; null for one without a five-number hand, a three-number colour or any blood.
    /// </summary>
    static Entry? ReadEntry(JsonElement item)
    {
        var h = item.GetProperty("hand").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var c = item.GetProperty("colour").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        float amount = item.GetProperty("amount").GetSingle();
        if (h.Length != 5 || c.Length != 3 || !(amount > 0)) return null;

        var hand = new Hand(h[0], h[1], h[2], h[3], h[4]);
        float wet = Math.Clamp(item.GetProperty("wet").GetSingle(), 0, 1);
        bool onBack = item.TryGetProperty("back", out var back) && back.ValueKind == JsonValueKind.True;
        return new Entry(hand, Math.Min(amount, 1), wet, Math.Clamp(c[0], 0, 1), Math.Clamp(c[1], 0, 1), Math.Clamp(c[2], 0, 1), onBack);
    }
}
