using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation.Player;

/// <summary>
/// The toggles of the HUD's orders panel (docs/game/ui-screens.md section 4), per character. Saved as the GAMESTATE_CHARACTER stance bools of the
/// player's characters (docs/formats/save.md) and the <c>stealth</c> bool; what each does here is in docs/simulation.md "Orders".
/// </summary>
[Flags]
public enum CharacterOrders : ushort
{
    /// <summary>
    /// No toggle set.
    /// </summary>
    None = 0,
    /// <summary>
    /// Defensive mode: +20 melee defence (the combat code's guarding state) and no attacks of its own (the button's tooltip, Verified).
    /// </summary>
    Block = 1,
    /// <summary>
    /// Hold position: attacks on sight and defends only against targets passing <see cref="Orders.HoldPost"/>, walks only within the hold area
    /// (docs/simulation.md "Order toggles").
    /// </summary>
    Hold = 2,
    /// <summary>
    /// Unused: the original's saved <c>chase mode</c> is the jobs switch (<see cref="Jobs"/>, docs/game/ai-tasks.md "Perma-jobs"); an old Meitou
    /// save's value is dropped.
    /// </summary>
    Chase = 4,
    /// <summary>
    /// Non-combat mode: no attack on sight, no help for others, and a lesser target (score -1); it still defends itself (the tooltip: "unless
    /// attacked first").
    /// </summary>
    Passive = 8,
    /// <summary>
    /// Ranged mode (on for every NPC): a crossbow carrier shoots until dry or a threat is within 40; off it never shoots (docs/simulation.md "Order
    /// toggles").
    /// </summary>
    Ranged = 16,
    /// <summary>
    /// Taunt mode: +2 in enemies' target score and its crowd penalty x0.35 (<see cref="Combat.TargetScore"/>).
    /// </summary>
    Taunt = 32,
    /// <summary>
    /// Sneaking: slower, harder to see and to hear, seen only once a looker's spot meter fills, trains stealth (docs/game/perception.md,
    /// docs/game/pathfinding.md "Sneaking").
    /// </summary>
    Sneak = 64,
    /// <summary>
    /// Enables automatic assigned jobs; direct player orders remain available.
    /// </summary>
    Jobs = 128,
}

/// <summary>
/// How exposed a sneaking character is to the hostile NPCs about (docs/game/perception.md "Spotting a sneaker"); ordered, the most exposed last.
/// </summary>
public enum StealthExposure : byte
{
    /// <summary>
    /// No hostile NPC has it within sight range.
    /// </summary>
    Unobserved,
    /// <summary>
    /// A hostile NPC has it within sight range but does not see it.
    /// </summary>
    Near,
    /// <summary>
    /// A hostile NPC sees it and its spot meter is filling (the original's yellow).
    /// </summary>
    Noticed,
    /// <summary>
    /// A hostile NPC's spot meter on it is full: it is seen (the original's red).
    /// </summary>
    Detected,
}

/// <summary>
/// A hostile NPC noticing or detecting a sneaker (its <see cref="Level"/> at <see cref="Tick"/>); the original keeps these 4 s for its pointers.
/// </summary>
public readonly record struct StealthWatcher(CharacterId Looker, StealthExposure Level, long Tick);

/// <summary>
/// A watcher as the HUD draws it: where the looker stands and how far it got.
/// </summary>
public readonly record struct WatcherView(Vector3 Position, StealthExposure Level);

/// <summary>
/// Turns an order on, off (<paramref name="On"/>) or over (null) for the characters, or for the player's selection when the list is empty.
/// </summary>
public sealed record OrdersCommand(IReadOnlyList<CharacterId> Characters, CharacterOrders Order, bool? On = null) : SimCommand;

/// <summary>
/// Steps the speed mode move orders use (the original's <c>cycle_run_speed</c> and the panel's speed arrows) by <paramref name="Step"/>, wrapping
/// through walk, run and no cap; every character takes the first one's new mode.
/// </summary>
public sealed record RunSpeedCommand(IReadOnlyList<CharacterId> Characters, int Step = 1) : SimCommand;

