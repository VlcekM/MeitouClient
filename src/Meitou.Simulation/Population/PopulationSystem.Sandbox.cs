using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

public sealed partial class PopulationSystem
{
    /// <summary>Characters the F10 debug menu has spawned (<see cref="DebugSpawn"/>): each one's look variant.</summary>
    int debugSpawns;

    /// <summary>
    /// The animation sandbox's spawn (the game host's <c>--sandbox</c>): one character made from <paramref name="recordId"/> at a place, alone in a squad of its own, standing idle.
    /// <paramref name="player"/> makes it one of the player's (the first call also sets the player faction and the factions it knows, as <see cref="StartPlayer"/> does); otherwise it belongs to the
    /// first faction that is hostile to the player's. A <paramref name="variant"/> above 0 is a bystander with a look of its own: of the faction <paramref name="player"/> picks,
    /// in a squad of its own that is not the player's. <paramref name="faction"/> (a position in the factions) overrides both choices: the game's <c>--duel</c> makes two NPCs of one faction.
    /// Before the first tick or in the serial Inputs phase; only the sandbox, the game's <c>--crowd</c> test, its <c>--duel</c> and the F10 debug menu call it.
    /// </summary>
    /// <summary>The look <see cref="SpawnSandbox"/> gives a character (the same arguments, <paramref name="faction"/> the one it ends up with): the duel picks variants that wear a helmet.</summary>
    public CharacterAppearance? SandboxLook(ulong seed, string recordId, bool player, int variant, int faction)
    {
        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(recordId) ^ (player ? 1UL : 2UL) ^ ((ulong)variant << 8));
        return data.Appearances?.Create(recordId, data.Factions[faction].Id, (int)(Rng.Hash(seed, key, RngPurpose.Spawn, SeedSalts.PlayerLook) & 0x7FFFFFFF));
    }

    public CharacterId SpawnSandbox(World world, string recordId, Vector2 at, bool player, float yaw = 0, int variant = 0, int? faction = null)
    {
        int chosen = SandboxFaction(player, faction);
        // An animal (ANIMAL_CHARACTER) has its species' shared look (nothing rolled); it is an adult (age 1).
        bool animal = data.Db.Find(recordId) is { Type: Meitou.Data.Fcs.FcsRecordType.ANIMAL_CHARACTER };
        CharacterAppearance? look = SandboxLook(world.Seed, recordId, player, variant, chosen);
        var member = new MemberPlan(recordId, SquadRole.Leader, 0, default, animal, 1);
        var template = new SquadTemplate { Id = "sandbox " + recordId, Name = data.Db.Find(recordId)?.Name ?? recordId };
        var plan = new SquadPlan(template, [member], [], []);
        var built = new BuiltSquad(plan, at, chosen, [new BuiltMember(member, look)]);
        bool playerSquad = player && variant == 0 && faction is null;
        var squad = SpawnSquad(world, built, null, 100, playerSquad, idle: true, yaw: yaw);
        var id = squad.Leader;
        if (playerSquad)
        {
            world.Player.Faction = data.PlayerFaction;
            world.Player.KnownFactions = data.KnownAtNewGame();
            world.Player.Squad = squad.Id;
            world.Player.Selection.Clear();
        }
        return id;
    }

    /// <summary><see cref="SpawnSandbox"/>'s faction: <paramref name="faction"/>, else the player's, else (an NPC) the first faction hostile to the player's.</summary>
    int SandboxFaction(bool player, int? faction)
    {
        if (data.PlayerFaction < 0) throw new InvalidOperationException("the game data has no player faction (Nameless)");
        int chosen = faction ?? data.PlayerFaction;
        if (!player && faction is null)
        {
            for (int f = 0; f < data.Factions.Count; f++)
                if (f != data.PlayerFaction && data.Relations.Get(f, data.PlayerFaction) <= FactionRelations.HostileThreshold) { chosen = f; break; }
            if (chosen == data.PlayerFaction) chosen = data.Factions.Select((f, i) => (f, i)).FirstOrDefault(p => p.i != data.PlayerFaction).i;
        }
        return chosen;
    }

    /// <summary>
    /// The game's <c>--battle</c> side: one NPC squad of characters made from <paramref name="recordId"/>, one at <paramref name="at"/> plus each of <paramref name="offsets"/>
    /// (the first leads), standing idle facing <paramref name="yaw"/>; member i has the look <see cref="SpawnSandbox"/> gives <paramref name="firstVariant"/> + i, the faction
    /// as it picks one for an NPC. Before the first tick. Returns the members in order.
    /// </summary>
    public IReadOnlyList<CharacterId> SpawnSandboxSquad(World world, string recordId, Vector2 at, IReadOnlyList<Vector2> offsets, float yaw, int firstVariant, int? faction)
    {
        int chosen = SandboxFaction(false, faction);
        bool animal = data.Db.Find(recordId) is { Type: Meitou.Data.Fcs.FcsRecordType.ANIMAL_CHARACTER };
        var members = new MemberPlan[offsets.Count];
        var built = new BuiltMember[offsets.Count];
        for (int i = 0; i < offsets.Count; i++)
        {
            members[i] = new MemberPlan(recordId, i == 0 ? SquadRole.Leader : SquadRole.Squad1, i, offsets[i], animal, 1);
            built[i] = new BuiltMember(members[i], SandboxLook(world.Seed, recordId, false, firstVariant + i, chosen));
        }
        var template = new SquadTemplate { Id = "sandbox " + recordId, Name = data.Db.Find(recordId)?.Name ?? recordId };
        var squad = SpawnSquad(world, new BuiltSquad(new SquadPlan(template, members, [], []), at, chosen, [.. built]), null, 100, idle: true, yaw: yaw);
        return squad.Members;
    }
}
