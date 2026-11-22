namespace Meitou.Simulation.Combat;

/// <summary>
/// The world's systems asked about worn armour pieces as a hit is about to go through them (<see cref="IArmourFilter"/>).
/// </summary>
sealed class ArmourFilters
{
    /// <summary>
    /// The filtering systems, each with its index and whether it is still asked.
    /// </summary>
    List<(IArmourFilter Filter, int SystemIndex, bool Enabled)>? _filters;
    /// <summary>
    /// The filters still asked, and the ones disabled after a failure.
    /// </summary>
    int _active, _faulted;
    /// <summary>
    /// The filtered pieces of the hit being resolved (hits land serially, so one buffer serves every hit).
    /// </summary>
    ArmourPiece[] _pieces = [];

    /// <summary>
    /// Whether any filter is asked; a hit uses the fighter's own pieces without one.
    /// </summary>
    internal bool Active => _active > 0;

    /// <summary>
    /// Registers <paramref name="system"/> when it is a filter.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex)
    {
        if (system is not IArmourFilter filter) return;

        (_filters ??= []).Add((filter, systemIndex, true));
        _active++;
    }

    /// <summary>
    /// Adds the disabled filters to the world's state hash. Successful filters add nothing.
    /// </summary>
    internal void HashFaults(ref StateHasher hash)
    {
        if (_faulted == 0) return;

        hash.Add(0x41524D46);
        hash.Add(_faulted);
        foreach (var entry in _filters!)
        {
            if (!entry.Enabled) hash.Add(entry.SystemIndex);
        }
    }

    /// <summary>
    /// The pieces a hit on <paramref name="wearer"/> goes through: the fighter's own without a filter, else each piece as the filters answer it.
    /// The span stays valid until the next hit. Serial phases only.
    /// </summary>
    internal ReadOnlySpan<ArmourPiece> Pieces(World world, CombatHitSource source, CharacterId wearer, Fighter? fighter)
    {
        if (fighter is null) return default;

        var own = fighter.Armour;
        if (_active == 0 || own.Length == 0) return own;
        if (_pieces.Length < own.Length) _pieces = new ArmourPiece[own.Length];

        for (int i = 0; i < own.Length; i++)
        {
            var item = i < fighter.ArmourItems.Count ? fighter.ArmourItems[i] : null;
            _pieces[i] = Filtered(world, new ArmourAsk(world.Tick, source, wearer, item, i, own[i]));
        }

        return _pieces.AsSpan(0, own.Length);
    }

    /// <summary>
    /// The piece after every enabled filter, each seeing the one before's answer.
    /// </summary>
    ArmourPiece Filtered(World world, ArmourAsk ask)
    {
        var filters = _filters!;
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i].Enabled) ask = ask with { Piece = Ask(world, i, ask) };
        }

        return ask.Piece;
    }

    /// <summary>
    /// One filter's answer; the piece as it came when the filter throws or answers a defence out of range, which disables it.
    /// </summary>
    ArmourPiece Ask(World world, int i, in ArmourAsk ask)
    {
        var entry = _filters![i];
        string? fault;
        var piece = ask.Piece;
        try
        {
            piece = entry.Filter.Piece(world, ask);
            fault = Valid(piece) ? null : $"piece {piece} has a defence not finite or out of range";
        }
        catch (Exception e)
        {
            fault = $"{e.GetType().Name}: {e.Message}";
            SimulationExtensions.TellOutdated(entry.Filter, e);
        }

        if (fault is null) return piece;

        _filters[i] = (entry.Filter, entry.SystemIndex, false);
        _active--;
        _faulted++;
        LogFault(entry.Filter, fault);
        return ask.Piece;
    }

    /// <summary>
    /// The game's ranges (<see cref="ArmourPiece.Create"/>): cut 0 to 0.9, blunt 0 to 0.8, pierce from 0, cut-into-stun 0 to 1.
    /// </summary>
    static bool Valid(in ArmourPiece p) =>
        In(p.CutDefence, 0.9f) && In(p.BluntDefence, 0.8f) && float.IsFinite(p.PierceDefence) && p.PierceDefence >= 0 && In(p.CutIntoStun, 1);

    /// <summary>
    /// A finite value from 0 to <paramref name="max"/>.
    /// </summary>
    static bool In(float v, float max) => float.IsFinite(v) && v >= 0 && v <= max;

    /// <summary>
    /// A disabled filter's log line: the mod and the name it registered the system under, as the mods' other log lines; else its type.
    /// </summary>
    static void LogFault(IArmourFilter filter, string fault)
    {
        if (SimulationExtensions.FactoryOf(filter) is { } factory)
            Console.Error.WriteLine($"mod       {factory.Owner}: armour filter '{factory.Name}' disabled for this world: {fault}");
        else
            Console.Error.WriteLine($"armour filter '{filter.GetType().FullName}' disabled for this world: {fault}");
    }
}
