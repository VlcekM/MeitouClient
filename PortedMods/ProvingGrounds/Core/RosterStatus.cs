// Pure part of upstream/ProvingGrounds/plugin/src/RosterStatus.{h,cpp} (Proving Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>C++: <c>namespace RosterStatus</c> (RosterStatus.h:8-19, RosterStatus.cpp:7-49): a character's lowest limb, blood and
/// recovery band. The C++ reads <c>Character*</c> / <c>MedicalSystem*</c> itself; here the glue passes what it read (see
/// <see cref="Read"/>). The band rules are <see cref="RosterStatusPolicy"/> (task 1.5), called, not re-ported. A separate class from
/// <c>RosterStatusPolicy</c> because the C++ keeps them in two namespaces.</summary>
public static class RosterStatus
{
    /// <summary>C++: RosterStatus.h:10-16 (copied by value). <see cref="Read"/> always fills every field, also when it returns false.</summary>
    public struct Snapshot
    {
        public float LowestLimb;
        public float Blood;
        public float Recovery;
        public RosterStatusPolicy.Band Band;
    }

    /// <summary>One <c>medical-&gt;anatomy[i]</c>: <c>flesh</c>, <c>fleshStun</c> and <c>maxHealth()</c>. A null part pointer is a
    /// <c>null</c> entry in the list.</summary>
    public struct Part
    {
        public float Flesh, FleshStun, MaxHealth;

        public Part(float flesh, float fleshStun, float maxHealth) { Flesh = flesh; FleshStun = fleshStun; MaxHealth = maxHealth; }
    }

    /// <summary><paramref name="characterValid"/> = <c>character &amp;&amp; character-&gt;isValid()</c>; <paramref name="hasMedical"/> =
    /// <c>getMedical() != NULL</c>; <paramref name="maxBlood"/> = <c>getMaxBlood()</c> and <paramref name="blood"/> = <c>blood</c> (read
    /// only when there is an anatomy); <paramref name="anatomy"/> the parts in order (null or empty = <c>anatomy.size() == 0</c>).
    /// <paramref name="error"/> is what the C++ writes through its optional <c>std::string*</c> ("" on success).</summary>
    // C++: RosterStatus.cpp:9-48
    public static bool Read(bool characterValid, bool hasMedical, float maxBlood, float blood, IReadOnlyList<Part?>? anatomy,
        out Snapshot snapshot, out string error)
    {
        snapshot.LowestLimb = 0.0f;
        snapshot.Blood = 0.0f;
        snapshot.Recovery = 0.0f;
        snapshot.Band = RosterStatusPolicy.Band.Red;
        error = "health data unavailable";
        if (!characterValid) return false;

        int size = anatomy?.Count ?? 0;
        if (!hasMedical || size == 0)
        {
            error = !hasMedical ? "medical system unavailable" : "anatomy is empty";
            return false;
        }

        snapshot.Blood = maxBlood > 0.0f ? blood / maxBlood : 0.0f;
        snapshot.LowestLimb = 1.0f;
        for (uint i = 0; i < (uint)size; ++i)
        {
            Part? part = anatomy![(int)i];
            if (part is not Part p || !(p.MaxHealth > 0.0f))
            {
                // sprintf_s "anatomy part %u/%u has no valid maximum health"
                error = "anatomy part " + i.ToString(CultureInfo.InvariantCulture) + "/" +
                    ((uint)size).ToString(CultureInfo.InvariantCulture) + " has no valid maximum health";
                return false;
            }
            float ratio = (p.Flesh - p.FleshStun) / p.MaxHealth;
            snapshot.LowestLimb = RosterStatusPolicy.LowestLimb(snapshot.LowestLimb, ratio);
        }
        snapshot.Recovery = RosterStatusPolicy.Recovery(snapshot.LowestLimb, snapshot.Blood);
        snapshot.Band = RosterStatusPolicy.BandFor(snapshot.Recovery);
        error = "";
        return true;
    }
}
