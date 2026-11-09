using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation;

/// <summary>
/// The world's publish: the partitions, the snapshot buffers and the character views the host draws.
/// </summary>
public sealed partial class World
{
    #region Partitions

    /// <summary>
    /// The partition <paramref name="i"/> of the parallel phases.
    /// </summary>
    internal Partition PartitionAt(int i) => _partitions[i];

    /// <summary>
    /// The effect buffer of partition <paramref name="i"/>.
    /// </summary>
    internal EffectBuffer BufferAt(int i) => _buffers[i];

    #endregion

    #region Snapshot buffers

    /// <summary>
    /// Slots a publish job takes at a time: the characters are made on the worker pool, each one reading only the tick's state.
    /// </summary>
    const int PublishChunk = 256;

    /// <summary>
    /// A set of character snapshots the publish fills in place: one object per slot, reused tick after tick. Four of them: the shown pair
    /// (<see cref="Snapshot"/> and <see cref="PreviousSnapshot"/>) stays untouched while the next ticks, run ahead, publish twice into the other two
    /// (docs/simulation.md "Snapshots").
    /// </summary>
    sealed class SnapshotBuffer
    {
        /// <summary>
        /// Per slot, the snapshot this publish shows: the object the buffer filled (<see cref="Own"/>, else <see cref="Spare"/>), or the last
        /// publish's for a character the far cadence skipped.
        /// </summary>
        public CharacterSnapshot?[] Slots = [];
        /// <summary>
        /// Per slot, the object this buffer fills and its spare (swapped in while another buffer still shows the own one).
        /// </summary>
        public CharacterSnapshot?[] Own = [], Spare = [];
        /// <summary>
        /// The living characters' snapshots in slot order, the published list.
        /// </summary>
        public readonly List<CharacterSnapshot> List = [];
    }

    /// <summary>
    /// The four buffers the publishes take in turn (<see cref="SnapshotBuffer"/>).
    /// </summary>
    readonly SnapshotBuffer[] _snapshotRing = [new(), new(), new(), new()];

    /// <summary>
    /// The buffers <see cref="Snapshot"/>, <see cref="PreviousSnapshot"/> and the snapshot before that were filled from (null:
    /// <see cref="WorldSnapshot.Empty"/>).
    /// </summary>
    SnapshotBuffer? _shownBuffer, _previousBuffer, _olderBuffer;

    /// <summary>
    /// The buffer's object to fill for slot <paramref name="i"/>: never one another buffer of the ring still shows (a far character's object is
    /// shared across publishes and the render thread may be reading it). The two the buffer owns alternate; one more is made only when both are shown
    /// elsewhere.
    /// </summary>
    CharacterSnapshot Fresh(SnapshotBuffer buffer, int i)
    {
        ref var own = ref buffer.Own[i];
        if (own is null || !ShownElsewhere(buffer, own, i)) return own ??= new();

        ref var spare = ref buffer.Spare[i];
        if (spare is not null && ShownElsewhere(buffer, spare, i)) spare = null;

        (own, spare) = (spare ?? new(), own);
        return own;
    }

    /// <summary>
    /// Whether another buffer of the ring shows <paramref name="s"/> at slot <paramref name="i"/>.
    /// </summary>
    bool ShownElsewhere(SnapshotBuffer buffer, CharacterSnapshot s, int i)
    {
        foreach (var b in _snapshotRing)
            if (b != buffer && i < b.Slots.Length && ReferenceEquals(b.Slots[i], s)) return true;

        return false;
    }

    /// <summary>
    /// Keeps <see cref="PreviousSnapshot"/> across a publish (a paused publish, loading): its buffer stays out of the ring with it.
    /// </summary>
    void KeepPrevious(WorldSnapshot previous, SnapshotBuffer? buffer) => (PreviousSnapshot, _previousBuffer) = (previous, buffer);

    #endregion

    #region Publish

    /// <summary>
    /// The facts of one publish its character fills read (<see cref="Fill"/>).
    /// </summary>
    /// <param name="KoSlot">The selection's first member that could knock someone out from behind (-1: none).</param>
    /// <param name="LeadSlot">The selection's lead (-1: none).</param>
    /// <param name="Pointed">The character under the pointer.</param>
    /// <param name="Aggro">The senses, for the stealth KO's spotted test (only with a knock-out candidate).</param>
    /// <param name="Talk">The dialogue system, for the trader flag.</param>
    /// <param name="Combat">The fights.</param>
    /// <param name="BodyConstants">The body system's constants.</param>
    /// <param name="Population">The population data.</param>
    /// <param name="ExtraBloodLoss">The body parts' extra blood loss factor (1 without a body system).</param>
    readonly record struct PublishFacts(int KoSlot, int LeadSlot, CharacterId Pointed, AggroSystem? Aggro, Dialogue.DialogueSystem? Talk,
        Combat.CombatSystem? Combat, GameConstants? BodyConstants, PopulationData? Population, float ExtraBloodLoss);

    /// <summary>
    /// Builds the <see cref="WorldSnapshot"/> of the tick that just ended from the state it left in <see cref="CharacterTable.Previous"/>.
    /// </summary>
    /// <param name="farShared">A tick's publish: a character the far cadence skipped this tick (<see cref="Steps"/>) is published as the last publish
    /// had it, the same object.</param>
    void Publish(TickProfile? prof = null, bool farShared = false)
    {
        var table = Characters;
        var state = table.Previous;
        var (koSlot, leadSlot) = SelectionSlots();

        var pointed = Player.Pointed;
        var aggro = koSlot >= 0 ? _systems.OfType<AggroSystem>().FirstOrDefault() : null;
        var talk = _systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault();
        var combat = _systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        var beds = _systems.OfType<Locks.BedSystem>().FirstOrDefault();
        var bodyConstants = _systems.OfType<BodySystem>().FirstOrDefault()?.Constants;
        var population = _systems.OfType<PopulationSystem>().FirstOrDefault()?.Data;
        float extraBloodLoss = _systems.OfType<BodySystem>().FirstOrDefault()?.Constants.ExtraBloodLossFromBodyparts ?? 1;
        var buffer = Array.Find(_snapshotRing, b => b != _shownBuffer && b != _previousBuffer && b != _olderBuffer)!;

        if (buffer.Slots.Length < state.Length)
        {
            Array.Resize(ref buffer.Slots, state.Length);
            Array.Resize(ref buffer.Own, state.Length);
            Array.Resize(ref buffer.Spare, state.Length);
        }

        var slots = buffer.Slots;
        var facts = new PublishFacts(koSlot, leadSlot, pointed, aggro, talk, combat, bodyConstants, population, extraBloodLoss);

        // A character the far cadence skipped this tick has not changed: the last publish's object stands for it again (docs/simulation.md "Far
        // cadence").
        var kept = farShared ? _shownBuffer?.Slots : null;
        ParallelFor((state.Length + PublishChunk - 1) / PublishChunk, chunk =>
        {
            var state = table.Previous;

            for (int i = chunk * PublishChunk, end = Math.Min(state.Length, i + PublishChunk); i < end; i++)
            {
                if (!state[i].Alive) continue;

                if (kept is not null && !Steps(i) && i < kept.Length && kept[i] is { } last && last.Id == new CharacterId(i, state[i].Generation))
                {
                    slots[i] = last;
                    continue;
                }

                Fill(slots[i] = Fresh(buffer, i), i, in facts);
            }
        });

        var list = buffer.List;
        list.Clear();
        for (int i = 0; i < state.Length; i++)
            if (state[i].Alive) list.Add(slots[i]!);

        prof?.Part("publish characters");   // F12, in a tick only (a paused publish has no lap to measure from)

        (_olderBuffer, _previousBuffer, _shownBuffer) = (_previousBuffer, _shownBuffer, buffer);
        PreviousSnapshot = Snapshot;
        Snapshot = SnapshotOf(list, population, beds, combat);
        prof?.Part("publish views");
    }

