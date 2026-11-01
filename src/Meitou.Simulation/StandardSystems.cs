using Meitou.Data.Gameplay;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Items;

namespace Meitou.Simulation;

/// <summary>The systems of the game's wiring, for <see cref="StandardSystemOptions.Parts"/>. Listed in the order they run.</summary>
[Flags]
public enum StandardParts
{
    None = 0,
    Population = 1,
    Player = 2,
    Pursuit = 4,
    Movement = 8,
    Body = 16,
    Combat = 32,
    Animation = 64,
    Feed = 128,
    Retaliation = 256,
    /// <summary>First aid and resting (<see cref="Meitou.Simulation.FirstAid.FirstAidSystem"/>).</summary>
    FirstAid = 512,
    /// <summary>Fights started by faction hostility, the leash and standing down (<see cref="AggroSystem"/>); with it the player's assaults and killings change the relations.</summary>
    Aggro = 1024,
    /// <summary>NPC squads running away from a lost fight (<see cref="FleeSystem"/>), by the relations of the data.</summary>
    Flee = 2048,
    /// <summary>Carrying downed characters, the rescue job and looting (<see cref="Meitou.Simulation.Carrying.CarrySystem"/>); with the population, raiders kidnapping the downed too (<see cref="Meitou.Simulation.Kidnap.KidnapSystem"/>).</summary>
    Carry = 4096,
    /// <summary>Crimes and bounties (<see cref="Meitou.Simulation.Crime.CrimeSystem"/>): pending crimes from the player's deeds, witnesses, the police; the arrests, fines and jail (<see cref="Meitou.Simulation.Crime.ArrestSystem"/>) and the lock orders (<see cref="Meitou.Simulation.Locks.LockSystem"/>) come with it.</summary>
    Crime = 8192,
    /// <summary>Shops, prices and trading (<see cref="Meitou.Simulation.Trading.TradeSystem"/>).</summary>
    Trade = 16384,
    /// <summary>The AI packages of the squads and their members' goals (<see cref="Meitou.Simulation.Ai.AiSystem"/>); runs right after the player system.</summary>
    Ai = 32768,
    /// <summary>Player construction (<see cref="Meitou.Simulation.Construction.ConstructionSystem"/>): placing sites, building and dismantling them, the auto-job.</summary>
    Construction = 65536,
    /// <summary>Talking to NPCs: the Talk order and the conversation (<see cref="Meitou.Simulation.Dialogue.DialogueSystem"/>).</summary>
    Dialogue = 131072,
    /// <summary>Production, farms and research of the player's complete buildings (<see cref="Meitou.Simulation.Production.ProductionSystem"/>).</summary>
    Production = 262144,
    /// <summary>What the game runs.</summary>
    All = Population | Player | Pursuit | Movement | Body | Combat | Animation | Feed | Retaliation | FirstAid | Aggro | Flee | Carry | Crime | Trade | Ai | Construction | Dialogue | Production,
}

