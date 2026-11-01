using System.Numerics;
using Meitou.Data.Characters;

namespace Meitou.Simulation;

/// <summary>One animation playing on a character: the skeleton animation's name, its time in seconds and its blend weight.</summary>
public readonly record struct AnimationLayer(string Name, float Time, float Weight)
{
    /// <summary>The clip's length in seconds (0: unknown) and the animation library index of the record playing it (-1: none): the sound events riding on the record fire at fractions of the length (docs/audio.md "Footsteps and animation events"). Set by the animation system; not part of what is drawn.</summary>
    public float Length { get; init; }
    public int Definition { get; init; } = -1;
}

/// <summary>What a character is doing about its body: nothing, bandaging someone (<see cref="CharacterTask.Treat"/>) or resting (<see cref="CharacterTask.Rest"/>).</summary>
public enum CareKind { None, Treating, Resting }

/// <summary>
/// The health the interface shows: blood (0..1 of capacity), the worst body part (fraction of its maximum HP), hunger (3 fed .. 0 starving) and whether it is out cold or dead; for every
/// character also whether first aid has anything to bandage (<see cref="NeedsAid"/>) or a limb to splint (<see cref="NeedsSplint"/>, the menu's test with skill 50), what the character is
/// doing (<see cref="Care"/>), its medic job switch and what is left of a knockout (<see cref="KoTimer"/>, the header's "Unconscious (N)").
/// </summary>
public readonly record struct BodyStatus(float Blood, float WorstPart, float Hunger, bool Unconscious, bool Dead, bool NeedsAid = false, CareKind Care = CareKind.None, bool MedicJob = false, bool RescueJob = false,
    bool NeedsSplint = false, float KoTimer = 0, bool PlayingDead = false, bool StayingLow = false, bool NeedsRobotAid = false)
{
    /// <summary>The context menu's two independent tests (<c>FUN_1407c05a0</c> with the kit kind 0 and 1, docs/game/ui-input.md "Context menu verbs"): the organic need above the floor (First Aid, 25) and the robot one (Repairs, 60); <see cref="NeedsAid"/> is either, <see cref="NeedsRobotAid"/> the robot one only when the organic is not.</summary>
    public bool NeedsOrganicAid { get; init; }
    public bool NeedsRepairs { get; init; }
    /// <summary>The health word's other states (docs/game/ui-screens.md "Health word choice"): being eaten (<see cref="Bodies.MedicalState.EatenCountdown"/>), the recovery coma, the forced crawl (posture 2) and a right arm that does not work.</summary>
    public bool BeingEaten { get; init; }
    public bool Coma { get; init; }
    public bool Crawling { get; init; }
    public bool RightArmDisabled { get; init; }
}
public sealed record HealthPartView(string Id, string Name, float Flesh, float Stun, float Bandage, float Rig, float Maximum, bool Robotic)
{
    /// <summary>The part's limb state (stump, replaced, crushed; <see cref="Bodies.LimbState.Original"/> for a part that is no limb): the medical row's Crushed and Robot layers.</summary>
    public Bodies.LimbState Limb { get; init; }
    /// <summary>The part's LOCATIONAL_DAMAGE <c>death</c> flag (head and torso) and its type (leg, arm...): the medical row's tooltip text (docs/game/ui-screens.md "Tooltips").</summary>
    public bool Vital { get; init; }
    public Meitou.Data.Gameplay.Bodies.BodyPartType Type { get; init; }
}
/// <summary>A line of the HUD's stats panel: the label, the value the character fights with and the value it would have unhurt (docs/game/ui-screens.md section 4).</summary>
public readonly record struct HudStatLine(string Label, float Value, float Base)
{
    /// <summary>The line's tooltip rows (key, value; either may start with a <c>#rrggbb</c> colour tag), empty for none (<see cref="HudTips"/>).</summary>
    public IReadOnlyList<(string Key, string Value)> Tip { get; init; } = [];
    /// <summary>A word the line shows in place of the number (an animal's age), null for a number.</summary>
    public string? Text { get; init; }
}
/// <summary>
/// The tooltip rows of the information panel's extended lines (<c>FUN_140895350</c>, <see cref="HudTips"/>), and what the run speed's rows need, which the host builds in its speed
/// unit: the stored athletics, the leg factor, the fitted legs' athletics factor, while sneaking the sneak speed cap in units per second, and the gear's run factor (CharStats <c>+0x18</c>).
/// </summary>
public sealed record ExtendedTips(IReadOnlyList<(string Key, string Value)> Athletics, IReadOnlyList<(string Key, string Value)> Strength,
    IReadOnlyList<(string Key, string Value)> Toughness, IReadOnlyList<(string Key, string Value)> Hunger, float StoredAthletics, float LegFactor, float RoboticLegs, float? SneakCap,
    float Gear = 1);
