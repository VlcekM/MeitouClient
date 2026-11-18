using Meitou.Simulation;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Town;

// Port of upstream/ProvingGrounds/plugin/src/TownChallengeWallet.h/.cpp (Proving Grounds by Shutori, GPLv3), task 7.5 (TW §2.5, CAP 10).
// E1 (every engine call of the C++ and its replacement):
//   ou->player->getFaction()->factionOwnerships (Wallet, :11-15) ... world.Player (PlayerState, Meitou src/Meitou.Simulation/World.cs:343: always
//       present, so the C++'s "no player faction" -1 / false cannot happen)
//   Ownerships::getMoney() ... PlayerState.Money (src/Meitou.Simulation/Player/PlayerState.cs:39, a long; read as the C++ int, clamped to the int range by Core/Glue/TownChallengeWalletRule.cs:
//       docs/divergences.md "TownArena challenge cards")
//   Ownerships::takeMoney(amount) / addMoney(amount) ... PlayerState.Money -= / += amount (a plain setter; no other path moves the player's cats)
// World writes only in serial steps (the town tick, the booking command).

/// <summary>C++ namespace <c>TownChallengeWallet</c>: the player's money, taken for a buy-in or a paid refresh and credited with a challenge's
/// return.</summary>
static class TownChallengeWallet
{
    // C++: TownChallengeWallet.cpp:17-20
    internal static int Balance(World world) => TownChallengeWalletRule.Balance(world.Player.Money);

    // C++: TownChallengeWallet.cpp:21-24
    internal static bool Take(World world, int amount)
    {
        if (!TownChallengeWalletRule.CanTake(world.Player.Money, amount)) return false;
        world.Player.Money -= amount;
        return true;
    }

    // C++: TownChallengeWallet.cpp:25-30
    internal static bool Credit(World world, int amount)
    {
        if (!TownChallengeWalletRule.CanCredit(world.Player.Money, amount)) return false;
        world.Player.Money += amount;
        return true;
    }
}
