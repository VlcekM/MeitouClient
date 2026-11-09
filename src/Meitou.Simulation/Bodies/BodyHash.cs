using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// Adds a character's stats and medical state to the state hash (the body classes themselves know nothing of it).
/// </summary>
static class BodyHash
{
    /// <summary>
    /// Adds the stats (and, while god mode boosts them, the values kept aside) and the medical state to <paramref name="h"/>; a missing one adds -1.
    /// </summary>
    public static void Add(ref StateHasher h, CharacterStats? stats, MedicalState? medical)
    {
        if (stats is null) h.Add(-1);
        else
        {
            foreach (float v in stats.ToArray()) h.Add(v);

            // Debug god mode keeps the real values aside; they come back on Unboost, so they are state. Only while boosted, so normal hashes are
            // unchanged.
            if (stats.GodOriginal is { } original)
            {
                h.Add(0x476F6444);
                foreach (float v in original) h.Add(v);
            }
        }
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
        h.Add(medical.Light);
        h.Add(medical.LegKoPoint);
        if (medical.StayingLow) h.Add(0x4C6F77);
        if (medical.PlayingDead) h.Add(0x446561);
        if (medical.WokenLow is { } low) h.Add(low ? 0x576B31 : 0x576B30);
        h.Add(medical.Wounds.Count);
        foreach (float w in medical.Wounds) h.Add(w);
    }
}