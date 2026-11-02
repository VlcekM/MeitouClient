using System.Numerics;
using Meitou.Engine;
using Meitou.Engine.Input;
using Meitou.Rendering;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The player's hands and the minimal HUD (docs/game/ui-input.md): click or box select, right click move orders (shift queues; holding the
/// button steers: the order follows the cursor),
/// the squad keys, the clock and speed buttons. Everything the player does becomes a <see cref="SimCommand"/> at the world's
/// current tick; this class never touches the simulation's state, it reads the published snapshot.
/// Positions are framebuffer pixels, top-left origin. The host sets <see cref="ViewProjection"/>, <see cref="Eye"/>, <see cref="Width"/>
/// and <see cref="Height"/> each frame (events arrive on the same thread, between frames).
/// </summary>
sealed partial class PlayerInterface(WorldSession session, Func<float, float, float> ground, Func<CharacterSnapshot, Vector3> where)
{
    public Matrix4x4 ViewProjection = Matrix4x4.Identity;
    public Vector3 Eye;
    public int Width = 1, Height = 1;
    /// <summary>How far from the camera a box select still takes characters.</summary>
    public const float BoxSelectRange = 7500;
    /// <summary>A person's height (the renderer's sphere takes 20) for the box select's aim point and the pick capsule of a character the renderer does not draw.</summary>
    public const float CharacterHeight = 18;
    const float DragThreshold = 5;

    Vector2? dragFrom;
    Vector2 mouse;
    bool dragging;
    // The held right button's move order: where it goes now and the tick it was given.
    bool steering;
    Vector3 steerTarget;
    long steerTick;


    IReadOnlyList<CharacterId> SelectedIds() => [.. Selected().Select(c => c.Id)];

