using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation;

public sealed partial class World
{
    internal Partition PartitionAt(int i) => partitions[i];
    internal EffectBuffer BufferAt(int i) => buffers[i];

    /// <summary>
    /// Where a character is drawn: a character on the water, which the simulation keeps on the navmesh's flat surface, is lowered to the seabed or, swimming, to <see cref="Bodies.Speed.WadingHeight"/>
    /// (the same surface test as <see cref="MovementSystem"/>'s water state: within <see cref="Bodies.Speed.WaterSlack"/> of the water level). Not a carried or a downed body.
    /// One lying in a bed is raised by <see cref="BedLift"/>.
    /// </summary>
    Vector3 DrawnPosition(CharacterCold? cold, Vector3 at)
    {
        if (cold?.InSomething == Locks.HeldState.Bed) return at with { Y = at.Y + BedLift };
        if (cold is not { Race: { } race, Medical: not { Incapacitated: true } } || !cold.CarriedBy.IsNone || at.Y > Meitou.Data.World.WorldWater.Height + Bodies.Speed.WaterSlack) return at;
        float terrain = Walkability.TerrainHeight(at.X, at.Z);
        return terrain < at.Y ? at with { Y = Bodies.Speed.WadingHeight(terrain, 1, race.Swims) } : at;
    }

    /// <summary>
    /// How far a sleeper is drawn above its lie spot (engine choice, docs/game/locks.md "Beds"): the bed clips hold <c>Bip01</c> about level with the mattress top
    /// (<c>sleepinbed</c> 6.7 on <c>Bed</c>'s 7.36, <c>sleeponfloor</c> 1.46 on <c>Camp Bed</c>'s 1.73), so the body sank into it.
    /// </summary>
    const float BedLift = 1.5f;

    /// <summary>
    /// The faction the bounty panel calls local (<c>FUN_140852010</c>, docs/game/ui-screens.md section 4): the cage owner's while imprisoned, else (Meitou's reading of the status
    /// object's <c>+0x88</c>) the town's the character stands in; -1 when none.
    /// </summary>
    int LocalFaction(CharacterCold c, Vector3 at, PopulationData? data) =>
        c.IsImprisoned ? c.InWhat is { } cage && Fixtures.Find(cage) is { } f ? f.Owner : -1
        : data is not null && data.TownAt(at) is >= 0 and var site && site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;

    static readonly string[] ClosableSlots = ["head", "shirt", "boots"];

    /// <summary>A player character's disguise for the panel (<see cref="Crime.ArrestSystem"/>: the level, the uniform faction, the blown cooldown, the tooltip's factors).</summary>
    DisguiseView DisguiseOf(CharacterCold c, Vector3 at, PopulationData? data)
    {
        var marks = c.DisguiseMarks;
        bool Marked(int k) => marks is not null && marks[k] > 0;
        string name = data is { Factions: { } factions } && (uint)c.DisguiseFaction < (uint)factions.Count ? factions[c.DisguiseFaction].Name : "";
        bool secure = systems.OfType<Crime.ArrestSystem>().FirstOrDefault()?.SecureLocation(this, at) ?? false;
        return new DisguiseView(c.DisguiseLevel, name, c.RecentlyWanted, Crime.ArrestSystem.SeenFaction(c) != c.Faction, secure,
            Marked(Crime.ArrestSystem.SameFactionMark), Marked(Crime.ArrestSystem.SlaveOwnerMark), Marked(Crime.ArrestSystem.KnowsMeMark));
    }

    /// <summary>
    /// The status line of the bounty panel (<c>FUN_1405d4260</c>, <b>Observed</b>, docs/game/ui-screens.md section 4), first match: the leader of a campaign's leader platoon
    /// (<c>Character::isLeadingAWarCampaign</c>): War Leader for a hostile campaign (<c>CampaignData::_isHostile</c>), else Leader; kidnapped; Diplomatic Status for an NPC class Diplomat
    /// (<c>Character::isDiplomaticStatus</c> without its biome test, <b>Verified</b> by disassembly); for a free character a pending crime, then the escaped-prisoner flag.
    /// </summary>
    StatusView? StatusOf(CharacterId id, CharacterCold c, Vector3 at, PopulationData? data)
    {
        if (c.SquadId >= 0 && Squads.Find(c.SquadId) is { PlatoonId: >= 0 } squad && squad.Leader == id && Platoons.Find(squad.PlatoonId) is { Campaign: { } inst } platoon && inst.Leader == platoon.Id)
            return new(systems.OfType<PopulationSystem>().FirstOrDefault()?.CampaignOf(inst.CampaignId)?.IsHostile == true ? StatusKind.WarLeader : StatusKind.Leader);
        if (c.Kidnapped) return new(StatusKind.Kidnapped);
        if (c.NpcClass == FundamentalType.Diplomat) return new(StatusKind.Diplomatic);
        if (c.SlaveState != 0) return null;
        if (c.Pending is { } crime)
            return new(StatusKind.Crime, Crime.CrimeTable.Name(crime.Code), (int)crime.Timer, c.Bounties is null ? 0 : Crime.CrimeSystem.Lines(c, Tick, TickSeconds, LocalFaction(c, at, data)).FirstOrDefault(b => b.Local).Amount);
        return c.Escaped ? new(StatusKind.EscapedPrisoner) : null;
    }

