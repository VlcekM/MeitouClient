using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Town;

// Task 7.4 "Town rules for fights (crime, relations, joiners, carrying)" (M7-town-core.md; decision D5: mirror the original's hooks with Harmony; CAP 2
// option 2, CAP 20 and the hooks table; PA §2.8 "the four crime/relation rows"; TW §2.2 who is sanctioned: the town bout's fighters). Meitou 0.17.3
// (tag v0.17.3, fa34a1b).
//
// The town rows of the original, in their files' order, and what covers each in the port. Controller decision 3: the patches are the 3.2b patch
// set (src/Game/CombatHooks.cs, CombatHookPatches), not a second patch class, so one view (CombatMatchView, Core/Glue/CombatHookLogic.cs) owns "is
// this pair isolated / sanctioned" for player matches and town bouts alike; the C++ predicates are Core/Glue/TownArenaLogic.Rules.cs.
//   TownArena::IsIncidentPair (TownArena.cpp:1415-1427), IsActiveCollateralIncidentPair (:1428-1438) ... TownArenaLogic.IsIncidentPair /
//       IsActiveCollateralIncidentPair over the state; the combat hooks publish CopyIncidentState into the view, whose IsIncidentPair /
//       IsCollateralPair run the same TownIncidentPairs loops (the patches read only the view)
//   TownArena::IsIncidentFactionPair (:1439-1456) ... not ported: Meitou raises no asynchronous combat event (TownArenaLogic.Rules.cs header)
//   CollateralPair / SanctionedPair (CombatHooks.cpp:32-40) ... CombatMatchView.IsCollateralPair / IsSanctionedPair
//   ArenaIncident, the thread-local incident factions (:42-68), meleeHit_hook (_NV_hitByMeleeAttack :80-89), attackingYou_hook (:115-119), and the
//       incident set inside iHitYouAreYouHit_hook (:201) and attackTarget_hook (:256) ... none needed: the C++ carried the blow's two characters
//       to the nested relation calls; Meitou's relation call (FactionDeeds.Blow) takes the striker and the victim itself, so its prefix tests the
//       pair directly
//   ArenaRelation / ArenaCombatOutcome (:69-79), relationEvent_hook (_NV_affectRelations :124-143), setEnemy_hook (_NV_setEnemy :144-148) ...
//       prefix on public FactionDeeds.Blow (Relations.cs:150): skipped for a sanctioned pair (every faction event and the squad memory of the blow;
//       the C++'s collateral debug line); the squad-memory skip of task 3.2b (FactionDeeds.Remember) stays for the player arena
//   setCrime_hook (BountyManager::setCrime for assault / VIP assault / murder, :94-114) ... prefix on public CrimeSystem.SetPending (CrimeSystem.cs:117,
//       the game's pending-crime setter FUN_140852c80, the C++'s setCrime): skipped for CrimeCode.Assault (5), Treason (6) or Murder (4) between a sanctioned
//       culprit and victim, whoever calls it (an attack order's Assault, a landed hit's Hit, the stealth knock-out's swing); VIP assault = Treason (6),
//       which nothing in Meitou 0.17.3 sets. The C++'s collateral debug line with the crime code
//   campaignAttack_hook (notifyTheCampaignOfAnAttack :120-123) ... none: Meitou has no campaign notice from a blow (CAP hooks table)
//   outsiders join (isEnemy / isAlly / isFightingAnAllyOfMine / attackTarget / iShouldntAggravateThisTarget for an isolated pair, :156-265) ...
//       task 3.2b's BestAttackerOf / FindFoe postfixes over the view, whose town fighters are TownArena::IsFighter's list (TownArena.CopyFighters)
//   IsScratchMedicActor / BlockTownBystander (ArenaMedical.cpp:44-55) ... TownCombatRules.IsScratchMedic below / CombatMatchView.BlocksTownBystander
//   shouldIHelpThisGuy_hook (:57-62) ... postfix on private FirstAidSystem.FindPatient (FirstAidSystem.Finder.cs:381, the one medic finder): a town
//       bout fighter found by a non-medic while the town owns the match is dropped (no patient this look)
//   pickupObject_hook (:64-71) ... task 3.2b's prefix on public CarrySystem.BeginPickUp (CarrySystem.cs:545): CombatMatchView.RefusesPickUp adds
//       BlockTownBystander, and its own-carrier exemption takes a Scratch medic lifting a town fighter
//   BlockFor's town half (ArenaMedical.cpp:159-160, the third caller of BlockTownBystander) ... ArenaMedicalLogic.BlockFor in task 4.7's
//       FirstAidSystem.Apply prefix (src/Game/ArenaMedical.cs)
//   (port only, M7-town-core.md task 7.4 "after the bout") ... TownBoutForgiveness (Core/Glue/TownArenaLogic.Rules.cs) bound below: the TEMP_ENEMY tags
//       a fight between the player's squad and the town wrote are cleared (SquadMemory.ClearShort, TEMP_ENEMY only) when the town event ends:
//       TownArenaLogic.Finish (ITownHost.TownRulesEventEnded) after the aftercare, or after the town's own abort (Cancel, CancelForSave, the stop
//       rule; before a save captures). Task 8.4b moved the end from the match's end to Finish: the C++'s IsIncidentPair keeps the pairs exempt
//       through Aftercare (TownArena.cpp:1424-1426), so a TEMP_ENEMY written then is the bout's too
//
// Engine calls (E1): getGameData()->stringID (IsScratchMedicActor) ... PgEngine.RecordIdAt(world, slot); Character::isPlayer ... PgEngine.IsPlayer;
// a side's squads ... PgEngine.FactionSquads; the squad memory's TEMP_ENEMY ... PgEngine.HoldsTempEnemy / ClearTempEnemy.
//
// Threading: BoutStarted / BoutEnded / Clear run on the sim thread's serial steps (TownArena's SparSession callbacks, its AbandonWorldState).
// IsScratchMedic is read by the serial patches (FindPatient, BeginPickUp, FirstAidSystem.Apply) and only when the patient is a town bout fighter.
//
// Callers: src/Town/TownArena.cs (OnMatchStarted, the host's TownRulesEventEnded from Finish, AbandonWorldState), src/Game/CombatHooks.cs and
// src/Game/ArenaMedical.cs (IsScratchMedic).

