using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the sim half of upstream/ProvingGrounds/plugin/src/MarksHUD.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6b (PA §2.23; CAP 7.1,
// 7.9). Every 250 ms of PgClock time (Tick :87-98) it decides whether "Marks: N" shows (Refresh :42-45, :61-63: an active save and the selected
// player character in Scratch) and publishes that as the MarksHudModel; the label itself, its placement above the money display (:46-81) and the
// money label's visibility are the window half's (src/Ui/MarksHUD.cs, an AddUi overlay label). Decisions: Core/Glue/MarksHUDLogic.cs (tested).
// Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial steps only.
//
// Engine calls of the C++ and what replaces them (E1):
//   GetTickCount() ................................ PgClock.NowMs (sim ms, frozen while paused)
//   LeaderboardStore::HasActiveSave() ............. ArenaNativePersistence.Ready (the system passes it)
//   gui->selectedPlayerCharacter.getCharacter() ... world.Player.Selection (PlayerState.cs:91): the selected character with the lowest table slot,
//                                                   the one Meitou's HUD describes when no other faction's character is inspected
//                                                   (PlayerInterface.Lead / Selected, PlayerInterface.cs:1261-1277; divergences.md)
//   character->isValid() / isPlayerCharacter() .... PgEngine.Exists / PgEngine.IsPlayer
//   character->getCurrentTownLocation()->getGameData()->stringID == "90-Proving Grounds.mod"
//                                                   ... Towns.InScratch(world, character) (task 2.2: Scratch = TOWN 90 of the data mod; since the task 7.2 review the
//                                                   status town, Towns.CurrentTownOf)
//   LeaderboardStore::GetMarks(character) ......... LeaderboardStore.GetMarks (src/Game/LeaderboardStore.cs, C++ LeaderboardStore.cpp:457-473)
//   PGLog::Error .................................. ModLog.Error
//   MyGUI widgets, moneyText / moneyLabel ......... the window half
// Callers (C++ -> port): ArenaUI.cpp:3165 Tick (the frame loop's MarksHUD.Tick slot); ArenaUI.cpp:3199 AbandonWorldState (ArenaUISim's
// OtherWindowsAbandon, task 4.5a).

/// <summary>What the Marks HUD shows (task 4.6b): <see cref="Show"/> (the sim half's conditions; the window side adds the money label's), the
/// caption, and <see cref="Epoch"/>, bumped by each AbandonWorldState (the window side's failure latch is per epoch, as the C++'s
/// <c>failed</c> is reset there). Published under <see cref="MarksHUDSim.ModelId"/> in the snapshot's model map (not a screen).</summary>
sealed record MarksHudModel(bool Show, string Text, int Epoch) : PgUiModel;

/// <summary>The sim half of the Marks HUD (task 4.6b). One per world, owned by ProvingGroundsSystem.</summary>
sealed class MarksHUDSim(MarksHUDSim.Services services)
{
    /// <summary>The model's key in <see cref="PgSnapshot.Ui"/>.</summary>
    internal const string ModelId = "pg.marks";

    /// <summary>What the HUD reads from the rest of the system: the ledger's Ready, the towns, LeaderboardStore::GetMarks and the clock.</summary>
    internal sealed record Services(Func<bool> HasActiveSave, Towns Towns, Func<World, CharacterId, int> Marks, PgClock Clock);

    readonly Services s = services;
    readonly MarksHudClock clock = new();
    MarksHudModel model = new(false, "", 0);
    int epoch;

    internal MarksHudModel Model => model;

    /// <summary>The F10 phase row.</summary>
    internal string Phase => clock.Failed ? "unavailable" : model.Show ? "shown: " + model.Text : "hidden";

    void Publish(bool show, string text)
    {
        if (model.Show == show && model.Text == text && model.Epoch == epoch) return;
        model = new MarksHudModel(show, text, epoch);
        // TEST ONLY: one line per change while the test verbs are armed (the marks-debug scenario reads it)
        if (Spikes.SpikePatches.Armed)
            ProvingGroundsMod.Ctx?.Log("PG-MARKS {\"show\":" + (show ? "true" : "false") + ",\"text\":" + PGLog.Quote(text) + ",\"epoch\":"
                + epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
    }

    /// <summary>C++ <c>gui->selectedPlayerCharacter</c>: the selected character with the lowest slot that resolves this tick, or none.</summary>
    static CharacterId SelectedCharacter(World world)
    {
        var best = CharacterId.None;
        foreach (var c in world.Player.Selection)
            if (PgEngine.Exists(world, c) && (best.IsNone || c.Slot < best.Slot)) best = c;
        return best;
    }

    /// <summary>C++: MarksHUD.cpp:87-98 Tick (and the sim half of Refresh, :42-45 + :61-63).</summary>
    internal void Tick(World world)
    {
        if (!clock.Due(s.Clock.NowMs)) return;
        try
        {
            var selected = SelectedCharacter(world);
            bool valid = !selected.IsNone && PgEngine.Exists(world, selected);
            bool player = valid && PgEngine.IsPlayer(world, selected);
            // C++ InScratch :34 tests valid and player before reading the town
            bool scratch = player && s.Towns.InScratch(world, selected);
            var d = MarksHudLogic.Decide(s.HasActiveSave(), MarksHudLogic.InScratch(!selected.IsNone, valid, player, scratch),
                () => s.Marks(world, selected), model.Text);
            Publish(d.Show, d.Text);
        }
        catch (Exception e)
        {
            Publish(false, model.Text);   // C++ :95 Hide(); failed = true
            clock.Fail();
            ModLog.Error(MarksHudTexts.Unavailable + e.Message);
        }
    }

    /// <summary>C++: MarksHUD.cpp:100-104 AbandonWorldState: the panel destroyed (hidden until a refresh shows it again), the clock and the failure
    /// latch reset.</summary>
    internal void AbandonWorldState()
    {
        clock.Reset();
        epoch++;
        Publish(false, "");   // the C++ label is destroyed: a new one starts with an empty caption (:56)
    }
}
