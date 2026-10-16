using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>
/// Provides file system operations for the coding agent.
/// All paths are validated to stay within the working directory.
/// </summary>
public class FileSystemService
{
    private string _workingDirectory = string.Empty;

    // Read-before-edit / stale-write tracking (Claude Code-style): full path → (mtime ticks, size) last seen
    // by the agent via read_file (or after it wrote the file). Edits consult this; cleared per project.
    // ConcurrentDictionary: read_file tools run in PARALLEL, so MarkKnown can be called from several
    // threads at once — a plain Dictionary would corrupt/throw under that race.
    private readonly ConcurrentDictionary<string, (long ticks, long size)> _readSnapshots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the working directory changes — lets HookService re-evaluate workspace
    /// trust for the new folder and drop stale project hooks.</summary>
    public event Action? OnWorkingDirectoryChanged;

    /// <summary>The project folder: the one a running agent started in (AgentRunScope) — so opening another project
    /// while it runs never moves its tools — else the one the user has open. Setting it always changes the APP's
    /// project (only the UI sets it; a run's folder is fixed when the run starts).</summary>
    public string WorkingDirectory
    {
        get => Root;
        set
        {
            if (string.Equals(_workingDirectory, value, StringComparison.OrdinalIgnoreCase)) return;
            _workingDirectory = value;
            // Not cleared: entries are full paths, so another project's never match — and a run still working in
            // the previous project keeps its "has read this file" record.
            OnWorkingDirectoryChanged?.Invoke();
        }
    }

    /// <summary>The project folder the user has open, ignoring any run (for the UI).</summary>
    public string AppWorkingDirectory => _workingDirectory;

    private string Root => AgentRunScope.Current?.WorkingDirectory ?? _workingDirectory;

    public bool HasWorkingDirectory => !string.IsNullOrEmpty(Root) && Directory.Exists(Root);

    // ─── Read File ───
    public string ReadFile(string relativePath)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");

