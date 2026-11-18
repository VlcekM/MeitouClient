using System.Globalization;

namespace ProvingGrounds.Core.Glue;

/// <summary>The pure half of the real <see cref="IPgLogSink"/> (src/Engine/PgLogSink.cs, task 2.7): the folder text, the values of the session
/// id, the C build text, the two startPlugin lines; and the route that gives every error to Meitou's error log too. Not a port of a file except where cited.</summary>
public static class PgLogGlue
{
    /// <summary>The mod folder WITH one trailing separator (PGLog.cpp:21-22 keeps the module path up to and including its last <c>\</c> or
    /// <c>/</c>); "" for none (then nothing is written, PGLog.cpp:43).</summary>
    public static string Folder(string? folder, char separator)
    {
        if (string.IsNullOrEmpty(folder)) return "";
        char last = folder[^1];
        return last == '\\' || last == '/' ? folder : folder + separator;
    }

    /// <summary><c>GetSystemTimeAsFileTime</c> (PGLog.cpp:24): 100 ns ticks since 1601-01-01 UTC, as its two DWORDs.</summary>
    public static (uint High, uint Low) FileTime(DateTime utc)
    {
        ulong ft = (ulong)utc.ToFileTimeUtc();
        return ((uint)(ft >> 32), (uint)(ft & 0xFFFFFFFFu));
    }

    /// <summary><c>GetTickCount64()</c> (CombatBalanceLog.cpp:167, :199 <c>wall_seconds</c>): real milliseconds since the system started, the
    /// counter <c>Environment.TickCount64</c> reads (GetTickCount64 on Windows). Real time, NOT the sim clock: it runs while the game is paused
    /// and does not speed up with the game, as the C++'s (task 8.7c; divergences.md "Diagnostics and balance logs (task 8.7)").</summary>
    // C++: CombatBalanceLog.cpp:167, :199 (GetTickCount64)
    public static ulong WallMs64() => (ulong)Environment.TickCount64;

    /// <summary><c>GetSystemTime</c>'s fields of a UTC time (PGLog.cpp:34).</summary>
    public static PgSystemTime SystemTime(DateTime utc) =>
        new((ushort)utc.Year, (ushort)utc.Month, (ushort)utc.Day, (ushort)utc.Hour, (ushort)utc.Minute, (ushort)utc.Second, (ushort)utc.Millisecond);

    static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary><c>__DATE__ " " __TIME__</c> (Plugin.cpp:15-16): "Mmm dd yyyy hh:mm:ss", the day padded with a space (C11 6.10.8.1).</summary>
    public static string BuildStamp(DateTime local)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        return Months[local.Month - 1] + " " + local.Day.ToString(ci).PadLeft(2, ' ') + " " + local.Year.ToString("D4", ci) + " " +
            local.Hour.ToString("D2", ci) + ":" + local.Minute.ToString("D2", ci) + ":" + local.Second.ToString("D2", ci);
    }

    /// <summary>The first two lines of startPlugin, before PGConfig::Load (the glue calls this once per process, right after Install).</summary>
    // C++: Plugin.cpp:15-16
    public static void StartPlugin(string build)
    {
        PGLog.Debug("Proving Grounds: startPlugin; logs beside ProvingGrounds.dll in the mod folder; build " + build);
        PGLog.Combat("\"event\":\"session_start\",\"schema\":1,\"build\":\"" + build + "\"");
    }
}

/// <summary>The glue's PGLog channels (task 2.7): <see cref="Debug"/> = <see cref="PGLog.Debug"/>; <see cref="Error"/> = <see cref="PGLog.Error"/>
/// AND Meitou's error log, whether or not pg_debug.log could be written (no folder, Install failed or not run): an error must never be
/// lost. Also the <see cref="IConfigLog"/> given to PGConfig, so its errors take the same way (PGConfig.cpp calls PGLog::Error). The
/// Meitou half is contained: a throw there never reaches the caller. Not a port (the original had only its file).</summary>
public sealed class PgLogRoute(Action<string> meitouError) : IConfigLog
{
    public void Debug(string message) => PGLog.Debug(message);

    public void Error(string message)
    {
        PGLog.Error(message);
        try { meitouError(message); } catch (Exception) { }
    }
}

/// <summary>The one JSON line every <c>pg</c> console verb answers (task 2.7): <c>{"verb":..,"ok":true,..}</c> or
/// <c>{"verb":..,"ok":false,"error":..}</c>. Field values are given as JSON text. Not a port: the original had no console.</summary>
public static class PgConsoleAnswer
{
    /// <summary>A JSON string (PGLog's quoting: valid JSON, non-ASCII kept as is).</summary>
    public static string Text(string? s) => PGLog.Quote(s);

    /// <summary>A JSON array of strings.</summary>
    public static string TextList(IEnumerable<string> items) => "[" + string.Join(",", items.Select(Text)) + "]";

    public static string Bool(bool b) => b ? "true" : "false";

    public static string Ok(string verb, params (string Key, string Json)[] fields)
    {
        var sb = new System.Text.StringBuilder("{\"verb\":").Append(Text(verb)).Append(",\"ok\":true");
        foreach (var (key, json) in fields) sb.Append(',').Append(Text(key)).Append(':').Append(json);
        return sb.Append('}').ToString();
    }

