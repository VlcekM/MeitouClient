using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation;

/// <summary>
/// The bodies of the characters (stage 7, docs/game/character-stats.md): every tick it runs each living character's <see cref="MedicalState.Tick"/> (blood,
/// bleeding, part healing, hunger, the knock-out timer), sets the top speed from the speed chain (<see cref="Speed.Run"/>: athletics, legs, hunger,
/// encumbrance) and stops a character that has just been knocked out or has died. It runs in the Act phase, before the animation system, and writes only its own
/// slots. A corpse is removed <see cref="CorpseHours"/> game hours after it died (an engine choice; the original's rule is <b>Unknown</b>).
/// </summary>
/// <remarks>
/// One tick is 1/30 s of game time at speed 1, which is <see cref="HoursPerTick"/> game hours; the medical rates are per game hour
/// (character-stats.md). <see cref="BodyTimeScale"/> multiplies the time of the part and blood rates (not hunger or the knock-out timer), see
/// <see cref="MedicalContext.BodyTimeScale"/>.
/// </remarks>
public sealed class BodySystem(GameConstants constants, BodyOptions? options = null, float bodyTimeScale = 1) : ITickSystem
{
    /// <summary>Game hours in one tick: 11/36000 h is 1/30 game s at the Verified 1200/11 s per game hour (<c>GameClock</c>). Whether the original's medical dt is in game hours is Unknown (that is what <see cref="BodyTimeScale"/> covers).</summary>
    public const float HoursPerTick = 11f / 36000;
    /// <summary>How long a corpse lies before it is removed, in game hours.</summary>
    public const float CorpseHours = 12;
    public const long CorpseTicks = (long)(CorpseHours / HoursPerTick);

    public GameConstants Constants { get; } = constants;
    public BodyOptions Options { get; } = options ?? BodyOptions.Default;
    /// <summary>The <see cref="MedicalContext.BodyTimeScale"/> every character gets (default 1: the documented rates).</summary>
    public float BodyTimeScale { get; } = bodyTimeScale;

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        List<MedicalEvent>? events = null;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { } medical || cold.Race is not { } race || cold.Stats is not { } stats) continue;
            ref var n = ref next[i];
            if (medical.Dead && cold.DiedTick < 0) cold.DiedTick = world.Tick;   // also when something outside the tick killed it (a hit)
            if (medical.Incapacitated && (n.Task != (byte)CharacterTask.Idle || (n.Flags & (ushort)MoveFlags.AnyPath) != 0))
            {
                MovementSystem.Stop(table, i);
                n.Velocity = Vector3.Zero;
            }
            if (medical.Dead) continue;
            // The load: the inventory weight against the strength the injuries leave (character-stats.md "Encumbrance and carrying").
            float load = cold.Inventory?.ContentWeight() ?? 0;
            float encumbrance = load <= 0 ? 1 : Encumbrance.Factor(Constants, load, false, stats.Strength, medical.StatMultiplier(Meitou.Data.Gameplay.Bodies.StatsEnumerated.Strength));
            var ctx = new MedicalContext(Constants, Options, race)
            {
                EncumbranceFactor = encumbrance,
                Toughness = stats.Toughness,
                Strength = stats.Strength,
                Seed = world.Seed,
                CharacterKey = Rng.Key(table.IdOf(i)),
                BodyTimeScale = BodyTimeScale,
            };
            events ??= [];
            events.Clear();
            medical.Tick(HoursPerTick, ctx, events);
            n.MaxSpeed = Speed.Run(race, stats, medical, encumbrance);
            n.Health = Math.Clamp(medical.Blood / MathF.Max(MedicalState.BloodCapacity(race, stats.Strength), 1) * 100, 0, 100);
            foreach (var e in events)
            {
                if (e.Kind is MedicalEventKind.Died or MedicalEventKind.KnockedOut)
                {
                    MovementSystem.Stop(table, i);
                    n.Velocity = Vector3.Zero;
                }
            }
        }
    }

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (table.Next[i].Alive && table.Cold(i) is { DiedTick: >= 0 } cold && world.Tick - cold.DiedTick >= CorpseTicks) table.Remove(new CharacterId(i, table.Next[i].Generation));
        }
    }
}
