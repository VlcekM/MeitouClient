using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay;

/// <summary>
/// A FACTION_CAMPAIGN <c>key</c>: the editor's <c>WarCampaignEnum</c> (docs/game/factions-squads-towns.md "Campaigns").
/// </summary>
public enum CampaignKey
{
    /// <summary>
    /// ASSAULT_TOWN (159 base records).
    /// </summary>
    AssaultTown = 0,
    /// <summary>
    /// CONQUER_TOWN (no base record).
    /// </summary>
    ConquerTown = 1,
    /// <summary>
    /// DEFEND_TOWN (13 records: ally help).
    /// </summary>
    DefendTown = 2,
    /// <summary>
    /// TRADER_VISIT (7 records: merchant visits).
    /// </summary>
    TraderVisit = 3
}

/// <summary>
/// A FACTION_CAMPAIGN <c>travel speed loaded</c> / <c>unloaded</c>: the editor's <c>MoveSpeed</c>.
/// </summary>
public enum MoveSpeed
{
    /// <summary>
    /// WALK.
    /// </summary>
    Walk = 0,
    /// <summary>
    /// JOG.
    /// </summary>
    Jog = 1,
    /// <summary>
    /// RUN.
    /// </summary>
    Run = 2,
    /// <summary>
    /// GROUPED.
    /// </summary>
    Grouped = 3,
    /// <summary>
    /// NO_SPEED_CHANGE (with JOG the data's usual values).
    /// </summary>
    NoSpeedChange = 4
}

/// <summary>
/// An entry of a weighted campaign list: the record and its three numbers.
/// </summary>
public readonly record struct CampaignLink(string Id, int V0, int V1, int V2);

/// <summary>
/// A FACTION_CAMPAIGN with its <c>inherits from</c> ancestors applied (docs/game/factions-squads-towns.md "Campaigns", the fields the original
/// reads). The appended lists (<see cref="SquadsToUse"/>, <see cref="SpecialLeaders"/>, <see cref="VictoryTriggers"/>, <see cref="LossTriggers"/>)
/// hold every ancestor's entries first, then the record's own; the original applies each ancestor twice (<see cref="From"/>'s <c>ancestorsTwice</c>),
/// which doubles their weight. AI packages, <c>trigger player ally</c> and <c>world state</c> merge (the later record wins), so applying an ancestor
/// again changes nothing there. The rest is the record's own. Ranges are in units (the data's tenths × 10) and <see cref="RepeatLimitHours"/> in game
/// hours (days × 24).
/// </summary>
public sealed record CampaignData
{
    /// <summary>
    /// The field prefixes of the three phases' AI packages (<c>pt1 AI leader</c>, <c>pt2 AI others</c>, ...).
    /// </summary>
    static readonly string[] Phases = ["pt1", "pt2", "retreat"];

    /// <summary>
    /// The FACTION_CAMPAIGN's string id.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The FACTION_CAMPAIGN's name in the active language.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// <c>display name</c>, else the record's name: {1} of the campaign messages.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// <c>territorial triggers</c>: the faction rolls the campaign from its <c>campaigns</c> list.
    /// </summary>
    public bool TerritorialTriggers { get; init; }

    /// <summary>
    /// <c>is hostile</c>: the campaign still targets a town with hostile occupiers.
    /// </summary>
    public bool IsHostile { get; init; }

    /// <summary>
    /// <c>target characters</c>.
    /// </summary>
    public bool TargetCharacters { get; init; }

    /// <summary>
    /// <c>ignores chance mults</c>: the raid frequency setting does not scale the roll.
    /// </summary>
    public bool IgnoresChanceMults { get; init; }

    /// <summary>
    /// <c>ignores nogo zones</c>: a target in the faction's <c>no-go zones</c> is not dropped.
    /// </summary>
    public bool IgnoresNogoZones { get; init; }

    /// <summary>
    /// <c>chance per day</c> (the data's 0.05 to 2).
    /// </summary>
    public float ChancePerDay { get; init; }

    /// <summary>
    /// <c>range near</c> x 10, in units: within it the roll chance is not reduced.
    /// </summary>
    public float RangeNear { get; init; }

    /// <summary>
    /// <c>range far</c> x 10, in units: beyond it the roll chance is 0 (0 or less: no falloff).
    /// </summary>
    public float RangeFar { get; init; }

    /// <summary>
    /// <c>repeat limit</c> x 24: the game hours before the campaign can start again.
    /// </summary>
    public float RepeatLimitHours { get; init; }

    /// <summary>
    /// <c>key</c>: what the campaign does.
    /// </summary>
    public CampaignKey Key { get; init; }

    /// <summary>
    /// <c>num forces</c>: the characters the campaign needs (times <c>raidSizeMult</c>).
    /// </summary>
    public int NumForces { get; init; }

    /// <summary>
    /// <c>target population min</c>: the living player characters a campaign against the player needs.
    /// </summary>
    public int TargetPopulationMin { get; init; }