/// <summary>What <see cref="StandardSystems.Build"/> builds and with which settings; the defaults are the game's.</summary>
public sealed class StandardSystemOptions
{
    /// <summary>The systems to build; a test that needs a few of them asks for those and gets them in the game's relative order.</summary>
    public StandardParts Parts { get; init; } = StandardParts.All;
    public PopulationSettings? Population { get; init; }
    /// <summary>True answers path requests inside the tick (tests); false on the path service's own thread (the interactive game).</summary>
    public bool SynchronousPaths { get; init; }
    /// <summary>Multiplies the time of the body-part and blood rates (<see cref="BodySystem"/>).</summary>
    public float BodyTimeScale { get; init; } = 1;
    /// <summary>The game's: the path service walks attackers to their targets and the body system ticks the medical state.</summary>
    public CombatOptions Combat { get; init; } = new() { SelfApproach = false, TickMedical = false };
    /// <summary>Null: the clip lengths of an empty set (every attack takes its default time).</summary>
    public AnimationLengths? AnimationLengths { get; init; }
    /// <summary>Null: built from the database of the data.</summary>
    public AnimationLibrary? AnimationLibrary { get; init; }
    /// <summary>Null: the animation system's default; the game passes the CONSTANTS value.</summary>
    public float? AnimationBlendRate { get; init; }
    public FeedSettings? Feed { get; init; }
    public Meitou.Simulation.Dialogue.DialogueSettings? Dialogue { get; init; }
    public Meitou.Simulation.FirstAid.FirstAidSettings? FirstAid { get; init; }
    public AggroSettings? Aggro { get; init; }
    public Meitou.Simulation.Carrying.CarrySettings? Carry { get; init; }
    public Meitou.Simulation.Crime.CrimeSettings? Crime { get; init; }
    public Meitou.Simulation.Crime.ArrestSettings? Arrest { get; init; }
    /// <summary>Meitou (on; Faithful off): doors walkers opened close behind them (<see cref="Meitou.Simulation.Locks.LockSystem.DoorsCloseBehind"/>).</summary>
    public bool DoorsCloseBehind { get; init; } = true;
    public Meitou.Simulation.Kidnap.KidnapSettings? Kidnap { get; init; }
    public Meitou.Simulation.Kidnap.CaptiveSettings? Captives { get; init; }
    public Meitou.Simulation.Trading.TradeSettings? Trade { get; init; }
    public Meitou.Simulation.Ai.AiSettings? Ai { get; init; }
    /// <summary>Squads in a fight run when they are not intact (<see cref="FleeSystem.SquadBreak"/>; off, as the game does).</summary>
    public bool SquadBreak { get; init; }
    public Meitou.Simulation.Construction.ConstructionSettings? Construction { get; init; }
    public Meitou.Simulation.Production.ProductionSettings? Production { get; init; }
    /// <summary>The rigs the characters are drawn with: given, the physics ragdolls run (after the animation); null, the fall and lying clips stand in for them.</summary>
    public Meitou.Data.Characters.CharacterRigs? Ragdolls { get; init; }
    /// <summary>What ragdolls fall onto (null: the walkable ground, no buildings).</summary>
    public Meitou.Simulation.Ragdolls.IRagdollEnvironment? RagdollEnvironment { get; init; }
}

/// <summary>The built systems in tick order, with the ones the host talks to.</summary>
public sealed class StandardSystemSet
{
    public required IReadOnlyList<ITickSystem> Systems { get; init; }
    public PopulationSystem? Population { get; init; }
    public MovementSystem? Movement { get; init; }
    /// <summary>Built when combat, pursuit or retaliation is asked for.</summary>
    public CombatSystem? Combat { get; init; }
    public AggroSystem? Aggro { get; init; }
    public Meitou.Simulation.Carrying.CarrySystem? Carry { get; init; }
    public Meitou.Simulation.Crime.CrimeSystem? Crime { get; init; }
    public Meitou.Simulation.Crime.ArrestSystem? Arrest { get; init; }
    public Meitou.Simulation.Kidnap.KidnapSystem? Kidnap { get; init; }
    /// <summary>What captors do with their captives (kill, eat, enslave, gather, deliver); with the population.</summary>
    public Meitou.Simulation.Kidnap.CaptiveSystem? Captives { get; init; }
    public Meitou.Simulation.Locks.LockSystem? Locks { get; init; }
    /// <summary>Built with first aid.</summary>
    public Meitou.Simulation.Locks.BedSystem? Beds { get; init; }
    public Meitou.Simulation.Trading.TradeSystem? Trade { get; init; }
    public Meitou.Simulation.Ai.AiSystem? Ai { get; init; }
    public Meitou.Simulation.Construction.ConstructionSystem? Construction { get; init; }
    public Meitou.Simulation.Production.ProductionSystem? Production { get; init; }
    public Meitou.Simulation.Ragdolls.RagdollSystem? Ragdolls { get; init; }
    public Meitou.Simulation.Dialogue.DialogueSystem? Dialogue { get; init; }
}

