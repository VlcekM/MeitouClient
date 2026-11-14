using Meitou.Data.Gameplay;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation;

/// <summary>
/// The systems of the game's wiring, for <see cref="StandardSystemOptions.Parts"/>. Listed in the order they run.
/// </summary>
[Flags]
public enum StandardParts
{
    /// <summary>
    /// No system.
    /// </summary>
    None = 0,
    /// <summary>
    /// The population (<see cref="PopulationSystem"/>).
    /// </summary>
    Population = 1,
    /// <summary>
    /// The player's commands (<see cref="PlayerSystem"/>).
    /// </summary>
    Player = 2,
    /// <summary>
    /// Attackers closing in on their targets (<see cref="PursuitSystem"/>).
    /// </summary>
    Pursuit = 4,
    /// <summary>
    /// Movement and the path service (<see cref="MovementSystem"/>).
    /// </summary>
    Movement = 8,
    /// <summary>
    /// The body and the weather (<see cref="BodySystem"/>, <see cref="WeatherSystem"/>).
    /// </summary>
    Body = 16,
    /// <summary>
    /// Combat (<see cref="CombatSystem"/>).
    /// </summary>
    Combat = 32,
    /// <summary>
    /// Animation (<see cref="AnimationSystem"/>).
    /// </summary>
    Animation = 64,
    /// <summary>
    /// Eating (<see cref="FeedSystem"/>).
    /// </summary>
    Feed = 128,
    /// <summary>
    /// Striking back (<see cref="RetaliationSystem"/>).
    /// </summary>
    Retaliation = 256,
    /// <summary>
    /// First aid and resting (<see cref="Meitou.Simulation.FirstAid.FirstAidSystem"/>).
    /// </summary>
    FirstAid = 512,
    /// <summary>
    /// Fights started by faction hostility, the leash and standing down (<see cref="AggroSystem"/>); with it the player's assaults and killings
    /// change the relations.
    /// </summary>
    Aggro = 1024,
    /// <summary>
    /// NPC squads running away from a lost fight (<see cref="FleeSystem"/>), by the relations of the data.
    /// </summary>
    Flee = 2048,
    /// <summary>
    /// Carrying downed characters, the rescue job and looting (<see cref="Meitou.Simulation.Carrying.CarrySystem"/>); with the population, raiders
    /// kidnapping the downed too (<see cref="Meitou.Simulation.Kidnap.KidnapSystem"/>).
    /// </summary>
    Carry = 4096,
    /// <summary>
    /// Crimes and bounties (<see cref="Meitou.Simulation.Crime.CrimeSystem"/>): pending crimes from the player's deeds, witnesses, the police; the
    /// arrests, fines and jail (<see cref="Meitou.Simulation.Crime.ArrestSystem"/>) and the lock orders
    /// (<see cref="Meitou.Simulation.Locks.LockSystem"/>) come with it.
    /// </summary>
    Crime = 8192,
    /// <summary>
    /// Shops, prices and trading (<see cref="Meitou.Simulation.Trading.TradeSystem"/>).
    /// </summary>
    Trade = 16384,
    /// <summary>
    /// The AI packages of the squads and their members' goals (<see cref="Meitou.Simulation.Ai.AiSystem"/>); runs right after the player system.
    /// </summary>
    Ai = 32768,
    /// <summary>
    /// Player construction (<see cref="Meitou.Simulation.Construction.ConstructionSystem"/>): placing sites, building and dismantling them, the
    /// auto-job.
    /// </summary>
    Construction = 65536,
    /// <summary>
    /// Talking to NPCs: the Talk order and the conversation (<see cref="Meitou.Simulation.Dialogue.DialogueSystem"/>).
    /// </summary>
    Dialogue = 131072,
    /// <summary>
    /// Production, farms and research of the player's complete buildings (<see cref="Meitou.Simulation.Production.ProductionSystem"/>).
    /// </summary>
    Production = 262144,
    /// <summary>
    /// What the game runs.
    /// </summary>
    All = Population | Player | Pursuit | Movement | Body | Combat | Animation | Feed | Retaliation | FirstAid | Aggro | Flee | Carry | Crime | Trade
        | Ai | Construction | Dialogue | Production,
}

