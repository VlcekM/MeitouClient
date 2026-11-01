using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Rendering;
using Meitou.Simulation;
using EngineKey = Meitou.Engine.Input.Key;

namespace Meitou.Game;

/// <summary>
/// The animation sandbox (<c>--sandbox [n]</c>, docs/simulation.md "Sandbox"): the real simulation and character renderer on a flat floor with no world, one character
/// of the player's (or two facing each other) and a debug panel with the speed and the published animation layers. Nothing here runs in the normal game.
/// </summary>
sealed partial class GameHost
{
    /// <summary>Half the side of the drawn floor, the grid spacing and the stronger line's, in world units (the terrain shader's grid).</summary>
    const float SandboxHalf = 500, SandboxMinor = 10, SandboxMajor = 100;
    /// <summary>The patrol runs along X between -200 and 200 (a 400-unit leg).</summary>
    const float PatrolHalf = 200;

    bool IsSandbox => g.Sandbox > 0;

    CharacterId sandboxHero = CharacterId.None, sandboxFoe = CharacterId.None;
    byte? sandboxMode;
    float sandboxSpeed;
    bool sandboxPatrol, sandboxFollow;   // follow off: the camera stays on the middle of the floor until moved (Y follows the pair)
    float patrolTarget;
    long patrolIssued = -1;
    SandboxAnimationPicker? sandboxPicker;   // made on the first draw (the library is loaded with the scene)
    float toggleX0, toggleY0, toggleX1, toggleY1;   // the panel's collapse button, as last drawn (pixels)

    /// <summary>A click on the panel's collapse button folds the panel to the button alone (a down arrow) or opens it again (<see cref="UserConfig.SandboxPanelCollapsed"/>).</summary>
    bool SandboxToggleClick(Vector2 at)
    {
        if (!IsSandbox || g.UiShowcase || at.X < toggleX0 || at.X > toggleX1 || at.Y < toggleY0 || at.Y > toggleY1) return false;
        config.SandboxPanelCollapsed = !config.SandboxPanelCollapsed;
        config.Save();
        return true;
    }

    /// <summary>The CHARACTER record the sandbox spawns: the one asked for, else the first member of the default new-game start's squad.</summary>
    string SandboxRecord(Meitou.Data.GameDatabase db)
    {
        if (g.SandboxCharacter is { } want)
            return FindCharacter(db, want)?.StringId ?? throw new ArgumentException($"--sandbox-character: no CHARACTER or ANIMAL_CHARACTER named '{want}'");
        var start = NewGameStart.Find(NewGameStart.LoadAll(db), NewGameStart.DefaultName);
        foreach (var link in start?.Squad ?? [])
        {
            if (db.Find(link.Id) is not { } record) continue;
            if (!SquadFactory.IsTemplate(record)) return link.Id;
            var plan = SquadFactory.Plan(db, SquadTemplate.From(record), 1, 0, 0, IWorldStates.None);
            if (plan.Members.FirstOrDefault(m => !m.IsAnimal) is { } member) return member.RecordId;
        }
        return "Greenlander";
    }

