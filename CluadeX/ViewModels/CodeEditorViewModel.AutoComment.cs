using System.IO;
using System.Windows.Input;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.AutoComment;
using CluadeX.Services.Providers;
using CluadeX.Views.Editor;

namespace CluadeX.ViewModels;

/// <summary>
/// Code Editor toolbar ▸ Auto-comment (phase 5, owner 2026-11-01): the AI reads code that already exists and explains it
/// with comments. Unlike ▸ AI comments (how the AI comments code it WRITES) this comments the file as it is. The model
/// only names lines and writes text — <see cref="AutoCommentService"/> inserts, proves the code unchanged, and the user
/// sees the result in the side-by-side diff before anything is written. Writes keep a Time Machine restore point.
/// </summary>
public partial class CodeEditorViewModel
{
    private readonly AiProviderManager? _providers;
    private CancellationTokenSource? _acmCts;
    private AutoCommentPreview? _acmPreview;
    private AutoCommentApplied? _acmLast;
    private string? _acmProject;   // the project when the run started: its restore point goes there even if another opens

    /// <summary>Test seam (like <see cref="ChoiceDialog.Override"/>): when set, the run asks this model instead of the
    /// active provider — harnesses drive the whole flow with a canned answer and no network.</summary>
    public static Func<IAutoCommentModel>? ModelOverride { get; set; }
    private ICommand? _setAcmScope, _setAcmLanguage, _cancelAcm, _applyAcm, _discardAcm, _undoAcm;

    public ICommand SetAutoCommentScopeCommand => _setAcmScope ??= new RelayCommand<string>(v =>
    { _settings.UpdateSettings(s => s.AutoCommentScope = v ?? "functions"); RaiseAutoComment(); });
    public ICommand SetAutoCommentLanguageCommand => _setAcmLanguage ??= new RelayCommand<string>(v =>
    { _settings.UpdateSettings(s => s.AutoCommentLanguage = CodeCommentPolicy.NormalizeLanguage(v)); RaiseAutoComment(); });
    public ICommand CancelAutoCommentCommand => _cancelAcm ??= new RelayCommand(() => _acmCts?.Cancel());
    public ICommand ApplyAutoCommentCommand => _applyAcm ??= new AsyncRelayCommand(ApplyAutoCommentAsync);
    public ICommand DiscardAutoCommentCommand => _discardAcm ??= new RelayCommand(DiscardAutoComment);
    public ICommand UndoAutoCommentCommand => _undoAcm ??= new AsyncRelayCommand(UndoAutoCommentAsync);

    private string AcmScope => _settings.Settings.AutoCommentScope ?? "functions";
    private string AcmLanguage => CodeCommentPolicy.NormalizeLanguage(_settings.Settings.AutoCommentLanguage);

    public bool AcmScopeIsFunctions => AcmScope == "functions";
    public bool AcmScopeIsChanged => AcmScope == "changed";
    public bool AcmScopeIsKey => AcmScope == "key";
    public bool AcmScopeIsFile => AcmScope == "file";
    public bool AcmScopeIsSelection => AcmScope == "selection";
    public bool AcmLangIsThai => AcmLanguage == CodeCommentPolicy.Thai;
    public bool AcmLangIsEnglish => AcmLanguage == CodeCommentPolicy.English;
    public bool AcmLangIsMatch => AcmLanguage == CodeCommentPolicy.MatchFile;

    private bool _isAutoCommenting;
    public bool IsAutoCommenting { get => _isAutoCommenting; private set { if (SetProperty(ref _isAutoCommenting, value)) RaiseAutoComment(); } }
    public bool CanStartAutoComment => !_isAutoCommenting && _acmPreview == null;

    private bool _hasAcmPreview;
    /// <summary>The diff panel is showing an auto-comment preview (Apply / Discard banner).</summary>
    public bool HasAutoCommentPreview { get => _hasAcmPreview; private set => SetProperty(ref _hasAcmPreview, value); }

