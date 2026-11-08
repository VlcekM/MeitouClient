using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Ai;

public sealed partial class AiSystem
{
    /// <summary>
    /// State 141 <c>strangers in house P</c> (evaluator <c>FUN_14060e640</c>, docs/game/ai-tasks.md "Stranger states"): false when the home is public (the original tests the target's building,
    /// which for goal 153 is the home bed's house; the planner gives a finder row's list B no target, so a spare bed in another building is not tested); else true when
    /// <see cref="StrangerInHome"/> finds someone. Goal 153 Going to bed needs it false.
    /// </summary>
    bool StrangersInHouseP(World world, Blackboard board, int slot)
    {
        if (board.OwnHome is not { } home) return false;   // no home: the finder returns 0 (and with nothing to test the building counts as public)
        // The player's Open to public toggle, else the residents' template (the original writes its `public day` over the home's flag), else public.
        if (world.Fixtures.PublicOf(home.Key) ?? board.HomePublic) return false;
        return StrangerInHome(world, home, slot);
    }

    /// <summary>State 108 <c>strangers in house</c> (evaluator <c>FUN_14060e5e0</c>, <b>Verified</b>): true when the stranger finder returns above 0, with no public test; false with no home. Goals 155, 197, 158, 283 and 239 read it.</summary>
    bool StrangersInHouse(World world, Blackboard board, int slot) => board.OwnHome is { } home && StrangerInHome(world, home, slot);

    struct StrangerScan
    {
        public World World;
        public FactionRelations Relations;
        public CharacterTable Table;
        public CharacterCold Me;
        public string Home;
        public int Self, Faction, Player;
        public float Outdoor;
        public bool Found;
    }

    /// <summary>
    /// The stranger finder (<c>FUN_1408ced70</c>, docs/game/ai-tasks.md "Stranger states"): a character inside the resident's home that is up, not caged and not a chained slave of the
    /// resident's faction, and either tagged neutral to it (neither an ally nor an enemy) or, for a resident that is not the player's, one of the player's. A sneaker counts only
    /// while the resident senses it (<see cref="SightMap.Sees"/>). Last tick's state, so any worker may ask.
    /// </summary>
    bool StrangerInHome(World world, TownBuilding home, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } me) return false;
        // Prefilter by the grid on the farthest spot of the interior (as LockSystem.Extent); the exact test is the building the sight map puts a character in.
        float reach = 30;
        foreach (var s in home.Spots) reach = MathF.Max(reach, Vector2.Distance(Flat(s.Position), Flat(home.Position)) + 10);
        var scan = new StrangerScan
        {
            World = world, Relations = Relations, Table = table, Me = me, Home = home.Key, Self = slot, Faction = me.Faction,
            Player = me.Faction == world.Player.Faction ? -1 : world.Player.Faction, Outdoor = AggroSystem.RangeOf(me, AggroSystem.OutdoorSight),
        };
        world.Grid.Query(home.Position.X, home.Position.Z, reach * 1.5f, ref scan, static (int other, ref StrangerScan st) =>
        {
            if (st.Found || other == st.Self) return;
            var prev = st.Table.Previous;
            if ((uint)other >= (uint)prev.Length || !prev[other].Alive || st.Table.Cold(other) is not { } c || st.World.Sight.BuildingOf(other) != st.Home) return;
            if (c.Medical is { Incapacitated: true } || !c.CarriedBy.IsNone || c.IsImprisoned || FirstAid.FirstAidSystem.OwnedChained(st.Me, c)) return;
            // The sense tag NEUTRAL; the tag mask also leaves out the law enforcement and military classes (bit 7) and the dead (bit 4).
            bool neutral = c.NpcClass is not (FundamentalType.LawEnforcement or FundamentalType.Military) && st.Relations.Contains(c.Faction)
                && !st.Relations.IsAlly(st.Faction, c.Faction) && !AggroSystem.IsEnemy(st.Relations, st.Faction, c.Faction);
            if (!neutral && !(st.Player >= 0 && c.IsPlayer)) return;
            // A sneaker (not in a fight) counts only when sensed: the resident's sight test stands in for its sense entry.
            if ((c.Orders & CharacterOrders.Sneak) != 0 && !c.InCombat
                && st.World.Sight.Sees(st.Self, other, prev[st.Self].Position, prev[other].Position, st.Outdoor, Orders.Stealth(c)) <= 0) return;
            st.Found = true;
        });
        return scan.Found;
    }
}
