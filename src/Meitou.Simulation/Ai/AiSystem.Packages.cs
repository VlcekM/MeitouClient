using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The package classes with rules of their own beyond the patrol, bar and retreat (docs/game/ai.md "Package classes", decoded from their vtables;
/// docs/simulation.md "AI", Package classes): the wandering trader (16), the passing-by assault on a player town (29) and the occupation of it that
/// follows (30), the refugee (22), and the campaign's war classes (19, 20, 32) and defence (31). All run in the serial step.
/// </summary>
public sealed partial class AiSystem
{
    #region State

    /// <summary>
    /// The game hour each faction's last passing-by assault ended (the original's global map <c>DAT_14212dde0</c>, by faction, not saved): a squad of
    /// the faction may start one only while another of its squads assaults, or once its own wait has passed since then.
    /// </summary>
    readonly SortedDictionary<int, double> _assaultEnds = [];

    /// <summary>
    /// Dialogue events: a leader coming into a town, each occupier as the occupation starts (the editor's <c>EventTriggerEnum</c>).
    /// </summary>
    const int EnterTown = 49, TakenOverPlayerTown = 73;

    /// <summary>
    /// The faction assault clocks once there is one (worlds without a passing-by assault hash as before), and the code mods' faulted NPC goals once
    /// one faulted.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        foreach (var (faction, end) in _assaultEnds)
        {
            hasher.Add(faction);
            hasher.Add(end);
        }

        _modGoals?.Hash(ref hasher);
    }

    #endregion

    #region Towns

    /// <summary>
    /// Radius factors of the status town (<c>FUN_1405a51e0</c>, <b>Verified</b>): a town of the player's faction, keeping the town the character is
    /// in, entering one.
    /// </summary>
    internal const float PlayerTownReach = 1.25f, StayReach = 1.5f, EnterReach = 1;

    /// <summary>
    /// Where the squad is: its leader's place, else its first living member's; null when nobody lives.
    /// </summary>
    static Vector3? LeaderPosition(World world, Squad squad)
    {
        var table = world.Characters;
        if (table.IsAlive(squad.Leader)) return table.Previous[squad.Leader.Slot].Position;
        foreach (var m in squad.Members)
            if (table.IsAlive(m)) return table.Previous[m.Slot].Position;
        return null;
    }

    /// <summary>
    /// The town a point is in (the original's <c>FUN_1402688d0</c>: the nearest town of any kind but a nest marker, when the point is within its
    /// radius, <c>FUN_140926d50</c>): placed towns by <see cref="TownData.Radius"/>, the player's by <see cref="Construction.PlayerTown.Radius"/>.
    /// </summary>
    internal CampaignTown CurrentTown(World world, Vector3 p) => TownAt(data, world, p);

    /// <summary>
    /// <see cref="CurrentTown"/> for any system that has the population data.
    /// </summary>
    internal static CampaignTown TownAt(PopulationData data, World world, Vector3 p)
    {
        var at = Flat(p);
        var best = CampaignTown.None;
        float bestD = float.MaxValue, bestR = 0;
        foreach (var site in data.Sites)
        {
            if (site.Town.Type is TownType.NestMarker or TownType.Null) continue;
            float d = Vector2.DistanceSquared(at, Flat(site.Position));
            if (d < bestD) (best, bestD, bestR) = (CampaignTown.OfSite(site.Index), d, site.Town.Radius);
        }

        foreach (var town in world.Buildings.Towns)
        {
            float d = Vector2.DistanceSquared(at, Flat(town.Position));
            if (d < bestD) (best, bestD, bestR) = (CampaignTown.OfPlayer(town.Number), d, town.Radius);
        }

        return bestD <= bestR * bestR ? best : CampaignTown.None;
    }

    /// <summary>
    /// The character's status town (the original's status <c>+0x88</c>, written by <c>FUN_1405a51e0</c> before each status update,
    /// docs/game/ai-tasks.md "Status town"), from its position and its <paramref name="current"/> one: the nearest placed town of the player's
    /// faction within 1.25 x its radius; else the current town within 1.5 x its radius, or, with none, the nearest placed town within its radius;
    /// else the nearest player town within the same reach. The original's walled-town and building steps are not modelled.
    /// </summary>
    internal static CampaignTown StatusTownAt(PopulationData data, World world, Vector3 p, CampaignTown current)
    {
        var at = Flat(p);
        int player = world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction;
        if (NearestSite(data, at, player) is { } own && Reaches(at, own, PlayerTownReach)) return own.Town;

        float reach = current.IsNone ? EnterReach : StayReach;
        var held = current.IsNone ? NearestSite(data, at, -1) : TownPlace(data, world, current);
        if (held is { } town && Reaches(at, town, reach)) return town.Town;
        return NearestPlayerTown(world, at) is { } built && Reaches(at, built, reach) ? built.Town : CampaignTown.None;
    }

    /// <summary>
    /// A campaign town with the centre and radius it is placed at.
    /// </summary>
    readonly record struct PlacedTown(CampaignTown Town, Vector2 Centre, float Radius);

    /// <summary>
    /// Whether <paramref name="at"/> is within <paramref name="factor"/> times the town's radius of its centre.
    /// </summary>
    static bool Reaches(Vector2 at, PlacedTown town, float factor)
    {
        float r = town.Radius * factor;
        return Vector2.DistanceSquared(at, town.Centre) < r * r;
    }

    /// <summary>
    /// The nearest placed town but a nest marker (of <paramref name="faction"/> when it is 0 or more).
    /// </summary>
    static PlacedTown? NearestSite(PopulationData data, Vector2 at, int faction)
    {
        PlacedTown? best = null;
        float bestD = float.MaxValue;
        var sites = data.Sites;
        for (int i = 0; i < sites.Count; i++)   // indexed: the shoppers' think asks this in the parallel step
        {
            var site = sites[i];
            if (site.Town.Type is TownType.NestMarker or TownType.Null) continue;
            if (faction >= 0 && (site.Index >= data.SiteFactions.Count || data.SiteFactions[site.Index] != faction)) continue;

            float d = Vector2.DistanceSquared(at, Flat(site.Position));
            if (d < bestD) (best, bestD) = (new PlacedTown(CampaignTown.OfSite(site.Index), Flat(site.Position), site.Town.Radius), d);
        }

        return best;
    }

    /// <summary>
    /// The player town whose centre is nearest <paramref name="at"/>; null when the player has none.
    /// </summary>
    static PlacedTown? NearestPlayerTown(World world, Vector2 at)
    {
        PlacedTown? best = null;
        float bestD = float.MaxValue;
        var towns = world.Buildings.Towns;
        for (int i = 0; i < towns.Count; i++)
        {
            var town = towns[i];
            float d = Vector2.DistanceSquared(at, Flat(town.Position));
            if (d < bestD) (best, bestD) = (new PlacedTown(CampaignTown.OfPlayer(town.Number), Flat(town.Position), town.Radius), d);
        }

        return best;
    }

    /// <summary>
    /// The centre (X/Z) of campaign town <paramref name="t"/>, and its radius; null when it is not (or no longer) there.
    /// </summary>
    internal static (Vector2 Centre, float Radius)? TownCircle(PopulationData data, World world, CampaignTown t) =>
        TownPlace(data, world, t) is { } place ? (place.Centre, place.Radius) : null;

    /// <summary>
    /// Where campaign town <paramref name="t"/> stands: a town site's position and radius, or a player town's; null when it is not (or no
    /// longer) there.
    /// </summary>
    static PlacedTown? TownPlace(PopulationData data, World world, CampaignTown t)
    {
        if (t.Site >= 0)
        {
            if (t.Site >= data.Sites.Count) return null;

            var site = data.Sites[t.Site];
            return new PlacedTown(t, Flat(site.Position), site.Town.Radius);
        }

        return world.Buildings.FindTown(t.PlayerTown) is { } built ? new PlacedTown(t, Flat(built.Position), built.Radius) : null;
    }

    /// <summary>
    /// The centre of the running package's mission town (X/Z); null without one or when that player town is gone.
    /// </summary>
    Vector2? DestinationCentre(World world, Blackboard board)
    {
        // A refugee on its way to a squad walks to that squad's leader.
        if (board.RefugeeTarget >= 0 && world.Squads.Find(board.RefugeeTarget) is { } other && LeaderPosition(world, other) is { } there)
            return Flat(there);

        var t = board.Destination;
        if (t.Site >= 0) return Flat(data.Sites[t.Site].Position);
        return t.PlayerTown > 0 && world.Buildings.FindTown(t.PlayerTown) is { } town ? Flat(town.Position) : null;
    }

    /// <summary>
    /// The radius of the running package's mission town: its site's size radius times the town's multiplier, else the player town's radius; 0
    /// without one.
    /// </summary>
    float DestinationRadius(World world, Blackboard board) =>
        board.Destination.Site >= 0
            ? data.Sites[board.Destination.Site].Town.SizeRadius * data.Sites[board.Destination.Site].Town.TownRadiusMult
            : world.Buildings.FindTown(board.Destination.PlayerTown)?.Radius ?? 0;

    /// <summary>
    /// The squad's package target, a town or a character (<c>FUN_140796120</c> over the platoon's <c>FUN_140269b60</c>, <b>Verified</b>): its
    /// platoon's campaign target while it runs one (<see cref="CampaignInstance.Target"/>); else the running package's (vtable +0x8): a broken
    /// refugee's squad to join once it has one (its leader), else its mission town (<c>FUN_140288dc0</c>); any other package's mission target
    /// (<c>FUN_1402859e0</c>: package +0x30), which a contract aimed at a town holds as its mission town (<see cref="Blackboard.AimMissionAtTown"/>).
    /// Both none: no target.
    /// </summary>
    internal (CampaignTown Town, CharacterId Character) PackageTarget(World world, Squad squad, Blackboard board)
    {
        if (squad.PlatoonId >= 0 && world.Platoons.Find(squad.PlatoonId) is { Campaign: { } campaign }) return (campaign.Target, CharacterId.None);

        if (board.CurrentClass == PackageClass.Refugee)
        {
            return board.RefugeeBroken && world.Squads.Find(board.RefugeeTarget) is { } join
                ? (CampaignTown.None, join.Leader)
                : (board.Destination, CharacterId.None);
        }

        return board.MissionTarget.IsNone ? (board.ContractTown, CharacterId.None) : (CampaignTown.None, board.MissionTarget);
    }

    /// <summary>
    /// The town the squad is in, before the package choice (the original's Blackboard +0x138 and its arrival hour +0x140); coming into one, the
    /// leader fires ENTER_TOWN (49).
    /// </summary>
    void UpdateInTown(World world, Squad squad, Blackboard board)
    {
        if (LeaderPosition(world, squad) is not { } at) return;
        var town = CurrentTown(world, at);
        if (board.InTown.IsNone && !town.IsNone)
        {
            board.InTownSince = world.GameHours;
            if (world.Characters.IsAlive(squad.Leader))
                world.Defer(new Dialogue.DialogueEvent(squad.Leader, CharacterId.None, EnterTown) { Tick = world.Tick + 1 });
        }

        board.InTown = town;
        if (town.Site >= 0 && !_townPlaces.Has(town.Site)) _townPlaces.Refresh(world, this);
    }

    #endregion

    #region Package steps

    /// <summary>
    /// The AI signals of <c>Contract_HiredAlly</c> (hired bodyguards) and <c>Contract_DefendPlayerBase</c> (the timed base defence).
    /// </summary>
    internal const int HiredAllySignal = 12, DefendBaseSignal = 15;

    /// <summary>
    /// The AI signal of <c>Campaign_DefendPlayerBase</c>, the campaign's base defence (its start and mission are the timed defence's).
    /// </summary>
    internal const int CampaignDefendSignal = 31;

    /// <summary>
    /// The running package's update (the original's vtable +0x48, after the choice): the trader's arrival; under an assault or occupation the squad
    /// treats the town's faction as enemies (<see cref="Squad.War"/>; the original tags each one it sees with squad memory 4) and roams the town
    /// (engine choice: its wander centre).
    /// </summary>
    void StepPackage(World world, Squad squad, Blackboard board)
    {
        switch (board.CurrentClass)
        {
            case PackageClass.WanderingTrader:
                StepTrader(world, squad, board);
                break;
            case PackageClass.Refugee:
                StepRefugee(world, squad, board);
                break;
            case PackageClass.Contract:
                TagMissionTarget(world, squad, board);
                break;
            case PackageClass.PassingAssault:
            case PackageClass.OccupyTown:
                var war = board.AssaultEnemy >= 0 ? new WarTargets(board.AssaultEnemy, false, CampaignTown.None) : WarTargets.None;
                if (squad.War != war) squad.War = war;
                if (DestinationCentre(world, board) is { } centre)
                {
                    squad.HomeCentre = centre;
                    squad.HomeRadius = MathF.Max(DestinationRadius(world, board) * 0.5f, 40);
                }

                break;
            default:
                // An AI-run squad is never a campaign's (the campaign runs those), so nobody else sets its war targets.
                if (squad.War.IsSet) squad.War = WarTargets.None;
                break;
        }
    }

    /// <summary>
    /// The update of Contract_HiredAlly (12), Contract_UntilOutOfDanger (14) and Contract_DefendPlayerBase (15) (<c>FUN_1402910e0</c>,
    /// <c>FUN_140290f90</c>, <c>FUN_140291410</c>, <b>Verified</b>): the package's mission target, when it is a character, is tagged TEMP ALLY (3)
    /// in the squad's memory each update; the defend package's is its town, so it tags nobody, and without a town it runs its start again
    /// (<see cref="ContractStart"/>). The sensing hook (<see cref="AggroSystem"/> <c>EmployerTags</c>) tags the employer's faction as it is
    /// seen, separately.
    /// </summary>
    void TagMissionTarget(World world, Squad squad, Blackboard board)
    {
        if (board.Contract?.Signal is not (HiredAllySignal or 14 or DefendBaseSignal) || board.Contract is not { } contract) return;
        if (contract.Signal == DefendBaseSignal && board.ContractTown.IsNone)
        {
            ContractStart(world, squad, board, contract);
            return;
        }

        if (Memory is null || board.MissionTarget.IsNone) return;
        Memory.Flag(world, squad.Id, board.MissionTarget, Dialogue.MemoryTag.TempAlly);
    }

    /// <summary>
    /// The start of <c>Contract_HiredAlly</c> (<c>FUN_140289490</c>) and <c>Contract_DefendPlayerBase</c> (<c>FUN_14028a170</c>), <b>Verified</b>:
    /// the contract's mission town is the town of the player's faction nearest the squad's leader (X/Z, <c>FUN_140927f10</c>), none while the player
    /// has none (<see cref="Blackboard.ContractTown"/>). The bodyguard's clears the blackboard's target town and keeps its employer as the mission
    /// target; the base defender's makes the town both its target town (its goals travel there, 130, and patrol it, 36) and its mission
    /// (<see cref="Blackboard.AimMissionAtTown"/>).
    /// </summary>
    void ContractStart(World world, Squad squad, Blackboard board, AiPackage package)
    {
        var at = LeaderPosition(world, squad) ?? squad.Position;
        board.ContractTown = NearestPlayerTown(world, Flat(at))?.Town ?? CampaignTown.None;
        if (package.Signal != DefendBaseSignal) return;

        board.Destination = board.ContractTown;
        board.AimMissionAtTown();
    }

    #endregion

    #region Patrol

    /// <summary>
    /// The game hours a squad stays in a town before its patrol package may start there (<c>DAT_1416ad81c</c>).
    /// </summary>
    const float PatrolTownHours = 8;

    /// <summary>
    /// The game hours after which a running patrol package is finished (<c>DAT_1416adc00</c>).
    /// </summary>
    const double PatrolHours = 12;

    /// <summary>
    /// <c>GoOutOnPatrol</c> slot 12 (<c>FUN_140288890</c>, <b>Verified</b>; signals 6 and 23, and 4 after its cage test,
    /// <see cref="CannibalPatrolHeld"/>): never the running package once it is finished; a squad with members that is intact
    /// (<c>FUN_1407ac7b0</c>), none of them carrying someone (<c>+0x348</c>), with an effective leader (<c>FUN_14079aea0</c>,
    /// <see cref="PopulationSystem.EffectiveLeader"/>); then while that leader is in no town (its status town, <c>+0x88</c>), or once the squad has
    /// been more than 8 game hours in the town it is in (the blackboard's arrival hour, <c>+0x140</c>). It asks for no home town, so a homeless
    /// squad's patrol starts where it stands.
    /// </summary>
    bool PatrolCanStart(World world, Squad squad, Blackboard board, AiPackage package)
    {
        if (board.Current == package && PatrolFinished(world, board)) return false;
        if (squad.Members.Count == 0 || !board.Intact || AnyCarrying(world, squad)) return false;
        if (CannibalPatrolHeld(world, squad, board, package)) return false;

        var table = world.Characters;
        var lead = world.SystemOf<PopulationSystem>()?.EffectiveLeader(world, squad) ?? squad.Leader;
        if (!table.TryResolveNext(lead, out int s) || table.Cold(s) is not { } leader) return false;

        var town = StatusTownAt(data, world, table.Next[s].Position, leader.StatusTown);
        return town.IsNone || world.GameHours - board.InTownSince > PatrolTownHours;
    }

    /// <summary>
    /// <c>GoOutOnPatrol</c> slot 13 (<c>FUN_1402870a0</c>, <b>Verified</b>; all three patrol classes): the squad is not intact, or more than 12
    /// game hours have passed since the package started (its activation <c>FUN_140287060</c> stamps the hour, <see cref="Blackboard.PatrolStart"/>).
    /// </summary>
    static bool PatrolFinished(World world, Blackboard board) => !board.Intact || world.GameHours - board.PatrolStart > PatrolHours;

    #endregion

    #region Wandering trader

    /// <summary>
    /// Whether a trader may start (vtable +0x60): always while on its way or just arrived; once done in a town, when it is ready to leave
    /// (<see cref="ReadyToLeave"/>); in a player town that is not public at once. Engine choice: a town's own squad never starts it (Meitou unloads a
    /// town's squads with their town, so one away from home would vanish when it unloads).
    /// </summary>
    bool TraderCanStart(World world, Squad squad, Blackboard board) =>
        !squad.TownOwn && (InPrivateOutpost(world, board) || board.TraderState != 2 || ReadyToLeave(world, board));

    /// <summary>
    /// Ready to go on (<c>FUN_1402868c0</c>): in a player town that is not public (<see cref="Construction.PlayerTown.Public"/>) at once; else after
    /// its stay (<see cref="Blackboard.TraderWait"/> hours since it came into the town) and only between 06:00 and 20:59.
    /// </summary>
    static bool ReadyToLeave(World world, Blackboard board) =>
        InPrivateOutpost(world, board) || board.TraderWait <= world.GameHours - board.InTownSince && world.Hour is > 5 and < 21;

    /// <summary>
    /// Whether the board's town is a player outpost that is not public.
    /// </summary>
    static bool InPrivateOutpost(World world, Blackboard board) =>
        board.InTown.PlayerTown > 0 && world.Buildings.FindTown(board.InTown.PlayerTown)?.Public != true;

    /// <summary>
    /// The trader's start (vtable +0x30): a stay of uniform [5, 8) hours, state 0 and the next town (<see cref="PopulationData.TraderTown"/> from
    /// where its leader is, not the town it is in); none found leaves it without one, so its next update tries again.
    /// </summary>
    void TraderStart(World world, Squad squad, Blackboard board)
    {
        ulong key = (ulong)squad.Id << 32 ^ (ulong)world.Tick;
        board.TraderWait = 5 + 3 * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, SeedSalts.AiTraderWait));
        board.TraderState = 0;
        board.VisitArrivalPending = false;
        var at = LeaderPosition(world, squad) ?? squad.Position;
        ulong townSeed = Rng.Hash(world.Seed, key, RngPurpose.Think, SeedSalts.AiTraderTown);
        board.Destination = data.TraderTown(squad.Faction, Flat(at), board.InTown, world.Buildings.Towns, townSeed);
    }

    /// <summary>
    /// The trader's update (vtable +0x48, <c>FUN_140288c30</c>, <b>Verified</b>): without a town it starts over; in any town other than the one it
    /// last left (<see cref="Blackboard.TraderLeft"/>), not only the one it set out for, the state goes 0 → 1 → 2 (2 is finished, so the next slot,
    /// its bar package, takes over), and when it is ready to leave there it drops the town (<c>FUN_140288bd0</c>: that town becomes the last left)
    /// to draw a new one next time. A <see cref="VisitTown"/> stops only at its town, and its stay starts its clock at the first step there
    /// (<see cref="Blackboard.VisitArrivalPending"/>). The original also leaves 0.2 h after a flag of its leader is set (Character +0x648 → +0x70,
    /// meaning <b>Unknown</b>): not built.
    /// </summary>
    void StepTrader(World world, Squad squad, Blackboard board)
    {
        if (board.Destination.IsNone)
        {
            TraderStart(world, squad, board);
            return;
        }

        if (!TraderArrived(world, board)) return;

        board.TraderState = board.TraderState == 0 ? 1 : 2;
        if (!ReadyToLeave(world, board)) return;

        board.TraderLeft = board.InTown;
        board.Destination = CampaignTown.None;
    }

    /// <summary>
    /// Whether this step counts as being in a town to stop at: any town but the one last left; a <see cref="VisitTown"/> only its own town, where
    /// the first step starts the stay's clock.
    /// </summary>
    static bool TraderArrived(World world, Blackboard board)
    {
        if (board.InTown.IsNone) return false;
        if (!board.VisitArrivalPending) return board.InTown != board.TraderLeft;
        if (board.InTown != board.Destination) return false;

        board.InTownSince = world.GameHours;
        board.VisitArrivalPending = false;
        return true;
    }

    #endregion

    #region Passing-by assault

    /// <summary>
    /// A passing-by assault looks for the town faction's people within this of the town (<b>Verified</b>, 2500 in <c>FUN_140290450</c>).
    /// </summary>
    internal const float AssaultReach = 2500;

    /// <summary>
    /// The assault is not over before this many game hours (<b>Verified</b>, 0.25 in its finished test).
    /// </summary>
    const double AssaultMinHours = 0.25;

    /// <summary>
    /// Whether a character is alive and not down.
    /// </summary>
    static bool Up(World world, CharacterId id) =>
        world.Characters.IsAlive(id) && world.Characters.TryResolveNext(id, out int s)
            && world.Characters.Cold(s)!.Medical is not { Incapacitated: true };

    /// <summary>
    /// The squad's members that are up (<see cref="Up"/>).
    /// </summary>
    int MembersUp(World world, Squad squad)
    {
        int n = 0;
        foreach (var m in squad.Members) if (Up(world, m)) n++;
        return n;
    }

    /// <summary>
    /// Whether someone of <paramref name="faction"/> (the player's characters for the player's faction) is up within <paramref name="range"/> of
    /// a point.
    /// </summary>
    bool FactionNear(World world, int faction, Vector2 at, float range)
    {
        var table = world.Characters;
        for (int i = 0; i < table.Previous.Length; i++)
        {
            if (!table.Previous[i].Alive || table.Cold(i) is not { } c || c.Medical is { Incapacitated: true } || !OfFaction(world, c, faction))
                continue;

            if (Vector2.DistanceSquared(Flat(table.Previous[i].Position), at) <= range * range) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a character belongs to <paramref name="faction"/>: its own faction, or a player character when the faction is the player's.
    /// </summary>
    bool OfFaction(World world, CharacterCold c, int faction) =>
        c.Faction == faction || c.IsPlayer && faction == (world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction);

    /// <summary>
    /// Whether a passing-by assault may start (vtable +0x60, <c>FUN_140290450</c>): attacks not set to Never; not spent; its leader in a player town
    /// whose faction it is not allied with (same or relation ≥ 50, <c>FUN_1406b2630</c>); the squad intact; its faction's wait passed
    /// (<c>FUN_140290020</c>: free while another squad of the faction assaults; a faction that never ended one is free and its clock starts now); and
    /// someone of the town's faction within 2500 of the town. The town test vtable +0x2f0 (Town +0x3b0, <b>Unknown</b>) is taken as passing.
    /// </summary>
    bool AssaultCanStart(World world, Squad squad, Blackboard board)
    {
        if (Settings.TownAttacks >= 5 || board.AssaultDone || !world.Characters.IsAlive(squad.Leader) || !board.Intact) return false;
        if (board.InTown.PlayerTown <= 0 || world.Buildings.FindTown(board.InTown.PlayerTown) is not { } town) return false;
        if (data.Relations.IsAlly(squad.Faction, town.Owner)) return false;
        if (!Assaulting(world, squad.Faction, squad))
        {
            if (_assaultEnds.TryGetValue(squad.Faction, out double end))
            {
                if (world.GameHours - end <= board.AssaultCooldown) return false;
            }
            else _assaultEnds[squad.Faction] = world.GameHours;
        }

        return FactionNear(world, town.Owner, Flat(town.Position), AssaultReach);
    }

    /// <summary>
    /// Whether another squad of the faction runs a passing-by assault now.
    /// </summary>
    static bool Assaulting(World world, int faction, Squad except)
    {
        foreach (var other in world.Squads.All)
            if (other != except && other.Faction == faction && other.Ai?.CurrentClass == PackageClass.PassingAssault) return true;
        return false;
    }

    /// <summary>
    /// The assault's start (vtable +0x30): its faction's wait for next time by <see cref="AiSettings.TownAttacks"/> (<c>FUN_140286a70</c>, game
    /// hours, uniform: Bombardment 0, High [6, 48), Normal [32, 96), Fewer [72, 144), Rare [130, 188)); the town its leader is in is its mission
    /// town, the town's faction its enemy, the start hour noted.
    /// </summary>
    void AssaultStarted(World world, Squad squad, Blackboard board)
    {
        var (min, max) = Settings.TownAttacks switch { 1 => (6f, 48f), 2 => (32f, 96f), 3 => (72f, 144f), 4 => (130f, 188f), _ => (0f, 0f) };
        ulong key = (ulong)squad.Id << 32 ^ (ulong)world.Tick;
        board.AssaultCooldown = min + (max - min) * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Think, SeedSalts.AiAssaultWait));
        board.Destination = board.InTown;
        board.AssaultStart = world.GameHours;
        board.AssaultEnemy = world.Buildings.FindTown(board.InTown.PlayerTown)?.Owner ?? -1;
        board.AssaultTargets.Clear();
        SeedAssaultTargets(world, board);
        if (DestinationCentre(world, board) is { } centre) PostsInto(world, squad, centre, null);
    }

    /// <summary>
    /// Engine choice: the members' posts move into the town they assault or hold (a ring round its centre by member), so their idle and their node
    /// and bed goals, which have no building there, stay in it (the original's ransacking goal, <c>Task_PatrolTownViolent</c>, has a target finder
    /// Meitou lacks); with <paramref name="say"/> each says it.
    /// </summary>
    static void PostsInto(World world, Squad squad, Vector2 centre, int? say)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s)?.Ai is not { } mind) continue;
            mind.Post = PostRound(centre, mind.MemberIndex);

            // A stroll picked round the old post is dropped, so the next one is round the new.
            if (table.Next[s].Task == (byte)CharacterTask.Wander) Become(world, s, CharacterTask.Idle);
            if (say is { } e) world.Defer(new Dialogue.DialogueEvent(m, CharacterId.None, e) { Tick = world.Tick + 1 });
        }
    }

    /// <summary>
    /// The assault gives way (vtable +0x38): the last of its faction's running ones to end starts the faction's wait.
    /// </summary>
    void AssaultEnded(World world, Squad squad)
    {
        if (!Assaulting(world, squad.Faction, squad)) _assaultEnds[squad.Faction] = world.GameHours;
    }

    /// <summary>
    /// Someone of the assault's target set (<see cref="Blackboard.AssaultTargets"/>: the attacked faction's people within 2500 at the start and those
    /// its members sense since; none leaves it) still stands in the mission town. The end and hand-over tests (<c>FUN_14028eca0</c>,
    /// <c>FUN_14028ea90</c>, <b>Verified</b>) count a target only when its current town is the mission town and it is not unconscious or dead, not
    /// caged and at posture 0 (staying low, crawling and playing dead do not count); the inside-town-walls value (vfunc 0x118, non-zero) is not
    /// modelled.
    /// </summary>
    bool DefenderStanding(World world, Blackboard board)
    {
        if (board.AssaultEnemy < 0) return false;
        var table = world.Characters;
        if (!board.AssaultSeeded) SeedAssaultTargets(world, board);
        foreach (var id in board.AssaultTargets)
        {
            if (!table.TryResolveNext(id, out int i) || !table.Previous[i].Alive || table.Cold(i) is not { } c) continue;
            if (Stands(c, CurrentTown(world, table.Previous[i].Position) == board.Destination)) return true;
        }

        return false;
    }

    /// <summary>
    /// A target set member that counts as standing (<c>FUN_14028eca0</c>): in the mission town, not caged, at posture 0 (not out, staying low,
    /// crawling or playing dead).
    /// </summary>
    internal static bool Stands(CharacterCold c, bool inMissionTown) => inMissionTown && !c.IsImprisoned && CharacterFacts.PostureZero(c);

    /// <summary>
    /// The start rule's part of the target set: every person of the attacked faction within <see cref="AssaultReach"/> of the mission
    /// town (<c>FUN_1402900c0</c>).
    /// </summary>
    void SeedAssaultTargets(World world, Blackboard board)
    {
        board.AssaultSeeded = true;
        var at = DestinationCentre(world, board);
        if (at is not { } centre) return;
        var table = world.Characters;
        for (int i = 0; i < table.Previous.Length; i++)
        {
            if (!table.Previous[i].Alive || table.Cold(i) is not { } c || !OfFaction(world, c, board.AssaultEnemy)) continue;
            if (Vector2.DistanceSquared(Flat(table.Previous[i].Position), centre) > AssaultReach * AssaultReach) continue;
            board.AssaultTargets.Add(table.IdOf(i));
        }
    }

    /// <summary>
    /// Whether the assault is over (vtable +0x68, <c>FUN_14028eca0</c>): the squad no longer intact ends it for good; not before 0.25 h; while a
    /// defender stands it goes on; then it is over (for good) once at least 2 of the squad are up. A finished assault never starts again (+0xb4).
    /// </summary>
    bool AssaultFinished(World world, Squad squad, Blackboard board, AiPackage package)
    {
        bool done;
        if (!board.Intact) done = true;
        else if (world.GameHours - board.AssaultStart < AssaultMinHours || DefenderStanding(world, board)) return false;
        else done = MembersUp(world, squad) >= 2;
        if (done && board.Current == package) board.AssaultDone = true;
        return done;
    }

    /// <summary>
    /// Whether the assault hands over to its <c>Leads to</c>, the occupation (vtable +0x90, <c>FUN_14028ea90</c>): intact, no defender standing and
    /// at least 2 up.
    /// </summary>
    bool AssaultMayLead(World world, Squad squad, Blackboard board) =>
        board.Intact && !DefenderStanding(world, board) && MembersUp(world, squad) >= 2;

    #endregion

    #region Occupation

    /// <summary>
    /// The occupation ends after this long with no able member in the town (<b>Observed</b>: 4 of the timer the finished test adds to, read as
    /// game seconds).
    /// </summary>
    const float OccupyGiveUp = 4;

    /// <summary>
    /// A member's post on the ring of <see cref="PostsInto"/>: by member index, 20 + 6 per index from the centre.
    /// </summary>
    static Vector2 PostRound(Vector2 centre, int index)
    {
        float angle = index * 2.39996f, r = 20 + 6 * index;
        return centre + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
    }

    /// <summary>
    /// The post of a member whose mind is made while the squad occupies a town (a loaded or kept occupier resumes before its members have minds): on
    /// the town's ring, so its goals without a building there are not all acted out where it stood when it was saved.
    /// </summary>
    Vector2? OccupierPost(World world, Blackboard board, int index) =>
        board.CurrentClass == PackageClass.OccupyTown && DestinationCentre(world, board) is { } centre ? PostRound(centre, index) : null;

    /// <summary>
    /// The occupation's start (vtable +0x30, <c>FUN_140288fe0</c>): the town its leader is in is its mission town and that town's faction its enemy;
    /// every member says TAKEN_OVER_PLAYER_TOWN (73). The original also makes the platoon persistent while it lasts (the population keeps an occupier
    /// out of the world, <see cref="Occupies"/>) and lists it among the town's hostile occupiers (Town +0x118, its count +0x128, which the campaigns
    /// read: <see cref="Occupied"/>). The posts move into the town (<see cref="PostsInto"/>).
    /// </summary>
    void OccupyStarted(World world, Squad squad, Blackboard board) => Occupy(world, squad, board, TakenOverPlayerTown);

    /// <summary>
    /// An occupier come back into the world (<see cref="Blackboard.Resume"/>): the occupation's state again from the town it is in, without the
    /// members' line.
    /// </summary>
    void OccupyResumed(World world, Squad squad, Blackboard board) => Occupy(world, squad, board, null);

    /// <summary>
    /// Occupies the player town the squad stands in: the town becomes its destination, the town's owner its enemy, and its members take posts round
    /// the town's position (<paramref name="say"/>: the line they say, if any).
    /// </summary>
    void Occupy(World world, Squad squad, Blackboard board, int? say)
    {
        board.OccupyAway = 0;
        if (board.InTown.PlayerTown <= 0 || world.Buildings.FindTown(board.InTown.PlayerTown) is not { } town) return;
        board.Destination = board.InTown;
        board.AssaultEnemy = town.Owner;
        PostsInto(world, squad, Flat(town.Position), say);
    }

    /// <summary>
    /// The complete buildings of the player town the squad's occupation holds (0 without one): where its town goals walk (<see cref="OccupiedSpot"/>).
    /// </summary>
    static int OccupiedBuildings(World world, Blackboard board)
    {
        if (board.CurrentClass != PackageClass.OccupyTown || board.Destination.PlayerTown <= 0) return 0;
        int n = 0;
        foreach (var site in world.Buildings.All) if (site.Complete && site.Town == board.Destination.PlayerTown) n++;
        return n;
    }

    /// <summary>
    /// Where 37 Wandering town of an occupier walks: a random complete building of the player town it holds (<b>engine choice</b>: Meitou reads no
    /// nodes in a player town); null without one. 36 Patrolling town patrols round that town's centre (<see cref="PatrolArea"/>).
    /// </summary>
    static Vector3? OccupiedSpot(World world, Blackboard board, CharacterId self)
    {
        int n = OccupiedBuildings(world, board);
        if (n == 0) return null;
        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, SeedSalts.AiTarget ^ (ulong)world.Tick << 8 ^ 0x4F), n);
        foreach (var site in world.Buildings.All)
            if (site.Complete && site.Town == board.Destination.PlayerTown && pick-- == 0) return site.Position;
        return null;
    }

    /// <summary>
    /// The bed an occupier with no home sleeps in (153 Go home go to bed): the original's NPC bed search falls back to the nearest beds of allies,
    /// and its allies test passes the occupied town's buildings (<c>canIUseThisBuilding</c>, docs/game/locks.md "Beds", <b>Verified</b> by
    /// decompile). Meitou takes the <see cref="AiMind.MemberIndex"/>-th (round robin) complete BF_BED of the occupied town that is reachable for it
    /// and not held by someone outside its squad, so the members spread over the beds and keep theirs as the others lie down (<b>engine choice</b>);
    /// null without one.
    /// </summary>
    static Locks.Fixture? OccupiedBed(World world, Blackboard board, AiMind mind, int slot)
    {
        if (board.CurrentClass != PackageClass.OccupyTown || board.Destination.PlayerTown <= 0 || world.Characters.Cold(slot) is not { } self)
            return null;

        var table = world.Characters;
        bool Takes(Locks.Fixture f) =>
            f.Kind == Locks.FixtureKind.Bed && f.Bed.Function == Meitou.Data.World.TownBuildings.BedFunction
            && world.Buildings.Find(f.Building) is { Complete: true } site && site.Town == board.Destination.PlayerTown
            && world.Fixtures.Reachable(world, f, self.Faction)
            && (Locks.FixtureRegistry.IsFree(table, f) || table.TryResolveNext(f.Occupant, out int o) && table.Cold(o)?.SquadId == mind.Squad.Id);
        int count = 0;
        foreach (var f in world.Fixtures.All) if (Takes(f)) count++;
        if (count == 0) return null;
        int want = mind.MemberIndex % count;
        foreach (var f in world.Fixtures.All)
            if (Takes(f) && want-- == 0) return f;
        return null;
    }

    /// <summary>
    /// Whether a package is an occupation (<c>Package_OccupyTown</c>, 30), whose platoon the original keeps while it lasts (persistent); false for
    /// null or an unknown id.
    /// </summary>
    public static bool Occupies(AiDatabase ai, string? package) =>
        package is not null && ai.Package(package) is { } p && PackageClasses.Of(p.Signal) == PackageClass.OccupyTown;

    /// <summary>
    /// Whether the occupation is over (vtable +0x68, <c>FUN_140289290</c>): the town gone; or no member alive and up in it (<c>FUN_140289180</c>) for
    /// more than 4 game seconds in a row (counted on the package cadence).
    /// </summary>
    bool OccupyFinished(World world, Squad squad, Blackboard board)
    {
        if (board.Destination.PlayerTown <= 0 || world.Buildings.FindTown(board.Destination.PlayerTown) is null) return true;
        var table = world.Characters;
        foreach (var m in squad.Members)
        {
            if (Up(world, m) && CurrentTown(world, table.Previous[m.Slot].Position) == board.Destination)
            {
                board.OccupyAway = 0;
                return false;
            }
        }

        board.OccupyAway += world.Every(Settings.PackageEveryTicks) * world.TickSeconds;
        return board.OccupyAway > OccupyGiveUp;
    }

    /// <summary>
    /// The occupation gives way (vtable +0x38, <c>FUN_140296150</c>): "{1} squad has been removed from {2}" (its faction, the town) while the
    /// town stands.
    /// </summary>
    void OccupyEnded(World world, Squad squad, Blackboard board)
    {
        if (board.Destination.PlayerTown <= 0 || world.Buildings.FindTown(board.Destination.PlayerTown) is not { } town) return;
        string faction = squad.Faction >= 0 && squad.Faction < data.Factions.Count ? data.Factions[squad.Faction].Name : "";
        world.Post(Loc.Tr("{1} squad has been removed from {2}", faction, town.Name));
    }

    /// <summary>
    /// Whether a town has hostile occupiers (the original's Town +0x128, the count of its occupier set): a squad whose running occupation holds it.
    /// </summary>
    public static bool Occupied(World world, CampaignTown town)
    {
        foreach (var squad in world.Squads.All)
            if (squad.Ai is { CurrentClass: PackageClass.OccupyTown } board && board.Destination == town) return true;

        return false;
    }

    #endregion

    #region Refugee

    /// <summary>
    /// A refugee joins the squad it makes for within this (squared 1e6, <b>Verified</b> in its update <c>FUN_14028d190</c>); a squad it joins has
    /// more than 2 members (<c>FUN_1407ec0c0</c>).
    /// </summary>
    const float RefugeeJoinRange = 1000;

    /// <summary>
    /// The platoon grid of the original's squad search (<c>FUN_1408f56b0</c>): square cells of 4608, the world offset by 147465, 64 by 64.
    /// </summary>
    const float PlatoonCell = 4608, PlatoonCellOffset = 147465;

    /// <summary>
    /// Squads to fold into another once the serial look is done (the registry cannot change while it is walked): (the refugees, the squad they join).
    /// </summary>
    readonly List<(Squad From, Squad Into)> _merges = [];

    /// <summary>
    /// The members not dead (a body stays on its squad's roll until it decays; the original's count, squad +0x58 and platoon +0xa4, is read as the
    /// living ones, <b>Unknown</b>).
    /// </summary>
    static int Living(World world, Squad squad)
    {
        int n = 0;
        foreach (var m in squad.Members)
            if (world.Characters.TryResolveNext(m, out int s) && world.Characters.Cold(s)!.Medical is not { Dead: true }) n++;
        return n;
    }

    /// <summary>
    /// Whether a refugee package may start (vtable +0x60, <c>FUN_140288ed0</c>): its leader is a slave or an escaped slave (the status object's first
    /// int, <c>FUN_1405c8aa0</c>: <see cref="CharacterCold.SlaveState"/> above 0) and the squad has fewer than 3 members. The original also starts it
    /// when its home town's vfunc 0x288 holds (<c>TownBase +0xe0 → +8</c>, read as "destroyed"); Meitou's towns are never destroyed, so that half is
    /// not built.
    /// </summary>
    static bool RefugeeCanStart(World world, Squad squad) =>
        world.Characters.TryResolveNext(squad.Leader, out int leader) && world.Characters.Cold(leader)!.SlaveState > 0 && Living(world, squad) < 3;

    /// <summary>
    /// The refugee's start (vtable +0x30, <c>FUN_140287760</c>): "broken" when the squad is not intact, no target squad yet, and the nearest town of
    /// its faction other than its home as the mission town (<c>FUN_140927ad0</c>; nest markers are not left out, as there). The original also makes
    /// the platoon not persistent and, with a destroyed home, re-homes it to that town.
    /// </summary>
    void RefugeeStarted(World world, Squad squad, Blackboard board)
    {
        board.RefugeeBroken = !board.Intact;
        board.RefugeeTarget = -1;
        if (LeaderPosition(world, squad) is not { } at) return;
        int best = -1;
        float bestD = float.MaxValue;
        foreach (var site in data.Sites)
        {
            if (site.Index == board.Town || site.Index >= data.SiteFactions.Count || data.SiteFactions[site.Index] != squad.Faction) continue;
            float d = Vector2.DistanceSquared(Flat(at), Flat(site.Position));
            if (d < bestD) (best, bestD) = (site.Index, d);
        }

        board.Destination = best >= 0 ? CampaignTown.OfSite(best) : CampaignTown.None;
    }

    /// <summary>
    /// Whether the refugee is done (vtable +0x68, <c>FUN_140288f80</c>): its leader stands in the mission town.
    /// </summary>
    bool RefugeeFinished(World world, Squad squad, Blackboard board) =>
        world.Characters.IsAlive(squad.Leader) && !board.InTown.IsNone && board.InTown == board.Destination;

    /// <summary>
    /// The refugee's update (vtable +0x48, <c>FUN_14028d190</c>): a broken one looks for the squad to join, the nearest of its faction in its grid
    /// cell that is intact and has more than 2 members (<c>FUN_14028c920</c>; Meitou: among the squads the AI runs, as it cannot hand members to a
    /// roaming one); the travel goals walk it there; within 1000 of it every member joins that squad and the refugee squad is removed. A target that
    /// is itself a refugee of the same package forgets its own target.
    /// </summary>
    void StepRefugee(World world, Squad squad, Blackboard board)
    {
        if (!board.RefugeeBroken || LeaderPosition(world, squad) is not { } at) return;
        if (board.RefugeeTarget < 0)
        {
            board.RefugeeTarget = RefugeeTargetFor(world, squad, at)?.Id ?? -1;
            return;
        }

        if (world.Squads.Find(board.RefugeeTarget) is not { } target || LeaderPosition(world, target) is not { } there)
        {
            board.RefugeeTarget = -1;
            return;
        }

        if (Vector3.DistanceSquared(at, there) >= RefugeeJoinRange * RefugeeJoinRange) return;
        if (target.Ai is { Current: { } running } theirs && board.Current is { } mine && running.Id == mine.Id) theirs.RefugeeTarget = -1;
        _merges.Add((squad, target));
    }

    /// <summary>
    /// The nearest intact squad of the same faction with more than 2 living members in the same platoon cell as <paramref name="at"/>; null when
    /// there is none.
    /// </summary>
    Squad? RefugeeTargetFor(World world, Squad squad, Vector3 at)
    {
        Squad? best = null;
        float bestD = float.MaxValue;
        foreach (var other in world.Squads.All)
        {
            if (other == squad || other.Faction != squad.Faction || other.Ai is not { Intact: true } || Living(world, other) <= 2) continue;
            if (LeaderPosition(world, other) is not { } there) continue;
            if (PlatoonCellOf(there.X) != PlatoonCellOf(at.X) || PlatoonCellOf(there.Z) != PlatoonCellOf(at.Z)) continue;
            float d = Vector3.DistanceSquared(at, there);
            if (d < bestD) (best, bestD) = (other, d);
        }

        return best;
    }

    /// <summary>
    /// The platoon cell (0..63) of one world coordinate (<see cref="PlatoonCell"/> wide, from <see cref="PlatoonCellOffset"/>).
    /// </summary>
    static int PlatoonCellOf(float v) => Math.Clamp((int)((v + PlatoonCellOffset) / PlatoonCell), 0, 63);

    /// <summary>
    /// Folds the squads <see cref="StepRefugee"/> queued into theirs (after the look; a squad that was itself folded away meanwhile waits for
    /// the next).
    /// </summary>
    void ApplyMerges(World world)
    {
        foreach (var (from, into) in _merges)
            if (world.Squads.Find(from.Id) == from && world.Squads.Find(into.Id) == into) SquadManagement.Merge(world, from, into);
        _merges.Clear();
    }

    #endregion

    #region War packages

    /// <summary>
    /// The war classes' finished test (<c>Package_WarBase</c>, <c>Package_WarAssault</c> and its cannibal kind share <c>FUN_14028fdb0</c>,
    /// <b>Verified</b>): the squad's platoon is in no campaign. Their start (the campaign's target as mission town, the leader platoon as a
    /// follower's mission target, the unloaded job 5 or 6; <c>FUN_14028fc80</c>) is left to the campaign, whose march moves the squad and whose
    /// stand-in takes those jobs (engine stand-in until goal 215 is acted out).
    /// </summary>
    internal bool OutOfCampaign(World world, Squad squad) =>
        squad.PlatoonId < 0 || world.Platoons.Find(squad.PlatoonId)?.Campaign is null;

    /// <summary>
    /// <c>Campaign_DefendPlayerBase</c>'s finished test (<c>FUN_14028a760</c>, <b>Verified</b>): its employer turned on it (not modelled, as for
    /// <c>Contract_DefendPlayerBase</c>), or its contract hours (+0x218) have passed and no hostile campaign runs (<c>FUN_1407f2e80</c>, the same
    /// call as the defence's phase-1 victory; <see cref="PopulationSystem.HostileAgainstPlayer"/>).
    /// </summary>
    internal bool CampaignDefendFinished(World world, Blackboard board) =>
        world.GameHours > board.ContractEnd && world.SystemOf<PopulationSystem>()?.HostileAgainstPlayer(world) != true;

    /// <summary>
    /// The start <c>Campaign_DefendPlayerBase</c> shares with <c>Contract_DefendPlayerBase</c> (<c>FUN_14028a170</c>, <b>Verified</b>): the player
    /// town nearest the leader is its mission town and its mission (<see cref="Blackboard.AimMissionAtTown"/>), so its sensing hook exempts the
    /// town's owner (<c>AggroSystem.EmployerOf</c>). Engine stand-in: the blackboard's target town (+0xf0) is left unset while the campaign march
    /// moves the squad, as it would make the package travel on its own (<see cref="BoardMoves"/>).
    /// </summary>
    internal void CampaignDefendStart(World world, Squad squad, Blackboard board)
    {
        var at = LeaderPosition(world, squad) ?? squad.Position;
        board.ContractTown = NearestPlayerTown(world, Flat(at))?.Town ?? CampaignTown.None;
        board.AimMissionAtTown();
    }

    /// <summary>
    /// The war classes' update on the package cadence, whoever moves the squad: a war assault (20, 32) marks every member "ordered"
    /// (<see cref="CharacterCold.Ordered"/>; <c>FUN_14028a4e0</c>, <b>Verified</b>: each update, no other condition), so a blow at one books no
    /// Assault, and the first step after it stops running (ended, replaced or swapped) clears the marks of the squad's members
    /// (<see cref="Blackboard.MarksOrdered"/>; engine stand-in for the original's clear at each task end, <c>FUN_1405c91c0</c>, which lets the mark
    /// lapse soon after the package stops renewing it); the campaign's defence without a mission town runs its start again (its update
    /// <c>FUN_140291410</c>, <b>Verified</b>).
    /// </summary>
    void StepWar(World world, Squad squad, Blackboard board)
    {
        switch (board.CurrentClass)
        {
            case PackageClass.WarAssault:
            case PackageClass.WarAssaultCannibal:
                SetOrdered(world, squad, true);
                board.MarksOrdered = true;
                return;
            case PackageClass.CampaignDefend when board.ContractTown.IsNone:
                CampaignDefendStart(world, squad, board);
                break;
        }

        if (!board.MarksOrdered) return;
        SetOrdered(world, squad, false);
        board.MarksOrdered = false;
    }

    /// <summary>
    /// Sets or clears the "ordered" status of every member of <paramref name="squad"/>.
    /// </summary>
    static void SetOrdered(World world, Squad squad, bool on)
    {
        var table = world.Characters;
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s)) table.Cold(s)!.Ordered = on;
    }

    #endregion
}
