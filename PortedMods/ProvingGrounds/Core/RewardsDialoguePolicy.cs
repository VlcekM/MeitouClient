// Port of upstream/ProvingGrounds/plugin/src/RewardsDialoguePolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>When Kenshi opens a money-trade window between a player character and the NPC 166 ("Null"), the trade is cancelled and
/// the Rewards window opens once the dialogue closes (PA §2.17). Header-only in the C++ (RewardsDialoguePolicy.h:1-52).</summary>
public static class RewardsDialoguePolicy
{
    // C++: RewardsDialoguePolicy.h:7-8
    public const string NullStringId = PgRecords.Null;

    /// <summary>C++: RewardsDialoguePolicy.h:10-15</summary>
    public enum TradeRoute
    {
        PassThrough = 0,
        OpenForFirst = 1,
        OpenForSecond = 2
    }

    /// <summary>C++: RewardsDialoguePolicy.h:17-22</summary>
    public enum PendingDecision
    {
        WaitForDialogue = 0,
        DiscardPending = 1,
        OpenPending = 2
    }

    /// <summary><c>stringId &amp;&amp; strcmp(stringId, kNullStringId) == 0</c>: a null id is never Null; a NUL ends the string as in C.</summary>
    // C++: RewardsDialoguePolicy.h:24-28
    public static bool IsNullId(string? stringId)
    {
        if (stringId == null)
            return false;
        int nul = stringId.IndexOf('\0');
        string c = nul < 0 ? stringId : stringId.Substring(0, nul);
        return string.CompareOrdinal(c, NullStringId) == 0;
    }

    // C++: RewardsDialoguePolicy.h:30-42
    public static TradeRoute ResolveTrade(
        string? firstStringId,
        string? secondStringId,
        bool moneyTrade)
    {
        if (!moneyTrade)
            return TradeRoute.PassThrough;
        bool firstIsNull = IsNullId(firstStringId);
        bool secondIsNull = IsNullId(secondStringId);
        if (firstIsNull == secondIsNull)
            return TradeRoute.PassThrough;
        return firstIsNull ? TradeRoute.OpenForSecond : TradeRoute.OpenForFirst;
    }

    // C++: RewardsDialoguePolicy.h:44-51
    public static PendingDecision ResolvePending(
        bool dialogueActive,
        bool participantValid)
    {
        if (!participantValid)
            return PendingDecision.DiscardPending;
        return dialogueActive ? PendingDecision.WaitForDialogue : PendingDecision.OpenPending;
    }
}
