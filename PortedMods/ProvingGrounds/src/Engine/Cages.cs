using System.Numerics;
using Meitou.Simulation;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;

namespace ProvingGrounds.Engine;

// The cage adapter (task 6.1, CAP 15): every Meitou read and write of cages, their owners and the held characters that PrisonerUtil.cpp /
// PrisonerUtilPlatoon.cpp make through Kenshi's engine. Meitou 0.17.3 (tag v0.17.3, fa34a1b); paths relative to its repo root. PUBLIC Meitou
// API, compiled in, except FixtureRegistry.Release (task 9.3d: 0.20.2 added a parameter, so it is bound by reflection: Members.FixturesRelease,
// part CageRelease); PgCheck engine JIT-binds this class, PgCheck versions reads its members in every installed game version. Sim thread, serial
// steps only.
//
// What a Kenshi building is here (the C++ works on Building* / UseableStuff*; the port on a string key, the one the PrisonerUtil logic holds):
//   a cage ......................... a Fixture of FixtureKind.Cage (cages, the prisoner pole, the peeler: Fixtures.cs:44-47 function 8), key
//                                    Fixture.Id: a placed cage's own lock is its building's instance id (the player's Prisoner Cage: its
//                                    BuildSite.Id, FixtureRegistry.cs:671-711 RegisterPlayerBuilding makes it when the site is complete), an
//                                    interior cage of a town house the TownFixture id (Fixtures.cs:213-218); UseableStuff = the cage itself
//   a player building .............. a listed BuildSite (world.Buildings, BuildingRegistry.cs:310-314), key BuildSite.Id
//   a town building ................ a TownBuilding of a placed town, key TownBuilding.Key, known only from the sphere query of the same call
//                                    (PgEngine.BuildingsNear; the host keeps that call's result)
//   a key that is a cage fixture is read as the cage first (a player's Prisoner Cage is both its site and its cage, same id and position).
//
// The Kenshi calls and their Meitou counterparts (E1):
//   UseableStuff::getOccupant() ......... Occupant(fixture): Fixture.Occupant unless FixtureRegistry.IsFree says the slot is stale (the
//                                         occupant gone or no longer held there, FixtureRegistry.cs:1412-1414; what CageSpots publishes, :260-280)
//   Building::getSpecialFunction()==BF_CAGE, getUseableStuff() ... Cage(world, key) is not null (a cage fixture's key; no other fixture kind is
//                                         a prison: Imprison refuses anything but FixtureKind.Cage, FixtureRegistry.cs:1593-1597)
//   Building::findAllFurnitureWithFunction(BF_CAGE) ... Furniture(): the building's cage fixtures (FixtureRegistry.OfBuilding, :136; not its own
//                                         lock fixture) and the player's cage sites standing in it (BuildSite.Parent, BuildingRegistry.cs:71)
//   Building::isThePlayer() ............. IsThePlayer(): the owner faction is the player's (Fixture.Owner, Fixtures.cs:246; BuildSite.Owner,
//                                         BuildingRegistry.cs:85; a town house: FixtureRegistry.PropertyOwner, FixtureRegistry.Property.cs:81)
//   Ownerships::isOwned (PrisonerUtilPlatoon.cpp:110-116) ... Owns(faction): FixtureRegistry.OwnsBuilding(building, faction) (Property.cs:103-104:
//                                         the house's property owner, else one of its fixtures' owner); for a cage, its owner or its building's
//   Ownerships::getHomeFurnitureOfType / getBuildingsWithFunction(BF_CAGE) (PrisonerUtilPlatoon.cpp:88-90) ... CageIds(): every cage fixture,
//                                         registry order, that src/Game/PrisonerUtilPlatoon.cs filters by Owns (Meitou has no per-squad
//                                         ownership lists: a bought house's fixtures become the player's on purchase, Property.cs:167-178)
//   Character::inSomething == IN_PRISON .. CharacterCold.IsImprisoned (CharacterTable.cs:973)
//   Character::inWhat.getBuilding() ..... HeldIn(): CharacterCold.InWhat (CharacterTable.cs:968, the fixture id; null = not known) when a fixture
//                                         with that id is registered
//   Character::isChainedMode() .......... PgEngine.IsChained (LockSystem.Chained, LockSystem.Shackles.cs:76)
//   building->getPosition() ............. a fixture's Position (Fixtures.cs:251, the cage's origin), a site's Position, a town building's
//   Character::setPrisonMode(false, NULL) (task 6.2, the release) ... Release(): FixtureRegistry.Release(world, id, -1) (FixtureRegistry.cs:1751),
//                                         called through Members.FixturesRelease (task 9.3d: 0.20.2 added a fifth parameter, jailer, passed false)
//   Character::setPrisonMode(true, cage) (task 6.3, the return) ... PutInCage(): S11's recipe (LetGo if carried, Release if held elsewhere,
//                                         FixtureRegistry.Imprison :1593, which locks the cage itself)
//   building->destroyed, isBroken(), getDoorLock(), AI::isDoorLocked (task 6.3, the lock check) ... IsDestroyed (BuildSite.Destroyed),
//                                         IsBroken (Machine.Broken / LockState.OwnerBroken), HasLock (Fixture.Lock), IsLocked
//                                         (LockState.EffectivelyLocked)
//   test verbs (S11's recipe): CarrySystem.LetGo (CarrySystem.cs:608), FixtureRegistry.Release (:1751, releaser -1: no crime, not escaped) and
//                                         Imprison (:1593: locks the cage itself, spike S11)
static class Cages
{
    /// <summary>The player's faction (<c>world.Player.Faction</c>, PlayerState.cs:22; -1: none).</summary>
    internal static int PlayerFaction(World world) => world.Player.Faction;

