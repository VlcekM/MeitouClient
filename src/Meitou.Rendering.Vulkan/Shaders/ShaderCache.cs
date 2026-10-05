using System.Security.Cryptography;
using System.Text;

namespace Meitou.Rendering.Vulkan.Shaders;

/// <summary>One file per program: magic, version, both SPIR-V blobs, SHA-256 of what precedes it.</summary>
static class ShaderCache
{
    const uint Magic = 0x4D535056; // "MSPV"

    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "shader-cache");

    public static string Key(string vertex, string fragment, ShaderCompileOptions o, int version)
    {
        using var sha = SHA256.Create();
        void Add(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            sha.TransformBlock(BitConverter.GetBytes(b.Length), 0, 4, null, 0);
            sha.TransformBlock(b, 0, b.Length, null, 0);
        }
        Add("v" + version);
        Add($"remap={o.RemapClipDepth};vb={o.VertexBindingBase};fb={o.FragmentBindingBase}");
        Add(vertex);
        Add(fragment);
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public static bool TryRead(string directory, string key, int version, out byte[] vertex, out byte[] fragment)
    {
        vertex = fragment = [];
        try
        {
            string path = Path.Combine(directory, key + ".spvpair");
            if (!File.Exists(path)) return false;
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 12 + 8 + 32) return false;
            if (BitConverter.ToUInt32(data, 0) != Magic || BitConverter.ToInt32(data, 4) != version) return false;
            byte[] hash = SHA256.HashData(data.AsSpan(0, data.Length - 32));
            if (!hash.AsSpan().SequenceEqual(data.AsSpan(data.Length - 32))) return false;
            int pos = 8;
            int a = BitConverter.ToInt32(data, pos); pos += 4;
            if (a <= 0 || a % 4 != 0 || pos + a + 4 > data.Length - 32) return false;
            byte[] v = data.AsSpan(pos, a).ToArray(); pos += a;
            int b = BitConverter.ToInt32(data, pos); pos += 4;
            if (b <= 0 || b % 4 != 0 || pos + b != data.Length - 32) return false;
            byte[] f = data.AsSpan(pos, b).ToArray();
            if (BitConverter.ToUInt32(v, 0) != 0x07230203 || BitConverter.ToUInt32(f, 0) != 0x07230203) return false;
            vertex = v;
            fragment = f;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public static void Write(string directory, string key, int version, byte[] vertex, byte[] fragment)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(Magic);
                w.Write(version);
                w.Write(vertex.Length);
                w.Write(vertex);
                w.Write(fragment.Length);
                w.Write(fragment);
            }
            ms.Write(SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
            string path = Path.Combine(directory, key + ".spvpair");
            string temp = path + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(temp, ms.ToArray());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is only slower.
        }
    }
}
