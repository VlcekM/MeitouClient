using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// Port of PrisonerUtil.cpp (Proving Grounds by Shutori, GPLv3), task 6.1: the cage and prisoner discovery (the anonymous-namespace helpers
// :159-440 and CollectNearbyCagePrisoners :855-902), the roster, FindCageForOccupant, CountHandlerCandidates, PrepareMatch and the match reads
// (:904-1108). See PrisonerUtilLogic.cs for the host, the state and the split.

public sealed partial class PrisonerUtilLogic<TId>
{
    // C++: PrisonerUtil.cpp:159-165
    static bool IsWithinRegistryRange(IPrisonerHost<TId> h, ArenaBuilding? registry, string? cage)
    {
        if (registry is not { } r || cage is null)
            return false;
        const float radiusSq = CageNearRegistry * CageNearRegistry;
        return HorizontalDistSq(r.Position, h.BuildingPosition(cage)) <= radiusSq;
    }

    // C++: PrisonerUtil.cpp:177-185
    static bool ContainsCageBuilding(List<NearbyCage> cages, string building)
    {
        for (int i = 0; i < cages.Count; ++i)
        {
            if (cages[i].Building == building)
                return true;
        }
        return false;
    }

    // C++: PrisonerUtil.cpp:187-202
    static string? AsUseableCage(IPrisonerHost<TId> h, string? building)
    {
        if (building is null)
            return null;

        string? useable = h.UseableStuffOf(building);
        if (useable is not null)
            return useable;

        // Cage furniture is often already the UseableStuff instance; base
        // getUseableStuff() can return null depending on how the pointer was typed.
        if (h.IsCageFunction(building))
            return building;

        return null;
    }

    // C++: PrisonerUtil.cpp:204-211
    static bool IsPlayerCageBuilding(IPrisonerHost<TId> h, string? building)
    {
        if (building is null || !h.BuildingValid(building))
            return false;
        if (h.IsThePlayer(building))
            return true;
        return h.IsBuildingOwnedByPlayer(building);
    }

    // C++: PrisonerUtil.cpp:213-233
    static void AddCageBuilding(IPrisonerHost<TId> h, ArenaBuilding? registry, string? building, List<NearbyCage> cagesOut)
    {
        if (registry is null || building is null || !h.BuildingValid(building))
            return;
        if (!IsWithinRegistryRange(h, registry, building))
            return;
        if (ContainsCageBuilding(cagesOut, building))
            return;

        string? useable = AsUseableCage(h, building);
        if (useable is null)
            return;

        cagesOut.Add(new NearbyCage(building, useable));
    }

    // C++: PrisonerUtil.cpp:235-257
    static void CollectCagesFromBuildingTree(IPrisonerHost<TId> h, ArenaBuilding? registry, string? building, List<NearbyCage> cagesOut)
    {
        if (registry is null || building is null || !h.BuildingValid(building))
            return;

        if (h.IsCageFunction(building) && IsPlayerCageBuilding(h, building))
            AddCageBuilding(h, registry, building, cagesOut);

        // Kenshi cages are usually furniture inside a player building.
        var furniture = new List<string>();
        h.CageFurniture(building, furniture);
        for (int i = 0; i < furniture.Count; ++i)
        {
            string cage = furniture[i];
            if (cage is null || !h.BuildingValid(cage))
                continue;
            if (!IsPlayerCageBuilding(h, cage) && !IsPlayerCageBuilding(h, building))
                continue;
            AddCageBuilding(h, registry, cage, cagesOut);
        }
    }

