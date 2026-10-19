using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CluadeX.Services.Connections;

/// <summary>What Windows says about an installer's digital signature.</summary>
public enum SignatureCheck { Valid, Unsigned, Invalid }

/// <summary>
/// "ติดตั้ง BrainX" in one click: download the installer from xman4289.com (the owner's rule: apps come only from there),
/// check it really is that installer, then run it — its own window, its own progress; when it finishes BrainX starts and
/// CluadeX connects to it (<see cref="Mcp.McpServerManager.AutoConnectInstalledBrainAsync"/> / the invite's wait).
///
/// Checks before anything runs: HTTPS from xman4289.com only (a redirect to another host, or to a web page, is refused
/// — the page is opened in the browser instead), an exe (MZ header) of a believable size, and Windows' Authenticode
/// verdict: a broken signature is refused; an unsigned file is shown to the user with its SHA-256 before it runs.
/// </summary>
public sealed class BrainXInstaller
{
    public const string DownloadUrl = "https://xman4289.com/brainx/download";
    public const string Host = "xman4289.com";
    private const long MinBytes = 5L * 1024 * 1024, MaxBytes = 1536L * 1024 * 1024;

    private readonly DebugLogService? _log;
    private readonly Func<HttpMessageHandler>? _handler;

    public BrainXInstaller(DebugLogService? log = null, Func<HttpMessageHandler>? handler = null)
    {
        _log = log;
        _handler = handler;
    }

    /// <summary>Is this response the installer itself (not a page, not another host)? Pure.</summary>
    public static string? RefuseReason(Uri? finalUri, string? mediaType, long? length)
    {
        if (finalUri == null || finalUri.Scheme != Uri.UriSchemeHttps
            || !(finalUri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || finalUri.Host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase)))
            return "brainx.err.host";
        if (mediaType != null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
            return "brainx.err.notFile";
        if (length is { } n && (n < MinBytes || n > MaxBytes)) return "brainx.err.size";
        return null;
    }

    /// <summary>Download to a fresh temp folder; reports bytes done / total. Throws InvalidOperationException(errorKey).</summary>
    public async Task<string> DownloadAsync(IProgress<(long Done, long? Total)>? progress = null, CancellationToken ct = default)
    {
        // Redirects are followed by hand: each hop must stay on xman4289.com (the store streams the file itself).
        using var http = _handler != null ? new HttpClient(_handler(), true) : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        http.Timeout = TimeSpan.FromMinutes(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CluadeX-BrainX-Installer");
        var uri = new Uri(DownloadUrl);
        HttpResponseMessage? resp = null;
        for (int hop = 0; hop < 5; hop++)
        {
            resp?.Dispose();
            resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                uri = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                if (RefuseReason(uri, null, null) == "brainx.err.host") throw new InvalidOperationException("brainx.err.host");
                continue;
            }
            break;
        }
        using var response = resp!;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("brainx.err.http");
        if (RefuseReason(uri, response.Content.Headers.ContentType?.MediaType, response.Content.Headers.ContentLength) is { } why)
            throw new InvalidOperationException(why);

        string dir = Path.Combine(Path.GetTempPath(), "cluadex-brainx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "BrainX-win-Setup.exe");
        long total = response.Content.Headers.ContentLength ?? -1, done = 0;
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buf = new byte[256 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (done > MaxBytes) throw new InvalidOperationException("brainx.err.size");
                progress?.Report((done, total > 0 ? total : null));
            }
        }
        if (done < MinBytes) { TryDelete(file); throw new InvalidOperationException("brainx.err.size"); }
        if (!IsExe(file)) { TryDelete(file); throw new InvalidOperationException("brainx.err.notFile"); }
        _log?.Info("BrainX", $"installer downloaded · {done / 1048576} MB · {uri.Host}");
        return file;
    }

    public static bool IsExe(string file)
    {
        try
        {
            using var f = File.OpenRead(file);
            return f.ReadByte() == 'M' && f.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    public static string Sha256(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>Run the installer and wait for it (its own window; Velopack starts BrainX when it is done).</summary>
    public async Task<int> RunAsync(string installer, CancellationToken ct = default)
    {
        if (Setup.SystemSetupService.InsidePackage()) throw new InvalidOperationException("inside-package");
        using var p = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installer)! })
                      ?? throw new InvalidOperationException("brainx.err.run");
        await p.WaitForExitAsync(ct);
        _log?.Info("BrainX", $"installer finished · exit {p.ExitCode}");
        return p.ExitCode;
    }

    public static void TryDelete(string file)
    {
        try { File.Delete(file); Directory.Delete(Path.GetDirectoryName(file)!, false); } catch { }
    }

    // ── Authenticode (WinVerifyTrust) ──

    public static SignatureCheck VerifySignature(string file)
    {
        var fileInfo = new WinTrustFileInfo(file);
        var data = new WinTrustData(fileInfo);
        try
        {
            uint result = WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, data);
            return result switch
            {
                0 => SignatureCheck.Valid,
                TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureCheck.Unsigned,
                _ => SignatureCheck.Invalid,
            };
        }
        finally
        {
            data.StateAction = 2;   // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ActionGenericVerifyV2, data);
            data.Dispose();
            fileInfo.Dispose();
        }
    }

    private const uint TrustENoSignature = 0x800B0100, TrustESubjectFormUnknown = 0x800B0003, TrustEProviderUnknown = 0x800B0001;
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, [In, Out] WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        public uint StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        public IntPtr FilePath;
        public IntPtr FileHandle = IntPtr.Zero;
        public IntPtr KnownSubject = IntPtr.Zero;
        public WinTrustFileInfo(string path) => FilePath = Marshal.StringToCoTaskMemUni(path);
        public void Dispose() { if (FilePath != IntPtr.Zero) { Marshal.FreeCoTaskMem(FilePath); FilePath = IntPtr.Zero; } }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData : IDisposable
    {
        public uint StructSize = (uint)Marshal.SizeOf<WinTrustData>();
        public IntPtr PolicyCallbackData = IntPtr.Zero;
        public IntPtr SipClientData = IntPtr.Zero;
        public uint UiChoice = 2;               // WTD_UI_NONE
        public uint RevocationChecks = 0;       // WTD_REVOKE_NONE (offline-safe; the chain itself is still checked)
        public uint UnionChoice = 1;            // WTD_CHOICE_FILE
        public IntPtr FileInfoPtr;
        public uint StateAction = 1;            // WTD_STATEACTION_VERIFY
        public IntPtr StateData = IntPtr.Zero;
        public IntPtr UrlReference = IntPtr.Zero;
        public uint ProvFlags = 0x00000010;     // WTD_REVOCATION_CHECK_NONE
        public uint UiContext = 0;
        public IntPtr SignatureSettings = IntPtr.Zero;

        public WinTrustData(WinTrustFileInfo info)
        {
            FileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(info, FileInfoPtr, false);
        }

        public void Dispose() { if (FileInfoPtr != IntPtr.Zero) { Marshal.FreeCoTaskMem(FileInfoPtr); FileInfoPtr = IntPtr.Zero; } }
    }
}
