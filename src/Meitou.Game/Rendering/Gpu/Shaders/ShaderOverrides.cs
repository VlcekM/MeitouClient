using System.Security.Cryptography;
using System.Text;

namespace Meitou.Game.Rendering.Gpu.Shaders;

/// <summary>
/// Shader sources from files, for iterating on a shader without rebuilding (a debug aid; docs/renderer-native.md "Shader overrides"). With the
/// environment variable <c>MEITOU_SHADER_DIR</c> set, every source the compiler is given is written once to <c>&lt;dir&gt;/&lt;hash&gt;.&lt;stage&gt;.glsl</c>
/// (the hash: the first 16 hex digits of the source's SHA-256), and a file there whose text differs from the source is compiled in its place
/// (before the SPIR-V caches are keyed, so they keep both). Find a shader by grepping the folder; edit its file; start the game again.
/// Without the variable nothing changes.
/// </summary>
public static class ShaderOverrides
{
    static readonly string? Dir = Environment.GetEnvironmentVariable("MEITOU_SHADER_DIR") is { Length: > 0 } d ? d : null;
    static readonly object Gate = new();

    /// <summary>The source to compile for <paramref name="source"/>: the edited file when there is one, else <paramref name="source"/> (written out).</summary>
    public static string Source(string source, string stage)
    {
        if (Dir is null) return source;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16].ToLowerInvariant();
        string path = Path.Combine(Dir, $"{hash}.{stage}.glsl");
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (!File.Exists(path)) { File.WriteAllText(path, source); return source; }
                string edited = File.ReadAllText(path);
                if (edited == source) return source;
                Console.WriteLine($"shader    override {path}");
                return edited;
            }
            catch (IOException e) { Console.WriteLine($"warning   shader override {path}: {e.Message}"); return source; }
            catch (UnauthorizedAccessException e) { Console.WriteLine($"warning   shader override {path}: {e.Message}"); return source; }
        }
    }
}
