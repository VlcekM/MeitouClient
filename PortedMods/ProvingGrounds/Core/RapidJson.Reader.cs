// Port of rapidjson v1.1.0 reader.h GenericReader (iterative parsing) (MIT, see NOTICE.md); part of Core/RapidJson.cs (its header says what is ported and why).
using System.Globalization;

namespace ProvingGrounds.Core;

internal static partial class RapidJson
{
    // ---- reader.h: GenericReader<UTF8<>, UTF8<> > with kParseIterativeFlag ------------------------------------------------------

    /// <summary>C++: reader.h:466-1859 GenericReader, iterative parsing only.</summary>
    internal sealed class Reader
    {
        ParseErrorCode _code;
        long _offset;
        MemoryStream _is = null!;
        ParseFlags _flags;
        IHandler _handler = null!;
        // the iterative parser's stack: pairs (saved state, member/element count)
        readonly List<uint> _stack = new List<uint>();

        public ParseErrorCode GetParseErrorCode() => _code;
        public long GetErrorOffset() => _offset;
        bool HasParseError() => _code != ParseErrorCode.None;
        void SetParseError(ParseErrorCode code, long offset) { _code = code; _offset = offset; }

        /// <summary><c>reader.Parse&lt;flags&gt;(MemoryStream(text + start, length), handler)</c>. Only iterative parsing is ported.
        /// Offsets count from <paramref name="start"/>.
        /// <paramref name="encodedInputStream"/>: read through EncodedInputStream&lt;UTF8&lt;&gt;, MemoryStream&gt; (encodedstream.h:68-76), as
        /// Document::Parse(str, length) does: its constructor takes a leading 0xEF, then 0xBB, then 0xBF, each only if it is next
        /// (offsets still count from the stream start).</summary>
        public ParseResult Parse(byte[] text, int start, int length, ParseFlags flags, IHandler handler, bool encodedInputStream = false)
        {
            if ((flags & ParseFlags.Iterative) == 0)
                throw new NotSupportedException("RapidJson port: only kParseIterativeFlag parsing is ported");
            _text = text;
            _start = start;
            _is = new MemoryStream(text, start, length);
            if (encodedInputStream)
            {
                // not reached by the snapshot codec; kept as a faithful copy (see Document.Parse)
                if (_is.Peek() == 0xEF) _is.Take();
                if (_is.Peek() == 0xBB) _is.Take();
                if (_is.Peek() == 0xBF) _is.Take();
            }
            _flags = flags;
            _handler = handler;
            return IterativeParse();
        }

        byte[] _text = Array.Empty<byte>();
        int _start;

        // C++: reader.h:263-268 SkipWhitespace (no SIMD: the generic loop)
        void SkipWhitespace()
        {
            byte c;
            while ((c = _is.Peek()) == ' ' || c == '\n' || c == '\r' || c == '\t')
                _is.Take();
        }

        // C++: reader.h:743-751 Consume
        bool Consume(byte expect)
        {
            if (_is.Peek() == expect)
            {
                _is.Take();
                return true;
            }
            return false;
        }

        // C++: reader.h:754-776 ParseHex4
        uint ParseHex4(long escapeOffset)
        {
            uint codepoint = 0;
            for (int i = 0; i < 4; i++)
            {
                byte c = _is.Peek();
                codepoint <<= 4;
                codepoint += c;
                if (c >= '0' && c <= '9')
                    codepoint -= '0';
                else if (c >= 'A' && c <= 'F')
                    codepoint -= 'A' - 10;
                else if (c >= 'a' && c <= 'f')
                    codepoint -= 'a' - 10;
                else
                {
                    SetParseError(ParseErrorCode.StringUnicodeEscapeInvalidHex, escapeOffset);
                    return 0;
                }
                _is.Take();
            }
            return codepoint;
        }

        // C++: reader.h:704-741 ParseNull / ParseTrue / ParseFalse
        void ParseNull()
        {
            _is.Take();
            if (Consume((byte)'u') && Consume((byte)'l') && Consume((byte)'l'))
            {
                if (!_handler.Null())
                    SetParseError(ParseErrorCode.Termination, _is.Tell());
            }
            else
                SetParseError(ParseErrorCode.ValueInvalid, _is.Tell());
        }

