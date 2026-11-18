using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using TaskType = Meitou.Data.Gameplay.Ai.TaskType;

namespace ProvingGrounds.Engine;

// The bed adapter (task 8.4a, CAP 12; m8-research.md "8.4"): the Meitou reads of a town building's beds that TownAftercare.cpp makes through
// Kenshi's engine. Meitou 0.17.3 (tag v0.17.3, fa34a1b); paths relative to its repo root. PUBLIC Meitou API only (nothing in Members.cs); PgCheck
// engine JIT-binds this class, PgCheck versions reads its members in every installed game version. Sim thread, serial steps only.
//
// The Kenshi calls and their Meitou counterparts (E1):
//   a bed (a Building with BF_BED / BF_SKELETON_BED, its furnitureParentBuilding() the hospital) ... a Fixture of FixtureKind.Bed of the hospital's
//       TownBuilding (FixtureRegistry.OfBuilding(key), FixtureRegistry.cs:136; ids "<key>@bed<k>" in spot order, :519-541)
//   Building::getSpecialFunction() of the bed ... Fixture.Bed.Function (the BUILDING's functionality "function": 6 normal, 25 skeleton; Fixtures.cs:80, 375)
//   UseableStuff::getOccupant() ... Fixture.Occupant; it may be stale: FixtureRegistry.IsFree (:1412-1414) tells whether it still holds the bed
//   Character::inSomething == IN_BED (on that bed) ... CharacterCold.InSomething == HeldState.Bed and InWhat == the bed's id
//   getBuildState()->isComplete, numOperatorsMax == 1, isDisabled() ... none: a town bed has no construction state, operator count or switch
//       (only power and reachability, FixtureRegistry.cs:1462)
//   the bed record's "power output" (the test verb only; BedSystem.cs:463-468 refuses a put-in for a negative one in a town with no generator)
// Task 8.4b, the bed orders of TownAftercare's Execute (TownAftercare.cpp:1003-1031; m8-research.md "8.4"):
//   c->addOrder(targetBed, PUT_SOMEONE_IN_BED, targetBed, ...) for a carrying medic ... PutIn: TaskStart.Start(GoTo, FindBedAndPutIn (106), Npc)
//       { Site = bed } (spike S8 live: the carry walks to the bed and lays the body in, BedSystem.StartPutIn :256-261, a carrying trip never picks
//       another bed :390-404; at the bed LayIn :491-506 tests free, power and rent, then LetGo + EnterBed; no robot or bed-function test)
//   p->addOrder(targetBed, USE_BED_ORDER, targetBed, ...) for a walking patient ... UseBed: TaskStart.Start(Sleep, UseBed (98), Npc) { Site = bed }
//       (what UseBedOrder's own handler starts, BedSystem.cs:212-219; a fixed bed, StartSleep(fixedBed: true) :297-315: a taken one makes it stand
//       and end the trip :390-397; at the bed LieIn tests power and rent, then EnterBed)
//   both with TaskSource.Npc, not Goal: a Goal-sourced GoTo counts as the AI's own walk, which a conversation start stops (TaskStart.OnAiWalk
//       :391-395, DialogueSystem.cs:551); nothing else reads the source for these rows (BedSystem.cs:646 reads only Job)
//   whether a bed has power (the test verb, the skeleton-bed finding) ... Powered: BedSystem.HasPower's two tests (:470-488), which are private
// Task 8.4c, ReleaseBedOccupant (TownAftercare.cpp:376-402; m8-research.md "Taking a patient out of a bed"):
//   setBedModeOriginal(p, false, bed) ... LeaveBedMode: TaskStart.End(Stopped) + FixtureRegistry.GetOutOfBed (BedSystem.GetUp's own call, now)
//   bed->stopOperating(actor) .......... StopOperating: FixtureRegistry.LeaveBed
static class Beds
{
    /// <summary>The bed fixtures of the town building <paramref name="buildingKey"/>, in spot order (empty: none, or not a town building).</summary>
    internal static List<string> OfBuilding(World world, string buildingKey)
    {
        var list = new List<string>();
        var own = world.Fixtures.OfBuilding(buildingKey);
        for (int i = 0; i < own.Count; i++) if (own[i].Kind == FixtureKind.Bed) list.Add(own[i].Id);
        return list;
    }

