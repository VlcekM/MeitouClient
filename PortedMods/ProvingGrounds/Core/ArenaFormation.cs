// Formation maths lifted from upstream/ProvingGrounds/plugin/src/ArenaIngress.cpp (Proving Grounds by Shutori, GPLv3), task M4.3a.
// ArenaIngress.cpp is glue (src/Game/ArenaIngress.cs, task 4.3b); this file holds its pure parts: the constants and marker tables
// (:46-110), the formation offsets, the per-fighter target lists, the staging scale, the Teams 1v1 walk-in target, the escort gather
// points, the Registry use-node fallback and the facing pick. Points are building-local offsets turned into world space with the
// site's position and orientation exactly as the C++ does (pos + ori * local, Ogre 1.9 quaternion maths in Core/Vec3.cs); a Banner
// uses world offsets and ignores its orientation. Pinned by Tests/Golden/formation.json (golden/gen_formation.cpp, verbatim copies).
// Left to the glue (4.3b): which site, whether a fighter or escort is valid / dead / a caged prisoner, the moves, parking and facing.
namespace ProvingGrounds.Core;

using static MatchRules;

public static class ArenaFormation
{
    /// <summary>Where a bout is staged. C++: <c>ArenaIngress::LocationMode</c>, ArenaIngress.h:12-17.</summary>
    public enum LocationMode { Arena = 0, SmallArena = 1, Banner = 2 }

    // C++: ArenaIngress.cpp:46-110 (the constants block). Pit entry relative to arena building (tune in-game if needed).
    static readonly Vec3[] s_arenaMarkers =
    [
        new(-80.0f, 0.0f,  0.0f),
        new(-60.0f, 0.0f,  30.0f),
        new(-60.0f, 0.0f, -30.0f),
        new( 80.0f, 0.0f,  0.0f),
        new( 60.0f, 0.0f,  30.0f),
        new( 60.0f, 0.0f, -30.0f),
    ];
    public const int ArenaMarkerCount = 6;
    // Further back than pit markers so waiters look like spectators.
    static readonly Vec3[] s_arenaBenchA =
    [
        new(-120.0f, 0.0f,  40.0f),
        new(-120.0f, 0.0f,   0.0f),
        new(-120.0f, 0.0f, -40.0f),
    ];
    static readonly Vec3[] s_arenaBenchB =
    [
        new( 120.0f, 0.0f,  40.0f),
        new( 120.0f, 0.0f,   0.0f),
        new( 120.0f, 0.0f, -40.0f),
    ];
    public const int ArenaBenchCount = 3;

    /// <summary>C++ <c>kArenaMarkers</c> (ArenaIngress.cpp:47-54).</summary>
    public static ReadOnlySpan<Vec3> ArenaMarkers => s_arenaMarkers;
    /// <summary>C++ <c>kArenaBenchA</c> (ArenaIngress.cpp:57-61).</summary>
    public static ReadOnlySpan<Vec3> ArenaBenchA => s_arenaBenchA;
    /// <summary>C++ <c>kArenaBenchB</c> (ArenaIngress.cpp:62-66).</summary>
    public static ReadOnlySpan<Vec3> ArenaBenchB => s_arenaBenchB;

    // Compact arena uses the same layout at half scale. Keep this separate
    // from the full arena values so it can be tuned against the placed model.
    public const float SmallArenaScale = 0.5f;
    public const float BannerGatherOffset = 20.0f;
    public const float BannerBenchExtra = 18.0f;
    public const float ArenaLastStandingInnerRadius = 60.0f;
    public const float ArenaLastStandingOuterRadius = 155.0f;
    public const float BannerLastStandingInnerRadius = 45.0f;
    public const float BannerLastStandingOuterRadius = 115.0f;
    public const float ArenaTeamSideOffset = 85.0f;
    public const float ArenaTeamColumnSpacing = 35.0f;
    public const float ArenaTeamRowSpacing = 46.0f;
    public const float BannerTeamSideOffset = 65.0f;
    public const float BannerTeamColumnSpacing = 35.0f;
    public const float BannerTeamRowSpacing = 42.0f;
    public const int TeamRowsPerColumn = 5;
    public const float EscortGatherTowardRegistry = 120.0f;
    public const float EscortGatherFallbackLocalX = -140.0f;
    public const float EscortSpacing = 18.0f;

