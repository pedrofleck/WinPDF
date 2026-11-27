using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;
using WinPDF.Helpers;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;

namespace WinPDF.Views;

/// <summary>
/// Presentation item for a saved signature card in <see cref="SignaturePickerDialog"/>.
/// </summary>
public sealed class SignatureCardItem
{
    /// <summary>Gets the underlying persisted signature record.</summary>
    public required SavedSignatureItem Item { get; init; }

    /// <summary>Gets the decoded preview bitmap loaded from memory.</summary>
    public BitmapImage? Preview { get; init; }

    /// <summary>Gets whether the yellow non-transparent background warning badge is visible.</summary>
    public Visibility OpaqueBadgeVisibility { get; init; }
}

/// <summary>
/// Modal Fluent 2 Acrylic dialog for managing, drawing, uploading, and selecting
/// non-digital signatures to place onto a PDF page.
/// </summary>
public sealed partial class SignaturePickerDialog : ContentDialog
{
    private const float BaseSignatureStrokeWidthDip = 2.6f;
    private const float MinPointDistanceDip = 1.2f;

    private static readonly Color InkBlack = Color.FromArgb(255, 20, 20, 20);
    private static readonly Color InkBlue = Color.FromArgb(255, 26, 75, 156);
    private static readonly Color InkRed = Color.FromArgb(255, 198, 40, 40);

    private readonly ISignatureStoreService _signatureStore;
    private readonly IFilePickerService _filePicker;
    private readonly ObservableCollection<SignatureCardItem> _cards = [];
    private readonly List<DrawnSignatureStroke> _strokes = [];

    private DrawnSignatureStroke? _activeStroke;
    private uint? _activePointerId;
    private Color _currentInkColor = InkBlack;
    private PdfCapturedImage? _pendingOpaqueImage;
    private bool _isBusy;

    private sealed class DrawnSignatureStroke
    {
        public List<PdfInkSample> Samples { get; } = [];
        public Color Color { get; init; }
        public bool HasPressureVariation { get; init; }
    }

