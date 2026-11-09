namespace Meitou.Game.Tutorials;

/// <summary>
/// What the tutorial triggers look at, filled by the interface at each poll (docs/game/ui-screens.md "Tutorials"). "PCs" are the player's characters;
/// the building tests are over the player's buildings.
/// </summary>
readonly record struct TutorialPoll
{
    /// <summary>
    /// The character editor is open or a world is loading (no trigger fires then).
    /// </summary>
    public bool EditorOrLoading { get; init; }

    /// <summary>
    /// A PC in combat mode, unconscious or in a bed or prison (resets the calm counter of What do I do?).
    /// </summary>
    public bool Unsettled { get; init; }

    /// <summary>
    /// A PC stands in a town (the world's tutorial facts).
    /// </summary>
    public bool InTown { get; init; }

    /// <summary>
    /// A PC needs first aid.
    /// </summary>
    public bool NeedsFirstAid { get; init; }

    /// <summary>
    /// A PC is in combat.
    /// </summary>
    public bool UnderAttack { get; init; }

    /// <summary>
    /// How many PCs are alive.
    /// </summary>
    public int PcCount { get; init; }

    /// <summary>
    /// The PCs, for the recruit tutorial's newest member.
    /// </summary>
    public IReadOnlyList<Simulation.CharacterId> Pcs { get; init; }

    /// <summary>
    /// The squad carries stolen goods (the world's tutorial facts).
    /// </summary>
    public bool Stolen { get; init; }

    /// <summary>
    /// A PC is unconscious (not playing dead).
    /// </summary>
    public bool AnyUnconscious { get; init; }

    /// <summary>
    /// A PC is conscious.
    /// </summary>
    public bool AnyConscious { get; init; }

    /// <summary>
    /// A PC is in a prison.
    /// </summary>
    public bool InPrison { get; init; }

    /// <summary>
    /// A PC is in a cannibal cage.
    /// </summary>
    public bool InCannibalCage { get; init; }

    /// <summary>
    /// A PC is sneaking.
    /// </summary>
    public bool Sneaking { get; init; }

    /// <summary>
    /// The steal pointer shows: a selection over another faction's ground item (cursor 0x15).
    /// </summary>
    public bool StealCursor { get; init; }

    /// <summary>
    /// The inspected character is another faction's with a bounty.
    /// </summary>
    public bool SelectedHasBounty { get; init; }

    /// <summary>
    /// A PC that is not a robot is hungry (hunger below 2).
    /// </summary>
    public bool Hungry { get; init; }

    /// <summary>
    /// A PC is playing dead.
    /// </summary>
    public bool PlayingDead { get; init; }

    /// <summary>
    /// The player has a production building (a machine with an output, a farm or a crafting bench).
    /// </summary>
    public bool HasProductionBuilding { get; init; }

    /// <summary>
    /// A PC is a slave.
    /// </summary>
    public bool Slave { get; init; }

    /// <summary>
    /// A PC wears a faction's uniform (the world's tutorial facts).
    /// </summary>
    public bool Uniform { get; init; }

    /// <summary>
    /// A player machine's output or farm is full.
    /// </summary>
    public bool MachineFull { get; init; }

    /// <summary>
    /// The inspected building is for sale.
    /// </summary>
    public bool SelectedForSale { get; init; }

    /// <summary>
    /// A finished player building with an output exists.
    /// </summary>
    public bool ProductionComplete { get; init; }

    /// <summary>
    /// Build mode is placing a building.
    /// </summary>
    public bool Placing { get; init; }

    /// <summary>
    /// A finished player crafting bench exists.
    /// </summary>
    public bool CraftingComplete { get; init; }

    /// <summary>
    /// A finished player research bench (tech level above 0) exists.
    /// </summary>
    public bool ResearchComplete { get; init; }

    /// <summary>
    /// The management (overview) tab shown, -1 when it is closed.
    /// </summary>
    public int ManagementTab { get; init; }

    /// <summary>
    /// The player knows a blueprint (the world's tutorial facts).
    /// </summary>
    public bool Blueprint { get; init; }

    /// <summary>
    /// A player construction is placed but not started (no progress).
    /// </summary>
    public bool UnstartedConstruction { get; init; }

    /// <summary>
    /// The prospecting window is open.
    /// </summary>
    public bool Prospecting { get; init; }

    /// <summary>
    /// The selected (else the inspected) character has a stump for a limb.
    /// </summary>
    public bool SelectedStump { get; init; }
}

