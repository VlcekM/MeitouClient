// Port of upstream/ProvingGrounds/plugin/src/TownAnnouncerPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownAnnouncerPolicy.h (header-only, namespace <c>TownAnnouncerPolicy</c>): the pre-fight announcer of player bouts
/// in town (TW §2.8): which cast speaks (House announcer, Skarn, or Senn and Torka), the fallback when the cast or the Announcer Spot
/// is missing (Skarn / Senn-Torka -&gt; House -&gt; the legacy countdown), the script variants, cue times (one line every 3.25 s,
/// then "FIGHT!"), the speakers' approach-stall tracker and the release rules. All script text is player text, byte for byte.
/// The ids are PgRecords.AnnouncerSpot / ScratchAnnouncer / Skarn / Senn / Torka, written as literals like the C++.</summary>
public static class TownAnnouncerPolicy
{
    /// <summary>C++: TownAnnouncerPolicy.h:7-12. A value type; the C++ default (0, 0) equals <c>default(ApproachProgress)</c>.</summary>
    public struct ApproachProgress
    {
        public float BestDistance;
        public float StalledElapsed;

        // C++: TownAnnouncerPolicy.h:11
        public ApproachProgress() { BestDistance = 0.0f; StalledElapsed = 0.0f; }
    }

    /// <summary>C++: TownAnnouncerPolicy.h:14-21 (returned by value; the C++ has no default constructor).</summary>
    public struct ReleasePlan
    {
        public bool ReleaseOwnership;
        public bool ResumeNative;
        public bool DeferNative;

        // C++: TownAnnouncerPolicy.h:19-20
        public ReleasePlan(bool release, bool resume, bool defer)
        {
            ReleaseOwnership = release; ResumeNative = resume; DeferNative = defer;
        }
    }

    /// <summary>C++: TownAnnouncerPolicy.h:23-28</summary>
    public enum PendingReleaseDisposition
    {
        ForgetPendingRelease = 0,
        WaitPendingRelease = 1,
        ResumePendingRelease = 2
    }

    /// <summary>C++: TownAnnouncerPolicy.h:30-36</summary>
    public enum Program
    {
        LegacyCountdown = 0,
        HouseAnnouncer = 1,
        SkarnAnnouncer = 2,
        SennAndTorkaAnnouncers = 3
    }

    /// <summary><c>a &amp;&amp; b &amp;&amp; strcmp(a, b) == 0</c>: two nulls are not equal. Call it with strings: an argument typed
    /// <c>object</c> binds to <c>object.Equals(object?, object?)</c> instead, which treats two nulls as equal.</summary>
    // C++: TownAnnouncerPolicy.h:38-41
    public static bool Equals(string? a, string? b)
    {
        return a != null && b != null && string.Equals(a, b, StringComparison.Ordinal);
    }

    // C++: TownAnnouncerPolicy.h:43-46
    public static bool IsSpot(string? id)
    {
        return Equals(id, "261-Proving Grounds.mod");
    }

    /// <summary><paramref name="opponentRoles"/> null = a NULL pointer; <paramref name="opponentCount"/> must not exceed its length
    /// (the C++ would read past the array; here it throws IndexOutOfRangeException). Slot 5 (Senn and Torka) is unreachable in the shipped game (TW §5).</summary>
    // C++: TownAnnouncerPolicy.h:48-70
    public static Program Select(bool playerMatch, int challengeSlot,
        string?[]? opponentRoles, int opponentCount)
    {
        if (!playerMatch)
            return Program.LegacyCountdown;
        if (challengeSlot == 5)
        {
            // Some unlocked Skarn rematches also put Senn and/or Torka in the
            // pit. They cannot occupy the viewing stand at the same time.
            for (int i = 0; opponentRoles != null && i < opponentCount; ++i)
                if (Equals(opponentRoles[i], "169-Proving Grounds.mod") ||
                    Equals(opponentRoles[i], "168-Proving Grounds.mod"))
                    return Program.HouseAnnouncer;
            return Program.SennAndTorkaAnnouncers;
        }
        for (int i = 0; opponentRoles != null && i < opponentCount; ++i)
        {
            if (Equals(opponentRoles[i], "169-Proving Grounds.mod") ||
                Equals(opponentRoles[i], "168-Proving Grounds.mod"))
                return Program.SkarnAnnouncer;
        }
        return Program.HouseAnnouncer;
    }