/// <summary>
/// What <see cref="StandardSystems.Build"/> builds and with which settings; the defaults are the game's.
/// </summary>
public sealed class StandardSystemOptions
{
    /// <summary>
    /// The systems to build; a test that needs a few of them asks for those and gets them in the game's relative order.
    /// </summary>
    public StandardParts Parts { get; init; } = StandardParts.All;
    /// <summary>
    /// The population settings (null: the system's default).
    /// </summary>
    public PopulationSettings? Population { get; init; }
    /// <summary>
    /// True answers path requests inside the tick (tests); false on the path service's own thread (the interactive game).
    /// </summary>
    public bool SynchronousPaths { get; init; }
    /// <summary>
    /// Multiplies the time of the body-part and blood rates (<see cref="BodySystem"/>).
    /// </summary>
    public float BodyTimeScale { get; init; } = 1;
    /// <summary>
    /// The game's: the path service walks attackers to their targets and the body system ticks the medical state.
    /// </summary>
    public CombatOptions Combat { get; init; } = new() { SelfApproach = false, TickMedical = false };
    /// <summary>
    /// Null: the clip lengths of an empty set (every attack takes its default time).
    /// </summary>
    public AnimationLengths? AnimationLengths { get; init; }
    /// <summary>
    /// Null: built from the database of the data.
    /// </summary>
    public AnimationLibrary? AnimationLibrary { get; init; }
    /// <summary>
    /// Null: the animation system's default; the game passes the CONSTANTS value.
    /// </summary>
    public float? AnimationBlendRate { get; init; }
    /// <summary>
    /// The feeding settings (null: the system's default).
    /// </summary>
    public FeedSettings? Feed { get; init; }
    /// <summary>
    /// The dialogue settings (null: the system's default).
    /// </summary>
    public Dialogue.DialogueSettings? Dialogue { get; init; }
    /// <summary>
    /// The first aid settings (null: the system's default).
    /// </summary>
    public FirstAid.FirstAidSettings? FirstAid { get; init; }
    /// <summary>
    /// The aggro settings (null: the system's default).
    /// </summary>
    public AggroSettings? Aggro { get; init; }
    /// <summary>
    /// The carrying settings (null: the system's default).
    /// </summary>
    public Carrying.CarrySettings? Carry { get; init; }
    /// <summary>
    /// The crime settings (null: the system's default).
    /// </summary>
    public Crime.CrimeSettings? Crime { get; init; }
    /// <summary>
    /// The arrest settings (null: the system's default).
    /// </summary>
    public Crime.ArrestSettings? Arrest { get; init; }
    /// <summary>
    /// Meitou (on; Faithful off): doors walkers opened close behind them (<see cref="Meitou.Simulation.Locks.LockSystem.DoorsCloseBehind"/>).
    /// </summary>
    public bool DoorsCloseBehind { get; init; } = true;
    /// <summary>
    /// The player's animals pick up loose items into their bags (<see cref="PlayerSystem.AnimalsPickUp"/>; off by default).
    /// </summary>
    public bool AnimalsPickUp { get; init; }
    /// <summary>
    /// The kidnapping settings (null: the system's default).
    /// </summary>
    public Kidnap.KidnapSettings? Kidnap { get; init; }
    /// <summary>
    /// The captive settings (null: the system's default).
    /// </summary>
    public Kidnap.CaptiveSettings? Captives { get; init; }
    /// <summary>
    /// The trade settings (null: the system's default).
    /// </summary>
    public Trading.TradeSettings? Trade { get; init; }
    /// <summary>
    /// The AI settings (null: the system's default).
    /// </summary>
    public Ai.AiSettings? Ai { get; init; }
    /// <summary>
    /// Squads in a fight run when they are not intact (<see cref="FleeSystem.SquadBreak"/>; off, as the game does).
    /// </summary>
    public bool SquadBreak { get; init; }
    /// <summary>
    /// The construction settings (null: the system's default).
    /// </summary>
    public Construction.ConstructionSettings? Construction { get; init; }
    /// <summary>
    /// The production settings (null: the system's default).
    /// </summary>
    public Production.ProductionSettings? Production { get; init; }
    /// <summary>
    /// The rigs the characters are drawn with: given, the physics ragdolls run (after the animation); null, the fall and lying clips stand in for
    /// them.
    /// </summary>
    public Data.Characters.CharacterRigs? Ragdolls { get; init; }
    /// <summary>
    /// What ragdolls fall onto (null: the walkable ground, no buildings).
    /// </summary>
    public Ragdolls.IRagdollEnvironment? RagdollEnvironment { get; init; }
    /// <summary>
    /// What code mods add (docs/modding.md): extra systems spliced in at their anchors, dialogue codes. Null: the standard wiring alone.
    /// </summary>
    public SimulationExtensions? Extensions { get; init; }
}

