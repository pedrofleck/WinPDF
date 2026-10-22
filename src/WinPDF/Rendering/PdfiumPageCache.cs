using WinPDF.Interop.Pdfium;

namespace WinPDF.Rendering;

/// <summary>
/// LRU cache of the most recently opened <c>FPDF_PAGE</c> handles (and their optional lazy <c>FPDF_TEXTPAGE</c>
/// and <c>FPDF_PAGELINK</c> companions).
/// </summary>
/// <remarks>
/// <para>
/// <c>FPDF_LoadPage</c> and <c>FPDFText_LoadPage</c> parse page dictionaries, resolve inherited attributes,
/// decompress zlib content streams, and build character layout tables. Keeping recent handles alive makes
/// zoom changes, reverse scrolling, cursor hit-testing, and text selection skip all of that CPU work.
/// </para>
/// <para>
/// <b>Not thread-safe by design:</b> every member must be called on the dedicated PDFium thread. In steady
/// state a miss at capacity recycles the evicted list node, so the cache performs no managed allocations.
/// Companion <c>FPDF_PAGELINK</c> and <c>FPDF_TEXTPAGE</c> handles are always disposed strictly before their
/// owning <c>FPDF_PAGE</c>.
/// </para>
/// </remarks>
internal sealed class PdfiumPageCache : IDisposable
{
    /// <summary>Default number of page handles retained.</summary>
    internal const int DefaultCapacity = 8;

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
    internal int Capacity { get; private set; }

    /// <summary>
    /// Updates the maximum number of retained page handles, evicting oldest entries if necessary.
    /// </summary>
    /// <param name="newCapacity">The new capacity (minimum 1).</param>
    internal void SetCapacity(int newCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(newCapacity, 1);
        Capacity = newCapacity;
        while (_lru.Count > Capacity)
        {
            LinkedListNode<Entry> tail = _lru.Last!;
            _lru.RemoveLast();
            _map.Remove((tail.Value.DocumentId, tail.Value.PageIndex));
            tail.Value.DisposeHandles();
        }
    }

    /// <summary>Gets the number of page handles currently retained.</summary>
    internal int Count => _map.Count;

    /// <summary>
    /// Attempts to retrieve an already-cached page handle without loading a new one or evicting existing entries.
    /// </summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="page">The cached page handle if present.</param>
    /// <param name="textPage">The cached text page handle if already loaded for that page.</param>
    /// <returns><see langword="true"/> if the page is currently in the cache; otherwise <see langword="false"/>.</returns>
    internal bool TryGetCached(
        long documentId,
        int pageIndex,
        out PdfiumPageHandle? page,
        out PdfiumTextPageHandle? textPage)
    {
        if (_map.TryGetValue((documentId, pageIndex), out LinkedListNode<Entry>? hit))
        {
            page = hit.Value.Page;
            textPage = hit.Value.TextPage;
            return true;
        }

        page = null;
        textPage = null;
        return false;
    }

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
        LinkedListNode<Entry>? node = GetOrLoadNode(document, documentId, pageIndex);
        return node?.Value.Page;
    }

    /// <summary>
    /// Returns the cached <c>FPDF_TEXTPAGE</c> handle for the specified page, loading and attaching it to the
    /// cached page entry on first access.
    /// </summary>
    /// <param name="document">Owning document handle.</param>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <returns>The text page handle, or <see langword="null"/> when the page or its text stream cannot be loaded.</returns>
    internal PdfiumTextPageHandle? GetOrLoadTextPage(PdfiumDocumentHandle document, long documentId, int pageIndex)
    {
        LinkedListNode<Entry>? node = GetOrLoadNode(document, documentId, pageIndex);
        if (node is null)
        {
            return null;
        }

        Entry entry = node.Value;
        if (entry.TextPage is { IsInvalid: false } existingTextPage)
        {
            return existingTextPage;
        }

        entry.TextPage?.Dispose();
        PdfiumTextPageHandle textPage = PdfiumNative.FPDFText_LoadPage(entry.Page);
        if (textPage.IsInvalid)
        {
            textPage.Dispose();
            entry.TextPage = null;
            return null;
        }

        entry.TextPage = textPage;
        return textPage;
    }

    /// <summary>
    /// Returns the cached <c>FPDF_PAGELINK</c> web-link handle for the specified page, loading it on first access.
    /// </summary>
    /// <param name="document">Owning document handle.</param>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <returns>The web-link handle, or <see langword="null"/> if none could be loaded.</returns>
    internal PdfiumPageLinkHandle? GetOrLoadWebLinks(PdfiumDocumentHandle document, long documentId, int pageIndex)
    {
        LinkedListNode<Entry>? node = GetOrLoadNode(document, documentId, pageIndex);
        if (node is null)
        {
            return null;
        }

        Entry entry = node.Value;
        if (entry.WebLinksLoaded)
        {
            return entry.WebLinks;
        }

        PdfiumTextPageHandle? textPage = GetOrLoadTextPage(document, documentId, pageIndex);
        entry.WebLinksLoaded = true;
        if (textPage is null)
        {
            return null;
        }

        PdfiumPageLinkHandle webLinks = PdfiumNative.FPDFLink_LoadWebLinks(textPage);
        if (webLinks.IsInvalid)
        {
            webLinks.Dispose();
            entry.WebLinks = null;
            return null;
        }

        entry.WebLinks = webLinks;
        return webLinks;
    }

    private LinkedListNode<Entry>? GetOrLoadNode(PdfiumDocumentHandle document, long documentId, int pageIndex)
    {
        (long, int) key = (documentId, pageIndex);

        if (_map.TryGetValue(key, out LinkedListNode<Entry>? hit))
        {
            if (!ReferenceEquals(hit, _lru.First))
            {
                _lru.Remove(hit);
                _lru.AddFirst(hit);
            }

            return hit;
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
            // Recycle the least recently used node: close its companion handles and page, then reuse the node.
            _lru.RemoveLast();
            _map.Remove((victim.Value.DocumentId, victim.Value.PageIndex));
            victim.Value.DisposeHandles();

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
        return node;
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
                node.Value.DisposeHandles();
            }

            node = next;
        }
    }

    /// <summary>
    /// Closes every cached page of a document outside [currentPage - margin, currentPage + margin].
    /// </summary>
    /// <param name="documentId">Engine-assigned document id.</param>
    /// <param name="currentPage">Zero-based active page index.</param>
    /// <param name="margin">Number of adjacent pages to retain (default 1).</param>
    internal void TrimPagesOutside(long documentId, int currentPage, int margin = 1)
    {
        LinkedListNode<Entry>? node = _lru.First;
        while (node is not null)
        {
            LinkedListNode<Entry>? next = node.Next;
            if (node.Value.DocumentId == documentId && Math.Abs(node.Value.PageIndex - currentPage) > margin)
            {
                _map.Remove((documentId, node.Value.PageIndex));
                _lru.Remove(node);
                node.Value.DisposeHandles();
            }

            node = next;
        }
    }

    /// <summary>Closes every cached page of every document.</summary>
    internal void Clear()
    {
        foreach (Entry entry in _lru)
        {
            entry.DisposeHandles();
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
        internal PdfiumTextPageHandle? TextPage;
        internal PdfiumPageLinkHandle? WebLinks;
        internal bool WebLinksLoaded;

        internal void DisposeHandles()
        {
            WebLinks?.Dispose();
            WebLinks = null;
            WebLinksLoaded = false;

            TextPage?.Dispose();
            TextPage = null;

            Page.Dispose();
        }
    }
}
