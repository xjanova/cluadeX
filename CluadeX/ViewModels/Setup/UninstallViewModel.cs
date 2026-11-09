using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CluadeX.Services;
using CluadeX.Services.Setup;
using CluadeX.Services.Uninstall;

namespace CluadeX.ViewModels.Setup;

public enum UninstallPage { Choose, Progress, Done }

/// <summary>One "keep this" row: a data group of ~/.cluadex, or a folder the user chose outside it.</summary>
public sealed class KeepRowVm : ViewModelBase
{
    private bool _keep;
    private string _size = "";
    public KeepGroupId Group { get; init; }
    public CustomFolder? Custom { get; init; }
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsCustom => Custom != null;
    public bool Keep { get => _keep; set => SetProperty(ref _keep, value); }
    public string Size { get => _size; set => SetProperty(ref _size, value); }
    public long Bytes { get; set; }
}

/// <summary>
/// The uninstall page (same look as the setup wizard): tick what to keep → a confirmation when something of the
/// user's goes for good → the steps → done (what was kept, anything that could not be deleted and why). State only;
/// <see cref="UninstallCleaner"/> does the work on a background thread.
/// </summary>
public sealed class UninstallViewModel : ViewModelBase
{
    private readonly UninstallOptions _opts;
    private readonly UninstallEntry.Install? _install;
    private readonly UninstallReport _report;
    private readonly LocalizationService _loc;
    private readonly IReadOnlyList<(string Name, int Pose)> _girls;
    private DataScan? _scan;
    private UninstallPage _page;
    private bool _showConfirm, _busy, _measuring = true, _appRunning;
    private string _doneBody = "", _problemsText = "";

    public ObservableCollection<KeepRowVm> Rows { get; } = new();
    public ObservableCollection<WizardStepVm> Steps { get; } = new();
    public ObservableCollection<string> ConfirmLines { get; } = new();
    public ObservableCollection<string> KeptLines { get; } = new();

    public string DataRoot => _opts.DataRoot;
    public bool Finished { get; private set; }
    public bool Running => _busy;

    public event Action? CloseRequested;

    public ICommand GoCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand BackToChooseCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand KeepAllCommand { get; }
    public ICommand RemoveAllCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenReportCommand { get; }
    public ICommand ToggleLanguageCommand { get; }

    public UninstallViewModel(UninstallOptions opts, UninstallEntry.Install? install, UninstallReport report, LocalizationService loc)
    {
        _opts = opts;
        _install = install;
        _report = report;
        _loc = loc;
        _girls = SetupGirls.Plan(GirlArt.Names, null, new Random());   // never written down: the data folder may be going

        GoCommand = new RelayCommand(Go, () => !_busy && !_measuring);
        ConfirmCommand = new RelayCommand(() => { ShowConfirm = false; _ = RunAsync(); }, () => !_busy);
        BackToChooseCommand = new RelayCommand(() => ShowConfirm = false);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(), () => !_busy);
        KeepAllCommand = new RelayCommand(() => { foreach (var r in Rows) r.Keep = true; OnPropertyChanged(nameof(GoText)); });
        RemoveAllCommand = new RelayCommand(() => { foreach (var r in Rows) r.Keep = false; OnPropertyChanged(nameof(GoText)); });
        CloseCommand = new RelayCommand(() => { Finished = Page == UninstallPage.Done; CloseRequested?.Invoke(); }, () => !_busy);
        RetryCommand = new RelayCommand(() => _ = RunAsync(), () => !_busy);
        OpenFolderCommand = new RelayCommand(() => Shell(DataRoot), () => Directory.Exists(DataRoot));
        OpenReportCommand = new RelayCommand(() => { _report.Save(); Shell(_report.Path); });
        ToggleLanguageCommand = new RelayCommand(() =>
        {
            _loc.SetLanguage(_loc.CurrentLanguage == "th" ? "en" : "th");
            OnPropertyChanged(nameof(LanguageToggleText));
            RebuildTexts();
        });

