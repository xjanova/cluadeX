using System.IO;
using System.Text;
using System.Text.Json;
using CluadeX.Helpers;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// A Claude Code tool call in CluadeX's own words: the tool name/type CluadeX's chat renders (rows, folding, verbs),
/// the arguments under the keys its rows read (path, command, pattern, url, query, task …), and for a file edit the
/// path relative to the run's folder. Hidden = CLI plumbing with nothing to show (ToolSearch loading schemas).
/// </summary>
public sealed record ClaudeDevMappedTool(string Name, ToolType Type, Dictionary<string, string> Args, string? FilePath, bool Hidden = false);

public static class ClaudeDevToolMap
{
    /// <summary>Longest value one argument keeps (a Write's whole file content is not row material).</summary>
    private const int MaxArgChars = 2000;

    private static string FirstQuestion(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty("questions", out var qs) && qs.ValueKind == JsonValueKind.Array
        && qs.GetArrayLength() > 0 && qs[0].ValueKind == JsonValueKind.Object && qs[0].TryGetProperty("question", out var q)
        && q.ValueKind == JsonValueKind.String ? q.GetString() ?? "" : "";

    public static ClaudeDevMappedTool Map(string cliName, JsonElement input, string? runFolder)
    {
        var a = new Dictionary<string, string>();
        string S(string key) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" : v.GetRawText()
            : "";
        void Put(string key, string value) { if (!string.IsNullOrEmpty(value)) a[key] = Cap(value); }
        string? Rel(string path) => string.IsNullOrWhiteSpace(path) ? null : RelativeTo(path, runFolder);

        switch (cliName)
        {
            case "Bash":
                Put("command", S("command")); Put("description", S("description"));
                return new("run_command", ToolType.RunCommand, a, null);
            case "BashOutput" or "TaskOutput":
                Put("id", S("bash_id") is { Length: > 0 } b ? b : S("task_id"));
                return new("bg_output", ToolType.BgOutput, a, null);
            case "Read":
                Put("path", Rel(S("file_path")) ?? ""); Put("offset", S("offset")); Put("limit", S("limit"));
                return new("read_file", ToolType.ReadFile, a, null);
            case "Write":
            {
                string? p = Rel(S("file_path"));
                Put("path", p ?? ""); Put("content", S("content"));
                return new("write_file", ToolType.WriteFile, a, p);
            }
            case "Edit":
            {
                string? p = Rel(S("file_path"));
                Put("path", p ?? ""); Put("old_string", S("old_string")); Put("new_string", S("new_string"));
                return new("edit_file", ToolType.EditFile, a, p);
            }
            case "MultiEdit":
            {
                string? p = Rel(S("file_path"));
                Put("path", p ?? ""); Put("edits", S("edits"));
                return new("multi_edit", ToolType.MultiEdit, a, p);
            }
            case "NotebookEdit":
            {
                string? p = Rel(S("notebook_path"));
                Put("notebook_path", p ?? ""); Put("new_source", S("new_source"));
                return new("notebook_edit", ToolType.NotebookEdit, a, p);
            }
            case "Grep":
                Put("pattern", S("pattern")); Put("path", Rel(S("path")) ?? ""); Put("glob", S("glob"));
                return new("grep", ToolType.Grep, a, null);
            case "Glob":
                Put("pattern", S("pattern")); Put("path", Rel(S("path")) ?? "");
                return new("glob", ToolType.GlobSearch, a, null);
            case "LS":
                Put("path", Rel(S("path")) ?? "");
                return new("list_files", ToolType.ListFiles, a, null);
            case "TodoWrite":
                Put("todos", S("todos"));
                return new("todo_write", ToolType.TodoWrite, a, null);
            case "TaskCreate" or "TaskUpdate" or "TaskList" or "TaskGet":
                foreach (var k in new[] { "subject", "taskId", "status", "description" }) Put(k, S(k));
                return new("todo_write", ToolType.TodoWrite, a, null);
            case "WebFetch":
                Put("url", S("url")); Put("prompt", S("prompt"));
                return new("web_fetch", ToolType.WebFetch, a, null);
            case "WebSearch":
                Put("query", S("query"));
                return new("web_search", ToolType.WebSearch, a, null);
            case "Task" or "Agent":
                Put("task", S("description") is { Length: > 0 } d ? d : S("prompt"));
                Put("type", S("subagent_type"));
                return new("agent_spawn", ToolType.AgentSpawn, a, null);
            case "Skill":
                Put("skill", S("skill") is { Length: > 0 } sk ? sk : S("command"));
                return new("skill_invoke", ToolType.SkillInvoke, a, null);
            case "AskUserQuestion":
                // Claude Code sends {questions:[{question}]}; Gemini's and Antigravity's translators a plain question.
                Put("question", S("question") is { Length: > 0 } one ? one : FirstQuestion(input));
                return new("ask_user", ToolType.AskUser, a, null);
            case "mcp__" + Tools.ChatToolBridge.ServerName + "__" + Tools.ChatToolBridge.AskToolName:
                Put("question", S("question"));
                return new("ask_user", ToolType.AskUser, a, null);
            case "ToolSearch":
                return new("tool_search", ToolType.McpTool, a, null, Hidden: true);
        }

        // mcp__server__tool keeps its name (the row then names its server); anything else becomes snake_case.
        if (input.ValueKind == JsonValueKind.Object)
            foreach (var p in input.EnumerateObject())
                Put(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText());
        return new(cliName.StartsWith("mcp__", StringComparison.Ordinal) ? cliName : SnakeCase(cliName), ToolType.McpTool, a, null);
    }

    /// <summary>An absolute path inside the run's folder → relative with '/', anything else unchanged.</summary>
    public static string RelativeTo(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(path)) return path;
        try
        {
            string full = Path.GetFullPath(path), r = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(r, full).Replace('\\', '/');
        }
        catch { /* not a path after all */ }
        return path;
    }

