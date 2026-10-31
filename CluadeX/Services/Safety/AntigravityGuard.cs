using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CluadeX.Services.Safety;

/// <summary>
/// <c>CluadeX.exe --cx-agy-guard</c>: the PreToolUse hook every Antigravity Dev turn carries (Google's Antigravity CLI,
/// <c>agy</c>). It is the turn's whole permission system, not an extra check: agy's headless mode cannot prompt and
/// refuses every tool that needs a permission — on agy 1.2.7 even a read of the project's own files — so the provider
/// runs it with <c>--dangerously-skip-permissions</c> and this hook decides every call (matcher "*"):
/// <list type="bullet">
/// <item>reads (view_file, list_dir, grep_search, find_by_name, the web reads) always pass;</item>
/// <item>file writes need the edit grant, commands the run grant, and both go through the same checks as Claude Dev's
///   guard (<see cref="GuardHook.Decide"/>, <see cref="GuardHook.NeverRun"/>, the red prompt);</item>
/// <item>MCP calls need a worker grant; any other tool (the browser, tools a later agy adds) needs the run grant;</item>
/// <item>nothing may write into the turn's own plugin folder (where this hook's .cmd lives).</item>
/// </list>
/// agy's hook protocol (docs/hooks + the CRT spike on agy 1.2.7, Windows): the call arrives on stdin as
/// <c>{"toolCall":{"name","args"},"workspacePaths",…}</c>; the answer is <c>{"decision":"allow"|"deny","reason"}</c> on
/// stdout. A hook that fails to run is a tool error in agy (fail closed), so every failure here is a deny or a crash.
/// The same flag with <c>--loaded &lt;file&gt;</c> is the PreInvocation hook: it writes the marker the provider checks
/// before the model's first step, proof that agy loaded the hooks at all.
/// </summary>
public static class AntigravityGuard
{
    public const string Flag = "--cx-agy-guard";

    /// <summary>The turn's settings for the hook, written next to its .cmd (guard.json): what the user granted, the
    /// project folder (for the changed-code scan) and the plugin folder nothing may write into.</summary>
    public sealed record Config(bool Edit, bool Run, string Cwd, string PluginDir);

    public enum Verdict { Allow, Deny, AskUser }

    /// <summary>The hook's decision about one call. AskUser = dangerous: allowed only after the user's yes on CluadeX's red
    /// prompt. Tool/Action describe the call for that prompt.</summary>
    public sealed record Decision(Verdict Verdict, string? Reason, string Tool = "", string Action = "");

