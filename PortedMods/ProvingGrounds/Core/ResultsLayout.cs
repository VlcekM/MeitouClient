using System.Text;

namespace ProvingGrounds.Core;

/// <summary>Pure geometry adapter for the match-results presentation. Keeping the coordination here makes ResultsUI consume the same
/// tested ceremonial rules as the leaderboards without duplicating MyGUI-independent layout decisions.
/// C++: ResultsLayout.h (header-only, namespace <c>ResultsLayout</c>).</summary>
public static class ResultsLayout
{
    /// <summary>The first character of a UTF-8 name, as the original's portrait initial. Works on the BYTES of a NUL-terminated C
    /// string: a 0 byte ends the string, and reading past the end of the span behaves as if a NUL followed (so a truncated sequence
    /// gives "?", never an exception). A null C pointer / an empty span gives "?".
    /// <para>Every sequence this accepts is well-formed UTF-8, so decoding the returned bytes into a C# string is lossless (the golden
    /// test re-encodes it and compares the bytes). A single byte 0x01..0x7f is returned as that one character. Accepted:
    /// <list type="bullet">
    /// <item>lead 0xc2..0xdf plus one continuation byte;</item>
    /// <item>lead 0xe0..0xef plus two; 0xe0 needs a second byte of at least 0xa0 (no overlongs), 0xed one below 0xa0 (no surrogates);</item>
    /// <item>lead 0xf0..0xf4 plus three; 0xf0 needs a second byte of at least 0x90, 0xf4 one below 0x90 (nothing past U+10FFFF).</item>
    /// </list></para></summary>
    // C++: ResultsLayout.h:11-39
    public static string PortraitInitialUtf8(ReadOnlySpan<byte> name)
    {
        if (At(name, 0) == 0)   // !name || !name[0]
            return "?";

        byte lead = name[0];
        int length;
        if (lead <= 0x7f) length = 1;
        else if (lead >= 0xc2 && lead <= 0xdf) length = 2;
        else if (lead >= 0xe0 && lead <= 0xef) length = 3;
        else if (lead >= 0xf0 && lead <= 0xf4) length = 4;
        else return "?";

        for (int i = 1; i < length; ++i)
        {
            byte b = At(name, i);
            if (b == 0 || (b & 0xc0) != 0x80)
                return "?";
        }

        // Reject overlong sequences, UTF-16 surrogate code points and values
        // beyond Unicode's upper bound before MyGUI validates the caption.
        if ((lead == 0xe0 && At(name, 1) < 0xa0) ||
            (lead == 0xed && At(name, 1) >= 0xa0) ||
            (lead == 0xf0 && At(name, 1) < 0x90) ||
            (lead == 0xf4 && At(name, 1) >= 0x90))
            return "?";

        return Encoding.UTF8.GetString(name[..length]);   // std::string(name, length)
    }

    /// <summary>The glue's entry: a null name gives "?", otherwise the name is encoded as UTF-8 and handed to the byte version. A C#
    /// string cannot hold the original's malformed UTF-8; a lone surrogate (<c>"\uD800"</c>) is encoded by .NET as EF BF BD, so it reads
    /// as the character U+FFFD (see docs/divergences.md).</summary>
    // C++: ResultsLayout.h:11-39 (the <c>!name</c> test and the call)
    public static string PortraitInitialUtf8(string? name)
    {
        if (name == null)
            return "?";
        return PortraitInitialUtf8(Encoding.UTF8.GetBytes(name));
    }

    /// <summary>The byte at <paramref name="index"/>, or 0 past the end (the C string's terminator).</summary>
    static byte At(ReadOnlySpan<byte> bytes, int index) => index < bytes.Length ? bytes[index] : (byte)0;

    /// <summary>C++: ResultsLayout.h:41-46 (returned by value; all zeros by default; holds a Podium). Do not call
    /// Equals/GetHashCode/Assert.Equal on it (.NET 10 throws NotSupportedException for a struct holding an InlineArray, and the C++ has no
    /// operator==): compare fields, or each <c>x.Podium.Slots[i]</c> (a PodiumSlot holds no arrays, so its own Equals works).</summary>
    public struct Ceremony
    {
        public LeaderboardLayout.CeremonialMetrics Metrics;
        public LeaderboardLayout.TitleRails Title;
        public LeaderboardLayout.Podium Podium;
    }

    // C++: ResultsLayout.h:48-64
    public static Ceremony FitCeremony(int availableWidth, int gap, int body, int resultCount, int measuredTitleWidth)
    {
        Ceremony result = default;
        result.Metrics = LeaderboardLayout.FitCeremonialMetrics(body, gap);
        result.Title = LeaderboardLayout.FitTitleRails(
            availableWidth, gap, measuredTitleWidth);
        result.Podium = LeaderboardLayout.FitPodium(
            availableWidth, gap, resultCount,
            result.Metrics.ChampionPortrait,
            result.Metrics.OtherPortrait);
        return result;
    }
}