    // C++: TownAnnouncerPolicy.h:72-77
    public static int RequiredSpeakers(Program program)
    {
        if (program == Program.SennAndTorkaAnnouncers)
            return 2;
        return program == Program.HouseAnnouncer || program == Program.SkarnAnnouncer ? 1 : 0;
    }

    /// <summary>The character id of speaker <paramref name="index"/>, or null (NULL).</summary>
    // C++: TownAnnouncerPolicy.h:79-88
    public static string? SpeakerRole(Program program, int index)
    {
        if (index < 0 || index >= RequiredSpeakers(program))
            return null;
        if (program == Program.HouseAnnouncer)
            return "260-Proving Grounds.mod";
        if (program == Program.SkarnAnnouncer)
            return "167-Proving Grounds.mod";
        return index == 0 ? "169-Proving Grounds.mod" : "168-Proving Grounds.mod";
    }

    /// <remarks>The glue's cast-fallback loop (TownAnnouncer.cpp:300-317), which uses this and <see cref="NextFallback"/>; the
    /// glue must keep this order. With no Announcer Spot the loop does not run: the candidate stays the requested program and the
    /// speaker count is 0, so Resolve gives the legacy countdown. With a spot, while the candidate is not LegacyCountdown:
    /// (1) clear the found list; (2) for each speaker index 0..RequiredSpeakers(candidate)-1, look up the character for
    /// SpeakerRole(candidate, i) once and count it if found; (3) if every required speaker was found, stop; (4) otherwise
    /// candidate = NextFallback(candidate) and repeat. Then Resolve(last candidate, spot != null, the LAST attempt's found
    /// count). The reference copy of this loop is AnnouncerGoldenTests.Cast_fallback_chain.</remarks>
    // C++: TownAnnouncerPolicy.h:90-95
    public static Program Resolve(Program requested, bool hasSpot, int speakerCount)
    {
        return hasSpot && speakerCount == RequiredSpeakers(requested)
            ? requested
            : Program.LegacyCountdown;
    }

    /// <remarks>Skarn and Senn-Torka fall back to House, House to the legacy countdown; the loop that uses it is described on
    /// <see cref="Resolve"/> (TownAnnouncer.cpp:300-317).</remarks>
    // C++: TownAnnouncerPolicy.h:97-102
    public static Program NextFallback(Program requested)
    {
        return requested == Program.SkarnAnnouncer || requested == Program.SennAndTorkaAnnouncers
            ? Program.HouseAnnouncer
            : Program.LegacyCountdown;
    }

    // C++: TownAnnouncerPolicy.h:104-113
    public static int VariantCount(Program program)
    {
        if (program == Program.HouseAnnouncer)
            return 8;
        if (program == Program.SkarnAnnouncer)
            return 5;
        if (program == Program.SennAndTorkaAnnouncers)
            return 4;
        return 1;
    }

    // C++: TownAnnouncerPolicy.h:115-118
    public static int Variant(Program program, uint roll)
    {
        return (int)(roll % (uint)VariantCount(program));
    }

    /// <summary>A negative <paramref name="variant"/> is cast to unsigned (-1 = roll 0xffffffff), like the C++.</summary>
    // C++: TownAnnouncerPolicy.h:120-130
    public static int SetupCueCount(Program program, int variant)
    {
        if (program == Program.SennAndTorkaAnnouncers)
            return 4;
        int selected = Variant(program, unchecked((uint)variant));
        if (program == Program.HouseAnnouncer)
            return 2 + selected % 2;
        if (program == Program.SkarnAnnouncer)
            return 3 + selected % 2;
        return 0;
    }

    // C++: TownAnnouncerPolicy.h:132-136
    public static int CueCount(Program program, int variant = 0)
    {
        int setup = SetupCueCount(program, variant);
        return setup > 0 ? setup + 1 : 0;
    }

    /// <summary>0 = the first speaker; Senn and Torka alternate (Torka speaks cues 1 and 3); -1 outside the cues.</summary>
    // C++: TownAnnouncerPolicy.h:138-145
    public static int CueSpeaker(Program program, int cue, int variant = 0)
    {
        if (cue < 0 || cue >= CueCount(program, variant))
            return -1;
        if (program == Program.SennAndTorkaAnnouncers)
            return cue == 1 || cue == 3 ? 1 : 0;
        return 0;
    }

    // C++: TownAnnouncerPolicy.h:147-152
    public static float CueStart(Program program, int cue, int variant = 0)
    {
        return cue >= 0 && cue < CueCount(program, variant)
            ? (float)cue * 3.25f
            : -1.0f;
    }

