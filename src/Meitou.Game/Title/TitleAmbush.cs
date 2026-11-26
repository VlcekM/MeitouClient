using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;

namespace Meitou.Game.Title;

/// <summary>
/// The title fight's second easter egg (Meitou's own): one title in <see cref="Odds"/>, as soon as the title shows, a Beak Thing charges in from
/// the right, smashes the pair furthest from the camera across to the left of the frame, kills the other four one blow at a time while they close in
/// on it, then turns to the wanderer on the hill, walks at him while he staggers back and runs at him and on past the camera once he has. A little
/// later two of the fallen, bloodied, get up and fight on for good. A system of the fight's own world (<see cref="TitleBattle"/>): it gives the
/// orders, puts the struck down and moves the Beak Thing on its run; the fight's combat, animation and ragdolls do the rest. Fighters 2p and 2p + 1
/// are pair p, pair 0 the furthest.
/// </summary>
sealed class TitleAmbush : ICombatHitObserver, ICombatDefenceObserver, ICombatDefenceFilter, ICombatHitFilter
{
    #region Rolling

    /// <summary>
    /// The environment variable that forces the ambush (1) or rules it out (0); unset, it rolls.
    /// </summary>
    public const string ForceVariable = "MEITOU_TITLE_AMBUSH";

    /// <summary>
    /// One title in this many is ambushed.
    /// </summary>
    const int Odds = 5000;

    /// <summary>
    /// Whether this title is ambushed: as <see cref="ForceVariable"/> says, else (not for a screenshot, which must not depend on chance) one in
    /// <see cref="Odds"/>.
    /// </summary>
    public static bool Rolls(bool screenshot) => Rolls(Environment.GetEnvironmentVariable(ForceVariable), screenshot, Random.Shared.Next(Odds));

    /// <summary>
    /// <see cref="Rolls(bool)"/> with the variable's value <paramref name="forced"/> and the roll <paramref name="roll"/>
    /// (0 to <see cref="Odds"/> - 1).
    /// </summary>
    internal static bool Rolls(string? forced, bool screenshot, int roll) => forced?.Trim() switch
    {
        "1" => true,
        "0" => false,
        _ => !screenshot && roll == 0,
    };

    #endregion

    #region Script

    /// <summary>
    /// The seconds the fight runs before the Beak Thing charges.
    /// </summary>
    public const float StartSeconds = 0;

    /// <summary>
    /// The places, as drawn.
    /// </summary>
    public TitleStage Stage { get; }

    /// <summary>
    /// The Beak Thing.
    /// </summary>
    public CharacterId Beak { get; private set; } = CharacterId.None;

    /// <summary>
    /// What the ambush is doing.
    /// </summary>
    public Step Now { get; private set; }

    /// <summary>
    /// The fighters, pair by pair.
    /// </summary>
    CharacterId[] _fighters = [];

    /// <summary>
    /// Seconds in the current step.
    /// </summary>
    float _stepSeconds;

    /// <summary>
    /// Whether the title shows (<see cref="Begin"/>): the fight's warm-up ticks before do not count towards the wait.
    /// </summary>
    bool _begun;

    /// <summary>
    /// The fight's combat, whose playing techniques show the Beak Thing's swings (<see cref="SwingStruck"/>); null: only landed blows count.
    /// </summary>
    readonly CombatSystem? _combat;

    /// <summary>
    /// What the ambush is doing.
    /// </summary>
    public enum Step
    {
        /// <summary>
        /// The fight goes on; the Beak Thing waits off the frame.
        /// </summary>
        Waiting,

        /// <summary>
        /// The Beak Thing charges the far pair.
        /// </summary>
        Charge,

        /// <summary>
        /// The four fight the Beak Thing, which strikes them down one at a time.
        /// </summary>
        Melee,

        /// <summary>
        /// The Beak Thing runs at the wanderer and on past the camera.
        /// </summary>
        Chase,