        void ParseTrue()
        {
            _is.Take();
            if (Consume((byte)'r') && Consume((byte)'u') && Consume((byte)'e'))
            {
                if (!_handler.Bool(true))
                    SetParseError(ParseErrorCode.Termination, _is.Tell());
            }
            else
                SetParseError(ParseErrorCode.ValueInvalid, _is.Tell());
        }

        void ParseFalse()
        {
            _is.Take();
            if (Consume((byte)'a') && Consume((byte)'l') && Consume((byte)'s') && Consume((byte)'e'))
            {
                if (!_handler.Bool(false))
                    SetParseError(ParseErrorCode.Termination, _is.Tell());
            }
            else
                SetParseError(ParseErrorCode.ValueInvalid, _is.Tell());
        }

        // C++: reader.h:807-833 ParseString (non-in-situ: decoded into the stack)
        void ParseString(bool isKey)
        {
            _is.Take(); // Skip '\"'
            var os = new List<byte>();
            ParseStringToStream(os);
            if (HasParseError())
                return;
            ReadOnlySpan<byte> str = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(os);
            bool success = isKey ? _handler.Key(str) : _handler.String(str);
            if (!success)
                SetParseError(ParseErrorCode.Termination, _is.Tell());
        }

        // C++: reader.h:839-904 ParseStringToStream (the escape table at :842-850)
        void ParseStringToStream(List<byte> os)
        {
            for (;;)
            {
                byte c = _is.Peek();
                if (c == '\\')
                {
                    // Escape: for invalid escaping, report the initial '\\' as error offset
                    long escapeOffset = _is.Tell();
                    _is.Take();
                    byte e = _is.Peek();
                    byte mapped = Unescape(e);
                    if (mapped != 0)
                    {
                        _is.Take();
                        os.Add(mapped);
                    }
                    else if (e == 'u')
                    {
                        _is.Take();
                        uint codepoint = ParseHex4(escapeOffset);
                        if (HasParseError()) return;
                        if (codepoint >= 0xD800 && codepoint <= 0xDBFF)
                        {
                            // Handle UTF-16 surrogate pair
                            if (!Consume((byte)'\\') || !Consume((byte)'u'))
                            {
                                SetParseError(ParseErrorCode.StringUnicodeSurrogateInvalid, escapeOffset);
                                return;
                            }
                            uint codepoint2 = ParseHex4(escapeOffset);
                            if (HasParseError()) return;
                            if (codepoint2 < 0xDC00 || codepoint2 > 0xDFFF)
                            {
                                SetParseError(ParseErrorCode.StringUnicodeSurrogateInvalid, escapeOffset);
                                return;
                            }
                            codepoint = (((codepoint - 0xD800) << 10) | (codepoint2 - 0xDC00)) + 0x10000;
                        }
                        Utf8Encode(os, codepoint);
                    }
                    else
                    {
                        SetParseError(ParseErrorCode.StringEscapeInvalid, escapeOffset);
                        return;
                    }
                }
                else if (c == '"')
                {
                    // Closing double quote
                    _is.Take();
                    return;
                }
                else if (c < 0x20)
                {
                    // RFC 4627: unescaped = %x20-21 / %x23-5B / %x5D-10FFFF
                    SetParseError(c == 0 ? ParseErrorCode.StringMissQuotationMark : ParseErrorCode.StringEscapeInvalid, _is.Tell());
                    return;
                }
                else
                {
                    long offset = _is.Tell();
                    bool ok;
                    if ((_flags & ParseFlags.ValidateEncoding) != 0)
                        ok = Utf8Validate(_is, os);
                    else
                    {
                        // not reached by the snapshot codec; kept as a faithful copy (the codec always sets kParseValidateEncodingFlag)
                        // Transcoder<UTF8, UTF8>::Transcode: copy one byte
                        os.Add(_is.Take());
                        ok = true;
                    }
                    if (!ok)
                    {
                        SetParseError(ParseErrorCode.StringInvalidEncoding, offset);
                        return;
                    }
                }
            }
        }

