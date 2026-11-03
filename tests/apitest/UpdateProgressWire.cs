// Owner 2026-11-03: "หน้า สแปรช หากเจออัพเดท ให้โหลดให้เสร็จแล้วติดตั้งก่อน ระบบโหลดต้องแม่นยำเปอเซ็น ทำให้ดี อย่าค้าง".
// The splash now downloads an update to the end and installs it before the app opens. What is proven here, offline and
// with the clock passed in: the one progress figure the splash and the update bar share (UpdateProgressComposer) —
// bytes over a size known up front, never backwards within a step, a restart of a file, parallel deltas, the fall-back
// from deltas to the full package, a package already on disk, clamping, the preparing step, stall timing — and the
// splash's rules (StartupUpdatePolicy): the bounded check, Skip, the circuit breaker, the overall bar.
using System.IO;
using CluadeX.Services;
using C = CluadeX.Services.UpdateProgressComposer;

namespace ApiTest;

internal static class UpdateProgressWire
{
    static readonly DateTime T0 = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
    static DateTime At(double s) => T0.AddSeconds(s);

    const long MB = 1024 * 1024;
    static readonly C.Asset Full = new("CluadeX-3.0.225-full.nupkg", 500 * MB);
    static readonly C.Asset D1 = new("CluadeX-3.0.224-delta.nupkg", 30 * MB);
    static readonly C.Asset D2 = new("CluadeX-3.0.225-delta.nupkg", 20 * MB);

    /// <summary>Percent and bytes say the same thing (the bar and the text are one source).</summary>
    static bool Consistent(C.Snapshot s) => s.Total > 0 && Math.Abs(s.Percent - 100.0 * s.Bytes / s.Total) < 0.01;

