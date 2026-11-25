using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using Meitou.Tests.Bodies;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

/// <summary>
/// Unit tests for the character lifecycle/state observers, dialogue choice hooks and audio modulation API.
/// </summary>
public class CharacterObserverTests
{
    #region Helpers

    /// <summary>
    /// Creates a minimal synthetic world with the given extra systems and spawns one character with medical state and inventory.
    /// </summary>
    static (SimWorld World, CharacterId Id, CharacterCold Cold) MakeWorld(params ITickSystem[] systems)
    {
        var ground = new OpenGroundWalkability((_, _) => 150);
        var world = new SimWorld(new WorldSettings { Seed = 7, Threads = 1, PublishSnapshots = false }, ground, systems);
        world.Player.Faction = 1;
        var cold = new CharacterCold
        {
            Name = "tester",
            RecordId = "rec-test",
            Faction = 2,
            Medical = BodyFixtures.Healthy(),
            Inventory = new Inventory(),
        };
        var id = world.Characters.Spawn(new CharacterHot
        {
            Position = new Vector3(0, 150, 0),
            Health = 100,
            MaxSpeed = 60,
            WalkSpeed = 15,
            Task = (byte)CharacterTask.Idle,
        }, cold, 0);
        return (world, id, cold);
    }

    /// <summary>
    /// Recording observer implementing all character lifecycle and state observer interfaces.
    /// </summary>
    sealed class Recorder : ITickSystem, IKnockoutObserver, ILimbObserver, ICarryObserver, IHeldStateObserver,
        IFactionChangeObserver, IRecruitObserver, IEquipObserver, ICharacterRemovedObserver
    {
        /// <summary>
        /// Recorded knockout / wake-up events.
        /// </summary>
        public readonly List<KnockoutChange> Knockouts = [];
        /// <summary>
        /// Recorded limb state transitions.
        /// </summary>
        public readonly List<LimbChange> Limbs = [];
        /// <summary>
        /// Recorded carry pick-up / put-down events.
        /// </summary>
        public readonly List<CarryChange> Carries = [];
        /// <summary>
        /// Recorded bed / cage held-state events.
        /// </summary>
        public readonly List<HeldStateChange> Held = [];
        /// <summary>
        /// Recorded faction changes.
        /// </summary>
        public readonly List<FactionChange> Factions = [];
        /// <summary>
        /// Recorded player-faction join / leave events.
        /// </summary>
        public readonly List<RecruitChange> Recruits = [];
        /// <summary>
        /// Recorded equipment changes.
        /// </summary>
        public readonly List<EquipChange> Equips = [];
        /// <summary>
        /// Recorded character removals.
        /// </summary>
        public readonly List<CharacterRemoved> RemovedEvents = [];
        /// <summary>
        /// Whether the removed character was still resolvable in the table during <see cref="Removed"/>.
        /// </summary>
        public bool ReadableOnRemove;

        /// <inheritdoc />
        public void KnockoutChanged(SimWorld world, in KnockoutChange change) => Knockouts.Add(change);
        /// <inheritdoc />
        public void LimbChanged(SimWorld world, in LimbChange change) => Limbs.Add(change);
        /// <inheritdoc />
        public void CarryChanged(SimWorld world, in CarryChange change) => Carries.Add(change);
        /// <inheritdoc />
        public void HeldStateChanged(SimWorld world, in HeldStateChange change) => Held.Add(change);
        /// <inheritdoc />
        public void FactionChanged(SimWorld world, in FactionChange change) => Factions.Add(change);
        /// <inheritdoc />
        public void RecruitChanged(SimWorld world, in RecruitChange change) => Recruits.Add(change);
        /// <inheritdoc />
        public void EquipChanged(SimWorld world, in EquipChange change) => Equips.Add(change);
        /// <inheritdoc />
        public void Removed(SimWorld world, in CharacterRemoved removed)
        {
            ReadableOnRemove = world.Characters.TryResolveNext(removed.Character, out _);
            RemovedEvents.Add(removed);
        }
    }

    /// <summary>
    /// Observer that throws on the first faction change, to verify fault isolation and state hashing.
    /// </summary>
    sealed class ThrowingFactionObserver : ITickSystem, IFactionChangeObserver
    {
        /// <summary>
        /// How many times the observer was invoked.
        /// </summary>
        public int Calls;

        /// <inheritdoc />
        public void FactionChanged(SimWorld world, in FactionChange change)
        {
            Calls++;
            throw new InvalidOperationException("boom");
        }
    }

    /// <summary>
    /// Dialogue choice observer + optional filter for testing <see cref="DialogueSystem"/>.
    /// </summary>
    sealed class DialogueHook(Func<DialogueChoiceEvent, bool>? claim = null) : ITickSystem, IDialogueChoiceFilter, IDialogueChoiceObserver
    {
        /// <summary>
        /// All observed choices.
        /// </summary>
        public readonly List<DialogueChoiceEvent> Observed = [];

