using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// Melee combat (docs/simulation.md "Combat", docs/game/combat.md). One system, three parts per tick:
/// <list type="bullet">
/// <item><b>Act</b> (parallel, per character, reading the last tick's <see cref="CombatSlot"/> array and the table's previous state, writing only its
/// own): the medical tick; a defender looks for blows coming at it and picks a reaction (block roll, wrong-direction block on a failure, dodge for
/// fists); an attacker keeps its swing going and, when the animation reaches a blow's <c>anim blocked frame</c>, emits a <see cref="BlowEffect"/> to
/// the target; a free attacker in reach picks a technique and starts it.</item>
/// <item><b>Apply</b> (serial, in the commit's order: target, source, system, sequence): the outcome from the defender's reaction progress
/// ("blocked", "dodged" or "hit", combat.md), then for a hit the damage packet, part choice, armour, toughness and the medical hit, a stagger for a
/// heavy hit, and XP for both sides.</item>
/// <item><b>Move</b>: faces the target and, with <see cref="CombatOptions.SelfApproach"/>, walks up to it.</item>
/// </list>
/// Everything random is a function of (seed, character, kind, counter) so the result does not depend on the thread count. Stats, bodies and gear
/// change only here in the commit and in the owner's own Act (the medical tick); other characters only read the <see cref="CombatSlot"/> copy.
/// </summary>
public sealed partial class CombatSystem : ITickSystem, IStateHashed, ITaskOwner, ITaskHandler, IOrderOwner
{
    /// <summary>
    /// The <see cref="Effect.Kind"/> of a blow: <c>Arg</c> is the blow (1 or 2) and <c>Amount</c> the attacker's attack number.
    /// </summary>
    public const int BlowEffect = 1;

    /// <summary>
    /// The chance that an attack on a character getting up with its fight off knocks it back down, and for how long (<c>AI::underMeleeAttack</c>, 0.1
    /// and 10.0, <b>Verified</b>).
    /// </summary>
    public const float KnockDownChance = 0.1f, KnockDownSeconds = 10;

    /// <summary>
    /// The techniques the fights choose from (<see cref="PunchReachFix"/> may change their reach) and the same as the data wrote them.
    /// </summary>
    readonly CombatTechnique[] _techniques, _asWritten;
    /// <summary>
    /// The combat CONSTANTS (attack slots, rates, reaches).
    /// </summary>
    readonly CombatConstants _combat;
    /// <summary>
    /// The CONSTANTS the medical hits and the XP read.
    /// </summary>
    readonly GameConstants _constants;
    /// <summary>
    /// The body options the medical hits get.
    /// </summary>
    readonly BodyOptions _options;
    /// <summary>
    /// The clip lengths of the techniques by skeleton.
    /// </summary>
    readonly AnimationLengths _lengths;
    /// <summary>
    /// The XP rates of the fights.
    /// </summary>
    readonly XpService _xp;
    /// <summary>
    /// The resolved blows (<see cref="Log"/>), filled while <see cref="CombatOptions.RecordLog"/> is on.
    /// </summary>
    readonly List<CombatLogEntry> _log = [];
    /// <summary>
    /// The medical events of the hit being applied; one list for every hit, as Apply is serial.
    /// </summary>
    readonly List<MedicalEvent> _hitEvents = [];
    /// <summary>
    /// The fight states as of the last tick: what every Act reads.
    /// </summary>
    CombatSlot[] _read = [];
    /// <summary>
    /// The fight states being written this tick; swapped with <see cref="_read"/> in the slow world.
    /// </summary>
    CombatSlot[] _write = [];
    /// <summary>
    /// The value behind <see cref="PunchReachFix"/>.
    /// </summary>
    bool _punchReachFix;

    /// <summary>
    /// The tick whose slow world swapped the state arrays last.
    /// </summary>
    long _swappedTick = -1;

    #region Configuration

    /// <summary>
    /// The squad memory (set by the standard systems): the damage floater reads the active squad's TEMP ENEMY / TEMP ALLY
    /// tags (<c>FUN_1406508d0</c>).
    /// </summary>
    public Dialogue.SquadMemory? Memory { get; set; }

