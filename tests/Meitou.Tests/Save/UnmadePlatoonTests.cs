using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;
using Meitou.Tests.Simulation;
using static Meitou.Tests.Save.SaveSamples;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Save;

/// <summary>
/// A save's town platoon the game never made (docs/formats/save.md "NPC platoons", Observed on one save): which ones become stand-ins, and which come
/// in at load.
/// </summary>
public class UnmadePlatoonTests
{
    /// <summary>
    /// The name of the platoon the game never made.
    /// </summary>
    const string UnmadeName = "Testfolk_77";

    /// <summary>
    /// A saved town platoon record of the given flags, count, home type and content file.
    /// </summary>
    static SavePlatoon Platoon(bool neverActivated = true, int charCount = 8, int homeType = (int)FcsRecordType.Building,
            string contentFile = "platoon/")
    {
        var r = new FcsRecord { Type = (int)FcsRecordType.Platoon, StringId = "Shinobi Thieves_0" };
        r.Bools["never been activated"] = neverActivated;
        r.Bools["persistent"] = true;
        r.Bools["is resident"] = false;
        r.Ints["char count"] = charCount;
        r.Filenames["content file"] = contentFile;
        new Hand(homeType, 2133, 11111, 5, 750000000).Write(r, "homebuilding");
        return new SavePlatoon(r, contentFile.Length > "platoon/".Length ? new SaveFile() : null);
    }

    // ------------------------------------------------------------------ through a save of the synthetic town

    /// <summary>
    /// A synthetic town's world with population, player, movement and bodies.
    /// </summary>
    static SimWorld NewWorld(PopulationData data)
    {
        var walk = new OpenGroundWalkability(SyntheticTown.Ground);
        var systems = SyntheticTown.Standard(data, walk,
                StandardParts.Population | StandardParts.Player | StandardParts.Movement | StandardParts.Body,
            new PopulationSettings { CheckEveryTicks = 1 });
        return new SimWorld(new WorldSettings { Seed = 11, Threads = 1, PublishSnapshots = false }, walk, systems.Systems);
    }

    /// <summary>
    /// Disposes the world and then each disposable system.
    /// </summary>
    static void Dispose(SimWorld world)
    {
        world.Dispose();
        foreach (var s in world.Systems) (s as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Writes the save into a fresh folder under the root and reads it back.
    /// </summary>
    static SaveGame RoundTrip(SaveGame save, string root)
    {
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N")[..8], QuickSave.Name);
        save.Write(folder);
        return SaveGame.Load(folder);
    }

    /// <summary>
    /// The save's platoons of the town faction (20-t) by name.
    /// </summary>
    static Dictionary<string, SavePlatoon> NpcPlatoons(SaveGame save) =>
        save.Platoons.Where(p => p.FactionId == "20-t").ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>
    /// A never-made Patrol saved before it is made keeps its record as it was; made at load, its squad is saved into that record.
    /// </summary>
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
            var (save0, at, home) = SaveWithAnUnmadePatrol(a, data, root);
            var handle = save0.Platoons.Single(p => p.Name == UnmadeName).Handle;
            var others0 = NpcPlatoons(save0).Keys.Where(n => n != UnmadeName).ToHashSet();

            // (1) Loaded and saved before it is made: its record once, as it was, and no squad for it.
            var loadedB = SaveLoader.Load(b, save0, data);
            IsStandIn(b, loadedB, at);
            var save1 = RoundTrip(SaveCapture.Capture(b, data, loadedB.Clock, loadedB), root);
            var kept = Assert.Single(save1.Platoons, p => p.Name == UnmadeName);

            Assert.Equal((handle, 4, true, home, "platoon/", "10-t", "30-t", false),
                    (kept.Handle, kept.CharCount, kept.Record.Bools["never been activated"],

                Hand.Read(kept.Record, "homebuilding"), kept.ContentFile, kept.SquadTemplate, kept.Record.Strings["basetown"], kept.IsLoaded));

            Assert.Equal(others0, NpcPlatoons(save1).Keys.Where(n => n != UnmadeName).ToHashSet());

            // (2) That save loads as the same stand-in.
            var loadedC = SaveLoader.Load(c, save1, data);
            IsStandIn(c, loadedC, at);

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

            Assert.Equal((handle, "10-t", "30-t", home, true, members, members, false),
                    (made.Handle, made.SquadTemplate, made.Record.Strings["basetown"],

                Hand.Read(made.Record, "homebuilding"), made.IsLoaded, made.CharCount, made.Characters.Count,
                        made.Record.Bools["never been activated"]));
        }
        finally
        {
            foreach (var w in new[] { a, b, c }) Dispose(w);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The synthetic town saved, then a never-made resident Patrol (10-t) of its own added at the player's place, as the original writes one; returns
    /// the save read back, the place and the platoon's home.
    /// </summary>
    static (SaveGame Save, Vector3 At, Hand Home) SaveWithAnUnmadePatrol(SimWorld a, PopulationData data, string root)
    {
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
        var home = new Hand((int)FcsRecordType.Building, 2133, 11111, 5, 750000000);
        home.Write(unmade.Record, "homebuilding");
        return (RoundTrip(base0, root), at, home);
    }

    /// <summary>
    /// The unmade platoon is a stand-in: unloaded, of the saved size and template at the place, its saved platoon empty, a reserved id, and a
    /// note.
    /// </summary>
    static void IsStandIn(SimWorld w, LoadedSave loaded, Vector3 at)
    {
        var p = w.Platoons.All.Single(x => x.Unmade);

        Assert.Equal((PlatoonState.Unloaded, 4, "10-t", new Vector2(at.X, at.Z)), (p.State, p.Size, p.TemplateId, p.Position));
        Assert.Equal(UnmadeName, p.Saved!.Name);
        Assert.Empty(p.Saved.Members);
        Assert.True(p.Id > int.MaxValue - 4);   // a reserved id
        Assert.Contains($"{UnmadeName}: never made by the game; made from its template when it activates.", loaded.Notes);
    }
}
