namespace Meitou.Data.Fcs;

/// <summary>
/// A game-data file in the format written by the Forgotten Construction Set (<c>.base</c>, <c>.mod</c>).
/// Layout: docs/formats/fcs-mod.md.
/// </summary>
public sealed class FcsFile
{
    /// <summary>The value of <see cref="Marker"/> in every known file.</summary>
    public const int KnownMarker = 0x4C67BE;

    public FcsFileType FileType { get; set; } = FcsFileType.V17;
    public int Version { get; set; } = 1;
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Files that must load before this one, e.g. <c>gamedata.base</c>.</summary>
    public List<string> Dependencies { get; } = [];

    /// <summary>Files this one refers to without depending on them.</summary>
    public List<string> References { get; } = [];

    /// <summary>
    /// Unparsed end of a <see cref="FcsFileType.V17"/> header (meaning unknown), kept for byte-exact round-trips.
    /// </summary>
    public byte[] HeaderTail { get; set; } = [];

    /// <summary>Unknown int before the record count; <see cref="KnownMarker"/> in every known file.</summary>
    public int Marker { get; set; } = KnownMarker;

    public List<FcsRecord> Records { get; } = [];
}

public enum FcsFileType
{
    /// <summary>Older format without a length-prefixed header (<c>gamedata.base</c>, <c>Newwworld.mod</c>).</summary>
    V16 = 16,

    /// <summary>Current format; the header is prefixed with its length.</summary>
    V17 = 17,
}
