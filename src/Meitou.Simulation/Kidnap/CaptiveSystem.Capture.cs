using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The slavers' dealings with the downed and with their slaves at work (docs/game/ai-tasks.md "Prisoner fate and slave rows" and "Slave work, shout
/// and capture"; docs/simulation.md "Captives"):
/// <list type="bullet">
/// <item><b>Capture</b> (167 "Capturing slave", finder <c>FUN_1408cdd00</c>, <b>Observed</b>): the nearest downed (<see cref="Down"/>: a crawler out
/// of combat mode too), uncarried, uncaged character on its sensed list (seen, so in its building or in the open; <see cref="NearestDown"/>) that is
/// no animal, not of the capturer's faction or an ally (<b>engine reading</b> of the sensed-class masks), no slave already (slave state 1), of NPC
/// class other than law enforcement, trader and diplomat, not maimed, and not in shackles (engine guard). The capturer walks to it, strips its
/// weapons (the <c>hip</c> and <c>back</c> sections go to the capturer, after 0.8 s of <c>medic</c>,
/// <c>Task_StripWeapons</c>), cuffs it
/// (<c>Task_CuffTarget</c>: a "Restraining" bar, <c>medic</c>, event 37 to the captive when it is full; the shackles are an <b>engine choice</b>,
/// their source in the original is <b>Unknown</b>)
/// and the cuff makes it a slave of the capturer's faction (<c>setChainedMode</c>, see <see cref="Hand"/>).</item>
/// <item><b>Carry</b> (168 "Carrying slaves", finder <c>FUN_1408cdf50</c>): a downed slave owned by the carrier's faction is lifted and carried along
/// with the squad until a cage is found (goal 112 in the original, here <see cref="KidnapSystem.Carry"/> hauling).</item>
/// <item><b>Shout</b> (188 "Working the slaves", finder <c>FUN_1408cd940</c>): the worker says event 38 SHOUT_AT_SLAVE_WORKER to a random slave of
/// its squad that is alive, uncarried and uncaged (the row's TaskData event, <b>Verified</b>: the registration of 188 stores 0x26 at <c>+0x130</c>)
/// and keeps with it, saying the event again every <see cref="CaptiveSettings.ShoutSeconds"/>, until it says a line: the row's B list is
/// <c>isDialogDelivered</c>, and the original's worker stays on 188 with one slave as target for long spells (<b>Observed</b>). The next sweep then
/// picks a slave again, with no pause between.</item>
/// </list>
/// A job that gives up puts its target on the capturer's give-up list for <see cref="CaptiveSettings.GiveUpHours"/> (engine: the writer of the
/// original's list is not traced).
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>
    /// The downed and cuffed or carried: the AI's give-up list entries (hashed while any exist).
    /// </summary>
    readonly List<(CharacterId Who, double Until)> _givenUp = [];

    /// <summary>
    /// The kidnapping system that carries a captive to a cage once it is cuffed (null: captives are cuffed and left).
    /// </summary>
    public KidnapSystem? Kidnap { get; set; }

    /// <summary>
    /// Weapon sections <c>Task_StripWeapons</c> empties (<c>FUN_140747ff0</c> reads the target's <c>hip</c> and <c>back</c> sections,
    /// <b>Observed</b>).
    /// </summary>
    static bool IsWeaponSection(string section) => section is "hip" or "back";

    /// <summary>
    /// The stock shackles (ARMOUR <c>SHACKLES</c>, "Prisoner Shackles"; docs/game/locks.md "Data") a capturer puts on a captive (engine choice).
    /// </summary>
    public const string ShacklesRecord = "SHACKLES";

    /// <summary>
    /// EventTriggerEnum SHOUT_AT_SLAVE_WORKER.
    /// </summary>
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
        {
            if (list[k].Who == who)
            {
                list[k] = (who, until);
                return;
            }
        }

        list.Add((who, until));
    }

    void ExpireWaits(double now)
    {
        _givenUp.RemoveAll(e => e.Until <= now);
    }

    /// <summary>
    /// Down for the capture and carry finders (<c>FUN_1408cdd00</c> / <c>FUN_1408cdf50</c>, <b>Verified</b>): alive, and unconscious or, out of
    /// combat mode, crawling or staying low (the KO and CRAWLING sense tags); the dead are excluded by the finders' tag mask.
    /// </summary>
    static bool Down(CharacterCold c) => c.Medical is { Dead: false } m && (m.Unconscious || (m.Crawling || m.StayingLow) && !c.InCombat);

    /// <summary>
    /// Maimed (<c>FUN_140644170</c>, <b>Verified</b>): a leg is a stump, or both arms are. The capture finder leaves such a character.
    /// </summary>
    static bool Maimed(MedicalState m)
    {
        int arms = 0;
        foreach (var p in m.Parts)
        {
            if (p.Limb != LimbState.Stump) continue;
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
        public SenseBlock? Senses;
        public int Self;
        public CharacterCold Me;
        public bool Capture;
        public int Best;
        public float BestD2;
    }

    /// <summary>
    /// The nearest character the capturer takes (see the class remarks) among those on its sensed list (the finders walk it with no range limit,
    /// <b>Verified</b>; so only one it saw, which needs the same building or the open unless either stands on top, docs/game/perception.md "Sight"):
    /// <see cref="Down"/>, uncarried and uncaged, no animal, not already a job's or given up; for a capture not of its own side, no slave of state 1,
    /// not law enforcement, trader or diplomat, not <see cref="Maimed"/>, not shackled (an engine guard so it is never cuffed twice); for a carry a
    /// slave of its faction that is not just staying low (the carry finder refuses posture 1). The entries its mask keeps
    /// (<see cref="ConsiderSensed"/>) come first, then whoever within its sight the capped block lacks (<see cref="CharacterFacts.Perceives"/>);
    /// nearest by 3D distance to where it stands now.
    /// </summary>
    int NearestDown(World world, int slot, CharacterCold me, bool capture)
    {
        var scan = new DownScan { System = this, World = world, Senses = CharacterFacts.Senses(world, slot), Self = slot, Me = me, Capture = capture,
                Best = -1, BestD2 = float.MaxValue };
        ConsiderSensed(ref scan);

        var p = world.Characters.Next[slot].Position;
        world.Grid.Query(p.X, p.Z, AggroSystem.RangeOf(me, AggroSystem.OutdoorSight), ref scan, static (int v, ref DownScan st) =>
        {
            if (st.Senses?.Knows(st.World.Characters.IdOf(v)) == true || !CharacterFacts.Perceives(st.World, st.Self, v))
                return;

            Consider(ref st, v);
        });
        return scan.Best;
    }

    /// <summary>
    /// The worker's sense entries whose character is still that slot's, less those its finder's exclude mask leaves out: for a capture ally or dead
    /// (0x11), for a carry dead or imprisoned (0x210; its include mask 0x20 is slave state 1, which <see cref="Eligible"/> tests on the current
    /// state).
    /// </summary>
    static void ConsiderSensed(ref DownScan scan)
    {
        if (scan.Senses is not { } senses) return;

        var table = scan.World.Characters;
        var excluded = scan.Capture ? SenseTag.Ally : SenseTag.Imprisoned;
        foreach (var e in senses.Entries)
        {
            if ((e.Tags & excluded) != 0 || (uint)e.Target >= (uint)table.HighWater || table.Next[e.Target].Generation != e.Generation)
                continue;

            Consider(ref scan, e.Target);
        }
    }

    static void Consider(ref DownScan st, int v)
    {
        if (!st.System.Eligible(st.World, st.Self, st.Me, v, st.Capture)) return;

        float d2 = Vector3.DistanceSquared(st.World.Characters.Next[st.Self].Position, st.World.Characters.Next[v].Position);
        if (d2 < st.BestD2) (st.Best, st.BestD2) = (v, d2);
    }

    bool Eligible(World world, int self, CharacterCold me, int v, bool capture)
    {
        var table = world.Characters;
        if (v == self || !table.Next[v].Alive || table.Cold(v) is not { } c || !Down(c)) return false;
        if (!c.CarriedBy.IsNone || c.InSomething != HeldState.None || c.IsImprisoned || c.Fighter is { IsAnimal: true }
                || c.Appearance?.Animal is not null || c.Race is null)
            return false;

        var id = table.IdOf(v);
        if (Busy(id) || Waiting(_givenUp, id, world.GameHours) || Kidnap is { } k && k.Abductions.Any(a => a.Victim == id))
            return false;

        if (!capture)
            return c.SlaveState != 0 && c.SlaveOwner == me.Faction && c.Medical is not { Unconscious: false, Crawling: false, StayingLow: true };

        if (c.SlaveState == 1 || c.NpcClass is FundamentalType.LawEnforcement or FundamentalType.Trader or FundamentalType.Diplomat
                || Maimed(c.Medical!) || LockSystem.WornShackles(c) is not null)
            return false;

        return c.Faction != me.Faction && !Allied(me.Faction, c.Faction);
    }

    /// <summary>
    /// Whether a character may start 167: awake, free, with one working arm (either: the planner's 38 <c>hasOneWorkingArm</c> for 166 Restraining and
    /// 181 Stripping weapons, <b>Verified</b>, ai-tasks.md; only the later lift, <see cref="CarrySystem.CanCarry"/>, needs the left arm) and in
    /// posture 0, which the slot-61 desire of a goal without a target reads.
    /// </summary>
    static bool CanCapture(CharacterCold cold) =>
        cold is { Medical: { Incapacitated: false, Standing: true } m, Race: not null, InCombat: false, IsImprisoned: false } && cold.Carrying.IsNone
                && cold.CarriedBy.IsNone
        && (m.ArmWorks(left: true) || m.ArmWorks(left: false));

    /// <summary>
    /// A job for goals 167 / 168 / 188, null when the member finds no target (or cannot carry one); <paramref name="probe"/> as
    /// <see cref="TryStart"/>'s.
    /// </summary>
    CaptiveJob? TryStartSlaveWork(World world, Squad squad, int slot, CharacterCold cold, TaskType type, bool probe = false)
    {
        var table = world.Characters;
        var worker = table.IdOf(slot);
        if (cold.Carrying is { IsNone: false }) return null;
        double now = world.GameHours;
        switch (type)
        {
            case TaskType.WorkTheSlaves:
                {
                    var slaves = new List<int>();
                    foreach (int s in SlavesOf(world, squad))
                    {
                        if (table.Cold(s) is { CarriedBy.IsNone: true, InSomething: HeldState.None } && !Waiting(_givenUp, table.IdOf(s), now)
                                && !Busy(table.IdOf(s)))
                            slaves.Add(s);
                    }

                    if (slaves.Count == 0) return null;
                    ulong roll = Rng.Hash(world.Seed, Rng.Key(worker), RngPurpose.Slow, ShoutSalt + (ulong)world.Tick);
                    int pick = slaves[Rng.Int(roll, slaves.Count)];
                    return new CaptiveJob { Kind = CaptiveJobKind.Shout, Worker = worker, Captive = table.IdOf(pick), Cage = "",
                            Faction = cold.Faction };
                }
            case TaskType.CaptureNewSlaves or TaskType.CarryWoundedSlaves:
                {
                    bool capture = type == TaskType.CaptureNewSlaves;
                    if (Kidnap is null || !(capture ? CanCapture(cold) : CarrySystem.CanCarry(world, cold, type)))
                        return null;
                    // 168's desire is the rescue one (FUN_140599480): 0 on the raw Hold bit (also while sneaking) or while carrying. 167 has no Hold
                    // test.
                    if (!capture && (cold.Orders & CharacterOrders.Hold) != 0) return null;
                    int v = NearestDown(world, slot, cold, capture);

                    // Row 170 (the lift 168 plans through) wants !22: a victim that needs first aid is treated first (row 25, with a kit and an arm),
                    // else the plan fails and nothing is lifted.
                    if (!capture && v >= 0 && table.Cold(v) is { } hurt && FirstAid.FirstAidSystem.NeedsFirstAid(world, hurt))
                    {
                        // A probe counts the goal as started: its plan treats first, which the start does.
                        if (probe)
                        {
                            return new CaptiveJob { Kind = CaptiveJobKind.Carry, Worker = worker, Captive = table.IdOf(v), Cage = "",
                                    Faction = cold.Faction };
                        }

                        FirstAid.FirstAidSystem.TreatBeforeLift(world, slot, v);
                        return null;
                    }

                    // Neither finder (FUN_1408cdd00, FUN_1408cdf50) tests for a cage: the cuffed captive is enslaved (Hand), and a lifted slave is
                    // carried until goal 112 finds one.
                    if (v < 0) return null;
                    return new CaptiveJob
                    {
                        Kind = capture ? CaptiveJobKind.Capture : CaptiveJobKind.Carry,
                        Worker = worker,
                        Captive = table.IdOf(v),
                        Cage = "",
                        Faction = cold.Faction,
                    };
                }
        }

        return null;
    }

    const ulong ShoutSalt = 0x53686F7574536C76;

    // ------------------------------------------------------------------ one job

    /// <summary>
    /// One tick of a Shout job (188, <c>Task_TalktoPlayer</c>): event 38 as it starts, as the task fires it, and again every
    /// <see cref="CaptiveSettings.ShoutSeconds"/> while the worker is quiet (engine choice: the original's re-plan rate is not decoded). The job ends
    /// once the worker speaks after an event went out (engine choice, see <c>docs/simulation.md</c>: the original's B list <c>isDialogDelivered</c>
    /// is true without a Dialogue object, else the event delivered in its town), so a line still showing from the last job neither ends this one nor
    /// is talked over. A worker with no line never ends it: <see cref="CaptiveSettings.GiveUpSeconds"/> does.
    /// </summary>
    bool StepShout(World world, CaptiveJob j, int w, int c, Vector2 to)
    {
        bool speaking = (_talk ??= world.SystemOf<Dialogue.DialogueSystem>())?.Engages(j.Worker) == true;
        if (j.Blows > 0 && speaking) return End(world, j, CaptiveEventKind.Shouted);

        if (!speaking && (j.Blows == 0 || j.Time >= Settings.ShoutSeconds))
        {
            j.Blows++;
            j.Time = 0;
            world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, ShoutAtSlaveWorker) { Tick = world.Tick + 1 });
        }

        FollowSlave(world, j, w, c, to);
        return true;
    }

    /// <summary>
    /// The worker walks to its slave and stands within <see cref="CaptiveSettings.ShoutReach"/> of it, walking on when the slave moves off; it runs
    /// while the slave moves faster than <see cref="SpeedMode.ChaseRunAbove"/> (<see cref="SpeedMode.Chasing"/>).
    /// </summary>
    void FollowSlave(World world, CaptiveJob j, int w, int slave, Vector2 to)
    {
        var table = world.Characters;
        var here = new Vector2(table.Next[w].Position.X, table.Next[w].Position.Z);
        bool walking = table.Next[w].Task == (byte)CharacterTask.GoTo;

        if (Vector2.Distance(here, to) <= Settings.ShoutReach)
        {
            if (walking) TaskStart.Stand(table, w);
            return;
        }

        byte mode = SpeedMode.Chasing(table, slave, SpeedMode.Walk);
        if (!walking || Vector2.Distance(table.Next[w].Goal, to) > 5)
        {
            TaskStart.Walk(world, w, to, mode, RowOf(j.Kind), TaskSource.Goal);
        }
        else
        {
            table.Next[w].Mode = mode;
        }
    }

    // The dialogue a Shout job asks whether its worker said a line (null until first asked, or without one).
    Dialogue.DialogueSystem? _talk;

    bool StepSlaveWork(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        float dt = world.TickSeconds;
        if (!table.TryResolveNext(j.Captive, out int c) || table.Cold(c) is not { } cc || cc.Medical is not { Dead: false })
            return End(world, j, CaptiveEventKind.GaveUp);

        if (!cc.CarriedBy.IsNone || cc.IsImprisoned)
        {
            // A capture whose target someone else took (a guard carrying it to jail): left alone for a while, so the next sweep does not send the
            // worker after it again at once (engine choice).
            if (j.Kind == CaptiveJobKind.Capture) SetWait(_givenUp, j.Captive, world.GameHours + Settings.GiveUpHours);
            return End(world, j, CaptiveEventKind.GaveUp);
        }

        var to = new Vector2(table.Next[c].Position.X, table.Next[c].Position.Z);
        if (j.Kind == CaptiveJobKind.Shout) return StepShout(world, j, w, c, to);

        // It got up: a caged one is beaten down first (196 Beat prisoner, Task_KillPrisoner: B list 78 imprisoned, 5 weapon drawn; effect 7 target
        // down) by an armed worker; any other is given up.
        if (!Down(cc) && j.Kind == CaptiveJobKind.Capture && (!cc.IsImprisoned || combat is null || wc.Fighter is not { Unarmed: false }))
            return End(world, j, CaptiveEventKind.GaveUp);

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
                    SetWait(_givenUp, j.Captive, world.GameHours + Settings.GiveUpHours);
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

        return CaptureAt(world, j, w, c, wc, cc);
    }

    /// <summary>
    /// The work at the captive: facing it, a caged one beaten down, its weapons stripped, then cuffed and handed over.
    /// </summary>
    bool CaptureAt(World world, CaptiveJob j, int w, int c, CharacterCold wc, CharacterCold cc)
    {
        var table = world.Characters;

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
            StripWeapons(world, c, wc, cc);
            j.Blows = 1;
            j.Time = 0;
            return true;
        }

        EnslavingCrime(world, w, c, cc);
        if (j.Time < Settings.CuffSeconds) return true;
        Cuff(world, c, cc);

        // FUN_140684990(dialogue, target, 0x25) when the bar is full
        world.Defer(new Dialogue.DialogueEvent(j.Worker, j.Captive, RecapturedSlave) { Tick = world.Tick + 1 });
        return Hand(world, j, w);
    }

    /// <summary>
    /// Ends the job. A capture's captive is now chained: the cuff's message (type 5, read by the world's update <c>FUN_1407b65b0</c>) runs
    /// <c>setChainedMode</c> (<c>FUN_14032e590</c>, <b>Verified</b>), which makes it a slave (state 1) of the cuffer's faction, obeying it (job 187);
    /// 168 then lifts it like any downed slave. A carry has the kidnapping system lift the slave and haul it (<see cref="KidnapSystem.Carry"/>; goal
    /// 112 cages it once a cage is found).
    /// </summary>
    bool Hand(World world, CaptiveJob j, int w)
    {
        Note(world, CaptiveEventKind.Captured, j);
        var captive = j.Captive;
        var kind = j.Kind;
        End(world, j, null);
        if (kind == CaptiveJobKind.Capture) Chain(world, w, captive);
        else if (Kidnap is null || !Kidnap.Carry(world, w, captive, hauls: true))
            SetWait(_givenUp, captive, world.GameHours + Settings.GiveUpHours);
        return false;
    }

    /// <summary>
    /// <c>setChainedMode</c>'s slave state and owner: a slave of the cuffer's faction obeying the cuffer's squad (the original's owner is the cuffer
    /// character).
    /// </summary>
    void Chain(World world, int w, CharacterId captive)
    {
        var table = world.Characters;
        if (table.Cold(w) is not { } wc || !table.TryResolveNext(captive, out int c) || table.Cold(c) is not { Medical.Dead: false } cc)
            return;

        FixtureRegistry.Enslave(world, cc, wc.Faction, wc.SquadId);
        if (Settings.RecordLog)
            _log.Add(new CaptiveEvent(world.Tick, CaptiveEventKind.Enslaved, CaptiveJobKind.Capture, table.IdOf(w), captive));
    }

    /// <summary>
    /// <c>Task_StripWeapons</c> once its 0.8 s are up: the items of the target's <c>hip</c> and <c>back</c> sections go to the stripper's inventory
    /// (the ground when it is full; engine choice). After each item moved the original sets the stripper's squad's short-term tag 6 on the target
    /// (<c>FUN_14034e7b0</c> calling <c>FUN_1406744a0</c>, <b>Verified</b>): <see cref="Dialogue.MemoryTag.Looted"/>, which the NPC looting finder
    /// skips.
    /// </summary>
    public void StripWeapons(World world, int c, CharacterCold wc, CharacterCold cc)
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

        if (moved && wc.SquadId >= 0)
            world.SystemOf<Dialogue.DialogueSystem>()?.Memory.Flag(world, wc.SquadId, world.Characters.IdOf(c), Dialogue.MemoryTag.Looted);

        cc.DrawnWeapon = WeaponKinds.None;
        _assembly.RefreshEquipment(cc);
    }

    /// <summary>
    /// Each update of <c>Task_CuffTarget</c> (<c>FUN_14034af20</c>, <b>Verified</b>): a captive in slave state 0 with no bounty for the null faction,
    /// whose squad's faction is the player's or anti-slavery, makes the cuffer the culprit of a pending Enslaving crime against the captive's
    /// faction.
    /// </summary>
    void EnslavingCrime(World world, int w, int c, CharacterCold cc)
    {
        if (cc.SlaveState != 0 || cc.Bounties?.AmountFor(-1) > 0 || cc.SquadId < 0 || world.Squads.Find(cc.SquadId) is not { } squad)
            return;

        bool enslaves = squad.Faction == world.Player.Faction || (uint)squad.Faction < (uint)data.Factions.Count
                && data.Factions[squad.Faction].AntiSlavery;
        if (enslaves)
        {
            world.SystemOf<Crime.CrimeSystem>()?.SetPending(world, w, Crime.CrimeCode.Enslaving, cc.Faction, world.Characters.IdOf(c),
                    Crime.CrimeSystem.MinTimer);
        }
    }

    /// <summary>
    /// The cuffing's result: locked shackles worn (engine choice, see the class remarks).
    /// </summary>
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
            if (!inv.Add(worn))
            {
                (worn.Section, worn.X, worn.Y) = (section, x, y);
                inv.Items.Add(worn);
            }
        }

        Items.InventoryCommands.TryEquip(inv, shackles);
        _assembly.RefreshEquipment(cc);
    }

    void HashWaits(ref StateHasher hasher)
    {
        hasher.Add(_givenUp.Count);
        foreach (var (who, until) in _givenUp)
        {
            hasher.Add(who);
            hasher.Add(until);
        }
    }
}