    /// <summary>
    /// The selection's slots the context figures are against: its first member that could knock someone out from behind (the stealth KO
    /// chances) and its lead (the kidnap chances); -1 for none.
    /// </summary>
    (int Ko, int Lead) SelectionSlots()
    {
        var table = Characters;

        // The selection's first member that could knock someone out from behind: the stealth KO chances are against it.
        int koSlot = -1;

        foreach (var s in Player.Selection)
        {
            if (table.TryResolveNext(s, out int k) && table.Cold(k) is { } c && Carrying.CarrySystem.CanKnockOut(c))
            {
                koSlot = k;
                break;
            }
        }

        // The selection's lead: the kidnap chances are against it.
        int leadSlot = -1;

        foreach (var s in Player.Selection)
        {
            if (table.TryResolveNext(s, out int l))
            {
                leadSlot = l;
                break;
            }
        }

        return (koSlot, leadSlot);
    }

    /// <summary>
    /// The world's snapshot of the tick over the published <paramref name="list"/>: the characters and every view of the world and the panels.
    /// </summary>
    WorldSnapshot SnapshotOf(List<CharacterSnapshot> list, PopulationData? population, Locks.BedSystem? beds, Combat.CombatSystem? combat)
    {
        return new WorldSnapshot(Tick, list)
        {
            TutorialFacts = PublishTutorialFacts(population),
            Projectiles = _projectiles?.PublishProjectiles() ?? [],
            Trade = _trades?.PublishTrade(),
            Conversation = _dialogues?.PublishConversation(),
            CharacterTrade = _dialogues?.PublishCharacterTrade(),
            Speech = PublishSpeech(),
            DialogueRequest = _dialogues?.PublishRequest(),
            Towns = Player.Towns,
            Camps = Player.Camps,
            KnownFactions = Player.KnownFactions,
            SquadAi = Player.SquadAi,
            PlayerSquads = PublishSquads(),
            ActiveSquad = Player.Squad,
            OpenLoot = Player.OpenLoot,
            LootOpener = Player.LootOpener,
            Held = Player.Held is { } held ? Items.HeldItemView.Of(held) : null,
            HeldSerial = Player.HeldSerial,
            Buildings = Buildings.Published(),
            BuildingsRevision = Buildings.Revision,
            NestBuildings = NestBuildings.Published(),
            NestMeshes = NestBuildings.Meshes(),
            NestBuildingsRevision = NestBuildings.Revision,
            Ruins = Fixtures.PublishedRuins(),
            RuinsRevision = Fixtures.RuinsRevision,
            EmptiedHouses = Fixtures.PublishedEmptied(),
            EmptiedHousesRevision = Fixtures.EmptiedRevision,
            GroundItems = GroundItems.Published(),
            GroundItemsRevision = GroundItems.Revision,
            UnlockedBuildings = Research.Unlocked,
            Doors = Fixtures.PublishedDoors(),
            Containers = Fixtures.ContainerSpots,
            Beds = Fixtures.BedSpots,
            Dummies = Fixtures.TrainingSpots,
            Seats = Fixtures.SeatSpots,
            Cages = Fixtures.CageSpots(Characters),
            DoorPicks = Fixtures.DoorPicks,
            OwnedTownBuildings = Fixtures.OwnedProperties(Player.Faction),
            InspectedBuilding = beds?.DetailOf(this, Player.InspectedBuilding) ?? Fixtures.DetailOf(Player.InspectedBuilding, Player.Faction),
            InspectedRichness = _production?.RichnessOf(this, Player.InspectedBuilding),
            InspectedTurret = combat?.TurretPanelOf(this, Player.InspectedBuilding),
            Machines = OpenContainerView() is { } container
                ? [.. _production is null ? [] : Machines.Published(_production.Items), container]
                : _production is null ? [] : Machines.Published(_production.Items),
            MachinesVersion = Machines.Version,
            Research = _production?.ResearchView(this) ?? Production.ResearchSnapshot.Empty,
            Outposts = _production?.OutpostView(this) ?? [],
            Crafts = _production?.CraftsView(this) ?? Snapshot.Crafts,
            Smiths = _production?.SmithsView(this) ?? Snapshot.Smiths
        };
    }

    /// <summary>
    /// The dialogue's speech bubbles, then the <see cref="Bark"/>s still due whose speaker has no dialogue line on show; expired barks are dropped.
    /// </summary>
    IReadOnlyList<Dialogue.SpeechBubble> PublishSpeech()
    {
        var speech = _dialogues?.PublishSpeech() ?? [];
        if (_barks.Count == 0) return speech;
        foreach (var gone in _barks
            .Where(b => Tick >= b.Value.Until || !Characters.TryResolveNext(b.Key, out _))
            .Select(b => b.Key)
            .ToArray()) _barks.Remove(gone);
        if (_barks.Count == 0) return speech;

        var all = new List<Dialogue.SpeechBubble>(speech);
        foreach (var (who, (text, _)) in _barks)
            if (!speech.Any(s => s.Speaker == who)) all.Add(new Dialogue.SpeechBubble(who, text, false));

        return all;
    }

    /// <summary>
    /// The open container (<see cref="PlayerState.OpenContainer"/>) shaped as a machine with a storage grid, so the host shows it in the storage
    /// window the research bench uses. <c>Site</c> is the fixture id, <c>Record</c> its BUILDING record. Null when none is open.
    /// </summary>
    Production.MachineSnapshot? OpenContainerView()
    {
        if (Player.OpenContainer is not { } id || Fixtures.Find(id) is not { Contents: not null } f) return null;

        var grid = Items.ContainerAccess.GridOf(this, f, out var region);
        var storage = new InventoryView(region.Width, region.Height, 0, 0,
            Array.AsReadOnly(grid.Items
                .Select((item, index) => (item, index))
                .Where(e => region.Holds(e.item))
                .Select(e => new InventoryItemView(e.index, e.item.Record, e.item.Name, "main", e.item.X - region.X, e.item.Y - region.Y,
                    e.item.Width, e.item.Height, e.item.Quantity, e.item.Weight, e.item.Quality)
                {
                    Material = e.item.MaterialId,
                    Colour = e.item.ColourId,
                    Charge = e.item.ChargeFraction,
                    Company = e.item.CompanyId,
                    Level = e.item.Level,
                    Charges = e.item.Charges,
                    MaxCharges = e.item.MaxCharges,
                    Unique = e.item.Unique
                })
                .ToArray()));
        return new Production.MachineSnapshot(id, f.RecordId, [], null, storage, null, 0, 0, 0) { IsContainer = true };
    }

    /// <summary>
    /// The sneaker's watchers of the last <see cref="AggroSystem.WatcherSeconds"/> that are still about, where they stand now.
    /// </summary>
    SquadView[] PublishSquads() => [.. SquadManagement.PlayerSquads(this).Select(s => new SquadView(s.Id, s.Name, [.. s.Members]))];

    #endregion

    #region Character fills

