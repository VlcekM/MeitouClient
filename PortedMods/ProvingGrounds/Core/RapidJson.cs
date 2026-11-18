// Port of the parts of Tencent rapidjson v1.1.0 (MIT licence, see NOTICE.md; commit f54b0e47, golden/fetch_rapidjson.sh) that
// ArenaSnapshotJson.cpp (Proving Grounds by Shutori, GPLv3) uses: the iterative SAX Reader (include/rapidjson/reader.h) with the UTF-8
// validation of encodings.h, a DOM built from it (document.h GenericDocument / GenericValue, the queries the codec calls), the
// PrettyWriter over a StringBuffer (writer.h, prettywriter.h) and its double formatting (internal/dtoa.h, diyfp.h: Grisu2).
//
// Why a port and not System.Text.Json (the base of PGConfig.Json.cs): the snapshot reader's messages carry rapidjson's own error
// text and byte offset ("Missing a comma or '}' after an object member. at byte 120"), its SyntaxGuard needs the SAX event order
// (keys before values, numbers as their literal text) to build field paths and reject duplicates before a DOM exists, and its
// writer's bytes (indentation, Grisu2 shortest digits, "1e30", "-0.0") are the sidecar text. System.Text.Json reports neither the
// same errors nor the same offsets.
//
// Ported exactly: the tokenizer and state machine of IterativeParse (reader.h:1407-1852), ParseString / ParseStringToStream /
// ParseHex4 (:755-904), ParseNumber (:1133-1389: every range check, NumberTooBig, the int/uint/int64/uint64/double event it sends),
// UTF8::Decode / Validate / Encode / GetRange (encodings.h), the GenericValue number flags (document.h), PrettyWriter (prettywriter.h)
// and Writer::WriteString / WriteDouble (writer.h), dtoa / Grisu2 / Prettify (internal/dtoa.h, diyfp.h).
// Kept although the snapshot codec never reaches them (each marked "not reached by the snapshot codec; kept as a faithful
// copy"): StrtodNormalPrecision (the codec always sets kParseFullPrecisionFlag), the Transcode branch without
// kParseValidateEncodingFlag, Dtoa / Prettify with maxDecimalPlaces other than 324, the BOM skip of Document.Parse, Document.RawNumber
// and Value.FromInt64 (the reader sends Int64 only below INT_MIN, where it is never an int).
//
// Files: RapidJson.cs (errors, flags, handler, UTF-8), RapidJson.Reader.cs, RapidJson.Document.cs, RapidJson.Strtod.cs,
// RapidJson.Writer.cs, RapidJson.Dtoa.cs.
// Not ported (the codec never uses them): the recursive parser, in-situ parsing, comments, trailing commas, NaN/Inf literals,
// kParseStopWhenDoneFlag, transcoding between encodings, other writers.
// A number's double value is internal::StrtodFullPrecision (strtod.h, biginteger.h, ieee754.h) as 1.1.0 computes it, including
// where that is not the correctly rounded value (2.4703282292062327e-324 gives 5e-324, not 0) and where it overflows (10e308 gives
// the garbage -3.09e-308, not infinity). Three inputs are undefined behaviour in 1.1.0 and get a defined answer here
// (docs/divergences.md): a zero significand that misses the fast path ("0e300"), a value below ~1e-330 written with many digits
// (an out-of-bounds read of the power table), and a big-integer shift by a whole number of 64-bit digits (an out-of-range memmove).
using System.Globalization;

namespace ProvingGrounds.Core;

internal static partial class RapidJson
{
    /// <summary>C++: error/error.h:64-90 (values in order, as GetParseErrorCode returns them).</summary>
    internal enum ParseErrorCode
    {
        None = 0,
        DocumentEmpty,
        DocumentRootNotSingular,
        ValueInvalid,
        ObjectMissName,
        ObjectMissColon,
        ObjectMissCommaOrCurlyBracket,
        ArrayMissCommaOrSquareBracket,
        StringUnicodeEscapeInvalidHex,
        StringUnicodeSurrogateInvalid,
        StringEscapeInvalid,
        StringMissQuotationMark,
        StringInvalidEncoding,
        NumberTooBig,
        NumberMissFraction,
        NumberMissExponent,
        Termination,
        UnspecificSyntaxError,
    }

