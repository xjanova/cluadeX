// tokenaudit — what CluadeX sends a model before the conversation, measured from the REAL CluadeX.dll. Offline: no API
// call, no model load, no MCP server (portable.txt keeps every file inside this harness's bin folder).
//
//   1. the system prompt, section by section, for each provider / backend shape (API native, [ACTION:], local ctx tiers)
//   2. the native tool schemas, per tool and per group (built-in / module), and the deferred-names note
//   3. what makes the system prompt change between two turns (git status, a new file, a saved memory, the track record)
//   4. the wrapper lines a CLI agent gets on every turn (Claude Dev's --append-system-prompt, the chat-tools hints)
//
// Build CluadeX first (the app may be running: build it with -p:OutDir=<dir>\), then:
//   dotnet run --project tests/tokenaudit -p:CluadeXBin=<dir>\ -- [--repo <project to measure>] [--mcp-names <file>]
// Report: bin/Debug/net8.0-windows/token-report.md (also printed). Token counts are CluadeX's own estimate
// (TokenBudget: ASCII/4 + 1 per non-ASCII char) — the app logs the same numbers as sysPrompt~N.

using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Helpers;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;
using Microsoft.Extensions.DependencyInjection;

string? repoArg = null, mcpNamesFile = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--repo" && i + 1 < args.Length) repoArg = args[++i];
    else if (args[i] == "--mcp-names" && i + 1 < args.Length) mcpNamesFile = args[++i];
}