/// <summary>
/// The system list the game runs, in the order it needs, built in one place so the game and the tests that mean "the game's wiring" cannot
/// drift apart. Systems of the same phase run in list order (<c>Inputs</c>, <c>Schedule</c>, <c>Apply</c> and <c>SlowWorld</c> serially, the
/// parallel phases one system after the other, and an effect's order of commit by the system's index), and the order is part of the
/// simulation's result (the golden hashes pin it). The rules:
/// <list type="number">
/// <item><b>Population first</b>: it takes the focus command in <c>Inputs</c> and loads, spawns and unloads in <c>SlowWorld</c>, so every system after it sees the characters it made.
/// <b>Player before movement</b>: the player system applies <c>SelectCommand</c>/<c>StopCommand</c> in <c>Inputs</c> and the movement system's <c>MoveOrder</c> in the same step reads the selection that results.</item>
/// <item><b>AI after the player, before pursuit and movement</b>: its <c>Think</c> (a character's goal, written into its own task and path flags) runs before the movement
/// system's, which follows the task the same tick; its <c>SlowWorld</c> sees the squads the population made this tick and gives them their blackboards. It adds nothing
/// to the hash until a squad has AI packages.</item>
/// <item><b>Pursuit before movement</b>: pursuit raises <c>NeedPath</c> in its <c>Schedule</c> and the movement system answers the same tick.</item>
/// <item><b>Body before combat</b>: the body system ticks the medical state (<c>TickMedical</c> is off in the combat system) and the combat
/// system reads its result in the same tick; with it on, combat does that itself.</item>
/// <item><b>Combat before animation and retaliation</b>: the combat system swaps its read and write arrays at the end of its <c>SlowWorld</c>,
/// so a system placed after it sees the new tick's combat state and one placed before it the old one. Retaliation reads the combat state of the tick that just ended
/// (animation is placed after combat in the game's order, and the golden hashes pin it). Moving the swap to a separate end-of-tick hook would change what retaliation reads.</item>
/// <item><b>Carry after body, before combat</b>: its <c>SlowWorld</c> sees the knock-outs of this tick's medical step and the corpses the body system removed, so a carrier that went down
/// drops its load in the same tick; its <c>Inputs</c> come before combat's, so a carrier ordered to attack lets go of the body first. Everything it does is serial.</item>
/// <item><b>Kidnap right after carry</b>: it lifts with the carry system and cages with the fixtures, in the same serial step order; it adds nothing to the hash until a kidnapping runs.</item>
/// <item><b>Beds after carry</b> (with first aid): a carrier lets go of the body it lays in a bed with the carry system's links; its <c>Inputs</c> come after movement's, so a move order has got a
/// sleeper up already, and its <c>SlowWorld</c> after the body system's knock-outs. It adds nothing to the hash until a bed trip runs or a rent is paid.</item>
/// <item><b>Construction after carry, before combat</b>: its walks and work (all serial) see the characters the body and carry systems left this tick, and a builder ordered to attack lets go of its site before combat's <c>Inputs</c> run; it adds nothing to the hash until the player has a site.</item>
/// <item><b>Production right after construction</b>: it sees the sites completed this tick and makes their machines; it adds nothing to the hash until the player has a working building.</item>
/// <item><b>Feed after body</b>: the game's order; eating reads hunger the body system advanced in the same tick.</item>
/// <item><b>Retaliation, then aggro, last</b>: both defer <c>AttackOrder</c>s for the next tick (<see cref="World.Defer"/>) after combat has swapped its arrays; aggro's
/// leash sweep sees the tasks every other system left, and its look (<c>Act</c>) runs after combat's, reading only the last tick's positions.
/// <b>Flee after aggro</b>: a squad that breaks sends its runners off after every fight of the tick was ordered, and its stop orders are deferred like theirs.</item>
/// <item><b>Crime last</b>: its witness sweep (<c>SlowWorld</c>) sees the positions, carry links and pending crimes every other system left this tick; the deeds are reported to it by
/// combat (<c>Inputs</c>, commit, the bolt flight in <c>Schedule</c>) and carrying (<c>SlowWorld</c>), all serial. It adds nothing to the hash until a crime is pending. The arrest system
/// right after it acts on the bookings and sightings of the same tick, then the lock system (its picks book crimes the next sweep sees).</item>
/// <item><b>Trade after crime</b>: a caught fence is reported to the crime system from the trade's <c>Inputs</c>, and the shops are opened and restocked in <c>SlowWorld</c> after the
/// population made the tick's squads. It adds nothing to the hash until a squad has a shop.</item>
/// </list>
/// An extra system a test wants (a probe, a poke) goes after these.
/// </summary>
public static class StandardSystems
{
    public static StandardSystemSet Build(PopulationData data, IWalkability walkability, StandardSystemOptions? options = null)
    {
        var o = options ?? new StandardSystemOptions();
        bool Has(StandardParts p) => (o.Parts & p) != 0;
        var systems = new List<ITickSystem>();
        PopulationSystem? population = null;
        MovementSystem? movement = null;
        CombatSystem? combat = null;
        if (Has(StandardParts.Population)) systems.Add(population = new PopulationSystem(data, o.Population));
        if (Has(StandardParts.Player)) systems.Add(new PlayerSystem(data));
        Meitou.Simulation.Ai.AiSystem? ai = null;
        if (Has(StandardParts.Ai)) systems.Add(ai = new Meitou.Simulation.Ai.AiSystem(data, o.Ai));
        if (Has(StandardParts.Pursuit | StandardParts.Combat | StandardParts.Retaliation | StandardParts.Flee))
            combat = new CombatSystem(data.Combat.Techniques, data.Combat.Constants, data.Bodies.Constants, data.BodyOptions, o.AnimationLengths ?? new AnimationLengths(), o.Combat);
        // The relation events of deeds (blows, first aid, cagings) go with the live relations, which the aggro part brings.
        var deeds = Has(StandardParts.Aggro) ? new FactionDeeds(data.Relations, data) : null;
        if (combat is not null && Has(StandardParts.Aggro)) (combat.Relations, combat.Deeds) = (data.Relations, deeds);
        if (combat is not null) combat.Buildings = o.RagdollEnvironment;
        if (ai is not null && combat is not null) ai.ThreatSource = combat;
        if (combat is not null) (combat.TurretGuns, combat.Towns) = (new Meitou.Data.Gameplay.Combat.TurretCatalog(data.Db), data);
        if (Has(StandardParts.Pursuit)) systems.Add(new PursuitSystem(combat!));
        if (Has(StandardParts.Movement)) systems.Add(movement = new MovementSystem(new PathService(walkability, o.SynchronousPaths)));
        if (Has(StandardParts.FirstAid)) systems.Add(new FirstAid.FirstAidSystem(data.Bodies.Constants, data.BodyOptions, data.Items, o.FirstAid) { Deeds = deeds, Relations = Has(StandardParts.Aggro) ? data.Relations : null });
        WeatherSystem? weather = null;
        if (Has(StandardParts.Body)) { systems.Add(new BodySystem(data.Bodies.Constants, data.BodyOptions, o.BodyTimeScale, data.Db)); systems.Add(weather = new WeatherSystem(data.Bodies.Constants) { Db = data.Db, Buildings = o.RagdollEnvironment }); }
        Meitou.Simulation.Carrying.CarrySystem? carry = null;
        Meitou.Simulation.Kidnap.KidnapSystem? kidnap = null;
        Meitou.Simulation.Ragdolls.RagdollSystem? ragdolls = null;
        if (Has(StandardParts.Carry))
        {
            // The lift takes the length of the shoulder lift left clip (1 s when the lengths are not known).
            carry = new Meitou.Simulation.Carrying.CarrySystem(data.Bodies.Constants, data.BodyOptions, o.AnimationLengths?.Of(Meitou.Simulation.Carrying.CarrySystem.LiftClip) ?? 1, o.Carry);
            if (Has(StandardParts.Aggro)) carry.Relations = data.Relations;
            carry.Assembly = new CharacterAssembly(data);
            carry.Data = data;
            systems.Add(carry);
            // Raiders lifting the downed and carrying them to a cage of their faction: right after the carry system, whose lift and links it uses; it adds nothing to the hash until one runs.
            if (population is not null) systems.Add(kidnap = new Meitou.Simulation.Kidnap.KidnapSystem(data, population, carry, o.Kidnap) { Deeds = deeds });
            // Town goals of AI squads outside the act-outs (removing downed intruders, caging them, fetching food): after the kidnapping, with the same lift; nothing in the hash until one runs.
            if (population is not null && Has(StandardParts.Ai)) systems.Add(new Meitou.Simulation.Kidnap.TownDutySystem(data, population, carry));
            // The zones' blood smell and the blood hunters (goal 176); nothing in the hash until a corpse has flies or someone bleeds.
            if (Has(StandardParts.Ai)) systems.Add(new SmellSystem(data, population));
        }
        // The captives' fate and the slavers' logistics: right after the kidnapping (serial, like it); nothing in the hash until a job or a town's kill timer exists.
        Meitou.Simulation.Kidnap.CaptiveSystem? captives = null;
        if (population is not null && Has(StandardParts.Ai)) systems.Add(captives = new Meitou.Simulation.Kidnap.CaptiveSystem(data, combat, o.Captives) { Kidnap = kidnap });
        Meitou.Simulation.Locks.BedSystem? beds = null;
        if (Has(StandardParts.FirstAid)) systems.Add(beds = new Meitou.Simulation.Locks.BedSystem(data) { Carry = carry });
        Meitou.Simulation.Construction.ConstructionSystem? construction = null;
        if (Has(StandardParts.Construction)) systems.Add(construction = new Meitou.Simulation.Construction.ConstructionSystem(data, o.Construction));
        if (combat is not null) combat.Construction = construction;
        Meitou.Simulation.Production.ProductionSystem? production = null;
        if (Has(StandardParts.Production)) systems.Add(production = new Meitou.Simulation.Production.ProductionSystem(data, o.Production) { Assembly = new CharacterAssembly(data), Construction = construction, Beds = beds, Carry = carry });
        if (Has(StandardParts.Combat)) systems.Add(combat!);
        if (Has(StandardParts.Animation))
        {
            var library = o.AnimationLibrary ?? AnimationLibrary.FromDatabase(data.Db);
            var lengths = o.AnimationLengths ?? new AnimationLengths();
            var animation = new AnimationSystem(library, lengths, o.AnimationBlendRate ?? 4, combat);
            systems.Add(animation);
            // Ragdolls right after the animation: they start from and pin to this tick's pose, and the whole body's position is theirs before anything later reads it.
            if (o.Ragdolls is not null) systems.Add(ragdolls = new Meitou.Simulation.Ragdolls.RagdollSystem(o.Ragdolls, o.RagdollEnvironment, data.BodyOptions));
            animation.Ragdolls = ragdolls;
            // The stealth knock-out swing lasts the length of the knockout record's clip.
            if (carry is not null && library.IndexOf(Meitou.Simulation.Carrying.CarrySystem.KnockoutClip) is >= 0 and var ko) carry.KnockoutSeconds = lengths.Of(library.Definitions[ko].Clip);
            if (carry is not null && library.IndexOf(Meitou.Simulation.Carrying.CarrySystem.LiftClip) is >= 0 and var lift && lengths.Of(library.Definitions[lift].Clip) is > 0 and var liftLength)
            {
                carry.LiftPlaySpeed = library.Definitions[lift].PlaySpeed > 0 ? library.Definitions[lift].PlaySpeed : 1;
                carry.LiftSeconds = liftLength / carry.LiftPlaySpeed;
            }
            if (captives is not null && library.IndexOf(Meitou.Simulation.Kidnap.CaptiveSystem.ChopClip) is >= 0 and var chop) captives.ChopSeconds = lengths.Of(library.Definitions[chop].Clip);
        }
        if (Has(StandardParts.Feed)) systems.Add(new FeedSystem(data.Items, o.Feed));
        if (Has(StandardParts.Retaliation)) systems.Add(new RetaliationSystem(combat!) { Towns = data });
        AggroSystem? aggro = null;
        if (Has(StandardParts.Aggro)) systems.Add(aggro = new AggroSystem(data.Relations, o.Aggro) { Combat = combat, Towns = data, Population = population });
        // Without the Aggro part a fight still reads the senses (target choice, threats, the fight's end): the look keeps them, and nothing else.
        else if (combat is not null) systems.Add(new AggroSystem(data.Relations, (o.Aggro ?? new AggroSettings()) with { AttackOnSight = false }) { Combat = combat, Towns = data, Population = population });
        if (carry is not null) carry.Aggro = aggro;
        if (Has(StandardParts.Flee)) systems.Add(new FleeSystem(data.Relations, combat!, o.Aggro?.SightRange ?? AggroSystem.OutdoorSight) { SquadBreak = o.SquadBreak });
        Meitou.Simulation.Crime.CrimeSystem? crime = null;
        Meitou.Simulation.Crime.ArrestSystem? arrest = null;
        Meitou.Simulation.Locks.LockSystem? locks = null;
        if (Has(StandardParts.Crime))
        {
            // Last: its sweep sees every position, task and carry link the tick left; the deeds come in from combat and carrying in their own serial steps.
            systems.Add(crime = new Meitou.Simulation.Crime.CrimeSystem(data.Relations, o.Crime) { Xp = new Meitou.Simulation.Bodies.XpService(data.Bodies.Constants, data.BodyOptions) });
            if (combat is not null) combat.Crimes = crime;
            if (carry is not null) carry.Crimes = crime;
            if (ai is not null) ai.Crimes = crime;
            // Right after it: the police act on this tick's bookings and sightings; the lock orders (picking, getting out) report their crimes to it.
            systems.Add(arrest = new Meitou.Simulation.Crime.ArrestSystem(crime, data.Bodies.Constants, o.Arrest) { Carry = carry, Deeds = deeds, Towns = data });
            systems.Add(locks = new Meitou.Simulation.Locks.LockSystem(data.Bodies.Constants, data.BodyOptions) { Crimes = crime, DoorsCloseBehind = o.DoorsCloseBehind, Assembly = new CharacterAssembly(data), Combat = combat, CombatConstants = data.Combat.Constants });
            if (combat is not null) combat.Locks = locks;
        }
        Meitou.Simulation.Trading.TradeSystem? trade = null;
        if (Has(StandardParts.Trade))
        {
            systems.Add(trade = new Meitou.Simulation.Trading.TradeSystem(data, o.Trade));
            trade.Crimes = crime;
        }
        Meitou.Simulation.Dialogue.DialogueSystem? talk = null;
        if (Has(StandardParts.Dialogue)) systems.Add(talk = new Meitou.Simulation.Dialogue.DialogueSystem(data, o.Dialogue, population));
        if (deeds is not null && talk is not null) (deeds.CagedHook, deeds.Memory) = (talk.Captured, talk.Memory);   // a captive's squad remembers its captor (MY_CAPTOR), blows their tags
        if (talk is not null && population is not null) (population.Announcer, population.PendingTalk) = (talk.Announce, talk.HasInterrupt);
        if (talk is not null) talk.Arrests = arrest;
        if (weather is not null) weather.Talk = talk;
        if (talk is not null && aggro is not null) (aggro.Memory, talk.Aggro) = (talk.Memory, aggro);
        if (talk is not null && population is not null) population.Memory = talk.Memory;
        if (arrest is not null) arrest.Talk = talk;
        return new StandardSystemSet { Systems = systems, Population = population, Movement = movement, Combat = combat, Aggro = aggro, Carry = carry, Kidnap = kidnap, Captives = captives, Crime = crime, Arrest = arrest, Locks = locks, Beds = beds, Trade = trade, Ai = ai, Construction = construction, Production = production, Ragdolls = ragdolls, Dialogue = talk };
    }
}
