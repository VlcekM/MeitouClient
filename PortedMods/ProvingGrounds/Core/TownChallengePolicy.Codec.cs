using System.Globalization;
using System.Text;
using static ProvingGrounds.Core.TownArenaPolicy;

namespace ProvingGrounds.Core;

// The compact text codec of a Card (TownChallengePolicy.h:228-317 and :476-572).
// In the shipped 1.3.4 plugin it has no caller outside its own header: the v16 save sidecar stores the card as JSON
// (ArenaSnapshotJson.cpp:457-460, `w.Key("challenges")`). It is ported because the upstream test uses it and the plan's golden list
// names Encode/Decode.
//
// Strings: the C++ codec works on the bytes of std::string. Hex writes the UTF-8 bytes of the C# string (identical to the C++ text
// for ASCII and valid UTF-8). Unhex can produce bytes that are not valid UTF-8, which a C# string cannot hold: the port decodes the
// bytes strictly and treats invalid UTF-8 as a parse failure (docs/divergences.md).
//
// Numbers: iostream `operator<<` / `operator>>` are reproduced by Format* below and by CStream (libstdc++ rules, the "C" locale):
// bool prints 1/0 and reads only 0/1 (noboolalpha, read through a long); `setprecision(17)` on a double is C `%.17g`; ints read an
// optional sign and decimal digits and fail on overflow; `>> std::string` reads one whitespace-delimited word. Inputs where libstdc++
// and the shipped MSVC build might differ are listed in docs/divergences.md.
public static partial class TownChallengePolicy
{
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static string B(bool v) => v ? "1" : "0";
    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
    static string U(uint v) => v.ToString(CultureInfo.InvariantCulture);

