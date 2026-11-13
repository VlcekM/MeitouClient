using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Movement;

/// <summary>
/// The animations a character plays: weighted clips (the movement blend by speed, an idle, a combat technique, a hit reaction) that cross-fade at
/// the CONSTANTS <c>animation blend rate</c>. Cold state of <see cref="CharacterCold"/>, changed only by <see cref="AnimationSystem"/>
/// in the character's own partition, hashed, and published as <see cref="AnimationLayer"/>s.
/// </summary>
public sealed class CharacterAnimation
{
    #region Layers

    /// <summary>
    /// Room for the layers of one character (engine choice: the original's layer lists grow without a cap, <b>Verified</b> <c>@ 1405b5270</c>; this
    /// is far above the most a fight needs, 5 in the base game).
    /// </summary>
    public const int MaxLayers = 16;

    /// <summary>
    /// One clip in play: the library index, its own time (seconds; unsynched clips only), its weight and the weight it is fading to.
    /// </summary>
    public struct Layer
    {
        /// <summary>
        /// The clip's index in the <see cref="AnimationLibrary"/> definitions.
        /// </summary>
        public int Definition;
        /// <summary>
        /// The clip's own time (seconds; unsynched clips only), its weight now and the weight it is fading to.
        /// </summary>
        public float Time, Weight, Target;

        /// <summary>
        /// Seconds of clip per second it last moved forwards at (the layer's play rate, +0x40): a block fading out plays backwards at it.
        /// </summary>
        public float Rate;

        /// <summary>
        /// The clip's length in seconds as the character's skeleton has it (set while the time advances; derived, not hashed): the sound events'
        /// fractions need it.
        /// </summary>
        public float Length;
    }

    /// <summary>
    /// The clips in play; the first <see cref="Count"/> are used.
    /// </summary>
    public readonly Layer[] Layers = new Layer[MaxLayers];
    /// <summary>
    /// How many of <see cref="Layers"/> are in play.
    /// </summary>
    public int Count;

    /// <summary>
    /// Phase of the synched movement clips in cycles (0..1): the lower and the upper body, and a walk fading into a run, stay in step.
    /// </summary>
    public float Phase;

    /// <summary>
    /// Per definition whether this body's rig plays it (1 yes, -1 no, 0 not asked), for the appearance <see cref="PlaysFor"/>.
    /// </summary>
    internal sbyte[]? Plays;
    /// <summary>
    /// The appearance <see cref="Plays"/> was filled for; another appearance starts it again.
    /// </summary>
    internal object? PlaysFor;

    /// <summary>
    /// Cycles per second the phase advances: written whenever the lower movement blend is picked (<b>Verified</b> <c>@ 14051d8e0</c>,
    /// <c>@ 14051d000</c>), kept while no such pick runs (standing out of combat, a technique): the legs go on stepping through their fade-out.
    /// </summary>
    public float PhaseRate;

    /// <summary>
    /// The idle chosen, and the seconds it still plays before another is chosen (-1 / 0: none yet).
    /// </summary>
    public int Idle = -1;
    /// <summary>
    /// The seconds the chosen <see cref="Idle"/> still plays before another is chosen.
    /// </summary>
    public float IdleLeft;

    /// <summary>
    /// A one-shot clip of the whole body that plays to its end (a hit reaction, the fall, getting up): the library index (-1: none), its time in
    /// seconds, and whether it is a hit reaction.
    /// </summary>
    public int Action = -1;
    /// <summary>
    /// The one-shot clip's time in seconds.
    /// </summary>
    public float ActionTime;
    /// <summary>
    /// Whether the one-shot clip is a hit reaction.
    /// </summary>
    public bool ActionIsStumble;

    /// <summary>
    /// Whether a clip it plays has <c>disables movement</c> (a bow, a reload, getting up): the movement system holds it still, its path kept.
    /// Derived, not hashed.
    /// </summary>
    public bool HoldsStill;

    /// <summary>
    /// The tick of the last hit a reaction was chosen for (-1: none), and whether the character was down (knocked out or dead) the last tick.
    /// </summary>
    public int SeenHit = -1;
    /// <summary>
    /// Whether the character was down (knocked out or dead) the last tick.
    /// </summary>
    public bool WasDown;

    /// <summary>
    /// Whether the character was crawling from a leg down (posture 2) the last tick: its end starts the get-up (docs/game/ragdolls.md "Getting up").
    /// </summary>
    public bool WasCrawling;

    /// <summary>
    /// The body's playback factor F = 2 - H (H the skeleton's movement scale; docs/animation.md "Movement"); 0 until the first tick sets it.
    /// </summary>
    public float Rate;

    /// <summary>
    /// The held clip (a work clip) the character plays this tick (-1: none) and the factor its play speed is scaled by (the lock picker's skill
    /// speed). Derived, not hashed.
    /// </summary>
    public int HeldIndex = -1;
    /// <summary>
    /// The factor the held clip's play speed is scaled by (the lock picker's skill speed); 1 for none.
    /// </summary>
    public float HeldRate = 1;

    #endregion

    #region Speed

    /// <summary>
    /// The last <see cref="SpeedWindow"/> speeds, oldest overwritten first (<see cref="SmoothSpeed"/>).
    /// </summary>
    public readonly float[] Speeds = new float[SpeedWindow];
    /// <summary>
    /// How many speeds <see cref="SmoothSpeed"/> has taken in all; the next goes to slot count mod window.
    /// </summary>
    public int SpeedCount;
    /// <summary>
    /// The speed window's length and how many samples at each end it drops, at a full window of <see cref="SpeedWindow"/> samples.
    /// </summary>
    public const int SpeedWindow = 8, SpeedTrim = 3;

    /// <summary>
    /// Side steps (docs/animation.md "Side steps"): whether the fighter is in its side-stepping state, the seconds before that state may flip again,
    /// its side speed (units per second, positive to its right; 0 out of the state) and the share of the legs the side-step clip has.
    /// </summary>
    public bool Strafing;
    /// <summary>
    /// The seconds before the side-stepping state may flip again, the side speed (units per second, positive to the right) and the side-step clip's
    /// share of the legs.
    /// </summary>
    public float StrafeTimer, StrafeSpeed, StrafeShare;

    /// <summary>
    /// The facing the body is drawn at (radians, as <c>CharacterHot.Yaw</c>) and whether it is set: it chases the character's facing at a limited
    /// rate (<see cref="AnimationSystem.FollowFacing"/>). Derived for drawing, not hashed.
    /// </summary>
    public float DrawYaw;
    /// <summary>
    /// Whether <see cref="DrawYaw"/> has been set.
    /// </summary>
    public bool HasDrawYaw;

    /// <summary>
    /// The speed the animations see (docs/animation.md "Movement", <b>Verified</b>): of the last 8 samples, sorted, the 3 lowest and 3 highest
    /// dropped and the middle two averaged (fewer samples at the start: the trimmed middle of those there are). Kenshi samples every frame; here
    /// every tick, so the window is <paramref name="window"/> ticks (<see cref="WindowFor"/>: as many seconds as 8 frames at 60 fps) with the trim
    /// scaled alike (engine).
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
        for (int i = trim; i < n - trim; i++)
            sum += sorted[i];

        return sum / (n - 2 * trim);
    }

    /// <summary>
    /// The window of <see cref="SmoothSpeed"/> for a tick of <paramref name="dt"/> seconds: the 8 samples of 60 fps frames in ticks (4 at 30 Hz).
    /// </summary>
    public static int WindowFor(float dt) => Math.Clamp((int)MathF.Round(SpeedWindow / (60 * dt)), 1, SpeedWindow);

    #endregion

    #region Hashing

    /// <summary>
    /// Adds the hashed animation state to <paramref name="h"/>: the layers in play, the phase, idle, one-shot and hit state, the playback factor and
    /// the speed window (derived fields left out).
    /// </summary>
    internal void Hash(ref StateHasher h)
    {
        h.Add(Count);
        for (int i = 0; i < Count; i++)
        {
            h.Add(Layers[i].Definition);
            h.Add(Layers[i].Time);
            h.Add(Layers[i].Weight);
            h.Add(Layers[i].Target);
            h.Add(Layers[i].Rate);
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
        foreach (float s in Speeds)
            h.Add(s);

        h.Add(HasDrawYaw);
        h.Add(DrawYaw);   // the sight cone reads it (SightMap)

        // only once set, so worlds without fights hash as before
        if (Strafing || StrafeTimer != 0 || StrafeSpeed != 0 || StrafeShare != 0)
        {
            h.Add(0x53747266);
            h.Add(Strafing);
            h.Add(StrafeTimer);
            h.Add(StrafeSpeed);
            h.Add(StrafeShare);
        }
    }

    #endregion
}