        /// <inheritdoc />
        public bool HandleChoice(SimWorld world, in DialogueChoiceEvent choice) => claim?.Invoke(choice) == true;
        /// <inheritdoc />
        public void ChoicePicked(SimWorld world, in DialogueChoiceEvent choice) => Observed.Add(choice);
    }

    #endregion

    #region Character observers

    /// <summary>
    /// First observation captures baseline without firing spurious events; subsequent knockout, limb, carry, held, faction, recruit and equip
    /// changes fire exact events.
    /// </summary>
    [Fact]
    public void Character_state_observers_capture_baseline_and_report_transitions()
    {
        var rec = new Recorder();
        var (world, id, cold) = MakeWorld(rec);
        using var _ = world;

        // Baseline tick: no spurious events.
        world.RunTick();
        Assert.Empty(rec.Knockouts);
        Assert.Empty(rec.Limbs);
        Assert.Empty(rec.Carries);
        Assert.Empty(rec.Held);
        Assert.Empty(rec.Factions);
        Assert.Empty(rec.Recruits);
        Assert.Empty(rec.Equips);

        // Knock out + sever a limb.
        var medical = cold.Medical!;
        medical.KnockOut(30);
        int armIndex = 0;
        for (int i = 0; i < medical.Parts.Count; i++)
        {
            if (medical.Parts[i].Template.Type == Data.Gameplay.Bodies.BodyPartType.Arm)
            {
                armIndex = i;
                break;
            }
        }
        medical.Parts[armIndex].Limb = LimbState.Stump;

        // Carry, cage, recruit, equip.
        var partner = new CharacterId(99, 1);
        cold.Carrying = partner;
        cold.InSomething = HeldState.Imprisoned;
        cold.InWhat = "cage-1";
        cold.Faction = world.Player.Faction;
        cold.Inventory!.Items.Add(new ItemInstance { Record = "helmet-1", Section = "head", Width = 1, Height = 1 });
        cold.Inventory.Items.Add(new ItemInstance { Record = "ration-1", Section = "main", Width = 1, Height = 1 });

        world.RunTick();

        Assert.Single(rec.Knockouts);
        Assert.True(rec.Knockouts[0].Unconscious);

        Assert.Single(rec.Limbs);
        Assert.Equal((armIndex, LimbState.Original, LimbState.Stump, true),
            (rec.Limbs[0].Part, rec.Limbs[0].Before, rec.Limbs[0].After, rec.Limbs[0].Lost));

        Assert.Single(rec.Carries);
        Assert.Equal((id, partner, true), (rec.Carries[0].Carrier, rec.Carries[0].Carried, rec.Carries[0].PickedUp));

        Assert.Single(rec.Held);
        Assert.Equal((HeldState.None, HeldState.Imprisoned, "cage-1"),
            (rec.Held[0].Before, rec.Held[0].After, rec.Held[0].Fixture));

        Assert.Single(rec.Factions);
        Assert.Equal((2, 1), (rec.Factions[0].Before, rec.Factions[0].After));

        Assert.Single(rec.Recruits);
        Assert.Equal((2, true), (rec.Recruits[0].OtherFaction, rec.Recruits[0].Joined));

        Assert.Single(rec.Equips);
        Assert.Equal(("head", "helmet-1", true), (rec.Equips[0].Section, rec.Equips[0].ItemRecord, rec.Equips[0].Equipped));

        // Reverse transitions: wake up, prosthetic, drop, leave cage, dismiss, unequip.
        medical.WakeUp();
        medical.Parts[armIndex].Limb = LimbState.Replaced;
        cold.Carrying = CharacterId.None;
        cold.InSomething = HeldState.None;
        cold.InWhat = null;
        cold.Faction = 3;
        cold.Inventory.Items.RemoveAll(i => i.Section == "head");

        world.RunTick();

        Assert.Equal(2, rec.Knockouts.Count);
        Assert.False(rec.Knockouts[1].Unconscious);

        Assert.Equal(2, rec.Limbs.Count);
        Assert.Equal((LimbState.Stump, LimbState.Replaced, false),
            (rec.Limbs[1].Before, rec.Limbs[1].After, rec.Limbs[1].Lost));

        Assert.Equal(2, rec.Carries.Count);
        Assert.False(rec.Carries[1].PickedUp);

        Assert.Equal(2, rec.Held.Count);
        Assert.Equal((HeldState.Imprisoned, HeldState.None, "cage-1"),
            (rec.Held[1].Before, rec.Held[1].After, rec.Held[1].Fixture));

        Assert.Equal(2, rec.Recruits.Count);
        Assert.Equal((3, false), (rec.Recruits[1].OtherFaction, rec.Recruits[1].Joined));

        Assert.Equal(2, rec.Equips.Count);
        Assert.Equal(("head", "helmet-1", false), (rec.Equips[1].Section, rec.Equips[1].ItemRecord, rec.Equips[1].Equipped));
    }

