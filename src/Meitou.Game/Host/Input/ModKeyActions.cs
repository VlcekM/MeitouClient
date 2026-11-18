using Meitou.Simulation;

namespace Meitou.Game.Host.Input;

/// <summary>
/// A key action a code mod added (docs/modding.md "Key actions"): its name (the saved key), its label and default keys, the action number the
/// bindings give it (<see cref="InputBindings.FirstModAction"/> and up, in load order) and what a press runs. It is listed on the settings' Controls
/// page after the game's actions, rebound and saved as theirs are.
/// </summary>
public sealed class ModKeyAction
{
    /// <summary>
    /// The longest name and label.
    /// </summary>
    const int MaxName = 48, MaxLabel = 48;

    /// <summary>
    /// What a press runs on the host's thread.
    /// </summary>
    readonly Action<ModKeyContext> _pressed;

    /// <summary>
    /// Made by <c>ModContext.AddKeyAction</c> for the mod <paramref name="owner"/> (its id) named <paramref name="ownerName"/>.
    /// </summary>
    internal ModKeyAction(string owner, string ownerName, string name, string label, Action<ModKeyContext> pressed, IReadOnlyList<Binding> defaults)
    {
        Owner = owner;
        OwnerName = ownerName;
        Name = name;
        Label = label;
        Defaults = defaults;
        _pressed = pressed;
    }

    /// <summary>
    /// The id of the mod that added it.
    /// </summary>
    public string Owner { get; }
    /// <summary>
    /// The name of the mod that added it, shown beside the label.
    /// </summary>
    public string OwnerName { get; }
    /// <summary>
    /// The action's name within its mod: the key its bindings are saved under.
    /// </summary>
    public string Name { get; }
    /// <summary>
    /// The action's name on the Controls page.
    /// </summary>
    public string Label { get; }
    /// <summary>
    /// The keys and buttons the mod asked for; a key another action holds is left out (<see cref="InputBindings"/>).
    /// </summary>
    public IReadOnlyList<Binding> Defaults { get; }
    /// <summary>
    /// The action's number in the bindings (<see cref="InputBindings.FirstModAction"/> and up), given when the mod's registrations are committed.
    /// </summary>
    public InputAction Action { get; internal set; }
    /// <summary>
    /// Whether one of its keys was down at the last control tick (the host's thread).
    /// </summary>
    public bool Held { get; internal set; }
    /// <summary>
    /// Set when a press threw: it gets no more presses this session.
    /// </summary>
    internal bool Faulted { get; set; }

    /// <summary>
    /// The row's caption: the label, then the mod's name in brackets.
    /// </summary>
    internal string Caption => $"{Label} [{OwnerName}]";

    /// <summary>
    /// Runs the press; a throw is logged with the mod and the action, and the action gets no more presses.
    /// </summary>
    internal void Press(ModKeyContext context)
    {
        if (Faulted) return;
        try
        {
            _pressed(context);
        }
        catch (Exception e)
        {
            Faulted = true;
            Console.Error.WriteLine($"mod       {Owner}: key action '{Name}' failed and gets no more presses: {e}");
        }
    }

