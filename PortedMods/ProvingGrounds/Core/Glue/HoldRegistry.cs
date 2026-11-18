namespace ProvingGrounds.Core.Glue;

// Port only (task 8.4a, controller decision; docs/divergences.md "Hold registry (task 8.4a)"): the one owner-counted hold list of a world. The C++
// takes an NPC's AI over through the order-receiver reservation tables behind its checkOrders hook (TownAftercare.cpp:29-33, :97-115); Meitou has no
// such hook, and the port holds an NPC with spike S10's recipe instead (CharacterCold.Engaged = true, Ai.Walking = false, TaskStart.Stand once;
// re-asserted every SlowWorld because saves, loads, conversations and trades clear it, CAP 19; given back with Engaged = false + Ai.Replan = true).
// M7 had three separate Engaged lists, each releasing unconditionally; M8 hands the same NPC from owner to owner (a fighter: bout, then patient, then
// limb-shop visitor), so the hold is given back only when the LAST owner lets go. The engine half is src/Engine/Holds.cs (PgEngine HoldStill /
// ReassertHold / ReleaseHold); M4's Park keeps its own list (it skips NPCs this registry holds). Tests: Tests/Glue/HoldRegistryTests.cs.

/// <summary>Who holds an NPC (one bit per owner; an NPC is held while any bit is set).</summary>
[Flags]
public enum HoldOwner
{
    None = 0,
    /// <summary>TownArena's bout fighter (task 7.3's borrow list; ArenaIngress.cpp:437 clearAllAIGoals for a town fighter).</summary>
    TownFighter = 1,
    /// <summary>TownAftercare's medic (TownAftercare.cpp:172-185 Direct: <c>reservations.Add</c>).</summary>
    AftercareMedic = 2,
    /// <summary>TownAftercare's patient (TownAftercare.cpp:277-280 DirectPatient: <c>patientReservations.Add</c>; task 8.4b).</summary>
    AftercarePatient = 4,
    /// <summary>SparSession's Teams 1v1 bench (TownAftercare.cpp:1052-1059 ReserveBenchOrders).</summary>
    Bench = 8,
    /// <summary>The external table (TownAftercare.cpp:1065-1074 ReserveExternalOrders: the announcer, the limb shop, the corpse cleanup).</summary>
    External = 16,
    /// <summary>TownSpectators' guest (task 8.2).</summary>
    Spectator = 32,
    /// <summary>TownAnnouncer's deferred release (task 8.3 quality review): a speaker unsafe to resume is kept held until TickReleases gives it its
    /// saved orders back (the C++ does not rethink it before then).</summary>
    AnnouncerPending = 64,
}

/// <summary>The engine half of the hold (src/Engine/Holds.cs over PgEngine; a fake in the tests). Serial steps only.</summary>
public interface IHoldEngine<TId>
{
    /// <summary>Spike S10's first hold (Engaged, Ai.Walking off, TaskStart.Stand once): false for a stale id (nothing held).</summary>
    bool Hold(TId id);
    /// <summary>Engaged again when someone cleared it: false for a stale id.</summary>
    bool Reassert(TId id);
    /// <summary>Engaged off, Ai.Replan on (the NPC walks back to its routine).</summary>
    void Release(TId id);
}

/// <summary>The NPCs one world holds, each with the owners that hold it, in hold order. One object per world: a new world starts empty, and
/// <see cref="Clear"/> forgets everything without touching an actor (Engaged is not saved, CAP 19).</summary>
public sealed class HoldRegistry<TId> where TId : notnull
{
    struct Entry
    {
        public TId Id;
        public HoldOwner Owners;
    }

    readonly List<Entry> entries = [];
    readonly IEqualityComparer<TId> eq = EqualityComparer<TId>.Default;

    /// <summary>Called once per world for the first exception a release or a re-assert throws (it never reaches the caller).</summary>
    public Action<string>? FailureLog;
    bool failureLogged;

    int IndexOf(TId id)
    {
        for (int i = 0; i < entries.Count; i++) if (eq.Equals(entries[i].Id, id)) return i;
        return -1;
    }

    /// <summary>The number of held NPCs.</summary>
    public int Count => entries.Count;

    /// <summary>The held NPC at <paramref name="index"/> (hold order) and its owners (tests, the test verb).</summary>
    public (TId Id, HoldOwner Owners) At(int index) => (entries[index].Id, entries[index].Owners);

