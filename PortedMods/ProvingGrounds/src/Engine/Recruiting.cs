using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Population;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

// The recruit adapter (task 6.4a, CAP 15 "Recruit"): Kenshi's PlayerInterface::recruit(prisoner, false) (PrisonerRecruitment.cpp:50) and the
// prison mode it ends (:120), in Meitou. Meitou 0.17.3 (tag v0.17.3, fa34a1b); paths relative to its repo root. Meitou has no public recruit:
// its own recruits (the dialogue's DA_JOIN_SQUAD, DialogueSystem.Recruit.cs:22-43; the slaves given, :149-170; the bought animals,
// CharacterTrading.cs:428-449) check the faction cap (PlayerHeads against CONSTANTS max faction size) and call internal
// DialogueSystem.JoinPlayer; a caged one is let out of its cage first (GiveSlaves :157, CAP 15: JoinPlayer does not release). The two internal
// members are Members.JoinPlayer / Members.PlayerHeads (part Recruitment, checked by PgCheck hooks / engine / versions); without them the part is
// off (one log line) and nobody is recruited. Sim thread, serial steps only.
//
// The Kenshi calls and their Meitou counterparts (E1):
//   ou->player (PrisonerRecruitment.cpp:91, PrisonerRecruitmentUI.cpp:244) .. Ready(): the world's DialogueSystem (world.Systems, as
//                                         PgEngine.InDialogue) and a living player character to stand as JoinPlayer's talker (not read in 0.17.3)
//   ou->player->recruit(prisoner, false) (:50) .. Recruit(): the faction cap (PlayerHeads(world) < data.Bodies.Constants.MaxFactionSize, as
//                                         Recruit :30 / GiveSlaves :160); a caged prisoner released first (Cages.Release: FixtureRegistry.Release,
//                                         releaser -1, no crime, not escaped: the cage unlocked and emptied, the prisoner at its exit); then
//                                         DialogueSystem.JoinPlayer(world, slot, talker). A JoinPlayer that refuses (no active squad) puts the
//                                         prisoner back in its cage (Cages.PutInCage: Imprison locks it), so a failed recruit leaves a prisoner, as
//                                         Kenshi's failed recruit leaves one
//   prisoner->setPrisonMode(false, NULL) (:120) .. the release above (before the join: it must leave the cage, GiveSlaves' order)
//   prisoner->setChainedMode(false, hand) (:121-125) .. nothing (task 6.2's divergence: no chained flag in Meitou)
// The order and every outcome (a throw read back: a player = joined, else put back in its cage and that checked) are Core/Glue/RecruitingRules.cs
// (fake-tested); this class is the binding.
static class Recruiting
{
    static Func<DialogueSystem, World, int, CharacterCold, bool>? joinPlayer;
    static Func<World, int>? playerHeads;
    static bool bound, missingLogged;

    /// <summary>The by-name members of the part that did not resolve (empty: all present).</summary>
    internal static IEnumerable<string> Missing() => Members.Missing(Part.Recruitment);

    /// <summary>The part is on: both members resolved and bound (the first call binds them; a failure logs once and keeps the part off).</summary>
    internal static bool Available
    {
        get
        {
            if (!bound) Bind();
            return joinPlayer is not null && playerHeads is not null;
        }
    }

    static void Bind()
    {
        bound = true;
        try
        {
            if (Members.JoinPlayer is { } j && Members.PlayerHeads is { } p)
            {
                joinPlayer = j.CreateDelegate<Func<DialogueSystem, World, int, CharacterCold, bool>>();
                playerHeads = p.CreateDelegate<Func<World, int>>();
                return;
            }
            PartOff(string.Join(", ", Missing()) + " not found");
        }
        catch (Exception e)
        {
            joinPlayer = null;
            playerHeads = null;
            PartOff($"{e.GetType().Name}: {e.Message}");
        }
    }

    static void PartOff(string why)
    {
        if (missingLogged) return;
        missingLogged = true;
        string line = $"recruitment off: {why}; prisoners cannot be recruited (the Grant Freedom button reads Recruitment Unavailable where it would be enabled)";
        ProvingGroundsMod.Ctx?.Log(line);
        ProvingGrounds.Core.PGLog.Debug("Proving Grounds: " + line);
    }

    /// <summary>The world's dialogue system (it owns JoinPlayer), or null.</summary>
    static DialogueSystem? Dialogue(World world) => world.Systems.OfType<DialogueSystem>().FirstOrDefault();

    /// <summary>JoinPlayer's talker: the first living player character in table order (the slot in use, IsPlayer, never died, not dead: the
    /// count PlayerHeads makes, DialogueSystem.Recruit.cs:48-55), or null. One pass over the table, no list made (Ready reads it each refresh).</summary>
    static CharacterCold? Talker(World world)
    {
        var t = world.Characters;
        var next = t.Next;
        for (int i = 0; i < next.Length; i++)
            if (next[i].Alive && t.Cold(i) is { IsPlayer: true, DiedTick: < 0, Medical: null or { Dead: false } } c) return c;   // Medical is nullable (CharacterTable.cs:384): none = not dead
        return null;
    }

    /// <summary>C++ <c>ou &amp;&amp; ou-&gt;player</c>: the player side a recruit joins (the world's DialogueSystem and a living player character).</summary>
    internal static bool Ready(World world) => Dialogue(world) is not null && Talker(world) is not null;

    /// <summary>C++ <c>ou-&gt;player-&gt;recruit(prisoner, false)</c> (PrisonerRecruitment.cpp:50) and the prison mode it ends (:120): see the header.
    /// Serial step only.</summary>
    internal static RecruitOutcome Recruit(World world, PopulationData data, CharacterId prisoner) =>
        RecruitingRules.Recruit(new Host(world, data, prisoner));

    /// <summary>IRecruitingHost for one recruit (this world, this prisoner, the cage it was found in).</summary>
    sealed class Host(World world, PopulationData data, CharacterId prisoner) : IRecruitingHost
    {
        DialogueSystem? dialogue;
        CharacterCold? talker, cold;
        int slot;
        Fixture? cage;

        public bool PartAvailable() => Available;
        public bool HasDialogue() => (dialogue = Dialogue(world)) is not null;
        public bool HasTalker() => (talker = Talker(world)) is not null;
        public bool Resolves() => world.Characters.TryResolveNext(prisoner, out slot) && (cold = world.Characters.Cold(slot)) is not null;
        public int PlayerHeads() => playerHeads!(world);
        public int MaxFactionSize() => data.Bodies.Constants.MaxFactionSize;
        public bool IsImprisoned() => cold!.IsImprisoned;
        public bool RememberCage() => (cage = cold!.InWhat is { } held ? world.Fixtures.Find(held) : null) is not null;
        public bool Release() => Cages.Release(world, prisoner);
        public bool JoinPlayer() => joinPlayer!(dialogue!, world, slot, talker!);
        public bool IsPlayer() => cold!.IsPlayer;
        public bool PutBack() => cage is not null && Cages.PutInCage(world, prisoner, cage);
        public void Log(string line)
        {
            ProvingGroundsMod.Ctx?.Log(line);
            ProvingGrounds.Core.PGLog.Debug("Proving Grounds: " + line);
        }
    }
}
