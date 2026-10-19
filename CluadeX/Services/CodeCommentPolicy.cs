using System.IO;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Code Editor ▸ AI comments: turns the user's choice (how much to comment, in which language) into the one instruction
/// every code-writing path hands the model — CluadeX's system prompt (both tool loops, plain chat, Studio and the editor's
/// docked chat go through it), the Claude Dev / Gemini Dev CLI wrappers and file-editing sub-agents.
/// Pure and deterministic: the text depends only on the setting (and joins lines with "\n" on every OS), so it never
/// breaks the prompt cache between turns. Level "none" (the default) returns "" — the prompt is byte-for-byte what it
/// was before this option existed.
/// </summary>
public static class CodeCommentPolicy
{
    public const string None = "none", Key = "key", Detailed = "detailed";
    public const string Thai = "th", English = "en", MatchFile = "match";

    public static string NormalizeLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        Key => Key,
        Detailed => Detailed,
        _ => None,
    };

    public static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        Thai => Thai,
        English => English,
        _ => MatchFile,
    };

    /// <summary>The dictionary key for a project folder: full path, no trailing slash, lower case (Windows paths).</summary>
    public static string ProjectKey(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "";
        string full;
        try { full = Path.GetFullPath(dir.Trim()); } catch { full = dir.Trim(); }
        return full.TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>The setting in force for <paramref name="projectDir"/>: the project's own when it has one, else the global pair.</summary>
    public static (string Level, string Language, bool PerProject) Resolve(AppSettings settings, string? projectDir)
    {
        string key = ProjectKey(projectDir);
        if (key.Length > 0 && settings.CodeCommentProjects != null
            && settings.CodeCommentProjects.TryGetValue(key, out var own) && own != null)
            return (NormalizeLevel(own.Level), NormalizeLanguage(own.Language), true);
        return (NormalizeLevel(settings.CodeCommentLevel), NormalizeLanguage(settings.CodeCommentLanguage), false);
    }

    /// <summary>The instruction for the setting in force in <paramref name="projectDir"/>; "" when the level is none.</summary>
    public static string InstructionFor(AppSettings settings, string? projectDir)
    {
        var (level, language, _) = Resolve(settings, projectDir);
        return Build(level, language);
    }

    /// <summary>The instruction for one level + language; "" for level none. Markdown section, starts with "# Code comments".</summary>
    public static string Build(string? level, string? language)
    {
        level = NormalizeLevel(level);
        language = NormalizeLanguage(language);
        if (level == None) return "";

        return string.Join("\n",
            "# Code comments (the user's setting — it overrides the comment guidance above)",
            "When you write or change code, comment it at the level the user chose:",
            level == Detailed
                ? "- Level DETAILED: comment every function, method and class you write or change, in the language's doc-comment form " +
                  "(/// <summary> in C#, docstrings in Python, JSDoc in JS/TS), and each block with real logic in it — what it does and why. " +
                  "Skip lines that explain themselves (plain assignments, trivial getters)."
                : "- Level KEY POINTS ONLY: add a short comment only where it earns its place — the reason behind a non-obvious decision, " +
                  "a subtle or surprising step, a workaround, a trap for the next reader. Don't narrate what the code plainly says.",
            language switch
            {
                Thai => "- Write the comments in Thai (this replaces \"comments stay in English\"); code, identifiers, paths and technical terms stay as they are.",
                English => "- Write the comments in English.",
                _ => "- Write the comments in the language the file's existing comments already use; a file without comments follows the rest of the project (English when unclear).",
            },
            "Limits that always apply:",
            "- Only the code you add or change in this task gets comments. Never open or edit a file just to comment it, and never comment code you did not otherwise touch.",
            "- Keep existing comments: never rewrite, translate, reword or delete someone else's comment — unless the code it describes changed and it became wrong; then correct just that comment.",
            "- Never put comments in a file that cannot hold them: JSON (.jsonc / .json5 can), lock files, CSV/TSV, minified, generated or binary files, or any data format without a comment syntax.",
            "- A comment must never change behaviour: nothing inside string literals, and shebangs / required headers stay first.");
    }
}
