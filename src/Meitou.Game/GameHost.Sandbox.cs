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
    /// <summary>Half the side of the drawn floor, the grid spacing and the stronger line's, in world units.</summary>
    const float SandboxHalf = 500, SandboxMinor = 10, SandboxMajor = 100;
    /// <summary>The patrol runs along X between -200 and 200 (a 400-unit leg).</summary>
    const float PatrolHalf = 200;

    bool IsSandbox => g.Sandbox > 0;

    CharacterId sandboxHero = CharacterId.None, sandboxFoe = CharacterId.None;
    byte? sandboxMode;
    float sandboxSpeed;
    bool sandboxPatrol, sandboxFollow = true;
    float patrolTarget;
    long patrolIssued = -1;

    /// <summary>The CHARACTER record the sandbox spawns: the one asked for, else the first member of the default new-game start's squad.</summary>
    string SandboxRecord(Meitou.Data.GameDatabase db)
    {
        if (g.SandboxCharacter is { } want)
            return db.Find(want)?.StringId
                ?? db.OfType(FcsRecordType.CHARACTER).FirstOrDefault(r => string.Equals(r.Name, want, StringComparison.OrdinalIgnoreCase))?.StringId
                ?? throw new ArgumentException($"--sandbox-character: no CHARACTER named '{want}'");
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

    /// <summary>Spawns the hero (and the foe), selects the hero, applies the command-line start state; returns where the camera starts.</summary>
    Vector3 SandboxSpawn()
    {
        var world = session.World;
        string record = SandboxRecord(scene.Database!);
        float half = g.Sandbox == 2 ? 15 : 0;
        sandboxHero = population!.SpawnSandbox(world, record, new Vector2(-half, 0), player: true, yaw: MathF.PI / 2);
        if (g.Sandbox == 2) sandboxFoe = population.SpawnSandbox(world, record, new Vector2(half, 0), player: false, yaw: -MathF.PI / 2);
        playerSquad = world.Squads.Find(world.Player.Squad);
        world.Commands.Enqueue(new SelectCommand([sandboxHero]) { Tick = world.Tick });
        if (g.SandboxMode is { } modeName)
            sandboxMode = modeName.ToLowerInvariant() switch { "walk" => SpeedMode.Walk, "run" => SpeedMode.Run, "free" => SpeedMode.Free, _ => throw new ArgumentException("--sandbox-mode: walk, run or free") };
        sandboxSpeed = g.SandboxSpeed;
        SandboxSendMovement();
        if (g.SandboxPatrol) SandboxTogglePatrol();
        session.Ticked += _ => SandboxTick();
        Console.WriteLine($"sandbox   {record}{(g.Sandbox == 2 ? ", two facing each other 30 units apart (G fights)" : "")}; flat floor {2 * SandboxHalf:0} x {2 * SandboxHalf:0}, grid {SandboxMinor:0} / {SandboxMajor:0}");
        return new Vector3(0, scene.FlatHeight, 0);
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

    /// <summary>Keeps the camera on the hero (the middle of the pair while there is a foe), when following.</summary>
    void SandboxFollowCamera()
    {
        if (!IsSandbox || !sandboxFollow || session.Camera.IsFree) return;
        var characters = session.CurrentSnapshot.Characters;
        if (characters.FirstOrDefault(c => c.Id == sandboxHero) is not { } hero) return;
        var p = DrawnPosition(hero);
        if (!sandboxFoe.IsNone && characters.FirstOrDefault(c => c.Id == sandboxFoe) is { } foe) p = (p + DrawnPosition(foe)) / 2;
        var s = session.Camera.Current;
        session.Camera.Place(new Vector2(p.X, p.Z), s.Yaw, s.Pitch, s.Distance);
        camera.Target = p;
    }

    /// <summary>The floor's grid and the debug panel, over the finished picture.</summary>
    void DrawSandbox(DebugOverlay o, int width, int height)
    {
        DrawSandboxGrid(o, width, height);
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
        float pad = 8, textW = lines.Max(l => l.Text.Length) * o.CharWidth, w = textW + 2 * pad, h = lines.Count * o.LineHeight + 2 * pad;
        float x0 = width - w - 10, y0 = height - h - 10;   // bottom right: the character stands in the middle of the view
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        for (int i = 0; i < lines.Count; i++) o.Text(lines[i].Text, x0 + pad, y0 + pad + i * o.LineHeight, lines[i].Colour);
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
        string modeText = mode switch { SpeedMode.Walk => $"WALK (cap {hot.WalkSpeed:0.#})", SpeedMode.Run => $"RUN (cap {SpeedMode.RunCap:0})", _ => "FREE (no cap)" };
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
            float length = animationLengths?.Of(layer.Name) ?? 1;
            add($"  {layer.Name,-28} t {layer.Time,5:0.00}/{length,5:0.00}  w {layer.Weight:0.00}", null);
        }
    }

    /// <summary>The floor's lines, projected through the camera: every 10 units, stronger every 100 (screen-space lines: no depth test, so they also cross the characters).</summary>
    void DrawSandboxGrid(DebugOverlay o, int width, int height)
    {
        var view = camera.View * camera.Projection(width / (float)Math.Max(height, 1), camera.Near, camera.ViewDistance);
        float y = scene.FlatHeight + 0.1f;
        var minor = new Vector4(0.1f, 0.12f, 0.2f, 0.3f);
        var major = new Vector4(0.05f, 0.07f, 0.15f, 0.75f);
        var axisX = new Vector4(1f, 0.45f, 0.4f, 0.8f);
        var axisZ = new Vector4(0.45f, 0.6f, 1f, 0.8f);
        for (float t = -SandboxHalf; t <= SandboxHalf + 0.01f; t += SandboxMinor)
        {
            bool isMajor = MathF.Abs(MathF.IEEERemainder(t, SandboxMajor)) < 0.01f;
            bool origin = MathF.Abs(t) < 0.01f;
            float thickness = isMajor ? 2 : 1;
            Segment(o, view, width, height, new Vector3(-SandboxHalf, y, t), new Vector3(SandboxHalf, y, t), thickness, origin ? axisX : isMajor ? major : minor);
            Segment(o, view, width, height, new Vector3(t, y, -SandboxHalf), new Vector3(t, y, SandboxHalf), thickness, origin ? axisZ : isMajor ? major : minor);
        }
        o.Flush(width, height);
    }

    static void Segment(DebugOverlay o, Matrix4x4 view, int width, int height, Vector3 a, Vector3 b, float thickness, Vector4 colour)
    {
        const float near = 0.5f;
        var ca = Vector4.Transform(new Vector4(a, 1), view);
        var cb = Vector4.Transform(new Vector4(b, 1), view);
        if (ca.W < near && cb.W < near) return;
        if (ca.W < near) ca = Vector4.Lerp(ca, cb, (near - ca.W) / (cb.W - ca.W));
        else if (cb.W < near) cb = Vector4.Lerp(cb, ca, (near - cb.W) / (ca.W - cb.W));
        float x0 = (ca.X / ca.W * 0.5f + 0.5f) * width, y0 = (0.5f - ca.Y / ca.W * 0.5f) * height;
        float x1 = (cb.X / cb.W * 0.5f + 0.5f) * width, y1 = (0.5f - cb.Y / cb.W * 0.5f) * height;
        // Both ends far off screen on the same side: nothing to see.
        if (x0 < -width && x1 < -width || x0 > 2 * width && x1 > 2 * width || y0 < -height && y1 < -height || y0 > 2 * height && y1 > 2 * height) return;
        o.Line(x0, y0, x1, y1, thickness, colour);
    }
}