/// <summary>
/// The built systems in tick order, with the ones the host talks to.
/// </summary>
public sealed class StandardSystemSet
{
    /// <summary>
    /// The systems in tick order.
    /// </summary>
    public required IReadOnlyList<ITickSystem> Systems { get; init; }
    /// <summary>
    /// The population (null: not built).
    /// </summary>
    public PopulationSystem? Population { get; init; }
    /// <summary>
    /// The movement (null: not built).
    /// </summary>
    public MovementSystem? Movement { get; init; }
    /// <summary>
    /// Built when combat, pursuit or retaliation is asked for.
    /// </summary>
    public CombatSystem? Combat { get; init; }
    /// <summary>
    /// The aggro (null: not built).
    /// </summary>
    public AggroSystem? Aggro { get; init; }
    /// <summary>
    /// The carrying (null: not built).
    /// </summary>
    public Carrying.CarrySystem? Carry { get; init; }
    /// <summary>
    /// The crimes (null: not built).
    /// </summary>
    public Crime.CrimeSystem? Crime { get; init; }
    /// <summary>
    /// The arrests (null: not built).
    /// </summary>
    public Crime.ArrestSystem? Arrest { get; init; }
    /// <summary>
    /// The kidnapping (null: not built).
    /// </summary>
    public Kidnap.KidnapSystem? Kidnap { get; init; }
    /// <summary>
    /// What captors do with their captives (kill, eat, enslave, gather, deliver); with the population.
    /// </summary>
    public Kidnap.CaptiveSystem? Captives { get; init; }
    /// <summary>
    /// The locks (null: not built).
    /// </summary>
    public Locks.LockSystem? Locks { get; init; }
    /// <summary>
    /// Built with first aid.
    /// </summary>
    public Locks.BedSystem? Beds { get; init; }
    /// <summary>
    /// The trade (null: not built).
    /// </summary>
    public Trading.TradeSystem? Trade { get; init; }
    /// <summary>
    /// The AI (null: not built).
    /// </summary>
    public Ai.AiSystem? Ai { get; init; }
    /// <summary>
    /// The construction (null: not built).
    /// </summary>
    public Construction.ConstructionSystem? Construction { get; init; }
    /// <summary>
    /// The production (null: not built).
    /// </summary>
    public Production.ProductionSystem? Production { get; init; }
    /// <summary>
    /// The ragdolls (null: not built).
    /// </summary>
    public Ragdolls.RagdollSystem? Ragdolls { get; init; }
    /// <summary>
    /// The dialogue (null: not built).
    /// </summary>
    public Dialogue.DialogueSystem? Dialogue { get; init; }
}

