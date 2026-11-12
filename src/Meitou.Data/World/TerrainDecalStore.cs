using System.Numerics;
using System.Runtime.InteropServices;

namespace Meitou.Data.World;

/// <summary>
/// The terrain's blood decals by cell (the game's <c>TerrainDecalsManager</c>, docs/game/blood-decals.md): adding a decal, the growing pool under a
/// bleeding character that stands still, and the ageing. The renderer draws the cells near the camera from <see cref="In"/>;
/// <see cref="CellVersion"/> tells it when a cell changed. Not thread-safe: the host owns it on one thread.
/// </summary>
/// <remarks>
/// Inside the store a decal's <see cref="TerrainDecal.Life"/> is the <see cref="Clock"/> time it runs out at, so ageing touches only the cells where
/// one has (<see cref="LifeLeft"/> gives the seconds left); <see cref="All"/> and <see cref="Load"/> use seconds left, as a save holds them.
/// </remarks>
public sealed class TerrainDecalStore
{
    /// <summary>
    /// The life a decal is made with, in seconds of game time.
    /// </summary>
    public const float NewLife = 99;
    /// <summary>
    /// The largest a bleeding pool grows.
    /// </summary>
    public const float PoolMax = 2;

    /// <summary>
    /// A bleeding character's pool: where it lies and how big it has grown (the game's <c>BleedToken</c>).
    /// </summary>
    record struct BleedToken(Vector2 At, float Size);

    /// <summary>
    /// A cell's decals, where in the list the first pool at each place is (pools are found by place every tick) and the soonest any runs out.
    /// </summary>
    sealed class Cell
    {
        /// <summary>
        /// The cell's decals in the order they were placed.
        /// </summary>
        public readonly List<TerrainDecal> Decals = [];
        /// <summary>
        /// The index in <see cref="Decals"/> of the first pool at each cell place.
        /// </summary>
        public readonly Dictionary<int, int> Pools = [];
        /// <summary>
        /// The soonest <see cref="Clock"/> time a decal of the cell runs out.
        /// </summary>
        public float Soonest = float.PositiveInfinity;

        /// <summary>
        /// Appends <paramref name="d"/>, noting it as the place's pool if it is the first there.
        /// </summary>
        public void Add(in TerrainDecal d)
        {
            if (d.Type == TerrainDecalType.Pool) Pools.TryAdd(Place(d.LocalX, d.LocalZ), Decals.Count);
            Soonest = MathF.Min(Soonest, d.Life);
            Decals.Add(d);
        }

        /// <summary>
        /// The index of the first pool at the cell place (<paramref name="x"/>, <paramref name="z"/>), or −1.
        /// </summary>
        public int PoolAt(short x, short z) => Pools.TryGetValue(Place(x, z), out int i) ? i : -1;

        /// <summary>
        /// Removes the decals that ran out by <paramref name="clock"/>; whether any did.
        /// </summary>
        public bool Expire(float clock)
        {
            int removed = Decals.RemoveAll(d => d.Life <= clock);
            if (removed == 0) return false;
            Pools.Clear();
            Soonest = float.PositiveInfinity;
            for (int i = 0; i < Decals.Count; i++)
            {
                var d = Decals[i];
                if (d.Type == TerrainDecalType.Pool) Pools.TryAdd(Place(d.LocalX, d.LocalZ), i);
                Soonest = MathF.Min(Soonest, d.Life);
            }
            return true;
        }

        /// <summary>
        /// The key of a cell place: x in the low 16 bits, z in the high.
        /// </summary>
        static int Place(short x, short z) => (ushort)x | (ushort)z << 16;
    }

    /// <summary>
    /// For each <see cref="TerrainDecalType"/>, the sizes in texels of its textures, by variant.
    /// </summary>
    readonly (int Width, int Height)[][] _sizes;
    /// <summary>
    /// The cells that hold decals, by key.
    /// </summary>
    readonly Dictionary<uint, Cell> _cells = [];
    /// <summary>
    /// Each cell's <see cref="CellVersion"/> and <see cref="CellRewrite"/>.
    /// </summary>
    readonly Dictionary<uint, int> _versions = [], _rewrites = [];
    /// <summary>
    /// Per cell, the decals placed or grown since its last rewrite, in that order.
    /// </summary>
    readonly Dictionary<uint, List<TerrainDecal>> _appended = [];
    /// <summary>
    /// The pool of each bleeding character, by character.
    /// </summary>
    readonly Dictionary<long, BleedToken> _bleeds = [];
    /// <summary>
    /// Scratch of <see cref="Age"/>: the cells where decals ran out.
    /// </summary>
    readonly List<uint> _touched = [];
    /// <summary>
    /// Scratch of <see cref="Age"/>: the cells left with no decals.
    /// </summary>
    readonly List<uint> _emptied = [];
    /// <summary>
    /// The last version handed out.
    /// </summary>
    int _nextVersion;
    /// <summary>
    /// See <see cref="Clock"/>.
    /// </summary>
    float _clock;

