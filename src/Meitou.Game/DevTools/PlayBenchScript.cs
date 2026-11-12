using System.Globalization;
using System.Numerics;
using Meitou.Game.Host;
using Meitou.Game.Host.Cameras;
using Meitou.Simulation;

namespace Meitou.Game.DevTools;

/// <summary>
/// One segment of the play benchmark's script: its name, its length in seconds, what it does each frame (t: seconds into it, dt: the frame's) and
/// once when it starts.
/// </summary>
sealed record BenchSegment(string Name, double Seconds, Action<double, double>? Frame = null, Action? Begin = null);

/// <summary>
/// Builds <see cref="PlayBench"/>'s script (docs/bench.md, "Play benchmark"): each segment's length and what it does each frame. Its fields are the
/// state the segments share while the script runs (the camera's start, the pan offset, the characters picked to talk to, trade with and loot).
/// </summary>
sealed class PlayBenchScript
{
    /// <summary>
    /// The game the script drives.
    /// </summary>
    readonly GameHost _host;

    /// <summary>
    /// The strategy camera the segments move.
    /// </summary>
    readonly StrategyCamera _strategy;

    /// <summary>
    /// The camera's yaw when the script was built (radians).
    /// </summary>
    readonly float _yaw0;

    /// <summary>
    /// The camera's pitch when the script was built (radians).
    /// </summary>
    readonly float _pitch0;

    /// <summary>
    /// The camera's distance when the script was built.
    /// </summary>
    readonly float _distance0;

    /// <summary>
    /// The squad leader (or first player character) the segments follow and give orders as.
    /// </summary>
    readonly CharacterId _leader;

    /// <summary>
    /// The camera pivot (x, z) when the script started.
    /// </summary>
    readonly Vector2 _origin;

    /// <summary>
    /// The direction pan, travel and the walk go: towards the world's origin, else +x.
    /// </summary>
    readonly Vector2 _direction;

    /// <summary>
    /// How far pan and travel have moved the pivot from <see cref="_origin"/>.
    /// </summary>
    Vector2 _pan = Vector2.Zero;

    /// <summary>
    /// The path segment's random directions and distances (seed 1, so every run asks the same).
    /// </summary>
    readonly Random _random = new(1);

    /// <summary>
    /// The character the talk segment talks to, null when none is within reach.
    /// </summary>
    CharacterSnapshot? _talkee;

    /// <summary>
    /// The trader the trade segment trades with, null when none is within reach.
    /// </summary>
    CharacterSnapshot? _trader;

    /// <summary>
    /// The downed body the loot segment loots, null when none is within reach.
    /// </summary>
    CharacterSnapshot? _body;

    /// <summary>
    /// The unlocked container the container segment opens, null when none is within reach.
    /// </summary>
    Simulation.Locks.ContainerSpot? _box;

    /// <summary>
    /// The panel the panel segments have open ("-" at their start, null for none).
    /// </summary>
    string? _panel;

    /// <summary>
    /// The flight segment's current hop (each player character in turn), -1 before the first.
    /// </summary>
    int _flightHop = -1;

    /// <summary>
    /// Reads the camera's start; <paramref name="leader"/>, <paramref name="origin"/> and <paramref name="direction"/> are fixed for the run.
    /// </summary>
    public PlayBenchScript(GameHost host, CharacterId leader, Vector2 origin, Vector2 direction)
    {
        _host = host;
        _leader = leader;
        _origin = origin;
        _direction = direction;
        _strategy = host.Session.Camera.Strategy;
        (_yaw0, _pitch0, _distance0) = (_strategy.Yaw, _strategy.Pitch, _strategy.Distance);
    }

