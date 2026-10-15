namespace CluadeX.Models;

/// <summary>
/// One image a tool produced (a browser screenshot, an MCP image block, a PNG read from the project), ready to be
/// sent to a model that can see images.
///
/// The bytes travel ONLY here: <see cref="Base64"/> never goes into <see cref="ToolResult.Output"/>, a chat
/// message or codex.db — the text side carries a short "[image N: WxH attached, saved at …]" marker instead, and
/// <see cref="FilePath"/> points at the JPEG on disk (<see cref="CluadeX.Services.Vision.ToolImageStore"/>).
/// </summary>
/// <param name="MediaType">MIME type of <see cref="Base64"/>: image/jpeg, image/png, image/webp or image/gif.</param>
/// <param name="Base64">The image bytes, base64 without a "data:" prefix.</param>
/// <param name="Width">Pixel width as encoded.</param>
/// <param name="Height">Pixel height as encoded.</param>
/// <param name="FilePath">Where the image was saved (for thumbnails and the omitted-image note), or null.</param>
/// <param name="Caption">Short text that goes with the image (what it shows, the CSS-pixel mapping), or null.</param>
public sealed record ToolImage(string MediaType, string Base64, int Width, int Height, string? FilePath = null, string? Caption = null)
{
    /// <summary>Decoded size in bytes, estimated from the base64 length (no decode).</summary>
    public long ApproxBytes => string.IsNullOrEmpty(Base64) ? 0 : Base64.Length / 4L * 3L;

    /// <summary>Never the base64: a record's generated ToString would put megabytes of it into any log line or
    /// exception message that mentions the image.</summary>
    public override string ToString() =>
        $"ToolImage {MediaType} {Width}x{Height} (~{ApproxBytes:N0} bytes){(FilePath != null ? " at " + FilePath : "")}";
}
