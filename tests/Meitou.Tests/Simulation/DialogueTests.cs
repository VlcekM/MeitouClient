using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Dialogue;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Saving;
using static Meitou.Tests.Gameplay.EconomyTests;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>Player conversations (docs/simulation.md "Dialogue", docs/game/dialogue.md) on an invented database.</summary>
public class DialogueTests
{
    static FcsReference Ref(string id, int v0 = 0) => new(id, v0, 0, 0);

    /// <summary>
    /// A villager with two event-1 dialogues: "rich" (needs the player's money &gt;= 500, so it scores 1) greets with the GREETING word swap and offers to sell for c.100, which locks it;
    /// "plain" (score 0) says hello.
    /// </summary>
    static PopulationData Data() => PopulationData.Create(Db([
        Rec("p-d", FcsRecordType.FACTION, r => r.Name = "Nameless"),
        Rec("f-d", FcsRecordType.FACTION, r => r.Name = "Villagers"),
        Rec("npc-d", FcsRecordType.CHARACTER, r => { r.Name = "Villager"; r.References["dialogue"] = [Ref("rich-d"), Ref("plain-d")]; }),
        Rec("money-d", FcsRecordType.DIALOG_ACTION, r => r.Ints["condition name"] = DialogueConditions.PlayerMoney),
        Rec("pay-d", FcsRecordType.DIALOG_ACTION, r => r.Ints["action name"] = DialogueActions.TakeMoney),
        Rec("rich-d", FcsRecordType.DIALOGUE, r => { r.References["conditions"] = [Ref("money-d", 500)]; r.References["lines"] = [Ref("rich-l")]; }),
        Rec("rich-l", FcsRecordType.DIALOGUE_LINE, r => { r.Strings["text0"] = "/GREETING/, rich /YOURNAME/."; r.References["lines"] = [Ref("buy-l"), Ref("bye-l")]; }),
        Rec("buy-l", FcsRecordType.DIALOGUE_LINE, r =>
        {
            r.Ints["speaker"] = (int)Talker.Target;
            r.Strings["text0"] = "Here, c./COST/.";
            r.References["effects"] = [Ref("pay-d", 100)];
            r.References["locks"] = [Ref("rich-d")];
        }),
        Rec("bye-l", FcsRecordType.DIALOGUE_LINE, r => { r.Ints["speaker"] = (int)Talker.Target; r.Strings["text0"] = "Bye."; }),
        Rec("plain-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("plain-l")]),
        Rec("plain-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Hello."),
        Rec("greet-d", FcsRecordType.WORD_SWAPS, r => { r.Name = "GREETING"; r.References["lines"] = [Ref("greet-l")]; }),
        Rec("greet-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "well met"),
    ]), []);

    /// <summary>
    /// The Shinobi Thieves' gate (docs/game/factions-squads-towns.md "Faction rank") on the same ids: at rank 0 the villager says "I don't know you." and offers to join
    /// (c.10,000 and DA_INCREASE_FACTION_RANK capped at 1); at rank 1 (checked with `who` 1, which the game ignores) it offers to buy.
    /// </summary>
    static PopulationData Guild() => PopulationData.Create(Db([
        Rec("p-d", FcsRecordType.FACTION, r => r.Name = "Nameless"),
        Rec("f-d", FcsRecordType.FACTION, r => r.Name = "Thieves"),
        Rec("npc-d", FcsRecordType.CHARACTER, r => { r.Name = "Villager"; r.References["dialogue"] = [Ref("stranger-d"), Ref("member-d")]; }),
        Rec("rank-d", FcsRecordType.DIALOG_ACTION, r => r.Ints["condition name"] = 10),
        Rec("rank-who-d", FcsRecordType.DIALOG_ACTION, r => { r.Ints["condition name"] = 10; r.Ints["who"] = 1; }),
        Rec("pay-d", FcsRecordType.DIALOG_ACTION, r => r.Ints["action name"] = DialogueActions.TakeMoney),
        Rec("raise-d", FcsRecordType.DIALOG_ACTION, r => r.Ints["action name"] = DialogueActions.IncreaseFactionRank),
        Rec("stranger-d", FcsRecordType.DIALOGUE, r => { r.References["conditions"] = [Ref("rank-d", 0)]; r.References["lines"] = [Ref("stranger-l")]; }),
        Rec("stranger-l", FcsRecordType.DIALOGUE_LINE, r => { r.Strings["text0"] = "I don't know you."; r.References["lines"] = [Ref("join-l")]; }),
        Rec("join-l", FcsRecordType.DIALOGUE_LINE, r =>
        {
            r.Ints["speaker"] = (int)Talker.Target;
            r.Strings["text0"] = "I'll join.";
            r.References["effects"] = [Ref("pay-d", 10000), Ref("raise-d", 1)];
        }),
        Rec("member-d", FcsRecordType.DIALOGUE, r => { r.References["conditions"] = [Ref("rank-who-d", 1)]; r.References["lines"] = [Ref("member-l")]; }),
        Rec("member-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Need to sell off some junk?"),
    ]), []);

    sealed class Village : IDisposable
    {
        public PopulationData Data { get; }
        public SimWorld World { get; }
        public DialogueSystem Dialogue { get; }
        public CharacterId Hero { get; }
        public CharacterId Villager { get; }

        public Village(int threads = 1, float apart = 20, PopulationData? data = null)
        {
            Data = data ?? DialogueTests.Data();
            Dialogue = new DialogueSystem(Data);
            var ground = new OpenGroundWalkability((_, _) => 150);
            World = new SimWorld(new WorldSettings { Seed = 5, Threads = threads, PublishSnapshots = false }, ground, [new MovementSystem(new PathService(ground, true)), Dialogue]);
            World.Player.Faction = Data.PlayerFaction;
            Hero = Spawn("hero", Vector2.Zero, Data.PlayerFaction, true, "");
            Villager = Spawn("Villager", new Vector2(apart, 0), Data.FactionIndex("f-d"), false, "npc-d");
        }

        public void Dispose() => World.Dispose();

        CharacterId Spawn(string name, Vector2 at, int faction, bool player, string record)
        {
            var squad = new Squad { Id = World.Squads.NextId(), TemplateId = "t", Faction = faction, HomeCentre = at, HomeRadius = 10 };
            World.Squads.Add(squad);
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            cold.Faction = faction;
            cold.IsPlayer = player;
            cold.RecordId = record;
            cold.Inventory = new Inventory();
            cold.SquadId = squad.Id;
            var id = World.Characters.Spawn(new CharacterHot { Position = new Vector3(at.X, 150, at.Y), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }

        public void Say(SimCommand c) => World.Commands.Enqueue(c with { Tick = World.Tick });
        public ConversationView? Talk => Dialogue.PublishConversation();
    }

    [Fact]
    public void The_best_scoring_passing_dialogue_is_said_with_its_word_swaps_and_its_effects_lock_it()
    {
        var data = Data();
        Assert.Equal(1, data.Dialogue.Node("rich-d")!.ListScore);   // one condition
        Assert.Equal(0, data.Dialogue.Node("plain-d")!.ListScore);

        using var v = new Village();
        v.World.Player.Money = 100;   // too poor for "rich": the condition fails, "plain" is said
        v.Say(new TalkOrder(v.Hero, v.Villager) { Tick = 0 });
        v.World.RunTicks(2);
        Assert.Equal(("Hello.", "[Leave]"), (v.Talk!.Text, v.Talk.Choices.Single()));
        v.Say(new DialogueChoice(0) { Tick = 0 });
        v.World.RunTicks(1);
        Assert.Null(v.Talk);

        v.World.Player.Money = 1000;
        v.Say(new TalkOrder(v.Hero, v.Villager) { Tick = 0 });
        v.World.RunTicks(2);
        Assert.Equal("Well met, rich hero.", v.Talk!.Text);
        Assert.Equal(["Here, c.100. [c.100]", "Bye."], v.Talk.Choices);   // one " [c.N]" per take-money effect (FUN_140675320)
        v.Say(new DialogueChoice(0) { Tick = 0 });
        v.World.RunTicks(1);
        Assert.Null(v.Talk);   // nothing follows the reply
        Assert.Equal(900, v.World.Player.Money);
        var cold = v.World.Characters.Cold(v.Villager.Slot)!;
        Assert.True(cold.DialogueLocks!["rich-d"]);

        v.Say(new TalkOrder(v.Hero, v.Villager) { Tick = 0 });   // locked: back to "plain"
        v.World.RunTicks(2);
        Assert.Equal("Hello.", v.Talk!.Text);

        // The lock is saved as the original's lok0 pair.
        var state = new FcsRecord { StringId = "s", RecordType = FcsRecordType.GAMESTATE_CHARACTER };
        DialogueSave.Write(state, cold);
        Assert.Equal(("rich-d", true), (state.Strings["lok0"], state.Bools["lok0"]));
        var loaded = new Meitou.Tests.Combat.Combatant { Name = "x" }.MakeCold();
        DialogueSave.Read(state, loaded);
        Assert.True(loaded.DialogueLocks!["rich-d"]);
    }

    [Fact]
    public void Joining_the_guild_raises_its_rank_to_the_cap_and_switches_its_dialogue()
    {
        using var v = new Village(data: Guild());
        int guild = v.Data.FactionIndex("f-d");
        v.World.Player.Money = 12000;
        v.Say(new TalkOrder(v.Hero, v.Villager) { Tick = 0 });
        v.World.RunTicks(2);
        Assert.Equal(("I don't know you.", "I'll join. [c.10,000]"), (v.Talk!.Text, v.Talk.Choices.Single()));
        v.Say(new DialogueChoice(0) { Tick = 0 });
        v.World.RunTicks(1);
        Assert.Equal((1, 2000), (v.Data.Relations.Rank(guild), v.World.Player.Money));

        v.Say(new TalkOrder(v.Hero, v.Villager) { Tick = 0 });
        v.World.RunTicks(2);
        Assert.Equal("Need to sell off some junk?", v.Talk!.Text);
        Assert.False(v.Data.Relations.RaiseRank(guild, 1));   // the join line's cap
        Assert.Equal(1, v.Data.Relations.Rank(guild));
    }

    /// <summary>
    /// A guard greets a stranger of another squad (event 3; its line refuses player targets) with a crowd trigger: his mate cheers (event 74), and the chain passes to the line the
    /// stranger says back (speaker T_TARGET).
    /// </summary>
    [Fact]
    public void A_greeting_between_npcs_sets_off_the_crowd_and_the_target_answers()
    {
        var data = PopulationData.Create(Db([
            Rec("p-d", FcsRecordType.FACTION, r => r.Name = "Nameless"),
            Rec("f-d", FcsRecordType.FACTION, r => r.Name = "Villagers"),
            Rec("guard-c", FcsRecordType.CHARACTER, r => { r.Name = "Guard"; r.References["dialogue package"] = [Ref("guard-p")]; }),
            Rec("guard-p", FcsRecordType.DIALOGUE_PACKAGE, r => r.References["dialogs"] = [Ref("hail-d", 3)]),
            Rec("player-dc", FcsRecordType.DIALOG_ACTION, r => { r.Ints["condition name"] = 36; r.Ints["who"] = (int)Talker.Target; }),
            Rec("hail-d", FcsRecordType.DIALOGUE, r => { r.Bools["monologue"] = true; r.References["lines"] = [Ref("hail-l")]; }),
            Rec("hail-l", FcsRecordType.DIALOGUE_LINE, r =>
            {
                r.Strings["text0"] = "Hail, /YOURNAME/.";
                r.References["conditions"] = [Ref("player-dc", 0)];
                r.References["crowd trigger"] = [Ref("cheer-d", 100)];
                r.References["lines"] = [Ref("answer-l")];
            }),
            Rec("answer-l", FcsRecordType.DIALOGUE_LINE, r => { r.Ints["speaker"] = (int)Talker.Target; r.Strings["text0"] = "Hail yourself."; }),
            Rec("cheer-d", FcsRecordType.DIALOGUE, r => { r.Bools["monologue"] = true; r.References["lines"] = [Ref("cheer-l")]; }),
            Rec("cheer-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "Hurrah!"),
        ]), []);
        var dialogue = new DialogueSystem(data, new DialogueSettings { AmbientEvents = true });
        var ground = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false }, ground, [dialogue]);
        world.Player.Faction = data.PlayerFaction;
        int villagers = data.FactionIndex("f-d");
        Squad? last = null;
        CharacterId Spawn(string name, float x, int faction, bool player, string record, bool join = false)
        {
            if (!join)
            {
                last = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction };
                world.Squads.Add(last);
            }
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            (cold.Faction, cold.IsPlayer, cold.RecordId, cold.Inventory, cold.SquadId) = (faction, player, record, new Inventory(), last!.Id);
            var id = world.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 150, 0), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            last.Members.Add(id);
            if (!join) last.Leader = id;
            return id;
        }
        Spawn("hero", 0, data.PlayerFaction, true, "");
        Spawn("Guard", 100, villagers, false, "guard-c");
        Spawn("Mate", 110, villagers, false, "guard-c", join: true);
        Spawn("Stranger", 150, villagers, false, "");

        var said = new HashSet<string>();
        for (int i = 0; i < 400; i++)
        {
            world.RunTicks(1);
            foreach (var s in dialogue.PublishSpeech()) said.Add($"{world.Characters.Cold(s.Speaker.Slot)!.Name}: {s.Text}");
        }
        // Which of the two greets depends on the scan order; the other cheers.
        string[] all = [.. said.Order()];
        Assert.True(all.SequenceEqual(["Guard: Hail, Stranger.", "Mate: Hurrah!", "Stranger: Hail yourself."]) || all.SequenceEqual(["Guard: Hurrah!", "Mate: Hail, Stranger.", "Stranger: Hail yourself."]),
            string.Join(" | ", all));
    }

    /// <summary>A guard's line with DA_TALK_TO_LEADER sends his squad's leader, 200 away, to the hero; within reach the leader opens the conversation window (event 1).</summary>
    [Fact]
    public void Talk_to_leader_sends_the_leader_to_open_a_conversation()
    {
        var data = PopulationData.Create(Db([
            Rec("p-d", FcsRecordType.FACTION, r => r.Name = "Nameless"),
            Rec("f-d", FcsRecordType.FACTION, r => r.Name = "Villagers"),
            Rec("guard-c", FcsRecordType.CHARACTER, r => { r.Name = "Guard"; r.References["dialogue"] = [Ref("ask-d")]; }),
            Rec("boss-c", FcsRecordType.CHARACTER, r => { r.Name = "Boss"; r.References["dialogue"] = [Ref("boss-d")]; }),
            Rec("leader-da", FcsRecordType.DIALOG_ACTION, r => r.Ints["action name"] = DialogueActions.TalkToLeader),
            Rec("ask-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("ask-l")]),
            Rec("ask-l", FcsRecordType.DIALOGUE_LINE, r => { r.Strings["text0"] = "Talk to the boss."; r.References["effects"] = [Ref("leader-da")]; }),
            Rec("boss-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("boss-l")]),
            Rec("boss-l", FcsRecordType.DIALOGUE_LINE, r => r.Strings["text0"] = "What do you want?"),
        ]), []);
        var dialogue = new DialogueSystem(data);
        var ground = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false }, ground, [new MovementSystem(new PathService(ground, true)), dialogue]);
        world.Player.Faction = data.PlayerFaction;
        int villagers = data.FactionIndex("f-d");
        Squad? last = null;
        CharacterId Spawn(string name, float x, int faction, bool player, string record, bool join = false)
        {
            if (!join)
            {
                last = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction };
                world.Squads.Add(last);
            }
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            (cold.Faction, cold.IsPlayer, cold.RecordId, cold.Inventory, cold.SquadId) = (faction, player, record, new Inventory(), last!.Id);
            var id = world.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 150, 0), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            last.Members.Add(id);
            if (!join) last.Leader = id;
            return id;
        }
        var hero = Spawn("hero", 0, data.PlayerFaction, true, "");
        var boss = Spawn("Boss", 220, villagers, false, "boss-c");
        var guard = Spawn("Guard", 20, villagers, false, "guard-c", join: true);

