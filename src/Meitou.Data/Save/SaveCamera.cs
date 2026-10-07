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
    public FcsRecord Record { get; } = record;

    /// <summary>The game version that wrote the save ("1.0.68").</summary>
    public string Version { get => Record.Strings.GetValueOrDefault("version", ""); set => Record.Strings["version"] = value; }

    public int Day { get => Record.Ints.GetValueOrDefault("time day"); set => Record.Ints["time day"] = value; }
    public int Hour { get => Record.Ints.GetValueOrDefault("time hour"); set => Record.Ints["time hour"] = value; }
    public int Minute { get => Record.Ints.GetValueOrDefault("time minute"); set => Record.Ints["time minute"] = value; }

    public int PlayerMoney { get => Record.Ints.GetValueOrDefault("player money"); set => Record.Ints["player money"] = value; }
    public string PlayerFactionName { get => Record.Strings.GetValueOrDefault("pfaction name", ""); set => Record.Strings["pfaction name"] = value; }
    public int Squads { get => Record.Ints.GetValueOrDefault("squads"); set => Record.Ints["squads"] = value; }
    public int Members { get => Record.Ints.GetValueOrDefault("members"); set => Record.Ints["members"] = value; }

    public Vector3 Position { get => Record.Vector3s.GetValueOrDefault("pos"); set => Record.Vector3s["pos"] = value; }

    /// <summary>x, y, z, w.</summary>
    public Vector4 Rotation { get => Record.Vector4s.GetValueOrDefault("rot", new Vector4(0, 0, 0, 1)); set => Record.Vector4s["rot"] = value; }
    public float Zoom { get => Record.Floats.GetValueOrDefault("zoom"); set => Record.Floats["zoom"] = value; }
    public string Area { get => Record.Strings.GetValueOrDefault("area", ""); set => Record.Strings["area"] = value; }

    public float SkyUpdateCurrentTime { get => Record.Floats.GetValueOrDefault("sky update current time"); set => Record.Floats["sky update current time"] = value; }
    public float SkyUpdateTotalTime { get => Record.Floats.GetValueOrDefault("sky update total time"); set => Record.Floats["sky update total time"] = value; }
    public bool SkyUpdating { get => Record.Bools.GetValueOrDefault("sky updating"); set => Record.Bools["sky updating"] = value; }
    public Vector3 SkyAmbientColorMult { get => Record.Vector3s.GetValueOrDefault("sky ambient color mult", Vector3.One); set => Record.Vector3s["sky ambient color mult"] = value; }
    public float SkyCloudsDensity { get => Record.Floats.GetValueOrDefault("sky clouds density"); set => Record.Floats["sky clouds density"] = value; }

    /// <summary>The selected character handle (null when none).</summary>
    public Hand SelectedCharacter { get => Hand.Read(Record, "selected_character"); set => value.Write(Record, "selected_character"); }

    /// <summary>The character the camera follows.</summary>
    public Hand Tracking { get => Hand.Read(Record, "tracking"); set => value.Write(Record, "tracking"); }

    /// <summary>The <c>zones</c> list: the zones that have a file, in grid order (X outer, Y inner), flag 1 = has a file.</summary>
    public IReadOnlyList<ZoneCoordinate> Zones =>
        Record.References.TryGetValue("zones", out var list) ? [.. list.Select(r => new ZoneCoordinate(r.Value0, r.Value1))] : [];

    /// <summary>Writes the <c>zones</c> list the way the game does: grid order, the entry's key its index in hexadecimal, values (X, Y, 1).</summary>
    public void SetZones(IEnumerable<ZoneCoordinate> zones)
    {
        var list = new List<FcsReference>();
        foreach (var z in zones.Distinct().OrderBy(z => z.X).ThenBy(z => z.Y))
            list.Add(new FcsReference(list.Count.ToString("x", System.Globalization.CultureInfo.InvariantCulture), z.X, z.Y, 1));
        Record.References["zones"] = list;
    }

    /// <summary>The <c>mods</c> list: the loaded data files in load order, as names without extension (<c>base</c> for <c>gamedata.base</c>).</summary>
    public IReadOnlyList<string> Mods =>
        Record.References.TryGetValue("mods", out var list) ? [.. list.Select(r => r.TargetStringId)] : [];

    /// <summary>Writes the <c>mods</c> list from data file names (<c>gamedata.base</c>, <c>Newwworld.mod</c>...): key = name without extension, <c>base</c> for the core file, values (-1, 0, 0).</summary>
    public void SetMods(IEnumerable<string> fileNames) =>
        Record.References["mods"] = [.. fileNames.Select(n => new FcsReference(ModKey(n), -1, 0, 0))];

    /// <summary>The key a data file has in <c>mods</c>.</summary>
    public static string ModKey(string fileName) =>
        fileName.EndsWith(".base", StringComparison.OrdinalIgnoreCase) ? "base" : Path.GetFileNameWithoutExtension(fileName);
}
