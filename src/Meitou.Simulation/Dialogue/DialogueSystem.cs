using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The player's Talk order: <see cref="Talker"/> walks up to <see cref="Target"/> and the conversation opens in reach (the original's task 12, then
/// event 1).
/// </summary>
public sealed record TalkOrder(CharacterId Talker, CharacterId Target) : OrderCommand;
/// <summary>
/// Clicks choice <see cref="Index"/> of the open conversation.
/// </summary>
public sealed record DialogueChoice(int Index) : SimCommand;
/// <summary>
/// Leaves the open conversation (its window closed).
/// </summary>
public sealed record EndConversation : SimCommand;
/// <summary>
/// Fires dialogue event <see cref="Event"/> at <see cref="Npc"/> with <see cref="Target"/> as its partner (the original's <c>FUN_140684990</c>, from
/// the systems that witness it): a line of the NPC's list for the event, or of <see cref="Dialogue"/> when given, said as speech bubbles.
/// </summary>
public sealed record DialogueEvent(CharacterId Npc, CharacterId Target, int Event, string? Dialogue = null) : SimCommand;

/// <summary>
/// The open player conversation as the conversation window shows it (published with the snapshot).
/// </summary>
public sealed record ConversationView(CharacterId Npc, CharacterId Player, CharacterId Speaker, string SpeakerName, string PlayerName, string Text,
    IReadOnlyList<string> Choices, IReadOnlyList<string> Log, long PlayerMoney);

/// <summary>
/// What a conversation asks the host to do once it has ended: DA_CHARACTER_EDITOR, the surgeon's editor on <see cref="DialogueRequest.Player"/>,
/// already paid; DA_JOIN_SQUAD_WITH_EDIT, the in-play editor on the recruit (<see cref="DialogueRequest.Player"/>); the animal buying window's
/// confirm, the in-play editor on the bought animals (<see cref="DialogueRequest.Characters"/>, to name them).
/// </summary>
public enum DialogueRequestKind : byte { CharacterEditor, RecruitEditor, AnimalEditor }
public sealed record DialogueRequest(long Id, DialogueRequestKind Kind, CharacterId Player, CharacterId Npc)
{
    /// <summary>
    /// The characters an <see cref="DialogueRequestKind.AnimalEditor"/> request opens the editor on.
    /// </summary>
    public IReadOnlyList<CharacterId> Characters { get; init; } = [];
}

/// <summary>
/// A system with a conversation to publish (the dialogue system).
/// </summary>
public interface IDialogueSource
{
    ConversationView? PublishConversation();
    DialogueRequest? PublishRequest();
    /// <summary>
    /// The open character trading window (DA 41 / 42; null: none).
    /// </summary>
    CharacterTradeView? PublishCharacterTrade() => null;
    /// <summary>
    /// The character answers the player's Talk (it has event-1 dialogue, and with <paramref name="talker"/> given, a line of it passes now).
    /// </summary>
    bool CanTalk(World world, int slot, CharacterId? talker = null);
    /// <summary>
    /// The character holds a line it wants to say to the player (the original's Dialogue <c>playerInterruptionDialog</c>, +0xc8): its quick right
    /// click pointer is <c>s_talk</c>.
    /// </summary>
    bool HasInterrupt(CharacterId npc) => false;
    /// <summary>
    /// The lines on show over characters' heads (speech bubbles).
    /// </summary>
    IReadOnlyList<SpeechBubble> PublishSpeech() => [];
}

public sealed class DialogueSettings
{
    /// <summary>
    /// Game seconds the talker may spend not moving on its walk to the target before the order is dropped (engine choice, <see cref="WalkUp"/>).
    /// </summary>
    public float ApproachSeconds { get; init; } = 60;
    /// <summary>
    /// NPCs react to a player character they see with an ambient line (events 3 neutral, 66 enemy, 67 ally) as a speech bubble. Engine choice for the
    /// Unknown perception rules (docs/game/dialogue.md "Ambient conversations"): off by default (the pinned scenarios), the game turns it on.
    /// </summary>
    public bool AmbientEvents { get; init; }
    /// <summary>
    /// How near a player character NPCs must be for their exchanges with each other to be run (a player character is seen by the sight test instead),
    /// how often (ticks) the NPCs look, how many speak per look and the game hours before a squad reacts to the same character again (engine
    /// choices).
    /// </summary>
    public float AmbientRange { get; init; } = 600;
    public int AmbientEveryTicks { get; init; } = 30;
    public int AmbientPerScan { get; init; } = 2;
    public float AmbientRepeatHours { get; init; } = 0.5f;
    /// <summary>
    /// Whether the displayed lines go through <see cref="Censorship"/> (the player's setting, read when a line is built).
    /// </summary>
    public Func<bool>? Censored { get; init; }
}

