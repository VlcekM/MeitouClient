using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Rendering.Characters;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// The squad bar's portraits (both UI modes; docs/game/ui-screens.md): a cell per member as the original's <c>Kenshi_PortraitCharacter</c> lays it
/// out (the background, the portrait the renderer draws into its atlas, the name along the bottom, the frame that is the button), left to right on
/// the squad tab's page.
/// </summary>
sealed class PortraitBar(PlayerInterface player)
{
    #region State

    /// <summary>
    /// The ImageBox texture name of the renderer's portrait atlas (<see cref="CharacterRenderer.PortraitAtlas"/>).
    /// </summary>
    public const string PortraitTexture = "meitou:portraits";

    /// <summary>
    /// The portrait atlas's cells.
    /// </summary>
    internal const int PortraitCells = CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitRows;

    /// <summary>
    /// The cell layout's size (Kenshi_PortraitCharacter: 136 x 136); the parts below are placed in its pixels.
    /// </summary>
    const float PortraitLayoutSize = 136;

    /// <summary>
    /// The first atlas cell after the squad's: the conversation window's speaker.
    /// </summary>
    public const int DialogueNpcCell = PortraitCells - 2;

    /// <summary>
    /// The second atlas cell after the squad's: the conversation window's player character.
    /// </summary>
    public const int DialoguePlayerCell = PortraitCells - 1;

    /// <summary>
    /// How much of its atlas cell a portrait fills (<see cref="CharacterRenderer.PortraitSizeFor"/>); the host sets it with the renderer's look.
    /// </summary>
    public int PortraitSize { get; set; } = CharacterRenderer.FaithfulPortraitSize;

    /// <summary>
    /// How many of the player's characters are posed every frame however far from the eye (<see cref="FullRateCharacters"/>).
    /// </summary>
    const int FullRateCount = 10;

    /// <summary>
    /// The squad bar's page, its row height and how far its rows are scrolled (the ItemBox scrolls a row per wheel notch).
    /// </summary>
    UiRect _squadBarPage;

    /// <summary>
    /// The squad bar's row height (<see cref="_squadBarPage"/>).
    /// </summary>
    internal float SquadBarRow;

    /// <summary>
    /// How far the squad bar's rows are scrolled (<see cref="_squadBarPage"/>).
    /// </summary>
    internal float SquadBarScroll;

    /// <summary>
    /// When each member's flash started (<see cref="Stopwatch"/> ticks); a member leaves when its flash stops.
    /// </summary>
    internal readonly Dictionary<CharacterId, long> PortraitFlashes = [];

    #endregion

    #region Portrait members

    /// <summary>
    /// An ImageBox's <c>ImageRect</c> for atlas cell <paramref name="cell"/>: the part of the cell its portrait fills.
    /// </summary>
    internal string PortraitRect(int cell)
    {
        int x = cell % CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitCell;
        int y = cell / CharacterRenderer.PortraitColumns * CharacterRenderer.PortraitCell;
        return FormattableString.Invariant($"{x} {y} {PortraitSize} {PortraitSize}");
    }

    /// <summary>
    /// The player's characters posed every frame however far (<see cref="CharacterInstance.FullRateAnimation"/>) into <paramref name="into"/>: the
    /// first <see cref="FullRateCount"/> of the squad bar and then the other squads in order, and the first selected one.
    /// </summary>
    public void FullRateCharacters(HashSet<CharacterId> into)
    {
        into.Clear();
        foreach (var c in SquadBarMembers())
        {
            if (into.Count < FullRateCount) into.Add(c.Id);
        }

        foreach (var squad in player.Session.CurrentSnapshot.PlayerSquads)
        {
            foreach (var id in squad.Members)
            {
                if (into.Count < FullRateCount) into.Add(id);
            }
        }

        if (player.Selected().FirstOrDefault() is { } first) into.Add(first.Id);
    }

