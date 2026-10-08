using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// Melee combat (docs/simulation.md "Combat as built (stage 8)", docs/game/combat.md). One system, three parts per tick:
/// <list type="bullet">
/// <item><b>Act</b> (parallel, per character, reading the last tick's <see cref="CombatSlot"/> array and the table's previous state, writing only its own): the medical tick; a
/// defender looks for blows coming at it and picks a reaction (block roll, wrong-direction block on a failure, dodge for fists); an attacker keeps its swing going and, when
/// the animation reaches a blow's <c>anim blocked frame</c>, emits a <see cref="BlowEffect"/> to the target; a free attacker in reach picks a technique and starts it.</item>
/// <item><b>Apply</b> (serial, in the commit's order: target, source, system, sequence): the outcome from the defender's reaction progress ("blocked", "dodged" or "hit", combat.md),
/// then for a hit the damage packet, part choice, armour, toughness and the medical hit, a stagger for a heavy hit, and XP for both sides.</item>
/// <item><b>Move</b>: faces the target and, with <see cref="CombatOptions.SelfApproach"/>, walks up to it.</item>
/// </list>
/// Everything random is a function of (seed, character, kind, counter) so the result does not depend on the thread count. Stats, bodies and gear change only here in the commit and in
/// the owner's own Act (the medical tick); other characters only read the <see cref="CombatSlot"/> copy.
/// </summary>
public sealed partial class CombatSystem : ITickSystem, IStateHashed
{
    /// <summary>The <see cref="Effect.Kind"/> of a blow: <c>Arg</c> is the blow (1 or 2) and <c>Amount</c> the attacker's attack number.</summary>
    public const int BlowEffect = 1;

    const float HoursPerSecond = 11f / 1200f;

    readonly CombatTechnique[] techniques;
    readonly CombatConstants combat;
    readonly GameConstants constants;
    readonly BodyOptions options;
    readonly AnimationLengths lengths;
    readonly XpService xp;
    readonly List<CombatLogEntry> log = [];
    CombatSlot[] read = [];
    CombatSlot[] write = [];

    public CombatSystem(IEnumerable<CombatTechnique> techniques, CombatConstants combat, GameConstants constants, BodyOptions options, AnimationLengths lengths, CombatOptions? settings = null)
    {
        this.techniques = [.. techniques];
        this.combat = combat;
        this.constants = constants;
        this.options = options;
        this.lengths = lengths;
        Settings = settings ?? new CombatOptions();
        xp = new XpService(constants, options);
    }

    public CombatOptions Settings { get; }
    public IReadOnlyList<CombatTechnique> Techniques => techniques;

    /// <summary>The resolved blows so far (when <see cref="CombatOptions.RecordLog"/> is on).</summary>
    public IReadOnlyList<CombatLogEntry> Log => log;

    /// <summary>The combat state of a character as of the last finished tick (default when it has none).</summary>
    public CombatSlot StateOf(CharacterId id) => id.Slot >= 0 && id.Slot < read.Length && read[id.Slot].Generation == id.Generation ? read[id.Slot] : CombatSlot.Fresh(id.Generation);

    /// <summary>The technique a character is playing and its progress (0 to 1), for the animation layer; null when it is not attacking or reacting.</summary>
    public (CombatTechnique Technique, float Progress, bool IsAttack)? Playing(CharacterId id, long tick)
    {
        var s = StateOf(id);
        if (s.AttackTech >= 0 && s.AttackTicks > 0) return (techniques[s.AttackTech], Math.Clamp((tick - s.AttackStart) / (float)s.AttackTicks, 0, 1), true);
        if (s.ReactTech >= 0 && s.ReactTicks > 0) return (techniques[s.ReactTech], Math.Clamp((tick - s.ReactStart) / (float)s.ReactTicks, 0, 1), false);
        return null;
    }

    // ------------------------------------------------------------------ state arrays

    void Sync(CharacterTable table)
    {
        int n = table.HighWater;
        if (read.Length < n)
        {
            int size = Math.Max(n, read.Length * 2);
            int old = read.Length;
            Array.Resize(ref read, size);
            Array.Resize(ref write, size);
            for (int i = old; i < size; i++) { read[i] = CombatSlot.Fresh(-1); write[i] = CombatSlot.Fresh(-1); }
        }
        var next = table.Next;
        for (int i = 0; i < n; i++)
        {
            int generation = next[i].Alive ? next[i].Generation : -1;
            if (read[i].Generation != generation) read[i] = CombatSlot.Fresh(generation);
        }
    }

