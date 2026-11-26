using System.Diagnostics;
using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>
/// The host callbacks (<c>__kl_*</c> globals, <c>KenshiLua.log</c>, <c>json</c>) one mod's sandbox gets, backed by <see cref="ILuaWorldBridge"/>;
/// the prelude in <see cref="KenshiLuaApi.PreludeLua"/> builds the KenshiLua object model on top of them.
/// </summary>
internal sealed class KenshiLuaBindings
{
    #region Setup

    /// <summary>
    /// The sandbox the callbacks are installed into.
    /// </summary>
    private readonly LuaSandbox _sandbox;

    /// <summary>
    /// The mod's id, as named in its log lines and as the one active mod whose path is its real folder.
    /// </summary>
    private readonly string _modId;

    /// <summary>
    /// The mod's folder; null when it has none, which turns off the mod file reader.
    /// </summary>
    private readonly string? _modFolder;

    /// <summary>
    /// The engine the callbacks read the world from and queue their actions on.
    /// </summary>
    private readonly ILuaWorldBridge _bridge;

    /// <summary>
    /// The names of the mods that are active now.
    /// </summary>
    private readonly Func<IReadOnlyList<string>> _activeMods;

    /// <summary>
    /// Takes one unit of this tick's action budget; false when it is used up.
    /// </summary>
    private readonly Func<bool> _trySpendAction;

    /// <summary>
    /// The host's log sink.
    /// </summary>
    private readonly Action<string> _log;

    /// <summary>
    /// Binds one mod's sandbox to the world bridge and the host's budgets.
    /// </summary>
    internal KenshiLuaBindings(
        LuaSandbox sandbox,
        string modId,
        string? modFolder,
        ILuaWorldBridge bridge,
        Func<IReadOnlyList<string>> activeMods,
        Func<bool> trySpendAction,
        Action<string> log)
    {
        _sandbox = sandbox;
        _modId = modId;
        _modFolder = modFolder;
        _bridge = bridge;
        _activeMods = activeMods;
        _trySpendAction = trySpendAction;
        _log = log;
    }

    /// <summary>
    /// Installs every callback into the sandbox.
    /// </summary>
    internal void Register()
    {
        RegisterEnvironment();
        RegisterWorldQueries();
        RegisterCamera();
        RegisterActions();
    }

    #endregion

    #region Environment

    /// <summary>
    /// The wall clock the sandboxed <c>os.clock</c> reads, started when the host loads.
    /// </summary>
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>
    /// Installs logging, the clock and date, the read-only mod file reader and <c>json</c>.
    /// </summary>
    private void RegisterEnvironment()
    {
        _sandbox.Expose("KenshiLua", "log", Log);
        Table klTbl = _sandbox.Script.Globals.Get("KenshiLua").Table;
        klTbl["version"] = "1.0.0-meitou";
        klTbl["isMeitou"] = true;

        _sandbox.Expose("", "__kl_clock", ClockSeconds);
        _sandbox.Expose("", "__kl_unix_time", UnixTime);
        _sandbox.Expose("", "__kl_date", FormatNow);
        _sandbox.Expose("", "__kl_read_mod_file", ReadModFile);

        _sandbox.Expose("json", "encode", JsonEncode);
        _sandbox.Expose("json", "decode", JsonDecode);
    }

    /// <summary>
    /// <c>KenshiLua.log(msg)</c>: writes the message to the host log, tagged with the mod.
    /// </summary>
    private DynValue Log(CallbackArguments a)
    {
        string msg = a.Count > 0 ? a[0].CastToString() ?? "" : "";
        _log($"[Meitou.Lua:{_modId}] {msg}");

        return DynValue.Nil;
    }