    /// <summary>Slots a publish job takes at a time: the characters are made on the worker pool, each one reading only the tick's state.</summary>
    const int PublishChunk = 256;
    CharacterSnapshot?[] published = [];

    /// <summary>Builds the <see cref="WorldSnapshot"/> of the tick that just ended from the state it left in <see cref="CharacterTable.Previous"/>.</summary>
    void Publish()
    {
        var table = Characters;
        var state = table.Previous;
        // The selection's first member that could knock someone out from behind: the stealth KO chances are against it.
        int koSlot = -1;
        foreach (var s in Player.Selection)
            if (table.TryResolveNext(s, out int k) && table.Cold(k) is { } c && Carrying.CarrySystem.CanKnockOut(c)) { koSlot = k; break; }
        // The selection's lead: the kidnap chances are against it.
        int leadSlot = -1;
        foreach (var s in Player.Selection)
            if (table.TryResolveNext(s, out int l)) { leadSlot = l; break; }
        var aggro = koSlot >= 0 ? systems.OfType<AggroSystem>().FirstOrDefault() : null;
        var talk = systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault();
        var combat = systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        var beds = systems.OfType<Locks.BedSystem>().FirstOrDefault();
        var bodyConstants = systems.OfType<BodySystem>().FirstOrDefault()?.Constants;
        var population = systems.OfType<PopulationSystem>().FirstOrDefault()?.Data;
        float extraBloodLoss = systems.OfType<BodySystem>().FirstOrDefault()?.Constants.ExtraBloodLossFromBodyparts ?? 1;
        if (published.Length < state.Length) published = new CharacterSnapshot?[state.Length];
        var made = published;
        ParallelFor((state.Length + PublishChunk - 1) / PublishChunk, chunk =>
        {
            var state = table.Previous;
            for (int i = chunk * PublishChunk, end = Math.Min(state.Length, i + PublishChunk); i < end; i++) made[i] = state[i].Alive ? Character(i) : null;
        });
        var list = new List<CharacterSnapshot>(table.Count);
        for (int i = 0; i < state.Length; i++)
            if (made[i] is { } snapshot) { list.Add(snapshot); made[i] = null; }

        CharacterSnapshot Character(int i)
        {
            var state = table.Previous;
            var id = new CharacterId(i, state[i].Generation);
            var cold = table.Cold(i);
            // The details only the selection shows: asked once per character, the selection is a list.
            bool selected = cold is { IsPlayer: true } && Player.Selection.Contains(id), inspected = !selected && id == Player.Inspected, detailed = selected || inspected;
            bool sneaking = cold is { IsPlayer: true } && (cold.Orders & CharacterOrders.Sneak) != 0;
            var watchers = sneaking ? WatchersOf(cold!) : [];
            bool fightsPlayer = cold is { IsPlayer: false, InCombat: true } && combat?.StateOf(id) is { TargetSlot: >= 0 and var fought } fight
                && fought < state.Length && state[fought].Alive && state[fought].Generation == fight.TargetGeneration && table.Cold(fought) is { IsPlayer: true };
            return new CharacterSnapshot(id, cold?.Appearance, DrawnPosition(cold, state[i].Position), state[i].Yaw, cold?.Animation is { } anim && animations is not null ? animations.Publish(anim) : AnimationLayers.For(state[i].Animation, state[i].AnimationTime))
            {
                DrawYaw = cold?.Animation is { HasDrawYaw: true } drawn ? drawn.DrawYaw : float.NaN,
                Faction = cold?.Faction ?? -1,
                WeaponDrawn = cold is not null && cold.DrawnWeapon != Meitou.Data.Gameplay.WeaponKinds.None,
                CrossbowShots = cold?.CrossbowShots ?? 0,
                StuckBolts = cold?.StuckBolts is { Count: > 0 } bolts ? bolts.ToArray() : [],
                RangedDrawn = cold is not null && cold.DrawnWeapon != Meitou.Data.Gameplay.WeaponKinds.None
                    && cold.DrawnWeapon == Meitou.Data.Gameplay.HandHold.OfCategory((int)Meitou.Data.Gameplay.Combat.WeaponCategory.Bow).Weapon,
                BackDrawn = cold?.Fighter?.WeaponIsBack ?? false,
                HasFlies = cold?.HasFlies ?? false,
                Name = cold?.Name ?? "",
                SquadId = cold?.SquadId ?? -1,
                IsPlayer = cold?.IsPlayer ?? false,
                IsTrader = talk?.Trades(this, i) ?? (cold is { IsPlayer: false, SquadId: >= 0 } && Squads.Find(cold.SquadId)?.Shop is not null),
                Selected = selected,
                Inspected = inspected,
                Skills = detailed && cold!.Stats is { } sk ? new SkillSummary(sk[StatsEnumerated.MeleeAttack], sk[StatsEnumerated.MeleeDefence], sk[StatsEnumerated.Dodge], sk.Toughness, sk.Strength, sk.Athletics) : null,
                Inventory = selected && cold!.Inventory is { } carried ? Items.InventoryText.Lines(carried) : [],
                AllSkills = detailed && cold!.Stats is { } allStats ? Array.AsReadOnly(allStats.ToArray()) : [],
                SkillXpMultipliers = detailed && cold!.Race is { } skillRace ? Array.AsReadOnly((float[])skillRace.StatMultipliers.Clone()) : [],
                Hud = detailed && cold!.Medical is { } hudMed && cold.Race is { } hudRace && cold.Stats is { } hudStats ? HudOf(id, cold, hudMed, hudRace, hudStats, state[i], combat, bodyConstants, !Sight.Outdoors(i), Sight.Inside(i), population?.Db) : null,
                LimbBits = cold?.Medical?.LimbBits ?? 0,
                BloodAmounts = cold?.Medical is { } bloodMedical ? Bodies.MedicalState.BloodAmounts(bloodMedical) : 0UL,
                Knockdown = cold?.Knockdown ?? default,
                ClosedSlots = cold is null || !detailed ? [] : [.. ClosableSlots.Where(s => Items.InventoryCommands.SectionClosed(cold, s))],
                HealthParts = detailed && cold!.Medical is { } health && cold.Race is { } anatomy ? Array.AsReadOnly(health.Parts.Select(p => new HealthPartView(p.Template.StringId, p.Template.Name, p.Flesh, p.Stun, p.Bandage, p.Rig, p.MaxHp, p.IsRobotic(anatomy)) { Vital = p.Template.Vital, Type = p.Template.Type, Limb = p.Limb }).ToArray()) : [],
                ItemGrid = (selected || id == Player.OpenLoot || cold is { IsPlayer: true } && Player.InventoryShown.Contains(id)) && cold!.Inventory is { } grid ? new InventoryView(grid.Width, grid.Height, grid.Backpack?.Contents?.Width ?? 0, grid.Backpack?.Contents?.Height ?? 0,
                    Array.AsReadOnly(grid.Shown().Select(shown => new InventoryItemView(shown.Index, shown.Item.Record, shown.Item.Name, shown.Item.Section, shown.Item.X, shown.Item.Y, shown.Item.Width, shown.Item.Height, shown.Item.Quantity, shown.Item.Weight, shown.Item.Quality) { Material = shown.Item.MaterialId, Colour = shown.Item.ColourId, Charge = shown.Item.ChargeFraction, Company = shown.Item.CompanyId, Crafter = shown.Item.Crafter, Level = shown.Item.Level, Charges = shown.Item.Charges, MaxCharges = shown.Item.MaxCharges,
                        StolenFrom = shown.Item.IsStolenFor(cold.Faction, population) ? shown.Item.StolenFrom : -1, Unique = shown.Item.Unique }).ToArray()))
                    { TotalWeight = grid.ContentWeight(), CarryCapacity = cold.Stats is { } strength && systems.OfType<BodySystem>().FirstOrDefault() is { } bodies
                        ? Bodies.Encumbrance.WindowCapacity(bodies.Constants, strength.Strength, cold.Medical?.StatMultiplier(StatsEnumerated.Strength) ?? 1) : null,
                        Burden = cold.Stats is { } bs && systems.OfType<BodySystem>().FirstOrDefault() is { } bb && grid.ContentWeight() is var bw && (bw > 0 || !cold.Carrying.IsNone)
                        ? Bodies.Encumbrance.Factor(bb.Constants, bw, !cold.Carrying.IsNone, bs.Strength, cold.Medical?.StatMultiplier(StatsEnumerated.Strength) ?? 1) : 1 } : null,
                Bounties = detailed && cold!.Bounties is not null ? Crime.CrimeSystem.Lines(cold, Tick, TickSeconds, LocalFaction(cold, state[i].Position, population)) : [],
                Slave = cold is { SlaveState: 1 or 2 } ? new SlaveView(cold.SlaveState, population is { Factions: { } factions } && (uint)cold.SlaveOwner < (uint)factions.Count ? factions[cold.SlaveOwner].Name : "",
                    systems.OfType<Kidnap.CaptiveSystem>().FirstOrDefault()?.LooksLikeSlaveLevel(cold) ?? 0) : null,
                Disguise = detailed && cold!.Faction == Player.Faction && (cold.DisguiseLevel > 0 || cold.RecentlyWanted > 0) ? DisguiseOf(cold, state[i].Position, population) : null,
                Status = detailed ? StatusOf(id, cold!, state[i].Position, population) : null,
                NoLootGuilt = cold is { } guilt && (guilt.NpcClass is FundamentalType.Bandit or FundamentalType.Slave || population is { Factions: { } fl } && (uint)guilt.Faction < (uint)fl.Count && fl[guilt.Faction].NotReal),
                WarParty = cold is { SquadId: >= 0 } warCold && Squads.Find(warCold.SquadId) is { PlatoonId: >= 0 } warSquad && Platoons.Find(warSquad.PlatoonId)?.Campaign is not null,
                Body = cold?.Medical is { } med && cold.Race is { } race ? BodyOf(cold, med, race, state[i].Task, extraBloodLoss) : null,
                // The original skips robots' appearances (FUN_140531820) and animals have none.
                Build = cold is { Stats: { } buildStats, Medical: { } fed, Race.IsRobot: false, Appearance: { Animal: null } look }
                    ? Meitou.Data.Characters.CharacterShape.BuildFromStats(look.Female, buildStats, static (stats, s) => stats[s], fed.Hunger) : null,
                Path = selected && (state[i].Flags & (ushort)MoveFlags.HasPath) != 0 ? cold!.Path.Skip(state[i].PathCursor).ToArray() : [],
                CarriedBy = cold?.CarriedBy ?? CharacterId.None,
                Carry = cold is { CarriedBy.IsNone: false } ? cold.CarryAttach ?? Carrying.CarryPose.Fallback : null,
                Carrying = cold?.Carrying ?? CharacterId.None,
                HeldTool = state[i].Task == (byte)CharacterTask.Operate && cold is { OperateSite: not null, Appearance.Animal: null } ? cold.OperateTool : null,
                Task = (CharacterTask)state[i].Task,
                PickingLock = cold?.PickingLock is not null,
                WorkingShackles = cold?.Shackles is { Work: Locks.ShackleWork.Pick or Locks.ShackleWork.Cut },
                InBed = cold is { InSomething: Locks.HeldState.Bed },
                Imprisoned = cold is { InSomething: Locks.HeldState.Imprisoned },
                CageOpen = cold is { InSomething: Locks.HeldState.Imprisoned, InWhat: { } heldIn } && Fixtures.Find(heldIn) is { Kind: Locks.FixtureKind.Cage, Lock: { EffectivelyLocked: false } },
                StayingLow = cold?.Medical is { StayingLow: true, Prone: true, Crawling: false },
                InCombat = cold?.InCombat ?? false,
                CombatMode = cold is not null && combat is not null && CharacterFacts.InCombatMode(combat, table, id, cold, true, true),
                FightsPlayer = fightsPlayer,
                HostileToPlayer = cold is not null && CharacterFacts.HostileToPlayer(population?.Relations, Player.Faction, cold.Faction, fightsPlayer),
                Shackled = cold?.Inventory is { } worn && worn.Items.Any(item => item.IsLocked && item.Section != "main"),
                PortraitSerial = cold?.PortraitSerial ?? 0,
                ShacklesLocked = Locks.LockSystem.WornShackles(cold) is not null,
                CanBeShackled = cold is { IsPlayer: false } && Locks.LockSystem.CanBeShackled(cold, true),
                CarriesShackles = selected && Locks.LockSystem.CarriedShackles(cold) is not null,
                Orders = cold?.Orders ?? CharacterOrders.None,
                RunSpeed = cold?.RunSpeed ?? SpeedMode.Free,
                Jobs = cold is { Jobs.Count: > 0 } ? cold.Jobs.ToArray() : [],
                Scale = cold?.Animal is { } animal ? animal.Scale(cold.ShownAge) : 1,   // the size last refreshed (BodySystem.Age)
                WeatherNotice = cold?.WeatherNotice ?? WeatherNotice.None, WeatherNoticeProtection = cold?.WeatherNoticeProtection ?? 0,
                Decision = Decisions.Tracing ? cold?.Decision : null,
                Activity = cold is null ? null : Ai.AiSystem.ActivityText(state[i], cold),
                // The turret the combat system runs with this gunner; else (no combat system) at the gun of the turret its squad gave it: the AI's turret goal, walked to and stood at
                // (derived from the claims, not hashed).
                Turret = combat?.TurretOf(cold?.Gunning) is { } gunned ? new MannedTurret(cold!.Gunning!, gunned.BuildingId, gunned.Position, gunned.Yaw, gunned.GunId) { Simulated = true, Aim = gunned.Aim, Angles = gunned.Angles }
                    : cold is { Ai: { Behaviour: Ai.AiBehaviour.Turret, Squad.Ai: { } board } } && (CharacterTask)state[i].Task == CharacterTask.Idle && Ai.AiSystem.Holds(state[i], cold)
                    && board.TurretClaims.TryGetValue(i, out int claimed) && (uint)claimed < (uint)board.Turrets.Count && board.Turrets[claimed] is { Turret: { } post } building
                    ? new MannedTurret(building.Key, building.BuildingId, post.Position, post.Yaw, post.GunId) : null,
                RangedLine = detailed && cold is not null ? combat?.RangedLineOf(this, id, cold) : null,
                InPlayerBuilding = cold is { IsPlayer: false } && cold.Faction != Player.Faction && (cold.Medical is { Unconscious: true } || Carrying.CarrySystem.Asleep(table, i))
                    && production?.InPlayerBuilding(this, state[i].Position) == true,
                BedRecoveryRate = detailed && cold is { InSomething: Locks.HeldState.Bed } ? systems.OfType<BodySystem>().FirstOrDefault()?.BedRecoveryRate(this, cold, in state[i]) : null,
                Stealth = sneaking ? watchers.Length == 0 ? StealthExposure.Unobserved : watchers.Max(w => w.Level) : null,
                Light = sneaking ? Sight.Light(i) : 1,
                Watchers = watchers,
                Bar = cold is { IsPlayer: true } ? PickBar(cold, state[i].Task) ?? TrainBar(cold) : null,
                Prison = cold is { IsImprisoned: true } ? new PrisonView(!(cold.InWhat is { } cageId && Fixtures.Find(cageId) is { } cage && cage.Lock is not { EffectivelyLocked: true }), cold.SentenceLength, cold.SentenceStart + cold.SentenceLength - GameHours) { CageOwner = cold.InWhat is { } owned && Fixtures.Find(owned) is { } jail ? jail.Owner : -1, CageId = cold.InWhat,
                    LockLevel = cold.InWhat is { } locked && Fixtures.Find(locked)?.Lock is { } cageLock ? cageLock.Level : -1 } : null,
                Ragdoll = ragdolls?.Publish(id),
                StealthKoChance = koSlot >= 0 && i != koSlot && cold is { IsPlayer: false } && Carrying.CarrySystem.CanBeKnockedOut(cold)
                    ? Carrying.CarrySystem.StealthKoChance(table.Cold(koSlot)!, cold, aggro?.Spotted(i, table.IdOf(koSlot)) == true) : null,
                Asleep = Carrying.CarrySystem.Asleep(table, i),
                Down = cold is not null && CharacterFacts.Down(cold),
                Conscious = cold is not null && CharacterFacts.Conscious(cold),
                Standing = cold is not null && CharacterFacts.Standing(cold),
                Animal = cold is not null && CharacterFacts.Animal(cold),
                CanOpenInventory = cold is not null && Carrying.CarrySystem.Openable(table, i),
                KidnapChance = leadSlot >= 0 && i != leadSlot && cold is { IsPlayer: false } ? Carrying.CarrySystem.KidnapChance(table.Cold(leadSlot)!, cold, Tick) : null,
                LockpickSkill = cold is { IsPlayer: true } ? Locks.Lockpicking.Skill(cold) : 0,
            };
        }
        PreviousSnapshot = Snapshot;
        Snapshot = new WorldSnapshot(Tick, list) { TutorialFacts = PublishTutorialFacts(population), Projectiles = projectiles?.PublishProjectiles() ?? [], Trade = trades?.PublishTrade(), Conversation = dialogues?.PublishConversation(), CharacterTrade = dialogues?.PublishCharacterTrade(), Speech = PublishSpeech(), DialogueRequest = dialogues?.PublishRequest(), Towns = Player.Towns, Camps = Player.Camps, KnownFactions = Player.KnownFactions, SquadAi = Player.SquadAi, PlayerSquads = PublishSquads(), ActiveSquad = Player.Squad, OpenLoot = Player.OpenLoot, LootOpener = Player.LootOpener,
            Held = Player.Held is { } held ? Items.HeldItemView.Of(held) : null, HeldSerial = Player.HeldSerial,
            Buildings = Buildings.Published(), BuildingsRevision = Buildings.Revision, GroundItems = GroundItems.Published(), GroundItemsRevision = GroundItems.Revision, UnlockedBuildings = Research.Unlocked, Doors = Fixtures.PublishedDoors(), Containers = Fixtures.ContainerSpots, Beds = Fixtures.BedSpots, Cages = Fixtures.CageSpots(Characters), DoorPicks = Fixtures.DoorPicks, OwnedTownBuildings = Fixtures.OwnedProperties(Player.Faction), InspectedBuilding = beds?.DetailOf(this, Player.InspectedBuilding) ?? Fixtures.DetailOf(Player.InspectedBuilding, Player.Faction), InspectedRichness = production?.RichnessOf(this, Player.InspectedBuilding), InspectedTurret = combat?.TurretPanelOf(this, Player.InspectedBuilding),
            Machines = OpenContainerView() is { } container ?[.. production is null ? [] : Machines.Published(production.Items), container] : production is null ? [] : Machines.Published(production.Items), MachinesVersion = Machines.Version, Research = production?.ResearchView(this) ?? Production.ResearchSnapshot.Empty, Outposts = production?.OutpostView(this) ?? [],
            Crafts = production?.CraftsView(this) ?? Snapshot.Crafts, Smiths = production?.SmithsView(this) ?? Snapshot.Smiths };
    }

