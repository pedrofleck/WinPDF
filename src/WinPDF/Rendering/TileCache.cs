namespace WinPDF.Rendering;

/// <summary>
/// Kind of bitmap stored in the <see cref="TileCache"/>.
/// </summary>
public enum TileKind
{
    /// <summary>A 512×512 (or clipped edge) tile of a magnified page.</summary>
    Tile,

    /// <summary>A whole page rasterized in a single call (the base Level 0 when <c>ZoomStep == 0</c>).</summary>
    FullPage,

    /// <summary>A sidebar thumbnail.</summary>
    Thumbnail,
}

/// <summary>
/// Identity of a cached bitmap: <c>(documentId, pageIndex, Zq, tileX, tileY, renderFlags)</c> plus the
/// rotation and DPI scale that also change the pixels.
/// </summary>
/// <param name="DocumentId">Engine-assigned document id.</param>
/// <param name="PageIndex">Zero-based page index.</param>
/// <param name="Kind">Bitmap kind.</param>
/// <param name="ZoomStep">Quantized zoom as the integer exponent of √2 (see <see cref="ZoomQuantizer.ToStep"/>).</param>
/// <param name="TileX">Tile column (0 for non-tile kinds).</param>
/// <param name="TileY">Tile row (0 for non-tile kinds).</param>
/// <param name="RenderFlags">PDFium render flags used.</param>
/// <param name="Rotation">User rotation in degrees applied on top of the page's <c>/Rotate</c>.</param>
/// <param name="ScalePercent"><c>RasterizationScale × 100</c>, rounded.</param>
public readonly record struct TileKey(
    long DocumentId,
    int PageIndex,
    TileKind Kind,
    int ZoomStep,
    int TileX,
    int TileY,
    int RenderFlags,
    int Rotation,
    int ScalePercent)
{
    /// <summary>
    /// Gets a value indicating whether this key addresses the protected Level 0 base bitmap
    /// (a full page at <c>Zq == 1.0</c>).
    /// </summary>
    public bool IsBaseLevel => Kind == TileKind.FullPage && ZoomStep == 0;
}

/// <summary>
/// Reference-counted cache payload. The cache holds one reference; every <see cref="TileCache.TryGet"/> hit adds
/// another that the caller must give back with <see cref="Release"/>. The underlying resource is disposed only
/// when the last reference is released, so eviction can never pull a bitmap out from under a draw call.
/// </summary>
public abstract class TileCacheEntry
{
    private int _referenceCount = 1;

    /// <summary>Gets the memory this entry accounts for in the byte budget (e.g. <c>stride × height</c>).</summary>
    public abstract long SizeInBytes { get; }

    /// <summary>
    /// Gets a value indicating whether the payload lives on a GPU device and must be dropped on device loss.
    /// </summary>
    public virtual bool IsDeviceBound => false;

    /// <summary>Attempts to add a reference.</summary>
    /// <returns><see langword="true"/> if the entry was still alive; otherwise <see langword="false"/>.</returns>
    public bool TryRetain()
    {
        int current = Volatile.Read(ref _referenceCount);
        while (current > 0)
        {
            int observed = Interlocked.CompareExchange(ref _referenceCount, current + 1, current);
            if (observed == current)
            {
                return true;
            }

            current = observed;
        }

        return false;
    }

    /// <summary>Adds a reference to an entry known to be alive.</summary>
    /// <exception cref="ObjectDisposedException">The entry was already disposed.</exception>
    public void Retain()
    {
        if (!TryRetain())
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    /// <summary>Releases one reference, disposing the payload when none remain.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _referenceCount) == 0)
        {
            DisposeResource();
        }
    }

    /// <summary>Disposes the underlying bitmap once the last reference is released.</summary>
    protected abstract void DisposeResource();
}

