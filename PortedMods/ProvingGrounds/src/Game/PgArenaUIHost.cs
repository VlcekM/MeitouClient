using System.Numerics;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

/// <summary>The Arena window rules' host (task 4.5a): each <see cref="IArenaUIHost{TId}"/> member is one PgEngine adapter method or one call of
/// another subsystem of this world (the E1 table in src/Game/ArenaUISim.cs). Bound to the world of each call and reused; sim thread, serial
/// steps only. Owned by the ArenaUISim of one world.</summary>
sealed class PgArenaUIHost(ArenaUISim.Services services) : IArenaUIHost<CharacterId>
{
    readonly ArenaUISim.Services s = services;
    readonly SiteCheckCache siteCheck = new();
    World? world;

    internal PgArenaUIHost Bind(World w) { world = w; return this; }

    World W => world ?? throw new InvalidOperationException("PgArenaUIHost used before Bind");

    /// <summary>The next site check asks ArenaIngress again (the window opens, Start).</summary>
    internal void ForgetSiteCheck() => siteCheck.Forget();

    // ---------- characters ----------
    public CharacterId None => CharacterId.None;
    public bool IsNone(CharacterId c) => c.IsNone;
    public bool IsValid(CharacterId c) => PgEngine.Exists(W, c);
    public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
    public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
    public string? Name(CharacterId c) => PgEngine.Name(W, c);
    public Vector3? Position(CharacterId c) => PgEngine.Position(W, c);

    /// <summary>C++ <c>getStats()</c> non-null and its card numbers: getOverallSkillLevel_0_100 = PgEngine.OverallSkill (spike S12's stand-in),
    /// toughness() = the stored toughness x Scale (<see cref="CombatStats.ScaledToughness"/>), strengthActual / dexterityActual = the current,
    /// condition-dependent values (task 7.2, src/Game/ArenaCombatProfile.cs Actuals: Meitou's CombatSystem.Eff for those stats).</summary>
    public bool Stats(CharacterId c, out ArenaCardStats stats)
    {
        var st = PgEngine.Stats(W, c);
        if (!st.Valid || !ArenaCombatProfile.Actuals(W, c, out float strength, out float dexterity)) { stats = default; return false; }
        stats = new ArenaCardStats(OverallSkill.Of(st), st.ScaledToughness, strength, dexterity);
        return true;
    }

    /// <summary>C++ <c>RosterStatus::Read</c> (RosterStatus.cpp:9-48) over PgEngine's body parts and blood ratio (Core/RosterStatus.cs decides).</summary>
    public bool Health(CharacterId c, out RosterStatus.Snapshot status)
    {
        var parts = PgEngine.LimbHealth(W, c);
        float ratio = PgEngine.BloodRatio(W, c);
        var anatomy = new RosterStatus.Part?[parts.Length];
        for (int i = 0; i < parts.Length; i++) anatomy[i] = new RosterStatus.Part(parts[i].Flesh, parts[i].Stun, parts[i].MaxHp);
        bool finite = float.IsFinite(ratio);
        return RosterStatus.Read(PgEngine.Exists(W, c), parts.Length > 0, finite ? 1f : 0f, finite ? ratio : 0f, anatomy, out status, out _);
    }

    public float Rating(CharacterId c) => s.Rating(W, c);

    /// <summary>C++ SquadUtil::CollectPlayerSquad (SquadUtil.cpp:22-67): each active player squad's members (PgEngine.PlayerSquads), valid and
    /// not dead, in squad and member order.</summary>
    public void CollectPlayerSquad(List<CharacterId> into)
    {
        foreach (var squad in PgEngine.PlayerSquads(W))
            foreach (var m in squad.Members)
                if (PgEngine.Exists(W, m) && !PgEngine.IsDead(W, m)) into.Add(m);
    }