    /// <summary>
    /// How the player looks at one character this publish (<see cref="Viewed"/>).
    /// </summary>
    /// <param name="Id">The character.</param>
    /// <param name="Cold">Its cold row.</param>
    /// <param name="Selected">One of the player's, selected.</param>
    /// <param name="Inspected">Inspected (not selected).</param>
    /// <param name="Detailed">Selected or inspected: the panels' details are filled.</param>
    /// <param name="Shown">Detailed or under the pointer: the context menu's figures are filled.</param>
    /// <param name="Sneaking">One of the player's, sneaking.</param>
    /// <param name="Watchers">The lookers watching it sneak.</param>
    /// <param name="FightsPlayer">An NPC fighting one of the player's characters.</param>
    readonly record struct PublishedCharacter(CharacterId Id, CharacterCold? Cold, bool Selected, bool Inspected, bool Detailed, bool Shown,
        bool Sneaking, WatcherView[] Watchers, bool FightsPlayer);

    /// <summary>
    /// Fills a character's snapshot. Every property is written on every fill: the object held another character's last time.
    /// </summary>
    void Fill(CharacterSnapshot s, int i, in PublishFacts f)
    {
        var v = Viewed(i, f);
        FillIdentity(s, i, in v, in f);
        FillPanels(s, i, in v, in f);
        FillBody(s, i, in v, in f);
        FillOrders(s, i, in v, in f);
        FillInteraction(s, i, in v, in f);
    }

    /// <summary>
    /// How the player looks at the character in <paramref name="i"/> this publish: selected, inspected, pointed at, sneaking (and its watchers),
    /// fighting one of the player's.
    /// </summary>
    PublishedCharacter Viewed(int i, in PublishFacts f)
    {
        var table = Characters;
        var combat = f.Combat;
        var pointed = f.Pointed;
        var state = table.Previous;
        var id = new CharacterId(i, state[i].Generation);
        var cold = table.Cold(i);

        // The details only the selection shows: asked once per character, the selection is a list.
        bool selected = cold is { IsPlayer: true } && Player.Selection.Contains(id), inspected = !selected
            && id == Player.Inspected, detailed = selected || inspected;
        bool shown = detailed || id == pointed;
        bool sneaking = cold is { IsPlayer: true } && (cold.Orders & CharacterOrders.Sneak) != 0;
        var watchers = sneaking ? WatchersOf(cold!) : [];
        bool fightsPlayer = cold is { IsPlayer: false, InCombat: true } && combat?.StateOf(id) is { TargetSlot: >= 0 and var fought } fight
            && fought < state.Length && state[fought].Alive && state[fought].Generation == fight.TargetGeneration
            && table.Cold(fought) is { IsPlayer: true };

        return new PublishedCharacter(id, cold, selected, inspected, detailed, shown, sneaking, watchers, fightsPlayer);
    }

    /// <summary>
    /// The identity and look: id, appearance, place, animation, faction, weapons drawn, name, squad, trader and selection.
    /// </summary>
    void FillIdentity(CharacterSnapshot s, int i, in PublishedCharacter v, in PublishFacts f)
    {
        var state = Characters.Previous;
        var id = v.Id;
        var cold = v.Cold;
        bool selected = v.Selected;
        bool inspected = v.Inspected;
        var talk = f.Talk;

        s.Id = id;
        s.Appearance = cold?.Appearance;
        s.Position = DrawnPosition(cold, state[i].Position);
        s.Yaw = state[i].Yaw;
        s.Animations = cold?.Animation is { } anim && _animations is not null
            ? _animations.Publish(anim, s.Layers)
            : AnimationLayers.For(state[i].Animation, state[i].AnimationTime);
        s.DrawYaw = cold?.Animation is { HasDrawYaw: true } drawn ? drawn.DrawYaw : float.NaN;
        s.Faction = cold?.Faction ?? -1;
        s.WeaponDrawn = cold is not null && cold.DrawnWeapon != WeaponKinds.None;
        s.CrossbowShots = cold?.CrossbowShots ?? 0;
        s.StuckBolts = cold?.StuckBolts is { Count: > 0 } bolts ? bolts.ToArray() : [];
        s.RangedDrawn = cold is not null && cold.DrawnWeapon != WeaponKinds.None
            && cold.DrawnWeapon == HandHold.OfCategory((int)Data.Gameplay.Combat.WeaponCategory.Bow).Weapon;
        s.BackDrawn = cold?.Fighter?.WeaponIsBack ?? false;
        s.HasFlies = cold?.HasFlies ?? false;
        s.Name = cold?.Name ?? "";
        s.RecordId = cold?.RecordId ?? "";
        s.SquadId = cold?.SquadId ?? -1;
        s.IsPlayer = cold?.IsPlayer ?? false;
        s.IsTrader = talk?.Trades(this, i) ?? (cold is { IsPlayer: false, SquadId: >= 0 } && Squads.Find(cold.SquadId)?.Shop is not null);
        s.IsDiplomat = cold?.NpcClass == FundamentalType.Diplomat;
        s.Selected = selected;
        s.Inspected = inspected;
    }

    /// <summary>
    /// The panels of a selected or inspected character: skills, inventory, HUD, health, bounties, slave, disguise, status and body.
    /// </summary>
    void FillPanels(CharacterSnapshot s, int i, in PublishedCharacter v, in PublishFacts f)
    {
        var state = Characters.Previous;
        var id = v.Id;
        var cold = v.Cold;
        bool selected = v.Selected;
        bool detailed = v.Detailed;
        var combat = f.Combat;
        var bodyConstants = f.BodyConstants;
        var population = f.Population;
        float extraBloodLoss = f.ExtraBloodLoss;

        s.Skills = detailed && cold!.Stats is { } sk
            ? new SkillSummary(sk[StatsEnumerated.MeleeAttack], sk[StatsEnumerated.MeleeDefence], sk[StatsEnumerated.Dodge], sk.Toughness,
                sk.Strength, sk.Athletics)
            : null;
        s.Inventory = selected && cold!.Inventory is { } carried ? Items.InventoryText.Lines(carried) : [];
        s.AllSkills = detailed && cold!.Stats is { } allStats ? Array.AsReadOnly(allStats.ToArray()) : [];
        s.SkillXpMultipliers = detailed && cold!.Race is { } skillRace ? Array.AsReadOnly((float[])skillRace.StatMultipliers.Clone()) : [];
        s.Hud = detailed && cold!.Medical is { } hudMed && cold.Race is { } hudRace && cold.Stats is { } hudStats
            ? HudOf(id, cold, hudMed, hudRace, hudStats, state[i], combat, bodyConstants, !Sight.Outdoors(i), Sight.Inside(i), population?.Db)
            : null;
        s.LimbBits = cold?.Medical?.LimbBits ?? 0;
        s.BloodAmounts = cold?.Medical?.ShownBlood ?? 0UL;
        s.Knockdown = cold?.Knockdown ?? default;
        s.ClosedSlots = cold is null || !detailed ? [] : ClosedSlotsOf(cold);
        s.HealthParts = detailed && cold!.Medical is { } health && cold.Race is { } anatomy ? HealthPartsOf(health, anatomy) : [];
        s.ItemGrid = (selected || id == Player.OpenLoot || cold is { IsPlayer: true } && Player.InventoryShown.Contains(id))
            && cold!.Inventory is { } grid
            ? InventoryViewOf(cold, grid, population, bodyConstants) : null;
        s.Bounties = detailed && cold!.Bounties is not null
            ? Crime.CrimeSystem.Lines(cold, Tick, TickSeconds, LocalFaction(cold, state[i].Position, population))
            : [];
        s.Slave = cold is { SlaveState: >= 1 and <= 3 }
            ? new SlaveView(cold.SlaveState,
                population is { Factions: { } factions } && (uint)cold.SlaveOwner < (uint)factions.Count ? factions[cold.SlaveOwner].Name : "",
                _systems.OfType<Kidnap.CaptiveSystem>().FirstOrDefault()?.LooksLikeSlaveLevel(cold) ?? 0)
            : null;
        s.Disguise = detailed && cold!.Faction == Player.Faction && (cold.DisguiseLevel > 0 || cold.RecentlyWanted > 0)
            ? DisguiseOf(cold, state[i].Position, population)
            : null;
        s.Status = detailed ? StatusOf(id, cold!, state[i].Position, population) : null;
        s.NoLootGuilt = cold is { } guilt
            && (guilt.NpcClass is FundamentalType.Bandit or FundamentalType.Slave || population is { Factions: { } fl }
                && (uint)guilt.Faction < (uint)fl.Count && fl[guilt.Faction].NotReal);
        s.WarParty = cold is { SquadId: >= 0 } warCold && Squads.Find(warCold.SquadId) is { PlatoonId: >= 0 } warSquad
            && Platoons.Find(warSquad.PlatoonId)?.Campaign is not null;
        s.Body = cold?.Medical is { } med && cold.Race is { } race ? BodyOf(cold, med, race, state[i].Task, extraBloodLoss) : null;
    }

