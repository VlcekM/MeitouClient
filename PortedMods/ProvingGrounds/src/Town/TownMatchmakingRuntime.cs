using System.Globalization;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// Port of the engine half of upstream/ProvingGrounds/plugin/src/TownMatchmakingRuntime.h/.cpp (Proving Grounds by Shutori, GPLv3) and of
// TownArena.cpp's Ready (:297-346), task 7.2 (TW §2.2 steps 2-3, §2.3; CAP 2, 6, 11; census docs/census.md). The decisions are
// Core/Glue/TownMatchmakingRuntimeLogic.cs (Collect, Resolve, Label) and Core/Glue/TownArenaLogic.Matchmaking.cs (Ready, SnapshotFor, the preview's
// throttle and frozen roster); this file is TownArena's host half for them: the character reads, and the TEST ONLY pg town roster / search verbs.
// Meitou 0.17.3 (fa34a1b); paths relative to its repo root.
//
// Engine calls of the C++ (E1) and what replaces them:
//   ou->getObjectsWithinSphere(objects, registry pos, 2500, CHARACTER, 512, NULL) ... PgEngine.CharactersNear(world, pos, 2500, 512) (slot order;
//       the C++ order is the engine's, and Collect sorts what it returns: npcs and players by id, the maps by key)
//   Character::isValid / getCurrentTownLocation / getGameData()->stringID / getName ... PgEngine.Exists / Towns.CurrentTownOf (the status town) / RecordId / Name
//   census filter (port only): PgEngine.Faction == the index of faction "91-Proving Grounds.mod" (Bloodstamp) in PopulationData.Factions
//   FighterIdentity::GetOrCreate / Find ... src/Game/FighterIdentity.cs (task 2.3)
//   LeaderboardStore::RemoveTownNpcProgression / GetRating(c, Town) / HasActiveSave ... src/Game/LeaderboardStore.cs (task 5.1)
//   ArenaCombatProfile::Evaluate(ArenaCombatProfile::Read(c)) ... src/Game/ArenaCombatProfile.cs (Read) + Core.ArenaCombatProfile.Evaluate
//   hand(c) {type, container, containerSerial, index, serial} -> TownArenaRuntimePolicy::PlayerSelectionKey ... PlayerSelectionKey(0, 0, 0, slot,
//       generation) of the CharacterId (Meitou's live handle; the C++ hand's type and container fields have no counterpart: 0; session-only key)
//   TownAftercare::IsManaged ... src/Town/TownAftercare.cs (task 8.4a); TownLimbShop::IsManaged ... src/Town/TownLimbShop.cs (task 8.5)
//   TownArena::IsFighter ... TownArenaLogic.IsFighter
//   isCarryingSomething / isBeingCarried ... PgEngine.IsCarrying / IsCarried
//   Ready: isDead / isUnconcious ... PgEngine.IsDead / IsUnconscious; isInCombatMode(true, true) ... PgEngine.InCombatMode;
//       ArenaMedical::NeedsStabilization ... ArenaMedical.NeedsStabilization (task 4.7); RosterStatus::Read ... PgEngine.RosterHealth;
//       the medic's native capacity (isInBloodlossTrauma, isProbablyDying, isCrippled, hasAnArmToFightWith) ... PgEngine.MedicallyAble;
//       ou->player->getAllPlayerCharacters() ... PgEngine.PlayerCharacters (the host's existing member)

sealed partial class TownArena
{
    /// <summary>The town faction's record id (census: every Proving Grounds person in Scratch is Bloodstamp).</summary>
    internal const string TownFactionId = "91-Proving Grounds.mod";
    int townFaction = -2;   // the index of TownFactionId in PopulationData.Factions; -2 = not looked up, -1 = absent

    int TownFaction()
    {
        if (townFaction != -2) return townFaction;
        townFaction = -1;
        for (int i = 0; i < data.Factions.Count; i++)
            if (data.Factions[i].Id == TownFactionId) { townFaction = i; break; }
        if (townFaction < 0) ProvingGroundsMod.Ctx?.Log($"town faction {TownFactionId} not found: the town roster is empty");
        return townFaction;
    }

