using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CluadeX.Services.Setup;

namespace CluadeX.Services.Connections;

/// <summary>The exact process a connector runs: what to start, with which arguments, and which environment variables
/// (the secret ones included) — or, for a service on the network, the url and headers (the key included) the launcher
/// relays to itself. Built fresh at every start; never written to any config file.</summary>
public sealed record LaunchPlan(string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env,
    string? Url = null, IReadOnlyDictionary<string, string>? Headers = null)
{
    public bool IsRemote => Url != null;
}

/// <summary>What the launcher found on this PC for one start (gathered by <see cref="ConnectorLauncher.Gather"/>, fed by
/// hand in the harness).</summary>
public sealed record LaunchFacts(string? Npx, string? GitHubServerExe, string? GitHubToken, string? Secret);

/// <summary>
/// <c>CluadeX.exe --connector &lt;id&gt;</c>: the command BrainX's bridge (or CluadeX's own MCP list) starts for a
/// connection. It reads the key at that moment — `gh auth token`, or the DPAPI store — puts it in the real server's
/// environment and relays the MCP messages both ways on stdin/stdout. So the config files that name a connection hold
/// only "CluadeX.exe --connector stripe": no key, nothing worth stealing from a synced vault.
/// Headless: no WPF, no window, no Velopack — like the Claude Dev guard hook.
/// </summary>
public static class ConnectorLauncher
{
    public const string Flag = "--connector";

    /// <summary>Exit codes the bridge's log shows.</summary>
    public const int ExitNotSetUp = 2, ExitUnknown = 3, ExitStartFailed = 4;

    /// <summary>The process for <paramref name="c"/>, or the reason it can't start yet (an error key the Setup page
    /// explains too). Pure.</summary>
    public static (LaunchPlan? Plan, string? Error) Plan(ConnectorDef c, LaunchFacts f)
    {
        switch (c.Id)
        {
            case "github":
                if (f.GitHubServerExe == null) return (null, "connect.err.noServer");
                if (string.IsNullOrWhiteSpace(f.GitHubToken)) return (null, "connect.err.signIn");
                return (new LaunchPlan(f.GitHubServerExe, new[] { "stdio" },
                    new Dictionary<string, string> { [c.KeyEnv!] = f.GitHubToken.Trim() }), null);
            case "stripe":
                if (f.Npx == null) return (null, "connect.err.noNode");
                if (string.IsNullOrWhiteSpace(f.Secret)) return (null, "connect.err.noKey");
                return (new LaunchPlan(f.Npx, new[] { "-y", ConnectorCatalog.StripePackage },
                    new Dictionary<string, string> { [c.KeyEnv!] = f.Secret.Trim() }), null);
            case "canva":
                if (f.Npx == null) return (null, "connect.err.noNode");
                return (new LaunchPlan(f.Npx, new[] { "-y", ConnectorCatalog.McpRemotePackage, ConnectorCatalog.CanvaUrl },
                    new Dictionary<string, string>()), null);
            case "xdreamer":
                if (string.IsNullOrWhiteSpace(f.Secret)) return (null, "connect.err.noKey");
                return (new LaunchPlan("", Array.Empty<string>(), new Dictionary<string, string>(), ConnectorCatalog.XDreamerUrl,
                    new Dictionary<string, string> { ["Authorization"] = "Bearer " + f.Secret.Trim() }), null);
            default:
                return (null, "connect.err.unknown");
        }
    }

    /// <summary>Where CluadeX keeps the GitHub server it downloaded (one current copy).</summary>
    public static string GitHubServerPath(string dataRoot) =>
        Path.Combine(dataRoot, "connectors", "github-mcp-server", ConnectorCatalog.GitHubServerExe);

