using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// A line on show over a character's head (docs/game/dialogue.md "Ambient conversations"): <see cref="Large"/> for the events that use the large
/// font, <see cref="Interrupting"/> for the interrupting set, whose bubbles fade over a longer range and stay on screen.
/// </summary>
public sealed record SpeechBubble(CharacterId Speaker, string Text, bool Large, bool Interrupting = false);

/// <summary>
/// The dialogue system's ambient conversations: event lines said as bubbles over heads, the sight events (greetings, animals, getting up) and
/// their cooldowns.
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
    readonly SortedDictionary<CharacterId, (string Text, long Until, bool Large, bool Interrupting)> _shown =
        new(Comparer<CharacterId>.Create(CompareIds));
    /// <summary>
    /// The per-event cooldowns (the original's Dialogue <c>+0xd0</c>): the tick until which a character's event is refused, kept only while it runs.
    /// Hashed.
    /// </summary>
    readonly SortedDictionary<(CharacterId Npc, int Event), long> _eventCooldown =
        new(Comparer<(CharacterId, int)>.Create((a, b) => CompareIds(a.Item1, b.Item1) is var c and not 0 ? c : a.Item2.CompareTo(b.Item2)));
    /// <summary>
    /// The NPC / player-character pairs sensed at the world's first look (a new game or a load) and not lost from the NPC's senses since: no sight
    /// line for them (engine choice; the original greets a character arriving, not one already beside it at the start, Observed). Hashed.
    /// </summary>
    readonly SortedSet<(CharacterId Npc, CharacterId Player)> _quiet = new(Comparer<(CharacterId, CharacterId)>.Create(
        (a, b) => CompareIds(a.Item1, b.Item1) is var c and not 0 ? c : CompareIds(a.Item2, b.Item2)));
    /// <summary>
    /// Whether the first look (<see cref="_quiet"/>'s fill) has run.
    /// </summary>
    bool _looked;
    /// <summary>
    /// The tick of the first sight step (-1 before it): the first look waits one aggro look round after it.
    /// </summary>
    long _firstTick = -1;
    /// <summary>
    /// Each character's sight cursor over its sensed list (<c>FUN_14085a5f0</c>'s <c>+0x100</c>), kept only while past the head. Hashed.
    /// </summary>
    readonly SortedDictionary<CharacterId, int> _sightCursor = new(Comparer<CharacterId>.Create(CompareIds));

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
    /// The lines on show, for the renderer's speech bubbles.
    /// </summary>
    public IReadOnlyList<SpeechBubble> PublishSpeech() =>
        [.. _shown.Select(s => new SpeechBubble(s.Key, s.Value.Text, s.Value.Large, s.Value.Interrupting))];

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
    /// Starts <paramref name="root"/> as a bubble conversation of <paramref name="npc"/> with <paramref name="whom"/> (<c>FUN_140683f90</c>): the
    /// best of its lines (speakers resolved as in any choice) is said, the chain after it. <paramref name="now"/>: said this tick, else on the next
    /// bubble step. Returns whether a line was found. <paramref name="force"/> (a crowd trigger): the start is not refused for a character busy
    /// talking or caged; what it was saying is dropped without the end step (no counter), as the original overwrites the state (a character in the
    /// open player conversation is still refused: engine choice).
    /// </summary>
    internal bool StartBubbles(World world, CharacterId npc, CharacterId whom, DialogueNode root, int ev, bool now = true, bool force = false)
    {
        if (!world.Characters.TryResolveNext(npc, out int n)) return false;
        if (force)
        {
            if (!CanSpeak(world.Characters, n) || _open is { } o && (o.Npc == npc || o.Player == npc)) return false;
            DropBubbles(npc);
        }
        else if (!Awake(world.Characters, n) || BubbleOf(npc)) return false;
        var c = new Conversation { Npc = npc, Player = whom, Event = ev, Root = root };
        if (Pick(world, c, root.Children) is not { } first || !force && Busy(first.Speaker, c)) return false;
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
        _shown.Remove(npc);
    }

    /// <summary>
    /// <paramref name="ev"/> fired at <paramref name="npc"/> with <paramref name="whom"/> as its target outside a player conversation
    /// (<c>FUN_140684990</c>): a line is chosen from the NPC's list for the event and said as a speech bubble, the rest of its chain after it.
    /// Returns whether a line was said.
    /// </summary>
    bool Fire(World world, CharacterId npc, CharacterId whom, int ev)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || !Awake(table, n) || SleepsThrough(table, n, ev) || BubbleOf(npc)) return false;
        if (CoolingDown(world, npc, ev)) return false;

        var c = new Conversation { Npc = npc, Player = whom, Event = ev };
        if (Pick(world, c, SetOf(world, n).For(ev)) is not { } picked || picked.Speaker.IsNone || picked.Speaker == whom || Busy(picked.Speaker, c))
            return false;

        c.Root = picked.Root;
        _bubbles.Add(new Bubble(c, picked.Line, world.Tick, picked.Speaker));
        StepBubbles(world);
        if (ev is 3 or 4 or SeeAnimalSquad or 66 or 67) _eventCooldown[(npc, ev)] = world.Tick + CooldownTicks(world);
        return true;
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
    /// The sight pass (<c>FUN_14085a5f0</c>, <b>Verified</b>): on each of a character's update turns (<see cref="BodySystem.UpdateTurn"/>, the
    /// character loop's budgets; <c>FUN_1405c7d40</c> runs it for a living character with a squad) it reacts to the next entry of its sensed list
    /// (<see cref="AggroSystem.SensesOf"/>, <see cref="React"/>); past the list's end the cursor goes back to the head and that turn reacts to nobody.
    /// Nothing before the world's first look (<see cref="FirstLook"/>).
    /// </summary>
    void SeeOthers(World world)
    {
        if (world.SystemOf<BodySystem>() is not { } bodies || world.SystemOf<AggroSystem>() is not { } aggro) return;
        if (!FirstLook(world, aggro)) return;

        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Next[i].Alive && bodies.UpdateTurn(i) && table.Cold(i) is { SquadId: >= 0 } && aggro.SensesOf(i) is { } senses)
                SeeNext(world, i, senses);
        }

        DropGoneCursors(world);
        DropRunOutCooldowns(world);
    }

    /// <summary>
    /// Whether the world's first look has run: one aggro look round (<see cref="AggroSettings.ScanEveryTicks"/>) after the dialogue's first step
    /// (a new game or a load), when every character has sensed once, the pairs it finds fill <see cref="_quiet"/>.
    /// </summary>
    bool FirstLook(World world, AggroSystem aggro)
    {
        if (_looked) return true;
        if (_firstTick < 0) _firstTick = world.Tick;
        if (world.Tick - _firstTick < world.Every(aggro.Settings.ScanEveryTicks)) return false;

        _looked = true;
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { IsPlayer: false } || aggro.SensesOf(i) is not { } senses) continue;
            foreach (var e in senses.Entries)
                if (e.Now && SensedPlayer(world, e) is { } pc) _quiet.Add((table.IdOf(i), pc));
        }
        return true;
    }

    /// <summary>
    /// The player character an entry names, if it names one that still lives.
    /// </summary>
    static CharacterId? SensedPlayer(World world, in SenseEntry e)
    {
        var id = new CharacterId(e.Target, e.Generation);
        return world.Characters.TryResolveNext(id, out int t) && world.Characters.Cold(t) is { IsPlayer: true } ? id : null;
    }

    /// <summary>
    /// One turn of the looker's cursor over its sensed list (<c>FUN_14085a5f0</c>'s <c>+0x100</c>): the entry under it is reacted to and the cursor
    /// moves on; at the end it goes back to 0, and the quiet pairs whose player character the looker no longer senses are dropped.
    /// </summary>
    void SeeNext(World world, int slot, SenseBlock senses)
    {
        var id = world.Characters.IdOf(slot);
        var entries = senses.Entries;
        int cursor = _sightCursor.GetValueOrDefault(id);
        if (cursor >= entries.Count)
        {
            _sightCursor.Remove(id);
            if (_quiet.Count > 0) _quiet.RemoveWhere(q => q.Npc == id && !StillSensed(entries, q.Player));
            return;
        }

        _sightCursor[id] = cursor + 1;
        React(world, slot, entries[cursor]);
    }

    /// <summary>
    /// Whether <paramref name="who"/> is among the entries, seen or heard at the last look.
    /// </summary>
    static bool StillSensed(IReadOnlyList<SenseEntry> entries, CharacterId who)
    {
        foreach (var e in entries)
            if (e.Target == who.Slot && e.Generation == who.Generation && e.Now) return true;

        return false;
    }

    /// <summary>
    /// Drops the cursors of characters that are gone (a cursor is kept only while it is past the head).
    /// </summary>
    void DropGoneCursors(World world)
    {
        if (_sightCursor.Count <= world.Characters.Count) return;

        List<CharacterId>? gone = null;
        foreach (var id in _sightCursor.Keys)
            if (!world.Characters.TryResolveNext(id, out _)) (gone ??= []).Add(id);
        if (gone is null) return;

        foreach (var id in gone) _sightCursor.Remove(id);
    }

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
    /// The looker's reaction to one sensed character (<c>FUN_140859680</c>, <b>Verified</b>), in the original's order: the gates
    /// (<see cref="Gates"/>); a down target (slot 6) gets nothing; an animal event 27 I_SEE_ANIMAL_SQUAD whether seen now or not; the rest only with
    /// a sight line (Dialogue <c>+0x28</c>, <see cref="HasSightLines"/>) and an entry seen or heard at the last look: a target the looker regards as
    /// friendly (slot 126) <see cref="SeeFriend"/>, any other <see cref="SeeOther"/> for a looker free (slave state 0), not an escaped prisoner and
    /// without a pending crime. A pair held quiet since the first look stays quiet while sensed.
    /// </summary>
    void React(World world, int slot, SenseEntry e)
    {
        var table = world.Characters;
        var target = new CharacterId(e.Target, e.Generation);
        if (e.Target == slot || !table.TryResolveNext(target, out int t) || table.Cold(t) is not { } tc) return;
        if (Quiet(table.IdOf(slot), target, e)) return;

        var lc = table.Cold(slot)!;
        if (!Gates(world, slot, lc, t, tc) || CharacterFacts.Down(tc)) return;
        if (SquadRegistry.IsAnimal(tc))
        {
            TrySay(world, slot, t, SeeAnimalSquad);
            return;
        }
        if (!e.Now || !HasSightLines(world, slot)) return;

        if (RegardsFriendly(world, slot, t)) SeeFriend(world, slot, lc, t, tc);
        else if (lc is { SlaveState: 0, Escaped: false, Pending: null }) SeeOther(world, slot, lc, t, tc);
    }

    /// <summary>
    /// Whether the pair was held quiet at the first look (<see cref="_quiet"/>) and the player character is still sensed; one no longer sensed is
    /// let go.
    /// </summary>
    bool Quiet(CharacterId npc, CharacterId target, in SenseEntry e)
    {
        if (_quiet.Count == 0 || !_quiet.Contains((npc, target))) return false;
        if (e.Now) return true;

        _quiet.Remove((npc, target));
        return false;
    }

    /// <summary>
    /// The sight pass's gates (<c>FUN_140859680</c>, <c>FUN_1408529c0</c>, <b>Verified</b>): an animal looker needs a line for event 3; a target in no
    /// squad (no platoon, slot 72) is passed over; a looker in a bed or cage (<c>+0x2f8</c>), down (slot 6) or a slave (state 1) reacts to nobody;
    /// and a looker out of view reacts only to a target in view or of the player's side (<c>+0x1a9</c>, <see cref="MedicalTimeBudget.Flagged"/>;
    /// <c>FUN_140856bc0</c>, <see cref="AggroSystem.PlayerSide"/>).
    /// </summary>
    bool Gates(World world, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (SquadRegistry.IsAnimal(lc) && SetOf(world, slot).For(3).Count == 0) return false;
        if (tc.SquadId < 0) return false;
        if (lc.InSomething != Locks.HeldState.None || CharacterFacts.Down(lc) || lc.SlaveState == 1) return false;

        var table = world.Characters;
        return MedicalTimeBudget.Flagged(world, lc, table.Next[slot].Position) || AggroSystem.PlayerSide(world, tc)
            || MedicalTimeBudget.Flagged(world, tc, table.Next[t].Position);
    }

    /// <summary>
    /// Event 67 I_SEE_ALLY_PLAYER, the friend branch (<b>Verified</b>): only while the looker's platoon is in no campaign (<c>FUN_1409c4540</c>) and
    /// the target is no slave (state 1), of the player's faction and of a faction allied to the looker's (the same counts, <c>FUN_1406b2630</c>).
    /// </summary>
    void SeeFriend(World world, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (InCampaign(world, lc) || tc.SlaveState == 1 || !OfPlayerFaction(world, tc) || !Allied(lc.Faction, tc.Faction)) return;

        TrySay(world, slot, t, 67);
    }

    /// <summary>
    /// The branch for a target not regarded as friendly (<b>Verified</b>; its crime events, 36 / 40 escaping slaves, 17 and 9, and 45 escaped
    /// prisoners, are the arrest and crime systems'): while neither side is in combat mode and the target has no pending crime, and the looker's
    /// squad does not love it (<c>FUN_140599590</c>), <see cref="SeePeaceful"/>; otherwise <see cref="TagBandit"/>; then, also in a fight, event 66
    /// again under its test for a target without a pending crime (the original's tail, which a line just said refuses).
    /// </summary>
    void SeeOther(World world, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        var target = world.Characters.IdOf(t);
        if (!lc.InCombat && !tc.InCombat && tc.Pending is null)
        {
            if (!Loves(world, lc, target)) SeePeaceful(world, slot, lc, t, tc);
        }
        else TagBandit(world, slot, lc, t, tc);

        if (tc.Pending is null && HostilePlayer(world, lc, tc) && !InCampaign(world, lc)) TrySay(world, slot, t, 66);
    }

    /// <summary>
    /// The fight branch's tag (<c>FUN_1406744a0(looker, target, 4)</c>, <b>Verified</b>): with either side in combat mode or a target with a pending
    /// crime, the looker's squad tags a target in no status town (status <c>+0x88</c>, <see cref="CharacterCold.StatusTown"/>) TEMP_ENEMY when
    /// either side's NPC class (status <c>+0xdc</c>) is Bandit (8); the wrapper's own skips apply (<see cref="TagEnemy"/>). Engine guard: also only
    /// outside every town's radius, where the status town is always set (the radius covers one not yet written by the arrest system's pass after a
    /// load or spawn), and never in a world without town data.
    /// </summary>
    void TagBandit(World world, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (tc.StatusTown != Population.CampaignTown.None) return;
        if (tc.NpcClass != Data.Gameplay.FundamentalType.Bandit && lc.NpcClass != Data.Gameplay.FundamentalType.Bandit) return;
        if (world.SystemOf<AggroSystem>()?.Towns is not { } towns) return;
        if (!Ai.AiSystem.TownAt(towns, world, world.Characters.Next[t].Position).IsNone) return;

        TagEnemy(world, slot, t);
    }

    /// <summary>
    /// EV_I_SEE_UNIFORM_IMPOSTER.
    /// </summary>
    const int UniformImposter = 34;

    /// <summary>
    /// The peaceful branch (<b>Verified</b>): event 34 I_SEE_UNIFORM_IMPOSTER at a disguise the looker sees through (<see cref="Imposter"/>) when
    /// it has a line for it and senses no enemy; else 59 USING_MY_TRAINING_EQUIPMENT at a target training on a dummy of the looker's faction while
    /// it senses no enemy; else, with the looker's platoon in no campaign and a target that is no slave (state 1), 66 I_SEE_ENEMY_PLAYER at one of
    /// the player's faction hostile to the looker's (<c>FUN_1406b26d0</c>), 3 I_SEE_NEUTRAL_SQUAD at any other while the looker senses no enemy.
    /// </summary>
    void SeePeaceful(World world, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        int enemies = world.SystemOf<AggroSystem>()?.SensesOf(slot)?.Enemies ?? 0;
        if (Imposter(world, lc, tc) && enemies == 0 && SetOf(world, slot).For(UniformImposter).Count > 0)
        {
            BookImposter(world, slot, lc, t, tc);
            TrySay(world, slot, t, UniformImposter);
            return;
        }
        if (TrainingDummyOwner(world, t) == lc.Faction && enemies == 0)
        {
            TrySay(world, slot, t, UsingMyTrainingEquipment);
            return;
        }
        if (InCampaign(world, lc) || tc.SlaveState == 1) return;

        if (HostilePlayer(world, lc, tc)) TrySay(world, slot, t, 66);
        else if (enemies == 0) TrySay(world, slot, t, 3);
    }

    /// <summary>
    /// The uniform test of the peaceful branch (<b>Verified</b>, constants from the data dump): the target's disguise level (status <c>+0x100</c>,
    /// <see cref="CharacterCold.DisguiseLevel"/>) above 0.25 when it wears a uniform piece of the looker's faction in any slot but shirt and boots
    /// (Character vfunc <c>0x408</c>), else above 0.5 when its uniform's faction (<c>+0x108</c>) is allied to the looker's, else never (99).
    /// </summary>
    bool Imposter(World world, CharacterCold lc, CharacterCold tc)
    {
        if (tc.DisguiseLevel <= 0) return false;

        float over = WearsUniformOf(lc.Faction, tc) ? 0.25f : tc.DisguiseFaction >= 0 && Allied(lc.Faction, tc.DisguiseFaction) ? 0.5f : 99;
        return tc.DisguiseLevel > over;
    }

    /// <summary>
    /// The seen-through impostor's booking before the line (<c>FUN_140852e10</c> with timer 20 and code 16, <b>Verified</b>): unless the looker's
    /// faction is <c>not real</c>, the target's disguise is blown (status <c>+0x110</c> 250, <see cref="CharacterCold.RecentlyWanted"/>) and a
    /// pending Uniform theft against the looker's faction is set (<see cref="Crime.CrimeSystem.SetPending"/>, the wrapper's path).
    /// </summary>
    void BookImposter(World world, int slot, CharacterCold lc, int t, CharacterCold tc)
    {
        if (_data.Relations.Contains(lc.Faction) && _data.Relations.Factions[lc.Faction].NotReal) return;

        tc.RecentlyWanted = Crime.CrimeSystem.RecentlyWantedSeconds;
        world.SystemOf<Crime.CrimeSystem>()?.SetPending(world, t, Crime.CrimeCode.UniformTheft, lc.Faction, world.Characters.IdOf(slot),
            Crime.CrimeSystem.WitnessTimer, viaWrapper: true);
    }

    /// <summary>
    /// Whether the character wears an item that is a uniform of <paramref name="faction"/> outside the shirt and boots slots.
    /// </summary>
    bool WearsUniformOf(int faction, CharacterCold c)
    {
        if (c.Inventory is not { } inv || !_data.Relations.Contains(faction)) return false;

        string id = _data.Factions[faction].Id;
        foreach (var item in inv.Items)
        {
            bool worn = item.Section is not ("shirt" or "boots") && Meitou.Simulation.Items.InventoryCommands.IsEquipmentSection(item.Section);
            if (worn && item.Uniform == id) return true;
        }
        return false;
    }

    /// <summary>
    /// The character's platoon is held in a campaign by its faction's war manager (<c>FUN_1409c4540</c>, any phase).
    /// </summary>
    static bool InCampaign(World world, CharacterCold c) =>
        c.SquadId >= 0 && world.Squads.Find(c.SquadId) is { PlatoonId: >= 0 } squad && world.Platoons.Find(squad.PlatoonId)?.Campaign is not null;

    /// <summary>
    /// Of the player's faction (the faction's <c>+0x250</c>).
    /// </summary>
    static bool OfPlayerFaction(World world, CharacterCold c) => world.Player.Faction >= 0 && c.Faction == world.Player.Faction;

    /// <summary>
    /// The same faction, or allied ones (<c>FUN_1406b2630</c>).
    /// </summary>
    bool Allied(int a, int b) => a == b || _data.Relations.Contains(a) && _data.Relations.Contains(b) && _data.Relations.IsAlly(a, b);

    /// <summary>
    /// A target of the player's faction whose faction the looker's is hostile to (<c>FUN_1406b26d0</c>).
    /// </summary>
    bool HostilePlayer(World world, CharacterCold lc, CharacterCold tc) =>
        OfPlayerFaction(world, tc) && _data.Relations.Contains(lc.Faction) && _data.Relations.Contains(tc.Faction)
        && _data.Relations.IsHostile(lc.Faction, tc.Faction);

    /// <summary>
    /// EV_USING_MY_TRAINING_EQUIPMENT (<c>FUN_140859680</c>).
    /// </summary>
    const int UsingMyTrainingEquipment = 59;

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
    /// Fires <paramref name="ev"/> at the character in <paramref name="target"/> when the looker has lines for it; whether a line was said.
    /// </summary>
    bool TrySay(World world, int slot, int target, int ev) =>
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
    /// Says the bubbles that are due (serial): the text, its effects, then the next line after 2 + 0.12 × the text's length seconds, or DA 12's own
    /// wait when longer.
    /// </summary>
    void StepBubbles(World world)
    {
        for (int k = 0; k < _bubbles.Count; k++)
        {
            var current = _bubbles[k];
            var (c, line, due, speaker, chained) = current;
            if (world.Tick < due) continue;
            if (!world.Characters.TryResolveNext(speaker, out int n) || !CanSpeak(world.Characters, n))
            {
                _bubbles.RemoveAt(k--);
                continue;
            }
            MarkUsed(world, c, line);
            string text =
                Said(Expand(world, c, line, PickText(world, speaker, line), speaker, speaker == c.Player ? c.Npc : c.Player)) + MoneySuffix(line);

            // locks, unlocks, interrupt and the rest land on the state of whoever says a chained line
            RunEffects(world, c, line, chained ? speaker : c.Npc);
            // A crowd trigger's forced starts drop other characters' bubbles from the list: find this one again.
            if ((k = _bubbles.FindIndex(b => ReferenceEquals(b, current))) < 0) continue;
            float seconds = 2 + 0.12f * text.Length;
            if (text.Length > 0)
            {
                long until = world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds);
                _shown[speaker] = (text, until, IsLarge(c.Event), IsInterrupting(c.Event));
                world.Said(NameOf(world, speaker), text, world.Characters.Cold(n) is { IsPlayer: true });
            }
            foreach (var e in line.Effects) if (e.Action == DialogueActions.BubbleWait) seconds = MathF.Max(seconds, e.Value);
            LogBubble(world, c, line, speaker, text);
            if (c.Ended || NextBubble(world, c, line) is not { } next)
            {
                string why = c.Ended ? " (ended by an effect)"
                    : line.Children.Count == 0 ? " (last line)"
                    : " (no next line passes, or its speaker is busy)";
                world.DialogueLines.Line(world, "bubble", $"{EventLog.Who(world, c.Npc)}: chain of {DialogueLog.Node(c.Root)} ends{why}");
                EndBubbles(world, c);
                _bubbles.RemoveAt(k--);
            }
            else _bubbles[k] = new Bubble(c, next.Line, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), next.Speaker, Chained: true);
        }
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
    /// A line's <c>crowd trigger</c> (<c>FUN_140680560</c>, unless the line is itself of event 74): every member of the saying character's squad
    /// (<paramref name="speaker"/>, the state the effects run on) that is not down (<see cref="CharacterFacts.Down"/>), not an animal and not in a
    /// bed starts that DIALOGUE as event 74 at the target, forced (<see cref="StartBubbles"/>): fighting, held and caged members too, a conversation
    /// they were in overwritten. Engine readings: the owner and the speaker are left out (they are talking), a player character is left to its
    /// player, and each member joins with the trigger's val0 percent (the data's values are 100 but for two lines).
    /// </summary>
    void CrowdTrigger(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        if (c.Event == CrowdTriggered || line.CrowdTrigger is not { } id || _dialogue.Node(id) is not { IsRoot: true } root) return;
        var table = world.Characters;
        foreach (var m in SquadOf(world, speaker))
        {
            if (m == c.Npc || m == speaker || !table.TryResolveNext(m, out int s) || !table.Next[s].Alive
                || table.Cold(s) is not { IsPlayer: false } mc)
            {
                continue;
            }
            if (mc.Fighter?.IsAnimal == true || mc.Appearance?.Animal is not null || CharacterFacts.Down(mc) || mc.InSomething == Locks.HeldState.Bed)
                continue;
            if (line.CrowdChance < 100 && Rng.Float(NextRoll(world, m)) * 100 >= line.CrowdChance) continue;
            StartBubbles(world, m, c.Player, root, CrowdTriggered, now: false, force: true);
        }
    }

    /// <summary>
    /// Whether the character at <paramref name="slot"/> can say a bubble line: alive and conscious. A caged one can (the original's step stops only
    /// for a ragdolled owner).
    /// </summary>
    static bool CanSpeak(CharacterTable table, int slot) => table.Next[slot].Alive && table.Cold(slot) is { } c && c.Medical?.Incapacitated != true;

    /// <summary>
    /// A bubble conversation's end (<c>FUN_140674830</c>): the event's counter and time; only an event-1 conversation leaves the speaker's squad
    /// remembering the target.
    /// </summary>
    void EndBubbles(World world, Conversation c)
    {
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
            _shown.Remove(who);
        }
    }
}