    /// <param name="sizes">For each <see cref="TerrainDecalType"/>, the sizes in texels of its folder's textures (the variants); a type without any
    /// makes no decals.</param>
    public TerrainDecalStore(IReadOnlyList<IReadOnlyList<(int Width, int Height)>> sizes)
    {
        this._sizes = new (int, int)[4][];
        for (int t = 0; t < 4; t++) this._sizes[t] = t < sizes.Count ? [.. sizes[t]] : [];
    }

    /// <summary>
    /// Changes with every change of any cell.
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// The cells that hold decals.
    /// </summary>
    public IEnumerable<uint> Cells => _cells.Keys;

    /// <summary>
    /// How many decals the cells hold (a decal over several cells counts once per cell).
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// The game seconds the store has aged by (<see cref="Age"/>): the clock the stored decals' lives run out on.
    /// </summary>
    public float Clock => _clock;

    /// <summary>
    /// The seconds of game time <paramref name="d"/> (one of <see cref="In"/> or <see cref="Appended"/>) has left; negative past its end.
    /// </summary>
    public float LifeLeft(in TerrainDecal d) => d.Life - _clock;

    /// <summary>
    /// The decals of a cell (empty when it has none); their <see cref="TerrainDecal.Life"/> is on <see cref="Clock"/> (<see cref="LifeLeft"/>).
    /// </summary>
    public ReadOnlySpan<TerrainDecal> In(uint key) => _cells.TryGetValue(key, out var cell) ? CollectionsMarshal.AsSpan(cell.Decals) : default;

    /// <summary>
    /// A number that changes whenever <paramref name="key"/>'s decals change (0 for a cell that never had any).
    /// </summary>
    public int CellVersion(uint key) => _versions.GetValueOrDefault(key);

    /// <summary>
    /// A number that changes when decals leave <paramref name="key"/> (ageing, a new world): a texture of the cell must be drawn again from
    /// <see cref="In"/>. While it stays, the cell's changes are only additions: <see cref="Appended"/>, in order, drawn over what the texture holds
    /// (the original likely draws a cell's new decals onto its texture too: it clears it only for a new cell, docs/game/blood-decals.md).
    /// </summary>
    public int CellRewrite(uint key) => _rewrites.GetValueOrDefault(key);

    /// <summary>
    /// The decals placed in <paramref name="key"/> or grown there since its last rewrite (<see cref="CellRewrite"/>), oldest first.
    /// </summary>
    public ReadOnlySpan<TerrainDecal> Appended(uint key) => _appended.TryGetValue(key, out var log) ? CollectionsMarshal.AsSpan(log) : default;

    /// <summary>
    /// Every decal and pool gone (a new world).
    /// </summary>
    public void Clear()
    {
        foreach (var key in _cells.Keys) Rewrite(key);
        _cells.Clear();
        _appended.Clear();
        _bleeds.Clear();
        Count = 0;
        _clock = 0;
    }

    /// <summary>
    /// Every cell's copy of every decal, cell by cell, its life in seconds left (what a save holds).
    /// </summary>
    public IEnumerable<TerrainDecal> All => _cells.Values.SelectMany(cell => cell.Decals).Select(d => d with { Life = d.Life - _clock });

    /// <summary>
    /// Decals as a save holds them, each into its own cell as it is (no placing again); one of no known type is skipped.
    /// </summary>
    public void Load(IEnumerable<TerrainDecal> decals)
    {
        foreach (var d in decals)
        {
            if ((int)d.Type >= _sizes.Length) continue;
            var stored = d with { Life = _clock + d.Life };
            CellOf(d.Key).Add(stored);
            Count++;
            Append(d.Key, stored);
        }
    }

    /// <summary>
    /// A new decal (<c>FUN_1408dce50</c>, <c>FUN_1408dc6b0</c>): a random texture of <paramref name="type"/>'s, turned so its width runs along
    /// <paramref name="direction"/> (X/Z), its width and height <paramref name="size"/> × the texture's texels × the cell's 288 units / 2048, stored
    /// in each cell its corners fall in. Nothing when the type has no textures or <paramref name="life"/> is not positive.
    /// </summary>
    public void Add(TerrainDecalType type, Vector3 at, Vector2 direction, float size, uint abgr, float life, Random random)
    {
        var variants = _sizes[(int)type];
        if (variants.Length == 0 || !(life > 0)) return;
        float angle = MathF.Atan2(direction.X, direction.Y);
        var decal = new TerrainDecal(0, type, (byte)random.Next(variants.Length), 0, 0, 0, 0, (short)(int)(angle * 1000), abgr, _clock + life);
        Place(decal, new Vector2(at.X, at.Z), size);
    }

