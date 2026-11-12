using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// The CAMERA record (type 56) of <c>quick.save</c>: the world record that holds everything which is not a collection (docs/formats/save.md "CAMERA").
/// Typed access to the fields the simulation uses; the rest (selection, squad toggles, unique characters, sky ramps) stays in <see cref="Record"/>.
/// </summary>
public sealed class SaveCamera(FcsRecord record)
{
    /// <summary>
    /// The CAMERA record the properties read and write.
    /// </summary>
    public FcsRecord Record { get; } = record;

    /// <summary>
    /// The game version that wrote the save ("1.0.68").
    /// </summary>
    public string Version { get => Record.Strings.GetValueOrDefault("version", ""); set => Record.Strings["version"] = value; }

    /// <summary>
    /// <c>time day</c>: the game day.
    /// </summary>
    public int Day { get => Record.Ints.GetValueOrDefault("time day"); set => Record.Ints["time day"] = value; }

    /// <summary>
    /// <c>time hour</c>: the hour of the game day.
    /// </summary>
    public int Hour { get => Record.Ints.GetValueOrDefault("time hour"); set => Record.Ints["time hour"] = value; }

    /// <summary>
    /// <c>time minute</c>: the minute of the game hour.
    /// </summary>
    public int Minute { get => Record.Ints.GetValueOrDefault("time minute"); set => Record.Ints["time minute"] = value; }

    /// <summary>
    /// <c>player money</c>.
    /// </summary>
    public int PlayerMoney { get => Record.Ints.GetValueOrDefault("player money"); set => Record.Ints["player money"] = value; }

    /// <summary>
    /// <c>pfaction name</c>: the player's faction name.
    /// </summary>
    public string PlayerFactionName { get => Record.Strings.GetValueOrDefault("pfaction name", ""); set => Record.Strings["pfaction name"] = value; }

    /// <summary>
    /// <c>squads</c>: the player's squad count.
    /// </summary>
    public int Squads { get => Record.Ints.GetValueOrDefault("squads"); set => Record.Ints["squads"] = value; }

    /// <summary>
    /// <c>members</c>: the player's member count.
    /// </summary>
    public int Members { get => Record.Ints.GetValueOrDefault("members"); set => Record.Ints["members"] = value; }

    /// <summary>
    /// <c>pos</c>: the camera pivot position (docs/formats/camera.md).
    /// </summary>
    public Vector3 Position { get => Record.Vector3S.GetValueOrDefault("pos"); set => Record.Vector3S["pos"] = value; }

    /// <summary>
    /// The camera pivot's orientation as the game stores it: <b>w, x, y, z</b> (the Ogre quaternion copied as is;
    /// <see cref="World.KenshiCamera.FromSaved"/>).
    /// </summary>
    public Vector4 Rotation { get => Record.Vector4S.GetValueOrDefault("rot", new Vector4(1, 0, 0, 0)); set => Record.Vector4S["rot"] = value; }

    /// <summary>
    /// Whether the record holds a camera view. Meitou saves written before it kept the camera have <c>pos</c> 0 and <c>rot</c> (0, 0, 0, 1), which
    /// read as w, x, y, z is a half turn of roll the game's camera cannot make, so that exact value marks "no view".
    /// </summary>
    public bool HasView => Record.Vector3S.ContainsKey("pos") && Record.Vector4S.TryGetValue("rot", out var rot) && rot != new Vector4(0, 0, 0, 1);

    /// <summary>
    /// <c>zoom</c>: the camera zoom (docs/formats/camera.md).
    /// </summary>
    public float Zoom { get => Record.Floats.GetValueOrDefault("zoom"); set => Record.Floats["zoom"] = value; }

    /// <summary>
    /// <c>area</c>: the region name the banner shows (e.g. "Okran's Gulf").
    /// </summary>
    public string Area { get => Record.Strings.GetValueOrDefault("area", ""); set => Record.Strings["area"] = value; }

    /// <summary>
    /// The region banner's cooldown in seconds (<c>biome_text_timer</c>; the original writes its raw, ever falling value).
    /// </summary>
    public float BiomeTextTimer { get => Record.Floats.GetValueOrDefault("biome_text_timer"); set => Record.Floats["biome_text_timer"] = value; }