    sealed partial class Host
    {
        // ---------- task 7.2: the roster reads (ITownRosterHost) ----------
        public IReadOnlyList<CharacterId>? CharactersInSphere(System.Numerics.Vector3 origin, float radius, int max) =>
            world is null ? null : PgEngine.CharactersNear(W, origin, radius, max);
        public bool IsTownFaction(CharacterId c) => owner.TownFaction() is int f && f >= 0 && PgEngine.Faction(W, c) == f;
        public string Role(CharacterId c) => PgEngine.RecordId(W, c) ?? "";
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public bool GetOrCreateIdentity(CharacterId c, out string id) => owner.identity().GetOrCreate(W, c, out id);
        public bool FindIdentity(CharacterId c, out string id) => owner.identity().Find(W, c, out id);
        public void RemoveTownNpcProgression(CharacterId c) => owner.store().RemoveTownNpcProgression(W, c);
        public string SelectionIdentity(CharacterId c) =>
            c.IsNone || !PgEngine.Exists(W, c) ? "" : TownArenaRuntimePolicy.PlayerSelectionKey(0, 0, 0, (uint)c.Slot, (uint)c.Generation);
        public double Ability(CharacterId c) => Game.ArenaCombatProfile.Ability(W, c);
        public double TownRating(CharacterId c) => owner.store().GetRating(W, c, LeaderboardData.Kind.Town);
        public bool AftercareManaged(CharacterId c) => owner.aftercare.IsManaged(W, c);   // C++ TownAftercare::IsManaged (task 8.4a)
        public bool LimbShopManaged(CharacterId c) => owner.limbShop.IsManaged(W, c);
        public bool TownArenaFighter(CharacterId c) => owner.logic.IsFighter(this, c);
        public bool IsCarrying(CharacterId c) => PgEngine.IsCarrying(W, c);
        public bool IsCarried(CharacterId c) => PgEngine.IsCarried(W, c);

        // ---------- task 7.2: readiness (TownArena.cpp:297-346) ----------
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool InCombatMode(CharacterId c) => PgEngine.InCombatMode(W, c);
        public bool NeedsStabilization(CharacterId c) => ArenaMedical.NeedsStabilization(W, c);
        public bool ReadHealth(CharacterId c, out RosterStatus.Snapshot health, out string error) => PgEngine.RosterHealth(W, c, out health, out error);
        public bool MedicallyAble(CharacterId c) => PgEngine.MedicallyAble(W, c);
        public bool HasActiveSave => owner.store().HasActiveSave();
    }

    // ---------- TEST ONLY (task 7.2): pg town roster | search | search-poll ----------

    /// <summary>The player characters standing in the bound registry's town, in the engine's order (the test verbs' lineup).</summary>
    List<CharacterId> PlayersInRegistryTown(World world)
    {
        var h = H(world);
        var list = new List<CharacterId>();
        if (logic.State.TownRegistry is not { } r || h.Resolve(r) is not { } b) return list;
        foreach (var p in PgEngine.PlayerCharacters(world))
            if (TownKey.Same(h.CurrentTown(p), b.Town)) list.Add(p);
        return list;
    }

    static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    static string Map(SortedDictionary<string, int> map)
    {
        var sb = new StringBuilder("{");
        foreach (var kv in map) { if (sb.Length > 1) sb.Append(','); sb.Append(PGLog.Quote(kv.Key)).Append(':').Append(N(kv.Value)); }
        return sb.Append('}').ToString();
    }

