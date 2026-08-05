using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using CluadeX.Models;

namespace CluadeX.Services;

/// <summary>
/// Thrown when LlamaInferenceService is asked to load a GGUF whose architecture
/// the bundled llama.cpp can't handle (e.g. Gemma 4). Callers (LocalGgufProvider)
/// can catch this and route to llama-server.exe instead.
/// </summary>
public sealed class UnsupportedModelArchitectureException : Exception
{
    public string Architecture { get; }
    public string ModelPath { get; }
    public UnsupportedModelArchitectureException(string architecture, string modelPath, string message)
        : base(message)
    {
        Architecture = architecture;
        ModelPath = modelPath;
    }
}

public class LlamaInferenceService : IDisposable
{
    private readonly SettingsService _settingsService;
    private LLamaWeights? _model;
    private LLamaContext? _context;
    private InteractiveExecutor? _executor;
    private ModelParams? _modelParams;   // stored for creating fresh contexts per inference
    private string? _loadedModelPath;
    private bool _isLoading;
    private bool _disposed;

    public bool IsModelLoaded => _model != null;
    public bool IsLoading => _isLoading;
    public string? LoadedModelName => _loadedModelPath != null ? Path.GetFileNameWithoutExtension(_loadedModelPath) : null;
    public string? LoadedModelPath => _loadedModelPath;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnLoadingChanged;
    public event Action<string>? OnError;

    public LlamaInferenceService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    // ─── Native backend selection (CUDA) ───────────────────────────────────────────────────────
    // CRITICAL local-mode fix. The project references BOTH LLamaSharp.Backend.Cpu AND .Cuda12. Without an
    // explicit NativeLibraryConfig, LLamaSharp's auto-probe can pick the CPU backend even on a CUDA box —
    // so the model runs entirely on CPU (GPU sits idle) and a 7B prefill takes tens of seconds to minutes.
    // That is the real "local chat hangs / 75s and no answer" report. We force CUDA (with CPU auto-fallback)
    // exactly ONCE, before the first native call, and log which backend actually loads so it's verifiable.
    private static int _nativeConfigured;

