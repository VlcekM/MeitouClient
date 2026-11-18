using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProvingGrounds.Core.Glue;

/// <summary>
/// The arena's data inside a Meitou save (task 2.4, PA §2.20, CAP 9.1), without game types: what <c>IModSaveData.Write</c> puts in its
/// stream and how <c>Read</c> decides what the bytes are. The glue (src/Game/ArenaNativePersistence.cs) owns the ledger and does the I/O.
/// <para>Layout (format version 1):</para>
/// <code>
///   byte 0x01                      the format version
///   the snapshot JSON              ArenaPersistence.EncodeSnapshot's bytes, unchanged: byte for byte the original's proving_grounds_mmr.json
///   byte 0x00                      separator (the codec rejects an unescaped NUL in JSON, so the first NUL ends the snapshot)
///   {"fighterIdentities":[...]}\n  the mod-side extension: the fighter identity tokens created in the world (FighterIdentityMap.Registered),
///                                  in ordinal order. The original kept them as UUIDs in each character's save state (FighterIdentity.cpp:69-123);
///                                  the port has no per-character slot, and the standings rows do not carry every created token (a match
///                                  creates ids for all its fighters before it skips some, LeaderboardStore.cpp:254-273). divergences.md.
/// </code>
/// <para>Reading never trusts part of a bad payload: anything but a clean frame, a Ready snapshot and a valid list is <see cref="ReadKind.Unreadable"/>
/// with a fresh snapshot and no tokens.</para>
/// </summary>
public static class ArenaSaveData
{
    /// <summary>The first byte of the payload.</summary>
    public const byte FormatVersion = 1;

    /// <summary>A resource guard for the identity list (as the codec's 16 MiB guard for the snapshot), not a gameplay limit.</summary>
    public const int MaxIdentityListBytes = 16 * 1024 * 1024;

    /// <summary>The largest payload <see cref="Read"/> takes: the version byte, the largest snapshot the codec takes, the separator and the
    /// largest list. <c>Read</c> reads at most one byte more from its stream (<see cref="ReadBounded"/>).</summary>
    public const int MaxPayloadBytes = 1 + ArenaPersistence.MaxSnapshotBytes + 1 + MaxIdentityListBytes;

    const string ListKey = "fighterIdentities";

    /// <summary>The snapshot's <c>saveKey</c> in a Meitou save. The original wrote the save folder's name there to match the sidecar to its
    /// save (ArenaSnapshotFiles.cpp:83-99); Meitou keeps the data inside the save, so nothing is matched. The codec requires a nonempty value
    /// (ArenaSnapshotJson.cpp:365, <c>Text(v,"saveKey","$",true)</c>), so "" (the plan's choice) cannot be written: a fixed key keeps the
    /// field and the format. divergences.md.</summary>
    public const string InSaveKey = "meitou";

    /// <summary>What <see cref="Read"/> found.</summary>
    public enum ReadKind
    {
        /// <summary>A Ready snapshot and its identity list.</summary>
        Loaded,
        /// <summary>A snapshot below schema 11 (the codec's LegacyReset): start fresh (ArenaNativePersistence.cpp:381-386).</summary>
        LegacyReset,
        /// <summary>Anything else (a bad frame, InvalidJson, InvalidData, UnsupportedVersion): the C++ blocks the arena
        /// (ArenaNativePersistence.cpp:387-395), see <see cref="ArenaWorldState.Read"/>.</summary>
        Unreadable,
    }

    /// <summary>The outcome of <see cref="Read"/>. <see cref="Snapshot"/> is the decoded snapshot for <see cref="ReadKind.Loaded"/>, a fresh
    /// one otherwise; <see cref="Reason"/> is empty for Loaded, else "field: message" (the C++ <c>loaded.field+": "+loaded.message</c>) or a
    /// framing reason.</summary>
    public sealed class ReadResult
    {
        public ReadKind Kind;
        /// <summary>The codec's code (Ready, LegacyReset, InvalidJson, ...); a framing failure is InvalidData.</summary>
        public ArenaPersistence.LoadCode Code = ArenaPersistence.LoadCode.InvalidData;
        public ArenaPersistence.Snapshot Snapshot = new();
        public List<string> Tokens = [];
        public string Reason = "";
    }

