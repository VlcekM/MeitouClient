using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    /// <summary>
    /// A chosen line, the root it came from (a root's chosen child), and who says it.
    /// </summary>
    readonly record struct Picked(DialogueNode Line, DialogueNode? Root, CharacterId Speaker);

    /// <summary>
    /// Chooses a line from <paramref name="list"/> (<c>FUN_14067a0c0</c>, docs/game/dialogue.md "Choosing a line"): the list is sorted by score; the
    /// walk stops at the first line whose score (with the race bonus) is below the best so far; each node's speaker is resolved and the line check
    /// run with that speaker as "me" (the NPC for a player-side line); a passing root descends into its children; every passing line scoring at least
    /// the best so far is a candidate (earlier lower ones stay) and one candidate is picked at random. The checks and the race bonus use the target
    /// the walk started with (<paramref name="target"/>): a line check that retargets (<see cref="SetTarget"/>) changes who the T_TARGET speakers are
    /// for the rest of the walk, not the target the walk tests.
    /// </summary>
    Picked? Pick(World world, Conversation c, IReadOnlyList<DialogueNode> list, int depth = 0, CharacterId? target = null)
    {
        if (depth > 8) return null;
        var whom = target ?? c.Player;
        var candidates = new List<Picked>();
        int best = int.MinValue;
        foreach (var node in list)
        {
            int own = node.Score + RaceBonus(world, node, whom);
            if (!node.IsRoot && own < best) break;
            if (node.Interjection)
            {
                int slot = node.Speaker is >= Talker.Interjector1 and <= Talker.Interjector3 ? node.Speaker - Talker.Interjector1 : 0;
                c.Interjectors[slot] = CharacterId.None;
                if (FindInterjector(world, c, node) is not { } interjector) continue;
                c.Interjectors[slot] = interjector;
                if (Pick(world, c, node.Children, depth + 1, whom) is not { } inner) continue;
                Consider(own + LeafScore(inner.Line), inner with { Root = inner.Root ?? c.Root });
                continue;
            }
            if (!SpeakerOf(world, c, node, out var speaker)) continue;
            var me = IsNpcSide(node.Speaker) ? speaker : c.Npc;
            if (!Check(world, c, node, me, whom)) continue;
            if (node.IsRoot)
            {
                var root = c.Root;
                c.Root = node;
                var child = Pick(world, c, node.Children, depth + 1, whom);
                c.Root = root;
                if (child is not { } p) continue;
                Consider(own + LeafScore(p.Line), p with { Root = node });
            }
            else Consider(own, new Picked(node, c.Root, speaker));
        }
        if (candidates.Count == 0) return null;
        return candidates.Count == 1 ? candidates[0] : candidates[Rng.Int(NextRoll(world, c.Npc), candidates.Count)];

        int LeafScore(DialogueNode leaf) => leaf.Score + RaceBonus(world, leaf, whom);

        // Every candidate scoring at least the best so far joins the list and sets the best; the list is never cleared, so lower earlier ones stay in
        // the draw.
        void Consider(int score, Picked p)
        {
            if (score < best) return;
            best = score;
            candidates.Add(p);
        }
    }

    /// <summary>
    /// +2 when the target's race, or one of its RACE_GROUP, is in the line's <c>target race</c> (<c>FUN_140671bb0</c>).
    /// </summary>
    int RaceBonus(World world, DialogueNode node, CharacterId target) =>
        node.TargetRace.Count > 0 && Cold(world, target)?.Race is { } race && _dialogue.RaceMatches(race.StringId, node.TargetRace) ? 2 : 0;

    /// <summary>
    /// Who says <paramref name="node"/> (<c>FUN_140672bf0</c>); false: nobody (the line is skipped).
    /// </summary>
    bool SpeakerOf(World world, Conversation c, DialogueNode node, out CharacterId speaker)
    {
        speaker = node.Speaker switch
        {
            Talker.Me or Talker.WholeSquad => c.Npc,
            Talker.Target => c.Player,
            Talker.TargetIfPlayer => Cold(world, c.Player) is { IsPlayer: true } ? c.Player : CharacterId.None,
            Talker.Interjector1 or Talker.Interjector2 or Talker.Interjector3 => c.Interjectors[node.Speaker - Talker.Interjector1],
            Talker.TargetWithRace => WithRace(world, node, c.Player),
            _ => CharacterId.None,
        };
        return !speaker.IsNone;
    }

    /// <summary>
    /// T_TARGET_WITH_RACE (<c>FUN_140672bf0</c>): for each listed race in turn, the first member of the target's squad (in squad order, perceived or
    /// not) whose race matches it.
    /// </summary>
    CharacterId WithRace(World world, DialogueNode node, CharacterId target)
    {
        if (target.IsNone) return CharacterId.None;
        var squad = SquadOf(world, target);
        foreach (string listed in node.TargetRace)
            foreach (var m in squad)
                if (Cold(world, m)?.Race is { } r && _dialogue.RaceMatches(r.StringId, listed)) return m;
        return CharacterId.None;
    }

    /// <summary>
    /// An interjector for <paramref name="node"/> (<c>FUN_140679bc0</c>): an awake member of the NPC's own squad, not an animal, not already talking
    /// or interjecting, passing the node's check as the speaker; a <c>dumb</c> character only when the node's <c>is character</c> lists it. Weighted
    /// by distance (1 within 30, 0 beyond ~450, x0.1 for another race).
    /// </summary>
    CharacterId? FindInterjector(World world, Conversation c, DialogueNode node)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(c.Npc, out int n) || table.Cold(n) is not { } owner) return null;
        var at = table.Next[n].Position;
        var weighted = new List<(CharacterId Id, float Weight)>();
        foreach (var m in SquadOf(world, c.Npc))
        {
            if (m == c.Npc || c.Interjectors.Contains(m) || !table.TryResolveNext(m, out int s) || !Awake(table, s) || table.Cold(s) is not { } mc) continue;
            if (mc.Engaged || mc.Fighter?.IsAnimal == true) continue;
            if (!ListsSpeaker(node, mc.RecordId)) continue;
            float d2 = Vector3.DistanceSquared(table.Next[s].Position, at);
            float w = Math.Clamp((d2 - 202800f) / (900f - 202800f), 0, 1);
            if (mc.Race?.StringId != owner.Race?.StringId) w *= 0.1f;
            if (w <= 0 || !Check(world, c, node, m, c.Player)) continue;
            weighted.Add((m, w));
        }
        if (weighted.Count == 0) return null;
        float pick = Rng.Float(NextRoll(world, c.Npc)) * weighted.Sum(e => e.Weight);
        foreach (var (id, w) in weighted)
        {
            if (pick < w) return id;
            pick -= w;
        }
        return weighted[^1].Id;
    }

    /// <summary>
    /// The is-character list test of a speaker (<c>FUN_14067e060</c>, <c>FUN_140684d70</c>, <c>FUN_140679bc0</c>, Verified): a <c>dumb</c> record
    /// (<c>FUN_1405e89c0</c> false) speaks a root or an interjection only when the list names it, so an empty list fails; any other record passes an
    /// empty list or one that names it. The later lines of a conversation only test a non-empty list.
    /// </summary>
    internal bool ListsSpeaker(DialogueNode node, string record) =>
        node.IsCharacter.Contains(record) || node.IsCharacter.Count == 0 && !((node.IsRoot || node.Interjection) && _data.Db.Find(record)?.GetBool("dumb") == true);

    /// <summary>
    /// The line check (<c>FUN_140678f30</c>, docs/game/dialogue.md "Line check") with <paramref name="me"/> as the speaker and
    /// <paramref name="other"/> the target: tag masks, the lock, <c>chance temporary</c>, the record lists, <c>repetition limit</c>, <c>target is
    /// type</c>, <c>chance permanent</c>, then the conditions.
    /// </summary>
    bool Check(World world, Conversation c, DialogueNode node, CharacterId me, CharacterId other)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int ms) || table.Cold(ms) is not { } mc) return false;
        var tc = Cold(world, other);
        if (node.PersonalityRequired != 0 || node.PersonalityForbidden != 0)
        {
            uint bit = 1u << PersonalityOf(world, me, mc);
            if (node.PersonalityRequired != 0 && (node.PersonalityRequired & bit) == 0) return false;
            if ((node.PersonalityForbidden & bit) != 0) return false;
        }
        // DC_HAS_TAG (FUN_140674c50): the speaker squad's long-term tags on the target hold every required tag and no forbidden one; never noted:
        // only lines that require nothing pass; no target: not tested.
        if ((node.MemoryRequired | node.MemoryForbidden) != 0 && tc is not null)
        {
            if (Memory.Find(world, mc.SquadId, other) is not { } known) { if (node.MemoryRequired != 0) return false; }
            else if ((known.Long & node.MemoryRequired) != node.MemoryRequired || (known.Long & node.MemoryForbidden) != 0) return false;
        }
        if (node.IsRoot && (mc.DialogueLocks?.TryGetValue(node.StringId, out bool locked) == true ? locked : node.Locked)) return false;
        if (node.OneAtATime && node.IsRoot && RootLive(world, c, node)) return false;
        if (node.ChanceTemporary < 99 && Rng.Float(NextRoll(world, me)) * 100 >= node.ChanceTemporary) return false;
        if (node.InTownOf.Count > 0 && (TownFaction(world, table.Next[ms].Position) is not { } tf || !node.InTownOf.Contains(tf))) return false;
        if (node.MyFaction.Count > 0 && !node.MyFaction.Contains(FactionId(mc.Faction))) return false;
        if (node.TargetFaction.Count > 0 && (tc is null || !node.TargetFaction.Contains(FactionId(tc.Faction)))) return false;
        if (node.HasPackage.Count > 0 && !node.HasPackage.Overlaps(SetOf(world, ms).Packages)) return false;
        if (!ListsSpeaker(node, mc.RecordId)) return false;
        if (node.TargetCarryingCharacter.Count > 0)
        {
            // Each listed character in turn: the first member of the target's squad (perceived) carrying it becomes the target; any hit passes.
            bool carried = false;
            foreach (string listed in node.TargetCarryingCharacter)
                foreach (var m in Known(world, me, other))
                    if (Cold(world, m) is { Carrying.IsNone: false } carrier && Cold(world, carrier.Carrying) is { } body && body.RecordId == listed)
                    {
                        SetTarget(c, me, m);
                        carried = true;
                        break;
                    }
            if (!carried) return false;
        }
        var states = _population?.States ?? _data.States;
        foreach (var (state, value) in node.WorldStates) if (!IWorldStates.Passes(states, _data.Db, state, value ? 1 : 0)) return false;
        if (node.TargetHasItemFunction != 0 || node.TargetHasItem.Count > 0)
        {
            // One ordered pass over the target's squad (FUN_140678f30): per member the function is seen first (sticky), then the listed items; the
            // first member holding a listed item (any member when the line lists none) becomes the target and ends the pass. The line needs both
            // flags.
            bool itemOk = node.TargetHasItem.Count == 0, typeOk = node.TargetHasItemFunction == 0;
            foreach (var m in Known(world, me, other))
            {
                var items = Cold(world, m)?.Inventory;
                if (!typeOk && items is not null && items.Items.Any(i => _data.Db.Find(i.Record)?.GetInt("item function") == node.TargetHasItemFunction)) typeOk = true;
                if (!itemOk && items is not null) itemOk = Listed(items).Any(i => node.TargetHasItem.Contains(i.Record));
                if (!itemOk) continue;
                SetTarget(c, me, m);
                break;
            }
            if (!itemOk || !typeOk) return false;
        }
        if (node.TargetRace.Count > 0 && !(tc?.Race is { } targetRace && _dialogue.RaceMatches(targetRace.StringId, node.TargetRace)))
        {
            // The target itself passes with no side effect; else per listed race in turn the first member of its squad of that race becomes the
            // target (the last race to hit decides).
            bool hit = false;
            foreach (string listed in node.TargetRace)
                foreach (var m in Known(world, me, other))
                    if (Cold(world, m)?.Race is { } r && _dialogue.RaceMatches(r.StringId, listed))
                    {
                        SetTarget(c, me, m);
                        hit = true;
                        break;
                    }
            if (!hit) return false;
        }
        if (node.MyRace.Count > 0 && (mc.Race is null || !_dialogue.RaceMatches(mc.Race.StringId, node.MyRace))) return false;
        if (node.MySubrace.Count > 0 && (mc.Race is null || !node.MySubrace.Contains(mc.Race.StringId))) return false;
        if (node.CooldownHours > 0 && _lastUse.TryGetValue(node.StringId, out double last) && world.GameHours - last < node.CooldownHours) return false;
        if (node.TargetIsType > 0)
        {
            if (tc is null) return false;
            // Slave (6) also takes a target whose slave state is slave or escaped, and an accepted
            // slave line is final: chance permanent and the conditions are not looked at.
            if (node.TargetIsType == 6) return tc.SlaveState is 1 or 2 || (int)tc.NpcClass == 6;
            if ((int)tc.NpcClass != node.TargetIsType) return false;
        }
        if (node.ChancePermanent < 99)
        {
            var key = (me, node.StringId);
            if (!_permanent.TryGetValue(key, out bool passed)) _permanent[key] = passed = Rng.Float(NextRoll(world, me)) * 100 < node.ChancePermanent;
            if (!passed) return false;
        }
        foreach (var condition in node.Conditions)
            if (!Condition(world, c, condition, me, mc, other)) return false;
        return true;
    }

    /// <summary>
    /// A line check found <paramref name="member"/> of the target's squad (<c>FUN_140671930</c>): it becomes the conversation's target unless it is
    /// the NPC itself or already the target. The state written is <paramref name="me"/>'s, so only a check of the conversation's NPC retargets (an
    /// interjector candidate's own state is not the conversation's). Happens when the test matches, even if a later test of the line fails.
    /// </summary>
    static void SetTarget(Conversation c, CharacterId me, CharacterId member)
    {
        if (me == c.Npc && !member.IsNone && member != c.Npc) c.Player = member;
    }

    /// <summary>
    /// What a <c>target has item</c> test scans (<c>FUN_140745f70</c>): the inventory's own flat list, then the worn backpack's own list; no
    /// container deeper.
    /// </summary>
    static IEnumerable<Items.ItemInstance> Listed(Items.Inventory inventory) =>
        inventory.Backpack?.Contents is { } bag ? inventory.Items.Concat(bag.Items) : inventory.Items;

    /// <summary>
    /// The character's cold state; null when the id no longer resolves.
    /// </summary>
    internal static CharacterCold? Cold(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int s) ? world.Characters.Cold(s) : null;

    /// <summary>
    /// The members of <paramref name="id"/>'s squad (itself alone without one).
    /// </summary>
    static IReadOnlyList<CharacterId> SquadOf(World world, CharacterId id) =>
        Cold(world, id) is { SquadId: >= 0 } c && world.Squads.Find(c.SquadId) is { } squad ? squad.Members : [id];

    /// <summary>
    /// The members of <paramref name="target"/>'s squad that <paramref name="me"/> perceives (<c>FUN_140855960</c>): the target itself, and the
    /// others within <paramref name="me"/>'s sight range (engine stand-in for the original's sensed list).
    /// </summary>
    static IEnumerable<CharacterId> Known(World world, CharacterId me, CharacterId target)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(me, out int ms) || table.Cold(ms) is not { } mc) return [];
        var at = table.Next[ms].Position;
        float r = AggroSystem.RangeOf(mc, AggroSystem.OutdoorSight);
        return SquadOf(world, target).Where(m => m == target || m == me
            || table.TryResolveNext(m, out int s) && Vector3.DistanceSquared(table.Next[s].Position, at) <= r * r);
    }

    /// <summary>
    /// The character's personality tag (<see cref="Personality"/>): rolled from its own record, else its faction's.
    /// </summary>
    int PersonalityOf(World world, CharacterId id, CharacterCold cold) => Personality.Of(world, _data.Db, id, cold, FactionId(cold.Faction));

    string FactionId(int faction) => _data.Relations.Contains(faction) ? _data.Factions[faction].Id : "";

    /// <summary>
    /// The placed town whose radius holds <paramref name="at"/> (the game's position lookup, <see cref="PopulationData.TownAt"/>: the town status of
    /// a character), or -1.
    /// </summary>
    int TownAt(Vector3 at) => _data.TownAt(at);

    string? TownFaction(World world, Vector3 at) =>
        TownAt(at) is >= 0 and var site && site < _data.SiteFactions.Count && _data.SiteFactions[site] is >= 0 and var f ? FactionId(f) : null;
}