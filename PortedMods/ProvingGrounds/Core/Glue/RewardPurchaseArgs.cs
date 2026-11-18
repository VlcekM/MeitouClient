namespace ProvingGrounds.Core.Glue;

/// <summary>Which RewardDelivery.h purchase a <c>RewardPurchase</c> command runs (task 5.2b): the four buttons of the original Rewards window
/// (RewardsUI.cpp:489-561: OnClaimClicked -> ClaimNext, OnLicenceClicked -> PurchaseLicence / PurchaseWeaponLicence, OnGeneralClicked ->
/// PurchaseGeneral).</summary>
public enum RewardPurchaseKind
{
    Claim,
    Licence,
    WeaponLicence,
    General,
}

/// <summary>The words of the TEST-ONLY <c>pg reward &lt;id&gt; claim &lt;piece&gt; | licence &lt;set&gt; | weapon-licence | general &lt;item&gt;</c>
/// (after the id) -> a purchase; port only. Names are the Core enum names (ArenaRewards.Piece / ArmourSet / GeneralItem), any case, never a
/// number, never the Count / None sentinels.</summary>
public static class RewardPurchaseArgs
{
    public const string Usage = "usage pg reward c:<slot>.<generation> claim <piece> | licence <T1|T2|T3> | weapon-licence | general <item> | offer <piece> | drop <piece>";

    /// <summary>A purchase from the words; false (with the usage) for anything else.</summary>
    public static bool TryParse(IReadOnlyList<string> words, out RewardPurchaseKind kind, out int item, out string error)
    {
        kind = RewardPurchaseKind.Claim; item = 0; error = Usage;
        if (words.Count == 0) return false;
        switch (words[0].ToLowerInvariant())
        {
            case "claim" when words.Count == 2 && TryName(words[1], ArenaRewards.Piece.Count, out ArenaRewards.Piece piece):
                kind = RewardPurchaseKind.Claim; item = (int)piece; break;
            case "licence" when words.Count == 2 && TryName(words[1], ArenaRewards.ArmourSet.None, out ArenaRewards.ArmourSet set):
                kind = RewardPurchaseKind.Licence; item = (int)set; break;
            case "weapon-licence" when words.Count == 1:
                kind = RewardPurchaseKind.WeaponLicence; item = 0; break;
            case "general" when words.Count == 2 && TryName(words[1], ArenaRewards.GeneralItem.Count, out ArenaRewards.GeneralItem general):
                kind = RewardPurchaseKind.General; item = (int)general; break;
            default:
                return false;
        }
        error = "";
        return true;
    }

    /// <summary>A Core enum by its name (any case), not a number and not <paramref name="sentinel"/>.</summary>
    public static bool TryName<T>(string word, T sentinel, out T value) where T : struct, Enum =>
        Enum.TryParse(word, true, out value) && Enum.IsDefined(value) && !EqualityComparer<T>.Default.Equals(value, sentinel)
        && !word.All(c => char.IsAsciiDigit(c) || c == '-' || c == '+');
}

/// <summary>Who sent a <c>RewardPurchase</c> (task 5.3a quality review): each sender counts its own sequence numbers and reads only its own last
/// result, so one sender's purchase never ends another's wait or shows in another's status line.</summary>
public enum RewardPurchaseSender
{
    /// <summary>The Rewards window's buttons (task 5.3b), counting from 1.</summary>
    Window,
    /// <summary>The TEST-ONLY <c>pg reward</c> (task 5.2b), counting from 1,000,000.</summary>
    TestVerb,
    /// <summary>The TEST-ONLY <c>pg rewards click</c> (task 5.3a): the sim half's own RewardsPurchaseGate, counting from 1.</summary>
    TestGate,
}

/// <summary>The last purchase result of each sender, immutable (a new object per result: the snapshot publishes it as it is).</summary>
public sealed class RewardPurchaseResults<T> where T : class
{
    readonly T?[] last;

    RewardPurchaseResults(T?[] last) => this.last = last;

    public static readonly RewardPurchaseResults<T> Empty = new(new T?[Enum.GetValues<RewardPurchaseSender>().Length]);

    /// <summary>The last result of <paramref name="sender"/>, or null.</summary>
    public T? Of(RewardPurchaseSender sender) => (uint)sender < (uint)last.Length ? last[(int)sender] : null;

    /// <summary>A copy with <paramref name="result"/> as the last of <paramref name="sender"/>.</summary>
    public RewardPurchaseResults<T> With(RewardPurchaseSender sender, T result)
    {
        var copy = (T?[])last.Clone();
        copy[(int)sender] = result;
        return new RewardPurchaseResults<T>(copy);
    }
}
