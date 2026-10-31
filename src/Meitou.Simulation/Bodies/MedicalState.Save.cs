using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
    // ------------------------------------------------------------------ save keys

    const string DecapitatedKey = "meitou decapitated";

    /// <summary>Writes the MEDICAL_STATE fields (docs/formats/save.md): the bools <c>coma dead unconcious incapacitated</c>, the floats <c>blood bleeding hung fed KO</c> and per part <c>k</c> <c>flesh hit bandage rig stun wear hitmult</c> and the string <c>sid</c>. Wounds are not part of these keys; the limbs (<see cref="LimbBits"/>) are written by the save code with their items.</summary>
    public void WriteSave(IDictionary<string, float> floats, IDictionary<string, bool> bools, IDictionary<string, string> strings)
    {
        bools["coma"] = Coma;
        bools["dead"] = Dead;
        bools["unconcious"] = Unconscious;
        bools["incapacitated"] = Incapacitated;
        // Meitou-only (docs/formats/save.md "Limbs"): the original reads its keys by name, so it ignores this one.
        if (Decapitated) bools[DecapitatedKey] = true;
        floats["blood"] = Blood;
        floats["bleeding"] = Bleeding;
        floats["hung"] = Hunger;
        floats["fed"] = Fed;
        floats["KO"] = KoTimer;
        for (int k = 0; k < Parts.Count; k++)
        {
            var p = Parts[k];
            floats["flesh" + k] = p.Flesh;
            floats["hit" + k] = p.HitWeight;
            floats["bandage" + k] = p.Bandage;
            floats["rig" + k] = p.Rig;
            floats["stun" + k] = p.Stun;
            floats["wear" + k] = p.Wear;
            floats["hitmult" + k] = p.HitMult;
            strings["sid" + k] = p.Template.StringId;
        }
    }

    /// <summary>
    /// The int <c>limbs</c> (<c>FUN_1400cfb50</c>, docs/formats/save.md "Limbs"): the <see cref="LimbState"/> of limb slot k in bits 2k..2k+1.
    /// </summary>
    /// <summary>
    /// The blood each of the first 8 parts shows on the body (<c>FUN_140530b30</c>, docs/game/blood-decals.md): 1 - clamp((flesh - stun + bandage) / (base x multiplier x scale - wear), 0, 1),
    /// byte k = part k scaled to 0..255 (a part with no maximum shows none).
    /// </summary>
    public static ulong BloodAmounts(MedicalState state)
    {
        ulong bits = 0;
        for (int k = 0; k < 8 && k < state.Parts.Count; k++)
        {
            var p = state.Parts[k];
            float max = p.MaxHp;
            if (!(max > 0)) continue;
            float amount = 1 - Math.Clamp((p.Flesh - p.Stun + p.Bandage) / max, 0f, 1f);
            bits |= (ulong)Math.Clamp((int)MathF.Round(amount * 255), 0, 255) << (8 * k);
        }
        return bits;
    }

    public int LimbBits
    {
        get
        {
            int bits = 0;
            foreach (var p in parts)
                if (p.Template.LimbSlot is >= 0 and < 4 and var k) bits |= ((int)p.Limb & 3) << (2 * k);
            return bits;
        }
    }

    /// <summary>The part of limb slot 0 to 3 (0 left arm, 1 right arm, 2 left leg, 3 right leg); null when the anatomy has none.</summary>
    public HealthPart? LimbPart(int slot)
    {
        foreach (var p in parts)
            if (p.Template.LimbSlot == slot) return p;
        return null;
    }

    /// <summary>
    /// Applies a saved <c>limbs</c> value (<c>FUN_1400d0190</c>): a stump stays a stump; a replaced slot gets the prosthetic <paramref name="fitted"/> gives for it, or becomes a
    /// stump without one; crushed is kept as the state. The parts' saved flesh, wear and stun are not touched (the load fits the item without taking its damage).
    /// </summary>
    public void ApplySavedLimbs(int bits, Func<int, (LimbReplacement Limb, Items.ItemInstance? Item)?> fitted)
    {
        for (int slot = 0; slot < 4; slot++)
        {
            if (LimbPart(slot) is not { } p) continue;
            switch ((LimbState)((bits >> (2 * slot)) & 3))
            {
                case LimbState.Stump: p.Limb = LimbState.Stump; break;
                case LimbState.Crushed: p.Limb = LimbState.Crushed; break;
                case LimbState.Replaced:
                    if (fitted(slot) is { } f)
                    {
                        p.Limb = LimbState.Replaced;
                        p.Replacement = f.Limb;
                        p.ReplacementItem = f.Item;
                        p.ReplacementQuality = Math.Clamp((f.Item?.Level ?? 0) * 0.01f, 0, 1);
                    }
                    else p.Limb = LimbState.Stump;
                    break;
            }
            p.WasDown = p.IsDown;
        }
    }

    /// <summary>
    /// Reads what <see cref="WriteSave"/> wrote. <paramref name="resolve"/> finds a LOCATIONAL_DAMAGE template by string id; the base HP of each part comes from the race's anatomy
    /// (100 when the race does not list the part). The parts come back in the original's part order (<see cref="BodyPartTemplate.MedicalOrder"/>; a vanilla save already
    /// has it, an older Meitou save may not), ties in the saved order. The limbs are applied after (<see cref="ApplySavedLimbs"/>).
    /// </summary>
    public static MedicalState ReadSave(RaceData race, IReadOnlyDictionary<string, float> floats, IReadOnlyDictionary<string, bool> bools,
        IReadOnlyDictionary<string, string> strings, Func<string, BodyPartTemplate?> resolve)
    {
        var parts = new List<HealthPart>();
        for (int k = 0; strings.TryGetValue("sid" + k, out var sid); k++)
        {
            if (resolve(sid) is not { } template) continue;
            float baseHp = 100;
            foreach (var a in race.Anatomy) if (a.Part.StringId == sid) { baseHp = a.BaseHp; break; }
            float F(string name, float fallback = 0) => floats.TryGetValue(name + k, out float v) ? v : fallback;
            parts.Add(new HealthPart(template, (int)F("hit"), baseHp)
            {
                Flesh = F("flesh"), Bandage = F("bandage"), Rig = F("rig"), Stun = F("stun"), Wear = F("wear"), HitMult = F("hitmult", 1),
                SelfHealing = race.SelfHealing,
            });
        }
        var state = new MedicalState(parts.OrderBy(p => p.Template.MedicalOrder))
        {
            Blood = floats.GetValueOrDefault("blood"),
            Hunger = floats.GetValueOrDefault("hung", MaxHunger),
            Fed = floats.GetValueOrDefault("fed"),
            KoTimer = floats.GetValueOrDefault("KO"),
            Unconscious = bools.GetValueOrDefault("unconcious"),
            Coma = bools.GetValueOrDefault("coma"),
            Dead = bools.GetValueOrDefault("dead"),
            Bleeding = floats.GetValueOrDefault("bleeding"),
            Decapitated = bools.GetValueOrDefault(DecapitatedKey),
        };
        foreach (var p in state.Parts) p.WasDown = p.IsDown;
        return state;
    }
}