/// <summary>
/// What the HUD shows of a selected character besides <see cref="BodyStatus"/> (docs/game/ui-screens.md section 4): the blood in units and its capacity, the bleeding
/// rate, the encumbrance factor (1 unburdened) and the stats panel's lines.
/// </summary>
public sealed record HudView(float Blood, float BloodCapacity, float Bleeding, float Encumbrance, IReadOnlyList<HudStatLine> Stats, ExtendedStats? Extended = null)
{
    /// <summary>The race's <c>bleed rate</c> multiplier (the Blood row's tooltip).</summary>
    public float BleedRate { get; init; } = 1;
    /// <summary>The stomach store (<see cref="Bodies.MedicalState.Fed"/>); above 0 the Hunger row shows <c>&gt;</c>.</summary>
    public float Fed { get; init; }
    /// <summary>The race's <c>is robot</c>: the medical panel has no Hunger row.</summary>
    public bool Robot { get; init; }
    /// <summary>The extended lines' tooltips; null when <see cref="Extended"/> is.</summary>
    public ExtendedTips? ExtendedTips { get; init; }
    /// <summary>The state panel's "Current Skill" line (null: the line is empty).</summary>
    public CurrentSkillView? Skill { get; init; }
    /// <summary>The KO point of every shown character (<c>MedicalState.KoPoint</c>): <see cref="Extended"/> has it only for the player and the inspected one. Null without game constants.</summary>
    public float? KoPoint { get; init; }
    /// <summary>The stats window's "Stats Affected" numbers by stat (<see cref="HudTips.StatsWindow"/>); stats without numbers are absent.</summary>
    public IReadOnlyDictionary<Meitou.Data.Gameplay.Bodies.StatsEnumerated, IReadOnlyList<StatReadout>>? StatReadouts { get; init; }
}
/// <summary>A stats window row's English label (as the exe writes it, without the leading "-"), its value and whether the row is in Main (the label and value of "Knockout point:").</summary>
public readonly record struct StatReadout(string Label, string Value, bool Main = false);
/// <summary>
/// The state panel's "Current Skill" line (docs/game/ui-screens.md section 4): the skill the job uses, labelled by its stat name (<see cref="Label"/> null), or a fallback with its
/// own label (the drawn weapon's class, Athletics, Stealth, Swimming). <see cref="Value"/> is the level, or for Swimming the swim speed in units per second (<see cref="Speed"/>);
/// <see cref="Penalised"/> shows it Bad. The tooltip: <see cref="Terms"/> for the job skill, else the fallback's own breakdown.
/// </summary>
public readonly record struct CurrentSkillView(Meitou.Data.Gameplay.Bodies.StatsEnumerated Stat, string? Label, float Value, bool Penalised = false, bool Speed = false,
    SkillTerms? Terms = null)
{
    /// <summary>The trained level the value is compared with (Stealth: "{v} ({d})" Bad when lower, Good when higher); null shows the value alone.</summary>
    public float? Base { get; init; }
    /// <summary>A melee weapon class line's "Damage:" (its blow's terms summed).</summary>
    public float? Damage { get; init; }
    public AccuracyTerms? Accuracy { get; init; }
    public StealthTerms? Stealth { get; init; }
    public AthleticsTerms? Athletics { get; init; }
    public SwimTerms? Swim { get; init; }
}
/// <summary>The "Accuracy Error:" line's tooltip: the crossbow's deviation at 0 skill, the effective skill and the resulting deviation, in degrees.</summary>
public readonly record struct AccuracyTerms(float Weapon, float Skill, float Deviation);
/// <summary>
/// The Stealth line's tooltip (<c>FUN_14088f9a0</c>): the trained level, the sneaking top speed (units a second), the XP rate, carrying, crawling (+100%, which ends the list), then
/// the encumbrance penalty, the body term, the gear and the fitted legs as signed percentages, and the total (the stealth term x 100).
/// </summary>
public readonly record struct StealthTerms(float Skill, float MaxSpeed, float XpRate, bool Carrying, bool Crawling, int Encumbrance, int Injuries, int Gear, int Robotics, float Total);
/// <summary>
/// The Athletics line's tooltip (<c>FUN_14088e780</c>): the trained level, the run value (units a second), the leg injury and encumbrance penalties, the fitted legs as a signed
/// percentage, the gear's run factor (CharStats <c>+0x18</c>, shown when not 1) and the top speed.
/// </summary>
public readonly record struct AthleticsTerms(float Skill, float MaxSpeed, int LegInjury, int Encumbrance, float Gear, int Robotics, float TotalSpeed);
/// <summary>
/// The Swimming line's tooltip (the HUD update): the trained level, the base swim speed, the injuries (legs and arms), hunger, encumbrance and limb items' penalties, both arms
/// broken, and the current swim speed.
/// </summary>
public readonly record struct SwimTerms(float Skill, float MaxSpeed, int Injuries, int Hunger, int Encumbrance, int Robotics, bool BrokenArms, float Speed);
/// <summary>
/// The breakdown of a skill's tooltip (<c>FUN_140890b00</c>, docs/game/ui-screens.md section 4): the trained level, then each penalty of the stat multiplier taken alone as
/// (int)((1 - factor) x 100) percent (Robotics below 0 is a bonus), the missing arm penalty (strength only, 0 or 33: (int) of (1 - 0.66) x 100), the effective level and, for
/// perception, the armour's <c>perception bonus</c> (CharStats <c>+0x30</c>).
/// </summary>
public readonly record struct SkillTerms(float Base, int Injuries, int Hunger, int Weather, int Darkness, int Robotics, bool HasRobotics, int Gear, int MissingArm, float Total,
    int ArmourBonus = 0)
{
    /// <summary>The line is Bad when the six terms sum above 2 (the missing arm penalty is not counted).</summary>
    public bool Penalised => Injuries + Hunger + Weather + Darkness + Robotics + Gear > 2;
}
/// <summary>
/// The information panel's extended lines (docs/game/ui-screens.md section 4): the XP rates as the fraction the panel shows as a percentage (Toughness as a plain multiplier),
/// the KO point (negative HP), the run speed in units per second with the unhurt run value it is measured against, and the hunger rate as a multiple of the race's.
/// <see cref="Resting"/>: in a bed or knocked out, which the hunger rate takes the bed rate for. <see cref="Fighting"/>: in combat mode, where Strength XP is the weapon weight factor
/// of the drawn weapon, or with nothing drawn (<see cref="Fists"/>) 1 - encumbrance, instead of the carrying rate.
/// </summary>
public readonly record struct ExtendedStats(float AthleticsXp, float StrengthXp, float ToughnessXp, float KoPoint, float RunSpeed, float RunValue, float HungerRate, bool Resting = false,
    bool Fighting = false, bool Fists = false, float? WorkHungerRate = null);
