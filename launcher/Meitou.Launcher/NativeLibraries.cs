using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Meitou.Launcher;

/// <summary>
/// The published launcher is one Native AOT executable, but Avalonia's native libraries (Skia, HarfBuzz, ANGLE on Windows,
/// AvaloniaNative on macOS) cannot be
/// linked in statically. The csproj embeds them as <c>native/&lt;file&gt;</c> resources; this writes them once to
/// <c>native/&lt;content hash&gt;/</c> in <see cref="LauncherConfig.DataDirectory"/> and loads them from there (docs/launcher.md).
/// Debug builds embed nothing and load them from next to the executable as usual.
/// </summary>
static class NativeLibraries
{
    /// <summary>
    /// The resource name prefix of the embedded native libraries.
    /// </summary>
    const string Prefix = "native/";

    /// <summary>
    /// Writes the embedded native libraries to their content-hash folder (once) and loads them from there; nothing in a Debug build.
    /// </summary>
    public static void Load()
    {
        var assembly = typeof(NativeLibraries).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var handles = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        RegisterImportResolver(assembly, handles);
        if (names.Length == 0) return;

        string directory = WriteLibraries(assembly, names, handles);
        RemoveOldDirectories(directory);
        RegisterSharedResolvers(handles);
    }

    /// <summary>
    /// Resolves the launcher's own imports (news videos) to the embedded libraries: OpenAL Soft by its file names, libvpx (linked in when published)
    /// by the system's.
    /// </summary>
    static void RegisterImportResolver(Assembly assembly, Dictionary<string, IntPtr> handles)
    {
        // The launcher's own imports (news videos): OpenAL Soft by its file names, libvpx (linked in when published) by the system's.
        NativeLibrary.SetDllImportResolver(assembly, (library, _, search) =>
        {
            string[] candidates = library switch
            {
                "openal" => ["soft_oal", "libopenal", "libopenal.so.1", "/System/Library/Frameworks/OpenAL.framework/OpenAL"],
                "vpx" =>
                    ["vpx", "libvpx.so.9", "libvpx.so.8", "libvpx.so.7", "libvpx", "/opt/homebrew/lib/libvpx.dylib", "/usr/local/lib/libvpx.dylib"],
                _ => [],
            };
            foreach (var candidate in candidates)
            {
                if (handles.GetValueOrDefault(candidate) is not 0 and var embedded) return embedded;
                else if (NativeLibrary.TryLoad(candidate, assembly, search, out var loaded)) return loaded;
            }
            return 0;
        });
    }

    /// <summary>
    /// Writes the embedded libraries to the content-named folder (a changed one is written again) and loads each by its full path. Returns the
    /// folder.
    /// </summary>
    static string WriteLibraries(Assembly assembly, string[] names, Dictionary<string, IntPtr> handles)
    {
        string directory = ContentDirectory(assembly, names);
        Directory.CreateDirectory(directory);
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name[Prefix.Length..]);
            WriteLibrary(assembly, name, path);

            // Loaded by full path now: ANGLE is then found by name (Windows reuses a loaded module), Skia and HarfBuzz through the resolver.
            handles[Path.GetFileNameWithoutExtension(path)] = NativeLibrary.Load(path);
        }

        return directory;
    }

    /// <summary>
    /// The folder the libraries go to: <c>native/&lt;content hash&gt;</c> under the data directory.
    /// </summary>
    static string ContentDirectory(Assembly assembly, string[] names)
    {
        // Named by content, so a launcher update with other libraries never overwrites ones a running launcher has loaded. The build
        // records each library's hash (the csproj's NativeLibraries metadata); without it, the names and sizes stand in.
        var contents = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NativeLibraries")?.Value
            ?? string.Join(";", names.Select(n => NameAndSize(assembly, n)));
        string hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents)))[..16];
        return Path.Combine(LauncherConfig.DataDirectory, "native", hash);
    }

    /// <summary>
    /// <c>name=size</c> of an embedded library, for <see cref="ContentDirectory"/> when the build recorded no hashes.
    /// </summary>
    static string NameAndSize(Assembly assembly, string name)
    {
        using var s = assembly.GetManifestResourceStream(name)!;
        return $"{name}={s.Length}";
    }

    /// <summary>
    /// Writes one embedded library to <paramref name="path"/> unless an equal-sized file is there already.
    /// </summary>
    static void WriteLibrary(Assembly assembly, string name, string path)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        if (File.Exists(path) && new FileInfo(path).Length == stream.Length) return;

        // Via a temporary file, so a crash or a second launcher starting at once never leaves a partial library.
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        using (var file = File.Create(temp)) stream.CopyTo(file);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Deletes the earlier launchers' folders; one still in use by a running launcher is left for next time.
    /// </summary>
    static void RemoveOldDirectories(string directory)
    {
        foreach (var old in Directory.EnumerateDirectories(Path.GetDirectoryName(directory)!).Where(d => d != directory))
        {
            try
            {
                Directory.Delete(old, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Points the Skia, HarfBuzz and Avalonia imports at the libraries written by <see cref="WriteLibraries"/>.
    /// </summary>
    static void RegisterSharedResolvers(Dictionary<string, IntPtr> handles)
    {
        DllImportResolver resolver = (library, _, _) => handles.GetValueOrDefault(Path.GetFileNameWithoutExtension(library));
        NativeLibrary.SetDllImportResolver(typeof(SkiaSharp.SKObject).Assembly, resolver);
        NativeLibrary.SetDllImportResolver(typeof(HarfBuzzSharp.Blob).Assembly, resolver);

        // macOS's libAvaloniaNative: dyld never matches a bare name to an already loaded library, so it needs the resolver too.
        NativeLibrary.SetDllImportResolver(typeof(Avalonia.AvaloniaNativePlatformOptions).Assembly, resolver);
    }
}
