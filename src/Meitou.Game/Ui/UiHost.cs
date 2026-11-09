using Meitou.Data.Ui;

namespace Meitou.Game.Ui;

public sealed record UiDiagnostic(string Extension, string Operation, Exception Error);
public sealed record UiActionContext(UiFrameContext Frame, string Argument);

/// <summary>
/// Read-only presentation data. UI extensions cannot mutate UiBindings or receive the simulation world.
/// </summary>
public sealed class UiFrameContext
{
    readonly UiHost _host;
    readonly Dictionary<string, string> _text;
    readonly HashSet<string> _disabled;

    internal UiFrameContext(UiHost host, UiMode mode, int width, int height, UiBindings bindings)
    {
        this._host = host;
        Mode = mode;
        Viewport = new(0, 0, width, height);
        _text = new(bindings.Text, StringComparer.Ordinal);
        _disabled = new(bindings.Disabled, StringComparer.Ordinal);
    }

    public UiMode Mode { get; }
    public UiRect Viewport { get; }

    public string Text(string key, string fallback = "") => _text.GetValueOrDefault(key) ?? fallback;
    public bool Enabled(string key) => !_disabled.Contains(key);

    public UiNode CreateControl(string id) => _host.CreateControl(id, this);
    public UiNode CreateBaseControl(string id) => _host.CreateControl(id, this, useOverrides: false);
}

/// <summary>
/// Platform-independent native UI extension host. Access on the UI thread only; no DLL discovery/loading.
/// </summary>
public sealed class UiHost
{
    #region State

    public const int ApiVersion = 1;

    readonly Dictionary<string, (string Owner, UiStyle Style)> _styles = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Owner, Func<UiFrameContext, UiNode> Factory)> _controls = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Owner, Action<UiActionContext> Handler)> _actions = new(StringComparer.Ordinal);
    readonly List<(string Owner, string Target, UiStyle Style)> _overrides = [];
    readonly List<(string Owner, string Target, Func<UiFrameContext, UiNode> Factory)> _controlOverrides = [];
    readonly List<(string Owner, string Slot, UiMode? Mode, int Order, Func<UiFrameContext, UiNode> Factory)> _contributions = [];
    readonly HashSet<string> _extensions = new(StringComparer.Ordinal);
    UiFrameContext? _frame;

    public event Action<UiDiagnostic>? Diagnostic;

    #endregion

    #region Registration

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

    internal static void ValidateName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException($"Invalid UI registration name: {id}");
    }

    internal void AddStyle(string owner, string id, UiStyle style)
    {
        UiInsets.Parse(style.TextPadding.ToString());
        _styles.Add(id, (owner, style));
    }

    internal void AddControl(string owner, string id, Func<UiFrameContext, UiNode> factory) => _controls.Add(id, (owner, factory));

    internal void AddAction(string owner, string id, Action<UiActionContext> action) => _actions.Add(id, (owner, action));

    internal void Override(string owner, string id, UiStyle style)
    {
        UiInsets.Parse(style.TextPadding.ToString());
        if (!_styles.ContainsKey(id)) throw new KeyNotFoundException($"Unknown UI style: {id}");

        _overrides.Add((owner, id, style));
    }

    internal void OverrideControl(string owner, string id, Func<UiFrameContext, UiNode> factory)
    {
        if (!_controls.ContainsKey(id)) throw new KeyNotFoundException($"Unknown UI control: {id}");

        _controlOverrides.Add((owner, id, factory));
    }

    internal void Contribute(string owner, string slot, UiMode? mode, int order, Func<UiFrameContext, UiNode> factory) =>
        _contributions.Add((owner, slot, mode, order, factory));

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

    public UiStyle Style(string id)
    {
        if (id.Length == 0) return new();
        if (!_styles.TryGetValue(id, out var entry)) throw new KeyNotFoundException($"Unknown UI style: {id}");

        return _overrides.LastOrDefault(o => o.Target == id).Style ?? entry.Style;
    }

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
    readonly UiHost _host;
    bool _disposed;

    internal UiRegistration(UiHost host, string id)
    {
        this._host = host;
        Id = id;
    }

    public string Id { get; }

    public string Key(string localName)
    {
        UiHost.ValidateName(localName);
        return $"{Id}:{localName}";
    }

    void Check() => ObjectDisposedException.ThrowIf(_disposed, this);

    public string RegisterStyle(string name, UiStyle style)
    {
        Check();
        ArgumentNullException.ThrowIfNull(style);

        string key = Key(name);
        _host.AddStyle(Id, key, style);
        return key;
    }

    public string RegisterControl(string name, Func<UiFrameContext, UiNode> factory)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);

        string key = Key(name);
        _host.AddControl(Id, key, factory);
        return key;
    }

    public string RegisterAction(string name, Action<UiActionContext> action)
    {
        Check();
        ArgumentNullException.ThrowIfNull(action);

        string key = Key(name);
        _host.AddAction(Id, key, action);
        return key;
    }

    public void OverrideStyle(string target, UiStyle style)
    {
        Check();
        ArgumentNullException.ThrowIfNull(style);
        _host.Override(Id, target, style);
    }

    public void OverrideControl(string target, Func<UiFrameContext, UiNode> factory)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);
        _host.OverrideControl(Id, target, factory);
    }

    public void Contribute(string slot, Func<UiFrameContext, UiNode> factory, int order = 0, UiMode? mode = null)
    {
        Check();
        ArgumentNullException.ThrowIfNull(factory);
        _host.Contribute(Id, slot, mode, order, factory);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _host.Remove(Id);
    }
}