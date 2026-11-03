using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The slavers' dealings with the downed and with their slaves at work (docs/game/ai-tasks.md "Prisoner fate and slave rows" and "Slave work, shout and capture"; docs/simulation.md "Captives"):
/// <list type="bullet">
/// <item><b>Capture</b> (167 "Capturing slave", finder <c>FUN_1408cdd00</c>, <b>Observed</b>): the nearest downed (<see cref="Down"/>: a crawler out of combat mode too), uncarried, uncaged character
/// in sight that is no animal, not of the capturer's faction or an ally (<b>engine reading</b> of the sensed-class masks), no slave already (slave state 1), of NPC class other than law
/// enforcement, trader and diplomat, not maimed, and not in shackles (engine guard). The
/// capturer walks to it, strips its weapons (the <c>hip</c> and <c>back</c> sections go to the capturer, after 0.8 s of <c>medic</c>, <c>Task_StripWeapons</c>), cuffs it
/// (<c>Task_CuffTarget</c>: a "Restraining" bar, <c>medic</c>, event 37 to the captive when it is full; the shackles are an <b>engine choice</b>, their source in the original is <b>Unknown</b>)
/// and carries it to a cage of its faction (goal 112 in the original, here <see cref="KidnapSystem.Carry"/>).</item>
/// <item><b>Carry</b> (168 "Carrying slaves", finder <c>FUN_1408cdf50</c>): the same for a downed slave owned by the carrier's faction, with no strip or cuff.</item>
/// <item><b>Shout</b> (188 "Working the slaves", finder <c>FUN_1408cd940</c>): the worker says event 38 SHOUT_AT_SLAVE_WORKER to a random slave of its squad that is alive, uncarried and uncaged
/// (the row's TaskData event, <b>Verified</b>: the registration of 188 stores 0x26 at <c>+0x130</c>), then walks towards it until the line is over (<c>Task_TalktoPlayer</c> fires the event when
/// it starts and walks on to the target). A worker shouts again after <see cref="CaptiveSettings.ShoutCooldownSeconds"/> (engine pacing: the original scores the goal at 0.25 per slave
/// against the others).</item>
/// </list>
/// A job that gives up puts its target on the capturer's give-up list for <see cref="CaptiveSettings.GiveUpHours"/> (engine: the writer of the original's list is not traced).
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>The downed and cuffed or carried: the AI's give-up list entries and the shouters' pacing (hashed while any exist).</summary>
    readonly List<(CharacterId Who, double Until)> givenUp = [], shoutUntil = [];

    /// <summary>The kidnapping system that carries a captive to a cage once it is cuffed (null: captives are cuffed and left).</summary>
    public KidnapSystem? Kidnap { get; set; }

    /// <summary>Weapon sections <c>Task_StripWeapons</c> empties (<c>FUN_140747ff0</c> reads the target's <c>hip</c> and <c>back</c> sections, <b>Observed</b>).</summary>
    static bool IsWeaponSection(string section) => section is "hip" or "back";

    /// <summary>The stock shackles (ARMOUR <c>SHACKLES</c>, "Prisoner Shackles"; docs/game/locks.md "Data") a capturer puts on a captive (engine choice).</summary>
    public const string ShacklesRecord = "SHACKLES";

    /// <summary>EventTriggerEnum SHOUT_AT_SLAVE_WORKER.</summary>
    public const int ShoutAtSlaveWorker = 38;

    bool Waiting(List<(CharacterId Who, double Until)> list, CharacterId who, double now)
    {
        for (int k = 0; k < list.Count; k++)
            if (list[k].Who == who) return list[k].Until > now;
        return false;
    }

    void SetWait(List<(CharacterId Who, double Until)> list, CharacterId who, double until)
    {
        for (int k = 0; k < list.Count; k++)
            if (list[k].Who == who) { list[k] = (who, until); return; }
        list.Add((who, until));
    }

    void ExpireWaits(double now)
    {
        givenUp.RemoveAll(e => e.Until <= now);
        shoutUntil.RemoveAll(e => e.Until <= now);
    }

    /// <summary>
    /// Down for the capture and carry finders (<c>FUN_1408cdd00</c> / <c>FUN_1408cdf50</c>, <b>Verified</b>): alive, and unconscious or, out of combat mode, crawling or staying low (the
    /// KO and CRAWLING sense tags); the dead are excluded by the finders' tag mask.
    /// </summary>
    static bool Down(CharacterCold c) => c.Medical is { Dead: false } m && (m.Unconscious || (m.Crawling || m.StayingLow) && !c.InCombat);

    /// <summary>Maimed (<c>FUN_140644170</c>, <b>Verified</b>): a leg is a stump, or both arms are. The capture finder leaves such a character.</summary>
    static bool Maimed(Bodies.MedicalState m)
    {
        int arms = 0;
        foreach (var p in m.Parts)
        {
            if (p.Limb != Bodies.LimbState.Stump) continue;
            if (p.Template.LimbSlot is 2 or 3) return true;
            if (p.Template.LimbSlot is 0 or 1) arms++;
        }
        return arms >= 2;
    }

    // ------------------------------------------------------------------ finders

    struct DownScan
    {
        public CaptiveSystem System;
        public World World;
        public int Self;
        public CharacterCold Me;
        public bool Capture;
        public int Best;
        public float BestD2;
    }

    /// <summary>
    /// The nearest character the capturer takes (see the class remarks): within its sight, <see cref="Down"/>, uncarried and uncaged, no animal, not already a job's or given up; for a
    /// capture not of its own side, no slave of state 1, not law enforcement, trader or diplomat, not <see cref="Maimed"/>, not shackled (an engine guard so it is never cuffed twice);
    /// for a carry a slave of its faction that is not just staying low (the carry finder refuses posture 1).
    /// </summary>
    int NearestDown(World world, int slot, CharacterCold me, bool capture)
    {
        var scan = new DownScan { System = this, World = world, Self = slot, Me = me, Capture = capture, Best = -1, BestD2 = float.MaxValue };
        var p = world.Characters.Next[slot].Position;
        world.Grid.Query(p.X, p.Z, AggroSystem.RangeOf(me, AggroSystem.OutdoorSight), ref scan, static (int v, ref DownScan st) =>
        {
            if (!st.System.Eligible(st.World, st.Self, st.Me, v, st.Capture)) return;
            var a = st.World.Characters.Next[st.Self].Position;
            var b = st.World.Characters.Next[v].Position;
            float d2 = Vector2.DistanceSquared(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
            if (d2 < st.BestD2) (st.Best, st.BestD2) = (v, d2);
        });
        return scan.Best;
    }

    bool Eligible(World world, int self, CharacterCold me, int v, bool capture)
    {
        var table = world.Characters;
        if (v == self || !table.Next[v].Alive || table.Cold(v) is not { } c || !Down(c)) return false;
        if (!c.CarriedBy.IsNone || c.InSomething != HeldState.None || c.IsImprisoned || c.Fighter is { IsAnimal: true } || c.Appearance?.Animal is not null || c.Race is null) return false;
        var id = table.IdOf(v);
        if (Busy(id) || Waiting(givenUp, id, world.GameHours) || Kidnap is { } k && k.Abductions.Any(a => a.Victim == id)) return false;
        if (!capture) return c.SlaveState != 0 && c.SlaveOwner == me.Faction && c.Medical is not { Unconscious: false, Crawling: false, StayingLow: true };
        if (c.SlaveState == 1 || c.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Trader or FundamentalType.Diplomat || Maimed(c.Medical!) || LockSystem.WornShackles(c) is not null) return false;
        return c.Faction != me.Faction && !Allied(me.Faction, c.Faction);
    }

    /// <summary>
    /// Whether a character may start 167: awake, free, with one working arm (either: the planner's 38 <c>hasOneWorkingArm</c> for 166 Restraining and 181 Stripping weapons, <b>Verified</b>, ai-tasks.md;
    /// only the later lift, <see cref="CarrySystem.CanCarry"/>, needs the left arm) and in posture 0, which the slot-61 desire of a goal without a target reads.
    /// </summary>
    static bool CanCapture(CharacterCold cold) =>
        cold is { Medical: { Incapacitated: false, Standing: true } m, Race: not null, InCombat: false, IsImprisoned: false } && cold.Carrying.IsNone && cold.CarriedBy.IsNone
        && (m.ArmWorks(left: true) || m.ArmWorks(left: false));

    /// <summary>A job for goals 167 / 168 / 188, null when the member finds no target (or cannot carry one).</summary>
    CaptiveJob? TryStartSlaveWork(World world, Squad squad, int slot, CharacterCold cold, TaskType type)
    {
        var table = world.Characters;
        var worker = table.IdOf(slot);
        if (cold.Carrying is { IsNone: false }) return null;
        double now = world.GameHours;
        switch (type)
        {
            case TaskType.WORK_THE_SLAVES:
            {
                if (Waiting(shoutUntil, worker, now)) return null;
                var slaves = new List<int>();
                foreach (int s in SlavesOf(world, squad))
                    if (table.Cold(s) is { CarriedBy.IsNone: true, InSomething: HeldState.None } && !Waiting(givenUp, table.IdOf(s), now) && !Busy(table.IdOf(s))) slaves.Add(s);
                if (slaves.Count == 0) return null;
                ulong roll = Rng.Hash(world.Seed, Rng.Key(worker), RngPurpose.Slow, ShoutSalt + (ulong)world.Tick);
                int pick = slaves[Rng.Int(roll, slaves.Count)];
                // 0.5 to 1.5 times the cooldown, so the workers do not shout together.
                SetWait(shoutUntil, worker, now + Settings.ShoutCooldownSeconds * (0.5 + Rng.Float(Rng.Mix(roll))) / World.SecondsPerGameHour);
                return new CaptiveJob { Kind = CaptiveJobKind.Shout, Worker = worker, Captive = table.IdOf(pick), Cage = "", Faction = cold.Faction };
            }
            case TaskType.CAPTURE_NEW_SLAVES or TaskType.CARRY_WOUNDED_SLAVES:
            {
                bool capture = type == TaskType.CAPTURE_NEW_SLAVES;
                if (Kidnap is null || !(capture ? CanCapture(cold) : CarrySystem.CanCarry(world, cold, type))) return null;
                // 168's desire is the rescue one (FUN_140599480): 0 on the raw Hold bit (also while sneaking) or while carrying. 167 has no Hold test.
                if (!capture && (cold.Orders & CharacterOrders.Hold) != 0) return null;
                int v = NearestDown(world, slot, cold, capture);
                // No cage to take it to: not started, so no one is cuffed and left (engine; the carry, goal 112, needs a free cage as the arrest goal does).
                return v < 0 || !Kidnap.HasCage(world, table.Next[v].Position, cold) ? null : new CaptiveJob
                {
                    Kind = capture ? CaptiveJobKind.Capture : CaptiveJobKind.Carry, Worker = worker, Captive = table.IdOf(v), Cage = "", Faction = cold.Faction,
                };
            }
        }
        return null;
    }

    const ulong ShoutSalt = 0x53686F7574536C76;

    // ------------------------------------------------------------------ one job

    bool StepSlaveWork(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        if (!table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { } cc || cc.Medical is not { Dead: false } || !cc.CarriedBy.IsNone || cc.IsImprisoned)
            return End(world, j, CaptiveEventKind.GaveUp);
        var to = new Vector2(table.Next[c].Position.X, table.Next[c].Position.Z);
        if (j.Kind == CaptiveJobKind.Shout)
        {
            if (j.Blows == 0)
            {
                // The task fires its event as it starts (Task_TalktoPlayer slot 1) and walks to the target while the line lasts.
                j.Blows = 1;
                world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, ShoutAtSlaveWorker) { Tick = world.Tick + 1 });
            }
            var here = new Vector2(table.Next[w].Position.X, table.Next[w].Position.Z);
            if (Vector2.Distance(here, to) <= Settings.ShoutReach || j.Age > Settings.ShoutSeconds) return End(world, j, CaptiveEventKind.Shouted);
            if (table.Next[w].Task != (byte)CharacterTask.GoTo || Vector2.Distance(table.Next[w].Goal, to) > 5) TaskStart.Walk(world, w, to, SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);
            return true;
        }
        // It got up: a caged one is beaten down first (196 Beat prisoner, Task_KillPrisoner: B list 78 imprisoned, 5 weapon drawn; effect 7 target down) by an armed worker;
        // any other is given up.
        if (!Down(cc) && j.Kind == CaptiveJobKind.Capture && (!cc.IsImprisoned || combat is null || wc.Fighter is not { Unarmed: false })) return End(world, j, CaptiveEventKind.GaveUp);
        if (j.Phase == CaptivePhase.Walk)
        {
            var here = new Vector2(table.Next[w].Position.X, table.Next[w].Position.Z);
            float d = Vector2.Distance(here, to);
            bool walking = table.Next[w].Task == (byte)CharacterTask.GoTo;
            if (d > Settings.Reach && !(!walking && j.Walks > 0 && d <= Settings.Slack))
            {
                if (walking && Vector2.Distance(table.Next[w].Goal, to) <= 5) return true;
                if (!walking && ++j.Walks > Settings.Walks)
                {
                    SetWait(givenUp, j.Captive, world.GameHours + Settings.GiveUpHours);
                    return End(world, j, CaptiveEventKind.GaveUp);
                }
                TaskStart.Walk(world, w, to, SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);
                return true;
            }
            TaskStart.Stand(table, w);
            if (j.Kind == CaptiveJobKind.Carry) return Hand(world, j, w);
            j.Phase = CaptivePhase.Work;
            j.Time = 0;
            wc.ActionClip = ProcessClip;
        }
        // Capture at the captive: facing it, strip, then cuff.
        var face = table.Next[c].Position - table.Next[w].Position;
        if (face.X * face.X + face.Z * face.Z > 1e-4f) table.Next[w].Yaw = MathF.Atan2(face.X, face.Z);
        if (!Down(cc))
        {
            // Beating it down: one blow per chop clip at 0.55 of it, as the kill; the KO ends it, a death ends the job.
            if (wc.ActionClip != ChopClip) (wc.ActionClip, j.Time, j.Beats) = (ChopClip, 0, 0);
            if (j.Time < (j.Beats + Settings.BlowAt) * MathF.Max(Settings.ChopSeconds, 0.1f)) return true;
            j.Beats++;
            if (combat!.Execute(world, w, c, j.Beats)) return Over(world, j, true);
            if (Down(cc)) (wc.ActionClip, j.Time) = (ProcessClip, 0);
            return true;
        }
        if (j.Blows == 0)
        {
            if (j.Time < Settings.StripSeconds) return true;
            Strip(world, c, wc, cc);
            j.Blows = 1;
            j.Time = 0;
            return true;
        }
        EnslavingCrime(world, w, c, cc);
        if (j.Time < Settings.CuffSeconds) return true;
        Cuff(world, c, cc);
        world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, RecapturedSlave) { Tick = world.Tick + 1 });   // FUN_140684990(dialogue, target, 0x25) when the bar is full
        return Hand(world, j, w);
    }

    /// <summary>Ends the job and, when a cage of the worker's faction is in range, has the kidnapping system carry the captive to it.</summary>
    bool Hand(World world, CaptiveJob j, int w)
    {
        Note(world, CaptiveEventKind.Captured, j);
        var captive = j.Captive;
        End(world, j, null);
        if (Kidnap is null || !Kidnap.Carry(world, w, captive)) SetWait(givenUp, captive, world.GameHours + Settings.GiveUpHours);
        return false;
    }

    /// <summary>
    /// <c>Task_StripWeapons</c> once its 0.8 s are up: the items of the target's <c>hip</c> and <c>back</c> sections go to the stripper's inventory (the ground when it is full; engine choice).
    /// After each item moved the original sets the stripper's squad's short-term tag 6 on the target (<c>FUN_14034e7b0</c> calling <c>FUN_1406744a0</c>, <b>Verified</b>): <see cref="Dialogue.MemoryTag.Looted"/>, which the NPC looting finder skips.
    /// </summary>
    void Strip(World world, int c, CharacterCold wc, CharacterCold cc)
    {
        if (cc.Inventory is not { } inv) return;
        var at = world.Characters.Next[c].Position;
        bool moved = false;
        foreach (var item in inv.Items.ToList())
        {
            if (item.LockedOn || !IsWeaponSection(item.Section)) continue;
            inv.Items.Remove(item);
            item.Section = "main";
            if (wc.Inventory is not { } into || !into.Add(item)) world.GroundItems.Drop(item, at, 0);
            moved = true;
        }
        if (moved && wc.SquadId >= 0) world.SystemOf<Dialogue.DialogueSystem>()?.Memory.Flag(world, wc.SquadId, world.Characters.IdOf(c), Dialogue.MemoryTag.Looted);
        cc.DrawnWeapon = WeaponKinds.None;
        assembly.RefreshEquipment(cc);
    }

    /// <summary>
    /// Each update of <c>Task_CuffTarget</c> (<c>FUN_14034af20</c>, <b>Verified</b>): a captive in slave state 0 with no bounty for the null faction, whose squad's faction is the player's or
    /// anti-slavery, makes the cuffer the culprit of a pending Enslaving crime against the captive's faction.
    /// </summary>
    void EnslavingCrime(World world, int w, int c, CharacterCold cc)
    {
        if (cc.SlaveState != 0 || cc.Bounties?.AmountFor(-1) > 0 || cc.SquadId < 0 || world.Squads.Find(cc.SquadId) is not { } squad) return;

        bool enslaves = squad.Faction == world.Player.Faction || (uint)squad.Faction < (uint)data.Factions.Count && data.Factions[squad.Faction].AntiSlavery;
        if (enslaves) world.SystemOf<Crime.CrimeSystem>()?.SetPending(world, w, Crime.CrimeCode.Enslaving, cc.Faction, world.Characters.IdOf(c), Crime.CrimeSystem.MinTimer);
    }

    /// <summary>The cuffing's result: locked shackles worn (engine choice, see the class remarks).</summary>
    void Cuff(World world, int c, CharacterCold cc)
    {
        if (cc.Inventory is not { } inv || LockSystem.WornShackles(cc) is not null) return;
        if (data.Items.Create(ShacklesRecord, 1) is not { } shackles) return;
        // What the captive wears in that slot goes into its inventory first (as when shackles are put on by hand, LockSystem.PutOn).
        if (inv.Items.FirstOrDefault(i => i.Section == shackles.EquipmentSection) is { } worn && !worn.LockedOn)
        {
            var (section, x, y) = (worn.Section, worn.X, worn.Y);
            inv.Items.Remove(worn);
            worn.Section = "main";
            if (!inv.Add(worn)) { (worn.Section, worn.X, worn.Y) = (section, x, y); inv.Items.Add(worn); }
        }
        Items.InventoryCommands.TryEquip(inv, shackles);
        assembly.RefreshEquipment(cc);
    }

    void HashWaits(ref StateHasher hasher)
    {
        hasher.Add(givenUp.Count);
        foreach (var (who, until) in givenUp) { hasher.Add(who); hasher.Add(until); }
        hasher.Add(shoutUntil.Count);
        foreach (var (who, until) in shoutUntil) { hasher.Add(who); hasher.Add(until); }
    }
}