    /// <summary>The owners holding <paramref name="id"/> (<see cref="HoldOwner.None"/>: not held).</summary>
    public HoldOwner OwnersOf(TId id)
    {
        int i = IndexOf(id);
        return i < 0 ? HoldOwner.None : entries[i].Owners;
    }

    /// <summary>Whether anyone holds <paramref name="id"/>.</summary>
    public bool IsHeld(TId id) => IndexOf(id) >= 0;

    /// <summary>Whether <paramref name="owner"/> holds <paramref name="id"/>.</summary>
    public bool IsHeldBy(TId id, HoldOwner owner) => (OwnersOf(id) & owner) != 0;

    /// <summary><paramref name="owner"/> holds <paramref name="id"/>: the first owner takes it by spike S10's recipe (<see cref="IHoldEngine{TId}.Hold"/>),
    /// a later owner joins and re-asserts Engaged. Holding twice by the same owner changes nothing but the re-assert. False when the id is stale (a
    /// held entry is dropped then, without a release: the NPC is gone) or the hold / re-assert threw (logged once; a held entry is dropped too).</summary>
    public bool Hold(IHoldEngine<TId> engine, TId id, HoldOwner owner)
    {
        int i = IndexOf(id);
        if (i >= 0)
        {
            bool alive = false;
            try { alive = engine.Reassert(id); } catch (Exception ex) { Failed("re-assert", id, ex); }
            if (!alive) { entries.RemoveAt(i); return false; }   // stale (gone) or the re-assert threw: nothing held, nothing to give back
            var e = entries[i];
            e.Owners |= owner;
            entries[i] = e;
            return true;
        }
        bool held = false;
        try { held = engine.Hold(id); } catch (Exception ex) { Failed("hold", id, ex); }
        if (!held) return false;
        entries.Add(new Entry { Id = id, Owners = owner });
        return true;
    }

    /// <summary><paramref name="owner"/> lets go of <paramref name="id"/>; the NPC is given back (<see cref="IHoldEngine{TId}.Release"/>) only when
    /// no owner is left. Nothing for an NPC this owner does not hold. The entry is dropped before the engine release, so a release that throws
    /// never leaves an ownerless entry the next re-assert would hold again.</summary>
    public void Release(IHoldEngine<TId> engine, TId id, HoldOwner owner)
    {
        int i = IndexOf(id);
        if (i < 0 || (entries[i].Owners & owner) == 0) return;
        var e = entries[i];
        e.Owners &= ~owner;
        if (e.Owners != HoldOwner.None) { entries[i] = e; return; }
        entries.RemoveAt(i);
        try { engine.Release(id); } catch (Exception ex) { Failed("release", id, ex); }
    }

    /// <summary><paramref name="owner"/> lets go of every NPC it holds (newest hold first, so a removal never skips an entry), each as
    /// <see cref="Release"/>.</summary>
    public void ReleaseAll(IHoldEngine<TId> engine, HoldOwner owner)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if ((entries[i].Owners & owner) == 0) continue;
            var e = entries[i];
            e.Owners &= ~owner;
            if (e.Owners != HoldOwner.None) { entries[i] = e; continue; }
            entries.RemoveAt(i);
            try { engine.Release(e.Id); } catch (Exception ex) { Failed("release", e.Id, ex); }
        }
    }

    /// <summary>Spike S10's re-assert of every held NPC (once per SlowWorld; newest first). A stale id (the NPC is gone: dead and removed, unloaded) is dropped:
    /// there is nothing to give back.</summary>
    public void ReassertAll(IHoldEngine<TId> engine)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            TId id = entries[i].Id;
            bool alive = true;
            try { alive = engine.Reassert(id); } catch (Exception ex) { Failed("re-assert", id, ex); }
            if (!alive) entries.RemoveAt(i);
        }
    }

    /// <summary>The world changed: forget every hold without an actor access (the old world's people are gone with it).</summary>
    public void Clear()
    {
        entries.Clear();
        failureLogged = false;   // the next world logs its own first failure
    }

    /// <summary>A step that threw: logged once per world, never rethrown (one NPC never keeps the others held or released).</summary>
    void Failed(string what, TId id, Exception e)
    {
        if (failureLogged) return;
        failureLogged = true;
        FailureLog?.Invoke($"hold registry {what} of {id} failed (logged once per world): {e.GetType().Name}: {e.Message}");
    }
}
