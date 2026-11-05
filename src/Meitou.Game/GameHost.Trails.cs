using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Audio;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Gpu;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The ground trails (docs/render-terrain.md "Ground trails"): with the <c>trails</c> switch on, the characters near the eye press marks into the soft ground, read from the
/// shown snapshots and the posed feet only. Display only: the simulation is not affected and nothing is saved.
/// </summary>
sealed partial class GameHost
{
    /// <summary>A foot counts as planted while it moves under this share of the body's speed (it stands still in the world) and as lifted over the second (it swings past).</summary>
    const float PlantedShare = 0.4f, LiftedShare = 1.2f;

    /// <summary>How far above the terrain a mark is still left: an ankle's height; higher is a floor or a roof.</summary>
    const float MarkAboveGround = 2.5f;

    /// <summary>A sole's length; a person's stride (when its feet were not posed: it was not drawn) and how far beside the path a foot lands.</summary>
    const float SoleLength = 2.6f, PersonStride = 4.5f, PersonGait = 0.6f;

    /// <summary>An animal's print, stride and gait at scale 1 (the rigs differ too much to find their feet).</summary>
    const float PawLength = 1.6f, AnimalStride = 5, AnimalGait = 1.2f;

    /// <summary>The furrow of a crawling or sliding body: one every this many units, its length and width.</summary>
    const float DragStep = 1.2f, DragLength = 3, DragWidth = 3;

    /// <summary>How deep packed dirt takes a mark, against sand, mud and snow.</summary>
    const float DirtMark = 0.45f;

    IGpuContext? trailContext;
    World? trailsFor;
    int trailFrame;
    readonly Dictionary<long, Walker> walkers = [];

    /// <summary>A character's last frame: where its body and feet were, whether each foot is in the air, and the way walked since the last mark.</summary>
    struct Walker
    {
        public Vector3 Body, Left, Right;
        public bool LeftLifted, RightLifted, LeftNext;
        public float Walked;
        public int Frame;
    }

    /// <summary>Once a frame, before the world is drawn: the marks the characters near the eye press this frame.</summary>
    void UpdateTrails()
    {
        if (!TerrainTrails.Enabled || trailContext is null || scene.Database is not { } db)
        {
            walkers.Clear();
            return;
        }
        var trails = gpu.Trails ??= new TerrainTrailRenderer(trailContext);
        var ground = groundSurfaces ??= new GroundSurfaces(install, db);
        if (!ReferenceEquals(trailsFor, session.World))
        {
            trailsFor = session.World;
            trails.Clear();
            walkers.Clear();
        }
        trailFrame++;
        IndexPrevious();
        var eye = camera.Eye;
        float alpha = session.SimulationAlpha;
        foreach (var c in session.CurrentSnapshot.Characters)
        {
            var flat = new Vector2(c.Position.X - eye.X, c.Position.Z - eye.Z);
            if (flat.LengthSquared() > TerrainTrailRenderer.Reach * TerrainTrailRenderer.Reach || c.Appearance is null) continue;
            if (!c.CarriedBy.IsNone || c.InBed || c.Imprisoned) continue;
            WalkTrail(trails, ground, c, previousById.TryGetValue(c.Id, out var before) ? Vector3.Lerp(before.Position, c.Position, alpha) : c.Position);
        }
        if (trailFrame % 256 == 0)
            foreach (var (key, w) in walkers)
                if (w.Frame != trailFrame) walkers.Remove(key);
    }

