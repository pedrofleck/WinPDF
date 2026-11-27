using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// Manages local persistence, decoding, transparency inspection, and PNG encoding
/// for reusable non-digital signatures.
/// </summary>
public interface ISignatureStoreService
{
    /// <summary>
    /// Gets the maximum number of saved signatures allowed in the signature gallery.
    /// </summary>
    int MaxSignatures { get; }

    /// <summary>
    /// Loads all saved signatures ordered from newest to oldest.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The list of saved signature items.</returns>
    Task<IReadOnlyList<SavedSignatureItem>> LoadSignaturesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves PNG-encoded signature bytes into the local signature store.
    /// </summary>
    /// <param name="pngBytes">Valid PNG image bytes.</param>
    /// <param name="hasTransparency">Whether the signature image has a transparent background.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The created <see cref="SavedSignatureItem"/>.</returns>
    Task<SavedSignatureItem> SaveSignaturePngAsync(byte[] pngBytes, bool hasTransparency, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the saved signature identified by <paramref name="id"/> from disk, if present.
    /// </summary>
    /// <param name="id">Signature identifier (file stem without extension).</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task DeleteSignatureAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes an image file into straight-alpha BGRA8888 pixels and detects whether it has a transparent background.
    /// </summary>
    /// <param name="filePath">Absolute path to the source image file.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>The decoded <see cref="PdfCapturedImage"/> and a flag indicating whether transparency was detected.</returns>
    Task<(PdfCapturedImage Image, bool HasTransparency)> DecodeImageForSignatureAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Encodes straight-alpha BGRA8888 pixels into a lossless PNG byte array.
    /// </summary>
    /// <param name="straightBgraPixels">Straight-alpha BGRA8888 pixel buffer.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>PNG-encoded image bytes.</returns>
    Task<byte[]> EncodeBgraToPngAsync(
        byte[] straightBgraPixels,
        int width,
        int height,
        CancellationToken cancellationToken = default);
}
