using System.Collections.Concurrent;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Simulation;

namespace Meitou.Lua;

/// <summary>
/// Per-part health snapshot exposed to Lua.
/// </summary>
public sealed record LuaBodyPartSnap(string Name, double Flesh, double Stun, double MaxHp, bool Fatal, double Wear = 0, double JuryRig = 0);

/// <summary>
/// Character snapshot exposed to Lua.
/// </summary>
public sealed record LuaCharSnap(
    int Slot,
    int Gen,
    string Name,
    int Faction,
    bool IsPlayer,
    double X,
    double Y,
    double Z,
    double Hp,
    bool Down,
    bool Dead,
    bool InCombat,
    int SquadId,
    IReadOnlyList<LuaBodyPartSnap>? Parts = null,
    double Toughness = 1,
    double Blood = 100,
    double MaxBlood = 100,
    bool Unconscious = false,
    int Relation = 0,
    bool IsEnemy = false,
    bool IsAlly = false,
    bool IsTrader = false,
    int Bounty = 0,
    string TemplateId = "",
    bool IsAnimal = false,
    double Age = 1.0,
    string RaceId = "");

/// <summary>
/// Door/gate snapshot exposed to Lua.
/// </summary>
public sealed record LuaDoorSnap(
    string Id,
    string Building,
    int Owner,
    bool IsPlayer,
    double X,
    double Y,
    double Z,
    double OpenAmount,
    string Motion,
    bool Locked,
    bool Broken,
    bool IsGate);

/// <summary>
/// Building snapshot exposed to Lua.
/// </summary>
public sealed record LuaBuildingSnap(
    string Id,
    string Name,
    string Record,
    int Owner,
    bool IsPlayer,
    double X,
    double Y,
    double Z,
    bool Complete,
    double Progress);

/// <summary>
/// Inventory item snapshot exposed to Lua.
/// </summary>
public sealed record LuaItemSnap(string Record, string Name, string Type, string Section, int Qty, int Level);

/// <summary>
/// Immutable world scan snapshot read by Lua on UI frames.
/// </summary>
public sealed record LuaWorldSnapshot(
    IReadOnlyList<LuaCharSnap> Characters,
    IReadOnlyList<LuaDoorSnap> Doors,
    IReadOnlyList<LuaBuildingSnap> Buildings,
    IReadOnlyDictionary<(int Slot, int Gen), IReadOnlyList<LuaItemSnap>>? Inventories = null)
{
    /// <summary>
    /// Empty snapshot used before a world is loaded.
    /// </summary>
    public static readonly LuaWorldSnapshot Empty = new([], [], [], new Dictionary<(int, int), IReadOnlyList<LuaItemSnap>>());
}

/// <summary>
/// Camera snapshot exposed to Lua.
/// </summary>
public sealed record LuaCameraSnap(
    double X,
    double Y,
    double Z,
    double FwdX,
    double FwdY,
    double FwdZ,
    double Yaw,
    double Pitch,
    double FovY,
    double Aspect = 16.0 / 9.0);

/// <summary>
/// Narrow read-only game event forwarded to Lua handlers.
/// </summary>
public sealed record LuaGameEvent(string Name, IReadOnlyDictionary<string, object?> Payload);

/// <summary>
/// FCS record lookup result for <c>world.gamedata:getDataByName</c>.
/// </summary>
public sealed record LuaGameDataRecord(string Id, string Name, int Type);

/// <summary>
/// Queued simulation action emitted by Lua and applied on the next deterministic tick.
/// </summary>
public sealed record LuaWorldAction(
    string Kind,
    string? TargetId = null,
    int TargetSlot = 0,
    int TargetGen = 0,
    double X = 0,
    double Y = 0,
    double Z = 0,
    bool Flag = false,
    string? Text = null,
    int Amount = 0,
    int Level = 0);

/// <summary>
/// Engine abstraction consumed by <see cref="KenshiLuaHost"/> so Lua compatibility can run both
/// against live <see cref="World"/> instances and in headless unit tests.
/// </summary>
public interface ILuaWorldBridge
{
    /// <summary>
    /// Current in-game hours, or null before a world is active.
    /// </summary>
    double? GameHours { get; }

    /// <summary>
    /// Current player cats, or null before a world is active.
    /// </summary>
    long? PlayerCats { get; }

    /// <summary>
    /// Primary selected character name, if any.
    /// </summary>
    string? SelectedName { get; }

    /// <summary>
    /// Current viewport size in pixels.
    /// </summary>
    (int Width, int Height) ViewSize { get; }

    /// <summary>
    /// Latest read-only snapshot of characters, doors, buildings, and inventories.
    /// </summary>
    LuaWorldSnapshot Scan { get; }

    /// <summary>
    /// Latest camera snapshot.
    /// </summary>
    LuaCameraSnap? Camera { get; }

    /// <summary>
    /// Looks up an FCS record by name (and optional itemType).
    /// </summary>
    LuaGameDataRecord? LookupGameData(string name, int? kind);

    /// <summary>
    /// Enqueues a world action to be applied on the next simulation tick.
    /// </summary>
    void EnqueueAction(LuaWorldAction action);
}

