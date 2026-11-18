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

// Port of upstream/ProvingGrounds/plugin/src/TownCorpseCleanup.h/.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.6 (TW §2.11, CAP 12, 14, spike
// S9). The C++ decisions are Core/Glue/TownCorpseCleanupLogic.cs (fake-tested, Tests/Glue/TownCorpseCleanupLogicTests.cs) over
// ITownCorpseCleanupHost; this class binds them to a world and to CharacterIds. The state (C++ file-level globals, TownCorpseCleanup.cpp:21-47) is the
// logic's, one object per world: TownArena builds this object (built again for every world) and clears it through its AbandonWorldState chain
// (TownArena.cpp:1800-1806 -> AbandonWorldState :376-386).
//
// Engine calls of the C++ (E1) and what replaces them:
//   ou->player->getAllPlayerCharacters() (Player :49-54) ... PgEngine.IsPlayer
//   Character::isValid / isDead / isUnconcious / isInCombatMode(true,true) / isBeingCarried / isCarryingSomething / getCarryingObject ... PgEngine
//       Exists / IsDead / IsUnconscious / InCombatMode / IsCarried / IsCarrying / CarryingOf; ArenaMedical::NeedsStabilization ... ArenaMedical
//   getGameData()->stringID + TownSpectatorPolicy::RoleFor (IsCageHandler :56-61) ... PgEngine.RecordId (Cage Handler 99, census)
//   Building::getSpecialFunction() == BF_CORPSE_DISPOSAL, getBuildState()->isComplete, getUseableStuff()->isDisabled, getTown (Disposal :63-72) ...
//       Corpses.FurnacesNear / Find (TownBuilding.Function == 11, PgEngine's listing and completion; a town building has no disabled flag)
//   ou->getObjectsWithinSphere(arena, 2500, BUILDING, 1024) (DiscoverFurnace :153-167) ... Corpses.FurnacesNear (into one reused list)
//   ou->getObjectsWithinSphere(arena, 2500, CHARACTER, 512) / (arena, 175, CHARACTER, 256) ... PgEngine.CharactersNear (into one reused list)
//   c->getCurrentTownLocation() ... Towns.CurrentTownOf (TownKey)
//   getStandingOrder / setStandingOrder (HOLD, PASSIVE) ... PgEngine HoldPassive / SetStandingOrders
//   getMovementSpeedOrders / setDesiredSpeedOrders + restoreDesiredSpeed / setDesiredSpeed(RUN) ... PgEngine RunSpeedOf / SetRunSpeedMode / SetRunSpeed
//   addOrder(MOVE_CUS_ORDERED) + removeJob + clearAllAIGoals (RestoreNative :93-95) ... PgEngine.ClearOrders; reThinkCurrentAIAction ... the hold's
//       release (Ai.Replan)
//   TownAftercare::IsManaged / ReserveExternalOrders / ReleaseExternalOrders ... src/Town/TownAftercare.cs (the external table + the hold, owner External)
//   TownSpectators::ReleaseCharacter ... src/Town/TownSpectators.cs
//   addOrder(NULL, LIFT_PERSON_PLAYER_ORDER, corpse, ...) ... PgEngine.BeginLift (CarrySystem.BeginPickUp)
//   addOrder(furnace, FEED_CORPSE_INTO_MACHINE, furnace, ...) (:352-357) ... spike S9's fallback: PgEngine.MoveTo (a RUN walk to the furnace, every
//       15 s), Corpses.AtFurnace (the game's AtSite rule) and Corpses.Dispose (LetGo, the label floater, Uniques.Left, Characters.Remove: SlowWorld)
//   c->dropCarriedObject(true, false) (TickDeferredHandlers :115) ... PgEngine.LetGo
//   GetTickCount ... PgClock.NowMs; ou->isPaused ... false; PGLog::Debug ... PGLog.Debug (texts byte for byte, "%.1f" through CMath.FormatF)

/// <summary>The TownCorpseCleanup of one world: the logic and its state, bound to a world for each call (sim thread, serial steps only).</summary>
sealed class TownCorpseCleanup
{
    readonly PopulationData data;
    readonly PgClock clock;
    readonly Towns towns;
    readonly TownAftercare aftercare;
    readonly TownSpectators spectators;
    readonly Host host;
    readonly TownCorpseCleanupLogic<CharacterId> logic = new(CharacterId.None);
    /// <summary>Port only (the Finish backstop): every NPC this cleanup reserved in the external table, until it gave it back.</summary>
    readonly List<CharacterId> reserved = [];