    // ---------- prisoners (PrisonerUtil, task 6.1) ----------
    public bool IsRosterPrisoner(CharacterId c) => s.Prisoners.IsRosterPrisoner(W, c);
    public bool HasCage(CharacterId c) => s.Prisoners.HasCageFor(W, c);
    public bool MatchIncludesPrisoner() => s.Prisoners.MatchIncludesPrisoner(W);
    public void CollectNearbyCagePrisoners(ArenaBuilding registry, List<CharacterId> into) => s.Prisoners.CollectNearbyCagePrisoners(W, registry, into);
    public IReadOnlyList<CharacterId> MatchPrisoners() => s.Prisoners.GetMatchPrisoners(W);
    public IReadOnlyList<CharacterId> MatchHandlers() => s.Prisoners.GetMatchHandlers(W);
    public bool PrisonersReturning() => s.Prisoners.IsReturning(W);
    public bool PrepareMatch(IReadOnlyList<CharacterId> prisoners, IReadOnlyList<CharacterId> handlers, out string status) =>
        s.Prisoners.PrepareMatch(W, prisoners, handlers, out status);
    public string ReturnAllToCages() { s.Prisoners.ReturnAllToCages(W, out string status); return status; }
    public void ClearMatchState() => s.Prisoners.ClearMatchState(W);

    // ---------- ArenaIngress ----------
    public ArenaBuilding? BoundRegistry() => s.Ingress.GetBoundRegistry(W);
    public void BindRegistry(ArenaBuilding? registry) => s.Ingress.BindRegistry(registry);
    public bool IngressPending() => s.Ingress.IsPending(W);
    public string IngressStatus() => s.Ingress.GetStatus();

    /// <summary>C++ ArenaIngress::HasLocationSite, kept for a second per (location, Registry) (Core/Glue SiteCheckCache; divergences.md).</summary>
    public bool HasLocationSite() =>
        siteCheck.Get(s.Ingress.GetLocationMode(), s.Ingress.Logic.BoundRegistryRef, s.Clock.NowMs, () => s.Ingress.HasLocationSite(W));

    public string MissingLocationMessage() => s.Ingress.GetMissingLocationMessage();
    public ArenaLocationMode LocationMode() => s.Ingress.GetLocationMode();
    public void SetLocationMode(ArenaLocationMode mode) => s.Ingress.SetLocationMode(mode);
    public bool IngressBegin(MatchRules.MatchMode mode, CharacterId[] fighters, MatchRules.MatchTeam[] teams, CharacterId[] escorts) =>
        s.Ingress.Begin(W, mode, fighters, teams, fighters.Length, escorts.Length == 0 ? null : escorts, escorts.Length);
    public void IngressCancel() => s.Ingress.Cancel(W);
    public CharacterId UiOpener() => s.Ingress.GetUiOpener(W);

    // ---------- SparSession ----------
    public bool SparActive() => s.Spar().IsActive(W);
    public string SparStatus() => s.Spar().GetStatus();
    public void SparStopManual() => s.Spar().Stop(W, SparStopReason.Manual);

    // ---------- TownArena (task 7.1) ----------
    public bool TownBusy() => s.Town.IsBusy(W);
    public bool TownHasBooking() => s.Town.HasBooking(W);
    public void TownCancel() => s.Town.Cancel(W);

    // ---------- ArenaMedical (task 4.7: the process-wide protocol; SetProtocol is locked while a match runs) ----------
    public ArenaMedicalPolicy.Protocol GetProtocol() => ArenaMedical.GetProtocol();
    public void SetProtocol(ArenaMedicalPolicy.Protocol protocol) => s.Medical.SetProtocol(W, protocol);

    // ---------- other windows ----------
    public void ShowRecruitment() => s.Recruitment.Show(W);   // task 6.4a (the sim half; the window half, task 6.4b, is src/Ui/PrisonerRecruitmentUI.cs)
    public void TownBookieClose() => s.Bookie.Close(W);        // task 8.1a (src/Town/TownBookie.cs)
    public void RecruitmentAbandon() => s.Recruitment.AbandonWorldState(W);
    public void TownArenaShow(ArenaBuilding registry, bool bindRegistry) => s.TownUi.Show(W, registry, bindRegistry);   // task 7.5 (the sim half; the window is task 7.6)
    public void TownArenaClose() => s.TownUi.Close(W);
    public void OtherWindowsAbandon()
    {
        // C++ ArenaUI.cpp:3199-3202, in order
        s.Marks.AbandonWorldState(W);
        s.TownUi.AbandonWorldState(W);
        s.Rewards.AbandonWorldState(W);
        s.Recruitment.AbandonWorldState(W);
    }
}
