using System.Numerics;
using Meitou.Data.Save;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Saving;

/// <summary>The in-game time a save holds: day, hour and minute (the CAMERA keys <c>time day</c>, <c>time hour</c>, <c>time minute</c>). The host sets its clock from it and reads it back when saving.</summary>
public readonly record struct SaveClock(int Day, int Hour, int Minute)
{
    /// <summary>Hours into the game's day, as a fraction: 13:30 is 13.5.</summary>
    public double HourOfDay => Hour + Minute / 60.0;
}

/// <summary>
/// What a save holds about one faction that the world does not keep itself: its prosperity, its platoon name counter and the whole relation table with trust.
/// (The relation values themselves are put into <see cref="Meitou.Data.Gameplay.FactionRelations"/> when loading, and read from it when saving.)
/// </summary>
public sealed class FactionState
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public float Prosperity { get; set; }
    public int PlatoonCounter { get; set; }
    public bool IsPlayer { get; init; }

    /// <summary>The faction's saved table: relation, trust and trustNeg per faction id. Empty for the player's faction.</summary>
    public Dictionary<string, SaveRelation> Relations { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The link of a world character to the save it was loaded from (<see cref="CharacterCold.Save"/>): the character's records, the stats and medical state read
/// from them, and the rotation it had, which is written back unchanged while the character has not turned.
/// </summary>
public sealed class SavedCharacterLink
{
    public required SaveCharacter Source { get; init; }
    /// <summary>The platoon's name (<c>The Holy Nation_3</c>) and the character's slot in it: together they find the character in a copy of the save.</summary>
    public required string PlatoonName { get; init; }
    public required int Slot { get; init; }
    /// <summary>The quaternion the save had, and the yaw it stood for.</summary>
    public Quaternion Rotation { get; init; } = Quaternion.Identity;
    public float Yaw { get; init; }
    /// <summary>Read from the character's STATS and MEDICAL_STATE records; null when the race or the data could not be resolved. The host's body systems take these over.</summary>
    public CharacterStats? Stats { get; set; }
    public MedicalState? Medical { get; set; }
    /// <summary>The bed or cage state and fixture read from the save (<see cref="HeldSave"/>): while the character still has them, the original's keys are written back as read.</summary>
    public Locks.HeldState InSomething { get; set; }
    public string? InWhat { get; set; }
    /// <summary>The whole-body ragdoll the save holds (<see cref="RagdollSave"/>), until the ragdoll system takes it; null: none.</summary>
    public SavedRagdoll? Ragdoll { get; set; }
    /// <summary>The partner of a carry the save holds (the <c>carrying</c> handle) and whether this character is the carrier (the <c>carrying</c> bool), until <see cref="Carrying.CarrySystem"/> links them.</summary>
    public Hand CarryPartner { get; set; } = Hand.Null;
    public bool CarryIsCarrier { get; set; }
    /// <summary>The owner character a slave's save names (the <c>slaver</c> handle, <see cref="SlaveSave"/>), until <see cref="Kidnap.CaptiveSystem"/> finds it in the world.</summary>
    public Hand Slaver { get; set; } = Hand.Null;
    /// <summary>The save's <c>slaver</c> handle no longer names the owner (the slave was freed or handed to another master): it is written as null.</summary>
    public bool SlaverStale { get; set; }

    /// <summary>Forgets the save's owner character: the slave was freed or handed on.</summary>
    public void DropSlaver()
    {
        Slaver = Hand.Null;
        SlaverStale = true;
    }
}

/// <summary>A player platoon of the save that is now a <see cref="Squad"/> in the world.</summary>
public sealed record LoadedPlatoon(SavePlatoon Platoon, Squad Squad);

/// <summary>What <see cref="SaveLoader.Load"/> made, and what it kept for <see cref="SaveCapture"/> to write back.</summary>
public sealed class LoadedSave
{
    public required SaveGame Source { get; init; }
    public SaveClock Clock { get; init; }
    public List<FactionState> Factions { get; } = [];
    public List<LoadedPlatoon> PlayerPlatoons { get; } = [];
    /// <summary>Every character spawned from the save, in platoon order.</summary>
    public List<CharacterId> Characters { get; } = [];
    /// <summary>The platoon name and slot of every character that was spawned, kept so that one that has left the world since can be told from one that was never made (a dead one).</summary>
    public HashSet<(string Platoon, int Slot)> SpawnedSlots { get; } = [];
    /// <summary>The relation table of the data right after loading (faction by faction, row by row): a value that still equals it was not changed in play, and the save keeps its own.</summary>
    public float[] RelationBaseline { get; set; } = [];
    /// <summary>Each faction's rank right after loading, kept as <see cref="RelationBaseline"/> is.</summary>
    public int[] RankBaseline { get; set; } = [];
    /// <summary>Each faction's global trust right after loading, and the war flags of every pair (faction by faction, row by row), kept as <see cref="RelationBaseline"/> is.</summary>
    public float[] TrustBaseline { get; set; } = [];
    public bool[] WarBaseline { get; set; } = [];
    /// <summary>The travelling stand-ins made from the save's NPC platoons: the world's platoon id to the save's platoon name.</summary>
    public Dictionary<int, string> RoamingPlatoons { get; } = [];
    /// <summary>NPC platoons that stay in their placed town (residents, bar squads, slaves): pinned stand-ins, made when the town loads, which then makes none of its own.</summary>
    public int TownBoundPlatoons { get; set; }
    /// <summary>Wild NPC platoons (a nest's or homeless): stand-ins that stay where they are, made when their zone loads, counted by their nest or sector.</summary>
    public int WildPlatoons { get; set; }
    /// <summary>The CAMERA <c>mods</c> entries the running load order lacks (the save was made with mods this game does not load).</summary>
    public List<string> MissingMods { get; } = [];
    /// <summary>What was left out or changed, one line each (a record that could not be resolved, characters that are dead, platoons without a town...).</summary>
    public List<string> Notes { get; } = [];

    public FactionState? FactionOf(string factionId) => Factions.FirstOrDefault(f => f.Id == factionId);
}

/// <summary>Rotations of characters: the save's quaternion and the simulation's yaw (0 along +Z, turning towards +X).</summary>
public static class SaveRotation
{
    public static float YawOf(Quaternion q)
    {
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        return MathF.Atan2(forward.X, forward.Z);
    }

    public static Quaternion OfYaw(float yaw) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

    /// <summary>The smaller angle between two yaws.</summary>
    public static float Difference(float a, float b)
    {
        float d = (a - b) % MathF.Tau;
        if (d > MathF.PI) d -= MathF.Tau;
        if (d < -MathF.PI) d += MathF.Tau;
        return d;
    }
}