    public static Task Run(Action<string, string, bool, string> check, Action<string> section)
    {
        section("Update progress — one accurate figure for the splash and the update bar (offline)");

        // ─── the route Velopack takes ───
        check("update-progress", "route: deltas when a base is on disk and they are few and small",
            C.Predict(Full, [D1, D2], hasBase: true, C.DefaultMaximumDeltas) == C.Route.Delta, "");
        var eleven = Enumerable.Range(0, 11).Select(i => new C.Asset($"CluadeX-3.0.{200 + i}-delta.nupkg", MB)).ToArray();
        check("update-progress", "route: full when there is no base, no delta, more than 10, or deltas bigger than full",
            C.Predict(Full, [D1, D2], hasBase: false, 10) == C.Route.Full
            && C.Predict(Full, [], hasBase: true, 10) == C.Route.Full
            && C.Predict(Full, eleven, hasBase: true, 10) == C.Route.Full
            && C.Predict(Full, eleven.Take(10).ToArray(), hasBase: true, 10) == C.Route.Delta
            && C.Predict(new C.Asset("x-full.nupkg", 40 * MB), [D1, D2], hasBase: true, 10) == C.Route.Full, "");

        // ─── full package: the denominator is known before the first byte ───
        var c = new C();
        c.Begin(Full, [], hasBase: false, 10, At(0));
        var s0 = c.Read(At(0));
        check("update-progress", "the size is known before the first byte (no invented figure)",
            s0.Total == Full.Size && s0.Bytes == 0 && s0.Percent == 0 && !s0.AnyBytes && s0.Stage == C.Stage.Downloading,
            $"total={s0.Total} pct={s0.Percent}");

        // A resumed partial, a drop, then the server ignores Range (200) and the file starts again at 0.
        var seen = new List<C.Snapshot>();
        void F(C x, string name, long bytes, double t, int failures = 0, string err = null, long total = 0)
        { x.OnFile(name, bytes, total > 0 ? total : 500 * MB, failures, err, At(t)); seen.Add(x.Read(At(t))); }
        F(c, Full.FileName, 100 * MB, 1);           // resumed from an earlier run
        F(c, Full.FileName, 150 * MB, 2);
        F(c, Full.FileName, 150 * MB, 3, failures: 1, err: "the connection ended early");
        var dropped = c.Read(At(3));
        F(c, Full.FileName, 0, 4);                  // 200 after a Range request: from byte 0
        var restarted = c.Read(At(4));
        F(c, Full.FileName, 120 * MB, 6);
        F(c, Full.FileName, 160 * MB, 8);
        bool monotonic = seen.Zip(seen.Skip(1)).All(p => p.Second.Percent >= p.First.Percent);
        check("update-progress", "a file restarted at 0 never moves the bar backwards", monotonic,
            string.Join(" ", seen.Select(x => x.Percent.ToString("0.0"))));
        check("update-progress", "…it holds and says why, then moves on once past the old mark",
            restarted.Percent == 30 && restarted.Redownloading && !seen[^1].Redownloading && seen[^1].Percent == 32,
            $"held={restarted.Percent} redl={restarted.Redownloading} last={seen[^1].Percent}");
        check("update-progress", "a drop shows 'resuming' with the reason", dropped.Resuming && dropped.Error == "the connection ended early"
            && !seen[^1].Resuming && seen[^1].Error == null, $"{dropped.Resuming} {dropped.Error}");
        check("update-progress", "bar and text are one source (percent = bytes / total)", seen.All(Consistent),
            string.Join(" ", seen.Select(x => $"{x.Bytes / MB}/{x.Total / MB}={x.Percent:0.0}")));
        check("update-progress", "the first report of a resumed file is a baseline, not speed",
            seen[0].BytesPerSecond == 0, seen[0].BytesPerSecond.ToString("0"));
        check("update-progress", "speed from the bytes that really arrived", seen[1].BytesPerSecond > 40 * MB && seen[1].BytesPerSecond < 60 * MB,
            (seen[1].BytesPerSecond / MB).ToString("0.0") + " MB/s");

        // Every byte in → its own step (Velopack verifies), not a bar stuck at a split value.
        c.OnVelopackPercent(64, At(9));             // Velopack's figure is not the bar
        check("update-progress", "Velopack's own percent does not drive the bar", c.Read(At(9)).Percent == 32, c.Read(At(9)).Percent.ToString());
        c.OnFile(Full.FileName, 500 * MB, 500 * MB, 0, null, At(20));
        var prep = c.Read(At(23));
        check("update-progress", "all bytes in → Preparing at 100 %, with its own seconds",
            prep.Stage == C.Stage.Preparing && prep.Percent == 100 && prep.Bytes == prep.Total && prep.StageSeconds == 3 && prep.StalledSeconds == null,
            $"{prep.Stage} {prep.Percent} {prep.StageSeconds}s stall={prep.StalledSeconds}");

        // ─── stall timing ───
        var st = new C();
        st.Begin(Full, [], false, 10, At(0));
        st.OnFile(Full.FileName, 10 * MB, 500 * MB, 0, null, At(1));
        st.OnFile(Full.FileName, 30 * MB, 500 * MB, 0, null, At(2.5));
        check("update-progress", "no stall before 12 s without a byte",
            st.Read(At(2.5 + 11.9)).StalledSeconds == null, st.Read(At(14.4)).StalledSeconds?.ToString() ?? "null");
        var stalled = st.Read(At(2.5 + 14.2));
        check("update-progress", "a stall says the real seconds, and the speed is 0 meanwhile",
            stalled.StalledSeconds == 14 && stalled.BytesPerSecond == 0, $"{stalled.StalledSeconds} {stalled.BytesPerSecond}");
        st.OnFile(Full.FileName, 30 * MB, 500 * MB, 1, "no data for 45 s", At(47));   // a report without new bytes is no byte
        check("update-progress", "a failure report is not a byte", st.Read(At(47)).StalledSeconds == 44, st.Read(At(47)).StalledSeconds?.ToString());
        st.OnFile(Full.FileName, 31 * MB, 500 * MB, 0, null, At(50));
        check("update-progress", "the next byte ends the stall", st.Read(At(55)).StalledSeconds == null, "");
        var noByte = new C();
        noByte.Begin(Full, [], false, 10, At(0));
        check("update-progress", "no byte at all since the download began counts too", noByte.Read(At(13)).StalledSeconds == 13, "");
        var stInfo = new UpdateProgressInfo(UpdatePhase.Downloading, "3.0.225", 6, 30 * MB, 500 * MB, 0, false, null) { StalledSeconds = 14 };
        check("update-progress", "the row text names the stall (en + th)",
            stInfo.Describe(false) == "30.0 / 500.0 MB · connection stalled 14 s — retrying…"
            && stInfo.Describe(true).Contains("สัญญาณค้าง 14 วินาที"), stInfo.Describe(false) + " | " + stInfo.Describe(true));

        // ─── parallel deltas whose totals arrive late ───
        var d = new C();
        d.Begin(Full, [D1, D2], hasBase: true, 10, At(0));
        var dSeen = new List<C.Snapshot> { d.Read(At(0)) };
        void DF(string name, long bytes, long total, double t) { d.OnFile(name, bytes, total, 0, null, At(t)); dSeen.Add(d.Read(At(t))); }
        DF(D2.FileName, 2 * MB, 0, 0.2);            // no Content-Length yet
        DF(D1.FileName, 3 * MB, 0, 0.3);
        DF(D2.FileName, 10 * MB, 20 * MB, 0.8);     // the total arrives
        DF(D1.FileName, 15 * MB, 30 * MB, 1.1);
        DF(D2.FileName, 20 * MB, 20 * MB, 1.5);
        d.OnVelopackPercent(55, At(1.6));
        dSeen.Add(d.Read(At(1.6)));
        check("update-progress", "deltas: the denominator is their sum from the start",
            dSeen.All(x => x.Total == 50 * MB && x.Route == C.Route.Delta && x.DeltaCount == 2), string.Join(",", dSeen.Select(x => x.Total / MB)));
        check("update-progress", "deltas in parallel: never backwards, bar = bytes",
            dSeen.Zip(dSeen.Skip(1)).All(p => p.Second.Percent >= p.First.Percent) && dSeen.Skip(1).All(Consistent),
            string.Join(" ", dSeen.Select(x => x.Percent.ToString("0.0"))));
        check("update-progress", "deltas: 35 of 50 MB is 70 %", dSeen[^1].Percent == 70, dSeen[^1].Percent.ToString());
        d.OnFile(D1.FileName, 30 * MB, 30 * MB, 0, null, At(2));
        var patching = d.Read(At(2));
        check("update-progress", "all deltas in → Preparing (Update.exe patches), not stuck at Velopack's 70",
            patching.Stage == C.Stage.Preparing && patching.Percent == 100, $"{patching.Stage} {patching.Percent}");
        var patchInfo = new UpdateProgressInfo(UpdatePhase.Preparing, "3.0.225", 100, 50 * MB, 50 * MB, 0, false, null) { Delta = true, DeltaCount = 2, PhaseSeconds = 12 };
        check("update-progress", "the preparing step has its own text (en + th)",
            patchInfo.Describe(false) == "downloaded — applying 2 patch(es)… 12 s" && patchInfo.Describe(true).Contains("2 แพตช์"),
            patchInfo.Describe(false) + " | " + patchInfo.Describe(true));
        var viaVpk = new C();
        viaVpk.Begin(Full, [D1, D2], true, 10, At(0));
        viaVpk.OnFile(D1.FileName, 29 * MB, 30 * MB, 0, null, At(1));
        viaVpk.OnVelopackPercent(70, At(2));
        check("update-progress", "Velopack at 70 on the delta route also means the patch step", viaVpk.Read(At(2)).Stage == C.Stage.Preparing, "");

        // ─── the delta path fails → the full package ───
        d.OnFile(Full.FileName, 0, 500 * MB, 0, null, At(30));
        var fb = d.Read(At(30));
        d.OnFile(Full.FileName, 50 * MB, 500 * MB, 0, null, At(31));
        var fb2 = d.Read(At(31));
        check("update-progress", "delta → full: the denominator is reset to the full size, and said",
            fb.Route == C.Route.Full && fb.Total == Full.Size && fb.FellBackToFull && fb.Stage == C.Stage.Downloading && fb.Percent == 0
            && fb2.Percent == 10 && Consistent(fb2), $"{fb.Route} {fb.Total / MB} fell={fb.FellBackToFull} {fb.Percent}→{fb2.Percent}");
        var fbVpk = new C();
        fbVpk.Begin(Full, [D1, D2], true, 10, At(0));
        fbVpk.OnVelopackPercent(70, At(5));
        fbVpk.OnVelopackPercent(1, At(9));          // the full download's first percent, before any file report
        var fbv = fbVpk.Read(At(9));
        check("update-progress", "delta → full is also seen from Velopack's figure dropping after the patch step",
            fbv.FellBackToFull && fbv.Route == C.Route.Full && fbv.Total == Full.Size && fbv.Stage == C.Stage.Downloading, $"{fbv.Route} {fbv.FellBackToFull}");
        var fbText = new UpdateProgressInfo(UpdatePhase.Downloading, "3.0.225", 10, 50 * MB, 500 * MB, 0, false, null) { FellBackToFull = true };
        check("update-progress", "…and the text says the patches failed", fbText.Describe(false).StartsWith("patches failed — downloading the full package · 50.0 / 500.0 MB")
            && fbText.Describe(true).StartsWith("แพตช์ใช้ไม่ได้"), fbText.Describe(false));
        var wrong = new C();
        wrong.Begin(Full, [D1, D2], hasBase: false, 10, At(0));   // predicted full, Velopack takes deltas after all
        wrong.OnFile(D1.FileName, 3 * MB, 30 * MB, 0, null, At(1));
        var w = wrong.Read(At(1));
        check("update-progress", "a wrong prediction is corrected by the first file", w.Route == C.Route.Delta && w.Total == 50 * MB && !w.FellBackToFull && w.Percent == 6, $"{w.Route} {w.Total / MB} {w.Percent}");

        // ─── already on disk / clamps / a feed without sizes ───
        var cached = new C();
        cached.Begin(Full, [], false, 10, At(0));
        var cs = cached.Read(At(0.3));
        check("update-progress", "package already on disk: no callbacks → no bytes counted, no figure invented",
            !cs.AnyBytes && cs.Percent == 0 && cs.Bytes == 0, $"{cs.AnyBytes} {cs.Percent}");
        var staged = new UpdateProgressInfo(UpdatePhase.Staged, "3.0.225", 100, 0, 0, 0, false, null) { FromCache = true };
        check("update-progress", "…the staged state carries it", staged.FromCache && staged.Percent == 100, "");
        var cl = new C();
        cl.Begin(new C.Asset("CluadeX-9-full.nupkg", 100), [], false, 10, At(0));
        cl.OnFile("CluadeX-9-full.nupkg", 80, 70, 0, null, At(1));   // the server says less than the feed
        cl.OnFile("CluadeX-9-full.nupkg", -5, 100, 0, null, At(1.5));
        cl.OnVelopackPercent(250, At(2));
        var cls = cl.Read(At(2));
        check("update-progress", "clamped: never above 100, bytes never above the total, negative is 0",
            cls.Percent <= 100 && cls.Bytes <= cls.Total && cl.Read(At(1.5)).Percent >= 80, $"{cls.Percent} {cls.Bytes}/{cls.Total}");
        var over = new C();
        over.Begin(new C.Asset("a-full.nupkg", 100), [], false, 10, At(0));
        over.OnFile("a-full.nupkg", 140, 140, 0, null, At(1));
        check("update-progress", "more bytes than the feed stated: capped at the size", over.Read(At(1)).Percent == 100 && over.Read(At(1)).Bytes == 100, "");
        var nos = new C();
        nos.Begin(new C.Asset("b-full.nupkg", 0), [], false, 10, At(0));
        nos.OnVelopackPercent(40, At(1));
        nos.OnFile("b-full.nupkg", 40, 100, 0, null, At(1));
        nos.OnVelopackPercent(30, At(2));
        var ns = nos.Read(At(2));
        check("update-progress", "a feed without sizes: Velopack's figure, still never backwards", ns.Percent == 40 && ns.Total == 100, $"{ns.Percent} {ns.Total}");

        // ─── the splash's rules ───
        section("Startup update — the splash's rules (offline)");
        var cap = StartupUpdatePolicy.CheckCap;
        check("startup-update", "the check is bounded: wait before the cap, open the app at it",
            StartupUpdatePolicy.Decide(false, UpdatePhase.Checking, cap - TimeSpan.FromSeconds(1), false) == StartupUpdatePolicy.Next.Wait
            && StartupUpdatePolicy.Decide(false, UpdatePhase.Checking, cap, false) == StartupUpdatePolicy.Next.CheckTooSlow
            && StartupUpdatePolicy.Decide(false, UpdatePhase.Idle, cap + TimeSpan.FromSeconds(5), false) == StartupUpdatePolicy.Next.CheckTooSlow
            && cap >= TimeSpan.FromSeconds(20) && cap <= TimeSpan.FromSeconds(25), cap.ToString());
        check("startup-update", "a download that started is waited for to the end (no 20 s / 60 s give-up)",
            StartupUpdatePolicy.Decide(false, UpdatePhase.Downloading, TimeSpan.FromMinutes(30), false) == StartupUpdatePolicy.Next.Wait
            && StartupUpdatePolicy.Decide(false, UpdatePhase.Preparing, TimeSpan.FromMinutes(6), false) == StartupUpdatePolicy.Next.Wait, "");
        // The job sets Staged / Failed as its last statements and its task completes a moment later: a poll in between
        // (long past the cap after a real download) must keep waiting, not open the app with "slow network".
        check("startup-update", "a job between its final phase and its end is waited for (no 'slow network' over a finished download)",
            StartupUpdatePolicy.Decide(false, UpdatePhase.Staged, TimeSpan.FromMinutes(12), false) == StartupUpdatePolicy.Next.Wait
            && StartupUpdatePolicy.Decide(false, UpdatePhase.Failed, TimeSpan.FromMinutes(12), false) == StartupUpdatePolicy.Next.Wait
            && StartupUpdatePolicy.Decide(true, UpdatePhase.Staged, TimeSpan.FromMinutes(12), false) == StartupUpdatePolicy.Next.Finished, "");
        check("startup-update", "Skip / Alt+F4 wins over everything, then the job's end",
            StartupUpdatePolicy.Decide(true, UpdatePhase.Staged, TimeSpan.Zero, true) == StartupUpdatePolicy.Next.Skipped
            && StartupUpdatePolicy.Decide(false, UpdatePhase.Downloading, TimeSpan.Zero, true) == StartupUpdatePolicy.Next.Skipped
            && StartupUpdatePolicy.Decide(true, UpdatePhase.Checking, TimeSpan.FromMinutes(9), false) == StartupUpdatePolicy.Next.Finished, "");
        check("startup-update", "the restart's wait for startup work is a few seconds",
            StartupUpdatePolicy.RestartPrepCap > TimeSpan.Zero && StartupUpdatePolicy.RestartPrepCap <= TimeSpan.FromSeconds(10), StartupUpdatePolicy.RestartPrepCap.ToString());

        // The circuit breaker: one or two failures are said, not a reason to stop (the splash used to stop on any text).
        string dir = Path.Combine(Path.GetTempPath(), "cluadex-apitest-breaker-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var log = UpdateAttemptLog.Load(dir);
            bool fresh = StartupUpdatePolicy.MayAutoInstall(log, "3.0.225") && log.Describe() == null;
            log.RecordAttempt("3.0.225");
            bool one = StartupUpdatePolicy.MayAutoInstall(log, "3.0.225") && log.Describe() != null;
            log.RecordAttempt("3.0.225");
            log.RecordAttempt("3.0.225");
            var reloaded = UpdateAttemptLog.Load(dir);   // it is on disk: it outlives the attempt
            bool three = !StartupUpdatePolicy.MayAutoInstall(reloaded, "3.0.225") && !StartupUpdatePolicy.MayAutoInstall(reloaded, "3.0.225+abc");
            bool other = StartupUpdatePolicy.MayAutoInstall(reloaded, "3.0.226") && !StartupUpdatePolicy.MayAutoInstall(reloaded, null);
            check("startup-update", "breaker: 1 failure is shown but installs again; 3 pause it (also after a restart)", fresh && one && three, $"{fresh} {one} {three}");
            check("startup-update", "breaker: a newer version, never a missing one, may install", other, "");
            string button = LocalizationService.Lookup("nav.installRestart", "en"), buttonTh = LocalizationService.Lookup("nav.installRestart", "th");
            string en = reloaded.Describe(thai: false), th = reloaded.Describe(thai: true);
            check("startup-update", "the paused text names the real button (en + th)",
                en.Contains($"“{button}”") && th.Contains($"“{buttonTh}”") && !en.Contains("Restart & apply"), en + " | " + th);
            reloaded.NoteRunningVersion("3.0.225+fd40d8d");
            check("startup-update", "running the target clears it", UpdateAttemptLog.Load(dir).Describe() == null && StartupUpdatePolicy.MayAutoInstall(UpdateAttemptLog.Load(dir), "3.0.225"), "");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        check("startup-update", "overall bar: the update's share folds in, never past 100",
            Math.Abs(StartupUpdatePolicy.OverallPercent(6, 7, 0.5) - 100.0 * 6.5 / 7) < 1e-9
            && StartupUpdatePolicy.OverallPercent(7, 7, 1) == 100 && StartupUpdatePolicy.OverallPercent(6, 7, 3) == 100
            && StartupUpdatePolicy.OverallPercent(0, 0, 1) == 0 && StartupUpdatePolicy.OverallPercent(6, 7, -1) == 100.0 * 6 / 7, "");
        check("startup-update", "the update bar's preparing text has a Thai value",
            LocalizationService.Lookup("update.bgPreparing", "th") is { } pth && pth != LocalizationService.Lookup("update.bgPreparing", "en") && pth.Contains("{0}") && pth.Contains("{1}"), "");
        check("startup-update", "UpdatePhase keeps its numbers (Preparing appended)",
            (int)UpdatePhase.Idle == 0 && (int)UpdatePhase.Failed == 4 && (int)UpdatePhase.Preparing == 5, "");
        return Task.CompletedTask;
    }
}
