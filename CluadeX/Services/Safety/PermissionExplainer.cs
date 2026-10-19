using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Safety;

public enum PermissionRisk { Low, Medium, High, Danger }

/// <summary>A permission prompt in people's words: what will happen, where, with what result, and how risky it is — the
/// real command stays available underneath, folded, for whoever wants to read it.</summary>
public sealed class PlainPermission
{
    public string Headline { get; init; } = "";
    public string What { get; init; } = "";
    public string Where { get; init; } = "";
    public string Effect { get; init; } = "";
    public PermissionRisk Risk { get; init; }
    public string RiskText { get; init; } = "";
    /// <summary>The dangerous parts, said plainly — never folded away (a recursive delete, a file outside the project,
    /// data sent out to the internet, code from the internet run as it comes).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Why it asks, as the asking tool said it in words (a build after edits nobody looked at, a policy's note).
    /// Empty when the request carried none — code and commands never go here.</summary>
    public string Note { get; init; } = "";
    /// <summary>The tool and the raw request, for the folded "technical details".</summary>
    public string Technical { get; init; } = "";
}

/// <summary>
/// Turns a permission request (the tool, its real arguments, the shell command it would run) into
/// <see cref="PlainPermission"/>. Owner 2026-10-19: "ตอนที่กล่องตอบโต้ปรากฎเพื่อถามว่าจะทำสิ่งนี้ได้ไหม ฉันเห็นเป็นคำสั่ง
/// หรือฟังชั่น ถ้าคนทั่วไปไม่เข้าใจแน่ อยากให้อธิบายแบบคนทั่วไปเข้าใจด้วย".
///
/// It reads what will REALLY run — each part of a command line (<c>a &amp;&amp; b | c</c>), each path against the open
/// project and the protected folders — never a generic "the AI wants to use a tool". What it cannot read is said to be
/// unread, and rated at least medium. Pure apart from checking whether a file to be written already exists.
/// </summary>
public static class PermissionExplainer
{
    public static PlainPermission Explain(PermissionRequestInfo info, string? projectRoot, Func<string, string>? t = null)
    {
        t ??= key => LocalizedResources.Instance[key];
        var e = new Builder(t, projectRoot);
        string tool = info.ToolName ?? "";
        string who = t("perm.who.ai");
        string bare = tool;
        foreach (var agent in new[] { "Claude Dev ", "Gemini Dev " })
            if (tool.StartsWith(agent, StringComparison.Ordinal)) { who = agent.Trim(); bare = tool[agent.Length..].Trim(); }

        var args = info.Args ?? new Dictionary<string, string>();
        string Arg(params string[] names)
        {
            foreach (var n in names)
                if (args.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return "";
        }
        // "scope: resource" (+ "\n" extra) is how most prompts carry their target; the danger pipe carries the action as-is.
        string detail = info.Detail ?? "";
        string firstLine = detail.Split('\n')[0];
        string resource = Regex.Replace(firstLine, @"^(execute|write|read|browse|desktop|delete|network)\s*:\s*", "", RegexOptions.IgnoreCase).Trim();
        string lower = bare.ToLowerInvariant();

        if (lower.StartsWith("mcp__"))
            e.Mcp(lower, args);
        else if (lower is "bash" or "powershell" or "run_command" or "run_shell_command" or "run_build" or "run_tests" or "shell"
                 or "execute_command" or "bg_start" or "ui studio" or "desktop_launch"
                 || lower.StartsWith("bg_") && Arg("command").Length > 0)
        {
            string command = Arg("command", "script", "cmd");
            if (command.Length == 0) command = resource;
            if (lower is "bg_start") e.Add(PermissionRisk.Medium, string.Format(t("perm.do.background"), Short(command, 80)), t("perm.effect.run"));
            e.Shell(command);
        }
        else if (lower is "write" or "write_file" or "create_file")
            e.WriteFile(First(Arg("path", "file_path"), FileTarget(resource)), overwrite: true, info.Diff?.Stat);
        else if (lower is "edit" or "edit_file" or "multiedit" or "multi_edit" or "replace" or "str_replace")
            e.WriteFile(First(Arg("path", "file_path"), FileTarget(resource)), overwrite: false, info.Diff?.Stat);
        else if (lower is "notebookedit" or "notebook_edit")
            e.WriteFile(First(Arg("notebook_path", "path"), FileTarget(resource)), overwrite: false, info.Diff?.Stat, notebook: true);
        else if (lower is "create_directory")
            e.AtPath(PermissionRisk.Medium, "perm.do.mkdir", First(Arg("path"), resource), "perm.effect.write");
        else if (lower is "delete_file" or "remove_file")
            e.Delete(First(Arg("path"), resource), recursive: false);
        else if (lower.StartsWith("git_") || lower.StartsWith("gh_"))
            e.GitTool(lower, args, resource);
        else if (lower is "web_fetch")
            e.Web(First(Arg("url"), resource));
        else if (lower is "web_search")
            e.Add(PermissionRisk.Low, t("perm.do.webSearch"), t("perm.effect.network"), t("perm.where.internet.any"));
        else if (lower.StartsWith("browser_") || firstLine.StartsWith("browse", StringComparison.OrdinalIgnoreCase))
            e.Browser(lower, First(Arg("url"), resource), Arg("script", "expression", "code", "js"));
        else if (lower.StartsWith("hex_"))
            e.AtPath(PermissionRisk.High, "perm.do.hex", First(Arg("path"), ExtractPath(detail) ?? resource), "perm.effect.write");
        else if (lower.StartsWith("desktop_") || firstLine.StartsWith("desktop", StringComparison.OrdinalIgnoreCase))
            e.Add(PermissionRisk.High, string.Format(t("perm.do.desktop"), Short(resource, 80)), t("perm.effect.run"), t("perm.where.machine"));
        else if (lower is "open_project")
            e.Folder(ExtractPath(detail) ?? First(Arg("path"), resource));
        else if (lower is "restart_app")
            e.Add(PermissionRisk.Medium, t("perm.do.restart"), t("perm.effect.run"), t("perm.where.machine"));
        else if (lower is "agent_spawn" or "subagent_invoke" or "task")
            e.Add(PermissionRisk.Medium, t("perm.do.agent"), t("perm.effect.run"));
        else if (lower is "repl" || lower.StartsWith("code block"))
        {
            string lang = Regex.Match(tool, @"\(([^)]+)\)").Groups[1].Value;
            e.Add(PermissionRisk.Medium, string.Format(t("perm.do.code"), lang.Length > 0 ? lang : bare), t("perm.effect.run"));
            string code = Arg("code", "input", "command");
            if (code.Length == 0) code = detail.Contains('\n') ? detail[(detail.IndexOf('\n') + 1)..] : resource;
            e.Shell(code, codeOnly: true);
        }
        else if (detail.Contains("http", StringComparison.OrdinalIgnoreCase) && Regex.Match(detail, @"https?://\S+") is { Success: true } url)
            e.Web(url.Value, redirect: lower is "web_fetch" || detail.Contains("→") || detail.Contains(" to ", StringComparison.Ordinal));
        else
            e.Add(PermissionRisk.Medium, string.Format(t("perm.do.tool"), bare), t("perm.effect.unknown"));

        if (info.Danger != null) e.Danger();
        // The lines under "scope: resource" are the asking tool's own words (why it asks) — shown as they are, except for
        // code (repl, a code block), which stays in the technical details.
        string note = lower is "repl" || lower.StartsWith("code block") || lower.StartsWith("mcp__") && !detail.Contains('\n')
            ? "" : string.Join("\n", detail.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0 && !LooksLikeCode(l)).Take(4));
        return e.Build(who, tool, detail, info.Danger, note);
    }

