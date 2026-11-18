using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of upstream/ProvingGrounds/plugin/src/PrisonerRecruitment.h/.cpp (Proving Grounds by Shutori, GPLv3), task 6.4a (PA §2.19, CAP 15). Every
// function and helper is Core/Glue/PrisonerRecruitmentGlue.cs (fake-tested, Tests/Glue/PrisonerRecruitmentGlueTests.cs); the recruit is
// src/Engine/Recruiting.cs. This file binds the logic to a world and to CharacterIds. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial
// steps only (the recruitment window's RecruitPrisoner command and the test verb run in Inputs).
//
// Engine calls of the C++ and what replaces them (E1; IPrisonerRecruitmentHost member -> adapter method):
//   prisoner (non-null), isValid(), isDead(), isUnconcious(), getName() .. CharacterId.IsNone / PgEngine.Exists / IsDead / IsUnconscious / Name
//   getStats()->getOverallSkillLevel_0_100() (:58) .................. PgEngine.Stats + OverallSkill.Of (spike S12's stand-in; Valid false = no stats)
//   PrisonerUtil::IsRosterPrisoner / HasRuntimeActivity / ForgetRosterPrisoner (:75, :81, :126) .. src/Game/PrisonerUtil.cs (task 6.1)
//   ArenaIngress::IsPending (:80) .................................... src/Game/ArenaIngress.cs (task 4.3b)
//   SparSession::IsActive (:80) ...................................... src/Game/SparSession.cs (task 3.3)
//   ou && ou->player (:91) ........................................... Recruiting.Ready (the DialogueSystem and a living player character)
//   LeaderboardStore::GetMarks / ReserveProgression (:98, :104) ...... src/Game/LeaderboardStore.cs (task 5.1)
//   hand actor; actor.getCharacter() == prisoner; FighterIdentity::Find (RecruitmentPayment::Matches :39-44) .. PgEngine.Exists (the id's
//       generation is the handle) + FighterIdentity.Find (task 2.3)
//   ou->player->recruit(prisoner, false) (RecruitAction::Apply :50), setPrisonMode(false) (:120) .. Recruiting.Recruit (release, then
//       DialogueSystem.JoinPlayer; part Recruitment); any exception there is a refused recruit (the guarded-write pattern of
//       src/Game/PrisonerUtil.Return.cs: logged once)
//   setChainedMode(false, hand) (:121-125) ........................... nothing (task 6.2's divergence: no chained flag in Meitou)
//   PGLog::Debug (:133-134) .......................................... PGLog.Debug
//
// Callers (C++ -> port): PrisonerRecruitmentUI.cpp:246-248, :266-269 (AverageCombatStat, RequiredMarks), :470 (Recruit): the recruitment window's
// sim half, src/Game/PrisonerRecruitmentUISim.cs.

/// <summary>PrisonerRecruitment of one world (task 6.4a): one object, owned by ProvingGroundsSystem. Each call binds the world and runs the C++
/// function in <see cref="PrisonerRecruitmentGlue{TId}"/>.</summary>
sealed class PrisonerRecruitment
{
    readonly Host host;
    readonly PrisonerRecruitmentGlue<CharacterId> logic;

    internal PrisonerRecruitment(LeaderboardStore store, FighterIdentity identity, PrisonerUtil prisoners, ArenaIngress ingress, Func<SparSession> spar,
        PopulationData data)
    {
        host = new Host(store, identity, prisoners, ingress, spar, data);
        logic = new PrisonerRecruitmentGlue<CharacterId>(host);
    }

    PrisonerRecruitmentGlue<CharacterId> L(World world) { host.World = world; return logic; }

    /// <summary>The recruit writes that have thrown in this process (each kind logged once): read as a refused recruit.</summary>
    static readonly HashSet<string> failedWrites = new(StringComparer.Ordinal);

    /// <summary>IPrisonerRecruitmentHost bound to the world of the current call (every call binds it first and finishes inside that serial step).</summary>
    sealed class Host(LeaderboardStore store, FighterIdentity identity, PrisonerUtil prisoners, ArenaIngress ingress, Func<SparSession> spar,
        PopulationData data) : IPrisonerRecruitmentHost<CharacterId>
    {
        internal World? World;
        World W => World ?? throw new InvalidOperationException("PrisonerRecruitment used before a world was bound");

        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool TryOverallSkill(CharacterId c, out float skill)
        {
            var stats = PgEngine.Stats(W, c);   // C++ getStats(): null = CombatStats.None (Valid false)
            skill = stats.Valid ? OverallSkill.Of(stats) : 0f;
            return stats.Valid;
        }
        public bool IsRosterPrisoner(CharacterId c) => prisoners.IsRosterPrisoner(W, c);
        public bool IngressPending() => ingress.IsPending(W);
        public bool SparActive() => spar().IsActive(W);
        public bool PrisonerRuntimeActivity() => prisoners.HasRuntimeActivity(W);
        public bool PlayerReady() => Recruiting.Ready(W);
        public bool RecruitAvailable() => Recruiting.Available;
        public int GetMarks(CharacterId c) => store.GetMarks(W, c);
        public bool ReserveProgression(CharacterId c, ArenaLedger.Purchase purchase, RewardTransaction.ReservedAccounting accounting) =>
            store.ReserveProgression(W, c, purchase, accounting);
        public bool IdentityMatches(CharacterId c, string token) =>
            !c.IsNone && PgEngine.Exists(W, c) && identity.Find(W, c, out string current) && current == token;
        public bool Recruit(CharacterId c)
        {
            RecruitOutcome outcome;
            try { outcome = Recruiting.Recruit(W, data, c); }   // the release and the join read back after a throw (RecruitingRules)
            catch (Exception e)   // a read before anything was moved (the cap, the talker): nothing changed
            {
                if (failedWrites.Add("recruit"))
                    ProvingGroundsMod.Ctx?.LogError($"prisoner recruitment: the recruit failed (read as refused; logged once): {e.GetType().Name}: {e.Message}");
                return false;
            }
            // the join's result not known: the recruitment's recovery reads it again (JoinedPlayer) and never refunds a recruit that may have happened
            if (outcome == RecruitOutcome.JoinThrewUnknown) throw new InvalidOperationException("the join threw and its result could not be read");
            if (outcome != RecruitOutcome.Joined)
                PGLog.Debug("Proving Grounds: prisoner recruitment: the recruit was " + (RecruitingRules.IsJoined(outcome) ? "done" : "refused") + " (" + outcome + ")");
            return RecruitingRules.IsJoined(outcome);
        }
        public bool JoinedPlayer(CharacterId c) => PgEngine.IsPlayer(W, c);
        public void ForgetRosterPrisoner(CharacterId c) => prisoners.ForgetRosterPrisoner(W, c);
        public string Name(CharacterId c) => c.IsNone ? "" : PgEngine.Name(W, c) ?? "";
        public void Debug(string line) => PGLog.Debug(line);
        public void Error(string line) => ModLog.Error(line);
    }

    // ---------- the public functions (PrisonerRecruitment.h:9-11) ----------

    // C++: PrisonerRecruitment.cpp:54-59
    internal float AverageCombatStat(World world, CharacterId prisoner) => L(world).AverageCombatStat(prisoner);
    // C++: PrisonerRecruitment.cpp:61-65
    internal int RequiredMarks(World world, CharacterId prisoner) => L(world).RequiredMarks(prisoner);
    // C++: PrisonerRecruitment.cpp:67-136
    internal bool Recruit(World world, CharacterId prisoner, out string status) => L(world).Recruit(prisoner, out status);
}