/// <summary>
/// Dialogue (docs/game/dialogue.md; the engine design is its "Meitou design"): the player's conversations with NPCs. A <see cref="TalkOrder"/> walks
/// the talker up and fires event 1
/// (PLAYER_TALK_TO_ME) at the NPC: its event-1 dialogue is chosen (score, line checks, a random pick at the top score), the NPC's line is said (its
/// effects run at once) and the
/// passing replies become choices. The window pauses the game, so <see cref="DialogueChoice"/> and <see cref="EndConversation"/> apply between ticks
/// (<see cref="World.ApplyPausedInterfaceCommands"/>);
/// effects that give AI orders start on the next tick.
/// <list type="bullet">
/// <item><b>Engine choices</b>: a childless NPC line is shown with a single <c>[Leave]</c> (the original ends at once and how it shows such a line is
/// <b>Unknown</b>); a reply whose best
/// next line is another reply shows that reply as the only choice; conditions whose inputs Meitou lacks compute "no"
/// (<see cref="Condition"/>).</item>
/// <item><b>State</b> (hashed): last use per line (global, as the original's), permanent chance rolls and event counters per character, the squads'
/// memories (<see cref="Memory"/>), pending
/// interrupts, the open conversation. The characters' dialogue locks are on <see cref="CharacterCold.DialogueLocks"/>.</item>
/// </list>
/// </summary>
public sealed partial class DialogueSystem : ITickSystem, IStateHashed, IDialogueSource
{
    readonly PopulationData _data;
    readonly DialogueDatabase _dialogue;
    readonly SortedDictionary<string, double> _lastUse = new(StringComparer.Ordinal);
    readonly SortedDictionary<(CharacterId, string), bool> _permanent = new(Comparer<(CharacterId, string)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : string.CompareOrdinal(a.Item2, b.Item2)));
    readonly SortedDictionary<(CharacterId, int), (int Count, double Last)> _counters = new(Comparer<(CharacterId, int)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : a.Item2.CompareTo(b.Item2)));
    readonly SortedDictionary<CharacterId, string> _interrupts = new(Comparer<CharacterId>.Create(CompareIds));
    ulong _rolls;
    long _requestId;

    /// <summary>
    /// The NPCs walking up to a player character to talk (<c>DA_TALK_TO_LEADER</c>).
    /// </summary>
    readonly ApproachQueue _npcApproaches;

    // The approach of a Talk order, and the open conversation.
    CharacterId _talker = CharacterId.None, _target = CharacterId.None;
    readonly WalkUp _approach;
    Conversation? _open;
    ConversationView? _view;
    DialogueRequest? _request;

    readonly PopulationSystem? _population;

    /// <summary>
    /// The cultures' illegal goods and the smuggling messages posted (DC_HAS_ILLEGAL_ITEM).
    /// </summary>
    readonly IllegalGoods _illegalGoods;

    /// <summary>
    /// The arrival banter tries (<see cref="AreaArrival"/>) and the last one's result.
    /// </summary>
    readonly AreaArrivalDialogue _arrival;

    /// <summary>
    /// The character trading window (DA 41, 42, 48).
    /// </summary>
    readonly CharacterTrading _characterTrading;

    /// <summary>
    /// The speakers following their targets while they talk (DA_FOLLOW_WHILE_TALKING).
    /// </summary>
    readonly TalkFollowers _followers;

    /// <summary>
    /// The town stalkers (DA_THUG_HUNTER).
    /// </summary>
    readonly TownStalkers _stalkers;

    public DialogueSystem(PopulationData data, DialogueSettings? settings = null, PopulationSystem? population = null)
    {
        this._data = data;
        this._population = population;
        _dialogue = data.Dialogue;
        Settings = settings ?? new DialogueSettings();
        _approach = new(Settings.ApproachSeconds);
        _npcApproaches = new(this);
        _illegalGoods = new(data);
        _arrival = new(this, data, _dialogue);
        _characterTrading = new(this, data);
        _followers = new(this);
        _stalkers = new(this, data);
    }

    public DialogueSettings Settings { get; }

    /// <summary>
    /// The result of the last arrival try (<see cref="AreaArrivalDialogue.ArrivalResult"/>).
    /// </summary>
    public (int Serial, bool Forget) ArrivalResult => _arrival.ArrivalResult;

    /// <summary>
    /// The squads' memories of characters (docs/game/dialogue.md "Squad memory"): written by dialogue effects and player conversations, read by the
    /// conditions.
    /// </summary>
    public SquadMemory Memory { get; } = new();

    internal static int CompareIds(CharacterId a, CharacterId b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation);

    /// <summary>
    /// One open player conversation.
    /// </summary>
    sealed class Conversation
    {
        public required CharacterId Npc, Player;
        public required int Event;
        /// <summary>
        /// The Talk menu's dry run of the pick: conditions post no messages.
        /// </summary>
        public bool DryRun { get; init; }
        public DialogueNode? Root;
        /// <summary>
        /// The NPC-side line on screen and who said it.
        /// </summary>
        public DialogueNode? Line;
        public CharacterId Speaker;
        public string Text = "";
        public readonly CharacterId[] Interjectors = [CharacterId.None, CharacterId.None, CharacterId.None];
        /// <summary>
        /// The choices: a reply line, or <c>null</c> for <c>[...]</c> (continue <see cref="Line"/>) / <c>[Leave]</c> (when <see cref="Line"/> has no
        /// next).
        /// </summary>
        public readonly List<(DialogueNode? Reply, string Text)> Choices = [];
        public readonly List<string> Log = [];
        public bool Ended;
    }

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case TalkOrder t:
                    End(world);
                    StartApproach(world, t);
                    break;
                case DialogueChoice c when _open is not null:
                    Choose(world, _open, c.Index);
                    if (_open.Ended) End(world);
                    break;
                case EndConversation:
                    End(world);
                    CancelApproach(world);
                    break;
                case ConfirmCharacterTrade confirm:
                    _characterTrading.ConfirmTrade(world, confirm.Selected);
                    break;
                case CloseCharacterTrade:
                    _characterTrading.Close();
                    break;
                case AreaArrival arrival:
                    _arrival.Arrive(world, arrival);
                    break;
                case DialogueEvent e:
                    if (e.Event == UnlockMyCageOrShackles && e.Dialogue is null) Freed(world, e.Npc, e.Target);
                    if (e.Dialogue is { } id) { if (_dialogue.Node(id) is { IsRoot: true } root) StartBubbles(world, e.Npc, e.Target, root, e.Event); }
                    else if (Settings.AmbientEvents) Fire(world, e.Npc, e.Target, e.Event);
                    break;
            }
        }
        // Bubbles a choice started (a crowd trigger) are said at once, also while the conversation window pauses the game.
        if (_bubbles.Count > 0) StepBubbles(world);
        RefreshView(world);
    }

    public void SlowWorld(World world)
    {
        ExpireSpeech(world);
        _characterTrading.KeepCharacterTrade(world);
        Memory.Age(world);
        if (_bubbles.Count > 0) StepBubbles(world);
        if (Settings.AmbientEvents && world.Tick % Settings.AmbientEveryTicks == 0) { SeeOthers(world); SeeGettingUp(world); }
        if (_npcApproaches.Count > 0) _npcApproaches.Step(world);
        _followers.StepFollowers(world);
        _stalkers.StepStalkers(world);
        if (!_talker.IsNone && _open is null)
        {
            var table = world.Characters;
            if (!table.TryResolveNext(_talker, out int p) || !table.TryResolveNext(_target, out int n) || !Awake(table, p) || !Awake(table, n)) CancelApproach(world);
            else if (InTalkRange(table, p, n))
            {
                TaskStart.Stand(table, p);
                var (who, whom) = (_talker, _target);
                CancelApproach(world);
                Start(world, whom, who, DialogueEvents.PlayerTalkToMe);
            }
            else if (!_approach.Follow(world, _talker, p, n)) CancelApproach(world);
            else Hold(world, n);
        }
        RefreshView(world);
    }

    void StartApproach(World world, TalkOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Talker, out int p) || table.Cold(p) is not { IsPlayer: true } pc || !table.TryResolveNext(order.Target, out int n) || table.Cold(n) is not { IsPlayer: false } nc) return;
        // The order check (FUN_1405d1940, order 12): a character whose record says dumb refuses to talk to people.
        if (_data.Db.Find(pc.RecordId)?.GetBool("dumb") == true)
        {
            world.Post(Loc.Tr("{1} can't talk to people", pc.Name));
            return;
        }
        if (!Awake(table, n)) return;
        // A campaign member with a pending interrupt (its negotiation) talks even when its faction is hostile (docs/game/factions-squads-towns.md
        // "Campaigns", announcement).
        if (_data.Relations.IsHostile(nc.Faction, world.Player.Faction) && !_interrupts.ContainsKey(order.Target))
        {
            world.Defer(new Combat.AttackOrder([order.Talker], order.Target) { Tick = world.Tick + 1 });
            return;
        }
        _talker = order.Talker;
        _target = order.Target;
        Hold(world, n);
        _approach.Begin(world, _talker, p, n);
    }

    void CancelApproach(World world)
    {
        if (!_target.IsNone && _open?.Npc != _target) Release(world, _target);
        _talker = _target = CharacterId.None;
    }

    /// <summary>
    /// The talk distance beyond the target's radius while walking (7 x 3, <c>AI::isWithinChoppingRange</c> for a character, docs/game/dialogue.md
    /// "Player conversation").
    /// </summary>
    public const float TalkReach = 21;
    /// <summary>
    /// The largest height difference to talk across (the original's on the same floor; engine choice, the sim has no floors here).
    /// </summary>
    public const float TalkHeight = 35;

    /// <summary>
    /// Whether <paramref name="talker"/> is near enough <paramref name="target"/> to talk: X/Z within the target's scaled radius (footprint x body
    /// scale) + <see cref="TalkReach"/>, twice the reach once the talker has stopped, and within <see cref="TalkHeight"/> in height.
    /// </summary>
    internal static bool InTalkRange(CharacterTable table, int talker, int target)
    {
        var (a, b) = (table.Next[talker].Position, table.Next[target].Position);
        float reach = Combat.CombatSteering.Radius(table.Cold(target)) + TalkReach * ((table.Next[talker].Flags & (ushort)MoveFlags.AnyPath) == 0 ? 2 : 1);
        return Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) < reach && MathF.Abs(a.Y - b.Y) < TalkHeight;
    }

    internal static bool Awake(CharacterTable table, int slot) => table.Next[slot].Alive && table.Cold(slot) is { IsImprisoned: false } c && c.Medical?.Incapacitated != true;

    /// <summary>
    /// Keeps the NPC standing while the talker comes and talks (as the trader in a trade); released when it ends.
    /// </summary>
    static void Hold(World world, int slot)
    {
        var table = world.Characters;
        table.Cold(slot)!.Engaged = true;
        if (table.Next[slot].Task is (byte)CharacterTask.Wander or (byte)CharacterTask.Idle) TaskStart.Stand(table, slot);
    }

    internal static void Release(World world, CharacterId id)
    {
        if (world.Characters.TryResolveNext(id, out int slot) && world.Characters.Cold(slot) is { } c) c.Engaged = false;
    }

    /// <summary>
    /// A player conversation is open.
    /// </summary>
    internal bool InConversation => _open is not null;

    /// <summary>
    /// Whether the open player conversation is with <paramref name="npc"/>.
    /// </summary>
    internal bool TalksWith(CharacterId npc) => _open?.Npc == npc;

    /// <summary>
    /// The farthest the talking player character may be from the camera for a player conversation to start (<c>FUN_140684320</c>, constant 9216).
    /// </summary>
    public const float CameraReach = 9216;

    /// <summary>
    /// Whether the talker is more than <see cref="CameraReach"/> from the camera focus (no focus known: never; headless runs and tests have none).
    /// </summary>
    bool TooFarFromCamera(CharacterTable table, CharacterId talker) =>
        _population?.Focus is { } focus && table.TryResolveNext(talker, out int p) && Vector3.Distance(table.Next[p].Position, focus) > CameraReach;

    /// <summary>
    /// Fires <paramref name="ev"/> at <paramref name="npc"/> with <paramref name="player"/> as its target and opens the conversation
    /// (docs/game/dialogue.md "Player conversation").
    /// </summary>
    internal void Start(World world, CharacterId npc, CharacterId player, int ev, bool quiet = false)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || table.Cold(n) is not { } nc) return;
        if (ev == DialogueEvents.PlayerTalkToMe && TooFarFromCamera(table, player)) return;
        var c = new Conversation { Npc = npc, Player = player, Event = ev };
        IReadOnlyList<DialogueNode> roots = _interrupts.Remove(npc, out var pending) && _dialogue.Node(pending) is { IsRoot: true } interrupt ? [interrupt] : SetOf(world, n).For(ev);
        var picked = Pick(world, c, roots);
        if (picked is not { } p)
        {
            if (!quiet) world.Post($"{nc.Name} has nothing to say.");
            return;
        }
        _open = c;
        Hold(world, n);
        if (TaskStart.OnAiWalk(table, n)) TaskStart.Stand(table, n);   // a walking NPC stops to talk (engine choice); the approach chased it
        c.Root = p.Root;
        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else Offer(world, c, p.Root ?? p.Line);
        if (c.Ended) End(world);
    }

    /// <summary>
    /// The character's dialogue (its record, its squad's template and its role, docs/game/dialogue.md "Who has which dialogue").
    /// </summary>
    internal DialogueSet SetOf(World world, int slot)
    {
        var cold = world.Characters.Cold(slot)!;
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        bool animal = cold.Fighter?.IsAnimal ?? cold.Appearance?.Animal is not null;
        string? playerFaction = _data.Relations.Contains(world.Player.Faction) ? _data.Factions[world.Player.Faction].Id : null;
        return _dialogue.SetFor(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == world.Characters.IdOf(slot), cold.IsPlayer, animal, playerFaction);
    }

    /// <summary>
    /// The character offers Trade: its squad has a shop, or has no home building and a template with <c>vendors</c> (a Caravan, stocked at its first
    /// trade), and its record says <c>is trader</c> or its dialogue reaches a Trade line (docs/game/economy.md "Who trades"); the guards and other
    /// members of a trader squad do not.
    /// </summary>
    public bool Trades(World world, int slot) =>
        world.Characters.Cold(slot) is { IsPlayer: false, SquadId: >= 0 } cold && world.Squads.Find(cold.SquadId) is { } squad
        && (squad.Shop is not null || squad.HomeBuilding is null && _data.Db.Find(squad.TemplateId)?.GetReferences("vendors").Count > 0)
        && (_data.Db.Find(cold.RecordId)?.GetBool("is trader") == true || SetOf(world, slot).Trades);

    public bool CanTalk(World world, int slot, CharacterId? talker = null)
    {
        if (world.Characters.Cold(slot) is not { IsPlayer: false }) return false;
        var npc = world.Characters.IdOf(slot);
        if (_interrupts.ContainsKey(npc)) return true;
        var roots = SetOf(world, slot).For(DialogueEvents.PlayerTalkToMe);
        if (roots.Count == 0) return false;
        if (talker is not { } player) return true;
        // A dry run of Start's pick (it rolls dice and memoizes permanent chances: both put back), so the menu never offers a Talk that ends in "has
        // nothing to say".
        var (savedRolls, known) = (_rolls, _permanent.Keys.ToList());
        bool says = Pick(world, new Conversation { Npc = npc, Player = player, Event = DialogueEvents.PlayerTalkToMe, DryRun = true }, roots) is not null;
        _rolls = savedRolls;
        foreach (var key in _permanent.Keys.Except(known).ToList()) _permanent.Remove(key);
        return says;
    }

    static bool IsNpcSide(Talker t) => t is Talker.Me or Talker.Interjector1 or Talker.Interjector2 or Talker.Interjector3 or Talker.WholeSquad;

    /// <summary>
    /// The NPC side says <paramref name="line"/> (docs/game/dialogue.md "Saying a line"): a text, marked used, its effects at once, then the choices.
    /// </summary>
    void Say(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        c.Line = line;
        c.Speaker = speaker;
        MarkUsed(world, c, line);
        string text = Said(Expand(world, c, line, PickText(world, speaker, line), speaker, c.Player));
        text += MoneySuffix(line);
        c.Text = text;
        c.Log.Add($"{NameOf(world, speaker)}: {text}");
        world.Said(NameOf(world, speaker), text, false);
        RunEffects(world, c, line, speaker);
        if (c.Ended) return;
        Offer(world, c, line);
    }

    /// <summary>
    /// The choices after <paramref name="line"/> (docs/game/dialogue.md "Choices"): the passing children in order; an NPC-side one makes a single
    /// <c>[...]</c>.
    /// </summary>
    void Offer(World world, Conversation c, DialogueNode line)
    {
        c.Choices.Clear();
        foreach (var child in line.Children)
        {
            if (!SpeakerOf(world, c, child, out var speaker)) continue;
            var me = IsNpcSide(child.Speaker) ? speaker : c.Npc;
            if (!Check(world, c, child, me, c.Player)) continue;
            if (IsNpcSide(child.Speaker))
            {
                c.Choices.Clear();
                c.Choices.Add((null, "[...]"));
                return;
            }
            string label = Said(Expand(world, c, child, PickText(world, c.Player, child), c.Player, c.Npc)) + MoneySuffix(child);
            if (label.Length > 0) c.Choices.Add((child, label));   // a choice with no text and no cost is never offered (FUN_140680130)
        }
        if (c.Choices.Count == 0) c.Choices.Add((null, LeaveChoice));
    }

    void Choose(World world, Conversation c, int index)
    {
        if ((uint)index >= (uint)c.Choices.Count) return;
        if (_bailList is not null) { ChooseBail(world, c, index); return; }
        var (reply, text) = c.Choices[index];
        DialogueNode? from = c.Line;
        if (reply is not null)
        {
            c.Log.Add($"{NameOf(world, c.Player)}: {text}");
            world.Said(NameOf(world, c.Player) is { Length: > 0 } name ? name : "Player", text, true);
            MarkUsed(world, c, reply);
            RunEffects(world, c, reply, c.Npc);
            if (c.Ended) return;
            from = reply;
        }
        else if (text == LeaveChoice || from is null) { c.Ended = true; return; }
        var next = Pick(world, c, from.Children);
        if (next is not { } p) { c.Ended = true; return; }
        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else
        {
            c.Choices.Clear();
            string label = Said(Expand(world, c, p.Line, PickText(world, c.Player, p.Line), c.Player, c.Npc)) + MoneySuffix(p.Line);
            if (label.Length == 0) c.Ended = true;   // nothing to offer: the conversation ends
            else c.Choices.Add((p.Line, label));
        }
    }

    /// <summary>
    /// Ends the open conversation (<c>FUN_140674830</c>): the event counter and its time, the speaker squad's memory of the partner, the NPC
    /// released.
    /// </summary>
    void End(World world)
    {
        if (_open is not { } c) return;
        _open = null;
        _view = null;
        if (_bailList is not null) { _bailList = null; Release(world, c.Npc); return; }   // the bail list closing: the conversation was counted already
        var key = (c.Npc, c.Event);
        var (count, _) = _counters.GetValueOrDefault(key);
        _counters[key] = (count + 1, world.GameHours);
        // A player conversation (event 1) leaves the speaker's squad remembering the partner (FUN_140674830; other events do not).
        if (c.Event == DialogueEvents.PlayerTalkToMe && world.Characters.TryResolveNext(c.Npc, out int n) && world.Characters.Cold(n) is { SquadId: >= 0 } nc)
            Memory.Remember(world, nc.SquadId, c.Player, MemoryTag.None);
        // An arrest the conversation started (DA 29, 30) keeps the guard held.
        if (Arrests?.Arrests.Any(a => a.Guard == c.Npc) != true) Release(world, c.Npc);
        OpenBail(world);
        _characterTrading.OpenCharacterTrade(world);
    }

    void MarkUsed(World world, Conversation c, DialogueNode line)
    {
        _lastUse[line.StringId] = world.GameHours;
        if (c.Root is { } root) _lastUse[root.StringId] = world.GameHours;
    }

    ulong NextRoll(World world, CharacterId who) => Rng.Hash(world.Seed, Rng.Key(who), RngPurpose.Dialogue, _rolls++);

    string PickText(World world, CharacterId who, DialogueNode line) =>
        line.Texts.Count switch { 0 => "", 1 => line.Texts[0], var n => line.Texts[Rng.Int(NextRoll(world, who), n)] };

    /// <summary>
    /// The cost a line's text shows (<c>FUN_140675320</c>, for a said line and a choice button alike): " [c.N]" once for every DA_TAKE_MONEY effect,
    /// N its own val0 with thousands separators, in effect order. DA_GIVE_MONEY adds nothing.
    /// </summary>
    static string MoneySuffix(DialogueNode line)
    {
        var sb = new StringBuilder();
        foreach (var e in line.Effects)
            if (e.Action == DialogueActions.TakeMoney) sb.Append(" [c.").Append(e.Value.ToString("N0", CultureInfo.InvariantCulture)).Append(']');
        return sb.ToString();
    }

    string NameOf(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s) is { } c ? c.Name : "";

    internal void RefreshView(World world)
    {
        _characterTrading.RefreshCharacterTrade(world);
        if (_open is not { } c) { _view = null; return; }
        _view = new ConversationView(c.Npc, c.Player, c.Speaker, NameOf(world, c.Speaker.IsNone ? c.Npc : c.Speaker), NameOf(world, c.Player), c.Text,
            [.. c.Choices.Select(Shown)], [.. c.Log], world.Player.Money);
    }

    /// <summary>
    /// The engine's own choice that ends the conversation. The simulation keeps this English marker; only the published view shows it translated.
    /// </summary>
    internal const string LeaveChoice = "[Leave]";

    /// <summary>
    /// A choice as the window shows it: the engine's [Leave] in the player's language; a reply's text is the data's own and stays as it is.
    /// </summary>
    static string Shown((DialogueNode? Reply, string Text) choice) => choice.Reply is null && choice.Text == LeaveChoice ? Loc.Tr(LeaveChoice) : choice.Text;

    public ConversationView? PublishConversation() => _view;
    public DialogueRequest? PublishRequest() => _request;
    public CharacterTradeView? PublishCharacterTrade() => _characterTrading.View;

    /// <summary>
    /// EV_BOUGHT_ME_FROM_SLAVERY.
    /// </summary>
    internal const int BoughtMeFromSlavery = 51;

    /// <summary>
    /// The end of the animal buying confirm (<c>FUN_1406aac20</c> -> <c>FUN_1406e32f0</c>, <b>Verified</b>): the character editor opens in mode 1 on
    /// the animals that were bought, after the money is taken, and not at all when none was. The host acts on the request (it names them: the
    /// editor's animal page, <see cref="Editing.CharacterEditing"/>'s name-only edit).
    /// </summary>
    internal void OpenAnimalEditor(CharacterId slaver, List<CharacterId> bought)
    {
        if (bought.Count == 0) return;
        _request = new DialogueRequest(++_requestId, DialogueRequestKind.AnimalEditor, bought[0], slaver) { Characters = bought };
    }

    public void Hash(ref StateHasher hasher)
    {
        if (_lastUse.Count == 0 && _permanent.Count == 0 && _counters.Count == 0 && Memory.IsEmpty && _interrupts.Count == 0 && _bubbles.Count == 0 && _ambientAt.Count == 0 && _npcApproaches.Count == 0 && _down.Count == 0 && _quiet.Count == 0 && _followers.Count == 0 && _stalkers.Count == 0 && _open is null && _talker.IsNone) return;
        hasher.Add(0x44696167);
        hasher.Add(_rolls);
        foreach (var (line, hours) in _lastUse) { hasher.Add(Rng.StableHash(line)); hasher.Add(hours); }
        foreach (var ((who, line), passed) in _permanent) { hasher.Add(who); hasher.Add(Rng.StableHash(line)); hasher.Add(passed); }
        foreach (var ((who, ev), (count, last)) in _counters) { hasher.Add(who); hasher.Add(ev); hasher.Add(count); hasher.Add(last); }
        Memory.Hash(ref hasher);
        _followers.Hash(ref hasher);
        _stalkers.Hash(ref hasher);
        foreach (var (who, line) in _interrupts) { hasher.Add(who); hasher.Add(Rng.StableHash(line)); }
        foreach (var b in _bubbles) { hasher.Add(b.C.Npc); hasher.Add(Rng.StableHash(b.Line.StringId)); hasher.Add(b.DueTick); }
        foreach (var ((squad, whom), hours) in _ambientAt) { hasher.Add(squad); hasher.Add(whom); hasher.Add(hours); }
        _npcApproaches.Hash(ref hasher);
        foreach (var id in _down) hasher.Add(id);
        foreach (var (npc, pc) in _quiet) { hasher.Add(npc); hasher.Add(pc); }
        hasher.Add(_talker);
        hasher.Add(_target);
        if (_open is { } c)
        {
            hasher.Add(c.Npc);
            hasher.Add(c.Player);
            hasher.Add(c.Line is { } l ? Rng.StableHash(l.StringId) : 0UL);
            hasher.Add(c.Choices.Count);
        }
    }
}