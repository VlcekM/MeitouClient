using System.Collections.Concurrent;
using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Dialogue;

/// <summary>
/// <c>TalkerEnum</c> (the FCS editor's, docs/game/dialogue.md "Enums"): who says a node.
/// </summary>
public enum Talker
{
    Me = 0, Target = 1, TargetIfPlayer = 2, Interjector1 = 3, Interjector2 = 4, Interjector3 = 5, WholeSquad = 6, TargetWithRace = 7,
}

/// <summary>
/// The <c>EventTriggerEnum</c> numbers the engine fires (docs/game/dialogue.md "Enums").
/// </summary>
public static class DialogueEvents
{
    public const int PlayerTalkToMe = 1, Announcement = 2;
    /// <summary>
    /// The highest event number (EV_MAX is 75).
    /// </summary>
    public const int Max = 75;
}

/// <summary>
/// A DIALOG_ACTION used as a condition: <c>condition name</c> (<c>DialogConditionEnum</c>), <c>compare by</c> (0 ==, 1 &lt;, 2 &gt;), <c>who</c>
/// (<see cref="Talker"/>) and the reference's val0.
/// </summary>
public readonly record struct DialogueCondition(int Condition, int CompareBy, int Who, int Value);

/// <summary>
/// A DIALOG_ACTION used as an effect: <c>action name</c> (<c>DialogActionEnum</c>) and the reference's val0.
/// </summary>
public readonly record struct DialogueEffect(int Action, int Value);

/// <summary>
/// One DIALOGUE (a root), DIALOGUE_LINE or WORD_SWAPS record as the game reads it (docs/game/dialogue.md "DIALOGUE and DIALOGUE_LINE"): shared by
/// every character, immutable once built. <see cref="Children"/> are sorted by <see cref="Score"/>, highest first; the tree may link back to earlier
/// nodes.
/// </summary>
public sealed class DialogueNode
{
    internal DialogueNode(GameRecord record)
    {
        Record = record;
    }