/// <summary>
/// The system list the game runs, in the order it needs, built in one place so the game and the tests that mean "the game's wiring" cannot
/// drift apart. Systems of the same phase run in list order (<c>Inputs</c>, <c>Schedule</c>, <c>Apply</c> and <c>SlowWorld</c> serially, the
/// parallel phases one system after the other, and an effect's order of commit by the system's index), and the order is part of the
/// simulation's result (the golden hashes pin it). The rules:
/// <list type="number">
/// <item><b>Population first</b>: it takes the focus command in <c>Inputs</c> and loads, spawns and unloads in <c>SlowWorld</c>, so every system
/// after it sees the characters it made. <b>Player before movement</b>: the player system applies <c>SelectCommand</c>/<c>StopCommand</c> in
/// <c>Inputs</c> and the movement system's <c>MoveOrder</c> in the same step reads the selection that results.</item>
/// <item><b>AI after the player, before pursuit and movement</b>: its <c>Think</c> (a character's goal, written into its own task and path flags)
/// runs before the movement system's, which follows the task the same tick; its <c>SlowWorld</c> sees the squads the population made this tick and
/// gives them their blackboards. It adds nothing to the hash until a squad has AI packages.</item>
/// <item><b>Pursuit before movement</b>: pursuit raises <c>NeedPath</c> in its <c>Schedule</c> and the movement system answers the same tick.</item>
/// <item><b>Body before combat</b>: the body system ticks the medical state (<c>TickMedical</c> is off in the combat system) and the combat
/// system reads its result in the same tick; with it on, combat does that itself.</item>
/// <item><b>Combat before animation and retaliation</b>: the combat system swaps its read and write arrays at the end of its <c>SlowWorld</c>, so a
/// system placed after it sees the new tick's combat state and one placed before it the old one. Retaliation reads the combat state of the tick that
/// just ended (animation is placed after combat in the game's order, and the golden hashes pin it). Moving the swap to a separate end-of-tick hook
/// would change what retaliation reads.</item>
/// <item><b>Carry after body, before combat</b>: its <c>SlowWorld</c> sees the knock-outs of this tick's medical step and the corpses the body system
/// removed, so a carrier that went down drops its load in the same tick; its <c>Inputs</c> come before combat's, so a carrier ordered to attack lets
/// go of the body first. Everything it does is serial.</item>
/// <item><b>Kidnap right after carry</b>: it lifts with the carry system and cages with the fixtures, in the same serial step order; it adds nothing
/// to the hash until a kidnapping runs.</item>
/// <item><b>Beds after carry</b> (with first aid): a carrier lets go of the body it lays in a bed with the carry system's links; its <c>Inputs</c>
/// come after movement's, so a move order has got a sleeper up already, and its <c>SlowWorld</c> after the body system's knock-outs. It adds nothing
/// to the hash until a bed trip runs or a rent is paid.</item>
/// <item><b>Construction after carry, before combat</b>: its walks and work (all serial) see the characters the body and carry systems left this
/// tick, and a builder ordered to attack lets go of its site before combat's <c>Inputs</c> run; it adds nothing to the hash until the player has a
/// site.</item>
/// <item><b>Production right after construction</b>: it sees the sites completed this tick and makes their machines; it adds nothing to the hash
/// until the player has a working building.</item>
/// <item><b>Feed after body</b>: the game's order; eating reads hunger the body system advanced in the same tick.</item>
/// <item><b>Retaliation, then aggro, last</b>: both defer <c>AttackOrder</c>s for the next tick (<see cref="World.Defer"/>) after combat has swapped
/// its arrays; aggro's leash sweep sees the tasks every other system left, and its look (<c>Act</c>) runs after combat's, reading only the last
/// tick's positions. <b>Flee after aggro</b>: a squad that breaks sends its runners off after every fight of the tick was ordered, and its stop
/// orders are deferred like theirs.</item>
/// <item><b>Crime last</b>: its witness sweep (<c>SlowWorld</c>) sees the positions, carry links and pending crimes every other system left this
/// tick; the deeds are reported to it by combat (<c>Inputs</c>, commit, the bolt flight in <c>Schedule</c>) and carrying (<c>SlowWorld</c>), all
/// serial. It adds nothing to the hash until a crime is pending. The arrest system right after it acts on the bookings and sightings of the same
/// tick, then the lock system (its picks book crimes the next sweep sees).</item>
/// <item><b>Trade after crime</b>: a caught fence is reported to the crime system from the trade's <c>Inputs</c>, and the shops are opened and
/// restocked in <c>SlowWorld</c> after the population made the tick's squads. It adds nothing to the hash until a squad has a shop.</item>
/// </list>
/// An extra system a test wants (a probe, a poke) goes after these.
/// </summary>
public static class StandardSystems
{
    /// <summary>
    /// Builds the standard systems for <paramref name="data"/>, in their order, with the parts and settings of <paramref name="options"/>.
    /// </summary>
    public static StandardSystemSet Build(PopulationData data, IWalkability walkability, StandardSystemOptions? options = null) =>
        new Builder(data, walkability, options ?? new StandardSystemOptions()).Build();

    /// <summary>
    /// One build of the standard systems: the wiring and the systems made so far, which later parts link to.
    /// </summary>
    sealed class Builder(PopulationData data, IWalkability walkability, StandardSystemOptions options)
    {
        /// <summary>
        /// The build's options.
        /// </summary>
        readonly StandardSystemOptions _o = options;

        /// <summary>
        /// The population data every system is made from.
        /// </summary>
        readonly PopulationData _data = data;

        /// <summary>
        /// The walkability the movement system paths on.
        /// </summary>
        readonly IWalkability _walkability = walkability;

        /// <summary>
        /// The systems in order, with their parts.
        /// </summary>
        readonly Wiring _systems = new();

        /// <summary>
        /// The population system, when built.
        /// </summary>
        PopulationSystem? _population;

        /// <summary>
        /// The movement system, when built.
        /// </summary>
        MovementSystem? _movement;

        /// <summary>
        /// The combat system, when built.
        /// </summary>
        CombatSystem? _combat;

        /// <summary>
        /// The AI system, when built.
        /// </summary>
        Ai.AiSystem? _ai;

        /// <summary>
        /// The relation events of deeds, with the live relations the aggro part brings.
        /// </summary>
        FactionDeeds? _deeds;

        /// <summary>
        /// The weather system, when built.
        /// </summary>
        WeatherSystem? _weather;

        /// <summary>
        /// The carry system, when built.
        /// </summary>
        Carrying.CarrySystem? _carry;

