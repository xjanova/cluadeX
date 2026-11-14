using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace CluadeX.Services.Media;

/// <summary>One detector the privacy blur uses: where it comes from and what it must be.</summary>
public sealed record PrivacyModel(string Key, string FileName, string Url, long Size, string Sha256, string License);

/// <summary>
/// The two small detectors behind the privacy blur (owner 2026-11-14: "เบลอคน เบลอทะเบียนรถ … ใส่ใน CluadeX"). They are
/// not shipped in the installer: the first blur fetches them once (≈ 8 MB) into &lt;data&gt;\Models\privacy, each checked
/// against its size and SHA-256 before it is used — a changed or broken file is refused and never loaded.
/// <list type="bullet">
/// <item>Faces: YuNet 2023mar (OpenCV Zoo, Apache-2.0), 232 KB.</item>
/// <item>Licence plates: YOLOv9-t 640 end-to-end (open-image-models by ankandrew, MIT), 7.8 MB — trained on plates of
///   many countries, Thai ones included.</item>
/// </list>
/// </summary>
public static class PrivacyModels
{
    public static readonly PrivacyModel Face = new(
        "face", "face_detection_yunet_2023mar.onnx",
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/f12e12798e8314f7c074a6656816c048dcc95b7a/models/face_detection_yunet/face_detection_yunet_2023mar.onnx",
        232_589, "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4", "Apache-2.0 (OpenCV Zoo)");

    public static readonly PrivacyModel Plate = new(
        "plate", "yolo-v9-t-640-license-plates-end2end.onnx",
        "https://github.com/ankandrew/open-image-models/releases/download/assets/yolo-v9-t-640-license-plates-end2end.onnx",
        7_835_770, PlateSha256, "MIT (open-image-models)");

    // Filled from the file itself on 2026-11-14 (the release does not publish a digest).
    private const string PlateSha256 = "c3c1026ca7d0585dd88084d68182dd897113712fa734ae1557ca70174440c076";

    public static IReadOnlyList<PrivacyModel> All { get; } = new[] { Face, Plate };

    /// <summary>For the harness: a folder to use instead of &lt;data&gt;\Models\privacy, and an HttpClient that is not the network.</summary>
    public static string? FolderOverride { get; set; }
    public static HttpClient? HttpOverride { get; set; }

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string Folder(string? dataRoot) =>
        FolderOverride ?? Path.Combine(string.IsNullOrEmpty(dataRoot) ? Path.GetTempPath() : dataRoot, "Models", "privacy");

    public static string PathOf(PrivacyModel m, string? dataRoot) => Path.Combine(Folder(dataRoot), m.FileName);

    /// <summary>The models of <paramref name="wanted"/> that are not on disk yet (a file of the wrong size counts as missing).</summary>
    public static List<PrivacyModel> Missing(IEnumerable<PrivacyModel> wanted, string? dataRoot) =>
        wanted.Where(m => !Present(m, dataRoot)).ToList();

    public static bool Present(PrivacyModel m, string? dataRoot)
    {
        try
        {
            var fi = new FileInfo(PathOf(m, dataRoot));
            return fi.Exists && fi.Length == m.Size;
        }
        catch { return false; }
    }

    /// <summary>Bytes still to fetch for <paramref name="wanted"/>.</summary>
    public static long BytesToFetch(IEnumerable<PrivacyModel> wanted, string? dataRoot) => Missing(wanted, dataRoot).Sum(m => m.Size);

    /// <summary>Is this the file we pinned? Size and SHA-256 both.</summary>
    public static bool Verify(string path, PrivacyModel m)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != m.Size) return false;
            using var s = File.OpenRead(path);
            return string.Equals(Convert.ToHexString(SHA256.HashData(s)), m.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Make sure every model of <paramref name="wanted"/> is on disk and is the pinned file. Missing ones are downloaded
    /// into a temp name beside the target, checked, then moved into place. Returns null when all are ready, else what
    /// went wrong (in English, for the log and the model; callers show their own localized line).
    /// </summary>
    public static async Task<string?> EnsureAsync(IEnumerable<PrivacyModel> wanted, string? dataRoot, CancellationToken ct)
    {
        string folder = Folder(dataRoot);
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { return $"cannot create {folder}: {ex.Message}"; }
        var http = HttpOverride ?? SharedHttp;
        foreach (var m in wanted)
        {
            string target = PathOf(m, dataRoot);
            if (Present(m, dataRoot))
            {
                if (Verify(target, m)) continue;
                TryDelete(target);   // right size, wrong bytes: fetch it again
            }
            string temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            try
            {
                using (var resp = await http.GetAsync(m.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return $"{m.FileName}: HTTP {(int)resp.StatusCode}";
                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buf = new byte[81920];
                    long total = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > m.Size) return $"{m.FileName}: the download is larger than the pinned {m.Size:N0} bytes";
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    }
                }
                if (!Verify(temp, m)) return $"{m.FileName}: the download does not match its pinned SHA-256 — not used";
                File.Move(temp, target, overwrite: true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return "cancelled"; }
            catch (Exception ex) { return $"{m.FileName}: {ex.GetType().Name}: {ex.Message}"; }
            finally { TryDelete(temp); }
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
