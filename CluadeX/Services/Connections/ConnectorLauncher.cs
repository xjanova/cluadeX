using System.Diagnostics;
using System.IO;
using System.Text;
using CluadeX.Services.Setup;

namespace CluadeX.Services.Connections;

/// <summary>The exact process a connector runs: what to start, with which arguments, and which environment variables
/// (the secret ones included). Built fresh at every start; never written to any config file.</summary>
public sealed record LaunchPlan(string Exe, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env);

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