        // Check file size (limit to 500KB to avoid memory issues)
        var fi = new FileInfo(fullPath);
        if (fi.Length > 512 * 1024)
            throw new InvalidOperationException($"File too large ({fi.Length / 1024}KB). Max 500KB.");

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }

    // ─── Read a line range (1-based), prefixed with line numbers ───
    /// <summary>
    /// Read a slice of a file as "N\ttext" lines (1-based). Lets the agent page through big files and
    /// reference code by line number (e.g. to cross-check an lsp_diagnostics error). Returns the slice
    /// text plus the file's total line count. Capped at 16 MB.
    /// </summary>
    public (string text, int total) ReadLines(string relativePath, int startLine, int maxLines)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");
        if (new FileInfo(fullPath).Length > 16 * 1024 * 1024)
            throw new InvalidOperationException($"File too large for a ranged read (>16MB): {relativePath}");

        var all = File.ReadAllLines(fullPath, Encoding.UTF8);
        int total = all.Length;
        if (startLine < 1) startLine = 1;
        if (maxLines < 1) maxLines = 2000;

        var sb = new StringBuilder();
        int end = Math.Min(startLine + maxLines - 1, total);
        for (int i = startLine; i <= end; i++)
            sb.Append(i).Append('\t').Append(all[i - 1]).Append('\n');
        if (startLine > total)
            sb.Append($"(file has only {total} line(s))\n");
        return (sb.ToString(), total);
    }

    /// <summary>What <see cref="ReadForAgent"/> returns: the numbered slice plus everything the caller
    /// needs to tell the model how to get the rest.</summary>
    public sealed record AgentRead(
        string Text, int TotalLines, int FromLine, int ToLine,
        bool CutByBudget, bool IsBinary, long SizeBytes, bool IsValidUtf8);

    /// <summary>Longest line shown verbatim; beyond it a minified bundle would swallow the whole budget.</summary>
    public const int MaxLineChars = 2000;

    /// <summary>
    /// The agent's read: numbered "N\t" lines from <paramref name="startLine"/>, stopping at
    /// <paramref name="maxLines"/> lines OR <paramref name="charBudget"/> characters, whichever comes first.
    ///
    /// A whole-file read used to return the entire file — a 5,000-line file came back as 48 KB, more than
    /// a 12k-token local model's whole context, in a single tool call. Every read now fits the budget
    /// and reports where it stopped so the model can page with offset.
    /// </summary>
    public AgentRead ReadForAgent(string relativePath, int startLine, int maxLines, int charBudget)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (Directory.Exists(fullPath))
            throw new InvalidOperationException($"'{relativePath}' is a directory, not a file — use list_files or glob to see what it contains.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");

        long size = new FileInfo(fullPath).Length;
        if (size > 16 * 1024 * 1024)
            throw new InvalidOperationException($"'{relativePath}' is {size / (1024 * 1024)} MB — too large to read as text. Use grep_search to find the part you need.");
        if (CluadeX.Helpers.TextFileCodec.LooksBinary(fullPath))
            return new AgentRead("", 0, 0, 0, false, true, size, true);

        var tf = CluadeX.Helpers.TextFileCodec.Read(fullPath);
        if (tf.Text.Length == 0)
            return new AgentRead("(the file is empty)\n", 0, 1, 0, false, false, size, true);
        var lines = tf.Text.Replace("\r\n", "\n").Split('\n');
        int total = lines.Length;
        if (total > 0 && tf.Text.EndsWith("\n")) total--;   // "a\nb\n" is two lines, not three

        if (startLine < 1) startLine = 1;
        if (maxLines < 1) maxLines = 2000;
        if (startLine > total)
            return new AgentRead($"(the file has only {total} line(s))\n", total, startLine, total, false, false, size, tf.IsValidUtf8);

        var sb = new StringBuilder();
        int shown = 0, last = startLine - 1;
        bool cut = false;
        for (int i = startLine; i <= total && shown < maxLines; i++)
        {
            string line = lines[i - 1];
            if (line.Length > MaxLineChars)
                line = line[..MaxLineChars] + $"… [+{line.Length - MaxLineChars:N0} more characters on this line]";
            string row = $"{i}\t{line}\n";
            if (shown > 0 && sb.Length + row.Length > charBudget) { cut = true; break; }
            sb.Append(row);
            shown++;
            last = i;
        }
        return new AgentRead(sb.ToString(), total, startLine, last, cut, false, size, tf.IsValidUtf8);
    }

    /// <summary>True when the agent has read or written this file in the current session.</summary>
    public bool HasSeen(string relativePath)
    {
        try { return _readSnapshots.ContainsKey(ResolveSafePath(relativePath)); }
        catch { return false; }
    }

    // ─── Multi-edit (atomic batch find/replace on one file) ───
    /// <summary>
    /// Apply several find/replace edits to ONE file in a single atomic write: every edit is applied
    /// in order to an in-memory copy, and the file is written ONLY if all of them match. If any edit
    /// can't be applied, nothing is written (the file is left untouched) and the failing edit is named.
    /// Reuses the same newline-tolerant + whitespace-flexible matching as EditFile.
    /// </summary>
    public (bool ok, int applied, string message) MultiEdit(
        string relativePath, IReadOnlyList<(string find, string replace, bool replaceAll)> edits)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");
        if (edits == null || edits.Count == 0)
            throw new ArgumentException("No edits provided.");

        var tf = CluadeX.Helpers.TextFileCodec.Read(fullPath);
        if (!tf.IsValidUtf8) throw new InvalidOperationException(CluadeX.Helpers.TextFileCodec.NotUtf8Message(relativePath));
        string raw = tf.Text;
        bool fileUsesCrLf = UsesCrLf(raw);

        var (ok, content, applied, message) = ApplyMultiEdit(raw.Replace("\r\n", "\n"), edits);
        if (!ok) return (false, applied, message);   // atomic: nothing written

        string outContent = fileUsesCrLf ? content.Replace("\n", "\r\n") : content;
        CluadeX.Helpers.TextFileCodec.Write(fullPath, outContent, tf.HasBom);
        return (true, applied, message);
    }

    /// <summary>The file's DOMINANT newline is CRLF (one stray CRLF in an LF file does not count).</summary>
    private static bool UsesCrLf(string raw)
    {
        int crlf = CountOccurrences(raw, "\r\n");
        return crlf > CountOccurrences(raw, "\n") - crlf;
    }

    /// <summary>Compute the result of a MultiEdit WITHOUT writing — for the pre-apply diff preview.</summary>
    public (bool ok, string before, string after, string message)? PreviewMultiEdit(
        string relativePath, IReadOnlyList<(string find, string replace, bool replaceAll)> edits)
    {
        try
        {
            string fullPath = ResolveSafePath(relativePath);
            if (!File.Exists(fullPath) || edits == null || edits.Count == 0) return null;
            if (new FileInfo(fullPath).Length > 1_048_576) return null;
            string content = CluadeX.Helpers.TextFileCodec.Read(fullPath).Text.Replace("\r\n", "\n");
            var (ok, after, _, message) = ApplyMultiEdit(content, edits);
            return (ok, content, after, message);
        }
        catch { return null; }
    }

    /// <summary>Pure core of MultiEdit — applies edits to normalized (\n) content; no IO.</summary>
    private static (bool ok, string content, int applied, string message) ApplyMultiEdit(
        string content, IReadOnlyList<(string find, string replace, bool replaceAll)> edits)
    {
        int applied = 0;
        for (int i = 0; i < edits.Count; i++)
        {
            var (find, replace, replaceAll) = edits[i];
            if (string.IsNullOrEmpty(find))
                return (false, content, applied, $"edit #{i + 1}: 'find' is empty.");

            var outcome = ApplyOneEdit(content, find, replace, replaceAll);
            if (outcome.Ambiguous > 1)
                return (false, content, applied, $"edit #{i + 1}: 'find' matches {outcome.Ambiguous} times — add surrounding context to make it unique, or set replace_all.");
            if (outcome.Content == null)
                return (false, content, applied, $"edit #{i + 1}: text not found — re-read the file and copy the exact block.");
            content = outcome.Content;
            applied++;
        }
        return (true, content, applied, $"{applied} edit(s) applied");
    }

    /// <summary>Result of matching one find/replace against \n-normalized content.</summary>
    private readonly record struct EditOutcome(string? Content, int Replacements, int Ambiguous);

    /// <summary>
    /// The single matching core behind edit_file, multi_edit and the approval-dialog preview, so a
    /// preview can never disagree with what the edit then does. In order:
    ///   1. exact match (newline-normalized);
    ///   2. whitespace-tolerant match — only when it lands on exactly one place — with the replacement
    ///      re-indented to the file's own indentation;
    ///   3. the same two again with read_file's "N\t" line-number prefixes removed, because a model that
    ///      has just read a numbered file routinely pastes those prefixes into 'find'.
    /// Content == null with Ambiguous &lt;= 1 means "not found".
    /// </summary>
    private static EditOutcome ApplyOneEdit(string content, string find, string replace, bool replaceAll)
    {
        string findN = find.Replace("\r\n", "\n");
        string replaceN = replace.Replace("\r\n", "\n");

        var first = MatchOnce(content, findN, replaceN, replaceAll);
        if (first.Content != null || first.Ambiguous > 1) return first;

        if (!HasLineNumberPrefixes(findN)) return first;
        string strippedFind = StripLineNumberPrefixes(findN);
        // Only strip the replacement when it carries the same prefixes; otherwise its digits are content.
        string strippedReplace = HasLineNumberPrefixes(replaceN) ? StripLineNumberPrefixes(replaceN) : replaceN;
        return MatchOnce(content, strippedFind, strippedReplace, replaceAll);
    }

    private static EditOutcome MatchOnce(string content, string findN, string replaceN, bool replaceAll)
    {
        if (findN.Length == 0) return new EditOutcome(null, 0, 0);
        int count = CountOccurrences(content, findN);
        if (count == 1 || (count > 1 && replaceAll))
            return new EditOutcome(content.Replace(findN, replaceN), count, 0);
        if (count > 1)
            return new EditOutcome(null, 0, count);
        string? flexed = TryFlexibleReplace(content, findN, replaceN);
        return flexed != null ? new EditOutcome(flexed, 1, 0) : new EditOutcome(null, 0, 0);
    }

    private static readonly Regex LineNumberPrefix = new(@"^\s*\d+\t", RegexOptions.Compiled);

    /// <summary>Every non-blank line starts with read_file's "N\t" prefix.</summary>
    private static bool HasLineNumberPrefixes(string text)
    {
        var nonBlank = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        return nonBlank.Count > 0 && nonBlank.All(l => LineNumberPrefix.IsMatch(l));
    }

    private static string StripLineNumberPrefixes(string text) =>
        string.Join("\n", text.Split('\n').Select(l => LineNumberPrefix.Replace(l, "", 1)));

    // ─── Safe raw read (for diff capture) ───
    /// <summary>
    /// Read a file's raw text for before/after diffing. Returns null instead of throwing when the file
    /// is missing, too large, or unreadable — diffing is best-effort and must never break the edit itself.
    /// </summary>
    public string? TryReadRaw(string relativePath, int maxBytes = 1_048_576)
    {
        try
        {
            string fullPath = ResolveSafePath(relativePath);
            if (!File.Exists(fullPath)) return null;
            if (new FileInfo(fullPath).Length > maxBytes) return null;
            return File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch { return null; }
    }

    // ─── Read-before-edit / stale-write guard (Claude Code-style) ───
    private (long ticks, long size)? SnapshotOf(string fullPath)
    {
        try { var fi = new FileInfo(fullPath); return fi.Exists ? (fi.LastWriteTimeUtc.Ticks, fi.Length) : null; }
        catch { return null; }
    }

    /// <summary>Record that the agent has seen a file's CURRENT contents (call after a successful read or write).</summary>
    public void MarkKnown(string relativePath)
    {
        try
        {
            string full = ResolveSafePath(relativePath);
            var snap = SnapshotOf(full);
            if (snap != null) _readSnapshots[full] = snap.Value;
        }
        catch { /* tracking is best-effort, never throws into a tool */ }
    }

    /// <summary>
    /// Returns null if the file may be modified, or an actionable message to send back to the model when it
    /// must not: an existing file that was never read this session, or one that changed on disk since the
    /// last read. New files (not on disk) are always allowed.
    /// </summary>
    public string? CheckCanModify(string relativePath)
    {
        string full;
        try { full = ResolveSafePath(relativePath); } catch { return null; }
        if (!File.Exists(full)) return null;   // creating a brand-new file — nothing to read first

        if (!_readSnapshots.TryGetValue(full, out var seen))
        {
            // NEVER-read file: auto-snapshot and ALLOW the edit instead of dead-ending. Weak local
            // models frequently jump straight to edit_file, hit "you must read first", and then can't
            // recover (they apologize in prose instead of reading) — a false finish. The edit itself
            // still validates its find-block against the real on-disk content via the fuzzy matcher,
            // so a blind edit fails cleanly with a nearest-match hint rather than being blocked outright.
            var snap0 = SnapshotOf(full);
            if (snap0 != null) _readSnapshots[full] = snap0.Value;
            return null;
        }

        var now = SnapshotOf(full);
        if (now != null && (now.Value.ticks != seen.ticks || now.Value.size != seen.size))
            return $"'{relativePath}' changed on disk since you last read it — read_file it again before editing (so you don't overwrite that change).";

        return null;
    }

    // ─── Build-command detection (for the verify loop / run_build tool) ───
    /// <summary>
    /// Best-effort detection of the project's build / type-check command from marker files in the working
    /// directory root. Returns null when no build system is recognised (e.g. plain scripts).
    /// </summary>
    public string? DetectBuildCommand() => DetectProjectCommand(test: false)?.Command;

    /// <summary>
    /// Best-effort detection of the project's TEST command. Returns null when no test setup is recognised.
    /// Mirrors <see cref="DetectBuildCommand"/>.
    /// </summary>
    public string? DetectTestCommand() => DetectProjectCommand(test: true)?.Command;

    /// <summary>A detected build/test command plus where it was found, for the tool's report.</summary>
    public sealed record ProjectCommand(string Command, string FoundIn);

    /// <summary>
    /// Find the build (or test) command. Looks at the project root first, then BELOW it: a repository
    /// whose only project lives in a subfolder (src/App/App.csproj, backend/package.json) used to get
    /// "couldn't detect a build system" — which silently disabled the agent's build-verify step for
    /// the most common repo layout there is.
    /// </summary>
    public ProjectCommand? DetectProjectCommand(bool test)
    {
        if (!HasWorkingDirectory) return null;
        string root = Root;

        string? atRoot = DetectCommandIn(root, test);
        if (atRoot != null) return new ProjectCommand(atRoot, "the project root");

        string verb = test ? "test" : "build";
        string Rel(string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

        // .NET: a solution anywhere below wins (it knows how its projects fit together).
        var slns = FindBelow(root, "*.sln", 3);
        if (slns.Count > 0)
        {
            string sln = slns[0];
            return new ProjectCommand($"dotnet {verb} \"{Rel(sln)}\"", Rel(sln));
        }

        var projects = FindBelow(root, "*.csproj", 3)
            .Concat(FindBelow(root, "*.fsproj", 3))
            .Concat(FindBelow(root, "*.vbproj", 3))
            .ToList();
        if (projects.Count > 0)
        {
            var chosen = test ? projects.Where(IsDotnetTestProject).ToList() : projects;
            if (chosen.Count > 0)
            {
                var take = chosen.Take(8).ToList();
                string cmd = string.Join(" && ", take.Select(p => $"dotnet {verb} \"{Rel(p)}\""));
                string where = take.Count == 1 ? Rel(take[0]) : $"{take.Count} projects" + (chosen.Count > 8 ? $" (of {chosen.Count})" : "");
                return new ProjectCommand(cmd, where);
            }
        }

        // Other ecosystems: a single project folder below the root.
        foreach (var marker in new[] { "Cargo.toml", "go.mod", "package.json", "pom.xml", "build.gradle", "build.gradle.kts", "pyproject.toml" })
        {
            var hits = FindBelow(root, marker, 2);
            if (hits.Count != 1) continue;
            string dir = Path.GetDirectoryName(hits[0])!;
            string? inner = DetectCommandIn(dir, test);
            if (inner == null) continue;
            return new ProjectCommand($"cd \"{Rel(dir)}\" && {inner}", Rel(dir));
        }
        return null;
    }

    /// <summary>The marker-file rules, applied to one directory.</summary>
    private static string? DetectCommandIn(string dir, bool test)
    {
        bool HasGlob(string pattern)
        {
            try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).Any(); }
            catch { return false; }
        }
        bool HasFile(string name) => File.Exists(Path.Combine(dir, name));

        if (test)
        {
            if (HasGlob("*.sln") || HasGlob("*.csproj") || HasGlob("*.fsproj")) return "dotnet test";
            if (HasFile("Cargo.toml")) return "cargo test";
            if (HasFile("go.mod")) return "go test ./...";
            if (HasFile("pyproject.toml") || HasFile("pytest.ini") || HasFile("tox.ini") || HasFile("setup.py")) return "pytest -q";
            if (HasFile("package.json")) return "npm test";
            if (HasFile("pom.xml")) return "mvn -q test";
            if (HasFile("build.gradle") || HasFile("build.gradle.kts")) return "gradle test";
            return null;
        }

        if (HasGlob("*.sln")) return "dotnet build";
        if (HasGlob("*.csproj") || HasGlob("*.fsproj")) return "dotnet build";
        if (HasFile("Cargo.toml")) return "cargo build";
        if (HasFile("go.mod")) return "go build ./...";
        if (HasFile("tsconfig.json")) return "npx tsc --noEmit";
        if (HasFile("package.json")) return "npm run build";
        if (HasFile("pom.xml")) return "mvn -q -DskipTests compile";
        if (HasFile("build.gradle") || HasFile("build.gradle.kts")) return "gradle build -x test";
        if (HasFile("Makefile") || HasFile("makefile")) return "make";
        if (HasFile("CMakeLists.txt")) return "cmake --build build";
        return null;
    }

    /// <summary>Files matching <paramref name="pattern"/> up to <paramref name="maxDepth"/> folders below
    /// <paramref name="root"/> (not in it), shallowest first, skipping build/vendor folders.</summary>
    private static List<string> FindBelow(string root, string pattern, int maxDepth)
    {
        var found = new List<(string path, int depth)>();
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            if (depth > 0)
            {
                try { foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)) found.Add((f, depth)); }
                catch { }
            }
            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var s in subs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (!WorkspaceFiles.IsPrunedDirName(Path.GetFileName(s))) queue.Enqueue((s, depth + 1));
        }
        return found.OrderBy(f => f.depth).ThenBy(f => f.path, StringComparer.OrdinalIgnoreCase).Select(f => f.path).ToList();
    }

    private static bool IsDotnetTestProject(string projectPath)
    {
        if (Path.GetFileNameWithoutExtension(projectPath).Contains("test", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            string xml = File.ReadAllText(projectPath);
            return xml.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                || xml.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ─── Write File ───
    /// <summary>
    /// Write a whole file. An existing file keeps its BOM (or lack of one) and its dominant newline style —
    /// a model writing \n into a CRLF file otherwise turns a two-line change into a whole-file diff. A new
    /// file is written as plain UTF-8 with no BOM.
    /// </summary>
    public void WriteFile(string relativePath, string content)
    {
        string fullPath = ResolveSafePath(relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        bool bom = false;
        if (File.Exists(fullPath))
        {
            var existing = CluadeX.Helpers.TextFileCodec.Read(fullPath);
            bom = existing.HasBom;
            if (existing.IsValidUtf8 && UsesCrLf(existing.Text))
                content = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        CluadeX.Helpers.TextFileCodec.Write(fullPath, content, bom);
    }

    // ─── Edit File (Find & Replace) ───
    // Line-ending tolerant + whitespace-flexible fallback. This is the most-used code-editing
    // primitive, so it must "just work" even when the model's find-block has \n while a Windows repo
    // file has \r\n, or the indentation is slightly off.
    public (bool found, int replacements) EditFile(string relativePath, string find, string replace, bool replaceAll = false)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");
        if (string.IsNullOrEmpty(find))
            throw new ArgumentException("The 'find' text must not be empty.");

        var tf = CluadeX.Helpers.TextFileCodec.Read(fullPath);
        if (!tf.IsValidUtf8) throw new InvalidOperationException(CluadeX.Helpers.TextFileCodec.NotUtf8Message(relativePath));
        string raw = tf.Text;

        // Remember the file's DOMINANT newline so we write it back unchanged — injecting \n into a \r\n
        // file (or vice-versa) made edits "succeed" then show phantom whole-file diffs in git.
        bool fileUsesCrLf = UsesCrLf(raw);

        // Match on \n-normalized text. The model emits \n; the file is often \r\n; an Ordinal compare
        // then never matches — the #1 reason edit_file "can't find" text that's visibly right there.
        var outcome = ApplyOneEdit(raw.Replace("\r\n", "\n"), find, replace, replaceAll);
        if (outcome.Ambiguous > 1)
            throw new InvalidOperationException(
                $"The text to replace appears {outcome.Ambiguous} times in {relativePath}. Add more surrounding " +
                "context so the match is unique, or pass replace_all=true to change every occurrence.");
        if (outcome.Content == null) return (false, 0);

        string outContent = fileUsesCrLf ? outcome.Content.Replace("\n", "\r\n") : outcome.Content;
        CluadeX.Helpers.TextFileCodec.Write(fullPath, outContent, tf.HasBom);
        return (true, outcome.Replacements);
    }

    /// <summary>
    /// Compute what an EditFile WOULD produce, without writing anything — used to preview a diff before
    /// the user approves. Read-only and best-effort: returns null on missing file, oversized file, empty
    /// find, no match, or ambiguous match (the real edit, run after approval, surfaces the actionable
    /// error). before/after are newline-normalized so the previewed diff is clean.
    /// </summary>
    public (bool found, string before, string after)? PreviewEdit(string relativePath, string find, string replace, bool replaceAll = false)
    {
        try
        {
            string fullPath = ResolveSafePath(relativePath);
            if (!File.Exists(fullPath) || string.IsNullOrEmpty(find)) return null;
            if (new FileInfo(fullPath).Length > 1_048_576) return null;

            var tf = CluadeX.Helpers.TextFileCodec.Read(fullPath);
            if (!tf.IsValidUtf8) return null;
            string content = tf.Text.Replace("\r\n", "\n");

            // Same matcher as the real edit, so the approval dialog shows exactly what will happen.
            var outcome = ApplyOneEdit(content, find, replace, replaceAll);
            if (outcome.Content == null) return null;   // not found / ambiguous — the real edit explains why
            return (true, content, outcome.Content);
        }
        catch { return null; }
    }

    /// <summary>
    /// When an edit's find-block doesn't match, locate the single file line most similar to the find-block's
    /// first non-blank line and return a "path:line → text" pointer, so a weak model's retry is GUIDED rather
    /// than blind (the spot small models loop on). Returns null when nothing is close enough or unreadable.
    /// </summary>
    public string? FindNearestAnchor(string relativePath, string find)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(find)) return null;
            string? raw = TryReadRaw(relativePath);
            if (raw == null) return null;

            string anchor = find.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            if (anchor.Length < 3) return null;

            var lines = raw.Replace("\r\n", "\n").Split('\n');
            int bestLine = -1, bestDist = int.MaxValue; string bestText = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0) continue;
                if (Math.Abs(t.Length - anchor.Length) > anchor.Length) continue; // cheap length prefilter
                int d = BoundedLevenshtein(anchor, t);
                if (d < bestDist) { bestDist = d; bestLine = i + 1; bestText = lines[i].TrimEnd(); }
            }
            // Only suggest when genuinely close: within half the anchor length (min 4 edits).
            if (bestLine < 0 || bestDist > Math.Max(4, anchor.Length / 2)) return null;
            string shown = bestText.Length > 160 ? bestText[..160] + "…" : bestText;
            return $"Closest existing line is {relativePath}:{bestLine} → {shown.Trim()}";
        }
        catch { return null; }
    }

    /// <summary>Levenshtein over the first 200 chars of each side (lines longer than that are clamped — the
    /// anchor heuristic only needs a rough closeness, not exact distance on pathological lines).</summary>
    private static int BoundedLevenshtein(string a, string b)
    {
        if (a.Length > 200) a = a[..200];
        if (b.Length > 200) b = b[..200];
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Last-resort match that tolerates per-line leading/trailing whitespace differences (the model
    /// dropped or shifted indentation). Returns edited content only when EXACTLY one location matches —
    /// ambiguity returns null so the caller asks the model for more context instead of guessing and
    /// corrupting the wrong block.
    /// </summary>
    private static string? TryFlexibleReplace(string content, string find, string replace)
    {
        var findLines = find.Split('\n');
        var pattern = new StringBuilder();
        for (int i = 0; i < findLines.Length; i++)
        {
            if (i > 0) pattern.Append('\n');
            pattern.Append("[ \\t]*")
                   .Append(Regex.Escape(findLines[i].Trim()))
                   .Append("[ \\t]*");
        }
        try
        {
            var rx = new Regex(pattern.ToString(), RegexOptions.None, TimeSpan.FromSeconds(2));
            var matches = rx.Matches(content);
            if (matches.Count != 1) return null;
            var m = matches[0];
            // The model's indentation was wrong (that is why only the tolerant pass matched), so its
            // replacement carries the same wrong indentation — map it onto the file's.
            string adapted = CluadeX.Helpers.IndentAdapter.Adapt(findLines, m.Value.Split('\n'), replace);
            return content[..m.Index] + adapted + content[(m.Index + m.Length)..];
        }
        catch { return null; } // RegexParseException / RegexMatchTimeoutException — fall back to "not found"
    }

    // ─── List Directory ───
    public List<FileEntry> ListDirectory(string relativePath = ".", int maxDepth = 1)
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {relativePath}");

        var entries = new List<FileEntry>();
        ListDirectoryRecursive(fullPath, fullPath, entries, 0, maxDepth);
        return entries;
    }

    private void ListDirectoryRecursive(string rootPath, string currentPath, List<FileEntry> entries, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                string name = Path.GetFileName(dir);
                // Skip hidden and common ignore dirs
                if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj" or ".git" or "__pycache__" or ".vs")
                    continue;

                string rel = Path.GetRelativePath(rootPath, dir).Replace('\\', '/');
                entries.Add(new FileEntry { Name = name, RelativePath = rel, IsDirectory = true });
                ListDirectoryRecursive(rootPath, dir, entries, depth + 1, maxDepth);
            }

            foreach (var file in Directory.GetFiles(currentPath))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith('.')) continue;

                string rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                var fi = new FileInfo(file);
                entries.Add(new FileEntry { Name = name, RelativePath = rel, IsDirectory = false, SizeBytes = fi.Length });
            }
        }
        catch (UnauthorizedAccessException) { }
    }

    // ─── Search Files by Pattern ───
    public List<string> SearchFiles(string pattern, string relativePath = ".")
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!Directory.Exists(fullPath))
            return new();

        var results = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, pattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
                // Skip common ignore dirs
                if (rel.Contains("node_modules/") || rel.Contains("/bin/") || rel.Contains("/obj/") ||
                    rel.Contains("/.git/") || rel.Contains("/__pycache__/") || rel.Contains("/.vs/"))
                    continue;
                results.Add(rel);
                if (results.Count >= 100) break; // Limit results
            }
        }
        catch (UnauthorizedAccessException) { }

        return results;
    }

    // ─── Search Content (grep-like) ───
    public List<SearchContentResult> SearchContent(string query, string relativePath = ".", string filePattern = "*.*")
    {
        string fullPath = ResolveSafePath(relativePath);
        if (!Directory.Exists(fullPath))
            return new();

        var results = new List<SearchContentResult>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(fullPath, filePattern, SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
                if (rel.Contains("node_modules/") || rel.Contains("/bin/") || rel.Contains("/obj/") ||
                    rel.Contains("/.git/") || rel.Contains("/__pycache__/") || rel.Contains("/.vs/"))
                    continue;

                // Only search text files (skip binary)
                var fi = new FileInfo(file);
                if (fi.Length > 1024 * 1024) continue; // Skip files > 1MB

                try
                {
                    string content = File.ReadAllText(file, Encoding.UTF8);
                    var lines = content.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new SearchContentResult
                            {
                                FilePath = rel,
                                LineNumber = i + 1,
                                LineContent = lines[i].TrimEnd('\r').Trim(),
                            });
                            if (results.Count >= 50) return results;
                        }
                    }
                }
                catch { /* skip unreadable files */ }
            }
        }
        catch (UnauthorizedAccessException) { }

        return results;
    }

    // ─── Create Directory ───
    public void CreateDirectory(string relativePath)
    {
        string fullPath = ResolveSafePath(relativePath);
        Directory.CreateDirectory(fullPath);
    }

    // ─── File/Dir Exists ───
    public bool FileExists(string relativePath)
    {
        try { return File.Exists(ResolveSafePath(relativePath)); }
        catch { return false; }
    }

    public bool DirectoryExists(string relativePath)
    {
        try { return Directory.Exists(ResolveSafePath(relativePath)); }
        catch { return false; }
    }

    // ─── Get Project Tree (compact) ───
    public string GetProjectTree(int maxDepth = 3)
    {
        if (!HasWorkingDirectory) return "(no working directory set)";

        var sb = new StringBuilder();
        sb.AppendLine(Path.GetFileName(Root) + "/");
        BuildTree(sb, Root, "", 0, maxDepth);
        return sb.ToString().TrimEnd();
    }

    private void BuildTree(StringBuilder sb, string dir, string indent, int depth, int maxDepth)
    {
        if (depth >= maxDepth) return;

        var dirs = GetFilteredDirs(dir);
        var files = GetFilteredFiles(dir);

        foreach (var d in dirs)
        {
            sb.AppendLine($"{indent}  {Path.GetFileName(d)}/");
            BuildTree(sb, d, indent + "  ", depth + 1, maxDepth);
        }
        foreach (var f in files)
        {
            sb.AppendLine($"{indent}  {Path.GetFileName(f)}");
        }
    }

    private static string[] GetFilteredDirs(string path)
    {
        try
        {
            return Directory.GetDirectories(path)
                .Where(d =>
                {
                    string n = Path.GetFileName(d);
                    return !n.StartsWith('.') && n is not ("node_modules" or "bin" or "obj" or "__pycache__" or ".vs");
                })
                .OrderBy(d => Path.GetFileName(d))
                .ToArray();
        }
        catch { return []; }
    }

    private static string[] GetFilteredFiles(string path)
    {
        try
        {
            return Directory.GetFiles(path)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => Path.GetFileName(f))
                .ToArray();
        }
        catch { return []; }
    }

    // ─── Safety: Resolve and validate path ───
    public string ResolveSafePath(string relativePath)
    {
        if (!HasWorkingDirectory)
            throw new InvalidOperationException("No working directory set. Open a project folder first.");

        // Normalize path
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);

        // Absolute path INSIDE the project → auto-convert to relative instead of failing. The system
        // prompt tells the model the working directory as an absolute path, so (especially weak local)
        // models constantly echo absolute paths back; rejecting those burned 4-5 loop steps per task on
        // "Absolute paths are not allowed" retries. Anything OUTSIDE the project still falls through to
        // the containment check below and is denied as before.
        if (Path.IsPathRooted(normalized))
        {
            string rootedFull;
            try { rootedFull = Path.GetFullPath(normalized); }
            catch { throw new UnauthorizedAccessException("Absolute paths are not allowed. Use relative paths from the project root."); }
            string wdFull = Path.GetFullPath(Root);
            string wdSep = wdFull.EndsWith(Path.DirectorySeparatorChar) ? wdFull : wdFull + Path.DirectorySeparatorChar;
            if (rootedFull.Equals(wdFull, StringComparison.OrdinalIgnoreCase))
                normalized = ".";
            else if (rootedFull.StartsWith(wdSep, StringComparison.OrdinalIgnoreCase))
                normalized = rootedFull[wdSep.Length..];
            // A related folder this run also opened (open_project add=true): reached by absolute path.
            else if (AgentRunScope.Current is { } scope
                     && AgentRunScope.ContainingFolder(rootedFull, scope.ExtraFolders) != null)
                return rootedFull;
            else
                throw new UnauthorizedAccessException(
                    $"Access denied: '{relativePath}' is outside the project root. Use relative paths from the project root.");
        }

        string fullPath = Path.GetFullPath(Path.Combine(Root, normalized));

        // Security: ensure path stays within working directory (handles .. traversal).
        // Compare with a trailing separator so "C:\App" doesn't accept sibling "C:\App-evil\secret".
        string workDirFull = Path.GetFullPath(Root);
        string workDirSep = workDirFull.EndsWith(Path.DirectorySeparatorChar) ? workDirFull : workDirFull + Path.DirectorySeparatorChar;
        string compare = fullPath.EndsWith(Path.DirectorySeparatorChar) ? fullPath : fullPath + Path.DirectorySeparatorChar;
        if (!compare.StartsWith(workDirSep, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access denied. Path must be within the working directory.");

        return fullPath;
    }
}

// ─── Helper Models ───
public class FileEntry
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
}

public class SearchContentResult
{
    public string FilePath { get; set; } = "";
    public int LineNumber { get; set; }
    public string LineContent { get; set; } = "";
}
