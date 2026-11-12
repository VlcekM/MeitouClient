using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

/// <summary>
/// A UI extension's failure: the extension, what it was doing and the exception.
/// </summary>
public sealed record UiDiagnostic(string Extension, string Operation, Exception Error);

/// <summary>
/// What an action handler gets: the frame's data and the control's argument.
/// </summary>
public sealed record UiActionContext(UiFrameContext Frame, string Argument);

/// <summary>
/// Read-only presentation data. UI extensions cannot mutate UiBindings or receive the simulation world.
/// </summary>
public sealed class UiFrameContext
{
    /// <summary>
    /// The host the frame's controls come from.
    /// </summary>
    readonly UiHost _host;

    /// <summary>
    /// The bound texts by key.
    /// </summary>
    readonly Dictionary<string, string> _text;

    /// <summary>
    /// The disabled keys.
    /// </summary>
    readonly HashSet<string> _disabled;

    /// <summary>
    /// The frame's data from <paramref name="bindings"/> (copied): mode, viewport, texts and disabled keys.
    /// </summary>
    internal UiFrameContext(UiHost host, UiMode mode, int width, int height, UiBindings bindings)
    {
        this._host = host;
        Mode = mode;
        Viewport = new(0, 0, width, height);
        _text = new(bindings.Text, StringComparer.Ordinal);
        _disabled = new(bindings.Disabled, StringComparer.Ordinal);
    }

    /// <summary>
    /// The UI mode the frame is drawn in.
    /// </summary>
    public UiMode Mode { get; }

    /// <summary>
    /// The whole screen, from 0, 0.
    /// </summary>
    public UiRect Viewport { get; }

    /// <summary>
    /// The bound text of <paramref name="key"/>, else <paramref name="fallback"/>.
    /// </summary>
    public string Text(string key, string fallback = "") => _text.GetValueOrDefault(key) ?? fallback;

    /// <summary>
    /// Whether <paramref name="key"/> is not disabled.
    /// </summary>
    public bool Enabled(string key) => !_disabled.Contains(key);

    /// <summary>
    /// A registered control <paramref name="id"/>, the last override's if any.
    /// </summary>
    public UiNode CreateControl(string id) => _host.CreateControl(id, this);

    /// <summary>
    /// A registered control <paramref name="id"/> as its owner made it, without overrides.
    /// </summary>
    public UiNode CreateBaseControl(string id) => _host.CreateControl(id, this, useOverrides: false);
}

/// <summary>
/// Platform-independent native UI extension host. Access on the UI thread only; no DLL discovery/loading.
/// </summary>
public sealed class UiHost
{
    #region State

    /// <summary>
    /// The extension API version the host provides; an extension must ask for it.
    /// </summary>
    public const int ApiVersion = 1;

    /// <summary>
    /// The styles by id, with their owners.
    /// </summary>
    readonly Dictionary<string, (string Owner, UiStyle Style)> _styles = new(StringComparer.Ordinal);

    /// <summary>
    /// The control factories by id, with their owners.
    /// </summary>
    readonly Dictionary<string, (string Owner, Func<UiFrameContext, UiNode> Factory)> _controls = new(StringComparer.Ordinal);

    /// <summary>
    /// The action handlers by id, with their owners.
    /// </summary>
    readonly Dictionary<string, (string Owner, Action<UiActionContext> Handler)> _actions = new(StringComparer.Ordinal);

    /// <summary>
    /// The style overrides in registration order (the last wins).
    /// </summary>
    readonly List<(string Owner, string Target, UiStyle Style)> _overrides = [];

    /// <summary>
    /// The control overrides in registration order (the last wins).
    /// </summary>
    readonly List<(string Owner, string Target, Func<UiFrameContext, UiNode> Factory)> _controlOverrides = [];

    /// <summary>
    /// The contributions: owner, slot, mode (null: every mode), order and factory.
    /// </summary>
    readonly List<(string Owner, string Slot, UiMode? Mode, int Order, Func<UiFrameContext, UiNode> Factory)> _contributions = [];

