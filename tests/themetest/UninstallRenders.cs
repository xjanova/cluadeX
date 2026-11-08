using System.IO;
using System.Windows;
using CluadeX.Services;
using CluadeX.Services.Uninstall;
using CluadeX.ViewModels.Setup;
using CluadeX.Views.Setup;
using Microsoft.Extensions.DependencyInjection;

namespace ThemeTest;

internal static partial class Program
{
    static bool UninstallOnly;

    /// <summary>
    /// The uninstall page, every page and state, rendered for the eye — Thai, then English. Nothing is deleted: the
    /// page is given a throwaway data folder and every state is set through the view model's Preview* hooks. The page
    /// has the setup wizard's own fixed look (it runs without the app, so no theme is loaded): one set of pictures
    /// covers every theme. Also checks no text is clipped and nothing sits outside the 960x600 window.
    ///
    ///   dotnet run -- --uninstall --out D:\...\renders\cluadex
    /// </summary>
    static async Task<int> UninstallShots()
    {
        Log("\n## Uninstall page (rendered for the eye)");
        var loc = Sp.GetRequiredService<LocalizationService>();
        LocalizedResources.Instance.Initialize(loc);
        SetupWizardWindow.Animations = false;

        string fake = Path.Combine(OutRoot, "_uninstall-data", ".cluadex");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake, "settings.json"), "{}");
        var opts = new UninstallOptions(fake, true, null, false, true);
        var custom = new CustomFolder("ModelDirectory", @"D:\DataAiModel", KeepGroupId.Models);
        (KeepGroupId, long, bool)[] Rows(bool keepAll, bool removeAll) => new[]
        {
            (KeepGroupId.Chats, 41L * 1024 * 1024, !removeAll),
            (KeepGroupId.Settings, 2L * 1024 * 1024 + 300_000, !removeAll),
            (KeepGroupId.License, 1L, !removeAll),
            (KeepGroupId.Extensions, 46L * 1024 * 1024, !removeAll),
            (KeepGroupId.Studio, 272L * 1024 * 1024, !removeAll),
            (KeepGroupId.Models, 1288L * 1024 * 1024, !removeAll),
            (KeepGroupId.Logs, 214L * 1024 * 1024, keepAll),
        };

        foreach (string lang in new[] { "th", "en" })
        {
            loc.SetLanguage(lang);
            string dir = Path.Combine(OutRoot, "uninstall", lang);
            Log($"\n### {lang}");
            string L(KeepGroupId g) => loc.T(UninstallCatalog.LabelKey(g));

            await UShot(dir, "01-choose", vm => vm.PreviewChoose(Rows(false, false), new[] { (custom, 18_400_000_000L, true) }, appRunning: true), vm =>
                Check($"uninstall[{lang}]: by default the user's data is kept, logs and cache are not", vm.Rows.Where(r => r.Group != KeepGroupId.Logs).All(r => r.Keep)
                    && !vm.Rows.Single(r => r.Group == KeepGroupId.Logs && !r.IsCustom).Keep));
            await UShot(dir, "02-choose-keep-all", vm => vm.PreviewChoose(Rows(true, false), new[] { (custom, 18_400_000_000L, true) }, appRunning: false), vm =>
                Check($"uninstall[{lang}]: everything ticked → the button says the data is kept", vm.GoText == loc.T("un.choose.goKeepAll")));
            await UShot(dir, "03-choose-remove-all", vm => vm.PreviewChoose(Rows(false, true), new[] { (custom, 18_400_000_000L, false) }, appRunning: false));
            await UShot(dir, "04-confirm", vm => { vm.PreviewChoose(Rows(false, false).Select(r => r.Item1 is KeepGroupId.Chats or KeepGroupId.Models ? (r.Item1, r.Item2, false) : r).ToArray(), new[] { (custom, 18_400_000_000L, false) }, false); vm.PreviewConfirm(); }, vm =>
                Check($"uninstall[{lang}]: deleting chats, models and a chosen folder asks first, listing all three (logs never ask)", vm.ShowConfirm && vm.ConfirmLines.Count == 3, string.Join(" | ", vm.ConfirmLines)));
            await UShot(dir, "05-progress", vm => vm.PreviewProgress(new[]
            {
                (loc.T("un.prog.close"), StepState.Done, loc.T("un.prog.closed")),
                (loc.T("un.prog.delete", L(KeepGroupId.Models)), StepState.Done, loc.T("un.prog.freed", "1.3 GB")),
                (loc.T("un.prog.delete", L(KeepGroupId.Logs)), StepState.Running, ""),
                (loc.T("un.prog.links"), StepState.Pending, ""),
                (loc.T("un.prog.program"), StepState.Pending, loc.T("un.prog.programAfter")),
            }));
            await UShot(dir, "06-progress-file-in-use", vm => vm.PreviewProgress(new[]
            {
                (loc.T("un.prog.close"), StepState.Done, loc.T("un.prog.notRunning")),
                (loc.T("un.prog.delete", L(KeepGroupId.Chats)), StepState.Done, loc.T("un.prog.freed", "41 MB")),
                (loc.T("un.prog.delete", L(KeepGroupId.Logs)), StepState.Failed, loc.T("un.prog.left", 2, loc.T("un.err.inUse"))),
                (loc.T("un.prog.links"), StepState.Done, loc.T("un.prog.linksDone", 4)),
                (loc.T("un.prog.program"), StepState.Pending, loc.T("un.prog.programAfter")),
            }));
            await UShot(dir, "07-done-kept", vm => vm.PreviewDone(new[]
            {
                $"{L(KeepGroupId.Chats)} · 41 MB", $"{L(KeepGroupId.Settings)} · 2.3 MB", L(KeepGroupId.License), $"{loc.T("un.c.models")} — D:\\DataAiModel",
            }, ""));
            await UShot(dir, "08-done-nothing-kept", vm => vm.PreviewDone(Array.Empty<string>(), ""));
            await UShot(dir, "09-done-problems", vm => vm.PreviewDone(new[] { $"{L(KeepGroupId.Chats)} · 41 MB" },
                loc.T("un.done.problems") + "\n• cluadex-20261108.log — " + loc.T("un.err.inUse") + "\n• ggml-cuda.dll — " + loc.T("un.err.denied")), vm =>
                Check($"uninstall[{lang}]: files left → the reason, Try again and the report are on the done page", vm.HasProblems));
        }
        loc.SetLanguage("en");
        try { Directory.Delete(Path.Combine(OutRoot, "_uninstall-data"), true); } catch { }
        return Finish();

        async Task UShot(string dir, string name, Action<UninstallViewModel> arrange, Action<UninstallViewModel> check = null)
        {
            var vm = new UninstallViewModel(opts, null, new UninstallReport(), loc);
            var win = new UninstallWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                win.Show();
                await Idle(400);   // the real measuring pass on the throwaway folder finishes first
                arrange(vm);
                await Idle(250);
                var bmp = await Stable(() => (RootVisual(win), 960, 600), "uninstall/" + name);
                string path = Path.Combine(dir, name + ".png");
                SavePng(bmp, path);
                Log($"  {name}: {path}");
                check?.Invoke(vm);
                CheckWizardLayout(win, name);
            }
            finally { win.Close(); await Idle(); }
        }
    }
}
