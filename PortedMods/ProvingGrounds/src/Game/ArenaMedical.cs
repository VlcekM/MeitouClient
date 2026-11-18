using System.Globalization;
using HarmonyLib;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using Protocol = ProvingGrounds.Core.ArenaMedicalPolicy.Protocol;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/ArenaMedical.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.7 (PA §2.11, CAP 12). The pure
// parts: Core/ArenaMedicalPolicy.cs (M1: ShouldBlockTreatment, ShouldStabilizePrisoner, GetProtocolName, NextProtocol) and
// Core/Glue/ArenaMedicalLogic.cs (SetProtocol, the block over the match view, ActionableAidNeed, NeedsStabilization, IssueStabilizeOrder, the
// test verb), tested with fakes in Tests/Glue/ArenaMedicalLogicTests.cs (E3). Meitou 0.17.3 (tag v0.17.3, fa34a1b).
//
// ArenaMedical.cpp in order and what covers each:
//   g_protocol (:35) ... ArenaMedical.protocol below (process-wide, see "Saves")
//   applyFirstAid_orig / applyDoctoring_orig / applyRigging_orig / shouldIHelpThisGuy_orig / pickupObject_orig (:37-42) ... Harmony
//   IsScratchMedicActor, BlockTownBystander, shouldIHelpThisGuy_hook, pickupObject_hook (:44-71) ... task 7.4 (town bouts only: they fire for
//       TownArena::IsFighter patients while TownArena::OwnsMatch, which never holds in the player arena): src/Town/TownCombatRules.cs
//       (IsScratchMedic), CombatMatchView.BlocksTownBystander, the FindPatient postfix and the BeginPickUp prefix in src/Game/CombatHooks.cs
//   FunctionFor, MedicSupplyAccess, RestockMedicIfDepleted (:73-155) ... task 8.4c: AfterTreatment below over src/Engine/Treatments.cs
//       (TreatmentTick, Treatments.KitAccess) and ArenaMedicalLogic.RestockMedicIfDepleted (Core MedicSupplyPolicy); the C++ lines byte for byte
//   BlockFor (:157-165) ... ArenaMedicalLogic.BlockFor over the published CombatMatchView (its town-bystander half: task 7.4, the medic's record
//       read only for a town fighter patient)
//   applyFirstAid_hook, applyDoctoring_hook, applyRigging_hook (:167-209) ... one prefix on public FirstAidSystem.Apply(World, in Effect)
//       (Members.FirstAidApply): Meitou applies first aid, robot repair (the robot kit branch, FirstAidSystem.cs:569-583) and splints (rigging,
//       :540-544) in that one serial step, one effect per medic-tick. Blocked = the original skipped for that tick (C++: "return false and do
//       nothing": nothing bandaged, no kit drained, no XP; the treatment is not complete, so the medic stays on it and treats once allowed).
//       TownAftercare::RecordTreatment and the restock calls in those hooks: task 8.4c, the prefix's read (Treatments.Read) and a postfix
//       (AfterApply -> AfterTreatment): the restock only when the tick was not blocked, RecordTreatment for every first-aid / robot-repair tick
//       (blocked or not; a splint records nothing, as applyRigging_hook).
//   SetProtocol / GetProtocol (:214-225) ... SetProtocol / GetProtocol (ArenaMedicalLogic.SetProtocol)
//   GetProtocolName / NextProtocol (ArenaMedicalPolicy.cpp:23-38) ... ArenaMedicalPolicy (reused)
//   ActionableAidNeed / NeedsStabilization / IssueStabilizeOrder (:227-267) ... the same names, ArenaMedicalLogic over WorldMedical
//   InstallHooks (:269-313) ... ArenaMedicalPatches.Install (the C++ texts; Plugin.cpp:26-27's failure line)
//
// Engine calls (E1): isValid / isDead ... PgEngine.Exists / IsDead; getMedical ... PgEngine.HasMedical; wounds.size() ... PgEngine.WoundCount;
// currentBleedRate ... PgEngine.BleedRate; scoreFirstAidNeed(robot) ... PgEngine.AidNeed; addOrder(NULL, FIRST_AID_ORDER | FIRST_AID_ROBOT,
// target, false, true, pos) ... PgEngine.StartTreat (TaskStart.Start, CAP 12).
//
// Which Meitou paths start or carry out a treatment (all reach FirstAidSystem.Apply, so the one prefix covers them):
//   the player's TreatOrder / SplintOrder (FirstAidSystem.cs:106-145 -> IOrderOwner.Start :226-244 -> TaskStart.Start Treat), the Medic button
//   and the medic / robotics / splint jobs (PressMedic :155, MedicJobs.Start, FirstAidSystem.Finder.cs StartJob :920-932), the NPCs' automatic
//   looks (SlowWorld :807-866 -> HealSomeone, Finder.cs:826; SendGoalTreatments :891), the lift planner's TreatBeforeLift (Finder.cs:901-910) and any code start
//   (TaskStart.Start(Treat), our StartTreat): each runs the treat task, whose Act (:484-508) emits EffectTreat and whose Apply (:524) does the
//   work. Meitou has no separate robot-repair task (the same task picks the robot kit for robotic parts). Resting and beds heal through
//   BodySystem; the C++ hooks do not block those either (it hooks only applyFirstAid / applyDoctoring / applyRigging).
//
// Threading: FirstAidSystem.Apply runs only in World.Commit's serial loop (World.cs:720-724, ITickSystem step 6 "serial", World.cs:56-57).
// The prefix still reads only the published immutable match view (CombatHooks.View, one volatile reference, republished at the end of the
// system's Inputs and SlowWorld) and the volatile protocol; no allocation, no lock.
//
// Saves: the C++ keeps the protocol in a process global (g_protocol, :35): never written to the save, never reset on a load, back to Ringside
// Aid at the next game start; the Arena window's own g_medicalProtocol (ArenaUI.cpp:194, also not saved) is pushed in at every Start
// (ArenaUI.cpp:2392). The port keeps it the same way: one process-wide value (not in the per-world system, which a load rebuilds).
//
// Carriers: this task adds none (no medic is moved or made to lift anyone), so the 3.2b BeginPickUp refusal needs no exemption here. Task 7.4
// exempts the Scratch medics for town bout fighters (CombatMatchView.IsOwnCarrier).
//
// Callers: the frame thread never calls this class. GetProtocol / SetProtocol: the Arena window (task 4.5, through a PgCommand) and, until then,
// the TEST-ONLY `pg medical`. Task 4.5 must push the window's protocol at EVERY Start (ArenaUI.cpp:2392, SetProtocol before StartMatch's
// ingress) and on preset load (:2184-2185, then read it back), not only on the toggle (:1536-1537), or a protocol M7 or a test changed would
// stand. M7 TownArena sets Ringside Aid for a town bout (:1719-1720) and restores the previous one at the bout's end (:414) AND when the world
// unbinds (its AbandonWorldState / unload, :1814): the protocol is process-wide, so a load in the middle of a town bout would otherwise carry
// the bout's protocol into the next world. M6 PrisonerUtil (TickRingsideAid, the cage return, PrisonerUtil.cpp:1311-1368, 1542-1548, 1625-1637) calls
// ShouldStabilizePrisoner(GetProtocol()), NeedsStabilization and IssueStabilizeOrder; M7 TownArena (:414, 1719-1720, 1814) sets and restores
// the protocol; M8 TownAftercare / TownCorpseCleanup / TownSpectators read NeedsStabilization / ActionableAidNeed.

