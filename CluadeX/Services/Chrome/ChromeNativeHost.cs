using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace CluadeX.Services.Chrome;

/// <summary>
/// CluadeX.exe as Chrome's native-messaging host: a relay between the extension (stdin/stdout) and the running app
/// (a pipe only this Windows user can open). Headless, before Velopack and WPF. Chrome starts it with the extension's
/// origin as the first argument (a host manifest cannot add a flag) and keeps it for as long as the extension holds
/// the port. Both sides frame messages the same way, so the bytes pass through untouched.
///
/// While the app is not running it keeps waiting for the pipe, holding what the extension already sent, and exits when
/// Chrome goes away. When the app goes away it exits too: the extension starts a new host, which waits again.
/// </summary>
public static class ChromeNativeHost
{
    private const string OriginPrefix = "chrome-extension://";

    /// <summary>Chrome started this process as the host.</summary>
    public static bool IsHostCall(string[] args) =>
        args.Length > 0 && args[0].StartsWith(OriginPrefix, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args)
    {
        if (!string.Equals(args[0].TrimEnd('/'), OriginPrefix + ChromeBridgeInstall.ExtensionId, StringComparison.OrdinalIgnoreCase)) return 1;
        try { return RelayAsync().GetAwaiter().GetResult(); }
        catch (IOException) { return 0; }   // either side closed mid-write: nothing left to relay
    }

    private static async Task<int> RelayAsync()
    {
        using var fromChrome = Console.OpenStandardInput();
        using var toChrome = Console.OpenStandardOutput();

        // Read from the start, also while the pipe is not there yet: an end of stdin is how Chrome says it left.
        var pending = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var reading = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                int n;
                while ((n = await fromChrome.ReadAsync(buffer)) > 0) pending.Writer.TryWrite(buffer[..n]);
            }
            finally { pending.Writer.TryComplete(); }
        });

        using var pipe = await ConnectAsync(reading);
        if (pipe == null) return 0;

        var chromeToApp = Task.Run(async () =>
        {
            await foreach (var chunk in pending.Reader.ReadAllAsync())
            {
                await pipe.WriteAsync(chunk);
                await pipe.FlushAsync();
            }
        });
        var appToChrome = Task.Run(async () =>
        {
            var buffer = new byte[256 * 1024];
            int n;
            while ((n = await pipe.ReadAsync(buffer)) > 0)
            {
                await toChrome.WriteAsync(buffer.AsMemory(0, n));
                await toChrome.FlushAsync();
            }
        });
        await Task.WhenAny(chromeToApp, appToChrome);
        return 0;
    }

    /// <summary>Wait for the app. The pipe takes one client: a second Chrome profile (or Edge) with the extension waits
    /// here for its turn instead of taking the connection away. Null when Chrome left first, or when the connection was
    /// switched off in CluadeX: nothing would ever come.</summary>
    private static async Task<NamedPipeClientStream?> ConnectAsync(Task chromeGone)
    {
        while (!chromeGone.IsCompleted && ChromeBridgeInstall.IsRegistered)
        {
            var pipe = new NamedPipeClientStream(".", ChromeBridgeInstall.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(1000);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                pipe.Dispose();
                await Task.Delay(250);
            }
        }
        return null;
    }
}
