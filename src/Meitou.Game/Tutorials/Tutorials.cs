namespace Meitou.Game;

/// <summary>
/// What the tutorial triggers look at, filled by the interface at each poll (docs/game/ui-screens.md "Tutorials"). "PCs" are the player's characters; the building
/// tests are over the player's buildings.
/// </summary>
readonly record struct TutorialPoll
{
    public bool EditorOrLoading { get; init; }
    /// <summary>A PC in combat mode, unconscious or in a bed or prison (resets the calm counter of What do I do?).</summary>
    public bool Unsettled { get; init; }
    public bool InTown { get; init; }
    public bool NeedsFirstAid { get; init; }
    public bool UnderAttack { get; init; }
    public int PcCount { get; init; }
    /// <summary>The PCs, for the recruit tutorial's newest member.</summary>
    public IReadOnlyList<Meitou.Simulation.CharacterId> Pcs { get; init; }
    public bool Stolen { get; init; }
    public bool AnyUnconscious { get; init; }
    public bool AnyConscious { get; init; }
    public bool InPrison { get; init; }
    public bool InCannibalCage { get; init; }
    public bool Sneaking { get; init; }
    public bool StealCursor { get; init; }
    public bool SelectedHasBounty { get; init; }
    public bool Hungry { get; init; }
    public bool PlayingDead { get; init; }
    public bool HasProductionBuilding { get; init; }
    public bool Slave { get; init; }
    public bool Uniform { get; init; }
    public bool MachineFull { get; init; }
    public bool SelectedForSale { get; init; }
    public bool ProductionComplete { get; init; }
    public bool Placing { get; init; }
    public bool CraftingComplete { get; init; }
    public bool ResearchComplete { get; init; }
    /// <summary>The management (overview) tab shown, -1 when it is closed.</summary>
    public int ManagementTab { get; init; }
    public bool Blueprint { get; init; }
    public bool UnstartedConstruction { get; init; }
    public bool Prospecting { get; init; }
    public bool SelectedStump { get; init; }
}

/// <summary>
/// The tutorial manager (<c>TutorialManager</c>, docs/game/ui-screens.md "Tutorials"): 34 items, each pending until its trigger holds at a poll (every 2 real seconds), then
/// active (listed in the panel) until dismissed, then ended. Its state is <c>__tutorials.data</c>: the active ids, then the pending ones.
/// </summary>
sealed class TutorialManager
{
    /// <summary>The items in the exe's order; the index is the id (the classes' names, <c>Tutorial_*</c>).</summary>
    public static readonly string[] Names =
    [
        "BasicControls", "GUIOverview", "WhatDoIDo", "WhatDoIDo_InTown", "Injured_Intro", "Injured_FirstAid", "UnderAttack", "HiredRecruit", "StolenGoods", "KOd",
        "KOdWithNonUnconsciousPC", "EscapePrision", "EscapeCannibalCage", "Sneaking", "HowToSteal", "HowToBounties", "YouAreHungry", "PlayingDead", "PermaJobs", "BeignSlave",
        "Disguises", "MachineFull", "BuildingForSale", "ProductionBuilding", "BuildingGateWalls", "HowToCraft", "HowToCraft_NoCraftingBench", "Research_Benches", "Research_Built",
        "Research_Blueprint", "BuildMode", "BuildMode_Construction", "Prospecting", "MissingLimb",
    ];

    public const string FileName = "__tutorials.data";
    const uint Magic = 0xBAAAAAAD;
    /// <summary>The seconds between trigger polls.</summary>
    const double PollSeconds = 2;
    /// <summary>What do I do? fires once its calm counter (2 per poll) passes this.</summary>
    const int CalmNeeded = 20;

    readonly List<int> active = [], pending = [], drops = [];
    readonly string path;
    double timer;
    int calm, recruits = -1;
    HashSet<Meitou.Simulation.CharacterId>? recruitBase;

    /// <summary>The recruit the hired-recruit tutorial found (its Select page waits for them to be selected); null: none.</summary>
    public Meitou.Simulation.CharacterId? Recruit { get; private set; }

    public TutorialManager(string path)
    {
        this.path = path;
        Load();
    }

    /// <summary>The <c>tutorials</c> option; off, nothing updates.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>No file was read: the one-off camera hint is due.</summary>
    public bool Fresh { get; private set; }
    public IReadOnlyList<int> Active => active;
    /// <summary>Each item's page (kept when it ends, as the original keeps it).</summary>
    public int[] Pages { get; } = new int[Names.Length];
    bool dirty;