/// <summary>The glue of the original ArenaMedical.cpp (the medical protocol and its treatment block). One object per world for the per-world
/// reads; the protocol itself is process-wide, as the C++'s global. Callers that change it must put it back (task 4.5 pushes the window's
/// value at every Start; M7 restores its saved value at bout end and on world unbind: see the header).</summary>
sealed class ArenaMedical(SparSession session, ArenaIngress ingress, CombatHooks hooks, PopulationData data, Func<Town.TownArena?> townArena)
{
    // C++: ArenaMedical.cpp:35 g_protocol. Written on the sim thread only (SetProtocol), read by the Apply prefix (serial commit) and the console.
    static volatile int protocol = (int)Protocol.RingsideAid;
    long blockedTicks;   // port-only diagnostics: treatment ticks refused (commit step only; F10 and pg medical)
    long restocks;       // port-only diagnostics (task 8.4c): Scratch medic supply restored lines written (F10 and pg medical)

    // ---------- the protocol (C++ :214-225, ArenaMedicalPolicy.cpp:23-38) ----------

    /// <summary>C++: ArenaMedical.cpp:214-220. Locked while a spar match runs; an out-of-range value becomes Ringside Aid. Sim thread.</summary>
    internal void SetProtocol(World world, Protocol p) =>
        protocol = (int)ArenaMedicalLogic.SetProtocol(GetProtocol(), p, session.IsActive(world));