    /// <summary>
    /// The body and its task: build, path, carrying, tool, mouth, work events, held state and posture.
    /// </summary>
    void FillBody(CharacterSnapshot s, int i, in PublishedCharacter v, in PublishFacts f)
    {
        var state = Characters.Previous;
        var cold = v.Cold;
        bool detailed = v.Detailed;

        // The original skips robots' appearances (FUN_140531820) and animals have none.
        s.Build = cold is { Stats: { } buildStats, Medical: { } fed, Race.IsRobot: false, Appearance: { Animal: null } look }
            ? ShapedBuild(cold, Data.Characters.CharacterShape.BuildFromStats(look.Female, buildStats, static (stats, s) => stats[s], fed.Hunger))
            : null;
        bool pathed = detailed && (state[i].Flags & (ushort)MoveFlags.HasPath) != 0;
        s.Path = pathed ? cold!.Path.Skip(state[i].PathCursor).ToArray() : [];
        s.PathExact = pathed && cold!.PathExact != int.MaxValue ? Math.Max(cold.PathExact - state[i].PathCursor, 0) : int.MaxValue;
        s.CarriedBy = cold?.CarriedBy ?? CharacterId.None;
        s.Carry = cold is { CarriedBy.IsNone: false } ? cold.CarryAttach ?? Carrying.CarryPose.Fallback : null;
        s.Carrying = cold?.Carrying ?? CharacterId.None;
        s.HeldTool = state[i].Task == (byte)CharacterTask.Operate && cold is { OperateSite: not null, Appearance.Animal: null }
            ? cold.OperateTool
            : null;
        s.Mouth = cold?.Mouth?.Record;
        s.MouthTone = cold?.MouthTone ?? default;
        s.WorkEvents = WorkEventsOf(state[i].Task, cold);
        s.Task = (CharacterTask)state[i].Task;
        s.PickingLock = cold?.PickingLock is not null;
        s.WorkingShackles = cold?.Shackles is { Work: Locks.ShackleWork.Pick or Locks.ShackleWork.Cut };
        s.InBed = cold is { InSomething: Locks.HeldState.Bed };
        s.Imprisoned = cold is { InSomething: Locks.HeldState.Imprisoned };
        s.CageOpen = cold is { InSomething: Locks.HeldState.Imprisoned, InWhat: { } heldIn }
            && Fixtures.Find(heldIn) is { Kind: Locks.FixtureKind.Cage, Lock: { EffectivelyLocked: false } };
        s.StayingLow = cold?.Medical is { StayingLow: true, Prone: true, Crawling: false };
    }

    /// <summary>
    /// The fight, the shackles, the orders and jobs, the size, the weather notice, the decision trace and the activity line.
    /// </summary>
    void FillOrders(CharacterSnapshot s, int i, in PublishedCharacter v, in PublishFacts f)
    {
        var table = Characters;
        var state = Characters.Previous;
        var id = v.Id;
        var cold = v.Cold;
        bool selected = v.Selected;
        bool shown = v.Shown;
        bool fightsPlayer = v.FightsPlayer;
        var combat = f.Combat;
        var population = f.Population;

        s.InCombat = cold?.InCombat ?? false;
        s.CombatMode = cold is not null && combat is not null && CharacterFacts.InCombatMode(combat, table, id, cold, true, true);
        s.FightsPlayer = fightsPlayer;
        s.HostileToPlayer = cold is not null && CharacterFacts.HostileToPlayer(population?.Relations, Player.Faction, cold.Faction, fightsPlayer);
        s.Shackled = cold?.Inventory is { } worn && WearsLocked(worn);
        s.PortraitSerial = cold?.PortraitSerial ?? 0;
        s.ShacklesLocked = shown && Locks.LockSystem.WornShackles(cold) is not null;
        s.CanBeShackled = shown && cold is { IsPlayer: false } && Locks.LockSystem.CanBeShackled(cold, true);
        s.CarriesShackles = selected && Locks.LockSystem.CarriedShackles(cold) is not null;
        s.Orders = cold?.Orders ?? CharacterOrders.None;
        s.RunSpeed = cold?.RunSpeed ?? SpeedMode.Free;
        s.Jobs = cold is { Jobs.Count: > 0 } ? cold.Jobs.ToArray() : [];
        s.Scale = cold?.Animal is { } animal ? animal.Scale(cold.ShownAge) : 1;   // the size last refreshed (BodySystem.Age)
        s.DrawDistanceScale = cold?.Race is { Gigantic: true } ? RaceData.GiganticDrawScale : 1;
        s.WeatherNotice = cold?.WeatherNotice ?? WeatherNotice.None;
        s.WeatherNoticeProtection = cold?.WeatherNoticeProtection ?? 0;
        s.Decision = Decisions.Tracing ? cold?.Decision : null;
        s.Activity = cold is not null && (shown || cold.IsPlayer) ? Ai.AiSystem.ActivityText(state[i], cold, Tick) : null;
    }

