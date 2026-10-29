using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace CluadeX.Helpers;

/// <summary>What a link in a chat reply points at, once read.</summary>
public enum ChatLinkKind
{
    /// <summary>An http(s) or mailto address: the browser / mail app opens it.</summary>
    Web,
    /// <summary>A file on this machine the shell may show (a picture, a page, a document): its viewer opens it.</summary>
    ViewFile,
    /// <summary>Any other file on this machine (a script, a program, source): shown selected in Explorer, never run.</summary>
    RevealFile,
    /// <summary>A folder on this machine: Explorer opens it.</summary>
    Folder,
    /// <summary>A local path that is not there (any more).</summary>
    Missing,
    /// <summary>Nothing this app opens: another scheme (ms-settings:, javascript:…) or a network share.</summary>
    Refused,
}

/// <summary>A link target, read: what it is and the address or full path to open.</summary>
public readonly record struct ChatLink(ChatLinkKind Kind, string Target);

/// <summary>
/// Links in a chat reply. The agents write local files the way their CLIs print them — `/D:/Code/x/a.png`,
/// `&lt;/D:/Code/x/a b.png&gt;`, `D:/Code/x/app.js:12`, `file:///…`, `%20` for spaces — and the old link handed that text to
/// the shell as it was, which failed without a word (owner 2026-10-29: "ทำไมลิ้งค์ในแชทกดไม่ได้ เช่น ภาพที่เอไอส่งให้").
/// Read here into a full path first. What a click may do is kept narrow: a web address opens in the browser, a picture or
/// a page in its viewer, anything else on disk is only shown in Explorer (a link to a .js, .bat or .exe must never run it),
/// and a network share or another scheme is refused (a \\host link would hand the Windows sign-in to that host).
/// </summary>
public static class ChatLinks
{
    /// <summary>The folder relative links are read against (the chat's project); set by the chat view.</summary>
    public static Func<string?>? ProjectFolder { get; set; }

    /// <summary>Opened in their own viewer: pictures, pages and documents. Everything else is revealed, not opened.</summary>
    private static readonly HashSet<string> ViewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico",
        ".html", ".htm", ".pdf", ".txt", ".md", ".log", ".csv", ".json",
        ".mp4", ".webm", ".mp3", ".wav",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    /// <summary>Is this a picture file the chat can draw (for `![alt](path)`)?</summary>
    public static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>Read a link's target. Pure apart from looking at the disk.</summary>
    public static ChatLink Resolve(string? raw, string? projectFolder = null)
    {
        string t = (raw ?? "").Trim();
        if (t.Length >= 2 && t[0] == '<' && t[^1] == '>') t = t[1..^1].Trim();   // [x](<path with spaces>)
        if (t.Length == 0) return new(ChatLinkKind.Refused, "");

        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1)   // "D:" parses as a scheme of one letter
        {
            if (uri.Scheme is "http" or "https" or "mailto") return new(ChatLinkKind.Web, uri.AbsoluteUri);
            if (uri.Scheme != "file" || uri.IsUnc || !string.IsNullOrEmpty(uri.Host)) return new(ChatLinkKind.Refused, t);
            t = uri.LocalPath;
        }
        else
        {
            try { t = Uri.UnescapeDataString(t); } catch (UriFormatException) { }
        }

        t = t.Replace('/', Path.DirectorySeparatorChar);
        if (t.Length >= 3 && t[0] == Path.DirectorySeparatorChar && char.IsLetter(t[1]) && t[2] == ':') t = t[1..];   // "\D:\x" (from /D:/x)
        if (t.StartsWith(@"\\", StringComparison.Ordinal)) return new(ChatLinkKind.Refused, t);   // a share, \\?\ or \\.\ device
        if (!Path.IsPathFullyQualified(t))
        {
            if (Path.IsPathRooted(t) || string.IsNullOrEmpty(projectFolder)) return new(ChatLinkKind.Missing, t);
            t = Path.Combine(projectFolder, t.TrimStart(Path.DirectorySeparatorChar));
        }

        string full;
        try { full = Path.GetFullPath(t); }
        catch (Exception) { return new(ChatLinkKind.Missing, t); }

        foreach (var candidate in WithoutLineSuffix(full))
        {
            if (Directory.Exists(candidate)) return new(ChatLinkKind.Folder, candidate);
            if (File.Exists(candidate))
                return new(ViewableExtensions.Contains(Path.GetExtension(candidate)) ? ChatLinkKind.ViewFile : ChatLinkKind.RevealFile, candidate);
        }
        return new(ChatLinkKind.Missing, full);
    }

    /// <summary>The path as written, then without a line number the CLIs add ("app.js:12", "app.js:12:5", "app.js#L12").</summary>
    private static IEnumerable<string> WithoutLineSuffix(string path)
    {
        yield return path;
        int hash = path.LastIndexOf("#L", StringComparison.Ordinal);
        if (hash > 2) yield return path[..hash];
        string p = path;
        for (int i = 0; i < 2; i++)
        {
            int colon = p.LastIndexOf(':');
            if (colon <= 2 || !p[(colon + 1)..].All(char.IsDigit) || colon == p.Length - 1) break;
            p = p[..colon];
            yield return p;
        }
    }

    /// <summary>Open a link the user clicked. False when there was nothing to open (missing, refused, or the shell failed).</summary>
    public static bool Open(string? raw, out ChatLink link)
    {
        link = Resolve(raw, ProjectFolder?.Invoke());
        try
        {
            switch (link.Kind)
            {
                case ChatLinkKind.Web:
                case ChatLinkKind.ViewFile:
                    Process.Start(new ProcessStartInfo(link.Target) { UseShellExecute = true })?.Dispose();
                    return true;
                case ChatLinkKind.RevealFile:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{link.Target}\"") { UseShellExecute = false })?.Dispose();
                    return true;
                case ChatLinkKind.Folder:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{link.Target}\"") { UseShellExecute = false })?.Dispose();
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception) { return false; }   // no app registered for it, or Explorer could not start
    }

    /// <summary>Say, by the pointer, why a click opened nothing — a link that does nothing is what was reported.</summary>
    internal static void ShowFailure(ChatLink link, FrameworkElement? anchor)
    {
        if (anchor == null || !anchor.IsLoaded) return;
        string key = link.Kind switch
        {
            ChatLinkKind.Missing => "chat.link.missing",
            ChatLinkKind.Refused => "chat.link.refused",
            _ => "chat.link.failed",
        };
        string target = link.Target.Length > 160 ? link.Target[..157] + "…" : link.Target;
        var tip = new ToolTip
        {
            Content = string.Format(Services.LocalizedResources.Instance[key], target),
            PlacementTarget = anchor,
            Placement = PlacementMode.Mouse,
        };
        tip.IsOpen = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        timer.Tick += (_, _) => { timer.Stop(); tip.IsOpen = false; };
        timer.Start();
    }
}

/// <summary>
/// One link or picture drawn in a reply, kept on its element (Tag). A click can arrive twice — the link's own Click and
/// the selectable block's press-release (Helpers/SelectableTextBlock, whose selection engine may take the mouse first) —
/// so it opens once.
/// </summary>
public sealed class ChatLinkTarget
{
    private long? _openedAt;

    public ChatLinkTarget(string url) => Url = url;

    public string Url { get; }

    public void Open(FrameworkElement? anchor)
    {
        long now = Environment.TickCount64;
        if (_openedAt is long then && now - then < 800) return;
        _openedAt = now;
        if (!ChatLinks.Open(Url, out var link)) ChatLinks.ShowFailure(link, anchor);
    }
}