    // The rest of the block: timings and radii ArenaIngress.cpp's state machine (the glue, 4.3b) reads.
    public const float ArrivalRadius = 60.0f;
    public const float SettleRadius = 120.0f;
    // Fight formations need a tighter gate than walking up to the Registry.
    // Otherwise everyone can still be near the middle when combat is engaged.
    public const float FormationArrivalRadius = 30.0f;
    public const float SettleSpeed = 0.35f;
    public const float SettleHoldSec = 0.8f;
    public const float IngressTimeoutSec = 20.0f;
    public const float NpcFormationTimeoutSec = 30.0f;
    // Extend only while every missing NPC is making measurable progress.
    public const float NpcFormationHardCapSec = 90.0f;
    public const float NpcFormationProgressLogSec = 10.0f;
    public const float NpcFormationStallSec = 20.0f;
    public const float NpcFormationProgressDistance = FormationArrivalRadius;
    public const float FightGatherHoldSec = 4.0f;
    public const float FightCountdownSec = 2.25f;
    // RMB mouse-up cancels orders issued on the same click — wait it out, then keep re-issuing.
    public const float ApproachFirstMoveDelaySec = 0.35f;
    public const float ApproachMoveReissueSec = 0.40f;
    // FCS Instance usenode1024 on Proving Grounds Registry (building-local).
    public static readonly Vec3 RegistryUseNodeLocal = new(-3.0f, 10.0f, 3.0f);
    // Node sits near mesh/board; nudge outward from building center onto walkable pad.
    public const float RegistryUseNodeOutwardNudge = 30.0f;
    // Kenshi world units are huge (banner coords ~50k). "Nearby" visually is thousands of units.
    public const float SiteSearchRadius = 25000.0f;
    public const int SiteSearchMax = 512;

    /// <summary>The player-run full Arena stages its layout at x0.70 (ArenaIngress.cpp:1331; an unnamed literal there).</summary>
    public const float PlayerArenaStagingScale = 0.70f;

