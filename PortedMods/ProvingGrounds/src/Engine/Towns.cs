using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

/// <summary>
/// Which town a point or building is in, and Scratch: written once here and used by every town task (the original's
/// <c>building-&gt;getTown() == character-&gt;getCurrentTownLocation()</c> test, TownArena.cpp:368-395 and :1301-1317, TW §2.1; CAP 6).
/// One object per world, owned by <see cref="ProvingGroundsSystem"/>. Sim thread, serial steps only.
/// <para>Town data read at world build is too early (SaveLoader.Load applies town overrides after the AddSystem factory; owner's recipe
/// "Learned on the QuestLog town plan"), so nothing is read in the constructor. <see cref="Refresh"/> runs at the start of each SlowWorld:
/// it resolves Scratch on the first call and again whenever a site's <c>Replacement</c> or <c>world.Fixtures.RuinsRevision</c> changed, and
/// rewrites the town circles (radius = the current TOWN's, <c>TownSite.Town.Radius</c>; the player towns) in place, allocation-free unless
/// the town count changes.</para>
/// <para>A character's town is <see cref="CurrentTownOf"/> (the original's status town, task 7.2 review); a point's is <see cref="TownAt"/>.</para>
/// <para>The rule for readers: <see cref="TownAt"/>, <see cref="CurrentTownOf"/> and <see cref="InScratch"/> answer from the circles of the last <see cref="Refresh"/>,
/// and refresh themselves first when none has run yet in this world (a command in a world's first Inputs, before its first SlowWorld).
/// <see cref="Scratch"/> and <see cref="IsScratch"/> are null / false until then.</para>
/// </summary>
sealed class Towns(PopulationData data)
{
    TownCircle[] circles = [];
    int[] playerNumbers = [];
    TownData?[] replacements = [];
    long ruinsRevision = long.MinValue;
    bool resolved;
    TownSite? scratch;

    /// <summary>
    /// Scratch's site: the placed town whose own record (<c>TownSite.Record</c>, the placement's TOWN, PopulationData.cs:167) is
    /// <see cref="PgRecords.ScratchTown"/> (TOWN 90 of the data mod), else the first whose shown name is "Scratch" (as the task 0.6 census found
    /// it, src/Town/Census.cs). Found by its OWN record, not the current one (<c>TownSite.Town</c>, which a town override replaces, :169-189), so
    /// Scratch replaced by an override (ruined, taken over) still reads as Scratch here; task 7.1 ("Scratch active") decides what a replaced
    /// Scratch means (its <c>Replacement</c> / <c>Town.Type</c>). Null before the first <see cref="Refresh"/> or when the world has none.
    /// </summary>
    internal TownSite? Scratch => scratch;

    /// <summary>Serial, once per tick before anything asks: rewrite the circles; re-resolve Scratch on the first call and after a town change.</summary>
    internal void Refresh(World world)
    {
        var sites = data.Sites;
        var players = world.Buildings.Towns;
        int count = sites.Count + players.Count;
        if (circles.Length != count) circles = new TownCircle[count];
        bool playerTownsChanged = false;
        if (playerNumbers.Length != players.Count) { playerNumbers = new int[players.Count]; playerTownsChanged = true; }
        if (replacements.Length != sites.Count) { replacements = new TownData?[sites.Count]; resolved = false; }

        bool changed = !resolved || world.Fixtures.RuinsRevision != ruinsRevision || playerTownsChanged;   // (the building-town cache: task 7.2 review)
        for (int i = 0; i < sites.Count; i++)
        {
            var s = sites[i];
            if (!ReferenceEquals(replacements[i], s.Replacement)) { replacements[i] = s.Replacement; changed = true; }
            var kind = s.Town.Type switch { TownType.NestMarker => TownKind.NestMarker, TownType.Null => TownKind.Null, _ => TownKind.Placed };
            circles[i] = new TownCircle(s.Position.X, s.Position.Z, s.Town.Radius, kind);
        }
        for (int i = 0; i < players.Count; i++)
        {
            circles[sites.Count + i] = new TownCircle(players[i].Position.X, players[i].Position.Z, players[i].Radius, TownKind.Player);
            if (playerNumbers[i] != players[i].Number) { playerNumbers[i] = players[i].Number; changed = true; }   // a player town's buildings
        }

        if (!changed) return;
        buildingTowns.Clear();   // a ruin or a replaced town can change a building's town
        ruinsRevision = world.Fixtures.RuinsRevision;
        resolved = true;
        var was = scratch;
        scratch = sites.FirstOrDefault(s => s.Record.Id == PgRecords.ScratchTown) ?? sites.FirstOrDefault(s => s.Town.Name == "Scratch");
        if (!ReferenceEquals(was, scratch) || was is null)
            ProvingGroundsMod.Ctx?.Log(scratch is null ? "towns: no Scratch in this world"
                : $"towns: Scratch is site {scratch.Index} ({scratch.Town.Name}, {scratch.Town.Type}, radius {CMath.FormatG(scratch.Town.Radius)})");
    }

