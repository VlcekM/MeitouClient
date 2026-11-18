using System.Runtime.InteropServices;

namespace Meitou.Steam;

/// <summary>
/// The Steam client's <c>ISteamUGC</c> through the flat C API of a Steam API library the user already has (<see cref="SteamLibrary"/>; nothing of
/// Steam is shipped), initialised as Kenshi so the client lists, subscribes to and downloads the account's Kenshi Workshop items. Steamworks
/// lives per process: keep one open only while it is needed, since the client shows the account as playing Kenshi meanwhile.
/// </summary>
/// <remarks>
/// Two generations of the library are spoken (docs/launcher.md "Workshop subscribe"): a current one (<c>SteamAPI_InitFlat</c>, the interfaces
/// from versioned <c>SteamAPI_SteamUGC_v0NN</c> accessors) and the one Kenshi ships (<c>SteamAPI_Init</c>, the interfaces from
/// <c>ISteamClient</c> by version string). The <c>ISteamUGC</c> and <c>ISteamUtils</c> functions used have the same flat signatures in both.
/// </remarks>
public sealed unsafe class SteamUgc : IDisposable
{
    /// <summary>
    /// What <see cref="SubscribeResult"/> answers for a call that failed without an <c>EResult</c> of Steam's.
    /// </summary>
    public const int CallFailed = -1;

    /// <summary>
    /// The size of Steamworks' <c>SteamErrMsg</c>.
    /// </summary>
    const int ErrorSize = 1024;

    /// <summary>
    /// The interface versions Kenshi's <c>steam_api64.dll</c> was built for (<b>Verified</b>: strings of <c>kenshi_x64.exe</c> 1.0.68 and of the
    /// DLL), asked of <c>ISteamClient</c> when the library has no versioned accessors.
    /// </summary>
    const string KenshiUgcVersion = "STEAMUGC_INTERFACE_VERSION008", KenshiUtilsVersion = "SteamUtils008";

    /// <summary>
    /// The accessor version numbers tried, newest first: <c>SteamAPI_SteamUGC_v0NN</c> and <c>SteamAPI_SteamUtils_v0NN</c> carry the version of
    /// the SDK the library was built from (<b>Verified</b> macOS client, 2026-10: UGC 21).
    /// </summary>
    const int NewestAccessor = 40, OldestAccessor = 9;

    /// <summary>
    /// The callback id of <c>RemoteStorageSubscribePublishedFileResult_t</c>, the result of <c>SubscribeItem</c>: an <c>EResult</c>, then the
    /// item id.
    /// </summary>
    const int SubscribeResultCallback = 1313;

    /// <summary>
    /// The environment variables that tell Steamworks which app the process is.
    /// </summary>
    static readonly string[] AppIdVariables = ["SteamAppId", "SteamGameId"];

    /// <summary>
    /// The loaded library, kept for the life of the process (loaded once, so a second open does not load it again).
    /// </summary>
    static nint s_library;

    /// <summary>
    /// The <c>ISteamUGC*</c>; 0 once disposed.
    /// </summary>
    nint _ugc;

    /// <summary>
    /// The <c>ISteamUtils*</c>, which answers for the calls Steam completes later.
    /// </summary>
    readonly nint _utils;

    /// <summary>
    /// <c>SteamAPI_Shutdown</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<void> _shutdown;

    /// <summary>
    /// <c>SteamAPI_RunCallbacks</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<void> _runCallbacks;

    /// <summary>
    /// <c>ISteamUGC::GetNumSubscribedItems</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, byte, uint> _countSubscribed;

    /// <summary>
    /// <c>ISteamUGC::GetSubscribedItems</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, ulong*, uint, byte, uint> _listSubscribed;

    /// <summary>
    /// <c>ISteamUGC::GetItemState</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, ulong, uint> _itemState;

    /// <summary>
    /// <c>ISteamUGC::DownloadItem</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, ulong, byte, byte> _download;

    /// <summary>
    /// <c>ISteamUGC::SubscribeItem</c>: the handle of the call Steam completes later.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, ulong, ulong> _subscribe;

    /// <summary>
    /// <c>ISteamUtils::IsAPICallCompleted</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, ulong, byte*, byte> _callCompleted;

    /// <summary>
    /// <c>ISteamUtils::GetAPICallResult</c>.
    /// </summary>
    readonly delegate* unmanaged[Cdecl]<nint, ulong, void*, int, int, byte*, byte> _callResult;

