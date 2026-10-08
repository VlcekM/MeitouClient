using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
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
}
