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
    /// Whether one of its keys was down at the last control tick; written and read on the host's thread (interface code), never the tick's.
    /// </summary>
    public bool Held { get; internal set; }
    /// <summary>
    /// Set when a press threw: it gets no more presses this session.
    /// </summary>
    internal bool Faulted { get; set; }

    /// <summary>
    /// The game action whose Controls row this action's row follows (docs/modding.md "Key actions"); null, a mod's action or a row the page does
    /// not show: after the game's rows. Mod rows after the same game row keep load order. Set it during <c>IMod.Initialize</c>.
    /// </summary>
    public InputAction? After { get; set; }

    /// <summary>
    /// The keys and buttons the action has now, as the Controls page shows them; none before the bindings take the mods' actions.
    /// </summary>
    public IReadOnlyList<Binding> Keys => Bindings?.Get(Action) ?? [];

    /// <summary>
    /// The bindings the action is in, set when they take the mods' actions; null before.
    /// </summary>
    internal InputBindings? Bindings { get; set; }

    /// <summary>
    /// The mod's translations its label is shown through.
    /// </summary>
    internal Meitou.Localisation.ModTranslations? Texts { get; init; }

    /// <summary>
    /// The label in the game's language (the mod's catalogues).
    /// </summary>
    internal string ShownLabel => Texts?.Text(Label) ?? Label;

    /// <summary>
    /// The row's caption: the label, then the mod's name in brackets.
    /// </summary>
    internal string Caption => $"{ShownLabel} [{OwnerName}]";

    /// <summary>
    /// Binds the action to <paramref name="keys"/> (none unbinds it) from the mod's own window, as the Controls page binds it (docs/modding.md "Key
    /// actions"): each key is taken from the other actions of its key map that have it, chords included, and the keys are kept in
    /// <c>meitou.user.json</c> as the page's are. False, with nothing changed, before the bindings take the mods' actions or when a key is one the
    /// page refuses (reserved, or the host's). On the host's thread.
    /// </summary>
    public bool Rebind(params Binding[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (Bindings is not { } bindings) return false;
        if (keys.Any(k => InputBindings.Reserved(k) || InputBindings.HostKey(k))) return false;

        var distinct = keys.Distinct().ToArray();
        foreach (var key in distinct) bindings.TakeFrom(Action, key);
        bindings.Set(Action, distinct);
        return true;
    }

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
            Content.CodeModFaults.Tell(Owner, e);
        }
    }

    /// <summary>
    /// Why an action of <paramref name="name"/>, <paramref name="label"/> and <paramref name="defaults"/> cannot be added after the mod's
    /// <paramref name="pending"/> ones: a bad name or label, a name the mod has already, or a default key the settings keep for themselves; null when
    /// it can. A default the game takes before the bindings (<see cref="InputBindings.HostKey"/>) is only left out, with a log line.
    /// </summary>
    internal static string? Refusal(string name, string label, IReadOnlyList<Binding> defaults, IEnumerable<ModKeyAction> pending)
    {
        if (name.Length is 0 or > MaxName || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            return $"Key action name '{name}' must be 1 to {MaxName} letters, digits, '_', '.' or '-'.";
        if (string.IsNullOrWhiteSpace(label) || label.Length > MaxLabel || label.Any(char.IsControl))
            return $"A key action's label is 1 to {MaxLabel} characters on one line.";
        if (pending.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return $"The mod already has a key action '{name}'.";
        foreach (var binding in defaults)
            if (InputBindings.Reserved(binding)) return $"{binding} is kept by the settings; a key action cannot take it.";
        return null;
    }
}

/// <summary>
/// What a key action's press gets, on the host's thread while a tick may be in flight: the snapshot on screen, the selected characters, the tick, the
/// command queue for anything that changes the world, and the modifier keys held as it was pressed.
/// </summary>
public sealed record ModKeyContext(WorldSnapshot Snapshot, IReadOnlyList<CharacterId> Selected, long Tick, Action<SimCommand> Enqueue)
{
    /// <summary>
    /// Either Ctrl key was held as the key was pressed.
    /// </summary>
    public bool Ctrl { get; init; }

    /// <summary>
    /// Either Shift key was held as the key was pressed.
    /// </summary>
    public bool Shift { get; init; }

    /// <summary>
    /// Either Alt key was held as the key was pressed.
    /// </summary>
    public bool Alt { get; init; }

    /// <summary>
    /// What the host shows as the key is pressed (docs/modding.md "Frame"); valid during the press only.
    /// </summary>
    public Modding.ModFrame? Frame { get; init; }
}

/// <summary>
/// The code mods' key actions in one set of bindings (<see cref="InputBindings"/>): their numbers, the keys each started with, and their saved form.
/// The player's saved keys come before any mod's defaults, as the game's own saved bindings do, so a default never takes a key the player chose.
/// </summary>
sealed class ModKeyBindings(IReadOnlyList<ModKeyAction> actions)
{
    /// <summary>
    /// Each action's saved entry as read at the start (none after Restore defaults): what an action the player has not touched since is saved as.
    /// </summary>
    readonly Dictionary<InputAction, string> _entries = [];
    /// <summary>
    /// The actions whose keys were set since the start or Restore defaults (the Controls page: a rebinding, Delete, a key taken for another
    /// action): saved as they are now.
    /// </summary>
    readonly HashSet<InputAction> _touched = [];

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
    /// The start, after the game's bindings are set: every action with a saved entry (<paramref name="saved"/>) takes its saved keys, in load order,
    /// then every other action its defaults, in load order; a key another action holds by then is left out. Returns a line for each key left out.
    /// </summary>
    public List<string> Start(InputBindings bindings, IReadOnlyDictionary<string, Dictionary<string, string>>? saved)
    {
        var problems = new List<string>();
        var texts = actions.Select(a => saved is null ? null : Saved(saved, a)).ToList();

        // Every action lets go of what it held (a second start), then the saved ones take their keys before any default is laid.
        foreach (var action in actions) bindings.Set(action.Action);
        for (int i = 0; i < actions.Count; i++)
            if (texts[i] is { } text) Lay(bindings, actions[i], Parse(actions[i], text, problems), "saved", problems);
        for (int i = 0; i < actions.Count; i++)
            if (texts[i] is null) Lay(bindings, actions[i], actions[i].Defaults, "default", problems);

        Remember(texts);
        return problems;
    }

    /// <summary>
    /// Restore defaults, after the game's defaults are laid: each action its defaults, in load order, a key another action holds left out.
    /// </summary>
    public List<string> Reset(InputBindings bindings)
    {
        var problems = new List<string>();
        foreach (var action in actions) bindings.Set(action.Action);
        foreach (var action in actions) Lay(bindings, action, action.Defaults, "default", problems);

        Remember(null);
        return problems;
    }

    /// <summary>
    /// Gives <paramref name="action"/> those of <paramref name="keys"/> that no other action holds and the game view can receive; the others are
    /// left out with a line naming <paramref name="kind"/> ("saved" or "default").
    /// </summary>
    void Lay(InputBindings bindings, ModKeyAction action, IEnumerable<Binding> keys, string kind, List<string> problems)
    {
        var kept = new List<Binding>();
        foreach (var binding in keys)
        {
            if (InputBindings.Reserved(binding) || InputBindings.HostKey(binding))
                problems.Add($"{action.Owner}: {action.Name} {kind} {binding} is kept by the game; left out");
            else if (bindings.Holder(binding, action.Action) is { } other)
                problems.Add($"{action.Owner}: {action.Name} {kind} {binding} is {Name(other)}'s; left out");
            else if (!kept.Contains(binding)) kept.Add(binding);
        }
        bindings.Set(action.Action, [.. kept]);
    }

    /// <summary>
    /// Keeps each action's saved entry, from <paramref name="texts"/> in action order (null: none), and forgets what was touched: the keys laid now
    /// are the start's.
    /// </summary>
    void Remember(List<string?>? texts)
    {
        _touched.Clear();
        _entries.Clear();
        for (int i = 0; i < actions.Count; i++)
            if (texts?[i] is { } text) _entries[actions[i].Action] = text;
    }

    /// <summary>
    /// Notes that <paramref name="action"/>'s keys were set (<see cref="InputBindings.Set"/>).
    /// </summary>
    public void Touch(InputAction action) => _touched.Add(action);

    /// <summary>
    /// The saved text of <paramref name="action"/>; null when none was saved.
    /// </summary>
    static string? Saved(IReadOnlyDictionary<string, Dictionary<string, string>> saved, ModKeyAction action) =>
        saved.TryGetValue(action.Owner, out var mine) && mine is not null && mine.TryGetValue(action.Name, out var text) ? text ?? "" : null;

    /// <summary>
    /// The keys of a saved <paramref name="text"/>; an unknown one is skipped with a line.
    /// </summary>
    static List<Binding> Parse(ModKeyAction action, string text, List<string> problems)
    {
        var keys = new List<Binding>();
        foreach (var token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Binding.TryParse(token, out var binding)) keys.Add(binding);
            else problems.Add($"{action.Owner}: {action.Name}: unknown binding '{token}'");
        }
        return keys;
    }

    /// <summary>
    /// <paramref name="saved"/> (copied) with the running mods' actions as they are now. An action the player has not touched since the start gets
    /// back the entry it had then (none stays none; Restore defaults leaves none); a touched one is saved as text (empty: unbound), or dropped when its
    /// keys are the mod's defaults (in any order). A running mod's entries for actions it no longer has are dropped while it still has an action;
    /// other mods' are kept. Null when nothing is left.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? Changes(InputBindings bindings, Dictionary<string, Dictionary<string, string>>? saved)
    {
        var result = saved?.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value ?? [])) ?? [];
        DropRemoved(result);
        foreach (var action in actions)
        {
            string now = InputBindings.Text(bindings.Get(action.Action));
            if (!result.TryGetValue(action.Owner, out var mine)) result[action.Owner] = mine = [];

            if (!_touched.Contains(action.Action))
            {
                if (_entries.TryGetValue(action.Action, out var entry)) mine[action.Name] = entry;
                else mine.Remove(action.Name);
            }
            else if (bindings.Get(action.Action).ToHashSet().SetEquals(action.Defaults)) mine.Remove(action.Name);
            else mine[action.Name] = now;
        }
        foreach (var (mod, mine) in result.ToList())
            if (mine.Count == 0) result.Remove(mod);
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Drops the running mods' entries for actions they no longer have (an action renamed or removed by a mod update).
    /// </summary>
    void DropRemoved(Dictionary<string, Dictionary<string, string>> result)
    {
        foreach (var owner in actions.Select(a => a.Owner).Distinct())
        {
            if (!result.TryGetValue(owner, out var mine)) continue;
            foreach (var name in mine.Keys.ToList())
                if (!actions.Any(a => a.Owner == owner && a.Name == name)) mine.Remove(name);
        }
    }

    /// <summary>
    /// An action's name for a log line: a mod action's label, else the game action's name.
    /// </summary>
    string Name(InputAction action) => Find(action)?.Label ?? action.ToString();
}
