using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using WinPDF.Models;
using WinPDF.Rendering;

namespace WinPDF.Controls;

/// <summary>
/// Live Win2D wet-ink stroke preview, zero-flicker pending-commit queue, and S Pen hover cursor overlay
/// rendered directly on <see cref="PdfPageCanvas"/> (0.0% idle CPU/GPU when stationary).
/// </summary>
public sealed partial class PdfPageCanvas
{
    private sealed record PendingWetInkStroke(
        int Token,
        List<PdfInkSample> DipSamples,
        bool HasPressureVariation,
        float BaseWidthPt,
        PdfAnnotationColor Color);

    private bool _isDrawingInk;
    private readonly List<PdfInkSample> _wetInkDipSamples = new(256);
    private bool _wetInkHasPressure;
    private float _wetInkBaseWidthPt = 2.0f;
    private PdfAnnotationColor _wetInkColor = PdfAnnotationColor.Blue;
    private readonly List<PendingWetInkStroke> _pendingWetInkStrokes = new(4);
    private int _nextPendingInkToken;

    private bool _hasPenHoverCursor;
    private Point _penHoverDip;
    private bool _penHoverIsEraser;
    private float _penHoverRadiusDip = 3.0f;
    private Windows.UI.Color _penHoverColor = Windows.UI.Color.FromArgb(220, 0, 120, 212);

    /// <summary>
    /// Gets a value indicating whether an active wet-ink stroke, pending ink commit, or pen hover cursor
    /// needs to be rendered during <see cref="DrawTextOverlays"/>.
    /// </summary>
    private bool HasActiveInkOrHoverOverlay
        => _isDrawingInk || _pendingWetInkStrokes.Count > 0 || _hasPenHoverCursor;

    /// <summary>
    /// Computes the consistent eraser tip radius in PDF points so the visual eraser ring
    /// and the eraser hit-test geometry match identically at any zoom level.
    /// </summary>
    /// <param name="zoomFactor">Active scroll viewer zoom factor.</param>
    /// <param name="strokeWidthPt">Current annotation stroke thickness setting in PDF points.</param>
    /// <returns>Eraser radius in PDF points.</returns>
    public static float ComputeEraserRadiusPdfPt(double zoomFactor, double strokeWidthPt)
    {
        float radiusScreenDip = (float)Math.Clamp(10.0 + (strokeWidthPt * 1.15), 11.0, 24.0);
        float radiusPageDip = radiusScreenDip / (float)Math.Max(0.25, zoomFactor);
        return Math.Clamp(radiusPageDip / (float)PageCoordinateTransform.DipsPerPoint, 3.0f, 36.0f);
    }

    /// <summary>
    /// Begins a new live wet-ink stroke in page DIPs (driven by the parent overlay above the scroll viewer).
    /// </summary>
    /// <param name="dipPt">Starting point in this canvas's page DIP coordinates.</param>
    /// <param name="pressure">Normalized stylus pressure in <c>[0, 1]</c>.</param>
    /// <param name="hasPressureVariation">Whether pressure modulates stroke thickness.</param>
    /// <param name="color">Ink color.</param>
    /// <param name="baseWidthPt">Nominal stroke width in PDF points.</param>
    public void BeginExternalInkStroke(
        Point dipPt,
        float pressure,
        bool hasPressureVariation,
        PdfAnnotationColor color,
        float baseWidthPt)
    {
        if (PageInfo is null)
        {
            return;
        }

        Point clamped = ClampToCanvasDip(dipPt);
        _wetInkDipSamples.Clear();
        _wetInkDipSamples.Add(new PdfInkSample((float)clamped.X, (float)clamped.Y, Math.Clamp(pressure, 0.04f, 1.0f)));
        _wetInkHasPressure = hasPressureVariation;
        _wetInkColor = color;
        _wetInkBaseWidthPt = Math.Clamp(baseWidthPt, 0.5f, 32f);
        _isDrawingInk = true;
        _hasPenHoverCursor = false;
        InvalidateOverlays();
    }

    /// <summary>
    /// Appends one or more samples to the active wet-ink stroke and triggers a single overlay redraw.
    /// </summary>
    /// <param name="samples">Chronologically ordered page-DIP samples.</param>
    public void AppendExternalInkSamples(IReadOnlyList<PdfInkSample> samples)
    {
        if (!_isDrawingInk || PageInfo is null || samples.Count == 0)
        {
            return;
        }

        bool changed = false;
        for (int i = 0; i < samples.Count; i++)
        {
            PdfInkSample raw = samples[i];
            Point clamped = ClampToCanvasDip(new Point(raw.X, raw.Y));
            float x = (float)clamped.X;
            float y = (float)clamped.Y;
            float p = Math.Clamp(raw.Pressure, 0.04f, 1.0f);

            if (_wetInkDipSamples.Count > 0)
            {
                PdfInkSample prev = _wetInkDipSamples[^1];
                float dx = x - prev.X;
                float dy = y - prev.Y;
                float distSq = (dx * dx) + (dy * dy);
                if (distSq < 0.25f && (!_wetInkHasPressure || MathF.Abs(p - prev.Pressure) < 0.06f))
                {
                    continue;
                }
            }

            _wetInkDipSamples.Add(new PdfInkSample(x, y, p));
            changed = true;
        }

        if (changed)
        {
            InvalidateOverlays();
        }
    }