        world.Commands.Enqueue(new TalkOrder(hero, guard) { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Equal(("Talk to the boss.", "[Leave]"), (dialogue.PublishConversation()!.Text, dialogue.PublishConversation()!.Choices.Single()));
        world.Commands.Enqueue(new DialogueChoice(0) { Tick = world.Tick });
        world.RunTicks(1);
        Assert.Null(dialogue.PublishConversation());

        for (int i = 0; i < 600 && dialogue.PublishConversation() is null; i++) world.RunTicks(1);
        var talk = dialogue.PublishConversation();
        Assert.NotNull(talk);
        Assert.Equal((boss, "What do you want?"), (talk.Npc, talk.Text));
    }

    /// <summary>
    /// A slaver of the jail town's faction whose reply carries DA 42 (CHOOSE_SLAVES_BUYING) lists its caged slave at the formula price, trunc(20 + 15 + 10.4) x 100 (docs/game/economy.md
    /// 12); buying it takes the money and frees it from cage and slavery, without joining the hero's squad.
    /// </summary>
    [Fact]
    public void Buying_a_caged_slave_lists_it_at_its_value_and_paying_frees_it()
    {
        var data = ArrestTests.Town([
            Rec("slaver-c", FcsRecordType.CHARACTER, r => { r.Name = "Slaver"; r.References["dialogue"] = [Ref("trade-d")]; }),
            Rec("buy-da", FcsRecordType.DIALOG_ACTION, r => r.Ints["action name"] = 42),
            Rec("trade-d", FcsRecordType.DIALOGUE, r => r.References["lines"] = [Ref("trade-l")]),
            Rec("trade-l", FcsRecordType.DIALOGUE_LINE, r => { r.Strings["text0"] = "Looking to trade?"; r.References["lines"] = [Ref("buy-l")]; }),
            Rec("buy-l", FcsRecordType.DIALOGUE_LINE, r => { r.Ints["speaker"] = (int)Talker.Target; r.Strings["text0"] = "I'm looking to buy some slaves"; r.References["effects"] = [Ref("buy-da")]; }),
        ]);
        var dialogue = new DialogueSystem(data);
        var ground = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 5, PublishSnapshots = false }, ground, [new MovementSystem(new PathService(ground, true)), dialogue]);
        world.Fixtures.RegisterSite(world, data, 0);
        world.Player.Faction = data.PlayerFaction;
        int city = data.FactionIndex("50-a");
        var centre = new Vector3(2000, 150, 2000);
        CharacterId Spawn(string name, Vector3 at, Squad squad, Action<CharacterCold> tweak)
        {
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            (cold.Inventory, cold.SquadId) = (new Inventory(), squad.Id);
            tweak(cold);
            var id = world.Characters.Spawn(new CharacterHot { Position = at, Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            if (squad.Leader.IsNone) squad.Leader = id;
            return id;
        }
        Squad NewSquad(int faction, int town = -1)
        {
            var s = new Squad { Id = world.Squads.NextId(), TemplateId = "t", Faction = faction, Town = town, HomeCentre = new Vector2(centre.X, centre.Z), HomeRadius = 500 };
            world.Squads.Add(s);
            return s;
        }
        var players = NewSquad(data.PlayerFaction);
        var slavers = NewSquad(city, town: 0);
        var hero = Spawn("hero", centre + new Vector3(100, 0, 20), players, c => (c.Faction, c.IsPlayer) = (data.PlayerFaction, true));
        var slaver = Spawn("Slaver", centre + new Vector3(100, 0, 0), slavers, c => (c.Faction, c.RecordId) = (city, "slaver-c"));
        var slave = Spawn("Slave", centre, NewSquad(city), c =>
        {
            c.Faction = city;
            (c.Stats![StatsEnumerated.Strength], c.Stats[StatsEnumerated.MeleeAttack], c.Stats[StatsEnumerated.Dexterity]) = (20, 15, 10.4f);
        });
        var sc = world.Characters.Cold(slave.Slot)!;
        Meitou.Simulation.Locks.FixtureRegistry.Enslave(world, sc, city);
        var cage = world.Fixtures.Find("0.0:cageA")!;
        Assert.True(world.Fixtures.Imprison(world, slave, cage));
        world.Player.Money = 10000;

        world.Commands.Enqueue(new TalkOrder(hero, slaver) { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Equal("Looking to trade?", dialogue.PublishConversation()!.Text);
        world.Commands.Enqueue(new DialogueChoice(0) { Tick = world.Tick });
        world.RunTicks(2);
        var window = dialogue.PublishCharacterTrade()!;
        Assert.Equal("Buying Slaves", window.Title);
        Assert.Equal([(slave, 4500L)], window.Rows.Select(r => (r.Character, r.Price)));

        world.Commands.Enqueue(new ConfirmCharacterTrade([slave]) { Tick = world.Tick });
        world.RunTicks(2);
        Assert.Null(dialogue.PublishCharacterTrade());
        Assert.Equal(5500, world.Player.Money);
        Assert.Equal((0, false, false), (sc.SlaveState, sc.IsImprisoned, sc.IsPlayer));
        Assert.False(cage.Lock!.Locked);
        Assert.DoesNotContain(slave, players.Members);
    }

    /// <summary>The hero walks 300 up to the villager and talks twice (rich, buying; then plain), pinned in <see cref="GoldenHashTests"/>.</summary>
    internal static List<ulong> Scripted(int threads)
    {
        using var v = new Village(threads, apart: 300);
        v.World.Player.Money = 1000;
        var hashes = new List<ulong>();
        string said = "";
        foreach (int target in new[] { 1, 60, 300, 310, 320, 400 })
        {
            v.World.RunTicks(target - (int)v.World.Tick);
            if (target == 1) v.Say(new TalkOrder(v.Hero, v.Villager) { Tick = 0 });
            if (target is 300 or 320) said += v.Talk?.Text + "|";
            if (target == 300) v.Say(new DialogueChoice(0) { Tick = 0 });
            if (target == 310) v.Say(new TalkOrder(v.Hero, v.Villager) { Tick = 0 });
            hashes.Add(v.World.StateHash());
        }
        Assert.Equal("Well met, rich hero.|Hello.|", said);   // it covers what it is for: an approach, a choice with effects, the lock
        return hashes;
    }
}

/// <summary>Talking to the Hub's barman, and the residents' speech bubbles, over the installed game's data. Skipped without the game.</summary>
[Slow]
public class DialogueInstallTests
{
    [Fact]
    public void The_hub_barman_talks_and_offers_two_replies_and_a_resident_says_a_speech_bubble()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var install = InstallData.Install!;
        var db = InstallData.BaseGame!;
        var levels = WorldLevelData.LoadBaseGame(install, includeInteriors: true);
        using var map = TerrainHeightmap.Open(install);
        var buildings = TownBuildings.Build(db, levels, new BuildingLayouts(db, levels.Interiors), (x, z) => map.HeightAt((float)x, (float)z));
        var data = PopulationData.Create(db, levels.Towns(), buildings: buildings);
        var hub = data.Sites.Single(s => s.Town.Name == "The Hub");
        var dialogue = new DialogueSystem(data, new DialogueSettings { AmbientEvents = true });
        var walk = new OpenGroundWalkability((_, _) => 300);
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, walk,
            [new PopulationSystem(data, new PopulationSettings { CheckEveryTicks = 1 }), new MovementSystem(new PathService(walk, true)), dialogue]);
        world.Player.Faction = data.PlayerFaction;
        world.Commands.Enqueue(new FocusCommand(hub.Position) { Tick = 0 });
        world.RunTicks(5);

        var table = world.Characters;
        int barman = Enumerable.Range(0, table.HighWater).First(i => table.Next[i].Alive && table.Cold(i) is { Name: "Barman" });
        var at = table.Next[barman].Position;
        var squad = new Squad { Id = world.Squads.NextId(), TemplateId = "p", Faction = data.PlayerFaction };
        world.Squads.Add(squad);
        var cold = new Meitou.Tests.Combat.Combatant { Name = "hero" }.MakeCold();
        cold.Faction = data.PlayerFaction;
        cold.IsPlayer = true;
        cold.Inventory = new Inventory();
        cold.SquadId = squad.Id;
        var hero = table.Spawn(new CharacterHot { Position = at + new Vector3(20, 0, 0), Health = 100, MaxSpeed = 60, WalkSpeed = 15 }, cold, 0);
        squad.Members.Add(hero);
        squad.Leader = hero;

        Assert.True(world.CanTalk(table.IdOf(barman)));
        world.Commands.Enqueue(new TalkOrder(hero, table.IdOf(barman)) { Tick = world.Tick });
        world.RunTicks(2);
        var talk = dialogue.PublishConversation();
        Assert.NotNull(talk);
        Assert.Equal("Barman", talk.SpeakerName);
        Assert.NotEmpty(talk.Text);
        Assert.Equal(2, talk.Choices.Count);   // trade or decline; their texts are picked at random

        // A resident next to the player character says a speech bubble within a few game hours.
        // The Hub is allied with the player: its residents have event 67 lines (most event 3 lines are disabled or conditional in the data); each is said at a chance of 30 % once per squad per half game hour, so the loop spans a few of those.
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Cold(i) is { IsPlayer: false } resident) data.Relations.Set(resident.Faction, data.PlayerFaction, 100);

        bool spoke = false;
        for (int i = 0; i < 8000 && !spoke; i++)
        {
            world.RunTicks(1);
            spoke = dialogue.PublishSpeech().Any(s => s.Text.Length > 0);
        }
        Assert.True(spoke);
    }

