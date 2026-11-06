using System.Text.RegularExpressions;

namespace CluadeX.Services.Providers;

/// <summary>
/// What a delegated CLI's refusal means for the turn that follows it. Pure, so the harness checks it.
///
/// Why this exists (failure audit 2026-11-05): OpenAI Dev runs of 61 and 114 steps ended on a used-up ChatGPT plan, and
/// <c>CodeAgentService.RunDelegatedTurnAsync</c> — which treats every refusal as a sign-in that may have renewed — sent the
/// same turn again at once. It failed the same way 3 s later, every time, and the log said "signed in · running the turn
/// again". Signing in fixes an expired token; it never fixes a plan limit, a rate limit or a policy refusal.
/// </summary>
public static class CliRefusals
{
    /// <summary>A plan, quota or rate limit: waiting fixes it, a sign-in or an immediate second try does not.</summary>
    public static bool IsLimit(string? text) => text != null && (
        text.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("weekly limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("hit your limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("limit reached", StringComparison.OrdinalIgnoreCase)
        || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || text.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal)
        || Regex.IsMatch(text, @"(?<![\d.])429(?![\d.])"));

    /// <summary>The model's usage-policy classifier stopped the session ("Opus 5.5's safeguards flagged this session",
    /// seen 3× in a row on 2026-10-31, $0.31–$0.34 each). The same conversation is flagged again on a retry.</summary>
    public static bool IsSafeguardFlag(string? text) => text != null && (
        text.Contains("safeguards flagged", StringComparison.OrdinalIgnoreCase)
        || text.Contains("anthropic.com/legal/aup", StringComparison.OrdinalIgnoreCase)
        || text.Contains("Usage Policy", StringComparison.Ordinal));

    /// <summary>A refusal that sending the same turn again cannot fix — only waiting, another model or the user can.</summary>
    public static bool RetryCannotHelp(string? text) => IsLimit(text) || IsSafeguardFlag(text);

    /// <summary>The hint appended to a safeguard refusal, so the user is not left to press "go on" into the same wall.</summary>
    public static string SafeguardHint(bool thai) => thai
        ? "💡 ตัวกรองนโยบายของโมเดลหยุดแชทนี้ การส่งข้อความเดิมซ้ำจะโดนหยุดอีกและเสียเงินทุกครั้ง "
          + "ลองเปลี่ยนโมเดล (เช่น Sonnet) ในกล่องเลือกโมเดล หรือเริ่มแชทใหม่แล้วอธิบายงานให้ชัดว่าทำเพื่ออะไร"
        : "💡 The model's policy filter stopped this chat. Sending the same message again is stopped again and billed each "
          + "time. Switch the model (e.g. Sonnet) in the model picker, or start a new chat that says plainly what the work is for.";

    /// <summary>One log-safe line of a refusal: newlines folded, capped, so the log keeps what the user was shown.</summary>
    public static string ForLog(string? text, int max = 400)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no text)";
        string one = Regex.Replace(text.Trim(), @"\s+", " ");
        // A CLI error can echo a credential (an "Invalid API key sk-…" line, a Bearer header): never into the log.
        one = Regex.Replace(one, @"\b(sk-[A-Za-z0-9_\-]{8,}|axm-[A-Za-z0-9_\-]{8,}|AIza[0-9A-Za-z_\-]{20,})", "[redacted]");
        one = Regex.Replace(one, @"(?i)\b(bearer)\s+[A-Za-z0-9._\-]{12,}", "$1 [redacted]");
        return one.Length > max ? one[..max] + "…" : one;
    }
}