    // Ownership furniture first, then nearby player buildings' cage furniture,
    // then standalone BF_CAGE hits in the sphere.
    // C++: PrisonerUtil.cpp:259-331
    static void CollectCagesNearRegistry(IPrisonerHost<TId> h, ArenaBuilding? registry, List<NearbyCage> cagesOut)
    {
        cagesOut.Clear();
        if (registry is not { } r || !h.RegistryValid(r))
            return;

        var ownedCages = new List<string>();
        h.CollectPlayerOwnedCageBuildings(ownedCages);
        for (int i = 0; i < ownedCages.Count; ++i)
            AddCageBuilding(h, registry, ownedCages[i], cagesOut);

        var results = h.BuildingsInSphere(r.Position, CageNearRegistry, SphereSearchMax);
        if (results is null)
        {
            h.Debug(string.Create(CultureInfo.InvariantCulture,
                $"Proving Grounds: cage discovery ownership={cagesOut.Count} sphere=skipped (no world)"));
            return;
        }

        int sphereBuildings = 0;
        int playerBuildings = 0;
        for (int i = 0; i < results.Count; ++i)
        {
            string building = results[i];
            if (building is null || !h.BuildingValid(building))
                continue;
            // (the C++'s getDataType() BUILDING / BUILDING_FUNCTIONALITY / FOLIAGE_BUILDING filter is the host's: BuildingsInSphere)

            ++sphereBuildings;

            if (IsPlayerCageBuilding(h, building) || h.IsThePlayer(building))
            {
                ++playerBuildings;
                CollectCagesFromBuildingTree(h, registry, building, cagesOut);
            }
            else if (h.IsCageFunction(building) && h.IsBuildingOwnedByPlayer(building))
            {
                AddCageBuilding(h, registry, building, cagesOut);
            }
        }

        h.Debug(string.Create(CultureInfo.InvariantCulture,
            $"Proving Grounds: cage discovery cages={cagesOut.Count} ownership={ownedCages.Count} sphereBuildings={sphereBuildings} playerBuildings={playerBuildings}"));
    }

    // Most reliable roster source: characters already marked IN_PRISON near registry.
    // C++: PrisonerUtil.cpp:332-399
    void CollectImprisonedCharactersNearRegistry(IPrisonerHost<TId> h, ArenaBuilding? registry, List<TId> output)
    {
        if (registry is not { } r || !h.RegistryValid(r))
            return;

        var origin = r.Position;
        // farRadius / nearRadius / always / maxFar / maxNear — take everyone in range.
        var results = h.CharactersInSphere(origin, CageNearRegistry, SphereSearchMax);
        if (results is null)   // C++ !ou: returns before the sphere, no line
            return;

        int imprisonedHits = 0;
        for (int i = 0; i < results.Count; ++i)
        {
            TId character = results[i];
            if (h.IsNull(character) || !h.Valid(character))
                continue;
            // (the C++'s CHARACTER / HUMAN_CHARACTER / ANIMAL_CHARACTER type filter: every result is a character)

            if (h.IsDead(character))
                continue;
            if (!h.IsImprisoned(character))
                continue;

            ++imprisonedHits;

            string? cageBuilding = h.HeldIn(character);

            // Prefer player cages; if inWhat is missing, still keep nearby
            // imprisoned characters (furniture handle resolution can be flaky).
            if (cageBuilding is not null && !IsPlayerCageBuilding(h, cageBuilding))
                continue;

            if (HorizontalDistSq(origin, h.Position(character)) >
                (CageNearRegistry * CageNearRegistry))
                continue;

            if (!ContainsCharacter(output, character))
                output.Add(character);

            if (cageBuilding is not null)
                AddCageBuilding(h, registry, cageBuilding, State.NearbyCages);
        }

        h.Debug(string.Create(CultureInfo.InvariantCulture,
            $"Proving Grounds: imprisoned scan hits={imprisonedHits} kept={output.Count}"));
    }

    // C++: PrisonerUtil.cpp:401-415
    public static bool IsCageFree(IPrisonerHost<TId> h, string? useable)
    {
        if (useable is null)
            return false;

        TId occupantChar = h.Occupant(useable);
        if (h.IsNull(occupantChar))   // !occupant.isValid() (and the C++'s !occupantChar below)
            return true;

        if (!h.Valid(occupantChar) || h.IsDead(occupantChar))
            return true;

        return false;
    }

    // C++: PrisonerUtil.cpp:430-441
    static int FindCageIndex(string? cage, List<NearbyCage> cages)
    {
        if (cage is null)
            return -1;

        for (int i = 0; i < cages.Count; ++i)
        {
            if (cages[i].Useable == cage)
                return i;
        }
        return -1;
    }

