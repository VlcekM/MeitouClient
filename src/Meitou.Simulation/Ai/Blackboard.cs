using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The package classes the engine builds, from the package's <c>signal func</c> (docs/game/ai.md "Signal functions and the package classes", Verified
/// mapping). <see cref="Retreat"/> follows docs/game/perception.md 7.3 (Observed); the trader, the passing-by assault and the occupation follow their
/// decoded classes (docs/game/ai.md "Package classes"); the rest are engine choices (docs/simulation.md "AI", Package classes).
/// </summary>
public enum PackageClass : byte
{
    /// <summary>
    /// The base <c>AIPackage</c> (signals 0, 2, 3, 7, 8, 9, 18, 21): can always start, never finishes.
    /// </summary>
    Plain = 0,
    /// <summary>
    /// <c>RetreatAndRefresh</c> (5): starts when the squad is not intact, finishes when it is.
    /// </summary>
    Retreat = 1,
    /// <summary>
    /// <c>GoOutOnPatrol</c> (6) and <c>GoOutOnPatrol_Cannibal</c> (4): a walk round the outskirts of the home town every few hours.
    /// </summary>
    Patrol = 2,
    /// <summary>
    /// <c>GoOutOnPatrol_Running</c> (23): the same at a run.
    /// </summary>
    PatrolRunning = 3,
    /// <summary>
    /// <c>Package_HangAroundBar</c> with argument 0 (17): the squad's home becomes the town's bar.
    /// </summary>
    Bar = 4,
    /// <summary>
    /// <c>Package_HangAroundBar</c> with argument 1 (26, outdoors only): the squad stays out of doors.
    /// </summary>
    BarOutdoors = 5,
    /// <summary>
    /// The contract classes (12 to 15, 33): started only as a contract (<see cref="StartContract"/>), finished when its hours are over.
    /// </summary>
    Contract = 6,
    /// <summary>
    /// Wars and campaigns (19, 20, 32, 31: the campaign drives its squads, <see cref="PopulationSystem"/>) and the rest of the unread classes: not
    /// built; never start (except as a lone slot-0 package), never finish.
    /// </summary>
    Unbuilt = 7,
    /// <summary>
    /// <c>CannibalRetreatFromRaid</c> (1) and <c>CannibalRetreatFromRaid_nearestNest</c> (25): back to the mission town while someone carries
    /// (AiSystem.Captives.cs).
    /// </summary>
    CannibalRetreat = 8,
    /// <summary>
    /// <c>Package_CannibalsAtHome</c> (27): while the squad is in its home town.
    /// </summary>
    CannibalsAtHome = 9,
    /// <summary>
    /// <c>PrisonerGathering</c> (10) and <c>PrisonerGathering_unholy</c> (24): while a town near the leader holds prisoners to gather.
    /// </summary>
    PrisonerGathering = 10,
    /// <summary>
    /// <c>SlaverPrisonerShipping_Camps</c> (11) and <c>_AnywhereTownsIncluded</c> (28): while the squad has slaves, to its mission town.
    /// </summary>
    SlaverShipping = 11,
    /// <summary>
    /// <c>Package_WanderingTrader</c> (16): to a town the chooser draws, a stay there, then on (docs/game/ai.md "Package classes", Verified by code).
    /// </summary>
    WanderingTrader = 12,
    /// <summary>
    /// <c>Package_PassingByTownAssault</c> (29): attacks a player town its leader walks into while the player's people are about.
    /// </summary>
    PassingAssault = 13,
    /// <summary>
    /// <c>Package_OccupyTown</c> (30): stays in the player town it took (only by <c>Leads to</c>: it can never start of itself).
    /// </summary>
    OccupyTown = 14,
    /// <summary>
    /// <c>Package_Refugee</c> (22): a broken or tiny squad flees to the nearest other town of its faction and joins a healthy squad there
    /// (docs/game/ai.md "Package classes", Verified by code).
    /// </summary>
    Refugee = 15,
}

/// <summary>
/// The <see cref="PackageClass"/> of each package signal.
/// </summary>
public static class PackageClasses
{
    /// <summary>
    /// The class of package signal <paramref name="signal"/>.
    /// </summary>
    public static PackageClass Of(int signal) => signal switch
    {
        0 or 2 or 3 or 7 or 8 or 9 or 18 or 21 => PackageClass.Plain,
        5 => PackageClass.Retreat,
        4 or 6 => PackageClass.Patrol,
        23 => PackageClass.PatrolRunning,
        17 => PackageClass.Bar,
        26 => PackageClass.BarOutdoors,
        12 or 13 or 14 or 15 or 33 => PackageClass.Contract,
        1 or 25 => PackageClass.CannibalRetreat,
        27 => PackageClass.CannibalsAtHome,
        10 or 24 => PackageClass.PrisonerGathering,
        11 or 28 => PackageClass.SlaverShipping,
        16 => PackageClass.WanderingTrader,
        29 => PackageClass.PassingAssault,
        30 => PackageClass.OccupyTown,
        22 => PackageClass.Refugee,
        _ => PackageClass.Unbuilt,
    };
}

/// <summary>
/// What a package of a class needs from the world when the blackboard chooses (<see cref="AiSystem"/> gives it; null: the base-class rules only).
/// </summary>
public interface IPackageRules
{
    /// <summary>
    /// Whether a package of the class may start now (the original's package start test).
    /// </summary>
    bool CanStart(Blackboard board, AiPackage package, PackageClass kind);
    /// <summary>
    /// Whether the running package of the class is finished (the original's finished test).
    /// </summary>
    bool Finished(Blackboard board, AiPackage package, PackageClass kind);

    /// <summary>
    /// A package of the class just started (a patrol lays out its route, a bar package moves the home).
    /// </summary>
    void Started(Blackboard board, AiPackage package, PackageClass kind);

    /// <summary>
    /// The running package gives way to another (the original's deactivate, package vtable +0x38).
    /// </summary>
    void Ended(Blackboard board, AiPackage package, PackageClass kind) { }

    /// <summary>
    /// A finished package may hand over to its <c>Leads to</c> (the original's vtable +0x90; the base class always may).
    /// </summary>
    bool MayLead(Blackboard board, AiPackage package, PackageClass kind) => true;
}

