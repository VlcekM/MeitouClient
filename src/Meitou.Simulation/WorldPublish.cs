using System.Numerics;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation;

public sealed partial class World
{
    internal Partition PartitionAt(int i) => partitions[i];
    internal EffectBuffer BufferAt(int i) => buffers[i];

    /// <summary>
    /// Where a character is drawn: a character on the water, which the simulation keeps on the navmesh's flat surface, is lowered to the seabed or, swimming, to <see cref="Bodies.Speed.WadingHeight"/>
    /// (the same surface test as <see cref="MovementSystem"/>'s water state: within <see cref="Bodies.Speed.WaterSlack"/> of the water level). Not a carried or a downed body.
    /// </summary>
    Vector3 DrawnPosition(CharacterCold? cold, Vector3 at)
    {
        if (cold is not { Race: { } race, Medical: not { Incapacitated: true } } || !cold.CarriedBy.IsNone || at.Y > Meitou.Data.World.WorldWater.Height + Bodies.Speed.WaterSlack) return at;
        float terrain = Walkability.TerrainHeight(at.X, at.Z);
        return terrain < at.Y ? at with { Y = Bodies.Speed.WadingHeight(terrain, 1, race.Swims) } : at;
    }

    /// <summary>
    /// The faction the bounty panel calls local (<c>FUN_140852010</c>, docs/game/ui-screens.md section 4): the cage owner's while imprisoned, else (Meitou's reading of the status
    /// object's <c>+0x88</c>) the town's the character stands in; -1 when none.
    /// </summary>
    int LocalFaction(CharacterCold c, Vector3 at, PopulationData? data) =>
        c.IsImprisoned ? c.InWhat is { } cage && Fixtures.Find(cage) is { } f ? f.Owner : -1
        : data is not null && data.TownAt(at) is >= 0 and var site && site < data.SiteFactions.Count ? data.SiteFactions[site] : -1;

