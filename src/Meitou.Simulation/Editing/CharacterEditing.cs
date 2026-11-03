using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Editing;

/// <summary>Why the character editor is open (docs/characters.md "Character editor"): making the start's characters, or a plastic surgeon's work.</summary>
/// <summary>The editor's use: the new game's characters, a plastic surgeon, or a recruit (DA_JOIN_SQUAD_WITH_EDIT; free, race and gender kept as at a surgeon).</summary>
public enum EditKind : byte { Creation, Surgery, Recruit }

/// <summary>
/// The character editor's result for one character: its new look (a CHARACTER_APPEARANCE, normally a <see cref="CharacterDraft"/> of its current one) and name.
/// Validated when applied (<see cref="CharacterEditing.Apply"/>): a player character; at creation only while the new game's editor is open
/// (<see cref="PlayerState.InCreation"/>), in surgery only beside the <see cref="Surgeon"/> and with the race and gender unchanged.
/// </summary>
public sealed record EditCharacter(CharacterId Character, FcsRecord Appearance, string Name, EditKind Kind) : SimCommand
{
    /// <summary>The plastic surgeon doing the work (surgery only).</summary>
    public CharacterId Surgeon { get; init; } = CharacterId.None;
    /// <summary>The surgery was paid already (a surgeon's dialogue line takes the money, DA_TAKE_MONEY, before it opens the editor).</summary>
    public bool Paid { get; init; }
}

/// <summary>Ends the new game's character creation: the start's characters cannot be remade any more.</summary>
public sealed record FinishCreation : SimCommand;

/// <summary>Applies <see cref="EditCharacter"/> (the player system's inputs).</summary>
public static class CharacterEditing
{
    /// <summary>The longest name the editor takes (the layout's <c>NameText</c> <c>MaxTextLength</c>).</summary>
    public const int MaxNameLength = 30;
    /// <summary>How near (world units) the surgeon must stand: the talk range of a dialogue (engine choice, <b>Unknown</b> in the original).</summary>
    public const float SurgeonReach = 60;

    /// <summary>What <paramref name="id"/>'s surgery costs: its record's dialogue, or its squad template's leader dialogue when it leads the squad; null: not a surgeon.</summary>
    internal static int? SurgeryCost(World world, Meitou.Data.Characters.SurgeonDialogues surgeons, CharacterId id, CharacterCold cold)
    {
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        return surgeons.CostOf(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == id);
    }

    public static void Apply(World world, PopulationData data, EditCharacter edit)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(edit.Character, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold || cold.Appearance is not { Animal: null } look)
        {
            world.Post("That character cannot be edited.");
            return;
        }
        var currentRace = look.Race;
        var proposedRace = edit.Appearance.References.TryGetValue("race", out var refs) && refs.Count > 0 ? data.Db.Find(refs[0].TargetStringId) : null;
        if (proposedRace is not { Type: FcsRecordType.RACE }) { world.Post("The edited look has no race."); return; }
        if (edit.Kind == EditKind.Creation)
        {
            if (!world.Player.InCreation) { world.Post("Characters can only be made at the start of a new game."); return; }
            if (!CharacterDraft.PlayableRaces(data.Db, world.Player.CreationRaces).Any(g => g.Races.Any(r => r.StringId == proposedRace.StringId)) && proposedRace.StringId != currentRace.StringId)
            {
                world.Post($"{proposedRace.Name} is not a playable race.");
                return;
            }
        }
        else if (edit.Kind == EditKind.Recruit)
        {
            bool female = edit.Appearance.Bools.TryGetValue("sex female", out var f) && f;
            if (proposedRace.StringId != currentRace.StringId || female != look.Female) { world.Post("A recruit's race and gender cannot be changed."); return; }
        }
        else
        {
            if (!table.TryResolveNext(edit.Surgeon, out int surgeon) || !table.IsAlive(edit.Surgeon) ||
                System.Numerics.Vector3.Distance(table.Next[surgeon].Position, table.Next[slot].Position) > SurgeonReach)
            {
                world.Post("The surgeon is not here any more.");
                return;
            }
            int? known = edit.Paid ? 0 : SurgeryCost(world, data.Surgeons, edit.Surgeon, table.Cold(surgeon)!);
            if (known is not { } cost) { world.Post("That is not a surgeon."); return; }
            // The dialogue's gate is DC_PLAYERMONEY > cost (strict), then DA_TAKE_MONEY takes it.
            if (cost > 0 && world.Player.Money <= cost) { world.Post($"Surgery costs c.{cost}: not enough money."); return; }
            bool female = edit.Appearance.Bools.TryGetValue("sex female", out var f) && f;
            if (proposedRace.StringId != currentRace.StringId || female != look.Female)
            {
                world.Post("A surgeon cannot change race or gender.");
                return;
            }
            world.Player.Money -= cost;
        }
        var record = data.Appearances?.Sanitize(edit.Appearance, proposedRace) ?? CharacterDraft.Copy(edit.Appearance);

        // A new race at creation: a body of that race (its stats and limbs), its walk and footprint.
        if (proposedRace.StringId != currentRace.StringId)
        {
            var (raceData, stats, medical) = data.Bodies.Create(proposedRace, data.Db.Find(cold.RecordId), world.Seed, Rng.Key(edit.Character));
            cold.Race = raceData;
            cold.Stats = stats;
            cold.Medical = medical;
            cold.FootprintRadius = raceData.PathfindFootprintRadius;
            ref var hot = ref table.Next[slot];
            hot.MaxSpeed = Bodies.Speed.Run(raceData, stats, medical, 1);
            hot.WalkSpeed = raceData.WalkSpeed;
        }

        // The look: the new record wearing what the character wears now.
        var worn = cold.Inventory is { } inventory ? SavedKit.FromInventory(inventory) : [];
        var kit = SavedKit.LoadoutOf(record, worn, data.Db, look.Loadout?.Seed ?? 0);
        if (kit is not null) SavedKit.SetLimbs(kit, cold.Medical, data.Db);   // stumps and prosthetics stay on the edited look
        if (kit is not null && data.Appearances?.Create(cold.RecordId, kit) is { } updated)
        {
            cold.Appearance = updated;
            cold.LookEdited = true;
        }
        else world.Post("The edited look could not be made.");
        string name = edit.Name.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (name.Length > 0) cold.Name = name;
    }

    /// <summary>
    /// The end of the new game's creation (<see cref="FinishCreation"/>; the editor's close in mode 0, <c>FUN_1405f5d10</c>): every human of the start's squad has its inventory sections
    /// validated for the race it has now (<see cref="Items.InventoryCommands.ValidateSections"/>, <c>validateInventorySections</c>), so one switched in the editor to a race with
    /// <c>no hats</c>, <c>no shirts</c> or <c>no shoes</c> loses what it wore there, and its worn look and gear are refreshed.
    /// </summary>
    internal static void EndCreation(World world, CharacterAssembly? assembly)
    {
        if (world.Squads.Find(world.Player.Squad) is not { } squad) return;
        foreach (var id in squad.Members)
        {
            if (!world.Characters.TryResolveNext(id, out int slot)) continue;
            if (world.Characters.Cold(slot) is not { Inventory: { } inventory } cold || SquadRegistry.IsAnimal(cold)) continue;

            int before = inventory.Items.Count;
            Items.InventoryCommands.ValidateSections(world, slot, cold);
            if (inventory.Items.Count != before) assembly?.RefreshEquipment(cold);
        }
    }
}