    /// <summary>
    /// Finalizes the active wet-ink stroke, converting samples into PDF user space while keeping
    /// the wet-ink preview visible in a pending queue until PDFium finishes rasterizing the persisted annotation.
    /// </summary>
    /// <param name="pdfStroke">Output stroke payload in PDF user space, or <see langword="null"/> if invalid.</param>
    /// <param name="pendingToken">Token to pass to <see cref="ReleasePendingInkStroke"/> once persisted.</param>
    /// <returns><see langword="true"/> if a valid stroke was captured.</returns>
    public bool CompleteExternalInkStrokeToPending(out PdfInkStrokeData? pdfStroke, out int pendingToken)
    {
        pdfStroke = null;
        pendingToken = 0;

        if (!_isDrawingInk || PageInfo is not { } pageInfo || _wetInkDipSamples.Count == 0)
        {
            _isDrawingInk = false;
            _wetInkDipSamples.Clear();
            return false;
        }

        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, RotationAngle);
        List<PdfInkSample> pdfSamples = new(_wetInkDipSamples.Count);
        for (int i = 0; i < _wetInkDipSamples.Count; i++)
        {
            PdfInkSample dip = _wetInkDipSamples[i];
            System.Numerics.Vector2 pdfPt = transform.ToPdf(new System.Numerics.Vector2(dip.X, dip.Y));
            pdfSamples.Add(new PdfInkSample(pdfPt.X, pdfPt.Y, dip.Pressure));
        }

        pdfStroke = new PdfInkStrokeData(pdfSamples, _wetInkHasPressure);
        pendingToken = ++_nextPendingInkToken;
        _pendingWetInkStrokes.Add(new PendingWetInkStroke(
            pendingToken,
            new List<PdfInkSample>(_wetInkDipSamples),
            _wetInkHasPressure,
            _wetInkBaseWidthPt,
            _wetInkColor));

