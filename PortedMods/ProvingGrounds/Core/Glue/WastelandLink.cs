// Port only (no C++ counterpart; task 10.2): the link to The Wasteland Remembers (another author's Meitou code mod, assembly
// "LivingWastelandMeitou"), through its public class LivingWasteland.MeitouMod.WastelandApi, read by reflection so neither mod depends on
// the other (its documentation: github.com/Meitou-Modding-Ecosystem/MeitouMods docs/wasteland-remembers-api.md). Only plain types cross:
// strings, ints, bools. A member is bound only when the API's Version has it (1: Ready, Note, Moment; 2: Report; 3: LeaveAlone) and its
// signature is exactly the documented one; anything else is left unbound and the call answers false. The API says nothing in it throws;
// if a call throws anyway the link switches itself off for the world (one log line) and Proving Grounds goes on without it.
// Calls run on the sim thread in a serial step (ArenaInterop, after the frame loop), as the API asks ("call from the game thread").
// Tests/Glue/WastelandLinkTests.cs binds it to fake API classes of each version.
using System.Reflection;

namespace ProvingGrounds.Core.Glue;

public sealed class WastelandLink
{
    public const string AssemblyName = "LivingWastelandMeitou";
    public const string TypeName = "LivingWasteland.MeitouMod.WastelandApi";
    /// <summary>The source id our calls give (the mod's id, meitou.json).</summary>
    public const string Source = "ProvingGroundsMeitou";

    readonly Func<bool>? ready;
    readonly Func<string, bool>? note;
    readonly Func<string, string, int, bool>? moment;
    readonly Func<string, string, string, string, int, string, bool>? report;
    readonly Func<string, string, string, bool>? leaveAlone;
    readonly Action<string>? log;
    bool broken;

    /// <summary>The API's Version constant (0 when it has none).</summary>
    public int Version { get; }

    public bool CanNote => note is not null && !broken;
    public bool CanMoment => moment is not null && !broken;
    public bool CanReport => report is not null && !broken;
    public bool CanLeaveAlone => leaveAlone is not null && !broken;
    public bool Broken => broken;

    WastelandLink(int version, Func<bool>? ready, Func<string, bool>? note, Func<string, string, int, bool>? moment,
        Func<string, string, string, string, int, string, bool>? report, Func<string, string, string, bool>? leaveAlone, Action<string>? log)
    {
        Version = version;
        this.ready = ready;
        this.note = note;
        this.moment = moment;
        this.report = report;
        this.leaveAlone = leaveAlone;
        this.log = log;
    }

    /// <summary>The API class when The Wasteland Remembers is loaded, else null. Mods load in any order: ask on a tick, not at Initialize.</summary>
    public static Type? Find()
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (a.GetName().Name != AssemblyName) continue;
            try { if (a.GetType(TypeName, false) is { } t) return t; }
            catch (Exception) { }   // an assembly that cannot list its types: treated as absent
        }
        return null;
    }

    /// <summary>Binds the members <paramref name="api"/> has (null: not installed). Never throws.</summary>
    public static WastelandLink? Bind(Type? api, Action<string>? log)
    {
        if (api is null) return null;
        try
        {
            int version = api.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is int v ? v : 0;
            var link = new WastelandLink(version,
                version >= 1 ? Getter(api, "Ready") : null,
                version >= 1 ? Method<Func<string, bool>>(api, "Note", typeof(string)) : null,
                version >= 1 ? Method<Func<string, string, int, bool>>(api, "Moment", typeof(string), typeof(string), typeof(int)) : null,
                version >= 2 ? Method<Func<string, string, string, string, int, string, bool>>(api, "Report", typeof(string), typeof(string),
                    typeof(string), typeof(string), typeof(int), typeof(string)) : null,
                version >= 3 ? Method<Func<string, string, string, bool>>(api, "LeaveAlone", typeof(string), typeof(string), typeof(string)) : null,
                log);
            return link;
        }
        catch (Exception e)
        {
            log?.Invoke($"The Wasteland Remembers: its API could not be read ({e.GetType().Name}: {e.Message}); going on without it");
            return null;
        }
    }

    static Func<bool>? Getter(Type api, string name)
    {
        var get = api.GetProperty(name, BindingFlags.Public | BindingFlags.Static)?.GetMethod;
        if (get is null || get.ReturnType != typeof(bool)) return null;
        try { return get.CreateDelegate<Func<bool>>(); } catch (ArgumentException) { return null; }
    }

    static T? Method<T>(Type api, string name, params Type[] parameters) where T : Delegate
    {
        var m = api.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
        if (m is null || m.ReturnType != typeof(bool)) return null;
        try { return m.CreateDelegate<T>(); } catch (ArgumentException) { return null; }
    }

    /// <summary>The names of the bound members, for the log line.</summary>
    public string Describe()
    {
        var bound = new List<string>();
        if (ready is not null) bound.Add("Ready");
        if (note is not null) bound.Add("Note");
        if (moment is not null) bound.Add("Moment");
        if (report is not null) bound.Add("Report");
        if (leaveAlone is not null) bound.Add("LeaveAlone");
        return $"API version {Version}, {(bound.Count == 0 ? "nothing we use" : string.Join(", ", bound))}";
    }

    /// <summary>A game is loaded and The Wasteland Remembers runs in it (its writes answer false before).</summary>
    public bool Ready => ready is not null && Call(ready, "Ready");

    /// <summary>Keeps a town fighter out of its rivals (never made a rival, never renamed). By name and faction as the game shows them.</summary>
    public bool LeaveAlone(string name, string faction) =>
        leaveAlone is { } f && Call(() => f(name, faction ?? "", Source), "LeaveAlone");

    /// <summary>News the towns near <paramref name="place"/> talk about (a kind of our own, so <paramref name="text"/> is the line).</summary>
    public bool Report(string kind, string place, string faction, string text, int weight) =>
        report is { } f && Call(() => f(kind, place, faction ?? "", text, Math.Clamp(weight, 0, 100), Source), "Report");

    /// <summary>A line in the squad's History tab.</summary>
    public bool Note(string text) => note is { } f && Call(() => f(text), "Note");

    /// <summary>A Recap moment (through The Wasteland Remembers' own link; false without Recap).</summary>
    public bool Moment(string caption, string place, int weight) =>
        moment is { } f && Call(() => f(caption, place, Math.Clamp(weight, 0, 100)), "Moment");

    bool Call(Func<bool> f, string what)
    {
        if (broken) return false;
        try { return f(); }
        catch (Exception e)
        {
            broken = true;
            log?.Invoke($"The Wasteland Remembers: {what} threw {e.GetBaseException().GetType().Name}: {e.GetBaseException().Message}; the link is off for this world");
            return false;
        }
    }
}
