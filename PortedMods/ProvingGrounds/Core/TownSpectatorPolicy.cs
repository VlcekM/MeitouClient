// Port of upstream/ProvingGrounds/plugin/src/TownSpectatorPolicy.h (Proving Grounds by Shutori, GPLv3).
namespace ProvingGrounds.Core;

/// <summary>C++: TownSpectatorPolicy.h (header-only, namespace <c>TownSpectatorPolicy</c>): who may sit in the town arena's audience,
/// how roles are picked round-robin, when a seat may be reserved, what happens on release and which arena a seat belongs to
/// (TW §2.7). The character ids are PgRecords.ScratchCivilian / ScratchBarResident / CageHandler, written as literals like the C++.</summary>
public static class TownSpectatorPolicy
{
    /// <summary>C++: TownSpectatorPolicy.h:5-10</summary>
    public enum AudienceRole
    {
        NotAudience = -1,
        Civilian = 0,
        BarResident = 1,
        CageHandler = 2
    }

    /// <summary>C++: TownSpectatorPolicy.h:11-15</summary>
    public enum ReleaseDisposition
    {
        ForgetRelease,
        DeferNativeResume,
        ResumeNativeNow
    }

    /// <summary><c>strcmp</c> on the id: ordinal, case-sensitive; null = not audience.</summary>
    // C++: TownSpectatorPolicy.h:17-25
    public static AudienceRole RoleFor(string? characterId)
    {
        if (characterId == null) return AudienceRole.NotAudience;
        if (string.Equals(characterId, "103-Proving Grounds.mod", StringComparison.Ordinal)) return AudienceRole.Civilian;
        // Character 258 is supplied by Scratch Bar Residents squad 257.
        if (string.Equals(characterId, "258-Proving Grounds.mod", StringComparison.Ordinal)) return AudienceRole.BarResident;
        // Character 99 is supplied by Scratch Cage Handlers squad 108.
        if (string.Equals(characterId, "99-Proving Grounds.mod", StringComparison.Ordinal)) return AudienceRole.CageHandler;
        return AudienceRole.NotAudience;
    }

    // C++: TownSpectatorPolicy.h:26-29
    public static bool CanWatch(AudienceRole role, bool player, bool permanentJob, bool healthy)
    {
        return role != AudienceRole.NotAudience && !player && healthy &&
            (!permanentJob || role == AudienceRole.BarResident || role == AudienceRole.CageHandler);
    }

    // C++: TownSpectatorPolicy.h:30-32
    public static bool CanReserve(bool free, bool reserved, int audience, int target)
    {
        return free && !reserved && audience >= 0 && target > 0 && target <= 120 && audience < target;
    }

    /// <summary>The first role with someone left, starting at <paramref name="cursor"/> (an out-of-range cursor starts at Civilian).</summary>
    // C++: TownSpectatorPolicy.h:33-41
    public static AudienceRole NextRole(int civilians, int residents, int handlers, AudienceRole cursor)
    {
        ReadOnlySpan<int> remaining = stackalloc int[] { civilians, residents, handlers };
        int start = cursor >= AudienceRole.Civilian && cursor <= AudienceRole.CageHandler ? (int)cursor : 0;
        for (int offset = 0; offset < 3; ++offset)
        {
            int role = (start + offset) % 3;
            if (remaining[role] > 0) return (AudienceRole)role;
        }
        return AudienceRole.NotAudience;
    }

    // C++: TownSpectatorPolicy.h:42-45
    public static ReleaseDisposition ReleaseFor(bool valid, bool dead, bool player, bool healthy)
    {
        if (!valid || dead || player) return ReleaseDisposition.ForgetRelease;
        return healthy ? ReleaseDisposition.ResumeNativeNow : ReleaseDisposition.DeferNativeResume;
    }

    /// <summary>Doubles, like the C++ (250000 = 500 units squared).</summary>
    // C++: TownSpectatorPolicy.h:46-48
    public static bool BelongsToArena(bool sameTown, double distanceSquared, double otherArenaDistanceSquared)
    {
        return sameTown && distanceSquared >= 0 && distanceSquared <= 250000 && distanceSquared < otherArenaDistanceSquared;
    }
}
