using WinPDF.Interop.Pdfium;

namespace WinPDF.Rendering;

/// <summary>
/// LRU cache of the most recently opened <c>FPDF_PAGE</c> handles (default: 10).
/// </summary>
/// <remarks>
/// <para>
/// <c>FPDF_LoadPage</c> parses the page dictionary, resolves inherited attributes and (on first render)
/// decompresses zlib content streams and loads embedded font programs. Keeping recent handles alive makes
/// zoom changes, reverse scrolling and page flips skip all of that CPU work.
/// </para>
/// <para>
/// <b>Not thread-safe by design:</b> every member must be called on the dedicated PDFium thread. In steady
/// state a miss at capacity recycles the evicted list node, so the cache performs no managed allocations.
/// </para>
/// </remarks>
internal sealed class PdfiumPageCache : IDisposable
{
    /// <summary>Default number of page handles retained.</summary>
    internal const int DefaultCapacity = 10;

    private readonly Dictionary<(long DocumentId, int PageIndex), LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new();

    /// <summary>Initializes a new cache.</summary>
    /// <param name="capacity">Maximum number of retained page handles.</param>
    internal PdfiumPageCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _map = new Dictionary<(long, int), LinkedListNode<Entry>>(capacity);
    }

    /// <summary>Gets the maximum number of retained page handles.</summary>
    internal int Capacity { get; }

    /// <summary>Gets the number of page handles currently retained.</summary>
    internal int Count => _map.Count;

    /// <summary>
    /// Returns the cached page handle, loading (and caching) it on a miss. The returned handle is borrowed:
    /// it stays valid until a later call evicts it, so use it within the current job only.
    /// </summary>
    /// <param name="document">Owning document handle.</param>
    /// <param name="documentId">Engine-assigned document id (cache key).</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <returns>The page handle, or <see langword="null"/> when PDFium cannot load the page.</returns>
    internal PdfiumPageHandle? GetOrLoad(PdfiumDocumentHandle document, long documentId, int pageIndex)
    {
        (long, int) key = (documentId, pageIndex);

        if (_map.TryGetValue(key, out LinkedListNode<Entry>? hit))
        {
            if (!ReferenceEquals(hit, _lru.First))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
            }

            return hit.Value.Page;
        }

        PdfiumPageHandle page = PdfiumNative.FPDF_LoadPage(document, pageIndex);
        if (page.IsInvalid)
        {
            page.Dispose();
            return null;
        }

        LinkedListNode<Entry> node;
        if (_map.Count >= Capacity && _lru.Last is { } victim)
        {
            // Recycle the least recently used node: close its page and reuse the node for the new entry.
            _lru.RemoveLast();
            _map.Remove((victim.Value.DocumentId, victim.Value.PageIndex));
            victim.Value.Page.Dispose();

            victim.Value.DocumentId = documentId;
            victim.Value.PageIndex = pageIndex;
            victim.Value.Page = page;
            node = victim;
            _lru.AddFirst(node);
        }
        else
        {
            node = _lru.AddFirst(new Entry(documentId, pageIndex, page));
        }

        _map[key] = node;
        return page;
    }

    /// <summary>
    /// Closes every cached page of a document. Must be called before the document itself is closed.
    /// </summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    internal void RemoveDocument(long documentId)
    {
        LinkedListNode<Entry>? node = _lru.First;
        while (node is not null)
        {
            LinkedListNode<Entry>? next = node.Next;
            if (node.Value.DocumentId == documentId)
            {
                _map.Remove((documentId, node.Value.PageIndex));
                _lru.Remove(node);
                node.Value.Page.Dispose();
            }

            node = next;
        }
    }

    /// <summary>Closes every cached page of every document.</summary>
    internal void Clear()
    {
        foreach (Entry entry in _lru)
        {
            entry.Page.Dispose();
        }

        _lru.Clear();
        _map.Clear();
    }

    /// <inheritdoc/>
    public void Dispose() => Clear();

    private sealed class Entry
    {
        internal Entry(long documentId, int pageIndex, PdfiumPageHandle page)
        {
            DocumentId = documentId;
            PageIndex = pageIndex;
            Page = page;
        }

        internal long DocumentId;
        internal int PageIndex;
        internal PdfiumPageHandle Page;
    }
}