public sealed record InventoryItemView(int Index, string Record, string Name, string Section, int X, int Y, int Width, int Height, int Quantity, float Weight, float Quality)
{
    /// <summary>The item's material record (<c>material sid</c>; "" none), which picks its icon.</summary>
    public string Material { get; init; } = "";
    /// <summary>The COLOR_DATA it is dyed with (<see cref="Items.ItemInstance.ColourId"/>; "" none), which also picks its icon.</summary>
    public string Colour { get; init; } = "";
    /// <summary>How full the item is (0 to 1, <see cref="Items.ItemInstance.ChargeFraction"/>); null: no charge bar.</summary>
    public float? Charge { get; init; }
    /// <summary>The item's manufacturer record (<c>company sid</c>; "" none), its model level and its charges (what the tooltip shows).</summary>
    public string Company { get; init; } = "";
    /// <summary>The smith who crafted the item (<see cref="Items.ItemInstance.Crafter"/>; "" none).</summary>
    public string Crafter { get; init; } = "";
    public int Level { get; init; }
    public float Charges { get; init; }
    public float MaxCharges { get; init; }
    /// <summary>The faction the item counts as stolen from for its holder (<see cref="Items.ItemInstance.IsStolenFor"/>), -1 when it is not stolen.</summary>
    public int StolenFrom { get; init; } = -1;
    /// <summary>The item's <c>unique</c> flag (<see cref="Items.ItemInstance.Unique"/>): the tooltip's [Unique] row.</summary>
    public bool Unique { get; init; }
}
public sealed record InventoryView(int Width, int Height, int BagWidth, int BagHeight, IReadOnlyList<InventoryItemView> Items)
{
    public float? CarryCapacity { get; init; }
    public float TotalWeight { get; init; }
    /// <summary>The holder's encumbrance factor (<see cref="Bodies.Encumbrance.Factor"/>, 1 unburdened): the window's burden word, for a looted body too (which has no HUD view).</summary>
    public float Burden { get; init; } = 1;
}