    /// <summary>
    /// Gets the signature selected by the user to place on the document, or <see langword="null"/> if cancelled/closed.
    /// </summary>
    public SavedSignatureItem? SelectedSignature { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SignaturePickerDialog"/> class.
    /// </summary>
    /// <param name="signatureStore">Local signature persistence service.</param>
    /// <param name="filePicker">File picker service for uploading signature images.</param>
    public SignaturePickerDialog(ISignatureStoreService signatureStore, IFilePickerService filePicker)
    {
        _signatureStore = signatureStore;
        _filePicker = filePicker;
        InitializeComponent();
        SignaturesGridView.ItemsSource = _cards;
        ApplyLocalizedStrings();
        UpdateDrawColorSelectionVisuals();
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    private void ApplyLocalizedStrings()
    {
        GalleryTitleText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogTitle);
        SelectHintText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogSelectHint);
        EmptyStateText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogEmptyText);
        DrawSignatureButtonText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogDrawButton);
        AddImageButtonText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogAddImageButton);
        CloseGalleryButton.Content = ResourceStrings.Get(ResourceKeys.SignatureDialogCloseButton);
        OpaqueWarningTitleText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogOpaqueWarningTitle);
        OpaqueWarningMessageText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogOpaqueWarningMessage);
        OpaqueCancelButton.Content = ResourceStrings.Get(ResourceKeys.SignatureDialogCancelButton);
        OpaqueAddAnywayButton.Content = ResourceStrings.Get(ResourceKeys.SignatureDialogAddAnywayButton);
        DrawTitleText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogDrawButton);
        DrawHintText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogDrawHint);
        ClearPadButtonText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogClearButton);
        BackToGalleryButton.Content = ResourceStrings.Get(ResourceKeys.SignatureDialogBackButton);
        SaveSignatureButtonText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogSaveButton);

        ToolTipService.SetToolTip(ColorBlackButton, ResourceStrings.Get(ResourceKeys.SignatureDialogColorBlack));
        ToolTipService.SetToolTip(ColorBlueButton, ResourceStrings.Get(ResourceKeys.SignatureDialogColorBlue));
        ToolTipService.SetToolTip(ColorRedButton, ResourceStrings.Get(ResourceKeys.SignatureDialogColorRed));
    }

    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += OnXamlRootChanged;
        }

        UpdateGridViewMaxHeight();
        await ReloadSignaturesAsync();
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
        }

        _strokes.Clear();
        _activeStroke = null;
        _activePointerId = null;
        _pendingOpaqueImage = null;
        SignatureDrawCanvas.RemoveFromVisualTree();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        UpdateGridViewMaxHeight();
    }

    private void UpdateGridViewMaxHeight()
    {
        if (XamlRoot is null)
        {
            return;
        }

        double reserved = OpaqueWarningPanel.Visibility == Visibility.Visible ? 430.0 : 250.0;
        SignaturesGridView.MaxHeight = Math.Max(140.0, XamlRoot.Size.Height - reserved);
    }

    private async Task ReloadSignaturesAsync()
    {
        IReadOnlyList<SavedSignatureItem> items = await _signatureStore.LoadSignaturesAsync();
        _cards.Clear();
        foreach (SavedSignatureItem item in items)
        {
            _cards.Add(await CreateCardAsync(item));
        }

        UpdateGalleryState();
    }

    private void UpdateGalleryState()
    {
        int count = _cards.Count;
        int max = _signatureStore.MaxSignatures;
        SignatureCountText.Text = string.Format(ResourceStrings.Get(ResourceKeys.SignatureDialogCountFormat), count, max);

        bool atLimit = count >= max;
        bool canAdd = !atLimit && !_isBusy;
        DrawSignatureButton.IsEnabled = canAdd;
        AddImageButton.IsEnabled = canAdd;

        string? limitTip = atLimit
            ? string.Format(ResourceStrings.Get(ResourceKeys.SignatureDialogLimitReached), max)
            : null;
        ToolTipService.SetToolTip(DrawSignatureButtonHost, limitTip);
        ToolTipService.SetToolTip(AddImageButtonHost, limitTip);

        EmptyStatePanel.Visibility = count == 0 && OpaqueWarningPanel.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;
        SignaturesGridView.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectHintText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGridViewMaxHeight();
    }

    private static async Task<SignatureCardItem> CreateCardAsync(SavedSignatureItem item)
    {
        BitmapImage? bmp = await LoadBitmapFromBytesAsync(item.FilePath, decodePixelHeight: 160);
        return new SignatureCardItem
        {
            Item = item,
            Preview = bmp,
            OpaqueBadgeVisibility = item.HasTransparency ? Visibility.Collapsed : Visibility.Visible,
        };
    }

    private void OnSignaturesGridViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SignaturesGridView.ItemsPanelRoot is ItemsWrapGrid wrap && e.NewSize.Width > 20)
        {
            double targetWidth = Math.Floor(e.NewSize.Width / 2.0);
            if (Math.Abs(wrap.ItemWidth - targetWidth) >= 1.0)
            {
                wrap.ItemWidth = targetWidth;
            }
        }
    }

    private void OnSignatureCardClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SignatureCardItem card)
        {
            SelectedSignature = card.Item;
            Hide();
        }
    }

    private void OnOpaqueBadgeLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border badge)
        {
            ToolTipService.SetToolTip(badge, ResourceStrings.Get(ResourceKeys.SignatureDialogOpaqueBadgeToolTip));
        }
    }

    private void OnDeleteButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button deleteButton || deleteButton.Flyout is not null)
        {
            return;
        }

        ToolTipService.SetToolTip(deleteButton, ResourceStrings.Get(ResourceKeys.SignatureDialogDeleteToolTip));

        Flyout confirmFlyout = new()
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
        };

        StackPanel flyoutBody = new() { Spacing = 10, MaxWidth = 220 };
        flyoutBody.Children.Add(new TextBlock
        {
            Text = ResourceStrings.Get(ResourceKeys.SignatureDialogDeleteConfirmText),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });

        StackPanel flyoutActions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        Button cancelDeleteBtn = new()
        {
            Content = ResourceStrings.Get(ResourceKeys.SignatureDialogCancelButton),
        };
        cancelDeleteBtn.Click += (_, _) => confirmFlyout.Hide();

        Button confirmDeleteBtn = new()
        {
            Content = ResourceStrings.Get(ResourceKeys.SignatureDialogDeleteConfirmButton),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        confirmDeleteBtn.Click += async (_, _) =>
        {
            confirmFlyout.Hide();
            if (deleteButton.Tag is SignatureCardItem card)
            {
                await DeleteCardAsync(card);
            }
        };

        flyoutActions.Children.Add(cancelDeleteBtn);
        flyoutActions.Children.Add(confirmDeleteBtn);
        flyoutBody.Children.Add(flyoutActions);
        confirmFlyout.Content = flyoutBody;
        deleteButton.Flyout = confirmFlyout;
    }

    private async Task DeleteCardAsync(SignatureCardItem card)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        try
        {
            await _signatureStore.DeleteSignatureAsync(card.Item.Id);
            _cards.Remove(card);
        }
        finally
        {
            _isBusy = false;
            UpdateGalleryState();
        }
    }

    private static async Task<BitmapImage?> LoadBitmapFromBytesAsync(string filePath, int decodePixelHeight)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            byte[] bytes = await File.ReadAllBytesAsync(filePath);
            using InMemoryRandomAccessStream stream = new();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            BitmapImage bitmap = new()
            {
                DecodePixelHeight = decodePixelHeight,
            };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        SelectedSignature = null;
        Hide();
    }

    private void OnOpenDrawPadClick(object sender, RoutedEventArgs e)
    {
        ClearOpaqueWarning();
        _strokes.Clear();
        _activeStroke = null;
        _activePointerId = null;
        SaveSignatureButton.IsEnabled = false;
        GalleryPanel.Visibility = Visibility.Collapsed;
        DrawPanel.Visibility = Visibility.Visible;
        SignatureDrawCanvas.Invalidate();
    }

    private void OnBackToGalleryClick(object sender, RoutedEventArgs e)
    {
        _strokes.Clear();
        _activeStroke = null;
        _activePointerId = null;
        DrawPanel.Visibility = Visibility.Collapsed;
        GalleryPanel.Visibility = Visibility.Visible;
    }

    private async void OnAddImageClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        string? path = await _filePicker.PickOpenImageAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _isBusy = true;
        try
        {
            (PdfCapturedImage image, bool hasTransparency) = await _signatureStore.DecodeImageForSignatureAsync(path);
            if (hasTransparency)
            {
                byte[] pngBytes = await _signatureStore.EncodeBgraToPngAsync(image.BgraPixels, image.PixelWidth, image.PixelHeight);
                SavedSignatureItem saved = await _signatureStore.SaveSignaturePngAsync(pngBytes, hasTransparency: true);
                ClearOpaqueWarning();
                _cards.Insert(0, await CreateCardAsync(saved));
            }
            else
            {
                _pendingOpaqueImage = image;
                byte[] previewPng = await _signatureStore.EncodeBgraToPngAsync(image.BgraPixels, image.PixelWidth, image.PixelHeight);
                using InMemoryRandomAccessStream stream = new();
                await stream.WriteAsync(previewPng.AsBuffer());
                stream.Seek(0);

                BitmapImage bmp = new() { DecodePixelHeight = 140 };
                await bmp.SetSourceAsync(stream);
                OpaquePreviewImage.Source = bmp;
                OpaquePreviewContainer.Visibility = Visibility.Visible;
                OpaqueWarningPanel.Visibility = Visibility.Visible;
                EmptyStatePanel.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            OpaquePreviewContainer.Visibility = Visibility.Collapsed;
            OpaqueWarningTitleText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogTitle);
            OpaqueWarningMessageText.Text = ex.Message;
            OpaqueWarningPanel.Visibility = Visibility.Visible;
            OpaqueAddAnywayButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isBusy = false;
            UpdateGalleryState();
        }
    }

    private void OnOpaqueWarningCancelClick(object sender, RoutedEventArgs e)
    {
        ClearOpaqueWarning();
        UpdateGalleryState();
    }

    private async void OnOpaqueWarningAddAnywayClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _pendingOpaqueImage is not { } image)
        {
            return;
        }

        _isBusy = true;
        try
        {
            byte[] pngBytes = await _signatureStore.EncodeBgraToPngAsync(image.BgraPixels, image.PixelWidth, image.PixelHeight);
            SavedSignatureItem saved = await _signatureStore.SaveSignaturePngAsync(pngBytes, hasTransparency: false);
            ClearOpaqueWarning();
            _cards.Insert(0, await CreateCardAsync(saved));
        }
        finally
        {
            _isBusy = false;
            UpdateGalleryState();
        }
    }

    private void ClearOpaqueWarning()
    {
        _pendingOpaqueImage = null;
        OpaquePreviewImage.Source = null;
        OpaquePreviewContainer.Visibility = Visibility.Visible;
        OpaqueWarningPanel.Visibility = Visibility.Collapsed;
        OpaqueAddAnywayButton.Visibility = Visibility.Visible;
        OpaqueWarningTitleText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogOpaqueWarningTitle);
        OpaqueWarningMessageText.Text = ResourceStrings.Get(ResourceKeys.SignatureDialogOpaqueWarningMessage);
    }

    private void OnDrawColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag)
        {
            return;
        }

        _currentInkColor = tag switch
        {
            "Blue" => InkBlue,
            "Red" => InkRed,
            _ => InkBlack,
        };
        UpdateDrawColorSelectionVisuals();
    }

    private void UpdateDrawColorSelectionVisuals()
    {
        Brush selectedBorder = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        Brush normalBorder = new SolidColorBrush(Colors.Transparent);

        ColorBlackButton.BorderBrush = _currentInkColor == InkBlack ? selectedBorder : normalBorder;
        ColorBlueButton.BorderBrush = _currentInkColor == InkBlue ? selectedBorder : normalBorder;
        ColorRedButton.BorderBrush = _currentInkColor == InkRed ? selectedBorder : normalBorder;
    }

    private void OnClearPadClick(object sender, RoutedEventArgs e)
    {
        _strokes.Clear();
        _activeStroke = null;
        _activePointerId = null;
        SaveSignatureButton.IsEnabled = false;
        SignatureDrawCanvas.Invalidate();
    }

    private void OnDrawCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId is not null)
        {
            return;
        }

        PointerPoint pt = e.GetCurrentPoint(SignatureDrawCanvas);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _activePointerId = e.Pointer.PointerId;
        SignatureDrawCanvas.CapturePointer(e.Pointer);

        bool hasPressure = e.Pointer.PointerDeviceType == PointerDeviceType.Pen;
        float pressure = hasPressure ? Math.Clamp(pt.Properties.Pressure, 0.04f, 1.0f) : 0.5f;

        _activeStroke = new DrawnSignatureStroke
        {
            Color = _currentInkColor,
            HasPressureVariation = hasPressure,
        };
        _activeStroke.Samples.Add(new PdfInkSample((float)pt.Position.X, (float)pt.Position.Y, pressure));
        SignatureDrawCanvas.Invalidate();
        e.Handled = true;
    }

    private void OnDrawCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activeStroke is null || _activePointerId != e.Pointer.PointerId)
        {
            return;
        }

        IList<PointerPoint> intermediates = e.GetIntermediatePoints(SignatureDrawCanvas);
        bool added = false;
        bool hasPressure = _activeStroke.HasPressureVariation;

        for (int i = intermediates.Count - 1; i >= 0; i--)
        {
            PointerPoint pt = intermediates[i];
            float x = (float)pt.Position.X;
            float y = (float)pt.Position.Y;
            float pressure = hasPressure ? Math.Clamp(pt.Properties.Pressure, 0.04f, 1.0f) : 0.5f;

            if (_activeStroke.Samples.Count > 0)
            {
                PdfInkSample last = _activeStroke.Samples[^1];
                float dx = x - last.X;
                float dy = y - last.Y;
                if ((dx * dx) + (dy * dy) < MinPointDistanceDip * MinPointDistanceDip)
                {
                    continue;
                }
            }

            _activeStroke.Samples.Add(new PdfInkSample(x, y, pressure));
            added = true;
        }

        if (added)
        {
            SignatureDrawCanvas.Invalidate();
        }

        e.Handled = true;
    }

    private void OnDrawCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId)
        {
            return;
        }

        FinalizeActiveStroke();
        e.Handled = true;
    }

    private void OnDrawCanvasPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId)
        {
            return;
        }

        FinalizeActiveStroke();
        e.Handled = true;
    }

    private void OnDrawCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointerId != e.Pointer.PointerId)
        {
            return;
        }

        FinalizeActiveStroke();
    }

    private void FinalizeActiveStroke()
    {
        SignatureDrawCanvas.ReleasePointerCaptures();
        _activePointerId = null;

        if (_activeStroke is null)
        {
            return;
        }

        if (_activeStroke.Samples.Count > 0)
        {
            List<PdfInkSample> smoothed = InkStrokeProcessor.SmoothSamples(_activeStroke.Samples);
            List<PdfInkSample> simplified = InkStrokeProcessor.SimplifySamples(smoothed, 0.35f, _activeStroke.HasPressureVariation);
            DrawnSignatureStroke finalized = new()
            {
                Color = _activeStroke.Color,
                HasPressureVariation = _activeStroke.HasPressureVariation,
            };
            finalized.Samples.AddRange(simplified);
            _strokes.Add(finalized);
        }

        _activeStroke = null;
        SaveSignatureButton.IsEnabled = _strokes.Count > 0;
        SignatureDrawCanvas.Invalidate();
    }

    private void OnDrawCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        foreach (DrawnSignatureStroke stroke in _strokes)
        {
            InkStrokeProcessor.DrawWetInkStroke(
                ds,
                stroke.Samples,
                stroke.HasPressureVariation,
                BaseSignatureStrokeWidthDip,
                stroke.Color,
                0f,
                0f);
        }

        if (_activeStroke is not null)
        {
            InkStrokeProcessor.DrawWetInkStroke(
                ds,
                _activeStroke.Samples,
                _activeStroke.HasPressureVariation,
                BaseSignatureStrokeWidthDip,
                _activeStroke.Color,
                0f,
                0f);
        }
    }

    private async void OnSaveDrawnSignatureClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _strokes.Count == 0)
        {
            return;
        }

        _isBusy = true;
        SaveSignatureButton.IsEnabled = false;
        try
        {
            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            foreach (DrawnSignatureStroke stroke in _strokes)
            {
                foreach (PdfInkSample s in stroke.Samples)
                {
                    if (s.X < minX) { minX = s.X; }
                    if (s.Y < minY) { minY = s.Y; }
                    if (s.X > maxX) { maxX = s.X; }
                    if (s.Y > maxY) { maxY = s.Y; }
                }
            }

            if (minX > maxX || minY > maxY)
            {
                return;
            }

            const float paddingDip = 12f;
            float contentW = Math.Max(16f, (maxX - minX) + (paddingDip * 2f));
            float contentH = Math.Max(16f, (maxY - minY) + (paddingDip * 2f));

            const float targetScale = 3.0f;
            float maxSide = Math.Max(contentW, contentH);
            float scale = maxSide * targetScale > 1400f ? 1400f / maxSide : targetScale;

            int pixelW = Math.Max(16, (int)Math.Ceiling(contentW * scale));
            int pixelH = Math.Max(16, (int)Math.Ceiling(contentH * scale));
            float offsetX = -minX + paddingDip;
            float offsetY = -minY + paddingDip;

            CanvasDevice device = CanvasDevice.GetSharedDevice();
            byte[] bgraPixels;
            using (CanvasRenderTarget rt = new(device, pixelW, pixelH, 96f))
            {
                using (CanvasDrawingSession ds = rt.CreateDrawingSession())
                {
                    ds.Clear(Colors.Transparent);
                    foreach (DrawnSignatureStroke stroke in _strokes)
                    {
                        List<PdfInkSample> scaledSamples = new(stroke.Samples.Count);
                        foreach (PdfInkSample s in stroke.Samples)
                        {
                            scaledSamples.Add(new PdfInkSample((s.X + offsetX) * scale, (s.Y + offsetY) * scale, s.Pressure));
                        }

                        InkStrokeProcessor.DrawWetInkStroke(
                            ds,
                            scaledSamples,
                            stroke.HasPressureVariation,
                            BaseSignatureStrokeWidthDip * scale,
                            stroke.Color,
                            0f,
                            0f);
                    }
                }

                bgraPixels = rt.GetPixelBytes();
            }

            UnpremultiplyBgraInPlace(bgraPixels);
            byte[] pngBytes = await _signatureStore.EncodeBgraToPngAsync(bgraPixels, pixelW, pixelH);
            SavedSignatureItem saved = await _signatureStore.SaveSignaturePngAsync(pngBytes, hasTransparency: true);

            _strokes.Clear();
            _activeStroke = null;
            DrawPanel.Visibility = Visibility.Collapsed;
            GalleryPanel.Visibility = Visibility.Visible;
            _cards.Insert(0, await CreateCardAsync(saved));
        }
        finally
        {
            _isBusy = false;
            SaveSignatureButton.IsEnabled = _strokes.Count > 0;
            UpdateGalleryState();
        }
    }

    private static void UnpremultiplyBgraInPlace(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            if (a == 0)
            {
                bgra[i] = 0;
                bgra[i + 1] = 0;
                bgra[i + 2] = 0;
            }
            else if (a < 255)
            {
                int half = a >> 1;
                bgra[i] = (byte)Math.Min(255, ((bgra[i] * 255) + half) / a);
                bgra[i + 1] = (byte)Math.Min(255, ((bgra[i + 1] * 255) + half) / a);
                bgra[i + 2] = (byte)Math.Min(255, ((bgra[i + 2] * 255) + half) / a);
            }
        }
    }
}