        /// <summary>
        /// The kidnap system, when built.
        /// </summary>
        Kidnap.KidnapSystem? _kidnap;

        /// <summary>
        /// The town duty system, when built.
        /// </summary>
        Kidnap.TownDutySystem? _townDuty;

        /// <summary>
        /// The ragdoll system, when built.
        /// </summary>
        Ragdolls.RagdollSystem? _ragdolls;

        /// <summary>
        /// The captive system, when built.
        /// </summary>
        Kidnap.CaptiveSystem? _captives;

        /// <summary>
        /// The bed system, when built.
        /// </summary>
        Locks.BedSystem? _beds;

        /// <summary>
        /// The construction system, when built.
        /// </summary>
        Construction.ConstructionSystem? _construction;

        /// <summary>
        /// The production system, when built.
        /// </summary>
        Production.ProductionSystem? _production;

        /// <summary>
        /// The stonemine cycle the bashers swing by (Task_BashDoor); 0: no animation data.
        /// </summary>
        float _bashCycle;

        /// <summary>
        /// The aggro system, when built.
        /// </summary>
        AggroSystem? _aggro;

        /// <summary>
        /// The crime system, when built.
        /// </summary>
        Crime.CrimeSystem? _crime;

        /// <summary>
        /// The arrest system, when built.
        /// </summary>
        Crime.ArrestSystem? _arrest;

        /// <summary>
        /// The lock system, when built.
        /// </summary>
        Locks.LockSystem? _locks;

        /// <summary>
        /// The trade system, when built.
        /// </summary>
        Trading.TradeSystem? _trade;

        /// <summary>
        /// The dialogue system, when built.
        /// </summary>
        Dialogue.DialogueSystem? _talk;

        /// <summary>
        /// Builds every part in its order and returns the set.
        /// </summary>
        public StandardSystemSet Build()
        {
            AddCore();
            AddCare();
            AddCarrying();
            AddWorks();
            AddAnimation();
            AddFights();
            AddLaw();
            AddTalk();

            return new StandardSystemSet
            {
                Systems = Splice(_systems, _o.Extensions, _data),
                Population = _population,
                Movement = _movement,
                Combat = _combat,
                Aggro = _aggro,
                Carry = _carry,
                Kidnap = _kidnap,
                Captives = _captives,
                Crime = _crime,
                Arrest = _arrest,
                Locks = _locks,
                Beds = _beds,
                Trade = _trade,
                Ai = _ai,
                Construction = _construction,
                Production = _production,
                Ragdolls = _ragdolls,
                Dialogue = _talk
            };
        }

        /// <summary>
        /// Whether the build includes a part.
        /// </summary>
        bool Has(StandardParts p) => (_o.Parts & p) != 0;

        /// <summary>
        /// The population, the player, the AI, the shared combat system and its links, pursuit and movement.
        /// </summary>
        void AddCore()
        {
            if (Has(StandardParts.Population))
                _systems.Add(StandardParts.Population, _population = new PopulationSystem(_data, _o.Population));

            if (Has(StandardParts.Player))
                _systems.Add(StandardParts.Player, new PlayerSystem(_data) { AnimalsPickUp = _o.AnimalsPickUp });

            if (Has(StandardParts.Ai)) _systems.Add(StandardParts.Ai, _ai = new Ai.AiSystem(_data, _o.Ai));

            if (Has(StandardParts.Pursuit | StandardParts.Combat | StandardParts.Retaliation | StandardParts.Flee))
            {
                _combat = new CombatSystem(_data.Combat.Techniques, _data.Combat.Constants, _data.Bodies.Constants, _data.BodyOptions,
                    _o.AnimationLengths ?? new AnimationLengths(), _o.Combat);
            }

            // The relation events of deeds (blows, first aid, cagings) go with the live relations, which the aggro part brings.
            _deeds = Has(StandardParts.Aggro) ? new FactionDeeds(_data.Relations, _data) : null;
            if (_combat is not null && Has(StandardParts.Aggro)) (_combat.Relations, _combat.Deeds) = (_data.Relations, _deeds);
            if (_combat is not null) _combat.Buildings = _o.RagdollEnvironment;
            if (_ai is not null && _combat is not null) _ai.ThreatSource = _combat;
            if (_combat is not null)
                (_combat.TurretGuns, _combat.Towns) = (new Meitou.Data.Gameplay.Combat.TurretCatalog(_data.Db), _data);

            if (Has(StandardParts.Pursuit)) _systems.Add(StandardParts.Pursuit, new PursuitSystem(_combat!));
            if (Has(StandardParts.Movement))
                _systems.Add(StandardParts.Movement, _movement = new MovementSystem(new PathService(_walkability, _o.SynchronousPaths)));
        }

