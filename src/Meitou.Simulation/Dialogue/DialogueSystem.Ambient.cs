using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// A line on show over a character's head (docs/game/dialogue.md "Ambient conversations"): <see cref="Large"/> for the events that use the large
/// font, <see cref="Interrupting"/> for the interrupting set, whose bubbles fade over a longer range and stay on screen.
/// </summary>
public sealed record SpeechBubble(CharacterId Speaker, string Text, bool Large, bool Interrupting = false);

/// <summary>
/// The dialogue system's ambient conversations: event lines said as bubbles over heads (the sight events fired by
/// <see cref="Perception.SightSystem"/>, the crime events cutting in, getting up), their cooldowns, the starter test and crowd triggers.
/// </summary>
public sealed partial class DialogueSystem
{
    /// <summary>
    /// A speech-bubble conversation under way (docs/game/dialogue.md "Ambient conversations"): the line said next, by whom, and when.
    /// <paramref name="Chained"/>: a line after the first, which the original runs on its speaker's own dialogue state (<c>FUN_140684720</c>); the
    /// first runs on the owner's (<c>FUN_140683f90</c>).
    /// </summary>
    sealed record Bubble(Conversation C, DialogueNode Line, long DueTick, CharacterId Speaker, bool Chained = false);

    /// <summary>
    /// The speech-bubble conversations under way.
    /// </summary>
    readonly List<Bubble> _bubbles = [];
    /// <summary>
    /// The lines on show: the text and the tick it goes (display only: follows from the bubbles, so not hashed).
    /// </summary>
    readonly SortedDictionary<CharacterId, (string Text, long Until, bool Large, bool Interrupting, int Event)> _shown =
        new(Comparer<CharacterId>.Create(CompareIds));
    /// <summary>
    /// The speech bubbles <see cref="PublishSpeech"/> last handed out, given again while <see cref="_shown"/> is unchanged; never changed once
    /// published (the host reads them on its own thread).
    /// </summary>
    IReadOnlyList<SpeechBubble> _publishedSpeech = [];
    /// <summary>
    /// Whether <see cref="_shown"/> changed since <see cref="_publishedSpeech"/> was made.
    /// </summary>
    bool _speechChanged;
    /// <summary>
    /// The per-event cooldowns (the original's Dialogue <c>+0xd0</c>): the tick until which a character's event is refused, kept only while it runs.
    /// Hashed.
    /// </summary>
    readonly SortedDictionary<(CharacterId Npc, int Event), long> _eventCooldown =
        new(Comparer<(CharacterId, int)>.Create((a, b) => CompareIds(a.Item1, b.Item1) is var c and not 0 ? c : a.Item2.CompareTo(b.Item2)));

    /// <summary>
    /// The event of a crowd trigger's lines (EV_CROWD_TRIGGERED).
    /// </summary>
    const int CrowdTriggered = 74;
    /// <summary>
    /// EV_UNLOCK_MY_CAGE_OR_SHACKLES: a freed prisoner's thanks, which also makes its squad remember the liberator (<see cref="Freed"/>).
    /// </summary>
    const int UnlockMyCageOrShackles = 13;
    /// <summary>
    /// The event of a contract's end talk (EV_CONTRACT_JOB_ENDED).
    /// </summary>
    public const int ContractJobEnded = 30;
    /// <summary>
    /// DC 16 TARGET_IN_TALKING_RANGE's range (the squared 67600 of <c>DAT_1416fdfec</c>, 260 units).
    /// </summary>
    const float TalkingRange = 260;

    /// <summary>
    /// The events whose bubbles use the large font (<c>FUN_1406713a0</c>, <b>Verified</b>); the others use the medium one.
    /// </summary>
    static bool IsLarge(int ev) => ev is 2 or 11 or 17 or 26 or 31 or 34 or 36 or 40 or 45 or 74;

