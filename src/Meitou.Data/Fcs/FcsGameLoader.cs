using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>
/// What the game loader keeps from one file to the next: its single buffer of string and filename values, zero at the start
/// (docs/formats/fcs-mod.md, "Strings"). One per load order, shared by its data and translation files.
/// </summary>
public sealed class FcsLoaderSession
{
    /// <summary>
    /// The value buffer's size.
    /// </summary>
    internal const int ValueSize = 4096;

    /// <summary>
    /// The value buffer and the unused byte after it.
    /// </summary>
    internal LoaderBuffer Values { get; } = LoaderBuffer.Zeroed(ValueSize + 1);
}

/// <summary>
/// Reads one data file the way the game's loader does (docs/formats/fcs-mod.md, "Game loader", "Strings", "Damaged files"): file types 8 to 17, strings
/// cut at the loader's buffers and at a NUL, the old types' tag filter and numeric ids, and past the end of a short file the values the loader last
/// read, up to the first value no read filled. Records come one at a time so the caller merges each before the next is read, as the loader does.
/// </summary>
public sealed class FcsGameLoader
{
    #region File

    /// <summary>
    /// The lowest file type the loader accepts.
    /// </summary>
    public const int FirstFileType = 8;

    /// <summary>
    /// The highest file type the loader accepts.
    /// </summary>
    public const int LastFileType = 17;

    /// <summary>
    /// The flags a record of a type below 15 is given (it stores none): RENAMED, since the loader renames an existing record from it and never logs a
    /// missing base, which is what a type-17 record with these flags does.
    /// </summary>
    const uint OldRecordFlags = 2;

    /// <summary>
    /// The file's bytes with the loader's stream rules.
    /// </summary>
    readonly LoaderStream _stream;

    /// <summary>
    /// The file's path as the loader is given it: searched for <c>.base</c> and named in the out-of-date warning.
    /// </summary>
    readonly string _path;

    /// <summary>
    /// Whether a record of <paramref name="stringId"/> exists (in the merged table, including this file's records merged so far).
    /// </summary>
    readonly Func<string, bool> _exists;

    /// <summary>
    /// The file type.
    /// </summary>
    readonly int _type;

    /// <summary>
    /// The loader's variable that holds the file type, then the header lengths, then the record count.
    /// </summary>
    readonly LoaderValue _count = new(4);

    /// <summary>
    /// The loader's variable that holds a type-16 header's version, then the next id.
    /// </summary>
    readonly LoaderValue _nextId = new(4);

    /// <summary>
    /// The records read so far.
    /// </summary>
    int _recordsRead;

    /// <summary>
    /// The record count, once the header was read; null before.
    /// </summary>
    int? _recordCount;

    /// <summary>
    /// The file's type.
    /// </summary>
    public FcsFileType FileType => (FcsFileType)_type;

    /// <summary>
    /// Whether the file is of a type below 15: records without flags, numeric instance ids and states.
    /// </summary>
    bool IsOld => _type < 15;

    /// <summary>
    /// Whether the stream came to its end before the file's declared content did: a short or damaged file.
    /// </summary>
    public bool Damaged => _stream.Failed;

    /// <summary>
    /// The stream position where reading stopped because the loader would go on with a value no read filled; null when it did not.
    /// </summary>
    public long? StoppedAt { get; private set; }

    /// <summary>
    /// The string ids of the records for which the loader shows its "Mod Warning" box (an out-of-date GLOBAL_CONSTANTS change), in order.
    /// </summary>
    public List<string> OutdatedConstants { get; } = [];