    /// <summary>
    /// A combat system over <paramref name="techniques"/> with the game's constants, the body options and the clip lengths;
    /// <paramref name="settings"/> null: the defaults.
    /// </summary>
    public CombatSystem(
        IEnumerable<CombatTechnique> techniques,
        CombatConstants combat,
        GameConstants constants,
        BodyOptions options,
        AnimationLengths lengths,
        CombatOptions? settings = null)
    {
        this._techniques = [.. techniques];
        _asWritten = [.. this._techniques];
        this._combat = combat;
        this._constants = constants;
        this._options = options;
        this._lengths = lengths;
        Settings = settings ?? new CombatOptions();
        UseAttackSlots(Settings.AttackSlots);
        LateDefences = Settings.LateDefences;
        GunnerFacesBarrel = Settings.GunnerFacesBarrel;
        PunchReachFix = Settings.PunchReachFix;
        StuckLimit = Math.Clamp(Settings.StuckLimit, RangedTuning.MinStuckLimit, RangedTuning.MaxStuckLimit);
        _xp = new XpService(constants, options);
    }

    /// <summary>
    /// The combat options it was made with.
    /// </summary>
    public CombatOptions Settings { get; }

    /// <summary>
    /// How many enemies may melee one target at once, before the target race's <c>extra attack slots</c>: CONSTANTS <c>max num attack slots</c> (1,
    /// Faithful) unless a setting changes it.
    /// </summary>
    public int AttackSlots { get; set; }

    /// <summary>
    /// Sets <see cref="AttackSlots"/> to a setting's value, or to the loaded CONSTANTS <c>max num attack slots</c> for null (the Faithful meaning,
    /// <c>FUN_1406661d0</c>, <b>Verified</b>).
    /// </summary>
    public void UseAttackSlots(int? slots) => AttackSlots = Math.Max(1, slots ?? _combat.MaxNumAttackSlots);

    /// <summary>
    /// Stuck bolts kept in the world per bolt mesh (the settings' <c>harpoonLimit</c>, <see cref="CombatOptions.StuckLimit"/>): past it the one that
    /// flew longest goes (<c>FUN_14043acf0</c>).
    /// </summary>
    public int StuckLimit
    {
        get => _projectiles.StuckLimit;
        set => _projectiles.StuckLimit = value;
    }

    /// <summary>
    /// A late but successfully rolled dodge or block still counts (<see cref="CombatOptions.LateDefences"/>); off is the game's rule.
    /// </summary>
    public bool LateDefences { get; set; }

    /// <summary>
    /// A turret's gunner faces along the barrel as the gun turns (<see cref="CombatOptions.GunnerFacesBarrel"/>); off is the game's facing.
    /// </summary>
    public bool GunnerFacesBarrel { get; set; }

    /// <summary>
    /// A fist attack whose <c>attack distance</c> is negative but above -100 (base data: "ma badpunch", -4, the only punch up to martial arts 20)
    /// reads it as positive (<see cref="CombatOptions.PunchReachFix"/>, Meitou fix; docs/game/combat.md "Attack technique choice"); off: as written,
    /// as the game does.
    /// </summary>
    public bool PunchReachFix
    {
        get => _punchReachFix;
        set
        {
            _punchReachFix = value;
            for (int t = 0; t < _techniques.Length; t++)
            {
                bool reads = value && _asWritten[t] is { IsBlock: false, IsDodge: false, AttackDistance: < 0 and > -100 } w
                    && (w.Kinds & WeaponKinds.Unarmed) != 0;
                _techniques[t] = reads ? _asWritten[t] with { AttackDistance = -_asWritten[t].AttackDistance } : _asWritten[t];
            }
        }
    }

    /// <summary>
    /// The buildings' collision that stops bolts and lines of fire (the ragdolls' environment, <see cref="SolidHits"/>); null: the terrain only.
    /// </summary>
    public Ragdolls.IRagdollEnvironment? Buildings { get; set; }

    /// <summary>
    /// The live relation table (<c>PopulationData.Relations</c>) that says who is an enemy; null: everyone of another faction is (hand-built
    /// test worlds).
    /// </summary>
    public FactionRelations? Relations { get; set; }

    /// <summary>
    /// The relation events of the landed blows (<see cref="FactionDeeds.Blow"/>); null: blows change no relation (hand-built test worlds).
    /// </summary>
    public FactionDeeds? Deeds { get; set; }

    /// <summary>
    /// The crime system the player's assaults and hits on the police are reported to (null: no crimes).
    /// </summary>
    public Crime.CrimeSystem? Crimes { get; set; }

