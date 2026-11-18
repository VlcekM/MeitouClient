using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// Adds a character's stats and medical state to the state hash (the body classes themselves know nothing of it).
/// </summary>
static class BodyHash
{
    /// <summary>
    /// Adds the stats (and, while the debug stat boost raises them, the values kept aside) and the medical state to <paramref name="h"/>; a
    /// missing one adds -1.
    /// </summary>
    public static void Add(ref StateHasher h, CharacterStats? stats, MedicalState? medical)
    {
        if (stats is null) h.Add(-1);
        else
        {
            foreach (float v in stats.ToArray()) h.Add(v);

            // The debug stat boost keeps the real values aside; they come back on Unboost, so they are state. Only while boosted, so normal hashes
            // are unchanged.
            if (stats.BoostOriginal is { } original)
            {
                h.Add(0x476F6444);
                foreach (float v in original) h.Add(v);
            }

            // Code mods' stat modifiers, only once one was set, so hashes without them are unchanged.
            if (stats.ModifiersOrNull is { All.Count: > 0 } modifiers)
            {
                h.Add(0x5374614D);
                modifiers.AddTo(ref h);
            }

            // Code mods' skill levels, only once one was set, so hashes without them are unchanged.
            if (stats.ModSkillsOrNull is { Count: > 0 } modSkills)
            {
                h.Add(0x536B6C4D);
                modSkills.AddTo(ref h);
            }

            // Code mods' status effects, only while one is on, so hashes without them are unchanged.
            if (stats.EffectsOrNull is { Count: > 0 } effects)
            {
                h.Add(0x45666654);
                effects.AddTo(ref h);
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