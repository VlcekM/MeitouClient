using Meitou.Data.Gameplay;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The squad-intact test (<c>FUN_14079acf0</c>, docs/game/perception.md "Fleeing", <b>Verified</b>), shared by the retreat package
/// (<see cref="AiSystem"/>) and the flee trigger (<see cref="FleeSystem"/>).
/// </summary>
static class SquadIntact
{
    /// <summary>
    /// From this many members the able share decides, below it the leader.
    /// </summary>
    public const int MinForRatio = 4;

    /// <summary>
    /// The share of the squad's size that must be able (<c>DAT_1416885d8</c>), and with the leader down (<c>DAT_141696c90</c>).
    /// </summary>
    public const float Ratio = 0.2f, LeaderDownRatio = 0.4f;

    /// <summary>
    /// A down member still counts as able below this organic first aid need: 0.8 for a persistent platoon with a home (<c>DAT_141693ea8</c>), else
    /// 0.2 (<c>DAT_14168a4a8</c>).
    /// </summary>
    public const float AbleNeedHome = 0.8f, AbleNeed = 0.2f;

    /// <summary>
    /// A member that counts as able (<c>FUN_14079acf0</c>'s loop, <b>Verified</b>): not dead (<c>FUN_1406215b0</c>, medical <c>+0x164</c>), and
    /// either not down (<see cref="CharacterFacts.Down"/>) or down with its organic first aid need (Character <c>+0x504</c> = medical <c>+0xac</c>,
    /// <see cref="Bodies.MedicalState.FirstAidNeeds"/>) under the threshold, so a knocked-out but barely hurt member still counts.
    /// </summary>
    public static bool Able(World world, CharacterCold c, bool persistentHome)
    {
        if (c.Medical is not { } med) return true;
        if (med.Dead) return false;
        return !CharacterFacts.Down(c) || DownIsAble(Need(world, c, med), persistentHome);
    }

    /// <summary>
    /// A down member counts as able while its organic first aid need is strictly under the threshold (<c>need &lt; t</c>: the original's <c>need
    /// &lt;= t &amp;&amp; need != t</c>).
    /// </summary>
    public static bool DownIsAble(float need, bool persistentHome) => need < (persistentHome ? AbleNeedHome : AbleNeed);

    /// <summary>
    /// A member's organic first aid need (<see cref="Bodies.MedicalState.FirstAidNeeds"/>), 0 without a race.
    /// </summary>
    static float Need(World world, CharacterCold c, MedicalState med)
    {
        if (c.Race is not { } race) return 0;

        float capacity = MathF.Max(MedicalState.BloodCapacity(race, c.Stats?.Strength ?? 50, med.HasStatsObject, med.ScaleK, med.ScaleZ), 1);
        float extraLoss = world.SystemOf<BodySystem>()?.Constants.ExtraBloodLossFromBodyparts ?? 1;
        return med.FirstAidNeeds(race, capacity, extraLoss).Organic;
    }

    /// <summary>
    /// A member in a cage or restraint, or carried by a character not allied with it (<c>FUN_14079ab40</c>, <b>Verified</b>): the squad stays intact,
    /// so it neither retreats nor breaks. A carrier that no longer resolves is ignored.
    /// </summary>
    public static bool Held(CharacterTable table, Squad squad, FactionRelations relations)
    {
        foreach (var m in squad.Members)
        {
            if (!table.TryResolveNext(m, out int s) || table.Cold(s) is not { } c) continue;
            if (c.IsImprisoned) return true;
            if (!c.CarriedBy.IsNone && table.TryResolveNext(c.CarriedBy, out int k) && table.Cold(k) is { } carrier && carrier.Faction != c.Faction
                && !(relations.Contains(c.Faction) && relations.Contains(carrier.Faction) && relations.IsAlly(c.Faction, carrier.Faction)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The leader's down test (<c>FUN_1405c9e10</c>, docs/game/ui-input.md): gone, or <see cref="CharacterFacts.Down"/> (out cold, dead, carried); a
    /// crawling leader is up.
    /// </summary>
    public static bool Down(CharacterTable table, CharacterId id) =>
        !table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c || CharacterFacts.Down(c);

    /// <summary>
    /// The engine's reading of the platoon's persistent flag plus a home holder: a town squad that has its own building. The one test every caller of
    /// <see cref="Decide"/> for a loaded squad passes as <c>persistentHome</c> (the AI's intact flag and <see cref="FleeSystem"/>).
    /// </summary>
    public static bool PersistentHome(Squad squad) => squad.Town >= 0 && squad.Ai?.OwnHome is not null;

    /// <summary>
    /// The verdict once no member is <see cref="Held"/>: a squad without a leader or with no able member is not intact; under
    /// <see cref="MinForRatio"/> members a persistent squad with a home is intact while anyone is able, any other while its leader is up; from that
    /// many, while the able are at least <see cref="Ratio"/> of the size (<see cref="LeaderDownRatio"/> with the leader down).
    /// <paramref name="able"/> counts <see cref="Able"/> members.
    /// </summary>
    public static bool Decide(int able, int size, bool hasLeader, bool leaderDown, bool persistentHome)
    {
        if (!hasLeader || able == 0) return false;
        if (size < MinForRatio) return persistentHome ? able > 0 : !leaderDown;
        return able >= size * (leaderDown ? LeaderDownRatio : Ratio);
    }
}