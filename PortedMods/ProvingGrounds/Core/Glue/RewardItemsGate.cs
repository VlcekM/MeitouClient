namespace ProvingGrounds.Core.Glue;

/// <summary>When the reward-item adapter (src/Engine/Items.cs) may act (task 5.2a review). Port only: the C++ has no such part.
/// <para>Two gates, on purpose. A by-name member that did not resolve (the fighter refresh, Inventory.Add) leaves nothing to call, so every move
/// is refused. A refresh that THREW once (the inventory changes it followed stand) turns the rewards part off for new purchases only: the moves
/// of a purchase already under way (Give of the new item, Give back of the previous grade on a rollback, TakeOut) must still run, or a previous
/// grade already taken out could not be put back and would be lost. The glue (task 5.2b) asks <see cref="MayStart"/> before it reserves or takes
/// anything out; the adapter's moves ask only <see cref="MayMove"/>.</para></summary>
public static class RewardItemsGate
{
    /// <summary>A new purchase (or test delivery) may start: every member resolved and the refresh never threw.</summary>
    public static bool MayStart(bool membersMissing, bool refreshFailed) => !membersMissing && !refreshFailed;

    /// <summary>An item move (Give, TakeOut, Destroy's take-out, Wear) may run: every member resolved. A failed refresh does not stop it.</summary>
    public static bool MayMove(bool membersMissing) => !membersMissing;
}