/// <summary>
/// Direct <see cref="World"/> + <see cref="GameDatabase"/> + <see cref="FactionRelations"/> bridge
/// for built-in <c>Meitou.Lua</c>. Captures snapshots and applies queued actions deterministically on the simulation tick.
/// </summary>
public sealed class SimulationLuaBridge : ILuaWorldBridge
{
    /// <summary>
    /// The most queued actions the bridge holds, and the most it applies in one tick.
    /// </summary>
    const int MaxQueuedActions = 64;

    /// <summary>
    /// The FCS database <see cref="LookupGameData"/> searches; null in headless tests.
    /// </summary>
    readonly GameDatabase? _database;

    /// <summary>
    /// The scanner that turns a world into the snapshot and camera Lua reads.
    /// </summary>
    readonly LuaWorldScanner _scanner;

    /// <summary>
    /// Actions Lua queued, applied on the next simulation tick.
    /// </summary>
    readonly ConcurrentQueue<LuaWorldAction> _actions = new();

    /// <summary>
    /// The boxed in-game hours of the last tick; null before a world is active.
    /// </summary>
    volatile object? _hours;

    /// <summary>
    /// The boxed player cats of the last tick; null when there is no player.
    /// </summary>
    volatile object? _cats;

    /// <summary>
    /// The selected character name the UI host last reported.
    /// </summary>
    volatile string? _selected;

    /// <summary>
    /// The snapshot of the last tick.
    /// </summary>
    volatile LuaWorldSnapshot _scan = LuaWorldSnapshot.Empty;

    /// <summary>
    /// The camera of the last tick that had a view.
    /// </summary>
    volatile LuaCameraSnap? _camera;

    /// <summary>
    /// The viewport size in pixels the renderer last reported.
    /// </summary>
    (int Width, int Height) _viewSize = (1920, 1080);

    /// <summary>
    /// Creates a simulation bridge backed by the active database and faction relations.
    /// </summary>
    public SimulationLuaBridge(GameDatabase? database = null, FactionRelations? relations = null)
    {
        _database = database;
        _scanner = new LuaWorldScanner(relations);
    }

    /// <inheritdoc />
    public double? GameHours => _hours is double h ? h : null;

    /// <inheritdoc />
    public long? PlayerCats => _cats is long c ? c : null;

    /// <inheritdoc />
    public string? SelectedName => _selected;

    /// <inheritdoc />
    public (int Width, int Height) ViewSize => _viewSize;

    /// <inheritdoc />
    public LuaWorldSnapshot Scan => _scan;

    /// <inheritdoc />
    public LuaCameraSnap? Camera => _camera;

    /// <summary>
    /// Updates the viewport pixel size from the renderer/UI host.
    /// </summary>
    public void SetViewSize(int width, int height) =>
        _viewSize = (Math.Max(320, width), Math.Max(200, height));

    /// <summary>
    /// Updates the currently selected character name from the UI host.
    /// </summary>
    public void SetSelectedName(string? name) => _selected = name;

    /// <inheritdoc />
    public LuaGameDataRecord? LookupGameData(string name, int? kind)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (_database is not null && FindRecord(_database, name) is { } found) return found;

        return new LuaGameDataRecord(name, name, kind ?? 5);
    }

    /// <inheritdoc />
    public void EnqueueAction(LuaWorldAction action)
    {
        if (_actions.Count >= MaxQueuedActions) return;

        _actions.Enqueue(action);
    }

    /// <summary>
    /// Runs on the simulation tick: applies queued Lua actions through <see cref="World.Commands"/>
    /// and publishes a fresh immutable snapshot for UI-thread Lua scripts.
    /// </summary>
    public void StepWorld(World world)
    {
        _hours = world.GameHours;
        _cats = world.Player.Exists ? world.Player.Money : null;

        ApplyQueuedActions(world);
        _scan = _scanner.CaptureScan(world);
        if (LuaWorldScanner.CaptureCamera(world) is { } camera) _camera = camera;
    }

    /// <summary>
    /// The first FCS record whose name or string id equals <paramref name="name"/> (ignoring case); null when there is none.
    /// </summary>
    static LuaGameDataRecord? FindRecord(GameDatabase database, string name)
    {
        foreach (GameRecord rec in database.Records.Values)
        {
            bool named = string.Equals(rec.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rec.StringId, name, StringComparison.OrdinalIgnoreCase);
            if (named) return new LuaGameDataRecord(rec.StringId, rec.Name, (int)rec.Type);
        }

        return null;
    }

    /// <summary>
    /// Applies up to <see cref="MaxQueuedActions"/> queued actions; a failing action never faults the tick.
    /// </summary>
    void ApplyQueuedActions(World world)
    {
        int processed = 0;
        long tick = world.Tick;
        while (processed < MaxQueuedActions && _actions.TryDequeue(out LuaWorldAction? act))
        {
            processed++;
            try
            {
                LuaActionApplier.Apply(world, act, tick);
            }
            catch (Exception)
            {
                // Simulation safety: a failed Lua action never faults the tick.
            }
        }
    }
}