    private static string Cap(string s) => s.Length > MaxArgChars ? s[..MaxArgChars] + "…" : s;

    private static string SnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

/// <summary>
/// One Claude Dev worker turn, read from the CLI's stream-json lines and turned into the same events CluadeX's own
/// native loop raises — reply text per step, thinking, a tool row per call (with its result, timing and diff), the
/// live plan, the sub-agent map — and the steps the turn took, for the AgentLoopResult. Before 2026-10-17 each line
/// became a quoted "⏵ Bash · `cmd`" in one long reply and none of the agent UI showed (owner: "ต้องให้มันทำงานเหมือน
/// api สิ"). Pure: no UI, no process — the contract harness feeds it a recorded transcript.
/// </summary>
public sealed class ClaudeDevStreamTurn
{
    /// <summary>Longest tool output a row keeps.</summary>
    private const int MaxOutputChars = 4000;

    private readonly string? _runFolder;
    private string? _messageKey;
    private AgentStep? _step;
    private bool _lastWasUser = true;
    private int _autoKey;
    private readonly Dictionary<string, (ToolCall Call, ClaudeDevMappedTool Map, JsonElement Input, AgentStep Step)> _pending = new();
    private readonly HashSet<string> _hidden = new();
    private readonly List<(string Id, TodoItem Item)> _plan = new();
    private int _taskSeq;
    private readonly Dictionary<string, CodeAgentService.SubAgentEvent> _subAgents = new();
    private int _subAgentSeq;

    public ClaudeDevStreamTurn(string? runFolder) => _runFolder = runFolder;

    /// <summary>Reply text of a step (the chat opens a new bubble when the step changes).</summary>
    public event Action<string, int>? Text;
    public event Action<string, int>? Thinking;
    /// <summary>A new assistant message began = a new step (its number).</summary>
    public event Action<int>? StepStarted;
    public event Action<ToolCall>? ToolStarting;
    public event Action<ToolResult>? ToolExecuted;
    /// <summary>The whole plan, every time it changes (TodoWrite, TaskCreate, TaskUpdate).</summary>
    public event Action<IReadOnlyList<TodoItem>>? PlanChanged;
    public event Action<CodeAgentService.SubAgentEvent>? SubAgent;

