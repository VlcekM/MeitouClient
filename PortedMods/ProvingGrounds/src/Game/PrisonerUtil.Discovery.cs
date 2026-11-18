using System.Numerics;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Task 6.1 (PrisonerUtil.cpp:150-440, 855-1108; PrisonerUtilPlatoon.cpp): the cage and prisoner discovery, the roster, the handlers and the
// match reads, bound to a world. The decisions are PrisonerUtilLogic's; the host below is each IPrisonerHost member as one adapter read
// (src/Engine/Cages.cs, PgEngine) or one call of another subsystem. See PrisonerUtil.cs for the E1 list.

sealed partial class PrisonerUtil
{
    /// <summary>IPrisonerHost over one world, rebound for every call (<see cref="Bind"/>). The town buildings of the last sphere query are kept
    /// until the next sphere query (or a bind to another world): a town building key names nothing else the host can read (Cages.cs header), and
    /// the logic reads a town key only in the call whose sphere query listed it.</summary>
    sealed partial class Host(PrisonerUtil owner) : IPrisonerHost<CharacterId>
    {
        World? world;

        /// <summary>The PrisonerUtil this host belongs to (the other parts of the host: PrisonerUtil.Release.cs / .Protection.cs, task 6.2).</summary>
        PrisonerUtil Owner => owner;
        readonly Dictionary<string, Vector3> sphereTown = new(StringComparer.Ordinal);   // key -> position, the last sphere query's town buildings

        internal Host Bind(World w)
        {
            if (!ReferenceEquals(world, w)) sphereTown.Clear();
            world = w;
            return this;
        }

        World W => world ?? throw new InvalidOperationException("PrisonerUtil used before a world was bound");

        // ---------- characters ----------
        public bool IsNull(CharacterId c) => c.IsNone;
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        // Vector3.Zero for a stale id: the logic reads a position only after Valid(c) (IPrisonerHost contract); callers must check Valid first
        public Vector3 Position(CharacterId c) => PgEngine.Position(W, c) ?? Vector3.Zero;
        public bool IsImprisoned(CharacterId c) => Cages.IsImprisoned(W, c);
        public string? HeldIn(CharacterId c) => Cages.HeldIn(W, c);
        public IReadOnlyList<CharacterId>? CharactersInSphere(Vector3 origin, float radius, int max) => PgEngine.CharactersNear(W, origin, radius, max);

        // ---------- buildings and cages ----------
        public bool RegistryValid(ArenaBuilding registry) => ArenaIdentity.Resolve(W, owner.data, registry.Ref) is not null;
        public bool BuildingValid(string building) =>
            Cages.Cage(W, building) is not null || Cages.PlayerSite(W, building) is not null || sphereTown.ContainsKey(building);
        // Vector3.Zero for an unknown key: the logic reads a position only after BuildingValid(building); callers must check BuildingValid first
        public Vector3 BuildingPosition(string building) =>
            Cages.Position(W, building) ?? (sphereTown.TryGetValue(building, out var p) ? p : Vector3.Zero);
        public bool IsThePlayer(string building) => Cages.IsThePlayer(W, building, sphereTown.ContainsKey(building));
        public bool IsCageFunction(string building) => Cages.Cage(W, building) is not null;
        public string? UseableStuffOf(string building) => Cages.Cage(W, building)?.Id;
        public void CageFurniture(string building, List<string> into) => Cages.Furniture(W, building, into);
        public CharacterId Occupant(string useable) => Cages.Cage(W, useable) is { } cage ? Cages.Occupant(W, cage) : CharacterId.None;

        /// <summary>The C++ sphere of buildings (PgEngine.BuildingsNear: the placed towns' buildings, then the player's sites, 3D, at most
        /// <paramref name="max"/>); the town ones are remembered for this call.</summary>
        public IReadOnlyList<string>? BuildingsInSphere(Vector3 origin, float radius, int max)
        {
            sphereTown.Clear();
            var found = PgEngine.BuildingsNear(W, owner.data, origin, radius, null, max);
            var keys = new List<string>(found.Count);
            foreach (var b in found)
            {
                keys.Add(b.Key);
                if (b.Source == PgEngine.BuildingSource.Town) sphereTown[b.Key] = b.Position;
            }
            return keys;
        }

        // ---------- PrisonerUtilInternal (PrisonerUtilPlatoon.cpp) ----------
        public void CollectPlayerOwnedCageBuildings(List<string> into)   // src/Game/PrisonerUtilPlatoon.cs (it clears its list, as the C++)
        {
            var owned = new List<string>();
            PrisonerUtilInternal.CollectPlayerOwnedCageBuildings(W, owned);
            into.AddRange(owned);
        }
        public bool IsBuildingOwnedByPlayer(string building) => PrisonerUtilInternal.IsBuildingOwnedByPlayer(W, building);

