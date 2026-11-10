using System.IO;
using System.Runtime.CompilerServices;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// "Local GGUF" provider that transparently routes between two backends:
///   • LLamaSharp (in-process, fast) — for models the bundled llama.cpp recognizes.
///   • llama-server.exe (out-of-process, llama-backend/) — for newer architectures
///     like Gemma 3/4, Llama 4, Qwen 3 that LLamaSharp's pinned llama.cpp doesn't
///     handle. The fallback is invisible to the user — they pick "Local GGUF" and
///     load whatever model they have; we figure out which backend can run it.
/// </summary>
public class LocalGgufProvider : IAiProvider
{
    private readonly LlamaInferenceService _llamaService;
    private readonly LlamaServerProvider _serverProvider;
    private readonly SettingsService _settingsService;

    private enum Backend { None, LlamaSharp, LlamaServer }
    private Backend _activeBackend = Backend.None;
    private string? _loadedModelPath;

    // Store event handlers as fields for proper unsubscription
    private readonly Action<string> _statusHandler;
    private readonly Action<bool> _loadingHandler;
    private readonly Action<string> _errorHandler;
    private readonly Action<string> _serverStatusHandler;
    private readonly Action<bool> _serverLoadingHandler;
    private readonly Action<string> _serverErrorHandler;

    public string ProviderId => "Local";
    public string DisplayName => "Local GGUF";
    public bool IsReady => _activeBackend switch
    {
        Backend.LlamaSharp => _llamaService.IsModelLoaded,
        Backend.LlamaServer => _serverProvider.IsServerRunning && _serverProvider.IsReady,
        _ => false,
    };
    public bool IsLoading => _llamaService.IsLoading || _serverProvider.IsLoading;
    public string StatusMessage => _activeBackend switch
    {
        Backend.LlamaSharp => _llamaService.IsModelLoaded
            ? $"Model: {_llamaService.LoadedModelName}"
            : _llamaService.IsLoading ? "Loading model..." : "No model loaded",
        Backend.LlamaServer => _serverProvider.StatusMessage,
        _ => _llamaService.IsLoading ? "Loading model..." : "No model loaded",
    };

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    public LocalGgufProvider(LlamaInferenceService llamaService, LlamaServerProvider serverProvider, SettingsService settingsService)
    {
        _llamaService = llamaService;
        _serverProvider = serverProvider;
        _settingsService = settingsService;

        _statusHandler = s => { if (_activeBackend == Backend.LlamaSharp) OnStatusChanged?.Invoke(s); };
        _loadingHandler = b => { if (_activeBackend == Backend.LlamaSharp) OnLoadingChanged?.Invoke(b); };
        _errorHandler = e => { if (_activeBackend == Backend.LlamaSharp) OnError?.Invoke(e); };

        _serverStatusHandler = s => { if (_activeBackend == Backend.LlamaServer) OnStatusChanged?.Invoke(s); };
        _serverLoadingHandler = b => { if (_activeBackend == Backend.LlamaServer) OnLoadingChanged?.Invoke(b); };
        _serverErrorHandler = e => { if (_activeBackend == Backend.LlamaServer) OnError?.Invoke(e); };

        _llamaService.OnStatusChanged += _statusHandler;
        _llamaService.OnLoadingChanged += _loadingHandler;
        _llamaService.OnError += _errorHandler;

        _serverProvider.OnStatusChanged += _serverStatusHandler;
        _serverProvider.OnLoadingChanged += _serverLoadingHandler;
        _serverProvider.OnError += _serverErrorHandler;
        _serverProvider.OnServerDied += OnServerDied;
    }

    public Task InitializeAsync(CancellationToken ct = default)
    {
        // Local model loading is managed from the Models tab — no-op here
        return Task.CompletedTask;
    }

    /// <summary>Models that failed on this PC (load or mid-answer). Replaced by the test harness.</summary>
    public Local.ModelHealthLog Health { get; set; } = Local.ModelHealthLog.Default;

