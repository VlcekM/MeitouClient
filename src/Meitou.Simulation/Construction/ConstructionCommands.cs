using System.Numerics;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Construction;

/// <summary>
/// One confirmed ghost: the BUILDING record, its absolute position, its rotation (tilt included), floor and parent building instance (null outdoors).
/// </summary>
public sealed record PlannedBuilding(string Record, Vector3 Position, Quaternion Rotation, int Floor = 0, string? Parent = null,
    Vector3 BoundsSize = default);

/// <summary>
/// Build mode's Confirm: creates a site per plan, in order, for the player faction (docs/simulation.md "Buildings"). A plan failing the data rules is
/// dropped with a message.
/// </summary>
public sealed record PlaceBuildings(IReadOnlyList<PlannedBuilding> Plans) : SimCommand;

/// <summary>
/// Player orders 2 Build / 95 Repair: the characters (the selection when empty) deliver what they carry to <see cref="Site"/> and work it.
/// </summary>
public sealed record BuildOrder(IReadOnlyList<CharacterId> Characters, string Site, bool Repair = false) : OrderCommand;

/// <summary>
/// Player order 96 Dismantle: the characters work <see cref="Site"/> down to 0, then it is removed with the refund.
/// </summary>
public sealed record DismantleOrder(IReadOnlyList<CharacterId> Characters, string Site) : OrderCommand;

/// <summary>
/// The selection panel's Dismantle once confirmed (<c>Building::confirmDismantle</c>): the player's site is dismantled at once, its furniture destroyed.
/// </summary>
public sealed record ConfirmDismantle(string Site) : SimCommand;

/// <summary>
/// Removes a site with nothing delivered at once (engine); a site with deliveries needs <see cref="DismantleOrder"/>.
/// </summary>
public sealed record CancelSite(string Site) : SimCommand;

/// <summary>
/// The site's <c>pause</c> flag: auto-jobs skip a paused site.
/// </summary>
public sealed record PauseSite(string Site, bool Paused) : SimCommand;

/// <summary>
/// The panel's [UPGRADE]: the site becomes its record's <c>upgrades to</c> BUILDING in place and is built up to it (<see cref="BuildingUpgrades"/>).
/// </summary>
public sealed record UpgradeSite(string Site) : SimCommand;

/// <summary>
/// The panel's [DOWNGRADE] on an unfinished upgrade: the site goes back to the BUILDING that upgrades to it (<see cref="BuildingUpgrades"/>).
/// </summary>
public sealed record DowngradeSite(string Site) : SimCommand;

/// <summary>
/// The panel's Open to public toggle on a building of the player's: a site's <see cref="BuildSite.Public"/>, else an owned town building's flag in the fixtures.
/// </summary>
public sealed record SetBuildingPublic(string Building, bool Public) : SimCommand;

/// <summary>
/// The town panel's name box accepted (<c>FUN_140722d60</c>): the player's outpost <see cref="Town"/> is renamed (no uniqueness test; an empty name
/// is ignored, engine choice).
/// </summary>
public sealed record RenameTown(int Town, string Name) : SimCommand;

/// <summary>
/// The town panel's Open to public tick on the player's outpost <see cref="Town"/> (<see cref="PlayerTown.Public"/>).
/// </summary>
public sealed record SetTownPublic(int Town, bool Public) : SimCommand;