        // C++: reader.h:842-850, the escape table: '"' '/' '\\' 'b' 'f' 'n' 'r' 't' -> the character they stand for, else 0
        static byte Unescape(byte e)
        {
            switch (e)
            {
            case (byte)'"': return (byte)'"';
            case (byte)'/': return (byte)'/';
            case (byte)'\\': return (byte)'\\';
            case (byte)'b': return (byte)'\b';
            case (byte)'f': return (byte)'\f';
            case (byte)'n': return (byte)'\n';
            case (byte)'r': return (byte)'\r';
            case (byte)'t': return (byte)'\t';
            default: return 0;
            }
        }

        // C++: reader.h:1132-1389 ParseNumber. NumberStream: with kParseNumbersAsStringsFlag every taken byte is pushed (the raw
        // literal); otherwise only the digits. The value of a double is computed from the literal (file header).
        // NumberStream<InputStream, true, false>::TakePush (reader.h:1096-1117): the digit is kept for StrtodFullPrecision; plain
        // Take/Consume ('-', '.', 'e', the exponent) are not. With kParseNumbersAsStringsFlag the raw literal is used instead.
        readonly List<byte> _decimals = new List<byte>();
        byte TakePush()
        {
            _decimals.Add(_is.Peek());
            return _is.Take();
        }

