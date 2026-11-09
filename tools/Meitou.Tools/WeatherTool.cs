using System.Globalization;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

/// <summary>
/// <c>meitou-tools weather</c>: prints the season and weather timeline of one region over a range of days (docs/formats/weather.md), for checking the
/// scheduler and for the doc. The region is named (<c>--region</c>) or found at a world position (<c>--at x,z</c>); <c>--list</c> prints the regions
/// with their calendars.
/// </summary>
static class WeatherTool
{
    /// <summary>
    /// <c>meitou-tools weather</c>: parses the options (<c>--region</c>, <c>--at</c>, <c>--days</c>, <c>--seed</c>, <c>--list</c>, <c>--extents</c>)
    /// and prints the region list, the regions' extents or a region's timeline. Returns the exit code.
    /// </summary>
    public static int Run(GameInstall install, string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string? regionName = null;
        (float X, float Z)? at = null;
        int from = 0, to = 100, seed = 1;
        bool list = false, extents = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--region" when i + 1 < args.Length: regionName = args[++i]; break;
                case "--at" when i + 1 < args.Length:
                    var p = args[++i].Split(',');
                    at = (float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
                    break;
                case "--days" when i + 2 < args.Length:
                    from = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    to = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--list": list = true; break;
                case "--extents": extents = true; break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete option {args[i]}.");
                    return 2;
            }
        }

        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var data = WeatherData.Load(db);
        var areas = WeatherAreas.Load(install);
        Console.WriteLine($"{data.Weathers.Count} weathers, {data.Seasons.Count} seasons, "
            + $"{data.Regions.Count} regions, {data.DaysPerYear} days per year");
        if (extents)
            return PrintExtents(data, areas);

        if (list)
            return PrintRegionList(data);

        RegionDef? region = regionName is not null ? data.FindRegion(regionName) : at is { } a ? areas.RegionAt(data, a.X, a.Z) : null;
        if (region is null)
        {
            Console.Error.WriteLine("Give --region <name> or --at x,z (see --list).");
            return 2;
        }

        PrintTimeline(data, region, seed, from, to);
        return 0;
    }

    /// <summary>
    /// Each region's cells: how many, the bounding box and the cell nearest the mean in world units (for placing a camera in a region).
    /// </summary>
    static int PrintExtents(WeatherData data, WeatherAreas areas)
    {
        var byRegion = CellsByRegion(data, areas);
        foreach (var (name, cells) in byRegion.OrderByDescending(c => c.Value.Count))
            PrintRegionExtent(name, cells);
        return 0;
    }

    /// <summary>
    /// Every region's zone cells, by region name.
    /// </summary>
    static Dictionary<string, List<(int X, int Z)>> CellsByRegion(WeatherData data, WeatherAreas areas)
    {
        var byRegion = new Dictionary<string, List<(int X, int Z)>>();
        for (int cz = 0; cz < WeatherAreas.Cells; cz++)
        {
            for (int cx = 0; cx < WeatherAreas.Cells; cx++)
            {
                var r = areas.RegionOf(data, cx, cz);
                if (!byRegion.TryGetValue(r.Name, out var l)) byRegion[r.Name] = l = [];
                l.Add((cx, cz));
            }
        }

        return byRegion;
    }

    /// <summary>
    /// Prints a region's cell count, its x and z extent in cells and the centre of its most central cell.
    /// </summary>
    static void PrintRegionExtent(string name, List<(int X, int Z)> cells)
    {
        float mx = (float)cells.Average(c => c.X), mz = (float)cells.Average(c => c.Z);
        var nearest = cells.OrderBy(c => (c.X - mx) * (c.X - mx) + (c.Z - mz) * (c.Z - mz)).First();
        var (x0, z0, x1, z1) = WeatherAreas.CellRect(nearest.X, nearest.Z);
        string xs = $"{cells.Min(c => c.X) - WeatherAreas.Origin}..{cells.Max(c => c.X) - WeatherAreas.Origin}";
        string zs = $"{cells.Min(c => c.Z) - WeatherAreas.Origin}..{cells.Max(c => c.Z) - WeatherAreas.Origin}";
        Console.WriteLine($"  {name,-24} {cells.Count,4} cells  x {xs}  z {zs}  central cell centre at {(x0 + x1) / 2:0},{(z0 + z1) / 2:0}");
    }

    /// <summary>
    /// Prints every region with its seasons and their weathers; returns 0.
    /// </summary>
    static int PrintRegionList(WeatherData data)
    {
        foreach (var r in data.Regions)
            Console.WriteLine($"  {r.Name,-24} {string.Join(", ", r.Seasons.Select(s => $"{s.Season.Name} {s.Days} d"))}");
        return 0;
    }

    /// <summary>
    /// Prints the region's seasons and weather changes from day <paramref name="from"/> to day <paramref name="to"/>, the scheduler seeded with
    /// <paramref name="seed"/>.
    /// </summary>
    static void PrintTimeline(WeatherData data, RegionDef region, int seed, int from, int to)
    {
        Console.WriteLine($"Region {region.Name}, seed {seed}, days {from}..{to}");
        foreach (var s in region.Seasons)
        {
            string weathers = string.Join(", ", s.Season.Weathers.Select(w => $"{w.Weather.Name} x{w.Weight} ({w.MinMinutes}-{w.MaxMinutes} min)"));
            Console.WriteLine($"  season {s.Season.Name}: {s.Days} days, "
                + $"strength {s.Season.StrengthLimitMin:0.##}..{s.Season.StrengthLimitMax:0.##}, " + weathers);
        }

        int index = data.Regions.ToList().IndexOf(region);
        var sim = new WeatherRegion(region, index, seed, WeatherTime.At(from));
        int lastSerial = 0, lastSeason = -1;
        int end = to * 1440;
        for (int minute = from * 1440; minute <= end; minute++)
        {
            var time = WeatherTime.At(minute / 1440, minute % 1440 / 60.0);
            sim.Update(time);
            if (sim.SeasonIndex != lastSeason)
            {
                Console.WriteLine($"day {time.Day,4}  season {sim.Season.Season.Name} until day {sim.SeasonEndDay}");
                lastSeason = sim.SeasonIndex;
            }

            if (sim.Serial != lastSerial)
            {
                lastSerial = sim.Serial;
                PrintWeatherChange(sim, time);
            }
        }
    }

    /// <summary>
    /// Prints one weather change: the weather, how long it lasts, the wind and the fog.
    /// </summary>
    static void PrintWeatherChange(WeatherRegion sim, WeatherTime time)
    {
        double endMinute = sim.WeatherEndMinute;
        string until = $"for {endMinute - sim.WeatherStartMinute,4:0} min (to day {(int)(endMinute / 1440)} {endMinute % 1440 / 60:0.00} h)";
        string wind = $"wind {sim.WindSpeed,5:0.0} u/s towards {MathF.Atan2(sim.WindDirection.Y, sim.WindDirection.X) * 180 / MathF.PI,4:0} deg";
        string fog = sim.Weather.FogEnabled ? $"  fog {sim.Weather.FogDistanceAt(sim.WindSpeed):0}" : "";
        Console.WriteLine($"day {time.Day,4} {time.Hours,5:0.00} h  {sim.Weather.Name,-36} s={sim.Strength:0.00}  {until}  {wind}{fog}");
    }
}