    /// <summary>
    /// The script: each segment's length and what it does each frame (t: seconds into it, dt: the frame's).
    /// </summary>
    public BenchSegment[] Segments() =>
    [
        new("settle", 8),
        new("idle", 10, Begin: IdleBegin),
        new("orbit", 12, (t, dt) => _strategy.Rotate((float)(dt * 2 * Math.PI / 12))),
        new("zoom", 10, ZoomFrame),
        new("pan", 12, PanFrame, Begin: () => _pan = Vector2.Zero),
        new("travel", 30, TravelFrame),
        new("return", 10, Begin: () => Look(_origin, _yaw0, _pitch0, _distance0)),
        new("speed-x2", 10, FollowLeader, Begin: () => _host.Session.Simulation.SetSpeed(2)),
        new("speed-x5", 15, FollowLeader, Begin: () => _host.Session.Simulation.SetSpeed(5)),
        new("talk", 10, Retry(() => _host.Session.CurrentSnapshot.Conversation is not null, TalkAgain), Begin: TalkBegin),
        new("trade", 12, Retry(() => _host.Session.CurrentSnapshot.Trade is not null, TradeAgain), Begin: TradeBegin),
        new("container", 10, FollowLeader, Begin: ContainerBegin),
        new("doors", 8, FollowLeader, Begin: DoorsBegin),
        new("pick-up", 8, FollowLeader, Begin: PickUpBegin),
        // Path requests: every half second the squad, or each member apart, is sent 20 to 200 m away in a random direction (some places it cannot
        // reach).
        new("paths", 20, PathsFrame),
        new("walk", 15, FollowLeader, Begin: WalkBegin),
        new("fight", 25, FollowLeader, Begin: FightBegin),
        new("fight-on", 0.1, Begin: FightOnBegin),
        new("loot", 8, Retry(() => !_host.Session.CurrentSnapshot.OpenLoot.IsNone, LootAgain), Begin: LootBegin),
        new("panels", 12, PanelsFrame, Begin: () => _panel = "-"),
        // The same panels again (what the first opening built is warm), with the limbs and the build menu.
        new("panels-warm", 15, PanelsWarmFrame, Begin: () => _panel = "-"),
        // The far grazing view: the most terrain, foliage and objects in sight.
        new("horizon", 12, (t, dt) => Look(_origin, _yaw0 + (float)(t * 2 * Math.PI / 12), 0.12f, 900),
            Begin: () => _host.Player.ShowUiPanel(null)),
        new("overview", 8, Begin: OverviewBegin),
        // Every player character in turn, twice, 6 s each, as a double click on its portrait does (a character flight with "Character flights"
        // on, else a cut).
        new("flights", 6 * 2 * Math.Max(1, _host.Session.CurrentSnapshot.PlayerSquads.Sum(s => s.Members.Count)), FlightsFrame,
            Begin: FlightsBegin),
    ];

    /// <summary>
    /// Idle: stops following and puts the camera back where it started.
    /// </summary>
    void IdleBegin()
    {
        _host.Player.Follow = null;
        Look(_origin, _yaw0, _pitch0, _distance0);
    }

    /// <summary>
    /// Zoom: out to the farthest camera distance over 5 s, back over the next 5.
    /// </summary>
    void ZoomFrame(double t, double dt)
    {
        float k = (float)(t < 5 ? t / 5 : 1 - (t - 5) / 5);
        Look(_origin, _strategy.Yaw, _pitch0, _distance0 + (Data.World.KenshiCamera.MaxDistance - _distance0) * k);
    }

    /// <summary>
    /// Pan: moves the pivot along <see cref="_direction"/> at 400 units/s.
    /// </summary>
    void PanFrame(double t, double dt)
    {
        _pan += _direction * (float)(400 * dt);
        Look(_origin + _pan, _strategy.Yaw, _pitch0, _distance0);
    }

    /// <summary>
    /// Travel: moves the pivot on along <see cref="_direction"/> at 3000 units/s.
    /// </summary>
    void TravelFrame(double t, double dt)
    {
        _pan += _direction * (float)(3000 * dt);
        Look(_origin + _pan, _strategy.Yaw, _pitch0, _distance0);
    }

