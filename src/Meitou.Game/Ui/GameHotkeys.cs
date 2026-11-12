using Meitou.Game.Host.Input;
using Meitou.Simulation;
using Meitou.Simulation.Player;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.Ui;

/// <summary>
/// The game view's hotkeys (docs/game/ui-input.md sections 4 and 6): each goes through the rebinding table (<see cref="InputBindings"/>), so the
/// settings' Controls page rebinds them. The orders hotkeys run the orders panel's own button actions, as the game's handler <c>FUN_1407226e0</c>
/// calls the buttons' handlers.
/// </summary>
sealed class GameHotkeys(PlayerInterface player)
{
    /// <summary>
    /// select_0..9: the squad bar's members by position.
    /// </summary>
    static readonly InputAction[] SelectMemberActions = [.. Enumerable.Range(0, 10).Select(i => InputAction.SelectMember1 + i)];

    /// <summary>
    /// The actions handled by <see cref="Key"/> and <see cref="PlayerInterface.UiKey"/>: a key bound to one of them is the game's, not the cinema's
    /// numpad key.
    /// </summary>
    static readonly InputAction[] HotkeyActions =
    [
        InputAction.ToggleInventory, InputAction.ToggleCharacterStats, InputAction.ToggleMap, InputAction.ToggleFaction,
        InputAction.ToggleResearch, InputAction.ToggleCrafting, InputAction.ToggleHelp,
        InputAction.ChangeSquad, InputAction.SelectAll, InputAction.NextCharacter, InputAction.PreviousCharacter,
        InputAction.FocusCharacter, InputAction.StopMovement,
        InputAction.ToggleBuild, InputAction.ToggleBlock, InputAction.ToggleHold, InputAction.TogglePassive,
        InputAction.ToggleJobs, InputAction.ToggleRanged,
        InputAction.ToggleSneak, InputAction.ToggleTaunt, InputAction.CycleRunSpeed, InputAction.Medic,
        InputAction.Rescue, InputAction.Prospect, InputAction.Rest, InputAction.PutDown,
        .. SelectMemberActions,
    ];

    /// <summary>
    /// Whether <paramref name="key"/> is one of <paramref name="action"/>'s bindings.
    /// </summary>
    internal bool Bound(InputAction action, EngineKey key) => player.Session.Bindings.Get(action).Contains(Binding.Of(key));

    /// <summary>
    /// Whether <paramref name="key"/> opens a window: inventory, character, help, map, faction, research or crafting.
    /// </summary>
    internal bool WindowKey(EngineKey key) =>
        Bound(InputAction.ToggleInventory, key) || Bound(InputAction.ToggleCharacterStats, key) || Bound(InputAction.ToggleHelp, key)
        || Bound(InputAction.ToggleMap, key) || Bound(InputAction.ToggleFaction, key) || Bound(InputAction.ToggleResearch, key)
        || Bound(InputAction.ToggleCrafting, key);

    /// <summary>
    /// Whether <paramref name="key"/> is bound to a game-view hotkey (the host then keeps it from the cinema's numpad keys).
    /// </summary>
    public bool IsHotkey(EngineKey key) => HotkeyActions.Any(a => Bound(a, key));

    /// <summary>
    /// The order toggle an orders hotkey stands for (toggle_block .. toggle_taunt), or null.
    /// </summary>
    CharacterOrders? OrderHotkey(EngineKey key) =>
        Bound(InputAction.ToggleBlock, key) ? CharacterOrders.Block : Bound(InputAction.ToggleHold, key) ? CharacterOrders.Hold
        : Bound(InputAction.TogglePassive, key) ? CharacterOrders.Passive : Bound(InputAction.ToggleJobs, key) ? CharacterOrders.Jobs
        : Bound(InputAction.ToggleRanged, key) ? CharacterOrders.Ranged : Bound(InputAction.ToggleSneak, key) ? CharacterOrders.Sneak
        : Bound(InputAction.ToggleTaunt, key) ? CharacterOrders.Taunt : null;

    /// <summary>
    /// The double-tap pairing of <c>FUN_140788100</c>: the last single event's action is remembered (any bound hotkey, not only the digits) with its
    /// time; an event is a double when it is the same action again less than half a second later. A double does not clear the memory (a third press
    /// is a double again); a different action replaces it, and two events in one frame forget it.
    /// </summary>
    bool HotkeyDoubleTap(EngineKey key)
    {
        var action = HotkeyActions.Where(a => Bound(a, key)).Select(a => (InputAction?)a).FirstOrDefault();
        if (action is null) return false;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool firstEvent = ++player.HotkeyEventsThisFrame == 1;
        bool doubleTap = firstEvent && action == player.LastHotkey
            && System.Diagnostics.Stopwatch.GetElapsedTime(player.LastHotkeyAt, now).TotalSeconds < PlayerInterface.DoubleTapSeconds;
        (player.LastHotkey, player.LastHotkeyAt) = firstEvent ? (action, now) : (null, now);
        return doubleTap;
    }