    /// <summary>agy tools that only read (names as agy 1.2.7 lists them in init.tools; a few from Antigravity's IDE that
    /// the CLI may add).</summary>
    public static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "view_file", "view_file_outline", "view_code_item", "view_content_chunk", "list_dir", "grep_search", "find_by_name",
        "codebase_search", "read_resource", "list_resources", "read_url_content", "search_web", "finish", "ask_question",
        "wait", "wait_5_seconds", "command_status", "read_terminal",
    };

    /// <summary>agy tools that write files.</summary>
    public static bool IsWriteTool(string tool) =>
        tool is "write_to_file" or "replace_file_content" or "multi_replace_file_content"
        || tool.Contains("write", StringComparison.OrdinalIgnoreCase) || tool.Contains("replace", StringComparison.OrdinalIgnoreCase)
        || tool.Contains("edit", StringComparison.OrdinalIgnoreCase) || tool.Contains("delete", StringComparison.OrdinalIgnoreCase)
        || tool.Contains("rename", StringComparison.OrdinalIgnoreCase) || tool.Contains("move", StringComparison.OrdinalIgnoreCase);

    /// <summary>agy tools that run commands.</summary>
    public static bool IsRunTool(string tool) => tool is "run_command" or "send_command_input";

    public const string McpTool = "call_mcp_tool";

    /// <summary>The decision for one hook input. Pure apart from the changed-files scan <see cref="GuardHook.Decide"/> makes
    /// for a command that runs project code.</summary>
    public static Decision Decide(string hookJson, Config cfg)
    {
        JsonObject root;
        try { root = JsonNode.Parse(hookJson) as JsonObject ?? throw new JsonException("not an object"); }
        catch (JsonException) { return Deny("CluadeX safety: the guard could not read the tool call — refused."); }
        var call = root["toolCall"] as JsonObject;
        string tool = Str(call, "name") ?? "";
        var args = call?["args"] as JsonObject ?? new JsonObject();
        if (tool.Length == 0) return Deny("CluadeX safety: the tool call names no tool — refused.");
        string who = "Antigravity Dev " + tool;

        if (ReadTools.Contains(tool)) return new Decision(Verdict.Allow, null, who);

        // Asking the user changes nothing, so a read-only chat may ask too.
        if (tool == McpTool && Str(args, "ToolName") == Tools.ChatToolBridge.AskToolName
            && Str(args, "ServerName")?.EndsWith(Tools.ChatToolBridge.ServerName, StringComparison.Ordinal) == true)
            return new Decision(Verdict.Allow, null, who);

        if (tool == McpTool)
            return cfg.Edit || cfg.Run
                ? new Decision(Verdict.Allow, null, who)
                : Deny("CluadeX: this chat is read-only (no edit/run grant) — MCP tools are not used here. Answer from what you can read.");

        if (IsRunTool(tool))
        {
            if (!cfg.Run) return Deny("CluadeX: this run may not run commands — tell the user the command instead.");
            string command = Str(args, "CommandLine") ?? Str(args, "command") ?? Str(args, "Input") ?? "";
            if (TouchesPlugin(command, cfg.PluginDir)) return Deny("CluadeX safety: commands may not touch CluadeX's guard folder.");
            if (GuardHook.NeverRun(command) is { } never) return Deny(never);
            string cwd = Str(args, "Cwd") is { Length: > 0 } c ? c : cfg.Cwd;
            string? why = GuardHook.Decide(JsonSerializer.Serialize(new { tool_name = "Bash", tool_input = new { command }, cwd }));
            return why == null ? new Decision(Verdict.Allow, null, who, command) : new Decision(Verdict.AskUser, why, who, Cap(command));
        }

        if (IsWriteTool(tool))
        {
            if (!cfg.Edit) return Deny("CluadeX: this run may not edit files — propose the change as a diff instead.");
            string file = PathArg(args);
            if (TouchesPlugin(file, cfg.PluginDir)) return Deny("CluadeX safety: files in CluadeX's guard folder may not be written.");
            // Every string the call carries is what it may put in the file (the argument names differ per tool).
            string text = string.Join("\n", Strings(args).Where(s => !ReferenceEquals(s, file) && s != file));
            string? why = GuardHook.Decide(JsonSerializer.Serialize(new { tool_name = "Write", tool_input = new { file_path = file, content = text }, cwd = cfg.Cwd }));
            return why == null ? new Decision(Verdict.Allow, null, who, "write " + file) : new Decision(Verdict.AskUser, why, who, "write " + file);
        }

        // The browser, permission tools, anything a later agy adds: only a run that may already run commands.
        return cfg.Run
            ? new Decision(Verdict.Allow, null, who)
            : Deny($"CluadeX: `{tool}` is not allowed in this run (it needs the run grant).");
    }

    /// <summary>The hook's entry point (args after the flag): <c>--config &lt;guard.json&gt;</c> for PreToolUse,
    /// <c>--loaded &lt;marker&gt;</c> for PreInvocation. Writes the decision JSON to stdout; exit 0.</summary>
    public static int Run(string[] args)
    {
        string input;
        try
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            input = stdin.ReadToEnd();
        }
        catch { input = ""; }

        string? loaded = ArgAfter(args, "--loaded");
        if (loaded != null)
        {
            try { File.WriteAllText(loaded, DateTime.UtcNow.ToString("O")); } catch { return 1; }
            return Answer(new JsonObject());
        }

        Config? cfg = null;
        try
        {
            if (ArgAfter(args, "--config") is { } path)
                cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
        }
        catch { cfg = null; }
        if (cfg == null) return Answer(DenyJson("CluadeX safety: the guard has no settings for this run — refused."));

        Decision d;
        try { d = Decide(input, cfg); }
        catch (Exception ex) { d = Deny("CluadeX safety: the guard failed (" + ex.GetType().Name + ") — refused to be safe."); }
        if (d.Verdict == Verdict.Allow) return Answer(new JsonObject { ["decision"] = "allow" });
        if (d.Verdict == Verdict.AskUser && DangerApprovalPipe.Ask(d.Tool, d.Action, d.Reason ?? ""))
            return Answer(new JsonObject { ["decision"] = "allow" });
        string reason = d.Verdict == Verdict.AskUser
            ? d.Reason + "\nNot approved: the user did not say yes on CluadeX's red prompt (or CluadeX is not running to ask). "
              + "Do not retry or work around this; tell the user what you wanted to run and why."
            : d.Reason ?? "CluadeX safety: refused.";
        return Answer(DenyJson(reason));
    }

    /// <summary>The hooks.json of a turn's plugin: PreToolUse on every tool and the PreInvocation marker, both through the
    /// absolute path of a .cmd (agy runs hook commands with cmd /c, which mangles a quoted exe path — CRT spike).</summary>
    public static JsonObject HooksJson(string guardCmd, string loadedCmd) => new()
    {
        ["cluadex-guard"] = new JsonObject
        {
            ["PreToolUse"] = new JsonArray
            {
                new JsonObject
                {
                    ["matcher"] = "*",
                    // Seconds (agy's unit): long enough for the user to read the red prompt and answer (it waits 280 s).
                    ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = guardCmd, ["timeout"] = 300 } },
                },
            },
            ["PreInvocation"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = loadedCmd, ["timeout"] = 30 } },
        },
    };

    /// <summary>The .cmd wrappers' text: only ASCII (cmd reads a .cmd in the OEM code page) — the exe by its short path,
    /// the rest relative to the .cmd itself (%~dp0).</summary>
    public static string GuardCmd(string exe) => $"@\"{exe}\" {Flag} --config \"%~dp0guard.json\"\r\n";
    public static string LoadedCmd(string exe) => $"@\"{exe}\" {Flag} --loaded \"%~dp0hooks.loaded\"\r\n";

    private static Decision Deny(string reason) => new(Verdict.Deny, reason);

    private static JsonObject DenyJson(string reason) => new() { ["decision"] = "deny", ["reason"] = reason };

    private static int Answer(JsonObject o)
    {
        try
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            stdout.Write(o.ToJsonString());
            return 0;
        }
        catch { return 1; }   // agy: a hook that fails is a tool error — refused
    }

    private static bool TouchesPlugin(string text, string pluginDir)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(pluginDir)) return false;
        string Norm(string s) => s.Replace('/', '\\').TrimEnd('\\');
        string dir = Norm(pluginDir);
        // The plugin folder is .agents\plugins\cluadex under the turn folder: its parent turn folder counts too.
        string? turn = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir)));
        string t = Norm(text);
        return t.Contains(dir, StringComparison.OrdinalIgnoreCase)
            || turn is { Length: > 3 } && t.Contains(turn, StringComparison.OrdinalIgnoreCase);
    }

    private static string PathArg(JsonObject args)
    {
        foreach (var k in new[] { "TargetFile", "AbsolutePath", "FilePath", "file_path", "path", "Path" })
            if (Str(args, k) is { Length: > 0 } p) return p;
        return "";
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue(out string? s) && s != null: yield return s; break;
            case JsonObject o: foreach (var kv in o) foreach (var s in Strings(kv.Value)) yield return s; break;
            case JsonArray a: foreach (var item in a) foreach (var s in Strings(item)) yield return s; break;
        }
    }

    private static string? Str(JsonObject? o, string key) =>
        o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue(out string? s) ? s : null;

    private static string? ArgAfter(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Cap(string s) => s.Length > 4000 ? s[..4000] + "…" : s;
}
