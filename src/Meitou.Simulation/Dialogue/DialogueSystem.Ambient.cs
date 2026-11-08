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
/// A line on show over a character's head (docs/game/dialogue.md "Ambient conversations"): <see cref="Large"/> for the events that use the large font,
/// <see cref="Interrupting"/> for the interrupting set, whose bubbles fade over a longer range and stay on screen.
/// </summary>
public sealed record SpeechBubble(CharacterId Speaker, string Text, bool Large, bool Interrupting = false);

public sealed partial class DialogueSystem
{
    /// <summary>
    /// A speech-bubble conversation under way (docs/game/dialogue.md "Ambient conversations"): the line said next, by whom, and when. <paramref name="Chained"/>: a line after the first, which the
    /// original runs on its speaker's own dialogue state (<c>FUN_140684720</c>); the first runs on the owner's (<c>FUN_140683f90</c>).
    /// </summary>
    sealed record Bubble(Conversation C, DialogueNode Line, long DueTick, CharacterId Speaker, bool Chained = false);

    readonly List<Bubble> _bubbles = [];
    // The lines on show: the text and the tick it goes (display only: follows from the bubbles, so not hashed).
    readonly SortedDictionary<CharacterId, (string Text, long Until, bool Large, bool Interrupting)> _shown = new(Comparer<CharacterId>.Create(CompareIds));
    // When a squad last reacted to seeing a character (the cooldown of SeeOthers).
    readonly SortedDictionary<(int Squad, CharacterId Target), double> _ambientAt = new(Comparer<(int, CharacterId)>.Create((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : CompareIds(a.Item2, b.Item2)));
    // The NPC / player-character pairs noticed at the world's first look (a new game or a load) and not lost from notice since: no sight line for them (engine choice; the
    // original greets a character arriving, not one already beside it at the start, Observed). Hashed.
    readonly SortedSet<(CharacterId Npc, CharacterId Player)> _quiet = new(Comparer<(CharacterId, CharacterId)>.Create((a, b) => CompareIds(a.Item1, b.Item1) is var c and not 0 ? c : CompareIds(a.Item2, b.Item2)));
    bool _looked;

    /// <summary>The event of a crowd trigger's lines (EV_CROWD_TRIGGERED), and of a contract's end talk (EV_CONTRACT_JOB_ENDED).</summary>
    const int CrowdTriggered = 74;
    /// <summary>EV_UNLOCK_MY_CAGE_OR_SHACKLES: a freed prisoner's thanks, which also makes its squad remember the liberator (<see cref="Freed"/>).</summary>
    const int UnlockMyCageOrShackles = 13;
    public const int ContractJobEnded = 30;
    /// <summary>The AI-to-AI talking range (DC 16 TARGET_IN_TALKING_RANGE, 260 units): how near another squad's NPC must be to be greeted (engine choice).</summary>
    const float TalkingRange = 260;

    /// <summary>The events whose bubbles use the large font (<c>FUN_1406713a0</c>, <b>Verified</b>); the others use the medium one.</summary>
    static bool IsLarge(int ev) => ev is 2 or 11 or 17 or 26 or 31 or 34 or 36 or 40 or 45 or 74;

    /// <summary>The interrupting set (<c>FUN_1406712f0</c>, <b>Verified</b>): the large-font events and 6, 13, 30, 46, 48, 51, 54.</summary>
    static bool IsInterrupting(int ev) => IsLarge(ev) || ev is 6 or 13 or 30 or 46 or 48 or 51 or 54;

    public IReadOnlyList<SpeechBubble> PublishSpeech() => [.. _shown.Select(s => new SpeechBubble(s.Key, s.Value.Text, s.Value.Large, s.Value.Interrupting))];

    /// <summary>
    /// A campaign's <c>dialog announcement</c> (docs/game/factions-squads-towns.md "Campaigns", announcement): <paramref name="dialogueId"/> fired as event 2 by
    /// <paramref name="npc"/> with no target. Its lines are said one after another as speech bubbles. Their effects run as said (an <c>interrupt</c> makes the next talk
    /// with the speaker that dialogue). An animal never announces (in the original only the leader's AI package does). Returns whether it started.
    /// </summary>
    public bool Announce(World world, CharacterId npc, string dialogueId) =>
        world.Characters.TryResolveNext(npc, out int slot) && world.Characters.Cold(slot) is { } c && !SquadRegistry.IsAnimal(c)
        && _dialogue.Node(dialogueId) is { IsRoot: true } root && StartBubbles(world, npc, CharacterId.None, root, DialogueEvents.Announcement, now: false);

    /// <summary>
    /// Starts <paramref name="root"/> as a bubble conversation of <paramref name="npc"/> with <paramref name="whom"/> (<c>FUN_140683f90</c>): the best of its lines (speakers resolved as in
    /// any choice) is said, the chain after it. <paramref name="now"/>: said this tick, else on the next bubble step. Returns whether a line was found. <paramref name="force"/>
    /// (a crowd trigger): the start is not refused for a character busy talking or caged; what it was saying is dropped without the end step (no counter), as the original overwrites
    /// the state (a character in the open player conversation is still refused: engine choice).
    /// </summary>
    bool StartBubbles(World world, CharacterId npc, CharacterId whom, DialogueNode root, int ev, bool now = true, bool force = false)
    {
        if (!world.Characters.TryResolveNext(npc, out int n)) return false;
        if (force)
        {
            if (!CanSpeak(world.Characters, n) || _open is { } o && (o.Npc == npc || o.Player == npc)) return false;
            _bubbles.RemoveAll(b => b.C.Npc == npc || b.Speaker == npc);
            _shown.Remove(npc);
        }
        else if (!Awake(world.Characters, n) || BubbleOf(npc)) return false;
        var c = new Conversation { Npc = npc, Player = whom, Event = ev, Root = root };
        if (Pick(world, c, root.Children) is not { } first || !force && Busy(first.Speaker, c)) return false;
        _bubbles.Add(new Bubble(c, first.Line, world.Tick, first.Speaker));
        if (now) StepBubbles(world);
        return true;
    }

    /// <summary>
    /// <paramref name="ev"/> fired at <paramref name="npc"/> with <paramref name="whom"/> as its target outside a player conversation (<c>FUN_140684990</c>): a line is chosen from the
    /// NPC's list for the event and said as a speech bubble, the rest of its chain after it. Returns whether a line was said.
    /// </summary>
    bool Fire(World world, CharacterId npc, CharacterId whom, int ev)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(npc, out int n) || !Awake(table, n) || BubbleOf(npc)) return false;
        var c = new Conversation { Npc = npc, Player = whom, Event = ev };
        if (Pick(world, c, SetOf(world, n).For(ev)) is not { } picked || picked.Speaker.IsNone || picked.Speaker == whom || Busy(picked.Speaker, c)) return false;
        c.Root = picked.Root;
        _bubbles.Add(new Bubble(c, picked.Line, world.Tick, picked.Speaker));
        StepBubbles(world);
        return true;
    }

    /// <summary>
    /// The "one at a time" test of a root (<c>FUN_140678f30</c>, <b>Verified</b>): a member of the conversation owner's squad, the owner included, has a live conversation rooted at <paramref name="node"/>,
    /// the open window or a bubble chain of another conversation than <paramref name="c"/>.
    /// </summary>
    bool RootLive(World world, Conversation c, DialogueNode node) =>
        SquadOf(world, c.Npc).Any(m => _open is { } o && o != c && o.Npc == m && o.Root == node || _bubbles.Any(b => b.C != c && b.C.Npc == m && b.C.Root == node));

    bool BubbleOf(CharacterId who) => _shown.ContainsKey(who) || _bubbles.Any(b => b.Speaker == who || b.C.Npc == who);

    /// <summary>Whether <paramref name="who"/> is saying a line of another conversation, or is in the open player conversation.</summary>
    bool Busy(CharacterId who, Conversation c) =>
        _open is { } o && (o.Npc == who || o.Player == who) || _bubbles.Any(b => b.C != c && (b.Speaker == who || b.C.Npc == who));

    /// <summary>
    /// Whom a loaded NPC reacts to by sight (the perception that fires events 3, 66 and 67, <c>FUN_140859680</c>): the nearest player character it sees by the sight test every
    /// other sighting uses (<see cref="SightMap.Sees"/>: its race's range, daylight, light, places and view cone; a sneaker with its stealth, without the spot meter: engine choice),
    /// or that a squad mate sees within twice its range R (the shared sightings of <c>FUN_140857b10</c>), 66 hostile, 67 ally, else 3; else the nearest awake NPC of another squad within <see cref="TalkingRange"/> (event 3, as the original fires it for a seen character outside
    /// the player's faction), only within <see cref="DialogueSettings.AmbientRange"/> of a player character, where it can be heard. The nearest to a player look first.
    /// </summary>
    void SeeOthers(World world)
    {
        var table = world.Characters;
        var players = new List<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: true, Medical.Dead: false }) players.Add(i);
        if (players.Count == 0 || !_data.Relations.Contains(world.Player.Faction)) return;
        float audible2 = Settings.AmbientRange * Settings.AmbientRange;
        // The NPCs that can look or be greeted: loaded, awake, free, within their sight reach of a player character; by that distance, then slot.
        var near = new List<(int Slot, float D2)>();
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Next[i].Task == (byte)CharacterTask.Rest || table.Cold(i) is not { IsPlayer: false, SquadId: >= 0, Engaged: false, InCombat: false, Fleeing: false, IsImprisoned: false, SlaveState: not 1 } nc) continue;   // a slave (state 1) says nothing (FUN_1408529c0)
            if (nc.Fighter?.IsAnimal == true || nc.Appearance?.Animal is not null || !_data.Relations.Contains(nc.Faction) || !Awake(table, i)) continue;
            float reach = MathF.Max(world.Sight.MaxRange(i, AggroSystem.RangeOf(nc, AggroSystem.OutdoorSight)), Settings.AmbientRange);
            float nearest = float.MaxValue;
            foreach (int p in players) nearest = MathF.Min(nearest, Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position));
            if (nearest <= reach * reach) near.Add((i, nearest));
        }
        near.Sort((a, b) => a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.Slot.CompareTo(b.Slot));
        // Who sees which player character, and which squads have a member seeing it (the original's squad-shared sightings, GroupSense: a mate's counts within 2 R).
        var sees = new HashSet<(int Npc, int Player)>();
        var squadSees = new HashSet<(int Squad, int Player)>();
        foreach (var (i, _) in near)
        {
            var nc = table.Cold(i)!;
            float outdoor = AggroSystem.RangeOf(nc, AggroSystem.OutdoorSight);
            foreach (int p in players)
            {
                var pc = table.Cold(p)!;
                float stealth = (pc.Orders & CharacterOrders.Sneak) != 0 ? Orders.Stealth(pc) : -1;
                if (world.Sight.Sees(i, p, table.Next[i].Position, table.Next[p].Position, outdoor, stealth) <= 0) continue;
                sees.Add((i, p));
                squadSees.Add((nc.SquadId, p));
            }
        }
        // The pairs noticed now (seen, or by a squad mate within 2 R). Those noticed at the first look of the world stay quiet until the player character has been out of notice.
        var noticed = new HashSet<(CharacterId Npc, CharacterId Player)>();
        foreach (var (i, _) in near)
        {
            float shared = 2 * AggroSystem.RangeOf(table.Cold(i)!, AggroSystem.OutdoorSight);
            foreach (int p in players)
                if (sees.Contains((i, p)) || Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position) <= shared * shared && squadSees.Contains((table.Cold(i)!.SquadId, p)))
                    noticed.Add((table.IdOf(i), table.IdOf(p)));
        }
        if (!_looked) { _looked = true; _quiet.UnionWith(noticed); }
        else _quiet.RemoveWhere(q => !noticed.Contains(q));
        int said = 0;
        foreach (var (i, nearest) in near)
        {
            if (said >= Settings.AmbientPerScan) break;
            var nc = table.Cold(i)!;
            var npc = table.IdOf(i);
            int best = -1;
            float best2 = float.MaxValue;
            foreach (int p in players)
            {
                float d2 = Vector3.DistanceSquared(table.Next[i].Position, table.Next[p].Position);
                if (d2 < best2 && noticed.Contains((npc, table.IdOf(p))) && !_quiet.Contains((npc, table.IdOf(p)))) (best, best2) = (p, d2);
            }
            // The squad's cooldown starts only once a line is said: the original asks again on every perception update and rolls `chance temporary` afresh each time
            // (the Red Sabres' attack root is 20%), so a failed look is tried again on the next scan. A player character it has nothing to say to leaves the turn to the NPCs.
            // Events 66 and 3 (the non-friend branch) need a looker that is no slave and has not escaped; 67 (the friend branch) does not (FUN_140859680).
            int sight = SightEvent(world, i, nc, best >= 0 ? best : -1);
            bool free = nc is { SlaveState: 0, Escaped: false };
            if (best >= 0 && (sight == 67 || free) && !Recent(world, nc.SquadId, table.IdOf(best))
                && TrySay(world, i, nc.SquadId, best, sight))
            {
                said++;
                continue;
            }
            if (nearest > audible2) continue;
            (best, best2) = (-1, TalkingRange * TalkingRange);
            foreach (var (o, od2) in near)
            {
                if (od2 > audible2 || table.Cold(o)!.SquadId == nc.SquadId) continue;
                float d2 = Vector3.DistanceSquared(table.Next[i].Position, table.Next[o].Position);
                if (d2 <= best2 && !Recent(world, nc.SquadId, table.IdOf(o))) (best, best2) = (o, d2);
            }
            if (best >= 0 && free && TrySay(world, i, nc.SquadId, best, 3)) said++;
        }
        SeeAnimals(world, near, said);
    }

    /// <summary>EV_USING_MY_TRAINING_EQUIPMENT (<c>FUN_140859680</c>).</summary>
    const int UsingMyTrainingEquipment = 59;

    /// <summary>
    /// The event an NPC fires at a seen player character (<c>FUN_140859680</c>, <b>Verified</b>): 67 for an ally; else 59 when the target is training on a dummy owned by the looker's faction
    /// (the target's <c>+0x158</c> holds the faction of the dummy it uses, set by <c>Task_UseTrainingDummy</c>'s update and reset every character tick) while the looker senses no enemy,
    /// else 66 for an enemy of the player and 3 for the rest.
    /// </summary>
    int SightEvent(World world, int looker, CharacterCold nc, int target)
    {
        if (_data.Relations.IsAlly(nc.Faction, world.Player.Faction)) return 67;
        if (target >= 0 && TrainingDummyOwner(world, target) == nc.Faction && world.SystemOf<AggroSystem>()?.SensesOf(looker) is not { Enemies: > 0 }) return UsingMyTrainingEquipment;
        return _data.Relations.IsHostile(nc.Faction, world.Player.Faction) ? 66 : 3;
    }

    /// <summary>The faction owning the training dummy the character is using now (Character <c>+0x158</c>); -1 when it uses none.</summary>
    internal static int TrainingDummyOwner(World world, int slot) =>
        world.Characters.Cold(slot) is { OperateSite: { } site } && world.Fixtures.Find(site) is { Kind: Locks.FixtureKind.Training } dummy ? dummy.Owner : -1;

    /// <summary>I_SEE_ANIMAL_SQUAD: an NPC looker that passes the same filter and sees an awake animal fires it at the animal (<c>FUN_140859680</c> :54-64, before the friend/enemy split).</summary>
    const int SeeAnimalSquad = 27;

    void SeeAnimals(World world, List<(int Slot, float D2)> near, int said)
    {
        var table = world.Characters;
        var animals = new List<int>();
        for (int a = 0; a < table.HighWater; a++)
            if (table.Next[a].Alive && Awake(table, a) && table.Cold(a) is { } ac && (ac.Fighter?.IsAnimal == true || ac.Appearance?.Animal is not null)) animals.Add(a);
        if (animals.Count == 0) return;
        foreach (var (i, _) in near)
        {
            if (said >= Settings.AmbientPerScan) return;
            var nc = table.Cold(i)!;
            float outdoor = AggroSystem.RangeOf(nc, AggroSystem.OutdoorSight);
            foreach (int a in animals)
                if (world.Sight.Sees(i, a, table.Next[i].Position, table.Next[a].Position, outdoor, -1) > 0 && !Recent(world, nc.SquadId, table.IdOf(a)) && TrySay(world, i, nc.SquadId, a, SeeAnimalSquad))
                {
                    said++;
                    break;
                }
        }
    }

    /// <summary>Fires <paramref name="ev"/> at the character in <paramref name="target"/>; when a line is said, the squad does not react to that character again for the cooldown.</summary>
    bool TrySay(World world, int slot, int squad, int target, int ev)
    {
        var whom = world.Characters.IdOf(target);
        if (SetOf(world, slot).For(ev).Count == 0 || !Fire(world, world.Characters.IdOf(slot), whom, ev)) return false;
        _ambientAt[(squad, whom)] = world.GameHours;
        return true;
    }

    // The characters that were down at the last look (the get-up events); hashed.
    readonly SortedSet<CharacterId> _down = new(Comparer<CharacterId>.Create(CompareIds));

    /// <summary>EV_GET_UP_PEACE, EV_GET_UP_FIGHT, EV_GET_UP_UNNECCESSARY_FIGHT.</summary>
    const int GetUpPeace = 23, GetUpFight = 24, GetUpUnnecessaryFight = 25;

    /// <summary>
    /// Getting up (<c>Task_GetUp</c>, <c>FUN_14033ca10</c> / <c>FUN_1408c6780</c>, docs/game/ragdolls.md): the events fire only when the task starts with posture 0, so a character that was not standing (out cold, staying low, crawling) at the last look and stands now fires, with no target (a wake that stays low fires nothing until it stands),
    /// 23 when it senses no enemy or neutral (<see cref="BodySystem.Senses"/>); else, stood up on the player's order with no conscious ally sensed, 25; else 24.
    /// </summary>
    void SeeGettingUp(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Medical: { } medical } c) continue;
            var id = table.IdOf(i);
            if (medical.Dead) { _down.Remove(id); continue; }
            if (!medical.Standing) { _down.Add(id); continue; }   // knocked out, staying low or crawling: posture not 0
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

    bool EnemyNear(World world, int slot, CharacterCold c)
    {
        var table = world.Characters;
        if (!_data.Relations.Contains(c.Faction)) return false;
        float r = AggroSystem.RangeOf(c, AggroSystem.OutdoorSight);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (i == slot || !table.Next[i].Alive || table.Cold(i) is not { Medical.Incapacitated: false } o || !_data.Relations.Contains(o.Faction)) continue;
            if (_data.Relations.IsHostile(c.Faction, o.Faction) && Vector3.DistanceSquared(table.Next[i].Position, table.Next[slot].Position) <= r * r) return true;
        }
        return false;
    }

    bool Recent(World world, int squad, CharacterId target) => _ambientAt.TryGetValue((squad, target), out double at) && world.GameHours - at < Settings.AmbientRepeatHours;

    /// <summary>Whether talking to <paramref name="npc"/> opens a pending <c>interrupt</c> dialogue (a campaign's negotiation).</summary>
    public bool HasInterrupt(CharacterId npc) => _interrupts.ContainsKey(npc);

    /// <summary>
    /// The line said after <paramref name="line"/> (<c>FUN_140684720</c>): the best of its children with their speakers resolved (the owner, its target, an interjector), as in a choice;
    /// none when the root is not a monologue (a non-monologue root in bubble mode ends after its first line, <c>FUN_140685090</c>) or the speaker is busy elsewhere.
    /// </summary>
    Picked? NextBubble(World world, Conversation c, DialogueNode line)
    {
        if (line.Children.Count == 0 || c.Root is { Monologue: false }) return null;
        return Pick(world, c, line.Children) is { } next && !Busy(next.Speaker, c) ? next : null;
    }

    /// <summary>Says the bubbles that are due (serial): the text, its effects, then the next line after 2 + 0.12 × the text's length seconds, or DA 12's own wait when longer.</summary>
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
            string text = Said(Expand(world, c, line, PickText(world, speaker, line), speaker, speaker == c.Player ? c.Npc : c.Player)) + MoneySuffix(line);
            RunEffects(world, c, line, chained ? speaker : c.Npc);   // locks, unlocks, interrupt and the rest land on the state of whoever says a chained line
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
            if (c.Ended || NextBubble(world, c, line) is not { } next)
            {
                EndBubbles(world, c);
                _bubbles.RemoveAt(k--);
            }
            else _bubbles[k] = new Bubble(c, next.Line, world.Tick + (long)MathF.Ceiling(seconds / world.TickSeconds), next.Speaker, Chained: true);
        }
    }

    /// <summary>
    /// A line's <c>crowd trigger</c> (<c>FUN_140680560</c>, unless the line is itself of event 74): every member of the saying character's squad (<paramref name="speaker"/>, the state the
    /// effects run on) that is not down (<see cref="Down"/>), not an animal and not in a bed starts that DIALOGUE as event 74 at the target, forced (<see cref="StartBubbles"/>): fighting,
    /// held and caged members too, a conversation they were in overwritten. Engine readings: the owner and the speaker are left out (they are talking), a player character is left to its
    /// player, and each member joins with the trigger's val0 percent (the data's values are 100 but for two lines).
    /// </summary>
    void CrowdTrigger(World world, Conversation c, DialogueNode line, CharacterId speaker)
    {
        if (c.Event == CrowdTriggered || line.CrowdTrigger is not { } id || _dialogue.Node(id) is not { IsRoot: true } root) return;
        var table = world.Characters;
        foreach (var m in SquadOf(world, speaker))
        {
            if (m == c.Npc || m == speaker || !table.TryResolveNext(m, out int s) || !table.Next[s].Alive || table.Cold(s) is not { IsPlayer: false } mc) continue;
            if (mc.Fighter?.IsAnimal == true || mc.Appearance?.Animal is not null || CharacterFacts.Down(mc) || mc.InSomething == Locks.HeldState.Bed) continue;
            if (line.CrowdChance < 100 && Rng.Float(NextRoll(world, m)) * 100 >= line.CrowdChance) continue;
            StartBubbles(world, m, c.Player, root, CrowdTriggered, now: false, force: true);
        }
    }

    /// <summary>Whether the character at <paramref name="slot"/> can say a bubble line: alive and conscious. A caged one can (the original's step stops only for a ragdolled owner).</summary>
    static bool CanSpeak(CharacterTable table, int slot) => table.Next[slot].Alive && table.Cold(slot) is { } c && c.Medical?.Incapacitated != true;

    /// <summary>A bubble conversation's end (<c>FUN_140674830</c>): the event's counter and time; only an event-1 conversation leaves the speaker's squad remembering the target.</summary>
    void EndBubbles(World world, Conversation c)
    {
        var key = (c.Npc, c.Event);
        var (count, _) = _counters.GetValueOrDefault(key);
        _counters[key] = (count + 1, world.GameHours);
        if (c.Event == DialogueEvents.PlayerTalkToMe && !c.Player.IsNone && world.Characters.TryResolveNext(c.Npc, out int n) && world.Characters.Cold(n) is { SquadId: >= 0 } nc)
            Memory.Remember(world, nc.SquadId, c.Player, MemoryTag.None);
    }

    /// <summary>Drops the lines whose time is up or whose speaker is gone (serial, each tick).</summary>
    void ExpireSpeech(World world)
    {
        if (_shown.Count == 0) return;
        foreach (var who in _shown.Where(s => world.Tick >= s.Value.Until || !world.Characters.TryResolveNext(s.Key, out _)).Select(s => s.Key).ToArray()) _shown.Remove(who);
    }
}