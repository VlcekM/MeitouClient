// Port of upstream/ProvingGrounds/plugin/src/ArenaSnapshotJson.h and ArenaSnapshotJson.cpp (Proving Grounds by Shutori, GPLv3).
using System.Globalization;
using System.Text;
using static ProvingGrounds.Core.RapidJson;

namespace ProvingGrounds.Core;

// The v16 save snapshot as JSON text, byte for byte the original's sidecar (proving_grounds_mmr.json); M2 stores it through
// IModSaveData. Text is BYTES (the C++ works on std::string): DecodeSnapshot takes the bytes of a file, EncodeSnapshot gives the
// bytes to store. rapidjson is ported in Core/RapidJson.cs (the reader, DOM and PrettyWriter this file calls).
//
// Strings: the C++ strings hold UTF-8 bytes; here they are C# strings. A decoded string has passed the reader's UTF-8 validation
// and the guard's scalar check, so it converts exactly. Writing encodes each string to UTF-8; a lone UTF-16 surrogate (which a
// std::string cannot hold) is written as the 3-byte sequence of its code unit, so the codec's own re-check rejects it ("Invalid
// encoding in string."), as the original rejects any invalid bytes it was handed. Sorting by id compares UTF-8 bytes (std::string
// operator<), not UTF-16 code units.
//
// For the M2 save glue:
// (a) DecodeSnapshot reads PGConfig.BookieCreditCeiling() (the bookie credit's upper bound): load pg_config.json before reading a save,
//     or a credit the configured stake range allows is rejected and the save's arena progression with it.
// (b) Reading from a stream (IModSaveData.Read), read at most MaxSnapshotBytes + 1 bytes and pass them as they are: the size check
//     then reports a too-large text as the original does, without reading an unbounded stream into memory.
// (c) Encode a Clone() of the snapshot taken on the game thread, never the live state (ArenaSaveCoordinator.h `captured = snapshot`).
public static partial class ArenaPersistence
{
    // C++: ArenaSnapshotJson.h:5
    public const int MaxSnapshotBytes = 16 * 1024 * 1024;

    // Resource guards, not gameplay limits. The schema itself needs at most 7 levels.
    // C++: ArenaSnapshotJson.cpp:22
    const int MaxDepth = 32;

    // C++: ArenaSnapshotJson.cpp:23-27
    sealed class Failure : Exception
    {
        public readonly LoadCode Code;
        public readonly string Field, Text;
        public Failure(LoadCode c, string p, string m) : base(m) { Code = c; Field = p; Text = m; }
    }

    // C++: ArenaSnapshotJson.cpp:28
    static void Fail(string field, string message) { throw new Failure(LoadCode.InvalidData, field, message); }
    // C++: ArenaSnapshotJson.cpp:29
    static string Index(string path, long i) => path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
    // C++: ArenaSnapshotJson.cpp:30
    static string Member(string path, string key) => path + "." + key;

    static string Str(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
    static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>UTF-8 bytes of a string, lone surrogates as the 3-byte form of their code unit (see the file header).</summary>
    internal static byte[] Utf8Bytes(string s)
    {
        var os = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; ++i)
        {
            uint cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                cp = (uint)char.ConvertToUtf32(s[i], s[++i]);
            if (cp <= 0x7F) os.Add((byte)cp);
            else if (cp <= 0x7FF) { os.Add((byte)(0xC0 | (cp >> 6))); os.Add((byte)(0x80 | (cp & 0x3F))); }
            else if (cp <= 0xFFFF) { os.Add((byte)(0xE0 | (cp >> 12))); os.Add((byte)(0x80 | ((cp >> 6) & 0x3F))); os.Add((byte)(0x80 | (cp & 0x3F))); }
            else
            {
                os.Add((byte)(0xF0 | (cp >> 18))); os.Add((byte)(0x80 | ((cp >> 12) & 0x3F)));
                os.Add((byte)(0x80 | ((cp >> 6) & 0x3F))); os.Add((byte)(0x80 | (cp & 0x3F)));
            }
        }
        return os.ToArray();
    }

    /// <summary>std::string operator&lt; : the UTF-8 bytes compared unsigned, a prefix first.</summary>
    static int ByteCompare(string a, string b) => Utf8Bytes(a).AsSpan().SequenceCompareTo(Utf8Bytes(b));

    // Validate while streaming, before allocating a DOM. Iterative parsing prevents
    // recursive stack exhaustion. Keeping every object key catches duplicates before
    // lookup can hide an edit; decoded keys also catch escaped-key aliases.
    // C++: ArenaSnapshotJson.cpp:35-99
    sealed class SyntaxGuard : IHandler
    {
        // C++: ArenaSnapshotJson.cpp:36-42
        sealed class Frame
        {
            public readonly string Path;
            public string Key = "";
            public readonly bool Object;
            public long Index;
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal);
            public Frame(string p, bool o) { Path = p; Object = o; Index = 0; }
        }

        readonly List<Frame> _frames = new List<Frame>();
        public string ErrorField = "", Error = "";
        public LoadCode Code = LoadCode.InvalidJson;

        // C++: ArenaSnapshotJson.cpp:47-51
        public string Path()
        {
            if (_frames.Count == 0) return "$";
            Frame f = _frames[_frames.Count - 1];
            return f.Object ? (f.Key.Length == 0 ? f.Path : Member(f.Path, f.Key)) : ArenaPersistence.Index(f.Path, f.Index);
        }

        // C++: ArenaSnapshotJson.cpp:52
        void Done() { if (_frames.Count != 0) { ++_frames[_frames.Count - 1].Index; _frames[_frames.Count - 1].Key = ""; } }

        // C++: ArenaSnapshotJson.cpp:53
        bool Stop(LoadCode c, string p, string message) { Code = c; ErrorField = p; Error = message; return false; }

        // C++: ArenaSnapshotJson.cpp:54-62
        bool Utf8(ReadOnlySpan<byte> s)
        {
            byte[] bytes = s.ToArray();
            var input = new RapidJson.MemoryStream(bytes, 0, bytes.Length);
            while (input.Tell() < bytes.Length)
            {
                if (!Utf8Decode(input, out uint cp) || (cp >= 0xD800 && cp <= 0xDFFF))
                    return Stop(LoadCode.InvalidJson, Path(), "invalid Unicode scalar or UTF-8 encoding");
            }
            return true;
        }

        // C++: ArenaSnapshotJson.cpp:63-68
        public bool Key(ReadOnlySpan<byte> s)
        {
            if (!Utf8(s)) return false;
            Frame f = _frames[_frames.Count - 1];
            f.Key = Str(s);
            if (!f.Keys.Add(f.Key)) return Stop(LoadCode.InvalidData, Path(), "duplicate object member");
            return true;
        }