        _isDrawingInk = false;
        _wetInkDipSamples.Clear();
        InvalidateOverlays();
        return true;
    }

    /// <summary>
    /// Removes a pending committed wet-ink preview once PDFium has refreshed the page bitmap.
    /// </summary>
    /// <param name="pendingToken">Token returned by <see cref="CompleteExternalInkStrokeToPending"/>.</param>
    public void ReleasePendingInkStroke(int pendingToken)
    {
        if (pendingToken <= 0 || _pendingWetInkStrokes.Count == 0)
        {
            return;
        }

        for (int i = _pendingWetInkStrokes.Count - 1; i >= 0; i--)
        {
            if (_pendingWetInkStrokes[i].Token == pendingToken)
            {
                _pendingWetInkStrokes.RemoveAt(i);
                InvalidateOverlays();
                return;
            }
        }
    }

    /// <summary>
    /// Cancels the in-progress wet-ink stroke without committing (for example when a 2-finger pinch begins).
    /// </summary>
    public void CancelExternalInkStroke()
    {
        if (!_isDrawingInk && _wetInkDipSamples.Count == 0)
        {
            return;
        }

        _isDrawingInk = false;
        _wetInkDipSamples.Clear();
        InvalidateOverlays();
    }

    /// <summary>
    /// Updates the live S Pen hover / eraser cursor overlay on this page.
    /// Redraws only when coordinates or visual appearance actually change (0% idle CPU/GPU).
    /// </summary>
    /// <param name="dipPt">Pointer coordinates in this canvas's page DIPs.</param>
    /// <param name="isEraser">Whether the cursor should render as an eraser ring rather than a pen nib.</param>
    /// <param name="brushWidthPt">Nominal brush thickness in PDF points.</param>
    /// <param name="color">Active annotation color.</param>
    public void UpdatePenHoverCursor(Point dipPt, bool isEraser, double brushWidthPt, PdfAnnotationColor color)
    {
        if (PageInfo is null)
        {
            return;
        }

        Point clamped = ClampToCanvasDip(dipPt);
        float targetRadiusDip = isEraser
            ? ComputeEraserRadiusPdfPt(ZoomFactor, brushWidthPt) * (float)PageCoordinateTransform.DipsPerPoint
            : Math.Max(1.75f, (float)(Math.Clamp(brushWidthPt, 0.5, 32.0) * PageCoordinateTransform.DipsPerPoint * 0.5));
        Windows.UI.Color targetColor = Windows.UI.Color.FromArgb(220, color.R, color.G, color.B);

        bool moved = !_hasPenHoverCursor ||
                     Math.Abs(clamped.X - _penHoverDip.X) >= 0.35 ||
                     Math.Abs(clamped.Y - _penHoverDip.Y) >= 0.35 ||
                     _penHoverIsEraser != isEraser ||
                     MathF.Abs(targetRadiusDip - _penHoverRadiusDip) >= 0.15f ||
                     _penHoverColor != targetColor;

        if (!moved)
        {
            return;
        }

        _hasPenHoverCursor = true;
        _penHoverDip = clamped;
        _penHoverIsEraser = isEraser;
        _penHoverRadiusDip = targetRadiusDip;
        _penHoverColor = targetColor;
        InvalidateOverlays();
    }

    /// <summary>
    /// Clears the pen hover / eraser cursor from this page if currently visible.
    /// </summary>
    public void ClearPenHoverCursor()
    {
        if (!_hasPenHoverCursor)
        {
            return;
        }

        _hasPenHoverCursor = false;
        InvalidateOverlays();
    }

    /// <summary>
    /// Renders pending committed strokes, the live wet-ink stroke, and the S Pen hover/eraser cursor
    /// inside <see cref="DrawTextOverlays"/>.
    /// </summary>
    private void DrawInkAndHoverOverlays(CanvasDrawingSession ds, double offsetX, double offsetY)
    {
        float ox = (float)offsetX;
        float oy = (float)offsetY;

        // 7A. Pending committed ink strokes (kept visible until PDFium finishes rasterizing the page)
        if (_pendingWetInkStrokes.Count > 0)
        {
            for (int i = 0; i < _pendingWetInkStrokes.Count; i++)
            {
                PendingWetInkStroke pending = _pendingWetInkStrokes[i];
                float widthDip = (float)(pending.BaseWidthPt * PageCoordinateTransform.DipsPerPoint);
                Windows.UI.Color c = Windows.UI.Color.FromArgb(255, pending.Color.R, pending.Color.G, pending.Color.B);
                InkStrokeProcessor.DrawWetInkStroke(ds, pending.DipSamples, pending.HasPressureVariation, widthDip, c, ox, oy);
            }
        }

        // 7B. Active live wet-ink stroke
        if (_isDrawingInk && _wetInkDipSamples.Count > 0)
        {
            float widthDip = (float)(_wetInkBaseWidthPt * PageCoordinateTransform.DipsPerPoint);
            Windows.UI.Color c = Windows.UI.Color.FromArgb(255, _wetInkColor.R, _wetInkColor.G, _wetInkColor.B);
            InkStrokeProcessor.DrawWetInkStroke(ds, _wetInkDipSamples, _wetInkHasPressure, widthDip, c, ox, oy);
        }

        // 7C. S Pen hover / eraser ring cursor
        if (_hasPenHoverCursor)
        {
            float cx = (float)_penHoverDip.X + ox;
            float cy = (float)_penHoverDip.Y + oy;
            float invZoom = 1.0f / (float)Math.Max(0.35, ZoomFactor);
            float haloStroke = Math.Clamp(1.35f * invZoom, 0.65f, 2.5f);

            if (_penHoverIsEraser)
            {
                float r = Math.Max(3.5f * invZoom, _penHoverRadiusDip);
                Windows.UI.Color fill = IsNightMode
                    ? Windows.UI.Color.FromArgb(46, 255, 130, 145)
                    : Windows.UI.Color.FromArgb(52, 232, 95, 110);
                Windows.UI.Color outerRing = IsNightMode
                    ? Windows.UI.Color.FromArgb(215, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(190, 32, 32, 32);
                Windows.UI.Color innerRing = Windows.UI.Color.FromArgb(235, 224, 72, 92);

                ds.FillCircle(cx, cy, r, fill);
                ds.DrawCircle(cx, cy, r + (haloStroke * 0.5f), outerRing, haloStroke);
                ds.DrawCircle(cx, cy, r, innerRing, haloStroke);
            }
            else
            {
                float r = Math.Max(2.2f * invZoom, _penHoverRadiusDip);
                Windows.UI.Color dotFill = Windows.UI.Color.FromArgb(175, _penHoverColor.R, _penHoverColor.G, _penHoverColor.B);
                Windows.UI.Color contrastHalo = IsNightMode
                    ? Windows.UI.Color.FromArgb(220, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(210, 24, 24, 24);

                ds.FillCircle(cx, cy, r, dotFill);
                ds.DrawCircle(cx, cy, r + (haloStroke * 0.75f), contrastHalo, haloStroke);
            }
        }
    }
}