    /// <summary>The cage fixture with this id, or null (no fixture, or not a cage).</summary>
    internal static Fixture? Cage(World world, string key) => world.Fixtures.Find(key) is { Kind: FixtureKind.Cage } f ? f : null;

    /// <summary>A listed player site with this id (not a town house under repair, not destroyed: PgEngine.IsListed), or null.</summary>
    internal static BuildSite? PlayerSite(World world, string key) => world.Buildings.Find(key) is { } s && PgEngine.IsListed(s) ? s : null;

    /// <summary>C++ <c>getOccupant()</c> as a valid handle: the occupant unless the slot is stale (FixtureRegistry.IsFree); none otherwise.</summary>
    internal static CharacterId Occupant(World world, Fixture cage) => FixtureRegistry.IsFree(world.Characters, cage) ? CharacterId.None : cage.Occupant;

    /// <summary>The C++ <c>isThePlayer()</c> of a cage, a player site or (with <paramref name="town"/> = it came from this call's sphere) a town
    /// building: its owner faction is the player's.</summary>
    internal static bool IsThePlayer(World world, string key, bool town)
    {
        int player = PlayerFaction(world);
        if (player < 0) return false;
        if (Cage(world, key) is { } cage) return cage.Owner == player;
        if (PlayerSite(world, key) is { } site) return site.Owner == player;
        return town && world.Fixtures.PropertyOwner(key) == player;
    }

    /// <summary>C++ <c>Ownerships::isOwned(handle)</c> for the ownerships of <paramref name="faction"/> (PrisonerUtilPlatoon.cpp:110-116): the
    /// faction owns the building (FixtureRegistry.OwnsBuilding: the property owner, else one of its fixtures' owner); a cage: the faction owns
    /// the cage (Fixture.Owner) or the building it is in. False for no faction.</summary>
    internal static bool Owns(World world, int faction, string key)
    {
        if (faction < 0) return false;
        if (Cage(world, key) is { } cage)
            return cage.Owner == faction || world.Fixtures.OwnsBuilding(cage.Building, faction);
        return world.Fixtures.OwnsBuilding(key, faction);
    }

    /// <summary>The ids of every cage fixture (FixtureRegistry.All, registry order): what <c>getHomeFurnitureOfType</c> /
    /// <c>getBuildingsWithFunction(BF_CAGE)</c> pick from, appended.</summary>
    internal static void CageIds(World world, List<string> into)
    {
        var all = world.Fixtures.All;
        for (int i = 0; i < all.Count; i++)
            if (all[i].Kind == FixtureKind.Cage) into.Add(all[i].Id);
    }

    /// <summary>C++ <c>findAllFurnitureWithFunction(BF_CAGE)</c>: the cage fixtures of the building (not its own lock fixture) and the player's
    /// cage sites standing in it (<c>BuildSite.Parent</c>), each once.</summary>
    internal static void Furniture(World world, string key, List<string> into)
    {
        var own = world.Fixtures.OfBuilding(key);
        for (int i = 0; i < own.Count; i++)
            if (own[i].Kind == FixtureKind.Cage && own[i].Id != key && !into.Contains(own[i].Id)) into.Add(own[i].Id);
        var sites = world.Buildings.All;
        for (int i = 0; i < sites.Count; i++)
        {
            var s = sites[i];
            if (s.Parent != key || !PgEngine.IsListed(s) || Cage(world, s.Id) is null || into.Contains(s.Id)) continue;
            into.Add(s.Id);
        }
    }

