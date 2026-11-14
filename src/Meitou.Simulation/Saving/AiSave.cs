using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Save;
using Meitou.Simulation.Ai;

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
    /// What <paramref name="board"/> holds of it; null when it runs neither.
    /// </summary>
    public static AiSave? Of(Blackboard? board) =>
        board is { } b && (b.Contract is not null || b.Replacement is not null)
            ? new AiSave(b.Contract?.Id, b.ContractEnd, b.Replacement?.Id) { Target = b.MissionTarget }
            : null;

    /// <summary>
    /// Writes the keys over the platoon's (each of them, so a contract that ended since the save was read is cleared). The mission target is
    /// written as the save named it; the capture writes the handle of a <see cref="Target"/> over it once the character has one.
    /// </summary>
    public static void Write(FcsRecord platoon, AiSave? ai)
    {
        platoon.Strings[ContractKey] = ai?.Contract ?? "";
        platoon.Strings[ReplacementKey] = ai?.Replacement ?? "";
        if (ai?.Contract is not null) platoon.Floats[EndKey] = (float)ai.ContractEnd;

        // A record without a contract keeps a null handle as it was read (its other parts are stale values).
        var target = ai?.Contract is not null ? ai.TargetHand : Hand.Null;
        if (!target.IsNull || !Hand.Read(platoon, TargetKey).IsNull) target.Write(platoon, TargetKey);
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
        return new AiSave(contract, platoon.Floats.GetValueOrDefault(EndKey), replacement) { TargetHand = target };
    }

    /// <summary>
    /// Puts it on a squad's new blackboard: the replacement first (its packages), then the contract with its saved end and mission target
    /// (<paramref name="characterOf"/> finds the character a saved handle names). Unknown packages are skipped.
    /// </summary>
    public void Apply(Blackboard board, AiDatabase ai, IPackageRules rules, Func<Hand, CharacterId?>? characterOf = null)
    {
        if (Replacement is not null && ai.Package(Replacement) is { } replacement) board.Replace(replacement, rules);
        if (Contract is null || ai.Package(Contract) is not { } contract) return;

        var target = Target.IsNone ? characterOf?.Invoke(TargetHand) ?? CharacterId.None : Target;
        board.ResumeContract(contract, ContractEnd, rules, target);
    }
}
