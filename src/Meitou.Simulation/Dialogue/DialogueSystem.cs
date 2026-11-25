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
/// The player's Talk order: <see cref="Talker"/> gets order 12 Talking on <see cref="Target"/> (<see cref="PlayerTalks"/>): it walks up and the
/// conversation opens in reach (event 1). The interface names one talker, the menu's Talk To as the quick right click (<c>FUN_1407fae70</c>);
/// the original's menu gives it to every selected character (<c>FUN_1407f9e20</c>), which Meitou does not by the owner's choice.
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
    IReadOnlyList<string> Choices, IReadOnlyList<string> Log, long PlayerMoney)
{
    /// <summary>
    /// Whether the player started it: opened by a player character's talk order (the menu's Talk, a walk to the NPC ending in it); false for a
    /// conversation an NPC's approach, a bail or any other event opened (docs/modding.md "Dialogue replies").
    /// </summary>
    public bool PlayerStarted { get; init; }
}

/// <summary>
/// What a conversation asks the host to do once it has ended: DA_CHARACTER_EDITOR, the surgeon's editor on <see cref="DialogueRequest.Player"/>,
/// already paid; DA_JOIN_SQUAD_WITH_EDIT, the in-play editor on the recruit (<see cref="DialogueRequest.Player"/>); the animal buying window's
/// confirm, the in-play editor on the bought animals (<see cref="DialogueRequest.Characters"/>, to name them).
/// </summary>
public enum DialogueRequestKind : byte
{
    /// <summary>
    /// DA_CHARACTER_EDITOR: the surgeon's editor on the player character.
    /// </summary>
    CharacterEditor,
    /// <summary>
    /// DA_JOIN_SQUAD_WITH_EDIT: the in-play editor on the recruit.
    /// </summary>
    RecruitEditor,
    /// <summary>
    /// The animal buying confirm: the in-play editor on the bought animals.
    /// </summary>
    AnimalEditor
}
/// <summary>
/// A request to the host from an ended conversation: its number (rising), what to open, the player character and the NPC.
/// </summary>
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
    /// <summary>
    /// The open player conversation as the window shows it; null: none.
    /// </summary>
    ConversationView? PublishConversation();
    /// <summary>
    /// The last request to the host (<see cref="DialogueRequest.Id"/> tells a new one); null: none.
    /// </summary>
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

/// <summary>
/// Settings of the <see cref="DialogueSystem"/>.
/// </summary>
public sealed class DialogueSettings
{
    /// <summary>
    /// Characters react to those they sense with an event line (the sight pass: 3 neutral, 66 enemy, 67 ally, 27 animals and the rest) and fire the
    /// other event lines as speech bubbles (docs/game/dialogue.md "Ambient conversations"): off by default (the pinned scenarios), the game turns it
    /// on.
    /// </summary>
    public bool AmbientEvents { get; init; }
    /// <summary>
    /// Whether the displayed lines go through <see cref="Censorship"/> (the player's setting, read when a line is built).
    /// </summary>
    public Func<bool>? Censored { get; init; }
}

