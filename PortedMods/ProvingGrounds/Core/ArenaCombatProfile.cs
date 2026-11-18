// Pure parts of upstream/ProvingGrounds/plugin/src/ArenaCombatProfile.{h,cpp} (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: <c>namespace ArenaCombatProfile</c> (ArenaCombatProfile.h:6-17, ArenaCombatProfile.cpp; TW §2.3), the parts that do not
/// read the game. <c>Read(Character*)</c> (ArenaCombatProfile.cpp:62-87) stays in the glue: it picks the weapon (primary, or the
/// secondary when the primary is missing, invalid or unarmed) and reads the stat getters; it builds the profile from those values with
/// <see cref="FromStats"/>, which holds Read's decisions (martial, style, weapon skill, valid).
/// <see cref="Evaluate"/> is the ability formula; its martial branch is the same expression as <see cref="TownMatchmakingPolicy.Ability"/>
/// (TownMatchmakingPolicy.h:56-60), but the C++ writes it out again here with the extra <c>valid</c> / weapon-skill checks, so it is
/// copied, not called (a test checks the two agree).</summary>
public static class ArenaCombatProfile
{
    /// <summary>C++: ArenaCombatProfile.h:7-12 (copied by value). Build with <c>new Profile()</c>, the C++ constructor (ArenaCombatProfile.cpp:59-60:
    /// not valid, not martial, style "", every stat 0); a <c>default</c> holds a null <see cref="Style"/>, which reads as "".
    /// <see cref="Defence"/> holds the dodge for a martial-arts profile and <see cref="Attack"/> the martial-arts attack (Read, ArenaCombatProfile.cpp:78-79).</summary>
    public struct Profile
    {
        public bool Valid, Martial;
        public string? Style;
        public double Attack, Defence, Strength, Toughness, Dexterity, WeaponSkill;

        // C++: ArenaCombatProfile.cpp:59-60
        public Profile()
        {
            Valid = false; Martial = false; Style = "";
            Attack = 0; Defence = 0; Strength = 0; Toughness = 0; Dexterity = 0; WeaponSkill = 0;
        }
    }

    /// <summary>The Kenshi <c>WeaponCategory</c> values the C++ switches on (ArenaCombatProfile.cpp:23-51: SkillFor and StyleFor), in that switch order;
    /// <see cref="Other"/> = any other category (the switches' <c>default</c>). This is the port's own numbering: never cast from the
    /// game's <c>getCategory()</c> value. The glue maps Kenshi's constants onto it in one switch (together with SkillFor), and M2 tests
    /// every constant through that switch.</summary>
    public enum WeaponCategory { Katanas, Sabres, Blunt, Heavy, Hackers, Unarmed, Bow, Turret, Polearms, Other }

    // C++: ArenaCombatProfile.cpp:12-15
    public static bool Finite(double value)
    {
#pragma warning disable CS1718 // value == value is the C++'s NaN test
        return value == value && value <= double.MaxValue && value >= -double.MaxValue;
#pragma warning restore CS1718
    }

    // C++: ArenaCombatProfile.cpp:17-21
    public static bool CoreStatsFinite(in Profile profile)
    {
        return Finite(profile.Attack) && Finite(profile.Defence) &&
            Finite(profile.Strength) && Finite(profile.Toughness) &&
            Finite(profile.Dexterity);
    }

    // C++: ArenaCombatProfile.cpp:38-51
    public static string StyleFor(WeaponCategory category)
    {
        switch (category)
        {
            case WeaponCategory.Katanas: return "Katana";
            case WeaponCategory.Sabres: return "Sabre";
            case WeaponCategory.Blunt: return "Blunt";
            case WeaponCategory.Heavy: return "Heavy weapon";
            case WeaponCategory.Hackers: return "Hacker";
            case WeaponCategory.Unarmed: return "Martial arts";
            case WeaponCategory.Bow: return "Crossbow";
            case WeaponCategory.Turret: return "Turret";
            case WeaponCategory.Polearms: return "Polearm";
            default: return "Weapon";
        }
    }

    /// <summary>Read's martial test (ArenaCombatProfile.cpp:74-76): no usable weapon, or an unarmed one. The glue asks this first, because
    /// the martial flag decides which getters it reads: <c>getMeleeAttack_unarmed(true)</c> / <c>getDodge(true)</c> when martial,
    /// <c>getMeleeAttack_melee()</c> / <c>getMeleeDefence_melee(false)</c> otherwise (:78-79).</summary>
    // C++: ArenaCombatProfile.cpp:74-76
    public static bool IsMartial(bool usableWeapon, WeaponCategory category)
    {
        WeaponCategory c = usableWeapon ? category : WeaponCategory.Unarmed;
        return !usableWeapon || c == WeaponCategory.Unarmed;
    }