    /// <summary>
    /// The registered extensions' ids.
    /// </summary>
    readonly HashSet<string> _extensions = new(StringComparer.Ordinal);

    /// <summary>
    /// The data of the last arranged frame (the actions get it); null before the first.
    /// </summary>
    UiFrameContext? _frame;

    /// <summary>
    /// An extension failed (a contribution's build or an action).
    /// </summary>
    public event Action<UiDiagnostic>? Diagnostic;

    #endregion

    #region Registration

    /// <summary>
    /// Registers <paramref name="extension"/> (a valid id, the host's API version, not registered yet); its scope's disposal removes everything it
    /// added.
    /// </summary>
    public IDisposable Register(IUiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);

        string id = extension.Id;
        ValidateName(id);
        if (extension.ApiVersion != ApiVersion)
            throw new NotSupportedException($"UI extension {id} requires API {extension.ApiVersion}; host provides {ApiVersion}.");
        if (!_extensions.Add(id)) throw new InvalidOperationException($"UI extension already registered: {id}");

        var scope = new UiRegistration(this, id);
        try
        {
            extension.Register(scope);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Throws for a name that is blank or holds anything but ASCII letters, digits, '.', '-' and '_'.
    /// </summary>
    internal static void ValidateName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException($"Invalid UI registration name: {id}");
    }

    /// <summary>
    /// Adds style <paramref name="id"/> (its padding checked) for <paramref name="owner"/>.
    /// </summary>
    internal void AddStyle(string owner, string id, UiStyle style)
    {
        UiInsets.Parse(style.TextPadding.ToString());
        _styles.Add(id, (owner, style));
    }

    /// <summary>
    /// Adds control factory <paramref name="id"/> for <paramref name="owner"/>.
    /// </summary>
    internal void AddControl(string owner, string id, Func<UiFrameContext, UiNode> factory) => _controls.Add(id, (owner, factory));

    /// <summary>
    /// Adds action handler <paramref name="id"/> for <paramref name="owner"/>.
    /// </summary>
    internal void AddAction(string owner, string id, Action<UiActionContext> action) => _actions.Add(id, (owner, action));

    /// <summary>
    /// Overrides the registered style <paramref name="id"/> (its padding checked) for <paramref name="owner"/>.
    /// </summary>
    internal void Override(string owner, string id, UiStyle style)
    {
        UiInsets.Parse(style.TextPadding.ToString());
        if (!_styles.ContainsKey(id)) throw new KeyNotFoundException($"Unknown UI style: {id}");

        _overrides.Add((owner, id, style));
    }

    /// <summary>
    /// Overrides the registered control <paramref name="id"/> for <paramref name="owner"/>.
    /// </summary>
    internal void OverrideControl(string owner, string id, Func<UiFrameContext, UiNode> factory)
    {
        if (!_controls.ContainsKey(id)) throw new KeyNotFoundException($"Unknown UI control: {id}");

        _controlOverrides.Add((owner, id, factory));
    }

    /// <summary>
    /// Adds a contribution to <paramref name="slot"/> for <paramref name="owner"/>.
    /// </summary>
    internal void Contribute(string owner, string slot, UiMode? mode, int order, Func<UiFrameContext, UiNode> factory) =>
        _contributions.Add((owner, slot, mode, order, factory));

