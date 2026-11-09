using System.Buffers.Binary;
using System.Text;

namespace Meitou.Data.Audio;

/// <summary>
/// A Wwise sound bank (<c>.bnk</c>), read clean-room from the layout in docs/formats/wwise-bnk.md: the header, the embedded media
/// (<c>DIDX</c>/<c>DATA</c>), the bank-name table (<c>STID</c>) and the object hierarchy (<c>HIRC</c>). Kenshi's banks are version 112
/// except <c>SFX_Bank.bnk</c> (88), whose objects are kept undecoded.
/// </summary>
public sealed class WwiseBank
{
    /// <summary>
    /// The bank version whose objects the reader decodes.
    /// </summary>
    public const uint DecodedVersion = 112;

    /// <summary>
    /// The whole bank file; media are slices of it.
    /// </summary>
    readonly byte[] _data;
    /// <summary>
    /// The HIRC objects by id.
    /// </summary>
    readonly Dictionary<uint, WwiseObject> _byId = [];
    /// <summary>
    /// The embedded media by WEM id.
    /// </summary>
    readonly Dictionary<uint, WwiseMedia> _mediaById = [];

    /// <summary>
    /// A bank over <paramref name="data"/>; <see cref="Read"/> fills it.
    /// </summary>
    WwiseBank(byte[] data) => this._data = data;

    /// <summary>
    /// The bank version from <c>BKHD</c>: 112 for Kenshi's banks but <c>SFX_Bank.bnk</c> (88).
    /// </summary>
    public uint Version { get; private set; }
    /// <summary>
    /// The bank's id from <c>BKHD</c>: the hash of its name.
    /// </summary>
    public uint Id { get; private set; }
    /// <summary>
    /// The language id from <c>BKHD</c>.
    /// </summary>
    public uint LanguageId { get; private set; }
    /// <summary>
    /// The sections in file order: tag, offset of the body, body length.
    /// </summary>
    public IReadOnlyList<(string Tag, int Offset, int Length)> Sections { get; private set; } = [];
    /// <summary>
    /// The embedded WEMs in <c>DIDX</c> order; empty when the bank has none.
    /// </summary>
    public IReadOnlyList<WwiseMedia> Media { get; private set; } = [];
    /// <summary>
    /// The <c>STID</c> table: bank ids and names (Kenshi's banks list only themselves).
    /// </summary>
    public IReadOnlyDictionary<uint, string> BankNames { get; private set; } = new Dictionary<uint, string>();
    /// <summary>
    /// The <c>HIRC</c> objects in file order, decoded in a version <see cref="DecodedVersion"/> bank and raw otherwise.
    /// </summary>
    public IReadOnlyList<WwiseObject> Objects { get; private set; } = [];
    /// <summary>
    /// <c>STMG</c> (only in <c>Init.bnk</c>): each game parameter's default value, by id.
    /// </summary>
    public IReadOnlyDictionary<uint, float> GameParameterDefaults { get; private set; } = new Dictionary<uint, float>();
    /// <summary>
    /// <c>STMG</c>: each state group's default transition time in ms, by id.
    /// </summary>
    public IReadOnlyDictionary<uint, int> StateGroupTransitions { get; private set; } = new Dictionary<uint, int>();

    /// <summary>
    /// The <c>HIRC</c> object with id <paramref name="id"/>; null when the bank has none.
    /// </summary>
    public WwiseObject? Find(uint id) => _byId.GetValueOrDefault(id);
    /// <summary>
    /// The <c>HIRC</c> object with id <paramref name="id"/> when it is a <typeparamref name="T"/>; null otherwise.
    /// </summary>
    public T? Find<T>(uint id) where T : WwiseObject => _byId.GetValueOrDefault(id) as T;
    /// <summary>
    /// The embedded WEM with id <paramref name="id"/>; null when the bank does not hold it.
    /// </summary>
    public WwiseMedia? FindMedia(uint id) => _mediaById.TryGetValue(id, out var m) ? m : null;

    /// <summary>
    /// The bytes of an embedded WEM.
    /// </summary>
    public ReadOnlyMemory<byte> MediaBytes(WwiseMedia media) => _data.AsMemory(media.Offset, media.Length);

    /// <summary>
    /// <see cref="Read"/> of the file at <paramref name="path"/>, its case resolved as on disk.
    /// </summary>
    public static WwiseBank ReadFile(string path) => Read(File.ReadAllBytes(Content.GameFiles.Resolve(path)));

    /// <summary>
    /// Reads a whole bank: its sections, the media table, the bank names, the global settings and the objects. <see cref="InvalidDataException"/>
    /// when it is not a bank or a section runs past the end.
    /// </summary>
    public static WwiseBank Read(byte[] data)
    {
        var bank = new WwiseBank(data);
        var sections = new List<(string, int, int)>();
        int dataOffset = -1;
        var index = new List<(uint Id, uint Offset, uint Length)>();
        int p = 0;
        while (p < data.Length)
        {
            if (p + 8 > data.Length) throw new InvalidDataException($"Truncated section header at {p}");
            string tag = Encoding.ASCII.GetString(data, p, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4));
            int body = p + 8;
            if (length < 0 || body + (long)length > data.Length)
                throw new InvalidDataException($"Section {tag} at {p} runs past the end of the file");
            sections.Add((tag, body, length));
            var span = data.AsSpan(body, length);
            switch (tag)
            {
                case "BKHD":
                    if (p != 0) throw new InvalidDataException("BKHD is not the first section");
                    bank.Version = BinaryPrimitives.ReadUInt32LittleEndian(span);
                    bank.Id = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                    bank.LanguageId = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                    break;
                case "DIDX":
                    if (length % 12 != 0) throw new InvalidDataException("DIDX length is not a multiple of 12");
                    for (int i = 0; i < length; i += 12)
                        index.Add((BinaryPrimitives.ReadUInt32LittleEndian(span[i..]), BinaryPrimitives.ReadUInt32LittleEndian(span[(i + 4)..]),
                            BinaryPrimitives.ReadUInt32LittleEndian(span[(i + 8)..])));
                    break;
                case "DATA":
                    dataOffset = body;
                    break;
                case "STID":
                    bank.BankNames = ReadStringTable(span);
                    break;
                case "STMG":
                    ReadGlobalSettings(bank, span);
                    break;
            }
            p = body + length;
        }
        if (sections.Count == 0 || sections[0].Item1 != "BKHD")
            throw new InvalidDataException("Not a Wwise bank (no BKHD)");
        bank.Sections = sections;

