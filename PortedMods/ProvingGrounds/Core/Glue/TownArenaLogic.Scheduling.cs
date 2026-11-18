namespace ProvingGrounds.Core.Glue;

// Task 7.1 (TownArena.cpp:368-395, 1296-1317, 1352-1359, 1860-2066; TW §2.1): when Scratch is active (a finished Town Registry within 2,500 of a
// player character standing in the registry's town, and some player character in that town), the stop rules (no player character left in the
// registry's town) and the 5 s schedule clock. The bout half of Tick (planning, the restored bout, beginning, the event phases) is task 7.3:
// TownArenaLogic.Bouts.cs, called here in the C++ order.

public sealed partial class TownArenaLogic<TId>
{
    /// <summary>C++ <c>getObjectsWithinSphere(..., 2500, BUILDING, 512, NULL)</c> (TownArena.cpp:1305): the radius ...</summary>
    public const float RegistrySearchRadius = 2500;
    /// <summary>... and the cap.</summary>
    public const int RegistrySearchMax = 512;
    /// <summary>C++ <c>now - g_lastScheduleCheck &lt; 5000</c> (TownArena.cpp:1892, :1945, :2049): the schedule clock's period in ms.</summary>
    public const uint ScheduleIntervalMs = 5000;

    // C++: TownArena.cpp:368-380
    bool DiscoverTownRegistry(ITownHost<TId> h)
    {
        if (!h.HasWorld || h.PlayerCharacters() is not { } players) return false;
        for (int i = 0; i < players.Count; ++i)
        {
            TId player = players[i];
            if (!h.IsNull(player) && h.Valid(player) && h.CurrentTown(player) != TownKey.None &&
                BindRegistryNear(h, player))
                return true;
        }
        return false;
    }

    /// <summary>C++: TownArena.cpp:382-395. <paramref name="registry"/> null = the C++ NULL building (an invalid hand).</summary>
    public bool HasPlayerInRegistryTown(ITownHost<TId> h, ArenaBuildingRef? registry) =>
        registry is { } r && h.HasWorld && HasPlayerInTown(h, h.Resolve(r));

    /// <summary>The body of <see cref="HasPlayerInRegistryTown"/> for a registry already resolved now (null = gone).</summary>
    bool HasPlayerInTown(ITownHost<TId> h, PlacedBuilding? registry)
    {
        if (!h.HasWorld || h.PlayerCharacters() is not { } players || registry is not { } b || b.Town == TownKey.None)
            return false;
        for (int i = 0; i < players.Count; ++i)
        {
            TId player = players[i];
            if (!h.IsNull(player) && h.Valid(player) &&
                TownKey.Same(h.CurrentTown(player), b.Town))
                return true;
        }
        return false;
    }

    /// <summary>C++: TownArena.cpp:1296-1300 (TownArenaUI binds the registry it was opened from, task 7.6).</summary>
    public void BindRegistry(ArenaBuilding? registry)
    {
        if (ArenaIdentityLogic.IsTownRegistry(registry) && ArenaIdentityLogic.IsRegistryFinished(registry))
            s.TownRegistry = registry!.Value.Ref;
    }

    // C++: TownArena.cpp:1301-1317
    public bool BindRegistryNear(ITownHost<TId> h, TId character)
    {
        if (!h.HasWorld || h.IsNull(character) || !h.Valid(character) || h.CurrentTown(character) == TownKey.None) return false;
        var nearby = h.BuildingsInSphere(h.Position(character), RegistrySearchRadius, RegistrySearchMax);
        if (nearby is null) return false;
        for (int i = 0; i < nearby.Count; ++i)
        {
            if (!ArenaIdentityLogic.IsTownRegistry(nearby[i].Building)) continue;
            PlacedBuilding registry = nearby[i];
            if (TownKey.Same(registry.Town, h.CurrentTown(character)) && ArenaIdentityLogic.IsRegistryFinished(registry.Building))
            {
                BindRegistry(registry.Building);
                return true;
            }
        }
        return false;
    }

    // C++: TownArena.cpp:1352-1359
    public void SetScheduleEnabled(ITownHost<TId> h, bool enabled)
    {
        s.SchedulePaused = !enabled;
        if (!enabled) CancelPlannedNpcBout(h, TownArena.Text.SchedulingPaused);
        s.LastScheduleCheck = 0;
        Status(h, TownArena.ScheduleMessage(enabled));
    }