/// <summary>
/// The AI's manoeuvre mode (<c>AI::ordersMovement</c> +0x2bc, KenshiLib <c>AIManuverabilityOrders</c>): how far from its centre of movement a
/// character takes targets and job targets (<see cref="Orders.InArea"/>). Set by the standing orders FAR (8, <see cref="ManoeuvreCommand"/> with
/// <see cref="RoamFar"/>) and NEAR (9, <see cref="StayClose"/>).
/// </summary>
public enum ManoeuvreMode : byte
{
    /// <summary>
    /// HOLD_GROUND (0): targets within twice the meiDistance and its tolerance (<see cref="Combat.TargetScore.RangeOf"/>); no order sets it.
    /// </summary>
    HoldGround = 0,
    /// <summary>
    /// STAY_CLOSE (1), set by NEAR (9): targets near the centre of movement, which every order move sets.
    /// </summary>
    StayClose = 1,
    /// <summary>
    /// ROAM_FAR (2), set by FAR (8) and every character's default: the plain range term, the centre of movement unused.
    /// </summary>
    RoamFar = 2,
}

/// <summary>
/// Standing order FAR (8) or NEAR (9) for the characters, or the player's selection when the list is empty (<c>AI::setManuveringFreedomLevel</c>).
/// </summary>
public sealed record ManoeuvreCommand(IReadOnlyList<CharacterId> Characters, ManoeuvreMode Mode) : SimCommand;

/// <summary>
/// Stand Up (order 28): the characters crawling by choice stand (<see cref="Bodies.MedicalState.StayingLow"/>).
/// </summary>
public sealed record StandUpCommand(IReadOnlyList<CharacterId> Characters) : SimCommand;

/// <summary>
/// The orders' rules and their save keys.
/// </summary>
public static class Orders
{
    /// <summary>
    /// How near a standing target must be for a holding character to take it on (<c>AI::isTargetInRangeForHOLDMode_hostiles</c>, Verified literal;
    /// <see cref="HoldPost"/>).
    /// </summary>
    public const float HoldReach = 40;

    /// <summary>
    /// The GAMESTATE_CHARACTER bool of each saved order. Block = defensive and Passive = non combat follow the tooltips; Hold = <c>passive mode</c>
    /// is by elimination (<b>Unknown</b>); <c>chase mode</c> is the jobs switch (<b>Verified</b>: the writer <c>FUN_1406280a0</c> stores the orders
    /// receiver's switch byte +0x35, the loader passes it back as toggle 15).
    /// </summary>
    public static readonly (CharacterOrders Order, string Key)[] SaveKeys =
    [
        (CharacterOrders.Block, "defensive mode"), (CharacterOrders.Jobs, "chase mode"), (CharacterOrders.Passive, "non combat mode"),
        (CharacterOrders.Hold, "passive mode"), (CharacterOrders.Ranged, "ranged mode"),
        (CharacterOrders.Taunt, "taunt mode"), (CharacterOrders.Sneak, "stealth"),
    ];

    /// <summary>
    /// The GAMESTATE_CHARACTER key of the character's speed mode.
    /// </summary>
    public const string SpeedModeKey = "speed mode";
    /// <summary>
    /// The jobs switch as older Meitou saves wrote it (GAMESTATE_CHARACTER): read, never written; the original's key is GAMESTATE_AI <c>jobs</c>
    /// (<see cref="Saving.PermaJobSave"/>).
    /// </summary>
    public const string JobsKey = "meitou jobs enabled";
    /// <summary>
    /// The toggles a new player character starts with: jobs and ranged on.
    /// </summary>
    public const CharacterOrders PlayerDefaults = CharacterOrders.Jobs | CharacterOrders.Ranged;

    /// <summary>
    /// Whether <paramref name="cold"/> has <paramref name="order"/> on; false for null.
    /// </summary>
    public static bool Has(CharacterCold? cold, CharacterOrders order) => cold is not null && (cold.Orders & order) != 0;