        BuildRows(DataScan.Run(DataRoot, measure: false));
        // started from inside the message loop: the page is built before Application.Run, when no WPF
        // SynchronizationContext exists yet, so an await here would resume on the thread pool — and --yes would then
        // run the whole uninstall there, where the first change to a bound list throws
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => _ = MeasureAsync()));
    }

    // ─────────────── page state ───────────────

    public UninstallPage Page
    {
        get => _page;
        private set
        {
            if (!SetProperty(ref _page, value)) return;
            OnPropertyChanged(nameof(IsChoose)); OnPropertyChanged(nameof(IsProgress)); OnPropertyChanged(nameof(IsDone));
            OnPropertyChanged(nameof(Stepper)); OnPropertyChanged(nameof(Girl)); OnPropertyChanged(nameof(GirlGlow));
        }
    }
    public bool IsChoose => Page == UninstallPage.Choose;
    public bool IsProgress => Page == UninstallPage.Progress;
    public bool IsDone => Page == UninstallPage.Done;
    public bool ShowConfirm { get => _showConfirm; private set => SetProperty(ref _showConfirm, value); }
    public bool Measuring { get => _measuring; private set { if (SetProperty(ref _measuring, value)) CommandManager.InvalidateRequerySuggested(); } }
    public bool AppRunning { get => _appRunning; private set => SetProperty(ref _appRunning, value); }
    public string DoneBody { get => _doneBody; private set => SetProperty(ref _doneBody, value); }
    public string ProblemsText { get => _problemsText; private set { if (SetProperty(ref _problemsText, value)) OnPropertyChanged(nameof(HasProblems)); } }
    public bool HasProblems => ProblemsText.Length > 0;
    public bool KeptSomething => KeptLines.Count > 0;
    public string LanguageToggleText => _loc.CurrentLanguage == "th" ? "EN" : "ไทย";
    public string GoText => _loc.T(Rows.All(r => r.Keep) ? "un.choose.goKeepAll" : "un.choose.go");

    public IReadOnlyList<StepperItem> Stepper
    {
        get
        {
            var pages = new[] { ("un.step.choose", UninstallPage.Choose), ("un.step.remove", UninstallPage.Progress), ("un.step.done", UninstallPage.Done) };
            return pages.Select((p, i) => new StepperItem(_loc.T(p.Item1), Page > p.Item2 ? "✓" : (i + 1).ToString(), Page == p.Item2, Page > p.Item2, i > 0)).ToList();
        }
    }

    private (string Name, int Pose)? GirlFor(UninstallPage p) => _girls.Count == 0 ? null : _girls[(int)p % _girls.Count];
    public ImageSource? Girl => GirlFor(Page) is { } g ? GirlArt.Image(g.Name, 1180, g.Pose) : null;
    public Color GirlGlow => GirlFor(Page) is { } g ? GirlArt.Glow(g.Name) : Color.FromRgb(0xC0, 0x84, 0xFC);

    // ─────────────── rows ───────────────

    private void BuildRows(DataScan scan)
    {
        _scan = scan;
        var keep = _opts.Keep != null ? new HashSet<KeepGroupId>(_opts.Keep) : null;
        Rows.Clear();
        foreach (var g in UninstallCatalog.Order)
        {
            if (!scan.HasAnything(g)) continue;
            Rows.Add(new KeepRowVm
            {
                Group = g,
                Label = _loc.T(UninstallCatalog.LabelKey(g)),
                Description = _loc.T(UninstallCatalog.DescKey(g)),
                Keep = keep?.Contains(g) ?? UninstallCatalog.DefaultKeep(g),
                Size = _loc.T("un.choose.measuring"),
            });
        }
        foreach (var c in scan.Custom)
        {
            Rows.Add(new KeepRowVm
            {
                Group = c.Kind,
                Custom = c,
                Label = _loc.T(c.Kind switch { KeepGroupId.Models => "un.c.models", KeepGroupId.Chats => "un.c.chats", _ => "un.c.logs" }),
                Description = c.Path,
                // a folder the user chose may hold things that are not CluadeX's: kept unless unticked by hand
                Keep = keep == null || keep.Contains(c.Kind),
                Size = _loc.T("un.choose.measuring"),
            });
        }
        foreach (var r in Rows) r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(KeepRowVm.Keep)) OnPropertyChanged(nameof(GoText)); };
        OnPropertyChanged(nameof(GoText));
    }

    private async Task MeasureAsync()
    {
        Measuring = true;
        var scan = await Task.Run(() => DataScan.Run(DataRoot, measure: true));
        bool running = await Task.Run(IsAppRunning);
        _scan = scan;
        foreach (var r in Rows)
        {
            r.Bytes = r.Custom != null ? scan.CustomBytes.GetValueOrDefault(r.Custom.Path) : scan.Bytes[r.Group];
            r.Size = r.Group == KeepGroupId.License && r.Custom == null ? "" : FormatBytes(r.Bytes, _loc);
        }
        AppRunning = running;
        Measuring = false;
        if (_opts.Yes && Page == UninstallPage.Choose) _ = RunAsync();
    }

    private bool IsAppRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("CluadeX"))
                using (p)
                {
                    if (p.Id == Environment.ProcessId) continue;
                    string? image = null;
                    try { image = p.MainModule?.FileName; } catch { }
                    if (image == null) continue;
                    if (_install != null ? Services.Safety.ProtectedPaths.IsInside(image, _install.Root) : string.Equals(image, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
        }
        catch { }
        return false;
    }

    private void RebuildTexts()
    {
        // labels are init-only: rebuild the row list in the new language, keeping the ticks and the sizes
        var ticks = Rows.ToDictionary(r => r.Custom?.Path ?? r.Group.ToString(), r => (r.Keep, r.Bytes));
        if (_scan != null) BuildRows(_scan);
        foreach (var r in Rows)
            if (ticks.TryGetValue(r.Custom?.Path ?? r.Group.ToString(), out var t))
            {
                r.Keep = t.Keep;
                r.Bytes = t.Bytes;
                r.Size = Measuring ? _loc.T("un.choose.measuring") : r.Group == KeepGroupId.License && r.Custom == null ? "" : FormatBytes(t.Bytes, _loc);
            }
        OnPropertyChanged(nameof(Stepper));
        OnPropertyChanged(nameof(GoText));
    }

    // ─────────────── go ───────────────

    private void Go()
    {
        // logs and cache are rebuilt when needed; anything else of the user's asks first
        var lose = Rows.Where(r => !r.Keep && !(r.Group == KeepGroupId.Logs && r.Custom == null)).ToList();
        if (lose.Count == 0) { _ = RunAsync(); return; }
        ConfirmLines.Clear();
        foreach (var r in lose)
            ConfirmLines.Add(r.Custom != null ? $"{r.Label} — {r.Description}" : r.Size.Length > 0 ? $"{r.Label} · {r.Size}" : r.Label);
        ShowConfirm = true;
    }

    private async Task RunAsync()
    {
        if (_busy) return;
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        ProblemsText = "";
        Page = UninstallPage.Progress;

        // the caller fires and forgets: anything thrown here would leave the page frozen on its spinner, unexplained
        try { await RunStepsAsync(); }
        catch (Exception ex)
        {
            _report.Line("uninstall stopped: " + ex);
            _report.Save();
            ProblemsText = _loc.T("un.done.problems") + "\n• " + ex.Message;
        }

        await Task.Delay(500);
        _busy = false;
        Page = UninstallPage.Done;
        CommandManager.InvalidateRequerySuggested();

        if (_opts.Yes && !HasProblems)
        {
            await Task.Delay(2500);
            Finished = true;
            CloseRequested?.Invoke();
        }
    }

    private async Task RunStepsAsync()
    {
        var scan = await Task.Run(() => DataScan.Run(DataRoot, measure: false));
        var keep = Rows.Where(r => r.Keep && r.Custom == null).Select(r => r.Group).ToList();
        var keepCustom = Rows.Where(r => r.Keep && r.Custom != null).Select(r => r.Custom!.Path).ToList();
        _report.Line($"keep: {(keep.Count == 0 ? "nothing" : string.Join(", ", keep))}{(keepCustom.Count > 0 ? " · folders " + string.Join(", ", keepCustom) : "")}");
        var cleaner = new UninstallCleaner(scan, keep, keepCustom, _install, _opts.DataRootOverridden, _report);

        Steps.Clear();
        var close = Step("close", _loc.T("un.prog.close"));
        var groupSteps = cleaner.GroupsToDelete()
            .Select(g => (g, Step("g." + g, _loc.T("un.prog.delete", _loc.T(UninstallCatalog.LabelKey(g)))))).ToList();
        var customSteps = scan.Custom.Where(c => !keepCustom.Contains(c.Path, StringComparer.OrdinalIgnoreCase))
            .Select(c => (c, Step("c." + c.Path, _loc.T("un.prog.delete", Rows.First(r => r.Custom?.Path == c.Path).Label)))).ToList();
        var links = Step("links", _loc.T("un.prog.links"));
        var program = Step("program", _loc.T("un.prog.program"));

        bool closedOk = await RunStep(close, () =>
        {
            int n = cleaner.CloseRunning(TimeSpan.FromSeconds(10));
            return n > 0 ? _loc.T("un.prog.closed") : _loc.T("un.prog.notRunning");
        }, ex => _loc.T("un.err.closeFailed"));

        if (closedOk)
        {
            foreach (var (g, step) in groupSteps)
                await RunStep(step, () => { long freed = cleaner.DeleteGroup(g); return Freed(freed, cleaner, step); }, ex => ex.Message);
            foreach (var (c, step) in customSteps)
                await RunStep(step, () => { long freed = cleaner.DeleteCustom(c); return Freed(freed, cleaner, step); }, ex => ex.Message);
            await RunStep(links, () =>
            {
                cleaner.ResetSetupWizard();   // kept settings: a reinstall still opens the setup wizard
                int n = cleaner.RemoveLinks();
                cleaner.RemoveDataRootIfEmpty();
                return n > 0 ? _loc.T("un.prog.linksDone", n) : _loc.T("un.prog.linksNone");
            }, ex => ex.Message);
            if (_install != null && !_opts.NoHandoff) program.Detail = _loc.T("un.prog.programAfter");
            else { program.State = StepState.Skipped; program.Detail = _loc.T("un.prog.programDev"); }
        }

        // the done page
        KeptLines.Clear();
        foreach (var r in Rows.Where(r => r.Keep)) KeptLines.Add(r.Custom != null ? $"{r.Label} — {r.Description}" : r.Size.Length > 0 ? $"{r.Label} · {r.Size}" : r.Label);
        OnPropertyChanged(nameof(KeptSomething));
        var problems = cleaner.Problems;
        if (!closedOk) ProblemsText = _loc.T("un.err.closeFailed");
        else if (problems.Count > 0)
            ProblemsText = _loc.T("un.done.problems") + "\n" + string.Join("\n", problems.Take(4).Select(p => $"• {Path.GetFileName(p.Path)} — {Reason(p)}"))
                           + (problems.Count > 4 ? $"\n• … +{problems.Count - 4}" : "");
        DoneBody = _loc.T("un.done.body");
        _report.Line($"done · freed {cleaner.BytesFreed:N0} bytes · {problems.Count} problem(s)");
        _report.Save();
    }

    private string Freed(long freed, UninstallCleaner cleaner, WizardStepVm step)
    {
        var mine = cleaner.Problems.Where(p => !_reported.Contains(p)).ToList();
        foreach (var p in mine) _reported.Add(p);
        if (mine.Count > 0)
        {
            step.State = StepState.Failed;
            return _loc.T("un.prog.left", mine.Count, Reason(mine[0]));
        }
        return _loc.T("un.prog.freed", FormatBytes(freed, _loc));
    }
    private readonly HashSet<DeleteProblem> _reported = new();

    private string Reason(DeleteProblem p) => p.Kind switch
    {
        DeleteFailure.InUse => _loc.T("un.err.inUse"),
        DeleteFailure.Denied => _loc.T("un.err.denied"),
        _ => p.Message,
    };

    private WizardStepVm Step(string id, string label)
    {
        var s = new WizardStepVm { Id = id, Label = label };
        Steps.Add(s);
        return s;
    }

    /// <summary>Run one step off the UI thread; its detail is what the work returns. A step the work already marked
    /// Failed (files left) stays Failed.</summary>
    private static async Task<bool> RunStep(WizardStepVm step, Func<string> work, Func<Exception, string> fail)
    {
        step.State = StepState.Running;
        try
        {
            var started = DateTime.UtcNow;
            string detail = await Task.Run(work);
            // a step that is over in a blink still shows its spinner for a moment, so the list reads as work being done
            var left = TimeSpan.FromMilliseconds(250) - (DateTime.UtcNow - started);
            if (left > TimeSpan.Zero) await Task.Delay(left);
            step.Detail = detail;
            if (step.State == StepState.Running) step.State = StepState.Done;
            return step.State == StepState.Done;
        }
        catch (Exception ex)
        {
            step.Detail = fail(ex);
            step.State = StepState.Failed;
            return false;
        }
    }

    // ─────────────── helpers ───────────────

    public static string FormatBytes(long bytes, LocalizationService loc)
    {
        if (bytes <= 0) return loc.T("un.choose.empty");
        double kb = bytes / 1024.0, mb = kb / 1024.0, gb = mb / 1024.0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return gb >= 1 ? gb.ToString("0.0", inv) + " GB" : mb >= 1 ? mb.ToString(mb >= 100 ? "0" : "0.0", inv) + " MB" : Math.Max(1, kb).ToString("0", inv) + " KB";
    }

    private static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ─────────────── test renders ───────────────

    /// <summary>The renders set a page and its state without doing anything (themetest --uninstall).</summary>
    public void PreviewChoose(IEnumerable<(KeepGroupId Group, long Bytes, bool Keep)> rows, IEnumerable<(CustomFolder Folder, long Bytes, bool Keep)> custom, bool appRunning)
    {
        var scan = new DataScan { DataRoot = DataRoot, Custom = custom.Select(c => c.Folder).ToList() };
        foreach (var (g, b, _) in rows) { scan.Bytes[g] = b; scan.Entries[g].Add(Path.Combine(DataRoot, g.ToString())); }
        BuildRows(scan);
        foreach (var r in Rows)
        {
            if (r.Custom != null)
            {
                var c = custom.First(x => x.Folder.Path == r.Custom.Path);
                r.Keep = c.Keep; r.Bytes = c.Bytes;
            }
            else
            {
                var x = rows.First(y => y.Group == r.Group);
                r.Keep = x.Keep; r.Bytes = x.Bytes;
            }
            r.Size = r.Group == KeepGroupId.License && r.Custom == null ? "" : FormatBytes(r.Bytes, _loc);
        }
        AppRunning = appRunning;
        Measuring = false;
        ShowConfirm = false;
        Page = UninstallPage.Choose;
    }

    public void PreviewConfirm() { Go(); }

    public void PreviewProgress(IEnumerable<(string Label, StepState State, string Detail)> steps)
    {
        Steps.Clear();
        foreach (var (label, state, detail) in steps) Steps.Add(new WizardStepVm { Label = label, State = state, Detail = detail });
        ShowConfirm = false;
        Page = UninstallPage.Progress;
    }

    public void PreviewDone(IEnumerable<string> kept, string problems)
    {
        KeptLines.Clear();
        foreach (var k in kept) KeptLines.Add(k);
        OnPropertyChanged(nameof(KeptSomething));
        DoneBody = _loc.T("un.done.body");
        ProblemsText = problems;
        ShowConfirm = false;
        Page = UninstallPage.Done;
    }
}