    /// <summary>The dialogue's speech bubbles, then the <see cref="Bark"/>s still due whose speaker has no dialogue line on show; expired barks are dropped.</summary>
    IReadOnlyList<Dialogue.SpeechBubble> PublishSpeech()
    {
        var speech = dialogues?.PublishSpeech() ?? [];
        if (barks.Count == 0) return speech;
        foreach (var gone in barks.Where(b => Tick >= b.Value.Until || !Characters.TryResolveNext(b.Key, out _)).Select(b => b.Key).ToArray()) barks.Remove(gone);
        if (barks.Count == 0) return speech;
        var all = new List<Dialogue.SpeechBubble>(speech);
        foreach (var (who, (text, _)) in barks)
            if (!speech.Any(s => s.Speaker == who)) all.Add(new Dialogue.SpeechBubble(who, text, false));
        return all;
    }

    /// <summary>
    /// The open container (<see cref="PlayerState.OpenContainer"/>) shaped as a machine with a storage grid, so the host shows it in the storage window the research bench uses.
    /// <c>Site</c> is the fixture id, <c>Record</c> its BUILDING record. Null when none is open.
    /// </summary>
    Production.MachineSnapshot? OpenContainerView()
    {
        if (Player.OpenContainer is not { } id || Fixtures.Find(id) is not { Contents: not null } f) return null;
        var grid = PlayerSystem.GridOf(this, f, out var region);
        var storage = new InventoryView(region.Width, region.Height, 0, 0,
            Array.AsReadOnly(grid.Items.Select((item, index) => (item, index)).Where(e => region.Holds(e.item)).Select(e => new InventoryItemView(e.index, e.item.Record,
                e.item.Name, "main", e.item.X - region.X, e.item.Y - region.Y, e.item.Width, e.item.Height, e.item.Quantity, e.item.Weight, e.item.Quality)
                { Material = e.item.MaterialId, Colour = e.item.ColourId, Charge = e.item.ChargeFraction, Company = e.item.CompanyId, Level = e.item.Level, Charges = e.item.Charges, MaxCharges = e.item.MaxCharges, Unique = e.item.Unique }).ToArray()));
        return new Production.MachineSnapshot(id, f.RecordId, [], null, storage, null, 0, 0, 0) { IsContainer = true };
    }

