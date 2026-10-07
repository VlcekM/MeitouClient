using System.Diagnostics;
using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Rendering.Gpu;

namespace Meitou.Rendering.Characters;

/// <summary>
/// The test harness behind <c>--crowd N</c> (docs/character-renderer.md): N characters generated with <see cref="CharacterGenerator"/> from the
/// CHARACTER records of the start town's squad templates (its residents and bar squads; any squad template without a town), standing in a
/// spiral round the start point on the ground, half idling and half walking in place. It stands in for the simulation, which will fill
/// the <see cref="CharacterDrawList"/> from its snapshots instead.
/// </summary>
internal sealed class CrowdHarness
{
    const float Spacing = 12;
    const string Idle = "idle_stand_relax";
    static readonly string[] Walk = ["walk lower", "walk upper"];

    readonly CharacterAppearance[] appearances;
    readonly CharacterPose[][] poses;
    readonly Vector3[] positions;
    readonly float[] yaws, phases;
    readonly Func<float> time;

    CrowdHarness(CharacterAppearance[] appearances, Vector3[] positions, float[] yaws, float[] phases, CharacterPose[][] poses, Func<float> time)
        => (this.appearances, this.positions, this.yaws, this.phases, this.poses, this.time) = (appearances, positions, yaws, phases, poses, time);

    /// <summary>Fills <paramref name="list"/> for this frame.</summary>
    public void Fill(CharacterDrawList list)
    {
        float t = time();
        for (int i = 0; i < appearances.Length; i++)
        {
            var p = poses[i];
            for (int k = 0; k < p.Length; k++) p[k] = p[k] with { Time = t + phases[i] };
            list.Add(new CharacterInstance(i + 1, appearances[i], positions[i], yaws[i], p));
        }
    }

    /// <summary>The renderer with a crowd attached (settled when not interactive, so a still shows every character).</summary>
    public static CharacterRenderer Create(GpuContext context, GameInstall install, WorldScene scene, AssetLocator assets, WorldOptions o, bool interactive)
    {
        var db = scene.Database ?? throw new InvalidOperationException("--crowd needs the game data");
        var watch = Stopwatch.StartNew();
        var (records, faction) = Candidates(db, o.Town);
        if (records.Count == 0) throw new InvalidOperationException("--crowd: no CHARACTER records found for the town");
        var random = new Random(o.CrowdSeed);
        int n = o.Crowd;
        var picks = new GameRecord[n];
        double total = records.Sum(r => r.Weight);
        for (int i = 0; i < n; i++)
        {
            double x = random.NextDouble() * total;
            int k = 0;
            while (k < records.Count - 1 && (x -= records[k].Weight) >= 0) k++;
            picks[i] = records[k].Record;
        }
        var appearances = new CharacterAppearance[n];
        var failures = 0;
        Parallel.For(0, n, i =>
        {
            try { appearances[i] = CharacterAppearance.Build(db, install.Root, picks[i].StringId, new CharacterOptions { Seed = o.CrowdSeed * 100003 + i, Faction = faction?.StringId }); }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or IOException or ArgumentException)
            {
                Interlocked.Increment(ref failures);
                Console.WriteLine($"warning   crowd character {i} ({picks[i].Name}): {e.Message}");
            }
        });
        var good = Enumerable.Range(0, n).Where(i => appearances[i] is not null).ToArray();
        var rosterSize = good.Length;
        Console.WriteLine($"crowd     {rosterSize} characters from {records.Count} records of {(faction is null ? "any squad" : $"'{faction.Name}'")} ({failures} failed), generated in {watch.ElapsedMilliseconds} ms");

        var centre = scene.Focus;
        var positions = new Vector3[rosterSize];
        var yaws = new float[rosterSize];
        var phases = new float[rosterSize];
        var poseLists = new CharacterPose[rosterSize][];
        var chosen = new CharacterAppearance[rosterSize];
        for (int j = 0; j < rosterSize; j++)
        {
            // A sunflower spiral round the start point: even spacing at any count.
            float radius = Spacing * MathF.Sqrt(j + 0.5f) * 0.56f, angle = j * 2.3999632f;
            float px = centre.X + radius * MathF.Cos(angle), pz = centre.Z + radius * MathF.Sin(angle);
            positions[j] = new Vector3(px, scene.GroundAt(px, pz), pz);
            yaws[j] = (float)(random.NextDouble() * Math.PI * 2);
            phases[j] = (float)random.NextDouble() * 1.5f;
            chosen[j] = appearances[good[j]];
            poseLists[j] = j % 2 == 0 ? [new CharacterPose(Idle, 0, 1)] : [.. Walk.Select(w => new CharacterPose(w, 0, 1))];
        }
        var clock = Stopwatch.StartNew();
        float fixedTime = o.CrowdTime ?? 0.35f;
        var harness = new CrowdHarness(chosen, positions, yaws, phases, poseLists, interactive || CharacterSwitches.CrowdAnimate ? () => (float)clock.Elapsed.TotalSeconds : () => fixedTime);
        var renderer = new CharacterRenderer(context, install, db, assets) { Source = harness.Fill, LoadBudget = interactive ? 4 : 0 };
        renderer.SettleFirst = !interactive;   // a still waits for everything at its first frame, once the camera is known (the textures' mip streaming needs it)
        return renderer;
    }

    /// <summary>The CHARACTER records the town's residents and bar squads choose from (weighted), and the town's faction.</summary>
    static (List<(GameRecord Record, double Weight)> Records, GameRecord? Faction) Candidates(GameDatabase db, string? townName)
    {
        var result = new List<(GameRecord, double)>();
        GameRecord? faction = null;
        IEnumerable<GameRecord> templates;
        if (townName is not null && db.OfType(FcsRecordType.TOWN).Where(t => t.Name.Contains(townName, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t.Name.Length).FirstOrDefault() is { } town)
        {
            faction = town.GetReferences("faction").Select(r => db.Find(r.TargetStringId)).FirstOrDefault(x => x is not null);
            templates = town.GetReferences("residents").Concat(town.GetReferences("bar squads")).Select(r => db.Find(r.TargetStringId))
                .Where(t => t is { Type: FcsRecordType.SQUAD_TEMPLATE }).Select(t => t!);
        }
        else templates = db.OfType(FcsRecordType.SQUAD_TEMPLATE);
        foreach (var template in templates)
            foreach (var r in template.GetReferences("choosefrom list"))
                if (db.Find(r.TargetStringId) is { Type: FcsRecordType.CHARACTER } character)
                    result.Add((character, Math.Max(r.Values.Value0, 1)));
        return (result, faction);
    }
}