        AddMedia(bank, sections, index, dataOffset);

        ReadObjects(bank, data, sections);
        return bank;
    }

    /// <summary>
    /// The media table (the <c>DIDX</c> entries) with each file's place in <c>DATA</c>; none when the bank has no index.
    /// </summary>
    static void AddMedia(WwiseBank bank, List<(string, int, int)> sections, List<(uint Id, uint Offset, uint Length)> index, int dataOffset)
    {
        if (index.Count == 0) return;

        var dataSection = sections.FirstOrDefault(s => s.Item1 == "DATA");
        if (dataOffset < 0) throw new InvalidDataException("DIDX without DATA");
        var media = new List<WwiseMedia>(index.Count);
        foreach (var (id, offset, len) in index)
        {
            if (offset + (long)len > dataSection.Item3)
                throw new InvalidDataException($"Media {id:X8} runs past DATA");
            var m = new WwiseMedia(id, dataOffset + (int)offset, (int)len, (int)offset);
            media.Add(m);
            bank._mediaById[id] = m;
        }
        bank.Media = media;
    }

    /// <summary>
    /// The object hierarchy (the <c>HIRC</c> section), indexed by id.
    /// </summary>
    static void ReadObjects(WwiseBank bank, byte[] data, List<(string, int, int)> sections)
    {
        foreach (var (tag, offset, length) in sections)
            if (tag == "HIRC")
                bank.Objects = WwiseHierarchyReader.Read(data.AsSpan(offset, length), bank.Version == DecodedVersion);
        foreach (var o in bank.Objects) bank._byId.TryAdd(o.Id, o);
    }

    /// <summary>
    /// <c>STMG</c>: f32 volume threshold, u16 max voices, u32 state group count, per group u32 id, u32 default transition ms, u32 count and
    /// (u32 from, u32 to, u32 ms) transitions; u32 switch group count, per group u32 id, u32 game parameter, u32 count and (f32, u32, u32) points;
    /// u32 game parameter count, per parameter u32 id, f32 default, u32 ramp type, f32 ramp up, f32 ramp down, u8 built-in.
    /// </summary>
    static void ReadGlobalSettings(WwiseBank bank, ReadOnlySpan<byte> s)
    {
        int p = 6;
        var transitions = new Dictionary<uint, int>();
        for (int i = U32(s, ref p); i > 0; i--)
        {
            uint id = (uint)U32(s, ref p);
            transitions[id] = U32(s, ref p);
            int count = U32(s, ref p);
            p += 12 * count;
        }
        for (int i = U32(s, ref p); i > 0; i--)
        {
            p += 8;
            int count = U32(s, ref p);
            p += 12 * count;
        }
        var defaults = new Dictionary<uint, float>();
        for (int i = U32(s, ref p); i > 0; i--)
        {
            uint id = (uint)U32(s, ref p);
            defaults[id] = BinaryPrimitives.ReadSingleLittleEndian(s[p..]);
            p += 4 + 13;
        }
        if (p != s.Length) throw new InvalidDataException($"STMG has {s.Length - p} bytes left over");
        bank.StateGroupTransitions = transitions;
        bank.GameParameterDefaults = defaults;
    }

    /// <summary>
    /// Reads the little-endian int32 at <paramref name="p"/> of an <c>STMG</c> section and moves past it.
    /// </summary>
    static int U32(ReadOnlySpan<byte> s, ref int p)
    {
        if (p + 4 > s.Length) throw new InvalidDataException("STMG is truncated");
        int v = BinaryPrimitives.ReadInt32LittleEndian(s[p..]);
        p += 4;
        return v;
    }

    /// <summary>
    /// <c>STID</c>: u32 string type (1), u32 count, then per entry u32 bank id, u8 length, the name (ASCII, no terminator).
    /// </summary>
    static Dictionary<uint, string> ReadStringTable(ReadOnlySpan<byte> span)
    {
        var names = new Dictionary<uint, string>();
        int count = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        int p = 8;
        for (int i = 0; i < count; i++)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(span[p..]);
            int len = span[p + 4];
            names[id] = Encoding.ASCII.GetString(span.Slice(p + 5, len));
            p += 5 + len;
        }
        if (p != span.Length) throw new InvalidDataException("STID has trailing bytes");
        return names;
    }
}

/// <summary>
/// An embedded WEM: its source id, where its bytes are in the bank file, and its offset inside <c>DATA</c> (what sounds refer to).
/// </summary>
public readonly record struct WwiseMedia(uint Id, int Offset, int Length, int DataOffset);