/// <summary>A character as the renderer needs it after one tick; <c>Appearance</c> is null for one that is simulated but has nothing to draw (a failed build, a world without looks): the host shows a marker (docs/simulation.md, "Threading", step 8).</summary>
public sealed record CharacterSnapshot(CharacterId Id, CharacterAppearance? Appearance, Vector3 Position, float Yaw, IReadOnlyList<AnimationLayer> Animations)
{
    /// <summary>The facing the body is drawn at (<see cref="CharacterAnimation.DrawYaw"/>: it follows <see cref="Yaw"/> at a limited rate); NaN when the character has none yet (draw <see cref="Yaw"/>).</summary>
    public float DrawYaw { get; init; } = float.NaN;
    /// <summary>The owner faction (a position in the world's faction list; -1 none), for debug markers.</summary>
    public int Faction { get; init; } = -1;
    /// <summary>A weapon is in hand (<c>CharacterCold.DrawnWeapon</c>): the renderer shows the drawn weapon meshes instead of the sheathed one.</summary>
    public bool WeaponDrawn { get; init; }
    /// <summary>The weapon in hand is the crossbow (ranged combat): the renderer shows the crossbow in the hands and the melee weapon sheathed.</summary>
    public bool RangedDrawn { get; init; }
    /// <summary>The melee weapon in hand is the back one (<see cref="Combat.Fighter.WeaponIsBack"/>): its drawn meshes show, the hip one's sheathed.</summary>
    public bool BackDrawn { get; init; }
    /// <summary>A corpse with flies (<see cref="CharacterCold.HasFlies"/>): the host shows the Fly-Particle effect and plays the Corpse Flies ambient at it.</summary>
    public bool HasFlies { get; init; }
    /// <summary>The bolts loaded in its crossbow (<see cref="CharacterCold.CrossbowShots"/>).</summary>
    public int CrossbowShots { get; init; }
    /// <summary>The bolts stuck in the character (<see cref="CharacterCold.StuckBolts"/>).</summary>
    public IReadOnlyList<StuckBolt> StuckBolts { get; init; } = [];
    public string Name { get; init; } = "";
    public int SquadId { get; init; } = -1;
    /// <summary>One of the player's characters, and whether it is selected.</summary>
    public bool IsPlayer { get; init; }
    /// <summary>A member of a squad with a shop (right click trades).</summary>
    public bool IsTrader { get; init; }
    public bool Selected { get; init; }
    /// <summary>Another faction's character the HUD shows (<see cref="InspectCommand"/>): it carries the same HUD details as a selected one but is not selected.</summary>
    public bool Inspected { get; init; }
    /// <summary>The orders panel's toggles and the move speed mode (<see cref="CharacterCold.Orders"/>, <see cref="CharacterCold.RunSpeed"/>).</summary>
    public CharacterOrders Orders { get; init; }
    public byte RunSpeed { get; init; } = SpeedMode.Free;
    /// <summary>The job list (<see cref="CharacterCold.Jobs"/>), first = highest priority.</summary>
    public IReadOnlyList<Production.PermaJob> Jobs { get; init; } = [];
    /// <summary>The path being followed from the next waypoint, for selected characters only (the host draws it).</summary>
    public IReadOnlyList<Vector3> Path { get; init; } = [];
    /// <summary>Null for a character without a body (an animal).</summary>
    public BodyStatus? Body { get; init; }
    /// <summary>The stat-driven body of a person (not a robot, not an animal), from its current stats and hunger; null for the others.</summary>
    public Meitou.Data.Characters.BodyBuild? Build { get; init; }
    /// <summary>What a selected character carries, one line per item (empty for the others).</summary>
    public IReadOnlyList<string> Inventory { get; init; } = [];
    /// <summary>A selected character's bounties, one line per faction that wants it (empty for the others and for one nobody wants).</summary>
    public IReadOnlyList<Crime.BountyLine> Bounties { get; init; } = [];
    /// <summary>Slave (state 1) or escaped slave (state 2) and its owner's name, for the status lines of the bounty panel's tooltip (docs/game/ui-screens.md section 4); null otherwise.</summary>
    public SlaveView? Slave { get; init; }
    /// <summary>The disguise of one of the player's characters while it has a level or a blown cooldown (<see cref="Crime.ArrestSystem"/>); null otherwise.</summary>
    public DisguiseView? Disguise { get; init; }
    /// <summary>The status line ahead of the bounty (docs/game/ui-screens.md section 4, <c>FUN_1405d4260</c>): campaign leader, kidnapped, committing a crime, escaped prisoner; null for none.</summary>
    public StatusView? Status { get; init; }
    /// <summary>A selected character's trained stats (the host formats them); null for the others and for a character without stats.</summary>
    public SkillSummary? Skills { get; init; }
    public IReadOnlyList<float> AllSkills { get; init; } = [];
    public IReadOnlyList<float> SkillXpMultipliers { get; init; } = [];
    public IReadOnlyList<HealthPartView> HealthParts { get; init; } = [];
    /// <summary>The limbs' states (<c>MedicalState.LimbBits</c>: limb slot k's <c>LimbState</c> in bits 2k..2k+1; 0 when every limb is the original).</summary>
    public int LimbBits { get; init; }
    /// <summary>The blood each of the first 8 body parts shows (<c>FUN_140530b30</c>: 1 - clamp((flesh - stun + bandage) / max, 0, 1), byte k = part k as 0 to 255; 0: unhurt).</summary>
    public ulong BloodAmounts { get; init; }
    /// <summary>The speed (world units/s) the blow that last put it down gave its ragdoll (<c>CharacterCold.Knockdown</c>; Meitou); a cut-off head is thrown with it.</summary>
    public Vector3 Knockdown { get; init; }
    /// <summary>The equipment sections disabled for the character (<see cref="Items.InventoryCommands.SectionClosed"/>; empty for most).</summary>
    public IReadOnlyList<string> ClosedSlots { get; init; } = [];
    /// <summary>The HUD's numbers for a selected character (null for the others).</summary>
    public HudView? Hud { get; init; }
    public InventoryView? ItemGrid { get; init; }
    /// <summary>
    /// The character carrying this one (none when not carried). A carried body has its carrier's position and facing; it is drawn at <see cref="Carry"/> from them,
    /// with the <c>carry me</c> pose.
    /// </summary>
    public CharacterId CarriedBy { get; init; } = CharacterId.None;
    /// <summary>Where a carried body hangs, in its carrier's frame (on the carrier's <c>Bip01 L UpperArm</c>); null when not carried.</summary>
    public Carrying.CarryAttach? Carry { get; init; }
    /// <summary>The ITEM (string id) held in the <c>hands</c> point while operating: the functionality's <c>special tool</c> (docs/game/buildings-production.md "Production"); null for none.</summary>
    public string? HeldTool { get; init; }
    /// <summary>The size the body is drawn at: an animal's lerp(age, <c>scale min</c>, <c>scale max</c>) (its <c>MedicalState.ScaleK</c>), 1 for people.</summary>
    public float Scale { get; init; } = 1;
    /// <summary>The movement task (<see cref="CharacterTask"/>); the host's sounds follow building, treating and looting from it.</summary>
    public CharacterTask Task { get; init; }
    /// <summary>Working on a cage's lock (the picker's order is given and it is in reach or walking there).</summary>
    public bool PickingLock { get; init; }
    /// <summary>Picking or cutting shackles (a <see cref="Locks.ShackleJob"/> of that work, in reach or walking there): the <c>Character_Unlock</c> loop. Breaking and putting on have none.</summary>
    public bool WorkingShackles { get; init; }
    /// <summary>Lying in a bed (<see cref="Locks.HeldState.Bed"/>).</summary>
    public bool InBed { get; init; }
    /// <summary>Held in a cage or restraint (<see cref="CharacterCold.InSomething"/>).</summary>
    public bool Imprisoned { get; init; }
    /// <summary>Imprisoned in a cage whose lock is open (unlocked, broken or level 0): the escape tutorial's success.</summary>
    public bool CageOpen { get; init; }
    /// <summary>Crawling by choice (<see cref="Bodies.MedicalState.StayingLow"/>, not crippled): the context menu's Stand Up.</summary>
    public bool StayingLow { get; init; }
    /// <summary>In a fight (<see cref="CharacterCold.InCombat"/>).</summary>
    public bool InCombat { get; init; }
    /// <summary>In combat mode (<c>Character::isInCombatMode(1, 1)</c>, <see cref="Combat.CombatSystem.IsInCombatMode"/>): the portrait's <c>Combat</c> overlay.</summary>
    public bool CombatMode { get; init; }
    /// <summary>An NPC whose fight's target is one of the player's characters, whatever its faction thinks of the player's (a robbery a dialogue line started, a retaliation).</summary>
    public bool FightsPlayer { get; init; }
    /// <summary>Looting its body is no theft in the pointer's eyes (<c>search</c> instead of <c>scavenge</c>): it is a bandit or a slave (NPC class 8 / 6), or its faction is not real (<c>FUN_1407fb410</c>, docs/game/ui-input.md "Quick right click").</summary>
    public bool NoLootGuilt { get; init; }
    /// <summary>Its squad is a campaign force of its faction (<c>FactionWarMgr</c> has its platoon, <c>FUN_140796240</c>): a raiding or war party member the quick right click can still talk to.</summary>
    public bool WarParty { get; init; }
    /// <summary>Wears locked armour (shackles, <see cref="Items.ItemInstance.IsLocked"/>): the portrait's <c>Slave</c> overlay.</summary>
    public bool Shackled { get; init; }
    /// <summary>Its worn shackles are locked (<see cref="Locks.LockSystem.WornShackles"/>): the context menu offers Unlock Shackles, Use Tools and Use Strength.</summary>
    public bool ShacklesLocked { get; init; }
    /// <summary>Alive, down or caged and wearing no shackles (<see cref="Locks.LockSystem.CanBeShackled"/>): the context menu offers Put on Shackles to a selection that carries some.</summary>
    public bool CanBeShackled { get; init; }
    /// <summary>A selected character carrying unworn shackles (<see cref="Locks.LockSystem.CarriedShackles"/>).</summary>
    public bool CarriesShackles { get; init; }
    /// <summary>The number that turns its portrait camera (<see cref="CharacterCold.PortraitSerial"/>).</summary>
    public uint PortraitSerial { get; init; }
    /// <summary>The character this one carries (none when not carrying).</summary>
    public CharacterId Carrying { get; init; } = CharacterId.None;
    /// <summary>The goal the AI is acting out, in the game's words (<c>Patrolling town</c>, <c>Shopkeeper</c>); null when the AI does not run the character now.</summary>
    public string? Activity { get; init; }
    /// <summary>A player character's last decision (<see cref="DecisionSystem.Tracing"/>: which source won and why each one before it could not run); null while tracing is off. Debug only.</summary>
    public DecisionTrace? Decision { get; init; }
    /// <summary>The weather effect that hurt this player character last tick and the protection it shows (the status notice, docs/formats/weather.md); None otherwise. Display only.</summary>
    public WeatherNotice WeatherNotice { get; init; }
    public float WeatherNoticeProtection { get; init; }
    /// <summary>
    /// A sneaking player character's state for its stealth tag (docs/game/perception.md "Spotting a sneaker"): <see cref="StealthExposure.Detected"/> seen,
    /// <see cref="StealthExposure.Noticed"/> almost seen, anything lower hidden; null when not a sneaking player character.
    /// </summary>
    public StealthExposure? Stealth { get; init; }
    /// <summary>A sneaking player character's light level (<see cref="SightMap.Light"/>) for its tag's light icon; 1 otherwise.</summary>
    public float Light { get; init; } = 1;
    /// <summary>The floating progress bar over a player character picking a lock (docs/game/locks.md "Lockpicking"); null when none.</summary>
    public TaskBar? Bar { get; init; }
    /// <summary>
    /// The chance in per cent that the selection's first sneaking member knocks this character out from behind (<see cref="Carrying.CarrySystem.StealthKoChance"/>); null when no
    /// selected character is sneaking or this one cannot be knocked out (a player character, or already down).
    /// </summary>
    public float? StealthKoChance { get; init; }
    /// <summary>The chance in per cent that the selection's lead kidnaps this NPC (<see cref="Carrying.CarrySystem.KidnapChance"/>); null for a player character or with nothing selected.</summary>
    public float? KidnapChance { get; init; }
    /// <summary>A player character's effective lockpicking (<see cref="Locks.Lockpicking.Skill"/>): the Pick Lock rows' chance column is <see cref="Locks.Lockpicking.Chance"/> of the lock's level and the lead's skill.</summary>
    public float LockpickSkill { get; init; }
    /// <summary>Asleep (<see cref="Carrying.CarrySystem.Asleep"/>): not standing, so the menu offers the down verbs (Kidnap among them).</summary>
    public bool Asleep { get; init; }
    /// <summary>Down as the original counts it (<see cref="CharacterFacts.Down"/>, <c>FUN_1405c9e10</c>): out cold, dead, playing dead or carried. The menu's down verbs read it.</summary>
    public bool Down { get; init; }
    /// <summary>Conscious as Set Free's precondition 125 reads it (<see cref="CharacterFacts.Conscious"/>): not dead, out cold or carried.</summary>
    public bool Conscious { get; init; }
    /// <summary>Awake: not dead or out cold (<see cref="CharacterFacts.Standing"/>), carried or not; the menu's attack and trade targets.</summary>
    public bool Standing { get; init; }
    /// <summary>An animal, which plans with the animal repertoire (<see cref="CharacterFacts.Animal"/>): the menu offers it only the verbs animals have.</summary>
    public bool Animal { get; init; }
    /// <summary>Hostile to the player: its faction is hostile to the player's, or it fights one of the player's characters (<see cref="CharacterFacts.HostileToPlayer"/>); the attack pointer and the menu's attack verbs.</summary>
    public bool HostileToPlayer { get; init; }
    /// <summary>An <see cref="Carrying.OpenLootOrder"/> may open its inventory (<see cref="Carrying.CarrySystem.Openable"/>: down, playing dead or carried, the player's own, a sleeper or a prisoner as a pickpocket): the menu's Loot and Trade.</summary>
    public bool CanOpenInventory { get; init; }
    /// <summary>In a cage: whether it is locked and the sentence (null: not imprisoned).</summary>
    public PrisonView? Prison { get; init; }
    /// <summary>The town turret this character stands at the gun of (the AI's turret goals, arrived and idle); null when it mans none. Read-only, for the host's turret sounds.</summary>
    public MannedTurret? Turret { get; init; }
    /// <summary>A selected or inspected character's ranged controller State line (<see cref="Combat.CombatSystem.RangedLineOf"/>); null otherwise.</summary>
    public Combat.RangedLineView? RangedLine { get; init; }
    /// <summary>A selected or inspected character's bed recovery rate above 1 (<see cref="BodySystem.BedRecoveryRate"/>); null otherwise.</summary>
    public float? BedRecoveryRate { get; init; }
    /// <summary>Another faction's character, down or asleep, in one of the player's buildings (<see cref="Production.ProductionSystem.InPlayerBuilding"/>): the menu's Throw out of home.</summary>
    public bool InPlayerBuilding { get; init; }
    /// <summary>The hostile NPCs that noticed or detected this sneaking player character in the last 4 s.</summary>
    public IReadOnlyList<WatcherView> Watchers { get; init; } = [];
    /// <summary>
    /// The bones the character's ragdoll sets (<see cref="Ragdolls.RagdollSystem"/>), in the model space of its drawn placement (position, minus the shape's ground offset, a
    /// carried body's <see cref="Carry"/>, facing, size: <see cref="CharacterRigs.Placement"/>), to pose over the animation; null without a ragdoll. Immutable.
    /// </summary>
    public BonePose? Ragdoll { get; init; }
}