    /// <summary>
    /// The ranged mode (CharStats <c>+0x129</c>): the player's Ranged toggle for its characters, always on for an NPC (docs/game/combat.md "Fight
    /// lifecycle and orders").
    /// </summary>
    public static bool RangedMode(CharacterCold cold) => !cold.IsPlayer || Has(cold, CharacterOrders.Ranged);

    /// <summary>
    /// Whether a holding character's attack on sight skips the Hold post test (<c>FUN_1407f2840</c> in <c>isTargetInRangeForHOLDMode_hostiles</c>,
    /// <b>Verified</b>): in <see cref="RangedMode"/>, upright (not staying low or crawling), and manning a turret, or with both arms working and a
    /// crossbow in hand or on its back (a crossbow on the hip does not count). The original's last test, a virtual of the character for the weapon
    /// type, is not decoded and is taken as true.
    /// </summary>
    public static bool RangedExempt(CharacterCold cold) =>
        RangedMode(cold) && cold.Medical is { Prone: false } medical
        && (cold.Gunning is not null || medical.BothArmsWork && cold.Fighter?.Crossbow is { } x
            && (x.AtBack || cold.DrawnWeapon == Data.Gameplay.HandHold.OfCategory((int)Data.Gameplay.Combat.WeaponCategory.Bow).Weapon));

    /// <summary>
    /// Writes the orders and the run speed into a character's GAMESTATE_CHARACTER record.
    /// </summary>
    public static void Write(FcsRecord state, CharacterCold cold)
    {
        foreach (var (order, key) in SaveKeys) state.Bools[key] = (cold.Orders & order) != 0;
        // The jobs switch is the AI record's (Saving.PermaJobSave); the key older Meitou saves wrote is dropped.
        state.Bools.Remove(JobsKey);
        state.Ints[SpeedModeKey] = cold.RunSpeed;
    }

    /// <summary>
    /// Reads them back; absent keys leave the character's defaults.
    /// </summary>
    public static void Read(FcsRecord state, CharacterCold cold)
    {
        foreach (var (order, key) in SaveKeys)
            if (state.Bools.TryGetValue(key, out bool on)) cold.Orders = on ? cold.Orders | order : cold.Orders & ~order;
        if (state.Bools.TryGetValue(JobsKey, out bool jobs))
            cold.Orders = jobs ? cold.Orders | CharacterOrders.Jobs : cold.Orders & ~CharacterOrders.Jobs;
        if (state.Ints.TryGetValue(SpeedModeKey, out int mode) && mode is >= 0 and <= SpeedMode.Group) cold.RunSpeed = (byte)mode;
    }

    /// <summary>
    /// The stealth term s of a character in [0, 1] (<c>FUN_140884850</c>): its stealth skill x 0.75 while carrying someone, x 2 while crawling
    /// (posture not 0), x the encumbrance factor, x the body term (<see cref="Bodies.MedicalState.StealthBodyFactor"/>), x the gear's factor
    /// (<see cref="Combat.Fighter.StealthMult"/>), x the fitted limbs' factor, x 0.01. Not the stat multiplier's table: the game never sends stealth
    /// through it.
    /// </summary>
    public static float Stealth(CharacterCold cold)
    {
        if (cold.Stats is not { } stats) return 0;
        float s = stats[StatsEnumerated.Stealth] * stats.Scale * (cold.Fighter is { } f ? f.EncumbranceFactor * f.StealthMult : 1);
        if (cold.Medical is { } medical)
        {
            s *= medical.StealthBodyFactor(stats.ScaledToughness)
                * Bodies.StatMultiplier.LimbItemFactor(medical, StatsEnumerated.Stealth, medical.Aggregates());
            if (medical.Prone) s *= 2;
        }
        if (!cold.Carrying.IsNone) s *= 0.75f;
        return Math.Clamp(s * 0.01f, 0, 1);
    }

