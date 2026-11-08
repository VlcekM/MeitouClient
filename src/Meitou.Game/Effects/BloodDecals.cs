using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Ogre;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Game.Audio;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Rendering.Particles;
using Meitou.Game.Rendering.Terrain;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// The blood on the ground (docs/game/blood-decals.md): with the game's <c>Blood</c> setting on, the simulation's <see cref="World.BloodMarks"/> become terrain decals in
/// a <see cref="TerrainDecalStore"/> that ages with game time, drawn by the world frame's <see cref="TerrainDecalRenderer"/> within <c>Decal Range</c> at
/// <c>Decal Resolution</c> (the install's <c>settings.cfg</c>). Display only: the simulation is not affected.
/// </summary>
sealed class BloodDecals(GameHost host)
{
    /// <summary>The <c>Blood</c> resource group's folders by decal type (<see cref="TerrainDecalType"/>), the game's names for them.</summary>
    static readonly string[] BloodFolders = ["line", "splash", "pool", "smear"];

    /// <summary>How far above the ground a mark is still left on it (an engine rule: a character on an upper floor of a building bleeds onto the floor, which has no decals).</summary>
    internal const float BloodAboveGround = 20;

    internal TerrainDecalStore? DecalStore;
    World? _bloodFor;
    long _bloodTick;
    double _debrisClock;
    readonly ConcurrentQueue<BloodMark> _bloodQueue = new();
    internal readonly Random BloodRandom = new();

    /// <summary>Makes the store and the renderer when the install's <c>Blood</c> setting is on (missing: on).</summary>
    internal void SetUpBlood(IGpuContext context)
    {
        host.Gpu.Debris ??= new DebrisRenderer(context, host.Gpu.Objects?.Resolver, host.Assets.Find) { SharedTextures = () => host.Gpu.Characters?.Textures };   // made now: its shaders compile here, not at the first burst
        host.Gpu.Particles?.PrepareBursts(Debris.BurstSplash, Debris.BurstSpray, SparksSystem);
        if (host.Gpu.Decals is not null || AudioSystem.Setting(host.Install, "Blood", 1) == 0) return;
        var files = BloodTextureFiles(host.Assets.Configured);
        DecalStore = new TerrainDecalStore([.. files.Select(list => (IReadOnlyList<(int, int)>)[.. list.Select(DdsSize)])]);
        host.Gpu.Decals = TerrainDecalRenderer.Create(context, files);
        host.Gpu.Decals.Meitou = host.WorldOptions.MeitouBlood;
        host.Gpu.Decals.GroundHeight = (x, z) => host.Gpu.Terrain.HeightAt(x, z);
        host.Gpu.Decals.Surfaces = host.WorldOptions.MeitouBloodSurfaces;
        host.Gpu.Decals.Resolution = (int)AudioSystem.Setting(host.Install, "Decal Resolution", 1024, 512, 2048);
        host.Gpu.Decals.Range = AudioSystem.Setting(host.Install, "Decal Range", 1000, 0, 5000);
        host.Gpu.DecalStore = DecalStore;
    }

