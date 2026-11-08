using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Game.Audio;

/// <summary>
/// What a character's sounds depend on, read once from its look (docs/audio.md "Characters"): the voice switch value, the creature class, whether its race goes barefoot
/// and wears boots, the body armour's material and the weapon switch.
/// </summary>
public sealed class CharacterSoundProfile
{
    /// <summary>An ANIMAL_CHARACTER: it uses the <c>Creature_VO</c> events and the race name as its <c>Weapons</c> switch.</summary>
    public bool Animal { get; init; }
    /// <summary>The <c>VO_Races</c> value of a person (<c>Human_Male</c>, <c>Shek_Female</c>, <c>Hive</c>, <c>Robot</c>), or the <c>Creature_VO</c> value of an animal (<c>Dog</c>, <c>Gar</c>, ...).</summary>
    public string Voice { get; init; } = "";
    /// <summary>The <c>Weapons</c> value the character starts with: its race name (animals), else the first weapon's category name (null: none, an unarmed person).</summary>
    public string? Weapons { get; init; }
    /// <summary>The race goes barefoot (RACE <c>barefoot</c>) and wears no boots: the <c>BFoot_</c> footstep sounds.</summary>
    public bool Barefoot { get; init; }
    /// <summary>A robot person (RACE <c>sounds</c> 3): the <c>_Robot</c> variants of the unarmed <c>Weapons</c> values.</summary>
    public bool Robot { get; init; }
    /// <summary>The material of the heaviest body armour worn (0 cloth, 1 leather, 2 chain, 3 plate): the footsteps' <c>Armor_Worn</c> switch.</summary>
    public int ArmourMaterial { get; init; }
    /// <summary>The race's looping sound on the character's own object (RACE <c>ambient sound</c>, its first entry: the robot spiders' <c>Iron_Spider</c>); null for none.</summary>
    public AmbientSound? Ambient { get; init; }

    /// <summary>
    /// Reads a profile (<b>Verified</b> against the exe: <c>uses_sounds</c> for the voice, <c>FUN_1405d28b0</c> for the armour, the appearance rebuild for <c>barefoot</c>, <c>uses_bare_sword</c> for the weapon).
    /// A person: RACE <c>sounds</c> 0..3 name Human, Shek, Hive, Robot (anything else Human); <c>_Male</c> / <c>_Female</c> follows unless the race has a single gender.
    /// An animal: <c>sounds</c> less 4 indexes the creature names; below 4 it is Dog. With a database the race's <c>ambient sound</c> is read too.
    /// </summary>
    public static CharacterSoundProfile From(CharacterAppearance look, GameDatabase? db = null)
    {
        int sounds = look.Race.GetInt("sounds");
        var ambient = db is null ? null : AmbientSound.FirstOf(db, look.Race, "ambient sound");
        bool boots = false;
        int armour = 0;
        string? weapon = null;
        foreach (var part in look.Parts)
        {
            if (part.Record.Type == FcsRecordType.ARMOUR && part.Slot is { } slot)
            {
                if (slot == AttachSlot.Boots) boots = true;
                if (slot == AttachSlot.Body) armour = Math.Max(armour, part.Record.GetInt("material type"));
            }
            else if (weapon is null && part.Mode == AttachMode.Bone && part.Record.Type is FcsRecordType.WEAPON or FcsRecordType.CROSSBOW)
                weapon = CharacterSounds.WeaponName(part.Record.Type == FcsRecordType.CROSSBOW ? 6 : part.Record.GetInt("skill category", 5));
        }
        if (look.Animal is not null)
        {
            string creature = CharacterSounds.CreatureName(sounds);
            return new CharacterSoundProfile { Animal = true, Voice = creature, Weapons = creature, Barefoot = false, ArmourMaterial = 0, Ambient = ambient };
        }
        string race = CharacterSounds.PersonName(sounds);
        return new CharacterSoundProfile
        {
            Robot = sounds == 3,
            Voice = look.Race.GetBool("single gender") ? race : race + (look.Female ? "_Female" : "_Male"),
            Weapons = string.IsNullOrEmpty(weapon) ? null : weapon,
            Barefoot = look.Race.GetBool("barefoot") && !boots,
            ArmourMaterial = Math.Clamp(armour, 0, 3),
            Ambient = ambient,
        };
    }
}