    /// <summary>
    /// Removes everything <paramref name="owner"/> registered.
    /// </summary>
    internal void Remove(string owner)
    {
        foreach (string id in _styles.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _styles.Remove(id);
        foreach (string id in _controls.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _controls.Remove(id);
        foreach (string id in _actions.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) _actions.Remove(id);
        _overrides.RemoveAll(o => o.Owner == owner);
        _controlOverrides.RemoveAll(o => o.Owner == owner);
        _contributions.RemoveAll(c => c.Owner == owner);
        _extensions.Remove(owner);
    }

    #endregion

    #region Lookup

    /// <summary>
    /// Style <paramref name="id"/>, the last override's if any; the default for an empty id.
    /// </summary>
    public UiStyle Style(string id)
    {
        if (id.Length == 0) return new();
        if (!_styles.TryGetValue(id, out var entry)) throw new KeyNotFoundException($"Unknown UI style: {id}");

        return _overrides.LastOrDefault(o => o.Target == id).Style ?? entry.Style;
    }

    /// <summary>
    /// Makes control <paramref name="id"/> for the frame (with <paramref name="useOverrides"/>, the last override's factory).
    /// </summary>
    internal UiNode CreateControl(string id, UiFrameContext context, bool useOverrides = true)
    {
        if (!_controls.TryGetValue(id, out var entry)) throw new KeyNotFoundException($"Unknown UI control: {id}");

        var factory = useOverrides ? _controlOverrides.LastOrDefault(o => o.Target == id).Factory ?? entry.Factory : entry.Factory;
        return factory(context);
    }

    #endregion

    #region Layout

    /// <summary>
    /// Arrange contributions into named parent rectangles in stable order. A failed contribution is omitted.
    /// </summary>
    public IReadOnlyList<UiControl> Arrange(UiMode mode, int width, int height, UiBindings bindings, IReadOnlyDictionary<string, UiRect> slots)
    {
        _frame = new(this, mode, width, height, bindings);
        var result = new List<UiControl>();
        if (width <= 0 || height <= 0) return result;

        foreach (var contribution in _contributions.OrderBy(c => c.Order).ToArray())
        {
            if (contribution.Mode is { } only && only != mode || !slots.TryGetValue(contribution.Slot, out var parent)) continue;

            try
            {
                var local = new List<UiControl>();
                ArrangeNode(contribution.Factory(_frame), parent, parent, true, local);
                result.AddRange(local);
            }
            catch (Exception e)
            {
                Diagnostic?.Invoke(new(contribution.Owner, $"build {contribution.Slot}", e));
            }
        }

        return result;
    }

    /// <summary>
    /// Lays out <paramref name="node"/> in <paramref name="parent"/> (or at <paramref name="placed"/>), clipped, and its children by its flow;
    /// nothing for a hidden or clipped-out node.
    /// </summary>
    void ArrangeNode(UiNode node, UiRect parent, UiRect clip, bool parentEnabled, List<UiControl> result, UiRect? placed = null)
    {
        if (!node.Visible) return;

        var b = placed ?? node.Placement.Resolve(parent);
        if (!float.IsFinite(b.X) || !float.IsFinite(b.Y) || !float.IsFinite(b.Width) || !float.IsFinite(b.Height))
            throw new InvalidOperationException($"Non-finite UI layout: {node.Id}");

        float x = Math.Max(b.X, clip.X), y = Math.Max(b.Y, clip.Y);
        var visible = new UiRect(x, y, Math.Max(0, Math.Min(b.X + b.Width, clip.X + clip.Width) - x),
            Math.Max(0, Math.Min(b.Y + b.Height, clip.Y + clip.Height) - y));
        if (visible.Width <= 0 || visible.Height <= 0) return;

        var style = Style(node.Style);
        bool enabled = parentEnabled && node.Enabled;
        var kind = node.Kind == UiNodeKind.Button ? "Button" : node.Kind == UiNodeKind.Label ? "TextBox" : "Widget";
        var properties = new Dictionary<string, string>
        {
            ["TextAlign"] = style.TextAlign,
            ["FontName"] = style.FontName,
            ["TextPadding"] = style.TextPadding.ToString(),
        };
        result.Add(new(node.Id, kind, visible, node.Text, node.Action, style.Skin, enabled, properties,
            style.Background, style.Text, style.Hover, style.Active, style.Disabled, node.Pickable, node.Argument, style.TextScale));

        var content = new UiRect(
            b.X + node.Padding, b.Y + node.Padding, Math.Max(0, b.Width - node.Padding * 2), Math.Max(0, b.Height - node.Padding * 2));
        float cursor = 0;
        foreach (var child in node.Children)
        {
            if (!child.Visible) continue;

            UiRect? childRect = null;
            if (node.Flow != UiFlow.Absolute)
            {
                var size = child.Placement.Resolve(content);
                childRect = new(
                    content.X + (node.Flow == UiFlow.Row ? cursor : 0),
                    content.Y + (node.Flow == UiFlow.Column ? cursor : 0),
                    size.Width,
                    size.Height);
                cursor += (node.Flow == UiFlow.Row ? size.Width : size.Height) + node.Gap;
            }

            ArrangeNode(child, content, visible, enabled, result, childRect);
        }
    }

    #endregion

    #region Dispatch

    /// <summary>
    /// Returns false for an unregistered action; disabled controls consume clicks without invoking handlers.
    /// </summary>
    public bool Dispatch(UiControl control)
    {
        if (!control.Enabled) return true;
        if (_frame is null || !_actions.TryGetValue(control.Action, out var action)) return false;

        try
        {
            action.Handler(new(_frame, control.Argument));
        }
        catch (Exception e)
        {
            Diagnostic?.Invoke(new(action.Owner, $"action {control.Action}", e));
        }

        return true;
    }

    #endregion
}

/// <summary>
/// Owned registration scope. Local IDs are prefixed with the extension ID to prevent collisions.
/// </summary>
public sealed class UiRegistration : IDisposable
{
    /// <summary>
    /// The host the scope registers into.
    /// </summary>
    readonly UiHost _host;

    /// <summary>
    /// The scope was disposed.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// A scope for extension <paramref name="id"/>.
    /// </summary>
    internal UiRegistration(UiHost host, string id)
    {
        this._host = host;
        Id = id;
    }

    /// <summary>
    /// The extension's id.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// <paramref name="localName"/> (validated) prefixed with the extension's id.
    /// </summary>
    public string Key(string localName)
    {
        UiHost.ValidateName(localName);
        return $"{Id}:{localName}";
    }

    /// <summary>
    /// Throws once the scope is disposed.
    /// </summary>
    void Check() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Registers a style; returns its key.
    /// </summary>
    public string RegisterStyle(string name, UiStyle style)
    {
        Check();
        ArgumentNullException.ThrowIfNull(style);

        string key = Key(name);
        _host.AddStyle(Id, key, style);
        return key;
    }

    /// <summary>
    /// Registers a control factory; returns its key.
    /// </summary>
    public string RegisterControl(string name, Func<UiFrameContext, UiNode> factory)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);

        string key = Key(name);
        _host.AddControl(Id, key, factory);
        return key;
    }

    /// <summary>
    /// Registers an action handler; returns its key.
    /// </summary>
    public string RegisterAction(string name, Action<UiActionContext> action)
    {
        Check();
        ArgumentNullException.ThrowIfNull(action);

        string key = Key(name);
        _host.AddAction(Id, key, action);
        return key;
    }

    /// <summary>
    /// Overrides a registered style.
    /// </summary>
    public void OverrideStyle(string target, UiStyle style)
    {
        Check();
        ArgumentNullException.ThrowIfNull(style);
        _host.Override(Id, target, style);
    }

    /// <summary>
    /// Overrides a registered control.
    /// </summary>
    public void OverrideControl(string target, Func<UiFrameContext, UiNode> factory)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);
        _host.OverrideControl(Id, target, factory);
    }

    /// <summary>
    /// Contributes a node to <paramref name="slot"/> (in <paramref name="order"/>; only in <paramref name="mode"/> when given).
    /// </summary>
    public void Contribute(string slot, Func<UiFrameContext, UiNode> factory, int order = 0, UiMode? mode = null)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);
        _host.Contribute(Id, slot, mode, order, factory);
    }

    /// <summary>
    /// Removes everything the extension registered (once).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _host.Remove(Id);
    }
}