    /// <summary>
    /// The BIOME_GROUP the region banner last tracked (<c>biome_text_groud</c>; empty: none).
    /// </summary>
    public string BiomeTextGroup { get => Record.Strings.GetValueOrDefault("biome_text_groud", ""); set => Record.Strings["biome_text_groud"] = value; }

    /// <summary>
    /// <c>sky update current time</c>: how far the camera's 30 s sky transition is (docs/formats/sky.md).
    /// </summary>
    public float SkyUpdateCurrentTime
    {
        get => Record.Floats.GetValueOrDefault("sky update current time");
        set => Record.Floats["sky update current time"] = value;
    }

    /// <summary>
    /// <c>sky update total time</c>: the sky transition's length.
    /// </summary>
    public float SkyUpdateTotalTime
    {
        get => Record.Floats.GetValueOrDefault("sky update total time");
        set => Record.Floats["sky update total time"] = value;
    }

    /// <summary>
    /// <c>sky updating</c>: whether the sky transition is running.
    /// </summary>
    public bool SkyUpdating { get => Record.Bools.GetValueOrDefault("sky updating"); set => Record.Bools["sky updating"] = value; }

    /// <summary>
    /// <c>sky ambient color mult</c>: the sky's ambient multiplier (default 1).
    /// </summary>
    public Vector3 SkyAmbientColorMult
    {
        get => Record.Vector3S.GetValueOrDefault("sky ambient color mult", Vector3.One);
        set => Record.Vector3S["sky ambient color mult"] = value;
    }

    /// <summary>
    /// <c>sky clouds density</c>: the sky's cloud density.
    /// </summary>
    public float SkyCloudsDensity { get => Record.Floats.GetValueOrDefault("sky clouds density"); set => Record.Floats["sky clouds density"] = value; }

    /// <summary>
    /// The selected character handle (null when none).
    /// </summary>
    public Hand SelectedCharacter { get => Hand.Read(Record, "selected_character"); set => value.Write(Record, "selected_character"); }

    /// <summary>
    /// The character the camera follows.
    /// </summary>
    public Hand Tracking { get => Hand.Read(Record, "tracking"); set => value.Write(Record, "tracking"); }

    /// <summary>
    /// The <c>zones</c> list: the zones that have a file, in grid order (X outer, Y inner), flag 1 = has a file.
    /// </summary>
    public IReadOnlyList<ZoneCoordinate> Zones =>
        Record.References.TryGetValue("zones", out var list) ? [.. list.Select(r => new ZoneCoordinate(r.Value0, r.Value1))] : [];

    /// <summary>
    /// Writes the <c>zones</c> list the way the game does: grid order, the entry's key its index in hexadecimal, values (X, Y, 1).
    /// </summary>
    public void SetZones(IEnumerable<ZoneCoordinate> zones)
    {
        var list = new List<FcsReference>();
        foreach (var z in zones.Distinct().OrderBy(z => z.X).ThenBy(z => z.Y))
            list.Add(new FcsReference(list.Count.ToString("x", System.Globalization.CultureInfo.InvariantCulture), z.X, z.Y, 1));
        Record.References["zones"] = list;
    }

    /// <summary>
    /// The <c>mods</c> list: the loaded data files in load order, as names without extension (<c>base</c> for <c>gamedata.base</c>).
    /// </summary>
    public IReadOnlyList<string> Mods =>
        Record.References.TryGetValue("mods", out var list) ? [.. list.Select(r => r.TargetStringId)] : [];

    /// <summary>
    /// Writes the <c>mods</c> list from data file names (<c>gamedata.base</c>, <c>Newwworld.mod</c>...): key = name without extension, <c>base</c>
    /// for the core file, values (-1, 0, 0).
    /// </summary>
    public void SetMods(IEnumerable<string> fileNames) =>
        Record.References["mods"] = [.. fileNames.Select(n => new FcsReference(ModKey(n), -1, 0, 0))];

    /// <summary>
    /// The key a data file has in <c>mods</c>.
    /// </summary>
    public static string ModKey(string fileName) =>
        fileName.EndsWith(".base", StringComparison.OrdinalIgnoreCase) ? "base" : Path.GetFileNameWithoutExtension(fileName);
}