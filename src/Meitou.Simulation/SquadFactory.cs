using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

/// <summary>Which WORLD_EVENT_STATE records hold. The start state of a new game is Unknown (docs/game/factions-squads-towns.md section 3.5), so the default holds none.</summary>
public interface IWorldStates
{
    bool IsTrue(string worldStateId);

    /// <summary>No state holds: templates and characters gated by a world state do not spawn.</summary>
    public static IWorldStates None { get; } = new NoStates();

    sealed class NoStates : IWorldStates
    {
        public bool IsTrue(string worldStateId) => false;
    }
}

/// <summary>One character a squad will have: which record, in which role, where relative to the squad position.</summary>
public sealed record MemberPlan(string RecordId, SquadRole Role, int Index, Vector2 Offset, bool IsAnimal, float Age);

/// <summary>What a squad template makes, before any character exists: the members in creation order and the squads made recursively for <c>slaves</c>.</summary>
public sealed record SquadPlan(SquadTemplate Template, IReadOnlyList<MemberPlan> Members, IReadOnlyList<SquadPlan> Slaves, IReadOnlyList<string> Problems)
{
    /// <summary>Members of this squad and, recursively, of its slave squads.</summary>
    public int TotalMembers => Members.Count + Slaves.Sum(s => s.TotalMembers);
}

/// <summary>
/// Squad creation per docs/game/factions-squads-towns.md section 5.1 (<c>createRandomSquad</c>, <b>Observed</b>), as a pure function
/// of the template, the multiplier and a seed: the same inputs give the same plan whatever thread or time it runs at. Randomness comes
/// from the keyed streams (<see cref="Rng"/>), not the C <c>rand()</c> of the original.
/// <list type="bullet">
/// <item>Counts: n = v0 when v1 is 0 or 100 (100 is a sentinel, not a maximum), else a uniform integer in [v0, v1] (v1 below v0 gives v0);
/// then n' = trunc(n x M), at least 1 when n is above 0, M the Squad size multiplier unless the template has <c>dont multiply</c>.</item>
/// <item>Order and roles: the first <c>leader</c> (role 2); <c>num random chars</c> picks from <c>choosefrom list</c> (weighted by v0, 0 counts
/// as 100, entries gated by a world state that does not hold are skipped; role 0, or 2 when there is no leader yet); <c>squad</c> (role 0);
/// <c>squad2</c> (role 1); <c>animals</c> (role 0, the first becomes the leader when nothing is); <c>animals2</c> (role 1); <c>slaves</c> as
/// squads of their own (role 4).</item>
/// <item>Layout: member number i (every member so far; the leader is 0) stands at (3 x (i mod 8), 3 x floor(i / 8) + (i mod 2)) from the squad
/// position; animals2 use spacing 5.</item>
/// <item>Not made yet (Unknown or later stages): <c>prisoners</c> (they need cages in the home building), <c>housemates</c>, the unique-character
/// replacement of a story-unique leader, and the squad's AI packages.</item>
/// </list>
/// </summary>
public static class SquadFactory
{
    /// <summary>Whether a record is something a squad is made from: a SQUAD_TEMPLATE or a UNIQUE_SQUAD_TEMPLATE (null: no).</summary>
    public static bool IsTemplate(GameRecord? record) => record is { Type: FcsRecordType.SQUAD_TEMPLATE or FcsRecordType.UNIQUE_SQUAD_TEMPLATE };

    /// <summary>The count of one member list entry (section 5.1): the range rule, then the multiplier.</summary>
    public static int Count(int v0, int v1, float multiplier, bool dontMultiply, ulong roll)
    {
        int n = v0;
        if (v1 != 0 && v1 != 100) n = v1 < v0 ? v0 : Math.Clamp(v0 + (int)(Rng.Float(roll) * (v1 - v0 + 1)), v0, v1);
        if (n > 0 && !dontMultiply && multiplier != 1) n = Math.Max((int)(n * multiplier), 1);
        return n;
    }

    /// <summary>Offset of member number <paramref name="i"/> from the squad position.</summary>
    public static Vector2 Offset(int i, float spacing = 3) => new(spacing * (i % 8), spacing * (i / 8) + (i % 2));

