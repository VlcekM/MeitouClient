namespace Meitou.Simulation.Bodies;

/// <summary>
/// The health values the observers last saw of one character (<see cref="CharacterCold.HealthWatch"/>) and the changes found since, not yet told.
/// Written only by the steps that change that character (its own partition in a parallel phase), read and emptied in the serial phases.
/// </summary>
sealed class HealthWatch
{
    /// <summary>
    /// Values per part, in <see cref="HealthValue"/> order up to <see cref="HealthValue.Rig"/>.
    /// </summary>
    const int PerPart = 4;

    /// <summary>
    /// The medical state the values are of; another one (a new body) is looked at afresh, its differences not told.
    /// </summary>
    MedicalState? _medical;

    /// <summary>
    /// The values last seen: each part's flesh, stun, bandage and rig, then the blood.
    /// </summary>
    float[] _values = [];

    /// <summary>
    /// The changes found and not yet told, in the order found.
    /// </summary>
    internal readonly List<(HealthChangeKind Kind, HealthValue Value, int Part, float Before, float After)> Pending = [];

    /// <summary>
    /// Notes every value of <paramref name="medical"/> that changed since it was last seen as a change of <paramref name="kind"/>, and keeps the
    /// new values. A medical state not seen before (or whose parts changed in number) is only taken in.
    /// </summary>
    internal void Look(MedicalState medical, HealthChangeKind kind)
    {
        var parts = medical.Parts;
        if (!ReferenceEquals(_medical, medical) || _values.Length != parts.Count * PerPart + 1)
        {
            TakeIn(medical);
            return;
        }

        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            Compare(kind, HealthValue.Flesh, p, part.Flesh);
            Compare(kind, HealthValue.Stun, p, part.Stun);
            Compare(kind, HealthValue.Bandage, p, part.Bandage);
            Compare(kind, HealthValue.Rig, p, part.Rig);
        }

        Compare(kind, HealthValue.Blood, -1, medical.Blood);
    }

    /// <summary>
    /// Keeps <paramref name="medical"/>'s values as the ones seen, telling nothing.
    /// </summary>
    void TakeIn(MedicalState medical)
    {
        var parts = medical.Parts;
        _medical = medical;
        _values = new float[parts.Count * PerPart + 1];
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            _values[p * PerPart] = part.Flesh;
            _values[p * PerPart + 1] = part.Stun;
            _values[p * PerPart + 2] = part.Bandage;
            _values[p * PerPart + 3] = part.Rig;
        }

        _values[^1] = medical.Blood;
    }

    /// <summary>
    /// Notes one value when it differs from the one seen (exactly: the same float is no change) and keeps it.
    /// </summary>
    void Compare(HealthChangeKind kind, HealthValue value, int part, float now)
    {
        int k = part < 0 ? _values.Length - 1 : part * PerPart + (int)value;
        float before = _values[k];
        if (before.Equals(now)) return;

        _values[k] = now;
        Pending.Add((kind, value, part, before, now));
    }
}

/// <summary>
/// The world's systems told of health changes (<see cref="IHealthChangeObserver"/>). The steps that change health look at the character before and
/// after (<see cref="Before"/>, <see cref="After"/>), so their changes carry their kind; the world's sweep (<see cref="Tell"/>) finds the rest as
/// <see cref="HealthChangeKind.Other"/> and tells everything in slot order, so the order never depends on the thread count. Nothing is looked at
/// without an observer.
/// </summary>
sealed class HealthObservers
{
    /// <summary>
    /// The registered observers.
    /// </summary>
    readonly SerialHooks<IHealthChangeObserver> _hooks = new(0x48454C54, "health change observer");

    /// <summary>
    /// Whether any observer is still told.
    /// </summary>
    internal bool Active => _hooks.Active;

    /// <summary>
    /// Registers <paramref name="system"/> when it is an observer.
    /// </summary>
    internal void Register(ITickSystem system, int systemIndex) => _hooks.Register(system, systemIndex);

    /// <summary>
    /// Adds the disabled observers to the world's state hash.
    /// </summary>
    internal void HashFaults(ref StateHasher hash) => _hooks.HashFaults(ref hash);

    /// <summary>
    /// Before a step of a known kind changes the character in <paramref name="slot"/>: what changed it since it was last seen is noted as
    /// <see cref="HealthChangeKind.Other"/>. Any phase, on the thread that owns the character.
    /// </summary>
    internal static void Before(World world, int slot) => Look(world, slot, HealthChangeKind.Other);

    /// <summary>
    /// After a step of <paramref name="kind"/> changed the character in <paramref name="slot"/>: what it changed is noted as that kind. Any phase,
    /// on the thread that owns the character.
    /// </summary>
    internal static void After(World world, int slot, HealthChangeKind kind) => Look(world, slot, kind);

    /// <summary>
    /// Notes the changes of the character in <paramref name="slot"/> as <paramref name="kind"/>; nothing without an observer.
    /// </summary>
    static void Look(World world, int slot, HealthChangeKind kind)
    {
        if (!world.HealthObservers.Active || world.Characters.Cold(slot) is not { Medical: { } medical } cold) return;

        (cold.HealthWatch ??= new HealthWatch()).Look(medical, kind);
    }

    /// <summary>
    /// Notes what no step named and tells every character's changes, in slot order. Serial; the world runs it after the slow world.
    /// </summary>
    internal void Tell(World world)
    {
        if (!Active) return;

        var table = world.Characters;
        for (int slot = 0; slot < table.HighWater && Active; slot++) TellOne(world, slot);
    }

    /// <summary>
    /// The character in <paramref name="slot"/> is about to leave the world: its changes are told, and what it is seen as is forgotten, so it is
    /// taken in afresh if it comes back. Serial phases.
    /// </summary>
    internal static void Leaving(World world, int slot, CharacterCold cold)
    {
        if (world.HealthObservers.Active) world.HealthObservers.TellOne(world, slot);

        cold.HealthWatch = null;
    }

    /// <summary>
    /// Notes the character's unnamed changes and tells its pending ones to every enabled observer.
    /// </summary>
    void TellOne(World world, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Medical: { } medical } cold) return;

        var watch = cold.HealthWatch ??= new HealthWatch();
        watch.Look(medical, HealthChangeKind.Other);
        if (watch.Pending.Count == 0) return;

        var id = table.IdOf(slot);
        foreach (var (kind, value, part, before, after) in watch.Pending)
            Observe(world, new HealthChange(world.Tick, id, kind, value, part, before, after));
        watch.Pending.Clear();
    }

    /// <summary>
    /// Passes one change to every enabled observer, disabling one that throws.
    /// </summary>
    void Observe(World world, in HealthChange change)
    {
        for (int i = 0; i < _hooks.Count; i++)
        {
            if (!_hooks.Enabled(i)) continue;

            try
            {
                _hooks[i].HealthChanged(world, change);
            }
            catch (Exception e)
            {
                _hooks.Threw(i, e);
            }
        }
    }
}
