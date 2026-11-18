using System.Globalization;
using System.Numerics;

namespace ProvingGrounds.Core.Glue;

// Port of upstream/ProvingGrounds/plugin/src/TownSpectators.cpp (Proving Grounds by Shutori, GPLv3), M8 task 8.2: Begin (:360-447) and Invite
// (:120-139), split from TownSpectatorsLogic.cs (the rest of the file's decisions) to keep each part short.

public sealed partial class TownSpectatorsLogic<TId>
{
    // C++: TownSpectators.cpp:120-139 (the seats' free test: <c>!Occupied(seat) &amp;&amp; isFreeSlot &amp;&amp; couldIOperate</c>; <paramref name="free"/> is
    // the read of Begin, before any invite; CanSit stands for couldIOperate's actor half)
    bool Invite(ITownSpectatorsHost<TId> h, TId c, List<SpectatorSeat> seats, bool[] free, int audienceTarget)
    {
        if (h.IsNull(c) || guests.Count >= audienceTarget) return false;
        if (!h.CanSit(c)) return false;
        for (int j = 0; j < seats.Count; ++j)
        {
            bool reserved = false;
            for (int k = 0; k < guests.Count; ++k) if (guests[k].Seat.Id == seats[j].Id) reserved = true;
            if (!TownSpectatorPolicy.CanReserve(free[j], reserved, guests.Count, audienceTarget)) continue;
            if (!h.Hold(c)) { ++HoldRefused; return false; }   // port only: the hold could not be taken (no guest without it)
            guests.Add(new Guest { Actor = c, Seat = seats[j], Seated = false, LastOrder = -3, Occupant = none });
            return true;
        }
        return false;
    }