/// <summary>
/// Dialogue (docs/game/dialogue.md; the engine design is its "Meitou design"): the player's conversations with NPCs. A <see cref="TalkOrder"/>'s
/// order 12 walks the talker up and fires event 1
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
public sealed partial class DialogueSystem : ITickSystem, IStateHashed, IDialogueSource, ITaskOwner
{
    /// <summary>
    /// The world's data: dialogue, factions, towns and the game database.
    /// </summary>
    readonly PopulationData _data;
    /// <summary>
    /// The dialogue records (the data's).
    /// </summary>
    readonly DialogueDatabase _dialogue;
    /// <summary>
    /// The game hour each line was last used, by line id (global, as the original's).
    /// </summary>
    readonly SortedDictionary<string, double> _lastUse = new(StringComparer.Ordinal);
    /// <summary>
    /// The permanent chance rolls per character and line: whether it passed.
    /// </summary>
    readonly SortedDictionary<(CharacterId, string), bool> _permanent = new(Comparer<(CharacterId, string)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : string.CompareOrdinal(a.Item2, b.Item2)));
    /// <summary>
    /// Per NPC and event: how often it fired and the game hour of the last time.
    /// </summary>
    readonly SortedDictionary<(CharacterId, int), (int Count, double Last)> _counters = new(Comparer<(CharacterId, int)>.Create((a, b) =>
        a.Item1 != b.Item1 ? CompareIds(a.Item1, b.Item1) : a.Item2.CompareTo(b.Item2)));
    /// <summary>
    /// Per NPC: the status town it stood in when its last ANNOUNCEMENT (event 2) conversation ended, or when the player's talk took over one it was
    /// saying (the Dialogue's per-event map <c>+0xf8</c>, written by <c>FUN_140674830</c> and <c>FUN_140684320</c>, <b>Verified</b>; never cleared,
    /// not saved: <see cref="MessageDelivered"/>).
    /// </summary>
    readonly SortedDictionary<CharacterId, CampaignTown> _delivered = new(Comparer<CharacterId>.Create(CompareIds));
    /// <summary>
    /// Per NPC: the id of its pending <c>interrupt</c> dialogue, opened when the player talks to it.
    /// </summary>
    readonly SortedDictionary<CharacterId, string> _interrupts = new(Comparer<CharacterId>.Create(CompareIds));
    /// <summary>
    /// The dice rolled so far (the dialogue RNG's counter).
    /// </summary>
    ulong _rolls;
    /// <summary>
    /// The last request number given (<see cref="DialogueRequest.Id"/>).
    /// </summary>
    long _requestId;

    /// <summary>
    /// The NPCs on order 102, walking up to a player character to talk (<c>DA_TALK_TO_LEADER</c>).
    /// </summary>
    readonly ApproachQueue _npcApproaches;

    /// <summary>
    /// The characters on order 12, walking up to talk or trade.
    /// </summary>
    readonly PlayerTalks _talks;

    /// <summary>
    /// The owner of the talk orders 12, 102, 164 and 165 and their act-outs.
    /// </summary>
    readonly DialogueOrders _orders;

    /// <summary>
    /// The open player conversation; null: none.
    /// </summary>
    Conversation? _open;
    /// <summary>
    /// The view of <see cref="_open"/> published with the snapshot.
    /// </summary>
    ConversationView? _view;
    /// <summary>
    /// The last request to the host.
    /// </summary>
    DialogueRequest? _request;

    /// <summary>
    /// The population system whose states and town levels the line checks read; null: the data's states and no town levels.
    /// </summary>
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

    /// <summary>
    /// Who started each character's last conversation (Dialogue <c>+0x2a0</c>).
    /// </summary>
    readonly ConversationStarters _starters = new();

    /// <summary>
    /// Sets up the dialogue system on <paramref name="data"/>'s dialogue (default settings when none are given) and its helpers.
    /// </summary>
    public DialogueSystem(PopulationData data, DialogueSettings? settings = null, PopulationSystem? population = null)
    {
        this._data = data;
        this._population = population;
        _dialogue = data.Dialogue;
        Settings = settings ?? new DialogueSettings();
        _talks = new(this);
        _npcApproaches = new(this);
        _illegalGoods = new(data);
        _arrival = new(this, data, _dialogue);
        _characterTrading = new(this, data);
        _followers = new(this);
        _stalkers = new(this, data);
        _orders = new(_talks, _npcApproaches, _followers, _stalkers);
    }

    /// <summary>
    /// The settings.
    /// </summary>
    public DialogueSettings Settings { get; }

    /// <summary>
    /// The owner of the talk orders 12, 102, 164 and 165 (<see cref="World.OrderOwnerOf"/>).
    /// </summary>
    internal IOrderOwner TalkOrders => _orders;

    /// <summary>
    /// Registers the talk act-out (orders 12, 102 and 165).
    /// </summary>
    public void RegisterTasks(TaskHandlers tasks) => tasks.Register(CharacterTask.Talk, _orders);

    /// <summary>
    /// The result of the last arrival try (<see cref="AreaArrivalDialogue.ArrivalResult"/>).
    /// </summary>
    public (int Serial, bool Forget) ArrivalResult => _arrival.ArrivalResult;

    /// <summary>
    /// The squads' memories of characters (docs/game/dialogue.md "Squad memory"): written by dialogue effects and player conversations, read by the
    /// conditions.
    /// </summary>
    public SquadMemory Memory { get; } = new();

    /// <summary>
    /// Orders character ids by slot, then generation (the sorted maps' order).
    /// </summary>
    internal static int CompareIds(CharacterId a, CharacterId b) =>
        a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Generation.CompareTo(b.Generation);

    /// <summary>
    /// One open player conversation.
    /// </summary>
    sealed class Conversation
    {
        /// <summary>
        /// The NPC and the player character talking.
        /// </summary>
        public required CharacterId Npc, Player;
        /// <summary>
        /// The event the conversation was fired by.
        /// </summary>
        public required int Event;
        /// <summary>
        /// The Talk menu's dry run of the pick: conditions post no messages.
        /// </summary>
        public bool DryRun { get; init; }
        /// <summary>
        /// Opened by a player character's talk order (<see cref="ConversationView.PlayerStarted"/>).
        /// </summary>
        public bool PlayerStarted { get; init; }
        /// <summary>
        /// The dialogue root the conversation runs.
        /// </summary>
        public DialogueNode? Root;
        /// <summary>
        /// The NPC-side line on screen and who said it.
        /// </summary>
        public DialogueNode? Line;
        /// <summary>
        /// Who said <see cref="Line"/>.
        /// </summary>
        public CharacterId Speaker;
        /// <summary>
        /// The text of <see cref="Line"/> as shown.
        /// </summary>
        public string Text = "";
        /// <summary>
        /// The three interjectors the lines' speakers 1 to 3 name (none for an empty slot).
        /// </summary>
        public readonly CharacterId[] Interjectors = [CharacterId.None, CharacterId.None, CharacterId.None];
        /// <summary>
        /// The choices: a reply line, or <c>null</c> for <c>[...]</c> (continue <see cref="Line"/>) / <c>[Leave]</c> (when <see cref="Line"/> has no
        /// next).
        /// </summary>
        public readonly List<(DialogueNode? Reply, string Text, string? Shown)> Choices = [];
        /// <summary>
        /// The lines said so far, "name: text", for the window's log.
        /// </summary>
        public readonly List<string> Log = [];
        /// <summary>
        /// The conversation has ended (an effect or the last line); the caller closes it.
        /// </summary>
        public bool Ended;
    }

    /// <summary>
    /// Takes the Talk orders, the choices and ends of the open conversation, the character trade window, arrivals and dialogue events; then says
    /// bubbles a choice started and refreshes the view.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case TalkOrder t:
                    End(world);
                    GiveTalk(world, t);
                    break;
                case DialogueChoice c when _open is not null:
                    Choose(world, _open, c.Index);
                    if (_open.Ended) End(world);
                    break;
                case EndConversation:
                    End(world);
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
                    if (e.Dialogue is { } id)
                    {
                        if (_dialogue.Node(id) is { IsRoot: true } root) StartBubbles(world, e.Npc, e.Target, root, e.Event);
                    }
                    else if (Settings.AmbientEvents) Fire(world, e.Npc, e.Target, e.Event);
                    break;
            }
        }
        // Bubbles a choice started (a crowd trigger) are said at once, also while the conversation window pauses the game.
        if (_bubbles.Count > 0) StepBubbles(world);
        RefreshView(world);
    }

    /// <summary>
    /// The slow-world step: the speech and trade window upkeep, the memory's ageing, the bubbles, the get-up events and the talk orders' act-outs
    /// (the player's talks, walk-ups, followers, stalkers; a talk's conversation opens in talk range). The sight events are
    /// <see cref="Perception.SightSystem"/>'s.
    /// </summary>
    public void SlowWorld(World world)
    {
        ExpireSpeech(world);
        _characterTrading.KeepCharacterTrade(world);
        Memory.Age(world);
        if (_bubbles.Count > 0) StepBubbles(world);
        if (Settings.AmbientEvents) SeeGettingUp(world);
        DropRunOutCooldowns(world);
        _starters.DropGone(world);
        _orders.Step(world);
        RefreshView(world);
    }

    /// <summary>
    /// Gives a Talk order: a player character gets order 12 on an awake NPC through its queue (<see cref="PlayerOrders.Give"/>, the queue cleared
    /// unless <see cref="OrderCommand.Queued"/>); the order check (<c>FUN_1405d1940</c>, before the giver's clear) refuses a dumb talker, and a
    /// hostile NPC without a pending interrupt is attacked instead.
    /// </summary>
    void GiveTalk(World world, TalkOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Talker, out int p) || table.Cold(p) is not { IsPlayer: true } pc
            || !table.TryResolveNext(order.Target, out int n) || table.Cold(n) is not { IsPlayer: false } nc)
        {
            return;
        }
        // The order check (FUN_1405d1940, order 12): a character whose record says dumb refuses to talk to people.
        if (_data.Db.Find(pc.RecordId)?.GetBool("dumb") == true)
        {
            world.Post(Loc.Tr("{1} can't talk to people", world.ShownName(pc)));
            return;
        }
        if (!Talkable(table, n) || SleepsThrough(table, n, DialogueEvents.PlayerTalkToMe)) return;
        // A campaign member with a pending interrupt (its negotiation) talks even when its faction is hostile (docs/game/factions-squads-towns.md
        // "Campaigns", announcement), and so does a caged one (its menu's Talk To, #737).
        if (_data.Relations.IsHostile(nc.Faction, world.Player.Faction) && !_interrupts.ContainsKey(order.Target) && !nc.IsImprisoned)
        {
            world.Defer(new Combat.AttackOrder([order.Talker], order.Target) { Tick = world.Tick + 1, Cause = "talking to a hostile character" });
            return;
        }
        PlayerOrders.Give(world, p, new OrderEntry(Data.Gameplay.Ai.TaskType.PlayerTalkTo, _orders) { Target = order.Target }, order.Queued);
    }

    /// <summary>
    /// The talk distance beyond the target's radius while walking (7 x 3, <c>AI::isWithinChoppingRange</c> for a character, docs/game/dialogue.md
    /// "Player conversation").
    /// </summary>
    public const float TalkReach = 21;
    /// <summary>
    /// The reach that replaces <see cref="TalkReach"/> for a shopkeeper at its counter while <see cref="World.CounterTalk"/> is on (Meitou): the
    /// customer talks from the counter's front instead of walking round behind it (docs/game/dialogue.md "Player conversation", counter reach).
    /// </summary>
    public const float CounterTalkReach = 31;
    /// <summary>
    /// The largest height difference to talk across (the original's on the same floor; engine choice, the sim has no floors here).
    /// </summary>
    public const float TalkHeight = 35;

    /// <summary>
    /// Whether <paramref name="talker"/> is near enough <paramref name="target"/> to talk: X/Z within the target's scaled radius (footprint x body
    /// scale) + <see cref="TalkReach"/> while walking (<see cref="CounterTalkReach"/> for a shopkeeper standing at its counter,
    /// <see cref="CounterReach"/>), or twice <see cref="TalkReach"/> once the talker has stopped, and within <see cref="TalkHeight"/> in height.
    /// </summary>
    internal static bool InTalkRange(World world, int talker, int target)
    {
        var table = world.Characters;
        var (a, b) = (table.Next[talker].Position, table.Next[target].Position);
        bool stopped = (table.Next[talker].Flags & (ushort)MoveFlags.AnyPath) == 0;
        float stride = stopped ? TalkReach * 2
            : CounterReach(world, target) ? CounterTalkReach
            : TalkReach;
        float reach = Combat.CombatSteering.Radius(table.Cold(target)) + stride;
        return Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) < reach && MathF.Abs(a.Y - b.Y) < TalkHeight;
    }

    /// <summary>
    /// Whether the character in <paramref name="target"/> is talked to across a shop counter: <see cref="World.CounterTalk"/> is on, it is not
    /// walking, and its AI goal is standing at a shopkeeper node (row 20, the node behind the counter).
    /// </summary>
    static bool CounterReach(World world, int target) =>
        world.CounterTalk
        && (world.Characters.Next[target].Flags & (ushort)MoveFlags.AnyPath) == 0
        && world.Characters.Cold(target)?.Ai?.Goal?.Task.Type == Data.Gameplay.Ai.TaskType.StandAtShopkeeperNode;

    /// <summary>
    /// Whether the character is alive, not imprisoned and not incapacitated.
    /// </summary>
    internal static bool Awake(CharacterTable table, int slot) =>
        table.Next[slot].Alive && table.Cold(slot) is { IsImprisoned: false } c && c.Medical?.Incapacitated != true;

    /// <summary>
    /// Whether a Talk order's target can be talked to: alive and not incapacitated, a caged one too (the order check <c>FUN_1405d1940</c> and the
    /// conversation start <c>FUN_140684320</c> test no imprisonment; Recruitable Prisoners' cage dialogue, #737).
    /// </summary>
    internal static bool Talkable(CharacterTable table, int slot) =>
        table.Next[slot].Alive && table.Cold(slot) is { } c && c.Medical?.Incapacitated != true;

    /// <summary>
    /// The events an asleep owner still fires, by bit (the mask <c>DAT_141eeaecc</c> of <c>FUN_140684990</c>, which shifts by the event's low five
    /// bits): 11, 23, 24, 25 and those 32 above them.
    /// </summary>
    const uint AsleepEvents = 0x03800800;

    /// <summary>
    /// Whether <paramref name="ev"/> is refused because its owner sleeps (<see cref="Carrying.CarrySystem.Asleep"/>): the original fires nothing
    /// at a sleeper but <see cref="AsleepEvents"/>, so a shopkeeper in bed neither greets nor answers a Talk (docs/game/dialogue.md "Firing an
    /// event").
    /// </summary>
    internal static bool SleepsThrough(CharacterTable table, int slot, int ev) =>
        (AsleepEvents >> (ev & 31) & 1) == 0 && Carrying.CarrySystem.Asleep(table, slot);

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
    /// (docs/game/dialogue.md "Player conversation"); <paramref name="playerStarted"/>: a player character's talk order opens it.
    /// </summary>
    internal void Start(World world, CharacterId npc, CharacterId player, int ev, bool quiet = false, bool playerStarted = false)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || table.Cold(n) is not { } nc) return;
        string pair = $"{EventLog.Who(world, npc)} with {EventLog.Who(world, player)}, {DialogueLog.Event(ev)}";
        if (ev == DialogueEvents.PlayerTalkToMe && TooFarFromCamera(table, player))
        {
            world.DialogueLines.Line(world, "talk", $"{pair}: not opened, the talker is too far from the camera");
            return;
        }
        if (SleepsThrough(table, n, ev))
        {
            world.DialogueLines.Line(world, "talk", $"{pair}: not opened, asleep");
            return;
        }
        if (StarterRefuses(npc, ev))
        {
            world.DialogueLines.Line(world, "talk", $"{pair}: not opened, the starter of its last conversation still talks");
            return;
        }

        var c = new Conversation { Npc = npc, Player = player, Event = ev, PlayerStarted = playerStarted };
        var interrupt = _interrupts.Remove(npc, out var pending) && _dialogue.Node(pending) is { IsRoot: true } root ? root : null;
        IReadOnlyList<DialogueNode> roots = interrupt is not null ? [interrupt] : SetOf(world, n).For(ev);
        string from = pending is null ? $"{roots.Count} root(s) of the event" : $"pending interrupt {pending}";
        var picked = Pick(world, c, roots);
        if (picked is not { } p)
        {
            world.DialogueLines.Line(world, "talk", $"{pair}: nothing to say (from {from})");
            if (!quiet) world.Post($"{world.ShownName(nc)} has nothing to say.");
            return;
        }

        if (Announcing(npc)) Deliver(world, npc);   // the talk cuts an announcement short: it counts as delivered (FUN_140684320)
        DropBubbles(npc);   // the conversation takes over the NPC's dialogue state: a chain it was saying does not go on after (#707)
        _starters.Started(npc, player);
        world.DialogueLines.Line(world, "talk", $"{pair}: conversation opens with {DialogueLog.Node(p.Root ?? p.Line)} (from {from})");
        _open = c;
        FaceOnce(world, n, player);
        c.Root = p.Root;

        // A Talk's opening line is picked before the start, which sets the talker as the target before saying it (FUN_140684990, FUN_140684320,
        // Verified): a squad search during the pick moves nothing. A pending interrupt's line is picked inside the start, after that reset.
        if (interrupt is null) c.Player = player;

        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else Offer(world, c, p.Root ?? p.Line);

        // The start ends by writing the talker back (FUN_140684320, Verified): a search while the line's replies were checked moved the target for
        // those checks only; the window, the replies and Trade stay with the talker. A search in a line reached by a reply keeps its move.
        if (c.Ended) End(world);
        else c.Player = player;
    }

    /// <summary>
    /// A conversation's start turns its owner once to face the other party (<c>FUN_140684320</c> for the player's, <c>FUN_140683f90</c> for a
    /// bubble's: the owner's mover, Dialogue <c>+0x180</c>, <b>Verified</b>), unless the owner is down or carried (<c>FUN_1407d1440</c>) or talks to
    /// itself; nobody is stopped and the other party is not turned.
    /// </summary>
    static void FaceOnce(World world, int owner, CharacterId other)
    {
        var table = world.Characters;
        if (table.Cold(owner) is not { CarriedBy.IsNone: true } oc || oc.Medical is { Incapacitated: true }) return;
        if (!table.TryResolveNext(other, out int o) || o == owner) return;

        DialogueOrders.Face(table, owner, table.Next[o].Position);
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
        return _dialogue.SetFor(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == world.Characters.IdOf(slot),
            cold.IsPlayer, animal, playerFaction);
    }

    /// <summary>
    /// Whether the character's dialogue has a line for one of the sight events (<see cref="DialogueEvents.Sight"/>): the original's Dialogue
    /// <c>+0x28</c> flag, without which its sight pass reacts to nobody it sees (docs/game/dialogue.md "Sight").
    /// </summary>
    public bool HasSightLines(World world, int slot)
    {
        var set = SetOf(world, slot);

        foreach (int ev in DialogueEvents.Sight)
            if (set.For(ev).Count > 0) return true;

        return false;
    }

    /// <summary>
    /// The character offers Trade: its squad has a shop, or has no home building and a template with <c>vendors</c> (a Caravan, stocked at its first
    /// trade), and its record says <c>is trader</c> or its dialogue reaches a Trade line (docs/game/economy.md "Who trades"); the guards and other
    /// members of a trader squad do not, nor does a trader held in a cage (engine choice).
    /// </summary>
    public bool Trades(World world, int slot) =>
        world.Characters.Cold(slot) is { IsPlayer: false, IsImprisoned: false, SquadId: >= 0 } cold && world.Squads.Find(cold.SquadId) is { } squad
        && (squad.Shop is not null || squad.HomeBuilding is null && _data.Db.Find(squad.TemplateId)?.ReferenceCount("vendors") > 0)
        && (_data.Db.Find(cold.RecordId)?.GetBool("is trader") == true || SetOf(world, slot).Trades);

    /// <inheritdoc/>
    public bool CanTalk(World world, int slot, CharacterId? talker = null)
    {
        if (world.Characters.Cold(slot) is not { IsPlayer: false }) return false;
        if (SleepsThrough(world.Characters, slot, DialogueEvents.PlayerTalkToMe)) return false;

        var npc = world.Characters.IdOf(slot);
        if (_interrupts.ContainsKey(npc)) return true;
        var roots = SetOf(world, slot).For(DialogueEvents.PlayerTalkToMe);
        if (roots.Count == 0) return false;
        if (talker is not { } player) return true;
        // A dry run of Start's pick (it rolls dice and memoizes permanent chances: both put back), so the menu never offers a Talk that ends in "has
        // nothing to say".
        var (savedRolls, known) = (_rolls, _permanent.Keys.ToList());
        bool says = Pick(world,
            new Conversation { Npc = npc, Player = player, Event = DialogueEvents.PlayerTalkToMe, DryRun = true }, roots) is not null;
        _rolls = savedRolls;
        foreach (var key in _permanent.Keys.Except(known).ToList()) _permanent.Remove(key);
        return says;
    }

    /// <summary>
    /// Whether <paramref name="t"/> is a speaker on the NPC's side (itself, an interjector or its whole squad).
    /// </summary>
    static bool IsNpcSide(Talker t) => t is Talker.Me or Talker.Interjector1 or Talker.Interjector2 or Talker.Interjector3 or Talker.WholeSquad;

    /// <summary>
    /// The NPC side says <paramref name="line"/> (docs/game/dialogue.md "Saying a line"): a text, marked used, its effects at once, then the choices.
    /// </summary>
    void Say(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        c.Line = line;
        c.Speaker = speaker;
        MarkUsed(world, c, line);
        var (said, shown) = Line(world, c, line, speaker, speaker, c.Player);
        string cost = MoneySuffix(line);
        string text = said + cost;
        c.Text = shown + cost;
        string name = world.ShownName(speaker);
        c.Log.Add($"{name}: {c.Text}");
        world.Said(name, c.Text, false);
        world.DialogueLines.Line(world, "say", $"{EventLog.Who(world, speaker)} to {EventLog.Who(world, c.Player)}: \"{text}\" "
            + $"(line {DialogueLog.Node(line)}{DialogueLog.Effects(line)})");
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
                c.Choices.Add((null, "[...]", null));
                return;
            }
            var (said, shown) = Line(world, c, child, c.Player, c.Player, c.Npc);
            string cost = MoneySuffix(child);
            // a choice with no text and no cost is never offered (FUN_140680130); the button shows the mods' text
            if ((said + cost).Length > 0) c.Choices.Add((child, said + cost, shown + cost));
        }
        if (c.Choices.Count == 0) c.Choices.Add((null, LeaveChoice, null));
    }

    /// <summary>
    /// Answers choice <paramref name="index"/> of <paramref name="c"/>: a bail list's pick, a reply said and its next line, or <c>[...]</c>
    /// and <c>[Leave]</c>.
    /// </summary>
    void Choose(World world, Conversation c, int index)
    {
        if ((uint)index >= (uint)c.Choices.Count) return;
        if (_bailList is not null)
        {
            ChooseBail(world, c, index);
            return;
        }
        var (reply, text, chosen) = c.Choices[index];
        bool leave = reply is null && text == LeaveChoice;
        if (world.DialogueChoiceObservers.Picked(world, c.Player, c.Npc, reply?.Record.StringId ?? "", text, index, leave))
        {
            c.Ended = true;
            return;
        }
        DialogueNode? from = c.Line;
        if (reply is not null)
        {
            string name = world.ShownName(c.Player);
            c.Log.Add($"{name}: {chosen ?? text}");
            world.Said(name.Length > 0 ? name : "Player", chosen ?? text, true);
            world.DialogueLines.Line(world, "reply", $"{EventLog.Who(world, c.Player)} to {EventLog.Who(world, c.Npc)}: \"{text}\" "
                + $"(line {DialogueLog.Node(reply)}{DialogueLog.Effects(reply)})");
            MarkUsed(world, c, reply);
            RunEffects(world, c, reply, c.Npc);
            if (c.Ended) return;
            from = reply;
        }
        else if (text == LeaveChoice || from is null)
        {
            c.Ended = true;
            return;
        }
        var next = Pick(world, c, from.Children);
        if (next is not { } p)
        {
            c.Ended = true;
            return;
        }
        if (IsNpcSide(p.Line.Speaker)) Say(world, c, p.Line, p.Speaker);
        else
        {
            c.Choices.Clear();
            var (said, shown) = Line(world, c, p.Line, c.Player, c.Player, c.Npc);
            string cost = MoneySuffix(p.Line);
            if ((said + cost).Length == 0) c.Ended = true;   // nothing to offer: the conversation ends
            else c.Choices.Add((p.Line, said + cost, shown + cost));
        }
    }

    /// <summary>
    /// Ends the open conversation (<c>FUN_140674830</c>): the event counter and its time, the speaker squad's memory of the partner; nobody's AI is
    /// reset.
    /// </summary>
    void End(World world)
    {
        if (_open is not { } c) return;
        _open = null;
        _starters.Ended(c.Npc);
        world.DialogueLines.Line(world, "talk", $"{EventLog.Who(world, c.Npc)} with {EventLog.Who(world, c.Player)}: conversation ends");
        _view = null;

        // the bail list closing: the conversation was counted already
        if (_bailList is not null)
        {
            _bailList = null;
            return;
        }
        var key = (c.Npc, c.Event);
        var (count, _) = _counters.GetValueOrDefault(key);
        _counters[key] = (count + 1, world.GameHours);
        // A player conversation (event 1) leaves the speaker's squad remembering the partner (FUN_140674830; other events do not).
        if (c.Event == DialogueEvents.PlayerTalkToMe && world.Characters.TryResolveNext(c.Npc, out int n)
            && world.Characters.Cold(n) is { SquadId: >= 0 } nc)
            Memory.Remember(world, nc.SquadId, c.Player, MemoryTag.None);
        OpenBail(world);
        _characterTrading.OpenCharacterTrade(world);
    }

    /// <summary>
    /// Marks <paramref name="line"/> and the conversation's root used at this game hour.
    /// </summary>
    void MarkUsed(World world, Conversation c, DialogueNode line)
    {
        _lastUse[line.StringId] = world.GameHours;
        if (c.Root is { } root) _lastUse[root.StringId] = world.GameHours;
    }

    /// <summary>
    /// The next dialogue roll of <paramref name="who"/>'s (counted by <see cref="_rolls"/>).
    /// </summary>
    ulong NextRoll(World world, CharacterId who) => Rng.Hash(world.Seed, Rng.Key(who), RngPurpose.Dialogue, _rolls++);

    /// <summary>
    /// One of the line's texts: none, its only one, or a roll's pick.
    /// </summary>
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

    /// <summary>
    /// The character's name; empty when the id no longer resolves.
    /// </summary>
    string NameOf(World world, CharacterId id) => world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s) is { } c ? c.Name : "";

    /// <summary>
    /// Rebuilds the published view of the open conversation and the character trade window.
    /// </summary>
    internal void RefreshView(World world)
    {
        _characterTrading.RefreshCharacterTrade(world);
        if (_open is not { } c)
        {
            _view = null;
            return;
        }
        var speaker = world.ShownName(c.Speaker.IsNone ? c.Npc : c.Speaker);
        _view = new ConversationView(c.Npc, c.Player, c.Speaker, speaker, world.ShownName(c.Player), c.Text,
            [.. c.Choices.Select(Shown)], [.. c.Log], world.Player.Money) { PlayerStarted = c.PlayerStarted };
    }

    /// <summary>
    /// The engine's own choice that ends the conversation. The simulation keeps this English marker; only the published view shows it translated.
    /// </summary>
    internal const string LeaveChoice = "[Leave]";

    /// <summary>
    /// A choice as the window shows it: the engine's [Leave] in the player's language; a reply's text is the data's own and stays as it is.
    /// </summary>
    static string Shown((DialogueNode? Reply, string Text, string? Shown) choice) =>
        choice.Shown ?? (choice.Reply is null && choice.Text == LeaveChoice ? Loc.Tr(LeaveChoice) : choice.Text);

    /// <inheritdoc/>
    public ConversationView? PublishConversation() => _view;
    /// <inheritdoc/>
    public DialogueRequest? PublishRequest() => _request;
    /// <inheritdoc/>
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

    /// <summary>
    /// Adds the dialogue state (rolls, line uses, permanent rolls, counters, memories, followers, stalkers, interrupts, bubbles, cooldowns,
    /// approaches, downs, starters, the player's talks and the open conversation) to the world hash; nothing while all are empty.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_lastUse.Count == 0 && _permanent.Count == 0 && _counters.Count == 0 && Memory.IsEmpty && _interrupts.Count == 0 && _bubbles.Count == 0
            && _delivered.Count == 0
            && _eventCooldown.Count == 0 && _npcApproaches.Count == 0 && _down.Count == 0 && _starters.Count == 0
            && _followers.Count == 0 && _stalkers.Count == 0 && _open is null && _talks.Count == 0)
        {
            return;
        }
        hasher.Add(0x44696167);
        hasher.Add(_rolls);
        foreach (var (line, hours) in _lastUse)
        {
            hasher.Add(Rng.StableHash(line));
            hasher.Add(hours);
        }
        foreach (var ((who, line), passed) in _permanent)
        {
            hasher.Add(who);
            hasher.Add(Rng.StableHash(line));
            hasher.Add(passed);
        }
        foreach (var ((who, ev), (count, last)) in _counters)
        {
            hasher.Add(who);
            hasher.Add(ev);
            hasher.Add(count);
            hasher.Add(last);
        }
        Memory.Hash(ref hasher);
        _followers.Hash(ref hasher);
        _stalkers.Hash(ref hasher);
        foreach (var (who, line) in _interrupts)
        {
            hasher.Add(who);
            hasher.Add(Rng.StableHash(line));
        }
        foreach (var (who, town) in _delivered)
        {
            hasher.Add(who);
            hasher.Add(town.Site);
            hasher.Add(town.PlayerTown);
            hasher.Add(town.Nest);
        }
        foreach (var b in _bubbles)
        {
            hasher.Add(b.C.Npc);
            hasher.Add(Rng.StableHash(b.Line.StringId));
            hasher.Add(b.DueTick);
        }
        foreach (var ((npc, ev), until) in _eventCooldown)
        {
            hasher.Add(npc);
            hasher.Add(ev);
            hasher.Add(until);
        }
        _npcApproaches.Hash(ref hasher);
        foreach (var id in _down) hasher.Add(id);
        _starters.Hash(ref hasher);
        _talks.Hash(ref hasher);
        if (_open is { } c)
        {
            hasher.Add(c.Npc);
            hasher.Add(c.Player);
            hasher.Add(c.Line is { } l ? Rng.StableHash(l.StringId) : 0UL);
            hasher.Add(c.Choices.Count);
        }
    }
}