/// <summary>
/// A guarded gate: its door leaf's centre at the leaf's foot, the unit X/Z way through it pointing away from the town, and half the leaf's width.
/// </summary>
public readonly record struct GatePost(TownBuilding Building, Vector3 Door, Vector2 Out, float HalfWidth);

/// <summary>
/// A seat a member sitting around claimed: the piece <paramref name="Index"/> (<see cref="TownBuilding.SeatAt"/>) of <paramref name="Building"/>.
/// </summary>
public readonly record struct SeatClaim(TownBuilding Building, int Index)
{
    /// <summary>
    /// The seat piece the claim names.
    /// </summary>
    public HomeSpot Spot => Building.SeatAt(Index);
}

/// <summary>
/// A member's entry in the crowding count: who acts out which goal type, aimed at whom (<see cref="AiSystem.CrowdTarget"/>).
/// </summary>
public readonly record struct CrowdEntry(CharacterId Who, TaskType Type, CharacterId Target);

/// <summary>
/// A squad's AI state (docs/game/ai.md "Blackboard update"): its packages by slot, the package running and the role lists it loaded, a contract
/// package that overrides the slots, and what the running package needs: a home building, a patrol route. Changed only in <see cref="AiSystem"/>'s
/// serial step; the members' think reads it.
/// </summary>
public sealed class Blackboard
{
    /// <summary>
    /// The empty role lists of a board with no package running.
    /// </summary>
    static readonly IReadOnlyList<AiGoal>[] NoRoles = [[], [], [], [], []];

    /// <summary>
    /// The squad's packages by slot, lowest slot first.
    /// </summary>
    readonly SortedDictionary<int, List<AiPackage>> _slots = [];

    /// <summary>
    /// Makes the board with the template's packages by slot; no package runs until the first <see cref="Update"/>.
    /// </summary>
    public Blackboard(IEnumerable<SlottedPackage> packages)
    {
        foreach (var (slot, package) in packages)
        {
            if (!_slots.TryGetValue(slot, out var list)) _slots[slot] = list = [];
            list.Add(package);
        }
    }

    /// <summary>
    /// The package running (null: none could start; the members take only their default goals).
    /// </summary>
    public AiPackage? Current { get; private set; }
    /// <summary>
    /// The class of the running package (<see cref="PackageClass.Plain"/> without one).
    /// </summary>
    public PackageClass CurrentClass => Current is { } p ? PackageClasses.Of(p.Signal) : PackageClass.Plain;
    /// <summary>
    /// The slot of the running package (-1: none, or a contract).
    /// </summary>
    public int CurrentSlot { get; private set; } = -1;

    /// <summary>
    /// The current package's place in its slot's list (-1: none, or a contract).
    /// </summary>
    public int CurrentIndex { get; private set; } = -1;

    /// <summary>
    /// Raised whenever another package starts, so the members choose again at once.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// The current package's goals per <see cref="AiRole"/>.
    /// </summary>
    public IReadOnlyList<AiGoal>[] Roles { get; private set; } = NoRoles;

    /// <summary>
    /// How many members act out each goal type (the crowding count of the original's <c>TaskInfo</c>), as counted on the last package cadence.
    /// </summary>
    public Dictionary<TaskType, int> Crowd { get; } = [];

    /// <summary>
    /// Each member's current goal as the original's <c>TaskInfo</c> entry holds it (character, goal type, goal target), as counted on the last
    /// package cadence.
    /// </summary>
    public List<CrowdEntry> CrowdEntries { get; } = [];

    /// <summary>
    /// The crowding count of a goal for <paramref name="asker"/> (<c>FUN_140268f30</c>, docs/game/ai-tasks.md "Scoring"): the other members whose
    /// entry has the goal's type and is not the goal's target character, and, for a row that needs a target, whose entry target is the same.
    /// </summary>
    public int CrowdOf(TaskType type, bool needsTarget, CharacterId target, CharacterId asker)
    {
        int n = 0;
        foreach (var e in CrowdEntries)
            if (e.Who != asker && e.Type == type && e.Who != target && (!needsTarget || e.Target == target)) n++;
        return n;
    }

    /// <summary>
    /// All packages in slot order (highest first), as listed.
    /// </summary>
    public IEnumerable<SlottedPackage> Packages => _slots.Reverse().SelectMany(s => s.Value.Select(p => new SlottedPackage(s.Key, p)));

    /// <summary>
    /// A contract package (the original's <c>contractjob</c>, set by dialogue's <c>AI contract</c>): while it runs it takes precedence over every
    /// slot.
    /// </summary>
    public AiPackage? Contract { get; private set; }

    /// <summary>
    /// The game hours (<see cref="World.GameHours"/>) the contract ends at.
    /// </summary>
    public double ContractEnd { get; private set; }

    /// <summary>
    /// The town the squad belongs to (a site index, -1: none) and the building it lives in (null: none known); <see cref="OwnHome"/> is the one it
    /// was given, <see cref="Home"/> the one in use (a bar package moves it to the bar).
    /// </summary>
    public int Town { get; init; } = -1;

    /// <summary>
    /// The town's turrets (<see cref="PopulationData.Turrets"/>) and the turret index each member's slot claimed to man, as the last serial step
    /// found them (derived, not hashed).
    /// </summary>
    public IReadOnlyList<TownBuilding> Turrets { get; set; } = [];
    /// <summary>
    /// The turret index each member's slot claimed to man (see <see cref="Turrets"/>; derived, not hashed).
    /// </summary>
    public Dictionary<int, int> TurretClaims { get; } = [];

    /// <summary>
    /// The turret finder's factor for each claim (<see cref="AiSystem"/>'s <c>TurretFactor</c>: 1 near, 0.3 far; in melee mode 0.8 and 0.05), as the
    /// last serial step found it (derived, not hashed).
    /// </summary>
    public Dictionary<int, float> TurretFactors { get; } = [];

    /// <summary>
    /// The seat of the bar (<see cref="Home"/> under a bar package) each relaxing member's slot claimed, as the last serial step found it (derived,
    /// not hashed).
    /// </summary>
    public Dictionary<int, HomeSpot> SeatClaims { get; } = [];

