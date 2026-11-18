// Port of upstream/ProvingGrounds/plugin/src/RewardProgression.h (Proving Grounds by Shutori, GPLv3).
using System.Globalization;
using System.Text;

namespace ProvingGrounds.Core;

/// <summary>Per-fighter reward tiers and faction unlocks, and their length-prefixed text form (PA §2.15, §2.18). Header-only in the
/// C++ (RewardProgression.h:1-201).
/// <para>The C++ works on <c>std::string</c> bytes; this port on C# chars. Lengths and offsets are therefore UTF-16 code units,
/// identical to bytes for ASCII (every stable id and unlock id the original writes is ASCII). See docs/divergences.md.</para>
/// <para>Number parsing follows the shipped MSVC x64 build: <c>std::strtoul</c> returns a 32-bit <c>unsigned long</c> and saturates
/// at 4294967295, <c>std::atoi</c> saturates at INT_MAX / INT_MIN (glibc differs on overflow, which is why the golden vectors never
/// hold a run of 10+ digits; the overflow cases are hand tests).</para></summary>
public static class RewardProgression
{
    /// <summary>C++: RewardProgression.h:10-17. A value type, copied by value like the C++ (a write goes back through the list
    /// indexer). <c>new TierEntry()</c> gives the C++ default (empty id, tier -1); note <c>default(TierEntry)</c> does not.</summary>
    public struct TierEntry
    {
        public string Id;
        public int Tier;

        // C++: RewardProgression.h:15
        public TierEntry() { Id = ""; Tier = -1; }
        // C++: RewardProgression.h:16
        public TierEntry(string value, int level) { Id = value; Tier = level; }
    }

    // C++: RewardProgression.h:19-27
    public static int HighestTier(
        List<TierEntry> entries,
        string id)
    {
        for (int i = 0; i < entries.Count; ++i)
            if (entries[i].Id == id)
                return entries[i].Tier;
        return -1;
    }

    // C++: RewardProgression.h:29-51
    public static bool SetHighestTier(
        List<TierEntry> entries,
        string id,
        int expectedPrevious,
        int nextTier)
    {
        // expectedPrevious + 1 at INT_MAX is signed overflow in C++ (MSVC wraps); C# wraps the same way.
        if (id.Length == 0 || nextTier < 0 ||
            (expectedPrevious >= 0 && nextTier != unchecked(expectedPrevious + 1)))
            return false;
        for (int i = 0; i < entries.Count; ++i)
        {
            if (entries[i].Id != id)
                continue;
            if (entries[i].Tier != expectedPrevious)
                return false;
            entries[i] = new TierEntry(entries[i].Id, nextTier);
            return true;
        }
        if (expectedPrevious != -1)
            return false;
        entries.Add(new TierEntry(id, nextTier));
        return true;
    }

    // C++: RewardProgression.h:53-72
    public static bool RestoreHighestTier(
        List<TierEntry> entries,
        string id,
        int tier)
    {
        for (int i = 0; i < entries.Count; ++i)
        {
            if (entries[i].Id != id)
                continue;
            if (tier < 0)
                entries.RemoveAt(i);
            else
                entries[i] = new TierEntry(entries[i].Id, tier);
            return true;
        }
        if (tier < 0 || id.Length == 0)
            return false;
        entries.Add(new TierEntry(id, tier));
        return true;
    }

    // C++: RewardProgression.h:74-82
    public static bool HasUnlock(
        List<string> unlocks,
        string id)
    {
        for (int i = 0; i < unlocks.Count; ++i)
            if (unlocks[i] == id)
                return true;
        return false;
    }

    // C++: RewardProgression.h:84-92
    public static bool AddUnlock(
        List<string> unlocks,
        string id)
    {
        if (id.Length == 0 || HasUnlock(unlocks, id))
            return false;
        unlocks.Add(id);
        return true;
    }

    // C++: RewardProgression.h:94-106
    public static bool RemoveUnlock(
        List<string> unlocks,
        string id)
    {
        for (int i = 0; i < unlocks.Count; ++i)
        {
            if (unlocks[i] != id)
                continue;
            unlocks.RemoveAt(i);
            return true;
        }
        return false;
    }