    /// <summary>One character's frame: nothing while it stands (or the game is paused), else a furrow, its posed feet, or its stride.</summary>
    void WalkTrail(TerrainTrailRenderer trails, GroundSurfaces ground, CharacterSnapshot c, Vector3 body)
    {
        long key = DrawKey(c.Id);
        ref var w = ref CollectionsMarshal.GetValueRefOrAddDefault(walkers, key, out bool known);
        bool continues = known && w.Frame == trailFrame - 1;
        var moved = new Vector2(body.X - w.Body.X, body.Z - w.Body.Z);
        (w.Body, w.Frame) = (body, trailFrame);
        float stepped = moved.Length();
        if (!continues || stepped < 1e-4f) return;
        var along = moved / stepped;
        bool animal = c.Appearance!.Animal is not null;
        if (c.Down || c.StayingLow || c.Body is { Crawling: true }) Drag(trails, ground, ref w, body, along, stepped);
        else if (!animal && gpu.Characters is { } characters && characters.BonePosition(key, "Bip01 L Foot") is { } left && characters.BonePosition(key, "Bip01 R Foot") is { } right)
        {
            Foot(trails, ground, key, ref w.Left, ref w.LeftLifted, left, "Bip01 L Toe0", along, stepped);
            Foot(trails, ground, key, ref w.Right, ref w.RightLifted, right, "Bip01 R Toe0", along, stepped);
        }
        else Stride(trails, ground, ref w, body, along, stepped, animal ? c.Scale : 1, animal);
    }

    /// <summary>A posed foot: a mark at the frame it comes down, turned along its toes; a foot stands still in the world while the body walks over it, whatever the rig.</summary>
    void Foot(TerrainTrailRenderer trails, GroundSurfaces ground, long key, ref Vector3 last, ref bool lifted, Vector3 ankle, string toeBone, Vector2 along, float stepped)
    {
        float moved = new Vector2(ankle.X - last.X, ankle.Z - last.Z).Length();
        last = ankle;
        if (moved > stepped * LiftedShare) lifted = true;
        if (!lifted || moved >= stepped * PlantedShare) return;
        lifted = false;
        if (gpu.Characters!.BonePosition(key, toeBone) is { } toe && new Vector2(toe.X - ankle.X, toe.Z - ankle.Z) is { } foot && foot.LengthSquared() > 0.04f)
            along = Vector2.Normalize(foot);
        var sole = new Vector2(ankle.X, ankle.Z) + along * (SoleLength * 0.3f);
        Press(trails, ground, ankle, new TrailStamp(sole, along, SoleLength, SoleLength, 1, TrailKind.Foot));
    }

    /// <summary>A character whose feet are not known: a print left and right of its path, a stride apart.</summary>
    void Stride(TerrainTrailRenderer trails, GroundSurfaces ground, ref Walker w, Vector3 body, Vector2 along, float stepped, float scale, bool animal)
    {
        w.Walked += stepped;
        if (w.Walked < (animal ? AnimalStride : PersonStride) * scale) return;
        w.Walked = 0;
        w.LeftNext = !w.LeftNext;
        var side = new Vector2(along.Y, -along.X) * ((animal ? AnimalGait : PersonGait) * scale * (w.LeftNext ? 1 : -1));
        float length = (animal ? PawLength : SoleLength) * scale;
        Press(trails, ground, body, new TrailStamp(new Vector2(body.X, body.Z) + side, along, length, length, 1, animal ? TrailKind.Paw : TrailKind.Foot));
    }

    void Drag(TerrainTrailRenderer trails, GroundSurfaces ground, ref Walker w, Vector3 body, Vector2 along, float stepped)
    {
        w.Walked += stepped;
        if (w.Walked < DragStep) return;
        w.Walked = 0;
        Press(trails, ground, body, new TrailStamp(new Vector2(body.X, body.Z), along, DragLength, DragWidth, 1, TrailKind.Drag));
    }

    /// <summary>Queues a mark where the ground takes one: soft, above the water, and right under what pressed it (not from a floor or a roof).</summary>
    void Press(TerrainTrailRenderer trails, GroundSurfaces ground, Vector3 at, in TrailStamp stamp)
    {
        float height = gpu.Terrain.HeightAt(stamp.Centre.X, stamp.Centre.Y);
        if (at.Y - height > MarkAboveGround || height < WorldWater.Height) return;
        float soft = ground.At(stamp.Centre.X, stamp.Centre.Y) switch
        {
            GroundSurfaces.Sand or GroundSurfaces.Mud or GroundSurfaces.Snow => 1,
            GroundSurfaces.Dirt => DirtMark,
            _ => 0,
        };
        if (soft > 0) trails.Stamp(stamp with { Strength = stamp.Strength * soft });
    }
}