        void ParseNumber()
        {
            _decimals.Clear();
            long startOffset = _is.Tell();
            double d = 0.0;

            // Parse minus
            bool minus = Consume((byte)'-');

            // Parse int: zero / ( digit1-9 *DIGIT )
            uint i = 0;
            ulong i64 = 0;
            bool use64bit = false;
            int significandDigit = 0;
            byte p0 = _is.Peek();
            if (p0 == '0')
            {
                i = 0;
                TakePush();
            }
            else if (p0 >= '1' && p0 <= '9')
            {
                i = (uint)(TakePush() - '0');
                if (minus)
                    while (IsDigit(_is.Peek()))
                    {
                        if (i >= 214748364)
                        {
                            // 2^31 = 2147483648
                            if (i != 214748364 || _is.Peek() > '8')
                            {
                                i64 = i;
                                use64bit = true;
                                break;
                            }
                        }
                        i = i * 10 + (uint)(TakePush() - '0');
                        significandDigit++;
                    }
                else
                    while (IsDigit(_is.Peek()))
                    {
                        if (i >= 429496729)
                        {
                            // 2^32 - 1 = 4294967295
                            if (i != 429496729 || _is.Peek() > '5')
                            {
                                i64 = i;
                                use64bit = true;
                                break;
                            }
                        }
                        i = i * 10 + (uint)(TakePush() - '0');
                        significandDigit++;
                    }
            }
            else
            {
                // kParseNanAndInfFlag is not set
                SetParseError(ParseErrorCode.ValueInvalid, _is.Tell());
                return;
            }

            // Parse 64bit int
            bool useDouble = false;
            if (use64bit)
            {
                if (minus)
                    while (IsDigit(_is.Peek()))
                    {
                        if (i64 >= 0x0CCCCCCCCCCCCCCCUL) // 2^63 = 9223372036854775808
                            if (i64 != 0x0CCCCCCCCCCCCCCCUL || _is.Peek() > '8')
                            {
                                d = i64;
                                useDouble = true;
                                break;
                            }
                        i64 = i64 * 10 + (uint)(TakePush() - '0');
                        significandDigit++;
                    }
                else
                    while (IsDigit(_is.Peek()))
                    {
                        if (i64 >= 0x1999999999999999UL) // 2^64 - 1 = 18446744073709551615
                            if (i64 != 0x1999999999999999UL || _is.Peek() > '5')
                            {
                                d = i64;
                                useDouble = true;
                                break;
                            }
                        i64 = i64 * 10 + (uint)(TakePush() - '0');
                        significandDigit++;
                    }
            }

            // Force double for big integer
            if (useDouble)
            {
                while (IsDigit(_is.Peek()))
                {
                    if (d >= 1.7976931348623157e307) // DBL_MAX / 10.0
                    {
                        SetParseError(ParseErrorCode.NumberTooBig, startOffset);
                        return;
                    }
                    d = d * 10 + (TakePush() - '0');
                }
            }

            // Parse frac = decimal-point 1*DIGIT
            int expFrac = 0;
            int decimalPosition;
            if (Consume((byte)'.'))
            {
                decimalPosition = _decimals.Count;

                if (!IsDigit(_is.Peek()))
                {
                    SetParseError(ParseErrorCode.NumberMissFraction, _is.Tell());
                    return;
                }
                if (!useDouble)
                {
                    // RAPIDJSON_64BIT: use i64 to store the significand
                    if (!use64bit)
                        i64 = i;
                    while (IsDigit(_is.Peek()))
                    {
                        if (i64 > 0x1FFFFFFFFFFFFFUL) // 2^53 - 1 for fast path
                            break;
                        i64 = i64 * 10 + (uint)(TakePush() - '0');
                        --expFrac;
                        if (i64 != 0)
                            significandDigit++;
                    }
                    d = i64;
                    useDouble = true;
                }
                while (IsDigit(_is.Peek()))
                {
                    if (significandDigit < 17)
                    {
                        d = d * 10.0 + (TakePush() - '0');
                        --expFrac;
                        if (d > 0.0)
                            significandDigit++;
                    }
                    else
                        TakePush();
                }
            }
            else
                decimalPosition = _decimals.Count; // decimal position at the end of integer.

            // Parse exp = e [ minus / plus ] 1*DIGIT
            int exp = 0;
            if (Consume((byte)'e') || Consume((byte)'E'))
            {
                if (!useDouble)
                {
                    d = use64bit ? i64 : i;
                    useDouble = true;
                }
                bool expMinus = false;
                if (Consume((byte)'+'))
                {
                }
                else if (Consume((byte)'-'))
                    expMinus = true;

                if (IsDigit(_is.Peek()))
                {
                    exp = _is.Take() - '0';
                    if (expMinus)
                    {
                        while (IsDigit(_is.Peek()))
                        {
                            exp = exp * 10 + (_is.Take() - '0');
                            if (exp >= 214748364)
                            {
                                // Issue #313: prevent overflow exponent; consume the rest of exponent
                                while (IsDigit(_is.Peek()))
                                    _is.Take();
                            }
                        }
                    }
                    else
                    {
                        // positive exp
                        int maxExp = 308 - expFrac;
                        while (IsDigit(_is.Peek()))
                        {
                            exp = exp * 10 + (_is.Take() - '0');
                            if (exp > maxExp)
                            {
                                SetParseError(ParseErrorCode.NumberTooBig, startOffset);
                                return;
                            }
                        }
                    }
                }
                else
                {
                    SetParseError(ParseErrorCode.NumberMissExponent, _is.Tell());
                    return;
                }

                if (expMinus)
                    exp = -exp;
            }

            // Finish parsing, call event according to the type of number.
            bool cont;
            ReadOnlySpan<byte> literal = new ReadOnlySpan<byte>(_text, _start + (int)startOffset, (int)(_is.Tell() - startOffset));
            if ((_flags & ParseFlags.NumbersAsStrings) != 0)
                cont = _handler.RawNumber(literal);
            else if (useDouble)
            {
                int p = exp + expFrac;
                d = (_flags & ParseFlags.FullPrecision) != 0
                    ? StrtodFullPrecision(d, p, _decimals.ToArray(), _decimals.Count, decimalPosition, exp)
                    : StrtodNormalPrecision(d, p);
                cont = _handler.Double(minus ? -d : d);
            }
            else if (use64bit)
                cont = minus ? _handler.Int64((long)(~i64 + 1)) : _handler.Uint64(i64);
            else
                cont = minus ? _handler.Int((int)(~i + 1)) : _handler.Uint(i);
            if (!cont)
                SetParseError(ParseErrorCode.Termination, startOffset);
        }

        static bool IsDigit(byte c) => c >= '0' && c <= '9';

        // C++: reader.h:1391-1404 ParseValue
        void ParseValue()
        {
            switch (_is.Peek())
            {
            case (byte)'n': ParseNull(); break;
            case (byte)'t': ParseTrue(); break;
            case (byte)'f': ParseFalse(); break;
            case (byte)'"': ParseString(false); break;
            default: ParseNumber(); break;
            }
        }