        /// <summary>
        /// The Beak Thing is gone; the risers wait, get up and fight.
        /// </summary>
        Gone,
    }

    /// <summary>
    /// An ambush of the places <paramref name="stage"/>; <see cref="Cast"/> gives it its actors; <paramref name="combat"/> is the
    /// fight's.
    /// </summary>
    public TitleAmbush(TitleStage stage, CombatSystem? combat = null) => (Stage, _combat) = (stage, combat);

    /// <summary>
    /// Sets the actors: the fighters pair by pair and the Beak Thing.
    /// </summary>
    public void Cast(IReadOnlyList<CharacterId> fighters, CharacterId beak)
    {
        _fighters = [.. fighters];
        Beak = beak;
        _hill = Stage.World(Stage.Wanderer);
        _past = Stage.World(Stage.Eye);
    }

    /// <summary>
    /// The title shows: the wait for the charge starts.
    /// </summary>
    public void Begin()
    {
        _begun = true;
        Next(Step.Waiting);
    }

    /// <summary>
    /// Each tick: the step's clock, then what the step does.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        if (Beak.IsNone || !_begun) return;

        _stepSeconds += world.TickSeconds;
        switch (Now)
        {
            case Step.Waiting when _stepSeconds >= StartSeconds:
                StartCharge(world);
                break;
            case Step.Charge:
                Charge(world);
                break;
            case Step.Melee:
                Melee(world);
                break;
            case Step.Chase:
                Chase(world);
                break;
            case Step.Gone:
                Rise(world);
                break;
        }

