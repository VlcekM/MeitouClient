using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>A save's town platoon the game never made (docs/formats/save.md "NPC platoons", Observed on one save): which ones become stand-ins, and which come in at load.</summary>
public class UnmadePlatoonTests
{
    static SavePlatoon Platoon(bool neverActivated = true, int charCount = 8, int homeType = (int)FcsRecordType.BUILDING, string contentFile = "platoon/")
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.PLATOON, StringId = "Shinobi Thieves_0" };
        r.Bools["never been activated"] = neverActivated;
        r.Bools["persistent"] = true;
        r.Bools["is resident"] = false;
        r.Ints["char count"] = charCount;
        r.Filenames["content file"] = contentFile;
        new Hand(homeType, 2133, 11111, 5, 750000000).Write(r, "homebuilding");
        return new SavePlatoon(r, contentFile.Length > "platoon/".Length ? new SaveFile() : null);
    }

    [Fact]
    public void Only_an_unfiled_never_activated_platoon_with_members_and_a_home_building_is_unmade()
    {
        Assert.True(SaveLoader.Unmade(Platoon()));
        Assert.False(SaveLoader.Unmade(Platoon(neverActivated: false)));
        Assert.False(SaveLoader.Unmade(Platoon(charCount: 0)));                       // the save's Holy Nation Outlaws that never appear
        Assert.False(SaveLoader.Unmade(Platoon(homeType: Hand.NullType)));           // roaming platoons: no home building
        Assert.False(SaveLoader.Unmade(Platoon(homeType: (int)FcsRecordType.TOWN))); // a home that is not a building
        Assert.False(SaveLoader.Unmade(Platoon(contentFile: "platoon/Shinobi Thieves_0.platoon")));   // a file: its own characters
    }

    static Meitou.Simulation.Platoon Stand(bool unmade, Vector2 at) =>
        new() { Id = 1, Origin = 0, TemplateId = "51709-rebirth.mod", Key = 1, Unmade = unmade, Position = at };

    [Fact]
    public void An_unmade_platoon_comes_in_at_load_only_in_the_player_box()
    {
        var box = new HashSet<ZoneCoordinate> { new(20, 32), new(21, 32) };
        // Position is X and Z: (20, 32) is the player's zone, (20, 33) a zone of the camera block outside the box.
        var inBox = Stand(true, new Vector2(-51039.9f, 3656.3f));
        var outside = Stand(true, new Vector2(-51727.4f, 4880.7f));
        var ordinary = Stand(false, new Vector2(-51727.4f, 4880.7f));
        Assert.Equal(new ZoneCoordinate(20, 33), WorldLayout.ZoneOf(outside.Position.X, outside.Position.Y));
        Assert.True(PopulationSystem.ActivatesAtLoad(inBox, box));
        Assert.False(PopulationSystem.ActivatesAtLoad(outside, box));
        Assert.True(PopulationSystem.ActivatesAtLoad(ordinary, box));
    }

    // ------------------------------------------------------------------ through a save of the synthetic town

    const string UnmadeName = "Testfolk_77";

    static SimWorld NewWorld(PopulationData data)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk, StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Body,
            new PopulationSettings { CheckEveryTicks = 1 });
        return new SimWorld(new WorldSettings { Seed = 11, Threads = 1, PublishSnapshots = false }, walk, systems.Systems);
    }

    static void Dispose(SimWorld world)
    {
        world.Dispose();
        foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
    }

    static SaveGame RoundTrip(SaveGame save, string root)
    {
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N")[..8], QuickSave.Name);
        save.Write(folder);
        return SaveGame.Load(folder);
    }

    static Dictionary<string, SavePlatoon> NpcPlatoons(SaveGame save) =>
        save.Platoons.Where(p => p.FactionId == "20-t").ToDictionary(p => p.Name, StringComparer.Ordinal);

    [Fact]
    public void An_unmade_platoon_saved_before_it_is_made_keeps_its_record_and_once_made_is_saved_into_that_record()
    {
        var data = SyntheticTown.Data(SyntheticTown.Database(anatomy: true));
        string root = Temp("unmade");
        var a = NewWorld(data);
        var b = NewWorld(data);
        var c = NewWorld(data);
        try
        {
            // The synthetic town saved, then a never-made resident Patrol (10-t) of its own added at the player's place, as the original writes one.
            var squad = a.Systems.OfType<PopulationSystem>().Single().StartPlayer(a, NewGameStart.From(data.Db.Find("40-t")!));
            a.Commands.Enqueue(new FocusCommand(SyntheticTown.Centre) { Tick = 0 });
            a.RunTicks(40);
            Assert.True(a.Characters.TryResolveNext(squad.Leader, out int lead));
            var at = a.Characters.Previous[lead].Position;
            var base0 = SaveCapture.Capture(a, data, new SaveClock(3, 7, 45), null,
                new SaveCaptureOptions { Placements = [new TownPlacement("inst", "30-t", SyntheticTown.Centre, null)] });
            var unmade = new SaveBuilder(base0).AddPlatoon(UnmadeName, "Testfolk", "20-t", "10-t", at);
            unmade.File = null;
            unmade.ContentFile = "platoon/";
            unmade.CharCount = 4;
            unmade.Record.Bools["never been activated"] = true;
            unmade.Record.Bools["is resident"] = true;
            unmade.Record.Strings["basetown"] = "30-t";
            var home = new Hand((int)FcsRecordType.BUILDING, 2133, 11111, 5, 750000000);
            home.Write(unmade.Record, "homebuilding");
            var save0 = RoundTrip(base0, root);
            var handle = save0.Platoons.Single(p => p.Name == UnmadeName).Handle;
            var others0 = NpcPlatoons(save0).Keys.Where(n => n != UnmadeName).ToHashSet();

            void IsStandIn(SimWorld w, LoadedSave loaded)
            {
                var p = w.Platoons.All.Single(x => x.Unmade);
                Assert.Equal((PlatoonState.Unloaded, 4, "10-t", new Vector2(at.X, at.Z)), (p.State, p.Size, p.TemplateId, p.Position));
                Assert.Equal(UnmadeName, p.Saved!.Name);
                Assert.Empty(p.Saved.Members);
                Assert.True(p.Id > int.MaxValue - 4);   // a reserved id
                Assert.Contains($"{UnmadeName}: never made by the game; made from its template when it activates.", loaded.Notes);
            }

            // (1) Loaded and saved before it is made: its record once, as it was, and no squad for it.
            var loadedB = SaveLoader.Load(b, save0, data);
            IsStandIn(b, loadedB);
            var save1 = RoundTrip(SaveCapture.Capture(b, data, loadedB.Clock, loadedB), root);
            var kept = Assert.Single(save1.Platoons, p => p.Name == UnmadeName);
            Assert.Equal((handle, 4, true, home, "platoon/", "10-t", "30-t", false), (kept.Handle, kept.CharCount, kept.Record.Bools["never been activated"],
                Hand.Read(kept.Record, "homebuilding"), kept.ContentFile, kept.SquadTemplate, kept.Record.Strings["basetown"], kept.IsLoaded));
            Assert.Equal(others0, NpcPlatoons(save1).Keys.Where(n => n != UnmadeName).ToHashSet());

            // (2) That save loads as the same stand-in.
            var loadedC = SaveLoader.Load(c, save1, data);
            IsStandIn(c, loadedC);

            // (3) In the player's box it is made at load, last, into the same platoon; the save then writes its squad into its own record.
            var stand = c.Platoons.All.Single(x => x.Unmade);
            var standIn = stand.Saved!;
            for (int i = 0; i < 20 && !c.PreparePopulation(SyntheticTown.Centre); i++) { }
            Assert.Same(stand, c.Platoons.Find(stand.Id));
            Assert.Equal((PlatoonState.Loaded, false, false, stand.Id), (stand.State, stand.Unmade, standIn.Despawned, standIn.PlatoonId));
            int members = c.Squads.Find(stand.SquadId)!.Members.Count;
            Assert.True(members > 0);
            var save2 = RoundTrip(SaveCapture.Capture(c, data, loadedC.Clock, loadedC), root);
            var after = NpcPlatoons(save2);
            Assert.Equal(others0.Append(UnmadeName).ToHashSet(), after.Keys.ToHashSet());   // no fresh platoon for it
            var made = after[UnmadeName];
            Assert.Equal((handle, "10-t", "30-t", home, true, members, members, false), (made.Handle, made.SquadTemplate, made.Record.Strings["basetown"],
                Hand.Read(made.Record, "homebuilding"), made.IsLoaded, made.CharCount, made.Characters.Count, made.Record.Bools["never been activated"]));
        }
        finally
        {
            foreach (var w in new[] { a, b, c }) Dispose(w);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
