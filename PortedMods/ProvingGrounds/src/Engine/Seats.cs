using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using ProvingGrounds.Core.Glue;
using TaskType = Meitou.Data.Gameplay.Ai.TaskType;

namespace ProvingGrounds.Engine;

// The seat adapter (task 8.2, CAP 5, 19; m8-research.md "8.2 Spectators: seats"; spike S16, docs/spikes-live.md): the Meitou reads and orders of
// a town seat that TownSpectators.cpp makes through Kenshi's engine. Meitou 0.17.3 (tag v0.17.3, fa34a1b); paths relative to its repo root. PUBLIC
// Meitou API only (as src/Engine/Beds.cs: nothing in Members.cs, which holds the by-name members); PgCheck engine JIT-binds this class, PgCheck
// versions reads its members in every installed game version, PgCheck hooks checks the task row it compiles in as a number. Serial steps only.
//
// The Kenshi calls and their Meitou counterparts (E1):
//   a Building 192 with BF_CHAIR, its UseableStuff (Seat :71-81) ... an Arena Pillow is a town building of its own whose one chair is the town seat
//       "<pillow key>@seat0" (world.Fixtures.FindSeat, src/Meitou.Simulation/Locks/FixtureRegistry.Seats.cs:42, :53-65; Data/World/
//       TownBuildingBuilder.cs:80-93); numOperatorsMax == 1, isDisabled() ... none (a town seat has no operator count or switch)
//   UseableStuff::getOccupant() (Occupied :67-70) ... Occupants: an operator sitting on it (task Operate, CharacterCold.OperateSite == the seat,
//       Operating: at the seat this step, Production/ProductionSystem.cs:1001-1031, ProductionSystem.Jobs.cs:441-448) or an AI sitter resting on
//       its place (task Rest with an Ai.SitClip within 1 unit: ProductionSystem.Seats.cs:48-59, the engine's own private test)
//   isFreeSlot(actor) ... Occupants' claims: an operator still on its way (OperateSite == the seat, not yet Operating), a squad board's sit or seat
//       claim on it (Blackboard.SitClaims: building key + spot position; SeatClaims: the spot position; Ai/Blackboard.cs:290-296, keyed by slot)
//   couldIOperate(actor) ... CanSit: TaskStart.Eligible(world, cold, SitAround) (Ai/TaskStart.cs:275; row 275 is a human row, TaskDataTable.cs:264)
//   c->addOrder(seat, seat->getDefaultTask(), seat, false, true, pos) + RUN (Tick :475-477) ... Sit: TaskStart.Start(world, slot, new
//       TaskRequest(CharacterTask.Operate, TaskType.SitAround, TaskSource.Npc) { Site = seat, Implicit = true }) (the operate handler has no
//       player test: OperateOrder refuses NPCs, ProductionSystem.cs:481; Begin :670-728 walks to the seat at SpeedMode.Free, Approach re-walks,
//       UseSpecial / Settle put it on the seat; Implicit + SitAround makes MovementSystem.Seated count it seated, MovementSystem.cs:2191-2194;
//       TaskSource.Npc keeps the job logic out, PermaJobs.cs:114-118)
//   seat->stopOperating(actor) + removeJob(task) (ReleaseGuest :96-97) ... StandUp: TaskStart.End(world, slot, TaskEnd.Stopped) while its task is
//       Operate on that seat (the AI never takes back an Operate task, AiSystem.cs:372-384, so the end comes before the hold is given back)
static class Seats
{
    /// <summary>Occupants' marks: seat i's occupant is an operator (it wins over a resting sitter). Reused (sim thread only).</summary>
    static bool[] byOperator = new bool[120];

    /// <summary>The town seat of the building <paramref name="buildingKey"/> (its first, "<key>@seat0"; null: none).</summary>
    internal static SeatSpot? FirstOf(World world, string buildingKey) => world.Fixtures.FindSeat(buildingKey + "@seat0");

    /// <summary>The town seat <paramref name="id"/> (null: none).</summary>
    internal static SeatSpot? Find(World world, string id) => world.Fixtures.FindSeat(id);

    /// <summary><c>couldIOperate(actor)</c>'s actor half: the character may take the sit row at all (alive, the row's repertoire). False for a stale id.</summary>
    internal static bool CanSit(World world, CharacterId c) =>
        world.Characters.TryResolveNext(c, out int slot) && world.Characters.Cold(slot) is { } cold && TaskStart.Eligible(world, cold, TaskType.SitAround);

