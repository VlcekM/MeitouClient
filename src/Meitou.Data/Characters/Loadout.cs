namespace Meitou.Data.Characters;

/// <summary>A worn item the generator chose: the ARMOUR / CONTAINER, its quality and, for clothing, its material.</summary>
public sealed record LoadoutItem(GameRecord Record)
{
    /// <summary>Item quality 0–100 (ARMOUR: from the CHARACTER's <c>armour grade</c>, docs/characters.md).</summary>
    public int Quality { get; init; }
    /// <summary>The MATERIAL_SPECS_CLOTHING picked from the item's <c>material</c> list, if any.</summary>
    public GameRecord? Material { get; init; }
    /// <summary>The COLOR_DATA the instance carries (its <c>color sid</c>, <see cref="Gameplay.ItemColours"/>); null: none yet, the wearer's scheme applies.</summary>
    public GameRecord? Colour { get; init; }
}

/// <summary>A carried weapon (or crossbow): which record, how it was made, and where it hangs.</summary>
public sealed record LoadoutWeapon(GameRecord Weapon)
{
    /// <summary>WEAPON_MANUFACTURER that made it (CHARACTER <c>weapon level</c>).</summary>
    public GameRecord? Manufacturer { get; init; }
    /// <summary>MATERIAL_SPECS_WEAPON model of the manufacturer (its look).</summary>
    public GameRecord? Model { get; init; }
    /// <summary>The model's level (manufacturer <c>weapon models</c> val0).</summary>
    public int Level { get; init; }
    /// <summary>Inventory section: <c>hip</c> or <c>back</c>.</summary>
    public required string Section { get; init; }
    /// <summary>Row in the section (the item's <c>inventory y</c>); a <c>back</c> item in row 1 or more hangs at <c>back2</c>.</summary>
    public int Row { get; init; }

    /// <summary>The attachment point it is shown at: the section name, <c>back</c> becoming <c>back2</c> below the first row.</summary>
    public string Point => Section == "back" && Row > 0 ? "back2" : Section;
}

/// <summary>
/// A limb that is not the natural one (docs/game/character-stats.md "Limb replacements and robots"): <see cref="State"/> 1 stump, 2 replaced, 3 crushed (the medical
/// <c>LimbState</c>), and for a replaced limb its LIMB_REPLACEMENT record.
/// </summary>
public sealed record LoadoutLimb(int State, GameRecord? Prosthetic = null);

/// <summary>
/// What <see cref="CharacterGenerator"/> rolled for one character: race, gender, a body-file-like appearance (head, hair,
/// colours, sliders, face poses), clothing, backpack and weapons. Feed it to <see cref="CharacterAppearance.Build"/>
/// through <see cref="CharacterOptions.Loadout"/>.
/// </summary>
public sealed class Loadout
{
    public required int Seed { get; init; }
    public GameRecord? Character { get; init; }
    /// <summary>The FACTION it was rolled for (null: the CHARACTER's own); its <c>color</c> is the uniform scheme of what it puts on.</summary>
    public GameRecord? Faction { get; init; }
    public required GameRecord Race { get; init; }
    public required bool Female { get; init; }
    /// <summary>The body file, or a generated CHARACTER_APPEARANCE (<see cref="Generated"/>) when the CHARACTER has none.</summary>
    public required AppearanceFile Appearance { get; init; }
    /// <summary>True when <see cref="Appearance"/> was rolled, false when it is the CHARACTER's own body file.</summary>
    public bool Generated { get; init; }
    /// <summary>Which of the race's <c>morph num</c> faces the face poses came from (−1 for a body file).</summary>
    public int MorphIndex { get; init; } = -1;

    public List<LoadoutItem> Clothing { get; } = [];
    public LoadoutItem? Backpack { get; set; }
    /// <summary>Worn ITEMs (the belt's lantern), which show only through their <c>physics attachment</c>.</summary>
    public List<LoadoutItem> Carried { get; } = [];
    public LoadoutWeapon? Crossbow { get; set; }
    /// <summary>Weapons in equip order (hip first, then back).</summary>
    public List<LoadoutWeapon> Weapons { get; } = [];
    /// <summary>The limbs by slot (0 left arm, 1 right arm, 2 left leg, 3 right leg); null for a natural one.</summary>
    public LoadoutLimb?[] Limbs { get; } = new LoadoutLimb?[4];
    /// <summary>The head was cut off (Meitou, <c>MedicalState.Decapitated</c>): the look has no head, hair, beard or hat.</summary>
    public bool Decapitated { get; set; }
    public List<string> Notes { get; } = [];

    /// <summary>One line per choice, for logs and the viewer.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"seed {Seed}: {Race.Name} {(Female ? "female" : "male")}" + (Generated ? $", generated appearance (face {MorphIndex})" : ", body file");
        foreach (var c in Clothing) yield return $"  wears {c.Record.Name} (quality {c.Quality}" + (c.Material is { } m ? $", {m.Name})" : ")");
        if (Backpack is { } b) yield return $"  backpack {b.Record.Name}";
        if (Crossbow is { } x) yield return $"  crossbow {x.Weapon.Name} at {x.Point}";
        foreach (var w in Weapons)
            yield return $"  weapon {w.Weapon.Name} at {w.Point}" + (w.Manufacturer is { } mf ? $", {mf.Name}" : "") + (w.Model is { } md ? $" {md.Name}" : "") + $" (level {w.Level})";
        for (int k = 0; k < Limbs.Length; k++)
            if (Limbs[k] is { } l) yield return $"  limb {(Limb)k}: " + (l.Prosthetic is { } p ? p.Name : l.State == 1 ? "stump" : $"state {l.State}");
        foreach (var n in Notes) yield return $"  note: {n}";
    }
}
