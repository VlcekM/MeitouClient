using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Saving;

/// <summary>
/// A character's limbs both ways between its <see cref="MedicalState"/> and the MEDICAL_STATE <c>limbs</c> keys
/// (<see cref="SaveLimbs"/>, docs/formats/save.md "Limbs").
/// </summary>
static class SavedLimbs
{
    /// <summary>
    /// Applies the saved limbs of <paramref name="medical"/> (a record of <paramref name="file"/>): stumps, crushed limbs and the fitted prosthetics
    /// as items.
    /// </summary>
    public static void Apply(MedicalState state, SaveFile? file, FcsRecord medical, ItemFactory items)
    {
        int bits = medical.Ints.GetValueOrDefault(SaveLimbs.Key);
        if (bits == 0) return;
        var saved = file is null ? new SaveItem?[SaveLimbs.Slots] : SaveLimbs.Read(file, medical);
        state.ApplySavedLimbs(bits, slot =>
        {
            if (saved[slot] is not { } s || items.Limb(s.BaseId) is not { } limb) return null;
            return (limb, SavedKit.ItemOf(s, items));
        });
    }

    /// <summary>
    /// Writes the limbs of <paramref name="state"/> into <paramref name="medical"/>, the prosthetics' records into <paramref name="file"/>.
    /// </summary>
    public static void Write(MedicalState state, SaveFile file, FcsRecord medical)
    {
        var items = new SaveItem?[SaveLimbs.Slots];
        for (int slot = 0; slot < SaveLimbs.Slots; slot++)
        {
            if (state.LimbPart(slot) is not { Limb: LimbState.Replaced, Replacement: { } limb } p) continue;

            // A prosthetic fitted without an item (none in play yet) is written as a level-0 one of its record,
            // so it loads as fitted rather than as a stump.
            var item = p.ReplacementItem is { } i
                ? SavedKit.FromItem(i)
                : new SaveItem { BaseId = limb.StringId, Level = (int)MathF.Round(p.ReplacementQuality * 100) };
            items[slot] = new SaveItem
            {
                BaseId = item.BaseId,
                MaterialId = item.MaterialId,
                CompanyId = item.CompanyId,
                ColourId = item.ColourId,
                Section = LimbReplacement.SlotOf(slot),
                Quantity = 1,
                Level = item.Level,
                ItemFunction = item.ItemFunction,
                Quality = item.Quality,
                Charges = item.Charges,
                Unique = item.Unique,
                Source = item.Source,
            };
        }

        SaveLimbs.Write(file, medical, state.LimbBits, items);
    }
}