using System.Numerics;
using Meitou.Data;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The turrets among the placed buildings (<see cref="BuildingRegistry"/>) with their posts, for the turret finder of a character in a player
/// town (<c>Ai.AiSystem.GuardTurretPick</c>). Built in a serial step when the registry changed; the thinks read the list of the last step and
/// test each site's state themselves. Derived from the registry: not saved, not hashed.
/// </summary>
internal sealed class PlacedTurrets
{
    /// <summary>
    /// The registry's revision the list was built for (-1: never).
    /// </summary>
    long _revision = -1;

    /// <summary>
    /// The turret sites in the registry's order.
    /// </summary>
    Entry[] _all = [];

    /// <summary>
    /// A placed turret: its site, its post and the number of the player town it belongs to (0: none).
    /// </summary>
    public readonly record struct Entry(BuildSite Site, TurretPost Post, int Town);

    /// <summary>
    /// The turret sites of the last <see cref="Refresh"/>, in the registry's order.
    /// </summary>
    public IReadOnlyList<Entry> All => _all;

    /// <summary>
    /// Builds the list again when the registry changed. Serial.
    /// </summary>
    public void Refresh(World world, GameDatabase db)
    {
        var reg = world.Buildings;
        if (reg.Revision == _revision) return;

        _revision = reg.Revision;
        var found = new List<Entry>();
        foreach (var site in reg.All)
            if (PostOf(db, site) is { } post) found.Add(new Entry(site, post, TownOf(reg, site)));

        _all = [.. found];
    }

    /// <summary>
    /// The player town of a placed building: the one it names, else (a loaded building that names none) the nearest one whose radius holds it;
    /// 0 with none.
    /// </summary>
    static int TownOf(BuildingRegistry reg, BuildSite site)
    {
        if (site.Town > 0 || site.InNoTown) return site.Town;

        int closest = 0;
        float best = float.MaxValue;
        var at = new Vector2(site.Position.X, site.Position.Z);
        foreach (var town in reg.Towns)
        {
            float distance = Vector2.Distance(new Vector2(town.Position.X, town.Position.Z), at);
            if (distance > town.Radius || distance >= best) continue;

            (closest, best) = (town.Number, distance);
        }

        return closest;
    }

    /// <summary>
    /// A placed turret's post (its functionality's <c>use range</c> and <c>gun turret</c>), facing the way its building does; null for a building
    /// that is no turret.
    /// </summary>
    public static TurretPost? PostOf(GameDatabase db, BuildSite site)
    {
        if (db.Find(site.Record) is not { } record) return null;

        var fn = record.GetReferences("functionality") is [var link, ..] ? db.Find(link.TargetStringId) : null;
        if (fn is null || fn.GetInt("function") != ObjectRanges.TurretFunction) return null;

        var forward = Vector3.Transform(Vector3.UnitZ, site.Rotation);
        string gun = record.GetReferences("gun turret").Select(r => r.TargetStringId).FirstOrDefault() ?? "";

        return new TurretPost(site.Position, MathF.Atan2(forward.X, forward.Z), fn.GetFloat("use range", 10), gun, null);
    }
}