        // C++: reader.h:1410-1431
        enum State
        {
            Start = 0,
            Finish,
            Error,
            ObjectInitial,
            MemberKey,
            KeyValueDelimiter,
            MemberValue,
            MemberDelimiter,
            ObjectFinish,
            ArrayInitial,
            Element,
            ElementDelimiter,
            ArrayFinish,
            Value,
        }

        // C++: reader.h:1436-1452
        enum Token
        {
            LeftBracket = 0,
            RightBracket,
            LeftCurlyBracket,
            RightCurlyBracket,
            Comma,
            Colon,
            String,
            False,
            True,
            Null,
            Number,
        }

        // C++: reader.h:1455-1480 Tokenize (every byte not listed is a NumberToken)
        static Token Tokenize(byte c)
        {
            switch (c)
            {
            case (byte)'"': return Token.String;
            case (byte)',': return Token.Comma;
            case (byte)':': return Token.Colon;
            case (byte)'[': return Token.LeftBracket;
            case (byte)']': return Token.RightBracket;
            case (byte)'f': return Token.False;
            case (byte)'n': return Token.Null;
            case (byte)'t': return Token.True;
            case (byte)'{': return Token.LeftCurlyBracket;
            case (byte)'}': return Token.RightCurlyBracket;
            default: return Token.Number;
            }
        }

        // C++: reader.h:1482-1644 Predict (the table G, state x token -> state)
        static readonly State[,] G =
        {
            // Start
            { State.ArrayInitial, State.Error, State.ObjectInitial, State.Error, State.Error, State.Error,
              State.Value, State.Value, State.Value, State.Value, State.Value },
            // Finish (sink state)
            { State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error },
            // Error (sink state)
            { State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error },
            // ObjectInitial
            { State.Error, State.Error, State.Error, State.ObjectFinish, State.Error, State.Error,
              State.MemberKey, State.Error, State.Error, State.Error, State.Error },
            // MemberKey
            { State.Error, State.Error, State.Error, State.Error, State.Error, State.KeyValueDelimiter,
              State.Error, State.Error, State.Error, State.Error, State.Error },
            // KeyValueDelimiter
            { State.ArrayInitial, State.Error, State.ObjectInitial, State.Error, State.Error, State.Error,
              State.MemberValue, State.MemberValue, State.MemberValue, State.MemberValue, State.MemberValue },
            // MemberValue
            { State.Error, State.Error, State.Error, State.ObjectFinish, State.MemberDelimiter, State.Error,
              State.Error, State.Error, State.Error, State.Error, State.Error },
            // MemberDelimiter
            { State.Error, State.Error, State.Error, State.ObjectFinish, State.Error, State.Error,
              State.MemberKey, State.Error, State.Error, State.Error, State.Error },
            // ObjectFinish (sink state)
            { State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error },
            // ArrayInitial
            { State.ArrayInitial, State.ArrayFinish, State.ObjectInitial, State.Error, State.Error, State.Error,
              State.Element, State.Element, State.Element, State.Element, State.Element },
            // Element
            { State.Error, State.ArrayFinish, State.Error, State.Error, State.ElementDelimiter, State.Error,
              State.Error, State.Error, State.Error, State.Error, State.Error },
            // ElementDelimiter
            { State.ArrayInitial, State.ArrayFinish, State.ObjectInitial, State.Error, State.Error, State.Error,
              State.Element, State.Element, State.Element, State.Element, State.Element },
            // ArrayFinish (sink state)
            { State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error },
            // Single Value (sink state)
            { State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error, State.Error },
        };

