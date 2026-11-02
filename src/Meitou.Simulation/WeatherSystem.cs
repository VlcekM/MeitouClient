using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// What the weather does to characters (docs/formats/weather.md "Effects on characters"; the character's weather update <c>FUN_1405cf790</c>, <b>Verified</b> by
/// decompilation). For each living character with a body every <see cref="EveryTicks"/> ticks, in the slow-world phase (serial), with that many ticks of character time:
/// <list type="number">
/// <item>Acid, unless the race is immune to it: swimming (water state 3) in a region with <c>acidic water</c> burns every part; otherwise, outdoors, standing (not down or
/// carried), not in a bed or cage and with an empty boots slot, <c>acidic ground</c> burns the legs.</item>
/// <item>Local effects: an outdoor character within an affecting unit's radius takes its burning or gas (dust units do nothing here).</item>
/// <item>The region's weather, while exposed (outdoors or on top of a building) and not immune: its kind and strength go on the character for sight and hearing
/// (<see cref="CharacterCold.Weather"/>); a dust storm sets the medical dust value (accuracy), acid rain burns every part. Rain, burning and gas weathers only do the first.</item>
/// </list>
/// Protection is the sum of the worn pieces' <c>weather protection amount</c> for the kind. A player character that is hurt gets the status notice ("Acid!", "Dust Storm (N% protection)"...,
/// <see cref="CharacterCold.WeatherNotice"/>, cleared at the start of each update as the original clears it), and any character that is hurt fires its dialogue event (61 acid feet, 62 acid rain,
/// 63 acid water, 65 poison gas, 69 burning) at <see cref="Talk"/>. Acid rain is stopped by a shelter (<see cref="Sheltered"/>). Not modelled: the eye-shielding overlay animation, the smoke
/// effect on a burnt character and the global switch at <c>DAT_142134690+0x2f0</c> that skips it all (<b>Unknown</b>).
/// </summary>
public sealed class WeatherSystem(GameConstants constants) : ITickSystem
{
    public GameConstants Constants { get; } = constants;

    /// <summary>The dialogue system the hurt characters' events go to (null: none are fired).</summary>
    public Dialogue.DialogueSystem? Talk { get; set; }

    /// <summary>The records, to read <c>is shelter</c> of the player's buildings (null: no building shelters).</summary>
    public Meitou.Data.GameDatabase? Db { get; set; }

    /// <summary>The buildings' collision, for the shelter's roof (null: no building shelters; the combat system's, <see cref="Combat.CombatSystem.Buildings"/>).</summary>
    public Ragdolls.IRagdollEnvironment? Buildings { get; set; }

    /// <summary>
    /// The dialogue events' pause per character and event, in seconds (<b>engine choice</b>). The original fires its weather events every tick the effect hurts and relies on the event's own
    /// refusals (<c>FUN_140684990</c>: the owner already talking, the lines' repetition limits); here a character that was just offered an event is not offered it again for this long, so a
    /// long storm costs one attempt per period instead of one per tick.
    /// </summary>
    public const float EventPause = 8;

    /// <summary>The event numbers of the weather (docs/game/dialogue.md): acid feet, acid rain, acid water, poison gas, burning.</summary>
    public const int AcidFeetEvent = 61, AcidRainEvent = 62, AcidWaterEvent = 63, GasEvent = 65, BurningEvent = 69;

    /// <summary>How far a shelter's roof may be from the character (<b>engine choice</b>: the original's ray asks the building it hits, here the nearest complete shelter of the player's must be this close).</summary>
    public const float ShelterReach = 120;

    /// <summary>The roof must be more than this over the character's feet (the original's <c>+10</c>).</summary>
    public const float RoofClearance = 10;

    /// <summary>
    /// Meitou (<b>engine choice</b>): a character is updated every this many ticks, on its slot's turn, with that many ticks of time (the original updates each one every frame); the
    /// harm is the same, it lands in steps of a sixth of a game second.
    /// </summary>
    public const int EveryTicks = 5;

    readonly Dictionary<(CharacterId, int), long> eventAfter = [];
    readonly Dictionary<string, bool> shelterRecords = new(StringComparer.Ordinal);

