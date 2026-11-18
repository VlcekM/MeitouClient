// Port of upstream/ProvingGrounds/plugin/src/MedicSupplyPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>Restocking a town Scratch medic's kit after use. C++: MedicSupplyPolicy.h (PA §2.11).</summary>
public static class MedicSupplyPolicy
{
    /// <summary>C++: MedicSupplyPolicy.h:4</summary>
    public enum Supply { FirstAid, RobotRepair }

    /// <summary>C++: MedicSupplyPolicy.h:5</summary>
    public enum RestockResult { NoRestock, ReplacementSpawned, UsedKitRecharged }

    /// <summary>The members the C++ template parameter <c>Access</c> must have (MedicSupplyPolicy.h:17-29); the glue implements it
    /// over the medic's inventories.</summary>
    public interface IAccess
    {
        bool IsScratchMedic();
        bool MainInventoryHasUsableKit(Supply supply);
        bool BackpackHasUsableKit(Supply supply);
        bool GiveReplacement(string itemId);
        void RechargeUsedKit();
    }

    // C++: MedicSupplyPolicy.h:7-11
    public static bool ShouldRestock(bool scratchMedic, float chargesBefore,
        float chargesAfter, bool anotherUsableKit)
    {
        return scratchMedic && chargesBefore > 0.0f && chargesAfter <= 0.0f &&
            !anotherUsableKit;
    }

    // C++: MedicSupplyPolicy.h:13-15
    public static string ReplacementId(Supply supply)
    {
        return supply == Supply.RobotRepair ? "18020-gamedata.base" : "1359-gamedata.base";
    }

    // C++: MedicSupplyPolicy.h:17-29 (template<class Access> ... Access& access; generic + ref so a struct access is not copied and
    // its changes are seen by the caller, like the C++ reference. The call site needs a local (or field) to pass by ref.)
    public static RestockResult RestockAfterUse<TAccess>(float chargesBefore, float chargesAfter,
        Supply supply, ref TAccess access) where TAccess : IAccess
    {
        if (!ShouldRestock(access.IsScratchMedic(), chargesBefore, chargesAfter, false))
            return RestockResult.NoRestock;
        if (access.MainInventoryHasUsableKit(supply) ||
                access.BackpackHasUsableKit(supply))
            return RestockResult.NoRestock;
        if (access.GiveReplacement(ReplacementId(supply)))
            return RestockResult.ReplacementSpawned;
        access.RechargeUsedKit();
        return RestockResult.UsedKitRecharged;
    }
}