    /// <summary>
    /// What the player may do with it: the turret it mans, the ranged line, stealth and watchers, the bars, the prison, the knock-out and
    /// kidnap chances.
    /// </summary>
    void FillInteraction(CharacterSnapshot s, int i, in PublishedCharacter v, in PublishFacts f)
    {
        var table = Characters;
        var state = Characters.Previous;
        var id = v.Id;
        var cold = v.Cold;
        bool detailed = v.Detailed;
        bool shown = v.Shown;
        bool sneaking = v.Sneaking;
        var watchers = v.Watchers;
        var aggro = f.Aggro;
        var combat = f.Combat;
        int koSlot = f.KoSlot;
        int leadSlot = f.LeadSlot;

        // The turret the combat system runs with this gunner; else (no combat system) at the gun of the turret its squad gave it: the AI's turret
        // goal, walked to and stood at (derived from the claims, not hashed).
        s.Turret = combat?.TurretOf(cold?.Gunning) is { } gunned
            ? new MannedTurret(cold!.Gunning!, gunned.BuildingId, gunned.Position, gunned.Yaw, gunned.GunId)
            {
                Simulated = true,
                Aim = gunned.Aim,
                Angles = gunned.Angles
            }
            : cold is { Ai: { Behaviour: Ai.AiBehaviour.Turret, Squad.Ai: { } board } } && (CharacterTask)state[i].Task == CharacterTask.Idle
                && Ai.AiSystem.Holds(state[i], cold)
                && board.TurretClaims.TryGetValue(i, out int claimed) && (uint)claimed < (uint)board.Turrets.Count
                && board.Turrets[claimed] is { Turret: { } post } building
                    ? new MannedTurret(building.Key, building.BuildingId, post.Position, post.Yaw, post.GunId)
                    : null;
        s.RangedLine = detailed && cold is not null ? combat?.RangedLineOf(this, id, cold) : null;
        s.InPlayerBuilding = cold is { IsPlayer: false } && cold.Faction != Player.Faction
            && (cold.Medical is { Unconscious: true } || Carrying.CarrySystem.Asleep(table, i))
            && _production?.InPlayerBuilding(this, state[i].Position) == true;
        s.BedRecoveryRate = detailed && cold is { InSomething: Locks.HeldState.Bed } ? _systems
            .OfType<BodySystem>()
            .FirstOrDefault()?.BedRecoveryRate(this, cold, in state[i]) : null;
        s.Stealth = sneaking ? watchers.Length == 0 ? StealthExposure.Unobserved : watchers.Max(w => w.Level) : null;
        s.Light = sneaking ? Sight.PeekLight(i) : 1;
        s.Watchers = watchers;
        s.Bar = cold is { IsPlayer: true } ? PickBar(cold, state[i].Task) ?? TrainBar(cold) : null;
        s.Prison = cold is { IsImprisoned: true }
            ? new PrisonView(!(cold.InWhat is { } cageId && Fixtures.Find(cageId) is { } cage && cage.Lock is not { EffectivelyLocked: true }),
                cold.SentenceLength, cold.SentenceStart + cold.SentenceLength - GameHours)
            {
                CageOwner = cold.InWhat is { } owned && Fixtures.Find(owned) is { } jail ? jail.Owner : -1,
                CageId = cold.InWhat,
                LockLevel = cold.InWhat is { } locked && Fixtures.Find(locked)?.Lock is { } cageLock ? cageLock.Level : -1
            }
            : null;
        s.Ragdoll = _ragdolls?.Publish(id);
        s.CanStealthKo = koSlot >= 0 && i != koSlot && cold is { IsPlayer: false } && Carrying.CarrySystem.CanBeKnockedOut(cold);
        s.StealthKoChance = s.CanStealthKo && shown
            ? Carrying.CarrySystem.StealthKoChance(table.Cold(koSlot)!, cold!, aggro?.Spotted(i, table.IdOf(koSlot)) == true)
            : null;
        s.Asleep = Carrying.CarrySystem.Asleep(table, i);
        s.Down = cold is not null && CharacterFacts.Down(cold);
        s.Conscious = cold is not null && CharacterFacts.Conscious(cold);
        s.Standing = cold is not null && CharacterFacts.Standing(cold);
        s.Animal = cold is not null && CharacterFacts.Animal(cold);
        s.CanOpenInventory = cold is not null && Carrying.CarrySystem.Openable(table, i);
        s.KidnapChance = shown && leadSlot >= 0 && i != leadSlot && cold is { IsPlayer: false }
            ? Carrying.CarrySystem.KidnapChance(table.Cold(leadSlot)!, cold, Tick)
            : null;
        s.LockpickSkill = cold is { IsPlayer: true } ? Locks.Lockpicking.Skill(cold) : 0;
    }

    #endregion

    #region Character views

    /// <summary>
    /// How far a sleeper is drawn above its lie spot (engine choice, docs/game/locks.md "Beds"): the bed clips hold <c>Bip01</c> about level with the
    /// mattress top (<c>sleepinbed</c> 6.7 on <c>Bed</c>'s 7.36, <c>sleeponfloor</c> 1.46 on <c>Camp Bed</c>'s 1.73), so the body sank into it.
    /// </summary>
    const float BedLift = 1.5f;

    /// <summary>
    /// The equipment slots a closed section of the inventory can be (<see cref="ClosedSlotsOf"/>).
    /// </summary>
    static readonly string[] ClosableSlots = ["head", "shirt", "boots"];

    /// <summary>
    /// The main grid, when closed (<see cref="ClosedSlotsOf"/>).
    /// </summary>
    static readonly string[] MainGrid = ["main"];

    /// <summary>
    /// Where a character is drawn: a character on the water, which the simulation keeps on the navmesh's flat surface, is lowered to what it stands
    /// on (<see cref="IWalkability.WaterBed"/>: a sunken floor, else the seabed) or, swimming, to <see cref="Bodies.Speed.WadingHeight"/> (the same
    /// surface test as <see cref="MovementSystem"/>'s water state: within <see cref="Bodies.Speed.WaterSlack"/> of the water level). Not a carried or
    /// a downed body. One lying in a bed is raised by <see cref="BedLift"/>.
    /// </summary>
    Vector3 DrawnPosition(CharacterCold? cold, Vector3 at)
    {
        if (cold?.InSomething == Locks.HeldState.Bed) return at with { Y = at.Y + BedLift };

        if (cold is not { Race: { } race, Medical: not { Incapacitated: true } } || !cold.CarriedBy.IsNone
            || at.Y > Data.World.WorldWater.Height + Speed.WaterSlack)
        {
            return at;
        }

        float bed = Walkability.WaterBed(at.X, at.Z, at.Y);
        return bed < at.Y ? at with { Y = Speed.WadingHeight(bed, 1, race.Swims) } : at;
    }

    /// <summary>
    /// The faction the bounty panel calls local (<c>FUN_140852010</c>, docs/game/ui-screens.md section 4): the cage owner's while imprisoned, else
    /// (Meitou's reading of the status object's <c>+0x88</c>) the town's the character stands in; -1 when none.
    /// </summary>
    int LocalFaction(CharacterCold c, Vector3 at, PopulationData? data) =>
        c.IsImprisoned ? c.InWhat is { } cage && Fixtures.Find(cage) is { } f ? f.Owner : -1
        : data is not null && data.TownAt(at) is >= 0 and var site && site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;

    /// <summary>
    /// The build a character is drawn with: the normal maps follow the live stats, the bone shape is made once and rebuilt only while the character
    /// is of the player's faction (<c>FUN_140531820</c> calls <c>FUN_14052e6c0</c> only then; <c>FUN_1405338f0</c> made it at creation, here the
    /// first publish; docs/animation.md "Stat-driven body").
    /// </summary>
    Data.Characters.BodyBuild ShapedBuild(CharacterCold cold, Data.Characters.BodyBuild live)
    {
        if (cold.ShapedBuild is not { } shaped || cold.Faction >= 0 && cold.Faction == Player.Faction)
            cold.ShapedBuild = shaped = live;

        return live.WithShapeOf(shaped);
    }

