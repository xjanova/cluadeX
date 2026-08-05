using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// "claude-dev" — the Claude Code CLI already installed on this machine, exposed as a
/// selectable CluadeX model. No API key: the CLI authenticates with the user's existing
/// Claude login, so picking this provider literally is "คุยกับ Claude" inside CluadeX.
///
/// Design decisions, in order of how much they matter:
///
/// - The CLI is ITS OWN AGENT. It plans, reads files, searches the brain (the user's
///   ~/.claude.json registers brainx-brain globally) and verifies — so CluadeX must NOT
///   wrap it in either of its agent loops. SupportsNativeToolUse stays false and
///   ExecuteAgenticAsync short-circuits for this provider: one delegated call per turn.
///   Wrapping one agent in another agent's loop is how you get two planners fighting.
///
/// - The prompt travels over STDIN, not argv. Thai text, quotes and multi-line specs
///   break Windows argv quoting long before they break a UTF-8 stream, and stdin has no
///   length ceiling.
///
/// - Headless `claude -p` cannot answer permission prompts, so the tool surface is
///   pinned to a read-only allowlist (file reads + brain recall). Anything write-shaped
///   is proposed as text/diffs for CluadeX's own loop to apply — this provider advises
///   with full context; it does not mutate the disk on its own.
///
/// - History is replayed as a compact transcript inside the prompt. Each -p call is a
///   fresh CLI session; `--continue` was rejected because it binds to "the last session
///   in this cwd", which the user's own terminal sessions would silently hijack.
/// </summary>
public class ClaudeDevProvider : IAiProvider
{
    private readonly SettingsService _settingsService;
    private readonly FileSystemService _fileSystemService;

    private string? _cliPath;
    private string _statusMessage = "Not initialised";
    private bool _isLoading;

    public string ProviderId => "ClaudeDev";
    public string DisplayName => "Claude Dev (Claude Code CLI)";
    public bool IsReady => _cliPath != null;
    public bool IsLoading => _isLoading;
    public string StatusMessage => _statusMessage;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    /// <summary>Read-only tools the headless CLI may use without a human to approve
    /// them. Everything else is denied by the CLI's own permission layer.</summary>
    private const string AllowedTools =
        "Read,Grep,Glob," +
        "mcp__brainx-brain__brain_search,mcp__brainx-brain__brain_semantic_search," +
        "mcp__brainx-brain__brain_get_note,mcp__brainx-brain__brain_expertise";