    /// <summary>
    /// <c>target tech level &gt;=</c>: the town level a town target needs.
    /// </summary>
    public int TargetTechLevel { get; init; }

    /// <summary>
    /// <c>travel speed loaded</c>.
    /// </summary>
    public MoveSpeed TravelSpeedLoaded { get; init; }

    /// <summary>
    /// <c>travel speed unloaded</c>.
    /// </summary>
    public MoveSpeed TravelSpeedUnloaded { get; init; }

    /// <summary>
    /// <c>squads to use</c>: SQUAD_TEMPLATEs, V0 = the draw weight.
    /// </summary>
    public IReadOnlyList<CampaignLink> SquadsToUse { get; init; } = [];

    /// <summary>
    /// <c>special leader</c>: CHARACTER records.
    /// </summary>
    public IReadOnlyList<string> SpecialLeaders { get; init; } = [];

    /// <summary>
    /// <c>world state</c>: WORLD_EVENT_STATE → the value it must have (V0 0 or 1).
    /// </summary>
    public IReadOnlyDictionary<string, bool> WorldStates { get; init; } = new Dictionary<string, bool>();

    /// <summary>
    /// <c>victory trigger</c> / <c>loss trigger</c>: FACTION_CAMPAIGNs, [V0, V1) the delay in hours, V2 the chance in percent.
    /// </summary>
    public IReadOnlyList<CampaignLink> VictoryTriggers { get; init; } = [];

    /// <summary>
    /// <c>loss trigger</c> entries with a chance above 0 (see <see cref="VictoryTriggers"/>).
    /// </summary>
    public IReadOnlyList<CampaignLink> LossTriggers { get; init; } = [];

    /// <summary>
    /// <c>trigger player ally</c>: FACTION_CAMPAIGNs (each with a <c>faction override</c>) asked of the player's allies at the start.
    /// </summary>
    public IReadOnlyList<string> TriggerPlayerAlly { get; init; } = [];

    /// <summary>
    /// <c>faction override</c>: the FACTION a request for this campaign is handed to (null: none).
    /// </summary>
    public string? FactionOverride { get; init; }

    /// <summary>
    /// <c>dialog announcement</c>: a DIALOGUE (null: none).
    /// </summary>
    public string? DialogAnnouncement { get; init; }

    /// <summary>
    /// The AI_PACKAGEs per phase (0 <c>pt1</c>, 1 <c>pt2</c>, 2 <c>retreat</c>) for the leader platoon and the others (null: none; others fall back
    /// to the leader's).
    /// </summary>
    public IReadOnlyList<string?> LeaderPackages { get; init; } = [null, null, null];

    /// <summary>
    /// The other platoons' AI_PACKAGEs per phase (see <see cref="LeaderPackages"/>).
    /// </summary>
    public IReadOnlyList<string?> OtherPackages { get; init; } = [null, null, null];

    /// <summary>
    /// A campaign without <c>pt2 AI leader</c> skips the at-target phase.
    /// </summary>
    public bool HasPt2 => LeaderPackages[1] is not null;

    /// <summary>
    /// The package of a platoon in <paramref name="phase"/> (0, 1, 2): the leader's, or the others' falling back to it.
    /// </summary>
    public string? PackageOf(int phase, bool leader) => leader ? LeaderPackages[phase] : OtherPackages[phase] ?? LeaderPackages[phase];