    private string _acmBanner = "", _acmSkipped = "";
    public string AutoCommentBanner { get => _acmBanner; private set => SetProperty(ref _acmBanner, value); }
    public string AutoCommentSkipped { get => _acmSkipped; private set => SetProperty(ref _acmSkipped, value); }

    public bool CanUndoAutoComment => _acmLast != null && !_isAutoCommenting;
    public string AutoCommentUndoLabel => T("edt.acm.undo", _acmLast != null ? Path.GetFileName(_acmLast.FullPath) : "");

    private string _acmEstimate = "";
    /// <summary>What the run would cost with the current choice — refreshed each time the menu opens.</summary>
    public string AutoCommentEstimate { get => _acmEstimate; private set => SetProperty(ref _acmEstimate, value); }

    /// <summary>Column captions of the diff panel: HEAD / working tree, or now / with comments for a preview.</summary>
    private string? _diffLeftCaption, _diffRightCaption;
    public string DiffLeftCaption { get => _diffLeftCaption ?? T("editor.diffHead"); private set => SetProperty(ref _diffLeftCaption, value); }
    public string DiffRightCaption { get => _diffRightCaption ?? T("editor.diffWorking"); private set => SetProperty(ref _diffRightCaption, value); }

    private void RaiseAutoComment()
    {
        foreach (var name in new[]
                 {
                     nameof(AcmScopeIsFunctions), nameof(AcmScopeIsChanged), nameof(AcmScopeIsKey), nameof(AcmScopeIsFile),
                     nameof(AcmScopeIsSelection), nameof(AcmLangIsThai), nameof(AcmLangIsEnglish), nameof(AcmLangIsMatch),
                     nameof(CanStartAutoComment), nameof(CanUndoAutoComment), nameof(AutoCommentUndoLabel),
                     nameof(DiffLeftCaption), nameof(DiffRightCaption),
                 })
            OnPropertyChanged(name);
    }

    // ───────────── model choice ─────────────

    private AiProviderType ProviderType => _providers?.ActiveProviderType ?? _settings.Settings.ActiveProvider;
    private bool ProviderIsLocal => ProviderType is AiProviderType.Local or AiProviderType.LlamaServer or AiProviderType.Ollama;
    private bool ProviderIsApi => ProviderType is AiProviderType.Anthropic or AiProviderType.OpenAI or AiProviderType.Gemini;

    private string ConfiguredModel()
    {
        var p = _providers?.ActiveProvider;
        if (p != null && _settings.Settings.ProviderConfigs.TryGetValue(p.ProviderId, out var cfg) && !string.IsNullOrWhiteSpace(cfg.EffectiveModelId))
            return cfg.EffectiveModelId!.Trim();
        return ProviderType switch
        {
            AiProviderType.Anthropic => AnthropicProvider.DefaultModel,
            AiProviderType.OpenAI => OpenAiProvider.DefaultModel,
            AiProviderType.Gemini => GeminiProvider.DefaultModel,
            _ => "",
        };
    }

    /// <summary>The models the menu offers: "" = the provider's configured model, then (API providers) the others it knows.</summary>
    public List<(string Id, string Label)> AutoCommentModelChoices()
    {
        string name = _providers?.ActiveProvider.DisplayName ?? ProviderType.ToString();
        string configured = ConfiguredModel();
        var list = new List<(string, string)> { ("", T("edt.acm.modelCurrent", configured.Length > 0 ? configured : name)) };
        if (!ProviderIsApi) return list;
        var known = ProviderType switch
        {
            AiProviderType.Anthropic => AnthropicProvider.KnownModels,
            AiProviderType.OpenAI => OpenAiProvider.KnownModels,
            _ => GeminiProvider.KnownModels,
        };
        foreach (var m in known.Where(m => !string.Equals(m, configured, StringComparison.OrdinalIgnoreCase)).Take(10)) list.Add((m, m));
        return list;
    }

    /// <summary>The model in force: the saved pick when the provider still offers it, else its configured one.</summary>
    public string AutoCommentModelId
    {
        get
        {
            string m = _settings.Settings.AutoCommentModel ?? "";
            return m.Length > 0 && AutoCommentModelChoices().Any(c => c.Id == m) ? m : "";
        }
    }

