using System.Numerics;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Dialogue;

/// <summary>
/// The biome banner has faded for <see cref="GroupId"/> (a BIOME_GROUP), or the host's 10 s retry while the squad stays in the group's area: the
/// active squad may banter about the area (<see cref="DialogueSystem"/>, "arrival dialog"). <see cref="Serial"/> numbers the try so the host can read
/// its result (<see cref="DialogueSystem.ArrivalResult"/>).
/// </summary>
public sealed record AreaArrival(string GroupId, int Serial = 0) : SimCommand;

public sealed partial class DialogueSystem
{
    int _arrivalResult;

    /// <summary>
    /// <c>PlayerInterface::triggerAreaArrivalDialogue</c> (<c>FUN_1407ffe40</c>, docs/game/dialogue.md "Arrival banter"): with the active squad of at
    /// least 2, its members awake, not carried, not in a bed or cage, not animals, not fighting and not in a town are the candidates; nothing when a
    /// member senses an enemy; with 2 or more, a line of the group's <c>arrival dialog</c> is said between two of them. Engine: the original scores
    /// every candidate with every dialogue (<c>scoreCustomDialog</c>) and runs the best; here the first dialogue, speaker and partner (squad order)
    /// that have a line win, "on screen" is not tested, and an enemy sensed is one hostile awake within sight range.
    /// </summary>
    void Arrive(World world, AreaArrival a)
    {
        bool forget = TryArrive(world, a);
        Volatile.Write(ref _arrivalResult, a.Serial * 2 + (forget ? 1 : 0));
    }

    /// <summary>
    /// The result of the last arrival try: its <see cref="AreaArrival.Serial"/> and whether the host forgets the group (<c>FUN_140800eb0</c>: a
    /// non-zero result of the banter, squad of under 2 or a line started; an enemy sensed, under 2 candidates or no pair with a line keep the 10 s
    /// retry going).
    /// </summary>
    public (int Serial, bool Forget) ArrivalResult
    {
        get
        {
            int r = Volatile.Read(ref _arrivalResult);
            return (r >> 1, (r & 1) != 0);
        }
    }

    /// <summary>
    /// One try of <see cref="Arrive"/>; true: forget the group.
    /// </summary>
    bool TryArrive(World world, AreaArrival a)
    {
        var table = world.Characters;
        if (world.Squads.Find(world.Player.Squad) is not { Members.Count: >= 2 } squad) return true;
        if (_data.Db.Find(a.GroupId) is not { } group) return false;
        var roots = group.GetReferences("arrival dialog")
            .Select(r => _dialogue.Node(r.TargetStringId))
            .Where(n => n is { IsRoot: true })
            .ToList();
        if (roots.Count == 0) return false;
        var candidates = new List<CharacterId>();
        foreach (var id in squad.Members)
        {
            if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c) continue;

            // Only a member that is up, free, not an animal and out of melee counts for the enemy test, which aborts the whole arrival; the town
            // filter comes after it (FUN_1407ffe40 :100-121).
            if (!Awake(table, s) || !c.CarriedBy.IsNone || c.InSomething != Locks.HeldState.None
                || SquadRegistry.IsAnimal(c) || c.InCombat) continue;
            if (SensesEnemy(world, s, c)) return false;
            if (_data.TownAt(table.Next[s].Position) >= 0) continue;
            candidates.Add(id);
        }

        if (candidates.Count < 2) return false;
        foreach (var root in roots)
        {
            foreach (var speaker in candidates)
            {
                foreach (var partner in candidates)
                {
                    if (partner == speaker) continue;
                    if (MaySpeak(world, root!, speaker) && StartBubbles(world, speaker, partner, root!, 0)) return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="speaker"/> may start <paramref name="root"/> (<c>FUN_14067e060</c>, <c>FUN_140684d70</c>, docs/game/dialogue.md
    /// "Arrival banter"): a character whose record says <c>dumb</c> only when the dialogue's <c>is character</c> names it (an empty list fails),
    /// another when the list is empty or names it.
    /// </summary>
    bool MaySpeak(World world, DialogueNode root, CharacterId speaker)
    {
        if (Cold(world, speaker) is not { } c) return false;
        return ListsSpeaker(root, c.RecordId);
    }

    /// <summary>
    /// The original's <c>numEnemies</c> &gt; 0: the character's senses count an enemy (<see cref="CharacterFacts.Senses"/>).
    /// </summary>
    static bool SensesEnemy(World world, int slot, CharacterCold c) => CharacterFacts.Senses(world, slot) is { Enemies: > 0 };
}