/// <summary>One animation of a character this frame, with the events riding on its record: the library index (the key of the event cursor), how far through its clip it is (0..1) and its weight.</summary>
/// <summary>One animation of a character this frame, with the events riding on its record: the library index (the key of the event cursor), how far through its clip it is (0..1) and its weight. <paramref name="Weapons"/> is the <c>Weapons</c> value an unarmed technique sets when it starts (<c>HtH_Punch</c> or <c>HtH_Kick</c>).</summary>
public readonly record struct SoundLayer(int Definition, AnimationSoundEvent[]? Events, float Fraction, float Weight, string? Weapons = null);

/// <summary>A job a character does with a sound that lasts (<b>Verified</b>, the task methods of the exe: start in slot 1, stop in slot 3).</summary>
public enum CharacterLoop : byte { None, Build, Medic, Unlock }

/// <summary>
/// The sounds of characters (docs/audio.md "Characters"): a Wwise game object per character near the camera, kept at its position and facing, with the switches and the
/// speed parameter the exe sets on it, the animation events (footsteps, swings) fired the way the exe fires them, and the voices and impacts of combat.
/// It reads the world through what the host hands it each frame and never writes back. The exe rules (Verified in the decompile unless marked):
/// <list type="bullet">
/// <item>An event is posted on the character's object and dropped when the character is farther from the camera than its limit (squared 1,000,000 = 1000 units; footsteps 40,000 = 200).</item>
/// <item>A footstep (an event named exactly <c>Footsteps</c>, no switch override) sets <c>Armor_Worn</c> to the body armour's material first.</item>
/// <item>While a character moves, <c>Surface_Material</c> follows the ground (every 120,000 squared units of camera distance or less; under height 100 it is <c>Water</c>; a building's floor under the foot names its own material), barefoot races use the <c>BFoot_</c> names.</item>
/// <item><c>Movement_Speed</c> is the speed in units per second, set within 1000 units.</item>
/// </list>
/// Characters beyond <see cref="TrackRadius"/> have no object (the exe keeps one for every loaded character; nothing farther than 1000 units is ever heard).
/// </summary>
public sealed class CharacterSounds
{
    /// <summary>Characters nearer than this to the camera get a game object; they lose it beyond <see cref="DropRadius"/>.</summary>
    public const float TrackRadius = 1100, DropRadius = 1400;
    /// <summary>The squared camera distances of the exe: an ordinary event, a footstep, the movement speed, the surface.</summary>
    public const float EventLimit = 1000000, FootstepLimit = 40000, SpeedLimit = 1000000, SurfaceLimit = 120000;
    /// <summary>The first game object id of a character, and of the pool that bolts sticking in the ground sound on.</summary>
    public const ulong CharacterObjectBase = 1_000_000, ImpactPoolBase = 900_000;
    public const int ImpactPoolSize = 8;
    /// <summary>The height under which a walker is in water (<c>FUN_14065df80</c>: 100).</summary>
    public const float WaterHeight = 100;

    static readonly string[] Persons = ["Human", "Shek", "Hive", "Robot"];
    static readonly string[] Creatures = ["Beak", "Bull", "Cage", "Dog", "Duck", "Gar", "Goat", "Gorillo", "IronSpider", "Leviathan", "Pack", "Spider", "Turtle"];
    static readonly string[] Weapons = ["Katanas", "Sabres", "Blunt", "Heavy", "Hackers", "", "Bow", "Turret", "Blunt"];
    static readonly string[] Armours = ["Cloth", "Leather", "Chain", "Plate"];
    static readonly string[] Shoes = ["Sand", "Grass", "Concrete", "Wood", "Metal", "Water", "Mud", "Snow", "Dirt"];
    static readonly string[] Bare = ["BFoot_Sand", "BFoot_Grass", "Concrete", "BFoot_Wood", "BFoot_Metal", "Water", "Mud", "Snow", "BFoot_Dirt"];

