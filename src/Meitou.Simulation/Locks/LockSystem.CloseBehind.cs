using System.Numerics;
using Meitou.Simulation.Ai;

namespace Meitou.Simulation.Locks;

/// <summary>
/// Doors closed behind a squad that closes them (<see cref="SquadConduct.CloseDoors"/>, docs/modding.md "Job choice"): a door a member opened on
/// its way is closed once that member is through it and nobody stands at it. Engine API, not in the original.
/// </summary>
public sealed partial class LockSystem
{
    /// <summary>
    /// How far past the leaf the opener must be before its door closes, and how near anyone else keeps it open (units).
    /// </summary>
    const float CloseBehindClear = 20;

    /// <summary>
    /// How long a door waits for its opener to pass before it is left open (game seconds).
    /// </summary>
    const float CloseBehindSeconds = 60;

    /// <summary>
    /// The doors to close behind their opener, by fixture id: the opener, the side of the leaf it opened from (the sign of its offset along the
    /// leaf's normal) and the tick it opened (hashed while any).
    /// </summary>
    readonly SortedDictionary<string, (CharacterId Opener, int Side, long Tick)> _closeBehind = new(StringComparer.Ordinal);

    /// <summary>
    /// Notes a door the walker in <paramref name="slot"/> opened when its squad closes doors behind it.
    /// </summary>
    void NoteCloseBehind(World world, Fixture door, DoorSpot spot, int slot)
    {
        var table = world.Characters;
        if (slot < 0 || table.Cold(slot) is not { SquadId: >= 0 } c || world.Squads.Find(c.SquadId) is not { } squad) return;
        if ((squad.Conduct & SquadConduct.CloseDoors) == 0) return;

        _closeBehind[door.Id] = (table.IdOf(slot), SideOf(table.Next[slot].Position, spot), world.Tick);
    }

    /// <summary>
    /// Closes each noted door whose opener is through it, past <see cref="CloseBehindClear"/>, with nobody else within that of the leaf; drops
    /// one no longer open, whose opener is gone, or that waited <see cref="CloseBehindSeconds"/>. Serial (the doors' step).
    /// </summary>
    void CloseBehind(World world)
    {
        if (_closeBehind.Count == 0) return;

        List<string>? done = null;
        foreach (var (id, noted) in _closeBehind)
        {
            if (CloseBehindDone(world, id, noted)) (done ??= []).Add(id);
        }

        if (done is not null) foreach (var id in done) _closeBehind.Remove(id);
    }

    /// <summary>
    /// One noted door's step; true when it is no longer noted.
    /// </summary>
    bool CloseBehindDone(World world, string id, (CharacterId Opener, int Side, long Tick) noted)
    {
        var table = world.Characters;
        if (world.Fixtures.Find(id) is not { Kind: FixtureKind.Door } door || door.DoorBroken) return true;
        if (door.Motion is not (DoorMotion.Open or DoorMotion.Opening) || world.Fixtures.DoorSpotOf(door) is not { } spot) return true;
        if (!table.TryResolveNext(noted.Opener, out int o)) return true;
        if ((world.Tick - noted.Tick) * world.TickSeconds > CloseBehindSeconds) return true;

        var at = table.Next[o].Position;
        if (door.Motion != DoorMotion.Open || SideOf(at, spot) == noted.Side || FromLeaf(at, spot) < CloseBehindClear) return false;
        if (AnyoneAt(world, spot)) return false;

        return FixtureRegistry.CloseDoor(door);
    }

    /// <summary>
    /// The side of the leaf <paramref name="at"/> is on: the sign of its offset along the leaf's normal (1 or -1).
    /// </summary>
    static int SideOf(Vector3 at, DoorSpot spot)
    {
        var d = new Vector2(at.X - spot.Centre.X, at.Z - spot.Centre.Z);
        return Vector2.Dot(d, new Vector2(spot.Normal.X, spot.Normal.Z)) >= 0 ? 1 : -1;
    }

    /// <summary>
    /// Whether a living character stands within <see cref="CloseBehindClear"/> of the leaf, at its floor.
    /// </summary>
    static bool AnyoneAt(World world, DoorSpot spot)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            ref var n = ref table.Next[s];
            if (!n.Alive || MathF.Abs(n.Position.Y - spot.Foot) > DoorHeight) continue;
            if (FromLeaf(n.Position, spot) < CloseBehindClear) return true;
        }

        return false;
    }

    /// <summary>
    /// Adds the noted doors to the state hash, only while there are any.
    /// </summary>
    void HashCloseBehind(ref StateHasher hasher)
    {
        if (_closeBehind.Count == 0) return;

        hasher.Add(0x436C4268);
        foreach (var (id, noted) in _closeBehind)
        {
            hasher.Add(Rng.StableHash(id));
            hasher.Add(noted.Opener);
            hasher.Add(noted.Side);
            hasher.Add(noted.Tick);
        }
    }
}
