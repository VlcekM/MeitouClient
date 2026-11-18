using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// The save: the MEDICAL_STATE fields written and read, the limb bits and the blood each part shows on the body.
/// </summary>
public sealed partial class MedicalState
{
    // ------------------------------------------------------------------ save keys

    /// <summary>
    /// The save key of <see cref="Decapitated"/> (a Meitou key).
    /// </summary>
    const string DecapitatedKey = "meitou decapitated";

    /// <summary>
    /// The save key prefix of an extra limb's state (a Meitou key, <see cref="IsExtraLimb"/>): <c>meitou limb&lt;k&gt;</c> for part k, the index of
    /// its <c>sid&lt;k&gt;</c>.
    /// </summary>
    const string ExtraLimbKey = "meitou limb";

    /// <summary>
    /// Writes the MEDICAL_STATE fields (docs/formats/save.md): the bools <c>coma dead unconcious incapacitated</c>, the floats <c>blood bleeding hung
    /// fed KO</c> and per part <c>k</c> <c>flesh hit bandage rig stun wear hitmult</c> and the string <c>sid</c>. Wounds are not part of these keys;
    /// the limbs (<see cref="LimbBits"/>) are written by the save code with their items. An extra limb's state goes into <paramref name="ints"/>
    /// (<see cref="WriteExtraLimbs"/>); a body with one part per limb slot, as every base-game race, writes none.
    /// </summary>
    public void WriteSave(IDictionary<string, float> floats, IDictionary<string, bool> bools, IDictionary<string, string> strings,
        IDictionary<string, int>? ints = null)
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