    public static string Fail(string verb, string error) => "{\"verb\":" + Text(verb) + ",\"ok\":false,\"error\":" + Text(error) + "}";

    /// <summary>An answer that is already one JSON object starting with its verb gets <c>"ok":true</c> after the verb; anything else is
    /// wrapped as <c>value</c>.</summary>
    public static string WithOk(string verb, string json)
    {
        string prefix = "{\"verb\":" + Text(verb) + ",";
        return json.StartsWith(prefix, StringComparison.Ordinal) ? prefix + "\"ok\":true," + json[prefix.Length..] : Ok(verb, ("value", json));
    }
}

/// <summary>The F10 row "Proving Grounds: status" (task 2.7): the system's status line, then each subsystem's phase, the current match and the
/// town state. Not a port.</summary>
public static class PgStatusRow
{
    public static string Text(string status, IReadOnlyList<(string Name, string Phase)> phases, string match, string town, string? perf = null) =>
        status + "; subsystems: " + string.Join(", ", phases.Select(p => p.Name + " " + p.Phase)) + "; current match: " + match + "; town: " + town
        + (perf is null ? "" : "; " + perf);   // task 9.2a: the tick cost (TickCostStats.RowText) while pg perf is on; null = the row as before

    /// <summary>The subsystem a frame-loop step belongs to: its name before the first dot ("KOWatcher.SlowWorld" -> "KOWatcher"); a name
    /// without a dot is its own subsystem.</summary>
    public static string Subsystem(string stepName)
    {
        int dot = stepName.IndexOf('.');
        return dot < 0 ? stepName : stepName[..dot];
    }
}

/// <summary>What the glue reads for one fighter of a <see cref="CombatBalanceLog"/> line (CombatBalanceLog.cpp:90-157 Fighter / StartAbility),
/// turned into a <see cref="CombatBalanceLog.FighterInfo"/>, with <c>ArenaCombatProfile::Read</c> (style, the six stats, Evaluate) and
/// <c>RosterStatus::Read</c> (health), task 7.2.</summary>
public static class CombatLogFighter
{
    /// <summary>The engine reads for one <c>Character*</c>.</summary>
    public struct Reads
    {
        /// <summary>A character (C++ non-null <c>Character*</c>).</summary>
        public bool Present;
        /// <summary><c>c->isValid()</c>.</summary>
        public bool Valid;
        /// <summary><c>TownMatchmakingRuntime::Identity(c)</c> (FighterIdentity::Find; "" when none, TownMatchmakingRuntime.cpp:21-25).</summary>
        public string? Identity;
        /// <summary><c>TownMatchmakingRuntime::Role(c)</c>: the character's record string id (:26-29).</summary>
        public string? Role;
        /// <summary><c>c->getName()</c>.</summary>
        public string? Name;
        /// <summary><c>LeaderboardStore::GetRating(c, kind)</c> (LeaderboardStoreLogic.GetRating, task 5.1), read only for a valid character, as <c>Fighter()</c>
        /// does (<c>valid ? GetRating(c, kind) : 0</c>, CombatBalanceLog.cpp:153); ignored otherwise.</summary>
        public float Rating;
        /// <summary><c>ArenaCombatProfile::Read(c)</c> (task 7.2; a new Profile for no character); <c>Evaluate</c> is taken from it.</summary>
        public ArenaCombatProfile.Profile Profile;
        /// <summary><c>RosterStatus::Read(c, health)</c> succeeded (task 7.2), and its snapshot.</summary>
        public bool HasHealth;
        public RosterStatus.Snapshot Health;
    }

    /// <summary>Null or invalid -> <see cref="CombatBalanceLog.FighterInfo.Unavailable"/>; valid -> identity, role, name, rating. Copies
    /// <c>Fighter()</c> (CombatBalanceLog.cpp:136-157), which reads the rating only for a valid character. StartAbility's own GetRating of
    /// any non-null character (:96) is its caller's read (M3 / M7), not this one.</summary>
    public static CombatBalanceLog.FighterInfo From(in Reads r)
    {
        var f = CombatBalanceLog.FighterInfo.Unavailable;
        if (!r.Present || !r.Valid) return f;
        f.Rating = r.Rating;
        f.Valid = true;
        f.Identity = r.Identity ?? "";
        f.Role = r.Role ?? "";
        f.Name = r.Name ?? "";
        f.ProfileValid = r.Profile.Valid;
        f.Style = r.Profile.Style ?? "";
        f.Attack = r.Profile.Attack; f.Defence = r.Profile.Defence; f.Strength = r.Profile.Strength;
        f.Toughness = r.Profile.Toughness; f.Dexterity = r.Profile.Dexterity; f.WeaponSkill = r.Profile.WeaponSkill;
        f.Ability = ArenaCombatProfile.Evaluate(r.Profile);
        f.HasHealth = r.HasHealth;
        if (r.HasHealth) { f.LowestLimb = r.Health.LowestLimb; f.Blood = r.Health.Blood; f.Recovery = r.Health.Recovery; }
        return f;
    }
}
