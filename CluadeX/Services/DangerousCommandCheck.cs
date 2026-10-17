using System.Text;

namespace CluadeX.Services;

/// <summary>
/// The known-dangerous shell command blocklist (wiping a drive, deleting recursively, downloading and running
/// code, changing accounts or ACLs). Defense-in-depth for the SHELL paths — run_command, powershell,
/// task_create, run_build / run_tests, auto-run shell code blocks and the autonomous verify command —
/// not a security boundary: the permission prompt is. It does not look at Python or JavaScript (repl, a
/// python code block); there the prompt, which shows the code, or the auto-run opt-in is the boundary.
/// One list for every caller so switching tools or languages can't step around it.
/// </summary>
public static class DangerousCommandCheck
{
    /// <summary>The shell a command is written for — it decides which lines are commands at all.</summary>
    public enum Dialect
    {
        /// <summary>Not known: the strict reading — every line, and every segment, is a command.</summary>
        Unknown,
        /// <summary>bash / sh (Git Bash): quotes and heredocs span lines; # starts a comment.</summary>
        Posix,
        /// <summary>PowerShell: quotes, here-strings (@' '@, @" "@) and &lt;# #&gt; comments span lines.</summary>
        PowerShell,
        /// <summary>cmd.exe or a .bat file: nothing spans lines and ' is not a quote — read strictly.</summary>
        Cmd,
    }

    /// <summary>The dialect of the shell <see cref="ShellRunner"/> runs a command in.</summary>
    public static Dialect DialectOf(ShellRunner.Shell shell) => shell switch
    {
        ShellRunner.Shell.Bash => Dialect.Posix,
        ShellRunner.Shell.PowerShell => Dialect.PowerShell,
        _ => Dialect.Cmd,
    };

    /// <summary>The dialect of a code block's language (see <see cref="IsShellLanguage"/>).</summary>
    public static Dialect DialectOfLanguage(string? language) => (language ?? "").Trim().ToLowerInvariant() switch
    {
        "bash" or "sh" => Dialect.Posix,
        "powershell" or "ps1" => Dialect.PowerShell,
        "cmd" or "bat" or "batch" => Dialect.Cmd,
        _ => Dialect.Unknown,
    };

    /// <summary>Languages of a code block that run as a shell script.</summary>
    public static bool IsShellLanguage(string? language) => (language ?? "").Trim().ToLowerInvariant() is
        "bash" or "sh" or "cmd" or "bat" or "batch" or "powershell" or "ps1";

    private static readonly string[] BlockedPrefixes =
    [
        "rm -rf /", "rm -rf ~", "rm -rf .",
        "format ", "format\t",
        "del /s", "del /f", "del /q",
        "rmdir /s", "rd /s",
        "shutdown", "restart-computer", "stop-computer",
        "reg delete", "reg add",
        "net user", "net localgroup",
        "takeown", "icacls",
        "mkfs.", "dd if=",
        "> /dev/", ">/dev/",
    ];

    private static readonly string[] BlockedContains =
    [
        "| rm ", "| del ", "&& rm ", "&& del ",
        "; rm ", "; del ", "` rm ", "` del ",
        "invoke-webrequest", "invoke-restmethod",
        "start-bitstransfer",
        "certutil -urlcache",
        "bitsadmin /transfer",
        // PowerShell destructive forms (piped/chained — prefix matching misses these)
        "remove-item -recurse", "remove-item -force", "-recurse -force",
        "format-volume", "clear-disk", "format-disk",
    ];

    /// <summary>Words before a command that leave it the command it is: then rm -rf ~, sudo rm -rf /.</summary>
    private static readonly string[] Leaders =
        ["then", "do", "else", "elif", "if", "while", "until", "!", "time", "sudo", "doas", "exec", "command", "builtin", "nohup"];

    /// <summary>The first characters of <see cref="BlockedPrefixes"/> and <see cref="Leaders"/>.</summary>
    private static readonly string FirstChars = new(BlockedPrefixes.Concat(Leaders).Select(w => w[0]).Distinct().ToArray());

    /// <summary>
    /// The line of <paramref name="command"/> that matches the blocklist, or null. Linear in its length.
    /// <list type="number">
    /// <item>The original check, unchanged: the whole command, trimmed and lower-cased, starting with a prefix
    /// (culture-sensitive, as it always was). It runs first, so everything it refused is still refused — the
    /// reading below can only add refusals.</item>
    /// <item>The "contains" patterns, on every line, quoted or not: a quoted string can be code.</item>
    /// <item>The prefixes wherever a COMMAND starts — the start of each line; each segment after ; &amp; &amp;&amp; | ||
    /// ( { `; inside $( … ) and `…`, also within "double quotes" (PowerShell: $( … ) in "…" and @" "@) — past a
    /// leading then / do / sudo …. Not on a line of a quoted string or a heredoc / here-string body: that is
    /// text (a commit message line "format the totals…", a python heredoc line "format = …"). Unless the text
    /// is handed to something that runs it — its pipeline runs code: bash -c "…", bash &lt;&lt;EOF, source / . ,
    /// eval, ssh, cat &lt;&lt;EOF | sh (also when the | ends a line and sh is on the next), … | iex,
    /// Invoke-Expression — then every line and segment of it is checked.</item>
    /// </list>
    /// What spans lines is read per <paramref name="dialect"/>; cmd (and an unknown shell) is read strictly —
    /// there every line is a command and ' is no quote, so "echo Don't" can never hide the lines after it. A
    /// reading that does not close (an unterminated quote, heredoc, $( … )) or is unsure falls back to the strict one.
    /// </summary>
    public static string? FindBlocked(string? command, Dialect dialect = Dialect.Unknown)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        // 1) The original (aa8726c) check of the whole command, exactly as it was.
        string original = command.ToLowerInvariant().Trim();
        foreach (var b in BlockedPrefixes)
            if (original.StartsWith(b, StringComparison.CurrentCulture)) return FirstLine(command);