        if (ints is not null) WriteExtraLimbs(ints);
    }

    /// <summary>
    /// Writes <c>meitou limb&lt;k&gt;</c> (Meitou-only, docs/formats/save.md "Limbs") for each extra limb that is not original, after removing every
    /// such key the record already has: a record read from a save is written again, and its keys may name positions that hold other parts now (an
    /// extra limb healed, or a mod's part dropped). The original reads its keys by name, so it ignores these; the <c>limbs</c> value keeps the first
    /// part of each slot, as the original's one part per slot.
    /// </summary>
    void WriteExtraLimbs(IDictionary<string, int> ints)
    {
        RemoveExtraLimbKeys(ints);
        for (int k = 0; k < _parts.Length; k++)
            if (IsExtraLimb(k) && _parts[k].Limb != LimbState.Original) ints[ExtraLimbKey + k] = (int)_parts[k].Limb;
    }

    /// <summary>
    /// Removes every <c>meitou limb&lt;k&gt;</c> key (the prefix and digits only) from <paramref name="ints"/>; a record without one, as every
    /// base-game character's, is left as it is.
    /// </summary>
    static void RemoveExtraLimbKeys(IDictionary<string, int> ints)
    {
        List<string>? stale = null;
        foreach (string key in ints.Keys)
            if (IsExtraLimbKey(key)) (stale ??= []).Add(key);

        if (stale is null) return;
        foreach (string key in stale) ints.Remove(key);
    }

    /// <summary>
    /// Whether <paramref name="key"/> is <c>meitou limb</c> followed by one or more digits.
    /// </summary>
    static bool IsExtraLimbKey(string key)
    {
        if (key.Length <= ExtraLimbKey.Length || !key.StartsWith(ExtraLimbKey, StringComparison.Ordinal)) return false;

        foreach (char c in key.AsSpan(ExtraLimbKey.Length))
            if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>
    /// Whether part <paramref name="index"/> is an extra limb, one the <c>limbs</c> value has no bits for: a part of limb slot 0 to 3 after the first
    /// of that slot (a second left arm), or a part whose <c>body part type</c> is none of the four (a tail, Meitou-only,
    /// <see cref="BodyPartTemplate.HasBaseType"/>). No base-game race has either.
    /// </summary>
    public bool IsExtraLimb(int index)
    {
        var template = _parts[index].Template;
        if (!template.HasBaseType) return true;

        int slot = template.LimbSlot;
        if (slot is < 0 or >= 4) return false;

        for (int i = 0; i < index; i++)
            if (_parts[i].Template.LimbSlot == slot) return true;
        return false;
    }

    /// <summary>
    /// The blood each of the first 8 parts shows on the body (<c>FUN_140530b30</c>, docs/game/blood-decals.md): 1 - clamp((flesh - stun + bandage) /
    /// (base x multiplier x scale - wear), 0, 1), byte k = part k scaled to 0..255 (a part with no maximum shows none).
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

    /// <summary>
    /// The int <c>limbs</c> (<c>FUN_1400cfb50</c>, docs/formats/save.md "Limbs"): the <see cref="LimbState"/> of limb slot k in bits 2k..2k+1, from
    /// the slot's first part (<see cref="LimbPart"/>, the part a load applies them to). A body with one part per slot, as every base-game race, has
    /// no other; an extra limb is saved apart (<see cref="WriteExtraLimbs"/>), not merged into its slot's bits.
    /// </summary>
    public int LimbBits
    {
        get
        {
            int bits = 0, seen = 0;
            foreach (var p in _parts)
            {
                if (p.Template.LimbSlot is not (>= 0 and < 4 and var k) || (seen & (1 << k)) != 0) continue;

                seen |= 1 << k;
                bits |= ((int)p.Limb & 3) << (2 * k);
            }
            return bits;
        }
    }

    /// <summary>
    /// The part of limb slot 0 to 3 (0 left arm, 1 right arm, 2 left leg, 3 right leg); null when the anatomy has none.
    /// </summary>
    public HealthPart? LimbPart(int slot)
    {
        foreach (var p in _parts)
            if (p.Template.LimbSlot == slot) return p;
        return null;
    }

    /// <summary>
    /// Applies a saved <c>limbs</c> value (<c>FUN_1400d0190</c>): a stump stays a stump; a replaced slot gets the prosthetic
    /// <paramref name="fitted"/> gives for it, or becomes a stump without one; crushed is kept as the state. The parts' saved flesh, wear and stun
    /// are not touched (the load fits the item without taking its damage).
    /// </summary>
    public void ApplySavedLimbs(int bits, Func<int, (LimbReplacement Limb, Items.ItemInstance? Item)?> fitted)
    {
        for (int slot = 0; slot < 4; slot++)
        {
            if (LimbPart(slot) is not { } p) continue;
            switch ((LimbState)((bits >> (2 * slot)) & 3))
            {
                case LimbState.Stump:
                    p.Limb = LimbState.Stump;
                    break;
                case LimbState.Crushed:
                    p.Limb = LimbState.Crushed;
                    break;
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
    /// Reads what <see cref="WriteSave"/> wrote. <paramref name="resolve"/> finds a LOCATIONAL_DAMAGE template by string id; the base HP of each part
    /// comes from the race's anatomy (100 when the race does not list the part). The parts come back in the original's part order
    /// (<see cref="BodyPartTemplate.MedicalOrder"/>; a vanilla save already has it, an older Meitou save may not), ties in the saved order. An extra
    /// limb's state comes from <paramref name="ints"/> (<see cref="ExtraLimbState"/>); the slots' limbs are applied after
    /// (<see cref="ApplySavedLimbs"/>).
    /// </summary>
    public static MedicalState ReadSave(RaceData race, IReadOnlyDictionary<string, float> floats, IReadOnlyDictionary<string, bool> bools,
        IReadOnlyDictionary<string, string> strings, Func<string, BodyPartTemplate?> resolve, IReadOnlyDictionary<string, int>? ints = null)
    {
        var parts = new List<HealthPart>();
        for (int k = 0; strings.TryGetValue("sid" + k, out var sid); k++)
        {
            if (resolve(sid) is not { } template) continue;
            float baseHp = 100;
            foreach (var a in race.Anatomy) if (a.Part.StringId == sid)
            {
                baseHp = a.BaseHp;
                break;
            }
            float F(string name, float fallback = 0) => floats.TryGetValue(name + k, out float v) ? v : fallback;
            parts.Add(new HealthPart(template, (int)F("hit"), baseHp)
            {
                Flesh = F("flesh"),
                Bandage = F("bandage"),
                Rig = F("rig"),
                Stun = F("stun"),
                Wear = F("wear"),
                HitMult = F("hitmult", 1),
                SelfHealing = race.SelfHealing,
                Limb = ExtraLimbState(ints, k),
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
        state.DropMisplacedLimbKeys();
        foreach (var p in state.Parts) p.WasDown = p.IsDown;
        return state;
    }

    /// <summary>
    /// The state saved for part <paramref name="k"/> as an extra limb (<see cref="WriteExtraLimbs"/>): 1 a stump, 3 crushed; original without the
    /// key or with any other value (the writer writes no other: an extra limb takes no prosthetic, so it is never 2). Whether part k is an extra
    /// limb is known only once the parts are sorted (<see cref="DropMisplacedLimbKeys"/>).
    /// </summary>
    static LimbState ExtraLimbState(IReadOnlyDictionary<string, int>? ints, int k)
    {
        if (ints is null || !ints.TryGetValue(ExtraLimbKey + k, out int saved)) return LimbState.Original;

        return saved switch
        {
            (int)LimbState.Stump => LimbState.Stump,
            (int)LimbState.Crushed => LimbState.Crushed,
            _ => LimbState.Original,
        };
    }

    /// <summary>
    /// Keeps a just-read <c>meitou limb&lt;k&gt;</c> state only on a part that is an extra limb in the loaded order and not vital: one that lands on
    /// a slot's first part, the head or a torso part (a mod's part dropped or changed, a hand-edited key) is undone. Runs before
    /// <see cref="ApplySavedLimbs"/>, so every part is still original unless a key set it.
    /// </summary>
    void DropMisplacedLimbKeys()
    {
        for (int i = 0; i < _parts.Length; i++)
            if (_parts[i].Limb != LimbState.Original && (!IsExtraLimb(i) || _parts[i].Template.Vital)) _parts[i].Limb = LimbState.Original;
    }
}