    private static void EnsureNativeBackendConfigured()
    {
        if (System.Threading.Interlocked.Exchange(ref _nativeConfigured, 1) != 0) return;
        try
        {
            LLama.Native.NativeLibraryConfig.All
                .WithCuda(true)
                .WithAutoFallback(true)
                .WithLogCallback((LLama.Native.LLamaLogLevel level, string message) =>
                {
                    if (string.IsNullOrEmpty(message)) return;
                    // Keep the diag focused on backend/device selection, not per-token spam.
                    if (message.IndexOf("cuda", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("backend", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("device", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("offload", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf("fallback", StringComparison.OrdinalIgnoreCase) >= 0
                        || message.IndexOf(".dll", StringComparison.OrdinalIgnoreCase) >= 0)
                        DiagLog($"[native {level}] {message.TrimEnd()}");
                });
            DiagLog("[native-config] WithCuda(true) + WithAutoFallback(true) applied");
        }
        catch (Exception ex)
        {
            // If native was already initialised earlier this process, this throws — harmless, the guard
            // means we only try once. Any other failure is logged but never blocks model loading.
            DiagLog($"[native-config-skipped] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Append a line to the local inference diagnostics log (best-effort, never throws).
    /// File: %LocalAppData%\CluadeX\inference-diag.log — used to prove GPU vs CPU + prefill timing.</summary>
    internal static void DiagLog(string line)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CluadeX");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "inference-diag.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { /* diagnostics must never break inference */ }
    }

    public async Task LoadModelAsync(string modelPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureNativeBackendConfigured(); // force CUDA backend selection BEFORE the first native call
        if (_isLoading) return;
        if (_loadedModelPath == modelPath && IsModelLoaded) return;

        if (!File.Exists(modelPath))
        {
            var msg = $"Model file not found: {modelPath}";
            OnError?.Invoke(msg);
            throw new FileNotFoundException(msg, modelPath);
        }

        // Basic file validation
        var fileInfo = new FileInfo(modelPath);
        if (fileInfo.Length < 1024)
        {
            var msg = $"File too small ({fileInfo.Length} bytes) — likely not a valid GGUF model.";
            OnError?.Invoke(msg);
            throw new InvalidOperationException(msg);
        }

        // Verify GGUF magic header
        try
        {
            using var fs = File.OpenRead(modelPath);
            var magic = new byte[4];
            await fs.ReadAsync(magic, 0, 4, ct);
            // GGUF magic: "GGUF" = 0x46475547
            if (magic[0] != 0x47 || magic[1] != 0x47 || magic[2] != 0x55 || magic[3] != 0x46)
            {
                var msg = $"Invalid file format. Expected GGUF but got [{magic[0]:X2} {magic[1]:X2} {magic[2]:X2} {magic[3]:X2}]. Make sure you downloaded a .gguf file (not .bin, .safetensors, etc.)";
                OnError?.Invoke(msg);
                throw new InvalidOperationException(msg);
            }
        }
        catch (IOException ex)
        {
            var msg = $"Cannot read model file: {ex.Message}";
            OnError?.Invoke(msg);
            throw new InvalidOperationException(msg, ex);
        }

        // ── Architecture pre-check ──
        // LLamaSharp ships an older llama.cpp build that doesn't recognize Gemma 3/4,
        // Llama 4, Qwen 3, etc. Without this check, LoadFromFile fails deep in native
        // code with a useless generic LoadWeightsFailedException. By detecting the
        // architecture upfront we can throw a typed exception that LocalGgufProvider
        // routes to llama-server.exe (which we ship in llama-backend/).
        var (needsFallback, architecture) = GgufMetadataReader.InspectModel(modelPath);
        if (needsFallback)
        {
            string fileName = Path.GetFileName(modelPath);
            string detail = $"This model uses architecture '{architecture}', which the " +
                            "bundled LLamaSharp build doesn't support. CluadeX will load it " +
                            "with the bundled llama-server.exe instead.";
            OnStatusChanged?.Invoke($"Routing {fileName} to llama-server (arch: {architecture})");
            throw new UnsupportedModelArchitectureException(architecture!, modelPath, detail);
        }

        _isLoading = true;
        OnLoadingChanged?.Invoke(true);

        try
        {
            progress?.Report("Unloading previous model...");
            UnloadModel();

            string fileName = Path.GetFileName(modelPath);
            string sizeDisplay = fileInfo.Length >= 1024L * 1024 * 1024
                ? $"{fileInfo.Length / (1024.0 * 1024 * 1024):F1} GB"
                : $"{fileInfo.Length / (1024.0 * 1024):F0} MB";
            progress?.Report($"Loading model: {fileName} ({sizeDisplay})");
            OnStatusChanged?.Invoke($"Loading {fileName}...");

            var settings = _settingsService.Settings;

            // Determine GPU layer count
            int gpuLayers = settings.GpuLayerCount;
            if (gpuLayers == -1) gpuLayers = 99; // auto = all layers on GPU

            // If backend is CPU, force 0 GPU layers
            if (settings.GpuBackend == "CPU")
            {
                gpuLayers = 0;
                progress?.Report("Using CPU-only backend (GPU layers set to 0)");
            }

            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = (uint)settings.ContextSize,
                GpuLayerCount = gpuLayers,
                BatchSize = (uint)settings.BatchSize,
            };

            // Set thread count if specified
            if (settings.ThreadCount > 0)
            {
                modelParams.Threads = settings.ThreadCount;
            }

            progress?.Report($"Loading with {gpuLayers} GPU layers, context={settings.ContextSize}, batch={settings.BatchSize}...");

            _model = await Task.Run(() =>
            {
                try
                {
                    return LLamaWeights.LoadFromFile(modelParams);
                }
                catch (Exception ex)
                {
                    string errMsg = ex.Message + " " + (ex.InnerException?.Message ?? "");

                    // Strategy 1: If GPU fails, try CPU fallback
                    if (settings.GpuBackend == "Auto" && gpuLayers > 0)
                    {
                        try
                        {
                            App.Current?.Dispatcher.Invoke(() =>
                                progress?.Report("GPU loading failed. Falling back to CPU..."));

                            modelParams.GpuLayerCount = 0;
                            return LLamaWeights.LoadFromFile(modelParams);
                        }
                        catch
                        {
                            // CPU also failed — continue to strategy 2
                        }
                    }

                    // Strategy 2: Try reduced context size
                    uint reducedContext = Math.Min((uint)settings.ContextSize, 2048);
                    if (reducedContext < modelParams.ContextSize)
                    {
                        try
                        {
                            App.Current?.Dispatcher.Invoke(() =>
                                progress?.Report($"Retrying with reduced context ({reducedContext})..."));

                            modelParams.ContextSize = reducedContext;
                            modelParams.GpuLayerCount = 0;
                            return LLamaWeights.LoadFromFile(modelParams);
                        }
                        catch
                        {
                            // Still failed — throw original error
                        }
                    }

                    throw; // Nothing worked
                }
            }, ct);

            progress?.Report("Creating inference context...");

            _modelParams = modelParams;  // store for creating fresh contexts per inference
            _context = _model.CreateContext(modelParams);
            _executor = new InteractiveExecutor(_context);
            _loadedModelPath = modelPath;

            // Determine actual backend info
            string backendInfo = modelParams.GpuLayerCount > 0 ? $"GPU ({modelParams.GpuLayerCount} layers)" : "CPU";
            DiagLog($"[load] {Path.GetFileName(modelPath)} ctx={modelParams.ContextSize} gpuLayers={modelParams.GpuLayerCount} batch={modelParams.BatchSize} backend={backendInfo}");
            progress?.Report($"Model loaded successfully! [{backendInfo}]");
            OnStatusChanged?.Invoke($"Ready: {Path.GetFileNameWithoutExtension(modelPath)} [{backendInfo}]");
        }
        catch (LoadWeightsFailedException ex)
        {
            UnloadModel();
            string friendlyMsg = DiagnoseLoadFailure(modelPath, ex);
            OnStatusChanged?.Invoke("Failed to load model");
            OnError?.Invoke(friendlyMsg);
            throw new InvalidOperationException(friendlyMsg, ex);
        }
        catch (DllNotFoundException ex)
        {
            UnloadModel();
            string msg = $"Backend library not found. The required runtime is not installed.\n{ex.Message}";
            OnStatusChanged?.Invoke("Failed: backend library missing");
            OnError?.Invoke(msg);
            throw new InvalidOperationException(msg, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            UnloadModel();
            string friendlyMsg = $"Failed to load model: {ex.Message}";
            OnStatusChanged?.Invoke($"Failed: {ex.Message}");
            OnError?.Invoke(friendlyMsg);
            throw new InvalidOperationException(friendlyMsg, ex);
        }
        finally
        {
            _isLoading = false;
            OnLoadingChanged?.Invoke(false);
        }
    }

    /// <summary>Diagnose model load failure and provide user-friendly message.</summary>
    private static string DiagnoseLoadFailure(string modelPath, Exception ex)
    {
        string fileName = Path.GetFileName(modelPath);
        var reasons = new System.Text.StringBuilder();
        reasons.AppendLine($"Failed to load: {fileName}");
        reasons.AppendLine();

        string msg = ex.Message + " " + (ex.InnerException?.Message ?? "");

        if (msg.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("architecture", StringComparison.OrdinalIgnoreCase))
        {
            reasons.AppendLine("Possible cause: Model architecture not supported by the bundled llama.cpp backend.");
            reasons.AppendLine();
            reasons.AppendLine("FIX: Download the latest llama.cpp release from:");
            reasons.AppendLine("  https://github.com/ggml-org/llama.cpp/releases");
            reasons.AppendLine("Extract it and either:");
            reasons.AppendLine("  1. Place DLLs in a 'llama-backend' folder next to CluadeX.exe");
            reasons.AppendLine("  2. Set CustomLlamaCppBackendPath in Settings");
            reasons.AppendLine("  3. Run: Scripts/update-llama-backend.ps1");
            reasons.AppendLine();
            reasons.AppendLine("This happens because LLamaSharp bundles an older llama.cpp");
            reasons.AppendLine("that doesn't recognize newer models like Gemma 4.");
        }
        else if (msg.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("alloc", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("VRAM", StringComparison.OrdinalIgnoreCase))
        {
            reasons.AppendLine("Possible cause: Not enough RAM/VRAM for this model.");
            reasons.AppendLine("Try: smaller quantization (Q4_K_S), fewer GPU layers, or smaller context size.");
        }
        else if (msg.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("magic", StringComparison.OrdinalIgnoreCase))
        {
            reasons.AppendLine("Possible cause: The GGUF file may be corrupted or incomplete.");
            reasons.AppendLine("Try re-downloading the model file.");
        }
        else
        {
            reasons.AppendLine("Possible causes:");
            reasons.AppendLine("  - Model format not compatible with this version");
            reasons.AppendLine("  - Insufficient system memory");
            reasons.AppendLine("  - File may be corrupted (try re-downloading)");
        }

        reasons.AppendLine();
        reasons.AppendLine($"Technical: {ex.Message}");
        return reasons.ToString();
    }

    public async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_model == null || _modelParams == null)
            throw new InvalidOperationException("No model loaded. Please load a model first from the Models tab.");

        var settings = _settingsService.Settings;

        // ── Build prompt directly ──
        // We bypass ChatSession to avoid InteractiveExecutor stale-state issues.
        // Format as ChatML (widely supported by GGUF models).
        var prompt = new StringBuilder();

        if (!string.IsNullOrEmpty(systemPrompt))
        {
            prompt.Append("<|im_start|>system\n");
            prompt.Append(systemPrompt);
            prompt.Append("<|im_end|>\n");
        }

        // Add conversation history — only User/Assistant, merge consecutive same-role
        string? lastRole = null;
        foreach (var msg in history)
        {
            if (string.IsNullOrWhiteSpace(msg.Content)) continue;

            if (msg.Role == MessageRole.User)
            {
                if (lastRole == "user")
                {
                    // Merge: remove the trailing close tag, append content
                    prompt.Length -= "<|im_end|>\n".Length;
                    prompt.Append('\n');
                    prompt.Append(msg.Content);
                    prompt.Append("<|im_end|>\n");
                }
                else
                {
                    prompt.Append("<|im_start|>user\n");
                    prompt.Append(msg.Content);
                    prompt.Append("<|im_end|>\n");
                }
                lastRole = "user";
            }
            else if (msg.Role == MessageRole.Assistant)
            {
                if (lastRole == "assistant")
                {
                    prompt.Length -= "<|im_end|>\n".Length;
                    prompt.Append('\n');
                    prompt.Append(msg.Content);
                    prompt.Append("<|im_end|>\n");
                }
                else
                {
                    prompt.Append("<|im_start|>assistant\n");
                    prompt.Append(msg.Content);
                    prompt.Append("<|im_end|>\n");
                }
                lastRole = "assistant";
            }
            // Skip ToolAction, CodeExecution, System (mid-conversation)
        }

        // Add the new user message
        prompt.Append("<|im_start|>user\n");
        prompt.Append(userMessage);
        prompt.Append("<|im_end|>\n");

        // Open assistant turn for the model to complete
        prompt.Append("<|im_start|>assistant\n");

        // ─── Clamp the generation budget to what actually fits the context window ───
        // A common mis-set is MaxTokens == ContextSize (e.g. both 4096): generation alone would then try to
        // consume the entire window, leaving ZERO room for the prompt → the model stalls or emits nothing
        // (a big chunk of the "local chat hangs / never answers" report). Reserve the prompt's footprint
        // (~4 chars/token heuristic) plus a small margin so there's always real room to answer. Reasoning
        // models (DeepSeek-R1, etc.) especially need this — they spend tokens on <think> before the reply.
        int ctxSize = Math.Max(512, (int)settings.ContextSize);
        int approxPromptTokens = prompt.Length / 4;
        int roomForGen = Math.Max(256, ctxSize - approxPromptTokens - 64);
        int effectiveMaxTokens = Math.Min(settings.MaxTokens, roomForGen);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = effectiveMaxTokens,
            AntiPrompts = new List<string>
            {
                "<|im_end|>", "<|im_start|>",       // ChatML
                "<|end|>", "<|eot_id|>", "</s>",     // Llama / general
                "<|endoftext|>", "<|end▁of▁sentence|>",
            },
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline
            {
                Temperature = settings.Temperature,
                TopP = settings.TopP,
                RepeatPenalty = settings.RepeatPenalty,
            },
        };

        // Create a FRESH context + executor for each call.
        // Model weights (_model) stay loaded in GPU/RAM — only KV cache is recreated.
        // This guarantees clean state: no stale KV cache from previous conversations.
        using var inferenceContext = _model.CreateContext(_modelParams);
        var executor = new InteractiveExecutor(inferenceContext);

        // Diagnostics so GPU-vs-CPU and prefill cost are provable from the log, not guessed.
        DiagLog($"[infer-start] promptChars={prompt.Length} ~promptTokens={approxPromptTokens} ctx={ctxSize} maxGen={effectiveMaxTokens}");
        var inferSw = System.Diagnostics.Stopwatch.StartNew();
        long firstTokenMs = -1;
        int rawChunks = 0;

        // Stateful sanitizer: catches control tokens (<|im_end|>, etc.) even when the model streams them
        // SPLIT across chunks (e.g. "<|im_" then "end|>") — the old per-chunk strip leaked those into chat.
        var sanitizer = new CluadeX.Helpers.StreamingSanitizer();

        bool hasOutput = false;
        await foreach (var text in executor.InferAsync(prompt.ToString(), inferenceParams, ct))
        {
            if (firstTokenMs < 0) { firstTokenMs = inferSw.ElapsedMilliseconds; DiagLog($"[infer-firsttoken] {firstTokenMs}ms"); }
            rawChunks++;

            var (clean, stop) = sanitizer.Push(text);
            if (!string.IsNullOrEmpty(clean))
            {
                hasOutput = true;
                yield return clean;
            }
            if (stop) break;
        }

        // Flush any held-back tail (text we were holding in case it was the start of a control token).
        string tail = sanitizer.Flush();
        if (!string.IsNullOrEmpty(tail)) { hasOutput = true; yield return tail; }

        double inferSecs = inferSw.Elapsed.TotalSeconds;
        DiagLog($"[infer-done] firstTokenMs={firstTokenMs} totalMs={inferSw.ElapsedMilliseconds} chunks={rawChunks} chunks/s={(inferSecs > 0 ? rawChunks / inferSecs : 0):F1} hadOutput={hasOutput}");

        if (!hasOutput)
        {
            yield return "(The model did not generate a response. Try reloading the model or using a different one.)";
        }
    }

    public async Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var token in ChatAsync(history, userMessage, systemPrompt, ct))
        {
            sb.Append(token);
        }
        return sb.ToString().Trim();
    }

    public void UnloadModel()
    {
        _executor = null;
        _context?.Dispose();
        _context = null;
        _model?.Dispose();
        _model = null;
        _modelParams = null;
        _loadedModelPath = null;
        OnStatusChanged?.Invoke("No model loaded");

        // Force GC to release VRAM
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnloadModel();
        GC.SuppressFinalize(this);
    }
}