    /// <summary>
    /// Reads a FACTION_CAMPAIGN. With <paramref name="ancestorsTwice"/> (Faithful) every ancestor's appended entries come twice, as the original's
    /// loader and record reader both apply the parents (<b>Observed</b>); without (Meitou) once.
    /// </summary>
    public static CampaignData From(GameDatabase db, GameRecord r, bool ancestorsTwice)
    {
        var squads = new List<CampaignLink>();
        var leaders = new List<string>();
        var victory = new List<CampaignLink>();
        var loss = new List<CampaignLink>();
        var ally = new List<string>();
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        var leaderPackages = new string?[3];
        var otherPackages = new string?[3];

        static CampaignLink Link(GameReference x) => new(x.TargetStringId, x.Values.Value0, x.Values.Value1, x.Values.Value2);
        void Apply(GameRecord rec)
        {
            squads.AddRange(rec.GetReferences("squads to use").Select(Link));
            leaders.AddRange(rec.GetReferences("special leader").Select(x => x.TargetStringId));
            victory.AddRange(rec.GetReferences("victory trigger").Select(Link));
            loss.AddRange(rec.GetReferences("loss trigger").Select(Link));
            foreach (var x in rec.GetReferences("trigger player ally"))
                if (!ally.Contains(x.TargetStringId)) ally.Add(x.TargetStringId);
            foreach (var x in rec.GetReferences("world state")) states[x.TargetStringId] = x.Values.Value0 != 0;
            for (int p = 0; p < 3; p++)
            {
                if (rec.GetReferences(Phases[p] + " AI leader") is { Count: > 0 } l)
                    leaderPackages[p] = l[0].TargetStringId;
                if (rec.GetReferences(Phases[p] + " AI others") is { Count: > 0 } o)
                    otherPackages[p] = o[0].TargetStringId;
            }
        }
        // Parents depth first, then the record (a cycle in the data is cut).
        void Ancestors(GameRecord rec, HashSet<string> path)
        {
            foreach (var parent in rec.GetReferences("inherits from"))
            {
                if (db.Find(parent.TargetStringId) is not { Type: FcsRecordType.FactionCampaign } p || !path.Add(p.StringId))
                    continue;
                Ancestors(p, path);
                Apply(p);
                path.Remove(p.StringId);
            }
        }
        var visiting = new HashSet<string>(StringComparer.Ordinal) { r.StringId };
        if (ancestorsTwice) Ancestors(r, visiting);
        Ancestors(r, visiting);
        Apply(r);

        string? First(string list) => r.GetReferences(list) is { Count: > 0 } l ? l[0].TargetStringId : null;
        string display = r.GetString("display name");
        return new CampaignData
        {
            Id = r.StringId,
            Name = r.Name,
            DisplayName = display.Length > 0 ? display : r.Name,
            TerritorialTriggers = r.GetBool("territorial triggers"),
            IsHostile = r.GetBool("is hostile"),
            TargetCharacters = r.GetBool("target characters"),
            IgnoresChanceMults = r.GetBool("ignores chance mults"),
            IgnoresNogoZones = r.GetBool("ignores nogo zones"),
            ChancePerDay = RecordReading.Float(r, "chance per day", 0),
            RangeNear = RecordReading.Float(r, "range near", 0) * 10,
            RangeFar = RecordReading.Float(r, "range far", 0) * 10,
            RepeatLimitHours = RecordReading.Float(r, "repeat limit", 0) * 24,
            Key = (CampaignKey)r.GetInt("key"),
            NumForces = RecordReading.Int(r, "num forces", 0),
            TargetPopulationMin = RecordReading.Int(r, "target population min", 0),
            TargetTechLevel = RecordReading.Int(r, "target tech level >=", 0),
            TravelSpeedLoaded = (MoveSpeed)r.GetInt("travel speed loaded"),
            TravelSpeedUnloaded = (MoveSpeed)r.GetInt("travel speed unloaded"),
            SquadsToUse = squads,
            SpecialLeaders = leaders,
            WorldStates = states,
            VictoryTriggers = [.. victory.Where(v => v.V2 > 0)],
            LossTriggers = [.. loss.Where(v => v.V2 > 0)],
            TriggerPlayerAlly = ally,
            FactionOverride = First("faction override"),
            DialogAnnouncement = First("dialog announcement"),
            LeaderPackages = leaderPackages,
            OtherPackages = otherPackages,
        };
    }

    /// <summary>
    /// The roll chance's distance falloff (<c>CampaignData::vfunc_1</c>, <b>Verified</b> by disassembly): 1 when <paramref name="far"/> ≤ 0 or within
    /// <paramref name="near"/>, 0 beyond <paramref name="far"/>, linear between.
    /// </summary>
    public static float Falloff(float distance, float near, float far)
    {
        if (far <= 0 || distance <= near) return 1;
        if (distance > far) return 0;
        return (distance - far) / (near - far);
    }

    /// <summary>
    /// The chance of one roll (four a day) at <paramref name="distance"/> from the faction's nearest town, before the other gates: <c>chance per
    /// day</c> × 0.25 × falloff (× the frequency setting).
    /// </summary>
    public float RollChance(float distance, float raidFrequencyMult)
    {
        float f = Falloff(distance, RangeNear, RangeFar);
        if (!IgnoresChanceMults) f *= raidFrequencyMult;
        return ChancePerDay * 0.25f * f;
    }
}

/// <summary>
/// Every FACTION_CAMPAIGN of a database, read once per inheritance rule (Faithful: ancestors twice; Meitou: once).
/// </summary>
public sealed class CampaignDatabase(GameDatabase db)
{
    /// <summary>
    /// The campaigns read so far by id, one map per rule (index 1: ancestors twice); each map is its own lock.
    /// </summary>
    readonly Dictionary<string, CampaignData?>[] _cache = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];

    /// <summary>
    /// The campaign <paramref name="id"/> (null: not a FACTION_CAMPAIGN).
    /// </summary>
    public CampaignData? Find(string id, bool ancestorsTwice)
    {
        var map = _cache[ancestorsTwice ? 1 : 0];
        lock (map)
        {
            if (map.TryGetValue(id, out var c)) return c;
            c = db.Find(id) is { Type: FcsRecordType.FactionCampaign } r ? CampaignData.From(db, r, ancestorsTwice) : null;
            map[id] = c;
            return c;
        }
    }
}