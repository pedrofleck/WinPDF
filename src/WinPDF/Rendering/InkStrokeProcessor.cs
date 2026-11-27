using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;
using WinPDF.Models;

namespace WinPDF.Rendering;

/// <summary>
/// Pure geometry, smoothing, simplification, variable-width ribbon generation,
/// Win2D wet-ink rendering, PDF <c>/AP</c> serialization, and eraser hit-testing
/// for freehand pen/ink strokes.
/// </summary>
public static class InkStrokeProcessor
{
    private const int EndCapSegments = 6;

    /// <summary>
    /// Shared round-cap/round-join Win2D stroke style for uniform-width wet ink rendering.
    /// </summary>
    public static CanvasStrokeStyle UniformStrokeStyle { get; } = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    /// <summary>
    /// Computes the half-width (radius) for a sample given the base stroke width and stylus pressure.
    /// </summary>
    /// <param name="baseWidth">Nominal stroke width in target units (DIPs or PDF points).</param>
    /// <param name="pressure">Normalized stylus pressure in <c>[0, 1]</c>.</param>
    /// <param name="hasPressureVariation">Whether pressure sensitivity modulates stroke width.</param>
    /// <returns>Half-width in target units.</returns>
    public static float ComputeHalfWidth(float baseWidth, float pressure, bool hasPressureVariation)
    {
        float clampedBase = Math.Clamp(baseWidth, 0.5f, 48f);
        if (!hasPressureVariation)
        {
            return clampedBase * 0.5f;
        }

        float p = Math.Clamp(pressure, 0.04f, 1.0f);
        // Natural calligraphy response: light touch -> ~0.32x, nominal 0.5 -> ~1.05x, firm press -> ~1.78x.
        float scale = 0.22f + (1.56f * MathF.Pow(p, 0.90f));
        return Math.Max(0.35f, clampedBase * scale * 0.5f);
    }

    /// <summary>
    /// Applies a light 3-point weighted moving average to smooth hardware pointer jitter
    /// while preserving exact start and end coordinates.
    /// </summary>
    /// <param name="samples">Raw input stroke samples.</param>
    /// <returns>Smoothed stroke samples.</returns>
    public static List<PdfInkSample> SmoothSamples(IReadOnlyList<PdfInkSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int count = samples.Count;
        List<PdfInkSample> result = new(count);
        if (count == 0)
        {
            return result;
        }

        if (count <= 2)
        {
            for (int i = 0; i < count; i++)
            {
                result.Add(samples[i]);
            }

            return result;
        }

        result.Add(samples[0]);
        for (int i = 1; i < count - 1; i++)
        {
            PdfInkSample prev = samples[i - 1];
            PdfInkSample curr = samples[i];
            PdfInkSample next = samples[i + 1];
            float x = (prev.X * 0.25f) + (curr.X * 0.50f) + (next.X * 0.25f);
            float y = (prev.Y * 0.25f) + (curr.Y * 0.50f) + (next.Y * 0.25f);
            float p = (prev.Pressure * 0.25f) + (curr.Pressure * 0.50f) + (next.Pressure * 0.25f);
            result.Add(new PdfInkSample(x, y, p));
        }

        result.Add(samples[count - 1]);
        return result;
    }

    /// <summary>
    /// Simplifies a stroke using Ramer-Douglas-Peucker while optionally preserving significant pressure changes.
    /// </summary>
    /// <param name="samples">Input samples (typically smoothed).</param>
    /// <param name="epsilon">Maximum perpendicular geometric deviation in target units.</param>
    /// <param name="preservePressure">Whether to retain vertices where stylus pressure shifts noticeably.</param>
    /// <returns>Simplified sample list.</returns>
    public static List<PdfInkSample> SimplifySamples(
        IReadOnlyList<PdfInkSample> samples,
        float epsilon,
        bool preservePressure)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int n = samples.Count;
        if (n <= 2)
        {
            return new List<PdfInkSample>(samples);
        }

        bool[] keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;

        float epsSq = MathF.Max(0.0001f, epsilon * epsilon);
        Stack<(int Start, int End)> stack = new(32);
        stack.Push((0, n - 1));

