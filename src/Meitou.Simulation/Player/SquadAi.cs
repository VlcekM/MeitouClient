using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Player;

/// <summary>
/// The squad AI options of the overview's AI tab (docs/game/ai-tasks.md "Squad AI options"): the original's bytes at <c>DAT_142134690</c> +0x110 ..
/// +0x11a. Ditch, Sleep and Sit start implicit jobs (<see cref="Production.ProductionSystem"/>); the others widen what other rules take in.
/// </summary>
[Flags]
public enum SquadAi : ushort
{
    /// <summary>
    /// No option set.
    /// </summary>
    None = 0,
    /// <summary>
    /// Ditch items (+0x117): idle characters put carried materials into storage (job 242).
    /// </summary>
    Ditch = 1,
    /// <summary>
    /// Sleep when injured (+0x116): idle injured characters go to bed until healed (jobs 153 and 257).
    /// </summary>
    Sleep = 2,
    /// <summary>
    /// Sit when idle (+0x118): idle characters at home sit on a free chair (job 275).
    /// </summary>
    Sit = 4,
    /// <summary>
    /// Heal Allies (+0x110): the medic and robotics jobs also treat characters of allied factions (<see cref="FirstAid.FirstAidSystem"/>).
    /// </summary>
    Heal = 8,
    /// <summary>
    /// Help Allies (+0x111): the player's characters count allied factions' fighters in the protect-allies finder (squared;
    /// <see cref="RetaliationSystem"/> goal 15).
    /// </summary>
    Help = 16,
    /// <summary>
    /// Rescue Allies (+0x112): the rescue job also picks up downed characters of allied factions (<see cref="Carrying.CarrySystem"/>).
    /// </summary>
    Rescue = 32,
    /// <summary>
    /// Share Food (+0x115): a hungry character with no food of its own eats from a squad mate's backpack within <see cref="SquadAiRules.ShareReach"/>
    /// (<see cref="Items.FeedSystem"/>).
    /// </summary>
    Share = 64,
    /// <summary>
    /// Feed Animals (+0x114): off, the player's animals do not eat (<see cref="Items.FeedSystem"/>).
    /// </summary>
    Feed = 128,
    /// <summary>
    /// Shoot first (+0x11a): the player's turret gunners also take dangerous neutrals (<see cref="SquadAiRules.ShootsFirstAt"/>).
    /// </summary>
    ShootFirst = 256,
    /// <summary>
    /// Heal Neutrals (Meitou-only): player medics also treat downed characters of factions that are no enemy (<see cref="FirstAid.FirstAidSystem"/>
    /// pass 2).
    /// </summary>
    HealNeutrals = 512,
    /// <summary>
    /// Heal All (Meitou-only): as <see cref="HealNeutrals"/>, and downed enemies too.
    /// </summary>
    HealAll = 1024,
    /// <summary>
    /// A new game's options (<b>Observed</b>: 511 of the owner's saves; heal and help are on in 4, rescue and shoot first in none).
    /// </summary>
    Defaults = Ditch | Sleep | Sit | Share | Feed,
}

/// <summary>
/// Turns a squad AI option on or off.
/// </summary>
public sealed record SquadAiCommand(SquadAi Option, bool On) : SimCommand;

/// <summary>
/// The AI tab's Follow Formation: the player faction's <c>squad formation</c> (<c>FUN_140494ce0</c> binds its combo to the FACTION record's value;
/// the movement rebuilds the slots, as the original's change handler marks every player squad).
/// </summary>
public sealed record SquadFormationCommand(SquadFormation Formation) : SimCommand;

/// <summary>
/// The squad AI options' CAMERA bools (<b>Verified</b>, the camera record writer <c>FUN_1403eb710</c> and reader <c>FUN_1403ebf50</c>: the byte each
/// key holds).
/// </summary>
public static class SquadAiKeys
{
    /// <summary>
    /// The CAMERA bool of each option the original saves.
    /// </summary>
    public static readonly (SquadAi Option, string Key)[] Keys =
    [
        (SquadAi.Ditch, "ditch"), (SquadAi.Sleep, "sleep"), (SquadAi.Sit, "sit"), (SquadAi.Heal, "heal"), (SquadAi.Help, "help"), (SquadAi.Rescue,
                "rescue"),
        (SquadAi.Share, "share"), (SquadAi.Feed, "feed"), (SquadAi.ShootFirst, "shootfirst"),
    ];