    internal TownCorpseCleanup(PopulationData data, PgClock clock, Towns towns, TownAftercare aftercare, TownSpectators spectators)
    {
        this.data = data;
        this.clock = clock;
        this.towns = towns;
        this.aftercare = aftercare;
        this.spectators = spectators;
        host = new Host(this);
    }

    internal TownCorpseCleanupLogic<CharacterId> Logic => logic;

    /// <summary>Port only: the first throw of one corpse's step this world is logged (Meitou's log and pg_debug.log); every one is counted by the logic.</summary>
    bool corpseFailureLogged;

    ITownCorpseCleanupHost<CharacterId> H(World world) => host.Bind(world);

    // ---------- the public calls (TownCorpseCleanup.h) ----------
    internal void Begin(World world, ArenaBuildingRef arena, CharacterId[] fighters, int count) =>
        logic.Begin(H(world), arena, fighters, count);                                                    // C++ :219-229
    internal void Seal() => logic.Seal();                                                                // C++ :231
    internal int AdmitArenaDeaths(World world) => logic.AdmitArenaDeaths(H(world));                      // C++ :233-283
    internal void Tick(World world) => logic.Tick(H(world));                                            // C++ :291-358
    internal bool HasPending(World world) => logic.HasPending(H(world));                                // C++ :285-289
    internal string GetStatus() => logic.GetStatus();                                                   // C++ :360
    internal void Release(World world) => logic.Release(H(world));                                      // C++ :362-374

    /// <summary>C++ :376-386: no actor access (the reservations and holds are cleared by TownAftercare's and TownArena's own resets).</summary>
    internal void AbandonWorldState()
    {
        logic.AbandonWorldState();
        reserved.Clear();
    }

    /// <summary>Port only (the Finish backstop): every external reservation this cleanup took that is neither its handler nor a deferred handler is
    /// given back (a reservation a throw left behind). After a failed Release (<paramref name="all"/>), every one is given back.</summary>
    internal void ReleaseOrphanReservations(World world, bool all)
    {
        for (int i = reserved.Count - 1; i >= 0; i--)
        {
            CharacterId c = reserved[i];
            if (!all && logic.Keeps(c)) continue;
            reserved.RemoveAt(i);
            aftercare.ReleaseExternalOrders(world, c);
        }
    }

    /// <summary>ITownCorpseCleanupHost over one world, rebound for every call.</summary>
    sealed class Host(TownCorpseCleanup owner) : ITownCorpseCleanupHost<CharacterId>
    {
        World? world;
        readonly List<CharacterId> nearby = new(512);
        readonly List<CorpseFurnace> furnaces = new(4);
        (int, int) furnaceHint = (-1, -1);

        internal Host Bind(World w) { world = w; return this; }

        World W => world ?? throw new InvalidOperationException("TownCorpseCleanup used before a world was bound");

        // ---------- the world ----------
        public bool IsPaused => false;   // a paused world runs no SlowWorld (World.Interface.cs:16-40)
        public uint NowMs => owner.clock.NowMs;
        public void Debug(string line) => PGLog.Debug(line);