        while (stack.Count > 0)
        {
            (int start, int end) = stack.Pop();
            if (end - start <= 1)
            {
                continue;
            }

            PdfInkSample a = samples[start];
            PdfInkSample b = samples[end];
            float maxDistSq = -1f;
            int splitIdx = -1;

            for (int i = start + 1; i < end; i++)
            {
                PdfInkSample c = samples[i];
                float distSq = PointToSegmentDistanceSquared(c.X, c.Y, a.X, a.Y, b.X, b.Y);
                if (preservePressure)
                {
                    float t = (float)(i - start) / (end - start);
                    float interpP = a.Pressure + ((b.Pressure - a.Pressure) * t);
                    float pDiff = MathF.Abs(c.Pressure - interpP);
                    if (pDiff > 0.08f)
                    {
                        float pressurePenalty = (pDiff * 14f) * epsilon;
                        distSq = MathF.Max(distSq, pressurePenalty * pressurePenalty);
                    }
                }

                if (distSq > maxDistSq)
                {
                    maxDistSq = distSq;
                    splitIdx = i;
                }
            }

            if (splitIdx > start && maxDistSq > epsSq)
            {
                keep[splitIdx] = true;
                stack.Push((start, splitIdx));
                stack.Push((splitIdx, end));
            }
        }

        List<PdfInkSample> output = new(n);
        for (int i = 0; i < n; i++)
        {
            if (keep[i])
            {
                output.Add(samples[i]);
            }
        }

