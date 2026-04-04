using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using CluadeX.Models;

namespace CluadeX.Services;

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
    private static bool _backendConfigured;

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
        ConfigureCustomBackend();
    }

    /// <summary>
    /// Configure a custom llama.cpp backend if specified in settings.
    /// This allows using a newer llama.cpp build that supports newer architectures
    /// (e.g., Gemma 4) before LLamaSharp officially updates.
    /// </summary>
    private void ConfigureCustomBackend()
    {
        if (_backendConfigured) return;
        _backendConfigured = true;

        try
        {
            string? customPath = _settingsService.Settings.CustomLlamaCppBackendPath;
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            string autoBackendDir = Path.Combine(appDir, "llama-backend");

            // Determine which directory to use
            string? backendDir = null;
            if (!string.IsNullOrEmpty(customPath) && Directory.Exists(customPath)
                && File.Exists(Path.Combine(customPath, "llama.dll")))
            {
                backendDir = customPath;
            }
            else if (Directory.Exists(autoBackendDir) && File.Exists(Path.Combine(autoBackendDir, "llama.dll")))
            {
                backendDir = autoBackendDir;
            }

            if (backendDir != null)
            {
                string llamaDll = Path.Combine(backendDir, "llama.dll");
                System.Diagnostics.Debug.WriteLine($"Custom llama.cpp backend: {backendDir}");
                System.Diagnostics.Debug.WriteLine($"  llama.dll size: {new FileInfo(llamaDll).Length} bytes");

                // Use WithLibrary for the most reliable override
                NativeLibraryConfig.LLama.WithLibrary(llamaDll);

                // Also set search directory for ggml-*.dll dependencies
                NativeLibraryConfig.All.WithSearchDirectory(backendDir);

                // Set DLL import resolver to ensure our DLLs are found first
                NativeLibrary.SetDllImportResolver(typeof(LLamaWeights).Assembly,
                    (name, assembly, searchPath) =>
                    {
                        // Try custom backend first
                        string candidate = Path.Combine(backendDir, name);
                        if (!candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            candidate += ".dll";

                        if (File.Exists(candidate))
                        {
                            System.Diagnostics.Debug.WriteLine($"  Loading from custom backend: {candidate}");
                            return NativeLibrary.Load(candidate);
                        }

                        // Fallback to default resolution
                        return IntPtr.Zero;
                    });

                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Custom backend config failed: {ex.Message}");
        }
    }

    public async Task LoadModelAsync(string modelPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
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

        var inferenceParams = new InferenceParams
        {
            MaxTokens = settings.MaxTokens,
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

        bool hasOutput = false;
        await foreach (var text in executor.InferAsync(prompt.ToString(), inferenceParams, ct))
        {
            // Filter out anti-prompt tokens that leak into the output
            string clean = text
                .Replace("<|im_end|>", "")
                .Replace("<|im_start|>", "");
            if (!string.IsNullOrEmpty(clean))
            {
                hasOutput = true;
                yield return clean;
            }
        }

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
