using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation;

/// <summary>
/// The per-character snapshot last seen by <see cref="CharacterStateObservers"/> (<see cref="CharacterCold.StateWatch"/>); allocated only while at
/// least one state observer is registered, cleared when the character leaves the world, and never saved or hashed.
/// </summary>
sealed class CharacterStateWatch
{
    /// <summary>
    /// Whether the initial snapshot has been taken (the first look records baseline state without firing transitions).
    /// </summary>
    internal bool Initialized;

    /// <summary>
    /// Last seen <see cref="MedicalState.Unconscious"/>.
    /// </summary>
    internal bool Unconscious;

    /// <summary>
    /// Last seen <see cref="MedicalState"/> instance (a new body resets limb tracking without spurious events).
    /// </summary>
    internal MedicalState? Medical;

    /// <summary>
    /// Last seen <see cref="HealthPart.Limb"/> per body part.
    /// </summary>
    internal LimbState[] Limbs = [];

    /// <summary>
    /// Last seen <see cref="CharacterCold.Carrying"/>.
    /// </summary>
    internal CharacterId Carrying = CharacterId.None;

    /// <summary>
    /// Last seen <see cref="CharacterCold.InSomething"/>.
    /// </summary>
    internal HeldState InSomething;

    /// <summary>
    /// Last seen <see cref="CharacterCold.InWhat"/>.
    /// </summary>
    internal string? InWhat;

    /// <summary>
    /// Last seen <see cref="CharacterCold.Faction"/>.
    /// </summary>
    internal int Faction;

    /// <summary>
    /// Whether the equipped-items baseline has been captured (only populated when an <see cref="IEquipObserver"/> is active).
    /// </summary>
    internal bool EquipInitialized;

    /// <summary>
    /// Last seen equipped items (every item whose section is not <c>main</c>) as (section, record id) pairs.
    /// </summary>
    internal List<(string Section, string RecordId)>? Equipped;
}

/// <summary>
/// The world's character lifecycle and state observers: knockout, limb state, carry, held state (bed / cage), faction change, recruitment,
/// equipment change and character removal. Zero cost when no observer is registered; swept serially in slot order after the slow world and when a
/// character leaves the table (<see cref="CharacterWatch"/>), so event order never depends on thread count.
/// </summary>
sealed class CharacterStateObservers
{
    /// <summary>
    /// Knockout / wake-up observers.
    /// </summary>
    readonly SerialHooks<IKnockoutObserver> _knockout = new(0x4B4F4F42, "knockout observer");

    /// <summary>
    /// Limb state observers.
    /// </summary>
    readonly SerialHooks<ILimbObserver> _limb = new(0x4C4D424F, "limb observer");

    /// <summary>
    /// Carry pick-up / put-down observers.
    /// </summary>
    readonly SerialHooks<ICarryObserver> _carry = new(0x4352594F, "carry observer");

    /// <summary>
    /// Bed / cage held-state observers.
    /// </summary>
    readonly SerialHooks<IHeldStateObserver> _held = new(0x484C444F, "held state observer");

    /// <summary>
    /// Faction change observers.
    /// </summary>
    readonly SerialHooks<IFactionChangeObserver> _faction = new(0x4641434F, "faction change observer");

    /// <summary>
    /// Player faction recruitment / dismissal observers.
    /// </summary>
    readonly SerialHooks<IRecruitObserver> _recruit = new(0x5245434F, "recruit observer");

    /// <summary>
    /// Equipment change observers.
    /// </summary>
    readonly SerialHooks<IEquipObserver> _equip = new(0x4551504F, "equip observer");

    /// <summary>
    /// Character removal observers.
    /// </summary>
    readonly SerialHooks<ICharacterRemovedObserver> _removed = new(0x524D564F, "character removed observer");

    /// <summary>
    /// Scratch buffer reused for comparing equipped items without allocating per character per tick.
    /// </summary>
    readonly List<(string Section, string RecordId)> _equipNow = [];

    /// <summary>
    /// Whether any observer in this group is active.
    /// </summary>
    internal bool Active => AnyStateWatch || _removed.Active;

    /// <summary>
    /// Whether any per-tick state diff observer (everything except removal) is active.
    /// </summary>
    internal bool AnyStateWatch => _knockout.Active || _limb.Active || _carry.Active || _held.Active
        || _faction.Active || _recruit.Active || _equip.Active;

