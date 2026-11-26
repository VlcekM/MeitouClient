using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Player;

namespace Meitou.Lua;

/// <summary>Per-part health snapshot exposed to Lua.</summary>
public sealed record LuaBodyPartSnap(string Name, double Flesh, double Stun, double MaxHp, bool Fatal, double Wear = 0, double JuryRig = 0);

/// <summary>Character snapshot exposed to Lua.</summary>
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

/// <summary>Door/gate snapshot exposed to Lua.</summary>
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

/// <summary>Building snapshot exposed to Lua.</summary>
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

/// <summary>Inventory item snapshot exposed to Lua.</summary>
public sealed record LuaItemSnap(string Record, string Name, string Type, string Section, int Qty, int Level);

/// <summary>Immutable world scan snapshot read by Lua on UI frames.</summary>
public sealed record LuaWorldSnapshot(
    IReadOnlyList<LuaCharSnap> Characters,
    IReadOnlyList<LuaDoorSnap> Doors,
    IReadOnlyList<LuaBuildingSnap> Buildings,
    IReadOnlyDictionary<(int Slot, int Gen), IReadOnlyList<LuaItemSnap>>? Inventories = null)
{
    /// <summary>Empty snapshot used before a world is loaded.</summary>
    public static readonly LuaWorldSnapshot Empty = new([], [], [], new Dictionary<(int, int), IReadOnlyList<LuaItemSnap>>());
}

/// <summary>Camera snapshot exposed to Lua.</summary>
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

/// <summary>Narrow read-only game event forwarded to Lua handlers.</summary>
public sealed record LuaGameEvent(string Name, IReadOnlyDictionary<string, object?> Payload);

/// <summary>FCS record lookup result for <c>world.gamedata:getDataByName</c>.</summary>
public sealed record LuaGameDataRecord(string Id, string Name, int Type);

/// <summary>Queued simulation action emitted by Lua and applied on the next deterministic tick.</summary>
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
    /// <summary>Current in-game hours, or null before a world is active.</summary>
    double? GameHours { get; }

    /// <summary>Current player cats, or null before a world is active.</summary>
    long? PlayerCats { get; }

    /// <summary>Primary selected character name, if any.</summary>
    string? SelectedName { get; }

    /// <summary>Current viewport size in pixels.</summary>
    (int Width, int Height) ViewSize { get; }

    /// <summary>Latest read-only snapshot of characters, doors, buildings, and inventories.</summary>
    LuaWorldSnapshot Scan { get; }

    /// <summary>Latest camera snapshot.</summary>
    LuaCameraSnap? Camera { get; }

    /// <summary>Looks up an FCS record by name (and optional itemType).</summary>
    LuaGameDataRecord? LookupGameData(string name, int? kind);

    /// <summary>Enqueues a world action to be applied on the next simulation tick.</summary>
    void EnqueueAction(LuaWorldAction action);
}

/// <summary>
/// Direct <see cref="World"/> + <see cref="GameDatabase"/> + <see cref="FactionRelations"/> bridge
/// for built-in <c>Meitou.Lua</c>. Captures snapshots and applies queued actions deterministically on the simulation tick.
/// </summary>
public sealed class SimulationLuaBridge : ILuaWorldBridge
{
    const int MaxQueuedActions = 64;
    const int MaxScannedChars = 512;

    readonly GameDatabase? _database;
    readonly FactionRelations? _relations;
    readonly ConcurrentQueue<LuaWorldAction> _actions = new();
    volatile object? _hours;
    volatile object? _cats;
    volatile string? _selected;
    volatile LuaWorldSnapshot _scan = LuaWorldSnapshot.Empty;
    volatile LuaCameraSnap? _camera;
    (int Width, int Height) _viewSize = (1920, 1080);

    /// <summary>Creates a simulation bridge backed by the active database and faction relations.</summary>
    public SimulationLuaBridge(GameDatabase? database = null, FactionRelations? relations = null)
    {
        _database = database;
        _relations = relations;
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

    /// <summary>Updates the viewport pixel size from the renderer/UI host.</summary>
    public void SetViewSize(int width, int height) =>
        _viewSize = (Math.Max(320, width), Math.Max(200, height));

    /// <summary>Updates the currently selected character name from the UI host.</summary>
    public void SetSelectedName(string? name) => _selected = name;

    /// <inheritdoc />
    public LuaGameDataRecord? LookupGameData(string name, int? kind)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_database is not null)
        {
            foreach (GameRecord rec in _database.Records.Values)
            {
                if (string.Equals(rec.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rec.StringId, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new LuaGameDataRecord(rec.StringId, rec.Name, (int)rec.Type);
                }
            }
        }
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
        CaptureScan(world);
        CaptureCamera(world);
    }