/// <summary>
/// A prisoner's cage and sentence (docs/game/economy.md 11.5): <see cref="Locked"/> the cage is effectively locked; <see cref="Sentence"/> its length in game hours (0: none, so it waits
/// to be released), <see cref="HoursLeft"/> what remains of it.
/// </summary>
/// <summary>A floating progress bar's caption and fill (0..1).</summary>
public readonly record struct TaskBar(string Text, float Progress);

public readonly record struct PrisonView(bool Locked, float Sentence, double HoursLeft)
{
    /// <summary>The faction owning the cage it is in (-1 none) and the cage's fixture id.</summary>
    public int CageOwner { get; init; }
    public string? CageId { get; init; }
    /// <summary>The cage lock's level, -1 when the character is in no cage with a lock (the State line's "Locked (lvl: N)").</summary>
    public int LockLevel { get; init; } = -1;
}

/// <summary>A slave's state (1 slave, 2 escaped slave; <c>SlaveStateEnum</c>) and the name of the faction that owns it ("someone" is the caller's word for none).</summary>
/// <summary>The slave state of a character (1 slave, 2 escaped), its owner faction's name and its "looks like a slave" level in 0..1 (<see cref="Kidnap.CaptiveSystem.LooksLikeSlaveLevel"/>).</summary>
public readonly record struct SlaveView(int State, string Owner, float Looks = 0);

