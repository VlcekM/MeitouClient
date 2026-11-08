using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Terrain;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// The birds (docs/game/birds.md): they fly on the display's real time (the original's <c>BirdManager</c> thread is kicked with the real dt), and the flocks
/// are those the camera's BIOME_GROUP lists.
/// </summary>
sealed class Birds(GameHost host)
{
    #region Flight

    BirdFlocks? _flocks;
    Data.Gameplay.BiomeMap? _areas;
    BirdThread? _step;
    bool _started;
    double _clock = double.NaN;
    readonly TerrainHeights _heights = new();

    // The inputs of the step running beside the frame.
    float _dt;
    Vector3 _eye, _focus;
    Vector4[]? _planes;

    /// <summary>
    /// Once a frame: shows the flocks of the last step and starts the next one beside the frame. Its flocks are shown one frame late (the original's
    /// thread is as loose), and a step still running when the next frame starts is left to finish. Stills and the duel step in the frame.
    /// </summary>
    internal void UpdateBirds(float aspect)
    {
        if (host.Scene.Database is not { } db)
        {
            host.Gpu.BirdPieces.Clear();
            return;
        }

        var flocks = _flocks ?? CreateFlocks(db);
        bool async = host.WorldOptions.Screenshot is null && !host.Duel.IsDuel;
        if (_step!.Busy)
            return;
        if (_started && async)
            ShowFlocks(flocks);

        UpdateAttractors(db);
        SetStepInputs(aspect);
        _started = true;

        if (async)
        {
            _step.Start();
        }
        else
        {
            flocks.Update(_dt, _eye, _focus, _planes);
            ShowFlocks(flocks);
        }
    }

    /// <summary>
    /// Reads the flock kinds and the weather areas (none when their file is missing or broken), and makes the step's thread.
    /// </summary>
    BirdFlocks CreateFlocks(Data.GameDatabase db)
    {
        var kinds = Data.Gameplay.BirdFlockData.LoadAll(db);
        try
        {
            _areas = Data.Gameplay.BiomeMap.Create(Data.Gameplay.BiomeGroupData.LoadAll(db), Data.World.WeatherAreas.Load(host.Install));
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
            _areas = null;
        }

        var flocks = _flocks = new BirdFlocks(kinds, _heights.HeightAt, GroupIdOfCell);
        _step = new BirdThread(() => flocks.Update(_dt, _eye, _focus, _planes));
        return flocks;
    }

    /// <summary>
    /// Hands the flocks' pieces to the renderer; a mesh is loaded when first asked for.
    /// </summary>
    void ShowFlocks(BirdFlocks flocks)
    {
        var gpu = host.Gpu;
        gpu.BirdPieces.Clear();
        flocks.AddPieces(gpu.BirdPieces, mesh => gpu.Debris is { } birdMeshes && birdMeshes.Extent(mesh, null) is not null);
    }

    void SetStepInputs(float aspect)
    {
        double now = host.RealTime.Elapsed.TotalSeconds;
        _dt = double.IsNaN(_clock) ? 0 : (float)(now - _clock);
        _clock = now;

        _eye = host.Camera.Eye;
        _focus = host.Camera.Target;
        _planes = Frustum(aspect);
        _heights.Refresh(host.Gpu.Terrain);
    }

    string? GroupIdOfCell(int cx, int cz) => _areas is { } areas && areas.GroupOf(cx, cz) is >= 0 and var g ? areas.Groups[g].Id : null;

    /// <summary>
    /// The view's six planes, normals inwards and normalised (Ogre's cached frustum planes, as the birds' distance test takes them).
    /// </summary>
    Vector4[] Frustum(float aspect)
    {
        var camera = host.Camera;
        var planes = WorldCamera.FrustumPlanes(camera.View * camera.Projection(aspect, camera.Near, camera.ViewDistance));
        for (int i = 0; i < planes.Length; i++)
            planes[i] /= new Vector3(planes[i].X, planes[i].Y, planes[i].Z).Length();

        return planes;
    }

    #endregion

    #region Attractors

    long _buildingsRevision = -1;
    object? _attractorsFor;
    Task<List<(string Key, Data.GameRecord Record, Vector3 Position)>>? _levelSources;
    readonly List<BirdAttractor> _buildingAttractors = [], _levelAttractors = [];
    bool _hadAnimals;

    /// <summary>
    /// The <c>bird attractor</c> references of the objects the original registers them for (<c>FUN_140263700</c>, on a building's, map feature's or animal's
    /// creation): the player's standing buildings, the level's buildings and map features (read once off the main thread) and every animal, which carries its
    /// own as it moves.
    /// </summary>
    void UpdateAttractors(Data.GameDatabase db)
    {
        var snapshot = host.Session.CurrentSnapshot;
        bool changed = ResetForNewWorld();
        changed |= TakeLevelAttractors();
        changed |= UpdateBuildingAttractors(db, snapshot);

        var animals = new List<BirdAttractor>();
        foreach (var c in snapshot.Characters)
        {
            if (c.Animal && db.Find(c.RecordId) is { } animal)
                AddAttractors(animals, c.Id, animal, c.Position);
        }

        if (changed || animals.Count > 0 || _hadAnimals)
            _flocks!.SetAttractors([.. _buildingAttractors, .. _levelAttractors, .. animals]);

        _hadAnimals = animals.Count > 0;
    }

    /// <summary>
    /// A new world starts reading its level's attractors and lists its buildings again.
    /// </summary>
    bool ResetForNewWorld()
    {
        if (ReferenceEquals(_attractorsFor, host.Session.World))
            return false;

        _attractorsFor = host.Session.World;
        _levelAttractors.Clear();
        _levelSources = host.Scene.Objects is { } objects ? Task.Run(objects.BirdAttractorSources) : null;
        _buildingsRevision = -1;
        return true;
    }

    bool TakeLevelAttractors()
    {
        if (_levelSources is not { IsCompletedSuccessfully: true } read)
            return false;

        foreach (var (key, record, at) in read.Result)
            AddAttractors(_levelAttractors, key, record, at);

        _levelSources = null;
        return true;
    }

    bool UpdateBuildingAttractors(Data.GameDatabase db, WorldSnapshot snapshot)
    {
        if (snapshot.BuildingsRevision == _buildingsRevision)
            return false;

        _buildingsRevision = snapshot.BuildingsRevision;
        _buildingAttractors.Clear();
        foreach (var building in snapshot.Buildings)
        {
            if (building.Complete && db.Find(building.Record) is { } record)
                AddAttractors(_buildingAttractors, building.Id, record, building.Position);
        }

        return true;
    }

    static void AddAttractors(List<BirdAttractor> into, object owner, Data.GameRecord record, Vector3 at)
    {
        foreach (var link in record.GetReferences("bird attractor"))
            into.Add(new BirdAttractor((owner, link.TargetStringId), link.TargetStringId, at, link.Values.Value0, link.Values.Value1));
    }

    #endregion
}
