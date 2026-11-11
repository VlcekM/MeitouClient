using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;

namespace Meitou.Simulation.Editing;

/// <summary>
/// Why the character editor is open (docs/characters.md "Character editor"): making the start's characters, or a plastic surgeon's work. The
/// editor's use: the new game's characters, a plastic surgeon, or a recruit (DA_JOIN_SQUAD_WITH_EDIT; free, race and gender kept as at a surgeon).
/// </summary>
public enum EditKind : byte
{
    /// <summary>
    /// The new game's characters.
    /// </summary>
    Creation,
    /// <summary>
    /// A plastic surgeon's work, paid.
    /// </summary>
    Surgery,
    /// <summary>
    /// A recruit's edit (DA_JOIN_SQUAD_WITH_EDIT): free, race and gender kept.
    /// </summary>
    Recruit
}

/// <summary>
/// The character editor's result for one character: its new look (a CHARACTER_APPEARANCE, normally a <see cref="CharacterDraft"/> of its current one)
/// and name. Validated when applied (<see cref="CharacterEditing.Apply"/>): a player character; at creation only while the new game's editor is open
/// (<see cref="PlayerState.InCreation"/>), in surgery only beside the <see cref="Surgeon"/> and with the race and gender unchanged. A null look
/// renames only: an animal's page in the editor offers its name and nothing else (docs/characters.md "Character editor"). Allowed in every kind: a
/// bought animal is named through the in-play (Recruit) editor; only a creation rename needs the creation to be open.
/// </summary>
public sealed record EditCharacter(CharacterId Character, FcsRecord? Appearance, string Name, EditKind Kind) : SimCommand
{
    /// <summary>
    /// The plastic surgeon doing the work (surgery only).
    /// </summary>
    public CharacterId Surgeon { get; init; } = CharacterId.None;

    /// <summary>
    /// The surgery was paid already (a surgeon's dialogue line takes the money, DA_TAKE_MONEY, before it opens the editor).
    /// </summary>
    public bool Paid { get; init; }
}

/// <summary>
/// Ends the new game's character creation: the start's characters cannot be remade any more.
/// </summary>
public sealed record FinishCreation : SimCommand;

/// <summary>
/// Applies <see cref="EditCharacter"/> (the player system's inputs).
/// </summary>
public static class CharacterEditing
{
    /// <summary>
    /// The longest name the editor takes (the layout's <c>NameText</c> <c>MaxTextLength</c>).
    /// </summary>
    public const int MaxNameLength = 30;

    /// <summary>
    /// How near (world units) the surgeon must stand: the talk range of a dialogue (engine choice, <b>Unknown</b> in the original).
    /// </summary>
    public const float SurgeonReach = 60;

    /// <summary>
    /// What <paramref name="id"/>'s surgery costs: its record's dialogue, or its squad template's leader dialogue when it leads the squad; null: not
    /// a surgeon.
    /// </summary>
    internal static int? SurgeryCost(World world, SurgeonDialogues surgeons, CharacterId id, CharacterCold cold)
    {
        var squad = cold.SquadId >= 0 ? world.Squads.Find(cold.SquadId) : null;
        return surgeons.CostOf(cold.RecordId, squad?.TemplateId, squad is not null && squad.Leader == id);
    }