    // C++: PrisonerUtil.cpp:855-902
    public void CollectNearbyCagePrisoners(IPrisonerHost<TId> h, ArenaBuilding? registry, List<TId> output)
    {
        output.Clear();
        State.RosterPrisoners.Clear();
        State.NearbyCages.Clear();

        if (registry is null)
        {
            h.Debug("Proving Grounds: prisoner collect skipped — no bound registry");
            return;
        }

        CollectCagesNearRegistry(h, registry, State.NearbyCages);

        // Occupant path (cage → prisoner).
        for (int i = 0; i < State.NearbyCages.Count; ++i)
        {
            string useable = State.NearbyCages[i].Useable;
            if (useable is null)
                continue;

            TId occupant = h.Occupant(useable);
            if (h.IsNull(occupant))   // !occupantHand.isValid() / !occupant
                continue;
            if (!h.Valid(occupant) || h.IsDead(occupant))
                continue;

            // Prefer IN_PRISON; also accept any living occupant of a player cage.
            if (!ContainsCharacter(output, occupant))
                output.Add(occupant);
        }

        // Character path (prisoner → cage). Catches cases where furniture
        // ownership / getOccupant fails but IN_PRISON is set.
        CollectImprisonedCharactersNearRegistry(h, registry, output);

        State.RosterPrisoners.Clear();
        State.RosterPrisoners.AddRange(output);

        h.Debug(string.Create(CultureInfo.InvariantCulture,
            $"Proving Grounds: prisoner roster size={output.Count} cages={State.NearbyCages.Count}"));
    }

    // C++: PrisonerUtil.cpp:904-911
    public bool IsRosterPrisoner(IPrisonerHost<TId> h, TId c)
    {
        if (h.IsNull(c))
            return false;
        if (HasMatchPrisonerEntry(h, c))
            return true;
        return ContainsCharacter(State.RosterPrisoners, c);
    }

    // C++: PrisonerUtil.cpp:913-916
    public bool IsMatchPrisoner(IPrisonerHost<TId> h, TId c) => HasMatchPrisonerEntry(h, c);

    // C++: PrisonerUtil.cpp:918-953
    public string? FindCageForOccupant(IPrisonerHost<TId> h, TId c)
    {
        if (h.IsNull(c) || !h.Valid(c))
            return null;

        if (h.IsImprisoned(c) && h.HeldIn(c) is { } building)
        {
            string? useable = AsUseableCage(h, building);
            if (useable is not null)
                return useable;
        }

        for (int i = 0; i < State.NearbyCages.Count; ++i)
        {
            string useable = State.NearbyCages[i].Useable;
            if (useable is null)
                continue;

            TId occupant = h.Occupant(useable);
            if (h.IsNull(occupant))   // !occupantHand.isValid()
                continue;

            if (Eq.Equals(occupant, c))
                return useable;
        }

        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            if (Eq.Equals(State.MatchEntries[i].Prisoner, c) && State.MatchEntries[i].CageUseable is { } cage)
                return cage;
        }