    /// <summary>
    /// Once a frame: luckyren Mode handed to the world, the marks of the ticks since the last frame into the store (bursts thrown), and the decals aged and the
    /// pieces moved by the game time they took.
    /// </summary>
    internal void UpdateBlood()
    {
        var world = host.Session.World;
        world.Luckyren = host.Config.LuckyrenMode;
        UpdateBlockSparks(world);
        if (DecalStore is null && !host.Config.LuckyrenMode && host.Debris.Pieces.Count == 0)
        {
            world.BloodMarks = null;
            host.Gpu.DebrisPieces.Clear();
            host.Debris.AddBolts();   // the bolts are drawn whatever the Blood setting
            host.Debris.AddTracers(world);   // with their tracers
            host.Debris.UpdateFalls(world);   // and the severed limbs falling
            return;
        }
        if (!ReferenceEquals(_bloodFor, world)) BloodForNewWorld(null);
        world.BloodMarks = _bloodQueue;
        float tick = world.TickSeconds;
        while (_bloodQueue.TryDequeue(out var m))
        {
            if (m.Kind == BloodMarkKind.Explosion)
            {
                host.Debris.Burst(m, world);
                continue;
            }
            if (DecalStore is not { } store) continue;
            uint opaque = 0xFF000000 | Abgr(m.Colour);
            long who = (long)(uint)m.Who.Slot << 32 | (uint)m.Who.Generation;
            if (host.Gpu.Decals is { Surfaces: true } surfaces) SurfaceMark(surfaces, m, who, opaque, tick);   // the walls, floors and objects too, upper floors included
            if (m.Position.Y > host.Gpu.Terrain.HeightAt(m.Position.X, m.Position.Z) + BloodAboveGround) continue;
            switch (m.Kind)
            {
                case BloodMarkKind.Pool: store.Bleed(who, m.Position, m.Amount, tick, opaque, BloodRandom); break;
                case BloodMarkKind.Smear:
                    store.Add(TerrainDecalType.Smear, m.Position, m.Direction, 0.2f, (uint)(Math.Clamp(m.Amount, 0, 1) * 255) << 24 | Abgr(m.Colour), TerrainDecalStore.NewLife, BloodRandom);
                    break;
                case BloodMarkKind.Drip: store.Add(TerrainDecalType.Pool, m.Position, m.Direction, m.Amount, opaque, TerrainDecalStore.NewLife, BloodRandom); break;
                case BloodMarkKind.Splash: store.Add(TerrainDecalType.Splash, m.Position, m.Direction, m.Amount, opaque, TerrainDecalStore.NewLife, BloodRandom); break;
            }
        }
        long ticks = world.Tick - _bloodTick;
        _bloodTick = world.Tick;
        if (ticks > 0 && DecalStore is { } aging && host.Gpu.Decals is { } decals)
        {
            aging.Age(ticks * tick, decals.Drawn);
            decals.AgeSurfaces(ticks * tick);
        }
        // The pieces move by the game time between frames, the tick count plus the simulation's alpha, so they fly smoothly at the display rate, not in tick steps.
        double clock = host.Session.ShownTick + host.Session.SimulationAlpha;   // the shown tick: one run ahead is not on screen yet
        host.Debris.UpdateDebris(world, (float)Math.Clamp((clock - _debrisClock) * tick, 0, 0.5));
        _debrisClock = clock;
    }

    /// <summary>One mark as the <c>bloodsurfaces</c> switch's decals (<see cref="TerrainDecalRenderer.Surfaces"/>); the same marks and sizes as the terrain's.</summary>
    void SurfaceMark(TerrainDecalRenderer surfaces, BloodMark m, long who, uint opaque, float tick)
    {
        switch (m.Kind)
        {
            case BloodMarkKind.Pool: surfaces.BleedSurface(who, m.Position, m.Amount, tick, opaque, BloodRandom); break;
            case BloodMarkKind.Smear:
                surfaces.AddSurface(TerrainDecalType.Smear, m.Position, m.Direction, 0.2f, (uint)(Math.Clamp(m.Amount, 0, 1) * 255) << 24 | Abgr(m.Colour), BloodRandom);
                break;
            case BloodMarkKind.Drip: surfaces.AddSurface(TerrainDecalType.Pool, m.Position, m.Direction, m.Amount, opaque, BloodRandom); break;
            case BloodMarkKind.Splash: surfaces.AddSurface(TerrainDecalType.Splash, m.Position, m.Direction, m.Amount, opaque, BloodRandom); break;
        }
    }

    /// <summary>The EFFECT `small- sparks1` (5463-nodes_furniture1_otto.mod) the original throws on a parry: its particle system.</summary>
    const string SparksSystem = "kenshi_sparks";
    readonly ConcurrentQueue<CharacterId> _sparkQueue = new();

    /// <summary>
    /// Once a frame: the parries of the ticks since the last frame throw <see cref="SparksSystem"/> at the parrying character's <c>Bip01 Prop2</c> (its weapon), 0 or 1 unit higher
    /// at random, when that character is drawn (the original's <c>FUN_1404394f0</c>: only for a character on screen; docs/game/combat.md "Block sparks").
    /// </summary>
    void UpdateBlockSparks(World world)
    {
        if (host.Gpu.Particles is not { } particles || host.Gpu.Characters is not { } characters)
        {
            world.BlockSparks = null;
            return;
        }
        if (!ReferenceEquals(world.BlockSparks, _sparkQueue)) { _sparkQueue.Clear(); world.BlockSparks = _sparkQueue; }
        while (_sparkQueue.TryDequeue(out var who))
            if (characters.BonePosition(host.DrawKey(who), "Bip01 Prop2") is { } at)
                particles.Burst(SparksSystem, at + new Vector3(0, BloodRandom.Next(2), 0), Quaternion.Identity, 0.1f);
    }