    /// <summary>
    /// A game hotkey: member selection (a double tap centres), build mode, focus, select all, next and previous, then the orders (not from the free
    /// camera).
    /// </summary>
    public void Key(EngineKey key, bool shift)
    {
        player.UiInput++;
        bool doubleTap = HotkeyDoubleTap(key);

        // select_0..9 (events 100..109, keys 1..9 and 0 by default): the active squad's members; a second press within half a second centres on the
        // member.
        if (Array.FindIndex(SelectMemberActions, a => Bound(a, key)) is var member and >= 0)
        {
            player.SelectMember(member, shift, doubleTap);
            return;
        }

        bool free = player.Session.Camera.IsFree;

        // toggle_build: the build window and the placement ghost; not over the title, the editor or while the free camera flies.
        if (Bound(InputAction.ToggleBuild, key) && !free && !player.TitleOpen && !player.CharacterEditor.EditorOpen)
            player.Build.ToggleBuildMode();
        // focus_char: put the pivot on the lead selected character without following it (the free camera's F is its down key).
        else if (Bound(InputAction.FocusCharacter, key) && !free && player.Selected().FirstOrDefault() is { } lead)
            player.CentreOn(lead.Id, follow: false);
        else if (Bound(InputAction.SelectAll, key)) player.Select(player.ActiveSquadMembers(), shift);
        else if (Bound(InputAction.NextCharacter, key)) StepCharacter(1);
        else if (Bound(InputAction.PreviousCharacter, key)) StepCharacter(-1);
        else if (free) return;   // the orders below are not given from the free camera (its R is up)
        else if (Bound(InputAction.StopMovement, key))
            player.Say(new StopCommand(player.SelectedIds()) { Tick = player.Session.World.Tick });
        else if (OrderHotkey(key) is { } order) player.ExecuteUiAction("order." + order, "");
        else if (Bound(InputAction.CycleRunSpeed, key)) player.ExecuteUiAction("runspeed.1", "");   // the right speed arrow (FUN_140722630)
        // Medic and Rescue: one search each, with Shift the job (58, with 57 for a squad with a robot / 148) added to the lists, as their buttons.
        else if (Bound(InputAction.Medic, key))
            player.Say(new Simulation.FirstAid.MedicJobCommand(player.SelectedIds(), shift) { Tick = player.Session.World.Tick });
        else if (Bound(InputAction.Rescue, key))
            player.Say(new Simulation.Carrying.RescueJobCommand(player.SelectedIds(), shift) { Tick = player.Session.World.Tick });
        else if (Bound(InputAction.Prospect, key)) player.ExecuteUiAction("prospect", "");
        else if (Bound(InputAction.Rest, key))
            player.Say(new Simulation.FirstAid.RestOrder(player.SelectedIds()) { Tick = player.Session.World.Tick });
        else if (Bound(InputAction.PutDown, key))
            player.Say(new Simulation.Carrying.PutDownOrder(player.SelectedIds()) { Tick = player.Session.World.Tick });
    }

    /// <summary>
    /// character_next / character_prev (<c>FUN_1407f9130</c>, docs/game/ui-input.md section 6): nothing unless the active squad has two members; the
    /// HUD's character, when it is a living one of the player's, gives way to the next (or previous) member of its own squad, wrapping; otherwise the
    /// selection narrows to its first character.
    /// </summary>
    void StepCharacter(int step)
    {
        var snapshot = player.Session.CurrentSnapshot;
        if (snapshot.PlayerSquads.FirstOrDefault(s => s.Id == snapshot.ActiveSquad) is not { Members.Count: >= 2 }) return;

        var present = player.PlayerCharacters().ToHashSet();
        if (player.Lead() is { IsPlayer: true, Body: not { Dead: true } } focus
            && snapshot.PlayerSquads.FirstOrDefault(s => s.Members.Contains(focus.Id)) is { } squad
            && squad.Members.Where(present.Contains).ToList() is { Count: > 0 } members)
        {
            int at = members.IndexOf(focus.Id);
            player.Select([members[((at + step) % members.Count + members.Count) % members.Count]], false);
            player.Inspect(CharacterId.None);
        }
        else if (player.Selected().FirstOrDefault() is { } first)
        {
            player.Select([first.Id], false);
            player.Inspect(CharacterId.None);
        }
    }
}