    void Load()
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != Magic) throw new InvalidDataException();
            r.ReadUInt32();
            var a = ReadList(r);
            var b = ReadList(r);
            active.AddRange(a);
            pending.AddRange(b);
            Fresh = false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            active.Clear();
            pending.Clear();
            pending.AddRange(Enumerable.Range(0, Names.Length));
            dirty = Fresh = true;
        }

        static IEnumerable<int> ReadList(BinaryReader r)
        {
            uint n = r.ReadUInt32();
            if (n > Names.Length) throw new InvalidDataException();
            var ids = new int[n];
            for (int i = 0; i < n; i++) ids[i] = (int)r.ReadUInt32() is var id and >= 0 && id < Names.Length ? id : throw new InvalidDataException();
            return ids;
        }
    }

    void Save()
    {
        dirty = false;
        try
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Magic);
            w.Write((uint)Names.Length);
            w.Write((uint)active.Count);
            foreach (int id in active) w.Write((uint)id);
            w.Write((uint)pending.Count);
            foreach (int id in pending) w.Write((uint)id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"tutorials save failed: {e.Message}"); }
    }

    bool Ended(int id) => !active.Contains(id) && !pending.Contains(id);

    /// <summary>One frame: drops, then every 2 s the pending items whose trigger holds turn active (returned, for the panel's flash and <c>Thing_Complete</c>); saves when changed.</summary>
    public List<int>? Update(double seconds, Func<TutorialPoll> poll)
    {
        if (!Enabled) return null;
        if (drops.Count > 0)
        {
            foreach (int id in drops) pending.Remove(id);
            drops.Clear();
            dirty = true;
        }
        List<int>? started = null;
        timer -= seconds;
        if (timer <= 0)
        {
            var p = poll();
            foreach (int id in pending.ToArray())
                if (Holds(id, p))
                {
                    pending.Remove(id);
                    active.Add(id);
                    (started ??= []).Add(id);
                    dirty = true;
                }
            timer = PollSeconds;
        }
        if (dirty) Save();
        return started;
    }

    /// <summary>An item's trigger (<c>conditionsMet</c>).</summary>
    bool Holds(int id, in TutorialPoll p)
    {
        switch (id)
        {
            case 0 or 1: return !p.EditorOrLoading;
            case 2:
                if (p.EditorOrLoading) return false;
                if (p.Unsettled) { calm = 0; return false; }
                calm += 2;
                return calm > CalmNeeded;
            case 3: return Ended(2) && p.InTown;
            case 4: return p.NeedsFirstAid;
            case 5: return Ended(4) && p.NeedsFirstAid;
            case 6: return p.UnderAttack;
            case 7:
                if (recruits < 0) { recruits = p.PcCount; recruitBase = [.. p.Pcs ?? []]; }
                if (p.PcCount <= recruits) return false;
                Recruit = (p.Pcs ?? []).LastOrDefault(c => recruitBase?.Contains(c) != true);
                return true;
            case 8: return p.Stolen;
            case 9: return p.AnyUnconscious;
            case 10: return p.AnyUnconscious && p.AnyConscious;
            case 11: return p.InPrison;
            case 12: return p.InCannibalCage;
            case 13: return p.Sneaking;
            case 14: return p.StealCursor;
            case 15: return p.SelectedHasBounty;
            case 16: return p.Hungry;
            case 17: return p.PlayingDead;
            case 18: return p.HasProductionBuilding;
            case 19: return p.Slave;
            case 20: return p.Uniform;
            case 21: return p.MachineFull;
            case 22: return p.SelectedForSale;
            case 23: return p.ProductionComplete;
            case 24 or 30: return p.Placing;
            case 25: return p.CraftingComplete;
            case 26:
                if (p.CraftingComplete) { drops.Add(id); return false; }
                return p.ManagementTab == 3;
            case 27:
                if (p.ResearchComplete) { drops.Add(id); return false; }
                return p.ManagementTab == 2;
            case 28: return p.ResearchComplete;
            case 29: return p.Blueprint;
            case 31: return p.UnstartedConstruction;
            case 32: return p.Prospecting;
            case 33: return p.SelectedStump;
            default: return false;
        }
    }

    /// <summary>Dismissed (the line's X, DISMISS, or FINISH on the last page): out of the active list, ended.</summary>
    public void Dismiss(int id)
    {
        if (active.Remove(id)) dirty = true;
    }

    /// <summary>The Tutorials window's ACTIVATE: an item not already active becomes active from its first page (true: added, for the flash and sound).</summary>
    public bool Activate(int id)
    {
        if (active.Contains(id)) return false;
        pending.Remove(id);
        active.Add(id);
        // reset: page 0, except the recruit and prison items, whose own reset clears only their memory.
        if (id == 7) (recruits, Recruit) = (-1, null);
        else if (id is not (11 or 12)) Pages[id] = 0;
        dirty = true;
        return true;
    }

    /// <summary>Reset Tutorials: nothing active, everything pending, the file deleted (the next update writes it anew). Pages are kept.</summary>
    public void Reset()
    {
        active.Clear();
        pending.Clear();
        pending.AddRange(Enumerable.Range(0, Names.Length));
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"tutorials reset: {e.Message}"); }
        dirty = true;
    }
}