/// <summary>
/// Picks each character's animation layers from its movement, stance and fight and advances their times in game time (docs/simulation.md
/// "Animation", the original's rules in docs/animation.md). Runs after movement and combat, in the Act phase, and writes only its own characters'
/// cold animation state and, for root motion, their position.
/// <list type="bullet">
/// <item><b>Verified</b> (decompilation, docs/animation.md): every movement clip that fits plays at once, weighted by a sigmoid of its speed ramp and
/// its leg ramp, normalised per body half; a synched clip's phase advances F x v x the weighted <c>play speed</c> cycles per second, an unsynched one
/// F x <c>play speed</c> x v clip seconds per second; weights move linearly towards their targets; hit reactions are chosen by <c>stumbles</c>,
/// <c>big stumble</c> and <c>stumble from</c>; <c>relocates</c> clips, attacks and dodges move the character by their <c>Bip01</c> track; getting up
/// plays <c>standing up 3</c> to 86 %.</item>
/// <item><b>Engine choice</b>: the blend rate is the same for every clip; in combat a standing character holds an upper-body guard idle over the
/// lower movement blend; techniques cut a hit reaction short; a character going down without a ragdoll (<see cref="Ragdolls"/>) falls with
/// <c>dodgefall</c>; down, it lies in <c>sleeponfloor</c> (under a ragdoll, the bones without a body); a character counts as moving from
/// <see cref="MovingSpeed"/> units per second.</item>
/// </list>
/// </summary>
public sealed class AnimationSystem(
    AnimationLibrary library,
    AnimationLengths lengths,
    float blendRate = 4,
    CombatSystem? combat = null) : ITickSystem, IAnimationSource
{
    #region Setup

    /// <summary>
    /// Speed below which a character stands: the original stands only at a filtered speed of exactly 0 (docs/animation.md); this only filters float
    /// noise.
    /// </summary>
    public const float MovingSpeed = 0.05f;

    /// <summary>
    /// Getting up ends when <c>standing up 3</c> is this far through (<b>Verified</b>, <c>Task_GetUp</c>).
    /// </summary>
    public const float GetUpEnd = 0.86f;

    /// <summary>
    /// The animation definitions (clips by index) the system picks from.
    /// </summary>
    public AnimationLibrary Library { get; } = library;
    /// <summary>
    /// The clips' lengths, for one-shot ends and the get-up time.
    /// </summary>
    public AnimationLengths Lengths { get; } = lengths;

    /// <summary>
    /// The CONSTANTS <c>animation blend rate</c> (fcs.def default 4, "1 is very slow"): weight per second.
    /// </summary>
    public float BlendRate { get; } = blendRate;

    /// <summary>
    /// The skeleton rigs, to know which clips a body can play (<see cref="Plays"/>); null: every clip plays (tests, headless worlds).
    /// </summary>
    public CharacterRigs? Rigs { get; init; }

    /// <summary>
    /// Whether the character's skeleton has the clip of definition <paramref name="index"/>. <b>Verified</b> (<c>FUN_1405b5c30</c>, the layer
    /// manager's update): a layer whose animation the entity lacks is deleted before the weights are summed or scaled, so it never takes weight from
    /// the others. A clip only a mod's other-sex animation file holds is such a one.
    /// </summary>
    bool Plays(CharacterAnimation a, CharacterCold cold, int index)
    {
        if (Rigs is null || cold.Appearance is not { } look) return true;

        if (!ReferenceEquals(a.PlaysFor, look) || a.Plays is null) (a.PlaysFor, a.Plays) = (look, new sbyte[Library.Definitions.Count]);
        ref sbyte known = ref a.Plays[index];
        if (known == 0) known = (sbyte)(Rigs.Plays(look, Library.Definitions[index].Name) ? 1 : -1);
        return known > 0;
    }

    /// <summary>
    /// The ragdolls (null: none; the fall and lying clips stand in for them). Read in the parallel phase: the last tick's state.
    /// </summary>
    public Ragdolls.IRagdollState? Ragdolls { get; set; }

    #endregion

    #region Clips

    /// <summary>
    /// The clip a knocked-out or dead character lies in: <c>sleeponfloor</c>, -1 when the data has none.
    /// </summary>
    int LyingClip { get; } = library.IndexOf("sleeponfloor");

    /// <summary>
    /// The whole-body clips held while a character bandages a patient (<c>medic</c>) or rests (<c>sitting idle</c>); -1 when the data has none. A
    /// held clip like the lying one (engine choice).
    /// </summary>
    int TreatingClip { get; } = library.IndexOf("medic");
    /// <summary>
    /// The sitting clip of a character resting with no chair clip (<c>sitting idle</c>); -1 when the data has none.
    /// </summary>
    int RestingClip { get; } = library.IndexOf("sitting idle");

    /// <summary>
    /// The chairs' sitting clips by record name (looked up on every worker).
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _sitClips = new(StringComparer.Ordinal);

    /// <summary>
    /// The library index of the clip named <paramref name="name"/>, looked up once per name; -1 when there is none.
    /// </summary>
    int SitClipOf(string name) => _sitClips.GetOrAdd(name, Library.IndexOf);

    /// <summary>
    /// The whole-body clip held while a character takes a building apart (<c>stonemine</c>, <b>Verified</b>: <c>Task_Dismantle</c>); -1 when the data
    /// has none.
    /// </summary>
    int DismantlingClip { get; } = library.IndexOf("stonemine");

    /// <summary>
    /// The crouch clip Task_Crouch plays forwards and Task_UnCrouch backwards (<c>squat T</c>, FUN_140346360 / FUN_140345fb0).
    /// </summary>
    int SquatClip { get; } = library.IndexOf("squat T");

    /// <summary>
    /// The carrier's lift clip (<c>shoulder lift left</c>, played backwards to put down); -1 when the data has none.
    /// </summary>
    int LiftClip { get; } = library.IndexOf(Carrying.CarrySystem.LiftClip);

    /// <summary>
    /// The overlay that holds a carrier's arm on the body (<c>carrying left arm only</c>: <c>override L arm</c>, every other bone deleted):
    /// <b>Verified</b>, <c>FUN_140520000</c> plays it every frame a character carries.
    /// </summary>
    int CarryArmClip { get; } = library.IndexOf("carrying left arm only");
    /// <summary>
    /// The stealth knock-out clip (<c>knockout</c>) the attacker plays; -1 when the data has none.
    /// </summary>
    int KnockoutClip { get; } = library.IndexOf(Carrying.CarrySystem.KnockoutClip);

    /// <summary>
    /// A carried body's held pose (<c>carry me</c>, a category 3 record the library appends); -1 when the data has none.
    /// </summary>
    int CarriedClip { get; } = library.IndexOf(Carrying.CarrySystem.CarriedClip);

    #endregion

    #region Held clips

    /// <summary>
    /// The held clip of a task (-1: none). Building, delivering materials and prospecting play the medic clip (Verified, Task_Build,
    /// Task_AddMaterialsToBuilding and Task_Prospecting). Resting on a chair plays the clip its functionality names ("sitting chair"), else sitting
    /// on the floor.
    /// </summary>
    int HeldClip(CharacterTask task, CharacterCold cold, long tick) => task switch
    {
        // Standing up from staying low plays the crouch clip backwards
        // (Task_UnCrouch, FUN_140345fb0 / FUN_140346090: squat T at rate -1; see FinishStandUp).
        _ when cold.StandingUp && SquatClip >= 0 => SquatClip,
        // A crouch goal (Task_Crouch 27 plays it forwards, Task_UnCrouch 28 backwards); the stance byte follows (FinishCrouch).
        _ when CrouchGoal(cold) != 0 && SquatClip >= 0 => SquatClip,
        // The lock picker plays the cage's `animation lockpick`, else the medic clip every update it picks
        // (Task_PickLock, FUN_140359160; the crawler's kneeling clip is chosen in Crawl).
        _ when cold.PickingLock is not null && cold.PickWork.Tick >= tick - 1
            => (cold.PickWork.Clip is { } pick ? SitClipOf(pick) : -1) is >= 0 and var picking ? picking : TreatingClip,
        CharacterTask.Build or CharacterTask.DeliverMaterials or CharacterTask.Prospect => TreatingClip,
        // A medic kneels only at a patient keeping still (Task_FirstAid's update plays the clip; FirstAidSystem);
        // waiting for one that moves it stands.
        CharacterTask.Treat => cold.TreatKneels ? TreatingClip : -1,
        // An NPC looting a body it has reached (Observed, Task_Loot_AI) or a forager waiting at one
        // (Task_Loot_ResourcesWeHaveStorageFor); the player's own take is instant.
        CharacterTask.Loot when (!cold.IsPlayer || cold.Foraging) && cold.LiftTime >= 0 => TreatingClip,
        CharacterTask.Rest => cold.Ai?.SitClip is { } sit && SitClipOf(sit) is >= 0 and var chair ? chair : RestingClip,
        CharacterTask.Dismantle => DismantlingClip,
        // An operator at work plays its functionality's clip (Observed, Task_OperateMachine; stonemine without one).
        CharacterTask.Operate
            => cold.Operating ? cold.OperateClip is { } clip && SitClipOf(clip) is >= 0 and var used ? used : DismantlingClip : -1,
        // In a bed: the clip its functionality names (Observed, Task_UseBed: sleepinbed, sleeponfloor), else lying on the floor.
        CharacterTask.Sleep
            => cold.InSomething != Locks.HeldState.Bed ? -1 : cold.OperateClip is { } bed && SitClipOf(bed) is >= 0 and var lain ? lain : LyingClip,
        // A captor killing, eating or processing a captive (Task_KillPrisoner, Task_EatPrisoner, Task_ProcessNewSlave, Observed).
        CharacterTask.Idle when cold.ActionClip is { } act && SitClipOf(act) is >= 0 and var acting => acting,
        _ => -1,
    };

    #endregion

    #region Hit reactions

    /// <summary>
    /// Whether the hook is set; set once while the system is made. The combat system asks this system for its hit reaction clips (it owns the stun,
    /// this system the clips).
    /// </summary>
    readonly bool _stumbleHook = Hook(combat, library, lengths);

    /// <summary>
    /// Gives <paramref name="combat"/> the hit reaction chooser and the get-up time of <c>standing up 3</c>; false without a combat system.
    /// </summary>
    static bool Hook(CombatSystem? combat, AnimationLibrary library, AnimationLengths lengths)
    {
        if (combat is null) return false;

        combat.StumbleChooser = (cold, part, big, side, roll) => ChooseStumble(library, lengths, cold, part, big, side, roll);
        int getUp = library.IndexOf("standing up 3");
        combat.GetUpClipSeconds = getUp >= 0 ? GetUpSeconds(library, lengths, getUp) : 0;
        return true;
    }

    /// <summary>
    /// The seconds definition <paramref name="index"/> plays until <see cref="GetUpEnd"/> of its clip, at its play speed.
    /// </summary>
    static float GetUpSeconds(AnimationLibrary library, AnimationLengths lengths, int index)
    {
        var definition = library.Definitions[index];
        float speed = definition.PlaySpeed > 0 ? definition.PlaySpeed : 1;
        return lengths.Of(definition.Clip) * GetUpEnd / speed;
    }

    #endregion

    #region Falls and ranged clips

    /// <summary>
    /// The fall (without a ragdoll) and getting up.
    /// </summary>
    readonly int _fall = library.IndexOfAny("Dodge back fall"), _getUp = library.IndexOf("standing up 3");

    /// <summary>
    /// The crossbow clips (RANGED records, played by the combat system's timing).
    /// </summary>
    readonly int _aimClip = library.IndexOf(AnimationLibrary.RangedAimRecord), _defaultReload = library.IndexOf(AnimationLibrary.RangedReloadRecord);
    /// <summary>
    /// The reload clips by RANGED <c>reload anim</c> id, looked up once each.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _reloadClips = new(StringComparer.Ordinal);
    /// <summary>
    /// The idle stance clips by ANIMATION record name, looked up once each.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _stanceClips = new(StringComparer.Ordinal);

    /// <summary>
    /// The character's own idle stance (its appearance's <c>idle stance</c>, an ANIMATION record name), -1 when it has none or the data lacks it.
    /// </summary>
    int OwnStance(CharacterCold cold) =>
        cold.Appearance?.Body?.IdleStance is { } name ? _stanceClips.GetOrAdd(name, static (key, lib) => lib.IndexOf(key), Library) : -1;

    /// <summary>
    /// The reload clip of the character's crossbow: its record's <c>reload anim</c>, else <c>reload 1 phase</c>.
    /// </summary>
    int ReloadClip(CharacterCold cold) => cold.Fighter?.Crossbow is { Data.ReloadAnimId: { Length: > 0 } id }
        ? _reloadClips.GetOrAdd(id, static (key, lib) => lib.IndexOfRecord(key), Library) is >= 0 and var k ? k : _defaultReload
        : _defaultReload;

    #endregion

    #region Animating

    /// <summary>
    /// Scratch lists of the thread running a partition, kept between ticks (Update clears them on entry).
    /// </summary>
    [ThreadStatic] static List<(int Index, float Weight)>? s_lowerScratch, s_upperScratch;

    /// <summary>
    /// The parallel step: each living, awake character stepping this tick gets its speed (signed along the facing in a fight), its layers updated for
    /// its task, combat view and ragdoll, the root motion applied and its drawn facing followed.
    /// </summary>
    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        var wantedLower = s_lowerScratch ??= [];
        var wantedUpper = s_upperScratch ??= [];
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i)) continue;

            float dt = world.StepSeconds(i);   // a far character's step covers the ticks since its last (the far cadence, World.StepTicks)
            var cold = table.Cold(i)!;
            var a = cold.Animation ??= new CharacterAnimation();
            var v = CappedToMoved(next[i].Velocity, prev[i].Position, next[i].Position, dt);
            float speed = v.Length();
            bool approaching = Approaching(table, i, cold);
            if (cold.InCombat && !approaching)
                speed = StrafeAlongFacing(world, i, a, v, next[i].Yaw, dt);
            else
                (a.Strafing, a.StrafeTimer, a.StrafeSpeed, a.StrafeShare) = (false, 0, 0, 0);

            speed = a.SmoothSpeed(speed, CharacterAnimation.WindowFor(dt));
            var id = table.IdOf(i);
            var ragdoll = Ragdolls is { } r ? new RagdollView(r.HasRagdoll(cold), r.WasWhole(id)) : default;
            var view = ViewOf(id, world.Tick, cold, dt);
            var task = (CharacterTask)next[i].Task;
            var root = Update(a, cold, speed, dt, world.Seed, Rng.Key(id), world.Tick, wantedLower, wantedUpper, view, task, ragdoll, approaching);
            if (root != Vector2.Zero) Relocate(world, i, ref next[i], root);
            bool drawTurns = cold.InCombat && !approaching && cold.CarriedBy.IsNone;
            bool free = cold.CarriedBy.IsNone && cold.InSomething == Locks.HeldState.None && cold.Medical is not { Incapacitated: true };
            FollowFacing(a, next[i].Yaw, drawTurns, free, dt);
        }
    }

    /// <summary>
    /// The original's animation speed is the displacement the physics step really made (docs/animation.md "Speed the animations see"): a walker held
    /// by a wall, a crowd or a body it cannot pass keeps its commanded velocity here, so it is taken no faster than it moved (never faster than
    /// commanded).
    /// </summary>
    static Vector3 CappedToMoved(Vector3 velocity, Vector3 from, Vector3 to, float dt)
    {
        float commanded = velocity.Length();
        if (commanded <= 0) return velocity;

        float moved = MathF.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z)) / dt;
        return moved < commanded ? velocity * (moved / commanded) : velocity;
    }

    /// <summary>
    /// In combat the speed is signed along the facing: backing off is negative and picks the clips with a negative move speed; the side speed drives
    /// the side steps. Returns that forward speed and updates the side-stepping state.
    /// </summary>
    static float StrafeAlongFacing(World world, int slot, CharacterAnimation a, Vector3 v, float yaw, float dt)
    {
        float sin = MathF.Sin(yaw), cos = MathF.Cos(yaw);
        float forward = v.X * sin + v.Z * cos;
        ulong roll = Rng.Hash(world.Seed, Rng.Key(world.Characters.IdOf(slot)), RngPurpose.Animation, 0x5354524146UL);
        Strafe(a, forward, v.Z * sin - v.X * cos, StrafeOnRatio(roll), StrafeOffRatio(roll), dt);
        return forward;
    }

    #endregion

    #region Drawn facing

    /// <summary>
    /// Radians per second the drawn body turns towards the character's facing in the combat stance and out of it (<b>Verified</b>,
    /// <c>AnimationClass</c> slot 8 <c>@ 1405b24e0</c>: a half-angle step of 2 x dt, 5 times that unless the stance flag is 1, taken as a rotation by
    /// 2 x the step).
    /// </summary>
    public const float CombatDrawTurn = 4, DrawTurn = 20;

    /// <summary>
    /// The drawn body follows the facing (<paramref name="yaw"/>) by at most <see cref="CombatDrawTurn"/> (in the combat stance, <paramref
    /// name="combat"/>) or <see cref="DrawTurn"/> rad/s, taking it at once while the character is not free (carried, held, down) or has no drawn
    /// facing yet (<b>Verified</b>: the original sets the node directly for an imprisoned character; engine choice for the other states).
    /// </summary>
    internal static void FollowFacing(CharacterAnimation a, float yaw, bool combat, bool free, float dt)
    {
        if (!a.HasDrawYaw || !free)
        {
            a.DrawYaw = yaw;
            a.HasDrawYaw = true;
            return;
        }

        float delta = MathF.IEEERemainder(yaw - a.DrawYaw, MathF.Tau);
        float step = (combat ? CombatDrawTurn : DrawTurn) * dt;
        a.DrawYaw = MathF.Abs(delta) <= step ? yaw : a.DrawYaw + MathF.CopySign(step, delta);
    }

    #endregion

    #region Combat stance

    /// <summary>
    /// Whether a fighter is running at its target out of the combat stance (<see cref="CharacterCold.CombatRunning"/>, set by the pursuit system): it
    /// keeps the plain walk and run clips until it is near (docs/game/combat.md "Combat stance").
    /// </summary>
    internal bool Approaching(CharacterTable table, int slot, CharacterCold cold) =>
        cold.InCombat && cold.CombatRunning && table.Next[slot].Task == (byte)CharacterTask.Attack;

    /// <summary>
    /// Seconds the side-stepping state holds after it flips (<c>CombatMovementController::combatMovementUpdate</c>, <b>Observed</b>).
    /// </summary>
    public const float StrafeHoldSeconds = 1;

    /// <summary>
    /// The side-to-forward ratio over which a fighter starts side-stepping, 1.5 + 3r per character (FORWARD_THRESHOLD,
    /// <c>setupCombatMovementVariables</c>, <b>Observed</b>).
    /// </summary>
    static float StrafeOnRatio(ulong roll) => 1.5f + 3 * Rng.Float(roll);

    /// <summary>
    /// The forward-to-side ratio over which it stops, 1.5 + 2r (STRAFE_THRESHOLD, same source).
    /// </summary>
    static float StrafeOffRatio(ulong roll) => 1.5f + 2 * ((roll & 0xFFFFFF) * (1f / (1 << 24)));

    /// <summary>
    /// The side-stepping state of a fighter in its stance (docs/animation.md "Side steps", <b>Observed</b> from <c>combatMovementUpdate</c> and
    /// <c>combatMovementAnimationUpdate</c>): from its <paramref name="forward"/> and <paramref name="side"/> speeds (along its facing, and to its
    /// right), it starts when |side| > <paramref name="on"/> x |forward| and stops when |forward| > <paramref name="off"/> x |side|, at most once per
    /// <see cref="StrafeHoldSeconds"/>; in the state its side speed (0 while it walks straighter than |forward| > 1 with |side| under 0.5) is what
    /// the side-step clip plays at.
    /// </summary>
    internal static void Strafe(CharacterAnimation a, float forward, float side, float on, float off, float dt)
    {
        a.StrafeTimer = MathF.Max(a.StrafeTimer - dt, 0);
        if (CanFlipStrafe(a, forward, side, on, off))
        {
            a.Strafing = !a.Strafing;
            a.StrafeTimer = StrafeHoldSeconds;
        }

        if (MathF.Abs(forward) > 1 && MathF.Abs(side) < 0.5f) side = 0;
        a.StrafeSpeed = a.Strafing ? side : 0;
    }

    /// <summary>
    /// Whether the side-stepping state flips this tick: the hold has run out, the fighter moves, and the ratio it leaves or enters at is crossed.
    /// </summary>
    static bool CanFlipStrafe(CharacterAnimation a, float forward, float side, float on, float off) =>
        (forward != 0 || side != 0) && a.StrafeTimer <= 0
        && (a.Strafing ? MathF.Abs(forward) > off * MathF.Abs(side) : MathF.Abs(side) > on * MathF.Abs(forward));

    /// <summary>
    /// What the combat system says about a character for its animation: the technique playing and its progress, and the last hit taken (the last
    /// finished tick's state).
    /// </summary>
    CombatView ViewOf(CharacterId id, long tick, CharacterCold cold, float tickSeconds)
    {
        if (combat is null) return CombatView.None;

        var slot = combat.StateOf(id);
        var (technique, progress) = TechniqueOf(id, tick, cold, tickSeconds);
        string? part = slot.HitPart >= 0 && cold.Medical is { } body && slot.HitPart < body.Parts.Count
            ? body.Parts[slot.HitPart].Template.StringId
            : null;
        return new CombatView(technique, progress, slot.HitTick, slot.HitHeavy, slot.HitBehind, part, slot.StumbleClip, slot.StumbleStart);
    }

    /// <summary>
    /// The clip index and progress of the technique or crossbow pose the character plays (-1 and 0 when none).
    /// </summary>
    (int Technique, float Progress) TechniqueOf(CharacterId id, long tick, CharacterCold cold, float tickSeconds)
    {
        if (combat!.Playing(id, tick) is { } playing)
            return (Library.IndexOfTechnique(playing.Technique.StringId), playing.Progress);

        if (combat.RangedPoseOf(id, tick, tickSeconds) is { } pose) return RangedClip(pose, cold);

        return (-1, 0);
    }

    /// <summary>
    /// The crossbow (engine choice of clips and timing): aiming raises "aimH" (clip aimshoot) through its first half over the steady time, the shot
    /// plays the second half, the reload plays the crossbow's own "reload anim" record (else "reload 1 phase") stretched over the reload time.
    /// </summary>
    (int Technique, float Progress) RangedClip((RangedPose Pose, float Progress) pose, CharacterCold cold) => pose.Pose switch
    {
        RangedPose.Aim => (_aimClip, pose.Progress * 0.5f),
        RangedPose.Shot => (_aimClip, 0.5f + pose.Progress * 0.5f),
        _ => (ReloadClip(cold), pose.Progress),
    };

    #endregion

    #region Root motion

    /// <summary>
    /// What shortened a root-motion step (debug log, <see cref="RootClamp"/>).
    /// </summary>
    public enum RootBlock { None, Body, Wall }

    /// <summary>
    /// Debug hook: called for every root-motion step with the wanted step, the one made (both on XZ), what shortened it and whether the end point
    /// was refused as unwalkable (then nothing moved).
    /// </summary>
    public static Action<int, Vector3, Vector2, Vector2, RootBlock, bool>? RootClamp;

    /// <summary>
    /// The root motion's step against the walls.
    /// </summary>
    struct BodyBlock
    {
        /// <summary>
        /// The step (the walls cut it).
        /// </summary>
        public Vector2 Step;

        /// <summary>
        /// The moving character's footprint radius.
        /// </summary>
        public float Radius;
    }

    /// <summary>
    /// The steps <see cref="ScanRoot"/> counts: the walkable spots probed, and their steps free, slid along a wall, cut by a wall to under half,
    /// refused at the end point, and of those the ones whose unslid step would have been walkable.
    /// </summary>
    struct RootScanTally
    {
        /// <summary>
        /// The walkable spots probed.
        /// </summary>
        public int Spots;

        /// <summary>
        /// The steps made whole.
        /// </summary>
        public int Free;

        /// <summary>
        /// The steps slid along a wall, to half or more.
        /// </summary>
        public int Slid;

        /// <summary>
        /// The steps cut by a wall to under half.
        /// </summary>
        public int Halved;

        /// <summary>
        /// The steps refused at the end point.
        /// </summary>
        public int Refused;

        /// <summary>
        /// The refused steps whose unslid step would have been walkable.
        /// </summary>
        public int RefusedWouldBeFree;
    }

    /// <summary>
    /// Whether the ground at (x, z) is within a step of <paramref name="y"/>, so that a root-motion step ending off the mesh is not over a drop or
    /// against a rise.
    /// </summary>
    static bool GroundNear(IWalkability walk, float x, float z, float y) =>
        MathF.Abs(walk.GroundHeight(x, z, y) - y) <= Navigation.NavmeshWalkability.StandRise;

    /// <summary>
    /// Root motion: moves the character by the ground part of its relocating clips' <c>Bip01</c> track (model X and Z turned by its heading), onto
    /// the ground, unless that point is not walkable. The original moves its nav agent, so the walls of the walkable surface stop the body's circle
    /// and it slides along them (<see cref="IWalkability.Slide"/>; docs/game/pathfinding.md "Walls"). Standing bodies do not stop the step: the
    /// original's body test only pushes overlapping characters apart (<c>FUN_1401476f0</c>), which <see cref="MovementSystem"/>'s separation does
    /// (docs/game/pathfinding.md "Bodies in the way").
    /// </summary>
    internal static void Relocate(World world, int self, ref CharacterHot c, Vector2 root)
    {
        float sin = MathF.Sin(c.Yaw), cos = MathF.Cos(c.Yaw);
        var step = new Vector2(root.X * cos + root.Y * sin, -root.X * sin + root.Y * cos);
        var block = new BodyBlock { Step = step, Radius = CombatTuning.Footprint(world.Characters.Cold(self)) };

        // The walls stop it as they stop the original's agent, which this move goes through: its circle slides along the first wall it meets
        // (docs/game/pathfinding.md "Walls").
        var walk = world.Walkability;
        var wanted = step;
        var moved = walk.Slide(c.Position, block.Step, block.Radius);
        float x = c.Position.X + moved.X, z = c.Position.Z + moved.Y;
        bool unwalkable = !walk.IsWalkable(x, z, c.Position.Y);
        var made = moved;
        if (unwalkable && walk.IsWalkable(c.Position.X, c.Position.Z, c.Position.Y))
            unwalkable = StepToSurface(walk, c.Position, moved, out made, out x, out z);
        // Standing where no surface is (pushed onto a mesh edge, in a hole), the end point cannot be worse: the move is made (the height follows
        // the ground).
        else unwalkable = false;

        var blocked = made != block.Step ? RootBlock.Wall : RootBlock.None;
        RootClamp?.Invoke(self, c.Position, wanted, unwalkable ? Vector2.Zero : made, blocked, unwalkable);
        if (unwalkable) return;

        c.Position = new Vector3(x, walk.GroundHeight(x, z, c.Position.Y), z);
    }

    /// <summary>
    /// A root-motion step <paramref name="moved"/> from <paramref name="from"/> whose end point has no surface: the part of it that is kept
    /// (<paramref name="made"/>, ending at <paramref name="x"/>, <paramref name="z"/>); true when it is still refused.
    /// </summary>
    static bool StepToSurface(IWalkability walk, Vector3 from, Vector2 moved, out Vector2 made, out float x, out float z)
    {
        // The end point has no surface a walker can stand on (a drop, a step too high, off the mesh) where the sweep saw no wall: keep the farthest
        // part of the step that has one, instead of dropping the whole step (the original has no such test: its sweep ends where the mesh's edge
        // is).
        float lo = 0, hi = 1;
        for (int k = 0; k < 6; k++)
        {
            float mid = (lo + hi) * 0.5f;
            if (walk.IsWalkable(from.X + moved.X * mid, from.Z + moved.Y * mid, from.Y)) lo = mid;
            else hi = mid;
        }

        made = moved * lo;
        (x, z) = (from.X + made.X, from.Z + made.Y);
        bool unwalkable = lo <= 0;

        // Nothing of the step has a surface, yet the sweep saw no wall and the ground is at hand: the original has no end-point test, so the step is
        // made.
        if (unwalkable && GroundNear(walk, from.X + moved.X, from.Z + moved.Y, from.Y))
        {
            made = moved;
            (x, z) = (from.X + moved.X, from.Z + moved.Y);
            unwalkable = false;
        }

        return unwalkable;
    }

    /// <summary>
    /// Debug probe (control command <c>root-probe</c>): the root-motion step of <paramref name="length"/> units from the character's spot in
    /// <paramref name="directions"/> evenly spread headings, as <see cref="Relocate"/> would make it against the walls and the end-point test
    /// (bodies left out): one line per heading with the distance made and what stopped it.
    /// </summary>
    public static List<string> ProbeRoot(World world, CharacterId id, float length, int directions, Vector2? at = null)
    {
        var lines = new List<string>();
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot))
        {
            lines.Add("no such character");
            return lines;
        }

        var pos = table.Previous[slot].Position;
        float radius = CombatTuning.Footprint(table.Cold(slot));
        var walk = world.Walkability;
        if (at is { } where) pos = new Vector3(where.X, walk.TryStand(where.X, where.Y, pos.Y, out float ay) ? ay : pos.Y, where.Y);

        lines.Add($"at {pos.X:0.0} {pos.Y:0.0} {pos.Z:0.0} radius {radius:0.0} walkable-here {walk.IsWalkable(pos.X, pos.Z, pos.Y)}");
        for (int k = 0; k < directions; k++)
        {
            float a = k * MathF.Tau / directions;
            lines.Add(ProbeHeading(walk, pos, radius, a, length));
        }

        return lines;
    }

    /// <summary>
    /// One <see cref="ProbeRoot"/> line: the step of <paramref name="length"/> from <paramref name="pos"/> at heading <paramref name="a"/>
    /// (radians).
    /// </summary>
    static string ProbeHeading(IWalkability walk, Vector3 pos, float radius, float a, float length)
    {
        var step = new Vector2(MathF.Cos(a), MathF.Sin(a)) * length;
        var moved = walk.Slide(pos, step, radius);
        float x = pos.X + moved.X, z = pos.Z + moved.Y;
        bool unwalkable = !walk.IsWalkable(x, z, pos.Y) && !GroundNear(walk, x, z, pos.Y);
        string why = unwalkable ? "UNWALKABLE-END" : moved != step ? "wall" : "free";
        bool stand = walk.TryStand(x, z, pos.Y, out float ey);
        string refused = unwalkable ? " -> 0 (end point refused)" : "";
        string slide = $"{a * 180 / MathF.PI,6:0} deg: slide {moved.Length():0.00}/{length:0.0}{refused} {why}";

        return $"{slide}; end any-surface {walk.IsWalkable(x, z)} stand {stand} dy {(stand ? ey - pos.Y : 0):0.0}";
    }

    /// <summary>
    /// Debug scan (control command <c>root-scan</c>): every walkable spot of a grid (<paramref name="spacing"/> apart, within
    /// <paramref name="radius"/> of the centre) is probed with 8 steps of <paramref name="length"/> as <see cref="ProbeRoot"/>; counts the steps
    /// free, slid along a wall, cut by a wall to under half, and refused at the end point, and lists the first refused ones.
    /// </summary>
    public static List<string> ScanRoot(World world, CharacterId id, float cx, float cz, float radius, float spacing, float length)
    {
        var lines = new List<string>();
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot))
        {
            lines.Add("no such character");
            return lines;
        }

        float footprint = CombatTuning.Footprint(table.Cold(slot));
        var walk = world.Walkability;
        float y0 = table.Previous[slot].Position.Y;
        var tally = new RootScanTally();
        for (float dz = -radius; dz <= radius; dz += spacing)
        {
            for (float dx = -radius; dx <= radius; dx += spacing)
            {
                float x = cx + dx, z = cz + dz;
                if (!walk.TryStand(x, z, y0, out float y)) continue;

                var pos = new Vector3(x, y, z);
                if (!walk.IsWalkable(x, z, y)) continue;

                tally.Spots++;
                ScanSpot(walk, pos, footprint, length, lines, ref tally);
            }
        }

        lines.Add($"spots {tally.Spots} steps {tally.Spots * 8}: free {tally.Free}, slid-short {tally.Slid}, cut under half {tally.Halved}, "
            + $"refused {tally.Refused} (of which the unslid step would have been walkable: {tally.RefusedWouldBeFree})");
        return lines;
    }

    /// <summary>
    /// The 8 steps of one <see cref="ScanRoot"/> spot, counted into <paramref name="tally"/>; the first refused ones listed in
    /// <paramref name="lines"/>.
    /// </summary>
    static void ScanSpot(IWalkability walk, Vector3 pos, float footprint, float length, List<string> lines, ref RootScanTally tally)
    {
        float x = pos.X, y = pos.Y, z = pos.Z;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.Tau / 8;
            var step = new Vector2(MathF.Cos(a), MathF.Sin(a)) * length;
            var moved = walk.Slide(pos, step, footprint);
            bool bad = !walk.IsWalkable(x + moved.X, z + moved.Y, y) && !GroundNear(walk, x + moved.X, z + moved.Y, y);
            if (bad)
            {
                tally.Refused++;
                if (walk.IsWalkable(x + step.X, z + step.Y, y)) tally.RefusedWouldBeFree++;
                if (tally.Refused <= 12)
                    lines.Add($"refused at {x:0.0},{z:0.0} y {y:0.0} heading {a * 180 / MathF.PI:0}: slide {moved.Length():0.00} of {length:0.0}");
            }
            else if (moved == step) tally.Free++;
            else if (moved.Length() < length * 0.5f) tally.Halved++;
            else tally.Slid++;
        }
    }

    #endregion

    #region Update

    /// <summary>
    /// The share of <c>squat T</c> past which Task_Crouch sets the crouched byte (FUN_140346440, the float at 14168a25c, 0.9).
    /// </summary>
    const float CrouchSet = 0.9f;

    /// <summary>
    /// The share of <c>squat T</c> left at which Task_UnCrouch ends and sets the posture upright (FUN_140346090: progress below 0.1,
    /// <b>Verified</b>).
    /// </summary>
    const float UnCrouchDone = 0.1f;

    /// <summary>
    /// What a character's clip pick of <see cref="Update"/> depends on besides its stance: whether it is down, carried, crawling, in a bed or a cage,
    /// the technique it plays, and its task.
    /// </summary>
    readonly record struct ClipPose(bool Down, bool Carried, bool Crawling, bool InBed, bool InCage, int Technique, bool FullTechnique,
        CharacterTask Task);

    /// <summary>
    /// One tick of one character's animation (public for tests). <paramref name="speed"/> is in units per second (signed along the facing in
    /// combat). Returns how far its relocating clips moved it this tick, in model space (X, Z; Z forward), weighted by their weights.
    /// </summary>
    public Vector2 Update(CharacterAnimation a, CharacterCold cold, float speed, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)>? lowerScratch = null, List<(int Index, float Weight)>? upperScratch = null, CombatView? combatView = null,
        CharacterTask task = CharacterTask.Idle, RagdollView ragdoll = default, bool approaching = false)
    {
        var fight = combatView ?? CombatView.None;
        var lower = lowerScratch ?? [];
        var upper = upperScratch ?? [];
        a.HeldIndex = -1;
        lower.Clear();
        upper.Clear();
        if (Library.Animal(cold.RecordId) is { } animal)
            return UpdateAnimal(a, cold, _animalPlans.Of(animal), speed, dt, seed, key, tick, lower, fight, ragdoll);

        if (a.Rate == 0) a.Rate = cold.Appearance is { } look ? 2 - CharacterShape.MovementScaleOf(look) : 1;

        // Carrying picks the "carrying left" clips and a carried body the "being carried" ones (the ANIMATION booleans, docs/animation.md).
        bool carried = !cold.CarriedBy.IsNone;
        var stance = StanceOf(cold) with { Combat = cold.InCombat && !approaching };
        bool down = cold.Medical is { Incapacitated: true };
        bool crawling = !carried && cold.Medical is { Prone: true };
        bool inBed = cold.InSomething == Locks.HeldState.Bed, inCage = cold.InSomething == Locks.HeldState.Imprisoned;

        // One held in a cage or on a pole is posed by the fixture's clip, not by a ragdoll: the fixture refuses the whole-body ragdoll
        // (RagdollSystem.Wanted), so holding the pose here would leave it flat in whatever pose the knockout left (docs/game/locks.md "Cages and
        // beds").
        if (!inCage && HoldsForRagdoll(a, down, carried, ragdoll)) return Vector2.Zero;

        UpdateFallAndGetUp(a, cold, down, carried, crawling, inBed, inCage, ragdoll);
        int technique = TechniqueAndAction(a, cold, speed, dt, down, carried, fight);
        bool fullTechnique = technique >= 0 && Library.Definitions[technique].Area == AnimationArea.All;

        var wanted = lower;
        var pose = new ClipPose(down, carried, crawling, inBed, inCage, technique, fullTechnique, task);
        int legs = 0;   // the lower movement clips at the head of the list (the phase follows them)
        float legsShare = 1;   // their share of the legs (less while a side step plays)
        PickClips(a, cold, stance, pose, speed, dt, seed, key, tick, lower, upper, ref legs, ref legsShare);

        if (technique >= 0)
        {
            // An attack or a dodge is the whole body; a block the upper body over the legs' clips.
            if (fullTechnique) wanted.Clear();

            wanted.Add((technique, 1));
        }

        if (!down && !carried) AddArmOverlay(a, cold, stance, crawling, speed, wanted);

        var (lift, liftTime) = PickLift(a, cold, task, down, carried, wanted, ref legs);

        if (Rigs is not null) DropUnplayable(a, cold, wanted, ref legs);

        var root = Settle(a, wanted, legs, lift, liftTime, technique, fight, speed, dt, Lengths, 1, seed, key, tick, legsShare);
        FinishPostures(a, cold);
        return root;
    }

    /// <summary>
    /// Starts the hit reaction the combat system picked, and ends or advances the action that plays; returns the technique to play (-1 for none).
    /// </summary>
    int TechniqueAndAction(CharacterAnimation a, CharacterCold cold, float speed, float dt, bool down, bool carried, in CombatView fight)
    {
        // The hit reaction the combat system picked (it holds the character helpless for the clip's length; see ChooseStumble).
        if (!down && !carried && fight.StumbleClip >= 0 && fight.StumbleStart > a.SeenHit && Plays(a, cold, fight.StumbleClip))
        {
            a.SeenHit = fight.StumbleStart;
            StartAction(a, fight.StumbleClip, true);
        }

        int technique = down || carried ? -1 : fight.Technique;   // a carried body plays nothing but its held pose

        // A blow or block its body has no clip for leaves it in its stance, not the binding pose.
        if (technique >= 0 && !Plays(a, cold, technique)) technique = -1;

        // A technique cuts a reaction or getting up short, and so does walking off (except a hit reaction, which moves the character itself); a
        // stumble the combat system ended with a won break-out fades out (FUN_140609290 -> FUN_1405b36f0, docs/game/combat.md "Hit reactions").
        bool cutsAction = technique >= 0 || MathF.Abs(speed) >= MovingSpeed && !a.ActionIsStumble || a.ActionIsStumble && fight.StumbleClip < 0;
        if (a.Action >= 0 && !down && cutsAction) a.Action = -1;

        if (a.Action >= 0)
        {
            var d = Library.Definitions[a.Action];
            a.ActionTime += dt * (d.PlaySpeed > 0 ? d.PlaySpeed : 1);
            bool fallWithoutLying = down && a.Action == _fall && LyingClip < 0;
            if (a.ActionTime >= Lengths.Of(d.Clip) * (a.Action == _getUp ? GetUpEnd : 1) && !fallWithoutLying) a.Action = -1;
        }

        return technique;
    }

    /// <summary>
    /// The clips the character's state plays (<paramref name="lower"/> is the wanted list): its action, a carried or downed body's pose, a caged
    /// one's, swimming, crawling, else its gait (<see cref="PickGait"/>).
    /// </summary>
    void PickClips(CharacterAnimation a, CharacterCold cold, in AnimationStance stance, in ClipPose pose, float speed, float dt, ulong seed,
        ulong key, long tick, List<(int Index, float Weight)> lower, List<(int Index, float Weight)> upper, ref int legs, ref float legsShare)
    {
        var wanted = lower;
        int technique = pose.Technique;
        if (a.Action >= 0) wanted.Add((a.Action, 1));

        // A carried body, awake or not: the attach (FUN_1405ced90) plays "carry me" by name as the character's action, and the update then plays that
        // action alone (FUN_140520500, docs/game/carry-loot.md "A carried body"), so no movement, idle or technique pick runs.
        else if (pose.Carried && CarriedClip >= 0)
        {
            wanted.Add((CarriedClip, 1));
            a.Idle = -1;
        }
        else if (pose.Down)
        {
            PickDown(a, cold, pose.InBed, pose.InCage, tick, wanted);
        }

        // Picking its cage's lock from inside: the cage's `animation lockpick`, else `medic` (FUN_140520bc0, Verified: the Prisoner Cage names none),
        // at the picking rate (#359).
        else if (pose.InCage && technique < 0 && cold.PickingLock is not null && cold.PickWork.Tick >= tick - 1
            && HeldClip(pose.Task, cold, tick) is >= 0 and var picking)
        {
            (a.HeldIndex, a.HeldRate) = (picking, cold.PickWork.Rate);
            wanted.Add((picking, 1));
            a.Idle = -1;
        }

        // Awake in a cage: its `animation dazed` with a leg down (engine reading of the original's dazed test), else its `animation` (the Cage's
        // stand 1).
        else if (pose.InCage && technique < 0 && (pose.Crawling ? cold.CageClips.Dazed : cold.CageClips.Idle) is { } cageClip
            && SitClipOf(cageClip) is >= 0 and var caged)
        {
            wanted.Add((caged, 1));
            a.Idle = -1;
        }

        // In deep water a race that swims plays its swimming clips (category 4), the ordinary ones where none fits.
        else if (!pose.FullTechnique && technique < 0 && !pose.Carried && cold is { WaterState: 3, Race.Swims: true }
            && Swim(a, stance, speed, lower))
        {
        }

        // A character with a leg down crawls: the grounded clips (category 5), whose lower layer is the whole body as no upper clip plays over it.
        // In a bed it lies as any sleeper does (#391).
        else if (!pose.FullTechnique && pose.Crawling && !pose.InBed && Crawl(a, stance, speed, pose.Task, cold, dt, seed, key, tick, lower, out legs))
        {
        }
        else if (!pose.FullTechnique)
        {
            PickGait(a, cold, stance, technique, pose.Task, speed, dt, seed, key, tick, lower, upper, ref legs, ref legsShare);
        }
    }

    /// <summary>
    /// A downed body's pose: in a bed its clip (the sleeper's), so it lies where a sleeper lies; out cold in a cage its <c>animation KO</c>; else
    /// the lying clip.
    /// </summary>
    void PickDown(CharacterAnimation a, CharacterCold cold, bool inBed, bool inCage, long tick, List<(int Index, float Weight)> wanted)
    {
        // Down in a bed: its clip (the sleeper's), so it lies where a sleeper lies.
        if (inBed && HeldClip(CharacterTask.Sleep, cold, tick) is >= 0 and var lain)
        {
            wanted.Add((lain, 1));
            if (cold.LeavingBed) (a.HeldIndex, a.HeldRate) = (lain, -1);   // Task_GetOutOfBed plays the bed clip backwards (FUN_140348480)
        }

        // Out cold in a cage: its `animation KO` (the Cage's foetal; the out-of-combat pick @ 140520500, docs/animation.md "In a bed or a cage").
        else if (inCage && cold.CageClips.Ko is { } ko && SitClipOf(ko) is >= 0 and var foetal)
        {
            wanted.Add((foetal, 1));
        }
        else if (LyingClip >= 0)
        {
            wanted.Add((LyingClip, 1));
        }

        a.Idle = -1;
    }

    /// <summary>
    /// A character on its feet: its movement clips by speed (the lower ones in its combat stance or under a block too, with the side step), and
    /// over them its upper movement or guard; standing, its held clip or an idle.
    /// </summary>
    void PickGait(CharacterAnimation a, CharacterCold cold, in AnimationStance stance, int technique, CharacterTask task, float speed, float dt,
        ulong seed, ulong key, long tick, List<(int Index, float Weight)> lower, List<(int Index, float Weight)> upper, ref int legs,
        ref float legsShare)
    {
        var wanted = lower;
        bool moving = MathF.Abs(speed) >= MovingSpeed;

        // A block played standing outside the combat stance (a fighter held at a distance, a wait with the running flag): its upper-body clip has
        // no lower-body tracks and no idle plays under a technique, so the legs would stay in the binding pose; they take the combat footwork's
        // lower clip, as in the stance (engine choice).
        bool blockStanding = !moving && !stance.Combat && technique >= 0;
        if (moving || stance.Combat || blockStanding)
            Library.Movement(AnimationArea.Lower, speed, blockStanding ? stance with { Combat = true } : stance, lower);

        // In combat the upper body plays no movement clip: it holds the guard (the original's combat pick has no upper movement pass,
        // docs/animation.md).
        if (moving && !stance.Combat) Library.Movement(AnimationArea.Upper, speed, stance, upper);

        moving = moving && (lower.Count > 0 || upper.Count > 0);
        if (!moving && !((stance.Combat || blockStanding) && lower.Count > 0)) lower.Clear();

        legs = lower.Count;
        if (stance.Combat) SideStep(a, stance, speed, lower, legs, ref legsShare);

        if (moving)
        {
            wanted.AddRange(upper);
            if (stance.Combat && technique < 0 && Library.CombatGuard(stance, speed) is >= 0 and var guard)
            {
                wanted.Add((guard, 1));
                a.Idle = -1;
            }
        }
        else if (technique < 0 && HeldClip(task, cold, tick) is >= 0 and var held)
        {
            a.HeldIndex = held;
            a.HeldRate = HeldRateOf(cold, held, tick);
            wanted.Add((held, 1));
            a.Idle = -1;
        }
        else if (technique < 0)
        {
            PickIdle(a, cold, stance, speed, dt, seed, key, tick, lower, wanted);
        }
    }

    /// <summary>
    /// Side-stepping: the side-step clip takes |side| / (|side| + |forward|) of the legs from the walking clips (none while both together are under
    /// 1: standing).
    /// </summary>
    void SideStep(CharacterAnimation a, in AnimationStance stance, float speed, List<(int Index, float Weight)> lower, int legs, ref float legsShare)
    {
        float side = MathF.Abs(a.StrafeSpeed), ahead = MathF.Abs(speed);
        a.StrafeShare = side + ahead > 1 ? side / (side + ahead) : 0;
        if (a.StrafeShare > 0 && Library.Strafe(stance) is >= 0 and var strafe)
        {
            legsShare = 1 - a.StrafeShare;
            for (int k = 0; k < legs; k++)
                lower[k] = (lower[k].Index, lower[k].Weight * legsShare);

            lower.Add((strafe, a.StrafeShare));
        }
    }

    /// <summary>
    /// A character standing with no technique and no held clip: its own idle stance, its guard in the combat stance, else a random idle.
    /// </summary>
    void PickIdle(CharacterAnimation a, CharacterCold cold, in AnimationStance stance, float speed, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)> lower, List<(int Index, float Weight)> wanted)
    {
        // A carrier standing: a whole-body idle as if it carried nothing, with the carrying arm pose (stand carrying left) over it (engine
        // choice: the data has no whole-body carrying idle).
        bool carrying = stance.CarryingLeft || stance.CarryingRight;
        var standing = carrying ? stance with { CarryingLeft = false, CarryingRight = false } : stance;

        // A human plays its own idle stance whenever that fits, the random idles only otherwise (the original's idle pick, docs/animation.md
        // "Idle stance").
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
        else
        {
            StandingIdle(a, standing, lower.Count > 0, dt, seed, key, tick, wanted);
        }
    }

    /// <summary>
    /// A carrier (FUN_140520000, the arm overlay update every frame): the carrying arm's overlay clip whatever else plays, a technique included
    /// (docs/animation.md "A carrier"). A character that carries nothing and stands holds its hurt parts (FUN_140520000 -> FUN_14051fe60,
    /// docs/animation.md "Injury holds").
    /// </summary>
    void AddArmOverlay(CharacterAnimation a, CharacterCold cold, in AnimationStance stance, bool crawling, float speed,
        List<(int Index, float Weight)> wanted)
    {
        bool swims = cold is { WaterState: 3, Race.Swims: true };
        bool armsFree = !stance.CarryingLeft && !stance.CarryingRight;
        if (stance.CarryingLeft && CarryArmClip >= 0)
        {
            wanted.Add((CarryArmClip, 1));
        }
        else if (armsFree && !crawling && speed <= PainHoldMaxSpeed && !swims && PainHold(a, cold, stance, wanted) is >= 0 and var hold)
        {
            wanted.Add((hold, 1));
        }
    }

    /// <summary>
    /// Lifting or putting down (the carry system's timing): the whole-body lift clip, forwards for a pick-up, backwards from its end for a
    /// put-down; or a stealth knock-out swing, the whole-body knockout clip at x1.2. The clip (-1 for none) and its time.
    /// </summary>
    (int Lift, float LiftTime) PickLift(CharacterAnimation a, CharacterCold cold, CharacterTask task, bool down, bool carried,
        List<(int Index, float Weight)> wanted, ref int legs)
    {
        int lift = -1;
        float liftTime = 0;
        if (!down && !carried && LiftClip >= 0 && cold.LiftTime >= 0 && task is CharacterTask.PickUp or CharacterTask.PutDown)
        {
            var liftDefinition = Library.Definitions[LiftClip];
            float length = Lengths.Of(liftDefinition.Clip);
            lift = LiftClip;
            float playSpeed = liftDefinition.PlaySpeed > 0 ? liftDefinition.PlaySpeed : 1;
            float played = cold.LiftTime * playSpeed;   // the record's play speed (2)
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

        return (lift, liftTime);
    }

    /// <summary>
    /// Ends the posture changes whose clips are done: standing up, crouching, leaving a bed; the end of the task that crouched calls the stance
    /// setter with (0, 0).
    /// </summary>
    void FinishPostures(CharacterAnimation a, CharacterCold cold)
    {
        if (cold.StandingUp) FinishStandUp(a, cold, moving: a.HeldIndex < 0);
        else if (CrouchGoal(cold) != 0) FinishCrouch(a, cold, CrouchGoal(cold));

        if (cold.LeavingBed && !cold.BedExitDone) FinishBedExit(a, cold);

        // The end of the task that crouched calls the stance setter with (0, 0).
        else if (cold.Crouched && cold.Ai is not null) cold.Crouched = false;
    }

    /// <summary>
    /// Takes the clips the character's skeleton lacks out of <paramref name="wanted"/>, and out of its synched count <paramref name="legs"/>.
    /// </summary>
    void DropUnplayable(CharacterAnimation a, CharacterCold cold, List<(int Index, float Weight)> wanted, ref int legs)
    {
        for (int k = wanted.Count - 1; k >= 0; k--)
        {
            if (Plays(a, cold, wanted[k].Index)) continue;

            wanted.RemoveAt(k);
            if (k < legs) legs--;
        }
    }

    /// <summary>
    /// The play speed factor of the held clip the character plays: -1 when it stands up from a crouch (the crouch clip backwards), the crouch goal's
    /// direction on the crouch clip, the lock picker's rate while it picks, else 1.
    /// </summary>
    float HeldRateOf(CharacterCold cold, int held, long tick)
    {
        if (cold.StandingUp && held == SquatClip) return -1;
        if (held == SquatClip && CrouchGoal(cold) != 0) return CrouchGoal(cold);

        return cold.PickingLock is not null && cold.PickWork.Tick >= tick - 1 ? cold.PickWork.Rate : 1;
    }

    /// <summary>
    /// Starts or ends the character's fall and getting-up action, from its posture and ragdoll this tick, and records the posture for the next one.
    /// </summary>
    void UpdateFallAndGetUp(CharacterAnimation a, CharacterCold cold, bool down, bool carried, bool crawling, bool inBed,
        bool inCage, RagdollView ragdoll)
    {
        // Held in a cage or on a pole, it plays the fixture's own pose whatever it was doing: a fall left over from before it was put in, or a
        // get-up, would show over it (docs/game/locks.md "Cages and beds": a captive lies in a cage or stands on a pole, and an awake one stands at
        // once).
        if (inCage) a.Action = -1;

        // A ragdoll falls instead; one in a bed lies on; one already held in a cage takes the cage's own out-cold pose (its `animation KO`) at once.
        if (down && !a.WasDown && !carried && !ragdoll.Has && !inBed && !inCage) StartAction(a, _fall, false);
        if (carried) a.Action = -1;   // held on the shoulder: no falling or getting up

        // Waking with a leg down there is nothing to get up on, and one staying low does not stand: the original's get-up ends with the bone blend
        // and the posture's idle follows (docs/game/ragdolls.md "Getting up"). Waking in a bed it lies on (engine choice); one held in a cage or on a
        // pole stands up at once, no get-up clip.
        else if (!down && a.WasDown && (crawling || inBed || inCage || cold.Medical is { StayingLow: true })) a.Action = -1;
        else if (!down && a.WasDown)
        {
            StartAction(a, _getUp, false);

            // Out of a ragdoll the clip waits at its first key while the bones blend into it (docs/game/ragdolls.md "Getting up").
            if (ragdoll.WasWhole) a.ActionTime = -Meitou.Simulation.Ragdolls.RagdollSystem.GetUpBlend;
        }

        // The leg back up or Stand Up from staying low: posture 2 or 1 ends in 0 and the posture setter queues the get-up task 65 (FUN_1405c7e20),
        // which plays `standing up 3`.
        else if (a.WasCrawling && !down && !carried && !inBed && !inCage && cold.Medical is { Crawling: false, Prone: false })
            StartAction(a, _getUp, false);

        a.WasDown = down;
        a.WasCrawling = cold.Medical is { Crawling: true } or { StayingLow: true };
    }

    /// <summary>
    /// Task_GetOutOfBed leaves when the bed clip played backwards is under 0.1 of its length (FUN_140348480, DAT_14168a318); a character with no clip
    /// playing leaves at once.
    /// </summary>
    static void FinishBedExit(CharacterAnimation a, CharacterCold cold)
    {
        if (a.HeldIndex < 0 || a.HeldRate >= 0)
        {
            cold.BedExitDone = true;
            return;
        }

        for (int k = 0; k < a.Count; k++)
            if (a.Layers[k].Definition == a.HeldIndex && a.Layers[k].Length > 0
                && a.Layers[k].Time < UnCrouchDone * a.Layers[k].Length)
                cold.BedExitDone = true;
    }

    /// <summary>
    /// The clip direction of a crouch goal the character runs: 1 for CROUCH (27), -1 for STAND_UP (28), 0 for any other.
    /// </summary>
    static int CrouchGoal(CharacterCold cold) =>
        cold.Ai is { Behaviour: Ai.AiBehaviour.Crouch, Goal: { } goal } ? goal.Task.Type == Data.Gameplay.Ai.TaskType.Crouch ? 1 : -1 : 0;

    /// <summary>
    /// Task_Crouch (<paramref name="direction"/> 1) sets the animation class's crouched byte once <c>squat T</c> has passed <see cref="CrouchSet"/>;
    /// Task_UnCrouch (-1) clears it once the clip played backwards is under <see cref="UnCrouchDone"/> (FUN_140346090). The byte is never set
    /// together with prone (<see cref="StanceOf"/>).
    /// </summary>
    void FinishCrouch(CharacterAnimation a, CharacterCold cold, int direction)
    {
        for (int k = 0; k < a.Count; k++)
        {
            ref var layer = ref a.Layers[k];
            if (layer.Definition != SquatClip || layer.Length <= 0) continue;

            if (direction > 0 && layer.Time > CrouchSet * layer.Length) cold.Crouched = true;
            else if (direction < 0 && layer.Time < UnCrouchDone * layer.Length) cold.Crouched = false;
        }
    }

    /// <summary>
    /// Ends the Stand Up order (task 28, Task_UnCrouch) when the crouch clip played backwards is under <see cref="UnCrouchDone"/> of its length; with
    /// no clip to play, or a character that is not holding still for it (a walker plays no held clip), at once (the old instant reset).
    /// </summary>
    void FinishStandUp(CharacterAnimation a, CharacterCold cold, bool moving)
    {
        bool done = moving || SquatClip < 0;
        if (!done)
            for (int k = 0; k < a.Count && !done; k++)
                done = a.Layers[k].Definition == SquatClip && a.Layers[k].Length > 0 && a.Layers[k].Time < UnCrouchDone * a.Layers[k].Length;

        if (!done) return;

        cold.StandingUp = false;
        if (cold.Medical is { } medical) medical.StayingLow = false;
    }

    #endregion

    #region Gaits

    /// <summary>
    /// The kneeling work clip of a crawler (<c>crawl use</c>, category 5); -1 when the data has none.
    /// </summary>
    int CrawlUseClip { get; } = library.IndexOf("crawl use");

    /// <summary>
    /// The swimming clips to play (<see cref="AnimationLibrary.SwimMovement"/> while it moves, else <see cref="AnimationLibrary.SwimIdle"/>) added to
    /// <paramref name="wanted"/>; false, with nothing added, when the data has none that fit. They are unsynched whole-body clips, so there is no
    /// walking phase to follow (engine choice; which clip the original plays, <c>FUN_1405c7fd0</c> level 3, is <b>Verified</b> only for getting up).
    /// </summary>
    bool Swim(CharacterAnimation a, in AnimationStance stance, float speed, List<(int Index, float Weight)> wanted)
    {
        if (MathF.Abs(speed) >= MovingSpeed)
        {
            Library.SwimMovement(speed, stance, wanted);
            if (wanted.Count > 0)
            {
                a.Idle = -1;
                return true;
            }
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
    /// The crawling clips to play added to <paramref name="wanted"/> (engine choice of the pick; the original's posture and category 5 are
    /// <b>Observed</b>): moving, <see cref="AnimationLibrary.CrawlMovement"/> (<paramref name="legs"/> its count, the synched phase follows it); else
    /// <c>crawl use</c> for the tasks that kneel to work (the original's medic clip becomes it, docs/game/locks.md), else a
    /// <see cref="AnimationLibrary.CrawlIdles"/> draw kept for its idle time. False, with nothing added, when the data has no crawl clip that fits (a
    /// weapon in the left hand).
    /// </summary>
    bool Crawl(CharacterAnimation a, in AnimationStance stance, float speed, CharacterTask task, CharacterCold cold, float dt, ulong seed, ulong key,
        long tick, List<(int Index, float Weight)> wanted, out int legs)
    {
        legs = 0;
        if (MathF.Abs(speed) >= MovingSpeed)
        {
            Library.CrawlMovement(speed, stance, wanted);
            if (wanted.Count > 0)
            {
                a.Idle = -1;
                legs = wanted.Count;
                return true;
            }
        }

        if (HeldClip(task, cold, tick) is >= 0 and var held && held == TreatingClip && CrawlUseClip >= 0)
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

    #endregion

    #region Blending

    /// <summary>
    /// The layer managers' blend factors (<b>Verified</b>, <c>@ 1406216b0</c>) and the clamp of a wanted layer's play rate (<c>@ 1405b5c30</c>).
    /// </summary>
    const float LowerBlend = 2, UpperBlend = 1.25f, OverlayBlend = 1, MinPlayBlend = 0.5f, MaxPlayBlend = 10;

    /// <summary>
    /// The layers' targets from <paramref name="wanted"/>, the synched phase (from the first <paramref name="legs"/> entries), the weights faded and
    /// the times advanced, with the clip lengths of <paramref name="lengths"/>; returns the root motion of the relocating clips times
    /// <paramref name="rootScale"/>.
    /// </summary>
    Vector2 Settle(CharacterAnimation a, List<(int Index, float Weight)> wanted, int legs, int lift, float liftTime,
        int technique, in CombatView fight,
        float speed, float dt, AnimationLengths lengths, float rootScale, ulong seed, ulong key, long tick, float legsShare = 1)
    {
        a.HoldsStill = false;
        foreach (var (index, _) in wanted)
            if (Library.Definitions[index].DisablesMovement)
            {
                a.HoldsStill = true;
                break;
            }

        // Targets: what is wanted fades in, everything else out. The first clips a character ever plays start at full weight (no fade from the
        // binding pose).
        bool first = a.Count == 0;

        // A looping clip that starts on a character joins at its own point of the loop, and a character's first steps at its own phase, so a squad or
        // herd made on one tick does not idle, graze and walk in lockstep (engine choice: docs/simulation.md "Animation"). One-shot clips (actions,
        // techniques, a lift) start at their beginning.
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
                    ? Rng.Float(Rng.Hash(seed, key, RngPurpose.Animation, (ulong)tick ^ ((ulong)index << 32) ^ 0x4C4F4F50UL)) * lengths.Of(d.Clip)
                    : 0;
                if (index == a.HeldIndex && a.HeldRate < 0) start = lengths.Of(d.Clip) * 0.999f;   // played backwards from its end
                a.Layers[k] = new CharacterAnimation.Layer { Definition = index, Weight = first ? weight : 0, Time = start };
            }

            a.Layers[k].Target = weight;
        }

        AdvancePhase(a, wanted, legs, speed, legsShare, dt);
        return AdvanceLayers(a, lift, liftTime, technique, fight, speed, dt, lengths) * rootScale;
    }

    /// <summary>
    /// The phase advances F x v x the weighted play speed of all the lower movement candidates (synched or not), in cycles per second; the rate is
    /// written only when that blend is picked and kept otherwise (Verified, @ 14051d8e0 / @ 14051d000), so a walk fading out after a stop goes on
    /// stepping at its last rate.
    /// </summary>
    void AdvancePhase(CharacterAnimation a, List<(int Index, float Weight)> wanted, int legs, float speed, float legsShare, float dt)
    {
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
    }

    /// <summary>
    /// Fades every layer towards its target, advances its time and drops the faded-out ones; returns the root motion of the relocating layers,
    /// weighted.
    /// </summary>
    Vector2 AdvanceLayers(CharacterAnimation a, int lift, float liftTime, int technique, in CombatView fight, float speed, float dt,
        AnimationLengths lengths)
    {
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
            // held at the start while getting up blends out of a ragdoll
            else if (layer.Definition == a.Action) layer.Time = MathF.Max(a.ActionTime, 0);
            else if (layer.Target <= 0 && !RunsFading(d, synched)) layer.Time = FadingTime(d, layer, dt);
            else if (IsStrafe(d))
            {
                // signed: a step the other way plays it backwards
                layer.Time = Wrap(layer.Time + dt * a.Rate * d.PlaySpeed * a.StrafeSpeed, length);
            }
            else if (synched) layer.Time = (a.Phase + d.SynchOffset - MathF.Floor(a.Phase + d.SynchOffset)) * length;
            else if (d.MoveSpeed != 0) layer.Time += dt * a.Rate * d.PlaySpeed * speed;
            else layer.Time += dt * (d.PlaySpeed > 0 ? d.PlaySpeed : d.Technique ? 1 : 0) * (layer.Definition == a.HeldIndex ? a.HeldRate : 1);

            // a one-shot clip holds its last frame
            if (!d.Loop && !synched && !IsStrafe(d)) layer.Time = Math.Clamp(layer.Time, 0, length * 0.999f);
            if (layer.Time > before && dt > 0) layer.Rate = (layer.Time - before) / dt;
            if (d.Relocates && layer.Time > before) root += (lengths.Root(d.Clip, layer.Time) - lengths.Root(d.Clip, before)) * layer.Weight;
            if (layer.Weight <= 0 && layer.Target <= 0) continue;   // faded out: dropped

            a.Layers[kept++] = layer;
        }

        a.Count = kept;
        return root;
    }

    /// <summary>
    /// Whether a layer fading out goes on playing (<c>FUN_1405b5c30</c>, the fading list: only a looping layer, +0x5d, advances; <b>Verified</b>).
    /// Synched and side-step clips are the looping movement clips.
    /// </summary>
    static bool RunsFading(AnimationDefinition d, bool synched) => d.Loop || synched || IsStrafe(d) || d.MoveSpeed != 0;

    /// <summary>
    /// A non-looping layer's time while it fades out: frozen, except a block (+0x62, set by <c>FUN_1405b7600</c> for a block technique), which plays
    /// backwards at its rate, lowering the guard.
    /// </summary>
    static float FadingTime(AnimationDefinition d, in CharacterAnimation.Layer layer, float dt) =>
        d.Technique && d.Area == AnimationArea.Upper ? MathF.Max(0, layer.Time - layer.Rate * dt) : layer.Time;

    /// <summary>
    /// A side-step clip (<see cref="AnimationLibrary.Strafe"/>): its time follows the side speed.
    /// </summary>
    static bool IsStrafe(AnimationDefinition d) =>
        d.Area == AnimationArea.Lower && d.MoveSpeed == 0 && d.StrafeSpeedMax > 0 && !d.Technique;

    /// <summary>
    /// <paramref name="time"/> wrapped into 0..<paramref name="length"/>; 0 for a clip without length.
    /// </summary>
    static float Wrap(float time, float length) => length > 0 ? time - MathF.Floor(time / length) * length : 0;

    /// <summary>
    /// How fast a layer's weight moves, per second (docs/animation.md "Layer managers"): the CONSTANTS blend rate times its body half's factor (lower
    /// 2, upper and whole body 1.25, overlay 1); a wanted layer moves at that times its play rate clamped to 0.5..10, one fading out at the plain
    /// rate.
    /// </summary>
    float BlendStep(AnimationDefinition d, bool wanted, float bodyRate, float speed)
    {
        float half = d.Area switch { AnimationArea.Lower => LowerBlend, AnimationArea.Overlay => OverlayBlend, _ => UpperBlend };
        if (!wanted) return BlendRate * half;

        // The play rate in clip seconds per second (a movement clip's follows the speed); a technique, an action or a lift runs on its own clock,
        // taken as 1.
        float play = d.MoveSpeed != 0 ? bodyRate * d.PlaySpeed * speed
            : d.PlaySpeed > 0 ? d.PlaySpeed
            : d.Technique || d.IsAction ? 1 : 0;
        return BlendRate * half * Math.Clamp(play, MinPlayBlend, MaxPlayBlend);
    }

    #endregion

    #region Animals

    /// <summary>
    /// The plans of the animals' clip sets (<see cref="AnimalPlans.Of"/>).
    /// </summary>
    readonly AnimalPlans _animalPlans = new(library, lengths);

    /// <summary>
    /// One tick of an animal's animation (docs/simulation.md "Animation", animals): its own clips only. Moving, the movement clips of its list by
    /// speed (unsynched: F x <c>play speed</c> x v clip seconds per second, F = 1 / its size: the humanoid F = (2 - H) / g with H = 1 and g the size,
    /// an <b>engine reading</b>); standing (in a fight too), one of its idles; a creature technique the combat system plays, over everything; down,
    /// its <c>gotosleep</c> once, then <c>sleep</c> (or the fall's last frame held); no hit reactions (no animal record has <c>stumbles</c>) and no
    /// getting up (none exists: it fades back to standing). Root motion of an attack is scaled by the size.
    /// </summary>
    Vector2 UpdateAnimal(CharacterAnimation a, CharacterCold cold, AnimalPlan plan, float speed, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)> wanted, in CombatView fight, RagdollView ragdoll)
    {
        float size = cold.Medical?.ScaleK ?? 1;
        if (!(size > 0.01f)) size = 1;
        if (a.Rate == 0) a.Rate = 1 / size;
        bool carried = !cold.CarriedBy.IsNone;
        bool down = cold.Medical?.Incapacitated ?? false;
        // Held in a cage or on a pole, an animal is left as it is: the original shows a still frame there, no idle and no fall
        // (docs/game/locks.md "Cages and beds").
        if (cold.InSomething == Locks.HeldState.Imprisoned) return Vector2.Zero;
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
            // Down without any lying clip (the crab, the robot spiders, gorillos: their skeletons have none) it stays in its idle rather than the
            // binding pose.
            if (v >= MovingSpeed && !down) Library.Movement(plan.Movement, v, wanted);
            if (wanted.Count == 0)
            {
                bool fresh = a.Idle < 0 || a.IdleLeft <= 0;
                StandingIdle(a, plan.Idles, dt, seed, key, tick, wanted);
                // A one-shot idle (the bonedog's "idle noloop", 2.5 s) is held only for its own length, not the record's 10 to 40 s idle time (engine
                // choice).
                if (fresh && a.Idle >= 0 && Library.Definitions[a.Idle] is { Loop: false } once)
                    a.IdleLeft = MathF.Min(a.IdleLeft, plan.Lengths.Of(once.Clip) / MathF.Max(once.PlaySpeed, 0.01f));
            }
            else a.Idle = -1;
        }
        return Settle(a, wanted, 0, -1, 0, technique, fight, v, dt, plan.Lengths, size, seed, key, tick);
    }

    #endregion

    #region Pain holds and idles

    /// <summary>
    /// The forward speed over which the injury hold is not played (<c>FUN_140520000</c>: the constant at <c>141683170</c>).
    /// </summary>
    const float PainHoldMaxSpeed = 50;

    /// <summary>
    /// The hurt-part hold the character plays now (<c>FUN_14064a100</c> builds the list, <c>FUN_140520000</c> and <c>FUN_14051fe60</c> pick;
    /// <b>Verified</b>, docs/animation.md "Injury holds"): the parts whose health fraction is under their <c>pain anim</c> threshold per cent, the
    /// lowest fraction first; the first whose record the free arms allow wins: a record that uses the left arm plays as is when the left arm is free
    /// and works, else as its <c>-R</c> mirror when the right arm is; a record that uses the right arm needs it free. An arm is busy while a hand
    /// holds a weapon or an upper / whole-body layer uses it. -1: none.
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
            if (part.Template.PainAnim is not { } record) continue;
            if (!(part.Fraction < part.Template.PainThreshold * 0.01f)) continue;
            if (hurt is not null && SameFraction(hurt, part.Fraction)) continue;

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

    /// <summary>
    /// Whether a hurt part with pain fraction <paramref name="fraction"/> is already in <paramref name="hurt"/>.
    /// </summary>
    static bool SameFraction(List<(float Fraction, string Record)> hurt, float fraction)
    {
        foreach (var h in hurt)
            if (h.Fraction == fraction) return true;
        return false;
    }

    /// <summary>
    /// A standing character's idle: out of combat a whole-body idle, in combat an upper-body guard over the lower movement blend (<paramref
    /// name="legs"/>: the lower clips are already wanted), kept until its time is up and then drawn again by <c>idle chance</c>.
    /// </summary>
    void StandingIdle(CharacterAnimation a, in AnimationStance stance, bool legs, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)> wanted) =>
        StandingIdle(a, legs ? Library.CombatIdles(stance) : Library.Idles(stance), dt, seed, key, tick, wanted);

    /// <summary>
    /// The idle among <paramref name="idles"/> (index, <c>idle chance</c>): kept until its time is up, then drawn again by weight.
    /// </summary>
    void StandingIdle(CharacterAnimation a, IReadOnlyList<(int Index, float Weight)> idles, float dt, ulong seed, ulong key, long tick,
        List<(int Index, float Weight)> wanted)
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
                    if ((pick -= w) < 0)
                    {
                        chosen = index;
                        break;
                    }
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

    #endregion

    #region Stumbles and stances

    /// <summary>
    /// The fraction of a stumble clip after which the stumble state ends (<c>DAT_14168db34</c>).
    /// </summary>
    public const float StumbleEnd = 0.98f;

    /// <summary>
    /// The hit reaction clip of a hit, for <see cref="CombatSystem.StumbleChooser"/> (docs/animation.md "Hit reactions", <b>Verified</b>; whether one
    /// is wanted at all, and how big, the combat system decides): one of the hit part's stumbles with that <c>big stumble</c> and <c>stumble from</c>
    /// = the side, drawn by weight with <paramref name="roll"/>; with its length in seconds at its play speed. Null when none fits.
    /// </summary>
    static (int Clip, float Seconds)? ChooseStumble(AnimationLibrary library, AnimationLengths lengths, CharacterCold cold,
        string part, bool big, int side,
        float roll)
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
            if ((pick -= w) < 0)
            {
                chosen = index;
                break;
            }
        }

        var d = library.Definitions[chosen];

        // The stumble state lasts until the clip, at its record's play speed, is past StumbleEnd of its length (Verified, FUN_140435570 ->
        // FUN_1405b3a40); no stat scales it.
        float seconds = StumbleEnd * lengths.Of(d.Clip) / (d.PlaySpeed > 0 ? d.PlaySpeed : 1);
        return seconds > 0 ? (chosen, seconds) : null;
    }

    /// <summary>
    /// The animation class's category (FUN_140520500, <b>Verified</b>): 4 in deep water for a race that swims, else 5 when held down or crawling,
    /// else 0.
    /// </summary>
    static int CategoryOf(CharacterCold cold) =>
        cold is { WaterState: 3, Race.Swims: true } ? AnimationLibrary.SwimmingCategory
        : cold.CarriedBy.IsNone && cold.Medical is { Prone: true } ? AnimationLibrary.GroundedCategory : 0;

    /// <summary>
    /// What a character holds and does, as the clips' conditions read it; leg health in per cent weighs the limp clips.
    /// </summary>
    static AnimationStance StanceOf(CharacterCold cold)
    {
        // The crawl records are prone; no standing clip (stumbles included) fits a crawler. Crouched is never together with prone (FUN_14051dda0).
        var stance = new AnimationStance
        {
            Right = new HandHold(cold.DrawnWeapon),
            Combat = cold.InCombat,
            Stealth = (cold.Orders & CharacterOrders.Sneak) != 0,
            CarryingLeft = !cold.Carrying.IsNone,
            BeingCarried = !cold.CarriedBy.IsNone,
            Prone = cold.CarriedBy.IsNone && cold.Medical is { Prone: true },
            Crouched = cold.Crouched && !(cold.CarriedBy.IsNone && cold.Medical is { Prone: true }),
        };
        stance = stance with { Category = CategoryOf(cold) };
        if (cold.Medical is not { } body) return stance;
        float left = 100, right = 100;
        foreach (var part in body.PartSpan)
        {
            if (part.Template.Type != Data.Gameplay.Bodies.BodyPartType.Leg) continue;
            if (part.Template.Side == Data.Gameplay.Bodies.BodySide.Left) left = MathF.Min(left, part.Fraction * 100);
            else right = MathF.Min(right, part.Fraction * 100);
        }
        return stance with { LeftLeg = left, RightLeg = right };
    }

    #endregion

    #region Layer slots

    /// <summary>
    /// A character lying in its ragdoll keeps the layers it had when it went down, unchanged (engine choice): the ragdoll starts from that pose on
    /// the tick it goes down, and the bones without a body (hands, feet, clavicles) keep it. One without layers yet (just loaded) takes the lying
    /// clip instead.
    /// </summary>
    static bool HoldsForRagdoll(CharacterAnimation a, bool down, bool carried, RagdollView ragdoll)
    {
        if (!down || carried || !ragdoll.Has || a.Count == 0) return false;
        a.WasDown = true;
        a.Action = -1;
        return true;
    }

    /// <summary>
    /// Starts a one-shot clip from its first frame (nothing when the data has no such clip).
    /// </summary>
    static void StartAction(CharacterAnimation a, int clip, bool stumble)
    {
        if (clip < 0) return;

        a.Action = clip;
        a.ActionTime = 0;
        a.ActionIsStumble = stumble;
        for (int k = 0; k < a.Count; k++)
            if (a.Layers[k].Definition == clip) a.Layers[k].Time = 0;
    }

    /// <summary>
    /// The index in <paramref name="a"/>'s layers of the layer playing <paramref name="definition"/>; -1 when none does.
    /// </summary>
    static int Find(CharacterAnimation a, int definition)
    {
        for (int k = 0; k < a.Count; k++) if (a.Layers[k].Definition == definition) return k;
        return -1;
    }

    /// <summary>
    /// Frees the faintest fading-out layer when all are in use; -1 when every layer is wanted.
    /// </summary>
    static int Evict(CharacterAnimation a)
    {
        int worst = -1;
        for (int k = 0; k < a.Count; k++)
            if (a.Layers[k].Target <= 0 && (worst < 0 || a.Layers[k].Weight < a.Layers[worst].Weight)) worst = k;
        return worst;
    }

    #endregion

    #region Publishing

    /// <summary>
    /// The layers to publish for a snapshot, written into <paramref name="into"/> (the snapshot's own list, cleared first): the record name (the
    /// renderer finds its track masks by it), the clip time in seconds and the weight. The weights are normalised as Kenshi's layers are
    /// (docs/animation.md "Layer managers"): the upper body (upper and whole-body clips) to a total of 1, the lower body to 1 minus the whole-body
    /// clips' weight (actions and <c>all</c> clips; a block is played without its lower-body tracks and keeps the legs' clips); overlays are left
    /// alone. Within a body half the layers fading out keep their weights and the wanted ones fill what is left (scaled up as well as down), so a
    /// cross-fade never lets the binding pose through; with only fading layers the total falls with them.
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
                case AnimationArea.Lower:
                    if (wanted) lowerWanted += layer.Weight;
                    else lowerFading += layer.Weight;
                    break;
                case AnimationArea.Overlay:
                    break;
                default:
                    if (wanted) upperWanted += layer.Weight;
                    else upperFading += layer.Weight;

                    // a block keeps the legs' clips
                    if (d.Area == AnimationArea.All || d.IsAction && !d.Technique)
                    {
                        if (wanted) fullWanted += layer.Weight;
                        else fullFading += layer.Weight;
                    }

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
    /// The scales of a body half's wanted and fading layers for a total of <paramref name="total"/> (Kenshi's layer manager, <c>@ 1405b5c30</c>): the
    /// wanted are scaled up to fill what the fading leave, then everything is scaled down if it is over the total.
    /// </summary>
    static (float Wanted, float Fading) Fill(float wanted, float fading, float total)
    {
        float wantedScale = wanted > 0 && total - fading > wanted ? (total - fading) / wanted : 1;
        float sum = wanted * wantedScale + fading;
        float down = sum > total && sum > 0 ? total / sum : 1;
        return (wantedScale * down, down);
    }

    #endregion
}