        // Invariant lower-casing maps one char to one char, so indices in `lower` are indices in `command`.
        string lower = command.ToLowerInvariant();
        if (lower.Length != command.Length) return FirstLine(command);   // cannot happen; refuse rather than misread

        bool[] starts = CommandStarts(command, dialect) ?? StrictStarts(command);
        for (int lineStart = 0; lineStart <= command.Length;)
        {
            int lineEnd = command.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = command.Length;
            var line = lower.AsSpan(lineStart, lineEnd - lineStart);

            foreach (var b in BlockedContains)
                if (line.Contains(b, StringComparison.Ordinal)) return command[lineStart..lineEnd].Trim();

            for (int k = lineStart; k < lineEnd; k++)
                if (starts[k] && BlockedAt(lower, k, lineEnd)) return command[lineStart..lineEnd].Trim();
            lineStart = lineEnd + 1;
        }
        return null;
    }

    /// <summary>A delete word anywhere on the line (rm, del, erase, rmdir / rd, Remove-Item / ri, unlink).</summary>
    private static readonly System.Text.RegularExpressions.Regex DeleteWord = new(
        @"(^|[\s;&|`(])(rm|rmdir|rd|del|erase|unlink|remove-item|ri)(\.exe)?(?=\s|$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>On a blocked line, the parts of the list that are not deletes: those stay refused whatever is deleted.</summary>
    private static readonly string[] NonDeleteMarks =
    [
        "format ", "format\t", "format-volume", "clear-disk", "format-disk", "shutdown", "restart-computer",
        "stop-computer", "reg delete", "reg add", "net user", "net localgroup", "takeown", "icacls", "mkfs.",
        "dd if=", "/dev/", "invoke-webrequest", "invoke-restmethod", "start-bitstransfer", "certutil",
        "bitsadmin",
    ];

    /// <summary>
    /// The owner's rule (2026-10-18): deleting files to clean up must just run — <c>rm -rf .dart_tool build</c>,
    /// <c>rmdir /s /q .tmp</c>, <c>Remove-Item .\build -Recurse</c>, <c>del /s /q *.tmp</c>, <c>rm -rf /tmp/x</c>.
    /// So a blocked line that ONLY deletes is not this list's business: Safety.DestructiveCodeScan judges every delete
    /// by its target, and CodeRunGuard puts the dangerous ones (a drive, a home, a system folder, "everything here")
    /// to the user on the red prompt — on every path this list guards. What stays here is the rest of the list:
    /// format / disk, registry, accounts and ACLs, downloads, shutdown.
    /// </summary>
    public static bool IsDeleteOnly(string blockedLine)
    {
        if (string.IsNullOrWhiteSpace(blockedLine)) return false;
        string l = blockedLine.ToLowerInvariant();
        foreach (var m in NonDeleteMarks)
            if (l.Contains(m, StringComparison.Ordinal)) return false;
        return DeleteWord.IsMatch(blockedLine);
    }

    /// <summary><see cref="FindBlocked"/> without the delete-only lines (<see cref="IsDeleteOnly"/>): the first line that
    /// is still serious, or null. Each delete line is set aside and the rest read again, so it cannot hide a serious one
    /// after it.</summary>
    public static string? FindSerious(string? command, Dialect dialect = Dialect.Unknown)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string rest = command;
        for (int round = 0; round < 64; round++)
        {
            string? hit = FindBlocked(rest, dialect);
            if (hit == null) return null;
            if (!IsDeleteOnly(hit)) return hit;
            int at = rest.IndexOf(hit, StringComparison.Ordinal);
            if (at < 0) return hit;   // cannot set it aside exactly: the strict answer
            rest = rest.Remove(at, hit.Length);
        }
        return FirstLine(command);
    }

    /// <summary>The refusal the agent reads. "Command blocked for safety: &lt;whole command&gt;" left the model guessing
    /// which part of a long chained command tripped the list (Reverse X run, 2026-10-13: it split a working
    /// `npm pack … &amp;&amp; tar …` into pieces until one passed); naming the line and the way around ends the guessing.</summary>
    public static string BlockedMessage(string blockedLine)
    {
        string shown = blockedLine.Length > 200 ? blockedLine[..200] + "…" : blockedLine;
        return $"Command blocked for safety: `{shown}` matches CluadeX's dangerous-command list (recursive delete, "
            + "disk / registry / account changes, download cmdlets, piping into rm or del). Do that part another way: "
            + "the file tools to write or delete files, curl or python to download, or a separate command without it.";
    }

    private static string FirstLine(string command)
    {
        string t = command.Trim();
        int eol = t.IndexOf('\n');
        return (eol < 0 ? t : t[..eol]).Trim();
    }

    /// <summary>Whether the command starting at <paramref name="at"/> (lower-cased) begins with a blocked prefix.</summary>
    private static bool BlockedAt(string lower, int at, int lineEnd)
    {
        var rest = lower.AsSpan(at, lineEnd - at).TrimStart();
        if (rest.IsEmpty || !FirstChars.Contains(rest[0])) return false;   // cheap: most starts begin with nothing listed
        for (bool skipped = true; skipped;)
        {
            skipped = false;
            foreach (var w in Leaders)
                if (rest.Length > w.Length && rest.StartsWith(w, StringComparison.Ordinal) && char.IsWhiteSpace(rest[w.Length]))
                {
                    rest = rest[w.Length..].TrimStart();
                    skipped = true;
                    break;
                }
        }
        foreach (var b in BlockedPrefixes)
            if (rest.StartsWith(b, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Where commands start in <paramref name="s"/> (starts[i]: one starts at i), or null when the
    /// dialect's reading does not close.</summary>
    private static bool[]? CommandStarts(string s, Dialect dialect) => dialect switch
    {
        Dialect.Posix => PosixStarts(s),
        Dialect.PowerShell => PowerShellStarts(s),
        _ => StrictStarts(s),
    };

    /// <summary>Every line start, and every position after a separator — quotes ignored.</summary>
    private static bool[] StrictStarts(string s)
    {
        var starts = new bool[s.Length + 1];
        MarkStrict(s, starts, 0, s.Length);
        return starts;
    }

    /// <summary>Reads s[from..to) strictly: every line and every segment in it is a command.</summary>
    private static void MarkStrict(string s, bool[] starts, int from, int to)
    {
        starts[from] = true;
        for (int k = from; k < to; k++)
            if (s[k] is '\n' or ';' or '&' or '|' or '(' or '{' or '`') starts[k + 1] = true;
    }

    // ───────────────────────── what runs text as code ─────────────────────────

    /// <summary>Commands that run the text they are given: a shell, eval, source / . , ssh, su, PowerShell's
    /// iex / Invoke-Expression …</summary>
    private static readonly HashSet<string> CodeRunners = new(StringComparer.Ordinal)
    {
        "sh", "bash", "zsh", "dash", "ksh", "fish", "csh", "tcsh", "busybox", "cmd", "powershell", "pwsh",
        "eval", "source", ".", "ssh", "su", "wsl", "start",
        "iex", "invoke-expression", "icm", "invoke-command", "start-process", "saps", "start-job", "sajb",
    };

    /// <summary>Keywords after which the next word is the command: if bash &lt;&lt;EOF, then sh -c "…".</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        { "then", "do", "else", "elif", "if", "while", "until", "!", "time" };

    /// <summary>Commands that run another command named somewhere in their arguments: sudo -u root bash -c "…".</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "sudo", "doas", "gsudo", "runas", "env", "exec", "command", "builtin", "nohup", "nice", "ionice",
        "xargs", "timeout", "stdbuf", "setsid", "unbuffer", "chroot",
    };

    /// <summary>"/usr/bin/bash" → "bash", "C:\Windows\System32\cmd.exe" → "cmd".</summary>
    private static string CommandName(string word)
    {
        int slash = word.LastIndexOfAny(['/', '\\']);
        string name = (slash >= 0 && slash < word.Length - 1 ? word[(slash + 1)..] : word).ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary>X=1 / X+=1 — an assignment before the command.</summary>
    private static bool IsAssignment(string word)
    {
        if (word.Length < 2 || !(char.IsAsciiLetter(word[0]) || word[0] == '_')) return false;
        for (int k = 1; k < word.Length; k++)
        {
            char c = word[k];
            if (c == '=' || (c == '+' && k + 1 < word.Length && word[k + 1] == '=')) return true;
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        }
        return false;
    }

    // ───────────────────────── the reading ─────────────────────────

    /// <summary>Where a pipeline's first stage stands: waiting for the command word, past a wrapper (sudo …),
    /// after PowerShell's call operator (&amp; x), or decided.</summary>
    private enum Stage { Await, AfterWrapper, CallOp, Done }

    /// <summary>One pipeline (a | b | c): whether one of its stages runs code, and the text it holds — its
    /// quoted strings, heredoc bodies, $( … ) contents. If it runs code, all that text is read strictly.</summary>
    private sealed class Pipeline
    {
        public bool Code;
        public List<(int From, int To)>? Text;
        public void Add(int from, int to) { if (to > from) (Text ??= []).Add((from, to)); }
    }

    /// <summary>A nesting level of the reading: commands (the top, a ( ) / $( ) / `…` / { }), a "string",
    /// or a PowerShell @" "@ here-string. Nested by Parent — no recursion, so no depth can overflow the stack.</summary>
    private sealed class Frame
    {
        public readonly Frame? Parent;
        public readonly char Kind;          // 'c' commands · 'q' a "string" · 'h' an @" "@ here-string
        public readonly char Closer;        // 'c': '\0' the end · ')' ( or $( · '`' a backtick · 'a' $(( )) · '}' a PowerShell { }
        public readonly int ContentStart;
        public readonly int Limit;          // nothing inside an @" "@ here-string may run past its footer
        public readonly bool InArith;       // inside arithmetic: << is a shift
        public int Arith;                   // open (( of an arithmetic command or $(( ))
        public Pipeline Pipe = null!;       // 'c' frames: the pipeline being read
        public Stage Stage;
        public bool AfterPipe;              // the last thing was a | : the pipeline goes on, even past a line break

        public Frame(Frame? parent, char kind, char closer, int contentStart, List<Pipeline> all, int limit = int.MaxValue)
        {
            Parent = parent;
            Kind = kind;
            Closer = closer;
            ContentStart = contentStart;
            Limit = Math.Min(limit, parent?.Limit ?? int.MaxValue);
            InArith = parent != null && (parent.InArith || parent.Arith > 0);
            CodePipes = all;
            if (kind == 'c') NewPipeline(this, all);
        }

        /// <summary>The pipelines of this reading that run code.</summary>
        public readonly List<Pipeline> CodePipes;
    }

    private static void NewPipeline(Frame f, List<Pipeline> all)
    {
        f.Pipe = new Pipeline();
        f.Stage = Stage.Await;
        f.AfterPipe = false;
    }

    private static void MarkCode(Frame f)
    {
        if (!f.Pipe.Code) f.CodePipes.Add(f.Pipe);
        f.Pipe.Code = true;
        f.Stage = Stage.Done;
    }

    /// <summary>A frame ends at <paramref name="end"/>: what it held is text of the pipeline around it.</summary>
    private static void Close(Frame f, int end)
    {
        if (f.Parent is { Kind: 'c' } p) p.Pipe.Add(f.ContentStart, end);
    }

    /// <summary>The reading is done: the text of every pipeline that runs code is read strictly.</summary>
    private static bool[] Finish(string s, bool[] starts, List<Pipeline> all)
    {
        var code = new List<(int From, int To)>();
        foreach (var p in all)   // the pipelines that run code
            if (p.Text != null) code.AddRange(p.Text);
        code.Sort((a, b) => a.From.CompareTo(b.From));
        int reach = 0;   // everything before it is marked already: nested text is marked once
        foreach (var (from, to) in code)
        {
            starts[from] = true;
            int a = Math.Max(from, reach);
            if (to > a) MarkStrict(s, starts, a, to);
            reach = Math.Max(reach, to);
        }
        return starts;
    }

    /// <summary>IndexOfAny that remembers: asked again from a later position it reuses its last answer while
    /// that still holds — asked from left to right it reads the text once.</summary>
    private sealed class NextAny(string text, char[] any)
    {
        private int _from = int.MaxValue, _at = -1;
        public int From(int from)
        {
            if (from < _from || (_at >= 0 && _at < from))
            {
                _from = from;
                _at = from >= text.Length ? -1 : text.IndexOfAny(any, from);
            }
            return _at;
        }
    }

    // ───────────────────────── bash / sh ─────────────────────────

    private static bool[]? PosixStarts(string s)
    {
        int n = s.Length;
        var starts = new bool[n + 1];
        starts[0] = true;
        var all = new List<Pipeline>();
        var root = new Frame(null, 'c', '\0', 0, all);
        var f = root;
        var heredocs = new Queue<(string Delimiter, bool StripTabs, Pipeline Pipe)>();
        int i = 0, escaped = -1, substEnd = -1;   // escaped: the last backslash-escaped char · substEnd: just past a $( ) / `…`
        bool atWord = true;

        while (i < n)
        {
            char c = s[i];
            if (f.Kind == 'q')
            {
                // Inside "…": text — except $( … ), $(( … )) and `…`, which run.
                switch (c)
                {
                    case '\\': i += 2; continue;
                    case '"': Close(f, i); f = f.Parent!; i++; continue;
                    case '`':
                        f = new Frame(f, 'c', '`', i + 1, all);
                        starts[++i] = true;
                        atWord = true;
                        continue;
                    case '$' when i + 1 < n && s[i + 1] == '(':
                        if (i + 2 < n && s[i + 2] == '(') { f = new Frame(f, 'c', 'a', i + 3, all) { Arith = 1 }; i += 3; continue; }
                        f = new Frame(f, 'c', ')', i + 2, all);
                        i += 2;
                        starts[i] = true;
                        atWord = true;
                        continue;
                    default: i++; continue;
                }
            }

            if (c == '\\' && i + 1 < n && s[i + 1] == '\n')
            {
                // A backslash-newline: the same command line goes on (no heredoc body starts, a | still waits for
                // its command). Its next line is still checked as one — stricter.
                i += 2;
                starts[i] = true;
                continue;
            }
            if (atWord && c is not (' ' or '\t' or '\r'))
            {
                if (f.Stage != Stage.Done && c is not ('\n' or ';' or '&' or '|' or '(' or ')' or '<' or '>' or '{' or '}' or '#'))
                    ClassifyPosix(s, i, f);
                atWord = false;   // the separators below set it again
            }

            switch (c)
            {
                case '\n':
                    i++;
                    // The bodies of the heredocs opened on the line just ended, one after the other — text of
                    // the pipeline that opened them.
                    while (heredocs.Count > 0)
                    {
                        var (delimiter, stripTabs, pipe) = heredocs.Dequeue();
                        int bodyStart = i;
                        bool closed = false;
                        while (i < n && !closed)
                        {
                            int eol = s.IndexOf('\n', i);
                            if (eol < 0) eol = n;
                            var body = s.AsSpan(i, eol - i).TrimEnd('\r');
                            if (stripTabs) body = body.TrimStart('\t');
                            closed = body.SequenceEqual(delimiter);
                            if (closed) pipe.Add(bodyStart, i);
                            i = Math.Min(eol + 1, n);
                        }
                        if (!closed) return null;
                    }
                    starts[i] = true;
                    if (!f.AfterPipe) NewPipeline(f, all);   // a line that ends in | goes on in the next one
                    atWord = true;
                    continue;
                case '\\':   // escapes the next character
                    escaped = i + 1;
                    i += 2;
                    continue;
                case '\'':
                {
                    bool ansiC = i > 0 && s[i - 1] == '$' && escaped != i - 1;   // $'…' takes backslash escapes
                    int close = ansiC ? SkipEscaped(s, i + 1, '\'') : s.IndexOf('\'', i + 1);
                    if (close < 0) return null;
                    f.Pipe.Add(i + 1, close);
                    i = close + 1;
                    continue;
                }
                case '"':
                    f = new Frame(f, 'q', '\0', i + 1, all);
                    i++;
                    continue;
                case '#':   // a comment only where a word starts: echo a#b, ${#x}, $(x)#y are not
                    if (i == 0 || (escaped != i - 1 && substEnd != i
                                   && s[i - 1] is ' ' or '\t' or '\r' or '\n' or ';' or '&' or '|' or '(' or ')' or '<' or '>'))
                    {
                        int eol = s.IndexOf('\n', i);
                        if (eol < 0) eol = n;
                        if (f.Closer == '`' && s.IndexOf('`', i, eol - i) is var bt and >= 0) eol = bt;   // inside `…` it ends there
                        i = eol;
                        continue;
                    }
                    i++;
                    continue;
                case '(':
                    if (i + 1 < n && s[i + 1] == '(') { f.Arith++; i += 2; continue; }   // (( … )): arithmetic
                    f = new Frame(f, 'c', ')', i + 1, all);                                // ( … ): a subshell
                    starts[++i] = true;
                    atWord = true;
                    continue;
                case ')':
                    if (f.Arith > 0 && i + 1 < n && s[i + 1] == ')')
                    {
                        f.Arith--;
                        i += 2;
                        if (f.Closer == 'a' && f.Arith == 0) { Close(f, i - 2); f = f.Parent!; substEnd = i; }
                        continue;
                    }
                    if (f.Closer == ')') { Close(f, i); f = f.Parent!; substEnd = ++i; continue; }
                    starts[++i] = true;   // a stray ) — a case pattern's: a command follows
                    atWord = true;
                    continue;
                case '`':
                    if (f.Closer == '`') { Close(f, i); f = f.Parent!; substEnd = ++i; continue; }
                    f = new Frame(f, 'c', '`', i + 1, all);
                    starts[++i] = true;
                    atWord = true;
                    continue;
                case '$' when i + 1 < n && s[i + 1] == '(':
                    if (i + 2 < n && s[i + 2] == '(') { f = new Frame(f, 'c', 'a', i + 3, all) { Arith = 1 }; i += 3; continue; }
                    f = new Frame(f, 'c', ')', i + 2, all);
                    i += 2;
                    starts[i] = true;
                    atWord = true;
                    continue;
                case '{' when i > 0 && s[i - 1] == '$' && escaped != i - 1:   // ${var}, not a { group; }
                    i++;
                    continue;
                case '{':   // a { group; }: commands — the same pipeline (… | { sh; })
                    starts[++i] = true;
                    f.Stage = Stage.Await;
                    atWord = true;
                    continue;
                case ';' or '&':   // also && and ;; — a new pipeline
                    if (i + 1 < n && s[i + 1] == c) i++;
                    starts[++i] = true;
                    NewPipeline(f, all);
                    atWord = true;
                    continue;
                case '|':
                    if (i + 1 < n && s[i + 1] == '|') { i += 2; starts[i] = true; NewPipeline(f, all); atWord = true; continue; }
                    if (i + 1 < n && s[i + 1] == '&') i++;   // |& pipes stderr too
                    starts[++i] = true;
                    f.Stage = Stage.Await;   // the next stage of the same pipeline — perhaps on the next line
                    f.AfterPipe = true;
                    atWord = true;
                    continue;
                case '<' when i + 1 < n && s[i + 1] == '<':
                {
                    if (i + 2 < n && s[i + 2] == '<') { i += 3; continue; }   // <<< here-string: one word, no body
                    if (f.Arith > 0 || f.InArith) { i += 2; continue; }        // a shift inside (( … ))
                    int j = i + 2;
                    bool stripTabs = j < n && s[j] == '-';
                    if (stripTabs) j++;
                    while (j < n && s[j] is ' ' or '\t') j++;
                    // The delimiter word, its quotes and backslashes removed — as bash compares it.
                    var delimiter = new StringBuilder();
                    while (j < n && s[j] is not (' ' or '\t' or '\r' or '\n' or ';' or '&' or '|' or '<' or '>' or '(' or ')'))
                    {
                        char d = s[j];
                        if (d is '\'' or '"')
                        {
                            int close = s.IndexOf(d, j + 1);
                            if (close < 0) return null;
                            delimiter.Append(s, j + 1, close - j - 1);
                            j = close + 1;
                        }
                        else if (d == '\\' && j + 1 < n) { delimiter.Append(s[j + 1]); j += 2; }
                        else { delimiter.Append(d); j++; }
                    }
                    if (delimiter.Length > 0) heredocs.Enqueue((delimiter.ToString(), stripTabs, f.Pipe));
                    i = j;
                    continue;
                }
                case ' ' or '\t' or '\r':
                    atWord = true;
                    i++;
                    continue;
                default:
                    i++;
                    continue;
            }
        }
        return f == root && heredocs.Count == 0 ? Finish(s, starts, all) : null;
    }

    /// <summary>The command word of a stage, met at <paramref name="i"/>: does this pipeline run code?</summary>
    private static void ClassifyPosix(string s, int i, Frame f)
    {
        f.AfterPipe = false;
        char c = s[i];
        if (c is '"' or '\'' or '`' or '$')
        {
            // A quoted or computed command ("$SHELL" -c …, $(cat <<EOF …)): which program runs is unknown —
            // read as code. Past a wrapper it is only an argument.
            if (f.Stage == Stage.Await) MarkCode(f);
            return;
        }
        var word = new StringBuilder();
        int j = i;
        while (j < s.Length)
        {
            char d = s[j];
            if (d == '\\' && j + 1 < s.Length) { if (s[j + 1] != '\n') word.Append(s[j + 1]); j += 2; continue; }   // b\ash is bash
            if (d is ' ' or '\t' or '\r' or '\n' or ';' or '&' or '|' or '(' or ')' or '<' or '>' or '"' or '\'' or '`') break;
            word.Append(d);
            j++;
        }
        string w = word.ToString();
        if (IsAssignment(w)) return;   // X=1 cmd …: the command is the next word
        string name = CommandName(w);
        if (CodeRunners.Contains(name)) { MarkCode(f); return; }
        if (f.Stage == Stage.Await && (name.Contains('$') || (j < s.Length && s[j] is '"' or '\'' or '`')))
        {
            MarkCode(f);   // ba"sh", b$x: a command name put together at run time
            return;
        }
        if (Keywords.Contains(name)) return;
        if (Wrappers.Contains(name)) { f.Stage = Stage.AfterWrapper; return; }
        if (f.Stage != Stage.AfterWrapper) f.Stage = Stage.Done;
    }

    /// <summary>The index of the <paramref name="quote"/> that closes a string opened before
    /// <paramref name="from"/>, where a backslash escapes the next character; -1 if none does.</summary>
    private static int SkipEscaped(string s, int from, char quote)
    {
        for (int j = from; j < s.Length; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == quote) return j;
        }
        return -1;
    }

    // ───────────────────────── PowerShell ─────────────────────────

    // PowerShell's tokenizer also takes the typographic quotes as quotes — and it sees them: the powershell tool
    // passes the script as UTF-16 (-EncodedCommand), a .ps1 code block is written with a UTF-8 BOM.
    private static bool IsSingleQuote(char c) => c is '\'' or '‘' or '’' or '‚' or '‛';
    private static bool IsDoubleQuote(char c) => c is '"' or '“' or '”' or '„';

    /// <summary>What can carry a reading across a line break.</summary>
    private static readonly char[] PsSpanners = ['\'', '‘', '’', '‚', '‛', '"', '“', '”', '„', '@', '<', '{', '(', '$', '`'];

    private static bool[]? PowerShellStarts(string s)
    {
        int n = s.Length;
        var starts = new bool[n + 1];
        starts[0] = true;
        var all = new List<Pipeline>();
        var root = new Frame(null, 'c', '\0', 0, all);
        var f = root;
        var spanner = new NextAny(s, PsSpanners);
        var newline = new NextAny(s, ['\n']);
        int i = 0;
        bool atWord = true;

        while (i < n)
        {
            if (i >= f.Limit)
            {
                if (f.Kind != 'h' || i > f.Limit) return null;   // something inside the here-string ran past its end
                Close(f, i);
                i += 2;   // the footer "@
                f = f.Parent!;
                continue;
            }
            char c = s[i];
            if (f.Kind is 'q' or 'h')
            {
                // Inside "…" / @" "@: text — except $( … ), which runs.
                if (c == '`') { i += 2; continue; }
                if (f.Kind == 'q' && IsDoubleQuote(c))
                {
                    if (i + 1 < n && IsDoubleQuote(s[i + 1])) { i += 2; continue; }   // "" is an escaped quote
                    Close(f, i);
                    f = f.Parent!;
                    i++;
                    continue;
                }
                if (c == '$' && i + 1 < n && s[i + 1] == '(')
                {
                    f = new Frame(f, 'c', ')', i + 2, all);
                    i += 2;
                    starts[i] = true;
                    atWord = true;
                    continue;
                }
                if (c == '$' && i + 1 < n && s[i + 1] == '{')   // ${braced name}
                {
                    int close = s.IndexOf('}', i + 2);
                    if (close < 0) return null;
                    i = close + 1;
                    continue;
                }
                i++;
                continue;
            }

            if (c == '`' && i + 1 < n && (s[i + 1] == '\n' || (s[i + 1] == '\r' && i + 2 < n && s[i + 2] == '\n')))
            {
                // A line continuation: the same command line goes on (a | still waits for its command). Its next
                // line is still checked as one — stricter.
                i += s[i + 1] == '\n' ? 2 : 3;
                starts[i] = true;
                continue;
            }
            if (atWord && !(char.IsWhiteSpace(c) && c != '\n'))
            {
                if (f.Stage is Stage.Await or Stage.CallOp && c is not ('\n' or ';' or '&' or '|' or ')' or '}' or '#' or ',' or '<' or '>'))
                    ClassifyPs(s, i, f);
                atWord = false;   // the separators below set it again
            }

            switch (c)
            {
                case '\n':
                    starts[++i] = true;
                    // A line that ends in | goes on in the next one; so does the line before one that STARTS with |
                    // (PowerShell 7, which the powershell tool runs when it is installed).
                    if (!f.AfterPipe && !NextLineStartsWithPipe(s, i)) NewPipeline(f, all);
                    atWord = true;
                    continue;
                case '`':   // escapes the next character
                    i += 2;
                    continue;
                case '@' when i + 1 < n && (IsSingleQuote(s[i + 1]) || IsDoubleQuote(s[i + 1])) && HereStringHeader(s, i + 2) is var header and >= 0:
                {
                    // A here-string: @' or @" and nothing else on the line; it ends at a line that STARTS with '@ / "@.
                    bool single = IsSingleQuote(s[i + 1]);
                    int footer = HereStringFooter(s, header + 1, single);
                    if (footer < 0 || footer >= f.Limit) return null;
                    if (single) { f.Pipe.Add(header + 1, footer); i = footer + 2; }
                    else { f = new Frame(f, 'h', '\0', header + 1, all, footer); i = header + 1; }
                    continue;   // the rest of the footer line ('@ | iex) continues the command
                }
                case '<' when i + 1 < n && s[i + 1] == '#':   // <# block comment #>
                {
                    int end = s.IndexOf("#>", i + 2, StringComparison.Ordinal);
                    if (end < 0) return null;
                    i = end + 2;
                    continue;
                }
                case '#':
                    if (i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] is ';' or '(' or ')' or '{' or '}' or '|' or '&' or ',')
                    {
                        int eol = s.IndexOf('\n', i);
                        i = eol < 0 ? n : eol;
                        continue;
                    }
                    // Right after a word, PowerShell decides by mode: in an expression ($x = 5#…, $x=#…) it starts a
                    // comment, in a command's argument (a#b, http://x/#y) it is part of the word. Harmless while
                    // nothing on the rest of the line can carry a reading past it; otherwise read strictly.
                    if (spanner.From(i + 1) is var sp and >= 0 && (newline.From(i + 1) is var eol2 && (eol2 < 0 || sp < eol2)))
                        return null;
                    i++;
                    continue;
                case '$' when i + 1 < n && s[i + 1] == '{':   // ${braced name}
                {
                    int close = s.IndexOf('}', i + 2);
                    if (close < 0) return null;
                    i = close + 1;
                    continue;
                }
                case '$' or '@' when i + 1 < n && s[i + 1] == '(':   // $( … ), @( … )
                    f = new Frame(f, 'c', ')', i + 2, all);
                    i += 2;
                    starts[i] = true;
                    atWord = true;
                    continue;
                case '@' when i + 1 < n && s[i + 1] == '{':   // @{ hashtable }
                    f = new Frame(f, 'c', '}', i + 2, all);
                    i += 2;
                    atWord = true;
                    continue;
                case '(':
                    f = new Frame(f, 'c', ')', i + 1, all);
                    starts[++i] = true;
                    atWord = true;
                    continue;
                case '{':
                    f = new Frame(f, 'c', '}', i + 1, all);
                    starts[++i] = true;
                    atWord = true;
                    continue;
                case ')' or '}':
                    if (f.Closer == c) { Close(f, i); f = f.Parent!; }
                    i++;
                    continue;
                case ';':
                    starts[++i] = true;
                    NewPipeline(f, all);
                    atWord = true;
                    continue;
                case '&':
                    if (i + 1 < n && s[i + 1] == '&') { i += 2; starts[i] = true; NewPipeline(f, all); atWord = true; continue; }
                    starts[++i] = true;
                    if (f.Stage == Stage.Await) f.Stage = Stage.CallOp;   // & x — the call operator
                    else NewPipeline(f, all);                              // x & — a background job
                    atWord = true;
                    continue;
                case '|':
                    if (i + 1 < n && s[i + 1] == '|') { i += 2; starts[i] = true; NewPipeline(f, all); atWord = true; continue; }
                    starts[++i] = true;
                    f.Stage = Stage.Await;   // the next stage of the same pipeline — perhaps on the next line
                    f.AfterPipe = true;
                    atWord = true;
                    continue;
                default:
                    if (char.IsWhiteSpace(c)) atWord = true;
                    else if (IsSingleQuote(c))   // to the closing quote; a doubled quote is an escaped one
                    {
                        int j = i + 1;
                        while (j < n && !(IsSingleQuote(s[j]) && !(j + 1 < n && IsSingleQuote(s[j + 1]))))
                            j += IsSingleQuote(s[j]) ? 2 : 1;
                        if (j >= n) return null;
                        f.Pipe.Add(i + 1, j);
                        i = j + 1;
                        continue;
                    }
                    else if (IsDoubleQuote(c))
                    {
                        f = new Frame(f, 'q', '\0', i + 1, all);
                        i++;
                        continue;
                    }
                    i++;
                    continue;
            }
        }
        return f == root ? Finish(s, starts, all) : null;
    }

    private static bool NextLineStartsWithPipe(string s, int lineStart)
    {
        int j = lineStart;
        while (j < s.Length && s[j] is ' ' or '\t' or '\r') j++;
        return j < s.Length && s[j] == '|' && !(j + 1 < s.Length && s[j + 1] == '|');
    }

    /// <summary>The line break that ends a here-string header (only blanks after @' / @"), or -1.</summary>
    private static int HereStringHeader(string s, int from)
    {
        int j = from;
        while (j < s.Length && s[j] is ' ' or '\t' or '\r') j++;
        return j < s.Length && s[j] == '\n' ? j : -1;
    }

    /// <summary>The footer of a here-string whose body starts at <paramref name="from"/>: the first line that
    /// STARTS with '@ (or "@) — PowerShell 5.1 takes nothing else — or -1.</summary>
    private static int HereStringFooter(string s, int from, bool single)
    {
        for (int j = from; j < s.Length;)
        {
            if (j + 1 < s.Length && (single ? IsSingleQuote(s[j]) : IsDoubleQuote(s[j])) && s[j + 1] == '@') return j;
            int eol = s.IndexOf('\n', j);
            if (eol < 0) break;
            j = eol + 1;
        }
        return -1;
    }

    /// <summary>The command word of a PowerShell stage, met at <paramref name="i"/>: does this pipeline run code?</summary>
    private static void ClassifyPs(string s, int i, Frame f)
    {
        f.AfterPipe = false;
        char c = s[i];
        if (f.Stage == Stage.CallOp)
        {
            // & x: x runs. A string, a variable, a script block: which code is unknown — read as code.
            if (IsSingleQuote(c) || IsDoubleQuote(c) || c is '$' or '(' or '{' or '@' or '[') MarkCode(f);
            else if (CodeRunners.Contains(CommandName(PsWord(s, i)))) MarkCode(f);
            else f.Stage = Stage.Done;
            return;
        }
        // A value — "…", @'…'@, @( ), ( ) — is not a command; a later stage (… | iex) decides.
        if (IsSingleQuote(c) || IsDoubleQuote(c) || c is '@' or '(') { f.Stage = Stage.Done; return; }
        string word = PsWord(s, i);
        string lw = word.ToLowerInvariant();
        if (c is '$' or '[')
        {
            // An expression: it runs code only through [scriptblock]::Create(…) / $ExecutionContext…InvokeScript(…).
            if (lw.Contains("scriptblock") || lw.Contains("invokescript")) MarkCode(f);
            else f.Stage = Stage.Done;
            return;
        }
        if (CodeRunners.Contains(CommandName(word)) || lw.Contains("scriptblock")) MarkCode(f);
        else f.Stage = Stage.Done;
    }

    /// <summary>The bare word at <paramref name="i"/>, backtick escapes resolved (i`ex is iex).</summary>
    private static string PsWord(string s, int i)
    {
        var word = new StringBuilder();
        for (int j = i; j < s.Length; j++)
        {
            char d = s[j];
            if (d == '`' && j + 1 < s.Length) { if (s[j + 1] is not ('\n' or '\r')) word.Append(s[j + 1]); j++; continue; }
            if (char.IsWhiteSpace(d) || d is ';' or '&' or '|' or '(' or ')' or '{' or '}' or ',' or '<' or '>' || IsSingleQuote(d) || IsDoubleQuote(d)) break;
            word.Append(d);
        }
        return word.ToString();
    }
}
