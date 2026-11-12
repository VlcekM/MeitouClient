using System.Globalization;
using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

/// <summary>
/// The options of <c>meitou-tools navmesh</c> (<see cref="NavmeshTool"/>), as given on the command line.
/// </summary>
sealed class NavmeshOptions
{
    /// <summary>
    /// <c>--zone x,y</c>: the zone to build; null builds the zone of <see cref="Town"/>.
    /// </summary>
    public ZoneCoordinate? Zone;

    /// <summary>
    /// <c>--town</c>, <c>--obj</c>, <c>--png</c>: the town whose zone is built (part of its name, The Hub by default), and the OBJ and PNG
    /// files to write.
    /// </summary>
    public string? Town, Obj, Png;

    /// <summary>
    /// <c>--scale</c>: the PNG's world units per pixel.
    /// </summary>
    public float UnitsPerPixel = 4;

    /// <summary>
    /// <c>--geometry</c>: write the gathered geometry, build nothing.
    /// </summary>
    public bool GeometryOnly;

    /// <summary>
    /// The build settings, changed by <c>--climb</c>, <c>--cell</c>, <c>--tile</c> and the other build options.
    /// </summary>
    public NavBuildSettings Settings = new();

    /// <summary>
    /// <c>--repeat</c>: how many times the centre zone is built, for timing.
    /// </summary>
    public int Repeat = 1;

    /// <summary>
    /// <c>--box x0,z0,x1,z1</c> (the PNG's and the bridges' box) and <c>--path x0,z0,x1,z1</c> (a path to find).
    /// </summary>
    public float[]? Box, PathArg;

    /// <summary>
    /// <c>--closed</c>: the path's agent finds doors closed; <c>--no-interiors</c>: building interiors are not built.
    /// </summary>
    public bool DoorsClosed, NoInteriors;

    /// <summary>
    /// <c>--to-y</c>, <c>--from-y</c>: the path's goal and start heights instead of the ground's.
    /// </summary>
    public float? ToY, FromY;

    /// <summary>
    /// <c>--near x,z,radius</c>, <c>--probe</c> (its first point's numbers) and <c>--route x0,z0,x1,z1</c>.
    /// </summary>
    public float[]? Near, Probe, Route;

    /// <summary>
    /// <c>--probe</c> as given: points <c>x,z</c> separated by <c>;</c>, or one <c>x,z,radius,step</c>.
    /// </summary>
    public string? ProbeArg;

    /// <summary>
    /// <c>--around n</c>: the zones within n of the centre are built and resolved with it.
    /// </summary>
    public int Around;

    /// <summary>
    /// <c>--fingerprint</c>, <c>--feature-tops</c>, <c>--regions</c>, <c>--base</c> (the base game's files only) and <c>--ragdoll</c>.
    /// </summary>
    public bool Fingerprint, FeatureTops, Regions, BaseGame, Ragdoll;

    /// <summary>
    /// <c>--interior key</c>: one building's interior to build alone.
    /// </summary>
    public string? InteriorKey;

    /// <summary>
    /// <c>--compare-interiors towns</c>: every town's zone is compared too.
    /// </summary>
    public bool CompareTowns;

    /// <summary>
    /// <c>--own-only</c>: the interior is gathered from its own building only.
    /// </summary>
    public bool OwnOnly;

    /// <summary>
    /// The zones of <c>--compare</c>, <c>--compare-interiors</c> and <c>--survey-interiors</c>.
    /// </summary>
    public readonly List<ZoneCoordinate> Compare = [], CompareInteriors = [], Survey = [];

    /// <summary>
    /// <c>--survey-interiors towns</c>: every town's zone is surveyed too.
    /// </summary>
    public bool SurveyTowns;

    /// <summary>
    /// <c>--name</c>: the survey's name filter (<see cref="InteriorSurvey"/>).
    /// </summary>
    public string? SurveyName;

    /// <summary>
    /// <c>--border</c>: the survey takes hulls over the zone's border too.
    /// </summary>
    public bool SurveyBorder;

    /// <summary>
    /// <c>--section x,z</c>: where the survey puts its vertical sections; the hull's middle when null.
    /// </summary>
    public Vector2? Section;

    /// <summary>
    /// <c>--verbose</c>: everything is listed in detail.
    /// </summary>
    public bool Verbose;

