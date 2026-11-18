using Meitou.Simulation;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Town;

// Task 8.4b (TW §2.9 "After the fight" steps 6-7, CAP 12, spike S8): the host's bed orders and reads for the treatment (Core/Glue/
// TownAftercareLogic.Beds.cs: the coordinator's bed, the delivery poll). The bed is always the one the coordinator chose, given to the trip as its
// Site; the engine's own bed search (FixtureRegistry.FindBed) is never asked. Engine calls (E1): PUT_SOMEONE_IN_BED ... Beds.PutIn (FindBedAndPutIn,
// source Npc); USE_BED_ORDER ... Beds.UseBed (UseBed, source Npc); the setBedMode hook's "lies in this bed" ... Beds.LiesIn.

sealed partial class TownAftercare
{
    sealed partial class Host
    {
        public bool PutInBed(CharacterId medic, string bed) => Beds.PutIn(W, medic, bed);
        public bool UseBed(CharacterId patient, string bed) => Beds.UseBed(W, patient, bed);
        public bool LiesIn(CharacterId c, string bed) => Beds.Find(W, bed) is { } f && Beds.LiesIn(W, c, f);
    }
}
