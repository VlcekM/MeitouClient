using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Game.Editor;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Terrain;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.Scenarios;

/// <summary>
/// The animation sandbox (<c>--sandbox [n]</c>, docs/simulation.md "Sandbox"): the real simulation and character renderer on a flat floor with no
/// world, one character of the player's (or two facing each other) and a debug panel with the speed and the published animation layers. Nothing here
/// runs in the normal game.
/// </summary>
sealed class SandboxScenario(GameHost host)
{
    /// <summary>
    /// Half the side of the drawn floor, the grid spacing and the stronger line's, in world units (the terrain shader's grid).
    /// </summary>
    const float SandboxHalf = 500, SandboxMinor = 10, SandboxMajor = 100;
    /// <summary>
    /// The patrol runs along X between -200 and 200 (a 400-unit leg).
    /// </summary>
    const float PatrolHalf = 200;

    /// <summary>
    /// Whether this run is a <c>--sandbox</c>.
    /// </summary>
    internal bool IsSandbox => host.GameOptions.Sandbox > 0;

    /// <summary>
    /// The sandbox's hero and, with two or more characters, the foe facing it; none when not spawned.
    /// </summary>
    internal CharacterId SandboxHero = CharacterId.None, SandboxFoe = CharacterId.None;
    /// <summary>
    /// The hero's forced speed mode (walk, run, free; <c>--sandbox-mode</c>); null for the hero's own.
    /// </summary>
    byte? _sandboxMode;
    /// <summary>
    /// The hero's fixed speed in units/s (<c>--sandbox-mode</c>); 0 for none.
    /// </summary>
    float _sandboxSpeed;
    /// <summary>
    /// Whether the hero patrols between ±200 along X, and whether the camera follows the pair (off: it stays on the middle of the floor until moved;
    /// Y follows the pair).
    /// </summary>
    bool _sandboxPatrol, _sandboxFollow;
    /// <summary>
    /// The X the patrolling hero walks to next.
    /// </summary>
    float _patrolTarget;
    /// <summary>
    /// The tick the patrol's last walk order was given at; -1 to give one.
    /// </summary>
    long _patrolIssued = -1;
    /// <summary>
    /// The animation drop-down, made on the first draw (the library is loaded with the scene).
    /// </summary>
    internal SandboxAnimationPicker? SandboxPicker;
    /// <summary>
    /// The panel's collapse button, as last drawn (pixels).
    /// </summary>
    float _toggleX0, _toggleY0, _toggleX1, _toggleY1;

    /// <summary>
    /// A click on the panel's collapse button folds the panel to the button alone (a down arrow) or opens it again (<see
    /// cref="UserConfig.SandboxPanelCollapsed"/>).
    /// </summary>
    internal bool SandboxToggleClick(Vector2 at)
    {
        if (!IsSandbox || host.GameOptions.UiShowcase || at.X < _toggleX0 || at.X > _toggleX1 || at.Y < _toggleY0 || at.Y > _toggleY1) return false;
        host.Config.SandboxPanelCollapsed = !host.Config.SandboxPanelCollapsed;
        host.Config.Save();
        return true;
    }