    /// <summary>
    /// A bleeding character standing still (<c>FUN_1408dcfa0</c>): <paramref name="bleeding"/> × <paramref name="dt"/> × 0.2 grows the pool at its
    /// feet, more slowly the bigger it is (by that over the larger of 1 and ten times the size), up to <see cref="PoolMax"/>; moved more than √10
    /// units away, it starts a new pool. The pool's decal is replaced when its size in the cell changes.
    /// </summary>
    public void Bleed(long who, Vector3 at, float bleeding, float dt, uint abgr, Random random)
    {
        float grow = bleeding * dt * 0.2f;
        var here = new Vector2(at.X, at.Z);
        if (!_bleeds.TryGetValue(who, out var token)) token = new BleedToken(here, grow);
        else if (Vector2.DistanceSquared(token.At, here) <= 10) token.Size = MathF.Min(token.Size + grow / MathF.Max(1, token.Size * 10), PoolMax);
        else token = new BleedToken(here, grow);
        _bleeds[who] = token;

        uint key = TerrainDecalCells.Key(token.At.X, token.At.Y);
        var (lx, lz) = Local(key, token.At);
        if (_cells.TryGetValue(key, out var cell) && cell.PoolAt(lx, lz) is int i and >= 0)
        {
            var d = cell.Decals[i];
            var (w, h) = Texels(d);
            int half = (short)(int)(w * token.Size * 10);
            if (half == d.HalfWidth && (short)(int)(h * token.Size * 10) == d.HalfHeight) return;
            Regrow(d, token.At, token.Size);
            return;
        }
        Add(TerrainDecalType.Pool, new Vector3(token.At.X, at.Y, token.At.Y), new Vector2(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f),
            token.Size, abgr, NewLife, random);
    }

    /// <summary>
    /// The character has stopped bleeding or gone: its next pool starts anew.
    /// </summary>
    public void Stanch(long who) => _bleeds.Remove(who);

    /// <summary>
    /// Ages every decal by <paramref name="dt"/> seconds of game time (the scaled frame time: nothing while paused) and removes those whose life ran
    /// out, except in the cells <paramref name="drawn"/> says are on a texture now (the game keeps what it has drawn until the cell's texture is
    /// given to another; <c>FUN_1408dd860</c>).
    /// </summary>
    public void Age(float dt, Func<uint, bool> drawn)
    {
        if (dt <= 0 || Count == 0) return;
        _clock += dt;
        _emptied.Clear();
        foreach (var (key, cell) in _cells)
        {
            if (cell.Soonest > _clock || drawn(key)) continue;
            int before = cell.Decals.Count;
            if (!cell.Expire(_clock)) continue;
            Count -= before - cell.Decals.Count;
            _touched.Add(key);
            if (cell.Decals.Count == 0) _emptied.Add(key);
        }
        foreach (var key in _emptied) { _cells.Remove(key); _appended.Remove(key); }
        foreach (var key in _touched) Rewrite(key);
        _touched.Clear();
    }

    /// <summary>
    /// The cell of <paramref name="key"/>, made when it has none.
    /// </summary>
    Cell CellOf(uint key)
    {
        if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = new Cell();
        return cell;
    }

    /// <summary>
    /// The size in texels of <paramref name="d"/>'s texture; (0, 0) for an unknown variant.
    /// </summary>
    (int Width, int Height) Texels(in TerrainDecal d)
    {
        var variants = _sizes[(int)d.Type];
        return d.Variant < variants.Length ? variants[d.Variant] : (0, 0);
    }

    /// <summary>
    /// The place of <paramref name="at"/> in cell <paramref name="key"/>, in thousandths of the cell's size.
    /// </summary>
    static (short X, short Z) Local(uint key, Vector2 at)
    {
        var (x0, z0) = TerrainDecalCells.Origin(key);
        return ((short)(int)((at.X - x0) / TerrainDecalCells.Size * 1000), (short)(int)((at.Y - z0) / TerrainDecalCells.Size * 1000));
    }

