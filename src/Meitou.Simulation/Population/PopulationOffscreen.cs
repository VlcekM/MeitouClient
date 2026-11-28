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

    /// <summary>
    /// The stand-ins that may fight in this check.
    /// </summary>
    readonly List<Platoon> _fighters = [];
    /// <summary>
    /// The fighting stand-ins by grid cell of <see cref="FightRadius"/>.
    /// </summary>
    readonly Dictionary<(int X, int Z), List<Platoon>> _fightCells = [];
    /// <summary>
    /// The enemies within reach of the stand-in looking for a fight, in id order (<see cref="FoeOf"/>).
    /// </summary>
    readonly List<Platoon> _foes = [];
    /// <summary>
    /// Whether the fights are being settled: a code mod's despawn waits (<see cref="DespawnPlatoon"/>).
    /// </summary>
    bool _fighting;
    /// <summary>
    /// Platoons by id.
    /// </summary>
    static readonly Comparison<Platoon> ById = (x, y) => x.Id.CompareTo(y.Id);
    /// <summary>
    /// The race data cache of the off-screen healing.
    /// </summary>
    readonly Dictionary<string, RaceData?> _recoverRaces = new(StringComparer.Ordinal);

    /// <summary>
    /// Settles the meetings of hostile stand-ins (serial phase): every unloaded platoon that is not a town's own nor the player's, met by an enemy
    /// within <see cref="FightRadius"/>, fights the first one met in id order that the code mods' rules do not skip
    /// (<see cref="IOffscreenFightRule"/>). The winner is the one the rules name, else drawn by the square of the strengths (A wins with S_A² / (S_A²
    /// + S_B²)); the loser is wiped out (despawned; a save leaves it out), and the winner loses its weakest members in the share (S_loser /
    /// S_winner)² / 2.
    /// </summary>
    void Fights(World world)
    {
        _fighting = true;
        try
        {
            SettleFights(world);
        }
        finally
        {
            _fighting = false;
        }
    }

    /// <summary>
    /// The body of <see cref="Fights"/>.
    /// </summary>
    void SettleFights(World world)
    {
        _fighters.Clear();
        foreach (var list in _fightCells.Values) list.Clear();
        foreach (var p in world.Platoons.All)
        {
            if (p.State != PlatoonState.Unloaded || p.Pinned || p.Faction < 0 || p.Faction == _data.PlayerFaction || p.Faction == world.Player.Faction)
                continue;

            // A captive's stand-in does not travel (Platoon.Captive), so it meets nobody: prisoners in neighbouring cages do not fight.
            if (p.Captive) continue;

            _fighters.Add(p);
            var cell = ((int)MathF.Floor(p.Position.X / FightRadius), (int)MathF.Floor(p.Position.Y / FightRadius));
            if (!_fightCells.TryGetValue(cell, out var at)) _fightCells[cell] = at = [];
            at.Add(p);
        }

        if (_fighters.Count < 2) return;

        var gone = new HashSet<int>();
        foreach (var a in _fighters)
        {
            if (gone.Contains(a.Id) || !Registered(world, a)) continue;
            if (FoeOf(world, a, gone) is not { } met) continue;

            var (foe, verdict) = met;
            var sa = Strengths(a);
            var sb = Strengths(foe);
            float ta = sa.Sum(s => s.Strength), tb = sb.Sum(s => s.Strength);
            if (ta <= 0 && tb <= 0) continue;

            bool aWins = verdict switch
            {
                OffscreenFightVerdict.FirstWins => true,
                OffscreenFightVerdict.SecondWins => false,
                _ => DrawsFirst(world, a, foe, ta, tb),
            };
            var (winner, loser, ws, wt, lt) = aWins ? (a, foe, sa, ta, tb) : (foe, a, sb, tb, ta);
            int losses = (int)MathF.Round(ws.Count * MathF.Min(1, lt * lt / MathF.Max(wt * wt, 1e-3f)) * 0.5f);
            foreach (var (member, _) in ws.OrderBy(s => s.Strength).Take(losses)) Kill(world, winner, member, loser.Faction);

            gone.Add(loser.Id);
            if (!Registered(world, loser)) continue;

            ReportWipedOut(world, loser, winner.Faction);
            Despawn(world, loser, killed: true, why: PlatoonGone.LostFight, by: winner.Id);
            if (_townLog.Count >= 256) _townLog.RemoveAt(0);
            _townLog.Add($"Off screen at {winner.Position.X:0}, {winner.Position.Y:0}: {NameOf(winner)} ({wt:0}) beat {NameOf(loser)} ({lt:0}), "
                + $"losing {losses}");
        }
    }

    /// <summary>
    /// Whether <paramref name="a"/> wins the draw against <paramref name="b"/>: with S_A² / (S_A² + S_B²), rolled from the pair and the tick.
    /// </summary>
    static bool DrawsFirst(World world, Platoon a, Platoon b, float ta, float tb)
    {
        float u = Rng.Float(Rng.Hash(world.Seed, (ulong)a.Id << 32 | (uint)b.Id, RngPurpose.Combat, (ulong)world.Tick));
        return u < ta * ta / (ta * ta + tb * tb);
    }

    /// <summary>
    /// The enemy <paramref name="a"/> fights: the first in id order within <see cref="FightRadius"/>, not gone, hostile either way and not skipped by
    /// the code mods' rules (<see cref="IOffscreenFightRule"/>, asked in that order until one is not skipped), with the rules' verdict; null for
    /// none, or once <paramref name="a"/> has left the world.
    /// </summary>
    (Platoon Foe, OffscreenFightVerdict Verdict)? FoeOf(World world, Platoon a, HashSet<int> gone)
    {
        Enemies(world, a, gone);
        if (_foes.Count == 0) return null;
        if (!world.OffscreenFightRules.Active) return (_foes[0], OffscreenFightVerdict.Fight);

        foreach (var b in _foes)
        {
            if (!Registered(world, b)) continue;

            var verdict = world.OffscreenFightRules.Judge(world, a, b);
            if (!Registered(world, a)) return null;
            if (verdict != OffscreenFightVerdict.Skip) return (b, verdict);
        }

        return null;
    }

    /// <summary>
    /// Fills <see cref="_foes"/> with the enemies within <see cref="FightRadius"/> of <paramref name="a"/>, not gone and hostile either way: in id
    /// order when the code mods' rules ask about them, else only the first in id order.
    /// </summary>
    void Enemies(World world, Platoon a, HashSet<int> gone)
    {
        _foes.Clear();
        bool all = world.OffscreenFightRules.Active;
        int cx = (int)MathF.Floor(a.Position.X / FightRadius), cz = (int)MathF.Floor(a.Position.Y / FightRadius);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!_fightCells.TryGetValue((cx + dx, cz + dz), out var cell)) continue;

                foreach (var b in cell)
                {
                    if (b.Id == a.Id || gone.Contains(b.Id) || !all && _foes.Count > 0 && b.Id >= _foes[0].Id) continue;
                    if (Vector2.DistanceSquared(a.Position, b.Position) > FightRadius * FightRadius) continue;
                    if (!AggroSystem.IsEnemy(_data.Relations, a.Faction, b.Faction) && !AggroSystem.IsEnemy(_data.Relations, b.Faction, a.Faction))
                        continue;

                    if (all) _foes.Add(b);
                    else if (_foes.Count == 0) _foes.Add(b);
                    else _foes[0] = b;
                }
            }
        }

        if (all) _foes.Sort(ById);
    }

    /// <summary>
    /// Whether <paramref name="p"/> is still in the world (a code mod's rule or death observer may have removed it during the fights).
    /// </summary>
    static bool Registered(World world, Platoon p) => world.Platoons.Find(p.Id) == p;

    /// <summary>
    /// A platoon's name for the town log: its stored or saved name, else its template's name, else the template id.
    /// </summary>
    string NameOf(Platoon p) => p.Stored?.Name ?? p.Saved?.Name ?? _data.Db.Find(p.TemplateId)?.Name ?? p.TemplateId;

    /// <summary>
    /// The strength of each member able to fight (alive and conscious): 10 + the mean of its melee attack, melee defence and toughness. A platoon
    /// whose characters were never made (no stored state) counts <see cref="Platoon.Size"/> members of strength 25. The object identifies the member
    /// for <see cref="Kill"/>.
    /// </summary>
    List<(object? Member, float Strength)> Strengths(Platoon p)
    {
        var list = new List<(object?, float)>();
        static float Of(CharacterStats? s) =>
            s is null ? 25 : 10 + (s[StatsEnumerated.MeleeAttack] + s[StatsEnumerated.MeleeDefence] + s.Toughness) / 3;
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
    /// A member killed off screen by a platoon of <paramref name="foeFaction"/>: out of its stored squad, or gone from its saved platoon's file,
    /// and reported to the death observers. One with no stored state cannot be singled out.
    /// </summary>
    void Kill(World world, Platoon p, object? member, int foeFaction)
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
            saved.Cold = null;
            recordId = saved.Source.RecordId;
        }

        if (recordId is null) return;

        if (UniqueCharacters.IsUnique(_data.Db.Find(recordId))) world.Uniques.Died(recordId, false, p.Faction);
        DeathObservers.ReportOffScreen(world, CharacterId.None, recordId, p.Faction, p.Id, foeFaction);
    }

    /// <summary>
    /// Reports each living member of a platoon wiped out off screen by a platoon of <paramref name="winnerFaction"/> to the death observers, in
    /// member order, with the platoon's faction: the characters of its squad in the world, else its stored or saved members, else
    /// <see cref="Platoon.Size"/> members never made. A member already dead was reported when it died (or is as it leaves the world). Nothing
    /// without an observer.
    /// </summary>
    void ReportWipedOut(World world, Platoon loser, int winnerFaction)
    {
        if (!world.DeathObservers.Active) return;

        var table = world.Characters;
        if (world.Squads.Find(loser.SquadId) is { } squad)
        {
            foreach (var m in squad.Members)
            {
                if (table.TryResolveNext(m, out int slot) && table.Cold(slot) is { Medical: not { Dead: true } } cold)
                    DeathObservers.ReportOffScreen(world, m, cold.RecordId, loser.Faction, loser.Id, winnerFaction);
            }
        }
        else if (loser.Stored is { } stored)
        {
            foreach (var m in stored.Members)
            {
                if (m.Cold.Medical is not { Dead: true })
                    DeathObservers.ReportOffScreen(world, CharacterId.None, m.Cold.RecordId, loser.Faction, loser.Id, winnerFaction);
            }
        }
        else if (loser.Saved is { } saved)
        {
            foreach (var m in saved.Members)
            {
                if (!m.Gone && !m.Dead && m.Medical is not { Dead: true })
                    DeathObservers.ReportOffScreen(world, CharacterId.None, m.Source.RecordId, loser.Faction, loser.Id, winnerFaction);
            }
        }
        else
        {
            for (int i = 0; i < loser.Size; i++) DeathObservers.ReportOffScreen(world, CharacterId.None, "", loser.Faction, loser.Id, winnerFaction);
        }
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
                if (CharacterAssembly.RaceId(record) is not { } raceId
                    || CharacterAssembly.RaceDataOf(_data.Db, raceId, _recoverRaces) is not { } race)
                    continue;

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
