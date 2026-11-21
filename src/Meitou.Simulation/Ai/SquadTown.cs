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
    /// <c>FUN_140269cd0</c>: the override package's mission town, set or not (a contract or a campaign's phase package, the running package while
    /// it runs, <see cref="RunningTown"/>); else the campaign's target when it is a town or nest; else, with a running package, that package's
    /// mission town, set or not; only with no running package and <paramref name="homeFallback"/> the squad's home town (<see cref="Home"/>); else
    /// none. States 36 and 94 and the travel task ask with the fallback, state 32 without.
    /// </summary>
    public static CampaignTown Of(World world, Squad squad, Blackboard board, bool homeFallback)
    {
        if (board.Contract is not null) return RunningTown(world, squad, board);
        if (CampaignTarget(world, squad) is { IsNone: false } target) return target;
        if (board.Current is not null) return RunningTown(world, squad, board);

        return homeFallback ? Home(world, squad, board) : CampaignTown.None;
    }

    /// <summary>
    /// The running package's mission town (the blackboard's current package <c>+0x48</c>, its hand <c>+0x8</c>), the hand the carried-body cage
    /// desire tests (<c>FUN_1405a0f10</c>, <b>Verified</b>): none without a running package or with its hand unset.
    /// </summary>
    public static CampaignTown MissionTown(World world, Squad squad, Blackboard board) =>
        board.Contract is null && board.Current is null ? CampaignTown.None : RunningTown(world, squad, board);

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
    /// the trader's drawn town and the refugee's (<see cref="Blackboard.Destination"/>); the passing assault's and the occupation's the town their
    /// leader stands in, else the home town the base start they run first set (<c>FUN_1402900c0</c>, <c>FUN_140288fe0</c>); the shipping's
    /// and the cannibal retreat's town (<see cref="Blackboard.MissionTown"/>, the retreat's else the home town); none for the patrols, whose start
    /// (<c>FUN_140287060</c>) only stamps its hour; the bar classes' bar town (<c>FUN_1402916b0</c>: the town of the home building, else the home
    /// town; <see cref="BarSite"/>); the war packages' campaign target (<c>FUN_14028fc80</c>, the campaign's vfunc 0xa0; with no campaign the home town); the contracts'
    /// as <see cref="ContractHand"/>; for every other class the base start (<c>FUN_1402856c0</c>) makes it the home town.
    /// </summary>
    static CampaignTown RunningTown(World world, Squad squad, Blackboard board) => board.CurrentClass switch
    {
        PackageClass.WanderingTrader or PackageClass.Refugee => board.Destination,
        PackageClass.PassingAssault or PackageClass.OccupyTown => board.Destination.IsNone ? Home(world, squad, board) : board.Destination,
        PackageClass.SlaverShipping => board.MissionTown >= 0 ? CampaignTown.OfSite(board.MissionTown) : CampaignTown.None,
        PackageClass.CannibalRetreat => board.MissionTown >= 0 ? CampaignTown.OfSite(board.MissionTown) : Home(world, squad, board),
        PackageClass.Patrol or PackageClass.PatrolRunning => CampaignTown.None,
        PackageClass.WarBase or PackageClass.WarAssault or PackageClass.WarAssaultCannibal => WarTown(world, squad, board),
        PackageClass.Contract or PackageClass.CampaignDefend => ContractHand(world, squad, board),
        PackageClass.Bar or PackageClass.BarOutdoors or PackageClass.CannibalsAtHome => BarSite(squad, board) is >= 0 and var bar
            ? CampaignTown.OfSite(bar)
            : Home(world, squad, board),
        _ => Home(world, squad, board),
    };

    /// <summary>
    /// The town a bar package uses: for a squad that is not a town's own, the placed town it is in; else its own town (<see cref="Blackboard.Town"/>,
    /// -1 without one).
    /// </summary>
    internal static int BarSite(Squad squad, Blackboard board) => !squad.TownOwn && board.InTown.Site >= 0 ? board.InTown.Site : board.Town;

    /// <summary>
    /// A war package's mission town: with a campaign its target (none when that is not a town or nest), else the home town.
    /// </summary>
    static CampaignTown WarTown(World world, Squad squad, Blackboard board)
    {
        if (squad.PlatoonId >= 0 && world.Platoons.Find(squad.PlatoonId)?.Campaign is { } campaign) return campaign.Target;

        return Home(world, squad, board);
    }

    /// <summary>
    /// A contract's mission town: the one its start or its save set (<see cref="Blackboard.ContractTown"/>; <c>Contract_HiredAlly</c> 12,
    /// <c>Contract_DefendPlayerBase</c> 15 and <c>Campaign_DefendPlayerBase</c> 31 the player's town nearest the squad, none without one); else for
    /// the classes on the base start (<c>Contract_UntilExSlave</c> 13, <c>Contract_UntilOutOfDanger</c> 14, <c>Contract_Timed</c> 33 and the plain
    /// package an override builds as it, vtable <c>+0x30</c> <c>FUN_1402856c0</c>, <b>Verified</b>) the home town.
    /// </summary>
    static CampaignTown ContractHand(World world, Squad squad, Blackboard board)
    {
        if (!board.ContractTown.IsNone) return board.ContractTown;

        return board.Current?.Signal is 12 or 15 or 31 ? CampaignTown.None : Home(world, squad, board);
    }

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
