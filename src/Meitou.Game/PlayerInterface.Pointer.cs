using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using Meitou.Game.Ui;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    /// <summary>Ctrl and Alt as the host last saw them: they turn the pointer over a downed character into the carry and loot pointers.</summary>
    public bool CtrlHeld, AltHeld;
    // Whether the mouse was over a control in the last arranged UI (ArrangeUi), so the pointer needs no arrange of its own.
    internal bool pointerOverUi;
    // Whether that control is one a click acts on (an enabled button, or a control with an action: as UiClickables takes them), for the enhanced Hardware mouse's hand.
    bool pointerOverButton;
    // The pointer, the character it is over and the building (when no character), chosen again when the mouse, the camera or a modifier changed, and every RepickTicks ticks while only the
    // world moves (the picks walk every character, item and building near the ray: 0.5 ms).
    (long Tick, Vector2 Mouse, bool Ctrl, bool Alt, string Name, CharacterId? Target, (SelectedBuilding Building, float T)? Building) pointer = (-1, default, false, false, "arrow", null, null);
    Matrix4x4 pointerView;
    const long RepickTicks = 3;

    /// <summary>
    /// The mouse pointer (kenshi_pointers.xml) for what the right button would do under the cursor (docs/game/ui-input.md, pointers): the quick order's pointer
    /// (<see cref="QuickOrder"/>: talk, attk, med, lift, search), with Ctrl lift and with Alt search over a downed character; over the interface, while dragging or with no
    /// selection, the arrow.
    /// </summary>
    public string PointerName => Point().Name;

    /// <summary>
    /// The character under the cursor (within <see cref="OutlineReach"/> of the eye) for the hover outline and its colour: the one a right click would act on, in the colour of that order (attack red, treat
    /// green, talk gold, carry or loot blue), else any character under the cursor in white; null over the interface or nobody.
    /// </summary>
    public (CharacterId Id, Vector4 Colour)? HoverTarget => Point() is { Target: { } id } p && WithinOutlineReach(id) ? (id, PointerColour(p.Name)) : null;

    /// <summary>How far from the eye a hovered character or building is still outlined (engine choice; the pointer and the click reach farther).</summary>
    public const float OutlineReach = 2000;

    bool WithinOutlineReach(CharacterId id) =>
        session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } c && Vector3.DistanceSquared(Eye, where(c)) <= OutlineReach * OutlineReach;

    /// <summary>
    /// The building under the cursor within <see cref="OutlineReach"/> along the ray (no character there, as a left click picks it) for the building outline: its placement key, floor and the colour of what a right
    /// click on it would do, as <see cref="HoverTarget"/>'s; null over the interface or none.
    /// </summary>
    public (string Key, float Floor, Vector4 Colour)? HoverBuilding =>
        Point() is { Building: { } b } p && b.T <= OutlineReach ? (b.Building.Instance, b.Building.Position.Y, PointerColour(p.Name)) : null;

    /// <summary>The outline colour for the order a pointer shows: attack red, treat green, talk gold, carry or loot blue, else white.</summary>
    static Vector4 PointerColour(string pointer) => pointer switch
    {
        "attk" or "knockout" => new Vector4(1f, 0.3f, 0.22f, 1),
        "med" => new Vector4(0.4f, 1f, 0.5f, 1),
        "talk" or "s_talk" => new Vector4(1f, 0.82f, 0.4f, 1),
        "lift" or "search" or "scavenge" => new Vector4(0.45f, 0.75f, 1f, 1),
        _ => WhiteOutline,
    };

    /// <summary>How far from the camera's focus the held Highlight key outlines characters (<see cref="HighlightTargets"/>).</summary>
    public const float HighlightRadius = 500;

    /// <summary>
    /// The characters the held Highlight key (Alt) outlines (docs/game/ui-input.md "Hover outline"): every character, the squad's, people and animals, within <paramref name="radius"/>
    /// of <paramref name="centre"/>, each in the colour hovering it would give (the quick order's rules applied to it alone), and the hovered one as <see cref="HoverTarget"/>.
    /// </summary>
    public List<(CharacterId Id, Vector4 Colour)> HighlightTargets(Vector3 centre, float radius)
    {
        var hover = HoverTarget;
        var list = new List<(CharacterId Id, Vector4 Colour)>();
        if (hover is { } h) list.Add(h);
        var selection = SelectedIds();
        foreach (var c in session.CurrentSnapshot.Characters)
            if (c.Id != hover?.Id && Vector3.DistanceSquared(c.Position, centre) <= radius * radius)
                list.Add((c.Id, PointerColour(QuickOrderFor(c, selection)?.Pointer ?? "arrow")));
        return list;
    }

    /// <summary>Another faction's hostile character on its feet (not downed, dead or carried): the attack pointer and red outline whatever is selected.</summary>
    bool StandingHostile(CharacterSnapshot c) => !c.IsPlayer && c.Conscious && c.HostileToPlayer;

    static readonly Vector4 WhiteOutline = new(0.95f, 0.93f, 0.85f, 0.85f);

    /// <summary>
    /// Whether the character's faction is hostile to the player's, or it is fighting one of the player's characters (a neutral squad robbing them): only a hostile one gets
    /// the attack pointer, the red outline and the quick-attack right click.
    /// </summary>
    bool Hostile(CharacterId id) => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } c || Hostile(c);
    bool Hostile(CharacterSnapshot c) => c.HostileToPlayer;

    (long Tick, Vector2 Mouse, bool Ctrl, bool Alt, string Name, CharacterId? Target, (SelectedBuilding Building, float T)? Building) Point()
    {
        if (worldMap.mapDrag is not null) return (-1, mouse, CtrlHeld, AltHeld, "hand", null, null);   // the map's pan
        if (UiVisible && pointerOverUi || UiCapturesPointer || ContextMenuOpen || ModalOpen || dragging || settingsOpen || TitleOpen)
            return (-1, mouse, CtrlHeld, AltHeld, "arrow", null, null);
        if (BuildPointer() is { } building) return (-1, mouse, CtrlHeld, AltHeld, building, null, null);
        var snapshot = session.CurrentSnapshot;
        if (pointer.Tick >= 0 && (ulong)(snapshot.Tick - pointer.Tick) < RepickTicks && pointer.Mouse == mouse && pointer.Ctrl == CtrlHeld && pointer.Alt == AltHeld
            && pointerView == ViewProjection) return pointer;
        (string Name, CharacterId? Target) chosen = ("arrow", null);
        if (SelectedIds().Count > 0)
        {
            if ((CtrlHeld || AltHeld) && PickDowned(mouse) is { } downed) chosen = (AltHeld ? "search" : "lift", downed);
            else
                // The nearest target, as the right click takes it (PickAt).
                switch (PickAt(mouse).Kind)
                {
                    case Picked.Character when QuickOrder(mouse) is { } order: chosen = (order.Pointer, order.Target); break;
                    case Picked.GroundItem: chosen = (StealsGroundItem(mouse) ? "steal" : "pick_item", null); break;
                    case Picked.Container when PickContainer(mouse) is { } container: chosen = (ContainerPointer(container), null); break;
                    case Picked.Door when PickDoor(mouse) is { } door: chosen = (DoorPointer(door), null); break;
                    case Picked.Bed or Picked.Cage or Picked.Dummy or Picked.Seat: chosen = ("use", null); break;   // a bed's slot 130 pointer (UseableStuff, no inventory)
                    case Picked.Rock: chosen = ("mine", null); break;   // a resource rock a right click would mine (the original's `mine` pointer)
                    case Picked.Site when SitePointer(mouse) is { } siteMode: chosen = (siteMode, null); break;
                }
        }
        else if (QuickOrder(mouse) is { } over0) chosen = (over0.Pointer, over0.Target);   // nothing selected: the pointer only, as the original sets it before it looks for orders to give
        chosen.Target ??= PickWhere(mouse, _ => true);
        // A hostile character shows the attack pointer and the red outline even with nobody selected to send at it.
        if (chosen.Name == "arrow" && chosen.Target is { } over && snapshot.Characters.FirstOrDefault(c => c.Id == over) is { } overChar && StandingHostile(overChar))
            chosen.Name = "attk";
        session.World.Player.Pointed = chosen.Target ?? CharacterId.None;
        pointerView = ViewProjection;
        var hovered = chosen.Target is null ? PickBuildingAt(mouse) : null;
        return pointer = (snapshot.Tick, mouse, CtrlHeld, AltHeld, chosen.Name, chosen.Target, hovered);
    }

    /// <summary>
    /// The chance label over the <c>knockout</c> and <c>lockpick</c> pointers (docs/game/ui-input.md "Pointer chance label", <c>FUN_1406ea070</c>): the chance f, clamped to 0..1,
    /// as int(f x 100) "%", coloured (1 - f, f, 0), its middle on the mouse's x and its bottom 32 pixels at 1080 above it. Shown whatever draws the pointer (the Hardware mouse too).
    /// </summary>
    void AddPointerChance(List<UiControl> controls)
    {
        if (uiPainter is not { } painter || PointerChance(Point()) is not { } chance) return;

        float f = Math.Clamp(chance, 0, 1);
        var lines = new List<TooltipLine> { new("", ((int)(f * 100)).ToString(CultureInfo.InvariantCulture) + "%") };
        string colour = string.Create(CultureInfo.InvariantCulture, $"{1 - f:0.###} {f:0.###} 0");
        float minimum = TitleOpen ? 0 : StandardPixels;
        var width = UiTooltips.Arrange(lines, TooltipKind.Floating, Width, Height, mouse, null,
            (text, wrap) => painter.MeasureText(TooltipFont, text, Height, wrap, minimum) is var s ? new(s.Width, s.Height) : default).Panel.Width;

        var bottomRight = new Vector2(mouse.X + width / 2, mouse.Y - PointerChanceRise * Height / 1080f);
        PlaceTooltip(controls, painter, lines, TooltipKind.Floating, null, "pointer-chance", bottomRight, [colour]);
    }

    /// <summary>
    /// The pointed chance (0..1) the label shows: the stealth KO chance / 100 of the pointed character under <c>knockout</c> (published only while a selected character sneaks),
    /// the pick chance under <c>lockpick</c> (<see cref="PointedLockChance"/>); null for any other pointer.
    /// </summary>
    float? PointerChance((long Tick, Vector2 Mouse, bool Ctrl, bool Alt, string Name, CharacterId? Target, (SelectedBuilding Building, float T)? Building) p) => p.Name switch
    {
        "knockout" when p.Target is { } id => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.StealthKoChance * 0.01f,
        "lockpick" => PointedLockChance(p.Target),
        _ => null,
    };

    /// <summary>
    /// The pick chance of the lock under the <c>lockpick</c> pointer, for the selected character nearest it, without the minimum the menu applies: a caged
    /// <paramref name="target"/>'s cage, else the container, else the door under the mouse.
    /// </summary>
    float? PointedLockChance(CharacterId? target)
    {
        var snapshot = session.CurrentSnapshot;
        if (target is { } id && snapshot.Characters.FirstOrDefault(c => c.Id == id) is { Prison: { LockLevel: >= 0 } prison } caged)
            return LockChance(caged.Position, prison.LockLevel);
        if (PickContainer(mouse) is { } container) return LockChance(container.Position, container.Level);
        if (PickDoor(mouse) is { Door: var door }) return LockChance(door.Position, snapshot.Doors.GetValueOrDefault((door.Building, door.Door)).Level);
        return null;
    }

    /// <summary>The original's 32 pixels (x its UI scale) between the mouse and the chance label's bottom edge.</summary>
    const float PointerChanceRise = 32;

    /// <summary>Draws the pointer <paramref name="name"/> at <paramref name="at"/> (framebuffer pixels) over everything drawn so far; nothing without the UI painter.</summary>
    public void DrawPointer(string name, Vector2 at, int width, int height, DebugOverlay.OverlayPass? pass = null)
        => uiPainter?.DrawPointer(name, at, width, height, pass);

    /// <summary>
    /// Draws the pointer <paramref name="name"/> to the right of the Windows cursor at <paramref name="at"/> (the enhanced Hardware mouse): its left edge <see cref="BesideOffset"/>
    /// right of the mouse, its middle 14 px below it (both at 1080); nothing for <c>arrow</c>, the walk or no order.
    /// </summary>
    public void DrawPointerBeside(string name, Vector2 at, int width, int height, DebugOverlay.OverlayPass? pass = null)
    {
        if (name != "arrow") uiPainter?.DrawPointerBeside(name, at + BesideOffset * (height / 1080f), width, height, pass);
    }

    /// <summary>Where the enhanced Hardware mouse puts the middle of the action pointer's left edge, from the mouse at 1080: just clear of the Windows arrow (engine choice).</summary>
    static readonly Vector2 BesideOffset = new(14, 14);

    /// <summary>
    /// Whether the enhanced Hardware mouse shows the hand for the pointer <paramref name="name"/>: an order a right click would give (not the walk, the map's pan or
    /// ground the characters cannot reach), or an enabled button under the mouse.
    /// </summary>
    public bool PointsAtClickable(string name) => name is not ("arrow" or "hand" or "invalidmove") || UiVisible && pointerOverButton;

    /// <summary>The pointer <paramref name="name"/> as a picture with its hot spot, for the window system to draw (<see cref="Meitou.Game.Rendering.Ui.UiPainter.PointerImage"/>).</summary>
    public (Meitou.Data.Textures.RgbaImage Image, int HotX, int HotY)? PointerImage(string name) => uiPainter?.PointerImage(name);
}