    /// <summary>
    /// Only <see cref="TryOpen"/> makes one, after Steamworks is initialised.
    /// </summary>
    SteamUgc()
    {
        _shutdown = (delegate* unmanaged[Cdecl]<void>)Export("SteamAPI_Shutdown");
        _runCallbacks = (delegate* unmanaged[Cdecl]<void>)Export("SteamAPI_RunCallbacks");
        (_ugc, _utils) = Interfaces();
        _countSubscribed = (delegate* unmanaged[Cdecl]<nint, byte, uint>)Export("SteamAPI_ISteamUGC_GetNumSubscribedItems");
        _listSubscribed = (delegate* unmanaged[Cdecl]<nint, ulong*, uint, byte, uint>)Export("SteamAPI_ISteamUGC_GetSubscribedItems");
        _itemState = (delegate* unmanaged[Cdecl]<nint, ulong, uint>)Export("SteamAPI_ISteamUGC_GetItemState");
        _download = (delegate* unmanaged[Cdecl]<nint, ulong, byte, byte>)Export("SteamAPI_ISteamUGC_DownloadItem");
        _subscribe = (delegate* unmanaged[Cdecl]<nint, ulong, ulong>)Export("SteamAPI_ISteamUGC_SubscribeItem");
        _callCompleted = (delegate* unmanaged[Cdecl]<nint, ulong, byte*, byte>)Export("SteamAPI_ISteamUtils_IsAPICallCompleted");
        _callResult = (delegate* unmanaged[Cdecl]<nint, ulong, void*, int, int, byte*, byte>)Export("SteamAPI_ISteamUtils_GetAPICallResult");
    }

    /// <summary>
    /// The size of the subscribe result: its 64-bit id is aligned to 8 on Windows and to 4 elsewhere (Steamworks' struct packing).
    /// </summary>
    static int SubscribeResultSize => OperatingSystem.IsWindows() ? 16 : 12;

    #region Lifetime

    /// <summary>
    /// Loads the Steam API library at <paramref name="libraryPath"/> and starts Steamworks as Kenshi: null, with the reason (in English) in
    /// <paramref name="problem"/>, when the library cannot be loaded or Steam refuses (not running, not signed in, no Kenshi licence, an API
    /// this code does not know).
    /// </summary>
    public static SteamUgc? TryOpen(string libraryPath, out string? problem)
    {
        problem = LoadLibrary(libraryPath);
        if (problem is not null) return null;

        SetAppId();
        problem = Initialise();
        var ugc = problem is null ? Bind(out problem) : null;
        if (ugc is null) ClearAppId();
        return ugc;
    }

    /// <summary>
    /// Shuts Steamworks down, so the client no longer shows Kenshi as running; once only.
    /// </summary>
    public void Dispose()
    {
        if (_ugc == 0) return;

        _ugc = 0;
        _shutdown();
        ClearAppId();
    }

    /// <summary>
    /// Loads the library once; the reason it cannot be loaded, else null.
    /// </summary>
    static string? LoadLibrary(string libraryPath)
    {
        if (s_library != 0) return null;

        try
        {
            s_library = NativeLibrary.Load(libraryPath);
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            return $"Steam's library could not be loaded ({e.Message}).";
        }
    }

    /// <summary>
    /// <c>SteamAPI_InitFlat</c>, or <c>SteamAPI_Init</c> in a library without it (Kenshi's, which gives no reason): null when Steamworks started,
    /// else why not.
    /// </summary>
    static string? Initialise()
    {
        const string advice = "Make sure Steam is running and signed in to an account that owns Kenshi.";
        try
        {
            if (!NativeLibrary.TryGetExport(s_library, "SteamAPI_InitFlat", out nint initFlat))
                return ((delegate* unmanaged[Cdecl]<byte>)Export("SteamAPI_Init"))() != 0 ? null : $"Steam could not be reached. {advice}";

            byte* error = stackalloc byte[ErrorSize];
            int result = ((delegate* unmanaged[Cdecl]<byte*, int>)initFlat)(error);
            if (result == 0) return null;

            string? message = Marshal.PtrToStringUTF8((nint)error);
            return $"Steam could not be reached: {(string.IsNullOrEmpty(message) ? $"error {result}" : message)}. {advice}";
        }
        catch (EntryPointNotFoundException e)
        {
            return $"Steam's library is not the version Meitou expects ({e.Message}).";
        }
    }

    /// <summary>
    /// Resolves the interfaces and their functions once Steamworks is up; on a missing one shuts Steamworks down again.
    /// </summary>
    static SteamUgc? Bind(out string? problem)
    {
        problem = null;
        try
        {
            return new SteamUgc();
        }
        catch (EntryPointNotFoundException e)
        {
            ((delegate* unmanaged[Cdecl]<void>)Export("SteamAPI_Shutdown"))();
            problem = $"Steam's library is not the version Meitou expects ({e.Message}).";
            return null;
        }
    }

    /// <summary>
    /// Puts Kenshi's App ID in the process's real environment, where Steamworks reads it: on Unix .NET's own call only changes its copy.
    /// </summary>
    static void SetAppId()
    {
        foreach (string name in AppIdVariables)
        {
            if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable(name, MacSteam.KenshiAppId);
            else Libc.SetEnvironment(name, MacSteam.KenshiAppId, 1);
        }
    }

    /// <summary>
    /// Takes Kenshi's App ID out of the process's environment again, so a game the launcher starts does not inherit it.
    /// </summary>
    static void ClearAppId()
    {
        foreach (string name in AppIdVariables)
        {
            if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable(name, null);
            else Libc.UnsetEnvironment(name);
        }
    }

