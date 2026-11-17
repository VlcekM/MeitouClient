using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The AI dialogue gave a squad, on its PLATOON record (docs/formats/save.md "NPC platoons"): <c>contractjob</c> (the contract package id, "" for
/// none), <c>jobhr</c> (a float: the game hours it ends at) and <c>replacementAI</c> (the <c>change AI</c> package id, "" for the template's
/// packages). The running package writes and reads its own hands (<c>FUN_140269640</c> / <c>FUN_140269790</c>, <b>Verified</b>): <c>mission
/// target</c>, <c>mission town</c> and <c>mission employer</c>; the load starts the package first and then reads them over what the start set
/// (<c>FUN_140272c20</c>).
/// </summary>
public sealed record AiSave(string? Contract, double ContractEnd, string? Replacement)
{
    /// <summary>
    /// The prefixes of the contract's hands: its mission target (the employer a bodyguard follows; a base defender's outpost), its mission town
    /// (<see cref="Blackboard.ContractTown"/>) and its employer (the hirer, whom the end talk is said to).
    /// </summary>
    public const string TargetKey = "mission target", MissionTownKey = "mission town", EmployerKey = "mission employer";

    /// <summary>
    /// The prefix of the blackboard's target town (+0xf0, <see cref="Blackboard.Destination"/>), which the blackboard writes beside its package
    /// keys (<c>FUN_14026c2d0</c>, the null handle without one) and its load reads back when it names one (<c>FUN_140272c20</c>), <b>Verified</b>.
    /// </summary>
    public const string TargetTownKey = "target town";

    /// <summary>
    /// The contract's mission town as a town of the world (none: not known as one, see <see cref="MissionTownHand"/>).
    /// </summary>
    public CampaignTown MissionTown { get; init; } = CampaignTown.None;

    /// <summary>
    /// The contract's mission town as the save names it (the null handle: none, or only known as <see cref="MissionTown"/>).
    /// </summary>
    public Hand MissionTownHand { get; init; } = Hand.Null;

    /// <summary>
    /// The blackboard's target town while the contract runs (a base defender's outpost; none for a bodyguard, whose start clears it), see
    /// <see cref="TargetTownHand"/>.
    /// </summary>
    public CampaignTown TargetTown { get; init; } = CampaignTown.None;

    /// <summary>
    /// The blackboard's target town as the save names it (the null handle: none, or only known as <see cref="TargetTown"/>).
    /// </summary>
    public Hand TargetTownHand { get; init; } = Hand.Null;

    /// <summary>
    /// The contract's mission target as a character of the world (none: not known as one, see <see cref="TargetHand"/>).
    /// </summary>
    public CharacterId Target { get; init; } = CharacterId.None;

    /// <summary>
    /// The contract's mission target as the save names it (the null handle: none, or only known as <see cref="Target"/>).
    /// </summary>
    public Hand TargetHand { get; init; } = Hand.Null;

    /// <summary>
    /// The contract's mission employer as a character of the world (none: not known as one, see <see cref="EmployerHand"/>).
    /// </summary>
    public CharacterId Employer { get; init; } = CharacterId.None;

    /// <summary>
    /// The contract's mission employer as the save names it (the null handle: none, or only known as <see cref="Employer"/>).
    /// </summary>
    public Hand EmployerHand { get; init; } = Hand.Null;

    /// <summary>
    /// The mission target is the mission town (<c>Contract_DefendPlayerBase</c>, <see cref="Blackboard.AimMissionAtTown"/>): the town's hand is
    /// written under both keys.
    /// </summary>
    public bool TargetIsTown { get; init; }

    /// <summary>
    /// The PLATOON keys: <c>contractjob</c> (the contract package id), <c>jobhr</c> (the game hour it ends at) and <c>replacementAI</c> (the
    /// <c>change AI</c> package id).
    /// </summary>
    public const string ContractKey = "contractjob", EndKey = "jobhr", ReplacementKey = "replacementAI";

    /// <summary>
    /// The PLATOON bool of a code mod's any-range contract (<see cref="Blackboard.MissionAnyRange"/>; engine key, written only when set, which the
    /// original ignores).
    /// </summary>
    public const string AnyRangeKey = "meitou any range";

    /// <summary>
    /// The contract's assault takes its mission target wherever it is (<see cref="Blackboard.MissionAnyRange"/>).
    /// </summary>
    public bool AnyRange { get; init; }

    /// <summary>
    /// What <paramref name="board"/> holds of it; null when it runs neither.
    /// </summary>
    public static AiSave? Of(Blackboard? board)
    {
        if (board is not { } b || b.Contract is null && b.Replacement is null) return null;

        bool contract = b.Contract is not null;
        return new AiSave(b.Contract?.Id, b.ContractEnd, b.Replacement?.Id)
        {
            Target = b.MissionTarget,
            Employer = b.MissionEmployer,
            MissionTown = contract ? b.ContractTown : CampaignTown.None,
            TargetTown = contract ? b.Destination : CampaignTown.None,
            TargetIsTown = b.Contract?.Signal == AiSystem.DefendBaseSignal,
            AnyRange = b.MissionAnyRange,
        };
    }

    /// <summary>
    /// Writes the keys over the platoon's (each of them, so a contract that ended since the save was read is cleared). The mission target and
    /// employer are written as the save named them; the capture writes the handle of a <see cref="Target"/> or <see cref="Employer"/> over them
    /// once the character has one. The towns are written with <paramref name="townHand"/> (a town's save handle; null: as the save named them).
    /// </summary>
    public static void Write(FcsRecord platoon, AiSave? ai, Func<CampaignTown, Hand>? townHand = null)
    {
        WriteMissionTown(platoon, ai, townHand);
        platoon.Strings[ContractKey] = ai?.Contract ?? "";
        platoon.Strings[ReplacementKey] = ai?.Replacement ?? "";
        if (ai?.Contract is not null) platoon.Floats[EndKey] = (float)ai.ContractEnd;
        if (ai is { Contract: not null, AnyRange: true }) platoon.Bools[AnyRangeKey] = true;
        else platoon.Bools.Remove(AnyRangeKey);

        // A record without a contract keeps a null handle as it was read (its other parts are stale values).
        var target = ai?.Contract is null
            ? Hand.Null
            : ai.TargetIsTown
                ? TownHand(ai.MissionTown, ai.MissionTownHand, townHand)
                : ai.TargetHand;
        WriteHand(platoon, TargetKey, target);
        if (ai?.Contract is null) return;

        // The employer only once known; the target town while the contract runs (the null handle for a bodyguard's).
        if (!ai.EmployerHand.IsNull) ai.EmployerHand.Write(platoon, EmployerKey);
        WriteHand(platoon, TargetTownKey, TownHand(ai.TargetTown, ai.TargetTownHand, townHand));
    }

    /// <summary>
    /// The contract's mission town handle: written while a contract runs, cleared once one the record held has ended, else left as the record has it
    /// (another package's own, which Meitou does not read).
    /// </summary>
    static void WriteMissionTown(FcsRecord platoon, AiSave? ai, Func<CampaignTown, Hand>? townHand)
    {
        bool held = platoon.Strings.GetValueOrDefault(ContractKey) is { Length: > 0 };
        if (ai?.Contract is null && !held) return;

        var town = ai?.Contract is null ? Hand.Null : TownHand(ai.MissionTown, ai.MissionTownHand, townHand);
        WriteHand(platoon, MissionTownKey, town);
    }

    /// <summary>
    /// The handle a town is written with: the world's town by <paramref name="townHand"/>, else the handle the save named.
    /// </summary>
    static Hand TownHand(CampaignTown town, Hand saved, Func<CampaignTown, Hand>? townHand) =>
        !town.IsNone && townHand is not null ? townHand(town) : saved;

    /// <summary>
    /// Writes one hand; a null one over a null handle keeps it as it was read (its other parts are stale values).
    /// </summary>
    static void WriteHand(FcsRecord platoon, string key, Hand hand)
    {
        if (!hand.IsNull || !Hand.Read(platoon, key).IsNull) hand.Write(platoon, key);
    }

    /// <summary>
    /// The AI the platoon record holds; null when it has neither a contract nor a replacement package.
    /// </summary>
    public static AiSave? Read(FcsRecord platoon)
    {
        string? contract = platoon.Strings.GetValueOrDefault(ContractKey) is { Length: > 0 } c ? c : null;
        string? replacement = platoon.Strings.GetValueOrDefault(ReplacementKey) is { Length: > 0 } r ? r : null;
        if (contract is null && replacement is null) return null;

        Hand HandOf(string key) => contract is null ? Hand.Null : Hand.Read(platoon, key);
        bool anyRange = contract is not null && platoon.Bools.GetValueOrDefault(AnyRangeKey);
        return new AiSave(contract, platoon.Floats.GetValueOrDefault(EndKey), replacement)
        {
            TargetHand = HandOf(TargetKey),
            EmployerHand = HandOf(EmployerKey),
            MissionTownHand = HandOf(MissionTownKey),
            TargetTownHand = HandOf(TargetTownKey),
            AnyRange = anyRange,
        };
    }

    /// <summary>
    /// Puts it on a squad's new blackboard: the replacement first (its packages), then the contract with its saved end, mission target, employer,
    /// mission town and target town (<paramref name="characterOf"/> and <paramref name="townOf"/> find the character and town a saved handle names).
    /// Unknown packages are skipped.
    /// </summary>
    public void Apply(Blackboard board, AiDatabase ai, IPackageRules rules, Func<Hand, CharacterId?>? characterOf = null,
        Func<Hand, CampaignTown>? townOf = null)
    {
        if (Replacement is not null && ai.Package(Replacement) is { } replacement) board.Replace(replacement, rules);
        if (Contract is null || ai.Package(Contract) is not { } contract) return;

        var target = Target.IsNone ? characterOf?.Invoke(TargetHand) ?? CharacterId.None : Target;
        var employer = Employer.IsNone ? characterOf?.Invoke(EmployerHand) ?? CharacterId.None : Employer;
        var town = MissionTown.IsNone ? townOf?.Invoke(MissionTownHand) ?? CampaignTown.None : MissionTown;
        var targetTown = TargetTown.IsNone ? townOf?.Invoke(TargetTownHand) ?? CampaignTown.None : TargetTown;
        board.ResumeContract(contract, ContractEnd, rules, target, AnyRange, employer, town, targetTown);
    }
}