    /// <summary>
    /// The bar of Task_PickLock and Task_PickLock_Shackles (docs/game/locks.md "Lockpicking"): "Lockpicking (Chance {p}%)" with the cage penalty, filled pick time / next-roll time,
    /// while the picker is at the lock (not walking to it).
    /// </summary>
    TaskBar? PickBar(CharacterCold c, byte task)
    {
        if (task == (byte)CharacterTask.GoTo) return null;
        var l = c.PickingLock is { } id ? Fixtures.Find(id)?.Lock
            : c.Shackles is { Work: Locks.ShackleWork.Pick } job && Characters.TryResolveNext(job.Wearer, out int w) ? Locks.LockSystem.WornShackles(Characters.Cold(w))?.Lock : null;
        if (l is not { EffectivelyLocked: true }) return null;
        float chance = Locks.Lockpicking.RollChance(c, l.Level);
        return new TaskBar(FormattableString.Invariant($"Lockpicking (Chance {MathF.Round(chance * 100)}%)"), l.NextRoll > 0 ? Math.Clamp(l.PickTime / l.NextRoll, 0, 1) : 0);
    }

    /// <summary>The bar of Task_UseTrainingDummy (docs/game/ai-tasks.md "Finders of the jobs"): "Training", filled with the trained stat's fraction, while the user works the dummy.</summary>
    TaskBar? TrainBar(CharacterCold c)
    {
        if (!c.Operating || c.OperateSite is not { } site || Machines.Find(site) is not { Data.IsTraining: true } m || c.Stats is not { } stats) return null;
        float stat = stats[(StatsEnumerated)m.Data.Stat];
        return new TaskBar("Training", stat - MathF.Floor(stat));
    }