    public GameRecord Record { get; }
    public string StringId => Record.StringId;
    public bool IsRoot => Record.Type == FcsRecordType.Dialogue;
    public Talker Speaker { get; internal set; }
    public bool Interjection { get; internal set; }
    /// <summary>
    /// The non-empty <c>text0</c>..<c>textN</c>; one is picked at random each time the line is said.
    /// </summary>
    public IReadOnlyList<string> Texts { get; internal set; } = [];
    /// <summary>
    /// <c>repetition limit</c> as cooldown hours (0: none); <see cref="OnceOnly"/> for DR_ONCE_ONLY.
    /// </summary>
    public float CooldownHours { get; internal set; }
    public bool OnceOnly { get; internal set; }
    public float ChancePermanent { get; internal set; } = 100;
    public float ChanceTemporary { get; internal set; } = 100;
    /// <summary>
    /// <c>target is type</c> (<c>CharacterTypeEnum</c>, 0 none).
    /// </summary>
    public int TargetIsType { get; internal set; }
    /// <summary>
    /// The specificity score computed at load (docs/game/dialogue.md "Score"); for a root, its own.
    /// </summary>
    public int Score { get; internal set; }
    /// <summary>
    /// A root's own score plus its best child's: what it sorts by in an event list.
    /// </summary>
    public int ListScore { get; internal set; }
    public bool Locked { get; internal set; }
    public bool OneAtATime { get; internal set; }
    public bool Monologue { get; internal set; }
    public IReadOnlyList<DialogueCondition> Conditions { get; internal set; } = [];
    /// <summary>
    /// DC_PERSONALITY_TAG (26) bits: the speaker's personality must be one of <see cref="PersonalityRequired"/> (when any) and none of
    /// <see cref="PersonalityForbidden"/>.
    /// </summary>
    public uint PersonalityRequired { get; internal set; }
    public uint PersonalityForbidden { get; internal set; }
    /// <summary>
    /// DC_HAS_TAG (23) bits over the speaker squad's memory of the target (<c>CharacterPerceptionTags_LongTerm</c>).
    /// </summary>
    public uint MemoryRequired { get; internal set; }
    public uint MemoryForbidden { get; internal set; }
    public IReadOnlyList<DialogueEffect> Effects { get; internal set; } = [];
    public IReadOnlySet<string> InTownOf { get; internal set; } = Empty;
    public IReadOnlySet<string> MyFaction { get; internal set; } = Empty;
    public IReadOnlySet<string> TargetFaction { get; internal set; } = Empty;
    public IReadOnlySet<string> MyRace { get; internal set; } = Empty;
    public IReadOnlySet<string> MySubrace { get; internal set; } = Empty;
    public IReadOnlySet<string> TargetRace { get; internal set; } = Empty;
    public IReadOnlySet<string> IsCharacter { get; internal set; } = Empty;
    public IReadOnlySet<string> HasPackage { get; internal set; } = Empty;
    public IReadOnlySet<string> TargetCarryingCharacter { get; internal set; } = Empty;
    public IReadOnlySet<string> TargetHasItem { get; internal set; } = Empty;
    /// <summary>
    /// <c>target has item type</c>: the <c>item function</c> of its first reference only (0: no test, also when the reference does not resolve).
    /// </summary>
    public int TargetHasItemFunction { get; internal set; }
    public IReadOnlyList<(string Id, bool Value)> WorldStates { get; internal set; } = [];
    public IReadOnlyList<(string Item, int Count)> GiveItems { get; internal set; } = [];
    public IReadOnlyList<(string Faction, int Amount)> ChangeRelations { get; internal set; } = [];
    public IReadOnlyList<string> Unlocks { get; internal set; } = [];
    public IReadOnlyList<string> UnlockButKeepMe { get; internal set; } = [];
    public IReadOnlyList<string> Locks { get; internal set; } = [];
    public string? Interrupt { get; internal set; }
    /// <summary>
    /// <c>crowd trigger</c>: the DIALOGUE the speaker's squadmates start (event 74) and its val0 (a percent; where the original reads it is
    /// <b>Unknown</b>).
    /// </summary>
    public string? CrowdTrigger { get; internal set; }
    public int CrowdChance { get; internal set; }
    /// <summary>
    /// <c>change AI</c>: the AI_PACKAGE that replaces the speaker squad's packages; <c>AI contract</c>: the AI_PACKAGE run as a contract and its val0
    /// hours.
    /// </summary>
    public string? ChangeAi { get; internal set; }
    public (string Package, int Hours)? AiContract { get; internal set; }
    /// <summary>
    /// <c>trigger campaign</c>: FACTION_CAMPAIGNs, V0..V1 hours of delay and V2 percent (one weighted draw).
    /// </summary>
    public IReadOnlyList<CampaignLink> TriggerCampaigns { get; internal set; } = [];
    public IReadOnlyList<DialogueNode> Children { get; internal set; } = [];

    /// <summary>
    /// The val0 of the line's first DA_TAKE_MONEY or DA_GIVE_MONEY effect, else of its first DC_PLAYERMONEY condition (the <c>/COST/</c> token);
    /// null: none.
    /// </summary>
    public int? Cost => Effects.FirstOrDefault(e => e.Action is DialogueActions.TakeMoney or DialogueActions.GiveMoney) is { Action: > 0 } e ? e.Value
        : Conditions.FirstOrDefault(c => c.Condition == DialogueConditions.PlayerMoney) is { Condition: > 0 } c ? c.Value : null;

    public bool TakesMoney => Effects.Any(e => e.Action == DialogueActions.TakeMoney);

    public override string ToString() => $"{StringId} {Record.Name} ({Record.Type})";

