// Port of rapidjson v1.1.0 document.h GenericValue / GenericDocument (MIT, see NOTICE.md); part of Core/RapidJson.cs (its header says what is ported and why).
using System.Globalization;

namespace ProvingGrounds.Core;

internal static partial class RapidJson
{
    // ---- document.h: the DOM -----------------------------------------------------------------------------------------------------

    internal enum Type { Null, False, True, Object, Array, String, Number }

    /// <summary>C++: document.h GenericValue: the queries ArenaSnapshotJson.cpp makes. Numbers keep rapidjson's flags: a value is an
    /// Int when it came as an int event or fits one, and so on (document.h:634-673, the number constructors).</summary>
    internal sealed class Value
    {
        public Type Kind;
        // number flags (document.h:1818-1835)
        bool _int, _uint, _int64, _uint64, _double;
        long _i64;
        ulong _u64;
        double _d;
        public byte[] Str = Array.Empty<byte>();
        public readonly List<KeyValuePair<Value, Value>> MembersList = new List<KeyValuePair<Value, Value>>();
        public readonly List<Value> Elements = new List<Value>();

        // C++: document.h:634 GenericValue(int)
        public static Value FromInt(int i) { var v = new Value { Kind = Type.Number, _i64 = i, _u64 = unchecked((ulong)(long)i), _int = true, _int64 = true }; if (i >= 0) { v._uint = true; v._uint64 = true; } return v; }
        // C++: document.h:640 GenericValue(unsigned)
        public static Value FromUint(uint u) { var v = new Value { Kind = Type.Number, _u64 = u, _i64 = u, _uint = true, _uint64 = true, _int64 = true }; if ((u & 0x80000000) == 0) v._int = true; return v; }
        // C++: document.h:646-659 GenericValue(int64_t)
        // not reached by the snapshot codec; kept as a faithful copy (the reader sends Int64 only below INT_MIN)
        public static Value FromInt64(long i)
        {
            var v = new Value { Kind = Type.Number, _i64 = i, _u64 = unchecked((ulong)i), _int64 = true };
            if (i >= 0)
            {
                v._uint64 = true;
                if ((unchecked((ulong)i) & 0xFFFFFFFF00000000UL) == 0) v._uint = true;
                if ((unchecked((ulong)i) & 0xFFFFFFFF80000000UL) == 0) v._int = true;
            }
            else if (i >= unchecked((long)0xFFFFFFFF80000000UL))
                v._int = true;
            return v;
        }
        // C++: document.h:661-671 GenericValue(uint64_t)
        public static Value FromUint64(ulong u)
        {
            var v = new Value { Kind = Type.Number, _u64 = u, _i64 = unchecked((long)u), _uint64 = true };
            if ((u & 0x8000000000000000UL) == 0) v._int64 = true;
            if ((u & 0xFFFFFFFF00000000UL) == 0) v._uint = true;
            if ((u & 0xFFFFFFFF80000000UL) == 0) v._int = true;
            return v;
        }
        // C++: document.h:673 GenericValue(double)
        public static Value FromDouble(double d) => new Value { Kind = Type.Number, _d = d, _double = true };

        public bool IsNull => Kind == Type.Null;
        public bool IsBool => Kind == Type.False || Kind == Type.True;
        public bool GetBool() => Kind == Type.True;
        public bool IsObject => Kind == Type.Object;
        public bool IsArray => Kind == Type.Array;
        public bool IsString => Kind == Type.String;
        public bool IsNumber => Kind == Type.Number;
        public bool IsInt => Kind == Type.Number && _int;
        public bool IsUint => Kind == Type.Number && _uint;
        public bool IsInt64 => Kind == Type.Number && _int64;
        public bool IsUint64 => Kind == Type.Number && _uint64;
        public bool IsDouble => Kind == Type.Number && _double;
        public int GetInt() => (int)_i64;
        public uint GetUint() => (uint)_u64;

