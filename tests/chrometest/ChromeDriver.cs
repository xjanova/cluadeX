using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ChromeTest;

/// <summary>Real Chrome on a throwaway profile, run by launch-chrome.mjs. <see cref="EvalAsync"/> runs code in the
/// extension's service worker, which is how the harness plays the user: pressing the icon, closing the window.</summary>
internal sealed class ChromeDriver : IDisposable
{
    private readonly Process _node;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _waiting = new();
    private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId;

    /// <summary>A folder the script could not delete. Nothing else here reports that.</summary>
    public string? ProfileLeft { get; private set; }

    private ChromeDriver(Process node)
    {
        _node = node;
        node.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
        node.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Console.WriteLine("  [node] " + e.Data); };
        node.BeginOutputReadLine();
        node.BeginErrorReadLine();
    }

    /// <summary>Starts Chrome and returns once the extension is loaded, with the id Chrome gave it.</summary>
    public static async Task<(ChromeDriver Driver, string ExtensionId)> StartAsync(string script, string chrome, string extensionFolder, string hostRules)
    {
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(chrome);
        start.ArgumentList.Add(extensionFolder);
        start.ArgumentList.Add(hostRules);
        var driver = new ChromeDriver(Process.Start(start) ?? throw new InvalidOperationException("node did not start"));
        try { return (driver, await driver._ready.Task.WaitAsync(TimeSpan.FromSeconds(30))); }
        catch { driver.Dispose(); throw; }
    }

    private void OnLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var m = doc.RootElement;
        if (m.TryGetProperty("ready", out _)) _ready.TrySetResult(m.GetProperty("extensionId").GetString()!);
        else if (m.TryGetProperty("failed", out var failed)) _ready.TrySetException(new InvalidOperationException(failed.GetString()));
        else if (m.TryGetProperty("profileLeft", out var left)) ProfileLeft = left.GetString();
        else if (m.TryGetProperty("exited", out _)) _exited.TrySetResult();
        else if (m.TryGetProperty("id", out var id) && _waiting.TryRemove(id.GetInt32(), out var waiter))
        {
            if (m.GetProperty("ok").GetBoolean()) waiter.TrySetResult(m.GetProperty("value").Clone());
            else waiter.TrySetException(new InvalidOperationException(m.GetProperty("error").GetString()));
        }
    }

    public async Task<JsonElement> EvalAsync(string expression, int timeoutMs = 15_000)
    {
        int id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        await _node.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, expression }));
        await _node.StandardInput.FlushAsync();
        return await answer.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    /// <summary>Chrome exits (politely, then by force) and its profile is deleted. True when that finished in time.</summary>
    public async Task<bool> QuitAsync()
    {
        try { _node.StandardInput.Close(); } catch (IOException) { }
        try { await _exited.Task.WaitAsync(TimeSpan.FromSeconds(25)); return true; }
        catch (TimeoutException) { return false; }
    }

    public void Dispose()
    {
        try { QuitAsync().GetAwaiter().GetResult(); }
        finally
        {
            try { if (!_node.HasExited) _node.Kill(); } catch (InvalidOperationException) { }
            _node.Dispose();
        }
    }
}
