using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

/// <summary>
/// The animations a character plays: weighted clips (the movement blend by speed, an idle, a combat technique, a hit reaction) that cross-fade at
/// the CONSTANTS <c>animation blend rate</c>. Cold state of <see cref="CharacterCold"/>, changed only by <see cref="AnimationSystem"/>
/// in the character's own partition, hashed, and published as <see cref="AnimationLayer"/>s.
/// </summary>
public sealed class CharacterAnimation
{
    /// <summary>Room for the layers of one character (engine choice: the original's layer lists grow without a cap, <b>Verified</b> <c>@ 1405b5270</c>; this is far above the most a fight needs, 5 in the base game).</summary>
    public const int MaxLayers = 16;

    /// <summary>One clip in play: the library index, its own time (seconds; unsynched clips only), its weight and the weight it is fading to.</summary>
    public struct Layer
    {
        public int Definition;
        public float Time, Weight, Target;
        /// <summary>The clip's length in seconds as the character's skeleton has it (set while the time advances; derived, not hashed): the sound events' fractions need it.</summary>
        public float Length;
    }

    public readonly Layer[] Layers = new Layer[MaxLayers];
    public int Count;
    /// <summary>Phase of the synched movement clips in cycles (0..1): the lower and the upper body, and a walk fading into a run, stay in step.</summary>
    public float Phase;
    /// <summary>Cycles per second the phase advances: written whenever the lower movement blend is picked (<b>Verified</b> <c>@ 14051d8e0</c>, <c>@ 14051d000</c>), kept while no such pick runs (standing out of combat, a technique): the legs go on stepping through their fade-out.</summary>
    public float PhaseRate;
    /// <summary>The idle chosen, and the seconds it still plays before another is chosen (-1 / 0: none yet).</summary>
    public int Idle = -1;
    public float IdleLeft;
    /// <summary>A one-shot clip of the whole body that plays to its end (a hit reaction, the fall, getting up): the library index (-1: none), its time in seconds, and whether it is a hit reaction.</summary>
    public int Action = -1;
    public float ActionTime;
    public bool ActionIsStumble;
    /// <summary>Whether a clip it plays has <c>disables movement</c> (a bow, a reload, getting up): the movement system holds it still, its path kept. Derived, not hashed.</summary>
    public bool HoldsStill;
    /// <summary>The tick of the last hit a reaction was chosen for (-1: none), and whether the character was down (knocked out or dead) the last tick.</summary>
    public int SeenHit = -1;
    public bool WasDown;
    /// <summary>Whether the character was crawling from a leg down (posture 2) the last tick: its end starts the get-up (docs/game/ragdolls.md "Getting up").</summary>
    public bool WasCrawling;
    /// <summary>The body's playback factor F = 2 - H (H the skeleton's movement scale; docs/animation.md "Movement"); 0 until the first tick sets it.</summary>
    public float Rate;
    /// <summary>The last <see cref="SpeedWindow"/> speeds, oldest overwritten first (<see cref="SmoothSpeed"/>).</summary>
    public readonly float[] Speeds = new float[SpeedWindow];
    public int SpeedCount;
    public const int SpeedWindow = 8, SpeedTrim = 3;
    /// <summary>
    /// Side steps (docs/animation.md "Side steps"): whether the fighter is in its side-stepping state, the seconds before that state may flip again, its side speed (units per
    /// second, positive to its right; 0 out of the state) and the share of the legs the side-step clip has.
    /// </summary>
    public bool Strafing;
    public float StrafeTimer, StrafeSpeed, StrafeShare;
    /// <summary>The facing the body is drawn at (radians, as <c>CharacterHot.Yaw</c>) and whether it is set: it chases the character's facing at a limited rate (<see cref="AnimationSystem.FollowFacing"/>). Derived for drawing, not hashed.</summary>
    public float DrawYaw;
    public bool HasDrawYaw;

    /// <summary>
    /// The speed the animations see (docs/animation.md "Movement", <b>Verified</b>): of the last 8 samples, sorted, the 3 lowest and 3 highest dropped and the
    /// middle two averaged (fewer samples at the start: the trimmed middle of those there are). Kenshi samples every frame; here every tick, so the window
    /// is <paramref name="window"/> ticks (<see cref="WindowFor"/>: as many seconds as 8 frames at 60 fps) with the trim scaled alike (engine).
    /// </summary>
    public float SmoothSpeed(float raw, int window = SpeedWindow)
    {
        window = Math.Clamp(window, 1, SpeedWindow);
        Speeds[SpeedCount++ % window] = raw;
        int n = Math.Min(SpeedCount, window);
        Span<float> sorted = stackalloc float[SpeedWindow];
        Speeds.AsSpan(0, n).CopyTo(sorted);
        sorted = sorted[..n];
        sorted.Sort();
        int trim = Math.Min(SpeedTrim * window / SpeedWindow, (n - 1) / 2);
        float sum = 0;
        for (int i = trim; i < n - trim; i++) sum += sorted[i];
        return sum / (n - 2 * trim);
    }

    /// <summary>The window of <see cref="SmoothSpeed"/> for a tick of <paramref name="dt"/> seconds: the 8 samples of 60 fps frames in ticks (4 at 30 Hz).</summary>
    public static int WindowFor(float dt) => Math.Clamp((int)MathF.Round(SpeedWindow / (60 * dt)), 1, SpeedWindow);

    internal void Hash(ref StateHasher h)
    {
        h.Add(Count);
        for (int i = 0; i < Count; i++)
        {
            h.Add(Layers[i].Definition);
            h.Add(Layers[i].Time);
            h.Add(Layers[i].Weight);
            h.Add(Layers[i].Target);
        }
        h.Add(Phase);
        h.Add(PhaseRate);
        h.Add(Idle);
        h.Add(IdleLeft);
        h.Add(Action);
        h.Add(ActionTime);
        h.Add(ActionIsStumble);
        h.Add(SeenHit);
        h.Add(WasDown);
        h.Add(WasCrawling);
        h.Add(Rate);
        h.Add(SpeedCount);
        foreach (float s in Speeds) h.Add(s);
        h.Add(HasDrawYaw);
        h.Add(DrawYaw);   // the sight cone reads it (SightMap)
        if (Strafing || StrafeTimer != 0 || StrafeSpeed != 0 || StrafeShare != 0) { h.Add(0x53747266); h.Add(Strafing); h.Add(StrafeTimer); h.Add(StrafeSpeed); h.Add(StrafeShare); }   // only once set, so worlds without fights hash as before
    }
}

/// <summary>
/// Picks each character's animation layers from its movement, stance and fight and advances their times in game time (docs/simulation.md
/// "Animation", the original's rules in docs/animation.md). Runs after movement and combat, in the Act phase, and writes only its own characters'
/// cold animation state and, for root motion, their position.
/// <list type="bullet">
/// <item><b>Verified</b> (decompilation, docs/animation.md): every movement clip that fits plays at once, weighted by a sigmoid of its speed ramp and its
/// leg ramp, normalised per body half; a synched clip's phase advances F x v x the weighted <c>play speed</c> cycles per second, an unsynched one F x
/// <c>play speed</c> x v clip seconds per second; weights move linearly towards their targets; hit reactions are chosen by <c>stumbles</c>,
/// <c>big stumble</c> and <c>stumble from</c>; <c>relocates</c> clips, attacks and dodges move the character by their <c>Bip01</c> track; getting up
/// plays <c>standing up 3</c> to 86 %.</item>
/// <item><b>Engine choice</b>: the blend rate is the same for every clip; in combat a standing character holds an upper-body guard idle over the
/// lower movement blend; techniques cut a hit reaction short; a character going down without a ragdoll (<see cref="Ragdolls"/>) falls with <c>dodgefall</c>; down, it lies in <c>sleeponfloor</c>
/// (under a ragdoll, the bones without a body); a character counts as moving from <see cref="MovingSpeed"/> units per second.</item>
/// </list>
/// </summary>
public sealed class AnimationSystem(AnimationLibrary library, AnimationLengths lengths, float blendRate = 4, CombatSystem? combat = null) : ITickSystem, IAnimationSource
{
    /// <summary>Speed below which a character stands: the original stands only at a filtered speed of exactly 0 (docs/animation.md); this only filters float noise.</summary>
    public const float MovingSpeed = 0.05f;
    /// <summary>Getting up ends when <c>standing up 3</c> is this far through (<b>Verified</b>, <c>Task_GetUp</c>).</summary>
    public const float GetUpEnd = 0.86f;

