using System.Reflection;
using HarmonyLib;

namespace ProvingGrounds.Engine;

/// <summary>The part of the mod a by-name member belongs to: the part that turns off without it.</summary>
enum Part
{
    /// <summary>The <c>pg</c> console command (src/Engine/PgConsole.cs).</summary>
    Console,
    /// <summary>Save safety (src/Game/WorldLifecycle.cs).</summary>
    Save,
    /// <summary>The combat hooks (src/Game/CombatHooks.cs, task 3.2b): each patch turns off alone when its own members are missing.</summary>
    CombatHooks,
    /// <summary>The medical protocol's treatment block (src/Game/ArenaMedical.cs, task 4.7).</summary>
    Medical,
    /// <summary>The test-only <c>pg</c> verbs (refused until <c>pg spikes on</c>; task 4.1: place / findspot / finish-build): checked like any
    /// other part; a missing member turns only that verb off.</summary>
    TestVerbs,
    /// <summary>The window framework (task 4.4, src/Ui/ScreenHost.cs): the screen host, its bindings and the Ctrl modifier; without them no Proving
    /// Grounds window opens (the console and the simulation run on).</summary>
    Windows,
    /// <summary>Recognising a right press the game takes before the windows (task 4.5b review, src/Ui/ScreenHost.cs): optional; without these
    /// members such a press reaches our windows as before (one log line), the windows stay on.</summary>
    WindowsPressTaken,
    /// <summary>Counting the game loop's passes for the windows' held buttons (task 4.5b review, src/Ui/ScreenHost.cs): optional; without it a release
    /// Meitou does not pass on is not noticed (one log line), the windows stay on.</summary>
    WindowsLoopPass,
    /// <summary>Following the pointer's moves for the window buttons' double-click timer (task 4.4b, src/Ui/ScreenHost.cs): optional; without it the
    /// widget under the pointer is still read at each interface arrange (one log line), the windows stay on.</summary>
    WindowsPointerMove,
    /// <summary>Character portraits in the windows (task 4.4, src/Ui/Portraits.cs, spike S7): without them the windows show names only.</summary>
    Portraits,
    /// <summary>The right click on a Registry / Banner / Leaderboard and the order hook (task 4.2, src/Game/ContextMenuHooks.cs, spike S3): without
    /// them the buildings answer as without the mod (one log line); the console openers still work.</summary>
    Interact,
    /// <summary>The "use" pointer over those buildings (task 4.2): optional; without it the pointer stays the game's (one log line).</summary>
    InteractCursor,
    // ---- task 4.6b ----
    /// <summary>The Marks HUD's label (task 4.6b, src/Ui/MarksHUD.cs): without the HUD's arranged controls the label is off (one log line).</summary>
    MarksHud,
    /// <summary>The debug menu's key (task 4.6b, src/Ui/DebugMenu.cs, CAP 7.10): without these the key does nothing (one log line); the window and
    /// the test verb <c>pg debug</c> stay.</summary>
    DebugMenuKey,
    // ---- end task 4.6b ----
    /// <summary>Reward items on a fighter (task 5.2a, src/Engine/Items.cs, CAP 10): rebuilding the fighter after its kit changed. Without it every
    /// reward delivery is refused (one log line); the catalogue window and the rest of the mod stay on.</summary>
    Rewards,
    /// <summary>Null's money trade opens the Rewards window (task 5.3a, src/Game/ContextMenuHooks.cs, spike S4): without these members Null's
    /// trade opens the game's trade window as without the mod (one log line); the console opener stays.</summary>
    RewardsTrade,
    /// <summary>The Rewards window's per-widget mouse wheel (task 5.3b, src/Ui/RewardsUI.cs: OnWheel / BindWheel :181-206, three body lines a notch):
    /// optional; without it the screen host's own wheel scrolls the canvases (50 px a notch, one log line), the window stays on.</summary>
    WindowsWheel,
    /// <summary>The Rewards window's item icons (task 5.3b, src/Ui/RewardsUI.cs: the inventory cells' icon source, CAP 7.3): optional; without
    /// it the cards show no icon, as the C++ does when no texture is made (one log line), the window stays on.</summary>
    RewardsIcons,
    /// <summary>The prisoner protection's <c>endCombatMode()</c> (task 6.2, PgEngine.EndCombat: CombatSystem.Disengage, internal): without it the
    /// protection only ends a fight task (PgEngine.EndAttack, the removeJob half) and never takes a character out of combat otherwise (one log line).</summary>
    PrisonerCombat,
    /// <summary>Freedom contracts' recruit (task 6.4a, src/Engine/Recruiting.cs: DialogueSystem.JoinPlayer and PlayerHeads, internal; CAP 15): without
    /// them a prisoner cannot join the squad (one log line): where the C++ rules would enable the recruitment window's button, it reads the port's
    /// "Recruitment Unavailable" instead (every other state, a Marks deficit or a busy arena, is shown as the C++ shows it), and a recruit is refused
    /// before anything is reserved; the window, the roster and the Marks stay.</summary>
    Recruitment,
    /// <summary>The town fighters' combat stats (task 7.2, src/Game/ArenaCombatProfile.cs: the attack and defence ratings Meitou computes in
    /// internal CombatSystem statics, the original's <c>getMeleeAttack_melee</c> / <c>getMeleeDefence_melee(false)</c> /
    /// <c>getMeleeAttack_unarmed(true)</c>): without them every profile reads as unusable (ability -1, one log line), so the town finds no
    /// eligible opponent ("invalid ability stats") and no player passes ("combat stats unavailable"); the rest of the mod stays on.</summary>
    CombatProfile,
    /// <summary>A character's status town for a character Meitou has not updated yet (task 7.2 review, src/Engine/Towns.cs CurrentTownOf: internal
    /// AiSystem.StatusTownAt): optional; without it such a character's town is read by the radius rule (AiSystem.TownAt's, one log line), every
    /// other read of CharacterCold.StatusTown stays.</summary>
    StatusTown,
    /// <summary>The world's hold registry (task 8.4a, src/Engine/Holds.cs, spike S10, CAP 19): spike S10's hold writes these public members
    /// (compiled in against 0.17.3, so a later Meitou without one would throw at the call); without them <c>Holds.ControlAvailable</c> is false
    /// (one log line): TownAftercare's ControlAvailable answers false, so no town bout or Teams 1v1 match starts, and nothing is ever held.</summary>
    Holds,
    /// <summary>The corpse cleanup's furnace and removal (task 8.6, src/Engine/Corpses.cs, spike S9, CAP 14): the furnace read (TownBuilding.Function,
    /// ProductionSystem.Data), the arrival rule and the steps of the engine's private Incinerate, public members compiled in against 0.17.3; without
    /// one <c>Corpses.Available</c> is false (one log line): no furnace is found, so a dead fighter waits ("dead fighter waiting: no usable corpse
    /// furnace nearby"), no body is lifted or removed, and the batch ends at its 180 s limit (the bodies left to the town).</summary>
    Corpses,
    /// <summary>The limb shop's fit (task 8.5, src/Engine/Limbs.cs, CAP 13): the stump read, the limb record, the item made at a quality, the fit and
    /// the limb section, public members compiled in against 0.17.3 (the equipment refresh is part Rewards'); without one <c>Limbs.Available</c> is
    /// false (one log line): the limb shop queues nobody at a bout's end, fighters keep their stumps.</summary>
    Limbs,
    /// <summary>Letting a prisoner out of a cage (task 9.3d, src/Engine/Cages.cs Release / PutInCage / Imprison: FixtureRegistry.Release, whose
    /// signature 0.20.2 changed): without it <c>Cages.ReleaseAvailable</c> is false (one log line): the Arena window's match start refuses any
    /// match with a prisoner ("Prisoner matches unavailable: this game version cannot release prisoners from cages", PrisonerUtilLogic.PrepareMatch),
    /// a freedom contract's recruit is refused with the prisoner left in its cage (RecruitOutcome.ReleaseRefused), and a cage return of a prisoner
    /// held in another cage fails (Imprison refuses a held character: the C++ return-failure path); matches without prisoners and the rest of the
    /// mod stay on.</summary>
    CageRelease,
    /// <summary>SPIKE code (src/Spikes, inert until <c>pg spikes on</c>): PgCheck hooks prints these but does not fail on them.</summary>
    Spike,
}

/// <summary>On every static field of <see cref="Members"/>: the member's name as the logs and PgCheck print it, its CAP / spike reference and
/// its part. PgCheck hooks walks the fields by reflection, so a new member is checked as soon as it is declared; a field without this
/// attribute fails the check.</summary>
[AttributeUsage(AttributeTargets.Field)]
sealed class MemberAttribute(Part part, string name, string cap) : Attribute
{
    public Part Part { get; } = part;
    public string Name { get; } = name;
    public string Cap { get; } = cap;
}

/// <summary>
/// Every by-name lookup of a game member, in one place (00 §9). Each entry cites the Meitou 0.17.3 source (tag v0.17.3, fa34a1b) and carries a
/// <see cref="MemberAttribute"/>. A member that does not resolve is null: the part that needs it turns off with one log line, never throws.
/// PgCheck hooks (task 2.8, Check/Hooks.cs) resolves every one against the real MeitouClient.dll.
/// </summary>
static class Members
{
    /// <summary>Every static field of this class with its <see cref="MemberAttribute"/> (null: the field has none), in declaration order.</summary>
    internal static IReadOnlyList<(FieldInfo Field, MemberAttribute? Info)> Table() =>
        [.. typeof(Members).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(f => !f.IsLiteral && !f.Name.Contains('<'))
            .OrderBy(f => f.MetadataToken)
            .Select(f => (f, f.GetCustomAttribute<MemberAttribute>()))];

    /// <summary>The names of a part's members that did not resolve (empty: all present).</summary>
    internal static IEnumerable<string> Missing(Part part) =>
        Table().Where(e => e.Info?.Part == part && e.Field.GetValue(null) is null).Select(e => e.Info!.Name);

    /// <summary><c>sealed class DevConsoleHost(GameHost host)</c>, internal: src/Meitou.Game/DevTools/DevConsoleHost.cs:19.</summary>
    [Member(Part.Console, "Meitou.Game.DevTools.DevConsoleHost", "spike S14 (docs/spikes.md; Meitou dev console, no CAP section)")]
    internal static readonly Type? DevConsoleHost = AccessTools.TypeByName("Meitou.Game.DevTools.DevConsoleHost");

    /// <summary><c>internal void RegisterConsoleCommands()</c>: DevConsoleHost.cs:330; called once on the first frame by
    /// AgentChannel.PollControl (AgentChannel.cs:148) or by the console toggle (DevConsoleHost.cs:104), after the mods initialized.</summary>
    [Member(Part.Console, "DevConsoleHost.RegisterConsoleCommands()", "spike S14 (docs/spikes.md; Meitou dev console, no CAP section)")]
    internal static readonly MethodInfo? RegisterConsoleCommands =
        DevConsoleHost is null ? null : AccessTools.Method(DevConsoleHost, "RegisterConsoleCommands", Type.EmptyTypes);

    /// <summary><c>internal readonly DevConsole DevConsole</c>: DevConsoleHost.cs:24. The public <c>DevConsole.Register(ConsoleCommand)</c> is
    /// DevConsole.cs:130, <c>ConsoleCommand</c> the public record at DevConsole.cs:12.</summary>
    [Member(Part.Console, "DevConsoleHost.DevConsole", "spike S14 (docs/spikes.md; Meitou dev console, no CAP section)")]
    internal static readonly FieldInfo? DevConsoleField = DevConsoleHost is null ? null : AccessTools.Field(DevConsoleHost, "DevConsole");

    /// <summary>The console members that did not resolve (empty: all present).</summary>
    internal static IEnumerable<string> MissingConsole() => Missing(Part.Console);

    // ---------- the engine adapter (task 2.2): src/Engine/PgEngine.cs, src/Engine/Towns.cs; task 5.2a src/Engine/Items.cs ----------
    // PgEngine and Towns use only PUBLIC Meitou 0.17.3 API (each use cites its file:line in place), so they have no by-name entry here; Items
    // (task 5.2a) needs the internal fighter refresh, listed below with its own part (Rewards). A later task that
    // needs a by-name member for the adapter adds it in this block and to MissingEngine(); PgCheck engine checks both: this list must be
    // empty, and every adapter method must JIT-bind against the real DLLs.