    /// <summary>
    /// The furniture each member sitting around (275) claimed (a seat of <see cref="TownBuilding.SeatAt"/> numbering, in any building the finder
    /// allows), as the last serial step found it (derived, not hashed).
    /// </summary>
    public Dictionary<int, SeatClaim> SitClaims { get; } = [];

    /// <summary>
    /// The gate the squad guards (its home when that is a gate, else for a gate guard goal the town's gate nearest the squad), as the last serial
    /// step found it (derived, not hashed); null: none, or its door's place not known.
    /// </summary>
    public GatePost? Gate { get; set; }

    /// <summary>
    /// The home the gate guard post was first resolved to (the blackboard's HOME_GATE hand, <c>FUN_14026b410</c>): taken from <see cref="OwnHome"/>
    /// once it is set, then kept even when the home changes.
    /// </summary>
    public TownBuilding? GateHome { get; set; }
    /// <summary>
    /// The home building the squad was given (see <see cref="Town"/>); <see cref="Home"/> is the one in use.
    /// </summary>
    public TownBuilding? OwnHome { get; set; }

    /// <summary>
    /// The template's <c>public day</c>, which the original writes over <see cref="OwnHome"/>'s public flag (docs/game/locks.md "Data"): what
    /// trespassing reads.
    /// </summary>
    public bool HomePublic { get; init; } = true;
    /// <summary>
    /// The home building in use: <see cref="OwnHome"/>, or the bar a bar package moved it to.
    /// </summary>
    public TownBuilding? Home { get; set; }

    /// <summary>
    /// The table a bar package reserved for the squad: an index into <see cref="Home"/>'s <see cref="TownBuilding.Tables"/>, 0 for an outdoor table
    /// (the home itself); -1: none.
    /// </summary>
    public int BarTable { get; set; } = -1;

    /// <summary>
    /// The members it was made with: the squad size of the intact rule.
    /// </summary>
    public int Size { get; set; }

    /// <summary>
    /// The squad is intact (docs/game/perception.md 7.3), as the last serial step found it.
    /// </summary>
    public bool Intact { get; set; } = true;

    /// <summary>
    /// A patrol's route (world X/Z) and the point the leader walks to next; empty when no patrol runs.
    /// </summary>
    public Vector2[] Route { get; set; } = [];
    /// <summary>
    /// The index in <see cref="Route"/> of the point the leader walks to next.
    /// </summary>
    public int RouteIndex { get; set; }

    /// <summary>
    /// The game hours from which the next patrol may start, and when the running one gives up.
    /// </summary>
    public double NextPatrol { get; set; }
    /// <summary>
    /// The game hours at which the running patrol gives up (see <see cref="NextPatrol"/>).
    /// </summary>
    public double PatrolGiveUp { get; set; }

    /// <summary>
    /// The town (site index) a retreat or slave shipping package goes to, set when it starts; -1: none.
    /// </summary>
    public int MissionTown { get; set; } = -1;

    /// <summary>
    /// The town the squad is in (the original's Blackboard +0x138, docs/game/ai.md "Package classes": its leader's town, else the town whose radius
    /// holds the squad) and the game hour it came in (+0x140); <see cref="CampaignTown.None"/> outside every town. Set on the package cadence before
    /// the choice.
    /// </summary>
    public CampaignTown InTown { get; set; } = CampaignTown.None;
    /// <summary>
    /// The game hour the squad came into <see cref="InTown"/> (the original's +0x140).
    /// </summary>
    public double InTownSince { get; set; }

    /// <summary>
    /// The running package's mission town (its <c>mission town</c> hand): the trader's next town, the assaulted or occupied player town; none for
    /// other classes.
    /// </summary>
    public CampaignTown Destination { get; set; } = CampaignTown.None;

    /// <summary>
    /// The trader's state (<c>Package_WanderingTrader</c> +0xe0: 0 on the way, 1 just in the town, 2 done there) and the hours it stays in a town
    /// before it may go on (+0xd4, uniform [5, 8) at each start). Kept while other packages run, as the original's package object.
    /// </summary>
    public int TraderState { get; set; }
    /// <summary>
    /// The hours the trader stays in a town before it may go on (+0xd4, uniform [5, 8) at each start; see <see cref="TraderState"/>).
    /// </summary>
    public float TraderWait { get; set; }

    /// <summary>
    /// The passing-by assault (<c>Package_PassingByTownAssault</c>): the game hour it started (+0xb8), the faction it attacks (+0x100, the town's),
    /// whether it is spent (+0xb4: it never starts again) and its faction's wait after the last such assault ends (+0xb0, hours).
    /// </summary>
    public double AssaultStart { get; set; }
    /// <summary>
    /// The faction the passing-by assault attacks (+0x100, the town's; -1: none).
    /// </summary>
    public int AssaultEnemy { get; set; } = -1;
    /// <summary>
    /// The passing-by assault is spent (+0xb4): it never starts again.
    /// </summary>
    public bool AssaultDone { get; set; }
    /// <summary>
    /// The faction's wait after the last passing-by assault ends, in hours (+0xb0).
    /// </summary>
    public float AssaultCooldown { get; set; }

    /// <summary>
    /// The passing-by assault's target set (+0xd8): the town faction's people within <see cref="AiSystem.AssaultReach"/> of the town at the start,
    /// plus every character its members sense as enemies (<c>AggroSystem.AssaultTargetEffect</c>). Nothing leaves it while the package runs
    /// (<c>FUN_1402900c0</c>, <c>FUN_1402903c0</c>, <c>FUN_140294980</c>). Not saved or hashed: a loaded assault seeds it again from the start rule
    /// (<see cref="AssaultSeeded"/>) and refills from the scan.
    /// </summary>
    public HashSet<CharacterId> AssaultTargets { get; } = [];
    /// <summary>
    /// Whether <see cref="AssaultTargets"/> was seeded from the start rule since the assault started or was loaded.
    /// </summary>
    public bool AssaultSeeded { get; set; }

    /// <summary>
    /// The occupation's clock (<c>Package_OccupyTown</c> +0xc0): game seconds in a row with no able member in the town; past 4 it ends.
    /// </summary>
    public float OccupyAway { get; set; }