    void ApplyQueuedActions(World world)
    {
        int processed = 0;
        long tick = world.Tick;
        while (processed < MaxQueuedActions && _actions.TryDequeue(out LuaWorldAction? act))
        {
            processed++;
            try
            {
                switch (act.Kind)
                {
                    case "door.set_open" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        bool anyNeedsToggle = false;
                        foreach (Fixture f in world.Fixtures.OfBuilding(act.TargetId))
                        {
                            if (f.Kind != FixtureKind.Door || f.DoorBroken) continue;
                            bool isOpen = f.Motion is DoorMotion.Open or DoorMotion.Opening || f.OpenAmount > 0.5f;
                            if (isOpen != act.Flag) { anyNeedsToggle = true; break; }
                        }
                        if (anyNeedsToggle) world.Commands.Enqueue(new ToggleDoorsOrder(act.TargetId) { Tick = tick });
                        break;
                    }
                    case "door.set_locked" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        bool anyNeedsToggle = false;
                        foreach (Fixture f in world.Fixtures.OfBuilding(act.TargetId))
                        {
                            if (f.Kind != FixtureKind.Door || f.DoorBroken) continue;
                            bool isLocked = f.Lock?.EffectivelyLocked ?? false;
                            if (isLocked != act.Flag) { anyNeedsToggle = true; break; }
                        }
                        if (anyNeedsToggle) world.Commands.Enqueue(new ToggleDoorLocksOrder(act.TargetId) { Tick = tick });
                        break;
                    }
                    case "world.floater" when !string.IsNullOrEmpty(act.Text):
                        world.Floaters?.Enqueue(Floater.Label(new Vector3((float)act.X, (float)act.Y, (float)act.Z), act.Text));
                        break;
                    case "item.give" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        CharacterId cid = new(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Inventory is { } inv
                            && world.Fixtures.Items is { } factory)
                        {
                            int count = Math.Clamp(act.Amount <= 0 ? 1 : act.Amount, 1, 20);
                            int lvl = Math.Clamp(act.Level, 0, 100);
                            for (int i = 0; i < count; i++)
                            {
                                ItemInstance? item = factory.Create(act.TargetId, lvl, 1f);
                                if (item is null || !inv.Add(item, true)) break;
                            }
                        }
                        break;
                    }
                    case "item.remove" when !string.IsNullOrEmpty(act.TargetId):
                    {
                        CharacterId cid = new(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Inventory is { } inv)
                        {
                            int left = Math.Clamp(act.Amount <= 0 ? 1 : act.Amount, 1, 50);
                            while (left > 0)
                            {
                                ItemInstance? match = null;
                                foreach (ItemInstance it in inv.Items)
                                {
                                    if (string.Equals(it.Record, act.TargetId, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(it.Name, act.TargetId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        match = it;
                                        break;
                                    }
                                }
                                if (match is null || !inv.RemoveOne(match)) break;
                                left--;
                            }
                        }
                        break;
                    }
                    case "medical.amputate":
                    {
                        CharacterId cid = new(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Medical is { } med)
                        {
                            string want = act.Amount switch { 0 => "left arm", 1 => "right arm", 2 => "left leg", 3 => "right leg", _ => "" };
                            foreach (HealthPart pt in med.Parts)
                            {
                                string pid = pt.Template?.StringId ?? "";
                                if (want.Length > 0 && pid.Contains(want, StringComparison.OrdinalIgnoreCase))
                                {
                                    pt.Limb = LimbState.Stump;
                                    pt.Flesh = Math.Min(pt.Flesh, -pt.MaxHp);
                                    break;
                                }
                            }
                        }
                        break;
                    }
                    case "medical.set_blood":
                    {
                        CharacterId cid = new(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Medical is { } med
                            && double.IsFinite(act.X))
                        {
                            med.Blood = (float)Math.Clamp(act.X, -200, 200);
                        }
                        break;
                    }
                    case "medical.add_fed":
                    {
                        CharacterId cid = new(act.TargetSlot, act.TargetGen);
                        if (world.Characters.TryResolveNext(cid, out int slot)
                            && world.Characters.Cold(slot)?.Medical is { } med
                            && double.IsFinite(act.X))
                        {
                            med.Fed = Math.Clamp(med.Fed + (float)Math.Clamp(act.X, -10, 10), 0, 100);
                        }
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // Simulation safety: a failed Lua action never faults the tick.
            }
        }
    }

    void CaptureScan(World world)
    {
        int playerFaction = world.Player.Exists ? world.Player.Faction : -1;
        List<LuaCharSnap> chars = [];
        Dictionary<(int Slot, int Gen), IReadOnlyList<LuaItemSnap>> invs = [];
        CharacterTable table = world.Characters;
        ReadOnlySpan<CharacterHot> next = table.Next;
        int hw = table.HighWater;

        for (int slot = 0; slot < hw && chars.Count < MaxScannedChars; slot++)
        {
            ref readonly CharacterHot hot = ref next[slot];
            if (!hot.Alive || hot.Dormant) continue;
            CharacterCold? cold = table.Cold(slot);
            if (cold is null) continue;
            CharacterId id = table.IdOf(slot);
            bool uncon = cold.Medical?.Unconscious ?? false;
            bool dead = cold.Medical?.Dead ?? false;
            bool down = uncon || dead;
            List<LuaBodyPartSnap>? parts = null;
            double blood = 100;
            if (cold.Medical is { } m)
            {
                blood = float.IsFinite(m.Blood) ? Math.Clamp(m.Blood, 0, 200) : 100;
                if (m.Parts.Count > 0)
                {
                    int pn = Math.Min(m.Parts.Count, 12);
                    parts = new List<LuaBodyPartSnap>(pn);
                    for (int pi = 0; pi < pn; pi++)
                    {
                        HealthPart pt = m.Parts[pi];
                        string pName = pt.Template?.StringId ?? $"part{pi}";
                        bool fatal = pi < 3
                            || pName.Contains("head", StringComparison.OrdinalIgnoreCase)
                            || pName.Contains("chest", StringComparison.OrdinalIgnoreCase)
                            || pName.Contains("stomach", StringComparison.OrdinalIgnoreCase);
                        parts.Add(new LuaBodyPartSnap(pName, pt.Flesh, pt.Stun, Math.Max(1, pt.MaxHp), fatal, pt.Wear, pt.Rig));
                    }
                }
            }

            double tough = cold.Stats is { } st && float.IsFinite(st[StatsEnumerated.Toughness])
                ? Math.Clamp(st[StatsEnumerated.Toughness], 0, 1000)
                : 1;
            int rel = 0;
            bool isAlly = cold.IsPlayer || (playerFaction >= 0 && cold.Faction == playerFaction);
            bool isEnemy = !isAlly && cold.InCombat;
            if (_relations is not null && playerFaction >= 0 && cold.Faction >= 0)
            {
                float r = _relations.Get(playerFaction, cold.Faction);
                if (float.IsFinite(r))
                {
                    rel = (int)MathF.Round(r);
                    if (!isAlly && r >= FactionRelations.AllyThreshold) isAlly = true;
                    if (!isAlly && r <= FactionRelations.HostileThreshold) isEnemy = true;
                }
            }
            else
            {
                rel = isAlly ? 100 : (isEnemy ? -50 : 0);
            }

            chars.Add(new LuaCharSnap(
                id.Slot, id.Generation,
                string.IsNullOrWhiteSpace(cold.Name) ? "Drifter" : cold.Name.Trim(),
                cold.Faction, cold.IsPlayer,
                hot.Position.X, hot.Position.Y, hot.Position.Z,
                hot.Health, down, dead, cold.InCombat, cold.SquadId,
                parts, tough, blood, 100, uncon, rel, isEnemy, isAlly,
                TemplateId: cold.RecordId ?? "",
                IsAnimal: cold.Animal is not null,
                Age: float.IsFinite(cold.Age) ? cold.Age : 1.0,
                RaceId: cold.Race?.Name ?? ""));

            if (cold.IsPlayer && cold.Inventory is { } inv && inv.Items.Count > 0)
            {
                int n = Math.Min(inv.Items.Count, 32);
                List<LuaItemSnap> list = new(n);
                for (int i = 0; i < n; i++)
                {
                    ItemInstance it = inv.Items[i];
                    list.Add(new LuaItemSnap(it.Record ?? "", it.Name ?? "", it.Type.ToString(), it.Section ?? "", it.Quantity, it.Level));
                }
                invs[(id.Slot, id.Generation)] = list;
            }
        }

        List<LuaDoorSnap> doors = [];
        foreach (Fixture f in world.Fixtures.All)
        {
            if (f.Kind != FixtureKind.Door) continue;
            bool locked = f.Lock?.EffectivelyLocked ?? false;
            doors.Add(new LuaDoorSnap(
                f.Id, f.Building, f.Owner, playerFaction >= 0 && f.Owner == playerFaction,
                f.Position.X, f.Position.Y, f.Position.Z,
                f.OpenAmount, f.Motion.ToString().ToLowerInvariant(),
                locked, f.DoorBroken, f.GateCode != 0));
        }

        List<LuaBuildingSnap> blds = [];
        foreach (var b in world.Buildings.All)
        {
            blds.Add(new LuaBuildingSnap(
                b.Id, b.Name ?? b.Record ?? "", b.Record ?? "",
                b.Owner, playerFaction >= 0 && b.Owner == playerFaction,
                b.Position.X, b.Position.Y, b.Position.Z,
                b.Complete, b.Progress));
        }

        _scan = new LuaWorldSnapshot(chars, doors, blds, invs);
    }

    void CaptureCamera(World world)
    {
        if (world.View is not { } v) return;
        Vector3 fwd = v.Forward;
        float len = fwd.Length();
        if (len > 1e-5f) fwd /= len;
        double yaw = ((Math.Atan2(fwd.X, fwd.Z) * 180.0 / Math.PI) + 360.0) % 360.0;
        double pitch = Math.Asin(Math.Clamp((double)fwd.Y, -1.0, 1.0)) * 180.0 / Math.PI;
        double aspect = float.IsFinite(v.Aspect) && v.Aspect > 0.1f ? v.Aspect : (16.0 / 9.0);
        _camera = new LuaCameraSnap(v.Eye.X, v.Eye.Y, v.Eye.Z, fwd.X, fwd.Y, fwd.Z, yaw, pitch, v.FovY, aspect);
    }
}
