using System.Windows.Input;
using CluadeX.Services.UiStudio;

namespace CluadeX.ViewModels;

/// <summary>
/// How the page's loading stands, for the toolbar's thin progress bar, the status strip and — when the browser has
/// nothing worth showing (its own error page after every retry, a page that stays blank) — the "could not be shown"
/// card with Try again. The view tracks the load (<see cref="PageLoadTracker"/>) and reports it through
/// <see cref="StudioService.PageLoad"/>. (Owner 2026-11-03: "เพิ่มการโหลดหน้าตามจริงใหม่ เพราะบางทีมัน ไม่สมบูรณ์ในการเรียกหน้า")
/// </summary>
public sealed partial class UiStudioViewModel
{
    private ICommand? _retryLoad;

    private void HookPageLoad() => _studio.PageLoadChanged += () =>
    {
        foreach (var name in new[] { nameof(IsPageLoading), nameof(PageLoadText), nameof(PageLoadFailed), nameof(PageLoadIsError) })
            OnPropertyChanged(name);
    };

    /// <summary>The page is on its way (loading, being checked, or waiting to try the server again).</summary>
    public bool IsPageLoading => _studio.PageLoad.IsBusy;

    /// <summary>The page could not be shown: the view hides the browser and shows the card instead.</summary>
    public bool PageLoadFailed => _studio.PageLoad.HidesPage;

    /// <summary>The page loaded with an HTTP error, or did not load (the status strip shows it in red).</summary>
    public bool PageLoadIsError => _studio.PageLoad.State is PageLoadState.Failed or PageLoadState.LoadedWithHttpError;

    public string PageLoadText
    {
        get
        {
            var p = _studio.PageLoad;
            return p.State switch
            {
                PageLoadState.Navigating => p.Attempt > 1 ? T("studio.load.waiting", p.Attempt, p.MaxAttempts) : T("studio.load.loading"),
                PageLoadState.Verifying => T("studio.load.verifying"),
                PageLoadState.Retrying => T("studio.load.waiting", p.Attempt + 1, p.MaxAttempts),
                PageLoadState.LoadedWithHttpError => T("studio.load.http", p.HttpStatus),
                PageLoadState.Failed => p.Problem switch
                {
                    PageLoadProblem.Unreachable => T("studio.load.unreachable", p.Detail ?? ""),
                    PageLoadProblem.Blank => T("studio.load.blank"),
                    PageLoadProblem.NoOverlay => T("studio.load.noOverlay"),
                    PageLoadProblem.BuildError => T("studio.load.build"),
                    _ => T("studio.err.navigate", p.Detail ?? ""),
                },
                _ => "",
            };
        }
    }

    /// <summary>The failure card's button: the same as Reload — the failed page is opened again, from attempt 1.</summary>
    public ICommand RetryLoadCommand => _retryLoad ??= new RelayCommand(() => _studio.ReloadPage(hard: false), () => _studio.HasProject);
}