    /// <summary>
    /// The interrupting set (<c>FUN_1406712f0</c>, <b>Verified</b>): the large-font events and 6, 13, 30, 46, 48, 51, 54.
    /// </summary>
    static bool IsInterrupting(int ev) => IsLarge(ev) || ev is 6 or 13 or 30 or 46 or 48 or 51 or 54;

    /// <summary>
    /// The lines on show, for the renderer's speech bubbles: the last list while they have not changed.
    /// </summary>
    public IReadOnlyList<SpeechBubble> PublishSpeech()
    {
        if (!_speechChanged) return _publishedSpeech;

        _speechChanged = false;
        return _publishedSpeech = _shown.Count == 0
            ? []
            : [.. _shown.Select(s => new SpeechBubble(s.Key, s.Value.Text, s.Value.Large, s.Value.Interrupting))];
    }

    /// <summary>
    /// A campaign's <c>dialog announcement</c> (docs/game/factions-squads-towns.md "Campaigns", announcement): <paramref name="dialogueId"/> fired as
    /// event 2 by <paramref name="npc"/> with no target. Its lines are said one after another as speech bubbles. Their effects run as said (an
    /// <c>interrupt</c> makes the next talk with the speaker that dialogue). An animal never announces (in the original only the leader's AI package
    /// does). Returns whether it started.
    /// </summary>
    public bool Announce(World world, CharacterId npc, string dialogueId) =>
        world.Characters.TryResolveNext(npc, out int slot) && world.Characters.Cold(slot) is { } c && !SquadRegistry.IsAnimal(c)
        && _dialogue.Node(dialogueId) is { IsRoot: true } root
            && StartBubbles(world, npc, CharacterId.None, root, DialogueEvents.Announcement, now: false);

    /// <summary>
    /// <c>isMessageDelivered</c> (state 76, <c>FUN_140792190</c> -> <c>FUN_140673830(dialogue, 2)</c>, <b>Verified</b>): an ANNOUNCEMENT conversation of
    /// <paramref name="npc"/> has ended (<see cref="_delivered"/>) in its current status town (any town while it is in none), and it is saying nothing
    /// now (its conversation ended, no chained speaker: here no line on show, no bubble chain of its own and not in the open conversation). Read by
    /// the AI's parallel think and the war manager; only serial steps write what it reads.
    /// </summary>
    internal bool MessageDelivered(World world, CharacterId npc)
    {
        if (!_delivered.TryGetValue(npc, out var town) || !SaysNothing(npc)) return false;

        var now = world.Characters.TryResolveNext(npc, out int s) ? world.Characters.Cold(s)?.StatusTown ?? CampaignTown.None : CampaignTown.None;
        return now.IsNone || now == town;
    }

    /// <summary>
    /// <c>Dialogue::conversationHasEnded</c> as the announcement reads it: <paramref name="npc"/> has no line on show, no bubble chain of its own or
    /// to speak in, and is not the open conversation's NPC. Any phase.
    /// </summary>
    internal bool SaysNothing(CharacterId npc) => !BubbleOf(npc) && _open?.Npc != npc;

    /// <summary>
    /// Whether <paramref name="npc"/> is saying an ANNOUNCEMENT bubble conversation of its own.
    /// </summary>
    bool Announcing(CharacterId npc)
    {
        foreach (var b in _bubbles)
            if (b.C.Npc == npc && b.C.Event == DialogueEvents.Announcement) return true;

        return false;
    }

    /// <summary>
    /// Records an ANNOUNCEMENT of <paramref name="npc"/> as delivered in its current status town (<see cref="_delivered"/>); records of characters no
    /// longer in the world go (their Dialogue object went with them).
    /// </summary>
    void Deliver(World world, CharacterId npc)
    {
        var table = world.Characters;
        foreach (var gone in _delivered.Keys.Where(k => !table.TryResolveNext(k, out _)).ToArray()) _delivered.Remove(gone);
        if (!table.TryResolveNext(npc, out int s) || table.Cold(s) is not { } c) return;

        _delivered[npc] = c.StatusTown;
    }