/// <summary>The engine half of task 7.4's port-only forgiveness (one per TownArena, so per world) and the Scratch medic test the patches share.</summary>
sealed class TownCombatRules
{
    readonly TownBoutForgiveness<CharacterId> forgiveness = new();

    /// <summary>C++: ArenaMedical.cpp:44-48 <c>IsScratchMedicActor</c>: the character in table slot <paramref name="slot"/> is a Scratch medic (record
    /// <c>100-Proving Grounds.mod</c>, TownArenaRuntimePolicy.IsMedic). False for an empty or out-of-table slot. Allocation-free.</summary>
    internal static bool IsScratchMedic(World world, int slot) => TownArenaRuntimePolicy.IsMedic(PgEngine.RecordIdAt(world, slot));

    /// <summary>The town's owned match started (TownArena.OnMatchStarted): records, for the bout's player / town pairs, each squad of the other side
    /// and whether it already held TEMP_ENEMY (TownBoutForgiveness.Begin).</summary>
    internal void BoutStarted(World world, ReadOnlySpan<CharacterId> fighters, ReadOnlySpan<MatchRules.MatchTeam> teams)
    {
        // never throws into SparSession's StartMatch (a throw there undoes the whole start): the clearing is lost for this match, the match starts
        if (forgiveness.TryBegin(new Memory(world), fighters, teams) is { } e)
            ProvingGroundsMod.Ctx?.LogError($"town rules: TEMP_ENEMY record failed at the match start (no after-bout clearing for this match): {e.GetType().Name}: {e.Message}");
    }

    /// <summary>The town event ended (TownArenaLogic.Finish, task 8.4b): clears the TEMP_ENEMY tags the bout wrote between the player's
    /// squad and the town (TownBoutForgiveness.End); one Meitou log line when any was cleared. Nothing recorded: nothing done.</summary>
    /// <paramref name="ownedMatchEnding"/>: the record's own event ends (Finish passes true; false leaves the record, TownBoutForgiveness.End).
    internal void BoutEnded(World world, bool ownedMatchEnding)
    {
        if (!forgiveness.Recorded) return;
        // never throws into its caller (the town's abort runs SparSession.AbortForSave and Finish right after); a throw drops the record
        if (forgiveness.TryEnd(new Memory(world), ownedMatchEnding, out int n) is { } e)
            ProvingGroundsMod.Ctx?.LogError($"town rules: TEMP_ENEMY clearing failed after {n} tag(s), the rest stay: {e.GetType().Name}: {e.Message}");
        else if (n > 0) ProvingGroundsMod.Ctx?.Log($"town rules: cleared {n} TEMP_ENEMY tag(s) the town bout wrote between the player's squad and the town");
    }

    /// <summary>The world is gone (TownArena.AbandonWorldState): forgets the record without touching any world.</summary>
    internal void Clear() => forgiveness.Clear();


    /// <summary>ITempEnemyHost over one world (a struct passed generically: no allocation).</summary>
    readonly struct Memory(World world) : ITempEnemyHost<CharacterId>
    {
        public bool IsPlayer(CharacterId c) => PgEngine.IsPlayer(world, c);
        public void SquadsOf(CharacterId observer, List<int> into) => PgEngine.FactionSquads(world, observer, into);
        public bool HoldsTempEnemy(int squad, CharacterId target) => PgEngine.HoldsTempEnemy(world, squad, target);
        public void ClearTempEnemy(int squad, CharacterId target) => PgEngine.ClearTempEnemy(world, squad, target);
    }
}