    /// <summary>The record a command line names: any record by id, else the CHARACTER (before the ANIMAL_CHARACTER) of that name, ignoring case.</summary>
    static Meitou.Data.GameRecord? FindCharacter(Meitou.Data.GameDatabase db, string want) =>
        db.Find(want) ?? db.Records.Values.Where(r => r.Type is FcsRecordType.CHARACTER or FcsRecordType.ANIMAL_CHARACTER).OrderBy(r => r.Type == FcsRecordType.ANIMAL_CHARACTER)
            .FirstOrDefault(r => string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>--sandbox-races</c>: every person race with the first CHARACTER record that lists it as its only race (records with a choice of races are skipped), by race name.</summary>
    static List<(string Race, string Record)> SandboxRaces(Meitou.Data.GameDatabase db) =>
        db.Records.Values.Where(r => r.Type == FcsRecordType.CHARACTER)
            .Select(r => (Id: r.StringId, Races: r.GetReferences("race").Select(x => x.TargetStringId).ToList()))
            .Where(x => x.Races.Count == 1 && db.Find(x.Races[0]) is not null)
            .GroupBy(x => x.Races[0]).Select(grp => (Race: db.Find(grp.Key)!.Name, Record: grp.First().Id))
            .OrderBy(x => x.Race, StringComparer.Ordinal).ToList();

    /// <summary>Spawns the hero (and the foe), selects the hero, applies the command-line start state; returns where the camera starts.</summary>
    Vector3 SandboxSpawn()
    {
        var world = session.World;
        string record = SandboxRecord(scene.Database!);
        float half = g.Sandbox >= 2 ? 15 : 0;
        sandboxHero = population!.SpawnSandbox(world, record, new Vector2(-half, 0), player: true, yaw: MathF.PI / 2);
        if (g.Sandbox >= 2) sandboxFoe = population.SpawnSandbox(world, record, new Vector2(half, 0), player: false, yaw: -MathF.PI / 2);
        // n above 2: the rest stand idle in rings round the pair (the player's faction, so they keep out of the fight), 15 units apart, a look each;
        // --sandbox-races: then one of every person race, in the rings after them.
        var extra = Enumerable.Repeat(record, Math.Max(0, g.Sandbox - 2)).ToList();
        if (g.SandboxRaces)
        {
            var races = SandboxRaces(scene.Database!);
            Console.WriteLine($"sandbox   {races.Count} races: {string.Join(", ", races.Select(x => x.Race))}");
            extra.AddRange(races.Select(x => x.Record));
        }
        for (int i = 0, ring = 1, placed = 0; i < extra.Count; i++, placed++)
        {
            int perRing = (int)(2 * MathF.PI * (25 + 15 * ring) / 15);
            if (placed == perRing) { ring++; placed = 0; perRing = (int)(2 * MathF.PI * (25 + 15 * ring) / 15); }
            float r = 25 + 15 * ring, angle = 2 * MathF.PI * placed / perRing;
            population.SpawnSandbox(world, extra[i], new Vector2(r * MathF.Cos(angle), r * MathF.Sin(angle)), player: true, yaw: angle + MathF.PI, variant: i + 1);
        }
        playerSquad = world.Squads.Find(world.Player.Squad);
        world.Commands.Enqueue(new SelectCommand([sandboxHero]) { Tick = world.Tick });
        bool patrol = false, fight = false;
        // --sandbox-mode: comma-separated walk, run or free, a fixed speed in units/s, patrol and fight.
        foreach (string token in (g.SandboxMode ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed)) sandboxSpeed = speed;
            else
                switch (token.ToLowerInvariant())
                {
                    case "walk": sandboxMode = SpeedMode.Walk; break;
                    case "run": sandboxMode = SpeedMode.Run; break;
                    case "free": sandboxMode = SpeedMode.Free; break;
                    case "patrol": patrol = true; break;
                    case "fight": fight = true; break;   // the G key at the start (pictures of a fight)
                    default: throw new ArgumentException("--sandbox-mode: walk, run, free, a speed in units/s, patrol or fight (comma-separated)");
                }
        }
        SandboxSendMovement();
        if (patrol) SandboxTogglePatrol();
        if (fight) SandboxKey(EngineKey.G);
        session.Ticked += _ => SandboxTick();
        Console.WriteLine($"sandbox   {record}{(g.Sandbox >= 2 ? ", two facing each other 30 units apart (G fights)" : "")}{(g.Sandbox > 2 ? $", {g.Sandbox - 2} more round them" : "")}; flat floor {2 * SandboxHalf:0} x {2 * SandboxHalf:0}, grid {SandboxMinor:0} / {SandboxMajor:0}");
        return new Vector3(0, scene.FlatHeight, 0);
    }

    int crowd;

    /// <summary>
    /// The game's <c>--crowd n</c> (a test of the character renderer and the simulation at scale): n idle characters of the sandbox's record, each with a look of its own, of the
    /// player's faction but in squads of their own, in rings 15 units apart round <paramref name="around"/> on walkable ground (a spot that is not is skipped).
    /// </summary>
    void SpawnCrowd(Vector3 around, int count)
    {
        var world = session.World;
        string record = SandboxRecord(scene.Database!);
        int placed = 0;
        for (int ring = 1; placed < count && ring < 200; ring++)
        {
            float r = 15 + 15 * ring;
            int perRing = (int)(2 * MathF.PI * r / 15);
            for (int k = 0; k < perRing && placed < count; k++)
            {
                float angle = 2 * MathF.PI * k / perRing;
                var at = new Vector2(around.X + r * MathF.Cos(angle), around.Z + r * MathF.Sin(angle));
                if (!world.Walkability.IsWalkable(at.X, at.Y)) continue;
                population!.SpawnSandbox(world, record, at, player: true, yaw: angle + MathF.PI, variant: ++placed);
            }
        }
        Console.WriteLine($"crowd     {placed} idle characters of {record} round {around.X:0}, {around.Z:0}");
    }

    void SandboxSendMovement()
    {
        if (sandboxHero.IsNone) return;
        session.World.Commands.Enqueue(new SandboxMovement(sandboxHero, sandboxMode, sandboxSpeed) { Tick = session.World.Tick });
    }