    /// <summary>C++: ArenaMedical.cpp:222-225. Any thread (one volatile read).</summary>
    internal static Protocol GetProtocol() => (Protocol)protocol;

    internal static string GetProtocolName(Protocol p) => ArenaMedicalPolicy.GetProtocolName(p);
    internal static Protocol NextProtocol(Protocol p) => ArenaMedicalPolicy.NextProtocol(p);
    internal static bool ShouldStabilizePrisoner(Protocol p) => ArenaMedicalPolicy.ShouldStabilizePrisoner(p);

    // ---------- the block (C++ BlockFor :157-165) ----------

    /// <summary>Whether a treatment tick by the medic in table slot <paramref name="actorSlot"/> (negative: none) on the patient in table slot
    /// <paramref name="patientSlot"/> is blocked (C++ BlockFor): the published view (slot keys) and the protocol, and (task 7.4) the medic's record
    /// only when the patient is a town bout fighter. Serial commit step (the Apply prefix); allocation-free.</summary>
    internal bool Blocks(World world, int actorSlot, int patientSlot)
    {
        var v = hooks.View;
        // IsTownFighter here (BlockFor tests it again) gates the medic test's record read to town fighter patients
        bool medic = actorSlot >= 0 && v.IsTownFighter(patientSlot) && Town.TownCombatRules.IsScratchMedic(world, actorSlot);
        bool block = ArenaMedicalLogic.BlockFor(v, GetProtocol(), actorSlot >= 0, patientSlot, medic);
        if (block) blockedTicks++;
        return block;
    }

    /// <summary>C++ ArenaMedical.cpp:174-178, 191-195 (task 8.4c): after the original (or its skip when blocked), the Scratch medic restock
    /// (RestockMedicIfDepleted, only when not blocked) and TownAftercare::RecordTreatment(who, patient, doctoring, blocked, complete, frameTime).
    /// Serial commit step; allocation-free unless a kit is restocked.</summary>
    internal void AfterTreatment(World world, int medic, int patient, in TreatmentTick t)
    {
        if (!t.Treat) return;
        if (Treatments.UsedKit(world, medic, t, out var kit, out float before, out float after, out bool robotKit) && kit is not null)
            RestockMedicIfDepleted(world, medic, kit, robotKit, before, after);
        bool complete = Treatments.Complete(world, medic, patient, t);
        townArena()?.Aftercare.RecordTreatment(Treatments.IdOf(world, medic), Treatments.IdOf(world, patient), t.Robot, t.Blocked, complete, t.Dt);
    }

    // C++: ArenaMedical.cpp:135-155
    void RestockMedicIfDepleted(World world, int medic, Meitou.Simulation.Items.ItemInstance kit, bool robotKit, float before, float after)
    {
        // the policy's first test (chargesBefore > 0 && chargesAfter <= 0) before any read of the medic
        if (!(before > 0.0f && after <= 0.0f)) return;
        var access = new Treatments.KitAccess(world, data, medic, kit, Town.TownCombatRules.IsScratchMedic(world, medic));
        string? line = ArenaMedicalLogic.RestockMedicIfDepleted(true, robotKit, before, after, ref access, Treatments.NameOf(world, medic), out bool error);
        if (line is null) return;
        restocks++;
        PGLog.Debug(line);
        if (error) PGLog.Error(line);
    }

    /// <summary>The F10 phase row.</summary>
    internal string Phase => string.Create(CultureInfo.InvariantCulture,
        $"{GetProtocolName(GetProtocol())}, block {ArenaMedicalPatches.BlockState}, {blockedTicks} treatment ticks blocked, {restocks} medic restocks");

    // ---------- aid need and the stabilise order (C++ :227-267; M6 / M8 call these in serial steps) ----------

    /// <summary>C++: ArenaMedical.cpp:227-243.</summary>
    internal static float ActionableAidNeed(World world, CharacterId target, bool robotAid) =>
        ArenaMedicalLogic.ActionableAidNeed(new WorldMedical(world), target, robotAid);

    /// <summary>C++: ArenaMedical.cpp:245-249.</summary>
    internal static bool NeedsStabilization(World world, CharacterId target) => ArenaMedicalLogic.NeedsStabilization(new WorldMedical(world), target);

    /// <summary>C++: ArenaMedical.cpp:251-267. Serial step (TaskStart.Start). Not refused by our own block: the C++ issues it only for an
    /// eliminated prisoner under Ringside Aid or after the match (PrisonerUtil.cpp:1311-1335, 1542-1548), where BlockFor never blocks, and the
    /// block works on the treatment ticks, never on the order.</summary>
    internal static bool IssueStabilizeOrder(World world, CharacterId medic, CharacterId target) =>
        ArenaMedicalLogic.IssueStabilizeOrder(new WorldMedical(world), medic, target);