    /// <summary>
    /// The store emptied for the session's world and hooked to its marks, then given the decals of the save's TERRAIN_DECALS record <paramref name="saved"/> when the
    /// world was loaded from one (the load calls it before the new world's first frame).
    /// </summary>
    internal void BloodForNewWorld(FcsRecord? saved)
    {
        var world = host.Session.World;
        host.Debris.Pieces.Clear();
        if (host.Gpu is not null) host.Gpu.DebrisPieces.Clear();
        _bloodQueue.Clear();
        _bloodFor = world;
        _bloodTick = world.Tick;
        _debrisClock = host.Session.ShownTick + host.Session.SimulationAlpha;
        if (DecalStore is not { } store) return;
        store.Clear();
        host.Gpu?.Decals?.ClearSurfaces();
        world.BloodMarks = _bloodQueue;
        if (saved is null) return;
        store.Load(SaveDecals.Read(saved));
        host.Gpu?.Decals?.LoadSurfaces(SaveSurfaceDecals.Read(saved));   // the bloodsurfaces switch's marks (Meitou-only keys)
    }

    /// <summary>The store's decals and the surfaces' into the save's TERRAIN_DECALS record (with <c>Blood</c> off the record keeps what the loaded save had).</summary>
    internal void WriteDecals(SaveGame game)
    {
        if (DecalStore is not { } store || game.Decals is not { } record) return;
        SaveDecals.Write(record, store.All);
        if (host.Gpu?.Decals is { } decals) SaveSurfaceDecals.Write(record, decals.SavedSurfaces);
    }

    /// <summary>The default <c>blood colour</c> (RACE, 0x800000), the red the blood particles' textures already are.</summary>
    const uint DefaultBloodColour = 0x800000;

    /// <summary>
    /// The recolour a blood particle burst takes for a race's <c>blood colour</c> <paramref name="rgb"/> (<see cref="ParticleRenderer.Burst"/>): none for the default red, which the
    /// textures show as they are; else the colour scaled as the default would be to full red, so a race's blood is as bright against its hue as red blood is.
    /// </summary>
    internal static Vector3? BloodRecolour(uint rgb) =>
        rgb == DefaultBloodColour ? null : new Vector3(rgb >> 16 & 0xFF, rgb >> 8 & 0xFF, rgb & 0xFF) / 0x80;

    /// <summary>0xRRGGBB as the decal's ABGR colour bytes (alpha 0).</summary>
    internal static uint Abgr(uint rgb) => (rgb >> 16 & 0xFF) | (rgb & 0xFF00) | (rgb & 0xFF) << 16;

    /// <summary>The <c>.dds</c> files of the <c>Blood</c> group's folders, by <see cref="BloodFolders"/> type, in registration order and by name within a folder.</summary>
    static List<IReadOnlyList<string>> BloodTextureFiles(OgreScriptResources resources)
    {
        var files = BloodFolders.Select(_ => new List<string>()).ToList();
        foreach (var location in resources.Locations)
        {
            if (!location.Group.Equals("Blood", StringComparison.Ordinal) || !Directory.Exists(location.Path)) continue;
            int type = Array.FindIndex(BloodFolders, f => f.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(location.Path)), StringComparison.OrdinalIgnoreCase));
            if (type < 0) continue;
            files[type].AddRange(Directory.EnumerateFiles(location.Path, "*.dds").Order(StringComparer.OrdinalIgnoreCase));
        }
        return [.. files.Cast<IReadOnlyList<string>>()];
    }

    /// <summary>A DDS file's size in texels from its header (0 × 0 when unreadable).</summary>
    static (int Width, int Height) DdsSize(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[20];
            using var file = File.OpenRead(path);
            file.ReadExactly(header);
            return (BitConverter.ToInt32(header[16..]), BitConverter.ToInt32(header[12..]));
        }
        catch (IOException) { return (0, 0); }
    }
}