    /// <summary>Builds the <see cref="WorldSnapshot"/> of the tick that just ended from the state it left in <see cref="CharacterTable.Previous"/>.</summary>
    void Publish()
    {
        var table = Characters;
        var list = new List<CharacterSnapshot>(table.Count);
        var state = table.Previous;
        // The selection's first member that could knock someone out from behind: the stealth KO chances are against it.
        int koSlot = -1;
        foreach (var s in Player.Selection)
            if (table.TryResolveNext(s, out int k) && table.Cold(k) is { } c && Carrying.CarrySystem.CanKnockOut(c)) { koSlot = k; break; }
        var aggro = koSlot >= 0 ? systems.OfType<AggroSystem>().FirstOrDefault() : null;
        var talk = systems.OfType<Dialogue.DialogueSystem>().FirstOrDefault();
        var combat = systems.OfType<Combat.CombatSystem>().FirstOrDefault();
        var population = systems.OfType<PopulationSystem>().FirstOrDefault()?.Data;
        for (int i = 0; i < state.Length; i++)
        {
            if (!state[i].Alive) continue;
            var id = new CharacterId(i, state[i].Generation);
            var cold = table.Cold(i);
            // The details only the selection shows: asked once per character, the selection is a list.
            bool selected = cold is { IsPlayer: true } && Player.Selection.Contains(id), inspected = !selected && id == Player.Inspected, detailed = selected || inspected;
            bool sneaking = cold is { IsPlayer: true } && (cold.Orders & CharacterOrders.Sneak) != 0;
            var watchers = sneaking ? WatchersOf(cold!) : [];
            list.Add(new CharacterSnapshot(id, cold?.Appearance, DrawnPosition(cold, state[i].Position), state[i].Yaw, cold?.Animation is { } anim && animations is not null ? animations.Publish(anim) : AnimationLayers.For(state[i].Animation, state[i].AnimationTime))
            {
                Faction = cold?.Faction ?? -1,
                WeaponDrawn = cold is not null && cold.DrawnWeapon != Meitou.Data.Gameplay.WeaponKinds.None,
                CrossbowShots = cold?.CrossbowShots ?? 0,
                StuckBolts = cold?.StuckBolts is { Count: > 0 } bolts ? bolts.ToArray() : [],
                RangedDrawn = cold is not null && cold.DrawnWeapon != Meitou.Data.Gameplay.WeaponKinds.None
                    && cold.DrawnWeapon == Meitou.Data.Gameplay.HandHold.OfCategory((int)Meitou.Data.Gameplay.Combat.WeaponCategory.Bow).Weapon,
                BackDrawn = cold?.Fighter?.WeaponIsBack ?? false,
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
                Hud = detailed && cold!.Medical is { } hudMed && cold.Race is { } hudRace && cold.Stats is { } hudStats ? HudOf(cold, hudMed, hudRace, hudStats) : null,
                LimbBits = cold?.Medical?.LimbBits ?? 0,
                HealthParts = detailed && cold!.Medical is { } health && cold.Race is { } anatomy ? Array.AsReadOnly(health.Parts.Select(p => new HealthPartView(p.Template.StringId, p.Template.Name, p.Flesh, p.Stun, p.Bandage, p.Rig, p.MaxHp, p.IsRobotic(anatomy)) { Vital = p.Template.Vital, Type = p.Template.Type }).ToArray()) : [],
                ItemGrid = (selected || id == Player.OpenLoot || cold is { IsPlayer: true } && Player.InventoryShown.Contains(id)) && cold!.Inventory is { } grid ? new InventoryView(grid.Width, grid.Height, grid.Backpack?.Contents?.Width ?? 0, grid.Backpack?.Contents?.Height ?? 0,
                    Array.AsReadOnly(grid.All().Select((item, index) => new InventoryItemView(index, item.Record, item.Name, item.Section, item.X, item.Y, item.Width, item.Height, item.Quantity, item.Weight, item.Quality) { Material = item.MaterialId, Charge = item.ChargeFraction, Company = item.CompanyId, Level = item.Level, Charges = item.Charges, MaxCharges = item.MaxCharges,
                        StolenFrom = item.IsStolenFor(cold.Faction) ? item.StolenFrom : -1 }).ToArray()))
                    { TotalWeight = grid.ContentWeight(), CarryCapacity = cold.Stats is { } strength && systems.OfType<BodySystem>().FirstOrDefault() is { } bodies
                        ? Bodies.Encumbrance.Capacity(bodies.Constants, strength.Strength, cold.Medical?.StatMultiplier(StatsEnumerated.Strength) ?? 1) : null } : null,
                Bounties = detailed && cold!.Bounties is not null ? Crime.CrimeSystem.Lines(cold, Tick, TickSeconds, LocalFaction(cold, state[i].Position, population)) : [],
                Slave = detailed && cold!.SlaveState is 1 or 2 ? new SlaveView(cold.SlaveState, population is { Factions: { } factions } && (uint)cold.SlaveOwner < (uint)factions.Count ? factions[cold.SlaveOwner].Name : "") : null,
                Body = cold?.Medical is { } med && cold.Race is { } race ? BodyOf(cold, med, race, state[i].Task) : null,
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
                InCombat = cold?.InCombat ?? false,
                FightsPlayer = cold is { IsPlayer: false, InCombat: true } && combat?.StateOf(id) is { TargetSlot: >= 0 and var fought } fight
                    && fought < state.Length && state[fought].Alive && state[fought].Generation == fight.TargetGeneration && table.Cold(fought) is { IsPlayer: true },
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
                Activity = cold is { Ai.Goal: { } goal } && Ai.AiSystem.Holds(state[i], cold) ? Ai.TaskBehaviours.Text(goal.Task.Type) : null,
                // The turret the combat system runs with this gunner; else (no combat system) at the gun of the turret its squad gave it: the AI's turret goal, walked to and stood at
                // (derived from the claims, not hashed).
                Turret = combat?.TurretOf(cold?.Gunning) is { } gunned ? new MannedTurret(cold!.Gunning!, gunned.BuildingId, gunned.Position, gunned.Yaw, gunned.GunId) { Simulated = true, Aim = gunned.Aim, Angles = gunned.Angles }
                    : cold is { Ai: { Behaviour: Ai.AiBehaviour.Turret, Squad.Ai: { } board } } && (CharacterTask)state[i].Task == CharacterTask.Idle && Ai.AiSystem.Holds(state[i], cold)
                    && board.TurretClaims.TryGetValue(i, out int claimed) && (uint)claimed < (uint)board.Turrets.Count && board.Turrets[claimed] is { Turret: { } post } building
                    ? new MannedTurret(building.Key, building.BuildingId, post.Position, post.Yaw, post.GunId) : null,
                Stealth = sneaking ? watchers.Length == 0 ? StealthExposure.Unobserved : watchers.Max(w => w.Level) : null,
                Light = sneaking ? Sight.Light(i) : 1,
                Watchers = watchers,
                Bar = cold is { IsPlayer: true } ? PickBar(cold, state[i].Task) : null,
                Prison = cold is { IsImprisoned: true } ? new PrisonView(!(cold.InWhat is { } cageId && Fixtures.Find(cageId) is { } cage && cage.Lock is not { EffectivelyLocked: true }), cold.SentenceLength, cold.SentenceStart + cold.SentenceLength - GameHours) { CageOwner = cold.InWhat is { } owned && Fixtures.Find(owned) is { } jail ? jail.Owner : -1, CageId = cold.InWhat } : null,
                Ragdoll = ragdolls?.Publish(id),
                StealthKoChance = koSlot >= 0 && i != koSlot && cold is { IsPlayer: false } && Carrying.CarrySystem.CanBeKnockedOut(cold)
                    ? Carrying.CarrySystem.StealthKoChance(table.Cold(koSlot)!, cold, aggro?.Spotted(i, table.IdOf(koSlot)) == true) : null,
            });
        }
        PreviousSnapshot = Snapshot;
        Snapshot = new WorldSnapshot(Tick, list) { Projectiles = projectiles?.PublishProjectiles() ?? [], Trade = trades?.PublishTrade(), Conversation = dialogues?.PublishConversation(), CharacterTrade = dialogues?.PublishCharacterTrade(), Speech = PublishSpeech(), DialogueRequest = dialogues?.PublishRequest(), Towns = Player.Towns, Camps = Player.Camps, SquadAi = Player.SquadAi, PlayerSquads = PublishSquads(), ActiveSquad = Player.Squad, OpenLoot = Player.OpenLoot, LootOpener = Player.LootOpener,
            Held = Player.Held is { } held ? Items.HeldItemView.Of(held) : null, HeldSerial = Player.HeldSerial,
            Buildings = Buildings.Published(), BuildingsRevision = Buildings.Revision, GroundItems = GroundItems.Published(), GroundItemsRevision = GroundItems.Revision, UnlockedBuildings = Research.Unlocked, Doors = Fixtures.PublishedDoors(), Containers = Fixtures.ContainerSpots, Beds = Fixtures.BedSpots, Cages = Fixtures.CageSpots(Characters), DoorPicks = Fixtures.DoorPicks, OwnedTownBuildings = Fixtures.OwnedProperties(Player.Faction), InspectedBuilding = Fixtures.DetailOf(Player.InspectedBuilding, Player.Faction), InspectedRichness = production?.RichnessOf(this, Player.InspectedBuilding),
            Machines = OpenContainerView() is { } container ?[.. production is null ? [] : Machines.Published(production.Items), container] : production is null ? [] : Machines.Published(production.Items), MachinesVersion = Machines.Version, Research = production?.ResearchView(this) ?? Production.ResearchSnapshot.Empty,
            Crafts = production?.CraftsView(this) ?? Snapshot.Crafts };
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
        var grid = PlayerSystem.GridOf(this, f);
        var storage = new InventoryView(grid.Width, grid.Height, 0, 0,
            Array.AsReadOnly(grid.Items.Select((item, index) => new InventoryItemView(index, item.Record, item.Name, "main", item.X, item.Y, item.Width, item.Height, item.Quantity, item.Weight, item.Quality)
                { Material = item.MaterialId, Charge = item.ChargeFraction, Company = item.CompanyId, Level = item.Level, Charges = item.Charges, MaxCharges = item.MaxCharges }).ToArray()));
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

    /// <summary>The sneaker's watchers of the last <see cref="AggroSystem.WatcherSeconds"/> that are still about, where they stand now.</summary>
    SquadView[] PublishSquads() => [.. SquadManagement.PlayerSquads(this).Select(s => new SquadView(s.Id, s.Name, [.. s.Members]))];

    WatcherView[] WatchersOf(CharacterCold cold)
    {
        if (cold.Watchers is not { Count: > 0 } list) return [];
        long lapse = Tick - (long)MathF.Ceiling(AggroSystem.WatcherSeconds / TickSeconds);
        var views = new List<WatcherView>(list.Count);
        foreach (var w in list)
            if (w.Tick >= lapse && Characters.TryResolveNext(w.Looker, out int slot)) views.Add(new WatcherView(Characters.Previous[slot].Position, w.Level));
        return [.. views];
    }

    /// <summary>
    /// The HUD's numbers of a selected character (docs/game/ui-screens.md section 4). The stats panel lists what the character fights with: Crossbows, Precision and
    /// Perception in ranged mode with a crossbow, else Martial Arts unarmed or Attack and Defense with a melee weapon; then Strength, Dexterity, Toughness and, but
    /// in ranged mode, Dodge. A value is the stat times its injury multiplier (and the fighter's dexterity or dodge factor), against the trained stat; toughness is plain.
    /// </summary>
    static HudView HudOf(CharacterCold cold, Bodies.MedicalState med, RaceData race, Bodies.CharacterStats stats)
    {
        var fighter = cold.Fighter;
        HudStatLine Line(string label, StatsEnumerated stat, float factor = 1) => new(label, stats[stat] * med.StatMultiplier(stat) * factor, stats[stat]);
        var lines = new List<HudStatLine>(6);
        bool ranged = (cold.Orders & CharacterOrders.Ranged) != 0 && fighter?.Crossbow is not null;
        if (ranged) { lines.Add(Line("Crossbows:", StatsEnumerated.Crossbows)); lines.Add(Line("Precision:", StatsEnumerated.FriendlyFire)); lines.Add(Line("Perception:", StatsEnumerated.Perception)); }
        else if (fighter is null || fighter.Unarmed) lines.Add(Line("Martial Arts:", StatsEnumerated.MartialArts));
        else { lines.Add(Line("Attack:", StatsEnumerated.MeleeAttack)); lines.Add(Line("Defense:", StatsEnumerated.MeleeDefence)); }
        lines.Add(Line("Strength:", StatsEnumerated.Strength));
        lines.Add(Line("Dexterity:", StatsEnumerated.Dexterity, fighter?.DexterityMult ?? 1));
        lines.Add(new("Toughness:", stats.Toughness, stats.Toughness));
        if (!ranged) lines.Add(Line("Dodge:", StatsEnumerated.Dodge, fighter?.DodgeMult ?? 1));
        return new HudView(med.Blood, Bodies.MedicalState.BloodCapacity(race, stats.Strength, med.HasStatsObject, med.ScaleK, med.ScaleZ), med.Bleeding,
            fighter?.EncumbranceFactor ?? 1, lines.AsReadOnly(), cold.Extended) { BleedRate = race.BleedRate };
    }

    static BodyStatus BodyOf(CharacterCold cold, Bodies.MedicalState med, RaceData race, byte task) => new(
        med.Blood / MathF.Max(Bodies.MedicalState.BloodCapacity(race, cold.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1), WorstPart(med), med.Hunger, med.Unconscious, med.Dead,
        cold.IsPlayer && (med.NeedsAid(race, false) || med.NeedsAid(race, true)),
        task == (byte)CharacterTask.Treat ? CareKind.Treating : task == (byte)CharacterTask.Rest ? CareKind.Resting : CareKind.None, cold.MedicJob, cold.RescueJob,
        cold.IsPlayer && med.NeedsSplint(race, 50), med.KoTimer);

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
