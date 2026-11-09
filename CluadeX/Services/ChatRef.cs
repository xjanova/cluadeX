using System.Security.Cryptography;
using System.Text;

namespace CluadeX.Services;

/// <summary>
/// A chat's short reference, "CX-8cf58c2a": shown pinned at the top of the chat page and in the sidebar row's tip, so the
/// owner can say WHICH chat they mean (to the team, or to the AI) and find it again by pasting it into the history search.
/// Computed from <see cref="Models.ChatSession.Id"/> — never stored, so every chat already has one and nothing migrates.
/// A GUID id gives its first 8 hex digits; any other id (an old import, a test) the first 8 of its SHA-256, so it is
/// still stable and still 8 hex digits.
/// </summary>
public static class ChatRef
{
    public const string Prefix = "CX-";
    public const int HexLength = 8;

    /// <summary>The start of the line every turn carries (<see cref="TurnNote"/>); harnesses cut the turn's text here.</summary>
    public const string TurnNoteHead = "[This chat's reference: ";

    /// <summary>"CX-" + 8 lowercase hex digits, the same for the same id on every machine. "" for no id.</summary>
    public static string Of(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string hex = Guid.TryParse(id.Trim(), out var g)
            ? g.ToString("N")
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();
        return Prefix + hex[..HexLength];
    }

    /// <summary>
    /// Reads a reference the owner typed or pasted: "CX-8cf58c2a", "cx-8CF58C2A", "#CX-8cf58c2a", "CX 8cf58c2a",
    /// "8cf58c2a", or the whole chat id (a GUID). <paramref name="reference"/> comes back in the <see cref="Of"/> form.
    /// Anything else — other words around it, 7 or 9 digits, a non-hex letter — is not a reference.
    /// </summary>
    public static bool TryParse(string? text, out string reference)
    {
        reference = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (Guid.TryParse(s, out var g)) { reference = Of(g.ToString()); return true; }
        if (s.StartsWith('#')) s = s[1..].TrimStart();
        if (s.StartsWith("cx", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
            if (s.Length > 0 && s[0] is '-' or '_' or ' ' or ':') s = s[1..].TrimStart();
        }
        if (s.Length != HexLength || !s.All(Uri.IsHexDigit)) return false;
        reference = Prefix + s.ToLowerInvariant();
        return true;
    }

    /// <summary>Does <paramref name="text"/> name the chat with this id?</summary>
    public static bool Matches(string? id, string? text) =>
        TryParse(text, out var r) && !string.IsNullOrEmpty(id) && string.Equals(Of(id), r, StringComparison.Ordinal);

    /// <summary>The one line the model gets with each turn, so it can use (and recognise) the chat's reference.</summary>
    public static string TurnNote(string reference) =>
        $"{TurnNoteHead}{reference} — the user may quote it; use it when referring to this chat]";

    /// <summary>The line for the run this code is part of: its chat's reference. Null outside a chat's run (no scope, or a
    /// scope that is not a chat: "auto-comment", "worktree"), and for a sub-agent — its parent's turn carries it.</summary>
    public static string? TurnNoteForCurrentRun()
    {
        var run = AgentRunScope.Current;
        if (run == null || run.Parent != null || !Guid.TryParse(run.SessionKey, out _)) return null;
        return TurnNote(Of(run.SessionKey));
    }

    /// <summary>The turn's message with <see cref="TurnNoteForCurrentRun"/> at its end (unchanged when there is none).</summary>
    public static string WithTurnNote(string? message) =>
        TurnNoteForCurrentRun() is { } note ? $"{message}\n\n{note}" : message ?? "";
}
