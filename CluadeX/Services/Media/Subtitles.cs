using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CluadeX.Services.Media;

/// <summary>
/// Subtitle files (WebVTT, SRT) turned into a transcript a model can read: timing lines and markup dropped, the rolling
/// repeats of YouTube's automatic captions removed (each cue there repeats the line before it and adds a few words), and
/// a "[mm:ss]" mark at the start of a line at most every <see cref="MarkEvery"/>, so the model can say where in the video
/// something is said. Pure: the harness checks it on real-shaped files.
/// </summary>
public static class Subtitles
{
    /// <summary>A time mark starts a new line once this much time has passed since the last one.</summary>
    public static readonly TimeSpan MarkEvery = TimeSpan.FromSeconds(30);

    public sealed record Line(TimeSpan Start, string Text);

    // "00:01:02.345 --> 00:01:04.000 align:start" (VTT, hours optional) or "00:01:02,345 --> 00:01:04,000" (SRT).
    private static readonly Regex Timing = new(
        @"^\s*(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s+-->\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Tag = new(@"<[^>]*>", RegexOptions.Compiled);

    /// <summary>The spoken lines of a VTT or SRT file, in order, each once.</summary>
    public static List<Line> Parse(string text)
    {
        var lines = new List<Line>();
        if (string.IsNullOrEmpty(text)) return lines;
        string last = "";
        TimeSpan? cueStart = null;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var m = Timing.Match(raw);
            if (m.Success)
            {
                cueStart = ParseTime(m);
                continue;
            }
            if (cueStart == null) continue;   // the WEBVTT header, Kind:/Language:, NOTE / STYLE blocks before the first cue
            // An empty line ends the cue (an SRT cue number follows). A line of spaces does not: YouTube's automatic
            // captions start every cue with one, before the words.
            if (raw.Length == 0) { cueStart = null; continue; }
            string line = raw.Trim();
            if (line.Length == 0) continue;
            line = WebUtility.HtmlDecode(Tag.Replace(line, "")).Replace(' ', ' ').Trim();
            line = Regex.Replace(line, @"\s+", " ");
            if (line.Length == 0 || line == last) continue;   // a rolling repeat of the line before
            last = line;
            lines.Add(new Line(cueStart.Value, line));
        }
        return lines;
    }

    private static TimeSpan ParseTime(Match m)
    {
        int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        int min = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        int ms = int.Parse(m.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return new TimeSpan(0, h, min, s, ms);
    }

    /// <summary>The lines as text: a "[mm:ss]" (or "[h:mm:ss]") mark at the start, then again on a new line whenever
    /// <see cref="MarkEvery"/> has passed; the lines in between are joined with a space.</summary>
    public static string Format(IReadOnlyList<Line> lines, TimeSpan? offset = null)
    {
        var sb = new StringBuilder();
        TimeSpan? lastMark = null;
        TimeSpan shift = offset ?? TimeSpan.Zero;
        foreach (var l in lines)
        {
            var at = l.Start + shift;
            if (lastMark == null || at - lastMark.Value >= MarkEvery)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append('[').Append(Clock(at)).Append("] ");
                lastMark = at;
            }
            else sb.Append(' ');
            sb.Append(l.Text);
        }
        return sb.ToString();
    }

    /// <summary>"m:ss" under an hour, "h:mm:ss" from an hour on.</summary>
    public static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes + t.Hours * 60}:{t.Seconds:00}";
    }
}
