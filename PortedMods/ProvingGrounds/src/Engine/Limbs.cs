using System.Globalization;
using System.Text;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;

namespace ProvingGrounds.Engine;

/// <summary>Task 8.5 (TW §2.10, CAP 13): a fighter's stumps and the robotic limb fitted on one, the game's own recipe (m8-research.md "8.5";
/// CharacterAssembly.cs:350-363 FitRolledLimbs, QuickMoves.cs:126-136). Meitou 0.17.3 (fa34a1b).
/// <para>The stumps: <c>MedicalState.LimbPart(slot).Limb == LimbState.Stump</c> (MedicalState.Save.cs:86, HealthPart.cs:98; slots 0 left arm,
/// 1 right arm, 2 left leg, 3 right leg). The limb record: <c>PopulationData.Items.Limb(record)</c> (ItemFactory.cs:39; null unless it is a
/// LIMB_REPLACEMENT). The item: <c>ItemFactory.Create(record, 1, quality)</c> (ItemFactory.cs:117; a limb gets <c>Level = (int)quality</c>, :175),
/// then <c>Level = quality</c> (the C++'s gear level) BEFORE <c>MedicalState.FitReplacement(limb, item)</c> (MedicalState.Hits.cs:311-326: it reads
/// the level at fit time; 0 damage / wear / stun), then the item into its limb section by a direct list add
/// (<c>(Section, X, Y) = (LimbReplacement.SlotOf(slot), 0, 0); inventory.Items.Add(item)</c>; never <c>Inventory.Add</c>, which puts it in the
/// main grid), and after every stump of the order one <c>CharacterAssembly.RefreshEquipment</c> (<see cref="Refresh"/>, by reflection). A stump whose
/// section already holds an item is refused (QuickMoves.cs:131). Never <c>world.Characters.LimbsChanged</c> for a fit (CAP 13 trap: the population
/// drops the race's severed limb on the ground, PopulationSystem.cs:266-274).</para>
/// <para>The members are compiled in against 0.17.3 and listed in Members.cs part <c>Limbs</c>; without one <see cref="Available"/> is false (one
/// log line) and the limb shop queues nobody. Each engine call sits in its own method, so a member a newer game moved fails there
/// (MissingMethodException), never in the caller.</para></summary>
static class Limbs
{
    /// <summary>The fit's members resolve (part Limbs, plus the equipment refresh of part Rewards); read once per process, a missing one logged once.</summary>
    internal static bool Available => available ??= Resolve();
    static bool? available;

    static bool Resolve()
    {
        var missing = Members.Missing(Part.Limbs).ToList();
        if (Members.CharacterAssemblyNew is null) missing.Add("CharacterAssembly(PopulationData)");
        if (Members.RefreshEquipment is null) missing.Add("CharacterAssembly.RefreshEquipment(CharacterCold)");
        if (missing.Count == 0) return true;
        ProvingGroundsMod.Ctx?.Log($"limb shop off: {string.Join(", ", missing)} not found; no fighter is queued for a robotic limb");
        return false;
    }

    static CharacterCold? Cold(World world, CharacterId id) =>
        world.Characters.TryResolveNext(id, out int slot) ? world.Characters.Cold(slot) : null;

    /// <summary>C++ <c>MissingLimbs</c> (TownLimbShop.cpp:112-120): bit i set when limb slot i is a stump. 0 for a stale id, no medical state or
    /// a missing member.</summary>
    internal static uint StumpMask(World world, CharacterId id)
    {
        if (!Available || Cold(world, id)?.Medical is not { } medical) return 0;
        return Stumps(medical);
    }

    static uint Stumps(MedicalState medical)
    {
        uint mask = 0;
        for (int i = 0; i < 4; i++)
            if (medical.LimbPart(i) is { Limb: LimbState.Stump }) mask |= 1u << i;
        return mask;
    }

    /// <summary>C++ <c>getLimbState(kLimbs[slot]) == LIMB_REPLACED</c> (TownLimbShop.cpp:338).</summary>
    internal static bool Replaced(World world, CharacterId id, int slot) =>
        Available && Cold(world, id)?.Medical is { } medical && IsReplaced(medical, slot);

    static bool IsReplaced(MedicalState medical, int slot) => medical.LimbPart(slot) is { Limb: LimbState.Replaced };

    /// <summary>C++ <c>ou-&gt;gamedata.getData(kLimbData[i])</c> + <c>data-&gt;type == LIMB_REPLACEMENT</c> (TownLimbShop.cpp:303-304): the record is
    /// a LIMB_REPLACEMENT of that limb slot.</summary>
    internal static bool RecordReady(PopulationData data, string record, int slot) => Available && LimbOf(data, record, slot) is not null;