    /// <summary>
    /// The Meitou-only options' CAMERA bools, written only while on.
    /// </summary>
    public static readonly (SquadAi Option, string Key)[] MeitouKeys = [(SquadAi.HealNeutrals, "meitou heal neutrals"),
            (SquadAi.HealAll, "meitou heal all")];

    /// <summary>
    /// Writes the options to the CAMERA record: the original's keys always, the Meitou keys only while on (removed while off).
    /// </summary>
    public static void Write(Data.Fcs.FcsRecord camera, SquadAi options)
    {
        foreach (var (option, key) in Keys) camera.Bools[key] = (options & option) != 0;
        foreach (var (option, key) in MeitouKeys)
            if ((options & option) != 0) camera.Bools[key] = true;
            else
                camera.Bools.Remove(key);
    }

    /// <summary>
    /// The options a CAMERA record holds; an absent key keeps the option's default (<see cref="SquadAi.Defaults"/>).
    /// </summary>
    public static SquadAi Read(Data.Fcs.FcsRecord camera)
    {
        var options = SquadAi.Defaults;
        foreach (var (option, key) in Keys.Concat(MeitouKeys))
            if (camera.Bools.TryGetValue(key, out bool on)) options = on ? options | option : options & ~option;
        return options;
    }

    /// <summary>
    /// The CAMERA int holding the player faction's <c>squad formation</c> (<b>Verified</b>: the same writer and reader; read only when the record has
    /// <c>ditch</c>).
    /// </summary>
    public const string FormationKey = "formation";

    /// <summary>
    /// Sets <paramref name="faction"/>'s formation in <see cref="World.FactionFormations"/> (made from <paramref name="factions"/> when the
    /// population has not set it yet).
    /// </summary>
    public static void SetFormation(World world, int faction, int formation, IReadOnlyList<Data.Gameplay.FactionData>? factions)
    {
        var list = world.FactionFormations?.ToArray() ?? factions?.Select(f => f.SquadFormation).ToArray();
        if (list is null || (uint)faction >= (uint)list.Length) return;
        list[faction] = formation;
        world.FactionFormations = list;
    }

    /// <summary>
    /// The player faction's formation as the AI tab and the save see it: the population's list, else the data's value, else RANDOM.
    /// </summary>
    public static int FormationOf(World world, int faction, IReadOnlyList<Data.Gameplay.FactionData>? factions) =>
        world.FactionFormations is { } list && (uint)faction < (uint)list.Count ? list[faction]
        : factions is not null && (uint)faction < (uint)factions.Count ? factions[faction].SquadFormation : (int)SquadFormation.Random;
}

/// <summary>
/// Rules of the squad AI options that other systems ask (docs/game/ai-tasks.md "Squad AI options").
/// </summary>
public static class SquadAiRules
{
    /// <summary>
    /// How far a squad mate's backpack is reached for food with <see cref="SquadAi.Share"/> (<b>Verified</b>: squared distance 40000,
    /// <c>FUN_1405d29c0</c>).
    /// </summary>
    public const float ShareReach = 200;

    /// <summary>
    /// Whether a player turret gunner with <see cref="SquadAi.ShootFirst"/> may take a sensed non-enemy of a faction whose fundamental type is
    /// <paramref name="fundamentalType"/> and whose own default relation is <paramref name="defaultRelation"/> (<c>FUN_1404374d0</c>, asked only when
    /// no enemy is in its sights): type 0 or 8, or a default below -30. Such a target is scored at range factor
    /// <see cref="TurretFormulas.ShootFirstRange"/> (CombatSystem.Turrets.cs).
    /// </summary>
    public static bool ShootsFirstAt(SquadAi options, int fundamentalType, float defaultRelation) =>
        (options & SquadAi.ShootFirst) != 0 && (fundamentalType is 0 or 8 || defaultRelation < -30);
}
