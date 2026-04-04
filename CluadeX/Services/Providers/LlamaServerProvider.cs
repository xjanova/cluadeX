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

    public bool IsServerRunning => _serverProcess is { HasExited: false };
    public string? LoadedModelName => _loadedModelPath != null ? Path.GetFileNameWithoutExtension(_loadedModelPath) : null;

    public LlamaServerProvider(SettingsService settingsService) : base(settingsService) { }

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

        string? serverExe = FindServerExe();
        if (serverExe == null)
            throw new FileNotFoundException(
                "llama-server.exe not found. Place it in 'llama-backend/' folder next to CluadeX.exe, " +
                "or run Scripts/update-llama-backend.ps1 to download it.");

        var settings = _settingsService.Settings;
        int gpuLayers = settings.GpuLayerCount == -1 ? 99 : settings.GpuLayerCount;
        if (settings.GpuBackend == "CPU") gpuLayers = 0;

        // Build command line
        var args = new List<string>
        {
            "-m", $"\"{modelPath}\"",
            "--port", _port.ToString(),
            "--host", "127.0.0.1",
            "-ngl", gpuLayers.ToString(),
            "-c", settings.ContextSize.ToString(),
            "-b", settings.BatchSize.ToString(),
            "--temp", settings.Temperature.ToString("F2"),
        };

        if (settings.ThreadCount > 0)
            args.AddRange(new[] { "-t", settings.ThreadCount.ToString() });

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

            // Read stderr in background for diagnostics
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_serverProcess is { HasExited: false })
                    {
                        string? line = await _serverProcess.StandardError.ReadLineAsync(ct);
                        if (line != null)
                            System.Diagnostics.Debug.WriteLine($"[llama-server] {line}");
                    }
                }
                catch { }
            }, ct);

            // Wait for server to become ready (health check)
            progress?.Report("Waiting for llama-server to load model...");
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
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    if (json.Contains("ok", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
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

        var requestObj = new
        {
            model = "local",
            messages,
            stream = true,
            max_tokens = settings.MaxTokens,
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

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!line.StartsWith("data: ")) continue;
            string data = line["data: ".Length..];
            if (data == "[DONE]") break;

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices))
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta)
                            && delta.TryGetProperty("content", out var c))
                        {
                            content = c.GetString();
                        }
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(content))
                yield return content;
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
                string? result = proc.StandardOutput.ReadLine();
                proc.WaitForExit(3000);
                if (!string.IsNullOrEmpty(result) && File.Exists(result)) return result;
            }
        }
        catch { }

        return null;
    }

    public override void Dispose()
    {
        StopServerAsync().Wait(5000);
        base.Dispose();
    }
}