    /// <summary>
    /// Removing a character flushes pending state transitions first and fires <see cref="ICharacterRemovedObserver"/> while the character is still
    /// readable in the table.
    /// </summary>
    [Fact]
    public void Character_removed_observer_flushes_state_and_runs_while_readable()
    {
        var rec = new Recorder();
        var (world, id, cold) = MakeWorld(rec);
        using var _ = world;

        world.RunTick();
        cold.Faction = 4;
        world.Characters.Remove(id);

        Assert.Single(rec.Factions);
        Assert.Equal((2, 4), (rec.Factions[0].Before, rec.Factions[0].After));
        Assert.Single(rec.RemovedEvents);
        Assert.True(rec.ReadableOnRemove);
        Assert.Equal((id, "rec-test", 4, false),
            (rec.RemovedEvents[0].Character, rec.RemovedEvents[0].Record, rec.RemovedEvents[0].Faction, rec.RemovedEvents[0].Dead));
    }

    /// <summary>
    /// A throwing observer is disabled for that world, other observers continue running, and the fault changes the world's state hash.
    /// </summary>
    [Fact]
    public void Throwing_observer_is_disabled_and_enters_state_hash()
    {
        var bad = new ThrowingFactionObserver();
        var good = new Recorder();
        var (world, _, cold) = MakeWorld(bad, good);
        using var _ = world;

        world.RunTick();
        ulong before = world.StateHash();

        cold.Faction = 5;
        world.RunTick();
        Assert.Equal(1, bad.Calls);
        Assert.Single(good.Factions);

        cold.Faction = 6;
        world.RunTick();
        Assert.Equal(1, bad.Calls);
        Assert.Equal(2, good.Factions.Count);
        Assert.NotEqual(before, world.StateHash());
    }

    #endregion

    #region Dialogue choice hooks

    /// <summary>
    /// <see cref="IDialogueChoiceFilter"/> returning true claims a reply and ends the conversation without advancing; returning false lets vanilla
    /// dialogue run, and <see cref="IDialogueChoiceObserver"/> observes both.
    /// </summary>
    [Fact]
    public void Dialogue_choice_filter_can_claim_or_pass_and_observer_hears_choice()
    {
        static FcsReference Ref(string id, int v0 = 0) => new(id, v0, 0, 0);

        var data = PopulationData.Create(Db([
            Rec("204-gamedata.base", FcsRecordType.Faction, r => r.Name = "Nameless"),
            Rec("nf", FcsRecordType.Faction, r => r.Name = "NPCs"),
            Rec("npc-c", FcsRecordType.Character, r => { r.Name = "Dealer"; r.References["dialogue"] = [Ref("dlg-c")]; }),
            Rec("dlg-c", FcsRecordType.Dialogue, r => r.References["lines"] = [Ref("greet-c")]),
            Rec("greet-c", FcsRecordType.DialogueLine, r =>
            {
                r.Strings["text0"] = "Want to play?";
                r.References["lines"] = [Ref("yes-c")];
            }),
            Rec("yes-c", FcsRecordType.DialogueLine, r =>
            {
                r.Ints["speaker"] = (int)Talker.Target;
                r.Strings["text0"] = "Sure, why not.";
                r.References["lines"] = [Ref("after-c")];
            }),
            Rec("after-c", FcsRecordType.DialogueLine, r => r.Strings["text0"] = "Vanilla follow-up."),
        ]), []);

        // 1. Filter claims the choice: conversation ends instead of advancing to "after-c".
        var claimingHook = new DialogueHook(c => c.LineId == "yes-c");
        var dialogue1 = new DialogueSystem(data);
        var ground = new OpenGroundWalkability((_, _) => 150);
        using (var world = new SimWorld(new WorldSettings { Seed = 5, Threads = 1, PublishSnapshots = false }, ground,
            [new MovementSystem(new PathService(ground, true)), dialogue1, claimingHook]))
        {
            world.Player.Faction = data.PlayerFaction;
            var hero = SpawnActor(world, "hero", Vector2.Zero, data.PlayerFaction, true, "");
            var npc = SpawnActor(world, "dealer", new Vector2(10, 0), data.FactionIndex("nf"), false, "npc-c");

            world.Commands.Enqueue(new TalkOrder(hero, npc) { Tick = world.Tick });
            world.RunTicks(5);
            Assert.NotNull(dialogue1.PublishConversation());

            world.Commands.Enqueue(new DialogueChoice(0) { Tick = world.Tick });
            world.RunTicks(2);

            Assert.Single(claimingHook.Observed);
            Assert.Equal(("yes-c", "Sure, why not.", false),
                (claimingHook.Observed[0].LineId, claimingHook.Observed[0].Text, claimingHook.Observed[0].Leave));
            Assert.Null(dialogue1.PublishConversation());
        }

        // 2. Filter passes (returns false): vanilla advances to "after-c".
        var passingHook = new DialogueHook(_ => false);
        var dialogue2 = new DialogueSystem(data);
        using (var world = new SimWorld(new WorldSettings { Seed = 5, Threads = 1, PublishSnapshots = false }, ground,
            [new MovementSystem(new PathService(ground, true)), dialogue2, passingHook]))
        {
            world.Player.Faction = data.PlayerFaction;
            var hero = SpawnActor(world, "hero", Vector2.Zero, data.PlayerFaction, true, "");
            var npc = SpawnActor(world, "dealer", new Vector2(10, 0), data.FactionIndex("nf"), false, "npc-c");

            world.Commands.Enqueue(new TalkOrder(hero, npc) { Tick = world.Tick });
            world.RunTicks(5);
            world.Commands.Enqueue(new DialogueChoice(0) { Tick = world.Tick });
            world.RunTicks(2);

            // the childless follow-up ends the talk and is said as a bubble (FUN_1406839a0)
            Assert.Single(passingHook.Observed);
            Assert.Null(dialogue2.PublishConversation());
            Assert.Equal("Vanilla follow-up.", dialogue2.PublishSpeech().Single(b => b.Speaker == npc).Text);
        }
    }

