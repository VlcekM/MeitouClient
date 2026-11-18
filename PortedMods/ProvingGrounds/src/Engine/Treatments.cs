using Meitou.Simulation;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;

namespace ProvingGrounds.Engine;

// The treatment-tick adapter (task 8.4c, CAP 12; m8-research.md "RecordTreatment's points", "Kits: the restock CAN fire"): what the C++'s three
// treatment hooks read around MedicalSystem::applyFirstAid / applyDoctoring (ArenaMedical.cpp:167-199) from Meitou's one first-aid step,
// FirstAidSystem.Apply(World, in Effect) (src/Meitou.Simulation/FirstAid/FirstAidSystem.cs:524-604, serial commit step). Meitou 0.17.3 (tag
// v0.17.3, fa34a1b); PUBLIC Meitou API only (nothing in Members.cs); PgCheck engine JIT-binds this class. No allocation on a treatment tick.
//
// The C++ hook arguments and their Meitou counterparts (E1):
//   who (the medic), thisptr->me (the patient) ... effect.Source / effect.Target, a treatment only while the medic's task is Treat on that
//       patient and not a splint (Apply :528-531, :540-544; applyRigging records nothing in the C++)
//   applyFirstAid vs applyDoctoring (robot repair) ... Apply's own choice of the kit: the robot kit for a robot's open wound, else for the robotic
//       parts once the organic ones need no bandage (:556, :571, :621)
//   frameTime ... world.StepSeconds(medic) (Apply :546, World.cs:491)
//   equipment, equipment->chargesLeft before and after ... CharacterCold.TreatKit (CharacterTable.cs:739): the kit the running treatment took,
//       kept across ticks until used up (KitOf :787-793); a kit taken this tick had charges above 0 (Fewest :735-750)
//   the original's "true" (complete) ... the treatment ended this tick (the medic's task no longer Treat on the patient) with no open wound and
//       nothing left to bandage, the end Apply calls after HealingOtherFinished (:570-576; BandageRoom, MedicalState.Tick.cs:83)
//   Inventory::getAllItemsWithFunction(ITEM_FIRSTAID | ITEM_ROBOTREPAIR) + chargesLeft, ContainerItem (hasABackpackOn) ... the medic's
//       Inventory.Items / Inventory.Backpack.Contents.Items, ItemFunction (1 first aid, 12 robot kit: ItemInfo.cs:105, 114), Charges
//   ou->theFactory->createItem(getData(id)) + medic->giveItem(item, false, true) ... Items.CreateRewardItem + Items.Give (task 5.2a: the
//       reflected Inventory.Add; 0.20 changed its signature)
//   usedKit->resetCharges(false) ... ItemInstance.Charges = MaxCharges (Meitou has already taken a used-up kit out, FirstAidSystem.cs:700)

/// <summary>The engine reads of one treatment tick, taken before FirstAidSystem.Apply and read again after it (ArenaMedical's prefix and
/// postfix). A value type: Harmony's <c>__state</c>, no allocation.</summary>
struct TreatmentTick
{
    /// <summary>A first-aid or robot-repair tick of a medic on its patient (false: nothing to record).</summary>
    internal bool Treat;
    /// <summary>The C++ applyDoctoring (robot repair) rather than applyFirstAid.</summary>
    internal bool Robot;
    /// <summary>ArenaMedical's block skipped the original.</summary>
    internal bool Blocked;
    /// <summary>The kit the treatment held before the tick (null: none yet) and its charges then.</summary>
    internal ItemInstance? Kit;
    internal float Charges;
    /// <summary>The time step (the C++ frameTime).</summary>
    internal float Dt;
}

static class Treatments
{
    const float Done = 1e-3f;   // FirstAidSystem.Done (FirstAidSystem.cs:55)

    /// <summary>The prefix's read: whether this <c>EffectTreat</c> is a first-aid / robot-repair tick of the medic in <paramref name="m"/> on the
    /// patient in <paramref name="p"/>, which kind, the kit and its charges, the step. Serial commit step.</summary>
    internal static TreatmentTick Read(World world, int m, int p, bool blocked)
    {
        var table = world.Characters;
        var t = default(TreatmentTick);
        if (m < 0 || p < 0 || m >= table.HighWater || p >= table.HighWater) return t;
        if (!table.Next[m].Alive || table.Next[m].Task != (byte)CharacterTask.Treat || table.Cold(m) is not { } medic || medic.TreatSplint) return t;
        if (!table.TryResolveNext(medic.TreatTarget, out int target) || target != p) return t;
        if (table.Cold(p) is not { Medical: { Dead: false } body, Race: { } race }) return t;
        t.Treat = true;
        t.Blocked = blocked;
        t.Robot = body.Wounds.Count > 0 ? race.IsRobot : body.BandageRoom(race, false) <= Done;
        int function = t.Robot ? ItemInfo.RobotKitFunction : ItemInfo.FirstAidKitFunction;
        if (medic.TreatKit is { } kit && kit.Item.ItemFunction == function) { t.Kit = kit.Item; t.Charges = kit.Item.Charges; }
        t.Dt = world.StepSeconds(m);
        return t;
    }