    IReadOnlyList<CharacterId> PlayerCharacters() =>
        [.. session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).Select(c => c.Id)];

    /// <summary>
    /// The active squad's members in squad order, the first leading (the squad bar's; the number keys and select_all pick from them, the original's <c>UiSelectSquadMember</c> /
    /// <c>UiSelectAllInSquad</c>). Every player character when there is no squad list (a world without a player squad).
    /// </summary>
    IReadOnlyList<CharacterId> ActiveSquadMembers()
    {
        var snapshot = session.CurrentSnapshot;
        if (snapshot.PlayerSquads.FirstOrDefault(s => s.Id == snapshot.ActiveSquad) is not { } squad) return PlayerCharacters();
        var present = PlayerCharacters().ToHashSet();
        return [.. squad.Members.Where(present.Contains)];
    }

    // ---- commands ----

    void Say(SimCommand command)
    {
        SoundForCommand(command);
        session.World.Commands.Enqueue(command);
    }

    /// <param name="toggle">A single character with shift: one already selected leaves the selection (<see cref="SelectCommand.Toggle"/>).</param>
    public void Select(IReadOnlyList<CharacterId> ids, bool additive, bool toggle = false)
    {
        if (ids.Count > 0) SetBuilding(null);   // selecting a character ends the building's selection
        Say(new SelectCommand(ids, additive, toggle && additive) { Tick = session.World.Tick });
    }

    public void Move(Vector3 target, bool queued) =>
        Say(new MoveOrder([], target) { Tick = session.World.Tick, Queued = queued });

    // ---- events ----

    /// <summary>Returns true when the click belonged to the HUD or the interface (the camera input should not see it).</summary>
    public bool MouseDown(MouseButton button, Vector2 p, bool shift, bool ctrl = false, bool alt = false)
    {
        mouse = p;
        (pressShift, pressCtrl) = (shift, ctrl);
        HideTooltip();   // a press captures the mouse, which hides a tooltip (MyGUI)
        if (button == MouseButton.Left && FeedbackButtonDown(p)) return true;
        if (EditorPointerDown(button, p)) return true;
        if (button == MouseButton.Right && CancelInventoryDrag()) return true;
        if (button == MouseButton.Right && !OverHud(p) && WallCancel()) return true;
        if (button == MouseButton.Left)
        {
            if (contextMenu is not null) return true;   // an open menu takes no press: its rows act on the release, a press beside it does nothing (FUN_1407a7f10)
            if (UiMouseDown(p)) return true;
            if (OverHud(p)) return true;
            // A ground item's name label (shown while Highlight is held) takes the left press; its release over it is the item's pick-up (FUN_1401638f0).
            if (LabelAt(p) is { } label) { labelPress = label; return true; }
            if (BuildClick()) return true;
            dragFrom = p;
            dragging = false;
            return true;
        }
        if (button == MouseButton.Right && InventoryRightClick(p, shift)) return true;
        if (button == MouseButton.Right && CloseContextMenu()) return true;
        if (button == MouseButton.Right && MapRightDown(p)) return true;
        // A right press on a squad bar portrait is remembered; its release over the same portrait selects the member and focuses the camera on it, as the double click
        // (FUN_140413d80, docs/game/ui-input.md "Squad bar portraits"). The original has no portrait menu; Meitou's (PortraitMenu) opens on a held press, the quick release still focuses.
        if (button == MouseButton.Right && SquadBarMemberAt(p) is { } member)
        {
            if (PortraitMenu && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == member) is { } shown
                && ContextPressOn(p, at => Menu(shown.Name, at, Verbs(shown)), () => FocusMember(member, shift))) return true;
            memberPress = member;
            return true;
        }
        if (button == MouseButton.Middle && UiVisible && MapPointerDown(p)) return true;
        if (button == MouseButton.Right && !OverHud(p))
        {
            // Alt + right click on a downed or dead character: the nearest selected loots it (take all). Ctrl + right click: picks it up, or puts it down when a selected
            // character carries it. A plain right click on one of your own downed characters that needs no first aid picks it up too (the original's quick right click).
            // A ground item's name label (shown while Alt is held) is drawn over everything: a click on it is the item's, never the body lying beside it.
            var downed = (alt || ctrl) && LabelAt(p) is null ? PickDowned(p) : null;
            if (alt && downed is { } loot && SelectedIds() is { Count: > 0 })
                Say(new Meitou.Simulation.Carrying.LootOrder([], loot) { Tick = session.World.Tick });
            else if (ctrl && downed is { } body && Selected().Where(c => c.Carrying == body).Select(c => c.Id).ToArray() is { Length: > 0 } carriers)
                Say(new Meitou.Simulation.Carrying.PutDownOrder(carriers) { Tick = session.World.Tick });
            else if (ctrl && downed is { } lift && SelectedIds() is { Count: > 0 })
                Say(new Meitou.Simulation.Carrying.PickUpOrder([], lift) { Tick = session.World.Tick });
            // Else the nearest target along the ray (PickAt). On a character: the quick order (trade with a trader, attack anyone else, treat a hurt ally, pick up a downed one of yours,
            // loot anyone else's) is given when the button comes up before the context menu's hold time; held longer, the menu opens instead (PlayerInterface.ContextMenu.cs).
            else if (PickAt(p).Kind is var kind && kind == Picked.Character) { var quick = QuickOrder(p)?.Do; if (!ContextPress(p, quick)) quick?.Invoke(); }
            else if (kind == Picked.GroundItem && GroundItemClick(p)) { }
            else if (kind == Picked.Container && ContainerClick(p, alt)) { }
            else if (kind == Picked.Door && DoorClick(p, alt)) { }
            else if (kind == Picked.Bed && BedPress(p)) { }
            else if (kind == Picked.Cage && CageClick(p)) { }
            else if (kind == Picked.Dummy && DummyPress(p)) { }
            else if (kind == Picked.Rock && RockPress(p)) { }
            else if (kind is Picked.Site or Picked.None && SitePress(p)) { }
            else if (Ray(p) is { } ray && MoveHit(ray.Origin, ray.Direction) is { } hit)
            {
                Move(hit, shift);
                PlaceMarker(hit);
                (steering, steerTarget, steerTick) = (!shift, hit, session.World.Tick);
            }
            return true;
        }
        return OverHud(p);
    }

    /// <summary>
    /// The order a quick right click on a character gives and the mouse pointer that shows it (PlayerInterface.Pointer.cs), or null when there is none: the click does nothing then (the
    /// original treats any character hit as handled, so it does not move either). The target is the nearest character under the cursor, as the original's pick takes it; with nothing
    /// selected the pointer is still given (<c>Do</c> is null). See <see cref="QuickOrderFor"/>.
    /// </summary>
    (string Pointer, CharacterId Target, Action? Do)? QuickOrder(Vector2 p)
    {
        if (PickWhereAt(p, _ => true) is not { } hit || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == hit.Id) is not { } target) return null;
        return QuickOrderFor(target, SelectedIds());
    }

    /// <summary>The talk pointer: <c>s_talk</c> while the character holds a line for the player (Dialogue <c>playerInterruptionDialog</c> +0xc8, <c>FUN_1407fb410</c>), else <c>talk</c>.</summary>
    string TalkPointer(CharacterSnapshot c) => session.World.WantsToTalk(c.Id) ? "s_talk" : "talk";

    /// <summary>
    /// The quick right click on <paramref name="c"/> (<c>FUN_1407fb410</c>, docs/game/ui-input.md "Quick right click"). The lead is the selected character nearest to it (with nothing selected the
    /// nearest of the player's characters, which gives the pointer only). Own characters: dead, loot; needing first aid (organic need above 0.01) 25, else robot repair 60 (all the selection); down
    /// and not carried, pick up (the nearest); carried by the lead, put down; carried by another, nothing. Others carried by a non-player: nothing. Hostile ones (<see cref="Hostile(CharacterSnapshot)"/>): down,
    /// loot; standing and the lead sneaking, stealth KO (or loot of a slave); fighting or without a talk, attack (with a gunner on a turret, Shoot At); a prisoner, loot. Neutral ones: down, loot
    /// (<c>search</c> pointer for a bandit, slave or not-real faction, else <c>scavenge</c>); standing and the lead sneaking, pick the shackles of a prisoner, loot a slave, else stealth KO; else talk
    /// when it has something to say (a trader trades), otherwise nothing (<c>green</c> pointer when allied to the lead).
    /// </summary>
    (string Pointer, CharacterId Target, Action? Do)? QuickOrderFor(CharacterSnapshot c, IReadOnlyList<CharacterId> selection)
    {
        var characters = session.CurrentSnapshot.Characters;
        CharacterSnapshot? lead = null;
        float nearest = float.MaxValue;
        foreach (var o in characters)
        {
            if (selection.Count > 0 ? !selection.Contains(o.Id) : !o.IsPlayer) continue;
            float d = Vector3.DistanceSquared(o.Position, c.Position);
            if (d < nearest) (lead, nearest) = (o, d);
        }
        if (lead is null) return null;
        var world = session.World;
        long tick = world.Tick;
        bool give = selection.Count > 0;
        Action? Do(Action a) => give ? a : null;
        bool carried = !c.CarriedBy.IsNone;
        if (carried && characters.FirstOrDefault(o => o.Id == c.CarriedBy) is not { IsPlayer: true }) return null;
        bool dead = c.Body is { Dead: true }, down = c.Down;
        var loot = Do(() => Say(new Meitou.Simulation.Carrying.OpenLootOrder([], c.Id) { Tick = tick }));
        var id = c.Id;
        if (c.IsPlayer)
        {
            if (dead) return ("search", id, loot);
            if (c.Body is { NeedsAid: true })
            {
                bool robot = c.Body is { NeedsRobotAid: true };
                return ("med", id, Do(() => OrderOrJob(robot ? Meitou.Data.Gameplay.Ai.TaskType.FIRST_AID_ROBOT : Meitou.Data.Gameplay.Ai.TaskType.FIRST_AID_ORDER, id,
                    () => Say(new Meitou.Simulation.FirstAid.TreatOrder(selection, id) { Tick = tick }))));
            }
            if (!down) return null;
            if (!carried) return lead.Id == id ? null : ("lift", id, Do(() => Say(new Meitou.Simulation.Carrying.PickUpOrder([], id) { Tick = tick })));
            return lead.Carrying == id ? ("lift", id, Do(() => Say(new Meitou.Simulation.Carrying.PutDownOrder([lead.Id]) { Tick = tick }))) : null;
        }
        bool sneaking = c.CanStealthKo;
        if (Hostile(c))
        {
            if (down) return ("search", id, loot);
            if (sneaking)
                return c.Slave is { State: 1 } ? ("scavenge", id, loot) : ("knockout", id, Do(() => Say(new Meitou.Simulation.Carrying.StealthKoOrder([lead.Id], id) { Tick = tick })));
            if (c.Prison is not null) return ("search", id, loot);
            if (c.InCombat || !(c.WarParty || c.Slave is { State: 1 }) || !world.CanTalk(id, lead.Id))
            {
                // Fighting back at someone attacking the squad is defence, not an assault on its faction.
                bool defence = c.FightsPlayer;
                return ("attk", id, Do(() =>
                {
                    if (lead.Turret is not null) Say(new Meitou.Simulation.Combat.ShootAtOrder(selection, id) { Tick = tick });
                    else OrderOrJob(Meitou.Data.Gameplay.Ai.TaskType.MELEE_ATTACK, id, () => Say(new Meitou.Simulation.Combat.AttackOrder(selection, id) { Tick = tick, Picked = true, Defensive = defence }));
                }));
            }
            return (TalkPointer(c), id, Do(() => Say(new Meitou.Simulation.Dialogue.TalkOrder(lead.Id, id) { Tick = tick })));
        }
        if (down) return (c.NoLootGuilt ? "search" : "scavenge", id, loot);
        if (sneaking)
        {
            if (c.Prison is not null)
                return ("lockpick", id, Do(() => { Say(new Meitou.Simulation.Locks.PickLockOrder([lead.Id], id) { Tick = tick }); Say(new Meitou.Simulation.Locks.ShackleOrder([lead.Id], id, Meitou.Simulation.Locks.ShackleWork.Pick) { Tick = tick }); }));
            return c.Slave is { State: 1 } ? ("scavenge", id, loot) : ("knockout", id, Do(() => Say(new Meitou.Simulation.Carrying.StealthKoOrder([lead.Id], id) { Tick = tick })));
        }
        // Order 12 (Task_PlayerTalkto) walks over and starts the conversation, a trader's included: its trade window is a reply of that conversation (DA_TRADE).
        if (world.CanTalk(id, lead.Id)) return (TalkPointer(c), id, Do(() => Say(new Meitou.Simulation.Dialogue.TalkOrder(lead.Id, id) { Tick = tick })));
        if (c.IsTrader) return ("talk", id, Do(() => Say(new Meitou.Simulation.Trading.OpenTrade(lead.Id, id) { Tick = tick })));   // engine: a shop whose keeper has no line to say
        return world.RegardsFriendly(id, lead.Id) ? ("green", id, null) : null;   // slot 126: the target regards the lead as friendly
    }

    public void MouseMove(Vector2 p)
    {
        mouse = p;
        if (EditorPointerMove(p)) return;
        if (ScrollThumbMove(p)) return;
        if (MapPointerMove(p)) return;
        if (UiVisible && faithfulScreens.PointerMove(p)) return;
        if (dragFrom is { } from && !dragging && Vector2.Distance(from, p) >= DragThreshold) dragging = true;
    }

    public void MouseUp(MouseButton button, Vector2 p, bool shift)
    {
        mouse = p;
        EditorPointerUp(button);
        if (ContextMenuRelease(button, p)) return;
        if (button == MouseButton.Left && labelPress is { } labelled)
        {
            labelPress = null;
            if (LabelAt(p) == labelled) LabelPickUp(labelled);
            return;
        }
        if (button == MouseButton.Left) { InventoryPointerUp(p); JobDragEnd(p); SquadPointerUp(p, shift); faithfulScreens.PointerUp(); mapDrag = null; }
        if (button == MouseButton.Middle) mapDrag = null;
        if (button == MouseButton.Right && memberPress is { } pressed)
        {
            memberPress = null;
            if (SquadBarMemberAt(p) == pressed) FocusMember(pressed, shift);
            return;
        }
        if (button == MouseButton.Right && MapRightUp(p)) return;
        if (button == MouseButton.Right) { ContextRelease(); SiteRelease(); RockRelease(); BedRelease(); DummyRelease(); }
        if (button == MouseButton.Right && steering)
        {
            Steer(final: true);
            steering = false;
        }
        if (button != MouseButton.Left || dragFrom is not { } from) return;
        dragFrom = null;
        if (dragging)
        {
            dragging = false;
            var boxed = InBox(from, p);
            Select(boxed, shift);
            if (boxed.Count > 0) Inspect(CharacterId.None);
        }
        else
        {
            // A click on another faction's character shows it in the information and medical panels and drops the squad's selection (docs/game/ui-screens.md section 4). Shift keeps the selection, a click on your own or on the ground clears the inspection.
            var hit = Pick(p);
            var other = hit is null && !shift ? PickWhere(p, c => !c.IsPlayer) : null;
            // A building under the cursor (and no character) is selected and the characters keep their selection (docs/game/ui-input.md "Building picking and selection").
            if (hit is null && PickWhere(p, _ => true) is null && PickBuilding(p) is { } building)
            {
                SetBuilding(building);
                Inspect(CharacterId.None);
                return;
            }
            Select(hit is { } id ? new[] { id } : [], shift, toggle: true);
            if (other is { } stranger) Inspect(stranger);
            else if (!shift || hit is not null) Inspect(CharacterId.None);
        }
    }

    /// <summary>Shows <paramref name="target"/> (another faction's character; <see cref="CharacterId.None"/> for nobody) in the HUD panels.</summary>
    void Inspect(CharacterId target)
    {
        if (!target.IsNone) SetBuilding(null);
        Say(new InspectCommand(target) { Tick = session.World.Tick });
    }

    /// <summary>
    /// Once a frame: while the right button is held after a move order, the order is given again at the floor or terrain point under the cursor once every world
    /// tick (also when the camera moves and the cursor does not). <paramref name="final"/> (the button is let go) gives the last point unless this tick
    /// already has the same one.
    /// </summary>
    public void Steer(bool final = false)
    {
        UpdateContextMenu();
        UpdateFactionName();
        UpdateTownName();
        UpdateBed();
        UpdateDummy();
        UpdateMap();
        if (!final) UpdateBuild();
        if (!steering || OverHud(mouse)) return;
        if (Ray(mouse) is not { } ray || MoveHit(ray.Origin, ray.Direction) is not { } hit) return;
        PlaceMarker(hit);   // the marker follows the cursor every frame; the order only once a tick
        if (!final && session.World.Tick == steerTick) return;
        if (final && session.World.Tick == steerTick && hit == steerTarget) return;
        Move(hit, queued: false);
        (steerTarget, steerTick) = (hit, session.World.Tick);
    }

    /// <summary>The character the strategy camera follows (a double tap of its number key, F, or a double click on its squad button); panning the camera lets go.</summary>
    public CharacterId? Follow { get; set; }
    CharacterId? memberPress;
    /// <summary>Holding the right button on a squad bar portrait opens that member's context menu (Advanced settings "Portrait menu"; Meitou only).</summary>
    public bool PortraitMenu { get; set; } = true;

    /// <summary>A portrait's right click: selects the member (Shift toggles) and focuses the camera on it, as the double click.</summary>
    void FocusMember(CharacterId member, bool shift)
    {
        Select([member], shift, toggle: true);
        if (FlyTo?.Invoke(member) != true) CentreOn(member);
    }
    /// <summary>The host's camera flight to a double-clicked squad member (Advanced settings "Character flights"); false: centre on it at once.</summary>
    public Func<CharacterId, bool>? FlyTo { get; set; }
    /// <summary>A third press (a single one while the flight to that member is under way): the host ends the flight on it at once.</summary>
    public Action<CharacterId>? SkipFlight { get; set; }
    // The last number key and when it was pressed: the same key again within DoubleTapSeconds centres on the character (docs/game/ui-input.md, events 100..120).
    Key lastSelectKey;
    long lastSelectAt;
    const double DoubleTapSeconds = 0.5;

    /// <summary>Selects the squad's <paramref name="index"/>th member (<paramref name="shift"/> adds); the second press within half a second centres the camera on it and follows.</summary>
    void SelectMember(int index, bool shift, bool doubleTap)
    {
        var members = ActiveSquadMembers();
        if (index < 0 || index >= members.Count) return;
        Select([members[index]], shift, toggle: true);
        if (!doubleTap) SkipFlight?.Invoke(members[index]);
        else if (FlyTo?.Invoke(members[index]) != true) CentreOn(members[index]);
    }

    /// <summary>Centres the camera on the lead selected character and follows it (after a load, which drops the follow).</summary>
    /// <summary>Where the first selected character stands, or null with none selected.</summary>
    public Vector3? SelectedPosition => Selected().FirstOrDefault() is { } lead ? lead.Position : null;

    public void FollowSelected()
    {
        if (Selected().FirstOrDefault() is { } lead) CentreOn(lead.Id);
    }

    void CentreOn(CharacterId id, bool follow = true)
    {
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } c) return;
        var view = session.Camera.Current;
        session.Camera.Place(new Vector2(c.Position.X, c.Position.Z), view.Yaw, view.Pitch, view.Distance);
        Follow = follow ? id : null;
    }
}
