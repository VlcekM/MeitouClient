using System.Runtime.InteropServices;
using Meitou.Content;
using Meitou.Localisation;

namespace Meitou.Game.Host;

/// <summary>
/// Optional Steamworks lifetime for the Steam copy of Kenshi. No Steam binaries are shipped with Meitou.
/// </summary>
sealed unsafe class SteamSession : IDisposable
{
    /// <summary>
    /// Kenshi's Steam App ID.
    /// </summary>
    const uint KenshiAppId = 233860;
    /// <summary>
    /// The install's <c>steam_api64.dll</c>, loaded; 0 once shut down.
    /// </summary>
    nint _library;
    /// <summary>
    /// The library's <c>SteamAPI_Shutdown</c>.
    /// </summary>
    delegate* unmanaged[Cdecl]<void> _shutdown;

    /// <summary>
    /// Only <see cref="TryStart"/> makes one.
    /// </summary>
    SteamSession() { }

    /// <summary>
    /// Starts Steamworks from the install's <c>steam_api64.dll</c> (Windows only): null, with the reason logged, when the library is missing, another
    /// App ID is configured or its init fails.
    /// </summary>
    public static SteamSession? TryStart(GameInstall install)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("Steamworks: skipped (currently supported on Windows only).");
            return null;
        }
        string path = Path.Combine(install.Root, "steam_api64.dll");
        if (!File.Exists(path))
        {
            Console.WriteLine("Steamworks: skipped (steam_api64.dll is missing from the Kenshi install).");
            return null;
        }

        try
        {
            string? fileAppId = File.Exists("steam_appid.txt") ? File.ReadAllText("steam_appid.txt").Trim() : null;
            string? processAppId = Environment.GetEnvironmentVariable("SteamAppId");
            if (fileAppId is not null && fileAppId != KenshiAppId.ToString() ||
                processAppId is not null && processAppId != KenshiAppId.ToString())
            {
                Console.WriteLine("Steamworks: skipped (a different Steam App ID is configured).");
                return null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Steamworks: could not read steam_appid.txt ({e.Message}).");
            return null;
        }

        nint library = 0;
        try
        {
            library = NativeLibrary.Load(path);
            var stop = (delegate* unmanaged[Cdecl]<void>)NativeLibrary.GetExport(library, "SteamAPI_Shutdown");
            if (NativeLibrary.TryGetExport(library, "SteamAPI_InitFlat", out nint initFlatAddress))
            {
                byte* error = stackalloc byte[1024];
                int result = ((delegate* unmanaged[Cdecl]<byte*, int>)initFlatAddress)(error);
                if (result != 0)
                {
                    string? message = Marshal.PtrToStringUTF8((nint)error);
                    string reason = string.IsNullOrEmpty(message) ? $"SteamAPI_InitFlat returned {result}" : message;
                    NativeLibrary.Free(library);
                    Console.WriteLine($"Steamworks: {reason}");
                    return null;
                }
            }
            else
            {
                // A C++ bool: one byte in AL, so not marshalled as a 4-byte BOOL.
                var init = (delegate* unmanaged[Cdecl]<byte>)NativeLibrary.GetExport(library, "SteamAPI_Init");
                if (init() == 0)
                {
                    NativeLibrary.Free(library);
                    Console.WriteLine("Steamworks: SteamAPI_Init failed (this Kenshi Steam API has no detailed init error).");
                    return null;
                }
            }
            var session = new SteamSession { _library = library, _shutdown = stop };
            GameLanguage.SteamClientLanguage = ReadGameLanguage(library);
            Console.WriteLine($"Steamworks: initialized for Kenshi (233860), language {GameLanguage.SteamClientLanguage ?? "unknown"}.");
            return session;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or FileLoadException)
        {
            if (library != 0) NativeLibrary.Free(library);
            Console.WriteLine($"Steamworks: could not load Kenshi's Steam API ({e.Message}).");
            return null;
        }
    }

    /// <summary>
    /// The language the Steam client runs Kenshi in (<c>ISteamApps::GetCurrentGameLanguage</c>, which the original asks at start to override its
    /// setting, docs/formats/localisation.md); null when the library lacks the exports.
    /// </summary>
    static string? ReadGameLanguage(nint library)
    {
        if (!NativeLibrary.TryGetExport(library, "SteamAPI_ISteamApps_GetCurrentGameLanguage", out nint current)) return null;
        nint apps = SteamApps(library);
        if (apps == 0) return null;

        byte* language = ((delegate* unmanaged[Cdecl]<nint, byte*>)current)(apps);
        return language is null ? null : Marshal.PtrToStringUTF8((nint)language);
    }

    /// <summary>
    /// The <c>ISteamApps</c> interface of version 008 (the original's): the newer libraries' accessor, else through <c>SteamClient</c> as Kenshi's own
    /// library offers it; 0 when neither is there.
    /// </summary>
    static nint SteamApps(nint library)
    {
        if (NativeLibrary.TryGetExport(library, "SteamAPI_SteamApps_v008", out nint accessor)) return ((delegate* unmanaged[Cdecl]<nint>)accessor)();
        if (!(NativeLibrary.TryGetExport(library, "SteamClient", out nint client)
            && NativeLibrary.TryGetExport(library, "SteamAPI_GetHSteamUser", out nint user)
            && NativeLibrary.TryGetExport(library, "SteamAPI_GetHSteamPipe", out nint pipe)
            && NativeLibrary.TryGetExport(library, "SteamAPI_ISteamClient_GetISteamApps", out nint getApps)))
        {
            return 0;
        }

        nint steamClient = ((delegate* unmanaged[Cdecl]<nint>)client)();
        int steamUser = ((delegate* unmanaged[Cdecl]<int>)user)(), steamPipe = ((delegate* unmanaged[Cdecl]<int>)pipe)();
        if (steamClient == 0 || steamPipe == 0) return 0;

        // A UTF-8 literal is stored with a terminating zero after its bytes.
        fixed (byte* version = "STEAMAPPS_INTERFACE_VERSION008"u8)
            return ((delegate* unmanaged[Cdecl]<nint, int, int, byte*, nint>)getApps)(steamClient, steamUser, steamPipe, version);
    }

    /// <summary>
    /// Shuts Steamworks down and frees the library; once only.
    /// </summary>
    public void Dispose()
    {
        if (_library == 0) return;
        _shutdown();
        NativeLibrary.Free(_library);
        _library = 0;
    }
}