    /// <summary>The postfix's read of the C++ "complete" (header). Serial commit step.</summary>
    internal static bool Complete(World world, int m, int p, in TreatmentTick t)
    {
        if (!t.Treat || t.Blocked) return false;
        var table = world.Characters;
        bool ended = table.Next[m].Task != (byte)CharacterTask.Treat || table.Cold(m) is not { } medic ||
            !table.TryResolveNext(medic.TreatTarget, out int target) || target != p;
        return ended && table.Cold(p) is { Medical: { } body, Race: { } race } && body.Wounds.Count == 0 &&
            body.BandageRoom(race, false) + body.BandageRoom(race, true) <= Done;
    }

    /// <summary>The kit the tick used, read after Apply (the C++ <c>equipment</c>): the medic's TreatKit when it is a first-aid or robot kit, with
    /// its charges before (the prefix's read when it is the same item; a kit taken this tick had charges above 0, so the restock rule, which
    /// asks only "above 0 before", reads <see cref="float.Epsilon"/>) and after. False: no kit (no restock).</summary>
    internal static bool UsedKit(World world, int m, in TreatmentTick t, out ItemInstance? kit, out float before, out float after, out bool robotKit)
    {
        kit = null; before = after = 0; robotKit = false;
        if (!t.Treat || t.Blocked || world.Characters.Cold(m)?.TreatKit is not { } k) return false;
        int f = k.Item.ItemFunction;
        if (f != ItemInfo.FirstAidKitFunction && f != ItemInfo.RobotKitFunction) return false;
        kit = k.Item;
        before = ProvingGrounds.Core.Glue.ArenaMedicalLogic.KitChargesBefore(ReferenceEquals(k.Item, t.Kit), t.Charges);
        after = k.Item.Charges;
        robotKit = f == ItemInfo.RobotKitFunction;
        return true;
    }

    /// <summary>The medic's id (C++ <c>who</c>) and the patient's (<c>thisptr-&gt;me</c>).</summary>
    internal static CharacterId IdOf(World world, int slot) => world.Characters.IdOf(slot);

    /// <summary>The C++ <c>medic-&gt;getName()</c> for the restock line ("invalid" for a dead slot).</summary>
    internal static string NameOf(World world, int slot) =>
        world.Characters.Next[slot].Alive ? PgEngine.Name(world, world.Characters.IdOf(slot)) ?? "" : "invalid";

    /// <summary>C++ ArenaMedical.cpp:77-128 <c>MedicSupplyAccess</c> over the medic in <paramref name="slot"/> (a struct: passed by ref to
    /// Core MedicSupplyPolicy.RestockAfterUse, no allocation until a replacement is made). <paramref name="scratchMedic"/>: the caller's
    /// IsScratchMedicActor (TownCombatRules.IsScratchMedic, task 7.4).</summary>
    internal struct KitAccess(World world, PopulationData data, int slot, ItemInstance usedKit, bool scratchMedic)
        : MedicSupplyPolicy.IAccess
    {
        /// <summary>The replacement was placed in the medic's inventory.</summary>
        internal bool Gave;

        public readonly bool IsScratchMedic() => scratchMedic;

        static bool HasUsable(List<ItemInstance>? items, MedicSupplyPolicy.Supply supply)
        {
            if (items is null) return false;
            int f = supply == MedicSupplyPolicy.Supply.RobotRepair ? ItemInfo.RobotKitFunction : ItemInfo.FirstAidKitFunction;
            for (int i = 0; i < items.Count; i++) if (items[i].ItemFunction == f && items[i].Charges > 0.0f) return true;
            return false;
        }

        public readonly bool MainInventoryHasUsableKit(MedicSupplyPolicy.Supply supply) =>
            world.Characters.Cold(slot)?.Inventory is { } inv && HasUsable(inv.Items, supply);

        public readonly bool BackpackHasUsableKit(MedicSupplyPolicy.Supply supply) =>
            world.Characters.Cold(slot)?.Inventory?.Backpack?.Contents is { } bag && HasUsable(bag.Items, supply);

        public bool GiveReplacement(string itemId)
        {
            if (!world.Characters.Next[slot].Alive) return false;
            if (Items.CreateRewardItem(data, itemId, -1) is not { } item) return false;
            Gave = Items.Give(world, data, world.Characters.IdOf(slot), item, destroyOnFail: true);
            return Gave;
        }

        public readonly void RechargeUsedKit() => usedKit.Charges = usedKit.MaxCharges;
    }
}