    public static string? FindNpx() => SystemSetupService.FindExe(SystemSetupService.FreshPath(), new[] { "npx.cmd" },
        new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npx.cmd") }, File.Exists);

    public static string? FindGh() => SystemSetupService.FindExe(SystemSetupService.FreshPath(), new[] { "gh.exe" },
        new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe") }, File.Exists);

    /// <summary>`gh auth token` for github.com — null when gh is missing or signed out.</summary>
    public static string? GhToken(string? gh, TimeSpan? timeout = null)
    {
        if (gh == null) return null;
        try
        {
            var psi = new ProcessStartInfo(gh, "auth token --hostname github.com")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.Environment["PATH"] = SystemSetupService.FreshPath();
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds)) { try { p.Kill(true); } catch { } return null; }
            string token = output.Result.Trim();
            return p.ExitCode == 0 && token.Length >= 20 && !token.Contains(' ') ? token : null;
        }
        catch { return null; }
    }

    /// <summary>Look up what this start needs (only what this connector uses: no gh call for Stripe).</summary>
    public static LaunchFacts Gather(ConnectorDef c, string dataRoot)
    {
        string? npx = c.Needs == ConnectorNeed.Node ? FindNpx() : null;
        string? server = null, token = null, secret = null;
        if (c.Auth == ConnectorAuth.GhCli)
        {
            string path = GitHubServerPath(dataRoot);
            server = File.Exists(path) ? path : null;
            token = GhToken(FindGh());
        }
        if (c.Auth == ConnectorAuth.ApiKey) secret = new ConnectorSecrets(dataRoot).Get(c.Id);
        return new LaunchFacts(npx, server, token, secret);
    }

