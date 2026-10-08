using System.Globalization;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.World;

/// <summary>
/// <c>meitou-tools weather</c>: prints the season and weather timeline of one region over a range of days (docs/formats/weather.md), for checking the
/// scheduler and for the doc. The region is named (<c>--region</c>) or found at a world position (<c>--at x,z</c>); <c>--list</c> prints the regions with their calendars.
/// </summary>
static class WeatherTool
{
    public static int Run(GameInstall install, string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string? regionName = null;
        (float X, float Z)? at = null;
        int from = 0, to = 100, seed = 1;
        bool list = false, extents = false, cells = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--region" when i + 1 < args.Length: regionName = args[++i]; break;
                case "--at" when i + 1 < args.Length:
                    var p = args[++i].Split(',');
                    at = (float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
                    break;
                case "--days" when i + 2 < args.Length: from = int.Parse(args[++i], CultureInfo.InvariantCulture); to = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--list": list = true; break;
                case "--extents": extents = true; break;
                case "--cells": cells = true; break;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete option {args[i]}.");
                    return 2;
            }
        }

        var db = GameDatabase.Load(LoadOrder.FromInstall(install));
        var data = WeatherData.Load(db);
        var areas = WeatherAreas.Load(install);
        Console.WriteLine($"{data.Weathers.Count} weathers, {data.Seasons.Count} seasons, {data.Regions.Count} regions, {data.DaysPerYear} days per year");
        if (extents)
        {
            // Each region's cells: how many, the bounding box and the cell nearest the mean in world units (for placing a camera in a region).
            var byRegion = new Dictionary<string, List<(int X, int Z)>>();
            for (int cz = 0; cz < WeatherAreas.Cells; cz++)
                for (int cx = 0; cx < WeatherAreas.Cells; cx++)
                {
                    var r = areas.RegionOf(data, cx, cz);
                    if (!byRegion.TryGetValue(r.Name, out var l)) byRegion[r.Name] = l = [];
                    l.Add((cx, cz));
                }
            foreach (var (name, l) in byRegion.OrderByDescending(c => c.Value.Count))
            {
                float mx = (float)l.Average(c => c.X), mz = (float)l.Average(c => c.Z);
                var nearest = l.OrderBy(c => (c.X - mx) * (c.X - mx) + (c.Z - mz) * (c.Z - mz)).First();
                var (x0, z0, x1, z1) = WeatherAreas.CellRect(nearest.X, nearest.Z);
                Console.WriteLine($"  {name,-24} {l.Count,4} cells  x {l.Min(c => c.X) - WeatherAreas.Origin}..{l.Max(c => c.X) - WeatherAreas.Origin}  z {l.Min(c => c.Z) - WeatherAreas.Origin}..{l.Max(c => c.Z) - WeatherAreas.Origin}  central cell centre at {(x0 + x1) / 2:0},{(z0 + z1) / 2:0}");
            }
            return 0;
        }
        if (list)
        {
            foreach (var r in data.Regions)
                Console.WriteLine($"  {r.Name,-24} {string.Join(", ", r.Seasons.Select(s => $"{s.Season.Name} {s.Days} d"))}");
            return 0;
        }

        RegionDef? region = regionName is not null ? data.FindRegion(regionName) : at is { } a ? areas.RegionAt(data, a.X, a.Z) : null;
        if (region is null)
        {
            Console.Error.WriteLine("Give --region <name> or --at x,z (see --list).");
            return 2;
        }
        if (cells)
        {
            // Where the region is: the centre of each of its 4608-unit cells, for placing the viewer (--at x,z).
            for (int cz = 0; cz < WeatherAreas.Cells; cz++)
                for (int cx = 0; cx < WeatherAreas.Cells; cx++)
                    if (areas.RegionOf(data, cx, cz) == region)
                        Console.WriteLine($"cell {cx},{cz}  centre {(cx - WeatherAreas.Origin + 0.5f) * WeatherAreas.CellSize:0},{(cz - WeatherAreas.Origin + 0.5f) * WeatherAreas.CellSize:0}");
            return 0;
        }
        Console.WriteLine($"Region {region.Name}, seed {seed}, days {from}..{to}");
        foreach (var s in region.Seasons)
            Console.WriteLine($"  season {s.Season.Name}: {s.Days} days, strength {s.Season.StrengthLimitMin:0.##}..{s.Season.StrengthLimitMax:0.##}, " +
                string.Join(", ", s.Season.Weathers.Select(w => $"{w.Weather.Name} x{w.Weight} ({w.MinMinutes}-{w.MaxMinutes} min)")));

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
                double endMinute = sim.WeatherEndMinute;
                Console.WriteLine($"day {time.Day,4} {time.Hours,5:0.00} h  {sim.Weather.Name,-36} s={sim.Strength:0.00}  for {endMinute - sim.WeatherStartMinute,4:0} min (to day {(int)(endMinute / 1440)} {endMinute % 1440 / 60:0.00} h)" +
                    $"  wind {sim.WindSpeed,5:0.0} u/s towards {MathF.Atan2(sim.WindDirection.Y, sim.WindDirection.X) * 180 / MathF.PI,4:0} deg" +
                    (sim.Weather.FogEnabled ? $"  fog {sim.Weather.FogDistanceAt(sim.WindSpeed):0}" : ""));
            }
        }
        return 0;
    }
}