    /// <summary>
    /// Applies an edit: a rename, or the look and the name once <see cref="Admit"/> allows it.
    /// </summary>
    public static void Apply(World world, PopulationData data, EditCharacter edit)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(edit.Character, out int slot)
            || table.Cold(slot) is not { IsPlayer: true, Appearance: { } look } cold)
        {
            world.Post("That character cannot be edited.");
            return;
        }

        // A name only: an animal's page (FUN_1405e9410 renames whichever character the editor shows, animals too).
        if (edit.Appearance is not { } appearance)
        {
            if (edit.Kind == EditKind.Creation && !world.Player.InCreation)
            {
                world.Post("Characters can only be made at the start of a new game.");
                return;
            }

            Rename(cold, edit.Name);
            return;
        }

        if (look.Animal is not null)
        {
            world.Post("That character cannot be edited.");
            return;
        }

        var proposedRace = ProposedRace(data, appearance);
        if (proposedRace is not { Type: FcsRecordType.Race })
        {
            world.Post("The edited look has no race.");
            return;
        }

        if (!Admit(world, data, edit, appearance, slot, look, proposedRace)) return;

        Remake(world, data, edit.Character, slot, cold, appearance, proposedRace);
        Rename(cold, edit.Name);
    }

    /// <summary>
    /// The race record the edited look names (its first <c>race</c> reference); null when it names none.
    /// </summary>
    static GameRecord? ProposedRace(PopulationData data, FcsRecord appearance) =>
        appearance.References.TryGetValue("race", out var refs) && refs.Count > 0 ? data.Db.Find(refs[0].TargetStringId) : null;

    /// <summary>
    /// Whether the edit may be made: at creation only while the editor is open and to a playable race; a recruit and a surgeon keep race and gender;
    /// a surgeon must stand by and be paid (taken here when the dialogue has not taken it).
    /// </summary>
    static bool Admit(
        World world, PopulationData data, EditCharacter edit, FcsRecord appearance, int slot, CharacterAppearance look, GameRecord proposedRace)
    {
        var table = world.Characters;
        bool sameRace = proposedRace.StringId == look.Race.StringId;
        bool sameSex = (appearance.Bools.TryGetValue("sex female", out var f) && f) == look.Female;
        if (edit.Kind == EditKind.Creation)
        {
            if (!world.Player.InCreation)
            {
                world.Post("Characters can only be made at the start of a new game.");
                return false;
            }

            bool playable = PlayableRaces.Of(data.Db, world.Player.CreationRaces)
                .Any(g => g.Races.Any(r => r.StringId == proposedRace.StringId));
            if (!playable && !sameRace)
            {
                world.Post($"{proposedRace.Name} is not a playable race.");
                return false;
            }

            return true;
        }

        if (edit.Kind == EditKind.Recruit)
        {
            if (!sameRace || !sameSex)
            {
                world.Post("A recruit's race and gender cannot be changed.");
                return false;
            }

            return true;
        }

        if (!table.TryResolveNext(edit.Surgeon, out int surgeon) || !table.IsAlive(edit.Surgeon)
            || System.Numerics.Vector3.Distance(table.Next[surgeon].Position, table.Next[slot].Position) > SurgeonReach)
        {
            world.Post("The surgeon is not here any more.");
            return false;
        }

        int? known = edit.Paid ? 0 : SurgeryCost(world, data.Surgeons, edit.Surgeon, table.Cold(surgeon)!);
        if (known is not { } cost)
        {
            world.Post("That is not a surgeon.");
            return false;
        }

        // The dialogue's gate is DC_PLAYERMONEY > cost (strict), then DA_TAKE_MONEY takes it.
        if (cost > 0 && world.Player.Money <= cost)
        {
            world.Post($"Surgery costs c.{cost}: not enough money.");
            return false;
        }

        if (!sameRace || !sameSex)
        {
            world.Post("A surgeon cannot change race or gender.");
            return false;
        }

        world.Player.Money -= cost;
        return true;
    }

    /// <summary>
    /// The character takes the edited look: a new race's body first (creation), then the look wearing what it wears now.
    /// </summary>
    static void Remake(World world, PopulationData data, CharacterId id, int slot, CharacterCold cold, FcsRecord appearance, GameRecord proposedRace)
    {
        var look = cold.Appearance!;
        var record = data.Appearances?.Sanitize(appearance, proposedRace) ?? CharacterDraft.Copy(appearance);

        // A new race at creation: a body of that race (its stats and limbs, the start's prosthetics kept on it), its walk and footprint.
        if (proposedRace.StringId != look.Race.StringId)
        {
            var prosthetics = Prosthetics(cold.Medical);
            var (raceData, stats, medical) = data.Bodies.Create(proposedRace, data.Db.Find(cold.RecordId), world.Seed, Rng.Key(id));
            if (!raceData.IsRobot && cold.Medical is { } previous) medical.Hunger = previous.Hunger;

            foreach (var (limb, item) in prosthetics) medical.FitReplacement(limb, item);
            cold.Race = raceData;
            cold.Stats = stats;
            cold.Medical = medical;
            cold.FootprintRadius = raceData.PathfindFootprintRadius;
            ref var hot = ref world.Characters.Next[slot];
            hot.MaxSpeed = Speed.Run(raceData, stats, medical, 1);
            hot.WalkSpeed = raceData.WalkSpeed;
        }

        // The look: the new record wearing what the character wears now.
        var worn = cold.Inventory is { } inventory ? SavedKit.FromInventory(inventory) : [];
        var kit = SavedKit.LoadoutOf(record, worn, data.Db, look.Loadout?.Seed ?? 0);

        // Stumps and prosthetics stay on the edited look.
        if (kit is not null) SavedKit.SetLimbs(kit, cold.Medical, data.Db);
        if (kit is not null && data.Appearances?.Create(cold.RecordId, kit, cold.Shaved) is { } updated)
        {
            cold.Appearance = updated;
            cold.LookEdited = true;
        }
        else
        {
            world.Post("The edited look could not be made.");
        }
    }

    /// <summary>
    /// The prosthetics a body wears (a start's <c>robot limbs</c>), to fit on the body a race switched in the creator gives it: without them the new
    /// body has natural limbs while their items still sit in the limb sections, so only the default race kept them (#360).
    /// </summary>
    static List<(Meitou.Data.Gameplay.Bodies.LimbReplacement Limb, Items.ItemInstance? Item)> Prosthetics(MedicalState? medical)
    {
        var list = new List<(Meitou.Data.Gameplay.Bodies.LimbReplacement, Items.ItemInstance?)>();
        for (int k = 0; k < 4; k++)
        {
            if (medical?.LimbPart(k) is { Limb: LimbState.Replaced, Replacement: { } limb } part) list.Add((limb, part.ReplacementItem));
        }

        return list;
    }

    /// <summary>
    /// The typed name, trimmed and cut to <see cref="MaxNameLength"/>; an empty one leaves the name.
    /// </summary>
    static void Rename(CharacterCold cold, string typed)
    {
        string name = typed.Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength];
        if (name.Length > 0)
        {
            cold.Name = name;
        }
    }

    /// <summary>
    /// The end of the new game's creation (<see cref="FinishCreation"/>; the editor's close in mode 0, <c>FUN_1405f5d10</c>): every human of the
    /// start's squad has its inventory sections validated for the race it has now (<see cref="Items.InventoryCommands.ValidateSections"/>,
    /// <c>validateInventorySections</c>), so one switched in the editor to a race with <c>no hats</c>, <c>no shirts</c> or <c>no shoes</c> loses what
    /// it wore there, and its worn look and gear are refreshed.
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
            if (inventory.Items.Count != before)
            {
                assembly?.RefreshEquipment(cold);
            }
        }
    }
}
