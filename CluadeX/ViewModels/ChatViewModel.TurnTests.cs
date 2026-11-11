using System.Collections.Concurrent;
using System.IO;
using CluadeX.Models;
using CluadeX.Services.Agents;
using CluadeX.Services.Tools;

namespace CluadeX.ViewModels;

/// <summary>
/// The test card at the end of a turn (owner 2026-11-11, from a Claude Code session's closing table: "CluadeX มีแบบนี้
/// ไหม"): every suite the turn ran with its pass/fail, each failure read as REAL / FLAKY / UNRELATED / FIXED, and the
/// suites the model left out as out of scope. The runs come from the chat's one tool-result path (OnToolExecuted), so
/// the native loop and every CLI agent fill it the same way; the skips come from wrap_up's tests_skipped or the
/// "Skipped: …" lines of the closing answer.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>wrap_up's tests_skipped per chat, taken when the turn ends.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _pendingTestSkips = new(StringComparer.Ordinal);

    private void InitTurnTests()
    {
        ChatMessage.TurnTestsFor = m => TurnTestSummary.FromJson(m.ToolOutput) is { } data ? new TurnTestsView(data) : null;
        OrchestrationToolModule.TestsSkippedNoted += (chat, items) =>
        {
            if (chat != null && items.Count > 0) _pendingTestSkips[chat] = items;
        };
    }

    /// <summary>At the end of a turn that ran tests (or said which it left out): the test card, after the change card
    /// and before the next-step box. Not after a Stop with nothing run.</summary>
    private void AddTurnTestsCard(ChatRun run)
    {
        bool hadSkips = _pendingTestSkips.TryRemove(run.Session.Id, out var skips);
        var tests = run.Tests;
        if (hadSkips) tests.NoteSkipped(skips);
        // the answer's own "Skipped: …" lines count only in a turn that worked on something: a chat about a video is no test report
        if (tests.Runs.Count > 0 || run.Changes.Count > 0 || hadSkips)
            tests.NoteSkipped(TurnTests.SkippedFromReply(LastReplyOf(run)));
        if (tests.IsEmpty) return;
        if (run.Stopped && tests.Runs.Count == 0) return;

        var changed = run.Changes.Files.Select(f => f.Path).ToList();
        var summary = tests.Summarize(changed, ReadSmallFile);
        run.Messages.Add(new ChatMessage
        {
            Role = MessageRole.System,
            ToolName = ChatMessage.TurnTestsTag,
            ToolOutput = summary.ToJson(),
            // the row's text: what a reloaded chat without the card, a copy, or the model's history shows
            Content = TurnTestsView.PlainLine(summary, Loc),
        });
    }

    private static string? LastReplyOf(ChatRun run)
    {
        for (int i = run.Messages.Count - 1; i >= Math.Min(run.FirstRow, run.Messages.Count); i--)
            if (run.Messages[i] is { Role: MessageRole.Assistant } m && !string.IsNullOrWhiteSpace(m.Content)) return m.Content;
        return null;
    }

    /// <summary>A changed file's text, to see whether it names a failing test (a source file, not a picture).</summary>
    private static string? ReadSmallFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length < 600_000 ? File.ReadAllText(path) : null;
    }
}