        // C++: document.h:1653-1660 GetDouble
        public double GetDouble()
        {
            if (_double) return _d;
            if (_int) return (int)_i64;
            if (_uint) return (uint)_u64;
            if (_int64) return _i64;
            return _u64;
        }

        public int Size => Elements.Count;
        public bool Empty => Elements.Count == 0;
        public Value this[int index] => Elements[index];

        /// <summary>C++: document.h:1153-1162 FindMember: the FIRST member whose name has the same bytes (length compared).</summary>
        public Value? FindMember(string name)
        {
            byte[] key = System.Text.Encoding.UTF8.GetBytes(name);
            foreach (KeyValuePair<Value, Value> m in MembersList)
                if (m.Key.Str.AsSpan().SequenceEqual(key))
                    return m.Value;
            return null;
        }

        public bool HasMember(string name) => FindMember(name) != null;
    }

    /// <summary>C++: document.h:2024-2400 GenericDocument: the Reader handler that builds values on a stack.</summary>
    internal sealed class Document : IHandler
    {
        readonly List<Value> _stack = new List<Value>();
        public Value Root = new Value();
        ParseResult _result;

        public bool HasParseError() => _result.IsError;
        public ParseErrorCode GetParseError() => _result.Code;
        public long GetErrorOffset() => _result.Offset;

        /// <summary><c>doc.Parse&lt;flags&gt;(text, length)</c> (document.h:2244-2256 + ParseStream :2149-2167).</summary>
        public Document Parse(byte[] text, int start, int length, ParseFlags flags)
        {
            var reader = new Reader();
            _stack.Clear();
            // the BOM skip is never reached by the codec: its DOM parse runs only after the SAX pass (a plain MemoryStream, where a
            // BOM is "Invalid value.") accepted the text, and the guard's number parse gets a number literal
            _result = reader.Parse(text, start, length, flags, this, encodedInputStream: true);
            if (!_result.IsError)
                Root = _stack[0];
            _stack.Clear();
            return this;
        }

        public bool Null() { _stack.Add(new Value { Kind = Type.Null }); return true; }
        public bool Bool(bool b) { _stack.Add(new Value { Kind = b ? Type.True : Type.False }); return true; }
        public bool Int(int i) { _stack.Add(Value.FromInt(i)); return true; }
        public bool Uint(uint u) { _stack.Add(Value.FromUint(u)); return true; }
        public bool Int64(long i) { _stack.Add(Value.FromInt64(i)); return true; }
        public bool Uint64(ulong u) { _stack.Add(Value.FromUint64(u)); return true; }
        public bool Double(double d) { _stack.Add(Value.FromDouble(d)); return true; }
        // document.h:2341-2346 RawNumber: the literal as a string value
        // not reached by the snapshot codec; kept as a faithful copy (its DOM parse does not set kParseNumbersAsStringsFlag)
        public bool RawNumber(ReadOnlySpan<byte> str) { _stack.Add(new Value { Kind = Type.String, Str = str.ToArray() }); return true; }
        public bool String(ReadOnlySpan<byte> str) { _stack.Add(new Value { Kind = Type.String, Str = str.ToArray() }); return true; }
        public bool StartObject() { _stack.Add(new Value { Kind = Type.Object }); return true; }
        public bool Key(ReadOnlySpan<byte> str) => String(str);

        public bool EndObject(uint memberCount)
        {
            int n = (int)memberCount * 2;
            int first = _stack.Count - n;
            Value obj = _stack[first - 1];
            for (int i = first; i < _stack.Count; i += 2)
                obj.MembersList.Add(new KeyValuePair<Value, Value>(_stack[i], _stack[i + 1]));
            _stack.RemoveRange(first, n);
            return true;
        }

        public bool StartArray() { _stack.Add(new Value { Kind = Type.Array }); return true; }

        public bool EndArray(uint elementCount)
        {
            int n = (int)elementCount;
            int first = _stack.Count - n;
            Value array = _stack[first - 1];
            for (int i = first; i < _stack.Count; ++i)
                array.Elements.Add(_stack[i]);
            _stack.RemoveRange(first, n);
            return true;
        }
    }
}
