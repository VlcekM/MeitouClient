using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// Fogman worship (237 / 236) and the dogs' limb play (277-279, from the races' AI Goals): docs/game/ai-tasks.md "Prisoner fate and slave rows",
/// docs/simulation.md "Captives".
/// </summary>
public sealed partial class CaptiveSystem
{
    /// <summary>
    /// How near a worshipper bows (<c>Task_WorshipTarget</c>, <b>Verified</b>), the range of its bark timer in game seconds, and event 53 (worship).
    /// </summary>
    public const float WorshipReach = 60, WorshipMin = 3, WorshipMax = 14;
    /// <summary>
    /// Event 53, the worshipper's bark (see <see cref="WorshipReach"/>).
    /// </summary>
    const int WorshipEvent = 53;
    /// <summary>
    /// The clip a worshipper bows with.
    /// </summary>
    public const string BowClip = "bowing";
    /// <summary>
    /// The limb fetch's reach (<c>AI::findLimbOnGround</c>, squared 40000), and the play run's legs (<c>Task_DogLimbRunningPlay</c>): 1000 + 400 x
    /// rand, a new one under 200 left.
    /// </summary>
    public const float FetchRange = 200, PlayLeg = 1000, PlayLegRandom = 400, PlayTurn = 200;
    /// <summary>
    /// The salt of the animals' play rolls.
    /// </summary>
    const ulong AnimalSalt = 0x416E_696D_616C_506C;

    /// <summary>
    /// <c>AI::findFogmanWorshipTarget</c> (<c>FUN_1408ce880</c>): in the town nearest the worshipper, the first cage (list order) whose occupant is
    /// being eaten. Null: none. A standing nest counts as a town (the original's town list holds the nests), its cages those of its buildings
    /// (<see cref="NestBuildings"/>); the nest markers do not.
    /// </summary>
    Fixture? WorshipTarget(World world, int slot)
    {
        var at = world.Characters.Next[slot].Position;
        var here = new Vector2(at.X, at.Z);
        int site = -1, nest = -1;
        float best = float.MaxValue;
        for (int s = 0; s < data.Sites.Count; s++)
        {
            if (data.Sites[s].Town.Type == Data.Gameplay.TownType.NestMarker) continue;   // a nest stands on it
            float d = Vector2.DistanceSquared(here, new Vector2(data.Sites[s].Position.X, data.Sites[s].Position.Z));
            if (d < best) (best, site) = (d, s);
        }

        foreach (var (n, centre) in world.NestBuildings.Standing)
        {
            float d = Vector2.DistanceSquared(here, centre);
            if (d < best) (best, nest) = (d, n);
        }

        var cages = nest >= 0 ? NestCages(world, nest) : site >= 0 ? CagesOf(world, site) : [];
        foreach (var cage in cages)
        {
            if (OccupantSlot(world, cage) >= 0
                    && _jobs.Any(j => j.Kind == CaptiveJobKind.Eat && j.Captive == cage.Occupant && j.Phase == CaptivePhase.Work))
                return cage;
        }

        return null;
    }

    /// <summary>
    /// The cages of the buildings a nest stood up.
    /// </summary>
    static IEnumerable<Fixture> NestCages(World world, int nest) =>
        world.NestBuildings.Of(nest).SelectMany(p => world.Fixtures.OfBuilding(p.Id)).Where(f => f.Kind == FixtureKind.Cage);

