using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The town places the AI's squads' members claim on the package cadence (serial): the turrets they man, the seats they relax or stay home on, the
/// pieces they sit around on and the town guard posts they hold. Every pass starts with <see cref="Reserve"/>; then each squad claims in squad order.
/// Derived, not hashed.
/// </summary>
sealed class TownPlaceClaims(PopulationData data)
{
    #region Cadence pass

    /// <summary>
    /// The cadence pass's claims start again: the seats and posts the members hold are reserved for every squad first.
    /// </summary>
    public void Reserve(World world)
    {
        _turretTaken.Clear();
        _seatTaken.Clear();
        _postTaken.Clear();
        ReserveSeats(world);
        ReservePosts(world);
    }

    /// <summary>
    /// A point on the ground plane (X, Z).
    /// </summary>
    static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>
    /// The list a member plans with, as its think's: its master squad's Slave AI Goals when it obeys one (<see cref="AiSystem.ObeyedRoles"/>), else
    /// its role's. So an enslaved member claims no place its plan never uses.
    /// </summary>
    static IReadOnlyList<AiGoal> RolesOf(World world, Blackboard board, Squad squad, CharacterId id, CharacterCold cold) =>
        AiSystem.ObeyedRoles(world, squad, cold) ?? board.Roles[(int)AiSystem.RoleOf(squad, id, cold)];

    #endregion

    #region Turrets

    /// <summary>
    /// The turret indices of each town claimed in the current cadence pass (derived, not hashed).
    /// </summary>
    readonly Dictionary<int, HashSet<int>> _turretTaken = [];