    /// <summary>
    /// The sneaking speed (docs/game/pathfinding.md "Sneaking", Observed): X = speed - 11, X = min(X, lerp(12, X, clamp((s - 0.1) / 0.8))), then X +
    /// 11, at least 11. A clumsy sneaker creeps at 23 units a second; from stealth 90 on the cap is gone.
    /// </summary>
    public static float SneakSpeed(float speed, float stealth)
    {
        float x = speed - 11;
        float t = Math.Clamp((stealth - 0.1f) / 0.8f, 0, 1);
        x = MathF.Min(x, 12 + (x - 12) * t);
        return MathF.Max(x + 11, 11);
    }

    /// <summary>
    /// The sneaking target's view factor (docs/game/perception.md "Sight", Verified constants): v x 0.75 when v is at least 0.5, else x 0.5, then x
    /// lerp(s, 1, 0.6). Passing this test only fills the looker's spot meter (<see cref="SpotSeconds"/>); the target counts as seen once the meter is
    /// full.
    /// </summary>
    public static float SneakView(float v, float stealth) => v * (v >= 0.5f ? 0.75f : 0.5f) * (1 + (0.6f - 1) * stealth);

    /// <summary>
    /// The looker's alertness f in the spot time (<c>FUN_140856cb0</c>): 0.5 when the looker has no current AI goal to read it from (Verified
    /// literal); the goal's own value (<b>Unknown</b> meaning) is not modelled, so every looker uses 0.5.
    /// </summary>
    public const float SpotAlertness = 0.5f;

    /// <summary>
    /// The seconds a looker's spot meter takes to fill on a sneaking target it sees with view factor <paramref name="v"/> (docs/game/perception.md
    /// "Spotting a sneaker"): lerp(v, 0.5 + 5 s, 0.5) x lerp(f, 4, 1), f = <see cref="SpotAlertness"/>. Clear in view a sneaker lasts 1.25 s; a
    /// master sneaker at the edge of sight about 14 s.
    /// </summary>
    public static float SpotSeconds(float v, float stealth) => (0.5f + 5 * stealth * (1 - Math.Clamp(v, 0, 1))) * (4 + (1 - 4) * SpotAlertness);

    /// <summary>
    /// The seconds a full spot meter takes to empty once the looker no longer sees its target (<c>FUN_140853aa0</c>: it falls by dt / 260, Verified
    /// literal).
    /// </summary>
    public const float SpotForgetSeconds = 260;

    /// <summary>
    /// A sneaking mover's noise range: lerp(s, 450 f, 80) (docs/game/perception.md "Hearing", Verified constants), f the weather's factor
    /// (<see cref="WeatherSystem.NoiseFactor"/>, 1 in clear weather).
    /// </summary>
    public static float SneakNoise(float stealth, float weather = 1) => 450 * weather + (80 - 450 * weather) * stealth;

    /// <summary>
    /// The base chance to hear a sneaking target: 0.05 for an awake listener (StateBroadcast <c>+0xb4</c> 0), 0.1 for a sleeper
    /// (<c>FUN_140857500</c>, Verified constants).
    /// </summary>
    public static float SneakHearBase(bool awake) => awake ? 0.05f : 0.1f;

    /// <summary>
    /// The chance to hear a sneaking target at <paramref name="distance"/>: base x (1 - s) x (1 - d^2 / N^2) ("Hearing").
    /// </summary>
    public static float SneakHearChance(float distance, float noise, float stealth, bool awake) =>
        noise > 0 && distance < noise ? SneakHearBase(awake) * (1 - stealth) * (1 - distance * distance / (noise * noise)) : 0;

