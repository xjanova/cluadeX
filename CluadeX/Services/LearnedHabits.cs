namespace CluadeX.Services;

/// <summary>
/// The learned habits (instincts strong enough for the prompt) for the CLI providers — Claude Dev, Gemini Dev, Grok
/// Dev, Codex, Antigravity. They build their own short prompt and never see CluadeX's system prompt, so the
/// "# Learned habits" section there reached only the API and local providers; the owner's provider (Claude Dev)
/// never got one. Each CLI call site appends this to the instruction it already sends (the code-comment policy).
/// </summary>
public static class LearnedHabits
{
    /// <summary>The section's heading, shared with CodeAgentService's system prompt.</summary>
    public const string Heading = "# Learned habits (from past chats with this user; apply when relevant)";

    /// <summary>The section for <paramref name="projectFolder"/>: global instincts and this project's, or "" when none
    /// qualifies. Characters a cmd.exe shim would act on (" % ^ &amp; | &lt; &gt;) are replaced: the Claude Dev
    /// wrapper goes through one.</summary>
    public static string For(string? projectFolder, InstinctService? store = null, int max = 5)
    {
        store ??= InstinctService.Current;
        if (store == null) return "";
        string? block;
        try { block = store.HabitsBlock(projectFolder, max); }
        catch { return ""; }
        if (string.IsNullOrWhiteSpace(block)) return "";
        var sb = new System.Text.StringBuilder(block.Length);
        foreach (char c in block)
            sb.Append(c switch
            {
                '"' => '\'',
                '%' => ' ',
                '^' or '&' or '|' or '<' or '>' or '`' => ' ',
                _ => char.IsControl(c) && c != '\n' ? ' ' : c,
            });
        return Heading + "\n" + sb.ToString();
    }

    /// <summary><paramref name="instruction"/> with the habits after it (a blank line between), or as it was when none.</summary>
    public static string Append(string? instruction, string? projectFolder)
    {
        string habits = For(projectFolder);
        if (habits.Length == 0) return instruction ?? "";
        return string.IsNullOrWhiteSpace(instruction) ? habits : instruction + "\n\n" + habits;
    }
}
