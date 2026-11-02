using System.IO;
using System.Text;
using CluadeX.Models;

namespace CluadeX.Services.Tools;

/// <summary>checkpoint_list / checkpoint_diff / checkpoint_restore — the agent rewinds its own edits (CheckpointService).</summary>
public sealed partial class EngineeringToolModule
{
    private const int MaxDiffFileBytes = 2 * 1024 * 1024;

    private ToolResult CheckpointList(ToolCall call, ToolModuleContext ctx)
    {
        if (CheckpointStore == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        string root = ctx.WorkingDirectory!;
        string session = AgentRunScope.Current?.SessionKey ?? "";
        bool all = IsTrue(Arg(call, "all_chats")) || session.Length == 0;
        int limit = IntArg(call, "limit", 15, 1, 100);
        var points = CheckpointStore.Points(root);
        if (!all) points = points.Where(p => p.Session == session).ToList();
        if (points.Count == 0)
            return Ok(call, all ? "No restore points for this project yet: they are made when an agent tool changes a file."
                : "This chat has no restore points in this project yet (all_chats=\"true\" lists every chat's).", "0 restore points");

        var sb = new StringBuilder($"{points.Count} restore point(s){(all ? "" : " from this chat")}, newest first"
            + (points.Count > limit ? $" (showing {limit})" : "") + ". Restoring a point puts back every file it or a later point changed.\n");
        foreach (var p in points.Take(limit))
        {
            string mine = session.Length > 0 && p.Session == session ? "*" : " ";
            string files = string.Join(", ", p.Files.Select(f => Rel(root, f.File) + (f.Existed ? "" : " (new)")).Take(8))
                           + (p.Files.Count > 8 ? $", +{p.Files.Count - 8}" : "");
            sb.AppendLine($"{mine} {p.Id}  {p.When}  step {p.Step}  {Clip(p.Title, 80)}");
            sb.AppendLine($"    {files}");
        }
        return Ok(call, OutputFit(sb.ToString(), ctx), $"{points.Count} restore point(s)");
    }

    private ToolResult CheckpointDiff(ToolCall call, ToolModuleContext ctx)
    {
        if (CheckpointStore == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        string root = ctx.WorkingDirectory!;
        string id = Arg(call, "id");
        var plan = id.Length == 0 ? null : CheckpointStore.RestorePlan(root, id);
        if (plan == null) return Fail(call, $"No restore point '{id}' in this project — checkpoint_list shows the ids.");
        string? only = Arg(call, "path") is { Length: > 0 } p ? InsideProject(root, p) : null;
        if (Arg(call, "path").Length > 0 && only == null) return Fail(call, $"'{Arg(call, "path")}' is outside the project.");

        var sb = new StringBuilder($"Restoring {id} would change {plan.Count} file(s) (− current, + restored):\n");
        int added = 0, removed = 0;
        foreach (var e in plan)
        {
            if (only != null && !string.Equals(Path.GetFullPath(e.File), only, StringComparison.OrdinalIgnoreCase)) continue;
            string rel = Rel(root, e.File);
            bool nowExists = File.Exists(e.File);
            byte[]? kept = CheckpointStore.ReadKept(e);
            sb.AppendLine();
            if (!e.Existed)
            {
                sb.AppendLine(nowExists ? $"=== {rel}: would be DELETED (that step created it)" : $"=== {rel}: created by that step, already gone");
                continue;
            }
            if (kept == null) { sb.AppendLine($"=== {rel}: the kept copy is missing — restoring would fail for this file"); continue; }
            byte[] current = nowExists ? SafeRead(e.File) : Array.Empty<byte>();
            if (current.AsSpan().SequenceEqual(kept)) { sb.AppendLine($"=== {rel}: unchanged since then"); continue; }
            if (kept.Length > MaxDiffFileBytes || current.Length > MaxDiffFileBytes || LooksBinary(kept) || LooksBinary(current))
            {
                sb.AppendLine($"=== {rel}: {(nowExists ? "" : "deleted since, ")}would go from {current.Length:N0} to {kept.Length:N0} bytes (binary or large — no line diff)");
                continue;
            }
            var diff = DiffService.BuildSideBySide(nowExists ? CluadeX.Helpers.TextFileCodec.Decode(current).Text : "", CluadeX.Helpers.TextFileCodec.Decode(kept).Text);
            sb.AppendLine($"=== {rel}{(nowExists ? "" : " (deleted since — would be recreated)")}: −{diff.Removed} +{diff.Added}");
            AppendHunks(sb, diff);
            added += diff.Added; removed += diff.Removed;
        }
        return Ok(call, OutputFit(sb.ToString(), ctx), $"checkpoint {id}: {plan.Count} file(s), −{removed} +{added}");
    }

    private ToolResult CheckpointRestore(ToolCall call, ToolModuleContext ctx)
    {
        if (CheckpointStore == null) return Fail(call, string.Format(L("tool.module.unavailable"), call.ToolName, Id));
        string root = ctx.WorkingDirectory!;
        string id = Arg(call, "id");
        var plan = id.Length == 0 ? null : CheckpointStore.RestorePlan(root, id);
        if (plan == null) return Fail(call, $"No restore point '{id}' in this project — checkpoint_list shows the ids.");
        // The index is a file in the user's profile: never let it steer a write outside the project.
        var outside = plan.Where(e => InsideProject(root, e.File) == null).Select(e => e.File).ToList();
        if (outside.Count > 0)
            return Fail(call, $"Restore point {id} also covers files outside this project ({string.Join(", ", outside.Take(3))}). "
                + "Nothing was restored — the user can do it from the Time Machine.");
        var (ok, files, message) = CheckpointStore.RestoreTo(root, id);
        if (!ok) return Fail(call, $"Restore to {id} failed: {message}. Files may be partly restored — checkpoint_list shows the undo point if one was made.");
        var sb = new StringBuilder($"Restored {files} file(s) to how they were before {id}:");
        foreach (var e in plan) sb.Append("\n  ").Append(Rel(root, e.File)).Append(e.Existed ? "" : " (removed — that step created it)");
        sb.Append($"\nThe state before this restore is kept as {message}: checkpoint_restore id=\"{message}\" undoes it.");
        sb.Append("\nRe-read any file you edit next — its content changed.");
        return Ok(call, sb.ToString(), $"restored {files} file(s) to {id}");
    }

    /// <summary>What the restore prompt shows the user: the files that would change.</summary>
    private string RestoreDetail(string id, string? root)
    {
        if (CheckpointStore == null || string.IsNullOrEmpty(root)) return "Puts files back to an earlier restore point.";
        var plan = CheckpointStore.RestorePlan(root, id);
        if (plan == null) return $"No restore point '{id}' in this project.";
        return $"Puts {plan.Count} file(s) back to how they were before this point (the current state is kept, so it can be undone):\n"
               + string.Join("\n", plan.Take(12).Select(e => "  " + Rel(root, e.File) + (e.Existed ? "" : " (deleted)")))
               + (plan.Count > 12 ? $"\n  … +{plan.Count - 12}" : "");
    }

    private static byte[] SafeRead(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return Array.Empty<byte>(); }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 8000);
        for (int i = 0; i < n; i++) if (bytes[i] == 0) return true;
        return false;
    }

    /// <summary>The changed rows with two rows of context, unified style.</summary>
    private static void AppendHunks(StringBuilder sb, SideBySideDiff diff)
    {
        var rows = diff.Rows;
        var keep = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].IsChange)
                for (int k = Math.Max(0, i - 2); k <= Math.Min(rows.Count - 1, i + 2); k++) keep[k] = true;
        bool gap = false;
        int lines = 0;
        for (int i = 0; i < rows.Count && lines < 400; i++)
        {
            if (!keep[i]) { gap = true; continue; }
            if (gap) { sb.AppendLine("  …"); gap = false; }
            var r = rows[i];
            switch (r.Kind)
            {
                case DiffRowKind.Unchanged: sb.AppendLine($"   {r.RightText}"); break;
                case DiffRowKind.Removed: sb.AppendLine($"-  {r.LeftText}"); break;
                case DiffRowKind.Added: sb.AppendLine($"+  {r.RightText}"); break;
                default: sb.AppendLine($"-  {r.LeftText}"); sb.AppendLine($"+  {r.RightText}"); break;
            }
            lines++;
        }
        if (diff.Truncated) sb.AppendLine("  (diff truncated)");
    }
}