    /// <summary>
    /// Why an action of <paramref name="name"/>, <paramref name="label"/> and <paramref name="defaults"/> cannot be added after the mod's
    /// <paramref name="pending"/> ones: a bad name or label, a name the mod has already, or a default key the settings keep for themselves; null when
    /// it can.
    /// </summary>
    internal static string? Refusal(string name, string label, IReadOnlyList<Binding> defaults, IEnumerable<ModKeyAction> pending)
    {
        if (name.Length is 0 or > MaxName || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return $"Key action name '{name}' must be 1 to {MaxName} letters, digits, '_', '.' or '-'.";
        if (string.IsNullOrWhiteSpace(label) || label.Length > MaxLabel) return $"A key action's label is 1 to {MaxLabel} characters.";
        if (pending.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return $"The mod already has a key action '{name}'.";
        foreach (var binding in defaults)
            if (InputBindings.Reserved(binding)) return $"{binding} is kept by the settings; a key action cannot take it.";
        return null;
    }
}

/// <summary>
/// What a key action's press gets, on the host's thread while a tick may be in flight: the snapshot on screen, the selected characters, the tick, and
/// the command queue for anything that changes the world.
/// </summary>
public sealed record ModKeyContext(WorldSnapshot Snapshot, IReadOnlyList<CharacterId> Selected, long Tick, Action<SimCommand> Enqueue);

/// <summary>
/// The code mods' key actions in one set of bindings (<see cref="InputBindings"/>): their numbers, the defaults each got, and their saved form.
/// </summary>
sealed class ModKeyBindings(IReadOnlyList<ModKeyAction> actions)
{
    /// <summary>
    /// Each action's defaults as last laid (the keys no other action held then), in the saved form: what a save leaves out.
    /// </summary>
    readonly Dictionary<InputAction, string> _defaults = [];

    /// <summary>
    /// The actions, in load order.
    /// </summary>
    public IReadOnlyList<ModKeyAction> Actions => actions;

    /// <summary>
    /// The actions' numbers, in load order.
    /// </summary>
    public IEnumerable<InputAction> Numbers => actions.Select(a => a.Action);

    /// <summary>
    /// The action numbered <paramref name="action"/>; null for none.
    /// </summary>
    public ModKeyAction? Find(InputAction action)
    {
        foreach (var a in actions)
            if (a.Action == action) return a;
        return null;
    }

    /// <summary>
    /// Gives each action, in load order, the default keys no other action holds (the game's, then the earlier mods'): a key belongs to one action,
    /// as on the Controls page. Returns a line for each default left out.
    /// </summary>
    public List<string> LayDefaults(InputBindings bindings)
    {
        var problems = new List<string>();
        foreach (var action in actions)
        {
            var kept = new List<Binding>();
            foreach (var binding in action.Defaults)
            {
                if (bindings.Holder(binding, action.Action) is { } other)
                    problems.Add($"{action.Owner}: {action.Name} default {binding} is {Name(other)}'s; left out");
                else if (!kept.Contains(binding)) kept.Add(binding);
            }
            bindings.Set(action.Action, [.. kept]);
            _defaults[action.Action] = InputBindings.Text(kept);
        }
        return problems;
    }

    /// <summary>
    /// The player's saved keys of the running mods' actions over their defaults: a key another action holds is left out, as a default is; an
    /// unknown key is skipped. Entries of actions or mods not running are left alone.
    /// </summary>
    public List<string> ApplySaved(InputBindings bindings, IReadOnlyDictionary<string, Dictionary<string, string>> saved)
    {
        var problems = new List<string>();
        var entries = actions
            .Select(a => (Action: a, Text: Saved(saved, a)))
            .Where(e => e.Text is not null)
            .ToList();

        // Every saved action lets go of its defaults first, so a key the player moved from one mod's action to a later one's is free for it.
        foreach (var (action, _) in entries) bindings.Set(action.Action);
        foreach (var (action, text) in entries) ApplyOne(bindings, action, text!, problems);
        return problems;
    }

    /// <summary>
    /// The saved text of <paramref name="action"/>; null when none was saved.
    /// </summary>
    static string? Saved(IReadOnlyDictionary<string, Dictionary<string, string>> saved, ModKeyAction action) =>
        saved.TryGetValue(action.Owner, out var mine) && mine is not null && mine.TryGetValue(action.Name, out var text) ? text ?? "" : null;

    /// <summary>
    /// Gives <paramref name="action"/> the keys of its saved <paramref name="text"/> that no other action holds.
    /// </summary>
    void ApplyOne(InputBindings bindings, ModKeyAction action, string text, List<string> problems)
    {
        var kept = new List<Binding>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Binding.TryParse(token, out var binding)) problems.Add($"{action.Owner}: {action.Name}: unknown binding '{token}'");
            else if (bindings.Holder(binding, action.Action) is { } other)
                problems.Add($"{action.Owner}: {action.Name}: {binding} is {Name(other)}'s; left out");
            else if (!InputBindings.Reserved(binding) && !kept.Contains(binding)) kept.Add(binding);
        }
        bindings.Set(action.Action, [.. kept]);
    }

    /// <summary>
    /// <paramref name="saved"/> (copied) with each running action's keys: left out at its defaults, else kept as text (empty: unbound); mods with
    /// nothing left are dropped, and null when no mod has anything.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? Changes(InputBindings bindings, Dictionary<string, Dictionary<string, string>>? saved)
    {
        var result = saved?.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value ?? [])) ?? [];
        foreach (var action in actions)
        {
            string now = InputBindings.Text(bindings.Get(action.Action));
            bool atDefault = _defaults.TryGetValue(action.Action, out var laid) && laid == now;
            if (!result.TryGetValue(action.Owner, out var mine)) result[action.Owner] = mine = [];

            if (atDefault) mine.Remove(action.Name);
            else mine[action.Name] = now;
        }
        foreach (var (mod, mine) in result.ToList())
            if (mine.Count == 0) result.Remove(mod);
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// An action's name for a log line: a mod action's label, else the game action's name.
    /// </summary>
    string Name(InputAction action) => Find(action)?.Label ?? action.ToString();
}