        /// <summary>
        /// First aid, the body and the weather.
        /// </summary>
        void AddCare()
        {
            if (Has(StandardParts.FirstAid))
            {
                _systems.Add(StandardParts.FirstAid,
                    new FirstAid.FirstAidSystem(_data.Bodies.Constants, _data.BodyOptions, _data.Items, _o.FirstAid)
                    {
                        Deeds = _deeds,
                        Relations = Has(StandardParts.Aggro) ? _data.Relations : null
                    });
            }

            if (Has(StandardParts.Body))
            {
                _systems.Add(StandardParts.Body, new BodySystem(_data.Bodies.Constants, _data.BodyOptions, _o.BodyTimeScale, _data.Db));
                _systems.Add(StandardParts.Body,
                    _weather = new WeatherSystem(_data.Bodies.Constants) { Db = _data.Db, Buildings = _o.RagdollEnvironment });
            }
        }

        /// <summary>
        /// Carrying, with the kidnapping, the town duties and the blood smell that use its lift.
        /// </summary>
        void AddCarrying()
        {
            if (Has(StandardParts.Carry))
            {
                // The lift takes the length of the shoulder lift left clip (1 s when the lengths are not known).
                _carry = new Carrying.CarrySystem(_data.Bodies.Constants, _data.BodyOptions,
                    _o.AnimationLengths?.Of(Carrying.CarrySystem.LiftClip) ?? 1, _o.Carry);
                if (Has(StandardParts.Aggro)) _carry.Relations = _data.Relations;

                _carry.Assembly = new CharacterAssembly(_data);
                _carry.Data = _data;
                _systems.Add(StandardParts.Carry, _carry);

                // Raiders lifting the downed and carrying them to a cage of their faction: right after the carry system, whose lift and links it
                // uses; it adds nothing to the hash until one runs.
                if (_population is not null)
                    _systems.Add(StandardParts.Carry, _kidnap = new Kidnap.KidnapSystem(_data, _population, _carry, _o.Kidnap) { Deeds = _deeds });

                // Town goals of AI squads outside the act-outs (removing downed intruders, caging them, fetching food): after the kidnapping, with
                // the same lift; nothing in the hash until one runs.
                if (_population is not null && Has(StandardParts.Ai))
                    _systems.Add(StandardParts.Carry, _townDuty = new Kidnap.TownDutySystem(_data, _population, _carry) { Deeds = _deeds });

                // The zones' blood smell and the blood hunters (goal 176); nothing in the hash until a corpse has flies or someone bleeds.
                if (Has(StandardParts.Ai)) _systems.Add(StandardParts.Carry, new SmellSystem(_data, _population));
            }
        }

        /// <summary>
        /// The captives, the beds, construction, production and the combat system's own place.
        /// </summary>
        void AddWorks()
        {
            // The captives' fate and the slavers' logistics: right after the kidnapping (serial, like it); nothing in the hash until a job or a
            // town's kill timer exists.
            if (_population is not null && Has(StandardParts.Ai))
                _systems.Add(StandardParts.None, _captives = new Kidnap.CaptiveSystem(_data, _combat, _o.Captives) { Kidnap = _kidnap });

            if (Has(StandardParts.FirstAid))
                _systems.Add(StandardParts.None, _beds = new Locks.BedSystem(_data) { Carry = _carry });

            if (Has(StandardParts.Construction))
                _systems.Add(StandardParts.Construction, _construction = new Construction.ConstructionSystem(_data, _o.Construction));

            if (_combat is not null) _combat.Construction = _construction;

            if (Has(StandardParts.Production))
            {
                _systems.Add(StandardParts.Production,
                    _production = new Production.ProductionSystem(_data, _o.Production)
                    {
                        Assembly = new CharacterAssembly(_data),
                        Construction = _construction,
                        Beds = _beds,
                        Carry = _carry
                    });
            }

            if (Has(StandardParts.Combat)) _systems.Add(StandardParts.Combat, _combat!);
        }

