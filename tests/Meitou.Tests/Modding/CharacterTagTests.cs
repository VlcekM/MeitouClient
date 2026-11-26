using System.Numerics;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Modding;

/// <summary>
/// The code mods' tags on characters (docs/modding.md "Character tags"): one per mod, kept by lasting id, found again by tag, saved with the ids.
/// </summary>
public class CharacterTagTests
{
    #region Worlds

    /// <summary>
    /// A world with no systems and one character.
    /// </summary>
    static (SimWorld World, CharacterId Character) Bare()
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 1, PublishSnapshots = false }, walk, []);
        var who = Spawn(world, "Rival");
        return (world, who);
    }

    /// <summary>
    /// A character named <paramref name="name"/> added to <paramref name="world"/>.
    /// </summary>
    static CharacterId Spawn(SimWorld world, string name) =>
        world.Characters.Spawn(new CharacterHot { Position = new Vector3(1000, 150, 1000) }, new CharacterCold { Name = name }, world.Tick);

    /// <summary>
    /// A world of the roaming synthetic towns with population and movement, the camera on the first town.
    /// </summary>
    static (SimWorld World, PopulationSystem Population) Town()
    {
        var db = SyntheticTown.Database(roaming: true);
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var population = new PopulationSystem(SyntheticTown.Data(db), new PopulationSettings { CheckEveryTicks = 15, UnloadGraceSeconds = 600 });
        var world = new SimWorld(new WorldSettings { Seed = 3, Threads = 1, PublishSnapshots = false }, walk,
            [population, new MovementSystem(new PathService(walk, true))]);
        world.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
        return (world, population);
    }

    #endregion

    #region Tags

    /// <summary>
    /// Two mods' tags on one character stay apart: each reads its own, removing one keeps the other, and each finds the character by its own tag
    /// only. Catches one mod's tag replacing another's, a remove that clears every mod's tag, or a find that matches another mod's tag.
    /// </summary>
    [Fact]
    public void Two_mods_tags_on_one_character_stay_apart()
    {
        var (w, who) = Bare();
        using var world = w;

        Assert.True(w.CharacterTags.Set(who, "mod.b", "war leader"));
        Assert.True(w.CharacterTags.Set(who, "mod.a", "rival|7"));

        Assert.Equal(("rival|7", "war leader"), (w.CharacterTags.Get(who, "mod.a"), w.CharacterTags.Get(who, "mod.b")));
        Assert.Equal(["mod.a", "mod.b"], w.CharacterTags.Of(who).Keys);
        Assert.Equal(who, w.CharacterTags.Find("mod.a", "rival|7"));
        Assert.Equal(CharacterId.None, w.CharacterTags.Find("mod.b", "rival|7"));
        Assert.False(w.Characters.Cold(who.Slot)!.LastingId.IsNone);

        Assert.True(w.CharacterTags.Set(who, "mod.a", null));

        Assert.Null(w.CharacterTags.Get(who, "mod.a"));
        Assert.Equal("war leader", w.CharacterTags.Get(who, "mod.b"));
        Assert.Equal(CharacterId.None, w.CharacterTags.Find("mod.a", "rival|7"));
    }

    /// <summary>
    /// A new character in the slot a tagged character left starts with no tags, and the tag no longer finds anyone in the world, while its lasting
    /// id still lists it. Catches a reused handle carrying the old character's tag (a respawned crew member taken for a rival).
    /// </summary>
    [Fact]
    public void A_new_character_in_a_reused_slot_has_no_tags()
    {
        var (w, rival) = Bare();
        using var world = w;
        w.CharacterTags.Set(rival, "mod.a", "rival|7");
        var id = w.Characters.Cold(rival.Slot)!.LastingId;

        Assert.True(w.Characters.Remove(rival));
        var stranger = Spawn(w, "Rival");

        Assert.Equal(rival.Slot, stranger.Slot);
        Assert.Empty(w.CharacterTags.Of(stranger));
        Assert.Null(w.CharacterTags.Get(stranger, "mod.a"));
        Assert.Equal(CharacterId.None, w.CharacterTags.Find("mod.a", "rival|7"));
        Assert.Equal([id], w.CharacterTags.FindIds("mod.a", "rival|7"));
        Assert.Equal("rival|7", w.CharacterTags.Get(id, "mod.a"));
    }

    /// <summary>
    /// Bad input is refused: an empty mod id throws, and a character not in the world is not tagged. Catches tags kept under no mod, or tags given
    /// to a character that is gone.
    /// </summary>
    [Fact]
    public void An_empty_mod_id_throws_and_a_missing_character_is_not_tagged()
    {
        var (w, who) = Bare();
        using var world = w;
        w.Characters.Remove(who);

        Assert.Throws<ArgumentException>(() => w.CharacterTags.Set(who, "", "x"));
        Assert.False(w.CharacterTags.Set(who, "mod.a", "x"));
        Assert.Empty(w.CharacterTags.FindIds("mod.a", "x"));
    }

    #endregion

    #region Saves

    /// <summary>
    /// The ids file writes an entry's tags and reads them back; an entry without tags writes no <c>tags</c>, and a damaged tag is dropped without
    /// losing the entry's id. Catches tags lost on a save, a file that changes for saves without tags, or one bad tag dropping a character's id.
    /// </summary>
    [Fact]
    public void The_ids_file_keeps_each_entrys_tags_and_a_bad_tag_drops_alone()
    {
        var tags = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["mod.b"] = "leader", ["mod.a"] = "rival|7" };
        var hand = new Hand(0, 1, 2, 3, 4);
        var plain = new Hand(0, 1, 2, 3, 5);
        byte[] file = SaveLastingIds.Write([new(hand, 0x1_0000_0001UL, "rec", tags), new(plain, 0x1_0000_0002UL, "rec")], 1, 3);

        var back = SaveLastingIds.Read(file)!.Value;

        Assert.Equal(tags, back.Entries[0].Tags);
        Assert.Null(back.Entries[1].Tags);
        Assert.Equal(1, System.Text.Encoding.UTF8.GetString(file).Split("\"tags\"").Length - 1);

        string damaged = """
            { "format": 1, "prefix": 1, "next": 2, "characters": [
              { "hand": [0, 1, 2, 3, 4], "id": 4294967297, "record": "rec", "tags": { "mod.a": 5, "mod.b": "leader", "": "x" } } ] }
            """;
        var read = SaveLastingIds.Read(System.Text.Encoding.UTF8.GetBytes(damaged))!.Value;

        Assert.Equal(4294967297UL, read.Entries[0].Id);
        Assert.Equal(new Dictionary<string, string> { ["mod.b"] = "leader" }, read.Entries[0].Tags);
    }

    /// <summary>
    /// A tag set on a town's character is saved with its lasting id and, loaded into a new world, finds the same character again by tag, with
    /// both mods' tags, once its platoon is made again. Catches a tag lost on a save and load, or found on a different character after the load.
    /// </summary>
    [Fact]
    public void A_tag_survives_a_save_and_load_and_finds_its_character()
    {
        var (w, population) = Town();
        using var world = w;
        w.RunTicks(50);
        var who = w.Squads.All.First(s => s.Members.Count > 0).Members[0];
        string name = w.Characters.Cold(who.Slot)!.Name;
        w.CharacterTags.Set(who, "mod.a", "rival|7");
        w.CharacterTags.Set(who, "mod.b", "named");

        var game = SaveCapture.Capture(w, population.Data, new SaveClock(1, 13, 0));
        var (w2, population2) = Town();
        using var back = w2;
        SaveLoader.Load(w2, game, population2.Data, new SaveLoadOptions { ApplyRelations = false });
        w2.RunTicks(900);   // the save's NPC platoon is made again near the camera, with its characters' ids
        var again = w2.CharacterTags.Find("mod.a", "rival|7");

        Assert.NotEqual(CharacterId.None, again);
        Assert.Equal(name, w2.Characters.Cold(again.Slot)!.Name);
        Assert.Equal("named", w2.CharacterTags.Get(again, "mod.b"));
        Assert.Equal(w.Characters.Cold(who.Slot)!.LastingId, w2.Characters.Cold(again.Slot)!.LastingId);
    }

    #endregion
}