    /// <summary>
    /// The options of <paramref name="args"/>; null, the option named on the error stream, for an unknown one.
    /// </summary>
    public static NavmeshOptions? Parse(string[] args)
    {
        var o = new NavmeshOptions();
        for (int i = 0; i < args.Length; i++)
        {
            if (o.Apply(args, ref i)) continue;
            Console.Error.WriteLine($"Unknown option {args[i]}");
            return null;
        }
        return o;
    }

    /// <summary>
    /// Takes the option at <paramref name="i"/> and its values, leaving <paramref name="i"/> on its last; false when it is no option.
    /// </summary>
    bool Apply(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--zone":
            {
                var p = args[++i].Split(',');
                Zone = new(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture));
                break;
            }
            case "--town": Town = args[++i]; break;
            case "--obj": Obj = args[++i]; break;
            case "--png": Png = args[++i]; break;
            case "--scale": UnitsPerPixel = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--geometry": GeometryOnly = true; break;
            case "--near": Near = Floats(args[++i]); break;
            case "--no-interiors": NoInteriors = true; break;
            case "--probe":
                ProbeArg = args[++i];
                Probe = Floats(ProbeArg.Split(';')[0]);
                break;
            case "--to-y": ToY = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--from-y": FromY = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--closed": DoorsClosed = true; break;
            case "--around": Around = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--path": PathArg = Floats(args[++i]); break;
            case "--repeat": Repeat = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--fingerprint": Fingerprint = true; break;
            case "--feature-tops": FeatureTops = true; break;
            case "--regions": Regions = true; break;
            case "--base": BaseGame = true; break;
            case "--ragdoll": Ragdoll = true; break;
            case "--route": Route = Floats(args[++i]); break;
            case "--compare": ReadZones(args, ref i, Compare); break;
            case "--compare-interiors": CompareTowns |= ReadZonesOrTowns(args, ref i, CompareInteriors); break;
            case "--survey-interiors": SurveyTowns |= ReadZonesOrTowns(args, ref i, Survey); break;
            case "--name": SurveyName = args[++i]; break;
            case "--border": SurveyBorder = true; break;
            case "--section":
            {
                var p = args[++i].Split(',');
                Section = new(float.Parse(p[0], CultureInfo.InvariantCulture), float.Parse(p[1], CultureInfo.InvariantCulture));
                break;
            }
            case "--verbose": Verbose = true; break;
            case "--interior": InteriorKey = args[++i]; break;
            case "--own-only": OwnOnly = true; break;
            case "--box": Box = Floats(args[++i]); break;
            default: return ApplyBuildSetting(args, ref i);
        }
        return true;
    }

    /// <summary>
    /// Takes a build setting option at <paramref name="i"/> and its value into <see cref="Settings"/>; false when it is none.
    /// </summary>
    bool ApplyBuildSetting(string[] args, ref int i)
    {
        switch (args[i])
        {
            case "--climb": Settings = Settings with { MaxClimb = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--cell": Settings = Settings with { CellSize = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--cell-height": Settings = Settings with { CellHeight = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--tile": Settings = Settings with { TileCells = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--edge": Settings = Settings with { MaxEdgeLength = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--monotone": Settings = Settings with { Watershed = false }; break;
            case "--min-region": Settings = Settings with { MinRegionArea = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--merge-region": Settings = Settings with { MergeRegionArea = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
            case "--simplify":
                Settings = Settings with { MaxSimplificationError = float.Parse(args[++i], CultureInfo.InvariantCulture) };
                break;
            default: return false;
        }
        return true;
    }

    /// <summary>
    /// Comma-separated numbers.
    /// </summary>
    static float[] Floats(string text) => text.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>
    /// The zones <c>x,y</c> following the option, up to the next option.
    /// </summary>
    static void ReadZones(string[] args, ref int i, List<ZoneCoordinate> zones)
    {
        while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            var p = args[++i].Split(',');
            zones.Add(new(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// The zones <c>x,y</c> following the option, up to the next option; true when one of them is <c>towns</c> (every town's zone).
    /// </summary>
    static bool ReadZonesOrTowns(string[] args, ref int i, List<ZoneCoordinate> zones)
    {
        bool towns = false;
        while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            if (args[++i] == "towns")
            {
                towns = true;
                continue;
            }
            var p = args[i].Split(',');
            zones.Add(new(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)));
        }
        return towns;
    }
}
