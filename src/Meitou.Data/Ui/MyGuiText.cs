namespace Meitou.Data.Ui;

/// <summary>
/// The characters MyGUI shows for a game string (docs/formats/mygui.md "Text of game strings"). The game hands its byte strings to
/// <c>MyGUI::UString</c>, whose UTF-8 reading never fails: a byte that does not start a well-formed sequence is shown as the code point of its value
/// (Latin-1). FCS strings keep such bytes as escapes (<see cref="Fcs.LosslessUtf8"/>), so text is drawn through <see cref="Runes"/> rather than
/// <see cref="string.EnumerateRunes"/>, which would show U+FFFD for them. Text without escapes reads the same either way.
/// </summary>
public static class MyGuiText
{
    /// <summary>
    /// The first and last escape of a byte (<see cref="Fcs.LosslessUtf8"/>: U+DC00 + byte, for the bytes 0x80 to 0xFF), and the last escape of a
    /// continuation byte (0x80 to 0xBF).
    /// </summary>
    const char FirstEscape = '\uDC80', LastEscape = '\uDCFF', LastContinuation = '\uDCBF';

    /// <summary>
    /// The characters of <paramref name="text"/> as MyGUI reads them, each with the number of UTF-16 units of <paramref name="text"/> it takes.
    /// </summary>
    public static MyGuiRuneEnumerator Runes(string text) => new(text);

    /// <summary>
    /// The character starting at <paramref name="index"/> of <paramref name="text"/> as MyGUI reads it: an escaped byte with the escapes that
    /// continue it, a surrogate pair, or one unit. A lone surrogate that is no escaped byte (it cannot come from game bytes) is U+FFFD, as .NET
    /// shows it.
    /// </summary>
    public static MyGuiRune RuneAt(string text, int index)
    {
        char c = text[index];
        if (!char.IsSurrogate(c)) return new(c, 1);

        bool paired = char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);
        if (paired) return new(char.ConvertToUtf32(c, text[index + 1]), 2);
        if (c is < FirstEscape or > LastEscape) return new(0xFFFD, 1);

        return Escaped(text, index, c - 0xDC00);
    }

    /// <summary>
    /// <b>Verified</b> (MyGUIEngine_x64.dll, <c>UString::assign(const std::string&amp;)</c> and its UTF-8 helpers, disassembled): the lead byte
    /// <paramref name="lead"/> gives the sequence length (two to six bytes by its high bits; one for ASCII, a continuation byte, 0xFE and 0xFF).
    /// When as many continuation bytes follow, their low six bits make the code point, with no check for overlong forms, surrogates or range; when
    /// one is missing (another byte, or the string ends) the lead byte alone is the code point of its value. Only escapes can be continuation bytes
    /// here: a character that decoded as UTF-8 starts with a lead byte.
    /// </summary>
    static MyGuiRune Escaped(string text, int index, int lead)
    {
        int length = SequenceLength(lead);
        if (length == 1) return new(lead, 1);

        int value = lead & (0x7F >> length);
        for (int k = 1; k < length; k++)
        {
            int at = index + k;
            if (at >= text.Length || text[at] is < FirstEscape or > LastContinuation) return new(lead, 1);
            value = (value << 6) | ((text[at] - 0xDC00) & 0x3F);
        }

        return new(StoredValue(value), length);
    }

    /// <summary>
    /// MyGUI's UTF-8 sequence length for a lead byte: by the count of its leading one bits, 1 for ASCII, continuation bytes, 0xFE and 0xFF.
    /// </summary>
    static int SequenceLength(int lead) => lead switch
    {
        < 0x80 => 1,
        >= 0xC0 and < 0xE0 => 2,
        >= 0xE0 and < 0xF0 => 3,
        >= 0xF0 and < 0xF8 => 4,
        >= 0xF8 and < 0xFC => 5,
        >= 0xFC and < 0xFE => 6,
        _ => 1,
    };

    /// <summary>
    /// <b>Verified</b> (MyGUIEngine_x64.dll, <c>UString::_utf32_to_utf16</c>): the code point the UTF-16 text holds. Up to U+FFFF one unit as it is
    /// (a surrogate value too); above it a pair built from the low 20 bits of the value minus 0x10000, so values past U+10FFFF wrap.
    /// </summary>
    static int StoredValue(int value) => value <= 0xFFFF ? value : 0x10000 + ((value - 0x10000) & 0xFFFFF);
}

/// <summary>
/// A character as MyGUI reads a string (<see cref="MyGuiText"/>).
/// </summary>
/// <param name="Value">The code point.</param>
/// <param name="Utf16SequenceLength">The UTF-16 units of the source string it takes.</param>
public readonly record struct MyGuiRune(int Value, int Utf16SequenceLength);

/// <summary>
/// Enumerates <see cref="MyGuiText.Runes"/> without allocating.
/// </summary>
public struct MyGuiRuneEnumerator(string text)
{
    /// <summary>
    /// The string enumerated.
    /// </summary>
    readonly string _text = text;

    /// <summary>
    /// The index of the next character's first unit.
    /// </summary>
    int _next;

    /// <summary>
    /// The current character.
    /// </summary>
    public MyGuiRune Current { get; private set; }

    /// <summary>
    /// The enumerator itself, so a <c>foreach</c> can take it.
    /// </summary>
    public readonly MyGuiRuneEnumerator GetEnumerator() => this;

    /// <summary>
    /// Moves to the next character; false at the end.
    /// </summary>
    public bool MoveNext()
    {
        if (_next >= _text.Length) return false;

        Current = MyGuiText.RuneAt(_text, _next);
        _next += Current.Utf16SequenceLength;
        return true;
    }
}
