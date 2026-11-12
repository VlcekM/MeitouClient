namespace Meitou.Data.Fcs;

/// <summary>
/// A game-data file in the format written by the Forgotten Construction Set (<c>.base</c>, <c>.mod</c>).
/// Layout: docs/formats/fcs-mod.md.
/// </summary>
public sealed class FcsFile
{
    /// <summary>
    /// The file type number the file starts with; it decides the header layout (<see cref="FcsFileType"/>). New files are V17.
    /// </summary>
    public FcsFileType FileType { get; set; } = FcsFileType.V17;

    /// <summary>
    /// The header's version int ("Version" in the editor's mod header); 1 in all base files. A V15 file stores none.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The header's author string; empty in a V15 file.
    /// </summary>
    public string Author { get; set; } = "";

    /// <summary>
    /// The header's description string; empty in a V15 file.
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    /// Files that must load before this one, e.g. <c>gamedata.base</c>.
    /// </summary>
    public List<string> Dependencies { get; } = [];

    /// <summary>
    /// Files this one refers to without depending on them.
    /// </summary>
    public List<string> References { get; } = [];

    /// <summary>
    /// Editor bookkeeping from a <see cref="FcsFileType.V17"/> header (null if the header doesn't have it).
    /// </summary>
    public FcsMergeInfo? Merge { get; set; }

    /// <summary>
    /// Editor bookkeeping from a <see cref="FcsFileType.V17"/> header: records other mods asked to revert.
    /// Null when the header has no such section (older editor versions); empty when it has an empty one.
    /// </summary>
    public List<FcsDeleteRequest>? DeleteRequests { get; set; }

    /// <summary>
    /// Header bytes after the known fields; empty in every known file. Kept for byte-exact round-trips.
    /// </summary>
    public byte[] HeaderTail { get; set; } = [];

    /// <summary>
    /// The editor's id counter: the number part of the next <c>"&lt;n&gt;-&lt;file&gt;"</c> string id it hands out.
    /// </summary>
    public int NextId { get; set; }

    /// <summary>
    /// The records in file order.
    /// </summary>
    public List<FcsRecord> Records { get; } = [];
}

/// <summary>
/// The file type int at the start of a file: which header follows (docs/formats/fcs-mod.md "File").
/// </summary>
public enum FcsFileType
{
    /// <summary>
    /// No header at all: the next id and the records follow the type. Used by world state files (<c>.zone</c>, <c>.level</c>).
    /// </summary>
    V15 = 15,

    /// <summary>
    /// Header without a length prefix or editor bookkeeping (<c>gamedata.base</c>, <c>Newwworld.mod</c>).
    /// </summary>
    V16 = 16,

    /// <summary>
    /// Current format; the header is prefixed with its length and carries editor bookkeeping.
    /// </summary>
    V17 = 17,
}

/// <summary>
/// The editor's merge bookkeeping of a V17 header (<see cref="FcsFile.Merge"/>), kept for byte-exact round-trips.
/// </summary>
/// <param name="SaveCounter">Incremented on every save; record flags store the counter of their last change.</param>
/// <param name="LastMergeResolve">Save counter at which merges into this mod were last resolved.</param>
/// <param name="Merged">Mods merged into this one: (file, value1, value2).</param>
public sealed record FcsMergeInfo(uint SaveCounter, uint LastMergeResolve, List<FcsMergedMod> Merged);

/// <summary>
/// One mod in the editor's merge bookkeeping (<see cref="FcsMergeInfo.Merged"/>): its file name and two uints of unknown meaning, kept as read.
/// </summary>
public sealed record FcsMergedMod(string File, uint Value1, uint Value2);

/// <summary>
/// One entry of a V17 header's delete requests (<see cref="FcsFile.DeleteRequests"/>): a mod asking records of this file to be reverted.
/// </summary>
/// <param name="Items">String ids of the records to revert.</param>
public sealed record FcsDeleteRequest(string File, uint Version, List<string> Items);