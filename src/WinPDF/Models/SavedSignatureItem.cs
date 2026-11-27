namespace WinPDF.Models;

/// <summary>
/// Represents a saved non-digital handwritten or image-based signature stored locally on disk.
/// </summary>
/// <param name="Id">Unique identifier (file stem without extension).</param>
/// <param name="FilePath">Absolute path to the stored PNG file.</param>
/// <param name="CreatedUtc">UTC timestamp when the signature was saved.</param>
/// <param name="HasTransparency">Whether the signature image has a transparent background.</param>
public sealed record SavedSignatureItem(
    string Id,
    string FilePath,
    DateTimeOffset CreatedUtc,
    bool HasTransparency = true);