        // C++: ArenaSnapshotJson.cpp:69
        public bool String(ReadOnlySpan<byte> s) { if (!Utf8(s)) return false; Done(); return true; }

        // C++: ArenaSnapshotJson.cpp:70-89
        public bool RawNumber(ReadOnlySpan<byte> s)
        {
            // The DOM conversion can underflow before Number sees the value.
            // Inspect the original mantissa, excluding exponent digits, so exact
            // zero forms such as -0.000e-400 remain valid edits.
            bool nonzero = false, floating = false, exponent = false;
            for (int i = 0; i < s.Length; ++i)
            {
                if (s[i] == 'e' || s[i] == 'E') { exponent = true; floating = true; }
                else if (s[i] == '.') floating = true;
                else if (!exponent && s[i] >= '1' && s[i] <= '9') nonzero = true;
            }
            if (nonzero && floating)
            {
                // number.Parse<kParseFullPrecisionFlag>(s,n): the C++ uses the recursive parser here; for one number literal the
                // iterative one sends the same event and the same errors
                byte[] literal = s.ToArray();
                var number = new Document().Parse(literal, 0, literal.Length, ParseFlags.FullPrecision | ParseFlags.Iterative);
                if (number.HasParseError())
                    return Stop(LoadCode.InvalidJson, Path(), GetParseErrorEn(number.GetParseError()));
                if (number.Root.GetDouble() == 0.0)
                    return Stop(LoadCode.InvalidData, Path(), "nonzero number is too small to represent as a double");
            }
            Done(); return true;
        }

        // C++: ArenaSnapshotJson.cpp:90 Default (BaseReaderHandler routes Null, Bool and the number events here)
        bool Default() { Done(); return true; }
        public bool Null() => Default();
        public bool Bool(bool b) => Default();
        public bool Int(int i) => Default();
        public bool Uint(uint u) => Default();
        public bool Int64(long i) => Default();
        public bool Uint64(ulong u) => Default();
        public bool Double(double d) => Default();

        // C++: ArenaSnapshotJson.cpp:91-94
        bool Start(bool @object)
        {
            if (_frames.Count >= MaxDepth) return Stop(LoadCode.InvalidJson, Path(), "JSON nesting exceeds 32 levels");
            _frames.Add(new Frame(Path(), @object)); return true;
        }

