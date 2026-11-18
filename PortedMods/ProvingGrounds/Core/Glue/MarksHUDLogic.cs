using System.Globalization;

namespace ProvingGrounds.Core.Glue;

// Port of the decisions and arithmetic of upstream/ProvingGrounds/plugin/src/MarksHUD.cpp (Proving Grounds by Shutori, GPLv3), task 4.6b
// (PA §2.23; CAP 7.1, 7.9). Pure: the 250 ms cadence and the failure latch of Tick (:87-98), what the sim half decides in Refresh (:42-45: the
// save, the selected player character in Scratch; the money label's visibility is the window side's), the caption (:61-63) and the panel's
// rectangle above the money display (:64-80). The glue is src/Game/MarksHUDSim.cs (sim half) and src/Ui/MarksHUD.cs (the overlay label).

/// <summary>The texts of the Marks HUD.</summary>
public static class MarksHudTexts
{
    /// <summary>C++ MarksHUD.cpp:62 <c>sprintf_s(amount, "Marks: %d", ...)</c>.</summary>
    public static string Marks(int marks) => "Marks: " + marks.ToString(CultureInfo.InvariantCulture);

    /// <summary>C++ MarksHUD.cpp:96 the failure line (+ the exception's text).</summary>
    public const string Unavailable = "Proving Grounds: Marks HUD unavailable: ";
}

/// <summary>C++ MarksHUD.cpp:23-28 and :87-104: <c>lastRefresh</c> (0 = never) and <c>failed</c>; <see cref="Due"/> is Tick's gate (a refresh at
/// most every 250 ms of the clock, none after a failure), <see cref="Reset"/> AbandonWorldState's part (:103).</summary>
public sealed class MarksHudClock
{
    /// <summary>C++ :91 the 250 ms between refreshes.</summary>
    public const uint IntervalMs = 250;

    uint lastRefresh;
    bool failed;

    public bool Failed => failed;

    /// <summary>C++ :89-92: false after a failure, or while less than 250 ms passed since the last refresh (unsigned difference, as DWORD
    /// arithmetic wraps); else remembers <paramref name="nowMs"/> and answers true.</summary>
    public bool Due(uint nowMs)
    {
        if (failed) return false;
        if (lastRefresh != 0 && unchecked(nowMs - lastRefresh) < IntervalMs) return false;
        lastRefresh = nowMs;
        return true;
    }

    /// <summary>C++ :95 <c>failed = true</c> (after Hide).</summary>
    public void Fail() => failed = true;

    /// <summary>C++ :103 <c>lastRefresh = 0; failed = false</c>.</summary>
    public void Reset()
    {
        lastRefresh = 0;
        failed = false;
    }
}

/// <summary>What one refresh decided: whether the label shows (before the money label's visibility, which the window side adds) and its caption.</summary>
public readonly record struct MarksHudDecision(bool Show, string Text);

/// <summary>The Marks HUD's rules (task 4.6b). Pure.</summary>
public static class MarksHudLogic
{
    /// <summary>
    /// C++ MarksHUD.cpp:42-45 and :61-63, the sim half of Refresh: hidden without an active save (<c>!LeaderboardStore::HasActiveSave()</c>; the
    /// C++'s <c>!ou</c>, <c>!gui</c>, <c>!gui->mainbar</c> are the window side's) or when the selected character is not a valid player character in
    /// Scratch (InScratch :32-38); else shown with "Marks: N" (<paramref name="marks"/> is asked only then, as GetMarks is). A hidden refresh keeps
    /// the caption the label had (<paramref name="previous"/>): the C++ sets it only when it shows.
    /// </summary>
    public static MarksHudDecision Decide(bool hasActiveSave, bool selectedPlayerInScratch, Func<int> marks, string previous)
    {
        if (!hasActiveSave || !selectedPlayerInScratch) return new MarksHudDecision(false, previous);
        return new MarksHudDecision(true, MarksHudTexts.Marks(marks()));
    }

    /// <summary>C++ MarksHUD.cpp:32-38 InScratch: a character that exists (<c>isValid</c>), is the player's (<c>isPlayerCharacter</c>) and whose
    /// current town is town 90 of the data mod ("90-Proving Grounds.mod").</summary>
    public static bool InScratch(bool present, bool valid, bool playerCharacter, bool currentTownIsScratch) =>
        present && valid && playerCharacter && currentTownIsScratch;

    /// <summary>
    /// C++ MarksHUD.cpp:64-80: the panel just above the money display. <paramref name="money"/> = the money amount's absolute rectangle
    /// (<c>moneyText</c>), <paramref name="moneyLabel"/> = the "Money" label's when it is visible (null otherwise); the panel spans both (left = the
    /// smaller left, top = the smaller top, right = the larger right), is at least the caption's width plus two spacings wide (and at most the screen),
    /// one row high (NativeUI::RowHeight of the label), kept on the screen horizontally and placed one row and one spacing above the money's top
    /// (never above 0). The label sits one spacing inside the panel. <paramref name="textWidth"/> / <paramref name="textHeight"/> = the label's
    /// <c>getTextSize()</c>, <paramref name="fontHeight"/> its <c>getFontHeight()</c>.
    /// </summary>
    public static (LayoutRect Panel, LayoutRect Label) Place(LayoutRect money, LayoutRect? moneyLabel, int screenWidth, int textWidth, int textHeight,
        int fontHeight)
    {
        int gap = NativeUI.Spacing(fontHeight);
        int left = money.X;
        int top = money.Y;
        int right = left + money.W;
        if (moneyLabel is { } ml)
        {
            left = Math.Min(left, ml.X);
            top = Math.Min(top, ml.Y);
            right = Math.Max(right, ml.X + ml.W);
        }
        int width = Math.Min(screenWidth, Math.Max(right - left, textWidth + 2 * gap));
        int height = NativeUI.RowHeight(textHeight, fontHeight, 0);
        left = Math.Max(0, Math.Min(left, screenWidth - width));
        top = Math.Max(0, top - height - gap);
        return (new LayoutRect(left, top, width, height), new LayoutRect(gap, gap, Math.Max(1, width - 2 * gap), height - 2 * gap));
    }
}
