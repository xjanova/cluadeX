using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace CluadeX.Services.Attachments;

/// <summary>How much one archive may unpack to. The defaults fit a website or a source tree; a zip bomb (a few KB
/// that inflate to gigabytes) trips the ratio or the byte cap long before the disk fills.</summary>
public sealed record ArchiveLimits
{
    public int MaxEntries { get; init; } = 5000;
    public long MaxTotalBytes { get; init; } = 1L << 30;           // 1 GB unpacked
    public long MaxEntryBytes { get; init; } = 512L * 1024 * 1024; // one file
    /// <summary>Unpacked ÷ packed above this (once past <see cref="RatioCheckMinBytes"/>) = a bomb.</summary>
    public double MaxRatio { get; init; } = 200;
    public long RatioCheckMinBytes { get; init; } = 4L * 1024 * 1024;

    public static ArchiveLimits Default { get; } = new();
}

public enum ArchiveFailure { None, NotFound, Unsupported, Corrupt, TooManyEntries, TooLarge, Bomb, Io }

public sealed class ArchiveEntryInfo
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public bool IsDirectory { get; init; }
}

public sealed class ArchiveListResult
{
    public bool Success { get; init; }
    public ArchiveFailure Failure { get; init; }
    public string? Error { get; init; }
    public List<ArchiveEntryInfo> Entries { get; init; } = new();
    /// <summary>Entries in the archive (may be more than <see cref="Entries"/>, which is capped).</summary>
    public int TotalEntries { get; init; }
    public long TotalBytes { get; init; }
}

public sealed class ArchiveExtractResult
{
    public bool Success { get; set; }
    public ArchiveFailure Failure { get; set; }
    /// <summary>Why it failed, in English for the model (the UI maps <see cref="Failure"/> to its own words).</summary>
    public string? Error { get; set; }
    public string Destination { get; set; } = "";
    /// <summary>Files written, relative to <see cref="Destination"/> with '/' separators.</summary>
    public List<string> Files { get; } = new();
    /// <summary>Entries not written and why ("../evil.txt: path leaves the folder").</summary>
    public List<string> Skipped { get; } = new();
    public long TotalBytes { get; set; }
}

/// <summary>
/// Unpacks .zip, .tar, .tar.gz/.tgz and single-file .gz safely: no entry may land outside the destination (zip-slip:
/// "../", absolute paths, drive letters, device names, alternate data streams), links are never followed or created,
/// and the bytes actually written are counted — the sizes an archive declares are not trusted — so a bomb stops at
/// the limit and what it already wrote is removed.
/// </summary>
public static class ArchiveExtractor
{
    public static bool IsSupported(string? path) => KindOf(path) != ArchiveKind.None;

    /// <summary>Formats people send that this cannot open (the model is told to use 7-Zip through the shell instead).</summary>
    public static bool IsOtherArchive(string? path) =>
        !string.IsNullOrEmpty(path) && Path.GetExtension(path).ToLowerInvariant() is ".7z" or ".rar" or ".xz" or ".bz2" or ".zst" or ".cab" or ".iso";

    private enum ArchiveKind { None, Zip, Tar, TarGz, Gz }

