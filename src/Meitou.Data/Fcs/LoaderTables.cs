using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>
/// The game loader's readers of a record's seven value tables (docs/formats/fcs-mod.md, "Game loader", "Strings", "Damaged files"): each call has its
/// own count, key length, key buffer and value, none of them set before their reads; the string and filename values share one buffer.
/// </summary>
internal sealed class LoaderTables(LoaderStream stream, LoaderBuffer values)
{
    /// <summary>
    /// A key buffer's size; the byte after it is where its call keeps the stack cookie.
    /// </summary>
    const int KeySize = 64;

    /// <summary>
    /// The file's stream.
    /// </summary>
    readonly LoaderStream _stream = stream;

    /// <summary>
    /// The value buffer the string and filename tables share (<see cref="FcsLoaderSession"/>).
    /// </summary>
    readonly LoaderBuffer _values = values;

    /// <summary>
    /// The key buffer of a table call, with the cookie byte after it.
    /// </summary>
    readonly LoaderBuffer _keys = new(KeySize + 1);

    /// <summary>
    /// The count variable of a table call.
    /// </summary>
    readonly LoaderValue _count = new(4);

    /// <summary>
    /// The key length variable of a table call.
    /// </summary>
    readonly LoaderValue _keyLength = new(4);

    /// <summary>
    /// The value variables of the table calls by size (1 bool, 4 float or int, 12 vec3, 16 vec4).
    /// </summary>
    readonly Dictionary<int, LoaderValue> _tableValues = new() { [1] = new(1), [4] = new(4), [12] = new(12), [16] = new(16) };

    /// <summary>
    /// The length variable of a string value, set to 0 before each read.
    /// </summary>
    readonly LoaderValue _valueLength = new(4);

    /// <summary>
    /// Reads the seven value tables in file order into <paramref name="record"/>; the vec3 and vec4 tables only when <paramref name="vectors"/>.
    /// </summary>
    public void Read(FcsRecord record, bool vectors, Dictionary<string, byte> filter)
    {
        ReadTable(record.Bools, 1, v => v.Byte != 0, filter);
        ReadTable(record.Floats, 4, v => v.Single(0), filter);
        ReadTable(record.Ints, 4, v => v.Int32, filter);
        if (vectors)
        {
            ReadTable(record.Vector3S, 12, v => new Vector3(v.Single(0), v.Single(4), v.Single(8)), filter);
            ReadTable(record.Vector4S, 16, v => new Vector4(v.Single(0), v.Single(4), v.Single(8), v.Single(12)), filter);
        }

        ReadStringTable(record.Strings, filter);
        ReadStringTable(record.Filenames, filter);
    }

    /// <summary>
    /// Reads one fixed-size value table: a count, then keys with values of <paramref name="size"/> bytes; a later key overwrites an earlier one. Once
    /// the stream has failed, every further pair repeats the last one, so the table stops after the first such pair.
    /// </summary>
    void ReadTable<T>(OrderedDictionary<string, T> into, int size, Func<LoaderValue, T> value, Dictionary<string, byte> filter)
    {
        var read = _tableValues[size];
        StartCall();
        read.Forget();
        int count = _count.Int32;
        for (int i = 0; i < count; i++)
        {
            bool repeats = _stream.Failed;
            _keys.ReadString(_stream, 0, KeySize, _keyLength);
            read.Read(_stream);
            string key = _keys.CString(0);
            if (FcsGameLoader.Passes(filter, key)) into[key] = value(read);
            if (repeats) break;
        }
    }

    /// <summary>
    /// Reads one string or filename table: a count, then keys with string values.
    /// </summary>
    void ReadStringTable(OrderedDictionary<string, string> into, Dictionary<string, byte> filter)
    {
        StartCall();
        int count = _count.Int32;
        for (int i = 0; i < count; i++)
        {
            bool repeats = _stream.Failed;
            _keys.ReadString(_stream, 0, KeySize, _keyLength);
            string value = ReadValue();
            string key = _keys.CString(0);
            if (FcsGameLoader.Passes(filter, key)) into[key] = value;
            if (repeats) break;
        }
    }

    /// <summary>
    /// A new table call: its locals and key buffer hold nothing the file decided, then the count is read.
    /// </summary>
    void StartCall()
    {
        _keys.Forget();
        _keyLength.Forget();
        _count.Forget();
        _count.Read(_stream);
    }

    /// <summary>
    /// Reads a string value into the shared value buffer and takes it up to its first NUL.
    /// </summary>
    string ReadValue()
    {
        _valueLength.Set(0);
        _values.ReadString(_stream, 0, FcsLoaderSession.ValueSize, _valueLength);
        return _values.CString(0);
    }
}