    /// <summary>
    /// The Hold order's post test (<c>AI::isTargetInRangeForHOLDMode_hostiles</c>, docs/game/perception.md "The attack-on-sight tests",
    /// <b>Verified</b>): a target slower than 2, or <paramref name="held"/> (posture above 1, crawling or playing dead, or a swing running: the
    /// movement's animation-driven flag), must be within <see cref="HoldReach"/> of the looker; one more than <see cref="HoldFar"/> from the hold
    /// point within 60. Every distance is 3D (Vector3::squaredDistance, FUN_140598630), the hold point (CharStats +0x130..+0x138) included.
    /// </summary>
    public static bool HoldPost(Vector3 looker, Vector3 hold, Vector3 target, Vector3 velocity, float targetYaw, bool held, bool targetFighting)
    {
        float d2 = Vector3.DistanceSquared(target, looker);
        if ((velocity.X * velocity.X + velocity.Z * velocity.Z < 2 * 2 || held) && d2 > HoldReach * HoldReach) return false;
        float far = HoldFar(looker, target, targetYaw, targetFighting);
        return Vector3.DistanceSquared(hold, target) <= far * far || d2 <= 60 * 60;
    }

    /// <summary>
    /// The distance from the hold point within which a target is taken without the 60 test: 80, or 140 when the target is in a fight (any fight,
    /// <c>Character::isInCombatMode</c> <c>FUN_140436610</c>) and its heading points towards the looker (dot with the unit vector target to looker
    /// above 0). The heading (the movement's +0xd0) is read as the yaw.
    /// </summary>
    public static float HoldFar(Vector3 looker, Vector3 target, float targetYaw, bool targetFighting)
    {
        if (!targetFighting) return 80;
        var to = looker - target;
        return MathF.Sin(targetYaw) * to.X + MathF.Cos(targetYaw) * to.Z > 0 ? 140 : 80;
    }

    /// <summary>
    /// Squared reach of ROAM_FAR (<c>DAT_1416efdcc</c>, about 6517 units), the constructor's <c>meiDistance</c> and <c>meiDistanceTolerance</c>
    /// (<c>AI::AI</c>; no other writer) and the extras of the other modes.
    /// </summary>
    public const float RoamFarArea2 = 42467328, MeiDistance = 18, MeiTolerance = 5, HoldGroundExtra = 40, StayCloseExtra = 200;

    /// <summary>
    /// The AI's area test <c>FUN_140597790</c> (<c>isTargetInRangeForOrders(Vector3)</c>, <b>Verified</b>): a zero point is in; ROAM_FAR measures the
    /// 3D squared distance from the character itself, HOLD_GROUND (meiDistance + 40 + tolerance) and STAY_CLOSE (meiDistance + 200 + tolerance) from
    /// the centre of movement taken at the character's height. HOLD_GROUND is also in when the squared distance d2 is under s2 squared, s2 being the
    /// squared distance from the AI's own position record (<c>AI +0x18 +0xc8</c>, here <paramref name="looker"/>, <b>Observed</b>: the record the AI
    /// reads its zone from) to the centre at the centre's own height (operands decoded from the disassembly, <b>Verified</b>; a unit slip of the
    /// original that admits every target once the looker is more than a few units from its centre). No order sets that mode (docs/game/combat.md
    /// "Standing orders").
    /// </summary>
    public static bool InArea(ManoeuvreMode mode, Vector3 looker, Vector3? centre, Vector3 target)
    {
        if (target == Vector3.Zero) return true;
        var from = mode == ManoeuvreMode.RoamFar || centre is not { } c ? looker : new Vector3(c.X, looker.Y, c.Z);
        float d2 = Vector3.DistanceSquared(from, target);
        float reach = MeiDistance + MeiTolerance + (mode == ManoeuvreMode.StayClose ? StayCloseExtra : HoldGroundExtra);
        if (mode == ManoeuvreMode.RoamFar) return d2 < RoamFarArea2;
        if (d2 < reach * reach) return true;
        if (mode != ManoeuvreMode.HoldGround || centre is not { } centreAt) return false;
        float s2 = Vector3.DistanceSquared(looker, centreAt);
        return d2 < s2 * s2;
    }

    /// <summary>
    /// The point a holding character holds (<see cref="CharacterCold.HoldPoint"/>, else where it stands).
    /// </summary>
    public static Vector3 HoldPointOf(CharacterCold cold, Vector3 at) => cold.HoldPoint ?? at;