    /// <summary>Spike S16: <paramref name="c"/> walks to the seat <paramref name="seatId"/> and sits on it (the implicit SitAround Operate task, source
    /// Npc). False for a stale id, a missing seat or a refused start. Serial step.</summary>
    internal static bool Sit(World world, CharacterId c, string seatId)
    {
        if (!world.Characters.TryResolveNext(c, out int slot) || Find(world, seatId) is not { } seat) return false;   // the handler's Site() throws on a bad id
        return TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Operate, TaskType.SitAround, TaskSource.Npc) { Site = seat.Id, Implicit = true })
            == StartResult.Started;
    }

    /// <summary>Whether <paramref name="c"/>'s task is Operate on the seat <paramref name="seatId"/> (on its way or sitting).</summary>
    internal static bool OnItsWay(World world, CharacterId c, string seatId) =>
        !c.IsNone && world.Characters.TryResolveNext(c, out int slot) && world.Characters.Next[slot].Task == (byte)CharacterTask.Operate &&
        world.Characters.Cold(slot)?.OperateSite == seatId;

    /// <summary>Whether <paramref name="c"/> sits on the seat <paramref name="seatId"/> now (Operate there and <c>Operating</c>: at the seat this step).
    /// <c>Operating</c> is cleared and set again inside the same operator step (ProductionSystem.cs:1010 clears it, UseSpecial -> Settle sets it,
    /// ProductionSystem.Jobs.cs:447), so a serial-step read never sees the half-way value: it is "at the seat in the last operator step".</summary>
    internal static bool SittingOn(World world, CharacterId c, string seatId) =>
        OnItsWay(world, c, seatId) && world.Characters.TryResolveNext(c, out int slot) && world.Characters.Cold(slot) is { Operating: true };

    /// <summary>The sit task on <paramref name="seatId"/> ended (<c>TaskStart.End(Stopped)</c>); nothing when the task is not Operate on that seat.
    /// Serial step.</summary>
    internal static void StandUp(World world, CharacterId c, string seatId)
    {
        if (!OnItsWay(world, c, seatId) || !world.Characters.TryResolveNext(c, out int slot)) return;
        TaskStart.End(world, slot, TaskEnd.Stopped);
    }

    /// <summary>One scan of the character table for the seats <paramref name="ids"/> (their buildings and places in <paramref name="buildings"/> and
    /// <paramref name="positions"/>, same order): <paramref name="occupants"/>[i] = the character sitting on seat i (an operator there and Operating,
    /// else an AI sitter resting on its place), <paramref name="claimed"/>[i] = an operator on its way to it, or a squad board claim on it. The
    /// arrays are filled from index 0 (the caller sized and cleared them). An operator always wins the seat over a resting AI sitter on its place
    /// (whatever their slot order): the operator holds the seat, the resting one only stands on its spot. <paramref name="boardClaims"/> false skips
    /// the squad boards (TownSpectators reads them only for Begin's free test). <paramref name="index"/> is the caller's reusable id map. No
    /// allocation once the map and the operator marks have grown. Serial step.</summary>
    internal static void Occupants(World world, IReadOnlyList<string> ids, IReadOnlyList<string> buildings, IReadOnlyList<Vector3> positions,
        Dictionary<string, int> index, CharacterId[] occupants, bool[] claimed, bool boardClaims = true)
    {
        if (byOperator.Length < ids.Count) byOperator = new bool[Math.Max(ids.Count, 2 * byOperator.Length)];
        Array.Clear(byOperator, 0, ids.Count);
        index.Clear();
        for (int i = 0; i < ids.Count; i++) index.TryAdd(ids[i], i);
        var table = world.Characters;
        var next = table.Next;
        int high = Math.Min(table.HighWater, next.Length);
        for (int slot = 0; slot < high; slot++)
        {
            if (!next[slot].Alive || table.Cold(slot) is not { } cold) continue;
            var id = new CharacterId(slot, next[slot].Generation);
            if (next[slot].Task == (byte)CharacterTask.Operate && cold.OperateSite is { } site && index.TryGetValue(site, out int k))
            {
                if (cold.Operating && SpectatorSeatRules.OccupantWins(true, !occupants[k].IsNone, byOperator[k])) { occupants[k] = id; byOperator[k] = true; }
                else if (!cold.Operating) claimed[k] = true;
            }
            else if (next[slot].Task == (byte)CharacterTask.Rest && cold.Ai?.SitClip is not null)
            {
                for (int i = 0; i < positions.Count; i++)
                    if (SpectatorSeatRules.OccupantWins(false, !occupants[i].IsNone, byOperator[i]) && Vector3.DistanceSquared(next[slot].Position, positions[i]) < 1)
                        occupants[i] = id;
            }
            if (!boardClaims || cold.SquadId < 0 || world.Squads.Find(cold.SquadId)?.Ai is not { } board) continue;
            if (board.SitClaims.TryGetValue(slot, out var sit))
            {
                var at = sit.Spot.Position;
                for (int i = 0; i < positions.Count; i++)
                    if (positions[i] == at && sit.Building.Key == buildings[i]) claimed[i] = true;
            }
            if (board.SeatClaims.TryGetValue(slot, out var spot))
                for (int i = 0; i < positions.Count; i++)
                    if (positions[i] == spot.Position) claimed[i] = true;
        }
    }
}