    // C++: error/en.h:36-64
    internal static string GetParseErrorEn(ParseErrorCode parseErrorCode)
    {
        switch (parseErrorCode)
        {
        case ParseErrorCode.None: return "No error.";
        case ParseErrorCode.DocumentEmpty: return "The document is empty.";
        case ParseErrorCode.DocumentRootNotSingular: return "The document root must not be followed by other values.";
        case ParseErrorCode.ValueInvalid: return "Invalid value.";
        case ParseErrorCode.ObjectMissName: return "Missing a name for object member.";
        case ParseErrorCode.ObjectMissColon: return "Missing a colon after a name of object member.";
        case ParseErrorCode.ObjectMissCommaOrCurlyBracket: return "Missing a comma or '}' after an object member.";
        case ParseErrorCode.ArrayMissCommaOrSquareBracket: return "Missing a comma or ']' after an array element.";
        case ParseErrorCode.StringUnicodeEscapeInvalidHex: return "Incorrect hex digit after \\u escape in string.";
        case ParseErrorCode.StringUnicodeSurrogateInvalid: return "The surrogate pair in string is invalid.";
        case ParseErrorCode.StringEscapeInvalid: return "Invalid escape character in string.";
        case ParseErrorCode.StringMissQuotationMark: return "Missing a closing quotation mark in string.";
        case ParseErrorCode.StringInvalidEncoding: return "Invalid encoding in string.";
        case ParseErrorCode.NumberTooBig: return "Number too big to be stored in double.";
        case ParseErrorCode.NumberMissFraction: return "Miss fraction part in number.";
        case ParseErrorCode.NumberMissExponent: return "Miss exponent in number.";
        case ParseErrorCode.Termination: return "Terminate parsing due to Handler error.";
        case ParseErrorCode.UnspecificSyntaxError: return "Unspecific syntax error.";
        default: return "Unknown error.";
        }
    }

    /// <summary>C++: reader.h:146-156, the flags this port implements (kParseIterativeFlag is the only parse mode: the recursive one
    /// is not ported). kParseFullPrecisionFlag changes only how rapidjson computes a double's value, see the file header.</summary>
    [Flags]
    internal enum ParseFlags
    {
        None = 0,
        ValidateEncoding = 2,
        Iterative = 4,
        FullPrecision = 16,
        NumbersAsStrings = 64,
    }

    /// <summary>C++: reader.h:167-194 (the Handler concept; BaseReaderHandler :196-226). Strings are the decoded bytes, without the terminating NUL.</summary>
    internal interface IHandler
    {
        bool Null();
        bool Bool(bool b);
        bool Int(int i);
        bool Uint(uint u);
        bool Int64(long i);
        bool Uint64(ulong u);
        bool Double(double d);
        bool RawNumber(ReadOnlySpan<byte> str);
        bool String(ReadOnlySpan<byte> str);
        bool StartObject();
        bool Key(ReadOnlySpan<byte> str);
        bool EndObject(uint memberCount);
        bool StartArray();
        bool EndArray(uint elementCount);
    }

    /// <summary>C++: error/error.h:106-135 ParseResult.</summary>
    internal readonly record struct ParseResult(ParseErrorCode Code, long Offset)
    {
        public bool IsError => Code != ParseErrorCode.None;
    }

    // ---- encodings.h: UTF8 --------------------------------------------------------------------------------------------------------

    // C++: encodings.h:204-219 (Bjoern Hoehrmann's DFA classes, remapped 1 -> 0x10, 7 -> 0x20, 9 -> 0x40)
    static readonly byte[] Utf8Type =
    {
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,  0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,  0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,  0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,  0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,0x10,
        0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,0x40,
        0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,
        0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,0x20,
        8,8,2,2,2,2,2,2,2,2,2,2,2,2,2,2,  2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,2,
        10,3,3,3,3,3,3,3,3,3,3,3,3,4,3,3, 11,6,6,6,5,8,8,8,8,8,8,8,8,8,8,8,
    };