    public AnimationLibrary Library { get; } = library;
    public AnimationLengths Lengths { get; } = lengths;
    /// <summary>The CONSTANTS <c>animation blend rate</c> (fcs.def default 4, "1 is very slow"): weight per second.</summary>
    public float BlendRate { get; } = blendRate;
    /// <summary>The ragdolls (null: none; the fall and lying clips stand in for them). Read in the parallel phase: the last tick's state.</summary>
    public Meitou.Simulation.Ragdolls.IRagdollState? Ragdolls { get; set; }

    /// <summary>The clip a knocked-out or dead character lies in: <c>sleeponfloor</c>, -1 when the data has none.</summary>
    int LyingClip { get; } = library.IndexOf("sleeponfloor");
    /// <summary>The whole-body clips held while a character bandages a patient (<c>medic</c>) or rests (<c>sitting idle</c>); -1 when the data has none. A held clip like the lying one (engine choice).</summary>
    int TreatingClip { get; } = library.IndexOf("medic");
    int RestingClip { get; } = library.IndexOf("sitting idle");
    // The chairs' sitting clips by record name (looked up on every worker).
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> sitClips = new(StringComparer.Ordinal);
    int SitClipOf(string name) => sitClips.GetOrAdd(name, Library.IndexOf);
    /// <summary>The whole-body clip held while a character takes a building apart (<c>stonemine</c>, <b>Verified</b>: <c>Task_Dismantle</c>); -1 when the data has none.</summary>
    int DismantlingClip { get; } = library.IndexOf("stonemine");
    /// <summary>The carrier's lift clip (<c>shoulder lift left</c>, played backwards to put down) and a carried body's held pose (<c>carry me</c>, a category 3 record the library appends); -1 when the data has none.</summary>
    int LiftClip { get; } = library.IndexOf(Carrying.CarrySystem.LiftClip);
    /// <summary>The overlay that holds a carrier's arm on the body (<c>carrying left arm only</c>: <c>override L arm</c>, every other bone deleted): <b>Verified</b>, <c>FUN_140520000</c> plays it every frame a character carries.</summary>
    int CarryArmClip { get; } = library.IndexOf("carrying left arm only");
    int KnockoutClip { get; } = library.IndexOf(Carrying.CarrySystem.KnockoutClip);
    int CarriedClip { get; } = library.IndexOf(Carrying.CarrySystem.CarriedClip);

    // Building, delivering materials and prospecting play the medic clip (Verified, Task_Build, Task_AddMaterialsToBuilding and Task_Prospecting).
    // Resting on a chair plays the clip its functionality names ("sitting chair"), else sitting on the floor.
    int HeldClip(CharacterTask task, CharacterCold cold) => task switch
    {
        CharacterTask.Treat or CharacterTask.Build or CharacterTask.DeliverMaterials or CharacterTask.Prospect => TreatingClip,
        // An NPC looting a body it has reached (Observed, Task_Loot_AI) or a forager waiting at one (Task_Loot_ResourcesWeHaveStorageFor); the player's own take is instant.
        CharacterTask.Loot when (!cold.IsPlayer || cold.Foraging) && cold.LiftTime >= 0 => TreatingClip,
        CharacterTask.Rest => cold.Ai?.SitClip is { } sit && SitClipOf(sit) is >= 0 and var chair ? chair : RestingClip,
        CharacterTask.Dismantle => DismantlingClip,
        // An operator at work plays its functionality's clip (Observed, Task_OperateMachine; stonemine without one).
        CharacterTask.Operate => cold.Operating ? cold.OperateClip is { } clip && SitClipOf(clip) is >= 0 and var used ? used : DismantlingClip : -1,
        // In a bed: the clip its functionality names (Observed, Task_UseBed: sleepinbed, sleeponfloor), else lying on the floor.
        CharacterTask.Sleep => cold.InSomething != Locks.HeldState.Bed ? -1 : cold.OperateClip is { } bed && SitClipOf(bed) is >= 0 and var lain ? lain : LyingClip,
        // A captor killing, eating or processing a captive (Task_KillPrisoner, Task_EatPrisoner, Task_ProcessNewSlave, Observed).
        CharacterTask.Idle when cold.ActionClip is { } act && SitClipOf(act) is >= 0 and var acting => acting,
        _ => -1,
    };
    // The combat system asks this system for its hit reaction clips (it owns the stun, this system the clips).
    readonly bool stumbleHook = Hook(combat, library, lengths);

    static bool Hook(CombatSystem? combat, AnimationLibrary library, AnimationLengths lengths)
    {
        if (combat is null) return false;
        combat.StumbleChooser = (cold, part, big, side, roll) => ChooseStumble(library, lengths, cold, part, big, side, roll);
        int getUp = library.IndexOf("standing up 3");
        combat.GetUpClipSeconds = getUp >= 0 ? lengths.Of(library.Definitions[getUp].Clip) * GetUpEnd / (library.Definitions[getUp].PlaySpeed > 0 ? library.Definitions[getUp].PlaySpeed : 1) : 0;
        return true;
    }

    // The fall (without a ragdoll) and getting up.
    readonly int fall = library.IndexOfAny("Dodge back fall"), getUp = library.IndexOf("standing up 3");
    // The crossbow clips (RANGED records, played by the combat system's timing).
    readonly int aimClip = library.IndexOf(AnimationLibrary.RangedAimRecord), defaultReload = library.IndexOf(AnimationLibrary.RangedReloadRecord);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> reloadClips = new(StringComparer.Ordinal);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> stanceClips = new(StringComparer.Ordinal);

    /// <summary>The character's own idle stance (its appearance's <c>idle stance</c>, an ANIMATION record name), -1 when it has none or the data lacks it.</summary>
    int OwnStance(CharacterCold cold) => cold.Appearance?.Body?.IdleStance is { } name ? stanceClips.GetOrAdd(name, static (key, lib) => lib.IndexOf(key), Library) : -1;

    /// <summary>The reload clip of the character's crossbow: its record's <c>reload anim</c>, else <c>reload 1 phase</c>.</summary>
    int ReloadClip(CharacterCold cold) => cold.Fighter?.Crossbow is { Data.ReloadAnimId: { Length: > 0 } id }
        ? reloadClips.GetOrAdd(id, static (key, lib) => lib.IndexOfRecord(key), Library) is >= 0 and var k ? k : defaultReload
        : defaultReload;

