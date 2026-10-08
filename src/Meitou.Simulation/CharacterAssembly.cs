using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;

namespace Meitou.Simulation;

/// <summary>
/// The spawn-time assembly of a character: from its CHARACTER record and race it gets <c>Race</c>, <c>Stats</c>, <c>Medical</c>, <c>Inventory</c>,
/// <c>Fighter</c>, <c>WaterFactor</c> and the top speed of the speed chain (docs/simulation.md "Bodies", "Combat"). The population
/// system uses the instance; the race lookups are static so the save code reads the same record the same way.
/// </summary>
sealed class CharacterAssembly(PopulationData data)
{
    /// <summary>The id of the race a CHARACTER record names: its first <c>race</c> reference with a positive count; null when it has none.</summary>
    public static string? RaceId(GameRecord record)
    {
        foreach (var r in record.GetReferences("race"))
            if (r.Values.Value0 > 0) return r.TargetStringId;
        return null;
    }

    /// <summary>The RACE record of a character: the one its generated look was made with, else the one its record names.</summary>
    public static GameRecord? RaceRecord(GameDatabase db, string recordId, CharacterAppearance? look)
    {
        var race = look?.Race;
        if (race is null && db.Find(recordId) is { } rec && RaceId(rec) is { } id) race = db.Find(id);
        return race;
    }

    /// <summary>The <see cref="RaceData"/> of a RACE record id, read once per id into <paramref name="cache"/> (null for an id that is not a race).</summary>
    public static RaceData? RaceDataOf(GameDatabase db, string raceId, Dictionary<string, RaceData?> cache)
    {
        if (cache.TryGetValue(raceId, out var cached)) return cached;
        var race = db.Find(raceId) is { Type: FcsRecordType.RACE } rec ? RaceData.From(rec, db) : null;
        cache[raceId] = race;
        return race;
    }

    /// <summary>The race record of a spawned character (see <see cref="RaceRecord"/>).</summary>
    public GameRecord? RaceOf(string recordId, CharacterAppearance? look) => RaceRecord(data.Db, recordId, look);

    /// <summary>
    /// The top speed a character starts with: the 45 of a creature without a race. A character with a race gets its real top speed from <see cref="Attach"/>
    /// (docs/game/pathfinding.md "Speed"), so the lerp over the race's speed skills that stood in for it before stats existed is gone.
    /// </summary>
    public const float StandInMaxSpeed = 45;

    /// <summary>The walk speed of a character: the race's <c>walk speed</c>, 15 without a race.</summary>
    public static float WalkSpeed(GameRecord? race) => race?.GetFloat("walk speed", 15) ?? 15;

    /// <summary>The weapon in hand (the first weapon: hip, then back; its quality is its level x 0.01) and the worn armour (quality / 100), docs/simulation.md "Combat".</summary>
    Fighter MakeFighter(Inventory inventory)
    {
        var combat = data.Combat;
        WeaponInstance? weapon = null;
        foreach (var section in new[] { "hip", "back" })
            foreach (var item in inventory.Items.Where(i => i.Section == section && i.Type == FcsRecordType.WEAPON))
                if (weapon is null && combat.Weapon(item.Record) is { } w)
                    weapon = WeaponInstance.Create(item.Level * 0.01f, w, combat.Manufacturer(item.CompanyId), combat.WeaponMaterial(item.MaterialId), combat.Constants);
        var armour = inventory.Items.Where(i => i.Type == FcsRecordType.ARMOUR && combat.Armour(i.Record) is not null)
            .Select(i => ArmourPiece.Create(combat.Armour(i.Record)!, i.Quality * 0.01f));
        return new Fighter(weapon, armour);
    }

    /// <summary>Gives a character with a race its stats and medical state, its inventory and fighter, and the top speed of the speed chain (stage 7). Animals (no race) are left as they are.</summary>
    public void Attach(CharacterId id, CharacterCold cold, ref CharacterHot hot, GameRecord? record, GameRecord? race, ulong seed)
    {
        if (race is null) return;
        var (raceData, stats, medical) = data.Bodies.Create(race, record, seed, Rng.Key(id));
        cold.Race = raceData;
        cold.Stats = stats;
        cold.Medical = medical;
        cold.Inventory = data.Items.Build(record, cold.Appearance?.Loadout);
        cold.Fighter = MakeFighter(cold.Inventory);
        cold.WaterFactor = raceData.WaterAvoidance * (cold.Faction == data.PlayerFaction && data.PlayerFaction >= 0 ? 0.5f : 1);
        hot.MaxSpeed = Speed.Run(raceData, stats, medical, 1);
    }
}