    static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// The <c>DialogActionEnum</c> numbers the engine runs (docs/game/dialogue.md "Effects").
/// </summary>
public static class DialogueActions
{
    public const int None = 0, Trade = 1, TalkToLeader = 2, JoinSquadWithEdit = 3, JoinSquadFast = 18, AffectRelations = 4,
        AffectReputation = 5, AttackChaseForever = 6, TakeMoney = 8, GiveMoney = 9, PayBounty = 10, CharacterEditor = 11,
        BubbleWait = 12, TagEscapedSlave = 25, ArrestTarget = 29, ArrestTargetsCarriedPerson = 30, RemoveSlaveStatus = 53,
        SurrenderNonHumans = 47, GiveTargetMySlaves = 24, EnslaveTargetsCarriedPerson = 40, AssignBounty = 32,
        IncreaseFactionRank = 35, LockThisDialog = 36, AssaultPhase = 37, RetreatPhase = 38, ClearBounty = 49,
        AttackStayNearHome = 55, AttackIfNoCoexist = 57,
        ChooseSlavesSelling = 41, ChooseSlavesBuying = 42, GoHome = 7, DeclareWar = 13, EndWar = 14, FollowWhileTalking = 16,
        ThugHunter = 17, RememberCharacter = 19,
        FlagTempAlly = 20, FlagTempEnemy = 21, MakeTargetRunFaster = 23, SeparateToMyOwnSquad = 28, CrimeAlarm = 33, RunAway = 34,
        SummonMySquad = 52, OpenNearestGate = 54,
        MassiveAlarm = 56, ChooseAnimalsBuying = 48;
}

/// <summary>
/// The <c>DialogConditionEnum</c> numbers with special handling at load (docs/game/dialogue.md "Line check").
/// </summary>
public static class DialogueConditions
{
    public const int PlayerMoney = 2, HasTag = 23, PersonalityTag = 26;
}

/// <summary>
/// A character's dialogue: the roots it answers per event (sorted by <see cref="DialogueNode.ListScore"/>, highest first) and the DIALOGUE_PACKAGEs
/// it has (for <c>has package</c>).
/// </summary>
public sealed class DialogueSet
{
    public static readonly DialogueSet Empty = new([], new HashSet<string>(StringComparer.Ordinal));

    internal DialogueSet(Dictionary<int, IReadOnlyList<DialogueNode>> byEvent, IReadOnlySet<string> packages)
    {
        ByEvent = byEvent;
        Packages = packages;
    }

    public IReadOnlyDictionary<int, IReadOnlyList<DialogueNode>> ByEvent { get; }
    public IReadOnlySet<string> Packages { get; }
    public IReadOnlyList<DialogueNode> For(int ev) => ByEvent.GetValueOrDefault(ev) ?? [];

    int _trades;   // 0 not yet known, 1 no, 2 yes
    /// <summary>
    /// A line under its event-1 dialogue has the Trade effect (conditions not checked): the character trades when talked to, as only the shopkeeper
    /// of most trader squads does.
    /// </summary>
    public bool Trades
    {
        get
        {
            if (_trades == 0)
            {
                var seen = new HashSet<DialogueNode>();
                bool Walk(DialogueNode n) => seen.Add(n) && (n.Effects.Any(e => e.Action == DialogueActions.Trade) || n.Children.Any(Walk));
                _trades = For(DialogueEvents.PlayerTalkToMe).Any(Walk) ? 2 : 1;
            }
            return _trades == 2;
        }
    }
}

/// <summary>
/// The dialogue records of a <see cref="GameDatabase"/> (docs/game/dialogue.md "Data"): one <see cref="DialogueNode"/> per DIALOGUE, DIALOGUE_LINE
/// and WORD_SWAPS record, built on first use; the packages a character gets; the word swaps by token. Thread-safe.
/// </summary>
public sealed class DialogueDatabase(GameDatabase db)
{
    readonly Lock _gate = new();
    readonly Dictionary<string, DialogueNode> _nodes = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<(string Character, string Template, bool Leader, bool Player, bool Animal, string PlayerFaction), DialogueSet>
        _sets = new();
    Dictionary<string, DialogueNode>? _wordSwaps;

    public GameDatabase Db => db;

    Dictionary<string, (string Group, string[] Races)>? _raceGroups;

    /// <summary>
    /// The RACE_GROUP a race belongs to and the group's races (the game's race loader, <c>FUN_140430070</c>): each group's <c>races</c> in data
    /// order, a race listed by several groups keeping the last. Null: the race is in no group.
    /// </summary>
    public (string Group, string[] Races)? RaceGroupOf(string raceId)
    {
        var map = Volatile.Read(ref _raceGroups);
        if (map is null)
        {
            map = new(StringComparer.Ordinal);
            foreach (var g in db.OfType(FcsRecordType.RaceGroup))
            {
                string[] races = [.. g.GetReferences("races").Select(r => r.TargetStringId)];
                foreach (string r in races) map[r] = (g.StringId, races);
            }
            Volatile.Write(ref _raceGroups, map);
        }
        return map.TryGetValue(raceId, out var group) ? group : null;
    }