    /// <summary>
    /// <c>Task_WorshipTarget</c> (236 on 237's target): farther than <see cref="WorshipReach"/> from the cage it walks there; within, it stops, faces
    /// it, bows, and every 3-14 game seconds barks event 53 (with no target: the cage resolves to no character). The original never ends it while the
    /// cage stands; Meitou ends it once nobody is held there or the job grows old.
    /// </summary>
    bool StepWorship(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        var table = world.Characters;
        if (world.Fixtures.Find(j.Cage) is not { } cage || OccupantSlot(world, cage) < 0) return End(world, j, null);
        ref var n = ref table.Next[w];
        var to = new Vector2(cage.Position.X, cage.Position.Z);
        if (Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), to) > WorshipReach)
        {
            wc.ActionClip = null;
            if (n.Task != (byte)CharacterTask.GoTo || Vector2.Distance(n.Goal, to) > WorshipReach)
                TaskStart.Walk(world, w, to, SpeedMode.Walk, RowOf(j.Kind), TaskSource.Goal);

            return true;
        }

        if (n.Task == (byte)CharacterTask.GoTo) TaskStart.Stand(table, w);
        var face = cage.Position - n.Position;
        if (face.X * face.X + face.Z * face.Z > 1e-4f) n.Yaw = MathF.Atan2(face.X, face.Z);
        wc.ActionClip = BowClip;
        if (j.Time >= j.NextBark)
        {
            j.NextBark = j.Time + WorshipMin
                    + (WorshipMax
                            - WorshipMin) * Rng.Float(Rng.Hash(world.Seed, (ulong)Rng.Key(j.Worker), RngPurpose.Slow, AnimalSalt + (ulong)j.Blows++));
            if (j.Blows > 1) world.Defer(new Dialogue.DialogueEvent(j.Worker, CharacterId.None, WorshipEvent) { Tick = world.Tick + 1 });
        }

        return true;
    }

    /// <summary>
    /// 277 Fetch (<c>AI::findLimbOnGround</c> <c>FUN_1409993a0</c>): an animal with nothing in its mouth (an adult only while its stomach is empty
    /// and its hunger at most 2.75) goes for the nearest severed limb on the ground within <see cref="FetchRange"/> and picks it up into its mouth
    /// (<see cref="Items.FeedSystem.HoldInMouth"/>, through the pick-up walk). No job.
    /// </summary>
    static void Fetch(World world, int slot, CharacterCold cold)
    {
        if (LimbFor(world, slot, cold) is { } best) Fetch(world, slot, cold, best);
    }

    /// <summary>
    /// Has the animal pick up <paramref name="limb"/>, running to it first when it is out of reach.
    /// </summary>
    static void Fetch(World world, int slot, CharacterCold cold, Items.GroundItem limb)
    {
        cold.PickingUpItem = limb.Id;
        if (Vector3.Distance(world.Characters.Next[slot].Position, limb.Position) > Items.PickUps.PickUpReach)
            TaskStart.Walk(world, slot, limb.Position, SpeedMode.Run, TaskType.AnimalFetchALimb, TaskSource.Goal);
    }

    /// <summary>
    /// 277's finder (a pure read): the limb <see cref="Fetch"/> goes for, null when none or the animal does not want one.
    /// </summary>
    static Items.GroundItem? LimbFor(World world, int slot, CharacterCold cold)
    {
        if (cold.Mouth is not null || cold.PickingUpItem is not null || cold.Medical is not { } med) return null;
        if (cold.Age > 0.9f && (med.Fed > 0 || med.Hunger > 2.75f)) return null;
        var at = world.Characters.Next[slot].Position;
        Items.GroundItem? best = null;
        float d2Best = FetchRange * FetchRange;
        foreach (var g in world.GroundItems.All)
        {
            if (g.Item.ItemFunction != Items.FeedSystem.LimbFunction || g.Slot is not null) continue;
            float d2 = Vector3.DistanceSquared(at, g.Position);
            if (d2 < d2Best) (best, d2Best) = (g, d2);
        }

        return best;
    }

    /// <summary>
    /// 278's desire (<c>AI::scoreDogPlaying</c> <c>FUN_140599720</c>, <b>Verified</b> by decompile): an animal with something in its mouth, 1.1, or
    /// 0.3 for one of the player's squads over age 0.4; 0 for the player's adults (over 0.95). Any desire above 0 plays (it beats IDLE's 0.1).
    /// </summary>
    static float PlayDesire(World world, CharacterCold c) =>
        c.Mouth is null || PlayerSquad(world, c) && c.Age > 0.95f ? 0 : PlayerSquad(world, c) && c.Age > 0.4f ? 0.3f : 1.1f;

    /// <summary>
    /// Whether 278's desire is above 0 (<see cref="PlayDesire"/>).
    /// </summary>
    static bool Plays(World world, CharacterCold c) => PlayDesire(world, c) > 0;

    /// <summary>
    /// 279's desire (<c>AI::scoreDogPlayingChase</c>): 1.2 for an NPC animal; of the player's squads 0 for an adult (over 0.95), 0.3 over 0.2, 0.6
    /// younger.
    /// </summary>
    static float ChaseDesire(World world, CharacterCold c) => !PlayerSquad(world, c) ? 1.2f : c.Age > 0.95f ? 0 : c.Age > 0.2f ? 0.3f : 0.6f;

    /// <summary>
    /// Whether 279's desire is above 0 (<see cref="ChaseDesire"/>).
    /// </summary>
    static bool Chases(World world, CharacterCold c) => ChaseDesire(world, c) > 0;

    /// <summary>
    /// Whether a character is of the player's side: a player character or of the player's faction.
    /// </summary>
    static bool PlayerSquad(World world, CharacterCold c) => c.IsPlayer || world.Player.Faction >= 0 && c.Faction == world.Player.Faction;

    /// <summary>
    /// <c>AI::findAllyAnimalWithFoodInMouth</c> (<c>FUN_14099ad40</c>): the first allied animal within sight, awake, with something in its mouth
    /// (slot order). -1: none.
    /// </summary>
    int MouthfulAlly(World world, int slot, CharacterCold cold)
    {
        var table = world.Characters;
        var at = table.Next[slot].Position;
        float range = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight);
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == slot || !table.Next[s].Alive || table.Cold(s) is not { Mouth: not null, Medical.Incapacitated: false } c
                    || c.Fighter is not { IsAnimal: true } || !Allied(cold.Faction, c.Faction))
                continue;

            if (Vector3.DistanceSquared(at, table.Next[s].Position) <= range * range) return s;
        }

        return -1;
    }

    /// <summary>
    /// <c>Task_DogLimbRunningPlay</c> (278): while it holds the limb the dog sprints in random legs of 1000-1400, a new one when under 200 is left or
    /// it stopped. Over when its mouth is empty (A !149) or the job grows old. Engine choice: the first leg too is random (the original runs it
    /// 80-180 past the centroid of who it senses, away from them).
    /// </summary>
    bool StepPlay(World world, CaptiveJob j, int w, CharacterCold wc)
    {
        if (wc.Mouth is null) return End(world, j, null);
        var table = world.Characters;
        ref var n = ref table.Next[w];
        bool walking = n.Task == (byte)CharacterTask.GoTo;
        if (walking && Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), n.Goal) >= PlayTurn) return true;
        ulong h = Rng.Hash(world.Seed, (ulong)Rng.Key(j.Worker), RngPurpose.Slow, AnimalSalt ^ (ulong)j.Walks++ << 8);
        var dir = new Vector2(Rng.Float(h) * 2 - 1, Rng.Float(h ^ 0x9E3779B97F4A7C15) * 2 - 1);
        dir = dir.LengthSquared() > 1e-6f ? Vector2.Normalize(dir) : Vector2.UnitX;
        float leg = PlayLeg + PlayLegRandom * Rng.Float(h ^ 0x5851F42D4C957F2D);
        TaskStart.Walk(world, w, new Vector2(n.Position.X, n.Position.Z) + dir * leg, SpeedMode.Run, RowOf(j.Kind), TaskSource.Goal);
        return true;
    }

    /// <summary>
    /// 279 Limb Jealousy (44 / 253 / 40 Following to its target): the dog runs after the mouthful ally until near it (10); over when the ally's mouth
    /// is empty or the job grows old.
    /// </summary>
    bool StepChase(World world, CaptiveJob j, int w)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(j.Captive, out int t) || table.Cold(t) is not { Mouth: not null })
            return End(world, j, null);

        ref var n = ref table.Next[w];
        var to = new Vector2(table.Next[t].Position.X, table.Next[t].Position.Z);
        if (Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), to) > 10
                && (n.Task != (byte)CharacterTask.GoTo || Vector2.Distance(n.Goal, to) > 20))
            TaskStart.Walk(world, w, to, SpeedMode.Run, RowOf(j.Kind), TaskSource.Goal);

        return true;
    }
}