        // C++: ArenaSnapshotJson.cpp:95-98
        public bool StartObject() => Start(true);
        public bool StartArray() => Start(false);
        public bool EndObject(uint memberCount) { _frames.RemoveAt(_frames.Count - 1); Done(); return true; }
        public bool EndArray(uint elementCount) { _frames.RemoveAt(_frames.Count - 1); Done(); return true; }
    }

    // C++: ArenaSnapshotJson.cpp:100-108
    static void Object(Value v, string p, string allowed)
    {
        if (!v.IsObject) Fail(p, "expected an object");
        string names = "|" + allowed + "|";
        foreach (KeyValuePair<Value, Value> i in v.MembersList)
        {
            string key = Str(i.Key.Str);
            if (key.Contains('\0') || key.Contains('|') || !names.Contains("|" + key + "|", StringComparison.Ordinal))
                Fail(Member(p, key), "unknown field");
        }
    }

    // C++: ArenaSnapshotJson.cpp:109-113
    static Value At(Value v, string key, string p)
    {
        Value? i = v.FindMember(key);
        if (i == null) Fail(Member(p, key), "required field is missing");
        return i!;
    }

    // C++: ArenaSnapshotJson.cpp:114
    static void Array(Value v, string p) { if (!v.IsArray) Fail(p, "expected an array"); }

    // C++: ArenaSnapshotJson.cpp:115-121
    static string String(Value v, string p, bool nonempty = false)
    {
        if (!v.IsString) Fail(p, "expected a string");
        string result = Str(v.Str);
        if (result.Contains('\0')) Fail(p, "embedded NUL is not supported");
        if (nonempty && result.Length == 0) Fail(p, "expected a nonempty identifier");
        return result;
    }

    // C++: ArenaSnapshotJson.cpp:122
    static string Text(Value v, string key, string p, bool nonempty = false) => String(At(v, key, p), Member(p, key), nonempty);

    // C++: ArenaSnapshotJson.cpp:123-126
    static int Integer(Value v, string p, int lo, int hi)
    {
        if (!v.IsInt || v.GetInt() < lo || v.GetInt() > hi) Fail(p, "expected an integer in [" + I(lo) + ", " + I(hi) + "]");
        return v.GetInt();
    }

    // C++: ArenaSnapshotJson.cpp:127
    static int Int(Value v, string key, string p, int lo, int hi) => Integer(At(v, key, p), Member(p, key), lo, hi);

    // C++: ArenaSnapshotJson.cpp:128-131
    static int OptionalInt(Value v, string key, string p, int lo, int hi, int fallback)
    {
        Value? i = v.FindMember(key);
        return i == null ? fallback : Integer(i, Member(p, key), lo, hi);
    }

    // C++: ArenaSnapshotJson.cpp:132-136
    static uint Uint(Value v, string key, string p, uint hi)
    {
        Value n = At(v, key, p);
        if (!n.IsUint || n.GetUint() > hi) Fail(Member(p, key), "expected an unsigned integer in [0, " + I(hi) + "]");
        return n.GetUint();
    }

    // C++: ArenaSnapshotJson.cpp:137-142
    static uint OptionalUint(Value v, string key, string p, uint hi, uint fallback)
    {
        Value? i = v.FindMember(key);
        if (i == null) return fallback;
        if (!i.IsUint || i.GetUint() > hi) Fail(Member(p, key), "expected an unsigned integer in [0, " + I(hi) + "]");
        return i.GetUint();
    }

    // C++: ArenaSnapshotJson.cpp:143-147 (`s << hi`: an ostream double, i.e. %g)
    static double Number(Value v, string key, string p, double hi)
    {
        Value n = At(v, key, p);
        if (!n.IsNumber || !(n.GetDouble() >= 0.0) || n.GetDouble() > hi) Fail(Member(p, key), "expected a finite number in [0, " + CMath.FormatG(hi) + "]");
        return n.GetDouble();
    }

    // C++: ArenaSnapshotJson.cpp:148-150
    static bool Bool(Value v, string key, string p)
    {
        Value b = At(v, key, p); if (!b.IsBool) Fail(Member(p, key), "expected a boolean"); return b.GetBool();
    }

    // C++: ArenaSnapshotJson.cpp:151-154
    static void BoolArray(Value v, string p, Span<bool> @out, int count)
    {
        Array(v, p); if (v.Size != count) Fail(p, "unexpected array length");
        for (int i = 0; i < count; ++i) { if (!v[i].IsBool) Fail(Index(p, i), "expected a boolean"); @out[i] = v[i].GetBool(); }
    }

    // C++: ArenaSnapshotJson.cpp:155-158
    static void IntArray(Value v, string p, Span<int> @out, int count)
    {
        Array(v, p); if (v.Size != count) Fail(p, "unexpected array length");
        for (int i = 0; i < count; ++i) @out[i] = Integer(v[i], Index(p, i), 0, int.MaxValue);
    }

    // C++: ArenaSnapshotJson.cpp:159-165
    static void NumberArray(Value v, string p, Span<double> @out, int count, double hi)
    {
        Array(v, p); if (v.Size != count) Fail(p, "unexpected array length");
        for (int i = 0; i < count; ++i)
        {
            if (!v[i].IsNumber || !(v[i].GetDouble() >= 0.0) || v[i].GetDouble() > hi) Fail(Index(p, i), "expected a finite nonnegative number");
            @out[i] = v[i].GetDouble();
        }
    }

    // C++: ArenaSnapshotJson.cpp:166 (std::set<std::string>: membership only)
    static void Unique(HashSet<string> seen, string id, string p) { if (!seen.Add(id)) Fail(p, "duplicate identifier"); }

    // C++: ArenaSnapshotJson.cpp:167-178
    static void Standings(Value list, string p, List<LeaderboardData.Standing> rows)
    {
        Array(list, p); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < list.Size; ++i)
        {
            Value v = list[i]; string row = Index(p, i);
            Object(v, row, "id|name|mmr|wins|losses|matches");
            var s = new LeaderboardData.Standing(); s.Id = Text(v, "id", row, true); Unique(seen, s.Id, Member(row, "id"));
            s.Name = Text(v, "name", row); double rating = Number(v, "mmr", row, float.MaxValue); s.Mmr = (float)rating;
            if (rating > 0.0 && s.Mmr == 0.0f) Fail(Member(row, "mmr"), "positive rating is too small to represent as a float");
            s.Wins = Int(v, "wins", row, 0, int.MaxValue); s.Losses = Int(v, "losses", row, 0, int.MaxValue); s.Matches = Int(v, "matches", row, 0, int.MaxValue);
            rows.Add(s);
        }
    }

    // C++: ArenaSnapshotJson.cpp:179-183
    static ArenaRewards.Piece Reward(string id, string p)
    {
        for (int i = 0; i < (int)ArenaRewards.Piece.Count; ++i)
            if (id == ArenaRewards.StableId((ArenaRewards.Piece)i)) return (ArenaRewards.Piece)i;
        Fail(p, "unknown reward ID"); return ArenaRewards.Piece.Count;
    }

    // C++: ArenaSnapshotJson.cpp:184-193
    static bool KnownUnlock(string id)
    {
        if (id == ArenaRewards.GetWeaponLicence().StableId) return true;
        for (int i = 0; i < (int)ArenaRewards.ArmourSet.None; ++i)
            if (id == ArenaRewards.GetLicence((ArenaRewards.ArmourSet)i).StableId) return true;
        for (int i = 0; i < (int)ArenaRewards.GeneralItem.Count; ++i)
        {
            string? unlock = ArenaRewards.GetGeneral((ArenaRewards.GeneralItem)i).GrantsUnlockId;
            if (unlock != null && id == unlock) return true;
        }
        return false;
    }

    // C++: ArenaSnapshotJson.cpp:194-212
    static void Progression(Value list, string p, List<LeaderboardData.Progression> rows)
    {
        Array(list, p); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < list.Size; ++i)
        {
            Value v = list[i]; string row = Index(p, i);
            Object(v, row, "id|name|marks|rewardTiers");
            var s = new LeaderboardData.Progression(); s.Id = Text(v, "id", row, true); Unique(seen, s.Id, Member(row, "id"));
            s.Name = Text(v, "name", row); s.Marks = Int(v, "marks", row, 0, int.MaxValue);
            Value tiers = At(v, "rewardTiers", row); string tp = Member(row, "rewardTiers");
            if (!tiers.IsObject) Fail(tp, "expected an object mapping reward IDs to numeric tiers");
            foreach (KeyValuePair<Value, Value> t in tiers.MembersList)
            {
                string id = String(t.Key, tp, true), path = Member(tp, id);
                ArenaRewards.Piece piece = Reward(id, path);
                int tier = Integer(t.Value, path, 0, (int)ArenaRewards.Tier.MaxCount - 1);
                if (!ArenaRewards.IsValidTier(piece, (ArenaRewards.Tier)tier)) Fail(path, "tier is not supported for this reward");
                s.RewardTiers.Add(new RewardProgression.TierEntry(id, tier));
            }
            rows.Add(s);
        }
    }

    // C++: ArenaSnapshotJson.cpp:213-222
    static void History(Value list, string p, List<List<string>> history)
    {
        Array(list, p); if (list.Size > 5) Fail(p, "at most 5 recent bouts are supported");
        for (int i = 0; i < list.Size; ++i)
        {
            string row = Index(p, i); Array(list[i], row);
            if (list[i].Empty || list[i].Size > 8) Fail(row, "a bout must contain 1 to 8 opponent IDs");
            var seen = new HashSet<string>(StringComparer.Ordinal); var ids = new List<string>();
            for (int j = 0; j < list[i].Size; ++j) { string id = String(list[i][j], Index(row, j), true); Unique(seen, id, Index(row, j)); ids.Add(id); }
            history.Add(ids);
        }
    }

    // C++: ArenaSnapshotJson.cpp:223-227
    static bool KnownRole(string id)
    {
        if (TownFighterCatalog.Find(id) != null) return true;
        for (int i = 0; i <= (int)TownChallengePolicy.Encounter.Veyr; ++i) if (id == TownChallengePolicy.Role(i)) return true;
        return false;
    }

    // C++: ArenaSnapshotJson.cpp:228-256
    static void Offer(Value v, string p, bool skarn, ref TownChallengePolicy.Offer o)
    {
        Object(v, p, "epoch|encounter|format|mode|state|generated|draw|division|playerCount|enemyCount|context|opponents");
        o.Epoch = Int(v, "epoch", p, -2, 1999999);
        o.Encounter = Int(v, "encounter", p, 0, skarn ? (int)TownChallengePolicy.Encounter.Skarn : (int)TownChallengePolicy.Encounter.Veyr);
        o.Format = Int(v, "format", p, 0, skarn ? 6 : 5);
        int savedMode = OptionalInt(v, "mode", p, 0, 2, (int)TownChallengePolicy.ChallengeMode.Teams);
        o.Mode = savedMode == 2 ? (int)TownChallengePolicy.ChallengeMode.Teams : savedMode;
        o.State = (TownArenaPolicy.OfferState)Int(v, "state", p, 0, 2); o.Generated = Bool(v, "generated", p);
        o.Draw = Uint(v, "draw", p, 9999); o.Division = Int(v, "division", p, 0, 2);
        o.PlayerCount = Int(v, "playerCount", p, 0, 3); o.EnemyCount = Int(v, "enemyCount", p, 0, 4); o.Context = Text(v, "context", p);
        Value opponents = At(v, "opponents", p); string op = Member(p, "opponents"); Array(opponents, op);
        if (opponents.Size != 3 && opponents.Size != 4) Fail(op, "expected 3 or 4 opponent slots");
        if (o.EnemyCount > opponents.Size) Fail(Member(p, "enemyCount"), "opponent count exceeds stored opponent slots");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < opponents.Size; ++i)
        {
            string slot = Index(op, i); Value member = opponents[i]; Object(member, slot, "id|name|role");
            ref string id = ref TownChallengePolicy.OpponentId(ref o, i); ref string name = ref TownChallengePolicy.OpponentName(ref o, i);
            id = Text(member, "id", slot); name = Text(member, "name", slot); o.Roles[i] = Text(member, "role", slot);
            if (id.Length != 0) Unique(seen, id, Member(slot, "id"));
            if (o.Roles[i].Length != 0 && !KnownRole(o.Roles[i])) Fail(Member(slot, "role"), "unknown opponent role ID");
            if (o.Generated)
            {
                if (i < o.EnemyCount && (id.Length == 0 || o.Roles[i].Length == 0)) Fail(slot, "generated opponent requires an ID and role");
                if (i >= o.EnemyCount && (id.Length != 0 || name.Length != 0 || o.Roles[i].Length != 0)) Fail(slot, "unused generated opponent slot must be empty");
            }
            else if (i >= TownChallengePolicy.Opponents(o) && (id.Length != 0 || name.Length != 0)) Fail(slot, "opponent slot is unused by this format");
        }
        if (o.Generated && (o.PlayerCount == 0) != (o.EnemyCount == 0)) Fail(Member(p, "playerCount"), "generated team counts must both be zero or both be positive");
        if (o.Generated && o.EnemyCount > 0 && o.Context.Length == 0) Fail(Member(p, "context"), "resolved generated offer requires matchup context");
        if (o.Generated && o.State == TownArenaPolicy.OfferState.Booked && !TownChallengePolicy.Resolved(o)) Fail(p, "booked generated offer requires a complete lineup");
    }

    // C++: ArenaSnapshotJson.cpp:257-277
    static void Card(Value v, string p, TownChallengePolicy.Card c)
    {
        Object(v, p, "seed|ambientSeed|division|milestones|uniqueWins|challengeWins|playerHistory|ambientHistory|offers|skarnOffer|skarnGenerated|skarnWon|skarnAttempted|skarnInProgress|skarnLastEnd|lastChallengeEndHours|hasChallengeEnd|paidRefreshCount|paidRefreshNonce");
        c.Seed = Uint(v, "seed", p, uint.MaxValue); c.AmbientSeed = Uint(v, "ambientSeed", p, uint.MaxValue); c.Division = Int(v, "division", p, 0, 2);
        c.Milestones = Uint(v, "milestones", p, 7); c.UniqueWins = OptionalUint(v, "uniqueWins", p, (uint)TownChallengePolicy.UniqueVictory.AllUniqueVictories, 0); c.ChallengeWins = Uint(v, "challengeWins", p, 1000000000u);
        History(At(v, "playerHistory", p), Member(p, "playerHistory"), c.PlayerHistory); History(At(v, "ambientHistory", p), Member(p, "ambientHistory"), c.AmbientHistory);
        Value offers = At(v, "offers", p); string op = Member(p, "offers"); Array(offers, op);
        if (offers.Size != 5) Fail(op, "expected 5 regular challenge offers");
        for (int i = 0; i < 5; ++i) Offer(offers[i], Index(op, i), false, ref c.Offers[i]);
        for (int i = 0; i < 5; ++i)
            if (c.Offers[i].Encounter == (int)TownChallengePolicy.Encounter.Skarn && !TownChallengePolicy.SkarnUnlocked(c))
                Fail(Member(Index(op, i), "encounter"), "Skarn requires all five unique-fighter victories");
        Offer(At(v, "skarnOffer", p), Member(p, "skarnOffer"), true, ref c.SkarnOffer);
        c.SkarnGenerated = Bool(v, "skarnGenerated", p); c.SkarnWon = Bool(v, "skarnWon", p); c.SkarnAttempted = Bool(v, "skarnAttempted", p); c.SkarnInProgress = Bool(v, "skarnInProgress", p);
        c.SkarnLastEnd = Number(v, "skarnLastEnd", p, 24000000.0);
        c.LastChallengeEndHours = v.HasMember("lastChallengeEndHours") ?
            Number(v, "lastChallengeEndHours", p, 24000000.0) : 0.0;
        c.HasChallengeEnd = v.HasMember("hasChallengeEnd") ? Bool(v, "hasChallengeEnd", p) : false;
        c.PaidRefreshCount = v.HasMember("paidRefreshCount") ? Int(v, "paidRefreshCount", p, 0, int.MaxValue) : 0;
        c.PaidRefreshNonce = OptionalUint(v, "paidRefreshNonce", p, uint.MaxValue, 1u);
        if (c.SkarnGenerated && c.SkarnOffer.Encounter != 5) Fail(Member(p, "skarnOffer.encounter"), "generated Skarn offer must refer to Skarn (5)");
    }

    // C++: ArenaSnapshotJson.cpp:278-320
    static void Diagnostic(Value v, string p, ref TownDiagnosticData.State d)
    {
        Object(v, p, "status|runId|nextSequence|config|counters|pending");
        d.Status = Int(v, "status", p, (int)TownDiagnosticData.RunStatus.Off, (int)TownDiagnosticData.RunStatus.Ended);
        d.RunId = Uint(v, "runId", p, uint.MaxValue); d.NextSequence = Uint(v, "nextSequence", p, uint.MaxValue);
        Value cfg = At(v, "config", p); string cp = Member(p, "config");
        Object(cfg, cp, "target|models|coverage|reducedInfluence|underdogPercent|balanceStyleTier");
        d.Config.Target = Int(cfg, "target", cp, 1, 10000);
        BoolArray(At(cfg, "models", cp), Member(cp, "models"), d.Config.Models, TownDiagnosticData.ModelCount);
        BoolArray(At(cfg, "coverage", cp), Member(cp, "coverage"), d.Config.Coverage, TownDiagnosticData.CoverageCount);
        d.Config.ReducedInfluence = Number(cfg, "reducedInfluence", cp, 1.0);
        d.Config.UnderdogPercent = Int(cfg, "underdogPercent", cp, 0, 100);
        d.Config.BalanceStyleTier = Bool(cfg, "balanceStyleTier", cp);
        bool anyModel = false, anyCoverage = false;
        for (int i = 0; i < TownDiagnosticData.ModelCount; ++i) anyModel = anyModel || d.Config.Models[i];
        for (int i = 0; i < TownDiagnosticData.CoverageCount; ++i) anyCoverage = anyCoverage || d.Config.Coverage[i];
        if (!anyModel) Fail(Member(cp, "models"), "at least one model must be enabled");
        if (!anyCoverage) Fail(Member(cp, "coverage"), "at least one coverage bucket must be enabled");
        Value c = At(v, "counters", p); string xp = Member(p, "counters");
        Object(c, xp, "opened|completed|cancelled|aborted|teamAWins|teamBWins|largerWins|smallerWins|equalWins|favoriteWins|modelCompleted|coverageCompleted|schedulingHours|lastCompletedHours|schedulingSamples");
        d.Counters.Opened = Int(c, "opened", xp, 0, int.MaxValue); d.Counters.Completed = Int(c, "completed", xp, 0, int.MaxValue);
        d.Counters.Cancelled = Int(c, "cancelled", xp, 0, int.MaxValue); d.Counters.Aborted = Int(c, "aborted", xp, 0, int.MaxValue);
        d.Counters.TeamAWins = Int(c, "teamAWins", xp, 0, int.MaxValue); d.Counters.TeamBWins = Int(c, "teamBWins", xp, 0, int.MaxValue);
        d.Counters.LargerWins = Int(c, "largerWins", xp, 0, int.MaxValue); d.Counters.SmallerWins = Int(c, "smallerWins", xp, 0, int.MaxValue);
        d.Counters.EqualWins = Int(c, "equalWins", xp, 0, int.MaxValue); d.Counters.FavoriteWins = Int(c, "favoriteWins", xp, 0, int.MaxValue);
        IntArray(At(c, "modelCompleted", xp), Member(xp, "modelCompleted"), d.Counters.ModelCompleted, TownDiagnosticData.ModelCount);
        IntArray(At(c, "coverageCompleted", xp), Member(xp, "coverageCompleted"), d.Counters.CoverageCompleted, TownDiagnosticData.CoverageCount);
        d.Counters.SchedulingHours = Number(c, "schedulingHours", xp, double.MaxValue);
        d.Counters.LastCompletedHours = Number(c, "lastCompletedHours", xp, 24000000.0);
        d.Counters.SchedulingSamples = Int(c, "schedulingSamples", xp, 0, int.MaxValue);
        Value q = At(v, "pending", p); string qp = Member(p, "pending");
        Object(q, qp, "active|underdog|sequence|model|requestedCoverage|actualCoverage|relaxation|sizeA|sizeB|scoreA|scoreB|probabilityA|announcedHours");
        d.Pending.Active = Bool(q, "active", qp); d.Pending.Underdog = Bool(q, "underdog", qp);
        d.Pending.Sequence = Uint(q, "sequence", qp, uint.MaxValue); d.Pending.Model = Int(q, "model", qp, 0, TownDiagnosticData.ModelCount - 1);
        d.Pending.RequestedCoverage = Int(q, "requestedCoverage", qp, 0, TownDiagnosticData.CoverageCount - 1);
        d.Pending.ActualCoverage = Int(q, "actualCoverage", qp, 0, TownDiagnosticData.CoverageCount - 1);
        d.Pending.Relaxation = Int(q, "relaxation", qp, 0, 3); d.Pending.SizeA = Int(q, "sizeA", qp, 0, 4); d.Pending.SizeB = Int(q, "sizeB", qp, 0, 4);
        NumberArray(At(q, "scoreA", qp), Member(qp, "scoreA"), d.Pending.ScoreA, TownDiagnosticData.ModelCount, double.MaxValue);
        NumberArray(At(q, "scoreB", qp), Member(qp, "scoreB"), d.Pending.ScoreB, TownDiagnosticData.ModelCount, double.MaxValue);
        NumberArray(At(q, "probabilityA", qp), Member(qp, "probabilityA"), d.Pending.ProbabilityA, TownDiagnosticData.ModelCount, 1.0);
        d.Pending.AnnouncedHours = Number(q, "announcedHours", qp, 24000000.0);
        if (d.Pending.Active && (d.Status != (int)TownDiagnosticData.RunStatus.Active || d.Pending.Sequence == 0)) Fail(qp, "pending assignment requires an active run and nonzero sequence");
        if (d.Counters.Completed > d.Config.Target) Fail(Member(xp, "completed"), "completed fights exceed target");
    }

    // C++: ArenaSnapshotJson.cpp:321-356
    static void PlannedNpc(Value v, PlannedNpcBout bout)
    {
        const string p = "$.plannedNpc";
        Object(v, p, "active|registryId|startHours|scoreA|scoreB|teamA|teamB|market|wagerPending|wagerSide|stake|payout");
        bout.Active = Bool(v, "active", p);
        bout.RegistryId = Text(v, "registryId", p, bout.Active);
        bout.StartHours = Number(v, "startHours", p, 24000000.0);
        bout.ScoreA = Number(v, "scoreA", p, double.MaxValue);
        bout.ScoreB = Number(v, "scoreB", p, double.MaxValue);
        Value[] sides = { At(v, "teamA", p), At(v, "teamB", p) };
        List<string>[] teams = { bout.TeamA, bout.TeamB };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int side = 0; side < 2; ++side)
        {
            string path = Member(p, side != 0 ? "teamB" : "teamA");
            Array(sides[side], path);
            if (sides[side].Size > 4 || (bout.Active && sides[side].Empty)) Fail(path, "expected 1 to 4 fighters");
            for (int i = 0; i < sides[side].Size; ++i)
            {
                string row = Index(path, i);
                string id = String(sides[side][i], row, true);
                Unique(seen, id, row); teams[side].Add(id);
            }
        }
        bout.Market = Bool(v, "market", p);
        bout.WagerPending = Bool(v, "wagerPending", p);
        bout.WagerSide = Int(v, "wagerSide", p, -1, 1);
        bout.Stake = Int(v, "stake", p, 0, int.MaxValue / 5);
        bout.Payout = Int(v, "payout", p, 0, int.MaxValue);
        if (bout.Active && (bout.StartHours <= 0 || bout.ScoreA <= 0 || bout.ScoreB <= 0))
            Fail(p, "announced bout requires start time and positive scores");
        if (bout.WagerPending && (!bout.Active || !bout.Market || bout.WagerSide < 0 ||
            bout.Stake <= 0 || bout.Stake % PGConfig.StakeStep != 0 || bout.Payout < bout.Stake ||
            bout.Payout > PGConfig.BookieCreditCeiling()))
            Fail(p, "pending wager requires a valid announced market and payable frozen terms");
        if (!bout.Active && (bout.RegistryId.Length != 0 || bout.StartHours != 0 || bout.ScoreA != 0 || bout.ScoreB != 0 ||
            bout.TeamA.Count != 0 || bout.TeamB.Count != 0 || bout.Market || bout.WagerPending ||
            bout.WagerSide != -1 || bout.Stake != 0 || bout.Payout != 0)) Fail(p, "inactive bout must be empty");
    }

    // C++: ArenaSnapshotJson.cpp:357-400
    static void Read(Value v, int sourceVersion, Snapshot s)
    {
        string fields = sourceVersion >= 16 ?
            "version|saveKey|generation|playerStandings|townStandings|progression|factionUnlocks|challenges|diagnostic|bookieCredit|challengeCredit|plannedNpc" : sourceVersion >= 15 ?
            "version|saveKey|generation|playerStandings|townStandings|progression|factionUnlocks|challenges|diagnostic|bookieCredit|challengeCredit" :
            sourceVersion >= 12 ?
            "version|saveKey|generation|performanceProfile|performanceWelcomeSeen|playerStandings|townStandings|progression|factionUnlocks|challenges|diagnostic|bookieCredit|challengeCredit" :
            "version|saveKey|generation|playerStandings|townStandings|progression|factionUnlocks|challenges|diagnostic|bookieCredit|challengeCredit";
        Object(v, "$", fields);
        s.SaveKey = Text(v, "saveKey", "$", true); s.Generation = Text(v, "generation", "$", true);
        if (sourceVersion >= 12 && sourceVersion < 15)
        {
            string value = Text(v, "performanceProfile", "$", true);
            ArenaPerformance.Profile legacyProfile = ArenaPerformance.Profile.High;
            if (!ArenaPerformance.Parse(value, ref legacyProfile))
                Fail("$.performanceProfile", "expected high, normal, or potato");
            _ = Bool(v, "performanceWelcomeSeen", "$");
        }
        Standings(At(v, "playerStandings", "$"), "$.playerStandings", s.Fighters.PlayerStandings);
        Standings(At(v, "townStandings", "$"), "$.townStandings", s.Fighters.TownStandings);
        Progression(At(v, "progression", "$"), "$.progression", s.Fighters.Progression);
        Value unlocks = At(v, "factionUnlocks", "$"); Array(unlocks, "$.factionUnlocks"); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < unlocks.Size; ++i)
        {
            string p = Index("$.factionUnlocks", i), id = String(unlocks[i], p, true); Unique(seen, id, p);
            if (!KnownUnlock(id)) Fail(p, "unknown faction unlock ID"); s.Fighters.FactionUnlocks.Add(id);
        }
        Card(At(v, "challenges", "$"), "$.challenges", s.Challenges);
        try
        {
            Diagnostic(At(v, "diagnostic", "$"), "$.diagnostic", ref s.Diagnostic);
        }
        catch (Failure f)
        {
            if (!f.Field.StartsWith("$.diagnostic", StringComparison.Ordinal)) throw;
            s.Diagnostic = new TownDiagnosticData.State();
            s.DiagnosticError = f.Field + ": " + f.Text;
        }
        // A bookie credit is one pending winning payout, so its ceiling follows
        // the configured stake range: a raised maximum must not let the writer
        // produce a value this reader rejects, because rejecting it discards the
        // save's whole arena progression. The challenge credit ceiling is the
        // quote's own payout cap (TownChallengeBuyInPolicy::Quote::Valid).
        s.BookieCredit = Int(v, "bookieCredit", "$", 0, PGConfig.BookieCreditCeiling()); s.ChallengeCredit = Int(v, "challengeCredit", "$", 0, 20000);
        if (sourceVersion >= 16)
        {
            PlannedNpc(At(v, "plannedNpc", "$"), s.PlannedNpc);
            if (s.PlannedNpc.WagerPending && s.BookieCredit != 0)
                Fail("$.bookieCredit", "a pending wager cannot coexist with a bookie credit");
        }
    }

    // ---- writer -----------------------------------------------------------------------------------------------------------------

    static byte[] K(string key) => Encoding.ASCII.GetBytes(key);
    // C++: ArenaSnapshotJson.cpp:401
    static void Str(PrettyWriter w, string s) { w.String(Utf8Bytes(s)); }
    // C++: ArenaSnapshotJson.cpp:402
    static void Text(PrettyWriter w, string key, string s) { w.Key(K(key)); Str(w, s); }
    // C++: ArenaSnapshotJson.cpp:403
    static void Int(PrettyWriter w, string key, int n) { w.Key(K(key)); w.Int(n); }
    // C++: ArenaSnapshotJson.cpp:404
    static void Uint(PrettyWriter w, string key, uint n) { w.Key(K(key)); w.Uint(n); }
    // C++: ArenaSnapshotJson.cpp:405
    static void Bool(PrettyWriter w, string key, bool b) { w.Key(K(key)); w.Bool(b); }
    // C++: ArenaSnapshotJson.cpp:406
    static void Number(PrettyWriter w, string key, double n, string p) { w.Key(K(key)); if (!w.Double(n)) Fail(p, "expected a finite number"); }
    // C++: ArenaSnapshotJson.cpp:408
    static void BoolArray(PrettyWriter w, string key, ReadOnlySpan<bool> values, int count) { w.Key(K(key)); w.StartArray(); for (int i = 0; i < count; ++i) w.Bool(values[i]); w.EndArray(); }
    // C++: ArenaSnapshotJson.cpp:409
    static void IntArray(PrettyWriter w, string key, ReadOnlySpan<int> values, int count) { w.Key(K(key)); w.StartArray(); for (int i = 0; i < count; ++i) w.Int(values[i]); w.EndArray(); }
    // C++: ArenaSnapshotJson.cpp:410
    static void NumberArray(PrettyWriter w, string key, ReadOnlySpan<double> values, int count, string p)
    {
        w.Key(K(key)); w.StartArray(); for (int i = 0; i < count; ++i) { if (!w.Double(values[i])) Fail(Index(p, i), "expected a finite number"); } w.EndArray();
    }

    // std::sort with ById (ArenaSnapshotJson.cpp:407) over a copy. std::sort is not stable; it differs from this stable sort only
    // between rows with the same id, which the writer's own re-check then rejects as duplicates.
    static List<T> SortedById<T>(IEnumerable<T> rows, Func<T, string> id)
    {
        var copy = new List<T>(rows);
        var keyed = copy.Select((row, index) => (row, index)).ToList();
        keyed.Sort((a, b) => { int c = ByteCompare(id(a.row), id(b.row)); return c != 0 ? c : a.index.CompareTo(b.index); });
        return keyed.Select(k => k.row).ToList();
    }

    // C++: ArenaSnapshotJson.cpp:411-418
    static void Standings(PrettyWriter w, string key, List<LeaderboardData.Standing> source)
    {
        List<LeaderboardData.Standing> rows = SortedById(source, r => r.Id); w.Key(K(key)); w.StartArray();
        for (int i = 0; i < rows.Count; ++i)
        {
            LeaderboardData.Standing s = rows[i]; w.StartObject(); Text(w, "id", s.Id); Text(w, "name", s.Name);
            Number(w, "mmr", s.Mmr, Member(Index(Member("$", key), i), "mmr")); Int(w, "wins", s.Wins); Int(w, "losses", s.Losses); Int(w, "matches", s.Matches); w.EndObject();
        }
        w.EndArray();
    }

    // C++: ArenaSnapshotJson.cpp:419-421
    static void History(PrettyWriter w, string key, List<List<string>> history)
    {
        w.Key(K(key)); w.StartArray(); for (int i = 0; i < history.Count; ++i) { w.StartArray(); for (int j = 0; j < history[i].Count; ++j) Str(w, history[i][j]); w.EndArray(); } w.EndArray();
    }

    // C++: ArenaSnapshotJson.cpp:422-426
    static void Offer(PrettyWriter w, in TownChallengePolicy.Offer o)
    {
        w.StartObject(); Int(w, "epoch", o.Epoch); Int(w, "encounter", o.Encounter); Int(w, "format", o.Format); Int(w, "mode", o.Mode); Int(w, "state", (int)o.State);
        Bool(w, "generated", o.Generated); Uint(w, "draw", o.Draw); Int(w, "division", o.Division); Int(w, "playerCount", o.PlayerCount); Int(w, "enemyCount", o.EnemyCount); Text(w, "context", o.Context);
        w.Key(K("opponents")); w.StartArray(); for (int i = 0; i < 4; ++i) { w.StartObject(); Text(w, "id", TownChallengePolicy.OpponentIdConst(o, i)); Text(w, "name", TownChallengePolicy.OpponentNameConst(o, i)); Text(w, "role", o.Roles[i]); w.EndObject(); } w.EndArray(); w.EndObject();
    }

    // C++: ArenaSnapshotJson.cpp:427-443
    static void Diagnostic(PrettyWriter w, in TownDiagnosticData.State d)
    {
        w.Key(K("diagnostic")); w.StartObject(); Int(w, "status", d.Status); Uint(w, "runId", d.RunId); Uint(w, "nextSequence", d.NextSequence);
        w.Key(K("config")); w.StartObject(); Int(w, "target", d.Config.Target); BoolArray(w, "models", d.Config.Models, TownDiagnosticData.ModelCount);
        BoolArray(w, "coverage", d.Config.Coverage, TownDiagnosticData.CoverageCount); Number(w, "reducedInfluence", d.Config.ReducedInfluence, "$.diagnostic.config.reducedInfluence");
        Int(w, "underdogPercent", d.Config.UnderdogPercent); Bool(w, "balanceStyleTier", d.Config.BalanceStyleTier); w.EndObject();
        w.Key(K("counters")); w.StartObject(); Int(w, "opened", d.Counters.Opened); Int(w, "completed", d.Counters.Completed); Int(w, "cancelled", d.Counters.Cancelled); Int(w, "aborted", d.Counters.Aborted);
        Int(w, "teamAWins", d.Counters.TeamAWins); Int(w, "teamBWins", d.Counters.TeamBWins); Int(w, "largerWins", d.Counters.LargerWins); Int(w, "smallerWins", d.Counters.SmallerWins);
        Int(w, "equalWins", d.Counters.EqualWins); Int(w, "favoriteWins", d.Counters.FavoriteWins); IntArray(w, "modelCompleted", d.Counters.ModelCompleted, TownDiagnosticData.ModelCount);
        IntArray(w, "coverageCompleted", d.Counters.CoverageCompleted, TownDiagnosticData.CoverageCount); Number(w, "schedulingHours", d.Counters.SchedulingHours, "$.diagnostic.counters.schedulingHours");
        Number(w, "lastCompletedHours", d.Counters.LastCompletedHours, "$.diagnostic.counters.lastCompletedHours");
        Int(w, "schedulingSamples", d.Counters.SchedulingSamples); w.EndObject();
        w.Key(K("pending")); w.StartObject(); Bool(w, "active", d.Pending.Active); Bool(w, "underdog", d.Pending.Underdog); Uint(w, "sequence", d.Pending.Sequence);
        Int(w, "model", d.Pending.Model); Int(w, "requestedCoverage", d.Pending.RequestedCoverage); Int(w, "actualCoverage", d.Pending.ActualCoverage); Int(w, "relaxation", d.Pending.Relaxation);
        Int(w, "sizeA", d.Pending.SizeA); Int(w, "sizeB", d.Pending.SizeB); NumberArray(w, "scoreA", d.Pending.ScoreA, TownDiagnosticData.ModelCount, "$.diagnostic.pending.scoreA");
        NumberArray(w, "scoreB", d.Pending.ScoreB, TownDiagnosticData.ModelCount, "$.diagnostic.pending.scoreB"); NumberArray(w, "probabilityA", d.Pending.ProbabilityA, TownDiagnosticData.ModelCount, "$.diagnostic.pending.probabilityA");
        Number(w, "announcedHours", d.Pending.AnnouncedHours, "$.diagnostic.pending.announcedHours"); w.EndObject(); w.EndObject();
    }

    // C++: ArenaSnapshotJson.cpp:444-471
    static void Write(PrettyWriter w, Snapshot s)
    {
        w.SetIndent(' ', 2); w.StartObject(); Int(w, "version", s.Version); Text(w, "saveKey", s.SaveKey); Text(w, "generation", s.Generation);
        Standings(w, "playerStandings", s.Fighters.PlayerStandings); Standings(w, "townStandings", s.Fighters.TownStandings);
        List<LeaderboardData.Progression> progression = SortedById(s.Fighters.Progression.Select(r => r.Clone()), r => r.Id);
        w.Key(K("progression")); w.StartArray();
        for (int i = 0; i < progression.Count; ++i)
        {
            LeaderboardData.Progression p = progression[i]; w.StartObject(); Text(w, "id", p.Id); Text(w, "name", p.Name); Int(w, "marks", p.Marks); w.Key(K("rewardTiers")); w.StartObject();
            List<RewardProgression.TierEntry> tiers = SortedById(p.RewardTiers, t => t.Id);
            for (int j = 0; j < tiers.Count; ++j) { w.Key(Utf8Bytes(tiers[j].Id)); w.Int(tiers[j].Tier); }
            w.EndObject(); w.EndObject();
        }
        w.EndArray(); List<string> unlocks = SortedById(s.Fighters.FactionUnlocks, u => u); w.Key(K("factionUnlocks")); w.StartArray();
        for (int i = 0; i < unlocks.Count; ++i) Str(w, unlocks[i]); w.EndArray();
        TownChallengePolicy.Card c = s.Challenges; w.Key(K("challenges")); w.StartObject(); Uint(w, "seed", c.Seed); Uint(w, "ambientSeed", c.AmbientSeed); Int(w, "division", c.Division); Uint(w, "milestones", c.Milestones); Uint(w, "uniqueWins", c.UniqueWins); Uint(w, "challengeWins", c.ChallengeWins);
        History(w, "playerHistory", c.PlayerHistory); History(w, "ambientHistory", c.AmbientHistory);
        w.Key(K("offers")); w.StartArray(); for (int i = 0; i < 5; ++i) Offer(w, c.Offers[i]); w.EndArray(); w.Key(K("skarnOffer")); Offer(w, c.SkarnOffer);
        Bool(w, "skarnGenerated", c.SkarnGenerated); Bool(w, "skarnWon", c.SkarnWon); Bool(w, "skarnAttempted", c.SkarnAttempted); Bool(w, "skarnInProgress", c.SkarnInProgress); Number(w, "skarnLastEnd", c.SkarnLastEnd, "$.challenges.skarnLastEnd"); Number(w, "lastChallengeEndHours", c.LastChallengeEndHours, "$.challenges.lastChallengeEndHours"); Bool(w, "hasChallengeEnd", c.HasChallengeEnd); Int(w, "paidRefreshCount", c.PaidRefreshCount); Uint(w, "paidRefreshNonce", c.PaidRefreshNonce); w.EndObject();
        Diagnostic(w, s.Diagnostic); Int(w, "bookieCredit", s.BookieCredit); Int(w, "challengeCredit", s.ChallengeCredit);
        PlannedNpcBout b = s.PlannedNpc;
        w.Key(K("plannedNpc")); w.StartObject(); Bool(w, "active", b.Active); Text(w, "registryId", b.RegistryId);
        Number(w, "startHours", b.StartHours, "$.plannedNpc.startHours");
        Number(w, "scoreA", b.ScoreA, "$.plannedNpc.scoreA"); Number(w, "scoreB", b.ScoreB, "$.plannedNpc.scoreB");
        w.Key(K("teamA")); w.StartArray(); for (int i = 0; i < b.TeamA.Count; ++i) Str(w, b.TeamA[i]); w.EndArray();
        w.Key(K("teamB")); w.StartArray(); for (int i = 0; i < b.TeamB.Count; ++i) Str(w, b.TeamB[i]); w.EndArray();
        Bool(w, "market", b.Market); Bool(w, "wagerPending", b.WagerPending);
        Int(w, "wagerSide", b.WagerSide); Int(w, "stake", b.Stake); Int(w, "payout", b.Payout);
        w.EndObject(); w.EndObject();
    }

    // ---- public API -------------------------------------------------------------------------------------------------------------

    /// <summary>C++: ArenaSnapshotJson.h:28 / .cpp:473-494. Unknown fields, duplicate keys/IDs, invalid UTF-8, nonfinite numbers and
    /// wrong JSON types are rejected; a version below 11 is a legacy reset, above 16 unsupported (the header comment's full list is
    /// in ArenaSnapshotJson.h:7-27). The codec does no I/O, hash sealing, or generation matching.</summary>
    public static LoadResult DecodeSnapshot(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var result = new LoadResult();
        try
        {
            if (json.Length > MaxSnapshotBytes) throw new Failure(LoadCode.InvalidJson, "$", "JSON exceeds the 16 MiB safety limit");
            if (System.Array.IndexOf(json, (byte)0) >= 0) throw new Failure(LoadCode.InvalidJson, "$", "unescaped NUL byte in JSON");
            var reader = new Reader(); var guard = new SyntaxGuard();
            const ParseFlags flags = ParseFlags.ValidateEncoding | ParseFlags.FullPrecision | ParseFlags.Iterative;
            if (reader.Parse(json, 0, json.Length, flags | ParseFlags.NumbersAsStrings, guard).IsError)
            {
                if (guard.Error.Length != 0) throw new Failure(guard.Code, guard.ErrorField, guard.Error);
                string message = GetParseErrorEn(reader.GetParseErrorCode()) + " at byte " + I(reader.GetErrorOffset());
                throw new Failure(LoadCode.InvalidJson, guard.Path(), message);
            }
            var doc = new Document(); doc.Parse(json, 0, json.Length, flags);
            if (doc.HasParseError()) throw new Failure(LoadCode.InvalidJson, "$", GetParseErrorEn(doc.GetParseError()));
            if (!doc.Root.IsObject) Fail("$", "expected a snapshot object");
            int version = Int(doc.Root, "version", "$", 0, int.MaxValue);
            if (version < 11) { result.Code = LoadCode.LegacyReset; result.Field = "$.version"; result.Message = "legacy arena progression must reset; preserve the original sidecar"; return result; }
            if (version > 16) { result.Code = LoadCode.UnsupportedVersion; result.Field = "$.version"; result.Message = "snapshot schema is newer than supported version 16"; return result; }
            var parsed = new Snapshot(); Read(doc.Root, version, parsed); result.Snapshot = parsed; result.Code = LoadCode.Ready;
        }
        catch (Failure f) { result.Code = f.Code; result.Field = f.Field; result.Message = f.Text; }
        return result;
    }

    /// <summary><see cref="DecodeSnapshot(byte[])"/> of the UTF-8 bytes of <paramref name="json"/> (lone surrogates as in the file
    /// header).</summary>
    public static LoadResult DecodeSnapshot(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return DecodeSnapshot(Utf8Bytes(json));
    }

    /// <summary>C++: ArenaSnapshotJson.h:30-31 / .cpp:495-509. On failure, json is unchanged and error includes the field path; on
    /// success error is empty. <paramref name="error"/> is <c>out</c> because the C++ assigns it on every path (error.clear() or
    /// error=...); IStorage's errors are <c>ref</c> because a storage call may leave the caller's earlier message, which the C++
    /// repository then reports.</summary>
    public static bool EncodeSnapshot(Snapshot value, ref byte[] json, out string error)
    {
        try
        {
            if (value.Version != 16) Fail("$.version", "only schema version 16 can be written");
            var writer = new PrettyWriter(); Write(writer, value);
            var encodedList = new List<byte>(writer.GetBytes()); encodedList.Add((byte)'\n');
            byte[] encoded = encodedList.ToArray();
            // One validation contract for edited files and runtime snapshots. Never
            // publish malformed live state, including duplicate IDs or tier entries.
            LoadResult @checked = DecodeSnapshot(encoded);
            if (@checked.Code != LoadCode.Ready || @checked.Snapshot.DiagnosticError.Length != 0)
            {
                error = @checked.Code != LoadCode.Ready ? @checked.Field + ": " + @checked.Message : @checked.Snapshot.DiagnosticError;
                return false;
            }
            json = encoded; error = ""; return true;
        }
        catch (Failure f) { error = f.Field + ": " + f.Text; return false; }
    }
}