    /// <summary>TEST ONLY <c>pg town roster</c>: SnapshotFor with no players (the ambient read, TownArena.cpp:654) and its counts: the catalog
    /// fighters present by type and by record, the ready ones by type, the medics, the rejections with their reasons, the settle clock.</summary>
    string RosterJson(World world)
    {
        var h = H(world);
        var s = logic.SnapshotFor(h, []);
        var present = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var presentRecords = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int fighters = 0, named = 0;
        foreach (var kv in s.Roles)
        {
            if (TownFighterCatalog.Find(kv.Key) is { } e)
            {
                present[e.Type] = (present.TryGetValue(e.Type, out int n) ? n : 0) + kv.Value;
                presentRecords[kv.Key] = kv.Value;
                fighters += kv.Value;
            }
            else if (TownChallengePolicy.NamedRoleDivision(kv.Key) >= 0) named += kv.Value;
        }
        // the catalog fighters in the circle but outside the registry's town (Towns.TownAt): the census counted the whole circle
        var outside = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int fightersOutside = 0;
        foreach (var kv in s.OtherTown)
            if (TownFighterCatalog.Find(kv.Key) is { } e) { outside[e.Type] = (outside.TryGetValue(e.Type, out int n) ? n : 0) + kv.Value; fightersOutside += kv.Value; }
        var ready = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int readyNamed = 0;
        foreach (var f in s.Npcs)
        {
            if (TownFighterCatalog.Find(f.Role) is { } e) ready[e.Type] = (ready.TryGetValue(e.Type, out int n) ? n : 0) + 1;
            else readyNamed++;
        }
        int medicsPresent = s.Roles.TryGetValue("100-Proving Grounds.mod", out int m) ? m : 0;
        var sb = new StringBuilder();
        sb.Append(",\"settled\":").Append(logic.RosterSettled(h) ? "true" : "false")
          .Append(",\"scanned\":").Append(N(s.Scanned)).Append(",\"inTown\":").Append(N(s.InTown))
          .Append(",\"otherTown\":").Append(Map(s.OtherTown)).Append(",\"otherFaction\":").Append(Map(s.OtherFaction))
          .Append(",\"missingIdentity\":").Append(N(s.MissingIdentity)).Append(",\"ambiguous\":").Append(N(s.Ambiguous))
          .Append(",\"fightersPresent\":").Append(N(fighters)).Append(",\"namedPresent\":").Append(N(named))
          .Append(",\"fightersOutsideTown\":").Append(N(fightersOutside)).Append(",\"outsideByType\":").Append(Map(outside))
          .Append(",\"presentByType\":").Append(Map(present)).Append(",\"presentByRecord\":").Append(Map(presentRecords))
          .Append(",\"eligible\":").Append(N(s.Npcs.Count)).Append(",\"eligibleNamed\":").Append(N(readyNamed))
          .Append(",\"eligibleByType\":").Append(Map(ready))
          .Append(",\"medicsPresent\":").Append(N(medicsPresent)).Append(",\"medics\":").Append(N(s.Medics.Count))
          .Append(",\"rejected\":").Append(Map(s.Blockers))
          .Append(",\"availability\":").Append(PGLog.Quote(s.Availability))
          .Append(",\"context\":").Append(PGLog.Quote(s.Context))
          .Append(",\"townMemo\":").Append(N(towns.MemoCount))
          .Append(",\"outsideDetail\":").Append(OutsideDetail(world));
        return sb.ToString();
    }

    /// <summary>TEST ONLY (pg town roster, task 7.2 review): every Bloodstamp catalog fighter in the 2,500 circle whose current town is not the
    /// registry's: record, distance on the ground from Scratch's town point, the building it stands in (null outdoors), Meitou's StatusTown and
    /// the port's memo (site index, -1 none).</summary>
    string OutsideDetail(World world)
    {
        var h = H(world);
        var sb = new StringBuilder("[");
        if (logic.State.TownRegistry is not { } r || h.Resolve(r) is not { } reg || towns.Scratch is not { } scratch) return "[]";
        foreach (var c in PgEngine.CharactersNear(world, reg.Building.Position, TownMatchmakingRuntimeLogic.SearchRadius, TownMatchmakingRuntimeLogic.SearchMax))
        {
            if (!h.Valid(c) || !h.IsTownFaction(c) || TownFighterCatalog.Find(h.Role(c)) is null) continue;
            if (TownKey.Same(h.CurrentTown(c), reg.Town)) continue;
            var p = PgEngine.Position(world, c) ?? default;
            float d = MathF.Sqrt((p.X - scratch.Position.X) * (p.X - scratch.Position.X) + (p.Z - scratch.Position.Z) * (p.Z - scratch.Position.Z));
            var (status, memo, building) = towns.Explain(world, c);
            // the nearest placed town (nest markers and Null left out), the town the status rule tests first (AiSystem.StatusTownAt step 2)
            TownSite? nearest = null; float nd = float.MaxValue;
            foreach (var site in data.Sites)
            {
                if (site.Town.Type is Meitou.Data.Gameplay.TownType.NestMarker or Meitou.Data.Gameplay.TownType.Null) continue;
                float sd = (p.X - site.Position.X) * (p.X - site.Position.X) + (p.Z - site.Position.Z) * (p.Z - site.Position.Z);
                if (sd < nd) (nearest, nd) = (site, sd);
            }
            if (sb.Length > 1) sb.Append(',');
            sb.Append("{\"id\":").Append(PGLog.Quote(CharacterIdText.Format(c))).Append(",\"record\":").Append(PGLog.Quote(h.Role(c)))
              .Append(",\"dist\":").Append(TestJson.F(d, 0)).Append(",\"building\":").Append(building is null ? "null" : PGLog.Quote(building))
              .Append(",\"statusTown\":").Append(N(status.IsNone ? -1 : status.Site)).Append(",\"memo\":").Append(N(memo.IsNone ? -1 : memo.Site))
              .Append(",\"nearestTown\":").Append(nearest is null ? "null" : "{\"site\":" + N(nearest.Index) + ",\"name\":" + PGLog.Quote(nearest.Town.Name)
                  + ",\"dist\":" + TestJson.F(MathF.Sqrt(nd), 0) + ",\"radius\":" + TestJson.G(nearest.Town.Radius, 6) + "}")
              .Append('}');
        }
        return sb.Append(']').ToString();
    }