    /// <summary>
    /// Picks the hit reaction clip of a hit (set by the animation system, which owns the clips): the defender, the hit part's LOCATIONAL_DAMAGE id,
    /// big stumble, the side (0 front, 1 rear) and a roll in [0, 1); returns the clip (an animation library index) and its length in seconds, or null
    /// when none fits. Null: hits play no clip.
    /// </summary>
    public Func<CharacterCold, string, bool, int, float, (int Clip, float Seconds)?>? StumbleChooser { get; set; }

    /// <summary>
    /// How long the get-up clip plays (the animation system sets it from <c>standing up 3</c>: its length to <c>GetUpEnd</c> over the record's play
    /// speed); 0: no get-up. A ragdoll's bone blend comes before it.
    /// </summary>
    public float GetUpClipSeconds { get; set; }

    /// <summary>
    /// The outdoor sight a fighter senses a current target within (before the race's multiplier; <see cref="AggroSettings.SightRange"/>'s default).
    /// </summary>
    public float SenseRange { get; set; } = AggroSystem.OutdoorSight;

    /// <summary>
    /// The techniques the fights choose from.
    /// </summary>
    public IReadOnlyList<CombatTechnique> Techniques => _techniques;

    /// <summary>
    /// The resolved blows so far (when <see cref="CombatOptions.RecordLog"/> is on).
    /// </summary>
    public IReadOnlyList<CombatLogEntry> Log => _log;

    #endregion

    #region State

    /// <summary>
    /// Whom the character in <paramref name="slot"/> (of <paramref name="generation"/>) fights as of the last tick (slot -1: nobody); a cheap read of
    /// <see cref="StateOf"/>, any thread after Schedule.
    /// </summary>
    public (int Slot, int Generation) TargetOf(int slot, int generation) =>
        (uint)slot < (uint)_read.Length && _read[slot].Generation == generation
            ? (_read[slot].TargetSlot, _read[slot].TargetGeneration)
            : (-1, 0);

    /// <summary>
    /// The combat state of a character as of the last finished tick (default when it has none).
    /// </summary>
    public CombatSlot StateOf(CharacterId id) =>
        id.Slot >= 0 && id.Slot < _read.Length && _read[id.Slot].Generation == id.Generation ? _read[id.Slot] : CombatSlot.Fresh(id.Generation);

    /// <summary>
    /// The technique a character is playing and its progress (0 to 1), for the animation layer; null when it is not attacking or reacting.
    /// </summary>
    public (CombatTechnique Technique, float Progress, bool IsAttack)? Playing(CharacterId id, long tick)
    {
        var s = StateOf(id);
        if (s.AttackTech >= 0 && s.AttackTicks > 0)
            return (_techniques[s.AttackTech], Math.Clamp((tick - s.AttackStart) / (float)s.AttackTicks, 0, 1), true);

        if (s.ReactTech >= 0 && s.ReactTicks > 0)
            return (_techniques[s.ReactTech], Math.Clamp((tick - s.ReactStart) / (float)s.ReactTicks, 0, 1), false);

        return null;
    }

    /// <summary>
    /// Adds every slot's fight state and zone hits, the log's length, the bolts and the turret manning to <paramref name="hasher"/>.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        hasher.Add(_read.Length);
        for (int i = 0; i < _read.Length; i++)
        {
            _read[i].Hash(ref hasher);
            _zones.Hash(ref hasher, i);
        }