    /// <summary>The adapter's by-name members that did not resolve (empty: all present): the reward items' fighter refresh (task 5.2a, below).</summary>
    internal static IEnumerable<string> MissingEngine() => Missing(Part.Rewards).Concat(Missing(Part.PrisonerCombat)).Concat(Missing(Part.Recruitment))
        .Concat(Missing(Part.CombatProfile)).Concat(Missing(Part.Holds)).Concat(Missing(Part.Corpses)).Concat(Missing(Part.Limbs))
        .Concat(Missing(Part.CageRelease));

    // ---------- the hold registry (task 8.4a): src/Engine/Holds.cs over PgEngine HoldStill / ReassertHold / ReleaseHold. Meitou 0.17.3 (fa34a1b);
    // spike S10, CAP 19. Public members the adapter calls directly; listed here so Holds.ControlAvailable (the C++ ControlAvailable) and PgCheck
    // hooks see them resolve ----------

    /// <summary><c>public bool CharacterCold.Engaged { get; set; }</c>: src/Meitou.Simulation/CharacterTable.cs:938 ("the AI leaves it alone",
    /// AiSystem.Holds :366).</summary>
    [Member(Part.Holds, "CharacterCold.Engaged", "spike S10; CAP 19; task 8.4a")]
    internal static readonly PropertyInfo? ColdEngaged = AccessTools.Property(typeof(Meitou.Simulation.CharacterCold), "Engaged");

    /// <summary><c>public bool AiMind.Walking { get; set; }</c>: src/Meitou.Simulation/Ai/Blackboard.cs:811.</summary>
    [Member(Part.Holds, "AiMind.Walking", "spike S10; CAP 19; task 8.4a")]
    internal static readonly PropertyInfo? AiWalking = AccessTools.Property(typeof(Meitou.Simulation.Ai.AiMind), "Walking");

    /// <summary><c>public bool AiMind.Replan { get; set; }</c>: src/Meitou.Simulation/Ai/Blackboard.cs:1070 (the release: the AI decides again).</summary>
    [Member(Part.Holds, "AiMind.Replan", "spike S10; CAP 19; task 8.4a")]
    internal static readonly PropertyInfo? AiReplan = AccessTools.Property(typeof(Meitou.Simulation.Ai.AiMind), "Replan");

    /// <summary><c>public static void TaskStart.Stand(CharacterTable table, int slot)</c>: src/Meitou.Simulation/Ai/TaskStart.cs:384 (the first hold).</summary>
    [Member(Part.Holds, "TaskStart.Stand(CharacterTable, Int32)", "spike S10; CAP 19; task 8.4a")]
    internal static readonly MethodInfo? TaskStand = AccessTools.Method(typeof(Meitou.Simulation.Ai.TaskStart), "Stand",
        [typeof(Meitou.Simulation.CharacterTable), typeof(int)]);

    // ---------- the corpse cleanup (task 8.6): src/Engine/Corpses.cs. Meitou 0.17.3 (fa34a1b); spike S9, CAP 14. Public members the adapter calls
    // directly; listed here so Corpses.Available and PgCheck hooks see them resolve ----------

