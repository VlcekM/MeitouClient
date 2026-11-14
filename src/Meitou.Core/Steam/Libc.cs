using System.Runtime.InteropServices;

namespace Meitou.Steam;

/// <summary>
/// The C library's environment functions: <see cref="Environment.SetEnvironmentVariable(string, string)"/> only changes .NET's own copy on Unix,
/// which native code (Steamworks reading <c>SteamAppId</c>) never sees.
/// </summary>
static partial class Libc
{
    /// <summary>
    /// <c>setenv</c>: sets a variable of the process's real environment.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SetEnvironment(string name, string value, int overwrite);

    /// <summary>
    /// <c>unsetenv</c>: removes a variable from the process's real environment.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "unsetenv", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int UnsetEnvironment(string name);
}