    public static SquadPlan Plan(GameDatabase db, SquadTemplate template, float multiplier, ulong seed, ulong key, IWorldStates? states = null)
    {
        states ??= IWorldStates.None;
        var members = new List<MemberPlan>();
        var problems = new List<string>();
        var slaves = new List<SquadPlan>();
        ulong counter = 0;
        ulong Next() => Rng.Hash(seed, key, RngPurpose.Spawn, counter++);
        bool haveLeader = false;

        void Add(string recordId, SquadRole role, bool animal, float age, float spacing)
        {
            if (role == SquadRole.Leader) haveLeader = true;
            int i = members.Count;
            members.Add(new MemberPlan(recordId, role, i, Offset(i, spacing), animal, age));
        }

        bool Gated(string recordId) => db.Find(recordId) is { } r && r.GetReferences("world state").Any(w => !states.IsTrue(w.TargetStringId));

        // The squad itself can be gated by its own world state (section 4: the AND gate for the spawn).
        if (template.WorldState.Any(w => !states.IsTrue(w.Id)))
        {
            problems.Add($"squad '{template.Name}' is gated by a world state that does not hold");
            return new SquadPlan(template, [], [], problems);
        }

        if (template.Leader is { } leader)
        {
            if (db.Find(leader.Id) is not null) Add(leader.Id, SquadRole.Leader, false, 1, 3);
            else problems.Add($"Missing squad leader data reference for squad '{template.Name}'");
        }

        // choosefrom list: N weighted picks.
        int pickMin = template.NumRandomChars, pickMax = template.NumRandomCharsMax;
        int picks = pickMin >= pickMax ? pickMin : pickMin + (int)(Rng.Float(Next()) * (pickMax - pickMin + 1));
        for (int p = 0; p < picks && template.ChooseFrom.Count > 0; p++)
        {
            var chosen = WeightedPick(template.ChooseFrom, Gated, Next);
            if (chosen is null) break;
            Add(chosen, haveLeader ? SquadRole.Squad1 : SquadRole.Leader, false, 1, 3);
        }

        void Group(IReadOnlyList<RecordLink> list, SquadRole role, bool animals, float spacing, string what)
        {
            foreach (var entry in list)
            {
                var record = db.Find(entry.Id);
                if (record is null)
                {
                    problems.Add($"Missing squad {what} data reference ({entry.Id}) for squad '{template.Name}'");
                    continue;
                }
                int n = Count(entry.V0, entry.V1, multiplier, template.DontMultiply, Next());
                for (int k = 0; k < n; k++)
                {
                    float age = 1;
                    if (animals) age = Math.Max(0, Rng.Float(Next()) * Math.Max(0, record.GetFloat("animal age random")) - 0.1f + entry.V2 / 100f);
                    var memberRole = animals && !haveLeader && role == SquadRole.Squad1 ? SquadRole.Leader : role;
                    Add(entry.Id, memberRole, animals, age, spacing);
                }
            }
        }

        Group(template.Squad, SquadRole.Squad1, false, 3, "squad");
        Group(template.Squad2, SquadRole.Squad2, false, 3, "squad2");
        Group(template.Animals, SquadRole.Squad1, true, 3, "animals");
        Group(template.Animals2, SquadRole.Squad2, true, 5, "animals2");

        foreach (var slave in template.Slaves)
        {
            if (db.Find(slave.Id) is not { } record || !IsTemplate(record))
            {
                problems.Add($"Missing squad slaves data reference ({slave.Id}) for squad '{template.Name}'");
                continue;
            }
            slaves.Add(Plan(db, SquadTemplate.From(record), multiplier, seed, Rng.Mix(key ^ (ulong)slaves.Count + SeedSalts.SlaveSquad), states));
        }
        return new SquadPlan(template, members, slaves, problems);
    }

    /// <summary>The generic weighted pick (section 6.1): weight v0 with 0 meaning 100, gated entries and non-positive weights skipped; a single candidate needs no roll.</summary>
    public static string? WeightedPick(IReadOnlyList<RecordLink> list, Func<string, bool> gated, Func<ulong> nextRoll)
    {
        var candidates = new List<(string Id, int Weight)>();
        foreach (var e in list)
        {
            int w = e.V0 == 0 ? 100 : e.V0;
            if (w > 0 && !gated(e.Id)) candidates.Add((e.Id, w));
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Id;
        long total = candidates.Sum(c => (long)c.Weight);
        long pick = (long)(Rng.Float(nextRoll()) * total);
        foreach (var c in candidates)
        {
            if (pick < c.Weight) return c.Id;
            pick -= c.Weight;
        }
        return candidates[^1].Id;
    }
}
