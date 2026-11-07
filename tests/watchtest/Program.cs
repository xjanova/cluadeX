// watchtest — watch_ci / watch_process (Services/Watch) against the built CluadeX.dll, offline: gh's output is parsed from
// recorded shapes, CI is a scripted probe, processes are real (BackgroundProcessService runs `cmd`), the tools run through
// the real EngineeringToolModule. No network, no GitHub, no model.
//
// Build CluadeX first (the app may be running: build it with -p:OutDir=<dir>\), then:
//   dotnet run --project tests/watchtest [-p:CluadeXBin=<dir>\]

using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Background;
using CluadeX.Services.Tools;
using CluadeX.Services.Watch;

int pass = 0, fail = 0;
void Check(string id, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"PASS {id}"); }
    else { fail++; Console.WriteLine($"FAIL {id} {detail}"); }
}

string root = Path.Combine(Path.GetTempPath(), "cx-watchtest-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
async Task<bool> Until(Func<bool> cond, int ms = 8000)
{
    var end = DateTime.Now.AddMilliseconds(ms);
    while (DateTime.Now < end) { if (cond()) return true; await Task.Delay(25); }
    return cond();
}
RunWatchService Svc(ScriptProbe? probe = null, string? dir = null, bool resume = true)
{
    var s = new RunWatchService(dir ?? Path.Combine(root, Guid.NewGuid().ToString("N")[..6]), null, null, probe ?? new ScriptProbe(), resume)
    {
        CiInterval = _ => TimeSpan.FromMilliseconds(30),
        NoChecksGrace = TimeSpan.FromMilliseconds(300),
        MinTimeout = TimeSpan.FromMilliseconds(50),
    };
    return s;
}
CiCheck C(string name, string bucket, string? link = null) => new(name, bucket, link);

try
{
    // ─── P: gh's output ───
    {
        var json = """
            [{"name":"build","state":"SUCCESS","bucket":"pass","link":"https://github.com/o/r/actions/runs/1/job/2","workflow":"CI"},
             {"name":"test","state":"FAILURE","bucket":"fail","link":"https://github.com/o/r/actions/runs/1/job/3","workflow":"CI"},
             {"name":"lint","state":"IN_PROGRESS","bucket":"pending","link":"","workflow":"CI"},
             {"name":"docs","state":"SKIPPED","bucket":"skipping","link":"","workflow":"Docs"},
             {"name":"old","state":"CANCELLED","link":""}]
            """;
        var c = RunWatchService.ParsePrChecksJson(json);
        Check("P1 pr checks JSON: five checks, buckets as gh says",
            c.Count == 5 && c[0].Bucket == "pass" && c[1].Bucket == "fail" && c[2].Bucket == "pending" && c[3].Bucket == "skipping",
            string.Join(",", c.Select(x => x.Bucket)));
        Check("P2 …a check with no bucket is judged by its state (CANCELLED → cancel)", c[4].Bucket == "cancel", c[4].Bucket);
        Check("P3 …links and workflows kept, an empty link is null", c[1].Link!.EndsWith("/job/3") && c[2].Link == null && c[3].Workflow == "Docs");

        var text = "build\tpass\t1m2s\thttps://github.com/o/r/actions/runs/1\ntest\tfail\t30s\thttps://x/2\nlint\tpending\t0\thttps://x/3\n";
        var t = RunWatchService.ParsePrChecksText(text);
        Check("P4 the plain listing of an older gh parses too",
            t.Count == 3 && t[0].Bucket == "pass" && t[1].Bucket == "fail" && t[2].Bucket == "pending" && t[0].Link!.StartsWith("https://"),
            string.Join(",", t.Select(x => x.Name + ":" + x.Bucket)));

        var runs = """
            [{"databaseId":9,"name":"push","workflowName":"CI","status":"completed","conclusion":"failure","url":"https://u/9","headSha":"bbb"},
             {"databaseId":8,"name":"push","workflowName":"Release","status":"in_progress","conclusion":"","url":"https://u/8","headSha":"bbb"},
             {"databaseId":7,"name":"push","workflowName":"CI","status":"completed","conclusion":"success","url":"https://u/7","headSha":"aaa"}]
            """;
        var newest = RunWatchService.ParseRunListJson(runs, newestCommitOnly: true);
        Check("P5 a branch's runs: only its newest commit's (an older green run does not count)",
            newest.Count == 2 && newest.All(x => x.Link != "https://u/7") && newest[0].Bucket == "fail" && newest[1].Bucket == "pending",
            string.Join(",", newest.Select(x => x.Name + ":" + x.Bucket)));
        var all = RunWatchService.ParseRunListJson(runs, newestCommitOnly: false);
        Check("P6 a commit's runs: every one listed", all.Count == 3 && all[2].Bucket == "pass");
        var conc = RunWatchService.ParseRunListJson("""
            [{"status":"completed","conclusion":"skipped","workflowName":"a","headSha":"x"},
             {"status":"completed","conclusion":"cancelled","workflowName":"b","headSha":"x"},
             {"status":"completed","conclusion":"timed_out","workflowName":"c","headSha":"x"},
             {"status":"queued","conclusion":"","workflowName":"d","headSha":"x"}]
            """, false);
        Check("P7 conclusions: skipped → skipping, cancelled → cancel, timed_out → fail, queued → pending",
            string.Join(",", conc.Select(x => x.Bucket)) == "skipping,cancel,fail,pending", string.Join(",", conc.Select(x => x.Bucket)));
        Check("P8 not an array = nothing (no exception)", RunWatchService.ParsePrChecksJson("{\"a\":1}").Count == 0);
    }

    // ─── E: the verdict ───
    {
        Check("E1 nothing yet = still watching", RunWatchService.EvaluateCi(Array.Empty<CiCheck>()).Done == null);
        var (d1, l1) = RunWatchService.EvaluateCi(new[] { C("a", "pass"), C("b", "pending") });
        Check("E2 a check still running = still watching, the line counts it", d1 == null && l1.Contains("1/2") && l1.Contains("1 running"), l1);
        var (d2, l2) = RunWatchService.EvaluateCi(new[] { C("a", "pass"), C("b", "skipping") });
        Check("E3 all passed (a skipped one counts as fine) = passed", d2 == WatchState.Passed && l2.Contains("2 checks"), l2);
        var (d3, l3) = RunWatchService.EvaluateCi(new[] { C("build", "fail"), C("b", "pass"), C("c", "cancel") });
        Check("E4 a failed or cancelled check = failed, named", d3 == WatchState.Failed && l3.Contains("build") && l3.Contains("2 of 3"), l3);
        var (d4, l4) = RunWatchService.EvaluateCi(new[] { C("build", "fail"), C("b", "pending") });
        Check("E5 failed early but one still runs = still watching (all of it reported at the end), the line says so",
            d4 == null && l4.Contains("failed already"), l4);
        string detail = RunWatchService.DescribeChecks(new[] { C("ok", "pass"), C("bad", "fail", "https://l") });
        Check("E6 the detail lists failures first, with their links", detail.StartsWith("✗ bad") && detail.Contains("https://l"), detail);
    }

    // ─── A: what gh may be handed ───
    {
        Check("A1 a PR number, a PR URL, a branch, a sha, a repo pass",
            GhCiProbe.CheckArgs("o/r", "12", null, null) == null && GhCiProbe.CheckArgs(null, "https://github.com/o/r/pull/3", null, null) == null
            && GhCiProbe.CheckArgs(null, null, "feat/x-1", null) == null && GhCiProbe.CheckArgs(null, null, null, "1a2b3c4") == null);
        Check("A2 an option smuggled as pr is refused (--repo=evil)", GhCiProbe.CheckArgs(null, "--repo=evil/x", null, null) != null);
        Check("A3 …as a branch (-R, --upload-pack, a .. path)", GhCiProbe.CheckArgs(null, null, "-R", null) != null
            && GhCiProbe.CheckArgs(null, null, "--upload-pack=x", null) != null && GhCiProbe.CheckArgs(null, null, "a/../b", null) != null);
        Check("A4 a commit that is not hex, a repo with a space or a shell char", GhCiProbe.CheckArgs(null, null, null, "HEAD;rm") != null
            && GhCiProbe.CheckArgs("o r/x", null, null, null) != null && GhCiProbe.CheckArgs("o/r&calc", null, null, null) != null);
        Check("A5 two targets at once are refused", GhCiProbe.CheckArgs(null, "12", "main", null) != null);
    }

    // ─── D: dry-run (this harness is not CluadeX.exe, so it is on): gh is never started for a watch ───
    {
        Check("D1 dry-run is on in a harness, and blocks gh", CluadeX.Services.Safety.ProcessLaunchPolicy.DryRun && GhCiProbe.GhBlocked);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dry = await new GhCiProbe().ProbeAsync(new WatchJob { Kind = WatchKind.Ci, Cwd = Environment.CurrentDirectory, Repo = "xjanova/cluadeX", Pr = "91" }, default);
        Check("D2 the real probe under dry-run starts no gh: a fatal [dry-run] answer at once (the watch ends saying so)",
            dry.Fatal && dry.Error!.StartsWith("[dry-run]") && dry.Checks.Count == 0 && sw.ElapsedMilliseconds < 1000, dry.Error + $" {sw.ElapsedMilliseconds} ms");
        var badDry = await new GhCiProbe().ProbeAsync(new WatchJob { Kind = WatchKind.Ci, Cwd = Environment.CurrentDirectory, Pr = "--repo=x/y" }, default);
        Check("D3 …the argument check still comes first", badDry.Fatal && badDry.Error!.StartsWith("pr must be"), badDry.Error);
    }

    // ─── S: the service, CI scripted ───
    {
        var probe = new ScriptProbe();
        probe.Enqueue(new CiProbeResult(new[] { C("build", "pending") }));
        probe.Enqueue(new CiProbeResult(new[] { C("build", "pending"), C("test", "pass") }));
        probe.Enqueue(new CiProbeResult(new[] { C("build", "pass"), C("test", "pass") }));
        var svc = Svc(probe);
        int started = 0, finished = 0, changed = 0;
        svc.Started += _ => started++;
        svc.Finished += _ => finished++;
        svc.Changed += _ => Interlocked.Increment(ref changed);
        var r = svc.StartCi("chat-1", root, null, "12", null, null, "merge it", null);
        Check("S1 a watch starts: an id, the target as people read it, a deadline an hour away",
            r.Job is { IsActive: true } j && j.Id.StartsWith('w') && j.Target == "PR #12" && (j.Deadline - j.CreatedAt).TotalMinutes is > 59 and < 61,
            r.Error ?? r.Job?.Target);
        Check("S2 …and says so once", started == 1);
        await Until(() => finished > 0);
        var job = r.Job!;
        Check("S3 pending, pending, green = passed, after three looks", job.State == WatchState.Passed && job.Polls == 3, $"{job.State} {job.Polls}");
        Check("S4 Finished is raised exactly once (and Changed along the way)", finished == 1 && changed >= 2, $"{finished} {changed}");
        await Task.Delay(150);
        Check("S5 no look after the end", probe.Calls == 3, probe.Calls.ToString());
        string report = RunWatchService.Report(job);
        Check("S6 the model's report: id, target, passed, the plan it gave", report.Contains(job.Id) && report.Contains("PR #12")
            && report.Contains("passed") && report.Contains("You planned: merge it"), report);

        // failed
        var p2 = new ScriptProbe();
        p2.Enqueue(new CiProbeResult(new[] { C("build", "fail", "https://github.com/o/r/actions/runs/77/job/1"), C("test", "pass") }));
        var s2 = Svc(p2);
        var j2 = s2.StartCi("chat-1", root, null, null, null, "abcdef1", null, null).Job!;
        await Until(() => !j2.IsActive);
        Check("S7 a failed check = failed; the summary names it", j2.State == WatchState.Failed && j2.Summary.Contains("build"), j2.Summary);
        Check("S8 …the report tells the model to read the run's log, with the link", RunWatchService.Report(j2).Contains("gh_run_log")
            && j2.Detail.Contains("/runs/77/"), RunWatchService.Report(j2));
        Check("S9 a commit target reads as its short hash", j2.Target == "commit abcdef1", j2.Target);

        // no checks at all
        var p3 = new ScriptProbe { Default = new CiProbeResult(Array.Empty<CiCheck>()) };
        var s3 = Svc(p3);
        var j3 = s3.StartCi("chat-1", root, null, null, "main", null, null, null).Job!;
        await Until(() => !j3.IsActive);
        Check("S10 no checks ever = ended after the grace, saying so", j3.State == WatchState.Ended && j3.Summary.Contains("no CI checks"), j3.Summary);

        // checks that appear late
        var p4 = new ScriptProbe();
        p4.Enqueue(new CiProbeResult(Array.Empty<CiCheck>()));
        p4.Enqueue(new CiProbeResult(Array.Empty<CiCheck>()));
        p4.Enqueue(new CiProbeResult(new[] { C("ci", "pass") }));
        var s4 = Svc(p4);
        var j4 = s4.StartCi("chat-1", root, null, "5", null, null, null, null).Job!;
        await Until(() => !j4.IsActive);
        Check("S11 checks that show up a moment after the push are waited for", j4.State == WatchState.Passed, j4.State.ToString());

        // errors
        var p5 = new ScriptProbe();
        p5.Enqueue(new CiProbeResult(Array.Empty<CiCheck>(), "gh is not signed in to GitHub", Fatal: true));
        var s5 = Svc(p5);
        var j5 = s5.StartCi("chat-1", root, null, "5", null, null, null, null).Job!;
        await Until(() => !j5.IsActive);
        Check("S12 a fatal answer (gh signed out) ends at once as an error", j5.State == WatchState.Error && p5.Calls == 1 && j5.Summary.Contains("signed in"),
            $"{j5.State} {p5.Calls}");
        var p6 = new ScriptProbe();
        for (int i = 0; i < 4; i++) p6.Enqueue(new CiProbeResult(Array.Empty<CiCheck>(), "gh did not answer within 30 s"));
        p6.Enqueue(new CiProbeResult(new[] { C("ci", "pass") }));
        var s6 = Svc(p6);
        var j6 = s6.StartCi("chat-1", root, null, "5", null, null, null, null).Job!;
        await Until(() => !j6.IsActive);
        Check("S13 four slow answers then green = passed (a slow network is not the end)", j6.State == WatchState.Passed, j6.State + " " + j6.Summary);
        var p7 = new ScriptProbe { Default = new CiProbeResult(Array.Empty<CiCheck>(), "gh did not answer within 30 s") };
        var s7 = Svc(p7);
        var j7 = s7.StartCi("chat-1", root, null, "5", null, null, null, null).Job!;
        await Until(() => !j7.IsActive);
        Check("S14 five failures in a row = error", j7.State == WatchState.Error && p7.Calls == 5, $"{j7.State} {p7.Calls}");
        var p8 = new ScriptProbe { Default = new CiProbeResult(Array.Empty<CiCheck>()), Throw = true };
        var s8 = Svc(p8);
        var j8 = s8.StartCi("chat-1", root, null, "5", null, null, null, null).Job!;
        await Until(() => !j8.IsActive);
        Check("S15 a probe that throws is an error answer, not a dead loop", j8.State == WatchState.Error, j8.State.ToString());

        // timeout
        var p9 = new ScriptProbe { Default = new CiProbeResult(new[] { C("slow", "pending") }) };
        var s9 = Svc(p9);
        var j9 = s9.StartCi("chat-1", root, null, "9", null, null, null, TimeSpan.FromMilliseconds(200)).Job!;
        await Until(() => !j9.IsActive);
        Check("S16 still running at the deadline = timed out", j9.State == WatchState.TimedOut && j9.Summary.Contains("still not finished"), j9.Summary);
        Check("S17 the timeout's report says to look once and decide", RunWatchService.Report(j9).Contains("look at it once"));

        // cancel
        var p10 = new ScriptProbe { Default = new CiProbeResult(new[] { C("slow", "pending") }) };
        var s10 = Svc(p10);
        int fin10 = 0;
        s10.Finished += _ => fin10++;
        var j10 = s10.StartCi("chat-A", root, null, "10", null, null, null, null).Job!;
        await Until(() => p10.Calls >= 1);
        Check("S18 another chat cannot cancel this chat's watch", !s10.Cancel(j10.Id, "chat-B") && j10.IsActive);
        Check("S19 its own chat can", s10.Cancel(j10.Id, "chat-A") && j10.State == WatchState.Cancelled);
        int callsAtCancel = p10.Calls;
        await Task.Delay(200);
        Check("S20 a cancelled watch stops looking and tells nobody", p10.Calls <= callsAtCancel + 1 && fin10 == 0, $"{p10.Calls} {fin10}");
        Check("S21 cancelling twice is a no", !s10.Cancel(j10.Id));
    }

    // ─── L: limits ───
    {
        var probe = new ScriptProbe { Default = new CiProbeResult(new[] { C("slow", "pending") }) };
        var svc = Svc(probe);
        var a = svc.StartCi("c", root, null, "1", null, null, "first", null);
        var again = svc.StartCi("c", root, null, "1", null, null, "second plan", null);
        Check("L1 the same target twice in a chat = the watch already there, its plan updated",
            again.Existing && ReferenceEquals(again.Job, a.Job) && a.Job!.Note == "second plan");
        Check("L2 …in another chat it is a watch of its own", svc.StartCi("d", root, null, "1", null, null, null, null).Job != a.Job);
        svc.StartCi("c", root, null, "2", null, null, null, null);
        svc.StartCi("c", root, null, "3", null, null, null, null);
        var fourth = svc.StartCi("c", root, null, "4", null, null, null, null);
        Check("L3 a fourth watch in one chat is refused, saying how many", fourth.Job == null && fourth.Error!.Contains("3"), fourth.Error);
        for (int i = 0; i < 20; i++) svc.StartCi("x" + i, root, null, "1", null, null, null, null);
        Check("L4 at most 12 watching across chats", svc.Jobs.Count(j => j.IsActive) == RunWatchService.MaxTotal, svc.Jobs.Count(j => j.IsActive).ToString());
        var big = svc.StartCi("y", root, null, "1", null, null, new string('n', 2000), null);
        Check("L5 a long plan is cut to 500 chars", big.Job == null || big.Job.Note!.Length <= RunWatchService.MaxNoteChars + 1);
        Check("L6 a watch with no chat is refused", svc.StartCi("", root, null, "1", null, null, null, null).Job == null);
        var hours = Svc(probe).StartCi("z", root, null, "1", null, null, null, TimeSpan.FromDays(3)).Job!;
        Check("L7 a 3-day timeout is held to 6 h", (hours.Deadline - hours.CreatedAt).TotalHours is > 5.9 and < 6.1);
        Check("L8 CancelChat / CancelAll end every watch they name", svc.CancelChat("c") == 3 && svc.CancelAll() > 0 && svc.Jobs.All(j => !j.IsActive));

        Check("W1 a chat may be woken MaxWakes times in the window…", Enumerable.Range(0, svc.MaxWakes).All(_ => svc.TryTakeWake("w")));
        Check("W2 …then not", !svc.TryTakeWake("w"));
        Check("W3 another chat has its own count", svc.TryTakeWake("w2"));
        svc.OwnerSpoke("w");
        Check("W4 the owner writing in the chat resets it", svc.TryTakeWake("w"));
        svc.WakeWindow = TimeSpan.FromMilliseconds(100);
        for (int i = 0; i < 10; i++) svc.TryTakeWake("w3");
        await Task.Delay(150);
        Check("W5 wakes older than the window no longer count", svc.TryTakeWake("w3"));
    }

    // ─── F: the file ───
    {
        string dir = Path.Combine(root, "persist");
        var slow = new ScriptProbe { Default = new CiProbeResult(new[] { C("slow", "pending") }) };
        var a = Svc(slow, dir);
        var ci = a.StartCi("chat-p", root, "o/r", "42", null, null, "then merge", null).Job!;
        var fresh = new ScriptProbe();
        fresh.Enqueue(new CiProbeResult(new[] { C("ci", "pass") }));
        var b = Svc(fresh, dir);
        var back = b.Get(ci.Id);
        Check("F1 a CI watch the last app run left is picked up again (same id, target, plan)",
            back is { IsActive: true, Repo: "o/r", Pr: "42", Note: "then merge" }, back?.State.ToString() ?? "(missing)");
        await Until(() => back is { IsActive: false });
        Check("F2 …and goes on to its end in the new run", back!.State == WatchState.Passed, back.State.ToString());
        a.CancelAll();

        string dir2 = Path.Combine(root, "persist2");
        File.WriteAllText(Path.Combine(dir2.Length > 0 ? Directory.CreateDirectory(dir2).FullName : dir2, "watch-jobs.json"), JsonSerializer.Serialize(new object[]
        {
            new { Id = "wproc1", ChatId = "c", Kind = "Process", ProcessId = 3, Target = "bg#3", State = "Watching", Deadline = DateTime.Now.AddHours(1), CreatedAt = DateTime.Now, Cwd = root },
            new { Id = "wold01", ChatId = "c", Kind = "Ci", Pr = "1", Target = "PR #1", State = "Watching", Deadline = DateTime.Now.AddMinutes(-5), CreatedAt = DateTime.Now.AddHours(-2), Cwd = root },
            new { Id = "wdone1", ChatId = "c", Kind = "Ci", Pr = "2", Target = "PR #2", State = "Passed", Summary = "passed", Deadline = DateTime.Now, CreatedAt = DateTime.Now, Cwd = root },
        }));
        var probe3 = new ScriptProbe();
        var c = Svc(probe3, dir2);
        Check("F3 a process watch cannot survive a restart: closed, saying why", c.Get("wproc1") is { State: WatchState.Cancelled } p && p.Summary.Contains("restarted"));
        Check("F4 a CI watch whose time ran out while closed is closed", c.Get("wold01") is { State: WatchState.Cancelled } o && o.Summary.Contains("time ran out"));
        Check("F5 a finished one stays for its card", c.Get("wdone1") is { State: WatchState.Passed });
        await Task.Delay(150);
        Check("F6 none of them is looked at again", probe3.Calls == 0, probe3.Calls.ToString());
        File.WriteAllText(Path.Combine(dir2, "watch-jobs.json"), "{ not json");
        Check("F7 an unreadable file = start empty, no exception", Svc(null, dir2).Jobs.Count == 0);
    }

    // ─── B: background processes, for real ───
    var bg = new BackgroundProcessService(null, null, Path.Combine(root, "bglogs"));
    {
        var svc = new RunWatchService(Path.Combine(root, "bgw"), bg, null, new ScriptProbe())
        { ProcessInterval = TimeSpan.FromMilliseconds(50), MinTimeout = TimeSpan.FromMilliseconds(50) };
        var failing = await bg.StartAsync(new BgStartRequest
        {
            Command = "ping -n 2 127.0.0.1 >nul & echo boom-line & exit /b 3", Cwd = root, Name = "failing", Shell = ShellRunner.Shell.Cmd,
            WaitForReady = false,
        });
        int id = failing.Process!.Id;
        var w = svc.StartProcess("chat-bg", id, "fix it", null);
        Check("B1 a running process can be watched; the target names it", w.Job is { IsActive: true } && w.Job.Target.StartsWith($"bg#{id}"), w.Error ?? w.Job?.Target);
        Check("B2 …and the service knows it is watched (the plain 'ended' notice steps aside)", svc.WatchesProcess(id));
        await Until(() => !w.Job!.IsActive, 15000);
        Check("B3 exit 3 = failed, 'exit 3'", w.Job!.State == WatchState.Failed && w.Job.Summary == "exit 3", w.Job.State + " " + w.Job.Summary);
        Check("B4 the report carries its last lines and its log", w.Job.Detail.Contains("boom-line") && w.Job.Detail.Contains("Log:"), w.Job.Detail);
        var ok = await bg.StartAsync(new BgStartRequest { Command = "echo fine", Cwd = root, Name = "ok", Shell = ShellRunner.Shell.Cmd, WaitForReady = false });
        await Until(() => bg.Get(ok.Process!.Id) is { IsRunning: false });
        var w2 = svc.StartProcess("chat-bg", ok.Process!.Id, null, null);
        await Until(() => !w2.Job!.IsActive);
        Check("B5 a process that already ended exit 0 = passed at the first look", w2.Job!.State == WatchState.Passed, w2.Job.State.ToString());
        Check("B6 an id that is not there is refused, naming bg_list", svc.StartProcess("chat-bg", 9999, null, null) is { Job: null } r && r.Error!.Contains("bg_list"));
        var sleeper = await bg.StartAsync(new BgStartRequest { Command = "ping -n 30 127.0.0.1 >nul", Cwd = root, Name = "sleeper", Shell = ShellRunner.Shell.Cmd, WaitForReady = false });
        var w3 = svc.StartProcess("chat-bg", sleeper.Process!.Id, null, TimeSpan.FromMilliseconds(300));
        await Until(() => !w3.Job!.IsActive);
        Check("B7 still running at the deadline = timed out (the process is left running)", w3.Job!.State == WatchState.TimedOut
            && bg.Get(sleeper.Process.Id)!.IsRunning, w3.Job.State.ToString());
        var sleeper2 = await bg.StartAsync(new BgStartRequest { Command = "ping -n 30 127.0.0.1 >nul", Cwd = root, Name = "sleeper2", Shell = ShellRunner.Shell.Cmd, WaitForReady = false });
        var w4 = svc.StartProcess("chat-bg", sleeper2.Process!.Id, null, null);
        await bg.StopAsync(sleeper2.Process.Id, TimeSpan.FromSeconds(3));
        await Until(() => !w4.Job!.IsActive);
        Check("B8 a process someone stopped = ended (not failed)", w4.Job!.State == WatchState.Ended && w4.Job.Summary == "stopped", w4.Job.State + " " + w4.Job.Summary);
        await bg.StopAllAsync(TimeSpan.FromSeconds(3));

        // ─── T: the tools, through the real module ───
        RunWatchService.Current = svc;
        var module = new EngineeringToolModule(() => null, null, null, null, null, null, null);
        var ctx = new ToolModuleContext { SessionKey = "chat-tool", WorkingDirectory = root };
        ToolCall Call(string name, params (string K, string V)[] args)
        {
            var c = new ToolCall { Type = module.ResolveToolType(name)!.Value, ToolName = name };
            foreach (var (k, v) in args) c.Arguments[k] = v;
            return c;
        }
        AgentRunScope.Enter(new AgentRunScope("chat-tool", root));
        var run = await bg.StartAsync(new BgStartRequest { Command = "ping -n 2 127.0.0.1 >nul", Cwd = root, Name = "tooljob", Shell = ShellRunner.Shell.Cmd, WaitForReady = false });
        var started = await module.ExecuteAsync(Call("watch_process", ("process_id", $"bg#{run.Process!.Id}"), ("note", "then report")), ctx, default);
        Check("T1 watch_process starts a watch for the calling chat, and tells the model to end its turn",
            started.Success && started.Output.Contains("End your turn") && svc.ActiveFor("chat-tool").Count == 1, started.Output + started.Error);
        var again = await module.ExecuteAsync(Call("watch_process", ("process_id", run.Process.Id.ToString())), ctx, default);
        Check("T2 the same process again: 'Already watching', no second watch", again.Success && again.Output.StartsWith("Already watching")
            && svc.ActiveFor("chat-tool").Count == 1, again.Output);
        var list = await module.ExecuteAsync(Call("watch_list"), ctx, default);
        Check("T3 watch_list shows it, watching", list.Success && list.Output.Contains("watching") && list.Output.Contains("tooljob"), list.Output);
        var badId = await module.ExecuteAsync(Call("watch_process", ("process_id", "rm -rf")), ctx, default);
        Check("T4 a process_id that is not a number is refused", !badId.Success && badId.Error!.Contains("bg#3"), badId.Error);
        var inject = await module.ExecuteAsync(Call("watch_ci", ("pr", "--repo=evil/x")), ctx, default);
        Check("T5 watch_ci refuses an option smuggled as pr, before gh is run", !inject.Success && inject.Error!.Contains("pr must be"), inject.Error);
        var repoOnly = await module.ExecuteAsync(Call("watch_ci", ("repo", "o/r")), ctx, default);
        Check("T6 another repo with nothing to watch there is refused", !repoOnly.Success && repoOnly.Error!.Contains("pr, branch or commit"), repoOnly.Error);
        var ciStart = await module.ExecuteAsync(Call("watch_ci", ("pr", "#77"), ("timeout_minutes", "5")), ctx, default);
        Check("T7 watch_ci pr=#77 starts 'PR #77' (the # is dropped)", ciStart.Success && svc.ActiveFor("chat-tool").Any(j => j.Target == "PR #77"), ciStart.Output + ciStart.Error);
        var cancelOther = await module.ExecuteAsync(Call("watch_cancel", ("id", svc.ActiveFor("chat-tool")[0].Id)),
            new ToolModuleContext { SessionKey = "x", WorkingDirectory = root }, default);
        Check("T8 the cancel is the calling chat's (scope), whatever the context says", cancelOther.Success);
        AgentRunScope.Enter(new AgentRunScope("another-chat", root));
        var foreign = await module.ExecuteAsync(Call("watch_cancel", ("id", svc.ActiveFor("chat-tool").FirstOrDefault()?.Id ?? "none")), ctx, default);
        Check("T9 another chat's agent cannot cancel it", !foreign.Success, foreign.Output);
        AgentRunScope.Enter(new AgentRunScope("chat-tool", root));
        var all = await module.ExecuteAsync(Call("watch_cancel", ("id", "all")), ctx, default);
        Check("T10 watch_cancel all ends this chat's watches", all.Success && svc.ActiveFor("chat-tool").Count == 0, all.Output);
        AgentRunScope.Enter(null);
        var noChat = await module.ExecuteAsync(Call("watch_list"), ctx, default);
        Check("T11 no chat run = refused (a watch belongs to a chat)", !noChat.Success);

        string repoDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string head = (await EngineeringToolModule.RunProcessAsync("git", new[] { "rev-parse", "HEAD" }, repoDir, 15000, default)).Text.Trim();
        var repoCtx = new ToolModuleContext { SessionKey = "chat-tool", WorkingDirectory = repoDir };
        RunWatchService.Current = svc;
        AgentRunScope.Enter(new AgentRunScope("chat-sha", repoDir));
        var shortSha = await module.ExecuteAsync(Call("watch_ci", ("commit", head[..7])), repoCtx, default);
        var shaJob = svc.ActiveFor("chat-sha").FirstOrDefault();
        Check("T11b a short commit hash is expanded to the full one before the watch starts (gh matches only that)",
            shortSha.Success && shaJob?.Commit == head && shaJob.Target == "commit " + head[..7], shortSha.Error ?? shaJob?.Commit);
        var unknown = await module.ExecuteAsync(Call("watch_ci", ("commit", "abcdef1234")), repoCtx, default);
        Check("T11c a short hash this repository does not know is refused, asking for the full one", !unknown.Success && unknown.Error!.Contains("40-character"), unknown.Error);
        svc.CancelChat("chat-sha");
        AgentRunScope.Enter(null);

        Check("T12 the aliases a model writes resolve (wait_for_ci, monitor_process, unwatch)",
            module.ResolveToolType("wait_for_ci") == ToolType.WatchCi && module.ResolveToolType("monitor_process") == ToolType.WatchProcess
            && module.ResolveToolType("unwatch") == ToolType.WatchCancel);
        var names = EngineeringToolModule.Schemas().Select(s => s.Name).ToList();
        Check("T13 the four schemas are offered", new[] { "watch_ci", "watch_process", "watch_list", "watch_cancel" }.All(names.Contains), string.Join(",", names));
        Check("T14 every CLI agent gets them too (mcp__cluadex__watch_ci …)",
            ChatToolBridge.QualifiedToolNames.Contains("mcp__cluadex__watch_ci") && ChatToolBridge.QualifiedToolNames.Contains("mcp__cluadex__watch_process"));
        Check("T15 they are free to call (no permission card): nothing in the project changes",
            module.DescribePermission(Call("watch_ci", ("pr", "1")), ctx).IsFree && new ToolCall { Type = ToolType.WatchProcess }.IsReadOnly);
        Check("T16 without the service they are not offered, with a reason", (RunWatchService.Current = null) == null
            && module.GetRefusal(ToolType.WatchCi) != null && module.GetRefusal(ToolType.WatchList) != null);
        Check("T17 the prompt section tells the model to hand the wait off, never poll",
            module.GetPromptSection(new PromptSectionContext { NativeTools = true, OfferedToolNames = new[] { "watch_ci" } })!.Contains("watch_ci"));
    }
    bg.OnAppShutdown();

    // ─── G (--live): the real gh, read-only, against this repository's own merged PR and its merge commit ───
    if (args.Contains("--live"))
    {
        CluadeX.Services.Safety.ProcessLaunchPolicy.Allowed.Add("gh.exe");   // read-only gh, on purpose
        string repoDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var gh = new GhCiProbe();
        var pr = await gh.ProbeAsync(new WatchJob { Kind = WatchKind.Ci, Cwd = repoDir, Repo = "xjanova/cluadeX", Pr = "91" }, default);
        Check("G1 live: gh pr checks --json on PR 91 parses, every check finished", pr.Ok && pr.Checks.Count > 0
            && pr.Checks.All(c => c.Bucket != "pending"), pr.Error ?? RunWatchService.DescribeChecks(pr.Checks));
        var (verdict, line) = RunWatchService.EvaluateCi(pr.Checks);
        Check("G2 live: …a verdict (passed or failed), not 'still running'", verdict is WatchState.Passed or WatchState.Failed, line);
        Console.WriteLine("     " + line);
        var commit = await gh.ProbeAsync(new WatchJob { Kind = WatchKind.Ci, Cwd = repoDir, Repo = "xjanova/cluadeX", Commit = "ddbd141" }, default);
        Check("G3 live: a SHORT hash (ddbd141) is expanded and its Actions run found and finished — gh matches the full hash only",
            commit.Ok && commit.Checks.Count > 0 && commit.Checks.All(c => c.Bucket != "pending"), commit.Error ?? RunWatchService.DescribeChecks(commit.Checks));
        Console.WriteLine("     " + RunWatchService.EvaluateCi(commit.Checks).Line);
        var nope = await gh.ProbeAsync(new WatchJob { Kind = WatchKind.Ci, Cwd = repoDir, Repo = "xjanova/cluadeX", Pr = "999999" }, default);
        Check("G4 live: a PR that does not exist is fatal (watching stops at once, saying so)", !nope.Ok && nope.Fatal, nope.Error);
    }

    // ─── K: every key has Thai ───
    {
        var keys = typeof(EngineeringToolModule).Assembly.GetType("CluadeX.Services.Localization.WatchKeys")!
            .GetField("Keys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)
            as Dictionary<string, Dictionary<string, string>>;
        Check("K1 every watch key has en and th", keys != null && keys.Count >= 20 && keys.Values.All(v => v.TryGetValue("th", out var th) && th.Length > 0 && v.ContainsKey("en")));
        Check("K2 every state has a word", Enum.GetNames<WatchState>().All(s => keys!.ContainsKey("watch.state." + s.ToLowerInvariant())));
    }
}
catch (Exception ex)
{
    fail++;
    Console.WriteLine("FAIL (crash) " + ex);
}
finally
{
    try { Directory.Delete(root, true); } catch { }
}

Console.WriteLine();
Console.WriteLine($"## Result: {pass} pass / {fail} FAIL");
return fail == 0 ? 0 : 1;

/// <summary>A CI probe that answers from a queue (then <see cref="Default"/>).</summary>
sealed class ScriptProbe : ICiProbe
{
    private readonly ConcurrentQueue<CiProbeResult> _q = new();
    private int _calls;
    public int Calls => _calls;
    public CiProbeResult Default { get; init; } = new(new[] { new CiCheck("never", "pending") });
    public bool Throw { get; init; }
    public void Enqueue(CiProbeResult r) => _q.Enqueue(r);
    public Task<CiProbeResult> ProbeAsync(WatchJob job, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Throw) throw new InvalidOperationException("probe broke");
        return Task.FromResult(_q.TryDequeue(out var r) ? r : Default);
    }
}