    /// <summary>
    /// Registers <paramref name="system"/> against every observer interface it implements.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        _knockout.Register(system, systemIndex);
        _limb.Register(system, systemIndex);
        _carry.Register(system, systemIndex);
        _held.Register(system, systemIndex);
        _faction.Register(system, systemIndex);
        _recruit.Register(system, systemIndex);
        _equip.Register(system, systemIndex);
        _removed.Register(system, systemIndex);
    }

    /// <summary>
    /// Adds any disabled observers to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        _knockout.HashFaults(ref hash);
        _limb.HashFaults(ref hash);
        _carry.HashFaults(ref hash);
        _held.HashFaults(ref hash);
        _faction.HashFaults(ref hash);
        _recruit.HashFaults(ref hash);
        _equip.HashFaults(ref hash);
        _removed.HashFaults(ref hash);
    }

    /// <summary>
    /// Sweeps all characters in slot order and delivers any state transitions to enabled observers. Serial; runs after the slow world.
    /// </summary>
    internal void Tell(World world)
    {
        if (!AnyStateWatch) return;

        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater && AnyStateWatch; slot++)
        {
            if (table.Cold(slot) is { } cold) TellOne(world, slot, cold);
        }
    }

    /// <summary>
    /// Flushes any final state transitions for a leaving character, clears its watch, and notifies <see cref="ICharacterRemovedObserver"/>. Serial.
    /// </summary>
    internal static void Leaving(World world, int slot, CharacterCold cold)
    {
        var observers = world.StateObservers;
        if (observers.AnyStateWatch) observers.TellOne(world, slot, cold);

        cold.StateWatch = null;

        if (!observers._removed.Active) return;

        var id = new CharacterId(slot, world.Characters.Next[slot].Generation);
        int platoon = world.Squads.Find(cold.SquadId)?.PlatoonId ?? -1;
        bool dead = cold.Medical?.Dead == true;
        var notice = new CharacterRemoved(world.Tick, id, cold.RecordId, cold.Faction, platoon, dead);
        Notify(observers._removed, (obs, w) => obs.Removed(w, notice), world);
    }

    /// <summary>
    /// Captures the baseline or emits state transitions for one character.
    /// </summary>
    void TellOne(World world, int slot, CharacterCold cold)
    {
        var watch = cold.StateWatch ??= new CharacterStateWatch();
        if (!watch.Initialized)
        {
            CaptureBaseline(watch, cold);
            return;
        }

        var id = world.Characters.IdOf(slot);
        CheckMedical(world, id, watch, cold.Medical);
        CheckCarry(world, id, watch, cold);
        CheckHeld(world, id, watch, cold);
        CheckFaction(world, id, watch, cold);
        CheckEquip(world, id, watch, cold);
    }

    /// <summary>
    /// Records a character's initial state without emitting transitions.
    /// </summary>
    void CaptureBaseline(CharacterStateWatch watch, CharacterCold cold)
    {
        watch.Initialized = true;
        watch.Carrying = cold.Carrying;
        watch.InSomething = cold.InSomething;
        watch.InWhat = cold.InWhat;
        watch.Faction = cold.Faction;

        if (cold.Medical is { } medical)
        {
            watch.Medical = medical;
            watch.Unconscious = medical.Unconscious;
            watch.Limbs = SnapshotLimbs(medical);
        }

        if (_equip.Active) CaptureEquipBaseline(watch, cold.Inventory);
    }

    /// <summary>
    /// Checks knockout and limb-state transitions on <paramref name="medical"/>.
    /// </summary>
    void CheckMedical(World world, CharacterId id, CharacterStateWatch watch, MedicalState? medical)
    {
        if (medical is null) return;

        if (!ReferenceEquals(watch.Medical, medical))
        {
            watch.Medical = medical;
            watch.Unconscious = medical.Unconscious;
            watch.Limbs = SnapshotLimbs(medical);
            return;
        }

        if (watch.Unconscious != medical.Unconscious)
        {
            watch.Unconscious = medical.Unconscious;
            if (_knockout.Active)
            {
                var change = new KnockoutChange(world.Tick, id, medical.Unconscious, medical.KoTimer, medical.Coma);
                Notify(_knockout, (obs, w) => obs.KnockoutChanged(w, change), world);
            }
        }

        var parts = medical.Parts;
        if (watch.Limbs.Length != parts.Count)
        {
            watch.Limbs = SnapshotLimbs(medical);
            return;
        }

        for (int p = 0; p < parts.Count; p++)
        {
            var now = parts[p].Limb;
            var before = watch.Limbs[p];
            if (before == now) continue;

            watch.Limbs[p] = now;
            if (_limb.Active)
            {
                var change = new LimbChange(world.Tick, id, p, parts[p].Template.Name, before, now);
                Notify(_limb, (obs, w) => obs.LimbChanged(w, change), world);
            }
        }
    }

    /// <summary>
    /// Checks pick-up and put-down transitions on <see cref="CharacterCold.Carrying"/>.
    /// </summary>
    void CheckCarry(World world, CharacterId id, CharacterStateWatch watch, CharacterCold cold)
    {
        var before = watch.Carrying;
        var after = cold.Carrying;
        if (before == after) return;

        watch.Carrying = after;
        if (!_carry.Active) return;

        if (!before.IsNone)
        {
            var drop = new CarryChange(world.Tick, id, before, PickedUp: false);
            Notify(_carry, (obs, w) => obs.CarryChanged(w, drop), world);
        }

        if (!after.IsNone)
        {
            var pick = new CarryChange(world.Tick, id, after, PickedUp: true);
            Notify(_carry, (obs, w) => obs.CarryChanged(w, pick), world);
        }
    }

    /// <summary>
    /// Checks bed / cage held-state transitions on <see cref="CharacterCold.InSomething"/> and <see cref="CharacterCold.InWhat"/>.
    /// </summary>
    void CheckHeld(World world, CharacterId id, CharacterStateWatch watch, CharacterCold cold)
    {
        var before = watch.InSomething;
        var after = cold.InSomething;
        string? fixtureBefore = watch.InWhat;
        string? fixtureAfter = cold.InWhat;
        if (before == after && fixtureBefore == fixtureAfter) return;

        watch.InSomething = after;
        watch.InWhat = fixtureAfter;
        if (!_held.Active) return;

        var change = new HeldStateChange(world.Tick, id, before, after, fixtureAfter ?? fixtureBefore);
        Notify(_held, (obs, w) => obs.HeldStateChanged(w, change), world);
    }

    /// <summary>
    /// Checks faction changes and player-faction recruitment / dismissal transitions on <see cref="CharacterCold.Faction"/>.
    /// </summary>
    void CheckFaction(World world, CharacterId id, CharacterStateWatch watch, CharacterCold cold)
    {
        int before = watch.Faction;
        int after = cold.Faction;
        if (before == after) return;

        watch.Faction = after;

        if (_faction.Active)
        {
            var change = new FactionChange(world.Tick, id, before, after);
            Notify(_faction, (obs, w) => obs.FactionChanged(w, change), world);
        }

        if (!_recruit.Active) return;

        int player = world.Player.Faction;
        if (after == player && before != player)
        {
            var joined = new RecruitChange(world.Tick, id, before, Joined: true);
            Notify(_recruit, (obs, w) => obs.RecruitChanged(w, joined), world);
        }
        else if (before == player && after != player)
        {
            var left = new RecruitChange(world.Tick, id, after, Joined: false);
            Notify(_recruit, (obs, w) => obs.RecruitChanged(w, left), world);
        }
    }

    /// <summary>
    /// Checks equipped-item transitions across all non-main inventory sections.
    /// </summary>
    void CheckEquip(World world, CharacterId id, CharacterStateWatch watch, CharacterCold cold)
    {
        if (!_equip.Active) return;

        if (!watch.EquipInitialized)
        {
            CaptureEquipBaseline(watch, cold.Inventory);
            return;
        }

        var prev = watch.Equipped!;
        CollectEquipped(cold.Inventory, _equipNow);
        if (SameEquipList(prev, _equipNow)) return;

        // Emit unequipped items first, then equipped items.
        foreach (var item in prev)
        {
            if (_equipNow.Contains(item)) continue;

            var unequip = new EquipChange(world.Tick, id, item.Section, item.RecordId, Equipped: false);
            Notify(_equip, (obs, w) => obs.EquipChanged(w, unequip), world);
        }

        foreach (var item in _equipNow)
        {
            if (prev.Contains(item)) continue;

            var equip = new EquipChange(world.Tick, id, item.Section, item.RecordId, Equipped: true);
            Notify(_equip, (obs, w) => obs.EquipChanged(w, equip), world);
        }

        prev.Clear();
        prev.AddRange(_equipNow);
    }

    /// <summary>
    /// Captures the initial equipped items of <paramref name="inventory"/> without emitting transitions.
    /// </summary>
    static void CaptureEquipBaseline(CharacterStateWatch watch, Inventory? inventory)
    {
        watch.EquipInitialized = true;
        watch.Equipped ??= [];
        CollectEquipped(inventory, watch.Equipped);
    }

    /// <summary>
    /// Collects all items in non-main sections of <paramref name="inventory"/> into <paramref name="target"/>.
    /// </summary>
    static void CollectEquipped(Inventory? inventory, List<(string Section, string RecordId)> target)
    {
        target.Clear();
        if (inventory is null) return;

        foreach (var item in inventory.Items)
        {
            if (item.Section != "main") target.Add((item.Section, item.Record));
        }
    }

    /// <summary>
    /// True when two equipped-item lists contain the exact same entries in the same order.
    /// </summary>
    static bool SameEquipList(List<(string Section, string RecordId)> a, List<(string Section, string RecordId)> b)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// Copies the current <see cref="HealthPart.Limb"/> values of <paramref name="medical"/>.
    /// </summary>
    static LimbState[] SnapshotLimbs(MedicalState medical)
    {
        var parts = medical.Parts;
        var limbs = new LimbState[parts.Count];
        for (int i = 0; i < parts.Count; i++) limbs[i] = parts[i].Limb;
        return limbs;
    }

    /// <summary>
    /// Delivers a notification to every enabled hook in <paramref name="hooks"/>, disabling any that throw.
    /// </summary>
    static void Notify<T>(SerialHooks<T> hooks, Action<T, World> invoke, World world) where T : class
    {
        for (int i = 0; i < hooks.Count; i++)
        {
            if (!hooks.Enabled(i)) continue;

            try
            {
                invoke(hooks[i], world);
            }
            catch (Exception e)
            {
                hooks.Threw(i, e);
            }
        }
    }
}
