using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>Orders the characters to fight <see cref="Target"/> (the AI's "attack" task, a player's attack command): they draw their weapon, close in and attack until one of them is down.</summary>
public sealed record AttackOrder(IReadOnlyList<CharacterId> Attackers, CharacterId Target) : SimCommand;

/// <summary>What a resolved blow did, for the log (a combat log, damage numbers, tests).</summary>
public readonly record struct CombatLogEntry(long Tick, int Attacker, int Defender, int Blow, BlowOutcome Outcome, int Part, float Cut, float Blunt, float Pierce, float Stun, bool Knockout, bool Death);

/// <summary>Settings of the <see cref="CombatSystem"/> that are the host's choice rather than the game's.</summary>
public sealed record CombatOptions
{
    /// <summary>The system closes the distance to the target itself, in a straight line (no navmesh). Off when a movement system does it.</summary>
    public bool SelfApproach { get; init; } = true;
    /// <summary>The system runs the medical tick (bleeding, healing, waking) of the characters that have a body. Off when a needs system does it.</summary>
    public bool TickMedical { get; init; } = true;
    /// <summary>Keep a <see cref="CombatSystem.Log"/> of every resolved blow.</summary>
    public bool RecordLog { get; init; }
    /// <summary>The speed (units per second) a character closes in at when it has no speed stat set.</summary>
    public float ApproachSpeed { get; init; } = 30;
}

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
public sealed class CombatSystem : ITickSystem, IStateHashed
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

    // ------------------------------------------------------------------ act

    static float Eff(CharacterCold c, StatsEnumerated stat) => c.Stats![stat] * c.Medical!.StatMultiplier(stat);

    int PauseTicks(World world, int slot, int counter)
    {
        float u = CombatRolls.Float(world.Seed, Rng.Key(world.Characters.IdOf(slot)), CombatRoll.Pause, (ulong)(uint)counter ^ ((ulong)(uint)world.Tick << 20));
        return Math.Max(1, (int)MathF.Ceiling((CombatTuning.PauseMin + u * (CombatTuning.PauseMax - CombatTuning.PauseMin)) / world.TickSeconds));
    }

    int Ticks(CombatTechnique t, float combatSpeed, float dt)
    {
        float clip = lengths.Of(t.AnimName);
        if (clip <= 0) clip = CombatTuning.DefaultClipSeconds;
        return Math.Max(2, (int)MathF.Ceiling(clip / (MathF.Max(t.AnimSpeedMult, 0.1f) * MathF.Max(combatSpeed, 0.1f)) / dt));
    }

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        int now = (int)world.Tick;
        float dt = world.TickSeconds;
        List<MedicalEvent>? events = null;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.Fighter is null || cold.Stats is null || cold.Medical is null || cold.Race is null) continue;
            ref var s = ref write[i];
            ulong key = Rng.Key(table.IdOf(i));

            // 1. The body: bleeding, healing, waking.
            if (Settings.TickMedical)
            {
                events ??= [];
                events.Clear();
                cold.Medical.Tick(dt * HoursPerSecond, MakeContext(world, i, cold), events);
            }
            s.Down = cold.Medical.Incapacitated;
            if (s.Down)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                s.ReactTech = -1;
                s.TargetSlot = -1;
                if (cold.InCombat) EndFight(table, i);
                continue;
            }

            // 2. Helpless after a heavy hit.
            if (now < s.StunUntil)
            {
                s.AttackTech = -1;
                s.NextBlow = 0;
                s.ReactTech = -1;
                continue;
            }

            // 3. The target.
            bool fighting = s.TargetSlot >= 0;
            if (fighting && !(s.TargetSlot < prev.Length && prev[s.TargetSlot].Alive && prev[s.TargetSlot].Generation == s.TargetGeneration && !read[s.TargetSlot].Down))
            {
                fighting = false;
                s.TargetSlot = -1;
                s.AttackTech = -1;
                s.NextBlow = 0;
                EndFight(table, i);
            }

            // 4. The reaction and the attack in progress.
            if (s.ReactTech >= 0 && now >= s.ReactStart + s.ReactTicks) s.ReactTech = -1;
            if (s.AttackTech >= 0)
            {
                var tech = techniques[s.AttackTech];
                if (s.NextBlow > 0 && fighting && now >= s.AttackStart + (int)MathF.Ceiling(tech.StrikeProgress(s.NextBlow) * s.AttackTicks))
                {
                    effects.Emit(i, s.TargetSlot, BlowEffect, s.AttackSeq, default, s.NextBlow);
                    s.Thrown++;
                    s.NextBlow = s.NextBlow < tech.Blows ? s.NextBlow + 1 : 0;
                }
                if (now >= s.AttackEnd)
                {
                    s.AttackTech = -1;
                    s.NextBlow = 0;
                    s.ReadyTick = now + PauseTicks(world, i, s.AttackSeq);
                }
            }

            // 5. Reacting comes before attacking: a free character looks for blows coming at it.
            if (s.ReactTech < 0 && s.AttackTech < 0) React(world, i, ref s, cold, key, now, dt);

            // 6. A free attacker in reach starts a technique.
            if (fighting && s.AttackTech < 0 && s.ReactTech < 0 && now >= s.ReadyTick) StartAttack(world, i, ref s, cold, key, now, dt);
        }
    }

    void StartAttack(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;
        int t = s.TargetSlot;
        var fighter = cold.Fighter!;
        var tc = table.Cold(t)!;
        float gap = Vector2.Distance(new(prev[i].Position.X, prev[i].Position.Z), new(prev[t].Position.X, prev[t].Position.Z)) - Radius(cold) - Radius(tc);
        bool moving = new Vector2(prev[t].Velocity.X, prev[t].Velocity.Z).Length() >= 1;
        float weaponReach = fighter.Unarmed ? 99 : fighter.Weapon!.Data.Length * 0.5f;
        float skill = Eff(cold, fighter.WeaponSkill);
        float roll = CombatRolls.Float(world.Seed, key, CombatRoll.Technique, (ulong)(s.AttackSeq + 1));
        int pick = TechniqueChooser.ChooseAttack(techniques, fighter.Kind, 0, false, skill, gap, moving, weaponReach, roll);
        if (pick < 0) return;
        var tech = techniques[pick];
        s.AttackSeq++;
        s.AttackTech = pick;
        s.AttackStart = now;
        s.AttackTicks = Ticks(tech, fighter.CombatSpeed, dt);
        s.AttackEnd = now + Math.Max(1, (int)MathF.Ceiling(tech.EndProgress * s.AttackTicks));
        s.NextBlow = 1;
        s.AttackReach = TechniqueChooser.ReachOf(tech, moving, weaponReach);
        s.AttackSkill = Eff(cold, fighter.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack) + (fighter.Unarmed ? fighter.UnarmedBonus : fighter.AttackBonus + (fighter.Weapon?.Stats.AttackMod ?? 0));
    }

    struct Scan
    {
        public CombatSlot[] Read;
        public CombatTechnique[] Techniques;
        public int Self, SelfGeneration, Now;
        public CombatSlot Me;
        public int Attacker, Blow, Strike;
    }

    void React(World world, int i, ref CombatSlot s, CharacterCold cold, ulong key, int now, float dt)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var scan = new Scan { Read = read, Techniques = techniques, Self = i, SelfGeneration = prev[i].Generation, Now = now, Me = s, Attacker = -1, Strike = int.MaxValue };
        world.Grid.Query(prev[i].Position.X, prev[i].Position.Z, CombatTuning.ReactionScanRadius, ref scan, static (int other, ref Scan st) =>
        {
            if (other == st.Self) return;
            ref readonly var a = ref st.Read[other];
            if (a.AttackTech < 0 || a.NextBlow == 0 || a.TargetSlot != st.Self || a.TargetGeneration != st.SelfGeneration) return;
            var tech = st.Techniques[a.AttackTech];
            int strike = a.AttackStart + (int)MathF.Ceiling(tech.StrikeProgress(a.NextBlow) * a.AttackTicks);
            if (strike <= st.Now) return;
            if (st.Me.ReactedSlot == other && st.Me.ReactedSeq == a.AttackSeq && st.Me.ReactedBlow == a.NextBlow) return;
            if (strike < st.Strike) { st.Strike = strike; st.Attacker = other; st.Blow = a.NextBlow; }
        });
        if (scan.Attacker < 0) return;

        var attack = read[scan.Attacker];
        var atkTech = techniques[attack.AttackTech];
        int direction = atkTech.DirectionOf(scan.Blow);
        s.ReactedSlot = scan.Attacker;
        s.ReactedSeq = attack.AttackSeq;
        s.ReactedBlow = scan.Blow;

        var fighter = cold.Fighter!;
        float meleeDefence = Eff(cold, StatsEnumerated.MeleeDefence);
        ulong counter = CombatRolls.BlowCounter(scan.Attacker, attack.AttackSeq, scan.Blow, 0);
        float chanceRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Block, counter);
        float pickRoll = CombatRolls.Float(world.Seed, key, CombatRoll.Reaction, counter);
        float block, dodge = 0;
        if (fighter.CanBlock)
        {
            float d = DefenceFormulas.Defence(meleeDefence, fighter.Weapon!.Stats.DefenceMod, true, fighter.DefenceBonus, fighter.Guarding);
            block = DefenceFormulas.BlockChance(d, attack.AttackSkill, combat);
        }
        else
        {
            block = 0;
            float effective = DefenceFormulas.EffectiveDefence(Eff(cold, StatsEnumerated.MartialArts), fighter.EncumbranceFactor, LegFactor(cold.Medical!), fighter.DefenceBonus, fighter.Guarding);
            dodge = DefenceFormulas.BlockChance(effective, attack.AttackSkill, combat, 0, 95);
        }
        int pick = TechniqueChooser.ChooseReaction(techniques, fighter.Kind, 0, false, fighter.CanBlock, direction, block, dodge, chanceRoll, pickRoll);
        if (pick < 0) return;
        var reaction = techniques[pick];
        int ticks = Ticks(reaction, fighter.CombatSpeed, dt);
        // The reaction is timed so that it is at its "blocked frame" when the blow arrives; with less lead time it starts at once and is less far along.
        int strike = scan.Strike;
        int start = Math.Max(now, strike - (int)MathF.Round(reaction.StrikeProgress(1) * ticks));
        s.ReactTech = pick;
        s.ReactStart = start;
        s.ReactTicks = ticks;
    }

    /// <summary>The worse leg's health fraction x 1.8 clamped to [0, 1] (<c>FUN_140884970</c>); 1 for a body without legs.</summary>
    static float LegFactor(MedicalState medical)
    {
        float worst = 1;
        bool any = false;
        foreach (var p in medical.Parts)
        {
            if (p.Template.Type != BodyPartType.Leg) continue;
            any = true;
            worst = MathF.Min(worst, p.Fraction);
        }
        return any ? Math.Clamp(worst * 1.8f, 0, 1) : 1;
    }

    MedicalContext MakeContext(World world, int slot, CharacterCold cold) =>
        new(constants, options, cold.Race!)
        {
            Toughness = cold.Stats![StatsEnumerated.Toughness],
            Strength = cold.Stats[StatsEnumerated.Strength],
            EncumbranceFactor = cold.Fighter?.EncumbranceFactor ?? 1,
            Seed = world.Seed,
            CharacterKey = Rng.Key(world.Characters.IdOf(slot)),
        };

    // ------------------------------------------------------------------ commit

    public void Apply(World world, in Effect e)
    {
        if (e.Kind != BlowEffect) return;
        var table = world.Characters;
        int a = e.Source, d = e.Target, blow = e.Arg, seq = (int)e.Amount, now = (int)world.Tick;
        ref var A = ref write[a];
        ref var D = ref write[d];
        if (A.AttackSeq != seq || A.AttackTech < 0) return;
        var ac = table.Cold(a);
        var dc = table.Cold(d);
        if (ac is null || dc is null || ac.Fighter is null || dc.Fighter is null || ac.Stats is null || dc.Stats is null || ac.Medical is null || dc.Medical is null || ac.Race is null || dc.Race is null) return;
        var tech = techniques[A.AttackTech];
        var prev = table.Previous;
        int direction = tech.DirectionOf(blow);

        // Reach: the blow arrives only if the target is still within the reach the attack started with.
        float gap = Vector2.Distance(new(prev[a].Position.X, prev[a].Position.Z), new(prev[d].Position.X, prev[d].Position.Z)) - Radius(ac) - Radius(dc);
        BlowOutcome outcome;
        float progress = 0;
        if (gap > A.AttackReach + CombatTuning.ReachSlack) outcome = BlowOutcome.Missed;
        else
        {
            CombatTechnique? reaction = D.ReactTech >= 0 && !D.Down && now >= D.StunUntil ? techniques[D.ReactTech] : null;
            if (reaction is not null) progress = Math.Clamp((now - D.ReactStart) / (float)D.ReactTicks, 0, 1);
            outcome = HitOutcomes.Decide(reaction, progress, direction);
        }

        switch (outcome)
        {
            case BlowOutcome.Missed:
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
            case BlowOutcome.Blocked:
                A.Parried++;
                D.Blocks++;
                GainCombatXp(ac, dc, XpEvent.GlancingHit, XpEvent.Defended);
                Stop(ref A, tech, blow);
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
            case BlowOutcome.Dodged:
                A.Evaded++;
                D.Dodges++;
                GainCombatXp(ac, dc, XpEvent.GlancingHit, null);
                var dodgeStats = dc.Stats;
                xp.PerEvent(dodgeStats, dc.Race, StatsEnumerated.Dodge, 0.25f * xp.SkillDifferenceFactor(dodgeStats[StatsEnumerated.Dodge], Eff(ac, StatsEnumerated.MeleeAttack)));
                Record(now, a, d, blow, outcome, -1, default, false, false);
                return;
        }

        // A hit.
        var fighter = ac.Fighter;
        var target = dc.Fighter;
        float toughness = Eff(dc, StatsEnumerated.Toughness);
        TargetKind kind = dc.Race.IsRobot ? TargetKind.Robot : target.IsAnimal ? TargetKind.Animal : TargetKind.Human;
        float skill = Eff(ac, fighter.WeaponSkill);
        float dex = Eff(ac, StatsEnumerated.Dexterity) * fighter.DexterityMult;
        float str = Eff(ac, StatsEnumerated.Strength);
        var packet = fighter.Unarmed
            ? DamageFormulas.UnarmedPacket(skill, Eff(ac, StatsEnumerated.Toughness), str, fighter.Weapon, kind, dc.Race.StringId, fighter.DamageOutput, combat)
            : DamageFormulas.Packet(skill, dex, str, fighter.Weapon!, kind, dc.Race.StringId, fighter.DamageOutput, combat);

        var ctx = MakeContext(world, d, dc);
        var events = new List<MedicalEvent>();
        ulong defenderKey = Rng.Key(table.IdOf(d));
        var armour = target.Armour;
        var armourSpan = armour is ArmourPiece[] array ? array.AsSpan() : [.. armour];
        var (part, damage) = HitResolver.Land(dc.Medical, ctx, packet, armourSpan,
            piece => CombatRolls.Float(world.Seed, defenderKey, CombatRoll.Cover, CombatRolls.BlowCounter(a, seq, blow, piece)),
            toughness, tech.LowStrike, direction == 6, combat, events);

        A.Landed++;
        D.Taken++;
        bool knockout = false, death = false;
        foreach (var ev in events)
        {
            switch (ev.Kind)
            {
                case MedicalEventKind.KnockedOut: knockout = true; break;
                case MedicalEventKind.Died: death = true; break;
                case MedicalEventKind.PartSevered: xp.ToughnessFromLimbLoss(dc.Stats, dc.Race); break;
            }
        }
        if (dc.Medical.Incapacitated)
        {
            D.Down = true;
            D.AttackTech = -1;
            D.NextBlow = 0;
            D.ReactTech = -1;
        }
        else if (damage.Cut + damage.Blunt + damage.Pierce + damage.ExtraStun > DamageFormulas.StumbleThreshold(toughness, combat))
        {
            // A heavy hit ("Heavy_Hit" instead of "Light_Hit"): the target is helpless for a moment. It takes effect at the next Act, so the blows already thrown this tick still land.
            D.StunUntil = Math.Max(D.StunUntil, now + (int)MathF.Ceiling(CombatTuning.HeavyHitStun / world.TickSeconds));
        }
        GainCombatXp(ac, dc, XpEvent.HitDealt, XpEvent.HitTaken);
        Record(now, a, d, blow, outcome, part, damage, knockout, death);
    }

    static void Stop(ref CombatSlot attacker, CombatTechnique tech, int blow)
    {
        // A blocked blow stops the swing at its "stop frame": the rest of the combo is not thrown.
        float stop = tech.StopProgress(blow);
        if (stop < 1) attacker.AttackEnd = Math.Min(attacker.AttackEnd, attacker.AttackStart + (int)MathF.Ceiling(stop * attacker.AttackTicks));
        attacker.NextBlow = 0;
    }

    void GainCombatXp(CharacterCold attacker, CharacterCold defender, XpEvent attackerEvent, XpEvent? defenderEvent)
    {
        var af = attacker.Fighter!;
        var df = defender.Fighter!;
        float aStrength = StrengthFactor(attacker, af);
        xp.Combat(attacker.Stats!, attacker.Race!, attackerEvent, af.Unarmed, af.Unarmed ? StatsEnumerated.None : af.Weapon!.Skill, Eff(defender, StatsEnumerated.MeleeDefence), aStrength);
        if (defenderEvent is { } ev)
        {
            float dStrength = StrengthFactor(defender, df);
            var weaponStat = df.Unarmed || ev == XpEvent.HitTaken ? StatsEnumerated.None : df.Weapon!.Skill;
            xp.Combat(defender.Stats!, defender.Race!, ev, df.Unarmed, weaponStat, Eff(attacker, af.Unarmed ? StatsEnumerated.MartialArts : StatsEnumerated.MeleeAttack), dStrength);
        }
    }

    float StrengthFactor(CharacterCold c, Fighter f) => f.Unarmed
        ? 1 - f.EncumbranceFactor
        : xp.WeaponWeightStrengthFactor(f.Weapon!.Stats.Weight, c.Stats![StatsEnumerated.Strength], c.Medical!.StatMultiplier(StatsEnumerated.Strength));

    void Record(int now, int a, int d, int blow, BlowOutcome outcome, int part, in HitDamage damage, bool knockout, bool death)
    {
        if (Settings.RecordLog) log.Add(new CombatLogEntry(now, a, d, blow, outcome, part, damage.Cut, damage.Blunt, damage.Pierce, damage.ExtraStun, knockout, death));
    }

    // ------------------------------------------------------------------ end of tick

    public void SlowWorld(World world) => (read, write) = (write, read);
}