    /// <summary><c>public bool CarrySystem.LetGo(World world, int carrier)</c>: src/Meitou.Simulation/Carrying/CarrySystem.cs:608 (Incinerate's first step).</summary>
    [Member(Part.Corpses, "CarrySystem.LetGo(World, Int32)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? CarryLetGo = AccessTools.Method(typeof(Meitou.Simulation.Carrying.CarrySystem), "LetGo",
        [typeof(Meitou.Simulation.World), typeof(int)]);

    /// <summary><c>public ConcurrentQueue&lt;Floater&gt;? World.Floaters { get; set; }</c>: src/Meitou.Simulation/Floaters.cs:121 (null without a host).</summary>
    [Member(Part.Corpses, "World.Floaters", "spike S9; CAP 14; task 8.6")]
    internal static readonly PropertyInfo? WorldFloaters = AccessTools.Property(typeof(Meitou.Simulation.World), "Floaters");

    /// <summary><c>public static Floater Floater.Label(Vector3 position, string text)</c>: src/Meitou.Simulation/Floaters.cs:103.</summary>
    [Member(Part.Corpses, "Floater.Label(Vector3, String)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? FloaterLabel = AccessTools.Method(typeof(Meitou.Simulation.Floater), "Label",
        [typeof(System.Numerics.Vector3), typeof(string)]);

    /// <summary><c>public void UniqueCharacters.Left(CharacterId id)</c>: src/Meitou.Simulation/Factions/UniqueCharacters.cs:159 (World.Uniques, World.cs:368).</summary>
    [Member(Part.Corpses, "UniqueCharacters.Left(CharacterId)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? UniquesLeft = AccessTools.Method(typeof(Meitou.Simulation.Factions.UniqueCharacters), "Left",
        [typeof(Meitou.Simulation.CharacterId)]);

    /// <summary><c>public bool CharacterTable.Remove(CharacterId id)</c>: src/Meitou.Simulation/CharacterTable.cs:1507 (throws in the Parallel, Inputs and
    /// Schedule phases: SlowWorld only).</summary>
    [Member(Part.Corpses, "CharacterTable.Remove(CharacterId)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? CharactersRemove = AccessTools.Method(typeof(Meitou.Simulation.CharacterTable), "Remove",
        [typeof(Meitou.Simulation.CharacterId)]);

    /// <summary><c>public bool CharacterTable.IsAlive(CharacterId id)</c>: src/Meitou.Simulation/CharacterTable.cs:1408 (Incinerate's check after LetGo).</summary>
    [Member(Part.Corpses, "CharacterTable.IsAlive(CharacterId)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? CharactersIsAlive = AccessTools.Method(typeof(Meitou.Simulation.CharacterTable), "IsAlive",
        [typeof(Meitou.Simulation.CharacterId)]);

    /// <summary><c>public static bool SiteReach.Within(in CharacterHot n, float gap, float reach)</c>: src/Meitou.Simulation/Construction/SiteReach.cs:32
    /// (the arrival rule; HeightLimit :39).</summary>
    [Member(Part.Corpses, "SiteReach.Within(in CharacterHot, Single, Single)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? SiteReachWithin = AccessTools.Method(typeof(Meitou.Simulation.Construction.SiteReach), "Within");

    /// <summary><c>public static float SiteReach.HeightLimit(bool door, bool inBuilding)</c>: src/Meitou.Simulation/Construction/SiteReach.cs:39.</summary>
    [Member(Part.Corpses, "SiteReach.HeightLimit(Boolean, Boolean)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? SiteReachHeight = AccessTools.Method(typeof(Meitou.Simulation.Construction.SiteReach), "HeightLimit",
        [typeof(bool), typeof(bool)]);

    /// <summary><c>public MachineData? ProductionSystem.Data(string record)</c>: src/Meitou.Simulation/Production/ProductionSystem.cs:181 (the furnace's
    /// label and use range; MachineData.Label :118, UseRange :86).</summary>
    [Member(Part.Corpses, "ProductionSystem.Data(String)", "spike S9; CAP 14; task 8.6")]
    internal static readonly MethodInfo? ProductionData = AccessTools.Method(typeof(Meitou.Simulation.Production.ProductionSystem), "Data",
        [typeof(string)]);

    /// <summary><c>public int TownBuilding.Function { get; init; }</c>: src/Meitou.Data/World/TownBuildings.cs:248 (11 = CorpseDisposalFunction, :350).</summary>
    [Member(Part.Corpses, "TownBuilding.Function", "spike S9; CAP 14; task 8.6")]
    internal static readonly PropertyInfo? TownBuildingFunction = AccessTools.Property(typeof(Meitou.Data.World.TownBuilding), "Function");

    // ---------- the limb shop (task 8.5): src/Engine/Limbs.cs. Meitou 0.17.3 (fa34a1b); CAP 13. Public members the adapter calls directly; listed
    // here so Limbs.Available and PgCheck hooks see them resolve ----------

    /// <summary><c>public ItemFactory PopulationData.Items</c>: src/Meitou.Simulation/Population/PopulationData.cs:682.</summary>
    [Member(Part.Limbs, "PopulationData.Items", "CAP 13; task 8.5")]
    internal static readonly PropertyInfo? PopulationItems = AccessTools.Property(typeof(Meitou.Simulation.Population.PopulationData), "Items");

    /// <summary><c>public LimbReplacement? ItemFactory.Limb(string recordId)</c>: src/Meitou.Simulation/Items/ItemFactory.cs:39 (null unless a
    /// LIMB_REPLACEMENT).</summary>
    [Member(Part.Limbs, "ItemFactory.Limb(String)", "CAP 13; task 8.5")]
    internal static readonly MethodInfo? ItemFactoryLimb = AccessTools.Method(typeof(Meitou.Simulation.Items.ItemFactory), "Limb", [typeof(string)]);

    /// <summary><c>public ItemInstance? ItemFactory.Create(string? recordId, int quantity = 1, float quality = 0)</c>: ItemFactory.cs:117 (a limb gets
    /// <c>Level = (int)quality</c>, :175).</summary>
    [Member(Part.Limbs, "ItemFactory.Create(String, Int32, Single)", "CAP 13; task 8.5")]
    internal static readonly MethodInfo? ItemFactoryCreate = AccessTools.Method(typeof(Meitou.Simulation.Items.ItemFactory), "Create",
        [typeof(string), typeof(int), typeof(float)]);

    /// <summary><c>public int ItemInstance.Level { get; set; }</c>: src/Meitou.Simulation/Items/ItemInstance.cs:116 (set before the fit: it is read then).</summary>
    [Member(Part.Limbs, "ItemInstance.Level", "CAP 13; task 8.5")]
    internal static readonly PropertyInfo? ItemLevel = AccessTools.Property(typeof(Meitou.Simulation.Items.ItemInstance), "Level");

    /// <summary><c>public HealthPart? MedicalState.LimbPart(int slot)</c>: src/Meitou.Simulation/Bodies/MedicalState.Save.cs:86 (0 left arm, 1 right
    /// arm, 2 left leg, 3 right leg).</summary>
    [Member(Part.Limbs, "MedicalState.LimbPart(Int32)", "CAP 13; task 8.5")]
    internal static readonly MethodInfo? MedicalLimbPart = AccessTools.Method(typeof(Meitou.Simulation.Bodies.MedicalState), "LimbPart", [typeof(int)]);

    /// <summary><c>public LimbState HealthPart.Limb { get; set; }</c>: src/Meitou.Simulation/Bodies/HealthPart.cs:98 (Stump 1, Replaced 2: PgCheck hooks).</summary>
    [Member(Part.Limbs, "HealthPart.Limb", "CAP 13; task 8.5")]
    internal static readonly PropertyInfo? HealthPartLimb = AccessTools.Property(typeof(Meitou.Simulation.Bodies.HealthPart), "Limb");

    /// <summary><c>public bool MedicalState.FitReplacement(LimbReplacement limb, ItemInstance? item, float damage = 0, float wear = 0, float stun = 0)</c>:
    /// src/Meitou.Simulation/Bodies/MedicalState.Hits.cs:311-326 (the part becomes Replaced at the item's level).</summary>
    [Member(Part.Limbs, "MedicalState.FitReplacement(LimbReplacement, ItemInstance, Single, Single, Single)", "CAP 13; task 8.5")]
    internal static readonly MethodInfo? MedicalFitReplacement = AccessTools.Method(typeof(Meitou.Simulation.Bodies.MedicalState), "FitReplacement",
        [typeof(Meitou.Data.Gameplay.Bodies.LimbReplacement), typeof(Meitou.Simulation.Items.ItemInstance), typeof(float), typeof(float), typeof(float)]);

    /// <summary><c>public static string LimbReplacement.SlotOf(int limbSlot)</c>: src/Meitou.Data/Gameplay/Bodies/LimbReplacement.cs:149 (the limb's
    /// inventory section).</summary>
    [Member(Part.Limbs, "LimbReplacement.SlotOf(Int32)", "CAP 13; task 8.5")]
    internal static readonly MethodInfo? LimbSlotOf = AccessTools.Method(typeof(Meitou.Data.Gameplay.Bodies.LimbReplacement), "SlotOf", [typeof(int)]);

    /// <summary><c>public int LimbReplacement.LimbSlot</c>: LimbReplacement.cs:139 (the record's slot - 50).</summary>
    [Member(Part.Limbs, "LimbReplacement.LimbSlot", "CAP 13; task 8.5")]
    internal static readonly PropertyInfo? LimbLimbSlot = AccessTools.Property(typeof(Meitou.Data.Gameplay.Bodies.LimbReplacement), "LimbSlot");

    /// <summary><c>public List&lt;ItemInstance&gt; Inventory.Items { get; }</c>: src/Meitou.Simulation/Items/Inventory.cs:36 (the direct list add into
    /// the limb section; never Inventory.Add, which puts an item in the main grid).</summary>
    [Member(Part.Limbs, "Inventory.Items", "CAP 13; task 8.5")]
    internal static readonly PropertyInfo? InventoryItems = AccessTools.Property(typeof(Meitou.Simulation.Items.Inventory), "Items");

    /// <summary>TEST ONLY (pg town sever): <c>public void MedicalState.Sever(int part, in MedicalContext ctx, List&lt;MedicalEvent&gt;? events = null)</c>:
    /// MedicalState.Hits.cs:230-238.</summary>
    [Member(Part.TestVerbs, "MedicalState.Sever(Int32, in MedicalContext, List<MedicalEvent>)", "CAP 13; task 8.5 (test only)")]
    internal static readonly MethodInfo? MedicalSever = AccessTools.Method(typeof(Meitou.Simulation.Bodies.MedicalState), "Sever",
        [typeof(int), typeof(Meitou.Simulation.Bodies.MedicalContext).MakeByRefType(), typeof(List<Meitou.Simulation.Bodies.MedicalEvent>)]);

    /// <summary>TEST ONLY (pg town sever): <c>public ConcurrentQueue&lt;(int Character, int Limb)&gt; CharacterTable.LimbsChanged { get; }</c>:
    /// src/Meitou.Simulation/CharacterTable.cs:1357 (the population's after-sever step, PopulationSystem.cs:266-274).</summary>
    [Member(Part.TestVerbs, "CharacterTable.LimbsChanged", "CAP 13; task 8.5 (test only)")]
    internal static readonly PropertyInfo? CharactersLimbsChanged = AccessTools.Property(typeof(Meitou.Simulation.CharacterTable), "LimbsChanged");

    // ---------- prisoner protection (task 6.2): src/Engine/PgEngine.cs EndCombat. Meitou 0.17.3 (fa34a1b); CAP 1 "Ending a bout" ----------

    /// <summary><c>internal void CombatSystem.Disengage(CharacterTable table, CharacterId id)</c>: src/Meitou.Simulation/Combat/CombatSystem.cs:595-602,
    /// documented there as <c>Character::endCombatMode</c>, any serial phase: the target, technique and blow dropped in both combat state arrays
    /// (:608-633) and the fight ended (EndFight :661-683: the combat flags cleared, an Attack task stepped to Idle, a new decision asked); any
    /// other running task (a walk) is left alone, unlike TaskStart.End. Not a patch target (called through a delegate).</summary>
    [Member(Part.PrisonerCombat, "CombatSystem.Disengage(CharacterTable, CharacterId)", "CAP 1 (Ending a bout); PA §2.19")]
    internal static readonly MethodInfo? CombatDisengage = AccessTools.Method(typeof(Meitou.Simulation.Combat.CombatSystem), "Disengage",
        [typeof(Meitou.Simulation.CharacterTable), typeof(Meitou.Simulation.CharacterId)]);

    // ---------- a character's current town (task 7.2 review): src/Engine/Towns.cs. Meitou 0.17.3 (fa34a1b); CAP 6 ----------

    /// <summary><c>internal static CampaignTown AiSystem.StatusTownAt(PopulationData data, World world, Vector3 p, CampaignTown current)</c>:
    /// src/Meitou.Simulation/Ai/AiSystem.Packages.cs:96-112: the status town (the original's status <c>+0x88</c>, FUN_1405a51e0) from a position and the
    /// current one: steps 1, 2 and 4 of docs/game/carry-loot.md "The status town". ArrestSystem.UpdateStatusTowns writes it into
    /// CharacterCold.StatusTown (Crime/ArrestSystem.cs:442-449); Towns.CurrentTownOf calls it with no current town for a character whose kept
    /// town is still none. Called through a delegate; not a patch target.</summary>
    [Member(Part.StatusTown, "AiSystem.StatusTownAt(PopulationData, World, Vector3, CampaignTown)", "CAP 6; task 7.2 review")]
    internal static readonly MethodInfo? AiStatusTownAt = AccessTools.Method(typeof(Meitou.Simulation.Ai.AiSystem), "StatusTownAt",
        [typeof(Meitou.Simulation.Population.PopulationData), typeof(Meitou.Simulation.World), typeof(System.Numerics.Vector3),
         typeof(Meitou.Simulation.Population.CampaignTown)]);

    // ---------- town fighters' combat stats (task 7.2): src/Game/ArenaCombatProfile.cs. Meitou 0.17.3 (fa34a1b); TW §2.3 ----------

    /// <summary><c>internal static float CombatSystem.AttackRating(World world, int slot, CharacterCold c, Fighter f)</c>: src/Meitou.Simulation/Combat/
    /// CombatSystem.Act.cs:157-164 (<c>FUN_140884ee0</c>, Verified): unarmed, the fist rating; else the trained melee attack times the stats' scale plus
    /// the gear's attack bonus and the weapon's attack mod (indoors modifier inside), less the dust penalty. The original's
    /// <c>CharStats::getMeleeAttack_melee()</c> (ArenaCombatProfile.cpp:78). Called through a delegate; not a patch target.</summary>
    [Member(Part.CombatProfile, "CombatSystem.AttackRating(World, Int32, CharacterCold, Fighter)", "TW §2.3; task 7.2")]
    internal static readonly MethodInfo? CombatAttackRating = AccessTools.Method(typeof(Meitou.Simulation.Combat.CombatSystem), "AttackRating",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.CharacterCold), typeof(Meitou.Simulation.Combat.Fighter)]);

    /// <summary><c>internal static float CombatSystem.DefenceRating(World world, int slot, CharacterCold c, Fighter f, bool guard = true)</c>: CombatSystem.Act.cs:
    /// 166-174 (<c>FUN_140886290</c> -&gt; <c>FUN_140884f50</c>, Verified): unarmed, the fist rating; else the trained melee defence plus the gear's
    /// defence bonus, the weapon's defence mod, the indoors modifier and, with <c>guard</c>, 20 when guarding. The original's
    /// <c>getMeleeDefence_melee(false)</c> (ArenaCombatProfile.cpp:79): called with <c>guard</c> false, as Meitou's own Rating does.</summary>
    [Member(Part.CombatProfile, "CombatSystem.DefenceRating(World, Int32, CharacterCold, Fighter, Boolean)", "TW §2.3; task 7.2")]
    internal static readonly MethodInfo? CombatDefenceRating = AccessTools.Method(typeof(Meitou.Simulation.Combat.CombatSystem), "DefenceRating",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.CharacterCold), typeof(Meitou.Simulation.Combat.Fighter), typeof(bool)]);

    /// <summary><c>internal static float CombatSystem.UnarmedRating(World world, int slot, CharacterCold c, Fighter fighter)</c>: CombatSystem.Act.cs:85-94
    /// (<c>FUN_140884e00</c> "with its flag set, as every combat caller passes it"): the raw martial arts times the scale, the gear's unarmed bonus
    /// (indoors modifier inside), the dust penalty and the load. The original's <c>getMeleeAttack_unarmed(true)</c> (ArenaCombatProfile.cpp:78).</summary>
    [Member(Part.CombatProfile, "CombatSystem.UnarmedRating(World, Int32, CharacterCold, Fighter)", "TW §2.3; task 7.2")]
    internal static readonly MethodInfo? CombatUnarmedRating = AccessTools.Method(typeof(Meitou.Simulation.Combat.CombatSystem), "UnarmedRating",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.CharacterCold), typeof(Meitou.Simulation.Combat.Fighter)]);

    // ---------- freedom contracts (task 6.4a): src/Engine/Recruiting.cs. Meitou 0.17.3 (fa34a1b); CAP 15 "Recruit" ----------

    /// <summary><c>internal bool DialogueSystem.JoinPlayer(World world, int slot, CharacterCold talker)</c>: src/Meitou.Simulation/Dialogue/
    /// DialogueSystem.Recruit.cs:93-131: the character at <c>slot</c> leaves its squad and joins the player's active squad (a new blank squad when
    /// it is full) as a player character (faction, squad, IsPlayer, AI and jobs cleared, uniforms dropped, Save = null); false with no active squad.
    /// <c>talker</c> is not read in 0.17.3. Meitou's own recruits call it after the faction cap check (Recruit :30-36, GiveSlaves :157-160: Release
    /// first). Called on the world's DialogueSystem through a delegate; not a patch target.</summary>
    [Member(Part.Recruitment, "DialogueSystem.JoinPlayer(World, Int32, CharacterCold)", "CAP 15 (Recruit); PA §2.19")]
    internal static readonly MethodInfo? JoinPlayer = AccessTools.Method(typeof(Meitou.Simulation.Dialogue.DialogueSystem), "JoinPlayer",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.CharacterCold)]);

    /// <summary><c>internal static int DialogueSystem.PlayerHeads(World world)</c>: DialogueSystem.Recruit.cs:48-55: the living player characters
    /// (the faction size cap counts them, CONSTANTS <c>max faction size</c>), as Recruit (:30) and GiveSlaves (:160) read it before JoinPlayer.
    /// 0.20.2-nightly.20261117.2 removed it: its Recruit / GiveSlaves read <c>public static int SquadManagement.CappedHeads(World)</c>
    /// (src/Meitou.Simulation/Factions/SquadManagement.cs, the living player characters the cap counts, members of a squad a code mod keeps
    /// uncounted left out), so the faction cap is read through whichever the game has, the old one first. <c>PgCheck versions</c> lists both.</summary>
    [Member(Part.Recruitment, "DialogueSystem.PlayerHeads(World) | SquadManagement.CappedHeads(World)", "CAP 15 (Recruit); PA §2.19")]
    internal static readonly MethodInfo? PlayerHeads =
        AccessTools.Method(typeof(Meitou.Simulation.Dialogue.DialogueSystem), "PlayerHeads", [typeof(Meitou.Simulation.World)]) is { IsStatic: true, ReturnType: var r1 } h1
            && r1 == typeof(int) ? h1
        : typeof(Meitou.Simulation.World).Assembly.GetType("Meitou.Simulation.Factions.SquadManagement") is { } squads
            && AccessTools.Method(squads, "CappedHeads", [typeof(Meitou.Simulation.World)]) is { IsStatic: true, ReturnType: var r2 } h2 && r2 == typeof(int) ? h2
        : null;

    // ---------- reward items (task 5.2a): src/Engine/Items.cs. Meitou 0.17.3 (fa34a1b); CAP 10 "Trap: the fighter is not rebuilt" ----------

    /// <summary><c>sealed class CharacterAssembly(PopulationData data)</c>, internal: src/Meitou.Simulation/Bodies/CharacterAssembly.cs:19. The
    /// population, the save loader and the debug commands each make their own (PopulationSystem.cs:144, SaveLoader.cs:95, StandardSystems.cs:601).</summary>
    [Member(Part.Rewards, "Meitou.Simulation.Bodies.CharacterAssembly", "CAP 10 (the fighter is not rebuilt); PA §2.18")]
    internal static readonly Type? CharacterAssembly = AccessTools.TypeByName("Meitou.Simulation.Bodies.CharacterAssembly");

    /// <summary>Its primary constructor <c>CharacterAssembly(PopulationData data)</c>: CharacterAssembly.cs:19.</summary>
    [Member(Part.Rewards, "CharacterAssembly(PopulationData)", "CAP 10 (the fighter is not rebuilt); PA §2.18")]
    internal static readonly ConstructorInfo? CharacterAssemblyNew = CharacterAssembly is null ? null
        : AccessTools.Constructor(CharacterAssembly, [typeof(Meitou.Simulation.Population.PopulationData)]);