    /// <summary>
    /// The refugee package (<c>Package_Refugee</c>): the squad was not intact when it started (+0xb0), and the squad it makes for to join (its
    /// mission target, +0x28; -1: none yet).
    /// </summary>
    public bool RefugeeBroken { get; set; }
    /// <summary>
    /// The squad the refugee package makes for to join (its mission target, +0x28; -1: none yet).
    /// </summary>
    public int RefugeeTarget { get; set; } = -1;

    /// <summary>
    /// One pass of the original's package choice (docs/game/ai.md "Blackboard update", Verified): a running contract first; then slots from the
    /// highest down until one reports success. In a slot the list is scanned in order: the current package stays while it is not finished, or hands
    /// over to its <c>Leads to</c> package when it is; an entry before it that can start pre-empts it; a lone slot-0 package starts even when it
    /// cannot. Without <paramref name="rules"/> every package keeps the base class's rules except that only plain packages can start.
    /// </summary>
    public void Update(IPackageRules? rules = null, double hours = 0)
    {
        if (Contract is { } contract)
        {
            if (hours < ContractEnd) return;
            _ended = (contract, MissionTarget);
            Contract = null;
            MissionTarget = CharacterId.None;
            Current = null;   // the slots choose again
        }

        foreach (var (slot, list) in _slots.Reverse())
            if (TrySlot(slot, list, rules)) return;
    }