    public void SlowWorld(World world)
    {
        if (world.Weather is not { } source) return;
        var table = world.Characters;
        var next = table.Next;
        // Each character every EveryTicks ticks on its slot's turn, with that many ticks of time.
        float dt = world.TickSeconds * EveryTicks;
        var zones = source.Zones;
        for (int i = (int)(world.Tick % EveryTicks); i < table.HighWater; i += EveryTicks)
        {
            if (!next[i].Alive || table.Cold(i) is not { } cold) continue;
            cold.Weather = WeatherAffecting.None;
            cold.WeatherStrength = cold.WeatherProtection = 0;
            cold.WeatherNotice = WeatherNotice.None;
            if (cold.Medical is not { Dead: false } medical || cold.Race is not { } race) continue;
            var at = next[i].Position;
            if (source.At(at.X, at.Z) is not { } here) continue;
            bool outdoors = world.Sight.Outdoors(i);

            // Acid water and acid ground (immunity to WA_ACID covers both).
            if (!race.ImmuneTo(WeatherAffecting.Acid))
            {
                var carrier = cold.CarriedBy;
                int water = cold.InSomething != Locks.HeldState.None ? 0
                    : table.IsAlive(carrier) && table.Cold(carrier.Slot) is { } c ? c.WaterState : cold.WaterState;
                if (water == 3)
                {
                    if (medical.AcidWater(dt * here.AcidicWater, Constants)) Hurt(world, i, cold, WeatherNotice.AcidWater, 0, AcidWaterEvent);
                }
                else if (outdoors && !medical.Incapacitated && carrier.IsNone && cold.InSomething == Locks.HeldState.None && Barefoot(cold, race)
                    && medical.AcidGround(dt * here.AcidicGround, race, Constants)) Hurt(world, i, cold, WeatherNotice.AcidFeet, 0, AcidFeetEvent);
            }

            // Local effects: the last unit of each kind that reaches the character (a later one replaces an earlier one's strength).
            if (outdoors && zones.Count > 0)
            {
                float burning = 0, gas = 0;
                foreach (var z in zones)
                {
                    if (z.Affect is not (WeatherAffecting.Burning or WeatherAffecting.Gas) || Vector3.DistanceSquared(z.Position, at) > z.Radius * z.Radius) continue;
                    if (z.Affect == WeatherAffecting.Burning) burning = z.Strength; else gas = z.Strength;
                }
                if (burning > 0 && !race.ImmuneTo(WeatherAffecting.Burning))
                {
                    float p = Protection(cold, WeatherAffecting.Burning);
                    if (medical.Burning(dt * burning, p)) Hurt(world, i, cold, WeatherNotice.Burning, p, BurningEvent);
                }
                if (gas > 0 && !race.ImmuneTo(WeatherAffecting.Gas))
                {
                    float p = Protection(cold, WeatherAffecting.Gas);
                    if (medical.Gas(dt * gas, p, Constants)) Hurt(world, i, cold, WeatherNotice.Gas, p, GasEvent);
                }
            }

            // The region's weather.
            var kind = here.Affect;
            if (kind == WeatherAffecting.None || world.Sight.Inside(i) || race.ImmuneTo(kind)) continue;
            float protection = Protection(cold, kind);
            cold.Weather = kind;
            cold.WeatherStrength = here.Strength;
            cold.WeatherProtection = protection;
            if (kind == WeatherAffecting.DustStorm)
            {
                medical.DustStorm(here.Strength, protection);
                // The dust notice shows while the storm still reaches it through its gear: s x (1 - P) above 0 (FUN_140648ef0's caller reads the stored value).
                if (cold.IsPlayer && here.Strength * (1 - protection) > 0) (cold.WeatherNotice, cold.WeatherNoticeProtection) = (WeatherNotice.Dust, protection);
            }
            else if (kind == WeatherAffecting.Acid && !Sheltered(world, i, at))
            {
                if (medical.AcidRain(dt, protection, Constants)) Hurt(world, i, cold, WeatherNotice.AcidRain, protection, AcidRainEvent);
            }
        }
    }

    /// <summary>A hurt character: the status notice for a player character, and the weather's dialogue event for any (<see cref="EventPause"/> apart).</summary>
    void Hurt(World world, int slot, CharacterCold cold, WeatherNotice notice, float protection, int ev)
    {
        if (cold.IsPlayer) (cold.WeatherNotice, cold.WeatherNoticeProtection) = (notice, protection);
        if (Talk is null) return;
        var id = world.Characters.IdOf(slot);
        if (eventAfter.TryGetValue((id, ev), out long after) && world.Tick < after) return;
        if (eventAfter.Count > 4096) eventAfter.Clear();
        eventAfter[(id, ev)] = world.Tick + (long)MathF.Ceiling(EventPause / world.TickSeconds);
        Talk.FireEvent(world, id, CharacterId.None, ev);
    }

