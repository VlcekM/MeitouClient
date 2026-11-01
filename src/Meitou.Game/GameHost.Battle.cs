using System.Numerics;
using Meitou.Rendering;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Game;

/// <summary>
/// The battle (<c>--battle n unit m unit</c>, docs/simulation.md "Battle"): two groups of NPCs, each of one CHARACTER or ANIMAL_CHARACTER record, in two blocks facing
/// each other on the map (the plateau, the duel's mesa or x,z) or the sandbox's floor. No towns and no aggro: the host gives every standing fighter without a target the nearest
/// standing enemy. Backspace starts the same fight again (a new world made the same way). Nothing here runs in the normal game.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The two fronts start this far apart along Z, each block in ranks of up to <see cref="BattleRankWidth"/> fighters <see cref="BattleSpacing"/> apart (more for bigger races).</summary>
    const float BattleGap = 40, BattleSpacing = 6;
    const int BattleRankWidth = 25;
    /// <summary>No orders before this tick: the blocks stand a moment before they charge.</summary>
    const int BattleOrderTick = 30;

    /// <summary>The default spot (the owner's pick, 2026-10-23): a plateau about 1380 high, from camera code 01C69590C7EAFBAB44A4CE5146EAC4B2BFD203D43E755F7D44.</summary>
    public static readonly Vector2 BattleSpot = new(-74028, 13428);
    /// <summary>The battle's starting hour (without <c>--time</c>).</summary>
    public const float BattleHour = 15;

    /// <summary><c>--battle-map fog</c>: a spot in the Fog Islands picked in the game view, about 460 units from the plateau first used (-71170, -40200: about 880 high, level to 13 units over 800 x 800, in its fog volumes, 10k from any building).</summary>
    public static readonly Vector2 FogBattleSpot = new(-71560, -39953);

    bool IsBattle => g.Battle is not null;
    /// <summary>The floor is the sandbox's: the animation sandbox's, or the battle's with <c>--battle-map sandbox</c>.</summary>
    bool IsFlat => IsSandbox || IsBattle && g.BattleFlat;

    Vector3 battleSpot;
    readonly List<(string Record, string Name, int Count, int? Faction)> battleSides = [];
    readonly List<CharacterId>[] battleFighters = [[], []];
    long battleOrderedAt = -1;
    bool battleRestartRequested, battleOver;

    /// <summary>Finds the two records and their factions and fills the session's world; returns where the camera starts.</summary>
    Vector3 BattleStart()
    {
        var db = scene.Database!;
        foreach (var (count, unit) in g.Battle!)
        {
            var record = FindCharacter(db, unit) ?? throw new ArgumentException($"--battle: no CHARACTER or ANIMAL_CHARACTER named '{unit}'");
            // The record's own faction for its looks (an animal has none: the first faction hostile to the player's).
            string? factionId = record.GetReferences("faction").Select(r => r.TargetStringId).FirstOrDefault();
            int faction = popData!.Factions.Select((f, i) => (f, i)).FirstOrDefault(p => p.f.Id == factionId, (null!, -1)).Item2;
            battleSides.Add((record.StringId, record.Name, count, faction >= 0 ? faction : null));
        }
        var spot = g.BattleFlat ? Vector2.Zero : g.BattleAt is { } at ? new Vector2(at.X, at.Z) : BattleSpot;
        battleSpot = new Vector3(spot.X, session.World.Walkability.GroundHeight(spot.X, spot.Y), spot.Y);
        BattleSetup(session.World, population!);
        session.Ticked += _ => BattleTick();
        var wet = battleFighters.SelectMany(f => f).Count(id => !simWalkability!.IsWalkable(session.World.Characters.Previous[id.Slot].Position.X, session.World.Characters.Previous[id.Slot].Position.Z));
        if (wet > 0) Console.Error.WriteLine($"battle    {wet} fighter(s) start under water: pick another spot with --battle-map x,z");
        Console.WriteLine($"battle    {BattleSide(0)} vs {BattleSide(1)} at {spot.X:0}, {spot.Y:0}{(g.BattleFlat ? " (sandbox floor)" : "")}; Backspace restarts");
        return battleSpot;
    }

    string BattleSide(int side) => $"{battleSides[side].Count} x {battleSides[side].Name}";

    /// <summary>Fills a new world with the two blocks, a squad each (its first fighter leads): side 0 south (−Z) facing +Z, side 1 north facing −Z, each centred on the spot's X.</summary>
    void BattleSetup(World world, PopulationSystem population)
    {
        for (int side = 0; side < 2; side++)
        {
            var (record, _, count, faction) = battleSides[side];
            var fighters = battleFighters[side];
            fighters.Clear();
            float sign = side == 0 ? -1 : 1;
            // Bigger bodies stand further apart: BattleSpacing is a human's (footprint 4), scaled by the race's footprint, never closer; the front rank at least that far off the middle.
            var db = scene.Database!;
            var race = db.Find(record)?.GetReferences("race").FirstOrDefault(r => r.Values.Value0 > 0) is { TargetStringId: { } raceId } ? db.Find(raceId) : null;
            float footprint = race?.GetFloat("pathfind footprint radius", CombatTuning.DefaultFootprint) ?? CombatTuning.DefaultFootprint;
            float spacing = Math.Max(BattleSpacing, BattleSpacing * footprint / CombatTuning.DefaultFootprint), front = Math.Max(BattleGap / 2, spacing);
            // --battle-width: rows of that many; else --battle's rows: that many rows of count / rows, the back row also taking the rest; otherwise rows of BattleRankWidth.
            int rows = g.BattleWidth[side] is null && g.BattleRows is { } r ? Math.Min(r, count) : 0, width = g.BattleWidth[side] ?? (rows > 0 ? count / rows : BattleRankWidth);
            var offsets = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int rank = rows > 0 ? Math.Min(i / width, rows - 1) : i / width;
                int first = rank * width, inRank = rows > 0 && rank == rows - 1 ? count - first : Math.Min(width, count - first);
                offsets[i] = new Vector2((i - first - (inRank - 1) / 2f) * spacing, sign * (front + rank * spacing));
            }
            // Facing +Z is yaw 0; a look of its own per fighter.
            fighters.AddRange(population.SpawnSandboxSquad(world, record, new Vector2(battleSpot.X, battleSpot.Z), offsets, side == 0 ? 0 : MathF.PI, side * 1000 + 1, faction));
        }
        battleOrderedAt = -1;
        battleOver = false;
    }

    /// <summary>
    /// Each control tick, once per world tick: a standing fighter without a target is ordered at the nearest standing enemy (one who wakes up joins in again).
    /// The first tick a side has nobody standing the battle is over and the orders stop.
    /// </summary>
    void BattleTick()
    {
        var world = session.World;
        if (battleOver || world.Tick < BattleOrderTick || world.Tick == battleOrderedAt) return;
        battleOrderedAt = world.Tick;
        // The world and its combat system are new after each restart.
        if (world.Systems.OfType<CombatSystem>().FirstOrDefault() is not { } combat) return;
        var table = world.Characters;
        bool Standing(CharacterId id) => table.TryResolveNext(id, out int slot) && table.Cold(slot)?.Medical is { Incapacitated: false };
        Vector3 At(CharacterId id) => table.Previous[id.Slot].Position;
        var standing = battleFighters.Select(f => f.Where(Standing).ToList()).ToArray();
        if (standing[0].Count == 0 || standing[1].Count == 0)
        {
            battleOver = true;
            int winner = standing[0].Count > 0 ? 0 : standing[1].Count > 0 ? 1 : -1;
            string result = winner < 0 ? "nobody left standing" : $"{battleSides[winner].Name} win, {standing[winner].Count} of {battleSides[winner].Count} standing";
            Console.WriteLine($"battle    {BattleSide(0)} vs {BattleSide(1)}: {result} after {world.Tick * world.TickSeconds:0.0} s");
            player.Notice($"Battle over: {result} (Backspace restarts)");
            return;
        }
        for (int side = 0; side < 2; side++)
            foreach (var id in standing[side])
            {
                if (combat.StateOf(id).TargetSlot >= 0) continue;
                var at = At(id);
                var foe = standing[1 - side].MinBy(e => Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(At(e).X, At(e).Z)));
                world.Commands.Enqueue(new AttackOrder([id], foe) { Tick = world.Tick });
            }
        if (world.Tick % BattleRetargetTicks == 0) BattleRetarget(world, combat, standing);
    }

    /// <summary>How often a fighter left out of a crowded target looks for another (engine choice: a second).</summary>
    const int BattleRetargetTicks = 30;

    /// <summary>
    /// A fighter its target left out (more than <see cref="CombatSystem.MaxEngaged"/> fighters after it, so it circles) turns on the nearest standing enemy with room
    /// for it, if any: otherwise every fighter keeps the enemy nearest at its order and the crowd round a few targets circles them in rings while others stand free.
    /// </summary>
    void BattleRetarget(World world, CombatSystem combat, List<CharacterId>[] standing)
    {
        var table = world.Characters;
        var at = new Dictionary<int, int>();   // the melee fighters after each slot
        foreach (var side in standing)
            foreach (var id in side)
                if (combat.StateOf(id) is { TargetSlot: >= 0 and var t, Down: false, Ranged: false }) at[t] = at.GetValueOrDefault(t) + 1;
        Vector2 Ground(CharacterId id) => new(table.Previous[id.Slot].Position.X, table.Previous[id.Slot].Position.Z);
        for (int side = 0; side < 2; side++)
            foreach (var id in standing[side])
            {
                var st = combat.StateOf(id);
                if (st.TargetSlot < 0 || st.Engaged || st.Ranged || st.AttackTech >= 0 ||at.GetValueOrDefault(st.TargetSlot) <= CombatSystem.MaxEngaged) continue;
                var here = Ground(id);
                CharacterId? best = null;
                float bestDistance = float.MaxValue;
                foreach (var e in standing[1 - side])
                {
                    if (e.Slot == st.TargetSlot || at.GetValueOrDefault(e.Slot) >= CombatSystem.MaxEngaged) continue;
                    float distance = Vector2.DistanceSquared(here, Ground(e));
                    if (distance < bestDistance) { bestDistance = distance; best = e; }
                }
                if (best is not { } foe) continue;
                at[st.TargetSlot]--;
                at[foe.Slot] = at.GetValueOrDefault(foe.Slot) + 1;
                world.Commands.Enqueue(new AttackOrder([id], foe) { Tick = world.Tick });
            }
    }

    // The counter's numbers per side (standing, alive), counted again when the world ticks.
    readonly (int Standing, int Alive)[] battleCounts = new (int, int)[2];
    long battleCountedAt = -1;
    World? battleCountedIn;

    /// <summary>
    /// The live counter at the top middle: each side's fighters still standing (the battle's own test: not incapacitated) and still alive (down but not dead count),
    /// of how many it started with. Interface pass only (after the world draw's tick has joined).
    /// </summary>
    void DrawBattleCounter(DebugOverlay o, int width, int height)
    {
        var world = session.World;
        if (battleCountedAt != world.Tick || battleCountedIn != world)
        {
            (battleCountedAt, battleCountedIn) = (world.Tick, world);
            var table = world.Characters;
            for (int side = 0; side < 2; side++)
            {
                int standing = 0, alive = 0;
                foreach (var id in battleFighters[side])
                {
                    if (!table.TryResolveNext(id, out int slot) || table.Cold(slot)?.Medical is not { } medical || medical.Dead) continue;
                    alive++;
                    if (!medical.Incapacitated) standing++;
                }
                battleCounts[side] = (standing, alive);
            }
        }
        string Line(int side) => $"{battleSides[side].Name}: {battleCounts[side].Standing} standing, {battleCounts[side].Alive} alive of {battleSides[side].Count}";
        string a = Line(0), b = Line(1);
        var colourA = new Vector4(0.55f, 0.8f, 1f, 1);
        var colourB = new Vector4(1f, 0.6f, 0.5f, 1);
        float pad = 8, w = Math.Max(a.Length, b.Length) * o.CharWidth + 2 * pad, h = 2 * o.LineHeight + 2 * pad;
        float x0 = (width - w) / 2, y0 = 10;   // top middle: the HUD holds the corners
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        o.Text(a, x0 + pad, y0 + pad, colourA);
        o.Text(b, x0 + pad, y0 + pad + o.LineHeight, colourB);
        o.Flush(width, height);
    }

    /// <summary>Once per frame, between ticks: Backspace starts the battle again in a new world made the same way.</summary>
    void BattleFrame()
    {
        if (!IsBattle || !battleRestartRequested) return;
        battleRestartRequested = false;
        var built = BuildSystems(interactive: true);
        session.ReplaceWorld(built.Systems, world => BattleSetup(world, built.Population!));
        population = built.Population;
        aggro = built.Aggro;
        worldEpoch++;
        indexedFor = null;
        previousById.Clear();
        player.Reset();
        player.Notice("Battle restarted (Backspace)");
    }
}
