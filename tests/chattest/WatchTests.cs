using System.IO;
using System.Windows;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Background;
using CluadeX.ViewModels;
using CluadeX.Views;
using Microsoft.Extensions.DependencyInjection;

namespace ChatTest;

internal static partial class Program
{
    /// <summary>W — the Watch page: commands found from the project, tests run to the end and summarized, a crash
    /// caught from the app's own output, a page that does not answer, and "ask the AI" filling the project's chat
    /// input (not sending).</summary>
    static async Task TestWatchPage()
    {
        Log("\n## W — Watch: your own apps — tests, crashes, web pages; report, then wait for the owner");
        string root = Path.Combine(Path.GetTempPath(), "cx-watch-" + Guid.NewGuid().ToString("N")[..6]);
        string good = Path.Combine(root, "GoodApp"), bad = Path.Combine(root, "BadApp"), dn = Path.Combine(root, "DotnetApp");
        Directory.CreateDirectory(good); Directory.CreateDirectory(bad); Directory.CreateDirectory(Path.Combine(dn, "src", "App")); Directory.CreateDirectory(Path.Combine(dn, "tests", "T"));
        File.WriteAllText(Path.Combine(dn, "Dotnet.slnx"), "<Solution/>");
        File.WriteAllText(Path.Combine(dn, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dn, "tests", "T", "T.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\"/></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(good, "package.json"), "{\"scripts\":{\"test\":\"x\",\"dev\":\"vite\"}}");
        var bg = Sp.GetRequiredService<BackgroundProcessService>();
        var svc = new WatchService(bg, Settings, null, Path.Combine(root, "watch.json"));
        Window? win = null;
        try
        {
            var d = WatchService.Detect(dn);
            Check("WA1 a .NET solution: dotnet test on the solution, dotnet run on the WinExe project (not the test project)",
                d.Test == "dotnet test \"Dotnet.slnx\"" && d.Run.Contains("dotnet run --project") && d.Run.Contains("App.csproj") && !d.Run.Contains("T.csproj"), $"{d.Test} | {d.Run}");
            var n = WatchService.Detect(good);
            Check("WA2 a Node project: npm test and npm run dev from package.json scripts", n.Test == "npm test" && n.Run == "npm run dev", $"{n.Test} | {n.Run}");
            Check("WA3 the summary line of dotnet test / jest / pytest is read",
                WatchService.SummarizeTests(["x", "Passed!  - Failed:     0, Passed:   225, Skipped:     2, Total:   227"]) == "225 passed, 0 failed, 2 skipped"
                && WatchService.SummarizeTests(["Tests:       1 failed, 4 passed, 5 total"]).Contains("1 failed")
                && WatchService.SummarizeTests(["===== 3 passed, 1 failed in 0.20s ====="]).StartsWith("3 passed"));

            var gapp = svc.Add(good);
            gapp.TestCommand = "echo Passed!  - Failed: 0, Passed: 3, Skipped: 0";
            var bapp = svc.Add(bad);
            bapp.TestCommand = "echo Failed!  - Failed: 1, Passed: 2, Skipped: 0 && exit 1";
            bapp.RunCommand = "echo starting && echo Unhandled exception: boom && exit 3";
            bapp.Url = "http://127.0.0.1:1/";
            Check("WA4 the same folder added twice is one entry", svc.Add(good) == gapp && svc.Apps.Count == 2);

            await svc.RunTestsAsync(gapp);
            await svc.RunTestsAsync(bapp);
            Check("WA5 tests run to the end: passing → passed with its summary; failing → failed with the output kept",
                gapp.TestState == "passed" && gapp.TestSummary == "3 passed, 0 failed, 0 skipped"
                && bapp.TestState == "failed" && bapp.TestSummary.StartsWith("2 passed, 1 failed") && bapp.TestTail.Contains("Failed!"),
                $"{gapp.TestState} {gapp.TestSummary} | {bapp.TestState} {bapp.TestSummary}");
            await svc.StartAppAsync(bapp);
            await WaitUntil(() => bapp.RunState == "crashed", 10000);
            Check("WA6 an app that ends by itself with an error is caught as crashed, with its error lines",
                bapp.RunState == "crashed" && bapp.RunErrors.Contains("boom"), $"{bapp.RunState} · {bapp.RunErrors}");
            await svc.CheckUrlAsync(bapp);
            Check("WA7 a page that does not answer is down", bapp.UrlState == "down", bapp.UrlDetail);
            Check("WA8 the problem shows on the app, the passing one is fine", bapp.HasProblem && !gapp.HasProblem);

            var svc2 = new WatchService(bg, Settings, null, Path.Combine(root, "watch.json"));
            var again = svc2.Apps.FirstOrDefault(a => a.ProjectPath == bad);
            Check("WA9 watched apps and their last test results survive a restart", svc2.Apps.Count == 2 && again?.TestState == "failed" && again.TestSummary.StartsWith("2 passed"));

            string ev = WatchService.Evidence(bapp, thai: true);
            Check("WA10 the evidence for the AI carries the failing tests, the crash and the page", ev.Contains("Failed!") && ev.Contains("boom") && ev.Contains("127.0.0.1:1"), ev);

            var vm = new WatchViewModel(svc, Vm, Sp.GetRequiredService<FileSystemService>(), LocSvc) { Selected = bapp };
            int sentBefore = Fake.Calls.Count;
            vm.AskAiCommand.Execute(bapp);
            await WaitUntil(() => Vm.UserInput.Contains("boom"));
            Check("WA11 \"ask the AI\" opens a chat in that project with the evidence typed in — and sends nothing",
                Vm.UserInput.Contains("boom") && string.Equals(Vm.WorkingDirectory.TrimEnd('\\'), bad, StringComparison.OrdinalIgnoreCase) && Fake.Calls.Count == sentBefore,
                $"folder={Vm.WorkingDirectory} calls {sentBefore}→{Fake.Calls.Count}");
            Vm.UserInput = "";

            win = new Window { Width = 1280, Height = 820, Content = new WatchView { DataContext = vm }, ShowActivated = false, Left = -12000, Top = 0 };
            win.Show();
            foreach (var lang in new[] { "th", "en" })
            {
                LocSvc.SetLanguage(lang);
                await Task.Delay(250); win.UpdateLayout();
                var el = (FrameworkElement)win.Content;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(el);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(AppContext.BaseDirectory, $"watch-{lang}.png"));
                enc.Save(fs);
            }
        }
        finally
        {
            win?.Close();
            foreach (var a in svc.Apps.ToList()) if (a.RunId is int id) await bg.StopAsync(id, TimeSpan.FromSeconds(3));
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