    // C++: RewardProgression.h:108-115
    public static string EncodeTiers(List<TierEntry> entries)
    {
        var @out = new StringBuilder();
        for (int i = 0; i < entries.Count; ++i)
            @out.Append(entries[i].Id.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(entries[i].Id).Append('=')
                .Append(entries[i].Tier.ToString(CultureInfo.InvariantCulture)).Append(';');
        return @out.ToString();
    }

    // C++: RewardProgression.h:117-132
    static bool ReadLength(
        string text,
        ref int cursor,
        out long length)
    {
        length = 0;   // the C++ leaves length unchanged on the early returns; every caller passes 0 and stops on false
        int colon = text.IndexOf(':', cursor);
        if (colon == -1 || colon == cursor)
            return false;
        for (int i = cursor; i < colon; ++i)
            if (text[i] < '0' || text[i] > '9')
                return false;
        length = StrToULongMsvc(text.Substring(cursor, colon - cursor));
        cursor = colon + 1;
        return cursor + length <= text.Length;
    }

    // C++: RewardProgression.h:134-171
    public static bool DecodeTiers(
        string text,
        List<TierEntry> entries)
    {
        var parsed = new List<TierEntry>();
        int cursor = 0;
        while (cursor < text.Length)
        {
            if (!ReadLength(text, ref cursor, out long length))
                return false;
            string id = text.Substring(cursor, (int)length);
            cursor += (int)length;
            if (id.Length == 0 || cursor >= text.Length || text[cursor++] != '=')
                return false;
            int end = text.IndexOf(';', cursor);
            if (end == -1 || end == cursor)
                return false;
            int tier = AtoiMsvc(text.Substring(cursor, end - cursor));
            if (tier < 0)
                return false;
            bool replaced = false;
            for (int i = 0; i < parsed.Count; ++i)
            {
                if (parsed[i].Id == id)
                {
                    parsed[i] = new TierEntry(parsed[i].Id, tier);
                    replaced = true;
                    break;
                }
            }
            if (!replaced)
                parsed.Add(new TierEntry(id, tier));
            cursor = end + 1;
        }
        // entries.swap(parsed)
        entries.Clear();
        entries.AddRange(parsed);
        return true;
    }

    // C++: RewardProgression.h:173-179
    public static string EncodeUnlocks(List<string> unlocks)
    {
        var @out = new StringBuilder();
        for (int i = 0; i < unlocks.Count; ++i)
            @out.Append(unlocks[i].Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(unlocks[i]).Append(';');
        return @out.ToString();
    }

    // C++: RewardProgression.h:181-200
    public static bool DecodeUnlocks(
        string text,
        List<string> unlocks)
    {
        var parsed = new List<string>();
        int cursor = 0;
        while (cursor < text.Length)
        {
            if (!ReadLength(text, ref cursor, out long length))
                return false;
            string id = text.Substring(cursor, (int)length);
            cursor += (int)length;
            if (id.Length == 0 || cursor >= text.Length || text[cursor++] != ';')
                return false;
            AddUnlock(parsed, id);
        }
        // unlocks.swap(parsed)
        unlocks.Clear();
        unlocks.AddRange(parsed);
        return true;
    }

    /// <summary><c>std::strtoul(digits, NULL, 10)</c> as MSVC x64 runs it (32-bit unsigned long, ERANGE -> ULONG_MAX). The caller
    /// (ReadLength) has already checked that every char is a digit, so the whitespace/sign handling of strtoul is never reached.</summary>
    static long StrToULongMsvc(string digits)
    {
        const long ULongMax = uint.MaxValue;
        long value = 0;
        foreach (char c in digits)
        {
            value = value * 10 + (c - '0');
            if (value > ULongMax)
                return ULongMax;
        }
        return value;
    }

    /// <summary><c>std::atoi(s)</c> as MSVC runs it: skip leading C-locale whitespace (space, \t \n \v \f \r), an optional sign, then
    /// decimal digits up to the first other char (none read = 0); saturates at INT_MAX / INT_MIN on overflow.</summary>
    internal static int AtoiMsvc(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\v' || s[i] == '\f' || s[i] == '\r'))
            ++i;
        bool negative = false;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            negative = s[i] == '-';
            ++i;
        }
        long value = 0;
        bool overflow = false;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9')
        {
            if (!overflow)
            {
                value = value * 10 + (s[i] - '0');
                if (value > (long)int.MaxValue + 1)
                    overflow = true;
            }
            ++i;
        }
        if (negative)
        {
            if (overflow || -value < int.MinValue) return int.MinValue;
            return (int)-value;
        }
        if (overflow || value > int.MaxValue) return int.MaxValue;
        return (int)value;
    }
}