/// <summary>
/// Thread-safe, byte-bounded LRU cache for rendered bitmaps (default 256 MB).
/// </summary>
/// <remarks>
/// <para>
/// Level 0 base bitmaps (<see cref="TileKey.IsBaseLevel"/>) live in a separate, smaller LRU (default 64 MB) so
/// that a burst of high-zoom tiles can never evict the page images used while gesturing; they are evicted last.
/// The two pools share the total budget: tiles may use whatever the base pool leaves free.
/// </para>
/// <para>
/// Eviction only drops the cache's reference; the payload is disposed when readers are done with it
/// (see <see cref="TileCacheEntry"/>).
/// </para>
/// </remarks>
public sealed class TileCache : IDisposable
{
    /// <summary>Default total budget: 256 MB.</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>Default budget reserved for the Level 0 pool: 64 MB.</summary>
    public const long DefaultMaxBaseBytes = 64L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<TileKey, LinkedListNode<Item>> _map = new();
    private readonly LinkedList<Item> _mainLru = new();
    private readonly LinkedList<Item> _baseLru = new();
    private readonly long _maxBytes;
    private readonly long _maxBaseBytes;
    private long _mainBytes;
    private long _baseBytes;
    private bool _disposed;

    /// <summary>Initializes a cache with the default 256 MB / 64 MB budgets.</summary>
    public TileCache()
        : this(DefaultMaxBytes, DefaultMaxBaseBytes)
    {
    }

