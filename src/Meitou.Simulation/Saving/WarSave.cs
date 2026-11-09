using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// The factions' WAR_SAVESTATEs (docs/formats/save.md "WAR_SAVESTATE (type 9)") to and from the war managers (<see cref="PopulationSystem.Wars"/>),
/// with the original's keys. Keys Meitou does not know are kept (the capture writes into the loaded save's records). The original saves no phase
/// timer (its phases start again at load); Meitou writes its own floats <c>meitou phase start c&lt;i&gt;</c>, <c>meitou fight start c&lt;i&gt;</c>,
/// <c>meitou arrived c&lt;i&gt;</c>, <c>meitou announced c&lt;i&gt;</c> and <c>meitou carry start p&lt;i&gt;</c> (on the campaign platoon
/// <c>p&lt;i&gt;</c>; absolute game hours, as <c>updatetime</c>; absent: not started) and reads them back, so a loaded campaign goes on where it was.
/// Older saves' <c>meitou carry start c&lt;i&gt;</c> (one stamp per campaign) is read onto each of its platoons and dropped on the next write.
/// </summary>
internal static class WarSave
{
    static readonly string[] ActiveStrings = ["dat c", "enemy c"], ActiveInts = ["phase c", "id c"], ActiveBools = ["rd c", "trig c"],
        ActiveHands = ["target c", "leader c", "home c"];
    const string PhaseStartKey = "meitou phase start c", FightStartKey = "meitou fight start c", ArrivedKey = "meitou arrived c",
        AnnouncedKey = "meitou announced c", CarryStartKey = "meitou carry start c", CarryPlatoonKey = "meitou carry start p";
    static readonly string[] Timers = [PhaseStartKey, FightStartKey, ArrivedKey, AnnouncedKey, CarryStartKey];
    static readonly string[] HandParts = ["TYPE", "C", "CS", "I", "S"];

    /// <summary>
    /// The platoon handles (container and serial) the states' <c>p&lt;i&gt;</c> name, of type 1 or 34 (the reader takes no other), each with the
    /// <c>home c&lt;j&gt;</c> town handle of the running campaign its <c>forces&lt;i&gt;</c> names (<see cref="Hand.Null"/> when none does).
    /// </summary>
    public static Dictionary<(int C, int CS), Hand> PlatoonHomes(IEnumerable<FcsRecord> wars)
    {
        var homes = new Dictionary<(int, int), Hand>();

        foreach (var war in wars)
        {
            var byId = new Dictionary<int, Hand>();
            for (int j = 0, n = war.Ints.GetValueOrDefault("num actives"); j < n; j++)
                byId.TryAdd(war.Ints.GetValueOrDefault($"id c{j}"), Hand.Read(war, $"home c{j}"));

            for (int i = 0, n = war.Ints.GetValueOrDefault("num plats"); i < n; i++)
            {
                if (Hand.Read(war, $"p{i}") is { Type: 1 or (int)FcsRecordType.Platoon } h)
                    homes.TryAdd((h.C, h.Cs), byId.GetValueOrDefault(war.Ints.GetValueOrDefault($"forces{i}"), Hand.Null));
            }
        }

        return homes;
    }

    /// <summary>
    /// Reads each faction's state into its manager (<c>FUN_1409c9dc0</c>'s rules): <c>updatetime</c> when present, <c>fwc id</c> raised past every
    /// running id; a running campaign whose <c>dat c</c> does not resolve is skipped (an unknown one joins the faction's list), one in phase 1 with
    /// no <c>pt2</c> ends; a request whose campaign or enemy does not resolve is skipped; <c>poss time</c> sets the known campaigns' last starts;
    /// <c>p&lt;i&gt;</c> joins its platoon to the campaign <c>forces&lt;i&gt;</c> names.
    /// </summary>
    public static void Read(World world, PopulationSystem population, SavedPopulation saved)
    {
        var data = population.Data;
        double now = world.GameHours;

        foreach (var (factionId, war) in saved.Wars)
        {
            if (population.WarOf(data.FactionIndex(factionId)) is not { } m) continue;
            if (war.Floats.TryGetValue("updatetime", out float update)) m.UpdateTime = update;

            m.NextId = war.Ints.GetValueOrDefault("fwc id");
            var leaders = new Dictionary<CampaignInstance, int>();

            // saves before the stamp was per platoon: every platoon starts there, and those carrying nobody drop it
            var legacyCarry = new Dictionary<CampaignInstance, double>();

            ReadActives(world, population, saved, war, m, now, leaders, legacyCarry);
            ReadRequests(world, population, saved, war, m);

            for (int i = 0, n = war.Ints.GetValueOrDefault("num poss"); i < n; i++)
            {
                if (m.Known.FirstOrDefault(k => k.Id == war.Strings.GetValueOrDefault($"poss sid{i}", "")) is { } known)
                    known.LastStart = war.Floats.GetValueOrDefault($"poss time{i}");
            }

            ReadPlatoons(world, population, saved, war, m, legacyCarry);

            foreach (var inst in m.Actives)
                inst.Leader = leaders.TryGetValue(inst, out int l) && inst.Platoons.Contains(l) ? l : inst.Platoons.Count > 0 ? inst.Platoons[0] : -1;
        }
    }

