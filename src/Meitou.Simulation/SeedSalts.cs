using Meitou.Simulation.Population;

namespace Meitou.Simulation;

/// <summary>
/// The constants mixed into the seed keys of the population's random streams, named in one place so two streams never share one by accident.
/// Changing any value re-rolls what it keys (and every state hash that follows), so they are pinned by the golden hashes.
/// </summary>
internal static class SeedSalts
{
    /// <summary>
    /// XORed into the key of a bar squad (a resident squad's key has none).
    /// </summary>
    public const ulong BarSquad = 0xBA45UL;
    /// <summary>
    /// XORed with the hash of the start-off's id into the player's start key.
    /// </summary>
    public const ulong PlayerStart = 0x57A27UL;
    /// <summary>
    /// Counter (under <see cref="RngPurpose.Spawn"/>, keyed by the start key) of the roll that picks the start town among the candidates.
    /// </summary>
    public const ulong PlayerStartTown = 0x57A28UL;
    /// <summary>
    /// XORed into the tick of a roaming squad's choice of its next town.
    /// </summary>
    public const ulong RoamNext = 0x7A6E7UL;
    /// <summary>
    /// XORed into the key of a roaming squad a town's pool makes.
    /// </summary>
    public const ulong RoamingPool = 0x20A4UL;
    /// <summary>
    /// Added to the count of slave squads in the key of the next slave squad.
    /// </summary>
    public const ulong SlaveSquad = 0x5A1AUL;

    /// <summary>
    /// XORed into the key of a homeless squad of an area sector (6.2).
    /// </summary>
    public const ulong HomelessSquad = 0x5EC7UL;
    /// <summary>
    /// XORed into the key of a homeless squad's patrol points, and of the area tick's sector picks (<c>PopulationHomeless.cs</c>).
    /// </summary>
    public const ulong HomelessPatrol = 0x5EC8UL, HomelessRefill = 0x5EC9UL;
    /// <summary>
    /// XORed into the key of a squad a nest makes (6.3).
    /// </summary>
    public const ulong NestSquad = 0x4E57UL;
    /// <summary>
    /// XORed into the key of the layout of the nests of a biome group (which nest, which cell, which spots).
    /// </summary>
    public const ulong NestLayout = 0x1A70UL;
    /// <summary>
    /// XORed into the key of a nest's <c>debris building</c> scatter (with its place: the original seeds it from the nest's X/Z).
    /// </summary>
    public const ulong NestFurniture = 0x4E5FUL;

    /// <summary>
    /// Counter of the roll that picks the template of a homeless squad.
    /// </summary>
    public const ulong HomelessPick = 6;
    /// <summary>
    /// Counter of the roll that picks the template of a nest's squad.
    /// </summary>
    public const ulong NestPick = 7;
    /// <summary>
    /// Counter base of a rolled nest's place (marker or cell and spots), apart from its TOWN pick so a changed world-state gate never moves it.
    /// </summary>
    public const ulong NestPlace = 1UL << 20;
    /// <summary>
    /// Counter base of the placement tries of a wild squad (the try is added).
    /// </summary>
    public const ulong WildPlace = 2000;