var configure = typeof(CluadeX.App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
var services = new ServiceCollection();
configure.Invoke(null, new object[] { services });
var provider = services.BuildServiceProvider();
var settings = provider.GetRequiredService<SettingsService>();
if (!settings.DataRoot.StartsWith(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("ABORT: data root is not isolated (portable.txt missing): " + settings.DataRoot);
    return 3;
}
var fs = provider.GetRequiredService<FileSystemService>();
var tools = provider.GetRequiredService<AgentToolService>();
var agent = provider.GetRequiredService<CodeAgentService>();
var pm = provider.GetRequiredService<AiProviderManager>();
var memory = provider.GetRequiredService<MemoryService>();
var report = new StringBuilder();
void Out(string s = "") { Console.WriteLine(s); report.AppendLine(s); }
int Tok(string? s) => TokenBudget.EstimateTokens(s);

// Point the manager at a provider WITHOUT initialising it (Initialize would test a connection / start a server).
var activeField = typeof(AiProviderManager).GetField("_activeProvider", BindingFlags.NonPublic | BindingFlags.Instance)!;
void Use(AiProviderType type)
{
    settings.Settings.ActiveProvider = type;
    activeField.SetValue(pm, pm.GetProvider(type));
    agent.InvalidateSystemPromptCache();
}

// ─── Sections: a line that starts a block in BuildBaseSystemPrompt / GetSystemPrompt ───
var headerRx = new Regex(@"^(#{1,3} .+|PROJECT STRUCTURE.*|CODEBASE MAP.*|KEY PROJECT FILES:|--- .+ ---|DETECTED PROJECT TYPE:.*|EXAMPLE of .*|NOTE: .{0,40}|You ALSO have MCP tools.{0,20}|\[ACTION.{0,30})$");
List<(string Name, string Text)> Sections(string prompt)
{
    var list = new List<(string, string)>();
    string name = "(head)";
    var sb = new StringBuilder();
    foreach (var raw in prompt.Split('\n'))
    {
        string line = raw.TrimEnd('\r');
        var m = headerRx.Match(line.Length > 120 ? line[..120] : line);
        if (m.Success && !line.StartsWith("[ACTION") && line.Length > 0 && sb.Length > 0)
        {
            list.Add((name, sb.ToString()));
            sb.Clear();
            name = line.Length > 70 ? line[..70] + "…" : line;
        }
        else if (m.Success && sb.Length == 0) name = line.Length > 70 ? line[..70] + "…" : line;
        sb.AppendLine(line);
    }
    if (sb.Length > 0) list.Add((name, sb.ToString()));
    return list;
}

void SectionTable(string title, string prompt, int top = 40)
{
    Out($"\n### {title}: {prompt.Length:N0} chars ≈ {Tok(prompt):N0} tokens");
    Out("| section | chars | ≈tokens | % |");
    Out("|---|---:|---:|---:|");
    int total = Math.Max(1, Tok(prompt));
    // Merge sections with the same name (e.g. several "--- file ---").
    foreach (var s in Sections(prompt).OrderByDescending(s => Tok(s.Text)).Take(top))
        Out($"| {s.Name.Replace("|", "/")} | {s.Text.Length:N0} | {Tok(s.Text):N0} | {Tok(s.Text) * 100.0 / total:0.0} |");
}

// ─── Project: the real repo (read-only) and a throwaway git repo for the volatility test ───
string repo = repoArg ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
Out($"# CluadeX token audit — {DateTime.Now:yyyy-MM-dd HH:mm} · CluadeX {typeof(CluadeX.App).Assembly.GetName().Version}");
Out($"project measured: `{repo}`");

settings.Settings.Features.GitIntegration = true;
fs.WorkingDirectory = repo;

// 1 ── system prompt per provider shape ──────────────────────────────────────
Out("\n## 1. System prompt per provider shape (this repo open, no brain connected, no memory)");
var shapes = new (string Label, AiProviderType Type, int Ctx, bool LocalNative)[]
{
    ("Anthropic API (native tool_use)", AiProviderType.Anthropic, 16384, true),
    ("Gemini API", AiProviderType.Gemini, 16384, true),
    ("OpenAI API", AiProviderType.OpenAI, 16384, true),
    ("llama-server native, ctx 16384", AiProviderType.LlamaServer, 16384, true),
    ("llama-server native, ctx 8192", AiProviderType.LlamaServer, 8192, true),
    ("LLamaSharp [ACTION:] loop, ctx 16384", AiProviderType.Local, 16384, false),
    ("Ollama, ctx 16384", AiProviderType.Ollama, 16384, false),
};
var promptOf = new Dictionary<string, string>();
Out("| shape | native tools | chars | ≈tokens |");
Out("|---|---|---:|---:|");
foreach (var sh in shapes)
{
    settings.Settings.ContextSize = (uint)sh.Ctx;
    settings.Settings.LocalNativeToolUseEnabled = sh.LocalNative;
    Use(sh.Type);
    bool native = pm.ActiveProvider?.SupportsNativeToolUse ?? false;
    string p = agent.GetSystemPrompt();
    promptOf[sh.Label] = p;
    Out($"| {sh.Label} | {native} | {p.Length:N0} | {Tok(p):N0} |");
}
SectionTable("Anthropic API — sections", promptOf["Anthropic API (native tool_use)"]);
SectionTable("LLamaSharp [ACTION:] ctx 16384 — sections", promptOf["LLamaSharp [ACTION:] loop, ctx 16384"], 15);
SectionTable("llama-server ctx 8192 — sections", promptOf["llama-server native, ctx 8192"], 15);

// 2 ── tool schemas ─────────────────────────────────────────────────────────
settings.Settings.ContextSize = 16384;
Use(AiProviderType.Anthropic);
var schemas = tools.BuildNativeToolSchemas();
int SchemaTok(ToolSchema t) => Tok(t.Name + " " + t.Description + " " + (t.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "" : t.InputSchema.GetRawText()));
Out($"\n## 2. Native tool schemas (no MCP server connected): {schemas.Count} tools ≈ {schemas.Sum(SchemaTok):N0} tokens");
Out($"deferrable (DeferLoading) among them: {schemas.Count(t => t.DeferLoading)}");
Out("| tool | ≈tokens | description chars | schema chars |");
Out("|---|---:|---:|---:|");
foreach (var t in schemas.OrderByDescending(SchemaTok).Take(25))
    Out($"| {t.Name} | {SchemaTok(t):N0} | {t.Description.Length:N0} | {t.InputSchema.GetRawText().Length:N0} |");
Out($"\nsmallest 10 total ≈ {schemas.OrderBy(SchemaTok).Take(10).Sum(SchemaTok)} tokens; median ≈ {schemas.Select(SchemaTok).OrderBy(x => x).ElementAt(schemas.Count / 2)} tokens/tool");
long descTok = schemas.Sum(t => Tok(t.Description)), inputTok = schemas.Sum(t => Tok(t.InputSchema.GetRawText()));
Out($"split: descriptions ≈ {descTok:N0} tokens, input_schema JSON ≈ {inputTok:N0} tokens");

// What a lean description would cost: first sentence only.
long firstSentence = schemas.Sum(t => Tok(t.Name + " " + FirstSentence(t.Description) + " " + t.InputSchema.GetRawText()));
Out($"if every description were cut to its first sentence: ≈ {firstSentence:N0} tokens (−{schemas.Sum(SchemaTok) - firstSentence:N0})");

string textCatalogue = tools.GetToolDefinitionsPrompt();
Out($"[ACTION:] text catalogue (GetToolDefinitionsPrompt): {textCatalogue.Length:N0} chars ≈ {Tok(textCatalogue):N0} tokens");

// Deferred names note for the real MCP tool names (a file of names, one per line), as AnthropicProvider sends it.
if (mcpNamesFile != null && File.Exists(mcpNamesFile))
{
    var fake = File.ReadAllLines(mcpNamesFile).Where(l => l.Trim().Length > 0)
        .Select(n => new ToolSchema { Name = n.Trim(), Description = "", InputSchema = System.Text.Json.JsonDocument.Parse("{}").RootElement, DeferLoading = true })
        .ToList();
    string note = AnthropicProvider.DeferredToolsNote(fake);
    Out($"deferred-tools note for {fake.Count} real MCP tool names: {note.Length:N0} chars ≈ {Tok(note):N0} tokens (in the system prompt on every request)");
}

// 3 ── volatility: what changes the system prompt between two turns ───────────
Out("\n## 3. What changes the system prompt between turns (throwaway git repo)");
string scratch = Path.Combine(AppContext.BaseDirectory, "vol-" + DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(Path.Combine(scratch, "src"));
File.WriteAllText(Path.Combine(scratch, "README.md"), "# Demo\nA small demo project.\n");
File.WriteAllText(Path.Combine(scratch, "src", "Calc.cs"), "namespace Demo;\npublic class Calc\n{\n    public int Add(int a, int b) => a + b;\n}\n");
File.WriteAllText(Path.Combine(scratch, "Demo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n");
Git(scratch, "init -q"); Git(scratch, "add -A"); Git(scratch, "-c user.email=a@b -c user.name=t commit -qm init");
fs.WorkingDirectory = scratch;
Use(AiProviderType.Anthropic);
string p0 = agent.GetSystemPrompt();
string again = agent.GetSystemPrompt();
Out($"- rebuilt with nothing changed: {(p0 == again ? "identical" : "DIFFERENT")}");
void Step(string what, Action change)
{
    string before = agent.GetSystemPrompt();
    change();
    agent.InvalidateSystemPromptCache();
    string after = agent.GetSystemPrompt();
    if (before == after) { Out($"- {what}: no change"); return; }
    var b = Sections(before).ToDictionary(s => s.Name, s => s.Text);
    var a = Sections(after);
    var changed = a.Where(s => !b.TryGetValue(s.Name, out var t) || t != s.Text).Select(s => s.Name).ToList();
    changed.AddRange(b.Keys.Where(k => a.All(s => s.Name != k)).Select(k => k + " (removed)"));
    Out($"- {what}: CHANGED · sections: {string.Join(" · ", changed.Distinct())} · first difference at char {FirstDiff(before, after):N0} of {after.Length:N0}");
}
Step("edit a tracked file (what every coding turn does)", () => File.AppendAllText(Path.Combine(scratch, "src", "Calc.cs"), "// edited\n"));
Step("add a new file with a class", () => File.WriteAllText(Path.Combine(scratch, "src", "Mul.cs"), "namespace Demo;\npublic class Mul { public int Times(int a, int b) => a * b; }\n"));
Step("commit (status back to clean)", () => { Git(scratch, "add -A"); Git(scratch, "-c user.email=a@b -c user.name=t commit -qm two"); });
// A saved memory is NOT exercised here: MemoryService keeps memories under the real ~/.cluadex (it does not follow
// portable.txt), so a save from this harness would land in the owner's memory. The memory index is read on every
// GetSystemPrompt (CodeAgentService "# Memory"), so a memory_save changes the next turn's prompt — read from the code.
Out($"- memory index in this prompt (read from the owner's ~/.cluadex/memory, read-only): ≈ {Tok(memory.LoadMemoryIndex())} tokens");
Out($"- Date line as sent: `{Regex.Match(p0, @"- Date: [^\r\n]+").Value}` (current culture {System.Globalization.CultureInfo.CurrentCulture.Name}; changes once a day)");

// Where the volatile lines sit: everything after the first volatile line is rewritten when it changes.
int gitAt = p0.IndexOf("- Git Status", StringComparison.Ordinal), dateAt = p0.IndexOf("- Date:", StringComparison.Ordinal);
Out($"- position of '- Date:' {dateAt:N0}, '- Git Status' {gitAt:N0}, of {p0.Length:N0} chars (stable text after it is rewritten with it)");

// 4 ── CLI wrappers ───────────────────────────────────────────────────────────
Out("\n## 4. What a CLI agent gets from CluadeX on every turn");
string cfgPath = Path.Combine(scratch, "tools.json");
string worker = ClaudeDevProvider.BuildArgs(scratch, new ClaudeDevProvider.WorkerGrant(true, true), "opus", "medium", toolsConfig: cfgPath,
    splitGuide: CluadeX.Services.Agents.WorkflowGate.SplitGuide(AiProviderType.ClaudeDev));
string advice = ClaudeDevProvider.BuildArgs(scratch, null, "opus", "medium", toolsConfig: cfgPath);
Out($"- Claude Dev worker argv (--append-system-prompt + flags): {worker.Length:N0} chars ≈ {Tok(worker):N0} tokens");
Out($"- Claude Dev read-only argv: {advice.Length:N0} chars ≈ {Tok(advice):N0} tokens");
foreach (var n in Enum.GetValues<CluadeX.Services.Tools.ChatToolBridge.CliToolNames>())
{
    string h = CluadeX.Services.Tools.ChatToolBridge.ChatToolsHint(n, splitChats: true, plan: true);
    Out($"- ChatToolsHint({n}, split, plan): {h.Length:N0} chars ≈ {Tok(h):N0} tokens");
}
Out($"- OpenProjectHint(no project): ≈ {Tok(CluadeX.Services.Tools.ChatToolBridge.OpenProjectHint(true))} tokens");
foreach (var t in new[] { AiProviderType.ClaudeDev, AiProviderType.OpenAIDev, AiProviderType.GeminiDev, AiProviderType.GrokDev, AiProviderType.AntigravityDev })
    Out($"- WorkflowGate.SplitGuide({t}) (Ultra chats only): ≈ {Tok(CluadeX.Services.Agents.WorkflowGate.SplitGuide(t))} tokens");
var hist = Enumerable.Range(0, 40).Select(i => new ChatMessage { Role = i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, Content = new string('x', 2400) }).ToList();
var bt = typeof(ClaudeDevProvider).GetMethod("BuildTranscript", BindingFlags.NonPublic | BindingFlags.Static);
if (bt != null) { string tr = (string)bt.Invoke(null, new object[] { hist })!; Out($"- replayed transcript cap (40 × 600-token messages): {tr.Length:N0} chars ≈ {Tok(tr):N0} tokens per CLI turn"); }

try { Directory.Delete(scratch, true); } catch { }
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "token-report.md"), report.ToString(), new UTF8Encoding(false));
Console.WriteLine($"\nreport: {Path.Combine(AppContext.BaseDirectory, "token-report.md")}");
return 0;

static string FirstSentence(string s)
{
    int i = s.IndexOf(". ", StringComparison.Ordinal);
    return i > 0 ? s[..(i + 1)] : s;
}
static int FirstDiff(string a, string b)
{
    int n = Math.Min(a.Length, b.Length);
    for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
    return n;
}
static void Git(string dir, string args)
{
    var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
    p.WaitForExit(15000);
}
