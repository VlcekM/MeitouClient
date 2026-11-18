using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Production;

/// <summary>
/// The answer of <see cref="ProductionSystem.CanSit"/> and <see cref="ProductionSystem.SitOn"/>.
/// </summary>
public enum SeatAnswer : byte
{
    /// <summary>
    /// It can sit there (<see cref="ProductionSystem.CanSit"/>), or it walks there or sits there now (<see cref="ProductionSystem.SitOn"/>).
    /// </summary>
    Seated,
    /// <summary>
    /// No complete chair or throne that is not destroyed has that id.
    /// </summary>
    NotASeat,
    /// <summary>
    /// Another character sits there or is on its way to it (one sitter per seat).
    /// </summary>
    Taken,
    /// <summary>
    /// The character cannot sit: gone, dead, down, carried, carrying someone, fleeing, in a bed or cage, or not of the human repertoire (an animal,
    /// a crawler, one staying low).
    /// </summary>
    Unable,
    /// <summary>
    /// The doors keep it from the seat: the seat's building is shut to it, or the one it stands in is shut with no way out.
    /// </summary>
    ShutOut,
}

/// <summary>
/// The production's seats: the town and player chairs a character sits on as a machine.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The refusals of <see cref="SitOn"/> already logged (row, seat, answer); for the log line only, not hashed.
    /// </summary>
    readonly HashSet<(TaskType Row, string Seat, SeatAnswer Answer)> _seatRefusalsLogged = [];

    /// <summary>
    /// The facing a sitter takes on a seat site. Town-layout seats already carry their effective facing. A player-built chair must resolve its
    /// current house/outpost's furniture at use time.
    /// </summary>
    float SeatFacing(World world, BuildSite site, Machine m)
    {
        if (world.Fixtures.FindSeat(site.Id) is { } seat) return world.BenchFacingFix ? seat.Yaw : seat.FaithfulYaw;

        float yaw = TownBuildings.YawOf(site.Rotation);
        if (m.Data.Function != TownBuildings.ChairFunction || m.Data.FunctionFlags is not (1 or 2) || world.Buildings.Find(site.Id) is null)
            return yaw;

        return TownBuildings.SeatYaw(site.Position, yaw, m.Data.FunctionFlags, Targets(), faithful: !world.BenchFacingFix);

        IEnumerable<Vector3> Targets()
        {
            if (site.Parent is { } parent)
                foreach (var at in world.Fixtures.TablesOfBuilding(parent)) yield return at;

            foreach (var other in world.Buildings.All)
            {
                if (other.Complete && !other.Destroyed && other.Parent == site.Parent && (site.Parent is not null || other.Town == site.Town)
                    && Data(other.Record)?.Function is TownBuildings.TableFunction or TownBuildings.CorpseDisposalFunction
                        or TownBuildings.RefineryFunction)
                {
                    yield return other.Position;
                }
            }
        }
    }

    /// <summary>
    /// Whether a character (other than the one at <paramref name="except"/>) sits resting on the seat site's place.
    /// </summary>
    static bool SeatOccupied(World world, BuildSite site, int except = -1)
    {
        var table = world.Characters;

        for (int i = 0; i < table.HighWater; i++)
        {
            if (i != except && table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.Rest && table.Cold(i)?.Ai?.SitClip is not null
                && Vector3.DistanceSquared(table.Next[i].Position, site.Position) < 1) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the character at <paramref name="slot"/> can be put on seat <paramref name="seat"/> by <see cref="SitOn"/> now (docs/modding.md
    /// "Seats"). It changes nothing in the world and logs nothing, so a mod may ask it of many seats; serial phases only (it reads other characters'
    /// state and fills this system's site caches), so never from a parallel phase such as an NPC goal's desire. <paramref name="seat"/> is a town
    /// seat's id (<see cref="FixtureRegistry.SeatSpots"/>) or a complete building's (<see cref="World.Buildings"/>) whose functionality is a chair
    /// or throne.
    /// </summary>
    public SeatAnswer CanSit(World world, int slot, string seat)
    {
        var table = world.Characters;
        if (table.Phase == TablePhase.Parallel) throw new InvalidOperationException("seats are asked and taken in the serial phases only");
        if ((uint)slot >= (uint)table.HighWater || !table.Next[slot].Alive || table.Cold(slot) is not { } cold) return SeatAnswer.Unable;
        if (SeatSite(world, seat) is not { } site) return SeatAnswer.NotASeat;
        if (!CanSitDown(world, cold)) return SeatAnswer.Unable;

        // Another rests there (the AI's sitters too), or one other character is on the operate task for it, walking there included: one sitter per
        // seat whatever its max operators, since the act-out has only the seat's origin to put a sitter on.
        int own = cold.OperateSite == seat ? 1 : 0;
        if (SeatOccupied(world, site, slot) || OperatorsOf(world, seat) - own >= 1) return SeatAnswer.Taken;

        return ShutTo(world, slot, site.Position) ? SeatAnswer.ShutOut : SeatAnswer.Seated;
    }

    /// <summary>
    /// Puts the character at <paramref name="slot"/> on seat <paramref name="seat"/> as the player's Use order does (serial phases only; docs/modding.md
    /// "Seats"): when <see cref="CanSit"/> answers <see cref="SeatAnswer.Seated"/>, the operate task starts through <see cref="TaskStart.Start"/> as
    /// row <paramref name="row"/> (a code mod's, from <see cref="SimulationExtensions.FirstModCode"/> up) from <paramref name="source"/>, so the
    /// character walks there and sits on the seat's origin facing its way; one already on that seat on that row is left as it is. Any other answer
    /// changes nothing and is logged once per row, seat and answer.
    /// </summary>
    public SeatAnswer SitOn(World world, int slot, string seat, TaskType row, TaskSource source)
    {
        if ((int)row < SimulationExtensions.FirstModCode) throw new ArgumentOutOfRangeException(nameof(row), row, "a seat takes a code mod's own row");

        var answer = CanSit(world, slot, seat);
        if (answer != SeatAnswer.Seated)
        {
            Refused(world, slot, seat, row, answer);
            return answer;
        }

        var cold = world.Characters.Cold(slot)!;
        bool there = world.Characters.Next[slot].Task == (byte)CharacterTask.Operate && cold.OperateSite == seat;
        if (there && cold.Running is { } running && running.Row == row && running.Runs(CharacterTask.Operate)) return SeatAnswer.Seated;

        if (TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Operate, row, source) { Site = seat }) == StartResult.Started)
            return SeatAnswer.Seated;

        Refused(world, slot, seat, row, SeatAnswer.Unable);
        return SeatAnswer.Unable;
    }

    /// <summary>
    /// The site of seat <paramref name="seat"/>: complete, not destroyed, and its functionality a chair or throne; null for any other id.
    /// </summary>
    BuildSite? SeatSite(World world, string? seat)
    {
        if (string.IsNullOrEmpty(seat) || Site(world, seat) is not { Complete: true, Destroyed: false } site) return null;
        return Data(site.Record) is { Function: TownBuildings.ChairFunction or ThroneFunction } ? site : null;
    }

    /// <summary>
    /// Whether the character can sit at all: not down or carried (<see cref="Able"/>), carrying no one, not fleeing (its flight starts its next hop at
    /// once), in no bed or cage, and of a repertoire that registers 87 Operating machine, the original's sitting (<c>Task_OperateMachine</c>; human
    /// only, so no animal, crawler or character staying low).
    /// </summary>
    static bool CanSitDown(World world, CharacterCold cold) =>
        Able(cold) && cold.Carrying.IsNone && !cold.Fleeing && cold.InSomething == HeldState.None
        && TaskStart.Eligible(world, cold, TaskType.OperateMachinery);

    /// <summary>
    /// Whether the doors keep the character from the seat at <paramref name="at"/>, as of now: it stands in a building shut to it with no way out, or
    /// the seat's building is shut to it (<see cref="DestinationAccess.Accessible"/> without the restricted rows, so no lock is picked or cut on the
    /// way; a character whose faction bashes doors may still bash its way in).
    /// </summary>
    static bool ShutTo(World world, int slot, Vector3 at)
    {
        if (world.SystemOf<LockSystem>() is { } locks && !locks.CanLeave(world, slot, new Vector2(at.X, at.Z))) return true;

        var building = DestinationAccess.BuildingAt(world, at);
        return !DestinationAccess.Accessible(world, slot, building, animalDefault: false, restricted: false);
    }

    /// <summary>
    /// The log line of a refused <see cref="SitOn"/>, once per row, seat and answer in this world (a mod retrying a taken seat every tick writes one).
    /// </summary>
    void Refused(World world, int slot, string seat, TaskType row, SeatAnswer answer)
    {
        if (!_seatRefusalsLogged.Add((row, seat, answer))) return;

        string who = (uint)slot < (uint)world.Characters.HighWater && world.Characters.Cold(slot) is { } cold ? cold.Name : "?";
        string line = $"seat      row {(int)row}: '{seat}' refused for {who} (slot {slot}): {answer}; not logged again for this row, seat and answer";
        Console.Error.WriteLine(line);
    }

    /// <summary>
    /// The town seats' sites by fixture id, made again when the seat changes.
    /// </summary>
    readonly Dictionary<string, (SeatSpot Source, BuildSite Site)> _seatSites = new(StringComparer.Ordinal);

    /// <summary>
    /// The site of town seat <paramref name="id"/> (cached); null when no seat has the id.
    /// </summary>
    BuildSite? Seat(World world, string id)
    {
        if (world.Fixtures.FindSeat(id) is not { } seat) return null;
        if (_seatSites.TryGetValue(id, out var cached) && ReferenceEquals(cached.Source, seat)) return cached.Site;

        var site = new BuildSite
        {
            Id = id,
            Record = seat.Record,
            Name = _db.Find(seat.Record)?.Name ?? seat.Record,
            Position = seat.Position,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, seat.Yaw),
            Owner = seat.Owner,
            Complete = true,
            Materials = [],
        };
        _seatSites[id] = (seat, site);
        return site;
    }
}