    /// <summary><c>public void RefreshEquipment(CharacterCold cold)</c>: CharacterAssembly.cs:207-222: builds <c>CharacterCold.Fighter</c> again
    /// from the inventory (the weapons of the back and hip sections, the worn armour; keeps guard and encumbrance), the drawn weapon following the
    /// new kit, dyes the gear and rebuilds the look. Combat reads only <c>Fighter</c> (CharacterAssembly.cs:72-90), so a reward worn, taken off or
    /// replaced does nothing until this runs. Not a patch target (called by reflection).</summary>
    [Member(Part.Rewards, "CharacterAssembly.RefreshEquipment(CharacterCold)", "CAP 10 (the fighter is not rebuilt); PA §2.18")]
    internal static readonly MethodInfo? RefreshEquipment = CharacterAssembly is null ? null
        : AccessTools.Method(CharacterAssembly, "RefreshEquipment", [typeof(Meitou.Simulation.CharacterCold)]);

    /// <summary><c>public bool Inventory.Add(ItemInstance item)</c> (0.17.3: src/Meitou.Simulation/Items/Inventory.cs:146), which 0.20 changed to
    /// <c>Add(ItemInstance item, bool mainShut = false)</c> (v0.20.1-nightly.20261115.3: Inventory.cs:147): a direct call compiled against 0.17.3
    /// throws MissingMethodException at JIT on 0.20 (meitou.json allows >=0.17.3). Bound by reflection to whichever exists (mainShut = false:
    /// the 0.17.3 behaviour), so Items.Give works on both. <c>PgCheck versions</c> reads this signature from every installed game version.</summary>
    [Member(Part.Rewards, "Inventory.Add(ItemInstance[, bool mainShut])", "CAP 10 (putting it on a character); PA §2.18")]
    internal static readonly MethodInfo? InventoryAdd =
        AccessTools.Method(typeof(Meitou.Simulation.Items.Inventory), "Add", [typeof(Meitou.Simulation.Items.ItemInstance)]) is { ReturnType: var r1 } m1 && r1 == typeof(bool) ? m1
        : AccessTools.Method(typeof(Meitou.Simulation.Items.Inventory), "Add", [typeof(Meitou.Simulation.Items.ItemInstance), typeof(bool)]) is { ReturnType: var r2 } m2 && r2 == typeof(bool) ? m2
        : null;

    // ---------- the cage release (task 9.3d): src/Engine/Cages.cs. Meitou 0.17.3 (fa34a1b), 0.20.1-nightly.20261115.3 (8d92723), 0.20.2-nightly.20261116 (42fc341) ----------

    /// <summary><c>public bool FixtureRegistry.Release(World world, CharacterId prisoner, int releaserFaction = -1, CrimeSystem? crimes = null)</c>:
    /// src/Meitou.Simulation/Locks/FixtureRegistry.cs:1751 at 0.17.3, :1869 at 0.20.1-nightly.20261115.3 (same four parameters; 0.18.x, 0.19.0 and
    /// 0.20.0 have them too, RefCheck), which 0.20.2-nightly.20261116 changed to <c>Release(..., CrimeSystem? crimes = null, bool jailer = false)</c>
    /// (FixtureRegistry.cs:1875: with <c>jailer</c> false the player-prisoner line "{1} has been released from prison." is not posted, as for bail,
    /// trade, recruiting and liberators). An added optional parameter is a binary break: a direct call compiled against 0.17.3 throws
    /// MissingMethodException at JIT on 0.20.2. Bound by reflection to whichever exists, called with releaser -1, no crime system and (five
    /// parameters) jailer false: the mod's releases are no jailer's task. <c>PgCheck versions</c> reads both signatures from every installed version.</summary>
    [Member(Part.CageRelease, "FixtureRegistry.Release(World, CharacterId, Int32, CrimeSystem[, Boolean jailer])", "CAP 15; task 6.2 (spike S11); task 9.3d")]
    internal static readonly MethodInfo? FixturesRelease =
        AccessTools.Method(typeof(Meitou.Simulation.Locks.FixtureRegistry), "Release", [typeof(Meitou.Simulation.World), typeof(Meitou.Simulation.CharacterId),
            typeof(int), typeof(Meitou.Simulation.Crime.CrimeSystem)]) is { ReturnType: var r4 } m4 && r4 == typeof(bool) ? m4
        : AccessTools.Method(typeof(Meitou.Simulation.Locks.FixtureRegistry), "Release", [typeof(Meitou.Simulation.World), typeof(Meitou.Simulation.CharacterId),
            typeof(int), typeof(Meitou.Simulation.Crime.CrimeSystem), typeof(bool)]) is { ReturnType: var r5 } m5 && r5 == typeof(bool) ? m5
        : null;

    // ---------- save safety (task 2.5, spike S5 kept for real): src/Game/WorldLifecycle.cs. Meitou 0.17.3 (fa34a1b); CAP table "The original's hooks" (SaveManager::save, execute, updateAutoSave), spike S5 ----------

