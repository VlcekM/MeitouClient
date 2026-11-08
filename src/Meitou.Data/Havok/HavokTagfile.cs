using System.Text;

namespace Meitou.Data.Havok;

/// <summary>A class described in a tagfile: its members after its parent's (docs/formats/havok-tagfile.md).</summary>
public sealed class HkClass(string name, int version, HkClass? parent, IReadOnlyList<HkMember> own)
{
    public string Name { get; } = name;
    public int Version { get; } = version;
    public HkClass? Parent { get; } = parent;
    public IReadOnlyList<HkMember> Own { get; } = own;
    /// <summary>Every member, the parent's first: the order of the presence bits and values.</summary>
    public IReadOnlyList<HkMember> All { get; } = [.. parent?.All ?? [], .. own];
    public override string ToString() => Name;
}

/// <summary>A member: its name and file type (low nibble the kind, 0x10 array, 0x20 tuple of <see cref="TupleSize"/>), and the name of its class for objects and structs.</summary>
public sealed record HkMember(string Name, int Type, int TupleSize, string? ClassName)
{
    public int Kind => Type & 0xF;
    public bool IsArray => (Type & 0x10) != 0;
    public bool IsTuple => (Type & 0x20) != 0;
}

/// <summary>An object or struct read from a tagfile: the members present in the file by name.</summary>
public sealed class HkObject(HkClass type)
{
    public HkClass Type { get; } = type;
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
    public object? this[string member] => Fields.GetValueOrDefault(member);
    public override string ToString() => Type.Name;
}

/// <summary>
/// A clean-room reader of Havok's binary tagfile (magic <c>CAB00D1E D011FACE</c>, versions 2..5, the format of Kenshi's <c>.hkt</c> files; docs/formats/havok-tagfile.md).
/// Values come back as <see cref="long"/> (ints), <see cref="float"/>, <see cref="byte"/>, <see cref="string"/>, <see cref="HkObject"/> (structs and pointers),
/// <see cref="float"/>[] (vectors), and <see cref="object"/>[] (arrays and tuples; arrays of bytes, ints and floats as <see cref="byte"/>[], <see cref="long"/>[], <see cref="float"/>[]).
/// </summary>
public sealed class HavokTagfile
{
    const uint Magic0 = 0xCAB00D1E, Magic1 = 0xD011FACE;
    const int KindVoid = 0, KindByte = 1, KindInt = 2, KindReal = 3, KindVec4 = 4, KindVec16 = 7, KindObject = 8, KindStruct = 9, KindString = 10;

    readonly byte[] _data;
    int _at;
    readonly List<string?> _strings = ["", null];
    readonly List<HkClass?> _classes = [null];
    readonly Dictionary<string, HkClass> _byName = new(StringComparer.Ordinal);
    readonly List<HkObject?> _objects = [null];
    // Pointers to objects not read yet: filled in once the file is read.
    readonly List<(Action<HkObject?> Set, int Index)> _forward = [];

    public int Version { get; private set; }
    public string? Sdk { get; private set; }
    /// <summary>The first object of the file (an <c>hkRootLevelContainer</c>).</summary>
    public HkObject? Root { get; private set; }
    public IReadOnlyList<HkClass> Classes => [.. _classes.Skip(1).OfType<HkClass>()];

    HavokTagfile(byte[] data) => this._data = data;