    /// <summary>The position of a cage fixture or a player site (null: neither).</summary>
    internal static Vector3? Position(World world, string key) =>
        Cage(world, key) is { } cage ? cage.Position : PlayerSite(world, key) is { } site ? site.Position : null;

    /// <summary>C++ <c>inSomething == IN_PRISON</c> (CharacterCold.IsImprisoned).</summary>
    internal static bool IsImprisoned(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s) is { IsImprisoned: true };

    /// <summary>C++ <c>inWhat.isValid() ? inWhat.getBuilding() : NULL</c>: the fixture the character is held in, when one with its
    /// <c>InWhat</c> id is registered; null otherwise (not held, not known, or gone).</summary>
    internal static string? HeldIn(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int s) && world.Characters.Cold(s)?.InWhat is { } what && world.Fixtures.Find(what) is { } f ? f.Id : null;

    /// <summary>Task 6.2: C++ <c>prisoner-&gt;setPrisonMode(false, NULL)</c> (ReleasePrisonerFromCage, PrisonerUtil.cpp:754), as the plan says
    /// (M6-prisoners.md task 6.2, spike S11): <c>world.Fixtures.Release(world, prisoner, releaserFaction: -1)</c> (FixtureRegistry.cs:1751-1785:
    /// the cage unlocked and emptied, the prisoner stepped out at the cage exit, its held state cleared; releaser -1 = no crime, not escaped).
    /// False when it was not imprisoned (nothing done). Serial step only.
    /// Task 9.3d: through <see cref="Members.FixturesRelease"/> (0.17.3-0.20.1: four parameters; 0.20.2: a fifth, <c>jailer</c>, passed false).
    /// False, with one log line, when neither signature exists (then <see cref="ReleaseAvailable"/> is false and PrisonerUtil refuses prisoner
    /// matches, PrisonerUtilLogic.PrepareMatch).</summary>
    internal static bool Release(World world, CharacterId prisoner)
    {
        if (!releaseBound) BindRelease();
        return release is not null && release(world.Fixtures, world, prisoner);
    }

    /// <summary>Task 9.3d: the release binds (Members.FixturesRelease found, its delegate made). Read by PrisonerUtil's host
    /// (IPrisonerHost.CageReleaseAvailable), as Holds.ControlAvailable gates the town bouts.</summary>
    internal static bool ReleaseAvailable
    {
        get
        {
            if (!releaseBound) BindRelease();
            return release is not null;
        }
    }

    /// <summary>TEST ONLY (PgCheck engine, task 9.3d): the number of parameters of the bound Release (4: 0.17.3-0.20.1, 5: 0.20.2), 0 when unbound.</summary>
    internal static int BoundReleaseParameters()
    {
        if (!releaseBound) BindRelease();
        return release is null ? 0 : releaseParameters;
    }

    static Func<FixtureRegistry, World, CharacterId, bool>? release;
    static int releaseParameters;
    static bool releaseBound;