    /// <summary>
    /// The four corners of a decal of <paramref name="size"/> centred on <paramref name="at"/> (<c>FUN_1408dc6b0</c>'s quad).
    /// </summary>
    void Corners(in TerrainDecal d, Vector2 at, float size, Span<Vector2> corners)
    {
        var (w, h) = Texels(d);
        float ex = size * w * TerrainDecalCells.Size / 4096, ez = size * h * TerrainDecalCells.Size / 4096;
        float angle = d.Rotation / 1000f, s = MathF.Sin(angle), c = MathF.Cos(angle);
        var width = new Vector2(s, c) * ex;
        var height = new Vector2(c, -s) * ez;
        corners[0] = at + width + height;
        corners[1] = at - width + height;
        corners[2] = at + width - height;
        corners[3] = at - width - height;
    }

    /// <summary>
    /// Places <paramref name="d"/> at <paramref name="at"/>, sized <paramref name="size"/>: a copy in each cell its corners reach.
    /// </summary>
    void Place(TerrainDecal d, Vector2 at, float size)
    {
        var (w, h) = Texels(d);
        d.HalfWidth = (short)(int)(w * size * 10);
        d.HalfHeight = (short)(int)(h * size * 10);
        Span<Vector2> corners = stackalloc Vector2[4];
        Corners(d, at, size, corners);
        Span<uint> keys = stackalloc uint[4];
        int n = 0;
        foreach (var corner in corners)
        {
            uint key = TerrainDecalCells.Key(corner.X, corner.Y);
            if (keys[..n].Contains(key)) continue;
            keys[n++] = key;
            var (lx, lz) = Local(key, at);
            var copy = d with { Key = key, LocalX = lx, LocalZ = lz };
            CellOf(key).Add(copy);
            Count++;
            Append(key, copy);
        }
    }

    /// <summary>
    /// A pool <paramref name="d"/> (a cell's copy, centred on <paramref name="at"/>) grown to <paramref name="size"/>: each cell's copy is replaced
    /// where it lies in the list, and the cells its larger corners now reach get one. A pool only grows, so no cell loses it, and a cell that had it
    /// is drawn again (<see cref="CellRewrite"/>).
    /// </summary>
    void Regrow(TerrainDecal d, Vector2 at, float size)
    {
        var (w, h) = Texels(d);
        var old = d;
        d.HalfWidth = (short)(int)(w * size * 10);
        d.HalfHeight = (short)(int)(h * size * 10);
        Span<Vector2> corners = stackalloc Vector2[4];
        Corners(d, at, size, corners);
        Span<uint> keys = stackalloc uint[4];
        int n = 0;
        foreach (var corner in corners)
        {
            uint key = TerrainDecalCells.Key(corner.X, corner.Y);
            if (keys[..n].Contains(key)) continue;
            keys[n++] = key;
            var (lx, lz) = Local(key, at);
            var cell = CellOf(key);
            var copy = d with { Key = key, LocalX = lx, LocalZ = lz };
            // The first pool at the place is the copy when it is this one (the same texture and turn); otherwise the list is searched.
            int at0 = cell.PoolAt(lx, lz);
            if (at0 >= 0 && !Same(cell.Decals[at0], old))
                at0 = cell.Decals.FindIndex(x => x.Type == old.Type && Same(x, old) && x.LocalX == lx && x.LocalZ == lz);
            if (at0 >= 0)
            {
                // Meitou: a grown pool's cell is drawn again from its list (one pass a frame however often the pool grew), not with the larger copy
                // over the old one: that stacked the pool's translucent edge a hundred times, hardening it, and showed a pop when the cell was
                // redrawn later.
                cell.Decals[at0] = copy with { Life = cell.Decals[at0].Life };
                Rewrite(key);
            }
            else { cell.Add(copy); Count++; Append(key, copy); }
        }

        static bool Same(in TerrainDecal x, in TerrainDecal old) => x.Variant == old.Variant && x.Rotation == old.Rotation;
    }

    /// <summary>
    /// Gives cell <paramref name="key"/> and the store a new version.
    /// </summary>
    void Touch(uint key)
    {
        _versions[key] = ++_nextVersion;
        Version = _nextVersion;
    }

    /// <summary>
    /// A decal added to (or grown in) <paramref name="key"/>'s list: drawn over what the cell's texture holds. A log much longer than the cell's list
    /// is dropped for a redraw.
    /// </summary>
    void Append(uint key, in TerrainDecal d)
    {
        if (!_appended.TryGetValue(key, out var log)) _appended[key] = log = [];
        log.Add(d);
        if (log.Count > 2 * (_cells.TryGetValue(key, out var cell) ? cell.Decals.Count : 0) + 64) Rewrite(key);
        else Touch(key);
    }

    /// <summary>
    /// Decals left <paramref name="key"/> (or its log grew too long): its texture is drawn again from the list.
    /// </summary>
    void Rewrite(uint key)
    {
        _rewrites[key] = ++_nextVersion;
        if (_appended.TryGetValue(key, out var log)) log.Clear();
        Touch(key);
    }
}