    static LimbReplacement? LimbOf(PopulationData data, string record, int slot) =>
        data.Items.Limb(record) is { } limb && limb.LimbSlot == slot ? limb : null;

    /// <summary>C++ <c>createItem(data, hand(), NULL, NULL, quality, NULL)</c> + <c>getClassType() == LIMB_REPLACEMENT</c> + <c>level_0_100 =
    /// quality</c> (TownLimbShop.cpp:314-330): the item, in no inventory. Null when it could not be made.</summary>
    internal static ItemInstance? Create(PopulationData data, string record, int quality) => Available ? Make(data, record, quality) : null;

    static ItemInstance? Make(PopulationData data, string record, int quality)
    {
        if (data.Items.Create(record, 1, quality) is not { Type: FcsRecordType.LimbReplacement } item) return null;
        item.Level = quality;
        return item;
    }

    /// <summary>C++ <c>medical-&gt;setRobotLimbItem(kLimbs[slot], item, false)</c> (TownLimbShop.cpp:334): fitted on the stump at the item's level
    /// and put into the limb section. False (nothing done) when the slot is not a stump, its section already holds an item, the record is not
    /// that slot's limb or the anatomy has no such limb. Serial step.</summary>
    internal static bool Fit(World world, PopulationData data, CharacterId id, int slot, ItemInstance item)
    {
        if (!Available || Cold(world, id) is not { Medical: { } medical, Inventory: { } inventory }) return false;
        return Attach(data, medical, inventory, slot, item);
    }

    static bool Attach(PopulationData data, MedicalState medical, Inventory inventory, int slot, ItemInstance item)
    {
        if (!Fittable(medical, inventory, slot)) return false;
        if (LimbOf(data, item.Record, slot) is not { } limb) return false;
        if (!medical.FitReplacement(limb, item)) return false;
        (item.Section, item.X, item.Y) = (LimbReplacement.SlotOf(slot), 0, 0);
        inventory.Items.Add(item);
        return true;
    }

    /// <summary>Port only (the C++ <c>setRobotLimbItem</c> refreshes the character itself): the fit's pre-check, read before any item is made:
    /// the slot is a stump, the anatomy has that limb, and its limb section holds no item (QuickMoves.cs:131). A slot this refuses would be refused
    /// by <see cref="Fit"/> too.</summary>
    internal static bool CanFit(World world, CharacterId id, int slot)
    {
        if (!Available || Cold(world, id) is not { Medical: { } medical, Inventory: { } inventory }) return false;
        return Fittable(medical, inventory, slot);
    }

    static bool Fittable(MedicalState medical, Inventory inventory, int slot)
    {
        if (medical.LimbPart(slot) is not { Limb: LimbState.Stump }) return false;
        string section = LimbReplacement.SlotOf(slot);
        var items = inventory.Items;
        for (int i = 0; i < items.Count; i++) if (items[i].Section == section) return false;
        return true;
    }

    /// <summary>CharacterAssembly.RefreshEquipment(cold) (Members.cs; CharacterAssembly.cs:207-222) through a delegate bound once per PopulationData
    /// to its own assembly, gated only on the two members (not on the Rewards part's state). Never throws: false when it did not run (stale id,
    /// a member missing, or it threw: <paramref name="error"/> then names the exception).</summary>
    internal static bool Refresh(World world, PopulationData data, CharacterId id, out string? error)
    {
        error = null;
        if (Members.CharacterAssemblyNew is not { } ctor || Members.RefreshEquipment is not { } method) { error = "CharacterAssembly members missing"; return false; }
        if (Cold(world, id) is not { } cold) { error = "no such character"; return false; }
        try
        {
            if (refresh is null || !ReferenceEquals(refreshData, data))
            {
                refresh = method.CreateDelegate<Action<CharacterCold>>(ctor.Invoke([data]));
                refreshData = data;
            }
            refresh(cold);
            return true;
        }
        catch (Exception e)
        {
            error = (e is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : e) is var x ? $"{x.GetType().Name}: {x.Message}" : "";
            return false;
        }
    }
    static Action<CharacterCold>? refresh;
    static PopulationData? refreshData;