        hasher.Add(_log.Count);
        _projectiles.Hash(ref hasher);
        _turretManning.Hash(ref hasher);
    }

    /// <summary>
    /// Grows the state arrays to the table and gives every slot whose character changed (a new generation, or gone) a fresh state.
    /// </summary>
    void Sync(CharacterTable table)
    {
        int n = table.HighWater;
        if (_read.Length < n) GrowStateArrays(n);
        if (_inFight.Length < _read.Length) _inFight = new bool[_read.Length];

        var next = table.Next;
        for (int i = 0; i < n; i++)
        {
            int generation = next[i].Alive ? next[i].Generation : -1;
            if (_read[i].Generation == generation) continue;

            _read[i] = CombatSlot.Fresh(generation);
            _zones.Clear(i);
        }
    }

    /// <summary>
    /// Grows both state arrays (and the zones) to <paramref name="n"/> slots at least, doubling; the new slots are fresh and empty.
    /// </summary>
    void GrowStateArrays(int n)
    {
        int size = Math.Max(n, _read.Length * 2);
        int old = _read.Length;
        Array.Resize(ref _read, size);
        Array.Resize(ref _write, size);
        for (int i = old; i < size; i++)
        {
            _read[i] = CombatSlot.Fresh(-1);
            _write[i] = CombatSlot.Fresh(-1);
        }

        _zones.Resize(size);
    }

    /// <summary>
    /// The combat state as of now in a serial phase: <c>read</c> in the inputs (and between ticks) and after this tick's swap, <c>write</c> from the
    /// schedule until the swap.
    /// </summary>
    CombatSlot[] Live(World world) =>
        world.Characters.Phase is TablePhase.Inputs or TablePhase.Outside
            || world.Characters.Phase == TablePhase.SlowWorld && _swappedTick == world.Tick
            ? _read
            : _write;

    #endregion

    #region Orders and inputs

    /// <summary>
    /// Takes the fight orders: attacks, shoot-at orders, stops, and the moves and follows that replace a fight.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        Sync(table);
        int now = (int)world.Tick;
        foreach (var command in commands)
        {
            switch (command)
            {
                case AttackOrder order:
                    AttackOrderInput(world, table, order, now);
                    break;
                case ShootAtOrder shoot:
                    ShootAtInput(world, table, shoot);
                    break;
                case StopCommand stop:
                    foreach (var id in stop.Characters) Disengage(table, id);
                    break;

                // A move or follow order replaces the fight (the original's new task drops the fight task, and a new movement goal clears the
                // stance): the runner neither swings at a chaser that comes into reach nor defends; its weapon stays out until it idles
                // (docs/game/combat.md "Fight lifecycle and orders").
                case MoveOrder move:
                    foreach (var id in move.Characters.Count > 0 ? move.Characters : world.Player.Selection.ToArray()) LeaveFight(table, id);
                    break;
                case FollowOrder follow:
                    foreach (var id in follow.Characters.Count > 0 ? follow.Characters : world.Player.Selection.ToArray())
                        if (id != follow.Target) LeaveFight(table, id);
                    break;
            }
        }
    }

    /// <summary>
    /// The attack order's input: each attacker either queues the player's pick or starts its fight at once, and an assault is booked for the ones
    /// that fight.
    /// </summary>
    void AttackOrderInput(World world, CharacterTable table, AttackOrder order, int now)
    {
        // The order itself changes no relation (the original's relation events come from the blows, Deeds); an attack that is not self-defence is an
        // assault. Whether the victim is in melee combat mode is read before the order engages anyone.
        bool victimFighting = VictimFighting(table, order.Target);
        foreach (var id in order.Attackers)
        {
            // The player's pick (row 5) is an order: it goes into the attacker's queue. Another fight (a reaction's, a goal's) starts at once.
            if (order.Picked)
            {
                if (table.TryResolveNext(id, out int a))
                {
                    var entry = new OrderEntry(order.Unprovoked ? TaskType.UnprovokedFocusedMeleeAttack : TaskType.FocusedMeleeAttack, this)
                    {
                        Target = order.Target,
                        Detail = new PickedAttack(order, victimFighting, world.Tick),
                    };
                    PlayerOrders.Give(world, a, entry, order.Queued);
                }

                continue;
            }

            if (Engage(world, table, id, order, now)) Assaulted(world, table, id, order, victimFighting);
        }
    }

    /// <summary>
    /// The shoot-at order's input: the gunners that man a turret take the victim as their turret target.
    /// </summary>
    void ShootAtInput(World world, CharacterTable table, ShootAtOrder shoot)
    {
        foreach (var id in shoot.Gunners.Count > 0 ? shoot.Gunners : world.Player.Selection.ToArray())
            if (table.TryResolveNext(id, out int g) && table.Cold(g) is { IsPlayer: true, Gunning: not null } gunner
                && table.TryResolveNext(shoot.Target, out int victim) && victim != g && table.Cold(victim) is { Medical.Dead: false })
                gunner.TurretTarget = shoot.Target;
    }

    /// <summary>
    /// Starts the fight of <paramref name="attacker"/> on the order's target through <see cref="TaskStart.Start"/>: row 5 (a target the player
    /// picked: the order), 13 Attacking attackers of (a defence: a reaction) or 16 Attacking enemies (a goal's fight; an NPC's engine fight,
    /// <see cref="AttackOrder.Source"/>). False when it cannot fight.
    /// </summary>
    bool Engage(World world, CharacterTable table, CharacterId attacker, AttackOrder order, int now)
    {
        var target = order.Target;
        if (!table.TryResolveNext(attacker, out int a) || !table.TryResolveNext(target, out int t) || a == t) return false;

        var cold = table.Cold(a)!;
        var tc = table.Cold(t)!;
        if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null || tc.Medical is null) return false;
        if (cold.Medical.Incapacitated || tc.Medical.Incapacitated) return false;

        // a carried mate does not fight (a prisoner neither: rows 5, 13 and 16 are not registered for the imprisoned; it may still be attacked,
        // engine choice)
        if (!cold.CarriedBy.IsNone) return false;

        // A fighter turns on no one it found no path to lately unless the player picked it (isPathImpossible in every finder's score,
        // docs/game/combat.md "Target score"): a mate that is called in on a sighting or an attack and cannot reach the enemy (a cage, a locked room)
        // would otherwise stand at the nearest spot for ever.
        if (!order.Picked && PursuitSystem.PathImpossible(cold, target, now)) return false;

        var (row, source) = order.Picked
            ? (order.Unprovoked ? TaskType.UnprovokedFocusedMeleeAttack : TaskType.FocusedMeleeAttack, TaskSource.Order)
            : order.Defensive
                ? (TaskType.AttackAttackersOf, TaskSource.Reaction)
                : (TaskType.AttackEnemies, TaskSource.Goal);
        var request = new TaskRequest(CharacterTask.Attack, row, order.Source ?? source) { Target = target, Job = order.Job };

        return TaskStart.Start(world, a, request) == StartResult.Started;
    }

    /// <summary>
    /// Whether the victim is in melee combat mode, read before an order engages anyone (an attack on a fighting character is a lesser assault).
    /// </summary>
    bool VictimFighting(CharacterTable table, CharacterId target) =>
        table.TryResolveNext(target, out int vs) && table.Cold(vs) is { } vc && IsInCombatMode(table, target, vc, melee: true, ranged: false);

    /// <summary>
    /// The order itself changes no relation (the original's relation events come from the blows, Deeds). Only Attack Unprovoked (row 61) is an
    /// assault at its start (<c>FUN_140334070</c>) and marks the attacker ordered whether or not the booking passes (<c>FUN_14033c1e0</c>); rows 5,
    /// 4, 13 and 16 do neither (<b>Verified</b>, flag <c>+0x98</c> of the task).
    /// </summary>
    void Assaulted(World world, CharacterTable table, CharacterId attacker, AttackOrder order, bool victimFighting)
    {
        if (!order.Unprovoked || order.Defensive) return;

        if (Crimes is { } crimes && table.TryResolveNext(order.Target, out int victimSlot))
            crimes.Assault(world, attacker.Slot, victimSlot, victimFighting);

        if (table.Cold(attacker.Slot) is { } cold) cold.Ordered = true;
    }

    /// <summary>
    /// The player's attack order in a queue: the command, and the victim's combat mode when it was given (read again when the order starts on a
    /// later tick).
    /// </summary>
    sealed record PickedAttack(AttackOrder Order, bool VictimFighting, long Tick);

    /// <summary>
    /// The save kind of an order: Saved: the pick is rebuilt from its attacker and target; the victim's combat mode is read when the order starts (a
    /// tick that is never the world's).
    /// </summary>
    int IOrderOwner.SaveKind(OrderEntry order) => order.Detail is PickedAttack ? 0 : -1;

    /// <inheritdoc/>
    object? IOrderOwner.LoadDetail(World world, CharacterId self, TaskType row, int kind, CharacterId target) =>
        new PickedAttack(new AttackOrder([self], target) { Picked = true, Unprovoked = row == TaskType.UnprovokedFocusedMeleeAttack,
            Tick = world.Tick }, false, -1);

    /// <summary>
    /// The attack order (row 5) needs a target that is there, awake and has a body (else it leaves the queue), and an attacker able to fight: a
    /// fighter, awake, not carried (<see cref="Engage"/>).
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int t) || t == slot || table.Cold(t)!.Medical is not { Incapacitated: false })
            return OrderCheck.Gone;

        var cold = table.Cold(slot)!;
        bool able = cold is { Fighter: not null, Stats: not null, Race: not null, Medical.Incapacitated: false } && cold.CarriedBy.IsNone;

        return able ? OrderCheck.Run : OrderCheck.Blocked();
    }

    /// <inheritdoc/>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var picked = (PickedAttack)order.Detail!;
        var id = table.IdOf(slot);
        bool victimFighting = picked.Tick == world.Tick ? picked.VictimFighting : VictimFighting(table, order.Target);
        Sync(table);
        if (Engage(world, table, id, picked.Order, (int)world.Tick)) Assaulted(world, table, id, picked.Order, victimFighting);
    }

    #endregion

    #region Hit reactions

    /// <summary>
    /// <c>AI::underMeleeAttack</c> on <paramref name="t"/> getting up with its fight off (targeted by a melee attacker, or hit by a blow;
    /// <paramref name="counter"/> tells the attacks of one tick apart): a <see cref="KnockDownChance"/> roll knocks it out for
    /// <see cref="KnockDownSeconds"/> (its KO timer set). A shooter's target and a bolt go through the shot handler instead, which has no roll.
    /// Serial phases.
    /// </summary>
    internal void UnderAttackGettingUp(World world, int t, CharacterCold tc, ulong counter)
    {
        if (tc.InCombat || !CharacterFacts.GettingUp(tc, world.Tick) || tc.Medical is not { Incapacitated: false } medical) return;

        float roll = CombatRolls.Float(world.Seed, Rng.Key(world.Characters.IdOf(t)), CombatRoll.KnockDown, ((ulong)world.Tick << 32) ^ counter);
        if (roll >= KnockDownChance) return;

        medical.KnockOut(KnockDownSeconds);
        tc.GettingUpUntil = 0;
    }

    /// <summary>
    /// The hit path of <c>AI::underMeleeAttack</c> (a landed blow, <c>FUN_1404394f0</c>; a bolt, <c>FUN_140999d50</c>): an NPC hit with its fight
    /// off, not getting up, has its AI think at the next tick (<see cref="Ai.AiMind.Replan"/>), where its Self preservation weighs the attacker the
    /// hit made a threat (<see cref="AggroSystem.Force"/>). Serial phases.
    /// </summary>
    static void HitReplan(CharacterCold dc, long tick)
    {
        if (!dc.InCombat && dc.Ai is { } mind && !CharacterFacts.GettingUp(dc, tick)) mind.Replan = true;
    }

    #endregion

    #region Fight task

    /// <summary>
    /// Registers this system as the handler of the attack task.
    /// </summary>
    public void RegisterTasks(TaskHandlers tasks) => tasks.Register(CharacterTask.Attack, this);

    /// <summary>
    /// The fight's start (<c>initCombatMode</c>): the target set, kept by a player's pick (row 5 or 61), the weapon drawn.
    /// </summary>
    void ITaskHandler.Start(World world, int a, in TaskRequest request)
    {
        var table = world.Characters;
        var cold = table.Cold(a)!;
        var target = request.Target;
        int t = target.Slot, now = (int)world.Tick;
        bool picked = request.Row is TaskType.FocusedMeleeAttack or TaskType.UnprovokedFocusedMeleeAttack;
        ref var s = ref _read[a];
        if (s.TargetSlot != t || s.TargetGeneration != target.Generation)
        {
            s.Engaged = false;
            s.LastTech = -1;
        }

        s.TargetSlot = t;
        s.TargetGeneration = target.Generation;
        (s.TaskSlot, s.TaskGeneration) = (t, target.Generation);
        s.Picked = picked;
        s.PickTick = now + PickTicks(world, cold);
        s.ReadyTick = now;   // no wait before the first swing (startupState enters the attack at once)

        // The fight's first path is asked for at once: the wait an idle or wandering character was in (3 to 10 s) is not the pursuit's repath timer
        // (PursuitSystem), and left in it the fighter stood there until it ran out.
        if (table.Next[a].Task != (byte)CharacterTask.Attack) table.Next[a].TaskTime = 0;
        table.Next[a].Task = (byte)CharacterTask.Attack;
        cold.InCombat = true;
        cold.DrawnWeapon = s.Ranged ? HandHold.OfCategory((int)WeaponCategory.Bow).Weapon : MeleeDrawn(cold.Fighter!);
    }

    /// <summary>
    /// The fight ends with any other task (a new task drops the fight task) and with the Stop order; nothing for a character not fighting.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var live = Live(world);
        if (world.Characters.Cold(slot)!.InCombat || slot < live.Length && live[slot].TargetSlot >= 0) Disengage(world.Characters, slot);
    }

    /// <summary>
    /// The end of the fight task (<c>Character::endCombatMode</c>), from any serial phase: the target and the swing dropped in both the last tick's
    /// and this tick's combat state (the one a phase reads differs), then <see cref="EndFight(CharacterTable, int)"/>.
    /// </summary>
    internal void Disengage(CharacterTable table, CharacterId id)
    {
        if (table.TryResolveNext(id, out int slot)) Disengage(table, slot);
    }

    /// <summary>
    /// Takes the character in <paramref name="slot"/> out of its fight: its target, technique and blow dropped in both state arrays (its engaged list
    /// ended), and its fight task ended.
    /// </summary>
    void Disengage(CharacterTable table, int slot)
    {
        if (slot < _read.Length)
        {
            if (table.Cold(slot)!.InCombat)
            {
                _read[slot].ListEpoch++;
                _write[slot].ListEpoch++;
            }

            Drop(ref _read[slot]);
            Drop(ref _write[slot]);
        }

        EndFight(table, slot);

        static void Drop(ref CombatSlot s)
        {
            s.TargetSlot = -1;
            s.TaskSlot = -1;
            s.AttackTech = -1;
            s.NextBlow = 0;
            s.Engaged = false;
            s.Picked = false;
        }
    }

    /// <summary>
    /// Takes a character out of its fight when it is in one or still has a target.
    /// </summary>
    void LeaveFight(CharacterTable table, CharacterId id)
    {
        if (table.TryResolveNext(id, out int slot) && (table.Cold(slot)!.InCombat || _read[slot].TargetSlot >= 0)) Disengage(table, slot);
    }

    /// <summary>
    /// The end of the fight task of <paramref name="slot"/> also zeroes its engaged list (<c>FUN_1406659b0</c> mode 2): everyone who let close in on
    /// it is out of it, by a new <see cref="CombatSlot.ListEpoch"/> the fighters read (<see cref="CombatSlot.JoinedEpoch"/>); they keep their target
    /// and ask again.
    /// </summary>
    static void EndFight(CharacterTable table, int slot, ref CombatSlot s)
    {
        var cold = table.Cold(slot)!;
        if (cold.InCombat) s.ListEpoch++;

        s.TaskSlot = -1;
        EndFight(table, slot);
    }

    /// <summary>
    /// Ends a character's fight task: its combat flags cleared, an NPC without a mind of its own sheathes, the attack task steps to idle, and it is
    /// decided again.
    /// </summary>
    static void EndFight(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        cold.Ordered = false;
        cold.InCombat = false;
        cold.CombatRunning = false;
        cold.RangedCombatMode = false;
        cold.TargetFleeingTime = 0;

        // The fight's end sheathes nothing (docs/game/combat.md "Fight lifecycle and orders", weapon away): the planner puts it away before a goal
        // that needs it away (the AI's UNEQUIP_WEAPON step), a player's character once it stops moving on an order (Act). A character without either
        // (no mind, or one whose routine is the engine's) sheathes now.
        if (!cold.IsPlayer && cold.Ai is null or { Engine: true }) cold.DrawnWeapon = WeaponKinds.None;

        // the fight's own end: idle, its provenance kept (a follow row's, FollowResume)
        if (table.Next[slot].Task == (byte)CharacterTask.Attack) TaskStart.Step(table, slot, CharacterTask.Idle);

        // the fight task ended: a player character is decided again (a follow row takes up its follow)
        DecisionSystem.Request(cold);

        // and an NPC's AI chooses again at the next tick (the original's "plan finished" trigger)
        if (cold.Ai is { } mind) mind.Replan = true;
    }

    #endregion

    #region Tick

    /// <summary>
    /// The serial schedule: the state arrays synced and copied, the bolts flown, the turrets ticked and the attackers indexed.
    /// </summary>
    public void Schedule(World world)
    {
        Sync(world.Characters);
        Array.Copy(_read, _write, world.Characters.HighWater);
        _zones.Copy(world.Characters.HighWater);
        _projectiles.Fly(world, this);
        TickTurrets(world);
        _index.Build(_read, world.Characters, world.Grid);
    }

    /// <summary>
    /// The move step (parallel): each fighter faces its target and, with <see cref="CombatOptions.SelfApproach"/>, walks up to its standing gap
    /// through the nav agent unless busy (down, swinging, defending, stunned or shooting).
    /// </summary>
    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        int now = (int)world.Tick;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || !world.Steps(i) || next[i].Task != (byte)CharacterTask.Attack) continue;

            ref readonly var s = ref _read[i];
            int t = s.TargetSlot;
            if (t < 0 || t >= prev.Length || !prev[t].Alive || prev[t].Generation != s.TargetGeneration) continue;

            var cold = table.Cold(i)!;
            var from = new Vector2(prev[i].Position.X, prev[i].Position.Z);
            var to = new Vector2(prev[t].Position.X, prev[t].Position.Z);
            var delta = to - from;
            float distance = delta.Length();
            if (distance < 1e-3f) continue;

            var dir = delta / distance;
            ref var n = ref next[i];

            // On a path to the target (the pursuit's) the movement system steers it: facing the target here would undo each tick's turn towards a
            // waypoint that lies well off the straight line, and a walker more than 90 degrees off its waypoint does not step (MovementSystem.Turn).
            if (!Settings.SelfApproach && (n.Flags & (ushort)MoveFlags.HasPath) != 0) continue;

            n.Yaw = MathF.Atan2(dir.X, dir.Y);
            if (!Settings.SelfApproach) continue;

            // A crossbowman stands while it aims or reloads and walks up only while closing in.
            bool shooting = s.Ranged && s.RangedState != (byte)RangedStates.Closing;
            bool busy = s.Down || s.AttackTech >= 0 || s.ReactTech >= 0 || now < s.StunUntil || shooting;
            float gap = distance - CombatTuning.Footprint(cold) - CombatTuning.Footprint(table.Cold(t));
            float stand = CombatTuning.StandGap(s.Reach, cold.Fighter?.WeaponLength ?? 0, cold.CombatRunning);
            if (busy || gap <= stand)
            {
                n.Velocity = default;
                continue;
            }

            float speed = n.MaxSpeed > 0 ? n.MaxSpeed : Settings.ApproachSpeed;
            if (!cold.CombatRunning) speed = MathF.Min(speed, CombatTuning.FootworkSpeed);

            // a far character's step covers the ticks since its last (the far cadence)
            float dts = world.StepSeconds(i);
            float step = MathF.Min(speed * dts, gap - stand);

            // Through the nav agent, as root motion: a wall stops the body's circle and it slides along it (docs/game/pathfinding.md "Walls").
            var moved = world.Walkability.Slide(prev[i].Position, dir * step, CombatTuning.Footprint(cold));
            float x = prev[i].Position.X + moved.X, z = prev[i].Position.Z + moved.Y;
            if (world.Walkability.IsWalkable(x, z, prev[i].Position.Y))
            {
                n.Position = new Vector3(x, world.Walkability.GroundHeight(x, z, prev[i].Position.Y), z);
                n.Velocity = new Vector3(moved.X / dts, 0, moved.Y / dts);
            }
            else
            {
                n.Velocity = default;
            }
        }
    }

    /// <summary>
    /// The medical context of a hit on the character in <paramref name="slot"/>: its toughness, strength, encumbrance and roll key.
    /// </summary>
    MedicalContext MakeContext(World world, int slot, CharacterCold cold) =>
        new(_constants, _options, cold.Race!)
        {
            Toughness = cold.Stats!.ScaledToughness,
            Strength = cold.Stats[StatsEnumerated.Strength],
            EncumbranceFactor = cold.Fighter?.EncumbranceFactor ?? 1,
            Seed = world.Seed,
            CharacterKey = Rng.Key(world.Characters.IdOf(slot)),
        };

    /// <summary>
    /// The serial slow world: the unprovoked attacks' assaults booked again, then the state arrays swapped.
    /// </summary>
    public void SlowWorld(World world)
    {
        RepeatAssaults(world);
        (_read, _write) = (_write, _read);
        _zones.Swap();
        _swappedTick = world.Tick;
    }

    /// <summary>
    /// Attack Unprovoked (row 61) books its Assault again on every update of the task (<c>FUN_14033c250</c> runs <c>FUN_140334070</c> each time,
    /// <b>Verified</b>), so the pending crime's timer is refreshed for as long as the attacker fights a victim that stays passive;
    /// <see cref="Assaulted"/> is the booking, with its own gates (the victim fighting, wanted, a bandit).
    /// </summary>
    void RepeatAssaults(World world)
    {
        if (Crimes is null) return;

        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { Running: { Task: CharacterTask.Attack, Row: TaskType.UnprovokedFocusedMeleeAttack } })
                continue;

            var fight = StateOf(table.IdOf(s));
            if (fight.TargetSlot < 0 || !table.TryResolveNext(new CharacterId(fight.TargetSlot, fight.TargetGeneration), out int victim)) continue;

            Crimes.Assault(world, s, victim, VictimFighting(table, table.IdOf(victim)));
        }
    }

    #endregion
}
