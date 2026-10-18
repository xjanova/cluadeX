using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CluadeX.Services.UiStudio;
using CluadeX.Services.UiStudio.Pages;
using CluadeX.Services.UiStudio.Sketch;
using CluadeX.Views.UiStudio;

namespace CluadeX.ViewModels;

/// <summary>One page in the gallery: its card text, its thumbnail (a real shot or a simulated wireframe) and state.</summary>
public sealed class PageCard : ViewModelBase
{
    private ImageSource? _thumb;
    private ScreenSketch? _sketch;
    private string _state = "none";
    private string _stateText = "";
    private bool _isCurrent, _isStale;
    private string _title;

    public PageCard(StudioPage page, string root)
    {
        Page = page;
        _title = page.Title;
        if (page.SourcePath != null && page.SourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            SourceText = page.SourcePath[root.Length..].TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/') + (page.SourceLine > 1 ? ":" + page.SourceLine : "");
        else SourceText = page.Url ?? "";
    }

    public StudioPage Page { get; }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string RouteText => Page.Kind == StudioPageKind.External ? Page.Url ?? "" : Page.Route ?? Page.Detail ?? "";
    public string SourceText { get; }
    public bool IsScreen => Page.Kind == StudioPageKind.Screen;
    public bool IsExternal => Page.Kind == StudioPageKind.External;
    public bool NeedsLogin => Page.NeedsLogin;
    public bool NeedsParams => Page.NeedsParams;

    public ImageSource? Thumb
    {
        get => _thumb;
        set { if (SetProperty(ref _thumb, value)) { OnPropertyChanged(nameof(HasThumb)); OnPropertyChanged(nameof(ShowPlaceholder)); OnPropertyChanged(nameof(ShowSketch)); } }
    }
    public ScreenSketch? Sketch
    {
        get => _sketch;
        set { if (SetProperty(ref _sketch, value)) { OnPropertyChanged(nameof(HasSketch)); OnPropertyChanged(nameof(ShowPlaceholder)); OnPropertyChanged(nameof(ShowSketch)); } }
    }
    public bool HasThumb => _thumb != null;
    public bool HasSketch => _sketch != null;
    /// <summary>The wireframe shows until Flutter's real picture of the screen is there.</summary>
    public bool ShowSketch => _sketch != null && _thumb == null;
    public bool ShowPlaceholder => _thumb == null && _sketch == null;

    private int _problems;
    /// <summary>Problems Flutter reported while drawing this screen (overflow, an exception in build).</summary>
    public int Problems { get => _problems; set { if (SetProperty(ref _problems, value)) OnPropertyChanged(nameof(HasProblems)); } }
    public bool HasProblems => _problems > 0;

    /// <summary>none · queued · shooting · done · failed · params · risky · noServer · manual · sketch</summary>
    public string State { get => _state; private set => SetProperty(ref _state, value); }
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public bool IsBusy => _state is "queued" or "shooting" or "rendering";
    public bool CanShootByHand => _state is "manual" or "risky" or "failed" or "done";

    public void SetState(string state, string text)
    {
        State = state;
        StateText = text;
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanShootByHand));
    }

    public bool IsCurrent { get => _isCurrent; set => SetProperty(ref _isCurrent, value); }
    /// <summary>The project changed after this thumbnail was taken.</summary>
    public bool IsStale { get => _isStale; set => SetProperty(ref _isStale, value); }
    internal string? ThumbDevice;
    internal bool Sketched;
}

/// <summary>A row of cards — the gallery virtualises by row, so a site with 1,500 pages stays light.</summary>
public sealed class PageRow
{
    public List<PageCard> Cards { get; } = new();
}

/// <summary>A filter chip above the gallery ("/admin · 120").</summary>
public sealed class PageSection : ViewModelBase
{
    private bool _selected;
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public int Count { get; init; }
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
}

/// <summary>
/// The page gallery: every page of the project as a thumbnail you can read — a real screenshot for a web page (shot by
/// a hidden browser, kept on disk, re-shot when the source changes), a wireframe simulated from the source for an
/// app screen (Flutter) — and a click loads that page into the Studio to edit. Any web page can be added too.
/// </summary>
public sealed partial class UiStudioViewModel
{
    private PageShotStore? _store;
    private PageCatalogResult? _catalog;
    private readonly List<PageCard> _allCards = new();
    private readonly LinkedList<PageCard> _queue = new();
    private CancellationTokenSource _pagesCts = new();
    private bool _pumping, _sketching;
    private DateTime _projectStamp = DateTime.MinValue;
    private StudioPage? _openWhenServing;

    /// <summary>Takes a thumbnail (set by the view, which owns the hidden browser).</summary>
    public Func<string, int, int, CancellationToken, Task<PageShot?>>? Shooter { get; set; }

    public ObservableCollection<PageRow> PageRows { get; } = new();
    public ObservableCollection<PageSection> PageSections { get; } = new();
    /// <summary>The screens of a simulated app (its left panel).</summary>
    public ObservableCollection<PageCard> ScreenCards { get; } = new();