/// <summary>
/// The tutorial manager (<c>TutorialManager</c>, docs/game/ui-screens.md "Tutorials"): 34 items, each pending until its trigger holds at a poll
/// (every 2 real seconds), then active (listed in the panel) until dismissed, then ended. Its state is <c>__tutorials.data</c>: the active ids, then
/// the pending ones.
/// </summary>
sealed class TutorialManager
{
    /// <summary>
    /// The items in the exe's order; the index is the id (the classes' names, <c>Tutorial_*</c>).
    /// </summary>
    public static readonly string[] Names =
    [
        "BasicControls", "GUIOverview", "WhatDoIDo", "WhatDoIDo_InTown", "Injured_Intro", "Injured_FirstAid", "UnderAttack", "HiredRecruit",
        "StolenGoods", "KOd", "KOdWithNonUnconsciousPC", "EscapePrision", "EscapeCannibalCage", "Sneaking", "HowToSteal", "HowToBounties",
        "YouAreHungry", "PlayingDead", "PermaJobs", "BeignSlave", "Disguises", "MachineFull", "BuildingForSale", "ProductionBuilding",
        "BuildingGateWalls", "HowToCraft", "HowToCraft_NoCraftingBench", "Research_Benches", "Research_Built", "Research_Blueprint", "BuildMode",
        "BuildMode_Construction", "Prospecting", "MissingLimb",
    ];

    /// <summary>
    /// The state file's name.
    /// </summary>
    public const string FileName = "__tutorials.data";

    /// <summary>
    /// The state file's magic number.
    /// </summary>
    const uint Magic = 0xBAAAAAAD;

    /// <summary>
    /// The seconds between trigger polls.
    /// </summary>
    const double PollSeconds = 2;

    /// <summary>
    /// What do I do? fires once its calm counter (2 per poll) passes this.
    /// </summary>
    const int CalmNeeded = 20;

    /// <summary>
    /// The active ids in the panel's order, the pending ones, and those a poll drops from pending (their trigger met another way).
    /// </summary>
    readonly List<int> _active = [], _pending = [], _drops = [];

    /// <summary>
    /// The state file's path.
    /// </summary>
    readonly string _path;

    /// <summary>
    /// Seconds until the next poll.
    /// </summary>
    double _timer;

    /// <summary>
    /// What do I do?'s calm counter; the PC count when the recruit tutorial started watching (-1 before).
    /// </summary>
    int _calm, _recruits = -1;

    /// <summary>
    /// The PCs when the recruit tutorial started watching, so the newest member is found.
    /// </summary>
    HashSet<Simulation.CharacterId>? _recruitBase;

    /// <summary>
    /// The recruit the hired-recruit tutorial found (its Select page waits for them to be selected); null: none.
    /// </summary>
    public Simulation.CharacterId? Recruit { get; private set; }

    /// <summary>
    /// The manager of the state file at <paramref name="path"/>, read at once.
    /// </summary>
    public TutorialManager(string path)
    {
        this._path = path;
        Load();
    }

    /// <summary>
    /// The <c>tutorials</c> option; off, nothing updates.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// No file was read: the one-off camera hint is due.
    /// </summary>
    public bool Fresh { get; private set; }

    /// <summary>
    /// The active ids, in the panel's order.
    /// </summary>
    public IReadOnlyList<int> Active => _active;

    /// <summary>
    /// Each item's page (kept when it ends, as the original keeps it).
    /// </summary>
    public int[] Pages { get; } = new int[Names.Length];

    /// <summary>
    /// The state changed since it was last saved.
    /// </summary>
    bool _dirty;

