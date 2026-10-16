using System.Collections.ObjectModel;
using System.Windows.Input;
using CluadeX.Services;

namespace CluadeX.ViewModels;

/// <summary>The Watch page — see <see cref="WatchService"/>.</summary>
public sealed class WatchViewModel : ViewModelBase
{
    private readonly WatchService _watch;
    private readonly ChatViewModel _chat;
    private readonly FileSystemService _fs;
    private readonly LocalizationService _loc;
    private readonly WatchWebServer? _web;

    // ─── The page for a phone on the same Wi-Fi ───
    public bool IsSharing
    {
        get => _web?.IsRunning == true;
        set
        {
            if (_web == null || value == _web.IsRunning) return;
            if (value && !_web.Start()) Status = string.Format(_loc.T("watch.web.startFailed"), _web.LastError);
            if (!value) _web.Stop();
            RaiseShare();
        }
    }
    public string PhoneUrl => _web?.PhoneUrl ?? "";
    public bool HasPhoneUrl => PhoneUrl.Length > 0;
    public System.Windows.Media.ImageSource? PhoneQr => HasPhoneUrl ? MakeQr(PhoneUrl) : null;

    private void RaiseShare()
    {
        OnPropertyChanged(nameof(IsSharing)); OnPropertyChanged(nameof(PhoneUrl));
        OnPropertyChanged(nameof(HasPhoneUrl)); OnPropertyChanged(nameof(PhoneQr));
    }

    /// <summary>A QR code of <paramref name="text"/> as a WPF image (QRCoder, PNG bytes → frozen bitmap).</summary>
    public static System.Windows.Media.ImageSource MakeQr(string text)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new System.IO.MemoryStream(png);
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public ICommand NewKeyCommand { get; }
    public ICommand CopyPhoneUrlCommand { get; }

    public ObservableCollection<WatchedApp> Apps => _watch.Apps;
    public bool HasApps => Apps.Count > 0;

    private WatchedApp? _selected;
    public WatchedApp? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selected != null;

    /// <summary>Projects that could be added: every project the app knows, not yet watched.</summary>
    public ObservableCollection<string> Candidates { get; } = new();
    private string? _candidate;
    public string? Candidate { get => _candidate; set => SetProperty(ref _candidate, value); }

    private string _status = "";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private bool _busyAll;
    public bool IsTestingAll { get => _busyAll; private set => SetProperty(ref _busyAll, value); }

    private WatchedApp? _pendingRemove;
    public WatchedApp? PendingRemove
    {
        get => _pendingRemove;
        set { if (SetProperty(ref _pendingRemove, value)) OnPropertyChanged(nameof(IsRemoveConfirmOpen)); }
    }
    public bool IsRemoveConfirmOpen => _pendingRemove != null;

    public ICommand AddCurrentCommand { get; }
    public ICommand AddCandidateCommand { get; }
    public ICommand SelectCommand { get; }
    public ICommand RunTestsCommand { get; }
    public ICommand TestAllCommand { get; }
    public ICommand StartAppCommand { get; }
    public ICommand StopAppCommand { get; }
    public ICommand CheckUrlCommand { get; }
    public ICommand AskAiCommand { get; }
    public ICommand AskRemoveCommand { get; }
    public ICommand ConfirmRemoveCommand { get; }
    public ICommand CancelRemoveCommand { get; }
    public ICommand SaveCommand { get; }

    public WatchViewModel(WatchService watch, ChatViewModel chat, FileSystemService fs, LocalizationService loc, WatchWebServer? web = null)
    {
        _web = web;
        NewKeyCommand = new RelayCommand(() =>
        {
            if (_web == null) return;
            _web.NewKey();
            RaiseShare();
            Status = _loc.T("watch.web.newKeyDone");
        });
        CopyPhoneUrlCommand = new RelayCommand(() =>
        {
            if (!HasPhoneUrl) return;
            try { System.Windows.Clipboard.SetText(PhoneUrl); Status = _loc.T("watch.web.copied"); } catch { }
        });
        _watch = watch;
        _chat = chat;
        _fs = fs;
        _loc = loc;
        Apps.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasApps)); RefreshCandidates(); };
        AddCurrentCommand = new RelayCommand(() =>
        {
            if (!_fs.HasWorkingDirectory) { Status = _loc.T("watch.noProject"); return; }
            Selected = _watch.Add(_fs.WorkingDirectory);
        });
        AddCandidateCommand = new RelayCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(Candidate)) return;
            Selected = _watch.Add(Candidate);
            Candidate = null;
        });
        SelectCommand = new RelayCommand<WatchedApp>(a => { if (a != null) Selected = a; });
        RunTestsCommand = new AsyncRelayCommand<WatchedApp>(async a => { if (a != null) await _watch.RunTestsAsync(a); });
        TestAllCommand = new AsyncRelayCommand(TestAllAsync);
        StartAppCommand = new AsyncRelayCommand<WatchedApp>(async a =>
        {
            if (a == null) return;
            string? msg = await _watch.StartAppAsync(a);
            if (!string.IsNullOrEmpty(msg) && a.RunState == "crashed") Status = msg;
            if (!string.IsNullOrWhiteSpace(a.Url)) await _watch.CheckUrlAsync(a);
        });
        StopAppCommand = new AsyncRelayCommand<WatchedApp>(async a => { if (a != null) await _watch.StopAppAsync(a); });
        CheckUrlCommand = new AsyncRelayCommand<WatchedApp>(async a => { if (a != null) await _watch.CheckUrlAsync(a); });
        AskAiCommand = new AsyncRelayCommand<WatchedApp>(async a =>
        {
            if (a == null) return;
            await _chat.PrepareMessageInProjectAsync(a.ProjectPath, WatchService.Evidence(a, _loc.CurrentLanguage == "th"));
        });
        AskRemoveCommand = new RelayCommand<WatchedApp>(a => PendingRemove = a);
        CancelRemoveCommand = new RelayCommand(() => PendingRemove = null);
        ConfirmRemoveCommand = new RelayCommand(() =>
        {
            if (_pendingRemove is { } a) { _watch.Remove(a); if (Selected == a) Selected = Apps.FirstOrDefault(); }
            PendingRemove = null;
        });
        SaveCommand = new RelayCommand(() => { _watch.Save(); Status = _loc.T("watch.saved"); });
        Selected = Apps.FirstOrDefault();
        RefreshCandidates();
    }

    /// <summary>Every project the app knows (opened folders, chats) that is not watched yet.</summary>
    public void RefreshCandidates()
    {
        var watched = new HashSet<string>(Apps.Select(a => a.ProjectPath), StringComparer.OrdinalIgnoreCase);
        var known = (ProjectRegistryService.Current?.Recent(200).Select(e => e.Path) ?? Enumerable.Empty<string>())
            .Concat(_chat.Sessions.Select(s => s.ProjectPath ?? ""))
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !watched.Contains(p) && System.IO.Directory.Exists(p)).ToList();
        Candidates.Clear();
        foreach (var p in known) Candidates.Add(p);
    }

    private async Task TestAllAsync()
    {
        if (IsTestingAll) return;
        IsTestingAll = true;
        try
        {
            foreach (var a in Apps.Where(a => !string.IsNullOrWhiteSpace(a.TestCommand)).ToList())
                await _watch.RunTestsAsync(a);
            int bad = Apps.Count(a => a.HasProblem);
            Status = bad == 0 ? _loc.T("watch.allGood") : string.Format(_loc.T("watch.someBad"), bad);
        }
        finally { IsTestingAll = false; }
    }
}