    public void SetAutoCommentModel(string id)
    {
        _settings.UpdateSettings(s => s.AutoCommentModel = id ?? "");
        RefreshAutoCommentEstimate(null);
    }

    private string EffectiveModel => AutoCommentModelId is { Length: > 0 } m ? m : ConfiguredModel();

    private CommentScopeKind ScopeKind => AcmScope switch
    {
        "changed" => CommentScopeKind.Changed,
        "key" => CommentScopeKind.KeyPoints,
        "file" => CommentScopeKind.File,
        "selection" => CommentScopeKind.Selection,
        _ => CommentScopeKind.Functions,
    };

    private string ScopeLabel => T("edt.acm.scope." + AcmScope);

    private string CostText(AutoCommentRequest req)
    {
        var (input, output, usd) = AutoCommentService.Estimate(req, EffectiveModel, ProviderIsLocal);
        string price = ProviderIsLocal ? T("edt.acm.free") : !ProviderIsApi ? T("edt.acm.unpriced") : "$" + usd.ToString("0.0000");
        return T("edt.acm.estimate", EffectiveModel.Length > 0 ? EffectiveModel : ProviderType.ToString(), input, output, price);
    }

    /// <summary>Recompute the estimate for the active file (the menu calls this as it opens). Changed lines are not read
    /// here (no git call on every open): that scope's estimate counts the whole file, an upper bound.</summary>
    public void RefreshAutoCommentEstimate((int First, int Last)? selection)
    {
        var tab = ActiveTab;
        if (tab == null || CommentLanguage.For(tab.FullPath) == null || !File.Exists(tab.FullPath))
        { AutoCommentEstimate = T("edt.acm.estimateNone"); return; }
        try
        {
            var scope = ScopeKind;
            if (scope == CommentScopeKind.Selection && selection == null) scope = CommentScopeKind.File;
            var req = AutoCommentService.Prepare(tab.FullPath, new AutoCommentOptions(scope, AcmLanguage, EffectiveModel, selection));
            AutoCommentEstimate = CostText(req);
        }
        catch (AutoCommentException ex) { AutoCommentEstimate = T(ex.Key, ex.Args); }
        catch (Exception ex) { AutoCommentEstimate = ex.Message; }
    }

    // ───────────── run ─────────────

    /// <summary>Menu ▸ Analyse and comment. <paramref name="selection"/>: the editor's selected lines (1-based), if any.</summary>
    public async Task StartAutoCommentAsync((int First, int Last)? selection)
    {
        if (!CanStartAutoComment) { StatusMessage = T("edt.acm.busy"); return; }
        var tab = ActiveTab;
        if (tab == null) { StatusMessage = T("edt.acm.noFile"); return; }
        if (tab.IsDirty)
        {
            ChoiceDialog.Show(T("edt.acm.saveFirstTitle"), T("edt.acm.saveFirstBody", tab.FileName), new[] { T("ide.ok") });
            return;
        }
        var scope = ScopeKind;
        if (scope == CommentScopeKind.Selection && selection == null) { StatusMessage = T("edt.acm.noSelectionYet"); return; }

        var provider = _providers?.ActiveProvider;
        if (ModelOverride == null && (provider == null || !provider.IsReady))
        {
            StatusMessage = T("edt.acm.notReady", provider?.DisplayName ?? ProviderType.ToString());
            return;
        }

        IsAutoCommenting = true;
        _acmCts = new CancellationTokenSource();
        var ct = _acmCts.Token;
        _acmProject = _workspace.HasWorkingDirectory ? _workspace.WorkingDirectory : null;
        try
        {
            IReadOnlyCollection<int>? changed = null;
            if (scope == CommentScopeKind.Changed) changed = await ChangedLinesAsync(tab.FullPath);

            var options = new AutoCommentOptions(scope, AcmLanguage, AutoCommentModelId.Length > 0 ? AutoCommentModelId : null, selection, changed);
            var req = await Task.Run(() => AutoCommentService.Prepare(tab.FullPath, options), ct);
            string model = EffectiveModel.Length > 0 ? EffectiveModel : provider?.DisplayName ?? "";

            // The cost before anything is spent — the user starts the run here, not from the menu click.
            int go = ChoiceDialog.Show(T("edt.acm.confirmTitle"),
                T("edt.acm.confirmBody", tab.FileName, model, ScopeLabel, T("edt.acm.lang." + AcmLanguage), req.Allowed.Count, CostText(req)),
                new[] { T("edt.acm.confirmStart"), T("edt.acm.cancelButton") }, defaultIndex: 0);
            if (go != 0) return;

            StatusMessage = T("edt.acm.running", tab.FileName, model);
            IAutoCommentModel ai = ModelOverride?.Invoke() ?? new ProviderCommentModel(provider!, options.Model);
            var preview = await Task.Run(() => AutoCommentService.RunAsync(req, ai, ct), ct);
            ShowAutoCommentPreview(preview);
        }
        catch (OperationCanceledException) { StatusMessage = T("edt.acm.cancelled"); }
        catch (AutoCommentException ex) { StatusMessage = T("edt.acm.failed", T(ex.Key, ex.Args)); }
        catch (Exception ex)
        {
            _log?.Warn("AutoComment", $"run failed: {tab.FullPath}", ex);
            StatusMessage = T("edt.acm.failed", ex.Message);
        }
        finally
        {
            _acmCts?.Dispose();
            _acmCts = null;
            IsAutoCommenting = false;
        }
    }

