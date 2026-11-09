using Meitou.Data.Fcs;

namespace Meitou.Data.Gameplay.Bodies;

/// <summary>
/// The creature fields of an ANIMAL_CHARACTER record (docs/game/animals.md). The race, the stats groups (<c>combat stats</c>, ...) and <c>stats
/// randomise</c> are read the way a CHARACTER's are; this holds what only animals have. Field names <b>Verified</b> against the install's 43 records;
/// what the game does with each is in the doc.
/// </summary>
public sealed record AnimalData
{
    /// <summary>
    /// The ANIMAL_CHARACTER's string id.
    /// </summary>
    public required string StringId { get; init; }

    /// <summary>
    /// The first <c>weapon</c> reference: the natural weapon (a WEAPON record of a creature skill category, 9 to 20); null for the few animals
    /// without one.
    /// </summary>
    public string? WeaponId { get; init; }

    /// <summary>
    /// The first <c>weapon level</c> reference: a WEAPON_MANUFACTURER (Ancient for nearly all).
    /// </summary>
    public string? WeaponLevelId { get; init; }

    /// <summary>
    /// <c>animal strength</c> (int; <c>CharStatsAnimal</c> +0x230). Its consumer was not found (<b>Unknown</b>); kept, not used.
    /// </summary>
    public int AnimalStrength { get; init; }

    /// <summary>
    /// <c>HP mult</c>, 1 when 0 or below (<b>Observed</b>, <c>FUN_14062f000</c>): the character's slot-0x238 scale (part body scale and
    /// blood capacity).
    /// </summary>
    public float HpMult { get; init; } = 1;

    /// <summary>
    /// <c>scale min</c> / <c>scale max</c>: the size the animal has at age 0 and 1 (slot 0x390 = lerp(age, min, max)).
    /// </summary>
    public float ScaleMin { get; init; } = 1;

    /// <summary>
    /// <c>scale max</c>: the size at age 1 (default 1).
    /// </summary>
    public float ScaleMax { get; init; } = 1;

    /// <summary>
    /// <c>lifespan</c> (<c>+0x6f8</c>): the game days the animal takes to grow from age 0 to 1 (<see cref="AgePerSecond"/>,
    /// docs/game/animals.md "Ageing").
    /// </summary>
    public float Lifespan { get; init; }

    /// <summary>
    /// <c>smell blood</c>, <c>smell eggs</c> (<c>+0x6e8</c>, <c>+0x6ec</c>, getters at slots 0x430 / 0x438), <c>turn rate</c>: read and kept; their
    /// consumers were not found (<b>Unknown</b>).
    /// </summary>
    public float SmellBlood { get; init; }

    /// <summary>
    /// <c>smell eggs</c> (<c>+0x6ec</c>); consumer <b>Unknown</b>.
    /// </summary>
    public float SmellEggs { get; init; }

    /// <summary>
    /// <c>turn rate</c>; consumer <b>Unknown</b>.
    /// </summary>
    public float TurnRate { get; init; }

    /// <summary>
    /// The age an animal stops growing at (<c>FUN_1405cdf00</c> grows it only below 1.1, <b>Observed</b>); a spawned or saved age can be higher
    /// (saves hold up to 2.07).
    /// </summary>
    public const float GrowUntil = 1.1f;

    /// <summary>
    /// The visual size is refreshed only below this age (<c>FUN_1405cdf00</c>, <b>Observed</b>).
    /// </summary>
    public const float ResizeBelow = 0.95f;

    /// <summary>
    /// The age step at which the visual size is refreshed (<c>FUN_1405cdf00</c>: when the age has moved more than 0.05 since the last refresh,
    /// <b>Observed</b>).
    /// </summary>
    public const float ResizeStep = 0.05f;

    /// <summary>
    /// The age gained per second of character time: 1 / (day length x <c>lifespan</c>), the day length 2618.18 s (<c>FUN_14066cd50</c>); 0 for a
    /// lifespan of 0 or less.
    /// </summary>
    public float AgePerSecond => Lifespan > 0 ? 1 / (Construction.MachineData.DaySeconds * Lifespan) : 0;

    /// <summary>
    /// The animal's size at <paramref name="age"/> (<c>CharacterAnimal</c> slot 0x390, <b>Observed</b>): lerp(age, scale min, scale max),
    /// not clamped.
    /// </summary>
    public float Scale(float age) => ScaleMin + (ScaleMax - ScaleMin) * age;

    /// <summary>
    /// The age word of the animal buying window's Age row (slot 0x3a0, <c>FUN_1408882b0</c>, <b>Observed</b>): below 0.4 "Pup", below 0.6 "Teen", up
    /// to 1.1 "Adult", above "Elder".
    /// </summary>
    public static string AgeWord(float age) => age < 0.4f ? "Pup" : age < 0.6f ? "Teen" : age <= 1.1f ? "Adult" : "Elder";

    /// <summary>
    /// The animal fields of a record; null when it is not an ANIMAL_CHARACTER.
    /// </summary>
    public static AnimalData? From(GameRecord? r)
    {
        if (r is not { Type: FcsRecordType.AnimalCharacter }) return null;

        static string? First(GameRecord r, string list) => r.GetReferences(list) is { Count: > 0 } refs ? refs[0].TargetStringId : null;

        float hp = r.GetFloat("HP mult", 1);
        return new AnimalData
        {
            StringId = r.StringId,
            WeaponId = First(r, "weapon"),
            WeaponLevelId = First(r, "weapon level"),
            AnimalStrength = r.GetInt("animal strength"),
            HpMult = hp <= 0 ? 1 : hp,
            ScaleMin = r.GetFloat("scale min", 1),
            ScaleMax = r.GetFloat("scale max", 1),
            Lifespan = r.GetFloat("lifespan"),
            SmellBlood = r.GetFloat("smell blood"),
            SmellEggs = r.GetFloat("smell eggs"),
            TurnRate = r.GetFloat("turn rate"),
        };
    }
}
