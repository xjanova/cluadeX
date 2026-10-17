using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Safety;

/// <summary>One thing that needs the user's explicit OK: which tool, what exactly it would run, and why it is dangerous.</summary>
public sealed record DangerRequest(string Tool, string Action, string Reason);

/// <summary>
/// The owner's rule (2026-10-18): a command or code that deletes a drive, a home or a system folder MAY run — but only
/// after the user says yes to a red, explicit prompt, every time. No rule, no "allow everything" chat, no switched-off
/// permission system and no agent answers it ("ห้ามทำพละการ"). Where nobody can answer (a test harness, a headless
/// run, the app closed) the answer is no.
///
/// The app sets <see cref="Handler"/> (ChatViewModel: a red permission row in the chat). Claude Dev's own shell reaches
/// it through <see cref="DangerApprovalPipe"/> from the <c>--cx-guard</c> hook.
/// </summary>
public static class DangerApproval
{
    /// <summary>Shows the prompt and returns the user's answer. Null = no UI here → every request is refused.</summary>
    public static Func<DangerRequest, Task<bool>>? Handler { get; set; }

    /// <summary>The user's answer; false when nobody can be asked or the prompt failed.</summary>
    public static async Task<bool> ConfirmAsync(string tool, string action, string reason)
    {
        var handler = Handler;
        if (handler == null) return false;
        try { return await handler(new DangerRequest(tool, action, reason)).ConfigureAwait(false); }
        catch { return false; }
    }

    /// <summary>
    /// The gate every runner calls with the guard's verdict: null (nothing dangerous) passes straight through; a
    /// finding is put to the user, and only their yes lets it run. Returns null = may run, otherwise the refusal.
    /// </summary>
    public static async Task<string?> GateAsync(string tool, string action, string? finding)
    {
        if (finding == null) return null;
        return await ConfirmAsync(tool, action, finding).ConfigureAwait(false)
            ? null
            : finding + "\nThe user did not approve it (it runs only after the user's explicit yes on the red prompt).";
    }
}

/// <summary>
/// A named pipe between the <c>CluadeX.exe --cx-guard</c> hook (Claude Dev's shell, a separate process) and the running
/// app: the hook sends one request line, the app shows the same red prompt and answers "allow" or "deny". Current user
/// only. No app listening, a timeout or anything unexpected = deny. <c>CLUADEX_DANGER_PIPE</c> renames the pipe (tests
/// use a private name so they never raise a prompt in the owner's running app).
/// </summary>
public static class DangerApprovalPipe
{
    public static string Name =>
        Environment.GetEnvironmentVariable("CLUADEX_DANGER_PIPE") is { Length: > 0 } n ? n : "cluadex-danger-approval";

    /// <summary>How long the hook waits for the user's answer (the hook's own timeout in GuardHook.SettingsFile is longer).</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(280);

    private static int _started;

    /// <summary>Serve requests in the app (once). If another CluadeX already serves the name, this one stays quiet.</summary>
    public static void StartServer()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        string name = Name;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch { return; }   // the name is taken (another instance) or pipes are unavailable
                try { await server.WaitForConnectionAsync().ConfigureAwait(false); }
                catch { server.Dispose(); await Task.Delay(500).ConfigureAwait(false); continue; }
                _ = Task.Run(() => ServeOneAsync(server));
            }
        });
    }

    private static async Task ServeOneAsync(NamedPipeServerStream server)
    {
        using (server)
        {
            bool allow = false;
            try
            {
                var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                var lineTask = reader.ReadLineAsync();
                if (await Task.WhenAny(lineTask, Task.Delay(5000)).ConfigureAwait(false) == lineTask && lineTask.Result is { Length: > 0 and < 65536 } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string Str(string p) => root.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var ask = DangerApproval.ConfirmAsync(Str("tool"), Str("action"), Str("reason"));
                    allow = await Task.WhenAny(ask, Task.Delay(AnswerTimeout)).ConfigureAwait(false) == ask && ask.Result;
                }
            }
            catch { allow = false; }
            try
            {
                var bytes = Encoding.UTF8.GetBytes(allow ? "allow\n" : "deny\n");
                await server.WriteAsync(bytes).ConfigureAwait(false);
                await server.FlushAsync().ConfigureAwait(false);
                server.WaitForPipeDrain();
            }
            catch { }
        }
    }

    /// <summary>Hook side: ask the running app. True only for an explicit "allow".</summary>
    public static bool Ask(string tool, string action, string reason)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            string json = JsonSerializer.Serialize(new { tool, action, reason });
            var bytes = Encoding.UTF8.GetBytes(json.Replace("\n", " ") + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            using var reader = new StreamReader(client, new UTF8Encoding(false));
            var read = reader.ReadLineAsync();
            if (!read.Wait(AnswerTimeout + TimeSpan.FromSeconds(10))) return false;
            return string.Equals(read.Result?.Trim(), "allow", StringComparison.Ordinal);
        }
        catch { return false; }
    }
}
