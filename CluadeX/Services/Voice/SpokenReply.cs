using CluadeX.Models;

namespace CluadeX.Services.Voice;

/// <summary>
/// Which rows of a turn are read aloud: every reply bubble she wrote in it, in order, each once (owner 2026-11-11:
/// "ทำไม ไม่อ่านทุกบับเบิ้ลที่ต้องอ่าน"). A reply bubble is the block with Copy / Explain simply / Rewind (owner
/// 2026-11-09: "ให้น้องพูดโต้ตอบเสียงเฉพาะบล็อกแบบนี้"): cards, notices, the next-step box, status rows, tool rows,
/// plans, errors and the app's own "no summary" line are passed over.
/// <para>Before 2026-11-11 only the turn's last row was read, and only when the turn ended: a Codex or Claude CLI turn
/// that wrote six bubbles between its tool calls read one of them (or none, when it ended on a question or a tool
/// row), and a turn waiting on ask_user read nothing while she asked.</para>
/// <para>A bubble is read once its text is final: a later row of hers (a new bubble or her question) follows it, or the
/// turn ended. A tool row after it is not enough — a CLI can keep writing into the same bubble after a tool or a
/// notice, and the turn's end may still put the final answer into the last one.</para>
/// </summary>
public static class SpokenReply
{
    /// <summary>One piece to read: a bubble and the words of it not read yet (all of it, or what it grew by).</summary>
    public readonly record struct Part(ChatMessage Message, string Text);

    /// <summary>The words of the turn the user message at <paramref name="userAt"/> started that are ready to be read
    /// and not read yet, in the order she wrote them. That turn is the last in the list: everything after the user
    /// message belongs to it. <paramref name="turnEnded"/> = every bubble is final.</summary>
    public static List<Part> ToRead(IReadOnlyList<ChatMessage> rows, int userAt, bool turnEnded)
    {
        var list = new List<Part>();
        if (userAt < 0 || userAt >= rows.Count) return list;
        // A bubble before her later row (her next bubble, or her question) is final by where it is, even if nothing
        // turned its streaming cursor off (a CLI's text before its ask_user). From her last row on, the text may still
        // change while the turn runs; when it ended, a row there still streaming was cut off (Stop) and is not read.
        int open = FirstOpenRow(rows, userAt);
        int end = turnEnded ? rows.Count : open;
        for (int i = userAt + 1; i < end; i++)
        {
            var m = rows[i];
            if (IsPassedOver(m)) continue;
            if (!(i < open ? IsReplyText(m) : IsReplyBubble(m))) continue;
            if (Unread(m) is { } words) list.Add(new Part(m, words));
        }
        return list;
    }

    /// <summary>The words of a bubble not read yet: all of it the first time; later only what was written after the
    /// words read (a CLI went on in the same bubble); null when nothing new is there or the text was replaced.</summary>
    public static string? Unread(ChatMessage m)
    {
        string now = m.Content ?? "";
        if (m.VoiceReadText is not { } read) return now;
        if (now.Length <= read.Length || !now.StartsWith(read, StringComparison.Ordinal)) return null;
        string more = now[read.Length..];
        return string.IsNullOrWhiteSpace(more) ? null : more;
    }

    /// <summary>While the turn runs: the index of the row from which on her text may still change — her last row
    /// (a bubble still being written or that the turn's end may finish). Everything before it is final.</summary>
    private static int FirstOpenRow(IReadOnlyList<ChatMessage> rows, int userAt)
    {
        for (int i = rows.Count - 1; i > userAt; i--)
        {
            var m = rows[i];
            // Her question to the user (ask_user) closes what came before it, and is itself not read as a bubble.
            if (m.Role == MessageRole.Question) return i;
            // Her text row (a bubble, being written or not): not a tool row, which a CLI may write more text above.
            if (m.Role == MessageRole.Assistant && string.IsNullOrEmpty(m.ToolName) && !m.FoldedIntoBalloon && !m.IsSideNote) return i;
        }
        return userAt + 1;
    }

    /// <summary>Not part of the conversation's text: cards and notices (System), "thinking…" and status lines, a note
    /// the user typed while she worked, and her short line folded into a tool balloon.</summary>
    private static bool IsPassedOver(ChatMessage m) =>
        m.Role is MessageRole.System or MessageRole.AgentStatus or MessageRole.Thinking or MessageRole.User
        || m.FoldedIntoBalloon || m.IsSideNote;

    /// <summary>The turn's last row that is part of the conversation, or null when there is none — for the log, which
    /// says why nothing was read.</summary>
    public static ChatMessage? LastRow(IReadOnlyList<ChatMessage> rows, int userAt)
    {
        if (userAt < 0 || userAt >= rows.Count) return null;
        for (int i = rows.Count - 1; i > userAt; i--)
            if (!IsPassedOver(rows[i])) return rows[i];
        return null;
    }

    /// <summary>A few words on a row that is not read: its role and what it is (the log line).</summary>
    public static string Describe(ChatMessage? m) => m == null
        ? "no row after the message"
        : $"{m.Role}{(string.IsNullOrEmpty(m.ToolName) ? "" : " " + m.ToolName)}"
          + (m.IsStreaming ? " streaming" : "") + (m.HasError ? " error" : "") + (m.IsInterrupted ? " interrupted" : "")
          + (m.IsTurnNote ? " turn-note" : "") + (m.IsPlan ? " plan" : "") + (m.IsAgentMap ? " agent-map" : "")
          + (m.IsDiffContent ? " diff" : "") + (string.IsNullOrWhiteSpace(m.Content) ? " empty" : "");

    /// <summary>A finished answer in her own words: the bubble, not a tool row, a card or the app's note.</summary>
    public static bool IsReplyBubble(ChatMessage m) => !m.IsStreaming && IsReplyText(m);

    /// <summary>Her own words in a bubble, written to the end or not.</summary>
    private static bool IsReplyText(ChatMessage m) =>
        m.Role == MessageRole.Assistant
        && !m.HasError && !m.IsInterrupted && !m.IsTurnNote
        && string.IsNullOrEmpty(m.ToolName)
        && !m.IsPlan && !m.IsAgentMap && !m.IsDiffContent
        && !string.IsNullOrWhiteSpace(m.Content);
}