    /// <summary>C++: TownArena.cpp:1860-2066. Task 7.1 owns the stop rules (:1864-1881), the per-tick calls of the other subsystems in their order
    /// (:1883-1888, M8 / task 7.5 members), the idle branch's 5 s clock, the registry discovery and the player-in-town check (:1942-1961); the
    /// restored bout (:1890-1939), the announced-bout check (:1940-1941), planning and beginning (:1963-1999) and the event phases (:2002-2066) are
    /// task 7.3's members (TownArenaLogic.Bouts.cs), called here where the C++ has them.</summary>
    public void Tick(ITownHost<TId> h)
    {
        if (!h.HasWorld) return;

        // Scratch's actors may unload after the last player character leaves
        // town. Stop owning them before any arena subsystem ticks again.
        if (IsBusy() && !HasPlayerInRegistryTown(h, s.ActiveRegistry))
        {
            if (s.Phase == TownPhase.Preparing && h.IngressPending()) h.IngressCancel();
            if (OwnsMatch(h)) AbortOwnedMatch(h);
            Finish(h, TownArena.Text.EventStopped);
            return;
        }

        // An announced ambient bout must not survive after Scratch becomes
        // unobserved. Paid player bookings remain queued for the squad's return.
        if (s.PlannedNpc.Valid && !s.RestoredNpcPending &&
            !HasPlayerInRegistryTown(h, s.PlannedRegistry))
        {
            CancelPlannedNpcBout(h, TownArena.Text.ScheduledNoPlayers);
            return;
        }

        if (h.LimbShopIsActive()) h.LimbShopTick();
        PayChallengeCredit(h);
        h.SpectatorsTick();
        h.AnnouncerTickReleases();
        h.CorpseCleanupTick();
        h.AftercareTickBedRecovery();
        uint now = h.NowMs;
        // port only (task 7.2, census): the roster settle clock (TownArenaLogic.Matchmaking.cs), before the restored-bout wait, which returns from
        // Tick until its fighters are ready: 7.3's restore waits for the roster, so the clock must not wait for the restore
        ObserveRoster(h, now);
        if (s.RestoredNpcPending && !TickRestoredNpc(h, now)) return;   // :1890-1939 (task 7.3): false = the C++ returns
        if (s.PlannedNpc.Valid && !h.IsPaused && !PlannedNpcReady(h))
            CancelPlannedNpcBout(h, TownArena.Text.AnnouncedUnavailable);
        if (!IsBusy())
        {
            if (h.IsPaused || (s.SchedulePaused && !s.BookingPending)) return;
            if (s.LastScheduleCheck != 0 && now - s.LastScheduleCheck < ScheduleIntervalMs) return;
            s.LastScheduleCheck = now;
            s.ScheduleChecks++;   // port only (pg town)

            ArenaBuildingRef? ambientRegistry = s.TownRegistry;
            PlacedBuilding? ambient = ambientRegistry is { } held ? h.Resolve(held) : null;
            bool registryReady = ambient is { } a && ArenaIdentityLogic.IsRegistryFinished(a.Building);
            if (!registryReady) s.TownRegistry = null;
            if (TownArenaPolicy.ShouldDiscoverRegistry(
                s.SchedulePaused, registryReady, s.BookingPending))
            {
                DiscoverTownRegistry(h);
                ambientRegistry = s.TownRegistry;
                ambient = ambientRegistry is { } found ? h.Resolve(found) : null;
                registryReady = ambient is { } b && ArenaIdentityLogic.IsRegistryFinished(b.Building);
            }

            if (!HasPlayerInRegistryTown(h, ambientRegistry)) return;

            // Port only (WorldLifecycle.cs, done in task 7.1; docs/divergences.md "TownArena"): while a save the sim prepared waits to be captured, no
            // bout is planned or begun, so nothing restarts town activity between PrepareForManualSave's cancel and the capture.
            if (h.SaveInFlight) return;

            if (!s.BookingPending && registryReady && !h.ArenaOperationBusy())
            {
                PlanNpcBout(h);
                if (!s.PlannedNpc.Valid) return;
                ambientRegistry = s.PlannedRegistry;
            }
            BeginNextEvent(h, registryReady, ambientRegistry);   // :1968-1999 (task 7.3)
            return;
        }
        TickEvent(h, now);   // :2002-2066 (task 7.3)
    }
}
