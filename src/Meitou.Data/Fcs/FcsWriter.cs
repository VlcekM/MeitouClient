using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>
/// Writes <see cref="FcsFile"/>s in the same layout <see cref="FcsReader"/> reads.
/// </summary>
public static class FcsWriter
{
    /// <summary>
    /// Writes <paramref name="file"/> to a new file at <paramref name="path"/> (strict UTF-8, real record sizes).
    /// </summary>
    public static void WriteFile(FcsFile file, string path)
    {
        using var stream = File.Create(path);
        Write(file, stream);
    }

    /// <summary>
    /// Writes <paramref name="file"/> to <paramref name="stream"/>, which is left open; <paramref name="lossless"/> encodes strings with
    /// <see cref="LosslessUtf8"/> (save files).
    /// </summary>
    /// <param name="keepByteSizes">
    /// Write each record's <see cref="FcsRecord.ByteSize"/> as stored instead of its real size. World-state files
    /// (<c>.zone</c>, <c>.level</c>) contain sizes that don't match their records; this keeps them byte-exact.
    /// </param>
    public static void Write(FcsFile file, Stream stream, bool keepByteSizes = false, bool lossless = false)
    {
        using var w = new FcsBinaryWriter(stream, lossless);
        w.Write((int)file.FileType);

        if (file.FileType == FcsFileType.V17)
        {
            var header = new MemoryStream();
            using (var hw = new FcsBinaryWriter(header, lossless))
                WriteHeaderBody(hw, file);
            w.Write(checked((int)header.Length));
            header.WriteTo(stream);
        }
        else if (file.FileType != FcsFileType.V15)
        {
            WriteHeaderBody(w, file);
        }

        w.Write(file.NextId);
        w.Write(file.Records.Count);

        foreach (var record in file.Records)
            WriteRecord(w, record, keepByteSizes, lossless);
    }

    /// <summary>
    /// Writes the header from the version on; for V17 also the merge info, the delete requests and <see cref="FcsFile.HeaderTail"/>. Delete requests
    /// without merge info throw, since the reader could not tell them apart.
    /// </summary>
    static void WriteHeaderBody(BinaryWriter w, FcsFile file)
    {
        w.Write(file.Version);
        WriteString(w, file.Author);
        WriteString(w, file.Description);
        WriteString(w, string.Join(',', file.Dependencies));
        WriteString(w, string.Join(',', file.References));
        if (file.FileType != FcsFileType.V17) return;

        if (file.Merge is { } merge)
        {
            w.Write(merge.SaveCounter);
            w.Write(merge.LastMergeResolve);
            w.Write(checked((byte)merge.Merged.Count));
            foreach (var m in merge.Merged)
            {
                WriteString(w, m.File);
                w.Write(m.Value1);
                w.Write(m.Value2);
            }
        }
        if (file.DeleteRequests is { } deletes)
        {
            if (file.Merge is null)
                throw new InvalidOperationException("A header with delete requests must also have merge info.");
            w.Write(checked((byte)deletes.Count));
            foreach (var d in deletes)
            {
                WriteString(w, d.File);
                w.Write(d.Version);
                WriteString(w, string.Join(':', d.Items));
            }
        }
        w.Write(file.HeaderTail);
    }

    /// <summary>
    /// Writes a record with its leading size field: the stored <see cref="FcsRecord.ByteSize"/> when kept or 0, else the real size.
    /// </summary>
    static void WriteRecord(BinaryWriter output, FcsRecord record, bool keepByteSize, bool lossless)
    {
        // Buffered so the leading size field can hold the record's real size.
        var buffer = new MemoryStream();
        using (var w = new FcsBinaryWriter(buffer, lossless))
            WriteRecordBody(w, record);
        output.Write(keepByteSize || record.ByteSize == 0 ? record.ByteSize : checked((uint)buffer.Length + 4));
        output.Flush();
        buffer.WriteTo(output.BaseStream);
    }

    /// <summary>
    /// Writes a record after its size field: fixed fields, the seven value maps, the reference lists and the instances.
    /// </summary>
    static void WriteRecordBody(BinaryWriter w, FcsRecord record)
    {
        w.Write(record.Type);
        w.Write(record.Id);
        WriteString(w, record.Name);
        WriteString(w, record.StringId);
        w.Write(record.Flags);

        WriteMap(w, record.Bools, (w, v) => w.Write((byte)(v ? 1 : 0)));
        WriteMap(w, record.Floats, (w, v) => w.Write(v));
        WriteMap(w, record.Ints, (w, v) => w.Write(v));
        WriteMap(w, record.Vector3S, WriteVector3);
        WriteMap(w, record.Vector4S, WriteVector4);
        WriteMap(w, record.Strings, WriteString);
        WriteMap(w, record.Filenames, WriteString);

        w.Write(record.References.Count);
        foreach (var (category, list) in record.References)
        {
            WriteString(w, category);
            w.Write(list.Count);
            foreach (var reference in list)
            {
                WriteString(w, reference.TargetStringId);
                w.Write(reference.Value0);
                w.Write(reference.Value1);
                w.Write(reference.Value2);
            }
        }

        w.Write(record.Instances.Count);
        foreach (var instance in record.Instances)
        {
            WriteString(w, instance.Id);
            WriteString(w, instance.Target);
            WriteVector3(w, instance.Position);
            WriteRotation(w, instance.Rotation);
            w.Write(instance.States.Count);
            foreach (var state in instance.States)
                WriteString(w, state);
        }
    }

    /// <summary>
    /// Writes the count and each key-value pair of <paramref name="map"/> in its order.
    /// </summary>
    static void WriteMap<T>(BinaryWriter w, OrderedDictionary<string, T> map, Action<BinaryWriter, T> writeValue)
    {
        w.Write(map.Count);
        foreach (var (key, value) in map)
        {
            WriteString(w, key);
            writeValue(w, value);
        }
    }

    /// <summary>
    /// Writes x, y, z.
    /// </summary>
    static void WriteVector3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    /// <summary>
    /// Writes x, y, z, w (the stored order of quaternion fields).
    /// </summary>
    static void WriteVector4(BinaryWriter w, Vector4 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
        w.Write(v.W);
    }

    /// <summary>
    /// Writes an instance rotation as w, x, y, z.
    /// </summary>
    static void WriteRotation(BinaryWriter w, Quaternion q)
    {
        w.Write(q.W);
        w.Write(q.X);
        w.Write(q.Y);
        w.Write(q.Z);
    }

    /// <summary>
    /// Writes a length-prefixed string: strict UTF-8, or <see cref="LosslessUtf8"/> when the writer is lossless.
    /// </summary>
    static void WriteString(BinaryWriter w, string s)
    {
        var bytes = w is FcsBinaryWriter { Lossless: true } ? LosslessUtf8.GetBytes(s) : FcsReader.Encoding.GetBytes(s);
        w.Write(bytes.Length);
        w.Write(bytes);
    }
}

/// <summary>
/// A binary writer that also tells <see cref="FcsWriter"/> how to encode strings.
/// </summary>
sealed class FcsBinaryWriter(Stream stream, bool lossless) : BinaryWriter(stream, FcsReader.Encoding, leaveOpen: true)
{
    /// <summary>
    /// True when strings are encoded with <see cref="LosslessUtf8"/> (save files) rather than strict UTF-8.
    /// </summary>
    public bool Lossless { get; } = lossless;
}