    // C++: encodings.h:204 GetRange
    static byte GetRange(byte c) => Utf8Type[c];

    /// <summary>C++: memorystream.h MemoryStream (Peek and Take give 0 past the end, Tell counts from the start).</summary>
    internal sealed class MemoryStream
    {
        readonly byte[] _src;
        readonly int _begin;
        readonly int _end;
        int _pos;

        public MemoryStream(byte[] src, int begin, int length) { _src = src; _begin = begin; _pos = begin; _end = begin + length; }

        public byte Peek() => _pos == _end ? (byte)0 : _src[_pos];
        public byte Take() => _pos == _end ? (byte)0 : _src[_pos++];
        public long Tell() => _pos - _begin;
    }

    /// <summary>C++: encodings.h:146-177 UTF8::Decode (validating; a NUL past the end of the stream reads as a byte 0).</summary>
    internal static bool Utf8Decode(MemoryStream input, out uint codepoint)
    {
        byte c = input.Take();
        if ((c & 0x80) == 0)
        {
            codepoint = c;
            return true;
        }
        byte type = GetRange(c);
        uint cp = type >= 32 ? 0u : (uint)((0xFF >> type) & c);
        bool result = true;
        void Copy() { c = input.Take(); cp = (cp << 6) | (uint)(c & 0x3F); }
        void Trans(int mask) { result &= (GetRange(c) & mask) != 0; }
        void Tail() { Copy(); Trans(0x70); }
        switch (type)
        {
        case 2: Tail(); break;
        case 3: Tail(); Tail(); break;
        case 4: Copy(); Trans(0x50); Tail(); break;
        case 5: Copy(); Trans(0x10); Tail(); Tail(); break;
        case 6: Tail(); Tail(); Tail(); break;
        case 10: Copy(); Trans(0x20); Tail(); break;
        case 11: Copy(); Trans(0x60); Tail(); Tail(); break;
        default: result = false; break;
        }
        codepoint = cp;
        return result;
    }

    // C++: encodings.h:179-202 UTF8::Validate (copies every byte it takes)
    static bool Utf8Validate(MemoryStream input, List<byte> os)
    {
        byte c = input.Take();
        os.Add(c);
        if ((c & 0x80) == 0)
            return true;
        bool result = true;
        void Copy() { c = input.Take(); os.Add(c); }
        void Trans(int mask) { result &= (GetRange(c) & mask) != 0; }
        void Tail() { Copy(); Trans(0x70); }
        switch (GetRange(c))
        {
        case 2: Tail(); return result;
        case 3: Tail(); Tail(); return result;
        case 4: Copy(); Trans(0x50); Tail(); return result;
        case 5: Copy(); Trans(0x10); Tail(); Tail(); return result;
        case 6: Tail(); Tail(); Tail(); return result;
        case 10: Copy(); Trans(0x20); Tail(); return result;
        case 11: Copy(); Trans(0x60); Tail(); Tail(); return result;
        default: return false;
        }
    }

    // C++: encodings.h:102-120 UTF8::Encode
    static void Utf8Encode(List<byte> os, uint codepoint)
    {
        if (codepoint <= 0x7F)
            os.Add((byte)(codepoint & 0xFF));
        else if (codepoint <= 0x7FF)
        {
            os.Add((byte)(0xC0 | ((codepoint >> 6) & 0xFF)));
            os.Add((byte)(0x80 | (codepoint & 0x3F)));
        }
        else if (codepoint <= 0xFFFF)
        {
            os.Add((byte)(0xE0 | ((codepoint >> 12) & 0xFF)));
            os.Add((byte)(0x80 | ((codepoint >> 6) & 0x3F)));
            os.Add((byte)(0x80 | (codepoint & 0x3F)));
        }
        else
        {
            os.Add((byte)(0xF0 | ((codepoint >> 18) & 0xFF)));
            os.Add((byte)(0x80 | ((codepoint >> 12) & 0x3F)));
            os.Add((byte)(0x80 | ((codepoint >> 6) & 0x3F)));
            os.Add((byte)(0x80 | (codepoint & 0x3F)));
        }
    }
}
