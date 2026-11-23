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
    /// The PLATOON bool of a contract that is a campaign's phase package (<see cref="Blackboard.PhaseContract"/>: it ends by its class's finished
    /// test, not its <c>jobhr</c>; engine key, written only when set, which the original ignores). A save without it (the original's own) loads
    /// the contract as a timed one until its campaign gives it again (<c>AiSystem.InstallPhasePackage</c>).
    /// </summary>
    public const string PhaseKey = "meitou phase package";

    /// <summary>
    /// The contract is a campaign's phase package (<see cref="Blackboard.PhaseContract"/>).
    /// </summary>
    public bool Phase { get; init; }

    /// <summary>
    /// The PLATOON string of a code mod's packages by slot (<see cref="Blackboard.Replacements"/>; engine key, written only when set, which the
    /// original ignores): <c>slot:package id</c> entries joined by <c>|</c>, the board's order. <see cref="ReplacementKey"/> then names the first,
    /// so the original runs that one alone.
    /// </summary>
    public const string PackagesKey = "meitou packages";

    /// <summary>
    /// A code mod's packages by slot (<see cref="Blackboard.Replacements"/>); null: none.
    /// </summary>
    public IReadOnlyList<SquadPackage>? Packages { get; init; }

    /// <summary>
    /// Whether a contract of package signal <paramref name="signal"/> is loaded as a campaign's phase package even without
    /// <see cref="PhaseKey"/>: the classes only a campaign runs as an override in the base data (the retreats 1 and 25, the war classes 19, 20 and 32,
    /// the defence 31; no dialogue <c>AI contract</c> names one, by record dump), so a save the original wrote while such a retreat outlived its
    /// campaign keeps it until its own test passes, as the original does (every override there ends by its class's test). A contract of one of
    /// these signals that a code mod started is then judged by its class after a load (engine choice).
    /// </summary>
    static bool CampaignOnly(int signal) => signal is 1 or 25 or 19 or 20 or 32 or 31;

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
            MissionTown = contract ? b.ContractMissionTown : CampaignTown.None,
            TargetTown = contract ? b.Destination : CampaignTown.None,
            TargetIsTown = b.Contract?.Signal is AiSystem.DefendBaseSignal or AiSystem.CampaignDefendSignal,
            AnyRange = b.MissionAnyRange,
            Phase = b.PhaseContract,
            Packages = b.Replacements is { } slots ? [.. slots.Select(p => new SquadPackage(p.Slot, p.Package.Id))] : null,
        };
    }

    /// <summary>
    /// Writes the keys over the platoon's (each of them, so a contract that ended since the save was read is cleared). The mission target and
    /// employer are written as the save named them; the capture writes the handle of a <see cref="Target"/> or <see cref="Employer"/> over them
    /// once the character has one. The towns are written with <paramref name="townHand"/> (a town's save handle; null: as the save named them).
    /// </summary>
    public static void Write(FcsRecord platoon, AiSave? ai, Func<CampaignTown, Hand>? townHand = null)
    {
        bool held = platoon.Strings.GetValueOrDefault(ContractKey) is { Length: > 0 };
        WriteMissionTown(platoon, ai, townHand, held);
        platoon.Strings[ContractKey] = ai?.Contract ?? "";
        platoon.Strings[ReplacementKey] = ai?.Replacement ?? "";
        if (ai?.Contract is not null) platoon.Floats[EndKey] = (float)ai.ContractEnd;
        if (ai is { Contract: not null, AnyRange: true }) platoon.Bools[AnyRangeKey] = true;
        else platoon.Bools.Remove(AnyRangeKey);
        if (ai is { Contract: not null, Phase: true }) platoon.Bools[PhaseKey] = true;
        else platoon.Bools.Remove(PhaseKey);
        WritePackages(platoon, ai?.Packages);

        WriteMissionTarget(platoon, ai, townHand, held);
        if (ai?.Contract is null) return;

        // The employer only once known; the target town while the contract runs (the null handle for a bodyguard's).
        if (!ai.EmployerHand.IsNull) ai.EmployerHand.Write(platoon, EmployerKey);
        WriteHand(platoon, TargetTownKey, TownHand(ai.TargetTown, ai.TargetTownHand, townHand));
    }

    /// <summary>
    /// Writes <see cref="PackagesKey"/> while a mod's packages are set, else removes it.
    /// </summary>
    static void WritePackages(FcsRecord platoon, IReadOnlyList<SquadPackage>? packages)
    {
        if (packages is not { Count: > 0 })
        {
            platoon.Strings.Remove(PackagesKey);
            return;
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        platoon.Strings[PackagesKey] = string.Join('|', packages.Select(p => $"{p.Slot.ToString(invariant)}:{p.PackageId}"));
    }

    /// <summary>
    /// The packages <see cref="PackagesKey"/> holds; null without the key or with an entry that does not read (the replacement alone is then
    /// loaded).
    /// </summary>
    static List<SquadPackage>? ReadPackages(FcsRecord platoon)
    {
        if (platoon.Strings.GetValueOrDefault(PackagesKey) is not { Length: > 0 } text) return null;

        var packages = new List<SquadPackage>();
        foreach (var entry in text.Split('|'))
        {
            int colon = entry.IndexOf(':');
            if (colon <= 0 || !int.TryParse(entry.AsSpan(0, colon), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int slot)) return null;
            packages.Add(new SquadPackage(slot, entry[(colon + 1)..]));
        }

        return packages;
    }

    /// <summary>
    /// The contract's mission town handle: written while a contract runs, cleared once one the record held (<paramref name="held"/>) has ended, else
    /// left as the record has it (another package's own, which Meitou does not read).
    /// </summary>
    static void WriteMissionTown(FcsRecord platoon, AiSave? ai, Func<CampaignTown, Hand>? townHand, bool held)
    {
        if (ai?.Contract is null && !held) return;

        var town = ai?.Contract is null ? Hand.Null : TownHand(ai.MissionTown, ai.MissionTownHand, townHand);
        WriteHand(platoon, MissionTownKey, town);
    }

    /// <summary>
    /// The contract's mission target handle (a base defence's mission town): written while a contract runs, cleared once one the record held
    /// (<paramref name="held"/>) has ended, else left as the record has it (another package's own, which Meitou does not read).
    /// </summary>
    static void WriteMissionTarget(FcsRecord platoon, AiSave? ai, Func<CampaignTown, Hand>? townHand, bool held)
    {
        if (ai?.Contract is null && !held) return;

        var target = ai?.Contract is null
            ? Hand.Null
            : ai.TargetIsTown
                ? TownHand(ai.MissionTown, ai.MissionTownHand, townHand)
                : ai.TargetHand;
        WriteHand(platoon, TargetKey, target);
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
        bool phase = contract is not null && platoon.Bools.GetValueOrDefault(PhaseKey);
        return new AiSave(contract, platoon.Floats.GetValueOrDefault(EndKey), replacement)
        {
            TargetHand = HandOf(TargetKey),
            EmployerHand = HandOf(EmployerKey),
            MissionTownHand = HandOf(MissionTownKey),
            TargetTownHand = HandOf(TargetTownKey),
            AnyRange = anyRange,
            Phase = phase,
            Packages = replacement is null ? null : ReadPackages(platoon),
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
        if (PackagesOf(ai) is { } packages) board.ReplaceAll(packages, rules);
        else if (Replacement is not null && ai.Package(Replacement) is { } replacement) board.Replace(replacement, rules);
        if (Contract is null || ai.Package(Contract) is not { } contract) return;

        var target = Target.IsNone ? characterOf?.Invoke(TargetHand) ?? CharacterId.None : Target;
        var employer = Employer.IsNone ? characterOf?.Invoke(EmployerHand) ?? CharacterId.None : Employer;
        var town = MissionTown.IsNone ? townOf?.Invoke(MissionTownHand) ?? CampaignTown.None : MissionTown;
        var targetTown = TargetTown.IsNone ? townOf?.Invoke(TargetTownHand) ?? CampaignTown.None : TargetTown;
        board.ResumeContract(contract, ContractEnd, rules, target, AnyRange, employer, town, targetTown, Phase || CampaignOnly(contract.Signal));
    }

    /// <summary>
    /// The saved mod packages as the data has them; null without any, or when one is unknown now (the replacement alone is then loaded).
    /// </summary>
    List<SlottedPackage>? PackagesOf(AiDatabase ai)
    {
        if (Packages is not { Count: > 0 } saved) return null;

        var packages = new List<SlottedPackage>(saved.Count);
        foreach (var (slot, id) in saved)
        {
            if (ai.Package(id) is not { } package) return null;
            packages.Add(new SlottedPackage(slot, package));
        }

        return packages;
    }
}
