using System.Globalization;
using System.Numerics;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownSpectators.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.2 (TW §2.7, CAP 5, 16, 19, spikes
// S10 and S16). The C++ decisions are Core/Glue/TownSpectatorsLogic.cs (fake-tested, Tests/Glue/TownSpectatorsLogicTests.cs) over
// ITownSpectatorsHost; this class binds them to a world and to CharacterIds. The state (C++ file-level globals, TownSpectators.cpp:28-54) is the
// logic's, one object per world: TownArena builds this object (built again for every world) and clears it through its AbandonWorldState chain
// (TownArena.cpp:1800-1806 -> AbandonWorldState :356-359). Core's class of this name is Core.TownSpectators (the chatter); in this namespace write
// Core.TownSpectators for it.
//
// Engine calls of the C++ (E1) and what replaces them:
//   ou->player->getAllPlayerCharacters() (Player :56-61) ... PgEngine.IsPlayer (CharacterCold.IsPlayer)
//   Character::isValid / isDead / isUnconcious / isInCombatMode(true,true) / isBeingCarried / isCarryingSomething (Healthy :62-66) ... PgEngine
//       Exists / IsDead / IsUnconscious / InCombatMode / IsCarried / IsCarrying; ArenaMedical::NeedsStabilization ... ArenaMedical.NeedsStabilization
//   Building / UseableStuff: Seat :71-81, Occupied :67-70, getOccupant, isFreeSlot, couldIOperate, stopOperating ... src/Engine/Seats.cs (the town
//       seat "<pillow key>@seat0", its occupants and claims in one scan, CanSit, StandUp)
//   ArenaIdentity::GetStringId(b), getSpecialFunction() == BF_CHAIR, getBuildState()->isComplete ... ArenaBuilding.RecordId == 192 (a pillow is its own
//       town building whose one seat is its chair: the function is implied), ArenaBuilding.Finished
//   getStandingOrder / setStandingOrder (HOLD, PASSIVE), getMovementSpeedOrders / setDesiredSpeedOrders / restoreDesiredSpeed, clearAllAIGoals +
//       reThinkCurrentAIAction (Invite :129-134), ResumeNativeAI (addOrder + removeJob + clearAllAIGoals + reThinkCurrentAIAction :82-87) ... the
//       world's hold registry with the owner Spectator (src/Engine/Holds.cs, spike S10: Engaged on the first hold, Engaged off + Ai.Replan when the
//       last owner lets go); the saved orders and speed have no counterpart (docs/divergences.md "TownSpectators (task 8.2)")
//   c->addOrder(seat, seat->getDefaultTask(), seat, false, true, pos) + setDesiredSpeedOrders(RUN) / setDesiredSpeed(RUN) (Tick :475-477) ...
//       Seats.Sit (the implicit SitAround Operate task, source Npc: it walks at SpeedMode.Free, the C++'s RUN, spike S16)
//   c->getPermajobCount() (Begin :421) ... HasDuty (controller decision 8.2: Meitou's cold.Jobs is empty for every town NPC; a post or duty goal
//       stands for it: AiMind.Behaviour Post / Work / Turret, AiMind.TownPost, AiMind.Engine, a TownDutySystem duty of the character)
//   c->getCurrentTownLocation(), b->getTown() ... Towns.CurrentTownOf / PlacedBuilding.Town (TownKey)
//   ou->getObjectsWithinSphere(arena, 2500, BUILDING, 2048 / CHARACTER, 512) ... PgEngine.BuildingsNear + ArenaIdentity.From / PgEngine.CharactersNear
//   ou->getTimeStamp_inGameHours().getTotalHours() ... World.GameHours; GetTickCount ... PgClock.NowMs; ou->isPaused ... false
//   SparSession::IsActive / GetMode / GetActiveA / GetActiveB / GetParticipantCount / GetParticipant / IsEliminated ... SparSession
//   TownArena::IsFighter ... TownArena.IsFighter; Character::getName ... PgEngine.Name
//   Character::say(text) ... PgEngine.Bark (CAP 16) + the test-only PG-TOWN say line (src/Town/TownSpeech.cs, by "crowd")
//   PGLog::Debug ... PGLog.Debug (texts byte for byte; "%.1f" through CMath.FormatF)

/// <summary>The TownSpectators of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed class TownSpectators
{
    readonly PopulationData data;
    readonly PgClock clock;
    readonly Holds holds;
    readonly Towns towns;
    readonly TownArena arena;
    readonly Func<SparSession?> spar;
    readonly Host host;
    readonly TownSpectatorsLogic<CharacterId> logic = new(CharacterId.None);

    internal TownSpectators(PopulationData data, PgClock clock, Holds holds, Towns towns, TownArena arena, Func<SparSession?> spar)
    {
        this.data = data;
        this.clock = clock;
        this.holds = holds;
        this.towns = towns;
        this.arena = arena;
        this.spar = spar;
        host = new Host(this);
    }

    internal TownSpectatorsLogic<CharacterId> Logic => logic;

    ITownSpectatorsHost<CharacterId> H(World world) => host.Bind(world);

    // ---------- the public calls (TownSpectators.h) ----------
    internal void Begin(World world, ArenaBuildingRef arenaRef) => logic.Begin(H(world), arenaRef, PGConfig.PerformanceProfile());   // C++ :360-447
    internal void Tick(World world) => logic.Tick(H(world));                                              // C++ :448-482
    internal void ReactToResult(World world, SparPodium.Snapshot result) => logic.ReactToResult(H(world), result);   // C++ :311-335
    /// <summary>C++ :336-350: task 8.6 calls it for the cage handler it takes from the audience.</summary>
    internal void ReleaseCharacter(World world, CharacterId c) => logic.ReleaseCharacter(H(world), c);
    internal void Release(World world) => logic.Release(H(world));                                        // C++ :351-355
    internal void ResetAfterFailedRelease() => logic.ResetAfterFailedRelease();                           // port only
    internal void AbandonWorldState() => logic.AbandonWorldState();                                       // C++ :356-359

    /// <summary>Port only (the Finish backstop): every Spectator hold of an NPC that is neither a guest nor waiting in the pending list is let go
    /// (a hold a throw left behind); the pending ones keep theirs until they are healthy (the C++'s deferred resume).</summary>
    internal void ReleaseOrphanHolds(World world)
    {
        orphans.Clear();
        var registry = holds.Registry;
        for (int i = 0; i < registry.Count; i++)
        {
            var (id, owners) = registry.At(i);
            if ((owners & HoldOwner.Spectator) == 0 || logic.IsPending(id) || IsGuest(id) || id == testSitter) continue;   // the test sitter: pg town stand
            orphans.Add(id);
        }
        for (int i = 0; i < orphans.Count; i++) holds.Release(world, orphans[i], HoldOwner.Spectator);
    }

    readonly List<CharacterId> orphans = [];

    bool IsGuest(CharacterId c)
    {
        var g = logic.Guests;
        for (int i = 0; i < g.Count; i++) if (g[i].Actor == c) return true;
        return false;
    }

    // ---------- the permanent-job stand-in (controller decision 8.2) ----------

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<World,
        System.Runtime.CompilerServices.StrongBox<Meitou.Simulation.Kidnap.TownDutySystem?>> dutiesOf = new();

    static Meitou.Simulation.Kidnap.TownDutySystem? Duties(World world) =>
        dutiesOf.GetValue(world, static w => new(w.Systems.OfType<Meitou.Simulation.Kidnap.TownDutySystem>().FirstOrDefault())).Value;

    /// <summary>The C++ <c>getPermajobCount() &gt; 0</c>'s stand-in: a post or duty goal (AiMind.Behaviour Post, Work or Turret while it has a goal;
    /// a town guard post, AiMind.TownPost; an engine mind, AiMind.Engine; a town duty of the character running, TownDutySystem.Duties).</summary>
    internal static bool HasDuty(World world, CharacterId c)
    {
        if (!world.Characters.TryResolveNext(c, out int slot) || world.Characters.Cold(slot) is not { } cold) return false;
        if (cold.Ai is { } mind)
        {
            if (mind.Engine || mind.TownPost is not null) return true;
            if (mind.Goal is not null && mind.Behaviour is Meitou.Simulation.Ai.AiBehaviour.Post or Meitou.Simulation.Ai.AiBehaviour.Work
                    or Meitou.Simulation.Ai.AiBehaviour.Turret) return true;
        }
        if (Duties(world) is { } duties)
        {
            var list = duties.Duties;
            for (int i = 0; i < list.Count; i++) if (list[i].Member == c) return true;
        }
        return false;
    }

    /// <summary>ITownSpectatorsHost over one world, rebound for every call.</summary>
    sealed class Host(TownSpectators owner) : ITownSpectatorsHost<CharacterId>
    {
        World? world;
        readonly List<string> seatIds = [], seatBuildings = [];
        readonly List<Vector3> seatPositions = [];
        readonly Dictionary<string, int> seatIndex = new(StringComparer.Ordinal);
        CharacterId[] occupants = new CharacterId[120];
        bool[] claims = new bool[120];

        internal Host Bind(World w) { world = w; return this; }

        World W => world ?? throw new InvalidOperationException("TownSpectators used before a world was bound");

        // ---------- the world ----------
        public bool IsPaused => false;   // a paused world runs no SlowWorld (World.Interface.cs:16-40)
        public uint NowMs => owner.clock.NowMs;
        public double TotalHours => W.GameHours;
        public void Debug(string line) => PGLog.Debug(line);

        // ---------- characters ----------
        public CharacterId None => CharacterId.None;
        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsPlayer(CharacterId c) => PgEngine.IsPlayer(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool InCombatMode(CharacterId c) => PgEngine.InCombatMode(W, c);
        public bool NeedsStabilization(CharacterId c) => ArenaMedical.NeedsStabilization(W, c);
        public bool IsCarried(CharacterId c) => PgEngine.IsCarried(W, c);
        public bool IsCarrying(CharacterId c) => PgEngine.IsCarrying(W, c);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public string RecordId(CharacterId c) => PgEngine.RecordId(W, c) ?? "";
        public long CurrentTown(CharacterId c) => Towns.Key(owner.towns.CurrentTownOf(W, c));
        public bool IsTownFaction(CharacterId c) => owner.arena.IsTownFactionCharacter(W, c);
        public bool HasDuty(CharacterId c) => TownSpectators.HasDuty(W, c);
        public bool HeldByAnotherOwner(CharacterId c) => (owner.holds.OwnersOf(c) & ~HoldOwner.Spectator) != HoldOwner.None;
        public bool IsTownFighter(CharacterId c) => owner.arena.IsFighter(W, c);
        public bool CanSit(CharacterId c) => Seats.CanSit(W, c);

        // ---------- buildings and seats ----------
        public PlacedBuilding? Resolve(ArenaBuildingRef held) =>
            ArenaIdentity.Resolve(W, owner.data, held, out var town) is { } b ? new PlacedBuilding(b, Towns.Key(town)) : null;

        public IReadOnlyList<PlacedBuilding>? BuildingsInSphere(Vector3 origin, float radius, int max)
        {
            var production = PgEngine.Production(W);
            var found = PgEngine.BuildingsNear(W, owner.data, origin, radius, null, max);
            var list = new List<PlacedBuilding>(found.Count);
            foreach (var b in found) list.Add(new PlacedBuilding(ArenaIdentity.From(b, production), Towns.Key(b.Town)));
            return list;
        }

        public IReadOnlyList<CharacterId>? CharactersInSphere(Vector3 origin, float radius, int max) => PgEngine.CharactersNear(W, origin, radius, max);

        public SpectatorSeat? SeatOf(ArenaBuilding building)
        {
            if (building.Source != ArenaBuildingSource.Town || !TownArenaRuntimePolicy.IdEquals(building.RecordId, PgRecords.ArenaPillow) || !building.Finished)
                return null;
            return Seats.FirstOf(W, building.Key) is { } s ? new SpectatorSeat(s.Id, s.Building, s.Position) : null;
        }

        public bool SeatExists(string seat) => Seats.Find(W, seat) is not null;

        public void ReadSeats(IReadOnlyList<SpectatorSeat> seats, CharacterId[] occ, bool[] claimed, bool boardClaims)
        {
            seatIds.Clear(); seatBuildings.Clear(); seatPositions.Clear();
            for (int i = 0; i < seats.Count; i++) { seatIds.Add(seats[i].Id); seatBuildings.Add(seats[i].Building); seatPositions.Add(seats[i].Position); }
            Seats.Occupants(W, seatIds, seatBuildings, seatPositions, seatIndex, occ, claimed, boardClaims);
        }

        public bool SittingOn(CharacterId c, string seat) => Seats.SittingOn(W, c, seat);

        // ---------- orders ----------
        public bool Hold(CharacterId c) => owner.holds.Hold(W, c, HoldOwner.Spectator);
        public void ReleaseHold(CharacterId c) => owner.holds.Release(W, c, HoldOwner.Spectator);
        public bool Sit(CharacterId c, string seat) => Seats.Sit(W, c, seat);
        public bool OnItsWay(CharacterId c, string seat) => Seats.OnItsWay(W, c, seat);
        public void StandUp(CharacterId c, string seat) => Seats.StandUp(W, c, seat);

        // ---------- the match ----------
        public bool SparIsActive => owner.spar() is { } s && s.IsActive(W);
        public bool SparTeams1v1 => owner.spar() is { } s && s.GetMode() == MatchRules.MatchMode.Teams1v1;
        public CharacterId SparActiveA => owner.spar()?.GetActiveA() ?? CharacterId.None;
        public CharacterId SparActiveB => owner.spar()?.GetActiveB() ?? CharacterId.None;
        public int SparParticipantCount => owner.spar()?.GetParticipantCount() ?? 0;
        public CharacterId SparParticipant(int i) => owner.spar()?.GetParticipant(i) ?? CharacterId.None;
        public bool SparIsEliminated(CharacterId c) => owner.spar() is { } s && s.IsEliminated(W, c);
        public void Say(CharacterId c, string line) => TownSpeech.Say(W, c, "crowd", line);

        // the test verbs' reads (no allocation concerns: test only)
        internal (CharacterId[] Occupants, bool[] Claimed) ReadForTest(IReadOnlyList<SpectatorSeat> seats)
        {
            if (occupants.Length < seats.Count) { occupants = new CharacterId[seats.Count]; claims = new bool[seats.Count]; }
            for (int i = 0; i < seats.Count; i++) { occupants[i] = CharacterId.None; claims[i] = false; }
            ReadSeats(seats, occupants, claims, boardClaims: true);
            return (occupants, claims);
        }
    }

    // ---------- TEST ONLY: pg town crowd / sit / stand ----------

    /// <summary>The test sitter of <c>pg town sit</c> (spike S16) and its seat; held with the owner Spectator like a guest.</summary>
    CharacterId testSitter = CharacterId.None;
    string? testSeat;

    /// <summary>TEST ONLY <c>pg town sit [id]</c> (spike S16): the given NPC (default: the first healthy Bloodstamp civilian, bar resident or cage
    /// handler of the registry's town that nobody holds and that has no duty) is held (owner Spectator) and ordered to the nearest free Arena Pillow
    /// seat (Seats.Sit). Null on success, else why not.</summary>
    internal string? SitForTest(World world, Vector3 centre, long town, IReadOnlyList<string> args)
    {
        var h = (Host)H(world);
        if (!testSitter.IsNone) return "a test sitter is already out: pg town stand first";
        var seats = PillowSeats(h, centre, town);
        if (seats.Count == 0) return "no Arena Pillow seat in the town";
        var (occ, claimed) = h.ReadForTest(seats);
        CharacterId c = CharacterId.None;
        if (args.Count > 1 && args[1] != "auto")
        {
            if (!CharacterIdText.TryParse(args[1], out c)) return "bad id " + args[1];
        }
        else
        {
            foreach (var x in PgEngine.CharactersNear(world, centre, TownSpectatorsLogic<CharacterId>.SearchRadius, 512))
            {
                string r = PgEngine.RecordId(world, x) ?? "";
                if (TownSpectatorPolicy.RoleFor(r) == TownSpectatorPolicy.AudienceRole.NotAudience || !h.IsTownFaction(x) || PgEngine.IsPlayer(world, x)
                    || holds.IsHeld(x) || HasDuty(world, x) || !TownKey.Same(h.CurrentTown(x), town) || PgEngine.IsDead(world, x)
                    || PgEngine.IsUnconscious(world, x) || PgEngine.InCombatMode(world, x) || !Seats.CanSit(world, x)) continue;
                c = x; break;
            }
            if (c.IsNone) return "no free candidate NPC in the town";
        }
        var pos = PgEngine.Position(world, c);
        if (pos is null) return "the NPC does not exist";
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < seats.Count; i++)
        {
            if (!occ[i].IsNone || claimed[i]) continue;
            float d = Vector3.DistanceSquared(seats[i].Position, pos.Value);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return "no free Arena Pillow seat";
        if (!holds.Hold(world, c, HoldOwner.Spectator)) return "the hold was refused";
        if (!Seats.Sit(world, c, seats[best].Id)) { holds.Release(world, c, HoldOwner.Spectator); return "the sit task was refused (TaskStart.Start)"; }
        testSitter = c; testSeat = seats[best].Id;
        return null;
    }

    /// <summary>TEST ONLY <c>pg town stand</c>: the test sitter stands up (Seats.StandUp) and the Spectator hold is let go.</summary>
    internal string? StandForTest(World world)
    {
        if (testSitter.IsNone || testSeat is null) return "no test sitter";
        Seats.StandUp(world, testSitter, testSeat);
        holds.Release(world, testSitter, HoldOwner.Spectator);
        testSitter = CharacterId.None; testSeat = null;
        return null;
    }

    /// <summary>The world changed: the test sitter is forgotten with the holds.</summary>
    internal void ForgetTestSitter() { testSitter = CharacterId.None; testSeat = null; }

    List<SpectatorSeat> PillowSeats(Host h, Vector3 centre, long town)
    {
        var list = new List<SpectatorSeat>();
        foreach (var b in h.BuildingsInSphere(centre, TownSpectatorsLogic<CharacterId>.SearchRadius, TownSpectatorsLogic<CharacterId>.BuildingMax) ?? [])
            if (TownKey.Same(b.Town, town) && h.SeatOf(b.Building) is { } s) list.Add(s);
        return list;
    }

    /// <summary>pg town crowd's object: the guests (seat, seated, occupant, task, held, distance to the seat), the pending releases, the chatter state,
    /// the stand-in counts, the test sitter.</summary>
    internal string CrowdJson(World world, string failuresJson)
    {
        var h = (Host)H(world);
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        static string F(float f) => TestJson.G(f);   // task 8.8: NaN / infinity -> null
        static string V(Vector3 v) => "[" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + "]";
        static string Id(CharacterId c) => PGLog.Quote(CharacterIdText.Format(c));
        var guests = logic.Guests;
        var seats = new List<SpectatorSeat>();
        foreach (var g in guests) seats.Add(g.Seat);
        if (!testSitter.IsNone && testSeat is { } ts && Seats.Find(world, ts) is { } tss) seats.Add(new SpectatorSeat(tss.Id, tss.Building, tss.Position));
        var (occ, _) = h.ReadForTest(seats);
        string Person(CharacterId c, SpectatorSeat seat, CharacterId occupant, string extra)
        {
            var pos = PgEngine.Position(world, c);
            float dist = pos is { } p ? MathF.Sqrt((p.X - seat.Position.X) * (p.X - seat.Position.X) + (p.Z - seat.Position.Z) * (p.Z - seat.Position.Z)) : -1f;
            string record = PgEngine.RecordId(world, c) ?? "";
            return "{\"id\":" + Id(c) + ",\"record\":" + PGLog.Quote(record) + ",\"catalogFighter\":" + B(TownArenaRuntimePolicy.IsRegularFighter(record))
                + ",\"seat\":" + PGLog.Quote(seat.Id) + ",\"seatRecord\":" + PGLog.Quote(Seats.Find(world, seat.Id)?.Record ?? "")
                + ",\"seatBuildingRecord\":" + PGLog.Quote(BuildingRecord(seat.Building))
                + ",\"seatBuilding\":" + PGLog.Quote(seat.Building) + ",\"seatPos\":" + V(seat.Position) + extra
                + ",\"occupant\":" + (occupant.IsNone ? "null" : Id(occupant)) + ",\"sitting\":" + B(Seats.SittingOn(world, c, seat.Id))
                + ",\"onItsWay\":" + B(Seats.OnItsWay(world, c, seat.Id)) + ",\"task\":" + PGLog.Quote(PgEngine.TaskText(world, c))
                + ",\"row\":" + (PgEngine.Running(world, c) is { } run ? PGLog.Quote(run.Row.ToString()) : "null")
                + ",\"pos\":" + (pos is { } pp ? V(pp) : "null") + ",\"dist\":" + F(dist)
                + ",\"held\":" + B(holds.IsHeld(c)) + ",\"owners\":" + PGLog.Quote(holds.OwnersOf(c).ToString())
                + ",\"engaged\":" + B(PgEngine.IsEngaged(world, c)) + "}";
        }
        string BuildingRecord(string key)
        {
            (int, int) hint = (-1, -1);
            return PgEngine.FindBuilding(world, data, PgEngine.BuildingSource.Town, key, ref hint)?.RecordId ?? "";
        }
        var sb = new StringBuilder(4096);
        var policy = logic.ActivePolicy;
        sb.Append(",\"crowd\":{\"profile\":").Append(PGLog.Quote(ArenaPerformance.PersistedName(PGConfig.PerformanceProfile())))
          .Append(",\"cap\":").Append(N(policy.SpectatorCap)).Append(",\"maintenanceMs\":").Append(N(policy.MaintenanceMs))
          .Append(",\"chatterEnabled\":").Append(B(policy.ChatterEnabled)).Append(",\"voices\":").Append(N(policy.MaxChatterVoices))
          .Append(",\"chatterMin\":").Append(F(policy.ChatterMinSeconds)).Append(",\"chatterMax\":").Append(F(policy.ChatterMaxSeconds))
          .Append(",\"arena\":").Append(logic.ActiveArena is { } a ? PGLog.Quote(ArenaIdentity.KeyText(a)) : "null")
          .Append(",\"elapsed\":").Append(F(logic.Elapsed)).Append(",\"chatterActive\":").Append(B(logic.ChatterActive))
          .Append(",\"cues\":").Append(N(logic.CueCount))
          .Append(",\"excludedByDuty\":").Append(N(logic.ExcludedByDuty)).Append(",\"excludedByHold\":").Append(N(logic.ExcludedByHold))
          .Append(",\"holdRefused\":").Append(N(logic.HoldRefused)).Append(",\"sitRefused\":").Append(N(logic.SitRefused)).Append(",\"guestFailures\":").Append(N(logic.GuestFailures))
          .Append(",\"failures\":").Append(failuresJson)
          .Append(",\"guests\":[");
        int seatedCount = 0;
        for (int i = 0; i < guests.Count; i++)
        {
            if (guests[i].Seated) seatedCount++;
            if (i > 0) sb.Append(',');
            sb.Append(Person(guests[i].Actor, guests[i].Seat, occ[i], ",\"seated\":" + B(guests[i].Seated)));
        }
        sb.Append("],\"seated\":").Append(N(seatedCount)).Append(",\"pending\":[");
        var pend = logic.PendingReleases;
        for (int i = 0; i < pend.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Id(pend[i])); }
        sb.Append("],\"spectatorHolds\":[");
        bool first = true;
        var registry = holds.Registry;
        for (int i = 0; i < registry.Count; i++)
        {
            var (id, owners) = registry.At(i);
            if ((owners & HoldOwner.Spectator) == 0) continue;
            if (!first) sb.Append(','); first = false;
            sb.Append(Id(id));
        }
        sb.Append(']');
        sb.Append(",\"testSitter\":");
        if (!testSitter.IsNone && testSeat is not null && seats.Count > guests.Count)
            sb.Append(Person(testSitter, seats[guests.Count], occ[guests.Count], ""));
        else sb.Append("null");
        sb.Append('}');
        return sb.ToString();
    }
}
