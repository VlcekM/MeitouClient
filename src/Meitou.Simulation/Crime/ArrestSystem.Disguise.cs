using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Crime;

// Disguises (docs/game/perception.md "Disguise"): the clothing level of the player's characters, the lookers that see through it, and the faction a disguised character passes for.
public sealed partial class ArrestSystem
{
    /// <summary>A disguise counts while its level is above this and it is not blown (<c>FUN_1407c0520</c>, <b>Verified</b>).</summary>
    public const float DisguiseWorks = 0.6f;
    /// <summary>A looker sees through a disguise within this distance (<c>SensoryData::disguiseMods</c>: 160000 squared, <b>Verified</b>).</summary>
    public const float DisguiseRange = 400;
    /// <summary>The seconds a looker's tooltip mark lasts (<c>disguiseMods</c>' 14.0, <b>Verified</b>).</summary>
    public const float MarkSeconds = 14;
    /// <summary>The marks: the uniform is the looker's faction's, the looker's faction owns the slave, the looker's squad knows the character (a grudge).</summary>
    public const int SameFactionMark = 0, SlaveOwnerMark = 1, KnowsMeMark = 2;

    /// <summary>The towns the location tests read (null: no town tests, every factor that needs one is left out).</summary>
    public PopulationData? Towns { get; set; }

    readonly Dictionary<(CharacterId Looker, CharacterId Target), float> disguiseMeters = [];

    /// <summary>The faction a looker takes <paramref name="c"/> for: its uniform's while the disguise works (<see cref="DisguiseWorks"/>, not blown), else its own.</summary>
    public static int SeenFaction(CharacterCold c) => c.DisguiseFaction >= 0 && c.DisguiseLevel > DisguiseWorks && c.RecentlyWanted <= 0 ? c.DisguiseFaction : c.Faction;

    /// <summary>
    /// The clothing disguise (<c>AI::vfunc_9</c> <c>FUN_1405a51e0</c>, <b>Observed</b>): a uniform hat 0.4, a uniform body armour 0.4 (its faction replaces the hat's), uniform legs of
    /// that same faction 0.2; the faction is the last uniform piece's. No body armour, no legs, or two pieces of different uniforms make it a tenth.
    /// </summary>
    public (float Level, int Faction) DisguiseOf(Inventory? inv)
    {
        if (inv is null) return (0, -1);
        float level = 0;
        int faction = -1;
        bool mixed = false;
        int Uniform(ItemInstance? i) => i is null || i.Uniform.Length == 0 ? -1 : Crimes.Relations.IndexOf(i.Uniform);
        int hat = Uniform(inv.Items.FirstOrDefault(i => i.Section == "head"));
        if (hat >= 0) (level, faction) = (level + 0.4f, hat);
        var bodyItem = inv.Items.FirstOrDefault(i => i.Section == "armour");
        if (bodyItem is null) mixed = true;
        else
        {
            int body = Uniform(bodyItem);
            if (faction >= 0 && faction != body) mixed = true;
            if (body >= 0) (level, faction) = (level + 0.4f, body);
        }
        var legsItem = inv.Items.FirstOrDefault(i => i.Section == "legs");
        if (legsItem is null) mixed = true;
        else
        {
            int legs = Uniform(legsItem);
            if (faction >= 0 && legs >= 0 && legs != faction) mixed = true;
            if (legs >= 0 && legs == faction) level += 0.2f;
        }
        return (mixed ? level * 0.1f : level, faction);
    }

