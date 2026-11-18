namespace ProvingGrounds.Core.Glue;

// Task 6.4b (moved out of PrisonerRecruitmentUILayout.cs in its quality review): the recruitment window's numbering of its RecruitPrisoner commands,
// the window side of task 6.4a's sequence rule (Core/Glue/PrisonerRecruitmentUILogic.cs OnRecruitCommand drops a request at or below LastSeq).
// No C++ counterpart: OnRecruitClicked (PrisonerRecruitmentUI.cpp:465-473) recruited inside the click. Task 7.6: shared with the town registry window
// (src/Ui/TownArenaUI.cs: its row releases through Next, its booking / refresh clicks through Send; the sim's rule is task 7.5's
// TownArenaUILogic.Accept), and both windows start over when the published RequestSerial changes. Tested in Tests/Glue/RecruitmentRequestGateTests.cs.

/// <summary>A window's numbering of its requests to the sim (the recruitment window, task 6.4b; the town registry window, task 7.6). The sim drops a
/// request whose sequence is at or below the model's LastSeq, so every number is above both the gate's own last number and the published LastSeq.
/// <see cref="Send"/> (an action: grant freedom, book, refresh) is not sent while the last one has not been answered (the published LastSeq is below
/// it); <see cref="Next"/> (a row release: the C++ toggles once per physical release) is always sent. A new world, or a new published RequestSerial
/// (each Show / Close of the window: an answer lost with the window recovers by reopening it), starts over. Window thread only.</summary>
public sealed class RecruitmentRequestGate
{
    object? world;
    long serial = long.MinValue;
    long last;      // the last sequence this gate sent (0: none since it started over)
    long waiting;   // the last Send's sequence (0: none)

    /// <summary>Starts over when <paramref name="current"/> (the publishing system: one per world) is not the one seen before.</summary>
    public void FollowWorld(object? current)
    {
        if (ReferenceEquals(current, world)) return;
        world = current;
        last = waiting = 0;
        serial = long.MinValue;
    }

    /// <summary>Starts over when the published model's RequestSerial (bumped by each Show / Close) is not the one seen before.</summary>
    public void FollowSerial(long requestSerial)
    {
        if (requestSerial == serial) return;
        serial = requestSerial;
        last = waiting = 0;
    }

    /// <summary>Whether the last <see cref="Send"/> is still unanswered (the model has not recorded it, nor a later one).</summary>
    public bool Waiting(long publishedLastSeq) => waiting != 0 && publishedLastSeq < waiting;

    /// <summary>A number above the gate's last and the published LastSeq, never held back (a row release).</summary>
    public long Next(long publishedLastSeq)
    {
        last = Math.Max(last, publishedLastSeq) + 1;
        return last;
    }

    /// <summary>The next action request's sequence (above the gate's last and the published LastSeq), or 0 while the last one waits.</summary>
    public long Send(long publishedLastSeq)
    {
        if (Waiting(publishedLastSeq)) return 0;
        waiting = Next(publishedLastSeq);
        return waiting;
    }
}