        _blows.Clear();
    }

    /// <summary>
    /// A focused attack of <paramref name="attackers"/> on <paramref name="target"/> next tick (row 5, as the player's pick): the fight keeps its
    /// target rather than choosing again among whoever is attacking it. It is an order, so an earlier order still holding a fighter (the four's on
    /// the Beak Thing, for the risers) does not refuse it as a goal's fight.
    /// </summary>
    static AttackOrder Focused(IReadOnlyList<CharacterId> attackers, CharacterId target, World world) =>
        new(attackers, target) { Row = TaskType.FocusedMeleeAttack, Source = TaskSource.Order, Tick = world.Tick + 1 };

    /// <summary>
    /// The ground gap (world units) between <paramref name="a"/> and <paramref name="b"/>.
    /// </summary>
    static float Gap(World world, CharacterId a, CharacterId b)
    {
        var (pa, pb) = (world.Characters.Next[a.Slot].Position, world.Characters.Next[b.Slot].Position);
        return Vector2.Distance(new Vector2(pa.X, pa.Z), new Vector2(pb.X, pb.Z));
    }

    /// <summary>
    /// Moves on to <paramref name="step"/>, its clock at 0.
    /// </summary>
    void Next(Step step)
    {
        if (step != Now) Console.WriteLine($"title     beak thing ambush: {step}");

        Now = step;
        _stepSeconds = 0;
    }

    #endregion

    #region Blows

    /// <summary>
    /// Who the Beak Thing's blows fell on since the last tick's inputs, landed, blocked or dodged alike: none of the fighters can stop it (the
    /// observers may not change the world).
    /// </summary>
    readonly List<CharacterId> _blows = [];

    /// <summary>
    /// The Beak Thing's landed blows, kept for the next tick's inputs.
    /// </summary>
    public void HitResolved(World world, in CombatHit hit)
    {
        if (hit.Attacker == Beak && hit.Source == CombatHitSource.Melee) _blows.Add(hit.Victim);
    }

    /// <summary>
    /// The Beak Thing's blocked and dodged blows, kept for the next tick's inputs as if they landed.
    /// </summary>
    public void Defended(World world, in CombatDefence defence)
    {
        if (defence.Attacker == Beak) _blows.Add(defence.Defender);
    }

    /// <summary>
    /// In the melee the four take no defence against the Beak Thing, so its every swing lands and each of them falls to one.
    /// </summary>
    public int Defence(World world, in DefenceChoiceAsk ask) => ask.Attacker == Beak && Now == Step.Melee ? -1 : ask.Technique;

    /// <summary>
    /// In the melee a blow of the four on the Beak Thing mid-swing misses, so no hit cuts its swing short and every swing reaches its strike.
    /// </summary>
    public void HitLanding(World world, ref PendingHit hit)
    {
        if (hit.Victim == Beak && Now == Step.Melee && Swinging(hit.Tick)) hit.Cancelled = true;
    }

    /// <summary>
    /// Whether the Beak Thing is mid-swing at tick <paramref name="tick"/>.
    /// </summary>
    bool Swinging(long tick) => _combat?.Playing(Beak, tick) is { IsAttack: true };

    #endregion

    #region Charge

    /// <summary>
    /// The longest the charge lasts before the smash comes anyway (a blow that never lands, a block).
    /// </summary>
    const float SmashLatestSeconds = 14;

    /// <summary>
    /// The gap (world units) inside which the charging Beak Thing drops from its run into its fighting stance.
    /// </summary>
    const float StanceGap = 60;

    /// <summary>
    /// The thrown pair's launch: its upward speed (world units/s).
    /// </summary>
    const float ThrowLift = 110;

    /// <summary>
    /// The cap of the thrown pair's whole launch speed (the ragdolls' own, <c>CombatSystem.BlastMaxSpeed</c>).
    /// </summary>
    const float ThrowMaxSpeed = 380;

    /// <summary>
    /// The gravity the thrown fly under (world units/s², docs/game/ragdolls.md "Middleware and scene").
    /// </summary>
    const float Gravity = 98.1f;

    /// <summary>
    /// The launch that lands a fighter standing at <paramref name="from"/> on the left of the frame: <see cref="ThrowLift"/> up and as fast out as
    /// that flight needs to get there, the second of the pair a little shorter.
    /// </summary>
    internal Vector3 ThrowToward(Vector3 from, int which)
    {
        var landing = Stage.World(Stage.Landing);
        var offset = landing - new Vector2(from.X, from.Z);
        float distance = offset.Length() * (which == 0 ? 1 : 0.85f);
        var dir = distance > 1e-3f ? Vector2.Normalize(offset) : -Vector2.UnitX;

        // flight range = 2 x lift x out / g
        float outward = distance * Gravity / (2 * ThrowLift);
        var launch = new Vector3(dir.X * outward, ThrowLift, dir.Y * outward);
        return launch.Length() > ThrowMaxSpeed ? launch * (ThrowMaxSpeed / launch.Length()) : launch;
    }

    /// <summary>
    /// The charge: the Beak Thing runs at the far pair's first fighter.
    /// </summary>
    void StartCharge(World world)
    {
        Next(Step.Charge);
        if (world.Characters.Cold(Beak.Slot) is { } beak) beak.CombatRunning = true;

        world.Commands.Enqueue(Focused([Beak], _fighters[0], world));
    }

    /// <summary>
    /// Near its target the Beak Thing takes its stance; its first blow (or the time running out) throws the far pair across to the left, and the
    /// four turn on it.
    /// </summary>
    void Charge(World world)
    {
        TakeStance(world);
        if (_blows.Count == 0 && _stepSeconds < SmashLatestSeconds) return;

        foreach (int f in new[] { 0, 1 }) Throw(world, f);

        Next(Step.Melee);
        world.Commands.Enqueue(Focused(_fighters[2..], Beak, world));
        NextTarget(world);
    }

    /// <summary>
    /// Drops the Beak Thing from its run into its fighting stance once it is within <see cref="StanceGap"/> of the far pair's first fighter.
    /// </summary>
    void TakeStance(World world)
    {
        if (world.Characters.Cold(Beak.Slot) is not { CombatRunning: true } beak) return;

        if (Gap(world, Beak, _fighters[0]) < StanceGap) beak.CombatRunning = false;
    }

    /// <summary>
    /// Strikes fighter <paramref name="fighter"/> of the far pair down and launches it towards the landing (<see cref="ThrowToward"/>).
    /// </summary>
    void Throw(World world, int fighter)
    {
        var table = world.Characters;
        int slot = _fighters[fighter].Slot;
        StrikeDown(world, fighter, out _);
        if (table.Cold(slot) is not { } thrown) return;

        thrown.Launch = ThrowToward(table.Next[slot].Position, fighter);
    }

    #endregion

    #region Melee

    /// <summary>
    /// The longest the Beak Thing fights one of the four before it goes down anyway: only a stall, as the four do not defend against it
    /// (<see cref="Defence"/>), so each falls to a blow.
    /// </summary>
    const float StrikeLatestSeconds = 20;

    /// <summary>
    /// The least time between two of the four falling, so one swing that catches two still fells one.
    /// </summary>
    const float StrikeGapSeconds = 2;

    /// <summary>
    /// How near (world units, across the ground) one of the four must be for the Beak Thing's swing to strike it down (<see cref="SwingStruck"/>).
    /// </summary>
    const float SwingReach = 45;

    /// <summary>
    /// The launch speed away from the Beak Thing of one of the four struck down, out (world units/s).
    /// </summary>
    const float StrikeThrow = 90;

    /// <summary>
    /// The launch speed away from the Beak Thing of one of the four struck down, up (world units/s).
    /// </summary>
    const float StrikeLift = 45;

    /// <summary>
    /// The fighter (position in <see cref="_fighters"/>) the Beak Thing is fighting in the melee; -1 before.
    /// </summary>
    int _target = -1;

    /// <summary>
    /// The Beak Thing's current swing has reached its strike and been counted (<see cref="SwingStruck"/>).
    /// </summary>
    bool _swingCounted;

    /// <summary>
    /// How many of the four fell to the stall fallback (<see cref="StrikeLatestSeconds"/>) rather than to a swing.
    /// </summary>
    internal int Stalls { get; private set; }

    /// <summary>
    /// The melee: each swing of the Beak Thing strikes one of the four down (the one its blow lands on, else the nearest within
    /// <see cref="SwingReach"/>, its target first), and it turns on the next; when none is left it turns for the hill. Only a stall past
    /// <see cref="StrikeLatestSeconds"/> fells its target without a swing.
    /// </summary>
    void Melee(World world)
    {
        if (_target < 0)
        {
            if (_stepSeconds >= ChaseDelaySeconds) StartChase(world);
            return;
        }

        int swung = SwingStruck(world);
        if (_stepSeconds < StrikeGapSeconds) return;

        int struck = Struck(swung);
        if (struck < 0) return;

        StrikeAway(world, struck);
        _stepSeconds = 0;
        NextTarget(world);
    }

    /// <summary>
    /// The one of the four to strike down this tick: the one a blow landed on, else the one the swing reached (<paramref name="swung"/>), else
    /// after <see cref="StrikeLatestSeconds"/> the target (a stall); -1 for none.
    /// </summary>
    int Struck(int swung)
    {
        int struck = StruckByBlow();
        if (struck < 0) struck = swung;
        if (struck >= 0 || _stepSeconds < StrikeLatestSeconds) return struck;

        Stalls++;
        return _target;
    }

    /// <summary>
    /// Strikes fighter <paramref name="struck"/> down and throws it out and up, away from the Beak Thing.
    /// </summary>
    void StrikeAway(World world, int struck)
    {
        var table = world.Characters;
        var beak = table.Next[Beak.Slot].Position;
        StrikeDown(world, struck, out int slot);

        var away = new Vector2(table.Next[slot].Position.X - beak.X, table.Next[slot].Position.Z - beak.Z);
        away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Vector2.UnitX;
        if (table.Cold(slot) is not { } victim) return;

        victim.Launch = new Vector3(away.X * StrikeThrow, StrikeLift, away.Y * StrikeThrow);
    }

    /// <summary>
    /// The one of the four still standing within <see cref="SwingReach"/> of the Beak Thing as its swing reaches its strike (its target first),
    /// once per swing; -1 for none. Its huge swings often land on no one in the fight's sweep, so the scene fells the one it swings at.
    /// </summary>
    int SwingStruck(World world)
    {
        if (_combat?.Playing(Beak, world.Tick) is not { IsAttack: true } playing)
        {
            _swingCounted = false;
            return -1;
        }

        if (playing.Progress < playing.Technique.StrikeProgress(0))
        {
            _swingCounted = false;
            return -1;
        }

        if (_swingCounted) return -1;

        _swingCounted = true;
        return Enumerable.Range(2, _fighters.Length - 2)
            .Where(f => !_struck.Contains(f) && Gap(world, Beak, _fighters[f]) < SwingReach)
            .OrderBy(f => f == _target ? 0 : 1)
            .ThenBy(f => Gap(world, Beak, _fighters[f]))
            .DefaultIfEmpty(-1)
            .First();
    }

    /// <summary>
    /// The one of the four still standing that the Beak Thing's blow reached this tick, its target first; -1 for none.
    /// </summary>
    int StruckByBlow()
    {
        if (_blows.Contains(_fighters[_target])) return _target;

        return Enumerable.Range(2, _fighters.Length - 2)
            .Where(f => !_struck.Contains(f) && _blows.Contains(_fighters[f]))
            .DefaultIfEmpty(-1)
            .First();
    }

    /// <summary>
    /// Sets the Beak Thing on the nearest of the four still standing; none left: <see cref="_target"/> is -1.
    /// </summary>
    void NextTarget(World world)
    {
        _target = Enumerable.Range(2, _fighters.Length - 2)
            .Where(f => !_struck.Contains(f))
            .OrderBy(f => Gap(world, Beak, _fighters[f]))
            .DefaultIfEmpty(-1)
            .First();
        if (_target >= 0) world.Commands.Enqueue(Focused([Beak], _fighters[_target], world));
    }

    #endregion

    #region Chase

    /// <summary>
    /// The pause after the last of the four falls before the Beak Thing runs for the hill.
    /// </summary>
    const float ChaseDelaySeconds = 0.8f;

    /// <summary>
    /// The Beak Thing's speed (world units/s) on its run for the hill.
    /// </summary>
    const float ChaseSpeed = 95;

    /// <summary>
    /// The Beak Thing's speed (world units/s) on its slow walk at the wanderer before its run.
    /// </summary>
    const float WalkSpeed = 20;

    /// <summary>
    /// How long the Beak Thing takes to turn in place to the wanderer once the last of the four is down.
    /// </summary>
    const float TurnSeconds = 1;

    /// <summary>
    /// The run's way point in the world under the wanderer.
    /// </summary>
    Vector2 _hill;

    /// <summary>
    /// The run's way point in the world under the camera's eye.
    /// </summary>
    Vector2 _past;

    /// <summary>
    /// Where the run started, in the world.
    /// </summary>
    Vector2 _runFrom;

    /// <summary>
    /// The Beak Thing's facing when its chase began, which it turns from (<see cref="Turn"/>).
    /// </summary>
    float _turnFrom;

    /// <summary>
    /// How long the Beak Thing walks at the wanderer before it runs: as long as his startled stagger (<see cref="TitleFlight.StaggerSeconds"/>), so
    /// it runs only once that has played out.
    /// </summary>
    public float WalkSeconds { get; set; } = TitleFlight.DefaultStaggerSeconds;

    /// <summary>
    /// Where the Beak Thing's run started, in the world.
    /// </summary>
    public Vector2 RunFrom => _runFrom;

    /// <summary>
    /// Seconds since the Beak Thing, turned to the wanderer, started its walk at him (he startles from then on); null before.
    /// </summary>
    public float? ChaseSeconds => Now switch
    {
        Step.Chase when _stepSeconds >= TurnSeconds => _stepSeconds - TurnSeconds,
        Step.Gone => float.MaxValue,
        _ => null,
    };

    /// <summary>
    /// How far along its run to the wanderer the Beak Thing is, 0 to 1 (1 from the wanderer on), for its climb up the hill as drawn.
    /// </summary>
    public float HillShare(Vector3 beak)
    {
        float whole = Vector2.Distance(_runFrom, _hill);
        return whole < 1e-3f ? 1 : Math.Clamp(Vector2.Distance(_runFrom, new Vector2(beak.X, beak.Z)) / whole, 0, 1);
    }

    /// <summary>
    /// On the Beak Thing's chase it turns in place to the wanderer, walks at him for <see cref="WalkSeconds"/>, then runs at
    /// <see cref="ChaseSpeed"/> for him and on under the camera.
    /// </summary>
    public void Move(World world, Partition part)
    {
        if (Now != Step.Chase || Beak.Slot < part.Start || Beak.Slot >= part.End) return;

        ref var n = ref world.Characters.Next[Beak.Slot];
        if (n.Task == (byte)CharacterTask.Attack) return;

        var from = world.Characters.Previous[Beak.Slot].Position;
        var dir = Heading(from);
        if (_stepSeconds < TurnSeconds)
        {
            Turn(ref n, MathF.Atan2(dir.X, dir.Y));
            return;
        }

        Walk(world, ref n, from, dir);
    }

    /// <summary>
    /// The run: the Beak Thing leaves the fight and runs (<see cref="Move"/>) until it is past the camera.
    /// </summary>
    void StartChase(World world)
    {
        Next(Step.Chase);
        var at = world.Characters.Next[Beak.Slot].Position;
        _runFrom = new Vector2(at.X, at.Z);
        _turnFrom = world.Characters.Next[Beak.Slot].Yaw;
        world.Commands.Enqueue(new StopCommand([Beak]) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// The run ends once the Beak Thing is past the point under the camera's eye.
    /// </summary>
    void Chase(World world)
    {
        var at = world.Characters.Next[Beak.Slot].Position;
        if (Vector2.Distance(_runFrom, new Vector2(at.X, at.Z)) < Vector2.Distance(_runFrom, _past)) return;

        world.Characters.Next[Beak.Slot].Velocity = Vector3.Zero;
        Next(Step.Gone);
    }

    /// <summary>
    /// The direction (across the ground) from <paramref name="from"/> to the wanderer's hill, once the run is that far along, else on to the
    /// point under the camera.
    /// </summary>
    Vector2 Heading(Vector3 from)
    {
        var at = new Vector2(from.X, from.Z);
        var toward = Vector2.Distance(_runFrom, at) < Vector2.Distance(_runFrom, _hill) ? _hill : _past;
        return Vector2.Normalize(toward - at + new Vector2(1e-4f, 0));
    }

    /// <summary>
    /// The Beak Thing standing, turned <see cref="_stepSeconds"/> of <see cref="TurnSeconds"/> of the way from its facing when the chase began to
    /// <paramref name="facing"/>.
    /// </summary>
    void Turn(ref CharacterHot n, float facing)
    {
        float share = Math.Clamp(_stepSeconds / TurnSeconds, 0, 1);
        n.Velocity = Vector3.Zero;
        n.Yaw = _turnFrom + MathF.IEEERemainder(facing - _turnFrom, MathF.Tau) * share;
    }

    /// <summary>
    /// One tick of the Beak Thing's walk at the wanderer, then its run, along <paramref name="dir"/> from <paramref name="from"/>.
    /// </summary>
    void Walk(World world, ref CharacterHot n, Vector3 from, Vector2 dir)
    {
        float speed = _stepSeconds < TurnSeconds + WalkSeconds ? WalkSpeed : ChaseSpeed;
        var step = dir * speed * world.TickSeconds;
        float x = from.X + step.X;
        float z = from.Z + step.Y;

        n.Position = new Vector3(x, world.Walkability.GroundHeight(x, z, from.Y), z);
        n.Velocity = new Vector3(dir.X, 0, dir.Y) * speed;
        n.Yaw = MathF.Atan2(dir.X, dir.Y);
    }

    #endregion

    #region Fallen

    /// <summary>
    /// After the Beak Thing is gone, the pause before two of the fallen get up.
    /// </summary>
    const float RiseDelaySeconds = 3;

    /// <summary>
    /// The time the two risers take to stand before they fight.
    /// </summary>
    const float StandSeconds = 3;

    /// <summary>
    /// How long a fighter that will get up again stays knocked out until then (character seconds; it is woken).
    /// </summary>
    const float OutSeconds = 1e6f;

    /// <summary>
    /// The two fighters (pair 1) that get up again at the end and fight each other.
    /// </summary>
    static readonly int[] Risers = [2, 3];

    /// <summary>
    /// The fighters struck down so far (positions in <see cref="_fighters"/>): they are drawn bloodied.
    /// </summary>
    readonly HashSet<int> _struck = [];

    /// <summary>
    /// Whether the risers were woken.
    /// </summary>
    bool _woken;

    /// <summary>
    /// Whether the risers were set on each other.
    /// </summary>
    bool _fighting;

    /// <summary>
    /// Whether the fighter in slot <paramref name="slot"/> was struck down (it is drawn bloodied, also after it gets up).
    /// </summary>
    public bool Bloodied(int slot) => _struck.Any(f => _fighters[f].Slot == slot);

    /// <summary>
    /// After the Beak Thing is gone the risers get up, and once on their feet they fight each other for good.
    /// </summary>
    void Rise(World world)
    {
        if (!_woken && _stepSeconds >= RiseDelaySeconds) WakeRisers(world);
        if (_woken && !_fighting && _stepSeconds >= RiseDelaySeconds + StandSeconds) SetRisersFighting(world);
    }

    /// <summary>
    /// Wakes the two risers, standing still where they lie.
    /// </summary>
    void WakeRisers(World world)
    {
        _woken = true;
        var table = world.Characters;
        foreach (int f in Risers)
        {
            int slot = _fighters[f].Slot;
            if (table.Cold(slot)?.Medical is { } medical && medical.WakeUp()) table.Next[slot].Velocity = Vector3.Zero;
        }
    }

    /// <summary>
    /// Sets the two risers on each other.
    /// </summary>
    void SetRisersFighting(World world)
    {
        _fighting = true;
        var (a, b) = (_fighters[Risers[0]], _fighters[Risers[1]]);
        world.Commands.Enqueue(Focused([a], b, world));
        world.Commands.Enqueue(Focused([b], a, world));
    }

    /// <summary>
    /// Strikes fighter <paramref name="fighter"/> down: knocked out until woken for a riser, else killed. <paramref name="slot"/> is its slot.
    /// </summary>
    void StrikeDown(World world, int fighter, out int slot)
    {
        var id = _fighters[fighter];
        slot = id.Slot;
        _struck.Add(fighter);
        if (world.Characters.Cold(slot)?.Medical is not { } medical) return;

        // As the debug kill and knock-out put a character down: it stops where it is.
        var events = new List<MedicalEvent>();
        if (Risers.Contains(fighter)) medical.KnockOut(OutSeconds, events);
        else medical.Kill(events);
        if (events.Count == 0) return;

        TaskStart.Stand(world.Characters, slot);
        world.Characters.Next[slot].Velocity = Vector3.Zero;
    }

    #endregion
}