    /// <summary>
    /// The Hold toggle (standing order 12): holding, the character not sneaking (<c>CharStats::holdPositionMode</c>).
    /// </summary>
    public static bool Holding(CharacterCold? cold) =>
        cold is not null && (cold.Orders & (CharacterOrders.Hold | CharacterOrders.Sneak)) == CharacterOrders.Hold;

    /// <summary>
    /// Applies the order commands (the player system's inputs).
    /// </summary>
    internal static void Apply(World world, SimCommand command)
    {
        var table = world.Characters;
        switch (command)
        {
            case OrdersCommand set:
                // A toggle sets the whole selection to the opposite of the first character's state, so a mixed selection ends up all on or all off.
                bool? on = set.On;
                foreach (var id in set.Characters.Count > 0 ? set.Characters : world.Player.Selection.ToArray())
                {
                    if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
                    on ??= (cold.Orders & set.Order) == 0;
                    cold.Orders = on.Value ? cold.Orders | set.Order : cold.Orders & ~set.Order;
                    if ((set.Order & CharacterOrders.Jobs) != 0) DecisionSystem.Request(cold);   // the jobs switch: decided again
                    // Sneak off stands one staying low (setStandingOrder 4).
                    if ((set.Order & CharacterOrders.Sneak) != 0 && !on.Value && cold.Medical is { } low) low.StayingLow = false;
                    // Turning Hold on holds where the character stands (setStandingOrder 12: hold point = position).
                    if ((set.Order & CharacterOrders.Hold) != 0) cold.HoldPoint = on.Value ? table.Next[slot].Position : null;
                }
                break;
            case RunSpeedCommand speed:
                int? mode = null;
                int group = -1;
                var targets = speed.Characters.Count > 0 ? speed.Characters : world.Player.Selection.ToArray();
                // The arrows do not step while anyone selected is staying low (posture 1, FUN_1407f3a20): the lead's displayed mode is applied to
                // everyone as it is.
                int step = speed.Step;
                foreach (var id in targets)
                    if (table.TryResolveNext(id, out int s)
                        && table.Cold(s) is { IsPlayer: true, Medical: { StayingLow: true, Crawling: false } stayLow }
                        && stayLow.Prone)
                        step = 0;
                foreach (var id in targets)
                {
                    if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
                    // The lead's mode is the one shown, even when the lead is the one staying low.
                    mode ??= ((cold.RunSpeed + step) % SpeedMode.Count + SpeedMode.Count) % SpeedMode.Count;
                    // One staying low (not crippled) stands up instead of changing speed (setStandingOrder 0, 1, 2, 16).
                    if (cold.Medical is { StayingLow: true, Crawling: false } low && low.Prone)
                    {
                        low.StayingLow = false;
                        continue;
                    }
                    cold.RunSpeed = (byte)mode.Value;
                    // Group speed gives the characters set together one speed group (PlayerInterface::assignSpeedGroup), keyed by the first one.
                    group = mode == SpeedMode.Group ? group < 0 ? slot : group : -1;
                    cold.SpeedGroup = group;
                }
                break;
            case ManoeuvreCommand manoeuvre:
                // setStandingOrder 8 / 9 (Verified): FAR is ROAM_FAR and clears the centre of movement, NEAR is STAY_CLOSE with the centre at the
                // character's position.
                foreach (var id in manoeuvre.Characters.Count > 0 ? manoeuvre.Characters : world.Player.Selection.ToArray())
                {
                    if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
                    cold.Manoeuvre = manoeuvre.Mode;
                    cold.CentreOfMovement = manoeuvre.Mode == ManoeuvreMode.RoamFar ? null : table.Next[slot].Position;
                }
                break;
            case StandUpCommand stand:
                foreach (var id in stand.Characters)
                    if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true, Medical: { StayingLow: true } } staying)
                        staying.StandingUp = true;
                break;
        }
    }
}