    /// <summary>The headless entry point: start the server and relay stdio until either side ends.</summary>
    public static int Run(string[] args)
    {
        var stderr = Console.Error;
        string? id = args.Length > 1 ? args[1] : null;
        var c = ConnectorCatalog.Find(id);
        if (c == null) { stderr.WriteLine($"[CluadeX connector] unknown connection '{id}'."); return ExitUnknown; }

        string dataRoot = SettingsService.ResolveDataRoot(out _);
        var (plan, error) = Plan(c, Gather(c, dataRoot));
        if (plan == null)
        {
            // Plain words for the bridge's log; the Setup page shows the same reason in the user's language.
            stderr.WriteLine($"[CluadeX connector] {c.Name} is not set up yet ({error}). Open CluadeX > Setup > Connections > {c.Name}.");
            return ExitNotSetUp;
        }

        if (plan.IsRemote)
        {
            try
            {
                using var stdinRemote = Console.OpenStandardInput();
                using var stdoutRemote = Console.OpenStandardOutput();
                return RelayHttpAsync(new Uri(plan.Url!), plan.Headers ?? new Dictionary<string, string>(), stdinRemote, stdoutRemote, stderr, c.Name)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex) { stderr.WriteLine($"[CluadeX connector] {c.Name}: {ex.Message}"); return ExitStartFailed; }
        }

        var psi = new ProcessStartInfo(plan.Exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in plan.Args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = SystemSetupService.FreshPath();
        foreach (var (k, v) in plan.Env) psi.Environment[k] = v;

        Process? child;
        try { child = Process.Start(psi); }
        catch (Exception ex) { stderr.WriteLine($"[CluadeX connector] could not start {c.Name}: {ex.Message}"); return ExitStartFailed; }
        if (child == null) return ExitStartFailed;

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var errOut = Console.OpenStandardError();
        var toChild = Pump(stdin, child.StandardInput.BaseStream, closeTarget: true);
        var fromChild = Pump(child.StandardOutput.BaseStream, stdout, closeTarget: false);
        var errors = Pump(child.StandardError.BaseStream, errOut, closeTarget: false);

        // Whoever started us went away (stdin closed): the server goes too — no orphaned node/npx processes.
        int done = Task.WaitAny(toChild, Task.Run(() => child.WaitForExit()));
        if (done == 0 && !child.HasExited)
        {
            if (!child.WaitForExit(3000)) { try { child.Kill(true); } catch { } }
        }
        try { Task.WaitAll(new[] { fromChild, errors }, 2000); } catch { }
        return child.HasExited ? child.ExitCode : 0;
    }

    /// <summary>
    /// A service on the network, spoken to as if it were a local stdio server: newline-delimited JSON-RPC in on
    /// <paramref name="input"/>, each message POSTed (MCP Streamable HTTP), every answer written back as one line on
    /// <paramref name="output"/>. Requests run side by side (a 45-second wait for a video must not hold up a quick
    /// call); initialize and notifications go in order. A session the server forgot is re-initialized once, with the
    /// client's own initialize, and the request retried. A request that cannot be delivered gets a JSON-RPC error with
    /// its own id, so the client never waits for an answer that will not come. Returns when the input ends.
    /// </summary>
    public static async Task<int> RelayHttpAsync(Uri url, IReadOnlyDictionary<string, string> headers, Stream input, Stream output,
        TextWriter log, string serviceName, HttpMessageHandler? handler = null)
    {
        using var client = new Mcp.McpHttpClient(url, headers, handler);
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeGate = new SemaphoreSlim(1, 1);
        var running = new List<Task>();
        string? initializeLine = null;
        var reinit = new SemaphoreSlim(1, 1);
        int unauthorizedLogged = 0;

        async Task Write(string line)
        {
            await writeGate.WaitAsync();
            try { await writer.WriteLineAsync(line); }
            finally { writeGate.Release(); }
        }

        async Task<IReadOnlyList<string>> SendWithRecovery(string line)
        {
            try { return await client.SendAsync(line, CancellationToken.None); }
            catch (Mcp.McpSessionExpiredException) when (initializeLine != null)
            {
                log.WriteLine($"[CluadeX connector] {serviceName}: the server's session expired — initializing again");
                await reinit.WaitAsync();
                try
                {
                    if (client.SessionId == null)
                    {
                        await client.SendAsync(initializeLine, CancellationToken.None);
                        await client.SendAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", CancellationToken.None);
                    }
                }
                finally { reinit.Release(); }
                return await client.SendAsync(line, CancellationToken.None);
            }
        }

        async Task Handle(string line, string? idJson)
        {
            try
            {
                foreach (var answer in await SendWithRecovery(line)) await Write(answer);
            }
            catch (Exception ex)
            {
                string why = ex is Mcp.McpUnauthorizedException
                    ? $"{serviceName} refused the key — check it on {url.Host} and connect again in CluadeX ({ex.Message})"
                    : $"{serviceName} could not be reached: {ex.Message}";
                if (ex is not Mcp.McpUnauthorizedException || Interlocked.Exchange(ref unauthorizedLogged, 1) == 0)
                    log.WriteLine($"[CluadeX connector] {why}");
                if (idJson != null)
                    await Write($"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":-32000,\"message\":{JsonSerializer.Serialize(why)}}}}}");
            }
        }

        using var reader = new StreamReader(input, new UTF8Encoding(false));
        string? raw;
        while ((raw = await reader.ReadLineAsync()) != null)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            string? method = null, idJson = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String) method = m.GetString();
                    if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                        idJson = id.GetRawText();
                }
            }
            catch (JsonException)
            {
                log.WriteLine("[CluadeX connector] dropped a line that is not JSON");
                continue;
            }

            if (method == "initialize") initializeLine = line;
            // In order: the handshake (the session id comes back on it) and notifications (initialized must land
            // before the requests that follow it). Requests: side by side.
            if (method == "initialize" || idJson == null)
            {
                await Handle(line, idJson);
                continue;
            }
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => Handle(line, idJson)));
        }
        // The client closed its side: let the calls in flight finish (bounded), then end the session politely.
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromMinutes(5)); } catch { }
        await client.CloseAsync();
        return 0;
    }

    /// <summary>Copy bytes as they arrive, flushing each chunk (an MCP message must not sit in a buffer).</summary>
    private static Task Pump(Stream from, Stream to, bool closeTarget) => Task.Run(() =>
    {
        var buf = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                to.Write(buf, 0, n);
                to.Flush();
            }
        }
        catch { /* either side closed */ }
        finally { if (closeTarget) { try { to.Close(); } catch { } } }
    });
}