/// <summary>
/// A disguise for the data panel and the health word (docs/game/ui-screens.md "Which lines"): the clothing level, the uniform faction's name, the blown cooldown (seconds), whether it
/// works, and the tooltip's factors (secure location, the looker marks).
/// </summary>
public readonly record struct DisguiseView(float Level, string Faction, float Blown, bool Works, bool Secure, bool SameFaction, bool SlaveOwner, bool KnowsMe);

/// <summary>
/// A turret building being manned (<see cref="CharacterSnapshot.Turret"/>): its key (a <see cref="Meitou.Data.World.TownBuilding"/> key, or a player site's id) and BUILDING record, where it stands and
/// faces (0 along +Z, turning towards +X) and its GUN_DATA record. <see cref="Simulated"/>: the combat system runs it, and <see cref="Aim"/> is the point its gun aims at (null: none yet).
/// </summary>
public sealed record MannedTurret(string Key, string BuildingId, Vector3 Position, float Yaw, string GunId)
{
    public bool Simulated { get; init; }
    public Vector3? Aim { get; init; }
    /// <summary>The turning of its parts so far, radians, in <c>TurretGun.Parts</c> order (a simulated turret only); the renderer turns the meshes by it.</summary>
    public float[]? Angles { get; init; }
}

/// <summary>The trained stats the HUD shows for the selection.</summary>
public readonly record struct SkillSummary(float Attack, float Defence, float Dodge, float Toughness, float Strength, float Athletics);