    /// <summary>
    /// Once a scan: the player's characters' disguise levels, the marks' countdown, and every NPC looker within <see cref="DisguiseRange"/> that sees a working disguise filling its meter
    /// on it (<see cref="DisguiseSeconds"/>; the original's spot meter, <c>FUN_140856cb0</c>). A meter that was full before this look sees through it: with a bounty of the looker's faction the
    /// looker fires event 44 BOUNTY_SPOTTED, else the disguise is blown for 250 s (<see cref="CharacterCold.RecentlyWanted"/>, the same status field). Unseen meters empty by dt / 260.
    /// </summary>
    void Disguise(World world, float seconds)
    {
        var table = world.Characters;
        var next = table.Next;
        int player = world.Player.Faction;
        var seen = new HashSet<(CharacterId, CharacterId)>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!next[s].Alive || table.Cold(s) is not { } c || c.Faction != player || player < 0) continue;
            (c.DisguiseLevel, c.DisguiseFaction) = c.Medical is { Dead: true } ? (0, -1) : DisguiseOf(c.Inventory);
            if (c.DisguiseMarks is { } marks) for (int m = 0; m < marks.Length; m++) marks[m] = MathF.Max(0, marks[m] - seconds);
            if (c.DisguiseFaction < 0 || c.DisguiseLevel <= DisguiseWorks || c.RecentlyWanted > 0 || !Suspect(world, s)) continue;
            var at = next[s].Position;
            var target = table.IdOf(s);
            var secure = SecureLocation(world, at);
            for (int w = 0; w < table.HighWater; w++)
            {
                if (w == s || !next[w].Alive || next[w].Task == (byte)CharacterTask.Rest) continue;
                if (table.Cold(w) is not { IsPlayer: false, IsImprisoned: false, Medical.Incapacitated: false, CarriedBy.IsNone: true } wc || wc.Faction == player || !Crimes.Relations.Contains(wc.Faction)) continue;
                if (Vector3.DistanceSquared(next[w].Position, at) > DisguiseRange * DisguiseRange) continue;
                float v = world.Sight.Sees(w, s, next[w].Position, at, DisguiseRange);
                if (v <= 0) continue;
                var key = (table.IdOf(w), target);
                seen.Add(key);
                float meter = disguiseMeters.GetValueOrDefault(key);
                if (meter > 1)
                {
                    SeeThrough(world, w, s, c, wc);
                    disguiseMeters.Remove(key);
                    break;
                }
                disguiseMeters[key] = meter + seconds / DisguiseSeconds(world, w, wc, target, c, v, secure);
            }
        }
        foreach (var key in disguiseMeters.Keys.ToList())
        {
            if (seen.Contains(key)) continue;
            float left = disguiseMeters[key] - seconds / Orders.SpotForgetSeconds;
            if (left <= 0 || !table.IsAlive(key.Looker) || !table.IsAlive(key.Target)) disguiseMeters.Remove(key);
            else disguiseMeters[key] = left;
        }
    }

    /// <summary>
    /// The seconds a looker takes to see through a working disguise (<c>disguiseMods</c>, <b>Observed</b>; literals <b>Verified</b>): a = 5 + 40 x lerp(stealth, 1, 2) x level x the
    /// factors (the uniform is the looker's faction's x 0.6, its faction owns the slave x 0.5, its squad has a grudge on the character x 0.5, the character is inside the walls of a town
    /// that is not public x 0.75, the looker stands outside a friendly town of a real faction x 2); then the spot meter's time lerp(v, 5a, a) x lerp(f, 4, 1) with the looker's alertness f
    /// (<see cref="Orders.SpotAlertness"/>). The looker's marks go on the character for the tooltip.
    /// </summary>
    float DisguiseSeconds(World world, int w, CharacterCold wc, CharacterId target, CharacterCold c, float v, bool secure)
    {
        float stealth = c.Stats is { } stats ? Math.Clamp(stats[StatsEnumerated.Stealth] * stats.Scale * 0.01f, 0, 1) : 0;
        float k = (1 + stealth) * c.DisguiseLevel;
        if (Talk?.Memory.Find(world, wc.SquadId, target) is { } e && (e.Long & Dialogue.MemoryTag.HateMask) != 0) { k *= 0.5f; Mark(c, KnowsMeMark); }
        if (c.DisguiseFaction == wc.Faction) { k *= 0.6f; Mark(c, SameFactionMark); }
        if (c.SlaveOwner >= 0 && c.SlaveOwner == wc.Faction) { k *= 0.5f; Mark(c, SlaveOwnerMark); }
        if (secure) k *= 0.75f;
        if (!InFriendlyTown(world, world.Characters.Next[w].Position, wc.Faction)) k *= 2;
        float a = 5 + 40 * k;
        return (5 * a + (a - 5 * a) * Math.Clamp(v, 0, 1)) * (4 + (1 - 4) * Orders.SpotAlertness);
    }

    static void Mark(CharacterCold c, int mark) => (c.DisguiseMarks ??= new float[3])[mark] = MarkSeconds;

    /// <summary>A looker has seen through the disguise: event 44 when the character is wanted by the looker's faction, else blown for 250 s.</summary>
    void SeeThrough(World world, int w, int s, CharacterCold c, CharacterCold wc)
    {
        if (c.Bounties?.BookedFor(Crimes.Factions.Relations, wc.Faction) is { Claimed: false, Amount: > 0 })
        {
            if (!FreeGuard(world, w)) return;
            if (Talks) Spotted(world, w, s, BountySpotted);
            else Start(world, w, s, force: false);
            return;
        }
        c.RecentlyWanted = CrimeSystem.RecentlyWantedSeconds;
    }

    /// <summary>
    /// "In secure location" (<b>engine reading</b> of <c>disguiseMods</c>' test on the status object's town: the town is not public, <c>Town::isPublic</c>, and the character is inside its
    /// walls, <c>amInsideTownWalls</c>): the town the point stands in is a placed one that is not public, or a player town that is not, and the point is inside its walls (the gate regions
    /// where known, else the town).
    /// </summary>
    public bool SecureLocation(World world, Vector3 at)
    {
        if (Towns is not { } data) return false;
        var town = Ai.AiSystem.TownAt(data, world, at);
        if (town.IsNone) return false;
        bool open = town.Site >= 0 ? data.Sites[town.Site].Town.IsPublic : world.Buildings.FindTown(town.PlayerTown)?.Public ?? true;
        if (open) return false;
        var regions = world.Systems.OfType<Locks.LockSystem>().FirstOrDefault()?.Regions;
        return regions?.CodeAt(at) is not { } code || code < 0 || code > 0;
    }

    /// <summary>
    /// The looker stands in a town of a real faction that is not the player's and is no nest, and is not hostile to that faction (<c>disguiseMods</c>' second test; the town is the one
    /// the point stands in, <b>engine reading</b> of the status object's hysteresis).
    /// </summary>
    bool InFriendlyTown(World world, Vector3 at, int faction)
    {
        if (Towns is not { } data) return true;
        var town = Ai.AiSystem.TownAt(data, world, at);
        if (town.Site < 0 || data.Sites[town.Site].Town.Type == TownType.Nest) return false;
        int owner = town.Site < data.SiteFactions.Count ? data.SiteFactions[town.Site] : -1;
        var relations = Crimes.Relations;
        if (!relations.Contains(owner) || owner == world.Player.Faction || relations.Factions[owner].NotReal) return false;
        return owner == faction || !relations.Contains(faction) || !relations.IsHostile(faction, owner);
    }

    void HashDisguise(ref StateHasher hasher)
    {
        if (disguiseMeters.Count == 0) return;   // only while a looker watches a disguise, so worlds without them hash as before
        hasher.Add(0x4D747273);
        hasher.Add(disguiseMeters.Count);
        foreach (var ((looker, target), meter) in disguiseMeters.OrderBy(k => k.Key.Looker.Slot).ThenBy(k => k.Key.Target.Slot)) { hasher.Add(looker); hasher.Add(target); hasher.Add(meter); }
    }
}