    /// <summary>
    /// The CHARACTER record the sandbox spawns: the one asked for, else the first member of the default new-game start's squad.
    /// </summary>
    internal string SandboxRecord(Data.GameDatabase db)
    {
        if (host.GameOptions.SandboxCharacter is { } want)
        {
            return FindCharacter(db, want)?.StringId
                ?? throw new ArgumentException($"--sandbox-character: no CHARACTER or ANIMAL_CHARACTER named '{want}'");
        }

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

    /// <summary>
    /// The record a command line names: any record by id, else the CHARACTER (before the ANIMAL_CHARACTER) of that name, ignoring case.
    /// </summary>
    internal static Data.GameRecord? FindCharacter(Data.GameDatabase db, string want) =>
        db.Find(want) ?? db.Records.Values
            .Where(r => r.Type is FcsRecordType.Character or FcsRecordType.AnimalCharacter)
            .OrderBy(r => r.Type == FcsRecordType.AnimalCharacter)
            .FirstOrDefault(r => string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>--sandbox-races</c>: every person race with the first CHARACTER record that lists it as its only race (records with a choice of races are
    /// skipped), by race name.
    /// </summary>
    static List<(string Race, string Record)> SandboxRaces(Data.GameDatabase db) =>
        db.Records.Values.Where(r => r.Type == FcsRecordType.Character)
            .Select(r => (Id: r.StringId, Races: r.GetReferences("race").Select(x => x.TargetStringId).ToList()))
            .Where(x => x.Races.Count == 1 && db.Find(x.Races[0]) is not null)
            .GroupBy(x => x.Races[0]).Select(grp => (Race: db.Find(grp.Key)!.Name, Record: grp.First().Id))
            .OrderBy(x => x.Race, StringComparer.Ordinal).ToList();

    /// <summary><c>--sandbox-animals</c>: the first ANIMAL_CHARACTER record of every race animals use, by name.</summary>
    static List<(string Race, string Record)> SandboxAnimals(Meitou.Data.GameDatabase db) =>
        db.Records.Values.Where(r => r.Type == FcsRecordType.AnimalCharacter)
            .Select(r => (Id: r.StringId, Name: r.Name, Race: Meitou.Data.Characters.CharacterAppearance.AnimalRaceId(r)))
            .Where(x => x.Race is not null)
            .GroupBy(x => x.Race!).Select(grp => (Race: db.Find(grp.Key)?.Name ?? grp.Key, Record: grp.First().Id))
            .OrderBy(x => x.Race, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Spawns the hero (and the foe), selects the hero, applies the command-line start state; returns where the camera starts.
    /// </summary>
    internal Vector3 SandboxSpawn()
    {
        var world = host.Session.World;
        string record = SandboxRecord(host.Scene.Database!);
        float half = host.GameOptions.Sandbox >= 2 ? 15 : 0;
        SandboxHero = host.Population!.SpawnSandbox(world, record, new Vector2(-half, 0), player: true, yaw: MathF.PI / 2);
        if (host.GameOptions.Sandbox >= 2)
            SandboxFoe = host.Population.SpawnSandbox(world, record, new Vector2(half, 0), player: false, yaw: -MathF.PI / 2);

        // n above 2: the rest stand idle in rings round the pair (the player's faction, so they keep out of the fight), 15 units apart, a look each;
        // --sandbox-races: then one of every person race, in the rings after them.
        var extra = Enumerable.Repeat(record, Math.Max(0, host.GameOptions.Sandbox - 2)).ToList();
        if (host.GameOptions.SandboxRaces)
        {
            var races = SandboxRaces(host.Scene.Database!);
            Console.WriteLine($"sandbox   {races.Count} races: {string.Join(", ", races.Select(x => x.Race))}");
            extra.AddRange(races.Select(x => x.Record));
        }
        if (host.GameOptions.SandboxAnimals)
        {
            var animals = SandboxAnimals(host.Scene.Database!);
            Console.WriteLine($"sandbox   {animals.Count} animal races: {string.Join(", ", animals.Select(x => $"{x.Race} ({x.Record})"))}");
            extra.AddRange(animals.Select(x => x.Record));
        }
        for (int i = 0, ring = 1, placed = 0; i < extra.Count; i++, placed++)
        {
            int perRing = (int)(2 * MathF.PI * (25 + 15 * ring) / 15);
            if (placed == perRing)
            {
                ring++;
                placed = 0;
                perRing = (int)(2 * MathF.PI * (25 + 15 * ring) / 15);
            }

            float r = 25 + 15 * ring, angle = 2 * MathF.PI * placed / perRing;
            host.Population.SpawnSandbox(world, extra[i], new Vector2(r * MathF.Cos(angle), r * MathF.Sin(angle)), player: true,
                yaw: angle + MathF.PI, variant: i + 1);
        }
        host.PlayerSquad = world.Squads.Find(world.Player.Squad);
        world.Commands.Enqueue(new SelectCommand([SandboxHero]) { Tick = world.Tick });
        bool patrol = false, fight = false;
        // --sandbox-mode: comma-separated walk, run or free, a fixed speed in units/s, patrol and fight.
        var tokens = (host.GameOptions.SandboxMode ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string token in tokens)
        {
            if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float speed)) _sandboxSpeed = speed;
            else
            {
                switch (token.ToLowerInvariant())
                {
                    case "walk":
                        _sandboxMode = SpeedMode.Walk;
                        break;
                    case "run":
                        _sandboxMode = SpeedMode.Run;
                        break;
                    case "free":
                        _sandboxMode = SpeedMode.Free;
                        break;
                    case "patrol":
                        patrol = true;
                        break;
                    case "fight":
                        fight = true;   // the G key at the start (pictures of a fight)
                        break;
                    default:
                        throw new ArgumentException("--sandbox-mode: walk, run, free, a speed in units/s, patrol or fight (comma-separated)");
                }
            }
        }
        SandboxSendMovement();
        if (patrol) SandboxTogglePatrol();
        if (fight) SandboxKey(EngineKey.G);
        host.Session.Ticked += _ => SandboxTick();
        string pair = host.GameOptions.Sandbox >= 2 ? ", two facing each other 30 units apart (G fights)" : "";
        string more = host.GameOptions.Sandbox > 2 ? $", {host.GameOptions.Sandbox - 2} more round them" : "";
        Console.WriteLine($"sandbox   {record}{pair}{more}; flat floor {2 * SandboxHalf:0} x {2 * SandboxHalf:0}, "
            + $"grid {SandboxMinor:0} / {SandboxMajor:0}");
        return new Vector3(0, host.Scene.FlatHeight, 0);
    }

    /// <summary>
    /// The <c>--crowd</c> count, taken from the world options at boot: idle characters spawned round the start of a game that is not the sandbox.
    /// </summary>
    internal int Crowd;

    /// <summary>
    /// The game's <c>--crowd n</c> (a test of the character renderer and the simulation at scale): n idle characters of the sandbox's record, each
    /// with a look of its own, of the player's faction but in squads of their own, in rings 15 units apart round <paramref name="around"/> on
    /// walkable ground (a spot that is not is skipped).
    /// </summary>
    internal void SpawnCrowd(Vector3 around, int count)
    {
        var world = host.Session.World;
        string record = SandboxRecord(host.Scene.Database!);
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
                host.Population!.SpawnSandbox(world, record, at, player: true, yaw: angle + MathF.PI, variant: ++placed);
            }
        }
        Console.WriteLine($"crowd     {placed} idle characters of {record} round {around.X:0}, {around.Z:0}");
    }