    /// <summary>
    /// Counter (under <see cref="RngPurpose.Spawn"/>) of the roll that decides whether a bar squad is present.
    /// </summary>
    public const ulong BarChance = 77;
    /// <summary>
    /// Counter (under <see cref="RngPurpose.Think"/>) of the roll that picks which of a town's bars a squad starting a bar package goes to.
    /// </summary>
    public const ulong BarPick = 78;
    /// <summary>
    /// Counter base (under <see cref="RngPurpose.Think"/>, xor the mind's node epoch) of the roll that draws which node a standing goal takes in its
    /// building (FUN_14099f280: uniform among the free nodes, kept until the goal is renewed).
    /// </summary>
    public const ulong NodePick = 0x4E6F6465_50696B21;
    /// <summary>
    /// Counter of the roll that picks which squad template a town's roaming pool draws.
    /// </summary>
    public const ulong RoamingPick = 5;
    /// <summary>
    /// Counter base of the roll for the look of member n of a town squad (n is added).
    /// </summary>
    public const ulong MemberLook = 1000;
    /// <summary>
    /// Counter base of the roll for the look of member n of the player's squad (n is added).
    /// </summary>
    public const ulong PlayerLook = 5000;
    /// <summary>
    /// Counter base of the rolls that scatter member n of the player's squad made from a CHARACTER entry: x at 2n, z at 2n + 1 added.
    /// </summary>
    public const ulong PlayerScatter = 6000;
    /// <summary>
    /// Counters (under <see cref="RngPurpose.Spawn"/>, keyed by the character's id) of a new NPC's bounty roll and its fuzz
    /// (<see cref="Crime.SpawnBounties"/>).
    /// </summary>
    public const ulong BountyChance = 0xB0C7UL, BountyFuzz = 0xB0F2UL;
    /// <summary>
    /// The AI's patrol timer and route (<see cref="Ai.AiSystem"/>).
    /// </summary>
    public const ulong AiPatrol = 0x5041UL;
    /// <summary>
    /// The AI's desire rolls, commitment lengths and town targets (<see cref="Ai.AiSystem"/>; the tick and goal type are mixed in).
    /// </summary>
    public const ulong AiDesire = 0x5043UL, AiCommit = 0x5044UL, AiTarget = 0x5045UL;
    /// <summary>
    /// The pick of the house a squad moves to when the player buys its home (<see cref="Ai.AiSystem.Evict"/>; the tick is mixed in).
    /// </summary>
    public const ulong AiRehome = 0x504BUL;
    /// <summary>
    /// The package classes' rolls (<see cref="Ai.AiSystem"/>; the tick is mixed in): a trader's stay and its next town, a passing-by assault's wait.
    /// </summary>
    public const ulong AiTraderWait = 0x5046UL, AiTraderTown = 0x5047UL, AiAssaultWait = 0x5048UL;
    /// <summary>
    /// The rolls of a shopper's purchase (<see cref="Ai.AiSystem"/>, under <see cref="RngPurpose.Trade"/>; the tick and the attempt are mixed in):
    /// which item, and whether the buyer keeps it.
    /// </summary>
    public const ulong AiShop = 0x504AUL;
    /// <summary>
    /// A platoon's short-range patrol point off screen (<see cref="Ai.PlatoonJobs"/>).
    /// </summary>
    public const ulong PlatoonPatrol = 0x5050UL;

    /// <summary>
    /// Town residents in their buildings (<c>PopulationHomes.cs</c>): the counter of the node a squad is made at, of a member's own node, counter
    /// base of the housemate counts, and the salt XORed into a housed squad's key.
    /// </summary>
    public const ulong HomeSpot = 0x40DE5UL, MemberSpot = 0x40DE6UL, Housemates = 0x40A7E00UL, ResidentHome = 0x2E51DUL;
    /// <summary>
    /// A housed squad's <c>prisoners</c> (<c>PopulationSystem.MakePrisoners</c>): the salt XORed into a prisoner squad's key, and counter base of the
    /// cage picks.
    /// </summary>
    public const ulong PrisonerSquad = 0x9215EUL, PrisonerCage = 0x921CA00UL;
    /// <summary>
    /// A housed squad's <c>slaves</c> (<c>PopulationSystem.MakeSlaves</c>): counter base of the slaves' looks, and of their cage picks.
    /// </summary>
    public const ulong SlaveLook = 0x5A1A100UL, SlaveCage = 0x5A1ACA00UL;

    /// <summary>
    /// Faction campaigns (<c>PopulationCampaigns.cs</c>): counters of a roll's pass test and its draw, a follow-up's pick and delay, a request's
    /// delay and retry; the salt XORed into a campaign squad's key, and counter base of its placement tries.
    /// </summary>
    public const ulong CampaignPass = 0xCA01UL, CampaignDraw = 0xCA02UL, CampaignFollowUp = 0xCA03UL, CampaignDelay = 0xCA04UL,
        CampaignRetry = 0xCA05UL,
        CampaignSquad = 0xCA5DUL, CampaignPlace = 0xCA90UL, CampaignTemplate = 0xCA70UL, CampaignDialogue = 0xCAD0UL, CampaignTrail = 0xCA7A0UL;
}