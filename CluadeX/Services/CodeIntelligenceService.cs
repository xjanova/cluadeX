using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services;

/// <summary>
/// The code-navigation engine behind the workbench's Search panel and the editor's
/// go-to-definition / find-references / rename commands.
///
/// Deliberately has NO hard dependency on a language server: on a clean machine there is no
/// OmniSharp / pylsp / tsserver installed, so an LSP-only implementation would be a button that
/// does nothing. Everything here works with zero installs (bounded workspace scan + the existing
/// <see cref="RepoMapService"/> declaration heuristics), and *upgrades* to real semantic results
/// when <see cref="LspClientService"/> happens to be connected.
///
/// Every scan is bounded (file count, file size, matches per file, total matches) and every cap
/// that actually bit is reported back through <c>Truncated</c> — a silent cap reads as "I searched
/// everything" when it didn't.
/// </summary>
public sealed class CodeIntelligenceService
{
    private readonly FileSystemService _fs;
    private readonly RepoMapService _repoMap;
    private readonly LspClientService? _lsp;

    public CodeIntelligenceService(FileSystemService fs, RepoMapService repoMap, LspClientService? lsp = null)
    {
        _fs = fs;
        _repoMap = repoMap;
        _lsp = lsp;
    }

    // ── Bounds. Tuned so a full scan of a large repo stays well under a second on a warm cache. ──
    private const int MaxFilesScanned = 20_000;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxMatchesPerFile = 200;
    private const int MaxTotalMatches = 5_000;
    private const int MaxLineLength = 1_000;
    private const int PreviewMaxChars = 240;

