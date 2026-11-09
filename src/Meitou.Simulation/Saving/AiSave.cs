using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The AI dialogue gave a squad, on its PLATOON record (docs/formats/save.md "NPC platoons"): <c>contractjob</c> (the contract package id, "" for
/// none), <c>jobhr</c> (a float: the game hours it ends at) and <c>replacementAI</c> (the <c>change AI</c> package id, "" for the template's
/// packages). The contract's mission target is not kept: the original loads it with an empty target too.
/// </summary>
public sealed record AiSave(string? Contract, double ContractEnd, string? Replacement)
{
    /// <summary>
    /// The PLATOON keys: <c>contractjob</c> (the contract package id), <c>jobhr</c> (the game hour it ends at) and <c>replacementAI</c> (the
    /// <c>change AI</c> package id).
    /// </summary>
    public const string ContractKey = "contractjob", EndKey = "jobhr", ReplacementKey = "replacementAI";

    /// <summary>
    /// What <paramref name="board"/> holds of it; null when it runs neither.
    /// </summary>
    public static AiSave? Of(Blackboard? board) =>
        board is { } b && (b.Contract is not null || b.Replacement is not null) ? new AiSave(b.Contract?.Id, b.ContractEnd, b.Replacement?.Id) : null;

    /// <summary>
    /// Writes the keys over the platoon's (each of them, so a contract that ended since the save was read is cleared).
    /// </summary>
    public static void Write(FcsRecord platoon, AiSave? ai)
    {
        platoon.Strings[ContractKey] = ai?.Contract ?? "";
        platoon.Strings[ReplacementKey] = ai?.Replacement ?? "";
        if (ai?.Contract is not null) platoon.Floats[EndKey] = (float)ai.ContractEnd;
    }

    /// <summary>
    /// The AI the platoon record holds; null when it has neither a contract nor a replacement package.
    /// </summary>
    public static AiSave? Read(FcsRecord platoon)
    {
        string? contract = platoon.Strings.GetValueOrDefault(ContractKey) is { Length: > 0 } c ? c : null;
        string? replacement = platoon.Strings.GetValueOrDefault(ReplacementKey) is { Length: > 0 } r ? r : null;
        return contract is null && replacement is null ? null : new AiSave(contract, platoon.Floats.GetValueOrDefault(EndKey), replacement);
    }

    /// <summary>
    /// Puts it on a squad's new blackboard: the replacement first (its packages), then the contract with its saved end. Unknown packages are skipped.
    /// </summary>
    public void Apply(Blackboard board, AiDatabase ai, IPackageRules rules)
    {
        if (Replacement is not null && ai.Package(Replacement) is { } replacement) board.Replace(replacement, rules);
        if (Contract is not null && ai.Package(Contract) is { } contract) board.ResumeContract(contract, ContractEnd, rules);
    }
}