    public static bool IsTagfile(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && BitConverter.ToUInt32(data) == Magic0 && BitConverter.ToUInt32(data[4..]) == Magic1;

    public static HavokTagfile Read(byte[] data)
    {
        if (!IsTagfile(data)) throw new InvalidDataException("not a Havok binary tagfile (magic mismatch)");
        var file = new HavokTagfile(data) { _at = 8 };
        file.ReadTags();
        return file;
    }

    public static HavokTagfile ReadFile(string path) => Read(File.ReadAllBytes(Content.GameFiles.Resolve(path)));

    /// <summary>The named variant of the root container whose class is <paramref name="className"/> (null: none).</summary>
    public HkObject? Variant(string className)
    {
        if (Root?["namedVariants"] is not object[] variants) return null;
        foreach (var v in variants)
            if (v is HkObject { } named && named["className"] as string == className) return named["variant"] as HkObject;
        return null;
    }

    void ReadTags()
    {
        while (true)
        {
            int tag = Int();
            switch (tag)
            {
                case 1: FileInfo(); break;
                case 2: _classes.Add(ClassInfo()); break;
                case 3 or 4 or 6: Object(tag, null); break;
                case 7:
                    foreach (var (set, index) in _forward) set(index < _objects.Count ? _objects[index] : null);
                    _forward.Clear();
                    Root = _objects.Count > 1 ? _objects[1] : null;
                    return;
                default: throw new InvalidDataException($"tagfile: unknown tag {tag} at {_at}");
            }
        }
    }

    void FileInfo()
    {
        Version = Int();
        if (Version is < 2 or > 5) throw new InvalidDataException($"tagfile: unsupported version {Version}");
        if (Version > 3) Sdk = String();
        if (Version > 4)
        {
            _at += 2;   // not used by the reader beyond skipping (a 16-bit value, then a count of 16-bit values)
            int count = BitConverter.ToUInt16(_data, _at);
            _at += 2 + 2 * count;
        }
    }

    HkClass ClassInfo()
    {
        string name = String() ?? "";
        int version = Int();
        var parent = _classes[Int()];
        int count = Int();
        var members = new List<HkMember>(count);
        for (int i = 0; i < count; i++)
        {
            string member = String() ?? "";
            int type = Int();
            int tuple = (type & 0x20) != 0 ? Int() : 0;
            string? cls = (type & 0xF) is KindObject or KindStruct ? String() : null;
            members.Add(new HkMember(member, type, tuple, cls));
        }
        var made = new HkClass(name, version, parent, members);
        _byName[name] = made;
        return made;
    }

    /// <summary>An object: tag 5 a back reference, 6 null, 3 or 4 (remembered) its class (unless given), presence bits and the members present.</summary>
    HkObject? Object(int tag, HkClass? given)
    {
        if (tag == 5) return _objects[Int()];
        if (tag == 6) return null;
        var type = given ?? _classes[Int()] ?? throw new InvalidDataException("tagfile: object of no class");
        var o = new HkObject(type);
        if (tag == 4) _objects.Add(o);
        var members = type.All;
        var present = Bits(members.Count);
        for (int i = 0; i < members.Count; i++)
            if (present[i]) o.Fields[members[i].Name] = Value(members[i], o, members[i].Name);
        return o;
    }

    bool[] Bits(int count)
    {
        var bits = new bool[count];
        for (int i = 0; i < count; i++) bits[i] = (_data[_at + (i >> 3)] >> (i & 7) & 1) != 0;
        _at += (count + 7) >> 3;
        return bits;
    }

    object? Value(HkMember m, HkObject owner, string name)
    {
        if (m.IsArray) return Array(m, Int());
        if (m.IsTuple) return Tuple(m, m.TupleSize);
        return m.Kind switch
        {
            KindVoid => null,
            KindByte => _data[_at++],
            KindInt => Long(),
            KindReal => Float(),
            >= KindVec4 and <= KindVec16 => Floats(VectorSize(m.Kind)),
            KindObject => Pointer(v => owner.Fields[name] = v),
            KindStruct => Object(3, ClassOf(m)),
            KindString => String(),
            _ => throw new InvalidDataException($"tagfile: member type {m.Type}"),
        };
    }

    static int VectorSize(int kind) => 4 * (kind - KindVec4 + 1);

    /// <summary>A tuple of <paramref name="size"/>: stored like an array of that length.</summary>
    object? Tuple(HkMember m, int size) => Elements(m with { Type = m.Kind }, size);

    object? Array(HkMember m, int count)
    {
        if (m.Kind == KindStruct && m.ClassName is null) throw new InvalidDataException("tagfile: array of untyped structs");
        return Elements(m with { Type = m.Kind }, count);
    }

    /// <summary><paramref name="count"/> values of the element type <paramref name="e"/> (the array readers' layouts: structs column by column).</summary>
    object? Elements(HkMember e, int count)
    {
        switch (e.Kind)
        {
            case KindVoid: return null;
            case KindByte: { var b = _data.AsSpan(_at, count).ToArray(); _at += count; return b; }
            case KindReal: return Floats(count);
            case KindInt:
                {
                    if (Version > 2) Int();   // the stored width (4 or 8); both are varints
                    var v = new long[count];
                    for (int i = 0; i < count; i++) v[i] = Long();
                    return v;
                }
            case KindString: { var s = new object?[count]; for (int i = 0; i < count; i++) s[i] = String(); return s; }
            case >= KindVec4 and <= KindVec16:
                {
                    int size = VectorSize(e.Kind), stored = size == 4 ? Int() : size;
                    var v = new object?[count];
                    for (int i = 0; i < count; i++)
                    {
                        var f = new float[size];
                        for (int k = 0; k < stored; k++) f[k] = Float();
                        v[i] = f;
                    }
                    return v;
                }
            case KindObject:
                {
                    var v = new object?[count];
                    for (int i = 0; i < count; i++) { int slot = i; v[i] = Pointer(p => v[slot] = p); }
                    return v;
                }
            case KindStruct:
                {
                    var type = ClassOf(e);
                    var v = new HkObject[count];
                    for (int i = 0; i < count; i++) v[i] = new HkObject(type);
                    var members = type.All;
                    var present = Bits(members.Count);
                    for (int k = 0; k < members.Count; k++)
                    {
                        if (!present[k]) continue;
                        var m = members[k];
                        if (m.IsTuple && !m.IsArray && m.Kind is not (>= KindVec4 and <= KindVec16))
                            for (int i = 0; i < count; i++) v[i].Fields[m.Name] = Tuple(m, m.TupleSize);
                        else if (m.IsArray)
                            for (int i = 0; i < count; i++) v[i].Fields[m.Name] = Array(m, Int());
                        else if (m.IsTuple)
                            for (int i = 0; i < count; i++) v[i].Fields[m.Name] = Tuple(m, m.TupleSize);
                        else if (m.Kind == KindObject)
                            for (int i = 0; i < count; i++) { var target = v[i]; target.Fields[m.Name] = Pointer(p => target.Fields[m.Name] = p); }
                        else if (Elements(m, count) is Array column)
                            for (int i = 0; i < count; i++) v[i].Fields[m.Name] = column.GetValue(i);
                    }
                    return v;
                }
            default: throw new InvalidDataException($"tagfile: array of type {e.Type}");
        }
    }

    HkClass ClassOf(HkMember m) => m.ClassName is { } n && _byName.TryGetValue(n, out var c) ? c : throw new InvalidDataException($"tagfile: struct of unknown class {m.ClassName}");

    /// <summary>A pointer: the index of a remembered object (0 null); one not read yet is set once the file is read.</summary>
    HkObject? Pointer(Action<HkObject?> set)
    {
        int index = Int();
        if (index < _objects.Count) return _objects[index];
        _forward.Add((set, index));
        return null;
    }

    float[] Floats(int count)
    {
        var f = new float[count];
        for (int i = 0; i < count; i++) f[i] = Float();
        return f;
    }

    float Float()
    {
        float f = BitConverter.ToSingle(_data, _at);
        _at += 4;
        return f;
    }

    /// <summary>The tagfile's integer: bit 0 of the first byte the sign, then 6 bits, then 7 bits a byte while the top bit is set.</summary>
    long Long()
    {
        byte b = _data[_at++];
        bool negative = (b & 1) != 0;
        long v = (b >> 1) & 0x3F;
        int shift = 6;
        while ((b & 0x80) != 0)
        {
            b = _data[_at++];
            v |= (long)(b & 0x7F) << shift;
            shift += 7;
        }
        return negative ? -v : v;
    }

    int Int() => (int)Long();

    /// <summary>A string: a positive length and the bytes (remembered), or 0 or a negative index of a remembered one.</summary>
    string? String()
    {
        int n = Int();
        if (n <= 0) return _strings[-n];
        string s = Encoding.UTF8.GetString(_data, _at, n);
        _at += n;
        _strings.Add(s);
        return s;
    }
}