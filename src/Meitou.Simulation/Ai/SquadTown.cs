using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The squad's town as the original's AI asks it (<c>FUN_140269cd0</c>, <b>Verified</b>; docs/game/ai-tasks.md "Town goals", 53 / 130): the town the
/// travel rows walk to, the town states 32 <c>hasDestinationTown</c>, 36 and 94 <c>atDestinationTown</c> test and the town 36 Patrolling town strolls
/// round. Pure reads of the board, the platoon and the population (any thread).
/// </summary>
internal static class SquadTown
{
    /// <summary>
    /// <c>FUN_140269cd0</c>: the override package's mission town, set or not (a contract or a campaign's phase package,
    /// <see cref="Blackboard.ContractMissionTown"/>); else the campaign's target when it is a town or nest; else, with a running package, that
    /// package's mission town, set or not (<see cref="RunningTown"/>); only with no running package and <paramref name="homeFallback"/> the
    /// squad's home town (<see cref="Home"/>); else none. States 36 and 94 and the travel task ask with the fallback, state 32 without.
    /// </summary>
    public static CampaignTown Of(World world, Squad squad, Blackboard board, bool homeFallback)
    {
        if (board.Contract is not null) return board.ContractMissionTown;
        if (CampaignTarget(world, squad) is { IsNone: false } target) return target;
        if (board.Current is not null) return RunningTown(world, squad, board);

        return homeFallback ? Home(world, squad, board) : CampaignTown.None;
    }

    /// <summary>
    /// States 36 and 94 <c>atDestinationTown</c> (<c>FUN_140507610</c>, <b>Verified</b>): the squad's town with the home fallback is the character's
    /// status town (<see cref="CharacterCold.StatusTown"/>, worked out from where it stands for one that holds none, as the status pass does on
    /// entering a town); no town: false (the evaluator then asks AI slot 11, the reach test of the row's own target, and the rows that need these
    /// states, 39, 192, 36, 53 and 130, carry none); no status town: false.
    /// </summary>
    public static bool Reached(World world, PopulationData data, int slot, Squad squad, Blackboard board)
    {
        var town = Of(world, squad, board, homeFallback: true);
        if (town.IsNone || world.Characters.Cold(slot) is not { } cold) return false;

        var status = cold.StatusTown.IsNone
            ? AiSystem.StatusTownAt(data, world, world.Characters.Previous[slot].Position, CampaignTown.None)
            : cold.StatusTown;
        return status == town;
    }

    /// <summary>
    /// The target of the campaign the squad's platoon marches in (the war manager's answer, a town or a nest); none outside one.
    /// </summary>
    static CampaignTown CampaignTarget(World world, Squad squad)
    {
        if (squad.PlatoonId < 0 || squad.Pinned || squad.Wild) return CampaignTown.None;

        return world.Platoons.Find(squad.PlatoonId)?.Campaign?.Target ?? CampaignTown.None;
    }

    /// <summary>
    /// The running package's mission town (its hand <c>+0x8</c>) as its class's start (vtable <c>+0x30</c>, <b>Verified</b> per class) leaves it:
    /// the trader's drawn town, the refugee's, the passing assault's and the occupation's town (<see cref="Blackboard.Destination"/>); the shipping's
    /// and the cannibal retreat's town (<see cref="Blackboard.MissionTown"/>, the retreat's else the home town); none for the patrols, whose start
    /// (<c>FUN_140287060</c>) only stamps its hour; the bar classes' bar town (<c>FUN_1402916b0</c>: the town of the home building, else the home
    /// town); for every other class the base start (<c>FUN_1402856c0</c>) makes it the home town.
    /// </summary>
    static CampaignTown RunningTown(World world, Squad squad, Blackboard board) => board.CurrentClass switch
    {
        PackageClass.WanderingTrader or PackageClass.Refugee or PackageClass.PassingAssault or PackageClass.OccupyTown => board.Destination,
        PackageClass.SlaverShipping => board.MissionTown >= 0 ? CampaignTown.OfSite(board.MissionTown) : CampaignTown.None,
        PackageClass.CannibalRetreat => board.MissionTown >= 0 ? CampaignTown.OfSite(board.MissionTown) : Home(world, squad, board),
        PackageClass.Patrol or PackageClass.PatrolRunning => CampaignTown.None,
        _ => Home(world, squad, board),
    };

    /// <summary>
    /// The platoon's Ownerships home town (<c>+0x30</c>): the squad's site or outpost (<see cref="Blackboard.HomeTown"/>), else the nest a nest squad
    /// was made for (its save's <c>hometown</c> is the nest, <b>Observed</b>); none without either.
    /// </summary>
    public static CampaignTown Home(World world, Squad squad, Blackboard board)
    {
        if (!board.HomeTown.IsNone) return board.HomeTown;

        int nest = world.SystemOf<PopulationSystem>()?.NestSiteOf(squad.Id) ?? -1;
        return nest >= 0 ? CampaignTown.OfNest(nest) : CampaignTown.None;
    }
}
