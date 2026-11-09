using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

/// <summary>
/// The Meitou side of two off-screen switches (docs/simulation.md "Off-screen tier"); the original does neither, nothing changes off screen there
/// (docs/game/factions-squads-towns.md "Off-screen world").
/// <list type="bullet">
/// <item><see cref="OffscreenFights"/>: hostile travelling stand-ins within <see cref="FightRadius"/> of each other fight it out by strength.</item>
/// <item><see cref="OffscreenHealing"/>: a squad coming back into the world catches up its healing for the time it was away.</item>
/// </list>
/// </summary>
public sealed partial class PopulationSystem
{
    /// <summary>
    /// Stand-ins this close (XZ) meet (engine choice: about a town's edge).
    /// </summary>
    public const float FightRadius = 300;

    readonly List<Platoon> _fighters = [];
    readonly Dictionary<(int X, int Z), List<Platoon>> _fightCells = [];
    readonly Dictionary<string, RaceData?> _recoverRaces = new(StringComparer.Ordinal);

    /// <summary>
    /// Settles the meetings of hostile stand-ins (serial phase): every unloaded platoon that is not a town's own nor the player's, met by an enemy within
    /// <see cref="FightRadius"/>, fights the first one met in id order. The winner is drawn by the square of the strengths (A wins with S_A² / (S_A² + S_B²)),
    /// the loser is wiped out (despawned; a save leaves it out), and the winner loses its weakest members in the share (S_loser / S_winner)² / 2.
    /// </summary>
    void Fights(World world)
    {
        _fighters.Clear();
        foreach (var list in _fightCells.Values) list.Clear();
        foreach (var p in world.Platoons.All)
        {
            if (p.State != PlatoonState.Unloaded || p.Pinned || p.Faction < 0 || p.Faction == _data.PlayerFaction || p.Faction == world.Player.Faction)
                continue;

            _fighters.Add(p);
            var cell = ((int)MathF.Floor(p.Position.X / FightRadius), (int)MathF.Floor(p.Position.Y / FightRadius));
            if (!_fightCells.TryGetValue(cell, out var at)) _fightCells[cell] = at = [];
            at.Add(p);
        }

        if (_fighters.Count < 2) return;

        var gone = new HashSet<int>();
        foreach (var a in _fighters)
        {
            if (gone.Contains(a.Id)) continue;

            int cx = (int)MathF.Floor(a.Position.X / FightRadius), cz = (int)MathF.Floor(a.Position.Y / FightRadius);
            Platoon? foe = null;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!_fightCells.TryGetValue((cx + dx, cz + dz), out var cell)) continue;

                    foreach (var b in cell)
                    {
                        if (b.Id == a.Id || gone.Contains(b.Id) || foe is not null && b.Id >= foe.Id) continue;
                        if (Vector2.DistanceSquared(a.Position, b.Position) > FightRadius * FightRadius) continue;
                        if (!AggroSystem.IsEnemy(_data.Relations, a.Faction, b.Faction) && !AggroSystem.IsEnemy(_data.Relations, b.Faction, a.Faction)) continue;

                        foe = b;
                    }
                }
            }

            if (foe is null) continue;

            var sa = Strengths(a);
            var sb = Strengths(foe);
            float ta = sa.Sum(s => s.Strength), tb = sb.Sum(s => s.Strength);
            if (ta <= 0 && tb <= 0) continue;

            float u = Rng.Float(Rng.Hash(world.Seed, (ulong)a.Id << 32 | (uint)foe.Id, RngPurpose.Combat, (ulong)world.Tick));
            bool aWins = u < ta * ta / (ta * ta + tb * tb);
            var (winner, loser, ws, wt, lt) = aWins ? (a, foe, sa, ta, tb) : (foe, a, sb, tb, ta);
            int losses = (int)MathF.Round(ws.Count * MathF.Min(1, lt * lt / MathF.Max(wt * wt, 1e-3f)) * 0.5f);
            foreach (var (member, _) in ws.OrderBy(s => s.Strength).Take(losses)) Kill(world, winner, member);

            gone.Add(loser.Id);
            Despawn(world, loser, killed: true);
            if (_townLog.Count >= 256) _townLog.RemoveAt(0);
            _townLog.Add($"Off screen at {winner.Position.X:0}, {winner.Position.Y:0}: {NameOf(winner)} ({wt:0}) beat {NameOf(loser)} ({lt:0}), losing {losses}");
        }
    }

    string NameOf(Platoon p) => p.Stored?.Name ?? p.Saved?.Name ?? _data.Db.Find(p.TemplateId)?.Name ?? p.TemplateId;

    /// <summary>
    /// The strength of each member able to fight (alive and conscious): 10 + the mean of its melee attack, melee defence and toughness. A platoon
    /// whose characters were never made (no stored state) counts <see cref="Platoon.Size"/> members of strength 25. The object identifies the member
    /// for <see cref="Kill"/>.
    /// </summary>
    List<(object? Member, float Strength)> Strengths(Platoon p)
    {
        var list = new List<(object?, float)>();
        static float Of(CharacterStats? s) => s is null ? 25 : 10 + (s[StatsEnumerated.MeleeAttack] + s[StatsEnumerated.MeleeDefence] + s.Toughness) / 3;
        if (p.Stored is { } stored)
        {
            foreach (var m in stored.Members)
            {
                if (m.Cold.Medical is not { Dead: true } and not { Unconscious: true }) list.Add((m, Of(m.Cold.Stats)));
            }
        }
        else if (p.Saved is { } saved)
        {
            foreach (var m in saved.Members)
            {
                if (m.Gone || m.Dead || m.Medical is { Dead: true } or { Unconscious: true }) continue;

                var stats = m.Stats ?? (m.Source.Stats is { } s ? CharacterStats.ReadSave(s.Floats) : null);
                list.Add((m, Of(stats)));
            }
        }
        else
        {
            for (int i = 0; i < p.Size; i++) list.Add((null, 25));
        }

        return list;
    }

    /// <summary>
    /// A member killed off screen: out of its stored squad, or gone from its saved platoon's file. One with no stored state cannot be singled out.
    /// </summary>
    void Kill(World world, Platoon p, object? member)
    {
        string? recordId = null;
        if (member is StoredMember sm)
        {
            p.Stored?.Members.Remove(sm);
            recordId = sm.Cold.RecordId;
        }
        else if (member is Saving.SavedMember saved)
        {
            saved.Gone = true;
            recordId = saved.Source.RecordId;
        }

        if (recordId is not null && UniqueCharacters.IsUnique(_data.Db.Find(recordId))) world.Uniques.Died(recordId, false, p.Faction);
    }

    /// <summary>
    /// Heals stored characters for the game hours since they were stored (a platoon's, or a kept wild squad's).
    /// </summary>
    void RecoverStored(World world, StoredSquad stored)
    {
        float hours = (world.Tick - stored.StoredTick) * BodySystem.HoursPerTick;
        foreach (var m in stored.Members)
        {
            if (m.Cold is { Medical: { } medical, Race: { } race } cold)
            {
                var context = new MedicalContext(_data.Bodies.Constants, _data.Bodies.Options, race)
                {
                    Toughness = cold.Stats?.ScaledToughness ?? 1,
                    Strength = cold.Stats?.Strength ?? 50,
                    Resting = true,
                };
                medical.Recover(hours, context);
            }
        }

        stored.StoredTick = world.Tick;
    }

    /// <summary>
    /// Heals a platoon's kept characters for the game hours since they left the world (<see cref="MedicalState.Recover"/>), just before they are made
    /// again. Characters a save holds that were never in the world keep the save's state.
    /// </summary>
    void Recover(World world, Platoon p)
    {
        var constants = _data.Bodies.Constants;
        var options = _data.Bodies.Options;
        if (p.Stored is { } stored)
        {
            RecoverStored(world, stored);
        }
        else if (p.Saved is { StoredTick: >= 0 } saved)
        {
            float hours = (world.Tick - saved.StoredTick) * BodySystem.HoursPerTick;
            foreach (var m in saved.Members)
            {
                if (m.Medical is not { } medical || m.Gone || m.Dead || _data.Db.Find(m.Source.RecordId) is not { } record) continue;
                if (CharacterAssembly.RaceId(record) is not { } raceId || CharacterAssembly.RaceDataOf(_data.Db, raceId, _recoverRaces) is not { } race) continue;

                var context = new MedicalContext(constants, options, race)
                {
                    Toughness = m.Stats?.ScaledToughness ?? 1,
                    Strength = m.Stats?.Strength ?? 50,
                    Resting = true,
                };
                medical.Recover(hours, context);
            }

            saved.StoredTick = world.Tick;
        }
    }
}