        // C++: reader.h:1648-1798 Transit
        State Transit(State src, Token token, State dst)
        {
            switch (dst)
            {
            case State.Error:
                return dst;

            case State.ObjectInitial:
            case State.ArrayInitial:
            {
                // Push the state (Element or MemberValue) if nested in another array or value of member.
                State n = src;
                if (src == State.ArrayInitial || src == State.ElementDelimiter)
                    n = State.Element;
                else if (src == State.KeyValueDelimiter)
                    n = State.MemberValue;
                _stack.Add((uint)n);
                _stack.Add(0);
                bool hr = dst == State.ObjectInitial ? _handler.StartObject() : _handler.StartArray();
                if (!hr)
                {
                    SetParseError(ParseErrorCode.Termination, _is.Tell());
                    return State.Error;
                }
                _is.Take();
                return dst;
            }

            case State.MemberKey:
                ParseString(true);
                return HasParseError() ? State.Error : dst;

            case State.KeyValueDelimiter:
                _is.Take();
                return dst;

            case State.MemberValue:
                // Must be non-compound value. Or it would be ObjectInitial or ArrayInitial state.
                ParseValue();
                return HasParseError() ? State.Error : dst;

            case State.Element:
                ParseValue();
                return HasParseError() ? State.Error : dst;

            case State.MemberDelimiter:
            case State.ElementDelimiter:
                _is.Take();
                // Update member/element count.
                _stack[_stack.Count - 1] = _stack[_stack.Count - 1] + 1;
                return dst;

            case State.ObjectFinish:
            {
                // Transit from delimiter is only allowed when trailing commas are enabled (they are not)
                if (src == State.MemberDelimiter)
                {
                    SetParseError(ParseErrorCode.ObjectMissName, _is.Tell());
                    return State.Error;
                }
                uint c = Pop();
                if (src == State.MemberValue)
                    ++c;
                State n = (State)Pop();
                if (n == State.Start)
                    n = State.Finish;
                bool hr = _handler.EndObject(c);
                if (!hr)
                {
                    SetParseError(ParseErrorCode.Termination, _is.Tell());
                    return State.Error;
                }
                _is.Take();
                return n;
            }

            case State.ArrayFinish:
            {
                if (src == State.ElementDelimiter)
                {
                    SetParseError(ParseErrorCode.ValueInvalid, _is.Tell());
                    return State.Error;
                }
                uint c = Pop();
                if (src == State.Element)
                    ++c;
                State n = (State)Pop();
                if (n == State.Start)
                    n = State.Finish;
                bool hr = _handler.EndArray(c);
                if (!hr)
                {
                    SetParseError(ParseErrorCode.Termination, _is.Tell());
                    return State.Error;
                }
                _is.Take();
                return n;
            }

            default:
                // IterativeParsingValueState: a single non-compound value at the root
                ParseValue();
                return HasParseError() ? State.Error : State.Finish;
            }
        }

        uint Pop()
        {
            uint v = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            return v;
        }

        // C++: reader.h:1800-1819 HandleError
        void HandleError(State src)
        {
            if (HasParseError())
                return;
            switch (src)
            {
            case State.Start: SetParseError(ParseErrorCode.DocumentEmpty, _is.Tell()); return;
            case State.Finish: SetParseError(ParseErrorCode.DocumentRootNotSingular, _is.Tell()); return;
            case State.ObjectInitial:
            case State.MemberDelimiter: SetParseError(ParseErrorCode.ObjectMissName, _is.Tell()); return;
            case State.MemberKey: SetParseError(ParseErrorCode.ObjectMissColon, _is.Tell()); return;
            case State.MemberValue: SetParseError(ParseErrorCode.ObjectMissCommaOrCurlyBracket, _is.Tell()); return;
            case State.KeyValueDelimiter:
            case State.ArrayInitial:
            case State.ElementDelimiter: SetParseError(ParseErrorCode.ValueInvalid, _is.Tell()); return;
            default: SetParseError(ParseErrorCode.ArrayMissCommaOrSquareBracket, _is.Tell()); return;
            }
        }

        // C++: reader.h:1821-1851 IterativeParse
        ParseResult IterativeParse()
        {
            _code = ParseErrorCode.None;
            _offset = 0;
            _stack.Clear();
            State state = State.Start;

            SkipWhitespace();
            while (_is.Peek() != 0)
            {
                Token t = Tokenize(_is.Peek());
                State n = G[(int)state, (int)t];
                State d = Transit(state, t, n);
                if (d == State.Error)
                {
                    HandleError(state);
                    break;
                }
                state = d;
                SkipWhitespace();
            }

            // Handle the end of file.
            if (state != State.Finish)
                HandleError(state);

            _stack.Clear();
            return new ParseResult(_code, _offset);
        }
    }
}