        /// <summary>
        /// The animation and the ragdolls, and the clip lengths the carry, lock and captive systems take from it.
        /// </summary>
        void AddAnimation()
        {
            if (Has(StandardParts.Animation))
            {
                var library = _o.AnimationLibrary ?? AnimationLibrary.FromDatabase(_data.Db);
                var lengths = _o.AnimationLengths ?? new AnimationLengths();
                var animation = new AnimationSystem(library, lengths, _o.AnimationBlendRate ?? 4, _combat) { Rigs = _o.Ragdolls };
                if (library.IndexOf("stonemine") is >= 0 and var mine)
                {
                    _bashCycle = lengths.Of(library.Definitions[mine].Clip) / (library.Definitions[mine].PlaySpeed > 0
                        ? library.Definitions[mine].PlaySpeed
                        : 1);
                }

                _systems.Add(StandardParts.Animation, animation);

                // Ragdolls right after the animation: they start from and pin to this tick's pose, and the whole body's position is theirs before
                // anything later reads it.
                if (_o.Ragdolls is not null)
                {
                    _systems.Add(StandardParts.Animation,
                        _ragdolls = new Ragdolls.RagdollSystem(_o.Ragdolls, _o.RagdollEnvironment, _data.BodyOptions));
                }

                animation.Ragdolls = _ragdolls;

                // The stealth knock-out swing lasts the length of the knockout record's clip.
                if (_carry is not null && library.IndexOf(Carrying.CarrySystem.KnockoutClip) is >= 0 and var ko)
                    _carry.KnockoutSeconds = lengths.Of(library.Definitions[ko].Clip);

                if (_carry is not null && library.IndexOf(Carrying.CarrySystem.LiftClip) is >= 0 and var lift
                    && lengths.Of(library.Definitions[lift].Clip) is > 0 and var liftLength)
                {
                    _carry.LiftPlaySpeed = library.Definitions[lift].PlaySpeed > 0 ? library.Definitions[lift].PlaySpeed : 1;
                    _carry.LiftSeconds = liftLength / _carry.LiftPlaySpeed;
                }

                if (_captives is not null && library.IndexOf(Kidnap.CaptiveSystem.ChopClip) is >= 0 and var chop)
                    _captives.ChopSeconds = lengths.Of(library.Definitions[chop].Clip);
            }
        }

        /// <summary>
        /// Feeding, retaliation, aggro and fleeing.
        /// </summary>
        void AddFights()
        {
            if (Has(StandardParts.Feed)) _systems.Add(StandardParts.Feed, new FeedSystem(_data.Items, _o.Feed));
            if (Has(StandardParts.Retaliation))
                _systems.Add(StandardParts.Retaliation, new RetaliationSystem(_combat!) { Towns = _data });

            if (Has(StandardParts.Aggro))
            {
                _systems.Add(StandardParts.Aggro,
                    _aggro = new AggroSystem(_data.Relations, _o.Aggro) { Combat = _combat, Towns = _data, Population = _population });
            }

            // Without the Aggro part a fight still reads the senses (target choice, threats, the fight's end): the look keeps them, and nothing else.
            else if (_combat is not null)
            {
                _systems.Add(StandardParts.Aggro,
                    new AggroSystem(_data.Relations, (_o.Aggro ?? new AggroSettings()) with { AttackOnSight = false })
                    {
                        Combat = _combat,
                        Towns = _data,
                        Population = _population
                    });
            }

            if (_carry is not null) _carry.Aggro = _aggro;
            if (Has(StandardParts.Flee))
            {
                _systems.Add(StandardParts.Flee,
                    new FleeSystem(_data.Relations, _combat!, _o.Aggro?.SightRange ?? AggroSystem.OutdoorSight) { SquadBreak = _o.SquadBreak });
            }
        }

        /// <summary>
        /// Crime, arrests and locks.
        /// </summary>
        void AddLaw()
        {
            if (Has(StandardParts.Crime))
            {
                // Last: its sweep sees every position, task and carry link the tick left; the deeds come in from combat and carrying in their own
                // serial steps.
                _systems.Add(StandardParts.Crime,
                    _crime = new Crime.CrimeSystem(_data.Relations, _o.Crime) { Xp = new XpService(_data.Bodies.Constants, _data.BodyOptions) });
                if (_combat is not null) _combat.Crimes = _crime;
                if (_carry is not null) _carry.Crimes = _crime;
                if (_ai is not null) _ai.Crimes = _crime;

                // Right after it: the police act on this tick's bookings and sightings; the lock orders (picking, getting out) report their crimes to
                // it.
                _systems.Add(StandardParts.Crime,
                    _arrest = new Crime.ArrestSystem(_crime, _data.Bodies.Constants, _o.Arrest) { Carry = _carry, Deeds = _deeds, Towns = _data });
                _arrest.Captives = _captives;
                if (_townDuty is not null) _townDuty.Arrests = _arrest;

                _systems.Add(StandardParts.Crime,
                    _locks = new Locks.LockSystem(_data.Bodies.Constants, _data.BodyOptions)
                    {
                        Crimes = _crime,
                        DoorsCloseBehind = _o.DoorsCloseBehind,
                        Assembly = new CharacterAssembly(_data),
                        Combat = _combat,
                        CombatConstants = _data.Combat.Constants
                    });
                if (_bashCycle > 0) _locks.BashClipSeconds = _bashCycle;
                if (_combat is not null) _combat.Locks = _locks;
            }
        }

