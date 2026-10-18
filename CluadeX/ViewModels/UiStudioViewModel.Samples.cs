using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using CluadeX.Services.UiStudio.Sketch;

namespace CluadeX.ViewModels;

/// <summary>
/// What a Flutter app's requests are answered with while its screens are drawn: the sample replies read from its own
/// code (<see cref="FlutterSampleData"/>); a per-project switch to draw without them (the screens' error and empty
/// states, as with no network); and the AI's realistic values for them — written into the Studio's own store (never
/// into the app) and merged over the made-up replies at every draw. Each change draws every screen again.
/// </summary>
public sealed partial class UiStudioViewModel
{
    /// <summary>The sample replies are written into the prompt up to this size; a bigger set goes in a file the AI reads.</summary>
    public const int MaxPromptSamples = 24_000;
    internal const string SamplesCopyFile = "sample_replies.json";

    private int _sampleCount, _sampleOverridden, _sampleInfoRun;
    private string? _sampleError;
    private bool _hasSampleOverrides, _makingSamples, _samplesAsking;

    /// <summary>The app's requests are answered with sample replies while its screens are drawn (the project's switch).</summary>
    public bool SampleDataOn
    {
        get => _store?.State.SampleDataOff != true;
        set
        {
            if (_store == null || value == SampleDataOn) return;
            _store.State.SampleDataOff = !value;
            _store.SaveState();
            RaiseSamples();
            Status(value ? T("studio.samples.switchedOn") : T("studio.samples.switchedOff"));
            RedrawForSamples();
        }
    }

    /// <summary>The other side of the switch.</summary>
    public bool SampleDataOff { get => !SampleDataOn; set => SampleDataOn = !value; }

    /// <summary>The switch shows for a Flutter app whose code makes requests the Studio can answer.</summary>
    public bool ShowSampleBar => IsFlutterApp && _sampleCount > 0;
    public int SampleRequestCount => _sampleCount;
    /// <summary>Replies the AI's realistic values change.</summary>
    public int SampleOverrideCount => _sampleOverridden;
    /// <summary>The AI's file is there (so it can be taken away).</summary>
    public bool HasSampleOverrides => _hasSampleOverrides;
    /// <summary>Where the AI writes the realistic values: the Studio's store for this project.</summary>
    public string? SampleOverridesPath => _store?.SampleOverridesPath;

    /// <summary>The AI is writing the realistic values.</summary>
    public bool IsMakingSamples
    {
        get => _makingSamples;
        private set { if (SetProperty(ref _makingSamples, value)) RaiseSamples(); }
    }

    public string SampleSummary =>
        _makingSamples ? T("studio.samples.working")
        : !SampleDataOn ? T("studio.samples.summaryOff")
        : _sampleError != null ? T("studio.samples.summaryBad", _sampleCount)
        : _sampleOverridden > 0 ? T("studio.samples.summaryAi", _sampleCount, _sampleOverridden)
        : T("studio.samples.summary", _sampleCount);