    /// <summary>
    /// The frame step of the segments that only keep the camera on the leader.
    /// </summary>
    void FollowLeader(double t, double dt) => OnLeader();

    /// <summary>
    /// Talk: back to normal speed, and the leader talks to the nearest living character that can talk to them.
    /// </summary>
    void TalkBegin()
    {
        _host.Session.Simulation.SetSpeed(1);
        _talkee = Nearest("talker", _host.Session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Alive(c)
            && _host.Session.World.CanTalk(c.Id, _leader)).ToList(), c => c.Position);
        if (_talkee is not null) Order(new Simulation.Dialogue.TalkOrder(_leader, _talkee.Id) { Tick = 0 });
    }

    /// <summary>
    /// Asks the talk again (the walk to the talker gave up).
    /// </summary>
    void TalkAgain()
    {
        if (_talkee is not null) Order(new Simulation.Dialogue.TalkOrder(_leader, _talkee.Id) { Tick = 0 });
    }

    /// <summary>
    /// Trade: ends the conversation and opens trade with the nearest living trader.
    /// </summary>
    void TradeBegin()
    {
        Order(new Simulation.Dialogue.EndConversation { Tick = 0 });
        _trader = Nearest("trader", _host.Session.CurrentSnapshot.Characters.Where(c => c.IsTrader && Alive(c)).ToList(), c => c.Position);
        if (_trader is not null) Order(new Simulation.Trading.OpenTrade(_leader, _trader.Id) { Tick = 0 });
    }

    /// <summary>
    /// Asks the trade again (the walk to the trader gave up).
    /// </summary>
    void TradeAgain()
    {
        if (_trader is not null) Order(new Simulation.Trading.OpenTrade(_leader, _trader.Id) { Tick = 0 });
    }

    /// <summary>
    /// Container: closes the trade and opens the nearest unlocked container.
    /// </summary>
    void ContainerBegin()
    {
        Order(new Simulation.Trading.CloseTrade { Tick = 0 });
        _box = Nearest("container", _host.Session.CurrentSnapshot.Containers.Where(c => !c.Locked).ToList(), c => c.Position);
        if (_box is not null)
            Order(new Simulation.Items.OpenContainerOrder([_leader], _box.Id) { Tick = 0 });
    }

    /// <summary>
    /// Doors: closes the container and toggles the three doors nearest the leader.
    /// </summary>
    void DoorsBegin()
    {
        Order(new Simulation.Items.CloseContainer { Tick = 0 });
        var leader = LeaderAt();
        var doors = _host.Session.CurrentSnapshot.DoorPicks.OrderBy(d => Vector3.DistanceSquared(d.Position, leader)).Take(3).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"bench     doors: {doors.Count}, "
            + $"the nearest {(doors.Count > 0 ? Vector3.Distance(doors[0].Position, leader) : float.NaN):0} away"));
        foreach (var door in doors) Order(new Simulation.Locks.DoorOrder([_leader], door.Id) { Tick = 0 });
    }

    /// <summary>
    /// Pick-up: the leader picks up the ground item nearest them.
    /// </summary>
    void PickUpBegin()
    {
        var leader = LeaderAt();
        var items = _host.Session.CurrentSnapshot.GroundItems;
        if (items.Count == 0)
        {
            Console.WriteLine("bench     no ground item");
            return;
        }

        var item = items.MinBy(i => Vector3.DistanceSquared(i.Position, leader));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"bench     ground item {item.Name} {Vector3.Distance(item.Position, leader):0} away"));
        Order(new Simulation.Items.PickUpItemOrder([_leader], item.Id) { Tick = 0 });
    }

    /// <summary>
    /// Paths: every half second sends the squad (every third time) or each member apart somewhere random around the leader.
    /// </summary>
    void PathsFrame(double t, double dt)
    {
        OnLeader();
        if ((int)((t - dt) * 2) == (int)(t * 2)) return;
        var at = LeaderAt();
        var squad = Squad();
        if ((int)(t * 2) % 3 == 0) Order(new MoveOrder(squad, Somewhere(at)) { Tick = 0 });
        else foreach (var id in squad) Order(new MoveOrder([id], Somewhere(at)) { Tick = 0 });
    }

    /// <summary>
    /// A random point 200 to 2000 units from <paramref name="at"/> in a random direction (two draws of <see cref="_random"/>).
    /// </summary>
    Vector3 Somewhere(Vector3 at)
    {
        double a = _random.NextDouble() * 2 * Math.PI, r = 200 + _random.NextDouble() * 1800;
        return at + new Vector3((float)(Math.Cos(a) * r), 0, (float)(Math.Sin(a) * r));
    }

    /// <summary>
    /// Walk: normal speed, and the squad walks 600 units along <see cref="_direction"/>.
    /// </summary>
    void WalkBegin()
    {
        _host.Session.Simulation.SetSpeed(1);
        var at = LeaderAt();
        _host.Session.World.Commands.Enqueue(new MoveOrder(Squad(),
            at + new Vector3(_direction.X * 600, 0, _direction.Y * 600)) { Tick = _host.Session.World.Tick });
    }

    /// <summary>
    /// Fight: spawns six of <c>--scene-npc</c>'s character beside the squad.
    /// </summary>
    void FightBegin()
    {
        var world = _host.Session.World;
        var at = LeaderAt();
        if (_host.DebugMenu.DebugRecord(_host.GameOptions.SceneNpc, character: true) is not { } npc)
        {
            Console.WriteLine($"bench     no character record matches '{_host.GameOptions.SceneNpc}': no fight");
            return;
        }

        for (int k = 0; k < 6; k++)
        {
            world.Commands.Enqueue(new DebugSpawn(npc.Id,
                new Vector2(at.X + 50 + 15 * (k % 3), at.Z - 20 + 20 * (k / 3))) { Tick = world.Tick });
        }
        Console.WriteLine($"bench     fight: six {npc.Name} spawned beside the squad");
    }

    /// <summary>
    /// Fight-on: the squad attacks the nearest non-player character within 150 units.
    /// </summary>
    void FightOnBegin()
    {
        var at = LeaderAt();
        if (_host.Session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && Vector3.Distance(c.Position, at) < 150)
            .OrderBy(c => Vector3.DistanceSquared(c.Position, at))
            .FirstOrDefault() is { } enemy)
            _host.Session.World.Commands.Enqueue(new Simulation.Combat.AttackOrder(Squad(), enemy.Id) { Tick = _host.Session.World.Tick });
    }

    /// <summary>
    /// Loot: the leader loots the nearest downed (dead or unconscious) body.
    /// </summary>
    void LootBegin()
    {
        _body = Nearest("downed body", _host.Session.CurrentSnapshot.Characters.Where(c => !c.IsPlayer && !Alive(c)).ToList(), c => c.Position);
        if (_body is not null)
            Order(new Simulation.Carrying.OpenLootOrder([_leader], _body.Id) { Tick = 0 });
    }

    /// <summary>
    /// Asks the loot again (the walk to the body gave up).
    /// </summary>
    void LootAgain()
    {
        if (_body is not null) Order(new Simulation.Carrying.OpenLootOrder([_leader], _body.Id) { Tick = 0 });
    }

    /// <summary>
    /// Panels: inventory, stats and the overview 3 s each, opened cold.
    /// </summary>
    void PanelsFrame(double t, double dt)
    {
        string? panel = t < 3 ? "inventory" : t < 6 ? "stats" : t < 9 ? "overview" : null;
        if (panel != _panel)
        {
            _panel = panel;
            try
            {
                _host.Player.ShowUiPanel(panel);
            }
            catch (InvalidOperationException e)
            {
                Console.WriteLine($"bench     panel {panel}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Panels-warm: inventory, stats, the overview, limbs and the build menu 2.5 s each.
    /// </summary>
    void PanelsWarmFrame(double t, double dt)
    {
        string? panel = t < 2.5 ? "inventory" : t < 5 ? "stats" : t < 7.5 ? "overview" : t < 10 ? "limbs" : t < 12.5 ? "build" : null;
        if (panel != _panel)
        {
            _panel = panel;
            try
            {
                _host.Player.ShowUiPanel(panel);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                Console.WriteLine($"bench     panel {panel}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Overview: closes the panels and looks straight down from the farthest distance.
    /// </summary>
    void OverviewBegin()
    {
        _host.Player.ShowUiPanel(null);
        Look(_origin, _yaw0, 1.2f, Data.World.KenshiCamera.MaxDistance);
    }

    /// <summary>
    /// Flights: every 6 s flies to (or follows) the next player character.
    /// </summary>
    void FlightsFrame(double t, double dt)
    {
        var members = _host.Session.CurrentSnapshot.PlayerSquads.SelectMany(s => s.Members).ToList();
        int hop = (int)(t / 6);
        if (members.Count == 0 || hop == _flightHop) return;
        _flightHop = hop;
        var id = members[hop % members.Count];
        if (_host.Player.FlyTo?.Invoke(id) != true) _host.Player.Follow = id;
        Console.WriteLine($"bench     flight {hop + 1} to {id}");
    }

    /// <summary>
    /// Flights start: closes the panels and resets the hop.
    /// </summary>
    void FlightsBegin()
    {
        _host.Player.ShowUiPanel(null);
        _flightHop = -1;
    }

    /// <summary>
    /// The leader's position, else the script's origin on the ground.
    /// </summary>
    Vector3 LeaderAt() => _host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == _leader)?.Position
        ?? new Vector3(_origin.X, 0, _origin.Y);

    /// <summary>
    /// Places the strategy camera.
    /// </summary>
    void Look(Vector2 at, float yaw, float pitch, float distance) => _strategy.Place(at, yaw, pitch, distance);

    /// <summary>
    /// Centres the camera on the leader at the start pitch and distance, keeping the yaw.
    /// </summary>
    void OnLeader()
    {
        var p = LeaderAt();
        Look(new Vector2(p.X, p.Z), _strategy.Yaw, _pitch0, _distance0);
    }

    /// <summary>
    /// Every player character's id.
    /// </summary>
    List<CharacterId> Squad() => [.. _host.Session.CurrentSnapshot.Characters.Where(c => c.IsPlayer).Select(c => c.Id)];

    /// <summary>
    /// Queues <paramref name="command"/> for the current tick.
    /// </summary>
    void Order(SimCommand command) => _host.Session.World.Commands.Enqueue(command with { Tick = _host.Session.World.Tick });

    /// <summary>
    /// The nearest of what the script interacts with, said on the console (or that there is none within reach, 1500 units).
    /// </summary>
    T? Nearest<T>(string what, IEnumerable<T> items, Func<T, Vector3> at) where T : class
    {
        var leader = LeaderAt();
        var found = items.OrderBy(i => Vector3.DistanceSquared(at(i), leader)).FirstOrDefault();
        float d = found is null ? float.NaN : Vector3.Distance(at(found), leader);
        Console.WriteLine(found is null || d > 1500 ? $"bench     no {what} within reach" : string.Create(CultureInfo.InvariantCulture,
            $"bench     {what} {d:0} away"));
        return d <= 1500 ? found : null;
    }

    /// <summary>
    /// Neither dead nor unconscious.
    /// </summary>
    static bool Alive(CharacterSnapshot c) => c.Body is not { Dead: true } and not { Unconscious: true };

    /// <summary>
    /// An interaction asked again now and then (a walk that outlasts the approach gives up), until it opens: every 3 s while
    /// <paramref name="open"/> is false; the camera stays on the leader.
    /// </summary>
    Action<double, double> Retry(Func<bool> open, Action again)
    {
        double next = 3;
        return (t, dt) =>
        {
            OnLeader();
            if (t >= next && !open())
            {
                again();
                next += 3;
            }
        };
    }
}