    // Bound once. A failure is logged once; releaseBound is set only once that line went out (ProvingGroundsMod.Ctx is set in Initialize, before
    // any world, so this matters only for a call made earlier: the next call tries again and logs then). Holds.Resolve does not do this.
    static void BindRelease()
    {
        try
        {
            if (Members.FixturesRelease is not { } m) { releaseBound = ReleaseOff("not found"); return; }
            releaseParameters = m.GetParameters().Length;
            if (releaseParameters == 4)
            {
                var four = m.CreateDelegate<Func<FixtureRegistry, World, CharacterId, int, Meitou.Simulation.Crime.CrimeSystem?, bool>>();
                release = (f, w, p) => four(f, w, p, -1, null);
            }
            else
            {
                var five = m.CreateDelegate<Func<FixtureRegistry, World, CharacterId, int, Meitou.Simulation.Crime.CrimeSystem?, bool, bool>>();
                release = (f, w, p) => five(f, w, p, -1, null, false);
            }
            releaseBound = true;
        }
        catch (Exception e) { release = null; releaseBound = ReleaseOff($"{e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>The part-off line; true when it was logged.</summary>
    static bool ReleaseOff(string why)
    {
        if (ProvingGroundsMod.Ctx is not { } ctx) return false;
        ctx.Log($"cage release off: FixtureRegistry.Release {why}; prisoner matches are refused (\"{ProvingGrounds.Core.Glue.PrisonerUtilLogic<CharacterId>.ReleaseUnavailableStatus}\"), "
            + "a recruit is refused (the prisoner stays in its cage)");
        return true;
    }

    // ---------- the cage return and its lock check (task 6.3, PrisonerUtil.cpp:593-698) ----------

    /// <summary>C++ <c>prisoner-&gt;setPrisonMode(true, cage)</c> (FinishCageEntry, PrisonerUtil.cpp:690), spike S11's recipe: a carried prisoner is
    /// let go first (<c>CarrySystem.LetGo</c>: Imprison refuses a carried body); one already held in this cage stays as it is (Kenshi's
    /// setPrisonMode on the cage it is in changes nothing); one held in another cage is released there first (releaser -1); then
    /// <c>world.Fixtures.Imprison(world, prisoner, cage)</c> (FixtureRegistry.cs:1593-1640: held at the cage, its orders dropped, the cage LOCKED,
    /// put in by the system: no carrier named, so no bounty claim). False when Imprison refused (the cage taken, the character carrying
    /// something) or the id is stale. Serial step.</summary>
    internal static bool PutInCage(World world, CharacterId prisoner, Fixture cage)
    {
        var t = world.Characters;
        if (!t.TryResolveNext(prisoner, out int s) || t.Cold(s) is not { } c) return false;
        if (!c.CarriedBy.IsNone) PgEngine.LetGo(world, c.CarriedBy);
        if (c.IsImprisoned && c.InWhat == cage.Id && cage.Occupant == prisoner) return true;
        if (c.IsImprisoned) Release(world, prisoner);
        return world.Fixtures.Imprison(world, prisoner, cage);
    }

    /// <summary>C++ <c>building-&gt;destroyed</c> of a cage: its own player site destroyed (<c>BuildSite.Destroyed</c>, BuildingRegistry.cs:123; the site whose id is the
    /// cage's, as IsBroken: a house it stands in is not read). False for a town cage (a destroyed town house's fixtures go with its ruin) and for no site.</summary>
    internal static bool IsDestroyed(World world, Fixture cage) =>
        world.Buildings.Find(cage.Id) is { Destroyed: true };

    /// <summary>C++ <c>cage-&gt;isBroken()</c>: the cage's machine is broken (<c>Machine.Broken</c>, Machines.cs:339-343, the save's <c>broken</c> =
    /// UseableStuff +0x3b6: set by every dismantle step), which its lock mirrors as <c>LockState.OwnerBroken</c> (Fixtures.cs:187-191; set from the cage's own site only, FixtureRegistry.cs:684,
    /// ConstructionSystem.cs:1792: a house the cage stands in is not read).</summary>
    internal static bool IsBroken(World world, Fixture cage) =>
        cage.Lock is { OwnerBroken: true } || world.Machines.Find(cage.Id)?.Broken == true;

    /// <summary>C++ <c>cage-&gt;getDoorLock() != NULL</c>: the fixture has a lock (Fixture.Lock, Fixtures.cs:271).</summary>
    internal static bool HasLock(Fixture cage) => cage.Lock is not null;

    /// <summary>C++ <c>AI::isDoorLocked(hand, pos)</c> (the KenshiLib export the C++ verifies with, "including lock strength and broken-lock
    /// checks", PrisonerUtil.cpp:546-561): <c>LockState.EffectivelyLocked</c> (Fixtures.cs:194-196: locked, neither it nor its owner broken, level
    /// at least 1).</summary>
    internal static bool IsLocked(Fixture cage) => cage.Lock is { EffectivelyLocked: true };

    /// <summary>TEST ONLY (pg imprison, spike S11's recipe promoted from src/Spikes/Spikes.cs Cage): lets go of the character if someone carries it
    /// (CarrySystem.LetGo), releases it if it is imprisoned (Release, releaser -1: no crime, not escaped), then puts it in the cage (Imprison,
    /// which locks the cage). Serial step only.</summary>
    internal static (bool Found, bool Released, bool Imprisoned) Imprison(World world, CharacterId who, Fixture cage)
    {
        var t = world.Characters;
        if (!t.TryResolveNext(who, out int s) || t.Cold(s) is not { } c) return (false, false, false);
        var carry = world.Systems.OfType<CarrySystem>().FirstOrDefault();
        if (!c.CarriedBy.IsNone && carry is not null && t.TryResolveNext(c.CarriedBy, out int cs)) carry.LetGo(world, cs);
        bool released = c.IsImprisoned && Release(world, who);
        bool imprisoned = world.Fixtures.Imprison(world, who, cage);
        return (true, released, imprisoned);
    }
}
