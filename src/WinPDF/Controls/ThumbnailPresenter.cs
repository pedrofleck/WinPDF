using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinPDF.Models;
using WinPDF.Rendering;
using WinPDF.Services;
using WinPDF.ViewModels;

namespace WinPDF.Controls;

/// <summary>
/// Displays a rendered PDF thumbnail with real aspect ratio and zero-LOH memory recycling.
/// </summary>
public sealed class ThumbnailPresenter : Grid
{
    [ComImport]
    [Guid("905a0fef-bc53-11df-8c49-001e4fc686da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess
    {
        unsafe byte* Buffer { get; }
    }

    /// <summary>
    /// Identifies the <see cref="Item"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ItemProperty =
        DependencyProperty.Register(
            nameof(Item),
            typeof(ThumbnailItemViewModel),
            typeof(ThumbnailPresenter),
            new PropertyMetadata(null, OnItemPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="PageInfo"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty PageInfoProperty =
        DependencyProperty.Register(
            nameof(PageInfo),
            typeof(PdfPageInfo),
            typeof(ThumbnailPresenter),
            new PropertyMetadata(null, OnPageInfoPropertyChanged));

    /// <summary>
    /// Identifies the <see cref="IsNightMode"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsNightModeProperty =
        DependencyProperty.Register(
            nameof(IsNightMode),
            typeof(bool),
            typeof(ThumbnailPresenter),
            new PropertyMetadata(false, OnIsNightModePropertyChanged));

    private readonly Image _image;
    private ThumbnailItemViewModel? _observedItem;
    private WriteableBitmap? _reusableBitmap;

    /// <summary>
    /// Gets or sets a value indicating whether night mode color inversion is enabled.
    /// </summary>
    public bool IsNightMode
    {
        get => (bool)GetValue(IsNightModeProperty);
        set => SetValue(IsNightModeProperty, value);
    }

    private static void OnIsNightModePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            if (presenter.DispatcherQueue.HasThreadAccess)
            {
                presenter.UpdateImage();
            }
            else
            {
                presenter.DispatcherQueue.TryEnqueue(presenter.UpdateImage);
            }
        }
    }

    /// <summary>
    /// Gets or sets the thumbnail view model to display.
    /// </summary>
    public ThumbnailItemViewModel? Item
    {
        get => (ThumbnailItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    /// <summary>
    /// Gets or sets optional page metadata for real aspect ratio calculation.
    /// </summary>
    public PdfPageInfo? PageInfo
    {
        get => (PdfPageInfo?)GetValue(PageInfoProperty);
        set => SetValue(PageInfoProperty, value);
    }

    /// <summary>
    /// Gets or sets a value that describes how the thumbnail image should be stretched to fill the destination rectangle.
    /// </summary>
    public Stretch Stretch
    {
        get => _image.Stretch;
        set => _image.Stretch = value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailPresenter"/> class.
    /// </summary>
    public ThumbnailPresenter()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Stretch.Fill,
        };
        Children.Add(_image);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToItem(Item);
        UpdateDimensions();
        UpdateImage();
        if (Item is { } item && (item.Image is null || item.Image.IsDisposed))
        {
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            item.NotifyRealized(scale);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromItem();
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
    }

    private static void OnItemPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            var newItem = e.NewValue as ThumbnailItemViewModel;
            presenter.SubscribeToItem(newItem);
            presenter.UpdateDimensions();
            presenter.UpdateImage();
            if (presenter.IsLoaded && newItem is not null && (newItem.Image is null || newItem.Image.IsDisposed))
            {
                double scale = presenter.XamlRoot?.RasterizationScale ?? 1.0;
                newItem.NotifyRealized(scale);
            }
        }
    }

    private static void OnPageInfoPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            presenter.UpdateDimensions();
            presenter.UpdateImage();
        }
    }

    private void SubscribeToItem(ThumbnailItemViewModel? newItem)
    {
        if (ReferenceEquals(_observedItem, newItem))
        {
            return;
        }

        UnsubscribeFromItem();

        if (newItem is not null)
        {
            _observedItem = newItem;
            _observedItem.PropertyChanged += OnObservedItemPropertyChanged;
        }
    }

    private void UnsubscribeFromItem()
    {
        if (_observedItem is not null)
        {
            _observedItem.PropertyChanged -= OnObservedItemPropertyChanged;
            _observedItem = null;
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
        }
    }

