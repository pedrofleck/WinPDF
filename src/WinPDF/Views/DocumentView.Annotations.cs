using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinPDF.Controls;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.ViewModels;

namespace WinPDF.Views;

public sealed partial class DocumentView
{
    private enum AnnotationEditorMode
    {
        CreateNote,
        CreateFreeText,
        CreateMarkupWithComment,
        EditExisting,
    }

    private AnnotationEditorMode _annotEditorMode;
    private int _annotEditorPageIndex = -1;
    private int _annotEditorAnnotIndex = -1;
    private Vector2 _annotEditorPdfPt;
    private PdfTextSelection? _annotEditorPendingSelection;
    private PdfAnnotationSubtype _annotEditorPendingMarkupSubtype = PdfAnnotationSubtype.Highlight;
    private PdfAnnotationColor _annotEditorSelectedColor = PdfAnnotationColor.Yellow;
    private bool _annotEditorCanEditText = true;
    private bool _annotEditorCanEditColor = true;
    private PdfPageCanvas? _contextTargetCanvas;
    private Vector2 _contextTargetPdfPt;
    private Point _contextTargetDipPt;
    private PdfAnnotationInfo? _contextTargetAnnotation;
    private PdfSignatureInfo? _contextTargetSignature;
    private PdfAnnotationInfo? _copiedAnnotation;
    private bool _isCutClipboard;
    private Action? _pendingAfterContextMenuClosed;
    private Action? _pendingAfterQuickBarClosed;
    private Action? _pendingAfterMouseSelectionClosed;
    private long _quickBarOpenedTick;
    private PdfAnnotationInfo? _quickBarAnnotation;
    private PdfPageCanvas? _quickBarCanvas;
    private Point _quickBarAnchorDipPt;
    private bool _quickBarClosedProgrammatically;
    private bool _annotEditorClosedProgrammatically;
    private long _annotEditorOpenedTick;
    private long _annotationPopupOpenSeq;
    private int _lastMouseClickPageIndex = -1;
    private Vector2 _lastMouseClickPdfPt;
    private bool _hasLastMouseClickPdfPt;

    private void ToggleOrSelectTool(AnnotationTool tool)
    {
        ViewModel.SetTool(ViewModel.ActiveTool == tool ? AnnotationTool.Select : tool);
    }

    private void OnToolPenClicked(object sender, RoutedEventArgs e)
    {
        ToggleOrSelectTool(AnnotationTool.Pen);
    }

