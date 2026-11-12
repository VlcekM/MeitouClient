using System.Numerics;
using Meitou.Game.Rendering.Frame;

namespace Meitou.ModelViewer;

/// <summary>
/// The camera motions of the offscreen benchmarks, one per frame: the flight round a circle (<c>--fly-benchmark</c>, <c>--bench-motion fly</c>), the
/// pure turn about the eye (<c>MEITOU_FLY_TURN</c>, <c>--bench-motion turn</c>) and the orbit round the target (<c>MEITOU_BENCH_ORBIT</c>,
/// <c>--bench-motion orbit</c>); the orbit also adds to the others.
/// </summary>
sealed class CameraMotion(WorldCamera camera, WorldGpu gpu, Vector3 centre, bool circle, float radius, float unitsPerFrame, float turnRadians,
        float orbitPixels)
{
    /// <summary>
    /// The flight's turn round the circle per frame, radians (the speed over the radius, at least 1).
    /// </summary>
    readonly float _angleStep = unitsPerFrame / Math.Max(radius, 1);

    /// <summary>
    /// The motion <c>--bench-motion</c> names.
    /// </summary>
    public static CameraMotion For(string name, WorldOptions o, WorldCamera camera, WorldGpu gpu) => new(camera, gpu, camera.Target, name == "fly",
            Math.Max(o.FlyRadius, 1), o.FlySpeed,
        name == "turn" ? 0.5f * MathF.PI / 180 : 0, name == "orbit" ? o.BenchOrbit : 0);   // the turn: 0.5 degrees a frame

    /// <summary>
    /// The old fly benchmark's: the circle, or the turn when MEITOU_FLY_TURN gives a rate, plus the orbit of MEITOU_BENCH_ORBIT.
    /// </summary>
    public static CameraMotion Fly(WorldOptions o, WorldCamera camera, WorldGpu gpu, float turnRadians, float orbitPixels) =>
        new(camera, gpu, camera.Target, turnRadians == 0, Math.Max(o.FlyRadius, 1), o.FlySpeed, turnRadians, orbitPixels);

    /// <summary>
    /// Whether the motion moves the camera at all (a circle, a turn or an orbit).
    /// </summary>
    public bool Moves => circle || turnRadians != 0 || orbitPixels != 0;

    /// <summary>
    /// Moves the camera to its place at frame <paramref name="i"/> (1-based; the circle starts at its east point).
    /// </summary>
    public void Step(int i)
    {
        // a speed of 0 leaves the camera exactly where it was placed (else its target drops to the ground, a pose of its own).
        if (circle
            && _angleStep != 0)
        {
            float a = i * _angleStep;
            float x = centre.X + radius * (MathF.Cos(a) - 1), z = centre.Z + radius * MathF.Sin(a);
            camera.Target = new Vector3(x, gpu.Terrain.HeightAt(x, z), z);
        }
        if (turnRadians != 0) camera.Look(turnRadians / 0.004f, 0);   // a pure turn about the eye
        if (orbitPixels != 0) camera.Orbit(orbitPixels, 0);
    }
}