    // C++: TownSpectators.cpp:360-447
    public void Begin(ITownSpectatorsHost<TId> h, ArenaBuildingRef arenaRef, ArenaPerformance.Profile profile)
    {
        Release(h);
        if (h.Resolve(arenaRef) is not { } arena || arena.Town == TownKey.None) return;
        activePolicy = ArenaPerformance.PolicyFor(profile);
        activeArena = arenaRef; elapsed = 0; lastTick = h.NowMs;
        ExcludedByDuty = 0; ExcludedByHold = 0; HoldRefused = 0; SitRefused = 0; sitRefusalLogged = false;
        Vector3 arenaPos = arena.Building.Position;
        // Diagnostic discovery is deliberately wider than seat eligibility so
        // logs distinguish missing furniture from distance/ownership rejection.
        var buildings = h.BuildingsInSphere(arenaPos, SearchRadius, BuildingMax) ?? [];
        var candidateSeats = new List<SpectatorSeat>();
        var candidateTowns = new List<long>();
        uint pillowCount = 0;
        for (int i = 0; i < buildings.Count; ++i)
        {
            ArenaBuilding b = buildings[i].Building;
            if (TownArenaRuntimePolicy.IdEquals(b.RecordId, PgRecords.ArenaPillow)) ++pillowCount;
            if (h.SeatOf(b) is not { } seat) continue;
            candidateSeats.Add(seat);
            candidateTowns.Add(buildings[i].Town);
        }
        var occupants = new TId[candidateSeats.Count];
        var claimed = new bool[candidateSeats.Count];
        for (int i = 0; i < occupants.Length; ++i) occupants[i] = none;
        if (candidateSeats.Count > 0) h.ReadSeats(candidateSeats, occupants, claimed, boardClaims: true);
        var seats = new List<SpectatorSeat>();
        var free = new List<bool>();
        for (int i = 0; i < candidateSeats.Count; ++i)
        {
            if (!h.IsNull(occupants[i])) continue;   // C++ :377 Occupied(seat)
            SpectatorSeat seat = candidateSeats[i];
            double distance = Vector3.DistanceSquared(seat.Position, arenaPos);
            double otherDistance = 1.0e30;
            for (int j = 0; j < buildings.Count; ++j)
            {
                ArenaBuilding o = buildings[j].Building;
                if (o.Ref == arenaRef || !TownArenaRuntimePolicy.IsNewArena(o.RecordId)) continue;
                if (!TownKey.Same(buildings[j].Town, arena.Town)) continue;
                double candidate = Vector3.DistanceSquared(seat.Position, o.Position);
                if (candidate < otherDistance) otherDistance = candidate;
            }
            bool sameTown = TownKey.Same(candidateTowns[i], arena.Town);
            if (TownSpectatorPolicy.BelongsToArena(sameTown, distance, otherDistance)) { seats.Add(seat); free.Add(!claimed[i]); }
            else
                h.Debug("Proving Grounds: pillow association rejected distanceSq=" + CMath.FormatF(distance, 1) + " otherArenaDistanceSq=" +
                    CMath.FormatF(otherDistance, 1) + " sameTown=" + (sameTown ? "1" : "0"));
        }
        var freeArray = free.ToArray();
        var nearby = h.CharactersInSphere(arenaPos, SearchRadius, CharacterMax);
        if (nearby is null || nearby.Count == 0) return;
        uint audienceSequence = nextAudience++;
        uint start = audienceSequence % (uint)nearby.Count;
        double gameHours = h.TotalHours;
        int epoch = TownArenaPolicy.CardEpoch(gameHours);
        bool daytime = epoch >= 0 && epoch % 2 == 0;
        int audienceTarget = activePolicy.SpectatorCap;
        var candidates = new[] { new List<TId>(), new List<TId>(), new List<TId>() };
        var fighterCandidates = new List<TId>();
        for (uint i = 0; i < (uint)nearby.Count; ++i)
        {
            TId c = nearby[(int)((start + i) % (uint)nearby.Count)];
            if (h.IsNull(c) || !h.Valid(c)) continue;
            string record = h.RecordId(c);
            var role = TownSpectatorPolicy.RoleFor(record.Length == 0 ? null : record);
            bool fighter = record.Length != 0 && TownArenaRuntimePolicy.IsRegularFighter(record);
            if (!TownKey.Same(h.CurrentTown(c), arena.Town) || h.IsPlayer(c) || !Healthy(h, c)) continue;
            if (!h.IsTownFaction(c)) continue;   // port only (shared brief, census): Bloodstamp only
            bool assigned = false;
            for (int j = 0; j < guests.Count; ++j) if (eq.Equals(guests[j].Actor, c)) assigned = true;
            if (assigned || IsPending(c)) continue;
            if (h.HeldByAnotherOwner(c)) { if (fighter || role != TownSpectatorPolicy.AudienceRole.NotAudience) ++ExcludedByHold; continue; }   // port only
            if (fighter)
            {
                if (!h.IsTownFighter(c)) fighterCandidates.Add(c);
                continue;
            }
            bool duty = role != TownSpectatorPolicy.AudienceRole.NotAudience && h.HasDuty(c);
            if (!TownSpectatorPolicy.CanWatch(role, false, duty, true))
            {
                if (duty && role == TownSpectatorPolicy.AudienceRole.Civilian) ++ExcludedByDuty;
                continue;
            }
            candidates[(int)role].Add(c);
        }
        var candidateIndex = new int[3];
        var roleCursor = (TownSpectatorPolicy.AudienceRole)(int)(audienceSequence % 3);
        while (guests.Count < audienceTarget)
        {
            var role = TownSpectatorPolicy.NextRole(
                candidates[0].Count - candidateIndex[0],
                candidates[1].Count - candidateIndex[1],
                candidates[2].Count - candidateIndex[2], roleCursor);
            if (role == TownSpectatorPolicy.AudienceRole.NotAudience) break;
            int roleIndex = (int)role;
            TId c = candidates[roleIndex][candidateIndex[roleIndex]++];
            roleCursor = (TownSpectatorPolicy.AudienceRole)((roleIndex + 1) % 3);
            Invite(h, c, seats, freeArray, audienceTarget);
        }
        for (int i = 0; i < fighterCandidates.Count && guests.Count < audienceTarget; ++i)
            Invite(h, fighterCandidates[i], seats, freeArray, audienceTarget);
        h.Debug(string.Create(CultureInfo.InvariantCulture,
            $"Proving Grounds: spectator seating - {(daytime ? "day" : "night")} target={audienceTarget}, {(uint)buildings.Count} nearby buildings, {pillowCount} matching pillows, {(uint)seats.Count} free pillows, {(uint)guests.Count} invited (candidates: {(uint)candidates[0].Count} civilians, {(uint)candidates[1].Count} bar residents, {(uint)candidates[2].Count} cage handlers, {(uint)fighterCandidates.Count} idle fighters)"));
        Tick(h);
    }
}
