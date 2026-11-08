using System.Runtime.InteropServices;
using Meitou.Content;

namespace Meitou.Game;

/// <summary>Optional Steamworks lifetime for the Steam copy of Kenshi. No Steam binaries are shipped with Meitou.</summary>
sealed unsafe class SteamSession : IDisposable
{
    const uint KenshiAppId = 233860;
    nint library;
    delegate* unmanaged[Cdecl]<void> shutdown;

    SteamSession() { }

    public static SteamSession? TryStart(GameInstall install)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Steamworks: skipped (currently supported on Windows only)."); return null; }
        string path = Path.Combine(install.Root, "steam_api64.dll");
        if (!File.Exists(path)) { Console.WriteLine("Steamworks: skipped (steam_api64.dll is missing from the Kenshi install)."); return null; }

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
            var session = new SteamSession { library = library, shutdown = stop };
            Console.WriteLine("Steamworks: initialized for Kenshi (233860).");
            return session;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or FileLoadException)
        {
            if (library != 0) NativeLibrary.Free(library);
            Console.WriteLine($"Steamworks: could not load Kenshi's Steam API ({e.Message}).");
            return null;
        }
    }

    public void Dispose()
    {
        if (library == 0) return;
        shutdown();
        NativeLibrary.Free(library);
        library = 0;
    }
}
