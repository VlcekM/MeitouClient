// Port of rapidjson v1.1.0 prettywriter.h, writer.h (MIT, see NOTICE.md); part of Core/RapidJson.cs (its header says what is ported and why).
using System.Globalization;

namespace ProvingGrounds.Core;

internal static partial class RapidJson
{
    // ---- prettywriter.h / writer.h ------------------------------------------------------------------------------------------------

    /// <summary>C++: prettywriter.h PrettyWriter&lt;StringBuffer&gt; (kFormatDefault) over writer.h Writer (kWriteDefaultFlags,
    /// maxDecimalPlaces 324). The output is bytes; strings are written as the bytes they hold (no validation).</summary>
    internal sealed class PrettyWriter
    {
        // C++: writer.h:256-260 Level
        struct Level
        {
            public bool InArray;
            public int ValueCount;
        }

        readonly List<byte> _os = new List<byte>();
        readonly List<Level> _levels = new List<Level>();
        byte _indentChar = (byte)' ';
        int _indentCharCount = 4;

        public byte[] GetBytes() => _os.ToArray();

        // C++: prettywriter.h:65-71 SetIndent
        public void SetIndent(char indentChar, int indentCharCount)
        {
            _indentChar = (byte)indentChar;
            _indentCharCount = indentCharCount;
        }

        // C++: prettywriter.h:190-232 PrettyPrefix
        void PrettyPrefix()
        {
            if (_levels.Count != 0)
            {
                // this value is not at root
                Level level = _levels[_levels.Count - 1];
                if (level.InArray)
                {
                    if (level.ValueCount > 0)
                        _os.Add((byte)','); // add comma if it is not the first element in array
                    _os.Add((byte)'\n');
                    WriteIndent();
                }
                else
                {
                    // in object
                    if (level.ValueCount > 0)
                    {
                        if (level.ValueCount % 2 == 0)
                        {
                            _os.Add((byte)',');
                            _os.Add((byte)'\n');
                        }
                        else
                        {
                            _os.Add((byte)':');
                            _os.Add((byte)' ');
                        }
                    }
                    else
                        _os.Add((byte)'\n');
                    if (level.ValueCount % 2 == 0)
                        WriteIndent();
                }
                level.ValueCount++;
                _levels[_levels.Count - 1] = level;
            }
            // else: the root value (Base::hasRoot_, only asserted in the C++)
        }

        // C++: prettywriter.h:234-237 WriteIndent
        void WriteIndent()
        {
            int count = _levels.Count * _indentCharCount;
            for (int i = 0; i < count; ++i) _os.Add(_indentChar);
        }

        void Ascii(string s) { foreach (char c in s) _os.Add((byte)c); }

        // C++: prettywriter.h:79-89 + writer.h:244-277 WriteBool / WriteInt / WriteUint (internal::i32toa / u32toa: plain decimal)
        public bool Bool(bool b) { PrettyPrefix(); Ascii(b ? "true" : "false"); return true; }
        public bool Int(int i) { PrettyPrefix(); Ascii(i.ToString(CultureInfo.InvariantCulture)); return true; }
        public bool Uint(uint u) { PrettyPrefix(); Ascii(u.ToString(CultureInfo.InvariantCulture)); return true; }

        // C++: prettywriter.h:91 Double -> writer.h:317-343 WriteDouble: false for NaN/Inf (kWriteNanAndInfFlag not set), after the
        // prefix was written
        public bool Double(double d)
        {
            PrettyPrefix();
            if (double.IsNaN(d) || double.IsInfinity(d))
                return false;
            Ascii(Dtoa(d));
            return true;
        }

        // C++: prettywriter.h:99-103 String, :117 Key
        public bool String(ReadOnlySpan<byte> str) { PrettyPrefix(); WriteString(str); return true; }
        public bool Key(ReadOnlySpan<byte> str) => String(str);

        // C++: prettywriter.h:111-115 StartObject
        public bool StartObject()
        {
            PrettyPrefix();
            _levels.Add(new Level { InArray = false });
            _os.Add((byte)'{');
            return true;
        }

        // C++: prettywriter.h:125-141 EndObject
        public bool EndObject()
        {
            bool empty = _levels[_levels.Count - 1].ValueCount == 0;
            _levels.RemoveAt(_levels.Count - 1);
            if (!empty)
            {
                _os.Add((byte)'\n');
                WriteIndent();
            }
            _os.Add((byte)'}');
            return true;
        }

        // C++: prettywriter.h:143-147 StartArray
        public bool StartArray()
        {
            PrettyPrefix();
            _levels.Add(new Level { InArray = true });
            _os.Add((byte)'[');
            return true;
        }

        // C++: prettywriter.h:149-165 EndArray
        public bool EndArray()
        {
            bool empty = _levels[_levels.Count - 1].ValueCount == 0;
            _levels.RemoveAt(_levels.Count - 1);
            if (!empty)
            {
                _os.Add((byte)'\n');
                WriteIndent();
            }
            _os.Add((byte)']');
            return true;
        }

        // C++: writer.h:345-418 WriteString (UTF-8 to UTF-8, no validation: every byte that needs no escape is copied as it is)
        void WriteString(ReadOnlySpan<byte> str)
        {
            const string hexDigits = "0123456789ABCDEF";
            _os.Add((byte)'"');
            foreach (byte c in str)
            {
                char e = Escape(c);
                if (e != '\0')
                {
                    _os.Add((byte)'\\');
                    _os.Add((byte)e);
                    if (e == 'u')
                    {
                        _os.Add((byte)'0');
                        _os.Add((byte)'0');
                        _os.Add((byte)hexDigits[c >> 4]);
                        _os.Add((byte)hexDigits[c & 0xF]);
                    }
                }
                else
                    _os.Add(c);
            }
            _os.Add((byte)'"');
        }

        // C++: writer.h:347-356, the escape table: control characters as \uXXXX except \b \t \n \f \r; '"' and '\\'
        static char Escape(byte c)
        {
            if (c < 0x20)
                return c == 0x08 ? 'b' : c == 0x09 ? 't' : c == 0x0A ? 'n' : c == 0x0C ? 'f' : c == 0x0D ? 'r' : 'u';
            if (c == '"') return '"';
            if (c == '\\') return '\\';
            return '\0';
        }
    }
}