    /// <summary>
    /// A dialogue race test (<c>FUN_14042ecd0</c>): the race is listed, or a race of its RACE_GROUP is.
    /// </summary>
    public bool RaceMatches(string raceId, IReadOnlySet<string> listed) =>
        listed.Contains(raceId) || (RaceGroupOf(raceId) is { } g && g.Races.Any(listed.Contains));

    /// <summary>
    /// <see cref="RaceMatches(string, IReadOnlySet{string})"/> against a single listed race.
    /// </summary>
    public bool RaceMatches(string raceId, string listed) =>
        raceId == listed || (RaceGroupOf(raceId) is { } g && g.Races.Contains(listed));

    /// <summary>
    /// The node of a dialogue record; null when it is none (or its name marks it <c>Unfinished</c>, which the game skips).
    /// </summary>
    public DialogueNode? Node(string id)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(id, out var known)) return known;
            if (db.Find(id) is not { Type: FcsRecordType.Dialogue or FcsRecordType.DialogueLine or FcsRecordType.WordSwaps } record)
                return null;
            if (record.Type == FcsRecordType.Dialogue && record.Name.Contains("Unfinished", StringComparison.Ordinal))
                return null;
            var built = new List<DialogueNode>();
            var node = Build(record, built);
            foreach (var n in built) Finish(n);
            return node;
        }
    }

    /// <summary>
    /// The WORD_SWAPS record named <paramref name="token"/> (trimmed), lowest string id first; null: none.
    /// </summary>
    public DialogueNode? WordSwap(string token)
    {
        Dictionary<string, DialogueNode>? map;
        lock (_gate) map = _wordSwaps;
        if (map is null)
        {
            map = new(StringComparer.Ordinal);
            foreach (var r in db.OfType(FcsRecordType.WordSwaps).OrderBy(r => r.StringId, StringComparer.Ordinal))
                if (Node(r.StringId) is { } n) map.TryAdd(r.Name.Trim(), n);
            lock (_gate) _wordSwaps = map;
        }
        return map.GetValueOrDefault(token);
    }

    /// <summary>
    /// What a character answers (docs/game/dialogue.md "Who has which dialogue"): its CHARACTER <c>dialogue</c> list at event 1, plus one package: a
    /// player character's <c>dialogue package player</c>, else <paramref name="playerFactionId"/>'s <c>dialog default</c>, else
    /// <c>5369-gamedata.base</c>; an animal its template's <c>dialog animal</c>; an NPC leader its template's <c>dialog leader</c>, else its own
    /// <c>dialogue package</c>, else <c>dialog squad</c>; the others <c>dialog squad</c>, else the character's <c>dialogue package</c>.
    /// </summary>
    public DialogueSet SetFor(string characterId, string? templateId, bool leader, bool player, bool animal, string? playerFactionId = null) =>
        _sets.GetOrAdd((characterId, templateId ?? "", leader, player, animal, playerFactionId ?? ""), Make);

    DialogueSet Make((string Character, string Template, bool Leader, bool Player, bool Animal, string PlayerFaction) key)
    {
        var character = db.Find(key.Character);
        var template = key.Template.Length > 0 ? db.Find(key.Template) : null;
        IEnumerable<GameReference> Refs(GameRecord? r, string list) => r?.GetReferences(list) ?? [];
        List<GameReference> packages;
        if (key.Animal) packages = [.. Refs(template, "dialog animal")];
        else if (key.Player)
        {
            packages = [.. Refs(character, "dialogue package player")];
            if (packages.Count == 0 && db.Find(key.PlayerFaction) is { } faction)
                packages = [.. Refs(faction, "dialog default")];
            if (packages.Count == 0) packages = [new GameReference("5369-gamedata.base", default)];
        }
        else
        {
            packages = key.Leader ? [.. Refs(template, "dialog leader")] : [];
            // A leader's own package comes before the squad's (FUN_14062b210): a shopkeeper leading guards keeps its trade lines.
            if (packages.Count == 0 && key.Leader) packages = [.. Refs(character, "dialogue package")];
            if (packages.Count == 0) packages = [.. Refs(template, "dialog squad")];
            if (packages.Count == 0) packages = [.. Refs(character, "dialogue package")];
        }
        var byEvent = new Dictionary<int, List<DialogueNode>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var packageIds = new HashSet<string>(StringComparer.Ordinal);
        void Add(int ev, string dialogue)
        {
            if (ev <= 0 || ev == 100 || ev > DialogueEvents.Max || Node(dialogue) is not { IsRoot: true } node) return;
            if (!byEvent.TryGetValue(ev, out var list)) byEvent[ev] = list = [];
            if (!list.Contains(node)) list.Add(node);
        }
        void Package(string id)
        {
            if (!seen.Add(id) || db.Find(id) is not { Type: FcsRecordType.DialoguePackage } p
                || p.Name.Contains("Unfinished", StringComparison.Ordinal))
                return;
            packageIds.Add(id);   // an Unfinished package is neither a package of the character nor followed (FUN_14067fab0)
            foreach (var parent in p.GetReferences("inheritsFrom")) Package(parent.TargetStringId);
            foreach (var d in p.GetReferences("dialogs"))
            {
                Add(d.Values.Value0, d.TargetStringId);
                Add(d.Values.Value1, d.TargetStringId);
                Add(d.Values.Value2, d.TargetStringId);
            }
        }
        foreach (var p in packages) Package(p.TargetStringId);
        foreach (var d in Refs(character, "dialogue")) Add(DialogueEvents.PlayerTalkToMe, d.TargetStringId);
        var sorted = byEvent.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DialogueNode>)[.. kv.Value.OrderByDescending(n => n.ListScore)]);
        return new DialogueSet(sorted, packageIds);
    }

    DialogueNode Build(GameRecord record, List<DialogueNode> built)
    {
        var node = new DialogueNode(record);
        _nodes[record.StringId] = node;
        built.Add(node);
        node.Speaker = (Talker)record.GetInt("speaker");
        node.Interjection = record.GetBool("interjection");
        var texts = new List<string>();
        for (int i = 0; record.Strings.TryGetValue("text" + i, out var text); i++)
            if (text.Length > 0) texts.Add(text);
        node.Texts = texts;
        int repetition = record.GetInt("repetition limit");
        node.CooldownHours = repetition switch { 1 => 2, 2 => 6, 3 => 48, 4 => 168, 5 => 5000, 6 => 0.33f, _ => 0 };
        node.OnceOnly = repetition == 5 || record.GetBool("once only");
        node.ChancePermanent = record.GetFloat("chance permanent", 100);
        node.ChanceTemporary = record.GetFloat("chance temporary", 100);
        node.TargetIsType = record.GetInt("target is type");
        node.Locked = record.GetBool("locked");
        node.OneAtATime = record.GetBool("one at a time");
        node.Monologue = record.GetBool("monologue");

        var conditions = new List<DialogueCondition>();
        int tagConditions = 0, requiredTags = 0;
        foreach (var c in record.GetReferences("conditions"))
        {
            if (db.Find(c.TargetStringId) is not { } a) continue;
            int dc = a.GetInt("condition name");
            int tag = a.GetInt("tag");
            if (dc is DialogueConditions.PersonalityTag or DialogueConditions.HasTag)
            {
                tagConditions++;
                if (c.Values.Value0 == 1) requiredTags++;
                if (tag is < 0 or > 31) continue;
                uint bit = 1u << tag;
                bool required = c.Values.Value0 == 1;
                if (dc == DialogueConditions.PersonalityTag)
                {
                    if (required) node.PersonalityRequired |= bit;
                    else node.PersonalityForbidden |= bit;
                }
                else if (required) node.MemoryRequired |= bit;
                else node.MemoryForbidden |= bit;
                continue;
            }
            conditions.Add(new(dc, a.GetInt("compare by"), a.GetInt("who"), c.Values.Value0));
        }
        node.Conditions = conditions;
        node.Effects = [.. record.GetReferences("effects").Select(e => (Effect: e, Record: db.Find(e.TargetStringId)))
            .Where(e => e.Record is not null)
            .Select(e => new DialogueEffect(e.Record!.GetInt("action name"), e.Effect.Values.Value0)).Where(e => e.Action != DialogueActions.None)];

        HashSet<string> Set(string list) => new(record.GetReferences(list).Select(r => r.TargetStringId), StringComparer.Ordinal);
        node.InTownOf = Set("in town of");
        node.MyFaction = Set("my faction");
        node.TargetFaction = Set("target faction");
        node.MyRace = Set("my race");
        node.MySubrace = Set("my subrace");
        node.TargetRace = Set("target race");
        node.IsCharacter = Set("is character");
        node.HasPackage = Set("has package");
        node.TargetCarryingCharacter = Set("target carrying character");
        node.TargetHasItem = Set("target has item");
        // Only the first reference is read (FUN_14067b070); it scores +2 once it resolves, whatever its function.
        var typeRefs = record.GetReferences("target has item type");
        bool itemTyped = typeRefs.Count > 0 && db.Find(typeRefs[0].TargetStringId) is not null;
        if (itemTyped) node.TargetHasItemFunction = db.Find(typeRefs[0].TargetStringId)!.GetInt("item function");
        node.WorldStates = [.. record.GetReferences("world state").Select(r => (r.TargetStringId, r.Values.Value0 != 0))];
        node.GiveItems = [.. record.GetReferences("give item").Select(r => (r.TargetStringId, r.Values.Value0))];
        node.ChangeRelations = [.. record.GetReferences("change relations").Select(r => (r.TargetStringId, r.Values.Value0))];
        node.Unlocks = [.. record.GetReferences("unlocks").Select(r => r.TargetStringId)];
        node.UnlockButKeepMe = [.. record.GetReferences("unlock but keep me").Select(r => r.TargetStringId)];
        node.Locks = [.. record.GetReferences("locks").Select(r => r.TargetStringId)];
        node.Interrupt = record.GetReferences("interrupt").FirstOrDefault().TargetStringId;
        var crowd = record.GetReferences("crowd trigger").FirstOrDefault();
        (node.CrowdTrigger, node.CrowdChance) = (crowd.TargetStringId, crowd.TargetStringId is null ? 0 : crowd.Values.Value0);
        node.ChangeAi = record.GetReferences("change AI").FirstOrDefault().TargetStringId;
        var contract = record.GetReferences("AI contract").FirstOrDefault();
        node.AiContract = contract.TargetStringId is { } package ? (package, contract.Values.Value0) : null;
        node.TriggerCampaigns = [.. record.GetReferences("trigger campaign")
            .Select(r => new CampaignLink(r.TargetStringId, r.Values.Value0, r.Values.Value1, r.Values.Value2))];

        // The specificity score (FUN_14067b070, docs/game/dialogue.md "Score").
        int score = record.GetInt("score bonus");
        if (node.Speaker != Talker.Me) score++;
        if (node.Locked) score += 2;
        foreach (var set in new[] { node.InTownOf, node.MyFaction, node.TargetFaction, node.TargetRace })
            if (set.Count > 0) score += 2;
        if (itemTyped) score += 2;
        score += 2 * node.TargetHasItem.Count + 2 * node.WorldStates.Count + conditions.Count + tagConditions + requiredTags;
        if (node.ChancePermanent < 99) score++;
        if (node.TargetIsType > 0) score++;
        if (node.Interjection) score += 2;
        if (node.Speaker is >= Talker.Interjector1 and <= Talker.WholeSquad) score++;
        node.Score = score;

        var children = new List<DialogueNode>();
        foreach (var l in record.GetReferences("lines"))
        {
            if (db.Find(l.TargetStringId) is not { Type: FcsRecordType.DialogueLine } line) continue;
            var child = _nodes.GetValueOrDefault(line.StringId) ?? Build(line, built);
            if (!children.Contains(child)) children.Add(child);
        }
        node.Children = children;
        return node;
    }

    /// <summary>
    /// Once a tree is read: children sorted by score (stable, so equal scores keep the data's order), and a root's list score.
    /// </summary>
    static void Finish(DialogueNode node)
    {
        if (node.Children.Count > 1) node.Children = [.. node.Children.OrderByDescending(c => c.Score)];
        node.ListScore = node.Score + (node.Children.Count > 0 ? node.Children.Max(c => c.Score) : 0);
    }
}