    /// <summary>
    /// The town a POINT is in: <see cref="TownRule.CurrentTown"/> (Meitou AiSystem.TownAt, src/Meitou.Simulation/Ai/AiSystem.Packages.cs:66-92,
    /// internal, re-implemented here over the public site and player-town lists; the original's FUN_1402688d0, the AI blackboard's town: 1 x radius,
    /// no hold, no building step). It is NOT the original's <c>getCurrentTownLocation()</c> of a character: that is <see cref="CurrentTownOf"/>
    /// (task 7.2 review). Used for points, and as that rule's last fallback. <see cref="CampaignTown.None"/> outside every town. Refreshes first if
    /// no Refresh ran yet.
    /// </summary>
    internal CampaignTown TownAt(World world, Vector3 p)
    {
        if (!resolved) Refresh(world);
        return ToTown(TownRule.CurrentTown(p.X, p.Z, circles));
    }

    /// <summary>
    /// C++ <c>Character::getCurrentTownLocation()</c> (vfunc 0x78 = FUN_1405c89b0: the status object's current town <c>+0x88</c>, written by
    /// FUN_1405a51e0 with a hold: entered at 1 x a town's radius, left only past 1.5 x; Meitou docs/game/economy.md "DA_ARREST_TARGET",
    /// docs/game/carry-loot.md "The status town"), by <see cref="TownRule.CharacterTown{T}"/>'s order:
    /// <list type="number">
    /// <item><c>CharacterCold.StatusTown</c> (public, CharacterTable.cs:1038-1042): Meitou's copy, kept by ArrestSystem.UpdateStatusTowns on its
    /// spotting pass (Crime/ArrestSystem.cs:410-416, :442-449) with AiSystem.StatusTownAt (steps 1, 2 and 4); none at load until that first update;</item>
    /// <item>when it is none: AiSystem.StatusTownAt(position, current = the port's memo) (internal, Members.AiStatusTownAt; steps 1, 2, 4: with no
    /// memo what Meitou's first update would write, with one step 2's 1.5 x hold of it); without the member, the memo while within 1.5 x its radius,
    /// else the radius rule <see cref="TownAt"/> (one log line);</item>
    /// <item>when that is none too: the town of the building the character stands in (step 5, which Meitou does not model): <c>world.Sight.BuildingOf
    /// (slot)</c> (Perception/SightMap.cs:603-605, the building whose indoor volume holds it) -&gt; that building's town (<see cref="PgEngine.PgBuilding.Town"/>,
    /// CAP 6), a town building first, then a player one.</item>
    /// </list>
    /// The answer becomes the memo (the original writes every answer to <c>+0x88</c>); a town Meitou's StatusTown gives drops it.
    /// <see cref="CampaignTown.None"/> for a stale id. docs/divergences.md "Matchmaking snapshot and search worker".
    /// </summary>
    internal CampaignTown CurrentTownOf(World world, CharacterId id)
    {
        if (!resolved) Refresh(world);
        if (id.IsNone || !world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } c) return CampaignTown.None;
        var position = world.Characters.Next[slot].Position;
        // TownRule.CharacterTown's order (unit-tested in Tests/Glue/TownRuleTests.cs), written out here without its closures (a per-tick path).
        // The two copies are kept in step by hand: change both.
        var status = c.StatusTown;
        if (!status.IsNone) { memo.Set(id, CampaignTown.None, true); return status; }
        var town = StatusTownAt(world, position, memo.Get(id));
        if (town.IsNone) town = BuildingTown(world, slot);
        memo.Set(id, town, town.IsNone);
        return town;
    }

    /// <summary>The port's stand-in for the original's <c>+0x88</c> while Meitou's StatusTown is none (<see cref="TownRule.CharacterTown{T}"/>):
    /// per character, this world only (a new world builds a new Towns).</summary>
    readonly StatusTownMemo<CharacterId, CampaignTown> memo = new(CampaignTown.None);

    /// <summary>Port only (the test-only pg town roster): what the character's town was read from: Meitou's StatusTown, the port's memo, the
    /// building it stands in (key, null outdoors).</summary>
    internal (CampaignTown Status, CampaignTown Memo, string? Building) Explain(World world, CharacterId id)
    {
        if (id.IsNone || !world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } c)
            return (CampaignTown.None, CampaignTown.None, null);
        return (c.StatusTown, memo.Get(id), world.Sight.BuildingOf(slot));
    }

    /// <summary>Port only: the memo's size (pg town roster).</summary>
    internal int MemoCount => memo.Count;

    // a Func, not a nested delegate type: PgCheck engine JIT-binds every method of the adapter's types (a delegate's BeginInvoke cannot bind)
    static Func<PopulationData, World, Vector3, CampaignTown, CampaignTown>? statusTownAt;
    static bool statusTownMissing;

    /// <summary>AiSystem.StatusTownAt(data, world, p, current) through its delegate (steps 1, 2, 4 with that current town); when the member is
    /// missing (Part.StatusTown): the current town while <see cref="TownRule.Holds"/> (1.5 x its radius), else the radius rule.</summary>
    CampaignTown StatusTownAt(World world, Vector3 p, CampaignTown current)
    {
        if (statusTownAt is null && !statusTownMissing)
        {
            try
            {
                if (Members.AiStatusTownAt is { } m) statusTownAt = m.CreateDelegate<Func<PopulationData, World, Vector3, CampaignTown, CampaignTown>>();
                else StatusTownMissing("not found");
            }
            catch (Exception e) { StatusTownMissing($"{e.GetType().Name}: {e.Message}"); }
        }
        if (statusTownAt is { } f) return f(data, world, p, current);
        return CircleOf(current) is { } held && TownRule.Holds(p.X, p.Z, held) ? current : TownAt(world, p);
    }

    /// <summary>The circle of a town (a placed site, a player town), null for none or one not in the list.</summary>
    TownCircle? CircleOf(CampaignTown town)
    {
        if (town.IsNone) return null;
        int sites = data.Sites.Count;
        if (town.Site >= 0) return town.Site < sites && town.Site < circles.Length ? circles[town.Site] : null;
        for (int i = 0; i < playerNumbers.Length; i++)
            if (playerNumbers[i] == town.PlayerTown && sites + i < circles.Length) return circles[sites + i];
        return null;
    }

    static void StatusTownMissing(string why)
    {
        statusTownMissing = true;
        ProvingGroundsMod.Ctx?.Log($"Part StatusTown off: AiSystem.StatusTownAt {why}; a character not updated yet is placed by the town radius");
    }

    readonly Dictionary<string, CampaignTown> buildingTowns = new(StringComparer.Ordinal);   // building key -> its town, cleared on a town change

    /// <summary>Step 5: the town of the building whose indoor volume holds the character (none outdoors or in a building of no town).</summary>
    CampaignTown BuildingTown(World world, int slot)
    {
        if (world.Sight.BuildingOf(slot) is not { } key) return CampaignTown.None;
        if (buildingTowns.TryGetValue(key, out var town)) return town;
        var hint = (-1, -1);
        var b = PgEngine.FindBuilding(world, data, PgEngine.BuildingSource.Town, key, ref hint)
            ?? PgEngine.FindBuilding(world, data, PgEngine.BuildingSource.Player, key, ref hint);
        town = b?.Town ?? CampaignTown.None;
        buildingTowns[key] = town;
        return town;
    }

    /// <summary>A circle index back to the town: sites first (<c>CampaignTown.OfSite(site.Index)</c>, as AiSystem.Packages.cs:84), then the
    /// player towns by their own <c>PlayerTown.Number</c> (BuildingRegistry.cs:205-236).</summary>
    CampaignTown ToTown(int i)
    {
        var sites = data.Sites;
        if (i < 0 || i >= circles.Length) return CampaignTown.None;
        return i < sites.Count ? CampaignTown.OfSite(sites[i].Index) : CampaignTown.OfPlayer(playerNumbers[i - sites.Count]);
    }

    /// <summary>The original's same-town test: both name the same town and neither is "no town" (<see cref="TownRule.SameTown"/>).</summary>
    internal static bool SameTown(CampaignTown a, CampaignTown b) => !a.IsNone && a == b;

    /// <summary>A town as Core's <see cref="TownKey"/> (task 7.1, TownArenaLogic): a placed site's index, a player town's number negated,
    /// <see cref="TownKey.None"/> for none. Meitou builds only three shapes (CampaignTown.None, OfSite(i &gt;= 0), OfPlayer(n &gt; 0),
    /// PopulationCampaigns.cs:23-35); for those, two towns get the same key exactly when they are equal (<see cref="SameTown(CampaignTown,
    /// CampaignTown)"/>). A hand-made value with both a site and a player town would map to its site.</summary>
    internal static long Key(CampaignTown town) => town.IsNone ? TownKey.None : town.Site >= 0 ? town.Site : -(long)town.PlayerTown;

    /// <summary>Whether <paramref name="town"/> is Scratch (false before the first Refresh).</summary>
    internal bool IsScratch(CampaignTown town) => scratch is not null && town.Site == scratch.Index && town.PlayerTown == 0;

    /// <summary>Whether a character is in Scratch (its <see cref="CurrentTownOf"/> is Scratch).</summary>
    internal bool InScratch(World world, CharacterId id) => IsScratch(CurrentTownOf(world, id));

    /// <summary>The town a building belongs to, the C++'s <c>Building::getTown()</c>: <see cref="PgEngine.PgBuilding.Town"/>.</summary>
    internal static CampaignTown TownOf(PgEngine.PgBuilding building) => building.Town;

    /// <summary>The C++ test "building's town == character's current town" (TownArena.cpp:1311, :391) for a building and a character.</summary>
    internal bool SameTown(World world, PgEngine.PgBuilding building, CharacterId character) =>
        SameTown(building.Town, CurrentTownOf(world, character));

    /// <summary>A town's shown name: the site's current TOWN name, a player town's own name; null for none. A site's index is its position in
    /// <c>PopulationData.Sites</c> (built as <c>new TownSite(sites.Count, ...)</c>, PopulationData.cs:737; Meitou's own PopulationData.TownAt
    /// returns the list position as the site, :407-418), so it is indexed directly.</summary>
    internal string? NameOf(World world, CampaignTown town)
    {
        var sites = data.Sites;
        if (town.Site >= 0) return town.Site < sites.Count ? sites[town.Site].Town.Name : null;
        return town.PlayerTown > 0 ? world.Buildings.FindTown(town.PlayerTown)?.Name : null;
    }
}