    /// <summary>DC 69 HAS_ILLEGAL_ITEM: a Holy Nation gate guard's inspection finds the player's Hashish (the faction culture's <c>illegal goods</c>) and posts the find once.</summary>
    [Fact]
    public void A_holy_nation_gate_guard_finds_hashish_and_posts_the_smuggling_once()
    {
        Assert.SkipWhen(InstallData.Install is null, "Kenshi install not found");
        var db = InstallData.BaseGame!;
        var data = PopulationData.Create(db, []);
        var dialogue = new DialogueSystem(data);
        var ground = new OpenGroundWalkability((_, _) => 150);
        using var world = new SimWorld(new WorldSettings { Seed = 1, PublishSnapshots = false }, ground, [new MovementSystem(new PathService(ground, true)), dialogue]);
        world.Player.Faction = data.PlayerFaction;
        // The check (DIALOGUE 52700) starts locked, and its male greeting (DIALOGUE_LINE 52728) asks for one personality tag of the guard.
        int tag = db.Find("52728-Dialogue.mod")!.GetReferences("conditions").Select(c => db.Find(c.TargetStringId)!).First(a => a.GetInt("condition name") == 26).GetInt("tag");

        CharacterId Spawn(string name, float x, int faction, bool player, string template)
        {
            var squad = new Squad { Id = world.Squads.NextId(), TemplateId = template, Faction = faction, HomeCentre = new(x, 0), HomeRadius = 10 };
            world.Squads.Add(squad);
            var cold = new Meitou.Tests.Combat.Combatant { Name = name }.MakeCold();
            cold.Faction = faction;
            cold.IsPlayer = player;
            cold.Inventory = new Inventory();
            cold.SquadId = squad.Id;
            if (!player) (cold.Personality, cold.DialogueLocks) = (tag, new(StringComparer.Ordinal) { ["52700-Dialogue.mod"] = false });   // part 1 (53178) unlocks the check as it stops the player
            var id = world.Characters.Spawn(new CharacterHot { Position = new Vector3(x, 150, 0), Health = 100, MaxSpeed = 60, WalkSpeed = 15, Task = (byte)CharacterTask.Idle }, cold, 0);
            squad.Members.Add(id);
            squad.Leader = id;
            return id;
        }
        var hero = Spawn("hero", 0, data.PlayerFaction, true, "");
        var guard = Spawn("Guard", 20, data.FactionIndex("1083-gamedata.base"), false, "18881-rebirth.mod");   // Holy Gate Guards
        Assert.True(world.Characters.TryResolveNext(hero, out int hs));
        Assert.True(world.Characters.Cold(hs)!.Inventory!.Add(new ItemInstance { Record = "1230-gamedata.base" }));   // Hashish

        world.Commands.Enqueue(new TalkOrder(hero, guard) { Tick = world.Tick });
        // Show the gear, then take the first reply each time until the guard orders the seizure ("/SEIZE/" says "Attack"); every line on the way re-checks DC 69.
        var posted = new List<string>();
        string said = "";
        for (int step = 0; step < 12 && !said.StartsWith("Attack", StringComparison.Ordinal); step++)
        {
            world.RunTicks(2);
            posted.AddRange(world.TakeMessages());
            var talk = dialogue.PublishConversation();
            Assert.NotNull(talk);
            said = talk.Text;
            int gear = talk.Choices.ToList().FindIndex(t => t.Contains("Show him your gear", StringComparison.Ordinal));
            world.Commands.Enqueue(new DialogueChoice(Math.Max(0, gear)) { Tick = world.Tick });
        }
        Assert.StartsWith("Attack", said);
        world.RunTicks(2);
        posted.AddRange(world.TakeMessages());
        Assert.Equal(["hero has been discovered smuggling Hashish"], posted.Where(m => m.Contains("smuggling", StringComparison.Ordinal)));
    }
}