    /// <summary>
    /// Gives the members' slots the town's turrets to man (docs/simulation.md "AI", turrets): in member order each takes the nearest turret no
    /// earlier member claimed (the original's unused turrets), by the distance to the character itself (<c>FUN_1408ccc70</c>); a member whose goal is
    /// the home building's turrets first takes one mounted on its home. Rebuilt on the package cadence; a town without turrets gives none.
    /// </summary>
    public void ClaimTurrets(World world, Blackboard board, Squad squad)
    {
        board.Turrets = data.Turrets(board.Town);
        board.TurretClaims.Clear();
        board.TurretFactors.Clear();
        if (board.Turrets.Count == 0) return;
        if (!_turretTaken.TryGetValue(board.Town, out var taken)) _turretTaken[board.Town] = taken = [];
        int hour = world.Hour, claimed = 0;
        for (int pass = 0; pass < 2 && claimed < board.Turrets.Count; pass++)
        {
            for (int k = 0; k < squad.Members.Count && claimed < board.Turrets.Count; k++)
            {
                var id = squad.Members[k];
                if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is not { } mind) continue;
                if (board.TurretClaims.ContainsKey(s) || !TurretGoal(RolesOf(world, board, squad, id, cold), hour, out bool homeOnly)) continue;
                if (pass == 0 ? !homeOnly : homeOnly) continue;
                int best = -1;
                float bestD = float.MaxValue;
                for (int t = 0; t < board.Turrets.Count; t++)
                {
                    if (taken.Contains(t) || board.Turrets[t].Turret is not { } turret) continue;
                    if (homeOnly && turret.MountedOn != board.Home?.Key) continue;
                    float d = Vector2.DistanceSquared(Flat(world.Characters.Previous[s].Position), Flat(turret.Position));
                    if (d < bestD) (best, bestD) = (t, d);
                }

                if (best >= 0)
                {
                    taken.Add(best);
                    board.TurretClaims[s] = best;
                    board.TurretFactors[s] = AiSystem.TurretFactor(bestD, cold.InCombat);
                    claimed++;
                }
            }
        }
    }

    /// <summary>
    /// Whether the member's list (<see cref="RolesOf"/>) has a turret goal open at <paramref name="hour"/>, and whether it is only the home
    /// building's (222): that one may only man a turret mounted on its home. That list only (not its race's goals): a race goal without the town's
    /// turret data names no turret.
    /// </summary>
    static bool TurretGoal(IReadOnlyList<AiGoal> roles, int hour, out bool homeOnly)
    {
        bool home = false, town = false;
        for (int k = 0; k < roles.Count; k++)
        {
            if (!roles[k].InWindow(hour)) continue;
            var type = roles[k].Task.Type;
            if (type == TaskType.ManATurretOnBuilding) home = true;
            else if (type is TaskType.ManATurret or TaskType.ManATurretPlayerJob or TaskType.UseTurret) town = true;
        }

        homeOnly = home && !town;
        return home || town;
    }

    #endregion

    #region Seats and posts

    /// <summary>
    /// The seats of each bar claimed in the current cadence pass, by building (derived, not hashed).
    /// </summary>
    readonly Dictionary<TownBuilding, HashSet<int>> _seatTaken = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The guard posts of each building held in the current cadence pass, by spot index (derived, not hashed).
    /// </summary>
    readonly Dictionary<TownBuilding, HashSet<int>> _postTaken = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The positions of the town training dummies a character trains on by an order or job (<see cref="CharacterTask.Operate"/>) in the current
    /// cadence pass: no member claims them (#996); derived, not hashed.
    /// </summary>
    readonly HashSet<Vector3> _usedDummies = [];

    /// <summary>
    /// The seats the members held before this pass (<see cref="ClaimSeats"/>), by slot; scratch.
    /// </summary>
    readonly Dictionary<int, HomeSpot> _kept = [];

    /// <summary>
    /// The sitting-around pieces the members held before this pass (<see cref="ClaimSitting"/>), by slot; scratch.
    /// </summary>
    readonly Dictionary<int, SeatClaim> _keptSits = [];

    /// <summary>
    /// The grid query of <see cref="Operated"/>: the characters, the spot and whether an operator stands on it.
    /// </summary>
    struct OperatorScan
    {
        /// <summary>
        /// The characters (last tick's positions).
        /// </summary>
        public CharacterTable Table;
        /// <summary>
        /// The spot.
        /// </summary>
        public Vector3 At;
        /// <summary>
        /// An operator stands on the spot.
        /// </summary>
        public bool Found;
    }

    /// <summary>
    /// Gives the members whose role list has relaxing (under a bar package) or staying home open at this hour a seat: relaxing in the package's bar
    /// (<see cref="Blackboard.Home"/>), staying home in the squad's own home (<see cref="Blackboard.OwnHome"/>: the finder of 43 lists the home
    /// building's furniture, not the package's target; <b>Observed</b>, the Hub squatters, whose package is a bar package, stay in their ruins in the
    /// original). In member order each takes the first seat no one in that building claimed in this cadence pass; members left without one do not
    /// relax or stay. Relaxing takes a chair, else a bar node, else a general node (the original operates furniture of the bar, docs/game/ai-tasks.md
    /// "Town goals", Observed; the node fallbacks are an engine choice); staying home a chair, dummy or workstation of the home, its nest's pieces
    /// round it included (<see cref="SeatsOf"/>). Sitting around (275) claims in <see cref="ClaimSitting"/>. A member keeps the seat it had while it
    /// still relaxes (engine choice: reassigning every pass in member order moved every later sitter to another chair whenever one came or went), and
    /// its piece while staying home is its current goal (<see cref="ClaimSeat"/>); the seats kept, and those a character sits on by an order (the player's Use), are reserved for every squad first
    /// (<see cref="ReserveSeats"/>).
    /// </summary>
    public void ClaimSeats(World world, Blackboard board, Squad squad)
    {
        _kept.Clear();
        foreach (var (slot, spot) in board.SeatClaims) _kept[slot] = spot;
        board.SeatClaims.Clear();
        board.UnpoweredSeats.Clear();
        if (board.Home is null && board.OwnHome is null) return;

        int hour = world.Hour;
        for (int k = 0; k < squad.Members.Count; k++)
        {
            var id = squad.Members[k];
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is null) continue;
            var roles = RolesOf(world, board, squad, id, cold);
            bool relaxes = false, sits = false;
            for (int g = 0; g < roles.Count; g++)
            {
                if (!roles[g].InWindow(hour)) continue;
                relaxes |= board.BarTable >= 0 && roles[g].Task.Type == TaskType.RelaxInTownPackage;
                sits |= roles[g].Task.Type == TaskType.StayInHome;
            }

            relaxes &= TaskStart.Eligible(world, cold, TaskType.RelaxInTownPackage);
            sits &= Seated(world, cold);
            ClaimSeat(world, board, s, id, relaxes, sits && !relaxes, cold.Ai);
        }
    }

    /// <summary>
    /// Whether the member's repertoire can use a seat of staying home or sitting around: both lead to 221 (effect 136), which only the human
    /// repertoire registers, so an animal or a crawling character plans nothing there and takes no seat (docs/game/ai-tasks.md "Town goals").
    /// </summary>
    static bool Seated(World world, CharacterCold cold) => TaskStart.Eligible(world, cold, TaskType.PretendToOperateMachinery);

    /// <summary>
    /// One member's seat of <see cref="ClaimSeats"/>: the one it kept, else a free one of the building its goal names; one that relaxes and stays
    /// no longer, or gave its seat up, leaves it to the next member. Staying home keeps its piece only while 43 is the member's current goal: the
    /// finder keeps a target already set, but a candidate that is not the current goal has its target cleared and draws again
    /// (<c>FUN_14050df10</c>), so a member not on it draws anew at each pass.
    /// </summary>
    void ClaimSeat(World world, Blackboard board, int slot, CharacterId id, bool relaxes, bool sits, AiMind mind)
    {
        var building = relaxes ? board.Home : board.OwnHome;
        bool kept = _kept.TryGetValue(slot, out var old);
        if (!relaxes && !sits || mind.SeatUnreachable || building is null)
        {
            if (kept) Release(world, board, old);
            return;
        }

        var seats = SeatsOf(world, building);
        var taken = TakenOf(building);
        int had = kept ? seats.IndexOf(old) : -1;
        if (had >= 0 && (Operated(world, seats.At(had).Position) || DummyUsed(seats.At(had)))) had = -1;
        bool staying = StaysAt(old.Kind) && mind.Goal?.Task.Type == TaskType.StayInHome;
        if (had >= 0 && (relaxes || staying))
        {
            taken.Add(had);
            Claim(world, board, slot, old);
            return;
        }

        // A redraw may draw its own piece again: the reservation of the seats held last pass keeps it from the others only.
        if (had >= 0) taken.Remove(had);

        int seat = relaxes ? BarSeat(building, board.BarTable, taken) : FreeFurniture(world, seats, taken, id);
        if (seat < 0 && relaxes && building.IsOutdoorTable) board.SeatClaims[slot] = new HomeSpot(HomeSpotKind.General, building.Position, building.Yaw);
        if (seat < 0) return;

        taken.Add(seat);
        Claim(world, board, slot, seats.At(seat));
    }

    /// <summary>
    /// A member's seat claim, marked in <see cref="Blackboard.UnpoweredSeats"/> when it is a workstation of the house's layout without power:
    /// staying home leads to 221, whose list B needs 93 BUILDING_HAS_POWER (<c>FUN_140596ab0</c>) and no row provides it, so the original plans
    /// nothing there and another goal wins (docs/game/ai-tasks.md "Town goals").
    /// </summary>
    static void Claim(World world, Blackboard board, int slot, HomeSpot spot)
    {
        board.SeatClaims[slot] = spot;
        if (spot is not { Kind: HomeSpotKind.Workstation, Piece: { } piece }) return;

        if (world.SystemOf<Production.ProductionSystem>()?.PiecePowered(world, piece) == false) board.UnpoweredSeats.Add(slot);
    }

    /// <summary>
    /// A free seat of the bar for a relaxing member, nearest the squad's table (engine choice; an outdoor table has no seats): a chair, else a bar
    /// node, else a general node; -1: none.
    /// </summary>
    static int BarSeat(TownBuilding bar, int table, HashSet<int> taken)
    {
        Vector3 near = table < bar.Tables.Count ? bar.Tables[table] : bar.Position;
        int seat = FreeSeat(bar, taken, HomeSpotKind.Chair, near);
        if (seat < 0) seat = FreeSeat(bar, taken, HomeSpotKind.Bar, near);
        if (seat < 0) seat = FreeSeat(bar, taken, HomeSpotKind.General, near);
        return seat;
    }

    /// <summary>
    /// Frees a seat a member held in the package's bar or its own home.
    /// </summary>
    void Release(World world, Blackboard board, HomeSpot spot)
    {
        foreach (var building in Buildings(board))
            if (SeatsOf(world, building).IndexOf(spot) is >= 0 and var i) TakenOf(building).Remove(i);
    }

    /// <summary>
    /// The buildings a squad's seats are in: the package's bar or home (<see cref="Blackboard.Home"/>) and its own home when that is another.
    /// </summary>
    static IEnumerable<TownBuilding> Buildings(Blackboard board)
    {
        if (board.Home is { } home) yield return home;
        if (board.OwnHome is { } own && !ReferenceEquals(own, board.Home)) yield return own;
    }

    /// <summary>
    /// The seats of a building as claims number them: its own (<see cref="TownBuilding.SeatAt"/>), then the furniture its residents' nest stood
    /// round it (<see cref="NestBuildings.FurnitureOf"/>).
    /// </summary>
    static HomeSeats SeatsOf(World world, TownBuilding building) => new(building, world.NestBuildings.FurnitureOf(building.Key));

    /// <summary>
    /// A building's seats (<see cref="SeatsOf"/>): <paramref name="building"/>'s own, then <paramref name="stood"/>.
    /// </summary>
    readonly struct HomeSeats(TownBuilding building, IReadOnlyList<HomeSpot> stood)
    {
        /// <summary>
        /// How many seats there are.
        /// </summary>
        public int Count => building.SeatCount + stood.Count;

        /// <summary>
        /// The seat of <paramref name="index"/>.
        /// </summary>
        public HomeSpot At(int index) => index < building.SeatCount ? building.SeatAt(index) : stood[index - building.SeatCount];

        /// <summary>
        /// The index of <paramref name="spot"/>; -1 when it is none of these.
        /// </summary>
        public int IndexOf(HomeSpot spot)
        {
            for (int i = 0; i < Count; i++)
                if (At(i) == spot) return i;
            return -1;
        }
    }

    /// <summary>
    /// Marks taken the chairs and thrones of <paramref name="home"/> a character sits on by an order (the player's Use,
    /// <see cref="CharacterTask.Operate"/>), so no member claims them.
    /// </summary>
    static void TakeOperatedSeats(World world, TownBuilding home, HashSet<int> taken)
    {
        for (int i = 0; i < home.Spots.Count; i++)
        {
            if (home.Spots[i].Kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat && !taken.Contains(i)
                && Operated(world, home.Spots[i].Position))
                taken.Add(i);
        }
    }

    /// <summary>
    /// Whether a character working a machine (<see cref="CharacterTask.Operate"/>) is on the spot, as of the last tick.
    /// </summary>
    static bool Operated(World world, Vector3 at)
    {
        var scan = new OperatorScan { Table = world.Characters, At = at };
        world.Grid.Query(at.X, at.Z, 1, ref scan, static (int other, ref OperatorScan st) =>
        {
            var prev = st.Table.Previous;
            if (st.Found || (uint)other >= (uint)prev.Length || !prev[other].Alive || prev[other].Task != (byte)CharacterTask.Operate) return;
            st.Found = Vector2.DistanceSquared(Flat(prev[other].Position), Flat(st.At)) < 1
                && MathF.Abs(prev[other].Position.Y - st.At.Y) <= AiSystem.FloorSlack;
        });
        return scan.Found;
    }

    /// <summary>
    /// Gathers <see cref="_usedDummies"/>: the training dummies characters operate as of the last tick, walking there included.
    /// </summary>
    void CollectUsedDummies(World world)
    {
        _usedDummies.Clear();
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Next[i].Task != (byte)CharacterTask.Operate || table.Cold(i)?.OperateSite is not { } site) continue;
            if (world.Fixtures.Find(site) is { Kind: Locks.FixtureKind.Training } dummy) _usedDummies.Add(dummy.Position);
        }
    }

    /// <summary>
    /// Marks taken the training dummies of <paramref name="home"/> (its nest's included) a character operates, so no member stays home at them.
    /// </summary>
    void TakeUsedDummies(World world, TownBuilding home, HashSet<int> taken)
    {
        if (_usedDummies.Count == 0) return;

        var seats = SeatsOf(world, home);
        for (int i = 0; i < seats.Count; i++)
            if (DummyUsed(seats.At(i))) taken.Add(i);
    }

    /// <summary>
    /// Whether <paramref name="spot"/> is a training dummy a character operates (<see cref="_usedDummies"/>).
    /// </summary>
    bool DummyUsed(HomeSpot spot) => spot.Kind == HomeSpotKind.Training && _usedDummies.Contains(spot.Position);

    /// <summary>
    /// The seats of <paramref name="building"/> taken in this cadence pass (indices of <see cref="TownBuilding.SeatAt"/>).
    /// </summary>
    HashSet<int> TakenOf(TownBuilding building) => _seatTaken.TryGetValue(building, out var taken) ? taken : _seatTaken[building] = [];

    /// <summary>
    /// The sitting-around (275) counterpart of <see cref="ClaimSeats"/> (the finder <c>FUN_1408caf00</c>, docs/game/ai-tasks.md "Town goals"): a
    /// member whose role list has 275 open at this hour takes a piece of furniture of the mask 0x1020080 that no one claimed in this cadence pass, in
    /// the town it stands in (<see cref="TownPlaces.Seats"/>; the home need not be a part of it): a chair or throne, and a training dummy unless the
    /// character is on the player's side (the mask 0x1020000 of <c>FUN_140856bc0</c>; an AI-run squad is never the player's, so this only spares a
    /// recruit). The town must be the squad's home town or owned by the squad's faction, a player-side character's a town of the player's faction
    /// (<see cref="MaySitIn"/>), and the piece's building owned by the character's faction or the squad's (<see cref="TownPlaces.SeatOwners"/>);
    /// outside every town it finds nothing. The nearest free piece is an engine choice (the original takes the first of its zone map). A member keeps
    /// its piece while it still sits around and may sit there, as in <see cref="ClaimSeats"/>.
    /// </summary>
    public void ClaimSitting(World world, Blackboard board, Squad squad, TownPlaceIndex places)
    {
        _keptSits.Clear();
        foreach (var (slot, claim) in board.SitClaims) _keptSits[slot] = claim;
        board.SitClaims.Clear();
        int hour = world.Hour;
        for (int k = 0; k < squad.Members.Count; k++)
        {
            var id = squad.Members[k];
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s) is not { } cold || cold.Ai is null) continue;
            var roles = RolesOf(world, board, squad, id, cold);
            bool sits = false, around = false;
            for (int g = 0; g < roles.Count; g++)
            {
                if (!roles[g].InWindow(hour)) continue;
                sits |= roles[g].Task.Type is TaskType.SitAround or TaskType.SitOnThrone;
                around |= roles[g].Task.Type == TaskType.SitAround;
            }

            bool had = _keptSits.TryGetValue(s, out var old);
            if (!sits || cold.Ai.SeatUnreachable || !Seated(world, cold))   // one that gave its seat up leaves it to the next member
            {
                if (had) TakenOf(old.Building).Remove(old.Index);
                continue;
            }

            bool playerSide = PlayerSide(world, cold);
            var at = world.Characters.Next[s].Position;
            int site = data.TownAt(at);
            var seats = site >= 0 && MaySitIn(world, board, squad, site, playerSide) ? places.Of(site) : TownPlaceIndex.None;
            bool Mine(TownBuilding b) =>
                seats.SeatOwners.TryGetValue(b, out int owner) && owner >= 0 && (owner == cold.Faction || owner == squad.Faction);

            // Only reigning open: the throne furniture alone (BF_THRONE, mask 0x1000000 of FUN_1408cbd50); sitting around also takes chairs and
            // dummies.
            bool Takes(HomeSpotKind kind) => around ? Sittable(kind, !playerSide) : kind == HomeSpotKind.ThroneSeat;
            if (had && Mine(old.Building) && Takes(old.Spot.Kind) && !DummyUsed(old.Spot))
            {
                TakenOf(old.Building).Add(old.Index);
                board.SitClaims[s] = old;
                continue;
            }

            if (had) TakenOf(old.Building).Remove(old.Index);
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < seats.Seats.Count; i++)
            {
                var seat = seats.Seats[i];
                if (!Takes(seat.Spot.Kind) || !Mine(seat.Building) || DummyUsed(seat.Spot)
                    || _seatTaken.TryGetValue(seat.Building, out var taken) && taken.Contains(seat.Index))
                    continue;

                float d = Vector3.DistanceSquared(seat.Spot.Position, at);
                if (d < bestD) (best, bestD) = (i, d);
            }

            if (best < 0) continue;
            var found = seats.Seats[best];
            TakenOf(found.Building).Add(found.Index);
            board.SitClaims[s] = found;
        }
    }

    /// <summary>
    /// The guard posts of 157 Static town guard (<c>AI::findGuardNodeForTown</c>, docs/game/ai-tasks.md "Town goals"): a member whose goal
    /// is 157 keeps the post it holds, else draws one uniformly among its town's guard posts no one holds (as the original's draw over the
    /// town's nodes held by no one else). A member on another goal, or whose home spots kept failing, lets its post go. None free: it holds
    /// none and stands where the home node goals stand (engine choice: the original's goal then finds nothing).
    /// </summary>
    public void ClaimTownPosts(World world, Blackboard board, Squad squad, TownPlaceIndex places)
    {
        var posts = places.Of(board.Town).Posts;
        for (int k = 0; k < squad.Members.Count; k++)
        {
            var id = squad.Members[k];
            if (!world.Characters.TryResolveNext(id, out int s) || world.Characters.Cold(s)?.Ai is not { } mind) continue;
            bool guards = mind.Goal?.Task.Type == TaskType.StandAtGuardNodeHometownOutside && !mind.HomeUnreachable;
            if (mind.TownPost is { } held && (!guards || world.Fixtures.IsDestroyed(held.Building)))
            {
                PostsTakenOf(held.Building).Remove(held.Index);
                mind.TownPost = null;
            }

            if (!guards || mind.TownPost is not null) continue;
            mind.TownPost = DrawPost(world, id, posts);
            if (mind.TownPost is { } got) PostsTakenOf(got.Building).Add(got.Index);
        }
    }

    /// <summary>
    /// One of <paramref name="posts"/> no one holds, drawn uniformly by the character's roll of this tick; null when all are held.
    /// </summary>
    SeatClaim? DrawPost(World world, CharacterId self, IReadOnlyList<SeatClaim> posts)
    {
        int free = 0;
        foreach (var p in posts) if (!PostHeld(p)) free++;
        if (free == 0) return null;
        ulong salt = SeedSalts.AiTarget ^ (ulong)world.Tick << 8 ^ (ulong)TaskType.StandAtGuardNodeHometownOutside;
        ulong roll = Rng.Hash(world.Seed, Rng.Key(self), RngPurpose.Think, salt);
        int pick = Rng.Int(roll, free);
        foreach (var p in posts)
            if (!PostHeld(p) && pick-- == 0) return p;
        return null;
    }

    /// <summary>
    /// The town posts the members hold, reserved for every squad before any draws (as <see cref="ReserveSeats"/>).
    /// </summary>
    void ReservePosts(World world)
    {
        foreach (var squad in world.Squads.All)
        {
            foreach (var id in squad.Members)
            {
                if (world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s)?.Ai?.TownPost is { } post)
                    PostsTakenOf(post.Building).Add(post.Index);
            }
        }
    }

    /// <summary>
    /// Whether someone holds <paramref name="post"/> in this cadence pass.
    /// </summary>
    bool PostHeld(SeatClaim post) => _postTaken.TryGetValue(post.Building, out var taken) && taken.Contains(post.Index);

    /// <summary>
    /// The guard posts of <paramref name="building"/> held in this cadence pass.
    /// </summary>
    HashSet<int> PostsTakenOf(TownBuilding building) => _postTaken.TryGetValue(building, out var taken) ? taken : _postTaken[building] = [];

    /// <summary>
    /// The guard posts of a building (its spots of kind <see cref="HomeSpotKind.Guard"/>), numbered as its spots.
    /// </summary>
    internal static IEnumerable<SeatClaim> GuardPosts(TownBuilding building)
    {
        for (int i = 0; i < building.Spots.Count; i++)
            if (building.Spots[i].Kind == HomeSpotKind.Guard) yield return new SeatClaim(building, i);
    }

    /// <summary>
    /// Whether a member standing in town <paramref name="site"/> may sit around there (the town gate of <c>FUN_1408caf00</c>): a non-player character
    /// needs the squad's home town or a town owned by the squad's faction, a player-side one a town owned by the player's faction (no home test).
    /// </summary>
    bool MaySitIn(World world, Blackboard board, Squad squad, int site, bool playerSide)
    {
        int owner = site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;
        if (playerSide) return owner >= 0 && owner == (world.Player.Faction >= 0 ? world.Player.Faction : data.PlayerFaction);
        return site == board.Town || owner >= 0 && owner == squad.Faction;
    }

    /// <summary>
    /// Whether sitting around takes a piece of furniture of this kind (bits CHAIR and THRONE of the mask; TRAINING too unless
    /// <paramref name="dummies"/> is false).
    /// </summary>
    internal static bool Sittable(HomeSpotKind kind, bool dummies) =>
        kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat || dummies && kind == HomeSpotKind.Training;

    /// <summary>
    /// Of the player's side: one of the player's characters or of the player's faction.
    /// </summary>
    static bool PlayerSide(World world, CharacterCold cold) => cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction;

    /// <summary>
    /// Marks every seat a squad's member holds from the last pass as taken, before any squad claims (the cadence pass; see <see cref="ClaimSeats"/>).
    /// </summary>
    void ReserveSeats(World world)
    {
        CollectUsedDummies(world);
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is not { } board) continue;
            foreach (var building in Buildings(board))
            {
                TakeOperatedSeats(world, building, TakenOf(building));
                TakeUsedDummies(world, building, TakenOf(building));
            }

            foreach (var spot in board.SeatClaims.Values) Reserve(world, board, spot);

            foreach (var claim in board.SitClaims.Values) TakenOf(claim.Building).Add(claim.Index);
        }
    }

    /// <summary>
    /// Marks a seat a member holds taken in the building it is in (the package's bar first, then the squad's own home).
    /// </summary>
    void Reserve(World world, Blackboard board, HomeSpot spot)
    {
        foreach (var building in Buildings(board))
        {
            if (SeatsOf(world, building).IndexOf(spot) is not (>= 0 and var i)) continue;
            TakenOf(building).Add(i);
            return;
        }
    }

    /// <summary>
    /// The kinds of furniture staying home (43) takes: the functions of the mask 0x2049a of <c>FUN_1408cbc00</c> (MINE, RESEARCH, REFINERY, TRAINING,
    /// CRAFTING, CHAIR; <b>Verified</b>).
    /// </summary>
    static bool StaysAt(HomeSpotKind kind) => kind is HomeSpotKind.Chair or HomeSpotKind.Training or HomeSpotKind.Workstation;

    /// <summary>
    /// Whether the home holds a piece staying home takes, its nest's pieces round it included (<see cref="SeatsOf"/>); with none the goal is not
    /// wanted (the finder's factor is 0, <c>FUN_1408cbc00</c>).
    /// </summary>
    internal static bool HasStayingFurniture(World world, TownBuilding? home)
    {
        if (home is null) return false;
        var seats = SeatsOf(world, home);
        for (int i = 0; i < seats.Count; i++)
            if (StaysAt(seats.At(i).Kind)) return true;
        return false;
    }

    /// <summary>
    /// A piece staying home takes, drawn at random from those no one claimed (the finder draws from its list, <c>FUN_1409fb020</c> with
    /// <c>FUN_1409b3b40</c>), a new draw at each pass (the roll keyed by the tick); -1: none free.
    /// </summary>
    static int FreeFurniture(World world, HomeSeats seats, HashSet<int> taken, CharacterId who)
    {
        int free = 0;
        for (int i = 0; i < seats.Count; i++)
            if (!taken.Contains(i) && StaysAt(seats.At(i).Kind)) free++;
        if (free == 0) return -1;

        ulong salt = SeedSalts.AiTarget ^ 0x43 ^ (ulong)world.Tick << 8;
        int pick = Rng.Int(Rng.Hash(world.Seed, Rng.Key(who), RngPurpose.Think, salt), free);
        for (int i = 0; i < seats.Count; i++)
            if (!taken.Contains(i) && StaysAt(seats.At(i).Kind) && pick-- == 0) return i;
        return -1;
    }

    /// <summary>
    /// The first free spot of <paramref name="kind"/> in <paramref name="bar"/>, or with <paramref name="near"/> the nearest free one to it; -1:
    /// none.
    /// </summary>
    static int FreeSeat(TownBuilding bar, HashSet<int> taken, HomeSpotKind kind, Vector3? near = null)
    {
        int best = -1;
        float bestD = float.MaxValue;
        for (int i = 0; i < bar.Spots.Count; i++)
        {
            if (bar.Spots[i].Kind != kind || taken.Contains(i)) continue;
            if (near is not { } p) return i;
            float d = Vector3.DistanceSquared(bar.Spots[i].Position, p);
            if (d < bestD) (best, bestD) = (i, d);
        }

        return best;
    }

    #endregion
}