    /// <summary>The cards in this PC now; the harness replaces it (the real one asks nvidia-smi once, then caches).</summary>
    public Func<string>? CurrentGpu { get; set; }
    private string Gpu() => (CurrentGpu ?? _serverProvider.GpuFingerprint)();
    /// <summary>The GPU fingerprint (asks nvidia-smi the first time: not on the UI thread).</summary>
    public string GpuNow() => Gpu();
    /// <summary>The fingerprint if already known, else "" — for the UI thread.</summary>
    public string CachedGpu => CurrentGpu != null ? CurrentGpu() : _serverProvider.CachedGpuFingerprint;

    /// <summary>The GGUF loaded now (either backend), null when none.</summary>
    public string? LoadedModelPath => _activeBackend == Backend.None ? null : _loadedModelPath;

    /// <summary>How fast each model ran on this PC (shared with the llama-server backend, which measures it).</summary>
    public Local.ModelSpeedLog Speed { get => _serverProvider.Speed; set => _serverProvider.Speed = value; }

    /// <summary>The last agent step's measured speed (llama-server only; LLamaSharp reports no timings).</summary>
    public Local.ModelSpeed? LastStepSpeed => _activeBackend == Backend.LlamaServer ? _serverProvider.LastStepSpeed : null;

    /// <summary>Tuned load settings (shared with the llama-server backend, which applies them).</summary>
    public Local.TuneStore Tunes { get => _serverProvider.Tunes; set => _serverProvider.Tunes = value; }
    /// <summary>The window the loaded model really has (a tuned profile may be smaller than Settings); null = Settings.</summary>
    public int? LoadedContextSize => _activeBackend == Backend.LlamaServer ? _serverProvider.LoadedContextSize : null;
    /// <summary>The tuned settings the loaded model runs with (null = the Settings page's).</summary>
    public Local.TuneCandidate? AppliedTune => _activeBackend == Backend.LlamaServer ? _serverProvider.AppliedTune : null;
    public bool TunedHere(string modelPath) => _serverProvider.TunedHere(modelPath);

    // ─── load tuning (LoadTuner) — only ever from the owner's click on the Models page ───

    /// <summary>What a tune of this model would do, decided without loading anything: the candidates, how many loads and
    /// about how long. Null with a reason when it cannot be tuned here. Reads the GGUF header, nvidia-smi and the
    /// server's --help: call it off the UI thread.</summary>
    public sealed record TunePlan(string ModelPath, Local.ModelShape Shape, Local.TuneHardware Hardware,
        IReadOnlyList<Local.TuneCandidate> Candidates, int EstimatedMinutes, string Gpu);

    public bool IsTuning { get; private set; }

    public (TunePlan? Plan, string Refusal) PlanTune(string modelPath)
    {
        bool th = IsThai;
        if (!_serverProvider.IsServerBackendAvailable)
            return (null, th ? "ปรับค่าได้เฉพาะเมื่อมี llama-server (ดาวน์โหลด llama.cpp ในหน้า Models ก่อน)"
                             : "Tuning needs llama-server (get llama.cpp on the Models page first)");
        var cat = HuggingFaceService.Catalog.FirstOrDefault(m =>
            string.Equals(m.FileName, Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase)
            || m.Quants.Any(q => string.Equals(q.FileName, Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase)));
        double? activeShare = cat is { TotalParamsB: > 0, ActiveParamsB: > 0 } ? cat.ActiveParamsB / cat.TotalParamsB : null;
        var shape = Local.LoadTuner.ShapeOf(modelPath, activeShare);
        if (shape == null)
            return (null, th ? "อ่านโครงสร้างโมเดลจากไฟล์ไม่ได้ (ไม่ใช่ GGUF หรือไฟล์เสีย)" : "Cannot read the model's shape from the file (not a GGUF, or damaged)");
        var gpus = SafeGpus();
        var (cpuMoe, fa) = _serverProvider.ServerFeatures();
        var s = _settingsService.Settings;
        var hw = new Local.TuneHardware(gpus.Sum(g => g.VramFreeMB), gpus.Sum(g => g.VramTotalMB), Local.LoadTuner.PhysicalCores(),
            cpuMoe, fa, (int)s.ContextSize, (int)s.BatchSize);
        var cands = Local.LoadTuner.Pick(shape, hw);
        if (cands.Count == 0)
            return (null, th ? "ไม่มีชุดค่าที่ลงใน VRAM ที่ว่างได้" : "No candidate fits the free VRAM");
        int minutes = Math.Min((int)Local.LoadTuner.Budget.TotalMinutes, (int)Math.Ceiling(cands.Count * 1.5));
        return (new TunePlan(modelPath, shape, hw, cands, minutes, Local.ModelHealthLog.GpuFingerprint(gpus)), "");
    }