    public bool IsSimulated => _studio.Project.Kind == StudioProjectKind.Simulated;
    public bool IsServerSite => _studio.Project.Kind == StudioProjectKind.Server;
    /// <summary>The project shows in the live browser (not a simulated app).</summary>
    public bool IsLiveView => HasProject && !IsSimulated;

    private bool _showPages;
    public bool ShowPages
    {
        get => _showPages;
        set
        {
            if (!SetProperty(ref _showPages, value)) return;
            if (value) { ShowGallery = false; _ = OpenPagesAsync(); }
            else { lock (_queue) _queue.Clear(); }
        }
    }

    private bool _discovering;
    public bool IsDiscovering
    {
        get => _discovering;
        private set { if (SetProperty(ref _discovering, value)) { OnPropertyChanged(nameof(ShowDiscovering)); OnPropertyChanged(nameof(ShowPagesEmpty)); } }
    }
    /// <summary>The first read of a project (later reads keep the cards on screen meanwhile — no flash).</summary>
    public bool ShowDiscovering => _discovering && PageRows.Count == 0;
    /// <summary>Read, and nothing matches (no pages, or the search/section hides them all).</summary>
    public bool ShowPagesEmpty => !_discovering && _catalog != null && PageRows.Count == 0;

    private string _pagesSummary = "";
    public string PagesSummary { get => _pagesSummary; private set => SetProperty(ref _pagesSummary, value); }

    private string _pageSearch = "";
    public string PageSearch { get => _pageSearch; set { if (SetProperty(ref _pageSearch, value ?? "")) RebuildRows(); } }

    private int _galleryColumns = 4;
    /// <summary>Cards per row (the view sets it from its width).</summary>
    public int GalleryColumns
    {
        get => _galleryColumns;
        set { value = Math.Clamp(value, 1, 12); if (SetProperty(ref _galleryColumns, value)) RebuildRows(); }
    }

