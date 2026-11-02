using System.Text.Json;
using System.Text.Json.Nodes;
using CluadeX.Models;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("tooltest")]

namespace CluadeX.Helpers;

/// <summary>
/// Makes a tool call from any model land on the right tool with the right arguments (owner 2026-11-02: "เครื่องมือ
/// ต้องใช้ร่วมกันได้ทุกโมเดล … เจ้าอื่นเงอะงะ ผิดพลาดรันไม่ค่อยได้"). Claude writes CluadeX's own names; other models write
/// the names they were trained on — Claude Code's (Read, Edit, Bash), Gemini CLI's (replace, run_shell_command),
/// Codex's (shell with a cmd array), a "functions." prefix from gpt-oss — and the argument names that go with them
/// (file_path, old_string, new_string, cmd). Those used to come back "Unknown tool" or "Missing 'path'", and an edit_file
/// with search + replace even appended the replacement to the end of the file.
/// Deterministic, no model calls. A name that does not resolve after this is still refused as unknown.
/// </summary>
public static class ToolCallNormalizer
{
    /// <summary>The argument a provider puts the raw text in when a model's arguments were not a JSON object.</summary>
    public const string InvalidArgumentsKey = "__invalid_arguments";

    private static readonly string[] Prefixes = { "functions.", "function.", "default_api.", "tools.", "tool.", "functions:", "api." };