        /// <summary>
        /// Trade, dialogue and the links that need the dialogue system.
        /// </summary>
        void AddTalk()
        {
            if (Has(StandardParts.Trade))
            {
                _systems.Add(StandardParts.Trade, _trade = new Trading.TradeSystem(_data, _o.Trade));
                _trade.Crimes = _crime;
            }

            if (Has(StandardParts.Dialogue))
                _systems.Add(StandardParts.Dialogue, _talk = new Dialogue.DialogueSystem(_data, _o.Dialogue, _population));

            // a captive's squad remembers its captor (MY_CAPTOR), blows their tags
            if (_deeds is not null && _talk is not null)
                (_deeds.CagedHook, _deeds.Memory) = (_talk.Captured, _talk.Memory);

            if (_talk is not null && _population is not null)
                (_population.Announcer, _population.PendingTalk) = (_talk.Announce, _talk.HasInterrupt);

            if (_talk is not null) (_talk.Arrests, _talk.Extensions) = (_arrest, _o.Extensions);
            if (_weather is not null) _weather.Talk = _talk;
            if (_talk is not null && _aggro is not null)
                (_aggro.Memory, _aggro.SightLines, _talk.Aggro) = (_talk.Memory, _talk.HasSightLines, _aggro);
            if (_talk is not null && _ai is not null) _ai.Memory = _talk.Memory;
            if (_talk is not null && _combat is not null) _combat.Memory = _talk.Memory;
            if (_talk is not null && _population is not null) _population.Memory = _talk.Memory;
            if (_arrest is not null) _arrest.Talk = _talk;
        }
    }

    /// <summary>
    /// The standard systems with the extensions' systems spliced in (docs/modding.md "Registration surface"): each at its anchor, before the first or
    /// after the last system of its part, at the end when the part is not built; systems at the same place in registration order. Without extensions
    /// the list is the standard one.
    /// </summary>
    static List<ITickSystem> Splice(Wiring wiring, SimulationExtensions? extensions, PopulationData data)
    {
        if (extensions is not { Systems.Count: > 0 }) return wiring.Systems;

        var context = new SystemBuildContext(data, wiring.Systems.ToArray());
        var placed = new List<(int Position, int Order, ITickSystem System)>();

        for (int k = 0; k < extensions.Systems.Count; k++)
        {
            var factory = extensions.Systems[k];
            if (factory.Create(context) is not { } system) continue;

            SimulationExtensions.Made(system, factory);
            placed.Add((wiring.Position(factory.Anchor), k, system));
        }

        placed.Sort((a, b) => a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Order.CompareTo(b.Order));
        var merged = new List<ITickSystem>(wiring.Systems.Count + placed.Count);
        int next = 0;

        for (int i = 0; i <= wiring.Systems.Count; i++)
        {
            while (next < placed.Count && placed[next].Position == i) merged.Add(placed[next++].System);
            if (i < wiring.Systems.Count) merged.Add(wiring.Systems[i]);
        }

        return merged;
    }

    /// <summary>
    /// The wiring's systems in order, each with the part it came with (<see cref="StandardParts.None"/>: no anchor), for <see cref="Splice"/>.
    /// </summary>
    sealed class Wiring
    {
        /// <summary>
        /// The part each system came with, by index.
        /// </summary>
        readonly List<StandardParts> _parts = [];

        /// <summary>
        /// The systems in order.
        /// </summary>
        public List<ITickSystem> Systems { get; } = [];

        /// <summary>
        /// Adds a system with the part it came with.
        /// </summary>
        public void Add(StandardParts part, ITickSystem system)
        {
            Systems.Add(system);
            _parts.Add(part);
        }

        /// <summary>
        /// The index an anchored system goes in at: before the part's first system or after its last; the end when the part is not built.
        /// </summary>
        public int Position(SystemAnchor anchor)
        {
            if (anchor.Part == StandardParts.None) return Systems.Count;

            int first = _parts.IndexOf(anchor.Part), last = _parts.LastIndexOf(anchor.Part);
            if (first < 0) return Systems.Count;

            return anchor.After ? last + 1 : first;
        }
    }
}