    /// <summary>A line of code or a command rather than a sentence (braces, semicolons, a shell prompt, no spaces at all).</summary>
    private static bool LooksLikeCode(string line) =>
        Regex.IsMatch(line, @"[{};]\s*$|^\s*(\$|>|PS [A-Z]:)|^[^\s]{40,}$|=>|\(\)") || line.Length > 400;

    private static string First(string a, string b) => a.Length > 0 ? a : b;

    /// <summary>"write C:\x" (the guard hook's action for a Claude Dev Write) → "C:\x".</summary>
    private static string FileTarget(string resource) => Regex.Replace(resource, @"^(write|edit)\s+", "", RegexOptions.IgnoreCase);

    internal static string Short(string s, int max)
    {
        s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    /// <summary>A Windows path or URL-free absolute path inside free text ("open a folder outside the project: D:\x").</summary>
    private static string? ExtractPath(string text)
    {
        var m = Regex.Match(text ?? "", @"([A-Za-z]:\\[^\r\n""<>|]*|\\\\[^\r\n""<>|]+)");
        return m.Success ? m.Value.Trim().TrimEnd('.', ',', ';') : null;
    }

    // ─── The builder: collects what each part does, keeps the worst risk, says the dangerous parts plainly ───

    private sealed class Builder
    {
        private readonly Func<string, string> _t;
        private readonly string? _root;
        private readonly List<string> _what = new();
        private readonly List<string> _where = new();
        private readonly List<string> _effects = new();
        private readonly List<string> _warnings = new();
        private PermissionRisk _risk = PermissionRisk.Low;
        private int _hidden;
        private bool _downloadRun;

        public Builder(Func<string, string> t, string? root) { _t = t; _root = string.IsNullOrWhiteSpace(root) ? null : root; }

        private string T(string key) => _t(key);
        private string F(string key, params object[] a) { try { return string.Format(_t(key), a); } catch (FormatException) { return _t(key); } }

        public void Add(PermissionRisk risk, string what, string? effect = null, string? where = null)
        {
            if (risk > _risk) _risk = risk;
            if (_what.Count < 4) { if (!_what.Contains(what)) _what.Add(what); }
            else _hidden++;
            if (effect != null && !_effects.Contains(effect)) _effects.Add(effect);
            if (where != null && !_where.Contains(where)) _where.Add(where);
        }

        public void Warn(string warning, PermissionRisk atLeast = PermissionRisk.High)
        {
            if (atLeast > _risk) _risk = atLeast;
            if (!_warnings.Contains(warning)) _warnings.Add(warning);
        }

        public void Danger()
        {
            _risk = PermissionRisk.Danger;
            _warnings.Insert(0, T("perm.warn.danger"));
        }

        // ─── Where a path is ───

        private enum Place { Project, Outside, Protected, Unknown }

        private (Place Place, string Shown) Locate(string raw)
        {
            string p = (raw ?? "").Trim().Trim('"', '\'');
            if (p.Length == 0) return (Place.Unknown, "");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            p = Regex.Replace(p, @"^(~|\$HOME|\$env:USERPROFILE|%USERPROFILE%)(?=$|[\\/])", home.Replace("$", "$$"), RegexOptions.IgnoreCase);
            try
            {
                bool rooted = Path.IsPathRooted(p);
                string full = rooted ? Path.GetFullPath(p) : _root != null ? Path.GetFullPath(Path.Combine(_root, p)) : p;
                if (_root != null && ProtectedPaths.IsInside(full, _root))
                    return (Place.Project, Path.GetRelativePath(_root, full) is var rel && rel != "." ? rel : Path.GetFileName(_root.TrimEnd('\\', '/')));
                if (!rooted && _root == null) return (Place.Unknown, p);
                if (ProtectedPaths.WhyProtected(full) != null) return (Place.Protected, full);
                return (Place.Outside, full);
            }
            catch { return (Place.Unknown, p); }
        }

        private void AddWhere(string where) { if (!_where.Contains(where)) _where.Add(where); }

        private void NoteWhere(Place place, string shown)
        {
            switch (place)
            {
                case Place.Project when _root != null: AddWhere(F("perm.where.project", ProjectName)); break;
                case Place.Outside: AddWhere(F("perm.where.outside", shown)); break;
                case Place.Protected: AddWhere(F("perm.where.protected", shown)); break;
            }
        }

        private string ProjectName => _root == null ? "" : Path.GetFileName(_root.TrimEnd('\\', '/'));

        // ─── Files ───

        public void AtPath(PermissionRisk risk, string whatKey, string path, string effectKey)
        {
            var (place, shown) = Locate(path);
            Add(risk, F(whatKey, shown.Length > 0 ? shown : path), T(effectKey));
            NoteWhere(place, shown);
            if (place is Place.Outside or Place.Protected) Warn(F("perm.warn.outside", shown));
        }

        public void WriteFile(string path, bool overwrite, string? diffStat, bool notebook = false)
        {
            var (place, shown) = Locate(path);
            bool exists = false;
            try { exists = shown.Length > 0 && File.Exists(place == Place.Project && _root != null ? Path.Combine(_root, shown) : shown); } catch { }
            string key = notebook ? "perm.do.notebook" : !exists && overwrite ? "perm.do.newFile" : overwrite ? "perm.do.overwrite" : "perm.do.writeFile";
            string what = F(key, shown.Length > 0 ? shown : path);
            if (!string.IsNullOrWhiteSpace(diffStat)) what += " (" + diffStat.Trim() + ")";
            Add(place == Place.Project ? PermissionRisk.Medium : PermissionRisk.High, what, T("perm.effect.write"));
            NoteWhere(place, shown);
            if (place is Place.Outside or Place.Protected) Warn(F("perm.warn.outside", shown));
        }

        public void Delete(string path, bool recursive)
        {
            var (place, shown) = Locate(path);
            string target = shown.Length > 0 ? shown : path;
            Add(recursive ? PermissionRisk.High : PermissionRisk.Medium, F(recursive ? "perm.do.deleteAll" : "perm.do.delete", target), T("perm.effect.delete"));
            NoteWhere(place, shown);
            if (recursive) Warn(F("perm.warn.deleteAll", target));
            if (place == Place.Outside) Warn(F("perm.warn.outside", target));
            if (place == Place.Protected) Warn(F("perm.warn.protected", target), PermissionRisk.Danger);
        }

        public void Folder(string path)
        {
            var (place, shown) = Locate(path);
            Add(PermissionRisk.Medium, F("perm.do.openFolder", shown.Length > 0 ? shown : path), T("perm.effect.folder"));
            NoteWhere(place, shown);
        }

        // ─── Network / browser ───

        public void Web(string url, bool redirect = false)
        {
            string host = HostOf(url);
            Add(PermissionRisk.Medium, F(redirect ? "perm.do.redirect" : "perm.do.web", host), T("perm.effect.network"), F("perm.where.internet", host));
            int q = url.IndexOf('?');
            if (q >= 0 && url.Length - q > 120) Warn(T("perm.warn.urlData"));
        }

        public void Browser(string tool, string url, string script)
        {
            string host = HostOf(url);
            if (script.Length > 0 || tool.Contains("eval") || tool.Contains("script") || tool.Contains("javascript"))
            {
                Add(PermissionRisk.High, F("perm.do.browserScript", host.Length > 0 ? host : "—"), T("perm.effect.browser"), host.Length > 0 ? F("perm.where.internet", host) : null);
                Warn(T("perm.warn.page"));
            }
            else
                Add(PermissionRisk.Medium, F("perm.do.browse", host.Length > 0 ? host : Short(url, 60)), T("perm.effect.browser"), host.Length > 0 ? F("perm.where.internet", host) : null);
        }

        private static string HostOf(string url)
        {
            var m = Regex.Match(url ?? "", @"^(?:[a-z][a-z0-9+.\-]*://)?(?:[^@/\s]*@)?([^/:\s?#]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : Short(url ?? "", 60);
        }

        // ─── MCP tools ───

        public void Mcp(string name, IReadOnlyDictionary<string, string> args)
        {
            var parts = name.Split("__", 3);
            string server = parts.Length > 1 ? parts[1] : "?";
            string leaf = parts.Length > 2 ? parts[2] : name;
            string argText = string.Join(", ", args.Take(3).Select(kv => kv.Key + "=" + Short(kv.Value, 40)));
            string l = leaf.ToLowerInvariant();
            string first = args.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
            if (Regex.IsMatch(l, @"delete|remove|drop|purge|destroy|trash"))
            { Add(PermissionRisk.High, F("perm.do.mcpDelete", leaf, server), T("perm.effect.external")); Warn(F("perm.warn.mcpDelete", leaf)); }
            else if (l.Contains("ssh") || l.Contains("remote"))
            {
                // A command on ANOTHER machine: its paths are that machine's, never this project's.
                string host = args.TryGetValue("profile", out var pr) && pr.Length > 0 ? pr : args.TryGetValue("host", out var h) && h.Length > 0 ? h : server;
                string c = args.TryGetValue("command", out var cmd) ? cmd : "";
                Add(PermissionRisk.High, c.Length > 0 ? F("perm.do.remoteCmd", Short(c, 70), host) : F("perm.do.mcpRun", leaf, server), T("perm.effect.remote"));
                AddWhere(F("perm.where.remote", host));
                Warn(F("perm.warn.remote", host));
                if (Regex.IsMatch(c, @"\b(rm\s+-[a-z]*r[a-z]*|rmdir|mkfs|dd\s+if=|shutdown|reboot|halt|poweroff|drop\s+database|truncate\s+table)", RegexOptions.IgnoreCase))
                    Warn(F("perm.warn.remoteDanger", Short(c, 70)), PermissionRisk.High);
                return;
            }
            else if (Regex.IsMatch(l, @"exec|execute|run|shell|eval|javascript|script|luau|code|terminal"))
            {
                Add(PermissionRisk.High, F("perm.do.mcpRun", leaf, server), T("perm.effect.run"));
                if (Regex.IsMatch(l, @"shell|bash|powershell|terminal|command") && args.TryGetValue("command", out var c) && c.Length > 0) Shell(c);
            }
            else if (Regex.IsMatch(l, @"send|post|publish|email|mail|message|reply|forward|say|share|upload|handoff|tweet"))
            { Add(PermissionRisk.High, F("perm.do.mcpSend", leaf, server), T("perm.effect.upload")); Warn(F("perm.warn.upload", server)); }
            else if (Regex.IsMatch(l, @"navigate|click|type|fill|press|hover|drag|select|tab|browser|page|screenshot"))
            {
                Add(PermissionRisk.Medium, F("perm.do.mcpBrowser", leaf, server), T("perm.effect.browser"));
                if (Regex.Match(first, @"https?://\S+") is { Success: true } u) AddWhere(F("perm.where.internet", HostOf(u.Value)));
            }
            else if (Regex.IsMatch(l, @"write|create|update|edit|set|patch|insert|add|save|modify|move|rename"))
                Add(PermissionRisk.Medium, F("perm.do.mcpChange", leaf, server), T("perm.effect.external"));
            else
                Add(PermissionRisk.Medium, F("perm.do.mcp", leaf, server), T("perm.effect.external"));
            if (argText.Length > 0) _effects.Add(F("perm.mcp.args", argText));
            AddWhere(F("perm.where.mcp", server));
        }

        // ─── Git tools (CluadeX's own git_* / gh_*) ───

        public void GitTool(string tool, IReadOnlyDictionary<string, string> args, string resource)
        {
            string a(string n) => args.TryGetValue(n, out var v) ? v : "";
            switch (tool)
            {
                case "git_push":
                    bool force = a("force") is "true" or "True" || a("args").Contains("--force") || a("args").Contains("-f");
                    GitPush(force); break;
                case "git_commit": Add(PermissionRisk.Medium, T("perm.do.gitCommit"), T("perm.effect.git")); break;
                case "git_pull": Add(PermissionRisk.Medium, T("perm.do.gitPull"), T("perm.effect.git")); break;
                case "git_clone":
                    string url = a("url").Length > 0 ? a("url") : resource;
                    Add(PermissionRisk.Medium, F("perm.do.gitClone", Short(url, 80)), T("perm.effect.install"), F("perm.where.internet", HostOf(url)));
                    if (Regex.Match(resource, @"→\s*(.+)$") is { Success: true } to) { var (pl, sh) = Locate(to.Groups[1].Value.Trim()); NoteWhere(pl, sh); }
                    break;
                case "git_checkout": Add(PermissionRisk.Medium, T("perm.do.gitSwitch"), T("perm.effect.git")); break;
                case "git_merge": Add(PermissionRisk.Medium, T("perm.do.gitMerge"), T("perm.effect.git")); break;
                case "git_worktree_remove": Delete(a("path").Length > 0 ? a("path") : resource, recursive: true); break;
                case "gh_pr_create" or "gh_issue_create":
                    Add(PermissionRisk.High, F("perm.do.ghPublish", tool == "gh_pr_create" ? "pull request" : "issue"), T("perm.effect.upload"), F("perm.where.internet", "github.com"));
                    Warn(F("perm.warn.upload", "github.com")); break;
                default: Add(PermissionRisk.Medium, F("perm.do.gitOther", tool.Replace('_', ' ')), T("perm.effect.git")); break;
            }
            NoteWhere(Place.Project, "");
        }

        private void GitPush(bool force)
        {
            Add(PermissionRisk.High, T(force ? "perm.do.gitPushForce" : "perm.do.gitPush"), T("perm.effect.upload"), F("perm.where.internet", "git remote"));
            Warn(force ? T("perm.warn.forcePush") : T("perm.warn.push"));
        }

        // ─── Shell commands ───

        public void Shell(string command, bool codeOnly = false)
        {
            command ??= "";
            if (command.Trim().Length == 0) { if (!codeOnly) Add(PermissionRisk.Medium, F("perm.do.unknown", "—"), T("perm.effect.unknown")); return; }
            if (DownloadRun.Match(command) is { Success: true } dr)
            {
                _downloadRun = true;
                string src = HostOf(Regex.Match(command, @"https?://[^\s'""\)]+").Value is { Length: > 0 } u ? u : dr.Value);
                Add(PermissionRisk.High, F("perm.do.downloadRun", src), T("perm.effect.run"), F("perm.where.internet", src));
                Warn(F("perm.warn.downloadRun", src));
            }
            var segments = SplitCommand(command);
            for (int i = 0; i < segments.Count; i++)
            {
                var (text, pipedFrom) = segments[i];
                Segment(text, codeOnly);
            }
            if (_what.Count == 0 && !codeOnly) Add(PermissionRisk.Medium, F("perm.do.unknown", Short(command, 60)), T("perm.effect.unknown"));
        }

        private void Segment(string text, bool codeOnly)
        {
            var words = Words(text);
            if (words.Count == 0) return;
            // Unwrap launchers: sudo, cmd /c, powershell -Command "…", & "x.exe", call, start.
            int k = 0;
            while (k < words.Count)
            {
                string w0 = Verb(words[k]);
                if (w0 is "sudo" or "call" or "&" or "start" or "nohup" or "time") { k++; continue; }
                if (w0 is "cmd" && k + 1 < words.Count && words[k + 1].Equals("/c", StringComparison.OrdinalIgnoreCase)) { k += 2; continue; }
                if (w0 is "powershell" or "pwsh")
                {
                    int c = words.FindIndex(k + 1, x => x.Equals("-command", StringComparison.OrdinalIgnoreCase) || x.Equals("-c", StringComparison.OrdinalIgnoreCase));
                    if (c >= 0 && c + 1 < words.Count) { Shell(string.Join(' ', words.Skip(c + 1)), codeOnly); return; }
                    int f = words.FindIndex(k + 1, x => x.Equals("-file", StringComparison.OrdinalIgnoreCase));
                    if (f >= 0 && f + 1 < words.Count) { Script(words[f + 1]); return; }
                }
                break;
            }
            if (k >= words.Count) return;
            string verb = Verb(words[k]);
            var rest = words.Skip(k + 1).ToList();
            var flags = rest.Where(IsFlag).Select(x => x.ToLowerInvariant()).ToList();
            var targets = rest.Where(x => !IsFlag(x)).ToList();
            string line = text.Trim();

            // Output written to a file: "> out.txt", Set-Content, Out-File.
            var redirect = Regex.Match(line, @"(?<![0-9&=\-<>])>{1,2}\s*([^\s|&;>()]+)");
            if (redirect.Success && redirect.Groups[1].Value is var rt && !rt.Equals("nul", StringComparison.OrdinalIgnoreCase) && rt != "/dev/null" && !rt.StartsWith("&"))
                AtPath(PermissionRisk.Medium, "perm.do.writeRedirect", rt, "perm.effect.write");

            switch (verb)
            {
                case "rm" or "del" or "erase" or "rd" or "rmdir" or "remove-item" or "ri" or "rimraf" or "unlink" or "shred":
                {
                    bool recursive = verb is "rimraf" || (verb is "rd" or "rmdir" && flags.Contains("/s"))
                        || flags.Any(f => f is "-r" or "-rf" or "-fr" or "-rv" or "-vr" or "-rfv" or "-frv" or "-vrf" or "-fvr" or "-rec"
                            or "-recurse" or "--recursive" or "/s");
                    var paths = targets.Where(x => !Regex.IsMatch(x, @"^-(path|literalpath)$", RegexOptions.IgnoreCase)).ToList();
                    if (paths.Count == 0) Add(PermissionRisk.Medium, F("perm.do.delete", "—"), T("perm.effect.delete"));
                    foreach (var p in paths.Take(4)) Delete(p, recursive);
                    break;
                }
                case "mv" or "move" or "ren" or "rename" or "move-item" or "rename-item" or "mi":
                    foreach (var p in targets.Take(2)) AtPath(PermissionRisk.Medium, "perm.do.move", p, "perm.effect.write");
                    if (targets.Count == 0) Add(PermissionRisk.Medium, F("perm.do.move", "—"), T("perm.effect.write"));
                    break;
                case "cp" or "copy" or "xcopy" or "robocopy" or "copy-item" or "cpi":
                    if (targets.Count >= 2) AtPath(PermissionRisk.Medium, "perm.do.copy", targets[^1], "perm.effect.write");
                    else Add(PermissionRisk.Medium, F("perm.do.copy", targets.FirstOrDefault() ?? "—"), T("perm.effect.write"));
                    if (verb == "robocopy" && flags.Any(f => f is "/mir" or "/purge")) Warn(F("perm.warn.deleteAll", targets.LastOrDefault() ?? "—"));
                    break;
                case "find" when rest.Any(x => x is "-delete" or "-exec" or "-execdir"):
                    if (rest.Contains("-delete")) Delete(targets.FirstOrDefault() ?? ".", recursive: true);
                    else Add(PermissionRisk.Medium, F("perm.do.script", Short(line, 60)), T("perm.effect.run"));
                    break;
                case "mkdir" or "md" or "new-item" or "ni" or "touch" or "set-content" or "add-content" or "out-file" or "ac" or "tee":
                    foreach (var p in targets.Where(x => !x.StartsWith("-")).Take(2))
                        AtPath(PermissionRisk.Medium, verb is "mkdir" or "md" ? "perm.do.mkdir" : "perm.do.writeRedirect", p, "perm.effect.write");
                    break;
                case "ls" or "dir" or "cat" or "type" or "get-content" or "gc" or "echo" or "write-output" or "write-host" or "pwd" or "cd"
                    or "set-location" or "where" or "which" or "findstr" or "grep" or "rg" or "head" or "tail" or "wc" or "get-childitem"
                    or "gci" or "select-string" or "sls" or "tree" or "get-item" or "test-path" or "resolve-path" or "find" or "sort" or "uniq"
                    or "less" or "more" or "printf" or "date" or "whoami" or "hostname" or "nvidia-smi" or "get-process" or "tasklist" or "ps"
                    or "env" or "printenv" or "set" or "measure-object" or "select-object" or "where-object" or "foreach-object" or "format-table":
                    Add(PermissionRisk.Low, F("perm.do.read", verb), T("perm.effect.read"));
                    break;
                case "git":
                    Git(rest);
                    break;
                case "npm" or "pnpm" or "yarn" or "bun" or "npx" or "pip" or "pip3" or "uv" or "poetry" or "dotnet" or "nuget" or "cargo"
                    or "go" or "gem" or "composer" or "flutter" or "dart" or "mvn" or "gradle" or "gradlew" or "make" or "cmake" or "msbuild"
                    or "node" or "python" or "python3" or "py" or "pytest" or "deno" or "java" or "ruby" or "php" or "tsc" or "vite" or "jest"
                    or "vitest" or "playwright" or "docker" or "docker-compose" or "kubectl":
                    Tooling(verb, rest, flags);
                    break;
                case "winget" or "choco" or "scoop" or "apt" or "apt-get" or "brew" or "msiexec" or "install-module" or "install-package":
                    Add(PermissionRisk.High, F("perm.do.installGlobal", Short(line, 60)), T("perm.effect.install"), T("perm.where.machine"));
                    Warn(T("perm.warn.globalInstall"));
                    break;
                case "curl" or "wget" or "invoke-webrequest" or "iwr" or "invoke-restmethod" or "irm" or "start-bitstransfer" or "http" or "httpie":
                    Network(verb, rest, flags, line);
                    break;
                case "ssh" or "scp" or "sftp" or "rsync" or "ftp" or "telnet" or "nc" or "ncat" or "plink" or "pscp":
                {
                    string host = targets.Select(x => Regex.Match(x, @"(?:[^@\s]+@)?([A-Za-z0-9.\-]+\.[A-Za-z]{2,}|\d{1,3}(?:\.\d{1,3}){3})").Groups[1].Value)
                                         .FirstOrDefault(h => h.Length > 0) ?? targets.FirstOrDefault() ?? "?";
                    Add(PermissionRisk.High, F("perm.do.remote", host), T("perm.effect.upload"), F("perm.where.internet", host));
                    Warn(verb is "scp" or "sftp" or "rsync" or "ftp" or "pscp" ? F("perm.warn.upload", host) : F("perm.warn.remote", host));
                    break;
                }
                case "kill" or "taskkill" or "stop-process" or "spps" or "pkill" or "killall":
                    Add(PermissionRisk.High, F("perm.do.kill", Short(string.Join(' ', targets), 50)), T("perm.effect.run"), T("perm.where.machine"));
                    Warn(T("perm.warn.kill"));
                    break;
                case "reg" or "regedit" or "setx" or "set-executionpolicy" or "netsh" or "sc.exe" or "bcdedit" or "diskpart" or "format"
                    or "systemctl" or "service" or "launchctl" or "crontab" or "ufw" or "iptables" or "firewall-cmd" or "useradd" or "passwd"
                    or "shutdown" or "restart-computer" or "stop-computer" or "icacls" or "takeown" or "chmod" or "chown" or "attrib"
                    or "set-itemproperty" or "new-itemproperty" or "remove-itemproperty" or "schtasks" or "net" or "wsl" or "mount" or "mkfs":
                    Add(PermissionRisk.High, F("perm.do.system", verb), T("perm.effect.system"), T("perm.where.machine"));
                    Warn(F("perm.warn.system", Short(line, 70)));
                    if (verb is "format" or "diskpart" or "mkfs" or "bcdedit") Warn(F("perm.warn.system", Short(line, 70)), PermissionRisk.Danger);
                    break;
                case "iex" or "invoke-expression" or "eval" or "sh" or "bash" or "zsh" or "source" or ".":
                    if (_downloadRun) break;   // already said: the downloaded script runs
                    Add(PermissionRisk.Medium, F("perm.do.script", Short(string.Join(' ', targets), 60)), T("perm.effect.run"));
                    break;
                default:
                    if (Regex.IsMatch(verb, @"\.(ps1|bat|cmd|sh|py|js|exe)$") || words[k].Contains('\\') || words[k].Contains('/'))
                        Script(words[k]);
                    else if (!codeOnly)
                        Add(PermissionRisk.Medium, F("perm.do.unknown", Short(line, 60)), T("perm.effect.unknown"));
                    else if (Regex.IsMatch(line, @"\b(os\.remove|shutil\.rmtree|unlink|rmSync|Remove-Item|File\.Delete|Directory\.Delete)\b"))
                        Add(PermissionRisk.High, F("perm.do.delete", "—"), T("perm.effect.delete"));
                    else if (Regex.IsMatch(line, @"\b(requests\.(post|put)|fetch\(|urllib|HttpClient|axios\.(post|put)|WebClient|socket)\b"))
                        Add(PermissionRisk.High, F("perm.do.upload", "—"), T("perm.effect.network"));
                    break;
            }
        }

        private void Script(string path)
        {
            var (place, shown) = Locate(path);
            Add(PermissionRisk.Medium, F("perm.do.script", shown.Length > 0 ? shown : path), T("perm.effect.run"));
            NoteWhere(place, shown);
            if (place is Place.Outside or Place.Protected) Warn(F("perm.warn.outside", shown));
        }

        private void Git(List<string> rest)
        {
            string sub = rest.FirstOrDefault(x => !x.StartsWith("-"))?.ToLowerInvariant() ?? "";
            var flags = rest.Where(IsFlag).Select(x => x.ToLowerInvariant()).ToList();
            switch (sub)
            {
                case "status" or "log" or "diff" or "show" or "blame" or "rev-parse" or "ls-files" or "remote" or "config" when sub != "config" || flags.Contains("--get") || flags.Contains("--list"):
                    Add(PermissionRisk.Low, F("perm.do.read", "git " + sub), T("perm.effect.read")); break;
                case "branch" when !flags.Any(f => f is "-d" or "-D" or "--delete" or "-m" or "-M"):
                    Add(PermissionRisk.Low, F("perm.do.read", "git branch"), T("perm.effect.read")); break;
                case "push":
                    GitPush(flags.Any(f => f is "--force" or "-f" or "--force-with-lease" || f.StartsWith("--force"))
                            || rest.Any(x => x.StartsWith("+"))); break;
                case "commit": Add(PermissionRisk.Medium, T("perm.do.gitCommit"), T("perm.effect.git")); break;
                case "add": Add(PermissionRisk.Low, F("perm.do.gitOther", "add"), T("perm.effect.git")); break;
                case "reset" when flags.Contains("--hard"):
                    Add(PermissionRisk.High, T("perm.do.gitReset"), T("perm.effect.git")); Warn(T("perm.warn.discard")); break;
                case "clean":
                    Add(PermissionRisk.High, T("perm.do.gitClean"), T("perm.effect.delete")); Warn(T("perm.warn.discard")); break;
                case "checkout" or "switch" or "restore":
                    Add(PermissionRisk.Medium, T("perm.do.gitSwitch"), T("perm.effect.git"));
                    if (rest.Contains("--") || rest.Contains(".") || sub == "restore") Warn(T("perm.warn.discard"), PermissionRisk.Medium);
                    break;
                case "pull" or "fetch": Add(PermissionRisk.Medium, T("perm.do.gitPull"), T("perm.effect.git"), F("perm.where.internet", "git remote")); break;
                case "clone":
                    string url = rest.Skip(1).FirstOrDefault(x => !x.StartsWith("-")) ?? "";
                    Add(PermissionRisk.Medium, F("perm.do.gitClone", Short(url, 80)), T("perm.effect.install"), F("perm.where.internet", HostOf(url))); break;
                case "merge" or "rebase" or "cherry-pick": Add(PermissionRisk.Medium, T("perm.do.gitMerge"), T("perm.effect.git")); break;
                case "branch": Add(PermissionRisk.Medium, F("perm.do.gitOther", "branch " + string.Join(' ', flags)), T("perm.effect.git")); break;
                default: Add(PermissionRisk.Medium, F("perm.do.gitOther", sub.Length > 0 ? sub : "—"), T("perm.effect.git")); break;
            }
            NoteWhere(Place.Project, "");
        }

        private void Tooling(string verb, List<string> rest, List<string> flags)
        {
            string sub = rest.FirstOrDefault(x => !IsFlag(x))?.ToLowerInvariant() ?? "";
            string shown = Short(verb + (sub.Length > 0 ? " " + sub : ""), 40);
            bool install = (verb is "npm" or "pnpm" or "yarn" or "bun" && sub is "install" or "i" or "add" or "ci")
                           || (verb is "pip" or "pip3" or "uv" or "poetry" or "gem" or "composer" && sub is "install" or "add" or "require")
                           || (verb is "dotnet" && sub is "add" or "restore" or "tool") || (verb is "cargo" && sub is "add" or "install")
                           || (verb is "go" && sub is "get" or "install") || (verb is "flutter" or "dart" && sub is "pub");
            if (verb == "npx") install = true;   // npx fetches the package when it is not there yet
            bool global = flags.Any(f => f is "-g" or "--global" or "--user") || (verb is "dotnet" && rest.Contains("-g"));
            if (install && global)
            {
                Add(PermissionRisk.High, F("perm.do.installGlobal", shown), T("perm.effect.install"), T("perm.where.machine"));
                Warn(T("perm.warn.globalInstall"));
            }
            else if (install)
            {
                Add(PermissionRisk.Medium, F("perm.do.install", shown), T("perm.effect.install"));
                NoteWhere(Place.Project, "");
            }
            else if (verb is "docker" or "docker-compose" or "kubectl")
                Add(PermissionRisk.Medium, F("perm.do.docker", shown), T("perm.effect.run"));
            else if (sub is "publish" && verb is "npm" or "dotnet" or "cargo" or "pnpm" or "yarn")
            {
                Add(PermissionRisk.High, F("perm.do.publish", shown), T("perm.effect.upload"));
                Warn(F("perm.warn.upload", verb + " registry"));
            }
            else
            {
                Add(PermissionRisk.Medium, F("perm.do.build", shown), T("perm.effect.run"));
                NoteWhere(Place.Project, "");
            }
        }

        private void Network(string verb, List<string> rest, List<string> flags, string line)
        {
            string url = rest.FirstOrDefault(x => Regex.IsMatch(x, @"^(https?|ftp)://", RegexOptions.IgnoreCase))
                         ?? rest.FirstOrDefault(x => !IsFlag(x) && x.Contains('.')) ?? "";
            string host = HostOf(url);
            bool sends = flags.Any(f => f is "-d" or "--data" or "--data-raw" or "--data-binary" or "--data-urlencode" or "-f" or "--form"
                                      or "-t" or "--upload-file" or "-body" or "-infile" or "--post-data" or "--post-file")
                         || Regex.IsMatch(line, @"(-X|--request|-Method)\s+['""]?(POST|PUT|PATCH|DELETE)", RegexOptions.IgnoreCase);
            if (sends)
            {
                Add(PermissionRisk.High, F("perm.do.upload", host), T("perm.effect.upload"), F("perm.where.internet", host));
                Warn(F("perm.warn.upload", host));
            }
            else
                Add(PermissionRisk.Medium, F("perm.do.download", host), T("perm.effect.network"), F("perm.where.internet", host));
            int q = url.IndexOf('?');
            if (q >= 0 && url.Length - q > 120) Warn(T("perm.warn.urlData"));
            int o = rest.FindIndex(x => x is "-o" or "-O" or "--output" or "-OutFile" or "-outfile");
            if (o >= 0 && o + 1 < rest.Count) AtPath(PermissionRisk.Medium, "perm.do.writeRedirect", rest[o + 1], "perm.effect.write");
        }

        public PlainPermission Build(string who, string tool, string detail, string? danger, string note = "")
        {
            if (_what.Count == 0) Add(PermissionRisk.Medium, F("perm.do.tool", tool), T("perm.effect.unknown"));
            string what = string.Join(T("perm.join"), _what.Where(w => w.Length > 0));
            if (_hidden > 0) what += " " + F("perm.do.more", _hidden);
            string riskKey = _risk switch
            {
                PermissionRisk.Low => "perm.risk.low",
                PermissionRisk.Medium => "perm.risk.medium",
                PermissionRisk.High => "perm.risk.high",
                _ => "perm.risk.danger",
            };
            var tech = new StringBuilder();
            tech.Append(F("perm.tech.tool", tool));
            if (!string.IsNullOrWhiteSpace(detail)) tech.Append('\n').Append(detail.Length > 6000 ? detail[..6000] + "…" : detail);
            if (!string.IsNullOrWhiteSpace(danger)) tech.Append("\n\n").Append(danger);
            return new PlainPermission
            {
                Headline = F("perm.headline", who, what),
                What = what,
                Where = _where.Count > 0 ? string.Join(" · ", _where.Take(3)) : _root != null ? F("perm.where.project", ProjectName) : T("perm.where.unknown"),
                Effect = string.Join(" ", _effects.Take(3)),
                Risk = _risk,
                RiskText = T(riskKey),
                Warnings = _warnings,
                Note = note,
                Technical = tech.ToString(),
            };
        }

        // ─── Command-line reading ───

        private static readonly Regex DownloadRun = new(
            @"((curl|wget|iwr|irm|invoke-webrequest|invoke-restmethod)\b[^|]*\|\s*(sh|bash|zsh|iex|invoke-expression|python3?|node|pwsh|powershell)\b)"
            + @"|((iex|invoke-expression)\s*\(?\s*(\(?\s*(irm|iwr|invoke-restmethod|invoke-webrequest|new-object\s+net\.webclient)))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool IsFlag(string w) =>
            w.Length > 1 && (w[0] == '-' || (w[0] == '/' && Regex.IsMatch(w, @"^/[A-Za-z?]{1,3}(:\S*)?$")));

        /// <summary>The program a word names: no folder, no .exe/.cmd, lower case.</summary>
        private static string Verb(string word)
        {
            string w = word.Trim().Trim('"', '\'').Replace('\\', '/');
            int slash = w.LastIndexOf('/');
            if (slash >= 0 && slash < w.Length - 1) w = w[(slash + 1)..];
            w = w.ToLowerInvariant();
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com" })
                if (w.EndsWith(ext) && w != "sc.exe") { w = w[..^ext.Length]; break; }
            return w;
        }

        /// <summary>A command line cut where one command ends and the next starts (&amp;&amp; || ; | newline), outside quotes.</summary>
        internal static List<(string Text, bool Piped)> SplitCommand(string command)
        {
            var parts = new List<(string, bool)>();
            var sb = new StringBuilder();
            char quote = '\0';
            bool piped = false;
            void Cut(bool nextPiped) { if (sb.ToString().Trim().Length > 0) parts.Add((sb.ToString().Trim(), piped)); sb.Clear(); piped = nextPiped; }
            for (int i = 0; i < command.Length; i++)
            {
                char c = command[i];
                if (quote != '\0') { if (c == quote) quote = '\0'; sb.Append(c); continue; }
                if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
                if (c is '\n' or '\r' or ';') { Cut(false); continue; }
                if (c == '&' && i + 1 < command.Length && command[i + 1] == '&') { Cut(false); i++; continue; }
                if (c == '|' && i + 1 < command.Length && command[i + 1] == '|') { Cut(false); i++; continue; }
                if (c == '|') { Cut(true); continue; }
                if (c == '&' && sb.ToString().Trim().Length > 0 && (i + 1 >= command.Length || command[i + 1] == ' ') && (i == 0 || command[i - 1] == ' '))
                { Cut(false); continue; }
                sb.Append(c);
            }
            Cut(false);
            return parts;
        }

        /// <summary>Words of one command, quotes respected and removed.</summary>
        private static List<string> Words(string text)
        {
            var words = new List<string>();
            var sb = new StringBuilder();
            char quote = '\0';
            foreach (char c in text)
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); continue; }
                if (c is '"' or '\'') { quote = c; continue; }
                if (char.IsWhiteSpace(c)) { if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); } continue; }
                if (c is '(' or ')' && sb.Length == 0) continue;
                sb.Append(c);
            }
            if (sb.Length > 0) words.Add(sb.ToString());
            // Redirection targets are not arguments.
            for (int i = words.Count - 1; i >= 0; i--)
                if (words[i].StartsWith(">") || words[i].StartsWith("2>"))
                {
                    if (words[i].TrimStart('2', '>').Length == 0 && i + 1 < words.Count) words.RemoveAt(i + 1);
                    words.RemoveAt(i);
                }
            return words;
        }
    }
}
