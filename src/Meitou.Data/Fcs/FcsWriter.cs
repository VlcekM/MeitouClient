using System.Numerics;

namespace Meitou.Data.Fcs;

/// <summary>Writes <see cref="FcsFile"/>s in the same layout <see cref="FcsReader"/> reads.</summary>
public static class FcsWriter
{
    public static void WriteFile(FcsFile file, string path)
    {
        using var stream = File.Create(path);
        Write(file, stream);
    }

    public static void Write(FcsFile file, Stream stream)
    {
        using var w = new BinaryWriter(stream, FcsReader.Encoding, leaveOpen: true);
        w.Write((int)file.FileType);
        if (file.FileType == FcsFileType.V17)
        {
            var header = new MemoryStream();
            using (var hw = new BinaryWriter(header, FcsReader.Encoding, leaveOpen: true))
                WriteHeaderBody(hw, file);
            w.Write(checked((int)header.Length));
            header.WriteTo(stream);
        }
        else
        {
            WriteHeaderBody(w, file);
        }

        w.Write(file.NextId);
        w.Write(file.Records.Count);
        foreach (var record in file.Records)
            WriteRecord(w, record);
    }

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

    static void WriteRecord(BinaryWriter output, FcsRecord record)
    {
        // Buffered so the leading size field can hold the record's real size.
        var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, FcsReader.Encoding, leaveOpen: true))
            WriteRecordBody(w, record);
        output.Write(record.ByteSize == 0 ? 0u : checked((uint)buffer.Length + 4));
        output.Flush();
        buffer.WriteTo(output.BaseStream);
    }

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
        WriteMap(w, record.Vector3s, WriteVector3);
        WriteMap(w, record.Vector4s, WriteVector4);
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

    static void WriteMap<T>(BinaryWriter w, OrderedDictionary<string, T> map, Action<BinaryWriter, T> writeValue)
    {
        w.Write(map.Count);
        foreach (var (key, value) in map)
        {
            WriteString(w, key);
            writeValue(w, value);
        }
    }

    static void WriteVector3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
    }

    static void WriteVector4(BinaryWriter w, Vector4 v)
    {
        w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W);
    }

    static void WriteRotation(BinaryWriter w, Quaternion q)
    {
        w.Write(q.W); w.Write(q.X); w.Write(q.Y); w.Write(q.Z);
    }

    static void WriteString(BinaryWriter w, string s)
    {
        var bytes = FcsReader.Encoding.GetBytes(s);
        w.Write(bytes.Length);
        w.Write(bytes);
    }
}
