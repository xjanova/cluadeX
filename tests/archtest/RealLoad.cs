using System.Diagnostics;
using System.IO;
using CluadeX.Models;
using CluadeX.Services;
using CluadeX.Services.Providers;

namespace ArchTest;

/// <summary>
/// dotnet run -- --real-load &lt;gguf&gt; &lt;llama.cpp folder&gt;: the real LocalGgufProvider loads a real model through
/// that llama-server (ctx 4096), answers one prompt, and unloads. Uses VRAM — only with the owner's OK and after
/// checking nvidia-smi (a 7B at ctx 12288 once restarted the PC).
/// </summary>
internal static class RealLoad
{
    public static int Run(string gguf, string backendDir)
    {
        Console.WriteLine($"── REAL load: {Path.GetFileName(gguf)} through {backendDir} ──");
        var settings = new SettingsService();
        settings.Settings.CustomLlamaCppBackendPath = backendDir;
        settings.Settings.ContextSize = 4096;
        settings.Settings.MaxTokens = 1024;
        settings.Settings.GpuBackend = "Auto";
        settings.Settings.GpuLayerCount = -1;
        var server = new LlamaServerProvider(settings);
        var provider = new LocalGgufProvider(new LlamaInferenceService(settings), server, settings);
        var sw = Stopwatch.StartNew();
        try
        {
            using var loadCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            provider.LoadModelAsync(gguf, new Progress<string>(s => Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,5:0.0}s] {s}")), loadCts.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine($"  loaded in {sw.Elapsed.TotalSeconds:0.0}s · server={server.ServerExePath} · ready={provider.IsReady}");
            Console.WriteLine("  VRAM: " + Smi());

            var reply = new System.Text.StringBuilder();
            sw.Restart();
            using var chatCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var e = provider.ChatAsync(new List<ChatMessage>(), "ตอบสั้นๆ เป็นภาษาไทย: 17 + 25 เท่ากับเท่าไหร่", "You are a helpful assistant. Answer briefly.", chatCts.Token)
                .GetAsyncEnumerator(chatCts.Token);
            var thought = new System.Text.StringBuilder();
            while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                // The chat's own rule (ChatViewModel.TryUnwrapThinking): a wrapped chunk is thinking, the rest is the reply.
                if (CluadeX.ViewModels.ChatViewModel.TryUnwrapThinking(e.Current, out var t)) thought.Append(t);
                else reply.Append(e.Current);
            }
            string text = reply.ToString().Trim();
            Console.WriteLine("  thinking: " + thought.ToString().Replace("\n", " ⏎ "));
            Console.WriteLine($"  reply in {sw.Elapsed.TotalSeconds:0.0}s · thinking {thought.Length} chars · answer: {text.Replace("\n", " ⏎ ")}");
            return text.Contains("42") && !text.Contains("<think") ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine($"  FAILED {ex.GetType().Name}: {ex.Message}"); return 1; }
        finally
        {
            try { provider.UnloadAsync().GetAwaiter().GetResult(); } catch { }
            Thread.Sleep(1500);
            Console.WriteLine($"  unloaded · llama-server processes left: {Process.GetProcessesByName("llama-server").Length} · VRAM: {Smi()}");
        }
    }

    static string Smi()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used,memory.free,temperature.gpu --format=csv,noheader")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(5000); return o;
        }
        catch { return "?"; }
    }
}