        return output;
    }

    /// <summary>
    /// Builds a closed variable-width outline polygon (ribbon with rounded caps) for a pressure-sensitive stroke.
    /// </summary>
    /// <param name="samples">Stroke samples.</param>
    /// <param name="baseWidth">Nominal stroke width in target units.</param>
    /// <returns>Ordered polygon vertices forming a closed contour.</returns>
    public static List<Vector2> BuildVariableWidthContour(IReadOnlyList<PdfInkSample> samples, float baseWidth)
    {
        ArgumentNullException.ThrowIfNull(samples);
        List<PdfInkSample> clean = DeduplicateNearPoints(samples, minDist: 0.15f);
        if (clean.Count == 0)
        {
            return [];
        }

        if (clean.Count == 1)
        {
            float r = ComputeHalfWidth(baseWidth, clean[0].Pressure, hasPressureVariation: true);
            return BuildCirclePolygon(clean[0].X, clean[0].Y, r, segments: 12);
        }

        int n = clean.Count;
        Vector2[] leftPts = new Vector2[n];
        Vector2[] rightPts = new Vector2[n];
        Vector2[] tangents = new Vector2[n];

        for (int i = 0; i < n; i++)
        {
            Vector2 t;
            if (i == 0)
            {
                t = NormalizeSafe(new Vector2(clean[1].X - clean[0].X, clean[1].Y - clean[0].Y));
            }
            else if (i == n - 1)
            {
                t = NormalizeSafe(new Vector2(clean[n - 1].X - clean[n - 2].X, clean[n - 1].Y - clean[n - 2].Y));
            }
            else
            {
                Vector2 tIn = NormalizeSafe(new Vector2(clean[i].X - clean[i - 1].X, clean[i].Y - clean[i - 1].Y));
                Vector2 tOut = NormalizeSafe(new Vector2(clean[i + 1].X - clean[i].X, clean[i + 1].Y - clean[i].Y));
                Vector2 avg = tIn + tOut;
                t = avg.LengthSquared() > 1e-4f ? Vector2.Normalize(avg) : tOut;
            }

            tangents[i] = t;
            Vector2 leftNormal = new(-t.Y, t.X);
            float r = ComputeHalfWidth(baseWidth, clean[i].Pressure, hasPressureVariation: true);
            Vector2 center = new(clean[i].X, clean[i].Y);
            leftPts[i] = center + (leftNormal * r);
            rightPts[i] = center - (leftNormal * r);
        }

        List<Vector2> contour = new((n * 2) + (EndCapSegments * 2) + 4);

        // Start cap: sweep semicircle from rightPts[0] around behind start to leftPts[0].
        Vector2 startCenter = new(clean[0].X, clean[0].Y);
        float startR = ComputeHalfWidth(baseWidth, clean[0].Pressure, hasPressureVariation: true);
        float startBaseAngle = MathF.Atan2(-tangents[0].X, tangents[0].Y);
        for (int s = 1; s < EndCapSegments; s++)
        {
            float frac = (float)s / EndCapSegments;
            float angle = startBaseAngle - (MathF.PI * frac);
            contour.Add(new Vector2(
                startCenter.X + (MathF.Cos(angle) * startR),
                startCenter.Y + (MathF.Sin(angle) * startR)));
        }

        // Left side forward.
        for (int i = 0; i < n; i++)
        {
            contour.Add(leftPts[i]);
        }

        // End cap: sweep semicircle from leftPts[n-1] around ahead of end to rightPts[n-1].
        Vector2 endCenter = new(clean[n - 1].X, clean[n - 1].Y);
        float endR = ComputeHalfWidth(baseWidth, clean[n - 1].Pressure, hasPressureVariation: true);
        float endLeftAngle = MathF.Atan2(tangents[n - 1].X, -tangents[n - 1].Y);
        for (int s = 1; s < EndCapSegments; s++)
        {
            float frac = (float)s / EndCapSegments;
            float angle = endLeftAngle - (MathF.PI * frac);
            contour.Add(new Vector2(
                endCenter.X + (MathF.Cos(angle) * endR),
                endCenter.Y + (MathF.Sin(angle) * endR)));
        }

        // Right side backward.
        for (int i = n - 1; i >= 0; i--)
        {
            contour.Add(rightPts[i]);
        }

        return contour;
    }

    /// <summary>
    /// Draws a live wet-ink stroke onto a Win2D <see cref="CanvasDrawingSession"/> with optional pressure-driven width.
    /// </summary>
    /// <param name="ds">Target Win2D drawing session.</param>
    /// <param name="dipSamples">Stroke samples in page DIPs (origin at page top-left).</param>
    /// <param name="hasPressureVariation">Whether pressure controls stroke thickness.</param>
    /// <param name="baseWidthDip">Nominal brush width in page DIPs.</param>
    /// <param name="color">Stroke RGBA color.</param>
    /// <param name="offsetX">Horizontal canvas offset in DIPs (for high-res detail patch alignment).</param>
    /// <param name="offsetY">Vertical canvas offset in DIPs (for high-res detail patch alignment).</param>
    public static void DrawWetInkStroke(
        CanvasDrawingSession ds,
        IReadOnlyList<PdfInkSample> dipSamples,
        bool hasPressureVariation,
        float baseWidthDip,
        Color color,
        float offsetX,
        float offsetY)
    {
        if (dipSamples.Count == 0)
        {
            return;
        }

        if (dipSamples.Count == 1)
        {
            float r = ComputeHalfWidth(baseWidthDip, dipSamples[0].Pressure, hasPressureVariation);
            ds.FillCircle(dipSamples[0].X + offsetX, dipSamples[0].Y + offsetY, r, color);
            return;
        }

        List<PdfInkSample> smoothed = dipSamples.Count >= 3 ? SmoothSamples(dipSamples) : new List<PdfInkSample>(dipSamples);

        if (hasPressureVariation)
        {
            List<Vector2> contour = BuildVariableWidthContour(smoothed, baseWidthDip);
            if (contour.Count < 3)
            {
                float r = ComputeHalfWidth(baseWidthDip, smoothed[0].Pressure, hasPressureVariation: true);
                ds.FillCircle(smoothed[0].X + offsetX, smoothed[0].Y + offsetY, r, color);
                return;
            }

            using CanvasPathBuilder pb = new(ds);
            pb.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
            pb.BeginFigure(contour[0].X + offsetX, contour[0].Y + offsetY);
            for (int i = 1; i < contour.Count; i++)
            {
                pb.AddLine(contour[i].X + offsetX, contour[i].Y + offsetY);
            }

            pb.EndFigure(CanvasFigureLoop.Closed);
            using CanvasGeometry geo = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(geo, color);
        }
        else
        {
            using CanvasPathBuilder pb = new(ds);
            pb.BeginFigure(smoothed[0].X + offsetX, smoothed[0].Y + offsetY);
            if (smoothed.Count == 2)
            {
                pb.AddLine(smoothed[1].X + offsetX, smoothed[1].Y + offsetY);
            }
            else
            {
                for (int i = 1; i < smoothed.Count - 1; i++)
                {
                    float midX = ((smoothed[i].X + smoothed[i + 1].X) * 0.5f) + offsetX;
                    float midY = ((smoothed[i].Y + smoothed[i + 1].Y) * 0.5f) + offsetY;
                    pb.AddQuadraticBezier(
                        new Vector2(smoothed[i].X + offsetX, smoothed[i].Y + offsetY),
                        new Vector2(midX, midY));
                }

                PdfInkSample last = smoothed[^1];
                pb.AddLine(last.X + offsetX, last.Y + offsetY);
            }

            pb.EndFigure(CanvasFigureLoop.Open);
            using CanvasGeometry geo = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geo, color, Math.Max(0.75f, baseWidthDip), UniformStrokeStyle);
        }
    }

    /// <summary>
    /// Builds a PDF normal appearance stream (<c>/AP /N</c>) for one or more freehand ink strokes in PDF user space.
    /// Emits filled variable-width vector contours for pressure-sensitive strokes and round-cap vector polylines for uniform strokes.
    /// </summary>
    /// <param name="strokes">Prepared strokes in PDF user space (points).</param>
    /// <param name="color">Stroke color.</param>
    /// <param name="baseWidthPt">Nominal stroke width in PDF points.</param>
    /// <returns>PDF content stream commands.</returns>
    public static string BuildPdfAppearanceStream(
        IReadOnlyList<PdfInkStrokeData> strokes,
        PdfAnnotationColor color,
        float baseWidthPt)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        float clampedWidth = Math.Clamp(baseWidthPt, 0.5f, 32f);

        StringBuilder sb = new(1024);
        sb.Append(CultureInfo.InvariantCulture, $"q 1 J 1 j {clampedWidth:F2} w {r:F3} {g:F3} {b:F3} RG {r:F3} {g:F3} {b:F3} rg ");

        for (int s = 0; s < strokes.Count; s++)
        {
            PdfInkStrokeData stroke = strokes[s];
            if (stroke.Samples.Count == 0)
            {
                continue;
            }

            if (stroke.HasPressureVariation)
            {
                List<Vector2> contour = BuildVariableWidthContour(stroke.Samples, clampedWidth);
                if (contour.Count >= 3)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{contour[0].X:F2} {contour[0].Y:F2} m ");
                    for (int i = 1; i < contour.Count; i++)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"{contour[i].X:F2} {contour[i].Y:F2} l ");
                    }

                    sb.Append("h f ");
                }
            }
            else
            {
                IReadOnlyList<PdfInkSample> pts = stroke.Samples;
                sb.Append(CultureInfo.InvariantCulture, $"{pts[0].X:F2} {pts[0].Y:F2} m ");
                if (pts.LengthOrCount() == 1)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{(pts[0].X + 0.1f):F2} {pts[0].Y:F2} l ");
                }
                else
                {
                    for (int i = 1; i < pts.Count; i++)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"{pts[i].X:F2} {pts[i].Y:F2} l ");
                    }
                }

                sb.Append("S ");
            }
        }

        sb.Append('Q');
        return sb.ToString();
    }

    /// <summary>
    /// Tests whether an eraser brush segment <c>(segA, segB)</c> in PDF user space intersects <paramref name="annotation"/>.
    /// Uses precise polyline hit-testing for ink/line/arrow strokes and hollow-perimeter testing for large unfilled shapes.
    /// </summary>
    /// <param name="annotation">Candidate annotation.</param>
    /// <param name="segA">Eraser segment start in PDF points.</param>
    /// <param name="segB">Eraser segment end in PDF points.</param>
    /// <param name="eraserRadiusPt">Eraser tip radius in PDF points.</param>
    /// <returns><see langword="true"/> if the eraser touches the annotation.</returns>
    public static bool IntersectsAnnotation(
        PdfAnnotationInfo annotation,
        Vector2 segA,
        Vector2 segB,
        float eraserRadiusPt)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.IsSignatureWidget)
        {
            return false;
        }

        float pad = MathF.Max(2f, eraserRadiusPt + MathF.Max(1.5f, annotation.BorderWidthPt * 0.75f));
        float minX = MathF.Min(segA.X, segB.X) - pad;
        float maxX = MathF.Max(segA.X, segB.X) + pad;
        float minY = MathF.Min(segA.Y, segB.Y) - pad;
        float maxY = MathF.Max(segA.Y, segB.Y) + pad;

        PdfRect b = annotation.Bounds;
        if (maxX < b.Left || minX > b.Right || maxY < b.Bottom || minY > b.Top)
        {
            return false;
        }

        // Prefer exact stroke polyline intersection for Ink / Line / Arrow / PolyLine annotations.
        if (annotation.Strokes is { Count: > 0 } strokes)
        {
            float threshSq = pad * pad;
            for (int s = 0; s < strokes.Count; s++)
            {
                IReadOnlyList<Vector2> stroke = strokes[s];
                if (stroke.Count == 0)
                {
                    continue;
                }

                if (stroke.Count == 1)
                {
                    if (PointToSegmentDistanceSquared(stroke[0].X, stroke[0].Y, segA.X, segA.Y, segB.X, segB.Y) <= threshSq)
                    {
                        return true;
                    }

                    continue;
                }

                for (int i = 0; i < stroke.Count - 1; i++)
                {
                    if (SegmentToSegmentDistanceSquared(segA, segB, stroke[i], stroke[i + 1]) <= threshSq)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Text markups: test individual quad rectangles.
        if (annotation.QuadRects is { Count: > 0 } quads)
        {
            for (int i = 0; i < quads.Count; i++)
            {
                if (SegmentIntersectsRect(segA, segB, quads[i], eraserRadiusPt))
                {
                    return true;
                }
            }

            return false;
        }

        // Large hollow Rectangle/Ellipse shapes: require touching the stroke border so erasing ink inside a box doesn't delete the box.
        if (annotation.Subtype is PdfAnnotationSubtype.Square or PdfAnnotationSubtype.Circle &&
            annotation.InteriorColor is null &&
            b.Width > 44f &&
            b.Height > 44f)
        {
            float threshSq = pad * pad;
            if (annotation.Subtype == PdfAnnotationSubtype.Square)
            {
                Vector2 bl = new(b.Left, b.Bottom);
                Vector2 br = new(b.Right, b.Bottom);
                Vector2 tr = new(b.Right, b.Top);
                Vector2 tl = new(b.Left, b.Top);
                return SegmentToSegmentDistanceSquared(segA, segB, bl, br) <= threshSq ||
                       SegmentToSegmentDistanceSquared(segA, segB, br, tr) <= threshSq ||
                       SegmentToSegmentDistanceSquared(segA, segB, tr, tl) <= threshSq ||
                       SegmentToSegmentDistanceSquared(segA, segB, tl, bl) <= threshSq;
            }

            // Approximate ellipse perimeter with 24 segments.
            float cx = (b.Left + b.Right) * 0.5f;
            float cy = (b.Bottom + b.Top) * 0.5f;
            float rx = b.Width * 0.5f;
            float ry = b.Height * 0.5f;
            const int segs = 24;
            Vector2 prev = new(cx + rx, cy);
            for (int i = 1; i <= segs; i++)
            {
                float angle = (MathF.Tau * i) / segs;
                Vector2 next = new(cx + (MathF.Cos(angle) * rx), cy + (MathF.Sin(angle) * ry));
                if (SegmentToSegmentDistanceSquared(segA, segB, prev, next) <= threshSq)
                {
                    return true;
                }

                prev = next;
            }

            return false;
        }

        return SegmentIntersectsRect(segA, segB, b, eraserRadiusPt);
    }

    private static int LengthOrCount(this IReadOnlyList<PdfInkSample> list) => list.Count;

    private static List<PdfInkSample> DeduplicateNearPoints(IReadOnlyList<PdfInkSample> samples, float minDist)
    {
        List<PdfInkSample> clean = new(samples.Count);
        float minDistSq = minDist * minDist;
        for (int i = 0; i < samples.Count; i++)
        {
            PdfInkSample s = samples[i];
            if (clean.Count == 0)
            {
                clean.Add(s);
                continue;
            }

            PdfInkSample prev = clean[^1];
            float dx = s.X - prev.X;
            float dy = s.Y - prev.Y;
            if ((dx * dx) + (dy * dy) >= minDistSq)
            {
                clean.Add(s);
            }
            else if (s.Pressure > prev.Pressure)
            {
                clean[^1] = new PdfInkSample(prev.X, prev.Y, s.Pressure);
            }
        }

        return clean;
    }

    private static List<Vector2> BuildCirclePolygon(float cx, float cy, float radius, int segments)
    {
        List<Vector2> pts = new(segments);
        for (int i = 0; i < segments; i++)
        {
            float angle = (MathF.Tau * i) / segments;
            pts.Add(new Vector2(cx + (MathF.Cos(angle) * radius), cy + (MathF.Sin(angle) * radius)));
        }

        return pts;
    }

    private static Vector2 NormalizeSafe(Vector2 v)
    {
        float lenSq = v.LengthSquared();
        return lenSq > 1e-8f ? v / MathF.Sqrt(lenSq) : Vector2.UnitX;
    }

    private static float PointToSegmentDistanceSquared(float px, float py, float ax, float ay, float bx, float by)
    {
        float abx = bx - ax;
        float aby = by - ay;
        float lenSq = (abx * abx) + (aby * aby);
        if (lenSq <= 1e-8f)
        {
            float dx0 = px - ax;
            float dy0 = py - ay;
            return (dx0 * dx0) + (dy0 * dy0);
        }

        float t = Math.Clamp((((px - ax) * abx) + ((py - ay) * aby)) / lenSq, 0f, 1f);
        float projX = ax + (t * abx);
        float projY = ay + (t * aby);
        float dx = px - projX;
        float dy = py - projY;
        return (dx * dx) + (dy * dy);
    }

    private static float SegmentToSegmentDistanceSquared(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
    {
        if (SegmentsIntersect(a1, a2, b1, b2))
        {
            return 0f;
        }

        float d1 = PointToSegmentDistanceSquared(a1.X, a1.Y, b1.X, b1.Y, b2.X, b2.Y);
        float d2 = PointToSegmentDistanceSquared(a2.X, a2.Y, b1.X, b1.Y, b2.X, b2.Y);
        float d3 = PointToSegmentDistanceSquared(b1.X, b1.Y, a1.X, a1.Y, a2.X, a2.Y);
        float d4 = PointToSegmentDistanceSquared(b2.X, b2.Y, a1.X, a1.Y, a2.X, a2.Y);
        return MathF.Min(MathF.Min(d1, d2), MathF.Min(d3, d4));
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        float d1 = Cross(p3, p4, p1);
        float d2 = Cross(p3, p4, p2);
        float d3 = Cross(p1, p2, p3);
        float d4 = Cross(p1, p2, p4);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        return false;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static bool SegmentIntersectsRect(Vector2 a, Vector2 b, PdfRect rect, float pad)
    {
        float left = rect.Left - pad;
        float right = rect.Right + pad;
        float bottom = rect.Bottom - pad;
        float top = rect.Top + pad;

        if ((a.X >= left && a.X <= right && a.Y >= bottom && a.Y <= top) ||
            (b.X >= left && b.X <= right && b.Y >= bottom && b.Y <= top))
        {
            return true;
        }

        Vector2 bl = new(left, bottom);
        Vector2 br = new(right, bottom);
        Vector2 tr = new(right, top);
        Vector2 tl = new(left, top);
        return SegmentsIntersect(a, b, bl, br) ||
               SegmentsIntersect(a, b, br, tr) ||
               SegmentsIntersect(a, b, tr, tl) ||
               SegmentsIntersect(a, b, tl, bl);
    }
}
