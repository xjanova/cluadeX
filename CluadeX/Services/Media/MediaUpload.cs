using System.IO;

namespace CluadeX.Services.Media;

/// <summary>
/// A file the media page writes back (an edited image, a WAV, a recorded clip, or ffmpeg's cut), landed safely: the
/// bytes go to a hidden temp file in the target's folder, in chunks the page sends one at a time (each waits for the
/// previous one's ack — never one giant message), and only a complete file is moved into place. A copy never takes
/// an existing name; an overwrite replaces the original in one step (File.Replace), so a crash or a full disk leaves
/// the old file or the new one, never half of one. Disposing an uncommitted upload deletes the temp file (the tab was
/// closed mid-save, the page died, the user pressed Cancel).
/// </summary>
public sealed class MediaUpload : IDisposable
{
    public string TempPath { get; }
    /// <summary>The extension the result is written in (no dot).</summary>
    public string Ext { get; }
    public long Expected { get; }
    public long Received { get; private set; }
    public bool Committed { get; private set; }
    private FileStream? _fs;
    private int _nextSeq;

    /// <summary>Start an upload of <paramref name="expected"/> bytes into a temp file next to <paramref name="nearPath"/>.</summary>
    public MediaUpload(string nearPath, string ext, long expected, bool openStream = true)
    {
        if (expected < 0 || expected > MediaFiles.MaxUploadBytes) throw new ArgumentOutOfRangeException(nameof(expected));
        Ext = ext;
        Expected = expected;
        TempPath = TempPathFor(nearPath, ext);
        if (openStream)
            _fs = new FileStream(TempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
    }

    /// <summary>A hidden temp name in <paramref name="nearPath"/>'s folder that keeps <paramref name="ext"/> (ffmpeg
    /// picks the container from the extension).</summary>
    public static string TempPathFor(string nearPath, string ext)
    {
        string dir = Path.GetDirectoryName(nearPath) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(nearPath);
        if (stem.Length > 40) stem = stem[..40];
        return Path.Combine(dir, $".{stem}.{Guid.NewGuid():N}.cxpart.{ext.TrimStart('.')}");
    }

    /// <summary>Append chunk <paramref name="seq"/> (base64). Out of order, or more than announced, is refused.</summary>
    public async Task AppendBase64Async(int seq, string base64, CancellationToken ct = default)
    {
        if (_fs == null || Committed) throw new InvalidOperationException("upload closed");
        if (seq != _nextSeq) throw new InvalidDataException($"chunk {seq} out of order (expected {_nextSeq})");
        byte[] bytes = Convert.FromBase64String(base64);
        if (Received + bytes.LongLength > Expected) throw new InvalidDataException("more bytes than announced");
        await _fs.WriteAsync(bytes, ct).ConfigureAwait(false);
        Received += bytes.LongLength;
        _nextSeq++;
    }

    /// <summary>All bytes arrived: flush and close the temp file (true), or false when some are missing.</summary>
    public async Task<bool> FinishAsync()
    {
        if (_fs == null) return File.Exists(TempPath);
        if (Received != Expected) return false;
        await _fs.FlushAsync().ConfigureAwait(false);
        _fs.Flush(flushToDisk: true);
        await _fs.DisposeAsync().ConfigureAwait(false);
        _fs = null;
        return true;
    }

    /// <summary>Move the finished temp file to a NEW name: <paramref name="pickName"/> is asked again when the name it
    /// gave was taken meanwhile (another program, a second save). Returns the final path.</summary>
    public string CommitAsCopy(Func<string> pickName)
    {
        for (int attempt = 0; ; attempt++)
        {
            string target = pickName();
            try
            {
                File.Move(TempPath, target, overwrite: false);
                Committed = true;
                return target;
            }
            catch (IOException) when (File.Exists(target) && attempt < 5) { /* taken a moment ago: another name */ }
        }
    }

    /// <summary>Replace <paramref name="original"/> with the finished temp file in one step (the caller has asked).
    /// The original gone meanwhile: the result simply takes its name.</summary>
    public void CommitOverwrite(string original)
    {
        if (File.Exists(original)) File.Replace(TempPath, original, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(TempPath, original);
        Committed = true;
    }

    public void Dispose()
    {
        try { _fs?.Dispose(); } catch { }
        _fs = null;
        if (Committed) return;
        try { if (File.Exists(TempPath)) File.Delete(TempPath); } catch { /* still locked (antivirus scanning it): best effort, the name is hidden and unique */ }
    }
}