    void SandboxTogglePatrol()
    {
        sandboxPatrol = !sandboxPatrol;
        if (!sandboxPatrol)
        {
            session.World.Commands.Enqueue(new StopCommand([sandboxHero]) { Tick = session.World.Tick });
            return;
        }
        float x = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == sandboxHero)?.Position.X ?? 0;
        patrolTarget = x > 0 ? -PatrolHalf : PatrolHalf;
        patrolIssued = -1;
    }

    /// <summary>Keeps the patrol going: the next leg is ordered when the hero has arrived at the end of the last one (the hero stops at once on arrival, as in the game).</summary>
    void SandboxTick()
    {
        if (!sandboxPatrol || sandboxHero.IsNone) return;
        var hero = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == sandboxHero);
        if (hero is null) return;
        long tick = session.World.Tick;
        bool arrived = patrolIssued >= 0 && Math.Abs(hero.Position.X - patrolTarget) < 12;
        if (arrived) patrolTarget = -patrolTarget;
        if (patrolIssued < 0 || arrived || tick - patrolIssued > 30 * 30)
        {
            session.World.Commands.Enqueue(new MoveOrder([sandboxHero], new Vector3(patrolTarget, 0, 0)) { Tick = tick });
            patrolIssued = tick;
        }
    }

    /// <summary>The sandbox's keys (the game's own keys still work).</summary>
    void SandboxKey(EngineKey key)
    {
        if (!IsSandbox) return;
        switch (key)
        {
            case EngineKey.Z or EngineKey.X or EngineKey.C:
                sandboxMode = key == EngineKey.Z ? SpeedMode.Walk : key == EngineKey.X ? SpeedMode.Run : SpeedMode.Free;
                sandboxSpeed = 0;
                SandboxSendMovement();
                break;
            case EngineKey.LeftBracket or EngineKey.RightBracket:
                sandboxSpeed = Math.Clamp(MathF.Round(sandboxSpeed / 5) * 5 + (key == EngineKey.RightBracket ? 5 : -5), 0, 150);
                SandboxSendMovement();
                break;
            case EngineKey.P: SandboxTogglePatrol(); break;
            case EngineKey.Y: sandboxFollow = !sandboxFollow; break;
            case EngineKey.G when !sandboxFoe.IsNone:
                session.World.Commands.Enqueue(new Meitou.Simulation.Combat.AttackOrder([sandboxHero], sandboxFoe) { Tick = session.World.Tick });
                session.World.Commands.Enqueue(new Meitou.Simulation.Combat.AttackOrder([sandboxFoe], sandboxHero) { Tick = session.World.Tick });
                break;
        }
    }

    /// <summary>Keeps the camera on the hero (the middle of the pair while there is a foe), when following. Only the pivot moves: a cut every frame
    /// (<see cref="CameraRig.Place"/>) would draw the rotation and zoom at the tick rate instead of interpolated.</summary>
    /// <summary>The strategy camera on the character the player follows (<see cref="PlayerInterface.Follow"/>), as the sandbox's follow does; a gone character is let go.</summary>
    void FollowCamera()
    {
        if (player.Follow is not { } id || session.Camera.IsFree) return;
        if (session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } followed) { player.Follow = null; return; }
        session.Camera.MovePivot(new Vector2(followed.Position.X, followed.Position.Z));
        camera.Target = DrawnPosition(followed);
    }

    void SandboxFollowCamera()
    {
        if (!IsSandbox || !sandboxFollow || session.Camera.IsFree) return;
        var characters = session.CurrentSnapshot.Characters;
        if (characters.FirstOrDefault(c => c.Id == sandboxHero) is not { } hero) return;
        var drawn = DrawnPosition(hero);
        var latest = hero.Position;
        if (!sandboxFoe.IsNone && characters.FirstOrDefault(c => c.Id == sandboxFoe) is { } foe)
        {
            drawn = (drawn + DrawnPosition(foe)) / 2;
            latest = (latest + foe.Position) / 2;
        }
        session.Camera.MovePivot(new Vector2(latest.X, latest.Z));
        camera.Target = drawn;
    }

    /// <summary>The debug panel, over the finished picture and the HUD (the floor's grid is the terrain's: <see cref="WorldRenderOptions.Grid"/>).</summary>
    void DrawSandbox(DebugOverlay o, int width, int height)
    {
        float buttonW = o.CharWidth + 12, buttonH = o.LineHeight + 4;
        void Toggle(float x1, float y0, string arrow)
        {
            (toggleX0, toggleY0, toggleX1, toggleY1) = (x1 - buttonW, y0, x1, y0 + buttonH);
            o.Rect(toggleX0, toggleY0, toggleX1, toggleY1, new Vector4(0.2f, 0.2f, 0.22f, 1));
            o.Text(arrow, toggleX0 + 6, y0 + 2, DebugOverlay.TextColour);
        }
        if (config.SandboxPanelCollapsed)
        {
            Toggle(width - 10, 10, "v");
            o.Flush(width, height);
            return;
        }
        var white = DebugOverlay.TextColour;
        var lines = new List<(string Text, Vector4 Colour)>();
        void Add(string text, Vector4? colour = null) => lines.Add((text, colour ?? white));
        var yellow = new Vector4(1f, 0.9f, 0.4f, 1);
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        AddCharacter("Hero", sandboxHero, Add, yellow, detailed: true);
        if (!sandboxFoe.IsNone) { Add(""); AddCharacter("Foe", sandboxFoe, Add, yellow, detailed: false); }
        Add("");
        Add($"patrol {(sandboxPatrol ? $"ON: x {-PatrolHalf:0} <-> {PatrolHalf:0} (to {patrolTarget:0})" : "off")}   follow {(sandboxFollow ? "on" : "off")}", dim);
        Add("Z walk  X run  C free  [ ] fixed speed +-5  P patrol  G fight  Y follow  R stop", dim);
        sandboxPicker ??= animationLibrary is { } lib ? new SandboxAnimationPicker(lib, animationLengths) : null;
        if (sandboxPicker is not null) { Add(""); if (sandboxPicker.Status is { Length: > 0 } status) Add(status, dim); }   // the button on the blank row
        float pad = 8, textW = lines.Max(l => l.Text.Length) * o.CharWidth, w = textW + 2 * pad + buttonW, h = lines.Count * o.LineHeight + 2 * pad;
        float x0 = width - w - 10, y0 = 10;   // top right: the HUD holds the bottom and the top left, the character stands in the middle
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        Toggle(x0 + w, y0, "^");
        for (int i = 0; i < lines.Count; i++) o.Text(lines[i].Text, x0 + pad, y0 + pad + i * o.LineHeight, lines[i].Colour);
        sandboxPicker?.Draw(o, x0 + pad, y0 + pad + (lines.Count - (sandboxPicker.Status.Length > 0 ? 2 : 1)) * o.LineHeight - 2, textW);
        o.Flush(width, height);
    }

    void AddCharacter(string label, CharacterId id, Action<string, Vector4?> add, Vector4 heading, bool detailed)
    {
        var table = session.World.Characters;
        var snapshot = session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id);
        if (snapshot is null || !table.IsAlive(id)) { add($"{label}: gone", null); return; }
        ref readonly var hot = ref table.Previous[id.Slot];
        var cold = table.Cold(id.Slot)!;
        float v = MathF.Sqrt(hot.Velocity.X * hot.Velocity.X + hot.Velocity.Z * hot.Velocity.Z), measured = 0;
        if (session.PreviousSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } before)
            measured = Vector3.Distance(new Vector3(before.Position.X, 0, before.Position.Z), new Vector3(snapshot.Position.X, 0, snapshot.Position.Z)) / (float)session.Simulation.TickSeconds;
        byte mode = cold.ModeOverride ?? hot.Mode;
        string modeText = mode switch { SpeedMode.Walk => $"WALK (cap {hot.WalkSpeed:0.#})", SpeedMode.Run => $"RUN (cap {SpeedMode.RunCap:0})", SpeedMode.Group => "GROUP (squad's slowest)", _ => "FREE (no cap)" };
        add($"{label}: {snapshot.Name}   {(CharacterTask)hot.Task}", heading);
        add($"v {v,6:0.00} u/s   measured {measured,6:0.00}   speed chain {hot.MaxSpeed:0.0}", null);
        if (!detailed) { AddLayers(snapshot, cold, add); return; }
        add($"mode {modeText}   fixed speed {(cold.SpeedOverride > 0 ? cold.SpeedOverride.ToString("0", CultureInfo.InvariantCulture) : "off")}", null);
        add($"pos {snapshot.Position.X,7:0.0}, {snapshot.Position.Z,7:0.0}   yaw {snapshot.Yaw * 180 / MathF.PI,6:0}", null);
        AddLayers(snapshot, cold, add);
    }

    void AddLayers(CharacterSnapshot snapshot, CharacterCold cold, Action<string, Vector4?> add)
    {
        if (cold.Animation is { } a) add($"synch phase {a.Phase:0.000}   rate F {a.Rate:0.000}   state {snapshot.Animations.Count} layer(s)", null);
        foreach (var layer in snapshot.Animations)
        {
            // Published by record name; the length is the clip's (a technique's record name differs from its clip).
            // An animal publishes its clips by clip name (its techniques by record name) and has its own skeleton's lengths.
            bool animal = cold.Fighter is { IsAnimal: true };
            string clip = animationLibrary is { } lib && lib.IndexOfAny(layer.Name) is var at and >= 0 && (!animal || lib.Definitions[at].Technique) ? lib.Definitions[at].Clip : layer.Name;
            float length = animationLengths?.For(animal ? cold.Race?.StringId : null).Of(clip) ?? 1;
            add($"  {layer.Name,-28} t {layer.Time,5:0.00}/{length,5:0.00}  w {layer.Weight:0.00}", null);
        }
    }
}