    /// <summary>Phone-shaped thumbnails (an app's screens, or the Studio set to a mobile width).</summary>
    public bool IsPhoneLayout => IsSimulated || Device == "mobile";
    public double CardWidth => IsPhoneLayout ? 184 : 272;
    public double ThumbHeight => IsPhoneLayout ? 360 : 170;

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(IsPhoneLayout));
        OnPropertyChanged(nameof(CardWidth));
        OnPropertyChanged(nameof(ThumbHeight));
    }

    private string _baseUrlText = "";
    public string BaseUrlText { get => _baseUrlText; set => SetProperty(ref _baseUrlText, value ?? ""); }

    private string _newWebUrl = "";
    public string NewWebUrl { get => _newWebUrl; set => SetProperty(ref _newWebUrl, value ?? ""); }

    /// <summary>The site lives on the internet: thumbnails are only taken when asked (each one is a visit).</summary>
    public bool IsRemoteSite => IsServerSite && !PageShotStore.IsLocalHost(SiteBaseUrl);
    public bool NeedsServerForShots => !IsSimulated && !IsServerSite && _studio.CurrentBaseUrl == null;

    private string? SiteBaseUrl => IsServerSite
        ? _store?.State.BaseUrl ?? _studio.ServerBaseUrl ?? _catalog?.SuggestedBaseUrl
        : _studio.CurrentBaseUrl;

    private (int W, int H) ShotSize => Device switch
    {
        "tablet" => (768, 1024),
        "mobile" => (390, 844),
        _ => (1280, 800),
    };
    private string DeviceKey => Device is "laptop" ? "desktop" : Device;

    // ───────────────────────────── discovery ─────────────────────────────

    private void ResetPages()
    {
        StopWatchingAppSources();
        _renderWanted.Clear();
        _compileErrors.Clear();
        CompileError = null;
        _checkCts?.Cancel();
        _selfWrites.Clear();
        _pendingFocus = null;
        _swatches = null;
        _palette = null;
        EndSession();
        CheckPassed = null;
        CheckFindings.Clear();
        _pagesCts.Cancel();
        _pagesCts = new CancellationTokenSource();
        lock (_queue) _queue.Clear();
        _allCards.Clear();
        _loaded.Clear();
        PageRows.Clear();
        PageSections.Clear();
        ScreenCards.Clear();
        _catalog = null;
        _store = null;
        _openWhenServing = null;
        StageCard = null;
        PagesSummary = "";
        _showPages = false;
        OnPropertyChanged(nameof(ShowPages));
        NotifyLayout();
        OnPropertyChanged(nameof(IsRemoteSite));
        OnPropertyChanged(nameof(NeedsServerForShots));
    }

    /// <summary>Read the project's pages (once per project; again on Refresh) and lay out the cards.</summary>
    public async Task OpenPagesAsync(bool rediscover = false)
    {
        if (!HasProject || IsUnsupported) return;
        if (_catalog != null && !rediscover)
        {
            // back to the gallery: if the project changed meanwhile, its pages are read again and old shots re-taken
            RebuildRows();
            var root = _studio.Root;
            DateTime stamp;
            try { stamp = await Task.Run(() => PageShotStore.ProjectStamp(root, _pagesCts.Token)); }
            catch (OperationCanceledException) { return; }
            if (stamp <= _projectStamp || !string.Equals(root, _studio.Root, StringComparison.OrdinalIgnoreCase)) return;
            rediscover = true;
        }
        var info = _studio.Project;
        var ct = _pagesCts.Token;
        string lang = _loc.CurrentLanguage;
        IsDiscovering = true;
        try
        {
            var (catalog, stamp) = await Task.Run(() => (PageCatalog.Discover(info, lang, ct), PageShotStore.ProjectStamp(info.Root, ct)), ct);
            if (ct.IsCancellationRequested || !string.Equals(info.Root, _studio.Root, StringComparison.OrdinalIgnoreCase)) return;
            _catalog = catalog;
            _projectStamp = stamp;
            _store ??= new PageShotStore(_studio.DataRoot, info.Root);
            if (IsServerSite)
            {
                BaseUrlText = _store.State.BaseUrl ?? _studio.ServerBaseUrl ?? catalog.SuggestedBaseUrl ?? "";
                _studio.ServerBaseUrl = _store.State.BaseUrl ?? _studio.ServerBaseUrl ?? catalog.SuggestedBaseUrl;
            }
            BuildCards();
            _store.Prune(_allCards.Select(c => c.Page.Id).ToList());
            if (IsSimulated)
            {
                ApplyCachedRenders();
                WatchAppSources();
            }
            if (IsSimulated && StageCard == null && ScreenCards.FirstOrDefault(c => c.Page.Route != null) is { } first) OpenStage(first, closeGallery: false);
            _ = SketchAllAsync();
            if (IsSimulated)
            {
                // the real screens: what is missing or older than the code (asked once per project)
                var order = new List<PageCard>();
                if (_stageCard != null) order.Add(_stageCard);
                order.AddRange(ScreenCards.Where(c => !ReferenceEquals(c, _stageCard)));
                RenderScreens(order.Where(c => !c.HasThumb || c.IsStale), ask: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Warn("UiStudio", "page discovery failed", ex);
            Status(T("studio.pages.failed"), error: true);
        }
        finally { IsDiscovering = false; }
    }

    private void BuildCards()
    {
        _allCards.Clear();
        _loaded.Clear();
        ScreenCards.Clear();
        if (_catalog == null || _store == null) return;
        string root = _studio.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var p in _catalog.Pages) _allCards.Add(new PageCard(p, root));
        foreach (var w in _store.State.WebPages)
            _allCards.Add(new PageCard(new StudioPage
            {
                Id = "url:" + w.Url, Kind = StudioPageKind.External, Url = w.Url, Title = w.Title, Route = null,
                Risky = PageSafety.IsRisky(SafePath(w.Url), null),
            }, root));
        foreach (var c in _allCards.Where(c => c.IsScreen)) ScreenCards.Add(c);
        foreach (var c in _allCards) ApplyCachedThumb(c, loadImage: false);
        BuildSections();
        RebuildRows();
        MarkCurrentPage(_studio.PageUrl);
    }

    private static string SafePath(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;

    private string? _section;

    private void BuildSections()
    {
        PageSections.Clear();
        if (_allCards.Count == 0) return;
        PageSections.Add(new PageSection { Key = "", Label = T("studio.pages.all"), Count = _allCards.Count, IsSelected = true });
        _section = null;
        if (IsSimulated)
        {
            int routed = _allCards.Count(c => c.Page.Route != null);
            if (routed > 0 && routed < _allCards.Count)
            {
                PageSections.Add(new PageSection { Key = "~routed", Label = T("studio.pages.routed"), Count = routed });
                PageSections.Add(new PageSection { Key = "~other", Label = T("studio.pages.otherScreens"), Count = _allCards.Count - routed });
            }
            return;
        }
        foreach (var g in _allCards.Where(c => !c.IsExternal && c.Page.Route != null)
                     .GroupBy(c => FirstSegment(c.Page.Route!)).Where(g => g.Key.Length > 0 && g.Count() >= 2)
                     .OrderByDescending(g => g.Count()).Take(10))
            PageSections.Add(new PageSection { Key = "/" + g.Key, Label = "/" + g.Key, Count = g.Count() });
        int withParams = _allCards.Count(c => c.NeedsParams);
        if (withParams > 0) PageSections.Add(new PageSection { Key = "~params", Label = T("studio.pages.withParams"), Count = withParams });
        int web = _allCards.Count(c => c.IsExternal);
        if (web > 0) PageSections.Add(new PageSection { Key = "~web", Label = T("studio.pages.webPages"), Count = web });
    }

    private static string FirstSegment(string route) => route.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } s && !s.StartsWith(':') ? s : "";

    private void PickSection(PageSection? s)
    {
        if (s == null) return;
        foreach (var x in PageSections) x.IsSelected = ReferenceEquals(x, s);
        _section = s.Key.Length == 0 ? null : s.Key;
        RebuildRows();
    }

    private bool Matches(PageCard c)
    {
        if (_section != null)
        {
            bool inSection = _section switch
            {
                "~params" => c.NeedsParams,
                "~web" => c.IsExternal,
                "~routed" => c.Page.Route != null,
                "~other" => c.Page.Route == null,
                _ => c.Page.Route != null && ("/" + FirstSegment(c.Page.Route)).Equals(_section, StringComparison.OrdinalIgnoreCase),
            };
            if (!inSection) return false;
        }
        string q = _pageSearch.Trim();
        if (q.Length == 0) return true;
        return c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.RouteText.Contains(q, StringComparison.OrdinalIgnoreCase)
               || c.SourceText.Contains(q, StringComparison.OrdinalIgnoreCase) || (c.Page.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void RebuildRows()
    {
        PageRows.Clear();
        OnPropertyChanged(nameof(ShowDiscovering));
        var shown = _allCards.Where(Matches).ToList();
        for (int i = 0; i < shown.Count; i += _galleryColumns)
        {
            var row = new PageRow();
            row.Cards.AddRange(shown.Skip(i).Take(_galleryColumns));
            PageRows.Add(row);
        }
        OnPropertyChanged(nameof(ShowDiscovering));
        OnPropertyChanged(nameof(ShowPagesEmpty));
        UpdateSummary(shown.Count);
    }

    private void UpdateSummary(int shown = -1)
    {
        if (shown < 0) shown = _allCards.Count(Matches);
        int withThumb = _allCards.Count(c => c.Page.Kind == StudioPageKind.Screen ? c.HasSketch : _store?.Get(c.Page.Id, DeviceKey) != null);
        PagesSummary = shown == _allCards.Count
            ? T("studio.pages.summary", _allCards.Count, withThumb)
            : T("studio.pages.summaryFiltered", shown, _allCards.Count, withThumb);
    }

    // ───────────────────────────── thumbnails ─────────────────────────────

    /// <summary>The view tells which cards are on screen: their thumbnails load, and the missing or stale ones are shot.</summary>
    public void NoteVisible(IEnumerable<PageCard> cards)
    {
        foreach (var c in cards)
        {
            if (c.IsScreen) continue;   // sketched in the background
            ApplyCachedThumb(c, loadImage: true);
            if (!_showPages) continue;
            bool needShot = c.ThumbDevice != DeviceKey || c.IsStale || !c.HasThumb;
            if (!needShot || c.IsBusy) continue;
            if (AutoShootable(c, out var why)) Enqueue(c, front: true);
            else if (!c.HasThumb) c.SetState(why.State, why.Text);
        }
        Pump();
    }

    private void ApplyCachedThumb(PageCard c, bool loadImage)
    {
        if (_store == null || c.IsScreen) return;
        var e = _store.Get(c.Page.Id, DeviceKey);
        if (e == null)
        {
            if (c.ThumbDevice != null && c.ThumbDevice != DeviceKey) { c.Thumb = null; c.ThumbDevice = null; }
            return;
        }
        c.IsStale = e.CapturedUtc < _projectStamp && !c.IsExternal;
        if (e.Title is { Length: > 0 } t && c.IsExternal) c.Title = t;
        if (c.State is "none" or "manual" or "noServer" or "risky") c.SetState("done", ShotText(e));
        if (!loadImage) return;
        TouchThumb(c);
        if (c.HasThumb && c.ThumbDevice == DeviceKey) return;
        c.Thumb = LoadImage(_store.ImagePath(e));
        c.ThumbDevice = c.Thumb != null ? DeviceKey : null;
    }

    // A site can have 1,500 pages: only the pictures seen lately stay decoded (≈0.7 MB each); the rest reload from disk.
    private const int MaxLoadedThumbs = 160;
    private readonly LinkedList<PageCard> _loaded = new();

    private void TouchThumb(PageCard c)
    {
        var node = _loaded.Find(c);
        if (node != null) _loaded.Remove(node);
        _loaded.AddFirst(c);
        while (_loaded.Count > MaxLoadedThumbs)
        {
            var old = _loaded.Last!.Value;
            _loaded.RemoveLast();
            old.Thumb = null;
            old.ThumbDevice = null;
        }
    }

    private string ShotText(PageShotEntry e)
    {
        string when = e.CapturedUtc.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.GetCultureInfo(_loc.CurrentLanguage == "th" ? "th-TH" : "en-US"));
        return e.Status >= 400 ? T("studio.pages.httpError", e.Status, when) : T("studio.pages.shotAt", when);
    }

    internal static ImageSource? LoadImage(string path, int decodeWidth = 520)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private record struct Why(string State, string Text);

    /// <summary>May the gallery open this page on its own? Never a page that needs a value, may act when fetched,
    /// or lives on the internet (each thumbnail is a real visit) — those wait for the user's click.</summary>
    private bool AutoShootable(PageCard c, out Why why)
    {
        why = default;
        if (Shooter == null) { why = new Why("none", ""); return false; }
        if (c.NeedsParams) { why = new Why("params", T("studio.pages.needsParams", string.Join(", ", c.Page.Params.Select(p => ":" + p)))); return false; }
        string? url = UrlFor(c);
        if (url == null) { why = new Why("noServer", T(IsServerSite ? "studio.pages.noBase" : "studio.pages.noServer")); return false; }
        if (c.Page.Risky) { why = new Why("risky", T("studio.pages.risky")); return false; }
        if (!PageShotStore.IsLocalHost(url)) { why = new Why("manual", T("studio.pages.manual")); return false; }
        return true;
    }

    private string? UrlFor(PageCard c) => c.Page.UrlOn(c.IsExternal ? null : SiteBaseUrl);

    private void Enqueue(PageCard c, bool front)
    {
        lock (_queue)
        {
            var existing = _queue.Find(c);
            if (existing != null) _queue.Remove(existing);
            if (front) _queue.AddLast(c); else _queue.AddFirst(c);   // the pump takes from the end: newest first
        }
        if (c.State != "shooting") c.SetState("queued", T("studio.pages.queued"));
    }

    private async void Pump()
    {
        if (_pumping || Shooter == null || _store == null) return;
        _pumping = true;
        var ct = _pagesCts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PageCard? card;
                lock (_queue)
                {
                    card = _queue.Last?.Value;
                    if (card != null) _queue.RemoveLast();
                }
                if (card == null) break;
                string? url = UrlFor(card);
                if (url == null) continue;
                card.SetState("shooting", T("studio.pages.shooting"));
                var (w, h) = ShotSize;
                string device = DeviceKey;   // the size this shot is taken at, even if the user switches meanwhile
                PageShot? shot = null;
                try { shot = await Shooter(url, w, h, ct); }
                catch (Exception ex) { _log?.Warn("UiStudio", "page shot failed " + url, ex); }
                if (ct.IsCancellationRequested || _store == null) break;
                if (shot == null) { card.SetState("failed", T("studio.pages.failedShot")); continue; }
                var entry = _store.Save(card.Page.Id, device, shot.Jpeg, shot.FinalUrl, shot.Title, shot.Status);
                if (device != DeviceKey) { card.SetState("none", ""); continue; }   // kept for that size; this card waits for its own
                card.Thumb = LoadImage(_store.ImagePath(entry));
                card.ThumbDevice = DeviceKey;
                TouchThumb(card);
                card.IsStale = false;
                if (card.IsExternal && entry.Title is { Length: > 0 } t)
                {
                    card.Title = t;
                    var w2 = _store.State.WebPages.FirstOrDefault(x => x.Url == card.Page.Url);
                    if (w2 != null && w2.Title != t) { w2.Title = t; _store.SaveState(); }
                }
                card.SetState("done", ShotText(entry));
                UpdateSummary();
            }
        }
        finally { _pumping = false; }
    }

    /// <summary>Draw every screen of a simulated app, one by one in the background.</summary>
    private async Task SketchAllAsync()
    {
        if (_sketching || _catalog?.Flutter is not { } flutter) return;
        _sketching = true;
        var ct = _pagesCts.Token;
        try
        {
            foreach (var c in _allCards.Where(c => c.IsScreen && !c.Sketched).ToList())
            {
                if (ct.IsCancellationRequested) break;
                ScreenSketch? sk = null;
                try { sk = await Task.Run(() => flutter.Sketch(c.Page), ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log?.Warn("UiStudio", "sketch failed " + c.Page.Id, ex); }
                c.Sketched = true;
                c.Sketch = sk;
                if (sk?.Title is { Length: > 0 } t && t != "…") c.Title = t;
                if (!c.HasThumb && !c.IsBusy) c.SetState(sk != null ? "sketch" : "failed", sk != null ? T("studio.pages.simulated") : T("studio.pages.noSketch"));
                if (ReferenceEquals(c, StageCard)) StageSketch = sk;
            }
            UpdateSummary();
        }
        finally { _sketching = false; }
    }

    private void OnDeviceChangedForPages()
    {
        NotifyLayout();
        if (_catalog == null) return;
        lock (_queue) _queue.Clear();
        foreach (var c in _allCards.Where(c => !c.IsScreen))
        {
            c.Thumb = null;
            c.ThumbDevice = null;
            c.SetState("none", "");
        }
        RebuildRows();   // the view reports the visible cards again
    }

    // ───────────────────────────── opening a page ─────────────────────────────

    private void OpenCard(PageCard? c)
    {
        if (c == null) return;
        if (c.IsScreen) { OpenStage(c, closeGallery: true); return; }
        if (c.NeedsParams)
        {
            // the user fills the value in the address bar
            string? pattern = c.Page.Route == null ? null : (SiteBaseUrl ?? "").TrimEnd('/') + c.Page.Route;
            ShowPages = false;
            Url = pattern ?? "";
            AddressFocusRequested?.Invoke(c.Page.Params.Count > 0 ? ":" + c.Page.Params[0] : null);
            Status(T("studio.pages.fillParams", string.Join(", ", c.Page.Params.Select(p => ":" + p))));
            return;
        }
        string? url = UrlFor(c);
        if (url == null)
        {
            if (!IsServerSite && !IsSimulated)
            {
                // not served yet: start the preview server, then open this page
                _openWhenServing = c.Page;
                ShowPages = false;
                _ = StartAsync();
                return;
            }
            Status(T("studio.pages.noBase"), error: true);
            return;
        }
        if (IsServerSite && !_studio.IsServing) _ = _studio.StartAsync();
        ShowPages = false;
        NavigateTo?.Invoke(url);
    }

    /// <summary>The preview server came up: open the page the user picked before it was running.</summary>
    internal void OnServingForPages()
    {
        OnPropertyChanged(nameof(NeedsServerForShots));
        if (_studio.CurrentBaseUrl == null) return;
        if (_openWhenServing is { } p && p.UrlOn(_studio.CurrentBaseUrl) is { } url)
        {
            _openWhenServing = null;
            NavigateTo?.Invoke(url);
        }
        if (_showPages) RebuildRows();
    }

    /// <summary>Which card is the page shown now (compared by path, so it holds across a server restart's new port).</summary>
    private void MarkCurrentPage(string? url)
    {
        if (_allCards.Count == 0) return;
        string? path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.TrimEnd('/') : null;
        string? local = SiteBaseUrl;
        foreach (var c in _allCards)
        {
            string? cu = UrlFor(c);
            c.IsCurrent = path != null && cu != null && Uri.TryCreate(cu, UriKind.Absolute, out var cuu)
                          && (c.IsExternal ? string.Equals(cu, url, StringComparison.OrdinalIgnoreCase)
                                           : local != null && string.Equals(cuu.AbsolutePath.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase) && cuu.Authority == u!.Authority);
        }
    }

    /// <summary>A web page opened from the address bar joins the gallery's web pages.</summary>
    private void NoteTypedUrl(string url)
    {
        if (_store == null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        if (SiteBaseUrl is { } b && Uri.TryCreate(b, UriKind.Absolute, out var bu) && bu.Authority == u.Authority) return;   // a page of the project
        if (_store.AddWebPage(url, null) && _catalog != null) { BuildCards(); }
    }

    private void AddWebPage()
    {
        string u = NewWebUrl.Trim();
        if (u.Length == 0) return;
        if (!u.Contains("://", StringComparison.Ordinal)) u = (u.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || u.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + u;
        if (_store == null || !_store.AddWebPage(u, null)) { Status(T("studio.err.badUrl"), error: true); return; }
        NewWebUrl = "";
        BuildCards();
        if (_allCards.FirstOrDefault(c => c.IsExternal && c.Page.Url == u) is { } card) { Enqueue(card, front: true); Pump(); }
    }

    private void RemoveWebPage(PageCard? c)
    {
        if (c == null || !c.IsExternal || _store == null) return;
        _store.RemoveWebPage(c.Page.Url!);
        BuildCards();
    }

    private void ShootCard(PageCard? c)
    {
        if (c == null || c.IsScreen) return;
        if (UrlFor(c) is not { } url) { Status(T(c.NeedsParams ? "studio.pages.fillParamsShort" : "studio.pages.noServer"), error: true); return; }
        if (c.Page.Risky)
        {
            int choice = Views.Editor.ChoiceDialog.Show(T("studio.pages.riskyTitle"), T("studio.pages.riskyBody", url),
                new[] { T("studio.pages.shootAnyway"), T("studio.cancel") }, 1);
            if (choice != 0) return;
        }
        Enqueue(c, front: true);
        Pump();
    }

    private void ShootAll()
    {
        if (_catalog == null) return;
        var list = _allCards.Where(Matches).Where(c => !c.IsScreen && !c.NeedsParams && !c.Page.Risky && UrlFor(c) != null).ToList();
        if (list.Count == 0) { Status(T(NeedsServerForShots ? "studio.pages.noServer" : "studio.pages.nothingToShoot")); return; }
        var remote = list.Where(c => !PageShotStore.IsLocalHost(UrlFor(c))).ToList();
        if (remote.Count > 0)
        {
            string hosts = string.Join(", ", remote.Select(c => new Uri(UrlFor(c)!).Host).Distinct().Take(3));
            int choice = Views.Editor.ChoiceDialog.Show(T("studio.pages.remoteTitle"), T("studio.pages.remoteBody", remote.Count, hosts),
                new[] { T("studio.pages.shootAll"), T("studio.cancel") }, 1);
            if (choice != 0) return;
        }
        foreach (var c in Enumerable.Reverse(list)) Enqueue(c, front: false);
        Pump();
    }

    private void ApplyBaseUrl()
    {
        string u = BaseUrlText.Trim();
        if (!u.Contains("://", StringComparison.Ordinal)) u = "http://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || _store == null)
        {
            Status(T("studio.err.badUrl"), error: true);
            return;
        }
        u = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        bool changed = !string.Equals(_store.State.BaseUrl, u, StringComparison.OrdinalIgnoreCase);
        _store.State.BaseUrl = u;
        _studio.ServerBaseUrl = u;
        BaseUrlText = u;
        if (changed) { _store.ClearShots(); _store.SaveState(); BuildCards(); }
        OnPropertyChanged(nameof(IsRemoteSite));
        Status(T("studio.pages.baseSet", u));
        if (_studio.IsServing) NavigateTo?.Invoke(u);
    }

    private void OpenCardSource(PageCard? c)
    {
        if (c?.Page.SourcePath is not { } path || !File.Exists(path)) return;
        _ = _editor.OpenAtLineAsync(path, Math.Max(1, c.Page.SourceLine));
        OpenPageRequested?.Invoke("Code");
    }

    /// <summary>The view focuses the address bar and selects this text (a route parameter to fill in).</summary>
    public event Action<string?>? AddressFocusRequested;

    // ───────────────────────────── simulated stage ─────────────────────────────

    private PageCard? _stageCard;
    public PageCard? StageCard
    {
        get => _stageCard;
        set
        {
            if (!SetProperty(ref _stageCard, value)) return;
            if (value != null) EnsureSketch(value);
            StageSketch = value?.Sketch;
            StageBox = null;
            LoadStageRender();
            StageNode = null;
            OnPropertyChanged(nameof(HasStage));
            OnPropertyChanged(nameof(StageTitle));
            OnPropertyChanged(nameof(StageRoute));
            OnPropertyChanged(nameof(StageFile));
            foreach (var c in ScreenCards) c.IsCurrent = ReferenceEquals(c, value);
        }
    }
    public bool HasStage => _stageCard != null;
    public string StageTitle => _stageCard?.Title ?? "";
    public string StageRoute => _stageCard?.Page.Route ?? _stageCard?.Page.Detail ?? "";
    public string StageFile => _stageCard?.SourceText ?? "";

    private ScreenSketch? _stageSketch;
    public ScreenSketch? StageSketch { get => _stageSketch; set => SetProperty(ref _stageSketch, value); }

    private SketchNode? _stageNode;
    public SketchNode? StageNode
    {
        get => _stageNode;
        set
        {
            if (!SetProperty(ref _stageNode, value)) return;
            if (value != null && _stageBox != null) { _stageBox = null; OnPropertyChanged(nameof(StageBox)); }
            if (value != null) _pendingFocus = null;
            foreach (var n in new[] { nameof(HasStageNode), nameof(StageNodeLabel), nameof(StageNodeText), nameof(StageNodeWhere), nameof(StageNodeUse) })
                OnPropertyChanged(n);
            RefreshTools();
        }
    }
    public bool HasStageNode => _stageNode != null || _stageBox != null;
    public string StageNodeLabel => _stageBox != null ? _stageBox.Widget : _stageNode == null ? "" : (_stageNode.Widget ?? _stageNode.Kind.ToString());
    public string StageNodeText => _stageBox != null ? "" : _stageNode?.Text ?? _stageNode?.Items.FirstOrDefault() ?? "";
    public string StageNodeWhere => _stageBox != null ? Rel(_stageBox.File) + ":" + _stageBox.Line
        : _stageNode?.File is { } f ? Rel(f) + ":" + _stageNode.Line : "";
    public string StageNodeUse => _stageBox != null ? (UseSiteOf(_stageBox) is { } u ? T("studio.stage.usedAt", Rel(u.File) + ":" + u.Line) : "")
        : _stageNode?.UseFile is { } f ? T("studio.stage.usedAt", Rel(f) + ":" + _stageNode.UseLine) : "";

    private string Rel(string full)
    {
        string root = _studio.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..].Replace('\\', '/') : Path.GetFileName(full);
    }

    private void OpenStage(PageCard c, bool closeGallery)
    {
        StageCard = c;
        if (closeGallery) ShowPages = false;
    }

    /// <summary>Draw a screen now when the background pass hasn't reached it yet (the user opened it first).</summary>
    private void EnsureSketch(PageCard c)
    {
        if (c.Sketched || !c.IsScreen || _catalog?.Flutter is not { } f) return;
        try { c.Sketch = f.Sketch(c.Page); } catch (Exception ex) { _log?.Warn("UiStudio", "sketch failed", ex); }
        c.Sketched = true;
        if (c.Sketch?.Title is { Length: > 0 } t && t != "…") c.Title = t;
        c.SetState(c.Sketch != null ? "sketch" : "failed", c.Sketch != null ? T("studio.pages.simulated") : T("studio.pages.noSketch"));
    }

    /// <summary>A click on the simulated screen: that block is selected (Ctrl+click opens its line at once).</summary>
    public void PickStageNode(SketchNode node, bool open)
    {
        StageNode = node;
        if (open) OpenStageNode();
    }

    private void OpenStageNode()
    {
        var (file, line) = _stageBox != null ? (_stageBox.File, _stageBox.Line) : _stageNode?.Target ?? (null, 0);
        file ??= _stageCard?.Page.SourcePath;
        if (file == null || !File.Exists(file)) return;
        _ = _editor.OpenAtLineAsync(file, Math.Max(1, line));
        OpenPageRequested?.Invoke("Code");
    }

    private string _stageRequest = "";
    public string StageRequest { get => _stageRequest; set => SetProperty(ref _stageRequest, value ?? ""); }

    private void AskAiAboutStage()
    {
        if (_stageCard == null || string.IsNullOrWhiteSpace(StageRequest)) return;
        if (AskAi(BuildStagePrompt(StageRequest.Trim()))) StageRequest = "";
    }

    /// <summary>The screen, its route and file, and the block the user picked (where it is written and where the
    /// screen uses it) — so the AI edits the right widget without searching the app.</summary>
    internal string BuildStagePrompt(string request, bool revising = false)
    {
        var c = _stageCard!;
        var sb = new StringBuilder();
        sb.AppendLine(revising
            ? "[UI Studio] More changes to the screen you just changed: the user watched it drawn and wants this too."
            : $"[UI Studio] Change a screen of this {(_studio.Project.Framework is { Length: > 0 } fw ? fw : "app")} app.");
        sb.AppendLine();
        sb.AppendLine("Request: " + request);
        sb.AppendLine();
        sb.AppendLine($"Screen: {c.Page.Detail} — \"{c.Title}\"" + (c.Page.Route != null ? $", route {c.Page.Route}" : ""));
        if (c.Page.SourcePath != null) sb.AppendLine($"Screen file: {Rel(c.Page.SourcePath)} (class at line {c.Page.SourceLine})");
        if (_stageBox is { } box)
        {
            sb.AppendLine($"The part the user picked: <{box.Widget}> written at {Rel(box.File)}:{box.Line}:{box.Column}");
            if (UseSiteOf(box) is { } use) sb.AppendLine($"  used by the screen at {Rel(use.File)}:{use.Line}");
            if (StageProblems.Count > 0) sb.AppendLine("Problems Flutter reported while drawing this screen: " + string.Join(" | ", StageProblems.Take(5)));
        }
        else if (_stageNode is { } n)
        {
            sb.AppendLine($"The part the user picked: <{n.Widget ?? n.Kind.ToString()}>" + (n.Text is { Length: > 0 } t && t != "…" ? $" showing \"{t}\"" : ""));
            if (n.File != null) sb.AppendLine($"  written at {Rel(n.File)}:{n.Line}");
            if (n.UseFile != null) sb.AppendLine($"  used by the screen at {Rel(n.UseFile)}:{n.UseLine}");
        }
        sb.AppendLine();
        sb.AppendLine("Edit only what the request needs. If the part is a shared widget used by other screens, change it only when the request is about every screen; otherwise change this screen's use of it. Keep the app's existing theme and widget kit.");
        if (IsSimulated)
            sb.AppendLine("The UI Studio draws the screen again with Flutter every time a file is saved, and the user is watching: save each step as you finish it. Afterwards the user reviews the change (keep / undo all / ask for more) and the save check runs dart analyze and draws every screen.");
        return sb.ToString();
    }

    // ───────────────────────────── commands ─────────────────────────────

    private ICommand? _showPagesCommand, _closePagesCommand, _refreshPagesCommand, _shootAllCommand, _openCardCommand, _shootCardCommand,
        _cardSourceCommand, _removeWebPageCommand, _addWebPageCommand, _applyBaseUrlCommand, _pickSectionCommand, _openStageNodeCommand,
        _askAiStageCommand, _openScreenCommand;

    public ICommand ShowPagesCommand => _showPagesCommand ??= new RelayCommand(() => ShowPages = !ShowPages, () => HasProject && !IsUnsupported);
    public ICommand ClosePagesCommand => _closePagesCommand ??= new RelayCommand(() => ShowPages = false);
    public ICommand RefreshPagesCommand => _refreshPagesCommand ??= new AsyncRelayCommand(async () =>
    {
        foreach (var c in _allCards) { c.Sketched = false; c.Sketch = null; }
        await OpenPagesAsync(rediscover: true);
    });
    public ICommand ShootAllCommand => _shootAllCommand ??= new RelayCommand(ShootAll, () => !IsSimulated);
    public ICommand OpenCardCommand => _openCardCommand ??= new RelayCommand<PageCard>(OpenCard);
    public ICommand ShootCardCommand => _shootCardCommand ??= new RelayCommand<PageCard>(ShootCard);
    public ICommand CardSourceCommand => _cardSourceCommand ??= new RelayCommand<PageCard>(OpenCardSource);
    public ICommand RemoveWebPageCommand => _removeWebPageCommand ??= new RelayCommand<PageCard>(RemoveWebPage);
    public ICommand AddWebPageCommand => _addWebPageCommand ??= new RelayCommand(AddWebPage, () => !string.IsNullOrWhiteSpace(NewWebUrl));
    public ICommand ApplyBaseUrlCommand => _applyBaseUrlCommand ??= new RelayCommand(ApplyBaseUrl);
    public ICommand PickSectionCommand => _pickSectionCommand ??= new RelayCommand<PageSection>(PickSection);
    public ICommand OpenStageNodeCommand => _openStageNodeCommand ??= new RelayCommand(OpenStageNode, () => _stageCard != null);
    public ICommand AskAiStageCommand => _askAiStageCommand ??= new RelayCommand(AskAiAboutStage, () => _stageCard != null && !string.IsNullOrWhiteSpace(StageRequest));
    public ICommand OpenScreenCommand => _openScreenCommand ??= new RelayCommand<PageCard>(c => { if (c != null) OpenStage(c, closeGallery: true); });
}