    /// <summary>Initializes a cache.</summary>
    /// <param name="maxBytes">Total byte budget across both pools.</param>
    /// <param name="maxBaseBytes">Byte budget of the Level 0 pool (clamped to <paramref name="maxBytes"/>).</param>
    public TileCache(long maxBytes, long maxBaseBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBaseBytes);
        _maxBytes = maxBytes;
        _maxBaseBytes = Math.Min(maxBaseBytes, maxBytes);
    }

    /// <summary>Gets the total byte budget.</summary>
    public long MaxBytes => _maxBytes;

    /// <summary>Gets the bytes currently held by both pools.</summary>
    public long CurrentBytes
    {
        get
        {
            lock (_gate)
            {
                return _mainBytes + _baseBytes;
            }
        }
    }

    /// <summary>Gets the number of cached entries.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Looks up an entry, marks it most recently used and adds a reference for the caller.
    /// </summary>
    /// <param name="key">Entry key.</param>
    /// <param name="entry">The retained entry; the caller must call <see cref="TileCacheEntry.Release"/>.</param>
    /// <returns><see langword="true"/> on a hit.</returns>
    public bool TryGet(in TileKey key, out TileCacheEntry? entry)
    {
        lock (_gate)
        {
            if (!_disposed && _map.TryGetValue(key, out LinkedListNode<Item>? node))
            {
                LinkedList<Item> lru = key.IsBaseLevel ? _baseLru : _mainLru;
                if (!ReferenceEquals(node, lru.First))
                {
                    lru.Remove(node);
                    lru.AddFirst(node);
                }

                if (node.Value.Entry.TryRetain())
                {
                    entry = node.Value.Entry;
                    return true;
                }
            }
        }

        entry = null;
        return false;
    }

    /// <summary>
    /// Inserts (or replaces) an entry. The cache takes its own reference; the caller keeps the one it created
    /// the entry with and must still release it when done.
    /// </summary>
    /// <param name="key">Entry key.</param>
    /// <param name="entry">Entry to cache.</param>
    /// <returns>
    /// <see langword="true"/> if the entry was cached; <see langword="false"/> if it is larger than its pool's
    /// budget (or the cache is disposed) and was therefore not stored.
    /// </returns>
    public bool Add(in TileKey key, TileCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        bool isBase = key.IsBaseLevel;
        long size = Math.Max(0, entry.SizeInBytes);
        List<TileCacheEntry>? evicted = null;
        bool stored = false;

        lock (_gate)
        {
            long poolLimit = isBase ? _maxBaseBytes : _maxBytes;
            if (!_disposed && size <= poolLimit && entry.TryRetain())
            {
                if (_map.Remove(key, out LinkedListNode<Item>? existing))
                {
                    RemoveNode(existing, key.IsBaseLevel, ref evicted);
                }

                LinkedList<Item> lru = isBase ? _baseLru : _mainLru;
                _map[key] = lru.AddFirst(new Item(key, entry, size));
                if (isBase)
                {
                    _baseBytes += size;
                }
                else
                {
                    _mainBytes += size;
                }

                EnforceBudgets(ref evicted);
                stored = true;
            }
        }

        ReleaseAll(evicted);
        return stored;
    }

    /// <summary>Removes one entry, if present.</summary>
    /// <param name="key">Entry key.</param>
    /// <returns><see langword="true"/> if an entry was removed.</returns>
    public bool Remove(in TileKey key)
    {
        List<TileCacheEntry>? evicted = null;
        bool removed;
        lock (_gate)
        {
            removed = _map.Remove(key, out LinkedListNode<Item>? node);
            if (removed)
            {
                RemoveNode(node!, key.IsBaseLevel, ref evicted);
            }
        }

        ReleaseAll(evicted);
        return removed;
    }

    /// <summary>Removes every entry of a document (call when it is closed).</summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    public void RemoveDocument(long documentId) => RemoveWhere(key => key.DocumentId == documentId);

    /// <summary>Removes every entry of a page (call when its annotations change).</summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    public void RemovePage(long documentId, int pageIndex)
        => RemoveWhere(key => key.DocumentId == documentId && key.PageIndex == pageIndex);

    /// <summary>
    /// Drops all GPU-bound entries after <c>CanvasDevice.DeviceLost</c>; CPU-backed entries survive.
    /// </summary>
    public void HandleDeviceLost() => RemoveWhere(static (_, entry) => entry.IsDeviceBound);

    /// <summary>Removes every entry.</summary>
    public void Clear() => RemoveWhere(static (_, _) => true);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Clear();
    }

    private void RemoveWhere(Func<TileKey, bool> predicate) => RemoveWhere((key, _) => predicate(key));

    private void RemoveWhere(Func<TileKey, TileCacheEntry, bool> predicate)
    {
        List<TileCacheEntry>? evicted = null;
        lock (_gate)
        {
            List<TileKey>? doomed = null;
            foreach ((TileKey key, LinkedListNode<Item> node) in _map)
            {
                if (predicate(key, node.Value.Entry))
                {
                    (doomed ??= new List<TileKey>()).Add(key);
                }
            }

            if (doomed is not null)
            {
                foreach (TileKey key in doomed)
                {
                    if (_map.Remove(key, out LinkedListNode<Item>? node))
                    {
                        RemoveNode(node, key.IsBaseLevel, ref evicted);
                    }
                }
            }
        }

        ReleaseAll(evicted);
    }

    // Caller holds _gate. The node must already be removed from _map.
    private void RemoveNode(LinkedListNode<Item> node, bool isBase, ref List<TileCacheEntry>? evicted)
    {
        if (isBase)
        {
            _baseLru.Remove(node);
            _baseBytes -= node.Value.Size;
        }
        else
        {
            _mainLru.Remove(node);
            _mainBytes -= node.Value.Size;
        }

        (evicted ??= new List<TileCacheEntry>()).Add(node.Value.Entry);
    }

    // Caller holds _gate. Evicts tiles first, Level 0 bitmaps only as a last resort.
    private void EnforceBudgets(ref List<TileCacheEntry>? evicted)
    {
        while (_baseBytes > _maxBaseBytes && _baseLru.Last is { } baseTail)
        {
            _map.Remove(baseTail.Value.Key);
            RemoveNode(baseTail, isBase: true, ref evicted);
        }

        while (_mainBytes + _baseBytes > _maxBytes)
        {
            if (_mainLru.Last is { } mainTail)
            {
                _map.Remove(mainTail.Value.Key);
                RemoveNode(mainTail, isBase: false, ref evicted);
            }
            else if (_baseLru.Last is { } baseLast)
            {
                _map.Remove(baseLast.Value.Key);
                RemoveNode(baseLast, isBase: true, ref evicted);
            }
            else
            {
                break;
            }
        }
    }

    // Always outside the lock: disposing a payload may enqueue PDFium work or touch GPU resources.
    private static void ReleaseAll(List<TileCacheEntry>? evicted)
    {
        if (evicted is null)
        {
            return;
        }

        foreach (TileCacheEntry entry in evicted)
        {
            entry.Release();
        }
    }

    private sealed class Item
    {
        internal Item(TileKey key, TileCacheEntry entry, long size)
        {
            Key = key;
            Entry = entry;
            Size = size;
        }

        internal TileKey Key { get; }

        internal TileCacheEntry Entry { get; }

        internal long Size { get; }
    }
}