    /// <summary><c>sealed class GameSaves(GameHost host)</c>, internal: src/Meitou.Game/Host/GameSaves.cs:24. Saves happen in its
    /// <c>HandleSaves</c>, once per frame between ticks (:50-91), on the host's frame thread.</summary>
    [Member(Part.Save, "Meitou.Game.Host.GameSaves", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly Type? GameSaves = AccessTools.TypeByName("Meitou.Game.Host.GameSaves");
    [Member(Part.Save, "Meitou.Game.GameHost", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    static readonly Type? GameHost = AccessTools.TypeByName("Meitou.Game.GameHost");
    [Member(Part.Save, "Meitou.Game.PlayerInterface", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    static readonly Type? PlayerInterface = AccessTools.TypeByName("Meitou.Game.PlayerInterface");

    /// <summary><c>internal void QuickSaveNow(string? destination = null, bool feedback = false)</c>: GameSaves.cs:190. The single entry of
    /// every save: F5 (QuickSaveRequested, :60-64; GameLoop.cs:718), menu Save and the agent's quicksave (SaveRequestedFolder, :70-74;
    /// GameLoop.cs:683, AgentWorld.cs:920-927), the autosave (<c>QuickSaveNow(QuickSave.AutosaveFolder(i))</c>, :105) and the feedback
    /// report (<c>feedback: true</c>, FeedbackCapture.cs:253). Patched with a bool prefix (spike S5, VERIFIED live).</summary>
    [Member(Part.Save, "GameSaves.QuickSaveNow(string?, bool)", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly MethodInfo? QuickSaveNow =
        GameSaves is null ? null : AccessTools.Method(GameSaves, "QuickSaveNow", [typeof(string), typeof(bool)]);

    /// <summary><c>internal void HandleSaves(double realDt)</c>: GameSaves.cs:54, called once per frame after the world advanced, between ticks
    /// (GameLoop.cs:1346, MetalGameLoop.cs:2340). Its prefix re-requests a save the system postponed or prepared, on the frame thread that owns
    /// the request fields; its postfix ends that frame's pass.</summary>
    [Member(Part.Save, "GameSaves.HandleSaves(double)", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly MethodInfo? HandleSaves =
        GameSaves is null ? null : AccessTools.Method(GameSaves, "HandleSaves", [typeof(double)]);

    /// <summary><c>internal bool QuickSaveRequested</c>: GameSaves.cs:31 (F5: <c>QuickSaveNow()</c> into the quicksave slot, :60-64).</summary>
    [Member(Part.Save, "GameSaves.QuickSaveRequested", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly FieldInfo? QuickSaveRequested = GameSaves is null ? null : AccessTools.Field(GameSaves, "QuickSaveRequested");

    /// <summary><c>internal string? SaveRequestedFolder</c>: GameSaves.cs:44 (<c>QuickSaveNow(folder)</c>, :70-74).</summary>
    [Member(Part.Save, "GameSaves.SaveRequestedFolder", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly FieldInfo? SaveRequestedFolder = GameSaves is null ? null : AccessTools.Field(GameSaves, "SaveRequestedFolder");

    /// <summary><c>internal bool Busy()</c>: GameSaves.cs:130 (<c>SaveWrite is { IsCompleted: false } || host.Covered</c>): QuickSaveNow refuses
    /// a save while it is true ("Save or load still in progress", :204-207), so a re-requested save waits for it.</summary>
    [Member(Part.Save, "GameSaves.Busy()", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly MethodInfo? SavesBusy = GameSaves is null ? null : AccessTools.Method(GameSaves, "Busy", Type.EmptyTypes);

    /// <summary>private <c>bool _autosaveDue</c>: GameSaves.cs:39 (Meitou's autosave, written at the start of the next HandleSaves, :100-106).</summary>
    [Member(Part.Save, "GameSaves._autosaveDue", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly FieldInfo? AutosaveDue = GameSaves is null ? null : AccessTools.Field(GameSaves, "_autosaveDue");

    /// <summary>The <c>GameHost host</c> GameSaves captures from its primary constructor (GameSaves.cs:24): a compiler-named field, found by
    /// its type (the only GameHost field).</summary>
    [Member(Part.Save, "GameSaves host field", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly FieldInfo? GameSavesHost =
        GameSaves is null || GameHost is null ? null : AccessTools.GetDeclaredFields(GameSaves).FirstOrDefault(f => f.FieldType == GameHost);

    /// <summary><c>internal WorldSession Session</c>: src/Meitou.Game/GameHost.cs:283 (public type; its <c>Simulation.IsPaused</c>,
    /// WorldSession.cs:75, SimulationClock.cs:118, is what Meitou's autosave reads, GameSaves.cs:108).</summary>
    [Member(Part.Save, "GameHost.Session", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly FieldInfo? HostSession = GameHost is null ? null : AccessTools.Field(GameHost, "Session");

    /// <summary><c>internal PlayerInterface Player</c>: src/Meitou.Game/GameHost.cs:332.</summary>
    [Member(Part.Save, "GameHost.Player", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly FieldInfo? HostPlayer = GameHost is null ? null : AccessTools.Field(GameHost, "Player");

    /// <summary><c>public void Notice(string text, bool error = false, string? logText = null)</c>: src/Meitou.Game/PlayerInterface.cs:392
    /// (on the internal PlayerInterface, so by name). The HUD notice, frame thread only (MessageRoller.QueueNotice, MessageRoller.cs:49).</summary>
    [Member(Part.Save, "PlayerInterface.Notice(string, bool, string?)", "CAP hooks table: SaveManager::save, execute, updateAutoSave; spike S5")]
    internal static readonly MethodInfo? Notice =
        PlayerInterface is null ? null : AccessTools.Method(PlayerInterface, "Notice", [typeof(string), typeof(bool), typeof(string)]);

    /// <summary>The save-safety members that did not resolve (empty: all present).</summary>
    internal static IEnumerable<string> MissingSave() => Missing(Part.Save);

    // ---------- the combat hooks (task 3.2b): src/Game/CombatHooks.cs. Meitou 0.17.3 (fa34a1b); PA §2.8, CAP 1-3 and the hooks table, spikes S1, S2 ----------
    // The types are public (typeof); the methods and the field are found by name.

    /// <summary>private <c>void ApplyBlow(World world, in Effect e)</c>: src/Meitou.Simulation/Combat/CombatSystem.Apply.cs:70; its one caller is
    /// the public <c>Apply</c> (:49-56), run only by the world's serial commit step (World.cs:710-727). Prefix + postfix (spike S2, moved here
    /// from the spike part in task 3.2b).</summary>
    [Member(Part.CombatHooks, "CombatSystem.ApplyBlow(World, in Effect)", "spike S2; CAP 3, hooks table _iHitYouAreYouHit / hitByMeleeAttack")]
    internal static readonly MethodInfo? ApplyBlow =
        AccessTools.Method(typeof(Meitou.Simulation.Combat.CombatSystem), "ApplyBlow", [typeof(Meitou.Simulation.World), typeof(Meitou.Simulation.Effect).MakeByRefType()]);

    /// <summary>private <c>CombatSlot[] _write</c>: CombatSystem.cs:82 (the slots the tick writes; swapped with <c>_read</c> every tick,
    /// :790, so it is read afresh in each patch call). Spike S2, moved here in task 3.2b.</summary>
    [Member(Part.CombatHooks, "CombatSystem._write", "spike S2; CAP 3, hooks table _iHitYouAreYouHit / hitByMeleeAttack")]
    internal static readonly FieldInfo? CombatWrite = AccessTools.Field(typeof(Meitou.Simulation.Combat.CombatSystem), "_write");

    /// <summary>public <c>int BestAttackerOf(World world, int subject, int looker, IReadOnlyDictionary&lt;int, int&gt;? pending = null)</c>:
    /// CombatSystem.Targets.cs:202 (self-defence and the protect goals pick the foe with it on the parallel think, AiSystem.Fights.cs:155-172,
    /// :370-391; the player's 15 Protect allies and the bodyguards' join in serial steps, Fighting.cs:718-722, :991). Postfix.</summary>
    [Member(Part.CombatHooks, "CombatSystem.BestAttackerOf(World, int, int, IReadOnlyDictionary<int, int>?)", "CAP 1 'The defender fights back', CAP 2, hooks table isFightingAnAllyOfMine / attackTarget")]
    internal static readonly MethodInfo? BestAttackerOf = AccessTools.Method(typeof(Meitou.Simulation.Combat.CombatSystem), "BestAttackerOf",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(int), typeof(IReadOnlyDictionary<int, int>)]);

    /// <summary>public <c>CharacterId FindFoe(World world, int slot, FoeFinder finder, IReadOnlyDictionary&lt;int, int&gt;? pending = null)</c>:
    /// src/Meitou.Simulation/Aggro/AggroSystem.cs:1786, the attack-goal finders ("a pure read of the last looks and fights: any thread in the
    /// think", :1776-1785; called from AiSystem.Fights.cs:115-119). Postfix.</summary>
    [Member(Part.CombatHooks, "AggroSystem.FindFoe(World, int, FoeFinder, IReadOnlyDictionary<int, int>?)", "CAP 2, hooks table iShouldntAggravateThisTarget")]
    internal static readonly MethodInfo? FindFoe = AccessTools.Method(typeof(Meitou.Simulation.Aggro.AggroSystem), "FindFoe",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.Aggro.AggroSystem.FoeFinder), typeof(IReadOnlyDictionary<int, int>)]);

    /// <summary>public <c>bool BeginPickUp(World world, int carrier, CharacterId body, CharacterTask resume = Idle, bool kidnap = false, TaskSource
    /// source = Npc, int job = -1, TaskType? row = null)</c>: src/Meitou.Simulation/Carrying/CarrySystem.cs:545 ("serial phases"; every NPC lift:
    /// town duty, kidnap, arrest, carrying errands). Prefix (GuildQuests 0.6 patches it too, TrialPart.cs:36: both only refuse their own cases).</summary>
    [Member(Part.CombatHooks, "CarrySystem.BeginPickUp(World, int, CharacterId, ...)", "CAP 2, hooks table pickupObject")]
    internal static readonly MethodInfo? BeginPickUp = AccessTools.Method(typeof(Meitou.Simulation.Carrying.CarrySystem), "BeginPickUp");

    /// <summary>private <c>void Remember(World world, int striker, int victim, bool dead)</c>: src/Meitou.Simulation/Factions/Relations.cs:185 on the
    /// public FactionDeeds: the long squad memory (KILLED_MY_FRIEND, DEFEATED_MY_SQUAD, SQUAD_LOST_TO_ME) of a downing blow between two different
    /// squads (:185-200), from <c>FactionDeeds.Blow</c> (:150-152), called by the commit step's LandBlow (CombatSystem.Apply.cs:327). Not a tiny
    /// method (a loop): no inlining risk. <c>SquadMemory.Remember</c> (Dialogue/SquadMemory.cs:127) is the general tag writer every dialogue
    /// effect uses, so it is not the one patched. Prefix.</summary>
    [Member(Part.CombatHooks, "FactionDeeds.Remember(World, int, int, bool)", "CAP 1 'Long squad memory'")]
    internal static readonly MethodInfo? FactionRemember = AccessTools.Method(typeof(Meitou.Simulation.Factions.FactionDeeds), "Remember",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(int), typeof(bool)]);

    // task 7.4 (the town rules for fights, M7-town-core.md; CAP 2 option 2, CAP 20 and the hooks table): one more patch each, in the same patch
    // class (CombatHookPatches), each acting only for the town bout's sanctioned pairs or its fighters.

    /// <summary>public <c>bool SetPending(World world, int culprit, CrimeCode code, int victimFaction, CharacterId victim, float timer, bool viaWrapper
    /// = false)</c>: src/Meitou.Simulation/Crime/CrimeSystem.cs:117, the game's pending-crime setter <c>FUN_140852c80</c> (named so at
    /// CarrySystem.StealthKo.cs:313-316): every crime is set through it (an attack order's Assault :158, the police-hit rule :246, the stealth
    /// knock-out's swing StealthKo.cs:316, stealing, kidnapping, locks, cages, fencing). Meitou's counterpart of <c>BountyManager::setCrime</c>
    /// (CAP 20). Serial (orders, the commit step, slow-world steps). Prefix.
    /// 0.20.2-nightly.20261117.2 added two optional parameters, <c>string? victimHolder = null, bool wasUp = false</c> (CrimeSystem.cs:121): the
    /// prefix reads only world, culprit, code and victim by name, so it is patched onto whichever of the two the game has (the 7-parameter one
    /// first); <c>PgCheck versions</c> lists both.</summary>
    [Member(Part.CombatHooks, "CrimeSystem.SetPending(World, int, CrimeCode, int, CharacterId, float, bool[, string, bool])", "CAP 2 option 2, CAP 20, hooks table setCrime")]
    internal static readonly MethodInfo? CrimeSetPending =
        AccessTools.Method(typeof(Meitou.Simulation.Crime.CrimeSystem), "SetPending",
            [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.Crime.CrimeCode), typeof(int), typeof(Meitou.Simulation.CharacterId),
                typeof(float), typeof(bool)])
        ?? AccessTools.Method(typeof(Meitou.Simulation.Crime.CrimeSystem), "SetPending",
            [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.Crime.CrimeCode), typeof(int), typeof(Meitou.Simulation.CharacterId),
                typeof(float), typeof(bool), typeof(string), typeof(bool)]);

    /// <summary>public <c>void Blow(World world, int striker, int victim, bool down, bool dead, bool bolt = false, bool aimed = true)</c>:
    /// src/Meitou.Simulation/Factions/Relations.cs:150 on the public FactionDeeds: every faction event of a blow (Killed, Hit, Knockout,
    /// KillingBlow, :150-163, through Apply :134-139) and the long squad memory (Remember, :152). Its callers: LandBlow (CombatSystem.Apply.cs:327,
    /// serial commit) and the bolt hit (CombatSystem.Ranged.cs:825). Counterpart of <c>FactionRelations::affectRelations</c> / <c>setEnemy</c>
    /// (CAP 20). Prefix.</summary>
    [Member(Part.CombatHooks, "FactionDeeds.Blow(World, int, int, bool, bool, bool, bool)", "CAP 2 option 2, CAP 20, hooks table setEnemy / affectRelations")]
    internal static readonly MethodInfo? DeedsBlow = AccessTools.Method(typeof(Meitou.Simulation.Factions.FactionDeeds), "Blow",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool)]);

    /// <summary>private <c>(int Slot, float Score) FindPatient(World world, int i, SquadGoals taken, bool robotics, bool ordered = false)</c>:
    /// src/Meitou.Simulation/FirstAid/FirstAidSystem.Finder.cs:381, the one medic finder (the original's <c>FUN_14099f690</c>) behind every
    /// automatic or job treatment: the NPCs' looks (FirstAidSystem.cs:807-866 -> KeepPick :879 / HealSomeone Finder.cs:826), the AI's goal
    /// treatments (SendGoalTreatments :891), the Medic / Robotics jobs (MedicJobs.CanRun :955) and the Medic button; all serial. Not a tiny method
    /// (several passes and loops): no inlining risk. The one overload (SquadGoals is internal, so it is found by name). Counterpart of
    /// <c>Character::shouldIHelpThisGuy</c> as the finder's patient test (CAP 12, hooks table). Postfix.</summary>
    [Member(Part.CombatHooks, "FirstAidSystem.FindPatient(World, int, SquadGoals, bool, bool)", "CAP 12, hooks table shouldIHelpThisGuy")]
    internal static readonly MethodInfo? FindPatient = AccessTools.Method(typeof(Meitou.Simulation.FirstAid.FirstAidSystem), "FindPatient");

    /// <summary>The combat-hook members that did not resolve (empty: all present).</summary>
    internal static IEnumerable<string> MissingCombatHooks() => Missing(Part.CombatHooks);

    // ---------- the medical protocol (task 4.7, src/Game/ArenaMedical.cs). Meitou 0.17.3 (fa34a1b). ----------

    /// <summary>public <c>void Apply(World world, in Effect effect)</c> on the public FirstAidSystem: src/Meitou.Simulation/FirstAid/FirstAidSystem.cs:524
    /// (ITickSystem step 6, run only by World.Commit's serial loop over the sorted effects, World.cs:720-724): one tick of treatment
    /// (<c>EffectTreat</c>, :51) from the medic (<c>Source</c>) on the patient (<c>Target</c>): closing a wound, bandaging with a first aid kit or a
    /// robot repair kit, kitless care of a prisoner, or a splint. Meitou's counterpart of MedicalSystem::applyFirstAid, applyDoctoring and
    /// applyRigging together. Not a tiny method (no inlining risk). Prefix.</summary>
    [Member(Part.Medical, "FirstAidSystem.Apply(World, in Effect)", "CAP 12; PA §2.11 hooks applyFirstAid / applyDoctoring / applyRigging")]
    internal static readonly MethodInfo? FirstAidApply = AccessTools.Method(typeof(Meitou.Simulation.FirstAid.FirstAidSystem), "Apply",
        [typeof(Meitou.Simulation.World), typeof(Meitou.Simulation.Effect).MakeByRefType()]);

    /// <summary>The medical members that did not resolve (empty: all present).</summary>
    internal static IEnumerable<string> MissingMedical() => Missing(Part.Medical);

    // ---------- TEST-ONLY verb members (task 4.1, from spike S15; src/Game/ArenaIdentityTestVerbs.cs). Meitou 0.17.3 (fa34a1b). ----------

    /// <summary>private <c>void Finish(World, BuildSite)</c>: src/Meitou.Simulation/Construction/ConstructionSystem.cs:1386-1396 (the engine's own
    /// completion: progress = target, complete, "construction complete" notice). <c>pg finish-build</c>.</summary>
    [Member(Part.TestVerbs, "ConstructionSystem.Finish(World, BuildSite)", "spike S15; CAP 6")]
    internal static readonly MethodInfo? ConstructionFinish = AccessTools.Method(typeof(Meitou.Simulation.Construction.ConstructionSystem), "Finish",
        [typeof(Meitou.Simulation.World), typeof(Meitou.Simulation.Construction.BuildSite)]);
    /// <summary>private <c>IReadOnlyList&lt;PlacementTown&gt; _towns</c>: ConstructionSystem.cs:134. <c>pg findspot</c>.</summary>
    [Member(Part.TestVerbs, "ConstructionSystem._towns", "spike S15; CAP 6")]
    internal static readonly FieldInfo? ConstructionTowns = AccessTools.Field(typeof(Meitou.Simulation.Construction.ConstructionSystem), "_towns");
    /// <summary>private <c>string _playerFactionId</c>: ConstructionSystem.cs:139. <c>pg findspot</c>.</summary>
    [Member(Part.TestVerbs, "ConstructionSystem._playerFactionId", "spike S15; CAP 6")]
    internal static readonly FieldInfo? ConstructionPlayerFactionId = AccessTools.Field(typeof(Meitou.Simulation.Construction.ConstructionSystem), "_playerFactionId");

    // ---------- the window framework (task 4.4): src/Ui/ScreenHost.cs, src/Ui/Portraits.cs. Meitou 0.17.3 (fa34a1b); CAP 7.0-7.4, spikes S6, S7 (VERIFIED live) ----------
    // Spike S6/S7's members moved here (the spike's test window and pg win / pg portrait are gone; task 4.4's pg window test replaces them).

    [Member(Part.Windows, "Meitou.Game.PlayerInterface", "CAP 7.2; spike S6")]
    static readonly Type? UiInterface = AccessTools.TypeByName("Meitou.Game.PlayerInterface");

    /// <summary><c>public void LoadUi(UiMode mode, Func&lt;string, string?&gt; resolve)</c> on the internal PlayerInterface:
    /// src/Meitou.Game/PlayerInterface.Ui.cs:143; makes a NEW MyGuiScreenHost each time (:166). Postfix: register every Proving Grounds screen.</summary>
    [Member(Part.Windows, "PlayerInterface.LoadUi(UiMode, Func<string, string?>)", "CAP 7.2; spike S6")]
    internal static readonly MethodInfo? LoadUi = UiInterface is null ? null
        : AccessTools.Method(UiInterface, "LoadUi", [typeof(Meitou.Data.Ui.UiMode), typeof(Func<string, string?>)]);

    /// <summary><c>internal MyGuiScreenHost FaithfulScreens</c>: PlayerInterface.Ui.cs:61 (public class MyGuiScreenHost, MyGuiScreenHost.cs:19).</summary>
    [Member(Part.Windows, "PlayerInterface.FaithfulScreens", "CAP 7.2; spike S6")]
    internal static readonly FieldInfo? FaithfulScreens = UiInterface is null ? null : AccessTools.Field(UiInterface, "FaithfulScreens");

    [Member(Part.Windows, "Meitou.Game.Ui.ScreenRouter", "CAP 7.2")]
    static readonly Type? ScreenRouter = AccessTools.TypeByName("Meitou.Game.Ui.ScreenRouter");

    /// <summary><c>internal UiBindings BindFaithfulScreen(string id)</c>: src/Meitou.Game/Ui/ScreenRouter.cs:74 (internal class :10); an unknown
    /// screen gets every button disabled (:84-85). Called for each open screen whenever the interface arranges its base
    /// (MyGuiScreenHost.Arrange, MyGuiScreenHost.cs:366; PlayerInterface.Ui.cs:531). Postfix: our screens' texts, actions and pictures.</summary>
    [Member(Part.Windows, "ScreenRouter.BindFaithfulScreen(string)", "CAP 7.2")]
    internal static readonly MethodInfo? BindFaithfulScreen = ScreenRouter is null ? null : AccessTools.Method(ScreenRouter, "BindFaithfulScreen", [typeof(string)]);

    /// <summary><c>public bool CtrlHeld</c> field on PlayerInterface: PlayerInterface.cs:723 ("Ctrl ... as the host last saw it", set every frame by
    /// GameLoop.cs:1444). Read at a button press as a flag the test window logs (task 4.4's right-click stand-in; since task 4.5b the right-click is real).</summary>
    [Member(Part.Windows, "PlayerInterface.CtrlHeld", "CAP 7.0; task 4.4 right-click question")]
    internal static readonly FieldInfo? CtrlHeld = UiInterface is null ? null : AccessTools.Field(UiInterface, "CtrlHeld");

    /// <summary><c>public bool MouseDown(MouseButton button, Vector2 p, bool shift, bool ctrl = false, bool alt = false)</c>: PlayerInterface.cs:779;
    /// every press of the game window (the agent channel's click too, GameLoop.cs StepControlPointer). A right press over a faithful screen reaches no
    /// widget (MyGuiScreenHost handles only PointerDown from LeftDown, PlayerInterface.Ui.cs:657-667; :797 returns OverHud). Postfix (task 4.5b): the
    /// right press on our windows (the original's MouseButton::Right handlers) and the left button's held state for a row drag.</summary>
    [Member(Part.Windows, "PlayerInterface.MouseDown(MouseButton, Vector2, bool, bool, bool)", "CAP 7.0; task 4.5b right press")]
    internal static readonly MethodInfo? MouseDown = UiInterface is null ? null : AccessTools.Method(UiInterface, "MouseDown",
        [typeof(Meitou.Game.Host.Input.MouseButton), typeof(System.Numerics.Vector2), typeof(bool), typeof(bool), typeof(bool)]);

    /// <summary><c>public void MouseUp(MouseButton button, Vector2 p, bool shift)</c>: PlayerInterface.cs:1023 (every release; ends the screens'
    /// drags, :1037). Postfix (task 4.5b): the release a row's eventMouseButtonReleased / Click / DoubleClick come from.</summary>
    [Member(Part.Windows, "PlayerInterface.MouseUp(MouseButton, Vector2, bool)", "CAP 7.0; task 4.5b row release")]
    internal static readonly MethodInfo? MouseUp = UiInterface is null ? null : AccessTools.Method(UiInterface, "MouseUp",
        [typeof(Meitou.Game.Host.Input.MouseButton), typeof(System.Numerics.Vector2), typeof(bool)]);

    [Member(Part.WindowsLoopPass, "Meitou.Game.Host.GameLoop", "CAP 7.0; task 4.5b review (loop passes)")]
    static readonly Type? GameLoop = AccessTools.TypeByName("Meitou.Game.Host.GameLoop");

    /// <summary>private <c>bool Frame()</c> on the internal sealed GameLoop (the Vulkan loop, Windows): src/Meitou.Game/Host/GameLoop.cs:1231, one frame
    /// of the loop, its <c>_window.DoEvents()</c> (every mouse event to the interface) at :1235. Prefix (task 4.5b review): counts the loop passes the
    /// held-button reconcile needs (an interface arrange is not a pass: event handlers inside DoEvents arrange too). A large method, not inlined.
    /// MetalGameLoop (macOS) is not patched: the system's buttons are read on Windows only.</summary>
    [Member(Part.WindowsLoopPass, "GameLoop.Frame()", "CAP 7.0; task 4.5b review (loop passes)")]
    internal static readonly MethodInfo? GameLoopFrame = GameLoop is null ? null : AccessTools.Method(GameLoop, "Frame", Type.EmptyTypes);

    // ---- a right press the game takes first (task 4.5b review): PlayerInterface.MouseDown, PlayerInterface.cs:779-800, answers true for a right
    // press before it would reach a window when an inventory drag is cancelled (:786, InventoryWindows.CancelInventoryDrag, InventoryWindows.cs:738-748:
    // no modal window and a held item, a scrollbar drag or a lift press) or an open context menu is closed (:792, ContextMenuWindow.CloseContextMenu,
    // ContextMenuWindow.cs:764-770). Read as a state snapshot in the MouseDown prefix (CloseContextMenu is small enough to be inlined into MouseDown,
    // Meitou docs/modding.md "Harmony", so it is not patched).

    /// <summary><c>internal ContextMenuWindow ContextMenuWindow</c> property: PlayerInterface.cs:87.</summary>
    [Member(Part.WindowsPressTaken, "PlayerInterface.ContextMenuWindow", "CAP 7.0; task 4.5b review (a right press the game took)")]
    internal static readonly PropertyInfo? ContextMenuWindowProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "ContextMenuWindow");

    /// <summary><c>internal ContextMenu? OpenMenu</c> field: ContextMenuWindow.cs:41 (CloseContextMenu answers whether it was set).</summary>
    [Member(Part.WindowsPressTaken, "ContextMenuWindow.OpenMenu", "CAP 7.0; task 4.5b review (a right press the game took)")]
    internal static readonly FieldInfo? OpenMenu = ContextMenuWindowProp is null ? null : AccessTools.Field(ContextMenuWindowProp.PropertyType, "OpenMenu");

    /// <summary><c>internal InventoryWindows InventoryWindows</c> property: PlayerInterface.cs:135.</summary>
    [Member(Part.WindowsPressTaken, "PlayerInterface.InventoryWindows", "CAP 7.0; task 4.5b review (a right press the game took)")]
    internal static readonly PropertyInfo? InventoryWindowsProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "InventoryWindows");

    /// <summary><c>internal HeldItemView? Held</c> field: InventoryWindows.cs:22 (an item on the pointer).</summary>
    [Member(Part.WindowsPressTaken, "InventoryWindows.Held", "CAP 7.0; task 4.5b review (a right press the game took)")]
    internal static readonly FieldInfo? HeldItem = InventoryWindowsProp is null ? null : AccessTools.Field(InventoryWindowsProp.PropertyType, "Held");

    /// <summary><c>internal Vector2? LiftPress</c> field: InventoryWindows.cs:38 (a press on an item not yet lifted).</summary>
    [Member(Part.WindowsPressTaken, "InventoryWindows.LiftPress", "CAP 7.0; task 4.5b review (a right press the game took)")]
    internal static readonly FieldInfo? LiftPress = InventoryWindowsProp is null ? null : AccessTools.Field(InventoryWindowsProp.PropertyType, "LiftPress");

    /// <summary><c>internal ScrollBars ScrollBars</c> property: PlayerInterface.cs:75.</summary>
    [Member(Part.WindowsPressTaken, "PlayerInterface.ScrollBars", "CAP 7.0; task 4.5b review (a right press the game took)")]
    internal static readonly PropertyInfo? ScrollBarsProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "ScrollBars");

    /// <summary><c>internal (string Screen, float Grab)? ScrollDrag</c> field: ScrollBars.cs:30 (a scroll bar being dragged).</summary>
    [Member(Part.WindowsPressTaken, "ScrollBars.ScrollDrag", "CAP 7.0; task 4.5b review (a right press the game took)")]
    internal static readonly FieldInfo? ScrollDrag = ScrollBarsProp is null ? null : AccessTools.Field(ScrollBarsProp.PropertyType, "ScrollDrag");

    /// <summary><c>internal Vector2 Mouse</c> field: PlayerInterface.cs:679, the pointer as the interface last saw it (set by MouseMove / MouseDown /
    /// MouseUp, :781, :1011, :1025). Read each frame for a row drag and the settings tip's hover (MyGUI getMousePositionByLayer).</summary>
    [Member(Part.Windows, "PlayerInterface.Mouse", "CAP 7.0; task 4.5b drag and tooltip")]
    internal static readonly FieldInfo? PointerField = UiInterface is null ? null : AccessTools.Field(UiInterface, "Mouse");

    /// <summary><c>internal float StandardPixels</c> property: PlayerInterface.cs:291, the standard text's em pixels at the window's height
    /// (UiPainter.StandardTextPixels, UiPainter.cs:211), the <c>minimumTextPixels</c> the faithful painter measures and draws with (ScreenRouter.cs:76).
    /// Task 4.5b: the Arena window's text measure passes it to UiPainter.MeasureText; logged with the font height (20.833 at 1080 lines live: Kenshi's
    /// standard font is drawn at 30 pt, KenshiFontSize.Effective).</summary>
    [Member(Part.Windows, "PlayerInterface.StandardPixels", "CAP 7.2; task 4.5b font height")]
    internal static readonly PropertyInfo? StandardPixels = UiInterface is null ? null : AccessTools.Property(UiInterface, "StandardPixels");

    /// <summary><c>internal UiPainter? UiPainter</c> field: PlayerInterface.Ui.cs:53 (public class UiPainter, its public MeasureText(fontName,
    /// text, viewportHeight, wrapWidth, minimumTextPixels), UiPainter.Tooltips.cs:71: the size the faithful screens draw a text at; the bindings'
    /// TextHeight calls it with the window's height and StandardPixels, ScreenRouter.cs:76). The Arena window's getTextSize (task 4.5b): heights
    /// and the buttons' natural widths (a width the TextHeight delegate cannot give: one word never wraps, DebugOverlay.WrapText).</summary>
    [Member(Part.Windows, "PlayerInterface.UiPainter", "CAP 7.2; task 4.5b text sizes")]
    internal static readonly FieldInfo? UiPainterField = UiInterface is null ? null : AccessTools.Field(UiInterface, "UiPainter");

    /// <summary><c>public void MouseMove(Vector2 p)</c>: PlayerInterface.cs:1009. The window framework's postfix follows the widget under the pointer
    /// for MyGUI's double-click timer (task 4.4b); TEST ONLY <c>pg window drag</c> (task 4.5b) calls it for the pointer move between the press and the
    /// release of a dragged row (the agent channel's click has none).</summary>
    [Member(Part.WindowsPointerMove, "PlayerInterface.MouseMove(Vector2)", "CAP 7.0; task 4.4b button clicks, task 4.5b pg window drag")]
    internal static readonly MethodInfo? MouseMove = UiInterface is null ? null : AccessTools.Method(UiInterface, "MouseMove", [typeof(System.Numerics.Vector2)]);

    /// <summary>private <c>bool EscapeKey()</c>: PlayerInterface.Ui.cs:1033, the game's Esc handler (closes the top faithful window via
    /// MyGuiScreenHost.Escape, :1078). TEST ONLY <c>pg window escape</c> (task 4.5b): the agent channel has no key command.</summary>
    [Member(Part.TestVerbs, "PlayerInterface.EscapeKey()", "CAP 7.0; task 4.5b pg window escape")]
    internal static readonly MethodInfo? EscapeKey = UiInterface is null ? null : AccessTools.Method(UiInterface, "EscapeKey", Type.EmptyTypes);

    /// <summary><c>public int Width, Height</c> fields: PlayerInterface.cs:241, the game window in pixels (the faithful screens arrange on it,
    /// PlayerInterface.Ui.cs:531). The Arena window's size and compact mode (MyGUI RenderManager::getViewSize, ArenaUI.cpp:2516).</summary>
    [Member(Part.Windows, "PlayerInterface.Width", "CAP 7.2; task 4.5b view size")]
    internal static readonly FieldInfo? UiWidth = UiInterface is null ? null : AccessTools.Field(UiInterface, "Width");
    /// <summary>See <see cref="UiWidth"/>.</summary>
    [Member(Part.Windows, "PlayerInterface.Height", "CAP 7.2; task 4.5b view size")]
    internal static readonly FieldInfo? UiHeight = UiInterface is null ? null : AccessTools.Field(UiInterface, "Height");

    [Member(Part.Portraits, "Meitou.Game.Ui.TradeWindows", "CAP 7.4; spike S7")]
    static readonly Type? TradeWindows = AccessTools.TypeByName("Meitou.Game.Ui.TradeWindows");
    [Member(Part.Portraits, "Meitou.Game.Ui.PortraitBar", "CAP 7.4; spike S7")]
    static readonly Type? PortraitBar = AccessTools.TypeByName("Meitou.Game.Ui.PortraitBar");

    /// <summary><c>public IReadOnlyList&lt;(CharacterId Character, int Cell)&gt; CharacterTradePortraits()</c>: src/Meitou.Game/Ui/TradeWindows.cs:594;
    /// every pair becomes a portrait request in private GameHost.FillPortraits (GameHost.Characters.cs:294-318) while the interface is visible.
    /// Postfix: append the cells our open windows need (spike S7, VERIFIED live).</summary>
    [Member(Part.Portraits, "TradeWindows.CharacterTradePortraits()", "CAP 7.4; spike S7")]
    internal static readonly MethodInfo? CharacterTradePortraits = TradeWindows is null ? null : AccessTools.Method(TradeWindows, "CharacterTradePortraits", Type.EmptyTypes);

    /// <summary><c>internal PortraitBar PortraitBar</c> property on PlayerInterface: PlayerInterface.cs:131.</summary>
    [Member(Part.Portraits, "PlayerInterface.PortraitBar", "CAP 7.4; spike S7")]
    internal static readonly PropertyInfo? PortraitBarProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "PortraitBar");

    /// <summary><c>internal string PortraitRect(int cell)</c>: PortraitBar.cs:83 (the ImageRect of a cell at the renderer's portrait size; S7 live:
    /// use it, a computed rect showed the wrong part of the cell).</summary>
    [Member(Part.Portraits, "PortraitBar.PortraitRect(int)", "CAP 7.4; spike S7")]
    internal static readonly MethodInfo? PortraitRect = PortraitBar is null ? null : AccessTools.Method(PortraitBar, "PortraitRect", [typeof(int)]);

    /// <summary><c>public int PortraitSize { get; set; }</c>: PortraitBar.cs:49 (part of PortraitRect, :80-85); the host resets it every frame from
    /// the look (128 faithful / 256 enhanced, GameHost.cs:1473-1479, MetalGameLoop.cs:2581): the rect cache is keyed on it.</summary>
    [Member(Part.Portraits, "PortraitBar.PortraitSize", "CAP 7.4; spike S7")]
    internal static readonly PropertyInfo? PortraitSize = PortraitBar is null ? null : AccessTools.Property(PortraitBar, "PortraitSize");

    /// <summary><c>public IReadOnlyList&lt;CharacterSnapshot&gt; PortraitMembers()</c>: PortraitBar.cs:127 (the squad bar's, and the squads screen's
    /// while open, cells 0..Count-1; TradeWindows.cs:600 starts after them).</summary>
    [Member(Part.Portraits, "PortraitBar.PortraitMembers()", "CAP 7.4; spike S7")]
    internal static readonly MethodInfo? PortraitMembers = PortraitBar is null ? null : AccessTools.Method(PortraitBar, "PortraitMembers", Type.EmptyTypes);

    /// <summary><c>public (CharacterId Npc, CharacterId Player)? DialoguePortraitCharacters()</c>: PortraitBar.cs:146; non-null while a conversation
    /// shows its two portraits in cells 30 and 31 (GameHost.Characters.cs:309-317).</summary>
    [Member(Part.Portraits, "PortraitBar.DialoguePortraitCharacters()", "CAP 7.4; spike S7")]
    internal static readonly MethodInfo? DialoguePortraitCharacters = PortraitBar is null ? null : AccessTools.Method(PortraitBar, "DialoguePortraitCharacters", Type.EmptyTypes);

    /// <summary><c>internal const int PortraitCells</c> (32: 16 x 2 cells, PortraitBar.cs:29) and <c>public const int DialogueNpcCell</c>
    /// (PortraitCells - 2, :39): the end of the free cells without / with a conversation (TradeWindows.cs:601).</summary>
    [Member(Part.Portraits, "PortraitBar.PortraitCells", "CAP 7.4; spike S7")]
    internal static readonly FieldInfo? PortraitCellsConst = PortraitBar is null ? null : AccessTools.Field(PortraitBar, "PortraitCells");
    [Member(Part.Portraits, "PortraitBar.DialogueNpcCell", "CAP 7.4; spike S7")]
    internal static readonly FieldInfo? DialogueNpcCellConst = PortraitBar is null ? null : AccessTools.Field(PortraitBar, "DialogueNpcCell");

    // ---------- task 4.2: the right click on our buildings (spike S3 kept for real), the "use" pointer, the orders (CAP 5, 6) ----------

    /// <summary>private <c>void PickedRightDown(Vector2 p, bool shift, bool alt)</c>: PlayerInterface.cs:870, the right press on what is under the
    /// cursor (reached from MouseDown -> WorldRightDown only when the press is not over the interface, :797, :863). Prefix (spike S3, VERIFIED live).</summary>
    [Member(Part.Interact, "PlayerInterface.PickedRightDown(Vector2, bool, bool)", "spike S3; CAP 6")]
    internal static readonly MethodInfo? PickedRightDown = UiInterface is null ? null : AccessTools.Method(UiInterface, "PickedRightDown",
        [typeof(System.Numerics.Vector2), typeof(bool), typeof(bool)]);

    /// <summary>internal <c>BuildMode Build</c> property: PlayerInterface.cs:111.</summary>
    [Member(Part.Interact, "PlayerInterface.Build", "spike S3; CAP 6")]
    internal static readonly PropertyInfo? BuildProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "Build");

    /// <summary>internal <c>BuildingSnapshot? PickSite(Vector2 pixel)</c>: BuildMode.cs:1014 (the site under the pixel: Id, Record, Complete,
    /// LevelPlacement, Destroyed; BuildingSnapshot is public, ConstructionSnapshot.cs:15).</summary>
    [Member(Part.Interact, "BuildMode.PickSite(Vector2)", "spike S3; CAP 6")]
    internal static readonly MethodInfo? PickSite = BuildProp is null ? null : AccessTools.Method(BuildProp.PropertyType, "PickSite", [typeof(System.Numerics.Vector2)]);

    /// <summary>internal <c>WorldPicking Picking</c> property: PlayerInterface.cs:95.</summary>
    [Member(Part.Interact, "PlayerInterface.Picking", "spike S3; CAP 6")]
    internal static readonly PropertyInfo? PickingProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "Picking");

    /// <summary>internal <c>(Picked Kind, float T) PickAt(Vector2 p)</c>: WorldPicking.cs:296 (the nearest pick; enum <c>Picked</c> :231 with None,
    /// Site). Read only to leave a character, item, container ... in front of the building its own click (PlayerInterface.cs:874-885).</summary>
    [Member(Part.Interact, "WorldPicking.PickAt(Vector2)", "spike S3; CAP 6")]
    internal static readonly MethodInfo? PickAt = PickingProp is null ? null : AccessTools.Method(PickingProp.PropertyType, "PickAt", [typeof(System.Numerics.Vector2)]);

    // task 7.6: the right press on the Town Registry (a level building: a town's static placement, never a player site; CAP 6 option B)

    /// <summary>internal <c>BuildingSelection BuildingSelection</c> property: PlayerInterface.cs:115.</summary>
    [Member(Part.Interact, "PlayerInterface.BuildingSelection", "CAP 6 option B; task 7.6")]
    internal static readonly PropertyInfo? BuildingSelectionProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "BuildingSelection");

    /// <summary>internal <c>SelectedBuilding? PickBuilding(Vector2 pixel)</c>: BuildingSelection.cs:104 (the nearest building under the pixel, a site of
    /// the player's or a level-data one, :108-170: a level building's <c>Instance</c> is its placement key, <c>Record</c> its BUILDING id, <c>SiteId</c>
    /// null).</summary>
    [Member(Part.Interact, "BuildingSelection.PickBuilding(Vector2)", "CAP 6 option B; task 7.6")]
    internal static readonly MethodInfo? PickBuilding = BuildingSelectionProp is null ? null
        : AccessTools.Method(BuildingSelectionProp.PropertyType, "PickBuilding", [typeof(System.Numerics.Vector2)]);

    /// <summary>The nested record <c>SelectedBuilding</c>'s <c>Instance</c>, <c>Record</c>, <c>SiteId</c> and <c>Destroyed</c> (BuildingSelection.cs:22-23).</summary>
    [Member(Part.Interact, "BuildingSelection.SelectedBuilding.Instance", "CAP 6 option B; task 7.6")]
    internal static readonly PropertyInfo? SelectedBuildingInstance = PickBuilding is null ? null : AccessTools.Property(PickBuilding.ReturnType, "Instance");
    [Member(Part.Interact, "BuildingSelection.SelectedBuilding.Record", "CAP 6 option B; task 7.6")]
    internal static readonly PropertyInfo? SelectedBuildingRecord = PickBuilding is null ? null : AccessTools.Property(PickBuilding.ReturnType, "Record");
    [Member(Part.Interact, "BuildingSelection.SelectedBuilding.SiteId", "CAP 6 option B; task 7.6")]
    internal static readonly PropertyInfo? SelectedBuildingSiteId = PickBuilding is null ? null : AccessTools.Property(PickBuilding.ReturnType, "SiteId");
    [Member(Part.Interact, "BuildingSelection.SelectedBuilding.Destroyed", "CAP 6 option B; task 7.6")]
    internal static readonly PropertyInfo? SelectedBuildingDestroyed = PickBuilding is null ? null : AccessTools.Property(PickBuilding.ReturnType, "Destroyed");

    /// <summary>internal <c>FurnitureUse Furniture</c> property: PlayerInterface.cs:119 (task 7.6 review: the seat in front of a town building).</summary>
    [Member(Part.Interact, "PlayerInterface.Furniture", "CAP 6; task 7.6")]
    internal static readonly PropertyInfo? FurnitureProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "Furniture");

    /// <summary>internal <c>SeatSpot? PickSeat(Vector2 pixel)</c>: FurnitureUse.cs:577 (SeatSpot is public, FixtureRegistry.Seats.cs:10: its
    /// <c>Building</c> is the owning building's key, :60).</summary>
    [Member(Part.Interact, "FurnitureUse.PickSeat(Vector2)", "CAP 6; task 7.6")]
    internal static readonly MethodInfo? PickSeat = FurnitureProp is null ? null : AccessTools.Method(FurnitureProp.PropertyType, "PickSeat", [typeof(System.Numerics.Vector2)]);

    /// <summary>internal <c>IReadOnlyList&lt;CharacterId&gt; SelectedIds()</c>: PlayerInterface.cs:1230 (vanilla SitePress needs one, BuildMode.cs:1250).</summary>
    [Member(Part.Interact, "PlayerInterface.SelectedIds()", "spike S3; CAP 6")]
    internal static readonly MethodInfo? SelectedIds = UiInterface is null ? null : AccessTools.Method(UiInterface, "SelectedIds", Type.EmptyTypes);

    /// <summary>public static <c>OrderCheck Give(World world, int slot, OrderEntry order, bool queued, bool show = true)</c>: src/Meitou.Simulation/
    /// Player/OrderQueue.cs:224, serial phases only; every player order reaches it (MovementSystem.GiveMove :357-391, CombatSystem :394,
    /// ProductionSystem :399-470, ...). Prefix: C++ Character::addOrder hook (ContextMenuHooks.cpp:228-258).</summary>
    [Member(Part.Interact, "PlayerOrders.Give(World, int, OrderEntry, bool, bool)", "CAP 5")]
    internal static readonly MethodInfo? OrdersGive = AccessTools.Method(typeof(Meitou.Simulation.Player.PlayerOrders), "Give",
        [typeof(Meitou.Simulation.World), typeof(int), typeof(Meitou.Simulation.Player.OrderEntry), typeof(bool), typeof(bool)]);

    /// <summary>internal <c>void SiteRelease()</c>: BuildMode.cs:1266, the right release of a site press: the quick order (Build on an unfinished
    /// site, Operate / a move on a finished one) unless the held menu already opened (UpdateSite, :1358-1366, clears the press). Prefix: an
    /// unfinished one of ours gives no quick order (C++ addOrder_hook :243-247).</summary>
    [Member(Part.Interact, "BuildMode.SiteRelease()", "spike S3; CAP 6")]
    internal static readonly MethodInfo? SiteRelease = BuildProp is null ? null : AccessTools.Method(BuildProp.PropertyType, "SiteRelease", Type.EmptyTypes);

    /// <summary>private <c>(BuildingSnapshot Site, Vector2 At, long Since)? _sitePending</c>: BuildMode.cs:987 (set by SitePress :1257).</summary>
    [Member(Part.Interact, "BuildMode._sitePending", "spike S3; CAP 6")]
    internal static readonly FieldInfo? SitePending = BuildProp is null ? null : AccessTools.Field(BuildProp.PropertyType, "_sitePending");

    [Member(Part.InteractCursor, "Meitou.Game.Ui.PointerView", "CAP 6")]
    static readonly Type? PointerView = AccessTools.TypeByName("Meitou.Game.Ui.PointerView");

    /// <summary>private <c>(string Name, CharacterId? Target) PickWithSelection()</c>: PointerView.cs:175 (the pointer with characters selected,
    /// "arrow" for nothing; re-picked when the pointer, modifiers or view change, :151-157). Postfix: the "use" pointer (CAP 6, option B).</summary>
    [Member(Part.InteractCursor, "PointerView.PickWithSelection()", "CAP 6")]
    internal static readonly MethodInfo? PickWithSelection = PointerView is null ? null : AccessTools.Method(PointerView, "PickWithSelection", Type.EmptyTypes);

    // ---------- task 4.6b: the Marks HUD and the debug menu's key (src/Ui/MarksHUD.cs, src/Ui/DebugMenu.cs). Meitou 0.17.3 (fa34a1b); CAP 7.1, 7.9, 7.10 ----------

    /// <summary><c>IReadOnlyList&lt;UiControl&gt; _hudControls</c> (private field): PlayerInterface.Ui.cs:420, the HUD's controls as ArrangeHud last
    /// arranged them (:587-596; ArrangeBase calls it just before the overlay contributions, :502-517, so the overlay factory reads this frame's). Hidden
    /// widgets and their children are not in it (MyGuiPresentation.Arrange, UiDocument.cs:114-117). The Marks HUD reads "MoneyAmountText" /
    /// "MoneyLabelText" from it (MarksHUD.cpp:44-45, :66-74 moneyText / moneyLabel). Read only, never patched.</summary>
    [Member(Part.MarksHud, "PlayerInterface._hudControls", "CAP 7.9; task 4.6b money widgets")]
    internal static readonly FieldInfo? HudControls = UiInterface is null ? null : AccessTools.Field(UiInterface, "_hudControls");

    /// <summary><c>public bool UiKey(Key key, bool ctrl = false, bool shift = false)</c>: PlayerInterface.Ui.cs:1011, a key to the interface; GameLoop.cs:873
    /// calls it for every key down the modal panels did not take, before GameKey (once per press: Silk's KeyDown, GameLoop.cs:813-819). Postfix
    /// (QuestLog's hotkey recipe, CAP 7.10): the "Debug menu key" toggles the debug menu when the game left the key unused.</summary>
    [Member(Part.DebugMenuKey, "PlayerInterface.UiKey(Key, bool, bool)", "CAP 7.10; task 4.6b debug menu key")]
    internal static readonly MethodInfo? UiKey = UiInterface is null ? null
        : AccessTools.Method(UiInterface, "UiKey", [typeof(Meitou.Game.Host.Input.Key), typeof(bool), typeof(bool)]);

    /// <summary><c>public bool UiVisible { get; set; }</c>: PlayerInterface.Ui.cs:85 (the interface shown).</summary>
    [Member(Part.DebugMenuKey, "PlayerInterface.UiVisible", "CAP 7.10; task 4.6b debug menu key")]
    internal static readonly PropertyInfo? UiVisible = UiInterface is null ? null : AccessTools.Property(UiInterface, "UiVisible");

    /// <summary><c>public bool UiEditing</c>: PlayerInterface.Ui.cs:1222 (an edit box has the keyboard).</summary>
    [Member(Part.DebugMenuKey, "PlayerInterface.UiEditing", "CAP 7.10; task 4.6b debug menu key")]
    internal static readonly PropertyInfo? UiEditing = UiInterface is null ? null : AccessTools.Property(UiInterface, "UiEditing");

    /// <summary><c>public bool TitleOpen</c>: PlayerInterface.cs:407 (the title screen shows).</summary>
    [Member(Part.DebugMenuKey, "PlayerInterface.TitleOpen", "CAP 7.10; task 4.6b debug menu key")]
    internal static readonly PropertyInfo? TitleOpen = UiInterface is null ? null : AccessTools.Property(UiInterface, "TitleOpen");

    /// <summary><c>public HashSet&lt;Key&gt; ReservedKeys</c>: PlayerInterface.Ui.cs:1001 (the keys the host keeps for itself: the sandbox's letters).</summary>
    [Member(Part.DebugMenuKey, "PlayerInterface.ReservedKeys", "CAP 7.10; task 4.6b debug menu key")]
    internal static readonly PropertyInfo? ReservedKeys = UiInterface is null ? null : AccessTools.Property(UiInterface, "ReservedKeys");

    /// <summary><c>internal GameHotkeys GameHotkeys</c>: PlayerInterface.cs:143 (the interface's game-action hotkeys part).</summary>
    [Member(Part.DebugMenuKey, "PlayerInterface.GameHotkeys", "CAP 7.10; task 4.6b debug menu key")]
    internal static readonly PropertyInfo? GameHotkeysProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "GameHotkeys");

    /// <summary><c>public bool IsHotkey(Key key)</c> on the internal sealed GameHotkeys: GameHotkeys.cs:53 (a key the player bound to a game action).</summary>
    [Member(Part.DebugMenuKey, "GameHotkeys.IsHotkey(Key)", "CAP 7.10; task 4.6b debug menu key")]
    internal static readonly MethodInfo? IsHotkey = GameHotkeysProp is null ? null
        : AccessTools.Method(GameHotkeysProp.PropertyType, "IsHotkey", [typeof(Meitou.Game.Host.Input.Key)]);

    // ---------- end task 4.6b ----------

    // ---------- task 5.3a: Null's trade opens the Rewards window (spike S4, VERIFIED live 2026-11-11; CAP 17) ----------

    /// <summary>private <c>bool ConversationEffect(World world, Conversation c, DialogueEffect effect, CharacterId me, CharacterCold? mc,
    /// CharacterId target)</c>: DialogueSystem.Effects.cs:150 (DialogueActions.Trade: <c>c.Ended = true</c> and a deferred
    /// <c>OpenTrade(target, me)</c>, :155-159).</summary>
    [Member(Part.RewardsTrade, "DialogueSystem.ConversationEffect", "spike S4; CAP 17; task 5.3a")]
    internal static readonly MethodInfo? ConversationEffect =
        AccessTools.Method(typeof(Meitou.Simulation.Dialogue.DialogueSystem), "ConversationEffect");

    /// <summary>public <c>bool Ended</c> of the private nested <c>DialogueSystem.Conversation</c>: DialogueSystem.cs:338.</summary>
    [Member(Part.RewardsTrade, "DialogueSystem.Conversation.Ended", "spike S4; CAP 17; task 5.3a")]
    internal static readonly FieldInfo? ConversationEnded =
        AccessTools.Inner(typeof(Meitou.Simulation.Dialogue.DialogueSystem), "Conversation") is { } cv ? AccessTools.Field(cv, "Ended") : null;

    /// <summary>The enum value <c>DialogueActions.Trade</c> (Meitou.Data, DialogueActions; DA_TRADE = 1): the prefix compares
    /// <c>effect.Action</c> with it (compiled in; listed so the check reports it).</summary>
    [Member(Part.RewardsTrade, "DialogueActions.Trade", "spike S4; CAP 17; task 5.3a")]
    internal static readonly FieldInfo? DialogueTrade = typeof(Meitou.Data.Gameplay.Dialogue.DialogueActions).GetField("Trade");

    // ---------- end task 5.3a ----------

    // ---------- task 5.3b: the Rewards window (window half) ----------

    /// <summary><c>public bool Wheel(Vector2 p, float steps)</c> of the public MyGuiScreenHost: MyGuiScreenHost.cs:602 (the wheel over the faithful
    /// screens, PlayerInterface.Ui.cs:1201; MyGuiScreenHost.Widgets.cs:446 WheelWidget scrolls the innermost scroll view 50 px). Prefix: the wheel
    /// over a Rewards widget the C++ binds (BindWheel :196-206) scrolls its canvas three body heights (OnWheel :181-195).</summary>
    [Member(Part.WindowsWheel, "MyGuiScreenHost.Wheel(Vector2, float)", "CAP 7.5; task 5.3b")]
    internal static readonly MethodInfo? ScreenHostWheel =
        AccessTools.Method(typeof(Meitou.Game.Ui.MyGuiScreenHost), "Wheel", [typeof(System.Numerics.Vector2), typeof(float)]);

    /// <summary><c>internal ItemCells ItemCells</c> property of the internal PlayerInterface: PlayerInterface.cs:79 (the inventory, trader and
    /// machine grids' item cells).</summary>
    [Member(Part.RewardsIcons, "PlayerInterface.ItemCells", "CAP 7.3; task 5.3b")]
    internal static readonly PropertyInfo? ItemCellsProp = UiInterface is null ? null : AccessTools.Property(UiInterface, "ItemCells");

    [Member(Part.RewardsIcons, "Meitou.Game.Ui.ItemCells", "CAP 7.3; task 5.3b")]
    static readonly Type? ItemCellsType = AccessTools.TypeByName("Meitou.Game.Ui.ItemCells");

    /// <summary><c>internal ItemIcons? ItemIcons</c> field of the internal ItemCells: ItemCells.cs:16 (the public Meitou.Data.Gameplay.ItemIcons:
    /// <c>Choose(record, material, colour)</c> -> a PNG file or a picture drawn from the item's mesh, ItemIcons.cs:64; the item cells show it with
    /// UiPainter.IconReady / IconTexture or RenderedIconReady / RenderedIconTexture, ItemCells.cs:48-55).</summary>
    [Member(Part.RewardsIcons, "ItemCells.ItemIcons", "CAP 7.3; task 5.3b")]
    internal static readonly FieldInfo? ItemIconsField = ItemCellsType is null ? null : AccessTools.Field(ItemCellsType, "ItemIcons");

    // ---------- end task 5.3b ----------

    // ---------- SPIKE members (task 0.7, src/Spikes): none left (S4's moved above in task 5.3a). A new spike member takes Part.Spike, which PgCheck
    // hooks prints without failing the gate (Check/Hooks.cs), so the part is kept. ----------
}
