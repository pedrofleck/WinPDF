using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPDF.Helpers;
using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// File-backed implementation of <see cref="ISignatureStoreService"/> storing signature PNGs
/// under <c>%LocalAppData%\WinPDF\Signatures</c>.
/// </summary>
public sealed class SignatureStoreService : ISignatureStoreService
{
    private const long MaxImageFileBytes = 20L * 1024 * 1024;
    private const uint MaxDecodedDimension = 1600;
    private readonly string _signaturesDir;

    /// <summary>
    /// Initializes a new instance of the <see cref="SignatureStoreService"/> class.
    /// </summary>
    public SignatureStoreService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _signaturesDir = Path.Combine(appData, "WinPDF", "Signatures");
    }

    /// <inheritdoc/>
    public int MaxSignatures => 10;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SavedSignatureItem>> LoadSignaturesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_signaturesDir))
        {
            return Array.Empty<SavedSignatureItem>();
        }

        List<SavedSignatureItem> items = [];
        foreach (string filePath in Directory.EnumerateFiles(_signaturesDir, "*.png", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(filePath);
            if (!info.Exists || info.Length == 0)
            {
                continue;
            }

            string id = Path.GetFileNameWithoutExtension(filePath);
            bool hasTransparency;
            if (id.EndsWith("_o", StringComparison.Ordinal))
            {
                hasTransparency = false;
            }
            else if (id.EndsWith("_a", StringComparison.Ordinal))
            {
                hasTransparency = true;
            }
            else
            {
                hasTransparency = await DetectTransparencyFromFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            items.Add(new SavedSignatureItem(id, filePath, new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero), hasTransparency));
        }

        items.Sort((a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        return items;
    }

    /// <inheritdoc/>
    public async Task<SavedSignatureItem> SaveSignaturePngAsync(byte[] pngBytes, bool hasTransparency, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_signaturesDir);
        string[] existing = Directory.GetFiles(_signaturesDir, "*.png", SearchOption.TopDirectoryOnly);
        if (existing.Length >= MaxSignatures)
        {
            throw new InvalidOperationException(
                string.Format(ResourceStrings.Get(ResourceKeys.SignatureDialogLimitReached), MaxSignatures));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string suffix = hasTransparency ? "a" : "o";
        string id = $"sig_{now:yyyyMMddHHmmssfff}_{Guid.NewGuid().ToString("N")[..8]}_{suffix}";
        string filePath = Path.Combine(_signaturesDir, $"{id}.png");
        await File.WriteAllBytesAsync(filePath, pngBytes, cancellationToken).ConfigureAwait(false);
        return new SavedSignatureItem(id, filePath, now, hasTransparency);
    }

    private static async Task<bool> DetectTransparencyFromFileAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            using IRandomAccessStream stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            uint srcW = decoder.PixelWidth;
            uint srcH = decoder.PixelHeight;
            if (srcW == 0 || srcH == 0)
            {
                return true;
            }

            const uint MaxThumbDim = 96;
            uint dstW = srcW;
            uint dstH = srcH;
            BitmapTransform transform = new();
            if (Math.Max(srcW, srcH) > MaxThumbDim)
            {
                double scale = MaxThumbDim / (double)Math.Max(srcW, srcH);
                dstW = Math.Max(1u, (uint)Math.Round(srcW * scale));
                dstH = Math.Max(1u, (uint)Math.Round(srcH * scale));
                transform.ScaledWidth = dstW;
                transform.ScaledHeight = dstH;
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            return DetectHasTransparentBackground(pixelData.DetachPixelData(), (int)dstW, (int)dstH);
        }
        catch
        {
            return true;
        }
    }

    /// <inheritdoc/>
    public Task DeleteSignatureAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.CompletedTask;
        }

        string safeName = Path.GetFileName(id);
        string filePath = Path.Combine(_signaturesDir, $"{safeName}.png");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<(PdfCapturedImage Image, bool HasTransparency)> DecodeImageForSignatureAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));
        }

        cancellationToken.ThrowIfCancellationRequested();
        FileInfo fileInfo = new(filePath);
        if (fileInfo.Exists && fileInfo.Length > MaxImageFileBytes)
        {
            throw new InvalidOperationException(ResourceStrings.Get(ResourceKeys.InsertImageTooLarge));
        }

        StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
        using IRandomAccessStream stream = await file.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        uint srcW = decoder.PixelWidth;
        uint srcH = decoder.PixelHeight;
        if (srcW == 0 || srcH == 0)
        {
            throw new InvalidOperationException("Selected image has invalid dimensions.");
        }

        uint dstW = srcW;
        uint dstH = srcH;
        BitmapTransform transform = new();
        if (Math.Max(srcW, srcH) > MaxDecodedDimension)
        {
            double scale = MaxDecodedDimension / (double)Math.Max(srcW, srcH);
            dstW = Math.Max(1u, (uint)Math.Round(srcW * scale));
            dstH = Math.Max(1u, (uint)Math.Round(srcH * scale));
            transform.ScaledWidth = dstW;
            transform.ScaledHeight = dstH;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        byte[] pixels = pixelData.DetachPixelData();
        bool hasTransparency = DetectHasTransparentBackground(pixels, (int)dstW, (int)dstH);
        PdfCapturedImage captured = new((int)dstW, (int)dstH, pixels, PdfAnnotationInfo.SignatureStampSubject);
        return (captured, hasTransparency);
    }

    /// <inheritdoc/>
    public async Task<byte[]> EncodeBgraToPngAsync(
        byte[] straightBgraPixels,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(straightBgraPixels);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            (uint)width,
            (uint)height,
            96.0,
            96.0,
            straightBgraPixels);
        await encoder.FlushAsync();

        stream.Seek(0);
        byte[] bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        return bytes;
    }

    private static bool DetectHasTransparentBackground(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return false;
        }

        const byte alphaThreshold = 245;
        int totalPixels = width * height;
        int transparentTotal = 0;

        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] < alphaThreshold)
            {
                transparentTotal++;
            }
        }

        if (transparentTotal * 200 >= totalPixels) // >= 0.5% transparent pixels overall
        {
            return true;
        }

        int borderPixels = 0;
        int transparentBorder = 0;

        for (int x = 0; x < width; x++)
        {
            borderPixels++;
            if (bgra[(x * 4) + 3] < alphaThreshold)
            {
                transparentBorder++;
            }

            if (height > 1)
            {
                borderPixels++;
                int bottomOffset = (((height - 1) * width) + x) * 4;
                if (bgra[bottomOffset + 3] < alphaThreshold)
                {
                    transparentBorder++;
                }
            }
        }

        for (int y = 1; y < height - 1; y++)
        {
            borderPixels++;
            if (bgra[(y * width * 4) + 3] < alphaThreshold)
            {
                transparentBorder++;
            }

            if (width > 1)
            {
                borderPixels++;
                int rightOffset = ((y * width) + (width - 1)) * 4;
                if (bgra[rightOffset + 3] < alphaThreshold)
                {
                    transparentBorder++;
                }
            }
        }

        return borderPixels > 0 && (transparentBorder * 10 >= borderPixels); // >= 10% transparent perimeter
    }
}
