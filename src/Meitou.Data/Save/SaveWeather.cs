using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Save;

/// <summary>
/// The weather of every region in the BIOMES record of <c>quick.save</c> (docs/formats/save.md "BIOMES"): each BIOME_GROUP id maps to a key prefix
/// <c>p</c>, and <c>p_w_s_*</c> / <c>p_w_w_id</c> / <c>p_w_wi_*</c> hold the region's season, weather and wind. Read into and written from
/// <see cref="WeatherRegionSnapshot"/>s. The original's wind angle turns the other way from Meitou's heading (its direction is <c>(cos a, 0, −sin
/// a)</c>), so heading = −angle.
/// </summary>
public static class SaveWeather
{
    /// <summary>
    /// Game-speed seconds per game minute (1200/11 s per game hour, docs/game/game-loop.md): <c>_w_wi_time</c> counts them.
    /// </summary>
    public const double SecondsPerGameMinute = 20.0 / 11.0;

    /// <summary>
    /// The region id → key prefix entries of a BIOMES record (a prefix is an integer; the region's own keys start with it and '_').
    /// </summary>
    public static IEnumerable<(string RegionId, string Prefix)> Prefixes(FcsRecord biomes)
    {
        foreach (var (key, value) in biomes.Strings)
        {
            if (value.Length > 0 && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && !key.StartsWith(value + "_", StringComparison.Ordinal))
                yield return (key, value);
        }
    }

    /// <summary>
    /// The regions the record has a weather for (a region without <c>_w_w_id</c> or <c>_w_wi_start</c> is left out: it keeps the schedule it has).
    /// </summary>
    public static List<WeatherRegionSnapshot> Read(FcsRecord biomes)
    {
        var list = new List<WeatherRegionSnapshot>();
        foreach (var (region, p) in Prefixes(biomes))
        {
            if (!biomes.Strings.TryGetValue(p + "_w_w_id", out var weather) || weather.Length == 0
                || !biomes.Ints.TryGetValue(p + "_w_wi_start", out int start))
                continue;
            int end = biomes.Ints.GetValueOrDefault(p + "_w_wi_end", start);
            int t0 = biomes.Ints.GetValueOrDefault(p + "_w_wi_bu_t_s"), t1 = biomes.Ints.GetValueOrDefault(p + "_w_wi_bu_t_e");
            float speed = biomes.Floats.GetValueOrDefault(p + "_w_wi_speed");
            var dir = biomes.Vector3S.GetValueOrDefault(p + "_w_wi_dir", Vector3.UnitX);
            float heading = MathF.Atan2(dir.Z, dir.X);
            // The last wind change: from (speed, angle) at t0 to the targets at t1 (a finished one the game loaded has both times 0); without its keys,
            // the current wind held.
            var wind = biomes.Floats.ContainsKey(p + "_w_wi_bu_a_e")
                ? new WindInterpolation(t0, Math.Max(0, t1 - t0), biomes.Floats.GetValueOrDefault(p + "_w_wi_bu_s_s", speed),
                    -biomes.Floats.GetValueOrDefault(p + "_w_wi_bu_a_s", -heading), biomes.Floats.GetValueOrDefault(p + "_w_wi_bu_s_e", speed),
                    -biomes.Floats.GetValueOrDefault(p + "_w_wi_bu_a_e", -heading))
                : new WindInterpolation(start, 0, speed, heading, speed, heading);
            list.Add(new WeatherRegionSnapshot(region, biomes.Strings.GetValueOrDefault(p + "_w_s_id", ""), -1,
                biomes.Ints.GetValueOrDefault(p + "_w_s_end"), weather, -1, start, end, biomes.Floats.GetValueOrDefault(p + "_w_wi_str", 1), wind,
                biomes.Ints.GetValueOrDefault(p + "_w_wi_udp", end), default, 0));
        }
        return list;
    }

    /// <summary>
    /// Writes the regions into the record at game time <paramref name="now"/>. A record that has regions (a loaded save) is written for those only:
    /// the original's region table is what it walked, and Meitou's data can hold BIOME_GROUPs that are not in it. An empty record (a new save) gets
    /// every region, numbered in order, each with its <c>_nestcount</c> (the BIOME_GROUP's <c>num nests</c>). <c>_w_fog_a</c> and the nest keys are
    /// left as they are.
    /// </summary>
    public static void Write(FcsRecord biomes, IEnumerable<WeatherRegionSnapshot> regions, WeatherTime now, GameDatabase? db)
    {
        var prefixes = Prefixes(biomes).ToDictionary(e => e.RegionId, e => e.Prefix, StringComparer.Ordinal);
        bool fresh = prefixes.Count == 0;
        int next = 0;
        foreach (var r in regions)
        {
            if (!prefixes.TryGetValue(r.RegionId, out var p))
            {
                if (!fresh) continue;
                p = (next++).ToString(CultureInfo.InvariantCulture);
                prefixes[r.RegionId] = p;
                biomes.Strings[r.RegionId] = p;
                biomes.Ints[p + "_nestcount"] = db?.Find(r.RegionId)?.GetInt("num nests") ?? 0;
            }
            var (speed, direction) = r.Wind.At(now.Minutes);
            biomes.Strings[p + "_w_s_id"] = r.SeasonId;
            biomes.Ints[p + "_w_s_end"] = r.SeasonEndDay;
            biomes.Strings[p + "_w_w_id"] = r.WeatherId;
            biomes.Floats[p + "_w_wi_speed"] = speed;
            biomes.Vector3S[p + "_w_wi_dir"] = new Vector3(direction.X, 0, direction.Y);
            biomes.Floats[p + "_w_wi_time"] = (float)(Math.Max(0, now.Minutes - r.WeatherStartMinute) * SecondsPerGameMinute);
            biomes.Ints[p + "_w_wi_start"] = Minute(r.WeatherStartMinute);
            biomes.Ints[p + "_w_wi_end"] = Minute(r.WeatherEndMinute);
            biomes.Ints[p + "_w_wi_udp"] = Minute(r.NextWindUpdateMinute);
            biomes.Floats[p + "_w_wi_bu_a_s"] = -r.Wind.Heading0;
            biomes.Floats[p + "_w_wi_bu_a_e"] = -r.Wind.Heading1;
            biomes.Floats[p + "_w_wi_bu_s_s"] = r.Wind.Speed0;
            biomes.Floats[p + "_w_wi_bu_s_e"] = r.Wind.Speed1;
            biomes.Ints[p + "_w_wi_bu_t_s"] = Minute(r.Wind.StartMinute);
            biomes.Ints[p + "_w_wi_bu_t_e"] = Minute(r.Wind.StartMinute + r.Wind.DurationMinutes);
            biomes.Floats[p + "_w_wi_str"] = r.Strength;
            // The original stores the weather's effect strength (its WEATHER `effect strength min`) truncated to an int.
            biomes.Ints[p + "_w_wi_ef_str"] = (int)(db?.Find(r.WeatherId)?.GetFloat("effect strength min", 1) ?? 1);
        }
    }

    /// <summary>
    /// A minute count as the int the save stores (floored, clamped to the int range).
    /// </summary>
    static int Minute(double minutes) => (int)Math.Clamp(Math.Floor(minutes), int.MinValue, int.MaxValue);
}