    /// <summary>The sneaker's watchers of the last <see cref="AggroSystem.WatcherSeconds"/> that are still about, where they stand now.</summary>
    SquadView[] PublishSquads() => [.. SquadManagement.PlayerSquads(this).Select(s => new SquadView(s.Id, s.Name, [.. s.Members]))];

    WatcherView[] WatchersOf(CharacterCold cold)
    {
        if (cold.Watchers is not { Count: > 0 } list) return [];
        long lapse = Tick - (long)MathF.Ceiling(AggroSystem.WatcherSeconds / TickSeconds);
        var views = new List<WatcherView>(list.Count);
        foreach (var w in list)
            if (w.Tick >= lapse && Characters.TryResolveNext(w.Looker, out int slot) && Characters.Cold(slot) is { CarriedBy.IsNone: true }) views.Add(new WatcherView(Characters.Previous[slot].Position, w.Level));
        return [.. views];
    }

    /// <summary>
    /// The HUD's numbers of a selected character (docs/game/ui-screens.md section 4). The stats panel lists what the character fights with: Crossbows, Precision and
    /// Perception in ranged mode with a crossbow, else Martial Arts unarmed or Attack and Defense with a melee weapon; then Strength, Dexterity, Toughness and, but
    /// in ranged mode, Dodge; the lines and their tooltips are <see cref="HudTips"/>'s.
    /// </summary>
    HudView HudOf(CharacterId id, CharacterCold cold, Bodies.MedicalState med, RaceData race, Bodies.CharacterStats stats, in CharacterHot hot, Combat.CombatSystem? combat,
        Meitou.Data.Gameplay.GameConstants? constants, bool inBuilding, bool inside, Meitou.Data.GameDatabase? db = null)
    {
        // Movement +0x24 currentlyMoving: the current speed (+0xb8, the Athletics line's too) above 0.1 (CharMovement::update).
        bool moving = hot.Velocity.X * hot.Velocity.X + hot.Velocity.Z * hot.Velocity.Z > 0.1f * 0.1f;
        int mode = combat?.StatsDisplayMode(id, cold, moving) ?? (cold.Fighter is { Unarmed: false } ? 0 : 1);
        var hc = combat?.HudNumbers(this, id.Slot, cold);
        var lines = HudTips.Lines(cold, med, stats, hc, constants, inBuilding, inside, mode);
        var extended = cold.Extended is { } x0 && systems.OfType<BodySystem>().FirstOrDefault() is { } bodies
            ? bodies.WithCombatMode(x0, cold, combat is not null && CharacterFacts.InCombatMode(combat, Characters, id, cold, true, true)) : cold.Extended;
        return new HudView(med.Blood, Bodies.MedicalState.BloodCapacity(race, stats.Strength, med.HasStatsObject, med.ScaleK, med.ScaleZ), med.Bleeding,
            cold.Fighter?.EncumbranceFactor ?? 1, lines.AsReadOnly(), extended) { BleedRate = race.BleedRate, Fed = med.Fed, Robot = race.IsRobot,
            ExtendedTips = extended is { } x ? HudTips.Extended(cold, med, race, stats, x, constants) : null, Skill = SkillLine(id, cold, med, race, stats, hot, combat, mode),
            KoPoint = constants is null ? null : Bodies.MedicalState.KoPoint(constants, stats.Toughness),
            StatReadouts = HudTips.StatsWindow(cold, med, race, stats, hc, constants, db) };
    }

