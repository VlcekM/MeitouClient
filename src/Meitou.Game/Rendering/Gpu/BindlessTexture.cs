namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// A stable bindless entry for one texture (the water's maps, the glass's scene copy); its sampler may change with the upscaler's LOD bias.
/// </summary>
sealed class BindlessTexture : IDisposable
{
    /// <summary>
    /// The context whose bindless table holds the entry.
    /// </summary>
    readonly IGpuContext _gpu;

    /// <summary>
    /// The kind of the entry (2D or 2D array).
    /// </summary>
    readonly BindlessKind _kind;

    /// <summary>
    /// The entry (made on first use).
    /// </summary>
    BindlessHandle _handle;

    /// <summary>
    /// The view the entry last held, to update it only on a change.
    /// </summary>
    SampledTexture _registered;

    /// <summary>
    /// The entry is registered.
    /// </summary>
    bool _has;

    /// <summary>
    /// An entry of <paramref name="kind"/>, registered on first use.
    /// </summary>
    public BindlessTexture(IGpuContext gpu, BindlessKind kind) => (this._gpu, this._kind) = (gpu, kind);

    /// <summary>
    /// The entry's index for <paramref name="texture"/>: registered the first time, updated when the view or sampler changed.
    /// </summary>
    public uint Index(in SampledTexture texture)
    {
        if (!_has)
        {
            _handle = new BindlessHandle(_kind, _gpu.Bindless.Register(_kind, texture));
            _has = true;
        }
        else if (!texture.Equals(_registered)) _gpu.Bindless.Update(_handle, texture);
        _registered = texture;
        return _handle.Index;
    }

    /// <summary>
    /// <see cref="Index"/>, or <paramref name="fallback"/> for a null texture.
    /// </summary>
    public uint IndexOr(in SampledTexture texture, uint fallback) => texture.IsNull ? fallback : Index(texture);

    /// <summary>
    /// Frees the entry.
    /// </summary>
    public void Dispose()
    {
        if (_has) _gpu.Bindless.Free(_handle);
    }
}