    /// <summary>TEST ONLY (pg town sever): limb slot <paramref name="slot"/> cut off by <c>MedicalState.Sever</c> (MedicalState.Hits.cs:230-238)
    /// with the context DebugCommands builds (DebugCommands.cs:848-859), then <c>world.Characters.LimbsChanged</c> as the game does after a sever
    /// (DebugCommands.cs:385: the population drops the severed limb item and rebuilds the kit). An arm's stump does not knock a standing character
    /// down (a leg's may, on the next collapse check). False when the slot is not an original limb or the character is stale. Serial step.</summary>
    internal static bool Sever(World world, PopulationData data, CharacterId id, int slot)
    {
        if (!Available || slot is < 0 or > 3 || !world.Characters.TryResolveNext(id, out int s)
            || world.Characters.Cold(s) is not { Medical: { } medical, Race: { } race } cold) return false;
        return Cut(world, data, s, cold, medical, race, slot);
    }

    static bool Cut(World world, PopulationData data, int s, CharacterCold cold, MedicalState medical, RaceData race, int slot)
    {
        int part = -1;
        for (int i = 0; i < medical.Parts.Count; i++)
            if (medical.Parts[i].Template.LimbSlot == slot) { part = i; break; }
        if (part < 0 || medical.Parts[part].Limb != LimbState.Original) return false;
        var ctx = new MedicalContext(data.Bodies.Constants, data.Bodies.Options, race)
        {
            Toughness = cold.Stats?.ScaledToughness ?? 0,
            Strength = cold.Stats?.Strength ?? 50,
            HasStats = cold.Stats is not null,
            Seed = world.Seed,
            CharacterKey = Rng.Key(world.Characters.IdOf(s)),
        };
        medical.Sever(part, ctx);
        if (!AfterSever(world, s, slot)) throw new InvalidOperationException("severed, but the after-sever step (CharacterTable.LimbsChanged) failed: no limb dropped, no kit rebuilt");
        return true;
    }

    /// <summary><c>world.Characters.LimbsChanged.Enqueue</c> by reflection: 0.17.3 queues <c>(int Character, int Limb)</c>, 0.18 and later
    /// <c>(int Character, int Limb, bool Drops)</c> (true after a sever, CombatSystem.Apply.cs:261 at v0.20.2-nightly.20261116).</summary>
    static bool AfterSever(World world, int s, int slot)
    {
        try
        {
            if (Members.CharactersLimbsChanged?.GetValue(world.Characters) is not { } queue || queue.GetType().GetMethod("Enqueue") is not { } enqueue) return false;
            var tuple = enqueue.GetParameters()[0].ParameterType;
            var args = tuple.GetGenericArguments();
            object? item = args.Length == 3 && args[2] == typeof(bool) ? Activator.CreateInstance(tuple, s, slot, true)
                : args.Length == 2 ? Activator.CreateInstance(tuple, s, slot) : null;
            if (item is null) return false;
            enqueue.Invoke(queue, [item]);
            return true;
        }
        catch { return false; }
    }

    /// <summary>TEST ONLY (pg town limbs &lt;id&gt;): the four limbs (state, the replacement's record and quality) and the items in the four limb
    /// sections (record, type, level).</summary>
    internal static string Json(World world, CharacterId id)
    {
        if (!Available || Cold(world, id) is not { Medical: { } medical } cold) return "null";
        var sb = new StringBuilder(512);
        sb.Append("{\"parts\":[");
        for (int i = 0; i < 4; i++)
        {
            if (i > 0) sb.Append(',');
            var part = medical.LimbPart(i);
            sb.Append("{\"slot\":").Append(i.ToString(CultureInfo.InvariantCulture))
              .Append(",\"state\":").Append(PGLog.Quote(part?.Limb.ToString() ?? "none"))
              .Append(",\"replacement\":").Append(PGLog.Quote(part?.ReplacementItem?.Record ?? ""))
              .Append(",\"quality\":").Append(TestJson.G(part?.ReplacementQuality ?? 0f))
              .Append(",\"section\":").Append(PGLog.Quote(LimbReplacement.SlotOf(i))).Append('}');
        }
        sb.Append("],\"sectionItems\":[");
        bool first = true;
        if (cold.Inventory is { } inv)
            foreach (var item in inv.Items)
            {
                if (!item.Section.EndsWith("_replace", StringComparison.Ordinal)) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append("{\"section\":").Append(PGLog.Quote(item.Section)).Append(",\"record\":").Append(PGLog.Quote(item.Record))
                  .Append(",\"type\":").Append(PGLog.Quote(item.Type.ToString())).Append(",\"level\":").Append(item.Level.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"x\":").Append(item.X.ToString(CultureInfo.InvariantCulture)).Append(",\"y\":").Append(item.Y.ToString(CultureInfo.InvariantCulture)).Append('}');
            }
        sb.Append("]}");
        return sb.ToString();
    }
}