    // C++: TownAnnouncerPolicy.h:154-160
    public static int CueAt(Program program, int lastCue, float elapsed, int variant = 0)
    {
        int next = lastCue + 1;
        return next < CueCount(program, variant) && elapsed >= CueStart(program, next, variant)
            ? next
            : -1;
    }

    // C++: TownAnnouncerPolicy.h:162-172
    public static int CuesDue(Program program, int lastCue, float elapsed, int variant = 0)
    {
        int count = 0;
        for (int cue = lastCue + 1; cue < CueCount(program, variant); ++cue)
        {
            if (elapsed < CueStart(program, cue, variant))
                break;
            ++count;
        }
        return count;
    }

    // C++: TownAnnouncerPolicy.h:174-178
    public static float Duration(Program program, int variant = 0)
    {
        int count = CueCount(program, variant);
        return count > 0 ? CueStart(program, count - 1, variant) + 0.75f : 0.0f;
    }

    // C++: TownAnnouncerPolicy.h:180-183
    public static float AdvanceApproach(float elapsed, float delta, bool paused)
    {
        return paused ? elapsed : elapsed + delta;
    }

    // C++: TownAnnouncerPolicy.h:185-189
    public static void ResetApproachProgress(ref ApproachProgress progress, float distance)
    {
        progress.BestDistance = distance;
        progress.StalledElapsed = 0.0f;
    }

    /// <summary>Progress = at least 2 units nearer than the best so far; stalled after 20 s without it. Delta is not clamped.</summary>
    // C++: TownAnnouncerPolicy.h:191-208
    public static bool ApproachStalled(ref ApproachProgress progress, float distance,
        float delta, bool paused)
    {
        const float meaningfulProgress = 2.0f;
        const float stallTimeout = 20.0f;
        if (paused)
            return false;
        if (distance <= progress.BestDistance - meaningfulProgress)
        {
            progress.BestDistance = distance;
            progress.StalledElapsed = 0.0f;
        }
        else
        {
            progress.StalledElapsed += delta;
        }
        return progress.StalledElapsed >= stallTimeout;
    }

    // C++: TownAnnouncerPolicy.h:210-214
    public static ReleasePlan PlanRelease(bool owned, bool directed, bool safeToResume)
    {
        return new ReleasePlan(owned, directed && safeToResume,
            directed && !safeToResume);
    }

    // C++: TownAnnouncerPolicy.h:216-222
    public static PendingReleaseDisposition PendingReleaseFor(bool valid, bool dead,
        bool safeToResume)
    {
        if (!valid || dead)
            return PendingReleaseDisposition.ForgetPendingRelease;
        return safeToResume ? PendingReleaseDisposition.ResumePendingRelease : PendingReleaseDisposition.WaitPendingRelease;
    }

    // C++: TownAnnouncerPolicy.h:224-227
    public static float CountdownElapsedAfterFallback(bool announcementLost, float elapsed)
    {
        return announcementLost ? 0.0f : elapsed;
    }

    /// <summary>The House scripts, [variant][line]. Even variants have 2 setup cues, so their third line is never spoken (it is
    /// kept, as shipped).</summary>
    // C++: TownAnnouncerPolicy.h:238-263
    // Do not mutate: shipped player text (internal only so tests can read it).
    internal static readonly string[][] HouseScripts =
    {
        new[] { "Fresh blood for the Rust Crucible! Let Scratch judge what the wastes have spared.",
          "Two sides enter our sand. Only one gets the cheers when this is done.",
          "Keep your hands off your Cats and your eyes on the pit." },
        new[] { "Cats change hands. Teeth hit sand. Scratch, keep your eyes on the pit.",
          "Pick a side now. Regret pays nothing once the first blow lands.",
          "When the dust settles, remember who took your money." },
        new[] { "The gates are shut and the medics are waiting. Let the Crucible settle the rest.",
          "They came here standing. The medics only promised to keep them breathing.",
          "Scratch, make some noise for the brave and the foolish." },
        new[] { "No titles below this rail. No excuses. Only iron and whoever still stands.",
          "Strength talks plainly in this pit. We are about to hear every word.",
          "The Crucible remembers winners. Everyone else gets carried out." },
        new[] { "The wastes made them hard. Scratch gets to find out how hard.",
          "Look well. Confidence weighs nothing once the steel starts moving.",
          "Let the pit strip away every boast they brought through the gate." },
        new[] { "Another drifter wagers bone against steel. The Rust Crucible accepts.",
          "One good strike buys glory. One bad step buys a bed in the hospital.",
          "Scratch has placed its bets. Now earn them." },
        new[] { "Steel is cheap. Blood is cheaper. Show Scratch what you are worth.",
          "No guards will save them and no story will soften the score.",
          "Stand tall now. The sand has room for both of you." },
        new[] { "They walked out of the wastes and into our pit. Let us see which was kinder.",
          "The crowd wants courage. The bookie wants Cats. The Crucible wants proof.",
          "Give Scratch a finish worth lying about tomorrow." }
    };

