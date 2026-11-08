using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Game.Rendering.Post;

namespace Meitou.Game;

/// <summary>
/// The blood a cut-off head leaves (Meitou decapitation, docs/game/character-stats.md "Decapitation (Meitou)"): with the <c>Blood</c> setting on and a race that bleeds, the
/// neck spurts <see cref="SpurtPulses"/> times, weaker each time, the luckyren burst's spray (<see cref="BurstSpray"/>) thrown out of the neck along the spine (from
/// <c>Bip01 Spine2</c> to <c>Bip01 Neck</c>, as the body is drawn then), each pulse leaving a splash on the ground where it lands. While a pulse emits, its emitter is moved
/// and turned with the neck every frame, so the spray follows the falling body; what it threw stays in the air. Timed by the shown tick (still while paused). Display only,
/// not saved.
/// </summary>
sealed partial class GameHost
{
    sealed class NeckSpurt
    {
        public required long From;
        public required uint Colour;
        /// <summary>The spray's colour for a race whose blood is not red (<see cref="BloodRecolour"/>).</summary>
        public Vector3? Recolour;
        /// <summary>Where the neck was and which way the stump pointed (spine to neck) when the body was last drawn.</summary>
        public required Vector3 Neck;
        public Vector3 Along = Vector3.UnitY;
        /// <summary>The shown tick of the next pulse, and how many are left.</summary>
        public double Next = double.NaN;
        public int Left = SpurtPulses;
        /// <summary>The pulse emitting now (null: none) and its spray's fixed offset from the stump's line.</summary>
        public EffectUnit? Emitting;
        public Vector3 Jitter;
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

    /// <summary>Follows the neck with the pulse emitting now and fires the pulses that are due.</summary>
    void UpdateSpurts()
    {
        if (spurts.Count == 0) return;
        double clock = session.ShownTick + session.SimulationAlpha;
        float tick = session.World.TickSeconds;
        for (int i = spurts.Count - 1; i >= 0; i--)
        {
            var s = spurts[i];
            if (double.IsNaN(s.Next)) s.Next = clock;
            TrackNeck(s);
            if (s.Left > 0 && clock >= s.Next)
            {
                Pulse(s);
                s.Next += SpurtGap / MathF.Max(tick, 1e-4f);
                s.Left--;
            }
            else if (s.Emitting is { Stopped: false } unit) gpu.Particles?.MoveBurst(unit, s.Neck, Turn(Vector3.UnitZ, SprayDirection(s)));
            // Done once the last pulse has emitted (or a gap after it: particles turned off stop the units' clock).
            if (s.Left <= 0 && (s.Emitting is null or { Stopped: true } || clock >= s.Next)) spurts.RemoveAt(i);
        }
    }

    /// <summary>The neck and the stump's direction as the body was drawn last; kept from the last time while it is not drawn.</summary>
    void TrackNeck(NeckSpurt s)
    {
        if (gpu.Characters is not { } characters || characters.BonePosition(s.From, "Bip01 Neck") is not { } neck) return;
        s.Neck = neck;
        if (characters.BonePosition(s.From, "Bip01 Spine2") is { } spine && Vector3.DistanceSquared(neck, spine) > 1e-4f) s.Along = Vector3.Normalize(neck - spine);
    }

    /// <summary>Thrown out of the neck, a little off the spine's line, and upward enough to arc.</summary>
    static Vector3 SprayDirection(NeckSpurt s) => Vector3.Normalize(s.Along + s.Jitter + new Vector3(0, 0.3f, 0));

    void Pulse(NeckSpurt s)
    {
        float strength = s.Left / (float)SpurtPulses;
        s.Jitter = new Vector3(bloodRandom.NextSingle() - 0.5f, bloodRandom.NextSingle() * 0.5f, bloodRandom.NextSingle() - 0.5f) * 0.6f;
        var dir = SprayDirection(s);
        s.Emitting = gpu.Particles?.Burst(BurstSpray, s.Neck, Turn(Vector3.UnitZ, dir), 0.15f + 0.25f * strength, 0.8f + 0.6f * strength, 1.5f, s.Recolour);
        SpurtSplash(s, dir, strength);
    }

    /// <summary>The pulse's splash on the ground, out along the spray, nearer for a weaker pulse.</summary>
    void SpurtSplash(NeckSpurt s, Vector3 dir, float strength)
    {
        if (decalStore is not { } store) return;
        var flat = new Vector2(dir.X, dir.Z);
        flat = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : RandomDirection();
        float far = SpurtReach * strength * (0.5f + bloodRandom.NextSingle() * 0.5f);
        var at = s.Neck + new Vector3(flat.X, 0, flat.Y) * far;
        at.Y = session.World.Walkability.GroundHeight(at.X, at.Z, s.Neck.Y);
        float size = 0.15f + 0.25f * strength;
        if (gpu.Decals is { Surfaces: true } surfaces) surfaces.AddSurface(TerrainDecalType.Splash, at, flat, size, s.Colour, bloodRandom);
        if (at.Y <= gpu.Terrain.HeightAt(at.X, at.Z) + BloodAboveGround) store.Add(TerrainDecalType.Splash, at, flat, size, s.Colour, TerrainDecalStore.NewLife, bloodRandom);
    }
}
