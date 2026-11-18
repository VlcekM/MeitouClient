namespace ProvingGrounds.Core.Glue;

// Task 7.5 (quality review): the rules of upstream/ProvingGrounds/plugin/src/TownChallengeWallet.cpp (Proving Grounds by Shutori, GPLv3) over
// Meitou's money, a long (PlayerState.Money), as the C++ int Ownerships::getMoney() reads it. src/Town/TownChallengeWallet.cs applies them;
// tested in Tests/Glue/TownChallengeWalletRuleTests.cs.

/// <summary>The wallet's arithmetic over a long purse read as the C++ int.</summary>
public static class TownChallengeWalletRule
{
    /// <summary>C++ <c>getMoney()</c> (an int): the long money clamped to the int range on both sides.</summary>
    public static int Balance(long money) => money > int.MaxValue ? int.MaxValue : money < int.MinValue ? int.MinValue : (int)money;

    // C++: TownChallengeWallet.cpp:21-24 (the take succeeds when the money covers it)
    public static bool CanTake(long money, int amount) => amount > 0 && Balance(money) >= amount;

    // C++: TownChallengeWallet.cpp:25-30
    public static bool CanCredit(long money, int amount)
    {
        int balance = Balance(money);
        return amount > 0 && balance >= 0 && balance <= int.MaxValue - amount;
    }
}