    // C++: TownChallengePolicy.h:228-239
    public static string Hex(string value)
    {
        if (value.Length == 0) return "-";
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var result = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; ++i)
        {
            byte c = bytes[i];
            result.Append("0123456789abcdef"[c >> 4]);
            result.Append("0123456789abcdef"[c & 15]);
        }
        return result.ToString();
    }

    /// <summary>As the C++, plus: decoded bytes that are not valid UTF-8 make it return false (a C# string cannot hold them).
    /// <paramref name="result"/> is cleared first (as the C++ <c>result.clear()</c>); on failure it holds no meaningful value.</summary>
    // C++: TownChallengePolicy.h:240-257
    public static bool Unhex(string value, ref string result)
    {
        result = "";
        if (value == "-") return true;
        // Length counts chars where the C++ counts bytes: same result, since any non-ASCII char fails the hex check below anyway.
        if (value.Length > 2048 || value.Length % 2 != 0) return false;
        var bytes = new byte[value.Length / 2];
        for (int i = 0; i < value.Length; i += 2)
        {
            uint b = 0;
            for (int j = 0; j < 2; ++j)
            {
                char c = value[i + j];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
                b = b * 16 + (uint)(c <= '9' ? c - '0' : c - 'a' + 10);
            }
            bytes[i / 2] = (byte)b;
        }
        try { result = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return false; }   // not in the C++ (see the file comment)
        return true;
    }

    // C++: TownChallengePolicy.h:258-273
    public static string EncodeLegacy(Card card)
    {
        var o_ = new StringBuilder();
        o_.Append(I(5)).Append(' ').Append(U(card.Seed)).Append(' ').Append(U(card.Milestones)).Append(' ').Append(U(card.UniqueWins))
            .Append(' ').Append(U(card.ChallengeWins))
            .Append(' ').Append(B(card.SkarnGenerated)).Append(' ').Append(B(card.SkarnWon)).Append(' ').Append(B(card.SkarnAttempted))
            .Append(' ').Append(B(card.SkarnInProgress)).Append(' ').Append(CMath.FormatG(card.SkarnLastEnd, 17));   // setprecision(17)
        for (int i = 0; i < 6; ++i)
        {
            ref readonly Offer o = ref (i == 5 ? ref card.SkarnOffer : ref card.Offers[i]);
            o_.Append(' ').Append(I(o.Epoch)).Append(' ').Append(I(o.Encounter)).Append(' ').Append(I((int)o.State)).Append(' ')
                .Append(Hex(o.Opponent)).Append(' ').Append(Hex(o.Name)).Append(' ').Append(I(o.Format)).Append(' ')
                .Append(Hex(o.SecondOpponent)).Append(' ').Append(Hex(o.SecondName)).Append(' ')
                .Append(Hex(o.ThirdOpponent)).Append(' ').Append(Hex(o.ThirdName));
        }
        return o_.ToString();
    }

    /// <summary><c>card = parsed</c> on success copies into <paramref name="card"/> (<see cref="Card.AssignFrom"/>); on failure the card
    /// is untouched.</summary>
    // C++: TownChallengePolicy.h:274-317
    public static bool DecodeLegacy(string value, Card card)
    {
        var @in = new CStream(value);
        Card parsed = new Card();
        int version;
        if (!(@in.Int(out version) && @in.UInt(out parsed.Seed)) || version < 1 || version > 5) return false;
        if (version >= 5)
        {
            if (!(@in.UInt(out parsed.Milestones) && @in.UInt(out parsed.UniqueWins) && @in.UInt(out parsed.ChallengeWins)) ||
                parsed.Milestones > 7 || parsed.UniqueWins > AllUniqueVictoriesBits || parsed.ChallengeWins > 1000000000u) return false;
        }
        else if (version >= 2 && (!(@in.UInt(out parsed.Milestones) && @in.UInt(out parsed.ChallengeWins)) ||
            parsed.Milestones > 7 || parsed.ChallengeWins > 1000000000u)) return false;
        if (version >= 4 && (!(@in.Bool(out parsed.SkarnGenerated) && @in.Bool(out parsed.SkarnWon) && @in.Bool(out parsed.SkarnAttempted) &&
            @in.Bool(out parsed.SkarnInProgress) && @in.Double(out parsed.SkarnLastEnd)) || !(parsed.SkarnLastEnd >= 0.0) ||
            parsed.SkarnLastEnd > 24000000.0)) return false;
        for (int i = 0; i < (version >= 4 ? 6 : 5); ++i)
        {
            ref Offer o = ref At(parsed, i);
            int state;
            string id, name;
            if (!(@in.Int(out o.Epoch) && @in.Int(out o.Encounter) && @in.Int(out state) && @in.Word(out id) && @in.Word(out name)) ||
                o.Epoch < -2 || o.Epoch > 1999999 || o.Encounter < 0 || o.Encounter > (i == 5 ? (int)Encounter.Skarn : (int)Encounter.Veyr) ||
                state < 0 || state > 2 || !Unhex(id, ref o.Opponent) || !Unhex(name, ref o.Name)) return false;
            o.State = (OfferState)state;
            if (version >= 3)
            {
                if (!(@in.Int(out o.Format) && @in.Word(out id) && @in.Word(out name)) || o.Format < 0 || o.Format > (i == 5 ? 6 : 5) ||
                    !Unhex(id, ref o.SecondOpponent) || !Unhex(name, ref o.SecondName)) return false;
                if (Opponents(o) == 1 && (o.SecondOpponent.Length != 0 || o.SecondName.Length != 0)) return false;
                if (o.Opponent.Length != 0 && o.Opponent == o.SecondOpponent) return false;
            }
            if (version >= 4)
            {
                if (!(@in.Word(out id) && @in.Word(out name)) || !Unhex(id, ref o.ThirdOpponent) || !Unhex(name, ref o.ThirdName)) return false;
                if (Opponents(o) < 3 && (o.ThirdOpponent.Length != 0 || o.ThirdName.Length != 0)) return false;
                if (o.ThirdOpponent.Length != 0 && (o.ThirdOpponent == o.Opponent || o.ThirdOpponent == o.SecondOpponent)) return false;
                if (i == 5 && parsed.SkarnGenerated && o.Encounter != 5) return false;
            }
            if (i < 5 && o.Encounter == (int)Encounter.Skarn && !SkarnUnlocked(parsed)) return false;
        }
        if (@in.Word(out _)) return false;   // std::string extra; if (in >> extra) return false;
        card.AssignFrom(parsed);
        return true;
    }

    /// <summary><c>out &lt;&lt; ...</c> into an <c>std::ostream&amp;</c>: appends to <paramref name="out"/>.</summary>
    // C++: TownChallengePolicy.h:476-482
    public static void WriteHistory(StringBuilder @out, List<List<string>> history)
    {
        @out.Append(' ').Append(history.Count.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < history.Count; ++i)
        {
            @out.Append(' ').Append(history[i].Count.ToString(CultureInfo.InvariantCulture));
            for (int j = 0; j < history[i].Count; ++j) @out.Append(' ').Append(Hex(history[i][j]));
        }
    }

    /// <summary>Reads from the stream <paramref name="in"/> and APPENDS bouts to <paramref name="history"/> (the C++ never clears it; a
    /// failure can leave the bouts read so far appended).</summary>
    // C++: TownChallengePolicy.h:483-499
    public static bool ReadHistory(CStream @in, List<List<string>> history)
    {
        int count;
        if (!@in.Int(out count) || count < 0 || count > 5) return false;
        for (int i = 0; i < count; ++i)
        {
            int members;
            if (!@in.Int(out members) || members < 1 || members > 8) return false;
            var bout = new List<string>();
            for (int j = 0; j < members; ++j)
            {
                string encoded, id = "";
                if (!@in.Word(out encoded) || !Unhex(encoded, ref id) || id.Length == 0) return false;
                for (int k = 0; k < bout.Count; ++k) if (bout[k] == id) return false;
                bout.Add(id);
            }
            history.Add(bout);
        }
        return true;
    }

    // C++: TownChallengePolicy.h:500-519
    public static string Encode(Card card)
    {
        Card carrier = card.Clone();
        for (int i = 0; i < 5; ++i)
            if (carrier.Offers[i].Generated)
            {
                ClearPreview(ref carrier.Offers[i]); carrier.Offers[i].Generated = false;
                carrier.Offers[i].Format = 0;
            }
        var @out = new StringBuilder();
        @out.Append("8 ").Append(EncodeLegacy(carrier)).Append(" MM ").Append(I(card.Division)).Append(' ').Append(U(card.AmbientSeed));
        for (int i = 0; i < 5; ++i)
        {
            ref readonly Offer o = ref card.Offers[i];
            @out.Append(' ').Append(B(o.Generated)).Append(' ').Append(U(o.Draw)).Append(' ').Append(I(o.Division)).Append(' ')
                .Append(I(o.PlayerCount)).Append(' ').Append(I(o.EnemyCount)).Append(' ').Append(Hex(o.Context));
            for (int j = 0; j < 4; ++j)
                @out.Append(' ').Append(Hex(OpponentIdConst(o, j))).Append(' ').Append(Hex(OpponentNameConst(o, j))).Append(' ').Append(Hex(o.Roles[j]));
        }
        WriteHistory(@out, card.PlayerHistory); WriteHistory(@out, card.AmbientHistory);
        for (int i = 0; i < 5; ++i) @out.Append(' ').Append(I(card.Offers[i].Mode));
        return @out.ToString();
    }

    /// <summary>On success copies into <paramref name="card"/>; on failure the card is untouched. <c>value.size() &gt; 100000</c> counts
    /// UTF-8 bytes, as the C++ counts the std::string's bytes.</summary>
    // C++: TownChallengePolicy.h:520-572
    public static bool Decode(string value, Card card)
    {
        bool version5 = value.StartsWith("5 ", StringComparison.Ordinal);   // value.compare(0, 2, "5 ") == 0
        bool version6 = value.StartsWith("6 ", StringComparison.Ordinal);
        bool version7 = value.StartsWith("7 ", StringComparison.Ordinal);
        bool version8 = value.StartsWith("8 ", StringComparison.Ordinal);
        if (!version5 && !version6 && !version7 && !version8) return DecodeLegacy(value, card);
        if (Encoding.UTF8.GetByteCount(value) > 100000) return false;
        int marker = value.IndexOf(" MM ", StringComparison.Ordinal);
        if (marker < 0) return false;
        Card parsed = new Card();
        // value.substr(2, marker - 2): with marker 1 ("8 MM ...") the size_t count wraps and substr takes the rest of the string
        if (!DecodeLegacy(marker >= 2 ? value.Substring(2, marker - 2) : value.Substring(2), parsed)) return false;
        var @in = new CStream(value.Substring(marker + 4));
        if (!(@in.Int(out parsed.Division) && @in.UInt(out parsed.AmbientSeed)) || parsed.Division < 0 || parsed.Division > 2) return false;
        for (int i = 0; i < 5; ++i)
        {
            ref Offer o = ref parsed.Offers[i];
            Offer legacyOffer = o;
            int generated; string context;
            if (!(@in.Int(out generated) && @in.UInt(out o.Draw) && @in.Int(out o.Division) && @in.Int(out o.PlayerCount) &&
                  @in.Int(out o.EnemyCount) && @in.Word(out context)) ||
                generated < 0 || generated > 1 || o.Draw >= 10000 || o.Division < 0 || o.Division > 2 ||
                o.PlayerCount < 0 || o.PlayerCount > 3 || o.EnemyCount < 0 || o.EnemyCount > (version8 ? 4 : 3) ||
                !Unhex(context, ref o.Context)) return false;
            o.Generated = generated != 0;
            for (int j = 0; j < (version8 ? 4 : 3); ++j)
            {
                string id, name, role;
                if (!(@in.Word(out id) && @in.Word(out name) && @in.Word(out role)) || !Unhex(id, ref OpponentId(ref o, j)) ||
                    !Unhex(name, ref OpponentName(ref o, j)) || !Unhex(role, ref o.Roles[j])) return false;
                if (!o.Generated && (OpponentIdConst(o, j) != OpponentIdConst(legacyOffer, j) ||
                    OpponentNameConst(o, j) != OpponentNameConst(legacyOffer, j))) return false;
                if (o.Generated)
                {
                    if (j < o.EnemyCount && (OpponentIdConst(o, j).Length == 0 || o.Roles[j].Length == 0)) return false;
                    if (j >= o.EnemyCount && (OpponentIdConst(o, j).Length != 0 || OpponentNameConst(o, j).Length != 0 || o.Roles[j].Length != 0)) return false;
                }
                for (int k = 0; k < j; ++k)
                    if (OpponentIdConst(o, j).Length != 0 && OpponentIdConst(o, j) == OpponentIdConst(o, k)) return false;
            }
            if (o.Generated && ((o.PlayerCount == 0) != (o.EnemyCount == 0) ||
                (o.EnemyCount > 0 && o.Context.Length == 0))) return false;
            if (o.Generated && o.State == OfferState.Booked && !Resolved(o)) return false;
        }
        if (!ReadHistory(@in, parsed.PlayerHistory) || !ReadHistory(@in, parsed.AmbientHistory)) return false;
        if (version6 || version7 || version8)
            for (int i = 0; i < 5; ++i)
            {
                int savedMode;
                if (!@in.Int(out savedMode) || savedMode < (int)ChallengeMode.Teams || savedMode > 2) return false;
                // Version 6 briefly shipped FFA as value 2. Retire it without
                // rejecting those saves; the card becomes a normal team fight.
                parsed.Offers[i].Mode = savedMode == 2 ? (int)ChallengeMode.Teams : savedMode;
            }
        if (@in.Word(out _)) return false;   // std::string extra; if (in >> extra) return false;
        card.AssignFrom(parsed);
        return true;
    }

    /// <summary>The <c>std::istringstream</c> the codec reads (libstdc++ <c>operator&gt;&gt;</c> in the "C" locale, default flags: decimal,
    /// skipws, noboolalpha). Each read skips leading whitespace (space, \t, \n, \v, \f, \r); a failed read sets the fail state, after which
    /// every read fails (the C++ failbit). A read that stops at the end of the text succeeds (eofbit alone is not a failure); the next
    /// read then fails. Not built: hex/octal bases, locales, grouping (the codec uses none).</summary>
    public sealed class CStream
    {
        readonly string _text;
        int _pos;
        bool _fail;

        public CStream(string text) { _text = text; }

        /// <summary>What is left unread (for tests).</summary>
        public string Rest => _text.Substring(_pos);

        static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';

        bool Begin()
        {
            if (_fail) return false;
            while (_pos < _text.Length && IsSpace(_text[_pos])) ++_pos;
            if (_pos >= _text.Length) { _fail = true; return false; }   // the sentry hits end of stream
            return true;
        }

        // libstdc++ __num_get::_M_extract_int (decimal): an optional sign, then digits. No digit, or a magnitude above max -> fail.
        bool Integer(ulong maxPositive, ulong maxNegative, out bool negative, out ulong magnitude)
        {
            negative = false; magnitude = 0;
            if (!Begin()) return false;
            char c = _text[_pos];
            // A sign may be consumed before the read fails (as num_get does); harmless, because failure is sticky like failbit.
            if (c == '-' || c == '+') { negative = c == '-'; ++_pos; }
            ulong limit = negative ? maxNegative : maxPositive;
            bool any = false, overflow = false;
            while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9')
            {
                any = true;
                ulong d = (ulong)(_text[_pos] - '0');
                if (!overflow && (magnitude > (limit - d) / 10)) overflow = true;
                if (!overflow) magnitude = magnitude * 10 + d;
                ++_pos;
            }
            if (!any || overflow) { _fail = true; return false; }
            return true;
        }

        /// <summary><c>&gt;&gt; int</c>: libstdc++ reads a <c>long</c> and fails outside the int range.</summary>
        public bool Int(out int value)
        {
            value = 0;
            if (!Integer(int.MaxValue, 1UL + int.MaxValue, out bool negative, out ulong magnitude)) return false;
            value = negative ? (int)(-(long)magnitude) : (int)magnitude;
            return true;
        }

        /// <summary><c>&gt;&gt; unsigned</c>: a magnitude above UINT_MAX fails; a '-' sign negates modulo 2^32 (libstdc++; see
        /// docs/divergences.md for MSVC).</summary>
        public bool UInt(out uint value)
        {
            value = 0;
            if (!Integer(uint.MaxValue, uint.MaxValue, out bool negative, out ulong magnitude)) return false;
            value = negative ? unchecked((uint)(0UL - magnitude)) : (uint)magnitude;
            return true;
        }

        /// <summary><c>&gt;&gt; bool</c> with noboolalpha: reads a long; 0 and 1 only, anything else fails.</summary>
        public bool Bool(out bool value)
        {
            value = false;
            if (!Integer(long.MaxValue, 1UL + long.MaxValue, out bool negative, out ulong magnitude)) return false;
            if (magnitude == 0) { value = false; return true; }   // "0", "-0", "+0"
            if (magnitude == 1 && !negative) { value = true; return true; }
            _fail = true;
            return false;
        }

        /// <summary><c>&gt;&gt; double</c>: libstdc++ __num_get::_M_extract_float collects [sign] digits [. digits] [e|E [sign] digits]
        /// ('e' only after a mantissa digit), then strtod must consume all of it. An overflow (infinity) fails.</summary>
        public bool Double(out double value)
        {
            value = 0;
            if (!Begin()) return false;
            int start = _pos;
            if (_text[_pos] == '+' || _text[_pos] == '-') ++_pos;
            bool mantissa = false, dot = false;
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c >= '0' && c <= '9') { mantissa = true; ++_pos; }
                else if (c == '.' && !dot) { dot = true; ++_pos; }
                else break;
            }
            bool expDigits = true;
            if (_pos < _text.Length && (_text[_pos] == 'e' || _text[_pos] == 'E') && mantissa)
            {
                ++_pos;
                if (_pos < _text.Length && (_text[_pos] == '+' || _text[_pos] == '-')) ++_pos;
                expDigits = false;
                while (_pos < _text.Length && _text[_pos] >= '0' && _text[_pos] <= '9') { expDigits = true; ++_pos; }
            }
            string collected = _text.Substring(start, _pos - start);
            if (!mantissa || !expDigits ||
                !double.TryParse(collected, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
                double.IsInfinity(parsed))
            { _fail = true; return false; }
            value = parsed;
            return true;
        }

        /// <summary><c>&gt;&gt; std::string</c>: one word up to the next whitespace (or the end).</summary>
        public bool Word(out string value)
        {
            value = "";
            if (!Begin()) return false;
            int start = _pos;
            while (_pos < _text.Length && !IsSpace(_text[_pos])) ++_pos;
            value = _text.Substring(start, _pos - start);
            return true;
        }
    }
}