    /// <summary>The payload for a save: <paramref name="snapshot"/> encoded by the Core codec (which re-checks it, ArenaSnapshotJson.cpp:495-509)
    /// and the tokens (sorted, repeats and empties dropped). False with the codec's error (and an empty payload) when it refuses.</summary>
    public static bool TryWrite(ArenaPersistence.Snapshot snapshot, IEnumerable<string> tokens, out byte[] payload, out string error)
    {
        payload = [];
        byte[] json = [];
        if (!ArenaPersistence.EncodeSnapshot(snapshot, ref json, out error)) return false;
        var sorted = new SortedSet<string>(tokens.Where(t => t.Length != 0), StringComparer.Ordinal);
        byte[] list;
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartArray(ListKey);
                foreach (var t in sorted) w.WriteStringValue(t);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            list = buffer.ToArray();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            error = "fighter identity list: " + e.Message;
            return false;
        }
        var all = new byte[1 + json.Length + 1 + list.Length];
        all[0] = FormatVersion;
        json.CopyTo(all, 1);
        all[1 + json.Length] = 0;
        list.CopyTo(all, 2 + json.Length);
        payload = all;
        error = "";
        return true;
    }

    /// <summary>Reads at most <paramref name="max"/> + 1 bytes (so a longer stream shows as too long without being read into memory).</summary>
    public static byte[] ReadBounded(Stream stream, int max)
    {
        int limit = max == int.MaxValue ? max : max + 1;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length < limit)
        {
            int want = (int)Math.Min(chunk.Length, limit - buffer.Length);
            int n = stream.Read(chunk, 0, want);
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>What a payload is (header). <paramref name="maxBytes"/> is the size guard (a test seam; <see cref="MaxPayloadBytes"/>).</summary>
    public static ReadResult Read(byte[] payload, int maxBytes = MaxPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0) return Bad("saved arena data is empty");
        if (payload.Length > maxBytes)
            return Bad("saved arena data is larger than " + maxBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
        if (payload[0] != FormatVersion)
            return Bad("unknown saved arena data format version " + payload[0].ToString(CultureInfo.InvariantCulture));
        int nul = Array.IndexOf(payload, (byte)0, 1);
        if (nul < 0) return Bad("no fighter identity list (the data ends inside the snapshot)");

        ArenaPersistence.LoadResult decoded = ArenaPersistence.DecodeSnapshot(payload.AsSpan(1, nul - 1).ToArray());
        if (decoded.Code == ArenaPersistence.LoadCode.LegacyReset)
            return new ReadResult { Kind = ReadKind.LegacyReset, Code = decoded.Code, Reason = decoded.Field + ": " + decoded.Message };
        if (decoded.Code != ArenaPersistence.LoadCode.Ready) return Bad(decoded.Field + ": " + decoded.Message, decoded.Code);

        if (!TryReadList(payload.AsMemory(nul + 1), out List<string> tokens, out string listError))
            return Bad("fighter identity list: " + listError);
        return new ReadResult { Kind = ReadKind.Loaded, Code = decoded.Code, Snapshot = decoded.Snapshot, Tokens = tokens };
    }

    static ReadResult Bad(string reason, ArenaPersistence.LoadCode code = ArenaPersistence.LoadCode.InvalidData) =>
        new() { Kind = ReadKind.Unreadable, Code = code, Reason = reason };

    // strict: one object, exactly one member "fighterIdentities", an array of distinct non-empty strings (TryWrite writes no repeats)
    static bool TryReadList(ReadOnlyMemory<byte> text, out List<string> tokens, out string error)
    {
        tokens = [];
        error = "";
        if (text.Length > MaxIdentityListBytes) { error = "larger than the 16 MiB safety limit"; return false; }
        try
        {
            using var doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "expected an object"; return false; }
            int members = 0;
            JsonElement array = default;
            foreach (var m in root.EnumerateObject())
            {
                members++;
                if (m.Name != ListKey) { error = "unknown member " + m.Name; return false; }
                array = m.Value;
            }
            if (members != 1) { error = members == 0 ? "missing " + ListKey : "repeated " + ListKey; return false; }
            if (array.ValueKind != JsonValueKind.Array) { error = ListKey + " is not an array"; return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in array.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String || e.GetString() is not { Length: > 0 } t) { error = "an entry is not a non-empty string"; tokens = []; return false; }
                if (!seen.Add(t)) { error = "repeated entry " + t; tokens = []; return false; }
                tokens.Add(t);
            }
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            error = e.Message;
            tokens = [];
            return false;
        }
    }

    /// <summary>The name of the recovered-data file: the UTC time as <c>yyyyMMdd-HHmmss.dat</c>, or <c>yyyyMMdd-HHmmss-2.dat</c>, -3, ... when a
    /// file of that name exists. <c>.dat</c>: it holds the whole payload as read (version byte, JSON, NUL, list), not a JSON file.</summary>
    public static string RecoveredFileName(DateTime now, Func<string, bool> exists)
    {
        string stem = now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string name = stem + ".dat";
        for (int i = 2; exists(name); i++) name = stem + "-" + i.ToString(CultureInfo.InvariantCulture) + ".dat";
        return name;
    }

    /// <summary>C++: LeaderboardStore.cpp:194-200 (<c>CompleteWorldTransition(true)</c>, a new game): a fresh snapshot whose card seed is
    /// <c>GetTickCount() | 1u</c>. A load without arena data starts from a default <c>Snapshot</c> instead (seed 1, ArenaNativePersistence.cpp:381-384).</summary>
    public static ArenaPersistence.Snapshot FreshSnapshot(uint tickCount)
    {
        var fresh = new ArenaPersistence.Snapshot();
        fresh.Challenges.Seed = tickCount | 1u;
        return fresh;
    }

    /// <summary>
    /// The snapshot a save writes: C++ LeaderboardStore.cpp:184-192 <c>CaptureSnapshot</c> (the ledger's copy, every booked offer cancelled)
    /// and ArenaNativePersistence.cpp:211 (<c>saveKey=name; generation=generation</c>; the save key is <see cref="InSaveKey"/>: Meitou keeps the data
    /// inside its save, so nothing has to match a file). Null when the ledger is not ready or a purchase is reserved
    /// (ArenaLedger.h:39), as the C++ save then reports "Arena portion of save unavailable".
    /// <para>The challenge credit: <paramref name="challengeCredit"/> = <c>TownChallengeBuyInPolicy::SaveCredit(g_challengeAccount)</c> (task 5.1:
    /// ArenaWorldState holds the buy-in account); null keeps the ledger's value (the tests of the codec). The planned bout is the ledger's until the
    /// town task fills <c>TownArena::CapturePlannedNpcBout</c> (ArenaNativePersistence.cpp:205).</para>
    /// </summary>
    public static ArenaPersistence.Snapshot? Capture(ArenaLedger.Ledger ledger, string generation, int? challengeCredit = null)
    {
        var output = new ArenaPersistence.Snapshot();
        if (!CaptureInto(ledger, output, challengeCredit)) return null;
        output.SaveKey = InSaveKey;
        output.Generation = generation;
        return output;
    }

    /// <summary>C++: LeaderboardStore.cpp:184-192 <c>CaptureSnapshot</c>, the one copy (Capture above and ArenaWorldState.CaptureSnapshot call it): the
    /// ledger's copy into <paramref name="output"/> (false, untouched, when not Ready or a purchase is reserved), the challenge credit
    /// (<paramref name="challengeCredit"/>; null keeps the ledger's value), every booked offer cancelled.</summary>
    public static bool CaptureInto(ArenaLedger.Ledger ledger, ArenaPersistence.Snapshot output, int? challengeCredit)
    {
        if (!ledger.Capture(output)) return false;
        if (challengeCredit is { } credit) output.ChallengeCredit = credit;
        CancelBookings(output);
        return true;
    }

    /// <summary>C++: LeaderboardStore.cpp:188-190: bookings do not resume on load, so a captured snapshot has every booked offer cancelled.</summary>
    static void CancelBookings(ArenaPersistence.Snapshot output)
    {
        for (int slot = 0; slot < 5; ++slot)
            TownArenaPolicy.CancelBooking(ref output.Challenges.Offers[slot].State);
        TownArenaPolicy.CancelBooking(ref output.Challenges.SkarnOffer.State);
    }
}
