using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CluadeX.Services.Safety;

/// <summary>One thing that needs the user's explicit OK: which tool, what exactly it would run, and why it is dangerous.</summary>
public sealed record DangerRequest(string Tool, string Action, string Reason);

/// <summary>
/// The owner's rule (2026-10-18, narrowed 2026-10-28 to wiping a whole drive, deleting the root or a home folder,
/// deleting Windows' backups and wiping a database — see DestructiveCodeScan): a command or code that does one of
/// those MAY run — but only after the user says yes to a red, explicit prompt, every time. No rule, no "allow
/// everything" chat, no switched-off permission system and no agent answers it ("ห้ามทำพละการ"). Where nobody can
/// answer (a test harness, a headless run, the app closed) the answer is no.
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
    private const string NameEnv = "CLUADEX_DANGER_PIPE";

    /// <summary>The app's own name carries its process id and <see cref="StartServer"/> puts it in the environment, so the
    /// hook of a CLI this app started asks this app — not a test harness that served the same fixed name (it would answer
    /// no, and the owner's red prompt never showed; ChatToolBridge.PipeName, 2026-11-03).</summary>
    public static string Name =>
        Environment.GetEnvironmentVariable(NameEnv) is { Length: > 0 } n ? n : "cluadex-danger-approval-" + Environment.ProcessId;

    /// <summary>How long the hook waits for the user's answer (the hook's own timeout in GuardHook.SettingsFile is longer).</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(280);

    private static int _started;

    /// <summary>Serve requests in the app (once). The first instance is created only if no other process holds the name
    /// (a child that inherited the name stays quiet and tries again later); the rest join it.</summary>
    public static void StartServer()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        string name = Name;
        Environment.SetEnvironmentVariable(NameEnv, name);
        _ = Task.Run(async () =>
        {
            bool owned = false;
            while (true)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
                                                   | (owned ? PipeOptions.None : PipeOptions.FirstPipeInstance));
                    owned = true;
                }
                catch when (!owned) { await Task.Delay(5000).ConfigureAwait(false); continue; }
                catch { return; }   // pipes are unavailable
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
                    if (StrictFrom(root, Str("tool"), Str("action"), Str("reason")) is { } strict)
                    {
                        // Strict approval: the one-time-code card, answered only by the owner (StrictApproval).
                        using var cts = new CancellationTokenSource(AnswerTimeout);
                        allow = await StrictApproval.ConfirmAsync(strict, cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        var ask = DangerApproval.ConfirmAsync(Str("tool"), Str("action"), Str("reason"));
                        allow = await Task.WhenAny(ask, Task.Delay(AnswerTimeout)).ConfigureAwait(false) == ask && ask.Result;
                    }
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

    /// <summary>The strict request a hook sent ("strict": {scope, kind, matched, host, where}), or null.</summary>
    private static StrictRequest? StrictFrom(JsonElement root, string tool, string action, string reason)
    {
        if (!root.TryGetProperty("strict", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        string Str(string p) => s.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var scope = HardToUndo.ParseScopes(Str("scope"));
        if (scope == StrictScope.None) scope = StrictScope.Local;
        var finding = new HardToUndoFinding(scope, Str("kind"), Str("matched"), Str("host") is { Length: > 0 } h ? h : null);
        return new StrictRequest(tool, action, Str("where"), finding, reason.Length > 0 ? reason : null);
    }

    /// <summary>Hook side: ask the running app. True only for an explicit "allow". <paramref name="strict"/> = put it on
    /// the strict-approval card (the code the owner types) instead of the red yes / no.</summary>
    public static bool Ask(string tool, string action, string reason, HardToUndoFinding? strict = null, string? where = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            string json = strict == null
                ? JsonSerializer.Serialize(new { tool, action, reason })
                : JsonSerializer.Serialize(new
                {
                    tool, action, reason,
                    strict = new { scope = HardToUndo.FormatScopes(strict.Scope), kind = strict.Kind, matched = strict.Matched, host = strict.Host ?? "", where = where ?? "" },
                });
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