    /// <summary>The <c>Weapons</c> value of a WEAPON <c>skill category</c> (the exe's table; unarmed (5) sets none; categories past 8 give "").</summary>
    public static string WeaponName(int category) => (uint)category < Weapons.Length ? Weapons[category] : "";
    /// <summary>The race name of a person's RACE <c>sounds</c> (0..3; anything else is Human).</summary>
    public static string PersonName(int sounds) => (uint)sounds < Persons.Length ? Persons[sounds] : Persons[0];
    /// <summary>The creature name of an animal's RACE <c>sounds</c> (the index less 4; below 4: Dog, the exe's index 3).</summary>
    public static string CreatureName(int sounds) => sounds - 4 is >= 0 and var i && i < Creatures.Length ? Creatures[i] : Creatures[3];
    /// <summary>The <c>Surface_Material</c> value of a GroundType for shoes or bare feet.</summary>
    public static string SurfaceName(int surface, bool barefoot) => (uint)surface < Shoes.Length ? (barefoot ? Bare : Shoes)[surface] : "";
    /// <summary>The <c>Armor_Worn</c> value of an armour material.</summary>
    public static string ArmourName(int material) => Armours[Math.Clamp(material, 0, 3)];

    sealed class Entry
    {
        public ulong Object;
        public Vector3 Position;
        public CharacterSoundProfile Profile = null!;
        public int Frame;
        public int Surface = -1;
        public Vector3? FloorPoint;
        public int FloorResult;
        public float Speed = -1;
        public double NextPassive;
        public bool Down;
        /// <summary>The race's ambient emitter: its switch is set, and it is started.</summary>
        public bool AmbientSet, AmbientOn;
        public CharacterLoop Loop;
        public readonly List<Cursor> Cursors = [];
    }

    sealed class Cursor
    {
        public int Definition;
        public AnimationSoundEvent[] Events = [];
        public AnimationEventCursor State;
        public int Frame;
    }

    readonly WwisePlayer player;
    readonly GroundSurfaces? ground;
    readonly Dictionary<ulong, Entry> entries = [];
    readonly List<ulong> gone = [];
    readonly Random random = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly ulong[] pool = new ulong[ImpactPoolSize];
    Vector3 camera;
    int frame, poolAt;

    /// <summary>Events posted since the start, for the statistics: footsteps, other animation events, impacts, voices.</summary>
    public int Footsteps { get; private set; }
    public int Swings { get; private set; }
    public int Impacts { get; private set; }
    public int Voices { get; private set; }
    public int Jobs { get; private set; }
    public int Objects => entries.Count;

    /// <summary>
    /// The GroundType of the building floor under a character's feet, or -1 when the ground is not a building's (then the terrain's overlay decides): the host's lookup of what the
    /// game's ground ray hits (docs/audio.md "Surface"). Asked for a moving character within <see cref="SurfaceLimit"/>, again only after it moved a unit.
    /// </summary>
    public Func<Vector3, int>? Floor { get; init; }

    public CharacterSounds(WwisePlayer player, GroundSurfaces? ground)
    {
        this.player = player;
        this.ground = ground;
        for (int i = 0; i < pool.Length; i++) pool[i] = ImpactPoolBase + (ulong)i;
        player.LoadBank("VO_Bank");
        player.LoadBank("SFX_General_Bank");
    }

    /// <summary>Starts a frame: where the camera's eye is (the origin of every distance limit).</summary>
    public void BeginFrame(Vector3 eye)
    {
        camera = eye;
        frame++;
    }

    /// <summary>Ends a frame: characters not handed over since the last one are gone, and lose their objects.</summary>
    public void EndFrame()
    {
        gone.Clear();
        foreach (var (key, e) in entries)
            if (e.Frame != frame) gone.Add(key);
        foreach (var key in gone) Remove(key);
    }

    /// <summary>Ends everything: every object is unregistered (a new world, the main menu).</summary>
    public void Clear()
    {
        foreach (var e in entries.Values) player.UnregisterObject(e.Object);
        entries.Clear();
    }