    private void OnObservedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThumbnailItemViewModel.Image))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateImage();
            }
            else
            {
                DispatcherQueue.TryEnqueue(UpdateImage);
            }
        }
        else if (e.PropertyName == nameof(ThumbnailItemViewModel.IsNightMode))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateImage();
            }
            else
            {
                DispatcherQueue.TryEnqueue(UpdateImage);
            }
        }
        else if (e.PropertyName is nameof(ThumbnailItemViewModel.WidthDip) or nameof(ThumbnailItemViewModel.HeightDip))
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateDimensions();
            }
            else
            {
                DispatcherQueue.TryEnqueue(UpdateDimensions);
            }
        }
    }

    /// <summary>
    /// Synchronizes the presenter and its ancestor card border to the current <see cref="Item"/> aspect-ratio dimensions.
    /// </summary>
    public void SyncCardDimensions() => UpdateDimensions();

    private void UpdateDimensions()
    {
        if (Item is not null)
        {
            double w = Item.WidthDip;
            double h = Item.HeightDip;
            if (double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0)
            {
                if (Parent is FrameworkElement parentGrid && parentGrid.Parent is Border cardBorder)
                {
                    cardBorder.Width = w;
                    cardBorder.Height = h;
                    Width = double.NaN;
                    Height = double.NaN;
                    _image.Width = double.NaN;
                    _image.Height = double.NaN;
                }
                else
                {
                    Width = w;
                    Height = h;
                    _image.Width = w;
                    _image.Height = h;
                }
            }
            else
            {
                Width = double.NaN;
                Height = double.NaN;
                _image.Width = double.NaN;
                _image.Height = double.NaN;
            }
        }
        else if (PageInfo is not null)
        {
            double aspect = DocumentViewModel.GetPageDisplayAspectRatio(PageInfo);
            (double boxW, double boxH) = DocumentViewModel.ComputeThumbnailBox(aspect, 108.0, 156.0);
            Width = boxW;
            Height = boxH;
            _image.Width = boxW;
            _image.Height = boxH;
        }
    }

    private void UpdateImage()
    {
        var entry = Item?.Image;
        if (entry is null || entry.IsDisposed || entry.ByteLength <= 0 || entry.Width <= 0 || entry.Height <= 0)
        {
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            return;
        }

        WriteableBitmap? bmp = PopulateWriteableBitmap(entry);
        if (bmp is not null)
        {
            _image.Source = bmp;
            _image.Visibility = Visibility.Visible;
        }
    }

    private unsafe WriteableBitmap? PopulateWriteableBitmap(PdfBitmapEntry entry)
    {
        try
        {
            WriteableBitmap bitmap = (_reusableBitmap is not null &&
                                      _reusableBitmap.PixelWidth == entry.Width &&
                                      _reusableBitmap.PixelHeight == entry.Height)
                ? _reusableBitmap
                : (_reusableBitmap = new WriteableBitmap(entry.Width, entry.Height));

            bool isNight = Item?.IsNightMode ?? IsNightMode;

            if (bitmap.PixelBuffer is IBufferByteAccess byteAccess)
            {
                byte* dest = byteAccess.Buffer;
                if (entry.Pixels is { Length: > 0 })
                {
                    fixed (byte* src = entry.Pixels)
                    {
                        CopyPixels(src, dest, entry.ByteLength, entry.Stride, entry.Height, isNight);
                    }
                }
                else if (entry.Bitmap is { IsDisposed: false } lease)
                {
                    byte* src = (byte*)lease.Scan0;
                    CopyPixels(src, dest, lease.SizeInBytes, lease.Stride, lease.Height, isNight);
                }
            }
            else
            {
                using Stream stream = bitmap.PixelBuffer.AsStream();
                if (entry.Pixels is { Length: > 0 })
                {
                    if (isNight)
                    {
                        byte[] temp = new byte[entry.ByteLength];
                        fixed (byte* s = entry.Pixels, d = temp)
                        {
                            CopyPixels(s, d, entry.ByteLength, entry.Stride, entry.Height, true);
                        }
                        stream.Write(temp);
                    }
                    else
                    {
                        stream.Write(entry.Pixels, 0, entry.ByteLength);
                    }
                }
                else if (entry.Bitmap is { IsDisposed: false } lease)
                {
                    if (isNight)
                    {
                        int byteCount = (int)Math.Min(lease.SizeInBytes, (long)lease.Stride * lease.Height);
                        byte[] temp = new byte[byteCount];
                        fixed (byte* d = temp)
                        {
                            CopyPixels((byte*)lease.Scan0, d, byteCount, lease.Stride, lease.Height, true);
                        }
                        stream.Write(temp);
                    }
                    else
                    {
                        stream.Write(lease.AsSpan());
                    }
                }
            }

            bitmap.Invalidate();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static unsafe void CopyPixels(byte* src, byte* dest, long byteLength, int stride, int height, bool isNight)
    {
        long bytesToCopy = Math.Min(byteLength, (long)stride * height);
        if (!isNight)
        {
            Buffer.MemoryCopy(src, dest, bytesToCopy, bytesToCopy);
        }
        else
        {
            // Invert RGB channels while leaving Alpha (A) 100% untouched:
            // In BGRA 32-bit little-endian words, (pixel ^ 0x00FFFFFF) inverts B, G, and R, leaving A intact.
            uint* pSrc = (uint*)src;
            uint* pDest = (uint*)dest;
            long pixelCount = bytesToCopy / 4;
            for (long i = 0; i < pixelCount; i++)
            {
                pDest[i] = pSrc[i] ^ 0x00FFFFFF;
            }
        }
    }
}