    /// <summary>The rest of <c>Read</c> over the values the glue read. <paramref name="weaponSkill"/> = <c>SkillFor(stats, category)</c>
    /// (SkillFor, ArenaCombatProfile.cpp:23-36: the category's own skill, <c>getEquippedWeaponSkill()</c> for <see cref="WeaponCategory.Other"/>);
    /// it is not read for a martial profile (0). Six doubles in a row: call it with named arguments. <c>Read</c> returns <c>new Profile()</c> for a null/invalid character or null stats (:64-66).</summary>
    // C++: ArenaCombatProfile.cpp:74-86 (the getters :78-82 and SkillFor :84 are the glue's parameters)
    public static Profile FromStats(bool usableWeapon, WeaponCategory category, double attack, double defence,
        double strength, double toughness, double dexterity, double weaponSkill)
    {
        Profile profile = new Profile();
        WeaponCategory c = usableWeapon ? category : WeaponCategory.Unarmed;
        profile.Martial = IsMartial(usableWeapon, category);
        // `usableWeapon ? StyleFor(category) : "Weapon"`: the "Weapon" arm is unreachable (not martial means a usable weapon)
        profile.Style = profile.Martial ? "Martial arts" : usableWeapon ? StyleFor(c) : "Weapon";
        profile.Attack = attack;
        profile.Defence = defence;
        profile.Strength = strength;
        profile.Toughness = toughness;
        profile.Dexterity = dexterity;
        profile.WeaponSkill = profile.Martial ? 0 : weaponSkill;
        profile.Valid = CoreStatsFinite(profile) && (profile.Martial || Finite(profile.WeaponSkill));
        return profile;
    }

    /// <summary>The ability score (TW §2.3): martial <c>10 + .30a + .30d + .15s + .15t + .10x</c>, weapon
    /// <c>10 + .25a + .25d + .15s + .15t + .10x + .10w</c>; -1 when unusable, non-finite or not positive.</summary>
    // C++: ArenaCombatProfile.cpp:89-98
    public static double Evaluate(in Profile profile)
    {
        if (!profile.Valid || !CoreStatsFinite(profile) ||
            (!profile.Martial && !Finite(profile.WeaponSkill))) return -1;
        double score = profile.Martial ?
            10 + .30 * profile.Attack + .30 * profile.Defence + .15 * profile.Strength +
                .15 * profile.Toughness + .10 * profile.Dexterity :
            10 + .25 * profile.Attack + .25 * profile.Defence + .15 * profile.Strength +
                .15 * profile.Toughness + .10 * profile.Dexterity + .10 * profile.WeaponSkill;
        return Finite(score) && score > 0 ? score : -1;
    }

    // C++: ArenaCombatProfile.cpp:53-56 (`out << label << ' ' << value`; :104 sets std::fixed << std::setprecision(0) = printf "%.0f")
    static void Append(System.Text.StringBuilder output, string label, double value)
    {
        if (output.Length > 0) output.Append('\n');
        output.Append(label).Append(' ').Append(CMath.FormatF(value, 0));
    }

    /// <summary>The bookie card's stat lines, one per line, numbers <c>%.0f</c> (msvcp100 -> msvcr100: ties half away from zero, see <see cref="CMath.FormatF"/>).</summary>
    // C++: ArenaCombatProfile.cpp:100-116
    public static string StatText(in Profile profile)
    {
        if (!profile.Valid || !CoreStatsFinite(profile) ||
            (!profile.Martial && !Finite(profile.WeaponSkill))) return "Combat stats unavailable";
        var output = new System.Text.StringBuilder();
        Append(output, profile.Martial ? "Martial arts" : "Attack", profile.Attack);
        Append(output, profile.Martial ? "Dodge" : "Defence", profile.Defence);
        Append(output, "Strength", profile.Strength);
        Append(output, "Toughness", profile.Toughness);
        Append(output, "Dexterity", profile.Dexterity);
        if (!profile.Martial)
        {
            string style = profile.Style ?? "";
            string skill = style.Length == 0 || style == "Weapon" ?
                "Weapon skill" : style + " skill";
            Append(output, skill, profile.WeaponSkill);
        }
        return output.ToString();
    }
}