/// <summary>
/// What a tick publishes for drawing. Immutable once published: the host keeps the last two and interpolates between them by the
/// frame's alpha, as it does for the camera; a character only in the newer one appears without interpolation.
/// </summary>
public sealed record WorldSnapshot(long Tick, IReadOnlyList<CharacterSnapshot> Characters)
{
    /// <summary>What the tutorial triggers ask of the player's characters (<see cref="Simulation.TutorialFacts"/>).</summary>
    public TutorialFacts TutorialFacts { get; init; }
    /// <summary>Crossbow bolts in flight and stuck in the ground (the renderer may draw them; empty when there are none).</summary>
    public IReadOnlyList<ProjectileSnapshot> Projectiles { get; init; } = [];
    /// <summary>The open trade (null: none).</summary>
    public Trading.TradeView? Trade { get; init; }
    /// <summary>The open player conversation (null: none).</summary>
    public Dialogue.ConversationView? Conversation { get; init; }
    /// <summary>The open character trading window (slaves bought or sold; null: none).</summary>
    public Dialogue.CharacterTradeView? CharacterTrade { get; init; }
    /// <summary>The lines on show over characters' heads: speech bubbles (empty when nobody speaks).</summary>
    public IReadOnlyList<Dialogue.SpeechBubble> Speech { get; init; } = [];
    /// <summary>The last thing a conversation asked the host to do (the host acts on each <see cref="Dialogue.DialogueRequest.Id"/> once).</summary>
    public Dialogue.DialogueRequest? DialogueRequest { get; init; }
    /// <summary>What the player knows of each placed town (<see cref="PlayerState.Towns"/>).</summary>
    public IReadOnlyList<TownKnowledge> Towns { get; init; } = [];
    /// <summary>The camps and nests the player knows (<see cref="PlayerState.Camps"/>).</summary>
    public IReadOnlyList<CampView> Camps { get; init; } = [];
    /// <summary>The factions the player has met (<see cref="PlayerState.KnownFactions"/>).</summary>
    public IReadOnlySet<int> KnownFactions { get; init; } = NoFactions;
    /// <summary>The squad AI options (<see cref="PlayerState.SquadAi"/>).</summary>
    public SquadAi SquadAi { get; init; } = SquadAi.Defaults;
    /// <summary>The player's squads in order (<see cref="SquadManagement.PlayerSquads"/>), each with its members in squad order (the first leads).</summary>
    public IReadOnlyList<SquadView> PlayerSquads { get; init; } = [];
    /// <summary>The active squad's id (<see cref="PlayerState.Squad"/>; -1 none): the squad bar shows it.</summary>
    public int ActiveSquad { get; init; } = -1;
    /// <summary>The downed (or the player's own) character whose inventory window is open (<see cref="PlayerState.OpenLoot"/>; its snapshot has the <see cref="CharacterSnapshot.ItemGrid"/>), and who opened it.</summary>
    public CharacterId OpenLoot { get; init; } = CharacterId.None;
    public CharacterId LootOpener { get; init; } = CharacterId.None;
    /// <summary>The item on the mouse (<see cref="PlayerState.Held"/>), null for none, and the serial of the last held-item command applied (<see cref="PlayerState.HeldSerial"/>).</summary>
    public Items.HeldItemView? Held { get; init; }
    public long HeldSerial { get; init; }
    /// <summary>Player-built buildings and construction sites, in creation order (docs/simulation.md "Buildings").</summary>
    public IReadOnlyList<Construction.BuildingSnapshot> Buildings { get; init; } = [];
    /// <summary>Bumped when a building is added, completed, destroyed or removed (not on progress): the host diffs <see cref="Buildings"/> by id when it changes.</summary>
    public long BuildingsRevision { get; init; }
    /// <summary>The player outposts with their power totals (the town panel, docs/game/ui-screens.md "Town panel").</summary>
    public IReadOnlyList<Production.OutpostSnapshot> Outposts { get; init; } = [];
    /// <summary>Loose items lying in the world (<see cref="Items.GroundItems"/>), oldest first.</summary>
    public IReadOnlyList<Items.GroundItemSnapshot> GroundItems { get; init; } = [];
    /// <summary>The containers with an inventory (a building interior's chests and boxes), where they stand.</summary>
    public IReadOnlyList<Locks.ContainerSpot> Containers { get; init; } = [];
    /// <summary>Every bed fixture, for picking (<see cref="Locks.FixtureRegistry.BedSpots"/>).</summary>
    public IReadOnlyList<Locks.BedSpot> Beds { get; init; } = [];
    /// <summary>Every cage fixture, for picking (<see cref="Locks.FixtureRegistry.CageSpots"/>); its occupant is as of this tick.</summary>
    public IReadOnlyList<Locks.CageSpot> Cages { get; init; } = [];
    /// <summary>Every door, where its leaf is, for the right click (<see cref="Locks.DoorOrder"/>); its state is in <see cref="Doors"/>.</summary>
    public IReadOnlyList<Locks.DoorPick> DoorPicks { get; init; } = [];
    /// <summary>Bumped when an item is dropped or goes: the host diffs <see cref="GroundItems"/> by id when it changes.</summary>
    public long GroundItemsRevision { get; init; }
    /// <summary>BUILDING ids the player's research has unlocked (the build menu's list).</summary>
    public IReadOnlySet<string> UnlockedBuildings { get; init; } = EmptySet;
    /// <summary>The doors of the player's buildings and the others that are open, moving, locked or set to lock, by (building instance, index in its <c>doors</c> list); any other door is closed and unlocked (docs/simulation.md "Cages, beds and locks").</summary>
    public IReadOnlyDictionary<(string Building, int Door), Locks.DoorView> Doors { get; init; } = NoDoors;

