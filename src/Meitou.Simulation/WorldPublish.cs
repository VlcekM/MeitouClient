using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation;

public sealed partial class World
{
    internal Partition PartitionAt(int i) => partitions[i];
    internal EffectBuffer BufferAt(int i) => buffers[i];

    /// <summary>Builds the <see cref="WorldSnapshot"/> of the tick that just ended from the state it left in <see cref="CharacterTable.Previous"/>.</summary>
    void Publish()
    {
        var table = Characters;
        var list = new List<CharacterSnapshot>(table.Count);
        var state = table.Previous;
        for (int i = 0; i < state.Length; i++)
        {
            if (!state[i].Alive) continue;
            var id = new CharacterId(i, state[i].Generation);
            var cold = table.Cold(i);
            // The details only the selection shows: asked once per character, the selection is a list.
            bool selected = cold is { IsPlayer: true } && Player.Selection.Contains(id);
            list.Add(new CharacterSnapshot(id, cold?.Appearance, state[i].Position, state[i].Yaw, cold?.Animation is { } anim && animations is not null ? animations.Publish(anim) : AnimationLayers.For(state[i].Animation, state[i].AnimationTime))
            {
                Faction = cold?.Faction ?? -1,
                Name = cold?.Name ?? "",
                SquadId = cold?.SquadId ?? -1,
                IsPlayer = cold?.IsPlayer ?? false,
                Selected = selected,
                Skills = selected && cold!.Stats is { } sk ? $"Atk {sk[StatsEnumerated.MeleeAttack]:0.0} Def {sk[StatsEnumerated.MeleeDefence]:0.0} Dodge {sk[StatsEnumerated.Dodge]:0.0} Tough {sk.Toughness:0.0} Str {sk.Strength:0.0} Ath {sk.Athletics:0.0}" : "",
                Inventory = selected && cold!.Inventory is { } carried ? Items.InventoryText.Lines(carried) : [],
                Body = cold?.Medical is { } med && cold.Race is { } race ? new BodyStatus(med.Blood / MathF.Max(Bodies.MedicalState.BloodCapacity(race, cold.Stats?.Strength ?? 50), 1), WorstPart(med), med.Hunger, med.Unconscious, med.Dead) : null,
                Path = selected && (state[i].Flags & (ushort)MoveFlags.HasPath) != 0 ? cold!.Path.Skip(state[i].PathCursor).ToArray() : [],
            });
        }
        PreviousSnapshot = Snapshot;
        Snapshot = new WorldSnapshot(Tick, list);
    }

    /// <summary>The lowest part fraction (1 for a body without parts), the way <c>Enumerable.Min</c> reads floats: a NaN wins.</summary>
    static float WorstPart(Bodies.MedicalState med)
    {
        var parts = med.Parts;
        if (parts.Count == 0) return 1;
        float worst = parts[0].Fraction;
        if (float.IsNaN(worst)) return worst;
        for (int k = 1; k < parts.Count; k++)
        {
            float f = parts[k].Fraction;
            if (f < worst) worst = f;
            else if (float.IsNaN(f)) return f;
        }
        return worst;
    }
}