    void Remove(ulong key)
    {
        if (!entries.Remove(key, out var e)) return;
        foreach (var c in e.Cursors) Stopped(e, c);
        player.UnregisterObject(e.Object);
    }

    ulong nextObject = CharacterObjectBase;

    /// <summary>
    /// One character this frame: its place, facing and speed (units per second), and the animations it plays with their events. Characters beyond the drop radius are
    /// forgotten; the first update inside the track radius makes the object, with the voice and weapon switches (<c>uses_sounds</c>, <c>uses_bare_sword</c>).
    /// </summary>
    public void Update(ulong key, CharacterSoundProfile profile, Vector3 position, float yaw, float speed, ReadOnlySpan<SoundLayer> layers, CharacterLoop loop = CharacterLoop.None, bool down = false, bool combat = false)
    {
        float d2 = player.HeardDistanceSquared(position, camera);
        entries.TryGetValue(key, out var e);
        if (e is null)
        {
            if (d2 > TrackRadius * TrackRadius) return;
            e = new Entry { Object = nextObject++, NextPassive = clock.Elapsed.TotalSeconds + random.NextDouble() * 100 };
            entries[key] = e;
            player.RegisterObject(e.Object);
        }
        else if (d2 > DropRadius * DropRadius)
        {
            Remove(key);
            return;
        }
        e.Frame = frame;
        if (!ReferenceEquals(e.Profile, profile))
        {
            // A new look (equipment changed): the switches follow. A person's voice, an animal's voice and weapons, the weapon in hand.
            bool first = e.Profile is null;
            e.Profile = profile;
            e.Surface = -1;
            if (profile.Animal)
            {
                if (first) { player.SetSwitch("Creature_VO", profile.Voice, e.Object); player.SetSwitch("Weapons", profile.Weapons!, e.Object); }
            }
            else
            {
                player.SetSwitch("VO_Races", profile.Voice, e.Object);
                if (profile.Weapons is { } weapon) player.SetSwitch("Weapons", weapon, e.Object);
            }
        }
        e.Position = position;
        // No front: the attenuation cone of the impact sounds (90 / 245 degrees, -6 dB) would halve a blow on a fighter facing away from the listener (docs/audio.md "Attenuation").
        player.SetPosition(e.Object, position);
        if (d2 < SpeedLimit && MathF.Abs(speed - e.Speed) > 0.01f)
        {
            e.Speed = speed;
            player.SetParameter("Movement_Speed", speed, e.Object);
        }
        if (speed > 0) UpdateSurface(e, position, d2);
        if (e.Profile.Ambient is { } ambient) UpdateAmbient(e, ambient, d2);
        SetLoop(e, loop, d2);
        ProcessLayers(e, layers, d2);
        if (e.Profile.Animal) PassiveVoice(e, d2, down, combat);
    }

    /// <summary>
    /// The race's looping sound (docs/audio.md "Point emitters"; <c>uses_ambient_sound</c>, updated by <c>FUN_1405cfd90</c> every frame): <c>Start_Emitter</c> on the character's own object while the eye
    /// is within the sound's range (the squared distance not beyond <c>cutoff² × 100</c>), <c>Stop_Emitter</c> beyond it, none for a body that is down (the exe makes it for a living character only).
    /// The sound needs no power; its base intensity is 0 for the robot spiders, so <c>Intensity</c> is not set on the object and the global one (the wind's) is heard.
    /// </summary>
    void UpdateAmbient(Entry e, AmbientSound sound, float d2)
    {
        bool want = !e.Down && d2 <= sound.Range * sound.Range;
        if (want == e.AmbientOn) return;
        e.AmbientOn = want;
        if (!want) { player.Post("Stop_Emitter", e.Object); return; }
        if (!e.AmbientSet)
        {
            e.AmbientSet = true;
            player.SetSwitch("Emitters", sound.Emitter, e.Object);
            if (sound.Intensity != 0) player.SetParameter("Intensity", sound.Intensity * 100, e.Object);
        }
        player.Post("Start_Emitter", e.Object);
    }

