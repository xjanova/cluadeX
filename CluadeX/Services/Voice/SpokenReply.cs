using CluadeX.Models;

namespace CluadeX.Services.Voice;

/// <summary>
/// Which row of a finished turn is read aloud: only the reply bubble the owner reads as the answer (owner 2026-11-09:
/// "ให้น้องพูดโต้ตอบเสียงเฉพาะบล็อกแบบนี้" — the bubble with Copy / Explain simply / Rewind). Cards, notices, the
/// next-step box and status rows are passed over; a turn whose last row is a tool call, a code run, a permission prompt
/// or the app's own "no summary" line has no answer to read, and an earlier bubble (her "let me look at the file
/// first" before a tool) is never read in its place.
/// </summary>
public static class SpokenReply
{
    /// <summary>The reply bubble to read for the turn the user message at <paramref name="userAt"/> started, or null.
    /// That turn is the one that just ended, the last in the list: everything after the user message belongs to it.</summary>
    public static ChatMessage? Of(IReadOnlyList<ChatMessage> rows, int userAt)
    {
        if (userAt < 0 || userAt >= rows.Count) return null;
        for (int i = rows.Count - 1; i > userAt; i--)
        {
            var m = rows[i];
            // Not part of the conversation's text: cards and notices (System), "thinking…" and status lines, a note
            // the user typed while she worked, and her short line folded into a tool balloon.
            if (m.Role is MessageRole.System or MessageRole.AgentStatus or MessageRole.Thinking or MessageRole.User) continue;
            if (m.FoldedIntoBalloon || m.IsSideNote) continue;
            return IsReplyBubble(m) ? m : null;
        }
        return null;
    }

    /// <summary>A finished answer in her own words: the bubble, not a tool row, a card or the app's note.</summary>
    public static bool IsReplyBubble(ChatMessage m) =>
        m.Role == MessageRole.Assistant
        && !m.IsStreaming && !m.HasError && !m.IsInterrupted && !m.IsTurnNote
        && string.IsNullOrEmpty(m.ToolName)
        && !m.IsPlan && !m.IsAgentMap && !m.IsDiffContent
        && !string.IsNullOrWhiteSpace(m.Content);
}