    /// <summary>
    /// Whether a roof of a shelter is over the character (<c>FUN_1405cf790</c>: a ray down from 1000 above hits a building more than 10 over the feet whose record has <c>is shelter</c>).
    /// <b>Engine choice</b>: the collision query does not say which building a triangle belongs to, so a player's complete building with <c>is shelter</c> within <see cref="ShelterReach"/> of the
    /// character is the one assumed hit, and the ray counts when it meets any building's collision more than <see cref="RoofClearance"/> over the feet. Buildings of the towns are not asked
    /// (the base game has none with the flag; the three that have it are the player's tents of a mod).
    /// </summary>
    bool Sheltered(World world, int slot, Vector3 at)
    {
        if (Db is null || Buildings is null || world.Buildings.Count == 0) return false;
        bool near = false;
        float reach2 = ShelterReach * ShelterReach;
        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Destroyed || site.Ruined || Vector2.DistanceSquared(new Vector2(site.Position.X, site.Position.Z), new Vector2(at.X, at.Z)) > reach2) continue;
            if (!shelterRecords.TryGetValue(site.Record, out bool shelter)) shelterRecords[site.Record] = shelter = Db.Find(site.Record)?.GetBool("is shelter") == true;
            if (!shelter) continue;
            near = true;
            break;
        }
        if (!near) return false;
        var from = new Vector3(at.X, at.Y + 1000, at.Z);
        var seg = new Vector3(0, -(1000 - RoofClearance), 0);
        Combat.SolidHits.Hit(world.Walkability, Buildings, from, seg, 50, out bool building);
        return building;
    }

    /// <summary>
    /// The worn protection from <paramref name="kind"/> (<c>FUN_140887030</c> builds it per kind into CharStats <c>+0x58</c>; the medical functions sum the worn list the same way):
    /// the sum of <c>weather protection amount</c> over the worn pieces that list the kind, unclamped.
    /// </summary>
    public static float Protection(CharacterCold cold, WeatherAffecting kind)
    {
        float sum = 0;
        if (cold.Fighter is { } f)
            foreach (var piece in f.Armour)
                if (piece.Data.Protects(kind)) sum += piece.Data.WeatherProtectionAmount;
        return sum;
    }

    /// <summary>
    /// The boots slot exists and is empty (the original: inventory section 9 found and empty). Engine choice: a race with <c>no shoes</c> has no boots slot, as the character
    /// generator gives it none.
    /// </summary>
    static bool Barefoot(CharacterCold cold, Meitou.Data.Gameplay.Bodies.RaceData race) =>
        !race.NoShoes && (cold.Inventory is not { } inv || !inv.Items.Exists(item => item.Section == "boots"));

    /// <summary>
    /// The sight range factor of the weather on a looker (<c>FUN_140851bc0</c>, <b>Verified</b>): lerp(s x (1 - protection), 1, 0.4) in a dust storm, lerp(.., 1, 0.75) in any other
    /// affecting weather (unclamped: protection above 1 lengthens the sight); 1 without weather.
    /// </summary>
    public static float SightFactor(CharacterCold cold) => cold.Weather == WeatherAffecting.None ? 1
        : 1 + ((cold.Weather == WeatherAffecting.DustStorm ? 0.4f : 0.75f) - 1) * cold.WeatherStrength * (1 - cold.WeatherProtection);

    /// <summary>
    /// The noise range factor of the weather on a mover (<c>FUN_140851d10</c>, <b>Verified</b>): lerp(s, 1, 0.25) in a dust storm, lerp(s, 1, 0.75) in rain or acid rain, 1 otherwise;
    /// no protection term. It scales both ends of the noise range except a sneaker's 80.
    /// </summary>
    public static float NoiseFactor(CharacterCold cold) => cold.Weather switch
    {
        WeatherAffecting.DustStorm => 1 - 0.75f * cold.WeatherStrength,
        WeatherAffecting.Rain or WeatherAffecting.Acid => 1 - 0.25f * cold.WeatherStrength,
        _ => 1,
    };
}

/// <summary>The status notice of a weather effect that hurt a player character this tick (docs/formats/weather.md "Effects on characters"); the HUD words it.</summary>
public enum WeatherNotice : byte { None, AcidWater, AcidFeet, AcidRain, Dust, Burning, Gas }