    void UpdateSurface(Entry e, Vector3 position, float d2)
    {
        int surface;
        if (position.Y < WaterHeight) surface = GroundSurfaces.Water;
        else if (d2 < SurfaceLimit)
        {
            // A building's floor under the foot names its own material; anywhere else the terrain's overlay does.
            surface = BuildingFloor(e, position);
            if (surface < 0 && ground is not null) surface = ground.At(position.X, position.Z);
        }
        else return;
        if (surface < 0 || surface >= Shoes.Length) return;   // still loading; or a type past the table (a building's material 9: the exe reads past its name table, Unknown)
        bool bare = e.Profile.Barefoot;
        int packed = surface | (bare ? 0x100 : 0);
        if (e.Surface == packed) return;
        e.Surface = packed;
        player.SetSwitch("Surface_Material", SurfaceName(surface, bare), e.Object);
    }

    int BuildingFloor(Entry e, Vector3 position)
    {
        if (Floor is null) return -1;
        if (e.FloorPoint is { } at && Vector3.DistanceSquared(at, position) < 1) return e.FloorResult;
        e.FloorPoint = position;
        return e.FloorResult = Floor(position);
    }

    void ProcessLayers(Entry e, ReadOnlySpan<SoundLayer> layers, float d2)
    {
        // Only the heaviest animation that carries events fires them (FUN_1405b5c30: the first of the highest weight).
        int dominant = -1;
        for (int i = 0; i < layers.Length; i++)
            if (layers[i].Events is { Length: > 0 } && (dominant < 0 || layers[i].Weight > layers[dominant].Weight)) dominant = i;
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = layers[i];
            if (layer.Events is not { Length: > 0 } events) continue;
            Cursor? cursor = null;
            foreach (var c in e.Cursors)
                if (c.Definition == layer.Definition) { cursor = c; break; }
            if (cursor is null)
            {
                e.Cursors.Add(cursor = new Cursor { Definition = layer.Definition, Events = events });
                // An unarmed technique choosing its blow (FUN_1402b4570): the fist or the foot, the robot variant for a robot.
                if (layer.Weapons is { } hth) player.SetSwitch("Weapons", e.Profile.Robot ? hth + "_Robot" : hth, e.Object);
            }
            else if (!ReferenceEquals(cursor.Events, events))
            {
                // A machine's events added to (or gone from) the playing clip.
                cursor.Events = events;
                cursor.State.Rebase(events);
            }
            cursor.Frame = frame;
            cursor.State.Update(events, Math.Clamp(layer.Fraction, 0, 1), i == dominant, ev => Fire(e, ev, d2));
        }
        // An animation that is gone: its events' stop events (the destructor, FUN_1400647e0).
        for (int k = e.Cursors.Count - 1; k >= 0; k--)
        {
            if (e.Cursors[k].Frame == frame) continue;
            Stopped(e, e.Cursors[k]);
            e.Cursors.RemoveAt(k);
        }
    }

    void Stopped(Entry e, Cursor cursor)
    {
        foreach (var ev in cursor.Events)
            if (ev.Stop.Length > 0) player.Post(ev.Stop, e.Object);
    }

    /// <summary><c>AnimationSFXEvent::vfunc_1</c> (<c>1400649c0</c>): the override switch, or the armour for a footstep, then the event within its distance.</summary>
    void Fire(Entry e, AnimationSoundEvent ev, float d2)
    {
        float limit = EventLimit;
        if (ev.OverrideState.Length == 0)
        {
            if (ev.Footstep)
            {
                player.SetSwitch("Armor_Worn", ArmourName(e.Profile.ArmourMaterial), e.Object);
                limit = FootstepLimit;
            }
        }
        else player.SetSwitch(ev.OverrideState, ev.OverrideValue, e.Object);
        if (d2 >= limit) return;
        player.Post(ev.Event, e.Object);
        if (ev.Footstep) Footsteps++; else Swings++;
    }

    /// <summary>An animal's idle call (<c>CharacterAnimal::vfunc_29</c>): <c>VO_Creature_Passive</c> every 20 to 60 seconds, the first up to 100 s after it appears. Not while <paramref name="down"/> (slot 6, the timer stays due); in combat mode (<c>isInCombatMode</c>) the call is skipped and the timer still reschedules (Verified <c>FUN_1405cdf00</c>).</summary>
    void PassiveVoice(Entry e, float d2, bool down, bool combat)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (now < e.NextPassive || down) return;
        if (!combat && d2 < EventLimit) { player.Post("VO_Creature_Passive", e.Object); Voices++; }
        e.NextPassive = now + random.NextDouble() * 40 + 20;
    }

    // ------------------------------------------------------------------ jobs

    /// <summary>Whether this loop's start is dropped beyond the usual distance (the unlock loop is; building and medic loops are heard from anywhere the object is).</summary>
    static float StartLimit(CharacterLoop loop) => loop == CharacterLoop.Unlock ? EventLimit : 0;

    static (string Start, string Stop) LoopEvents(CharacterLoop loop) => loop switch
    {
        CharacterLoop.Build => ("Character_Build_Loop_Start", "Character_Build_Loop_Stop"),
        CharacterLoop.Medic => ("Character_Medic_Start", "Character_Medic_Stop"),
        _ => ("Character_Unlock", "Character_Unlock_Fail"),
    };

    /// <summary>
    /// The job loop follows what the character is doing: a change stops the old one (stops are never dropped) and starts the new one, which also happens when the character first gets
    /// its object in the middle of a job (the exe's object exists all along). Building and medic loops are never dropped by distance; the unlock loop is dropped past 1000 units.
    /// </summary>
    void SetLoop(Entry e, CharacterLoop loop, float d2)
    {
        if (e.Loop == loop) return;
        if (e.Loop != CharacterLoop.None) player.Post(LoopEvents(e.Loop).Stop, e.Object);
        e.Loop = loop;
        if (loop == CharacterLoop.None) return;
        float limit = StartLimit(loop);
        if (limit == 0 || d2 < limit) { player.Post(LoopEvents(loop).Start, e.Object); Jobs++; }
    }

    /// <summary>
    /// A one-shot job sound on the character (<paramref name="limit"/> is the squared distance of the exe's call, 0 for none), after the switch it sets first: <c>Character_Loot</c> (start of a loot),
    /// <c>Character_Unlock_Success</c> (a lock opened; it holds the stop of the unlock loop), <c>Character_Lock</c>, <c>Character_Bed</c> with <c>Bed</c> = <c>Get_In</c> / <c>Get_Out</c>,
    /// <c>Character_Consume</c> with <c>Consume</c> = <c>Eat</c> (200 units).
    /// </summary>
    public void Job(ulong key, string eventName, float limit = EventLimit, string? group = null, string? value = null)
    {
        if (!entries.TryGetValue(key, out var e)) return;
        if (group is not null && value is not null) player.SetSwitch(group, value, e.Object);
        if (limit != 0 && player.HeardDistanceSquared(e.Position, camera) >= limit) return;
        if (eventName == "Character_Unlock_Success" && e.Loop == CharacterLoop.Unlock) e.Loop = CharacterLoop.None;
        player.Post(eventName, e.Object);
        Jobs++;
    }

    /// <summary>A person lifted onto a shoulder or set down (<c>FUN_14034fbe0</c>, <c>Task_PutDownObject</c>): posted on the carried person with its own <c>Armor_Worn</c>, 1000 units.</summary>
    public void Carried(ulong key, bool lifted)
    {
        if (!entries.TryGetValue(key, out var e)) return;
        player.SetSwitch("Armor_Worn", ArmourName(e.Profile.ArmourMaterial), e.Object);
        if (player.HeardDistanceSquared(e.Position, camera) >= EventLimit) return;
        player.Post(lifted ? "Character_Pick_Up_Person" : "Character_Drop_Person", e.Object);
        Jobs++;
    }

    // ------------------------------------------------------------------ combat

    /// <summary>Whether the character has an object now (it is near enough to be heard).</summary>
    public bool Tracked(ulong key) => entries.ContainsKey(key);

    bool Post(ulong key, string ev, float limit = EventLimit)
    {
        if (!entries.TryGetValue(key, out var e)) return false;
        if (player.HeardDistanceSquared(LastPosition(e), camera) >= limit && limit != 0) return false;
        player.Post(ev, e.Object);
        return true;
    }

    // The distance of an event is checked against the position the object was last placed at.
    static Vector3 LastPosition(Entry e) => e.Position;

    /// <summary>A melee blow landed on <paramref name="key"/>: the <c>Impacts</c> switch (<c>Light_Hit</c>, <c>Heavy_Hit</c>, <c>HtH_Impacts</c>, <c>HtH_Impacts_Robot</c>), <c>Impact</c> and, half the time, the pain voice (<c>CharacterAnimal::vfunc_62</c>).</summary>
    public void Hit(ulong key, bool heavy, bool attackerUnarmed, bool attackerRobot)
    {
        if (!entries.TryGetValue(key, out var e)) return;
        string impacts = attackerUnarmed ? (attackerRobot ? "HtH_Impacts_Robot" : "HtH_Impacts") : heavy ? "Heavy_Hit" : "Light_Hit";
        Impact(e, impacts);
        PainVoice(e);
    }

    /// <summary>A melee blow was blocked: <c>Impacts</c> = <c>Parry</c> (<c>HtH_Impacts</c> when the attacker is unarmed and not a robot), <c>Impact</c>; no voice.</summary>
    public void Blocked(ulong key, bool attackerUnarmed, bool attackerRobot)
    {
        if (!entries.TryGetValue(key, out var e)) return;
        Impact(e, attackerUnarmed && !attackerRobot ? "HtH_Impacts" : "Parry");
    }

    /// <summary>A bolt in a body (<c>FUN_140439c90</c>): <c>Deflection</c> with <c>Armor_Worn</c> when armour covers the spot, else <c>Impact</c> as the last <c>Impacts</c> value stood, and the pain voice.</summary>
    public void BoltHit(ulong key, bool armoured)
    {
        if (!entries.TryGetValue(key, out var e)) return;
        if (armoured)
        {
            player.SetSwitch("Armor_Worn", ArmourName(e.Profile.ArmourMaterial), e.Object);
            Impact(e, "Light_Hit", "Deflection");
        }
        else Impact(e, "Light_Hit");
        PainVoice(e);
    }

    void Impact(Entry e, string impacts, string ev = "Impact")
    {
        player.SetSwitch("Impacts", impacts, e.Object);
        if (player.HeardDistanceSquared(LastPosition(e), camera) >= EventLimit) return;
        player.Post(ev, e.Object);
        Impacts++;
    }

    /// <summary>A person with 50 % (the random value's bit 8 clear) says <c>VO_Get_Hit</c>, an animal <c>VO_Creature_Take_Damage</c>.</summary>
    void PainVoice(Entry e)
    {
        if ((random.Next() & 8) != 0 || player.HeardDistanceSquared(LastPosition(e), camera) >= EventLimit) return;
        player.Post(e.Profile.Animal ? "VO_Creature_Take_Damage" : "VO_Get_Hit", e.Object);
        Voices++;
    }

    /// <summary>A crossbow was fired: <c>Attack</c> on the shooter, with the <c>Weapons</c> switch it holds.</summary>
    public void Shot(ulong key)
    {
        if (Post(key, "Attack")) Swings++;
    }

    /// <summary>
    /// A turret fired, its muzzle at <paramref name="at"/> (<c>FUN_14043a730</c>): <c>Attack</c> with <c>Weapons</c> = <c>Turret</c>. The exe posts it on the building's object; here on a pooled object
    /// at the muzzle (<b>engine choice</b>).
    /// </summary>
    public void TurretShot(Vector3 at)
    {
        if (player.HeardDistanceSquared(at, camera) >= EventLimit) return;
        ulong id = pool[poolAt++ % pool.Length];
        player.SetPosition(id, at);
        player.SetSwitch("Weapons", "Turret", id);
        player.Post("Attack", id);
        Swings++;
    }

    /// <summary>
    /// A wall line broke at <paramref name="at"/> (<c>WallBuilding::vfunc_62</c>): <c>Break</c>, on a pooled object there (the exe: the building's object). <paramref name="fix"/>: the
    /// <c>Break</c> switch set to <c>Gate</c> (<b>engine choice</b>, the bank has only <c>Door</c> and <c>Gate</c>); off, as the exe, which never sets it, so it plays nothing.
    /// </summary>
    public void WallBreak(Vector3 at, bool fix)
    {
        if (player.HeardDistanceSquared(at, camera) >= EventLimit) return;
        ulong id = pool[poolAt++ % pool.Length];
        player.SetPosition(id, at);
        if (fix) player.SetSwitch("Break", "Gate", id);
        player.Post("Break", id);
        Impacts++;
    }

    /// <summary>A bolt stuck in the ground at <paramref name="at"/>: <c>Impact</c> on a pooled object there, <c>Impacts</c> = the ground's name (<c>Stone</c> for anything the table lacks).</summary>
    public void BoltStuck(Vector3 at, int surface)
    {
        if (player.HeardDistanceSquared(at, camera) >= EventLimit) return;
        ulong id = pool[poolAt++ % pool.Length];
        player.SetPosition(id, at);
        player.SetSwitch("Impacts", SurfaceName(surface, false) is { Length: > 0 } name ? name : "Stone", id);
        player.Post("Impact", id);
        Impacts++;
    }

    /// <summary>The squared camera distance within which a luckyren burst also squelches (<see cref="Burst"/>; its sound has no distance falloff).</summary>
    public const float SquelchLimit = 160000;

    /// <summary>
    /// luckyren Mode's burst at <paramref name="at"/> (docs/game/luckyren.md; the game has no such sound): <c>Impact</c> as a <c>Heavy_Hit</c> on a pooled object there and,
    /// within <see cref="SquelchLimit"/>, the wet meat drop (<c>Item_Drop</c>, <c>Inventory_Items</c> = <c>Meat</c>), an interface sound on the GUI object.
    /// </summary>
    public void Burst(Vector3 at)
    {
        float distance = player.HeardDistanceSquared(at, camera);
        PointImpact(at, "Heavy_Hit");
        if (distance >= SquelchLimit) return;
        player.SetSwitch("Inventory_Items", "Meat", AudioSystem.GuiObject);
        player.Post("Item_Drop", AudioSystem.GuiObject);
    }

    /// <summary>A luckyren piece of flesh landing at <paramref name="at"/>: <c>Impact</c> as a <c>Light_Hit</c> on a pooled object there.</summary>
    public void Splat(Vector3 at) => PointImpact(at, "Light_Hit");

    void PointImpact(Vector3 at, string impacts)
    {
        if (player.HeardDistanceSquared(at, camera) >= EventLimit) return;
        ulong id = pool[poolAt++ % pool.Length];
        player.SetPosition(id, at);
        player.SetSwitch("Impacts", impacts, id);
        player.Post("Impact", id);
        Impacts++;
    }

    /// <summary>A character went down. An animal calls <c>VO_Creature_Die</c> (<c>FUN_1405cbd60</c>); the attacker animal <c>VO_Creature_Victory</c> (<c>CharacterAnimal::vfunc_62</c>).</summary>
    public void Down(ulong key, ulong attacker)
    {
        if (entries.TryGetValue(key, out var e))
        {
            e.Down = true;
            if (e.Profile?.Animal == true && Post(key, "VO_Creature_Die")) Voices++;
        }
        if (attacker != 0 && entries.TryGetValue(attacker, out var a) && a.Profile?.Animal == true && Post(attacker, "VO_Creature_Victory")) Voices++;
    }

    /// <summary>An animal takes up a fight: <c>VO_Creature_Alert</c> (<c>FUN_14085a070</c>).</summary>
    public void Alert(ulong key)
    {
        if (entries.TryGetValue(key, out var e) && e.Profile?.Animal == true && Post(key, "VO_Creature_Alert")) Voices++;
    }

    /// <summary>One line for the statistics.</summary>
    public string StatsLine() => $"{Objects} characters, {Footsteps} footsteps, {Swings} swings, {Impacts} impacts, {Voices} voices, {Jobs} job sounds";
}