    /// <summary>
    /// A player character's disguise for the panel (<see cref="Crime.ArrestSystem"/>: the level, the uniform faction, the blown cooldown, the
    /// tooltip's factors).
    /// </summary>
    DisguiseView DisguiseOf(CharacterCold c, Vector3 at, PopulationData? data)
    {
        var marks = c.DisguiseMarks;

        bool Marked(int k) => marks is not null && marks[k] > 0;

        string name = data is { Factions: { } factions } && (uint)c.DisguiseFaction < (uint)factions.Count ? factions[c.DisguiseFaction].Name : "";
        bool secure = _systems.OfType<Crime.ArrestSystem>().FirstOrDefault()?.SecureLocation(this, at) ?? false;
        return new DisguiseView(c.DisguiseLevel, name, c.RecentlyWanted, Crime.ArrestSystem.SeenFaction(c) != c.Faction, secure,
            Marked(Crime.ArrestSystem.SameFactionMark), Marked(Crime.ArrestSystem.SlaveOwnerMark), Marked(Crime.ArrestSystem.KnowsMeMark));
    }

    /// <summary>
    /// The status line of the bounty panel (<c>FUN_1405d4260</c>, <b>Observed</b>, docs/game/ui-screens.md section 4), first match: the leader of a
    /// campaign's leader platoon (<c>Character::isLeadingAWarCampaign</c>): War Leader for a hostile campaign (<c>CampaignData::_isHostile</c>), else
    /// Leader; kidnapped; Diplomatic Status for an NPC class Diplomat (<c>Character::isDiplomaticStatus</c> without its biome test, <b>Verified</b>
    /// by disassembly); for a free character a pending crime, then the escaped-prisoner flag.
    /// </summary>
    StatusView? StatusOf(CharacterId id, CharacterCold c, Vector3 at, PopulationData? data)
    {
        if (c.SquadId >= 0 && Squads.Find(c.SquadId) is { PlatoonId: >= 0 } squad && squad.Leader == id
            && Platoons.Find(squad.PlatoonId) is { Campaign: { } inst } platoon && inst.Leader == platoon.Id)
        {
            return new(_systems.OfType<PopulationSystem>().FirstOrDefault()?.CampaignOf(inst.CampaignId)?.IsHostile == true
                ? StatusKind.WarLeader
                : StatusKind.Leader);
        }

        if (c.Kidnapped) return new(StatusKind.Kidnapped);
        if (c.NpcClass == FundamentalType.Diplomat) return new(StatusKind.Diplomatic);
        if (c.SlaveState != 0) return null;

        if (c.Pending is { } crime)
        {
            return new(StatusKind.Crime, Crime.CrimeTable.Name(crime.Code), (int)crime.Timer,
                c.Bounties is null ? 0 : Crime.CrimeSystem.Lines(c, Tick, TickSeconds, LocalFaction(c, at, data)).FirstOrDefault(b => b.Local).Amount);
        }

        return c.Escaped ? new(StatusKind.EscapedPrisoner) : null;
    }

    /// <summary>
    /// The machine whose <c>animation events</c> sound on the character's clip: the player's operate task while it works (its functionality on
    /// <see cref="CharacterCold.OperateClip"/>), or an AI worker's or obedient slave's standing work while its <see cref="CharacterCold.ActionClip"/>
    /// is still that work's clip; null otherwise.
    /// </summary>
    static WorkEvents? WorkEventsOf(byte task, CharacterCold? cold)
    {
        if (task == (byte)CharacterTask.Operate)
        {
            return cold is { Operating: true, OperateSite: not null, OperateFunctionality: { } works, OperateClip: { } workClip }
                ? new WorkEvents(works, workClip)
                : null;
        }

        return task == (byte)CharacterTask.Idle && cold is { ActionWork: { } standing, ActionClip: { } clip } && clip == standing.Clip
            ? standing
            : null;
    }

    /// <summary>
    /// The inventory sections closed on the character. Out of Fill: a lambda capturing its locals would make the compiler allocate a closure on
    /// every character's fill, selected or not.
    /// </summary>
    static string[] ClosedSlotsOf(CharacterCold cold) =>
        [.. ClosableSlots.Where(slot => Items.InventoryCommands.SectionClosed(cold, slot)),
            .. Items.InventoryCommands.MainClosed(cold) ? MainGrid : []];

    /// <summary>
    /// The health panel's rows: each body part's flesh, stun, bandage and rig.
    /// </summary>
    static IReadOnlyList<HealthPartView> HealthPartsOf(MedicalState health, RaceData anatomy) =>
        Array.AsReadOnly(health.Parts
            .Select(p => new HealthPartView(p.Template.StringId, p.Template.Name, p.Flesh, p.Stun, p.Bandage, p.Rig, p.MaxHp,
                p.IsRobotic(anatomy)) { Vital = p.Template.Vital, Type = p.Template.Type, Limb = p.Limb })
            .ToArray());

    /// <summary>
    /// The inventory window's view: Total Weight is the carried weight (<see cref="Bodies.Encumbrance.CarriedWeight"/>, a carried person included, as
    /// the original's panel <c>FUN_1405d4260</c>, docs/game/ui-screens.md), the maximum and the burden factor from the holder's strength.
    /// </summary>
    InventoryView InventoryViewOf(CharacterCold cold, Items.Inventory grid, PopulationData? population, GameConstants? constants)
    {
        float items = grid.ContentWeight(Player.Held);
        bool carrying = !cold.Carrying.IsNone;
        float strengthMult = cold.Medical?.StatMultiplier(StatsEnumerated.Strength) ?? 1;
        var stats = constants is null ? null : cold.Stats;
        return new InventoryView(grid.Width, grid.Height, grid.Backpack?.Contents?.Width ?? 0, grid.Backpack?.Contents?.Height ?? 0,
            ItemsOf(grid, cold.Faction, population))
        {
            TotalWeight = constants is null ? items : Encumbrance.CarriedWeight(constants, items, carrying),
            CarryCapacity = stats is null ? null : Encumbrance.WindowCapacity(constants!, stats.Strength, strengthMult),
            Burden = stats is null || items <= 0 && !carrying ? 1 : Encumbrance.Factor(constants!, items, carrying, stats.Strength, strengthMult),
        };
    }

    /// <summary>
    /// The inventory panel's items shown in a grid.
    /// </summary>
    static IReadOnlyList<InventoryItemView> ItemsOf(Items.Inventory grid, int faction, PopulationData? population) =>
        Array.AsReadOnly(grid.Shown().Select(shown => new InventoryItemView(shown.Index, shown.Item.Record, shown.Item.Name, shown.Item.Section,
            shown.Item.X, shown.Item.Y, shown.Item.Width, shown.Item.Height, shown.Item.Quantity, shown.Item.Weight, shown.Item.Quality)
        {
            Material = shown.Item.MaterialId,
            Colour = shown.Item.ColourId,
            Charge = shown.Item.ChargeFraction,
            Company = shown.Item.CompanyId,
            Crafter = shown.Item.Crafter,
            Level = shown.Item.Level,
            Charges = shown.Item.Charges,
            MaxCharges = shown.Item.MaxCharges,
            StolenFrom = shown.Item.IsStolenFor(faction, population) ? shown.Item.StolenFrom : -1,
            Unique = shown.Item.Unique,
            BagItems = shown.Item.Contents?.Items.Count ?? -1
        }).ToArray());