    private void OnToolHighlighterClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is { ActiveSelection: { HasContent: true } sel } doc)
        {
            _ = doc.AddMarkupForSelectionAsync(sel, PdfAnnotationSubtype.Highlight, doc.ActiveAnnotationColor);
            HideTouchSelectionFlyout();
            ReturnFocusToViewer();
            return;
        }

        ToggleOrSelectTool(AnnotationTool.Highlighter);
    }

    private void OnToolHighlighterButtonSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0)
        {
            return;
        }

        if (double.IsNaN(ToolbarShapeButton.Height) || Math.Abs(ToolbarShapeButton.Height - e.NewSize.Height) > 0.1)
        {
            ToolbarShapeButton.Height = e.NewSize.Height;
        }

        if (double.IsNaN(ToolbarShapePickerButton.Height) || Math.Abs(ToolbarShapePickerButton.Height - e.NewSize.Height) > 0.1)
        {
            ToolbarShapePickerButton.Height = e.NewSize.Height;
        }
    }

    private void OnToolShapeClicked(object sender, RoutedEventArgs e)
    {
        ToggleOrSelectTool(AnnotationTool.Shape);
    }

    private void OnShapeKindMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tagStr } &&
            Enum.TryParse<PdfAnnotationShapeKind>(tagStr, ignoreCase: true, out PdfAnnotationShapeKind kind))
        {
            ViewModel.ActiveShapeKind = kind;
            SyncActiveShapeUi();
            ViewModel.SetTool(AnnotationTool.Shape);
        }
    }

    private void OnToolbarStrokeThicknessChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SyncStrokeThicknessUi();
    }

    private void SyncActiveShapeUi()
    {
        PdfAnnotationShapeKind kind = ViewModel.ActiveShapeKind;
        SetVisible(ToolbarShapeLineIcon, kind == PdfAnnotationShapeKind.Line);
        SetVisible(ToolbarShapeArrowIcon, kind == PdfAnnotationShapeKind.Arrow);
        SetVisible(ToolbarShapeRectangleIcon, kind == PdfAnnotationShapeKind.Rectangle);
        SetVisible(ToolbarShapeEllipseIcon, kind == PdfAnnotationShapeKind.Ellipse);

        if (ToolbarShapeButton is not null)
        {
            string kindKey = kind switch
            {
                PdfAnnotationShapeKind.Arrow => ResourceKeys.AnnotSubtypeArrow,
                PdfAnnotationShapeKind.Rectangle => ResourceKeys.AnnotSubtypeSquare,
                PdfAnnotationShapeKind.Ellipse => ResourceKeys.AnnotSubtypeCircle,
                _ => ResourceKeys.AnnotSubtypeLine,
            };
            string shapeName = ResourceStrings.Get(kindKey);
            ToolTipService.SetToolTip(
                ToolbarShapeButton,
                ResourceStrings.Format(ResourceKeys.ToolShapeFormatToolTip, shapeName));
        }
    }

    private void SyncStrokeThicknessUi()
    {
        double pt = Math.Clamp(ViewModel.StrokeThicknessPt, 1.0, 12.0);
        if (ToolbarThicknessValueText is not null)
        {
            ToolbarThicknessValueText.Text = ResourceStrings.Format(ResourceKeys.ToolbarThicknessPtFormat, pt);
        }

        if (ToolbarThicknessPreviewLine is not null)
        {
            double previewHeight = Math.Clamp(pt * 1.4, 1.5, 18.0);
            ToolbarThicknessPreviewLine.Height = previewHeight;
            ToolbarThicknessPreviewLine.CornerRadius = new CornerRadius(previewHeight * 0.5);
            PdfAnnotationColor activeColor = ViewModel.CurrentDocument?.ActiveAnnotationColor ?? ViewModel.DefaultAnnotationColor;
            ToolbarThicknessPreviewLine.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, activeColor.R, activeColor.G, activeColor.B));
        }
    }

    private void OnDocumentViewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Shift)
        {
            RefreshActiveShapePreviewConstraints();
        }
    }

    private void RefreshActiveShapePreviewConstraints()
    {
        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            canvas.RefreshShapePreviewConstraint();
        }
    }

    private bool CancelShapeDrawingOnAllCanvases()
    {
        bool canceledAny = false;
        if (_isShapeOverlayTouchDrawing || _shapeOverlayDrawCanvas is not null || _shapeOverlayMouseCanvas is not null)
        {
            _shapeOverlayDrawCanvas?.CancelShapeDrawing();
            _shapeOverlayMouseCanvas?.CancelShapeDrawing();
            _isShapeOverlayTouchDrawing = false;
            _shapeOverlayDrawPointerId = 0;
            _shapeOverlayDrawCanvas = null;
            _shapeOverlayMouseCanvas = null;
            canceledAny = true;
        }

        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            if (canvas.IsDrawingShape)
            {
                canvas.CancelShapeDrawing();
                canceledAny = true;
            }
        }

        return canceledAny;
    }

    private void OnToolEraserClicked(object sender, RoutedEventArgs e)
    {
        ToggleOrSelectTool(AnnotationTool.Eraser);
    }

    private void OnScrollViewerContentRootContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_isLastPointerInputTouch || _isDraggingTouchScrollScrubber || IsDescendantOfTouchScrubber(args.OriginalSource as DependencyObject))
        {
            // Suppress the desktop right-click MenuFlyout on touch long-press so touch uses native text selection + CommandBarFlyout
            args.Handled = true;
            return;
        }

        args.Handled = true;
        _pendingAfterContextMenuClosed = null;
        _contextTargetCanvas = null;
        _contextTargetAnnotation = null;
        _contextTargetSignature = null;
        _contextTargetPdfPt = default;
        _contextTargetDipPt = default;

        if (ScrollViewerContentRoot is null || DocumentContextMenuFlyout is null)
        {
            return;
        }

        Point showRootPt = new(
            Math.Max(24.0, ScrollViewerContentRoot.ActualWidth * 0.5),
            Math.Max(24.0, ScrollViewerContentRoot.ActualHeight * 0.35));
        bool hasPointerPosition = args.TryGetPosition(ScrollViewerContentRoot, out Point pointerRootPt);
        if (hasPointerPosition)
        {
            showRootPt = pointerRootPt;
        }

        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc is not null && !ViewModel.IsPresentationMode)
        {
            if (hasPointerPosition)
            {
                if (TryFindCanvasAtRootPoint(pointerRootPt, out PdfPageCanvas? hitCanvas, out Point canvasDip))
                {
                    _contextTargetCanvas = hitCanvas;
                    _contextTargetDipPt = canvasDip;
                    _contextTargetPdfPt = hitCanvas!.ToPdfPoint(canvasDip);
                    if (hitCanvas.PageInfo is { } hitPageInfo)
                    {
                        _lastMouseClickPageIndex = hitPageInfo.Index;
                        _lastMouseClickPdfPt = _contextTargetPdfPt;
                        _hasLastMouseClickPdfPt = true;
                    }
                }
            }
            else if (FindRealizedCanvasForPage(doc.CurrentPageIndex) is { } fallbackCanvas)
            {
                double cw = fallbackCanvas.ActualWidth > 0 ? fallbackCanvas.ActualWidth : fallbackCanvas.Width;
                _contextTargetCanvas = fallbackCanvas;
                _contextTargetDipPt = new Point(Math.Max(12.0, cw * 0.5), 80.0);
                _contextTargetPdfPt = fallbackCanvas.ToPdfPoint(_contextTargetDipPt);
                try
                {
                    showRootPt = fallbackCanvas.TransformToVisual(ScrollViewerContentRoot).TransformPoint(_contextTargetDipPt);
                }
                catch
                {
                }
            }

            if (_contextTargetCanvas?.PageInfo is { } pageInfo)
            {
                PdfAnnotationInfo? hitAnnot = doc.HitTestAnnotation(
                    pageInfo.Index,
                    _contextTargetPdfPt.X,
                    _contextTargetPdfPt.Y,
                    tolerancePt: 6.0);
                if (hitAnnot is not null)
                {
                    if (hitAnnot.IsSignatureWidget)
                    {
                        _contextTargetSignature = doc.GetSignatureByIndex(hitAnnot.SignatureIndex);
                    }
                    else
                    {
                        _contextTargetAnnotation = hitAnnot;
                    }
                }
            }
        }

        bool hasSel = ViewModel.HasTextSelection && !ViewModel.IsPresentationMode;
        if (hasSel && MouseSelectionFlyout is not null)
        {
            HideAnnotationPopupsImmediate();
            ShowMouseSelectionFlyoutAt(showRootPt);
            return;
        }

        bool canAnnotatePage = _contextTargetCanvas is not null && doc is not null && !ViewModel.IsPresentationMode;
        bool onSignature = !hasSel && _contextTargetSignature is not null;
        bool onAnnotation = !hasSel && _contextTargetAnnotation is not null;
        bool canCopyOrCutAnnot = onAnnotation && _contextTargetAnnotation is { CanCopyOrCut: true };
        bool onEmptyPage = !hasSel && !onSignature && !onAnnotation && canAnnotatePage;
        bool canUndo = doc is { CanUndoAnnotation: true } && !ViewModel.IsPresentationMode;

        SetVisible(ContextCopyMenuItem, false);
        SetVisible(ContextHighlightMenuItem, false);
        SetVisible(ContextHighlightWithCommentMenuItem, false);
        SetVisible(ContextMarkupSubMenu, false);
        SetVisible(ContextInspectSignatureMenuItem, onSignature);
        SetVisible(ContextEditAnnotationMenuItem, onAnnotation);
        SetVisible(ContextCopyAnnotationMenuItem, canCopyOrCutAnnot);
        SetVisible(ContextCutAnnotationMenuItem, canCopyOrCutAnnot);
        SetVisible(ContextDeleteAnnotationMenuItem, onAnnotation);
        SetVisible(ContextAddTextBoxMenuItem, onEmptyPage);
        SetVisible(ContextPasteAnnotationMenuItem, onEmptyPage && _copiedAnnotation is not null);
        SetVisible(ContextUndoAnnotationMenuItem, canUndo);
        SetVisible(ContextAnnotSeparator, onSignature || onAnnotation || onEmptyPage || canUndo);

        HideAnnotationPopupsImmediate();

        try
        {
            DocumentContextMenuFlyout.ShowAt(
                ScrollViewerContentRoot,
                new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = showRootPt });
        }
        catch
        {
        }
    }

    private void ShowMouseSelectionFlyoutAt(Point rootPt)
    {
        if (MouseSelectionFlyout is null || ScrollViewerContentRoot is null)
        {
            return;
        }

        try
        {
            SyncDefaultColorIndicator();
            double rootW = Math.Max(64.0, ScrollViewerContentRoot.ActualWidth);
            double rootH = Math.Max(64.0, ScrollViewerContentRoot.ActualHeight);
            double clampedX = Math.Clamp(rootPt.X, 24.0, rootW - 24.0);
            double clampedY = Math.Clamp(rootPt.Y, 24.0, rootH - 24.0);
            bool placeBelow = clampedY < 96.0;
            double offsetY = placeBelow ? Math.Min(rootH - 24.0, clampedY + 14.0) : Math.Max(24.0, clampedY - 14.0);

            if (MouseSelectionFlyout.IsOpen)
            {
                MouseSelectionFlyout.Hide();
            }

            var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Point(clampedX, offsetY),
                Placement = placeBelow
                    ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
                    : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            };
            MouseSelectionFlyout.ShowAt(ScrollViewerContentRoot, options);
        }
        catch
        {
        }
    }

    private void OnMouseSelectionFlyoutClosed(object? sender, object e)
    {
        Action? pending = _pendingAfterMouseSelectionClosed;
        _pendingAfterMouseSelectionClosed = null;
        if (pending is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => pending());
        }
    }

    private void OnMouseSelectionCopyClick(object sender, RoutedEventArgs e)
    {
        _pendingAfterMouseSelectionClosed = null;
        MouseSelectionFlyout?.Hide();
        ViewModel.CopySelectedText();
        ReturnFocusToViewer();
    }

    private void OnMouseSelectionHighlightClick(object sender, RoutedEventArgs e)
    {
        _pendingAfterMouseSelectionClosed = null;
        MouseSelectionFlyout?.Hide();
        if (ViewModel.CurrentDocument is { ActiveSelection: { HasContent: true } sel } doc)
        {
            _ = doc.AddMarkupForSelectionAsync(sel, PdfAnnotationSubtype.Highlight, doc.ActiveAnnotationColor);
        }

        ReturnFocusToViewer();
    }

    private void OnMouseSelectionCommentClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc?.ActiveSelection is not { HasContent: true } sel)
        {
            MouseSelectionFlyout?.Hide();
            return;
        }

        PdfPageCanvas? anchorCanvas = _contextTargetCanvas?.PageInfo?.Index == sel.StartPageIndex
            ? _contextTargetCanvas
            : FindRealizedCanvasForPage(sel.StartPageIndex);
        Point anchorDip = ComputeSelectionAnchorCanvasDip(sel, anchorCanvas);

        _pendingAfterMouseSelectionClosed = () => OpenCreateAnnotationEditor(
            AnnotationEditorMode.CreateMarkupWithComment,
            sel.StartPageIndex,
            default,
            anchorCanvas,
            anchorDip,
            sel);

        if (MouseSelectionFlyout?.IsOpen == true)
        {
            MouseSelectionFlyout.Hide();
        }
        else
        {
            OnMouseSelectionFlyoutClosed(null, EventArgs.Empty);
        }
    }

    private void OnMouseSelectionMarkupSubtypeClick(object sender, RoutedEventArgs e)
    {
        _pendingAfterMouseSelectionClosed = null;
        MouseSelectionFlyout?.Hide();
        if (ViewModel.CurrentDocument is { ActiveSelection: { HasContent: true } sel } doc && sender is FrameworkElement fe)
        {
            PdfAnnotationSubtype subtype = ParseMarkupSubtypeTag(fe.Tag as string, PdfAnnotationSubtype.Underline);
            _ = doc.AddMarkupForSelectionAsync(sel, subtype, doc.ActiveAnnotationColor);
        }

        ReturnFocusToViewer();
    }

    private void OnMouseSelectionColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            PdfAnnotationColor current = ViewModel.CurrentDocument?.ActiveAnnotationColor ?? ViewModel.DefaultAnnotationColor;
            PdfAnnotationColor color = ParseAnnotationColorTag(fe.Tag as string, current);
            SetDefaultAnnotationColor(color);
        }
    }

    private void OnToolbarDefaultColorSelectClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            PdfAnnotationColor current = ViewModel.CurrentDocument?.ActiveAnnotationColor ?? ViewModel.DefaultAnnotationColor;
            SetDefaultAnnotationColor(ParseAnnotationColorTag(fe.Tag as string, current));
        }

        ToolbarColorFlyout?.Hide();
    }

    private void OnContextUndoAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is { CanUndoAnnotation: true } doc)
        {
            _ = doc.UndoAnnotationAsync();
        }
    }

    private void SetDefaultAnnotationColor(PdfAnnotationColor color)
    {
        ViewModel.DefaultAnnotationColor = color;
        if (ViewModel.CurrentDocument is { } doc && !doc.ActiveAnnotationColor.Equals(color))
        {
            doc.ActiveAnnotationColor = color;
        }

        SyncDefaultColorIndicator();
    }

    private void SyncDefaultColorIndicator()
    {
        PdfAnnotationColor color = ViewModel.CurrentDocument?.ActiveAnnotationColor ?? ViewModel.DefaultAnnotationColor;
        if (ToolbarColorIndicatorDot is not null)
        {
            ToolbarColorIndicatorDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));
        }

        ApplySwatchSelectedState(ToolbarColorYellowButton, IsSameColor(color, PdfAnnotationColor.Yellow));
        ApplySwatchSelectedState(ToolbarColorGreenButton, IsSameColor(color, PdfAnnotationColor.Green));
        ApplySwatchSelectedState(ToolbarColorBlueButton, IsSameColor(color, PdfAnnotationColor.Blue));
        ApplySwatchSelectedState(ToolbarColorPinkButton, IsSameColor(color, PdfAnnotationColor.Pink));
        ApplySwatchSelectedState(ToolbarColorOrangeButton, IsSameColor(color, PdfAnnotationColor.Orange));
        ApplySwatchSelectedState(ToolbarColorRedButton, IsSameColor(color, PdfAnnotationColor.Red));
        ApplySwatchSelectedState(MouseSelColorYellowButton, IsSameColor(color, PdfAnnotationColor.Yellow));
        ApplySwatchSelectedState(MouseSelColorGreenButton, IsSameColor(color, PdfAnnotationColor.Green));
        ApplySwatchSelectedState(MouseSelColorBlueButton, IsSameColor(color, PdfAnnotationColor.Blue));
        ApplySwatchSelectedState(MouseSelColorPinkButton, IsSameColor(color, PdfAnnotationColor.Pink));
        ApplySwatchSelectedState(MouseSelColorOrangeButton, IsSameColor(color, PdfAnnotationColor.Orange));
        ApplySwatchSelectedState(MouseSelColorRedButton, IsSameColor(color, PdfAnnotationColor.Red));
        SyncStrokeThicknessUi();
    }

    private static void SetVisible(UIElement? element, bool visible)
    {
        if (element is not null)
        {
            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnDocumentContextMenuClosed(object? sender, object e)
    {
        Action? pending = _pendingAfterContextMenuClosed;
        _pendingAfterContextMenuClosed = null;
        if (pending is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => pending());
        }
    }

    /// <summary>
    /// Defers <paramref name="action"/> until the right-click menu has fully closed so a follow-up flyout
    /// is not light-dismissed together with the menu.
    /// </summary>
    private void RunAfterContextMenuClosed(Action action)
    {
        if (DocumentContextMenuFlyout?.IsOpen == true)
        {
            _pendingAfterContextMenuClosed = action;
            return;
        }

        _pendingAfterContextMenuClosed = null;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => action());
    }

    private void InitializeAnnotationUiText()
    {
        if (TouchSelectionHighlightButton is not null)
        {
            ToolTipService.SetToolTip(TouchSelectionHighlightButton, ResourceStrings.Get(ResourceKeys.TouchSelectionHighlightToolTip));
        }

        if (TouchSelectionCommentButton is not null)
        {
            ToolTipService.SetToolTip(TouchSelectionCommentButton, ResourceStrings.Get(ResourceKeys.TouchSelectionCommentToolTip));
        }

        if (TouchSelectionUnderlineButton is not null)
        {
            TouchSelectionUnderlineButton.Label = ResourceStrings.Get(ResourceKeys.AnnotSubtypeUnderline);
        }

        if (TouchSelectionStrikeOutButton is not null)
        {
            TouchSelectionStrikeOutButton.Label = ResourceStrings.Get(ResourceKeys.AnnotSubtypeStrikeOut);
        }

        if (TouchSelectionSquigglyButton is not null)
        {
            TouchSelectionSquigglyButton.Label = ResourceStrings.Get(ResourceKeys.AnnotSubtypeSquiggly);
        }

        ConfigureColorButton(TouchSelectionColorYellowButton, ContextColorYellowItem, AnnotColorYellowButton, QuickColorYellowButton, MouseSelColorYellowButton, ToolbarColorYellowButton, ResourceKeys.ColorYellow, PdfAnnotationColor.Yellow);
        ConfigureColorButton(TouchSelectionColorGreenButton, ContextColorGreenItem, AnnotColorGreenButton, QuickColorGreenButton, MouseSelColorGreenButton, ToolbarColorGreenButton, ResourceKeys.ColorGreen, PdfAnnotationColor.Green);
        ConfigureColorButton(TouchSelectionColorBlueButton, ContextColorBlueItem, AnnotColorBlueButton, QuickColorBlueButton, MouseSelColorBlueButton, ToolbarColorBlueButton, ResourceKeys.ColorBlue, PdfAnnotationColor.Blue);
        ConfigureColorButton(TouchSelectionColorPinkButton, ContextColorPinkItem, AnnotColorPinkButton, QuickColorPinkButton, MouseSelColorPinkButton, ToolbarColorPinkButton, ResourceKeys.ColorPink, PdfAnnotationColor.Pink);
        ConfigureColorButton(TouchSelectionColorOrangeButton, ContextColorOrangeItem, AnnotColorOrangeButton, QuickColorOrangeButton, MouseSelColorOrangeButton, ToolbarColorOrangeButton, ResourceKeys.ColorOrange, PdfAnnotationColor.Orange);
        ConfigureColorButton(TouchSelectionColorRedButton, ContextColorRedItem, AnnotColorRedButton, QuickColorRedButton, MouseSelColorRedButton, ToolbarColorRedButton, ResourceKeys.ColorRed, PdfAnnotationColor.Red);

        if (TouchEmptyPasteButton is not null)
        {
            string paste = ResourceStrings.Get(ResourceKeys.TouchEmptyPasteLabel);
            TouchEmptyPasteButton.Label = paste;
            ToolTipService.SetToolTip(TouchEmptyPasteButton, paste);
        }

        if (TouchSelectionPasteButton is not null)
        {
            string paste = ResourceStrings.Get(ResourceKeys.TouchEmptyPasteLabel);
            TouchSelectionPasteButton.Label = paste;
            ToolTipService.SetToolTip(TouchSelectionPasteButton, paste);
        }

        if (StatusBarSignedLabelText is not null)
        {
            StatusBarSignedLabelText.Text = ResourceStrings.Get(ResourceKeys.StatusBarSignedLabel);
        }

        SetToolTip(ToolbarColorPickerButton, ResourceKeys.ToolbarColorToolTip);
        SetToolTip(MouseSelCopyButton, ResourceKeys.TouchSelectionCopyToolTip);
        SetToolTip(MouseSelHighlightButton, ResourceKeys.TouchSelectionHighlightToolTip);
        SetToolTip(MouseSelCommentButton, ResourceKeys.TouchSelectionCommentToolTip);
        SetToolTip(MouseSelUnderlineButton, ResourceKeys.AnnotSubtypeUnderline);
        SetToolTip(MouseSelStrikeOutButton, ResourceKeys.AnnotSubtypeStrikeOut);
        SetToolTip(MouseSelSquigglyButton, ResourceKeys.AnnotSubtypeSquiggly);
        SetToolTip(QuickBarAddCommentButton, ResourceKeys.AnnotAddCommentToolTip);
        SetToolTip(QuickBarColorButton, ResourceKeys.AnnotColorToolTip);
        SetToolTip(QuickBarCopyButton, ResourceKeys.AnnotCopyToolTip);
        SetToolTip(QuickBarCutButton, ResourceKeys.AnnotCutToolTip);
        SetToolTip(QuickBarDeleteButton, ResourceKeys.AnnotDelete);
        SetToolTip(QuickBarCloseButton, ResourceKeys.AnnotClose);
        SetToolTip(TouchQuickBarAddCommentButton, ResourceKeys.AnnotAddCommentToolTip);
        SetToolTip(TouchQuickBarColorButton, ResourceKeys.AnnotColorToolTip);
        SetToolTip(TouchQuickBarCopyButton, ResourceKeys.AnnotCopyToolTip);
        SetToolTip(TouchQuickBarCutButton, ResourceKeys.AnnotCutToolTip);
        SetToolTip(TouchQuickBarDeleteButton, ResourceKeys.AnnotDelete);
        SetToolTip(TouchQuickBarCloseButton, ResourceKeys.AnnotClose);
        SetToolTip(TouchQuickColorYellowButton, ResourceKeys.ColorYellow);
        SetToolTip(TouchQuickColorGreenButton, ResourceKeys.ColorGreen);
        SetToolTip(TouchQuickColorBlueButton, ResourceKeys.ColorBlue);
        SetToolTip(TouchQuickColorPinkButton, ResourceKeys.ColorPink);
        SetToolTip(TouchQuickColorOrangeButton, ResourceKeys.ColorOrange);
        SetToolTip(TouchQuickColorRedButton, ResourceKeys.ColorRed);
        SetToolTip(AnnotationEditorCopyButton, ResourceKeys.AnnotCopyToolTip);
        SetToolTip(AnnotationEditorCutButton, ResourceKeys.AnnotCutToolTip);
        SetToolTip(AnnotationEditorCloseButton, ResourceKeys.AnnotClose);
        SetToolTip(InlineTextSizeButton, ResourceKeys.TextBarSizeToolTip);
        SetToolTip(InlineTextBoldToggle, ResourceKeys.TextBarBoldToolTip);
        SetToolTip(InlineTextItalicToggle, ResourceKeys.TextBarItalicToolTip);
        SetToolTip(InlineTextUnderlineToggle, ResourceKeys.TextBarUnderlineToolTip);
        SetToolTip(InlineTextColorButton, ResourceKeys.TextBarColorToolTip);
        SetToolTip(InlineTextDeleteButton, ResourceKeys.TextBarDeleteToolTip);
        SetToolTip(InlineTextConfirmButton, ResourceKeys.TextBarConfirmToolTip);
        if (InlineTextEditorTextBox is not null)
        {
            InlineTextEditorTextBox.PlaceholderText = ResourceStrings.Get(ResourceKeys.AnnotTextBoxPlaceholder);
        }

        if (AnnotationEditorDeleteButton is not null)
        {
            AnnotationEditorDeleteButton.Content = ResourceStrings.Get(ResourceKeys.AnnotDelete);
        }

        if (AnnotationEditorSaveButton is not null)
        {
            AnnotationEditorSaveButton.Content = ResourceStrings.Get(ResourceKeys.AnnotSave);
        }

        if (AnnotationEditorTextBox is not null)
        {
            AnnotationEditorTextBox.TextChanged += OnAnnotationEditorTextBoxTextChanged;
        }

        SyncDefaultColorIndicator();
    }

    private static void SetToolTip(DependencyObject? element, string resourceKey)
    {
        if (element is not null)
        {
            ToolTipService.SetToolTip(element, ResourceStrings.Get(resourceKey));
        }
    }

    private static void ConfigureColorButton(
        AppBarButton? touchBtn,
        MenuFlyoutItem? menuItem,
        Button? swatchBtn,
        Button? quickSwatchBtn,
        Button? mouseSelSwatchBtn,
        Button? toolbarSwatchBtn,
        string colorNameKey,
        PdfAnnotationColor color)
    {
        string label = ResourceStrings.Get(colorNameKey);
        Windows.UI.Color uiColor = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B);

        if (touchBtn is not null)
        {
            touchBtn.Label = label;
            touchBtn.Icon = CreateColorDotIcon(uiColor);
        }

        if (menuItem is not null)
        {
            menuItem.Text = label;
            menuItem.Icon = CreateColorDotIcon(uiColor);
        }

        SetDirectToolTip(swatchBtn, label);
        SetDirectToolTip(quickSwatchBtn, label);
        SetDirectToolTip(mouseSelSwatchBtn, label);
        SetDirectToolTip(toolbarSwatchBtn, label);
    }

    private static void SetDirectToolTip(DependencyObject? element, string text)
    {
        if (element is not null)
        {
            ToolTipService.SetToolTip(element, text);
        }
    }

    private static FontIcon CreateColorDotIcon(Windows.UI.Color color) => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        Glyph = "●",
        FontSize = 14,
        Foreground = new SolidColorBrush(color),
    };

    private static PdfAnnotationColor ParseAnnotationColorTag(string? tag, PdfAnnotationColor fallback) => tag switch
    {
        "Yellow" => PdfAnnotationColor.Yellow,
        "Green" => PdfAnnotationColor.Green,
        "Blue" => PdfAnnotationColor.Blue,
        "Pink" => PdfAnnotationColor.Pink,
        "Orange" => PdfAnnotationColor.Orange,
        "Red" => PdfAnnotationColor.Red,
        _ => fallback,
    };

    private static PdfAnnotationSubtype ParseMarkupSubtypeTag(string? tag, PdfAnnotationSubtype fallback) => tag switch
    {
        "Highlight" => PdfAnnotationSubtype.Highlight,
        "Underline" => PdfAnnotationSubtype.Underline,
        "StrikeOut" => PdfAnnotationSubtype.StrikeOut,
        "Squiggly" => PdfAnnotationSubtype.Squiggly,
        _ => fallback,
    };

    private bool TryFindCanvasAtRootPoint(Point rootPt, out PdfPageCanvas? hitCanvas, out Point canvasDipPt)
    {
        hitCanvas = null;
        canvasDipPt = default;

        if (ScrollViewerContentRoot is null)
        {
            return false;
        }

        foreach (PdfPageCanvas canvas in _activeCanvases)
        {
            if (canvas.IsSwipePreview || !canvas.IsLoaded || canvas.PageInfo is null)
            {
                continue;
            }

            double cw = canvas.ActualWidth > 0 ? canvas.ActualWidth : canvas.Width;
            double ch = canvas.ActualHeight > 0 ? canvas.ActualHeight : canvas.Height;
            if (cw <= 0 || ch <= 0)
            {
                continue;
            }

            try
            {
                Point localPt = ScrollViewerContentRoot.TransformToVisual(canvas).TransformPoint(rootPt);
                if (localPt.X >= 0 && localPt.X <= cw && localPt.Y >= 0 && localPt.Y <= ch)
                {
                    hitCanvas = canvas;
                    canvasDipPt = localPt;
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>
    /// Shows or hides the "| Digitally signed [status]" badge next to the file size in the status bar.
    /// Unsigned documents keep the status bar unchanged.
    /// </summary>
    private void UpdateStatusBarSignatureIndicator()
    {
        if (StatusBarSignaturePanel is null || StatusBarSignatureIcon is null)
        {
            return;
        }

        DocumentViewModel? doc = _observedDocument;
        if (doc is not { HasSignatures: true })
        {
            StatusBarSignaturePanel.Visibility = Visibility.Collapsed;
            if (StatusBarPropertiesButton is not null)
            {
                ToolTipService.SetToolTip(StatusBarPropertiesButton, PropertiesToolTipText);
            }

            return;
        }

        if (StatusBarSignedLabelText is not null)
        {
            StatusBarSignedLabelText.Text = ResourceStrings.Get(ResourceKeys.StatusBarSignedLabel);
        }

        StatusBarSignatureIcon.Glyph = doc.SignatureStatusGlyph;
        string brushKey = doc.OverallSignatureStatus switch
        {
            PdfSignatureVerificationStatus.Valid or PdfSignatureVerificationStatus.ValidUntrustedRoot => "SystemFillColorSuccessBrush",
            PdfSignatureVerificationStatus.ModifiedAfterSigning => "SystemFillColorCautionBrush",
            PdfSignatureVerificationStatus.Invalid => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };

        if (Application.Current.Resources.TryGetValue(brushKey, out object? brushObj) && brushObj is Brush brush)
        {
            StatusBarSignatureIcon.Foreground = brush;
        }

        StatusBarSignaturePanel.Visibility = Visibility.Visible;

        if (StatusBarPropertiesButton is not null)
        {
            string tip = string.IsNullOrWhiteSpace(doc.SignatureStatusTooltip)
                ? PropertiesToolTipText
                : $"{PropertiesToolTipText}\n{doc.SignatureStatusTooltip}";
            ToolTipService.SetToolTip(StatusBarPropertiesButton, tip);
        }
    }

    private void ScrollToAnnotation(PdfAnnotationInfo annot)
    {
        IReadOnlyList<PdfRect> rects = annot.QuadRects.Count > 0
            ? annot.QuadRects
            : [annot.Bounds];
        ScrollToSearchMatch(new PdfSearchMatch(annot.PageIndex, 0, 0, 0, rects));
    }

    private void OnAnnotationFocusRequested(PdfAnnotationInfo annot)
    {
        ScrollToAnnotation(annot);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (ViewModel.CurrentDocument is null)
            {
                return;
            }

            PdfPageCanvas? canvas = FindRealizedCanvasForPage(annot.PageIndex);
            if (canvas?.PageInfo is { } pageInfo)
            {
                PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
                Rect pageDip = transform.ToPageRect(annot.Bounds);
                Point centerDip = new(pageDip.Left + (pageDip.Width * 0.5), pageDip.Bottom);
                OpenAnnotationUi(annot, canvas, centerDip);
            }
        });
    }

    /// <summary>
    /// Opens the full editor when the annotation already carries text; otherwise opens the compact quick-bar.
    /// On touch input, uses the non-modal floating QuickBar overlay so moving and resizing never hit a light-dismiss wall.
    /// </summary>
    private void OpenAnnotationUi(PdfAnnotationInfo annot, PdfPageCanvas? canvas, Point canvasDipPt)
    {
        if (_isLastPointerInputTouch)
        {
            // Keep touch interaction 100% non-modal: selecting shows TouchAnnotationMoveBodyHandle + resize corners +
            // TouchAnnotationQuickBarBorder above ScrollViewer so dragging/resizing works on the very first touch.
            _touchQuickBarBoundAnnotPage = -1;
            _touchQuickBarBoundAnnotIndex = -1;
            UpdateTouchAnnotationResizeHandlesLayout();
            return;
        }

        if (annot.IsTextBox)
        {
            OpenAnnotationQuickBar(annot, canvas, canvasDipPt);
            return;
        }

        if (annot.HasComment)
        {
            OpenExistingAnnotationEditor(annot, canvas, canvasDipPt);
        }
        else
        {
            OpenAnnotationQuickBar(annot, canvas, canvasDipPt);
        }
    }

    private void OnSignatureInspectRequested(PdfSignatureInfo sig)
    {
        _ = ViewModel.ShowSignatureDetailsAsync(sig);
    }

    private void OnCanvasAnnotationClicked(PdfPageCanvas canvas, PdfAnnotationInfo annot, Point canvasDipPt)
    {
        HideAnnotationPopupsImmediate();
        OpenAnnotationUi(annot, canvas, canvasDipPt);
    }

    private void OnCanvasAnnotationMoved(PdfPageCanvas canvas, PdfAnnotationInfo annot, Vector2 deltaPdf)
    {
        HideAnnotationPopupsImmediate();
        if (ViewModel.CurrentDocument is { } doc)
        {
            _ = doc.MoveAnnotationAsync(annot.PageIndex, annot.AnnotIndex, deltaPdf.X, deltaPdf.Y);
        }
    }

    private void OnCanvasAnnotationResized(PdfPageCanvas canvas, PdfAnnotationInfo annot, PdfRect newBounds)
    {
        HideAnnotationPopupsImmediate();
        if (ViewModel.CurrentDocument is { } doc)
        {
            _ = doc.ResizeAnnotationAsync(annot.PageIndex, annot.AnnotIndex, newBounds);
        }
    }

    private void OnCanvasSignatureClicked(PdfPageCanvas canvas, PdfSignatureInfo sig, Point canvasDipPt)
    {
        HideAnnotationPopupsImmediate();
        _ = ViewModel.ShowSignatureDetailsAsync(sig);
    }

    private void OnCanvasPlaceTextNoteRequested(PdfPageCanvas canvas, Vector2 pdfPt, Point canvasDipPt)
    {
        if (canvas.PageInfo is not { } pageInfo)
        {
            return;
        }

        if (ViewModel.ActiveTool == AnnotationTool.TextNote)
        {
            ViewModel.ActiveTool = AnnotationTool.Select;
        }

        DismissTopNotification();
        HideAnnotationPopupsImmediate();
        OpenCreateAnnotationEditor(AnnotationEditorMode.CreateNote, pageInfo.Index, pdfPt, canvas, canvasDipPt, pendingSelection: null);
    }

    private async void OnCanvasPlaceImageRequested(PdfPageCanvas canvas, Vector2 pdfPt, Point canvasDipPt)
    {
        if (canvas.PageInfo is not { } pageInfo || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        PdfCapturedImage? pending = ViewModel.PendingImageToInsert;
        DismissTopNotification();
        ViewModel.CancelActiveInsertTool();
        if (pending is null)
        {
            return;
        }

        HideAnnotationPopupsImmediate();
        await doc.AddImageAnnotationAsync(pageInfo.Index, pdfPt.X, pdfPt.Y, pending);
    }

    private void OnCanvasPlaceTextBoxRequested(PdfPageCanvas canvas, Vector2 pdfPt, Point canvasDipPt)
    {
        if (canvas.PageInfo is null || ViewModel.CurrentDocument is null)
        {
            return;
        }

        DismissTopNotification();
        ViewModel.CancelActiveInsertTool();
        HideAnnotationPopupsImmediate();
        OpenInlineTextEditorForCreate(canvas, pdfPt);
    }

    private async void OnCanvasShapeCompleted(PdfPageCanvas canvas, PdfAnnotationShapeKind shapeKind, Vector2 startPdf, Vector2 endPdf)
    {
        if (canvas.PageInfo is not { } pageInfo || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        HideAnnotationPopupsImmediate();
        try
        {
            await doc.AddShapeAsync(
                pageInfo.Index,
                shapeKind,
                startPdf,
                endPdf,
                doc.ActiveAnnotationColor,
                ViewModel.StrokeThicknessPt);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }


    private void OnContextHighlightClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc?.ActiveSelection is { HasContent: true } sel)
        {
            _ = doc.AddMarkupForSelectionAsync(sel, PdfAnnotationSubtype.Highlight, doc.ActiveAnnotationColor);
        }
    }

    private void OnContextHighlightWithCommentClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc?.ActiveSelection is not { HasContent: true } sel)
        {
            return;
        }

        PdfPageCanvas? anchorCanvas = _contextTargetCanvas?.PageInfo?.Index == sel.StartPageIndex
            ? _contextTargetCanvas
            : FindRealizedCanvasForPage(sel.StartPageIndex);
        Point anchorDip = ComputeSelectionAnchorCanvasDip(sel, anchorCanvas);
        RunAfterContextMenuClosed(() => OpenCreateAnnotationEditor(
            AnnotationEditorMode.CreateMarkupWithComment,
            sel.StartPageIndex,
            default,
            anchorCanvas,
            anchorDip,
            sel));
    }

    private void OnContextMarkupSubtypeClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc?.ActiveSelection is { HasContent: true } sel && sender is FrameworkElement fe)
        {
            PdfAnnotationSubtype subtype = ParseMarkupSubtypeTag(fe.Tag as string, PdfAnnotationSubtype.Underline);
            _ = doc.AddMarkupForSelectionAsync(sel, subtype, doc.ActiveAnnotationColor);
        }
    }

    private void OnContextColorHighlightClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            PdfAnnotationColor current = ViewModel.CurrentDocument?.ActiveAnnotationColor ?? ViewModel.DefaultAnnotationColor;
            PdfAnnotationColor color = ParseAnnotationColorTag(fe.Tag as string, current);
            SetDefaultAnnotationColor(color);
        }
    }

    private void OnContextInspectSignatureClick(object sender, RoutedEventArgs e)
    {
        if (_contextTargetSignature is { } sig)
        {
            RunAfterContextMenuClosed(() => _ = ViewModel.ShowSignatureDetailsAsync(sig));
        }
    }

    private void OnContextEditAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (_contextTargetAnnotation is { } annot && _contextTargetCanvas is { } canvas)
        {
            Point dipPt = _contextTargetDipPt;
            RunAfterContextMenuClosed(() =>
            {
                if (ViewModel.CurrentDocument is not null)
                {
                    ViewModel.CurrentDocument.SelectedAnnotation = annot;
                }

                OpenExistingAnnotationEditor(annot, canvas, dipPt);
            });
        }
    }

    private void OnContextCopyAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (_contextTargetAnnotation is { } annot)
        {
            CopyAnnotationToClipboard(annot);
        }
    }

    private void OnContextCutAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (_contextTargetAnnotation is { } annot && ViewModel.CurrentDocument is { } doc)
        {
            _ = CutAnnotationToClipboardAsync(doc, annot);
        }
    }

    private void OnContextPasteAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (_contextTargetCanvas?.PageInfo is { } pageInfo && ViewModel.CurrentDocument is { } doc)
        {
            _ = PasteCopiedAnnotationAtAsync(doc, pageInfo.Index, _contextTargetPdfPt.X, _contextTargetPdfPt.Y);
        }
    }

    private void OnContextDeleteAnnotationClick(object sender, RoutedEventArgs e)
    {
        if (_contextTargetAnnotation is { } annot && ViewModel.CurrentDocument is { } doc)
        {
            _ = doc.DeleteAnnotationAsync(annot.PageIndex, annot.AnnotIndex);
        }
    }

    private void OnContextAddTextBoxClick(object sender, RoutedEventArgs e)
    {
        if (_contextTargetCanvas is { PageInfo: { } pageInfo } canvas)
        {
            int pageIndex = pageInfo.Index;
            Vector2 pdfPt = _contextTargetPdfPt;
            Point dipPt = _contextTargetDipPt;
            RunAfterContextMenuClosed(() => OpenCreateAnnotationEditor(
                AnnotationEditorMode.CreateNote,
                pageIndex,
                pdfPt,
                canvas,
                dipPt,
                pendingSelection: null));
        }
    }

    private void OnCanvasMouseClickedOnPage(PdfPageCanvas canvas, Vector2 pdfPt)
    {
        if (canvas.PageInfo is { } pageInfo)
        {
            _lastMouseClickPageIndex = pageInfo.Index;
            _lastMouseClickPdfPt = pdfPt;
            _hasLastMouseClickPdfPt = true;
        }
    }

    private void TryRecordLastMouseClickFromCursor()
    {
        if (ScrollViewerContentRoot is null || XamlRoot?.Content is null)
        {
            return;
        }

        try
        {
            nint hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            if (hwnd == nint.Zero || GetCursorPos(out NativePoint screenPt) == 0 || ScreenToClient(hwnd, ref screenPt) == 0)
            {
                return;
            }

            double scale = XamlRoot.RasterizationScale > 0.01 ? XamlRoot.RasterizationScale : 1.0;
            Point clientDip = new(screenPt.X / scale, screenPt.Y / scale);
            Point rootPt = XamlRoot.Content.TransformToVisual(ScrollViewerContentRoot).TransformPoint(clientDip);
            if (TryFindCanvasAtRootPoint(rootPt, out PdfPageCanvas? hitCanvas, out Point canvasDip) &&
                hitCanvas?.PageInfo is { } pageInfo)
            {
                _lastMouseClickPageIndex = pageInfo.Index;
                _lastMouseClickPdfPt = hitCanvas.ToPdfPoint(canvasDip);
                _hasLastMouseClickPdfPt = true;
            }
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetCursorPos(out NativePoint lpPoint);

    [LibraryImport("user32.dll")]
    private static partial int ScreenToClient(nint hWnd, ref NativePoint lpPoint);

    // =========================================================================
    // Compact annotation quick-bar (icon-only Acrylic bar)
    // =========================================================================

    private void OpenAnnotationQuickBar(PdfAnnotationInfo annot, PdfPageCanvas? canvas, Point anchorCanvasDipPt)
    {
        if (AnnotationQuickBarFlyout is null || ScrollViewerContentRoot is null || annot.IsSignatureWidget)
        {
            return;
        }

        _quickBarAnnotation = annot;
        _quickBarCanvas = canvas;
        _quickBarAnchorDipPt = anchorCanvasDipPt;
        _pendingAfterQuickBarClosed = null;

        SetVisible(QuickBarColorButton, annot.SupportsColorChange);
        SetVisible(QuickBarCopyButton, annot.CanCopyOrCut);
        SetVisible(QuickBarCutButton, annot.CanCopyOrCut);
        if (QuickBarAddCommentButton?.Content is FontIcon quickCommentIcon)
        {
            quickCommentIcon.Glyph = annot.IsTextBox ? "\uE70F" : "\uE90A";
        }
        SetToolTip(
            QuickBarAddCommentButton,
            annot.IsTextBox ? ResourceKeys.AnnotEditTextToolTip : ResourceKeys.AnnotAddCommentToolTip);
        SetVisible(QuickBarColorRow, false);
        ApplySwatchSelectedState(QuickColorYellowButton, IsSameColor(annot.Color, PdfAnnotationColor.Yellow));
        ApplySwatchSelectedState(QuickColorGreenButton, IsSameColor(annot.Color, PdfAnnotationColor.Green));
        ApplySwatchSelectedState(QuickColorBlueButton, IsSameColor(annot.Color, PdfAnnotationColor.Blue));
        ApplySwatchSelectedState(QuickColorPinkButton, IsSameColor(annot.Color, PdfAnnotationColor.Pink));
        ApplySwatchSelectedState(QuickColorOrangeButton, IsSameColor(annot.Color, PdfAnnotationColor.Orange));
        ApplySwatchSelectedState(QuickColorRedButton, IsSameColor(annot.Color, PdfAnnotationColor.Red));

        try
        {
            double rootW = Math.Max(64.0, ScrollViewerContentRoot.ActualWidth);
            double rootH = Math.Max(64.0, ScrollViewerContentRoot.ActualHeight);
            Point rootPt = new(rootW * 0.5, rootH * 0.35);
            double annotTopY = rootPt.Y;
            double annotBottomY = rootPt.Y;
            if (canvas is not null)
            {
                try
                {
                    rootPt = canvas.TransformToVisual(ScrollViewerContentRoot).TransformPoint(anchorCanvasDipPt);
                    annotTopY = rootPt.Y;
                    annotBottomY = rootPt.Y;
                    if (canvas.PageInfo is { } pageInfo && !annot.Bounds.IsEmpty)
                    {
                        PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
                        Rect pageDip = transform.ToPageRect(annot.Bounds);
                        Point topPt = canvas.TransformToVisual(ScrollViewerContentRoot).TransformPoint(new Point(pageDip.Left + (pageDip.Width * 0.5), pageDip.Top));
                        Point botPt = canvas.TransformToVisual(ScrollViewerContentRoot).TransformPoint(new Point(pageDip.Left + (pageDip.Width * 0.5), pageDip.Bottom));
                        annotTopY = Math.Min(topPt.Y, botPt.Y);
                        annotBottomY = Math.Max(topPt.Y, botPt.Y);
                    }
                }
                catch
                {
                }
            }

            double clampedX = Math.Clamp(rootPt.X, 24.0, rootW - 24.0);
            bool placeBelow = Math.Min(rootPt.Y, annotTopY) < 92.0;
            double offsetY = placeBelow
                ? Math.Clamp(Math.Max(rootPt.Y, annotBottomY) + 20.0, 24.0, rootH - 24.0)
                : Math.Clamp(Math.Min(rootPt.Y, annotTopY) - 20.0, 24.0, rootH - 24.0);

            if (AnnotationQuickBarFlyout.IsOpen)
            {
                _quickBarClosedProgrammatically = true;
                AnnotationQuickBarFlyout.Hide();
            }

            long openSeq = ++_annotationPopupOpenSeq;
            var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Point(clampedX, offsetY),
                Placement = placeBelow
                    ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
                    : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top,
                // Use Transient on touch so touching the selected annotation (or its resize handles) or tapping outside
                // passes straight through to PdfPageCanvas instead of being swallowed by a modal light-dismiss wall.
                ShowMode = _isLastPointerInputTouch
                    ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Transient
                    : Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            };

            void ShowQuickBarNow()
            {
                if (_annotationPopupOpenSeq != openSeq || AnnotationQuickBarFlyout is null || ScrollViewerContentRoot is null)
                {
                    return;
                }

                try
                {
                    _quickBarClosedProgrammatically = false;
                    _quickBarOpenedTick = Environment.TickCount64;
                    AnnotationQuickBarFlyout.ShowAt(ScrollViewerContentRoot, options);
                }
                catch
                {
                }
            }

            if (_isLastPointerInputTouch)
            {
                // Defer 1 dispatcher tick on touch so Windows completes PointerReleased / synthesized touch-up events
                // before the modal light-dismiss flyout enters the visual tree.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, ShowQuickBarNow);
            }
            else
            {
                ShowQuickBarNow();
            }
        }
        catch
        {
        }
    }

    private void OnAnnotationQuickBarFlyoutClosed(object? sender, object e)
    {
        bool wasProgrammatic = _quickBarClosedProgrammatically;
        _quickBarClosedProgrammatically = false;
        SetVisible(QuickBarColorRow, false);
        Action? pending = _pendingAfterQuickBarClosed;
        _pendingAfterQuickBarClosed = null;
        if (!wasProgrammatic && pending is null)
        {
            if (!_isLastPointerInputTouch)
            {
                TryRecordLastMouseClickFromCursor();
            }

            bool isSpuriousTouchDismiss = _isLastPointerInputTouch &&
                (Environment.TickCount64 - _quickBarOpenedTick) < 220;
            if (!isSpuriousTouchDismiss && ViewModel.CurrentDocument is { SelectedAnnotation: { } sel } doc)
            {
                bool keepForMouseResize = !_isLastPointerInputTouch &&
                    sel.IsResizable &&
                    IsCursorNearAnnotationBounds(sel, marginDip: 14.0);
                if (!keepForMouseResize)
                {
                    doc.SelectedAnnotation = null;
                }
            }
        }

        if (pending is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => pending());
        }
    }

    private bool IsCursorNearAnnotationBounds(PdfAnnotationInfo annot, double marginDip)
    {
        if (ScrollViewerContentRoot is null ||
            XamlRoot?.Content is null ||
            annot.Bounds.IsEmpty ||
            FindRealizedCanvasForPage(annot.PageIndex) is not { PageInfo: { } pageInfo } canvas)
        {
            return false;
        }

        try
        {
            nint hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
            if (hwnd == nint.Zero || GetCursorPos(out NativePoint screenPt) == 0 || ScreenToClient(hwnd, ref screenPt) == 0)
            {
                return false;
            }

            double scale = XamlRoot.RasterizationScale > 0.01 ? XamlRoot.RasterizationScale : 1.0;
            Point clientDip = new(screenPt.X / scale, screenPt.Y / scale);
            Point canvasDip = XamlRoot.Content.TransformToVisual(canvas).TransformPoint(clientDip);
            PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
            Rect rectDip = transform.ToPageRect(annot.Bounds);
            return canvasDip.X >= rectDip.Left - marginDip &&
                   canvasDip.X <= rectDip.Right + marginDip &&
                   canvasDip.Y >= rectDip.Top - marginDip &&
                   canvasDip.Y <= rectDip.Bottom + marginDip;
        }
        catch
        {
            return false;
        }
    }

    private void CloseQuickBarThen(Action? action)
    {
        SetVisible(TouchQuickBarColorRow, false);
        if (TouchAnnotationQuickBarBorder is not null && TouchAnnotationQuickBarBorder.Visibility == Visibility.Visible)
        {
            TouchAnnotationQuickBarBorder.Visibility = Visibility.Collapsed;
        }

        if (AnnotationQuickBarFlyout?.IsOpen == true)
        {
            _quickBarClosedProgrammatically = true;
            _pendingAfterQuickBarClosed = action;
            AnnotationQuickBarFlyout.Hide();
            return;
        }

        _pendingAfterQuickBarClosed = null;
        if (action is not null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () => action());
        }
    }

    private void OnQuickBarAddCommentClick(object sender, RoutedEventArgs e)
    {
        if (_quickBarAnnotation is not { } annot)
        {
            CloseQuickBarThen(null);
            return;
        }

        PdfPageCanvas? canvas = _quickBarCanvas;
        Point dipPt = _quickBarAnchorDipPt;
        if (annot.IsTextBox)
        {
            CloseQuickBarThen(() => OpenInlineTextEditorForExisting(annot, canvas));
            return;
        }

        CloseQuickBarThen(() => OpenExistingAnnotationEditor(annot, canvas, dipPt));
    }

    private void OnQuickBarColorToggleClick(object sender, RoutedEventArgs e)
    {
        if (TouchAnnotationQuickBarBorder?.Visibility == Visibility.Visible && TouchQuickBarColorRow is not null)
        {
            TouchQuickBarColorRow.Visibility = TouchQuickBarColorRow.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => UpdateTouchAnnotationResizeHandlesLayout());
            return;
        }

        if (QuickBarColorRow is not null)
        {
            QuickBarColorRow.Visibility = QuickBarColorRow.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }

    private void OnQuickBarColorSelectClick(object sender, RoutedEventArgs e)
    {
        PdfAnnotationInfo? annot = _quickBarAnnotation;
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        CloseQuickBarThen(null);

        if (annot is null || doc is null || sender is not FrameworkElement fe || !annot.SupportsColorChange)
        {
            return;
        }

        PdfAnnotationColor color = ParseAnnotationColorTag(fe.Tag as string, annot.Color);
        SetDefaultAnnotationColor(color);
        PdfFreeTextStyle? updatedTextStyle = annot.TextStyle is { } ts ? ts with { Color = color } : null;
        _ = doc.UpdateAnnotationAsync(annot.PageIndex, annot.AnnotIndex, annot.Contents ?? string.Empty, color, newTextStyle: updatedTextStyle);
    }

    private void OnQuickBarCopyClick(object sender, RoutedEventArgs e)
    {
        if (_quickBarAnnotation is { } annot)
        {
            CopyAnnotationToClipboard(annot);
        }

        CloseQuickBarThen(null);
    }

    private void OnQuickBarCutClick(object sender, RoutedEventArgs e)
    {
        // Guard against accidental second click of a double-click / double-tap landing on Cut right after open.
        if (Environment.TickCount64 - _quickBarOpenedTick < 280)
        {
            return;
        }

        PdfAnnotationInfo? annot = _quickBarAnnotation;
        CloseQuickBarThen(null);
        if (annot is not null && ViewModel.CurrentDocument is { } doc)
        {
            _ = CutAnnotationToClipboardAsync(doc, annot);
        }
    }

    private void OnQuickBarDeleteClick(object sender, RoutedEventArgs e)
    {
        // Guard against accidental second click of a double-click / double-tap landing on Delete right after open.
        if (Environment.TickCount64 - _quickBarOpenedTick < 280)
        {
            return;
        }

        PdfAnnotationInfo? annot = _quickBarAnnotation;
        CloseQuickBarThen(null);
        if (annot is not null && ViewModel.CurrentDocument is { } doc)
        {
            _ = doc.DeleteAnnotationAsync(annot.PageIndex, annot.AnnotIndex);
        }

        ReturnFocusToViewer();
    }

    private void OnQuickBarCloseClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is { } doc)
        {
            doc.SelectedAnnotation = null;
        }

        CloseQuickBarThen(null);
        ReturnFocusToViewer();
    }

    private static bool IsSameColor(PdfAnnotationColor a, PdfAnnotationColor b) =>
        Math.Abs(a.R - b.R) < 12 && Math.Abs(a.G - b.G) < 12 && Math.Abs(a.B - b.B) < 12;

    private static void ApplySwatchSelectedState(Button? btn, bool isSelected)
    {
        if (btn is null)
        {
            return;
        }

        if (isSelected)
        {
            btn.BorderThickness = new Thickness(2.5);
            btn.BorderBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
        else
        {
            btn.BorderThickness = new Thickness(1);
            btn.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        }
    }

    // =========================================================================
    // Annotation clipboard (copy / cut / paste)
    // =========================================================================

    private void CopyAnnotationToClipboard(PdfAnnotationInfo annot) => _ = CopyAnnotationToClipboardAsync(annot);

    private async Task CopyAnnotationToClipboardAsync(PdfAnnotationInfo annot)
    {
        if (!annot.CanCopyOrCut)
        {
            return;
        }

        if (annot is { Subtype: PdfAnnotationSubtype.Stamp, IsTextBox: false, CapturedImage: null } &&
            ViewModel.CurrentDocument is { } doc)
        {
            PdfCapturedImage? captured = await doc.CaptureStampImageAsync(annot.PageIndex, annot.AnnotIndex);
            if (captured is not null)
            {
                annot = annot with { CapturedImage = captured };
            }
        }

        _copiedAnnotation = annot;
        _isCutClipboard = false;
    }

    private async Task CutAnnotationToClipboardAsync(DocumentViewModel doc, PdfAnnotationInfo annot)
    {
        if (!annot.CanCopyOrCut)
        {
            return;
        }

        if (annot is { Subtype: PdfAnnotationSubtype.Stamp, IsTextBox: false, CapturedImage: null })
        {
            PdfCapturedImage? captured = await doc.CaptureStampImageAsync(annot.PageIndex, annot.AnnotIndex);
            if (captured is not null)
            {
                annot = annot with { CapturedImage = captured };
            }
        }

        _copiedAnnotation = annot;
        _isCutClipboard = true;
        await doc.DeleteAnnotationAsync(annot.PageIndex, annot.AnnotIndex);
    }

    private async Task PasteCopiedAnnotationAtAsync(DocumentViewModel doc, int pageIndex, double pdfX, double pdfY)
    {
        PdfAnnotationInfo? source = _copiedAnnotation;
        if (source is null)
        {
            return;
        }

        bool wasCut = _isCutClipboard;
        PdfAnnotationInfo? pasted = await doc.PasteAnnotationAsync(source, pageIndex, pdfX, pdfY);
        if (pasted is not null && wasCut && ReferenceEquals(_copiedAnnotation, source))
        {
            _copiedAnnotation = null;
            _isCutClipboard = false;
        }
    }

    private Task PasteCopiedAnnotationFromKeyboardAsync(DocumentViewModel doc)
    {
        if (_copiedAnnotation is not { } source || doc.Pages.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (_hasLastMouseClickPdfPt &&
            _lastMouseClickPageIndex >= 0 &&
            _lastMouseClickPageIndex < doc.Pages.Count)
        {
            return PasteCopiedAnnotationAtAsync(doc, _lastMouseClickPageIndex, _lastMouseClickPdfPt.X, _lastMouseClickPdfPt.Y);
        }

        int pageIndex = Math.Clamp(doc.CurrentPageIndex, 0, doc.Pages.Count - 1);
        PdfPageInfo page = doc.Pages[pageIndex];
        double minX = page.OriginXPt;
        double minY = page.OriginYPt;
        double maxX = minX + page.WidthPt;
        double maxY = minY + page.HeightPt;
        double w = source.Bounds.Width;
        double h = source.Bounds.Height;

        double x;
        double y;
        if (source.PageIndex == pageIndex && !source.Bounds.IsEmpty)
        {
            double offset = _isCutClipboard ? 0.0 : 18.0;
            x = source.Bounds.Left + offset;
            y = source.Bounds.Top - offset;
        }
        else
        {
            x = minX + (page.WidthPt * 0.5) - (w * 0.5);
            y = minY + (page.HeightPt * 0.5) + (h * 0.5);
        }

        x = Math.Clamp(x, minX, Math.Max(minX, maxX - w));
        y = Math.Clamp(y, Math.Min(maxY, minY + h), maxY);
        return PasteCopiedAnnotationAtAsync(doc, pageIndex, x, y);
    }

    private Point ComputeSelectionAnchorCanvasDip(PdfTextSelection selection, PdfPageCanvas? canvas)
    {
        IReadOnlyList<PdfRect> rects = selection.GetRectsForPage(selection.StartPageIndex);
        if (rects.Count > 0 && canvas?.PageInfo is { } pageInfo)
        {
            PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
            Rect r = transform.ToPageRect(rects[0]);
            return new Point(r.Left + (r.Width * 0.5), r.Bottom);
        }

        return _contextTargetDipPt;
    }

    private PdfAnnotationInfo? _annotEditorAnnotation;

    private void OpenCreateAnnotationEditor(
        AnnotationEditorMode mode,
        int pageIndex,
        Vector2 pdfPt,
        PdfPageCanvas? anchorCanvas,
        Point anchorCanvasDipPt,
        PdfTextSelection? pendingSelection)
    {
        if (AnnotationEditorFlyout is null || ViewModel.CurrentDocument is not { } doc)
        {
            return;
        }

        _annotEditorMode = mode;
        _annotEditorPageIndex = pageIndex;
        _annotEditorAnnotIndex = -1;
        _annotEditorAnnotation = null;
        _annotEditorPdfPt = pdfPt;
        _annotEditorPendingSelection = pendingSelection;
        _annotEditorPendingMarkupSubtype = PdfAnnotationSubtype.Highlight;
        _annotEditorSelectedColor = doc.ActiveAnnotationColor;
        _annotEditorCanEditText = true;
        _annotEditorCanEditColor = true;

        if (AnnotationEditorTitleText is not null)
        {
            AnnotationEditorTitleText.Text = mode switch
            {
                AnnotationEditorMode.CreateFreeText => ResourceStrings.Get(ResourceKeys.AnnotSubtypeFreeText),
                AnnotationEditorMode.CreateMarkupWithComment => ResourceStrings.Get(ResourceKeys.AnnotSubtypeHighlight),
                _ => ResourceStrings.Get(ResourceKeys.AnnotSubtypeText),
            };
        }

        if (AnnotationEditorMetaText is not null)
        {
            AnnotationEditorMetaText.Text = string.Empty;
            AnnotationEditorMetaText.Visibility = Visibility.Collapsed;
        }

        if (AnnotationEditorQuoteBorder is not null && AnnotationEditorQuoteText is not null)
        {
            if (pendingSelection is { HasContent: true } && !string.IsNullOrWhiteSpace(pendingSelection.SelectedText))
            {
                AnnotationEditorQuoteText.Text = pendingSelection.SelectedText.Trim();
                AnnotationEditorQuoteBorder.Visibility = Visibility.Visible;
            }
            else
            {
                AnnotationEditorQuoteText.Text = string.Empty;
                AnnotationEditorQuoteBorder.Visibility = Visibility.Collapsed;
            }
        }

        SetVisible(AnnotationEditorColorRow, true);
        SetVisible(AnnotationEditorDeleteButton, false);
        SetVisible(AnnotationEditorCopyButton, false);
        SetVisible(AnnotationEditorCutButton, false);

        if (AnnotationEditorTextBox is not null)
        {
            AnnotationEditorTextBox.Text = string.Empty;
            AnnotationEditorTextBox.PlaceholderText = mode == AnnotationEditorMode.CreateFreeText
                ? ResourceStrings.Get(ResourceKeys.AnnotTextBoxPlaceholder)
                : ResourceStrings.Get(ResourceKeys.AnnotCommentPlaceholder);
        }

        UpdateAnnotationEditorUiState();
        ShowAnnotationEditorAt(anchorCanvas, anchorCanvasDipPt);
    }

    private void OpenExistingAnnotationEditor(PdfAnnotationInfo annot, PdfPageCanvas? anchorCanvas, Point anchorCanvasDipPt)
    {
        if (AnnotationEditorFlyout is null)
        {
            return;
        }

        _annotEditorMode = AnnotationEditorMode.EditExisting;
        _annotEditorPageIndex = annot.PageIndex;
        _annotEditorAnnotIndex = annot.AnnotIndex;
        _annotEditorAnnotation = annot;
        _annotEditorPdfPt = default;
        _annotEditorPendingSelection = null;
        _annotEditorSelectedColor = annot.Color;
        _annotEditorCanEditText = !annot.IsSignatureWidget;
        _annotEditorCanEditColor = annot.SupportsColorChange;

        if (AnnotationEditorTitleText is not null)
        {
            AnnotationEditorTitleText.Text = AnnotationItemViewModel.ResolveSubtypeTitle(annot);
        }

        if (AnnotationEditorMetaText is not null)
        {
            string meta = BuildAnnotationMetadataLine(annot);
            AnnotationEditorMetaText.Text = meta;
            AnnotationEditorMetaText.Visibility = string.IsNullOrWhiteSpace(meta) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (AnnotationEditorQuoteBorder is not null && AnnotationEditorQuoteText is not null)
        {
            if (!string.IsNullOrWhiteSpace(annot.QuotedText))
            {
                AnnotationEditorQuoteText.Text = annot.QuotedText;
                AnnotationEditorQuoteBorder.Visibility = Visibility.Visible;
            }
            else
            {
                AnnotationEditorQuoteText.Text = string.Empty;
                AnnotationEditorQuoteBorder.Visibility = Visibility.Collapsed;
            }
        }

        SetVisible(AnnotationEditorColorRow, _annotEditorCanEditColor);
        SetVisible(AnnotationEditorDeleteButton, true);
        SetVisible(AnnotationEditorCopyButton, annot.CanCopyOrCut);
        SetVisible(AnnotationEditorCutButton, annot.CanCopyOrCut);

        if (AnnotationEditorTextBox is not null)
        {
            AnnotationEditorTextBox.Text = annot.Contents ?? string.Empty;
            AnnotationEditorTextBox.PlaceholderText = annot.Subtype == PdfAnnotationSubtype.FreeText
                ? ResourceStrings.Get(ResourceKeys.AnnotTextBoxPlaceholder)
                : ResourceStrings.Get(ResourceKeys.AnnotCommentPlaceholder);
        }

        UpdateAnnotationEditorUiState();
        ShowAnnotationEditorAt(anchorCanvas, anchorCanvasDipPt);
    }

    private void ShowAnnotationEditorAt(PdfPageCanvas? anchorCanvas, Point anchorCanvasDipPt)
    {
        if (AnnotationEditorFlyout is null || ScrollViewerContentRoot is null)
        {
            return;
        }

        try
        {
            double rootW = Math.Max(64.0, ScrollViewerContentRoot.ActualWidth);
            double rootH = Math.Max(64.0, ScrollViewerContentRoot.ActualHeight);
            Point rootPt = new(rootW * 0.5, rootH * 0.35);

            if (anchorCanvas is not null)
            {
                try
                {
                    rootPt = anchorCanvas.TransformToVisual(ScrollViewerContentRoot).TransformPoint(anchorCanvasDipPt);
                }
                catch
                {
                }
            }

            double clampedX = Math.Clamp(rootPt.X, 24.0, rootW - 24.0);
            double clampedY = Math.Clamp(rootPt.Y, 24.0, rootH - 24.0);

            if (AnnotationEditorFlyout.IsOpen)
            {
                _annotEditorClosedProgrammatically = true;
                AnnotationEditorFlyout.Hide();
            }

            long openSeq = ++_annotationPopupOpenSeq;
            var options = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = new Point(clampedX, clampedY),
                Placement = clampedY > rootH * 0.65
                    ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft
                    : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
                ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            };

            void ShowEditorNow()
            {
                if (_annotationPopupOpenSeq != openSeq || AnnotationEditorFlyout is null || ScrollViewerContentRoot is null)
                {
                    return;
                }

                try
                {
                    _annotEditorClosedProgrammatically = false;
                    _annotEditorOpenedTick = Environment.TickCount64;
                    AnnotationEditorFlyout.ShowAt(ScrollViewerContentRoot, options);

                    if (AnnotationEditorTextBox is not null)
                    {
                        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                        {
                            try
                            {
                                AnnotationEditorTextBox.Focus(FocusState.Programmatic);
                                AnnotationEditorTextBox.SelectionStart = AnnotationEditorTextBox.Text?.Length ?? 0;
                            }
                            catch
                            {
                            }
                        });
                    }
                }
                catch
                {
                }
            }

            if (_isLastPointerInputTouch)
            {
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, ShowEditorNow);
            }
            else
            {
                ShowEditorNow();
            }
        }
        catch
        {
        }
    }

    private void UpdateAnnotationEditorUiState()
    {
        UpdateSwatchSelection(AnnotColorYellowButton, PdfAnnotationColor.Yellow);
        UpdateSwatchSelection(AnnotColorGreenButton, PdfAnnotationColor.Green);
        UpdateSwatchSelection(AnnotColorBlueButton, PdfAnnotationColor.Blue);
        UpdateSwatchSelection(AnnotColorPinkButton, PdfAnnotationColor.Pink);
        UpdateSwatchSelection(AnnotColorOrangeButton, PdfAnnotationColor.Orange);
        UpdateSwatchSelection(AnnotColorRedButton, PdfAnnotationColor.Red);

        if (AnnotationEditorTextBox is not null)
        {
            AnnotationEditorTextBox.IsReadOnly = !_annotEditorCanEditText;
        }

        if (AnnotationEditorSaveButton is not null)
        {
            bool requiresNonEmptyText = _annotEditorMode is AnnotationEditorMode.CreateNote or AnnotationEditorMode.CreateFreeText;
            string currentText = AnnotationEditorTextBox?.Text ?? string.Empty;
            AnnotationEditorSaveButton.IsEnabled = !requiresNonEmptyText || !string.IsNullOrWhiteSpace(currentText);
        }
    }

    private void UpdateSwatchSelection(Button? btn, PdfAnnotationColor preset)
    {
        ApplySwatchSelectedState(btn, IsSameColor(_annotEditorSelectedColor, preset));
    }

    private void OnAnnotationEditorTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateAnnotationEditorUiState();
    }

    private void OnAnnotationColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            _annotEditorSelectedColor = ParseAnnotationColorTag(fe.Tag as string, _annotEditorSelectedColor);
            SetDefaultAnnotationColor(_annotEditorSelectedColor);
            UpdateAnnotationEditorUiState();
        }
    }

    private async void OnAnnotationEditorSaveClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        if (doc is null)
        {
            AnnotationEditorFlyout?.Hide();
            return;
        }

        string contents = (AnnotationEditorTextBox?.Text ?? string.Empty).Trim();
        SetDefaultAnnotationColor(_annotEditorSelectedColor);

        AnnotationEditorMode mode = _annotEditorMode;
        int pageIndex = _annotEditorPageIndex;
        int annotIndex = _annotEditorAnnotIndex;
        Vector2 pdfPt = _annotEditorPdfPt;
        PdfTextSelection? pendingSel = _annotEditorPendingSelection;
        PdfAnnotationSubtype markupSubtype = _annotEditorPendingMarkupSubtype;
        PdfAnnotationColor color = _annotEditorSelectedColor;
        bool canEditColor = _annotEditorCanEditColor;

        AnnotationEditorFlyout?.Hide();

        switch (mode)
        {
            case AnnotationEditorMode.CreateNote:
                await doc.AddTextNoteAsync(pageIndex, pdfPt.X, pdfPt.Y, color, contents);
                break;

            case AnnotationEditorMode.CreateFreeText:
                if (!string.IsNullOrWhiteSpace(contents))
                {
                    await doc.AddFreeTextAsync(pageIndex, pdfPt.X, pdfPt.Y, color, contents);
                }

                break;

            case AnnotationEditorMode.CreateMarkupWithComment:
                if (pendingSel is { HasContent: true })
                {
                    await doc.AddMarkupForSelectionAsync(pendingSel, markupSubtype, color, contents);
                }

                break;

            case AnnotationEditorMode.EditExisting:
                if (pageIndex >= 0 && annotIndex >= 0)
                {
                    await doc.UpdateAnnotationAsync(pageIndex, annotIndex, contents, canEditColor ? color : null);
                }

                break;
        }

        ReturnFocusToViewer();
    }

    private async void OnAnnotationEditorDeleteClick(object sender, RoutedEventArgs e)
    {
        DocumentViewModel? doc = ViewModel.CurrentDocument;
        int pageIndex = _annotEditorPageIndex;
        int annotIndex = _annotEditorAnnotIndex;
        AnnotationEditorFlyout?.Hide();

        if (doc is not null && pageIndex >= 0 && annotIndex >= 0)
        {
            await doc.DeleteAnnotationAsync(pageIndex, annotIndex);
        }

        ReturnFocusToViewer();
    }

    private void OnAnnotationEditorCopyClick(object sender, RoutedEventArgs e)
    {
        if (_annotEditorAnnotation is { } annot)
        {
            CopyAnnotationToClipboard(annot);
        }

        AnnotationEditorFlyout?.Hide();
        ReturnFocusToViewer();
    }

    private void OnAnnotationEditorCutClick(object sender, RoutedEventArgs e)
    {
        PdfAnnotationInfo? annot = _annotEditorAnnotation;
        AnnotationEditorFlyout?.Hide();
        if (annot is not null && ViewModel.CurrentDocument is { } doc)
        {
            _ = CutAnnotationToClipboardAsync(doc, annot);
        }

        ReturnFocusToViewer();
    }

    private void OnAnnotationEditorCloseClick(object sender, RoutedEventArgs e)
    {
        AnnotationEditorFlyout?.Hide();
        ReturnFocusToViewer();
    }

    private void OnAnnotationEditorFlyoutClosed(object? sender, object e)
    {
        bool wasProgrammatic = _annotEditorClosedProgrammatically;
        _annotEditorClosedProgrammatically = false;
        _annotEditorPendingSelection = null;
        _annotEditorAnnotation = null;
        bool isSpuriousTouchDismiss = _isLastPointerInputTouch &&
            (Environment.TickCount64 - _annotEditorOpenedTick) < 220;
        if (!wasProgrammatic && !isSpuriousTouchDismiss && ViewModel.CurrentDocument is { } doc)
        {
            doc.SelectedAnnotation = null;
        }

        UpdateTouchAnnotationResizeHandlesLayout();
    }

    private void HideAnnotationPopupsImmediate()
    {
        _annotationPopupOpenSeq++;
        HideTouchSelectionFlyout();

        try
        {
            _pendingAfterMouseSelectionClosed = null;
            if (MouseSelectionFlyout?.IsOpen == true)
            {
                MouseSelectionFlyout.Hide();
            }
        }
        catch
        {
        }

        try
        {
            if (ToolbarColorFlyout?.IsOpen == true)
            {
                ToolbarColorFlyout.Hide();
            }
        }
        catch
        {
        }

        try
        {
            if (TouchEmptyHoldCommandBarFlyout?.IsOpen == true)
            {
                TouchEmptyHoldCommandBarFlyout.Hide();
            }
        }
        catch
        {
        }
        finally
        {
            _isTouchEmptyHoldFlyoutOpen = false;
        }

        SetVisible(TouchQuickBarColorRow, false);
        if (TouchAnnotationQuickBarBorder is not null)
        {
            TouchAnnotationQuickBarBorder.Visibility = Visibility.Collapsed;
        }

        try
        {
            _pendingAfterQuickBarClosed = null;
            if (AnnotationQuickBarFlyout?.IsOpen == true)
            {
                _quickBarClosedProgrammatically = true;
                AnnotationQuickBarFlyout.Hide();
            }
        }
        catch
        {
        }

        try
        {
            if (AnnotationEditorFlyout?.IsOpen == true)
            {
                _annotEditorClosedProgrammatically = true;
                AnnotationEditorFlyout.Hide();
            }
        }
        catch
        {
        }

        CloseInlineTextEditorImmediate();
    }

    // =========================================================================
    // Inline direct-on-page text editor + coupled Acrylic formatting mini-bar
    // =========================================================================

    private static readonly float[] s_inlineTextFontSizes =
        [8f, 9f, 10f, 11f, 12f, 14f, 16f, 18f, 20f, 24f, 28f, 36f, 48f, 72f];

    private bool _isInlineTextEditorOpen;
    private bool _isCommittingInlineText;
    private bool _isSyncingInlineRichEdit;
    private int _inlineTextPageIndex = -1;
    private Vector2 _inlineTextAnchorPdfPt;
    private PdfAnnotationInfo? _inlineTextEditingAnnot;
    private PdfFreeTextStyle _inlineTextStyle = PdfFreeTextStyle.Default;
    private double _lastInlineRichEditFontSizeDip;
    private TextBlock? _inlineTextMeasureBlock;

    private string GetInlineEditorPlainText()
    {
        if (InlineTextEditorTextBox is null)
        {
            return string.Empty;
        }

        InlineTextEditorTextBox.Document.GetText(Microsoft.UI.Text.TextGetOptions.UseLf, out string raw);
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (raw.EndsWith('\n') || raw.EndsWith('\r'))
        {
            return raw[..^1];
        }

        return raw;
    }

    private void SetInlineEditorPlainText(string text)
    {
        if (InlineTextEditorTextBox is null)
        {
            return;
        }

        _isSyncingInlineRichEdit = true;
        try
        {
            InlineTextEditorTextBox.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, text ?? string.Empty);
        }
        finally
        {
            _isSyncingInlineRichEdit = false;
        }
    }

    private void GetInlineEditorDisplayColors(
        out Windows.UI.Color textColor,
        out Windows.UI.Color placeholderColor,
        out Windows.UI.Color paperColor)
    {
        PdfAnnotationColor c = _inlineTextStyle.Color;
        if (ViewModel.IsNightMode)
        {
            textColor = Windows.UI.Color.FromArgb(255, (byte)(255 - c.R), (byte)(255 - c.G), (byte)(255 - c.B));
            placeholderColor = Windows.UI.Color.FromArgb(140, 195, 195, 195);
            paperColor = Windows.UI.Color.FromArgb(255, 30, 30, 30);
        }
        else
        {
            textColor = Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
            placeholderColor = Windows.UI.Color.FromArgb(140, 102, 102, 102);
            paperColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }
    }

    private void ApplyInlineRichEditCharacterFormat(double fontSizeDip)
    {
        if (InlineTextEditorTextBox is null)
        {
            return;
        }

        _isSyncingInlineRichEdit = true;
        try
        {
            float sizePt = (float)Math.Clamp(fontSizeDip * 72.0 / 96.0, 4.5, 195.0);
            GetInlineEditorDisplayColors(out Windows.UI.Color textColor, out _, out _);

            void ConfigureChar(Microsoft.UI.Text.ITextCharacterFormat fmt)
            {
                fmt.Name = "Arial";
                fmt.FontStretch = Windows.UI.Text.FontStretch.Normal;
                fmt.Size = sizePt;
                fmt.Bold = _inlineTextStyle.IsBold ? Microsoft.UI.Text.FormatEffect.On : Microsoft.UI.Text.FormatEffect.Off;
                fmt.Italic = _inlineTextStyle.IsItalic ? Microsoft.UI.Text.FormatEffect.On : Microsoft.UI.Text.FormatEffect.Off;
                fmt.Underline = _inlineTextStyle.IsUnderline ? Microsoft.UI.Text.UnderlineType.Single : Microsoft.UI.Text.UnderlineType.None;
                fmt.ForegroundColor = textColor;
            }

            Microsoft.UI.Text.RichEditTextDocument doc = InlineTextEditorTextBox.Document;
            Microsoft.UI.Text.ITextCharacterFormat defChar = doc.GetDefaultCharacterFormat();
            ConfigureChar(defChar);
            doc.SetDefaultCharacterFormat(defChar);

            Microsoft.UI.Text.ITextParagraphFormat defPara = doc.GetDefaultParagraphFormat();
            defPara.SpaceBefore = 0f;
            defPara.SpaceAfter = 0f;
            doc.SetDefaultParagraphFormat(defPara);

            Microsoft.UI.Text.ITextRange allRange = doc.GetRange(0, Microsoft.UI.Text.TextConstants.MaxUnitCount);
            ConfigureChar(allRange.CharacterFormat);
            allRange.ParagraphFormat.SpaceBefore = 0f;
            allRange.ParagraphFormat.SpaceAfter = 0f;

            ConfigureChar(doc.Selection.CharacterFormat);
            _lastInlineRichEditFontSizeDip = fontSizeDip;
        }
        finally
        {
            _isSyncingInlineRichEdit = false;
        }
    }

    private void OpenInlineTextEditorForCreate(PdfPageCanvas canvas, Vector2 pdfPt)
    {
        if (canvas.PageInfo is not { } pageInfo || InlineTextEditorOverlay is null || InlineTextEditorTextBox is null)
        {
            return;
        }

        double pageRightPt = pageInfo.OriginXPt + pageInfo.WidthPt;
        double maxWidthPt = Math.Clamp(pageRightPt - pdfPt.X - 6.0, 110.0, Math.Max(110.0, pageInfo.WidthPt));

        _inlineTextEditingAnnot = null;
        _inlineTextPageIndex = pageInfo.Index;
        _inlineTextAnchorPdfPt = pdfPt;
        _inlineTextStyle = ViewModel.DefaultTextStyle with { FontFamily = "Arial", MaxWidthPt = maxWidthPt };

        SetInlineEditorPlainText(string.Empty);
        ShowInlineTextEditorCore(moveCaretToEnd: false);
    }

    private void OpenInlineTextEditorForExisting(PdfAnnotationInfo annot, PdfPageCanvas? canvas)
    {
        if (InlineTextEditorOverlay is null || InlineTextEditorTextBox is null || ViewModel.CurrentDocument is not { Pages.Count: > 0 } doc)
        {
            return;
        }

        int pageIndex = Math.Clamp(annot.PageIndex, 0, doc.Pages.Count - 1);
        PdfPageInfo pageInfo = canvas?.PageInfo ?? doc.Pages[pageIndex];
        double pageRightPt = pageInfo.OriginXPt + pageInfo.WidthPt;
        PdfFreeTextStyle baseStyle = annot.TextStyle ?? (ViewModel.DefaultTextStyle with
        {
            FontFamily = "Arial",
            Color = annot.Color
        });

        float marginPt = Math.Max(3.0f, baseStyle.FontSizePt * 0.12f);
        float anchorX = annot.Bounds.Left + marginPt;
        float anchorY = annot.Bounds.Top - marginPt;
        double defaultMaxW = Math.Clamp(pageRightPt - anchorX - 6.0, 110.0, Math.Max(110.0, pageInfo.WidthPt));

        _inlineTextEditingAnnot = annot;
        _inlineTextPageIndex = pageIndex;
        _inlineTextAnchorPdfPt = new Vector2(anchorX, anchorY);
        _inlineTextStyle = baseStyle with
        {
            FontFamily = "Arial",
            MaxWidthPt = Math.Max(defaultMaxW, baseStyle.MaxWidthPt)
        };

        SetInlineEditorPlainText(annot.Contents ?? string.Empty);
        ShowInlineTextEditorCore(moveCaretToEnd: true);
    }

    private void ShowInlineTextEditorCore(bool moveCaretToEnd)
    {
        if (InlineTextEditorOverlay is null || InlineTextEditorTextBox is null)
        {
            return;
        }

        _isInlineTextEditorOpen = true;
        _isCommittingInlineText = false;
        SyncTouchShapeDrawOverlayVisibility();
        SyncTouchInkDrawOverlayVisibility();
        ApplyInlineTextStyleToUi();
        InlineTextEditorOverlay.Visibility = Visibility.Visible;
        RepositionInlineTextEditor();

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            if (!_isInlineTextEditorOpen || InlineTextEditorTextBox is null)
            {
                return;
            }

            RepositionInlineTextEditor();
            _ = InlineTextEditorTextBox.Focus(FocusState.Programmatic);
            if (moveCaretToEnd)
            {
                int endPos = GetInlineEditorPlainText().Length;
                InlineTextEditorTextBox.Document.Selection.SetRange(endPos, endPos);
            }
        });
    }

    private void CloseInlineTextEditorImmediate()
    {
        _isInlineTextEditorOpen = false;
        _inlineTextEditingAnnot = null;
        _inlineTextPageIndex = -1;

        try
        {
            if (InlineTextColorFlyout?.IsOpen == true)
            {
                InlineTextColorFlyout.Hide();
            }
        }
        catch
        {
        }

        if (InlineTextEditorOverlay is not null)
        {
            InlineTextEditorOverlay.Visibility = Visibility.Collapsed;
        }

        SyncTouchShapeDrawOverlayVisibility();
        SyncTouchInkDrawOverlayVisibility();
        UpdateTouchAnnotationResizeHandlesLayout();
    }

    private void CancelInlineTextEditor()
    {
        CloseInlineTextEditorImmediate();
        ReturnFocusToViewer();
    }

    private void ApplyInlineTextStyleToUi()
    {
        if (InlineTextSizeLabel is not null)
        {
            InlineTextSizeLabel.Text = ((int)Math.Round(_inlineTextStyle.FontSizePt)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (InlineTextBoldToggle is not null)
        {
            InlineTextBoldToggle.IsChecked = _inlineTextStyle.IsBold;
        }

        if (InlineTextItalicToggle is not null)
        {
            InlineTextItalicToggle.IsChecked = _inlineTextStyle.IsItalic;
        }

        if (InlineTextUnderlineToggle is not null)
        {
            InlineTextUnderlineToggle.IsChecked = _inlineTextStyle.IsUnderline;
        }

        PdfAnnotationColor c = _inlineTextStyle.Color;
        if (InlineTextColorDot is not null)
        {
            InlineTextColorDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        }

        GetInlineEditorDisplayColors(
            out Windows.UI.Color displayColor,
            out Windows.UI.Color placeholderColor,
            out Windows.UI.Color paperColor);

        if (InlineTextEditorBoxContainer is not null)
        {
            InlineTextEditorBoxContainer.Opacity = _inlineTextEditingAnnot is not null ? 1.0 : 0.92;
        }

        if (InlineTextEditorMaskBorder is not null)
        {
            InlineTextEditorMaskBorder.Background = new SolidColorBrush(paperColor);
        }

        if (InlineTextEditorTextBox is not null)
        {
            InlineTextEditorTextBox.RequestedTheme = ViewModel.IsNightMode ? ElementTheme.Dark : ElementTheme.Light;
            InlineTextEditorTextBox.FontFamily = new FontFamily("Arial");
            InlineTextEditorTextBox.FontStretch = Windows.UI.Text.FontStretch.Normal;
            InlineTextEditorTextBox.IsTextScaleFactorEnabled = false;
            InlineTextEditorTextBox.FontWeight = _inlineTextStyle.IsBold
                ? Microsoft.UI.Text.FontWeights.Bold
                : Microsoft.UI.Text.FontWeights.Normal;
            InlineTextEditorTextBox.FontStyle = _inlineTextStyle.IsItalic
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal;

            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlBackground", paperColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlBackgroundPointerOver", paperColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlBackgroundFocused", paperColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlForeground", displayColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlForegroundPointerOver", displayColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlForegroundFocused", displayColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlPlaceholderForeground", placeholderColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlPlaceholderForegroundPointerOver", placeholderColor);
            SetResourceBrushColor(InlineTextEditorTextBox.Resources, "TextControlPlaceholderForegroundFocused", placeholderColor);

            if (InlineTextEditorTextBox.Background is SolidColorBrush bgBrush)
            {
                bgBrush.Color = paperColor;
            }
            else
            {
                InlineTextEditorTextBox.Background = new SolidColorBrush(paperColor);
            }

            if (InlineTextEditorTextBox.Foreground is SolidColorBrush fgBrush)
            {
                fgBrush.Color = displayColor;
            }
            else
            {
                InlineTextEditorTextBox.Foreground = new SolidColorBrush(displayColor);
            }

            double fontSizeDip = InlineTextEditorTextBox.FontSize > 1.0
                ? InlineTextEditorTextBox.FontSize
                : _inlineTextStyle.FontSizePt * PageCoordinateTransform.DipsPerPoint;
            if (_inlineTextPageIndex >= 0 && FindRealizedCanvasForPage(_inlineTextPageIndex) is { } realizedCanvas)
            {
                fontSizeDip = Math.Clamp(_inlineTextStyle.FontSizePt * ComputeDipsPerPoint(realizedCanvas), 6.0, 260.0);
                InlineTextEditorTextBox.FontSize = fontSizeDip;
            }

            ApplyInlineRichEditCharacterFormat(fontSizeDip);
        }
    }

    private static void SetResourceBrushColor(ResourceDictionary resources, string key, Windows.UI.Color color)
    {
        if (resources.TryGetValue(key, out object? existing) && existing is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private double ComputeDipsPerPoint(PdfPageCanvas canvas)
    {
        if (ScrollViewerContentRoot is null)
        {
            double fallbackZoom = Math.Max(0.25, ContinuousScrollViewer?.ZoomFactor ?? ViewModel.CurrentDocument?.ZoomFactor ?? 1.0);
            return PageCoordinateTransform.DipsPerPoint * fallbackZoom;
        }

        try
        {
            Microsoft.UI.Xaml.Media.GeneralTransform gt = canvas.TransformToVisual(ScrollViewerContentRoot);
            Point p0 = gt.TransformPoint(new Point(0.0, 0.0));
            Point p1 = gt.TransformPoint(new Point(72.0 * PageCoordinateTransform.DipsPerPoint, 0.0));
            double dx = p1.X - p0.X;
            double dy = p1.Y - p0.Y;
            double span = Math.Sqrt((dx * dx) + (dy * dy));
            if (double.IsFinite(span) && span > 4.0)
            {
                return span / 72.0;
            }
        }
        catch
        {
        }

        double zoom = Math.Max(0.25, ContinuousScrollViewer?.ZoomFactor ?? ViewModel.CurrentDocument?.ZoomFactor ?? 1.0);
        return PageCoordinateTransform.DipsPerPoint * zoom;
    }

    private double MeasureInlineEditorDesiredWidthDip(
        string text,
        string placeholderText,
        double fontSizeDip,
        bool isBold,
        bool isItalic,
        double minWidthDip,
        double maxWidthDip)
    {
        _inlineTextMeasureBlock ??= new TextBlock
        {
            FontFamily = new FontFamily("Arial"),
            FontStretch = Windows.UI.Text.FontStretch.Normal,
            IsTextScaleFactorEnabled = false,
            TextWrapping = TextWrapping.NoWrap
        };

        _inlineTextMeasureBlock.FontFamily = new FontFamily("Arial");
        _inlineTextMeasureBlock.FontStretch = Windows.UI.Text.FontStretch.Normal;
        _inlineTextMeasureBlock.IsTextScaleFactorEnabled = false;
        _inlineTextMeasureBlock.FontSize = fontSizeDip;

        double caretPad = Math.Clamp(fontSizeDip * 0.22, 10.0, 18.0);

        if (string.IsNullOrEmpty(text))
        {
            if (string.IsNullOrEmpty(placeholderText))
            {
                return minWidthDip;
            }

            _inlineTextMeasureBlock.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            _inlineTextMeasureBlock.FontStyle = Windows.UI.Text.FontStyle.Normal;
            _inlineTextMeasureBlock.Text = placeholderText;
            _inlineTextMeasureBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Clamp(_inlineTextMeasureBlock.DesiredSize.Width + caretPad, minWidthDip, maxWidthDip);
        }

        _inlineTextMeasureBlock.FontWeight = isBold
            ? Microsoft.UI.Text.FontWeights.Bold
            : Microsoft.UI.Text.FontWeights.Normal;
        _inlineTextMeasureBlock.FontStyle = isItalic
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;

        double maxTextW = 0.0;
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                continue;
            }

            _inlineTextMeasureBlock.Text = lines[i];
            _inlineTextMeasureBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            maxTextW = Math.Max(maxTextW, _inlineTextMeasureBlock.DesiredSize.Width);
        }

        return Math.Clamp(maxTextW + caretPad, minWidthDip, maxWidthDip);
    }

    private void RepositionInlineTextEditor()
    {
        if (!_isInlineTextEditorOpen ||
            ScrollViewerContentRoot is null ||
            InlineTextEditorBoxContainer is null ||
            InlineTextFormatBar is null ||
            InlineTextEditorTextBox is null ||
            _inlineTextPageIndex < 0)
        {
            return;
        }

        PdfPageCanvas? canvas = FindRealizedCanvasForPage(_inlineTextPageIndex);
        if (canvas?.PageInfo is not { } pageInfo)
        {
            return;
        }

        try
        {
            PageCoordinateTransform transform = PageCoordinateTransform.FromPage(pageInfo, canvas.RotationAngle);
            Vector2 pageDipPt = transform.ToPage(_inlineTextAnchorPdfPt);
            Point boxRootPt = canvas.TransformToVisual(ScrollViewerContentRoot)
                .TransformPoint(new Point(pageDipPt.X, pageDipPt.Y));

            Vector2 pageRightDipPt = transform.ToPage(new Vector2(
                (float)(pageInfo.OriginXPt + pageInfo.WidthPt),
                _inlineTextAnchorPdfPt.Y));
            Point rightRootPt = canvas.TransformToVisual(ScrollViewerContentRoot)
                .TransformPoint(new Point(pageRightDipPt.X, pageRightDipPt.Y));

            double dipsPerPt = ComputeDipsPerPoint(canvas);
            double fontSizeDip = Math.Clamp(_inlineTextStyle.FontSizePt * dipsPerPt, 6.0, 260.0);
            InlineTextEditorTextBox.FontSize = fontSizeDip;
            if (Math.Abs(_lastInlineRichEditFontSizeDip - fontSizeDip) > 0.05)
            {
                ApplyInlineRichEditCharacterFormat(fontSizeDip);
            }

            string currentText = GetInlineEditorPlainText();
            double maxBoxW = Math.Max(120.0, rightRootPt.X - boxRootPt.X - 6.0);
            double minBoxW = string.IsNullOrEmpty(currentText)
                ? Math.Min(maxBoxW, Math.Max(80.0, 64.0 * dipsPerPt))
                : Math.Min(maxBoxW, Math.Max(36.0, fontSizeDip * 0.95));
            double desiredW = MeasureInlineEditorDesiredWidthDip(
                currentText,
                InlineTextEditorTextBox.PlaceholderText ?? string.Empty,
                fontSizeDip,
                _inlineTextStyle.IsBold,
                _inlineTextStyle.IsItalic,
                minBoxW,
                maxBoxW);

            InlineTextEditorBoxContainer.Width = desiredW;
            InlineTextEditorBoxContainer.MaxWidth = maxBoxW;
            InlineTextEditorTextBox.Width = desiredW;

            double rootW = Math.Max(120.0, ScrollViewerContentRoot.ActualWidth);
            double rootH = Math.Max(120.0, ScrollViewerContentRoot.ActualHeight);
            double boxLeft = Math.Round(Math.Clamp(boxRootPt.X, 4.0, Math.Max(4.0, rootW - 48.0)));
            double boxTop = Math.Round(Math.Clamp(boxRootPt.Y, 4.0, Math.Max(4.0, rootH - 36.0)));

            Canvas.SetLeft(InlineTextEditorBoxContainer, boxLeft);
            Canvas.SetTop(InlineTextEditorBoxContainer, boxTop);

            double boxH = InlineTextEditorBoxContainer.ActualHeight > 1.0
                ? InlineTextEditorBoxContainer.ActualHeight
                : Math.Max(24.0, fontSizeDip * 1.25);
            double barW = InlineTextFormatBar.ActualWidth > 1.0 ? InlineTextFormatBar.ActualWidth : 260.0;
            double barH = InlineTextFormatBar.ActualHeight > 1.0 ? InlineTextFormatBar.ActualHeight : 40.0;

            double barY = boxTop - barH - 12.0;
            if (barY < 8.0)
            {
                barY = boxTop + boxH + 12.0;
            }

            double barX = Math.Round(Math.Clamp(boxLeft, 8.0, Math.Max(8.0, rootW - barW - 8.0)));
            barY = Math.Round(Math.Clamp(barY, 8.0, Math.Max(8.0, rootH - barH - 8.0)));

            Canvas.SetLeft(InlineTextFormatBar, barX);
            Canvas.SetTop(InlineTextFormatBar, barY);
        }
        catch
        {
        }
    }

    private void OnInlineTextSizeFlyoutOpening(object? sender, object e)
    {
        if (InlineTextSizeMenuFlyout is null)
        {
            return;
        }

        InlineTextSizeMenuFlyout.Items.Clear();
        foreach (float sizePt in s_inlineTextFontSizes)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = ((int)sizePt).ToString(System.Globalization.CultureInfo.InvariantCulture),
                IsChecked = Math.Abs(_inlineTextStyle.FontSizePt - sizePt) < 0.25f
            };
            item.Click += (_, _) =>
            {
                _inlineTextStyle = _inlineTextStyle with { FontSizePt = sizePt };
                ApplyInlineTextStyleToUi();
                RepositionInlineTextEditor();
                RefocusInlineTextEditor();
            };
            InlineTextSizeMenuFlyout.Items.Add(item);
        }
    }

    private void OnInlineTextBoldClick(object sender, RoutedEventArgs e)
    {
        _inlineTextStyle = _inlineTextStyle with { IsBold = InlineTextBoldToggle?.IsChecked == true };
        ApplyInlineTextStyleToUi();
        RepositionInlineTextEditor();
        RefocusInlineTextEditor();
    }

    private void OnInlineTextItalicClick(object sender, RoutedEventArgs e)
    {
        _inlineTextStyle = _inlineTextStyle with { IsItalic = InlineTextItalicToggle?.IsChecked == true };
        ApplyInlineTextStyleToUi();
        RepositionInlineTextEditor();
        RefocusInlineTextEditor();
    }

    private void OnInlineTextUnderlineClick(object sender, RoutedEventArgs e)
    {
        _inlineTextStyle = _inlineTextStyle with { IsUnderline = InlineTextUnderlineToggle?.IsChecked == true };
        ApplyInlineTextStyleToUi();
        RepositionInlineTextEditor();
        RefocusInlineTextEditor();
    }

    private void OnInlineTextColorSwatchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            InlineTextColorFlyout?.Hide();
        }
        catch
        {
        }

        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            string[] rgb = tag.Split(',');
            if (rgb.Length == 3 &&
                byte.TryParse(rgb[0], out byte r) &&
                byte.TryParse(rgb[1], out byte g) &&
                byte.TryParse(rgb[2], out byte b))
            {
                _inlineTextStyle = _inlineTextStyle with { Color = new PdfAnnotationColor(r, g, b, 255) };
                ApplyInlineTextStyleToUi();
            }
        }

        RefocusInlineTextEditor();
    }

    private void OnInlineTextDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_inlineTextEditingAnnot is { } existing && ViewModel.CurrentDocument is { } doc)
        {
            CloseInlineTextEditorImmediate();
            _ = doc.DeleteAnnotationAsync(existing.PageIndex, existing.AnnotIndex);
            ReturnFocusToViewer();
            return;
        }

        CancelInlineTextEditor();
    }

    private void OnInlineTextConfirmClick(object sender, RoutedEventArgs e)
        => _ = CommitInlineTextEditorAsync();

    private void OnInlineTextEditorBackdropPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        _ = CommitInlineTextEditorAsync();
    }

    private void OnInlineTextEditorTextChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingInlineRichEdit || !_isInlineTextEditorOpen)
        {
            return;
        }

        RepositionInlineTextEditor();
    }

    private async void OnInlineTextEditorPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        if (!_isInlineTextEditorOpen || InlineTextEditorTextBox is null)
        {
            return;
        }

        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
            {
                return;
            }

            string plainText = await content.GetTextAsync();
            if (string.IsNullOrEmpty(plainText) || !_isInlineTextEditorOpen || InlineTextEditorTextBox is null)
            {
                return;
            }

            InlineTextEditorTextBox.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.None, plainText);
            ApplyInlineTextStyleToUi();
            RepositionInlineTextEditor();
        }
        catch
        {
        }
    }

    private void OnInlineTextEditorBoxSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInlineTextEditorOpen)
        {
            RepositionInlineTextEditor();
        }
    }

    private void OnInlineTextFormatBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isInlineTextEditorOpen)
        {
            RepositionInlineTextEditor();
        }
    }

    private void OnInlineTextEditorPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isInlineTextEditorOpen)
        {
            return;
        }

        bool isCtrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CancelInlineTextEditor();
            return;
        }

        if (isCtrl && e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = CommitInlineTextEditorAsync();
            return;
        }

        if (isCtrl && e.Key == Windows.System.VirtualKey.B)
        {
            e.Handled = true;
            _inlineTextStyle = _inlineTextStyle with { IsBold = !_inlineTextStyle.IsBold };
            ApplyInlineTextStyleToUi();
            RepositionInlineTextEditor();
            return;
        }

        if (isCtrl && e.Key == Windows.System.VirtualKey.I)
        {
            e.Handled = true;
            _inlineTextStyle = _inlineTextStyle with { IsItalic = !_inlineTextStyle.IsItalic };
            ApplyInlineTextStyleToUi();
            RepositionInlineTextEditor();
            return;
        }

        if (isCtrl && e.Key == Windows.System.VirtualKey.U)
        {
            e.Handled = true;
            _inlineTextStyle = _inlineTextStyle with { IsUnderline = !_inlineTextStyle.IsUnderline };
            ApplyInlineTextStyleToUi();
            RepositionInlineTextEditor();
        }
    }

    private void RefocusInlineTextEditor()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isInlineTextEditorOpen && InlineTextEditorTextBox is not null)
            {
                _ = InlineTextEditorTextBox.Focus(FocusState.Programmatic);
            }
        });
    }

    private async Task CommitInlineTextEditorAsync()
    {
        if (!_isInlineTextEditorOpen || _isCommittingInlineText)
        {
            return;
        }

        _isCommittingInlineText = true;
        try
        {
            DocumentViewModel? doc = ViewModel.CurrentDocument;
            int pageIndex = _inlineTextPageIndex;
            Vector2 anchorPt = _inlineTextAnchorPdfPt;
            PdfAnnotationInfo? editingAnnot = _inlineTextEditingAnnot;
            PdfFreeTextStyle style = _inlineTextStyle;
            string rawText = GetInlineEditorPlainText();
            string text = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();

            CloseInlineTextEditorImmediate();
            ReturnFocusToViewer();

            if (doc is null || pageIndex < 0 || pageIndex >= doc.Pages.Count)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                if (editingAnnot is not null)
                {
                    await doc.DeleteAnnotationAsync(editingAnnot.PageIndex, editingAnnot.AnnotIndex);
                }

                return;
            }

            PdfPageInfo pageInfo = doc.Pages[pageIndex];
            double pageRightPt = pageInfo.OriginXPt + pageInfo.WidthPt;
            double maxWidthPt = Math.Clamp(pageRightPt - anchorPt.X - 6.0, 110.0, Math.Max(110.0, pageInfo.WidthPt));
            style = style with { FontFamily = "Arial", MaxWidthPt = maxWidthPt };

            ViewModel.DefaultTextStyle = style;

            if (editingAnnot is not null)
            {
                _ = await doc.UpdateAnnotationAsync(
                    editingAnnot.PageIndex,
                    editingAnnot.AnnotIndex,
                    text,
                    style.Color,
                    newTextStyle: style);
            }
            else
            {
                _ = await doc.AddFreeTextAsync(
                    pageIndex,
                    anchorPt.X,
                    anchorPt.Y,
                    style.Color,
                    text,
                    maxWidthPt: style.MaxWidthPt,
                    style: style);
            }
        }
        finally
        {
            _isCommittingInlineText = false;
        }
    }

    private static string BuildAnnotationMetadataLine(PdfAnnotationInfo annot)
    {
        string dateText = annot.ModifiedDate is { } dt
            ? dt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(annot.Author) && !string.IsNullOrWhiteSpace(dateText))
        {
            return $"{annot.Author} • {dateText}";
        }

        if (!string.IsNullOrWhiteSpace(annot.Author))
        {
            return annot.Author;
        }

        return dateText;
    }

}
