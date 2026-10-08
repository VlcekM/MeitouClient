using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
    // ------------------------------------------------------------------ save keys

    /// <summary>Writes the MEDICAL_STATE fields (docs/formats/save.md): the bools <c>coma dead unconcious incapacitated</c>, the floats <c>blood bleeding hung fed KO</c> and per part <c>k</c> <c>flesh hit bandage rig stun wear hitmult</c> and the string <c>sid</c>. Limb states and wounds are not part of these keys.</summary>
    public void WriteSave(IDictionary<string, float> floats, IDictionary<string, bool> bools, IDictionary<string, string> strings)
    {
        bools["coma"] = Coma;
        bools["dead"] = Dead;
        bools["unconcious"] = Unconscious;
        bools["incapacitated"] = Incapacitated;
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
    /// Reads what <see cref="WriteSave"/> wrote. <paramref name="resolve"/> finds a LOCATIONAL_DAMAGE template by string id; the base HP of each part comes from the race's anatomy
    /// (100 when the race does not list the part). The parts come back in the saved order.
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
        var state = new MedicalState(parts)
        {
            Blood = floats.GetValueOrDefault("blood"),
            Hunger = floats.GetValueOrDefault("hung", MaxHunger),
            Fed = floats.GetValueOrDefault("fed"),
            KoTimer = floats.GetValueOrDefault("KO"),
            Unconscious = bools.GetValueOrDefault("unconcious"),
            Dead = bools.GetValueOrDefault("dead"),
            Bleeding = floats.GetValueOrDefault("bleeding"),
        };
        foreach (var p in state.Parts) p.WasDown = p.IsDown;
        return state;
    }
}
