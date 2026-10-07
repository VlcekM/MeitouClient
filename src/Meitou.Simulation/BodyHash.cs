using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>Adds a character's stats and medical state to the state hash (the body classes themselves know nothing of it).</summary>
static class BodyHash
{
    public static void Add(ref StateHasher h, CharacterStats? stats, MedicalState? medical)
    {
        if (stats is null) h.Add(-1);
        else foreach (float v in stats.ToArray()) h.Add(v);
        if (medical is null)
        {
            h.Add(-1);
            return;
        }
        h.Add(medical.Parts.Count);
        foreach (var p in medical.Parts)
        {
            h.Add(p.Flesh);
            h.Add(p.Stun);
            h.Add(p.Bandage);
            h.Add(p.Rig);
            h.Add(p.Wear);
            h.Add(p.HitMult);
            h.Add((int)p.Limb);
        }
        h.Add(medical.Blood);
        h.Add(medical.Bleeding);
        h.Add(medical.Hunger);
        h.Add(medical.Fed);
        h.Add(medical.KoTimer);
        h.Add(medical.Unconscious);
        h.Add(medical.Dead);
        h.Add((int)medical.Cause);
        h.Add(medical.Pain);
        h.Add(medical.Wounds.Count);
        foreach (float w in medical.Wounds) h.Add(w);
    }
}
