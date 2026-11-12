using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Beds, cages, sentences and building locks in a save (docs/formats/save.md "Locks, cages and sentences"; the key facts in docs/game/locks.md "Save
/// keys" and economy.md 11.5). Character keys of GAMESTATE_CHARACTER: <c>in something</c> (int, <see cref="HeldState"/>), <c>in what</c> (a building
/// handle), <c>sentence</c> (int, the sentence's start in game hours, day x 24 + hour, truncated) and <c>psts</c> (float, its length in hours);
/// Meitou's own <c>meitou in something</c> / <c>meitou in what</c> (the <see cref="Fixture.Id"/>) carry what the original's handle cannot name (an
/// interior cage, a bed). Building locks are read from and written into the zones' existing GAMESTATE_BUILDING records only.
/// </summary>
public static class HeldSave
{
    /// <summary>
    /// Meitou's GAMESTATE_CHARACTER keys of the held state and the fixture id.
    /// </summary>
    public const string MeitouState = "meitou in something", MeitouWhat = "meitou in what";

    /// <summary>
    /// The zones' building state records by <see cref="Meitou.Data.World.BuildingKey"/> (the building lists' instances and their
    /// GAMESTATE_BUILDING state).
    /// </summary>
    public static Dictionary<string, FcsRecord> BuildingStates(SaveGame save)
    {
        var map = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            var states = new Dictionary<string, FcsRecord>(StringComparer.Ordinal);
            foreach (var r in zone.BuildingStates) states[r.StringId] = r;
            foreach (var i in list.Instances)
            {
                // The building state; an inventory state can come first (docs/formats/save.md "Zone files").
                foreach (var id in i.States)
                {
                    if (states.TryGetValue(id, out var state))
                    {
                        map[Data.World.BuildingKey.Of(zone.Coordinate, i.Id)] = state;
                        break;
                    }
                }
            }
        }

        return map;
    }

    /// <summary>
    /// The record and position of every placement in the zones' building lists, by <see cref="Meitou.Data.World.BuildingKey"/>: an <c>in what</c>
    /// naming a generated interior building (its save id is not mapped) is matched by them to Meitou's interior fixture
    /// (<see cref="FixtureRegistry.SavedSpots"/>).
    /// </summary>
    public static Dictionary<string, SavedSpot> BuildingSpots(SaveGame save)
    {
        var map = new Dictionary<string, SavedSpot>(StringComparer.Ordinal);
        foreach (var zone in save.Zones.Values)
        {
            if (zone.BuildingList is not { } list) continue;

            foreach (var i in list.Instances)
                if (i.Target.Length > 0) map[Data.World.BuildingKey.Of(zone.Coordinate, i.Id)] = new SavedSpot(i.Target, i.Position);
        }

        return map;
    }

    /// <summary>
    /// Reads a character's held state and sentence (the Meitou keys first; else <c>in what</c> resolved through <paramref name="handles"/> to a
    /// building key).
    /// </summary>
    public static void Read(FcsRecord? state, CharacterCold cold, SaveHandles handles, IReadOnlyDictionary<FcsRecord, string> instanceOf)
    {
        if (state is null) return;

        if (state.Strings.TryGetValue(MeitouWhat, out var what) && what.Length > 0)
        {
            cold.InSomething = (HeldState)Math.Clamp(state.Ints.GetValueOrDefault(MeitouState), 0, 2);
            cold.InWhat = what;
        }
        else
        {
            cold.InSomething = (HeldState)Math.Clamp(state.Ints.GetValueOrDefault("in something"), 0, 2);
            cold.InWhat = cold.InSomething != HeldState.None && handles.Building(Hand.Read(state, "in what")) is { } b
                ? instanceOf.GetValueOrDefault(b)
                : null;
        }

        if (cold.InSomething == HeldState.None) cold.InWhat = null;

        cold.SentenceStart = state.Ints.GetValueOrDefault("sentence");
        cold.SentenceLength = state.Floats.GetValueOrDefault("psts");
    }

    /// <summary>
    /// Writes them back. The original's keys are left as read while the held state and fixture did not change since loading
    /// (<paramref name="loaded"/>); otherwise <c>in what</c> is the handle of the fixture's building when the fixture is the building's own (a placed
    /// cage or standalone bed) and the save has its state, else the null handle with <c>in something</c> 0 (the original would hold a prisoner
    /// with no prison; Meitou's keys keep the truth).
    /// </summary>
    public static void Write(
        FcsRecord state, CharacterCold cold, SavedCharacterLink? loaded, FixtureRegistry fixtures, IReadOnlyDictionary<string, FcsRecord> buildings)
    {
        if (cold.SentenceLength > 0 || state.Floats.ContainsKey("psts"))
        {
            state.Ints["sentence"] = (int)cold.SentenceStart;
            state.Floats["psts"] = cold.SentenceLength;
        }

        if (loaded is not null && loaded.InSomething == cold.InSomething && loaded.InWhat == cold.InWhat) return;

        if (cold.InSomething != HeldState.None && cold.InWhat is { } what)
        {
            state.Ints[MeitouState] = (int)cold.InSomething;
            state.Strings[MeitouWhat] = what;
        }
        else
        {
            state.Ints.Remove(MeitouState);
            state.Strings.Remove(MeitouWhat);
        }

        var hand = Hand.Null;
        if (cold.InSomething != HeldState.None && cold.InWhat is { } id && fixtures.Find(id) is { } f
            && (f.Id == f.Building || f is { Kind: FixtureKind.Bed, Indoors: false })
            && buildings.TryGetValue(f.Building, out var b) && Hand.IsComplete(b, "handle"))
            hand = Hand.Read(b, "handle");

        state.Ints["in something"] = hand.IsNull ? 0 : (int)cold.InSomething;
        hand.Write(state, "in what");
    }
}