    /// <summary>
    /// The state panel's "Current Skill" line (the HUD update's panel 3, docs/game/ui-screens.md section 4): the skill the job used this tick at its effective level with its
    /// tooltip (<see cref="SkillTermsOf"/>); else, with the stats display mode 2 or the ranged controller's combat mode, the crossbow's accuracy error; else, in a melee fight, the
    /// class of the weapon in hand at its trained level with its damage; else Stealth when sneaking, Athletics when moving faster than 9 units a second. Swimming replaces it with
    /// the swim speed. Picking a lock is Lockpicking.
    /// </summary>
    CurrentSkillView? SkillLine(CharacterId id, CharacterCold cold, Bodies.MedicalState med, RaceData race, Bodies.CharacterStats stats, in CharacterHot hot, Combat.CombatSystem? combat,
        int mode)
    {
        float encumbrance = cold.Fighter?.EncumbranceFactor ?? 1;
        if (cold.WaterState == 3) return new(StatsEnumerated.Swimming, "Swimming", cold.SwimSpeed, Speed: true) { Swim = SwimTermsOf(cold, med, race, stats, encumbrance) };
        var stat = cold.SkillInUseTick >= Tick - 1 ? cold.SkillInUse : PickBar(cold, hot.Task) is not null ? StatsEnumerated.Lockpicking : StatsEnumerated.None;
        if (stat != StatsEnumerated.None)
        {
            var terms = SkillTermsOf(cold, med, stats, stat);
            return new(stat, null, MathF.Round(terms.Total), terms.Penalised, Terms: terms);
        }
        // getGUIStatsDisplayMode 2 or the ranged controller's combat mode (a shooting fight, a manned turret; no target test), with a crossbow: its accuracy with Crossbows, or with
        // Turrets when Character +0x3d8 (the last weapon category the HUD saw in a melee fight) is 7.
        bool aiming = mode == 2 || Combat.CombatSystem.RangedCombatMode(cold);
        if (aiming)
        {
            var aimStat = cold.LastWeaponCategory == (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Turret ? StatsEnumerated.Turrets : StatsEnumerated.Crossbows;
            if (Combat.CombatSystem.HudAccuracy(cold, aimStat) is { } aim)
                return new(aimStat, "Accuracy Error:", aim.Deviation) { Accuracy = new(aim.Weapon, aim.Skill, aim.Deviation) };
        }
        else if (combat?.MeleeCombatMode(id, cold) == true)
        {
            // The HUD writes Character +0x3d8 from the weapon in hand (getCurrentWeapon; the FISTS weapon with nothing drawn), then labels by it. The only writer besides the constructor,
            // so this display state lives on the character; each character is published by one worker, so the write does not race.
            cold.LastWeaponCategory = Combat.CombatSystem.HandCategory(cold);
            var (label, skill) = cold.LastWeaponCategory switch
            {
                3 => ("Heavy:", StatsEnumerated.HeavyWeapons), 0 => ("Katana:", StatsEnumerated.Katanas), 4 => ("Hacker:", StatsEnumerated.Hackers), 1 => ("Sabre:", StatsEnumerated.Sabres),
                5 => ("Martial Arts:", StatsEnumerated.MartialArts), 8 => ("Polearm:", StatsEnumerated.Polearms), _ => ((string?)null, StatsEnumerated.None),
            };
            if (label is not null) return new(skill, label, (int)stats[skill]) { Damage = combat.HudDamage(this, id.Slot, cold) };
        }
        if ((cold.Orders & CharacterOrders.Sneak) != 0)
        {
            float s = Orders.Stealth(cold);
            int total = (int)(s * 100), trained = (int)stats[StatsEnumerated.Stealth];
            float t = MathF.Max(0, (s - 0.1f) / 0.8f), top = hot.MaxSpeed;
            return new(StatsEnumerated.Stealth, "Stealth:", total) { Base = trained, Stealth = new(trained, 12 + (top - 12) * t, Bodies.XpService.SneakRate(BodySystem.PaidExposureOf(this, cold)),
                !cold.Carrying.IsNone, med.Prone, (int)((1 - encumbrance) * 100), (int)((med.StealthBodyFactor(stats[StatsEnumerated.Toughness] * stats.Scale) - 1) * 100),
                (int)(((cold.Fighter?.StealthMult ?? 1) - 1) * 100), (int)((med.LegItemFactor(StatsEnumerated.Stealth) - 1) * 100), total) };
        }
        float speed = MathF.Sqrt(hot.Velocity.X * hot.Velocity.X + hot.Velocity.Z * hot.Velocity.Z);
        if (speed <= 9) return null;
        return new(StatsEnumerated.Athletics, "Athletics:", (int)stats.Athletics) { Athletics = new((int)stats.Athletics, Bodies.Speed.RunValue(race, stats.Athletics),
            (int)((1 - Bodies.Speed.LegFactor(med)) * 100), (int)((1 - encumbrance) * 100), cold.Fighter?.AthleticsFactor ?? 1, (int)((med.LegItemFactor(StatsEnumerated.Athletics) - 1) * 100),
            hot.MaxSpeed) };
    }

    /// <summary>
    /// The Swimming line's tooltip (the HUD update): the trained level, the base swim speed (<see cref="Bodies.Speed.SwimBase"/>), the leg term with 0.6 per unusable arm, the hunger
    /// term, the encumbrance and the limb items' factor (all four limbs) as percentages, whether both arms are broken, and the current swim speed.
    /// </summary>
    static SwimTerms SwimTermsOf(CharacterCold cold, Bodies.MedicalState med, RaceData race, Bodies.CharacterStats stats, float encumbrance)
    {
        bool left = med.ArmWorks(left: true), right = med.ArmWorks(left: false);
        float legs = Bodies.Speed.LegFactor(med) * (left ? 1 : 0.6f) * (right ? 1 : 0.6f);
        float items = med.LegItemFactor(StatsEnumerated.Swimming) * med.ArmItemFactor(StatsEnumerated.Swimming);
        return new(stats[StatsEnumerated.Swimming], Bodies.Speed.SwimBase(race, stats), (int)((1 - legs) * 100), (int)((1 - Bodies.Speed.HungerFactor(med.Hunger)) * 100),
            (int)((1 - encumbrance) * 100), (int)((1 - items) * 100), !left && !right, cold.SwimSpeed);
    }

    /// <summary>
    /// A skill's tooltip terms (<c>FUN_140890b00</c>): the stat multiplier (<c>FUN_1406457e0</c>) asked for one term at a time, injuries (the limb term), hunger, weather (the dust
    /// factor), darkness (the light term), robotics (fitted limbs and the disabled-arm 0.66) and, but for strength and perception, the worn gear (perception has its armour bonus,
    /// added to the level before the multiplier, instead); for strength the 0.66 of a
    /// disabled arm again as the missing arm penalty. The total is the stat with every term.
    /// </summary>
    internal static (float Level, int Penalty) EffectiveSkill(CharacterCold cold, Bodies.MedicalState med, Bodies.CharacterStats stats, StatsEnumerated stat)
    {
        var factors = (cold.Fighter?.Factors ?? Bodies.StatFactors.None) with { Stun = med.DustStatFactor };
        float multiplier = med.StatMultiplier(stat, Bodies.StatUse.All, factors);
        return ((stats[stat] + Combat.CombatSystem.GearBonus(cold, stat)) * multiplier, (int)((1 - multiplier) * 100));
    }

    static SkillTerms SkillTermsOf(CharacterCold cold, Bodies.MedicalState med, Bodies.CharacterStats stats, StatsEnumerated stat)
    {
        var factors = (cold.Fighter?.Factors ?? Bodies.StatFactors.None) with { Stun = med.DustStatFactor };
        int Term(Bodies.StatUse use) => (int)((1 - med.StatMultiplier(stat, use, factors)) * 100);
        bool robotics = med.Parts.Any(p => p.Limb == Bodies.LimbState.Replaced && p.Replacement is not null);
        int gear = stat is StatsEnumerated.Strength or StatsEnumerated.Perception ? 0 : Term(Bodies.StatUse.LimbItems);
        int missingArm = stat == StatsEnumerated.Strength && med.Aggregates().EitherArmDisabled ? 33 : 0;
        int bonus = Combat.CombatSystem.GearBonus(cold, stat);
        return new(stats[stat], Term(Bodies.StatUse.Limbs), Term(Bodies.StatUse.Hunger), Term(Bodies.StatUse.Stun), Term(Bodies.StatUse.Light), Term(Bodies.StatUse.DamageState),
            robotics, gear, missingArm, (stats[stat] + bonus) * med.StatMultiplier(stat, Bodies.StatUse.All, factors), bonus);
    }

    static BodyStatus BodyOf(CharacterCold cold, Bodies.MedicalState med, RaceData race, byte task, float extraBloodLoss)
    {
        // The quick right click's tests (FUN_1407fb410): the organic need score above 0.01 is first aid (order 25), else the robot one is robot repair (60).
        float capacity = MathF.Max(Bodies.MedicalState.BloodCapacity(race, cold.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1);
        bool organic = med.FirstAidNeed(race, false, capacity, extraBloodLoss) > Bodies.MedicalState.AidNeedFloor;
        bool robotNeed = med.FirstAidNeed(race, true, capacity, extraBloodLoss) > Bodies.MedicalState.AidNeedFloor;
        bool robot = !organic && robotNeed;
        return new(med.Blood / capacity, WorstPart(med), med.Hunger, med.Unconscious, med.Dead, organic || robot,
            task == (byte)CharacterTask.Treat ? CareKind.Treating : task == (byte)CharacterTask.Rest ? CareKind.Resting : CareKind.None, cold.MedicJob, cold.RescueJob,
            med.NeedsSplint(race, 50), med.KoTimer, med.PlayingDead, med is { StayingLow: true, Prone: true, Crawling: false }, robot)
            { NeedsOrganicAid = organic, NeedsRepairs = robotNeed, BeingEaten = med.EatenCountdown > 0, Coma = med.Coma, Crawling = med.Crawling, RightArmDisabled = !med.ArmWorks(left: false) };
    }

    /// <summary>The lowest part fraction (1 for a body without parts), the way <c>Enumerable.Min</c> reads floats: a NaN wins.</summary>
    static float WorstPart(Bodies.MedicalState med)
    {
        var parts = med.Parts;
        if (parts.Count == 0) return 1;
        float worst = parts[0].Fraction;
        if (float.IsNaN(worst)) return worst;
        for (int k = 1; k < parts.Count; k++)
        {
            float f = parts[k].Fraction;
            if (f < worst) worst = f;
            else if (float.IsNaN(f)) return f;
        }
        return worst;
    }
}