    /// <summary>
    /// The bar of Task_PickLock and Task_PickLock_Shackles (docs/game/locks.md "Lockpicking"): "Lockpicking (Chance {p}%)" with the cage penalty,
    /// filled pick time / next-roll time, while the picker is at the lock (not walking to it).
    /// </summary>
    TaskBar? PickBar(CharacterCold c, byte task)
    {
        if (task == (byte)CharacterTask.GoTo) return null;

        var l = c.PickingLock is { } id ? Fixtures.Find(id)?.Lock
            : c.Shackles is { Work: Locks.ShackleWork.Pick } job && Characters.TryResolveNext(job.Wearer, out int w)
                ? Locks.LockSystem.WornShackles(Characters.Cold(w))?.Lock
                : null;
        if (l is not { EffectivelyLocked: true }) return null;

        float chance = Locks.Lockpicking.RollChance(c, l.Level);
        string text = Loc.Tr("Lockpicking (Chance {1}%)", MathF.Round(chance * 100));
        return new TaskBar(text, l.NextRoll > 0 ? Math.Clamp(l.PickTime / l.NextRoll, 0, 1) : 0);
    }

    /// <summary>
    /// The bar of Task_UseTrainingDummy (docs/game/ai-tasks.md "Finders of the jobs"): "Training", filled with the trained stat's fraction, while the
    /// user works the dummy.
    /// </summary>
    TaskBar? TrainBar(CharacterCold c)
    {
        if (!c.Operating || c.OperateSite is not { } site || Machines.Find(site) is not { Data.IsTraining: true } m || c.Stats is not { } stats)
            return null;

        float stat = stats[(StatsEnumerated)m.Data.Stat];
        return new TaskBar("Training", stat - MathF.Floor(stat));
    }

    /// <summary>
    /// The lookers watching a sneaker (its watcher list), for the stealth arrows.
    /// </summary>
    WatcherView[] WatchersOf(CharacterCold cold)
    {
        if (cold.Watchers is not { Count: > 0 } list) return [];

        long lapse = Tick - (long)MathF.Ceiling(AggroSystem.WatcherSeconds / TickSeconds);
        var views = new List<WatcherView>(list.Count);

        foreach (var w in list)
        {
            if (w.Tick >= lapse && Characters.TryResolveNext(w.Looker, out int slot) && Characters.Cold(slot) is { CarriedBy.IsNone: true })
                views.Add(new WatcherView(Characters.Previous[slot].Position, w.Level));
        }

        return [.. views];
    }

    /// <summary>
    /// A locked item worn (not in the main grid): shackles. A loop, not Any with a lambda: it runs for every character on every publish.
    /// </summary>
    static bool WearsLocked(Items.Inventory worn)
    {
        foreach (var item in worn.Items)
            if (item.IsLocked && item.Section != "main") return true;

        return false;
    }