    /// <summary>
    /// Seconds since the host started.
    /// </summary>
    private static DynValue ClockSeconds(CallbackArguments args)
    {
        return DynValue.NewNumber(Clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// The current UTC time in Unix seconds.
    /// </summary>
    private static DynValue UnixTime(CallbackArguments args)
    {
        return DynValue.NewNumber(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// <c>os.date(fmt)</c>: the local time with the <c>%Y %m %d %H %M %S</c> fields filled in.
    /// </summary>
    private static DynValue FormatNow(CallbackArguments a)
    {
        string fmt = a.Count > 0 && a[0].Type == DataType.String ? a[0].String : "%Y-%m-%d %H:%M:%S";
        DateTime now = DateTime.Now;
        string formatted = fmt
            .Replace("%Y", now.ToString("yyyy"))
            .Replace("%m", now.ToString("MM"))
            .Replace("%d", now.ToString("dd"))
            .Replace("%H", now.ToString("HH"))
            .Replace("%M", now.ToString("mm"))
            .Replace("%S", now.ToString("ss"));

        return DynValue.NewString(formatted);
    }

    /// <summary>
    /// Reads a text file of the mod's own folder (at most 256 KiB); nil when it is missing, too big or outside the folder.
    /// </summary>
    private DynValue ReadModFile(CallbackArguments a)
    {
        if (string.IsNullOrEmpty(_modFolder) || a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;

        string? full = ResolveModFile(_modFolder, a[0].String);
        if (full is null || !File.Exists(full)) return DynValue.Nil;

        return ReadSmallFile(full);
    }

    /// <summary>
    /// The full path of a requested file inside <paramref name="modFolder"/>: the request with the folder prefix stripped,
    /// or failing that its leaf name.
    /// </summary>
    private static string? ResolveModFile(string modFolder, string request)
    {
        string req = request.Trim().Replace('\\', '/');
        string normRoot = modFolder.TrimEnd('\\', '/').Replace('\\', '/') + "/";
        if (req.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase)) req = req[normRoot.Length..];

        string leaf = Path.GetFileName(req);
        return LuaSandbox.SafePath(modFolder, req) ?? (!string.IsNullOrEmpty(leaf) ? LuaSandbox.SafePath(modFolder, leaf) : null);
    }

    /// <summary>
    /// A file's text, or nil when it is over 256 KiB or cannot be read.
    /// </summary>
    private static DynValue ReadSmallFile(string full)
    {
        try
        {
            FileInfo fi = new(full);
            if (fi.Length > 256 * 1024) return DynValue.Nil;

            return DynValue.NewString(File.ReadAllText(full));
        }
        catch (Exception)
        {
            // An unreadable file reads as missing.
            return DynValue.Nil;
        }
    }

    /// <summary>
    /// <c>json.encode(value)</c>.
    /// </summary>
    private static DynValue JsonEncode(CallbackArguments a)
    {
        return DynValue.NewString(a.Count > 0 ? LuaJson.Encode(a[0], 0) : "null");
    }

    /// <summary>
    /// <c>json.decode(text)</c>: nil for anything but a string holding valid JSON.
    /// </summary>
    private DynValue JsonDecode(CallbackArguments a)
    {
        return a.Count > 0 && a[0].Type == DataType.String ? LuaJson.Decode(_sandbox.Script, a[0].String) : DynValue.Nil;
    }

    #endregion

    #region World queries

    /// <summary>
    /// Installs the read-only world queries.
    /// </summary>
    private void RegisterWorldQueries()
    {
        _sandbox.Expose("", "__kl_game_hours", GameHours);
        _sandbox.Expose("", "__kl_player_cats", PlayerCats);
        _sandbox.Expose("", "__kl_selected_name", SelectedName);
        _sandbox.Expose("", "__kl_active_mods", ActiveMods);
        _sandbox.Expose("", "__kl_gamedata_by_name", GameDataByName);

        _sandbox.Expose("", "__kl_chars", Characters);
        _sandbox.Expose("", "__kl_char_by_id", CharacterById);
        _sandbox.Expose("", "__kl_faction_is_ally", FactionIsAlly);
        _sandbox.Expose("", "__kl_faction_relation", FactionRelation);

        _sandbox.Expose("", "__kl_building_by_id", BuildingById);
        _sandbox.Expose("", "__kl_doors", Doors);
        _sandbox.Expose("", "__kl_char_items", CharacterItems);
    }

    /// <summary>
    /// The in-game time in hours; nil before a world is loaded.
    /// </summary>
    private DynValue GameHours(CallbackArguments args)
    {
        return _bridge.GameHours is double h ? DynValue.NewNumber(h) : DynValue.Nil;
    }

    /// <summary>
    /// The player's money; nil before a world is loaded.
    /// </summary>
    private DynValue PlayerCats(CallbackArguments args)
    {
        return _bridge.PlayerCats is long c ? DynValue.NewNumber(c) : DynValue.Nil;
    }

    /// <summary>
    /// The selected character's name; nil when nothing is selected.
    /// </summary>
    private DynValue SelectedName(CallbackArguments args)
    {
        return _bridge.SelectedName is { Length: > 0 } n ? DynValue.NewString(n) : DynValue.Nil;
    }

    /// <summary>
    /// The active mods as a list of <c>{ name, path }</c> tables.
    /// </summary>
    private DynValue ActiveMods(CallbackArguments args)
    {
        Table t = new(_sandbox.Script);
        IReadOnlyList<string> mods = _activeMods();

        for (int i = 0; i < mods.Count; i++)
        {
            t[i + 1] = ActiveModEntry(mods[i]);
        }

        return DynValue.NewTable(t);
    }

    /// <summary>
    /// One active mod's table; this mod's path is its real folder, the others' a conventional <c>mods/name/</c>.
    /// </summary>
    private Table ActiveModEntry(string name)
    {
        Table entry = new(_sandbox.Script);
        entry["name"] = name;
        entry["path"] = string.Equals(name, _modId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_modFolder)
            ? _modFolder.Replace('\\', '/') + "/"
            : "mods/" + name + "/";

        return entry;
    }

    /// <summary>
    /// Looks an FCS record up by name and optional item type; nil when there is none.
    /// </summary>
    private DynValue GameDataByName(CallbackArguments a)
    {
        if (a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;

        int? kind = a.Count >= 2 && a[1].Type == DataType.Number ? (int)a[1].Number : null;
        if (_bridge.LookupGameData(a[0].String, kind) is not { } rec) return DynValue.Nil;

        return DynValue.NewTable(LuaSnapshotTables.GameData(_sandbox.Script, rec));
    }

    /// <summary>
    /// Every scanned character as a list of tables.
    /// </summary>
    private DynValue Characters(CallbackArguments args)
    {
        LuaWorldSnapshot snap = _bridge.Scan;
        Table arr = new(_sandbox.Script);

        for (int i = 0; i < snap.Characters.Count; i++)
        {
            arr[i + 1] = LuaSnapshotTables.Character(_sandbox.Script, snap.Characters[i]);
        }

        return DynValue.NewTable(arr);
    }

    /// <summary>
    /// The scanned character with the given slot and generation; nil when there is none.
    /// </summary>
    private DynValue CharacterById(CallbackArguments a)
    {
        if (!HasSlotAndGen(a)) return DynValue.Nil;

        int slot = (int)a[0].Number;
        int gen = (int)a[1].Number;

        foreach (LuaCharSnap c in _bridge.Scan.Characters)
        {
            if (c.Slot == slot && c.Gen == gen) return DynValue.NewTable(LuaSnapshotTables.Character(_sandbox.Script, c));
        }

        return DynValue.Nil;
    }

    /// <summary>
    /// Whether the first scanned member of a faction is its allies' (false for an unknown faction).
    /// </summary>
    private DynValue FactionIsAlly(CallbackArguments a)
    {
        if (a.Count < 1 || a[0].Type != DataType.Number) return DynValue.False;

        int f = (int)a[0].Number;

        foreach (LuaCharSnap c in _bridge.Scan.Characters)
        {
            if (c.Faction == f) return DynValue.NewBoolean(c.IsAlly);
        }

        return DynValue.False;
    }

    /// <summary>
    /// The relation the first scanned member of a faction shows to the player (0 for an unknown faction).
    /// </summary>
    private DynValue FactionRelation(CallbackArguments a)
    {
        if (a.Count < 1 || a[0].Type != DataType.Number) return DynValue.NewNumber(0);

        int f = (int)a[0].Number;

        foreach (LuaCharSnap c in _bridge.Scan.Characters)
        {
            if (c.Faction == f) return DynValue.NewNumber(c.Relation);
        }

        return DynValue.NewNumber(0);
    }

    /// <summary>
    /// The scanned door whose building or id is the given one; nil when there is none.
    /// </summary>
    private DynValue BuildingById(CallbackArguments a)
    {
        if (a.Count < 1 || a[0].Type != DataType.String) return DynValue.Nil;

        string bid = a[0].String;

        foreach (LuaDoorSnap d in _bridge.Scan.Doors)
        {
            if (IsBuilding(d, bid)) return DynValue.NewTable(LuaSnapshotTables.Door(_sandbox.Script, d));
        }

        return DynValue.Nil;
    }

    /// <summary>
    /// Whether a door's building name or id is <paramref name="bid"/>, ignoring case.
    /// </summary>
    private static bool IsBuilding(LuaDoorSnap d, string bid)
    {
        return string.Equals(d.Building, bid, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Id, bid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every scanned door as a list of tables.
    /// </summary>
    private DynValue Doors(CallbackArguments args)
    {
        LuaWorldSnapshot snap = _bridge.Scan;
        Table arr = new(_sandbox.Script);

        for (int i = 0; i < snap.Doors.Count; i++)
        {
            arr[i + 1] = LuaSnapshotTables.Door(_sandbox.Script, snap.Doors[i]);
        }

        return DynValue.NewTable(arr);
    }

    /// <summary>
    /// The items a character (slot, generation) carries as a list of tables; empty when it is not scanned.
    /// </summary>
    private DynValue CharacterItems(CallbackArguments a)
    {
        Table arr = new(_sandbox.Script);
        if (!HasSlotAndGen(a)) return DynValue.NewTable(arr);

        if (FindInventory((int)a[0].Number, (int)a[1].Number) is not { } list) return DynValue.NewTable(arr);

        for (int i = 0; i < list.Count; i++)
        {
            arr[i + 1] = LuaSnapshotTables.Item(_sandbox.Script, list[i]);
        }

        return DynValue.NewTable(arr);
    }

    /// <summary>
    /// The scanned inventory of a character (slot, generation); null when the snapshot has none for it.
    /// </summary>
    private IReadOnlyList<LuaItemSnap>? FindInventory(int slot, int gen)
    {
        IReadOnlyDictionary<(int Slot, int Gen), IReadOnlyList<LuaItemSnap>>? inventories = _bridge.Scan.Inventories;
        if (inventories is null) return null;

        return inventories.TryGetValue((slot, gen), out IReadOnlyList<LuaItemSnap>? list) ? list : null;
    }

    /// <summary>
    /// Whether the first two arguments are numbers (a character's slot and generation).
    /// </summary>
    private static bool HasSlotAndGen(CallbackArguments a)
    {
        return a.Count >= 2 && a[0].Type == DataType.Number && a[1].Type == DataType.Number;
    }

    #endregion

    #region Camera

    /// <summary>
    /// Installs the camera query and projection.
    /// </summary>
    private void RegisterCamera()
    {
        _sandbox.Expose("", "__kl_camera", CameraTable);
        _sandbox.Expose("", "__kl_cam_project", ProjectPoint);
    }

    /// <summary>
    /// The camera as a table; nil when no camera is available.
    /// </summary>
    private DynValue CameraTable(CallbackArguments args)
    {
        if (_bridge.Camera is not { } c) return DynValue.Nil;

        return DynValue.NewTable(LuaSnapshotTables.Camera(_sandbox.Script, c));
    }

    /// <summary>
    /// Projects a world point to normalized screen coordinates; nil without a camera or with fewer than three coordinates.
    /// </summary>
    private DynValue ProjectPoint(CallbackArguments a)
    {
        if (_bridge.Camera is not { } c || a.Count < 3) return DynValue.Nil;

        double wx = a[0].Number;
        double wy = a[1].Number;
        double wz = a[2].Number;

        return DynValue.NewTable(ProjectToScreen(c, wx, wy, wz));
    }

    /// <summary>
    /// The screen table (<c>x</c>, <c>y</c>, <c>dist</c>, <c>visible</c>) of a world point: a pinhole projection with the camera's right and up axes.
    /// </summary>
    private Table ProjectToScreen(LuaCameraSnap c, double wx, double wy, double wz)
    {
        double dx = wx - c.X;
        double dy = wy - c.Y;
        double dz = wz - c.Z;
        double depth = (dx * c.FwdX) + (dy * c.FwdY) + (dz * c.FwdZ);
        if (depth <= 0.1) return BehindCamera(depth);

        (double rx, double rz, double ux, double uy, double uz) = CameraAxes(c);
        double cx = (dx * rx) + (dz * rz);
        double cy = (dx * ux) + (dy * uy) + (dz * uz);

        double tanHalfY = Math.Tan(VerticalFovRadians(c) * 0.5);
        double aspect = double.IsFinite(c.Aspect) && c.Aspect > 0.1 ? c.Aspect : (16.0 / 9.0);
        double sx = 0.5 + (0.5 * (cx / (depth * tanHalfY * aspect)));
        double sy = 0.5 - (0.5 * (cy / (depth * tanHalfY)));

        Table t = new(_sandbox.Script);
        t["x"] = sx;
        t["y"] = sy;
        t["dist"] = depth;
        t["visible"] = sx >= 0.0 && sx <= 1.0 && sy >= 0.0 && sy <= 1.0;
        return t;
    }

    /// <summary>
    /// The screen table of a point at or behind the camera plane: off-screen at (-1, -1).
    /// </summary>
    private Table BehindCamera(double depth)
    {
        Table t = new(_sandbox.Script);
        t["x"] = -1;
        t["y"] = -1;
        t["visible"] = false;
        t["dist"] = depth;
        return t;
    }

    /// <summary>
    /// The camera's right axis on the ground plane (x and z) and its up axis, from its forward vector; right is +x when looking straight up or down.
    /// </summary>
    private static (double Rx, double Rz, double Ux, double Uy, double Uz) CameraAxes(LuaCameraSnap c)
    {
        double fx = c.FwdX;
        double fy = c.FwdY;
        double fz = c.FwdZ;
        double rx = -fz;
        double rz = fx;
        double rlen = Math.Sqrt((rx * rx) + (rz * rz));

        if (rlen > 1e-6)
        {
            rx /= rlen;
            rz /= rlen;
        }
        else
        {
            rx = 1;
            rz = 0;
        }

        double ux = -rz * fy;
        double uy = (rz * fx) - (rx * fz);
        double uz = rx * fy;
        return (rx, rz, ux, uy, uz);
    }

    /// <summary>
    /// The camera's vertical field of view in radians; a value above 3.5 is taken as degrees, and an unusable one falls back to 60 degrees.
    /// </summary>
    private static double VerticalFovRadians(LuaCameraSnap c)
    {
        double fovRad = c.FovY > 3.5 ? c.FovY * Math.PI / 180.0 : c.FovY;
        if (!double.IsFinite(fovRad) || fovRad <= 0.05) fovRad = Math.PI / 3.0;

        return fovRad;
    }

    #endregion

    #region Actions

    /// <summary>
    /// Installs the actions a mod can queue; each costs one unit of the tick's action budget and returns whether it was queued.
    /// </summary>
    private void RegisterActions()
    {
        _sandbox.Expose("", "__kl_floater", Floater);
        _sandbox.Expose("", "__kl_door_open", DoorOpen);
        _sandbox.Expose("", "__kl_door_lock", DoorLock);

        _sandbox.Expose("", "__kl_item_give", ItemGive);
        _sandbox.Expose("", "__kl_item_remove", ItemRemove);

        _sandbox.Expose("", "__kl_med_amputate", MedAmputate);
        _sandbox.Expose("", "__kl_med_set_blood", MedSetBlood);
        _sandbox.Expose("", "__kl_med_add_fed", MedAddFed);
    }

    /// <summary>
    /// Queues a floating text at a world position.
    /// </summary>
    private DynValue Floater(CallbackArguments a)
    {
        if (a.Count < 4 || !_trySpendAction()) return DynValue.False;

        LuaWorldAction action = new("world.floater", X: a[0].Number, Y: a[1].Number, Z: a[2].Number, Text: a[3].CastToString() ?? "");
        return Enqueue(action);
    }

    /// <summary>
    /// Queues opening or closing a door by building id.
    /// </summary>
    private DynValue DoorOpen(CallbackArguments a)
    {
        return QueueDoorFlag(a, "door.set_open");
    }

    /// <summary>
    /// Queues locking or unlocking a door by building id.
    /// </summary>
    private DynValue DoorLock(CallbackArguments a)
    {
        return QueueDoorFlag(a, "door.set_locked");
    }

    /// <summary>
    /// Queues a door action of the given kind with the flag argument.
    /// </summary>
    private DynValue QueueDoorFlag(CallbackArguments a, string kind)
    {
        if (a.Count < 2 || a[0].Type != DataType.String || !_trySpendAction()) return DynValue.False;

        LuaWorldAction action = new(kind, TargetId: a[0].String.Trim(), Flag: a[1].CastToBool());
        return Enqueue(action);
    }

    /// <summary>
    /// Queues giving a character 1 to 20 of an item record.
    /// </summary>
    private DynValue ItemGive(CallbackArguments a)
    {
        return QueueItemAction(a, "item.give", 20);
    }

    /// <summary>
    /// Queues removing 1 to 50 of an item record from a character.
    /// </summary>
    private DynValue ItemRemove(CallbackArguments a)
    {
        return QueueItemAction(a, "item.remove", 50);
    }

    /// <summary>
    /// Queues an item action of the given kind for (slot, generation, record id, optional count clamped to 1..<paramref name="maxCount"/>).
    /// </summary>
    private DynValue QueueItemAction(CallbackArguments a, string kind, int maxCount)
    {
        if (!HasSlotGenAndRecord(a) || !_trySpendAction()) return DynValue.False;

        int cnt = a.Count >= 4 && a[3].Type == DataType.Number ? (int)Math.Clamp(a[3].Number, 1, maxCount) : 1;
        LuaWorldAction action = new(kind, TargetId: a[2].String.Trim(), TargetSlot: (int)a[0].Number, TargetGen: (int)a[1].Number, Amount: cnt);
        return Enqueue(action);
    }

    /// <summary>
    /// Whether the arguments start with a slot, a generation (numbers) and a record id (string).
    /// </summary>
    private static bool HasSlotGenAndRecord(CallbackArguments a)
    {
        return a.Count >= 3 && a[0].Type == DataType.Number && a[1].Type == DataType.Number && a[2].Type == DataType.String;
    }

    /// <summary>
    /// Queues amputating a limb (the third argument) of a character.
    /// </summary>
    private DynValue MedAmputate(CallbackArguments a)
    {
        if (a.Count < 3 || !_trySpendAction()) return DynValue.False;

        LuaWorldAction action = new("medical.amputate", TargetSlot: (int)a[0].Number, TargetGen: (int)a[1].Number, Amount: (int)a[2].Number);
        return Enqueue(action);
    }

    /// <summary>
    /// Queues setting a character's blood to the third argument.
    /// </summary>
    private DynValue MedSetBlood(CallbackArguments a)
    {
        return QueueMedicalAmount(a, "medical.set_blood");
    }

    /// <summary>
    /// Queues adding the third argument to a character's food level.
    /// </summary>
    private DynValue MedAddFed(CallbackArguments a)
    {
        return QueueMedicalAmount(a, "medical.add_fed");
    }

    /// <summary>
    /// Queues a medical action of the given kind for (slot, generation, amount).
    /// </summary>
    private DynValue QueueMedicalAmount(CallbackArguments a, string kind)
    {
        if (a.Count < 3 || !_trySpendAction()) return DynValue.False;

        LuaWorldAction action = new(kind, TargetSlot: (int)a[0].Number, TargetGen: (int)a[1].Number, X: a[2].Number);
        return Enqueue(action);
    }

    /// <summary>
    /// Hands an action to the bridge for the next tick; always reports true.
    /// </summary>
    private DynValue Enqueue(LuaWorldAction action)
    {
        _bridge.EnqueueAction(action);

        return DynValue.True;
    }

    #endregion
}