    public void Hash(ref StateHasher hasher)
    {
        hasher.Add(read.Length);
        for (int i = 0; i < read.Length; i++) read[i].Hash(ref hasher);
        hasher.Add(log.Count);
    }

    // ------------------------------------------------------------------ inputs

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
                    foreach (var id in order.Attackers) Engage(world, table, id, order.Target, now);
                    break;
                case StopCommand stop:
                    foreach (var id in stop.Characters) Disengage(table, id);
                    break;
            }
        }
    }

    void Engage(World world, CharacterTable table, CharacterId attacker, CharacterId target, int now)
    {
        if (!table.TryResolveNext(attacker, out int a) || !table.TryResolveNext(target, out int t) || a == t) return;
        var cold = table.Cold(a)!;
        var tc = table.Cold(t)!;
        if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null || tc.Medical is null || cold.Medical.Incapacitated || tc.Medical.Incapacitated) return;
        ref var s = ref read[a];
        s.TargetSlot = t;
        s.TargetGeneration = target.Generation;
        s.ReadyTick = now + PauseTicks(world, a, 0);
        table.Next[a].Task = (byte)CharacterTask.Attack;
        cold.InCombat = true;
        cold.DrawnWeapon = cold.Fighter.Weapon is { } w && !cold.Fighter.Unarmed ? HandHold.OfCategory(w.Data.SkillCategory).Weapon : WeaponKinds.None;
    }

    void Disengage(CharacterTable table, CharacterId id)
    {
        if (!table.TryResolveNext(id, out int slot)) return;
        ref var s = ref read[slot];
        s.TargetSlot = -1;
        s.AttackTech = -1;
        s.NextBlow = 0;
        EndFight(table, slot);
    }

    static void EndFight(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        cold.InCombat = false;
        cold.DrawnWeapon = WeaponKinds.None;
        if (table.Next[slot].Task == (byte)CharacterTask.Attack) table.Next[slot].Task = (byte)CharacterTask.Idle;
    }

    // ------------------------------------------------------------------ schedule, move

    public void Schedule(World world)
    {
        Sync(world.Characters);
        Array.Copy(read, write, world.Characters.HighWater);
    }

    public void Move(World world, Partition part)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        int now = (int)world.Tick;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || next[i].Task != (byte)CharacterTask.Attack) continue;
            ref readonly var s = ref read[i];
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
            n.Yaw = MathF.Atan2(dir.X, dir.Y);
            if (!Settings.SelfApproach) continue;
            bool busy = s.Down || s.AttackTech >= 0 || s.ReactTech >= 0 || now < s.StunUntil;
            float gap = distance - Radius(cold) - Radius(table.Cold(t)!);
            if (busy || gap <= CombatTuning.CloseInGap)
            {
                n.Velocity = default;
                continue;
            }
            float speed = n.MaxSpeed > 0 ? n.MaxSpeed : Settings.ApproachSpeed;
            float step = MathF.Min(speed * dt, gap - CombatTuning.CloseInGap);
            float x = prev[i].Position.X + dir.X * step, z = prev[i].Position.Z + dir.Y * step;
            if (world.Walkability.IsWalkable(x, z))
            {
                n.Position = new Vector3(x, world.Walkability.GroundHeight(x, z), z);
                n.Velocity = new Vector3(dir.X * step / dt, 0, dir.Y * step / dt);
            }
            else n.Velocity = default;
        }
    }

    static float Radius(CharacterCold c) => c.Race?.PathfindFootprintRadius ?? 4;

    MedicalContext MakeContext(World world, int slot, CharacterCold cold) =>
        new(constants, options, cold.Race!)
        {
            Toughness = cold.Stats![StatsEnumerated.Toughness],
            Strength = cold.Stats[StatsEnumerated.Strength],
            EncumbranceFactor = cold.Fighter?.EncumbranceFactor ?? 1,
            Seed = world.Seed,
            CharacterKey = Rng.Key(world.Characters.IdOf(slot)),
        };

    // ------------------------------------------------------------------ end of tick

    public void SlowWorld(World world) => (read, write) = (write, read);
}
