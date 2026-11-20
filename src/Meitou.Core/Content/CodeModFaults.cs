using System.Reflection;

namespace Meitou.Content;

/// <summary>
/// How a code mod built against another version of the game fails (docs/modding.md "Versioning"): the runtime cannot bind a member or type the mod
/// was compiled against, because the game removed it or made it internal. Every place that catches a mod's fault asks <see cref="BuiltForAnother"/>
/// and then gives the player the one notice <see cref="Notice"/>.
/// </summary>
public static class CodeModFaults
{
    /// <summary>
    /// Whether <paramref name="e"/> (or the exception it wraps: a reflection call's, a static constructor's) is a binding failure: a member that is
    /// missing or no longer accessible (<see cref="MemberAccessException"/>, which <see cref="MissingMemberException"/> is), a type that cannot load
    /// (<see cref="TypeLoadException"/>, <see cref="TypeAccessException"/> among them; not a native library's), or an assembly whose types cannot
    /// load (<see cref="ReflectionTypeLoadException"/>, judged by its loader exceptions).
    /// </summary>
    public static bool BuiltForAnother(Exception e)
    {
        var inner = Unwrapped(e);
        if (inner is ReflectionTypeLoadException types) return Array.Exists(types.LoaderExceptions, l => l is not null && BuiltForAnother(l));
        if (inner is DllNotFoundException or EntryPointNotFoundException) return false;

        return inner is MemberAccessException or TypeLoadException;
    }

    /// <summary>
    /// The player's notice for a mod that failed as one built for another version of the game (<see cref="BuiltForAnother"/>): its id, and that it
    /// is stopped while the game goes on.
    /// </summary>
    public static string Notice(string mod) =>
        $"Mod {mod} was built for another version of the game and needs an update; it is stopped and the rest of the game goes on.";

    /// <summary>
    /// The exception a reflection call (<see cref="TargetInvocationException"/>) or a static constructor (<see cref="TypeInitializationException"/>)
    /// wraps, unwrapped to the first that is neither.
    /// </summary>
    static Exception Unwrapped(Exception e)
    {
        while (e is TargetInvocationException or TypeInitializationException && e.InnerException is { } inner) e = inner;

        return e;
    }
}