    /// <summary>
    /// The body status of a character: blood, worst part, hunger, its care and whether it needs first aid or repairs.
    /// </summary>
    static BodyStatus BodyOf(CharacterCold cold, MedicalState med, RaceData race, byte task, float extraBloodLoss)
    {
        // The quick right click's tests (FUN_1407fb410): the organic need score above 0.01 is first aid (order 25), else the robot one is robot
        // repair (60).
        float capacity = MathF.Max(MedicalState.BloodCapacity(race, cold.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1);
        var (organicNeed, robotScore) = med.FirstAidNeeds(race, capacity, extraBloodLoss);
        bool organic = organicNeed > MedicalState.AidNeedFloor;
        bool robotNeed = robotScore > MedicalState.AidNeedFloor;
        bool robot = !organic && robotNeed;
        return new(med.Blood / capacity, med.WorstPart, med.Hunger, med.Unconscious, med.Dead, organic || robot,
            task == (byte)CharacterTask.Treat ? CareKind.Treating : task == (byte)CharacterTask.Rest ? CareKind.Resting : CareKind.None,
            cold.MedicJob, cold.RescueJob,
            med.NeedsSplint(race, 50), med.KoTimer, med.PlayingDead, med is { StayingLow: true, Prone: true, Crawling: false }, robot)
        { NeedsOrganicAid = organic, NeedsRepairs = robotNeed, BeingEaten = med.EatenCountdown > 0, Coma = med.Coma, Crawling = med.Crawling,
            RightArmDisabled = !med.ArmWorks(left: false) };
    }

    #endregion

    #region HUD and skills

    /// <summary>
    /// The HUD's numbers of a selected character (docs/game/ui-screens.md section 4). The stats panel lists what the character fights with:
    /// Crossbows, Precision and Perception in ranged mode with a crossbow, else Martial Arts unarmed or Attack and Defense with a melee weapon; then
    /// Strength, Dexterity, Toughness and, but in ranged mode, Dodge; the lines and their tooltips are <see cref="HudTips"/>'s.
    /// </summary>
    HudView HudOf(CharacterId id, CharacterCold cold, MedicalState med, RaceData race, CharacterStats stats, in CharacterHot hot,
        Combat.CombatSystem? combat,
        GameConstants? constants, bool inBuilding, bool inside, Data.GameDatabase? db = null)
    {
        // Movement +0x24 currentlyMoving: the current speed (+0xb8, the Athletics line's too) above 0.1 (CharMovement::update).
        bool moving = hot.Velocity.X * hot.Velocity.X + hot.Velocity.Z * hot.Velocity.Z > 0.1f * 0.1f;
        int mode = combat?.StatsDisplayMode(id, cold, moving) ?? (cold.Fighter is { Unarmed: false } ? 0 : 1);
        var hc = combat?.HudNumbers(this, id.Slot, cold);
        var lines = HudTips.Lines(cold, med, stats, hc, constants, inBuilding, inside, mode);
        var extended = cold.Extended is { } x0 && _systems.OfType<BodySystem>().FirstOrDefault() is { } bodies
            ? bodies.WithCombatMode(x0, cold, combat is not null && CharacterFacts.InCombatMode(combat, Characters, id, cold, true, true))
            : cold.Extended;
        return new HudView(med.Blood, MedicalState.BloodCapacity(race, stats.Strength, med.HasStatsObject, med.ScaleK, med.ScaleZ), med.Bleeding,
            cold.Fighter?.EncumbranceFactor ?? 1, lines.AsReadOnly(), extended)
        {
            BleedRate = race.BleedRate,
            Fed = med.Fed,
            Robot = race.IsRobot,
            ExtendedTips = extended is { } x ? HudTips.Extended(cold, med, race, stats, x, constants) : null,
            Skill = SkillLine(id, cold, med, race, stats, hot, combat, mode),
            KoPoint = constants is null ? null : MedicalState.KoPoint(constants, stats.ScaledToughness),
            StatReadouts = HudTips.StatsWindow(cold, med, race, stats, hc, constants, db)
        };
    }

    /// <summary>
    /// The state panel's "Current Skill" line (the HUD update's panel 3, docs/game/ui-screens.md section 4): the skill the job used this tick at its
    /// effective level with its tooltip (<see cref="SkillTermsOf"/>); else, with the stats display mode 2 or the ranged controller's combat mode, the
    /// crossbow's accuracy error; else, in a melee fight, the class of the weapon in hand at its trained level with its damage; else Stealth when
    /// sneaking, Athletics when moving faster than 9 units a second. Swimming replaces it with the swim speed. Picking a lock is Lockpicking.
    /// </summary>
    CurrentSkillView? SkillLine(CharacterId id, CharacterCold cold, MedicalState med, RaceData race, CharacterStats stats, in CharacterHot hot,
        Combat.CombatSystem? combat,
        int mode)
    {
        float encumbrance = cold.Fighter?.EncumbranceFactor ?? 1;
        if (cold.WaterState == 3)
            return new(StatsEnumerated.Swimming, "Swimming", cold.SwimSpeed, Speed: true) { Swim = SwimTermsOf(cold, med, race, stats, encumbrance) };

        var stat = cold.SkillInUseTick >= Tick - 1
            ? cold.SkillInUse
            : PickBar(cold, hot.Task) is not null ? StatsEnumerated.Lockpicking : StatsEnumerated.None;

        if (stat != StatsEnumerated.None)
        {
            var terms = SkillTermsOf(cold, med, stats, stat);
            return new(stat, null, MathF.Round(terms.Total), terms.Penalised, Terms: terms);
        }

        // getGUIStatsDisplayMode 2 or the ranged controller's combat mode (a shooting fight, a manned turret; no target test), with a crossbow: its
        // accuracy with Crossbows, or with Turrets when Character +0x3d8 (the last weapon category the HUD saw in a melee fight) is 7.
        bool aiming = mode == 2 || Combat.CombatSystem.RangedCombatMode(cold);

        if (aiming)
        {
            var aimStat = cold.LastWeaponCategory == (int)Data.Gameplay.Combat.WeaponCategory.Turret
                ? StatsEnumerated.Turrets
                : StatsEnumerated.Crossbows;
            if (Combat.CombatSystem.HudAccuracy(cold, aimStat) is { } aim)
                return new(aimStat, "Accuracy Error:", aim.Deviation) { Accuracy = new(aim.Weapon, aim.Skill, aim.Deviation) };
        }
        else if (combat?.MeleeCombatMode(id, cold) == true)
        {
            // The HUD writes Character +0x3d8 from the weapon in hand (getCurrentWeapon; the FISTS weapon with nothing drawn), then labels by it. The
            // only writer besides the constructor, so this display state lives on the character; each character is published by one worker, so the
            // write does not race.
            cold.LastWeaponCategory = Combat.CombatSystem.HandCategory(cold);
            var (label, skill) = cold.LastWeaponCategory switch
            {
                3 => ("Heavy:", StatsEnumerated.HeavyWeapons),
                0 => ("Katana:", StatsEnumerated.Katanas),
                4 => ("Hacker:", StatsEnumerated.Hackers),
                1 => ("Sabre:", StatsEnumerated.Sabres),
                5 => ("Martial Arts:", StatsEnumerated.MartialArts),
                8 => ("Polearm:", StatsEnumerated.Polearms),
                _ => ((string?)null, StatsEnumerated.None),
            };
            if (label is not null)
                return new(skill, label, (int)stats[skill]) { Damage = combat.HudDamage(this, id.Slot, cold) };
        }

        if ((cold.Orders & CharacterOrders.Sneak) != 0)
        {
            float s = Orders.Stealth(cold);
            int total = (int)(s * 100), trained = (int)stats[StatsEnumerated.Stealth];
            float t = MathF.Max(0, (s - 0.1f) / 0.8f), top = hot.MaxSpeed;
            return new(StatsEnumerated.Stealth, "Stealth:", total)
            {
                Base = trained,
                Stealth = new(trained, 12 + (top - 12) * t, XpService.SneakRate(BodySystem.PaidExposureOf(this, cold)),
                !cold.Carrying.IsNone, med.Prone, (int)((1 - encumbrance) * 100), (int)((med.StealthBodyFactor(stats.ScaledToughness) - 1) * 100),
                (int)(((cold.Fighter?.StealthMult ?? 1) - 1) * 100), (int)((med.LegItemFactor(StatsEnumerated.Stealth) - 1) * 100), total)
            };
        }

        float speed = MathF.Sqrt(hot.Velocity.X * hot.Velocity.X + hot.Velocity.Z * hot.Velocity.Z);
        if (speed <= 9) return null;

        return new(StatsEnumerated.Athletics, "Athletics:", (int)stats.Athletics)
        {
            Athletics = new((int)stats.Athletics, Speed.RunValue(race, stats.Athletics), (int)((1 - Speed.LegFactor(med)) * 100),
                (int)((1 - encumbrance) * 100), cold.Fighter?.AthleticsFactor ?? 1, (int)((med.LegItemFactor(StatsEnumerated.Athletics) - 1) * 100),
                hot.MaxSpeed)
        };
    }

    /// <summary>
    /// The Swimming line's tooltip (the HUD update): the trained level, the base swim speed (<see cref="Bodies.Speed.SwimBase"/>), the leg term with
    /// 0.6 per unusable arm, the hunger term, the encumbrance and the limb items' factor (all four limbs) as percentages, whether both arms are
    /// broken, and the current swim speed.
    /// </summary>
    static SwimTerms SwimTermsOf(CharacterCold cold, MedicalState med, RaceData race, CharacterStats stats, float encumbrance)
    {
        bool left = med.ArmWorks(left: true), right = med.ArmWorks(left: false);
        float legs = Speed.LegFactor(med) * (left ? 1 : 0.6f) * (right ? 1 : 0.6f);
        float items = med.LegItemFactor(StatsEnumerated.Swimming) * med.ArmItemFactor(StatsEnumerated.Swimming);
        return new(stats[StatsEnumerated.Swimming], Speed.SwimBase(race, stats), (int)((1 - legs) * 100),
            (int)((1 - Speed.HungerFactor(med.Hunger)) * 100),
            (int)((1 - encumbrance) * 100), (int)((1 - items) * 100), !left && !right, cold.SwimSpeed);
    }

    /// <summary>
    /// A skill's tooltip terms (<c>FUN_140890b00</c>): the stat multiplier (<c>FUN_1406457e0</c>) asked for one term at a time, injuries (the limb
    /// term), hunger, weather (the dust factor), darkness (the light term), robotics (fitted limbs and the disabled-arm 0.66) and, but for strength
    /// and perception, the worn gear (perception has its armour bonus, added to the level before the multiplier, instead); for strength the 0.66 of a
    /// disabled arm again as the missing arm penalty. The total is the stat with every term.
    /// </summary>
    internal static (float Level, int Penalty) EffectiveSkill(CharacterCold cold, MedicalState med, CharacterStats stats, StatsEnumerated stat)
    {
        var factors = (cold.Fighter?.Factors ?? StatFactors.None) with { Stun = med.DustStatFactor };
        float multiplier = med.StatMultiplier(stat, StatUse.All, factors);
        return ((stats[stat] + Combat.CombatSystem.GearBonus(cold, stat)) * multiplier, (int)((1 - multiplier) * 100));
    }

    /// <summary>
    /// The terms of a skill's effective level for the skill line: the stat factors and penalties that apply to it.
    /// </summary>
    static SkillTerms SkillTermsOf(CharacterCold cold, MedicalState med, CharacterStats stats, StatsEnumerated stat)
    {
        var factors = (cold.Fighter?.Factors ?? StatFactors.None) with { Stun = med.DustStatFactor };

        int Term(StatUse use) => (int)((1 - med.StatMultiplier(stat, use, factors)) * 100);

        bool robotics = med.Parts.Any(p => p.Limb == LimbState.Replaced && p.Replacement is not null);
        int gear = stat is StatsEnumerated.Strength or StatsEnumerated.Perception ? 0 : Term(StatUse.LimbItems);
        int missingArm = stat == StatsEnumerated.Strength && med.Aggregates().EitherArmDisabled ? 33 : 0;
        int bonus = Combat.CombatSystem.GearBonus(cold, stat);
        return new(stats[stat], Term(StatUse.Limbs), Term(StatUse.Hunger), Term(StatUse.Stun), Term(StatUse.Light), Term(StatUse.DamageState),
            robotics, gear, missingArm, (stats[stat] + bonus) * med.StatMultiplier(stat, StatUse.All, factors), bonus);
    }

    #endregion
}