    public List<AgentStep> Steps { get; } = new();
    public IReadOnlyList<TodoItem> Plan => _plan.Select(p => p.Item).ToList();
    /// <summary>The model said something or called a tool.</summary>
    public bool HasOutput { get; private set; }
    public bool Done { get; private set; }
    public bool ResultIsError { get; private set; }
    public string? ResultText { get; private set; }
    public decimal? CostUsd { get; private set; }
    public int? NumTurns { get; private set; }

    /// <summary>The reply: the text of the last step that said something — what the chat's last bubble holds — never
    /// the tool quotes. The result's own text when the model wrote none.</summary>
    public string FinalText =>
        Steps.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.ResponseText))?.ResponseText?.Trim()
        ?? (!ResultIsError ? ResultText?.Trim() ?? "" : "");

    /// <summary>The turn's answer for a caller (the pipe, the last bubble): <see cref="FinalText"/>, unless that is a
    /// short closing remark after a substantive answer one step earlier (the user's Stop hook nudging the CLI to talk
    /// about the brain at the end, 2026-10-17) — then the earlier answer with the remark under it, the same rule as the
    /// native loop's <see cref="CodeAgentService.KeepAnswerOverRemark"/>.</summary>
    public string Answer
    {
        get
        {
            var spoken = Steps.Where(s => !string.IsNullOrWhiteSpace(s.ResponseText)).ToList();
            if (spoken.Count < 2) return FinalText;
            return CodeAgentService.KeepAnswerOverRemark(spoken[^2].ResponseText!.Trim(), FinalText) ?? FinalText;
        }
    }

    public void Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = Str(root, "type");
            string? parent = root.TryGetProperty("parent_tool_use_id", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null;
            switch (type)
            {
                case "assistant" when parent != null: OnSubAgentMessage(parent, root); break;
                case "assistant": OnAssistant(root); break;
                case "user" when parent == null: OnUser(root); break;
                case "result":
                    Done = true;
                    ResultIsError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    ResultText = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) CostUsd = c.GetDecimal();
                    if (root.TryGetProperty("num_turns", out var n) && n.ValueKind == JsonValueKind.Number) NumTurns = n.GetInt32();
                    break;
            }
        }
        catch (JsonException) { /* a half line or junk: nothing to show */ }
    }

    private void OnAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return;
        // The CLI prints one line per content block; the blocks of one API message share its id. A message = a step.
        string key = Str(msg, "id") is { Length: > 0 } id ? id
            : _lastWasUser || _messageKey == null ? "auto-" + ++_autoKey : _messageKey;
        _lastWasUser = false;
        if (key != _messageKey || _step == null)
        {
            _messageKey = key;
            _step = new AgentStep { StepNumber = Steps.Count + 1 };
            Steps.Add(_step);
            StepStarted?.Invoke(_step.StepNumber);
        }
        var step = _step;

        foreach (var part in content.EnumerateArray())
        {
            switch (Str(part, "type"))
            {
                case "text":
                {
                    string text = Str(part, "text");
                    if (string.IsNullOrWhiteSpace(text)) break;
                    string token = string.IsNullOrEmpty(step.ResponseText) ? text : "\n\n" + text;
                    step.ResponseText += token;
                    HasOutput = true;
                    Text?.Invoke(token, step.StepNumber);
                    break;
                }
                case "thinking":
                {
                    // Claude Code redacts thinking to a signature with an empty text on most setups — nothing to show.
                    string thought = Str(part, "thinking");
                    if (string.IsNullOrWhiteSpace(thought)) break;
                    step.ThinkingText = string.IsNullOrEmpty(step.ThinkingText) ? thought : step.ThinkingText + "\n\n" + thought;
                    Thinking?.Invoke(thought, step.StepNumber);
                    break;
                }
                case "tool_use":
                    OnToolUse(part, step);
                    break;
            }
        }
    }

    private void OnToolUse(JsonElement part, AgentStep step)
    {
        string id = Str(part, "id");
        string name = Str(part, "name");
        var input = part.TryGetProperty("input", out var inp) ? inp.Clone() : default;
        var map = ClaudeDevToolMap.Map(name, input, _runFolder);
        if (map.Hidden) { _hidden.Add(id); return; }
        HasOutput = true;
        var call = new ToolCall { ToolName = map.Name, Type = map.Type, Arguments = map.Args };
        step.ToolCalls.Add(call);
        if (id.Length > 0) _pending[id] = (call, map, input, step);

        if (name is "TodoWrite" or "TaskCreate" or "TaskUpdate") UpdatePlan(name, input);
        if (map.Type == ToolType.AgentSpawn)
        {
            var ev = new CodeAgentService.SubAgentEvent(1000 + ++_subAgentSeq,
                map.Args.GetValueOrDefault("type") is { Length: > 0 } t ? t : "agent",
                map.Args.GetValueOrDefault("type") is "Explore" or "explore" ? "explore" : "general",
                map.Args.GetValueOrDefault("task") ?? "", "start", "", 0);
            if (id.Length > 0) _subAgents[id] = ev;
            SubAgent?.Invoke(ev);
        }
        ToolStarting?.Invoke(call);
    }

    private void OnUser(JsonElement root)
    {
        _lastWasUser = true;
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return;
        root.TryGetProperty("tool_use_result", out var structured);
        foreach (var part in content.EnumerateArray())
        {
            if (Str(part, "type") != "tool_result") continue;
            string id = Str(part, "tool_use_id");
            if (_hidden.Remove(id) || !_pending.Remove(id, out var call)) continue;
            bool isError = part.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            string text = ResultText_(part);
            var result = new ToolResult
            {
                ToolName = call.Map.Name,
                Type = call.Map.Type,
                Success = !isError,
                Output = Trim(text),
                Error = isError ? Trim(text) : "",
                Arguments = call.Map.Args,
                FilePath = call.Map.FilePath,
            };
            if (!isError && call.Map.FilePath != null) result.Diff = DiffOf(call.Map.Type, call.Input, structured);
            if (call.Map.Type == ToolType.TodoWrite && _plan.Count > 0)
                result.Summary = $"Plan: {_plan.Count(p => p.Item.Status == "completed")}/{_plan.Count}";
            call.Step.ToolResults.Add(result);

            if (_subAgents.Remove(id, out var sub))
                SubAgent?.Invoke(sub with { Phase = isError ? "failed" : "done", Detail = FirstLine(text, 120) });
            ToolExecuted?.Invoke(result);
        }
    }

    /// <summary>A sub-agent's own message (parent_tool_use_id = the Task call): its tools go to the agent map, not to
    /// the main chat's rows.</summary>
    private void OnSubAgentMessage(string parent, JsonElement root)
    {
        if (!_subAgents.TryGetValue(parent, out var sub)) return;
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) return;
        foreach (var part in content.EnumerateArray())
        {
            if (Str(part, "type") != "tool_use") continue;
            var map = ClaudeDevToolMap.Map(Str(part, "name"), part.TryGetProperty("input", out var inp) ? inp : default, _runFolder);
            if (map.Hidden) continue;
            string target = map.Args.GetValueOrDefault("command") ?? map.Args.GetValueOrDefault("pattern")
                ?? map.Args.GetValueOrDefault("path") ?? map.Args.GetValueOrDefault("url") ?? map.Args.GetValueOrDefault("query") ?? "";
            sub = sub with { Phase = "tool", Detail = FirstLine((map.Name + " " + target).Trim(), 120), Steps = sub.Steps + 1 };
            _subAgents[parent] = sub;
            SubAgent?.Invoke(sub);
        }
    }

    /// <summary>The run's plan: TodoWrite sends the whole list; the newer CLI's TaskCreate adds one item (ids "1", "2" …
    /// in order) and TaskUpdate changes one by id.</summary>
    private void UpdatePlan(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        if (tool == "TodoWrite")
        {
            if (!input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return;
            _plan.Clear();
            int i = 0;
            foreach (var t in todos.EnumerateArray())
            {
                string text = Str(t, "content");
                if (string.IsNullOrWhiteSpace(text)) continue;
                _plan.Add(((++i).ToString(), new TodoItem { Content = text.Trim(), Status = Status(Str(t, "status")) }));
            }
        }
        else if (tool == "TaskCreate")
        {
            string text = Str(input, "subject") is { Length: > 0 } s ? s : Str(input, "description");
            if (string.IsNullOrWhiteSpace(text)) return;
            _plan.Add(((++_taskSeq).ToString(), new TodoItem { Content = text.Trim(), Status = "pending" }));
        }
        else
        {
            string id = Str(input, "taskId");
            int at = _plan.FindIndex(p => p.Id == id);
            if (at < 0) return;
            string st = Str(input, "status");
            if (st == "deleted") _plan.RemoveAt(at);
            else
            {
                if (st.Length > 0) _plan[at].Item.Status = Status(st);
                if (Str(input, "subject") is { Length: > 0 } subj) _plan[at].Item.Content = subj.Trim();
            }
        }
        PlanChanged?.Invoke(Plan);
    }

    private static string Status(string s) => s switch
    {
        "completed" or "done" => "completed",
        "in_progress" or "active" => "in_progress",
        _ => "pending",
    };

    /// <summary>The before/after diff of a file edit, from the CLI's structured result (originalFile) when it sent one,
    /// else from the call's own old/new strings.</summary>
    private static EditDiffResult? DiffOf(ToolType type, JsonElement input, JsonElement structured)
    {
        try
        {
            string? original = structured.ValueKind == JsonValueKind.Object && structured.TryGetProperty("originalFile", out var of)
                && of.ValueKind == JsonValueKind.String ? of.GetString() : null;
            switch (type)
            {
                case ToolType.WriteFile:
                    return DiffUtil.Compute(original ?? "", Str(input, "content"));
                case ToolType.EditFile:
                {
                    string oldS = Str(input, "old_string"), newS = Str(input, "new_string");
                    if (original == null) return DiffUtil.Compute(oldS, newS);
                    bool all = input.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;
                    return DiffUtil.Compute(original, Replace(original, oldS, newS, all));
                }
                case ToolType.MultiEdit:
                {
                    if (original == null || !input.TryGetProperty("edits", out var edits) || edits.ValueKind != JsonValueKind.Array) return null;
                    string now = original;
                    foreach (var ed in edits.EnumerateArray())
                        now = Replace(now, Str(ed, "old_string"), Str(ed, "new_string"),
                            ed.TryGetProperty("replace_all", out var r) && r.ValueKind == JsonValueKind.True);
                    return DiffUtil.Compute(original, now);
                }
            }
        }
        catch { /* a diff is decoration: the row still shows */ }
        return null;
    }

    private static string Replace(string text, string oldS, string newS, bool all)
    {
        if (oldS.Length == 0) return text;
        if (all) return text.Replace(oldS, newS, StringComparison.Ordinal);
        int at = text.IndexOf(oldS, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + newS + text[(at + oldS.Length)..];
    }

    /// <summary>A tool_result's content: a string, or an array of blocks whose text parts are joined.</summary>
    private static string ResultText_(JsonElement part)
    {
        if (!part.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var b in c.EnumerateArray())
        {
            if (Str(b, "type") == "text") sb.Append(Str(b, "text")).Append('\n');
            else if (Str(b, "type") == "image") sb.Append("[image]\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Trim(string s) =>
        s.Length > MaxOutputChars ? s[..MaxOutputChars] + $"\n… ({s.Length - MaxOutputChars} more characters)" : s;

    private static string FirstLine(string s, int max)
    {
        string first = (s ?? "").Trim().Split('\n')[0].Trim();
        return first.Length > max ? first[..(max - 1)] + "…" : first;
    }

    private static string Str(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
