using System.Numerics;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Editing;
using Meitou.Simulation.Population;
using static Meitou.Tests.Simulation.SyntheticTown;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

/// <summary>A plastic surgeon's work (docs/characters.md "Plastic surgeons"): <see cref="EditCharacter"/> is refused unless the surgeon is in reach, the race and gender are kept and the money is more than the price.</summary>
public class SurgeryTests
{
    const int Price = 200;

    /// <summary>Gives every look the tests' race, and keeps the proposed record as it is.</summary>
    sealed class Looks(GameRecord race) : IAppearanceSource
    {
        public CharacterAppearance? Create(string recordId, string? factionId, int seed) => new() { Race = race };
        public CharacterAppearance? Create(string recordId, Loadout loadout) => new() { Race = race };
    }

    [Fact]
    public void A_surgeon_out_of_reach_too_little_money_and_a_new_race_or_gender_are_refused_and_a_valid_edit_takes_the_price()
    {
        // A dialogue whose one line opens the character editor and takes the price, on a CHARACTER that offers it.
        var open = Rec("81-t", FcsRecordType.DialogAction, "Editor");
        open.Ints["action name"] = SurgeonDialogues.CharacterEditor;
        var take = Rec("82-t", FcsRecordType.DialogAction, "Take money");
        take.Ints["action name"] = SurgeonDialogues.TakeMoney;
        var line = Rec("83-t", FcsRecordType.DialogueLine, "Change my looks");
        line.References["effects"] = [Ref("81-t"), Ref("82-t", Price)];
        var dialogue = Rec("84-t", FcsRecordType.Dialogue, "Surgery");
        dialogue.References["lines"] = [Ref("83-t")];
        var surgeonRecord = Rec("85-t", FcsRecordType.Character, "Surgeon");
        surgeonRecord.References["race"] = [Ref("1-t", 100)];
        surgeonRecord.References["dialogue"] = [Ref("84-t")];
        var db = Database(extra: [open, take, line, dialogue, surgeonRecord, Rec("86-t", FcsRecordType.Race, "Other")]);
        var race = db.Find("1-t")!;
        var data = PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)], new Looks(race));

        var walk = new OpenGroundWalkability(Ground);
        var systems = Standard(data, walk, StandardParts.Population | StandardParts.Player, new PopulationSettings { CheckEveryTicks = 1 });
        using var world = new SimWorld(new WorldSettings { Seed = 11, PublishSnapshots = false }, walk, systems.Systems);
        var squad = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        var patient = squad.Leader;
        var cold = world.Characters.Cold(patient.Slot)!;
        var before = cold.Appearance = new CharacterAppearance { Race = race };
        string name = cold.Name;
        var at = world.Characters.Previous[patient.Slot].Position;

        CharacterId Surgeon(Vector3 where) =>
            world.Characters.Spawn(new CharacterHot { Position = where, Health = 100 }, new CharacterCold { Name = "Surgeon", RecordId = "85-t", Faction = cold.Faction }, world.Tick);
        var near = Surgeon(at + new Vector3(10, 0, 0));
        var far = Surgeon(at + new Vector3(CharacterEditing.SurgeonReach + 40, 0, 0));
        world.RunTicks(1);

        FcsRecord Look(string raceId, bool female)
        {
            var record = Rec("look", FcsRecordType.Character, "Look");
            record.References["race"] = [Ref(raceId)];
            record.Bools["sex female"] = female;
            return record;
        }

        // Each attempt: the money at the start, the edit, and the one message the refusal posts.
        void Attempt(CharacterId surgeon, string raceId, bool female, int money, bool accepted)
        {
            world.Player.Money = money;
            world.Commands.Enqueue(new EditCharacter(patient, Look(raceId, female), "Renamed", EditKind.Surgery) { Tick = world.Tick, Surgeon = surgeon });
            world.RunTicks(1);
            var messages = world.TakeMessages();
            if (accepted)
            {
                Assert.Empty(messages);
                Assert.Equal(money - Price, world.Player.Money);
                Assert.Equal("Renamed", cold.Name);
                Assert.NotSame(before, cold.Appearance);
                Assert.True(cold.LookEdited);
            }
            else
            {
                Assert.Single(messages);
                Assert.Equal(money, world.Player.Money);
                Assert.Equal(name, cold.Name);
                Assert.Same(before, cold.Appearance);
                Assert.False(cold.LookEdited);
            }
        }

        Attempt(far, "1-t", false, 500, accepted: false);      // out of reach
        Attempt(near, "1-t", false, Price, accepted: false);   // the money must be more than the price
        Attempt(near, "86-t", false, 500, accepted: false);    // another race
        Attempt(near, "1-t", true, 500, accepted: false);      // another gender
        Attempt(near, "1-t", false, 500, accepted: true);
    }

    /// <summary>
    /// An animal on the new-game editor's list (docs/characters.md "Character editor", #312): a name-only edit renames it, a new look sent for it is refused and leaves it as it was.
    /// </summary>
    [Fact]
    public void At_creation_a_name_only_edit_renames_an_animal_and_a_new_look_for_it_is_refused()
    {
        var animalRecord = Rec("87-t", FcsRecordType.AnimalCharacter, "Hound");
        animalRecord.References["race"] = [Ref("1-t", 100)];
        var db = Database(extra: [animalRecord]);
        var race = db.Find("1-t")!;
        var data = PopulationData.Create(db, [new TownPlacement("inst", "30-t", Centre, null)], new Looks(race));
        var walk = new OpenGroundWalkability(Ground);
        var systems = Standard(data, walk, StandardParts.Population | StandardParts.Player, new PopulationSettings { CheckEveryTicks = 1 });
        using var world = new SimWorld(new WorldSettings { Seed = 11, PublishSnapshots = false }, walk, systems.Systems);
        var squad = systems.Population!.StartPlayer(world, NewGameStart.From(db.Find("40-t")!));
        world.Commands.Enqueue(new FocusCommand(new Vector3(-90000, 0, -90000)) { Tick = 0 });
        var dog = squad.Leader;
        var cold = world.Characters.Cold(dog.Slot)!;
        var look = cold.Appearance = CharacterAppearance.BuildAnimal(db, "", db.Find("87-t")!);
        string name = cold.Name;
        Assert.True(world.Player.InCreation);

        void Edit(FcsRecord? appearance, string typed)
        {
            world.Commands.Enqueue(new EditCharacter(dog, appearance, typed, EditKind.Creation) { Tick = world.Tick });
            world.RunTicks(1);
        }

        var proposed = Rec("look", FcsRecordType.Character, "Look");
        proposed.References["race"] = [Ref("1-t")];
        Edit(proposed, "Wrong");
        Assert.Single(world.TakeMessages());
        Assert.Equal(name, cold.Name);
        Assert.Same(look, cold.Appearance);
        Assert.False(cold.LookEdited);

        Edit(null, "Rex");
        Assert.Empty(world.TakeMessages());
        Assert.Equal("Rex", cold.Name);
        Assert.Same(look, cold.Appearance);
    }
}