    /// <summary>
    /// Whether the file at <paramref name="path"/> opens and starts with a file type the loader accepts: what decides whether the game loads a data
    /// file at all (docs/formats/fcs-mod.md, "Files that fail to load"); damage past it does not.
    /// </summary>
    public static bool Opens(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> type = stackalloc byte[4];
            return stream.ReadAtLeast(type, 4, throwOnEndOfStream: false) == 4 && IsAccepted(BitConverter.ToInt32(type));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts reading <paramref name="data"/>: its first int must be a file type from 8 to 17, else <see cref="FcsFormatException"/> (the game does not
    /// load it). <paramref name="entryName"/> is the load entry's name in the game's bytes (<c>base</c>, a mod's name, empty for a translation file),
    /// which names the instances of the old types; <paramref name="exists"/> tells whether a record is loaded already.
    /// </summary>
    public FcsGameLoader(byte[] data, string path, string entryName, FcsLoaderSession session, Func<string, bool> exists)
    {
        _stream = new LoaderStream(data);
        _path = path;
        _entryName = entryName;
        _containerName = entryName.Length > 0 ? entryName : ContainerName(path);
        _tables = new LoaderTables(_stream, session.Values);
        _exists = exists;
        _stateLength.Set(entryName.Length > 0 ? 2 : 1);

        // A file shorter than the type leaves it unread: Meitou does not load it (docs/formats/fcs-mod.md, "Files that fail to load").
        _count.Read(_stream);
        if (_stream.Failed) throw new FcsFormatException("The file is shorter than its file type.", 0);

        _type = _count.Int32;
        if (!IsAccepted(_type)) throw new FcsFormatException($"Invalid data file (file type {_type}).", 0);
    }

    /// <summary>
    /// Reads the next record, or returns null when the file's records are done or reading stopped (<see cref="StoppedAt"/>). A record the stop cut
    /// short comes back with what was read of it, as the loader has merged that much.
    /// </summary>
    public FcsRecord? Next()
    {
        if (StoppedAt is not null) return null;

        _current = null;
        try
        {
            _recordCount ??= ReadHeader();
            if (_recordsRead >= _recordCount) return null;

            _recordsRead++;
            ReadRecord();
            return _current;
        }
        catch (UnreadValueException)
        {
            StoppedAt = _stream.Position;
            return _current;
        }
    }

    /// <summary>
    /// The header as the loader reads it (type 17: its length, then skipped; type 16: the version and four skipped strings; below: none), the next id
    /// and the record count.
    /// </summary>
    int ReadHeader()
    {
        if (_type == 17)
        {
            _count.Read(_stream);
            _stream.Skip(_count.Int32);
        }
        else if (_type == 16)
        {
            _nextId.Read(_stream);
            for (int i = 0; i < 4; i++)
            {
                _count.Read(_stream);
                _stream.Skip(_count.Int32);
            }
        }

        _nextId.Read(_stream);
        _count.Read(_stream);
        return _count.Int32;
    }

    /// <summary>
    /// Whether the loader accepts file type <paramref name="type"/>.
    /// </summary>
    static bool IsAccepted(int type) => type is >= FirstFileType and <= LastFileType;

    #endregion

    #region Record

    /// <summary>
    /// The record being read; null until its fixed fields were read.
    /// </summary>
    FcsRecord? _current;

    /// <summary>
    /// Record string ids of this file by their numeric id (types below 15), which instance states name.
    /// </summary>
    readonly Dictionary<int, string> _stringIdsById = [];

    /// <summary>
    /// The variable of a record's size field, read and not used.
    /// </summary>
    readonly LoaderValue _byteSize = new(4);

    /// <summary>
    /// The variable of a record's type.
    /// </summary>
    readonly LoaderValue _recordType = new(4);

    /// <summary>
    /// The variable of a record's numeric id.
    /// </summary>
    readonly LoaderValue _recordId = new(4);

    /// <summary>
    /// The variable of a record's name length.
    /// </summary>
    readonly LoaderValue _nameLength = new(4);

    /// <summary>
    /// The variable of a record's string id length.
    /// </summary>
    readonly LoaderValue _stringIdLength = new(4);

    /// <summary>
    /// The variable of a record's flags (types from 15).
    /// </summary>
    readonly LoaderValue _flags = new(4);

    /// <summary>
    /// Reads one record into <see cref="_current"/>: the fixed fields, the tag filter, the value tables, the references and the instances.
    /// </summary>
    void ReadRecord()
    {
        _byteSize.Read(_stream);
        _recordType.Read(_stream);
        _recordId.Read(_stream);
        ReadString(_names, NameA, NameSize, _nameLength);
        ReadString(_names, NameB, NameSize, _stringIdLength);

        // A numeric id an earlier instance state named before this record came gets that state's text as its string id.
        int id = _recordId.Int32;
        if (_ingameNames.TryGetValue(id, out var ingame)) PutCString(NameB, ingame);

        _current = StartRecord(id);
        if (IsOld) _stringIdsById[id] = _current.StringId;

        var filter = ReadTags(_current);
        ReadTables(_current, filter);
        ReadReferences(_current, filter);
        ReadInstances(_current);
    }

    /// <summary>
    /// The record of the fixed fields: its string id and flags; the name is taken only when the loader takes it (a new record, or a rename).
    /// </summary>
    FcsRecord StartRecord(int id)
    {
        uint flags = OldRecordFlags;
        if (!IsOld)
        {
            _flags.Read(_stream);
            flags = _flags.UInt32;
        }

        var record = new FcsRecord { Type = _recordType.Int32, Id = id, StringId = _names.CString(NameB), Flags = flags };
        if (!_exists(record.StringId) || record.IsRenamed) record.Name = _names.CString(NameA);
        return record;
    }

    #endregion

    #region Tags

    /// <summary>
    /// The record type whose tagged changes the loader drops with a warning in types 11 and 13 (GLOBAL_CONSTANTS).
    /// </summary>
    const int ConstantsType = 27;

    /// <summary>
    /// The variable of a record's tag count.
    /// </summary>
    readonly LoaderValue _tagCount = new(4);

    /// <summary>
    /// The variable of a tag's key length.
    /// </summary>
    readonly LoaderValue _tagKeyLength = new(4);

    /// <summary>
    /// The variable of a tag's byte.
    /// </summary>
    readonly LoaderValue _tagByte = new(1);

    /// <summary>
    /// Reads the tag block of types 11, 13 and 14 into the record's filter (empty: every key passes; docs/formats/fcs-mod.md, "Game loader"). An
    /// out-of-date GLOBAL_CONSTANTS change keeps its filter empty and is noted in <see cref="OutdatedConstants"/>; a path with <c>.base</c> empties it.
    /// </summary>
    Dictionary<string, byte> ReadTags(FcsRecord record)
    {
        var filter = new Dictionary<string, byte>(StringComparer.Ordinal);
        if (_type is not (11 or 13 or 14)) return filter;

        _tagCount.Read(_stream);
        int count = _tagCount.Int32;
        bool outdated = count >= 1 && _type <= 13 && _recordType.Int32 == ConstantsType;
        for (int i = 0; i < count; i++)
        {
            bool repeats = _stream.Failed;
            ReadString(_names, NameA, NameSize, _tagKeyLength);
            _tagByte.Read(_stream);
            if (!outdated) filter[_names.CString(NameA)] = _tagByte.Byte;
            if (repeats) break;
        }

        if (outdated) OutdatedConstants.Add(record.StringId);
        if (count != 0 && filter.Count > 0 && _path.Contains(".base", StringComparison.Ordinal)) filter.Clear();
        return filter;
    }

    /// <summary>
    /// Whether <paramref name="filter"/> passes <paramref name="key"/>: an empty filter passes every key, else the key needs a non-zero tag.
    /// </summary>
    internal static bool Passes(Dictionary<string, byte> filter, string key) => filter.Count == 0 || (filter.TryGetValue(key, out var tag) && tag != 0);

    #endregion

    #region Tables

    /// <summary>
    /// The reader of the seven value tables.
    /// </summary>
    readonly LoaderTables _tables;

    /// <summary>
    /// Reads the seven value tables in file order; vec3 and vec4 exist from type 9.
    /// </summary>
    void ReadTables(FcsRecord record, Dictionary<string, byte> filter) => _tables.Read(record, _type > 8, filter);

    #endregion

    #region References

    /// <summary>
    /// The variable of a record's reference list count.
    /// </summary>
    readonly LoaderValue _listCount = new(4);

    /// <summary>
    /// The variable of a list name's length.
    /// </summary>
    readonly LoaderValue _listNameLength = new(4);

    /// <summary>
    /// The variable of a list's reference count.
    /// </summary>
    readonly LoaderValue _referenceCount = new(4);

    /// <summary>
    /// The variable of a reference target's length.
    /// </summary>
    readonly LoaderValue _targetLength = new(4);

    /// <summary>
    /// The variable of a reference's first value.
    /// </summary>
    readonly LoaderValue _value0 = new(4);

    /// <summary>
    /// The variable of a reference's second value (types from 11).
    /// </summary>
    readonly LoaderValue _value1 = new(4);

    /// <summary>
    /// The variable of a reference's third value (types from 11).
    /// </summary>
    readonly LoaderValue _value2 = new(4);

    /// <summary>
    /// Reads the reference lists: a list name that repeats adds to the list; a reference whose three values are all <see cref="int.MaxValue"/> is a
    /// removal and passes any filter, any other needs <c>-ref-&lt;target&gt;</c> to pass. Below type 11 the second and third values are 0.
    /// </summary>
    void ReadReferences(FcsRecord record, Dictionary<string, byte> filter)
    {
        _listCount.Read(_stream);
        int lists = _listCount.Int32;
        for (int l = 0; l < lists; l++)
        {
            bool repeats = _stream.Failed;
            ReadString(_names, NameA, NameSize, _listNameLength);
            _referenceCount.Read(_stream);
            int count = _referenceCount.Int32;
            for (int i = 0; i < count; i++)
                if (ReadReference(record, filter)) break;
            if (repeats) break;
        }
    }

    /// <summary>
    /// Reads one reference into the list named in buffer A; true when the stream had failed before it, so the rest of the list repeats it.
    /// </summary>
    bool ReadReference(FcsRecord record, Dictionary<string, byte> filter)
    {
        bool repeats = _stream.Failed;
        ReadString(_names, NameB, NameSize, _targetLength);
        _value0.Read(_stream);
        if (_type > 10)
        {
            _value1.Read(_stream);
            _value2.Read(_stream);
        }

        int v0 = _value0.Int32;
        const int removed = FcsReference.RemovedValue;
        if (v0 == removed && Value1() == removed && Value2() == removed)
            AddReference(record, FcsReference.Removed(_names.CString(NameB)));
        else if (Passes(filter, "-ref-" + _names.CString(NameB)))
            AddReference(record, new FcsReference(_names.CString(NameB), v0, Value1(), Value2()));
        return repeats;
    }

    /// <summary>
    /// The reference's second value: the one read, or 0 below type 11.
    /// </summary>
    int Value1() => _type > 10 ? _value1.Int32 : 0;

    /// <summary>
    /// The reference's third value: the one read, or 0 below type 11.
    /// </summary>
    int Value2() => _type > 10 ? _value2.Int32 : 0;

    /// <summary>
    /// Adds <paramref name="reference"/> to the list named in buffer A.
    /// </summary>
    void AddReference(FcsRecord record, FcsReference reference)
    {
        string list = _names.CString(NameA);
        if (!record.References.TryGetValue(list, out var refs)) record.References[list] = refs = [];
        refs.Add(reference);
    }

    #endregion

    #region Instances

    /// <summary>
    /// The rotation each record starts with (the quaternion constructor's w 1, x y z 0).
    /// </summary>
    static readonly byte[] IdentityRotation = [0, 0, 0x80, 0x3F, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// The load entry's name, which names the instances of the old types (<c>&lt;n&gt;-&lt;name&gt;</c>).
    /// </summary>
    readonly string _entryName;

    /// <summary>
    /// The name the old types' made-up state ids carry: the entry's name, else the path's file name.
    /// </summary>
    readonly string _containerName;

    /// <summary>
    /// The made-up ids (<c>&lt;n&gt;-&lt;name&gt;-INGAME</c>) of old-type states that named a numeric id no earlier record of the file had.
    /// </summary>
    readonly Dictionary<int, string> _ingameNames = [];

    /// <summary>
    /// The variable of a record's instance count.
    /// </summary>
    readonly LoaderValue _instanceCount = new(4);

    /// <summary>
    /// The variable of an old-type instance's numeric id.
    /// </summary>
    readonly LoaderValue _instanceNumber = new(4);

    /// <summary>
    /// The variable of an instance id's length.
    /// </summary>
    readonly LoaderValue _instanceIdLength = new(4);

    /// <summary>
    /// The variable of an instance target's length.
    /// </summary>
    readonly LoaderValue _instanceTargetLength = new(4);

    /// <summary>
    /// The variable of an instance's position; nothing resets it between records.
    /// </summary>
    readonly LoaderValue _position = new(12);

    /// <summary>
    /// The variable of an instance's rotation (w, x, y, z), reset to <see cref="IdentityRotation"/> for each record.
    /// </summary>
    readonly LoaderValue _rotation = new(16);

    /// <summary>
    /// The variable of an instance's state count.
    /// </summary>
    readonly LoaderValue _stateCount = new(4);

    /// <summary>
    /// The variable of an old-type state's numeric id.
    /// </summary>
    readonly LoaderValue _stateNumber = new(4);

    /// <summary>
    /// The variable of a state's length; before the first state it holds 2, or 1 when the entry has no name (it held a flag before).
    /// </summary>
    readonly LoaderValue _stateLength = new(4);

    /// <summary>
    /// Reads the instances: an empty target removes the instance, any other sets it and adds its states.
    /// </summary>
    void ReadInstances(FcsRecord record)
    {
        _rotation.Set(IdentityRotation);
        _instanceCount.Read(_stream);
        int count = _instanceCount.Int32;
        for (int i = 0; i < count; i++)
        {
            bool repeats = _stream.Failed;
            var instance = ReadInstance();
            record.Instances.Add(instance);
            if (!repeats) continue;

            // From here every instance reads the same values again: the same instance is set again and adds the same states again.
            var states = instance.States.ToList();
            for (int n = count - i - 1; n > 0; n--) instance.States.AddRange(states);
            break;
        }
    }

    /// <summary>
    /// Reads one instance: its id, target, position, rotation and states.
    /// </summary>
    FcsInstance ReadInstance()
    {
        if (IsOld)
        {
            _instanceNumber.Read(_stream);
            Format(NameA, $"{_instanceNumber.Int32}-{_entryName}");
        }
        else
        {
            ReadString(_names, NameA, NameSize, _instanceIdLength);
        }

        ReadString(_names, NameB, NameSize, _instanceTargetLength);
        _position.Read(_stream);
        _rotation.Read(_stream);
        var states = ReadStates();
        if (_names.IsNul(NameB)) return new FcsInstance { Id = _names.CString(NameA) };

        var instance = new FcsInstance
        {
            Target = _names.CString(NameB),
            Id = _names.CString(NameA),
            Position = new Vector3(_position.Single(0), _position.Single(4), _position.Single(8)),
            Rotation = new Quaternion(_rotation.Single(4), _rotation.Single(8), _rotation.Single(12), _rotation.Single(0)),
        };
        instance.States.AddRange(states);
        return instance;
    }

    /// <summary>
    /// Reads an instance's states: strings, or below type 15 numeric ids (<see cref="OldState"/>).
    /// </summary>
    List<string> ReadStates()
    {
        _stateCount.Read(_stream);
        int count = _stateCount.Int32;
        var states = new List<string>();
        for (int i = 0; i < count; i++)
        {
            if (IsOld)
            {
                states.Add(OldState());
                continue;
            }

            ReadString(_names, NameC, NameSize, _stateLength);
            states.Add(_names.CString(NameC));
        }

        return states;
    }

    /// <summary>
    /// An old-type state: the string id of this file's earlier record with that numeric id, else the made-up <c>&lt;n&gt;-&lt;name&gt;-INGAME</c>,
    /// which a later record with that numeric id takes as its string id.
    /// </summary>
    string OldState()
    {
        _stateNumber.Read(_stream);
        int number = _stateNumber.Int32;
        if (_stringIdsById.TryGetValue(number, out var stringId)) return stringId;

        Format(NameC, $"{number}-{_containerName}-INGAME");
        string made = _names.CString(NameC);
        _ingameNames[number] = made;
        return made;
    }

    /// <summary>
    /// The part of <paramref name="path"/> after its last slash or backslash.
    /// </summary>
    static string ContainerName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    #endregion

    #region Strings

    /// <summary>
    /// The size of each of the three name buffers.
    /// </summary>
    const int NameSize = 512;

    /// <summary>
    /// Buffer A: names, tag keys, list names, instance ids.
    /// </summary>
    const int NameA = 0;

    /// <summary>
    /// Buffer B, right after A: string ids, reference targets, instance targets.
    /// </summary>
    const int NameB = NameSize;

    /// <summary>
    /// Buffer C, right after B: states; the stack cookie follows it.
    /// </summary>
    const int NameC = 2 * NameSize;

    /// <summary>
    /// The three name buffers in a row and the cookie byte after them: a string's terminator one past its buffer lands in the next.
    /// </summary>
    readonly LoaderBuffer _names = new((3 * NameSize) + 1);

    /// <summary>
    /// <see cref="LoaderBuffer.ReadString"/> from the file.
    /// </summary>
    void ReadString(LoaderBuffer buffer, int offset, int size, LoaderValue length) => buffer.ReadString(_stream, offset, size, length);

    /// <summary>
    /// Writes <paramref name="text"/> (bytes as <see cref="LosslessUtf8"/> keeps them) into the name buffer at <paramref name="offset"/> as the
    /// loader's bounded print does: at most 512 bytes, with a terminator only when shorter.
    /// </summary>
    void Format(int offset, string text)
    {
        var bytes = LosslessUtf8.GetBytes(text);
        int n = Math.Min(bytes.Length, NameSize);
        _names.Put(offset, bytes.AsSpan(0, n));
        if (n < NameSize) _names.Put(offset + n, 0);
    }

    /// <summary>
    /// Copies <paramref name="text"/> with its terminator into the name buffer at <paramref name="offset"/>.
    /// </summary>
    void PutCString(int offset, string text)
    {
        var bytes = LosslessUtf8.GetBytes(text);
        _names.Put(offset, bytes);
        _names.Put(offset + bytes.Length, 0);
    }

    #endregion
}