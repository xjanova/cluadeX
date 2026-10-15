using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace AgentTest;

/// <summary>
/// One real agent session, wired exactly like the app: CluadeX's own DI registration (App.ConfigureServices),
/// the real CodeAgentService loop, the real tools, the real BrainX MCP server, a live Claude model. It works on
/// a throwaway CLONE of a real project (its remote removed — nothing can be pushed), so the original is never
/// touched. Every permission prompt the user would see is approved and listed in the report.
///
///   dotnet run -- --project D:\GameProject\Snooker2D [--model claude-sonnet-5-5] [--no-brain]
///                 [--prompt "…" | --prompt-file task.txt] [--max-iterations 20]
///
/// Costs real API money (a few cents to a few dollars per run, depending on the model and the task).
/// </summary>
internal static class Program
{
    static readonly StringBuilder Report = new();

    static void Log(string line)
    {
        Console.WriteLine(line);
        Report.AppendLine(line);
    }

    static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log("FATAL " + ex); return 2; }
        finally
        {
            string path = Path.Combine(AppContext.BaseDirectory, "agent-report.md");
            File.WriteAllText(path, Report.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"\nreport: {path}");
        }
    }

    static string Arg(string[] args, string name, string fallback = null)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    static async Task<int> Run(string[] args)
    {
        string source = Arg(args, "--project") ?? throw new ArgumentException("--project <git repo> is required");
        string model = Arg(args, "--model", "claude-sonnet-5-5");
        bool useBrain = !args.Contains("--no-brain");
        string prompt = Arg(args, "--prompt")
            ?? (Arg(args, "--prompt-file") is { } pf ? File.ReadAllText(pf) : null)
            ?? throw new ArgumentException("--prompt or --prompt-file is required");

        var (key, ws) = ReadUserAnthropicConfig();
        if (string.IsNullOrWhiteSpace(key)) { Log("SKIP — no Anthropic key saved in CluadeX settings"); return 1; }

        // ── throwaway clone, remote removed ──
        string work = Path.Combine(Path.GetTempPath(), "cluadex-agenttest-" + Guid.NewGuid().ToString("N")[..8]);
        Git(null, $"clone --quiet \"{source}\" \"{work}\"");
        Git(work, "remote remove origin");
        Git(work, "config user.email agenttest@localhost");
        Git(work, "config user.name agenttest");
        Log($"# Agent session on a clone of {source}\n\nclone: {work} (no remote) · model: {model} · BrainX: {(useBrain ? "on" : "off")}\n");
        Log("## Task\n\n" + prompt.Trim() + "\n");

        // ── the app's own DI ──
        var services = new ServiceCollection();
        typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [services]);
        using var sp = services.BuildServiceProvider();

        var settings = sp.GetRequiredService<SettingsService>();
        Directory.CreateDirectory(settings.DataRoot);
        if (useBrain)
        {
            string userCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "mcp_servers.json");
            if (File.Exists(userCfg)) File.Copy(userCfg, Path.Combine(settings.DataRoot, "mcp_servers.json"), overwrite: true);
        }
        else
        {
            File.WriteAllText(Path.Combine(settings.DataRoot, "mcp_servers.json"), "{\"mcpServers\":{}}");
        }
        settings.UpdateSettings(s =>
        {
            s.ActiveProvider = AiProviderType.Anthropic;
            s.ProviderConfigs["Anthropic"] = new ProviderConfig { ApiKey = key, WorkspaceId = ws, SelectedModel = model };
            s.Language = "th";
            if (Arg(args, "--max-iterations") is { } mi) s.MaxAgentIterations = int.Parse(mi);
        });

        sp.GetRequiredService<FileSystemService>().WorkingDirectory = work;
        var agent = sp.GetRequiredService<CodeAgentService>();
        agent.InvalidateSystemPromptCache();

        var permissionPrompts = new List<string>();
        sp.GetRequiredService<AgentToolService>().OnPermissionRequired += info =>
        {
            lock (permissionPrompts) permissionPrompts.Add($"{info.ToolName}: {info.Detail}");
            Console.WriteLine($"  [permission asked → approved] {info.ToolName}: {info.Detail}");
            return Task.FromResult(true);
        };

        var sw = Stopwatch.StartNew();
        if (useBrain)
        {
            var mcp = sp.GetRequiredService<McpServerManager>();
            await mcp.InitializeAsync();
            Log($"BrainX: {(mcp.IsBrainConnected ? $"connected · {mcp.BrainToolCount} tools" : "NOT connected")} ({sw.ElapsedMilliseconds} ms) · {mcp.BrainStatusText}");
        }

        var providers = sp.GetRequiredService<AiProviderManager>();
        await providers.SwitchProviderAsync(AiProviderType.Anthropic);
        Log($"provider: {providers.ActiveProvider?.ProviderId} ready={providers.ActiveProvider?.IsReady} nativeTools={providers.ActiveProvider?.SupportsNativeToolUse}\n");

        // ── the turn ──
        var progress = new Progress<string>(p => Console.WriteLine("  · " + p.Replace('\n', ' ')));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        sw.Restart();
        var result = await agent.ExecuteAgenticAsync(new List<ChatMessage>(), prompt, progress, cts.Token);
        long ms = sw.ElapsedMilliseconds;

        // ── what happened ──
        Log($"\n## Result\n\nsuccess={result.Success} · stop={result.StopReason} · turns={result.TurnCount} · tool calls={result.TotalToolCalls} · {ms / 1000.0:0.0} s");
        var cost = sp.GetRequiredService<CostTrackingService>();
        Log($"tokens in={cost.TotalInputTokens} out={cost.TotalOutputTokens} · cost ≈ ${cost.TotalCostUsd:0.000}\n");

        Log("## Tool calls\n");
        Log("| # | tool | argument | ok | result |");
        Log("|---|------|----------|----|--------|");
        int n = 0;
        var used = new Dictionary<string, int>();
        foreach (var step in result.Steps)
        {
            for (int i = 0; i < step.ToolCalls.Count; i++)
            {
                var call = step.ToolCalls[i];
                var res = i < step.ToolResults.Count ? step.ToolResults[i] : null;
                string name = call.McpToolName != null ? $"{call.McpServerName}:{call.McpToolName}" : call.ToolName;
                used[name] = used.GetValueOrDefault(name) + 1;
                string arg = call.Arguments.Values.FirstOrDefault() ?? "";
                string output = res?.Output ?? res?.Error ?? "";
                Log($"| {++n} | {name} | {Cell(arg, 70)} | {(res == null ? "?" : res.Success ? "✓" : "✗")} | {Cell(output, 110)} |");
            }
        }
        Log("\nby tool: " + string.Join(", ", used.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}")));
        bool brainUsed = used.Keys.Any(k => k.Contains("brain", StringComparison.OrdinalIgnoreCase));
        Log($"brain consulted: {brainUsed}");

        Log("\n## Permission prompts the user would have seen\n");
        Log(permissionPrompts.Count == 0 ? "(none)" : string.Join("\n", permissionPrompts.Select(p => "- " + p)));

        Log("\n## Final answer\n");
        Log(result.FinalResponse);

        Log("\n## Changes in the clone\n");
        Log("```\n" + Git(work, "status --short") + Git(work, "diff --stat") + "```");
        string diff = Git(work, "diff");
        Log("```diff\n" + (diff.Length > 12000 ? diff[..12000] + "\n…(truncated)" : diff) + "\n```");

        // A static web project: every inline <script> must still parse.
        foreach (var html in Directory.EnumerateFiles(work, "*.html", SearchOption.AllDirectories)
                     .Where(p => !p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)))
        {
            string text = File.ReadAllText(html);
            int idx = 0, block = 0;
            while ((idx = text.IndexOf("<script", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int open = text.IndexOf('>', idx);
                int close = text.IndexOf("</script>", open, StringComparison.OrdinalIgnoreCase);
                if (open < 0 || close < 0) break;
                string tag = text[idx..open];
                string body = text[(open + 1)..close];
                idx = close + 9;
                if (tag.Contains("src=", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(body)) continue;
                block++;
                string tmp = Path.Combine(Path.GetTempPath(), $"agenttest-{Guid.NewGuid():N}.{(tag.Contains("module") ? "mjs" : "js")}");
                File.WriteAllText(tmp, body);
                var (code, output) = RunProcess("node", $"--check \"{tmp}\"", null);
                File.Delete(tmp);
                Log($"node --check {Path.GetRelativePath(work, html)} script #{block}: {(code == 0 ? "OK" : "SYNTAX ERROR\n" + output)}");
            }
        }
        return 0;
    }

    static string Cell(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
        return s.Length <= max ? s : s[..max] + "…";
    }

    static string Git(string cwd, string args)
    {
        var (code, output) = RunProcess("git", args, cwd);
        if (code != 0) throw new InvalidOperationException($"git {args} failed: {output}");
        return output;
    }

    static (int, string) RunProcess(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (cwd != null) psi.WorkingDirectory = cwd;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output + err.Result);
    }

    static (string Key, string Workspace) ReadUserAnthropicConfig()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cluadex", "settings.json");
        if (!File.Exists(path)) return (null, null);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("ProviderConfigs", out var cfgs) || !cfgs.TryGetProperty("Anthropic", out var a))
            return (null, null);
        string ws = a.TryGetProperty("WorkspaceId", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        if (!a.TryGetProperty("ApiKey", out var k) || k.ValueKind != JsonValueKind.String) return (null, ws);
        string stored = k.GetString();
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith("ENC:")) return (stored, ws);
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(stored[4..]), null, DataProtectionScope.CurrentUser);
        return (Encoding.UTF8.GetString(plain), ws);
    }
}