/// <summary>
/// What the world asks to turn a character's animation state into the layers a snapshot carries.
/// </summary>
public interface IAnimationSource
{
    /// <summary>
    /// Writes the layers into <paramref name="into"/> (cleared first) and returns it.
    /// </summary>
    IReadOnlyList<AnimationLayer> Publish(CharacterAnimation animation, List<AnimationLayer> into);
}

/// <summary>
/// The combat state the animation follows: the technique playing (library index, -1 none) and its progress (0 to 1), and the last hit taken (tick, -1
/// none; heavy, from behind, the body part's LOCATIONAL_DAMAGE string id), and the hit reaction clip the combat system started (library index, -1
/// none) and its tick.
/// </summary>
public readonly record struct CombatView(
    int Technique,
    float Progress,
    int HitTick,
    bool HitHeavy,
    bool HitBehind,
    string? HitPart,
    int StumbleClip = -1,
    int StumbleStart = -1)
{
    /// <summary>
    /// No technique, no hit, no hit reaction.
    /// </summary>
    public static readonly CombatView None = new(-1, 0, -1, false, false, null);
}

/// <summary>
/// What the animation of one character needs to know of its ragdoll: whether it has one (it falls by it, not by the fall clip) and whether the whole
/// body was one.
/// </summary>
public readonly record struct RagdollView(bool Has, bool WasWhole);