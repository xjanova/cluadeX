using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CluadeX.Models;

namespace CluadeX.Services.Providers;

/// <summary>
/// AI provider that runs llama.cpp's built-in OpenAI-compatible server (llama-server).
/// Supports ALL model architectures including Gemma 4, without LLamaSharp version constraints.
/// Falls back to llama-backend/llama-server.exe next to the app.
/// </summary>
public class LlamaServerProvider : ApiProviderBase
{
    public override string ProviderId => "LlamaServer";
    public override string DisplayName => "llama.cpp Server (Local)";

    private Process? _serverProcess;
    private string? _loadedModelPath;
    private int _port = 8087; // Avoid conflict with common ports
    private string ServerUrl => $"http://127.0.0.1:{_port}";
    private readonly GpuDetectionService? _gpuDetection;

    public bool IsServerRunning => _serverProcess is { HasExited: false };
    public string? LoadedModelName => _loadedModelPath != null ? Path.GetFileNameWithoutExtension(_loadedModelPath) : null;

    public LlamaServerProvider(SettingsService settingsService, GpuDetectionService? gpuDetection = null) : base(settingsService)
    {
        _gpuDetection = gpuDetection;
    }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        // Check if server is already running
        if (IsServerRunning)
        {
            IsReady = true;
            SetStatus($"llama-server ready: {LoadedModelName}");
            return;
        }

