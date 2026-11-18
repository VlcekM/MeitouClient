// Port of upstream/ProvingGrounds/plugin/src/ArenaPerformance.h and ArenaPerformance.cpp (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>The crowd performance profiles (pg_config.json <c>ui_settings.performance_profile</c>): spectator cap, upkeep cadence and
/// crowd chatter per profile.</summary>
public static class ArenaPerformance
{
    /// <summary>C++: ArenaPerformance.h:7-12</summary>
    public enum Profile
    {
        High = 0,
        Normal = 1,
        Potato = 2
    }

    /// <summary>C++: ArenaPerformance.h:14-33 (returned by value). <c>new Policy()</c> carries the C++ default arguments
    /// (120, 0, 4.0f, 8.0f, 5, true); note <c>default(Policy)</c> is all zeros, which the C++ cannot express.</summary>
    public struct Policy
    {
        public int SpectatorCap;
        public uint MaintenanceMs;
        public float ChatterMinSeconds;
        public float ChatterMaxSeconds;
        public int MaxChatterVoices;
        public bool ChatterEnabled;

        // C++: ArenaPerformance.h:23-32 (the default arguments)
        public Policy() : this(120, 0, 4.0f, 8.0f, 5, true) { }

        // C++: ArenaPerformance.h:23-32
        public Policy(
            int cap,
            uint maintenance = 0,
            float chatterMin = 4.0f,
            float chatterMax = 8.0f,
            int voices = 5,
            bool chatter = true)
        {
            SpectatorCap = cap;
            MaintenanceMs = maintenance;
            ChatterMinSeconds = chatterMin;
            ChatterMaxSeconds = chatterMax;
            MaxChatterVoices = voices;
            ChatterEnabled = chatter;
        }
    }

    // C++: ArenaPerformance.cpp:5-8
    public static bool IsValid(int value)
    {
        return value >= (int)Profile.High && value <= (int)Profile.Potato;
    }

    // C++: ArenaPerformance.cpp:10-18
    public static string Name(Profile profile)
    {
        switch (profile)
        {
        case Profile.Normal: return "Normal";
        case Profile.Potato: return "Potato";
        default: return "High";
        }
    }

    // C++: ArenaPerformance.cpp:20-28
    public static string PersistedName(Profile profile)
    {
        switch (profile)
        {
        case Profile.Normal: return "normal";
        case Profile.Potato: return "potato";
        default: return "high";
        }
    }

    // C++: ArenaPerformance.cpp:30-37 (std::string ==: an exact, case-sensitive comparison; profile is left alone on false)
    public static bool Parse(string value, ref Profile profile)
    {
        if (value == "high") profile = Profile.High;
        else if (value == "normal") profile = Profile.Normal;
        else if (value == "potato") profile = Profile.Potato;
        else return false;
        return true;
    }

    // C++: ArenaPerformance.cpp:39-47
    public static Policy PolicyFor(Profile profile)
    {
        switch (profile)
        {
        case Profile.Normal: return new Policy(60, 100, 8.0f, 12.0f, 2, true);
        case Profile.Potato: return new Policy(20, 250, 0.0f, 0.0f, 0, false);
        default: return new Policy();
        }
    }
}