    /// <summary>
    /// Tries the packages of one slot: starts or keeps one that can run; true when the slot's package runs.
    /// </summary>
    bool TrySlot(int slot, List<AiPackage> list, IPackageRules? rules)
    {
        if (slot == 0 && list.Count == 1)
        {
            if (Current != list[0]) Start(list[0], 0, rules);
            else if (Finished(list[0], rules))
            {
                // Finished and alone: its Leads to, else itself again once it can start (a patrol's next round).
                if (list[0].LeadsTo is { } hop && Find(hop) is { } follow && MayLead(list[0], rules)) Start(follow.Package, follow.Slot, rules);
                else if (CanStart(list[0], rules)) Start(list[0], 0, rules);
            }

            return true;
        }

        foreach (var p in list)
        {
            if (p == Current)
            {
                if (!Finished(p, rules)) return true;
                if (p.LeadsTo is { } next && Find(next) is { } follow && MayLead(p, rules))
                {
                    Start(follow.Package, follow.Slot, rules);
                    return true;
                }

                return false;
            }

            if (CanStart(p, rules))
            {
                Start(p, slot, rules);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether package <paramref name="p"/> may start: a plain one always, another by the class rules (none given: never).
    /// </summary>
    bool CanStart(AiPackage p, IPackageRules? rules)
    {
        var kind = PackageClasses.Of(p.Signal);
        return kind == PackageClass.Plain || (rules?.CanStart(this, p, kind) ?? false);
    }

    /// <summary>
    /// Whether package <paramref name="p"/> is finished: never a plain one, another by the class rules (none given: never).
    /// </summary>
    bool Finished(AiPackage p, IPackageRules? rules)
    {
        var kind = PackageClasses.Of(p.Signal);
        return kind != PackageClass.Plain && (rules?.Finished(this, p, kind) ?? false);
    }

    /// <summary>
    /// Whether finished package <paramref name="p"/> may hand over to its <c>Leads to</c> (no rules given: yes).
    /// </summary>
    bool MayLead(AiPackage p, IPackageRules? rules) => rules?.MayLead(this, p, PackageClasses.Of(p.Signal)) ?? true;

    /// <summary>
    /// The package with id <paramref name="id"/> and its slot; null when the board has none.
    /// </summary>
    SlottedPackage? Find(string id)
    {
        foreach (var (slot, list) in _slots)
        {
            foreach (var p in list)
                if (p.Id == id) return new SlottedPackage(slot, p);
        }

        return null;
    }

    /// <summary>
    /// Starts a package in <paramref name="slot"/> (-1: a contract): ends the one it replaces, loads its roles, bumps the version, resets the route,
    /// home, bar table, mission town, destination and refugee state, then tells the rules it started.
    /// </summary>
    void Start(AiPackage package, int slot, IPackageRules? rules)
    {
        if (Current is { } old && old != package) rules?.Ended(this, old, PackageClasses.Of(old.Signal));
        Current = package;
        CurrentSlot = slot;
        CurrentIndex = slot >= 0 && _slots.TryGetValue(slot, out var list) ? list.IndexOf(package) : -1;
        Roles = package.Roles;
        Version++;
        Route = [];
        RouteIndex = 0;
        Home = OwnHome;
        BarTable = -1;
        MissionTown = -1;
        Destination = CampaignTown.None;
        RefugeeBroken = false;
        RefugeeTarget = -1;
        rules?.Started(this, package, PackageClasses.Of(package.Signal));
    }

    /// <summary>
    /// Makes the members think again at once (a patrol moved on).
    /// </summary>
    public void Touch() => Version++;

    /// <summary>
    /// Starts a contract package that runs for <paramref name="hours"/> game hours from <paramref name="now"/> (docs/game/ai.md "Dialogue and AI":
    /// under 1 hour means 10 minutes, Observed), aimed at <paramref name="target"/> (its goals aimed at the squad's mission, as a bodyguard's, follow
    /// it). It takes precedence over the slots until it ends; then the slots choose again. Starting the contract already running does nothing (end
    /// time and target kept); a different one replaces it and the old one ends first, its end talk to the old target (<c>FUN_140272240</c>,
    /// <c>FUN_14026a110</c>, <b>Verified</b>).
    /// </summary>
    public void StartContract(AiPackage package, double now, double hours, IPackageRules? rules = null, CharacterId? target = null)
    {
        if (Contract is { } running)
        {
            if (running.Id == package.Id) return;
            _ended = (running, MissionTarget);
        }

        Contract = package;
        ContractEnd = now + (hours < 1 ? 10.0 / 60 : hours);
        MissionTarget = target ?? CharacterId.None;
        Start(package, -1, rules);
    }

    /// <summary>
    /// Makes a package of the slots the current one again without its start (the original keeps the running package object on its platoon while the
    /// squad is out of the world); false when no slot lists it.
    /// </summary>
    public bool Resume(AiPackage package)
    {
        foreach (var (slot, list) in _slots)
        {
            if (list.Contains(package))
            {
                Start(package, slot, null);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A saved contract (<see cref="Saving.AiSave"/>) running again until <paramref name="end"/>, with no target.
    /// </summary>
    public void ResumeContract(AiPackage package, double end, IPackageRules? rules = null)
    {
        Contract = package;
        ContractEnd = end;
        MissionTarget = CharacterId.None;
        Start(package, -1, rules);
    }

    /// <summary>
    /// The package <c>change AI</c> put in place of the template's (saved as <c>replacementAI</c>); null: the template's.
    /// </summary>
    public AiPackage? Replacement { get; private set; }

    /// <summary>
    /// The squad's mission target: whom its running contract is aimed at (none without one).
    /// </summary>
    public CharacterId MissionTarget { get; private set; } = CharacterId.None;

    /// <summary>
    /// The contract that ended on the last <see cref="Update"/> and whom it was aimed at, until <see cref="TakeEnded"/> takes it.
    /// </summary>
    (AiPackage Package, CharacterId Target)? _ended;

    /// <summary>
    /// The contract that ended since the last call (the AI fires the leader's <c>contract end talk passive</c>, event 30), or null.
    /// </summary>
    public (AiPackage Package, CharacterId Target)? TakeEnded()
    {
        var e = _ended;
        _ended = null;
        return e;
    }

    /// <summary>
    /// Dialogue's <c>change AI</c> (<c>FUN_140272430</c>): the squad's packages become <paramref name="package"/> alone and it starts at once (every
    /// member chooses again). A running contract keeps running.
    /// </summary>
    public void Replace(AiPackage package, IPackageRules? rules = null)
    {
        Replacement = package;
        _slots.Clear();
        _slots[0] = [package];
        if (Contract is null) Start(package, 0, rules);
        else Version++;
    }

    /// <summary>
    /// Adds the board's state to the world's state hash (the derived claims are left out).
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        hasher.Add(CurrentSlot);
        hasher.Add(CurrentIndex);
        hasher.Add(Version);
        hasher.Add(Contract is not null);
        hasher.Add(ContractEnd);
        if (!MissionTarget.IsNone) hasher.Add(MissionTarget);
        if (Replacement is not null) hasher.Add(Rng.StableHash(Replacement.Id));
        hasher.Add(Intact);
        hasher.Add(Route.Length);
        foreach (var p in Route) hasher.Add(p);
        hasher.Add(RouteIndex);
        hasher.Add(NextPatrol);
        hasher.Add(PatrolGiveUp);
        hasher.Add(Home?.Position ?? default);
        if (BarTable >= 0) hasher.Add(BarTable);
        if (MissionTown >= 0) hasher.Add(MissionTown + 0x4D69);

        // The package classes' own state, only once one has run (worlds without them hash as before).
        if (!Destination.IsNone)
        {
            hasher.Add(Destination.Site);
            hasher.Add(Destination.PlayerTown);
        }

        if (TraderWait > 0)
        {
            hasher.Add(TraderState);
            hasher.Add(TraderWait);
            hasher.Add(InTown.Site);
            hasher.Add(InTown.PlayerTown);
            hasher.Add(InTownSince);
        }

        if (AssaultEnemy >= 0 || AssaultDone)
        {
            hasher.Add(AssaultStart);
            hasher.Add(AssaultEnemy);
            hasher.Add(AssaultDone);
            hasher.Add(AssaultCooldown);
        }

        if (OccupyAway > 0) hasher.Add(OccupyAway);
        if (RefugeeBroken || RefugeeTarget >= 0)
        {
            hasher.Add(RefugeeBroken);
            hasher.Add(RefugeeTarget);
        }
    }
}

/// <summary>
/// A flight the think asks the <see cref="FleeSystem"/> to start (<see cref="AiMind.FleeRequest"/>).
/// </summary>
public enum FleeAsk : byte
{
    /// <summary>
    /// No flight asked.
    /// </summary>
    None = 0,
    /// <summary>
    /// Self preservation's provider 35: from the threats attacking it, until none is left (<see cref="CharacterCold.FleesAlone"/>).
    /// </summary>
    Threat = 1,
    /// <summary>
    /// A goal 35 of its lists: from the enemies it senses, until none is in sight and nobody attacks it (the squads' rule).
    /// </summary>
    Enemies = 2,
}

/// <summary>
/// What the AI decided for one character (<see cref="CharacterCold.Ai"/>): the goal it acts out, where its post and bed are, the fight it asks for.
/// </summary>
public sealed class AiMind
{
    /// <summary>
    /// The squad it thinks for (fixed for the mind's life: a character does not change squads), held so the think needs no lookup. Not hashed (the
    /// squad is).
    /// </summary>
    public required Squad Squad { get; init; }

    /// <summary>
    /// Its place among the squad's members when the mind was made: which bed and which node of the home it takes.
    /// </summary>
    public int MemberIndex { get; init; }

    /// <summary>
    /// The goal being acted out (null before the first think).
    /// </summary>
    public AiGoal? Goal { get; set; }

    /// <summary>
    /// The goal's plan (<see cref="TaskPlanner"/>), leaf first: the step carried out is the first. Planned again at every think.
    /// </summary>
    public List<PlanStep> Plan { get; } = [];

    /// <summary>
    /// The plan's first step (None without a plan).
    /// </summary>
    public TaskType Step => Plan.Count > 0 ? Plan[0].Type : TaskType.None;
    /// <summary>
    /// The behaviour the engine acts the goal out with (<see cref="TaskBehaviours.Of"/>).
    /// </summary>
    public AiBehaviour Behaviour { get; set; }

    /// <summary>
    /// The <see cref="Blackboard.Version"/> the goal was chosen under.
    /// </summary>
    public int Version { get; set; } = -1;

    /// <summary>
    /// Its own spot: where it stood when the AI took it over (the stand-in where the home has no node or bed for it).
    /// </summary>
    public Vector2 Post { get; set; }

    /// <summary>
    /// The game hours (<see cref="World.GameHours"/>) until which it is committed to its goal (docs/game/ai.md "Packages and goals").
    /// </summary>
    public double CommitUntil { get; set; }

    /// <summary>
    /// Where its town goal goes (a node, a shop, a town; null: not picked yet, picked when the goal is acted out).
    /// </summary>
    public Vector3? Target { get; set; }

    /// <summary>
    /// The centre and radius <see cref="MovementSystem"/>'s wander uses instead of the squad's home (radius 0: the squad's).
    /// </summary>
    public Vector2 WanderCentre { get; set; }
    /// <summary>
    /// The radius of the wander round <see cref="WanderCentre"/> (0: the squad's home's).
    /// </summary>
    public float WanderRadius { get; set; }

    /// <summary>
    /// Its routine is the engine's, not the AI's: it is a member of a squad the AI does not run (a roaming platoon's active half, whose trip is the
    /// population's, or a squad whose template lists no AI packages). The AI weighs only its combat goals (<see cref="EngineGoals"/> and its race's)
    /// and Self preservation, on that routine and in its fights, and acts nothing else out (docs/simulation.md "AI").
    /// </summary>
    public bool Engine { get; init; }

    /// <summary>
    /// The combat goals of its platoon's package for its role (none without a package), set by the AI's serial step on the package cadence. Follow
    /// from the package, so not hashed.
    /// </summary>
    public IReadOnlyList<AiGoal> EngineGoals { get; set; } = [];

    /// <summary>
    /// Its race's <c>AI Goals</c>, looked up once when the mind is made (the lookup takes a lock; the think runs on every worker). Follows from the
    /// race, so not hashed.
    /// </summary>
    public IReadOnlyList<AiGoal> RaceGoals { get; init; } = [];

    /// <summary>
    /// It was sent walking to a spot and has not arrived.
    /// </summary>
    public bool Walking { get; set; }

    /// <summary>
    /// The spot its last walk ended at on its own (the movement followed the path to its last point, <see cref="WalkEnd.Finished"/>, and no fight or
    /// flight took it): the original counts a finished path as arrival before any distance test (<c>isAtLocation</c> <c>FUN_1405a2450</c>, Verified).
    /// Cleared when it walks again; <see cref="ArrivedFrom"/> is where the walk ended.
    /// </summary>
    public Vector2? ArrivedAt { get; set; }

    /// <summary>
    /// Where it stood when the walk to <see cref="ArrivedAt"/> finished. Not hashed: it follows the position.
    /// </summary>
    public Vector2 ArrivedFrom { get; set; }

    /// <summary>
    /// How far (units) it may have moved from <see cref="ArrivedFrom"/> and still count as arrived at <see cref="ArrivedAt"/> (engine choice: shoves
    /// from its neighbours stay within it, another system's walk, a round to a door, does not).
    /// </summary>
    public const float ArrivedDrift = 20;

    /// <summary>
    /// Its last walk finished at <paramref name="target"/> and it still stands where that walk ended (<see cref="ArrivedDrift"/>).
    /// </summary>
    public bool ArrivedThere(Vector2 target, Vector2 here) =>
        ArrivedAt == target && Vector2.DistanceSquared(here, ArrivedFrom) <= ArrivedDrift * ArrivedDrift;

    /// <summary>
    /// Walks in a row that ended away from the spot (no way there, or interrupted); at <see cref="AiSystem.MaxFailedWalks"/> it gives the spot up.
    /// </summary>
    public int FailedWalks { get; set; }

    /// <summary>
    /// The home's spots kept failing: it uses <see cref="Post"/> instead until its home changes.
    /// </summary>
    public bool HomeUnreachable { get; set; }

    /// <summary>
    /// Walks to its seat kept failing: it does not relax or sit there any more.
    /// </summary>
    public bool SeatUnreachable { get; set; }

    /// <summary>
    /// The guard post of its town it holds for 157 Static town guard (<see cref="AiSystem"/>'s serial step draws it on the package
    /// cadence); null on another goal or when no post was free. Not saved: a load draws again (derived, not hashed).
    /// </summary>
    public SeatClaim? TownPost { get; set; }

    /// <summary>
    /// The sitting clip of the chair it sits on (<see cref="HomeSpot.SitClip"/>), for <see cref="AnimationSystem"/>; null when not seated. Follows
    /// from the goal and seat, so not hashed.
    /// </summary>
    public string? SitClip { get; set; }

    /// <summary>
    /// That chair's functionality <c>hunger rate</c> (<see cref="HomeSpot.HungerRate"/>; 1 when not seated). Set with <see cref="SitClip"/>, so not
    /// hashed.
    /// </summary>
    public float SitHungerRate { get; set; } = 1;

    /// <summary>
    /// The work goals' pick of building (<see cref="AiBehaviour.Work"/>): the number of picks made since it last stopped working, so the plan's
    /// finder and the act-out name the same building (the pick is a roll of this and the character), and whether it is working now.
    /// </summary>
    public int WorkEpoch { get; set; }

    /// <summary>
    /// The roll that picks this goal's node among the home's (uniform; 0: not drawn yet, the member's round robin stands in) and the number of draws:
    /// renewed when a goal is taken up anew.
    /// </summary>
    public ulong NodeRoll { get; set; }
    /// <summary>
    /// The number of node draws behind <see cref="NodeRoll"/>.
    /// </summary>
    public int NodeEpoch { get; set; }
    /// <summary>
    /// Whether it is working now (see <see cref="WorkEpoch"/>).
    /// </summary>
    public bool WorkActive { get; set; }

    /// <summary>
    /// The town building it operates this tick (Task_OperateMachine, row 87, at the building) or empties the output of (row 92), and the faction
    /// owning its machine; set by the think, applied by <see cref="Production.ProductionSystem"/> (derived, not hashed).
    /// </summary>
    public TownBuilding? WorkOperate { get; set; }
    /// <summary>
    /// The town building it empties the output of this tick (row 92; see <see cref="WorkOperate"/>; derived, not hashed).
    /// </summary>
    public TownBuilding? CollectFrom { get; set; }

    /// <summary>
    /// The town building it fills (Task_FillMachine, row 93, at the building) with the inputs it carries this tick; set by the think, applied by
    /// <see cref="Production.ProductionSystem"/> (derived, not hashed).
    /// </summary>
    public TownBuilding? FillTo { get; set; }

    /// <summary>
    /// The plan's step 93 Filling machine into the ditch target (rows 113, 114, 242) is due: the think sets it, <see cref="Kidnap.TownDutySystem"/>
    /// starts the errand and clears it (derived, not hashed).
    /// </summary>
    public bool DitchRequest { get; set; }
    /// <summary>
    /// The faction owning the machine of <see cref="WorkOperate"/> (-1: none; derived, not hashed).
    /// </summary>
    public int WorkOwner { get; set; }

    /// <summary>
    /// The building its work goals picked (set with <see cref="WorkClip"/>).
    /// </summary>
    public TownBuilding? WorkBuilding { get; set; }

    /// <summary>
    /// The workplace it holds an operator slot of while on 189 / 190 (<see cref="AiSystem"/>'s serial step claims it on the package cadence); null on
    /// another goal or when no workplace had a free slot. Not saved: a load claims again (derived, not hashed).
    /// </summary>
    public TownBuilding? WorkPost { get; set; }

    /// <summary>
    /// The clip it plays at its work (the ANIMATION its building's functionality names), cleared with <see cref="CharacterCold.ActionClip"/> when it
    /// stops; null when not working.
    /// </summary>
    public string? WorkClip { get; set; }

    /// <summary>
    /// Its work building's BUILDING_FUNCTIONALITY (string id; null without one), whose <c>animation events</c> sound on <see cref="WorkClip"/>. Set
    /// with it, so not hashed.
    /// </summary>
    public string? WorkFunctionality { get; set; }

    /// <summary>
    /// Its work building's functionality <c>hunger rate</c> (1 by default), what <c>Task_OperateMachine</c> gives its operator. Set with
    /// <see cref="WorkClip"/>, so not hashed.
    /// </summary>
    public float WorkHungerRate { get; set; } = 1;

    /// <summary>
    /// How near it must come to the building it walks to for its work (<see cref="AiSystem.WorkRangeOf"/>). Set with that building each think, so not
    /// hashed.
    /// </summary>
    public float WorkRange { get; set; } = AiSystem.WorkReach;

    /// <summary>
    /// Its first think puts it where its plan's walk ends (<see cref="Squad.Resume"/>: back from its town's time unloaded); cleared by that think.
    /// </summary>
    public bool Resume { get; set; }

    /// <summary>
    /// Where the serial step puts it this tick (set by the think that took <see cref="Resume"/> up); null otherwise.
    /// </summary>
    public Vector3? PlaceAt { get; set; }

    /// <summary>
    /// The bed the serial step lies it in this tick (set by the think that arrived at its home's bed spot to sleep); null otherwise.
    /// </summary>
    public string? BedAt { get; set; }

    /// <summary>
    /// Whom its follow goal follows instead of the squad leader: the contract's mission target for a goal aimed at it (none: the leader).
    /// </summary>
    public CharacterId FollowTarget { get; set; } = CharacterId.None;

    /// <summary>
    /// Whom the think picked to assault (<see cref="AiBehaviour.Assault"/>); the serial step sends the attack and clears it.
    /// </summary>
    public CharacterId AssaultTarget { get; set; } = CharacterId.None;

    /// <summary>
    /// The Medic goal's finder result (<see cref="AiBehaviour.Medic"/>): the patient and score <see cref="FirstAid.FirstAidSystem.FindPatient"/> gave
    /// on the character's last sweep turn (none and 0: no patient). The finder shares scratch lists, so it runs in the serial sweep and the parallel
    /// think reads what it kept.
    /// </summary>
    public CharacterId MedicPatient { get; set; } = CharacterId.None;
    /// <summary>
    /// The finder's score for <see cref="MedicPatient"/> (0: no patient).
    /// </summary>
    public float MedicScore { get; set; }

    /// <summary>
    /// The think chose the Medic goal: the serial step of the same tick starts the treatment
    /// (<see cref="FirstAid.FirstAidSystem.SendGoalTreatments"/>) and clears it (not hashed).
    /// </summary>
    public bool MedicAsk { get; set; }

    /// <summary>
    /// The captive goals (<see cref="AiBehaviour.Captive"/>) whose job would start, each with the original's desire, found on the character's last
    /// captive sweep turn or at the end of its job (<see cref="Kidnap.CaptiveSystem"/>). Kept for a character awake that the sweep does not take
    /// (seated, in a job, on a duty), so its think weighs them. Not saved; hashed while set.
    /// </summary>
    public List<(TaskType Type, float Desire)> CaptiveOffers { get; } = [];

    /// <summary>
    /// The think chose a captive goal: the serial step of the same tick starts its job (<see cref="Kidnap.CaptiveSystem.SendGoalJobs"/>) and clears
    /// it (not hashed).
    /// </summary>
    public bool CaptiveAsk { get; set; }

    /// <summary>
    /// The desire kept for the captive goal <paramref name="type"/> (<see cref="CaptiveOffers"/>); 0 without one.
    /// </summary>
    public float CaptiveDesire(TaskType type)
    {
        foreach (var (t, d) in CaptiveOffers)
            if (t == type) return d;
        return 0;
    }

    /// <summary>
    /// The Assault order a dialogue attack effect gave this character (<c>FUN_140680560</c> cases 6, 0x37 and 0x39 order goal 101, or 248 for 0x37,
    /// on the effect's target): the member of the target's squad it attacks, picked again whenever it is free
    /// (<see cref="AiSystem.AssaultTargetFor"/>); none when it has no such order. Not saved; hashed while set.
    /// </summary>
    public CharacterId ForcedAssault { get; set; } = CharacterId.None;

    /// <summary>
    /// Where the stranger a static guard stands beside is (<see cref="AiSystem"/>'s <c>GuardFactor</c>, FUN_14099edd0); null: the plain guard post.
    /// Derived by the think; hashed while set.
    /// </summary>
    public Vector3? GuardVisitorAt { get; set; }

    /// <summary>
    /// What the think asks of the <see cref="FleeSystem"/> this tick: to run the character from its threats (Self preservation's provider 35) or from
    /// the enemies it senses (a goal 35 of its lists). Set by the think on the mind's own character, taken and cleared by the serial flee step of the
    /// same tick, so it is never set between ticks (not hashed).
    /// </summary>
    public FleeAsk FleeRequest { get; set; }

    /// <summary>
    /// The fight the think chose for it (a combat goal's, <see cref="AiBehaviour.Fight"/>): the goal and the character the goal's finder (and, for
    /// the protect goals, row 13) picked. Set by the think on the mind's own character, taken and cleared by <see cref="AiSystem"/>'s serial step of
    /// the same tick, which starts the fight; never set between ticks (not hashed).
    /// </summary>
    public TaskType FightGoal { get; set; }
    /// <summary>
    /// The character the goal's finder picked for <see cref="FightGoal"/> (none: no fight asked).
    /// </summary>
    public CharacterId FightTarget { get; set; } = CharacterId.None;

    /// <summary>
    /// The leader rescue goal's factor (<see cref="AiBehaviour.RescueLeader"/>): 1 while the squad leader is down and can be lifted, kept by the
    /// carry system's serial sweep (the think reads it).
    /// </summary>
    public float RescueWant { get; set; }

    /// <summary>
    /// The tick the last fight the serial step started for it begins (<see cref="CharacterCold.Running"/>'s <c>Tick</c> of that fight; 0: none yet):
    /// while it runs, the fight is the AI's own, a plan step of its combat goal, and the character thinks in it (<see cref="AiSystem.Holds"/>).
    /// </summary>
    public long FightFrom { get; set; }

    /// <summary>
    /// The think of a fighter chose a goal that is no fight: the serial step of the same tick ends the fight, and it thinks again at the next tick.
    /// Never set between ticks (not hashed).
    /// </summary>
    public bool LeaveFight { get; set; }

    /// <summary>
    /// It thinks at the next tick whatever its cadence (a replan request, engine stand-in for the original's triggers): set by a sighting of its own
    /// or of a squad mate, a new enemy sensed while it fights (<see cref="AggroSystem"/>), an alert near it (<see cref="RetaliationSystem"/>), a
    /// mate's fight start and the end of its own fight (<see cref="Combat.CombatSystem"/>); cleared by that think.
    /// </summary>
    public bool Replan { get; set; }

    /// <summary>
    /// The AI task system's plan reset (<c>FUN_1405074a0</c>): the pending steps and the committed goal go, so the next think chooses again.
    /// </summary>
    public void ResetPlan()
    {
        Plan.Clear();
        CommitUntil = 0;
        Replan = true;
    }

    /// <summary>
    /// What it wants to buy (goal 118, <see cref="ShoppingList"/>); made with the mind from its record and the world seed, not saved.
    /// </summary>
    public ShoppingList Shopping { get; init; } = new();

    /// <summary>
    /// The shop it walks to for goal 118 (set when the target is picked, so the arrival knows what the shop stocks); follows from the target, so not
    /// hashed.
    /// </summary>
    public TownBuilding? ShopAt { get; set; }

    /// <summary>
    /// The world tick of its next unit at the shop (<c>Task_Shopping</c>, goal 119: a second after it arrived, then a second after each sale); 0: the
    /// task is not running. Not saved (the task is not).
    /// </summary>
    public long NextBuyTick { get; set; }

    /// <summary>
    /// The cage the kill and eat goals chose last (the AI memory's hand <c>+0x88</c>, <c>FUN_1408d0d90</c>, <b>Verified</b>): asked again while it
    /// still holds a captive, the finder returns it as it is. One memory for goals 135, 139, 282 and the eat goals. Not saved.
    /// </summary>
    public string? FateCage { get; set; }

    /// <summary>
    /// The investigation's look-about count in seconds (<see cref="Crime.CrimeSystem.LookAboutSeconds"/>; the task starts it at 15).
    /// </summary>
    public float LookAbout { get; set; } = Crime.CrimeSystem.FirstLookAboutSeconds;

    /// <summary>
    /// Adds the mind's state to the world's state hash.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (Engine) hasher.Add(0x456E6769);
        hasher.Add((int)(Goal?.Task.Type ?? TaskType.None));
        hasher.Add((int)Step);
        hasher.Add((int)Behaviour);
        hasher.Add(Version);
        hasher.Add(Post);
        hasher.Add(WanderCentre);
        hasher.Add(WanderRadius);
        hasher.Add(Walking);
        hasher.Add(FailedWalks);
        if (ArrivedAt is { } arrived)
        {
            hasher.Add(0x41727276);
            hasher.Add(arrived);
        }   // only while set
        hasher.Add(HomeUnreachable);
        hasher.Add(SeatUnreachable);
        hasher.Add(CommitUntil);
        hasher.Add(Target ?? new Vector3(float.NaN));
        if (WorkActive || WorkEpoch != 0)
        {
            hasher.Add(WorkEpoch);
            hasher.Add(WorkActive);
        }

        if (NodeRoll != 0) hasher.Add(NodeRoll);
        if (Resume) hasher.Add(0x52657375);
        if (PlaceAt is { } place) hasher.Add(place);
        if (BedAt is { } bed) hasher.Add(Rng.StableHash(bed));
        if (!FollowTarget.IsNone) hasher.Add(FollowTarget);
        if (!AssaultTarget.IsNone) hasher.Add(AssaultTarget);
        if (MedicScore != 0)
        {
            hasher.Add(MedicPatient);
            hasher.Add(MedicScore);
        }

        if (CaptiveOffers.Count > 0)
        {
            hasher.Add(0x434F6666);   // "COff"
            foreach (var (t, d) in CaptiveOffers)
            {
                hasher.Add((int)t);
                hasher.Add(d);
            }
        }

        if (!ForcedAssault.IsNone)
        {
            hasher.Add(0x466F7241);
            hasher.Add(ForcedAssault);
        }

        if (GuardVisitorAt is { } visitor)
        {
            hasher.Add(0x47756172);
            hasher.Add(visitor);
        }

        if (FightFrom != 0) hasher.Add(FightFrom);
        if (Replan) hasher.Add(0x5265706C);
        if (LookAbout != Crime.CrimeSystem.FirstLookAboutSeconds) hasher.Add(LookAbout);
        if (NextBuyTick != 0) hasher.Add(NextBuyTick);
        if (FateCage is { } fateCage) hasher.Add(Rng.StableHash(fateCage));
        Shopping.Hash(ref hasher);
    }
}