        IsReady = false;
        SetStatus("llama-server not running. Load a model to start.");
    }

    /// <summary>Start llama-server with a GGUF model.</summary>
    public async Task LoadModelAsync(string modelPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Model file not found", modelPath);

        // Stop existing server
        await StopServerAsync();

        // Pick a free port (8087 may be held by another app or a stale llama-server). Binding a
        // taken port makes llama-server die on startup and surface only a vague timeout, so probe first.
        _port = FindFreePort(8087);

        string? serverExe = FindServerExe();
        if (serverExe == null)
            throw new FileNotFoundException(
                "llama-server.exe not found. Place it in 'llama-backend/' folder next to CluadeX.exe, " +
                "or run Scripts/update-llama-backend.ps1 to download it.");

        var settings = _settingsService.Settings;
        int gpuLayers = settings.GpuLayerCount == -1 ? 999 : settings.GpuLayerCount;
        if (settings.GpuBackend == "CPU") gpuLayers = 0;

        // ─── Multi-GPU tensor split ───
        // If the user has more than one NVIDIA card, proportionally split the model
        // across them by VRAM. nvidia-smi lists GPU 0, 1, 2 ... and llama-server takes
        // --tensor-split as a comma-separated weight list (e.g. "8,8" or "24,8").
        // Without this flag llama.cpp puts everything on the main GPU, wasting the rest.
        string? tensorSplit = null;
        int gpuCount = 1;
        if (settings.GpuBackend != "CPU" && _gpuDetection != null)
        {
            try
            {
                var gpus = _gpuDetection.DetectAllNvidiaGpus();
                if (gpus.Count > 1)
                {
                    gpuCount = gpus.Count;
                    // Use each card's total VRAM as the weight. llama.cpp normalises internally.
                    tensorSplit = string.Join(",", gpus.Select(g => g.VramTotalMB.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            catch { /* single-GPU fallback */ }
        }

        // ─── Threads ───
        // 0 (our default) = use ALL physical cores. Without -t llama.cpp picks its own
        // default which is often half the cores, leaving performance on the table.
        int threads = settings.ThreadCount > 0
            ? settings.ThreadCount
            : Math.Max(1, Environment.ProcessorCount);

        // Inspect arch so we can apply model-specific defaults. For Gemma 4 the
        // Unsloth + ggml-org cards both prescribe temp=1.0 / top-p=0.95 / top-k=64
        // — using ChatML-style defaults produces garbled output.
        string? arch = GgufMetadataReader.TryReadArchitecture(modelPath);
        bool isGemma = !string.IsNullOrEmpty(arch) && arch.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);

        float temp = settings.Temperature;
        float topP = settings.TopP;
        int topK = 40;
        if (isGemma)
        {
            // Don't override if the user has clearly customized things; only nudge defaults.
            if (Math.Abs(settings.Temperature - 0.7f) < 0.001f) temp = 1.0f;
            if (Math.Abs(settings.TopP - 0.9f) < 0.001f) topP = 0.95f;
            topK = 64;
        }

        // Build command line.
        // --jinja is critical for any modern model (Gemma 4, Llama 3+, Qwen 3): it tells
        // llama-server to use the chat template embedded in the GGUF metadata instead of
        // a hard-coded one. Without it, Gemma 4 outputs garbage because the server falls
        // back to ChatML formatting, which Gemma's tokenizer doesn't recognize.
        //
        // --reasoning-format none keeps the model's thinking tokens inside message.content
        // instead of splitting them into message.reasoning_content. Gemma 4 / DeepSeek R1
        // / Qwen 3 all emit a reasoning phase; with the default "deepseek" format, the
        // server returns content="" and puts everything in reasoning_content — our SSE
        // parser below only reads delta.content, so the user sees a blank response.
        // "none" is the safe cross-model default.
        var args = new List<string>
        {
            "-m", $"\"{modelPath}\"",
            "--port", _port.ToString(),
            "--host", "127.0.0.1",
            "-ngl", gpuLayers.ToString(),
            "-c", settings.ContextSize.ToString(),
            "-b", settings.BatchSize.ToString(),
            "--temp", temp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            "--top-p", topP.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            "--top-k", topK.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--jinja",
            "--reasoning-format", "none",
            "-t", threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        // Multi-GPU: split model by VRAM weight across all NVIDIA cards.
        if (tensorSplit != null)
        {
            args.AddRange(new[] { "--tensor-split", tensorSplit });
        }

        string fileName = Path.GetFileName(modelPath);
        progress?.Report($"Starting llama-server with {fileName}...");
        SetLoading(true);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = string.Join(' ', args),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // Add DLL directory to PATH so llama-server can find ggml-cuda.dll etc.
            string dllDir = Path.GetDirectoryName(serverExe)!;
            string existingPath = psi.EnvironmentVariables["PATH"] ?? "";
            psi.EnvironmentVariables["PATH"] = dllDir + ";" + existingPath;

            _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _serverProcess.Start();
            _loadedModelPath = modelPath;

            // Read stderr in background for diagnostics + load-progress forwarding.
            // llama-server emits structured lines during weight loading; we mine them for
            // a percent counter so the UI has something to show beyond a static spinner.
            string fileName2 = Path.GetFileName(modelPath);
            var stderrProc = _serverProcess; // capture so a concurrent StopServerAsync nulling the field can't NRE us mid-read
            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = stderrProc.StandardError;
                    string? line;
                    // ReadLineAsync returns null at EOF (the server closed stderr) — that IS the exit signal.
                    // The old code did `continue` on null, which busy-spun the CPU until HasExited flipped.
                    while ((line = await stderr.ReadLineAsync(ct)) != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[llama-server] {line}");
                        ReportLoadProgress(line, progress, fileName2);
                    }
                }
                catch (OperationCanceledException) { /* load cancelled — expected */ }
                catch (ObjectDisposedException) { /* process disposed during shutdown — expected */ }
                catch (Exception ex)
                {
                    // Never swallow silently: a broken stderr pipe used to vanish here, leaving the UI
                    // with no clue the progress reader had died.
                    System.Diagnostics.Debug.WriteLine($"[llama-server stderr reader stopped] {ex.GetType().Name}: {ex.Message}");
                }
            }, ct);

            // Wait for server to become ready (health check)
            progress?.Report($"Loading {fileName2} into VRAM...");
            bool ready = await WaitForServerReady(ct);

            if (ready)
            {
                IsReady = true;
                string backendInfo = gpuLayers > 0 ? $"GPU ({gpuLayers} layers)" : "CPU";
                SetStatus($"Ready: {Path.GetFileNameWithoutExtension(modelPath)} [{backendInfo}]");
                progress?.Report($"llama-server ready! [{backendInfo}]");

                // Update settings
                _settingsService.UpdateSettings(s =>
                {
                    s.SelectedModelPath = modelPath;
                    s.SelectedModelName = Path.GetFileNameWithoutExtension(modelPath);
                });
            }
            else
            {
                await StopServerAsync();
                throw new InvalidOperationException("llama-server failed to start. Check if the model is compatible.");
            }
        }
        catch (Exception) when (_serverProcess is { HasExited: true })
        {
            string exitInfo = $"Exit code: {_serverProcess.ExitCode}";
            await StopServerAsync();
            throw new InvalidOperationException($"llama-server crashed during startup. {exitInfo}");
        }
        finally
        {
            SetLoading(false);
        }
    }

    // Precompiled once — regex hit on every stderr line, keep it cheap.
    private static readonly System.Text.RegularExpressions.Regex LoadTensorsRegex =
        new(@"load_tensors:.*?(\d+)\s*MiB", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex OffloadedRegex =
        new(@"offloaded\s+(\d+)\s*/\s*(\d+)\s+layers", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Mine meaningful load progress out of llama-server's verbose stderr and surface it
    /// via the progress callback. We recognise three phases:
    ///   • "load_tensors: CUDA0 model buffer size = 4123 MiB" — weights uploaded per device
    ///   • "load_tensors: offloaded 32/33 layers to GPU" — layer offload summary
    ///   • "init_from_params: n_ctx_per_seq = ..." — context allocation (near the end)
    /// Lines that don't match are ignored so the UI doesn't churn on every stderr write.
    /// </summary>
    private static void ReportLoadProgress(string line, IProgress<string>? progress, string fileName)
    {
        if (progress == null) return;
        try
        {
            // GPU layer offload → most useful single number for "how much is on VRAM?"
            var off = OffloadedRegex.Match(line);
            if (off.Success)
            {
                int done = int.Parse(off.Groups[1].Value);
                int total = int.Parse(off.Groups[2].Value);
                int pct = total > 0 ? done * 100 / total : 0;
                progress.Report($"Loading {fileName} — {done}/{total} layers on GPU ({pct}%)");
                return;
            }

            // Per-device weight buffer size reports — pass through as status
            var buf = LoadTensorsRegex.Match(line);
            if (buf.Success)
            {
                string mib = buf.Groups[1].Value;
                progress.Report($"Uploading weights to VRAM: {mib} MiB...");
                return;
            }

            // Context allocation — final phase before serve loop
            if (line.Contains("init_from_params", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report($"Allocating context memory...");
                return;
            }

            // Final "server is listening on" message — model is live
            if (line.Contains("server is listening", StringComparison.OrdinalIgnoreCase)
             || line.Contains("all slots are idle", StringComparison.OrdinalIgnoreCase))
            {
                progress.Report("Model ready — warming up...");
            }
        }
        catch { /* best-effort */ }
    }

    private async Task<bool> WaitForServerReady(CancellationToken ct, int maxWaitMs = 120_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            if (_serverProcess is null or { HasExited: true }) return false;

            try
            {
                using var response = await _httpClient.GetAsync($"{ServerUrl}/health", ct);
                // llama-server returns 503 ("loading model") until the weights are resident, then 200.
                // The HTTP status IS the readiness signal — string-matching the body was brittle across
                // server versions that changed the {"status":"ok"} shape.
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (HttpRequestException) { /* Server not ready yet */ }

            await Task.Delay(1000, ct);
        }
        return false;
    }

    public async Task StopServerAsync()
    {
        IsReady = false;

        if (_serverProcess != null)
        {
            try
            {
                if (!_serverProcess.HasExited)
                {
                    _serverProcess.Kill(entireProcessTree: true);
                    await Task.Delay(500);
                }
            }
            catch { }

            _serverProcess.Dispose();
            _serverProcess = null;
        }

        _loadedModelPath = null;
        SetStatus("llama-server stopped");
    }

    public override async IAsyncEnumerable<string> ChatAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsServerRunning || !IsReady)
        {
            RaiseError("llama-server is not running. Load a model first.");
            yield break;
        }

        var settings = _settingsService.Settings;
        var messages = BuildChatMessages(history, userMessage, systemPrompt);

        // Clamp generation budget so the prompt isn't starved of the window (the reopened chat-hang on the
        // GPU path: ctx=4096 + max_tokens=4096 reserves the whole window for output). See TokenBudget.
        int approxPromptTokens = CluadeX.Helpers.TokenBudget.EstimateTokens(JsonSerializer.Serialize(messages));
        int maxTokens = CluadeX.Helpers.TokenBudget.ClampMaxTokens((int)settings.ContextSize, approxPromptTokens, settings.MaxTokens);

        var requestObj = new
        {
            model = "local",
            messages,
            stream = true,
            max_tokens = maxTokens,
            temperature = (double)settings.Temperature,
            top_p = (double)settings.TopP,
        };

        var body = JsonSerializer.Serialize(requestObj);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/v1/chat/completions");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            RaiseError($"llama-server error {response.StatusCode}: {error}");
            yield break;
        }

        // Parse SSE stream (OpenAI format: data: {...}\n\n)
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // NOTE: never loop on reader.EndOfStream — it is a SYNCHRONOUS property that blocks the calling
        // thread on the network read until the next byte arrives. During a long local prefill (no bytes
        // for 30-120s) that froze the consuming thread — the literal "app hangs while loading" bug.
        // ReadLineAsync returns null at end-of-stream, which is the fully-async equivalent.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!line.StartsWith("data: ")) continue;
            string data = line["data: ".Length..];
            if (data == "[DONE]") break;

            // Read both delta.content AND delta.reasoning_content. We pass
            // --reasoning-format none so content is normally everything, but if a server
            // version ignores that flag (or the user overrode it) we still want the
            // reasoning tokens to reach the UI instead of being silently dropped.
            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices))
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (!choice.TryGetProperty("delta", out var delta)) continue;
                        if (delta.TryGetProperty("content", out var c))
                        {
                            var s = c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                            if (!string.IsNullOrEmpty(s)) content = (content ?? "") + s;
                        }
                        if (delta.TryGetProperty("reasoning_content", out var r))
                        {
                            var s = r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                            if (!string.IsNullOrEmpty(s)) content = (content ?? "") + s;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A malformed/truncated SSE chunk used to vanish here, making the stream look like it
                // just stopped mid-sentence. Log and keep reading — the next chunk is usually intact.
                System.Diagnostics.Debug.WriteLine($"[llama-server SSE chunk skipped] {ex.Message}");
            }

            if (!string.IsNullOrEmpty(content))
            {
                // Local chat models (DeepSeek R1 Distill, Qwen, …) sometimes leak their ChatML control
                // tokens into content and even begin a fake next turn ("…GPT-4<|im_end|><|im_start|>").
                // Cut at the first turn-end marker and strip any stray control tokens before display.
                var (clean, stop) = CluadeX.Helpers.ModelOutputSanitizer.SanitizeStreamChunk(content);
                if (!string.IsNullOrEmpty(clean)) yield return clean;
                if (stop) yield break;
            }
        }
    }

    public override async Task<string> GenerateAsync(
        List<ChatMessage> history,
        string userMessage,
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        await foreach (var token in ChatAsync(history, userMessage, systemPrompt, ct))
            sb.Append(token);
        return sb.ToString();
    }

    // ─── Native tool use (OpenAI-style function calling) ───
    // Gated on a setting so users with non-tool GGUFs can fall back to the [ACTION:] text loop.
    public override bool SupportsNativeToolUse => _settingsService.Settings.LocalNativeToolUseEnabled;

    /// <summary>
    /// Native tool-calling via llama-server's OpenAI-compatible endpoint. Requires the loaded model's
    /// chat template to support tools (Qwen2.5-Coder, Llama 3.x, Hermes, Mistral…) — we already start
    /// the server with --jinja so the template is honoured. Non-streaming: the agent loop consumes one
    /// response per turn, exactly like the Anthropic path.
    /// </summary>
    public override async Task<NativeToolResponse> ChatWithToolsAsync(
        List<NativeMessage> messages, string systemPrompt, List<ToolSchema> tools,
        Action<string>? onTextDelta = null, CancellationToken ct = default, string? toolChoice = null)
    {
        if (!IsServerRunning || !IsReady)
            return new NativeToolResponse { TextContent = "llama-server is not running. Load a model first.", StopReason = "end_turn" };

        var settings = _settingsService.Settings;
        // Weak-model tool path: low temperature + top_k/min_p/repeat_penalty for deterministic, well-formed
        // tool-call JSON (chat temp 0.6 + only top_p let small models drift into broken/looping output here).
        var requestObj = new Dictionary<string, object>
        {
            ["model"] = "local",
            ["messages"] = BuildOpenAiToolMessages(messages, systemPrompt),
            ["temperature"] = (double)Math.Min(settings.Temperature, settings.ToolCallTemperature),
            ["top_p"] = (double)settings.TopP,
            ["top_k"] = settings.TopK,
            ["min_p"] = (double)settings.MinP,
            ["repeat_penalty"] = (double)settings.RepeatPenalty,
            ["stream"] = false,
        };
        if (tools.Count > 0)
        {
            requestObj["tools"] = BuildOpenAiToolDefs(tools);
            // Constrained tool decoding: "auto" by default; the planner / forced-retry pass "required" or a
            // specific tool name so llama.cpp applies the tool grammar EAGERLY (guaranteeing a valid tool call
            // instead of free prose) — the biggest reliability lever for weak local tool-callers.
            requestObj["tool_choice"] = BuildOpenAiToolChoice(toolChoice);
        }

        // Clamp AFTER assembling messages+tools so the estimate reflects the real prompt footprint (tool
        // schemas are rendered into the prompt by --jinja, so they count toward the window too).
        int approxPromptTokens = CluadeX.Helpers.TokenBudget.EstimateTokens(JsonSerializer.Serialize(requestObj));
        requestObj["max_tokens"] = CluadeX.Helpers.TokenBudget.ClampMaxTokens((int)settings.ContextSize, approxPromptTokens, settings.MaxTokens);

        var body = JsonSerializer.Serialize(requestObj);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ServerUrl}/v1/chat/completions");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            // StopReason "error" (NOT "end_turn"): end_turn made the agent loop treat the HTTP failure text
            // as the model's final ANSWER — a silent failure the user saw as a weird one-line reply. The
            // loop surfaces "error" responses as provider errors instead.
            return new NativeToolResponse
            {
                TextContent = HumanizeServerError((int)response.StatusCode, error),
                StopReason = "error",
            };
        }

        var responseText = await response.Content.ReadAsStringAsync(ct);
        return ParseOpenAiToolResponse(responseText);
    }

    /// <summary>Turn llama-server's raw JSON error into a short, actionable message. The most common one for
    /// local mode is context overflow on the first agentic message — the system prompt + tool schemas alone
    /// exceed a small n_ctx — so we name the real fix (raise Context Size + reload) instead of dumping JSON.</summary>
    private string HumanizeServerError(int status, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                string type = err.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
                string msg = err.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                if (type.Contains("context", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("context size", StringComparison.OrdinalIgnoreCase))
                {
                    int nCtx = err.TryGetProperty("n_ctx", out var c) && c.TryGetInt32(out var ci) ? ci : (int)_settingsService.Settings.ContextSize;
                    int nPrompt = err.TryGetProperty("n_prompt_tokens", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
                    int suggest = Math.Max(8192, nPrompt > 0 ? ((nPrompt + 2048 + 4095) / 4096) * 4096 : 8192);
                    return $"⚠ Context window too small: this request needs ~{nPrompt} tokens but the model is "
                         + $"loaded with only {nCtx}. Raise **Context Size** to ≥ {suggest} in Settings → Inference, "
                         + $"then reload the model.\n(บริบทเล็กเกินไป — ตั้ง Context Size ≥ {suggest} แล้วโหลดโมเดลใหม่)";
                }
                if (!string.IsNullOrWhiteSpace(msg))
                    return $"llama-server error ({status}): {msg}";
            }
        }
        catch { /* not JSON — fall through to the raw text */ }
        return $"llama-server tool call failed ({status}): {(raw.Length > 300 ? raw[..300] : raw)}";
    }

    public override async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsServerRunning)
            return (false, "llama-server is not running");

        try
        {
            using var response = await _httpClient.GetAsync($"{ServerUrl}/health", ct);
            if (response.IsSuccessStatusCode)
                return (true, $"llama-server running with {LoadedModelName}");
            return (false, $"Health check failed: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>True if a llama-server.exe can be located (custom path, bundled llama-backend/, or PATH).
    /// Lets the local router prefer this backend — it ships the full CUDA runtime and offloads to the GPU
    /// reliably — when the user wants GPU but in-process LLamaSharp can't use CUDA (no CUDA Toolkit).</summary>
    public bool IsServerBackendAvailable => FindServerExe() != null;

    private string? FindServerExe()
    {
        // Check custom path
        string? customPath = _settingsService.Settings.CustomLlamaCppBackendPath;
        if (!string.IsNullOrEmpty(customPath))
        {
            string custom = Path.Combine(customPath, "llama-server.exe");
            if (File.Exists(custom)) return custom;
        }

        // Check llama-backend/ next to app
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string auto = Path.Combine(appDir, "llama-backend", "llama-server.exe");
        if (File.Exists(auto)) return auto;

        // Check PATH
        try
        {
            var psi = new ProcessStartInfo("where", "llama-server.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                string? result = proc.StandardOutput.ReadLine()?.Trim();
                if (!proc.WaitForExit(3000))
                {
                    // `where` hung (rare, but a stuck handle blocks the using-dispose) — don't orphan it.
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
                if (!string.IsNullOrEmpty(result) && File.Exists(result)) return result;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Pick an available loopback TCP port starting from <paramref name="preferred"/>. llama-server
    /// binds exactly one port; if 8087 is already held (another app, or a stale llama-server that
    /// didn't die cleanly), the bind fails and the model "load" just times out with a vague error.
    /// Probing first turns that silent hang into a clean port hop.
    /// </summary>
    private static int FindFreePort(int preferred)
    {
        for (int port = preferred; port < preferred + 64; port++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException) { /* in use — try the next port */ }
        }
        return preferred; // give up probing; let llama-server surface the real bind error
    }

    public override void Dispose()
    {
        // Synchronous cleanup — kill the process directly to avoid sync-over-async deadlock
        IsReady = false;
        if (_serverProcess != null)
        {
            try
            {
                if (!_serverProcess.HasExited)
                    _serverProcess.Kill(entireProcessTree: true);
            }
            catch { }
            _serverProcess.Dispose();
            _serverProcess = null;
        }
        _loadedModelPath = null;
        base.Dispose();
    }
}
