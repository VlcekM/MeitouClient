using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace Meitou.Game.Host;

/// <summary>
/// Lets Silk.NET find the native libraries its packages put under <c>runtimes/&lt;rid&gt;/native</c> (GLFW, OpenAL Soft, shaderc) in a build not
/// published for one platform (<c>dotnet build</c>, <c>dotnet run</c>, the tests). Silk names the platform from <c>/etc/os-release</c> and falls back
/// to <c>linux-…</c> by replacing the part before the first '-': an ID with a hyphen of its own, such as openSUSE Tumbleweed's
/// <c>opensuse-tumbleweed.20261016-x64</c>, falls back to <c>linux-tumbleweed.20261016-x64</c>, the <c>linux-x64</c> folder is never searched and no
/// window opens without a system GLFW. .NET's own portable identifier names the folder the build copied them to.
/// </summary>
static class NativeLibraryPaths
{
    /// <summary>
    /// Adds the runtimes folder of .NET's identifier as Silk's last resort when it exists (a published build has the libraries beside the
    /// executable instead); runs when the assembly loads, before any Silk API is made.
    /// </summary>
    [ModuleInitializer]
    internal static void AddRuntimesFolder()
    {
        if (PathResolver.Default is not DefaultPathResolver resolver) return;

        string folder = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        if (!Directory.Exists(folder)) return;

        resolver.Resolvers.Add(name => FindIn(folder, name));
    }

    /// <summary>
    /// The library <paramref name="name"/> in <paramref name="folder"/>, or nothing when it is not there.
    /// </summary>
    static IEnumerable<string> FindIn(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        return File.Exists(path) ? [path] : [];
    }
}
