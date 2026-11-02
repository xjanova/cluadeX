using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Models;

namespace CluadeX.Services.Tools;

/// <summary>find_references, rename_symbol and impact_of — CodeIntelligenceService, which the IDE already uses.</summary>
public sealed partial class EngineeringToolModule
{
    private static readonly Regex Identifier = new(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.Compiled);

    // What may stand before a declared name on its line: modifiers, keywords, a type — no call, member
    // access, assignment or base list. RepoMap's index answers "a declaration line that mentions the
    // name", so `int Twice() => Total(…)` came back as a definition of Total without this.
    private static readonly Regex DeclarationPrefix = new(@"^\s*(?:[\w<>\[\],?*&@]+\s+)*$", RegexOptions.Compiled);

    // Logs and data files that happen to contain the word: a rename leaves them alone (a test log
    // saying "Total: 3" is not a use of Total) and says so, so the model edits one by hand if it means to.
    private static readonly HashSet<string> NotSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".trx", ".out", ".csv", ".tsv", ".lock", ".sum", ".map", ".snap",
    };

    private static bool IsNotSource(SearchFileGroup file) => NotSourceExtensions.Contains(Path.GetExtension(file.FullPath));

    private static string FileCounts(IEnumerable<SearchFileGroup> files) =>
        string.Join(", ", files.Select(f => $"{f.RelPath.Replace('\\', '/')} ({f.Matches.Count})"));

    private async Task<ToolResult> FindReferencesAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        if (_intel == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        string symbol = Arg(call, "symbol");
        if (!Identifier.IsMatch(symbol)) return Fail(call, "find_references needs 'symbol': one identifier (letters, digits, _), e.g. CalculateTotal.");
        int max = IntArg(call, "max_results", 200, 1, 2000);

        var found = await _intel.SearchAsync(new SearchQuery
        {
            Text = symbol, MatchCase = true, WholeWord = true, Include = Arg(call, "include"), IncludeLongLines = true,
        }, ct);
        if (found.Error != null) return Fail(call, found.Error);
        if (found.TotalMatches == 0)
            return Ok(call, $"No whole-word, case-sensitive use of '{symbol}' in the project ({found.FilesScanned} files scanned).", $"0 references to {symbol}");

        var defs = await DefinitionLinesAsync(symbol, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"{found.TotalMatches} reference(s) to '{symbol}' in {found.Files.Count} file(s){(found.Truncated ? " (search stopped early — narrow it with include)" : "")}. "
            + "Textual match: same-named members of other types are included.");
        int shown = 0;
        foreach (var file in found.Files)
        {
            if (shown >= max) break;
            sb.AppendLine();
            sb.AppendLine($"{file.RelPath.Replace('\\', '/')} ({file.Matches.Count})");
            foreach (var m in file.Matches)
            {
                if (shown++ >= max) break;
                bool def = defs.Contains((file.FullPath.ToLowerInvariant(), m.Line))
                    && m.PreviewColumn <= m.LineText.Length && DeclarationPrefix.IsMatch(m.LineText[..m.PreviewColumn]);
                sb.AppendLine($"  {m.Line}:{m.Column + 1}{(def ? " [definition]" : "")}  {Clip(m.LineText.Trim(), 200)}");
            }
        }
        if (shown < found.TotalMatches) sb.AppendLine($"\n… {found.TotalMatches - shown} more not listed (raise max_results or narrow with include).");
        return Ok(call, OutputFit(sb.ToString(), ctx), $"{found.TotalMatches} reference(s) to {symbol} in {found.Files.Count} file(s)");
    }

    /// <summary>(full path lowercased, 1-based line) of each place <paramref name="symbol"/> is declared.</summary>
    private async Task<HashSet<(string, int)>> DefinitionLinesAsync(string symbol, CancellationToken ct)
    {
        var set = new HashSet<(string, int)>();
        if (_intel == null) return set;
        try
        {
            var nav = await _intel.FindDefinitionsAsync(symbol, null, 0, 0, ct);
            foreach (var loc in nav.Locations) set.Add((loc.FullPath.ToLowerInvariant(), loc.Line));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* the marks are a convenience */ }
        return set;
    }

    private async Task<ToolResult> RenameAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        if (_intel == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        string oldName = Arg(call, "old_name"), newName = Arg(call, "new_name");
        if (!Identifier.IsMatch(oldName)) return Fail(call, "rename_symbol needs 'old_name': one identifier.");
        var plan = await _intel.PrepareRenameAsync(oldName, newName, ct);
        if (plan.Error != null) return Fail(call, plan.Error);
        var leftAlone = plan.Files.Where(IsNotSource).ToList();
        if (leftAlone.Count > 0)
        {
            plan.Files = plan.Files.Where(f => !IsNotSource(f)).ToList();
            plan.TotalEdits = plan.Files.Sum(f => f.Matches.Count);
        }
        if (plan.TotalEdits == 0 && leftAlone.Count > 0)
            return Fail(call, $"'{oldName}' occurs only in files that are not source code ({FileCounts(leftAlone)}) — nothing to rename. Use edit_file if you mean to change them.");
        if (plan.TotalEdits == 0) return Fail(call, $"'{oldName}' does not occur in the project as a whole word — nothing to rename.");
        if (plan.Truncated)
            return Fail(call, $"'{oldName}' occurs in more places than one rename may cover (the search stopped early). Rename it in parts with edit_file, or narrow the name.");

        bool apply = IsTrue(Arg(call, "apply"));
        if (!apply)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"PREVIEW — nothing changed. Renaming '{oldName}' → '{newName}' would make {plan.TotalEdits} edit(s) in {plan.Files.Count} file(s):");
            foreach (var file in plan.Files)
            {
                sb.AppendLine();
                sb.AppendLine($"{file.RelPath.Replace('\\', '/')} ({file.Matches.Count})");
                foreach (var m in file.Matches.Take(40))
                    sb.AppendLine($"  {m.Line}:{m.Column + 1}  {Clip(m.LineText.Trim(), 200)}");
                if (file.Matches.Count > 40) sb.AppendLine($"  … {file.Matches.Count - 40} more in this file");
            }
            if (leftAlone.Count > 0)
                sb.AppendLine().AppendLine($"Left alone — not source code: {FileCounts(leftAlone)}. Use edit_file if one of them should change too.");
            sb.AppendLine();
            sb.AppendLine("Check the list for unrelated same-named symbols (strings, comments, other types). To apply exactly this: "
                + $"rename_symbol with old_name=\"{oldName}\", new_name=\"{newName}\", apply=\"true\", expected_edits=\"{plan.TotalEdits}\".");
            return Ok(call, OutputFit(sb.ToString(), ctx), $"rename preview: {plan.TotalEdits} edit(s) in {plan.Files.Count} file(s)");
        }

        if (!int.TryParse(Arg(call, "expected_edits"), out int expected))
            return Fail(call, $"apply needs expected_edits: preview first (rename_symbol without apply) and pass its edit count. The code has {plan.TotalEdits} edit(s) now.");
        if (expected != plan.TotalEdits)
            return Fail(call, $"The code changed since the preview: it now has {plan.TotalEdits} edit(s), not {expected}. Nothing was renamed — preview again.");

        // Each file as it was, so checkpoint_restore can take the rename back.
        foreach (var file in plan.Files)
            CheckpointStore?.CaptureBeforeChange(file.FullPath, $"rename {oldName} → {newName}");
        var result = await _intel.ApplyRenameAsync(plan, ct);
        _intel.InvalidateSymbolIndex();
        _repoMap?.Invalidate();
        var outText = new StringBuilder($"Renamed '{oldName}' → '{newName}': {result.EditsApplied} edit(s) in {result.FilesChanged} file(s).");
        foreach (var path in result.ChangedPaths) outText.Append("\n  ").Append(Rel(ctx.WorkingDirectory!, path));
        if (result.Skipped.Count > 0)
        {
            outText.Append($"\nSkipped {result.Skipped.Count} file(s) — not renamed:");
            foreach (var s in result.Skipped) outText.Append("\n  ").Append(s);
        }
        outText.Append("\nBuild (run_build or verify_change) to confirm nothing else referred to the old name.");
        var ok = Ok(call, outText.ToString(), $"renamed {oldName} → {newName} ({result.EditsApplied} edits)");
        ok.Success = result.FilesChanged > 0;
        if (!ok.Success) ok.Error = "Nothing was renamed.";
        return ok;
    }

    private async Task<ToolResult> ImpactAsync(ToolCall call, ToolModuleContext ctx, CancellationToken ct)
    {
        if (_intel == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        string root = ctx.WorkingDirectory!;
        var symbols = new List<string>();
        string? ownFile = null;
        if (Arg(call, "symbol") is { Length: > 0 } sym)
        {
            if (!Identifier.IsMatch(sym)) return Fail(call, "impact_of needs 'symbol': one identifier.");
            symbols.Add(sym);
        }
        else if (Arg(call, "path") is { Length: > 0 } path)
        {
            ownFile = InsideProject(root, path);
            if (ownFile == null) return Fail(call, $"'{path}' is outside the project.");
            if (!File.Exists(ownFile)) return Fail(call, $"No file at {path}.");
            string text;
            try { text = File.ReadAllText(ownFile); }
            catch (Exception ex) { return Fail(call, $"Could not read {path}: {ex.Message}"); }
            var outline = _repoMap?.OutlineContent(text) ?? new List<(int line, string symbol)>();
            foreach (var (_, s) in outline)
            {
                var m = Regex.Match(s, @"^(?:class|interface|struct|enum|record|type|trait|module|object)\s+(\w+)");
                if (m.Success && !symbols.Contains(m.Groups[1].Value)) symbols.Add(m.Groups[1].Value);
            }
            if (symbols.Count == 0)
                symbols.Add(Path.GetFileNameWithoutExtension(ownFile));
        }
        else return Fail(call, "impact_of needs 'symbol' or 'path'.");

        var users = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int total = 0;
        foreach (var s in symbols.Take(12))
        {
            var found = await _intel.SearchAsync(new SearchQuery { Text = s, MatchCase = true, WholeWord = true }, ct);
            foreach (var f in found.Files)
            {
                if (ownFile != null && string.Equals(f.FullPath, ownFile, StringComparison.OrdinalIgnoreCase)) continue;
                users[f.FullPath] = users.GetValueOrDefault(f.FullPath) + f.Matches.Count;
                total += f.Matches.Count;
            }
        }

        var sb = new StringBuilder();
        string what = ownFile != null ? $"{Rel(root, ownFile)} (declares {string.Join(", ", symbols.Take(12))})" : $"'{symbols[0]}'";
        if (users.Count == 0)
        {
            sb.AppendLine($"Nothing else in the project refers to {what} by name. A change there is local — but reflection, configuration, "
                + "string-based lookups and other repositories are not searched.");
            return Ok(call, sb.ToString().TrimEnd(), "impact: no other file refers to it");
        }
        bool IsTest(string f) => Regex.IsMatch(Rel(root, f), @"(^|[/\\._-])(tests?|specs?|__tests__)([/\\._-]|$)", RegexOptions.IgnoreCase);
        var tests = users.Keys.Where(IsTest).ToList();
        var projects = users.Keys.Select(f => OwningProject(f, root)).Where(p => p != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        sb.AppendLine($"Impact of {what}: {total} use(s) in {users.Count} other file(s).");
        sb.AppendLine();
        sb.AppendLine("Files that use it:");
        foreach (var (f, n) in users.OrderByDescending(x => x.Value).Take(60))
            sb.AppendLine($"  {Rel(root, f)} ({n}){(IsTest(f) ? " [test]" : "")}");
        if (users.Count > 60) sb.AppendLine($"  … {users.Count - 60} more");
        sb.AppendLine();
        sb.AppendLine(tests.Count > 0
            ? $"Tests that touch it ({tests.Count}): {string.Join(", ", tests.Take(10).Select(t => Rel(root, t)))} — run them after the change."
            : "No test file refers to it by name: a change here is not covered by any test that names it.");
        if (projects.Count > 0) sb.AppendLine($"Projects to rebuild: {string.Join(", ", projects.Select(p => Rel(root, p!)))}");
        sb.AppendLine("Textual search: same-named symbols in unrelated types are counted too; reflection and config are not seen.");
        return Ok(call, OutputFit(sb.ToString(), ctx), $"impact: {users.Count} file(s), {tests.Count} test file(s)");
    }

    /// <summary>The nearest project file (csproj, package.json, Cargo.toml, go.mod, pyproject.toml) above <paramref name="file"/>.</summary>
    private static string? OwningProject(string file, string root)
    {
        try
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            string? dir = Path.GetDirectoryName(file);
            while (dir != null && dir.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                var proj = Directory.EnumerateFiles(dir, "*.*proj").FirstOrDefault()
                    ?? new[] { "package.json", "Cargo.toml", "go.mod", "pyproject.toml" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
                if (proj != null) return proj;
                if (string.Equals(dir.TrimEnd('\\', '/'), r, StringComparison.OrdinalIgnoreCase)) break;
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { }
        return null;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string OutputFit(string text, ToolModuleContext ctx) =>
        CluadeX.Helpers.OutputBudget.HeadAndTail(text.TrimEnd(), Math.Max(4000, ctx.OutputBudgetChars));
}