    /// <summary>
    /// The <c>ISteamUGC*</c> and <c>ISteamUtils*</c>: from the library's versioned accessors, else (Kenshi's library) from <c>ISteamClient</c>.
    /// <see cref="EntryPointNotFoundException"/> when the library offers neither way or Steam has no such interface.
    /// </summary>
    static (nint Ugc, nint Utils) Interfaces()
    {
        var (ugc, utils) = Accessor("SteamAPI_SteamUGC_v0") is { } ugcAccessor && Accessor("SteamAPI_SteamUtils_v0") is { } utilsAccessor
            ? (((delegate* unmanaged[Cdecl]<nint>)ugcAccessor)(), ((delegate* unmanaged[Cdecl]<nint>)utilsAccessor)())
            : ClientInterfaces();
        if (ugc == 0 || utils == 0) throw new EntryPointNotFoundException("Steam has no Workshop interface of the library's version");

        return (ugc, utils);
    }

    /// <summary>
    /// The library's export named <paramref name="prefix"/> and a two-digit version, the newest it has; null when it has none.
    /// </summary>
    static nint? Accessor(string prefix)
    {
        for (int version = NewestAccessor; version >= OldestAccessor; version--)
            if (NativeLibrary.TryGetExport(s_library, $"{prefix}{version:00}", out nint accessor)) return accessor;

        return null;
    }

    /// <summary>
    /// The two interfaces by Kenshi's version strings from the library's <c>ISteamClient</c>, for the user and pipe Steamworks opened.
    /// </summary>
    static (nint Ugc, nint Utils) ClientInterfaces()
    {
        var ugcOf = (delegate* unmanaged[Cdecl]<nint, int, int, nint, nint>)Export("SteamAPI_ISteamClient_GetISteamUGC");
        var utilsOf = (delegate* unmanaged[Cdecl]<nint, int, nint, nint>)Export("SteamAPI_ISteamClient_GetISteamUtils");
        nint client = ((delegate* unmanaged[Cdecl]<nint>)Export("SteamClient"))();
        int user = ((delegate* unmanaged[Cdecl]<int>)Export("SteamAPI_GetHSteamUser"))();
        int pipe = ((delegate* unmanaged[Cdecl]<int>)Export("SteamAPI_GetHSteamPipe"))();
        if (client == 0) return (0, 0);

        nint ugcVersion = Marshal.StringToCoTaskMemUTF8(KenshiUgcVersion);
        nint utilsVersion = Marshal.StringToCoTaskMemUTF8(KenshiUtilsVersion);
        try
        {
            return (ugcOf(client, user, pipe, ugcVersion), utilsOf(client, pipe, utilsVersion));
        }
        finally
        {
            Marshal.FreeCoTaskMem(ugcVersion);
            Marshal.FreeCoTaskMem(utilsVersion);
        }
    }

    /// <summary>
    /// An export of the loaded library; <see cref="EntryPointNotFoundException"/> when it has none.
    /// </summary>
    static nint Export(string name) => NativeLibrary.GetExport(s_library, name);

    #endregion

    #region Items

    /// <summary>
    /// The ids of the account's subscribed Kenshi items.
    /// </summary>
    public ulong[] SubscribedItems()
    {
        uint count = _countSubscribed(_ugc, 0);
        var ids = new ulong[count];
        if (count == 0) return ids;

        fixed (ulong* buffer = ids)
        {
            uint listed = _listSubscribed(_ugc, buffer, count, 0);
            return ids[..(int)listed];
        }
    }

    /// <summary>
    /// What the client holds of an item.
    /// </summary>
    public WorkshopItemState State(ulong id) => (WorkshopItemState)_itemState(_ugc, id);

    /// <summary>
    /// Asks the client to download or update an item; false when it will not.
    /// </summary>
    public bool Download(ulong id) => _download(_ugc, id, 0) != 0;

    /// <summary>
    /// Lets Steamworks handle what the client sent since the last call.
    /// </summary>
    public void RunCallbacks() => _runCallbacks();

    #endregion

    #region Subscribing

    /// <summary>
    /// Asks Steam to subscribe the account to an item: the handle of the call for <see cref="SubscribeResult"/>, 0 when Steam takes no call.
    /// </summary>
    public ulong Subscribe(ulong id) => _subscribe(_ugc, id);

    /// <summary>
    /// How the call <see cref="Subscribe"/> returned ended: null while Steam has not answered, else its <c>EResult</c> (1 is OK), or
    /// <see cref="CallFailed"/> when the call itself failed or its result cannot be read.
    /// </summary>
    public int? SubscribeResult(ulong call)
    {
        byte failed = 0;
        if (_callCompleted(_utils, call, &failed) == 0) return null;
        if (failed != 0) return CallFailed;

        // Room for either packing; the EResult is first in both.
        long* result = stackalloc long[2];
        bool read = _callResult(_utils, call, result, SubscribeResultSize, SubscribeResultCallback, &failed) != 0 && failed == 0;
        return read ? *(int*)result : CallFailed;
    }

    #endregion
}