    /// <summary>The engine side of <see cref="IMedicalWorld{TId}"/> (PgEngine, serial steps). A struct: no allocation.</summary>
    readonly struct WorldMedical(World world) : IMedicalWorld<CharacterId>
    {
        public bool IsValid(CharacterId c) => !c.IsNone && PgEngine.Exists(world, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(world, c);
        public bool HasMedical(CharacterId c) => PgEngine.HasMedical(world, c);
        public int WoundCount(CharacterId c) => PgEngine.WoundCount(world, c);
        public float BleedRate(CharacterId c) => PgEngine.BleedRate(world, c);
        public float AidNeed(CharacterId c, bool robot) => PgEngine.AidNeed(world, c, robot);
        public bool GiveTreatOrder(CharacterId medic, CharacterId target, bool robotAid) => PgEngine.StartTreat(world, medic, target, robotAid);
    }

    // ---------- TEST ONLY: pg medical (until the Arena window, task 4.5) ----------

    /// <summary>TEST ONLY <c>pg medical &lt;ringside|bloodsport|status&gt;</c> (divergences.md "Test-only commands"): the Arena window's toggle
    /// rule (refused while an arena start is pending or a match runs, ArenaUI.cpp:1534) then SetProtocol; answered as one PG-MEDICAL JSON line.
    /// Sim thread, Inputs.</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        string word = args.Count > 0 ? args[0].ToLowerInvariant() : "status";
        var r = ArenaMedicalLogic.Command(word, GetProtocol(), session.IsActive(world), ingress.IsPending(world));
        if (r.Ok) protocol = (int)r.Protocol;   // Command already applied SetProtocol's rule (status: unchanged)
        var p = GetProtocol();
        string line = "{\"event\":\"medical\",\"verb\":" + PGLog.Quote(word) + ",\"ok\":" + (r.Ok ? "true" : "false")
            + ",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
            + (r.Error is null ? "" : ",\"error\":" + PGLog.Quote(r.Error))
            + ",\"protocol\":" + PGLog.Quote(GetProtocolName(p)) + ",\"matchActive\":" + (session.IsActive(world) ? "true" : "false")
            + ",\"blockOn\":" + (ArenaMedicalPatches.BlockWorking ? "true" : "false") + ",\"block\":" + PGLog.Quote(ArenaMedicalPatches.BlockState)
            + ",\"blockedTicks\":" + blockedTicks.ToString(CultureInfo.InvariantCulture)
            + ",\"restocks\":" + restocks.ToString(CultureInfo.InvariantCulture) + "}";
        ProvingGroundsMod.Ctx?.Log("PG-MEDICAL " + line);
        return r.Ok ? "medical " + GetProtocolName(p) : "medical refused: " + r.Error;
    }
}

/// <summary>The Harmony patch of ArenaMedical (task 4.7) and its installer (C++: ArenaMedical.cpp:269-313 <c>InstallHooks</c>). A good
/// neighbour: a prefix that skips the original only for a blocked match patient, respects another prefix's skip, never throws into the game
/// (a failure is logged at most three times; after <see cref="OffAfterFailures"/> in a row the block is off for the process).</summary>
static class ArenaMedicalPatches
{
    internal const int OffAfterFailures = 20;

    /// <summary>The Apply prefix was applied.</summary>
    internal static volatile bool BlockOn;

    /// <summary>The block works: applied and not turned off after repeated failures.</summary>
    internal static bool BlockWorking => BlockOn && Volatile.Read(ref off) == 0;

    /// <summary>"on", "off after failures" or "off" (not applied), for the F10 row and pg medical.</summary>
    internal static string BlockState => !BlockOn ? "off" : Volatile.Read(ref off) != 0 ? "off after failures" : "on";
    static int failures, consecutive, off;

    static readonly PgPatch ApplyPatch = new(Part.Medical, "FirstAidSystem.Apply prefix and postfix", Members.FirstAidApply,
        PgPatches.Method(typeof(ArenaMedicalPatches), nameof(BeforeApply)), PgPatches.Method(typeof(ArenaMedicalPatches), nameof(AfterApply)));

    /// <summary>The medical patch (applied by <see cref="Install"/> and by PgCheck hooks, through PgPatches.Apply).</summary>
    internal static IEnumerable<PgPatch> Patches() => [ApplyPatch];

