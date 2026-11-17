using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The AI dialogue gave a squad, on its PLATOON record (docs/formats/save.md "NPC platoons"): <c>contractjob</c> (the contract package id, "" for
/// none), <c>jobhr</c> (a float: the game hours it ends at) and <c>replacementAI</c> (the <c>change AI</c> package id, "" for the template's
/// packages). The contract's mission target is the handle <c>mission target</c>, which the running package writes and reads itself
/// (<c>FUN_140269640</c> / <c>FUN_140269790</c>).
/// </summary>
public sealed record AiSave(string? Contract, double ContractEnd, string? Replacement)
{
    /// <summary>
    /// The prefix of the handle of the contract's mission target (the employer a bodyguard follows).
    /// </summary>
    public const string TargetKey = "mission target";

    /// <summary>
    /// The prefix of the handle of the contract's mission town (a bodyguard's fallback, <see cref="Blackboard.ContractTown"/>), which the package
    /// writes and reads beside its mission target (<c>FUN_140269640</c> / <c>FUN_140269790</c>, <b>Verified</b>).
    /// </summary>
    public const string MissionTownKey = "mission town";

    /// <summary>
    /// The contract's mission town as a town of the world (none: not known as one, see <see cref="MissionTownHand"/>).
    /// </summary>
    public CampaignTown MissionTown { get; init; } = CampaignTown.None;

    /// <summary>
    /// The contract's mission town as the save names it (the null handle: none, or only known as <see cref="MissionTown"/>).
    /// </summary>
    public Hand MissionTownHand { get; init; } = Hand.Null;

    /// <summary>
    /// The contract's mission target as a character of the world (none: not known as one, see <see cref="TargetHand"/>).
    /// </summary>
    public CharacterId Target { get; init; } = CharacterId.None;

    /// <summary>
    /// The contract's mission target as the save names it (the null handle: none, or only known as <see cref="Target"/>).
    /// </summary>
    public Hand TargetHand { get; init; } = Hand.Null;

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
    public static AiSave? Of(Blackboard? board) =>
        board is { } b && (b.Contract is not null || b.Replacement is not null)
            ? new AiSave(b.Contract?.Id, b.ContractEnd, b.Replacement?.Id)
            {
                Target = b.MissionTarget, AnyRange = b.MissionAnyRange, MissionTown = b.Contract is null ? CampaignTown.None : b.ContractTown,
            }
            : null;

    /// <summary>
    /// Writes the keys over the platoon's (each of them, so a contract that ended since the save was read is cleared). The mission target is
    /// written as the save named it; the capture writes the handle of a <see cref="Target"/> over it once the character has one. The mission town
    /// is written with <paramref name="townHand"/> (a town's save handle; null: as the save named it).
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
        var target = ai?.Contract is not null ? ai.TargetHand : Hand.Null;
        if (!target.IsNull || !Hand.Read(platoon, TargetKey).IsNull) target.Write(platoon, TargetKey);
    }

    /// <summary>
    /// The contract's mission town handle: written while a contract runs, cleared once one the record held has ended, else left as the record has it
    /// (another package's own, which Meitou does not read).
    /// </summary>
    static void WriteMissionTown(FcsRecord platoon, AiSave? ai, Func<CampaignTown, Hand>? townHand)
    {
        bool held = platoon.Strings.GetValueOrDefault(ContractKey) is { Length: > 0 };
        if (ai?.Contract is null && !held) return;

        var town = ai?.Contract is null ? Hand.Null
            : !ai.MissionTown.IsNone && townHand is not null ? townHand(ai.MissionTown)
            : ai.MissionTownHand;
        if (!town.IsNull || !Hand.Read(platoon, MissionTownKey).IsNull) town.Write(platoon, MissionTownKey);
    }

    /// <summary>
    /// The AI the platoon record holds; null when it has neither a contract nor a replacement package.
    /// </summary>
    public static AiSave? Read(FcsRecord platoon)
    {
        string? contract = platoon.Strings.GetValueOrDefault(ContractKey) is { Length: > 0 } c ? c : null;
        string? replacement = platoon.Strings.GetValueOrDefault(ReplacementKey) is { Length: > 0 } r ? r : null;
        if (contract is null && replacement is null) return null;

        var target = contract is null ? Hand.Null : Hand.Read(platoon, TargetKey);
        var town = contract is null ? Hand.Null : Hand.Read(platoon, MissionTownKey);
        bool anyRange = contract is not null && platoon.Bools.GetValueOrDefault(AnyRangeKey);
        return new AiSave(contract, platoon.Floats.GetValueOrDefault(EndKey), replacement)
        {
            TargetHand = target, MissionTownHand = town, AnyRange = anyRange,
        };
    }

    /// <summary>
    /// Puts it on a squad's new blackboard: the replacement first (its packages), then the contract with its saved end, mission target and mission
    /// town (<paramref name="characterOf"/> and <paramref name="townOf"/> find the character and town a saved handle names). Unknown packages are
    /// skipped.
    /// </summary>
    public void Apply(Blackboard board, AiDatabase ai, IPackageRules rules, Func<Hand, CharacterId?>? characterOf = null,
        Func<Hand, CampaignTown>? townOf = null)
    {
        if (Replacement is not null && ai.Package(Replacement) is { } replacement) board.Replace(replacement, rules);
        if (Contract is null || ai.Package(Contract) is not { } contract) return;

        var target = Target.IsNone ? characterOf?.Invoke(TargetHand) ?? CharacterId.None : Target;
        board.ResumeContract(contract, ContractEnd, rules, target, AnyRange);
        board.ContractTown = MissionTown.IsNone ? townOf?.Invoke(MissionTownHand) ?? CampaignTown.None : MissionTown;
    }
}