    /// <summary>
    /// Reads the state file: the active and then the pending ids; a missing or broken file makes every item pending (and <see cref="Fresh"/>).
    /// </summary>
    void Load()
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(_path));
            if (r.ReadUInt32() != Magic) throw new InvalidDataException();
            r.ReadUInt32();
            var a = ReadList(r);
            var b = ReadList(r);
            _active.AddRange(a);
            _pending.AddRange(b);
            Fresh = false;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException)
        {
            _active.Clear();
            _pending.Clear();
            _pending.AddRange(Enumerable.Range(0, Names.Length));
            _dirty = Fresh = true;
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

    /// <summary>
    /// Writes the state file: magic, item count, the active ids, then the pending ones; a failure is only logged.
    /// </summary>
    void Save()
    {
        _dirty = false;
        try
        {
            using var w = new BinaryWriter(File.Create(_path));
            w.Write(Magic);
            w.Write((uint)Names.Length);
            w.Write((uint)_active.Count);
            foreach (int id in _active) w.Write((uint)id);
            w.Write((uint)_pending.Count);
            foreach (int id in _pending) w.Write((uint)id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"tutorials save failed: {e.Message}");
        }
    }

    /// <summary>
    /// Whether item <paramref name="id"/> has ended (neither active nor pending).
    /// </summary>
    bool Ended(int id) => !_active.Contains(id) && !_pending.Contains(id);

    /// <summary>
    /// One frame: drops, then every 2 s the pending items whose trigger holds turn active (returned, for the panel's flash and
    /// <c>Thing_Complete</c>); saves when changed.
    /// </summary>
    public List<int>? Update(double seconds, Func<TutorialPoll> poll)
    {
        if (!Enabled) return null;
        if (_drops.Count > 0)
        {
            foreach (int id in _drops) _pending.Remove(id);
            _drops.Clear();
            _dirty = true;
        }

        List<int>? started = null;
        _timer -= seconds;
        if (_timer <= 0)
        {
            var p = poll();
            foreach (int id in _pending.ToArray())
                if (Holds(id, p))
                {
                    _pending.Remove(id);
                    _active.Add(id);
                    (started ??= []).Add(id);
                    _dirty = true;
                }
            _timer = PollSeconds;
        }

        if (_dirty) Save();
        return started;
    }

    /// <summary>
    /// An item's trigger (<c>conditionsMet</c>).
    /// </summary>
    bool Holds(int id, in TutorialPoll p)
    {
        switch (id)
        {
            case 0 or 1: return !p.EditorOrLoading;
            case 2:
                if (p.EditorOrLoading) return false;
                if (p.Unsettled)
                {
                    _calm = 0;
                    return false;
                }

                _calm += 2;
                return _calm > CalmNeeded;
            case 3: return Ended(2) && p.InTown;
            case 4: return p.NeedsFirstAid;
            case 5: return Ended(4) && p.NeedsFirstAid;
            case 6: return p.UnderAttack;
            case 7:
                if (_recruits < 0)
                {
                    _recruits = p.PcCount;
                    _recruitBase = [.. p.Pcs ?? []];
                }

                if (p.PcCount <= _recruits) return false;
                Recruit = (p.Pcs ?? []).LastOrDefault(c => _recruitBase?.Contains(c) != true);
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
                if (p.CraftingComplete)
                {
                    _drops.Add(id);
                    return false;
                }

                return p.ManagementTab == 3;
            case 27:
                if (p.ResearchComplete)
                {
                    _drops.Add(id);
                    return false;
                }

                return p.ManagementTab == 2;
            case 28: return p.ResearchComplete;
            case 29: return p.Blueprint;
            case 31: return p.UnstartedConstruction;
            case 32: return p.Prospecting;
            case 33: return p.SelectedStump;
            default: return false;
        }
    }

    /// <summary>
    /// Dismissed (the line's X, DISMISS, or FINISH on the last page): out of the active list, ended.
    /// </summary>
    public void Dismiss(int id)
    {
        if (_active.Remove(id)) _dirty = true;
    }

    /// <summary>
    /// The Tutorials window's ACTIVATE: an item not already active becomes active from its first page (true: added, for the flash and sound).
    /// </summary>
    public bool Activate(int id)
    {
        if (_active.Contains(id)) return false;
        _pending.Remove(id);
        _active.Add(id);
        // reset: page 0, except the recruit and prison items, whose own reset clears only their memory.
        if (id == 7) (_recruits, Recruit) = (-1, null);
        else if (id is not (11 or 12)) Pages[id] = 0;
        _dirty = true;
        return true;
    }

    /// <summary>
    /// Reset Tutorials: nothing active, everything pending, the file deleted (the next update writes it anew). Pages are kept.
    /// </summary>
    public void Reset()
    {
        _active.Clear();
        _pending.Clear();
        _pending.AddRange(Enumerable.Range(0, Names.Length));
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"tutorials reset: {e.Message}");
        }

        _dirty = true;
    }
}