    /// <summary>
    /// Starts <paramref name="root"/> as a bubble conversation of <paramref name="npc"/> with <paramref name="whom"/> (<c>FUN_140683f90</c>): the
    /// best of its lines (speakers resolved as in any choice) is said, the chain after it. <paramref name="now"/>: said this tick, else on the next
    /// bubble step. Returns whether a line was found. <paramref name="force"/> (a crowd trigger): the start is not refused for a character busy
    /// talking or caged; what it was saying is dropped without the end step (no counter), as the original overwrites the state (a character in the
    /// open player conversation is still refused: engine choice). <paramref name="checkRoot"/>: the root itself must first pass the line check, its
    /// <c>repetition limit</c> included, as the arrival banter's <c>scoreCustomDialog</c> (<c>FUN_14067e060</c>) runs it on the DIALOGUE.
    /// </summary>
    internal bool StartBubbles(
        World world, CharacterId npc, CharacterId whom, DialogueNode root, int ev, bool now = true, bool force = false, bool checkRoot = false)
    {
        if (!world.Characters.TryResolveNext(npc, out int n)) return false;
        if (force)
        {
            if (!CanSpeak(world.Characters, n) || _open is { } o && (o.Npc == npc || o.Player == npc)) return false;
            DropBubbles(npc);
        }
        else if (!Awake(world.Characters, n) || BubbleOf(npc)) return false;
        var c = new Conversation { Npc = npc, Player = whom, Event = ev, Root = root };
        IReadOnlyList<DialogueNode> from = checkRoot ? [root] : root.Children;
        if (Pick(world, c, from) is not { } first || !force && Busy(first.Speaker, c)) return false;
        _starters.Started(npc, whom);
        FaceOnce(world, n, whom);
        _bubbles.Add(new Bubble(c, first.Line, world.Tick, first.Speaker));
        if (now) StepBubbles(world);
        return true;
    }

    /// <summary>
    /// Drops what <paramref name="npc"/> is saying: its bubble chains, the chains it speaks in and its shown bubble, without the end step (no
    /// counter), as when the original overwrites the character's one dialogue state with a new start.
    /// </summary>
    void DropBubbles(CharacterId npc)
    {
        _bubbles.RemoveAll(b => b.C.Npc == npc || b.Speaker == npc);
        _speechChanged |= _shown.Remove(npc);
    }