    /// <summary>C++: ArenaMedical.cpp:269-313. The three apply* hooks are one patch here, so they stand or fall together (the C++'s three
    /// failure texts; Plugin.cpp:26-27's line when it fails). The shouldIHelpThisGuy / pickupObject hooks are task 7.4's patches in CombatHookPatches
    /// (src/Game/CombatHooks.cs), which write their C++ failure lines.</summary>
    internal static void Install(ModContext c)
    {
        bool ok = false;
        try
        {
            var missing = Members.MissingMedical().ToList();
            if (missing.Count > 0) c.LogError($"medical block: {string.Join(", ", missing)} not found (block off)");
            else
            {
                try { PgPatches.Apply(c.Harmony, ApplyPatch); ok = true; c.Log($"medical hook on: {ApplyPatch.What}"); }
                catch (Exception e) { c.LogError($"medical hook off: {ApplyPatch.What}: {e.GetType().Name}: {e.Message}"); }
            }
        }
        catch (Exception e) { c.LogError($"medical hook off: {e.GetType().Name}: {e.Message}"); }
        BlockOn = ok;
        if (!ok)
        {
            PGLog.Error("Proving Grounds: failed to hook MedicalSystem::applyFirstAid");
            PGLog.Error("Proving Grounds: failed to hook MedicalSystem::applyDoctoring");
            PGLog.Error("Proving Grounds: failed to hook MedicalSystem::applyRigging");
            PGLog.Error("Proving Grounds: ArenaMedical hooks install failed");
        }
    }

    /// <summary>Prefix on public FirstAidSystem.Apply(World world, in Effect effect) (FirstAidSystem.cs:524), serial commit: a treatment tick
    /// (<c>EffectTreat</c>) on a patient the protocol blocks is skipped (C++ applyFirstAid_hook / applyDoctoring_hook / applyRigging_hook:
    /// blocked -> the original not called, false returned). Everything else, or when another prefix already skipped the original, runs as
    /// without the mod.</summary>
    static bool BeforeApply(World world, in Effect effect, bool __runOriginal, out TreatmentTick __state)
    {
        using var perfScope = PgPerf.Measure(PgPerf.MedicalApplyPrefix);   // task 9.2a: the cost instrument (off: one volatile read)
        __state = default;
        if (!__runOriginal || effect.Kind != FirstAidSystem.EffectTreat || Volatile.Read(ref off) != 0) return true;
        try
        {
            var medical = ProvingGroundsSystem.MedicalFor(world);
            bool block = medical?.Blocks(world, effect.Source, effect.Target) == true;
            if (medical is not null) __state = Treatments.Read(world, effect.Source, effect.Target, block);   // task 8.4c: the postfix's "before"
            if (consecutive != 0) consecutive = 0;
            return !block;
        }
        catch (Exception e)
        {
            try
            {
                if (++consecutive >= OffAfterFailures && Interlocked.Exchange(ref off, 1) == 0)
                    ProvingGroundsMod.Ctx?.LogError($"medical block: {OffAfterFailures} failures in a row, turned off for this session (treatment runs as without the mod)");
                if (++failures <= 3)
                    ProvingGroundsMod.Ctx?.LogError($"medical block failed (the treatment runs): {e.GetType().Name}: {e.Message}");
            }
            catch { }
            return true;
        }
    }

    /// <summary>Postfix on FirstAidSystem.Apply (task 8.4c): the C++ hooks' calls after the original, RestockMedicIfDepleted and
    /// TownAftercare::RecordTreatment (ArenaMedical.cpp:174-178, 191-195), from the prefix's read. Runs whether the prefix skipped the original
    /// (blocked: recorded as blocked, no restock) or not; nothing when the prefix read no treatment. Never throws into the game (failures counted
    /// with the prefix's).</summary>
    static void AfterApply(World world, in Effect effect, TreatmentTick __state)
    {
        using var perfScope = PgPerf.Measure(PgPerf.MedicalApplyPostfix);   // task 9.2a: the cost instrument (off: one volatile read)
        if (!__state.Treat) return;
        try { ProvingGroundsSystem.MedicalFor(world)?.AfterTreatment(world, effect.Source, effect.Target, __state); }
        catch (Exception e)
        {
            try
            {
                if (++failures <= 3)
                    ProvingGroundsMod.Ctx?.LogError($"medical record failed (the treatment ran): {e.GetType().Name}: {e.Message}");
            }
            catch { }
        }
    }
}