    // Scratch lists of the thread running a partition, kept between ticks (Update clears them on entry).
    [ThreadStatic] static List<(int Index, float Weight)>? lowerScratch, upperScratch;

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float dt = world.TickSeconds;
        var wantedLower = lowerScratch ??= [];
        var wantedUpper = upperScratch ??= [];
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            var a = cold.Animation ??= new CharacterAnimation();
            var v = next[i].Velocity;
            // The original's animation speed is the displacement the physics step really made (docs/animation.md "Speed the animations see"): a walker held by a wall,
            // a crowd or a body it cannot pass keeps its commanded velocity here, so it is taken no faster than it moved (never faster than commanded).
            float commanded = v.Length();
            if (commanded > 0)
            {
                float moved = MathF.Sqrt((next[i].Position.X - prev[i].Position.X) * (next[i].Position.X - prev[i].Position.X) + (next[i].Position.Z - prev[i].Position.Z) * (next[i].Position.Z - prev[i].Position.Z)) / dt;
                if (moved < commanded) v *= moved / commanded;
            }
            float speed = v.Length();
            bool approaching = Approaching(table, i, cold);
            // In combat the speed is signed along the facing: backing off is negative and picks the clips with a negative move speed; the side speed drives the side steps.
            if (cold.InCombat && !approaching)
            {
                float sin = MathF.Sin(next[i].Yaw), cos = MathF.Cos(next[i].Yaw);
                speed = v.X * sin + v.Z * cos;
                ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.Animation, 0x5354524146UL);
                Strafe(a, speed, v.Z * sin - v.X * cos, StrafeOnRatio(roll), StrafeOffRatio(roll), dt);
            }
            else (a.Strafing, a.StrafeTimer, a.StrafeSpeed, a.StrafeShare) = (false, 0, 0, 0);
            speed = a.SmoothSpeed(speed, CharacterAnimation.WindowFor(dt));
            var id = table.IdOf(i);
            var ragdoll = Ragdolls is { } r ? new RagdollView(r.HasRagdoll(cold), r.WasWhole(id)) : default;
            var root = Update(a, cold, speed, dt, world.Seed, Rng.Key(id), world.Tick, wantedLower, wantedUpper, ViewOf(id, world.Tick, cold, dt), (CharacterTask)next[i].Task, ragdoll, approaching);
            if (root != Vector2.Zero) Relocate(world, i, ref next[i], root);
            FollowFacing(a, next[i].Yaw, cold.InCombat && !approaching && cold.CarriedBy.IsNone, free: cold.CarriedBy.IsNone && cold.InSomething == Locks.HeldState.None && cold.Medical is not { Incapacitated: true }, dt);
        }
    }

    /// <summary>Radians per second the drawn body turns towards the character's facing in the combat stance and out of it (<b>Verified</b>, <c>AnimationClass</c> slot 8 <c>@ 1405b24e0</c>: a half-angle step of 2 x dt, 5 times that unless the stance flag is 1, taken as a rotation by 2 x the step).</summary>
    public const float CombatDrawTurn = 4, DrawTurn = 20;

    /// <summary>
    /// The drawn body follows the facing (<paramref name="yaw"/>) by at most <see cref="CombatDrawTurn"/> (in the combat stance, <paramref name="combat"/>) or <see cref="DrawTurn"/> rad/s,
    /// taking it at once while the character is not free (carried, held, down) or has no drawn facing yet (<b>Verified</b>: the original sets the node directly for an imprisoned
    /// character; engine choice for the other states).
    /// </summary>
    internal static void FollowFacing(CharacterAnimation a, float yaw, bool combat, bool free, float dt)
    {
        if (!a.HasDrawYaw || !free) { a.DrawYaw = yaw; a.HasDrawYaw = true; return; }
        float delta = MathF.IEEERemainder(yaw - a.DrawYaw, MathF.Tau), step = (combat ? CombatDrawTurn : DrawTurn) * dt;
        a.DrawYaw = MathF.Abs(delta) <= step ? yaw : a.DrawYaw + MathF.CopySign(step, delta);
    }

    /// <summary>
    /// Whether a fighter is running at its target out of the combat stance (<see cref="CharacterCold.CombatRunning"/>, set by the pursuit system): it keeps the plain walk
    /// and run clips until it is near (docs/game/combat.md "Combat stance").
    /// </summary>
    internal bool Approaching(CharacterTable table, int slot, CharacterCold cold) =>
        cold.InCombat && cold.CombatRunning && table.Next[slot].Task == (byte)CharacterTask.Attack;

    /// <summary>Seconds the side-stepping state holds after it flips (<c>CombatMovementController::combatMovementUpdate</c>, <b>Observed</b>).</summary>
    public const float StrafeHoldSeconds = 1;

    /// <summary>The side-to-forward ratio over which a fighter starts side-stepping, 1.5 + 3r per character (FORWARD_THRESHOLD, <c>setupCombatMovementVariables</c>, <b>Observed</b>).</summary>
    static float StrafeOnRatio(ulong roll) => 1.5f + 3 * Rng.Float(roll);
    /// <summary>The forward-to-side ratio over which it stops, 1.5 + 2r (STRAFE_THRESHOLD, same source).</summary>
    static float StrafeOffRatio(ulong roll) => 1.5f + 2 * ((roll & 0xFFFFFF) * (1f / (1 << 24)));

    /// <summary>
    /// The side-stepping state of a fighter in its stance (docs/animation.md "Side steps", <b>Observed</b> from <c>combatMovementUpdate</c> and
    /// <c>combatMovementAnimationUpdate</c>): from its <paramref name="forward"/> and <paramref name="side"/> speeds (along its facing, and to its right), it starts when
    /// |side| > <paramref name="on"/> x |forward| and stops when |forward| > <paramref name="off"/> x |side|, at most once per <see cref="StrafeHoldSeconds"/>; in the
    /// state its side speed (0 while it walks straighter than |forward| > 1 with |side| under 0.5) is what the side-step clip plays at.
    /// </summary>
    internal static void Strafe(CharacterAnimation a, float forward, float side, float on, float off, float dt)
    {
        a.StrafeTimer = MathF.Max(a.StrafeTimer - dt, 0);
        if ((forward != 0 || side != 0) && a.StrafeTimer <= 0
            && (a.Strafing ? MathF.Abs(forward) > off * MathF.Abs(side) : MathF.Abs(side) > on * MathF.Abs(forward)))
        {
            a.Strafing = !a.Strafing;
            a.StrafeTimer = StrafeHoldSeconds;
        }
        if (MathF.Abs(forward) > 1 && MathF.Abs(side) < 0.5f) side = 0;
        a.StrafeSpeed = a.Strafing ? side : 0;
    }

    /// <summary>What the combat system says about a character for its animation: the technique playing and its progress, and the last hit taken (the last finished tick's state).</summary>
    CombatView ViewOf(CharacterId id, long tick, CharacterCold cold, float tickSeconds)
    {
        if (combat is null) return CombatView.None;
        var slot = combat.StateOf(id);
        int technique = -1;
        float progress = 0;
        if (combat.Playing(id, tick) is { } playing)
        {
            technique = Library.IndexOfTechnique(playing.Technique.StringId);
            progress = playing.Progress;
        }
        else if (combat.RangedPoseOf(id, tick, tickSeconds) is { } pose)
        {
            // The crossbow (engine choice of clips and timing): aiming raises "aimH" (clip aimshoot) through its first half over the steady time, the shot plays the second
            // half, the reload plays the crossbow's own "reload anim" record (else "reload 1 phase") stretched over the reload time.
            (technique, progress) = pose.Pose switch
            {
                RangedPose.Aim => (aimClip, pose.Progress * 0.5f),
                RangedPose.Shot => (aimClip, 0.5f + pose.Progress * 0.5f),
                _ => (ReloadClip(cold), pose.Progress),
            };
        }
        string? part = slot.HitPart >= 0 && cold.Medical is { } body && slot.HitPart < body.Parts.Count ? body.Parts[slot.HitPart].Template.StringId : null;
        return new CombatView(technique, progress, slot.HitTick, slot.HitHeavy, slot.HitBehind, part, slot.StumbleClip, slot.StumbleStart);
    }

    /// <summary>
    /// Root motion: moves the character by the ground part of its relocating clips' <c>Bip01</c> track (model X and Z turned by its heading), onto
    /// the ground, unless that point is not walkable. The original moves its nav agent, so the walls of the walkable surface stop the body's circle and it slides
    /// along them (<see cref="IWalkability.Slide"/>; docs/game/pathfinding.md "Walls").
    /// The bodies collide too (engine stand-in for the original's avoidance of the characters round it, <c>FUN_1401476f0</c>): the part of the move that would bring it nearer than
    /// the two footprint radii to a standing character (last tick's positions) is dropped, so an attack that gains ground stops at its target instead of walking into it.
    /// A downed, dead, carried, bedded, caged or sitting body is not in the way (<see cref="CombatSteering.OutOfFlock"/>; docs/game/pathfinding.md "Bodies in the way").
    /// </summary>
    internal static void Relocate(World world, int self, ref CharacterHot c, Vector2 root)
    {
        float sin = MathF.Sin(c.Yaw), cos = MathF.Cos(c.Yaw);
        var step = new Vector2(root.X * cos + root.Y * sin, -root.X * sin + root.Y * cos);
        var table = world.Characters;
        var block = new BodyBlock { Table = table, Self = self, From = new Vector2(c.Position.X, c.Position.Z), Step = step, Radius = CombatTuning.Footprint(table.Cold(self)) };
        world.Grid.Query(block.From.X, block.From.Y, block.Radius + MaxFootprint + step.Length(), ref block, static (int other, ref BodyBlock b) =>
        {
            var all = b.Table.Previous;
            if (other == b.Self || !all[other].Alive || b.Table.Cold(other) is not { } them || CombatSteering.OutOfFlock(all[other], them)) return;
            var away = b.From - new Vector2(all[other].Position.X, all[other].Position.Z);
            float reach = b.Radius + CombatTuning.Footprint(b.Table.Cold(other));
            if ((away + b.Step).LengthSquared() >= reach * reach) return;
            // It would end up inside: keep only the part of the step that does not close the distance.
            float length = away.Length();
            if (length < 1e-4f) return;
            var normal = away / length;
            float closing = Vector2.Dot(b.Step, normal);
            if (closing < 0) b.Step -= normal * closing;
        });
        // The walls stop it as they stop the original's agent, which this move goes through: its circle slides along the first wall it meets (docs/game/pathfinding.md "Walls").
        var walk = world.Walkability;
        var moved = walk.Slide(c.Position, block.Step, block.Radius);
        float x = c.Position.X + moved.X, z = c.Position.Z + moved.Y;
        if (!walk.IsWalkable(x, z, c.Position.Y)) return;
        c.Position = new Vector3(x, walk.GroundHeight(x, z, c.Position.Y), z);
    }

    /// <summary>The largest footprint radius a body is tested against (a human's is 4; the biggest animals' are larger).</summary>
    const float MaxFootprint = 30;

    struct BodyBlock
    {
        public CharacterTable Table;
        public int Self;
        public Vector2 From, Step;
        public float Radius;
    }

    /// <summary>
    /// One tick of one character's animation (public for tests). <paramref name="speed"/> is in units per second (signed along the facing in
    /// combat). Returns how far its relocating clips moved it this tick, in model space (X, Z; Z forward), weighted by their weights.
    /// </summary>
    public Vector2 Update(CharacterAnimation a, CharacterCold cold, float speed, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)>? lowerScratch = null, List<(int Index, float Weight)>? upperScratch = null, CombatView? combatView = null, CharacterTask task = CharacterTask.Idle,
        RagdollView ragdoll = default, bool approaching = false)
    {
        var fight = combatView ?? CombatView.None;
        var lower = lowerScratch ?? [];
        var upper = upperScratch ?? [];
        lower.Clear();
        upper.Clear();
        if (Library.Animal(cold.RecordId) is { } animal) return UpdateAnimal(a, cold, PlanOf(animal), speed, dt, seed, key, tick, lower, fight, ragdoll);
        if (a.Rate == 0) a.Rate = cold.Appearance is { } look ? 2 - CharacterShape.MovementScaleOf(look) : 1;
        // Carrying picks the "carrying left" clips and a carried body the "being carried" ones (the ANIMATION booleans, docs/animation.md).
        bool carried = !cold.CarriedBy.IsNone;
        var stance = StanceOf(cold) with { Combat = cold.InCombat && !approaching };
        bool down = cold.Medical is { Incapacitated: true };
        bool crawling = !carried && cold.Medical is { Prone: true };
        if (HoldsForRagdoll(a, down, carried, ragdoll)) return Vector2.Zero;

        bool inBed = cold.InSomething == Locks.HeldState.Bed, inCage = cold.InSomething == Locks.HeldState.Imprisoned;
        if (down && !a.WasDown && !carried && !ragdoll.Has && !inBed) StartAction(a, fall, false);   // a ragdoll falls instead; one in a bed lies on
        if (carried) a.Action = -1;   // held on the shoulder: no falling or getting up
        // Waking with a leg down there is nothing to get up on, and one staying low does not stand: the original's get-up ends with the bone blend and the posture's idle
        // follows (docs/game/ragdolls.md "Getting up"). Waking in a bed it lies on (engine choice).
        else if (!down && a.WasDown && (crawling || inBed || cold.Medical is { StayingLow: true })) a.Action = -1;
        else if (!down && a.WasDown)
        {
            StartAction(a, getUp, false);
            // Out of a ragdoll the clip waits at its first key while the bones blend into it (docs/game/ragdolls.md "Getting up").
            if (ragdoll.WasWhole) a.ActionTime = -Meitou.Simulation.Ragdolls.RagdollSystem.GetUpBlend;
        }
        // The leg back up: posture 2 ends in 0 and the posture setter queues the get-up task 65 (FUN_1405c7e20), which plays `standing up 3`.
        else if (a.WasCrawling && !down && !carried && !inBed && cold.Medical is { Crawling: false, Prone: false }) StartAction(a, getUp, false);
        a.WasDown = down;
        a.WasCrawling = cold.Medical is { Crawling: true };
        // The hit reaction the combat system picked (it holds the character helpless for the clip's length; see ChooseStumble).
        if (!down && !carried && fight.StumbleClip >= 0 && fight.StumbleStart > a.SeenHit)
        {
            a.SeenHit = fight.StumbleStart;
            StartAction(a, fight.StumbleClip, true);
        }
        int technique = down || carried ? -1 : fight.Technique;   // a carried body plays nothing but its held pose
        bool fullTechnique = technique >= 0 && Library.Definitions[technique].Area == AnimationArea.All;
        // A technique cuts a reaction or getting up short, and so does walking off (except a hit reaction, which moves the character itself).
        if (a.Action >= 0 && !down && (technique >= 0 || MathF.Abs(speed) >= MovingSpeed && !a.ActionIsStumble)) a.Action = -1;
        if (a.Action >= 0)
        {
            var d = Library.Definitions[a.Action];
            a.ActionTime += dt * (d.PlaySpeed > 0 ? d.PlaySpeed : 1);
            if (a.ActionTime >= Lengths.Of(d.Clip) * (a.Action == getUp ? GetUpEnd : 1) && !(down && a.Action == fall && LyingClip < 0)) a.Action = -1;
        }

        var wanted = lower;
        bool moving = false;
        int legs = 0;   // the lower movement clips at the head of the list (the phase follows them)
        float legsShare = 1;   // their share of the legs (less while a side step plays)
        if (a.Action >= 0) wanted.Add((a.Action, 1));
        // A carried body, awake or not: the attach (FUN_1405ced90) plays "carry me" by name as the character's action, and the update then plays that action alone (FUN_140520500,
        // docs/game/carry-loot.md "A carried body"), so no movement, idle or technique pick runs.
        else if (carried && CarriedClip >= 0) { wanted.Add((CarriedClip, 1)); a.Idle = -1; }
        else if (down)
        {
            // Down in a bed: its clip (the sleeper's), so it lies where a sleeper lies.
            if (inBed && HeldClip(CharacterTask.Sleep, cold) is >= 0 and var lain) wanted.Add((lain, 1));
            // Out cold in a cage: its `animation KO` (the Cage's foetal; the out-of-combat pick @ 140520500, docs/animation.md "In a bed or a cage").
            else if (inCage && cold.CageClips.Ko is { } ko && SitClipOf(ko) is >= 0 and var foetal) wanted.Add((foetal, 1));
            else if (LyingClip >= 0) wanted.Add((LyingClip, 1));
            a.Idle = -1;
        }
        // Awake in a cage: its `animation dazed` with a leg down (engine reading of the original's dazed test), else its `animation` (the Cage's stand 1).
        else if (inCage && technique < 0 && (crawling ? cold.CageClips.Dazed : cold.CageClips.Idle) is { } cageClip && SitClipOf(cageClip) is >= 0 and var caged)
        {
            wanted.Add((caged, 1));
            a.Idle = -1;
        }
        // In deep water a race that swims plays its swimming clips (category 4), the ordinary ones where none fits.
        else if (!fullTechnique && technique < 0 && !carried && cold is { WaterState: 3, Race.Swims: true } && Swim(a, stance, speed, lower)) { }
        // A character with a leg down crawls: the grounded clips (category 5), whose lower layer is the whole body as no upper clip plays over it.
        else if (!fullTechnique && crawling && Crawl(a, stance, speed, task, cold, dt, seed, key, tick, lower, out legs)) moving = legs > 0;
        else if (!fullTechnique)
        {
            moving = MathF.Abs(speed) >= MovingSpeed;
            // A block played standing outside the combat stance (a fighter held at a distance, a wait with the running flag): its upper-body clip has no lower-body tracks and
            // no idle plays under a technique, so the legs would stay in the binding pose; they take the combat footwork's lower clip, as in the stance (engine choice).
            bool blockStanding = !moving && !stance.Combat && technique >= 0;
            if (moving || stance.Combat || blockStanding) Library.Movement(AnimationArea.Lower, speed, blockStanding ? stance with { Combat = true } : stance, lower);
            // In combat the upper body plays no movement clip: it holds the guard (the original's combat pick has no upper movement pass, docs/animation.md).
            if (moving && !stance.Combat) Library.Movement(AnimationArea.Upper, speed, stance, upper);
            moving = moving && (lower.Count > 0 || upper.Count > 0);
            if (!moving && !((stance.Combat || blockStanding) && lower.Count > 0)) lower.Clear();
            legs = lower.Count;
            // Side-stepping: the side-step clip takes |side| / (|side| + |forward|) of the legs from the walking clips (none while both together are under 1: standing).
            if (stance.Combat)
            {
                float side = MathF.Abs(a.StrafeSpeed), ahead = MathF.Abs(speed);
                a.StrafeShare = side + ahead > 1 ? side / (side + ahead) : 0;
                if (a.StrafeShare > 0 && Library.Strafe(stance) is >= 0 and var strafe)
                {
                    legsShare = 1 - a.StrafeShare;
                    for (int k = 0; k < legs; k++) lower[k] = (lower[k].Index, lower[k].Weight * legsShare);
                    lower.Add((strafe, a.StrafeShare));
                }
            }
            if (moving)
            {
                wanted.AddRange(upper);
                if (stance.Combat && technique < 0 && Library.CombatGuard(stance, speed) is >= 0 and var guard) { wanted.Add((guard, 1)); a.Idle = -1; }
            }
            else if (technique < 0 && HeldClip(task, cold) is >= 0 and var held) { wanted.Add((held, 1)); a.Idle = -1; }
            else if (technique < 0)
            {
                // A carrier standing: a whole-body idle as if it carried nothing, with the carrying arm pose (stand carrying left) over it (engine choice: the data has no whole-body carrying idle).
                bool carrying = stance.CarryingLeft || stance.CarryingRight;
                var standing = carrying ? stance with { CarryingLeft = false, CarryingRight = false } : stance;
                // A human plays its own idle stance whenever that fits, the random idles only otherwise (the original's idle pick, docs/animation.md "Idle stance").
                if (lower.Count == 0 && OwnStance(cold) is >= 0 and var own && Library.Definitions[own].FitsAsStance(standing))
                {
                    (a.Idle, a.IdleLeft) = (own, 0);
                    wanted.Add((own, 1));
                }
                else if (stance.Combat && lower.Count > 0)
                {
                    if (Library.CombatGuard(standing, speed) is >= 0 and var guard) wanted.Add((guard, 1));
                    a.Idle = -1;
                }
                else StandingIdle(a, standing, lower.Count > 0, dt, seed, key, tick, wanted);
            }
        }
        if (technique >= 0)
        {
            // An attack or a dodge is the whole body; a block the upper body over the legs' clips.
            if (fullTechnique) wanted.Clear();
            wanted.Add((technique, 1));
        }
        // A carrier (FUN_140520000, the arm overlay update every frame): the carrying arm's overlay clip whatever else plays, a technique included (docs/animation.md "A carrier").
        // A character that carries nothing and stands holds its hurt parts (FUN_140520000 -> FUN_14051fe60, docs/animation.md "Injury holds").
        if (!down && !carried)
        {
            if (stance.CarryingLeft && CarryArmClip >= 0) wanted.Add((CarryArmClip, 1));
            else if (!stance.CarryingLeft && !stance.CarryingRight && !crawling && speed <= PainHoldMaxSpeed && !(cold is { WaterState: 3, Race.Swims: true })
                && PainHold(a, cold, stance, wanted) is >= 0 and var hold) wanted.Add((hold, 1));
        }
        // Lifting or putting down (the carry system's timing): the whole-body lift clip, forwards for a pick-up, backwards from its end for a put-down.
        int lift = -1;
        float liftTime = 0;
        if (!down && !carried && LiftClip >= 0 && cold.LiftTime >= 0 && task is CharacterTask.PickUp or CharacterTask.PutDown)
        {
            float length = Lengths.Of(Library.Definitions[LiftClip].Clip);
            lift = LiftClip;
            float played = cold.LiftTime * (Library.Definitions[LiftClip].PlaySpeed > 0 ? Library.Definitions[LiftClip].PlaySpeed : 1);   // the record's play speed (2)
            liftTime = task == CharacterTask.PickUp ? MathF.Min(played, length) : MathF.Max(length - played, 0);
            wanted.Clear();
            wanted.Add((lift, 1));
            legs = 0;   // the lift is the whole body: no walking clips left to synch
            a.Idle = -1;
        }
        // A stealth knock-out swing (the carry system's timing): the whole-body knockout clip at x1.2.
        else if (!down && !carried && KnockoutClip >= 0 && cold.LiftTime >= 0 && task == CharacterTask.StealthKo)
        {
            lift = KnockoutClip;
            liftTime = MathF.Min(cold.LiftTime * Carrying.CarrySystem.KnockoutPlaySpeed, Lengths.Of(Library.Definitions[KnockoutClip].Clip));
            wanted.Clear();
            wanted.Add((lift, 1));
            legs = 0;   // the lift is the whole body: no walking clips left to synch
            a.Idle = -1;
        }

        return Settle(a, wanted, legs, lift, liftTime, technique, fight, speed, dt, Lengths, 1, seed, key, tick, legsShare);
    }

    /// <summary>
    /// The swimming clips to play (<see cref="AnimationLibrary.SwimMovement"/> while it moves, else <see cref="AnimationLibrary.SwimIdle"/>) added to <paramref name="wanted"/>; false, with
    /// nothing added, when the data has none that fit. They are unsynched whole-body clips, so there is no walking phase to follow (engine choice; which clip the original plays, <c>FUN_1405c7fd0</c> level 3, is <b>Verified</b> only for getting up).
    /// </summary>
    bool Swim(CharacterAnimation a, in AnimationStance stance, float speed, List<(int Index, float Weight)> wanted)
    {
        if (MathF.Abs(speed) >= MovingSpeed)
        {
            Library.SwimMovement(speed, stance, wanted);
            if (wanted.Count > 0) { a.Idle = -1; return true; }
        }
        if (Library.SwimIdle(stance) is >= 0 and var idle)
        {
            wanted.Add((idle, 1));
            a.Idle = -1;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The crawling clips to play added to <paramref name="wanted"/> (engine choice of the pick; the original's posture and category 5 are <b>Observed</b>): moving, <see cref="AnimationLibrary.CrawlMovement"/>
    /// (<paramref name="legs"/> its count, the synched phase follows it); else <c>crawl use</c> for the tasks that kneel to work (the original's medic clip becomes it, docs/game/locks.md), else a
    /// <see cref="AnimationLibrary.CrawlIdles"/> draw kept for its idle time. False, with nothing added, when the data has no crawl clip that fits (a weapon in the left hand).
    /// </summary>
    bool Crawl(CharacterAnimation a, in AnimationStance stance, float speed, CharacterTask task, CharacterCold cold, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)> wanted, out int legs)
    {
        legs = 0;
        if (MathF.Abs(speed) >= MovingSpeed)
        {
            Library.CrawlMovement(speed, stance, wanted);
            if (wanted.Count > 0) { a.Idle = -1; legs = wanted.Count; return true; }
        }
        if (HeldClip(task, cold) is >= 0 and var held && held == TreatingClip && CrawlUseClip >= 0)
        {
            wanted.Add((CrawlUseClip, 1));
            a.Idle = -1;
            return true;
        }
        var idles = Library.CrawlIdles(stance);
        if (idles.Count == 0) return false;
        StandingIdle(a, idles, dt, seed, key, tick, wanted);
        return wanted.Count > 0;
    }

    /// <summary>The kneeling work clip of a crawler (<c>crawl use</c>, category 5); -1 when the data has none.</summary>
    int CrawlUseClip { get; } = library.IndexOf("crawl use");

    /// <summary>
    /// The layers' targets from <paramref name="wanted"/>, the synched phase (from the first <paramref name="legs"/> entries), the weights faded and the times advanced, with
    /// the clip lengths of <paramref name="lengths"/>; returns the root motion of the relocating clips times <paramref name="rootScale"/>.
    /// </summary>
    Vector2 Settle(CharacterAnimation a, List<(int Index, float Weight)> wanted, int legs, int lift, float liftTime, int technique, in CombatView fight, float speed, float dt,
        AnimationLengths lengths, float rootScale, ulong seed, ulong key, long tick, float legsShare = 1)
    {
        a.HoldsStill = false;
        foreach (var (index, _) in wanted) if (Library.Definitions[index].DisablesMovement) { a.HoldsStill = true; break; }
        // Targets: what is wanted fades in, everything else out. The first clips a character ever plays start at full weight (no fade from the binding pose).
        bool first = a.Count == 0;
        // A looping clip that starts on a character joins at its own point of the loop, and a character's first steps at its own phase, so a squad or herd made on one
        // tick does not idle, graze and walk in lockstep (engine choice: docs/simulation.md "Animation"). One-shot clips (actions, techniques, a lift) start at their beginning.
        if (first) a.Phase = Rng.Float(Rng.Hash(seed, key, RngPurpose.Animation, (ulong)tick ^ 0x5048415345UL));
        for (int k = 0; k < a.Count; k++) a.Layers[k].Target = 0;
        foreach (var (index, weight) in wanted)
        {
            int k = Find(a, index);
            if (k < 0)
            {
                k = a.Count < CharacterAnimation.MaxLayers ? a.Count++ : Evict(a);
                if (k < 0) continue;
                var d = Library.Definitions[index];
                // A clip that never advances (the stances: play speed 0) holds its first frame, as the editor and the portraits show it.
                float start = d.Loop && !d.IsAction && !d.Technique && index != a.Action && index != lift && (d.PlaySpeed > 0 || d.MoveSpeed != 0)
                    ? Rng.Float(Rng.Hash(seed, key, RngPurpose.Animation, (ulong)tick ^ ((ulong)index << 32) ^ 0x4C4F4F50UL)) * lengths.Of(d.Clip) : 0;
                a.Layers[k] = new CharacterAnimation.Layer { Definition = index, Weight = first ? weight : 0, Time = start };
            }
            a.Layers[k].Target = weight;
        }

        // The phase advances F x v x the weighted play speed of all the lower movement candidates (synched or not), in cycles per second; the rate is written only when that blend is
        // picked and kept otherwise (Verified, @ 14051d8e0 / @ 14051d000), so a walk fading out after a stop goes on stepping at its last rate.
        if (legs > 0)
        {
            float cycles = 0;
            for (int k = 0; k < legs; k++)
            {
                var (index, weight) = wanted[k];
                cycles += weight * Library.Definitions[index].PlaySpeed;
            }
            if (legsShare is > 0 and < 1) cycles /= legsShare;   // a side step thins the walking clips, not their stride
            a.PhaseRate = a.Rate * speed * cycles;
        }
        a.Phase += a.PhaseRate * dt;
        a.Phase -= MathF.Floor(a.Phase);

        int kept = 0;
        var root = Vector2.Zero;
        for (int k = 0; k < a.Count; k++)
        {
            ref var layer = ref a.Layers[k];
            var d = Library.Definitions[layer.Definition];
            float step = BlendStep(d, layer.Target > 0, a.Rate, speed) * dt;
            layer.Weight = layer.Weight < layer.Target ? MathF.Min(layer.Weight + step, layer.Target) : MathF.Max(layer.Weight - step, layer.Target);
            float before = layer.Time;
            float length = lengths.Of(d.Clip);
            layer.Length = length;
            bool synched = d.Synchs && d.MoveSpeed != 0;
            if (layer.Definition == lift) layer.Time = liftTime;
            else if (layer.Definition == technique) layer.Time = fight.Progress * length;   // the combat system's timing: the blow lands at its frame
            else if (layer.Definition == a.Action) layer.Time = MathF.Max(a.ActionTime, 0);   // held at the start while getting up blends out of a ragdoll
            else if (IsStrafe(d)) layer.Time = Wrap(layer.Time + dt * a.Rate * d.PlaySpeed * a.StrafeSpeed, length);   // signed: a step the other way plays it backwards
            else if (synched) layer.Time = (a.Phase + d.SynchOffset - MathF.Floor(a.Phase + d.SynchOffset)) * length;
            else if (d.MoveSpeed != 0) layer.Time += dt * a.Rate * d.PlaySpeed * speed;
            else layer.Time += dt * (d.PlaySpeed > 0 ? d.PlaySpeed : d.Technique ? 1 : 0);
            if (!d.Loop && !synched && !IsStrafe(d)) layer.Time = Math.Clamp(layer.Time, 0, length * 0.999f);   // a one-shot clip holds its last frame
            if (d.Relocates && layer.Time > before) root += (lengths.Root(d.Clip, layer.Time) - lengths.Root(d.Clip, before)) * layer.Weight;
            if (layer.Weight <= 0 && layer.Target <= 0) continue;   // faded out: dropped
            a.Layers[kept++] = layer;
        }
        a.Count = kept;
        return root * rootScale;
    }

    /// <summary>A side-step clip (<see cref="AnimationLibrary.Strafe"/>): its time follows the side speed.</summary>
    static bool IsStrafe(AnimationDefinition d) => d.Area == AnimationArea.Lower && d.MoveSpeed == 0 && d.StrafeSpeedMax > 0 && !d.Technique;

    static float Wrap(float time, float length) => length > 0 ? time - MathF.Floor(time / length) * length : 0;

    /// <summary>
    /// How fast a layer's weight moves, per second (docs/animation.md "Layer managers"): the CONSTANTS blend rate times its body half's factor (lower 2, upper and whole
    /// body 1.25, overlay 1); a wanted layer moves at that times its play rate clamped to 0.5..10, one fading out at the plain rate.
    /// </summary>
    float BlendStep(AnimationDefinition d, bool wanted, float bodyRate, float speed)
    {
        float half = d.Area switch { AnimationArea.Lower => LowerBlend, AnimationArea.Overlay => OverlayBlend, _ => UpperBlend };
        if (!wanted) return BlendRate * half;
        // The play rate in clip seconds per second (a movement clip's follows the speed); a technique, an action or a lift runs on its own clock, taken as 1.
        float play = d.MoveSpeed != 0 ? bodyRate * d.PlaySpeed * speed : d.PlaySpeed > 0 ? d.PlaySpeed : d.Technique || d.IsAction ? 1 : 0;
        return BlendRate * half * Math.Clamp(play, MinPlayBlend, MaxPlayBlend);
    }

    /// <summary>The layer managers' blend factors (<b>Verified</b>, <c>@ 1406216b0</c>) and the clamp of a wanted layer's play rate (<c>@ 1405b5c30</c>).</summary>
    const float LowerBlend = 2, UpperBlend = 1.25f, OverlayBlend = 1, MinPlayBlend = 0.5f, MaxPlayBlend = 10;

    /// <summary>An animal's clips with those its skeleton lacks taken out, and the fall and lying clip chosen (<see cref="PlanOf"/>).</summary>
    sealed record AnimalPlan(int[] Movement, IReadOnlyList<(int Index, float Weight)> Idles, int Fall, int Lying, AnimationLengths Lengths);

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, AnimalPlan> animalPlans = new(StringComparer.Ordinal);

    /// <summary>
    /// The plan of an animal's clip set: the race's skeleton lengths (<see cref="AnimationLengths.For"/>), and when that skeleton was read, only the clips it has (a record whose
    /// clip the skeleton lacks would pose nothing: the skimmer's <c>idle1</c>); the first fall and lying candidates it has.
    /// </summary>
    AnimalPlan PlanOf(AnimalClips clips) => animalPlans.GetOrAdd(clips.RecordId, static (_, s) =>
    {
        var (self, c) = s;
        var lengths = self.Lengths.For(c.RaceId);
        bool known = c.RaceId is { } race && self.Lengths.HasSpecies(race);
        bool Has(int i) => !known || lengths.Has(self.Library.Definitions[i].Clip);
        int First(int[] list) { foreach (int i in list) if (Has(i)) return i; return -1; }
        return new AnimalPlan([.. c.Movement.Where(Has)], [.. c.Idles.Where(x => Has(x.Index))], First(c.Fall), First(c.Lying), lengths);
    }, (this, clips));

    /// <summary>
    /// One tick of an animal's animation (docs/simulation.md "Animation", animals): its own clips only. Moving, the movement clips of its list by speed (unsynched: F x
    /// <c>play speed</c> x v clip seconds per second, F = 1 / its size: the humanoid F = (2 - H) / g with H = 1 and g the size, an <b>engine reading</b>); standing (in a
    /// fight too), one of its idles; a creature technique the combat system plays, over everything; down, its <c>gotosleep</c> once, then <c>sleep</c> (or the fall's last
    /// frame held); no hit reactions (no animal record has <c>stumbles</c>) and no getting up (none exists: it fades back to standing). Root motion of an attack is scaled by
    /// the size.
    /// </summary>
    Vector2 UpdateAnimal(CharacterAnimation a, CharacterCold cold, AnimalPlan plan, float speed, float dt, ulong seed, ulong key, long tick, List<(int Index, float Weight)> wanted, in CombatView fight, RagdollView ragdoll)
    {
        float size = cold.Medical?.ScaleK ?? 1;
        if (!(size > 0.01f)) size = 1;
        if (a.Rate == 0) a.Rate = 1 / size;
        bool carried = !cold.CarriedBy.IsNone;
        bool down = cold.Medical?.Incapacitated ?? false;
        if (HoldsForRagdoll(a, down, carried, ragdoll)) return Vector2.Zero;
        if (down && !a.WasDown && !carried && !ragdoll.Has) StartAction(a, plan.Fall, false);
        if (carried || !down) a.Action = -1;   // the fall is an animal's only one-shot clip
        a.WasDown = down;
        int technique = down ? -1 : fight.Technique;
        if (a.Action >= 0)
        {
            var d = Library.Definitions[a.Action];
            a.ActionTime += dt * (d.PlaySpeed > 0 ? d.PlaySpeed : 1);
            if (a.ActionTime >= plan.Lengths.Of(d.Clip) && plan.Lying >= 0) a.Action = -1;   // without a lying clip the fall's last frame is held
        }
        wanted.Clear();
        float v = MathF.Abs(speed);
        if (a.Action >= 0) wanted.Add((a.Action, 1));
        else if (down && plan.Lying >= 0)
        {
            wanted.Add((plan.Lying, 1));
            a.Idle = -1;
        }
        else if (technique >= 0) wanted.Add((technique, 1));
        else
        {
            // Down without any lying clip (the crab, the robot spiders, gorillos: their skeletons have none) it stays in its idle rather than the binding pose.
            if (v >= MovingSpeed && !down) Library.Movement(plan.Movement, v, wanted);
            if (wanted.Count == 0)
            {
                bool fresh = a.Idle < 0 || a.IdleLeft <= 0;
                StandingIdle(a, plan.Idles, dt, seed, key, tick, wanted);
                // A one-shot idle (the bonedog's "idle noloop", 2.5 s) is held only for its own length, not the record's 10 to 40 s idle time (engine choice).
                if (fresh && a.Idle >= 0 && Library.Definitions[a.Idle] is { Loop: false } once)
                    a.IdleLeft = MathF.Min(a.IdleLeft, plan.Lengths.Of(once.Clip) / MathF.Max(once.PlaySpeed, 0.01f));
            }
            else a.Idle = -1;
        }
        return Settle(a, wanted, 0, -1, 0, technique, fight, v, dt, plan.Lengths, size, seed, key, tick);
    }

    /// <summary>The forward speed over which the injury hold is not played (<c>FUN_140520000</c>: the constant at <c>141683170</c>).</summary>
    const float PainHoldMaxSpeed = 50;

    /// <summary>
    /// The hurt-part hold the character plays now (<c>FUN_14064a100</c> builds the list, <c>FUN_140520000</c> and <c>FUN_14051fe60</c> pick; <b>Verified</b>, docs/animation.md "Injury holds"): the parts whose
    /// health fraction is under their <c>pain anim</c> threshold per cent, the lowest fraction first; the first whose record the free arms allow wins: a record that uses the left arm
    /// plays as is when the left arm is free and works, else as its <c>-R</c> mirror when the right arm is; a record that uses the right arm needs it free. An arm is busy while a
    /// hand holds a weapon or an upper / whole-body layer uses it. -1: none.
    /// </summary>
    int PainHold(CharacterAnimation a, CharacterCold cold, in AnimationStance stance, List<(int Index, float Weight)> wanted)
    {
        if (cold.Medical is not { } medical) return -1;
        bool leftBusy = stance.Left.Holds, rightBusy = stance.Right.Holds;
        void Scan(int index, float weight)
        {
            var d = Library.Definitions[index];
            if (weight <= 0 || d.Area is not (AnimationArea.Upper or AnimationArea.All)) return;
            leftBusy |= d.UsesLeftArm;
            rightBusy |= d.UsesRightArm;
        }
        foreach (var (index, weight) in wanted) Scan(index, weight);
        for (int k = 0; k < a.Count; k++) Scan(a.Layers[k].Definition, a.Layers[k].Weight);
        bool leftFree = !leftBusy && medical.ArmWorks(left: true), rightFree = !rightBusy && medical.ArmWorks(left: false);
        // Made only for a character with a hurt part (most have none), and no lambda over the loop's part: this runs for every character every tick.
        List<(float Fraction, string Record)>? hurt = null;
        foreach (var part in medical.PartSpan)
        {
            if (part.Template.PainAnim is not { } record || !(part.Fraction < part.Template.PainThreshold * 0.01f) || hurt is not null && SameFraction(hurt, part.Fraction)) continue;
            (hurt ??= []).Add((part.Fraction, record));
        }
        if (hurt is null) return -1;
        hurt.Sort(static (x, y) => x.Fraction.CompareTo(y.Fraction));
        foreach (var (_, record) in hurt)
        {
            int index = Library.IndexOfRecord(record);
            if (index < 0) continue;
            var d = Library.Definitions[index];
            if (d.UsesRightArm && !rightFree) continue;
            if (leftFree || !d.UsesLeftArm) return index;
            if (rightFree && Library.IndexOf(d.Name + "-R") is >= 0 and var mirror) return mirror;
        }
        return -1;
    }

    static bool SameFraction(List<(float Fraction, string Record)> hurt, float fraction)
    {
        foreach (var h in hurt)
            if (h.Fraction == fraction) return true;
        return false;
    }

    /// <summary>
    /// A standing character's idle: out of combat a whole-body idle, in combat an upper-body guard over the lower movement blend (<paramref name="legs"/>:
    /// the lower clips are already wanted), kept until its time is up and then drawn again by <c>idle chance</c>.
    /// </summary>
    void StandingIdle(CharacterAnimation a, in AnimationStance stance, bool legs, float dt, ulong seed, ulong key, long tick, List<(int Index, float Weight)> wanted) =>
        StandingIdle(a, legs ? Library.CombatIdles(stance) : Library.Idles(stance), dt, seed, key, tick, wanted);

    /// <summary>The idle among <paramref name="idles"/> (index, <c>idle chance</c>): kept until its time is up, then drawn again by weight.</summary>
    void StandingIdle(CharacterAnimation a, IReadOnlyList<(int Index, float Weight)> idles, float dt, ulong seed, ulong key, long tick, List<(int Index, float Weight)> wanted)
    {
        if (a.Idle < 0 || a.IdleLeft <= 0 || !idles.Any(x => x.Index == a.Idle))
        {
            float total = 0;
            foreach (var (_, w) in idles) total += w;
            if (idles.Count > 0)
            {
                ulong roll = Rng.Hash(seed, key, RngPurpose.Animation, (ulong)tick);
                float pick = Rng.Float(roll) * total;
                int chosen = idles[^1].Index;
                foreach (var (index, w) in idles)
                {
                    if ((pick -= w) < 0) { chosen = index; break; }
                }
                var d = Library.Definitions[chosen];
                a.Idle = chosen;
                a.IdleLeft = d.IdleTimeMin + Rng.Float(Rng.Mix(roll)) * Math.Max(d.IdleTimeMax - d.IdleTimeMin, 0);
                // A new idle starts from its first frame.
                for (int k = 0; k < a.Count; k++) if (a.Layers[k].Definition == chosen) a.Layers[k].Time = 0;
            }
            else a.Idle = -1;
        }
        if (a.Idle >= 0)
        {
            wanted.Add((a.Idle, 1));
            a.IdleLeft -= dt;
        }
    }

    /// <summary>
    /// The hit reaction clip of a hit, for <see cref="CombatSystem.StumbleChooser"/> (docs/animation.md "Hit reactions", <b>Verified</b>; whether one is wanted at all, and
    /// how big, the combat system decides): one of the hit part's stumbles with that <c>big stumble</c> and <c>stumble from</c> = the side, drawn by weight with
    /// <paramref name="roll"/>; with its length in seconds at its play speed. Null when none fits.
    /// </summary>
    static (int Clip, float Seconds)? ChooseStumble(AnimationLibrary library, AnimationLengths lengths, CharacterCold cold, string part, bool big, int side, float roll)
    {
        if (library.Animal(cold.RecordId) is not null) return null;
        var candidates = new List<(int Index, float Weight)>();
        int playing = cold.Animation is { ActionIsStumble: true, Action: >= 0 } an ? an.Action : -1;
        library.Stumbles(part, big, side, StanceOf(cold), playing, candidates);
        if (candidates.Count == 0) return null;
        float total = 0;
        foreach (var (_, w) in candidates) total += w;
        float pick = roll * total;
        int chosen = candidates[^1].Index;
        foreach (var (index, w) in candidates)
        {
            if ((pick -= w) < 0) { chosen = index; break; }
        }
        var d = library.Definitions[chosen];
        // The stumble state lasts until the clip, at its record's play speed, is past StumbleEnd of its length (Verified, FUN_140435570 -> FUN_1405b3a40); no stat scales it.
        float seconds = StumbleEnd * lengths.Of(d.Clip) / (d.PlaySpeed > 0 ? d.PlaySpeed : 1);
        return seconds > 0 ? (chosen, seconds) : null;
    }

    /// <summary>The fraction of a stumble clip after which the stumble state ends (<c>DAT_14168db34</c>).</summary>
    public const float StumbleEnd = 0.98f;

    /// <summary>What a character holds and does, as the clips' conditions read it; leg health in per cent weighs the limp clips.</summary>
    static AnimationStance StanceOf(CharacterCold cold)
    {
        var stance = new AnimationStance
        {
            Right = new HandHold(cold.DrawnWeapon), Combat = cold.InCombat, Stealth = (cold.Orders & CharacterOrders.Sneak) != 0, CarryingLeft = !cold.Carrying.IsNone, BeingCarried = !cold.CarriedBy.IsNone,
            Prone = cold.CarriedBy.IsNone && cold.Medical is { Prone: true },   // the crawl records are prone; no standing clip (stumbles included) fits a crawler
        };
        if (cold.Medical is not { } body) return stance;
        float left = 100, right = 100;
        foreach (var part in body.PartSpan)
        {
            if (part.Template.Type != Meitou.Data.Gameplay.Bodies.BodyPartType.Leg) continue;
            if (part.Template.Side == Meitou.Data.Gameplay.Bodies.BodySide.Left) left = MathF.Min(left, part.Fraction * 100);
            else right = MathF.Min(right, part.Fraction * 100);
        }
        return stance with { LeftLeg = left, RightLeg = right };
    }

    /// <summary>
    /// A character lying in its ragdoll keeps the layers it had when it went down, unchanged (engine choice): the ragdoll starts from that pose on the tick it goes down, and
    /// the bones without a body (hands, feet, clavicles) keep it. One without layers yet (just loaded) takes the lying clip instead.
    /// </summary>
    static bool HoldsForRagdoll(CharacterAnimation a, bool down, bool carried, RagdollView ragdoll)
    {
        if (!down || carried || !ragdoll.Has || a.Count == 0) return false;
        a.WasDown = true;
        a.Action = -1;
        return true;
    }

    /// <summary>Starts a one-shot clip from its first frame (nothing when the data has no such clip).</summary>
    static void StartAction(CharacterAnimation a, int clip, bool stumble)
    {
        if (clip < 0) return;
        a.Action = clip;
        a.ActionTime = 0;
        a.ActionIsStumble = stumble;
        for (int k = 0; k < a.Count; k++) if (a.Layers[k].Definition == clip) a.Layers[k].Time = 0;
    }

    static int Find(CharacterAnimation a, int definition)
    {
        for (int k = 0; k < a.Count; k++) if (a.Layers[k].Definition == definition) return k;
        return -1;
    }

    /// <summary>Frees the faintest fading-out layer when all are in use; -1 when every layer is wanted.</summary>
    static int Evict(CharacterAnimation a)
    {
        int worst = -1;
        for (int k = 0; k < a.Count; k++)
            if (a.Layers[k].Target <= 0 && (worst < 0 || a.Layers[k].Weight < a.Layers[worst].Weight)) worst = k;
        return worst;
    }

    /// <summary>
    /// The layers to publish for a snapshot, written into <paramref name="into"/> (the snapshot's own list, cleared first): the record name (the renderer finds its track masks by it), the clip time in seconds and the weight. The
    /// weights are normalised as Kenshi's layers are (docs/animation.md "Layer managers"): the upper body (upper and whole-body clips) to a total of 1, the
    /// lower body to 1 minus the whole-body clips' weight (actions and <c>all</c> clips; a block is played without its lower-body tracks and keeps the legs'
    /// clips); overlays are left alone. Within a body half the layers fading out keep their weights and the wanted ones fill what is left (scaled up as well as
    /// down), so a cross-fade never lets the binding pose through; with only fading layers the total falls with them.
    /// </summary>
    public IReadOnlyList<AnimationLayer> Publish(CharacterAnimation a, List<AnimationLayer> into)
    {
        int visible = 0;
        float upperWanted = 0, upperFading = 0, fullWanted = 0, fullFading = 0, lowerWanted = 0, lowerFading = 0;
        for (int k = 0; k < a.Count; k++)
        {
            var layer = a.Layers[k];
            if (layer.Weight <= 0) continue;
            visible++;
            var d = Library.Definitions[layer.Definition];
            bool wanted = layer.Target > 0;
            switch (d.Area)
            {
                case AnimationArea.Lower: if (wanted) lowerWanted += layer.Weight; else lowerFading += layer.Weight; break;
                case AnimationArea.Overlay: break;
                default:
                    if (wanted) upperWanted += layer.Weight; else upperFading += layer.Weight;
                    if (d.Area == AnimationArea.All || d.IsAction && !d.Technique) { if (wanted) fullWanted += layer.Weight; else fullFading += layer.Weight; }   // a block keeps the legs' clips
                    break;
            }
        }
        into.Clear();
        if (visible == 0) return into;
        var (upperWantedScale, upperFadingScale) = Fill(upperWanted, upperFading, 1);
        float lowerRoom = MathF.Max(0, 1 - fullWanted * upperWantedScale - fullFading * upperFadingScale);
        var (lowerWantedScale, lowerFadingScale) = Fill(lowerWanted, lowerFading, lowerRoom);
        into.EnsureCapacity(visible);
        for (int k = 0; k < a.Count; k++)
        {
            var layer = a.Layers[k];
            if (layer.Weight <= 0) continue;
            var d = Library.Definitions[layer.Definition];
            bool wanted = layer.Target > 0;
            float scale = d.Area switch
            {
                AnimationArea.Lower => wanted ? lowerWantedScale : lowerFadingScale,
                AnimationArea.Overlay => 1,
                _ => wanted ? upperWantedScale : upperFadingScale,
            };
            into.Add(new AnimationLayer(d.Name, layer.Time, layer.Weight * scale) { Length = layer.Length, Definition = layer.Definition });
        }
        return into;
    }

    /// <summary>
    /// The scales of a body half's wanted and fading layers for a total of <paramref name="total"/> (Kenshi's layer manager, <c>@ 1405b5c30</c>): the wanted are scaled up to fill
    /// what the fading leave, then everything is scaled down if it is over the total.
    /// </summary>
    static (float Wanted, float Fading) Fill(float wanted, float fading, float total)
    {
        float wantedScale = wanted > 0 && total - fading > wanted ? (total - fading) / wanted : 1;
        float sum = wanted * wantedScale + fading;
        float down = sum > total && sum > 0 ? total / sum : 1;
        return (wantedScale * down, down);
    }
}

/// <summary>What the world asks to turn a character's animation state into the layers a snapshot carries.</summary>
public interface IAnimationSource
{
    /// <summary>Writes the layers into <paramref name="into"/> (cleared first) and returns it.</summary>
    IReadOnlyList<AnimationLayer> Publish(CharacterAnimation animation, List<AnimationLayer> into);
}

/// <summary>
/// The combat state the animation follows: the technique playing (library index, -1 none) and its progress (0 to 1), and the last hit taken (tick, -1
/// none; heavy, from behind, the body part's LOCATIONAL_DAMAGE string id), and the hit reaction clip the combat system started (library index, -1 none) and its tick.
/// </summary>
public readonly record struct CombatView(int Technique, float Progress, int HitTick, bool HitHeavy, bool HitBehind, string? HitPart, int StumbleClip = -1, int StumbleStart = -1)
{
    public static readonly CombatView None = new(-1, 0, -1, false, false, null);
}

/// <summary>What the animation of one character needs to know of its ragdoll: whether it has one (it falls by it, not by the fall clip) and whether the whole body was one.</summary>
public readonly record struct RagdollView(bool Has, bool WasWhole);