    /// <summary>
    /// Sends the hero's forced speed mode and speed to the simulation.
    /// </summary>
    void SandboxSendMovement()
    {
        if (SandboxHero.IsNone) return;
        host.Session.World.Commands.Enqueue(new SandboxMovement(SandboxHero, _sandboxMode, _sandboxSpeed) { Tick = host.Session.World.Tick });
    }

    /// <summary>
    /// Starts the hero walking to the far end of the patrol, or stops it.
    /// </summary>
    void SandboxTogglePatrol()
    {
        _sandboxPatrol = !_sandboxPatrol;
        if (!_sandboxPatrol)
        {
            host.Session.World.Commands.Enqueue(new StopCommand([SandboxHero]) { Tick = host.Session.World.Tick });
            return;
        }
        float x = host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == SandboxHero)?.Position.X ?? 0;
        _patrolTarget = x > 0 ? -PatrolHalf : PatrolHalf;
        _patrolIssued = -1;
    }

    /// <summary>
    /// Keeps the patrol going: the next leg is ordered when the hero has arrived at the end of the last one (the hero stops at once on arrival, as in
    /// the game).
    /// </summary>
    void SandboxTick()
    {
        if (!_sandboxPatrol || SandboxHero.IsNone) return;
        var hero = host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == SandboxHero);
        if (hero is null) return;
        long tick = host.Session.World.Tick;
        bool arrived = _patrolIssued >= 0 && Math.Abs(hero.Position.X - _patrolTarget) < 12;
        if (arrived) _patrolTarget = -_patrolTarget;
        if (_patrolIssued < 0 || arrived || tick - _patrolIssued > 30 * 30)
        {
            host.Session.World.Commands.Enqueue(new MoveOrder([SandboxHero], new Vector3(_patrolTarget, 0, 0)) { Tick = tick });
            _patrolIssued = tick;
        }
    }

    /// <summary>
    /// The sandbox's keys (the game's own keys still work).
    /// </summary>
    internal void SandboxKey(EngineKey key)
    {
        if (!IsSandbox) return;
        switch (key)
        {
            case EngineKey.Z or EngineKey.X or EngineKey.C:
                _sandboxMode = key == EngineKey.Z ? SpeedMode.Walk : key == EngineKey.X ? SpeedMode.Run : SpeedMode.Free;
                _sandboxSpeed = 0;
                SandboxSendMovement();
                break;
            case EngineKey.LeftBracket or EngineKey.RightBracket:
                _sandboxSpeed = Math.Clamp(MathF.Round(_sandboxSpeed / 5) * 5 + (key == EngineKey.RightBracket ? 5 : -5), 0, 150);
                SandboxSendMovement();
                break;
            case EngineKey.P:
                SandboxTogglePatrol();
                break;
            case EngineKey.Y:
                _sandboxFollow = !_sandboxFollow;
                break;
            case EngineKey.G when !SandboxFoe.IsNone:
                host.Session.World.Commands.Enqueue(new Simulation.Combat.AttackOrder([SandboxHero], SandboxFoe) { Tick = host.Session.World.Tick });
                host.Session.World.Commands.Enqueue(new Simulation.Combat.AttackOrder([SandboxFoe], SandboxHero) { Tick = host.Session.World.Tick });
                break;
        }
    }

    /// <summary>
    /// The strategy camera on the character the player follows (<see cref="PlayerInterface.Follow"/>), the target at its hips (the game's position
    /// + <see cref="KenshiCamera.TrackHeight"/>); a gone character is let go. A carried character is tracked by its carrier until put down: the body
    /// sways on the carrier's shoulder with every step, which shook the view.
    /// </summary>
    internal void FollowCamera()
    {
        if (host.Player.Follow is not { } id || host.Session.Camera.IsFree) return;
        var characters = host.Session.CurrentSnapshot.Characters;
        if (characters.FirstOrDefault(c => c.Id == id) is not { } followed)
        {
            host.Player.Follow = null;
            return;
        }
        var tracked = host.Tracked(followed);
        host.Session.Camera.MovePivot(new Vector2(tracked.Position.X, tracked.Position.Z));
        host.Camera.Target = host.DrawnPosition(tracked) + new Vector3(0, KenshiCamera.TrackHeight, 0);
    }

    /// <summary>
    /// Keeps the camera on the hero and the foe (their drawn places; the middle of the pair while there is a foe) while the camera follows and is
    /// not free. Only the pivot moves: a cut every frame (<see cref="CameraRig.Place"/>) would draw the rotation and zoom at the tick rate instead
    /// of interpolated.
    /// </summary>
    internal void SandboxFollowCamera()
    {
        if (!IsSandbox || !_sandboxFollow || host.Session.Camera.IsFree) return;
        var characters = host.Session.CurrentSnapshot.Characters;
        if (characters.FirstOrDefault(c => c.Id == SandboxHero) is not { } hero) return;
        var drawn = host.DrawnPosition(hero);
        var latest = hero.Position;
        if (!SandboxFoe.IsNone && characters.FirstOrDefault(c => c.Id == SandboxFoe) is { } foe)
        {
            drawn = (drawn + host.DrawnPosition(foe)) / 2;
            latest = (latest + foe.Position) / 2;
        }
        host.Session.Camera.MovePivot(new Vector2(latest.X, latest.Z));
        host.Camera.Target = drawn;
    }

    /// <summary>
    /// The debug panel, over the finished picture and the HUD (the floor's grid is the terrain's: <see cref="WorldRenderOptions.Grid"/>).
    /// </summary>
    internal void DrawSandbox(DebugOverlay o, int width, int height)
    {
        float buttonW = o.CharWidth + 12, buttonH = o.LineHeight + 4;
        void Toggle(float x1, float y0, string arrow)
        {
            (_toggleX0, _toggleY0, _toggleX1, _toggleY1) = (x1 - buttonW, y0, x1, y0 + buttonH);
            o.Rect(_toggleX0, _toggleY0, _toggleX1, _toggleY1, new Vector4(0.2f, 0.2f, 0.22f, 1));
            o.Text(arrow, _toggleX0 + 6, y0 + 2, DebugOverlay.TextColour);
        }
        if (host.Config.SandboxPanelCollapsed)
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
        AddCharacter("Hero", SandboxHero, Add, yellow, detailed: true);
        if (!SandboxFoe.IsNone)
        {
            Add("");
            AddCharacter("Foe", SandboxFoe, Add, yellow, detailed: false);
        }
        Add("");
        Add($"patrol {(_sandboxPatrol ? $"ON: x {-PatrolHalf:0} <-> {PatrolHalf:0} (to {_patrolTarget:0})" : "off")}   "
            + $"follow {(_sandboxFollow ? "on" : "off")}", dim);
        Add("Z walk  X run  C free  [ ] fixed speed +-5  P patrol  G fight  Y follow  R stop", dim);
        SandboxPicker ??= host.AnimationLibrary is { } lib ? new SandboxAnimationPicker(lib, host.AnimationLengths) : null;
        if (SandboxPicker is not null)   // the button on the blank row
        {
            Add("");
            if (SandboxPicker.Status is { Length: > 0 } status) Add(status, dim);
        }
        float pad = 8, textW = lines.Max(l => l.Text.Length) * o.CharWidth, w = textW + 2 * pad + buttonW, h = lines.Count * o.LineHeight + 2 * pad;
        float x0 = width - w - 10, y0 = 10;   // top right: the HUD holds the bottom and the top left, the character stands in the middle
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        Toggle(x0 + w, y0, "^");
        for (int i = 0; i < lines.Count; i++) o.Text(lines[i].Text, x0 + pad, y0 + pad + i * o.LineHeight, lines[i].Colour);
        SandboxPicker?.Draw(o, x0 + pad, y0 + pad + (lines.Count - (SandboxPicker.Status.Length > 0 ? 2 : 1)) * o.LineHeight - 2, textW);
        o.Flush(width, height);
    }

    /// <summary>
    /// One character's lines on the sandbox panel: its speed (set and measured), facing, state and animation layers; "gone" when it no longer lives.
    /// </summary>
    void AddCharacter(string label, CharacterId id, Action<string, Vector4?> add, Vector4 heading, bool detailed)
    {
        var table = host.Session.World.Characters;
        var snapshot = host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id);
        if (snapshot is null || !table.IsAlive(id))
        {
            add($"{label}: gone", null);
            return;
        }
        ref readonly var hot = ref table.Previous[id.Slot];
        var cold = table.Cold(id.Slot)!;
        float v = MathF.Sqrt(hot.Velocity.X * hot.Velocity.X + hot.Velocity.Z * hot.Velocity.Z), measured = 0;
        if (host.Session.PreviousSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } before)
        {
            measured = Vector3.Distance(new Vector3(before.Position.X, 0, before.Position.Z), new Vector3(snapshot.Position.X, 0, snapshot.Position.Z))
                / (float)host.Session.Simulation.TickSeconds;
        }
        byte mode = cold.ModeOverride ?? hot.Mode;
        string modeText = mode switch
        {
            SpeedMode.Walk => $"WALK (cap {hot.WalkSpeed:0.#})",
            SpeedMode.Run => $"RUN (cap {SpeedMode.RunCap:0})",
            SpeedMode.Group => "GROUP (squad's slowest)",
            _ => "FREE (no cap)",
        };
        add($"{label}: {snapshot.Name}   {(CharacterTask)hot.Task}", heading);
        add($"v {v,6:0.00} u/s   measured {measured,6:0.00}   speed chain {hot.MaxSpeed:0.0}", null);
        if (!detailed)
        {
            AddLayers(snapshot, cold, add);
            return;
        }

        add($"mode {modeText}   fixed speed {(cold.SpeedOverride > 0 ? cold.SpeedOverride.ToString("0", CultureInfo.InvariantCulture) : "off")}", null);
        add($"pos {snapshot.Position.X,7:0.0}, {snapshot.Position.Z,7:0.0}   yaw {snapshot.Yaw * 180 / MathF.PI,6:0}", null);
        AddLayers(snapshot, cold, add);
    }

    /// <summary>
    /// A character's animation layers on the panel: the synch phase and rate, then each layer's clip, time, length and weight.
    /// </summary>
    void AddLayers(CharacterSnapshot snapshot, CharacterCold cold, Action<string, Vector4?> add)
    {
        if (cold.Animation is { } a) add($"synch phase {a.Phase:0.000}   rate F {a.Rate:0.000}   state {snapshot.Animations.Count} layer(s)", null);
        foreach (var layer in snapshot.Animations)
        {
            // Published by record name; the length is the clip's (a technique's record name differs from its clip).
            // An animal publishes its clips by clip name (its techniques by record name) and has its own skeleton's lengths.
            bool animal = cold.Fighter is { IsAnimal: true };
            var library = host.AnimationLibrary;
            string clip = library is { } lib && lib.IndexOfAny(layer.Name) is var at and >= 0 && (!animal || lib.Definitions[at].Technique)
                ? lib.Definitions[at].Clip : layer.Name;
            float length = host.AnimationLengths?.For(animal ? cold.Race?.StringId : null).Of(clip) ?? 1;
            add($"  {layer.Name,-28} t {layer.Time,5:0.00}/{length,5:0.00}  w {layer.Weight:0.00}", null);
        }
    }
}