    /// <summary>
    /// The running campaigns: one whose <c>dat c</c> does not resolve is skipped, one in phase 1 with no <c>pt2</c> ends; their leaders noted.
    /// </summary>
    static void ReadActives(World world, PopulationSystem population, SavedPopulation saved, FcsRecord war, WarManager m, double now,
        Dictionary<CampaignInstance, int> leaders, Dictionary<CampaignInstance, double> legacyCarry)
    {
        var data = population.Data;
        for (int i = 0, n = war.Ints.GetValueOrDefault("num actives"); i < n; i++)
        {
            string id = war.Strings.GetValueOrDefault($"dat c{i}", "");
            if (population.CampaignOf(id) is not { } c) continue;

            string enemy = war.Strings.GetValueOrDefault($"enemy c{i}", "");
            var inst = new CampaignInstance
            {
                Faction = m.Faction,
                Id = war.Ints.GetValueOrDefault($"id c{i}"),
                CampaignId = id,
                Target = Town(world, saved, Hand.Read(war, $"target c{i}")),
                Home = Town(world, saved, Hand.Read(war, $"home c{i}")),
                Enemy = enemy.Length > 0 ? data.FactionIndex(enemy) : -1,
                Phase = Math.Clamp(war.Ints.GetValueOrDefault($"phase c{i}"), 0, 2),
                Reached = war.Bools.GetValueOrDefault($"rd c{i}"),
                Trig = war.Bools.GetValueOrDefault($"trig c{i}"),
                PhaseStart = Hour(war, PhaseStartKey + i) ?? now,
                Required = (int)(c.NumForces * population.Settings.RaidSizeMult),
                FightStart = Hour(war, FightStartKey + i) ?? -1,
                ArrivedAt = Hour(war, ArrivedKey + i) ?? -1,
                AnnouncedAt = Hour(war, AnnouncedKey + i) ?? -1,
            };
            if (Hour(war, CarryStartKey + i) is double carry) legacyCarry[inst] = carry;
            if (inst.Phase == 1 && !c.HasPt2) inst.Phase = 2;
            if (Platoon(saved, Hand.Read(war, $"leader c{i}")) is int leader) leaders[inst] = leader;

            m.Know(id);
            m.Actives.Add(inst);
            m.NextId = Math.Max(m.NextId, inst.Id + 1);
        }
    }

    /// <summary>
    /// The campaign requests: one whose campaign or enemy does not resolve is skipped.
    /// </summary>
    static void ReadRequests(World world, PopulationSystem population, SavedPopulation saved, FcsRecord war, WarManager m)
    {
        var data = population.Data;
        for (int i = 0, n = war.Ints.GetValueOrDefault("num requests"); i < n; i++)
        {
            string id = war.Strings.GetValueOrDefault($"req sid{i}", "");
            int enemy = data.FactionIndex(war.Strings.GetValueOrDefault($"req enemysid{i}", ""));
            if (population.CampaignOf(id) is null || enemy < 0) continue;

            m.Requests.Add(new CampaignRequest
            {
                CampaignId = id,
                Enemy = enemy,
                Due = war.Floats.GetValueOrDefault($"req time{i}"),
                Target = Town(world, saved, Hand.Read(war, $"req targ{i}")),
                Home = Town(world, saved, Hand.Read(war, $"req home{i}")),
            });
        }
    }