        return null;
    }

    // C++: PrisonerUtil.cpp:955-979 (no caller in the C++: ArenaUI counts its handlers itself, ArenaUI.cpp:510-525)
    public static int CountHandlerCandidates(IPrisonerHost<TId> h, IReadOnlyList<TId> squadInRange, IReadOnlyList<TId> matchFighters)
    {
        int count = 0;
        for (int i = 0; i < squadInRange.Count; ++i)
        {
            TId candidate = squadInRange[i];
            if (h.IsNull(candidate))
                continue;

            bool isFighter = false;
            for (int f = 0; f < matchFighters.Count; ++f)
            {
                if (Eq.Equals(matchFighters[f], candidate))
                {
                    isFighter = true;
                    break;
                }
            }
            if (!isFighter)
                ++count;
        }
        return count;
    }

    // C++: PrisonerUtil.cpp:981-1050
    public bool PrepareMatch(IPrisonerHost<TId> h, IReadOnlyList<TId> prisonerFighters, IReadOnlyList<TId> handlerCandidates, out string statusOut)
    {
        if (State.Returning || HasPendingCageLocks())
        {
            statusOut = "Wait for prisoner return and cage locking to finish";
            return false;
        }
        ClearMatchState(h);
        State.CageReturnFailed = false;
        State.CageReturnStatus = "";

        // Port only (task 9.3d, docs/divergences.md "Prisoner matches without a cage release"): no prisoner match when the engine cannot release one.
        if (prisonerFighters.Count != 0 && !h.CageReleaseAvailable())
        {
            statusOut = ReleaseUnavailableStatus;
            return false;
        }

        if (handlerCandidates.Count < prisonerFighters.Count)
        {
            statusOut = "Not enough handlers for prisoners";
            return false;
        }

        for (int i = 0; i < prisonerFighters.Count; ++i)
        {
            if (h.IsNull(handlerCandidates[i]))
            {
                statusOut = "Not enough handlers for prisoners";
                return false;
            }
        }

        ArenaBuilding? registry = h.BoundRegistry();
        var cages = new List<NearbyCage>();
        CollectCagesNearRegistry(h, registry, cages);
        State.NearbyCages.Clear();
        State.NearbyCages.AddRange(cages);

        var planned = new List<MatchEntry<TId>>(prisonerFighters.Count);

        for (int i = 0; i < prisonerFighters.Count; ++i)
        {
            TId prisoner = prisonerFighters[i];
            string? cage = FindCageForOccupant(h, prisoner);
            if (cage is null)
            {
                statusOut = "Prisoner has no cage";
                return false;
            }

            var entry = new MatchEntry<TId>();
            MakeEmptyEntry(entry);
            entry.Prisoner = prisoner;
            entry.CageUseable = cage;
            entry.CageHand = cage;
            entry.Handler = handlerCandidates[i];
            entry.PrisonerHand = prisoner;
            entry.HandlerHand = entry.Handler;
            entry.OriginalCageIndex = FindCageIndex(cage, cages);
            planned.Add(entry);
        }

        for (int i = 0; i < planned.Count; ++i)
        {
            // Stay caged until the handler arrives to "unlock" them.
            State.MatchEntries.Add(planned[i]);
            State.MatchHandlers.Add(planned[i].Handler);
        }

        State.MatchHasPrisoner = prisonerFighters.Count != 0;
        statusOut = "";
        return true;
    }

    // C++: PrisonerUtil.cpp:1052-1055
    public IReadOnlyList<TId> GetMatchHandlers() => State.MatchHandlers;

    // C++: PrisonerUtil.cpp:1057-1063
    public IReadOnlyList<TId> GetMatchPrisoners()
    {
        State.MatchPrisonersScratch.Clear();
        for (int i = 0; i < State.MatchEntries.Count; ++i)
            State.MatchPrisonersScratch.Add(State.MatchEntries[i].Prisoner);
        return State.MatchPrisonersScratch;
    }

    // C++: PrisonerUtil.cpp:1065-1068
    public IReadOnlyList<TId> GetRosterPrisoners() => State.RosterPrisoners;

    // C++: PrisonerUtil.cpp:1070-1079
    public void ForgetRosterPrisoner(TId prisoner)
    {
        for (int i = 0; i < State.RosterPrisoners.Count; )
        {
            if (Eq.Equals(State.RosterPrisoners[i], prisoner))
                State.RosterPrisoners.RemoveAt(i);
            else
                ++i;
        }
    }

    // C++: PrisonerUtil.cpp:1081-1084
    public bool MatchIncludesPrisoner() => State.MatchHasPrisoner;

    // C++: PrisonerUtil.cpp:1086-1096
    public bool IsMatchPrisonerReleased(IPrisonerHost<TId> h, TId c)
    {
        if (h.IsNull(c))
            return false;
        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            if (Eq.Equals(State.MatchEntries[i].Prisoner, c))
                return State.MatchEntries[i].Released;
        }
        return false;
    }

    // C++: PrisonerUtil.cpp:1098-1108
    public bool AllMatchPrisonersReleased()
    {
        if (!State.MatchHasPrisoner)
            return true;
        for (int i = 0; i < State.MatchEntries.Count; ++i)
        {
            if (!State.MatchEntries[i].Released)
                return false;
        }
        return true;
    }
}
