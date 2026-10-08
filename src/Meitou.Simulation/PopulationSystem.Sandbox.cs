using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation;

public sealed partial class PopulationSystem
{
    /// <summary>
    /// The animation sandbox's spawn (the game host's <c>--sandbox</c>): one character made from <paramref name="recordId"/> at a place, alone in a squad of its own, standing idle.
    /// <paramref name="player"/> makes it one of the player's (the first call also sets the player faction, as <see cref="StartPlayer"/> does); otherwise it belongs to the
    /// first faction that is hostile to the player's. Before the first tick; nothing in the normal game calls it.
    /// </summary>
    public CharacterId SpawnSandbox(World world, string recordId, Vector2 at, bool player, float yaw = 0)
    {
        if (data.PlayerFaction < 0) throw new InvalidOperationException("the game data has no player faction (Nameless)");
        int faction = data.PlayerFaction;
        if (!player)
        {
            for (int f = 0; f < data.Factions.Count; f++)
                if (f != data.PlayerFaction && data.Relations.Get(f, data.PlayerFaction) <= FactionRelations.HostileThreshold) { faction = f; break; }
            if (faction == data.PlayerFaction) faction = data.Factions.Select((f, i) => (f, i)).FirstOrDefault(p => p.i != data.PlayerFaction).i;
        }
        var factionId = data.Factions[faction].Id;
        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(recordId) ^ (player ? 1UL : 2UL));
        CharacterAppearance? look = data.Appearances?.Create(recordId, factionId, (int)(Rng.Hash(world.Seed, key, RngPurpose.Spawn, SeedSalts.PlayerLook) & 0x7FFFFFFF));
        var member = new MemberPlan(recordId, SquadRole.Leader, 0, default, false, 1);
        var template = new SquadTemplate { Id = "sandbox " + recordId, Name = data.Db.Find(recordId)?.Name ?? recordId };
        var plan = new SquadPlan(template, [member], [], []);
        var built = new BuiltSquad(plan, at, faction, [new BuiltMember(member, look)]);
        var squad = SpawnSquad(world, built, null, 100, player, idle: true, yaw: yaw);
        var id = squad.Leader;
        if (player)
        {
            world.Player.Faction = data.PlayerFaction;
            world.Player.Squad = squad.Id;
            world.Player.Selection.Clear();
        }
        return id;
    }
}