    private static ArchiveKind KindOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return ArchiveKind.None;
        string n = Path.GetFileName(path).ToLowerInvariant();
        if (n.EndsWith(".tar.gz") || n.EndsWith(".tgz")) return ArchiveKind.TarGz;
        if (n.EndsWith(".tar")) return ArchiveKind.Tar;
        if (n.EndsWith(".gz")) return ArchiveKind.Gz;
        if (n.EndsWith(".zip")) return ArchiveKind.Zip;
        return ArchiveKind.None;
    }

    /// <summary>A folder name for unpacking <paramref name="archivePath"/> next to itself: "site.zip" → "site".</summary>
    public static string StemOf(string archivePath)
    {
        string n = Path.GetFileName(archivePath);
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar", ".zip", ".gz" })
            if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return n[..^ext.Length];
        return Path.GetFileNameWithoutExtension(n);
    }

    // ─── Listing ───

    public static ArchiveListResult List(string archivePath, int maxListed = 500)
    {
        if (!File.Exists(archivePath)) return new() { Failure = ArchiveFailure.NotFound, Error = $"No file at {archivePath}" };
        var kind = KindOf(archivePath);
        try
        {
            var list = new List<ArchiveEntryInfo>();
            int total = 0;
            long bytes = 0;
            switch (kind)
            {
                case ArchiveKind.Zip:
                    using (var zip = ZipFile.OpenRead(archivePath))
                        foreach (var e in zip.Entries)
                        {
                            total++;
                            bytes += e.Length;
                            if (list.Count < maxListed)
                                list.Add(new() { Name = e.FullName.Replace('\\', '/'), Size = e.Length, IsDirectory = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\') });
                        }
                    break;
                case ArchiveKind.Tar:
                case ArchiveKind.TarGz:
                    using (var fs = File.OpenRead(archivePath))
                    using (Stream s = kind == ArchiveKind.TarGz ? new GZipStream(fs, CompressionMode.Decompress) : fs)
                    using (var tar = new TarReader(s))
                    {
                        TarEntry? e;
                        while ((e = tar.GetNextEntry(copyData: false)) != null)
                        {
                            total++;
                            bytes += e.Length;
                            if (list.Count < maxListed)
                                list.Add(new() { Name = e.Name.Replace('\\', '/'), Size = e.Length, IsDirectory = e.EntryType == TarEntryType.Directory });
                            if (total > 100_000) break;
                        }
                    }
                    break;
                case ArchiveKind.Gz:
                    total = 1;
                    list.Add(new() { Name = Path.GetFileNameWithoutExtension(archivePath), Size = -1 });
                    break;
                default:
                    return new() { Failure = ArchiveFailure.Unsupported, Error = Unsupported(archivePath) };
            }
            return new() { Success = true, Entries = list, TotalEntries = total, TotalBytes = bytes };
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            return new() { Failure = ArchiveFailure.Corrupt, Error = $"{Path.GetFileName(archivePath)} is damaged or not really a {kind.ToString().ToLowerInvariant()} archive." };
        }
        catch (Exception ex)
        {
            return new() { Failure = ArchiveFailure.Io, Error = $"Could not read {Path.GetFileName(archivePath)}: {ex.Message}" };
        }
    }

    // ─── Extracting ───

    /// <summary>Unpack <paramref name="archivePath"/> into <paramref name="destination"/> (created when missing). An
    /// existing file is kept and listed in Skipped unless <paramref name="overwrite"/>.</summary>
    public static ArchiveExtractResult Extract(string archivePath, string destination, bool overwrite = false,
        ArchiveLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= ArchiveLimits.Default;
        var result = new ArchiveExtractResult();
        if (!File.Exists(archivePath)) return Fail(result, ArchiveFailure.NotFound, $"No file at {archivePath}");
        var kind = KindOf(archivePath);
        if (kind == ArchiveKind.None) return Fail(result, ArchiveFailure.Unsupported, Unsupported(archivePath));

        string dest;
        try { dest = Path.GetFullPath(destination).TrimEnd('\\', '/'); }
        catch (Exception ex) { return Fail(result, ArchiveFailure.Io, $"'{destination}' is not a valid folder: {ex.Message}"); }
        result.Destination = dest;
        bool createdDest = !Directory.Exists(dest);
        long packed = Math.Max(1, new FileInfo(archivePath).Length);
        var written = new List<string>();

        try
        {
            Directory.CreateDirectory(dest);
            var sink = new Sink(dest, limits, packed, overwrite, result, written);
            switch (kind)
            {
                case ArchiveKind.Zip: ExtractZip(archivePath, sink, limits, ct); break;
                case ArchiveKind.Tar:
                case ArchiveKind.TarGz: ExtractTar(archivePath, kind == ArchiveKind.TarGz, sink, limits, ct); break;
                case ArchiveKind.Gz: ExtractGz(archivePath, sink, ct); break;
            }
            result.Success = true;
            return result;
        }
        catch (LimitException lex)
        {
            Cleanup(written, dest, createdDest);
            result.Files.Clear();
            return Fail(result, lex.Failure, lex.Message);
        }
        catch (OperationCanceledException)
        {
            Cleanup(written, dest, createdDest);
            result.Files.Clear();
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            Cleanup(written, dest, createdDest);
            result.Files.Clear();
            return Fail(result, ArchiveFailure.Corrupt, $"{Path.GetFileName(archivePath)} is damaged or not really a {kind.ToString().ToLowerInvariant()} archive.");
        }
        catch (Exception ex)
        {
            Cleanup(written, dest, createdDest);
            result.Files.Clear();
            return Fail(result, ArchiveFailure.Io, $"Unpacking {Path.GetFileName(archivePath)} failed: {ex.Message}");
        }
    }

    private static void ExtractZip(string archivePath, Sink sink, ArchiveLimits limits, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > limits.MaxEntries)
            throw new LimitException(ArchiveFailure.TooManyEntries, $"The archive has {zip.Entries.Count} entries; the limit is {limits.MaxEntries}.");
        long declared = zip.Entries.Sum(e => Math.Max(0, e.Length));
        if (declared > limits.MaxTotalBytes)
            throw new LimitException(ArchiveFailure.TooLarge, $"The archive unpacks to {Mb(declared)} MB; the limit is {Mb(limits.MaxTotalBytes)} MB.");
        foreach (var e in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            bool isDir = e.FullName.EndsWith('/') || e.FullName.EndsWith('\\');
            // Unix mode bits in the high word: 0xA000 = a symbolic link (its "content" is the target path).
            bool isLink = ((e.ExternalAttributes >> 16) & 0xF000) == 0xA000;
            if (isLink) { sink.Skip(e.FullName, "symbolic link"); continue; }
            if (isDir) { sink.Directory(e.FullName); continue; }
            if (e.Length > limits.MaxEntryBytes)
                throw new LimitException(ArchiveFailure.TooLarge, $"{e.FullName} unpacks to {Mb(e.Length)} MB; the limit for one file is {Mb(limits.MaxEntryBytes)} MB.");
            using var src = e.Open();
            sink.File(e.FullName, src, e.CompressedLength, ct);
        }
    }

    private static void ExtractTar(string archivePath, bool gz, Sink sink, ArchiveLimits limits, CancellationToken ct)
    {
        using var fs = File.OpenRead(archivePath);
        using Stream s = gz ? new GZipStream(fs, CompressionMode.Decompress) : fs;
        using var tar = new TarReader(s);
        int count = 0;
        TarEntry? e;
        while ((e = tar.GetNextEntry(copyData: false)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (++count > limits.MaxEntries)
                throw new LimitException(ArchiveFailure.TooManyEntries, $"The archive has more than {limits.MaxEntries} entries.");
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    sink.Directory(e.Name);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    if (e.Length > limits.MaxEntryBytes)
                        throw new LimitException(ArchiveFailure.TooLarge, $"{e.Name} unpacks to {Mb(e.Length)} MB; the limit for one file is {Mb(limits.MaxEntryBytes)} MB.");
                    if (e.DataStream == null) { sink.Directory(Path.GetDirectoryName(e.Name) ?? ""); break; }
                    sink.File(e.Name, e.DataStream, -1, ct);
                    break;
                case TarEntryType.SymbolicLink:
                case TarEntryType.HardLink:
                    sink.Skip(e.Name, "link");
                    break;
                case TarEntryType.GlobalExtendedAttributes:
                case TarEntryType.ExtendedAttributes:
                    break;
                default:
                    sink.Skip(e.Name, "special entry (" + e.EntryType + ")");
                    break;
            }
        }
    }

    private static void ExtractGz(string archivePath, Sink sink, CancellationToken ct)
    {
        using var fs = File.OpenRead(archivePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        sink.File(Path.GetFileNameWithoutExtension(archivePath), gz, fs.Length, ct);
    }

    /// <summary>Where an entry named <paramref name="entryName"/> may be written under <paramref name="destFull"/>
    /// (a full path, no trailing separator), or null when the name must not be written at all.</summary>
    public static string? SafeTarget(string destFull, string entryName, out string? reason)
    {
        reason = null;
        string name = (entryName ?? "").Replace('\\', '/');
        if (name.Trim('/').Length == 0) { reason = "empty name"; return null; }
        if (name.StartsWith('/') || (name.Length > 1 && name[1] == ':')) { reason = "absolute path"; return null; }
        var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part == "..") { reason = "path leaves the folder"; return null; }
            if (part == ".") continue;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { reason = "invalid characters in the name"; return null; }
            if (part.EndsWith('.') || part.EndsWith(' ')) { reason = "a name ending in a dot or space"; return null; }
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            { reason = "a reserved device name"; return null; }
        }
        string full;
        try { full = Path.GetFullPath(Path.Combine(new[] { destFull }.Concat(parts.Where(p => p != ".")).ToArray())); }
        catch { reason = "invalid path"; return null; }
        if (!full.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { reason = "path leaves the folder"; return null; }
        return full;
    }

    private sealed class Sink(string dest, ArchiveLimits limits, long packedTotal, bool overwrite, ArchiveExtractResult result, List<string> written)
    {
        private readonly byte[] _buffer = new byte[81920];

        public void Skip(string name, string why) => result.Skipped.Add($"{name}: {why}");

        public void Directory(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var target = SafeTarget(dest, name, out var why);
            if (target == null) { Skip(name, why!); return; }
            System.IO.Directory.CreateDirectory(target);
        }

        public void File(string name, Stream src, long packedEntry, CancellationToken ct)
        {
            var target = SafeTarget(dest, name, out var why);
            if (target == null) { Skip(name, why!); return; }
            if (System.IO.File.Exists(target) && !overwrite) { Skip(name, "a file with this name is already there (pass overwrite=true to replace it)"); return; }
            if (System.IO.Directory.Exists(target)) { Skip(name, "a folder with this name is already there"); return; }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            long entryBytes = 0;
            // Only files this run created are removed if it aborts: a file it replaced (overwrite) was the user's, and
            // deleting it would lose more than the replacement already did.
            if (!System.IO.File.Exists(target)) written.Add(target);
            using (var outFs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int n;
                while ((n = src.Read(_buffer, 0, _buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    entryBytes += n;
                    result.TotalBytes += n;
                    if (entryBytes > limits.MaxEntryBytes)
                        throw new LimitException(ArchiveFailure.TooLarge, $"{name} unpacks to more than {Mb(limits.MaxEntryBytes)} MB.");
                    if (result.TotalBytes > limits.MaxTotalBytes)
                        throw new LimitException(ArchiveFailure.TooLarge, $"The archive unpacks to more than {Mb(limits.MaxTotalBytes)} MB.");
                    if (packedEntry > 0 && entryBytes > limits.RatioCheckMinBytes && entryBytes / (double)packedEntry > limits.MaxRatio)
                        throw new LimitException(ArchiveFailure.Bomb, $"{name} inflates more than {limits.MaxRatio:0}× — this looks like a zip bomb.");
                    if (result.TotalBytes > limits.RatioCheckMinBytes && result.TotalBytes / (double)packedTotal > limits.MaxRatio)
                        throw new LimitException(ArchiveFailure.Bomb, $"The archive inflates more than {limits.MaxRatio:0}× — this looks like a zip bomb.");
                    outFs.Write(_buffer, 0, n);
                }
            }
            result.Files.Add(Path.GetRelativePath(dest, target).Replace('\\', '/'));
        }
    }

    private sealed class LimitException(ArchiveFailure failure, string message) : Exception(message)
    {
        public ArchiveFailure Failure { get; } = failure;
    }

    private static void Cleanup(List<string> written, string dest, bool createdDest)
    {
        foreach (var f in written)
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        if (createdDest)
            try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch { }
    }

    private static ArchiveExtractResult Fail(ArchiveExtractResult r, ArchiveFailure f, string msg)
    {
        r.Success = false;
        r.Failure = f;
        r.Error = msg;
        return r;
    }

    private static string Unsupported(string path) =>
        $"{Path.GetFileName(path)}: only .zip, .tar, .tar.gz/.tgz and .gz can be unpacked here"
        + (IsOtherArchive(path) ? " — for this format use 7-Zip through the shell if it is installed (7z x \"file\" -o\"folder\")." : ".");

    private static long Mb(long bytes) => (bytes + (1 << 20) - 1) >> 20;
}