    /// <summary>Task 8.4c: the bed fixtures of every standing building of record <paramref name="recordId"/> in the placed town
    /// <paramref name="site"/> (TownSite.Buildings, PopulationData.cs:197-205; a destroyed one left out as PgEngine.BuildingsNear does), each with
    /// its building's record, appended to <paramref name="into"/> in building then spot order.</summary>
    internal static void OfTownRecord(World world, TownSite site, string recordId, List<(string Bed, string Record)> into)
    {
        var buildings = site.Buildings;   // copy-on-write list: take it once
        for (int i = 0; i < buildings.Count; i++)
        {
            var b = buildings[i];
            if (!string.Equals(b.BuildingId, recordId, StringComparison.Ordinal) || !PgEngine.IsListed(world, b)) continue;
            var own = world.Fixtures.OfBuilding(b.Key);
            for (int k = 0; k < own.Count; k++) if (own[k].Kind == FixtureKind.Bed) into.Add((own[k].Id, b.BuildingId));
        }
    }

    /// <summary>Task 8.4c: <c>world.Fixtures.RuinsRevision</c>, which moves when a building is ruined or rebuilt (Towns.Refresh reads it the same way):
    /// the bed recovery finds the hospital's beds again when it changes.</summary>
    internal static long RuinsRevision(World world) => world.Fixtures.RuinsRevision;

    /// <summary>The bed fixture <paramref name="id"/> (null: none, or not a bed).</summary>
    internal static Fixture? Find(World world, string id) => world.Fixtures.Find(id) is { Kind: FixtureKind.Bed } f ? f : null;

    /// <summary>Whether the bed is held: its occupant still lies there (<c>!FixtureRegistry.IsFree</c>).</summary>
    internal static bool Occupied(World world, Fixture bed) => !FixtureRegistry.IsFree(world.Characters, bed);

    /// <summary>Whether <paramref name="c"/> lies in <paramref name="bed"/> (<c>InSomething == HeldState.Bed</c>, <c>InWhat</c> the bed).</summary>
    internal static bool LiesIn(World world, CharacterId c, Fixture bed) =>
        !c.IsNone && world.Characters.TryResolveNext(c, out int s) && world.Characters.Cold(s) is { } cold &&
        cold.InSomething == HeldState.Bed && cold.InWhat == bed.Id;

    /// <summary>Task 8.4b: the carrier in <paramref name="carrier"/> takes the body it carries to the bed <paramref name="bedId"/> and lays it in
    /// (FindBedAndPutIn, source Npc). False for a stale id, a missing bed or a refused start (a carrier that cannot walk, row 106 is human-only).
    /// Serial step.</summary>
    internal static bool PutIn(World world, CharacterId carrier, string bedId)
    {
        if (!world.Characters.TryResolveNext(carrier, out int slot) || Find(world, bedId) is not { } bed) return false;
        return TaskStart.Start(world, slot, new TaskRequest(CharacterTask.GoTo, TaskType.FindBedAndPutIn, TaskSource.Npc) { Site = bed.Id })
            == StartResult.Started;
    }