    /// <summary>Skarn's scripts, [variant][line]. Even variants have 3 setup cues, so their fourth line is never spoken (kept).</summary>
    // C++: TownAnnouncerPolicy.h:268-289
    // Do not mutate: shipped player text (internal only so tests can read it).
    internal static readonly string[][] SkarnScripts =
    {
        new[] { "One of Scratch's own stands below. The challenger came anyway.",
          "Courage brought them through the gate. Courage will not carry them back out.",
          "Watch closely. Scratch protects its name with fists, not promises.",
          "The pit is ready to collect what pride owes." },
        new[] { "That one survived my training. We will see if the challenger survives them.",
          "I broke every soft habit they carried into this town.",
          "The challenger sees one fighter. I see every lesson waiting to hurt them.",
          "Do not disappoint me. Either of you." },
        new[] { "Scratch knows this fighter. The outsider still has time to run.",
          "No? Good. I was tired of watching sensible people.",
          "Our sand does not care where you came from. It only remembers where you fell.",
          "Show this crowd which name deserves to leave standing." },
        new[] { "The pit raised one of these. The wastes dragged in the other.",
          "Both claim the harder teacher. Claims are cheap above the rail.",
          "Below it, every scar becomes an argument.",
          "Settle the lesson where Scratch can see it." },
        new[] { "I taught them to finish fights. Let us see if they listened.",
          "A clean victory earns respect. A slow one earns practice.",
          "The challenger has one chance to make my work look poor.",
          "Make this worth the blood we are about to wash away." }
    };

    // C++: TownAnnouncerPolicy.h:294-303
    // Do not mutate: shipped player text (internal only so tests can read it).
    internal static readonly string[][] SennScripts =
    {
        new[] { "Scratch, look sharp! The old monster has stepped into his own pit.",
          "I give the challenger thirty breaths before Skarn remembers his manners." },
        new[] { "The boss finally came down from the rail. Try to look frightened.",
          "I brought bandages, but not enough for his pride." },
        new[] { "Skarn wants blood on his own stones. Rude not to give him some.",
          "Someone tell the medics to bring the bucket with the strong handle." },
        new[] { "Clear the rail! Scratch's oldest bad decision is fighting again.",
          "If the challenger wins, I am claiming I trained them." }
    };

    // C++: TownAnnouncerPolicy.h:304-313
    // Do not mutate: shipped player text (internal only so tests can read it).
    internal static readonly string[][] TorkaScripts =
    {
        new[] { "Skarn! Leave enough challenger for the medics to identify.",
          "Thirty breaths? I wager ten, and I want change." },
        new[] { "Try not to break this one before I win my bet.",
          "Keep the bandages. I put my Cats on the old man." },
        new[] { "Old man, the crowd paid for a fight, not an execution.",
          "Use the good bucket. This one looks expensive." },
        new[] { "I counted their limbs. Do not make me count again.",
          "Fine. I will claim I trained whoever is still conscious." }
    };

    /// <summary>The line spoken at <paramref name="cue"/>, null (NULL) outside the cues; the last cue is always "FIGHT!".
    /// Note the argument order (program, variant, cue), unlike the other cue functions.</summary>
    // C++: TownAnnouncerPolicy.h:229-321
    public static string? CueText(Program program, int variant, int cue)
    {
        if (cue < 0 || cue >= CueCount(program, variant))
            return null;
        if (cue == CueCount(program, variant) - 1)
            return "FIGHT!";

        if (program == Program.HouseAnnouncer)
        {
            return HouseScripts[Variant(program, unchecked((uint)variant))][cue];
        }
        if (program == Program.SkarnAnnouncer)
        {
            return SkarnScripts[Variant(program, unchecked((uint)variant))][cue];
        }
        if (program == Program.SennAndTorkaAnnouncers)
        {
            int selected = Variant(program, unchecked((uint)variant));
            if (cue == 0) return SennScripts[selected][0];
            if (cue == 1) return TorkaScripts[selected][0];
            if (cue == 2) return SennScripts[selected][1];
            return TorkaScripts[selected][1];
        }
        return null;
    }
}
