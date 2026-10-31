using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The blood a cut-off head leaves (Meitou decapitation, docs/game/character-stats.md "Decapitation (Meitou)"): with the <c>Blood</c> setting on and a race that bleeds, the
/// neck spurts <see cref="SpurtPulses"/> times, weaker each time, the luckyren burst's spray (<see cref="BurstSpray"/>) thrown out of the neck along the spine (from
/// <c>Bip01 Spine2</c> to <c>Bip01 Neck</c>, as the body is drawn then, so it follows the falling body), each pulse leaving a splash on the ground where it lands. Timed by the
/// shown tick (still while paused). Display only, not saved.
/// </summary>
sealed partial class GameHost
{
    sealed class NeckSpurt
    {
        public required long From;
        public required uint Colour;
        /// <summary>The spray's colour for a race whose blood is not red (<see cref="BloodRecolour"/>).</summary>
        public Vector3? Recolour;
        /// <summary>Where the neck was when it was cut (used while the body is not drawn).</summary>
        public required Vector3 Neck;
        /// <summary>The shown tick of the next pulse, and how many are left.</summary>
        public double Next = double.NaN;
        public int Left = SpurtPulses;
    }

    readonly List<NeckSpurt> spurts = [];
    readonly Dictionary<string, RaceData> bloodRaces = new(StringComparer.Ordinal);
    const int SpurtPulses = 6;
    /// <summary>Seconds between pulses, and how far (units) the first pulse's blood lands from the neck.</summary>
    const float SpurtGap = 0.35f, SpurtReach = 18;

    /// <summary>Starts the neck's spurts for the character drawn as <paramref name="from"/>, whose head was at <paramref name="head"/>.</summary>
    void Spurt(long from, CharacterAppearance look, Vector3 head)
    {
        if (decalStore is null || scene.Database is not { } db) return;
        if (!bloodRaces.TryGetValue(look.Race.StringId, out var race)) bloodRaces[look.Race.StringId] = race = RaceData.From(look.Race, db);
        if (race.BleedRate <= 0) return;
        var recolour = BloodRecolour(race.BloodColour);
        spurts.Add(new NeckSpurt { From = from, Colour = 0xFF000000 | Abgr(race.BloodColour), Recolour = recolour, Neck = gpu.Characters?.BonePosition(from, "Bip01 Neck") ?? head });
        gpu.Particles?.Burst(BurstSplash, head, Quaternion.Identity, 0.2f, 1.2f, 1.5f, recolour);
    }

    /// <summary>Fires the pulses that are due.</summary>
    void UpdateSpurts()
    {
        if (spurts.Count == 0) return;
        double clock = session.ShownTick + session.SimulationAlpha;
        float tick = session.World.TickSeconds;
        for (int i = spurts.Count - 1; i >= 0; i--)
        {
            var s = spurts[i];
            if (double.IsNaN(s.Next)) s.Next = clock;
            if (clock < s.Next) continue;
            Pulse(s);
            s.Next += SpurtGap / MathF.Max(tick, 1e-4f);
            if (--s.Left <= 0) spurts.RemoveAt(i);
        }
    }

    void Pulse(NeckSpurt s)
    {
        var characters = gpu.Characters;
        var neck = characters?.BonePosition(s.From, "Bip01 Neck") ?? s.Neck;
        var along = characters?.BonePosition(s.From, "Bip01 Spine2") is { } spine && Vector3.DistanceSquared(neck, spine) > 1e-4f ? Vector3.Normalize(neck - spine) : Vector3.UnitY;
        float strength = s.Left / (float)SpurtPulses;
        // Thrown out of the neck, a little off the spine's line, and upward enough to arc.
        var jitter = new Vector3(bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() * 0.5f, bloodRandom.NextSingle() - 0.5f) * 0.6f;
        var dir = Vector3.Normalize(along + jitter + new Vector3(0, 0.3f, 0));
        gpu.Particles?.Burst(BurstSpray, neck, Turn(Vector3.UnitZ, dir), 0.15f + 0.25f * strength, 0.8f + 0.6f * strength, 1.5f, s.Recolour);
        if (decalStore is not { } store) return;
        var flat = new Vector2(dir.X, dir.Z);
        flat = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : RandomDirection();
        float far = SpurtReach * strength * (0.5f + bloodRandom.NextSingle() * 0.5f);
        var at = neck + new Vector3(flat.X, 0, flat.Y) * far;
        at.Y = session.World.Walkability.GroundHeight(at.X, at.Z, neck.Y);
        float size = 0.15f + 0.25f * strength;
        if (gpu.Decals is { Surfaces: true } surfaces) surfaces.AddSurface(TerrainDecalType.Splash, at, flat, size, s.Colour, bloodRandom);
        if (at.Y <= gpu.Terrain.HeightAt(at.X, at.Z) + BloodAboveGround) store.Add(TerrainDecalType.Splash, at, flat, size, s.Colour, TerrainDecalStore.NewLife, bloodRandom);
    }
}