    /// <summary>TEST ONLY <c>pg town search</c>: a fresh snapshot with the first player character in the registry's town as the lineup, a new search
    /// generation, and one Easy / Common card request (as UpdateMatchmakingPreview builds one, TownArena.cpp:875-896, maxTotal = the medics) queued
    /// on the world's background search. Null when queued, else why not; <paramref name="fields"/> the request's counts.</summary>
    string? QueueSearchForTest(World world, out string fields)
    {
        var h = H(world);
        fields = "";
        var players = PlayersInRegistryTown(world);
        if (players.Count == 0) return "no player character in the registry's town";
        var snapshot = logic.SnapshotFor(h, [players[0]]);
        fields = ",\"playersReady\":" + (snapshot.PlayersReady ? "true" : "false") + ",\"playerBlocker\":" + PGLog.Quote(snapshot.PlayerBlocker)
            + ",\"npcs\":" + N(snapshot.Npcs.Count) + ",\"medics\":" + N(snapshot.Medics.Count);
        if (!snapshot.PlayersReady) return "the player is not ready: " + snapshot.PlayerBlocker;
        uint generation = logic.BeginSearchGeneration(h);
        var request = new Core.TownMatchmakingSearch.Request
        {
            Generation = generation, Slot = 0, Players = snapshot.Players, Npcs = snapshot.Npcs,
            Division = TownMatchmakingPolicy.Division.Easy, Seed = 0x2468ACE1u, MaxEnemies = 4, MaxTotal = snapshot.Medics.Count, Rarity = 0,
            ChallengeDifficulty = PGConfig.ChallengeValues().Difficulty,
        };
        fields += ",\"generation\":" + N(generation);
        return h.MatchmakingEnqueue(request) ? null : "Background matchmaking is unavailable.";
    }

    /// <summary>TEST ONLY <c>pg town search-poll</c>: one result of the background search, if any (consumed), with the opponents' labels.</summary>
    string PollSearchForTest(World world)
    {
        var h = H(world);
        var sb = new StringBuilder();
        sb.Append(",\"running\":").Append(search.Running ? "true" : "false").Append(",\"generation\":").Append(N(search.Generation))
          .Append(",\"pendingRequests\":").Append(N(search.PendingRequests)).Append(",\"searches\":").Append(N(search.Searches))
          .Append(",\"failures\":").Append(N(search.Failures));
        if (!h.MatchmakingPoll(out var r) || r is null) return sb.Append(",\"found\":false").ToString();
        var snapshot = logic.SnapshotFor(h, []);
        string Labels(List<string> ids)
        {
            var parts = new List<string>();
            foreach (var id in ids)
                parts.Add(PGLog.Quote(TownMatchmakingRuntimeLogic.Resolve(h, snapshot, id, out var c) ? TownMatchmakingRuntimeLogic.Label(h, c) : id));
            return "[" + string.Join(",", parts) + "]";
        }
        sb.Append(",\"found\":true,\"resultGeneration\":").Append(N(r.Generation)).Append(",\"slot\":").Append(N(r.Slot))
          .Append(",\"valid\":").Append(r.Match.Valid ? "true" : "false").Append(",\"failure\":").Append(PGLog.Quote(r.Match.Failure.ToString()))
          .Append(",\"bestAvailable\":").Append(r.Match.BestAvailable ? "true" : "false")
          .Append(",\"a\":").Append(N(r.Match.A.Count)).Append(",\"b\":").Append(Labels(r.Match.B))
          .Append(",\"scoreA\":").Append(TestJson.G(r.Match.ScoreA, 6)).Append(",\"scoreB\":").Append(TestJson.G(r.Match.ScoreB, 6));
        return sb.ToString();
    }
}