    // C++: ArenaIngress.cpp:161-166
    public static float HorizontalDistSq(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    /// <summary>A building-local point in world space: <c>pos + (ori * local)</c>, with the building's position and orientation.</summary>
    // C++: ArenaIngress.cpp:168-173
    public static Vec3 LocalToWorld(Vec3 buildingPosition, Quat buildingOrientation, Vec3 local)
    {
        Vec3 pos = buildingPosition;
        Quat ori = buildingOrientation;
        return pos + (ori * local);
    }

    // C++: ArenaIngress.cpp:235-254
    public static Vec3 ScrambleOffset(int slot, int count, float innerRadius, float outerRadius)
    {
        // A golden-angle sunflower pattern fills the pit without obvious rows
        // or a predictable ring. Keeping the centre empty prevents one fighter
        // becoming the natural first target of everyone around them.
        const float kGoldenAngle = 2.39996323f;
        int safeCount = count > 0 ? count : 1;
        float areaFraction = ((float)slot + 0.5f) / (float)safeCount;
        float innerSq = innerRadius * innerRadius;
        float outerSq = outerRadius * outerRadius;
        float radius = MathF.Sqrt(innerSq + areaFraction * (outerSq - innerSq));
        float angle = ((float)slot * kGoldenAngle) + ((float)safeCount * 0.37f);
        return new Vec3(MathF.Cos(angle) * radius, 0.0f, MathF.Sin(angle) * radius);
    }

    // C++: ArenaIngress.cpp:256-278
    public static Vec3 TeamFormationOffset(
        MatchTeam team,
        int slot,
        int teamCount,
        float sideOffset,
        float columnSpacing,
        float rowSpacing)
    {
        int column = slot / TeamRowsPerColumn;
        int row = slot % TeamRowsPerColumn;
        int firstInColumn = column * TeamRowsPerColumn;
        int remaining = teamCount - firstInColumn;
        int rowsInColumn = remaining < TeamRowsPerColumn
            ? remaining
            : TeamRowsPerColumn;
        float centeredRow = (float)row - ((float)(rowsInColumn - 1) * 0.5f);
        float side = (team == MatchTeam.TeamA) ? -1.0f : 1.0f;
        // Extra columns grow away from the centre. Growing inward made large
        // teams start almost on top of each other.
        float x = side * (sideOffset + (float)column * columnSpacing);
        return new Vec3(x, 0.0f, centeredRow * rowSpacing);
    }

    /// <summary>One target per fighter at an Arena / Small arena: Teams 1v1 = each side's first fighter on its pit marker
    /// (+-80 local), the rest on its bench (+-120, z 40/0/-40, cycling); Last Standing = the sunflower between 60 and 155;
    /// Team AvB = columns of 5 rows. Every offset is multiplied by <paramref name="scale"/> and turned by the building's orientation.
    /// A fighter whose team is not A counts as team B in Teams 1v1 and takes a B slot in AvB (as the C++).</summary>
    /// <remarks>C++: ArenaIngress.cpp:280-361.
    /// <paramref name="teams"/> may be null only in Last Standing or with no fighters (the C++ never reads it then); otherwise
    /// <see cref="ArgumentNullException"/> (the C++ would dereference it).</remarks>
    public static Vec3[] AssignArenaTargets(
        MatchMode mode,
        MatchTeam[]? teams,
        int count,
        Vec3 arenaPosition,
        Quat arenaOrientation,
        float scale = 1.0f)
    {
        RequireTeams(mode, teams, count);
        return ArenaTargets(mode, teams, count, arenaPosition, arenaOrientation, scale);
    }

    /// <summary><see cref="AssignArenaTargets(MatchMode, MatchTeam[], int, Vec3, Quat, float)"/> with one target per entry of
    /// <paramref name="teams"/> (count = its length).</summary>
    public static Vec3[] AssignArenaTargets(
        MatchMode mode,
        ReadOnlySpan<MatchTeam> teams,
        Vec3 arenaPosition,
        Quat arenaOrientation,
        float scale = 1.0f) =>
        ArenaTargets(mode, teams, teams.Length, arenaPosition, arenaOrientation, scale);

    // C++: ArenaIngress.cpp:280-361
    static Vec3[] ArenaTargets(
        MatchMode mode,
        ReadOnlySpan<MatchTeam> teams,
        int count,
        Vec3 arenaPosition,
        Quat arenaOrientation,
        float scale)
    {
        var targets = new Vec3[count];
        const int half = ArenaMarkerCount / 2;

        if (mode == MatchMode.Teams1v1)
        {
            int aSlot = 0;
            int bSlot = 0;
            for (int i = 0; i < count; ++i)
            {
                Vec3 local;
                if (teams[i] == MatchTeam.TeamA)
                {
                    if (aSlot == 0)
                        local = s_arenaMarkers[0] * scale;
                    else
                        local = s_arenaBenchA[(aSlot - 1) % ArenaBenchCount] * scale;
                    ++aSlot;
                }
                else
                {
                    if (bSlot == 0)
                        local = s_arenaMarkers[half] * scale;
                    else
                        local = s_arenaBenchB[(bSlot - 1) % ArenaBenchCount] * scale;
                    ++bSlot;
                }
                targets[i] = LocalToWorld(arenaPosition, arenaOrientation, local);
            }
            return targets;
        }

        if (mode == MatchMode.LastStanding)
        {
            for (int i = 0; i < count; ++i)
            {
                targets[i] =
                    LocalToWorld(
                        arenaPosition,
                        arenaOrientation,
                        ScrambleOffset(
                            i,
                            count,
                            ArenaLastStandingInnerRadius * scale,
                            ArenaLastStandingOuterRadius * scale));
            }
            return targets;
        }

        int teamACount = 0;
        int teamBCount = 0;
        for (int i = 0; i < count; ++i)
        {
            if (teams[i] == MatchTeam.TeamA)
                ++teamACount;
            else if (teams[i] == MatchTeam.TeamB)
                ++teamBCount;
        }

        int teamASlot = 0;
        int teamBSlot = 0;
        for (int i = 0; i < count; ++i)
        {
            bool teamA = teams[i] == MatchTeam.TeamA;
            int slot = teamA ? teamASlot++ : teamBSlot++;
            int teamCount = teamA ? teamACount : teamBCount;
            Vec3 local = TeamFormationOffset(
                teams[i],
                slot,
                teamCount,
                ArenaTeamSideOffset * scale,
                ArenaTeamColumnSpacing * scale,
                ArenaTeamRowSpacing * scale);
            targets[i] = LocalToWorld(arenaPosition, arenaOrientation, local);
        }
        return targets;
    }

    /// <summary>One target per fighter at a Banner: world offsets from the banner's position (its orientation is not used).
    /// Teams 1v1 = first fighters at x -+20, the rest at -+(20+18) with z = 8 per slot; Last Standing = the sunflower between 45 and
    /// 115; Team AvB = columns at 65 + 35 per column, rows 42 apart.</summary>
    /// <remarks>C++: ArenaIngress.cpp:363-429. <paramref name="teams"/> may be null only in Last Standing or with no fighters,
    /// otherwise <see cref="ArgumentNullException"/>: the C++ tests it for Teams 1v1 (<c>&amp;&amp; teams</c>), but a null list then falls
    /// through to the AvB branch, which dereferences it.</remarks>
    public static Vec3[] AssignBannerTargets(
        MatchMode mode,
        MatchTeam[]? teams,
        int count,
        Vec3 bannerPosition)
    {
        RequireTeams(mode, teams, count);
        return BannerTargets(mode, teams, count, bannerPosition);
    }

    /// <summary><see cref="AssignBannerTargets(MatchMode, MatchTeam[], int, Vec3)"/> with one target per entry of
    /// <paramref name="teams"/> (count = its length).</summary>
    public static Vec3[] AssignBannerTargets(MatchMode mode, ReadOnlySpan<MatchTeam> teams, Vec3 bannerPosition) =>
        BannerTargets(mode, teams, teams.Length, bannerPosition);

    // C++: ArenaIngress.cpp:363-429
    static Vec3[] BannerTargets(
        MatchMode mode,
        ReadOnlySpan<MatchTeam> teams,
        int count,
        Vec3 bannerPosition)
    {
        var targets = new Vec3[count];
        Vec3 @base = bannerPosition;

        // the C++ also requires teams != NULL here; a null list never gets this far (RequireTeams)
        if (mode == MatchMode.Teams1v1)
        {
            int aSlot = 0;
            int bSlot = 0;
            for (int i = 0; i < count; ++i)
            {
                bool teamA = (teams[i] == MatchTeam.TeamA);
                float side = teamA ? -1.0f : 1.0f;
                int slot = teamA ? aSlot : bSlot;
                float radius = BannerGatherOffset + (slot > 0 ? BannerBenchExtra : 0.0f);
                targets[i] = @base + new Vec3(side * radius, 0.0f, (float)slot * 8.0f);
                if (teamA)
                    ++aSlot;
                else
                    ++bSlot;
            }
            return targets;
        }

        if (mode == MatchMode.LastStanding)
        {
            for (int i = 0; i < count; ++i)
                targets[i] =
                    @base + ScrambleOffset(
                        i,
                        count,
                        BannerLastStandingInnerRadius,
                        BannerLastStandingOuterRadius);
            return targets;
        }

        int teamACount = 0;
        int teamBCount = 0;
        for (int i = 0; i < count; ++i)
        {
            if (teams[i] == MatchTeam.TeamA)
                ++teamACount;
            else if (teams[i] == MatchTeam.TeamB)
                ++teamBCount;
        }

        int teamASlot = 0;
        int teamBSlot = 0;
        for (int i = 0; i < count; ++i)
        {
            bool teamA = teams[i] == MatchTeam.TeamA;
            int slot = teamA ? teamASlot++ : teamBSlot++;
            int teamCount = teamA ? teamACount : teamBCount;
            targets[i] = @base + TeamFormationOffset(
                teams[i],
                slot,
                teamCount,
                BannerTeamSideOffset,
                BannerTeamColumnSpacing,
                BannerTeamRowSpacing);
        }
        return targets;
    }

    /// <summary>The scale an Arena / Small arena stages its layout at: Small arena x0.5, else x0.70 for the player-run arena (no town
    /// bout running) and x1.0 while the town arena is busy (a town bout). Not used for a Banner. The staging scale is the LAYOUT's,
    /// not the arena's size (ArenaIngress.cpp:1340-1342).</summary>
    // C++: ArenaIngress.cpp:1329-1331 (inside Begin)
    public static float StagingScale(LocationMode location, bool townArenaBusy)
    {
        return location == LocationMode.SmallArena
            ? SmallArenaScale
            : (!townArenaBusy ? PlayerArenaStagingScale : 1.0f);
    }

    /// <summary>Begin's formation: a Banner's targets, or the Arena's at <see cref="StagingScale"/>. <paramref name="townArenaBusy"/>
    /// is the C++ <c>TownArena::IsBusy()</c> at the call. C++: ArenaIngress.cpp:1326-1335 (inside Begin).</summary>
    public static Vec3[] AssignSiteTargets(
        LocationMode location,
        bool townArenaBusy,
        MatchMode mode,
        MatchTeam[]? teams,
        int count,
        Vec3 sitePosition,
        Quat siteOrientation)
    {
        RequireTeams(mode, teams, count);
        return SiteTargets(location, townArenaBusy, mode, teams, count, sitePosition, siteOrientation);
    }

    /// <summary><see cref="AssignSiteTargets(LocationMode, bool, MatchMode, MatchTeam[], int, Vec3, Quat)"/> with one target per
    /// entry of <paramref name="teams"/> (count = its length).</summary>
    public static Vec3[] AssignSiteTargets(
        LocationMode location,
        bool townArenaBusy,
        MatchMode mode,
        ReadOnlySpan<MatchTeam> teams,
        Vec3 sitePosition,
        Quat siteOrientation) =>
        SiteTargets(location, townArenaBusy, mode, teams, teams.Length, sitePosition, siteOrientation);

    // C++: ArenaIngress.cpp:1326-1335 (inside Begin)
    static Vec3[] SiteTargets(
        LocationMode location,
        bool townArenaBusy,
        MatchMode mode,
        ReadOnlySpan<MatchTeam> teams,
        int count,
        Vec3 sitePosition,
        Quat siteOrientation)
    {
        if (location == LocationMode.Banner)
            return BannerTargets(mode, teams, count, sitePosition);
        float arenaScale = StagingScale(location, townArenaBusy);
        return ArenaTargets(
            mode, teams, count, sitePosition, siteOrientation,
            arenaScale);
    }

    /// <summary>The array forms' null rule: the teams list is read unless the mode is Last Standing or there are no fighters.</summary>
    static void RequireTeams(MatchMode mode, MatchTeam[]? teams, int count)
    {
        if (teams == null && count > 0 && mode != MatchMode.LastStanding)
            throw new ArgumentNullException(nameof(teams), $"{mode} reads a team per fighter");
    }

    /// <summary>The Teams 1v1 walk-in target for a bench fighter of <paramref name="team"/> (A or B; BeginWalkIn refuses any other
    /// before this point): Banner = x -+20 from the banner (world); Arena = the side's pit marker (+-80 local), x0.5 at a Small arena,
    /// and NOT scaled by the 0.70 the player arena stages at (unlike the initial formation, as the C++).</summary>
    // C++: ArenaIngress.cpp:1735-1750 (inside BeginWalkIn)
    public static Vec3 WalkInTarget(LocationMode location, MatchTeam team, Vec3 sitePosition, Quat siteOrientation)
    {
        if (location == LocationMode.Banner)
        {
            Vec3 @base = sitePosition;
            float side = (team == MatchTeam.TeamA) ? -1.0f : 1.0f;
            return @base + new Vec3(side * BannerGatherOffset, 0.0f, 0.0f);
        }
        else
        {
            const int half = ArenaMarkerCount / 2;
            Vec3 local = (team == MatchTeam.TeamA)
                ? s_arenaMarkers[0]
                : s_arenaMarkers[half];
            if (location == LocationMode.SmallArena)
                local = local * SmallArenaScale;
            return LocalToWorld(sitePosition, siteOrientation, local);
        }
    }

    /// <summary>Where the escorts (prisoner handlers) gather: 120 from the site toward the bound Registry when there is one more
    /// than 1 unit away on the ground; else a Banner's point 120 along -Z (world), else the site's local (-140, 0, 0).
    /// <paramref name="sitePosition"/> null = no site (the C++ answers the origin); <paramref name="registryPosition"/> = the bound
    /// Registry's position when <c>GetBoundRegistry()</c> is non-null and valid, else null.</summary>
    // C++: ArenaIngress.cpp:549-574
    public static Vec3 EscortGatherBase(LocationMode location, Vec3? sitePosition, Quat siteOrientation, Vec3? registryPosition)
    {
        if (sitePosition is not Vec3 sitePos)
            return new Vec3(0.0f, 0.0f, 0.0f);

        if (registryPosition is Vec3 registry)
        {
            Vec3 dir = registry - sitePos;
            dir.Y = 0.0f;
            float lenSq = (dir.X * dir.X) + (dir.Z * dir.Z);
            if (lenSq > 1.0f)
            {
                float invLen = 1.0f / MathF.Sqrt(lenSq);
                dir.X *= invLen;
                dir.Z *= invLen;
                return sitePos + (dir * EscortGatherTowardRegistry);
            }
        }

        if (location == LocationMode.Banner)
            return sitePos + new Vec3(0.0f, 0.0f, -EscortGatherTowardRegistry);

        return LocalToWorld(sitePos, siteOrientation, new Vec3(EscortGatherFallbackLocalX, 0.0f, 0.0f));
    }

    /// <summary>The offset of escort <paramref name="index"/> (its place in the escort list, skipped escorts included) from the
    /// gather base: three per row, 18 apart, rows 18 behind each other (world axes).</summary>
    // C++: ArenaIngress.cpp:588-593 (inside IssueEscortMoves)
    public static Vec3 EscortOffset(int index)
    {
        int col = index % 3;
        int row = index / 3;
        return new Vec3(
            ((float)col - 1.0f) * EscortSpacing,
            0.0f,
            (float)row * EscortSpacing);
    }

    /// <summary>Escort <paramref name="index"/>'s move target: <c>base + offset</c>. The glue skips null / invalid escorts (:585-586)
    /// without closing the gap.</summary>
    // C++: ArenaIngress.cpp:594 (inside IssueEscortMoves)
    public static Vec3 EscortMoveTarget(Vec3 gatherBase, int index) => gatherBase + EscortOffset(index);

    /// <summary>The point a character walks to when using a Registry or Leaderboard. <paramref name="positionMarker"/> =
    /// <c>building->getPositionMarker(from->getPosition())</c>, asked only when the walking character is non-null and valid (null
    /// otherwise): it wins unless it sits within 1 unit of the building's origin (3D). Else the use node (-3, 10, 3) local, nudged 30
    /// outward on the ground from the building's centre when that direction is longer than 1.</summary>
    // C++: ArenaIngress.cpp:483-511
    public static Vec3 RegistryApproachPoint(Vec3 buildingPosition, Quat buildingOrientation, Vec3? positionMarker)
    {
        if (positionMarker is Vec3 marker)
        {
            Vec3 center0 = buildingPosition;
            Vec3 delta = marker - center0;
            float lenSq0 = (delta.X * delta.X) + (delta.Y * delta.Y) + (delta.Z * delta.Z);
            // Marker valid when it isn't stuck at the building origin.
            if (lenSq0 > 1.0f)
                return marker;
        }

        Vec3 world = LocalToWorld(buildingPosition, buildingOrientation, RegistryUseNodeLocal);
        Vec3 center = buildingPosition;
        Vec3 dir = world - center;
        dir.Y = 0.0f;
        float lenSq = (dir.X * dir.X) + (dir.Z * dir.Z);
        if (lenSq > 1.0f)
        {
            // dir /= sqrtf(lenSq): Ogre's Vector3::operator/= multiplies by fInv = 1.0f / fScalar (OgreVector3.h:326-336)
            float fInv = 1.0f / MathF.Sqrt(lenSq);
            dir.X *= fInv;
            dir.Y *= fInv;
            dir.Z *= fInv;
            world.X += dir.X * RegistryUseNodeOutwardNudge;
            world.Z += dir.Z * RegistryUseNodeOutwardNudge;
        }
        return world;
    }

    /// <summary>C++ FighterAtMark's test: within <paramref name="radius"/> of the mark on the ground.</summary>
    // C++: ArenaIngress.cpp:625-626 (inside FighterAtMark, :618-627)
    public static bool AtMark(Vec3 position, Vec3 target, float radius)
    {
        float radiusSq = radius * radius;
        return HorizontalDistSq(position, target) <= radiusSq;
    }

    /// <summary>The point fighter <paramref name="index"/> faces on arrival: the nearest (on the ground) assigned mark of an opponent
    /// (any other fighter in Last Standing), at the fighter's own height. Null = no face: a teams list not as long as the targets, an
    /// index past them, no opponent, or the nearest mark within 1 unit. The fighter's own validity and death are the glue's checks
    /// (:633-635), and so is the index against the fighter list.</summary>
    // C++: ArenaIngress.cpp:629-658 (FaceOpposingFormation)
    public static Vec3? OpposingFacePoint(
        MatchMode mode,
        ReadOnlySpan<MatchTeam> teams,
        ReadOnlySpan<Vec3> targets,
        int index,
        Vec3 fighterPosition)
    {
        if (index < 0 || index >= targets.Length || teams.Length != targets.Length)
            return null;

        // Use the opponent's assigned mark even before they arrive; the marks
        // already account for the arena's orientation in world space.
        int closest = targets.Length;
        float distance = 1.0e30f;
        for (int other = 0; other < targets.Length; ++other)
        {
            if (other == index ||
                (mode != MatchMode.LastStanding && teams[other] == teams[index]))
                continue;
            float candidate = HorizontalDistSq(fighterPosition, targets[other]);
            if (candidate < distance)
            {
                distance = candidate;
                closest = other;
            }
        }
        if (closest == targets.Length || distance <= 1.0f)
            return null;
        Vec3 face = targets[closest];
        face.Y = fighterPosition.Y;
        return face;
    }
}