    /// <summary>
    /// <paramref name="ev"/> fired at <paramref name="npc"/> with <paramref name="whom"/> as its target outside a player conversation
    /// (<c>FUN_140684990</c>, <b>Verified</b>): refused while the event cools down, by the starter test (<see cref="StarterRefuses"/>), for a
    /// character asleep, out cold or caged, for one talking (any event: the interrupting set does not lift this) and for one in a building whose
    /// interior is not shown (<see cref="UnseenIndoors"/>); else a line is chosen from
    /// the NPC's list for the event and said as a speech bubble, the rest of its chain after it. Returns whether a line was said.
    /// </summary>
    bool Fire(World world, CharacterId npc, CharacterId whom, int ev)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || !table.Next[n].Alive || CoolingDown(world, npc, ev)) return false;
        if (StarterRefuses(npc, ev) || UnseenIndoors(world, n, ev)) return false;
        if (!Awake(table, n) || SleepsThrough(table, n, ev) || BubbleOf(npc)) return false;

        var c = new Conversation { Npc = npc, Player = whom, Event = ev };
        if (Pick(world, c, SetOf(world, n).For(ev)) is not { } picked || picked.Speaker.IsNone || picked.Speaker == whom || Busy(picked.Speaker, c))
            return false;

        c.Root = picked.Root;
        _starters.Started(npc, whom);
        FaceOnce(world, n, whom);
        _bubbles.Add(new Bubble(c, picked.Line, world.Tick, picked.Speaker));
        StepBubbles(world);
        if (ev is 3 or 4 or SeeAnimalSquad or 66 or 67) _eventCooldown[(npc, ev)] = world.Tick + CooldownTicks(world);
        return true;
    }

    /// <summary>
    /// The interior test of <c>FUN_140684990</c> (<b>Verified</b>): a character in a building says no line while the building's interior is not shown
    /// (<c>Building::myInterior</c> <c>+0x1f0</c>, its <c>+0x10</c> set only while shown), but for events 6 (sound the alarm), 11 (screaming torture)
    /// and 39 (slave delivery). Without a view (<see cref="World.ShownInteriors"/> null) it never refuses.
    /// </summary>
    static bool UnseenIndoors(World world, int slot, int ev) =>
        ev is not (6 or Kidnap.CaptiveSystem.ScreamingTorture or Kidnap.CaptiveSystem.SlaveDelivery)
        && world.ShownInteriors is { } shown
        && world.Sight.BuildingOf(slot) is { } building
        && !shown.Contains(building);

    /// <summary>
    /// The starter test of <c>FUN_140684990</c> (<b>Verified</b>), for a speaker that runs no conversation of its own (Dialogue <c>+0x190</c>
    /// empty): it refuses an event outside the interrupting set (event 1 with a pending interrupt passes too) while the character that started the
    /// speaker's last conversation (<c>+0x2a0</c>, <see cref="ConversationStarters"/>) is still talking; passing, the speaker becomes its own starter.
    /// </summary>
    bool StarterRefuses(CharacterId npc, int ev)
    {
        if (LeadsTalk(npc)) return false;

        bool exempt = IsInterrupting(ev) || ev == DialogueEvents.PlayerTalkToMe && _interrupts.ContainsKey(npc);
        return _starters.Refuses(npc, exempt, Free);
    }

    /// <summary>
    /// Whether the character's dialogue state is free (<c>FUN_140671c60</c>): it says no line, has none on show and is not the NPC of the open
    /// player conversation.
    /// </summary>
    internal bool Free(CharacterId who) =>!BubbleOf(who) && _open?.Npc != who;

    /// <summary>
    /// A crime event of the sight pass (<c>FUN_140685730</c> -> <c>FUN_140685660</c>, <b>Verified</b>: events 45, 36, 40, 17 and 9): fired as any
    /// event when the looker is free; else, when the conversation it is in runs another event and it has a line for this one, that conversation
    /// ends (<c>FUN_140674830</c>) and the event fires; the same event again does nothing. Returns whether a line was said.
    /// </summary>
    internal bool CutIn(World world, int slot, int target, int ev) => CutIn(world, slot, world.Characters.IdOf(target), ev);

    /// <summary>
    /// <see cref="CutIn(World, int, int, int)"/> with a partner given by id, none for an event said to nobody (a slave's escape, 55).
    /// </summary>
    internal bool CutIn(World world, int slot, CharacterId whom, int ev)
    {
        var npc = world.Characters.IdOf(slot);
        if (Free(npc)) return Fire(world, npc, whom, ev);
        if (RunningEvent(npc) == ev || SetOf(world, slot).For(ev).Count == 0) return false;

        EndTalkOf(world, npc);
        return Fire(world, npc, whom, ev);
    }

    /// <summary>
    /// The event of the conversation the character is in (Dialogue <c>+0x188</c>): the open player conversation's, a bubble chain's it owns or
    /// speaks in, else the line it still has on show.
    /// </summary>
    int RunningEvent(CharacterId who)
    {
        if (_open is { } o && o.Npc == who) return o.Event;
        foreach (var b in _bubbles)
            if (b.C.Npc == who || b.Speaker == who) return b.C.Event;

        return _shown.TryGetValue(who, out var shown) ? shown.Event : 0;
    }

    /// <summary>
    /// Ends the conversation the character is in (<c>FUN_140674830</c>): the open player conversation when it is its NPC, the bubble chains it owns
    /// or speaks in (each conversation counted once), and its line on show.
    /// </summary>
    void EndTalkOf(World world, CharacterId who)
    {
        if (_open is { } o && o.Npc == who) End(world);
        foreach (var c in _bubbles.Where(b => b.C.Npc == who || b.Speaker == who).Select(b => b.C).Distinct().ToArray())
        {
            EndBubbles(world, c);
            _bubbles.RemoveAll(b => b.C == c);
        }
        _speechChanged |= _shown.Remove(who);
    }

    /// <summary>
    /// The game seconds an event the sight pass fires (3, 4, 27, 66, 67) is refused for after a line of it was said (<c>FUN_140684990</c> sets the
    /// timer to 1.0, <c>FUN_140685090</c> takes the update's time off it, <b>Verified</b>).
    /// </summary>
    const float EventCooldownSeconds = 1;

    /// <summary>
    /// <see cref="EventCooldownSeconds"/> in ticks, at least one.
    /// </summary>
    static long CooldownTicks(World world) => Math.Max(1, (long)MathF.Ceiling(EventCooldownSeconds / world.TickSeconds));

    /// <summary>
    /// Whether the character's <paramref name="ev"/> is still refused by its cooldown (<c>FUN_140684990</c>: the event's timer above 0); a run-out
    /// entry is dropped.
    /// </summary>
    bool CoolingDown(World world, CharacterId npc, int ev)
    {
        if (!_eventCooldown.TryGetValue((npc, ev), out long until)) return false;
        if (world.Tick < until) return true;

        _eventCooldown.Remove((npc, ev));
        return false;
    }

    /// <summary>
    /// The "one at a time" test of a root (<c>FUN_140678f30</c>, <b>Verified</b>): a member of the conversation owner's squad, the owner included,
    /// has a live conversation rooted at <paramref name="node"/>, the open window or a bubble chain of another conversation than
    /// <paramref name="c"/>.
    /// </summary>
    bool RootLive(World world, Conversation c, DialogueNode node) =>
        SquadOf(world, c.Npc).Any(m =>
            _open is { } o && o != c && o.Npc == m && o.Root == node || _bubbles.Any(b => b.C != c && b.C.Npc == m && b.C.Root == node));

    /// <summary>
    /// Whether <paramref name="who"/> is the NPC of a bubble conversation or of the open player conversation (a follower keeps following while it
    /// is).
    /// </summary>
    internal bool LeadsTalk(CharacterId who)
    {
        foreach (var b in _bubbles)
            if (b.C.Npc == who) return true;

        return _open?.Npc == who;
    }

    /// <summary>
    /// Whether <paramref name="who"/> is either side of a bubble conversation or of the open player conversation.
    /// </summary>
    internal bool Talking(CharacterId who)
    {
        foreach (var b in _bubbles)
            if (b.C.Npc == who || b.C.Player == who) return true;

        return _open?.Npc == who || _open?.Player == who;
    }

    /// <summary>
    /// Whether <paramref name="who"/> has a line on show or is the speaker or NPC of a bubble conversation.
    /// </summary>
    bool BubbleOf(CharacterId who) => _shown.ContainsKey(who) || _bubbles.Any(b => b.Speaker == who || b.C.Npc == who);

    /// <summary>
    /// Whether <paramref name="who"/> is saying a line of another conversation, or is in the open player conversation.
    /// </summary>
    bool Busy(CharacterId who, Conversation c) =>
        _open is { } o && (o.Npc == who || o.Player == who) || _bubbles.Any(b => b.C != c && (b.Speaker == who || b.C.Npc == who));

    /// <summary>
    /// Drops the per-event cooldowns that have run out (<see cref="CoolingDown"/> drops one when it is next asked; this keeps the table to the
    /// running ones).
    /// </summary>
    void DropRunOutCooldowns(World world)
    {
        List<(CharacterId, int)>? over = null;
        foreach (var (key, until) in _eventCooldown)
            if (world.Tick >= until) (over ??= []).Add(key);
        if (over is null) return;

        foreach (var key in over) _eventCooldown.Remove(key);
    }

    /// <summary>
    /// The faction owning the training dummy the character is using now (Character <c>+0x158</c>); -1 when it uses none.
    /// </summary>
    internal static int TrainingDummyOwner(World world, int slot) =>
        world.Characters.Cold(slot) is { OperateSite: { } site } && world.Fixtures.Find(site) is { Kind: Locks.FixtureKind.Training } dummy
            ? dummy.Owner : -1;

    /// <summary>
    /// I_SEE_ANIMAL_SQUAD (<c>FUN_140859680</c>: fired at an animal before the friend / other split).
    /// </summary>
    const int SeeAnimalSquad = 27;

    /// <summary>
    /// A sight event (<c>FUN_140685070</c>): fires <paramref name="ev"/> at the character in <paramref name="target"/> when the looker has lines for
    /// it; whether a line was said.
    /// </summary>
    internal bool Say(World world, int slot, int target, int ev) =>
        SetOf(world, slot).For(ev).Count > 0 && Fire(world, world.Characters.IdOf(slot), world.Characters.IdOf(target), ev);

    /// <summary>
    /// The characters that were down at the last look (the get-up events); hashed.
    /// </summary>
    readonly SortedSet<CharacterId> _down = new(Comparer<CharacterId>.Create(CompareIds));

    /// <summary>
    /// EV_GET_UP_PEACE, EV_GET_UP_FIGHT, EV_GET_UP_UNNECCESSARY_FIGHT.
    /// </summary>
    const int GetUpPeace = 23, GetUpFight = 24, GetUpUnnecessaryFight = 25;

    /// <summary>
    /// Getting up (<c>Task_GetUp</c>, <c>FUN_14033ca10</c> / <c>FUN_1408c6780</c>, docs/game/ragdolls.md): the events fire only when the task starts
    /// with posture 0, so a character that was not standing (out cold, staying low, crawling) at the last look and stands now fires, with no target
    /// (a wake that stays low fires nothing until it stands), 23 when it senses no enemy or neutral (<see cref="BodySystem.Senses"/>); else, stood up
    /// on the player's order with no conscious ally sensed, 25; else 24.
    /// </summary>
    void SeeGettingUp(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical: { } medical } c) continue;
            var id = table.IdOf(i);
            if (medical.Dead)
            {
                _down.Remove(id);
                continue;
            }
            // knocked out, staying low or crawling: posture not 0
            if (!medical.Standing)
            {
                _down.Add(id);
                continue;
            }
            if (!_down.Remove(id)) continue;
            bool ordered = c.OrderedUp;
            c.OrderedUp = false;
            int ev;
            if (world.SystemOf<Combat.CombatSystem>() is { } combat)
            {
                var (enemies, neutrals, allies) = BodySystem.Senses(world, combat, i, c);
                ev = enemies + neutrals == 0 ? GetUpPeace : ordered && allies == 0 ? GetUpUnnecessaryFight : GetUpFight;
            }
            else ev = c.InCombat || EnemyNear(world, i, c) ? GetUpFight : GetUpPeace;
            Fire(world, id, CharacterId.None, ev);
        }
        _down.RemoveWhere(id => !table.TryResolveNext(id, out _));
    }

    /// <summary>
    /// Whether a hostile awake character is within the character's outdoor sight range (the get-up events without a combat system).
    /// </summary>
    bool EnemyNear(World world, int slot, CharacterCold c)
    {
        var table = world.Characters;
        if (!_data.Relations.Contains(c.Faction)) return false;
        float r = AggroSystem.RangeOf(c, AggroSystem.OutdoorSight);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (i == slot || !table.Next[i].Alive || table.Cold(i) is not { Medical.Incapacitated: false } o || !_data.Relations.Contains(o.Faction))
                continue;
            if (_data.Relations.IsHostile(c.Faction, o.Faction)
                && Vector3.DistanceSquared(table.Next[i].Position, table.Next[slot].Position) <= r * r)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether talking to <paramref name="npc"/> opens a pending <c>interrupt</c> dialogue (a campaign's negotiation).
    /// </summary>
    public bool HasInterrupt(CharacterId npc) => _interrupts.ContainsKey(npc);

    /// <summary>
    /// The line said after <paramref name="line"/> (<c>FUN_140684720</c>): the best of its children with their speakers resolved (the owner, its
    /// target, an interjector), as in a choice; none when the root is not a monologue (a non-monologue root in bubble mode ends after its first line,
    /// <c>FUN_140685090</c>) or the speaker is busy elsewhere.
    /// </summary>
    Picked? NextBubble(World world, Conversation c, DialogueNode line)
    {
        if (line.Children.Count == 0 || c.Root is { Monologue: false }) return null;
        return Pick(world, c, line.Children) is { } next && !Busy(next.Speaker, c) ? next : null;
    }

    /// <summary>
    /// Says the bubbles that are due (serial, <see cref="SayBubble"/>), then moves each chain on (<see cref="MoveOn"/>).
    /// </summary>
    void StepBubbles(World world)
    {
        for (int k = 0; k < _bubbles.Count; k++)
        {
            var current = _bubbles[k];
            if (world.Tick < current.DueTick) continue;
            if (!world.Characters.TryResolveNext(current.Speaker, out int n) || !CanSpeak(world.Characters, n))
            {
                _bubbles.RemoveAt(k--);
                continue;
            }
            float seconds = SayBubble(world, current, n);

            // A crowd trigger's forced starts drop bubbles from the list (this one too when its owner or speaker joins the crowd): find it again.
            if ((k = _bubbles.FindIndex(b => ReferenceEquals(b, current))) < 0) continue;
            if (MoveOn(world, k, current, seconds)) k--;
        }
    }

    /// <summary>
    /// Says one bubble line as the original's monologue step does (<c>FUN_140683910</c>, <b>Verified</b>): the text and its bubble first
    /// (<c>FUN_14067fd80</c>), then the line's effects (<c>FUN_140680560</c>). Returns the seconds until the next line: 2 + 0.12 × the text's length,
    /// or DA 12's own wait when longer.
    /// </summary>
    float SayBubble(World world, Bubble b, int n)
    {
        var (c, line, _, speaker, chained) = b;
        MarkUsed(world, c, line);
        var (said, shown) = Line(world, c, line, speaker, speaker, speaker == c.Player ? c.Npc : c.Player);
        string cost = MoneySuffix(line);
        string text = said + cost;
        float seconds = 2 + 0.12f * text.Length;   // the game's text: the mods' text never changes how long a bubble stays
        if (text.Length > 0)
        {
            long until = world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds);
            string bubble = shown + cost;
            _shown[speaker] = (bubble, until, IsLarge(c.Event), IsInterrupting(c.Event), c.Event);
            _speechChanged = true;
            world.Said(world.ShownName(speaker), bubble, world.Characters.Cold(n) is { IsPlayer: true });
        }
        LogBubble(world, c, line, speaker, text);

        // locks, unlocks, interrupt and the rest land on the state of whoever says a chained line
        RunEffects(world, c, line, chained ? speaker : c.Npc);
        foreach (var e in line.Effects) if (e.Action == DialogueActions.BubbleWait) seconds = MathF.Max(seconds, e.Value);
        return seconds;
    }

    /// <summary>
    /// Moves the chain of bubble <paramref name="k"/> on to its next line after <paramref name="seconds"/>, or ends it (an effect ended it, or no
    /// next line passes). Returns whether it ended (and left the list).
    /// </summary>
    bool MoveOn(World world, int k, Bubble b, float seconds)
    {
        var (c, line, _, _, _) = b;
        if (!c.Ended && NextBubble(world, c, line) is { } next)
        {
            _bubbles[k] = new Bubble(c, next.Line, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), next.Speaker, Chained: true);
            return false;
        }

        string why = c.Ended ? " (ended by an effect)"
            : line.Children.Count == 0 ? " (last line)"
            : " (no next line passes, or its speaker is busy)";
        world.DialogueLines.Line(world, "bubble", $"{EventLog.Who(world, c.Npc)}: chain of {DialogueLog.Node(c.Root)} ends{why}");
        EndBubbles(world, c);
        _bubbles.RemoveAt(k);
        return true;
    }

    /// <summary>
    /// The dialogue log's line for a bubble line said: speaker, target, text, event, root, line and effects.
    /// </summary>
    static void LogBubble(World world, Conversation c, DialogueNode line, CharacterId speaker, string text)
    {
        var to = speaker == c.Player ? c.Npc : c.Player;
        world.DialogueLines.Line(world, "bubble", $"{EventLog.Who(world, speaker)} to {EventLog.Who(world, to)}: \"{text}\" "
            + $"({DialogueLog.Event(c.Event)}, root {DialogueLog.Node(c.Root)}, line {DialogueLog.Node(line)}{DialogueLog.Effects(line)})");
    }

    /// <summary>
    /// A line's <c>crowd trigger</c> (<c>FUN_140680560</c>, <b>Verified</b>): unless the conversation is itself of event 74 and the line hangs
    /// straight under its root (a line with no grandparent), every member of the squad of <paramref name="owner"/> (the state the effects run on),
    /// that member itself, the conversation's owner and player characters included, that is not down (<see cref="CharacterFacts.Down"/>), not an
    /// animal and not in a bed starts that DIALOGUE as event 74 at the target, forced (<see cref="StartBubbles"/>): a conversation it was in is
    /// overwritten without its end step, so a greeter that sets off its crowd says the crowd's line instead of going on with its own chain. The
    /// trigger's val0 is never read.
    /// </summary>
    void CrowdTrigger(World world, Conversation c, DialogueNode line, CharacterId owner)
    {
        if (line.CrowdTrigger is not { } id || _dialogue.Node(id) is not { IsRoot: true } root) return;
        if (c.Event == CrowdTriggered && c.Root?.Children.Contains(line) == true) return;

        var table = world.Characters;
        foreach (var m in SquadOf(world, owner).ToArray())
        {
            if (!table.TryResolveNext(m, out int s) || !table.Next[s].Alive || table.Cold(s) is not { } mc) continue;
            if (SquadRegistry.IsAnimal(mc) || CharacterFacts.Down(mc) || mc.InSomething == Locks.HeldState.Bed) continue;

            StartBubbles(world, m, c.Player, root, CrowdTriggered, now: false, force: true);
        }
    }

    /// <summary>
    /// Whether the character at <paramref name="slot"/> can say a bubble line: alive and conscious. A caged one can (the original's step stops only
    /// for a ragdolled owner).
    /// </summary>
    static bool CanSpeak(CharacterTable table, int slot) => table.Next[slot].Alive && table.Cold(slot) is { } c && c.Medical?.Incapacitated != true;

    /// <summary>
    /// A bubble conversation's end (<c>FUN_140674830</c>): the event's counter and time, the owner's starter cleared; only an event-1 conversation
    /// leaves the speaker's squad remembering the target.
    /// </summary>
    void EndBubbles(World world, Conversation c)
    {
        _starters.Ended(c.Npc);
        if (c.Event == DialogueEvents.Announcement) Deliver(world, c.Npc);
        var key = (c.Npc, c.Event);
        var (count, _) = _counters.GetValueOrDefault(key);
        _counters[key] = (count + 1, world.GameHours);
        if (c.Event == DialogueEvents.PlayerTalkToMe && !c.Player.IsNone && world.Characters.TryResolveNext(c.Npc, out int n)
            && world.Characters.Cold(n) is { SquadId: >= 0 } nc)
            Memory.Remember(world, nc.SquadId, c.Player, MemoryTag.None);
    }

    /// <summary>
    /// Drops the lines whose time is up or whose speaker is gone (serial, each tick).
    /// </summary>
    void ExpireSpeech(World world)
    {
        if (_shown.Count == 0) return;
        foreach (var who in _shown.Where(s => world.Tick >= s.Value.Until || !world.Characters.TryResolveNext(s.Key, out _)).Select(s => s.Key)
            .ToArray())
        {
            _speechChanged |= _shown.Remove(who);
        }
    }
}