    /// <summary>
    /// The campaign platoons: each joins the running campaign its <c>forces</c> names, with its carry stamp (an older save's campaign-wide one).
    /// </summary>
    static void ReadPlatoons(World world, PopulationSystem population, SavedPopulation saved, FcsRecord war, WarManager m,
        Dictionary<CampaignInstance, double> legacyCarry)
    {
        for (int i = 0, n = war.Ints.GetValueOrDefault("num plats"); i < n; i++)
        {
            var h = Hand.Read(war, $"p{i}");

            if (h.Type is not (1 or (int)FcsRecordType.Platoon) || Platoon(saved, h) is not int pid || world.Platoons.Find(pid) is not { } p
                || p.Campaign is not null)
            {
                continue;
            }

            int forces = war.Ints.GetValueOrDefault($"forces{i}");
            if (m.Actives.FirstOrDefault(a => a.Id == forces) is not { } inst) continue;

            p.Campaign = inst;
            inst.Platoons.Add(p.Id);
            inst.Men += population.LivingOf(world, p);
            double? carry = Hour(war, CarryPlatoonKey + i);
            if (carry is null && legacyCarry.TryGetValue(inst, out double legacy)) carry = legacy;
            if (carry is { } stamp) inst.CarryStarts[p.Id] = stamp;
        }
    }

    static double? Hour(FcsRecord war, string key) => war.Floats.TryGetValue(key, out float h) && float.IsFinite(h) ? h : null;

    static void Hour(FcsRecord war, string key, double hour)
    {
        if (hour >= 0) war.Floats[key] = (float)hour;
        else war.Floats.Remove(key);
    }

    static int? Platoon(SavedPopulation saved, Hand h) => !h.IsNull && saved.PlatoonIds.TryGetValue((h.C, h.Cs), out int id) ? id : null;

    static CampaignTown Town(World world, SavedPopulation saved, Hand h)
    {
        if (h.IsNull) return CampaignTown.None;
        if (saved.TownSites.TryGetValue((h.C, h.Cs), out int site)) return CampaignTown.OfSite(site);
        foreach (var town in world.Buildings.Towns)
            if (town.SaveHandle is { } t && t.C == h.C && t.Cs == h.Cs) return CampaignTown.OfPlayer(town.Number);

        return CampaignTown.None;
    }

    /// <summary>
    /// Writes each faction's manager into its WAR_SAVESTATE: <c>faction</c>, <c>fwc id</c>, <c>updatetime</c>, the running campaigns, the requests,
    /// every known campaign with its last start and the campaign platoons (<paramref name="platoons"/>: the PLATOON handle each world platoon was
    /// written with; <paramref name="outposts"/>: each player town's). Numbered keys past the new counts are removed.
    /// </summary>
    public static void Write(SaveGame game, World world, PopulationData data, PopulationSystem population, IReadOnlyDictionary<int, Hand> platoons,
        IReadOnlyDictionary<int, Hand> outposts)
    {
        population.EnsureWars(world);
        var townHands = new Dictionary<string, Hand>(StringComparer.Ordinal);
        foreach (var t in game.Towns)
            if (!t.IsNest && t.Instance.Length > 0 && !t.Handle.IsNull) townHands.TryAdd(t.Instance, t.Handle);

        var writer = new WarWriter(townHands, data, platoons, outposts);
        foreach (var f in game.Factions)
        {
            if (f.War is not { } war || population.WarOf(data.FactionIndex(f.Id)) is not { } m) continue;

            war.Strings["faction"] = f.Id;
            war.Ints["fwc id"] = m.NextId;
            war.Floats["updatetime"] = (float)m.UpdateTime;

            writer.WriteActives(war, m);
            writer.WriteRequests(war, m);
            writer.WriteKnown(war, m);
            writer.WritePlatoons(war, m);
        }
    }

