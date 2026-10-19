using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Voice;

/// <summary>
/// A chat reply turned into something worth hearing. Replies are Markdown with code, paths and links: read as they are,
/// a voice spells out every backtick, pipe and URL. Code blocks are left out (a short word says one was there), links
/// keep their text, Markdown marks go, and the whole is cut at a sentence end before it gets long.
/// </summary>
public static partial class SpeechText
{
    /// <summary>About this many characters are read aloud from one reply (a minute or so of speech).</summary>
    public const int DefaultMaxChars = 1200;

    public static string ForSpeech(string? markdown, bool thai, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        string codeWord = thai ? " (มีโค้ดในแชท) " : " (code in the chat) ";
        string s = markdown.Replace("\r\n", "\n");
        s = ThinkBlock().Replace(s, " ");
        s = FencedCode().Replace(s, codeWord);
        s = Image().Replace(s, " ");
        s = Link().Replace(s, "$1");
        s = BareUrl().Replace(s, " ");
        s = InlineCode().Replace(s, "$1");
        s = HtmlTag().Replace(s, " ");
        var sb = new StringBuilder(s.Length);
        foreach (var raw in s.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) { sb.Append('\n'); continue; }
            if (TableRule().IsMatch(line) || HorizontalRule().IsMatch(line)) continue;
            line = Heading().Replace(line, "");
            line = Quote().Replace(line, "");
            line = Bullet().Replace(line, "");
            if (line.Contains('|')) line = string.Join(", ", line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            line = Emphasis().Replace(line, "$2");
            line = line.Replace("~~", "").Replace("__", "");
            sb.Append(line);
            // A line with no full stop of its own is a list item or a heading: a pause, not a run-on.
            sb.Append(EndsSentence(line) ? " " : ". ");
            sb.Append('\n');
        }
        s = Spaces().Replace(sb.ToString(), " ");
        s = RepeatedStops().Replace(s, ". ");
        s = s.Replace(" .", ".").Trim();
        if (s.StartsWith('.')) s = s.TrimStart('.', ' ');
        return Cut(s, maxChars, thai);
    }

    /// <summary>Text cut to at most <paramref name="max"/> characters, at a sentence end when one is near, else at a
    /// space; an ellipsis marks the cut.</summary>
    public static string Cut(string s, int max, bool thai = false)
    {
        if (s.Length <= max) return s;
        string head = s[..max];
        int stop = Math.Max(Math.Max(head.LastIndexOf(". ", StringComparison.Ordinal), head.LastIndexOf("? ", StringComparison.Ordinal)),
            Math.Max(head.LastIndexOf("! ", StringComparison.Ordinal), head.LastIndexOf('\n')));
        if (stop >= max / 2) head = head[..(stop + 1)];
        else
        {
            int space = head.LastIndexOf(' ');
            if (space >= max / 2) head = head[..space];
        }
        return head.TrimEnd() + (thai ? " … (อ่านต่อในแชท)" : " … (more in the chat)");
    }

    /// <summary>
    /// Text split into pieces of at most <paramref name="maxChars"/>, on sentence or word boundaries — GPT-Live takes at
    /// most 500 tokens per context append, and a Thai character can be a token of its own, so the caller keeps pieces
    /// well under that in characters.
    /// </summary>
    public static List<string> Chunks(string text, int maxChars)
    {
        var parts = new List<string>();
        string rest = (text ?? "").Trim();
        while (rest.Length > maxChars)
        {
            string head = rest[..maxChars];
            int cut = Math.Max(head.LastIndexOf(". ", StringComparison.Ordinal), head.LastIndexOf('\n'));
            if (cut < maxChars / 2) cut = head.LastIndexOf(' ');
            if (cut < maxChars / 2) cut = maxChars - 1;
            parts.Add(rest[..(cut + 1)].Trim());
            rest = rest[(cut + 1)..].Trim();
        }
        if (rest.Length > 0) parts.Add(rest);
        return parts;
    }

    private static bool EndsSentence(string line) => line.Length > 0 && ".!?:;…。".Contains(line[^1]);

    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)] private static partial Regex ThinkBlock();
    [GeneratedRegex(@"```[\s\S]*?(```|$)")] private static partial Regex FencedCode();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\((?:[^)]*)\)")] private static partial Regex Link();
    [GeneratedRegex(@"\b(?:https?|file)://\S+", RegexOptions.IgnoreCase)] private static partial Regex BareUrl();
    [GeneratedRegex(@"`([^`\n]*)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"</?[a-zA-Z][^>\n]*>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")] private static partial Regex TableRule();
    [GeneratedRegex(@"^([-*_]\s*){3,}$")] private static partial Regex HorizontalRule();
    [GeneratedRegex(@"^#{1,6}\s+")] private static partial Regex Heading();
    [GeneratedRegex(@"^(>\s*)+")] private static partial Regex Quote();
    [GeneratedRegex(@"^([-*+•]|\d+[.)])\s+(\[[ xX]\]\s+)?")] private static partial Regex Bullet();
    [GeneratedRegex(@"(\*{1,3})([^*\n]+)\1")] private static partial Regex Emphasis();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"(\s*\.\s*){2,}")] private static partial Regex RepeatedStops();
}
