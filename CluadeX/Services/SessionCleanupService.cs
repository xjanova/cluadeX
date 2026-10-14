using CluadeX.Services.Background;
using CluadeX.Services.Browser;

namespace CluadeX.Services;

/// <summary>
/// What closing a chat cleans up: everything its agent runs left open. A run that started `npx vite` and opened the
/// preview kept both alive after the chat was deleted — dev servers holding ports and RAM until the app quit. Only
/// what a run of THAT chat started is touched (owner = AgentRunScope.SessionKey); processes the user started from the
/// Task Manager page and another chat's servers stay. Files in the project are never deleted here.
/// </summary>
public sealed class SessionCleanupService
{
    private readonly BackgroundProcessService? _bg;
    private readonly BrowserService? _browser;
    private readonly SettingsService? _settings;

    public SessionCleanupService(BackgroundProcessService? bg = null, BrowserService? browser = null, SettingsService? settings = null)
    {
        _bg = bg;
        _browser = browser;
        _settings = settings;
    }

    public sealed record Result(int ProcessesStopped, bool PreviewClosed, int ImagesDeleted)
    {
        public bool Any => ProcessesStopped > 0 || PreviewClosed || ImagesDeleted > 0;
    }

    /// <summary>Stop the chat's background processes, close the preview if its agent was the last to use it, and
    /// (when <paramref name="deleteImages"/>) delete the screenshots its runs saved. Never throws.</summary>
    public async Task<Result> CleanAsync(string? sessionKey, bool deleteImages)
    {
        if (string.IsNullOrWhiteSpace(sessionKey)) return new Result(0, false, 0);
        int stopped = 0, images = 0;
        bool closed = false;
        try { if (_bg != null) stopped = await _bg.StopForSessionAsync(sessionKey, TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[cleanup] stopping processes failed: {ex.Message}"); }
        try { if (_browser != null) closed = await _browser.CloseIfOwnedByAsync(sessionKey, TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[cleanup] closing the preview failed: {ex.Message}"); }
        if (deleteImages && _settings != null)
        {
            try { images = Vision.ToolImageWire.DeleteSessionImages(Vision.ToolImageWire.StoreFor(_settings), sessionKey); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[cleanup] deleting screenshots failed: {ex.Message}"); }
        }
        return new Result(stopped, closed, images);
    }
}