    /// <summary>
    /// The characters the squad bar shows, the active squad's in order: the first atlas cells (<see cref="PortraitMembers"/>).
    /// </summary>
    public IReadOnlyList<CharacterSnapshot> SquadBarMembers()
    {
        var byId = player.Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToDictionary(c => c.Id);
        return [.. player.ActiveSquadMembers().Select(byId.GetValueOrDefault).OfType<CharacterSnapshot>().Take(DialogueNpcCell)];
    }

    /// <summary>
    /// The characters whose portraits the host draws, each into the atlas cell of its index: the squad bar's first, then, while the squads screen is
    /// open, the other squads' members in order. As many as there are cells before the conversation's (30, CONSTANTS <c>max faction size</c>); a
    /// modded faction past that shows nameless cells.
    /// </summary>
    public IReadOnlyList<CharacterSnapshot> PortraitMembers()
    {
        var bar = SquadBarMembers();
        if (!player.SquadWindows.SquadsShown) return bar;   // the others are drawn (and posed out of range) only while the squads screen shows them

        var byId = player.Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).ToDictionary(c => c.Id);
        var shown = bar.Select(c => c.Id).ToHashSet();
        var rest = player.Session.CurrentSnapshot.PlayerSquads
            .SelectMany(s => s.Members)
            .Where(id => shown.Add(id))
            .Select(byId.GetValueOrDefault)
            .OfType<CharacterSnapshot>();
        return [.. bar.Concat(rest).Take(DialogueNpcCell)];
    }

    /// <summary>
    /// The speaker and the player character of the open conversation, whose portraits the host draws into <see cref="DialogueNpcCell"/> and
    /// <see cref="DialoguePlayerCell"/>.
    /// </summary>
    public (CharacterId Npc, CharacterId Player)? DialoguePortraitCharacters() =>
        player.Session.CurrentSnapshot.Conversation is { } talk ? (talk.Speaker.IsNone ? talk.Npc : talk.Speaker, talk.Player) : null;

    /// <summary>
    /// The number that turns a character's portrait camera (<see cref="CharacterSnapshot.PortraitSerial"/>; 0 for one not in the snapshot).
    /// </summary>
    public uint PortraitSerial(CharacterId id) => player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id)?.PortraitSerial ?? 0;

    /// <summary>
    /// The conversation window's two portraits over the layout's <c>NpcPortrait</c> and <c>PlayerPortrait</c> frames.
    /// </summary>
    internal void AddDialoguePortraits(List<UiControl> controls)
    {
        foreach (var (name, cell) in new[] { ("NpcPortrait", DialogueNpcCell), ("PlayerPortrait", DialoguePlayerCell) })
        {
            if (controls.FirstOrDefault(c => c.Name == name) is not { } frame) continue;

            controls.Add(new($"{name}-image", "ImageBox", frame.Bounds, "", "", "", true,
                new Dictionary<string, string> { ["ImageTexture"] = PortraitTexture, ["ImageFlipV"] = "true", ["ImageRect"] = PortraitRect(cell) },
                Vector4.Zero, Pickable: false, Clip: frame.Clip));
        }
    }

    #endregion

    #region Squad bar

    /// <summary>
    /// The wheel over the squad bar scrolls its rows; clamped when the bar is laid out.
    /// </summary>
    internal bool SquadBarWheel(Vector2 p, float steps)
    {
        if (SquadBarRow <= 0 || !_squadBarPage.Contains(p.X, p.Y)) return false;

        SquadBarScroll -= MathF.Sign(steps) * SquadBarRow;
        if (player.ScrollBars.InventoryViews.TryGetValue("SquadPanel", out var view))
            SquadBarScroll = player.ScrollBars.InventoryScroll["SquadPanel"] = Math.Clamp(SquadBarScroll, 0, view.Maximum);

        return true;
    }

    /// <summary>
    /// The squad bar member whose portrait is at <paramref name="p"/>, if any.
    /// </summary>
    internal CharacterId? SquadBarMemberAt(Vector2 p)
    {
        if (!player.UiVisible || MyGuiPresentation.HitTest(player.ArrangeUi(), p.X, p.Y) is not { Action: "meitou:member" } frame
            || !int.TryParse(frame.Argument, NumberStyles.None, CultureInfo.InvariantCulture, out int i)) return null;

        var members = SquadBarMembers();
        return i < members.Count ? members[i].Id : null;
    }

    /// <summary>
    /// Adds the squad bar to <paramref name="panel"/>'s tab page: the portrait cells in their grid, with a scroll bar when they do not fit.
    /// </summary>
    internal void AddSquadPortraits(List<UiControl> controls, UiRect panel)
    {
        var members = SquadBarMembers();
        if (members.Count == 0) return;

        // The tab's page (the layout's TabItem rectangle inside SquadPanel).
        var page = new UiRect(panel.X + panel.Width * 0.0183727f, panel.Y + panel.Height * 0.206897f, panel.Width * 0.965879f,
            panel.Height * 0.737069f);
        float scale = player.UiScale;
        var grid = SquadGrid(page, members.Count, scale);
        if (grid.Maximum > 0)
        {
            float barWidth = 17 * scale;
            page = page with { Width = Math.Max(0, page.Width - barWidth) };
            grid = SquadGrid(page, members.Count, scale);
            SquadBarScroll = Math.Clamp(player.ScrollBars.InventoryScroll.GetValueOrDefault("SquadPanel"), 0, grid.Maximum);
            player.ScrollBars.InventoryScroll["SquadPanel"] = SquadBarScroll;
            player.ScrollBars.InventoryViews["SquadPanel"] = (page, grid.Maximum);
            var track = new UiRect(page.X + page.Width, page.Y, barWidth, page.Height);
            float content = page.Height + grid.Maximum;
            player.ScrollBars.AddScrollBar("SquadPanel", controls, track, page.Height, content, SquadBarScroll, grid.Maximum, panel, "hud-squad");
        }
        else
        {
            player.ScrollBars.InventoryViews.Remove("SquadPanel");
            player.ScrollBars.InventoryTracks.Remove("SquadPanel");
            SquadBarScroll = 0;
        }

        _squadBarPage = page;
        SquadBarRow = grid.Size;
        SquadBarScroll = Math.Clamp(SquadBarScroll, 0, Math.Max(0, grid.Rows * grid.Size - page.Height));
        PrunePortraitFlashes(members);
        AddSquadCells(controls, members, page, grid.Size, grid.Columns);
    }

    /// <summary>
    /// The page's ItemBox (docs/game/ui-screens.md "Squad bar"): square cells 0.9 of its height, or of its width over the members (counted up to 10),
    /// 60 to 128 px at 1080p and even, filled left to right in rows; rows past the page are cut off. Returns the cell size, the columns, the rows and
    /// the scroll maximum.
    /// </summary>
    static (float Size, int Columns, int Rows, float Maximum) SquadGrid(UiRect page, int count, float scale)
    {
        float fit = Math.Min(page.Height * 0.9f, page.Width * 0.9f / Math.Min(count, 10)) / scale;
        float size = MathF.Floor(Math.Clamp(fit, 60, 128) / 2) * 2 * scale;
        int columns = Math.Max(1, (int)(page.Width / size));
        int rows = (count + columns - 1) / columns;
        float maximum = Math.Max(0, rows * size - page.Height);
        return (size, columns, rows, maximum);
    }

    /// <summary>
    /// The squad bar's cells on <paramref name="page"/>, <paramref name="columns"/> to a row of <paramref name="size"/>; rows scrolled off the page
    /// are left out.
    /// </summary>
    void AddSquadCells(List<UiControl> controls, IReadOnlyList<CharacterSnapshot> members, UiRect page, float size, int columns)
    {
        float k = size / PortraitLayoutSize;
        var camera = player.Session.Camera.Current;
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            float left = page.X + i % columns * size;
            float top = page.Y + i / columns * size - SquadBarScroll;
            if (top + size <= page.Y || top >= page.Y + page.Height) continue;

            UiRect At(float x, float y, float w, float h) => new(left + x * k, top + y * k, w * k, h * k);

            // The layout's depths, back to front: background, back overlay, portrait, front overlay, name, frame.
            var state = PortraitStateOf(m, camera.Eye, camera.Target);
            string background = (state & PortraitState.Hurt) != 0 ? "Hurt" : "Normal";
            float flash = PortraitFlash(m.Id, state);
            controls.Add(PortraitImage($"portrait-back-{i}", At(0, 0, 135, 135), "Background", background, flash) with { Clip = page });
            if (BackOverlayName(state) is { Length: > 0 } back)
                controls.Add(PortraitImage($"portrait-back-overlay-{i}", At(6, 5, 124, 124), "BackOverlay", back) with { Clip = page });

            var portrait = new Dictionary<string, string>
            {
                ["ImageTexture"] = PortraitTexture,
                ["ImageFlipV"] = "true",
                ["ImageRect"] = PortraitRect(i),
            };
            if ((state & PortraitState.Faded) != 0) portrait["Alpha"] = "0.25";
            controls.Add(new($"portrait-{i}", "ImageBox", At(6, 6, 124, 123), "", "", "", true, portrait, Vector4.Zero, Clip: page));
            if (FrontOverlayName(state) is { Length: > 0 } front)
                controls.Add(PortraitImage($"portrait-front-overlay-{i}", At(6, 6, 124, 123), "FrontOverlay", front) with { Clip = page });

            var nameProperties = new Dictionary<string, string> { ["TextAlign"] = "HCenter Bottom", ["TextColour"] = "1 1 1", ["TextShadow"] = "true" };
            controls.Add(new($"portrait-name-{i}", "TextBox", At(4, 4, 128, 126), m.Name, "", "Kenshi_TextboxStandardText", true,
                nameProperties, Vector4.Zero, Pickable: false, Clip: page));
            var frameProperties = new Dictionary<string, string> { ["Checked"] = m.Selected ? "true" : "false" };
            controls.Add(new($"member-{i}", "Button", At(0, 0, 136, 136), "", "meitou:member", "Kenshi_PortraitFrameSkin", true,
                frameProperties, Vector4.Zero, Argument: i.ToString(CultureInfo.InvariantCulture), Clip: page));
        }
    }

    #endregion

    #region Portrait states

    /// <summary>
    /// The squad portraits' states (docs/game/ui-screens.md "Portrait cells"): the original's per-cell flags and how its
    /// <c>Kenshi_PortraitCharacter</c> cell shows them, the <c>Background</c> (Normal, or a flashing Hurt), the <c>BackOverlay</c> (Combat, Hunger),
    /// the <c>FrontOverlay</c> (Slave, Prison) and the faded portrait.
    /// </summary>
    [Flags]
    enum PortraitState
    {
        /// <summary>
        /// No flag.
        /// </summary>
        None = 0,

        /// <summary>
        /// The character is selected.
        /// </summary>
        Selected = 1,

        /// <summary>
        /// Away from the camera and in danger: the red background, flashing.
        /// </summary>
        Hurt = 2,

        /// <summary>
        /// Down, carried or in bed: the portrait at a quarter alpha, and no flash.
        /// </summary>
        Faded = 4,

        /// <summary>
        /// In combat mode: the <c>Combat</c> back overlay.
        /// </summary>
        Combat = 8,

        /// <summary>
        /// Shackled: the <c>Slave</c> front overlay.
        /// </summary>
        Slave = 0x10,

        /// <summary>
        /// In a prison: the <c>Prison</c> front overlay.
        /// </summary>
        Prison = 0x20,

        /// <summary>
        /// Being eaten: the <c>Eaten</c> front overlay, over Prison and Slave.
        /// </summary>
        Eaten = 0x40,

        /// <summary>
        /// Hungry (hunger below 1.25): the <c>Hunger</c> back overlay, under Combat.
        /// </summary>
        Hunger = 0x80,
    }

    /// <summary>
    /// The original's flags (<c>FUN_140414dc0</c>). Its danger test reads a float of the character's AI that is not decoded (eating also waits while
    /// it is above 0); Meitou takes "in a fight" for it. Being eaten (<c>+0x279 &amp; 7</c>, <see cref="BodyStatus.BeingEaten"/>) also flashes, and
    /// sets <c>Eaten</c>.
    /// </summary>
    PortraitState PortraitStateOf(CharacterSnapshot m, Vector3 eye, Vector3 focus)
    {
        var s = PortraitState.None;
        if (m.Selected) s |= PortraitState.Selected;

        bool dead = m.Body is { Dead: true } or { Unconscious: true };
        bool carried = !m.CarriedBy.IsNone;
        bool eaten = m.Body is { BeingEaten: true };
        bool down = dead || carried;
        bool farAway = Vector3.DistanceSquared(m.Position, eye) >= 1e6f && Vector3.DistanceSquared(m.Position, focus) >= 1e5f;
        bool inDanger = !down && m.InCombat && m.Prison is null || eaten;
        if (farAway && inDanger) s |= PortraitState.Hurt;

        if (eaten) s |= PortraitState.Eaten;
        if (dead || carried || m.InBed) s |= PortraitState.Faded;
        if (m.CombatMode) s |= PortraitState.Combat;
        if (m.Body is { Hunger: < 1.25f }) s |= PortraitState.Hunger;
        if (m.Shackled) s |= PortraitState.Slave;
        if (m.Prison is not null) s |= PortraitState.Prison;
        return s;
    }

    /// <summary>
    /// The background's alpha (<c>FUN_140415150</c>): a flash starts at 1 and moves by 2 per second, down to 0 and back, while the member is
    /// <see cref="PortraitState.Hurt"/> and not <see cref="PortraitState.Faded"/>; otherwise 1.
    /// </summary>
    float PortraitFlash(CharacterId id, PortraitState s)
    {
        if ((s & PortraitState.Hurt) == 0 || (s & PortraitState.Faded) != 0)
        {
            PortraitFlashes.Remove(id);
            return 1;
        }

        long now = Stopwatch.GetTimestamp();
        if (!PortraitFlashes.TryGetValue(id, out long start)) PortraitFlashes[id] = start = now;

        double t = Stopwatch.GetElapsedTime(start, now).TotalSeconds;
        return (float)Math.Abs(1 - 2 * (t - Math.Floor(t)));
    }

    /// <summary>
    /// Drops the flashes of characters no longer on the squad bar.
    /// </summary>
    void PrunePortraitFlashes(IReadOnlyList<CharacterSnapshot> members)
    {
        if (PortraitFlashes.Count == 0) return;

        foreach (var id in PortraitFlashes.Keys.ToList())
        {
            if (!members.Any(m => m.Id == id)) PortraitFlashes.Remove(id);
        }
    }

    /// <summary>
    /// The back overlay a state shows: Combat, else Hunger; empty for none.
    /// </summary>
    static string BackOverlayName(PortraitState s) =>
        (s & PortraitState.Combat) != 0 ? "Combat" : (s & PortraitState.Hunger) != 0 ? "Hunger" : "";

    /// <summary>
    /// The front overlay a state shows: Eaten, else Prison, else Slave; empty for none.
    /// </summary>
    static string FrontOverlayName(PortraitState s) =>
        (s & PortraitState.Eaten) != 0 ? "Eaten" : (s & PortraitState.Prison) != 0 ? "Prison" : (s & PortraitState.Slave) != 0 ? "Slave" : "";

    /// <summary>
    /// An image of the cell's <c>Kenshi_PortraitImage</c> set at <paramref name="alpha"/>.
    /// </summary>
    static UiControl PortraitImage(string name, UiRect bounds, string group, string image, float alpha = 1)
    {
        var properties = new Dictionary<string, string> { ["ImageResource"] = "Kenshi_PortraitImage", ["ImageGroup"] = group, ["ImageName"] = image };
        if (alpha < 1) properties["Alpha"] = alpha.ToString(CultureInfo.InvariantCulture);
        return new(name, "ImageBox", bounds, "", "", "", true, properties, Vector4.Zero, Pickable: false);
    }

    #endregion
}