    private List<Models.GpuInfo> SafeGpus()
    {
        try { return _serverProvider.DetectGpus(); } catch { return new(); }
    }
    /// <summary>The GPU reader the tuner asks for free VRAM before every candidate; the harness replaces it.</summary>
    public Func<int>? FreeVramMb { get; set; }

    /// <summary>Run a planned tune: the loaded model is unloaded first, each candidate loads, runs the benchmark and stops.
    /// The result is kept as the model's profile for this card (a load then uses it); only a tune that found nothing fast
    /// enough sets the "slow here" flag. Cancel = no verdict, no profile. Refused while the model serves a request.</summary>
    public async Task<Local.TuneResult> RunTuneAsync(TunePlan plan, IProgress<string>? progress, CancellationToken ct,
        Func<Local.TuneCandidate, CancellationToken, Task<Local.TuneBench>>? bench = null)
    {
        bool th = IsThai;
        if (IsTuning) throw new InvalidOperationException(th ? "กำลังปรับค่าอยู่แล้ว" : "A tune is already running");
        if (IsLoading) throw new InvalidOperationException(th ? "กำลังโหลดโมเดลอยู่ รอให้เสร็จก่อน" : "A model is loading, wait for it");
        if (_serverProvider.InUse)
            throw new InvalidOperationException(th ? "โมเดลกำลังถูกใช้งาน (มีงาน AI รันอยู่หรือเพิ่งจบ) — รอให้ว่างก่อนแล้วค่อยปรับค่า"
                                                   : "The model is in use (an AI run is working or just ended) — wait until it is idle, then tune");
        IsTuning = true;
        try
        {
            await UnloadAsync();
            Func<int> free = FreeVramMb ?? (() =>
            {
                Thread.Sleep(1500);   // the stopped server's VRAM takes a moment to come back
                return SafeGpus().Sum(g => g.VramFreeMB);
            });
            var result = await Local.LoadTuner.RunAsync(plan.Candidates,
                bench ?? ((c, t) => _serverProvider.BenchmarkAsync(plan.ModelPath, c, t)), free, progress, th, ct);
            if (result.Measured == 0 && !result.Trials.Any(t => t.Bench != null))
                return result;   // everything skipped (VRAM taken by something else): no verdict
            string tried = Local.LoadTuner.TriedSummary(result, th);
            if (result.Best != null || !result.Passed)
                Tunes.Save(new Local.TuneProfile(plan.ModelPath, SizeOf(plan.ModelPath), plan.Gpu, DateTime.UtcNow,
                    result.Best ?? plan.Candidates[0], result.BestBench?.PromptTps ?? 0, result.BestBench?.GenTps ?? 0,
                    result.Passed, result.Measured, tried));
            Speed.RecordTune(plan.ModelPath, plan.Gpu, result.BestBench, result.Passed, tried);
            return result;
        }
        finally
        {
            IsTuning = false;
        }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return -1; }
    }
    private bool IsThai => string.Equals(_settingsService.Settings.Language, "th", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Load a GGUF model — unless it failed on this PC before: then refuse with why and what to do
    /// (<see cref="Local.ModelBlockedException"/>) until the owner presses Force load (<paramref name="force"/>).
    /// A load that fails is recorded with its reason (memory, crash, damaged file…) and explained
    /// (<see cref="Local.ModelLoadFailedException"/>); one that works clears the record.
    /// </summary>
    public async Task LoadModelAsync(string modelPath, IProgress<string>? progress = null, CancellationToken ct = default, bool force = false)
    {
        bool th = IsThai;

        // The last load never finished (in-process crash, the PC restarted): that model failed, whatever it was.
        if (Health.TakeInterruptedLoad() is { } lost)
            OnError?.Invoke(Local.ModelHealthLog.FailedMessage(lost, th));

        if (!force && Health.Find(modelPath) is { } past)
        {
            string gpuThen = "";
            try { gpuThen = await Task.Run(Gpu, ct); } catch { }
            string msg = Local.ModelHealthLog.BlockedMessage(past, gpuThen, th, out string shortMsg);
            OnStatusChanged?.Invoke(shortMsg);
            throw new Local.ModelBlockedException(past, msg, shortMsg);
        }

        string gpuNow = "";
        try { gpuNow = await Task.Run(Gpu, ct); } catch { }   // nvidia-smi: not on the UI thread
        Health.MarkLoading(modelPath, gpuNow);
        try
        {
            await LoadModelCoreAsync(modelPath, progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                       and not ModelArchitectureNotSupportedException   // its own way out: get a newer llama.cpp
                                       and not FileNotFoundException)
        {
            Health.EndLoading();
            string detail = Local.ModelHealthLog.Describe(ex, _serverProvider.RecentStderr);
            var kind = Local.ModelHealthLog.Classify(detail);
            var failure = Health.Record(modelPath, kind, detail, gpuNow, duringRun: false);
            string msg = Local.ModelHealthLog.FailedMessage(failure, th);
            OnStatusChanged?.Invoke(th ? "โหลดโมเดลไม่สำเร็จ — " + Local.ModelHealthLog.ReasonText(kind, true)
                                       : "Model load failed — " + Local.ModelHealthLog.ReasonText(kind, false));
            throw new Local.ModelLoadFailedException(failure, msg, ex);
        }
        catch
        {
            Health.EndLoading();
            throw;
        }
        Health.EndLoading();
        Health.Clear(modelPath);
    }

    /// <summary>The server died while a model was loaded and answering: remember it, and say why.</summary>
    private void OnServerDied(string modelPath, int exitCode)
    {
        try
        {
            var lines = _serverProvider.RecentStderr;
            string telling = string.Join(" | ", lines.Where(l => Local.ModelHealthLog.Classify(l) != Local.ModelFailureKind.Unknown)
                .Select(l => l.Trim()).TakeLast(3));
            var kind = Local.ModelHealthLog.Classify(telling, exitCode);
            // Exit 0 with nothing telling = closed from outside (Task Manager, another tool), not the model's fault.
            if (exitCode == 0 && kind == Local.ModelFailureKind.Unknown) return;
            if (kind == Local.ModelFailureKind.Unknown) kind = Local.ModelFailureKind.Crash;
            string detail = $"llama-server exited with code 0x{unchecked((uint)exitCode):X8}" + (telling.Length > 0 ? " | " + telling : "");
            var failure = Health.Record(modelPath, kind, detail, Gpu(), duringRun: true);
            OnError?.Invoke(Local.ModelHealthLog.FailedMessage(failure, IsThai));
        }
        catch { /* the crash message from the server itself still reaches the owner */ }
    }

    private async Task LoadModelCoreAsync(string modelPath, IProgress<string>? progress, CancellationToken ct)
    {
        var settings = _settingsService.Settings;

        // Does the user want GPU? (-1 = auto/all layers, >0 = N layers, 0 = CPU-only; GpuBackend "CPU" = no GPU.)
        bool wantGpu = settings.GpuLayerCount != 0
            && !string.Equals(settings.GpuBackend, "CPU", StringComparison.OrdinalIgnoreCase);

        // Pre-inspect the GGUF so we can pick the backend BEFORE doing any expensive work
        // (memory mapping, GPU upload, etc.). If inspection returns null we assume the file
        // is loadable by LLamaSharp and let it fail naturally.
        string? arch = GgufMetadataReader.TryReadArchitecture(modelPath);
        // Asked of the llama.dll files themselves: LLamaSharp's (runtimes\…\llama.dll) and the one next to the
        // llama-server that would run. A hardcoded list missed Qwen 3.5, which then failed deep in native code with
        // "format / memory / corrupt file" while the real reason was a llama.cpp older than the model.
        bool? inProcessSupports = string.IsNullOrEmpty(arch) ? null : LlamaBackendInfo.SupportsArchitectureInProcess(arch);
        string? serverExe = _serverProvider.ServerExePath;
        string? serverDll = serverExe == null ? null : Path.Combine(Path.GetDirectoryName(serverExe)!, "llama.dll");
        bool? serverSupports = serverDll != null && File.Exists(serverDll) && !string.IsNullOrEmpty(arch)
            ? LlamaBackendInfo.SupportsArchitectureIn(serverDll, arch)
            : null;

        // Backend decision (LocalBackendRouter.Decide):
        //   • Architecture the in-process llama.cpp lacks → MUST use llama-server; no server that knows it →
        //     refuse now, with the way out, instead of a generic load error.
        //   • GPU requested → PREFER llama-server. The in-process LLamaSharp backend only uses CUDA when the
        //     CUDA Toolkit is installed (it gates detection on CUDA_PATH); the vast majority of users have
        //     just the NVIDIA driver, so LLamaSharp silently runs everything on CPU — brutally slow for a
        //     7B+ model (this is the real "local chat hangs / 75s and no answer" report). The bundled
        //     llama-server ships the full CUDA runtime (cudart/cublas) and offloads to the GPU reliably,
        //     so it is the correct GPU path. Verified: llama-server puts all 29/29 layers of a 7B on a
        //     GTX 1070 Ti; in-process LLamaSharp leaves the GPU idle on the same machine.
        //   • A vision projector next to the model → llama-server too: only it reads images (--mmproj); the
        //     in-process backend would load the same model blind.
        bool hasProjector = serverExe != null && VisionProjector.FindFor(modelPath) != null;
        var route = LocalBackendRouter.Decide(arch, inProcessSupports, serverExe != null, serverSupports, wantGpu, hasProjector);

        if (route == LocalBackendRoute.Unsupported)
        {
            var refusal = LocalBackendRouter.Refusal(arch!, modelPath, serverExe, settings.Language);
            OnStatusChanged?.Invoke(refusal.ShortMessage);
            OnError?.Invoke(refusal.Message);
            throw refusal;
        }

        bool needsFallback = route == LocalBackendRoute.Server;
        if (route is LocalBackendRoute.Server or LocalBackendRoute.PreferServer)
        {
            progress?.Report(needsFallback
                ? $"Architecture '{arch}' is newer than the bundled llama.cpp — using the llama-server backend."
                : hasProjector && !wantGpu
                    ? "Vision projector found — using llama-server backend so the model can see images."
                    : "GPU requested — using llama-server backend for reliable CUDA offload.");
            try
            {
                await SwitchToServerBackendAsync(modelPath, progress, ct);
                return;
            }
            catch (Exception ex) when (!needsFallback)
            {
                // GPU-preferred server path failed (missing exe, port busy, etc.). Don't hard-fail — degrade
                // to the in-process backend (CPU) so the user still gets a working model, just slower.
                progress?.Report($"llama-server unavailable ({ex.Message}). Falling back to in-process (CPU)...");
            }
        }

        // GPU asked for but no llama-server to give it: the in-process backend is CPU only (CluadeX no longer ships
        // LLamaSharp's CUDA backend — see CluadeX.csproj), so say so and where the GPU build is, instead of a silent
        // slow CPU run.
        if (wantGpu && serverExe == null)
        {
            string lang = settings.Language == "th" ? "th" : "en";
            progress?.Report(LocalizationService.Lookup("local.gpuNeedsServer", lang) ?? "local.gpuNeedsServer");
        }

        // In-process LLamaSharp: the CPU path.
        // If it throws our typed "unsupported architecture" exception (the inspector missed something),
        // fall back to llama-server transparently. Other failures propagate — the user needs the real error.
        try
        {
            await UnloadInternalAsync(keepBackend: Backend.LlamaSharp);

            // Route events to the LlamaSharp backend BEFORE loading so progress/loading/
            // status updates during LoadModelAsync actually propagate to the UI. If we set
            // the flag only after load, IsReady and status messages fired during load get
            // filtered out and the status bar stays stuck on "No model loaded".
            _activeBackend = Backend.LlamaSharp;
            _loadedModelPath = modelPath;
            try
            {
                await _llamaService.LoadModelAsync(modelPath, progress, ct);
            }
            catch
            {
                _activeBackend = Backend.None;
                _loadedModelPath = null;
                throw;
            }
        }
        catch (UnsupportedModelArchitectureException ex)
        {
            // The service's own check disagreed with the one above (the dll changed in between). Same rule: a
            // server that can run it, or the clear refusal — never llama-server's "exe not found".
            if (serverExe == null || serverSupports == false)
                throw LocalBackendRouter.Refusal(ex.Architecture, modelPath, serverExe, settings.Language);
            progress?.Report($"LLamaSharp can't handle '{ex.Architecture}'. Falling back to llama-server...");
            await SwitchToServerBackendAsync(modelPath, progress, ct);
        }
    }

    private async Task SwitchToServerBackendAsync(string modelPath, IProgress<string>? progress, CancellationToken ct)
    {
        await UnloadInternalAsync(keepBackend: Backend.LlamaServer);

        // Flip to LlamaServer BEFORE launching. Events (status/loading) fired by
        // LlamaServerProvider during startup are gated by _activeBackend — if we wait until
        // after LoadModelAsync returns, "Starting llama-server...", "Waiting for model...",
        // and the final "Ready:" status all get dropped and MainViewModel never learns the
        // model is loaded.
        _activeBackend = Backend.LlamaServer;
        _loadedModelPath = modelPath;
        try
        {
            await _serverProvider.LoadModelAsync(modelPath, progress, ct);
        }
        catch
        {
            _activeBackend = Backend.None;
            _loadedModelPath = null;
            throw;
        }
    }

    /// <summary>
    /// Unload the model from whichever backend holds it: stops llama-server (freeing its VRAM and the file
    /// lock) and/or unloads LLamaSharp. The Models page used to unload only LLamaSharp, while the default
    /// path (GPU → llama-server) kept the server and its VRAM alive behind a "Model unloaded" message — and
    /// deleting that model then failed with "file in use". Safe to call when nothing is loaded.
    /// </summary>
    public async Task UnloadAsync()
    {
        await UnloadInternalAsync();
        // Also release a backend that was started outside this router (e.g. an older auto-load path).
        if (_llamaService.IsModelLoaded) _llamaService.UnloadModel();
        if (_serverProvider.IsServerRunning) await _serverProvider.StopServerAsync();
        _activeBackend = Backend.None;
        _loadedModelPath = null;
        OnStatusChanged?.Invoke("No model loaded");
    }

    /// <summary>Unload whichever backend is currently active. Pass <paramref name="keepBackend"/> to skip unloading one.</summary>
    private async Task UnloadInternalAsync(Backend keepBackend = Backend.None)
    {
        if (_activeBackend == Backend.LlamaSharp && keepBackend != Backend.LlamaSharp)
        {
            _llamaService.UnloadModel();
        }
        else if (_activeBackend == Backend.LlamaServer && keepBackend != Backend.LlamaServer)
        {
            await _serverProvider.StopServerAsync();
        }
    }

    public IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
        => _activeBackend == Backend.LlamaServer
            ? _serverProvider.ChatAsync(history, userMessage, systemPrompt, ct)
            : _llamaService.ChatAsync(history, userMessage, systemPrompt, ct);

    public Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
        => _activeBackend == Backend.LlamaServer
            ? _serverProvider.GenerateAsync(history, userMessage, systemPrompt, ct)
            : _llamaService.GenerateAsync(history, userMessage, systemPrompt, ct);

    // ─── Native tool use: forward to the llama-server backend ───
    // Without these overrides this router fell back to IAiProvider's default (SupportsNativeToolUse=false),
    // so the ENTIRE weak-model tool harness (constrained decoding, salvage, auto-verify, escalation) was
    // dead whenever the user picked "Local GGUF" — the default local path. LlamaSharp (in-proc) has no
    // native tool calling, so only the server backend advertises support; the agent loop uses the
    // legacy [ACTION:] text loop for LlamaSharp models as before.
    public bool SupportsNativeToolUse
        => _activeBackend == Backend.LlamaServer && _serverProvider.SupportsNativeToolUse;

    /// <summary>Tool images: only through the llama-server backend, and only when it has a vision projector —
    /// false today (LLamaSharp in-process never sees images).</summary>
    /// <summary>The vision projector the loaded model runs with (null = blind, or not on llama-server).</summary>
    public string? LoadedProjectorPath => _activeBackend == Backend.LlamaServer ? _serverProvider.LoadedProjectorPath : null;
    /// <summary>Why a projector next to the model was left out on the last load ("" = it was used or there was none).</summary>
    public string VisionFallbackReason => _activeBackend == Backend.LlamaServer ? _serverProvider.VisionFallbackReason : "";

    public bool SupportsToolResultImages(string modelId)
        => _activeBackend == Backend.LlamaServer && _serverProvider.SupportsToolResultImages(modelId);

    public Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages,
        string systemPrompt,
        List<ToolSchema> tools,
        Action<string>? onTextDelta = null,
        CancellationToken ct = default,
        string? toolChoice = null)
        => _activeBackend == Backend.LlamaServer
            ? _serverProvider.ChatWithToolsAsync(messages, systemPrompt, tools, onTextDelta, ct, toolChoice)
            : Task.FromResult(new NativeToolResponse
            {
                TextContent = "Native tool use is not supported by the in-process LLamaSharp backend.",
                StopReason = "error",
            });

    public Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        return _activeBackend switch
        {
            Backend.LlamaServer => _serverProvider.TestConnectionAsync(ct),
            Backend.LlamaSharp => Task.FromResult(_llamaService.IsModelLoaded
                ? (true, $"Model loaded: {_llamaService.LoadedModelName}")
                : (false, "No model loaded.")),
            _ => Task.FromResult((false, "No model loaded. Go to Models tab to load a GGUF model.")),
        };
    }

    public void Dispose()
    {
        _llamaService.OnStatusChanged -= _statusHandler;
        _llamaService.OnLoadingChanged -= _loadingHandler;
        _llamaService.OnError -= _errorHandler;
        _serverProvider.OnStatusChanged -= _serverStatusHandler;
        _serverProvider.OnLoadingChanged -= _serverLoadingHandler;
        _serverProvider.OnError -= _serverErrorHandler;
    }
}
