// Port of upstream/ProvingGrounds/plugin/src/PrisonerRecruitmentPresentation.h and PrisonerRecruitmentPresentation.cpp (Proving
// Grounds by Shutori, GPLv3).
using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>The freedom button's caption, state and explanation in the recruitment window.
/// C++: PrisonerRecruitmentPresentation.h, PrisonerRecruitmentPresentation.cpp (PA §2.19).</summary>
public static class PrisonerRecruitmentPresentation
{
    /// <summary>C++: PrisonerRecruitmentPresentation.h:7-18. The C++ constructor (.cpp:19-28) sets every field false / 0, which is
    /// the C# struct default.</summary>
    public struct Input
    {
        public bool HasSelection;
        public bool LivingRosterPrisoner;
        public bool Busy;
        public bool Unconscious;
        public bool PlayerReady;
        public int CurrentMarks;
        public int RequiredMarks;
    }

    /// <summary>C++: PrisonerRecruitmentPresentation.h:20-25. The strings are nullable because a <c>default</c> State holds null where
    /// a default-constructed C++ State holds empty strings; <see cref="Evaluate"/> always sets both.</summary>
    public struct State
    {
        public bool Enabled;
        public string? ActionCaption;
        public string? Explanation;
    }

    // C++: PrisonerRecruitmentPresentation.cpp:9-16
    static State Blocked(string caption, string explanation)
    {
        State state;
        state.Enabled = false;
        state.ActionCaption = caption;
        state.Explanation = explanation;
        return state;
    }

    // C++: PrisonerRecruitmentPresentation.cpp:30-65
    public static State Evaluate(in Input input)
    {
        if (!input.HasSelection)
            return Blocked("No Prisoner Selected",
                "No nearby living prisoners are available.");
        if (!input.LivingRosterPrisoner)
            return Blocked("Prisoner Unavailable",
                "This fighter is not an available arena prisoner.");
        if (input.Busy)
            return Blocked("Arena Operation Active",
                "Finish the current arena operation before recruiting.");
        if (input.Unconscious)
            return Blocked("Prisoner Unconscious",
                "The prisoner must be conscious to accept freedom.");
        if (!input.PlayerReady)
            return Blocked("Player Faction Unavailable",
                "The player faction is not ready.");
        if (input.CurrentMarks < input.RequiredMarks)
        {
            // port note: requiredMarks - currentMarks wraps like the C++ int (the C++ is UB on overflow; MSVC wraps)
            int deficit = unchecked(input.RequiredMarks - input.CurrentMarks);
            string message = string.Create(CultureInfo.InvariantCulture, $"This prisoner needs {deficit} more Arena Mark");
            if (deficit != 1)
                message += "s";
            message += ".";
            return Blocked("More Marks Required", message);
        }

        State state;
        state.Enabled = true;
        state.ActionCaption = string.Create(CultureInfo.InvariantCulture, $"Grant Freedom - {input.RequiredMarks} Marks");
        state.Explanation = "This prisoner can earn freedom now.";
        return state;
    }
}