    /// <summary>
    /// Writes the managers' numbered keys with the save's town and platoon handles (<see cref="Write"/>).
    /// </summary>
    sealed class WarWriter(Dictionary<string, Hand> townHands, PopulationData data, IReadOnlyDictionary<int, Hand> platoons,
        IReadOnlyDictionary<int, Hand> outposts)
    {
        /// <summary>
        /// The running campaigns; the keys past the new count are removed.
        /// </summary>
        public void WriteActives(FcsRecord war, WarManager m)
        {
            int old = war.Ints.GetValueOrDefault("num actives");
            war.Ints["num actives"] = m.Actives.Count;

            for (int i = 0; i < m.Actives.Count; i++)
            {
                var a = m.Actives[i];
                war.Strings[$"dat c{i}"] = a.CampaignId;
                TownHand(a.Target).Write(war, $"target c{i}");
                war.Strings[$"enemy c{i}"] = FactionId(a.Enemy);
                PlatoonHand(a.Leader).Write(war, $"leader c{i}");
                TownHand(a.Home).Write(war, $"home c{i}");
                war.Ints[$"phase c{i}"] = a.Phase;
                war.Ints[$"id c{i}"] = a.Id;
                war.Bools[$"rd c{i}"] = a.Reached;
                war.Bools[$"trig c{i}"] = a.Trig;
                Hour(war, PhaseStartKey + i, a.PhaseStart);
                Hour(war, FightStartKey + i, a.FightStart);
                Hour(war, ArrivedKey + i, a.ArrivedAt);
                Hour(war, AnnouncedKey + i, a.AnnouncedAt);
                war.Floats.Remove(CarryStartKey + i);   // the campaign-wide stamp older saves wrote: now one per platoon
            }

            for (int i = m.Actives.Count; i < old; i++) Remove(war, i, ActiveStrings, ActiveInts, ActiveBools, ActiveHands, Timers);
        }

        /// <summary>
        /// The campaign requests; the keys past the new count are removed.
        /// </summary>
        public void WriteRequests(FcsRecord war, WarManager m)
        {
            int old = war.Ints.GetValueOrDefault("num requests");
            war.Ints["num requests"] = m.Requests.Count;

            for (int i = 0; i < m.Requests.Count; i++)
            {
                var r = m.Requests[i];
                war.Strings[$"req sid{i}"] = r.CampaignId;
                war.Strings[$"req enemysid{i}"] = FactionId(r.Enemy);
                war.Floats[$"req time{i}"] = (float)r.Due;
                TownHand(r.Target).Write(war, $"req targ{i}");
                TownHand(r.Home).Write(war, $"req home{i}");
            }

            for (int i = m.Requests.Count; i < old; i++) Remove(war, i, ["req sid", "req enemysid"], [], [], ["req targ", "req home"], ["req time"]);
        }

        /// <summary>
        /// Every known campaign with its last start; the keys past the new count are removed.
        /// </summary>
        public void WriteKnown(FcsRecord war, WarManager m)
        {
            int old = war.Ints.GetValueOrDefault("num poss");
            war.Ints["num poss"] = m.Known.Count;

            for (int i = 0; i < m.Known.Count; i++)
            {
                war.Strings[$"poss sid{i}"] = m.Known[i].Id;
                war.Floats[$"poss time{i}"] = (float)m.Known[i].LastStart;
            }

            for (int i = m.Known.Count; i < old; i++) Remove(war, i, ["poss sid"], [], [], [], ["poss time"]);
        }

        /// <summary>
        /// The campaign platoons with their campaign and carry stamp; the keys past the new count are removed.
        /// </summary>
        public void WritePlatoons(FcsRecord war, WarManager m)
        {
            int old = war.Ints.GetValueOrDefault("num plats");
            int k = 0;

            foreach (var a in m.Actives)
            {
                foreach (int id in a.Platoons)
                {
                    if (PlatoonHand(id) is not { IsNull: false } h) continue;

                    h.Write(war, $"p{k}");
                    war.Ints[$"forces{k}"] = a.Id;
                    Hour(war, CarryPlatoonKey + k, a.CarryStarts.GetValueOrDefault(id, -1));
                    k++;
                }
            }

            war.Ints["num plats"] = k;
            for (int i = k; i < old; i++) Remove(war, i, [], ["forces"], [], ["p"], [CarryPlatoonKey]);
        }

        /// <summary>
        /// The handle of a campaign's town: a placed town's as the save has it, else the player town's.
        /// </summary>
        Hand TownHand(CampaignTown t)
        {
            if (t.Site >= 0) return townHands.GetValueOrDefault(data.Sites[t.Site].InstanceId, Hand.Null);

            return outposts.GetValueOrDefault(t.PlayerTown, Hand.Null);
        }

        /// <summary>
        /// The handle a platoon was written with; none for -1.
        /// </summary>
        Hand PlatoonHand(int id) => id >= 0 && platoons.TryGetValue(id, out var h) ? h : Hand.Null;

        /// <summary>
        /// A faction's string id; empty for none.
        /// </summary>
        string FactionId(int f) => f >= 0 && f < data.Factions.Count ? data.Factions[f].Id : "";
    }

    static void Remove(FcsRecord r, int i, string[] strings, string[] ints, string[] bools, string[] hands, string[] floats)
    {
        foreach (var s in strings) r.Strings.Remove(s + i);
        foreach (var s in ints) r.Ints.Remove(s + i);
        foreach (var s in bools) r.Bools.Remove(s + i);
        foreach (var s in floats) r.Floats.Remove(s + i);
        foreach (var s in hands)
            foreach (var part in HandParts) r.Ints.Remove(s + i + part);
    }
}