        // ---------- other subsystems ----------
        public ArenaBuilding? BoundRegistry() => owner.ingress.GetBoundRegistry(W);
        public void DisengageMatch(IReadOnlyList<CharacterId> handlers)
        {
            var array = handlers.ToArray();
            owner.fightStarter.DisengageMatch(W, array, array.Length);
        }
        public void Debug(string line) => PGLog.Debug(line);
    }

    // ---------- the roster and the cages (the Arena window, the debug menu, the leaderboard) ----------

    // C++: PrisonerUtil.cpp:855-902
    internal void CollectNearbyCagePrisoners(World world, ArenaBuilding? registry, List<CharacterId> into)
    {
        // the C++ out.clear()s its vector; the callers here append to theirs (ArenaUI's prisoners list and DebugMenu's are fresh), so the
        // discovery fills a list of its own and appends it.
        var found = new List<CharacterId>();
        logic.CollectNearbyCagePrisoners(H(world), registry, found);
        into.AddRange(found);
    }

    // C++: PrisonerUtil.cpp:904-911
    internal bool IsRosterPrisoner(World world, CharacterId c) => logic.IsRosterPrisoner(H(world), c);

    // C++: PrisonerUtil.cpp:913-916
    internal bool IsMatchPrisoner(World world, CharacterId c) => logic.IsMatchPrisoner(H(world), c);

    // C++: PrisonerUtil.cpp:918-953 (the cage key; null = none)
    internal string? FindCageForOccupant(World world, CharacterId c) => logic.FindCageForOccupant(H(world), c);

    /// <summary>ArenaUI.cpp:337 <c>PrisonerUtil::FindCageForOccupant(c) != NULL</c>.</summary>
    internal bool HasCageFor(World world, CharacterId c) => FindCageForOccupant(world, c) is not null;

    // C++: PrisonerUtil.cpp:955-979 (no caller in the C++)
    internal int CountHandlerCandidates(World world, IReadOnlyList<CharacterId> squadInRange, IReadOnlyList<CharacterId> matchFighters) =>
        PrisonerUtilLogic<CharacterId>.CountHandlerCandidates(H(world), squadInRange, matchFighters);

    // C++: PrisonerUtil.cpp:981-1050
    internal bool PrepareMatch(World world, IReadOnlyList<CharacterId> prisoners, IReadOnlyList<CharacterId> handlers, out string status) =>
        logic.PrepareMatch(H(world), prisoners, handlers, out status);

    // C++: PrisonerUtil.cpp:1052-1055
    internal IReadOnlyList<CharacterId> GetMatchHandlers(World world) => logic.GetMatchHandlers();

    // C++: PrisonerUtil.cpp:1057-1063 (the C++ scratch vector: valid until the next call)
    internal IReadOnlyList<CharacterId> GetMatchPrisoners(World world) => logic.GetMatchPrisoners();

    // C++: PrisonerUtil.cpp:1065-1068
    internal IReadOnlyList<CharacterId> GetRosterPrisoners(World world) => logic.GetRosterPrisoners();

    // C++: PrisonerUtil.cpp:1070-1079 (PrisonerRecruitment.cpp:126, task 6.4)
    internal void ForgetRosterPrisoner(World world, CharacterId prisoner) => logic.ForgetRosterPrisoner(prisoner);

    // C++: PrisonerUtil.cpp:1081-1084
    internal bool MatchIncludesPrisoner(World world) => logic.MatchIncludesPrisoner();

    // C++: PrisonerUtil.cpp:1086-1096
    internal bool IsMatchPrisonerReleased(World world, CharacterId c) => logic.IsMatchPrisonerReleased(H(world), c);

    // C++: PrisonerUtil.cpp:1098-1108
    internal bool AllMatchPrisonersReleased(World world) => logic.AllMatchPrisonersReleased();

    /// <summary>CombatHooks (task 3.2b, CombatHooks.cpp:165-168 <c>PrisonerUtil::IsMatchPrisoner</c>): every match prisoner (an entry from
    /// PrepareMatch on, released or not, as IsMatchPrisoner answers), appended; copied into the published isolation view each serial step. Task 6.3:
    /// with each prisoner its handler (<paramref name="handlersInto"/>, the same order), for the view's own-carrier exemption (the handler lifts its
    /// prisoner on the return's carry). No allocation: the entries are read directly.</summary>
    internal void CopyMatchPrisoners(World world, List<CharacterId> into, List<CharacterId> handlersInto)
    {
        var entries = logic.State.MatchEntries;
        for (int i = 0; i < entries.Count; i++) { into.Add(entries[i].PrisonerHand); handlersInto.Add(entries[i].HandlerHand); }
    }
}