    /// <summary>Task 8.4b: <paramref name="c"/> walks to the bed <paramref name="bedId"/> and lies in it (UseBed, source Npc; a taken bed makes it
    /// stand and end the trip). False for a stale id, a missing bed or a refused start (asleep, fleeing, carried). Serial step.</summary>
    internal static bool UseBed(World world, CharacterId c, string bedId)
    {
        if (!world.Characters.TryResolveNext(c, out int slot) || Find(world, bedId) is not { } bed) return false;
        return TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Sleep, TaskType.UseBed, TaskSource.Npc) { Site = bed.Id })
            == StartResult.Started;
    }

    /// <summary>Task 8.4b (the test verb): whether a lie-in or put-in at <paramref name="bed"/> passes the power test, the two tests of the private
    /// <c>BedSystem.HasPower</c> (BedSystem.cs:470-488): a machine that consumes power and gets none refuses; a placed bed (no machine) whose
    /// BUILDING draws power (<c>power output</c> below 0, the base Skeleton Repair Bed's -10) in a town none of whose standing buildings holds a
    /// generator refuses (<c>Unpowered</c>).</summary>
    internal static bool Powered(World world, PopulationData data, Fixture bed)
    {
        if (world.Machines.Find(bed.Building) is { } machine) return !(machine.Data.IsConsumer && !machine.HasPower);
        if (bed.Site < 0 || bed.Site >= data.Sites.Count || !(data.Db.Find(bed.RecordId)?.GetInt("power output") < 0)) return true;
        foreach (var b in data.Sites[bed.Site].Buildings)
            if (b.Generators > 0 && !world.Fixtures.IsDestroyed(b)) return true;
        return false;
    }

    /// <summary>Task 8.4c: TownAftercare's ReleaseBedOccupant <c>setBedModeOriginal(p, false, bed)</c> (TownAftercare.cpp:381): the Sleep task ended
    /// (<c>TaskStart.End(world, slot, TaskEnd.Stopped)</c>, TaskStart.cs:485-505: the bed trip dropped, BedSystem.cs:269-274, the task Idle,
    /// MovementSystem.cs:273-283, a decision asked; skipped for a dead character (<c>Medical.Dead</c>): a corpse has no task to end, and the C++
    /// asks setBedMode(false) of it all the same, which is the get-up below) and then the engine's own get-up,
    /// <c>FixtureRegistry.GetOutOfBed</c> (FixtureRegistry.cs:1862-1871), the call BedSystem.GetUp makes at its next step for a sleeper whose task
    /// is no longer Sleep (BedSystem.cs:368-376): a base bed is left at once (StandBeside), a bed with the go-to-bed clip keeps the sleeper while
    /// the clip plays backwards. Nothing for a stale id or one not in a bed. Serial step.</summary>
    internal static void LeaveBedMode(World world, CharacterId c)
    {
        if (!world.Characters.TryResolveNext(c, out int slot) || world.Characters.Cold(slot) is not { InSomething: HeldState.Bed } cold) return;
        if (cold.Medical is not { Dead: true }) TaskStart.End(world, slot, TaskEnd.Stopped);
        world.Fixtures.GetOutOfBed(world, c);
    }

    /// <summary>Task 8.4c: ReleaseBedOccupant's fallback <c>bed-&gt;stopOperating(actor)</c> (TownAftercare.cpp:382-388): the character out of its
    /// bed at once (<c>FixtureRegistry.LeaveBed</c>, FixtureRegistry.cs:1878-1888: the bed freed, the character stood where it lay or beside the
    /// bed; no clip). <paramref name="bedId"/> is the bed the registry holds it in (LeaveBed frees the bed the character lies in). Serial step.</summary>
    internal static bool StopOperating(World world, CharacterId c, string bedId) =>
        !c.IsNone && Find(world, bedId) is not null && world.Fixtures.LeaveBed(world, c);

    /// <summary>The bed <paramref name="c"/> lies in (<c>InWhat</c> while <c>InSomething == HeldState.Bed</c>), null otherwise. Test verb only.</summary>
    internal static string? InWhat(World world, CharacterId c) =>
        !c.IsNone && world.Characters.TryResolveNext(c, out int s) && world.Characters.Cold(s) is { InSomething: HeldState.Bed } cold ? cold.InWhat : null;

    /// <summary>The bed record's "power output" (null: no record or no such field). Test verb only.</summary>
    internal static int? PowerOutput(PopulationData data, Fixture bed) =>
        bed.RecordId.Length == 0 ? null : data.Db.Find(bed.RecordId)?.GetInt("power output");
}
