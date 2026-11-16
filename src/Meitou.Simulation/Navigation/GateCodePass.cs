using System.Globalization;
using System.Numerics;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The numbering of the gate-code pass (<c>FUN_1402ef4a0</c>, docs/game/pathfinding.md "Gate codes"): each gate's first and second code, and the
/// codes flooded into the faces.
/// </summary>
internal sealed class GateCodePass
{
    /// <summary>
    /// The gates in list order.
    /// </summary>
    readonly List<GateSide> _gates;
    /// <summary>
    /// The connection test and the flood.
    /// </summary>
    readonly GateCodeFlood _flood;
    /// <summary>
    /// The codes that failed to flood since the last reset.
    /// </summary>
    readonly List<int> _failed = [];

    /// <summary>
    /// A pass over <paramref name="world"/> for <paramref name="gates"/>; its connection tests keep <paramref name="footprint"/> clear.
    /// </summary>
    public GateCodePass(NavWorld world, List<GateSide> gates, float footprint)
    {
        _gates = gates;
        _flood = new GateCodeFlood(world, footprint);
        First = new int[gates.Count];
        Second = new int[gates.Count];
    }

    /// <summary>
    /// Each gate's first code (GateData <c>+0x38</c>, the door's <c>+0x378</c> once applied).
    /// </summary>
    public int[] First { get; }

    /// <summary>
    /// Each gate's second code (GateData <c>+0x3c</c>, the door's and the building's own code once applied).
    /// </summary>
    public int[] Second { get; }

    /// <summary>
    /// The faces' codes by polygon key.
    /// </summary>
    public Dictionary<long, int> Codes { get; } = [];

    /// <summary>
    /// The pass's log lines.
    /// </summary>
    public List<string> Log { get; } = [];

    /// <summary>
    /// Runs the steps in the original's order.
    /// </summary>
    public void Run()
    {
        EnabledTest();
        InteriorRegionTest();
        FloodAll(First, skipUnset: true);
        SecondCodes();
        int next = UpdateCodes();
        ExternalCodeFix();
        FloodAll(Second, skipUnset: false);
        if (_failed.Count == 1) SwapWithZero(next);
        ZeroFailed();
    }

    /// <summary>
    /// "Enabled test": a gate whose two sides connect under the gate filter gets first code -1, any other the next number from 1.
    /// </summary>
    void EnabledTest()
    {
        int next = 0;
        for (int i = 0; i < _gates.Count; i++)
            First[i] = _flood.Connected(_gates[i].First, _gates[i].Second) ? -1 : ++next;
    }

    /// <summary>
    /// "Interior region test": a numbered gate whose first side connects to an earlier numbered gate's takes that gate's number.
    /// </summary>
    void InteriorRegionTest()
    {
        for (int i = 0; i < _gates.Count; i++)
        {
            if (First[i] <= 0) continue;

            for (int j = i + 1; j < _gates.Count; j++)
                if (First[j] > 0 && _flood.Connected(_gates[i].First, _gates[j].First)) First[j] = First[i];
        }
    }

    /// <summary>
    /// Floods each code above 0 of <paramref name="codes"/> from the side point of the last gate holding it, in ascending order
    /// (<c>FUN_1402ed460</c>, first sides for the first codes, second sides for the second); a first code 0xffff is left out.
    /// </summary>
    void FloodAll(int[] codes, bool skipUnset)
    {
        var seeds = new SortedDictionary<int, Vector3>();
        for (int i = 0; i < _gates.Count; i++)
        {
            if (skipUnset && codes[i] == 0xffff) continue;

            seeds[codes[i]] = codes == First ? _gates[i].First : _gates[i].Second;
        }

        foreach (var (code, seed) in seeds)
            if (code > 0) FloodOne(code, seed);
    }

    /// <summary>
    /// Floods one code and logs what went wrong.
    /// </summary>
    void FloodOne(int code, Vector3 seed)
    {
        string at = string.Create(CultureInfo.InvariantCulture, $"code {code} at {seed.X:0},{seed.Y:0},{seed.Z:0}");
        switch (_flood.Flood(code, seed, Codes))
        {
            case GateCodeFlood.Outcome.NoStart:
                Log.Add($"Gates - Floodfill has no starting polygon ({at})");
                break;
            case GateCodeFlood.Outcome.Failed:
                Log.Add($"Gates - FloodFill {at} - ERROR: LIMIT REACHED");
                _failed.Add(code);
                break;
        }
    }

    /// <summary>
    /// The second code is the code of the face at the second side (<c>NavMesh::getGateCode</c>), 0 when below 1; a first code -1 takes it.
    /// </summary>
    void SecondCodes()
    {
        for (int i = 0; i < _gates.Count; i++)
        {
            int code = _flood.CodeAt(_gates[i].Second, Codes);
            Second[i] = code < 1 ? 0 : code;
            if (First[i] == -1) First[i] = Second[i];
        }
    }

    /// <summary>
    /// "Update codes": the gates with second code 0 are grouped by whether their second sides connect, each group numbered from the gate count + 1;
    /// the first group's number then becomes 0 in both codes. Returns the next free number.
    /// </summary>
    int UpdateCodes()
    {
        int n = _gates.Count, next = n + 1;
        for (int i = 0; i < n; i++)
        {
            if (Second[i] != 0) continue;

            Second[i] = next;
            for (int j = i + 1; j < n; j++)
                if (Second[j] == 0 && _flood.Connected(_gates[i].Second, _gates[j].Second)) Second[j] = next;

            next++;
        }

        Replace(n + 1, 0);
        if (next > n + 2) Log.Add("Gates - Info: Multiple gatecode 0 regions");
        return next;
    }

    /// <summary>
    /// "External code fix": a first code 0xffff takes the second.
    /// </summary>
    void ExternalCodeFix()
    {
        for (int i = 0; i < _gates.Count; i++)
            if (First[i] == 0xffff) First[i] = Second[i];
    }

    /// <summary>
    /// "Single gatecode fail - Attempting to swap with code 0": code 0 becomes <paramref name="next"/>, the failed code 0, and the region of the
    /// first gate holding <paramref name="next"/> is flooded with it from that side; what fails then is what fails.
    /// </summary>
    void SwapWithZero(int next)
    {
        Log.Add("Gates - Single gatecode fail - Attempting to swap with code 0");
        int failed = _failed[0];
        Replace(0, next);
        Replace(failed, 0);
        _failed.Clear();
        for (int i = 0; i < _gates.Count; i++)
        {
            if (First[i] != next && Second[i] != next) continue;

            FloodOne(next, First[i] == next ? _gates[i].First : _gates[i].Second);
            return;
        }
    }

    /// <summary>
    /// "Failed to write n codes": every failed code becomes 0 in the gates (its faces were never written).
    /// </summary>
    void ZeroFailed()
    {
        if (_failed.Count > 0) Log.Add($"Gates - Failed to write {_failed.Count} codes");
        foreach (int code in _failed)
            Replace(code, 0);
    }

    /// <summary>
    /// Every gate code <paramref name="from"/>, first or second, becomes <paramref name="to"/>.
    /// </summary>
    void Replace(int from, int to)
    {
        for (int i = 0; i < _gates.Count; i++)
        {
            if (First[i] == from) First[i] = to;
            if (Second[i] == from) Second[i] = to;
        }
    }
}