    /// <summary>Regex match timeout — a user-supplied pattern must never be able to hang the app
    /// (catastrophic backtracking is trivially reachable from the search box: <c>(a+)+$</c>).</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly string[] IgnoreDirs =
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "out", "target",
        ".vs", ".idea", "__pycache__", ".pytest_cache", "packages", "vendor",
        ".next", ".nuxt", ".gradle", ".venv", "venv", ".cache",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".so", ".dylib", ".bin", ".obj", ".lib", ".a",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svgz", ".tiff",
        ".zip", ".gz", ".7z", ".rar", ".tar", ".xz", ".jar", ".nupkg",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".webm", ".flac",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".gguf", ".safetensors", ".onnx", ".pt", ".pth", ".db", ".sqlite",
    };

    public bool HasWorkspace => _fs.HasWorkingDirectory;
    public string WorkingDirectory => _fs.WorkingDirectory;

    /// <summary>True when a language server is live, so results are semantic rather than textual.
    /// The UI shows this so the user always knows which engine answered.</summary>
    public bool IsLspConnected => _lsp?.IsConnected == true;

    // ═══════════════════════════ Search across files ═══════════════════════════

    /// <summary>
    /// Search every text file in the workspace. Runs entirely on a background thread; honours
    /// cancellation between files AND between lines so retyping in the search box aborts the
    /// in-flight scan immediately instead of queueing another full pass behind it.
    /// </summary>
    public Task<SearchOutcome> SearchAsync(SearchQuery query, CancellationToken ct = default)
        => Task.Run(() => Search(query, ct), ct);

    private SearchOutcome Search(SearchQuery query, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var outcome = new SearchOutcome();

        if (!_fs.HasWorkingDirectory)
        {
            outcome.Error = "No project open.";
            return outcome;
        }
        if (string.IsNullOrEmpty(query.Text))
            return outcome;

        Regex rx;
        try
        {
            rx = BuildMatcher(query);
        }
        catch (ArgumentException ex)
        {
            // Invalid user regex — a normal thing to type mid-pattern, not a crash.
            outcome.Error = $"Invalid regular expression: {ex.Message.Split('\n')[0]}";
            return outcome;
        }

        var include = BuildGlobFilter(query.Include);
        var exclude = BuildGlobFilter(query.Exclude);
        string root = _fs.WorkingDirectory;

        foreach (var file in EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();

            if (outcome.FilesScanned >= MaxFilesScanned) { outcome.Truncated = true; break; }
            if (outcome.TotalMatches >= MaxTotalMatches) { outcome.Truncated = true; break; }

            string rel;
            try { rel = Path.GetRelativePath(root, file).Replace('\\', '/'); }
            catch { continue; }

            if (include != null && !include.Any(g => g.IsMatch(rel))) continue;
            if (exclude != null && exclude.Any(g => g.IsMatch(rel))) continue;
            if (!LooksTextual(file)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            outcome.FilesScanned++;

            List<SearchMatch>? matches = null;
            for (int i = 0; i < lines.Length; i++)
            {
                if ((i & 0x3F) == 0) ct.ThrowIfCancellationRequested();

                string line = lines[i];
                if (line.Length == 0 || line.Length > MaxLineLength) continue;

                MatchCollection found;
                try { found = rx.Matches(line); }
                catch (RegexMatchTimeoutException) { outcome.Truncated = true; continue; }
                if (found.Count == 0) continue;

                foreach (Match m in found)
                {
                    if (m.Length == 0) continue;   // zero-width pattern would match every position
                    matches ??= new List<SearchMatch>();
                    matches.Add(new SearchMatch
                    {
                        RelPath = rel,
                        FullPath = file,
                        Line = i + 1,
                        Column = m.Index,
                        Length = m.Length,
                        LineText = Preview(line, m.Index, out int shift),
                        PreviewColumn = m.Index - shift,
                    });
                    if (matches.Count >= MaxMatchesPerFile) break;
                }

                if (matches != null && matches.Count >= MaxMatchesPerFile) { outcome.Truncated = true; break; }
            }

            if (matches == null) continue;
            outcome.TotalMatches += matches.Count;
            outcome.Files.Add(new SearchFileGroup
            {
                RelPath = rel,
                FullPath = file,
                Matches = matches,
            });
        }

        outcome.Elapsed = sw.Elapsed;
        return outcome;
    }

    /// <summary>Compose the literal/regex + case + whole-word options into one matcher.</summary>
    private static Regex BuildMatcher(SearchQuery q)
    {
        string pattern = q.IsRegex ? q.Text : Regex.Escape(q.Text);
        if (q.WholeWord)
        {
            // \b is meaningless next to a non-word char (searching "foo(" whole-word would never
            // match), so only anchor the ends that actually border a word character.
            string bare = q.IsRegex ? q.Text : q.Text;
            if (bare.Length > 0 && (char.IsLetterOrDigit(bare[0]) || bare[0] == '_')) pattern = @"\b" + pattern;
            if (bare.Length > 0 && (char.IsLetterOrDigit(bare[^1]) || bare[^1] == '_')) pattern += @"\b";
        }

        var opts = RegexOptions.CultureInvariant;
        if (!q.MatchCase) opts |= RegexOptions.IgnoreCase;
        return new Regex(pattern, opts, RegexTimeout);
    }

    /// <summary>
    /// Window a long line around the match so the results list shows the hit rather than 900
    /// characters of minified prefix. Reports how far the window shifted so the highlight stays aligned.
    /// </summary>
    private static string Preview(string line, int matchIndex, out int shift)
    {
        shift = 0;
        string trimmed = line;

        // Drop leading indentation (and keep the highlight aligned with it).
        int lead = 0;
        while (lead < trimmed.Length && (trimmed[lead] == ' ' || trimmed[lead] == '\t')) lead++;
        if (lead > 0 && lead <= matchIndex) { trimmed = trimmed[lead..]; shift = lead; }

        if (trimmed.Length <= PreviewMaxChars) return trimmed;

        int localIdx = matchIndex - shift;
        if (localIdx < PreviewMaxChars - 40) return trimmed[..PreviewMaxChars] + "…";

        int start = Math.Max(0, localIdx - 40);
        shift += start;
        string window = trimmed[start..Math.Min(trimmed.Length, start + PreviewMaxChars)];
        return "…" + window + (start + PreviewMaxChars < trimmed.Length ? "…" : "");
    }

    // ═══════════════════════════ Go to definition ═══════════════════════════

    /// <summary>
    /// Resolve where a symbol is declared. Prefers a real language server when one is connected
    /// (exact, semantic); otherwise falls back to the repo-map declaration heuristics, which need
    /// no install. <paramref name="fromFile"/>/<paramref name="line"/>/<paramref name="character"/>
    /// are only used by the LSP path.
    /// </summary>
    public async Task<NavigationResult> FindDefinitionsAsync(
        string symbol, string? fromFile, int line, int character, CancellationToken ct = default)
    {
        var result = new NavigationResult { Symbol = symbol };
        if (string.IsNullOrWhiteSpace(symbol)) return result;
        if (!_fs.HasWorkingDirectory) { result.Error = "No project open."; return result; }

        // 1) Language server, when present.
        if (_lsp?.IsConnected == true && !string.IsNullOrEmpty(fromFile))
        {
            try
            {
                string loc = await _lsp.GetDefinitionAsync(fromFile, line, character, ct);
                if (!string.IsNullOrEmpty(loc) && TryParseLocation(loc, out var parsed))
                {
                    result.Engine = NavigationEngine.LanguageServer;
                    result.Locations.Add(parsed);
                    return result;
                }
            }
            catch { /* fall through to the local index */ }
        }

        // 2) Zero-install fallback — declaration heuristics over the workspace.
        result.Engine = NavigationEngine.WorkspaceIndex;
        var hits = await Task.Run(() => _repoMap.FindSymbolDefinitions(symbol), ct);
        string root = _fs.WorkingDirectory;
        foreach (var h in hits)
        {
            result.Locations.Add(new CodeLocation
            {
                RelPath = h.File,
                FullPath = Path.GetFullPath(Path.Combine(root, h.File)),
                Line = h.Line + 1,          // CodeHit.Line is 0-based
                Snippet = h.Snippet,
                Score = h.Score,
            });
        }
        return result;
    }

    private bool TryParseLocation(string encoded, out CodeLocation location)
    {
        // LspClientService returns "C:\path\file.cs:12:5"
        location = new CodeLocation();
        int lastColon = encoded.LastIndexOf(':');
        if (lastColon <= 0) return false;
        int prevColon = encoded.LastIndexOf(':', lastColon - 1);
        if (prevColon <= 0) return false;

        if (!int.TryParse(encoded[(prevColon + 1)..lastColon], out int line)) return false;
        string path = encoded[..prevColon];
        if (string.IsNullOrEmpty(path)) return false;

        location.FullPath = path;
        location.Line = Math.Max(1, line);
        location.Score = 100;
        try
        {
            location.RelPath = _fs.HasWorkingDirectory
                ? Path.GetRelativePath(_fs.WorkingDirectory, path).Replace('\\', '/')
                : path;
        }
        catch { location.RelPath = path; }
        try
        {
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                if (location.Line - 1 < lines.Length) location.Snippet = lines[location.Line - 1].Trim();
            }
        }
        catch { /* snippet is cosmetic */ }
        return true;
    }

    // ═══════════════════════════ Find references ═══════════════════════════

    /// <summary>
    /// Every whole-word occurrence of a symbol across the workspace, with declaration sites flagged
    /// so the results list can separate "defined here" from "used here". Textual by nature — it will
    /// include same-named members of unrelated types, which the UI states plainly rather than
    /// pretending to be a semantic index.
    /// </summary>
    public Task<SearchOutcome> FindReferencesAsync(string symbol, CancellationToken ct = default)
        => SearchAsync(new SearchQuery
        {
            Text = symbol,
            IsRegex = false,
            MatchCase = true,
            WholeWord = true,
        }, ct);

    // ═══════════════════════════ Rename symbol ═══════════════════════════

    /// <summary>
    /// Build (but do not apply) a rename plan: every whole-word, case-sensitive occurrence that
    /// would change. Always previewed before applying — a repo-wide text rewrite is exactly the kind
    /// of destructive action that must never happen on one keystroke.
    /// </summary>
    public async Task<RenamePlan> PrepareRenameAsync(string oldName, string newName, CancellationToken ct = default)
    {
        var plan = new RenamePlan { OldName = oldName?.Trim() ?? "", NewName = newName?.Trim() ?? "" };

        if (string.IsNullOrEmpty(plan.OldName)) { plan.Error = "Nothing to rename — put the caret on a symbol first."; return plan; }
        if (string.IsNullOrEmpty(plan.NewName)) { plan.Error = "New name is empty."; return plan; }
        if (plan.OldName == plan.NewName) { plan.Error = "The new name is identical to the old one."; return plan; }
        if (!IsIdentifier(plan.NewName)) { plan.Error = $"'{plan.NewName}' is not a valid identifier."; return plan; }
        if (!_fs.HasWorkingDirectory) { plan.Error = "No project open."; return plan; }

        var found = await FindReferencesAsync(plan.OldName, ct);
        plan.Error = found.Error;
        plan.Files = found.Files;
        plan.TotalEdits = found.TotalMatches;
        plan.Truncated = found.Truncated;
        return plan;
    }

    /// <summary>
    /// Apply a previously previewed rename. Re-reads and re-matches every file at write time rather
    /// than trusting the offsets captured during preview — the file may have changed since (the agent
    /// edits in the background), and writing stale offsets would corrupt it. Returns per-file results.
    /// </summary>
    public Task<RenameResult> ApplyRenameAsync(RenamePlan plan, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var result = new RenameResult();
            if (plan.Files.Count == 0) return result;

            Regex rx;
            try { rx = BuildMatcher(new SearchQuery { Text = plan.OldName, MatchCase = true, WholeWord = true }); }
            catch (ArgumentException ex) { result.Error = ex.Message; return result; }

            foreach (var group in plan.Files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!File.Exists(group.FullPath)) { result.Skipped.Add($"{group.RelPath} (gone)"); continue; }

                    string content = File.ReadAllText(group.FullPath);
                    string updated = rx.Replace(content, plan.NewName);
                    if (string.Equals(content, updated, StringComparison.Ordinal))
                    {
                        result.Skipped.Add($"{group.RelPath} (no longer matches)");
                        continue;
                    }

                    File.WriteAllText(group.FullPath, updated);
                    result.FilesChanged++;
                    result.EditsApplied += group.Matches.Count;
                    result.ChangedPaths.Add(group.FullPath);
                }
                catch (Exception ex)
                {
                    result.Skipped.Add($"{group.RelPath} ({ex.GetType().Name})");
                }
            }
            return result;
        }, ct);

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!char.IsLetter(s[0]) && s[0] != '_') return false;
        foreach (char c in s)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    // ═══════════════════════════ Autocomplete ═══════════════════════════

    private List<CompletionItem>? _symbolIndex;
    private string? _symbolIndexDir;
    private readonly object _indexLock = new();

    /// <summary>Drop the cached workspace symbol index (after a rename, or a project switch).</summary>
    public void InvalidateSymbolIndex()
    {
        lock (_indexLock) { _symbolIndex = null; _symbolIndexDir = null; }
    }

    /// <summary>
    /// Completion candidates for the caret position, ranked: identifiers already in this buffer first
    /// (what you're most likely to repeat), then declarations from anywhere in the workspace, then
    /// language keywords. A connected language server is merged in ahead of all of them.
    ///
    /// Matching is prefix OR camel-hump ("OS" → OrderService), the way editors have worked for years.
    /// </summary>
    public async Task<List<CompletionItem>> GetCompletionsAsync(
        string? filePath, string documentText, int caretOffset, CancellationToken ct = default)
    {
        string prefix = PrefixAt(documentText, caretOffset);
        var results = new List<CompletionItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 1) Language server — real, scope-aware completions when one is running.
        if (_lsp?.IsConnected == true && !string.IsNullOrEmpty(filePath))
        {
            try
            {
                var (line, character) = OffsetToLineChar(documentText, caretOffset);
                foreach (var label in await _lsp.GetCompletionsAsync(filePath!, line, character, ct))
                {
                    if (string.IsNullOrEmpty(label) || !seen.Add(label)) continue;
                    results.Add(new CompletionItem { Label = label, Kind = "lsp", Detail = "language server", Score = 1000 });
                }
            }
            catch { /* fall through to the local sources */ }
        }

        var keywords = KeywordsFor(filePath);
        var keywordSet = new HashSet<string>(keywords, StringComparer.Ordinal);

        // 2) Identifiers in the buffer being edited. A language keyword is labelled as one even
        //    though it was found in the buffer — "public" is a keyword, not a word you happened to
        //    type, and mislabelling it makes the popup's explanation worthless.
        foreach (var (word, count) in BufferIdentifiers(documentText, prefix, caretOffset))
        {
            if (!seen.Add(word)) continue;
            bool isKeyword = keywordSet.Contains(word);
            results.Add(new CompletionItem
            {
                Label = word,
                Kind = isKeyword ? "keyword" : "word",
                Detail = isKeyword ? "keyword" : "in this file",
                Score = 500 + Math.Min(count, 20),
            });
        }

        // 3) Declarations from anywhere in the workspace.
        foreach (var sym in await GetSymbolIndexAsync(ct))
        {
            if (!seen.Add(sym.Label)) continue;
            results.Add(sym);
        }

        // 4) Keywords for this language that weren't already in the buffer.
        foreach (var kw in keywords)
        {
            if (!seen.Add(kw)) continue;
            results.Add(new CompletionItem { Label = kw, Kind = "keyword", Detail = "keyword", Score = 100 });
        }

        // Filter + rank against what has actually been typed.
        var ranked = new List<CompletionItem>();
        foreach (var item in results)
        {
            int bonus = MatchScore(item.Label, prefix);
            if (bonus < 0) continue;
            item.Score += bonus;
            ranked.Add(item);
        }

        ranked.Sort((a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
        if (ranked.Count > 60) ranked.RemoveRange(60, ranked.Count - 60);
        return ranked;
    }

    /// <summary>The partial identifier immediately before the caret — what the user has typed so far.</summary>
    public static string PrefixAt(string text, int caretOffset)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int end = Math.Clamp(caretOffset, 0, text.Length);
        int start = end;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        return text[start..end];
    }

    /// <summary>
    /// -1 = no match. Otherwise a bonus: an exact-case prefix beats a case-insensitive prefix, which
    /// beats a camel-hump match. An empty prefix (Ctrl+Space on whitespace) matches everything.
    /// </summary>
    private static int MatchScore(string candidate, string prefix)
    {
        if (prefix.Length == 0) return 0;
        if (candidate.Length <= prefix.Length
            && string.Equals(candidate, prefix, StringComparison.Ordinal)) return -1;  // already typed in full

        if (candidate.StartsWith(prefix, StringComparison.Ordinal)) return 400;
        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 300;
        return IsCamelHumpMatch(candidate, prefix) ? 150 : -1;
    }

    /// <summary>
    /// "OS" → OrderService, "ord" → _orders. Every prefix character must either continue the previous
    /// match (contiguous run) or land on a hump / word boundary.
    ///
    /// The contiguity rule is what keeps this useful: a plain subsequence match would let "Ord" hit
    /// <c>override</c> (o…r…d with unrelated letters between), and once a three-letter prefix matches
    /// half the keyword list the popup is worse than no popup.
    /// </summary>
    private static bool IsCamelHumpMatch(string candidate, string prefix)
    {
        int ci = 0;
        int previousMatch = -2;   // -2 = nothing matched yet, so nothing is "contiguous"

        for (int pi = 0; pi < prefix.Length; pi++)
        {
            char want = char.ToLowerInvariant(prefix[pi]);
            bool found = false;

            while (ci < candidate.Length)
            {
                int at = ci++;
                char cur = candidate[at];
                if (char.ToLowerInvariant(cur) != want) continue;

                bool isHump = at == 0 || char.IsUpper(cur) || candidate[at - 1] == '_';
                bool isContiguous = at == previousMatch + 1;
                if (!isHump && !isContiguous) continue;

                previousMatch = at;
                found = true;
                break;
            }
            if (!found) return false;
        }
        return true;
    }

    private static readonly Regex IdentifierRx =
        new(@"[A-Za-z_]\w{2,}", RegexOptions.Compiled, RegexTimeout);

    /// <summary>Distinct identifiers in the open document, with occurrence counts, excluding the
    /// half-typed token sitting under the caret (offering the user what they are mid-way through
    /// typing is noise).</summary>
    private static IEnumerable<(string word, int count)> BufferIdentifiers(string text, string prefix, int caretOffset)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2_000_000) yield break;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int tokenStart = Math.Clamp(caretOffset, 0, text.Length) - prefix.Length;

        MatchCollection matches;
        try { matches = IdentifierRx.Matches(text); }
        catch (RegexMatchTimeoutException) { yield break; }

        foreach (Match m in matches)
        {
            if (m.Index == tokenStart && m.Length == prefix.Length) continue;  // the token being typed
            counts[m.Value] = counts.TryGetValue(m.Value, out int n) ? n + 1 : 1;
            if (counts.Count > 5000) break;
        }
        foreach (var kv in counts) yield return (kv.Key, kv.Value);
    }

    private static (int line, int character) OffsetToLineChar(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 0, lastNewline = -1;
        for (int i = 0; i < offset; i++)
            if (text[i] == '\n') { line++; lastNewline = i; }
        return (line, offset - lastNewline - 1);
    }

    /// <summary>Workspace-wide declarations, cached per project. Built off the UI thread.</summary>
    private async Task<List<CompletionItem>> GetSymbolIndexAsync(CancellationToken ct)
    {
        if (!_fs.HasWorkingDirectory) return new();
        string dir = _fs.WorkingDirectory;

        lock (_indexLock)
        {
            if (_symbolIndex != null && string.Equals(_symbolIndexDir, dir, StringComparison.OrdinalIgnoreCase))
                return _symbolIndex;
        }

        var built = await Task.Run(() => BuildSymbolIndex(dir, ct), ct);
        lock (_indexLock) { _symbolIndex = built; _symbolIndexDir = dir; }
        return built;
    }

    private static readonly Regex IndexTypeDecl = new(
        @"\b(class|interface|struct|enum|record|trait|protocol|type)\s+([A-Za-z_]\w*)",
        RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex IndexFuncDecl = new(
        @"\b(?:def|func|function|fn|sub)\s+([A-Za-z_]\w*)|(?:public|private|protected|internal|static|async|override|virtual)\s+[\w<>\[\],\?\.]+\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled, RegexTimeout);

    private static List<CompletionItem> BuildSymbolIndex(string root, CancellationToken ct)
    {
        var byName = new Dictionary<string, CompletionItem>(StringComparer.Ordinal);
        int scanned = 0;

        foreach (var file in EnumerateFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned >= 4000 || byName.Count >= 20_000) break;
            if (!CodeFileExtensions.Contains(Path.GetExtension(file))) continue;

            string[] lines;
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length > 512 * 1024) continue;
                lines = File.ReadAllLines(file);
            }
            catch { continue; }
            scanned++;

            string fileName = Path.GetFileName(file);
            foreach (var raw in lines)
            {
                if (raw.Length == 0 || raw.Length > 400) continue;

                try
                {
                    var t = IndexTypeDecl.Match(raw);
                    if (t.Success)
                    {
                        Add(byName, t.Groups[2].Value, t.Groups[1].Value, fileName, 700);
                        continue;
                    }
                    if (raw.Contains('('))
                    {
                        var f = IndexFuncDecl.Match(raw);
                        if (f.Success)
                        {
                            string name = f.Groups[1].Success ? f.Groups[1].Value : f.Groups[2].Value;
                            Add(byName, name, "method", fileName, 600);
                        }
                    }
                }
                catch (RegexMatchTimeoutException) { /* skip a pathological line */ }
            }
        }

        return byName.Values.ToList();

        static void Add(Dictionary<string, CompletionItem> map, string name, string kind, string file, int score)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 2 || map.ContainsKey(name)) return;
            map[name] = new CompletionItem { Label = name, Kind = kind, Detail = $"{kind} · {file}", Score = score };
        }
    }

    private static readonly HashSet<string> CodeFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".java", ".rs", ".rb", ".php",
        ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".swift", ".kt", ".scala", ".dart", ".xaml",
    };

    private static readonly string[] CSharpKeywords =
    {
        "abstract","as","async","await","base","bool","break","byte","case","catch","char","checked",
        "class","const","continue","decimal","default","delegate","do","double","else","enum","event",
        "explicit","extern","false","finally","fixed","float","for","foreach","get","goto","if",
        "implicit","in","int","interface","internal","is","lock","long","namespace","new","null",
        "object","operator","out","override","params","private","protected","public","readonly","ref",
        "return","sealed","set","short","sizeof","stackalloc","static","string","struct","switch",
        "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using",
        "var","virtual","void","volatile","while","record","nameof","when","yield",
    };

    private static readonly string[] JsKeywords =
    {
        "async","await","break","case","catch","class","const","continue","debugger","default","delete",
        "do","else","export","extends","finally","for","function","if","import","in","instanceof","let",
        "new","null","of","return","static","super","switch","this","throw","true","false","try","typeof",
        "undefined","var","void","while","yield","interface","type","enum","implements","readonly",
    };

    private static readonly string[] PythonKeywords =
    {
        "and","as","assert","async","await","break","class","continue","def","del","elif","else","except",
        "False","finally","for","from","global","if","import","in","is","lambda","None","nonlocal","not",
        "or","pass","raise","return","True","try","while","with","yield","self",
    };

    private static string[] KeywordsFor(string? filePath)
    {
        string ext = string.IsNullOrEmpty(filePath) ? "" : Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cs" => CSharpKeywords,
            ".ts" or ".tsx" or ".js" or ".jsx" => JsKeywords,
            ".py" => PythonKeywords,
            _ => Array.Empty<string>(),
        };
    }

    // ═══════════════════════════ Shared helpers ═══════════════════════════

    /// <summary>
    /// Word under a character offset — what "go to definition" means when the user just puts the
    /// caret in the middle of an identifier and presses F12.
    /// </summary>
    public static string WordAt(string text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (offset > text.Length) offset = text.Length;
        if (offset < 0) offset = 0;

        // Caret sitting just AFTER a word (the common case when you finish typing) still counts.
        if (offset == text.Length || !IsWordChar(text[offset]))
        {
            if (offset > 0 && IsWordChar(text[offset - 1])) offset--;
            else return "";
        }

        int start = offset;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        int end = offset;
        while (end < text.Length && IsWordChar(text[end])) end++;
        return text[start..end];
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Comma/semicolon-separated globs → regexes over the forward-slashed relative path.
    /// Returns null when the filter is empty (meaning "no filter"), never an empty list that would
    /// silently match nothing.</summary>
    private static List<Regex>? BuildGlobFilter(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns)) return null;

        var list = new List<Regex>();
        foreach (var raw in patterns.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string glob = raw.Trim().Replace('\\', '/');
            if (glob.Length == 0) continue;

            // A bare "*.cs" or "node_modules" should match at any depth, like every editor does.
            if (!glob.Contains('/')) glob = "**/" + glob;
            if (glob.EndsWith("/")) glob += "**";

            var sb = new StringBuilder("^");
            for (int i = 0; i < glob.Length; i++)
            {
                char c = glob[i];
                if (c == '*')
                {
                    bool doubleStar = i + 1 < glob.Length && glob[i + 1] == '*';
                    if (doubleStar)
                    {
                        // "**/" may also match zero directories, so "**/x" matches a root-level "x".
                        if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                        else { sb.Append(".*"); i++; }
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');

            try { list.Add(new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)); }
            catch { /* skip an unusable pattern rather than failing the whole search */ }
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>Cheap binary check: known-binary extension, or a NUL byte in the first 8 KB.</summary>
    private static bool LooksTextual(string path)
    {
        try
        {
            if (BinaryExtensions.Contains(Path.GetExtension(path))) return false;

            var fi = new FileInfo(path);
            if (fi.Length == 0 || fi.Length > MaxFileBytes) return false;

            int take = (int)Math.Min(8192, fi.Length);
            byte[] sample = new byte[take];
            using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = fs.Read(sample, 0, take);
                for (int i = 0; i < read; i++)
                    if (sample[i] == 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Pruning walk that never descends into ignore dirs and survives per-directory access errors
    /// (Directory.EnumerateFiles with AllDirectories aborts the whole enumeration on the first
    /// UnauthorizedAccess). Reparse points are skipped so a junction looping back to an ancestor
    /// can't spin the scan forever.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sd in subdirs)
            {
                string name = Path.GetFileName(sd);
                if (name.StartsWith('.') && name is not ".claude" and not ".cluadex" and not ".github") continue;
                if (Array.IndexOf(IgnoreDirs, name) >= 0) continue;
                try { if ((File.GetAttributes(sd) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                stack.Push(sd);
            }
        }
    }
}

// ═══════════════════════════ Models ═══════════════════════════

public sealed class SearchQuery
{
    public string Text { get; set; } = "";
    public bool IsRegex { get; set; }
    public bool MatchCase { get; set; }
    public bool WholeWord { get; set; }
    /// <summary>Comma-separated globs, e.g. "*.cs, Views/**". Empty = every file.</summary>
    public string Include { get; set; } = "";
    public string Exclude { get; set; } = "";
}

public sealed class SearchMatch
{
    public string RelPath { get; set; } = "";
    public string FullPath { get; set; } = "";
    /// <summary>1-based, so it can be handed straight to the editor's ScrollToLine.</summary>
    public int Line { get; set; }
    /// <summary>0-based offset of the match within the raw line.</summary>
    public int Column { get; set; }
    public int Length { get; set; }
    /// <summary>Windowed line text shown in the results list.</summary>
    public string LineText { get; set; } = "";
    /// <summary>Offset of the match within <see cref="LineText"/> (the window may have shifted it).</summary>
    public int PreviewColumn { get; set; }

    public string LineLabel => Line.ToString();
}

public sealed class SearchFileGroup
{
    public string RelPath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public List<SearchMatch> Matches { get; set; } = new();
    public string FileName => Path.GetFileName(RelPath);
    public string Directory => Path.GetDirectoryName(RelPath)?.Replace('\\', '/') ?? "";
}

public sealed class SearchOutcome
{
    public List<SearchFileGroup> Files { get; } = new();
    public int TotalMatches { get; set; }
    public int FilesScanned { get; set; }
    /// <summary>A cap was hit — results are incomplete and the UI must say so.</summary>
    public bool Truncated { get; set; }
    public string? Error { get; set; }
    public TimeSpan Elapsed { get; set; }
}

public enum NavigationEngine
{
    /// <summary>Bounded workspace scan + declaration heuristics — works with nothing installed.</summary>
    WorkspaceIndex,
    /// <summary>A real language server answered — semantic and exact.</summary>
    LanguageServer,
}

public sealed class CodeLocation
{
    public string RelPath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public int Line { get; set; }
    public string Snippet { get; set; } = "";
    public int Score { get; set; }
    public string FileName => Path.GetFileName(RelPath);
}

public sealed class NavigationResult
{
    public string Symbol { get; set; } = "";
    public NavigationEngine Engine { get; set; } = NavigationEngine.WorkspaceIndex;
    public List<CodeLocation> Locations { get; } = new();
    public string? Error { get; set; }
}

public sealed class RenamePlan
{
    public string OldName { get; set; } = "";
    public string NewName { get; set; } = "";
    public List<SearchFileGroup> Files { get; set; } = new();
    public int TotalEdits { get; set; }
    public bool Truncated { get; set; }
    public string? Error { get; set; }
}

public sealed class CompletionItem
{
    public string Label { get; set; } = "";
    /// <summary>class / interface / method / keyword / word / lsp — drives the icon and colour.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Where it came from, shown next to the label so a suggestion is never unexplained.</summary>
    public string Detail { get; set; } = "";
    public int Score { get; set; }
}

public sealed class RenameResult
{
    public int FilesChanged { get; set; }
    public int EditsApplied { get; set; }
    public List<string> ChangedPaths { get; } = new();
    public List<string> Skipped { get; } = new();
    public string? Error { get; set; }
}