    /// <summary>Other agents' tool names → CluadeX's.</summary>
    private static readonly Dictionary<string, string> ForeignNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Claude Code
        ["read"] = "read_file", ["write"] = "write_file", ["edit"] = "edit_file", ["multiedit"] = "multi_edit",
        ["bash"] = "run_command", ["ls"] = "list_files", ["task"] = "agent_spawn", ["todowrite"] = "todo_write",
        // Gemini CLI
        ["replace"] = "edit_file", ["run_shell_command"] = "run_command", ["search_file_content"] = "grep_search",
        ["google_web_search"] = "web_search", ["read_folder"] = "list_files", ["save_memory"] = "memory_save",
        // Codex / OpenAI agents
        ["shell_command"] = "run_command", ["local_shell"] = "run_command", ["container.exec"] = "run_command",
        ["execute_command"] = "run_command", ["terminal"] = "run_command", ["cmd"] = "run_command",
        // common guesses
        ["create_file"] = "write_file", ["str_replace"] = "edit_file", ["replace_in_file"] = "edit_file",
        ["view_file"] = "read_file", ["open_file"] = "read_file", ["cat"] = "read_file", ["browse"] = "web_fetch",
        ["list_dir"] = "list_files", ["find_in_files"] = "grep_search", ["ripgrep"] = "grep_search", ["rg"] = "grep_search",
    };

    /// <summary>The name to try when <paramref name="name"/> itself is not a tool: lowercased, with a "functions." /
    /// "default_api." prefix removed and another agent's name translated. Returns the input when nothing applies.</summary>
    public static string NormalizeName(string? name)
    {
        string n = (name ?? "").Trim().Trim('`', '"', '\'');
        foreach (var p in Prefixes)
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { n = n[p.Length..]; break; }
        if (ForeignNames.TryGetValue(n, out var mapped)) return mapped;
        // CamelCase / PascalCase / kebab-case → snake_case ("ReadFile", "read-file" → read_file).
        var sb = new System.Text.StringBuilder(n.Length + 4);
        for (int i = 0; i < n.Length; i++)
        {
            char c = n[i];
            if (c is '-' or ' ') { sb.Append('_'); continue; }
            if (char.IsUpper(c) && i > 0 && n[i - 1] != '_' && !char.IsUpper(n[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        string snake = sb.ToString();
        return ForeignNames.TryGetValue(snake, out var m2) ? m2 : snake;
    }

    // Per tool: alternative key → CluadeX's key. Applied only when CluadeX's key is absent.
    private static readonly (string From, string To)[] PathKeys =
        { ("file_path", "path"), ("filepath", "path"), ("filename", "path"), ("file_name", "path"), ("file", "path"),
          ("absolute_path", "path"), ("target_file", "path"), ("relative_path", "path") };

    private static readonly (string From, string To)[] DirKeys =
        { ("dir_path", "path"), ("dir", "path"), ("directory", "path"), ("folder", "path"), ("dirpath", "path"), ("relative_workspace_path", "path") };

    private static readonly (string From, string To)[] EditKeys =
        { ("old_string", "find"), ("old_str", "find"), ("old_text", "find"), ("oldtext", "find"), ("search", "find"),
          ("original", "find"), ("target", "find"), ("from", "find"),
          ("new_string", "replace"), ("new_str", "replace"), ("new_text", "replace"), ("newtext", "replace"),
          ("replacement", "replace"), ("to", "replace"), ("with", "replace"), ("replaceall", "replace_all") };

    private static readonly (string From, string To)[] ContentKeys =
        { ("contents", "content"), ("text", "content"), ("file_text", "content"), ("data", "content"), ("body", "content"), ("code", "content") };

    private static readonly (string From, string To)[] CommandKeys =
        { ("cmd", "command"), ("script", "command"), ("shell_command", "command"), ("commandline", "command"),
          ("command_line", "command"), ("code", "command"), ("input", "command") };

    private static readonly (string From, string To)[] ReadKeys =
        { ("start_line", "offset"), ("line_offset", "offset"), ("from_line", "offset"), ("startline", "offset"),
          ("max_lines", "limit"), ("line_count", "limit"), ("lines", "limit") };

    /// <summary>
    /// Rename the arguments a model sent under another agent's names to the ones CluadeX's executor reads, in place.
    /// Never overwrites a key that is already there. A Codex-style command array (["bash","-lc","ls"]) becomes the
    /// command text. multi_edit's edits get the same find/replace renames inside the JSON.
    /// </summary>
    public static void NormalizeArgs(ToolType type, Dictionary<string, string> args)
    {
        if (args.Count == 0) return;
        switch (type)
        {
            case ToolType.ReadFile:
                Rename(args, PathKeys); Rename(args, ReadKeys); break;
            case ToolType.WriteFile:
                Rename(args, PathKeys); Rename(args, ContentKeys); break;
            case ToolType.EditFile:
                Rename(args, PathKeys); Rename(args, EditKeys); break;
            case ToolType.MultiEdit:
                Rename(args, PathKeys);
                if (args.TryGetValue("edits", out var edits)) args["edits"] = NormalizeEdits(edits);
                break;
            case ToolType.ListFiles or ToolType.CreateDirectory:
                Rename(args, DirKeys); Rename(args, PathKeys); break;
            case ToolType.RunCommand or ToolType.PowerShell:
                Rename(args, CommandKeys);
                if (args.TryGetValue("command", out var c)) args["command"] = CommandText(c);
                if (!args.ContainsKey("timeout_ms") && args.TryGetValue("timeout", out var t)) args["timeout_ms"] = t;
                if (!args.ContainsKey("cwd") && args.TryGetValue("workdir", out var wd)) args["cwd"] = wd;
                break;
            case ToolType.Grep:
                Rename(args, new[] { ("regex", "pattern"), ("query", "pattern"), ("search", "pattern"), ("dir_path", "path"), ("include", "glob") }); break;
            case ToolType.GlobSearch or ToolType.SearchFiles:
                Rename(args, new[] { ("glob_pattern", "pattern"), ("glob", "pattern"), ("query", "pattern"), ("dir_path", "path"), ("directory", "path") }); break;
            case ToolType.SearchContent:
                Rename(args, new[] { ("search", "query"), ("text", "query"), ("search_term", "query"), ("dir_path", "path") }); break;
            case ToolType.WebFetch:
                Rename(args, new[] { ("uri", "url"), ("link", "url"), ("href", "url"), ("prompt", "query") }); break;
            case ToolType.WebSearch:
                Rename(args, new[] { ("q", "query"), ("search_query", "query"), ("search", "query"), ("text", "query") }); break;
        }
    }

    private static void Rename(Dictionary<string, string> args, (string From, string To)[] map)
    {
        foreach (var (from, to) in map)
            if (!args.ContainsKey(to) && args.TryGetValue(from, out var v))
            {
                args[to] = v;
                args.Remove(from);
            }
    }

    /// <summary>A command sent as a JSON array: ["bash","-lc","npm test"] → "npm test", ["git","status"] → "git status".</summary>
    internal static string CommandText(string command)
    {
        string s = command.Trim();
        if (!s.StartsWith('[') || !s.EndsWith(']')) return command;
        try
        {
            var parts = JsonSerializer.Deserialize<List<string>>(s);
            if (parts is not { Count: > 0 }) return command;
            // A shell wrapper: the script is its last element.
            if (parts.Count >= 3 && parts[0].ToLowerInvariant() is "bash" or "sh" or "zsh" or "cmd" or "cmd.exe" or "powershell" or "pwsh" or "powershell.exe"
                && parts[1].ToLowerInvariant() is "-lc" or "-c" or "/c" or "-command" or "-noprofile")
                return parts[^1];
            return string.Join(" ", parts.Select(p => p.Contains(' ') && !p.StartsWith('"') ? $"\"{p}\"" : p));
        }
        catch (JsonException) { return command; }
    }

    /// <summary>multi_edit's edits with old_string/new_string (or old_str/new_str, search/replace…) renamed to find/replace.</summary>
    internal static string NormalizeEdits(string edits)
    {
        string s = edits.Trim();
        if (!s.StartsWith('[')) return edits;
        try
        {
            if (JsonNode.Parse(s) is not JsonArray arr) return edits;
            bool changed = false;
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                foreach (var (from, to) in EditKeys)
                {
                    if (o.ContainsKey(to) || !o.TryGetPropertyValue(from, out var v)) continue;
                    o.Remove(from);
                    o[to] = v;
                    changed = true;
                }
            }
            return changed ? arr.ToJsonString() : edits;
        }
        catch (JsonException) { return edits; }
    }
}
