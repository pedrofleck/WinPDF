using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinPDF.Services;
using WinPDF.ViewModels;

namespace WinPDF.Controls;

/// <summary>
/// Displays a rendered PDF thumbnail without Large Object Heap allocations.
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

    private readonly Image _image;
    private ThumbnailItemViewModel? _observedItem;

    /// <summary>
    /// Gets or sets the thumbnail view model to display.
    /// </summary>
    public ThumbnailItemViewModel? Item
    {
        get => (ThumbnailItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
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
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        _image = new Image
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
        Children.Add(_image);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToItem(Item);
        UpdateImage();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromItem();
    }

    private static void OnItemPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ThumbnailPresenter presenter)
        {
            presenter.SubscribeToItem(e.NewValue as ThumbnailItemViewModel);
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
        }
    }

    private void OnObservedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThumbnailItemViewModel.Image))
        {
            DispatcherQueue.TryEnqueue(UpdateImage);
        }
    }

    private void UpdateImage()
    {
        var entry = Item?.Image;
        if (entry is null)
        {
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            return;
        }

        PdfBitmapLease lease = entry.Bitmap;
        if (lease.IsDisposed || lease.Width <= 0 || lease.Height <= 0)
        {
            _image.Source = null;
            _image.Visibility = Visibility.Collapsed;
            return;
        }

        _image.Source = CreateWriteableBitmap(lease);
        _image.Visibility = Visibility.Visible;
    }

    private static unsafe WriteableBitmap? CreateWriteableBitmap(PdfBitmapLease lease)
    {
        try
        {
            WriteableBitmap bitmap = new(lease.Width, lease.Height);
            if (bitmap.PixelBuffer is IBufferByteAccess byteAccess)
            {
                byte* dest = byteAccess.Buffer;
                byte* src = (byte*)lease.Scan0;
                long bytesToCopy = Math.Min(lease.SizeInBytes, (long)lease.Stride * lease.Height);
                Buffer.MemoryCopy(src, dest, bytesToCopy, bytesToCopy);
            }
            else
            {
                using Stream stream = bitmap.PixelBuffer.AsStream();
                stream.Write(lease.AsSpan());
            }

            bitmap.Invalidate();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