        // ---------- characters ----------
        public CharacterId None => CharacterId.None;
        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsPlayer(CharacterId c) => PgEngine.IsPlayer(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool InCombatMode(CharacterId c) => PgEngine.InCombatMode(W, c);
        public bool IsCarried(CharacterId c) => PgEngine.IsCarried(W, c);
        public bool IsCarrying(CharacterId c) => PgEngine.IsCarrying(W, c);
        public CharacterId CarryingOf(CharacterId c) => PgEngine.CarryingOf(W, c);
        public bool InBedOrCage(CharacterId body) => Corpses.InBedOrCage(W, body);
        public CharacterId CarrierOf(CharacterId body) => Corpses.CarrierOf(W, body);
        public bool NeedsStabilization(CharacterId c) => ArenaMedical.NeedsStabilization(W, c);
        public string RecordId(CharacterId c) => PgEngine.RecordId(W, c) ?? "";
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public string ActorText(CharacterId c) => CharacterIdText.Format(c);
        public Vector3 Position(CharacterId c) => PgEngine.Position(W, c) ?? Vector3.Zero;
        public long CurrentTown(CharacterId c) => Towns.Key(owner.towns.CurrentTownOf(W, c));
        public IReadOnlyList<CharacterId> CharactersInSphere(Vector3 origin, float radius, int max)
        {
            PgEngine.CharactersNear(W, origin, radius, max, nearby);
            return nearby;
        }

        // ---------- buildings ----------
        // the held arena resolved again only when its key changed, a building was ruined or rebuilt (RuinsRevision, as task 8.4c's bed scan) or 5 s
        // passed (ArenaIdentity.Resolve builds a PgBuilding record: not on every 500 ms poll; spec review 8.6)
        public PlacedBuilding? Resolve(ArenaBuildingRef held)
        {
            long revision = Beds.RuinsRevision(W);
            uint now = owner.clock.NowMs;
            if (arenaCached && arenaWorld == W && arenaRef == held && arenaRevision == revision && unchecked(now - arenaAt) < 5000) return arenaPlaced;
            arenaPlaced = ArenaIdentity.Resolve(W, owner.data, held, out var town) is { } b ? new PlacedBuilding(b, Towns.Key(town)) : null;
            arenaCached = true; arenaWorld = W; arenaRef = held; arenaRevision = revision; arenaAt = now;
            return arenaPlaced;
        }
        bool arenaCached;
        World? arenaWorld;
        ArenaBuildingRef arenaRef;
        long arenaRevision;
        uint arenaAt;
        PlacedBuilding? arenaPlaced;
        public CorpseFurnace? ResolveFurnace(string key) => Corpses.Find(W, owner.data, key, ref furnaceHint);
        public IReadOnlyList<CorpseFurnace> FurnacesInSphere(Vector3 origin, float radius, int max)
        {
            Corpses.FurnacesNear(W, owner.data, origin, radius, max, furnaces);
            return furnaces;
        }

        // ---------- other subsystems ----------
        public bool AftercareManages(CharacterId c) => owner.aftercare.IsManaged(W, c);
        public void SpectatorsReleaseCharacter(CharacterId c) => owner.spectators.ReleaseCharacter(W, c);
        public bool ReserveExternal(CharacterId c)
        {
            if (!owner.aftercare.ReserveExternalOrders(W, c)) return false;
            if (!owner.reserved.Contains(c)) owner.reserved.Add(c);
            return true;
        }
        public void ReleaseExternal(CharacterId c)
        {
            owner.aftercare.ReleaseExternalOrders(W, c);
            owner.reserved.Remove(c);
        }

        // ---------- orders ----------
        public (bool Hold, bool Passive) StandingOrders(CharacterId c) => PgEngine.HoldPassive(W, c);
        public void SetStandingOrders(CharacterId c, bool hold, bool passive) => PgEngine.SetStandingOrders(W, c, hold, passive);
        public byte SpeedOf(CharacterId c) => PgEngine.RunSpeedOf(W, c);
        public void SetSpeed(CharacterId c, byte speed) => PgEngine.SetRunSpeedMode(W, c, speed);
        public void SetRun(CharacterId c) => PgEngine.SetRunSpeed(W, c);
        public void ClearOrders(CharacterId c) => PgEngine.ClearOrders(W, c);
        public bool BeginLift(CharacterId c, CharacterId corpse) => PgEngine.BeginLift(W, c, corpse);
        public bool LetGo(CharacterId c) => PgEngine.LetGo(W, c);
        public void WalkTo(CharacterId c, Vector3 position) => PgEngine.MoveTo(W, c, position);
        public bool AtFurnace(CharacterId c, CorpseFurnace furnace) => Corpses.AtFurnace(W, c, furnace);
        public bool DisposalAvailable => Corpses.Available;
        public CorpseDisposalResult Dispose(CharacterId handler, CharacterId body, CorpseFurnace furnace, bool log)
        {
            string name = PgEngine.Name(W, body) ?? "";   // read before the body leaves the world (a reference, no copy)
            var result = Corpses.Dispose(W, handler, body, furnace);
            if (!log && result is (CorpseDisposalResult.NotCarried or CorpseDisposalResult.Failed)) return result;   // a repeat: logged once
            string actor = CharacterIdText.Format(body);
            // port only: the feed the C++'s engine does silently, for the scenario and the log
            PGLog.Debug("Proving Grounds: corpse cleanup fed name=" + name + " actor=" + actor + " handler=" + CharacterIdText.Format(handler) +
                " furnace=" + furnace.Key + " label=" + furnace.Label + " floater=" + (result == CorpseDisposalResult.Removed && furnace.Label.Length > 0
                && W.Floaters is not null ? "queued" : "none") + " result=" + result);
            return result;
        }
        public void CorpseFailed(Exception e)
        {
            if (owner.corpseFailureLogged) return;
            owner.corpseFailureLogged = true;
            PGLog.Debug("Proving Grounds: corpse cleanup step failed error=" + e.GetType().Name + ": " + e.Message);
            ProvingGroundsMod.Ctx?.LogError($"corpse cleanup step failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>pg town corpses' object (TEST ONLY): the status, the batch, the furnace, the handler, the corpses, the deferred handlers, the
    /// reservations and the counters.</summary>
    internal string CorpsesJson(World world, string failuresJson)
    {
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        static string B(bool b) => b ? "true" : "false";
        static string F(float f) => TestJson.G(f);   // task 8.8: NaN / infinity -> null
        static string V(Vector3 v) => "[" + F(v.X) + "," + F(v.Y) + "," + F(v.Z) + "]";
        static string Id(CharacterId c) => PGLog.Quote(CharacterIdText.Format(c));
        string Person(CharacterId c)
        {
            if (c.IsNone) return "null";
            var pos = PgEngine.Position(world, c);
            return "{\"id\":" + Id(c) + ",\"exists\":" + B(PgEngine.Exists(world, c)) + ",\"name\":" + PGLog.Quote(PgEngine.Name(world, c) ?? "")
                + ",\"record\":" + PGLog.Quote(PgEngine.RecordId(world, c) ?? "") + ",\"dead\":" + B(PgEngine.IsDead(world, c))
                + ",\"carried\":" + B(PgEngine.IsCarried(world, c)) + ",\"carrying\":" + (PgEngine.CarryingOf(world, c) is { IsNone: false } k ? Id(k) : "null")
                + ",\"pos\":" + (pos is { } p ? V(p) : "null") + ",\"task\":" + PGLog.Quote(PgEngine.TaskText(world, c))
                + ",\"engaged\":" + B(PgEngine.IsEngaged(world, c)) + "}";
        }
        var sb = new StringBuilder(2048);
        sb.Append(",\"corpses\":{\"status\":").Append(PGLog.Quote(logic.GetStatus()))
          .Append(",\"active\":").Append(B(logic.Active)).Append(",\"sealed\":").Append(B(logic.Sealed))
          .Append(",\"pending\":").Append(B(logic.HasPending(H(world))))
          .Append(",\"batchStarted\":").Append(B(logic.BatchStarted)).Append(",\"batchExpired\":").Append(B(logic.BatchExpired))
          .Append(",\"batchElapsed\":").Append(F(logic.BatchElapsed)).Append(",\"current\":").Append(N(logic.Current))
          .Append(",\"action\":").Append(PGLog.Quote(logic.LastAction.ToString())).Append(",\"directed\":").Append(B(logic.Directed))
          .Append(",\"available\":").Append(B(Corpses.Available))
          .Append(",\"disposed\":").Append(N(logic.Disposed)).Append(",\"liftRefused\":").Append(N(logic.LiftRefused))
          .Append(",\"disposeFailures\":").Append(N(logic.DisposeFailures)).Append(",\"corpseFailures\":").Append(N(logic.CorpseFailures))
          .Append(",\"takenElsewhere\":").Append(N(logic.TakenElsewhere)).Append(",\"failures\":").Append(failuresJson)
          .Append(",\"furnace\":");
        if (logic.Furnace is { } f)
            sb.Append("{\"key\":").Append(PGLog.Quote(f.Key)).Append(",\"record\":").Append(PGLog.Quote(f.Record)).Append(",\"pos\":").Append(V(f.Position)).Append(",\"town\":").Append(N(f.Town))
              .Append(",\"complete\":").Append(B(f.Complete)).Append(",\"label\":").Append(PGLog.Quote(f.Label))
              .Append(",\"useRange\":").Append(F(f.UseRange)).Append('}');
        else sb.Append("null");
        sb.Append(",\"handler\":").Append(Person(logic.Handler));
        if (!logic.Handler.IsNone && logic.Furnace is { } hf)
            sb.Append(",\"handlerAtFurnace\":").Append(B(Corpses.AtFurnace(world, logic.Handler, hf)));
        sb.Append(",\"bodies\":[");
        for (int i = 0; i < logic.CorpseCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"resolved\":").Append(B(logic.CorpseResolved(i))).Append(",\"actor\":").Append(Person(logic.CorpseAt(i))).Append('}');
        }
        sb.Append("],\"deferred\":[");
        for (int i = 0; i < logic.DeferredCount; i++) { if (i > 0) sb.Append(','); sb.Append(Id(logic.DeferredAt(i))); }
        sb.Append("],\"reserved\":[");
        for (int i = 0; i < reserved.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Id(reserved[i])); }
        sb.Append("]}");
        return sb.ToString();
    }
}