    /// <summary>
    /// Spawns a dialogue actor with squad and inventory.
    /// </summary>
    static CharacterId SpawnActor(SimWorld world, string name, Vector2 at, int faction, bool player, string record)
    {
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction, HomeCentre = at, HomeRadius = 10 };
        world.Squads.Add(squad);
        var cold = new CharacterCold
        {
            Name = name,
            Faction = faction,
            IsPlayer = player,
            RecordId = record,
            Inventory = new Inventory(),
            SquadId = squad.Id,
        };
        var id = world.Characters.Spawn(new CharacterHot
        {
            Position = new Vector3(at.X, 150, at.Y),
            Health = 100,
            MaxSpeed = 60,
            WalkSpeed = 15,
            Task = (byte)CharacterTask.Idle,
        }, cold, 0);
        squad.Members.Add(id);
        squad.Leader = id;
        return id;
    }

    #endregion

    #region Audio modulation

    /// <summary>
    /// <see cref="AudioModulation.Clamped"/> clamps out-of-range and non-finite values, and <see cref="ModAudioModulations"/> composes multiple
    /// mods across world, music and UI channels and clears cleanly.
    /// </summary>
    [Fact]
    public void Audio_modulation_clamps_composes_and_clears()
    {
        var clamped = new AudioModulation(Rate: 0.01f, LowPass: float.NaN, Gain: 99f).Clamped();
        Assert.Equal((AudioModulation.MinRate, 1f, AudioModulation.MaxGain), (clamped.Rate, clamped.LowPass, clamped.Gain));

        var mods = new ModAudioModulations();
        Assert.Null(mods.Current);

        mods.Set("mod.slowmo", new AudioModulation(Rate: 0.5f, LowPass: 0.2f, Gain: 0.8f));
        var snap1 = mods.Current!;
        Assert.Equal((0.5f, 0.2f, 0.8f), (snap1.World.Rate, snap1.World.LowPass, snap1.World.Gain));
        Assert.Equal(AudioChannelModulation.Identity, snap1.Music);
        Assert.Equal(AudioChannelModulation.Identity, snap1.Ui);

        mods.Set("mod.muffle", new AudioModulation(Rate: 0.8f, LowPass: 0.1f, Gain: 0.5f, AffectMusic: true, AffectUi: true));
        var snap2 = mods.Current!;
        Assert.Equal(0.4f, snap2.World.Rate, 3);
        Assert.Equal(0.1f, snap2.World.LowPass, 3);
        Assert.Equal(0.4f, snap2.World.Gain, 3);
        Assert.Equal((0.8f, 0.1f, 0.5f), (snap2.Music.Rate, snap2.Music.LowPass, snap2.Music.Gain));
        Assert.Equal((0.8f, 0.1f, 0.5f), (snap2.Ui.Rate, snap2.Ui.LowPass, snap2.Ui.Gain));

        mods.Set("mod.slowmo", AudioModulation.Identity);
        mods.Drop("mod.muffle");
        Assert.Null(mods.Current);
    }

    #endregion
}