    /// <summary>The open tabs for the chat's add_code_comments tool: which file "the open file" is, and which ones hold
    /// unsaved typing (never written over). Any thread — read on the UI thread.</summary>
    public IReadOnlyList<EditorFileState> OpenFilesForTools()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess())
            return d.Invoke(OpenFilesForTools, System.Windows.Threading.DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(5));
        var active = ActiveTab;
        return Tabs.Select(t => new EditorFileState(t.FullPath, t.IsDirty, ReferenceEquals(t, active))).ToList();
    }

    private Task<IReadOnlyCollection<int>?> ChangedLinesAsync(string path) => AutoCommentService.ChangedLinesAsync(_git, path);

    private void ShowAutoCommentPreview(AutoCommentPreview p)
    {
        var diff = DiffService.BuildSideBySide(p.Request.Text, p.Plan.Result);
        OnUi(() =>
        {
            _acmPreview = p;
            DiffRows = diff.Rows;
            DiffTitle = p.FullPath;
            DiffSummary = T("edt.acm.previewSummary", p.Plan.Inserted.Count, p.Plan.Skipped.Count);
            DiffLeftCaption = T("edt.acm.leftCaption");
            DiffRightCaption = T("edt.acm.rightCaption");
            AutoCommentBanner = T("edt.acm.previewBanner", p.Plan.Count, Path.GetFileName(p.FullPath));
            AutoCommentSkipped = p.Plan.Skipped.Count == 0 ? "" : T("edt.acm.skippedHead", string.Join(" · ",
                p.Plan.Skipped.GroupBy(s => s.ReasonKey).Select(g => $"{g.Count()} {T(g.Key)}")));
            HasAutoCommentPreview = true;
            IsDiffViewActive = true;
            StatusMessage = AutoCommentBanner;
            RaiseAutoComment();
        });
    }

    private void EndAutoCommentPreview()
    {
        _acmPreview = null;
        HasAutoCommentPreview = false;
        AutoCommentBanner = AutoCommentSkipped = "";
        DiffLeftCaption = null!;
        DiffRightCaption = null!;
        IsDiffViewActive = false;
        DiffRows = new List<DiffRow>();
        DiffTitle = DiffSummary = "";
        RaiseAutoComment();
    }

    private void DiscardAutoComment()
    {
        if (_acmPreview == null) return;
        EndAutoCommentPreview();
        StatusMessage = T("edt.acm.discarded");
    }

    private async Task ApplyAutoCommentAsync()
    {
        var p = _acmPreview;
        if (p == null) return;
        var tab = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, p.FullPath, StringComparison.OrdinalIgnoreCase));
        if (tab is { IsDirty: true })
        {
            ChoiceDialog.Show(T("edt.acm.saveFirstTitle"), T("edt.acm.saveFirstBody", tab.FileName), new[] { T("ide.ok") });
            return;
        }
        try
        {
            string? project = _acmProject;
            var applied = await Task.Run(() => AutoCommentService.Apply(p, CheckpointService.Current, project));
            _acmLast = applied;
            EndAutoCommentPreview();
            await ReloadTabAsync(applied.FullPath);
            StatusMessage = T("edt.acm.applied", applied.Comments, Path.GetFileName(applied.FullPath));
            await RefreshGitAsync();
            await BuildCheckAsync(applied);
        }
        catch (AutoCommentException ex)
        {
            StatusMessage = T("edt.acm.failed", T(ex.Key, ex.Args));
            EndAutoCommentPreview();
        }
        catch (Exception ex)
        {
            _log?.Warn("AutoComment", $"apply failed: {p.FullPath}", ex);
            StatusMessage = T("edt.acm.failed", ex.Message);
        }
    }

    private async Task ReloadTabAsync(string path)
    {
        var tab = Tabs.FirstOrDefault(t => string.Equals(t.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (tab == null) return;
        var reloaded = await _workspace.OpenFileAsync(path);
        if (reloaded != null) OnUi(() => { tab.MarkOpened(reloaded.Content); _workspace.AdoptDiskState(tab, reloaded); });
    }

    /// <summary>Rebuild where the project has a build: the comments are proven not to change code, but the owner asked
    /// for the build as the second proof. A failure offers the undo (it may have been failing before — the text says so).</summary>
    private async Task BuildCheckAsync(AutoCommentApplied applied)
    {
        var cmd = _fs.DetectProjectCommand(test: false);
        if (cmd == null || !_workspace.HasWorkingDirectory) return;
        IsAutoCommenting = true;
        _acmCts = new CancellationTokenSource();   // Auto-comment ▸ Cancel stops the build too
        try
        {
            StatusMessage = T("edt.acm.building", cmd.Command);
            var r = await ShellRunner.RunAsync(ShellRunner.DefaultShell, cmd.Command, _workspace.WorkingDirectory, 10 * 60 * 1000,
                _acmCts.Token, captureChars: 200_000);
            if (r.Cancelled) { StatusMessage = T("edt.acm.applied", applied.Comments, Path.GetFileName(applied.FullPath)); return; }
            if (r.ExitCode == 0 && !r.TimedOut)
            {
                StatusMessage = T("edt.acm.buildOk", applied.Comments, Path.GetFileName(applied.FullPath));
                return;
            }
            string tail = r.Output.Length > 1500 ? "…" + r.Output[^1500..] : r.Output;
            int pick = ChoiceDialog.Show(T("edt.acm.buildFailTitle"), T("edt.acm.buildFailBody", tail.Trim()),
                new[] { T("edt.acm.undoButton"), T("edt.acm.keep") }, defaultIndex: 1);
            if (pick == 0) await UndoAutoCommentAsync();
        }
        catch (Exception ex) { _log?.Warn("AutoComment", "build check failed to run", ex); }
        finally
        {
            _acmCts?.Dispose();
            _acmCts = null;
            IsAutoCommenting = false;
        }
    }

    private async Task UndoAutoCommentAsync()
    {
        var a = _acmLast;
        if (a == null) return;
        try
        {
            await Task.Run(() => AutoCommentService.Undo(a));
            _acmLast = null;
            await ReloadTabAsync(a.FullPath);
            StatusMessage = T("edt.acm.undone", Path.GetFileName(a.FullPath));
            await RefreshGitAsync();
        }
        catch (AutoCommentException ex) { StatusMessage = T("edt.acm.failed", T(ex.Key, ex.Args)); }
        catch (Exception ex) { StatusMessage = T("edt.acm.failed", ex.Message); }
        finally { RaiseAutoComment(); }
    }
}