    private void RaiseSamples()
    {
        foreach (var n in new[] { nameof(SampleDataOn), nameof(SampleDataOff), nameof(ShowSampleBar), nameof(SampleRequestCount), nameof(SampleOverrideCount),
                     nameof(HasSampleOverrides), nameof(SampleOverridesPath), nameof(SampleSummary) })
            OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Count the requests the app's code makes and what the AI's file changes — in the background (the code is
    /// read again only when it changed).</summary>
    private async void RefreshSampleInfo()
    {
        int run = ++_sampleInfoRun;
        if (_catalog?.Flutter == null || _store == null) { SetSampleInfo(null, false); return; }
        string root = _studio.Root, over = _store.SampleOverridesPath;
        var ct = _pagesCts.Token;
        SampleData data;
        try { data = await Task.Run(() => FlutterSampleData.For(root, over, ct), ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log?.Warn("UiStudio", "reading the sample data failed", ex); return; }
        if (run != _sampleInfoRun || ct.IsCancellationRequested) return;
        SetSampleInfo(data, File.Exists(over));
    }

    private void SetSampleInfo(SampleData? data, bool hasFile)
    {
        _sampleCount = data?.Replies.Count ?? 0;
        _sampleOverridden = data?.Overridden ?? 0;
        _sampleError = data?.OverridesError;
        _hasSampleOverrides = hasFile;
        RaiseSamples();
    }

    /// <summary>What the screens are drawn with now — kept with each picture, so one drawn otherwise is drawn again.</summary>
    private string SampleModeKey() =>
        !SampleDataOn ? "off" : FlutterSampleData.OverridesStamp(_store?.SampleOverridesPath) is { Length: > 0 } s ? "on:" + s : "on";

    /// <summary>What a picture was drawn with (null: a picture from before it was kept).</summary>
    internal static string? SidecarSamples(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("samples", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Every screen drawn again with what the requests are answered with now, the stage first; a draw still
    /// running with the old answers is stopped.</summary>
    private void RedrawForSamples()
    {
        if (_catalog?.Flutter == null) return;
        if (_rendering) _renderCts?.Cancel();
        var order = new List<PageCard>();
        if (_stageCard != null) order.Add(_stageCard);
        order.AddRange(ScreenCards.Where(c => !ReferenceEquals(c, _stageCard)));
        foreach (var c in order) c.IsStale = true;
        RenderScreens(order);
    }

    // ───────────────────────────── realistic values by the AI ─────────────────────────────

    /// <summary>The AI gets the requests, their models' files and the sample replies; it writes realistic Thai values
    /// into the Studio's store, then they are checked and every screen is drawn again with them.</summary>
    private async Task MakeSamplesRealAsync()
    {
        if (_catalog?.Flutter == null || _store == null || !SampleDataOn) return;
        if (StudioAi.IsBusy) { Status(T("studio.ai.busy"), error: true); return; }
        string root = _studio.Root, over = _store.SampleOverridesPath;
        var ct = _pagesCts.Token;
        SampleData data;
        try { data = await Task.Run(() => FlutterSampleData.For(root, over, ct), ct); }
        catch (OperationCanceledException) { return; }
        if (data.Replies.Count == 0) { Status(T("studio.samples.none"), error: true); return; }
        // the AI made values before: it writes the file again (seeing the values the screens use now) — asked first
        if (data.Overridden > 0 && File.Exists(over))
        {
            bool again = ToolConfirmOverride ?? Views.Editor.ChoiceDialog.Show(T("studio.samples.redoTitle"), T("studio.samples.redoBody", data.Overridden),
                new[] { T("studio.samples.redoYes"), T("studio.cancel") }, 1) == 0;
            if (!again) return;
        }
        string before = FlutterSampleData.OverridesStamp(over);
        _samplesAsking = true;
        // the Studio's round: if the AI touches the app's files anyway, that is there to review and undo
        if (!AskAi(BuildSamplesPrompt(data, over))) { _samplesAsking = false; RaiseSession(); return; }
        Status(T("studio.samples.sent"));
        IsMakingSamples = true;
        try { await WaitAiRunAsync(ct); }
        catch (OperationCanceledException) { return; }   // another project was opened: this one's file is its own
        finally
        {
            _samplesAsking = false;
            IsMakingSamples = false;
            RaiseSession();
        }
        if (FlutterSampleData.OverridesStamp(over) == before) { Status(T("studio.samples.notWritten"), error: true); return; }
        try { data = await Task.Run(() => FlutterSampleData.For(root, over, ct), ct); }
        catch (OperationCanceledException) { return; }
        SetSampleInfo(data, File.Exists(over));
        if (data.OverridesError is { } why) { Status(T("studio.samples.bad", T(why)), error: true); return; }
        if (data.Overridden == 0) { Status(data.Kept.Count == 0 && data.Ignored.Count > 0 ? T("studio.samples.noMatch") : T("studio.samples.noChange"), error: true); return; }
        Status(data.Kept.Count > 0 ? T("studio.samples.doneKept", data.Overridden, data.Kept.Count) : T("studio.samples.done", data.Overridden));
        // (switched off meanwhile: the screens are already drawn without any sample data)
        if (SampleDataOn) RedrawForSamples();
    }

    /// <summary>What the AI gets: where to write (the Studio's store, never the app), the rules that keep the app's code
    /// reading the values, each request with its model's file, and the replies the screens are drawn with now.</summary>
    internal string BuildSamplesPrompt(SampleData data, string overridesPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[UI Studio — sample data] The UI Studio draws this Flutter app's screens offline (flutter test, no network). Each request the app's code makes " +
                      "is answered with a sample reply the Studio made up from the key names (\"ตัวอย่าง 2\", 1250.5, \"สมชาย ใจดี\"). Make these replies realistic for this app, in Thai.");
        sb.AppendLine();
        sb.AppendLine("Write exactly one file: " + overridesPath);
        sb.AppendLine("It is the UI Studio's own store for this project, outside the app. Do not create or change any file of the app (lib/, pubspec.yaml, assets, .arb files): its code stays as it is.");
        sb.AppendLine();
        sb.AppendLine("The file is one JSON object: for each request, \"METHOD /path\" → its reply, in the structure of the sample replies below.");
        sb.AppendLine("- Change the values, not the structure: the same keys and nesting. Text stays text, a number stays a number (a whole number stays whole), true/false stays true/false, " +
                      "a date stays an ISO-8601 date like \"2026-10-18T09:30:00Z\", null stays null (pictures can't load here).");
        sb.AppendLine("- Make them fit this app: read the models' files to see what each key means. Thai names for people, products and places; amounts and counts that fit the business; dates in the last few weeks.");
        sb.AppendLine("- Keep the code's own words as they are: values the code compares with (status words such as \"paid\" or \"active\"), codes and keys.");
        sb.AppendLine("- Lists: 3 to 6 items, each one different. A list that is empty below stays empty (errors, failures: the screens show a healthy state).");
        sb.AppendLine("- You may leave things out: a request left out keeps its sample reply, a key left out keeps its sample value, and a list item takes the keys it leaves out from the sample item at its place. " +
                      "Requests that only change something (POST, PUT, PATCH, DELETE) rarely show on a screen: leave them out unless a screen draws their reply.");
        sb.AppendLine();
        sb.AppendLine("Requests (method and path → what the reply is read into · the model's file · where the code makes the request):");
        foreach (var r in data.Replies)
            sb.AppendLine($"- {r.Key} → {r.Model}" + (r.ModelFiles.Count > 0 ? " · " + string.Join(", ", r.ModelFiles.Select(Rel)) : "") + $" · {Rel(r.File)}:{r.Line}");
        sb.AppendLine();
        string json = data.RepliesJson();
        string? copy = json.Length > MaxPromptSamples ? WriteSamplesCopy(json) : null;
        if (copy != null) sb.AppendLine($"The sample replies now ({data.Replies.Count} requests) are in {Rel(copy)} — read it, and write the file in that structure.");
        else
        {
            sb.AppendLine("The sample replies now:");
            sb.AppendLine("```json");
            sb.AppendLine(json);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("When the file is saved, the UI Studio checks each value against the sample (a value of another kind keeps the sample's) and draws every screen again with it.");
        return sb.ToString();
    }

    /// <summary>A big set of sample replies for the AI to read, beside the renderer's test (in .dart_tool, never in the sources).</summary>
    private string? WriteSamplesCopy(string json)
    {
        try
        {
            string dir = Path.Combine(_studio.Root, FlutterRenderer.Folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, SamplesCopyFile);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            _log?.Warn("UiStudio", "writing the sample replies for the AI failed", ex);
            return null;
        }
    }

    /// <summary>Back to the values made up from the key names: the AI's file goes (asked first), every screen is drawn again.</summary>
    private void ClearSampleOverrides()
    {
        if (_store == null) return;
        string path = _store.SampleOverridesPath;
        if (!File.Exists(path)) { RefreshSampleInfo(); return; }
        bool go = ToolConfirmOverride ?? Views.Editor.ChoiceDialog.Show(T("studio.samples.clearTitle"), T("studio.samples.clearBody", _sampleOverridden),
            new[] { T("studio.samples.clearYes"), T("studio.cancel") }, 1) == 0;
        if (!go) return;
        try { File.Delete(path); }
        catch (Exception ex)
        {
            _log?.Warn("UiStudio", "deleting the AI's sample values failed", ex);
            Status(T("studio.samples.clearFailed"), error: true);
            return;
        }
        _sampleOverridden = 0;
        _sampleError = null;
        _hasSampleOverrides = false;
        RaiseSamples();
        RefreshSampleInfo();
        Status(T("studio.samples.cleared"));
        RedrawForSamples();
    }

    // ───────────────────────────── commands ─────────────────────────────

    private ICommand? _makeSamplesRealCommand, _clearSampleOverridesCommand;

    public ICommand MakeSamplesRealCommand => _makeSamplesRealCommand ??= new AsyncRelayCommand(MakeSamplesRealAsync,
        () => ShowSampleBar && SampleDataOn && !_makingSamples && !AiWorking && !IsChecking);
    public ICommand ClearSampleOverridesCommand => _clearSampleOverridesCommand ??= new RelayCommand(ClearSampleOverrides, () => _hasSampleOverrides && !_makingSamples);
}
