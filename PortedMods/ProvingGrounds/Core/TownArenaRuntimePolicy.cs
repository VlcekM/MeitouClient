using System.Globalization;

namespace ProvingGrounds.Core;

/// <summary>C++: TownArenaRuntimePolicy.h (header-only, namespace <c>TownArenaRuntimePolicy</c>). The two templates over engine types
/// (<c>DiscoveryIndex&lt;Actor&gt;</c> holding <c>Actor*</c>, <c>GuardPreparation&lt;Preparation&gt;</c>) are generics here, so no engine
/// type enters Core (rule 7); the glue instantiates them.</summary>
public static class TownArenaRuntimePolicy
{
    // Player reservations hold live handles and are cancelled on load. Their
    // preview keys must distinguish namesakes without requiring saved NPC IDs.
    // C++: TownArenaRuntimePolicy.h:15-20
    public static string PlayerSelectionKey(uint type, uint container, uint containerSerial,
        uint index, uint serial)
    {
        return string.Create(CultureInfo.InvariantCulture, $"player:{type}:{container}:{containerSerial}:{index}:{serial}");
    }

    // Medical staff are distinct live actors; only booked opponent lookup needs
    // an unambiguous persistent key. Namesake staff must not disappear with it.
    /// <summary><c>std::vector&lt;Actor*&gt;</c> -&gt; <see cref="Actors"/>, <c>std::map&lt;std::string, Actor*&gt;</c> -&gt; <see cref="ByIdentity"/>
    /// (ordinal key order; a <c>null</c> value is the C++ NULL that marks an ambiguous key). Actors compare by reference, as the C++
    /// compares pointers in <c>std::find</c>.</summary>
    // C++: TownArenaRuntimePolicy.h:23-36
    public sealed class DiscoveryIndex<TActor> where TActor : class
    {
        public readonly List<TActor> Actors = new();
        public readonly SortedDictionary<string, TActor?> ByIdentity = new(StringComparer.Ordinal);

        // C++: TownArenaRuntimePolicy.h:26-35
        public bool Add(TActor? actor, string key)
        {
            if (actor == null || Actors.Exists(a => ReferenceEquals(a, actor))) return false;
            Actors.Add(actor);
            if (key.Length != 0)
            {
                if (!ByIdentity.ContainsKey(key)) ByIdentity[key] = actor;
                else ByIdentity[key] = null;
            }
            return true;
        }
    }

    // C++: TownArenaRuntimePolicy.h:37-42
    public static void MedicStandbyOffset(float dx, float dz, out float x, out float z)
    {
        float length = MathF.Sqrt(dx * dx + dz * dz);
        x = length > 1.0f ? dx * 190.0f / length : 190.0f;
        z = length > 1.0f ? dz * 190.0f / length : 0.0f;
    }

    // C++: TownArenaRuntimePolicy.h:43-46
    public static bool RetainedIncidentPair(bool retained, int a, int b)
    {
        return retained && ((a == 0 && b == 1) || (a == 1 && b == 0));
    }

    // C++: TownArenaRuntimePolicy.h:47-50
    public static bool ExemptArenaIncident(bool townMatch, bool opponents, bool combatIncident)
    {
        return townMatch && opponents && combatIncident;
    }

    // C++: TownArenaRuntimePolicy.h:51-54
    public static float AdvanceAftercare(float elapsed, float dt, bool paused)
    {
        return !paused && dt > 0.0f ? elapsed + dt : elapsed;
    }

    // C++: TownArenaRuntimePolicy.h:55
    public static bool AftercareExpired(float elapsed) { return elapsed >= 300.0f; }

    /// <summary>The C++ <c>Equals</c>, renamed <c>IdEquals</c> so it does not sit beside <see cref="object.Equals(object, object)"/>.
    /// <c>id</c> is a <c>const char*</c> in the C++: <c>null</c> stands for NULL (never equal). Ordinal comparison (std::strcmp).</summary>
    // C++: TownArenaRuntimePolicy.h:56-59
    public static bool IdEquals(string? id, string expected)
    {
        return id != null && string.Equals(id, expected, StringComparison.Ordinal);
    }

    // C++: TownArenaRuntimePolicy.h:60-63
    public static bool IsNewArena(string? id)
    {
        return IdEquals(id, "183-Proving Grounds.mod");
    }

    // C++: TownArenaRuntimePolicy.h:64-67
    public static bool IsTownRegistry(string? id)
    {
        return IdEquals(id, "195-Proving Grounds.mod");
    }

    // C++: TownArenaRuntimePolicy.h:68-71
    public static bool IsRegularFighter(string? id)
    {
        return TownFighterCatalog.Find(id) != null;
    }

    // C++: TownArenaRuntimePolicy.h:72-75
    public static bool IsMedic(string? id)
    {
        return IdEquals(id, "100-Proving Grounds.mod");
    }

    /// <summary>C++: TownArenaRuntimePolicy.h:76-80 (a plain aggregate, passed by const reference).</summary>
    public struct Readiness
    {
        public bool Valid, Player, Dead, Unconscious, InCombat, NeedsAid;
        public float MinimumHealth, Blood;
    }

    // C++: TownArenaRuntimePolicy.h:81-85
    public static bool CanParticipate(in Readiness r)
    {
        return r.Valid && !r.Player && !r.Dead && !r.Unconscious &&
            !r.InCombat && !r.NeedsAid && r.MinimumHealth >= 0.9f && r.Blood >= 0.9f;
    }

    /// <summary>What <see cref="GuardPreparation"/> needs of its argument: the C++ template calls <c>Ready()</c> and <c>Cancel()</c>.</summary>
    public interface IPreparation
    {
        bool Ready();
        void Cancel();
    }

    // Used both while gathering and at ingress's final combat handoff.
    // Cancellation owns ingress cleanup and the existing ticket refund path.
    /// <summary>The C++ takes the preparation by reference; <c>ref</c> keeps that for a struct implementation.</summary>
    // C++: TownArenaRuntimePolicy.h:88-93
    public static bool GuardPreparation<TPreparation>(bool npcPreparation, ref TPreparation preparation) where TPreparation : IPreparation
    {
        if (!npcPreparation || preparation.Ready()) return true;
        preparation.Cancel();
        return false;
    }

    // Medics work outside combat; use native medical capacity instead of
    // requiring the fighter's 90% blood reserve.
    // C++: TownArenaRuntimePolicy.h:96-100
    public static bool CanProvideAid(in Readiness r, bool medicallyAble)
    {
        return r.Valid && !r.Player && !r.Dead && !r.Unconscious &&
            !r.InCombat && !r.NeedsAid && medicallyAble;
    }

    // C++: TownArenaRuntimePolicy.h:101-104
    public static bool IsolatePair(bool managedA, bool managedB, bool opponents)
    {
        return !opponents && (managedA || managedB);
    }
}
