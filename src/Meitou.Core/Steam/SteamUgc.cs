using System.Runtime.InteropServices;

namespace Meitou.Steam;

/// <summary>
/// The Steam client's <c>ISteamUGC</c> through the flat C API of the <c>libsteam_api.dylib</c> in the user's own Steam install (nothing of Steam is
/// shipped), initialised as Kenshi so the client lists and downloads the account's Kenshi Workshop items. Steamworks lives per process: keep
/// one open only while it is needed, since the client shows the account as playing Kenshi meanwhile.
/// </summary>
public sealed unsafe class SteamUgc : IDisposable
{
    /// <summary>
    /// The size of Steamworks' <c>SteamErrMsg</c>.
    /// </summary>
    const int ErrorSize = 1024;

    /// <summary>
    /// The loaded library, kept for the life of the process (loaded once, so a second open does not load it again).
    /// </summary>
    static nint s_library;

    /// <summary>
    /// The <c>ISteamUGC*</c>; 0 once disposed.
    /// </summary>
    nint _ugc;

    /// <summary>
    /// <c>SteamAPI_Shutdown</c>.
    /// </summary>
    delegate* unmanaged[Cdecl]<void> _shutdown;

    /// <summary>
    /// <c>SteamAPI_RunCallbacks</c>.
    /// </summary>
    delegate* unmanaged[Cdecl]<void> _runCallbacks;

    /// <summary>
    /// <c>ISteamUGC::GetNumSubscribedItems</c>.
    /// </summary>
    delegate* unmanaged[Cdecl]<nint, byte, uint> _countSubscribed;

    /// <summary>
    /// <c>ISteamUGC::GetSubscribedItems</c>.
    /// </summary>
    delegate* unmanaged[Cdecl]<nint, ulong*, uint, byte, uint> _listSubscribed;

    /// <summary>
    /// <c>ISteamUGC::GetItemState</c>.
    /// </summary>
    delegate* unmanaged[Cdecl]<nint, ulong, uint> _itemState;

    /// <summary>
    /// <c>ISteamUGC::DownloadItem</c>.
    /// </summary>
    delegate* unmanaged[Cdecl]<nint, ulong, byte, byte> _download;

    /// <summary>
    /// Only <see cref="TryOpen"/> makes one, after Steamworks is initialised.
    /// </summary>
    SteamUgc()
    {
        _shutdown = (delegate* unmanaged[Cdecl]<void>)Export("SteamAPI_Shutdown");
        _runCallbacks = (delegate* unmanaged[Cdecl]<void>)Export("SteamAPI_RunCallbacks");
        _ugc = ((delegate* unmanaged[Cdecl]<nint>)Export("SteamAPI_SteamUGC_v021"))();
        _countSubscribed = (delegate* unmanaged[Cdecl]<nint, byte, uint>)Export("SteamAPI_ISteamUGC_GetNumSubscribedItems");
        _listSubscribed = (delegate* unmanaged[Cdecl]<nint, ulong*, uint, byte, uint>)Export("SteamAPI_ISteamUGC_GetSubscribedItems");
        _itemState = (delegate* unmanaged[Cdecl]<nint, ulong, uint>)Export("SteamAPI_ISteamUGC_GetItemState");
        _download = (delegate* unmanaged[Cdecl]<nint, ulong, byte, byte>)Export("SteamAPI_ISteamUGC_DownloadItem");
    }

    #region Lifetime

    /// <summary>
    /// Loads Steam's library and starts Steamworks as Kenshi: null, with the reason (in English) in <paramref name="problem"/>, when the library
    /// cannot be loaded or Steam refuses (not running, not signed in, no Kenshi licence, an API this code does not know).
    /// </summary>
    public static SteamUgc? TryOpen(out string? problem)
    {
        problem = LoadLibrary();
        if (problem is not null) return null;

        Libc.SetEnvironment("SteamAppId", MacSteam.KenshiAppId, 1);
        Libc.SetEnvironment("SteamGameId", MacSteam.KenshiAppId, 1);
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
    static string? LoadLibrary()
    {
        if (s_library != 0) return null;

        try
        {
            s_library = NativeLibrary.Load(MacSteam.LibraryPath);
            return null;
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException)
        {
            return $"Steam's library could not be loaded ({e.Message}).";
        }
    }

    /// <summary>
    /// <c>SteamAPI_InitFlat</c>: null when Steamworks started, else Steam's reason.
    /// </summary>
    static string? Initialise()
    {
        try
        {
            byte* error = stackalloc byte[ErrorSize];
            int result = ((delegate* unmanaged[Cdecl]<byte*, int>)Export("SteamAPI_InitFlat"))(error);
            if (result == 0) return null;

            string? message = Marshal.PtrToStringUTF8((nint)error);
            return $"Steam could not be reached: {(string.IsNullOrEmpty(message) ? $"error {result}" : message)}. " +
                "Make sure Steam is signed in to an account that owns Kenshi.";
        }
        catch (EntryPointNotFoundException e)
        {
            return $"Steam's library is not the version Meitou expects ({e.Message}).";
        }
    }

    /// <summary>
    /// Resolves the <c>ISteamUGC</c> functions once Steamworks is up; on a missing one shuts Steamworks down again.
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
    /// Takes Kenshi's App ID out of the process's environment again, so a game the launcher starts does not inherit it.
    /// </summary>
    static void ClearAppId()
    {
        Libc.UnsetEnvironment("SteamAppId");
        Libc.UnsetEnvironment("SteamGameId");
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
}