    /// <summary>The town houses the player owns (bought ones too, with or without doors); indoor placement checks them.</summary>
    public IReadOnlySet<string> OwnedTownBuildings { get; init; } = EmptySet;
    /// <summary>The doors of the building the HUD shows (<see cref="InspectBuildingCommand"/>); null when none is shown or it has no fixtures.</summary>
    public Locks.BuildingDetail? InspectedBuilding { get; init; }
    /// <summary>The resource richness of the building the HUD shows (a mine or rock); null for others.</summary>
    public Production.RichnessView? InspectedRichness { get; init; }
    /// <summary>The gun numbers of the building the HUD shows when it is a turret (its Stats 1 gun lines); null for others.</summary>
    public Combat.TurretPanelView? InspectedTurret { get; init; }
    /// <summary>The machines of the player's complete buildings (docs/simulation.md "Production"), in the order they were made.</summary>
    public IReadOnlyList<Production.MachineSnapshot> Machines { get; init; } = [];
    /// <summary>Bumped on every machine change (items, progress, status).</summary>
    public long MachinesVersion { get; init; }
    /// <summary>The player's research: levels, queue, rate and what may be queued.</summary>
    public Production.ResearchSnapshot Research { get; init; } = Production.ResearchSnapshot.Empty;
    /// <summary>What each of the player's crafting benches can craft (by site), for the Craft tab.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Production.CraftOptionView>> Crafts { get; init; } = new Dictionary<string, IReadOnlyList<Production.CraftOptionView>>();
    /// <summary>Each crafting bench's current smith (by site), for the Craft tab's queue description; a bench nobody has worked has none.</summary>
    public IReadOnlyDictionary<string, Production.CraftSmithView> Smiths { get; init; } = new Dictionary<string, Production.CraftSmithView>();

    static readonly IReadOnlyDictionary<(string Building, int Door), Locks.DoorView> NoDoors = new Dictionary<(string Building, int Door), Locks.DoorView>();

    static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);
    static readonly IReadOnlySet<int> NoFactions = new HashSet<int>();

    // After the statics above: static initializers run in textual order, and the property defaults read them.
    public static readonly WorldSnapshot Empty = new(-1, []);
}

/// <summary>
/// A bolt as the renderer needs it: the nose position, the unit direction of flight, the stretch of its mesh along its length (max(1, speed x 0.005) in flight, 1 once
/// stuck; <c>FUN_1404380e0</c>), whether it is stuck in the ground, and the <c>live ammo</c> mesh of the crossbow that shot it.
/// </summary>
public readonly record struct ProjectileSnapshot(int Id, Vector3 Position, Vector3 Direction, float Stretch, bool Stuck, string Mesh)
{
    /// <summary>Who fired it (the renderer starts the drawn bolt at that character's crossbow).</summary>
    public CharacterId Shooter { get; init; }
    /// <summary>Its speed in units a second (0 once stuck; the renderer's tracers are as long as a moment of it).</summary>
    public float Speed { get; init; }
}

/// <summary>
/// A bolt stuck in a character (the original sticks every bolt that hits one, <c>FUN_1406508d0</c>, <b>Observed</b>): its <c>live ammo</c> mesh, the bone of the struck part
/// (<c>bone name</c>, or <c>bone name 2</c> by a coin toss when there is one), and its flight direction in the character's frame (its yaw taken off) at the hit.
/// </summary>
public readonly record struct StuckBolt(int Id, string Mesh, string Bone, Vector3 Direction);

/// <summary>A system that has projectiles to publish (the combat system).</summary>
public interface IProjectileSource
{
    /// <summary>The projectiles after the tick (a new list; empty when none).</summary>
    IReadOnlyList<ProjectileSnapshot> PublishProjectiles();
}

/// <summary>One of the player's squads as the squad tabs and the squads screen show it.</summary>
public sealed record SquadView(int Id, string Name, IReadOnlyList<CharacterId> Members);

/// <summary>Which status line a character's bounty panel shows (<c>FUN_1405d4260</c>, <b>Observed</b>).</summary>
public enum StatusKind : byte { Leader, WarLeader, Kidnapped, Crime, EscapedPrisoner, Diplomatic }

/// <summary>A status line: for <see cref="StatusKind.Crime"/> the crime's name, its pending timer and the local faction's bounty.</summary>
public readonly record struct StatusView(StatusKind Kind, string Crime = "", int Timer = 0, int Bounty = 0);