    public ClaudeDevProvider(SettingsService settingsService, FileSystemService fileSystemService)
    {
        _settingsService = settingsService;
        _fileSystemService = fileSystemService;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_cliPath != null) return;
        SetLoading(true);
        try
        {
            _cliPath = await ResolveCliAsync(ct);
            SetStatus(_cliPath != null
                ? $"Ready · {_cliPath}"
                : "Claude Code CLI not found — install it or add 'claude' to PATH");
        }
        finally { SetLoading(false); }
    }

    /// <summary>
    /// `where.exe claude` first (respects however the user installed it), then the
    /// known install roots. The .cmd shim is fine — Process.Start handles it via
    /// cmd /c when UseShellExecute is false and the file is a script, so prefer a
    /// real .exe when one exists.
    /// </summary>
    private static async Task<string?> ResolveCliAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", "claude")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string outp = await p.StandardOutput.ReadToEndAsync(ct);
                await p.WaitForExitAsync(ct);
                // .exe ONLY. npm puts an extensionless bash shim named `claude` on PATH
                // first, and Process.Start refuses it ("not a valid application") —
                // observed live. The .cmd shim would need a cmd.exe wrapper; the real
                // exe in the fallback below is strictly better than either.
                var first = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first)) return first;
            }
        }
        catch { /* fall through to known locations */ }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string claudeRoot = Path.Combine(appData, "Claude", "claude-code");
        try
        {
            if (Directory.Exists(claudeRoot))
            {
                // Versioned folders (2.1.221\claude.exe) — newest wins.
                var exe = Directory.GetDirectories(claudeRoot)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "claude.exe"))
                    .FirstOrDefault(File.Exists);
                if (exe != null) return exe;
            }
        }
        catch { }
        return null;
    }

    public async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_cliPath == null) await InitializeAsync(ct);
        if (_cliPath == null)
        {
            OnError?.Invoke("Claude Code CLI not found on this machine");
            yield return "Claude Code CLI ไม่พบบนเครื่องนี้ — ติดตั้ง Claude Code หรือเพิ่ม 'claude' เข้า PATH ก่อน";
            yield break;
        }

        string cwd = _fileSystemService.HasWorkingDirectory
            ? _fileSystemService.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The CLI builds its own context (CLAUDE.md, MCP, its system prompt). CluadeX's
        // big prompt would just be noise on top — send a three-line wrapper instead.
        string wrapper =
            "You are 'claude-dev', answering inside the CluadeX desktop app. " +
            $"Project working directory: {cwd}. " +
            "You have READ-ONLY tools plus the BrainX brain recall tools; propose file edits as diffs/snippets for the app to apply, don't try to write. " +
            "Reply in the language the user writes in.";

        var args = new StringBuilder("-p --output-format text");
        args.Append(" --append-system-prompt ").Append(Quote(wrapper));
        args.Append(" --allowedTools ").Append(Quote(AllowedTools));
        string model = _settingsService.Settings.ProviderConfigs.TryGetValue(ProviderId, out var cfg)
            && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId) ? cfg.EffectiveModelId : "";
        // "default"/"auto"/empty → NO --model flag. An explicit alias routed the call to
        // API-credit billing and died with "Credit balance is too low" on a machine whose
        // Claude login is subscription-based; the CLI's own default rides the subscription.
        if (!string.IsNullOrWhiteSpace(model)
            && !model.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            args.Append(" --model ").Append(Quote(model));

        var psi = new ProcessStartInfo(_cliPath, args.ToString())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        SetLoading(true);
        SetStatus("Claude Dev thinking…");
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null)
            {
                OnError?.Invoke("Failed to start the Claude Code CLI");
                yield break;
            }

            // Prompt = compact replayed transcript + the new message, over stdin.
            using (var stdin = new StreamWriter(proc.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                string transcript = BuildTranscript(history);
                if (transcript.Length > 0)
                {
                    await stdin.WriteLineAsync("<conversation_so_far>");
                    await stdin.WriteLineAsync(transcript);
                    await stdin.WriteLineAsync("</conversation_so_far>");
                    await stdin.WriteLineAsync();
                }
                await stdin.WriteLineAsync(userMessage);
            }

            // Hard ceiling so a wedged CLI can never hang the chat forever. Generous,
            // because a real turn with tool use routinely takes tens of seconds.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(600));

            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            string? line;
            bool any = false;
            string? lastLine = null;
            while ((line = await ReadLineOrNullAsync(proc.StandardOutput, timeout.Token)) != null)
            {
                // The CLI reports auth/billing refusals on STDOUT with a non-zero exit,
                // not on stderr — so a one-line refusal looks like a successful reply
                // unless the exit code is consulted. Hold the first line back until we
                // know; a real answer is many lines and streams normally after it.
                if (!any && LooksLikeAuthRefusal(line)) { lastLine = line; any = false; continue; }
                any = true;
                yield return line + "\n";
            }
            await proc.WaitForExitAsync(timeout.Token);

            if (!any || proc.ExitCode != 0)
            {
                string err = (await SafeAwait(stderrTask)).Trim();
                if (err.Length > 400) err = err[..400];
                if (!any)
                {
                    // "Credit balance is too low" is an ANSWER FROM ANTHROPIC — the
                    // prompt got there and came back — so it is never a plumbing
                    // problem, and printing it raw sends the user hunting in the
                    // wrong place. The CLI can say exactly what is wrong; ask it.
                    string? explained = await ExplainAuthFailureAsync(lastLine ?? err, ct);
                    yield return explained
                        ?? (string.IsNullOrWhiteSpace(err) && string.IsNullOrWhiteSpace(lastLine)
                            ? "(claude-dev returned no output)"
                            : $"claude-dev error: {(string.IsNullOrWhiteSpace(err) ? lastLine : err)}");
                }
                else if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                    OnError?.Invoke($"claude-dev exit {proc.ExitCode}: {err}");
            }
            SetStatus("Ready");
        }
        finally
        {
            SetLoading(false);
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    /// <summary>Newest-first token-budgeted replay, same shape as the local history
    /// fit: the CLI call is stateless, so the transcript IS its memory of the chat.</summary>
    private static string BuildTranscript(List<ChatMessage> history)
    {
        const int budget = 6000; // tokens — the CLI's window is huge; this bounds latency, not fit
        var kept = new List<ChatMessage>();
        int spent = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            int cost = CluadeX.Helpers.TokenBudget.EstimateTokens(history[i].Content);
            if (spent + cost > budget && kept.Count > 0) break;
            kept.Add(history[i]);
            spent += cost;
        }
        kept.Reverse();
        var sb = new StringBuilder();
        foreach (var m in kept)
        {
            string who = m.Role == MessageRole.Assistant ? "assistant" : "user";
            sb.Append('[').Append(who).Append("] ").AppendLine(m.Content.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Refusals the CLI prints on stdout instead of stderr.</summary>
    private static bool LooksLikeAuthRefusal(string line) =>
        line.Contains("Credit balance", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase)
        || line.Contains("authentication", StringComparison.OrdinalIgnoreCase)
        || line.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
        || line.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turn an auth/billing refusal into the sentence that actually fixes it, by
    /// asking `claude auth status` instead of guessing.
    ///
    /// Measured on this machine 2026-08-05: `-p` returned "Credit balance is too low"
    /// while `auth status` reported `loggedIn: true`, `authMethod: "claude.ai"` and
    /// **`subscriptionType: null`** — signed in, but on a token with no subscription
    /// attached, so every request billed to Console credits that are at zero. The raw
    /// message sends you looking for a billing page; the real fix is one re-login.
    /// </summary>
    private async Task<string?> ExplainAuthFailureAsync(string? refusal, CancellationToken ct)
    {
        if (_cliPath == null || string.IsNullOrWhiteSpace(refusal)) return null;
        if (!LooksLikeAuthRefusal(refusal)) return null;
        try
        {
            var psi = new ProcessStartInfo(_cliPath, "auth status")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string json = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            bool loggedIn = json.Contains("\"loggedIn\": true", StringComparison.OrdinalIgnoreCase);
            bool noSubscription = json.Contains("\"subscriptionType\": null", StringComparison.OrdinalIgnoreCase);

            if (loggedIn && noSubscription)
                return "claude-dev เข้าถึง Anthropic ได้แล้ว แต่ถูกปฏิเสธเรื่องการเรียกเก็บเงิน:\n"
                     + $"  {refusal.Trim()}\n\n"
                     + "`claude auth status` บอกว่า **loggedIn: true แต่ subscriptionType: null** — "
                     + "แปลว่า login ค้างอยู่บน token ที่ไม่มี subscription ผูกไว้ ทุก request จึงไปเรียกเก็บที่ "
                     + "Anthropic Console credits ซึ่งเป็นศูนย์ ไม่ใช่ที่ Claude subscription ของคุณ\n\n"
                     + "แก้ครั้งเดียวจบ (ต้องทำในเทอร์มินัลเพราะเป็นการ login):\n"
                     + "  claude auth logout\n"
                     + "  claude auth login --claudeai\n\n"
                     + "เสร็จแล้ว claude-dev ใช้ได้ทันทีโดยไม่ต้องใช้ API key.";

            if (!loggedIn)
                return "claude-dev ยังไม่ได้ login เข้า Anthropic\n\n"
                     + "รันในเทอร์มินัล:  claude auth login --claudeai";

            return $"claude-dev ถูกปฏิเสธ: {refusal.Trim()}\n\n`claude auth status`:\n{json.Trim()}";
        }
        catch { return null; }
    }

    private static async Task<string?> ReadLineOrNullAsync(StreamReader reader, CancellationToken ct)
    {
        try { return await reader.ReadLineAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    private static async Task<string> SafeAwait(Task<string> t)
    {
        try { return await t; } catch { return ""; }
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public async Task<string> GenerateAsync(
        List<ChatMessage> history, string userMessage, string? systemPrompt = null, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in ChatAsync(history, userMessage, systemPrompt, ct))
            sb.Append(chunk);
        return sb.ToString().Trim();
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (_cliPath == null) return (false, "Claude Code CLI not found (is 'claude' on PATH?)");
        try
        {
            var psi = new ProcessStartInfo(_cliPath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string v = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? (true, $"Claude Code {v}") : (false, $"exit {p.ExitCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private void SetStatus(string s) { _statusMessage = s; OnStatusChanged?.Invoke(s); }
    private void SetLoading(bool b) { _isLoading = b; OnLoadingChanged?.Invoke(b); }

    public void Dispose() { }
}
