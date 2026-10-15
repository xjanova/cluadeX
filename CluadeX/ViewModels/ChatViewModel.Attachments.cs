using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CluadeX.Models;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>
/// Images attached to the next message — pasted with Ctrl+V, dropped on the composer or picked from "+" (owner
/// 2026-10-11: "ควร ctrl+v แล้ววางรูปภาพลงแชทให้เอไอดูได้"). Each is copied under the data folder's attachments\ so
/// the chat keeps showing it after the original moves; on send they go with the message to a vision model.
/// </summary>
public partial class ChatViewModel
{
    public const int MaxPendingImages = 8;
    private const long MaxImageBytes = 20L * 1024 * 1024;

    public ObservableCollection<string> PendingImages { get; } = new();
    public bool HasPendingImages => PendingImages.Count > 0;

    private ICommand? _removePendingImageCommand;
    public ICommand RemovePendingImageCommand => _removePendingImageCommand ??= new RelayCommand<string>(p =>
    {
        if (p != null) PendingImages.Remove(p);
    });

    private bool _pendingImagesHooked;
    private void HookPendingImages()
    {
        if (_pendingImagesHooked) return;
        _pendingImagesHooked = true;
        PendingImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingImages));
    }

    private string AttachmentsFolder =>
        Path.Combine(_settingsService.DataRoot, "attachments", DateTime.Now.ToString("yyyy-MM"));

    /// <summary>The kinds of image file that can be attached (a BMP or TIFF is converted to PNG).</summary>
    public static bool LooksLikeImageFile(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff";

    /// <summary>Image bytes (a pasted screenshot): saved as a file and attached. False — and why in the status line —
    /// when they are not an image, too big, or the limit is reached.</summary>
    public bool AttachImageBytes(byte[] bytes)
    {
        HookPendingImages();
        if (PendingImages.Count >= MaxPendingImages) { StatusText = LocF("chat.attach.tooMany", MaxPendingImages); return false; }
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes) { StatusText = Loc("chat.attach.tooBig"); return false; }
        string? mime = Services.Vision.ImageCodec.SniffMediaType(bytes);
        string? ext = mime switch
        {
            "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp",
            "image/bmp" or "image/tiff" => ".png",
            _ => null,
        };
        if (ext == null) { StatusText = Loc("chat.attach.notImage"); return false; }
        try
        {
            if (mime is "image/bmp" or "image/tiff") bytes = ToPng(bytes);
            Directory.CreateDirectory(AttachmentsFolder);
            string path = Path.Combine(AttachmentsFolder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{ext}");
            File.WriteAllBytes(path, bytes);
            PendingImages.Add(path);
            StatusText = LocF("chat.attach.added", PendingImages.Count);
            return true;
        }
        catch (Exception ex)
        {
            _debugLog?.Warn("Chat", "attaching an image failed", ex);
            StatusText = Loc("chat.attach.failed");
            return false;
        }
    }

    /// <summary>Image files (dropped, picked, or copied in Explorer): each read and attached like pasted bytes.</summary>
    public int AttachImageFiles(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (var p in paths.Where(LooksLikeImageFile))
        {
            try
            {
                var fi = new FileInfo(p);
                if (!fi.Exists) continue;
                if (fi.Length > MaxImageBytes) { StatusText = Loc("chat.attach.tooBig"); continue; }
                if (AttachImageBytes(File.ReadAllBytes(p))) added++;
                else if (PendingImages.Count >= MaxPendingImages) break;
            }
            catch (Exception ex) { _debugLog?.Warn("Chat", $"attaching {p} failed", ex); }
        }
        return added;
    }

    private static byte[] ToPng(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(frame);
        using var output = new MemoryStream();
        enc.Save(output);
        return output.ToArray();
    }

    /// <summary>The attached files decoded for a model (resized and re-encoded as the vision pipeline does for tool
    /// images). A file that is gone or unreadable is left out.</summary>
    private async Task<List<ToolImage>> LoadAttachedImagesAsync(IEnumerable<string> paths)
    {
        var list = new List<ToolImage>();
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p)) continue;
                var decoded = await Services.Vision.ImageCodec.ProcessAsync(await File.ReadAllBytesAsync(p));
                if (decoded.Image != null) list.Add(decoded.Image with { FilePath = p });
                else _debugLog?.Warn("Chat", $"attached image {p} could not be used: {decoded.Error}");
            }
            catch (Exception ex) { _debugLog?.Warn("Chat", $"attached image {p} could not be read", ex); }
        }
        return list;
    }
}
